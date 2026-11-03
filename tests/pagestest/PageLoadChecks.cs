using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views;

namespace PagesTest;

/// <summary>The Studio's page loading in the real browser (owner 2026-11-03: "เพิ่มการโหลดหน้าตามจริงใหม่ เพราะบางทีมัน
/// ไม่สมบูรณ์ในการเรียกหน้า"): a page counts as loaded only when it is really there, and gets there by itself when it was
/// not ready. Localhost only — the Studio's own static server and a tiny HTTP server written here.</summary>
internal static partial class Program
{
    /// <summary>The smallest HTTP server: every request gets the same answer (status + HTML body), then the connection closes.</summary>
    sealed class MiniHttp : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}/";

        public MiniHttp(int port, int status, string body)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => LoopAsync(status, body));
        }

        private async Task LoopAsync(int status, string body)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client, status, body));
            }
        }

        private static async Task ServeAsync(TcpClient client, int status, string body)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buf = new byte[16384];
                    var seen = new StringBuilder();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int n = await stream.ReadAsync(buf, timeout.Token);
                        if (n == 0) return;
                        seen.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }
                    byte[] content = Encoding.UTF8.GetBytes(body);
                    string reason = status == 200 ? "OK" : status == 500 ? "Internal Server Error" : "Error";
                    byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(content);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static bool TreeHas(StudioService studio, string tag) => studio.Tree?.Walk().Any(n => n.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) == true;

    static async Task<bool> HasOverlay(StudioService studio) => studio.Evaluate != null && await studio.Evaluate("!!window.__cx") == "true";

    static void GoTo(UiStudioViewModel vm, string url)
    {
        vm.Url = url;
        vm.NavigateCommand.Execute(null);   // the address bar
    }

    static async Task PageLoading(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Page loading (real browser)");
        var studio = Studio(vm);
        var states = new List<PageLoadState>();
        studio.PageLoadChanged += () => { lock (states) states.Add(studio.PageLoadState); };

        // (a) a static page with no script at all, opened right after a project switch: its only tree is the one the
        // overlay sends at DOMContentLoaded — dropped before, because the page still counted as the last project's
        string plain = Path.Combine(fx, "plain-site");
        W(plain, "index.html", "<!doctype html><html><head><title>Plain</title></head><body><h1>Plain page</h1><p>No script, and nothing ever changes.</p></body></html>");
        W(plain, "about.html", Page("Plain about", "#00838F"));
        vm.OpenProject(plain, autoStart: true);
        bool layered = await WaitUntil(() => vm.Layers.Count > 0 && TreeHas(studio, "h1") && studio.PageLoadState == PageLoadState.Loaded
                                             && vm.Url.EndsWith("/index.html", StringComparison.Ordinal), 20000);
        Check("load: a no-JS static page opened right after a project switch fills Layers (no DOM change needed) and is verified",
            layered && !studio.PageIsOld, $"{studio.PageLoadState} layers={vm.Layers.Count} old={studio.PageIsOld} url={vm.Url}");
        Check("load: …nothing is shown as loading once it is there", !vm.IsPageLoading && vm.PageLoadText == "" && !vm.PageLoadFailed, vm.PageLoadText);
        string home = vm.Url;

        // (c) a 500 page with a body: it is this project's page — picks work, and the status says HTTP 500
        using (var err = new MiniHttp(0, 500, "<!doctype html><html><body><h1 id=\"oops\">Server error</h1><p>Something broke.</p></body></html>"))
        {
            GoTo(vm, err.Url);
            bool loaded = await WaitUntil(() => studio.PageLoadState == PageLoadState.LoadedWithHttpError, 15000);
            Check("load: an HTTP 500 page loads as LoadedWithHttpError and the status strip says HTTP 500",
                loaded && studio.PageLoad.HttpStatus == 500 && vm.PageLoadText.Contains("HTTP 500", StringComparison.Ordinal) && vm.PageLoadIsError && !vm.PageLoadFailed,
                $"{studio.PageLoadState} {studio.PageLoad.HttpStatus} '{vm.PageLoadText}'");
            studio.RunScript("window.__cx && __cx.selectBySelector('#oops')");
            bool picked = await WaitUntil(() => studio.Selection?.Tag == "h1", 8000);
            Check("load: …and a pick on it reaches the Studio (the error page is not dropped as another project's)", picked && TreeHas(studio, "h1"),
                $"selection={studio.Selection?.Tag} old={studio.PageIsOld}");
            Check("load: …the page stays on screen (no failure card over an error page that says what is wrong)",
                ((System.Windows.FrameworkElement)view.FindName("Frame")).Visibility == System.Windows.Visibility.Visible);
        }

        // (b) a local server that starts listening 2 s after the page was asked for: it loads by itself
        int latePort = FreePort();
        string lateUrl = $"http://127.0.0.1:{latePort}/";
        lock (states) states.Clear();
        GoTo(vm, lateUrl);
        MiniHttp? late = null;
        var opener = Task.Run(async () => { await Task.Delay(2000); late = new MiniHttp(latePort, 200, "<!doctype html><html><body><h1>Late server</h1></body></html>"); });
        bool lateLoaded = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url.StartsWith(lateUrl, StringComparison.Ordinal) && TreeHas(studio, "h1")
                                               && studio.Tree!.Walk().Any(n => n.Text?.Contains("Late server") == true || n.Label.Contains("Late", StringComparison.Ordinal)), 25000);
        await opener;
        bool retried;
        lock (states) retried = states.Contains(PageLoadState.Retrying);
        Check("load: a local server that starts 2 s late is waited for and loads by itself (retried, then verified, Layers filled)",
            lateLoaded && retried, $"{studio.PageLoadState} retried={retried} url={vm.Url} detail={studio.PageLoad.Detail} states={string.Join(",", states.Distinct())}");

        // (d) Reload on the browser's error page / on about:blank, with the project serving: the wanted page again
        studio.NavigatePage(home);
        await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url == home, 15000);
        int dead = FreePort();
        studio.RunScript($"location.href = 'http://127.0.0.1:{dead}/'");   // the page itself goes to a dead address
        bool broken = await WaitUntil(() => studio.PageLoadState is PageLoadState.Retrying or PageLoadState.Failed, 15000);
        vm.ReloadCommand.Execute(null);
        bool back = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url == home, 15000) && await HasOverlay(studio);
        Check("load: Reload on the browser's error page (another address) opens the wanted page, verified, overlay present",
            broken && back, $"broken={broken} {studio.PageLoadState} url={vm.Url}");
        late?.Dispose();

        studio.RunScript("location.href = 'about:blank'");
        bool blank = await WaitUntil(() => vm.Url == "" && studio.PageLoadState == PageLoadState.Idle, 10000);
        vm.ReloadCommand.Execute(null);
        bool fromBlank = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url == home, 15000) && await HasOverlay(studio);
        Check("load: Reload on about:blank while the project serves opens the wanted page, verified (it used to reload the blank page)",
            blank && fromBlank, $"blank={blank} {studio.PageLoadState} url={vm.Url}");

        // (e) a gallery card opened while the browser is (re)starting lands on that card's page, with the overlay
        vm.ShowPages = true;
        await WaitUntil(() => Card(vm, "/about.html") != null && Cards(vm).All(c => c.State is "done" or "failed" or "risky" or "manual"), 40000);
        Invoke(view, "RebuildEngine");   // what a browser process that died gets: a fresh engine, starting now
        bool starting = GetField(view, "_engineReady") is false;
        vm.OpenCardCommand.Execute(Card(vm, "/about.html"));
        bool landed = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url.EndsWith("/about.html", StringComparison.Ordinal), 30000);
        bool cx = landed && await HasOverlay(studio);
        Check("load: a gallery card opened during the browser's start lands on that card's page with the overlay present",
            starting && landed && cx, $"starting={starting} {studio.PageLoadState} url={vm.Url} cx={cx}");
        Check("load: …the rebuilt browser's page is picked from again (Layers of that page)", await WaitUntil(() => TreeHas(studio, "h1") && vm.Layers.Count > 0, 8000));
    }
}
