// Owner 2026-10-20: "ระบบอัพเดทอัตโนมัติยังไม่โอเค มีเวอร์ชั่นใหม่ก็เจอช้า โหลดช้าก็ตัด". The updater's downloads had a cap on
// the total time (30 s on the zip path) and started over after any drop; the app looked for a release only at launch.
// ResumableDownloader against a localhost server that drops the connection, stalls, fails and swaps the file; then the
// pure rules of the background probe.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using CluadeX.Services;

namespace ApiTest;

internal static class UpdateDownloadWire
{
    /// <summary>Serves one file with Range / If-Range, and does what each request's plan says.</summary>
    sealed class RangeServer : IDisposable
    {
        public readonly record struct Plan(long DropAfter = 0, int StallMs = 0, int Status = 0);   // 0 = not that

        readonly HttpListener _listener = new();
        public string Root { get; }
        public string Url => Root + "/releases/CluadeX-9.9.9-full.nupkg";
        public byte[] Data = [];
        public string ETag = "\"v1\"";
        public Func<int, Plan> Plans = _ => default;
        public readonly List<string> Ranges = new();
        public readonly List<string> IfRanges = new();
        public readonly List<string> Accepts = new();
        long _served;
        int _requests;
        public long Served => Interlocked.Read(ref _served);
        public int Requests => Volatile.Read(ref _requests);

        public RangeServer()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Root = $"http://localhost:{port}";
            _ = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        async Task Handle(HttpListenerContext ctx)
        {
            int i = Interlocked.Increment(ref _requests) - 1;
            var plan = Plans(i);
            var resp = ctx.Response;
            byte[] data = Data;
            lock (Ranges)
            {
                Ranges.Add(ctx.Request.Headers["Range"] ?? "");
                IfRanges.Add(ctx.Request.Headers["If-Range"] ?? "");
                Accepts.Add(ctx.Request.Headers["Accept"] ?? "");
            }
            try
            {
                if (plan.Status != 0) { resp.StatusCode = plan.Status; resp.Close(); return; }
                long start = 0;
                string range = ctx.Request.Headers["Range"] ?? "";
                string ifRange = ctx.Request.Headers["If-Range"] ?? "";
                if (range.StartsWith("bytes=") && long.TryParse(range[6..].TrimEnd('-'), out long s) && (ifRange.Length == 0 || ifRange == ETag)) start = s;
                resp.Headers["ETag"] = ETag;
                resp.Headers["Accept-Ranges"] = "bytes";
                if (start >= data.Length && start > 0)
                {
                    resp.StatusCode = 416;
                    resp.Headers["Content-Range"] = $"bytes */{data.Length}";
                    resp.Close();
                    return;
                }
                if (start > 0)
                {
                    resp.StatusCode = 206;
                    resp.Headers["Content-Range"] = $"bytes {start}-{data.Length - 1}/{data.Length}";
                }
                resp.ContentLength64 = data.Length - start;
                var output = resp.OutputStream;
                long pos = start, sent = 0;
                while (pos < data.Length)
                {
                    int n = (int)Math.Min(64 * 1024, data.Length - pos);
                    await output.WriteAsync(data.AsMemory((int)pos, n));
                    await output.FlushAsync();
                    pos += n; sent += n;
                    Interlocked.Add(ref _served, n);
                    if (plan.DropAfter > 0 && sent >= plan.DropAfter) { resp.Abort(); return; }
                    if (plan.StallMs > 0 && sent >= 512 * 1024) { await Task.Delay(plan.StallMs); resp.Abort(); return; }
                }
                resp.Close();
            }
            catch { try { resp.Abort(); } catch { } }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    /// <summary>--live-update: the real thing, against GitHub. The newest release's page redirect, then a real release
    /// asset through GitHub's 302 to its CDN: stopped at ~2 MB, resumed by a new downloader (the next launch) and
    /// stopped again at ~4 MB — the second run must go on from the first one's bytes (Range + If-Range survive the
    /// redirect). About 4 MB of traffic.</summary>
    public static async Task RunLive(Action<string, string, bool, string> check, Action<string> section)
    {
        section("live · GitHub: probe + resume across a restart");
        var svc = new AutoUpdateService(new SettingsService());
        string? latest = await svc.ProbeLatestReleaseAsync();
        check("update-live", "the probe reads the newest release from GitHub's redirect", latest != null && AutoUpdateService.IsNewer(latest, "3.0.0"), latest ?? "null");
        if (latest == null) return;

        string url = $"https://github.com/xjanova/cluadeX/releases/download/v{latest}/CluadeX-{latest}-full.nupkg";
        string root = Path.Combine(Path.GetTempPath(), "cx-apitest-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            async Task<(long First, long Last, bool Resumed)> RunTo(long stopAt)
            {
                using var dl = new ResumableDownloader(root) { Backoff = _ => TimeSpan.FromSeconds(1) };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                long first = -1, last = 0; bool resumed = false;
                dl.Progress += f => { if (first < 0) first = f.Bytes; last = f.Bytes; resumed |= f.Resumed; if (f.Bytes >= stopAt) cts.Cancel(); };
                try { await dl.DownloadFile(url, Path.Combine(root, "never.nupkg"), _ => { }, cancelToken: cts.Token); } catch (OperationCanceledException) { }
                return (first, last, resumed);
            }
            var one = await RunTo(2 * 1024 * 1024);
            var two = await RunTo(4 * 1024 * 1024);
            check("update-live", "a real GitHub asset, stopped and started again, goes on from the first run's bytes",
                one.Last >= 2 * 1024 * 1024 && two.Resumed && two.First >= one.Last && two.Last >= 4 * 1024 * 1024,
                $"run1 stopped at {one.Last:N0} · run2 first={two.First:N0} last={two.Last:N0} resumed={two.Resumed}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static byte[] Payload(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · update download that survives a slow or flaky line");
        string root = Path.Combine(Path.GetTempPath(), "cx-apitest-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var server = new RangeServer();
            ResumableDownloader Dl(string cache, int stallSeconds = 10, int maxFailures = 8) =>
                new(Path.Combine(root, cache)) { StallTimeout = TimeSpan.FromSeconds(stallSeconds), MaxFailuresInARow = maxFailures, Backoff = _ => TimeSpan.Zero };
            string Target(string name) => Path.Combine(root, "out", name);
            bool Same(string path, byte[] want) => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(want);

            // 1. Clean download.
            server.Data = Payload(3 * 1024 * 1024 + 123, 1);
            var pcts = new List<int>();
            using (var dl = Dl("c1"))
                await dl.DownloadFile(server.Url, Target("clean.nupkg"), p => { lock (pcts) pcts.Add(p); });
            check("update-dl", "a clean download: the file arrives whole, progress climbs to 100",
                Same(Target("clean.nupkg"), server.Data) && pcts.Count > 0 && pcts[^1] == 100 && pcts.Zip(pcts.Skip(1)).All(z => z.First <= z.Second) && server.Requests == 1,
                $"requests={server.Requests} last%={(pcts.Count > 0 ? pcts[^1] : -1)}");
            check("update-dl", "a finished download leaves no partial file behind",
                !Directory.EnumerateFiles(Path.Combine(root, "c1")).Any(), string.Join(",", Directory.EnumerateFiles(Path.Combine(root, "c1")).Select(Path.GetFileName)));

            // 2. The connection drops at 1 MB: the next request asks for the rest only.
            int before = server.Requests; long servedBefore = server.Served;
            server.Plans = i => i == before ? new RangeServer.Plan(DropAfter: 1024 * 1024) : default;
            using (var dl = Dl("c2"))
                await dl.DownloadFile(server.Url, Target("drop.nupkg"), _ => { });
            string resumeRange; lock (server.Ranges) resumeRange = server.Ranges.Count > before + 1 ? server.Ranges[before + 1] : "";
            long resumeAt = resumeRange.StartsWith("bytes=") && long.TryParse(resumeRange[6..].TrimEnd('-'), out long r) ? r : 0;
            check("update-dl", "a dropped connection goes on from where it stopped (Range), not from zero",
                Same(Target("drop.nupkg"), server.Data) && resumeAt > 0 && server.Served - servedBefore < server.Data.Length * 3 / 2,
                $"range='{resumeRange}' served={server.Served - servedBefore:N0} of {server.Data.Length:N0}");
            string ifRange; lock (server.IfRanges) ifRange = server.IfRanges.Count > before + 1 ? server.IfRanges[before + 1] : "";
            check("update-dl", "the resume is guarded by If-Range on the ETag (a changed file is never stitched)", ifRange == "\"v1\"", ifRange);

            // 3. The server stops sending (a stalled line): no byte for StallTimeout → a new request, the rest arrives.
            before = server.Requests;
            server.Plans = i => i == before ? new RangeServer.Plan(StallMs: 8000) : default;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var dl = Dl("c3", stallSeconds: 1))
                await dl.DownloadFile(server.Url, Target("stall.nupkg"), _ => { });
            check("update-dl", "a stalled transfer is noticed in seconds and resumed (no waiting out a total cap)",
                Same(Target("stall.nupkg"), server.Data) && sw.Elapsed < TimeSpan.FromSeconds(6) && server.Requests - before >= 2,
                $"{sw.Elapsed.TotalSeconds:0.0} s, requests={server.Requests - before}");

            // 4. A 503 is worth another try; a 404 is not.
            before = server.Requests;
            server.Plans = i => i == before ? new RangeServer.Plan(Status: 503) : default;
            using (var dl = Dl("c4"))
                await dl.DownloadFile(server.Url, Target("busy.nupkg"), _ => { });
            check("update-dl", "HTTP 503 → tried again → the file arrives", Same(Target("busy.nupkg"), server.Data) && server.Requests - before == 2, $"requests={server.Requests - before}");

            before = server.Requests;
            server.Plans = _ => new RangeServer.Plan(Status: 404);
            Exception? notFound = null;
            using (var dl = Dl("c5"))
                try { await dl.DownloadFile(server.Url, Target("gone.nupkg"), _ => { }); } catch (Exception ex) { notFound = ex; }
            check("update-dl", "HTTP 404 is final at once (one request, an error that names it)",
                notFound is HttpRequestException && notFound.Message.Contains("404") && server.Requests - before == 1, $"{notFound?.GetType().Name}: {notFound?.Message} requests={server.Requests - before}");

            before = server.Requests;
            server.Plans = _ => new RangeServer.Plan(Status: 503);
            Exception? gaveUp = null;
            using (var dl = Dl("c6", maxFailures: 3))
                try { await dl.DownloadFile(server.Url, Target("down.nupkg"), _ => { }); } catch (Exception ex) { gaveUp = ex; }
            check("update-dl", "a server that keeps failing is given up after the set number of tries in a row",
                gaveUp is IOException && server.Requests - before == 4, $"{gaveUp?.GetType().Name} requests={server.Requests - before}");
            server.Plans = _ => default;

            // 5. The app closes mid-download (cancel): the partial stays; the next launch (a new downloader on the same
            //    cache) asks for the rest.
            before = server.Requests; servedBefore = server.Served;
            server.Plans = i => i == before ? new RangeServer.Plan(StallMs: 4000) : default;
            using var cts = new CancellationTokenSource();
            bool cancelled = false;
            using (var dl = Dl("c7"))
            {
                dl.Progress += f => { if (f.Bytes >= 512 * 1024) cts.Cancel(); };
                try { await dl.DownloadFile(server.Url, Target("later.nupkg"), _ => { }, cancelToken: cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            bool partKept = Directory.EnumerateFiles(Path.Combine(root, "c7"), "*.part").Any();
            check("update-dl", "closing the app mid-download stops it and keeps what arrived", cancelled && partKept && !File.Exists(Target("later.nupkg")), $"cancelled={cancelled} part={partKept}");
            server.Plans = _ => default;
            int second = server.Requests;
            using (var dl = Dl("c7"))
                await dl.DownloadFile(server.Url, Target("later.nupkg"), _ => { });
            string nextRange; lock (server.Ranges) nextRange = server.Ranges.Count > second ? server.Ranges[second] : "";
            check("update-dl", "the next launch goes on from the same byte",
                Same(Target("later.nupkg"), server.Data) && nextRange.StartsWith("bytes=") && !nextRange.StartsWith("bytes=0-"),
                $"range='{nextRange}' served={server.Served - servedBefore:N0} of {server.Data.Length:N0}");

            // 6. The file on the server changed between the two (a re-published asset): the old part is not stitched to
            //    the new file — If-Range fails, the server sends the whole new file, and that is what lands.
            before = server.Requests;
            server.Plans = i => i == before ? new RangeServer.Plan(StallMs: 4000) : default;
            using var cts2 = new CancellationTokenSource();
            using (var dl = Dl("c8"))
            {
                dl.Progress += f => { if (f.Bytes >= 512 * 1024) cts2.Cancel(); };
                try { await dl.DownloadFile(server.Url, Target("swapped.nupkg"), _ => { }, cancelToken: cts2.Token); } catch (OperationCanceledException) { }
            }
            server.Plans = _ => default;
            server.Data = Payload(2 * 1024 * 1024 + 7, 2);
            server.ETag = "\"v2\"";
            using (var dl = Dl("c8"))
                await dl.DownloadFile(server.Url, Target("swapped.nupkg"), _ => { });
            check("update-dl", "a file that changed on the server is fetched whole, never stitched onto the old part",
                Same(Target("swapped.nupkg"), server.Data), $"len={(File.Exists(Target("swapped.nupkg")) ? new FileInfo(Target("swapped.nupkg")).Length : -1)} want={server.Data.Length}");

            // 7. Velopack's small requests (release lists) carry its headers and come back as text.
            before = server.Requests;
            using (var dl = Dl("c9"))
            {
                string text = await dl.DownloadString(server.Url, new Dictionary<string, string> { ["Accept"] = "application/octet-stream", ["Authorization"] = "" });
                string accept; lock (server.Accepts) accept = server.Accepts.Count > before ? server.Accepts[before] : "";
                check("update-dl", "DownloadString passes Velopack's Accept header (an empty Authorization is left out)",
                    text.Length > 0 && accept == "application/octet-stream", $"accept='{accept}' len={text.Length}");
            }

            check("update-dl", "partial files are named from the URL's file plus a hash — safe and stable",
                ResumableDownloader.CacheKey(server.Url) == ResumableDownloader.CacheKey(server.Url)
                && ResumableDownloader.CacheKey(server.Url).StartsWith("CluadeX-9.9.9-full.nupkg-")
                && ResumableDownloader.CacheKey("https://x/a%20b%3F..%5C/evil:name.zip").IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                ResumableDownloader.CacheKey("https://x/a%20b%3F..%5C/evil:name.zip"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        section("offline · finding a new release while the app runs");
        check("update-probe", "the latest-release redirect is read as a version",
            AutoUpdateService.ParseReleaseTagUrl(new Uri("https://github.com/xjanova/cluadeX/releases/tag/v3.0.178")) == "3.0.178"
            && AutoUpdateService.ParseReleaseTagUrl(new Uri("https://github.com/xjanova/cluadeX/releases/tag/3.1.0/")) == "3.1.0", "");
        check("update-probe", "a page that did not redirect to a tag is no answer",
            AutoUpdateService.ParseReleaseTagUrl(new Uri("https://github.com/xjanova/cluadeX/releases/latest")) == null
            && AutoUpdateService.ParseReleaseTagUrl(new Uri("https://github.com/login?return_to=x")) == null
            && AutoUpdateService.ParseReleaseTagUrl(new Uri("https://github.com/xjanova/cluadeX/releases/tag/nightly")) == null
            && AutoUpdateService.ParseReleaseTagUrl(null) == null, "");
        check("update-probe", "versions compare as numbers, build metadata ignored",
            AutoUpdateService.IsNewer("3.0.179", "3.0.178+26993b4") && AutoUpdateService.IsNewer("3.0.1000", "3.0.999")
            && !AutoUpdateService.IsNewer("3.0.178", "3.0.178+26993b4") && !AutoUpdateService.IsNewer("3.0.99", "3.0.178")
            && !AutoUpdateService.IsNewer("garbage", "3.0.1") && !AutoUpdateService.IsNewer(null, "3.0.1"), "");
        var hour = AutoUpdateService.FullCheckEvery;
        check("update-probe", "a newer release on the probe → check now; the same one as running or as staged → nothing",
            AutoUpdateService.ShouldCheckNow("3.0.179", "3.0.178", null, TimeSpan.Zero)
            && !AutoUpdateService.ShouldCheckNow("3.0.178", "3.0.178+abc", null, TimeSpan.Zero)
            && !AutoUpdateService.ShouldCheckNow("3.0.179", "3.0.178", "3.0.179", TimeSpan.Zero)
            && AutoUpdateService.ShouldCheckNow("3.0.180", "3.0.178", "3.0.179", TimeSpan.Zero), "");
        check("update-probe", "no probe answer → a full check once an hour has passed, not before",
            !AutoUpdateService.ShouldCheckNow(null, "3.0.178", null, hour - TimeSpan.FromMinutes(1))
            && AutoUpdateService.ShouldCheckNow(null, "3.0.178", null, hour), "");
        check("update-probe", "the probe runs every 10 minutes", AutoUpdateService.ProbeEvery == TimeSpan.FromMinutes(10), AutoUpdateService.ProbeEvery.ToString());

        var p = new UpdateProgressInfo(UpdatePhase.Downloading, "3.0.179", 40, 12_900_000, 28_300_000, 2_202_009, false, null);
        check("update-probe", "the bar's text: sizes and speed, invariant digits",
            p.Describe(thai: false) == "12.3 / 27.0 MB · 2.1 MB/s" && p.EtaSeconds is > 6 and < 8, $"'{p.Describe(false)}' eta={p.EtaSeconds}");
        check("update-probe", "a resumed download says so (Thai)",
            (p with { Resuming = true }).Describe(thai: true).EndsWith("สัญญาณหลุด กำลังต่อจากเดิม"), (p with { Resuming = true }).Describe(true));
        check("update-probe", "before the first byte the bar shows the percent alone",
            (p with { Total = 0, Bytes = 0 }).Describe(false) == "40%" && (p with { Total = 0 }).EtaSeconds == null, "");
    }
}
