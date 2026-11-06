using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// COP — a CLI agent in a chat with no project (owner 2026-11-06: Codex was told "ทำใหม่เลยในโฟลเดอร์ใหม่ เปิดโฟลเดอร์ใหม่เลย"
/// and answered "this session is read-only" for 13 turns). Through the real chat page and CodeAgentService with a scripted
/// CLI: the wrapper names open_project when nothing is open; a turn that opens a folder is followed AT ONCE by a turn that
/// starts in it; the user is asked once whether the CLI may edit there, and a yes holds for that chat; a no says so and
/// starts nothing. Also the pure pieces (the bridge's note, the follow-up's message, the grant). No CLI runs.
/// </summary>
internal static partial class Program
{
    static async Task TestCliOpenProject()
    {
        Log("\n## COP — a CLI agent opens a folder for the task: asked once, then it carries on there with edit rights");
        CopPure();

        var perms = Sp.GetRequiredService<PermissionService>();
        var savedRules = perms.Rules.ToList();
        var cli = new ScriptedCli();
        var before = Settings.Settings.ActiveProvider;
        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.ClaudeDev);
        SetField(Providers, "_activeProvider", cli);
        var tools = Sp.GetRequiredService<AgentToolService>();
        try
        {
            perms.Rules.Clear();   // no Allow rule: what decides is Accept edits (off) and the answer to the card
            perms.SetAcceptEdits(null);
            await CopCarryOn(cli, tools, allow: true);
            await CopCarryOn(cli, tools, allow: false);
            await CopNoOpen(cli);
        }
        finally
        {
            perms.Rules.Clear();
            perms.Rules.AddRange(savedRules);
            perms.SetAcceptEdits(null);
            Settings.UpdateSettings(s => s.ActiveProvider = before);
            SetField(Providers, "_activeProvider", Fake);
            Vm.SetWorkingDirectory(ProjA);
            await FreshChat();
        }
    }

    static void CopPure()
    {
        string none = OpenAIDevProvider.BuildPrompt(@"C:\Users\me", null, null, "", "make 100 clips", noProject: true);
        Check("COP0 Codex with no project: the wrapper names open_project with create=\"true\" and says not to answer 'cannot'",
            none.Contains("open_project") && none.Contains("create=\"true\"") && none.Contains("Never answer that you cannot create files")
            && !none.Contains("propose file edits as diffs"), none.Length > 900 ? none[..900] : none);
        string proj = OpenAIDevProvider.BuildPrompt(@"D:\Code\App", null, null, "", "explain", noProject: false);
        Check("COP0 Codex in a read-only project: still says how to open / start another folder; the old read-only line stays",
            proj.Contains("open_project") && proj.Contains("propose file edits as diffs") && !proj.Contains("No project folder is open"), "");
        string gem = string.Join(" ", GeminiDevProvider.BuildArgs(@"C:\Users\me", null, "default", null, noProject: true));
        string agy = AntigravityDevProvider.BuildPrompt(@"C:\Users\me", null, null, "", "go", noProject: true);
        string grok = GrokDevProvider.BuildPrompt(@"C:\Users\me", null, null, "", "go", toolsHint: "CluadeX's own tools are on the MCP server cluadex.", noProject: true);
        string claude = ClaudeDevProvider.BuildArgs(@"C:\Users\me", null, "", "auto", toolsConfig: @"C:\t\mcp.json", noProject: true);
        Check("COP0 Gemini Dev, Antigravity, Grok Dev and Claude Dev with no project say the same (each by its own tool name)",
            gem.Contains("No project folder is open") && agy.Contains("No project folder is open")
            && grok.Contains("cluadex__open_project (through use_tool)") && claude.Contains("call mcp__cluadex__open_project yourself"),
            $"gemini={gem.Contains("No project")} agy={agy.Contains("No project")} grok={grok.Contains("__open_project (through")} claude={claude.Contains("mcp__cluadex__open_project yourself")}");

        Check("COP0 the bridge's note after open_project: this process cannot write there, end the turn, CluadeX carries on",
            ChatToolBridge.OpenProjectNote(null, @"F:\clip1").Contains("CluadeX carries the task on at once")
            && ChatToolBridge.OpenProjectNote(@"D:\A", @"d:\a\").Contains("nothing changes")
            && ChatToolBridge.OpenProjectNote(@"D:\A", @"D:\Lib", added: true).Contains("--add-dir"), "");
        Check("COP0 FolderOpenedDuringTurn: none → X = X; X → X (case, slash) = none; X → Y = Y; X → none = none",
            CodeAgentService.FolderOpenedDuringTurn(null, @"F:\clip1") == @"F:\clip1"
            && CodeAgentService.FolderOpenedDuringTurn(@"D:\A", @"d:\a\") == null
            && CodeAgentService.FolderOpenedDuringTurn(@"D:\A", @"D:\B") == @"D:\B"
            && CodeAgentService.FolderOpenedDuringTurn(@"D:\A", null) == null, "");
        string msg = CodeAgentService.OpenedFolderMessage(@"F:\clip1", new ClaudeDevProvider.WorkerGrant(true, false), "ทำ 100 คลิปในโฟลเดอร์ใหม่");
        Check("COP0 the follow-up's message: the folder, may create and edit, do the work now, the user's own request",
            msg.Contains(@"F:\clip1") && msg.Contains("may now create and edit files") && !msg.Contains("run commands")
            && msg.Contains("ทำ 100 คลิปในโฟลเดอร์ใหม่") && msg.Contains("do not ask the user to open or allow anything"), msg);

        string folder = Path.Combine(PipeRoot, "cop-grant-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var perms = new PermissionService(new SettingsService());
        perms.Rules.Clear();
        var g = CodeAgentService.DecideWorkerGrant(perms, folder, editsApprovedInChat: true);
        perms.Rules.Add(new PermissionRule { Pattern = "*", Scope = "write", ToolName = "write_file", Action = PermAction.Deny });
        Check("COP0 the chat's yes counts as Accept edits for that folder — never over a Deny rule, never for a drive root",
            g is { Edit: true, Run: false } && CodeAgentService.DecideWorkerGrant(perms, folder, true) == null
            && CodeAgentService.DecideWorkerGrant(new PermissionService(new SettingsService()), @"C:\", true) == null, $"{g}");
    }

    /// <summary>A chat with no project; the CLI's turn opens <c>folder</c> (as its open_project call through the bridge
    /// would). Allow → a second call that starts in the folder with edit rights, and the next turn in that chat keeps them
    /// without asking again. Deny → one call only, and the reply says why nothing was made.</summary>
    static async Task CopCarryOn(ScriptedCli cli, AgentToolService tools, bool allow)
    {
        string tag = allow ? "yes" : "no";
        string folder = Path.Combine(PipeRoot, "cop-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        Vm.SetWorkingDirectory("");
        await FreshChat();
        cli.Reset();
        cli.Turns.Enqueue(new CliTurn([CliResult("Opened the folder; CluadeX carries on there.")], Before: async () =>
        {
            var r = await tools.ExecuteToolAsync(new ToolCall
            {
                Type = ToolType.OpenProject, ToolName = "open_project", Arguments = new() { ["path"] = folder },
            });
            if (!r.Success) throw new InvalidOperationException("open_project failed: " + r.Output);
        }));
        cli.Turns.Enqueue(new CliTurn([
            CliText("f1", "Made plan.md with the 100 clips."),
            CliResult("Made plan.md with the 100 clips."),
        ]));
        int cardsBefore = Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest);
        await Send("ทำแผน 100 คลิปในโฟลเดอร์ใหม่ " + tag);
        ChatMessage? card = null;
        bool asked = await WaitUntil(() => Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest) > cardsBefore
                                         && (card = Vm.Messages.Last(m => m.Role == MessageRole.PermissionRequest)) != null, 15000);
        Check($"COP{(allow ? 1 : 3)} after the CLI opened the folder the user is asked once, in words: Claude Dev asks to create and edit files in it",
            asked && card != null && (card.Content ?? "").Contains(folder) && (card.Content ?? "").Contains("Claude Dev")
            && (card.Content ?? "").Contains(LocF("perm.do.cliEditFolder", folder)), card?.Content ?? "no card");
        card?.ResolvePermission(allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied);
        bool idle = await WaitIdle(20000);
        string reply = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Content ?? "";
        if (allow)
        {
            var second = cli.Starts.Count > 1 ? cli.Starts[1] : default;
            Check("COP1 yes → a second CLI call at once, starting IN the opened folder with edit rights (the first was read-only, no folder)",
                idle && cli.Messages.Count == 2 && cli.Starts[0].Grant == null && string.IsNullOrEmpty(cli.Starts[0].Folder)
                && second.Grant is { Edit: true } && SamePath(second.Folder ?? "", folder),
                $"calls={cli.Messages.Count} first={cli.Starts.ElementAtOrDefault(0)} second={second}");
            Check("COP1 …it is told the folder, that it may edit, to do the work now, and the user's request",
                cli.Messages.Count == 2 && cli.Messages[1].Contains(folder) && cli.Messages[1].Contains("may now create and edit files")
                && cli.Messages[1].Contains("ทำแผน 100 คลิปในโฟลเดอร์ใหม่ yes"), cli.Messages.ElementAtOrDefault(1) ?? "");
            Check("COP1 …the chat now belongs to that folder and the reply is the follow-up's report",
                SamePath(Vm.CurrentSession?.ProjectPath ?? "", folder) && reply.Contains("Made plan.md"), $"{Vm.CurrentSession?.ProjectPath} · {reply}");

            // The next message in the same chat: edit rights without a new card.
            cli.Reset();
            cli.Turns.Enqueue(new CliTurn([CliText("n1", "Added 10 more."), CliResult("Added 10 more.")]));
            int cards = Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest);
            await Send("เพิ่มอีก 10");
            bool idle2 = await WaitIdle(20000);
            Check("COP2 the next turn in that chat starts in the folder with edit rights, and asks nothing again",
                idle2 && cli.Starts.Count == 1 && cli.Starts[0].Grant is { Edit: true }
                && Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest) == cards,
                $"calls={cli.Starts.Count} grant={cli.Starts.ElementAtOrDefault(0).Grant}");
        }
        else
        {
            Check("COP3 no → no second call, and the reply says editing there was not allowed and how to go on",
                idle && cli.Messages.Count == 1 && reply.Contains(LocF("agent.cliEditDeclined", "Claude Dev", folder)),
                $"calls={cli.Messages.Count} reply={reply}");
        }
    }

    /// <summary>A CLI turn that opens nothing runs once and asks nothing.</summary>
    static async Task CopNoOpen(ScriptedCli cli)
    {
        Vm.SetWorkingDirectory("");
        await FreshChat();
        cli.Reset();
        cli.Turns.Enqueue(new CliTurn([CliResult("Here is the plan, in the chat.")]));
        int cards = Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest);
        await Send("แค่ตอบในแชท");
        bool idle = await WaitIdle(20000);
        Check("COP4 a turn that opens no folder: one call, no card, the reply as it was",
            idle && cli.Messages.Count == 1 && Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest) == cards
            && (Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Content ?? "").Contains("Here is the plan"),
            $"calls={cli.Messages.Count}");
    }
}
