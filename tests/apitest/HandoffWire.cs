// Switching a running task to another model (owner, 2026-10-17: the API quota ran low at step 101 of a HIVE // BREACH
// task; Claude Dev said "CLI not found", the cowork room got the bare word "ทำต่อ"): "งานต้องต่อกันได้จริงๆ".
// Checked here: the CLI is found in every install layout on this machine, the new model gets a real handoff brief,
// Claude Dev may edit/run exactly as CluadeX's own agent could, the task's chat keeps its folder, and stream-json
// events show as steps.
using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class HandoffWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · carrying a task over to Claude Dev / the cowork room");

        // ─── finding the CLI ───
        const string App = @"C:\U\AppData\Roaming", Home = @"C:\U";
        string? Find(string where, params string[] files)
        {
            var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                for (string? d = Path.GetDirectoryName(f); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) dirs.Add(d);
            IEnumerable<string> Sub(string d) => dirs.Where(x => string.Equals(Path.GetDirectoryName(x), d, StringComparison.OrdinalIgnoreCase));
            return ClaudeDevProvider.FindCli(where, App, Home, set.Contains, dirs.Contains, Sub);
        }
        string npmCmd = App + @"\npm\claude.cmd", npmExe = App + @"\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        string desk295 = App + @"\Claude\claude-code\2.1.295\d2e29494ec83\claude.exe";
        string native = Home + @"\.local\bin\claude.exe";
        check("cli", "only npm shims on PATH → the exe the shim runs (this machine, 2026-10-17)",
            Find(App + @"\npm\claude" + "\r\n" + npmCmd + "\r\n", npmExe, desk295, native) == npmExe, "");
        check("cli", "desktop app layout <version>\\<hash>\\claude.exe; a half-written newer version is skipped",
            Find("", desk295, App + @"\Claude\claude-code\2.1.299\f0584be0fbaf\.payload") == desk295, "");
        check("cli", "versions compare as versions: 2.1.299 beats 2.1.30",
            Find("", App + @"\Claude\claude-code\2.1.30\claude.exe", App + @"\Claude\claude-code\2.1.299\aa\claude.exe")
                == App + @"\Claude\claude-code\2.1.299\aa\claude.exe", "");
        check("cli", "a real claude.exe on PATH wins", Find(@"D:\bin\claude.exe", @"D:\bin\claude.exe", npmExe) == @"D:\bin\claude.exe", "");
        check("cli", "the native installer's ~/.local/bin is the last resort", Find("", native) == native, "");
        check("cli", "nothing installed = null", Find("") == null, "");

        // ─── the handoff brief ───
        var history = new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = "Add LYRA's 4 skills.\nRead AGENTS.md first." },
            new() { Role = MessageRole.ToolAction, Content = "write_file data/skills.json\n{...}" },
            new() { Role = MessageRole.ToolAction, Content = "run_command godot --headless tests" },
            new() { Role = MessageRole.Assistant, Content = "⏸ หยุดชั่วคราวหลังขั้นที่ 101" },
        };
        string git = "branch: lyra-skills\ngit status --short:\n M scripts/actors/hero.gd\n?? data/skills.json\n";
        string? brief = AgentHandoff.Compose(history, @"D:\Game", git, 24000);
        check("handoff", "the brief carries the task, the folder, where it stopped, its steps and git",
            brief != null && brief.Contains("Add LYRA's 4 skills.") && brief.Contains(@"D:\Game") && brief.Contains("⏸")
            && brief.Contains("run_command godot") && brief.Contains("data/skills.json") && brief.Contains("lyra-skills"), brief ?? "(null)");
        check("handoff", "a fresh chat gets no brief", AgentHandoff.Compose(new List<ChatMessage> { new() { Role = MessageRole.User, Content = "hi" } }, @"D:\Game", git, 24000) == null, "");
        var huge = new List<ChatMessage>(history) { [0] = new ChatMessage { Role = MessageRole.User, Content = new string('x', 50000) } };
        string? small = AgentHandoff.Compose(huge, @"D:\Game", git, 3600);
        check("handoff", "the room's budget is kept (a 50 KB task still fits ~3.6 KB) and git is not crowded out",
            small != null && small.Length <= 3600 && small.Contains("lyra-skills"), $"{small?.Length}");
        // A compacted chat: the model's first turn is a summary that cuts the request short (the real hive-t2 brief).
        string fullTask = "Next task on HIVE // BREACH. CONTEXT: build on enemies-spitter-brute: create branch `lyra-skills` from it. " + new string('y', 900) + " END-OF-TASK";
        var compacted = new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = AgentHandoff.CompactionMark + " — those messages were compacted out]\nUser requests:\n  - Next task on HIVE // BREACH. CONTEXT: create branch `lyra-s..." },
            new() { Role = MessageRole.ToolAction, Content = "multi_edit run_tests.gd" },
            new() { Role = MessageRole.User, Content = "เกมไม่เปิดขึ้นมา" },
            new() { Role = MessageRole.Assistant, Content = "⏸ paused" },
        };
        string? cb = AgentHandoff.Compose(compacted, @"D:\Game", git, 24000, fullTask);
        int taskAt = cb?.IndexOf("### The task", StringComparison.Ordinal) ?? -1, bgAt = cb?.IndexOf("### Background", StringComparison.Ordinal) ?? -1;
        check("handoff", "after a compaction the task is the transcript's first request word for word; the summary is background",
            cb != null && cb.Contains("END-OF-TASK") && taskAt >= 0 && bgAt > taskAt && cb.Contains("- เกมไม่เปิดขึ้นมา"), cb ?? "(null)");
        string? cbNoTask = AgentHandoff.Compose(compacted, @"D:\Game", git, 24000);
        check("handoff", "no transcript request at hand: the summary still stands in as the task",
            cbNoTask != null && cbNoTask.Contains("lyra-s...") && cbNoTask.IndexOf("### The task", StringComparison.Ordinal) >= 0, "");

        string sent = AgentHandoff.WithBrief("@codex ทำต่อ", brief!);
        check("handoff", "the user's words lead (an @name target still parses)", BrainXRoomProvider.ParseTarget(sent).To == "codex", "");
        check("handoff", "the room's board card names the task, not just 'ทำต่อ'",
            BrainXRoomProvider.CardTitle(AgentHandoff.WithBrief("ทำต่อ", brief!)) == "ทำต่อ · Add LYRA's 4 skills.",
            BrainXRoomProvider.CardTitle(AgentHandoff.WithBrief("ทำต่อ", brief!)));

        // ─── what Claude Dev may do ───
        string folder = Path.Combine(Path.GetTempPath(), "cx-handoff-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        try
        {
            var perms = new PermissionService(new SettingsService());
            perms.Rules.Clear();
            check("grant", "no rule, no Accept edits = read-only", CodeAgentService.DecideWorkerGrant(perms, folder) == null, "");
            perms.SetAcceptEdits(folder);
            var g1 = CodeAgentService.DecideWorkerGrant(perms, folder);
            check("grant", "Accept edits on for the folder = may edit, not run", g1 is { Edit: true, Run: false }, $"{g1}");
            perms.SetAcceptEdits(null);
            perms.Rules.Add(new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow });
            var g2 = CodeAgentService.DecideWorkerGrant(perms, folder);
            check("grant", "an allow-everything rule = edit and run", g2 is { Edit: true, Run: true }, $"{g2}");
            perms.Rules.Insert(0, new PermissionRule { Pattern = "*", Scope = "execute", Action = PermAction.Ask });
            var g3 = CodeAgentService.DecideWorkerGrant(perms, folder);
            check("grant", "commands set to Ask = no commands (a headless CLI cannot ask)", g3 is { Edit: true, Run: false }, $"{g3}");
            check("grant", "a drive root is never a work folder", CodeAgentService.DecideWorkerGrant(perms, @"C:\") == null, "");
            check("grant", "no folder = read-only", CodeAgentService.DecideWorkerGrant(perms, null) == null, "");
            perms.Rules.Clear();
        }
        finally { try { Directory.Delete(folder, true); } catch { } }

        string ro = ClaudeDevProvider.BuildArgs(@"D:\Game", null, "", "auto");
        string rw = ClaudeDevProvider.BuildArgs(@"D:\Game", new ClaudeDevProvider.WorkerGrant(true, true), "", "auto");
        check("args", "advice turn: read-only tools, text output", ro.Contains("--output-format text") && !ro.Contains("Edit,") && !ro.Contains("Bash"), ro);
        check("args", "worker turn: edit tools, Bash, acceptEdits, stream-json, dangerous commands denied",
            rw.Contains("stream-json") && rw.Contains("Edit,Write") && rw.Contains(",Bash") && rw.Contains("--permission-mode acceptEdits")
            && rw.Contains("--disallowedTools") && rw.Contains("git push --force"), rw);
        string runOnly = ClaudeDevProvider.BuildArgs(@"D:\Game", new ClaudeDevProvider.WorkerGrant(false, true), "", "auto");
        check("args", "run without edit: no edit tools, no acceptEdits", !runOnly.Contains("Edit,Write") && !runOnly.Contains("acceptEdits"), runOnly);

        // ─── stream-json events ───
        string tool = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Running the tests.\"},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"godot --headless tests\"}}]}}";
        string? shown = ClaudeDevProvider.RenderStreamLine(tool, out bool e1, out _);
        check("stream", "text and a tool step both show", shown != null && shown.Contains("Running the tests.") && shown.Contains("⏵ Bash") && shown.Contains("godot --headless tests") && !e1, shown ?? "");
        ClaudeDevProvider.RenderStreamLine("{\"type\":\"result\",\"is_error\":true,\"result\":\"Credit balance is too low\"}", out bool e2, out string? r2);
        check("stream", "an error result is caught with its text", e2 && r2 == "Credit balance is too low", r2 ?? "");
        check("stream", "system/init lines and junk show nothing",
            ClaudeDevProvider.RenderStreamLine("{\"type\":\"system\",\"subtype\":\"init\"}", out _, out _) == null
            && ClaudeDevProvider.RenderStreamLine("not json", out _, out _) == null, "");

        // ─── quota failover: which errors mean "the account cannot pay" ───
        check("failover", "credit, quota and usage-limit errors count as exhausted",
            CodeAgentService.IsQuotaExhausted("Your credit balance is too low to access the Anthropic API")
            && CodeAgentService.IsQuotaExhausted("HTTP 429: You exceeded your current quota, please check your plan")
            && CodeAgentService.IsQuotaExhausted("RESOURCE_EXHAUSTED: quota exceeded")
            && CodeAgentService.IsQuotaExhausted("HTTP 400: You have reached your specified API usage limits"), "");
        check("failover", "a passing rate limit, an outage or a bad request do not",
            !CodeAgentService.IsQuotaExhausted("HTTP 429: rate_limit_error: Number of request tokens has exceeded your per-minute rate limit")
            && !CodeAgentService.IsQuotaExhausted("HTTP 529: Overloaded") && !CodeAgentService.IsQuotaExhausted("HTTP 400: invalid tool schema")
            && !CodeAgentService.IsQuotaExhausted(null), "");

        // ─── the task's chat keeps its folder ───
        bool Has(string p) => p == @"D:\Game";
        check("folder", "a pipe task's chat runs in its own folder when the window shows another project",
            ChatViewModel.PickTaskFolder(@"D:\Game", @"E:\MV_assets", "[ObsidianX] hive-t2", false, Has) == @"D:\Game", "");
        check("folder", "a paused chat too", ChatViewModel.PickTaskFolder(@"D:\Game", @"E:\MV_assets", "Skills", true, Has) == @"D:\Game", "");
        check("folder", "an ordinary chat follows the window", ChatViewModel.PickTaskFolder(@"D:\Game", @"E:\MV_assets", "Skills", false, Has) == null, "");
        check("folder", "same folder, or the folder is gone = the window's",
            ChatViewModel.PickTaskFolder(@"D:\Game", @"d:\game\", "[ObsidianX] x", true, Has) == null
            && ChatViewModel.PickTaskFolder(@"D:\Gone", @"E:\MV_assets", "[ObsidianX] x", true, Has) == null, "");
        return Task.CompletedTask;
    }
}
