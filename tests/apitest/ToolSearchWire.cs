// Tool search (found by the Godot snooker run, 2026-10-10): every request carried every MCP tool definition — the brain
// alone is 128 tools, ~59k tokens, mostly Unity/Roblox proxies a Godot task never touches. MCP tools now go out with
// defer_loading behind Anthropic's server-side tool search; checked here against the REAL provider and a localhost mock:
//   - which models / endpoints defer, and what the tools array and system prompt look like;
//   - the server_tool_use + tool_search_tool_result blocks come back out of the stream and go back verbatim next step;
//   - the tools array is byte-identical between steps (the cached prefix holds);
//   - search off, or an API that refuses it, sends every tool up front — and drops the server blocks it can't read.
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class ToolSearchWire
{
    static ToolSchema Tool(string name, bool defer = false) => new()
    {
        Name = name,
        Description = "Tool " + name,
        InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}").RootElement.Clone(),
        DeferLoading = defer,
    };

    /// <summary>3 built-ins, 1 pinned brain tool, 10 deferrable MCP tools on two servers.</summary>
    static List<ToolSchema> Catalogue()
    {
        var t = new List<ToolSchema> { Tool("read_file"), Tool("edit_file"), Tool("run_command"), Tool("mcp__brainx-brain__brain_search") };
        for (int i = 0; i < 6; i++) t.Add(Tool($"mcp__brainx-brain__unity__tool{i}", defer: true));
        for (int i = 0; i < 4; i++) t.Add(Tool($"mcp__cluadex-chrome__browse{i}", defer: true));
        return t;
    }

    const string SearchResult = "{\"type\":\"tool_search_tool_result\",\"tool_use_id\":\"srvtoolu_01\",\"content\":{\"type\":\"tool_search_tool_search_result\",\"tool_references\":[{\"type\":\"tool_reference\",\"tool_name\":\"mcp__cluadex-chrome__browse2\"}]}}";

    static string Sse(params string[] events) => string.Concat(events.Select(e => $"event: x\ndata: {e}\n\n"));

    /// <summary>Text, a tool search, its result, then a call to the tool it found.</summary>
    static readonly string SearchSse = Sse(
        "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":3000,\"cache_creation_input_tokens\":500}}}",
        "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Looking for a browser tool.\"}}",
        "{\"type\":\"content_block_stop\",\"index\":0}",
        "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_01\",\"name\":\"tool_search_tool_bm25\",\"input\":{}}}",
        "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"query\\\":\\\"open a \"}}",
        "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"browser tab\\\"}\"}}",
        "{\"type\":\"content_block_stop\",\"index\":1}",
        "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":" + SearchResult + "}",
        "{\"type\":\"content_block_stop\",\"index\":2}",
        "{\"type\":\"content_block_start\",\"index\":3,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_01\",\"name\":\"mcp__cluadex-chrome__browse2\",\"input\":{}}}",
        "{\"type\":\"content_block_delta\",\"index\":3,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"q\\\":\\\"chatgpt.com\\\"}\"}}",
        "{\"type\":\"content_block_stop\",\"index\":3}",
        "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":40}}",
        "{\"type\":\"message_stop\"}");

    static readonly string DoneSse = Sse(
        "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}",
        "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}",
        "{\"type\":\"content_block_stop\",\"index\":0}",
        "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}",
        "{\"type\":\"message_stop\"}");

    static List<NativeMessage> Ask() =>
        [new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "text", Text = "open chatgpt in my chrome" } } }];

    static string SystemText(JsonElement root) =>
        root.TryGetProperty("system", out var s)
            ? s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : string.Concat(s.EnumerateArray().Select(b => b.GetProperty("text").GetString()))
            : "";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · tool search (MCP tools loaded on demand)");

        // A failed tool with no text: the Messages API refuses an empty error tool_result — and with it the whole
        // request, so every later turn of a chat whose history carries one failed too (2026-10-11, git_commit).
        check("wire", "an empty FAILED tool_result gets a line of text; an empty OK one stays empty (cache prefixes); text is kept",
            AnthropicProvider.ToolResultText(new ContentBlock { Type = "tool_result", IsError = true, Content = "" }).Length > 0
            && AnthropicProvider.ToolResultText(new ContentBlock { Type = "tool_result", IsError = true, Content = null }).Length > 0
            && AnthropicProvider.ToolResultText(new ContentBlock { Type = "tool_result", IsError = false, Content = "" }) == ""
            && AnthropicProvider.ToolResultText(new ContentBlock { Type = "tool_result", IsError = true, Content = "boom" }) == "boom", "");

        // 1. Rules
        var cat = Catalogue();
        check("toolsearch", "models: Sonnet 5.5 / Opus 5.5 / Haiku 4.5 / Opus 4.5 / Fable search; Opus 4.1 and Sonnet 4 do not",
            AnthropicProvider.SupportsToolSearch("claude-sonnet-5-5") && AnthropicProvider.SupportsToolSearch("claude-opus-5-5")
            && AnthropicProvider.SupportsToolSearch("claude-haiku-4-5") && AnthropicProvider.SupportsToolSearch("claude-opus-4-5-20251101")
            && AnthropicProvider.SupportsToolSearch("claude-fable-5-1")
            && !AnthropicProvider.SupportsToolSearch("claude-opus-4-1") && !AnthropicProvider.SupportsToolSearch("claude-sonnet-4-20250514")
            && !AnthropicProvider.SupportsToolSearch("my-custom-model"), "");
        check("toolsearch", "endpoints: api.anthropic.com and loopback defer; a remote proxy does not",
            AnthropicProvider.UseToolSearch("claude-sonnet-5-5", null, true, cat)
            && AnthropicProvider.UseToolSearch("claude-sonnet-5-5", "https://api.anthropic.com", true, cat)
            && AnthropicProvider.UseToolSearch("claude-sonnet-5-5", "http://localhost:4000", true, cat)
            && !AnthropicProvider.UseToolSearch("claude-sonnet-5-5", "https://openrouter.ai/api", true, cat), "");
        check("toolsearch", "switched off, or fewer deferrable tools than the threshold, sends everything up front",
            !AnthropicProvider.UseToolSearch("claude-sonnet-5-5", null, false, cat)
            && !AnthropicProvider.UseToolSearch("claude-sonnet-5-5", null, true, cat.Take(4 + AnthropicProvider.ToolSearchMinDeferred - 1).ToList()), "");

        var defs = AnthropicProvider.BuildToolDefs(cat, search: true, forcedTool: "mcp__cluadex-chrome__browse0");
        int deferred = defs.Count(d => d.ContainsKey("defer_loading"));
        check("toolsearch", "tools: the search tool first, MCP tools deferred, built-ins + the pinned brain tool + a forced tool loaded",
            (string)defs[0]["type"] == AnthropicProvider.ToolSearchType && deferred == 9
            && !defs.Single(d => (string)d["name"] == "mcp__brainx-brain__brain_search").ContainsKey("defer_loading")
            && !defs.Single(d => (string)d["name"] == "mcp__cluadex-chrome__browse0").ContainsKey("defer_loading")
            && !defs.Single(d => (string)d["name"] == "read_file").ContainsKey("defer_loading"), $"deferred={deferred}");
        string note = AnthropicProvider.DeferredToolsNote(cat);
        check("toolsearch", "the prompt names every deferred tool by server, with how to load it",
            note.Contains(AnthropicProvider.ToolSearchName) && note.Contains("mcp__brainx-brain__*: unity__tool0") && note.Contains("mcp__cluadex-chrome__*: browse0")
            && !note.Contains("brain_search"), note.Replace("\n", " | ")[..Math.Min(200, note.Length)]);

        // 2. The real provider against a localhost mock
        var svc = new SettingsService();
        using var mock = new GoldenMock();
        check("toolsearch", "Anthropic mock proves its marker before any request", await mock.ProveAsync(), mock.Root);
        ImageWireGolden.Pin(svc.Settings, "claude-sonnet-5-5", mock.Root);
        svc.Settings.ToolSearchEnabled = true;
        var p = new AnthropicProvider(svc);

        mock.Responder = _ => (200, "text/event-stream", SearchSse);
        var conv = Ask();
        var r1 = await p.ChatWithToolsAsync(conv, "system prompt", cat);
        using var b1 = JsonDocument.Parse(mock.BodyAt(mock.Count - 1) ?? "{}");
        var tools1 = b1.RootElement.GetProperty("tools");
        check("toolsearch", "request: search tool + 10 deferred MCP tools, none of them carrying cache_control",
            tools1[0].GetProperty("type").GetString() == AnthropicProvider.ToolSearchType
            && tools1.EnumerateArray().Count(t => t.TryGetProperty("defer_loading", out _)) == 10
            && !tools1.EnumerateArray().Any(t => t.TryGetProperty("defer_loading", out _) && t.TryGetProperty("cache_control", out _)),
            $"tools={tools1.GetArrayLength()}");
        check("toolsearch", "request: the system prompt carries the deferred-tools note after the agent's prompt",
            SystemText(b1.RootElement).StartsWith("system prompt") && SystemText(b1.RootElement).Contains("Tools loaded on demand"), "");
        // An agent run waits on approvals, builds and the browser: the prefix is cached for an hour, the conversation's
        // breakpoints keep the default 5 minutes (longer TTLs must come first — they do: tools, system, then messages).
        var sysCache = b1.RootElement.GetProperty("system")[0].GetProperty("cache_control");
        bool msgTtl = b1.RootElement.GetProperty("messages").EnumerateArray().SelectMany(m => m.GetProperty("content").EnumerateArray())
            .Any(b => b.TryGetProperty("cache_control", out var cc) && cc.TryGetProperty("ttl", out _));
        check("toolsearch", "request: the system prefix is cached for 1 hour, the conversation breakpoints for the default 5 minutes",
            sysCache.TryGetProperty("ttl", out var ttl) && ttl.GetString() == "1h" && !msgTtl, sysCache.GetRawText());

        check("toolsearch", "response: the discovered tool's call comes through; one search counted; cache usage reported",
            r1.ToolCalls.Count == 1 && r1.ToolCalls[0].Name == "mcp__cluadex-chrome__browse2" && r1.ToolSearches == 1
            && r1.CacheReadTokens == 3000 && r1.CacheWriteTokens == 500,
            $"calls={r1.ToolCalls.Count} searches={r1.ToolSearches} cacheR={r1.CacheReadTokens} cacheW={r1.CacheWriteTokens}");
        string order = string.Join(",", r1.Blocks.Select(b => b.Type == "provider_item" ? JsonDocument.Parse(b.Data!).RootElement.GetProperty("type").GetString() : b.Type));
        check("toolsearch", "response: blocks kept in stream order — text, server_tool_use, tool_search_tool_result, tool_use",
            order == "text,server_tool_use,tool_search_tool_result,tool_use", order);

        // Next step: the assistant turn goes back as-is, plus the tool result.
        conv.Add(r1.ToAssistantMessage());
        conv.Add(new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "tool_result", ToolUseId = "toolu_01", Content = "tab opened" } } });
        mock.Responder = _ => (200, "text/event-stream", DoneSse);
        await p.ChatWithToolsAsync(conv, "system prompt", cat);
        using var b2 = JsonDocument.Parse(mock.BodyAt(mock.Count - 1) ?? "{}");
        var assistant = b2.RootElement.GetProperty("messages")[1].GetProperty("content");
        var srv = assistant.EnumerateArray().FirstOrDefault(b => b.GetProperty("type").GetString() == "server_tool_use");
        var res = assistant.EnumerateArray().FirstOrDefault(b => b.GetProperty("type").GetString() == "tool_search_tool_result");
        check("toolsearch", "next request: server_tool_use goes back with its id and the streamed query",
            srv.ValueKind == JsonValueKind.Object && srv.GetProperty("id").GetString() == "srvtoolu_01"
            && srv.GetProperty("input").GetProperty("query").GetString() == "open a browser tab", srv.ValueKind == JsonValueKind.Object ? srv.GetRawText() : "missing");
        check("toolsearch", "next request: tool_search_tool_result goes back exactly as the API sent it",
            res.ValueKind == JsonValueKind.Object && JsonSerializer.Serialize(res) == JsonSerializer.Serialize(JsonDocument.Parse(SearchResult).RootElement),
            res.ValueKind == JsonValueKind.Object ? res.GetRawText() : "missing");
        check("toolsearch", "next request: no tool_result for the server call (the API rejects one)",
            !b2.RootElement.GetProperty("messages").EnumerateArray().SelectMany(m => m.GetProperty("content").EnumerateArray())
                .Any(b => b.TryGetProperty("tool_use_id", out var id) && id.GetString() == "srvtoolu_01" && b.GetProperty("type").GetString() == "tool_result"), "");
        check("toolsearch", "the tools array is byte-identical between steps (the cached prefix holds)",
            tools1.GetRawText() == b2.RootElement.GetProperty("tools").GetRawText(), "");

        // 3. Switched off: everything up front, and the server blocks it can't read are left out.
        svc.Settings.ToolSearchEnabled = false;
        await p.ChatWithToolsAsync(conv, "system prompt", cat);
        string offBody = mock.BodyAt(mock.Count - 1) ?? "";
        check("toolsearch", "search off: no search tool, no defer_loading, no server blocks, no note",
            !offBody.Contains("tool_search_tool_bm25") && !offBody.Contains("defer_loading") && !offBody.Contains("server_tool_use")
            && !offBody.Contains("Tools loaded on demand"), "");
        svc.Settings.ToolSearchEnabled = true;

        // 4. An API that refuses tool search: retried at once without it, and not offered again.
        var p2 = new AnthropicProvider(svc);
        int calls = 0;
        mock.Responder = body =>
        {
            calls++;
            return body.Contains("defer_loading")
                ? (400, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"tools.0: tool_search_tool_bm25_20251119 is not supported here\"}}")
                : (200, "text/event-stream", DoneSse);
        };
        var rf = await p2.ChatWithToolsAsync(Ask(), "system prompt", cat);
        int before = calls;
        await p2.ChatWithToolsAsync(Ask(), "system prompt", cat);
        check("toolsearch", "refused: the same request succeeds without search, and later requests don't try it again",
            rf.TextContent == "done" && before == 2 && calls == 3 && !(mock.BodyAt(mock.Count - 1) ?? "").Contains("defer_loading"),
            $"text={rf.TextContent} calls={calls}");

        // 5. The chat strip's cost estimate — the arithmetic, by hand (Sonnet 5.5: $2 in, $10 out, $0.10 read, $2.50 write;
        //    the read price was $0.20 here until the 2026-10-13 pricing check — PricingWire).
        section("offline · cost estimate (the chat strip's ≈ $/step)");
        var e = CostEstimate.Estimate("claude-sonnet-5-5", prefixTokens: 25_000, historyTokens: 2_000, outputPerStep: 1_200, growthPerStep: 3_000);
        // step 1: 25k × $4 (1-hour write = 2x input) + 2k × $2.50 + 1.2k × $10 = 0.100 + 0.005 + 0.012
        check("estimate", "step 1 = prefix written for an hour (2x input) + history at the 5-minute price + the answer", e.FirstStep == 0.117m, e.FirstStep.ToString());
        // step k: (27k + k × 4.2k) × $0.10 + 4.2k × $2.50 + 1.2k × $10
        check("estimate", "a later step = everything before read back + the step's new content written + the answer",
            e.LaterLow == (27_000m + 4_200m) * 0.1m / 1_000_000m + 4_200m * 2.5m / 1_000_000m + 0.012m
            && e.LaterHigh == (27_000m + 40 * 4_200m) * 0.1m / 1_000_000m + 4_200m * 2.5m / 1_000_000m + 0.012m, $"{e.LaterLow} … {e.LaterHigh}");
        var (fewest, most) = e.StepsFor(10m);
        check("estimate", "a $10 budget buys between (10 − step 1) / long-run step and (10 − step 1) / early step, plus step 1",
            fewest == 1 + (int)((10m - e.FirstStep) / e.LaterHigh) && most == 1 + (int)((10m - e.FirstStep) / e.LaterLow) && fewest < most, $"{fewest}–{most}");
        var off = CostEstimate.Estimate("claude-sonnet-5-5", 25_000, 2_000, 1_200, 3_000, caching: false);
        check("estimate", "caching off: every step pays full input for the whole prompt (dearer than with the cache)",
            off.LaterLow > e.LaterLow * 2 && off.FirstStep < e.FirstStep, $"{off.LaterLow} vs {e.LaterLow}");
        var oa = CostEstimate.Estimate("gpt-6-sol", 25_000, 2_000, 1_200, 3_000);
        check("estimate", "OpenAI (no cache-write premium): the prefix is billed as plain input on step 1", oa.FirstStep == (27_000m * 2m + 1_200m * 10m) / 1_000_000m, oa.FirstStep.ToString());
        check("estimate", "the answer size follows effort when nothing was measured yet (low < medium < high < max)",
            CostEstimate.DefaultOutputPerStep("low", true) < CostEstimate.DefaultOutputPerStep("medium", true)
            && CostEstimate.DefaultOutputPerStep("medium", true) < CostEstimate.DefaultOutputPerStep("high", false)
            && CostEstimate.DefaultOutputPerStep("high", false) < CostEstimate.DefaultOutputPerStep("max", false), "");
    }
}
