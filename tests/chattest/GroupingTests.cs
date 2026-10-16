using System.IO;
using System.Reflection;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>G — tool rows fold together: calls of one kind in a row become one row (MCP per server, shell,
    /// reading, plan…); an edit, a failure, a question stay on their own row.</summary>
    static async Task TestToolGrouping()
    {
        Log("\n## G — tool rows of one kind in a row fold into one row; edits and failures stay on their own");
        await FreshChat();
        var fire = typeof(ChatViewModel).GetMethod("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Tool(string name, bool ok = true, string output = "ok") =>
            fire.Invoke(Vm, [new ToolResult { ToolName = name, Type = ToolType.McpTool, Success = ok, Output = output, Arguments = new() }]);
        async Task Settle() { await Render(); await Task.Delay(50); await Render(); }

        Tool("mcp__brainx-brain__task_update"); Tool("mcp__brainx-brain__agent_inbox"); Tool("mcp__brainx-brain__agent_inbox");
        Tool("mcp__brainx-brain__agent_send"); Tool("mcp__brainx-brain__agent_send");
        await Settle();
        var rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G1 five brainx-brain calls in a row are ONE row that lists them shortly",
            rows.Count == 1 && rows[0].IsCollapsedGroup && rows[0].CollapsedCount == 5 && rows[0].Content.Contains("brainx-brain/agent_send") && !rows[0].Content.Contains("mcp__") && rows[0].ToolTarget.Contains("agent_inbox ×2"),
            string.Join(" | ", rows.Select(r => r.Content)));
        Check("G1b the folded row opens into one small box per call, in order",
            rows.Count == 1 && rows[0].GroupSteps.Count == 5 && rows[0].GroupSteps[0].Kind == "mcp__brainx-brain__task_update"
            && rows[0].GroupSteps[4].Verb.Contains("agent_send"),
            string.Join(" | ", rows.FirstOrDefault()?.GroupSteps.Select(s => $"{s.Kind}:{s.Verb}:{s.Target}") ?? []));

        Tool("mcp__cluadex-chrome__computer");
        Tool("run_command"); Tool("powershell"); Tool("run_command");
        Tool("read_file"); Tool("grep_search");
        await Settle();
        rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G2 background calls of every kind in a row (another server, shell, reading) fold into the SAME row, called 'Worked'",
            rows.Count == 1 && rows[0].CollapsedCount == 11 && rows[0].ToolVerb == L("chat.tools.groupVerb"),
            string.Join(" | ", rows.Select(r => $"{r.CollapsedCount}:{r.ToolVerb}:{r.Content}")));

        Tool("edit_file"); Tool("edit_file");
        Tool("run_command", ok: false, output: "error: build failed"); Tool("run_command");
        await Settle();
        rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G3 edits stay one row each (their own diff); a failed command stays visible on its own",
            rows.Count == 5 && !rows[1].IsCollapsedGroup && !rows[2].IsCollapsedGroup && !rows[3].ToolSuccess && !rows[4].IsCollapsedGroup,
            string.Join(" | ", rows.Select(r => $"{r.ToolName}:{r.ToolSuccess}:{r.CollapsedCount}")));
        Tool("mcp__cluadex-chrome__tabs_context_mcp", ok: false, output: "Browser extension is not connected. Please ensure the Claude browser extension is installed");
        Tool("mcp__cluadex-chrome__tabs_context_mcp", ok: false, output: "Browser extension is not connected. Please ensure the Claude browser extension is installed");
        await Settle();
        var help = Vm.Messages.Where(m => m.Role == MessageRole.System && m.Content.Contains("chrome://extensions")).ToList();
        Check("G5 Chrome not connected → ONE plain-language note with the steps (extension, account, reload), not one per failure",
            help.Count == 1 && help[0].IsSideNote, string.Join(" | ", help.Select(h => h.Content.Split('\n')[0])));
        bool internals = Vm.ShowAgentInternals;
        Vm.ShowAgentInternals = true;   // the owner shows tool rows; the harness default hides the successful ones
        var list = Find<System.Windows.Controls.ListBox>(Win).First(l => l.Name == "ChatList");
        list.ScrollIntoView(Vm.Messages.First());
        await Settle();
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-tool-groups.png"));
        rows[0].IsExpanded = true;
        list.ScrollIntoView(rows[0]);
        await Settle(); await Task.Delay(500); await Render();
        var boxes = Container(rows[0]) is { } c0 ? Find<System.Windows.Controls.ItemsControl>(c0).FirstOrDefault(i => ReferenceEquals(i.ItemsSource, rows[0].GroupSteps)) : null;
        Check("G4 an opened group shows its calls as boxes on screen", boxes is { IsVisible: true, Items.Count: 11 }, $"boxes={boxes?.Items.Count} visible={boxes?.IsVisible}");
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-tool-group-open.png"));
        rows[0].IsExpanded = false;
        Vm.ShowAgentInternals = internals;

    
        // G6 — a REAL run (the agent loop, so each call first shows its "⏵ running" line): the calls must still fold into
        // one balloon. They did not in the app: the finished status line stayed between two tool rows (tests that fired
        // OnToolExecuted directly never saw it).
        await FreshChat();
        // The plain queue: a keyed script only matches the turn's first call (the loop's next calls carry tool results).
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Reply("[ACTION: list_files]\npath: .\n[/ACTION]"));
        Fake.Script.Enqueue(FakeStep.Reply("[ACTION: read_file]\npath: app.txt\n[/ACTION]"));
        Fake.Script.Enqueue(FakeStep.Reply("[ACTION: list_files]\npath: .\n[/ACTION]"));
        Fake.Script.Enqueue(FakeStep.Reply("All read."));
        Vm.SetWorkingDirectory(ProjA);   // a project to look around in (app.txt is there)
        await Send("[G6] look around");
        bool sawLive = false;
        for (int i = 0; i < 200 && Vm.IsGenerating; i++)
        {
            if (Vm.Messages.Any(m => m.IsLive && m.LiveText.Length > 0)) sawLive = true;
            foreach (var pr in Vm.Messages.Where(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered).ToList())
                pr.ResolvePermission(PermissionOutcome.Allowed);
            await Task.Delay(25);
        }
        await WaitIdle();
        await Settle();
        var g6 = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G6 a real run's background calls fold into ONE row (no status line left between them)",
            g6.Count == 1 && g6[0].CollapsedCount == 3 && !Vm.Messages.Any(m => m.Role == MessageRole.AgentStatus && m.ToolKind != null),
            string.Join(" | ", Vm.Messages.Select(m => $"{m.Role}:{m.ToolKind}:{m.CollapsedCount}:{(m.Content ?? "").Replace('\n', '⏎')[..Math.Min(90, (m.Content ?? "").Length)]}")));
        Check("G6 while it ran, the row said what runs (the balloon); once done it is still", sawLive && !Vm.Messages.Any(m => m.IsLive),
            $"sawLive={sawLive}");
        Fake.ScriptFor.Clear();
    }
}
