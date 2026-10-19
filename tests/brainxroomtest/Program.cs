using System.IO;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.Services.Providers;

// Live round trip through the BrainX cowork room. Offline checks first (parsing), then — with --live — a real
// request: "dotnet run -- --live @claude <text>". Somebody in the room has to answer it, ending with [done].
static class Program
{
    static int pass, fail;
    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {id,-5} {name}" + (ok || detail.Length == 0 ? "" : $"\n        {detail}"));
    }

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("── offline: request parsing and room JSON ──");
        var (to, text) = BrainXRoomProvider.ParseTarget("@codex แก้ปุ่ม Save");
        Check("P1", "@codex … goes to codex", to == "codex" && text == "แก้ปุ่ม Save", $"{to}|{text}");
        (to, text) = BrainXRoomProvider.ParseTarget("@Claude, codex ช่วยดู");
        Check("P2", "@claude, codex … goes to both", to == "claude,codex" && text == "ช่วยดู", $"{to}|{text}");
        (to, text) = BrainXRoomProvider.ParseTarget("ช่วยดู bug email@x.com หน่อย");
        Check("P3", "no leading @ = whoever is free (an @ inside the text is not a name)", to == null, $"{to}|{text}");
        var lines = BrainXRoomProvider.ParseLines("""{"inRoom":true,"messages":[{"from":"codex","body":"done ```cs\nx\n``` [done]","addressed":"you","topic":"cx-1"},{"from":"cluadex","body":"me","mine":true}]}""");
        Check("P4", "cowork_read lines parse (from, body, addressed, topic, mine)",
            lines.Count == 2 && lines[0].From == "codex" && lines[0].Addressed == "you" && lines[0].Topic == "cx-1" && lines[1].Mine,
            string.Join(" | ", lines));
        var online = BrainXRoomProvider.RoomOnline("""{"room":[{"agent":"claude","online":true},{"agent":"codex","online":false}]}""");
        Check("P5", "room[] → who is online now", online.SequenceEqual(new[] { "claude" }), string.Join(",", online));

        Console.WriteLine("── offline: the board card (2026-10-19: Stop / give-up drop it; a held card keeps the wait going) ──");
        string? card = BrainXRoomProvider.CardIdOf("""{"ok":true,"task":{"id":"t-3f9a1c","title":"x","status":"open"},"room":[]}""");
        Check("B1", "cowork_task add → the card id", card == "t-3f9a1c", card ?? "null");
        Check("B2", "a failed add has no id", BrainXRoomProvider.CardIdOf("""{"ok":false,"note":"title required"}""") == null);
        const string board = """{"board":[{"id":"t-aaaaaa","status":"open"},{"id":"t-3f9a1c","status":"doing","assignee":"codex"}],"room":[]}""";
        Check("B3", "the card on the board: status and who holds it", BrainXRoomProvider.CardStatus(board, "t-3f9a1c") == ("doing", "codex"),
            BrainXRoomProvider.CardStatus(board, "t-3f9a1c").ToString());
        Check("B4", "a card no longer on the (unfinished-only) board is gone = done or dropped", BrainXRoomProvider.CardStatus(board, "t-999999").Status == "gone");
        Check("B5", "an unreadable list is unknown, not gone", BrainXRoomProvider.CardStatus("not json", "t-3f9a1c").Status == "unknown");
        Check("B6", "the board's own ✅ line for our card closes it; another card's does not",
            BrainXRoomProvider.ClosesCard("✅ codex เสร็จแล้ว [t-3f9a1c] fix — ok", "t-3f9a1c")
            && !BrainXRoomProvider.ClosesCard("✅ codex เสร็จแล้ว [t-aaaaaa] other", "t-3f9a1c")
            && !BrainXRoomProvider.ClosesCard("▶ codex กำลังทำ [t-3f9a1c] fix", "t-3f9a1c"));

        Console.WriteLine("── offline: which room lines belong to this turn ──");
        var mine = new BrainXRoomProvider.RoomLine("codex", "here it is", "you", null, false);
        var onTopic = new BrainXRoomProvider.RoomLine("gemini", "x", "", "cx-1", false);
        var aboutCard = new BrainXRoomProvider.RoomLine("claude", "▶ claude กำลังทำ [t-3f9a1c] fix", "", null, false);
        var chatter = new BrainXRoomProvider.RoomLine("grok", "anyone seen my keys", "", "other", false);
        Check("F1", "addressed to us / on our topic / about our card → shown",
            BrainXRoomProvider.IsForThisTurn(mine, "cx-1", "t-3f9a1c", null) && BrainXRoomProvider.IsForThisTurn(onTopic, "cx-1", null, null)
            && BrainXRoomProvider.IsForThisTurn(aboutCard, "cx-1", "t-3f9a1c", null));
        Check("F2", "unrelated room chatter is no longer pasted into the reply (it was, with nobody named)",
            !BrainXRoomProvider.IsForThisTurn(chatter, "cx-1", "t-3f9a1c", null));
        Check("F3", "a named agent's lines count even off-topic", BrainXRoomProvider.IsForThisTurn(chatter, "cx-1", null, new(StringComparer.OrdinalIgnoreCase) { "Grok" }));

        Console.WriteLine("── offline: the request says what the owner's rules allow ──");
        string ro = BrainXRoomProvider.BuildRequest("fix it", "cx-1", new() { @"E:\Code\X" }, null);
        string rw = BrainXRoomProvider.BuildRequest("fix it", "cx-1", new() { @"E:\Code\X" }, new ClaudeDevProvider.WorkerGrant(true, true));
        Check("R1", "no edit grant → read only, propose diffs (it always said 'you may edit')", ro.Contains("do NOT allow edits") && !ro.Contains("you may edit"), ro);
        Check("R2", "edit + run grant → may edit and run builds", rw.Contains("you may edit files there directly") && rw.Contains("run builds"), rw);
        Check("R3", "a failed join/say is explained in words, not raw JSON",
            BrainXRoomProvider.ReasonOf("""{"ok":false,"note":"the room is closed"}""") == "the room is closed"
            && BrainXRoomProvider.ReasonOf("""{"ok":false}""") == null);

        Console.WriteLine("── offline: loop guards ──");
        static async Task<string> Ask(BrainXRoomProvider pv, string msg)
        {
            var sb = new StringBuilder();
            await foreach (var c in pv.ChatAsync(new List<ChatMessage>(), msg)) sb.Append(c);
            return sb.ToString();
        }
        var offlineProvider = new BrainXRoomProvider(new FileSystemService());
        var pipeScope = new AgentRunScope("pipe-test", null) { FromPipe = true };
        AgentRunScope.Enter(pipeScope);
        string fromPipe = await Ask(offlineProvider, "@codex ทำงานนี้");
        AgentRunScope.Enter(null);
        Check("G1", "a run started over the pipe (the room handing work to cluadex) is never relayed back into the room",
            fromPipe.Contains("วนลูป") && !fromPipe.Contains("ส่งเข้าห้อง cowork แล้ว"), fromPipe);
        string relayed = await Ask(offlineProvider, "ทำ X\n---\n" + BrainXRoomProvider.RelayFooter + " (...)");
        Check("G2", "a request that already carries our relay footer is not relayed again", relayed.Contains("ส่งซ้ำ"), relayed);

        if (!args.Contains("--live")) { Console.WriteLine($"\n═══ {pass} pass · {fail} FAIL ═══ (add --live for the room round trip)"); return fail == 0 ? 0 : 1; }

        Console.WriteLine("── live: a request through the room ──");
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "current", "mcp", "brainx-mcp.exe");
        var settings = new SettingsService();
        var log = new DebugLogService();
        var mcp = new McpServerManager(settings, log);
        mcp.SetConfig("brainx-brain", new McpServerConfig
        {
            Command = exe,
            Env = new() { ["BRAINX_VAULT"] = Environment.GetEnvironmentVariable("BRAINX_VAULT") ?? @"G:\Obsidian" },
        });
        bool started = await mcp.StartServerAsync("brainx-brain");
        var brain = new BrainSyncService(mcp, log);
        Check("L0", "brainx-mcp starts as CluadeX's child and is seen as the brain", started && brain.IsBrainAvailable, mcp.BrainStatusText);
        if (!brain.IsBrainAvailable) return 1;

        var fs = new FileSystemService { WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")) };
        var provider = new BrainXRoomProvider(fs);
        var (okConn, conn) = await provider.TestConnectionAsync();
        Console.WriteLine("  test connection: " + conn);

        string prompt = string.Join(" ", args.SkipWhile(a => a != "--live").Skip(1));
        if (prompt.Length == 0) prompt = "@claude ทดสอบ BrainX model ของ CluadeX: ตอบคำว่า pong พร้อมโค้ดบล็อก C# หนึ่งบรรทัด";
        var reply = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var chunk in provider.ChatAsync(new List<ChatMessage>(), prompt))
        {
            reply.Append(chunk);
            Console.Write(chunk);
        }
        Console.WriteLine($"\n  ({sw.Elapsed.TotalSeconds:0} s)");
        string r = reply.ToString();
        Check("L1", "the request went into the room", r.Contains("ส่งเข้าห้อง cowork แล้ว"), r);
        Check("L2", "an answer came back under the responder's name", r.Contains("### 💬 "), r);
        Check("L3", "its code block came through intact", r.Contains("```"), r);
        Check("L4", "[done] ended the wait and is not shown as text", r.Contains("✅ เสร็จแล้ว") && !r.Contains("[done]"), r);
        await mcp.StopServerAsync("brainx-brain");
        Console.WriteLine($"\n═══ {pass} pass · {fail} FAIL ═══");
        return fail == 0 ? 0 : 1;
    }
}
