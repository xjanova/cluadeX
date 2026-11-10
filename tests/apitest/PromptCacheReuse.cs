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

        await CompactMarks(check);
        StablePrompt(check);
    }

    // ── 2026-11-07 token audit: the system prompt no longer moves with the working tree or the project list ──
    static void StablePrompt(Action<string, string, bool, string> check)
    {
        string note = CodeAgentService.FormatGitStatusNote("main", " M src/a.cs\r\n?? b.txt\n");
        check("prompt-stable", "[git status] note: branch + one line per file, CR stripped",
            note == "[git status — branch main, when this message was sent]\n M src/a.cs\n?? b.txt", note.Replace("\n", "⏎"));
        check("prompt-stable", "clean tree says so",
            CodeAgentService.FormatGitStatusNote("dev", "") == "[git status — branch dev, when this message was sent]\nclean (no changes)", "");
        string many = CodeAgentService.FormatGitStatusNote("x", string.Join("\n", Enumerable.Range(0, 25).Select(i => $" M f{i}.cs")));
        check("prompt-stable", "≤ 20 file lines, then the count of the rest",
            many.Split('\n').Length == 22 && many.EndsWith("... (5 more files)") && !many.Contains("f20.cs"), many[^40..]);
        check("prompt-stable", "the note goes after the turn's words; no note = the words unchanged",
            CodeAgentService.WithGitStatus("fix it", "[git status]") == "fix it\n\n[git status]"
            && CodeAgentService.WithGitStatus("fix it", null) == "fix it" && CodeAgentService.WithGitStatus(null, null) == "", "");

        var a = new ProjectRegistryService.Entry { Name = "beta", Path = @"D:\b", LastOpened = DateTime.Now };
        var b = new ProjectRegistryService.Entry { Name = "Alpha", Path = @"D:\a", LastOpened = DateTime.Now.AddDays(-3) };
        var c = new ProjectRegistryService.Entry { Name = "alpha", Path = @"C:\a", LastOpened = DateTime.Now.AddDays(-1) };
        var order1 = ProjectRegistryService.ByName(new[] { a, b, c }).Select(e => e.Path);
        var order2 = ProjectRegistryService.ByName(new[] { c, a, b }).Select(e => e.Path);
        check("prompt-stable", "Related projects: by name (then path), the same order whichever was opened last",
            order1.SequenceEqual(new[] { @"C:\a", @"D:\a", @"D:\b" }) && order1.SequenceEqual(order2), string.Join(",", order1));
    }

    // ── 2026-11-07 token audit: a breakpoint at microcompact's cut, images cut only with the compaction ──
    // In 19 days of logs every move of the cut wrote 105–167k tokens again (23 times, ≈ $17.65), and the batched image
    // cut rewrote the conversation after the oldest image 17 times (≈ $11.49). Checked against a model of the API's
    // cache: a request reads back the longest prefix that ends at a breakpoint an earlier request wrote, when that point
    // lies at or up to 20 blocks before one of this request's breakpoints.
    static List<Dictionary<string, object>> WireOf(List<NativeMessage> ms) => ms.Where(m => m.Role != "system").Select(m => new Dictionary<string, object>
    {
        ["role"] = m.Role,
        ["content"] = m.Content.Select(b => (object)new Dictionary<string, object>
        {
            ["type"] = b.Type, ["text"] = b.Text ?? b.Content ?? "",
            // An image weighs ~1.6k tokens on the wire: its path plus 6,000 characters stands in for it.
            ["images"] = string.Join(",", b.Images?.Select(i => (i.FilePath ?? "") + new string('i', 6_000)) ?? Array.Empty<string>()),
        }).ToList(),
    }).ToList();

    static bool HasMark(Dictionary<string, object> m) =>
        ((List<object>)m["content"]).Any(b => ((Dictionary<string, object>)b).ContainsKey("cache_control"));

    static async Task CompactMarks(Action<string, string, bool, string> check)
    {
        // 5a. The marker with the loop's marks
        var conv = Conversation(6, 50);
        var wire = WireOf(conv);
        AnthropicProvider.MarkConversationCache(wire, new[] { 4 });
        var at = Enumerable.Range(0, wire.Count).Where(i => HasMark(wire[i])).ToList();
        check("cache-mark", "one loop mark: the last message, the mark and the previous user turn (3 + system = the API's 4)",
            at.SequenceEqual(new[] { 4, wire.Count - 3, wire.Count - 1 }), string.Join(",", at));
        wire = WireOf(conv);
        AnthropicProvider.MarkConversationCache(wire, new[] { 6, 2, 4 });
        at = Enumerable.Range(0, wire.Count).Where(i => HasMark(wire[i])).ToList();
        check("cache-mark", "the cut moved (old cut + new cut): those two + the last message, never more than 3",
            at.SequenceEqual(new[] { 2, 4, wire.Count - 1 }), string.Join(",", at));
        wire = WireOf(conv);
        AnthropicProvider.MarkConversationCache(wire, new[] { wire.Count - 1, -3, 99 });
        at = Enumerable.Range(0, wire.Count).Where(i => HasMark(wire[i])).ToList();
        check("cache-mark", "control: marks on the last message or out of range change nothing (the old 2 breakpoints)",
            at.SequenceEqual(new[] { wire.Count - 3, wire.Count - 1 }), string.Join(",", at));

        // 5b. SetCompactCacheMarks marks the step's own copies, never the loop's conversation
        var loopConv = Conversation(10, 50);
        var outbound = CodeAgentService.MicrocompactUpTo(loopConv, 9, 800);
        CodeAgentService.SetCompactCacheMarks(outbound, 9, 5);
        var marked = Enumerable.Range(0, outbound.Count).Where(i => outbound[i].CacheMark).ToList();
        check("cache-mark", "marks go on the last message before the new cut and before the old one (the shortened copies)",
            marked.SequenceEqual(new[] { 4, 8 }) && !loopConv.Any(m => m.CacheMark), string.Join(",", marked));
        var noCut = Conversation(3, 50);
        CodeAgentService.SetCompactCacheMarks(noCut, 0, 0);
        check("cache-mark", "control: no cut (a short conversation) = no mark", !noCut.Any(m => m.CacheMark), "");

        // 5c. The request really sent: system 1 + conversation 3
        var svc = new SettingsService();
        using (var mock = new GoldenMock())
        {
            check("cache-mark", "Anthropic mock proves its marker before any request", await mock.ProveAsync(), mock.Root);
            mock.Responder = _ => (200, "text/event-stream", ToolSse);
            ImageWireGolden.Pin(svc.Settings, "claude-opus-5-5", mock.Root);
            svc.Settings.PromptCachingEnabled = true;
            var p = new AnthropicProvider(svc);
            var sent = CodeAgentService.MicrocompactUpTo(Conversation(8, 200), 9, 800);
            CodeAgentService.SetCompactCacheMarks(sent, 9, 5);
            await p.ChatWithToolsAsync(sent, "system prompt", ImageWireGolden.Tools());
            using var doc = JsonDocument.Parse(mock.BodyAt(mock.Count - 1) ?? "{}");
            var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            int inSystem = doc.RootElement.GetProperty("system").EnumerateArray().Count(b => b.TryGetProperty("cache_control", out _));
            var where = Enumerable.Range(0, msgs.Count).Where(i => msgs[i].GetProperty("content").EnumerateArray().Any(b => b.TryGetProperty("cache_control", out _))).ToList();
            check("cache-mark", "sent request: system 1 + messages 3 = 4 breakpoints (the API's limit), on the marked copies and the last message",
                inSystem == 1 && where.SequenceEqual(new[] { 4, 8, msgs.Count - 1 }), $"system={inSystem} messages={string.Join(",", where)}");
        }

        // 5d. A long screenshot run over the threshold: what the cache writes, old way vs new way
        var (oldWrite, oldTotal, oldHits) = SimulateRun(newWay: false);
        var (newWrite, newTotal, advanceHits) = SimulateRun(newWay: true);
        check("cache-mark", "screenshot run past 90k tokens: the new way writes less to the cache (same requests)",
            newWrite < oldWrite * 0.75, $"written old={oldWrite:N0} new={newWrite:N0} of sent old={oldTotal:N0} new={newTotal:N0}");
        check("cache-mark", "every step the cut moves still reads the prefix shortened at the previous move",
            advanceHits.Count >= 2 && advanceHits.All(h => h), string.Join(",", advanceHits.Select(h => h ? "hit" : "MISS")));
        check("cache-mark", "control: the old way missed that prefix when the cut moved (what the logs showed)",
            oldHits.Count >= 2 && oldHits.Any(h => !h), string.Join(",", oldHits.Select(h => h ? "hit" : "MISS")));
    }

    /// <summary>The loop's per-step cache work, written in the order CodeAgentService runs it, with the API's cache
    /// modelled on wire prefixes. Returns the characters written to the cache, the characters sent, and for every step
    /// after the first cut move whether the prefix up to the previous cut was read back.</summary>
    static (long Written, long Sent, List<bool> AdvanceHits) SimulateRun(bool newWay)
    {
        var msgs = Conversation(10, 30_000);
        NativeMessage? mark = null;
        var cached = new HashSet<string>();     // wire prefixes (JSON of messages 0..i) the cache holds
        long written = 0, sent = 0;
        var hits = new List<bool>();
        bool movedBefore = false;
        for (int step = 0; step < 40; step++)
        {
            int prev = CodeAgentService.CompactBoundaryOf(msgs, mark);
            if (!newWay) CodeAgentService.PruneToolImagesBatched(msgs, 2, 3);
            int boundary = CodeAgentService.NextCompactBoundary(msgs, mark, true, false, 3, 800, out bool moved);
            mark = boundary > 0 && boundary < msgs.Count ? msgs[boundary] : null;
            if (newWay) CodeAgentService.StepImagePrune(msgs, 2, 3, moved, prev);
            var outbound = boundary > 0 ? CodeAgentService.MicrocompactUpTo(msgs, boundary, 800) : msgs;
            if (newWay) CodeAgentService.SetCompactCacheMarks(outbound, boundary, moved ? prev : 0);
            var wire = WireOf(outbound);
            // The cache keys on content: prefixes are taken before cache_control goes on (it is not part of the prefix).
            var prefixes = new string[wire.Count];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < wire.Count; i++) { sb.Append(JsonSerializer.Serialize(wire[i])); prefixes[i] = sb.ToString(); }
            var marks = Enumerable.Range(0, outbound.Count).Where(i => outbound[i].CacheMark).ToList();
            AnthropicProvider.MarkConversationCache(wire, marks);
            foreach (var m in outbound) m.CacheMark = false;   // the copies' marks; the loop's own messages carry none
            var bps = Enumerable.Range(0, wire.Count).Where(i => HasMark(wire[i])).ToList();
            // Read back: the longest cached prefix ending at, or up to 20 messages before, a breakpoint of this request.
            int readEnd = -1;
            foreach (int bp in bps)
                for (int i = bp; i >= Math.Max(0, bp - 20); i--)
                    if (cached.Contains(prefixes[i])) { readEnd = Math.Max(readEnd, i); break; }
            long total = prefixes[^1].Length, read = readEnd >= 0 ? prefixes[readEnd].Length : 0;
            sent += total; written += total - read;
            if (moved && movedBefore && prev > 0)
                hits.Add(readEnd >= 0 && readEnd >= Math.Min(prev, boundary) - 1);
            if (moved) movedBefore = true;
            foreach (int bp in bps) cached.Add(prefixes[bp]);

            msgs.Add(new NativeMessage { Role = "assistant", Content = { Think(), Use("s" + step) } });
            msgs.Add(new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "tool_result", ToolUseId = "s" + step,
                Content = $"shot {step} " + new string('z', 30_000),
                Images = [new CluadeX.Models.ToolImage("image/jpeg", "QUFB", 10, 10, $@"C:\s\{step}.jpg")] } } });
        }
        return (written, sent, hits);
    }
}
