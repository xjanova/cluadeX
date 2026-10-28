using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Browser;

/// <summary>Basic facts about the page (from the isolated helper).</summary>
public sealed record PageInfo(string Url, string Title, double Width, double Height, double Dpr, double ScrollX, double ScrollY,
    double ScrollWidth, double ScrollHeight, string ReadyState, bool HasFocus);

/// <summary>What a navigation did.</summary>
public sealed class NavigationResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Url { get; init; } = "";
    public int? Status { get; init; }
    public string Title { get; init; } = "";
    public long ElapsedMs { get; init; }
    public bool StillLoading { get; init; }
    /// <summary>The off-screen page had no animation frames after loading and visibility was re-asserted.</summary>
    public bool FrameLoopKicked { get; init; }
}

/// <summary>The outcome of browser_eval.</summary>
public sealed record EvalResult(bool Ok, string Text, bool TimedOut = false, int? Line = null);

/// <summary>Where a screenshot came from — and so how image pixels map back to CSS pixels.</summary>
public sealed record ShotFrame(double ClipX, double ClipY, double ClipW, double ClipH, int ImageW, int ImageH, double Dpr,
    double ScrollX, double ScrollY, string Mode)
{
    public double PxPerCssX => ImageW / Math.Max(1, ClipW);
    public double PxPerCssY => ImageH / Math.Max(1, ClipH);
}

/// <summary>A captured screenshot with its digest.</summary>
public sealed class ShotResult
{
    public required byte[] Jpeg { get; init; }
    public required ShotFrame Frame { get; init; }
    public required string Digest { get; init; }
    public required PixelFrame Pixels { get; init; }
}

/// <summary>
/// One page in the built-in browser — engine-agnostic (it only talks to an <see cref="ICdpChannel"/>). Holds the
/// console and network buffers, the isolated-world helper, the last screenshot's coordinate frame, the
/// viewport emulation and the things the engine did on its own (dialogs, blocked pop-ups). Input that takes
/// several events (move, press, hold, release) is serialized per session, so two tools can never interleave.
/// </summary>
public sealed class BrowserSession : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private const string EvalGroup = "cluadex-eval";
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ICdpChannel _channel;
    // Never disposed: SemaphoreSlim.Dispose drops queued waiters, which then never return (not even on their own
    // cancellation token). Closing the page cancels _closing instead, and every waiter's wait is linked to it.
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private string? _guardScriptId;
    private string? _guardSignature;
    private string _guardSource = "";
    private string? _navChainOrigin;
    private string? _userReloadOrigin;
    private int _userReloads;
    private string? _docOrigin;
    private readonly ConcurrentQueue<BrowserNotice> _notices = new();
    private readonly ConcurrentQueue<string> _dialogsForShot = new();
    private readonly object _stateLock = new();
    private readonly Timer _pumpTimer;
    private string? _mainFrameId;
    private int _isolatedContextId;
    private string _url = "about:blank";
    private TaskCompletionSource<string>? _loadWaiter;
    private string _loadWaitFor = "load";
    private FrameSignature? _lastSignature;
    private ShotFrame? _lastShot;
    private string? _originalUserAgent;
    private int _buttonsDown;
    private double _lastPointerX, _lastPointerY;
    private volatile bool _crashed;
    private volatile bool _disposed;

    public ConsoleBuffer Console { get; } = new();
    public NetworkLog Network { get; } = new();
    public ICdpChannel Channel => _channel;
    public string Engine => _channel.Engine;
    public bool IsAlive => !_disposed && _channel.IsAlive;
    public bool IsCrashed => _crashed;
    public string CurrentUrl { get { lock (_stateLock) return _url; } }
    public ShotFrame? LastShot { get { lock (_stateLock) return _lastShot; } }

    /// <summary>"desktop", "mobile", "tablet" or "custom".</summary>
    public string Viewport { get; private set; } = "desktop";
    public bool TouchEmulation { get; private set; }

    /// <summary>The open project, for file:// rules — refreshed by every tool call.</summary>
    public string? ProjectRoot { get; set; }
    /// <summary>The folders the chat opened beside the project (open_project): their files open like the project's.
    /// Refreshed with <see cref="ProjectRoot"/>.</summary>
    public IReadOnlyList<string> ExtraRoots { get; set; } = Array.Empty<string>();
    /// <summary>The "allow external pages" setting: "ask", "allow" or "deny".</summary>
    public Func<string> ExternalMode { get; set; } = () => "ask";
    /// <summary>May the page contact this external origin right now (the user's sites, the active chat's)?</summary>
    public Func<string, bool> IsOriginApproved { get; set; } = _ => false;
    /// <summary>The user's browse permission rule for a URL (Allow / Deny / null = none).</summary>
    public Func<string, Models.PermAction?> UrlRule { get; set; } = _ => null;
    /// <summary>The extra origins the page-side WebSocket guard lets through (see <see cref="NetGuardScript"/>).</summary>
    public Func<IReadOnlyCollection<string>> GuardAllowList { get; set; } = () => Array.Empty<string>();
    /// <summary>A same-site redirect of an approved page was let through: (from origin, to origin).</summary>
    public Action<string, string>? RedirectApproved { get; set; }
    /// <summary>The origin of the document in the main frame ("http://127.0.0.1:5173"), or null (file://, blank).
    /// Its own requests are always allowed — the page that is open never breaks because a chat changed.</summary>
    public string? DocumentOrigin { get { lock (_stateLock) return _docOrigin; } }

    /// <summary>The URL or the console changed (the preview pane listens; raised on the engine thread).</summary>
    public event Action? Changed;

    public BrowserSession(ICdpChannel channel)
    {
        _channel = channel;
        _channel.EventReceived += OnEvent;
        _channel.ChildEventReceived += OnChildEvent;
        _channel.Notice += OnNotice;
        _channel.NavigationFilter = FilterNavigation;
        _channel.RequestFilter = FilterRequest;
        // Parse buffered console/network events off the engine thread, so a chatty page never piles them up.
        _pumpTimer = new Timer(_ => { try { Console.Pump(); Network.Pump(); } catch { } }, null, 300, 300);
    }

    // ─────────────────────────── setup ───────────────────────────

    /// <summary>Turn on everything the tools read BEFORE the first navigation, so a &lt;head&gt; script's logs
    /// are kept: Runtime, Log, Network, Page, lifecycle events, focus emulation, the helper world.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _channel.Subscribe(
            "Runtime.consoleAPICalled", "Runtime.exceptionThrown", "Runtime.executionContextCreated",
            "Runtime.executionContextDestroyed", "Runtime.executionContextsCleared", "Log.entryAdded",
            "Network.requestWillBeSent", "Network.responseReceived", "Network.loadingFinished", "Network.loadingFailed",
            "Page.loadEventFired", "Page.domContentEventFired", "Page.frameNavigated", "Page.frameStartedLoading",
            "Page.lifecycleEvent", "Inspector.targetCrashed", "Runtime.bindingCalled", "Target.attachedToTarget");
        await EnableDomainsAsync(ct);
        try
        {
            var v = await SendAsync("Browser.getVersion", null, ct);
            _originalUserAgent = ConsoleBuffer.Str(v, "userAgent");
        }
        catch { }
    }

    private async Task EnableDomainsAsync(CancellationToken ct)
    {
        await SendAsync("Page.enable", null, ct);
        await SendAsync("Runtime.enable", null, ct);
        await SendAsync("Log.enable", null, ct);
        await SendAsync("Network.enable", new { maxTotalBufferSize = 2_000_000, maxResourceBufferSize = 500_000 }, ct);
        // Fresh files on every load: the agent edits and reloads; a heuristically cached index.html would hide it.
        await SendAsync("Network.setCacheDisabled", new { cacheDisabled = true }, ct);
        await SendAsync("Page.setLifecycleEventsEnabled", new { enabled = true }, ct);
        // document.hasFocus() true and focus/keydown delivered while no window of ours has the real focus.
        await SendAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, ct);
        await SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = RefMapScript.Source, worldName = RefMapScript.WorldName }, ct);
        // The page-side WebSocket guard reports refusals through this binding (main world of every frame).
        try { await SendAsync("Runtime.addBinding", new { name = NetGuardScript.BindingName }, ct); }
        catch (CdpException ex) { Debug.WriteLine($"[browser] guard binding: {ex.Message}"); }   // already there (after a crash)
        await EnsureNetGuardAsync(ct, force: true);
        // The page's own service worker is NOT bypassed: a dev app's Mock Service Worker or PWA offline handler must
        // answer the page's fetches here exactly as in a real browser (bypassing it sent mocked API calls to the
        // network and the agent chased bugs that do not exist — browsertest SW1 / SW1n). Egress does not need the
        // bypass: a service worker's own network requests pass the request filter (3-argument filter, E1h, SW2b),
        // and the page assets a pass-through worker relays are still judged as assets
        // (WebView2Channel.ServiceWorkerRequestKind, SW3 / SW3n). Stale files from a caching worker: browser_open
        // fresh=true removes the site's service workers (SW4), and browser_open says when one controls the page (SW5).
        // Dedicated workers (and out-of-process frames) are held at start until the guard ran in them; each of them
        // auto-attaches its own children the same way, so a worker started by a worker is guarded too (NW1).
        try { await SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, ct); }
        catch (CdpException ex) { Debug.WriteLine($"[browser] worker auto-attach unavailable: {ex.Message}"); }
        await RefreshMainFrameAsync(ct);
    }

    /// <summary>(Re)register the WebSocket guard for the current policy — the external-pages mode and the origins
    /// approved right now. Takes effect for the next document (every navigation calls this first).</summary>
    private async Task EnsureNetGuardAsync(CancellationToken ct, bool force = false)
    {
        // One registration at a time (the pane and the agent can navigate at once): two would leave an orphan
        // guard with an older allow list that might run first in the next document.
        await _guardLock.WaitAsync(ct);
        try
        {
            string mode = ExternalMode();
            var allow = GuardAllowList();
            string signature = mode + "|" + string.Join(",", allow);
            if (!force && signature == _guardSignature && _guardScriptId != null) return;
            string source = NetGuardScript.Build(mode, allow);
            if (_guardScriptId != null)
            {
                try { await SendAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier = _guardScriptId }, ct); }
                catch (CdpException) { }
            }
            var r = await SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source }, ct);
            _guardScriptId = ConsoleBuffer.Str(r, "identifier");
            _guardSignature = signature;
            _guardSource = source;
        }
        finally { _guardLock.Release(); }
    }

    private readonly SemaphoreSlim _guardLock = new(1, 1);

    private async Task RefreshMainFrameAsync(CancellationToken ct)
    {
        var tree = await SendAsync("Page.getFrameTree", null, ct);
        if (tree.TryGetProperty("frameTree", out var ft) && ft.TryGetProperty("frame", out var f))
        {
            lock (_stateLock)
            {
                _mainFrameId = ConsoleBuffer.Str(f, "id");
                _url = ConsoleBuffer.Str(f, "url") ?? _url;
            }
        }
    }

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserSession));
        return _channel.SendAsync(method, parameters, timeout ?? CallTimeout, ct);
    }

    // ─────────────────────────── events ───────────────────────────

    private void OnEvent(string method, string json)
    {
        switch (method)
        {
            case "Runtime.consoleAPICalled":
            case "Runtime.exceptionThrown":
            case "Log.entryAdded":
                Console.EnqueueRaw(method, json);
                Changed?.Invoke();
                return;
            case "Network.requestWillBeSent":
            case "Network.responseReceived":
            case "Network.loadingFinished":
            case "Network.loadingFailed":
                Network.EnqueueRaw(method, json);
                return;
        }

        // The rest is rare and small: handled right here.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var p = doc.RootElement;
            switch (method)
            {
                case "Page.frameStartedLoading":
                    if (IsMainFrame(ConsoleBuffer.Str(p, "frameId"))) Network.EnqueueNavigation();
                    break;
                case "Page.frameNavigated":
                    if (p.TryGetProperty("frame", out var f) && !f.TryGetProperty("parentId", out _))
                    {
                        lock (_stateLock)
                        {
                            _mainFrameId = ConsoleBuffer.Str(f, "id") ?? _mainFrameId;
                            _url = (ConsoleBuffer.Str(f, "url") ?? _url) + (ConsoleBuffer.Str(f, "urlFragment") ?? "");
                            _docOrigin = UrlPolicy.OriginOf(_url);
                            _isolatedContextId = 0;
                        }
                        Console.EnqueueNavigation();
                        Changed?.Invoke();
                    }
                    break;
                case "Page.loadEventFired":
                    SignalLoad("load");
                    break;
                case "Page.domContentEventFired":
                    SignalLoad("domcontentloaded");
                    break;
                case "Page.lifecycleEvent":
                    if (IsMainFrame(ConsoleBuffer.Str(p, "frameId")) && ConsoleBuffer.Str(p, "name") is { } name)
                    {
                        if (name == "networkIdle") SignalLoad("networkidle");
                        else if (name == "load") SignalLoad("load");
                        else if (name == "DOMContentLoaded") SignalLoad("domcontentloaded");
                    }
                    break;
                case "Runtime.executionContextCreated":
                    if (p.TryGetProperty("context", out var ctx)
                        && ConsoleBuffer.Str(ctx, "name") == RefMapScript.WorldName
                        && ctx.TryGetProperty("auxData", out var aux) && IsMainFrame(ConsoleBuffer.Str(aux, "frameId"))
                        && ctx.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                    {
                        // The first world that appears for this document keeps the refs; never flip to another.
                        lock (_stateLock) if (_isolatedContextId == 0) _isolatedContextId = id;
                    }
                    break;
                case "Runtime.executionContextDestroyed":
                    if (p.TryGetProperty("executionContextId", out var dEl) && dEl.TryGetInt32(out var dId))
                        lock (_stateLock) if (_isolatedContextId == dId) _isolatedContextId = 0;
                    break;
                case "Runtime.executionContextsCleared":
                    lock (_stateLock) _isolatedContextId = 0;
                    break;
                case "Inspector.targetCrashed":
                    MarkCrashed("page crashed");
                    break;
                case "Runtime.bindingCalled":
                    OnGuardReport(p);
                    break;
                case "Target.attachedToTarget":
                    _ = PrepareChildAsync(p.Clone());
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] {method} handling failed: {ex.Message}"); }
    }

    /// <summary>Events of auto-attached workers / frames: the guard's reports, and their own children (a worker
    /// started by a worker, a frame inside an out-of-process frame) — held at start like the first level.</summary>
    private void OnChildEvent(string sessionId, string method, string json)
    {
        if (method is not ("Runtime.bindingCalled" or "Target.attachedToTarget")) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (method == "Target.attachedToTarget") _ = PrepareChildAsync(doc.RootElement.Clone());
            else OnGuardReport(doc.RootElement);
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] child {method} handling failed: {ex.Message}"); }
    }

    /// <summary>The page-side guard refused a WebSocket / WebTransport: the same console note and "Request
    /// blocked" notice as a refused fetch. The binding is reachable by page script too, so only the two known
    /// kinds are accepted and the URL is cut to one short line.</summary>
    private void OnGuardReport(JsonElement p)
    {
        if (ConsoleBuffer.Str(p, "name") != NetGuardScript.BindingName || ConsoleBuffer.Str(p, "payload") is not { } payload) return;
        string kind, url;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            kind = ConsoleBuffer.Str(doc.RootElement, "kind") ?? "";
            url = ConsoleBuffer.Str(doc.RootElement, "url") ?? "";
        }
        catch { return; }
        if (kind is not ("WebSocket" or "WebTransport")) return;
        url = url.Replace("\r", "").Replace("\n", "");
        if (url.Length > 300) url = url[..300] + "…";
        string origin = UrlPolicy.OriginOf(url) ?? url;
        OnNotice(new BrowserNotice(BrowserNoticeKind.RequestBlocked, $"{kind} connection to a site this chat has not allowed ({origin})", url));
    }

    /// <summary>A dedicated worker (or an out-of-process frame) was created and is held at its start: install the
    /// WebSocket guard in it, have it hold ITS children the same way (a worker may start workers — those report
    /// here through <see cref="OnChildEvent"/>), then let it run — always, even when installing failed (a held
    /// worker never starts).</summary>
    private async Task PrepareChildAsync(JsonElement p)
    {
        string? sid = ConsoleBuffer.Str(p, "sessionId");
        if (sid == null) return;
        string type = p.TryGetProperty("targetInfo", out var ti) ? ConsoleBuffer.Str(ti, "type") ?? "" : "";
        bool waiting = p.TryGetProperty("waitingForDebugger", out var w) && w.ValueKind == JsonValueKind.True;
        var t = TimeSpan.FromSeconds(5);
        try
        {
            if (_guardSource.Length > 0 && type is "worker" or "iframe")
            {
                try
                {
                    await _channel.SendToChildAsync(sid, "Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, t, CancellationToken.None);
                }
                catch (CdpException ex) { Debug.WriteLine($"[browser] nested auto-attach in a {type} unavailable: {ex.Message}"); }
                await _channel.SendToChildAsync(sid, "Runtime.addBinding", new { name = NetGuardScript.BindingName }, t, CancellationToken.None);
                if (type == "worker")
                    await _channel.SendToChildAsync(sid, "Runtime.evaluate", new { expression = _guardSource }, t, CancellationToken.None);
                else
                {
                    await _channel.SendToChildAsync(sid, "Page.enable", null, t, CancellationToken.None);
                    await _channel.SendToChildAsync(sid, "Page.addScriptToEvaluateOnNewDocument", new { source = _guardSource }, t, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] guard for a {type} failed: {ex.Message}"); }
        finally
        {
            if (waiting)
            {
                try { await _channel.SendToChildAsync(sid, "Runtime.runIfWaitingForDebugger", null, t, CancellationToken.None); }
                catch (Exception ex) { Debug.WriteLine($"[browser] could not resume a {type}: {ex.Message}"); }
            }
        }
    }

    private bool IsMainFrame(string? frameId)
    {
        lock (_stateLock) return frameId != null && (_mainFrameId == null || frameId == _mainFrameId);
    }

    private void SignalLoad(string what)
    {
        TaskCompletionSource<string>? w;
        string waitFor;
        lock (_stateLock) { w = _loadWaiter; waitFor = _loadWaitFor; }
        if (w == null) return;
        // "networkidle" also accepts a late load (some pages never go idle; the timeout covers the rest).
        bool enough = waitFor switch
        {
            "domcontentloaded" => what is "domcontentloaded" or "load" or "networkidle",
            "networkidle" => what == "networkidle",
            _ => what is "load" or "networkidle",
        };
        if (enough) w.TrySetResult(what);
    }

    private void OnNotice(BrowserNotice n)
    {
        _notices.Enqueue(n);
        while (_notices.Count > 50 && _notices.TryDequeue(out _)) { }
        switch (n.Kind)
        {
            case BrowserNoticeKind.Dialog:
                _dialogsForShot.Enqueue(n.Text);
                while (_dialogsForShot.Count > 10 && _dialogsForShot.TryDequeue(out _)) { }
                Console.EnqueueNote("info", "browser", "dialog " + n.Text + " — dismissed automatically", n.Url);
                break;
            case BrowserNoticeKind.RendererCrashed:
                MarkCrashed(n.Text);
                break;
            case BrowserNoticeKind.NavigationBlocked:
                Console.EnqueueNote("warn", "browser", "navigation blocked: " + n.Text, n.Url);
                break;
            case BrowserNoticeKind.RequestBlocked:
                Console.EnqueueNote("error", "browser", BlockedNote(n), n.Url);
                break;
            case BrowserNoticeKind.PopupBlocked:
                Console.EnqueueNote("info", "browser", "pop-up not opened (contained): " + (n.Url ?? ""), n.Url);
                break;
        }
        try { NoticeRaised?.Invoke(n); } catch (Exception ex) { Debug.WriteLine($"[browser] notice listener threw: {ex.Message}"); }
        Changed?.Invoke();
    }

    /// <summary>Something the engine did on its own (raised on the engine thread; listeners only store it). The
    /// agent's tool results get the same notices through <see cref="DrainNotices"/>.</summary>
    public event Action<BrowserNotice>? NoticeRaised;

    /// <summary>The console line for a refused request, naming the one way to approve it: a file outside the
    /// project can only be copied in or served; a site can be approved with browser_open allow=… (asks the user).</summary>
    internal static string BlockedNote(BrowserNotice n)
    {
        var loc = LocalizedResources.Instance;
        if (n.Url != null && n.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return string.Format(loc["browser.request.fileOutside"], ConsoleEntry.ShortUrl(n.Url));
        string origin = UrlPolicy.OriginOf(n.Url) ?? n.Url ?? "";
        return string.Format(loc["browser.request.blocked"], n.Text, origin);
    }

    private void MarkCrashed(string why)
    {
        if (_crashed) return;
        _crashed = true;
        lock (_stateLock) _isolatedContextId = 0;
        Console.EnqueueNote("error", "browser", why);
        _loadWaiter?.TrySetResult("crashed");
    }

    /// <summary>What the engine did on its own since the last call (dialogs, blocked pop-ups/navigations).</summary>
    public List<BrowserNotice> DrainNotices()
    {
        var list = new List<BrowserNotice>();
        while (_notices.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    // ─────────────────────── navigation policy ───────────────────────

    /// <summary>Engine thread, before every top-level navigation and each of its redirects: null = allowed, else
    /// why not. An approved page that redirects within its own site (http → https, example.com → www.example.com)
    /// carries the approval along; a redirect to another site is refused with the exact address to ask for.</summary>
    private string? FilterNavigation(string url, bool isRedirect)
    {
        string? from, userReload;
        lock (_stateLock) { from = _navChainOrigin; userReload = _userReloadOrigin; }
        var v = UrlPolicy.Classify(url, ProjectRoot, ExtraRoots);
        string? reason = DecideNavigation(v, isRedirect ? from : null, userReload);
        if (reason == null)
        {
            lock (_stateLock)
            {
                _navChainOrigin = v.Kind is UrlKind.Local or UrlKind.External ? v.Origin : null;
                // The user's Reload follows its own same-site redirects (http → https → www.), one hop at a time.
                if (_userReloadOrigin != null && v.Kind == UrlKind.External) _userReloadOrigin = v.Origin;
            }
        }
        return reason;
    }

    private string? DecideNavigation(UrlVerdict v, string? redirectFrom, string? userReload = null)
    {
        switch (v.Kind)
        {
            case UrlKind.Local:
            case UrlKind.ProjectFile:
                return null;
            case UrlKind.External:
            {
                string mode = ExternalMode();
                if (mode == "deny") return "external pages are switched off: " + v.Origin;
                var rule = UrlRule(v.Url);
                if (rule == Models.PermAction.Deny) return "denied by your permission rule: " + v.Origin;
                if (mode == "allow" || rule == Models.PermAction.Allow || IsOriginApproved(v.Origin)) return null;
                // The user pressed Reload on the page they are looking at: that site, for this navigation only —
                // nobody's approvals change (see ReloadByUserAsync).
                if (userReload != null && (redirectFrom == null
                        ? string.Equals(v.Origin, userReload, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(redirectFrom, userReload, StringComparison.OrdinalIgnoreCase) && UrlPolicy.SameSite(userReload, v.Origin)))
                    return null;
                if (redirectFrom != null && IsOriginApproved(redirectFrom) && UrlPolicy.SameSite(redirectFrom, v.Origin))
                {
                    try { RedirectApproved?.Invoke(redirectFrom, v.Origin); } catch { }
                    return null;
                }
                return redirectFrom != null
                    ? $"{redirectFrom} redirected to another site, which is not approved: {v.Url} — open that address with browser_open (the user is asked)"
                    : "external site not approved: " + v.Origin;
            }
            default:
                return v.RefusalKey == "browser.url.fileOutside" ? "file outside the open project" : "address not allowed";
        }
    }

    /// <summary>What a page may load: a page's own assets (scripts, styles, images, fonts, media) load from
    /// anywhere, as in any browser; documents (pages, frames) and programmatic requests (fetch, XHR, EventSource,
    /// beacons — from the page, its workers, shared workers and service workers) to a site nobody allowed are
    /// refused — answered locally with a CORS-correct 403, never sent. Allowed: this PC, project files, the open
    /// document's own origin, the sites the user or this chat approved, a user's browse Allow rule, and everything
    /// in "allow" mode. A user's Deny rule and "deny" mode refuse even assets. browser_eval is free on a local page
    /// and must not become a way to send data to the internet without asking, and a cancelled link click must not
    /// have reached the site first (measured: NavigationStarting's cancel alone let the request out and a 200 come
    /// back). WebSockets never reach this filter — <see cref="NetGuardScript"/> covers them in the page. The page
    /// assets a pass-through service worker relays arrive with their asset kind (see
    /// <see cref="WebView2Channel.ServiceWorkerRequestKind"/>); the worker's programmatic requests as
    /// "service worker fetch".</summary>
    private static readonly HashSet<string> AssetContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stylesheet", "Image", "Media", "Font", "Script", "TextTrack", "Manifest",
    };

    private string? FilterRequest(string url, string context)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return null;
        var v = UrlPolicy.ClassifyRequest(url, ProjectRoot, ExtraRoots);
        switch (v.Kind)
        {
            case UrlKind.Local:
            case UrlKind.ProjectFile:
                return null;
            case UrlKind.External:
            {
                string mode = ExternalMode();
                if (mode == "deny") return $"{context} request to {v.Origin} (external pages are switched off)";
                var rule = UrlRule(v.Url);
                if (rule == Models.PermAction.Deny) return $"{context} request to {v.Origin} (denied by your permission rule)";
                if (mode == "allow" || rule == Models.PermAction.Allow || IsOriginApproved(v.Origin)
                    || string.Equals(v.Origin, DocumentOrigin, StringComparison.OrdinalIgnoreCase)) return null;
                if (AssetContexts.Contains(context)) return null;
                // The document request of the user's Reload — a same-site redirect hop of it reaches this filter
                // after FilterNavigation let the hop through (measured: NavigationStarting comes first).
                if (context == "Document")
                {
                    string? userReload;
                    lock (_stateLock) userReload = _userReloadOrigin;
                    if (userReload != null && string.Equals(v.Origin, userReload, StringComparison.OrdinalIgnoreCase)) return null;
                }
                return $"{context} request to a site this chat has not allowed ({v.Origin})";
            }
            default:
                return url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? "file outside the open project" : null;
        }
    }

    // ─────────────────────────── navigation ───────────────────────────

    /// <summary>Navigate the page and wait for <paramref name="waitUntil"/> ("load", "domcontentloaded",
    /// "networkidle"). The caller has already applied UrlPolicy and permissions.</summary>
    public async Task<NavigationResult> NavigateAsync(string url, string waitUntil, int timeoutMs, bool fresh, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        bool wasCrashed = _crashed;
        if (fresh)
        {
            try
            {
                await SendAsync("Network.clearBrowserCache", null, ct);
                var origin = UrlPolicy.Classify(url, ProjectRoot, ExtraRoots).Origin;
                if (origin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    await SendAsync("Storage.clearDataForOrigin", new { origin, storageTypes = "all" }, ct);
            }
            catch { /* best effort */ }
        }

        // The new document gets the WebSocket guard for the sites approved right now.
        if (!wasCrashed)
        {
            try { await EnsureNetGuardAsync(ct); }
            catch (CdpException ex) { Debug.WriteLine($"[browser] net guard update failed: {ex.Message}"); }
        }

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock) { _loadWaiter = waiter; _loadWaitFor = waitUntil; }
        Network.EnqueueNavigation();
        try
        {
            JsonElement nav;
            try
            {
                nav = await SendAsync("Page.navigate", new { url }, ct);
            }
            catch (CdpException ex) when (wasCrashed)
            {
                // A dead renderer can refuse the first command; the engine starts a new one for the next.
                Debug.WriteLine($"[browser] navigate after crash: {ex.Message}; retrying");
                await Task.Delay(300, ct);
                nav = await SendAsync("Page.navigate", new { url }, ct);
            }
            _crashed = false;
            string? error = ConsoleBuffer.Str(nav, "errorText");
            string? loaderId = ConsoleBuffer.Str(nav, "loaderId");
            if (!string.IsNullOrEmpty(error))
            {
                return new NavigationResult { Ok = false, Error = error, Url = url, ElapsedMs = sw.ElapsedMilliseconds };
            }
            if (wasCrashed)
            {
                try { await EnableDomainsAsync(ct); } catch { /* domains survive in most engines */ }
                try { await EnsureNetGuardAsync(ct, force: true); } catch { }
            }

            bool stillLoading = false;
            if (!string.IsNullOrEmpty(loaderId))   // no loaderId = a same-document (#hash) navigation: nothing to wait for
            {
                try { await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 120_000)), ct); }
                catch (TimeoutException) { stillLoading = true; }
            }
            if (_crashed) return new NavigationResult { Ok = false, Error = "page crashed while loading", Url = url, ElapsedMs = sw.ElapsedMilliseconds };

            var info = await TryInfoAsync(ct);
            bool kicked = await EnsureFrameLoopAsync(ct);
            await Task.Delay(50, ct);   // let the response event reach the log
            return new NavigationResult
            {
                Ok = true,
                Url = info?.Url ?? CurrentUrl,
                Title = info?.Title ?? "",
                Status = Network.DocumentStatus(loaderId),
                ElapsedMs = sw.ElapsedMilliseconds,
                StillLoading = stillLoading,
                FrameLoopKicked = kicked,
            };
        }
        finally
        {
            lock (_stateLock) if (ReferenceEquals(_loadWaiter, waiter)) _loadWaiter = null;
        }
    }

    /// <summary>The user pressed Reload in the preview pane: load the open document again. The chat driving the
    /// browser now may not be the one that approved this site — the user reloading what they are looking at lets
    /// THIS navigation (and its same-site redirects) through, and approves the site for nobody: not for the user's
    /// all-chats set, not for any chat. Later page navigations to it, and any chat's reads of it, decide as before.
    /// Deny mode and a user's Deny rule still refuse. Null = nothing to reload (blank page).</summary>
    public async Task<NavigationResult?> ReloadByUserAsync(int timeoutMs, CancellationToken ct)
    {
        string url = CurrentUrl;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return null;
        var v = UrlPolicy.Classify(url, ProjectRoot, ExtraRoots);
        // Reload and the pane's Desktop/Mobile switch (which reloads) can overlap: the window closes with the last one.
        lock (_stateLock) { _userReloadOrigin = v.Kind == UrlKind.External ? v.Origin : null; _userReloads++; }
        try { return await NavigateAsync(url, "load", timeoutMs, false, ct); }
        finally { lock (_stateLock) if (--_userReloads == 0) _userReloadOrigin = null; }
    }

    /// <summary>Does a service worker control the open document (its fetch handler answers the page's requests
    /// first — a Mock Service Worker, or a PWA cache that may serve old files)? False when it cannot tell.
    /// browser_open reports it, with fresh=true as the way to remove it.</summary>
    public async Task<bool> IsServiceWorkerControlledAsync(CancellationToken ct)
    {
        try
        {
            var r = await HelperAsync("!!(navigator.serviceWorker && navigator.serviceWorker.controller)", ct);
            return r.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// While the page is off-screen, make sure its renderer ticks requestAnimationFrame (a canvas game's loop):
    /// count frames for 250 ms in the helper world, and only if none came, re-assert visibility and count again.
    /// Returns true when it had to step in. Never throws (a page that cannot be probed is left alone).
    /// </summary>
    public async Task<bool> EnsureFrameLoopAsync(CancellationToken ct)
    {
        if (_channel is not IOffScreenEngine off || !off.IsOffScreen || _crashed) return false;
        try
        {
            var n = await HelperAsync("__cx.frames(250)", ct);
            if (n.ValueKind != JsonValueKind.Number || n.GetInt32() > 0) return false;
            await off.KickFrameLoopAsync();
            Debug.WriteLine("[browser] off-screen page had no animation frames — visibility re-asserted");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    // ─────────────────────── isolated helper ───────────────────────

    private async Task<int> EnsureHelperContextAsync(CancellationToken ct)
    {
        int id;
        lock (_stateLock) id = _isolatedContextId;
        if (id != 0) return id;

        string? frame;
        lock (_stateLock) frame = _mainFrameId;
        if (frame == null) { await RefreshMainFrameAsync(ct); lock (_stateLock) frame = _mainFrameId; }
        var world = await SendAsync("Page.createIsolatedWorld", new { frameId = frame, worldName = RefMapScript.WorldName, grantUniveralAccess = false }, ct);
        if (!world.TryGetProperty("executionContextId", out var idEl) || !idEl.TryGetInt32(out id))
            throw new CdpException("Could not create the helper world.");
        await SendAsync("Runtime.evaluate", new { expression = RefMapScript.Source, contextId = id, returnByValue = true }, ct);
        lock (_stateLock)
        {
            // An event may have registered the document's own helper world meanwhile — keep the first one.
            if (_isolatedContextId == 0) _isolatedContextId = id;
            return _isolatedContextId;
        }
    }

    /// <summary>Evaluate <paramref name="expression"/> (a call on __cx) in the helper world; returns the value.</summary>
    public async Task<JsonElement> HelperAsync(string expression, CancellationToken ct)
    {
        if (_crashed) throw new CdpException("The page crashed — open it again with browser_open.");
        for (int attempt = 0; ; attempt++)
        {
            int ctx = await EnsureHelperContextAsync(ct);
            try
            {
                var r = await SendAsync("Runtime.evaluate", new { expression, contextId = ctx, returnByValue = true, awaitPromise = true }, ct);
                if (r.TryGetProperty("exceptionDetails", out var ex))
                {
                    string text = ex.TryGetProperty("exception", out var eo) ? ConsoleBuffer.Str(eo, "description") ?? "" : ConsoleBuffer.Str(ex, "text") ?? "";
                    // The world was replaced by a navigation between the lookup and the call — find it again.
                    if (attempt == 0 && text.Contains("__cx", StringComparison.Ordinal))
                    {
                        lock (_stateLock) if (_isolatedContextId == ctx) _isolatedContextId = 0;
                        continue;
                    }
                    throw new CdpException(FirstLine(text));
                }
                return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : default;
            }
            catch (CdpException ex) when (attempt == 0 && (ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase)
                                                         || ex.Message.Contains("frame", StringComparison.OrdinalIgnoreCase)))
            {
                lock (_stateLock) if (_isolatedContextId == ctx) _isolatedContextId = 0;
            }
        }
    }

    public static string Js(string? s) => JsonSerializer.Serialize(s ?? "");

    public async Task<PageInfo> GetInfoAsync(CancellationToken ct)
    {
        var v = await HelperAsync("__cx.info()", ct);
        return new PageInfo(
            ConsoleBuffer.Str(v, "url") ?? CurrentUrl, ConsoleBuffer.Str(v, "title") ?? "",
            Num(v, "w"), Num(v, "h"), Num(v, "dpr", 1), Num(v, "sx"), Num(v, "sy"), Num(v, "sw"), Num(v, "sh"),
            ConsoleBuffer.Str(v, "ready") ?? "", v.TryGetProperty("focus", out var fo) && fo.ValueKind == JsonValueKind.True);
    }

    public async Task<PageInfo?> TryInfoAsync(CancellationToken ct)
    {
        try { return await GetInfoAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static double Num(JsonElement e, string name, double fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : fallback;

    // ─────────────────────────── eval ───────────────────────────

    /// <summary>Run <paramref name="expression"/> in the page's MAIN world (where its own variables live).
    /// A timeout terminates the script (Runtime.terminateExecution), so the next call works.</summary>
    public async Task<EvalResult> EvaluateAsync(string expression, bool awaitPromise, int timeoutMs, CancellationToken ct)
    {
        if (_crashed) return new EvalResult(false, "The page crashed — open it again with browser_open.");
        timeoutMs = Math.Clamp(timeoutMs, 100, 60_000);
        JsonElement r;
        var sw = Stopwatch.StartNew();
        string timedOut = $"Timed out after {timeoutMs} ms — the script was stopped.";
        try
        {
            r = await SendAsync("Runtime.evaluate", new
            {
                expression, objectGroup = EvalGroup, returnByValue = false, awaitPromise, userGesture = true,
                replMode = true, allowUnsafeEvalBlockedByCSP = true, timeout = timeoutMs,
            }, ct, TimeSpan.FromMilliseconds(timeoutMs + 2000));
        }
        catch (TimeoutException)
        {
            // Still running (an awaited promise that never settles, or a loop the engine's own timeout missed).
            try { await SendAsync("Runtime.terminateExecution", null, CancellationToken.None, TimeSpan.FromSeconds(5)); } catch { }
            return new EvalResult(false, timedOut, TimedOut: true);
        }
        catch (CdpException) when (sw.ElapsedMilliseconds >= timeoutMs * 0.9)
        {
            // The engine's own timeout terminated the script and answered with a bare protocol error.
            return new EvalResult(false, timedOut, TimedOut: true);
        }

        if (r.TryGetProperty("exceptionDetails", out var ex))
        {
            string message = ex.TryGetProperty("exception", out var eo)
                ? FirstLine(ConsoleBuffer.Str(eo, "description") ?? ConsoleBuffer.RemoteObjectText(eo))
                : ConsoleBuffer.Str(ex, "text") ?? "error";
            int? line = ExceptionLine(ex);
            bool terminated = message.Contains("terminated", StringComparison.OrdinalIgnoreCase);
            await ReleaseEvalObjectsAsync();
            return new EvalResult(false, terminated ? timedOut : message, terminated, line);
        }
        if (!r.TryGetProperty("result", out var res)) return new EvalResult(true, "undefined");
        try
        {
            string type = ConsoleBuffer.Str(res, "type") ?? "";
            // Re-encoded so Thai and other non-ASCII text stays readable (the engine may send \uXXXX escapes).
            if (res.TryGetProperty("value", out var v)) return new EvalResult(true, JsonSerializer.Serialize(v, ReadableJson));
            if (type == "undefined") return new EvalResult(true, "undefined");
            if (ConsoleBuffer.Str(res, "unserializableValue") is { } u) return new EvalResult(true, u);
            if (ConsoleBuffer.Str(res, "objectId") is { } objectId)
            {
                const string serializer = "function(){const seen=new WeakSet();try{return JSON.stringify(this,(k,v)=>{" +
                    "if(typeof Node!=='undefined'&&v instanceof Node)return '<'+v.nodeName.toLowerCase()+(v.id?'#'+v.id:'')+'>';" +
                    "if(typeof v==='bigint')return v.toString()+'n';if(typeof v==='function')return '[function '+(v.name||'')+']';" +
                    "if(typeof v==='object'&&v!==null){if(seen.has(v))return '[circular]';seen.add(v);" +
                    "if(v instanceof Map)return Object.fromEntries(v);if(v instanceof Set)return [...v];}return v;})}catch(e){return String(this)}}";
                var s = await SendAsync("Runtime.callFunctionOn", new { objectId, functionDeclaration = serializer, returnByValue = true }, ct,
                    TimeSpan.FromSeconds(5));
                if (s.TryGetProperty("result", out var sr) && sr.TryGetProperty("value", out var sv) && sv.ValueKind == JsonValueKind.String)
                    return new EvalResult(true, sv.GetString() ?? "");
                return new EvalResult(true, ConsoleBuffer.Str(res, "description") ?? type);
            }
            return new EvalResult(true, ConsoleBuffer.Str(res, "description") ?? type);
        }
        finally { await ReleaseEvalObjectsAsync(); }
    }

    /// <summary>1-based line of an evaluated script's exception. The top stack frame is exact; the details'
    /// own lineNumber is one too high for REPL-mode scripts (measured: "throw" on line 1 reports 1, 0-based).</summary>
    internal static int? ExceptionLine(JsonElement details)
    {
        if (details.TryGetProperty("stackTrace", out var st) && st.TryGetProperty("callFrames", out var frames)
            && frames.ValueKind == JsonValueKind.Array && frames.GetArrayLength() > 0
            && frames[0].TryGetProperty("lineNumber", out var fl) && fl.TryGetInt32(out var top))
            return top + 1;
        return details.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? Math.Max(1, l) : null;
    }

    private async Task ReleaseEvalObjectsAsync()
    {
        try { await SendAsync("Runtime.releaseObjectGroup", new { objectGroup = EvalGroup }, CancellationToken.None, TimeSpan.FromSeconds(3)); }
        catch { }
    }

    // ─────────────────────────── screenshot ───────────────────────────

    /// <summary>
    /// Capture as JPEG q75, scaled so the long edge is at most <paramref name="maxEdge"/> and the area at most
    /// 1.15 MP. <paramref name="mode"/>: "viewport", "full" (whole page), "ref" (one element) or "region"
    /// ("x0,y0,x1,y1" in the previous screenshot's pixels).
    /// </summary>
    public async Task<ShotResult> ScreenshotAsync(string mode, string? refId, string? region, int maxEdge, CancellationToken ct)
    {
        if (_crashed) throw new CdpException("The page crashed — open it again with browser_open.");
        // An element is brought into view first (only if it is not fully visible), so its shot can stay inside the
        // viewport: capturing beyond the viewport briefly resizes the page, and a canvas game that re-lays out on
        // "resize" (clearing its canvas) must not be disturbed by a mere look at it.
        JsonElement? refRect = null;
        if (mode == "ref")
        {
            var r = await HelperAsync($"__cx.reveal({Js(refId)})", ct);
            if (r.ValueKind != JsonValueKind.Object) throw new ArgumentException("stale_ref");
            refRect = r;
        }
        var metrics = await SendAsync("Page.getLayoutMetrics", null, ct);
        double vx = 0, vy = 0, vw = 1280, vh = 800, cw = 1280, ch = 800;
        if (metrics.TryGetProperty("cssVisualViewport", out var vv))
        {
            vx = Num(vv, "pageX"); vy = Num(vv, "pageY");
            vw = Num(vv, "clientWidth", 1280); vh = Num(vv, "clientHeight", 800);
        }
        if (metrics.TryGetProperty("cssContentSize", out var cs)) { cw = Num(cs, "width", vw); ch = Num(cs, "height", vh); }
        double dpr = 1;
        try { var d = await SendAsync("Runtime.evaluate", new { expression = "devicePixelRatio", returnByValue = true }, ct); dpr = Num(d.GetProperty("result"), "value", 1); }
        catch { }

        double x = vx, y = vy, w = vw, h = vh;
        bool beyond = false;
        bool Inside() => x >= vx - 0.5 && y >= vy - 0.5 && x + w <= vx + vw + 0.5 && y + h <= vy + vh + 0.5;
        switch (mode)
        {
            case "full":
                // The one mode that must render beyond the viewport (the page sees one resize event).
                x = 0; y = 0; w = Math.Max(1, cw); h = Math.Min(Math.Max(1, ch), 16_384); beyond = true;
                break;
            case "ref":
            {
                var r = refRect!.Value;
                x = Num(r, "x"); y = Num(r, "y"); w = Math.Max(1, Num(r, "w")); h = Math.Max(1, Num(r, "h"));
                beyond = !Inside();   // only an element larger than the viewport needs it
                break;
            }
            case "region":
            {
                var last = LastShot ?? throw new ArgumentException("no_previous_shot");
                var nums = (region ?? "").Split(',', ' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.NaN).ToArray();
                if (nums.Length != 4 || nums.Any(double.IsNaN) || nums[2] <= nums[0] || nums[3] <= nums[1]) throw new ArgumentException("bad_region");
                x = last.ClipX + nums[0] / last.PxPerCssX; y = last.ClipY + nums[1] / last.PxPerCssY;
                w = (nums[2] - nums[0]) / last.PxPerCssX; h = (nums[3] - nums[1]) / last.PxPerCssY;
                beyond = !Inside();   // a zoom into the visible page never resizes it
                break;
            }
        }

        double scale = ScreenshotPipeline.Scale(w, h, dpr, maxEdge);
        byte[] jpeg = await CaptureAsync(x, y, w, h, scale, beyond, ct);
        var pixels = await Task.Run(() => PixelFrame.Decode(jpeg), ct);
        double longEdge = Math.Max(pixels.Width, pixels.Height), area = (double)pixels.Width * pixels.Height;
        if (longEdge > maxEdge || area > ScreenshotPipeline.MaxPixels)
        {
            // The engine applied the device scale differently than assumed — correct once from the real size.
            double k = Math.Min(maxEdge / longEdge, Math.Sqrt(ScreenshotPipeline.MaxPixels / area)) * 0.99;
            jpeg = await CaptureAsync(x, y, w, h, scale * k, beyond, ct);
            pixels = await Task.Run(() => PixelFrame.Decode(jpeg), ct);
        }

        var frame = new ShotFrame(x, y, w, h, pixels.Width, pixels.Height, dpr, vx, vy, mode);

        // Canvases on screen, mapped into image pixels.
        var regions = new List<CanvasRegion>();
        bool? overflow = null;
        try
        {
            var list = await HelperAsync("__cx.canvases()", ct);
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var c in list.EnumerateArray())
                {
                    double docX = Num(c, "x") + vx, docY = Num(c, "y") + vy;
                    string label = (ConsoleBuffer.Str(c, "id") is { Length: > 0 } cid ? "#" + cid : "") + " [" + ConsoleBuffer.Str(c, "ref") + "]";
                    regions.Add(new CanvasRegion(label.Trim(),
                        (int)Math.Round((docX - x) * frame.PxPerCssX), (int)Math.Round((docY - y) * frame.PxPerCssY),
                        (int)Math.Round(Num(c, "w") * frame.PxPerCssX), (int)Math.Round(Num(c, "h") * frame.PxPerCssY),
                        (int)Math.Round(Num(c, "w")), (int)Math.Round(Num(c, "h"))));
                }
            var of = await HelperAsync("__cx.overflowX()", ct);
            overflow = of.ValueKind == JsonValueKind.True;
        }
        catch { }

        var dialogs = new List<string>();
        while (_dialogsForShot.TryDequeue(out var d)) dialogs.Add(d);

        string key = $"{mode}|{Math.Round(x)}|{Math.Round(y)}|{Math.Round(w)}x{Math.Round(h)}|{pixels.Width}x{pixels.Height}";
        var signature = PixelDigest.Signature(pixels, key);
        FrameSignature? previous;
        lock (_stateLock)
        {
            previous = _lastSignature;
            _lastSignature = signature;
            _lastShot = frame;
        }
        string digest = PixelDigest.Describe(pixels, regions, previous, signature, dialogs, overflow);
        return new ShotResult { Jpeg = jpeg, Frame = frame, Digest = digest, Pixels = pixels };
    }

    private async Task<byte[]> CaptureAsync(double x, double y, double w, double h, double scale, bool beyond, CancellationToken ct)
    {
        var shot = await SendAsync("Page.captureScreenshot", new
        {
            format = "jpeg", quality = 75, optimizeForSpeed = true, fromSurface = true, captureBeyondViewport = beyond,
            clip = new { x = Math.Round(x, 2), y = Math.Round(y, 2), width = Math.Round(w, 2), height = Math.Round(h, 2), scale = Math.Round(scale, 4) },
        }, ct, TimeSpan.FromSeconds(30));
        string data = ConsoleBuffer.Str(shot, "data") ?? throw new CdpException("The engine returned no image.");
        return Convert.FromBase64String(data);
    }

    // ─────────────────────────── coordinates ───────────────────────────

    /// <summary>
    /// A point to act on, in viewport CSS pixels: a ref's centre (scrolled into view), or x,y. x,y are image
    /// pixels of the last screenshot unless <paramref name="css"/> is true (or no screenshot was taken yet, when
    /// image and CSS pixels are the same thing).
    /// </summary>
    public async Task<(double X, double Y, JsonElement? Target)> ResolvePointAsync(string? refId, double? x, double? y, bool css, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(refId))
        {
            var p = await HelperAsync($"__cx.point({Js(refId)})", ct);
            if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("stale_ref");
            return (Num(p, "x"), Num(p, "y"), p);
        }
        if (x is not double px || y is not double py) throw new ArgumentException("no_target");
        var shot = LastShot;
        if (css || shot == null) return (px, py, null);
        // Image pixel → document CSS pixel → viewport CSS pixel (as the page was scrolled at the shot).
        return (shot.ClipX + px / shot.PxPerCssX - shot.ScrollX, shot.ClipY + py / shot.PxPerCssY - shot.ScrollY, null);
    }

    // ─────────────────────────── input ───────────────────────────

    /// <summary>Run a multi-event input sequence with no other input in between. <paramref name="action"/> gets a
    /// token that also fires when the page is closed (browser_close from another agent, the pane's Close button):
    /// a call still queued here then ends at once with <see cref="ObjectDisposedException"/> ("No page is open")
    /// instead of waiting forever.</summary>
    public async Task<T> WithInputLockAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserSession));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        try { await _inputLock.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(BrowserSession));
        }
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BrowserSession));
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(BrowserSession));
        }
        finally
        {
            try { _inputLock.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        }
    }

    public async Task ClickAsync(double x, double y, string button, bool doubleClick, int modifiers, CancellationToken ct)
    {
        if (TouchEmulation && button == "left")
        {
            await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchStart", x, y, modifiers), ct);
            await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchEnd", x, y, modifiers), ct);
            if (doubleClick)
            {
                await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchStart", x, y, modifiers), ct);
                await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchEnd", x, y, modifiers), ct);
            }
            return;
        }
        int bit = InputDispatcher.ButtonBit(button);
        await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseMoved", x, y, "none", 0, 0, modifiers), ct);
        await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mousePressed", x, y, button, bit, 1, modifiers), ct);
        await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseReleased", x, y, button, 0, 1, modifiers), ct);
        if (doubleClick)
        {
            await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mousePressed", x, y, button, bit, 2, modifiers), ct);
            await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseReleased", x, y, button, 0, 2, modifiers), ct);
        }
        _lastPointerX = x; _lastPointerY = y;
    }

    /// <summary>Low-level pointer steps for canvas games: move / down / up / hover / drag / press (down, hold, up).</summary>
    public async Task PointerAsync(string action, double x, double y, double? toX, double? toY, int holdMs, int steps, string button, CancellationToken ct)
    {
        holdMs = Math.Clamp(holdMs, 0, 10_000);
        steps = Math.Clamp(steps, 1, 100);
        int bit = InputDispatcher.ButtonBit(button);
        bool touch = TouchEmulation;

        async Task Move(double mx, double my)
        {
            if (touch && _buttonsDown != 0) await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchMove", mx, my), ct);
            else if (!touch) await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseMoved", mx, my, _buttonsDown != 0 ? button : "none", _buttonsDown), ct);
            _lastPointerX = mx; _lastPointerY = my;
        }
        async Task Down(double dx, double dy)
        {
            // Marked pressed BEFORE the press is sent: a cancel that lands while the press is in flight (the engine
            // may still deliver it) must release it, never leave a game charging forever.
            if (touch)
            {
                _lastPointerX = dx; _lastPointerY = dy;
                _buttonsDown |= bit;
                await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchStart", dx, dy), ct);
            }
            else
            {
                await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseMoved", dx, dy, "none", _buttonsDown), ct);
                _lastPointerX = dx; _lastPointerY = dy;
                int before = _buttonsDown;
                _buttonsDown |= bit;
                await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mousePressed", dx, dy, button, before | bit, 1), ct);
            }
        }
        async Task Up(double ux, double uy)
        {
            if (touch) await SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchEnd", ux, uy), ct);
            else await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseReleased", ux, uy, button, _buttonsDown & ~bit, 1), ct);
            _buttonsDown &= ~bit;
        }

        try
        {
            switch (action)
            {
                case "move":
                case "hover":
                    await Move(x, y);
                    break;
                case "down":
                    await Down(x, y);
                    if (holdMs > 0) await Task.Delay(holdMs, ct);
                    break;
                case "up":
                    await Up(x, y);
                    break;
                case "press":
                    await Down(x, y);
                    if (holdMs > 0) await Task.Delay(holdMs, ct);
                    await Up(x, y);
                    break;
                case "drag":
                {
                    double tx = toX ?? x, ty = toY ?? y;
                    await Down(x, y);
                    if (holdMs > 0) await Task.Delay(holdMs, ct);
                    for (int i = 1; i <= steps; i++)
                    {
                        await Move(x + (tx - x) * i / steps, y + (ty - y) * i / steps);
                        await Task.Delay(16, ct);
                    }
                    await Up(tx, ty);
                    break;
                }
                default:
                    throw new ArgumentException("bad_action");
            }
        }
        catch (Exception ex) when ((_buttonsDown & bit) != 0 && ex is OperationCanceledException or TimeoutException or CdpException)
        {
            // Stopped (or failed) mid-sequence: never leave the button pressed (a game would charge forever).
            try
            {
                if (touch) await _channel.SendAsync("Input.dispatchTouchEvent", InputDispatcher.Touch("touchEnd", _lastPointerX, _lastPointerY), TimeSpan.FromSeconds(3), CancellationToken.None);
                else await _channel.SendAsync("Input.dispatchMouseEvent", InputDispatcher.Mouse("mouseReleased", _lastPointerX, _lastPointerY, button, _buttonsDown & ~bit, 1), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch { }
            _buttonsDown &= ~bit;
            throw;
        }
    }

    /// <summary>Press keys in order: for each chord, modifiers down, the key down, (hold), the key up, modifiers up.</summary>
    public async Task<List<string>> KeysAsync(IReadOnlyList<KeyChord> chords, int repeat, int holdMs, CancellationToken ct,
        Func<KeyChord, CancellationToken, Task>? beforeChord = null)
    {
        var sent = new List<string>();
        repeat = Math.Clamp(repeat, 1, 100);
        holdMs = Math.Clamp(holdMs, 0, 5000);
        static int Bit(KeySpec m) => m.Key switch { "Alt" => InputDispatcher.Alt, "Control" => InputDispatcher.Ctrl, "Meta" => InputDispatcher.Meta, "Shift" => InputDispatcher.Shift, _ => 0 };
        for (int r = 0; r < repeat; r++)
            foreach (var chord in chords)
            {
                // browser_key's credential check runs before EVERY chord: a Tab can move the focus mid-sequence.
                if (beforeChord != null) await beforeChord(chord, ct);
                int mods = 0;
                // Everything is marked pressed BEFORE its keyDown is sent, and unmarked only after its keyUp went:
                // a cancel or a failure anywhere in between releases exactly what may be down (key, then modifiers).
                var downMods = new List<KeySpec>();
                bool keyDown = false;
                try
                {
                    foreach (var m in chord.ModifierKeys)
                    {
                        mods |= Bit(m);
                        downMods.Add(m);
                        await SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyDown(m, mods), ct);
                    }
                    keyDown = true;
                    await SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyDown(chord.Key, chord.Modifiers), ct);
                    if (holdMs > 0) await Task.Delay(holdMs, ct);
                    await SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyUp(chord.Key, chord.Modifiers), ct);
                    keyDown = false;
                    for (int i = downMods.Count - 1; i >= 0; i--)
                    {
                        var m = downMods[i];
                        mods &= ~Bit(m);
                        await SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyUp(m, mods), ct);
                        downMods.RemoveAt(i);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or CdpException)
                {
                    var t = TimeSpan.FromSeconds(3);
                    try
                    {
                        if (keyDown) await _channel.SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyUp(chord.Key, chord.Modifiers), t, CancellationToken.None);
                        for (int i = downMods.Count - 1; i >= 0; i--)
                            await _channel.SendAsync("Input.dispatchKeyEvent", InputDispatcher.KeyUp(downMods[i], 0), t, CancellationToken.None);
                    }
                    catch { }
                    throw;
                }
                sent.Add(chord.Key.Key == " " ? "Space" : chord.Key.Key);
            }
        return sent;
    }

    /// <summary>Insert text at the focus (Thai and emoji safe: no IME, no key mapping).</summary>
    public Task InsertTextAsync(string text, CancellationToken ct) => SendAsync("Input.insertText", new { text }, ct);

    public async Task WheelAsync(double x, double y, double dx, double dy, CancellationToken ct)
    {
        await SendAsync("Input.dispatchMouseEvent", InputDispatcher.Wheel(x, y, dx, dy), ct);
    }

    // ─────────────────────────── viewport ───────────────────────────

    /// <summary>Emulate a device: "mobile" (375×812 @3, touch, mobile UA), "tablet" (768×1024 @2, touch),
    /// "desktop" (clear), or a custom size. Persists across reloads until "desktop".</summary>
    public async Task ResizeAsync(string preset, int? width, int? height, double? dpr, bool? touch, CancellationToken ct)
    {
        const string MobileUa = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";
        const string TabletUa = "Mozilla/5.0 (Linux; Android 14; Pixel Tablet) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        switch (preset)
        {
            case "desktop":
                await SendAsync("Emulation.clearDeviceMetricsOverride", null, ct);
                await SendAsync("Emulation.setTouchEmulationEnabled", new { enabled = false }, ct);
                try { await SendAsync("Emulation.setEmitTouchEventsForMouse", new { enabled = false }, ct); } catch { }
                if (_originalUserAgent != null) await SendAsync("Emulation.setUserAgentOverride", new { userAgent = _originalUserAgent }, ct);
                Viewport = "desktop"; TouchEmulation = false;
                break;
            default:
            {
                bool mobile = preset is "mobile" or "tablet";
                int w = preset switch { "mobile" => 375, "tablet" => 768, _ => Math.Clamp(width ?? 1280, 200, 4096) };
                int h = preset switch { "mobile" => 812, "tablet" => 1024, _ => Math.Clamp(height ?? 800, 200, 4096) };
                double scale = dpr ?? (preset switch { "mobile" => 3, "tablet" => 2, _ => 1 });
                bool useTouch = touch ?? mobile;
                await SendAsync("Emulation.setDeviceMetricsOverride", new
                {
                    width = w, height = h, deviceScaleFactor = Math.Clamp(scale, 0.5, 4), mobile,
                    screenWidth = w, screenHeight = h,
                }, ct);
                await SendAsync("Emulation.setTouchEmulationEnabled", new { enabled = useTouch, maxTouchPoints = useTouch ? 5 : 1 }, ct);
                // The user clicking in the pane under touch emulation then produces touches too.
                try { await SendAsync("Emulation.setEmitTouchEventsForMouse", new { enabled = useTouch, configuration = useTouch ? "mobile" : "desktop" }, ct); } catch { }
                if (mobile) await SendAsync("Emulation.setUserAgentOverride", new { userAgent = preset == "mobile" ? MobileUa : TabletUa }, ct);
                Viewport = preset is "mobile" or "tablet" ? preset : "custom";
                TouchEmulation = useTouch;
                break;
            }
        }
        lock (_stateLock) { _lastShot = null; _lastSignature = null; }
        Changed?.Invoke();
    }

    // ─────────────────────────── teardown ───────────────────────────

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pumpTimer.Dispose(); } catch { }
        _channel.EventReceived -= OnEvent;
        _channel.ChildEventReceived -= OnChildEvent;
        _channel.Notice -= OnNotice;
        // The filters stay until the engine is closed (the channel is closed right after): clearing them first
        // would let a request slip out in the gap.
        _loadWaiter?.TrySetResult("closed");
        // Wakes every call queued on the input lock (and one holding it in a pause) — they end with "No page is
        // open" instead of hanging. The lock itself is left to the GC.
        try { _closing.Cancel(); } catch { }
    }
}
