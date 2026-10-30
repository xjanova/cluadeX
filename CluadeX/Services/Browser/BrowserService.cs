using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services.Chrome;
using Microsoft.Web.WebView2.Core;

namespace CluadeX.Services.Browser;

/// <summary>Options for a <see cref="BrowserService"/> that is not the app's (harnesses).</summary>
public sealed class BrowserServiceOptions
{
    /// <summary>Profile folder; default DataRoot\WebView2.</summary>
    public string? UserDataFolder { get; init; }
    /// <summary>Engine thread; default the app's UI dispatcher, else a dedicated STA thread.</summary>
    public Dispatcher? Dispatcher { get; init; }
    /// <summary>Software rendering. Null = automatic (on while a local model holds the GPU).</summary>
    public bool? DisableGpu { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 800;
    /// <summary>More Chromium switches (harnesses map test host names onto loopback with --host-resolver-rules).</summary>
    public string? ExtraArguments { get; init; }
}

/// <summary>The engine cannot run here; <see cref="Exception.Message"/> is the localized reason with the fix.</summary>
public sealed class BrowserUnavailableException : Exception
{
    public BrowserUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The built-in browser: one page (like Claude Code's preview), created on the first browser_open and kept until
/// browser_close or exit. The page lives in WebView2, or in the user's real Chrome when <c>BrowserEngine</c> is
/// "chrome" (<see cref="ChromeExtensionChannel"/>). Owns the engine thread, the WebView2 profile folder and the page's off-screen host; a
/// preview pane borrows the page with <see cref="AttachHost"/> and returns it with <see cref="ParkHost"/>, so the
/// page (and a game's state) survives CluadeX page switches.
///
/// It also owns WHICH external sites the page may contact: the ones the user typed into the pane (theirs, in any
/// chat), and per chat the ones that chat approved (browser_open asked, or a read/act prompt was answered "Allow").
/// Switching chats swaps the per-chat set instead of clearing it, and the page's own document origin is always
/// allowed for its own requests — so a chat switch never breaks the page that is open.
///
/// Threading: in the app the engine lives on the UI thread (the pane's window is a child of MainWindow, and
/// WebView2 wants the creating and the parent thread to match); without an Application (a console harness) it
/// gets its own STA thread running a dispatcher. Every call into the engine is marshalled with InvokeAsync and a
/// timeout — never .Result/.Wait — so the agent's pool threads cannot deadlock the UI.
/// </summary>
public sealed class BrowserService : IAppShutdownHook, IDisposable
{
    private readonly SettingsService? _settings;
    private readonly BrowserServiceOptions _options;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private Dispatcher? _dispatcher;
    private Thread? _ownThread;
    private ICdpChannel? _channel;
    private BrowserSession? _session;
    private bool _channelGpuOff;
    private IntPtr _wantedHost;
    private (int W, int H, double Scale) _wantedHostSize;
    private int _agentCalls;
    private volatile bool _disposed;

    private readonly ConcurrentDictionary<string, bool> _userOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _chatOrigins = new(StringComparer.Ordinal);
    private volatile string _activeChat = "";

    /// <summary>The value of <c>BrowserEngine</c> that puts the page in the user's real Chrome.</summary>
    public const string ChromeEngine = "chrome";

    public static bool IsChromeEngine(string? engine) => string.Equals(engine?.Trim(), ChromeEngine, StringComparison.OrdinalIgnoreCase);

    /// <summary>The page goes to the user's real Chrome, through CluadeX's own extension.</summary>
    public bool UsesChrome => IsChromeEngine(_settings?.Settings.BrowserEngine);

    /// <summary>The connection to the extension. Wired by DI; null = no Chrome engine.</summary>
    public ChromeBridgeServer? ChromeBridge { get; set; }

    /// <summary>The DI constructor (the app).</summary>
    public BrowserService(SettingsService settings) : this(settings, new BrowserServiceOptions()) { }

    public BrowserService(SettingsService? settings, BrowserServiceOptions options)
    {
        _settings = settings;
        _options = options;
        _dispatcher = options.Dispatcher;
    }

    /// <summary>Session created/closed, URL changed, console changed, agent started/stopped driving.
    /// Raised on any thread — UI listeners marshal.</summary>
    public event Action? StateChanged;

    public BrowserSession? Session => _session is { IsAlive: true } s ? s : null;
    /// <summary>The WebView2 engine, when the page lives in it and it is alive.</summary>
    public WebView2Channel? Channel => _channel is WebView2Channel { IsAlive: true } c ? c : null;
    public bool IsAgentDriving => Volatile.Read(ref _agentCalls) > 0;
    public string UserDataFolder => _options.UserDataFolder
        ?? Path.Combine(_settings?.DataRoot ?? Path.Combine(AppContext.BaseDirectory, "Data"), "WebView2");
    public BrowserEngineInfo EngineInfo => BrowserEngineProbe.Probe();
    /// <summary>The engine renders in software (a local model holds the GPU, or forced).</summary>
    public bool IsSoftwareRendering => _channel != null && _channelGpuOff;

    /// <summary>The user's permission rules for a URL with scope "browse" (PermissionService.FindUrlRule): Allow,
    /// Deny or null (no rule). Wired by DI; null = no rules.</summary>
    public Func<string, PermAction?>? UrlRule { get; set; }

    /// <summary>A local model is loaded or loading on this PC (the GPU guard). Wired by DI to the provider layer;
    /// a running llama-server process counts as well.</summary>
    public Func<bool>? LocalModelLoaded { get; set; }

    /// <summary>The engine thread's dispatcher (created on first use).</summary>
    public Dispatcher Dispatcher => _dispatcher ??= ResolveDispatcher();

    private Dispatcher ResolveDispatcher()
    {
        var app = Application.Current?.Dispatcher;
        if (app != null && !app.HasShutdownStarted) return app;
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "CluadeX browser engine" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        _ownThread = t;
        return ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Marks a tool call in progress (the pane shows "agent is driving").</summary>
    public IDisposable BeginAgentCall()
    {
        if (Interlocked.Increment(ref _agentCalls) == 1) RaiseStateChanged();
        return new Ender(this);
    }

    private sealed class Ender : IDisposable
    {
        private BrowserService? _owner;
        public Ender(BrowserService owner) => _owner = owner;
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            if (o != null && Interlocked.Decrement(ref o._agentCalls) == 0) o.RaiseStateChanged();
        }
    }

    internal void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch (Exception ex) { Debug.WriteLine($"[browser] StateChanged handler threw: {ex.Message}"); }
    }

    /// <summary>Something the page's engine did on its own (a pop-up not opened, a request blocked, a dialog
    /// dismissed). Raised on the engine thread — listeners only store it.</summary>
    public event Action<BrowserNotice>? NoticeRaised;

    private void RaiseNotice(BrowserNotice n)
    {
        try { NoticeRaised?.Invoke(n); } catch (Exception ex) { Debug.WriteLine($"[browser] notice listener threw: {ex.Message}"); }
    }

    // ─────────────────────────── which sites the page may contact ───────────────────────────

    /// <summary>"ask" (default), "allow" or "deny" — Settings ▸ Features ▸ Dev Preview ▸ External sites.</summary>
    public string ExternalMode => (_settings?.Settings.BrowserAllowExternal ?? "ask").Trim().ToLowerInvariant() switch
    {
        "allow" => "allow",
        "deny" => "deny",
        _ => "ask",
    };

    /// <summary>The chat whose approvals the page uses right now (the last chat that drove the browser).</summary>
    public string ActiveChat
    {
        get => _activeChat;
        set => _activeChat = value ?? "";
    }

    /// <summary>Chat <paramref name="chat"/> approved <paramref name="origin"/> (browser_open asked, or the user
    /// answered "Allow" to a read / act prompt on it).</summary>
    public void ApproveForChat(string chat, string origin)
    {
        if (string.IsNullOrEmpty(origin)) return;
        _chatOrigins.GetOrAdd(chat ?? "", _ => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase))[origin] = true;
    }

    public bool IsApprovedForChat(string chat, string origin) =>
        !string.IsNullOrEmpty(origin) && _chatOrigins.TryGetValue(chat ?? "", out var set) && set.ContainsKey(origin);

    /// <summary>The user typed this site into the preview's address bar: theirs, in every chat. (The agent's reads
    /// and actions on it still ask once per chat — the user's logged-in page is not every chat's to read.)</summary>
    public void ApproveByUser(string origin)
    {
        if (!string.IsNullOrEmpty(origin)) _userOrigins[origin] = true;
    }

    public bool IsUserApproved(string origin) => !string.IsNullOrEmpty(origin) && _userOrigins.ContainsKey(origin);

    /// <summary>May the page contact <paramref name="origin"/> right now (the user's sites, the active chat's)?</summary>
    public bool PageMayContact(string origin) => IsUserApproved(origin) || IsApprovedForChat(_activeChat, origin);

    /// <summary>The origins the page-side network guard lets WebSockets reach (besides this PC and the page's own).</summary>
    public IReadOnlyCollection<string> GuardAllowList()
    {
        var all = new SortedSet<string>(_userOrigins.Keys, StringComparer.OrdinalIgnoreCase);
        if (_chatOrigins.TryGetValue(_activeChat, out var set)) all.UnionWith(set.Keys);
        return all;
    }

    /// <summary>The user's browse rule for <paramref name="url"/>; never throws (a broken rule set = no rule).</summary>
    public PermAction? RuleFor(string url)
    {
        try { return UrlRule?.Invoke(url); }
        catch (Exception ex) { Debug.WriteLine($"[browser] URL rule lookup failed: {ex.Message}"); return null; }
    }

    // ─────────────────────────── the page ───────────────────────────

    /// <summary>The page, created on first use. Throws <see cref="BrowserUnavailableException"/> (localized, with
    /// the install command) when no engine can run. <paramref name="forNavigation"/>: the caller is about to load a
    /// new document (browser_open), so a GPU-rendering engine is swapped for a software one if a local model has
    /// started holding the GPU since the engine was created — the page is being replaced anyway.</summary>
    /// <summary>The chat whose agent run last used the page (AgentRunScope.SessionKey); null = the user / no run.
    /// Closing, clearing or deleting that chat closes the page.</summary>
    public string? OwnerSessionKey { get; private set; }

    /// <summary>Close the page when chat <paramref name="sessionKey"/>'s agent was the last to use it. True when closed.</summary>
    public async Task<bool> CloseIfOwnedByAsync(string sessionKey, TimeSpan waitForExit)
    {
        if (string.IsNullOrEmpty(sessionKey) || OwnerSessionKey != sessionKey || Session == null) return false;
        await CloseSessionAsync(waitForExit);
        OwnerSessionKey = null;
        return true;
    }

    public async Task<BrowserSession> GetSessionAsync(CancellationToken ct, bool forNavigation = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserService));
        if (AgentRunScope.Current?.SessionKey is { Length: > 0 } owner) OwnerSessionKey = owner;
        if (Session is { } live && _channel is { IsAlive: true } && !NeedsNewEngine(forNavigation)) return live;

        await _createLock.WaitAsync(ct);
        try
        {
            if (Session is { } again && _channel is { IsAlive: true })
            {
                if (!NeedsNewEngine(forNavigation)) return again;
                Debug.WriteLine("[browser] the engine is not the one wanted any more (a local model holds the GPU, or the setting changed) — restarting the page");
            }
            await DisposeEngineAsync(TimeSpan.FromSeconds(3));   // a dead engine (browser process exited) is replaced

            string engine = (_settings?.Settings.BrowserEngine ?? "auto").Trim().ToLowerInvariant();
            if (engine == "cdp")
                throw new BrowserUnavailableException(L("browser.engine.cdpUnavailable"));

            bool disableGpu = false;
            ICdpChannel channel;
            if (engine == ChromeEngine) channel = await OpenChromeAsync(ct);
            else
            {
                disableGpu = _options.DisableGpu ?? LocalModelHoldsGpu();
                channel = await CreateWebView2Async(disableGpu, ct);
            }

            var session = new BrowserSession(channel)
            {
                ExternalMode = () => ExternalMode,
                IsOriginApproved = PageMayContact,
                UrlRule = RuleFor,
                GuardAllowList = GuardAllowList,
                RedirectApproved = OnRedirectApproved,
            };
            try { await session.InitializeAsync(ct); }
            catch
            {
                session.Dispose();
                await channel.CloseAsync(TimeSpan.FromSeconds(3));
                throw;
            }
            session.Changed += RaiseStateChanged;
            session.NoticeRaised += RaiseNotice;
            if (channel is WebView2Channel webView) webView.SourceChanged += _ => RaiseStateChanged();
            _channel = channel;
            _channelGpuOff = disableGpu;
            _session = session;

            // A pane that was waiting for the page gets it now.
            if (_wantedHost != IntPtr.Zero && channel is WebView2Channel shown)
            {
                var host = _wantedHost; var size = _wantedHostSize;
                await Dispatcher.InvokeAsync(() => { try { shown.AttachTo(host, size.W, size.H, size.Scale); } catch (Exception ex) { Debug.WriteLine($"[browser] attach failed: {ex.Message}"); } }).Task;
            }
            RaiseStateChanged();
            return session;
        }
        finally { _createLock.Release(); }
    }

    private async Task<WebView2Channel> CreateWebView2Async(bool disableGpu, CancellationToken ct)
    {
        var info = BrowserEngineProbe.Probe(refresh: true);
        if (!info.HasWebView2)
            throw new BrowserUnavailableException(string.Format(L("browser.engine.missing"), BrowserEngineProbe.InstallCommand));
        try
        {
            return await WebView2Channel.CreateAsync(Dispatcher, new WebView2ChannelOptions
            {
                UserDataFolder = UserDataFolder, Width = _options.Width, Height = _options.Height, DisableGpu = disableGpu,
                ExtraArguments = _options.ExtraArguments,
            }, ct);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new BrowserUnavailableException(string.Format(L("browser.engine.missing"), BrowserEngineProbe.InstallCommand), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[browser] engine start failed: {ex}");
            throw new BrowserUnavailableException(L("browser.engine.startFailed"), ex);
        }
    }

    /// <summary>A window in the user's Chrome. A moment's grace for a worker Chrome is just waking; never a silent
    /// fall back to WebView2, which has none of the user's logins.</summary>
    private async Task<ChromeExtensionChannel> OpenChromeAsync(CancellationToken ct)
    {
        var bridge = ChromeBridge;
        if (bridge == null || !await bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(3), ct))
            throw new BrowserUnavailableException(L("browser.chrome.notConnected"));
        if (bridge.Paused) throw new BrowserUnavailableException(L("browser.chrome.paused"));
        try { return await ChromeExtensionChannel.OpenAsync(bridge, _options.Width, _options.Height, ct); }
        catch (Exception ex) when (ex is CdpException or TimeoutException)
        {
            Debug.WriteLine($"[browser] Chrome window failed: {ex}");
            throw new BrowserUnavailableException(string.Format(L("browser.chrome.openFailed"), ex.Message), ex);
        }
    }

    /// <summary>A same-site redirect of an approved page (http → https, example.com → www.example.com) carries the
    /// approval along: the user's own if the user typed the source, the active chat's otherwise.</summary>
    private void OnRedirectApproved(string fromOrigin, string toOrigin)
    {
        if (IsUserApproved(fromOrigin)) ApproveByUser(toOrigin);
        if (IsApprovedForChat(_activeChat, fromOrigin)) ApproveForChat(_activeChat, toOrigin);
    }

    private bool NeedsSoftwareSwap() =>
        _channel is WebView2Channel && _options.DisableGpu == null && !_channelGpuOff && LocalModelHoldsGpu();

    /// <summary>The live engine is not the one to use now: the Chrome switch moved, or (before a load, when the page is
    /// being replaced anyway) a local model started holding the GPU.</summary>
    private bool NeedsNewEngine(bool forNavigation) =>
        UsesChrome != _channel is ChromeExtensionChannel || (forNavigation && NeedsSoftwareSwap());

    /// <summary>Close the page and the engine (browser_close, the pane's Close button, Dev Preview switched off).
    /// The next browser_open starts a fresh one.</summary>
    public async Task CloseSessionAsync(TimeSpan waitForExit)
    {
        await _createLock.WaitAsync();
        try { await DisposeEngineAsync(waitForExit); }
        finally { _createLock.Release(); }
        RaiseStateChanged();
    }

    private async Task DisposeEngineAsync(TimeSpan waitForExit)
    {
        var session = Interlocked.Exchange(ref _session, null);
        var channel = Interlocked.Exchange(ref _channel, null);
        if (session != null) { session.Changed -= RaiseStateChanged; session.NoticeRaised -= RaiseNotice; session.Dispose(); }
        if (channel != null) await channel.CloseAsync(waitForExit).ConfigureAwait(false);
    }

    // ─────────────────────────── preview hosts (UI thread) ───────────────────────────

    /// <summary>A preview pane wants to show the page in <paramref name="hwnd"/> (device pixels). Remembered, so
    /// a page created later lands in it. Engine (UI) thread; never throws.</summary>
    public void AttachHost(IntPtr hwnd, int widthPx, int heightPx, double scale)
    {
        _wantedHost = hwnd;
        _wantedHostSize = (widthPx, heightPx, scale);
        try { if (Channel is { } c && c.Dispatcher.CheckAccess()) c.AttachTo(hwnd, widthPx, heightPx, scale); }
        catch (Exception ex) { Debug.WriteLine($"[browser] attach failed: {ex.Message}"); }
        RaiseStateChanged();
    }

    /// <summary>The pane at <paramref name="hwnd"/> resized. Engine (UI) thread; never throws (the engine may have
    /// died a moment before its exit event arrived — a COM error must never escape a WPF layout pass).</summary>
    public void ResizeHost(IntPtr hwnd, int widthPx, int heightPx, double scale)
    {
        if (_wantedHost != hwnd) return;
        _wantedHostSize = (widthPx, heightPx, scale);
        try { if (Channel is { } c && c.Dispatcher.CheckAccess() && !c.IsParked) c.Resize(widthPx, heightPx, scale); }
        catch (Exception ex) { Debug.WriteLine($"[browser] resize failed: {ex.Message}"); }
    }

    /// <summary>The pane at <paramref name="hwnd"/> is going away: the page goes back to the off-screen host
    /// (only if that pane still has it). Engine (UI) thread; never throws.</summary>
    public void ParkHost(IntPtr hwnd)
    {
        if (_wantedHost == hwnd) _wantedHost = IntPtr.Zero;
        try { (_channel as WebView2Channel)?.Park(hwnd); } catch { }
        RaiseStateChanged();
    }

    /// <summary>Which pane window shows the page now (IntPtr.Zero = none — off-screen).</summary>
    public IntPtr ActiveHost => _channel is WebView2Channel { IsParked: false } c ? c.HostWindow : IntPtr.Zero;

    // ─────────────────────────── GPU ───────────────────────────

    /// <summary>A local model is loaded or loading — in-process (LLamaSharp) or a llama-server: render the page in
    /// software rather than push VRAM over the edge (the 2026-09-24 PC restart).</summary>
    private bool LocalModelHoldsGpu()
    {
        try { if (LocalModelLoaded?.Invoke() == true) return true; }
        catch (Exception ex) { Debug.WriteLine($"[browser] local model probe failed: {ex.Message}"); }
        try
        {
            var procs = Process.GetProcessesByName("llama-server");
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
        catch { return false; }
    }

    // ─────────────────────────── shutdown ───────────────────────────

    /// <summary>App exit: close the engine, bounded (the browser process exits on its own once its last view is
    /// closed; we don't hold the exit for it).</summary>
    public void OnAppShutdown()
    {
        if (_disposed) return;
        _disposed = true;
        if (_channel == null && _session == null) { StopOwnThread(); return; }   // never started: nothing to close
        try
        {
            var t = DisposeEngineAsync(TimeSpan.Zero);
            if (_dispatcher != null && !_dispatcher.CheckAccess()) t.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { Debug.WriteLine($"[browser] shutdown: {ex.Message}"); }
        StopOwnThread();
    }

    /// <summary>Close the engine and wait (up to 5 s) for its browser process to exit — harnesses check that no
    /// msedgewebview2.exe survives for the profile folder.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        // On the engine thread itself a blocking wait would starve the very events it waits for.
        if (_dispatcher != null && _dispatcher.CheckAccess()) { OnAppShutdown(); return; }
        _disposed = true;
        // The create lock is never disposed: a caller still waiting on it must get through, not hang forever.
        if (_channel == null && _session == null) { StopOwnThread(); return; }
        try { DisposeEngineAsync(TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(12)); }
        catch (Exception ex) { Debug.WriteLine($"[browser] dispose: {ex.Message}"); }
        StopOwnThread();
    }

    private void StopOwnThread()
    {
        if (_ownThread == null || _dispatcher == null) return;
        try { _dispatcher.InvokeShutdown(); } catch { }
        try { _ownThread.Join(TimeSpan.FromSeconds(3)); } catch { }
        _ownThread = null;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
}
