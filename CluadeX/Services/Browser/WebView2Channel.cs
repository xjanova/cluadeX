using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CluadeX.Services.Browser;

/// <summary>How the WebView2 engine is created.</summary>
public sealed class WebView2ChannelOptions
{
    /// <summary>The profile folder (cookies, cache, local storage). Always explicit: the default next to the exe
    /// sits inside Velopack's swapped "current" folder and may be read-only.</summary>
    public required string UserDataFolder { get; init; }
    /// <summary>Viewport of the off-screen host, in device pixels.</summary>
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 800;
    /// <summary>Software rendering — next to a local model that already fills the GPU.</summary>
    public bool DisableGpu { get; init; }
    /// <summary>Default timeout of one CDP call.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>More Chromium switches (harnesses: --host-resolver-rules to map test host names onto loopback).</summary>
    public string? ExtraArguments { get; init; }
}

/// <summary>
/// The WebView2 engine behind one page. The controller lives in an off-screen, visible, never-activated popup
/// window owned by this class ("parking"), so requestAnimationFrame keeps running while no pane shows the page;
/// a preview pane borrows it with <see cref="AttachTo"/> and gives it back with <see cref="Park"/>.
///
/// Thread affinity: every CoreWebView2 call runs on <see cref="Dispatcher"/> through InvokeAsync, never with
/// .Result/.Wait, and every call has a timeout — a page stuck in a JavaScript loop cannot hang the agent.
/// </summary>
public sealed class WebView2Channel : ICdpChannel, IOffScreenEngine
{
    // Flags that keep a page that nobody looks at running at full speed (rAF, timers, the game loop).
    internal const string BackgroundFlags =
        "--disable-renderer-backgrounding --disable-background-timer-throttling " +
        "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion";

    private readonly WebView2ChannelOptions _options;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private HwndSource? _parking;
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _receivers = new();
    private readonly TaskCompletionSource _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _alive;
    private volatile bool _disposed;
    private IntPtr _attachedTo;

    public Dispatcher Dispatcher { get; }
    public string Engine => "webview2";
    public bool IsAlive => _alive && !_disposed;
    public string UserDataFolder => _options.UserDataFolder;
    /// <summary>The browser process id (for the harness's leak check), 0 when unknown.</summary>
    public int BrowserProcessId { get; private set; }
    /// <summary>The window the page is shown in right now (the parking window when no pane has it).</summary>
    public IntPtr HostWindow => _attachedTo != IntPtr.Zero ? _attachedTo : _parking?.Handle ?? IntPtr.Zero;
    public bool IsParked => _attachedTo == IntPtr.Zero;

    public event Action<string, string>? EventReceived;
    public event Action<string, string, string>? ChildEventReceived;
    public event Action<BrowserNotice>? Notice;
    /// <summary>The top-level URL changed (address bar).</summary>
    public event Action<string>? SourceChanged;
    public Func<string, bool, string?>? NavigationFilter { get; set; }
    public Func<string, string, string?>? RequestFilter { get; set; }

    /// <summary>The engine reported that its browser process (and every helper process) exited.</summary>
    public bool BrowserExitObserved => _browserExited.Task.IsCompleted;

    private WebView2Channel(Dispatcher dispatcher, WebView2ChannelOptions options)
    {
        Dispatcher = dispatcher;
        _options = options;
    }

    /// <summary>Create the engine on <paramref name="dispatcher"/>'s thread (an STA thread running a dispatcher).
    /// Throws <see cref="WebView2RuntimeNotFoundException"/> when the runtime is missing.</summary>
    public static async Task<WebView2Channel> CreateAsync(Dispatcher dispatcher, WebView2ChannelOptions options, CancellationToken ct)
    {
        var channel = new WebView2Channel(dispatcher, options);
        try
        {
            await dispatcher.InvokeAsync(channel.InitializeOnUiAsync).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(60), ct);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    private async Task InitializeOnUiAsync()
    {
        Directory.CreateDirectory(_options.UserDataFolder);

        // WS_POPUP | WS_VISIBLE, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: never in the taskbar or Alt+Tab, never takes
        // focus, far off every monitor — but VISIBLE, which is what keeps Chromium painting and ticking rAF.
        var p = new HwndSourceParameters("CluadeX preview host")
        {
            WindowStyle = unchecked((int)(0x80000000 | 0x10000000)),
            ExtendedWindowStyle = 0x00000080 | 0x08000000,
            PositionX = -24000,
            PositionY = -24000,
            Width = _options.Width,
            Height = _options.Height,
        };
        _parking = new HwndSource(p);

        string args = BackgroundFlags + (_options.DisableGpu ? " --disable-gpu" : "")
                      + (string.IsNullOrWhiteSpace(_options.ExtraArguments) ? "" : " " + _options.ExtraArguments.Trim());
        var envOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args };
        _environment = await CoreWebView2Environment.CreateAsync(null, _options.UserDataFolder, envOptions);
        _environment.BrowserProcessExited += OnBrowserProcessExited;

        _controller = await _environment.CreateCoreWebView2ControllerAsync(_parking.Handle);
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.RasterizationScale = 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, _options.Width, _options.Height);
        _controller.DefaultBackgroundColor = System.Drawing.Color.White;
        _controller.IsVisible = true;
        _core = _controller.CoreWebView2;
        try { BrowserProcessId = (int)_core.BrowserProcessId; } catch { }

        var s = _core.Settings;
        s.AreHostObjectsAllowed = false;            // no .NET object reachable from page script
        s.IsWebMessageEnabled = false;              // nor a message channel into CluadeX
        s.AreDefaultScriptDialogsEnabled = false;   // alert() must never wedge the page: ScriptDialogOpening answers
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;             // Ctrl+wheel zoom would shift every coordinate the agent uses
        try
        {
            _core.Profile.IsPasswordAutosaveEnabled = false;
            _core.Profile.IsGeneralAutofillEnabled = false;
        }
        catch { /* older runtime: the Settings switches above already apply */ }

        _core.NavigationStarting += OnNavigationStarting;
        _core.NewWindowRequested += OnNewWindowRequested;
        _core.DownloadStarting += OnDownloadStarting;
        _core.PermissionRequested += OnPermissionRequested;
        _core.ScriptDialogOpening += OnScriptDialogOpening;
        _core.ProcessFailed += OnProcessFailed;
        _core.SourceChanged += OnSourceChanged;
        try { _core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme; } catch { }
        // Every request the page makes passes the request filter (answered locally with a 403 when refused) —
        // including those of its shared workers and service workers (the 2-argument overload covers only requests
        // a document makes: measured, a SharedWorker's and a service worker's fetch() went out unfiltered).
        // WebSockets never reach this event (measured on WebView2 154) — the page-side net guard covers them.
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        _core.WebResourceRequested += OnWebResourceRequested;
        _alive = true;
    }

    /// <summary>Requests to this machine's dev server are by far the most frequent (hundreds of ES modules per
    /// load) and always allowed: recognised by prefix, without parsing, on the UI thread.</summary>
    internal static bool IsFastAllowed(string uri)
    {
        ReadOnlySpan<char> s = uri;
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        s = s[7..];
        int end = s.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? s : s[..end];
        if (authority.Contains('@')) return false;   // http://127.0.0.1@evil.example/ — userinfo, the host is elsewhere
        int colon = authority.StartsWith("[") ? authority.IndexOf("]:") + 1 : authority.LastIndexOf(':');
        var host = colon > 0 ? authority[..colon] : authority;
        if (colon > 0)
            foreach (char c in authority[(colon + 1)..])
                if (c is < '0' or > '9') return false;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.SequenceEqual("127.0.0.1") || host.SequenceEqual("[::1]");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var filter = RequestFilter;
        if (filter == null || _environment == null) return;
        string uri;
        try { uri = e.Request.Uri; } catch { return; }
        if (IsFastAllowed(uri)) return;
        string? reason;
        try { reason = filter(uri, KindOf(e)); }
        catch { reason = "policy error"; }
        if (reason == null) return;
        try { e.Response = BlockedResponse(e.Request); }
        catch (Exception ex) { Debug.WriteLine($"[browser] could not block {uri}: {ex.Message}"); return; }
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.RequestBlocked, reason, uri));
    }

    /// <summary>The request kind the policy judges: the engine's resource context — except for a service worker's
    /// requests, which WebView2 reports as XmlHttpRequest whatever they are (measured on 154).</summary>
    private static string KindOf(CoreWebView2WebResourceRequestedEventArgs e)
    {
        string context = ContextName(e.ResourceContext);
        bool fromServiceWorker = false;
        try { fromServiceWorker = e.RequestedSourceKind == CoreWebView2WebResourceRequestSourceKinds.ServiceWorker; }
        catch { /* older runtime: no source kind — judged by the context alone, as before */ }
        if (!fromServiceWorker) return context;
        string method = "GET", origin = "", referer = "", accept = "";
        try { method = e.Request.Method ?? "GET"; } catch { }
        try
        {
            var h = e.Request.Headers;
            if (h.Contains("Origin")) origin = h.GetHeader("Origin");
            if (h.Contains("Referer")) referer = h.GetHeader("Referer");
            if (h.Contains("Accept")) accept = h.GetHeader("Accept");
        }
        catch { }
        return ServiceWorkerRequestKind(method, origin, referer, accept);
    }

    /// <summary>The policy kind of a request a service worker makes. A pass-through worker (Mock Service Worker's
    /// passthrough(), Workbox routes such as the Google Fonts recipe) relays every request of the page it controls
    /// with fetch(), so the page's own stylesheets, scripts, images and fonts arrive here as the WORKER's requests.
    /// Measured on WebView2 154 (all reported as context XmlHttpRequest, source ServiceWorker):
    /// <list type="bullet">
    /// <item>a no-cors load (&lt;link rel=stylesheet&gt;, classic &lt;script&gt;, &lt;img&gt;, new Image(),
    /// importScripts) is a GET without an Origin header — opaque to the page, the same egress as an &lt;img&gt;: an asset;</item>
    /// <item>a subresource of a resource from ANOTHER site (a font a CDN stylesheet names, a module a CDN module
    /// imports) carries the page's Origin and a Referer of that other site. Script cannot forge a cross-site Referer
    /// (fetch() falls back to its own URL), so the page's fetch() never takes this path: an asset;</item>
    /// <item>everything else — the page's fetch() / XHR relayed with its Origin (CORS), any POST (it always carries
    /// Origin), the worker's own requests — stays a programmatic request ("service worker fetch").</item>
    /// </list>
    /// The kind it names (Stylesheet, Image, Script, Font) is read from Accept, which only names the resource in a
    /// console note — the decision above never rests on it (a page may set Accept on its own fetch).</summary>
    internal static string ServiceWorkerRequestKind(string method, string? origin, string? referer, string? accept)
    {
        const string Programmatic = "service worker fetch";
        bool simple = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        if (!simple) return Programmatic;
        origin = (origin ?? "").Trim();
        string a = (accept ?? "").TrimStart();
        string? named = a.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) ? "Stylesheet"
                      : a.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Image" : null;
        if (origin.Length == 0) return named ?? "Script";
        string? refOrigin = UrlPolicy.OriginOf(referer ?? "");
        string? own = origin == "null" ? null : UrlPolicy.OriginOf(origin);
        if (refOrigin != null && own != null && !string.Equals(refOrigin, own, StringComparison.OrdinalIgnoreCase))
            return named ?? "Font";
        return Programmatic;
    }

    private static string ContextName(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Document => "Document",
        CoreWebView2WebResourceContext.Stylesheet => "Stylesheet",
        CoreWebView2WebResourceContext.Image => "Image",
        CoreWebView2WebResourceContext.Media => "Media",
        CoreWebView2WebResourceContext.Font => "Font",
        CoreWebView2WebResourceContext.Script => "Script",
        CoreWebView2WebResourceContext.XmlHttpRequest => "XmlHttpRequest",
        CoreWebView2WebResourceContext.Fetch => "Fetch",
        CoreWebView2WebResourceContext.TextTrack => "TextTrack",
        CoreWebView2WebResourceContext.EventSource => "EventSource",
        CoreWebView2WebResourceContext.Websocket => "Websocket",
        CoreWebView2WebResourceContext.Manifest => "Manifest",
        CoreWebView2WebResourceContext.SignedExchange => "SignedExchange",
        CoreWebView2WebResourceContext.Ping => "Ping",
        CoreWebView2WebResourceContext.CspViolationReport => "CspViolationReport",
        _ => "Other",
    };

    /// <summary>The local answer to a refused request. It carries the CORS headers the page's own origin needs, so
    /// a cross-origin fetch sees a plain "403 Blocked by CluadeX" — not "TypeError: Failed to fetch" plus a CORS
    /// error that would send the agent chasing a CORS bug that does not exist. A CORS preflight (OPTIONS) is
    /// answered "204, allowed" so the real request comes next and gets the 403. Nothing is ever sent.</summary>
    private CoreWebView2WebResourceResponse BlockedResponse(CoreWebView2WebResourceRequest request)
    {
        string origin = "", method = "GET", requestHeaders = "";
        try { method = request.Method ?? "GET"; } catch { }
        try { if (request.Headers.Contains("Origin")) origin = request.Headers.GetHeader("Origin"); } catch { }
        try { if (request.Headers.Contains("Access-Control-Request-Headers")) requestHeaders = request.Headers.GetHeader("Access-Control-Request-Headers"); } catch { }
        // Header values must stay single-line (a page could send anything in its own headers).
        origin = SingleLine(origin);
        requestHeaders = SingleLine(requestHeaders);
        string cors = origin.Length > 0 && origin != "null"
            ? $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nVary: Origin"
            : "Access-Control-Allow-Origin: *";
        if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return _environment!.CreateWebResourceResponse(null, 204, "No Content",
                cors + "\r\nAccess-Control-Allow-Methods: GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS"
                     + "\r\nAccess-Control-Allow-Headers: " + (requestHeaders.Length > 0 ? requestHeaders : "*")
                     + "\r\nAccess-Control-Max-Age: 0");
        }
        var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Blocked by CluadeX: this site is not approved for the built-in browser."));
        return _environment!.CreateWebResourceResponse(body, 403, "Blocked by CluadeX",
            "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\n" + cors + "\r\nAccess-Control-Expose-Headers: *");
    }

    private static string SingleLine(string s)
    {
        if (s.IndexOfAny(new[] { '\r', '\n' }) >= 0) s = s.Replace("\r", "").Replace("\n", "");
        return s.Length > 500 ? s[..500] : s.Trim();
    }

    // ─────────────────────────── CDP ───────────────────────────

    public Task<JsonElement> SendAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(null, method, parameters, timeout, ct);

    public Task<JsonElement> SendToChildAsync(string sessionId, string method, object? parameters, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(sessionId, method, parameters, timeout, ct);

    private async Task<JsonElement> SendCoreAsync(string? sessionId, string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsAlive) throw new CdpException("The browser engine is not running.");
        string json = SerializeParams(parameters);
        if (timeout <= TimeSpan.Zero) timeout = _options.CallTimeout;

        string result;
        try
        {
            var op = Dispatcher.InvokeAsync(() =>
            {
                if (_core == null || _disposed) throw new CdpException("The browser engine is not running.");
                return sessionId == null
                    ? _core.CallDevToolsProtocolMethodAsync(method, json)
                    : _core.CallDevToolsProtocolMethodForSessionAsync(sessionId, method, json);
            });
            result = await op.Task.Unwrap().WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new TimeoutException($"{method} did not answer within {timeout.TotalSeconds:0.#} s."); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { throw new CdpException("The browser engine shut down."); }
        catch (CdpException) { throw; }
        catch (Exception ex) { throw new CdpException(CleanCdpError(ex.Message), null, ex); }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(result) ? "{}" : result);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex) { throw new CdpException("Unreadable answer from the browser engine.", null, ex); }
    }

    private static readonly JsonSerializerOptions CdpJson = new()
    {
        // CDP rejects "text": null for an optional string — leave unset members out entirely.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>CDP params as JSON: null → "{}", a string is taken as already-serialized JSON.</summary>
    public static string SerializeParams(object? parameters) => parameters switch
    {
        null => "{}",
        string s => s,
        _ => JsonSerializer.Serialize(parameters, CdpJson),
    };

    /// <summary>The CDP error text out of the COM exception message ("…{"code":-32000,"message":"X"}" → "X").</summary>
    internal static string CleanCdpError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "The browser engine refused the command.";
        int brace = message.IndexOf('{');
        if (brace >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(message[brace..]);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? message;
            }
            catch { }
        }
        int nl = message.IndexOf('\n');
        return (nl > 0 ? message[..nl] : message).Trim();
    }

    public void Subscribe(params string[] eventNames)
    {
        var todo = new List<string>();
        lock (_subscribed)
            foreach (var n in eventNames)
                if (_subscribed.Add(n)) todo.Add(n);
        if (todo.Count == 0) return;
        void Do()
        {
            if (_core == null) return;
            foreach (var name in todo)
            {
                var receiver = _core.GetDevToolsProtocolEventReceiver(name);
                string captured = name;
                EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> h = (_, e) =>
                {
                    try
                    {
                        string? session = null;
                        try { session = e.SessionId; } catch { }
                        // Events of auto-attached workers / frames carry their session; the page's own carry none.
                        if (string.IsNullOrEmpty(session)) EventReceived?.Invoke(captured, e.ParameterObjectAsJson);
                        else ChildEventReceived?.Invoke(session, captured, e.ParameterObjectAsJson);
                    }
                    catch (Exception ex) { Debug.WriteLine($"[browser] event handler for {captured} threw: {ex.Message}"); }
                };
                receiver.DevToolsProtocolEventReceived += h;
                _receivers.Add((receiver, h));
            }
        }
        if (Dispatcher.CheckAccess()) Do();
        else Dispatcher.Invoke(Do, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(15));
    }

    // ─────────────────────── host window ───────────────────────

    /// <summary>Show the page inside <paramref name="parent"/> (a preview pane's child window) at
    /// <paramref name="widthPx"/>×<paramref name="heightPx"/> device pixels. Engine thread only.</summary>
    public void AttachTo(IntPtr parent, int widthPx, int heightPx, double rasterizationScale)
    {
        Dispatcher.VerifyAccess();
        if (_controller == null || parent == IntPtr.Zero) return;
        _controller.ParentWindow = parent;
        _attachedTo = parent;
        Resize(widthPx, heightPx, rasterizationScale);
        _controller.IsVisible = true;
    }

    /// <summary>Change the size of the page's window (device pixels). Engine thread only.</summary>
    public void Resize(int widthPx, int heightPx, double rasterizationScale)
    {
        Dispatcher.VerifyAccess();
        if (_controller == null) return;
        widthPx = Math.Clamp(widthPx, 1, 8192);
        heightPx = Math.Clamp(heightPx, 1, 8192);
        if (rasterizationScale > 0 && Math.Abs(_controller.RasterizationScale - rasterizationScale) > 0.001)
            _controller.RasterizationScale = rasterizationScale;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, widthPx, heightPx);
        if (_attachedTo != IntPtr.Zero) _controller.NotifyParentWindowPositionChanged();
    }

    /// <summary>Give the page back to the off-screen window — before the pane's window is destroyed, or the
    /// controller would be destroyed with it. Keeps the current size, so the page does not see a resize just
    /// because the user switched CluadeX pages. Engine thread only; never throws.</summary>
    public void Park(IntPtr onlyIfAttachedTo = default)
    {
        if (!Dispatcher.CheckAccess()) return;
        try
        {
            if (_controller == null || _parking == null) return;
            if (onlyIfAttachedTo != IntPtr.Zero && _attachedTo != onlyIfAttachedTo) return;
            _controller.ParentWindow = _parking.Handle;
            _attachedTo = IntPtr.Zero;
            _controller.IsVisible = true;   // still visible — off-screen — so the game keeps running
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] park failed: {ex.Message}"); }
    }

    /// <summary>Move keyboard focus into the page (the user clicked the pane). Engine thread only.</summary>
    public void FocusPage()
    {
        if (!Dispatcher.CheckAccess() || _controller == null) return;
        try { _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
    }

    public bool IsOffScreen => IsParked;

    public async Task KickFrameLoopAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_controller == null) return;
                _controller.IsVisible = false;
                _controller.IsVisible = true;
                if (_attachedTo != IntPtr.Zero) _controller.NotifyParentWindowPositionChanged();
            }).Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] frame-loop kick failed: {ex.Message}"); }
    }

    public double RasterizationScale => _controller?.RasterizationScale ?? 1.0;

    /// <summary>Set the rasterization scale (device pixels per CSS pixel at zoom 1). Engine thread only.</summary>
    public void SetRasterizationScale(double scale)
    {
        Dispatcher.VerifyAccess();
        if (_controller != null && scale > 0) _controller.RasterizationScale = scale;
    }

    // ─────────────────── native policy events ───────────────────

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var filter = NavigationFilter;
        if (filter == null) return;
        string? reason;
        bool redirect = false;
        try { redirect = e.IsRedirected; } catch { }
        try { reason = filter(e.Uri, redirect); }
        catch { reason = "policy error"; }
        if (reason == null) return;
        e.Cancel = true;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.NavigationBlocked, reason, e.Uri));
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Contained: no second window, no real browser. The agent can browser_open the URL if it wants it.
        e.Handled = true;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.PopupBlocked, "window.open / target=_blank", e.Uri));
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
        string uri = "";
        try { uri = e.DownloadOperation?.Uri ?? ""; } catch { }
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.Download, Path.GetFileName(e.ResultFilePath ?? ""), uri));
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.PermissionDenied, e.PermissionKind.ToString(), e.Uri));
    }

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // Returning without Accept() = "Cancel" for confirm/prompt and "OK" for alert — the page goes on.
        // beforeunload is accepted, or the agent could never navigate away from a page that asks.
        string kind = e.Kind switch
        {
            CoreWebView2ScriptDialogKind.Alert => "alert",
            CoreWebView2ScriptDialogKind.Confirm => "confirm",
            CoreWebView2ScriptDialogKind.Prompt => "prompt",
            CoreWebView2ScriptDialogKind.Beforeunload => "beforeunload",
            _ => "dialog",
        };
        if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload) e.Accept();
        string message = e.Message ?? "";
        if (message.Length > 300) message = message[..300] + "…";
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.Dialog, $"{kind} \"{message}\"", e.Uri));
    }

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.ExternalScheme, "external app link not opened", e.Uri));
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _alive = false;
                RaiseNotice(new BrowserNotice(BrowserNoticeKind.BrowserExited, "browser process exited"));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                RaiseNotice(new BrowserNotice(BrowserNoticeKind.RendererCrashed,
                    e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive ? "page unresponsive" : "page crashed"));
                break;
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        try { SourceChanged?.Invoke(_core?.Source ?? ""); } catch { }
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        _alive = false;
        _browserExited.TrySetResult();
    }

    private void RaiseNotice(BrowserNotice notice)
    {
        try { Notice?.Invoke(notice); }
        catch (Exception ex) { Debug.WriteLine($"[browser] notice handler threw: {ex.Message}"); }
    }

    /// <summary>The page's current URL (engine thread).</summary>
    public string CurrentSource => Dispatcher.CheckAccess() ? _core?.Source ?? "" : "";

    /// <summary>Reload with the engine's own reload (the user's reload button). Never throws.</summary>
    public void ReloadFromUi()
    {
        if (!Dispatcher.CheckAccess() || _core == null) return;
        try { _core.Reload(); } catch { }
    }

    // ─────────────────────────── close ───────────────────────────

    /// <summary>Close the page and the engine and wait (bounded) until the browser process has exited, so its
    /// profile folder is released. Safe to call more than once; never throws.</summary>
    public async Task CloseAsync(TimeSpan waitForExit)
    {
        if (_disposed) { await WaitExitAsync(waitForExit).ConfigureAwait(false); return; }
        _disposed = true;
        _alive = false;
        try
        {
            if (Dispatcher.CheckAccess()) CloseOnUi();
            else if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(CloseOnUi).Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] close failed: {ex.Message}"); }
        await WaitExitAsync(waitForExit).ConfigureAwait(false);
    }

    private async Task WaitExitAsync(TimeSpan wait)
    {
        // Never wait on the engine thread: BrowserProcessExited is raised there.
        if (BrowserProcessId == 0 || wait <= TimeSpan.Zero || Dispatcher.CheckAccess()) return;
        try { await _browserExited.Task.WaitAsync(wait).ConfigureAwait(false); } catch { }
    }

    private void CloseOnUi()
    {
        foreach (var (receiver, handler) in _receivers)
            try { receiver.DevToolsProtocolEventReceived -= handler; } catch { }
        _receivers.Clear();
        if (_core != null)
        {
            try
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.NewWindowRequested -= OnNewWindowRequested;
                _core.DownloadStarting -= OnDownloadStarting;
                _core.PermissionRequested -= OnPermissionRequested;
                _core.ScriptDialogOpening -= OnScriptDialogOpening;
                _core.ProcessFailed -= OnProcessFailed;
                _core.SourceChanged -= OnSourceChanged;
                _core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                _core.WebResourceRequested -= OnWebResourceRequested;
            }
            catch { }
        }
        try { _controller?.Close(); } catch { }
        _controller = null;
        _core = null;
        try { _parking?.Dispose(); } catch { }
        _parking = null;
        _attachedTo = IntPtr.Zero;
        // The environment stays referenced until BrowserProcessExited fired (the event needs it).
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            var t = CloseAsync(TimeSpan.Zero);
            if (!Dispatcher.CheckAccess()) t.Wait(TimeSpan.FromSeconds(10));
        }
        catch { }
    }
}
