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
    private FileSystemWatcher? _libWatcher;
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
        try
        {
            var w = new FileSystemWatcher(_studio.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler on = (_, e) => OnAppSourceChanged(e.FullPath);
            w.Changed += on;
            w.Created += on;
            w.Deleted += on;
            w.Renamed += (_, e) => OnAppSourceChanged(e.FullPath);
            w.EnableRaisingEvents = true;
            _libWatcher = w;
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "source watcher failed", ex); }
    }

    private void StopWatchingAppSources()
    {
        if (_libWatcher != null) { _libWatcher.EnableRaisingEvents = false; _libWatcher.Dispose(); _libWatcher = null; }
        _renderDebounce?.Stop();
    }

    private void OnAppSourceChanged(string path)
    {
        string root = _studio.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        string rel = path[root.Length..].Replace('\\', '/');
        var l10n = _l10n;
        bool translations = rel == "l10n.yaml" || l10n?.IsArb(path) == true;
        if (!(translations || rel.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || rel == "pubspec.yaml")) return;
        if (rel.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) || rel.Contains("/.", StringComparison.Ordinal)) return;
        // what gen-l10n writes while a screen is drawn: the .arb files were the change, this is only its result
        if (l10n?.IsGenerated(path) == true) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (rel == "l10n.yaml")
            {
                try { _l10n = FlutterL10n.Load(_studio.Root); } catch { _l10n = null; }
            }
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
