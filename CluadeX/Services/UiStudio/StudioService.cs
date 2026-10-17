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

    public bool IsServing => _static?.IsRunning == true || _dev?.IsRunning == true;
    public bool IsStarting { get; private set; }
    public string WebDataFolder => Path.Combine(_settings.DataRoot, "UiStudio", "WebView2");

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
        _undo.Clear();
        Project = StudioProject.Detect(root);
        Selection = null;
        Tree = null;
        _log?.Info("UiStudio", $"project {root} → {Project.Kind} ({Project.Framework})");
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
        TreeChanged?.Invoke();
    }

    public void CloseProject()
    {
        StopServing();
        _undo.Clear();
        Project = new StudioProjectInfo();
        Selection = null;
        Tree = null;
        PageUrl = null;
        ProjectChanged?.Invoke();
        SelectionChanged?.Invoke();
        TreeChanged?.Invoke();
    }

    /// <summary>Start the right server for the project and navigate to it. Returns an error key or null.</summary>
    public async Task<string?> StartAsync()
    {
        if (!HasProject) return "studio.err.noProject";
        if (IsServing || IsStarting) { if (PageUrl != null) NavigateRequested?.Invoke(PageUrl); return null; }
        IsStarting = true;
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
                    StartWatching();
                    string url = server.BaseUrl + (Project.EntryHtml ?? "").Replace('\\', '/');
                    NavigateRequested?.Invoke(url);
                    return null;
                }
                case StudioProjectKind.Vite:
                case StudioProjectKind.NodeOther:
                {
                    var dev = new DevServerRunner(Root);
                    var dispatcher = Application.Current?.Dispatcher;
                    dev.Output += line => dispatcher?.BeginInvoke(() => ServerOutput?.Invoke(line));
                    dev.UrlFound += url => dispatcher?.BeginInvoke(() => { if (ReferenceEquals(_dev, dev)) NavigateRequested?.Invoke(url); });
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
                case StudioProjectKind.Unsupported:
                    return Project.UnsupportedReason ?? "studio.unsupported.none";
                default:
                    return "studio.err.noProject";
            }
        }
        finally
        {
            IsStarting = false;
            ServingChanged?.Invoke();
        }
    }

    public void StopServing()
    {
        try { _startCts?.Cancel(); } catch { }
        _startCts?.Dispose();
        _startCts = null;
        StopWatching();
        _static?.Dispose();
        _static = null;
        _dev?.Dispose();
        _dev = null;
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

    // ───────────────────────────── static-site reload ─────────────────────────────

    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _reloadTimer;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file of the static site changed on disk (by the Studio, the AI or another editor): the view reloads
    /// the page — or only swaps the stylesheets when nothing but CSS changed (no flash, scroll and state kept).</summary>
    public event Action<bool>? ReloadRequested;   // true = CSS only

    private void StartWatching()
    {
        StopWatching();
        if (Project.Kind != StudioProjectKind.Static) return;
        try
        {
            var w = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, e) => Changed(e.FullPath);
            w.Changed += onChange;
            w.Created += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, e) => Changed(e.FullPath);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "file watcher failed", ex); }
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
        string rel = path.Length > Root.Length ? path[Root.Length..] : path;
        if (rel.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (seg.StartsWith('.')) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is "" or ".tmp" or ".swp" or ".bak") return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_quietWrites.TryGetValue(path, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(2)) return;
            _changed.Add(ext);
            _reloadTimer ??= NewReloadTimer();
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private System.Windows.Threading.DispatcherTimer NewReloadTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            bool cssOnly = _changed.Count > 0 && _changed.All(e => e == ".css");
            _changed.Clear();
            if (IsServing) ReloadRequested?.Invoke(cssOnly);
        };
        return t;
    }

    private void StopWatching()
    {
        if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        _reloadTimer?.Stop();
        _changed.Clear();
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
