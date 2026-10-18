using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.UiStudio.Pages;

/// <summary>One saved thumbnail of a page.</summary>
public sealed class PageShotEntry
{
    public string File { get; set; } = "";
    public string Url { get; set; } = "";
    public string Device { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public string? Title { get; set; }
    public int Status { get; set; }
}

/// <summary>A web page the user added to the gallery (or opened from the address bar).</summary>
public sealed class WebPageRef
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

/// <summary>The gallery's per-project settings.</summary>
public sealed class PageGalleryState
{
    /// <summary>Where a server-rendered site runs (Laravel) — the user's choice, else APP_URL.</summary>
    public string? BaseUrl { get; set; }
    public List<WebPageRef> WebPages { get; set; } = new();
    public Dictionary<string, PageShotEntry> Shots { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The user's answer to "draw this app's real screens with Flutter?" (null: not asked yet).</summary>
    public bool? FlutterRender { get; set; }
}

/// <summary>
/// Thumbnails of a project's pages, kept on disk under the app's data folder (never in the project): one JPEG per
/// page and device plus an index. A thumbnail is "stale" once any source file of the project is newer than it — it
/// is still shown (no blank flash on return) while a new one is taken.
/// </summary>
public sealed class PageShotStore
{
    public const int MaxWebPages = 40;
    private readonly string _index;
    public string Folder { get; }
    public PageGalleryState State { get; private set; } = new();

    public PageShotStore(string dataRoot, string projectRoot)
    {
        string key = Hash(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())[..16];
        Folder = Path.Combine(dataRoot, "UiStudio", "pages", key);
        _index = Path.Combine(Folder, "index.json");
        Load();
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Key(string pageId, string device) => pageId + "|" + device;

    private void Load()
    {
        try
        {
            if (!File.Exists(_index)) return;
            State = JsonSerializer.Deserialize<PageGalleryState>(File.ReadAllText(_index)) ?? new PageGalleryState();
            State.Shots = new Dictionary<string, PageShotEntry>(State.Shots ?? new(), StringComparer.Ordinal);
            State.WebPages ??= new List<WebPageRef>();
        }
        catch { State = new PageGalleryState(); }   // a damaged index: start over (the images are re-taken)
    }

    public void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = _index + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, _index, overwrite: true);
        }
        catch { /* the gallery still works for this session */ }
    }

    public PageShotEntry? Get(string pageId, string device)
    {
        if (!State.Shots.TryGetValue(Key(pageId, device), out var e)) return null;
        return File.Exists(ImagePath(e)) ? e : null;
    }

    /// <summary>The image of an entry — always a plain .jpg name inside <see cref="Folder"/>, whatever the index says
    /// (a tampered index can't point a delete at another file).</summary>
    public string ImagePath(PageShotEntry e)
    {
        string name = Path.GetFileName(e.File ?? "");
        if (name.Length == 0 || !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name != e.File) name = "invalid.jpg";
        return Path.Combine(Folder, name);
    }

    private void DeleteImage(PageShotEntry e)
    {
        string path = ImagePath(e);
        if (Path.GetFileName(path) == "invalid.jpg" || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); } catch { }
        try { File.Delete(Path.ChangeExtension(path, ".json")); } catch { }
    }

    /// <summary>What goes with a picture (a rendered screen's widget map and problems) — next to it, same name.</summary>
    public string SidecarPath(PageShotEntry e) => Path.ChangeExtension(ImagePath(e), ".json");

    public void SaveSidecar(PageShotEntry e, string json)
    {
        try { File.WriteAllText(SidecarPath(e), json); } catch { }
    }

    public string? ReadSidecar(PageShotEntry e)
    {
        try { string p = SidecarPath(e); return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; }
    }

    /// <summary>Save a page's JPEG (overwrites the old one) and remember it.</summary>
    public PageShotEntry Save(string pageId, string device, byte[] jpeg, string url, string? title, int status)
    {
        Directory.CreateDirectory(Folder);
        string key = Key(pageId, device);
        var e = new PageShotEntry
        {
            File = Hash(key)[..20] + ".jpg", Url = url, Device = device, CapturedUtc = DateTime.UtcNow,
            Title = title is { Length: > 120 } ? title[..120] : title, Status = status,
        };
        File.WriteAllBytes(ImagePath(e), jpeg);
        State.Shots[key] = e;
        SaveState();
        return e;
    }

    /// <summary>Forget every thumbnail (the site moved to another address): files go too.</summary>
    public void ClearShots()
    {
        foreach (var e in State.Shots.Values) DeleteImage(e);
        State.Shots.Clear();
        SaveState();
    }

    /// <summary>Drop thumbnails of pages the project no longer has.</summary>
    public void Prune(ICollection<string> pageIds)
    {
        var keep = new HashSet<string>(pageIds, StringComparer.Ordinal);
        var gone = State.Shots.Where(kv => !keep.Contains(kv.Key[..kv.Key.LastIndexOf('|')])).Select(kv => kv.Key).ToList();
        if (gone.Count == 0) return;
        foreach (var k in gone)
        {
            DeleteImage(State.Shots[k]);
            State.Shots.Remove(k);
        }
        SaveState();
    }

    public bool AddWebPage(string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        State.WebPages.RemoveAll(w => string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase));
        State.WebPages.Insert(0, new WebPageRef { Url = url, Title = string.IsNullOrWhiteSpace(title) ? u.Host + u.AbsolutePath : title, AddedUtc = DateTime.UtcNow });
        if (State.WebPages.Count > MaxWebPages) State.WebPages.RemoveRange(MaxWebPages, State.WebPages.Count - MaxWebPages);
        SaveState();
        return true;
    }

    public void RemoveWebPage(string url)
    {
        State.WebPages.RemoveAll(w => string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase));
        SaveState();
    }

    private static readonly HashSet<string> StampExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".vue", ".svelte", ".astro", ".php", ".dart",
        ".md", ".mdx", ".json", ".arb", ".svg", ".png", ".jpg", ".jpeg", ".webp",
    };

    /// <summary>When the project's source last changed (the newest file the pages are built from).</summary>
    public static DateTime ProjectStamp(string root, CancellationToken ct = default)
    {
        DateTime newest = DateTime.MinValue;
        foreach (var f in SourceSearch.ProjectFiles(root, StampExt, 8000))
        {
            ct.ThrowIfCancellationRequested();
            try { var t = File.GetLastWriteTimeUtc(f); if (t > newest) newest = t; } catch { }
        }
        return newest;
    }

    /// <summary>A host the gallery may visit on its own: this machine or a local dev domain. Any other site is only
    /// shot when the user asks (a thumbnail is a real visit).</summary>
    public static bool IsLocalHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        string h = u.Host.ToLowerInvariant();
        return u.IsLoopback || h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal) || h.EndsWith(".test", StringComparison.Ordinal)
               || h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".lan", StringComparison.Ordinal);
    }
}
