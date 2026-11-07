using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Skills;

/// <summary>
/// The brain's half of skill picking (owner 2026-11-07: "ถ้าเราย้ายสกิลไปที่สมอง และให้ cluadex เรียกเทียบงานที่จะใช้สกิลไหน").
/// When a BrainX brain is connected, every skill gets a short card note in it (name, description, when to use — never the
/// prompt), tag <see cref="Tag"/>, folder <see cref="Folder"/>. Each turn asks the brain's semantic search, limited to that
/// tag, which cards fit the task: multilingual embeddings read Thai and paraphrases the local word table misses. The
/// ranking is one more signal for <see cref="SkillPicker"/>, never the only one — no brain, a slow brain or an error and
/// the pick is the local one. The prompt a skill runs is always read from its file on this PC, never from the note.
/// What was written is kept in DataRoot/skill-brain-index.json (name → note id + card hash): a card is written again only
/// when its text changed.
/// </summary>
public sealed class SkillBrainIndex
{
    public const string Tag = "cluadex-skill";
    public const string Folder = "Notes/CluadeX-Skills";
    public const string TitlePrefix = "CluadeX skill: ";
    /// <summary>How long a turn waits for the brain's ranking before it picks without it.</summary>
    public static readonly TimeSpan RankTimeout = TimeSpan.FromSeconds(4);

    private readonly BrainSyncService? _brain;
    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;
    private readonly SemaphoreSlim _indexing = new(1, 1);
    private readonly object _stateLock = new();
    private Dictionary<string, Entry>? _state;
    /// <summary>Cards the brain refused this session: not tried again until the app restarts (no call storm per turn).</summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    public sealed class Entry
    {
        public string? Id { get; set; }
        public string Hash { get; set; } = "";
    }

    public SkillBrainIndex(SettingsService settings, BrainSyncService? brain = null, DebugLogService? log = null)
    {
        _settings = settings; _brain = brain; _log = log;
    }

    public bool Enabled => _settings.Settings.SkillBrainIndexEnabled;
    public bool BrainReady => _brain?.IsBrainAvailable == true;

    private string StatePath => Path.Combine(_settings.DataRoot, "skill-brain-index.json");

    // ── cards (pure) ──────────────────────────────────────────────────────

    public static string CardTitle(SkillDefinition s) => TitlePrefix + s.Name;

    public static string CardBody(SkillDefinition s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {CardTitle(s)}");
        sb.AppendLine();
        sb.AppendLine(s.Description.Trim());
        if (!string.IsNullOrWhiteSpace(s.WhenToUse)) { sb.AppendLine(); sb.AppendLine($"**Use when:** {s.WhenToUse!.Trim()}"); }
        if (!string.IsNullOrWhiteSpace(s.ArgumentHint)) sb.AppendLine($"**Arguments:** {s.ArgumentHint}");
        sb.AppendLine();
        sb.AppendLine($"Slash command: `/{s.Name}` · source: {(s.IsBuiltIn ? "built-in" : s.Source.ToString().ToLowerInvariant())}");
        sb.AppendLine();
        sb.AppendLine("_A card CluadeX keeps so the brain can say which skill fits a task. The prompt the skill runs lives in "
            + "CluadeX's skill files, not here; editing this note changes nothing CluadeX runs._");
        return sb.ToString();
    }

    public static string CardHash(SkillDefinition s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CardBody(s)));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>The skill names in a brain search answer, best first: titles "CluadeX skill: name" only (another note that
    /// happens to carry the tag is ignored). Anything that is not the expected JSON = an empty list.</summary>
    public static IReadOnlyList<string> ParseRanked(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return names;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return names;
            foreach (var hit in arr.EnumerateArray())
            {
                if (!hit.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                string title = t.GetString() ?? "";
                if (!title.StartsWith(TitlePrefix, StringComparison.Ordinal)) continue;
                string name = title[TitlePrefix.Length..].Trim();
                if (SkillService.ValidNameRegex.IsMatch(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
        }
        catch (JsonException) { }
        return names;
    }

    // ── state ─────────────────────────────────────────────────────────────

    private Dictionary<string, Entry> State()
    {
        lock (_stateLock)
        {
            if (_state != null) return _state;
            try
            {
                if (File.Exists(StatePath))
                    _state = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StatePath));
            }
            catch { }
            return _state ??= new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveState()
    {
        lock (_stateLock)
        {
            try
            {
                Directory.CreateDirectory(_settings.DataRoot);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, StatePath, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>Skills whose card in the brain is missing or out of date.</summary>
    public IReadOnlyList<SkillDefinition> Stale(IEnumerable<SkillDefinition> skills)
    {
        var st = State();
        lock (_stateLock)
            return skills.Where(s => !st.TryGetValue(s.Name, out var e) || e.Id == null || e.Hash != CardHash(s)).ToList();
    }

    // ── brain calls ───────────────────────────────────────────────────────

    /// <summary>Write the cards that are missing or changed (at most <paramref name="max"/> per call, one call at a time).
    /// Returns how many were written. Never throws.</summary>
    public async Task<int> EnsureIndexedAsync(IReadOnlyList<SkillDefinition> skills, CancellationToken ct = default, int max = 40)
    {
        if (!Enabled || _brain == null || !BrainReady) return 0;
        if (!await _indexing.WaitAsync(0, ct)) return 0;
        int written = 0;
        try
        {
            List<SkillDefinition> todo;
            lock (_stateLock) todo = Stale(skills).Where(x => !_refused.Contains(x.Name)).Take(max).ToList();
            foreach (var s in todo)
            {
                ct.ThrowIfCancellationRequested();
                var st = State();
                Entry? old; lock (_stateLock) st.TryGetValue(s.Name, out old);
                string hash = CardHash(s);
                bool ok = false; string? id = old?.Id;
                // No id kept (a new PC, a lost state file): the card may already be in the brain — find it by its title
                // so it is updated, not written twice.
                if (id == null)
                {
                    var (fok, found) = await _brain.CallToolAsync("brain_search", new()
                    {
                        ["query"] = CardTitle(s),
                        ["limit"] = 5,
                        ["compact"] = true,
                    }, ct);
                    if (fok) id = IdForTitle(found, CardTitle(s));
                }
                if (id != null)
                {
                    // The card changed: say so under it (the brain's search reads the whole note).
                    var (aok, _) = await _brain.CallToolAsync("brain_append_note", new()
                    {
                        ["id"] = id,
                        ["content"] = $"\n## Card updated {DateTime.Now:yyyy-MM-dd}\n\n" + CardBody(s),
                    }, ct);
                    ok = aok;
                }
                if (!ok)
                {
                    var (cok, text) = await _brain.CallToolAsync("brain_create_note", new()
                    {
                        ["title"] = CardTitle(s),
                        ["folder"] = Folder,
                        ["tags"] = Tag + ",cluadex,skill",
                        ["content"] = CardBody(s),
                    }, ct);
                    if (cok) { ok = true; id = ReadId(text); }
                    else _log?.Warn("SkillBrain", $"card for '{s.Name}' not written: {text}");
                }
                if (!ok) { lock (_stateLock) _refused.Add(s.Name); continue; }
                lock (_stateLock) st[s.Name] = new Entry { Id = id, Hash = hash };
                written++;
            }
            if (written > 0) { SaveState(); _log?.Info("SkillBrain", $"wrote {written} skill card(s) to the brain ({Folder})"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("SkillBrain", "indexing stopped: " + ex.Message); }
        finally { _indexing.Release(); }
        return written;
    }

    /// <summary>The id of the note titled exactly <paramref name="title"/> in a brain search answer, or null.</summary>
    public static string? IdForTitle(string? json, string title)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var hit in arr.EnumerateArray())
                if (hit.TryGetProperty("title", out var t) && t.GetString() == title
                    && hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static string? ReadId(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var key in new[] { "id", "noteId" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The brain's ranking of skill cards for <paramref name="task"/>, best first, or empty (no brain, off, slow,
    /// an error). Bounded by <see cref="RankTimeout"/>; never throws except for the caller's own cancel.</summary>
    public async Task<IReadOnlyList<string>> RankAsync(string task, CancellationToken ct = default)
    {
        if (!Enabled || _brain == null || string.IsNullOrWhiteSpace(task) || !BrainReady) return Array.Empty<string>();
        lock (_stateLock) if (State().Count == 0) return Array.Empty<string>();   // nothing indexed yet
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RankTimeout);
        try
        {
            string q = Services.Mcp.McpServerManager.RedactSecrets(task.Length > 300 ? task[..300] : task);
            var (ok, text) = await _brain.CallToolAsync("brain_semantic_search", new()
            {
                ["query"] = q,
                ["tag"] = Tag,
                ["limit"] = 8,
                ["compact"] = true,
            }, cts.Token);
            return ok ? ParseRanked(text) : Array.Empty<string>();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Info("SkillBrain", $"brain ranking took over {RankTimeout.TotalSeconds:0}s — picking without it");
            return Array.Empty<string>();
        }
    }
}
