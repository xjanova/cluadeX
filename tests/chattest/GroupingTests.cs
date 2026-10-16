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

        Tool("mcp__cluadex-chrome__computer");
        Tool("run_command"); Tool("powershell"); Tool("run_command");
        Tool("read_file"); Tool("grep_search");
        await Settle();
        rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G2 another server starts its own row; shell commands fold together; reading and searching fold together",
            rows.Count == 4 && rows[2].CollapsedCount == 3 && rows[3].CollapsedCount == 2, string.Join(" | ", rows.Select(r => $"{r.CollapsedCount}:{r.Content}")));

        Tool("edit_file"); Tool("edit_file");
        Tool("run_command", ok: false, output: "error: build failed"); Tool("run_command");
        await Settle();
        rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        Check("G3 edits stay one row each (their own diff); a failed command stays visible on its own",
            rows.Count == 8 && !rows[4].IsCollapsedGroup && !rows[5].IsCollapsedGroup && !rows[6].ToolSuccess && !rows[7].IsCollapsedGroup,
            string.Join(" | ", rows.Select(r => $"{r.ToolName}:{r.ToolSuccess}:{r.CollapsedCount}")));
        bool internals = Vm.ShowAgentInternals;
        Vm.ShowAgentInternals = true;   // the owner shows tool rows; the harness default hides the successful ones
        var list = Find<System.Windows.Controls.ListBox>(Win).First(l => l.Name == "ChatList");
        list.ScrollIntoView(Vm.Messages.First());
        await Settle();
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-tool-groups.png"));
        Vm.ShowAgentInternals = internals;

    }
}
