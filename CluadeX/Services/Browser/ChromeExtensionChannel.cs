using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Chrome;

namespace CluadeX.Services.Browser;

/// <summary>
/// One page in the user's real Chrome, in a window of its own, reached through the CluadeX Bridge extension and
/// chrome.debugger. Same profile, same logins, no Claude account or claude.exe in between. Everything above it
/// (console, screenshots, input, approvals) is <see cref="BrowserSession"/>, as on WebView2.
///
/// WebView2's native events do not exist here, so the policy runs on CDP: the Fetch domain holds every request of the
/// page and its workers until <see cref="NavigationFilter"/> and <see cref="RequestFilter"/> have answered. Chrome
/// does not show a page's service worker to chrome.debugger, so that worker's own requests pass unjudged (WebView2
/// judges them).
/// </summary>
public sealed class ChromeExtensionChannel : ICdpChannel
{
    public const string EngineName = "chrome-extension";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    // What this class handles itself, on top of what BrowserSession subscribes to.
    private static readonly string[] OwnEvents =
    {
        "Fetch.requestPaused", "Page.javascriptDialogOpening", "Page.frameNavigated", "Page.downloadWillBegin", "Target.attachedToTarget", "Log.entryAdded",
    };

    // A ctrl-, shift- or middle-click on a link is the browser's own "new tab": the extension's sandbox does not stop it
    // and its tab is not under the debugger. Only the agent's clicks can do this (a page cannot make a trusted click).
    private const string NoNewTabScript = """
        for (const type of ['click', 'auxclick'])
          addEventListener(type, e => {
            if ((e.button === 1 || e.ctrlKey || e.metaKey || e.shiftKey) && e.composedPath().some(n => n.matches?.('a[href], area[href]'))) e.preventDefault();
          }, true);
        """;

    private readonly ChromeBridgeServer _bridge;
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private volatile string? _mainFrameId;
    private volatile bool _alive;
    private volatile bool _closed;

    public string Engine => EngineName;
    public bool IsAlive => _alive && !_closed;

    public event Action<string, string>? EventReceived;
    public event Action<string, string, string>? ChildEventReceived;
    public event Action<BrowserNotice>? Notice;
    public Func<string, bool, string?>? NavigationFilter { get; set; }
    public Func<string, string, string?>? RequestFilter { get; set; }

    private ChromeExtensionChannel(ChromeBridgeServer bridge) => _bridge = bridge;

    /// <summary>Open the window and start holding its requests. Throws <see cref="CdpException"/> when the extension
    /// refuses (paused, the user's policy) and <see cref="TimeoutException"/> when Chrome does not answer.</summary>
    public static async Task<ChromeExtensionChannel> OpenAsync(ChromeBridgeServer bridge, int width, int height, CancellationToken ct)
    {
        var channel = new ChromeExtensionChannel(bridge);
        bridge.MessageReceived += channel.OnBridgeMessage;
        bridge.Disconnected += channel.OnBridgeLost;
        try
        {
            await bridge.RequestAsync("open", w => { w.WriteNumber("width", width); w.WriteNumber("height", height); }, TimeSpan.FromSeconds(30), ct);
            channel._alive = true;
            channel.Subscribe();
            await channel.SendAsync("Page.enable", null, TimeSpan.FromSeconds(30), ct);
            await channel.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = NoNewTabScript }, DefaultTimeout, ct);
            var tree = await channel.SendAsync("Page.getFrameTree", null, DefaultTimeout, ct);
            channel._mainFrameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            await channel.EnableFetchAsync(null, ct);
            return channel;
        }
        catch
        {
            await channel.CloseAsync(TimeSpan.FromSeconds(3));
            throw;
        }
    }

    private Task EnableFetchAsync(string? sessionId, CancellationToken ct) => SendCoreAsync(sessionId, "Fetch.enable",
        new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, DefaultTimeout, ct);

    // ─────────────────────────── CDP ───────────────────────────

    public Task<JsonElement> SendAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(null, method, parameters, timeout, ct);

    public Task<JsonElement> SendToChildAsync(string sessionId, string method, object? parameters, TimeSpan timeout, CancellationToken ct) =>
        SendCoreAsync(sessionId, method, parameters, timeout, ct);

    private async Task<JsonElement> SendCoreAsync(string? sessionId, string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        switch (method)
        {
            // chrome.debugger has no browser-level commands; the extension told us the user agent when it connected.
            case "Browser.getVersion":
                return JsonSerializer.SerializeToElement(new { userAgent = _bridge.UserAgent });
            // browser_open fresh=true on a dev server. Here it would sign the user out of a real site.
            case "Network.clearBrowserCache":
            case "Storage.clearDataForOrigin":
                RaiseNotice(new BrowserNotice(BrowserNoticeKind.PermissionDenied, "fresh=true does nothing in the user's real Chrome: clearing a site's data would sign them out"));
                throw new CdpException(method + " is not allowed on the user's real Chrome profile.");
        }
        if (!IsAlive) throw new CdpException("Chrome is not connected.");
        if (timeout <= TimeSpan.Zero) timeout = DefaultTimeout;
        return await _bridge.RequestAsync("cdp", w => WriteCommand(w, sessionId, method, parameters), timeout, ct).ConfigureAwait(false);
    }

    private static void WriteCommand(Utf8JsonWriter w, string? sessionId, string method, object? parameters)
    {
        w.WriteString("method", method);
        if (sessionId != null) w.WriteString("sessionId", sessionId);
        w.WritePropertyName("params");
        w.WriteRawValue(WebView2Channel.SerializeParams(parameters), skipInputValidation: true);
    }

    /// <summary>An answer to a held request: order matters and nobody waits for it.</summary>
    private void Answer(string? sessionId, string method, object parameters) =>
        _bridge.Post("cdp", w => WriteCommand(w, sessionId, method, parameters));

    public void Subscribe(params string[] eventNames)
    {
        string[] all;
        lock (_subscribed)
        {
            foreach (string name in eventNames) _subscribed.Add(name);
            all = _subscribed.Concat(OwnEvents).Distinct(StringComparer.Ordinal).ToArray();
        }
        _bridge.Post("subscribe", w =>
        {
            w.WriteStartArray("events");
            foreach (string name in all) w.WriteStringValue(name);
            w.WriteEndArray();
        });
    }

    // ─────────────────────────── from the extension ───────────────────────────

    private void OnBridgeMessage(JsonElement m)
    {
        switch (ConsoleBuffer.Str(m, "ev"))
        {
            case "cdp":
                OnCdpEvent(ConsoleBuffer.Str(m, "sessionId"), ConsoleBuffer.Str(m, "method") ?? "", m.TryGetProperty("params", out var p) ? p : default);
                break;
            case "popup":
                RaiseNotice(new BrowserNotice(BrowserNoticeKind.PopupBlocked, "window.open / target=_blank", ConsoleBuffer.Str(m, "url")));
                break;
            case "detached":
                OnDetached(ConsoleBuffer.Str(m, "reason"));
                break;
        }
    }

    private void OnCdpEvent(string? sessionId, string method, JsonElement p)
    {
        try
        {
            switch (method)
            {
                case "Fetch.requestPaused":
                    OnRequestPaused(sessionId, p);
                    return;
                case "Page.javascriptDialogOpening":
                    OnDialog(p);
                    return;
                case "Page.downloadWillBegin":
                    RaiseNotice(new BrowserNotice(BrowserNoticeKind.Download, "Chrome is saving this file itself, and may ask the user where: " + ConsoleBuffer.Str(p, "suggestedFilename"), ConsoleBuffer.Str(p, "url")));
                    return;
                case "Page.frameNavigated":
                    if (sessionId == null && p.TryGetProperty("frame", out var f) && !f.TryGetProperty("parentId", out _)) _mainFrameId = ConsoleBuffer.Str(f, "id");
                    break;
                case "Log.entryAdded":
                    if (BlockedPopup(p) is { } popup) RaiseNotice(new BrowserNotice(BrowserNoticeKind.PopupBlocked, "window.open / target=_blank", popup));
                    break;
                case "Target.attachedToTarget":
                    // Sent first so that it reaches the child before BrowserSession releases it.
                    if (ConsoleBuffer.Str(p, "sessionId") is { } child)
                    {
                        Answer(child, "Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } });
                        if (p.TryGetProperty("targetInfo", out var info) && ConsoleBuffer.Str(info, "type") == "iframe")
                            Answer(child, "Page.addScriptToEvaluateOnNewDocument", new { source = NoNewTabScript });
                    }
                    break;
            }
            bool wanted;
            lock (_subscribed) wanted = _subscribed.Contains(method);
            if (!wanted) return;
            string json = p.ValueKind == JsonValueKind.Undefined ? "{}" : p.GetRawText();
            if (sessionId == null) EventReceived?.Invoke(method, json);
            else ChildEventReceived?.Invoke(sessionId, method, json);
        }
        catch (Exception ex) { Debug.WriteLine($"[chrome] {method} handling failed: {ex.Message}"); }
    }

    /// <summary>The extension's sandbox stops a popup inside the page, and the console line is all that says so.</summary>
    private static string? BlockedPopup(JsonElement p)
    {
        const string Start = "Blocked opening '";
        string text = p.TryGetProperty("entry", out var entry) ? ConsoleBuffer.Str(entry, "text") ?? "" : "";
        int end = text.StartsWith(Start, StringComparison.Ordinal) ? text.IndexOf('\'', Start.Length) : -1;
        return end < 0 ? null : text[Start.Length..end];
    }

    private void OnRequestPaused(string? sessionId, JsonElement p)
    {
        string? requestId = ConsoleBuffer.Str(p, "requestId");
        if (requestId == null || !p.TryGetProperty("request", out var request)) return;
        string url = ConsoleBuffer.Str(request, "url") ?? "";
        string type = ConsoleBuffer.Str(p, "resourceType") ?? "Other";
        bool topLevel = sessionId == null && type == "Document" && ConsoleBuffer.Str(p, "frameId") == _mainFrameId;

        string? reason;
        var kind = BrowserNoticeKind.RequestBlocked;
        try
        {
            reason = topLevel ? NavigationFilter?.Invoke(url, p.TryGetProperty("redirectedRequestId", out _)) : null;
            if (reason != null) kind = BrowserNoticeKind.NavigationBlocked;
            else if (!WebView2Channel.IsFastAllowed(url)) reason = RequestFilter?.Invoke(url, type == "XHR" ? "XmlHttpRequest" : type);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[chrome] policy threw: {ex.Message}");
            reason = "policy error";
        }

        if (reason == null) Answer(sessionId, "Fetch.continueRequest", new { requestId });
        else
        {
            // 204 on a navigation leaves the page where it is, as a cancelled navigation does on WebView2. Failing the
            // request would replace it with an error page.
            Answer(sessionId, "Fetch.fulfillRequest", topLevel ? new { requestId, responseCode = 204 } : Blocked(requestId, request));
            RaiseNotice(new BrowserNotice(kind, reason, url));
        }
    }

    private static object Blocked(string requestId, JsonElement request)
    {
        string Header(string name)
        {
            if (!request.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object) return "";
            foreach (var h in headers.EnumerateObject())
                if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return h.Value.GetString() ?? "";
            return "";
        }
        var answer = WebView2Channel.BlockedAnswer(ConsoleBuffer.Str(request, "method") ?? "GET", Header("Origin"), Header("Access-Control-Request-Headers"));
        return new
        {
            requestId,
            responseCode = answer.Status,
            responsePhrase = answer.Phrase,
            responseHeaders = answer.Headers.Split("\r\n").Select(line => line.Split(": ", 2)).Select(h => new { name = h[0], value = h[1] }).ToArray(),
            body = answer.Body == null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(answer.Body)),
        };
    }

    /// <summary>As on WebView2: alert is answered OK, confirm and prompt Cancel, and beforeunload is accepted, or the
    /// agent could never leave a page that asks.</summary>
    private void OnDialog(JsonElement p)
    {
        string kind = ConsoleBuffer.Str(p, "type") ?? "dialog";
        Answer(null, "Page.handleJavaScriptDialog", new { accept = kind is "alert" or "beforeunload" });
        string message = ConsoleBuffer.Str(p, "message") ?? "";
        if (message.Length > 300) message = message[..300] + "…";
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.Dialog, $"{kind} \"{message}\"", ConsoleBuffer.Str(p, "url")));
    }

    private void OnDetached(string? reason)
    {
        if (!_alive) return;
        _alive = false;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.BrowserExited, reason switch
        {
            "paused" or "canceled_by_user" => "the user stopped CluadeX in Chrome",
            "target_closed" => "the Chrome window was closed",
            _ => "Chrome ended the connection: " + reason,
        }));
    }

    private void OnBridgeLost()
    {
        if (!_alive) return;
        _alive = false;
        RaiseNotice(new BrowserNotice(BrowserNoticeKind.BrowserExited, "Chrome disconnected"));
    }

    private void RaiseNotice(BrowserNotice notice)
    {
        try { Notice?.Invoke(notice); }
        catch (Exception ex) { Debug.WriteLine($"[chrome] notice handler threw: {ex.Message}"); }
    }

    // ─────────────────────────── close ───────────────────────────

    /// <summary>Close the agent's tab, which ends its window (a tab the user added keeps it open). Never throws.</summary>
    public async Task CloseAsync(TimeSpan waitForExit)
    {
        if (_closed) return;
        _closed = true;
        bool open = _alive;
        _alive = false;
        _bridge.MessageReceived -= OnBridgeMessage;
        _bridge.Disconnected -= OnBridgeLost;
        if (!open || waitForExit <= TimeSpan.Zero) { if (open) _bridge.Post("close"); return; }
        try { await _bridge.RequestAsync("close", null, waitForExit, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is CdpException or TimeoutException) { Debug.WriteLine($"[chrome] close: {ex.Message}"); }
    }

    public void Dispose() => _ = CloseAsync(TimeSpan.FromSeconds(3));
}
