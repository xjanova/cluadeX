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

    // OpenAI Responses API: step 1 = reasoning + a commentary message + one function call; step 2 = answer.
    const string ResponsesStep1 =
        "data: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\"}}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"ENC-REASONING\"}}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":1,\"content_index\":0,\"delta\":\"Let me echo\"}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"phase\":\"commentary\",\"content\":[{\"type\":\"output_text\",\"text\":\"Let me echo\"}]}}\n\n" +
        "data: {\"type\":\"response.output_item.added\",\"output_index\":2,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"\"}}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":2,\"delta\":\"{\\\"text\\\":\"}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":2,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"echo\",\"arguments\":\"{\\\"text\\\":\\\"ping\\\"}\"}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":5},\"output_tokens\":7}}}\n\n";

    const string ResponsesStep2 =
        "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_2\",\"output_index\":0,\"content_index\":0,\"delta\":\"done\"}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_2\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":40,\"output_tokens\":2}}}\n\n";

    const string ResponsesReject =
        "{\"error\":{\"message\":\"Unknown parameter: 'tools[0].strict'.\",\"type\":\"invalid_request_error\",\"param\":\"tools\"}}";

    // Gemini function calling: step 1 = a thought summary, then two parallel calls (signature on the first
    // only); step 2 = the answer plus an empty-text part carrying a signature.
    const string GeminiToolStep1 =
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"plan\",\"thought\":true}]}}]}\n\n" +
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"id\":\"fc-a\",\"name\":\"echo\",\"args\":{\"text\":\"ping\"}},\"thoughtSignature\":\"SIG_A\"},{\"functionCall\":{\"id\":\"fc-b\",\"name\":\"echo\",\"args\":{\"text\":\"pong\"}}}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":30,\"candidatesTokenCount\":9,\"thoughtsTokenCount\":4}}\n\n";

    const string GeminiToolStep2 =
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]}}]}\n\n" +
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"\",\"thoughtSignature\":\"SIG_END\"}]},\"finishReason\":\"STOP\"}]}\n\n";

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

        await ToolRoundTrips(check, section, svc, root);
        listener.Stop();
    }

    static readonly List<ToolSchema> Echo =
    [
        new ToolSchema
        {
            Name = "echo",
            Description = "Echo a word back.",
            InputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}").RootElement.Clone(),
        },
    ];

    static List<NativeMessage> AskTool() =>
        [new NativeMessage { Role = "user", Content = [new ContentBlock { Type = "text", Text = "echo ping" }] }];

    /// <summary>The agent loop's own way of continuing: the assistant turn from the response (minus
    /// tool_use), a tool_use per executed call, then one tool_result per call.</summary>
    static void Continue(List<NativeMessage> msgs, NativeToolResponse r)
    {
        var turn = r.ToAssistantMessage(includeToolUse: false);
        foreach (var c in r.ToolCalls)
            turn.Content.Add(new ContentBlock { Type = "tool_use", Id = c.Id, Name = c.Name, Input = c.Input });
        msgs.Add(turn);
        msgs.Add(new NativeMessage
        {
            Role = "user",
            Content = r.ToolCalls.Select(c => new ContentBlock
                { Type = "tool_result", ToolUseId = c.Id, Content = c.Input.GetProperty("text").GetString() }).ToList(),
        });
    }

    static async Task ToolRoundTrips(Action<string, string, bool, string> check, Action<string> section, SettingsService svc, string root)
    {
        var s = svc.Settings;
        section("offline · native tool calling (localhost mock of the documented wire formats)");

        // ── OpenAI Responses API ──
        s.ReasoningEffort = "high";
        s.ExtendedThinkingEnabled = false;
        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = root + "/v1", SelectedModel = "gpt-6-sol" };
        var oa = new OpenAiProvider(svc);
        check("openai-tools", "native tool use is on", oa.SupportsNativeToolUse, "");
        var msgs = AskTool();
        var streamed = new StringBuilder();
        var r1 = await oa.ChatWithToolsAsync(msgs, "sys", Echo, t => streamed.Append(t));
        var b1 = LastBody("/v1/responses");
        var tool0 = b1.GetProperty("tools")[0];
        check("openai-tools", "request: function tool, store:false, instructions, effort, no temperature",
            Str(tool0, "type") == "function" && Has(tool0, "parameters") && Str(b1, "store") == "False"
            && Str(b1, "instructions") == "sys" && Str(b1, "tool_choice") == "auto"
            && Str(b1.GetProperty("reasoning"), "effort") == "high" && !Has(b1, "temperature"), Raw(b1));
        check("openai-tools", "call parsed from output_item.done", r1.ToolCalls.Count == 1 && r1.ToolCalls[0].Id == "call_1"
            && r1.ToolCalls[0].Name == "echo" && r1.ToolCalls[0].Input.GetProperty("text").GetString() == "ping"
            && r1.StopReason == "tool_use", string.Join(",", r1.ToolCalls.Select(c => $"{c.Name}#{c.Id}")));
        check("openai-tools", "commentary text streamed live and kept", streamed.ToString() == "Let me echo" && r1.TextContent == "Let me echo",
            $"streamed='{streamed}' text='{r1.TextContent}'");
        check("openai-tools", "usage: cached input split out", r1.InputTokens == 15 && r1.OutputTokens == 7, $"in={r1.InputTokens} out={r1.OutputTokens}");

        Continue(msgs, r1);
        var r2 = await oa.ChatWithToolsAsync(msgs, "sys", Echo);
        var input = LastBody("/v1/responses").GetProperty("input");
        string Kinds(JsonElement arr) => string.Join(",", arr.EnumerateArray().Select(i => Str(i, "type") ?? ("msg:" + Str(i, "role"))));
        check("openai-tools", "turn 2 input: user, then output items verbatim, then the call's output",
            Kinds(input) == "msg:user,reasoning,message,function_call,function_call_output", Kinds(input));
        check("openai-tools", "reasoning echoed with its encrypted state", Str(input[1], "id") == "rs_1" && Str(input[1], "encrypted_content") == "ENC-REASONING", Raw(input[1]));
        check("openai-tools", "assistant message keeps its phase", Str(input[2], "phase") == "commentary", Raw(input[2]));
        check("openai-tools", "function_call keeps id + call_id; output answers call_id",
            Str(input[3], "id") == "fc_1" && Str(input[3], "call_id") == "call_1"
            && Str(input[4], "call_id") == "call_1" && Str(input[4], "output") == "ping", Raw(input[3]) + " " + Raw(input[4]));
        check("openai-tools", "final answer", r2.TextContent == "done" && r2.StopReason == "end_turn", $"{r2.TextContent}/{r2.StopReason}");

        await oa.ChatWithToolsAsync(AskTool(), "sys", Echo, toolChoice: "required");
        check("openai-tools", "tool_choice required", Str(LastBody("/v1/responses"), "tool_choice") == "required", "");
        await oa.ChatWithToolsAsync(AskTool(), "sys", Echo, toolChoice: "echo");
        var named = LastBody("/v1/responses").GetProperty("tool_choice");
        check("openai-tools", "tool_choice naming one function", Str(named, "type") == "function" && Str(named, "name") == "echo", Raw(named));

        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = root + "/v1", SelectedModel = "reject-tools" };
        var oaBad = new OpenAiProvider(svc);
        var rb = await oaBad.ChatWithToolsAsync(AskTool(), "sys", Echo);
        check("openai-tools", "API rejects the tool format → switches to text protocol, says so",
            rb.StopReason == "error" && rb.TextContent!.Contains("text tool protocol") && !oaBad.SupportsNativeToolUse, rb.TextContent ?? "");

        // ── Gemini function calling ──
        s.ReasoningEffort = "auto";
        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "gemini-3.8-flash" };
        var gm = new GeminiProvider(svc);
        check("gemini-tools", "native tool use is on", gm.SupportsNativeToolUse, "");
        var gmsgs = AskTool();
        var g1 = await gm.ChatWithToolsAsync(gmsgs, "sys", Echo);
        var gb1 = LastBody(":streamGenerateContent");
        var decl = gb1.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        check("gemini-tools", "request: parametersJsonSchema (not parameters), mode AUTO, systemInstruction",
            Has(decl, "parametersJsonSchema") && !Has(decl, "parameters")
            && Str(gb1.GetProperty("toolConfig").GetProperty("functionCallingConfig"), "mode") == "AUTO"
            && Has(gb1, "systemInstruction"), Raw(decl));
        check("gemini-tools", "two parallel calls with the model's ids; thought kept apart",
            g1.ToolCalls.Count == 2 && g1.ToolCalls[0].Id == "fc-a" && g1.ToolCalls[1].Id == "fc-b"
            && g1.ThinkingContent == "plan" && g1.StopReason == "tool_use", string.Join(",", g1.ToolCalls.Select(c => c.Id)));
        check("gemini-tools", "usage: thinking counted as output", g1.InputTokens == 30 && g1.OutputTokens == 13, $"in={g1.InputTokens} out={g1.OutputTokens}");

        Continue(gmsgs, g1);
        var g2 = await gm.ChatWithToolsAsync(gmsgs, "sys", Echo);
        var contents = LastBody(":streamGenerateContent").GetProperty("contents");
        check("gemini-tools", "turn 2: user / model / user", string.Join(",", contents.EnumerateArray().Select(c => Str(c, "role"))) == "user,model,user",
            Raw(contents));
        var modelParts = contents[1].GetProperty("parts");
        check("gemini-tools", "model parts echoed verbatim: signature on the first call only, thought summary dropped",
            modelParts.GetArrayLength() == 2 && Str(modelParts[0], "thoughtSignature") == "SIG_A" && !Has(modelParts[1], "thoughtSignature")
            && !Has(modelParts[0], "thought"), Raw(modelParts));
        var frs = contents[2].GetProperty("parts");
        check("gemini-tools", "both functionResponses after both calls, matching id + name",
            frs.GetArrayLength() == 2
            && Str(frs[0].GetProperty("functionResponse"), "id") == "fc-a" && Str(frs[0].GetProperty("functionResponse"), "name") == "echo"
            && Str(frs[1].GetProperty("functionResponse"), "id") == "fc-b"
            && frs[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetString() == "ping", Raw(frs));
        check("gemini-tools", "final answer; trailing empty part with its signature kept for the echo",
            g2.TextContent == "done" && g2.StopReason == "end_turn" && g2.Blocks.Any(b => b.Type == "provider_item" && b.Data!.Contains("SIG_END")),
            $"{g2.TextContent}/{g2.StopReason}");

        // A call the agent loop injected itself carries the documented bypass signature.
        var synthetic = new List<NativeMessage>
        {
            AskTool()[0],
            new() { Role = "assistant", Content = [new ContentBlock { Type = "tool_use", Id = "fc-x", Name = "echo", Input = JsonDocument.Parse("{\"text\":\"x\"}").RootElement.Clone() }] },
            new() { Role = "user", Content = [new ContentBlock { Type = "tool_result", ToolUseId = "fc-x", Content = "x" }] },
        };
        var sc = GeminiProvider.BuildToolContents(synthetic);
        var scText = JsonSerializer.Serialize(sc);
        check("gemini-tools", "injected call gets skip_thought_signature_validator", scText.Contains("skip_thought_signature_validator"), scText);

        await gm.ChatWithToolsAsync(AskTool(), "sys", Echo, toolChoice: "echo");
        var fcc = LastBody(":streamGenerateContent").GetProperty("toolConfig").GetProperty("functionCallingConfig");
        check("gemini-tools", "named tool → mode ANY + allowedFunctionNames", Str(fcc, "mode") == "ANY" && Raw(fcc).Contains("echo"), Raw(fcc));

        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = root, SelectedModel = "reject-tools" };
        var gBad = new GeminiProvider(svc);
        var gr = await gBad.ChatWithToolsAsync(AskTool(), "sys", Echo);
        check("gemini-tools", "API rejects the tool format → switches to text protocol, says so",
            gr.StopReason == "error" && gr.TextContent!.Contains("text tool protocol") && !gBad.SupportsNativeToolUse, gr.TextContent ?? "");
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
                "/v1/responses" when body.Contains("reject-tools") => (400, "application/json", ResponsesReject),
                "/v1/responses" => (200, "text/event-stream", body.Contains("function_call_output") ? ResponsesStep2 : ResponsesStep1),
                _ when path.EndsWith(":streamGenerateContent") && path.Contains("reject-tools")
                    => (400, "application/json", "{\"error\":{\"code\":400,\"message\":\"Invalid JSON payload received. Unknown name \\\"parametersJsonSchema\\\"\",\"status\":\"INVALID_ARGUMENT\"}}"),
                _ when path.EndsWith(":streamGenerateContent") && body.Contains("functionDeclarations")
                    => (200, "text/event-stream", body.Contains("functionResponse") ? GeminiToolStep2 : GeminiToolStep1),
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
