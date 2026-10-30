using System.Text.Json;

namespace CluadeX.Services.Browser;

/// <summary>
/// One page driven over the Chrome DevTools Protocol. Engine-agnostic: <see cref="WebView2Channel"/> talks to the
/// in-process WebView2 (CallDevToolsProtocolMethodAsync), a CDP WebSocket engine would talk to a headless Edge /
/// Chrome. Everything above it (console, screenshots, input, policy) lives once in <see cref="BrowserSession"/>.
///
/// Threading: every member may be called from any thread. Events are raised on the engine's own thread and their
/// handlers must only enqueue — never block, never call back into the channel synchronously.
/// </summary>
public interface ICdpChannel : IDisposable
{
    /// <summary>"webview2" or "chrome-extension" — shown in tool output and the harness report.</summary>
    string Engine { get; }

    /// <summary>False once the engine is gone (browser process exited, disposed). A crashed renderer is still
    /// alive — the next navigation starts a new one.</summary>
    bool IsAlive { get; }

    /// <summary>Send one CDP command and return its result object. Throws <see cref="CdpException"/> for a
    /// protocol error, <see cref="TimeoutException"/> after <paramref name="timeout"/>, and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.</summary>
    Task<JsonElement> SendAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct);

    /// <summary>Send a CDP command to an auto-attached child target (a worker, an out-of-process frame) by its
    /// flat session id (from Target.attachedToTarget).</summary>
    Task<JsonElement> SendToChildAsync(string sessionId, string method, object? parameters, TimeSpan timeout, CancellationToken ct);

    /// <summary>Start receiving <paramref name="eventNames"/> through <see cref="EventReceived"/> (idempotent).</summary>
    void Subscribe(params string[] eventNames);

    /// <summary>A CDP event of the page itself: (method, params as raw JSON). Raised on the engine thread — enqueue only.</summary>
    event Action<string, string>? EventReceived;

    /// <summary>A CDP event of an auto-attached child target: (session id, method, params as raw JSON).</summary>
    event Action<string, string, string>? ChildEventReceived;

    /// <summary>Something the engine decided on its own: a dialog dismissed, a download cancelled, a pop-up or a
    /// navigation blocked, a permission denied, a crash. Raised on the engine thread.</summary>
    event Action<BrowserNotice>? Notice;

    /// <summary>Asked before every top-level navigation (link click, form submit, location.href, Page.navigate) and
    /// before every redirect of one: (url, isRedirect) → null to allow, or the reason it is blocked. Null filter =
    /// allow everything.</summary>
    Func<string, bool, string?>? NavigationFilter { get; set; }

    /// <summary>Close the page and whatever exists only for it, waiting at most <paramref name="waitForExit"/>. Safe to
    /// call more than once; never throws.</summary>
    Task CloseAsync(TimeSpan waitForExit);

    /// <summary>Asked before every network request the page, its workers, shared workers and service workers make:
    /// (url, resource kind — "Fetch", "XmlHttpRequest", "Image", "Script", …) → null to allow, or the reason it is
    /// blocked (answered locally with a CORS-correct 403, never sent). WebSocket handshakes never reach it (measured
    /// on WebView2) — <see cref="NetGuardScript"/> covers those in the page. Null filter = allow everything.</summary>
    Func<string, string, string?>? RequestFilter { get; set; }
}

/// <summary>An engine that renders into a window which may be off-screen (WebView2's parking host).</summary>
public interface IOffScreenEngine
{
    /// <summary>The page is in the off-screen host right now (no preview pane shows it).</summary>
    bool IsOffScreen { get; }

    /// <summary>Re-assert "visible" so the page's renderer ticks requestAnimationFrame again. Never throws.</summary>
    Task KickFrameLoopAsync();
}

/// <summary>What happened without anyone asking (reported in the next tool result).</summary>
public sealed record BrowserNotice(BrowserNoticeKind Kind, string Text, string? Url = null)
{
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;
}

public enum BrowserNoticeKind
{
    Dialog,             // alert / confirm / prompt / beforeunload — auto-dismissed
    Download,           // cancelled (WebView2), left to Chrome (the user's Chrome)
    PopupBlocked,       // window.open / target=_blank
    NavigationBlocked,  // UrlPolicy refused a navigation
    RequestBlocked,     // a fetch / XHR / WebSocket / beacon to a site this chat has not allowed
    PermissionDenied,   // camera / mic / geolocation / clipboard / notifications
    ExternalScheme,     // mailto:, ms-settings:, … never launched
    RendererCrashed,
    BrowserExited,
}

/// <summary>A CDP command the engine answered with an error ({"code":-32000,"message":"…"}).</summary>
public sealed class CdpException : Exception
{
    public int? Code { get; }
    public CdpException(string message, int? code = null, Exception? inner = null) : base(message, inner) { Code = code; }
}

/// <summary>
/// Turns a reference the agent writes in browser_open ("bg:3") into a URL. Team T2's background-process service
/// registers one (an adapter over its registry) as <c>IBrowserUrlResolver</c>; without any, "bg:N" is refused
/// with "unknown background id".
/// </summary>
public interface IBrowserUrlResolver
{
    /// <summary>True when this resolver knows <paramref name="reference"/> (the text after "bg:"). On success
    /// <paramref name="url"/> is set; on a known-but-unusable reference (exited, no URL yet) return true with
    /// <paramref name="url"/> null and the reason in <paramref name="error"/>.</summary>
    bool TryResolve(string reference, out string? url, out string? error);
}

/// <summary>Shows the page to the user (the PREVIEW split or the pop-out window). The UI layer registers one;
/// harnesses have none.</summary>
public interface IPreviewPresenter
{
    /// <summary>Called after the agent opened a page in chat <paramref name="chatKey"/>: when no preview pane is
    /// visible, open the pop-out window — once per chat, never again after the user closed it in that chat, and
    /// without taking the keyboard focus. Must not block (marshal to the UI thread) and must not throw. Returns false
    /// when the page was left off-screen on purpose (the user closed the window in this chat).</summary>
    bool EnsureVisible(string chatKey);
}
