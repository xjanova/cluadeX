using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CluadeX.Models;

namespace CluadeX.Services.Batch;

/// <summary>What a batch job was for — decides who takes its results when it ends.</summary>
public static class BatchKinds
{
    /// <summary>A chat question sent with "batch": the answer goes back into the chat it came from.</summary>
    public const string Chat = "chat";
    /// <summary>Session-memory + instinct extraction after a chat ends: nobody waits for it.</summary>
    public const string SessionMemory = "session-memory";
}

/// <summary>One submitted batch, as kept in batches.json — enough to find it again after a restart and to hand
/// its results to whoever asked for them.</summary>
public sealed class BatchJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public AiProviderType Provider { get; set; }
    public string BatchId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Kind { get; set; } = BatchKinds.Chat;
    public string Title { get; set; } = "";
    /// <summary>The chat a <see cref="BatchKinds.Chat"/> answer goes back to.</summary>
    public string? SessionId { get; set; }
    /// <summary>The project folder open when it was submitted (project-scope memories are saved there).</summary>
    public string? ProjectPath { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public BatchState State { get; set; } = BatchState.InProgress;
    public string Detail { get; set; } = "";
    public int Requests { get; set; }
    public int Succeeded { get; set; }
    public int Errored { get; set; }
    public int Pending { get; set; }
    /// <summary>Filled once the batch ended (in any way).</summary>
    public List<BatchResult>? Results { get; set; }
    /// <summary>Its results reached whoever asked for them (the chat, the memory store).</summary>
    public bool Delivered { get; set; }
    /// <summary>The last time polling it failed, and why (key redacted). Cleared by the next good poll.</summary>
    public string? LastError { get; set; }

    [JsonIgnore] public bool IsOpen => State == BatchState.InProgress;
}

/// <summary>
/// Runs work through a provider's batch API (Anthropic Message Batches, OpenAI Batch): half price, the answer comes
/// back later. Jobs are written to batches.json under the data folder, so one submitted before a restart is still
/// polled — and paid-for results still delivered — after it. Polling only runs while a job is open.
/// </summary>
public class BatchService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly AiProviderManager _providerManager;
    private readonly DebugLogService? _debugLog;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly List<BatchJob> _jobs;
    private readonly Dictionary<string, Func<BatchJob, Task<bool>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    // One delivery pass at a time: RegisterHandler and every poll start one, and two passes over the same ended job
    // would put its answer into the chat twice.
    private readonly SemaphoreSlim _deliverGate = new(1, 1);
    private Timer? _timer;
    private bool _timerArmed;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A job was added, changed state, or was removed. Raised on a worker thread.</summary>
    public event Action? JobsChanged;

    /// <summary>A job ended and its results are in (raised before delivery is attempted).</summary>
    public event Action<BatchJob>? JobEnded;

    /// <summary>Seconds between polls while a job is open. Public for the contract harness.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay before the first poll once a job is open. Public for the contract harness.</summary>
    public TimeSpan FirstPollDelay { get; set; } = TimeSpan.FromSeconds(5);

    public BatchService(SettingsService settingsService, AiProviderManager providerManager, DebugLogService? debugLog = null)
    {
        _settingsService = settingsService;
        _providerManager = providerManager;
        _debugLog = debugLog;
        _path = Path.Combine(settingsService.DataRoot, "batches.json");
        _jobs = Load(_path);
    }

    public string StorePath => _path;

    public IReadOnlyList<BatchJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    /// <summary>The provider a new batch would go to: the active one, when its batch API is usable as configured.</summary>
    public IBatchProvider? ActiveBatchProvider =>
        _providerManager.ActiveProvider is IBatchProvider b && b.SupportsBatch ? b : null;

    public bool CanBatchNow => ActiveBatchProvider != null;

    /// <summary>Whoever takes the results of one kind of job. Return true once they are delivered; false (or a
    /// throw) leaves the job undelivered, and delivery is retried on the next poll and after a restart.</summary>
    public void RegisterHandler(string kind, Func<BatchJob, Task<bool>> handler)
    {
        lock (_lock) _handlers[kind] = handler;
        // Results that ended while nobody was listening (previous run, or before this handler registered).
        _ = Task.Run(() => DeliverPendingAsync(CancellationToken.None));
    }

    /// <summary>Submit the requests as one batch on the active provider and start polling for it.</summary>
    /// <exception cref="BatchException">The provider has no usable batch API, or it refused the batch.</exception>
    public async Task<BatchJob> SubmitAsync(string kind, string title, IReadOnlyList<BatchRequest> requests,
        string? sessionId = null, string? projectPath = null, CancellationToken ct = default)
    {
        if (requests.Count == 0) throw new BatchException("Nothing to send.");
        var provider = ActiveBatchProvider ?? throw new BatchException(
            $"{_providerManager.ActiveProvider.DisplayName} has no batch API as configured (Anthropic and OpenAI do).");

        string batchId = await provider.SubmitBatchAsync(requests, ct);
        var job = new BatchJob
        {
            Provider = _providerManager.ActiveProviderType,
            BatchId = batchId,
            Model = provider.BatchModel,
            Kind = kind,
            Title = title,
            SessionId = sessionId,
            ProjectPath = projectPath,
            Requests = requests.Count,
            Pending = requests.Count,
            Detail = "submitted",
        };
        lock (_lock) _jobs.Add(job);
        Save();
        _debugLog?.Info("Batch", $"submitted {kind} batch {batchId} ({requests.Count} request(s), {job.Model})");
        JobsChanged?.Invoke();
        EnsurePolling();
        return job;
    }

    /// <summary>Start (or keep) polling when a job is open; called at startup to resume jobs from the last run.</summary>
    public void EnsurePolling()
    {
        if (_disposed) return;
        lock (_lock)
        {
            bool open = _jobs.Any(j => j.IsOpen || (!j.Delivered && j.Results != null));
            if (!open)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                _timerArmed = false;
                return;
            }
            // Already ticking: leave its schedule alone (re-arming here after every poll made it poll every 5 s).
            if (_timerArmed) return;
            _timer ??= new Timer(_ => _ = PollOnceAsync(CancellationToken.None), null, Timeout.Infinite, Timeout.Infinite);
            _timerArmed = true;
            // The first check soon (a batch often finishes in minutes), then every PollInterval.
            _timer.Change(FirstPollDelay, PollInterval);
        }
    }

    /// <summary>Check every open job once; fetch and deliver the results of those that ended. Never throws.
    /// Public for the contract harness and the jobs list's "check now".</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        if (!await _pollGate.WaitAsync(0, ct)) return;   // a poll is already running
        try
        {
            foreach (var job in Jobs.Where(j => j.IsOpen))
            {
                ct.ThrowIfCancellationRequested();
                await PollJobAsync(job, ct);
            }
            await DeliverPendingAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _debugLog?.Warn("Batch", "poll failed", ex); }
        finally
        {
            _pollGate.Release();
            EnsurePolling();
        }
    }

    private async Task PollJobAsync(BatchJob job, CancellationToken ct)
    {
        if (ProviderFor(job) is not { } provider)
        {
            Update(job, j => j.LastError = $"{job.Provider} is no longer available to check this batch.");
            return;
        }
        try
        {
            var status = await provider.GetBatchStatusAsync(job.BatchId, ct);
            Update(job, j =>
            {
                j.Detail = status.Detail;
                j.Succeeded = status.Succeeded;
                j.Errored = status.Errored;
                j.Pending = status.Pending;
                j.LastError = null;
            });
            if (status.State == BatchState.InProgress) return;

            IReadOnlyList<BatchResult> results;
            try { results = await provider.GetBatchResultsAsync(job.BatchId, ct); }
            catch (BatchException) when (status.State is BatchState.Failed or BatchState.Canceled or BatchState.Expired)
            {
                results = [];   // refused as a whole, or stopped before anything finished: there is no output file
            }
            Update(job, j =>
            {
                j.State = status.State;
                j.Results = results.ToList();
                j.Succeeded = results.Count(r => r.Ok);
                j.Errored = results.Count(r => !r.Ok);
                j.Pending = 0;
                j.EndedUtc = DateTime.UtcNow;
            });
            _debugLog?.Info("Batch", $"{job.Kind} batch {job.BatchId} ended ({status.Detail}): {job.Succeeded} ok, {job.Errored} failed");
            JobEnded?.Invoke(job);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A network blip or 5xx: keep the job open and try again next poll; the jobs list shows why.
            Update(job, j => j.LastError = ex is BatchException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            _debugLog?.Warn("Batch", $"checking batch {job.BatchId} failed", ex);
        }
    }

    private async Task DeliverPendingAsync(CancellationToken ct)
    {
        await _deliverGate.WaitAsync(ct);
        try { await DeliverPendingLockedAsync(); }
        finally { _deliverGate.Release(); }
    }

    private async Task DeliverPendingLockedAsync()
    {
        foreach (var job in Jobs.Where(j => !j.IsOpen && !j.Delivered && j.Results != null))
        {
            Func<BatchJob, Task<bool>>? handler;
            lock (_lock) _handlers.TryGetValue(job.Kind, out handler);
            if (handler == null) continue;   // its owner has not started yet; delivered once it registers
            bool ok;
            try { ok = await handler(job); }
            catch (Exception ex) { _debugLog?.Warn("Batch", $"delivering batch {job.BatchId} failed", ex); ok = false; }
            if (ok) Update(job, j => j.Delivered = true);
        }
    }

    /// <summary>Ask the provider to stop the batch. Requests already done are still billed and still delivered.</summary>
    public async Task CancelAsync(BatchJob job, CancellationToken ct = default)
    {
        if (!job.IsOpen) return;
        var provider = ProviderFor(job) ?? throw new BatchException($"{job.Provider} is no longer available to cancel this batch.");
        await provider.CancelBatchAsync(job.BatchId, ct);
        Update(job, j => j.Detail = "canceling");
        EnsurePolling();
    }

    /// <summary>Forget a job that ended (the jobs list's remove). An open job is cancelled first, never just dropped:
    /// dropping it would leave a batch running and billing with nobody to collect its answers.</summary>
    public bool Remove(BatchJob job)
    {
        if (job.IsOpen) return false;
        bool removed;
        lock (_lock) removed = _jobs.RemoveAll(j => j.Id == job.Id) > 0;
        if (removed) { Save(); JobsChanged?.Invoke(); }
        return removed;
    }

    private IBatchProvider? ProviderFor(BatchJob job)
    {
        try { return _providerManager.GetProvider(job.Provider) as IBatchProvider; }
        catch { return null; }
    }

    private void Update(BatchJob job, Action<BatchJob> change)
    {
        lock (_lock)
        {
            var live = _jobs.FirstOrDefault(j => j.Id == job.Id);
            if (live == null) return;
            change(live);
            if (!ReferenceEquals(live, job)) change(job);
        }
        Save();
        JobsChanged?.Invoke();
    }

    private static List<BatchJob> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<BatchJob>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch
        {
            // A damaged file must not stop the app; keep it aside so the batch ids in it are not lost.
            try { File.Copy(path, path + ".unreadable", overwrite: true); } catch { }
            return new();
        }
    }

    private void Save()
    {
        lock (_saveLock)
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_jobs, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = $"{_path}.{Environment.ProcessId}.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex) { _debugLog?.Warn("Batch", "saving batches.json failed", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        GC.SuppressFinalize(this);
    }
}
