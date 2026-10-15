using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ApiTest;

/// <summary>
/// CluadeX on an NVIDIA DGX Spark, without a Spark: the real DgxSparkService talks to a localhost stand-in for the
/// Spark's Ollama (/api/version, /api/tags, streamed /api/pull). Proves host input is reduced to a bare host
/// (nothing else reaches the URL), a probe reports the backend and its models, a download streams progress and
/// surfaces Ollama's own error, cancelling stops it, and using a Spark model raises the context window only while
/// on the Spark — switching away gives the old size back before a local llama-server could read it, and so does
/// re-pointing Ollama at this PC while it stays active. A switch that doesn't happen (another switch in progress)
/// or a Spark that doesn't answer leaves every setting as it was. The window runs several downloads at once and
/// keeps Connect locked while they run.
/// The debug-build override CLUADEX_SPARK_OLLAMA_PORT points the service at the mock.
/// </summary>
internal static class MockSpark
{
    static readonly List<string> Installed = ["qwen3-coder:30b", "llama3"];
    static readonly List<string> PullBodies = [];

    /// <summary>Set: every ordinary pull sends "pulling manifest", then waits here — so downloads overlap on purpose.</summary>
    static volatile TaskCompletionSource<bool> HoldPulls;
    static int InFlightPulls;
    /// <summary>OpenAI's connection test (GET /v1/models) reached the mock / may answer now.</summary>
    static volatile TaskCompletionSource<bool> OpenAiHit, OpenAiRelease;

    /// <summary>How the next /api/tags requests answer, in order: "ok" (the installed list), "500", "empty",
    /// "garbage" (not JSON), "hold" (wait for <see cref="TagsRelease"/>, then the list). Nothing queued = "ok".</summary>
    static readonly Queue<string> TagsScript = new();
    static volatile TaskCompletionSource<bool> TagsHeld, TagsRelease;
    /// <summary>A refused pull whose error body stalls until this is set.</summary>
    static volatile TaskCompletionSource<bool> SlowBodyRelease;

    static void ScriptTags(params string[] modes)
    {
        lock (TagsScript)
        {
            TagsScript.Clear();
            foreach (var m in modes) TagsScript.Enqueue(m);
        }
    }

    static int TagsScriptLeft() { lock (TagsScript) return TagsScript.Count; }

    /// <summary>The settings UseOllamaModelAsync writes — compared whole to prove a rollback.</summary>
    sealed record UseState(string BaseUrl, string SelectedModel, bool UseCustomModel, uint ContextSize, uint? Saved, string SparkHost);

    static UseState Snap(SettingsService svc)
    {
        var s = svc.Settings;
        var o = s.ProviderConfigs.TryGetValue("Ollama", out var c) ? c : null;
        return new UseState(o?.BaseUrl, o?.SelectedModel, o?.UseCustomModel ?? false, s.ContextSize, s.DgxSparkSavedContextSize, s.DgxSparkHost);
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) return false;
            await Task.Delay(25);
        }
        return true;
    }

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

        // ── Which model runs on this PC's GPU (status-bar readout + nvidia-smi poll) — pure rules, no network ──
        static AppSettings S(AiProviderType p, string ollamaUrl, string sparkHost = null)
        {
            var s = new AppSettings { ActiveProvider = p, DgxSparkHost = sparkHost };
            s.ProviderConfigs["Ollama"] = new ProviderConfig { BaseUrl = ollamaUrl };
            return s;
        }
        foreach (var (name, settings, want) in new (string, AppSettings, bool)[]
        {
            ("Ollama on localhost", S(AiProviderType.Ollama, "http://localhost:11434"), true),
            ("Ollama on 127.0.0.1", S(AiProviderType.Ollama, "http://127.0.0.1:11434"), true),
            ("Ollama on [::1]", S(AiProviderType.Ollama, "http://[::1]:11434"), true),
            ("Ollama with no address (its default)", S(AiProviderType.Ollama, null), true),
            ("Ollama on a LAN box", S(AiProviderType.Ollama, "http://192.168.1.50:11434"), false),
            ("Ollama on the remembered Spark", S(AiProviderType.Ollama, "http://localhost:11434", "localhost"), false),
            ("LLamaSharp", S(AiProviderType.Local, "http://192.168.1.50:11434"), true),
            ("llama-server", S(AiProviderType.LlamaServer, null), true),
            ("OpenAI", S(AiProviderType.OpenAI, "http://localhost:11434"), false),
        })
            check("spark", $"GPU on this PC: {name} → {want}", DgxSparkService.ModelRunsOnThisPc(settings) == want, "");
        check("spark", "on the Spark = exact host, not a substring",
            !DgxSparkService.IsSparkActive(S(AiProviderType.Ollama, "http://spark-old.local:11434", "spark"))
            && DgxSparkService.IsSparkActive(S(AiProviderType.Ollama, "http://Spark:11434", "spark")), "");

        // ── Failures name a kind (localized in the window), never the exception text ──
        foreach (var (name, ex, want) in new (string, Exception, SparkFailure)[]
        {
            ("connection refused", new HttpRequestException("refused", new SocketException(10061)), SparkFailure.Network),
            ("HTTP 500", new HttpRequestException("500", null, HttpStatusCode.InternalServerError), SparkFailure.Server),
            ("HttpClient timeout", new TaskCanceledException("timeout", new TimeoutException()), SparkFailure.Timeout),
            ("garbled JSON", new JsonException("bad"), SparkFailure.BadResponse),
            ("stream cut", new IOException("ended"), SparkFailure.Network),
            ("Ollama's own error", new OllamaErrorException("pull model manifest: file does not exist"), SparkFailure.Ollama),
        })
            check("spark", $"failure kind: {name}", DgxSparkService.Classify(ex) == want, DgxSparkService.Classify(ex).ToString());

        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        Environment.SetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT", port.ToString());

        var svc = new SettingsService();   // portable — never the user's settings
        AiProviderType startProvider = svc.Settings.ActiveProvider;
        uint startCtx = svc.Settings.ContextSize;
        var ollamaStart = svc.Settings.ProviderConfigs.TryGetValue("Ollama", out var oc0) && oc0 != null
            ? new ProviderConfig { BaseUrl = oc0.BaseUrl, SelectedModel = oc0.SelectedModel, UseCustomModel = oc0.UseCustomModel, CustomModelId = oc0.CustomModelId }
            : null;
        svc.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.ContextSize = 12288;
            s.DgxSparkSavedContextSize = null;
            s.DgxSparkHost = null;
            if (s.ProviderConfigs.TryGetValue("OpenAI", out var oc) && oc != null) oc.ApiKey = null;   // switching there stays offline
        });
        var openAiStart = svc.Settings.ProviderConfigs.TryGetValue("OpenAI", out var oa0) && oa0 != null
            ? new ProviderConfig { BaseUrl = oa0.BaseUrl, SelectedModel = oa0.SelectedModel, UseCustomModel = oa0.UseCustomModel, CustomModelId = oa0.CustomModelId }
            : null;
        var server = new LlamaServerProvider(svc);
        var mgr = new AiProviderManager(svc, new LocalGgufProvider(new LlamaInferenceService(svc), server, svc), server,
            new CostTrackingService(), new FileSystemService());
        var spark = new DgxSparkService(svc, mgr);

        try
        {
            await RunAgainstMock(check, svc, mgr, spark, port);
        }
        finally
        {
            HoldPulls?.TrySetResult(true);
            OpenAiRelease?.TrySetResult(true);
            TagsRelease?.TrySetResult(true);
            SlowBodyRelease?.TrySetResult(true);
            ScriptTags();
            Environment.SetEnvironmentVariable("CLUADEX_SPARK_ERRBODY_TIMEOUT_MS", null);
            svc.UpdateSettings(s =>
            {
                s.ActiveProvider = startProvider;
                s.ContextSize = startCtx;
                s.DgxSparkHost = null;
                s.DgxSparkSavedContextSize = null;
                if (ollamaStart != null) s.ProviderConfigs["Ollama"] = ollamaStart; else s.ProviderConfigs.Remove("Ollama");
                if (openAiStart != null) s.ProviderConfigs["OpenAI"] = openAiStart;   // ApiKey stays null
            });
            Environment.SetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT", null);
            listener.Stop();
        }
    }

    static async Task RunAgainstMock(Action<string, string, bool, string> check, SettingsService svc, AiProviderManager mgr, DgxSparkService spark, int port)
    {
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

        async Task<Exception> PullFails(string model)
        {
            try { await spark.PullAsync("localhost", model, new SyncProgress(_ => { }), CancellationToken.None); return null; }
            catch (Exception ex) { return ex; }
        }
        var e404 = await PullFails("http404:1");
        check("spark", "an error status with Ollama's {\"error\"} keeps Ollama's words",
            e404 is OllamaErrorException && e404.Message == "model 'http404:1' not found", $"{e404?.GetType().Name}: {e404?.Message}");
        var e500 = await PullFails("http500:1");
        check("spark", "a bare HTTP 500 is a server failure, not raw text", e500 != null && DgxSparkService.Classify(e500) == SparkFailure.Server,
            $"{e500?.GetType().Name} → {(e500 == null ? "-" : DgxSparkService.Classify(e500).ToString())}");

        // ── A refused pull's error body is read only up to 64 KB and ~10 s (the small {"error"} above is the control) ──
        var eBig = await PullFails("http500big:1");
        check("spark", "an error body over 64 KB is a server failure, not read as Ollama's words",
            eBig is HttpRequestException { StatusCode: HttpStatusCode.InternalServerError } && DgxSparkService.Classify(eBig) == SparkFailure.Server,
            $"{eBig?.GetType().Name} ({eBig?.Message.Length ?? 0} chars) → {(eBig == null ? "-" : DgxSparkService.Classify(eBig).ToString())}");
        Environment.SetEnvironmentVariable("CLUADEX_SPARK_ERRBODY_TIMEOUT_MS", "600");   // read per call: applies from the next pull
        SlowBodyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var swSlow = System.Diagnostics.Stopwatch.StartNew();
            var eSlow = await PullFails("http500slow:1");
            check("spark", "an error body that stalls is a server failure, given up on in time",
                eSlow is HttpRequestException { StatusCode: HttpStatusCode.InternalServerError } && DgxSparkService.Classify(eSlow) == SparkFailure.Server
                && swSlow.Elapsed < TimeSpan.FromSeconds(5),
                $"{eSlow?.GetType().Name} after {swSlow.ElapsedMilliseconds} ms → {(eSlow == null ? "-" : DgxSparkService.Classify(eSlow).ToString())}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLUADEX_SPARK_ERRBODY_TIMEOUT_MS", null);
            SlowBodyRelease.TrySetResult(true);
        }
        var eCut = await PullFails("truncated:1");
        check("spark", "a stream that stops before \"success\" is not a finished download", eCut != null && DgxSparkService.Classify(eCut) == SparkFailure.Network,
            $"{eCut?.GetType().Name} → {(eCut == null ? "-" : DgxSparkService.Classify(eCut).ToString())}");
        lock (Installed)
            check("spark", "…and the half download isn't listed", !Installed.Contains("truncated:1"), string.Join(", ", Installed));

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool cancelled = false;
            try { await spark.PullAsync("localhost", "slow:1", new SyncProgress(_ => { }), cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check("spark", "cancel stops a download that never ends", cancelled && sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.ElapsedMilliseconds} ms");
        }

        // ── Use a Spark model: the window grows only while on the Spark ──
        var use1 = await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
        var s1 = svc.Settings;
        var oll = s1.ProviderConfigs["Ollama"];
        check("spark", "use → reported ok", use1.Ok, use1.Failure.ToString());
        check("spark", "use → Ollama is the active provider", mgr.ActiveProviderType == AiProviderType.Ollama, mgr.ActiveProviderType.ToString());
        check("spark", "use → Ollama points at the Spark", oll.BaseUrl == $"http://localhost:{port}" && oll.EffectiveModelId == "qwen3-coder:30b",
            $"{oll.BaseUrl} {oll.EffectiveModelId}");
        check("spark", "use → provider connected", mgr.ActiveProvider.IsReady, mgr.ActiveProvider.StatusMessage);
        // Written before the switch, while Local was still active: the settings-changed check must not undo it.
        check("spark", "use → window raised to 32k, old size remembered (own write not undone)",
            s1.ContextSize == DgxSparkService.SparkContextSize && s1.DgxSparkSavedContextSize == 12288, $"{s1.ContextSize} saved={s1.DgxSparkSavedContextSize}");
        check("spark", "use → IsActive", spark.IsActive && s1.DgxSparkHost == "localhost", s1.DgxSparkHost ?? "");

        // ── The Ollama provider's connection test asks /api/tags itself: a list that didn't come is a failure ──
        var ollamaP = (OllamaProvider)mgr.GetProvider(AiProviderType.Ollama);
        string mockUrl = $"http://localhost:{port}";
        ScriptTags("empty");
        var tc = await ollamaP.TestConnectionAsync();
        check("ollama", "Test Connection: 2xx with an empty list → still success", tc.Success
            && tc.Message == string.Format(LocalizedResources.Instance["ollama.noModels"], mockUrl), tc.Message);
        ScriptTags("500");
        tc = await ollamaP.TestConnectionAsync();
        check("ollama", "Test Connection: /api/tags HTTP 500 → failure, localized, no server text",
            !tc.Success && tc.Message == string.Format(LocalizedResources.Instance["ollama.httpError"], mockUrl, 500) && !tc.Message.Contains("tags broke"), tc.Message);
        ScriptTags("garbage");
        tc = await ollamaP.TestConnectionAsync();
        check("ollama", "Test Connection: a reply that isn't the list → failure",
            !tc.Success && tc.Message == string.Format(LocalizedResources.Instance["ollama.badReply"], mockUrl), tc.Message);
        tc = await ollamaP.TestConnectionAsync();
        check("ollama", "Test Connection: the list came → success (control)", tc.Success
            && tc.Message == string.Format(LocalizedResources.Instance["ollama.connected"], mockUrl, Installed.Count), tc.Message);
        string deadUrl = $"http://127.0.0.1:{FreePort()}";
        s1.ProviderConfigs["Ollama"].BaseUrl = deadUrl;   // in memory only — no settings event
        try
        {
            tc = await ollamaP.TestConnectionAsync();
            check("ollama", "Test Connection: nothing listening → failure (was \"connected\" with the cached list)",
                !tc.Success && tc.Message == string.Format(LocalizedResources.Instance["ollama.unreachable"], deadUrl), tc.Message);
            var errors = new List<string>();
            void OnErr(string e) => errors.Add(e);
            ollamaP.OnError += OnErr;
            var cached = await ollamaP.GetAvailableModelsAsync();
            ollamaP.OnError -= OnErr;
            check("ollama", "model list while unreachable: the last list, a localized error without exception text",
                cached.Count > 0 && errors.Count == 1 && errors[0] == string.Format(LocalizedResources.Instance["ollama.unreachable"], deadUrl),
                $"{cached.Count} cached · {string.Join(" | ", errors)}");
        }
        finally { s1.ProviderConfigs["Ollama"].BaseUrl = mockUrl; }
        check("ollama", "every scripted /api/tags answer was used", TagsScriptLeft() == 0, TagsScriptLeft().ToString());

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

        // ── Leaving the Spark WITHOUT a provider switch: Ollama stays active, its address moves to this PC ──
        // Nothing listens on this port, so a re-initialise there fails fast and never reaches a real Ollama.
        string localOllama = $"http://127.0.0.1:{FreePort()}";
        svc.UpdateSettings(s => { s.ContextSize = 12288; s.DgxSparkSavedContextSize = null; });
        var use2 = await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
        check("spark", "back on the Spark (32k)", use2.Ok && svc.Settings.ContextSize == 32768 && svc.Settings.DgxSparkSavedContextSize == 12288,
            $"{use2.Failure} {svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");

        var restores = new List<SparkContextRestore>();
        void OnRestore(SparkContextRestore r) { lock (restores) restores.Add(r); }
        spark.ContextRestored += OnRestore;

        // The Settings page saves a second after each keystroke: an address that isn't a host yet says nothing about
        // where Ollama is — the window is left alone until it is one.
        foreach (string partial in new[] { "http://", "http://[fe80" })
        {
            svc.UpdateSettings(s => s.ProviderConfigs["Ollama"].BaseUrl = partial);
            check("spark", $"Ollama address half-typed (\"{partial}\") → window left alone",
                svc.Settings.ContextSize == 32768 && svc.Settings.DgxSparkSavedContextSize == 12288 && restores.Count == 0,
                $"{svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize} restores={restores.Count}");
        }

        // Settings ▸ Ollama address ▸ save, the way the Settings page writes: its own SettingsChanged event is not
        // reloaded from, but the window given back INSIDE that save (another handler writing in turn) must be.
        var own = new CluadeX.Helpers.OwnSettingsWrite();
        int pageReloads = 0;
        void Page() { if (own.Observe()) return; pageReloads++; }
        svc.SettingsChanged += Page;
        try
        {
            var (okOwn, othersOwn) = own.Run(svc, s => s.ProviderConfigs["Ollama"].BaseUrl = localOllama);
            check("settings", "page's own save of the Ollama address → told the window changed inside it (reloads ContextSize)",
                okOwn && othersOwn && svc.Settings.ContextSize == 12288 && pageReloads == 0,
                $"ok={okOwn} others={othersOwn} ctx={svc.Settings.ContextSize} reloads={pageReloads}");
            var (okPlain, othersPlain) = own.Run(svc, s => s.Temperature = s.Temperature);
            check("settings", "a save nobody reacted to → nothing to reload (control)", okPlain && !othersPlain && pageReloads == 0,
                $"ok={okPlain} others={othersPlain} reloads={pageReloads}");
            svc.UpdateSettings(s => s.MaxTokens = s.MaxTokens);   // somebody else's write
            check("settings", "a write from elsewhere still reaches the page", pageReloads == 1, pageReloads.ToString());
        }
        finally { svc.SettingsChanged -= Page; }
        check("spark", "leaving the Spark is announced once (From 32k → To the old size)",
            restores.Count == 1 && restores[0].From == 32768 && restores[0].To == 12288 && spark.LastContextRestore == restores[0],
            string.Join(", ", restores.Select(r => $"{r.From}→{r.To}")));
        spark.ContextRestored -= OnRestore;
        check("spark", "Ollama re-pointed at this PC (saved, still active) → window back",
            mgr.ActiveProviderType == AiProviderType.Ollama && !spark.IsActive
            && svc.Settings.ContextSize == 12288 && svc.Settings.DgxSparkSavedContextSize == null,
            $"{mgr.ActiveProviderType} active={spark.IsActive} {svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");

        // Ollama active on this PC, then a Spark model: the re-initialise path. Its write happens while IsActive is
        // already true, and it must survive the re-initialise's own loading events.
        var use3 = await spark.UseOllamaModelAsync("localhost", "llama3");
        check("spark", "use while Ollama is active on this PC → Spark, 32k",
            use3.Ok && spark.IsActive && svc.Settings.ContextSize == 32768 && svc.Settings.DgxSparkSavedContextSize == 12288
            && svc.Settings.ProviderConfigs["Ollama"].BaseUrl == $"http://localhost:{port}",
            $"{use3.Failure} {svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize} {svc.Settings.ProviderConfigs["Ollama"].BaseUrl}");

        svc.Settings.ProviderConfigs["Ollama"].BaseUrl = localOllama;   // changed in memory, no save …
        await mgr.ReinitializeActiveAsync();                            // … then re-initialised (Use this provider)
        check("spark", "address changed without a save, then re-initialised → window back",
            svc.Settings.ContextSize == 12288 && svc.Settings.DgxSparkSavedContextSize == null,
            $"{svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");

        // ── A switch to Local lands while the re-initialise path is still bringing Ollama up on the Spark ──
        // (use3 above is the same path without the race, and succeeds.) The mock holds Ollama's /api/tags during
        // the re-initialise; a provider switch happens meanwhile; then the re-initialise finishes.
        await RaceSwitchDuringReinit(check, svc, mgr, spark, localOllama);

        var use4 = await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
        svc.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);   // MCP set_model's forced write: no switch event
        check("spark", "ActiveProvider written straight to Local (no switch event) → window back",
            use4.Ok && svc.Settings.ContextSize == 12288 && svc.Settings.DgxSparkSavedContextSize == null,
            $"{use4.Failure} {svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");
        await mgr.SwitchProviderAsync(AiProviderType.Local);   // the manager's provider back in line with the settings

        // ── A switch that never happened is rolled back: another provider switch is in progress ──
        svc.UpdateSettings(s =>
        {
            s.ContextSize = 12288;
            s.DgxSparkSavedContextSize = null;
            s.DgxSparkHost = "spark-old.local";
            var o = s.ProviderConfigs["Ollama"];
            o.BaseUrl = localOllama;
            o.SelectedModel = "llama3";
            o.UseCustomModel = true;
            // OpenAI's connection test goes to this mock, which holds it — the other switch stays in progress.
            s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = $"http://localhost:{port}/v1", SelectedModel = "gpt-4o" };
        });
        OpenAiHit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OpenAiRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherSwitch = mgr.SwitchProviderAsync(AiProviderType.OpenAI);
        bool held = await Task.WhenAny(OpenAiHit.Task, Task.Delay(5000)) == OpenAiHit.Task;
        check("spark", "busy: the other switch is held by the mock (proved before using)", held, held ? "OpenAI /v1/models reached the mock" : "no request in 5 s");
        if (held)
        {
            var before = Snap(svc);
            var busy = await spark.UseOllamaModelAsync("localhost", "qwen3-coder:30b");
            check("spark", "busy: the switch that didn't happen is reported, not success", busy.Failure == SparkFailure.Busy, busy.Failure.ToString());
            var now = Snap(svc);
            check("spark", "busy: everything it wrote is put back", now == before, $"{now} vs {before}");
            check("spark", "busy: the other switch's provider is left alone",
                svc.Settings.ActiveProvider == AiProviderType.OpenAI && mgr.ActiveProviderType == AiProviderType.OpenAI, svc.Settings.ActiveProvider.ToString());
        }
        OpenAiRelease.TrySetResult(true);
        await otherSwitch;
        svc.UpdateSettings(s => s.ProviderConfigs["OpenAI"].ApiKey = null);
        await mgr.SwitchProviderAsync(AiProviderType.Local);

        // ── The Spark is checked before anything is written ──
        var before2 = Snap(svc);
        var down = await spark.UseOllamaModelAsync("no-such-spark.invalid", "llama3");
        check("spark", "Spark that doesn't answer → Unreachable, nothing written", down.Failure == SparkFailure.Unreachable && Snap(svc) == before2,
            $"{down.Failure} {Snap(svc)}");
        var missing = await spark.UseOllamaModelAsync("localhost", "not-on-the-spark:1b");
        check("spark", "model not on the Spark → ModelMissing, nothing written", missing.Failure == SparkFailure.ModelMissing && Snap(svc) == before2,
            $"{missing.Failure} {Snap(svc)}");

        // A model list that didn't come is not an empty list: "the model isn't on the Spark" would be a wrong answer.
        ScriptTags("500");
        var pFail = await spark.ProbeAsync("localhost");
        check("spark", "probe: /api/version answers, /api/tags HTTP 500 → reachable, list failed", pFail.Reachable && pFail.ModelListFailed && pFail.Models.Count == 0,
            $"reachable={pFail.Reachable} failed={pFail.ModelListFailed} error={pFail.Error}");
        ScriptTags("empty");
        var pEmpty = await spark.ProbeAsync("localhost");
        check("spark", "probe: an empty list is a list, not a failure (control)", pEmpty.Reachable && !pEmpty.ModelListFailed && pEmpty.Models.Count == 0,
            $"reachable={pEmpty.Reachable} failed={pEmpty.ModelListFailed}");
        ScriptTags("garbage");
        var useGarbled = await spark.UseOllamaModelAsync("localhost", "llama3");
        check("spark", "use: the list isn't Ollama's → Server failure, nothing written", useGarbled.Failure == SparkFailure.Server && Snap(svc) == before2,
            $"{useGarbled.Failure} {Snap(svc)}");
        ScriptTags("500");
        var useListFail = await spark.UseOllamaModelAsync("localhost", "llama3");
        check("spark", "use: /api/tags HTTP 500 → Server (spark.err.server), not ModelMissing; nothing written",
            useListFail.Failure == SparkFailure.Server && Snap(svc) == before2, $"{useListFail.Failure} {Snap(svc)}");
        ScriptTags("empty");
        var useEmpty = await spark.UseOllamaModelAsync("localhost", "llama3");
        check("spark", "use: an empty list → ModelMissing (control)", useEmpty.Failure == SparkFailure.ModelMissing && Snap(svc) == before2,
            $"{useEmpty.Failure} {Snap(svc)}");
        check("spark", "every scripted /api/tags answer was used (probe)", TagsScriptLeft() == 0, TagsScriptLeft().ToString());
        check("spark", "…and the provider didn't move", mgr.ActiveProviderType == AiProviderType.Local, mgr.ActiveProviderType.ToString());

        // ── The window: several downloads at once, Connect locked while they run, closing asks first ──
        svc.UpdateSettings(s => s.DgxSparkHost = "localhost");
        HoldPulls = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new DgxSparkViewModel(spark, svc, new LocalizationService());   // looks the remembered Spark up at once
        try
        {
            bool connected = await WaitUntil(() => vm.IsConnected, 6000);
            check("spark", "window connects to the mock before any download", connected && vm.Status.Contains("0.12.9-mock"), vm.Status);
            if (!connected || !vm.Status.Contains("0.12.9-mock")) return;

            var a = vm.Models.FirstOrDefault(m => m.Tag == "gpt-oss:120b");
            var b = vm.Models.FirstOrDefault(m => m.Tag == "qwen3:30b");
            check("spark", "two recommended rows not on the Spark yet", a is { CanPull: true } && b is { CanPull: true },
                string.Join(", ", vm.Models.Select(m => $"{m.Tag}:{(m.IsInstalled ? "installed" : "-")}")));
            if (a == null || b == null) return;

            vm.PullCommand.Execute(a);
            await WaitUntil(() => Volatile.Read(ref InFlightPulls) >= 1, 5000);
            check("spark", "one row downloading → another row's Download still enabled", a.IsPulling && vm.PullCommand.CanExecute(b),
                $"a.IsPulling={a.IsPulling} canB={vm.PullCommand.CanExecute(b)}");
            var q1 = vm.CloseQuestion();
            check("close", "closing while one download runs asks, naming it", q1 is { } q1q && q1q.Body.Contains(a.Name), q1?.Body ?? "no question");
            vm.AgreeToStopRunningDownloads();   // "Stop download and close" (e.g. answered for an update restart)
            check("close", "the answer covers the download running then", vm.CloseQuestion() == null, "");

            vm.PullCommand.Execute(b);
            bool both = await WaitUntil(() => Volatile.Read(ref InFlightPulls) >= 2, 5000);
            check("spark", "two downloads run at the same time", both && a.IsPulling && b.IsPulling, $"in flight={Volatile.Read(ref InFlightPulls)}");
            check("spark", "the same row can't start twice", !vm.PullCommand.CanExecute(a), "");
            check("spark", "Connect is disabled while a download runs", !vm.ConnectCommand.CanExecute(null), "");
            var q = vm.CloseQuestion();
            check("close", "a download started after the answer is asked about again (naming both)",
                q is { } qq && qq.Body.Contains(a.Name) && qq.Body.Contains(b.Name), q?.Body ?? "no question — the old answer was sticky");
            vm.AgreeToStopRunningDownloads();
            check("close", "answering again covers both", vm.CloseQuestion() == null, "");
            vm.ResetCloseConfirmation();   // the update the answer was for failed / was cancelled
            check("close", "after a close that didn't happen, the next close asks again", vm.CloseQuestion() != null, "");
            vm.SetClosingForRestart(true);
            check("close", "while the app restarts for an update, closing doesn't ask (Cancel would be ignored)", vm.CloseQuestion() == null, "");
            vm.ResetCloseConfirmation();
            check("close", "an install that didn't restart ends that — closing asks again", vm.CloseQuestion() != null, "");

            HoldPulls.TrySetResult(true);
            bool done = await WaitUntil(() => !a.IsPulling && !b.IsPulling, 10000);
            check("spark", "both downloads finish and are marked installed", done && a.IsInstalled && b.IsInstalled,
                $"a={a.IsInstalled} b={b.IsInstalled} status={vm.Status}");
            check("spark", "Connect is enabled again", vm.ConnectCommand.CanExecute(null), "");
            check("spark", "nothing to ask on close any more", vm.CloseQuestion() == null, "");

            // ── Ollama answers the Spark check, then fails its own connection test while it initialises: the
            //    switch is rolled back and the window says so in the UI language (no server text) ──
            var en = new LocalizationService();
            var localP = mgr.GetProvider(AiProviderType.Local);
            var beforeUse = Snap(svc);
            ScriptTags("ok", "500");   // the Spark check's list; then Ollama's /api/tags during its initialise
            check("spark", "a downloaded row can be used", vm.UseCommand.CanExecute(a) && ReferenceEquals(mgr.ActiveProvider, localP),
                $"canUse={vm.UseCommand.CanExecute(a)} active={mgr.ActiveProvider.DisplayName}");
            vm.UseCommand.Execute(a);
            await WaitUntil(() => !vm.IsSwitching, 10000);
            string wantFailed = en.T("spark.useFailed", en.T("spark.err.notReady"));
            check("spark", "Ollama not ready → \"Couldn't switch …\" in the UI language", vm.Status == wantFailed && vm.StatusIsError && !vm.Status.Contains("500"),
                vm.Status);
            check("spark", "…every setting put back and Local active again",
                Snap(svc) == beforeUse && mgr.ActiveProviderType == AiProviderType.Local && ReferenceEquals(mgr.ActiveProvider, localP)
                && !mgr.GetProvider(AiProviderType.Ollama).IsReady,
                $"{Snap(svc)} vs {beforeUse} · active={mgr.ActiveProviderType}");
            check("spark", "both scripted /api/tags answers were used (the failure came from the initialise)", TagsScriptLeft() == 0, TagsScriptLeft().ToString());

            // ── Leaving the Spark: the window says why the context window shrank ──
            ScriptTags();
            vm.UseCommand.Execute(a);
            await WaitUntil(() => !vm.IsSwitching, 10000);
            string N(uint v) => v.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            check("spark", "use from the window (control)", spark.IsActive && vm.Status == en.T("spark.used", a.Name, "localhost", N(32768))
                && vm.HasActiveLine && !vm.HasContextNote, vm.Status);
            await mgr.SwitchProviderAsync(AiProviderType.Local);
            string wantNote = en.T("spark.contextRestored", N(12288), N(32768));
            check("spark", "switching away → the window explains the smaller context window", vm.ContextNote == wantNote && !vm.HasActiveLine
                && vm.Models.All(m => !m.IsInUse) && svc.Settings.ContextSize == 12288, vm.ContextNote);
            var vm2 = new DgxSparkViewModel(spark, svc, new LocalizationService());
            check("spark", "…and a window opened later still says so", vm2.ContextNote == wantNote, vm2.ContextNote);
            vm2.Detach();
        }
        finally
        {
            HoldPulls?.TrySetResult(true);
            HoldPulls = null;
            vm.Detach();
        }
    }

    /// <summary>Problem: Restore() used to write ActiveProvider / ContextSize back unconditionally, so a switch to Local
    /// that landed during the re-initialise ended with settings saying "Ollama on the Spark, 32k" while Local ran.</summary>
    static async Task RaceSwitchDuringReinit(Action<string, string, bool, string> check, SettingsService svc, AiProviderManager mgr,
        DgxSparkService spark, string localOllama)
    {
        var ollama = mgr.GetProvider(AiProviderType.Ollama);
        var local = mgr.GetProvider(AiProviderType.Local);
        bool ready = ReferenceEquals(mgr.ActiveProvider, ollama) && svc.Settings.ContextSize == 12288 && svc.Settings.DgxSparkSavedContextSize == null;
        check("race", "starts on Ollama on this PC (the re-initialise path), 12k", ready,
            $"{mgr.ActiveProvider.DisplayName} {svc.Settings.ContextSize} saved={svc.Settings.DgxSparkSavedContextSize}");
        if (!ready) return;
        string hostBefore = svc.Settings.DgxSparkHost;

        TagsHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TagsRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptTags("ok", "hold");   // the Spark check's list answers; Ollama's own connection test is held
        uint? windowWhenLocalStarted = null;
        void OnSwitch(IAiProvider p) { if (ReferenceEquals(p, local)) windowWhenLocalStarted = svc.Settings.ContextSize; }
        mgr.OnProviderChanged += OnSwitch;   // after DgxSparkService's own handler: sees what Local's initialise will read
        Task<SparkUseResult> race = null;
        try
        {
            race = spark.UseOllamaModelAsync("localhost", "llama3");
            bool held = await Task.WhenAny(TagsHeld.Task, Task.Delay(5000)) == TagsHeld.Task;
            check("race", "Ollama's re-initialise is held by the mock (proved before switching)", held, held ? "/api/tags held" : "no request in 5 s");
            if (!held) return;
            check("race", "…with the Spark's settings already written", svc.Settings.ContextSize == 32768 && svc.Settings.DgxSparkHost == "localhost"
                && svc.Settings.ProviderConfigs["Ollama"].EffectiveModelId == "llama3",
                $"{svc.Settings.ContextSize} host={svc.Settings.DgxSparkHost}");

            await mgr.SwitchProviderAsync(AiProviderType.Local);   // Settings ▸ Use this provider / MCP set_model, meanwhile
            TagsRelease.TrySetResult(true);
            var result = await race;

            var s = svc.Settings;
            check("race", "the overtaken switch is reported (Busy), not success", result.Failure == SparkFailure.Busy, result.Failure.ToString());
            check("race", "Local stays active — settings and manager agree", s.ActiveProvider == AiProviderType.Local && ReferenceEquals(mgr.ActiveProvider, local),
                $"settings={s.ActiveProvider} manager={mgr.ActiveProvider.DisplayName}");
            check("race", "the window was back to 12k before Local initialised", windowWhenLocalStarted == 12288, windowWhenLocalStarted?.ToString() ?? "no switch seen");
            check("race", "after the rollback: 12k, nothing remembered, Ollama's address and the Spark host as before",
                s.ContextSize == 12288 && s.DgxSparkSavedContextSize == null && s.ProviderConfigs["Ollama"].BaseUrl == localOllama
                && s.DgxSparkHost == hostBefore && !spark.IsActive,
                $"{s.ContextSize} saved={s.DgxSparkSavedContextSize} url={s.ProviderConfigs["Ollama"].BaseUrl} host={s.DgxSparkHost}");
        }
        finally
        {
            mgr.OnProviderChanged -= OnSwitch;
            TagsRelease.TrySetResult(true);
            if (race != null) try { await race; } catch { /* reported above */ }
            ScriptTags();
        }
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
                string mode;
                lock (TagsScript) mode = TagsScript.Count > 0 ? TagsScript.Dequeue() : "ok";
                if (mode == "500")
                {
                    res.StatusCode = 500;
                    await Json(res, "{\"error\":\"mock: tags broke\"}");
                    return;
                }
                if (mode == "empty") { await Json(res, "{\"models\":[]}"); return; }
                if (mode == "garbage") { await Json(res, "<html>mock: not ollama</html>"); return; }
                if (mode == "hold")
                {
                    TagsHeld?.TrySetResult(true);
                    if (TagsRelease is { } release) await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                string list;
                lock (Installed) list = string.Join(",", Installed.Select(n => JsonSerializer.Serialize(new { name = n, model = n })));
                await Json(res, "{\"models\":[" + list + "]}");
                return;
            }
            if (path == "/v1/models")
            {
                // OpenAI's connection test, pointed here on purpose: held until the test lets it answer.
                OpenAiHit?.TrySetResult(true);
                if (OpenAiRelease is { } release) await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                res.StatusCode = 401;
                await Json(res, "{\"error\":{\"message\":\"mock: invalid key\",\"type\":\"invalid_request_error\"}}");
                return;
            }
            if (path == "/api/pull" && ctx.Request.HttpMethod == "POST")
            {
                string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                lock (PullBodies) PullBodies.Add(body);
                string model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
                if (model == "http404:1")
                {
                    res.StatusCode = 404;
                    await Json(res, $"{{\"error\":\"model '{model}' not found\"}}");
                    return;
                }
                if (model == "http500:1")
                {
                    res.StatusCode = 500;
                    res.Close();
                    return;
                }
                if (model == "http500big:1")
                {
                    // Valid {"error": …} JSON, but 100 KB of it — not Ollama's one-line refusal.
                    res.StatusCode = 500;
                    await Json(res, "{\"error\":\"" + new string('x', 100_000) + "\"}");
                    return;
                }
                if (model == "http500slow:1")
                {
                    // The headers come, then the error body stalls.
                    res.StatusCode = 500;
                    res.ContentType = "application/json";
                    res.SendChunked = true;
                    var sw = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
                    await sw.WriteAsync("{\"error\":\"slo");
                    if (SlowBodyRelease is { } release) await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await sw.WriteAsync("w\"}");
                    res.Close();
                    return;
                }
                res.ContentType = "application/x-ndjson";
                res.SendChunked = true;
                var w = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                await w.WriteLineAsync("{\"status\":\"pulling manifest\"}");
                if (model == "nope:1")
                {
                    await w.WriteLineAsync("{\"error\":\"pull model manifest: file does not exist\"}");
                }
                else if (model == "truncated:1")
                {
                    await w.WriteLineAsync("{\"status\":\"pulling 7d1f\",\"digest\":\"sha256:7d1f\",\"total\":1000,\"completed\":250}");
                    // ends here: no "success"
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
                    if (HoldPulls is { } hold)
                    {
                        Interlocked.Increment(ref InFlightPulls);
                        try { await hold.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
                        finally { Interlocked.Decrement(ref InFlightPulls); }
                    }
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
