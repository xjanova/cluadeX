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
/// OpenAI and Gemini without keys: their real provider classes talk to a localhost server that records
/// each request body and answers in the provider's wire format (SSE chunks, model lists, errors). Proves
/// what CluadeX SENDS per model and that it PARSES the documented response shapes — not that the live
/// service agrees, which needs a key.
/// </summary>
internal static class MockProviders
{
    static readonly List<(string Method, string Path, string Body)> Captured = new();

    const string OpenAiChatSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"pon\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"g\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n" +
        "data: [DONE]\n\n";

    const string OpenAiModels =
        "{\"object\":\"list\",\"data\":[{\"id\":\"gpt-6-sol\",\"created\":3},{\"id\":\"gpt-6-astra\",\"created\":5},{\"id\":\"gpt-5.5\",\"created\":1}]}";

    const string GeminiSse =
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"plan\",\"thought\":true},{\"text\":\"po\"}]}}]}\n\n" +
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ng\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":12,\"candidatesTokenCount\":2,\"thoughtsTokenCount\":5}}\n\n";

    const string GeminiBlockedSse =
        "data: {\"candidates\":[{\"finishReason\":\"SAFETY\"}]}\n\n";

    const string GeminiModels =
        "{\"models\":[" +
        "{\"name\":\"models/gemini-3.8-flash\",\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]}," +
        "{\"name\":\"models/gemini-2.5-flash-preview-tts\",\"supportedGenerationMethods\":[\"generateContent\"]}," +
        "{\"name\":\"models/text-embedding-004\",\"supportedGenerationMethods\":[\"embedContent\"]}," +
        "{\"name\":\"models/gemma-3-27b-it\",\"supportedGenerationMethods\":[\"generateContent\"]}]}";

    const string GeminiNotFound =
        "{\"error\":{\"code\":404,\"message\":\"models/bad-model is not found for API version v1beta\",\"status\":\"NOT_FOUND\"}}";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · OpenAI / Gemini wire format (localhost mock)");

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        string root = $"http://localhost:{port}";

        var svc = new SettingsService();   // portable — never the user's settings
        var s = svc.Settings;
        s.MaxTokens = 4096;
        s.Temperature = 0.6f;
        s.ExtendedThinkingEnabled = false;

        // ── OpenAI: reasoning model ──
        var cost = new CostTrackingService();
        s.ReasoningEffort = "max";
        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = root + "/v1", SelectedModel = "gpt-5.5" };
        var oa = new OpenAiProvider(svc, cost);
        string err = null; oa.OnError += e => err = e;
        string text = await Collect(oa.ChatAsync([], "ping", "sys"));
        var body = LastBody("/v1/chat/completions");
        check("openai", "reasoning model: reasoning_effort rounded to the model's top (max → xhigh)",
            Str(body, "reasoning_effort") == "xhigh", Raw(body));
        check("openai", "reasoning model: max_completion_tokens ≥ 32000, no temperature/top_p/max_tokens",
            Int(body, "max_completion_tokens") >= 32000 && !Has(body, "temperature") && !Has(body, "top_p") && !Has(body, "max_tokens"), Raw(body));
        check("openai", "stream asks for usage", body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), Raw(body));
        check("openai", "streamed text assembled", text == "pong" && err == null, $"text='{text}' err={err}");
        check("openai", "usage recorded (cached tokens split out)", cost.TotalInputTokens == 6 && cost.TotalOutputTokens == 2,
            $"in={cost.TotalInputTokens} out={cost.TotalOutputTokens}");

        // ── OpenAI: classic model ──
        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = root + "/v1", SelectedModel = "gpt-4o" };
        await Collect(new OpenAiProvider(svc).ChatAsync([], "ping", "sys"));
        body = LastBody("/v1/chat/completions");
        check("openai", "gpt-4o: temperature + max_tokens, no reasoning_effort",
            Has(body, "temperature") && Has(body, "max_tokens") && !Has(body, "reasoning_effort"), Raw(body));

        // ── OpenAI: model listing + Test Connection ──
        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = root + "/v1", SelectedModel = "gpt-6-sol" };
        var listed = await new OpenAiProvider(svc).ListModelsAsync();
        check("openai", "model list newest first", listed != null && string.Join(",", listed) == "gpt-6-astra,gpt-6-sol,gpt-5.5",
            listed == null ? "null" : string.Join(",", listed));
        var (ok, msg) = await new OpenAiProvider(svc).TestConnectionAsync();
        check("openai", "test connection finds the picked model", ok && !msg.Contains("isn't in"), msg);
        s.ProviderConfigs["OpenAI"].SelectedModel = "gpt-9";
        (ok, msg) = await new OpenAiProvider(svc).TestConnectionAsync();
        check("openai", "test connection warns about a model the key doesn't have", ok && msg.Contains("gpt-9"), msg);

        // ── Gemini 3: thinkingLevel ──
        var gcost = new CostTrackingService();
        s.ReasoningEffort = "max";
        s.ExtendedThinkingEnabled = true;
        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "gemini-3.8-flash" };
        var gm = new GeminiProvider(svc, gcost);
        err = null; gm.OnError += e => err = e;
        var chunks = new List<string>();
        await foreach (var c in gm.ChatAsync([], "ping", "sys")) chunks.Add(c);
        body = LastBody(":streamGenerateContent");
        var gen = body.GetProperty("generationConfig");
        var tc = gen.TryGetProperty("thinkingConfig", out var tce) ? tce : default;
        check("gemini", "Gemini 3: thinkingLevel high (max rounds down), no thinkingBudget",
            tc.ValueKind == JsonValueKind.Object && Str(tc, "thinkingLevel") == "high" && !Has(tc, "thinkingBudget"), Raw(gen));
        check("gemini", "Gemini 3: thought summaries requested when Think is on", tc.ValueKind == JsonValueKind.Object && Has(tc, "includeThoughts"), Raw(gen));
        check("gemini", "Gemini 3: no temperature/topP, maxOutputTokens ≥ 32768",
            !Has(gen, "temperature") && !Has(gen, "topP") && Int(gen, "maxOutputTokens") >= 32768, Raw(gen));
        string visible = string.Concat(chunks.Where(c => !c.StartsWith("<thinking>")));
        check("gemini", "every part read; thought parts kept apart from the answer",
            visible == "pong" && chunks.Contains("<thinking>plan</thinking>") && err == null, string.Join(" | ", chunks));
        check("gemini", "usage recorded (thinking counted as output)", gcost.TotalInputTokens == 12 && gcost.TotalOutputTokens == 7,
            $"in={gcost.TotalInputTokens} out={gcost.TotalOutputTokens}");

        // ── Gemini 2.5: thinkingBudget + sampling ──
        s.ReasoningEffort = "medium";
        s.ExtendedThinkingEnabled = false;
        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "gemini-2.5-flash" };
        await Collect(new GeminiProvider(svc).ChatAsync([], "ping", "sys"));
        gen = LastBody(":streamGenerateContent").GetProperty("generationConfig");
        tc = gen.TryGetProperty("thinkingConfig", out tce) ? tce : default;
        check("gemini", "Gemini 2.5: thinkingBudget 8192 for medium, no thinkingLevel, temperature kept",
            tc.ValueKind == JsonValueKind.Object && Int(tc, "thinkingBudget") == 8192 && !Has(tc, "thinkingLevel") && Has(gen, "temperature"), Raw(gen));

        // ── Gemini: auto + Think off sends no thinkingConfig at all ──
        s.ReasoningEffort = "auto";
        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "gemini-3.8-flash" };
        await Collect(new GeminiProvider(svc).ChatAsync([], "ping", "sys"));
        gen = LastBody(":streamGenerateContent").GetProperty("generationConfig");
        check("gemini", "auto + Think off → model default (no thinkingConfig)", !Has(gen, "thinkingConfig"), Raw(gen));

        // ── Gemini: listing, unknown model, safety block ──
        var glisted = await new GeminiProvider(svc).ListModelsAsync();
        check("gemini", "model list: generateContent only, no tts/embedding, Gemini before Gemma",
            glisted != null && string.Join(",", glisted) == "gemini-3.8-flash,gemma-3-27b-it", glisted == null ? "null" : string.Join(",", glisted));

        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "bad-model" };
        var gbad = new GeminiProvider(svc);
        err = null; gbad.OnError += e => err = e;
        await Collect(gbad.ChatAsync([], "ping", "sys"));
        check("gemini", "unknown model → says the model isn't available", err != null && err.Contains("isn't available"), err ?? "(no error)");

        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "blocked-model" };
        var gblock = new GeminiProvider(svc);
        err = null; gblock.OnError += e => err = e;
        await Collect(gblock.ChatAsync([], "ping", "sys"));
        check("gemini", "safety block → explained instead of an empty reply", err != null && err.Contains("SAFETY"), err ?? "(no error)");

        listener.Stop();
    }

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            string path = ctx.Request.Url!.AbsolutePath;
            lock (Captured) Captured.Add((ctx.Request.HttpMethod, path, body));

            (int status, string type, string payload) = path switch
            {
                "/v1/chat/completions" => (200, "text/event-stream", OpenAiChatSse),
                "/v1/models" => (200, "application/json", OpenAiModels),
                "/v1beta/models" => (200, "application/json", GeminiModels),
                _ when path.Contains("bad-model") => (404, "application/json", GeminiNotFound),
                _ when path.Contains("blocked-model") => (200, "text/event-stream", GeminiBlockedSse),
                _ when path.EndsWith(":streamGenerateContent") => (200, "text/event-stream", GeminiSse),
                _ => (404, "application/json", "{\"error\":{\"message\":\"no route\"}}"),
            };
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var sb = new StringBuilder();
        await foreach (var t in stream) sb.Append(t);
        return sb.ToString();
    }

    static JsonElement LastBody(string pathPart)
    {
        lock (Captured)
        {
            var hit = Captured.LastOrDefault(c => c.Path.Contains(pathPart) && c.Body.Length > 0);
            return JsonDocument.Parse(hit.Body ?? "{}").RootElement.Clone();
        }
    }

    static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
    static string Str(JsonElement e, string name) => Has(e, name) ? e.GetProperty(name).ToString() : null;
    static int Int(JsonElement e, string name) => Has(e, name) && e.GetProperty(name).TryGetInt32(out var i) ? i : -1;
    static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "(none)" : e.GetRawText();
}
