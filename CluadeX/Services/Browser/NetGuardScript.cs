using System.Text.Json;

namespace CluadeX.Services.Browser;

/// <summary>
/// The page-side half of the built-in browser's egress rule, for the one channel the engine never shows CluadeX:
/// WebSockets (and WebTransport). WebView2 raises no WebResourceRequested for a WebSocket handshake, and
/// Network.setBlockedURLs does not stop one either (both measured on WebView2 154) — so a local page, where
/// browser_eval is free, could otherwise open "wss://anywhere/?data" without asking.
///
/// The guard replaces the WebSocket / WebTransport constructors with ones that refuse a site this chat has not
/// approved: a refused WebSocket behaves exactly like an unreachable server (CONNECTING, then "error" and "close"
/// 1006 — no exception an app would not expect), and the refusal is reported to CluadeX through the
/// <see cref="BindingName"/> binding, where it becomes the same console note as a blocked fetch. This PC
/// (localhost, 127.x, [::1], *.localhost), the page's own origin and the approved origins pass untouched — Vite's
/// HMR socket keeps working. It is installed in every frame of the page (Page.addScriptToEvaluateOnNewDocument,
/// which also covers about:blank frames) and in every dedicated worker (Target.setAutoAttach, the worker is held
/// until the guard ran) — a worker started by a worker included: each held worker auto-attaches its own children
/// before it is let run (browsertest NW1). The native constructor is unreachable afterwards:
/// WebSocket.prototype.constructor points at the guard too.
///
/// What it does not cover, by design and documented: WebSockets opened by a SharedWorker or a service worker (their
/// fetch / XHR are filtered by the engine), and the covert channels any page has — an image URL or a DNS name
/// carrying data — since a page's assets load from anywhere, as in any browser.
/// </summary>
public static class NetGuardScript
{
    /// <summary>The CDP binding the guard reports refusals through: payload {"kind","url"}.</summary>
    public const string BindingName = "__cluadexBlocked";

    /// <summary>The guard for one policy: <paramref name="mode"/> ("ask" / "allow" / "deny" — "allow" installs
    /// nothing) and the extra origins WebSockets may reach (lower-case "scheme://host[:port]", http or https).</summary>
    public static string Build(string mode, IEnumerable<string> allowOrigins)
    {
        string allow = JsonSerializer.Serialize(allowOrigins.Select(o => o.ToLowerInvariant()).Distinct().ToArray());
        return Source.Replace("__CX_MODE__", mode == "allow" ? "allow" : mode == "deny" ? "deny" : "ask")
                     .Replace("__CX_ALLOW__", allow)
                     .Replace("__CX_BINDING__", BindingName);
    }

    private const string Source = """
(() => {
  const g = globalThis;
  if (g.__cxNetGuardInstalled) return;
  try { Object.defineProperty(g, '__cxNetGuardInstalled', { value: true, configurable: false, enumerable: false, writable: false }); } catch { return; }
  const MODE = '__CX_MODE__';
  if (MODE === 'allow') return;
  const ALLOW = new Set(__CX_ALLOW__);
  const loopback = h => {
    h = String(h || '').toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
    return h === 'localhost' || h.endsWith('.localhost') || h === '::1' || /^127\.\d+\.\d+\.\d+$/.test(h) || /^::ffff:127\./.test(h);
  };
  const base = () => { try { return g.location && g.location.href; } catch { return undefined; } };
  const allowed = raw => {
    let u;
    try { u = new URL(String(raw), base()); } catch { return true; }   // the native constructor reports a bad URL itself
    if (u.protocol !== 'ws:' && u.protocol !== 'wss:' && u.protocol !== 'http:' && u.protocol !== 'https:') return true;
    if (loopback(u.hostname)) return true;
    const origin = ((u.protocol === 'ws:' ? 'http:' : u.protocol === 'wss:' ? 'https:' : u.protocol) + '//' + u.host).toLowerCase();
    let own = '';
    try { own = (g.location.protocol + '//' + g.location.host).toLowerCase(); } catch { }
    if (MODE !== 'deny' && origin === own) return true;
    return ALLOW.has(origin);
  };
  const report = (kind, url) => {
    try { const b = g['__CX_BINDING__']; if (typeof b === 'function') b(JSON.stringify({ kind, url: String(url).slice(0, 500) })); } catch { }
  };
  const guard = (name, kind, makeFailed) => {
    const Native = g[name];
    if (typeof Native !== 'function') return;
    const Guarded = new Proxy(Native, {
      construct(target, args, newTarget) {
        if (args.length > 0 && !allowed(args[0])) { report(kind, args[0]); return makeFailed(Native, args); }
        return Reflect.construct(target, args, newTarget === Guarded ? target : newTarget);
      },
    });
    try { Object.defineProperty(Native.prototype, 'constructor', { value: Guarded, writable: true, configurable: true, enumerable: false }); } catch { }
    try { Object.defineProperty(g, name, { value: Guarded, writable: true, configurable: true, enumerable: false }); } catch { }
  };
  // A refused WebSocket behaves like an unreachable server: CONNECTING, then 'error' and 'close' (1006).
  const failedSocket = (Native, args) => {
    const target = new EventTarget();
    const url = (() => { try { return new URL(String(args[0]), base()).href; } catch { return String(args[0]); } })();
    let state = 0, onopen = null, onerror = null, onclose = null, onmessage = null, binaryType = 'blob';
    const sock = Object.create(Native.prototype, {
      url: { get: () => url }, readyState: { get: () => state }, bufferedAmount: { get: () => 0 },
      extensions: { get: () => '' }, protocol: { get: () => '' },
      binaryType: { get: () => binaryType, set: v => { binaryType = String(v); } },
      onopen: { get: () => onopen, set: v => { onopen = v; } }, onerror: { get: () => onerror, set: v => { onerror = v; } },
      onclose: { get: () => onclose, set: v => { onclose = v; } }, onmessage: { get: () => onmessage, set: v => { onmessage = v; } },
      send: { value: () => { if (state === 0) throw new DOMException("Failed to execute 'send' on 'WebSocket': Still in CONNECTING state.", 'InvalidStateError'); } },
      close: { value: () => { state = 3; } },
      addEventListener: { value: (...a) => target.addEventListener(...a) },
      removeEventListener: { value: (...a) => target.removeEventListener(...a) },
      dispatchEvent: { value: e => target.dispatchEvent(e) },
    });
    setTimeout(() => {
      state = 3;
      const err = new Event('error');
      try { if (typeof onerror === 'function') onerror.call(sock, err); } catch (e) { setTimeout(() => { throw e; }); }
      target.dispatchEvent(err);
      const close = new CloseEvent('close', { code: 1006, reason: '', wasClean: false });
      try { if (typeof onclose === 'function') onclose.call(sock, close); } catch (e) { setTimeout(() => { throw e; }); }
      target.dispatchEvent(close);
    }, 0);
    return sock;
  };
  guard('WebSocket', 'WebSocket', failedSocket);
  // WebTransport is promise-based: 'ready' and 'closed' reject, like an unreachable server.
  guard('WebTransport', 'WebTransport', () => {
    const err = new DOMException('Opening handshake failed.', 'NetworkError');
    const ready = Promise.reject(err); ready.catch(() => { });
    const closed = Promise.reject(err); closed.catch(() => { });
    return { ready, closed, close() { }, datagrams: null, createBidirectionalStream: () => ready, createUnidirectionalStream: () => ready };
  });
})();
""";
}
