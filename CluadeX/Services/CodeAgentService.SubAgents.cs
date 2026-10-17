using System.Text;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services;

/// <summary>
/// Real sub-agents for agent_spawn, the way Claude Code's Agent tool works: each one is its own small tool loop with
/// its own conversation, on the same model and the same tools (under the same permission rules), and only its final
/// report comes back to the main agent. Several agent_spawn calls in one turn run at the same time. Before this,
/// agent_spawn only handed the task text back to the main agent ("Sub-task queued"), so nothing ran in parallel and
/// every bulky file the "sub-task" read landed in the main conversation (found in the Warroom run, 2026-10-10).
/// </summary>
public partial class CodeAgentService
{
    /// <summary>Steps one sub-agent may take before it must report.</summary>
    public const int SubAgentMaxSteps = 30;

    /// <summary>Characters of one tool result a sub-agent keeps (the rest is cut with a note), so a sub-agent's own
    /// context stays small; the main agent never sees these at all.</summary>
    public const int SubAgentMaxResultChars = 20_000;

    /// <summary>Tools a sub-agent never gets: it cannot spawn more agents, ask the user, commit or push, or change
    /// the session's mode. Public for the contract harness.</summary>
    public static readonly HashSet<string> SubAgentExcludedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent_spawn", "subagent_invoke", "ask_user", "plan_mode", "todo_write", "config",
        "git_commit", "git_push", "git_merge", "git_checkout", "git_stash", "git_pull",
        "git_worktree_create", "git_worktree_remove", "gh_pr_create", "gh_issue_create",
        "memory_save", "memory_delete", "instinct_evolve", "skill_invoke",
    };

    /// <summary>The read-only tools an "explore" sub-agent (the default) gets. Public for the contract harness.</summary>
    public static readonly HashSet<string> SubAgentExploreTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_files", "search_content", "glob", "grep_search", "codebase_search",
        "list_symbols", "find_symbol", "lsp_diagnostics", "git_status", "git_diff", "git_log", "git_branch",
        "web_fetch", "web_search", "brain_recall", "gh_pr_list", "gh_issue_list", "gh_repo_view",
    };

    /// <summary>The tool set a sub-agent of this type gets, from the main loop's catalogue. "explore": reading,
    /// searching and the brain's read tools; "general": everything but <see cref="SubAgentExcludedTools"/>.
    /// Public for the contract harness.</summary>
    public static List<ToolSchema> SubAgentTools(IEnumerable<ToolSchema> all, string type)
    {
        bool general = string.Equals(type, "general", StringComparison.OrdinalIgnoreCase);
        return all.Where(t => !SubAgentExcludedTools.Contains(t.Name)
                              && (general || SubAgentExploreTools.Contains(t.Name) || IsReadOnlyBrainTool(t.Name)))
                  .ToList();
    }

    static bool IsReadOnlyBrainTool(string name)
    {
        // mcp__brainx-brain__brain_search / brain_recall / brain_get_note / brain_walk … — reading the brain, not writing it.
        int i = name.LastIndexOf("__", StringComparison.Ordinal);
        string leaf = i >= 0 ? name[(i + 2)..] : name;
        return name.Contains("brain", StringComparison.OrdinalIgnoreCase)
               && leaf is "brain_search" or "brain_recall" or "brain_get_note" or "brain_semantic_search" or "brain_walk"
                   or "brain_get_backlinks" or "brain_list" or "brain_bundle" or "brain_bundles_list";
    }

    static string SubAgentSystemPrompt(string type, string workingDir, bool thai) =>
        $"""
        You are a sub-agent of CluadeX working for the main agent, not for the user directly. Project folder: {workingDir}
        Do the task below with your tools, end to end, then stop. Be efficient: search before reading, read only what you need.
        If BrainX tools (brain_search / brain_recall) are available, search the brain FIRST for this task (2-4 keywords, project
        name first): past fixes, decisions and gotchas from this machine beat guessing. Name the notes you used in your report.
        {(string.Equals(type, "general", StringComparison.OrdinalIgnoreCase)
            ? "You may edit files when the task asks for it. Never commit, push or touch other repositories."
            : "You are read-only: investigate and report. Do not try to change files.")}
        You cannot ask the user questions or start other agents.
        Your final message is the ONLY thing the main agent sees: report what you found, with file:line references and the
        evidence (exact values, error text), and what you changed if anything. Keep it under about 400 words.
        Write the report in {(thai ? "Thai (code, paths and identifiers stay as they are)" : "English")}.
        """;

    /// <summary>The tools a specialist (subagent_invoke) gets: the ones its definition allows, matched through the
    /// same name resolver the loop uses ("grep" → grep_search), never the excluded ones. Public for the contract harness.</summary>
    public static List<ToolSchema> SpecialistTools(IEnumerable<ToolSchema> all, IEnumerable<string> allowed, Func<string, ToolType?> resolve)
    {
        var allowedTypes = allowed.Select(resolve).Where(t => t != null).Select(t => t!.Value).ToHashSet();
        return all.Where(t => !SubAgentExcludedTools.Contains(t.Name) && resolve(t.Name) is { } type && allowedTypes.Contains(type)).ToList();
    }

    /// <summary>What a sub-agent is doing, for the agent map: Phase "start" | "tool" | "done" | "failed" | "stopped".
    /// Raised in the run's own flow (AgentRunScope.Current is the run that spawned it).</summary>
    public sealed record SubAgentEvent(int Id, string Label, string Kind, string Task, string Phase, string Detail, int Steps,
        string? Model = null, decimal CostUsd = 0);

    /// <summary>How one sub-agent runs when the dispatcher starts it: its map id and label, kind, routed model, own scope
    /// (its cost), its warm-start context; ReportOnDone = false for a fixer, whose "done" comes after the re-run.</summary>
    private sealed record SubAgentRunOptions(int Id, string Label, string Kind, string? Model, AgentRunScope? Scope,
        string? Warm = null, bool ReportOnDone = true);

    public event Action<SubAgentEvent>? OnSubAgentEvent;

    private void RaiseSubAgent(SubAgentEvent e)
    {
        try { OnSubAgentEvent?.Invoke(e); } catch (Exception ex) { _debugLog?.Warn("SubAgent", "agent map update failed", ex); }
    }

    /// <summary>Run one sub-agent to the end and return its report; null when the active model cannot run one (no
    /// native tool use — the [ACTION:] protocol), so the caller falls back to handing the task to the main agent.</summary>
    private async Task<string?> RunSubAgentAsync(SubAgentRequest request, CancellationToken ct)
    {
        _subAgentLast.Value = null;
        if (string.Equals(request.Type, "workers", StringComparison.OrdinalIgnoreCase))
            return await RunWorkersAsync(request, ct);

        // ─── the dispatch policy: may this one run, on which model, how many at once ───
        var dispatch = CurrentDispatch();
        bool canEdit = SubAgentCanEdit(request);
        if (Agents.DispatchPolicy.Gate(dispatch, request.Type, canEdit) is { } refusal)
        {
            _debugLog?.Info("Dispatch", $"sub-agent refused ({request.Type}, edit={canEdit}) · {dispatch.Reason}");
            return refusal;
        }
        var parent = AgentRunScope.Current;
        var gate = GateFor(parent, dispatch.MaxConcurrency);
        await gate.WaitAsync(ct);
        try
        {
            var kind = Agents.DispatchPolicy.KindOf(canEdit);
            string? model = dispatch.Legacy ? null : RoutedModel(kind);
            var child = parent?.CreateChild();
            if (child != null && !dispatch.Legacy && kind == Agents.SubAgentKind.Explore) child.StepEffort = "low";
            AgentRunScope.Enter(child ?? parent);
            Agents.SubAgentModel.Use(model);
            if (!dispatch.Legacy) parent?.MarkStrategy((int)Agents.DispatchStrategy.FanOut);
            string warm = dispatch.Legacy ? "" : WarmContext();
            if (!dispatch.Legacy && !canEdit && QuotaRecentlyExhausted && _settingsService.Settings.AutoFailoverOnQuota)
                return await RunSubAgentOnClaudeDevAsync(BriefOf(request, warm), false, ct);
            int n = Interlocked.Increment(ref _subAgentCounter);
            bool thai = _localizationService.CurrentLanguage == "th";
            bool specialist = !string.IsNullOrWhiteSpace(request.Persona);
            string label = (thai ? $"เอเจนต์ย่อย #{n}" : $"Sub-agent #{n}") + (specialist ? $" ({request.Type})" : "");
            return await RunSubAgentCoreAsync(request, ct, new SubAgentRunOptions(n, label, request.Type, model, child, warm));
        }
        catch (OperationCanceledException)
        {
            if (_subAgentLast.Value is { } e) RaiseSubAgent(e with { Phase = "stopped", Detail = "" });
            throw;
        }
        catch (Exception ex)
        {
            if (_subAgentLast.Value is { } e) RaiseSubAgent(e with { Phase = "failed", Detail = Shorten(ex.Message, 120) });
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>Would this request's sub-agent get edit tools?</summary>
    private bool SubAgentCanEdit(SubAgentRequest request)
    {
        if (request.AllowedTools is { Count: > 0 } allowed)
            return allowed.Select(_agentToolService.ResolveToolTypePublic)
                .Any(t => t is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit);
        return !string.IsNullOrWhiteSpace(request.Persona) || string.Equals(request.Type, "general", StringComparison.OrdinalIgnoreCase);
    }

    private static string BriefOf(SubAgentRequest r, string warm) =>
        r.Task.Trim() + (string.IsNullOrWhiteSpace(r.Context) ? "" : "\n\nContext from the main agent:\n" + r.Context.Trim())
        + (string.IsNullOrWhiteSpace(warm) ? "" : "\n\n" + warm) + "\n\nYou are read-only: investigate and report (file:line, evidence).";

    // The sub-agent this flow is running (for the failed / stopped event).
    private readonly AsyncLocal<SubAgentEvent?> _subAgentLast = new();

    private async Task<string?> RunSubAgentCoreAsync(SubAgentRequest request, CancellationToken ct, SubAgentRunOptions? opts)
    {
        var provider = _providerManager.ActiveProvider;
        if (provider == null || !provider.SupportsNativeToolUse || !provider.IsReady)
            return null;

        string task = request.Task, context = request.Context, type = request.Type;
        bool thai = _localizationService.CurrentLanguage == "th";
        int n = opts?.Id ?? Interlocked.Increment(ref _subAgentCounter);
        bool specialist = !string.IsNullOrWhiteSpace(request.Persona);
        string label = opts?.Label ?? (thai ? $"เอเจนต์ย่อย #{n}" : $"Sub-agent #{n}") + (specialist ? $" ({type})" : "");
        var catalogue = _agentToolService.BuildNativeToolSchemas();
        var tools = request.AllowedTools is { Count: > 0 } allowed
            ? SpecialistTools(catalogue, allowed, _agentToolService.ResolveToolTypePublic)
            : SubAgentTools(catalogue, specialist ? "general" : type);
        tools = CapToolSchemas(tools, MaxApiToolSchemas, out _, ManagedMcpServers());
        bool canEdit = tools.Any(t => t.Name is "write_file" or "edit_file" or "multi_edit" or "apply_patch");
        string system = SubAgentSystemPrompt(specialist ? (canEdit ? "general" : "explore") : type, _fileSystemService.WorkingDirectory ?? "", thai);
        if (specialist)
            system += $"\n\n## Your role: {type}\n{request.Persona!.Trim()}";

        var first = new StringBuilder(task.Trim());
        if (!string.IsNullOrWhiteSpace(context)) first.Append("\n\nContext from the main agent:\n").Append(context.Trim());
        // Warm start: the cached repo map and the run's brain recall, once — not a re-read of the project. A fixer's or
        // worker's prompt already carries its own (Warm is the prompt itself then).
        if (!string.IsNullOrWhiteSpace(opts?.Warm) && opts!.Warm != task) first.Append("\n\n").Append(opts.Warm!.Trim());
        var messages = new List<NativeMessage> { new() { Role = "user", Content = { new ContentBlock { Type = "text", Text = first.ToString() } } } };

        OnAgentStatus?.Invoke($"{label}: {(thai ? "เริ่มงาน" : "starting")} · {Shorten(task, 60)}");
        var ev = new SubAgentEvent(n, label, opts?.Kind ?? type, Shorten(task, 160), "start", "", 0, opts?.Model);
        decimal Cost() => opts?.Scope?.CostUsd ?? 0;
        _subAgentLast.Value = ev;
        RaiseSubAgent(ev);
        _debugLog?.Info("SubAgent", $"{label} ({type}) start: {Shorten(task, 120)} · {tools.Count} tools");

        int timeoutSec = _settingsService.Settings.InteractiveRequestTimeoutSeconds;
        for (int step = 1; step <= SubAgentMaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            bool lastStep = step == SubAgentMaxSteps;
            if (lastStep)
                messages[^1].Content.Add(new ContentBlock { Type = "text", Text = "[Step budget reached: no more tools. Write your report now.]" });

            NativeToolResponse response;
            using (var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Idle timeout, reset by every stream event (the same keep-alive the main loop uses).
                if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    response = await provider.ChatWithToolsAsync(messages, system, tools,
                        onTextDelta: _ => { if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec)); },
                        ct: callCts.Token, toolChoice: lastStep ? "none" : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && IsQuotaExhausted(ex.Message)
                                           && _settingsService.Settings.AutoFailoverOnQuota)
                {
                    // The API ran out: this sub-agent's task goes to Claude Dev, like the main loop's failover.
                    NoteQuotaExhausted();
                    _debugLog?.Warn("SubAgent", $"{label}: quota exhausted — carrying on with Claude Dev");
                    RaiseSubAgent(ev with { Phase = "tool", Detail = "→ Claude Dev (API quota)", Steps = step, CostUsd = Cost() });
                    string? cdReport = await RunSubAgentOnClaudeDevAsync(first.ToString(), canEdit, ct);
                    if (cdReport == null) throw;
                    RaiseSubAgent(ev with { Phase = opts?.ReportOnDone == false ? "tool" : "done", Detail = Shorten(cdReport, 140), Steps = step, CostUsd = Cost() });
                    return cdReport;
                }
            }

            if (response.ToolCalls.Count == 0)
            {
                string report = (response.TextContent ?? "").Trim();
                _debugLog?.Info("SubAgent", $"{label} done in {step} step(s), {report.Length} chars");
                OnAgentStatus?.Invoke($"{label}: {(thai ? "เสร็จ" : "done")} ({step} {(thai ? "ขั้น" : "steps")})");
                // The node shows the report's opening words — as text, not markdown (**, `, #, list bullets).
                string gist = System.Text.RegularExpressions.Regex.Replace(report, @"[*`#>_]+|^\s*[-•]\s*", "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                RaiseSubAgent(ev with { Phase = opts?.ReportOnDone == false ? "tool" : "done",
                    Detail = Shorten(gist, 140), Steps = step, CostUsd = Cost() });
                return report.Length > 0 ? report : (thai ? "(เอเจนต์ย่อยไม่ได้เขียนรายงาน)" : "(the sub-agent wrote no report)");
            }

            var assistant = response.ToAssistantMessage(includeToolUse: true);
            var results = new NativeMessage { Role = "user" };
            // Its own tools in parallel when they only read, one by one otherwise — like the main loop.
            var calls = response.ToolCalls.Select(tc => (tc, type: _agentToolService.ResolveToolTypePublic(tc.Name))).ToList();
            async Task<ContentBlock> Run((NativeToolCall tc, ToolType? type) c)
            {
                try
                {
                    if (c.type == null || !tools.Any(t => t.Name.Equals(c.tc.Name, StringComparison.OrdinalIgnoreCase)))
                        return new ContentBlock { Type = "tool_result", ToolUseId = c.tc.Id, IsError = true,
                            Content = $"Tool '{c.tc.Name}' is not available to a sub-agent." };
                    var call = new ToolCall { ToolName = c.tc.Name, Type = c.type.Value, Arguments = ParseJsonInputToArgs(c.tc.Input) };
                    string doing = GetToolStatusMessage(c.tc.Name, call.Arguments);
                    OnAgentStatus?.Invoke($"{label}: {doing}");
                    RaiseSubAgent(ev with { Phase = "tool", Detail = Shorten(doing, 90), Steps = step, CostUsd = Cost() });
                    var r = await _agentToolService.ExecuteToolAsync(call, ct);
                    string text = r.Success ? (r.Output ?? "") : $"Error: {r.Error ?? r.Output}";
                    if (text.Length > SubAgentMaxResultChars)
                        text = text[..SubAgentMaxResultChars] + $"\n… (cut: {text.Length - SubAgentMaxResultChars} more characters — read a narrower range)";
                    return new ContentBlock { Type = "tool_result", ToolUseId = c.tc.Id, Content = text, IsError = !r.Success };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return new ContentBlock { Type = "tool_result", ToolUseId = c.tc.Id, IsError = true, Content = "Error: " + ex.Message };
                }
            }
            var safe = calls.Where(c => c.type is { } t && new ToolCall { Type = t }.IsConcurrencySafe).ToList();
            var ordered = new Dictionary<string, ContentBlock>();
            foreach (var (c, block) in safe.Zip(await Task.WhenAll(safe.Select(Run))))
                ordered[c.tc.Id] = block;
            foreach (var c in calls.Except(safe))
                ordered[c.tc.Id] = await Run(c);
            foreach (var c in calls) results.Content.Add(ordered[c.tc.Id]);

            messages.Add(assistant);
            messages.Add(results);
        }
        RaiseSubAgent(ev with { Phase = opts?.ReportOnDone == false ? "tool" : "done",
            Detail = thai ? "ใช้ขั้นครบโดยไม่ได้สรุป" : "step budget used, no report", Steps = SubAgentMaxSteps, CostUsd = Cost() });
        return thai ? "(เอเจนต์ย่อยใช้ขั้นตอนครบโดยไม่ได้สรุป)" : "(the sub-agent used its step budget without a report)";
    }

    private int _subAgentCounter;

    static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
