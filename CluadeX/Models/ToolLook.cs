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
        if (t.StartsWith("git_", StringComparison.Ordinal) || t.StartsWith("github_", StringComparison.Ordinal) || t.StartsWith("gh_", StringComparison.Ordinal)) return ("", "tool.verb.git");
        if (t.StartsWith("bg_", StringComparison.Ordinal)) return ("", "tool.verb.background");
        if (t.StartsWith("memory_", StringComparison.Ordinal)) return ("", "tool.verb.memory");
        return t switch
        {
            "read_file" or "notebook_read" => ("", "tool.verb.read"),
            "write_file" or "create_directory" => ("", "tool.verb.write"),
            "edit_file" or "multi_edit" or "notebook_edit" or "rename_symbol" => ("", "tool.verb.edit"),
            "list_files" or "glob" or "search_files" => ("", "tool.verb.list"),
            "search_content" or "grep" or "find_symbol" or "list_symbols" or "find_references" or "impact_of" => ("", "tool.verb.search"),
            "run_command" or "powershell" or "repl" => ("", "tool.verb.run"),
            "run_build" or "verify_change" => ("", "tool.verb.build"),
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

/// <summary>One call inside a folded group of tool rows — the small box the group shows when opened.</summary>
public sealed record ToolStepView(string? Kind, string Verb, string? Target, string? Elapsed)
{
    public string Glyph => ToolLook.Of(Kind).Glyph;
    public bool HasTarget => !string.IsNullOrEmpty(Target);
    public bool HasElapsed => !string.IsNullOrEmpty(Elapsed);
}

/// <summary>One step of the agent's plan, as the plan card, the plan bar and the plan window show it. Its state changes
/// IN PLACE (<see cref="Reconcile"/>) so a row on screen sees the change and plays it (the check pops, the shimmer
/// stops) instead of being redrawn already finished.</summary>
public sealed class PlanItemView : System.ComponentModel.INotifyPropertyChanged
{
    public PlanItemView(int number, string text, string state)
    {
        Number = number;
        Text = text;
        _state = state;
    }

    public int Number { get; }
    public string Text { get; }

    private string _state;
    private DateTime _doneAt;
    /// <summary>"done", "active", "pending" or "queued" (the plan window's items the owner typed, not sent yet).</summary>
    public string State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            bool wasDone = IsDone;
            _state = value;
            // Only a change someone is watching pops: a row drawn already done (a chat opened later) just sits there.
            if (IsDone && !wasDone && PropertyChanged != null) _doneAt = DateTime.UtcNow;
            Raise(nameof(State)); Raise(nameof(IsDone)); Raise(nameof(IsActive)); Raise(nameof(IsPending)); Raise(nameof(IsQueued));
            Raise(nameof(JustDone)); Raise(nameof(IsWorking));
        }
    }
    public bool IsDone => _state == "done";
    public bool IsActive => _state == "active";
    public bool IsPending => _state == "pending";
    public bool IsQueued => _state == "queued";
    /// <summary>True for a moment after the row turned done on screen: the check pops and the row flashes green once.
    /// Read again when the row is redrawn (the plan card moving down the chat) — by then it is false, so nothing replays.</summary>
    public bool JustDone => _doneAt != default && DateTime.UtcNow - _doneAt < TimeSpan.FromSeconds(2);

    private bool _isLive = true;
    /// <summary>Its run is still going. A step left "in progress" by a run that ended keeps its colour but stops moving.</summary>
    public bool IsLive
    {
        get => _isLive;
        set { if (_isLive == value) return; _isLive = value; Raise(nameof(IsLive)); Raise(nameof(IsWorking)); }
    }
    /// <summary>The step is being worked on right now — the only state whose row animates without end (the ring turns,
    /// the light sweeps), and only while that row is on screen.</summary>
    public bool IsWorking => IsActive && _isLive;

    /// <summary>The rows already drawn, brought to <paramref name="fresh"/>: a row with the same words at the same place
    /// is kept and its state set in place; anything else is the fresh row. Returns <paramref name="current"/> itself when
    /// every row was kept (the list on screen does not redraw at all).</summary>
    public static List<PlanItemView> Reconcile(IReadOnlyList<PlanItemView>? current, List<PlanItemView> fresh)
    {
        if (current == null || current.Count == 0) return fresh;
        var merged = new List<PlanItemView>(fresh.Count);
        bool allKept = current.Count == fresh.Count;
        for (int i = 0; i < fresh.Count; i++)
        {
            var f = fresh[i];
            if (i < current.Count && current[i].Text == f.Text && current[i].Number == f.Number)
            {
                var keep = current[i];
                keep.State = f.State;
                keep.IsLive = f.IsLive;
                keep.Running = f.Running;
                merged.Add(keep);
            }
            else { merged.Add(f); allKept = false; }
        }
        return allKept && current is List<PlanItemView> same ? same : merged;
    }

    private string _running = "";
    /// <summary>On the active step only: what runs under it now ("กำลังรัน: เอเจนต์ย่อย 2 · คำสั่งเบื้องหลัง 1"), set in
    /// place on the same row so the card does not redraw its list (Services/Agents/RunActivity).</summary>
    public string Running
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            PropertyChanged?.Invoke(this, new(nameof(Running)));
            PropertyChanged?.Invoke(this, new(nameof(HasRunning)));
        }
    }
    public bool HasRunning => _running.Length > 0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
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
    private DateTime _endedAt;
    /// <summary>"running", "done", "failed" or "stopped".</summary>
    public string State
    {
        get => _state;
        set
        {
            bool wasRunning = IsRunning;
            _state = value;
            // An end someone is watching plays once (the check pops / the card shakes); a node read back from the
            // database is set here before anything shows it, so it just sits there.
            if (wasRunning && !IsRunning && PropertyChanged != null) _endedAt = DateTime.UtcNow;
            Raise(nameof(State)); Raise(nameof(IsRunning)); Raise(nameof(IsDone)); Raise(nameof(IsFailed));
            Raise(nameof(JustDone)); Raise(nameof(JustFailed)); Raise(nameof(IsNew));
        }
    }
    /// <summary>What it is doing now ("อ่าน main.ts"), or how it ended.</summary>
    public string Action { get => _action; set { _action = value; Raise(nameof(Action)); } }
    public int Steps { get => _steps; set { _steps = value; Raise(nameof(Steps)); Raise(nameof(StatsText)); } }
    public int ToolCalls { get => _tools; set { _tools = value; Raise(nameof(ToolCalls)); Raise(nameof(StatsText)); } }
    public string Elapsed { get => _elapsed; set { _elapsed = value; Raise(nameof(Elapsed)); Raise(nameof(StatsText)); } }
    private string _model = "";
    private decimal _cost;
    /// <summary>The model it runs on (routed by the dispatch policy; empty = the run's own).</summary>
    public string Model { get => _model; set { _model = value ?? ""; Raise(nameof(Model)); Raise(nameof(StatsText)); } }
    /// <summary>What its requests cost so far (USD).</summary>
    public decimal CostUsd { get => _cost; set { _cost = value; Raise(nameof(CostUsd)); Raise(nameof(StatsText)); } }
    public string StatsText => $"{_tools} tools · {_elapsed}"
        + (_model.Length > 0 ? $" · {_model.Replace("claude-", "")}" : "")
        + (_cost > 0 ? $" · ${_cost:0.000}" : "");
    public bool IsRunning => _state == "running";
    public bool IsDone => _state == "done";
    public bool IsFailed => _state is "failed" or "stopped";
    private bool JustEnded => _endedAt != default && DateTime.UtcNow - _endedAt < TimeSpan.FromSeconds(2);
    /// <summary>For a moment after it finished on screen (the check pops). A redrawn card reads false: nothing replays.</summary>
    public bool JustDone => IsDone && JustEnded;
    /// <summary>For a moment after it failed or was stopped on screen (the card shakes once).</summary>
    public bool JustFailed => IsFailed && JustEnded;
    /// <summary>It started a moment ago: its card slides in once. Only a running node can be new — one read back from
    /// the database never is.</summary>
    public bool IsNew => IsRunning && DateTime.Now - StartedAt < TimeSpan.FromSeconds(2);

    /// <summary>"explore", "general" or "specialist" — the avatar's look (a named specialist is any other kind).</summary>
    public string KindClass => Kind switch { "explore" => "explore", "" or "general" => "general", _ => "specialist" };
    /// <summary>The avatar's Segoe MDL2 glyph: a magnifier for explore, a wrench for general, a star for a specialist.</summary>
    public string KindGlyph => KindClass switch { "explore" => "\uE721", "general" => "\uE90F", _ => "\uE734" };

    private Services.Agents.SubAgentControl? _control;
    /// <summary>The owner's hold on it (stop, messages) — only for a sub-agent CluadeX runs itself; null for a CLI's own
    /// sub-agent and the dispatcher's workers, which cannot be steered from here.</summary>
    public Services.Agents.SubAgentControl? Control { get => _control; set { _control = value; Raise(nameof(Control)); } }
    private bool _stoppedByOwner;
    /// <summary>It ended because the owner pressed Stop in its window.</summary>
    public bool StoppedByOwner { get => _stoppedByOwner; set { _stoppedByOwner = value; Raise(nameof(StoppedByOwner)); } }

    /// <summary>What it ran, in order, with each result as it came back \u2014 the floating agent window lists these. Kept for
    /// the session only (a reloaded chat's node has none); the oldest finished ones go past <see cref="MaxActivity"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AgentActivityStep> Activity { get; } = new();
    public const int MaxActivity = 300;

    /// <summary>A tool started (or another step of its work). A step with no call id ends when the next one starts:
    /// nothing will report its result.</summary>
    public AgentActivityStep BeginStep(string? callId, string text)
    {
        foreach (var open in Activity)
            if (open.IsRunning && open.CallId == null) open.Finish("done", null);
        var step = new AgentActivityStep { CallId = string.IsNullOrEmpty(callId) ? null : callId, Text = text ?? "" };
        Activity.Add(step);
        while (Activity.Count > MaxActivity && Activity.FirstOrDefault(a => !a.IsRunning) is { } old) Activity.Remove(old);
        return step;
    }

    /// <summary>A tool's result came back (its call id names it). False when no running step has that id.</summary>
    public bool EndStep(string? callId, bool ok, string? result)
    {
        if (string.IsNullOrEmpty(callId)) return false;
        var step = Activity.LastOrDefault(a => a.CallId == callId && a.IsRunning);
        if (step == null) return false;
        step.Finish(ok ? "ok" : "error", result);
        return true;
    }

    /// <summary>The agent ended: a step still open stops with it ("done" when it finished, else "stopped").</summary>
    public void CloseSteps()
    {
        foreach (var open in Activity)
            if (open.IsRunning) open.Finish(IsDone ? "done" : "stopped", null);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}

/// <summary>One step of a sub-agent's work in its window: what it ran and, once back, how it went and what it returned.</summary>
public sealed class AgentActivityStep : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>The tool call's id when the engine knows it (its result is matched by it); null for a progress line.</summary>
    public string? CallId { get; init; }
    public string Text { get; init; } = "";
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public const int MaxResultChars = 4000;

    private string _state = "running", _result = "";
    private DateTime _endedAt;
    private bool _expanded;
    /// <summary>"running", "ok", "error", "done" (ended, no result known) or "stopped".</summary>
    public string State => _state;
    public string Result => _result;
    public bool HasResult => _result.Length > 0;
    public bool IsRunning => _state == "running";
    public bool IsOk => _state is "ok" or "done";
    public bool IsError => _state is "error" or "stopped";
    /// <summary>Its first line, for the row (the whole result opens under it).</summary>
    public string ResultPreview
    {
        get
        {
            string first = _result.TrimStart().Split('\n', 2)[0].Trim();
            return first.Length > 140 ? first[..140] + "\u2026" : first;
        }
    }
    public string DurationText
    {
        get
        {
            if (_endedAt == default) return "";
            var d = _endedAt - StartedAt;
            return d.TotalSeconds < 1 ? $"{Math.Max(0, d.TotalMilliseconds):0} ms"
                : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.TotalSeconds:0.0} s";
        }
    }
    /// <summary>Its whole result is open in the window.</summary>
    public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(nameof(IsExpanded)); } }

    internal void Finish(string state, string? result)
    {
        _state = state;
        _endedAt = DateTime.Now;
        if (!string.IsNullOrEmpty(result))
            _result = result.Length > MaxResultChars ? result[..MaxResultChars] + $"\n\u2026 (+{result.Length - MaxResultChars:N0})" : result;
        foreach (var p in new[] { nameof(State), nameof(Result), nameof(HasResult), nameof(IsRunning), nameof(IsOk), nameof(IsError),
                     nameof(ResultPreview), nameof(DurationText) })
            Raise(p);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}
