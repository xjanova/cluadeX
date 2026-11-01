using System.IO;
using System.Windows;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// The UI Studio's shared state — one instance for the app, so the UI Studio page and the Node Graph page work on
/// the same project, the same running page and the same selection. It owns the preview servers (the local static
/// server, a Node dev server) so they outlive a page switch, writes every source edit (with undo), and is an exit
/// hook: no dev server survives CluadeX.
///
/// Threading: every member is called and every event is raised on the UI thread. The dev server's output arrives
/// on pool threads and is marshalled with BeginInvoke (never Invoke — a blocking hop from a reader thread is how
/// CluadeX froze for 11 minutes once).
/// </summary>
public sealed class StudioService : IAppShutdownHook, IDisposable
{
    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;
    private StudioStaticServer? _static;
    private DevServerRunner? _dev;
    private CancellationTokenSource? _startCts;
    private readonly Stack<UndoEntry> _undo = new();
    private const int MaxUndo = 50;

    private sealed record UndoEntry(string Path, string Before, string After, bool Bom, string Label);

    public StudioService(SettingsService settings, DebugLogService? log = null)
    {
        _settings = settings;
        _log = log;
    }

    // ───────────────────────────── state ─────────────────────────────

    public StudioProjectInfo Project { get; private set; } = new();
    public string Root => Project.Root;
    public bool HasProject => Project.Kind is not StudioProjectKind.None;

    /// <summary>The page shown now (what the address bar says).</summary>
    public string? PageUrl { get; private set; }
    /// <summary>The page is served from this machine (localhost) — the only pages whose source the Studio writes.</summary>
    public bool PageIsLocal { get; private set; }

    public StudioElement? Selection { get; private set; }
    public DomNode? Tree { get; private set; }
    public bool TreeTruncated { get; private set; }

    public bool IsServing => _static?.IsRunning == true || _dev?.IsRunning == true || _external;
    private bool _external;

    /// <summary>Where a server-rendered site (Laravel) runs — set from the page gallery (default: APP_URL).</summary>
    public string? ServerBaseUrl { get; set; }

    /// <summary>The root URL the project's pages are served under now (null while nothing serves them).</summary>
    public string? CurrentBaseUrl =>
        _static?.IsRunning == true ? _static.BaseUrl
        : _dev is { IsRunning: true, Url: { } u } && Uri.TryCreate(u, UriKind.Absolute, out var du) ? du.GetLeftPart(UriPartial.Authority) + "/"
        : _external ? ServerBaseUrl
        : null;
    public bool IsStarting { get; private set; }
    public string WebDataFolder => Path.Combine(_settings.DataRoot, "UiStudio", "WebView2");
    /// <summary>The app's data folder (the page gallery keeps its thumbnails under it, never in the project).</summary>
    public string DataRoot => _settings.DataRoot;

    public event Action? ProjectChanged;
    public event Action? SelectionChanged;
    public event Action? TreeChanged;
    public event Action? ServingChanged;
    public event Action<string>? ServerOutput;
    /// <summary>A page URL the view should navigate to.</summary>
    public event Action<string>? NavigateRequested;
    /// <summary>JavaScript the view should run in the page (the overlay API: <c>__cx.*</c>).</summary>
    public event Action<string>? ScriptRequested;
    /// <summary>A source file was written by the Studio (path).</summary>
    public event Action<string>? SourceWritten;
    /// <summary>Files of the open project changed on disk — by the AI in the chat, the code editor, the Studio or any
    /// other program (full paths, gathered over a short pause). Raised whatever page is on screen: listeners that
    /// draw something only note the change while they are hidden and catch up when shown.</summary>
    public event Action<IReadOnlyCollection<string>>? FilesChanged;
    /// <summary>Something needs the running page awake now (the Node Graph's live view came on screen).</summary>
    public event Action? WakeRequested;

    /// <summary>The UI Studio's page is on screen (set by its view). A reload asked while it is hidden waits and is
    /// done the moment it is shown — a hidden page does no work for changes nobody is watching.</summary>
    public bool PageVisible
    {
        get => _pageVisible;
        set
        {
            if (_pageVisible == value) return;
            _pageVisible = value;
            FlushPendingReload();
        }
    }
    private bool _pageVisible;
    private bool? _pendingReload;

    /// <summary>The Node Graph's live view (the page's element tree) is on screen: the page must keep running even
    /// though the Studio itself is hidden.</summary>
    public bool KeepPageAwake
    {
        get => _keepAwake;
        set
        {
            if (_keepAwake == value) return;
            _keepAwake = value;
            if (!value) return;
            WakeRequested?.Invoke();
            FlushPendingReload();
        }
    }
    private bool _keepAwake;

    private void FlushPendingReload()
    {
        if (!(_pageVisible || _keepAwake) || _pendingReload is not { } cssOnly) return;
        _pendingReload = null;
        if (IsServing) ReloadRequested?.Invoke(cssOnly);
    }

    /// <summary>The last page the Studio asked its browser to show — for a browser that is created after the ask
    /// (the project was opened from the chat before the Studio page was ever built).</summary>
    public string? LastRequestedUrl { get; private set; }

    private void RequestNavigate(string url)
    {
        LastRequestedUrl = url;
        NavigateRequested?.Invoke(url);
    }

    /// <summary>What the browser is sent to when its project is gone: no page shows under another project's name.</summary>
    public const string BlankPage = "about:blank";

    /// <summary>The browser may still hold the last project's page (until a navigation lands). What that page sends
    /// — a pick, a tree, an inline edit whose line numbers mean another project's files — is dropped.</summary>
    public bool PageIsOld { get; private set; }

    /// <summary>The view reports that a navigation landed: messages now come from a page opened for this project.</summary>
    public void NotePageLoaded() => PageIsOld = false;

    private void LeavePage()
    {
        PageIsOld = true;
        PageUrl = null;
        PageIsLocal = false;
        LastRequestedUrl = null;
        ServerBaseUrl = null;
        Selection = null;
        Tree = null;
        TreeTruncated = false;
        _undo.Clear();
        NavigateRequested?.Invoke(BlankPage);
    }

    public int UndoCount => _undo.Count;

    // ───────────────────────────── project & serving ─────────────────────────────

    public void OpenProject(string root)
    {
        if (string.Equals(Root, root, StringComparison.OrdinalIgnoreCase) && HasProject)
        {
            Project = StudioProject.Detect(root);   // re-detect (locator may have been installed)
            ProjectChanged?.Invoke();
            return;
        }
        StopServing();
        Project = StudioProject.Detect(root);
        LeavePage();
        StartWatching();
        _log?.Info("UiStudio", $"project {root} → {Project.Kind} ({Project.Framework})");
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
        TreeChanged?.Invoke();
    }

    public void CloseProject()
    {
        StopServing();
        StopWatching();
        Project = new StudioProjectInfo();
        LeavePage();
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
        TreeChanged?.Invoke();
    }

    /// <summary>Start the right server for the project and navigate to it. Returns an error key or null.</summary>
    public async Task<string?> StartAsync()
    {
        if (!HasProject) return "studio.err.noProject";
        if (IsServing || IsStarting) { if (PageUrl != null) RequestNavigate(PageUrl); return null; }
        IsStarting = true;
        int start = ++_startRun;
        ServingChanged?.Invoke();
        try
        {
            switch (Project.Kind)
            {
                case StudioProjectKind.Static:
                {
                    var server = new StudioStaticServer(Root);
                    try { server.Start(); }
                    catch (Exception ex)
                    {
                        _log?.Warn("UiStudio", "static server failed", ex);
                        server.Dispose();
                        return "studio.err.server";
                    }
                    _static = server;
                    string url = server.BaseUrl + (Project.EntryHtml ?? "").Replace('\\', '/');
                    RequestNavigate(url);
                    return null;
                }
                case StudioProjectKind.Vite:
                case StudioProjectKind.NodeOther:
                {
                    var dev = new DevServerRunner(Root);
                    var dispatcher = Application.Current?.Dispatcher;
                    dev.Output += line => dispatcher?.BeginInvoke(() => { if (ReferenceEquals(_dev, dev)) ServerOutput?.Invoke(line); });
                    dev.UrlFound += url => dispatcher?.BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(_dev, dev)) return;
                        RequestNavigate(url);
                        ServingChanged?.Invoke();   // the base URL is known now: the page gallery can shoot
                    });
                    dev.Exited += code => dispatcher?.BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(_dev, dev)) return;
                        ServerOutput?.Invoke($"[exited {code}]");
                        _dev = null;
                        dev.Dispose();
                        ServingChanged?.Invoke();
                    });
                    _dev = dev;
                    _startCts = new CancellationTokenSource();
                    var (ok, error) = await dev.StartAsync(Project.DevScript ?? "dev", _startCts.Token);
                    if (!ok)
                    {
                        if (ReferenceEquals(_dev, dev)) _dev = null;
                        dev.Dispose();
                        if (error == "cancelled") return null;
                        _log?.Warn("UiStudio", "dev server failed: " + error);
                        return error == "unsafe" ? "studio.err.unsafe"
                            : error != null && error.StartsWith("noNode", StringComparison.Ordinal) ? "studio.err.noNode" : "studio.err.install";
                    }
                    return null;
                }
                case StudioProjectKind.Server:
                {
                    // Nothing is started: the site already runs somewhere (Laragon, php artisan serve, the live site).
                    string? baseUrl = ServerBaseUrl ?? Pages.LaravelPages.AppUrl(Root) ?? "http://localhost:8000/";
                    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) || b.Scheme is not ("http" or "https")) return "studio.err.badUrl";
                    ServerBaseUrl = baseUrl;
                    _external = true;
                    RequestNavigate(baseUrl);
                    return null;
                }
                case StudioProjectKind.Simulated:
                    return null;   // simulated screens: nothing to run
                case StudioProjectKind.Unsupported:
                    return Project.UnsupportedReason ?? "studio.unsupported.none";
                default:
                    return "studio.err.noProject";
            }
        }
        finally
        {
            // (a start that was stopped: the next project's own start may be running by now, and it keeps its flag)
            if (start == _startRun)
            {
                IsStarting = false;
                ServingChanged?.Invoke();
            }
        }
    }

    private int _startRun;

    public void StopServing()
    {
        try { _startCts?.Cancel(); } catch { }
        _startCts?.Dispose();
        _startCts = null;
        _startRun++;
        IsStarting = false;
        _pendingReload = null;   // (the project's watcher stays: it belongs to the project, not to the preview)
        _static?.Dispose();
        _static = null;
        _dev?.Dispose();
        _dev = null;
        _external = false;
        ServingChanged?.Invoke();
    }

    /// <summary>The view reports where the page is now.</summary>
    public void NotePage(string? url)
    {
        PageUrl = url;
        PageIsLocal = Uri.TryCreate(url, UriKind.Absolute, out var u)
                      && u.Scheme is "http" or "https"
                      && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────── overlay traffic ─────────────────────────────

    public void HandleOverlayMessage(OverlayMessage msg)
    {
        if (PageIsOld) return;
        switch (msg.Type)
        {
            case "select":
                Selection = msg.Element;
                SelectionChanged?.Invoke();
                break;
            case "deselect":
                Selection = null;
                SelectionChanged?.Invoke();
                break;
            case "tree":
                Tree = msg.Tree;
                TreeTruncated = msg.Truncated;
                Tree?.LinkParents();
                TreeChanged?.Invoke();
                break;
        }
    }

    /// <summary>Run overlay JS in the page (the view executes it).</summary>
    public void RunScript(string js) => ScriptRequested?.Invoke(js);

    /// <summary>Run JS and get its JSON result (set by the view while it has a page; null otherwise).</summary>
    public Func<string, Task<string?>>? Evaluate { get; set; }

    // ───────────────────────────── project files on disk ─────────────────────────────

    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadTimer;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _reloadWanted;

    /// <summary>A file the page shows changed on disk (by the Studio, the AI or another editor): the view reloads the
    /// page — or only swaps the stylesheets when nothing but CSS changed (no flash, scroll and state kept). A static
    /// site and a server-rendered one (Laravel views) are reloaded from here; a dev server (Vite…) reloads its own page.</summary>
    public event Action<bool>? ReloadRequested;   // true = CSS only

    /// <summary>Folders whose files are never the project's own source (installed packages, build output, logs).</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "vendor", "storage", "build", "dist", "coverage", "target", "__pycache__" };

    /// <summary>One watcher for the open project, from the moment it is opened (not only while a preview runs): the
    /// Node Graph and the preview follow the files through it. An OS watcher costs nothing while nothing changes.</summary>
    private void StartWatching()
    {
        StopWatching();
        if (!HasProject || !Directory.Exists(Root)) return;
        try
        {
            var w = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,   // an AI or a git checkout writing many files at once
            };
            FileSystemEventHandler onChange = (_, e) => Changed(e.FullPath);
            w.Changed += onChange;
            w.Created += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
            w.Error += (_, e) => _log?.Warn("UiStudio", "file watcher overflow", e.GetException());
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "file watcher failed", ex); }
    }

    /// <summary>Whether a changed file is the project's own source, worth reacting to. Called on the watcher's thread:
    /// it touches no state.</summary>
    public static bool IsSourceChange(string root, string path)
    {
        if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        string rel = path[root.Length..];
        foreach (var seg in rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            if (seg.StartsWith('.') || IgnoredDirs.Contains(seg)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not ("" or ".tmp" or ".swp" or ".bak" or ".log" or ".lock") && !path.EndsWith('~');
    }

    /// <summary>Files the Studio itself just wrote without moving any line: the page already shows that change (the
    /// live preview), and every locator on the page still points at the right line — so the reload is skipped (no
    /// flash, no lost scroll). A write that adds or removes lines still reloads, so later elements' locators are fresh.</summary>
    private readonly Dictionary<string, DateTime> _quietWrites = new(StringComparer.OrdinalIgnoreCase);

    private void NoteSelfWrite(string path, string before, string after)
    {
        static int Lines(string t) => t.Count(c => c == '\n');
        if (Lines(before) == Lines(after)) _quietWrites[path] = DateTime.UtcNow;
        else _quietWrites.Remove(path);
    }

    private void Changed(string path)
    {
        string root = Root;
        if (!IsSourceChange(root, path)) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(root, Root, StringComparison.OrdinalIgnoreCase) || _watcher == null) return;   // another project since
            _changedPaths.Add(path);
            bool quiet = _quietWrites.TryGetValue(path, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(2);
            if (!quiet && ReloadsPage(path, ext))
            {
                _changed.Add(ext);
                _reloadWanted = true;
            }
            _reloadTimer ??= NewReloadTimer();
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    /// <summary>Does this file change what the preview shows, where the Studio (not a dev server) has to reload?</summary>
    private bool ReloadsPage(string path, string ext) => Project.Kind switch
    {
        StudioProjectKind.Static => true,
        // Blade views, routes and controllers render the page; CSS/JS count only where the site serves them as they are.
        StudioProjectKind.Server => ext == ".php"
            || ext is ".css" or ".js" && path.Contains(Path.DirectorySeparatorChar + "public" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private System.Windows.Threading.DispatcherTimer NewReloadTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var paths = _changedPaths.ToList();
            _changedPaths.Clear();
            bool reload = _reloadWanted;
            bool cssOnly = _changed.Count > 0 && _changed.All(e => e == ".css");
            _changed.Clear();
            _reloadWanted = false;
            if (paths.Count > 0)
            {
                try { FilesChanged?.Invoke(paths); }
                catch (Exception ex) { _log?.Warn("UiStudio", "a files-changed listener failed", ex); }
            }
            if (!reload || !IsServing || Project.Kind == StudioProjectKind.Server && !PageIsLocal) return;
            if (PageVisible || KeepPageAwake) ReloadRequested?.Invoke(cssOnly);
            else _pendingReload = (_pendingReload ?? true) && cssOnly;   // waits for the page to be shown; a full reload wins
        };
        return t;
    }

    private void StopWatching()
    {
        if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        _reloadTimer?.Stop();
        _changed.Clear();
        _changedPaths.Clear();
        _reloadWanted = false;
    }

    public void SelectNode(int nodeId) => RunScript($"window.__cx && __cx.selectById({nodeId})");
    public void HoverNode(int nodeId) => RunScript($"window.__cx && __cx.hoverById({nodeId})");
    public void RequestTree() => RunScript("window.__cx && __cx.sendTree()");

    // ───────────────────────────── source edits ─────────────────────────────

    public SourceRef? ResolveSource(string? src) => HasProject ? SourceLocator.Resolve(src, Root) : null;

    /// <summary>
    /// Apply an edit to the element's opening tag in its source file. Returns null on success, else a
    /// localization key (the file is untouched then). Only pages served from this machine are written.
    /// </summary>
    public string? EditSource(string? src, string tagName, string label, Func<string, SourceTag, SourceDialect, SourceEdit> edit)
    {
        if (!PageIsLocal) return "studio.err.remotePage";
        var where = ResolveSource(src);
        if (where == null) return "studio.err.noSource";
        CluadeX.Helpers.TextFileCodec.TextFile file;
        try { file = CluadeX.Helpers.TextFileCodec.Read(where.FullPath); }
        catch (Exception ex) { _log?.Warn("UiStudio", "read failed " + where.FullPath, ex); return "studio.err.read"; }
        if (!file.IsValidUtf8) return "studio.err.notUtf8";

        var dialect = SourceTagEditor.DialectFor(where.FullPath);
        var tag = SourceTagEditor.Locate(file.Text, where.Line, where.Column, tagName, dialect, out _);
        if (tag == null) return "studio.err.stale";
        var result = edit(file.Text, tag, dialect);
        if (!result.Ok) return "studio.edit." + result.Error;
        if (result.Text == file.Text) return null;

        try { CluadeX.Helpers.TextFileCodec.Write(where.FullPath, result.Text!, file.HasBom); }
        catch (Exception ex) { _log?.Warn("UiStudio", "write failed " + where.FullPath, ex); return "studio.err.write"; }

        // Value edits are already on screen (the live preview); structure edits (move, duplicate, delete) are not.
        if (label is "inspector" or "text" or "resize") NoteSelfWrite(where.FullPath, file.Text, result.Text!);
        else _quietWrites.Remove(where.FullPath);
        _undo.Push(new UndoEntry(where.FullPath, file.Text, result.Text!, file.HasBom, label));
        TrimUndo();
        _log?.Info("UiStudio", $"{label} → {where}");
        SourceWritten?.Invoke(where.FullPath);
        return null;
    }

    /// <summary>
    /// Swap two sibling elements that the page shows next to each other (<paramref name="firstSrc"/> right before
    /// <paramref name="secondSrc"/>). Both must be in the same file and written next to each other there.
    /// </summary>
    public string? SwapSiblings(string? firstSrc, string? secondSrc, string label)
    {
        var a = ResolveSource(firstSrc);
        var b = ResolveSource(secondSrc);
        if (a == null || b == null) return "studio.edit.noSibling";
        if (!string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase)) return "studio.edit.otherFile";
        return EditSource(firstSrc, "", label, (text, tag, dialect) =>
        {
            int expected = SourceTagEditor.OffsetOf(text, b.Line, b.Column);
            return expected < 0 ? SourceEdit.Fail("stale") : SourceTagEditor.SwapWithNext(text, tag, dialect, expected);
        });
    }

    /// <summary>Write a whole-file edit made by the Studio's own tools (the Flutter widget tools) — only while the
    /// file is still exactly <paramref name="before"/> (nothing else changed it since it was read); it joins the undo
    /// stack like every Studio edit. Returns null or an error key.</summary>
    public string? WriteEdit(string path, string before, string after, bool bom, string label)
    {
        string now;
        try { now = CluadeX.Helpers.TextFileCodec.Read(path).Text; }
        catch (Exception ex) { _log?.Warn("UiStudio", "read failed " + path, ex); return "studio.err.read"; }
        if (now != before) return "studio.fedit.changedMeanwhile";
        try { CluadeX.Helpers.TextFileCodec.Write(path, after, bom); }
        catch (Exception ex) { _log?.Warn("UiStudio", "write failed " + path, ex); return "studio.err.write"; }
        _undo.Push(new UndoEntry(path, before, after, bom, label));
        TrimUndo();
        _log?.Info("UiStudio", $"{label} → {path}");
        SourceWritten?.Invoke(path);
        return null;
    }

    /// <summary>Put files back as they were (the review's "undo all"): each path gets its old text, a file that did
    /// not exist (null) is deleted. The undo stack is cleared — its entries no longer match the files. Returns the
    /// paths that could not be restored.</summary>
    public List<string> RestoreFiles(IReadOnlyDictionary<string, string?> before)
    {
        var failed = new List<string>();
        foreach (var (path, text) in before)
        {
            try
            {
                if (text == null) { if (File.Exists(path)) File.Delete(path); continue; }
                if (File.Exists(path) && CluadeX.Helpers.TextFileCodec.Read(path).Text == text) continue;
                bool bom = File.Exists(path) && CluadeX.Helpers.TextFileCodec.Read(path).HasBom;
                CluadeX.Helpers.TextFileCodec.Write(path, text, bom);
                SourceWritten?.Invoke(path);
            }
            catch (Exception ex) { _log?.Warn("UiStudio", "restore failed " + path, ex); failed.Add(path); }
        }
        _undo.Clear();
        return failed;
    }

    private void TrimUndo()
    {
        if (_undo.Count <= MaxUndo) return;
        var keep = _undo.Take(MaxUndo).Reverse().ToList();
        _undo.Clear();
        foreach (var e in keep) _undo.Push(e);
    }

    /// <summary>Undo the last Studio edit — only when the file is still exactly what that edit wrote (an edit by
    /// the AI or the user since then is never thrown away). Returns null or an error key.</summary>
    public string? Undo(out string? label)
    {
        label = null;
        if (_undo.Count == 0) return "studio.undo.empty";
        var e = _undo.Peek();
        label = e.Label;
        string now;
        try { now = CluadeX.Helpers.TextFileCodec.Read(e.Path).Text; }
        catch { return "studio.err.read"; }
        if (now != e.After) { _undo.Clear(); return "studio.undo.changed"; }
        _quietWrites.Remove(e.Path);   // an undo is not on screen yet: reload
        try { CluadeX.Helpers.TextFileCodec.Write(e.Path, e.Before, e.Bom); }
        catch { return "studio.err.write"; }
        _undo.Pop();
        SourceWritten?.Invoke(e.Path);
        return null;
    }

    // ───────────────────────────── lifetime ─────────────────────────────

    public void OnAppShutdown() => Dispose();

    public void Dispose()
    {
        try { _startCts?.Cancel(); } catch { }
        StopWatching();
        _static?.Dispose();
        _static = null;
        _dev?.Dispose();
        _dev = null;
    }
}
