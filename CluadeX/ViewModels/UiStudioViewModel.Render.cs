using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.ViewModels;

/// <summary>
/// Real screens for a simulated (Flutter) app: Flutter draws each screen with the app's own theme, fonts and
/// widgets (<see cref="FlutterRenderer"/>), the picture replaces the wireframe on its card and on the stage, and
/// every change to the app's code draws the affected screens again — the screen on the stage first, while the AI
/// or the user is editing it.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private readonly List<FileSystemWatcher> _libWatchers = new();
    /// <summary>The app's gen-l10n setup: its .arb files are sources, the Dart gen-l10n makes from them is not.</summary>
    private FlutterL10n? _l10n;
    private DispatcherTimer? _renderDebounce;
    private bool _rendering, _renderAgain;
    private CancellationTokenSource? _renderCts;
    private int _renderRun;

    /// <summary>The open app is a Flutter app: real screens and the widget tools (a WPF app is wireframes only).</summary>
    public bool IsFlutterApp => _catalog?.Flutter != null;
    /// <summary>The widget palette: a Flutter screen on the stage.</summary>
    public bool ShowPalette => HasStage && IsFlutterApp;

    /// <summary>Set by a harness to answer the one-time "run flutter test here?" question without a dialog.</summary>
    public bool? RenderConsentOverride { get; set; }

    private bool _isRendering;
    public bool IsRendering { get => _isRendering; private set => SetProperty(ref _isRendering, value); }

    private string _renderStatus = "";
    public string RenderStatus { get => _renderStatus; private set => SetProperty(ref _renderStatus, value); }

    private bool _showRealRender = true;
    /// <summary>Show Flutter's real picture of a screen (when there is one) instead of the wireframe.</summary>
    public bool ShowRealRender
    {
        get => _showRealRender;
        set
        {
            if (!SetProperty(ref _showRealRender, value)) return;
            foreach (var n in new[] { nameof(ShowWireframe), nameof(StageShowsRender), nameof(StageShowsSketch) }) OnPropertyChanged(n);
        }
    }

    /// <summary>The other side of the real / wireframe switch.</summary>
    public bool ShowWireframe { get => !_showRealRender; set => ShowRealRender = !value; }

    /// <summary>The stage shows the real picture (else the wireframe).</summary>
    public bool StageShowsRender => _showRealRender && _stageRender != null;
    public bool StageShowsSketch => !StageShowsRender;

    private ImageSource? _stageRender;
    /// <summary>The stage screen as Flutter drew it (null until drawn).</summary>
    public ImageSource? StageRender
    {
        get => _stageRender;
        private set
        {
            if (!SetProperty(ref _stageRender, value)) return;
            foreach (var n in new[] { nameof(StageShowsRender), nameof(StageShowsSketch), nameof(StageRenderShown) }) OnPropertyChanged(n);
        }
    }

    private IReadOnlyList<WidgetBox> _stageBoxes = Array.Empty<WidgetBox>();
    public IReadOnlyList<WidgetBox> StageBoxes
    {
        get => _stageBoxes;
        private set { if (SetProperty(ref _stageBoxes, value)) OnPropertyChanged(nameof(StageBoxesShown)); }
    }

    private IReadOnlyList<string> _stageProblems = Array.Empty<string>();
    /// <summary>What went wrong while Flutter drew the stage screen (overflow, an exception in build).</summary>
    public IReadOnlyList<string> StageProblems
    {
        get => _stageProblems;
        private set { if (SetProperty(ref _stageProblems, value)) OnPropertyChanged(nameof(HasStageProblems)); }
    }
    public bool HasStageProblems => _stageProblems.Count > 0;

    private string? _compileError;
    /// <summary>The app didn't compile at the last draw (the compiler's message).</summary>
    public string? CompileError
    {
        get => _compileError;
        private set { if (SetProperty(ref _compileError, value)) OnPropertyChanged(nameof(HasCompileError)); }
    }
    public bool HasCompileError => _compileError != null;

    private WidgetBox? _stageBox;
    /// <summary>The widget picked on the real picture (its exact file:line from Flutter's widget inspector).</summary>
    public WidgetBox? StageBox
    {
        get => _stageBox;
        set
        {
            if (!SetProperty(ref _stageBox, value)) return;
            if (value != null && _stageNode != null) { _stageNode = null; OnPropertyChanged(nameof(StageNode)); }
            foreach (var n in new[] { nameof(HasStageNode), nameof(StageNodeLabel), nameof(StageNodeText), nameof(StageNodeWhere), nameof(StageNodeUse) })
                OnPropertyChanged(n);
            RefreshTools();
        }
    }

    /// <summary>A click on the real picture (Ctrl+click opens the line at once).</summary>
    public void PickStageBox(WidgetBox box, bool open)
    {
        StageBox = box;
        if (open) OpenStageNode();
    }

    /// <summary>Where the screen's own file uses the picked widget: the nearest box around it written in that file.</summary>
    internal WidgetBox? UseSiteOf(WidgetBox box)
    {
        string? screenFile = _stageCard?.Page.SourcePath;
        if (screenFile == null || string.Equals(box.File, screenFile, StringComparison.OrdinalIgnoreCase)) return null;
        return _stageBoxes
            .Where(b => b.Depth < box.Depth && string.Equals(b.File, screenFile, StringComparison.OrdinalIgnoreCase)
                        && b.X <= box.X + 0.5 && b.Y <= box.Y + 0.5 && b.X + b.W >= box.X + box.W - 0.5 && b.Y + b.H >= box.Y + box.H - 0.5)
            .OrderByDescending(b => b.Depth).FirstOrDefault();
    }

    /// <summary>Called when the stage screen changes: show its last picture right away.</summary>
    private void LoadStageRender()
    {
        UpdateCompileError();
        var c = _stageCard;
        if (c == null || _store == null) { StageRender = null; StageBoxes = Array.Empty<WidgetBox>(); StageProblems = Array.Empty<string>(); return; }
        var e = _store.Get(c.Page.Id, "app");
        if (e == null) { StageRender = null; StageBoxes = Array.Empty<WidgetBox>(); StageProblems = Array.Empty<string>(); return; }
        StageRender = LoadImage(_store.ImagePath(e), decodeWidth: 0);
        var (boxes, problems) = ReadRenderSidecar(_store.ReadSidecar(e));
        StageBoxes = boxes;
        StageProblems = problems;
        ResolvePendingFocus();
    }

    /// <summary>The widget map kept beside a picture (for a harness that checks it).</summary>
    public static IReadOnlyList<WidgetBox> ReadRenderSidecarPublic(string? json) => ReadRenderSidecar(json).Boxes;

    internal static (List<WidgetBox> Boxes, List<string> Problems) ReadRenderSidecar(string? json)
    {
        var boxes = new List<WidgetBox>();
        var problems = new List<string>();
        if (string.IsNullOrEmpty(json)) return (boxes, problems);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("problems", out var pr)) foreach (var p in pr.EnumerateArray()) if (p.GetString() is { } s) problems.Add(s);
            if (r.TryGetProperty("boxes", out var bx))
                foreach (var b in bx.EnumerateArray())
                    boxes.Add(new WidgetBox(b.GetProperty("Widget").GetString() ?? "", b.GetProperty("File").GetString() ?? "", b.GetProperty("Line").GetInt32(),
                        b.GetProperty("Column").GetInt32(), b.GetProperty("X").GetDouble(), b.GetProperty("Y").GetDouble(), b.GetProperty("W").GetDouble(),
                        b.GetProperty("H").GetDouble(), b.GetProperty("Depth").GetInt32(),
                        b.TryGetProperty("Id", out var id) ? id.GetInt32() : -1, b.TryGetProperty("Parent", out var pa) ? pa.GetInt32() : -1));
        }
        catch { }
        return (boxes, problems);
    }

    // ───────────────────────────── consent ─────────────────────────────

    /// <summary>Drawing real screens runs the app's code under flutter test — asked once per project.</summary>
    private bool RenderAllowed(bool ask)
    {
        if (_store == null || _catalog?.Flutter == null) return false;
        if (RenderConsentOverride is bool forced) return forced;
        if (_store.State.FlutterRender is bool answered) return answered;
        if (!ask) return false;
        int choice = Views.Editor.ChoiceDialog.Show(T("studio.render.askTitle"), T("studio.render.askBody", Path.GetFileName(_studio.Root.TrimEnd(Path.DirectorySeparatorChar))),
            new[] { T("studio.render.allow"), T("studio.render.notNow") }, 1);
        _store.State.FlutterRender = choice == 0;
        _store.SaveState();
        OnPropertyChanged(nameof(CanRender));
        return choice == 0;
    }

    public bool CanRender => _catalog?.Flutter != null && _store?.State.FlutterRender != false;

    // ───────────────────────────── drawing ─────────────────────────────

    /// <summary>Draw the given screens (the stage screen first). A request while a draw is running is merged into
    /// the next one — a burst of saves draws once.</summary>
    internal async void RenderScreens(IEnumerable<PageCard> cards, bool ask = false)
    {
        if (_catalog?.Flutter is not { } flutter || _store == null) return;
        foreach (var c in cards) if (c.IsScreen) _renderWanted.Add(c);
        if (_renderWanted.Count == 0) return;
        if (_rendering) { _renderAgain = true; return; }
        if (!RenderAllowed(ask)) { _renderWanted.Clear(); return; }
        _rendering = true;
        IsRendering = true;
        var ct = (_renderCts = CancellationTokenSource.CreateLinkedTokenSource(_pagesCts.Token)).Token;
        try
        {
            while (_renderWanted.Count > 0 && !ct.IsCancellationRequested)
            {
                // the stage screen alone first (seconds), then the rest in one batch
                var batch = _stageCard != null && _renderWanted.Contains(_stageCard) && _renderWanted.Count > 1
                    ? new List<PageCard> { _stageCard }
                    : _renderWanted.ToList();
                foreach (var c in batch) _renderWanted.Remove(c);
                RenderStatus = T("studio.render.drawing", batch.Count);
                foreach (var c in batch) c.SetState("rendering", T("studio.render.drawingOne"));
                string outDir = Path.Combine(_studio.DataRoot, "UiStudio", "render", Path.GetFileName(_store.Folder), "run" + (++_renderRun % 3));
                // what the app's requests are answered with in this draw (the switch, the AI's values)
                string samples = SampleModeKey();
                RenderRun run;
                try
                {
                    run = await FlutterRenderer.RunAsync(flutter, batch.Select(c => c.Page).ToList(), outDir, _loc.CurrentLanguage, ct,
                        line => { if (line.Length > 0 && line != "·") _log?.Info("UiStudio", "render: " + line); },
                        sampleData: samples != "off", sampleOverrides: _store?.SampleOverridesPath);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested) _log?.Warn("UiStudio", "render failed", ex);
                    run = new RenderRun { Error = "studio.render.failed" };
                }
                if (ct.IsCancellationRequested) break;
                ApplyRun(batch, run, samples);
                if (_renderAgain) { _renderAgain = false; }
            }
        }
        finally
        {
            _rendering = false;
            IsRendering = false;
            RenderStatus = CompileError != null ? T("studio.render.compileFailed") : "";
        }
        if (_renderAgain || _renderWanted.Count > 0) { _renderAgain = false; RenderScreens(Array.Empty<PageCard>()); }
    }

    private readonly HashSet<PageCard> _renderWanted = new();

    /// <summary>The compiler's message for each screen whose last draw did not compile (one broken file stops every
    /// screen drawn with it; the screens drawn in another batch keep their own result).</summary>
    private readonly Dictionary<PageCard, string> _compileErrors = new();

    /// <summary>The stage shows the compile error of its own screen.</summary>
    private void UpdateCompileError()
    {
        CompileError = _stageCard != null && _compileErrors.TryGetValue(_stageCard, out var e) ? e : null;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyRun(List<PageCard> batch, RenderRun run, string samples)
    {
        if (run.Error == null)
        {
            foreach (var c in batch)
                if (run.CompileError != null) _compileErrors[c] = run.CompileError; else _compileErrors.Remove(c);
            UpdateCompileError();
        }
        if (run.Error != null && run.Error != "cancelled")
        {
            Status(T(run.Error), error: run.Error != "studio.render.dryRun");
            foreach (var c in batch) c.SetState(c.HasSketch ? "sketch" : "failed", c.HasSketch ? T("studio.pages.simulated") : T("studio.pages.noSketch"));
            return;
        }
        for (int i = 0; i < batch.Count && i < run.Screens.Count; i++)
        {
            var c = batch[i];
            var sr = run.Screens[i];
            if (sr.Skipped != null)
            {
                c.SetState("sketch", T("studio.render.skipped", sr.Skipped));
                continue;
            }
            if (sr.PngPath == null || _store == null)
            {
                c.SetState("failed", run.CompileError != null ? T("studio.render.compileFailed") : T("studio.render.notDrawn"));
                continue;
            }
            byte[] jpeg;
            try
            {
                using var fs = File.OpenRead(sr.PngPath);
                (jpeg, _, _) = Views.UiStudio.PageShooter.ToJpeg(fs, 900);
            }
            catch { c.SetState("failed", T("studio.render.notDrawn")); continue; }
            var entry = _store.Save(c.Page.Id, "app", jpeg, "flutter:" + c.Page.Id, null, sr.Problems.Count > 0 ? 500 : 200);
            _store.SaveSidecar(entry, JsonSerializer.Serialize(new { boxes = sr.Boxes, problems = sr.Problems, noise = sr.Noise, requests = sr.Requests, samples }));
            c.Thumb = LoadImage(_store.ImagePath(entry));
            c.ThumbDevice = "app";
            c.IsStale = false;
            c.Problems = sr.Problems.Count;
            // (the numbers and names on it are made up when its requests were answered with sample data; with the
            // samples off, a screen that asked for data shows what it shows without an answer)
            int fed = sr.Requests.Count(r => !r.EndsWith(" -", StringComparison.Ordinal));
            string at = entry.CapturedUtc.ToLocalTime().ToString("HH:mm:ss");
            c.SetState("done", sr.Problems.Count > 0 ? T("studio.render.withProblems", sr.Problems.Count)
                : fed > 0 ? T("studio.render.drawnSample", at, fed)
                : samples == "off" && sr.Requests.Count > 0 ? T("studio.render.drawnNoSample", at) : T("studio.render.drawn", at));
            if (ReferenceEquals(c, _stageCard)) LoadStageRender();
        }
        UpdateSummary();
    }

    /// <summary>Show each screen's last real picture (if any) on its card; the ones older than the code, or drawn with
    /// other sample data (the switch, the AI's values), are marked.</summary>
    private void ApplyCachedRenders()
    {
        if (_store == null) return;
        string samples = SampleModeKey();
        foreach (var c in _allCards.Where(c => c.IsScreen))
        {
            var e = _store.Get(c.Page.Id, "app");
            if (e == null) continue;
            c.Thumb = LoadImage(_store.ImagePath(e));
            c.ThumbDevice = "app";
            string? side = _store.ReadSidecar(e);
            c.IsStale = e.CapturedUtc < _projectStamp || SidecarSamples(side) is { } drawnWith && drawnWith != samples;
            var (_, problems) = ReadRenderSidecar(side);
            c.Problems = problems.Count;
        }
    }

    // ───────────────────────────── commands ─────────────────────────────

    private System.Windows.Input.ICommand? _renderStageCommand, _renderAllCommand, _fixProblemsCommand;

    /// <summary>Draw the stage screen again now (asks the one-time question if it hasn't been answered).</summary>
    public System.Windows.Input.ICommand RenderStageCommand => _renderStageCommand ??= new RelayCommand(() =>
    {
        if (_store?.State.FlutterRender == false) { _store.State.FlutterRender = null; _store.SaveState(); }
        if (_stageCard != null) RenderScreens(new[] { _stageCard }, ask: true);
    }, () => _catalog?.Flutter != null && _stageCard != null);

    public System.Windows.Input.ICommand RenderAllCommand => _renderAllCommand ??= new RelayCommand(() =>
    {
        if (_store?.State.FlutterRender == false) { _store.State.FlutterRender = null; _store.SaveState(); }
        RenderScreens(ScreenCards.ToList(), ask: true);
    }, () => _catalog?.Flutter != null);

    /// <summary>Hand what Flutter reported (a compile error, overflow, an exception while drawing) to the AI to fix.</summary>
    public System.Windows.Input.ICommand FixProblemsCommand => _fixProblemsCommand ??= new RelayCommand(() =>
    {
        if (_stageCard == null || !(HasStageProblems || HasCompileError)) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[UI Studio] Flutter reported problems while drawing a screen of this app. Fix them in the code.");
        sb.AppendLine();
        sb.AppendLine($"Screen: {_stageCard.Page.Detail} (\"{_stageCard.Title}\"{(_stageCard.Page.Route != null ? ", route " + _stageCard.Page.Route : "")})");
        if (_stageCard.Page.SourcePath != null) sb.AppendLine("Screen file: " + Rel(_stageCard.Page.SourcePath));
        if (CompileError != null) { sb.AppendLine(); sb.AppendLine("The app does not compile:"); sb.AppendLine("```"); sb.AppendLine(CompileError); sb.AppendLine("```"); }
        if (StageProblems.Count > 0) { sb.AppendLine(); sb.AppendLine("While drawing the screen:"); foreach (var p in StageProblems.Take(12)) sb.AppendLine("- " + p); }
        sb.AppendLine();
        sb.AppendLine("Keep the screen's design and the app's widget kit; fix the cause (an overflow means the content needs to wrap, scroll or shrink). The UI Studio draws the screen again when the files change.");
        AskAi(sb.ToString());
    }, () => (HasStageProblems || HasCompileError) && !AiWorking);

    // ───────────────────────────── following the code ─────────────────────────────

    /// <summary>Watch the app's lib/ (and assets) — every save draws the screens again, the stage screen first.</summary>
    private void WatchAppSources()
    {
        StopWatchingAppSources();
        // the tools' colours and palette come from this app (its colours, its assets)
        _swatches = null;
        _palette = null;
        OnPropertyChanged(nameof(Swatches));
        OnPropertyChanged(nameof(Palette));
        OnPropertyChanged(nameof(IsFlutterApp));
        OnPropertyChanged(nameof(ShowPalette));
        _l10n = null;
        RefreshSampleInfo();
        if (_catalog?.Flutter == null) return;
        try { _l10n = FlutterL10n.Load(_studio.Root); }
        catch (Exception ex) { _log?.Warn("UiStudio", "reading l10n.yaml failed", ex); }
        StartAppSourceWatchers();
    }

    /// <summary>Only the folders the screens are drawn from: lib/, assets/, the .arb folder, and the root's own files
    /// (pubspec.yaml, l10n.yaml) without its subfolders. One watcher on the whole root also queued everything under
    /// build/ and .dart_tool/ — which the Studio's own flutter test, pub get and a git checkout fill by the thousand —
    /// into an 8 KB buffer; it overflowed, and the lib/ saves in that burst were lost without a trace: the screens
    /// were not drawn again and a review round never saw the file change (2026-11-02).</summary>
    private void StartAppSourceWatchers()
    {
        DisposeAppSourceWatchers();
        string root = _studio.Root;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        // the root alone: its files, and lib/ or assets/ made after the watch started (DirectoryName)
        AddAppSourceWatcher(root, recursive: false);
        var dirs = new List<string>();
        foreach (var d in new[] { Path.Combine(root, "lib"), Path.Combine(root, "assets") })
            if (Directory.Exists(d)) { AddAppSourceWatcher(d, recursive: true); dirs.Add(Path.GetFullPath(d) + Path.DirectorySeparatorChar); }
        // an arb-dir outside lib/ and assets/ (IsArb wants that folder itself, so its subfolders are not needed)
        string? arb = _l10n?.ArbDir;
        if (arb is not { Length: > 0 } || SamePath(arb, root)) return;
        string arbSlash = arb.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (dirs.Any(d => arbSlash.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return;
        if (Directory.Exists(arb)) { AddAppSourceWatcher(arb, recursive: false); return; }
        // Not there yet (l10n.yaml written before the .arb folder, or a git checkout took it away): watch the nearest
        // folder above it that is there, so the folder is seen when it appears (OnAppSourceEvent). The root watcher
        // already covers an arb-dir straight under the root.
        string rootSlash = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!arbSlash.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase)) return;
        string? up = Path.GetDirectoryName(arb.TrimEnd(Path.DirectorySeparatorChar));
        while (up != null && !SamePath(up, root) && !Directory.Exists(up)) up = Path.GetDirectoryName(up);
        if (up == null || SamePath(up, root)) return;
        string upSlash = up + Path.DirectorySeparatorChar;
        if (upSlash.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase) && !dirs.Any(d => upSlash.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            AddAppSourceWatcher(up, recursive: false);
    }

    private void AddAppSourceWatcher(string dir, bool recursive)
    {
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = recursive,
                // DirectoryName on every watcher: a folder renamed, moved in or out inside lib/ is one event for the
                // folder and none for its files (OnAppSourceEvent)
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,   // the AI, a formatter or a git checkout writing many files at once
            };
            FileSystemEventHandler on = (_, e) => OnAppSourceEvent(w, e.FullPath, e.ChangeType, recursive, null);
            w.Changed += on;
            w.Created += on;
            w.Deleted += on;
            w.Renamed += (_, e) => OnAppSourceEvent(w, e.FullPath, e.ChangeType, recursive, e.OldFullPath);
            // (can be raised inside EnableRaisingEvents below, before the Add: the handler runs later, on the dispatcher)
            w.Error += (_, e) =>
            {
                var ex = e.GetException();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnAppSourceWatcherError(w, dir, ex));
            };
            w.EnableRaisingEvents = true;
            _libWatchers.Add(w);
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "source watcher failed", ex); }
    }

    /// <summary>A watcher failed (on the UI thread): it has stopped, and the changes it lost can't be named. Every error
    /// watches again and draws everything again — an overflow, a watched folder that went away (a git checkout), and a
    /// network share whose own notify buffer overflowed (Win32 error 1022, not an InternalBufferOverflowException).
    /// A folder whose watch keeps failing (\\wsl$, a share without change notifications) is stopped by
    /// <see cref="MaxAppResyncFailures"/>: after that it is left alone. A local overflow is not a failure: it proves the
    /// watcher works (it keeps running), and a build_runner burst of *.g.dart writes — which never reset the count —
    /// must not use up the cap and drop the resync for a lib/ save lost in the next burst (<see cref="AppResyncGap"/>
    /// already limits how often it runs).</summary>
    private void OnAppSourceWatcherError(FileSystemWatcher w, string dir, Exception? ex)
    {
        if (!_libWatchers.Contains(w)) return;   // replaced or stopped since
        bool overflow = ex is InternalBufferOverflowException;
        if (overflow) _log?.Warn("UiStudio", "source watcher overflow: drawing the screens again", ex);
        else if (!_appWatchErrorLogged)
        {
            _log?.Warn("UiStudio", $"source watcher on {dir} failed: watching again", ex);
            _appWatchErrorLogged = true;
        }
        RequestAppResync(w, failure: !overflow);
    }

    /// <summary>A watcher's event (its thread). A folder that appears where the screens are drawn from (lib/ or
    /// assets/ at the root, the .arb folder or a folder above it: made, put back by a git checkout, or renamed in) and
    /// a watched folder renamed away both watch again: a watcher follows its folder, not its name.</summary>
    private void OnAppSourceEvent(FileSystemWatcher w, string path, WatcherChangeTypes change, bool recursive, string? oldPath)
    {
        if (!recursive && Directory.Exists(path))
        {
            // only a folder that appears: lib/ itself changes (its LastWrite) with every file made in it
            if (change is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (IsAppSourceFolder(path) || oldPath != null && IsAppSourceFolder(oldPath)) RequestAppResync(w, failure: false);
                });
            return;
        }
        // a folder inside lib/ or assets/ renamed (git mv lib/screens lib/pages), moved in, moved out or deleted: one
        // event for the folder, none for the files in it. A deleted path can't be told from a file: both go on below too.
        if (recursive && change != WatcherChangeTypes.Changed)
        {
            bool folder = change != WatcherChangeTypes.Deleted && Directory.Exists(path);
            if (folder || change == WatcherChangeTypes.Deleted)
            {
                string? gone = change == WatcherChangeTypes.Deleted ? path : change == WatcherChangeTypes.Renamed ? oldPath : null;
                string? came = folder ? path : null;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = OnAppFolderEventAsync(w, gone, came));
                if (folder) return;
            }
        }
        // a file renamed (git mv, a shell mv): its old name is gone (undo all puts it back), its new one is new
        bool renamed = oldPath != null && !Directory.Exists(path);
        if (renamed) OnAppSourceChanged(oldPath!);
        OnAppSourceChanged(path, renamedIn: renamed);
    }

    /// <summary>A folder in lib/ or assets/ renamed, moved in or out, or deleted (UI thread): its files raised no events
    /// of their own. In a review round every snapshot file under the old name counts as changed (undo all puts it back),
    /// and a file under the new name whose old name the snapshot holds is new (undo all deletes it — it keeps the old
    /// file's creation time, so <see cref="NoteSourceChanged"/> would not take it for new). A folder moved in from
    /// outside the watched folder goes through NoteSourceChanged's own rule: undo all could not put its files back where
    /// they came from, so they are never deleted. <paramref name="gone"/> may be a deleted file: that one is noted by
    /// <see cref="OnAppSourceChanged"/>.</summary>
    private async Task OnAppFolderEventAsync(FileSystemWatcher w, string? gone, string? came)
    {
        if (!_libWatchers.Contains(w)) return;   // replaced or stopped since
        string root = _studio.Root;
        string rootSlash = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var l10n = _l10n;
        // what the watcher skips (generated code, hidden folders) is not counted here either
        bool Source(string p) => p.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase)
                                 && !IsNotSource(p, p[rootSlash.Length..].Replace('\\', '/'), l10n);
        string? goneSlash = gone == null ? null : gone.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool noted = false;
        if (gone != null && _session is { } s0 && !s0.Before.ContainsKey(gone))   // (in the snapshot: a file)
        {
            foreach (var p in s0.Before.Keys.Where(k => k.StartsWith(goneSlash!, StringComparison.OrdinalIgnoreCase) && Source(k)).ToList())
            {
                NoteSourceChanged(p);
                noted = true;
            }
        }
        if (came != null)
        {
            List<string> files;
            try { files = await Task.Run(() => Directory.EnumerateFiles(came, "*.*", SearchOption.AllDirectories).ToList()); }
            catch (Exception ex) { _log?.Warn("UiStudio", $"reading the folder {came} failed", ex); files = new List<string>(); }
            if (!_libWatchers.Contains(w) || !SamePath(_studio.Root, root)) return;   // another project, or stopped since
            string cameSlash = came.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var f in files)
            {
                if (!Source(f)) continue;
                if (gone != null && _session is { FullSnapshot: true } s && !s.Before.ContainsKey(f) && !s.Unread.Contains(f)
                    && IsSnapshotKind(f) && f.StartsWith(cameSlash, StringComparison.OrdinalIgnoreCase)
                    && s.Before.ContainsKey(goneSlash + f[cameSlash.Length..]))
                    s.Before[f] = null;
                NoteSourceChanged(f);
                noted = true;
            }
        }
        // a file deleted (OnAppSourceChanged has it), or a folder the round never read and nothing in it
        if (!noted && came == null) return;
        _appResyncFailures = 0;   // a file event came through: the watchers work
        _appResyncGaveUp = false;
        _sourcesChanged = true;
        _renderDebounce ??= NewRenderDebounce();
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    /// <summary>Is <paramref name="dir"/> a folder the watchers are placed by: lib/ or assets/ at the root, the
    /// arb-dir, or a folder the arb-dir lies in (UI thread).</summary>
    private bool IsAppSourceFolder(string dir)
    {
        string root = _studio.Root;
        if (string.IsNullOrEmpty(root)) return false;
        string d = dir.TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(d);
        if ((name.Equals("lib", StringComparison.OrdinalIgnoreCase) || name.Equals("assets", StringComparison.OrdinalIgnoreCase))
            && Path.GetDirectoryName(d) is { } parent && SamePath(parent, root)) return true;
        return _l10n?.ArbDir is { Length: > 0 } arb
               && (SamePath(arb, d) || arb.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Watchers are re-created at most once every <see cref="AppResyncGap"/> (a later ask waits out the gap, so
    /// none is lost), and only <see cref="MaxAppResyncFailures"/> times in a row for failures with no file event seen in
    /// between: a folder whose watch keeps failing must not rebuild watchers, fill the log and hold off the redraw for good.</summary>
    private static readonly TimeSpan AppResyncGap = TimeSpan.FromSeconds(2);
    private const int MaxAppResyncFailures = 3;
    private DateTime _lastAppResync = DateTime.MinValue;
    private DispatcherTimer? _appResyncTimer;
    private int _appResyncFailures;
    private bool _appWatchErrorLogged, _appResyncGaveUp;

    private void RequestAppResync(FileSystemWatcher from, bool failure)
    {
        if (!_libWatchers.Contains(from) || _catalog?.Flutter == null) return;   // another project, or stopped since
        // one is already waiting: it covers this one too (the root, lib/ and assets/ watchers failing together are one
        // failure, not three toward the cap)
        if (_appResyncTimer?.IsEnabled == true) return;
        if (failure && ++_appResyncFailures > MaxAppResyncFailures)
        {
            if (!_appResyncGaveUp) _log?.Warn("UiStudio", "source watchers keep failing: not re-created again until a file change comes through");
            _appResyncGaveUp = true;
            return;
        }
        var wait = _lastAppResync + AppResyncGap - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero) { ResyncAppSources(); return; }
        if (_appResyncTimer == null)
        {
            var t = new DispatcherTimer();
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_libWatchers.Count > 0 && _catalog?.Flutter != null) ResyncAppSources();
            };
            _appResyncTimer = t;
        }
        _appResyncTimer.Interval = wait;
        _appResyncTimer.Start();
    }

    /// <summary>Watch the app's folders again and count everything as changed (what was missed can't be named). In a
    /// review round the files are also compared with the round's snapshot, so the ones whose events were lost still
    /// count as changed: Undo all must take back every one of them.</summary>
    private void ResyncAppSources()
    {
        _appResyncTimer?.Stop();
        _lastAppResync = DateTime.UtcNow;
        StartAppSourceWatchers();
        _sourcesChanged = true;
        _renderDebounce ??= NewRenderDebounce();
        _renderDebounce.Stop();
        _renderDebounce.Start();
        if (_session is { FullSnapshot: true } s) _ = ReconcileSessionAsync(s);
    }

    /// <summary>After lost watcher events: every snapshot file now changed or gone, and every app file the snapshot did
    /// not have, goes through <see cref="NoteSourceChanged"/> (which applies its own rules for new files). The files are
    /// read off the UI thread; the round is checked again before anything is noted.</summary>
    private async Task ReconcileSessionAsync(ChangeSession s)
    {
        string root = _studio.Root;
        var before = s.Before.ToList();
        var skip = new HashSet<string>(s.Unread, StringComparer.OrdinalIgnoreCase);
        var l10n = _l10n;
        string? arbDir = l10n?.ArbDir;
        string rootSlash = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // what the watcher skips (generated code, hidden folders) is not counted here either
        bool NotSource(string p) => p.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase)
                                    && IsNotSource(p, p[rootSlash.Length..].Replace('\\', '/'), l10n);
        List<string> touched;
        try
        {
            touched = await Task.Run(() =>
            {
                var list = new List<string>();
                foreach (var (path, text) in before)
                {
                    if (NotSource(path)) continue;
                    bool exists = File.Exists(path);
                    if (text == null) { if (exists) list.Add(path); continue; }   // made this round, still there
                    if (!exists) { list.Add(path); continue; }
                    string now;
                    try { now = CluadeX.Helpers.TextFileCodec.Read(path).Text; } catch { continue; }
                    if (!string.Equals(now, text, StringComparison.Ordinal)) list.Add(path);
                }
                var known = new HashSet<string>(before.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);
                var files = new List<string>();
                try { files.AddRange(AppFiles(root)); } catch { /* lib/ going away mid-scan: what was read is enough */ }
                try
                {
                    if (arbDir is { Length: > 0 } && Directory.Exists(arbDir)) files.AddRange(Directory.EnumerateFiles(arbDir, "*.arb"));
                }
                catch { /* the same */ }
                foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!known.Contains(f) && !skip.Contains(f) && !NotSource(f)) list.Add(f);
                return list;
            });
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "comparing the review round with its snapshot failed", ex); return; }
        if (!ReferenceEquals(_session, s) || !SamePath(_studio.Root, root)) return;   // the round ended, or another project
        foreach (var p in touched)
            if (!s.Changed.Contains(p)) NoteSourceChanged(p);
    }

    private void DisposeAppSourceWatchers()
    {
        foreach (var w in _libWatchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _libWatchers.Clear();
    }

    private void StopWatchingAppSources()
    {
        DisposeAppSourceWatchers();
        _renderDebounce?.Stop();
        _appResyncTimer?.Stop();
        _lastAppResync = DateTime.MinValue;
        _appResyncFailures = 0;
        _appWatchErrorLogged = _appResyncGaveUp = false;
    }

    /// <summary>A file under the watched folders that is not a source the round follows (any thread): generated code
    /// (*.g.dart from build_runner, what gen-l10n writes while a screen is drawn — the .arb files were the change, this
    /// is only its result) and anything in a hidden folder. The watcher and <see cref="ReconcileSessionAsync"/> both
    /// skip these, so a resync never counts them as changed.</summary>
    private static bool IsNotSource(string path, string rel, FlutterL10n? l10n)
        => rel.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) || rel.Contains("/.", StringComparison.Ordinal)
           || l10n?.IsGenerated(path) == true;

    private void OnAppSourceChanged(string path, bool renamedIn = false)
    {
        string root = _studio.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        string rel = path[root.Length..].Replace('\\', '/');
        var l10n = _l10n;
        bool translations = rel == "l10n.yaml" || l10n?.IsArb(path) == true;
        if (!(translations || rel.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || rel == "pubspec.yaml")) return;
        if (IsNotSource(path, rel, l10n)) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // renamed in during the round and not in the snapshot: a new file (undo all deletes it) — it keeps the old
            // file's creation time, so NoteSourceChanged would not take it for new
            if (renamedIn && _session is { FullSnapshot: true } s && !s.Before.ContainsKey(path) && !s.Unread.Contains(path)
                && IsSnapshotKind(path) && File.Exists(path))
                s.Before[path] = null;
            if (rel == "l10n.yaml")
            {
                string? arbDir = _l10n?.ArbDir;
                try { _l10n = FlutterL10n.Load(_studio.Root); } catch { _l10n = null; }
                // the .arb files moved to another folder: watch that one
                if (!string.Equals(arbDir, _l10n?.ArbDir, StringComparison.OrdinalIgnoreCase) && _libWatchers.Count > 0) StartAppSourceWatchers();
            }
            _appResyncFailures = 0;   // a file event came through: the watchers work
            _appResyncGaveUp = false;
            NoteSourceChanged(path);
            _sourcesChanged = true;
            _renderDebounce ??= NewRenderDebounce();
            _renderDebounce.Stop();
            _renderDebounce.Start();
        });
    }

    private bool _sourcesChanged;

    private DispatcherTimer NewRenderDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            if (!_sourcesChanged || _catalog?.Flutter == null) return;
            // nobody looks at the screens: drawing them (a flutter test run) waits for the page to be shown —
            // except inside a review round or a save check, which wait for the drawing themselves
            if (!IsOnScreen && _session == null && !IsChecking) return;
            _sourcesChanged = false;
            // the code changed: read the app again (new screens, new titles), then draw what is on screen again
            await RefreshFlutterAfterEditAsync();
        };
        return t;
    }

    /// <summary>After a save: the wireframes and the screen list follow the code, the pictures are drawn again —
    /// the stage first, then the others.</summary>
    private bool _refreshing;

    private async Task RefreshFlutterAfterEditAsync()
    {
        if (_catalog?.Flutter == null) return;
        var info = _studio.Project;
        string lang = _loc.CurrentLanguage;
        var ct = _pagesCts.Token;
        PageCatalogResult fresh;
        _refreshing = true;
        try { fresh = await Task.Run(() => PageCatalog.Discover(info, lang, ct), ct); }
        catch { _refreshing = false; return; }
        _refreshing = false;
        if (ct.IsCancellationRequested || fresh.Flutter == null) return;
        _catalog = fresh;
        _projectStamp = DateTime.UtcNow;
        RefreshSampleInfo();   // (the requests the code makes may have changed)
        // keep the cards (and the stage) — refresh what each one draws
        foreach (var c in _allCards.Where(c => c.IsScreen))
        {
            c.Sketched = false;
            c.IsStale = true;
        }
        _ = SketchAllAsync();
        var order = new List<PageCard>();
        if (_stageCard != null) order.Add(_stageCard);
        order.AddRange(_allCards.Where(c => c.IsScreen && !ReferenceEquals(c, _stageCard)));
        RenderScreens(order);
    }
}
