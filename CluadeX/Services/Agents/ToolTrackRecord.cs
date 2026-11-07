using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>
/// How each model has done with each tool on this PC: calls, failures and what kind of failure. The next run of that model
/// gets a few lines of advice for the tools it keeps getting wrong (a model that fails edit_file on "text not found" 7 times
/// in 12 is told to read the lines first, or write a small file whole) — learned from what really happened here, not from
/// a table typed in advance. Stored in <c>&lt;data root&gt;\tool-record.json</c>; counts are halved once a tool passes
/// <see cref="DecayAt"/> calls, so a model that has learned is not reminded forever.
/// </summary>
public sealed class ToolTrackRecord
{
    public const int DecayAt = 200;
    public const int MinCalls = 5, MinFails = 3;
    public const double MinFailRate = 0.3;
    public const int MaxHints = 3;
    public const int MaxModels = 60;

    public sealed class ToolStat
    {
        public int Calls { get; set; }
        public int Fails { get; set; }
        public Dictionary<string, int> Kinds { get; set; } = new(StringComparer.Ordinal);
        public DateTime LastUtc { get; set; }
    }

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Dictionary<string, ToolStat>> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private FileShape _data = new();
    private bool _dirty;
    private Timer? _saveTimer;
    private DateTime _lastStamp = DateTime.MinValue;

    /// <param name="path">The file to read and write; null = in memory only (a harness).</param>
    public ToolTrackRecord(string? path)
    {
        _path = path;
        Load();
    }

    private static readonly Lazy<ToolTrackRecord> _shared = new(() =>
        new ToolTrackRecord(Path.Combine(SettingsService.ResolveDataRoot(out _), "tool-record.json")));

    /// <summary>The one record of this data home.</summary>
    public static ToolTrackRecord Shared => _shared.Value;

    /// <summary>One finished call. Refusals that are the user's or the app's (denied, Send now, cancelled) are not the
    /// model's mistake and are not counted.</summary>
    public void Record(string model, string tool, bool success, string? error)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(tool)) return;
        string kind = success ? "" : Classify(error);
        if (kind == "not-the-model") return;
        lock (_gate)
        {
            if (!_data.Models.TryGetValue(model, out var tools))
            {
                if (_data.Models.Count >= MaxModels) DropOldestModel();
                _data.Models[model] = tools = new Dictionary<string, ToolStat>(StringComparer.OrdinalIgnoreCase);
            }
            if (!tools.TryGetValue(tool, out var stat)) tools[tool] = stat = new ToolStat();
            stat.Calls++;
            // Strictly increasing, so "the oldest model" is never a tie between two records of the same clock tick.
            var now = DateTime.UtcNow;
            _lastStamp = now > _lastStamp ? now : _lastStamp.AddTicks(1);
            stat.LastUtc = _lastStamp;
            if (!success)
            {
                stat.Fails++;
                stat.Kinds[kind] = stat.Kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
            }
            if (stat.Calls >= DecayAt)
            {
                stat.Calls /= 2;
                stat.Fails /= 2;
                foreach (var k in stat.Kinds.Keys.ToList()) stat.Kinds[k] /= 2;
                foreach (var k in stat.Kinds.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) stat.Kinds.Remove(k);
            }
            _dirty = true;
        }
        ScheduleSave();
    }

    /// <summary>A copy of one model's numbers (for the harness and a future page).</summary>
    public IReadOnlyDictionary<string, ToolStat> StatsFor(string model)
    {
        lock (_gate)
            return _data.Models.TryGetValue(model, out var t)
                ? t.ToDictionary(kv => kv.Key, kv => new ToolStat
                    { Calls = kv.Value.Calls, Fails = kv.Value.Fails, Kinds = new(kv.Value.Kinds), LastUtc = kv.Value.LastUtc },
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ToolStat>();
    }

    /// <summary>
    /// The lines for the system prompt of a run of <paramref name="model"/>: at most <see cref="MaxHints"/> tools it has
    /// offered now that it failed at least <see cref="MinFails"/> times in at least <see cref="MinCalls"/> calls, at a
    /// rate of <see cref="MinFailRate"/> or more, each with the advice for its most common kind of failure. Null when
    /// there is nothing worth saying. English: it is read by the model.
    /// </summary>
    public string? HintsFor(string model, IEnumerable<string>? offeredTools = null)
    {
        var offered = offeredTools == null ? null : new HashSet<string>(offeredTools, StringComparer.OrdinalIgnoreCase);
        List<(string Tool, ToolStat Stat)> bad;
        lock (_gate)
        {
            if (!_data.Models.TryGetValue(model, out var tools)) return null;
            bad = tools.Where(kv => kv.Value.Calls >= MinCalls && kv.Value.Fails >= MinFails
                                    && (double)kv.Value.Fails / kv.Value.Calls >= MinFailRate
                                    && (offered == null || offered.Contains(kv.Key)))
                .OrderByDescending(kv => kv.Value.Fails)
                .ThenByDescending(kv => (double)kv.Value.Fails / kv.Value.Calls)
                .Take(MaxHints).Select(kv => (kv.Key, kv.Value)).ToList();
        }
        if (bad.Count == 0) return null;
        var sb = new StringBuilder("# Your track record with these tools on this PC\n");
        sb.Append("Earlier runs of this model here often got these calls wrong. Do it the way that works:\n");
        foreach (var (tool, stat) in bad)
        {
            string kind = stat.Kinds.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "other";
            sb.Append($"- {tool}: failed {stat.Fails} of {stat.Calls} calls");
            if (kind != "other") sb.Append($" (mostly: {KindLabel(kind)})");
            sb.Append(". ").Append(Advice(tool, kind)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── failure kinds ─────────────────────────

    private static readonly (string Kind, Regex Pattern)[] KindRules =
    [
        ("not-the-model", new Regex(@"denied by the user|user (declined|denied|refused)|not approved|pressed Send now|cancell?ed by the user|stopped by the user|permission prompt was dismissed", RegexOptions.IgnoreCase)),
        ("must-read", new Regex(@"must read|read it first|read the file first|has not been read", RegexOptions.IgnoreCase)),
        ("text-not-found", new Regex(@"text not found|not found in .*matching|find block|no match for|old_string", RegexOptions.IgnoreCase)),
        ("ambiguous", new Regex(@"ambiguous|matches \d+ places|more than once|multiple matches", RegexOptions.IgnoreCase)),
        ("file-not-found", new Regex(@"file not found|does not exist|no such file|cannot find (the )?(file|path)|directory not found", RegexOptions.IgnoreCase)),
        ("outside-project", new Regex(@"outside the project|absolute paths are not allowed|not inside the project|access to the path .* is denied", RegexOptions.IgnoreCase)),
        ("bad-args", new Regex(@"missing (required )?(parameter|argument|field)|invalid (json|argument|parameter)|required property|expected .* got|could not parse|unknown parameter|is required", RegexOptions.IgnoreCase)),
        ("unknown-tool", new Regex(@"unknown tool|not a registered tool", RegexOptions.IgnoreCase)),
        ("timeout", new Regex(@"timed? ?out|took too long|timeout", RegexOptions.IgnoreCase)),
        ("command-failed", new Regex(@"exit code|exited with|is not recognized as|command not found|non-zero", RegexOptions.IgnoreCase)),
    ];

    /// <summary>What kind of failure an error text is ("other" when none fits; "not-the-model" for a refusal).</summary>
    public static string Classify(string? error)
    {
        string e = error ?? "";
        foreach (var (kind, pattern) in KindRules)
            if (pattern.IsMatch(e)) return kind;
        return "other";
    }

    private static string KindLabel(string kind) => kind switch
    {
        "must-read" => "edited a file it had not read",
        "text-not-found" => "the text to replace was not in the file",
        "ambiguous" => "the text to replace matched more than one place",
        "file-not-found" => "the path did not exist",
        "outside-project" => "a path outside the project",
        "bad-args" => "missing or malformed arguments",
        "unknown-tool" => "a tool name that does not exist",
        "timeout" => "it timed out",
        "command-failed" => "the command failed",
        _ => kind,
    };

    /// <summary>The one sentence that fixes the most common failure of a tool.</summary>
    public static string Advice(string tool, string kind)
    {
        bool edit = tool.Contains("edit", StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            "must-read" => "Call read_file on the file first, then edit it.",
            "text-not-found" when edit => "read_file the exact lines first and copy them character for character; for a small file use write_file with the complete new content instead.",
            "ambiguous" when edit => "Include two or three more surrounding lines so the text to replace occurs exactly once.",
            "file-not-found" => "Check the path with list_files or search_files before using it; paths are relative to the project root.",
            "outside-project" => "Use a path relative to the project root; for scratch files use the agent scratch folder.",
            "bad-args" => "Send every required argument with the names and types the tool's schema gives.",
            "unknown-tool" => "Only call tools that are in your tool list, spelled exactly.",
            "timeout" => "Split the work into smaller calls, or give a narrower path or pattern.",
            "command-failed" => "This is Windows (PowerShell/cmd): no POSIX-only commands; read the error, then change the command, not just retry it.",
            _ => "Read the error it returns and change the call before trying again — never repeat a call that just failed unchanged.",
        };
    }

    // ───────────────────────── file ─────────────────────────

    private void Load()
    {
        if (_path == null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);
            if (data?.Models == null) return;
            // Re-key case-insensitively (the serializer builds ordinal dictionaries).
            var models = new Dictionary<string, Dictionary<string, ToolStat>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (m, tools) in data.Models)
            {
                if (tools == null) continue;
                var t = new Dictionary<string, ToolStat>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, stat) in tools)
                {
                    if (stat == null || stat.Calls < 0 || stat.Fails < 0 || stat.Fails > stat.Calls) continue;   // a hand-edited file
                    stat.Kinds ??= new(StringComparer.Ordinal);
                    t[name] = stat;
                }
                models[m] = t;
            }
            _data = new FileShape { Models = models };
            _lastStamp = models.Values.SelectMany(t => t.Values).Select(s => s.LastUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _data = new FileShape();   // unreadable: start over, never fail a run over advice
        }
    }

    private void ScheduleSave()
    {
        if (_path == null) return;
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Write the file now when something changed (atomic: temp file, then replace).</summary>
    public void Save()
    {
        if (_path == null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(_data, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;   // try again with the next record
        }
    }

    private void DropOldestModel()
    {
        var oldest = _data.Models.OrderBy(kv => kv.Value.Values.Select(s => s.LastUtc).DefaultIfEmpty(DateTime.MinValue).Max())
            .Select(kv => kv.Key).FirstOrDefault();
        if (oldest != null) _data.Models.Remove(oldest);
    }
}
