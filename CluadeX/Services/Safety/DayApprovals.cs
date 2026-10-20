using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Safety;

/// <summary>
/// "Allow for today" (owner 2026-10-20: "อนุญาตทั้งวัน ในวันนี้ก็ไม่ต้องกดอีก"): the same chat-scope keys "Allow in this
/// chat" remembers (<c>chat-tool|server|leaf</c>, <c>desktop-chat:app</c>…), but for every chat until local midnight.
/// Kept in <c>permissions-today.json</c> under the data root so a restart the same day does not ask again; a file from
/// another day is ignored. Only keys a prompt offered as "Allow in this chat" ever get here — an Ask rule, an
/// always-ask call and the red dangerous-action prompt never offer it.
/// </summary>
public sealed class DayApprovals
{
    public const string FileName = "permissions-today.json";
    private const int MaxKeys = 500;

    private readonly string? _file;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _day;
    private bool _loaded;

    /// <param name="file">The JSON file (null = memory only, for tests).</param>
    /// <param name="now">Local clock (tests pin it).</param>
    public DayApprovals(string? file, Func<DateTime>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTime.Now);
    }

    public bool Contains(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_lock) { Roll(); return _keys.Contains(key); }
    }

    public void Add(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            Roll();
            if (_keys.Count >= MaxKeys || !_keys.Add(key)) return;
            Save();
        }
    }

    /// <summary>How many keys today holds (for the harness).</summary>
    public int Count { get { lock (_lock) { Roll(); return _keys.Count; } } }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(_now());
        if (!_loaded)
        {
            _loaded = true;
            _day = today;
            Load(today);
            return;
        }
        if (today != _day) { _keys.Clear(); _day = today; }
    }

    private void Load(DateOnly today)
    {
        try
        {
            if (_file == null || !File.Exists(_file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_file, Encoding.UTF8));
            var root = doc.RootElement;
            if (!root.TryGetProperty("date", out var d) || d.GetString() != today.ToString("yyyy-MM-dd")) return;
            if (!root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return;
            foreach (var k in keys.EnumerateArray())
                if (k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 and < 400 } s && _keys.Count < MaxKeys)
                    _keys.Add(s);
        }
        catch { /* a damaged file just means today starts empty */ }
    }

    private void Save()
    {
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string json = JsonSerializer.Serialize(new { date = _day.ToString("yyyy-MM-dd"), keys = _keys.OrderBy(k => k, StringComparer.Ordinal) });
            string tmp = _file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* remembered in memory for this run anyway */ }
    }
}
