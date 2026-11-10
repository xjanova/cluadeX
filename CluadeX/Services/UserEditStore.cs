using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Problems;

namespace CluadeX.Services;

/// <summary>
/// Where the editor remembers the lines the user edited by hand, per file (<see cref="UserEditRecord"/>):
/// &lt;data root&gt;\user-edits\&lt;hash of the path&gt;.json, one small file each. It is what keeps the user's colour on a file
/// that cannot hold a <see cref="UserEditMarker"/> comment (JSON, Markdown, XAML…) or where the setting is off, across a
/// reopen and a restart. Search (<see cref="FindInProject"/>) reads it together with the markers in the files.
/// </summary>
public sealed class UserEditStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _root;
    private readonly object _lock = new();

    public static UserEditStore? Current { get; set; }

    /// <summary>The app's store (made on first use, under the data root — a harness's portable root keeps it isolated).</summary>
    public static UserEditStore Shared => Current ??= new UserEditStore();

    public UserEditStore(string? root = null)
    {
        _root = root ?? Path.Combine(SettingsService.ResolveDataRoot(out _), "user-edits");
    }

    public string Root => _root;

    private string FileFor(string fullPath)
    {
        string key = Path.GetFullPath(fullPath).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(_root, hash + ".json");
    }

    public UserEditRecord? Get(string fullPath)
    {
        try
        {
            string f = FileFor(fullPath);
            if (!File.Exists(f)) return null;
            lock (_lock) return JsonSerializer.Deserialize<UserEditRecord>(File.ReadAllText(f, Encoding.UTF8), Json);
        }
        catch { return null; }   // a damaged record is the same as none: the markers in the file still show
    }

    /// <summary>Keep <paramref name="record"/> (an empty one removes the file).</summary>
    public void Put(UserEditRecord record)
    {
        string f = FileFor(record.Path);
        lock (_lock)
        {
            if (record.IsEmpty) { if (File.Exists(f)) File.Delete(f); return; }
            Directory.CreateDirectory(_root);
            string tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json), new UTF8Encoding(false));
            File.Move(tmp, f, overwrite: true);
        }
    }

    /// <summary>After the editor saved <paramref name="fullPath"/> from <paramref name="disk"/> to <paramref name="saved"/>.</summary>
    public UserEditRecord Remember(string fullPath, string disk, string saved, DateTime date)
    {
        var rec = ChangeMarks.Record(Get(fullPath), Path.GetFullPath(fullPath), disk, saved, date);
        Put(rec);
        return rec;
    }

    /// <summary>Every record of a file under <paramref name="root"/>.</summary>
    public List<UserEditRecord> Under(string root)
    {
        var list = new List<UserEditRecord>();
        if (!Directory.Exists(_root)) return list;
        string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<UserEditRecord>(File.ReadAllText(f, Encoding.UTF8), Json);
                if (r != null && r.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) list.Add(r);
            }
            catch { }
        }
        return list;
    }

    /// <summary>One user-edited spot, for the search list.</summary>
    public sealed record Spot(string FullPath, string RelPath, int Line, string Date, string Text, bool HasMarker);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "dist", "build", "out", ".next", "target", "__pycache__", ".venv", "venv" };

    /// <summary>
    /// Every place the user edited by hand under <paramref name="root"/>: lines with a <see cref="UserEditMarker.Tag"/>
    /// comment (files up to 2 MB, the usual build and dependency folders skipped) and the remembered lines of files that
    /// have no marker there. Capped at <paramref name="max"/>; <paramref name="openText"/> gives the editor's unsaved text
    /// of a file when it has one.
    /// </summary>
    public List<Spot> FindInProject(string root, Func<string, string?>? openText = null, int max = 2000, CancellationToken ct = default)
    {
        var spots = new List<Spot>();
        if (!Directory.Exists(root)) return spots;
        string full = Path.GetFullPath(root).TrimEnd('\\', '/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = Under(full).ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

        void Scan(string path, string text)
        {
            var at = new HashSet<int>();
            string rel = Path.GetRelativePath(full, path);
            foreach (var (line, date, t) in UserEditMarker.Find(text))
            {
                at.Add(line);
                spots.Add(new Spot(path, rel, line, date, t, true));
            }
            if (records.TryGetValue(path, out var rec))
            {
                var lines = UserEditMarker.SplitLines(text);
                var matched = ChangeMarks.Match(rec.Lines, lines);
                // One spot per run of remembered lines (a marker already stands for its run).
                int prev = -10;
                foreach (int l in matched)
                {
                    bool runGoesOn = l == prev + 1;
                    prev = l;
                    if (runGoesOn || at.Contains(l) || at.Contains(l - 1)) continue;
                    string date = rec.Lines.FirstOrDefault(r => r.Text == UserEditMarker.Normalize(lines[l - 1]))?.Date ?? "";
                    spots.Add(new Spot(path, rel, l, date, lines[l - 1].Trim(), false));
                }
            }
        }

        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0 && spots.Count < max)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir); dirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var d in dirs)
            {
                try
                {
                    if (SkipDirs.Contains(Path.GetFileName(d)) || (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                stack.Push(d);
            }
            foreach (var f in files)
            {
                if (spots.Count >= max) break;
                bool marked = UserEditMarker.StyleFor(f) != null;
                if (!marked && !records.ContainsKey(f)) continue;
                try
                {
                    string? text = openText?.Invoke(f);
                    if (text == null)
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length > 2_000_000) continue;
                        text = CluadeX.Helpers.TextFileCodec.Decode(File.ReadAllBytes(f)).Text;
                    }
                    seen.Add(f);
                    Scan(f, text);
                }
                catch { }
            }
        }
        // Remembered files the walk skipped (inside a skipped folder): still the user's edits.
        foreach (var rec in records.Values)
        {
            if (spots.Count >= max || seen.Contains(rec.Path) || !File.Exists(rec.Path)) continue;
            try { Scan(rec.Path, openText?.Invoke(rec.Path) ?? CluadeX.Helpers.TextFileCodec.Decode(File.ReadAllBytes(rec.Path)).Text); } catch { }
        }
        spots.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));
        return spots.Count > max ? spots.GetRange(0, max) : spots;
    }
}
