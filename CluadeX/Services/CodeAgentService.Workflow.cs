using CluadeX.Models;
using CluadeX.Services.Agents;

namespace CluadeX.Services;

/// <summary>
/// The workflow tool's agents (docs/agent-orchestration.md §2): each one is a native sub-agent — the same
/// <see cref="RunHeldSubAgentAsync"/> agent_spawn uses, on the routed model, under a child of the calling run's scope (so
/// its spend lands on the run and its budget guard holds). The workflow engine already holds the machine's
/// <see cref="AgentLoad"/> slot for it, so the held core is called, never the one that takes a second slot.
/// </summary>
public partial class CodeAgentService
{
    /// <summary>Connect the app's workflows to this agent (once, at startup).</summary>
    public void ConnectWorkflows(WorkflowService svc)
    {
        svc.Runner = RunWorkflowAgentAsync;
        svc.RunnerRefusal = WorkflowRunnerRefusal;
        svc.BudgetSpent = RunBudgetSpent;
        svc.BudgetLeft = () =>
        {
            decimal per = _settingsService.Settings.RunBudgetUsd;
            if (per <= 0 || RootOf(AgentRunScope.Current) is not { } root) return null;
            return Math.Max(0, per + root.BudgetGrantedUsd - root.CostUsd);
        };
    }

    /// <summary>Why a workflow's agents cannot run on the active model now, or null.</summary>
    private string? WorkflowRunnerRefusal()
    {
        var p = _providerManager.ActiveProvider;
        if (p == null || !p.IsReady) return "No model is ready to run the workflow's agents. Pick and connect a model first.";
        // A CLI agent's chat (Claude Dev, Codex, Gemini, Antigravity): each workflow agent is its own Claude Dev process
        // (the same path a sub-agent takes when the API quota runs out). The CLI's OWN spawns stay under CliAgentGate.
        if (p.RunsOwnSubAgents) return null;
        if (!p.SupportsNativeToolUse)
            return "The active model has no native tool calling (it works through the [ACTION:] text protocol), so sub-agents — and a "
                 + "workflow, which is made of them — cannot run on it. Do the work yourself, or the user can switch to a Claude / "
                 + "OpenAI / Gemini API model or llama-server.";
        return null;
    }

    private async Task<WorkflowAgentResult> RunWorkflowAgentAsync(WorkflowAgentCall call, CancellationToken ct)
    {
        var provider = _providerManager.ActiveProvider;
        bool edit = string.Equals(call.Type, "general", StringComparison.OrdinalIgnoreCase);
        if (provider is { RunsOwnSubAgents: true, IsReady: true } && !provider.SupportsNativeToolUse)
        {
            string? cd = await RunSubAgentOnClaudeDevAsync(call.Prompt, edit, ct);
            return cd != null ? new WorkflowAgentResult(cd, 0)
                : throw new InvalidOperationException("Claude Dev (the Claude Code CLI) is not ready, so this workflow agent could not run.");
        }
        if (provider == null || !provider.SupportsNativeToolUse || !provider.IsReady)
            throw new InvalidOperationException(WorkflowRunnerRefusal() ?? "no model");
        // In a turn: a child of its run. Resumed from the card: a scope of its own for that chat.
        var parent = AgentRunScope.Current;
        var scope = parent?.CreateChild()
                    ?? new AgentRunScope(call.SessionKey, _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null)
                    { Provider = provider, ProviderType = _providerManager.ActiveProviderType };
        var kind = DispatchPolicy.KindOf(edit);
        if (kind == SubAgentKind.Explore) scope.StepEffort = "low";
        AgentRunScope.Enter(scope);
        string? model = RoutedModel(kind);
        SubAgentModel.Use(model);
        int n = Interlocked.Increment(ref _subAgentCounter);
        string warm = call.Attempt == 1 ? WarmContext() : "";
        var request = new SubAgentRequest(call.Prompt, "", edit ? "general" : "explore");
        string report = await RunHeldSubAgentAsync(provider, request, ct,
            new SubAgentRunOptions(n, call.Label, edit ? "general" : "explore", model, scope, warm, WorkflowId: call.WorkflowId)) ?? "";
        return new WorkflowAgentResult(report, scope.CostUsd);
    }
}
