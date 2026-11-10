using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// workflow and wrap_up (docs/agent-orchestration.md §2, §6). workflow is opt-in — offered only when the chat's Ultra
/// switch is on or the owner's own message asked for one (<see cref="WorkflowGate"/>) — and runs on
/// <see cref="WorkflowService"/>. wrap_up shows the next-step box at the end of a turn; it is always offered.
/// </summary>
public sealed class OrchestrationToolModule : IAgentToolModule
{
    public const string WorkflowToolName = "workflow";
    public const string WrapUpToolName = "wrap_up";
    public static readonly string[] ToolNames = { WorkflowToolName, WrapUpToolName };

    /// <summary>wrap_up was called in chat (key, may be null outside a run). The chat page shows the box when the turn ends.</summary>
    public static event Action<string?, NextStep>? NextStepRequested;

    public string Id => "orchestration";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.Workflow, ToolType.WrapUp };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => TypeOf(toolName);

    public static ToolType? TypeOf(string? toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "workflow" or "run_workflow" or "start_workflow" or "workflow_run" => ToolType.Workflow,
        "wrap_up" or "wrapup" or "next_step" or "end_turn_summary" => ToolType.WrapUp,
        _ => null,
    };

    private const string WorkflowSchemaJson = """
    {"type":"object","properties":{
      "name":{"type":"string","description":"Short name shown on the workflow card"},
      "concurrency":{"type":"integer","description":"Agents at once (default 3; the machine's own limit also holds)"},
      "phases":{"type":"array","description":"Run in order. Each phase has exactly one of: agents (parallel fan-out, optionally with verify = refute each agent's items as soon as it ends), pipeline (items pass through stages one by one, no barrier), or verify with from (refute an earlier phase's items).",
        "items":{"type":"object","properties":{
          "title":{"type":"string"},
          "agents":{"type":"array","items":{"type":"object","properties":{
             "label":{"type":"string"},"prompt":{"type":"string","description":"Self-contained task: the agent sees nothing else"},
             "type":{"type":"string","description":"explore (read-only, default) or general (may edit files)"},
             "isolation":{"type":"string","description":"worktree = this agent alone works in its own git worktree (overrides the phase)"},
             "schema":{"type":"object","description":"Optional JSON schema the answer must match, e.g. {\"type\":\"object\",\"properties\":{\"findings\":{\"type\":\"array\"}},\"required\":[\"findings\"]}"}},
             "required":["label","prompt"]}},
          "verify":{"type":"object","properties":{
             "from":{"type":"string","description":"A verify-only phase: the earlier phase's title"},
             "field":{"type":"string","description":"The array field holding the claims, e.g. findings"},
             "refuters":{"type":"integer","description":"Adversarial verifiers per claim (default 3); a claim more than half refute is dropped"},
             "prompt":{"type":"string","description":"What counts as wrong here (optional)"}}},
          "pipeline":{"type":"object","properties":{
             "from":{"type":"string"},"field":{"type":"string"},
             "items":{"type":"array","items":{"type":"string"}},
             "stages":{"type":"array","items":{"type":"object","properties":{
                "label":{"type":"string"},"prompt":{"type":"string","description":"May use {{item}} and {{prev}}"},
                "type":{"type":"string"},"schema":{"type":"object"}},"required":["label","prompt"]}}}}},
          "isolation":{"type":"string","description":"worktree = every agent of this agents phase edits in its own git worktree (seeded with the project's current state, uncommitted work included), so parallel agents never overwrite each other; each result carries its diff. Agents become type general. Needs the project to be a git repository."},
          "gate":{"type":"string","description":"With isolation: a build/test command the app itself runs in each agent's worktree after it ends (exit 0 = green), e.g. dotnet build. A red agent's diff is never merged. It runs in a fresh checkout: ignored folders (node_modules, bin, obj) are not there."},
          "merge":{"type":"object","description":"With isolation: when every agent of the phase has ended, apply the green diffs to the project one by one (plan order). After each, the gate runs on the merged tree; a diff that no longer applies, or turns it red, is left out and reported with its diff. Then test runs once on the merged result. Nothing is committed.",
             "properties":{"test":{"type":"string","description":"The full test command run once on the merged result (default: the gate)"}}},
          "required":["title"]}},
      "resume_id":{"type":"string","description":"Resume a stopped workflow by its id instead (finished agents are reused)"}},
     "required":[]}
    """;

    private const string WrapUpSchemaJson = """
    {"type":"object","properties":{
      "recap":{"type":"string","description":"Two or three short sentences (max 300 chars): what is now true or changed, then what is not done or not verified."},
      "ask":{"type":"string","description":"Optional: the ONE decision left to the user, one short sentence (max 100 chars), no options listed."},
      "actions":{"type":"array","items":{"type":"string"},"description":"1-2 button labels answering ask (1-3 words each, your recommendation first). A click sends the label as the user's message."}},
     "required":["recap"]}
    """;

    public IEnumerable<ToolSchema> GetToolSchemas() => Schemas();

    public static IEnumerable<ToolSchema> Schemas()
    {
        yield return new ToolSchema
        {
            Name = WorkflowToolName,
            Description = "Run a multi-agent workflow the app executes deterministically: phases of parallel sub-agents, pipelines with "
                + "no barrier, per-agent JSON answers, and adversarial verification (N refuters per claim, majority drops it). For "
                + "parallel code changes, give the implementing phase isolation \"worktree\" + gate (the build/test command) + "
                + "merge: each agent edits its own git worktree, the app re-runs the gate there itself, merges only green diffs, "
                + "reports conflicts and red merges, and runs the test command once on the merged result. Only "
                + "offered when the user opted in (the chat's Ultra switch, or they asked for a workflow / multi-agent work) because "
                + "it spends many tokens; for one sub-task use agent_spawn. The call waits for the workflow and returns each phase's "
                + "results; the user watches it on a workflow card and can stop or resume it.",
            InputSchema = JsonDocument.Parse(WorkflowSchemaJson).RootElement.Clone(),
        };
        yield return new ToolSchema
        {
            Name = WrapUpToolName,
            Description = "End a turn that did multi-step work with the next-step box under your reply: a 2-3 sentence recap (what is "
                + "now true, what is not done or verified) and, only when the next move is the user's call, ONE short question with "
                + "1-2 answer buttons (your pick first). A click sends the label to you as the user's message. Call it once, last, "
                + "then write your closing message. Skip it for a short answer or pure conversation.",
            InputSchema = JsonDocument.Parse(WrapUpSchemaJson).RootElement.Clone(),
        };
    }

    private static string? Chat => RootOf(AgentRunScope.Current)?.SessionKey;

    private static AgentRunScope? RootOf(AgentRunScope? s)
    {
        while (s?.Parent != null) s = s.Parent;
        return s;
    }

    public string? GetRefusal(ToolType type) => type == ToolType.Workflow ? WorkflowRefusal(Chat) : null;

    /// <summary>Why workflow may not run in <paramref name="chat"/> now, or null. Public for the harness and the bridge.</summary>
    public static string? WorkflowRefusal(string? chat)
    {
        if (!WorkflowGate.Allowed(chat))
            return "Workflows are off for this chat: they start many agents and spend many tokens, so the user opts in — the "
                 + "Ultra switch beside the chat's input, or asking for a workflow / multi-agent work in their message. Do the work "
                 + "with agent_spawn or yourself; you may tell the user they can switch Ultra on.";
        var svc = WorkflowService.Current;
        if (svc?.Runner == null) return "Workflows are not available in this build (no agent runner).";
        return svc.RunnerRefusal?.Invoke();
    }

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        if (call.Type == ToolType.WrapUp) return ToolPermission.Free("read", WrapUpToolName);
        string chat = Chat ?? context.SessionKey;
        string resume = call.GetArg("resume_id").Trim();
        string detail;
        List<string> commands = new();
        if (resume.Length > 0)
        {
            detail = $"Resume workflow {resume}: finished agents are reused, the rest run.";
            if (WorkflowService.Current?.Get(resume) is { } old && WorkflowPlan.Parse(old.PlanJson, out _) is { } oldPlan) commands = oldPlan.Commands();
        }
        else if (PlanOf(call, out _, out _) is { } p && WorkflowService.Current is { } svc)
        {
            var est = svc.EstimateFor(p);
            detail = $"Workflow \"{p.Name}\": up to {est.Agents} agents, {est.AtOnce} at once · ≈ ${est.Usd:0.00} · ≈ {est.RamMb / 1024.0:0.0} GB RAM while it runs.";
            if (p.UsesWorktrees) detail += " Agents edit in their own git worktrees; green diffs are merged into the project's working tree (not committed).";
            commands = p.Commands();
        }
        else detail = "Workflow (its plan has a problem — the call will say what).";
        // The engine runs these itself, outside the tool path a run_command would take: the owner sees them every time.
        if (commands.Count > 0)
            return new ToolPermission
            {
                Scope = "execute", Resource = WorkflowToolName, AlwaysAsk = true,
                Detail = detail + " The app itself runs these commands in the worktrees and the project: " + string.Join(" · ", commands.Select(c => "`" + c + "`")),
            };
        // Ultra on = the owner already said yes to workflows in this chat; the card still shows the estimate.
        if (WorkflowGate.IsUltra(chat)) return new ToolPermission { Scope = "execute", Resource = WorkflowToolName, IsFree = true, Detail = detail };
        return new ToolPermission { Scope = "execute", Resource = WorkflowToolName, AlwaysAsk = true, Detail = detail };
    }

    /// <summary>The plan from the call: <c>plan</c> (an object or JSON text) or the top-level name / concurrency / phases.</summary>
    public static WorkflowPlan? PlanOf(ToolCall call, out string json, out string? error)
    {
        json = call.GetArg("plan").Trim();
        if (json.Length == 0)
        {
            var obj = new JsonObject();
            if (call.GetArg("name") is { Length: > 0 } n) obj["name"] = n;
            if (int.TryParse(call.GetArg("concurrency"), out int c)) obj["concurrency"] = c;
            string phases = call.GetArg("phases").Trim();
            if (phases.Length == 0) { error = "Give phases (or resume_id)."; return null; }
            try { obj["phases"] = JsonNode.Parse(phases); }
            catch (JsonException ex) { error = "phases is not valid JSON: " + ex.Message; return null; }
            json = obj.ToJsonString();
        }
        return WorkflowPlan.Parse(json, out error);
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        ToolResult Fail(string msg) => new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };
        if (call.Type == ToolType.WrapUp)
        {
            var step = NextStep.FromArgsRepaired(call.GetArg("recap"), call.GetArg("ask"), call.GetArg("actions"), out var err, out var repaired);
            if (step == null) return Fail(err!);
            NextStepRequested?.Invoke(Chat, step);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = (repaired != null ? repaired + " Nothing else is needed: do not redo any work. " : "")
                    + "The next-step box will show under your closing message. Now write that message (your answer in full) and end the turn; do not repeat the box's question in prose.",
                Summary = "⏭ " + (step.Ask ?? step.Recap),
            };
        }

        string? chat = Chat ?? context.SessionKey;
        if (WorkflowRefusal(chat) is { } refusal) return Fail(refusal);
        var svc = WorkflowService.Current!;
        WorkflowRunState? st;
        string resume = call.GetArg("resume_id").Trim();
        if (resume.Length > 0)
        {
            if (svc.ResumeRefusal(resume) is { } no) return Fail(no);
            // Another chat's workflow is that chat's (its owner opted in there, its card is there).
            if (svc.Get(resume) is { } other && !string.IsNullOrEmpty(other.SessionKey) && other.SessionKey != chat)
                return Fail($"Workflow {resume} belongs to another chat; it can be resumed from its card there.");
            st = await svc.ResumeAsync(resume, ct);
            if (st == null) return Fail($"Workflow {resume} could not be resumed (it started running elsewhere).");
        }
        else
        {
            var plan = PlanOf(call, out string json, out var error);
            if (plan == null) return Fail(error!);
            string? folder = RootOf(AgentRunScope.Current)?.WorkingDirectory ?? context.WorkingDirectory;
            if (await WorkflowService.FolderRefusalAsync(plan, folder, ct) is { } noFolder) return Fail(noFolder);
            var est = svc.EstimateFor(plan);
            if (svc.BudgetLeft() is { } left && est.Usd > left)
                return Fail($"This workflow is estimated at ≈ ${est.Usd:0.00} ({est.Agents} agents) but the run has ${left:0.00} of its budget left. "
                            + "Plan fewer agents, items or refuters, or ask the user to raise the budget.");
            st = await svc.RunAsync(plan, json, chat ?? "", ct, folder);
        }
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = st.Status is "done" or "stopped",
            Output = WorkflowService.Report(st),
            Summary = "🧩 " + (st.Summary ?? st.Name),
        };
    }

    /// <summary>How to keep the plan card, in the system prompt (fixed text: the cached prompt never changes with it).</summary>
    public const string PlanGuide =
        "PLAN: for every request that edits files or runs more than one command, your FIRST tool call is todo_write with the "
        + "whole plan in `todos` — short imperative steps (about 7 words each), the step you are starting in_progress, the rest "
        + "pending — and `scope`: first_task (nothing finished in this chat yet), continuation (builds on this chat's work, "
        + "including a follow-up or acting on what you just found; when unsure, this one) or new_task (unrelated to anything "
        + "done here). Send it in the same response as that first real tool call, not alone. Then `advance: true` alongside "
        + "the call that begins each next step (`done` + `start` when out of order); keep exactly one step in_progress. When "
        + "the plan changes (also right after the user interrupts with a new instruction), send the whole new list again: it "
        + "replaces the old one. Do not write the plan as a numbered list in your reply — the card is the plan. A one-step "
        + "answer or pure conversation needs no plan.";

    /// <summary>The same rule for a local model (small context, every token counts — the 2026-11-06 Sudoku run overflowed):
    /// about a fifth of <see cref="PlanGuide"/>, and only what it must do.</summary>
    public const string PlanGuideShort =
        "PLAN: a task with several steps → first call todo_write {todos:[{content,status}], scope} (scope: first_task / "
        + "continuation / new_task), one step in_progress; then todo_write {advance:true} as each step finishes.";

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    public string? GetPromptSection(PromptSectionContext context)
    {
        bool wf = context.OfferedToolNames.Contains(WorkflowToolName);
        string s = "";
        // The plan card (Claude Code's update_plan): the user follows the work through it, so it comes first, not by step 3.
        if (context.OfferedToolNames.Contains("todo_write") || context.OfferedToolNames.Contains("update_plan"))
            s = (context.IsLocalModel ? PlanGuideShort : PlanGuide) + "\n";
        s += "At the end of a turn that did multi-step work, call wrap_up once (recap, and at most one question with 1-2 "
           + "buttons when the next move is the user's; it also checks off the plan's remaining steps), then write your closing message.";
        if (!context.NativeTools)
            s += "\n[ACTION: wrap_up]\nrecap: Fixed the timeout in client.cs; the build passes. Not run against the live API.\n"
               + "ask: Run the live test now?\nactions: [\"Yes, run it\", \"Not now\"]\n[/ACTION]";
        if (wf)
            s += "\n" + WorkflowGate.SplitGuide(cli: false)
               + " When several agents change code at once, give that phase isolation \"worktree\", a gate (the build/test "
               + "command) and merge, so the app checks each diff itself and merges only what stays green.";
        return s;
    }
}
