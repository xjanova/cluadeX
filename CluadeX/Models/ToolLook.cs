using System.Text.RegularExpressions;

namespace CluadeX.Models;

/// <summary>
/// How a tool row looks: its icon (Segoe MDL2 glyph) and the localization key of the verb it is shown with
/// ("อ่านไฟล์ scripts/main.gd"). One table, so the chat and anything else that lists tool calls agree.
/// </summary>
public static class ToolLook
{
    public static (string Glyph, string VerbKey) Of(string? tool)
    {
        string t = (tool ?? "").ToLowerInvariant();
        if (t.StartsWith("mcp__", StringComparison.Ordinal)) return ("", "tool.verb.mcp");
        if (t.StartsWith("browser_", StringComparison.Ordinal)) return ("", "tool.verb.browser");
        if (t.StartsWith("git_", StringComparison.Ordinal) || t.StartsWith("github_", StringComparison.Ordinal)) return ("", "tool.verb.git");
        if (t.StartsWith("bg_", StringComparison.Ordinal)) return ("", "tool.verb.background");
        if (t.StartsWith("memory_", StringComparison.Ordinal)) return ("", "tool.verb.memory");
        return t switch
        {
            "read_file" or "notebook_read" => ("", "tool.verb.read"),
            "write_file" or "create_directory" => ("", "tool.verb.write"),
            "edit_file" or "multi_edit" or "notebook_edit" => ("", "tool.verb.edit"),
            "list_files" or "glob" or "search_files" => ("", "tool.verb.list"),
            "search_content" or "grep" or "find_symbol" or "list_symbols" => ("", "tool.verb.search"),
            "run_command" or "powershell" or "repl" => ("", "tool.verb.run"),
            "run_build" => ("", "tool.verb.build"),
            "run_tests" => ("", "tool.verb.test"),
            "web_fetch" or "web_search" => ("", "tool.verb.web"),
            "agent_spawn" or "subagent_invoke" => ("", "tool.verb.agent"),
            "todo_write" => ("", "tool.verb.plan"),
            "ask_user" => ("", "tool.verb.ask"),
            "skill_invoke" => ("", "tool.verb.skill"),
            _ => ("", "tool.verb.other"),
        };
    }

    /// <summary>The MCP server of a qualified tool name (mcp__server__tool), else null.</summary>
    public static string? McpServer(string? tool)
    {
        if (tool == null || !tool.StartsWith("mcp__", StringComparison.Ordinal)) return null;
        int end = tool.IndexOf("__", 5, StringComparison.Ordinal);
        return end > 5 ? tool[5..end] : null;
    }
}

/// <summary>One row of a side-by-side diff: the old line on the left, the new on the right (either may be empty).</summary>
public sealed record DiffPairRow(string OldNo, string OldText, EditDiffKind OldKind,
                                 string NewNo, string NewText, EditDiffKind NewKind, string? Hunk)
{
    public bool IsHunk => Hunk != null;
}

public static class DiffPairs
{
    private static readonly Regex HunkNumbers = new(@"@@ -(\d+) \+(\d+) @@", RegexOptions.Compiled);

    /// <summary>Pair a unified diff for two columns: context on both sides, a removed block next to the added block
    /// that replaced it, line numbers from the hunk headers ("@@ -old +new @@").</summary>
    public static List<DiffPairRow> SideBySide(IReadOnlyList<EditDiffLine> lines)
    {
        var rows = new List<DiffPairRow>();
        int oldNo = 1, newNo = 1;
        var removed = new List<string>();
        var added = new List<string>();
        void Flush()
        {
            int n = Math.Max(removed.Count, added.Count);
            for (int i = 0; i < n; i++)
            {
                bool l = i < removed.Count, r = i < added.Count;
                rows.Add(new DiffPairRow(
                    l ? (oldNo++).ToString() : "", l ? removed[i] : "", l ? EditDiffKind.Remove : EditDiffKind.Context,
                    r ? (newNo++).ToString() : "", r ? added[i] : "", r ? EditDiffKind.Add : EditDiffKind.Context, null));
            }
            removed.Clear();
            added.Clear();
        }
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case EditDiffKind.Hunk:
                    Flush();
                    var m = HunkNumbers.Match(line.Text);
                    if (m.Success) { oldNo = int.Parse(m.Groups[1].Value); newNo = int.Parse(m.Groups[2].Value); }
                    rows.Add(new DiffPairRow("", "", EditDiffKind.Hunk, "", "", EditDiffKind.Hunk, line.Text));
                    break;
                case EditDiffKind.Remove:
                    if (added.Count > 0) Flush();   // an added block before a removed one: they are not one change
                    removed.Add(line.Text);
                    break;
                case EditDiffKind.Add:
                    added.Add(line.Text);
                    break;
                default:
                    Flush();
                    rows.Add(new DiffPairRow((oldNo++).ToString(), line.Text, EditDiffKind.Context,
                                             (newNo++).ToString(), line.Text, EditDiffKind.Context, null));
                    break;
            }
        }
        Flush();
        return rows;
    }
}

/// <summary>One step of the agent's plan, as the plan card shows it.</summary>
public sealed record PlanItemView(int Number, string Text, string State)
{
    public bool IsDone => State == "done";
    public bool IsActive => State == "active";
    public bool IsPending => State == "pending";
}

/// <summary>One sub-agent on the agent map card: what it was given, what it is doing, how it ended. Updated in place
/// while it runs (UI thread).</summary>
public sealed class AgentNode : System.ComponentModel.INotifyPropertyChanged
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    /// <summary>"explore", "general" or a specialist's name.</summary>
    public string Kind { get; init; } = "";
    public string Task { get; init; } = "";
    public DateTime StartedAt { get; init; } = DateTime.Now;

    private string _state = "running", _action = "", _elapsed = "";
    private int _steps, _tools;
    /// <summary>"running", "done", "failed" or "stopped".</summary>
    public string State { get => _state; set { _state = value; Raise(nameof(State)); Raise(nameof(IsRunning)); Raise(nameof(IsDone)); Raise(nameof(IsFailed)); } }
    /// <summary>What it is doing now ("อ่าน main.ts"), or how it ended.</summary>
    public string Action { get => _action; set { _action = value; Raise(nameof(Action)); } }
    public int Steps { get => _steps; set { _steps = value; Raise(nameof(Steps)); Raise(nameof(StatsText)); } }
    public int ToolCalls { get => _tools; set { _tools = value; Raise(nameof(ToolCalls)); Raise(nameof(StatsText)); } }
    public string Elapsed { get => _elapsed; set { _elapsed = value; Raise(nameof(Elapsed)); Raise(nameof(StatsText)); } }
    public string StatsText => $"{_tools} tools · {_elapsed}";
    public bool IsRunning => _state == "running";
    public bool IsDone => _state == "done";
    public bool IsFailed => _state is "failed" or "stopped";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}
