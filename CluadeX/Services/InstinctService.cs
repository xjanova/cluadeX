using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Backend for the Instinct System (Sprint 2 #1 — ECC parity).
///
/// Sprint 2 #1 scope (v1):
///   - JSON-backed store at ~/.cluadex/instincts.json
///   - CRUD + accept/reject voting + confidence (computed on the model)
///   - PromoteToSkill — writes ~/.cluadex/skills/promoted-{name}.md so the
///     existing SkillService picks it up on next scan
///   - RecordObservation — bumps occurrence + LastSeen (called by Stop hook
///     or by the AI itself via the upcoming `instinct_record` tool)
///   - Export / Import (a single JSON snapshot — for team sharing)
///
/// Deferred to v2:
///   - Automatic Stop-hook extraction from transcripts
///   - /evolve clustering (semantic similarity grouping)
///   - Brain MCP push for high-confidence instincts (cross-machine sync)
///   - SQLite migration for performance at scale
/// </summary>
public class InstinctService
{
    private readonly string _storePath;
    private readonly object _lock = new();
    private InstinctStore? _cache;

    /// <summary>Raised when the store mutates (UI re-binds).</summary>
    public event Action? Changed;

    public InstinctService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "instincts.json");
    }

    public string StorePath => _storePath;

    // ─── Persistence ─────────────────────────────────────────────────

    private InstinctStore Load()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(_storePath))
                {
                    var json = File.ReadAllText(_storePath);
                    _cache = JsonSerializer.Deserialize<InstinctStore>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new InstinctStore();
                }
                else
                {
                    _cache = SeedDemoStore();
                    Save(_cache);
                }
            }
            catch
            {
                // Corrupt file — start fresh, don't crash. Backup the bad file.
                try { File.Move(_storePath, _storePath + ".bad-" + DateTime.Now.Ticks); } catch { }
                _cache = new InstinctStore();
            }
            return _cache;
        }
    }

    private void Save(InstinctStore store)
    {
        lock (_lock)
        {
            store.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_storePath, json);
            _cache = store;
        }
        Changed?.Invoke();
    }

    public List<Instinct> GetAll()
    {
        var store = Load();
        // Returned in confidence-desc order so the UI puts the strongest first
        return store.Instincts.OrderByDescending(i => i.Confidence).ToList();
    }

    public Instinct? GetById(string id)
    {
        var store = Load();
        return store.Instincts.FirstOrDefault(i => i.Id == id);
    }

    // ─── Mutations ───────────────────────────────────────────────────

    public Instinct Add(Instinct instinct)
    {
        var store = Load();
        if (string.IsNullOrEmpty(instinct.Id))
            instinct.Id = Guid.NewGuid().ToString("N")[..8];
        if (instinct.FirstSeen == default) instinct.FirstSeen = DateTime.UtcNow;
        instinct.LastSeen = DateTime.UtcNow;
        store.Instincts.Add(instinct);
        Save(store);
        return instinct;
    }

    public bool Delete(string id)
    {
        var store = Load();
        var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
        if (hit == null) return false;
        store.Instincts.Remove(hit);
        Save(store);
        return true;
    }

    /// <summary>User confirmed this is a real pattern → boosts confidence.</summary>
    public void Accept(string id)
    {
        var store = Load();
        var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
        if (hit == null) return;
        hit.AcceptCount++;
        hit.LastSeen = DateTime.UtcNow;
        Save(store);
    }

    /// <summary>User said no → drops confidence so it stops surfacing.</summary>
    public void Reject(string id)
    {
        var store = Load();
        var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
        if (hit == null) return;
        hit.RejectCount++;
        hit.LastSeen = DateTime.UtcNow;
        Save(store);
    }

    /// <summary>Called by the Stop hook (or by the AI) when the pattern fires again.</summary>
    public void RecordObservation(string pattern, string? trigger = null, string? action = null)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        var store = Load();
        // Dedupe by exact Pattern text (case-insensitive). v2 will use semantic dedupe.
        var existing = store.Instincts.FirstOrDefault(i =>
            string.Equals(i.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.OccurrenceCount++;
            existing.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(trigger) && string.IsNullOrEmpty(existing.Trigger))
                existing.Trigger = trigger;
            if (!string.IsNullOrEmpty(action) && string.IsNullOrEmpty(existing.Action))
                existing.Action = action;
        }
        else
        {
            store.Instincts.Add(new Instinct
            {
                Pattern = pattern.Trim(),
                Trigger = trigger?.Trim() ?? "",
                Action = action?.Trim() ?? "",
            });
        }
        Save(store);
    }

    // ─── Promote → Skill ─────────────────────────────────────────────

    /// <summary>
    /// Write a Skill .md file based on this instinct, then mark Promoted=true.
    /// The next SkillService.ReloadSkills() picks it up automatically.
    /// </summary>
    public string? PromoteToSkill(string id)
    {
        var instinct = GetById(id);
        if (instinct == null) return null;
        if (!instinct.CanPromote && !instinct.Promoted) return null;

        string skillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "skills");
        Directory.CreateDirectory(skillsDir);

        string safeName = SanitizeFileName(instinct.Pattern);
        if (string.IsNullOrEmpty(safeName)) safeName = $"instinct-{instinct.Id}";
        string fileName = $"promoted-{safeName}.md";
        string filePath = Path.Combine(skillsDir, fileName);

        var md = new StringBuilder();
        md.AppendLine("---");
        md.AppendLine($"name: {safeName}");
        md.AppendLine($"description: {EscapeYamlValue(instinct.Pattern)}");
        if (instinct.Tags.Count > 0)
            md.AppendLine($"tags: [{string.Join(", ", instinct.Tags.Select(t => $"\"{t}\""))}]");
        md.AppendLine($"source: promoted-instinct ({instinct.Id})");
        md.AppendLine($"confidence: {instinct.Confidence:F2}");
        md.AppendLine($"occurrences: {instinct.OccurrenceCount}");
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine($"# {instinct.Pattern}");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(instinct.Trigger))
        {
            md.AppendLine("## When");
            md.AppendLine(instinct.Trigger);
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(instinct.Action))
        {
            md.AppendLine("## Do");
            md.AppendLine(instinct.Action);
            md.AppendLine();
        }
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine($"_Promoted automatically from observed instinct on " +
                      $"{DateTime.Now:yyyy-MM-dd HH:mm}. " +
                      $"Observed {instinct.OccurrenceCount}× · " +
                      $"accepted {instinct.AcceptCount}× · " +
                      $"rejected {instinct.RejectCount}×._");

        File.WriteAllText(filePath, md.ToString());

        // Mark as promoted
        var store = Load();
        var live = store.Instincts.FirstOrDefault(i => i.Id == id);
        if (live != null)
        {
            live.Promoted = true;
            live.PromotedSkillPath = filePath;
            Save(store);
        }
        return filePath;
    }

    // ─── Export / Import ─────────────────────────────────────────────

    public void ExportTo(string path)
    {
        var store = Load();
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json);
    }

    /// <summary>Returns count of imported instincts. Skips duplicates by Pattern.</summary>
    public int ImportFrom(string path)
    {
        if (!File.Exists(path)) return 0;
        var imported = JsonSerializer.Deserialize<InstinctStore>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (imported == null) return 0;

        var store = Load();
        int added = 0;
        foreach (var inc in imported.Instincts)
        {
            if (store.Instincts.Any(i =>
                string.Equals(i.Pattern, inc.Pattern, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (string.IsNullOrEmpty(inc.Id)) inc.Id = Guid.NewGuid().ToString("N")[..8];
            store.Instincts.Add(inc);
            added++;
        }
        if (added > 0) Save(store);
        return added;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) clean.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') clean.Append('-');
            // skip everything else
        }
        var s = clean.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length > 60) s = s[..60];
        return s;
    }

    private static string EscapeYamlValue(string s)
    {
        if (s.Contains(':') || s.Contains('#') || s.Contains('"'))
            return $"\"{s.Replace("\"", "\\\"")}\"";
        return s;
    }

    /// <summary>3 demo instincts shipped on first run so the UI isn't empty.</summary>
    private static InstinctStore SeedDemoStore() => new()
    {
        Instincts = new()
        {
            new Instinct
            {
                Pattern = "Run dotnet build after every C# edit before declaring done",
                Trigger = "User asks to edit any .cs / .xaml file",
                Action = "End the turn with `dotnet build` and only declare done if it succeeds.",
                Tags = new() { "csharp", "verify" },
                OccurrenceCount = 8, AcceptCount = 3,
                FirstSeen = DateTime.UtcNow.AddDays(-12),
                LastSeen = DateTime.UtcNow.AddHours(-2),
            },
            new Instinct
            {
                Pattern = "User prefers Thai status reports with English code/file paths",
                Trigger = "Reporting completion of a task",
                Action = "Write Thai prose for status, but keep file paths, commands, and code in English.",
                Tags = new() { "style", "communication" },
                OccurrenceCount = 15, AcceptCount = 6,
                FirstSeen = DateTime.UtcNow.AddDays(-20),
                LastSeen = DateTime.UtcNow.AddMinutes(-30),
            },
            new Instinct
            {
                Pattern = "Always commit + push when user says 'push' (no further confirmation)",
                Trigger = "User says push / push ก่อน / ขึ้น git",
                Action = "Stage everything relevant, write a thorough commit message, push without asking.",
                Tags = new() { "git", "workflow" },
                OccurrenceCount = 5, AcceptCount = 2, RejectCount = 0,
                FirstSeen = DateTime.UtcNow.AddDays(-4),
                LastSeen = DateTime.UtcNow.AddHours(-6),
            },
        },
    };
}
