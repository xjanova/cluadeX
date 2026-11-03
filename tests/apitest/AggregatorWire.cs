using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

/// <summary>
/// Every OpenAI-compatible preset (OpenRouter, Groq, DeepSeek, … LM Studio, custom) through the real CompatibleProvider
/// against a localhost server that records each request and answers in that service's dialect: a streamed reply with
/// reasoning, a streamed tool call in fragments (whole, without an index, for xAI), the tool round trip, a refused key,
/// a model that refuses tools, and each service's own /models shape. Proves what CluadeX SENDS to each service and that
/// it PARSES what the docs say comes back — not that the live service agrees, which needs a key and costs money.
/// </summary>
internal static class AggregatorWire
{
    const string Key = "sk-agg-secret-123";

    record Hit(string Method, string Path, string Body, Dictionary<string, string> Headers);
    static readonly List<Hit> Hits = new();

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · OpenAI-compatible services (localhost mock)");

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        string root = $"http://localhost:{port}";

        var svc = new SettingsService();   // portable — never the user's settings
        var s = svc.Settings;
        s.MaxTokens = 20_000;
        s.Temperature = 0.4f;

        // ── every preset: chat, stream, tools, error, fallback, listing ──
        foreach (var preset in CompatiblePresets.All)
        {
            string t = preset.Type.ToString();
            string id = t.ToLowerInvariant();
            ProviderConfig Cfg(string model) => new() { ApiKey = Key, BaseUrl = $"{root}/{id}", SelectedModel = model };

            // chat (streamed)
            var cost = new CostTrackingService();
            s.ProviderConfigs[t] = Cfg("chat-ok");
            var p = new CompatibleProvider(svc, preset, cost);
            string err = null; p.OnError += e => err = e;
            var chunks = new List<string>();
            await foreach (var c in p.ChatAsync([], "ping", "sys")) chunks.Add(c);
            var hit = Last(id, "/chat/completions");
            var body = Json(hit.Body);
            string visible = string.Concat(chunks.Where(c => !c.StartsWith("<thinking>")));
            check(t, "chat: streamed reply assembled, reasoning shown once before it",
                visible == "pong" && chunks.Count(c => c.StartsWith("<thinking>")) == 1 && chunks[0] == "<thinking>think</thinking>" && err == null,
                $"{string.Join(" | ", chunks)} err={err}");
            check(t, "chat: model, stream, no stray fields",
                Str(body, "model") == "chat-ok" && body.GetProperty("stream").GetBoolean() && !Has(body, "tools") && !Has(body, "tool_choice")
                && !Has(body, "parallel_tool_calls"), Raw(body));
            check(t, preset.StreamUsage ? "chat: asks for usage in the stream (documented)" : "chat: no stream_options (not documented here)",
                Has(body, "stream_options") == preset.StreamUsage, Raw(body));
            int wantMax = preset.MaxOutputCap is int cap ? Math.Min(20_000, cap) : 20_000;
            check(t, $"chat: max_tokens {wantMax}", Int(body, "max_tokens") == wantMax, Raw(body));
            bool keyOk = preset.AuthHeader == "api-key"
                ? hit.Headers.GetValueOrDefault("api-key") == Key && !hit.Headers.ContainsKey("Authorization")
                : hit.Headers.GetValueOrDefault("Authorization") == "Bearer " + Key;
            check(t, $"auth: key in {preset.AuthHeader}", keyOk, string.Join(", ", hit.Headers.Keys));
            var row = cost.Log.ReadAll().LastOrDefault();
            if (preset.Local)
                check(t, "usage recorded as free local use (like Ollama): tokens on the Billing page, nothing in the spend ledger",
                    cost.TotalCostUsd == 0m && cost.TotalInputTokens == 0 && row is { Kind: CluadeX.Services.Billing.UsageCostKind.Free, Usd: 0m, Input: 10, Output: 2 } && row.Provider == t,
                    $"usd={cost.TotalCostUsd} row={row?.Provider}/{row?.Kind}/{row?.Input}/{row?.Output}");
            else
                check(t, "usage recorded: tokens, cached split out, unknown price → $0 (never the Sonnet fallback), on this service's own Billing tab",
                    cost.TotalInputTokens == 6 && cost.TotalCacheReadTokens == 4 && cost.TotalOutputTokens == 2 && cost.TotalCostUsd == 0m && row?.Provider == t,
                    $"in={cost.TotalInputTokens} cache={cost.TotalCacheReadTokens} out={cost.TotalOutputTokens} usd={cost.TotalCostUsd} tab={row?.Provider}");

            // tool round trip
            s.ProviderConfigs[t] = Cfg("tool-ok");
            p = new CompatibleProvider(svc, preset, cost);
            var conv = Ask();
            var deltas = new List<string>();
            var r1 = await p.ChatWithToolsAsync(conv, "sys", Echo(), d => deltas.Add(d));
            var b1 = Json(Last(id, "/chat/completions").Body);
            var call = r1.ToolCalls.FirstOrDefault();
            check(t, "tools: streamed call assembled (name, id, fragmented arguments)",
                r1.StopReason == "tool_use" && call?.Name == "echo" && call.Input.GetProperty("text").GetString() == "ping"
                && call.Id.Length > 0 && r1.TextContent == "Let me echo" && r1.ThinkingContent == "plan",
                $"stop={r1.StopReason} calls={r1.ToolCalls.Count} text={r1.TextContent} think={r1.ThinkingContent}");
            check(t, "tools: text streamed live, keep-alives sent", deltas.Contains("Let me echo") && deltas.Contains(""), string.Join("|", deltas));
            check(t, "tools: tools[] sent as functions, tool_choice left to the default (auto)",
                Has(b1, "tools") && b1.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "echo" && !Has(b1, "tool_choice"),
                Raw(b1));

            conv.Add(r1.ToAssistantMessage());
            conv.Add(new NativeMessage { Role = "user", Content = [new ContentBlock { Type = "tool_result", ToolUseId = call?.Id, Content = "ping" }] });
            var r2 = await p.ChatWithToolsAsync(conv, "sys", Echo());
            var b2 = Json(Last(id, "/chat/completions").Body);
            var msgs = b2.GetProperty("messages").EnumerateArray().ToList();
            var asst = msgs.FirstOrDefault(m => Str(m, "role") == "assistant");
            var toolMsg = msgs.FirstOrDefault(m => Str(m, "role") == "tool");
            string sentCallId = asst.ValueKind == JsonValueKind.Object && Has(asst, "tool_calls") ? asst.GetProperty("tool_calls")[0].GetProperty("id").GetString() : null;
            check(t, "tools: round trip — call id echoed on the assistant turn and the tool reply, answer read",
                r2.TextContent == "done" && r2.StopReason == "end_turn" && sentCallId != null && Str(toolMsg, "tool_call_id") == sentCallId
                && (!preset.ShortToolCallIds || System.Text.RegularExpressions.Regex.IsMatch(sentCallId, "^[A-Za-z0-9]{9}$")),
                $"answer={r2.TextContent} id={sentCallId} tool_call_id={Str(toolMsg, "tool_call_id")}");
            string field = id is "openrouter" or "groq" ? "reasoning" : "reasoning_content";
            bool echoed = asst.ValueKind == JsonValueKind.Object && Str(asst, field) == "plan";
            check(t, preset.EchoReasoning ? $"tools: earlier reasoning sent back as {field}" : "tools: reasoning not sent back",
                echoed == preset.EchoReasoning, Raw(asst));

            // a forced tool: named where the service takes it, otherwise left to auto
            await p.ChatWithToolsAsync(Ask(), "sys", Echo(), toolChoice: "echo");
            var bf = Json(Last(id, "/chat/completions").Body);
            bool named = Has(bf, "tool_choice") && bf.GetProperty("tool_choice").ValueKind == JsonValueKind.Object
                && bf.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString() == "echo";
            check(t, preset.ToolChoice == ToolChoiceSupport.Full ? "forced tool: named tool_choice" : "forced tool: only auto here, so none sent",
                preset.ToolChoice == ToolChoiceSupport.Full ? named : !Has(bf, "tool_choice"), Raw(bf));

            // a refused key: explained, with where to get a new one, and the key never in the text
            s.ProviderConfigs[t] = Cfg("err-401");
            p = new CompatibleProvider(svc, preset, cost);
            err = null; p.OnError += e => err = e;
            await foreach (var _ in p.ChatAsync([], "ping", "sys")) { }
            check(t, "error: 401 explained, key page named, key never echoed",
                err != null && err.Contains("rejected the API key") && !err.Contains(Key)
                && err.Contains(preset.KeysUrl.Length == 0 ? "own console" : preset.KeysUrl.Replace("https://", "")), err ?? "(no error)");

            // a model that refuses tools → the text protocol, said so
            s.ProviderConfigs[t] = Cfg("reject-tools");
            p = new CompatibleProvider(svc, preset, cost);
            bool before = p.SupportsNativeToolUse;
            var rr = await p.ChatWithToolsAsync(Ask(), "sys", Echo());
            check(t, "tools refused → switches this model to the text protocol, says so",
                before && rr.StopReason == "error" && rr.TextContent!.Contains("text tool protocol") && !p.SupportsNativeToolUse,
                $"{rr.StopReason}: {rr.TextContent}");
            s.ProviderConfigs[t] = Cfg("tool-ok");
            check(t, "…only that model: another model keeps native tools", p.SupportsNativeToolUse, "");

            // listing + Test Connection
            s.ProviderConfigs[t] = Cfg(preset.DefaultModel.Length > 0 ? preset.DefaultModel : "model-b");
            p = new CompatibleProvider(svc, preset, cost);
            var (ok, msg) = await p.TestConnectionAsync();
            check(t, "test connection", ok && !msg.Contains(Key), msg);
            if (preset.ListsModels || preset.Local)
            {
                var listed = await p.ListModelsAsync();
                check(t, "model list: chat models only", listed is { Count: > 0 } && !listed.Any(m => m.Contains("embed") || m.Contains("whisper")),
                    listed == null ? "null" : string.Join(",", listed));
            }
        }

        // ── service-specific shapes ──
        await Specifics(check, svc, root);
    }

    static async Task Specifics(Action<string, string, bool, string> check, SettingsService svc, string root)
    {
        var s = svc.Settings;
        CompatibleProvider P(AiProviderType type, string model, CostTrackingService cost = null, string baseUrl = null, string key = Key)
        {
            s.ProviderConfigs[type.ToString()] = new ProviderConfig
                { ApiKey = key, BaseUrl = baseUrl ?? $"{root}/{type.ToString().ToLowerInvariant()}", SelectedModel = model };
            return new CompatibleProvider(svc, CompatiblePresets.For(type), cost);
        }

        // OpenRouter: identifies the app; its /models teaches price, window and tool support
        var or = P(AiProviderType.OpenRouter, "vendor/coder");
        await or.TestConnectionAsync();
        var h = Last("openrouter", "/models").Headers;
        check("openrouter", "sends HTTP-Referer + X-OpenRouter-Title (+ the old X-Title)",
            h.GetValueOrDefault("HTTP-Referer") == "https://github.com/xjanova/cluadeX" && h.GetValueOrDefault("X-OpenRouter-Title") == "CluadeX"
            && h.GetValueOrDefault("X-Title") == "CluadeX", string.Join(", ", h.Select(kv => kv.Key + "=" + kv.Value)));
        var orList = await or.ListModelsAsync();
        check("openrouter", "image-only models dropped, newest first", orList != null && string.Join(",", orList) == "vendor/coder,vendor/no-tools",
            orList == null ? "null" : string.Join(",", orList));
        check("openrouter", "context window from /models feeds the context meter",
            ContextMemoryService.ContextWindowFor(AiProviderType.OpenRouter, "vendor/coder", 4096) == 262_144,
            ContextMemoryService.ContextWindowFor(AiProviderType.OpenRouter, "vendor/coder", 4096).ToString());
        check("openrouter", "unlisted model: 128K default", ContextMemoryService.ContextWindowFor(AiProviderType.Groq, "nothing-known", 4096) == 128_000, "");
        var orCost = new CostTrackingService();
        or = P(AiProviderType.OpenRouter, "vendor/coder", orCost);
        await foreach (var _ in or.ChatAsync([], "ping", "sys")) { }
        // 6 new input × $2/M + 4 cached × $2/M (no cache price listed → input price) + 2 output × $8/M
        check("openrouter", "cost at the price /models reported ($2 / $8 per M)", orCost.TotalCostUsd == 0.000036m, orCost.TotalCostUsd.ToString());
        or = P(AiProviderType.OpenRouter, "vendor/no-tools");
        check("openrouter", "a model /models lists without tools goes straight to the text protocol", !or.SupportsNativeToolUse, "");
        or = P(AiProviderType.OpenRouter, "or-404-tools");
        var r404 = await or.ChatWithToolsAsync(Ask(), "sys", Echo());
        check("openrouter", "404 \"no endpoints that support tool use\" → text protocol", r404.StopReason == "error" && !or.SupportsNativeToolUse, r404.TextContent ?? "");

        // Groq: usage under x_groq, inactive and speech models dropped
        var gCost = new CostTrackingService();
        var gq = P(AiProviderType.Groq, "chat-ok", gCost);
        await foreach (var _ in gq.ChatAsync([], "ping", "sys")) { }
        check("groq", "usage read from x_groq.usage", gCost.TotalOutputTokens == 2 && gCost.TotalInputTokens == 6, $"in={gCost.TotalInputTokens} out={gCost.TotalOutputTokens}");
        var gList = await gq.ListModelsAsync();
        check("groq", "inactive + whisper dropped", gList != null && string.Join(",", gList) == "openai/gpt-oss-120b", gList == null ? "null" : string.Join(",", gList));

        // Together: a bare array, priced per million; Hugging Face: providers[]; Mistral: capabilities
        var tList = await P(AiProviderType.Together, "Org/Chat-1").ListModelsAsync();
        check("together", "bare-array /models read, embedding dropped, price learned",
            tList != null && string.Join(",", tList) == "Org/Chat-1" && CostTrackingService.PricingOf("Org/Chat-1").InputPerMToken == 0.6m,
            tList == null ? "null" : string.Join(",", tList));
        var hList = await P(AiProviderType.HuggingFace, "org/hf-chat").ListModelsAsync();
        check("huggingface", "providers[] read: window, tools, price",
            hList?.SequenceEqual(["org/hf-chat"]) == true && CompatibleProvider.ContextWindowOf(AiProviderType.HuggingFace, "org/hf-chat") == 65_536
            && CompatibleProvider.InfoOf(AiProviderType.HuggingFace, "org/hf-chat")?.Tools == true
            && CostTrackingService.PricingOf("org/hf-chat").OutputPerMToken == 0.6m, hList == null ? "null" : string.Join(",", hList));
        var mList = await P(AiProviderType.Mistral, "mistral-large-4").ListModelsAsync();
        check("mistral", "capabilities.completion_chat=false dropped, 1M window learned",
            mList?.SequenceEqual(["mistral-large-4"]) == true && CompatibleProvider.ContextWindowOf(AiProviderType.Mistral, "mistral-large-4") == 1_000_000,
            mList == null ? "null" : string.Join(",", mList));
        check("mistral", "foreign tool-call ids become 9 letters/digits, stable; a 9-char id is kept",
            CompatibleProvider.ShortId("toolu_01ABCdef") is { Length: 9 } sid && sid.All(char.IsAsciiLetterOrDigit)
            && sid == CompatibleProvider.ShortId("toolu_01ABCdef") && CompatibleProvider.ShortId("Ab3dE6gH9") == "Ab3dE6gH9", "");

        // adapting to what a service refuses
        var ds = P(AiProviderType.DeepSeek, "no-stream-options");
        string dsErr = null; ds.OnError += e => dsErr = e;
        int hitsBefore = Count("deepseek");
        string dsText = "";
        await foreach (var c in ds.ChatAsync([], "ping", "sys")) if (!c.StartsWith("<thinking>")) dsText += c;
        var dsLast = Json(Last("deepseek", "/chat/completions").Body);
        check("adapt", "stream_options refused (400) → sent again without it, answer read",
            dsText == "pong" && dsErr == null && !Has(dsLast, "stream_options") && Count("deepseek") - hitsBefore == 2, $"text={dsText} err={dsErr}");

        var az = P(AiProviderType.AzureOpenAI, "max-completion");
        string azText = "";
        await foreach (var c in az.ChatAsync([], "ping", "sys")) if (!c.StartsWith("<thinking>")) azText += c;
        var azLast = Json(Last("azureopenai", "/chat/completions").Body);
        check("adapt", "\"use max_completion_tokens\" → switches the field", azText == "pong" && Int(azLast, "max_completion_tokens") == 20_000 && !Has(azLast, "max_tokens"), Raw(azLast));

        var qw = P(AiProviderType.Qwen, "no-tool-stream");
        var qr = await qw.ChatWithToolsAsync(Ask(), "sys", Echo());
        var qLast = Json(Last("qwen", "/chat/completions").Body);
        check("adapt", "tools refused with stream=true → non-streamed tool call read",
            qr.ToolCalls.Count == 1 && qr.ToolCalls[0].Name == "echo" && !qLast.GetProperty("stream").GetBoolean(), $"{qr.StopReason} {qr.TextContent}");

        var ov = P(AiProviderType.Groq, "overflow");
        int ovBefore = Count("groq");
        string ovMsg = null;
        try { await ov.ChatWithToolsAsync(Ask(), "sys", Echo()); } catch (HttpRequestException ex) { ovMsg = ex.Message; }
        check("adapt", "context overflow is NOT adapted: one request, thrown with the API's words (the agent loop compacts)",
            ovMsg != null && ovMsg.Contains("maximum context length") && Count("groq") - ovBefore == 1 && ov.SupportsNativeToolUse, ovMsg ?? "(nothing thrown)");

        // the configuration guard
        var bad = P(AiProviderType.Groq, "chat-ok", baseUrl: "http://example.com/v1");
        var (bok, bmsg) = await bad.TestConnectionAsync();
        check("guard", "plain http to another host is refused before anything is sent", !bok && bmsg.Contains("https"), bmsg);
        var noUrl = P(AiProviderType.AzureOpenAI, "dep", baseUrl: "");
        var (nok, nmsg) = await noUrl.TestConnectionAsync();
        check("guard", "Azure without a Base URL says what to type", !nok && nmsg.Contains("openai.azure.com"), nmsg);
        var noKey = P(AiProviderType.Groq, "chat-ok", key: "");
        var (kok, kmsg) = await noKey.TestConnectionAsync();
        check("guard", "a cloud service without a key: refused, no request", !kok, kmsg);
        var lm = P(AiProviderType.LMStudio, "chat-ok", key: "");
        var (lok, lmsg) = await lm.TestConnectionAsync();
        check("guard", "LM Studio needs no key and sends no Authorization", lok && !Last("lmstudio", "/models").Headers.ContainsKey("Authorization"), lmsg);
        check("guard", "a model missing from the list is said once, without \"Connected … Connected\"", lmsg.IndexOf("Connected") == lmsg.LastIndexOf("Connected"), lmsg);
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); closed = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var down = P(AiProviderType.LMStudio, "chat-ok", baseUrl: $"http://127.0.0.1:{closed}/v1", key: "");
        var (dok, dmsg) = await down.TestConnectionAsync();
        check("guard", "LM Studio not running → says to start its server", !dok && dmsg.Contains("start its server"), dmsg);

        // every service has its guide in both languages, and every new string has a Thai value
        var loc = typeof(LocalizationService).GetField("Translations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
        var missing = new List<string>();
        foreach (var preset in CompatiblePresets.All)
            foreach (var k in new[] { preset.GuideKey + ".steps", preset.GuideKey + ".free" })
                if (loc == null || !loc.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v.GetValueOrDefault("en")) || string.IsNullOrWhiteSpace(v.GetValueOrDefault("th")))
                    missing.Add(k);
        foreach (var k in new[] { "settings.agg.guide", "settings.agg.open", "settings.agg.free", "settings.agg.baseUrlExample", "settings.agg.noKeyNeeded",
                     "agg.err.baseUrl", "agg.err.noModel", "agg.err.localDown", "agg.err.ownConsole" })
            if (loc == null || !loc.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v.GetValueOrDefault("th"))) missing.Add(k);
        check("guide", "every service: steps + free-use note in English and Thai", loc != null && missing.Count == 0, string.Join(", ", missing));
        check("guide", "every cloud service names its key page (https)",
            CompatiblePresets.All.Where(p => p.KeyRequired).All(p => p.KeysUrl.StartsWith("https://")), "");
        check("guide", "every preset base URL is https, or http only to this PC",
            CompatiblePresets.All.All(p => p.BaseUrl.Length == 0 || p.BaseUrl.StartsWith("https://") || p.BaseUrl.StartsWith("http://localhost")), "");
        var logos = CompatiblePresets.All.Where(p => p.Logo != null).Select(p => p.Logo).ToList();
        // The repo's logo folder, found upward from the harness (it may run from a copy of its bin folder).
        string logoDir = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && logoDir == null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "CluadeX", "Assets", "Logos"))) logoDir = Path.Combine(dir.FullName, "CluadeX", "Assets", "Logos");
        logoDir ??= Path.Combine(Environment.GetEnvironmentVariable("CLUADEX_REPO") ?? ".", "CluadeX", "Assets", "Logos");
        var missingLogos = logos.Where(l => !File.Exists(Path.Combine(logoDir, l))).ToList();
        check("guide", "every logo file a preset names is in Assets\\Logos", missingLogos.Count == 0, string.Join(", ", missingLogos));
    }

    // ── conversation pieces ──

    static List<NativeMessage> Ask() => [new() { Role = "user", Content = [new ContentBlock { Type = "text", Text = "echo ping" }] }];

    static List<ToolSchema> Echo() =>
    [
        new ToolSchema
        {
            Name = "echo", Description = "Echo text",
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone(),
        },
    ];

    // ── the mock server ──

    static string Sse(params string[] chunks) => string.Concat(chunks.Select(c => c.StartsWith(":") ? c + "\n\n" : "data: " + c + "\n\n")) + "data: [DONE]\n\n";

    static string ChatSse(string svc)
    {
        string rf = svc is "openrouter" or "groq" ? "reasoning" : "reasoning_content";
        string usage = """{"prompt_tokens":10,"completion_tokens":2,"prompt_tokens_details":{"cached_tokens":4}}""";
        return Sse(
            $$$"""{"model":"served-{{{svc}}}","choices":[{"index":0,"delta":{"role":"assistant","{{{rf}}}":"thi"}}]}""",
            $$$"""{"choices":[{"index":0,"delta":{"{{{rf}}}":"nk"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"po"}}]}""",
            ": OPENROUTER PROCESSING",
            """{"choices":[{"index":0,"delta":{"content":"ng"},"finish_reason":"stop"}]}""",
            svc == "groq" ? """{"choices":[],"x_groq":{"id":"req_1","usage":""" + usage + "}}" : """{"choices":[],"usage":""" + usage + "}");
    }

    static string ToolStep1(string svc)
    {
        string rf = svc is "openrouter" or "groq" ? "reasoning" : "reasoning_content";
        string id = svc == "mistral" ? "Ab3dE6gH9" : "call_abc123";
        var head = new List<string>
        {
            $$$"""{"choices":[{"index":0,"delta":{"role":"assistant","{{{rf}}}":"plan"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"Let me echo"}}]}""",
        };
        if (svc == "xai")   // xAI: the whole call in one chunk, no index
            head.Add($$$"""{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"{{{id}}}","type":"function","function":{"name":"echo","arguments":"{\"text\":\"ping\"}"}}]}}]}""");
        else
        {
            head.Add($$$"""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"{{{id}}}","type":"function","function":{"name":"echo","arguments":""}}]}}]}""");
            head.Add("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"text\":"}}]}}]}""");
            head.Add("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"ping\"}"}}]}}]}""");
        }
        head.Add("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":20,"completion_tokens":7}}""");
        return Sse(head.ToArray());
    }

    const string ToolStep2Sse =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    const string NonStreamToolCall =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_ns","type":"function","function":{"name":"echo","arguments":"{\"text\":\"ping\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""";

    static string Models(string svc) => svc switch
    {
        "openrouter" => """{"data":[{"id":"vendor/no-tools","created":4,"context_length":32768,"pricing":{"prompt":"0","completion":"0"},"supported_parameters":["temperature"],"architecture":{"output_modalities":["text"]}},{"id":"vendor/coder","created":5,"context_length":262144,"pricing":{"prompt":"0.000002","completion":"0.000008"},"supported_parameters":["tools","temperature"],"architecture":{"output_modalities":["text"]}},{"id":"vendor/painter","created":3,"architecture":{"output_modalities":["image"]}}]}""",
        "together" => """[{"id":"Org/Chat-1","type":"chat","context_length":131072,"pricing":{"input":0.6,"output":2.4}},{"id":"Org/Embed","type":"embedding"}]""",
        "huggingface" => """{"object":"list","data":[{"id":"org/hf-chat","providers":[{"provider":"groq","status":"live","context_length":65536,"pricing":{"input":0.15,"output":0.6},"supports_tools":true}]}]}""",
        "mistral" => """{"data":[{"id":"mistral-large-4","capabilities":{"completion_chat":true,"function_calling":true},"max_context_length":1000000},{"id":"mistral-embed","capabilities":{"completion_chat":false}}]}""",
        "groq" => """{"data":[{"id":"openai/gpt-oss-120b","active":true,"context_window":131072},{"id":"old-model","active":false},{"id":"whisper-large-v3","active":true}]}""",
        _ => """{"object":"list","data":[{"id":"model-a","created":1},{"id":"model-b","created":3},{"id":"text-embedding-3","created":5},{"id":"whisper-large-v3","created":4}]}""",
    };

    static (int, string, string) Route(string method, string path, string body)
    {
        var seg = path.Trim('/').Split('/');
        string svc = seg[0];
        const string json = "application/json", sse = "text/event-stream";
        if (method == "GET" && path.EndsWith("/models"))
            return svc == "azureopenai" ? (404, json, """{"error":{"message":"Resource not found"}}""") : (200, json, Models(svc));
        if (!path.EndsWith("/chat/completions")) return (404, json, """{"error":{"message":"no route"}}""");

        using var doc = JsonDocument.Parse(body);
        var req = doc.RootElement;
        string model = req.GetProperty("model").GetString();
        bool tools = req.TryGetProperty("tools", out _);
        bool stream = req.TryGetProperty("stream", out var st) && st.GetBoolean();
        bool toolReply = body.Contains("\"role\":\"tool\"");

        if (model == "err-401") return (401, json, $$$"""{"error":{"message":"Invalid API key: {{{Key}}}","type":"invalid_request_error"}}""");
        if (model == "reject-tools" && tools) return (400, json, """{"error":{"message":"This model does not support tools","type":"invalid_request_error"}}""");
        if (model == "or-404-tools" && tools) return (404, json, """{"error":{"message":"No endpoints found that support tool use.","code":404}}""");
        if (model == "no-stream-options" && req.TryGetProperty("stream_options", out _))
            return (400, json, """{"error":{"message":"Unrecognized request argument supplied: stream_options"}}""");
        if (model == "max-completion" && req.TryGetProperty("max_tokens", out _))
            return (400, json, """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.","param":"max_tokens"}}""");
        if (model == "no-tool-stream" && tools)
            return stream ? (400, json, """{"error":{"message":"tools cannot be used with stream mode"}}""") : (200, json, NonStreamToolCall);
        if (model == "overflow")
            return (400, json, """{"error":{"message":"This model's maximum context length is 8192 tokens. Please reduce the length of the messages."}}""");
        if (tools) return (200, sse, toolReply ? ToolStep2Sse : ToolStep1(svc));
        return (200, sse, ChatSse(svc));
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
            var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            lock (Hits) Hits.Add(new Hit(ctx.Request.HttpMethod, path, body, headers));
            int status; string type, payload;
            try { (status, type, payload) = Route(ctx.Request.HttpMethod, path, body); }
            catch (Exception ex) { (status, type, payload) = (500, "application/json", JsonSerializer.Serialize(new { error = new { message = ex.Message } })); }
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    // ── helpers ──

    static Hit Last(string svc, string pathEnd)
    {
        lock (Hits) return Hits.LastOrDefault(x => x.Path.StartsWith("/" + svc + "/") && x.Path.EndsWith(pathEnd))
                           ?? new Hit("", "", "{}", new Dictionary<string, string>());
    }

    static int Count(string svc) { lock (Hits) return Hits.Count(x => x.Path.StartsWith("/" + svc + "/")); }

    static JsonElement Json(string body) => JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body).RootElement.Clone();
    static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
    static string Str(JsonElement e, string name) => Has(e, name) && e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString() : null;
    static int Int(JsonElement e, string name) => Has(e, name) && e.GetProperty(name).TryGetInt32(out var i) ? i : -1;
    static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "(none)" : e.GetRawText();
}
