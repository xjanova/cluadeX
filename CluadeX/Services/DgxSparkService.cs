using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services;

/// <summary>What a DGX Spark (or any box on the LAN) is serving.</summary>
public sealed record DgxSparkProbe(string Host, string? Backend, string? Version, int? Port, IReadOnlyList<string> Models, string? Error)
{
    /// <summary><see cref="Error"/> when the server answered but its model list did not come (an error status, a
    /// reply that isn't the list, a dropped connection) — not the same as a list that came back empty.</summary>
    public const string ModelListFailedError = "model-list-failed";

    public bool Reachable => Backend != null;
    /// <summary>The server answered, but what it has installed is unknown.</summary>
    public bool ModelListFailed => Reachable && Error == ModelListFailedError;
}

/// <summary>CluadeX left the Spark and gave the context window back: it was <paramref name="From"/> tokens on the
/// Spark and is <paramref name="To"/> again.</summary>
public sealed record SparkContextRestore(uint From, uint To, DateTime AtUtc);

/// <summary>A model that fits a DGX Spark's 128 GB of unified memory, with what published tests measured.
/// <paramref name="UseKey"/> is the localization key of what it is good for.</summary>
public sealed record SparkModelPick(string OllamaTag, string Name, string Size, string Speed, string UseKey);

/// <summary>Why a Spark action failed, as something the window can put into the UI language. The exception
/// text itself only goes to the debug log.</summary>
public enum SparkFailure
{
    None,
    /// <summary>Nothing answered at the Spark (checked before anything was changed).</summary>
    Unreachable,
    /// <summary>Connection refused / reset / DNS — the request never got a reply.</summary>
    Network,
    Timeout,
    /// <summary>An HTTP error status with no message of Ollama's own.</summary>
    Server,
    /// <summary>A reply that isn't the JSON Ollama sends.</summary>
    BadResponse,
    /// <summary>Ollama's own {"error": …} — its text is shown as is.</summary>
    Ollama,
    /// <summary>The model isn't on the Spark (any more).</summary>
    ModelMissing,
    /// <summary>Another provider switch was in progress, so the switch to the Spark never happened.</summary>
    Busy,
    /// <summary>The switch happened but Ollama did not come up.</summary>
    NotReady,
    Unknown,
}

/// <summary>Ollama's own error (the {"error": "…"} line of /api/pull). Its message is Ollama's text, meant for the user.</summary>
public sealed class OllamaErrorException(string message) : InvalidOperationException(message);

/// <summary>Result of <see cref="DgxSparkService.UseOllamaModelAsync"/>. On failure nothing it wrote is left behind.</summary>
public sealed record SparkUseResult(SparkFailure Failure, string? ProviderStatus = null)
{
    public bool Ok => Failure == SparkFailure.None;
}

/// <summary>
/// CluadeX on an NVIDIA DGX Spark. The Spark runs DGX OS (Linux, ARM64) and the desktop app runs on Windows,
/// so CluadeX does not run ON the Spark — it drives the model the Spark serves over the local network:
/// Ollama (port 11434, the setup NVIDIA's own Spark playbooks use), llama.cpp's llama-server (8080) or vLLM
/// (8000). With 128 GB of memory the Spark runs 120B-class models that no 8–24 GB desktop GPU can hold.
/// </summary>
public class DgxSparkService
{
    /// <summary>The XMAN Studio campaign page — the only purchase link the app shows (owner rule).</summary>
    public const string CampaignUrl = "https://xman4289.com/dgx-spark";

    /// <summary>Models worth running on a Spark, best first. Speeds are measured figures from NVIDIA's developer
    /// blog and a single-Spark benchmark on the NVIDIA developer forum (decode tokens/s, single request); they
    /// vary with quantisation, context length and serving stack.</summary>
    public static readonly IReadOnlyList<SparkModelPick> RecommendedModels =
    [
        // gpt-oss-120b: Ollama 42.1 · llama.cpp 55.4 · vLLM 60.7     qwen3-coder-next: Ollama 59.2 · vLLM 74.8
        // gpt-oss-20b: llama.cpp 82.7                              Qwen3-30B-A3B: vLLM 77.1 · Ollama 85.2
        // (NVIDIA developer blog "How NVIDIA DGX Spark's performance enables intensive AI tasks" for llama.cpp;
        //  NVIDIA developer forum "Measured inference benchmarks on a single DGX Spark" for Ollama / vLLM.)
        new("gpt-oss:120b", "gpt-oss 120B", "≈ 65 GB", "≈ 42–61 tok/s", "spark.pick.gptoss120"),
        new("qwen3-coder-next", "Qwen3-Coder-Next (80B MoE)", "≈ 52 GB", "≈ 59–75 tok/s", "spark.pick.qwen3coder"),
        new("gpt-oss:20b", "gpt-oss 20B", "≈ 14 GB", "≈ 83 tok/s", "spark.pick.gptoss20"),
        new("qwen3:30b", "Qwen3 30B-A3B", "≈ 19 GB", "≈ 77–85 tok/s", "spark.pick.qwen30"),
    ];

    /// <summary>Ollama's port on the Spark. A debug build reads CLUADEX_SPARK_OLLAMA_PORT so the contract harness can
    /// stand a localhost mock in for the Spark without colliding with a real Ollama on this PC. Read on every call,
    /// never cached in a static field: a cached value is fixed at the type's first use, which in the harness came
    /// before the override was set — and its "mock" download went to the real Ollama.</summary>
    private static int OllamaPort =>
#if DEBUG
        int.TryParse(Environment.GetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT"), out int port) && port > 0 ? port :
#endif
        11434;

    /// <summary>Where the Ollama provider goes when its address is left empty (OllamaProvider's default).</summary>
    private const string OllamaDefaultUrl = "http://localhost:11434";

    /// <summary>The most of a refused /api/pull's body read for Ollama's {"error": …} sentence. Ollama's is a line;
    /// anything bigger isn't Ollama's error and is reported as a server failure without being read.</summary>
    public const int MaxPullErrorBodyBytes = 64 * 1024;

    /// <summary>How long a refused /api/pull may take to send its error body. A debug build reads
    /// CLUADEX_SPARK_ERRBODY_TIMEOUT_MS (per call) so the harness can prove a stalled body without waiting 10 s.</summary>
    private static TimeSpan PullErrorBodyTimeout =>
#if DEBUG
        int.TryParse(Environment.GetEnvironmentVariable("CLUADEX_SPARK_ERRBODY_TIMEOUT_MS"), out int ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) :
#endif
        TimeSpan.FromSeconds(10);

    /// <summary>Context window CluadeX asks a Spark-served model for. 128 GB leaves room for a long window.</summary>
    public const int SparkContextSize = 32768;

    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$|^\[[0-9A-Fa-f:]+\]$", RegexOptions.Compiled);

    private readonly SettingsService _settings;
    private readonly AiProviderManager _providers;
    private readonly DebugLogService? _log;
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _pull = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>&gt; 0 while <see cref="UseOllamaModelAsync"/> is writing settings and switching. Its own write
    /// (the Spark's address and 32k window, saved before the provider switch) would otherwise look like "left the
    /// Spark" to the settings-changed check and be undone on the spot.</summary>
    private int _applying;

    /// <summary>Raised after CluadeX left the Spark and the context window was given back (any thread).</summary>
    public event Action<SparkContextRestore>? ContextRestored;

    /// <summary>The last time the window was given back this run — the Spark window explains it when it opens later.</summary>
    public SparkContextRestore? LastContextRestore { get; private set; }

    public DgxSparkService(SettingsService settings, AiProviderManager providers, DebugLogService? log = null)
    {
        _settings = settings;
        _providers = providers;
        _log = log;
        // CluadeX can leave the Spark three ways, and each must give the context size back before a local server
        // loads a model with the Spark's window:
        //  • a provider switch — OnProviderChanged runs before the new provider initialises. A switch to anything
        //    but Ollama is never part of going onto the Spark (its rollback switches back only after the settings
        //    are put back), so it counts even while UseOllamaModelAsync is running: a switch to Local that lands
        //    in the middle of it must not load the local model with the Spark's 32k;
        //  • Ollama re-pointed at this PC while it stays active (Settings ▸ Use this provider, MCP set_model's
        //    forced ActiveProvider write) — no provider switch at all, only a settings save;
        //  • an address changed without a save, then re-initialised — caught when the provider stops loading.
        // Also checked once now, for a switch that happened while this service didn't exist.
        _providers.OnProviderChanged += p =>
            RestoreContextIfLeftSpark(evenWhileApplying: !ReferenceEquals(p, _providers.GetProvider(AiProviderType.Ollama)));
        _providers.OnLoadingChanged += loading => { if (!loading) RestoreContextIfLeftSpark(); };
        _settings.SettingsChanged += () => RestoreContextIfLeftSpark();
        RestoreContextIfLeftSpark();
    }

    /// <summary>"spark-1a2b.local", "192.168.1.50", "http://spark:11434/" → the bare host (no scheme, port or path),
    /// or null when it isn't a host name / IP at all.</summary>
    public static string? NormalizeHost(string? input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (Uri.TryCreate(s.Contains("://") ? s : "http://" + s, UriKind.Absolute, out var u))
            s = u.HostNameType == UriHostNameType.IPv6 ? $"[{u.IdnHost.Trim('[', ']')}]" : u.IdnHost;
        if (s.StartsWith('[') && !s.EndsWith(']')) s += "]";
        return HostPattern.IsMatch(s) ? s : null;
    }

    /// <summary>"llama3" and "llama3:latest" are the same Ollama model.</summary>
    public static string CanonicalTag(string tag) => tag.Contains(':') ? tag : tag + ":latest";

    /// <summary>Ask the box what it serves. Every backend is tried at once; Ollama wins when several answer.</summary>
    public async Task<DgxSparkProbe> ProbeAsync(string hostInput, CancellationToken ct = default)
    {
        string? host = NormalizeHost(hostInput);
        if (host == null) return new DgxSparkProbe(hostInput, null, null, null, [], "invalid-host");

        var ollama = ProbeOllamaAsync(host, ct);
        var llama = ProbeOpenAiCompatibleAsync(host, 8080, "llama.cpp", ct);
        var vllm = ProbeOpenAiCompatibleAsync(host, 8000, "vLLM", ct);
        await Task.WhenAll(ollama, llama, vllm);

        var hit = new[] { ollama.Result, llama.Result, vllm.Result }.FirstOrDefault(p => p.Reachable);
        _log?.Info("DgxSpark", hit != null
            ? $"{host}: {hit.Backend} {hit.Version} · {hit.Models.Count} model(s)"
            : $"{host}: nothing answered on 11434 / 8080 / 8000");
        return hit ?? new DgxSparkProbe(host, null, null, null, [], "unreachable");
    }

    private async Task<DgxSparkProbe> ProbeOllamaAsync(string host, CancellationToken ct)
    {
        try
        {
            string root = $"http://{host}:{OllamaPort}";
            using var ver = await _probe.GetAsync(root + "/api/version", ct);
            if (!ver.IsSuccessStatusCode) return Miss(host);
            string? version = null;
            using (var doc = JsonDocument.Parse(await ver.Content.ReadAsStringAsync(ct)))
                if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();

            // Ollama answered. Its model list is a separate request: a list that didn't come (error status, not the
            // list, dropped connection) is not an empty list — "the model isn't on the Spark" would be a wrong answer.
            var models = await ListOllamaModelsAsync(host, root, ct);
            return models != null
                ? new DgxSparkProbe(host, "Ollama", version, OllamaPort, models, null)
                : new DgxSparkProbe(host, "Ollama", version, OllamaPort, [], DgxSparkProbe.ModelListFailedError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Debug("DgxSpark", $"{host}: Ollama probe missed — {ex.GetType().Name}: {ex.Message}");
            return Miss(host);
        }
    }

    /// <summary>Ollama's /api/tags as model names; null when the list didn't come (the reason goes to the debug log).</summary>
    private async Task<List<string>?> ListOllamaModelsAsync(string host, string root, CancellationToken ct)
    {
        try
        {
            using var tags = await _probe.GetAsync(root + "/api/tags", ct);
            if (!tags.IsSuccessStatusCode)
            {
                _log?.Warn("DgxSpark", $"{host}: Ollama answered /api/version but /api/tags with HTTP {(int)tags.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                _log?.Warn("DgxSpark", $"{host}: /api/tags did not answer with Ollama's model list");
                return null;
            }
            var models = new List<string>();
            foreach (var m in arr.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                    models.Add(name);
            return models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Warn("DgxSpark", $"{host}: listing Ollama's models failed", ex);
            return null;
        }
    }

    private async Task<DgxSparkProbe> ProbeOpenAiCompatibleAsync(string host, int port, string backend, CancellationToken ct)
    {
        try
        {
            using var r = await _probe.GetAsync($"http://{host}:{port}/v1/models", ct);
            if (!r.IsSuccessStatusCode) return Miss(host);
            var models = new List<string>();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name) models.Add(name);
            return new DgxSparkProbe(host, backend, null, port, models, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Miss(host);
        }
    }

    private static DgxSparkProbe Miss(string host) => new(host, null, null, null, [], null);

    /// <summary>
    /// Download a model onto the Spark (Ollama /api/pull, streamed). Reports (status, fraction 0–1 or null).
    /// The download happens on the Spark itself — nothing large comes to this PC. Throws
    /// <see cref="OllamaErrorException"/> with Ollama's own words when Ollama refuses; anything else is a transport
    /// or format failure for <see cref="Classify"/>.
    /// </summary>
    public async Task PullAsync(string host, string model, IProgress<(string Status, double? Fraction)> progress, CancellationToken ct)
    {
        string? h = NormalizeHost(host) ?? throw new ArgumentException("invalid host", nameof(host));
        string body = JsonSerializer.Serialize(new { model, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{h}:{OllamaPort}/api/pull")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _pull.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Ollama answers a refused pull with {"error":"…"} and an error status — that sentence is the useful part.
            // Read at most 64 KB of it, for at most ~10 s: a server that sends more, or sends it slowly, is not
            // Ollama refusing — it is a server failure, and the window must not hang on it.
            string? said = null;
            try
            {
                string? errorBody = await ReadBoundedAsync(resp.Content, MaxPullErrorBodyBytes, PullErrorBodyTimeout, ct);
                if (errorBody == null) _log?.Warn("DgxSpark", $"{h}: /api/pull HTTP {(int)resp.StatusCode} with an error body over {MaxPullErrorBodyBytes / 1024} KB — not read");
                else said = ErrorOf(errorBody);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log?.Warn("DgxSpark", $"{h}: /api/pull HTTP {(int)resp.StatusCode} — its error body could not be read", ex);
            }
            if (!string.IsNullOrWhiteSpace(said)) throw new OllamaErrorException(said);
            throw new HttpRequestException($"{h}: /api/pull answered HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        }

        bool succeeded = false;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new OllamaErrorException(err.ValueKind == JsonValueKind.String ? err.GetString() ?? "" : err.GetRawText());
            string status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (status == "success") succeeded = true;
            double? fraction = root.TryGetProperty("total", out var t) && t.TryGetInt64(out long total) && total > 0
                && root.TryGetProperty("completed", out var c) && c.TryGetInt64(out long done)
                ? Math.Clamp((double)done / total, 0, 1) : null;
            progress.Report((status, fraction));
        }
        // A stream that just stops (Spark rebooted, cable pulled) is not a finished download.
        if (!succeeded) throw new IOException($"{h}: the /api/pull stream ended before Ollama reported success");
        _log?.Info("DgxSpark", $"{h}: pulled {model}");
    }

    /// <summary>The body as UTF-8 text when it is at most <paramref name="maxBytes"/> and arrives within
    /// <paramref name="timeout"/>; null when it is bigger. A slow body throws OperationCanceledException with
    /// <paramref name="ct"/> itself not cancelled.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        await using var stream = await content.ReadAsStreamAsync(limit.Token);
        var buffer = new byte[maxBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), limit.Token);
            if (n == 0) break;
            total += n;
        }
        return total > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string? ErrorOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
            ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
            : null;
    }

    /// <summary>What kind of failure an exception from <see cref="PullAsync"/> / <see cref="ProbeAsync"/> is, for a
    /// localized sentence. Call only for failures the user did not cancel.</summary>
    public static SparkFailure Classify(Exception ex) => ex switch
    {
        OllamaErrorException => SparkFailure.Ollama,
        HttpRequestException { StatusCode: not null } => SparkFailure.Server,
        HttpRequestException or SocketException or IOException => SparkFailure.Network,
        OperationCanceledException or TimeoutException => SparkFailure.Timeout,   // HttpClient's own timeout
        JsonException or FormatException or InvalidDataException => SparkFailure.BadResponse,
        _ => SparkFailure.Unknown,
    };

    /// <summary>Details of a failure the window only names in the UI language.</summary>
    public void LogFailure(string what, Exception ex) => _log?.Warn("DgxSpark", what, ex);

    /// <summary>The settings as <see cref="UseOllamaModelAsync"/> found them (read under the settings lock, in the
    /// same update that writes).</summary>
    private sealed record UseSnapshot(bool HadOllamaConfig, string? BaseUrl, string? SelectedModel, bool UseCustomModel,
        uint ContextSize, uint? SavedContextSize, string? SparkHost, AiProviderType ActiveProvider);

    /// <summary>What <see cref="UseOllamaModelAsync"/> wrote: the values, and which optional fields it wrote at all
    /// (the window only when it raised it, the remembered size only when none was remembered, ActiveProvider only on
    /// the re-initialise path).</summary>
    private sealed record UseWrite(string BaseUrl, string Model, bool RaisedContext, uint? Saved, string Host, bool ActiveProvider);

    /// <summary>
    /// Make the Spark's model the active one: the Ollama provider pointed at the Spark, a long window. Checks the
    /// Spark first (nothing is written when it doesn't answer, can't list its models, or lacks the model), and after
    /// the switch checks that Ollama really is the active, ready provider — <see cref="AiProviderManager.SwitchProviderAsync"/>
    /// returns silently while another switch is in progress. On any failure what this wrote is put back — field by
    /// field, and only where the field still holds this call's own value (see <see cref="Restore"/>).
    /// </summary>
    public async Task<SparkUseResult> UseOllamaModelAsync(string host, string model, CancellationToken ct = default)
    {
        string h = NormalizeHost(host) ?? throw new ArgumentException("invalid host", nameof(host));

        var probe = await ProbeOllamaAsync(h, ct);
        if (!probe.Reachable) return new SparkUseResult(SparkFailure.Unreachable);
        // Ollama answered but its model list didn't come: a server failure, not "the model isn't there".
        if (probe.ModelListFailed) return new SparkUseResult(SparkFailure.Server);
        if (!probe.Models.Any(m => string.Equals(CanonicalTag(m), CanonicalTag(model), StringComparison.OrdinalIgnoreCase)))
            return new SparkUseResult(SparkFailure.ModelMissing);

        var ollama = _providers.GetProvider(AiProviderType.Ollama);
        // Ollama already answering (even when a forced settings write says otherwise): re-initialise in place —
        // SwitchProviderAsync is a no-op for the active provider.
        bool reinit = ReferenceEquals(_providers.ActiveProvider, ollama);
        // The provider that was answering, for the way back (settings may say otherwise after a forced write).
        AiProviderType? previousType = TypeOf(_providers.ActiveProvider);
        UseSnapshot? before = null;
        UseWrite? wrote = null;

        Interlocked.Increment(ref _applying);
        try
        {
            _settings.UpdateSettings(s =>
            {
                bool had = s.ProviderConfigs.TryGetValue("Ollama", out var cfg) && cfg != null;
                before = new UseSnapshot(had, cfg?.BaseUrl, cfg?.SelectedModel, cfg?.UseCustomModel ?? false,
                    s.ContextSize, s.DgxSparkSavedContextSize, s.DgxSparkHost, s.ActiveProvider);

                if (cfg == null) s.ProviderConfigs["Ollama"] = cfg = new ProviderConfig();
                string url = $"http://{h}:{OllamaPort}";
                cfg.BaseUrl = url;
                cfg.SelectedModel = model;
                cfg.UseCustomModel = false;
                bool raised = false;
                uint? saved = null;
                if (s.ContextSize < SparkContextSize)
                {
                    if (s.DgxSparkSavedContextSize == null) s.DgxSparkSavedContextSize = saved = s.ContextSize;
                    s.ContextSize = SparkContextSize;
                    raised = true;
                }
                s.DgxSparkHost = h;
                if (reinit) s.ActiveProvider = AiProviderType.Ollama;
                wrote = new UseWrite(url, model, raised, saved, h, reinit);
            });

            if (reinit) await _providers.ReinitializeActiveAsync(ct);
            else await _providers.SwitchProviderAsync(AiProviderType.Ollama, ct);

            bool switched = ReferenceEquals(_providers.ActiveProvider, ollama) && _providers.ActiveProviderType == AiProviderType.Ollama;
            if (switched && ollama.IsReady)
            {
                _log?.Info("DgxSpark", $"using {model} on {h} through Ollama");
                return new SparkUseResult(SparkFailure.None);
            }

            string? status = switched ? ollama.StatusMessage : null;
            _log?.Warn("DgxSpark", switched
                ? $"Ollama at {h} did not come up ({status}) — putting the previous settings back"
                : $"the switch to Ollama did not happen or was overtaken (another provider switch; active = {_providers.ActiveProviderType}) — putting the previous settings back");
            Restore(before!, wrote!, ollama);
            // Back to what was answering before — the old provider, or Ollama's old address — unless another switch
            // already moved CluadeX somewhere else meanwhile (then that switch's provider stays).
            if (switched && ReferenceEquals(_providers.ActiveProvider, ollama))
            {
                if (!reinit) await _providers.SwitchProviderAsync(previousType ?? before!.ActiveProvider, CancellationToken.None);
                else await _providers.ReinitializeActiveAsync(CancellationToken.None);
            }
            return new SparkUseResult(switched ? SparkFailure.NotReady : SparkFailure.Busy, status);
        }
        catch (Exception ex)
        {
            _log?.Warn("DgxSpark", $"switching to {model} on {h} failed — putting the previous settings back", ex);
            try { if (before != null && wrote != null) Restore(before, wrote, ollama); }
            catch (Exception rex) { _log?.Warn("DgxSpark", "putting the previous settings back failed", rex); }
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            return new SparkUseResult(Classify(ex));
        }
        finally
        {
            Interlocked.Decrement(ref _applying);
            RestoreContextIfLeftSpark();
        }
    }

    /// <summary>Which provider type <paramref name="provider"/> is (null when it isn't one of the manager's).</summary>
    private AiProviderType? TypeOf(IAiProvider provider)
    {
        foreach (var t in Enum.GetValues<AiProviderType>())
            if (ReferenceEquals(_providers.GetProvider(t), provider)) return t;
        return null;
    }

    /// <summary>
    /// Put back what <see cref="UseOllamaModelAsync"/> wrote — compare-and-swap, field by field, inside one settings
    /// update: a field is restored only while it still holds the value this call wrote. Whatever changed it since
    /// (a provider switch that landed during the re-initialise, the window given back on leaving the Spark, an edit
    /// on the Settings page) keeps its value. ActiveProvider is put back only when this call wrote it, it still says
    /// Ollama, and the manager's active provider is still the Ollama instance.
    /// </summary>
    private void Restore(UseSnapshot b, UseWrite w, IAiProvider ollama)
    {
        _settings.UpdateSettings(s =>
        {
            if (s.ProviderConfigs.TryGetValue("Ollama", out var cfg) && cfg != null)
            {
                bool urlOurs = cfg.BaseUrl == w.BaseUrl;
                bool modelOurs = cfg.SelectedModel == w.Model;
                bool customOurs = !cfg.UseCustomModel;
                bool restOfItUntouched = cfg.ApiKey == null && cfg.CustomModelId == null && cfg.WorkspaceId == null;
                if (!b.HadOllamaConfig && urlOurs && modelOurs && customOurs && restOfItUntouched)
                    s.ProviderConfigs.Remove("Ollama");   // this call created it, and nothing else has touched it
                else
                {
                    if (urlOurs) cfg.BaseUrl = b.BaseUrl;
                    if (modelOurs) cfg.SelectedModel = b.SelectedModel;
                    if (customOurs) cfg.UseCustomModel = b.UseCustomModel;
                }
            }
            if (w.RaisedContext && s.ContextSize == SparkContextSize) s.ContextSize = b.ContextSize;
            if (w.Saved is uint saved && s.DgxSparkSavedContextSize == saved) s.DgxSparkSavedContextSize = b.SavedContextSize;
            if (string.Equals(s.DgxSparkHost, w.Host, StringComparison.Ordinal)) s.DgxSparkHost = b.SparkHost;
            if (w.ActiveProvider && s.ActiveProvider == AiProviderType.Ollama && ReferenceEquals(_providers.ActiveProvider, ollama))
                s.ActiveProvider = b.ActiveProvider;
        });
    }

    /// <summary>CluadeX moved off the Spark: give the context size back to what it was, before a local load reads it.
    /// Not while <see cref="UseOllamaModelAsync"/> is writing (its own write looks like leaving), unless
    /// <paramref name="evenWhileApplying"/> — a switch to another provider, which is never its own. Not while
    /// Ollama's address is half-typed (not a host yet): whether that is the Spark is unknown until it is one.</summary>
    private void RestoreContextIfLeftSpark(bool evenWhileApplying = false)
    {
        if (!evenWhileApplying && Volatile.Read(ref _applying) > 0) return;
        var current = _settings.Settings;
        if (current.DgxSparkSavedContextSize is not uint || IsSparkActive(current) || OllamaAddressUnfinished(current)) return;
        uint? restored = null;
        uint from = 0;
        _settings.UpdateSettings(s =>
        {
            if (s.DgxSparkSavedContextSize is not uint saved || IsSparkActive(s) || OllamaAddressUnfinished(s)) return;
            from = s.ContextSize;
            s.ContextSize = saved;
            s.DgxSparkSavedContextSize = null;
            restored = saved;
        });
        if (restored is not uint r) return;
        _log?.Info("DgxSpark", $"left the Spark — context size back to {r} (was {from})");
        var note = new SparkContextRestore(from, r, DateTime.UtcNow);
        LastContextRestore = note;
        try { ContextRestored?.Invoke(note); }
        catch (Exception ex) { _log?.Warn("DgxSpark", "a context-restored listener failed", ex); }
    }

    /// <summary>Ollama is the active provider and its address is being typed: set, but not a host yet ("http://",
    /// "http://[fe80"). The Settings page saves a second after each keystroke.</summary>
    private static bool OllamaAddressUnfinished(AppSettings s) =>
        s.ActiveProvider == AiProviderType.Ollama && OllamaHostOf(s) == null;

    /// <summary>True when the active provider is the Ollama server on the remembered Spark.</summary>
    public bool IsActive => IsSparkActive(_settings.Settings);

    /// <summary>Ollama is the active provider and its address is the remembered Spark's host (exactly — not a
    /// substring: "spark" must not match "spark-old.local").</summary>
    public static bool IsSparkActive(AppSettings s)
    {
        if (s.ActiveProvider != AiProviderType.Ollama || string.IsNullOrEmpty(s.DgxSparkHost)) return false;
        string? host = OllamaHostOf(s);
        return host != null && string.Equals(host, s.DgxSparkHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The host the Ollama provider talks to (its default when the address is empty), or null when the
    /// address isn't usable.</summary>
    private static string? OllamaHostOf(AppSettings s)
    {
        string url = s.ProviderConfigs.TryGetValue("Ollama", out var cfg) && !string.IsNullOrWhiteSpace(cfg?.BaseUrl)
            ? cfg.BaseUrl! : OllamaDefaultUrl;
        return NormalizeHost(url);
    }

    /// <summary>The Ollama provider's server runs on this PC: its address is loopback (localhost, 127.x, [::1]) and
    /// it isn't the remembered Spark.</summary>
    public static bool IsOllamaOnThisPc(AppSettings s)
    {
        string? host = OllamaHostOf(s);
        if (host == null) return false;
        if (!string.IsNullOrEmpty(s.DgxSparkHost) && string.Equals(host, s.DgxSparkHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>The model answering runs on this PC's GPU: LLamaSharp, llama-server, or an Ollama on this PC.
    /// The one rule for the status-bar GPU readout and the nvidia-smi poll.</summary>
    public static bool ModelRunsOnThisPc(AppSettings s) => s.ActiveProvider switch
    {
        AiProviderType.Local or AiProviderType.LlamaServer => true,
        AiProviderType.Ollama => IsOllamaOnThisPc(s),
        _ => false,
    };
}
