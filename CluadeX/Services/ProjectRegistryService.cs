using System.IO;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// The projects CluadeX has opened: folder, name, when last and how often (~/.cluadex/projects.json). A chat with no
/// folder open gets the recent ones in its prompt, so "open the atmos project" resolves to D:\Code\Atmos at once —
/// no brain search, and it still works with the brain disconnected. Recorded whenever the app opens a folder
/// (the button, a chat switching back to its project) or an agent run opens one (open_project).
/// </summary>
public sealed class ProjectRegistryService
{
    public sealed class Entry
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime LastOpened { get; set; }
        public int Opens { get; set; }
    }

    private const int Keep = 50;
    private readonly string _file;
    private readonly object _lock = new();
    private List<Entry> _entries = new();

    /// <summary>The app's registry (null in a process that never built one, e.g. a harness that did not ask for it).</summary>
    public static ProjectRegistryService? Current { get; private set; }

    public ProjectRegistryService(FileSystemService? fileSystem = null, SettingsService? settings = null, string? file = null)
    {
        // The data folder (a portable run / the harnesses keep their own list).
        _file = file ?? System.IO.Path.Combine(settings?.DataRoot
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex"), "projects.json");
        try
        {
            if (File.Exists(_file))
                _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? new();
        }
        catch { _entries = new(); }   // a damaged list starts over; it is only a shortcut
        if (fileSystem != null)
            fileSystem.OnWorkingDirectoryChanged += () => Record(fileSystem.AppWorkingDirectory);
        Current = this;
    }

    /// <summary>Note that <paramref name="path"/> was opened now. Folders that no longer exist are ignored.</summary>
    public void Record(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return; }
        if (full.EndsWith(':')) return;                 // a drive root is not a project
        if (!Directory.Exists(full)) return;
        string json;
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase));
            if (e == null) _entries.Add(e = new Entry { Path = full, Name = System.IO.Path.GetFileName(full) });
            e.LastOpened = DateTime.Now;
            e.Opens++;
            _entries = _entries.OrderByDescending(x => x.LastOpened).Take(Keep).ToList();
            json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[projects] saving the list failed: {ex.Message}"); }
    }

    /// <summary>Add a project known from an older record (a saved chat's folder) without bumping it: kept at
    /// <paramref name="when"/>, and an entry already listed is left alone.</summary>
    public void Seed(string? path, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return; }
        if (full.EndsWith(':') || !Directory.Exists(full)) return;
        lock (_lock)
        {
            if (_entries.Any(x => string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            _entries.Add(new Entry { Path = full, Name = System.IO.Path.GetFileName(full), LastOpened = when, Opens = 1 });
            _entries = _entries.OrderByDescending(x => x.LastOpened).Take(Keep).ToList();
        }
    }

    /// <summary>The most recently opened projects that still exist, newest first.</summary>
    public IReadOnlyList<Entry> Recent(int max = 10)
    {
        lock (_lock)
            return _entries.Where(e => Directory.Exists(e.Path)).OrderByDescending(e => e.LastOpened).Take(max).ToList();
    }

    /// <summary>The prompt lines for a chat with no folder open: "- Atmos — D:\Code\Atmos". Empty when none.</summary>
    public string PromptList(int max = 10)
    {
        var recent = Recent(max);
        if (recent.Count == 0) return "";
        return "Projects opened before (most recent first) — when the user names one, call open_project with its path directly:\n"
            + string.Join("\n", recent.Select(e => $"- {e.Name} — {e.Path}"));
    }
}
