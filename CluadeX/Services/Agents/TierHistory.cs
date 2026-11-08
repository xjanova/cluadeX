using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>
/// How often each kind of sub-agent had to climb a tier (owner 2026-11-08: "ระวังเรื่องเลือกโมเดลผิดแต่แรก แทนที่จะประหยัด
/// กลับต้องทำงานสองรอบ"): per agent type ("explore", "general", a specialist's name) and the tier it started on, the last
/// <see cref="Window"/> outcomes — climbed or not. <see cref="DispatchPolicy.StartTier"/> reads it: a kind that keeps
/// failing cheap starts one tier up, so the cheap attempt is not paid for again and again.
/// Kept in <c>subagent-tiers.json</c> in the data folder; a damaged or missing file is an empty history.
/// </summary>
public sealed class TierHistory
{
    public const int Window = 20;

    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<string, List<bool>> _outcomes = new(StringComparer.OrdinalIgnoreCase);

    public TierHistory(string? path)
    {
        _path = path;
        if (path == null) return;
        try
        {
            if (File.Exists(path))
                _outcomes = JsonSerializer.Deserialize<Dictionary<string, List<bool>>>(File.ReadAllText(path)) is { } d
                    ? new Dictionary<string, List<bool>>(d, StringComparer.OrdinalIgnoreCase)
                    : _outcomes;
        }
        catch { _outcomes = new(StringComparer.OrdinalIgnoreCase); }
    }

    public static string KeyOf(string type, SubAgentTier tier) => $"{(type ?? "").Trim().ToLowerInvariant()}|{tier.ToString().ToLowerInvariant()}";

    /// <summary>Runs and climbs in the window for this type starting at this tier.</summary>
    public (int Runs, int Climbs) Of(string type, SubAgentTier tier)
    {
        lock (_lock)
            return _outcomes.TryGetValue(KeyOf(type, tier), out var l) ? (l.Count, l.Count(x => x)) : (0, 0);
    }

    /// <summary>One request finished: it started at <paramref name="startTier"/> and climbed (or not).</summary>
    public void Record(string type, SubAgentTier startTier, bool climbed)
    {
        if (startTier == SubAgentTier.Deep) return;   // nothing to learn: deep never climbs
        string json;
        lock (_lock)
        {
            string key = KeyOf(type, startTier);
            if (!_outcomes.TryGetValue(key, out var l)) _outcomes[key] = l = new List<bool>();
            l.Add(climbed);
            while (l.Count > Window) l.RemoveAt(0);
            json = JsonSerializer.Serialize(_outcomes);
        }
        if (_path == null) return;
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* a history we cannot save is only a worse guess next time */ }
    }
}
