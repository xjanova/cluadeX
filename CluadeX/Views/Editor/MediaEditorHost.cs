using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Media;
using CluadeX.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CluadeX.Views.Editor;

/// <summary>
/// The media editor for one <see cref="MediaFileTab"/>: a WebView2 running Resources/MediaEditor/media.html (image crop /
/// resize / rotate / flip / light, audio waveform with trim / delete / fades / volume, video in-out cut and snapshots).
///
/// The file is never read here. Its folder is mapped to https://cluadex-media.example/ (SetVirtualHostNameToFolderMapping,
/// "Allow" so the page's canvas may read the pixels), and Chromium streams it with range requests — a 2 GB video opens
/// and seeks without being loaded into memory. What the page writes back comes in acked chunks into a hidden temp file
/// (<see cref="MediaUpload"/>) and is moved into place whole; an ffmpeg cut writes its own temp file the same way.
/// A copy never takes an existing name; replacing the original asks first, every time (view model).
///
/// One host lives per open media tab, so switching tabs keeps each page's edits; a hidden one is paused. The control
/// moves with the editor view when the page pops out into its own window and back (Views/PopOut) — the WPF WebView2
/// re-parents its window, nothing restarts. <see cref="Close"/> (the tab closed, the view gone) stops any running
/// save or cut, removes its unfinished file and disposes the browser.
/// </summary>
public sealed class MediaEditorHost : Border, IMediaEditorSession
{
    public const string HostName = "cluadex-media.example";

    public MediaFileTab Tab { get; }
    private readonly CodeEditorViewModel _vm;
    private WebView2? _web;
    private bool _ready, _closed, _hooked;
    private int _restarts;
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Envs = new(StringComparer.OrdinalIgnoreCase);
    private ThemeService? _themes;
    private LocalizationService? _loc;
    private (long Length, DateTime Written) _shownStamp;

    // a save in flight: one at a time (a double-click on Save writes once)
    private MediaUpload? _upload;
    private int _uploadId;
    private string _uploadMode = "copy", _uploadPurpose = "edit";
    private string? _uploadTarget;
    private CancellationTokenSource? _work;
    private bool _cutRunning;
    // a save is being set up: the overwrite question (a modal loop that still delivers page messages) or the temp file
    // being made — a second Save arriving then is refused, not started alongside
    private bool _claiming;
    private bool Occupied => _upload != null || _burn != null || _claiming || Tab.IsBusy;
    private readonly Dictionary<int, TaskCompletionSource<bool>> _exports = new();
    private string? _exportPath;
    private TaskCompletionSource<bool>? _released;
    private int _nextId = 1_000_000;   // ids the host makes (export, release) never meet the page's own (from 1)

    /// <summary>The page is up and was told what to show (for the harness).</summary>
    public bool IsReady => _ready;
    /// <summary>What the page reported after loading the file ("w×h", a duration), or its load error (for the harness).</summary>
    public string? PageState { get; private set; }
    /// <summary>No WebView2 runtime, or the browser could not start: a plain message is shown instead.</summary>
    public bool ShowsMessage { get; private set; }
    /// <summary>The page reported a load error (the harness waits for it).</summary>
    public event Action<MediaEditorHost>? StateChanged;

    public MediaEditorHost(MediaFileTab tab, CodeEditorViewModel vm)
    {
        Tab = tab;
        _vm = vm;
        Focusable = false;
        SetResourceReference(BackgroundProperty, "Bg.Window");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        tab.Session = this;
        Start();
    }

    // ───────────────────────────── life ─────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked || _closed) return;   // Loaded again without an Unloaded: a re-parent (pop-out), already wired
        _hooked = true;
        _themes = ThemeService.Current;
        if (_themes != null) _themes.ThemeChanged += OnThemeChanged;
        _loc = _vm.Localization;
        _loc.LanguageChanged += OnLanguageChanged;
        SendTheme();
        SendStrings();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unhook();
        Pause();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        if (_themes != null) _themes.ThemeChanged -= OnThemeChanged;
        _themes = null;
        if (_loc != null) _loc.LanguageChanged -= OnLanguageChanged;
        _loc = null;
    }

    private void OnThemeChanged(string _) => Dispatcher.BeginInvoke(SendTheme);
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() => { SendStrings(); _ = SendFontsAsync(); });   // (the default font follows the language)

    private async void Start()
    {
        try
        {
            try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex)
            {
                _vm.LogMedia("WebView2 runtime not available for the media editor", ex);
                ShowMessage("media.noWebView2.title", "media.noWebView2.body");
                return;
            }
            var web = new WebView2();
            if (TryColor("Bg.Window") is { } bg) web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
            _web = web;
            Child = web;
            string folder = _vm.MediaWebDataFolder;
            Directory.CreateDirectory(folder);
            Task<CoreWebView2Environment> envTask;
            lock (Envs)
                if (!Envs.TryGetValue(folder, out envTask!))
                    Envs[folder] = envTask = CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions());
            var env = await envTask;
            await web.EnsureCoreWebView2Async(env);
            if (_closed || !ReferenceEquals(web, _web)) return;
            var core = web.CoreWebView2;
            var s = core.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;   // Ctrl+S / Ctrl+W reach the editor page (WPF), not the browser
            s.IsSwipeNavigationEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
            s.IsWebMessageEnabled = true;
            web.AllowExternalDrop = false;   // a file dropped on the page would navigate away from it
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += OnProcessFailed;
            // only our page: a link, a drop or a script never takes the tab anywhere else
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("data:", StringComparison.Ordinal) && a.Uri != "about:blank") a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.DownloadStarting += (_, a) => a.Cancel = true;
            core.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;
            MapFolder(core);
            web.NavigateToString(Page());
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _vm.LogMedia($"Media editor failed to start for {Tab.FullPath}", ex);
            ShowMessage("media.noWebView2.title", "media.engineFailed.body");
        }
    }

    /// <summary>The file's folder, as the page's only source. A tab is one file, so one folder for its life.</summary>
    private void MapFolder(CoreWebView2 core)
    {
        string dir = Path.GetDirectoryName(Tab.FullPath) ?? ".";
        core.SetVirtualHostNameToFolderMapping(HostName, dir, CoreWebView2HostResourceAccessKind.Allow);
    }

    private static string? _page;
    private static string Page()
    {
        if (_page != null) return _page;
        using var s = typeof(MediaEditorHost).Assembly.GetManifestResourceStream("MediaEditor/media.html")
                      ?? throw new FileNotFoundException("MediaEditor/media.html");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _page = r.ReadToEnd();
    }

    private void ShowMessage(string titleKey, string bodyKey)
    {
        ShowsMessage = true;
        DisposeWeb();
        var title = new TextBlock { Text = _vm.Loc(titleKey), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var body = new TextBlock { Text = _vm.Loc(bodyKey), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        Child = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), Children = { title, body } };
        StateChanged?.Invoke(this);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = e.ProcessFailedKind;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _vm.LogMedia($"Media editor page for {Tab.FileName}: {kind} ({e.Reason})");
            if (kind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.BrowserProcessExited)) return;
            // The page and its edits are gone: say nothing is pending any more, stop what it was saving, start again.
            AbortWork();
            Tab.IsDirty = false;
            _ready = false;
            DisposeWeb();
            if (++_restarts > 3) { ShowMessage("media.noWebView2.title", "media.engineFailed.body"); return; }
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited) lock (Envs) Envs.Remove(_vm.MediaWebDataFolder);
            Start();
        });
    }

    /// <summary>The tab is gone (or the view): stop playback and any save, remove an unfinished file, free the browser.
    /// A page that still had edits takes them with it, so the tab (if it stays) is no longer dirty.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        AbortWork();
        foreach (var t in _exports.Values) t.TrySetResult(false);
        _exports.Clear();
        _released?.TrySetResult(false);
        Unhook();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DisposeWeb();
        Child = null;
        if (ReferenceEquals(Tab.Session, this)) Tab.Session = null;
        Tab.IsBusy = false;
        Tab.IsDirty = false;
    }

    private void DisposeWeb()
    {
        _ready = false;
        var web = _web;
        _web = null;
        if (web == null) return;
        try
        {
            if (web.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnMessage;
                core.ProcessFailed -= OnProcessFailed;
            }
        }
        catch { /* its browser process is already gone */ }
        try { web.Dispose(); } catch { }
        if (ReferenceEquals(Child, web)) Child = null;
    }

    /// <summary>Shown as the active tab: take the keyboard (Space plays, Ctrl+Z undoes in the page).</summary>
    public void Activate()
    {
        try { _web?.Focus(); } catch { }
    }

    // ───────────────────────────── IMediaEditorSession ─────────────────────────────

    public void Pause() => Post(new { t = "pause" });

    public void CancelWork() => AbortWork();

    public void ReloadFromDisk()
    {
        if (!_ready || Tab.IsBusy) return;
        var stamp = Stamp();
        if (stamp == _shownStamp) return;   // our own save, already shown
        if (stamp.Length < 0) { SendGone(); return; }
        SendInit(reload: true);
    }

    public Task<bool> SaveCopyAsync() => Export(null);

    public Task<bool> SaveToAsync(string path) => Export(path);

    private Task<bool> Export(string? path)
    {
        if (!_ready || _closed) return Task.FromResult(false);
        int id = _nextId++;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exports[id] = tcs;
        _exportPath = path;   // the page may only write where the host was asked to (its own "path" is ignored)
        Post(new { t = "export", id, path = path == null ? null : "host", ext = path == null ? null : MediaFiles.ExtOf(path) });
        return tcs.Task;
    }

    // ───────────────────────────── from the page ─────────────────────────────

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }
        using var _doc = doc;   // pooled buffers back once handled (a 2 GB save is ~2,700 chunk messages)
        JsonElement m = doc.RootElement;
        // Out of the browser's event before anything that can show a dialog (the overwrite question): a modal loop
        // inside a WebView2 handler is re-entrancy it does not support. Continuations keep their order.
        await Task.Yield();
        if (_closed) return;
        try
        {
            string t = Str(m, "t") ?? "";
            switch (t)
            {
                case "ready":
                    _ready = true;
                    SendTheme();
                    SendStrings();
                    SendInit(reload: false);
                    _ = SendFontsAsync();
                    break;
                case "dirty":
                    Tab.IsDirty = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                    break;
                case "loaded":
                    PageState = "loaded " + m.GetRawText();
                    StateChanged?.Invoke(this);
                    break;
                case "loadError":
                    PageState = "error " + (Str(m, "why") ?? "");
                    _vm.LogMedia($"Media editor could not load {Tab.FullPath}: {Str(m, "why")}");
                    if (!File.Exists(Tab.FullPath)) SendGone();
                    StateChanged?.Invoke(this);
                    break;
                case "begin": await BeginUploadAsync(m); break;
                case "chunk": await ChunkAsync(m); break;
                case "end": await EndUploadAsync(m); break;
                case "abort": if (Int(m, "id") == _uploadId && !_cutRunning) EndWork(); break;
                case "cut": await CutAsync(m); break;
                case "burn": await BurnBeginAsync(m); break;
                case "burnChunk": await BurnChunkAsync(m); break;
                case "burnEnd": await BurnEndAsync(m); break;
                case "cancel":
                    _work?.Cancel();
                    if (_burn != null && !_cutRunning && Int(m, "id") == _uploadId) AbortWork();   // still sending pictures
                    break;
                case "exported":
                    if (_exports.Remove(Int(m, "id"), out var tcs)) tcs.TrySetResult(m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
                    _exportPath = null;
                    break;
                case "released": _released?.TrySetResult(true); break;
                case "openAsText": await _vm.OpenMediaAsTextAsync(Tab); break;
                case "log": _vm.LogMedia("page: " + (Str(m, "text") ?? "")); break;
            }
        }
        catch (Exception ex)
        {
            _vm.LogMedia($"Media editor message failed for {Tab.FullPath}", ex);
            if (_uploadId != 0) Result(_uploadId, ok: false, textKey: "media.status.saveFailed");
            EndWork();
        }
    }

    /// <summary>Where a save of <paramref name="ext"/> goes: null = a new copy (named at commit time), else the path to
    /// replace. Null with <paramref name="refuse"/> set = not allowed (localized key; "" = the user said no).</summary>
    private string? PlanTarget(string mode, string ext, out string? refuse)
    {
        refuse = null;
        switch (mode)
        {
            case "copy":
                return null;
            case "overwrite":
                if (!MediaFiles.SameFormat(Tab.FullPath, ext)) { refuse = "media.status.sameFormatOnly"; return null; }
                if (!_vm.ConfirmMediaOverwrite(Tab)) { refuse = ""; return null; }
                return Tab.FullPath;
            case "path":
                if (_exportPath == null || !Path.IsPathFullyQualified(_exportPath)) { refuse = "media.status.saveFailed"; return null; }
                return _exportPath;   // Save As: its dialog asked before replacing anything
            default:
                refuse = "media.status.saveFailed";
                return null;
        }
    }

    private static string SuffixFor(string purpose) => purpose switch { "frame" => "frame", "cut" => "cut", _ => "edit" };

    private async Task BeginUploadAsync(JsonElement m)
    {
        int id = Int(m, "id");
        if (Occupied) { Result(id, ok: false, textKey: "media.status.busy"); return; }
        string? ext = MediaFiles.SafeExt(Str(m, "ext"));
        long size = m.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : -1;
        if (ext == null || size < 0) { Result(id, ok: false, textKey: "media.status.saveFailed"); return; }
        if (size > MediaFiles.MaxUploadBytes) { Result(id, ok: false, textKey: "media.status.tooLarge"); return; }
        string mode = Str(m, "mode") ?? "copy";
        MediaUpload up;
        string? target;
        _claiming = true;
        try
        {
            target = PlanTarget(mode, ext, out string? refuse);
            if (refuse != null) { Result(id, ok: false, textKey: refuse.Length == 0 ? null : refuse, cancelled: refuse.Length == 0); return; }
            if (_closed) return;
            up = await Task.Run(() => new MediaUpload(target ?? Tab.FullPath, ext, size));
        }
        catch (Exception ex)
        {
            _vm.LogMedia($"Could not start saving {Tab.FullPath}", ex);
            Result(id, ok: false, textKey: "media.status.saveFailed");
            return;
        }
        finally { _claiming = false; }
        if (_closed) { up.Dispose(); return; }   // the tab closed while the temp file was being made
        _upload = up;
        _uploadId = id;
        _uploadMode = mode;
        _uploadTarget = target;
        _uploadPurpose = Str(m, "purpose") ?? "edit";
        _work = new CancellationTokenSource();
        Tab.IsBusy = true;
        Post(new { t = "go", id });
    }

    private async Task ChunkAsync(JsonElement m)
    {
        int id = Int(m, "id");
        var up = _upload;
        if (up == null || id != _uploadId) { Result(id, ok: false, textKey: "media.status.saveCancelled", cancelled: true); return; }
        await up.AppendBase64Async(Int(m, "seq"), Str(m, "data") ?? "", _work?.Token ?? default);
        if (_closed || !ReferenceEquals(up, _upload)) return;
        Post(new { t = "ack", id, seq = Int(m, "seq") });
    }

    private async Task EndUploadAsync(JsonElement m)
    {
        int id = Int(m, "id");
        var up = _upload;
        if (up == null || id != _uploadId) return;
        if (!await up.FinishAsync()) { Result(id, ok: false, textKey: "media.status.saveFailed"); EndWork(); return; }
        if (_closed || !ReferenceEquals(up, _upload)) return;
        await CommitAsync(id, up, _uploadTarget, _uploadPurpose);
    }

    /// <summary>The finished temp file takes its place: a new copy name, or (asked already) the original / the Save As path.</summary>
    private async Task CommitAsync(int id, MediaUpload up, string? target, string purpose)
    {
        string final;
        bool overwrote = false;
        try
        {
            if (target == null)
            {
                string original = Tab.FullPath, ext = up.Ext, suffix = SuffixFor(purpose);
                final = await Task.Run(() => up.CommitAsCopy(() => MediaFiles.CopyPathFor(original, ext, suffix)));
            }
            else
            {
                overwrote = string.Equals(Path.GetFullPath(target), Path.GetFullPath(Tab.FullPath), StringComparison.OrdinalIgnoreCase);
                if (overwrote) await ReleaseAsync();   // the page lets go of the file it streams
                await ReplaceWithRetryAsync(up, target);
                final = target;
            }
        }
        catch (Exception ex)
        {
            _vm.LogMedia($"Saving {Tab.FullPath} failed at the last step", ex);
            Result(id, ok: false, textKey: "media.status.saveFailed");
            if (overwrote) Post(new { t = "reattach" });
            EndWork();
            return;
        }
        EndWork();
        if (_closed) return;
        Post(new { t = "saveResult", id, ok = true, name = Path.GetFileName(final) });
        _vm.LogMedia($"Saved {final} ({MediaFiles.SizeText(SafeLength(final))}) from {Tab.FileName}");
        _vm.MediaSaved(Tab, final, overwrote, openCopy: purpose != "frame");
        if (overwrote)
        {
            Tab.ByteLength = SafeLength(final);
            SendInit(reload: true);   // show what is on disk now (the page's edits are in it)
        }
    }

    private static async Task ReplaceWithRetryAsync(MediaUpload up, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await Task.Run(() => up.CommitOverwrite(target)); return; }
            catch (IOException) when (attempt < 8) { await Task.Delay(250); }   // the player may hold it a moment longer
            catch (UnauthorizedAccessException) when (attempt < 8) { await Task.Delay(250); }
        }
    }

    private async Task ReleaseAsync()
    {
        if (!_ready) return;
        int id = _nextId++;
        _released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { t = "release", id });
        await Task.WhenAny(_released.Task, Task.Delay(3000));
        _released = null;
        await Task.Delay(150);   // the media pipeline closes its handle just after the element lets go
    }

    /// <summary>ffmpeg cuts [start, end) of the original into its own format, losslessly (-c copy), into a temp file
    /// that is then committed like an upload. Cancellable from the page and by closing the tab.</summary>
    private async Task CutAsync(JsonElement m)
    {
        int id = Int(m, "id");
        if (Occupied) { Result(id, ok: false, textKey: "media.status.busy"); return; }
        if (Ffmpeg.Locate() == null)
        {
            Post(new { t = "ffmpeg", on = false });
            Result(id, ok: false, textKey: "media.status.ffmpegMissing");
            return;
        }
        double start = Dbl(m, "start"), end = Dbl(m, "end");
        if (!(end > start) || start < 0) { Result(id, ok: false, textKey: "media.status.saveFailed"); return; }
        string ext = MediaFiles.ExtOf(Tab.FullPath);
        string mode = Str(m, "mode") ?? "copy";
        string? target;
        _claiming = true;
        try
        {
            target = PlanTarget(mode, ext, out string? refuse);
            if (refuse != null) { Result(id, ok: false, textKey: refuse.Length == 0 ? null : refuse, cancelled: refuse.Length == 0); return; }
        }
        finally { _claiming = false; }
        if (_closed) return;

        var up = new MediaUpload(target ?? Tab.FullPath, ext, 0, openStream: false);
        _upload = up;
        _uploadId = id;
        _work = new CancellationTokenSource();
        Tab.IsBusy = true;
        var ct = _work.Token;
        string input = Tab.FullPath;
        FfmpegResult result;
        _cutRunning = true;
        try { result = await Task.Run(() => Ffmpeg.CutAsync(input, up.TempPath, start, end, Ffmpeg.TimeoutFor(Tab.ByteLength), ct)); }
        finally { _cutRunning = false; }
        await FinishFfmpegAsync(id, up, target, result, "cut", "cut");
    }

    /// <summary>An ffmpeg run is over: commit its file, or say why not. The temp files go either way.</summary>
    private async Task FinishFfmpegAsync(int id, MediaUpload up, string? target, FfmpegResult result, string purpose, string what)
    {
        if (_closed || !ReferenceEquals(up, _upload))
        {
            if (ReferenceEquals(up, _upload)) EndWork(); else up.Dispose();
            return;
        }
        switch (result.Outcome)
        {
            case FfmpegOutcome.Done:
                await CommitAsync(id, up, target, purpose);
                return;
            case FfmpegOutcome.Cancelled:
                Result(id, ok: false, textKey: null, cancelled: true);
                break;
            case FfmpegOutcome.TimedOut:
                _vm.LogMedia($"ffmpeg {what} of {Tab.FullPath} timed out: {result.Detail}");
                Result(id, ok: false, textKey: "media.status.ffmpegTimeout");
                break;
            case FfmpegOutcome.Missing:
                Result(id, ok: false, textKey: "media.status.ffmpegMissing");
                break;
            default:
                _vm.LogMedia($"ffmpeg {what} of {Tab.FullPath} failed: {result.Detail}");
                Result(id, ok: false, textKey: "media.status.ffmpegFailed");
                break;
        }
        EndWork();
    }

    // ───────────────────────────── text burned into a video ─────────────────────────────

    /// <summary>A text burn in flight: the layers' pictures arriving (one temp PNG each, in a folder of its own under
    /// %TEMP%), then ffmpeg laying them over the frames.</summary>
    private sealed class Burn
    {
        public required string Dir { get; init; }
        public required MediaUpload[] Parts { get; init; }
        public required double[] From { get; init; }
        public required double[] To { get; init; }
        public double Start { get; init; }
        public double End { get; init; }
        public long Pixels { get; init; }
        public string? Target { get; init; }

        /// <summary>The pictures and their folder go (best effort: an antivirus may hold one a moment longer).</summary>
        public void Cleanup()
        {
            foreach (var p in Parts) { try { p.Dispose(); } catch { } }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private Burn? _burn;

    /// <summary>"burn": the page will send <c>overlays.length</c> PNGs, then "burnEnd". Checked here: ffmpeg is there, the
    /// times are numbers and inside the clip, the counts and sizes are sane, an overwrite was confirmed.</summary>
    private async Task BurnBeginAsync(JsonElement m)
    {
        int id = Int(m, "id");
        if (Occupied) { Result(id, ok: false, textKey: "media.status.busy"); return; }
        if (Ffmpeg.Locate() == null)
        {
            Post(new { t = "ffmpeg", on = false });
            Result(id, ok: false, textKey: "media.status.ffmpegMissing");
            return;
        }
        double start = Dbl(m, "start"), end = Dbl(m, "end");
        int w = Int(m, "w"), h = Int(m, "h");
        var spans = new List<(long Size, double From, double To)>();
        if (m.TryGetProperty("overlays", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var o in list.EnumerateArray())
                spans.Add((o.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : -1, Dbl(o, "t0"), Dbl(o, "t1")));
        bool sane = double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start
                    && w is > 0 and <= 16384 && h is > 0 and <= 16384
                    && spans.Count is > 0 and <= MediaOverlay.MaxLayers
                    && spans.All(s => s.Size is > 0 and <= MediaOverlay.MaxPngBytes && double.IsFinite(s.From) && double.IsFinite(s.To));
        if (!sane) { Result(id, ok: false, textKey: "media.status.saveFailed"); return; }

        string ext = MediaFiles.ExtOf(Tab.FullPath);
        string mode = Str(m, "mode") ?? "copy";
        string? target;
        Burn burn;
        _claiming = true;
        try
        {
            target = PlanTarget(mode, ext, out string? refuse);
            if (refuse != null) { Result(id, ok: false, textKey: refuse.Length == 0 ? null : refuse, cancelled: refuse.Length == 0); return; }
            if (_closed) return;
            string dir = Path.Combine(Path.GetTempPath(), "cluadex-media", Guid.NewGuid().ToString("N"));
            burn = await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                try
                {
                    return new Burn
                    {
                        Dir = dir,
                        Parts = spans.Select((s, i) => new MediaUpload(Path.Combine(dir, $"text{i}.png"), "png", s.Size)).ToArray(),
                        From = spans.Select(s => s.From).ToArray(),
                        To = spans.Select(s => s.To).ToArray(),
                        Start = start, End = end, Pixels = (long)w * h, Target = target,
                    };
                }
                catch { try { Directory.Delete(dir, true); } catch { } throw; }
            });
        }
        catch (Exception ex)
        {
            _vm.LogMedia($"Could not start the text render for {Tab.FullPath}", ex);
            Result(id, ok: false, textKey: "media.status.saveFailed");
            return;
        }
        finally { _claiming = false; }
        if (_closed) { _ = Task.Run(burn.Cleanup); return; }
        _burn = burn;
        _uploadId = id;
        _work = new CancellationTokenSource();
        Tab.IsBusy = true;
        Post(new { t = "go", id });
    }

    private async Task BurnChunkAsync(JsonElement m)
    {
        int id = Int(m, "id"), n = Int(m, "n"), seq = Int(m, "seq");
        var burn = _burn;
        if (burn == null || id != _uploadId || n < 0 || n >= burn.Parts.Length) { Result(id, ok: false, textKey: "media.status.saveCancelled", cancelled: true); return; }
        await burn.Parts[n].AppendBase64Async(seq, Str(m, "data") ?? "", _work?.Token ?? default);
        if (_closed || !ReferenceEquals(burn, _burn)) return;
        Post(new { t = "ack", id, n, seq });
    }

    /// <summary>"burnEnd": every picture is here — ffmpeg cuts [start, end), lays them over their windows and re-encodes
    /// the picture (the sound is copied), into a temp file committed like any save. Progress goes to the page.</summary>
    private async Task BurnEndAsync(JsonElement m)
    {
        int id = Int(m, "id");
        var burn = _burn;
        if (burn == null || id != _uploadId) return;
        foreach (var part in burn.Parts)
            if (!await part.FinishAsync()) { Result(id, ok: false, textKey: "media.status.saveFailed"); EndWork(); return; }
        if (_closed || !ReferenceEquals(burn, _burn)) return;
        string? exe = Ffmpeg.Locate();
        if (exe == null) { Result(id, ok: false, textKey: "media.status.ffmpegMissing"); EndWork(); return; }

        string ext = MediaFiles.ExtOf(Tab.FullPath), input = Tab.FullPath;
        var up = new MediaUpload(burn.Target ?? Tab.FullPath, ext, 0, openStream: false);
        _upload = up;
        var spans = burn.Parts.Select((p, i) => new OverlaySpan(p.TempPath, burn.From[i], burn.To[i])).ToList();
        double length = burn.End - burn.Start;
        var timeout = MediaOverlay.TimeoutForEncode(length, burn.Pixels);
        var ct = _work?.Token ?? CancellationToken.None;

        // progress: ffmpeg's "-progress" lines on a pool thread → at most a few posts a second to the page
        double shown = -1;
        long lastTick = 0;
        void OnLine(string line)
        {
            if (MediaOverlay.ProgressOf(line, length) is not { } f) return;
            long now = Environment.TickCount64;
            if (f < 1 && (f - shown < 0.01 || now - lastTick < 250)) return;
            shown = f; lastTick = now;
            Dispatcher.BeginInvoke(() => { if (ReferenceEquals(burn, _burn)) Post(new { t = "progress", id, f }); });
        }

        FfmpegResult result = new(FfmpegOutcome.Failed, "not run");
        _cutRunning = true;
        try
        {
            for (int attempt = 0; attempt < MediaOverlay.EncoderAttempts(ext); attempt++)
            {
                var args = MediaOverlay.BurnArguments(input, spans, up.TempPath, burn.Start, burn.End, burn.Pixels, attempt);
                result = await Task.Run(() => Ffmpeg.RunAsync(exe, args, up.TempPath, timeout, ct, OnLine));
                if (result.Outcome != FfmpegOutcome.Failed || !MediaOverlay.IsMissingEncoder(result.Detail)) break;
                _vm.LogMedia($"ffmpeg has no first-choice encoder for .{ext}; trying the next: {result.Detail}");
            }
        }
        finally { _cutRunning = false; }
        await FinishFfmpegAsync(id, up, burn.Target, result, "edit", "text render");
    }

    /// <summary>Tell the page how its save ended (and the status line, when it did not happen).</summary>
    private void Result(int id, bool ok, string? textKey, bool cancelled = false)
    {
        if (_closed) return;
        string? text = textKey == null ? null : _vm.Loc(textKey, Tab.FileName);
        Post(new { t = "saveResult", id, ok, cancelled, text });
        if (!ok)
        {
            if (cancelled) _vm.MediaSaveFailed(Tab, "media.status.saveCancelled");
            else if (textKey != null) _vm.MediaSaveFailed(Tab, textKey);
        }
    }

    /// <summary>The save in flight is over (done, failed or stopped): its temp file goes unless it was committed.</summary>
    private void EndWork()
    {
        var up = _upload;
        _upload = null;
        _uploadId = 0;
        _uploadTarget = null;
        try { _work?.Dispose(); } catch { }
        _work = null;
        if (up != null) _ = Task.Run(up.Dispose);
        var burn = _burn;
        _burn = null;
        if (burn != null) _ = Task.Run(burn.Cleanup);
        Tab.IsBusy = false;
    }

    /// <summary>Stop the save in flight. An ffmpeg run ends by itself once cancelled (it deletes its output, and
    /// <see cref="CutAsync"/> tidies up); an upload from the page stops here and its temp file goes.</summary>
    private void AbortWork()
    {
        try { _work?.Cancel(); } catch { }
        if (_upload == null && _burn == null || _cutRunning) return;
        int id = _uploadId;
        EndWork();
        if (id != 0) Result(id, ok: false, textKey: null, cancelled: true);
    }

    // ───────────────────────────── to the page ─────────────────────────────

    private void Post(object message)
    {
        if (!_ready || _web == null || _closed) return;
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) { _vm.LogMedia("media editor post failed: " + ex.Message); }
    }

    private (long Length, DateTime Written) Stamp()
    {
        try
        {
            var fi = new FileInfo(Tab.FullPath);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1, DateTime.MinValue);
        }
        catch { return (-1, DateTime.MinValue); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private void SendInit(bool reload)
    {
        var stamp = Stamp();
        _shownStamp = stamp;
        if (stamp.Item1 < 0) { SendGone(); return; }
        Tab.ByteLength = stamp.Item1;
        string url = $"https://{HostName}/{Uri.EscapeDataString(Tab.FileName)}?v={stamp.Item2.Ticks}";
        string kind = Tab.Kind switch { MediaKind.Image => "image", MediaKind.Audio => "audio", _ => "video" };
        Post(new
        {
            t = reload ? "reload" : "init",
            kind, url,
            name = Tab.FileName,
            ext = MediaFiles.ExtOf(Tab.FullPath),
            size = stamp.Item1,
            viewOnly = MediaFiles.IsViewOnly(Tab.FullPath),
            ffmpeg = Ffmpeg.Locate() != null,
            maxAudioBytes = MediaFiles.MaxAudioEditBytes,
            maxAudioSeconds = MediaFiles.MaxAudioEditSeconds,
        });
    }

    /// <summary>The fonts on this PC for the text layers (the page cannot list them), and the one a new layer starts
    /// with: a font with Thai letters when the app speaks Thai.</summary>
    private async Task SendFontsAsync()
    {
        IReadOnlyList<string> list;
        try { list = await MediaFonts.InstalledAsync(); }
        catch (Exception ex) { _vm.LogMedia("Could not list the installed fonts", ex); list = Array.Empty<string>(); }
        if (!_ready || _closed) return;
        Post(new { t = "fonts", list, def = MediaFonts.DefaultFont(list, _vm.Localization.CurrentLanguage) });
    }

    private void SendGone() => Post(new { t = "fail", title = _vm.Loc("media.gone.title"), body = _vm.Loc("media.gone.body", Tab.FileName) });

    private void SendStrings()
    {
        if (!_ready) return;
        var s = new Dictionary<string, string>();
        foreach (var key in CluadeX.Services.Localization.MediaKeys.Keys.Keys)
            if (key.StartsWith("media.page.", StringComparison.Ordinal)) s[key] = _vm.Loc(key);
        Post(new { t = "strings", s });
    }

    private void SendTheme()
    {
        if (!_ready) return;
        string C(string key, string fallback) => TryColor(key) is { } c ? Hex(c) : fallback;
        var bg = TryColor("Bg.Window") ?? TryColor("Surface.Sunken") ?? Color.FromRgb(11, 15, 23);
        bool light = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255 > 0.5;
        var vars = new Dictionary<string, string>
        {
            ["bg"] = Hex(bg),
            ["panel"] = C("Bg.Panel", "#11151E"),
            ["raised"] = C("Surface.Raised", "#161B26"),
            ["border"] = C("Border.Subtle", "#2A3142"),
            ["text"] = C("Text.Primary", "#E8ECF4"),
            ["muted"] = C("Text.Muted", "#8A93A6"),
            ["faint"] = C("Text.Faint", "#6B7385"),
            ["accent"] = C("Accent.1", "#4CDFFF"),
            ["accent2"] = C("Accent.2", "#A672FF"),
            ["danger"] = C("Status.Danger", "#FF6E6E"),
            ["ok"] = C("Status.Success", "#5CFFB0"),
            ["warn"] = C("Status.Warning", "#FFD166"),
            ["hover"] = light ? "rgba(0,0,0,.06)" : "rgba(255,255,255,.06)",
            ["checkA"] = light ? "#FFFFFF" : "#1A1F2B",
            ["checkB"] = light ? "#E3E5EA" : "#232A38",
        };
        Post(new { t = "theme", vars, light });
        if (_web != null) _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
    }

    private Color? TryColor(string key) => (TryFindResource(key) ?? Application.Current?.TryFindResource(key)) switch
    {
        SolidColorBrush b => b.Color,
        GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
        Color c => c,
        _ => null,
    };

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string? Str(JsonElement m, string name) => m.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static int Int(JsonElement m, string name) => m.TryGetProperty(name, out var p) && p.TryGetInt32(out int v) ? v : 0;
    private static double Dbl(JsonElement m, string name) => m.TryGetProperty(name, out var p) && p.TryGetDouble(out double v) ? v : double.NaN;
}
