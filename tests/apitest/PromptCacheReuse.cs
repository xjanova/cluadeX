// Prompt-cache reuse in the agent loop (found by the real Snooker2D run, 2026-10-10: 47 requests, 2.2M uncached input
// tokens, $11.87). Two causes, both checked here against the REAL code:
//   1. only the system prompt carried cache_control, so every step re-sent the whole conversation uncached;
//   2. microcompact moved its cut on every request past 90k tokens, so an old message changed each step and no
//      conversation prefix could ever be read back.
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class PromptCacheReuse
{
    const string ToolSse =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    static ContentBlock Text(string t) => new() { Type = "text", Text = t };
    static ContentBlock Use(string id) => new() { Type = "tool_use", Id = id, Name = "read_file", Input = JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement };
    static ContentBlock Result(string id, string body) => new() { Type = "tool_result", ToolUseId = id, Content = body };
    static ContentBlock Think() => new() { Type = "thinking", Text = "plan", Signature = "sig" };

    /// <summary>A tool conversation of <paramref name="steps"/> steps: user task, then assistant tool_use / user tool_result pairs.</summary>
    static List<NativeMessage> Conversation(int steps, int resultChars)
    {
        var m = new List<NativeMessage> { new() { Role = "user", Content = { Text("fix the aim line") } } };
        for (int i = 0; i < steps; i++)
        {
            m.Add(new NativeMessage { Role = "assistant", Content = { Think(), Use("t" + i) } });
            m.Add(new NativeMessage { Role = "user", Content = { Result("t" + i, $"step {i} " + new string((char)('a' + i % 26), resultChars)) } });
        }
        return m;
    }

    static string Wire(IEnumerable<NativeMessage> ms) => JsonSerializer.Serialize(ms.Select(m => new
    {
        m.Role, Content = m.Content.Select(b => new { b.Type, b.Text, b.Content, b.ToolUseId, b.Id }).ToList(),
    }));

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · prompt-cache reuse in the agent loop");

        // 1. Where the breakpoints go (the provider's own marker)
        var conv = Conversation(3, 50);
        conv[^1].Content.Add(Text("[note]"));
        var wire = conv.Select(m => new Dictionary<string, object>
        {
            ["role"] = m.Role,
            ["content"] = m.Content.Select(b => (object)new Dictionary<string, object> { ["type"] = b.Type, ["text"] = b.Text ?? "" }).ToList(),
        }).ToList();
        AnthropicProvider.MarkConversationCache(wire);
        bool Marked(int msg, int block) => ((Dictionary<string, object>)((List<object>)wire[msg]["content"])[block]).ContainsKey("cache_control");
        int marks = wire.Sum(m => ((List<object>)m["content"]).Count(b => ((Dictionary<string, object>)b).ContainsKey("cache_control")));
        check("cache", "the last block of the last message is a cache breakpoint", Marked(wire.Count - 1, 1), $"marks={marks}");
        check("cache", "the previous user turn's last block is a breakpoint too (the prefix the last request wrote)", Marked(wire.Count - 3, 0), "");
        bool thinkingMarked = wire.Where(m => (string)m["role"] == "assistant").Any(m => Marked(wire.IndexOf(m), 0));
        check("cache", "thinking blocks never carry cache_control; exactly 2 conversation breakpoints", !thinkingMarked && marks == 2, $"marks={marks}");

        // 2. The request the provider really sends (mock proves its marker first)
        var svc = new SettingsService();
        using (var mock = new GoldenMock())
        {
            check("cache", "Anthropic mock proves its marker before any request", await mock.ProveAsync(), mock.Root);
            mock.Responder = _ => (200, "text/event-stream", ToolSse);
            ImageWireGolden.Pin(svc.Settings, "claude-opus-5-5", mock.Root);
            svc.Settings.PromptCachingEnabled = true;
            var p = new AnthropicProvider(svc);
            await p.ChatWithToolsAsync(Conversation(4, 200), "system prompt", ImageWireGolden.Tools());
            string body = mock.BodyAt(mock.Count - 1) ?? "";
            using var doc = JsonDocument.Parse(body);
            int inSystem = doc.RootElement.GetProperty("system").EnumerateArray().Count(b => b.TryGetProperty("cache_control", out _));
            int inMessages = doc.RootElement.GetProperty("messages").EnumerateArray()
                .Sum(m => m.GetProperty("content").EnumerateArray().Count(b => b.TryGetProperty("cache_control", out _)));
            check("cache", "sent request: system 1 + conversation 2 breakpoints (≤ 4, the API limit)", inSystem == 1 && inMessages == 2, $"system={inSystem} messages={inMessages}");

            svc.Settings.PromptCachingEnabled = false;
            await p.ChatWithToolsAsync(Conversation(4, 200), "system prompt", ImageWireGolden.Tools());
            check("cache", "control: caching off sends no cache_control at all", !(mock.BodyAt(mock.Count - 1) ?? "").Contains("cache_control"), "");
            svc.Settings.PromptCachingEnabled = true;
        }

        // 3. Microcompact keeps the prefix stable: 12 agent steps over the 90k-token threshold
        var msgs = Conversation(14, 30_000);          // 29 messages, ~105k tokens: over the threshold from the start
        int newMisses = 0, oldMisses = 0, advances = 0;
        string? prevNew = null, prevOld = null;
        NativeMessage? mark = null;
        for (int step = 0; step < 12; step++)
        {
            int boundary = CodeAgentService.NextCompactBoundary(msgs, mark, true, false, 3, 800, out bool adv);
            if (adv) advances++;
            mark = boundary > 0 && boundary < msgs.Count ? msgs[boundary] : null;
            var outNew = boundary > 0 ? CodeAgentService.MicrocompactUpTo(msgs, boundary, 800) : msgs;
            var outOld = CodeAgentService.MicrocompactNativeMessages(msgs, 3, 800);   // the old per-request cut
            // The cache can be read back only if everything this request shares with the previous one is byte-identical.
            string nNew = Wire(outNew), nOld = Wire(outOld);
            if (prevNew != null && !nNew.StartsWith(prevNew[..^1])) newMisses++;
            if (prevOld != null && !nOld.StartsWith(prevOld[..^1])) oldMisses++;
            prevNew = nNew; prevOld = nOld;
            msgs.Add(new NativeMessage { Role = "assistant", Content = { Think(), Use("s" + step) } });
            msgs.Add(new NativeMessage { Role = "user", Content = { Result("s" + step, $"new {step} " + new string('z', 6_000)) } });
        }
        check("cache", "control: the old per-request cut changed the prefix on (nearly) every step", oldMisses >= 10, $"old misses={oldMisses}/11");
        check("cache", "stepped cut: the prefix changes only when the cut moves (cache reads back on the other steps)", newMisses <= advances && newMisses <= 4, $"misses={newMisses}/11 advances={advances}");
        var shortened = CodeAgentService.MicrocompactUpTo(msgs, CodeAgentService.CompactBoundaryOf(msgs, mark), 800);
        check("cache", "the stepped cut still keeps the conversation under control (shortened ≤ 90k-token threshold + one step)",
            CodeAgentService.EstimateNativeTokens(shortened) < CodeAgentService.ApiCompactThresholdTokens + 20_000,
            $"tokens≈{CodeAgentService.EstimateNativeTokens(shortened)}");

        // 4. Local models still compact every step (their window, not the cache, is the limit)
        var local = Conversation(5, 1_000); NativeMessage? lm = null; int moves = 0;
        for (int s = 0; s < 3; s++)
        {
            int b = CodeAgentService.NextCompactBoundary(local, lm, true, true, 3, 800, out bool a);
            if (a) moves++;
            lm = b > 0 && b < local.Count ? local[b] : null;
            local.Add(new NativeMessage { Role = "assistant", Content = { Use("l" + s) } });
            local.Add(new NativeMessage { Role = "user", Content = { Result("l" + s, "x") } });
        }
        check("cache", "local models: the cut follows the conversation every step, as before", moves == 3, $"moves={moves}");
    }
}
