using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CluadeX.Services;
using CluadeX.Services.UiStudio;

namespace StudioTest;

/// <summary>The UI Studio's page loading made real (owner 2026-11-03: "เพิ่มการโหลดหน้าตามจริงใหม่ เพราะบางทีมัน
/// ไม่สมบูรณ์ในการเรียกหน้า"): the load tracker driven with a fake clock — no browser, no timers — plus the dev server's
/// URL matching and port wait against real sockets, and the service's page generations.</summary>
internal static partial class Program
{
    /// <summary>The tracker with a clock the test moves and the commands it raised, in order.</summary>
    sealed class LoadRig
    {
        public readonly PageLoadTracker T = new();
        public readonly List<PageLoadCommand> Cmds = new();
        public DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        private ulong _nav = 100;

        public LoadRig() => T.Command += c => Cmds.Add(c);

        public void Advance(double seconds) { Now += TimeSpan.FromSeconds(seconds); T.Tick(Now); }
        public ulong Start(string url) { ulong id = ++_nav; T.Started(id, url, Now); return id; }
        public int Count(PageLoadCommandKind k) => Cmds.Count(c => c.Kind == k);
        public long LastProbe => Cmds.LastOrDefault(c => c.Kind == PageLoadCommandKind.Probe).Token;
        public void Answer(PageProbe? p) => T.Probed(LastProbe, p, Now);
        /// <summary>One navigation whose server is not up: the browser's error page.</summary>
        public ulong Refused(string url)
        {
            ulong id = Start(url);
            T.ContentLoading(id, isErrorPage: true, Now);
            T.Completed(id, false, "CannotConnect", 0, Now);
            return id;
        }
        /// <summary>One navigation that lands (ok, HTTP <paramref name="status"/>), probed as <paramref name="probe"/>.</summary>
        public ulong Lands(string url, PageProbe probe, int status = 200, bool ok = true)
        {
            ulong id = Start(url);
            T.ContentLoading(id, isErrorPage: false, Now);
            T.OverlayReady(Now);
            T.Completed(id, ok, "Unknown", status, Now);
            Answer(probe);
            return id;
        }
    }

    static PageProbe GoodPage(string href = "http://localhost:5173/") => new("complete", true, 3, 120, false, false, href);
    static PageProbe BlankBody => new("complete", true, 2, 0, true, false, "http://localhost:5173/");
    static PageProbe NoCx => new("complete", false, 3, 120, false, false, "http://localhost:5173/");

    static void PageLoading()
    {
        Console.WriteLine("Page loading");
        const string U = "http://localhost:5173/";

        // 1 · a server that is not up yet: tried again after 0.5 / 1 / 2 / 4 / 8 s, then unreachable
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            Check("load: a Studio navigation starts as Navigating, attempt 1", r.T.State == PageLoadState.Navigating && r.T.Attempt == 1);
            var waits = new List<double>();
            for (int i = 0; i < PageLoadTracker.MaxAttempts; i++)
            {
                r.Refused(U);
                if (r.T.State != PageLoadState.Retrying) break;
                var due = r.T.NextDue!.Value;
                waits.Add((due - r.Now).TotalSeconds);
                int navs = r.Count(PageLoadCommandKind.Navigate);
                r.Advance((due - r.Now).TotalSeconds - 0.05);
                bool early = r.Count(PageLoadCommandKind.Navigate) != navs;
                r.Advance(0.05);
                if (early || r.Count(PageLoadCommandKind.Navigate) != navs + 1 || r.Cmds[^1].Url != U) { waits.Add(-1); break; }
            }
            Check("load: refused → retried after 0.5 / 1 / 2 / 4 / 8 s (never early), at the same address",
                string.Join("/", waits.Select(w => w.ToString(System.Globalization.CultureInfo.InvariantCulture))) == "0.5/1/2/4/8", string.Join(" ", waits));
            Check("load: …then, after 6 attempts, Failed as unreachable — the page is hidden for the Try-again card, nothing more is due",
                r.T.State == PageLoadState.Failed && r.T.Problem == PageLoadProblem.Unreachable && r.T.Attempt == 6 && r.T.Info.HidesPage && r.T.NextDue == null,
                $"{r.T.State} {r.T.Problem} attempt {r.T.Attempt} due {r.T.NextDue}");

            var ok = new LoadRig();
            ok.T.Want(U, ok.Now);
            ok.Refused(U); ok.Advance(0.5);
            ok.Refused(U); ok.Advance(1);
            Check("load: the third attempt is attempt 3 of 6", ok.T.Attempt == 3 && ok.T.Info.MaxAttempts == 6, $"{ok.T.Attempt}/{ok.T.Info.MaxAttempts}");
            ok.Lands(U, GoodPage());
            Check("load: a server that comes up on attempt 3 ends Loaded, and the tree is asked for",
                ok.T.State == PageLoadState.Loaded && ok.Count(PageLoadCommandKind.RequestTree) == 1 && ok.T.NextDue == null, $"{ok.T.State}");
            Check("load: 502 / 503 / 504 from a dev server's proxy are retried too", Retries(503) && Retries(502) && Retries(504));
            Check("load: a certificate error is not retried (it will not fix itself)", Fails("CertificateIsInvalid"));
            Check("load: an unknown host is retried only on this machine", PageLoadTracker.IsRetryable("HostNameNotResolved", "http://localhost:3000/")
                && !PageLoadTracker.IsRetryable("HostNameNotResolved", "https://shop.example.invalid/"));

            bool Retries(int code)
            {
                var x = new LoadRig();
                x.T.Want(U, x.Now);
                ulong id = x.Start(U);
                x.T.ContentLoading(id, false, x.Now);
                x.T.Completed(id, false, "Unknown", code, x.Now);
                return x.T.State == PageLoadState.Retrying;
            }
            bool Fails(string error)
            {
                var x = new LoadRig();
                x.T.Want("https://localhost:8443/", x.Now);
                ulong id = x.Start("https://localhost:8443/");
                x.T.Completed(id, false, error, 0, x.Now);
                return x.T.State == PageLoadState.Failed && x.T.Problem == PageLoadProblem.Error && x.T.NextDue == null && x.Count(PageLoadCommandKind.Navigate) == 0;
            }
        }

        // 2 · a newer navigation wins over a pending retry
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            r.Refused(U);
            int gen = r.T.Generation;
            r.T.Want("http://localhost:5173/about", r.Now);
            r.Start("http://localhost:5173/about");
            r.Advance(30);
            Check("load: a newer Studio navigation cancels the old one's pending retry (no stale Navigate to the old page)",
                r.Count(PageLoadCommandKind.Navigate) == 0 && r.T.Generation == gen + 1 && r.T.Attempt == 1, $"navs {r.Count(PageLoadCommandKind.Navigate)}");
            var f = new LoadRig();
            f.T.Want(U, f.Now);
            f.Refused(U);
            f.Start("http://localhost:5173/other");   // a link clicked in the page (not the Studio's)
            f.Advance(30);
            Check("load: …and so does a navigation the page started itself", f.Count(PageLoadCommandKind.Navigate) == 0 && f.T.CurrentUrl == "http://localhost:5173/other");
            var late = new LoadRig();
            late.T.Want(U, late.Now);
            ulong first = late.Start(U);
            ulong second = late.Start("http://localhost:5173/b");
            late.T.Completed(first, false, "OperationCanceled", 0, late.Now);
            Check("load: the older navigation's late 'cancelled' does not end the newer one", late.T.State == PageLoadState.Navigating && late.T.NavigationId == second);
        }

        // 3 · an HTTP error page with a body is this project's page: loaded with the code, no retry
        foreach (int code in new[] { 404, 500 })
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            ulong id = r.Start(U);
            bool counts = r.T.ContentLoading(id, isErrorPage: false, r.Now);
            r.T.Completed(id, false, "Unknown", code, r.Now);
            r.Answer(new PageProbe("complete", true, 1, 40, false, false, U));
            Check($"load: HTTP {code} with a body → LoadedWithHttpError {code}, picks allowed (its document counts), tree asked, no retry",
                counts && r.T.State == PageLoadState.LoadedWithHttpError && r.T.Problem == PageLoadProblem.HttpError && r.T.HttpStatus == code
                && r.Count(PageLoadCommandKind.RequestTree) == 1 && r.Count(PageLoadCommandKind.Navigate) + r.Count(PageLoadCommandKind.Reload) == 0
                && r.T.NextDue == null && !r.T.Info.HidesPage, $"{r.T.State} {r.T.HttpStatus}");
        }
        {
            // an empty 404 (Chromium draws its own error page, the overlay may not be in it): shown as it is
            var r = new LoadRig();
            r.T.Want(U + "missing.html", r.Now);
            ulong id = r.Start(U + "missing.html");
            r.T.ContentLoading(id, isErrorPage: true, r.Now);
            r.T.Completed(id, false, "Unknown", 404, r.Now);
            r.Answer(new PageProbe("complete", false, 0, 0, false, false, U));
            r.Advance(0.3);
            r.Answer(new PageProbe("complete", false, 0, 0, false, false, U));
            Check("load: an empty 404 → one overlay injection, then LoadedWithHttpError (no reload loop)",
                r.T.State == PageLoadState.LoadedWithHttpError && r.Count(PageLoadCommandKind.InjectOverlay) == 1 && r.Count(PageLoadCommandKind.Reload) == 0, $"{r.T.State}");
        }

        // 4 · the overlay's "ready" comes before NavigationCompleted (DOMContentLoaded < load)
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            ulong id = r.Start(U);
            r.T.OverlayReady(r.Now);
            bool earlyIgnored = !r.T.OverlayIsReady;
            r.T.ContentLoading(id, false, r.Now);
            r.T.OverlayReady(r.Now);
            bool ready = r.T.OverlayIsReady;
            r.T.Completed(id, true, "Unknown", 200, r.Now);
            r.Answer(GoodPage());
            Check("load: 'ready' before Completed is accepted (the real order); one from the old document (before ContentLoading) is not",
                earlyIgnored && ready && r.T.State == PageLoadState.Loaded);
        }

        // 5 · the overlay is missing: inject it, then one reload, then give up
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            r.Lands(U, NoCx);
            bool injected = r.Count(PageLoadCommandKind.InjectOverlay) == 1 && r.T.NextDue != null;
            r.Advance(0.3);
            r.Answer(NoCx);
            bool reloaded = r.Count(PageLoadCommandKind.Reload) == 1 && r.T.State == PageLoadState.Navigating;
            r.Lands(U, NoCx);   // the reload's document: still none
            Check("load: no __cx → overlay.js injected, then exactly one reload, then Failed(NoOverlay) — the page stays on screen",
                injected && reloaded && r.T.State == PageLoadState.Failed && r.T.Problem == PageLoadProblem.NoOverlay
                && r.Count(PageLoadCommandKind.InjectOverlay) == 1 && r.Count(PageLoadCommandKind.Reload) == 1 && !r.T.Info.HidesPage,
                $"inject {r.Count(PageLoadCommandKind.InjectOverlay)} reload {r.Count(PageLoadCommandKind.Reload)} {r.T.State} {r.T.Problem}");
            var fixedByInject = new LoadRig();
            fixedByInject.T.Want(U, fixedByInject.Now);
            fixedByInject.Lands(U, NoCx);
            fixedByInject.Advance(0.3);
            fixedByInject.Answer(GoodPage());
            Check("load: …and when the injection was enough, Loaded with no reload", fixedByInject.T.State == PageLoadState.Loaded && fixedByInject.Count(PageLoadCommandKind.Reload) == 0);
        }

        // 6 · blank for the whole grace period: one reload, then blank; a build error is never retried
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            r.Lands(U, BlankBody);
            int probesInGrace = 0;
            while (r.T.State == PageLoadState.Verifying && r.Count(PageLoadCommandKind.Reload) == 0 && probesInGrace < 100)
            {
                r.Advance(0.4);
                if (r.T.State == PageLoadState.Verifying) { probesInGrace++; r.Answer(BlankBody); }
            }
            bool reloadedOnce = r.Count(PageLoadCommandKind.Reload) == 1;
            bool waitedGrace = probesInGrace >= 18;   // ~8 s at 0.4 s
            r.Lands(U, BlankBody);
            for (int i = 0; i < 40 && r.T.State == PageLoadState.Verifying; i++) { r.Advance(0.4); if (r.T.State == PageLoadState.Verifying) r.Answer(BlankBody); }
            Check("load: an empty SPA root for the whole 8 s grace → one reload, then Failed(Blank) (the card hides the page)",
                waitedGrace && reloadedOnce && r.T.State == PageLoadState.Failed && r.T.Problem == PageLoadProblem.Blank && r.Count(PageLoadCommandKind.Reload) == 1 && r.T.Info.HidesPage,
                $"probes {probesInGrace} reloads {r.Count(PageLoadCommandKind.Reload)} {r.T.State} {r.T.Problem}");

            var slow = new LoadRig();
            slow.T.Want(U, slow.Now);
            slow.Lands(U, BlankBody);
            slow.Advance(0.4); slow.Answer(BlankBody);
            slow.Advance(0.4); slow.Answer(GoodPage());
            Check("load: an SPA that renders within the grace → Loaded, no reload", slow.T.State == PageLoadState.Loaded && slow.Count(PageLoadCommandKind.Reload) == 0);

            var build = new LoadRig();
            build.T.Want(U, build.Now);
            build.Lands(U, new PageProbe("complete", true, 3, 50, false, true, U));
            build.Advance(60);
            Check("load: a Vite error overlay → Failed(BuildError), never retried, the page (with the error) stays on screen",
                build.T.State == PageLoadState.Failed && build.T.Problem == PageLoadProblem.BuildError
                && build.Count(PageLoadCommandKind.Reload) + build.Count(PageLoadCommandKind.Navigate) == 0 && !build.T.Info.HidesPage);
            // the error is fixed by Vite's hot update: no navigation, only the page's tree changes
            int probesBefore = build.Count(PageLoadCommandKind.Probe);
            build.T.PageChanged(build.Now);
            bool looked = build.Count(PageLoadCommandKind.Probe) == probesBefore + 1;
            build.Answer(new PageProbe("complete", true, 3, 50, false, true, U));   // still the error overlay
            bool stillError = build.T.State == PageLoadState.Failed && build.T.Problem == PageLoadProblem.BuildError;
            build.T.PageChanged(build.Now);
            build.Answer(GoodPage(U));
            Check("load: a build error fixed by a hot update (a new tree, no navigation) is looked at again → Loaded, its tree asked for",
                looked && stillError && build.T.State == PageLoadState.Loaded && build.Count(PageLoadCommandKind.RequestTree) == 1
                && build.Count(PageLoadCommandKind.Reload) + build.Count(PageLoadCommandKind.Navigate) == 0,
                $"looked={looked} stillError={stillError} {build.T.State}/{build.T.Problem}");
            var calm = new LoadRig();
            calm.T.Want(U, calm.Now);
            calm.Lands(U, GoodPage());
            int calmProbes = calm.Count(PageLoadCommandKind.Probe);
            calm.T.PageChanged(calm.Now);
            Check("load: …a loaded page's own changes are not probed (only a build error is looked at again)",
                calm.Count(PageLoadCommandKind.Probe) == calmProbes && calm.T.State == PageLoadState.Loaded);
            var gone = new LoadRig();
            gone.T.Want(U, gone.Now);
            gone.Lands(U, GoodPage());
            gone.T.Want(U, gone.Now);
            gone.Start(U);
            gone.T.Probed(1, GoodPage(), gone.Now);
            Check("load: a probe answer of an older document is ignored", gone.T.State == PageLoadState.Navigating);
        }

        // 7 · hidden: nothing is due; shown: checked again
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            r.Refused(U);
            r.Advance(0.2);
            r.T.Hidden(r.Now);
            bool nothingDue = r.T.NextDue == null;
            r.Advance(120);
            bool noRetryWhileHidden = r.Count(PageLoadCommandKind.Navigate) == 0 && r.T.Attempt == 1;
            r.T.Shown(true, U, r.Now);
            var left = r.T.NextDue - r.Now;
            Check("load: while hidden nothing is due (no retry counted against an unseen page); shown, the wait left resumes",
                nothingDue && noRetryWhileHidden && left is { } l && Math.Abs(l.TotalSeconds - 0.3) < 0.01, $"left {left}");

            var b = new LoadRig();
            b.T.Want(U, b.Now);
            b.Lands(U, GoodPage());
            b.Start("about:blank");   // the page went blank by itself (or the browser was emptied)
            b.T.Hidden(b.Now);
            b.T.Shown(true, "about:blank", b.Now);
            Check("load: shown on about:blank while the project serves → Navigate(the wanted page)",
                b.Cmds.LastOrDefault(c => c.Kind == PageLoadCommandKind.Navigate).Url == U && b.T.State == PageLoadState.Navigating);
            var nb = new LoadRig();
            nb.T.Want(U, nb.Now);
            nb.Start("about:blank");
            nb.T.Shown(false, "about:blank", nb.Now);
            Check("load: …but not while nothing serves it", nb.Count(PageLoadCommandKind.Navigate) == 0);
            var chk = new LoadRig();
            chk.T.Want(U, chk.Now);
            chk.Lands(U, GoodPage());
            chk.T.Hidden(chk.Now);
            chk.T.Shown(true, U, chk.Now);
            bool probed = chk.Count(PageLoadCommandKind.Probe) == 2 && chk.T.State == PageLoadState.Loaded;
            chk.Answer(GoodPage());
            Check("load: shown on a loaded page → one quiet re-check (no 'loading' flash, no second tree)",
                probed && chk.T.State == PageLoadState.Loaded && chk.Count(PageLoadCommandKind.RequestTree) == 1);
            var f = new LoadRig();
            f.T.Want(U, f.Now);
            for (int i = 0; i < 6; i++) { f.Refused(U); f.Advance(10); }
            f.T.Shown(true, U, f.Now);
            Check("load: shown after it failed → a fresh attempt at the page", f.T.State == PageLoadState.Navigating && f.T.Attempt == 1
                && f.Cmds[^1].Kind == PageLoadCommandKind.Navigate);
        }

        // 8 · Vite's "new dependencies optimized, reloading": a second document from the same load
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            ulong first = r.Start(U);
            r.T.ContentLoading(first, false, r.Now);
            r.T.Completed(first, true, "Unknown", 200, r.Now);
            long staleProbe = r.LastProbe;
            ulong second = r.Start(U);   // the page reloads itself
            r.T.Probed(staleProbe, BlankBody, r.Now);
            r.T.Completed(first, false, "OperationCanceled", 0, r.Now);
            bool restarted = r.T.State == PageLoadState.Navigating;
            r.T.ContentLoading(second, false, r.Now);
            r.T.Completed(second, true, "Unknown", 200, r.Now);
            r.Answer(GoodPage());
            Check("load: a second document of the same load restarts the check and ends Loaded (never Failed)",
                restarted && r.T.State == PageLoadState.Loaded && r.T.Attempt == 1);
        }

        // what Reload does on each page
        {
            var r = new LoadRig();
            r.T.Want(U, r.Now);
            r.Lands(U, GoodPage());
            Check("reload: a loaded page is really reloaded", r.T.ReloadTarget(U) == null);
            Check("reload: on about:blank or the browser's error page → the wanted page", r.T.ReloadTarget("about:blank") == U && r.T.ReloadTarget("chrome-error://chromewebdata/") == U && r.T.ReloadTarget(null) == U);
            r.Refused("http://127.0.0.1:9/");
            Check("reload: a failed page of another site → the wanted page", r.T.ReloadTarget("http://127.0.0.1:9/") == U);
            r.Refused(U + "deep/page.html");
            Check("reload: a failed page of the wanted site → that page again", r.T.ReloadTarget(U + "deep/page.html") == U + "deep/page.html");
            var none = new LoadRig();
            Check("reload: before any ask, a blank page is simply reloaded", none.T.ReloadTarget("about:blank") == null);
        }

        // the probe's answer
        var p = PageProbe.Parse("{\"rs\":\"complete\",\"cx\":true,\"b\":2,\"t\":0,\"root\":true,\"err\":false,\"href\":\"http://x/\"}");
        Check("probe: parsed (an empty #root with no text is no content)", p is { ReadyState: "complete", HasOverlay: true, BodyElements: 2, RootEmpty: true, BuildError: false, HasContent: false });
        Check("probe: text in the body is content", PageProbe.Parse("{\"rs\":\"complete\",\"cx\":false,\"b\":0,\"t\":5}") is { HasContent: true, HasOverlay: false });
        Check("probe: 'null', garbage and a string are no answer", PageProbe.Parse("null") == null && PageProbe.Parse("<html>") == null && PageProbe.Parse("\"x\"") == null && PageProbe.Parse(null) == null);

        DevServerUrl();
        PageGenerations();
    }

    static void DevServerUrl()
    {
        Console.WriteLine("Dev server URL");
        Check("dev url: Vite", DevServerRunner.MatchLocalUrl("  ➜  Local:   http://localhost:5173/") == "http://localhost:5173/");
        Check("dev url: Vite with its colours", DevServerRunner.MatchLocalUrl("\u001b[32m  ➜\u001b[39m  \u001b[1mLocal\u001b[22m:   \u001b[36mhttp://localhost:\u001b[1m5173\u001b[22m/\u001b[39m") == "http://localhost:5173/",
            DevServerRunner.MatchLocalUrl("\u001b[32m  ➜\u001b[39m  \u001b[1mLocal\u001b[22m:   \u001b[36mhttp://localhost:\u001b[1m5173\u001b[22m/\u001b[39m") ?? "null");
        Check("dev url: Next", DevServerRunner.MatchLocalUrl("   - Local:        http://localhost:3000") == "http://localhost:3000");
        Check("dev url: Astro", DevServerRunner.MatchLocalUrl(" ┃ Local    http://localhost:4321/") == "http://localhost:4321/");
        Check("dev url: 0.0.0.0 → localhost", DevServerRunner.MatchLocalUrl("Listening on http://0.0.0.0:3000/") == "http://localhost:3000/");
        Check("dev url: trailing punctuation dropped", DevServerRunner.MatchLocalUrl("open your browser on (http://127.0.0.1:8080).") == "http://127.0.0.1:8080");
        Check("dev url: a network address is not the page", DevServerRunner.MatchLocalUrl("  ➜  Network: http://192.168.1.5:5173/") == null && DevServerRunner.MatchLocalUrl("no url here") == null);

        // the port: a server that prints its address a second before it listens
        int port = FreeTcpPort();
        var uri = new Uri($"http://localhost:{port}/");
        TcpListener? late = null;
        var opener = Task.Run(async () => { await Task.Delay(1000); late = new TcpListener(IPAddress.Loopback, port); late.Start(); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool answered = DevServerRunner.WaitForPortAsync(uri, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        double took = sw.Elapsed.TotalSeconds;
        opener.Wait();
        Check("dev port: waits for a port that starts listening 1 s late", answered && took >= 0.8 && took < 5, $"{answered} after {took:0.00}s");

        // the runner itself: a URL line from the dev process is reported once the port answers; an install line never
        var runner = new DevServerRunner(Path.GetTempPath());
        string? found = null;
        using var gotIt = new ManualResetEventSlim();
        runner.UrlFound += u => { found = u; gotIt.Set(); };
        var onLine = typeof(DevServerRunner).GetMethod("OnLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onLine.Invoke(runner, new object?[] { $"npm http fetch GET 200 http://localhost:{port}/react 12ms (registry)", false });
        bool installIgnored = !gotIt.Wait(600);
        onLine.Invoke(runner, new object?[] { $"  ➜  Local:   http://localhost:{port}/", true });
        Check("dev port: an install's output is never taken as the page; the dev process's address is, once its port answers",
            installIgnored && gotIt.Wait(5000) && found == $"http://localhost:{port}/" && runner.Url == found, found ?? "nothing");
        runner.Dispose();
        late?.Stop();

        int dead = FreeTcpPort();
        sw.Restart();
        bool timedOut = !DevServerRunner.WaitForPortAsync(new Uri($"http://127.0.0.1:{dead}/"), TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        Check("dev port: bounded — a port that never opens gives up after the timeout", timedOut && sw.Elapsed.TotalSeconds < 4, $"{sw.Elapsed.TotalSeconds:0.00}s");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        sw.Restart();
        bool cancelled = false;
        try { DevServerRunner.WaitForPortAsync(new Uri($"http://localhost:{dead}/"), TimeSpan.FromSeconds(30), cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("dev port: cancellable (stopping the server stops the wait)", cancelled && sw.Elapsed.TotalSeconds < 3, $"{cancelled} {sw.Elapsed.TotalSeconds:0.00}s");
        Check("dev port: only this machine is dialled", !DevServerRunner.WaitForPortAsync(new Uri("http://example.invalid:80/"), TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult());
    }

    static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>The service's page generations: a document started for an older one never makes the page this project's.</summary>
    static void PageGenerations()
    {
        Console.WriteLine("Page generations");
        string dir = Path.Combine(Path.GetTempPath(), "cx-pagegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><h1>gen</h1>");
        var svc = new StudioService(new SettingsService());
        try
        {
            var navs = new List<string>();
            svc.NavigateRequested += navs.Add;
            svc.OpenProject(dir);
            int left = svc.PageGeneration;
            Check("gen: opening a project leaves the last page (blank, old) and moves the generation", svc.PageIsOld && navs.LastOrDefault() == StudioService.BlankPage && left > 0);
            svc.StartAsync().GetAwaiter().GetResult();
            int asked = svc.PageGeneration;
            Check("gen: the project's page is a new generation", asked == left + 1 && navs.LastOrDefault()?.EndsWith("index.html") == true, navs.LastOrDefault() ?? "");
            Check("gen: a document of the older generation does not make the page this project's", !svc.NoteDocumentLoaded(left) && svc.PageIsOld);
            Check("gen: one of the current generation does", svc.NoteDocumentLoaded(asked) && !svc.PageIsOld);
            svc.NavigatePage(svc.CurrentBaseUrl + "index.html");
            Check("gen: NavigatePage (address bar, gallery) is a Studio navigation: the wanted page, a new generation",
                svc.PageGeneration == asked + 1 && svc.LastRequestedUrl == svc.CurrentBaseUrl + "index.html");
            int changes = 0;
            svc.PageLoadChanged += () => changes++;
            svc.SetPageLoad(new PageLoadInfo(PageLoadState.Navigating, Attempt: 1, MaxAttempts: 6));
            svc.SetPageLoad(new PageLoadInfo(PageLoadState.Navigating, Attempt: 1, MaxAttempts: 6));
            Check("gen: the same load state twice is one change", changes == 1 && svc.PageLoadState == PageLoadState.Navigating);
            bool? hard = null;
            svc.PageReloadRequested += h => hard = h;
            int watcherReloads = 0;
            svc.ReloadRequested += _ => watcherReloads++;
            svc.ReloadPage(hard: true);
            Check("gen: ReloadPage raises its own event (not the file watcher's)", hard == true && watcherReloads == 0);
            int g = svc.PageGeneration;
            svc.CloseProject();
            Check("gen: closing the project moves the generation and resets the load state", svc.PageGeneration == g + 1 && svc.PageLoadState == PageLoadState.Idle
                && !svc.NoteDocumentLoaded(svc.PageGeneration));
        }
        finally
        {
            svc.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
