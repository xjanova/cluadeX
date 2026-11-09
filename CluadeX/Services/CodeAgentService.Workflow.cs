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
        string? worktree = call.WorkingDirectory;
        var kind = DispatchPolicy.KindOf(edit);
        bool auto = _settingsService.Settings.SubAgentAutoModel;
        var parent = AgentRunScope.Current;
        AgentRunScope NewScope(Providers.IAiProvider p, AiProviderType t) =>
            // In a turn: a child of its run. Resumed from the card: a scope of its own for that chat. An isolated agent's
            // scope is its worktree, and Accept edits covers that worktree only (as a worker's does).
            (worktree != null ? parent?.CreateChild(worktree, acceptEditsInFolder: true) : parent?.CreateChild())
            ?? new AgentRunScope(call.SessionKey, worktree ?? (_fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null))
            { Provider = p, ProviderType = t, AcceptEditsRoot = worktree };

        // A read-only agent goes to the model already up on this PC (Colibri, llama-server, Ollama…) when the owner chose
        // that and it can do the work — for a CLI chat too, whose own spawns can never reach it. Never one that would
        // have to load, and never a server whose slots are all taken (it would queue and time out).
        if (!edit && auto && await LocalSubAgentProviderAsync(Models.SubAgentTier.Cheap, false, new HashSet<string>(), ct) is { } local)
        {
            using (local.Lease)
            {
                var ls = NewScope(local.Provider, _providerManager.ActiveProviderType);
                ls.EffortCap = DispatchPolicy.EffortCapFor(Models.SubAgentTier.Cheap);
                ls.SubAgentTier = "cheap";
                AgentRunScope.Enter(ls);
                SubAgentModel.Use(null);   // a local model reads its own configuration
                int ln = Interlocked.Increment(ref _subAgentCounter);
                string localModel = LocalModelNameFor(local.Provider) is { Length: > 0 } lm ? System.IO.Path.GetFileName(lm) : local.Provider.ProviderId;
                _debugLog?.Info("Dispatch", $"workflow agent {call.Label} · read-only · local {local.Provider.ProviderId} ({localModel})");
                string r = await RunHeldSubAgentAsync(local.Provider, new SubAgentRequest(call.Prompt, "", "explore"), ct,
                    new SubAgentRunOptions(ln, call.Label, "explore", localModel, ls, call.Attempt == 1 ? WarmContext() : "",
                        WorkflowId: call.WorkflowId, Provider: local.Provider)) ?? "";
                return new WorkflowAgentResult(r, ls.CostUsd);
            }
        }

        if (provider is { RunsOwnSubAgents: true, IsReady: true } && !provider.SupportsNativeToolUse)
        {
            // A CLI chat: each workflow agent is a CLI process of its own, in its own worktree when it has one (the CLI
            // runs there and nowhere else — WorkerGrant.Folder). A Codex chat's agents run on Codex, the plan the owner
            // picked (`codex exec -C <worktree>`); the others on Claude Dev. Until 2026-11-09 a worktree agent on a CLI
            // chat threw, so a Codex chat could never split its edits.
            var cli = WorkflowCliFor(_providerManager.ActiveProviderType);
            _debugLog?.Info("Dispatch", $"workflow agent {call.Label} · {(edit ? "edit" : "read-only")} · {cli}"
                + (worktree != null ? $" in worktree {worktree}" : ""));
            string? cliReport = cli == AiProviderType.OpenAIDev
                ? await RunSubAgentOnCodexAsync(call.Prompt, edit, ct, worktree)
                : await RunSubAgentOnClaudeDevAsync(call.Prompt, edit, ct, worktree);
            if (cliReport == null && cli != AiProviderType.ClaudeDev)
                cliReport = await RunSubAgentOnClaudeDevAsync(call.Prompt, edit, ct, worktree);
            return cliReport != null ? new WorkflowAgentResult(cliReport, 0)
                : throw new InvalidOperationException($"Neither {cli} nor Claude Dev (a CLI on this PC) is ready, so this workflow agent could not run.");
        }
        if (provider == null || !provider.SupportsNativeToolUse || !provider.IsReady)
            throw new InvalidOperationException(WorkflowRunnerRefusal() ?? "no model");
        var scope = NewScope(provider, _providerManager.ActiveProviderType);
        // Auto model (Settings ▸ Agent): a read-only workflow agent runs cheap — the cheapest model at low effort, a
        // ceiling its own loop cannot raise. Off: the run's own model and effort.
        if (auto && kind == SubAgentKind.Explore)
        {
            scope.EffortCap = DispatchPolicy.EffortCapFor(Models.SubAgentTier.Cheap);
            scope.SubAgentTier = "cheap";
            scope.TierRunModel = RunModel() is { Length: > 0 } rm ? rm : null;
        }
        AgentRunScope.Enter(scope);
        string? model = auto ? RoutedModel(kind)
            : DispatchPolicy.ManualModelFor(kind, RunModel() is { Length: > 0 } run ? run : null, _settingsService.Settings.SubAgentModelOverrides);
        SubAgentModel.Use(model);
        int n = Interlocked.Increment(ref _subAgentCounter);
        string warm = call.Attempt == 1 ? WarmContext() : "";
        var request = new SubAgentRequest(call.Prompt, "", edit ? "general" : "explore");
        string report = await RunHeldSubAgentAsync(provider, request, ct,
            new SubAgentRunOptions(n, call.Label, edit ? "general" : "explore", model, scope, warm, WorkflowId: call.WorkflowId)) ?? "";
        return new WorkflowAgentResult(report, scope.CostUsd);
    }
}
