using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services.Billing;

/// <summary>How a usage row's dollars were arrived at.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageCostKind
{
    /// <summary>Metered API use: the provider reported the tokens, priced at its list price.</summary>
    Billed,
    /// <summary>A subscription CLI (Claude Dev, Codex, Gemini, Antigravity): not billed per token — the dollars are what
    /// the same tokens would cost at API prices.</summary>
    Estimated,
    /// <summary>A model on this machine (Ollama, llama-server, local GGUF): $0, tokens and time only.</summary>
    Free,
}

/// <summary>One stretch of use: a provider, a model and a chat within one 15-minute window (rows of the same request
/// and its neighbours are merged before they are written). <see cref="At"/> is UTC, so any time zone can be applied
/// when the rows are read.</summary>
public sealed class UsageEntry
{
    [JsonPropertyName("t")] public DateTimeOffset At { get; set; }
    /// <summary>An <see cref="Models.AiProviderType"/> name ("Anthropic", "OpenAIDev", "Ollama" …), or "Other".</summary>
    [JsonPropertyName("p")] public string Provider { get; set; } = "";
    [JsonPropertyName("m")] public string Model { get; set; } = "";
    /// <summary>The chat (ChatSession.Id) the use was for; null outside any chat run (a title, a test).</summary>
    [JsonPropertyName("c")] public string? Chat { get; set; }
    [JsonPropertyName("k")] public UsageCostKind Kind { get; set; }
    /// <summary>Made by a sub-agent of the chat's run (its spend is part of the chat's too).</summary>
    [JsonPropertyName("sub"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SubAgent { get; set; }
    /// <summary>Written from the day ledger kept before this log existed: no model, no chat.</summary>
    [JsonPropertyName("legacy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Legacy { get; set; }
    [JsonPropertyName("in")] public long Input { get; set; }
    [JsonPropertyName("out")] public long Output { get; set; }
    [JsonPropertyName("cr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long CacheRead { get; set; }
    [JsonPropertyName("cw"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long CacheWrite { get; set; }
    /// <summary>Reasoning tokens, where the provider reports them apart (they are part of <see cref="Output"/> too).</summary>
    [JsonPropertyName("th"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Thinking { get; set; }
    [JsonPropertyName("usd")] public decimal Usd { get; set; }
    [JsonPropertyName("n")] public int Requests { get; set; }
    /// <summary>Time the model spent answering (local models; 0 where it was not measured).</summary>
    [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Seconds { get; set; }

    public long Tokens => Input + Output + CacheRead + CacheWrite;

    internal UsageEntry Copy() => (UsageEntry)MemberwiseClone();
}

/// <summary>
/// The per-request usage behind the Billing page: <c>usage-log.jsonl</c> in the data folder, one JSON row per line,
/// appended only. A request reports usage several times (input at its start, output deltas, the end), so rows are
/// merged in memory per provider · model · chat · kind within a 15-minute UTC window and written at most every
/// <see cref="FlushEvery"/> — a long agent session adds a few lines an hour, not one per token. Reading is incremental:
/// only bytes appended since the last read are parsed.
/// </summary>
public sealed class UsageLog
{
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);
    private const long WindowTicks = TimeSpan.TicksPerMinute * 15;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<(string, string, string?, UsageCostKind, bool, long), UsageEntry> _pending = new();
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private int _flushQueued;

    private readonly object _readLock = new();
    private readonly List<UsageEntry> _persisted = new();
    private long _readOffset;
    private int _version;

    /// <param name="path">The file; null = memory only (nothing is written).</param>
    public UsageLog(string? path) { _path = path; }

    public string? FilePath => _path;

    /// <summary>Bumped by every row added: a cached aggregation older than this is stale.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Raised (on the recording thread) after a row is added.</summary>
    public event Action? Added;

    public void Add(UsageEntry e)
    {
        if (e.Requests == 0 && e.Tokens == 0 && e.Usd == 0 && e.Seconds == 0) return;
        e = e.Copy();
        if (string.IsNullOrWhiteSpace(e.Provider)) e.Provider = "Other";
        e.Model ??= "";
        e.At = e.At.ToUniversalTime();
        long window = e.At.UtcTicks / WindowTicks;
        bool flush;
        lock (_lock)
        {
            var key = (e.Provider, e.Model, e.Chat, e.Kind, e.SubAgent, window);
            if (_pending.TryGetValue(key, out var p))
            {
                p.Input += e.Input; p.Output += e.Output; p.CacheRead += e.CacheRead; p.CacheWrite += e.CacheWrite;
                p.Thinking += e.Thinking; p.Usd += e.Usd; p.Requests += e.Requests; p.Seconds += e.Seconds;
            }
            else _pending[key] = e;
            flush = _path != null && DateTime.UtcNow - _lastFlushUtc >= FlushEvery;
        }
        Interlocked.Increment(ref _version);
        if (flush && Interlocked.Exchange(ref _flushQueued, 1) == 0)
            _ = Task.Run(() => { try { Flush(); } finally { Volatile.Write(ref _flushQueued, 0); } });
        try { Added?.Invoke(); } catch { /* a listener must never break the provider's stream */ }
    }

    /// <summary>Write the merged rows now (app exit; otherwise every <see cref="FlushEvery"/>).</summary>
    public void Flush()
    {
        if (_path == null) return;
        List<UsageEntry> rows;
        lock (_lock)
        {
            _lastFlushUtc = DateTime.UtcNow;
            if (_pending.Count == 0) return;
            rows = _pending.Values.OrderBy(r => r.At).ToList();
            _pending.Clear();
        }
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(JsonSerializer.Serialize(r, Json)).Append('\n');
        try
        {
            lock (_readLock)   // a read never sees half a line of ours
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // The disk refused (full, locked): keep the rows for the next flush rather than lose them.
            lock (_lock)
                foreach (var r in rows)
                {
                    var key = (r.Provider, r.Model, r.Chat, r.Kind, r.SubAgent, r.At.UtcTicks / WindowTicks);
                    if (_pending.TryGetValue(key, out var p)) { p.Input += r.Input; p.Output += r.Output; p.CacheRead += r.CacheRead; p.CacheWrite += r.CacheWrite; p.Thinking += r.Thinking; p.Usd += r.Usd; p.Requests += r.Requests; p.Seconds += r.Seconds; }
                    else _pending[key] = r;
                }
        }
    }

    /// <summary>Every row: the file's (parsed once, then only what was appended since) and the ones not written yet.
    /// Copies — safe to read on any thread. A damaged line is skipped.</summary>
    public List<UsageEntry> ReadAll()
    {
        var all = new List<UsageEntry>();
        lock (_readLock)
        {
            ReadAppended();
            all.AddRange(_persisted);
        }
        lock (_lock) all.AddRange(_pending.Values.Select(p => p.Copy()));
        return all;
    }

    private void ReadAppended()
    {
        if (_path == null || !File.Exists(_path)) { _persisted.Clear(); _readOffset = 0; return; }
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _readOffset) { _persisted.Clear(); _readOffset = 0; }   // replaced or cut: read it again
            if (fs.Length == _readOffset) return;
            fs.Seek(_readOffset, SeekOrigin.Begin);
            var bytes = new byte[fs.Length - _readOffset];
            int got = 0;
            while (got < bytes.Length) { int n = fs.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
            int lastNl = Array.LastIndexOf(bytes, (byte)'\n', got - 1 < 0 ? 0 : got - 1);
            if (lastNl < 0) return;   // only a line still being written
            int start = 0;
            for (int i = 0; i <= lastNl; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (i > start)
                {
                    try
                    {
                        var row = JsonSerializer.Deserialize<UsageEntry>(new ReadOnlySpan<byte>(bytes, start, i - start), Json);
                        if (row != null && !string.IsNullOrEmpty(row.Provider)) _persisted.Add(row);
                    }
                    catch (JsonException) { /* a damaged line: the rest of the file is still good */ }
                }
                start = i + 1;
            }
            _readOffset += lastNl + 1;
        }
        catch (IOException) { /* locked for a moment: what was read before still stands */ }
        catch (UnauthorizedAccessException) { }
    }
}
