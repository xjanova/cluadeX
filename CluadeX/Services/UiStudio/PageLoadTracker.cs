using System.Text.Json;

namespace CluadeX.Services.UiStudio;

/// <summary>Where the Studio's page is in loading. Only <see cref="Loaded"/> and <see cref="LoadedWithHttpError"/>
/// mean the page is really there: the document finished, the overlay runs in it and its body shows something.</summary>
public enum PageLoadState { Idle, Navigating, Verifying, Loaded, LoadedWithHttpError, Retrying, Failed }

/// <summary>Why a load ended badly (or with an HTTP error page).</summary>
public enum PageLoadProblem { None, Unreachable, HttpError, Blank, NoOverlay, BuildError, Error }

/// <summary>What the Studio's page loading shows now (a snapshot: the view model reads it, nothing in it changes).</summary>
public sealed record PageLoadInfo(PageLoadState State, PageLoadProblem Problem = PageLoadProblem.None, int Attempt = 0, int MaxAttempts = 0,
    int HttpStatus = 0, string? Detail = null, string? Url = null)
{
    public static readonly PageLoadInfo Idle = new(PageLoadState.Idle);

    /// <summary>Something is still on its way (the toolbar shows a thin progress bar, never a full-screen spinner).</summary>
    public bool IsBusy => State is PageLoadState.Navigating or PageLoadState.Verifying or PageLoadState.Retrying;

    /// <summary>A failure that leaves nothing worth looking at in the browser (its own error page, a blank page): the
    /// view hides the page and shows a card with "Try again". A build error or an HTTP error page stays on screen —
    /// the page itself says what is wrong.</summary>
    public bool HidesPage => State == PageLoadState.Failed && Problem is PageLoadProblem.Unreachable or PageLoadProblem.Blank or PageLoadProblem.Error;
}

public enum PageLoadCommandKind
{
    /// <summary>Navigate the browser to <see cref="PageLoadCommand.Url"/> (a retry, or the wanted page again).</summary>
    Navigate,
    /// <summary>Reload the page that is shown.</summary>
    Reload,
    /// <summary>Run overlay.js in the page now (it guards itself: running it twice does nothing).</summary>
    InjectOverlay,
    /// <summary>The load is verified: ask the overlay for its element tree (a tree sent before the page counted as this
    /// project's was dropped), and do the "ready" work if the ready message never reached the host.</summary>
    RequestTree,
    /// <summary>Run <see cref="PageProbe.Script"/> and hand the result back with <see cref="PageLoadTracker.Probed"/> and this token.</summary>
    Probe,
}

public readonly record struct PageLoadCommand(PageLoadCommandKind Kind, string? Url = null, long Token = 0);

/// <summary>What the page says about itself when probed after its navigation completed.</summary>
public sealed record PageProbe(string ReadyState, bool HasOverlay, int BodyElements, int TextLength, bool RootEmpty, bool BuildError, string? Href)
{
    /// <summary>The probe the view runs in the page. Its result is an object, so ExecuteScriptAsync returns it as JSON.</summary>
    public const string Script = @"(() => { const b = document.body, r = document.querySelector('#root, #app, #__next');
  return { rs: document.readyState, cx: !!window.__cx, b: b ? b.childElementCount : -1,
    t: b ? Math.min((b.innerText || '').trim().length, 100000) : 0,
    root: !!r && r.childElementCount === 0 && !(r.textContent || '').trim(),
    err: !!document.querySelector('vite-error-overlay, #webpack-dev-server-client-overlay'),
    href: String(location.href) }; })()";

    /// <summary>The body shows something: some text, or elements while no SPA root (#root, #app, #__next) is still empty.</summary>
    public bool HasContent => TextLength > 0 || BodyElements > 0 && !RootEmpty;

    /// <summary>The JSON ExecuteScriptAsync returned for <see cref="Script"/>; null when it is not that (the page was
    /// gone, the script failed, "null").</summary>
    public static PageProbe? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("rs", out var rs) || rs.ValueKind != JsonValueKind.String) return null;
            static bool B(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            static int I(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
            string? href = o.TryGetProperty("href", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            return new PageProbe(rs.GetString() ?? "", B(o, "cx"), I(o, "b"), I(o, "t"), B(o, "root"), B(o, "err"), href);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Makes "the page loaded" mean the page really is there, and gets it there when it is not. The view feeds it the
/// browser's events (navigation started, content loading, completed, the overlay's "ready", probe results, shown /
/// hidden) and runs the commands it raises; it touches no WebView2 and reads no clock — every input carries the time,
/// and <see cref="NextDue"/> says when <see cref="Tick"/> is wanted next (the view drives one timer from it).
///
///   · a server that is not up yet (connection refused, timed out, a dev server's 502/503/504) is tried again after
///     0.5 / 1 / 2 / 4 / 8 s — six attempts in all — and then reported as unreachable;
///   · an HTTP error page with a body (a Laravel 500, a static 404) is this project's page: loaded, with the code,
///     no retry;
///   · a page the overlay is missing from gets overlay.js run in it, then one reload, then "no overlay";
///   · a body still empty after <see cref="BlankGrace"/> gets one reload, then "blank";
///   · a Vite / webpack error overlay is a build error: reported, never retried (reloading cannot fix the code);
///   · every Studio navigation is a new generation, and any newer navigation cancels what an older one had pending;
///   · while the page is hidden nothing is due (no retries counted against an unseen, possibly suspended page);
///     when it is shown again the load is checked once more.
/// UI thread only.
/// </summary>
public sealed class PageLoadTracker
{
    /// <summary>The waits before attempts 2…6.</summary>
    public static readonly TimeSpan[] Backoff =
        { TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };
    public const int MaxAttempts = 6;
    /// <summary>How long a page may stay blank (an SPA still rendering into an empty #root) before it is reloaded.</summary>
    public static readonly TimeSpan BlankGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ProbeAfterInject = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ProbeRetry = TimeSpan.FromSeconds(1);
    private const int MaxProbeFailures = 3;

    public const string BlankPage = "about:blank";

    /// <summary>Raised for every command, in order, after the tracker's own state is settled (a handler may call back in).</summary>
    public event Action<PageLoadCommand>? Command;
    /// <summary>Raised when <see cref="Info"/> changed.</summary>
    public event Action? Changed;

    public PageLoadState State { get; private set; }
    public PageLoadProblem Problem { get; private set; }
    /// <summary>The attempt at the current URL (1 = the first).</summary>
    public int Attempt { get; private set; }
    public int HttpStatus { get; private set; }
    public string? Detail { get; private set; }
    /// <summary>Bumped by every Studio navigation (<see cref="Want"/>).</summary>
    public int Generation { get; private set; }
    /// <summary>The page the Studio asked for last (null before any).</summary>
    public string? WantedUrl { get; private set; }
    /// <summary>The URL of the navigation followed now (a link the user clicked, a redirect target…).</summary>
    public string? CurrentUrl { get; private set; }
    public ulong? NavigationId { get; private set; }
    /// <summary>The overlay said "ready" for the current document.</summary>
    public bool OverlayIsReady { get; private set; }
    public bool IsPaused => _hidden;
    /// <summary>When <see cref="Tick"/> has work; null when nothing is due (or the page is hidden).</summary>
    public DateTime? NextDue => _hidden ? null : _due;

    public PageLoadInfo Info => State == PageLoadState.Idle ? PageLoadInfo.Idle
        : new PageLoadInfo(State, Problem, Attempt, MaxAttempts, HttpStatus, Detail, CurrentUrl);

    private enum DueKind { None, Retry, Probe }

    private DateTime? _due;
    private DueKind _dueKind;
    private bool _hidden;
    private DateTime? _pausedAt;
    private bool _expectOurs;
    private bool _committed, _completed, _errorPage, _rechecking;
    private DateTime? _verifyStart;
    private long _probeToken;
    private int _probeFails;
    // per chain of loads of one URL (kept across the tracker's own reloads, so they cannot loop)
    private bool _injected, _reloadedForOverlay, _reloadedForBlank;
    private readonly List<PageLoadCommand> _out = new();
    private bool _dirty, _flushing;

    // ───────────────────────────── what the Studio asks ─────────────────────────────

    /// <summary>The Studio sends the browser to <paramref name="url"/> (the view navigates right after).</summary>
    public void Want(string url, DateTime now)
    {
        Generation++;
        WantedUrl = url;
        Begin(url);
        Flush();
    }

    /// <summary>The page shown is reloaded by the user or the file watcher (the view reloads right after).</summary>
    public void Reloading(DateTime now)
    {
        NewChain();
        Attempt = 1;
        _expectOurs = true;
        ClearDue();
        if (!IsBlank(CurrentUrl)) Set(PageLoadState.Navigating);
        Flush();
    }

    /// <summary>The view goes to <paramref name="url"/> again in place of a reload (the page shown is blank or the
    /// browser's error page) or after the engine was rebuilt — the same generation, a fresh count.</summary>
    public void Restart(string url, DateTime now)
    {
        Begin(url);
        Flush();
    }

    /// <summary>The navigation could not even start (the browser refused the address).</summary>
    public void Cancel(DateTime now)
    {
        _expectOurs = false;
        ClearDue();
        Set(PageLoadState.Idle);
        Flush();
    }

    /// <summary>Where a Reload should go instead of reloading what is shown: the wanted page when the browser shows
    /// a blank page or its own error page for another site, the failed URL when it failed on the wanted site; null =
    /// a real reload.</summary>
    public string? ReloadTarget(string? source)
    {
        string? wanted = WantedUrl is { } w && !IsBlank(w) ? w : null;
        if (string.IsNullOrEmpty(source) || IsBlank(source) || source.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase)) return wanted;
        bool failed = _errorPage || State == PageLoadState.Retrying || State == PageLoadState.Failed && Problem is PageLoadProblem.Unreachable or PageLoadProblem.Error;
        if (!failed) return null;
        string? current = CurrentUrl ?? source;
        return wanted != null && !SameOrigin(current, wanted) ? wanted : current;
    }

    // ───────────────────────────── what the browser says ─────────────────────────────

    /// <summary>NavigationStarting (also raised again for a server redirect of the same navigation).</summary>
    public void Started(ulong navigationId, string uri, DateTime now)
    {
        if (NavigationId == navigationId) { CurrentUrl = uri; return; }   // a redirect: the same load
        bool ours = _expectOurs;
        _expectOurs = false;
        NavigationId = navigationId;
        CurrentUrl = uri;
        ClearDue();   // a newer navigation wins over a pending retry or probe
        ResetDocument();
        if (!ours)
        {
            // the page's own navigation (a link, a form, Vite reloading after it optimised its dependencies): a fresh start
            NewChain();
            Attempt = 1;
        }
        Set(IsBlank(uri) ? PageLoadState.Idle : PageLoadState.Navigating);
        Flush();
    }

    /// <summary>ContentLoading: the new document replaces the old one (before any of its scripts runs). True when it
    /// is a document of the page the Studio asked for — from now on what the page sends counts as this project's.</summary>
    public bool ContentLoading(ulong navigationId, bool isErrorPage, DateTime now)
    {
        if (navigationId != NavigationId) return false;
        if (isErrorPage) { _errorPage = true; return false; }
        _committed = true;
        if (IsBlank(CurrentUrl)) return false;
        if (State == PageLoadState.Navigating) Set(PageLoadState.Verifying);
        Flush();
        return WantedUrl != null && !IsBlank(WantedUrl);
    }

    /// <summary>NavigationCompleted. <paramref name="webError"/> is the WebView2 error status's name.</summary>
    public void Completed(ulong navigationId, bool ok, string webError, int httpStatus, DateTime now)
    {
        if (navigationId != NavigationId) return;   // an older navigation finishing (usually cancelled by the newer one)
        _completed = true;
        if (IsBlank(CurrentUrl)) { Set(PageLoadState.Idle); Flush(); return; }
        HttpStatus = httpStatus;
        if (!ok && webError == "OperationCanceled" && httpStatus == 0)
        {
            Set(PageLoadState.Idle);   // stopped, or it turned into a download: nothing is loading any more
            Flush();
            return;
        }
        if (httpStatus is 502 or 503 or 504) RetryOrFail(now, "HTTP " + httpStatus);
        else if (httpStatus >= 400 || ok || httpStatus is >= 200 and < 400 && !_errorPage) BeginVerify(now);
        else if (IsRetryable(webError, CurrentUrl)) RetryOrFail(now, webError);
        else Fail(PageLoadProblem.Error, webError);
        Flush();
    }

    /// <summary>The overlay posted "ready" (it runs at DOMContentLoaded, before NavigationCompleted).</summary>
    public void OverlayReady(DateTime now)
    {
        if (!_committed) return;   // still the old document talking
        OverlayIsReady = true;
        if (State == PageLoadState.Verifying && _dueKind == DueKind.Probe) _due = now;   // look again now
    }

    /// <summary>The result of the probe with this token (null: the script could not run).</summary>
    public void Probed(long token, PageProbe? probe, DateTime now)
    {
        if (token != _probeToken) return;
        bool recheck = _rechecking;
        if (!(State == PageLoadState.Verifying
              || recheck && (State is PageLoadState.Loaded or PageLoadState.LoadedWithHttpError || IsBuildError))) return;
        _rechecking = false;
        _verifyStart ??= now;

        if (probe == null)
        {
            if (++_probeFails >= MaxProbeFailures) { if (!recheck) Fail(PageLoadProblem.Error, "probe"); }
            else Schedule(DueKind.Probe, now + ProbeRetry);
            Flush();
            return;
        }
        _probeFails = 0;
        if (probe.BuildError) { Fail(PageLoadProblem.BuildError); Flush(); return; }

        if (!probe.HasOverlay)
        {
            Set(PageLoadState.Verifying);
            if (!_injected)
            {
                _injected = true;
                Emit(PageLoadCommandKind.InjectOverlay);
                Schedule(DueKind.Probe, now + ProbeAfterInject);
            }
            else if (HttpStatus >= 400) Finish(recheck: false);   // an error page the overlay cannot live on: shown as it is
            else if (!_reloadedForOverlay) ReloadOnce(ref _reloadedForOverlay);
            else Fail(PageLoadProblem.NoOverlay);
            Flush();
            return;
        }

        bool inGrace = now - _verifyStart.Value < BlankGrace;
        if (probe.ReadyState != "complete" && inGrace)
        {
            Set(PageLoadState.Verifying);
            Schedule(DueKind.Probe, now + ProbeEvery);
            Flush();
            return;
        }
        if (!probe.HasContent && HttpStatus < 400)
        {
            Set(PageLoadState.Verifying);
            if (inGrace) Schedule(DueKind.Probe, now + ProbeEvery);
            else if (!_reloadedForBlank) ReloadOnce(ref _reloadedForBlank);
            else Fail(PageLoadProblem.Blank);
            Flush();
            return;
        }
        Finish(recheck);
        Flush();
    }

    /// <summary>The page's own content changed without a navigation (the overlay sent a new tree). A build error is
    /// fixed that way: Vite's hot update takes its error overlay away and draws the page, no reload — so a page that
    /// failed with a build error is looked at again, and is loaded once the overlay is gone.</summary>
    public void PageChanged(DateTime now)
    {
        if (!IsBuildError || _hidden || _rechecking) return;
        _rechecking = true;
        _verifyStart = now;
        EmitProbe();
        Flush();
    }

    private bool IsBuildError => State == PageLoadState.Failed && Problem == PageLoadProblem.BuildError;

    // ───────────────────────────── on screen ─────────────────────────────

    /// <summary>The page went off screen: nothing is due until it is shown again (the wait left is kept).</summary>
    public void Hidden(DateTime now)
    {
        if (_hidden) return;
        _hidden = true;
        _pausedAt = now;
    }

    /// <summary>The page is on screen again (or something needs it awake). <paramref name="source"/> is what the
    /// browser shows; <paramref name="serving"/>: the project's preview runs.</summary>
    public void Shown(bool serving, string? source, DateTime now)
    {
        if (_hidden)
        {
            _hidden = false;
            if (_due is { } due && _pausedAt is { } at) _due = now + (due > at ? due - at : TimeSpan.Zero);
            _pausedAt = null;
        }
        bool deadSource = string.IsNullOrEmpty(source) || IsBlank(source) || source.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase);
        bool inFlight = State is PageLoadState.Navigating or PageLoadState.Retrying or PageLoadState.Verifying;
        if (serving && WantedUrl is { } wanted && !IsBlank(wanted)
            && (deadSource && !inFlight || State == PageLoadState.Failed && Problem is PageLoadProblem.Unreachable or PageLoadProblem.Blank or PageLoadProblem.Error))
        {
            string target = deadSource || CurrentUrl == null || IsBlank(CurrentUrl) || !SameOrigin(CurrentUrl, wanted) ? wanted : CurrentUrl;
            Begin(target);
            Emit(PageLoadCommandKind.Navigate, target);
        }
        else if (State is PageLoadState.Loaded or PageLoadState.LoadedWithHttpError)
        {
            // still there? (a hidden page may have been suspended in the middle of anything)
            _rechecking = true;
            _verifyStart = now;
            EmitProbe();
        }
        Flush();
    }

    /// <summary>Do what is due at <paramref name="now"/> (a retry, a probe).</summary>
    public void Tick(DateTime now)
    {
        if (_hidden || _due is not { } due || now < due) return;
        var kind = _dueKind;
        ClearDue();
        switch (kind)
        {
            case DueKind.Retry:
                Attempt++;
                _expectOurs = true;
                Set(PageLoadState.Navigating);
                Emit(PageLoadCommandKind.Navigate, CurrentUrl);
                break;
            case DueKind.Probe:
                EmitProbe();
                break;
        }
        Flush();
    }

    // ───────────────────────────── inside ─────────────────────────────

    private void Begin(string url)
    {
        CurrentUrl = url;
        NavigationId = null;
        NewChain();
        Attempt = 1;
        _expectOurs = true;
        ClearDue();
        Set(IsBlank(url) ? PageLoadState.Idle : PageLoadState.Navigating);
    }

    private void NewChain()
    {
        _injected = _reloadedForOverlay = _reloadedForBlank = false;
        ResetDocument();
    }

    private void ResetDocument()
    {
        _committed = _completed = _errorPage = _rechecking = false;
        OverlayIsReady = false;
        _verifyStart = null;
        _probeFails = 0;
        _probeToken++;   // a probe still on its way belongs to the old document
        HttpStatus = 0;
        Detail = null;
    }

    private void BeginVerify(DateTime now)
    {
        _committed = true;
        _verifyStart = now;
        Set(PageLoadState.Verifying);
        EmitProbe();
    }

    private void RetryOrFail(DateTime now, string why)
    {
        if (Attempt >= MaxAttempts) { Fail(PageLoadProblem.Unreachable, why); return; }
        Detail = why;
        Set(PageLoadState.Retrying, PageLoadProblem.Unreachable);
        Schedule(DueKind.Retry, now + Backoff[Math.Clamp(Attempt - 1, 0, Backoff.Length - 1)]);
    }

    private void ReloadOnce(ref bool done)
    {
        done = true;
        _expectOurs = true;
        Set(PageLoadState.Navigating);
        Emit(PageLoadCommandKind.Reload);
    }

    private void Finish(bool recheck)
    {
        bool wasLoaded = State is PageLoadState.Loaded or PageLoadState.LoadedWithHttpError;
        if (HttpStatus >= 400) Set(PageLoadState.LoadedWithHttpError, PageLoadProblem.HttpError);
        else Set(PageLoadState.Loaded);
        if (!(recheck && wasLoaded)) Emit(PageLoadCommandKind.RequestTree);
    }

    private void Fail(PageLoadProblem problem, string? detail = null)
    {
        ClearDue();
        if (detail != null) Detail = detail;
        Set(PageLoadState.Failed, problem);
    }

    private void Schedule(DueKind kind, DateTime at) { _dueKind = kind; _due = at; }

    private void ClearDue() { _due = null; _dueKind = DueKind.None; }

    private void EmitProbe()
    {
        _probeToken++;
        Emit(PageLoadCommandKind.Probe, token: _probeToken);
    }

    private void Emit(PageLoadCommandKind kind, string? url = null, long token = 0) => _out.Add(new PageLoadCommand(kind, url, token));

    private void Set(PageLoadState state, PageLoadProblem problem = PageLoadProblem.None)
    {
        if (State == state && Problem == problem) { _dirty = true; return; }   // (the attempt or the code may have moved)
        State = state;
        Problem = problem;
        _dirty = true;
    }

    private void Flush()
    {
        if (_flushing) return;
        _flushing = true;
        try
        {
            while (true)
            {
                if (_dirty) { _dirty = false; Changed?.Invoke(); continue; }
                if (_out.Count == 0) break;
                var c = _out[0];
                _out.RemoveAt(0);
                Command?.Invoke(c);
            }
        }
        finally { _flushing = false; }
    }

    // ───────────────────────────── rules ─────────────────────────────

    public static bool IsBlank(string? url) => url != null && url.StartsWith(BlankPage, StringComparison.OrdinalIgnoreCase);

    /// <summary>Errors a server that is still starting gives (and a dev server restarting): worth another attempt.</summary>
    public static bool IsRetryable(string webError, string? url) => webError switch
    {
        "CannotConnect" or "ConnectionAborted" or "ConnectionReset" or "ConnectionRefused" or "Disconnected" or "ServerUnreachable"
            or "Timeout" or "ErrorHttpInvalidServerResponse" => true,
        "HostNameNotResolved" or "Unknown" => IsLoopback(url),
        _ => false,
    };

    private static bool IsLoopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    private static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.GetLeftPart(UriPartial.Authority), y.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
