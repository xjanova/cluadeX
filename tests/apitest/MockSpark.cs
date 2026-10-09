using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

/// <summary>
/// CluadeX on an NVIDIA DGX Spark, without a Spark: the real DgxSparkService talks to a localhost stand-in for the
/// Spark's Ollama (/api/version, /api/tags, streamed /api/pull). Proves host input is reduced to a bare host
/// (nothing else reaches the URL), a probe reports the backend and its models, a download streams progress and
/// surfaces Ollama's own error, cancelling stops it, and using a Spark model raises the context window only while
/// on the Spark — switching away gives the old size back before a local llama-server could read it.
/// The debug-build override CLUADEX_SPARK_OLLAMA_PORT points the service at the mock.
/// </summary>
internal static class MockSpark
{
    static readonly List<string> Installed = ["qwen3-coder:30b", "llama3"];
    static readonly List<string> PullBodies = [];

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · DGX Spark: host input, probe, model download, context window (localhost mock of the Spark)");

        foreach (var (input, want) in new (string, string)[]
        {
            ("spark-1a2b.local", "spark-1a2b.local"), ("  192.168.1.50 ", "192.168.1.50"),
            ("http://spark:11434/", "spark"), ("https://Spark.LAN/api/tags", "spark.lan"),
            ("user@spark", "spark"), ("evil.com/../x", "evil.com"), ("[::1]", "[::1]"),
            ("", null), ("bad host!", null), ("a;rm -rf /", null), ("spark$(calc)", null),
        })
        {
            string got = DgxSparkService.NormalizeHost(input);
            check("spark", $"host \"{input}\" → {want ?? "rejected"}", got == want, $"got {got ?? "null"}");
        }

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        Environment.SetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT", port.ToString());

        var svc = new SettingsService();   // portable — never the user's settings
        AiProviderType startProvider = svc.Settings.ActiveProvider;
        uint startCtx = svc.Settings.ContextSize;
        svc.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.ContextSize = 12288;
            s.DgxSparkSavedContextSize = null;
            s.DgxSparkHost = null;
            if (s.ProviderConfigs.TryGetValue("OpenAI", out var oc) && oc != null) oc.ApiKey = null;   // switching there stays offline
        });
        var server = new LlamaServerProvider(svc);
        var mgr = new AiProviderManager(svc, new LocalGgufProvider(new LlamaInferenceService(svc), server, svc), server,
            new CostTrackingService(), new FileSystemService());
        var spark = new DgxSparkService(svc, mgr);

        // ── Probe ──
        var bad = await spark.ProbeAsync("bad host!");
        check("spark", "invalid host is refused before any request", !bad.Reachable && bad.Error == "invalid-host", bad.Error ?? "");
        var p = await spark.ProbeAsync("http://localhost/");
        check("spark", "probe finds Ollama on the Spark", p.Backend == "Ollama" && p.Port == port, $"{p.Backend} :{p.Port}");
        check("spark", "probe reads the Ollama version", p.Version == "0.12.9-mock", p.Version ?? "");
        if (p.Version != "0.12.9-mock")
        {
            // Everything below downloads models and repoints the provider. If the probe reached anything but the
            // mock (a real Ollama on this PC answers on 11434), stop here — a "test" pull once fetched 14 GB for real.
            check("spark", "STOPPED: the probe did not reach the mock — no downloads attempted", false, $"{p.Backend} {p.Version} :{p.Port}");
            listener.Stop();
            return;
        }
        check("spark", "probe lists the installed models", p.Models.SequenceEqual(Installed), string.Join(", ", p.Models));
        var gone = await spark.ProbeAsync("no-such-spark.invalid");
        check("spark", "a host that doesn't exist is unreachable, not an exception", !gone.Reachable && gone.Error == "unreachable", gone.Error ?? "");

        // ── Download onto the Spark ──
        var reports = new List<(string Status, double? Fraction)>();
        await spark.PullAsync("localhost", "gpt-oss:20b", new SyncProgress(reports.Add), CancellationToken.None);
        var fractions = reports.Where(r => r.Fraction != null).Select(r => r.Fraction.Value).ToList();
        check("spark", "download streams progress", fractions.Count >= 3, $"{reports.Count} reports, {fractions.Count} with a fraction");
        check("spark", "progress only moves forward and ends at 100 %",
            fractions.Zip(fractions.Skip(1)).All(z => z.Second >= z.First) && fractions.LastOrDefault() == 1.0,
            string.Join(" ", fractions.Select(f => f.ToString("0.00"))));
        check("spark", "the final status is success", reports.LastOrDefault().Status == "success", reports.LastOrDefault().Status ?? "");
        string body;
        lock (PullBodies) body = PullBodies.LastOrDefault() ?? "";
        check("spark", "pull request names the model and streams", body.Contains("\"model\":\"gpt-oss:20b\"") && body.Contains("\"stream\":true"), body);
        var after = await spark.ProbeAsync("localhost");
        check("spark", "the downloaded model is now listed", after.Models.Contains("gpt-oss:20b"), string.Join(", ", after.Models));

        string err = null;
        try { await spark.PullAsync("localhost", "nope:1", new SyncProgress(_ => { }), CancellationToken.None); }
        catch (InvalidOperationException ex) { err = ex.Message; }
        check("spark", "Ollama's own error reaches the user", err?.Contains("file does not exist") == true, err ?? "no exception");

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool cancelled = false;
            try { await spark.PullAsync("localhost", "slow:1", new SyncProgress(_ => { }), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check("spark", "cancel stops a download that never ends", cancelled && sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.ElapsedMilliseconds} ms");
        }

        // ── Use a Spark model: the window grows only while on the Spark ──
        await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
        var s1 = svc.Settings;
        var oll = s1.ProviderConfigs["Ollama"];
        check("spark", "use → Ollama is the active provider", mgr.ActiveProviderType == AiProviderType.Ollama, mgr.ActiveProviderType.ToString());
        check("spark", "use → Ollama points at the Spark", oll.BaseUrl == $"http://localhost:{port}" && oll.EffectiveModelId == "qwen3-coder:30b",
            $"{oll.BaseUrl} {oll.EffectiveModelId}");
        check("spark", "use → provider connected", mgr.ActiveProvider.IsReady, mgr.ActiveProvider.StatusMessage);
        check("spark", "use → window raised to 32k, old size remembered",
            s1.ContextSize == DgxSparkService.SparkContextSize && s1.DgxSparkSavedContextSize == 12288, $"{s1.ContextSize} saved={s1.DgxSparkSavedContextSize}");
        check("spark", "use → IsActive", spark.IsActive && s1.DgxSparkHost == "localhost", s1.DgxSparkHost ?? "");

        await spark.UseOllamaModelAsync("localhost", "llama3");   // Ollama already active: must still take effect
        check("spark", "second model while on Ollama takes effect", svc.Settings.ProviderConfigs["Ollama"].EffectiveModelId == "llama3",
            svc.Settings.ProviderConfigs["Ollama"].EffectiveModelId ?? "");
        check("spark", "second model keeps the ORIGINAL remembered size", svc.Settings.DgxSparkSavedContextSize == 12288,
            $"saved={svc.Settings.DgxSparkSavedContextSize}");

        await mgr.SwitchProviderAsync(AiProviderType.OpenAI);
        check("spark", "leaving the Spark gives the window back",
            svc.Settings.ContextSize == 12288 && svc.Settings.DgxSparkSavedContextSize == null && !spark.IsActive,
            $"{svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");

        svc.UpdateSettings(s => s.ContextSize = 65536);
        await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
        check("spark", "a bigger window the user chose is left alone",
            svc.Settings.ContextSize == 65536 && svc.Settings.DgxSparkSavedContextSize == null, $"{svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");
        await mgr.SwitchProviderAsync(AiProviderType.OpenAI);
        check("spark", "…and stays after leaving", svc.Settings.ContextSize == 65536, svc.Settings.ContextSize.ToString());

        svc.UpdateSettings(s =>
        {
            s.ActiveProvider = startProvider;
            s.ContextSize = startCtx;
            s.DgxSparkHost = null;
            s.DgxSparkSavedContextSize = null;
        });
        Environment.SetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT", null);
        listener.Stop();
    }

    /// <summary>IProgress that reports inline — Progress&lt;T&gt; posts to the thread pool and could arrive after the await.</summary>
    sealed class SyncProgress(Action<(string, double?)> onReport) : IProgress<(string Status, double? Fraction)>
    {
        public void Report((string Status, double? Fraction) value) => onReport(value);
    }

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            string path = ctx.Request.Url!.AbsolutePath;
            if (path == "/api/version") { await Json(res, "{\"version\":\"0.12.9-mock\"}"); return; }
            if (path == "/api/tags")
            {
                string list;
                lock (Installed) list = string.Join(",", Installed.Select(n => JsonSerializer.Serialize(new { name = n, model = n })));
                await Json(res, "{\"models\":[" + list + "]}");
                return;
            }
            if (path == "/api/pull" && ctx.Request.HttpMethod == "POST")
            {
                string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                lock (PullBodies) PullBodies.Add(body);
                string model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
                res.ContentType = "application/x-ndjson";
                res.SendChunked = true;
                var w = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                await w.WriteLineAsync("{\"status\":\"pulling manifest\"}");
                if (model == "nope:1")
                {
                    await w.WriteLineAsync("{\"error\":\"pull model manifest: file does not exist\"}");
                }
                else if (model == "slow:1")
                {
                    for (long done = 0; ; done += 1000)
                    {
                        await w.WriteLineAsync($"{{\"status\":\"pulling 5f1a\",\"digest\":\"sha256:5f1a\",\"total\":100000000,\"completed\":{done}}}");
                        await Task.Delay(200);
                    }
                }
                else
                {
                    foreach (long done in new long[] { 0, 250, 500, 1000 })
                        await w.WriteLineAsync($"{{\"status\":\"pulling 9c2e\",\"digest\":\"sha256:9c2e\",\"total\":1000,\"completed\":{done}}}");
                    await w.WriteLineAsync("{\"status\":\"verifying sha256 digest\"}");
                    await w.WriteLineAsync("{\"status\":\"success\"}");
                    lock (Installed) if (!Installed.Contains(model)) Installed.Add(model);
                }
                res.Close();
                return;
            }
            res.StatusCode = 404;
            res.Close();
        }
        catch { try { res.Abort(); } catch { } }
    }

    static async Task Json(HttpListenerResponse res, string json)
    {
        byte[] b = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json";
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
        res.Close();
    }
}
