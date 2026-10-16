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
