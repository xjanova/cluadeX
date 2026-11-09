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
        "find_references", "impact_of", "git_blame", "git_show", "gh_pr_view", "gh_pr_checks", "gh_run_log",
        "checkpoint_list", "checkpoint_diff",
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

    /// <summary>What a sub-agent is doing, for the agent map: Phase "start" | "tool" | "result" | "done" | "failed" |
    /// "stopped". A "tool" with a <paramref name="CallId"/> is answered by a "result" with the same id, its
    /// <paramref name="Result"/> (trimmed) and <paramref name="Ok"/> — the agent's floating window lists both.
    /// Raised in the run's own flow (AgentRunScope.Current is the run that spawned it).
    /// <paramref name="Tool"/>: the tool a "tool" phase runs (its avatar shows it). <paramref name="Tier"/>: "cheap" / "mid" /
    /// "deep" when auto model chose it. <paramref name="Color"/>: a specialist's accent (its file's color).</summary>
    public sealed record SubAgentEvent(int Id, string Label, string Kind, string Task, string Phase, string Detail, int Steps,
        string? Model = null, decimal CostUsd = 0, string? WorkflowId = null, string? CallId = null, string? Result = null, bool Ok = true,
        Agents.SubAgentControl? Control = null, string? Tool = null, string? Tier = null, string? Color = null, string? Provider = null);

    /// <summary>A tool result as the agent window shows it: at most this many characters.</summary>
    internal const int SubAgentEventResultChars = 2000;

    internal static string ResultForMap(string? text)
    {
        text = (text ?? "").Trim();
        return text.Length <= SubAgentEventResultChars ? text : text[..SubAgentEventResultChars] + "…";
    }

    /// <summary>How one sub-agent runs when the dispatcher starts it: its map id and label, kind, routed model, own scope
    /// (its cost), its warm-start context; ReportOnDone = false for a fixer, whose "done" comes after the re-run.
    /// Tier: its tier when auto model chose one (shown on its node). Provider: another provider than the run's (a local
    /// model for a cheap one). MaxSteps: its step budget (0 = <see cref="SubAgentMaxSteps"/>). Upgrade: the model and
    /// provider its window's Upgrade moves it to.</summary>
    private sealed record SubAgentRunOptions(int Id, string Label, string Kind, string? Model, AgentRunScope? Scope,
        string? Warm = null, bool ReportOnDone = true, SubAgentThread? Thread = null, string? WorkflowId = null,
        Agents.SubAgentControl? Control = null, SubAgentTier? Tier = null, string? Color = null,
        Providers.IAiProvider? Provider = null, int MaxSteps = 0, Func<(string? Model, Providers.IAiProvider Provider)>? Upgrade = null);

    /// <summary>A sub-agent's conversation, so agent_spawn's continue_agent carries on with it. Finished: it ended with a
    /// report, so the conversation can take another message; one that failed mid-tool cannot.</summary>
    private sealed class SubAgentThread
    {
        public required SubAgentRequest Request { get; init; }
        public required int Id { get; init; }
        public List<NativeMessage> Messages { get; } = new();
        public bool Finished;
        public int Busy;
        /// <summary>What it ran on last — a carried-on conversation stays there (its history belongs to that model).</summary>
        public SubAgentTier Tier = SubAgentTier.Deep;
        public string? Model;
        public Providers.IAiProvider? Provider;
    }

    private Agents.TierHistory? _tierHistory;
    /// <summary>How often each kind climbed from each tier (subagent-tiers.json in the data folder).</summary>
    private Agents.TierHistory TierStats => LazyInitializer.EnsureInitialized(ref _tierHistory,
        () => new Agents.TierHistory(SafeDataPath("subagent-tiers.json")));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _tierBumps = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A kind started one tier up because of its history is tried at its own tier every this many runs.</summary>
    private const int ReprobeEvery = 5;

    private string? SafeDataPath(string file)
    {
        try { return string.IsNullOrWhiteSpace(_settingsService.DataRoot) ? null : System.IO.Path.Combine(_settingsService.DataRoot, file); }
        catch { return null; }
    }

    /// <summary>The tool calls an attempt made, as "read_file(src/A.cs)" — what the next tier gets told it already ran.</summary>
    private static List<string> ToolCallsOf(IEnumerable<NativeMessage> messages)
    {
        var list = new List<string>();
        foreach (var m in messages)
            if (m.Role == "assistant")
                foreach (var b in m.Content)
                    if (b.Type == "tool_use" && !string.IsNullOrEmpty(b.Name))
                        list.Add(b.Name + "(" + Shorten(FirstStringArg(b.Input), 100) + ")");
        return list;
    }

    private static string FirstStringArg(System.Text.Json.JsonElement? input)
    {
        if (input is not { ValueKind: System.Text.Json.JsonValueKind.Object } o) return "";
        foreach (var key in new[] { "path", "file_path", "pattern", "query", "command", "url" })
            if (o.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() ?? "";
        foreach (var p in o.EnumerateObject())
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String) return p.Value.GetString() ?? "";
        return "";
    }

    private static string TierWord(SubAgentTier t, bool thai) => t switch
    {
        SubAgentTier.Cheap => thai ? "ประหยัด" : "cheap",
        SubAgentTier.Mid => thai ? "กลาง" : "mid",
        _ => thai ? "เต็มกำลัง" : "deep",
    };

    /// <summary>The local model a read-only sub-agent may run on (Settings ▸ auto model ▸ local, or the Colibri card's
    /// switch): one already up that can run a tool loop — llama-server, Ollama, the built-in engine, or a server on this PC
    /// behind a compatible preset (Colibri, LM Studio, vLLM). Cheap tier on a 7B+ model, mid tier on a 30B+ one
    /// (<see cref="Agents.DispatchPolicy.UseLocalFor"/>). <paramref name="tried"/> holds the local providers this
    /// sub-agent already ran on: a weak report climbs AWAY from them, never back. Null = none (it goes to the API).</summary>
    /// <summary>A local model a sub-agent may run on, and its place on that server (<see cref="Agents.LocalSlots"/>) —
    /// dispose it when the attempt ends.</summary>
    private sealed record LocalPick(Providers.IAiProvider Provider, IDisposable Lease);

    /// <summary>What a local server says about its model, for the tier rule: its size in billions, whether it chats, and
    /// how many requests it answers at once (0 = no limit known). Colibri: from the plugin's catalog — its server's id
    /// rarely carries the size, and an image model (qwen-image-2.1) has no tool loop. Others: the model's name.</summary>
    private (double? Billions, bool Chat, int Slots) LocalModelFacts(AiProviderType t, string name)
    {
        if (t == AiProviderType.Colibri && Colibri.ColibriService.ModelFacts(_settingsService.DataRoot) is { } f)
            return (f.Billions ?? Agents.ModelCapability.ParseBillions(name), f.Chat, f.Slots);
        return (Agents.ModelCapability.ParseBillions(name), true, 0);
    }

    private async Task<LocalPick?> LocalSubAgentProviderAsync(SubAgentTier tier, bool canEdit, IReadOnlySet<string> tried, CancellationToken ct)
    {
        var s = _settingsService.Settings;
        if (!s.SubAgentLocalCheap || tier == SubAgentTier.Deep || canEdit) return null;
        bool runIsLocal = IsLocalType(_providerManager.ActiveProviderType);
        foreach (var t in LocalSubAgentTypes)
        {
            var p = _providerManager.GetProvider(t);
            if (p == null || ReferenceEquals(p, _providerManager.ActiveProvider) || p.ProviderId != t.ToString() || tried.Contains(LocalKeyOf(p))) continue;
            bool ready, tools;
            try
            {
                ready = p.IsReady;
                // A server on this PC behind a compatible preset is "ready" only once something initialised it: ask it
                // (its /models, a local call) — at most once a minute after it said no, so a server that is off costs nothing.
                if (!ready && Providers.CompatiblePresets.For(t) is { Local: true } && ProbeDue(t))
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probe.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await p.InitializeAsync(probe.Token); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                    ready = p.IsReady;
                    if (!ready) _localProbeFailed[t] = DateTime.UtcNow;
                }
                tools = p.SupportsNativeToolUse;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { continue; }
            var (billions, chat, slots) = LocalModelFacts(t, LocalModelNameFor(p));
            if (!chat) continue;   // a picture model: no tool loop to run
            var size = Agents.ModelCapability.ForBillions(billions).Tier;
            if (!Agents.DispatchPolicy.UseLocalFor(true, tier, canEdit, runIsLocal, ready, tools, size, billions)) continue;
            // Only while the server has a free slot: past it the request would queue (colibri: 429 after 300 s), and the
            // API answers now — the local model is for work that can wait for a free place, never one that waits in line.
            string key = LocalKeyOf(p);
            if (Agents.LocalSlots.TryTake(key, slots) is { } lease) return new LocalPick(p, lease);
            _debugLog?.Info("Dispatch", $"local {t}: all {slots} slot(s) busy — this sub-agent goes to the API");
        }
        return null;
    }

    /// <summary>Where a sub-agent looks for a local model, in order: the ones CluadeX loads itself, then servers on this PC
    /// — Colibri first among those (it is installed for exactly this).</summary>
    private static readonly AiProviderType[] LocalSubAgentTypes =
    {
        AiProviderType.LlamaServer, AiProviderType.Ollama, AiProviderType.Local,
        AiProviderType.Colibri, AiProviderType.LMStudio, AiProviderType.Vllm,
    };

    /// <summary>Which local model a sub-agent ran on, for "never back to it": a server behind a compatible preset is its
    /// address (vLLM and colibri both default to port 8000 — two presets, one server), anything else its provider id.</summary>
    private string LocalKeyOf(Providers.IAiProvider p)
    {
        if (Enum.TryParse<AiProviderType>(p.ProviderId, out var t) && Providers.CompatiblePresets.For(t) is { Local: true } preset)
        {
            string url = _settingsService.Settings.ProviderConfigs.TryGetValue(p.ProviderId, out var cfg) && !string.IsNullOrWhiteSpace(cfg.BaseUrl)
                ? cfg.BaseUrl : preset.BaseUrl;
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
                return (u.IsLoopback ? "loopback" : u.Host.ToLowerInvariant()) + ":" + u.Port;
        }
        return p.ProviderId;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<AiProviderType, DateTime> _localProbeFailed = new();
    private bool ProbeDue(AiProviderType t) => !_localProbeFailed.TryGetValue(t, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1);

    /// <summary>A run's sub-agent conversations: they end with the run.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentRunScope, System.Collections.Concurrent.ConcurrentDictionary<int, SubAgentThread>> _subAgentThreads = new();

    public event Action<SubAgentEvent>? OnSubAgentEvent;

    private void RaiseSubAgent(SubAgentEvent e)
    {
        // The plan's "N running" line counts from these same events (raised in the spawning run's flow).
        if (e.WorkflowId == null)
        {
            string? chat = RootOf(AgentRunScope.Current)?.SessionKey;
            if (e.Phase == "start") Agents.RunActivity.SubAgentStarted(chat, "sa-" + e.Id);
            else if (e.Phase is "done" or "failed" or "stopped") Agents.RunActivity.SubAgentEnded(chat, "sa-" + e.Id);
        }
        try { OnSubAgentEvent?.Invoke(e); } catch (Exception ex) { _debugLog?.Warn("SubAgent", "agent map update failed", ex); }
    }

    /// <summary>Run one sub-agent to the end and return its report; null when the active model cannot run one (no
    /// native tool use — the [ACTION:] protocol), so the caller falls back to handing the task to the main agent.</summary>
    private async Task<string?> RunSubAgentAsync(SubAgentRequest request, CancellationToken ct)
    {
        _subAgentLast.Value = null;
        if (string.Equals(request.Type, "workers", StringComparison.OrdinalIgnoreCase))
            return await RunWorkersAsync(request, ct);

        var parent = AgentRunScope.Current;
        SubAgentThread? earlier = null;
        if (request.ContinueId is int carryOn)
        {
            if (parent == null || !_subAgentThreads.GetOrCreateValue(parent).TryGetValue(carryOn, out earlier))
                return $"There is no sub-agent {carryOn} in this run to carry on with. Start a new one.";
            if (!earlier.Finished)
                return $"Sub-agent {carryOn} did not end with a report, so it cannot take another message. Start a new one.";
            if (Interlocked.CompareExchange(ref earlier.Busy, 1, 0) != 0)
                return $"Sub-agent {carryOn} is already working on a message.";
            request = earlier.Request with { Task = request.Task, Context = request.Context, ContinueId = carryOn };
        }
        try { return await RunRoutedSubAgentAsync(request, parent, earlier, ct); }
        finally { if (earlier != null) Volatile.Write(ref earlier.Busy, 0); }
    }

    private async Task<string?> RunRoutedSubAgentAsync(SubAgentRequest request, AgentRunScope? parent, SubAgentThread? earlier, CancellationToken ct)
    {
        // ─── the dispatch policy: may this one run, on which model, how many at once ───
        var dispatch = CurrentDispatch();
        bool canEdit = SubAgentCanEdit(request);
        if (Agents.DispatchPolicy.Gate(dispatch, request.Type, canEdit) is { } refusal)
        {
            _debugLog?.Info("Dispatch", $"sub-agent refused ({request.Type}, edit={canEdit}) · {dispatch.Reason}");
            return refusal;
        }
        var gate = GateFor(parent, dispatch.MaxConcurrency);
        await gate.WaitAsync(ct);
        try
        {
            var s = _settingsService.Settings;
            var kind = Agents.DispatchPolicy.KindOf(canEdit);
            // Tier routing (Settings ▸ Agent ▸ auto model): the model and effort the work needs. A carried-on conversation
            // keeps the tier it ended on — its history belongs to that model.
            bool auto = !dispatch.Legacy && s.SubAgentAutoModel;
            var tier = earlier?.Tier ?? (auto
                ? Agents.DispatchPolicy.TierFor(kind, Agents.DispatchPolicy.ParseTier(request.Tier), dispatch.BudgetCapped)
                : SubAgentTier.Deep);
            // A kind that kept climbing from this tier starts one up (doing it twice costs more than once on the stronger
            // model); now and then it is tried at its own tier again, so a better cheap model can win the kind back.
            var proposedTier = tier;
            if (auto && earlier == null)
            {
                var (runs, climbed) = TierStats.Of(request.Type, tier);
                var start = Agents.DispatchPolicy.StartTier(tier, runs, climbed, dispatch.BudgetCapped);
                if (start != tier && _tierBumps.AddOrUpdate(Agents.TierHistory.KeyOf(request.Type, tier), 1, (_, v) => v + 1) % ReprobeEvery != 0)
                {
                    _debugLog?.Info("Dispatch", $"{request.Type}: climbed {climbed} of the last {runs} times from {tier} — starting at {start}");
                    tier = start;
                }
            }
            var startTier = tier;
            if (!dispatch.Legacy) parent?.MarkStrategy((int)Agents.DispatchStrategy.FanOut);
            string warm = dispatch.Legacy || earlier != null ? "" : WarmContext();
            if (!dispatch.Legacy && !canEdit && QuotaRecentlyExhausted && s.AutoFailoverOnQuota)
            {
                AgentRunScope.Enter(parent?.CreateChild() ?? parent);
                return await RunSubAgentOnClaudeDevAsync(BriefOf(request, warm), false, ct);
            }
            bool thai = _localizationService.CurrentLanguage == "th";
            bool specialist = !string.IsNullOrWhiteSpace(request.Persona);
            string? runModel = RunModel() is { Length: > 0 } rm ? rm : null;

            var triedLocal = new HashSet<string>(StringComparer.Ordinal);
            for (int climbs = 0; ; climbs++)
            {
                // ─── this attempt: its model, provider, effort ceiling, step budget ───
                string? model;
                Providers.IAiProvider? onProvider = null;
                IDisposable? localLease = null;
                if (earlier?.Provider is { } kept) { onProvider = kept; model = earlier.Model; }
                else if (dispatch.Legacy) model = null;
                else if (!auto) model = Agents.DispatchPolicy.ManualModelFor(kind, runModel, s.SubAgentModelOverrides);
                else if (await LocalSubAgentProviderAsync(tier, canEdit, triedLocal, ct) is { } local)
                {
                    onProvider = local.Provider;
                    localLease = local.Lease;
                    model = LocalModelNameFor(local.Provider) is { Length: > 0 } ln ? System.IO.Path.GetFileName(ln) : null;
                }
                else model = Agents.DispatchPolicy.ModelForTier(tier, kind, _providerManager.ActiveProviderType, runModel,
                    request.Model, s.SubAgentModelOverrides, dispatch.BudgetCapped);

                var child = parent?.CreateChild();
                if (child != null && auto)
                {
                    child.EffortCap = Agents.DispatchPolicy.EffortCapFor(tier);
                    child.SubAgentTier = tier.ToString().ToLowerInvariant();
                    child.TierRunModel = runModel;
                }
                AgentRunScope.Enter(child ?? parent);
                // A local model reads its own configuration: no API model override goes with it.
                Agents.SubAgentModel.Use(onProvider != null ? null : model);

                int n = climbs == 0 && earlier != null ? earlier.Id : Interlocked.Increment(ref _subAgentCounter);
                string label = (thai ? $"เอเจนต์ย่อย #{n}" : $"Sub-agent #{n}") + (specialist ? $" ({request.Type})" : "")
                               + (climbs > 0 ? $" ↑{TierWord(tier, thai)}" : "");
                var thread = climbs == 0 && earlier != null ? earlier : new SubAgentThread { Request = request, Id = n };
                thread.Tier = tier; thread.Model = model; thread.Provider = onProvider;
                if (parent != null) _subAgentThreads.GetOrCreateValue(parent)[n] = thread;
                if (climbs > 0) _debugLog?.Info("Dispatch", $"sub-agent climbs to {tier} ({model ?? "run model"}) after a weak report");
                _debugLog?.Info("Dispatch", $"sub-agent #{n} · {(auto ? "tier " + tier : "auto model off")} · {model ?? "run model"}"
                    + (onProvider != null ? " (local)" : "") + $" · effort cap {child?.EffortCap ?? "none"}");

                // Its own stop (the owner's, from its window) and message queue: linked to the run's token, never the reverse.
                using var control = new Agents.SubAgentControl(ct, canEdit) { CanUpgrade = auto && tier != SubAgentTier.Deep };
                int maxSteps = auto ? Agents.DispatchPolicy.MaxStepsFor(tier, SubAgentMaxSteps) : SubAgentMaxSteps;
                Func<(string? Model, Providers.IAiProvider Provider)>? upgrade = auto
                    ? () => (Agents.DispatchPolicy.ModelForTier(SubAgentTier.Deep, kind, _providerManager.ActiveProviderType, runModel,
                                 request.Model, s.SubAgentModelOverrides), _providerManager.ActiveProvider)
                    : null;
                string? report;
                try
                {
                    report = await RunSubAgentCoreAsync(request, control.Token,
                        new SubAgentRunOptions(n, label, request.Type, model, child, warm, Thread: thread, Control: control,
                            Tier: auto ? tier : null, Color: request.Color, Provider: onProvider, MaxSteps: maxSteps, Upgrade: upgrade));
                }
                finally { localLease?.Dispose(); }

                // ─── a weak report climbs one tier (cheap → mid → deep), in a fresh conversation ───
                var now = CurrentDispatch();
                if (report != null && Agents.DispatchPolicy.ShouldEscalate(thread.Tier, report, climbs, auto && s.SubAgentEscalate,
                        control.StopRequested, now.BudgetCapped || !now.SubAgents, earlier != null))
                {
                    _costTrackingService?.RecordTierClimb(thread.Tier.ToString().ToLowerInvariant(), child);
                    // The local model gave the weak report: the next try is on the API, never on it again.
                    if (thread.Provider is { } ranOn) triedLocal.Add(LocalKeyOf(ranOn));
                    tier = Agents.DispatchPolicy.NextTier(thread.Tier);
                    OnAgentStatus?.Invoke(thai ? $"{label}: รายงานยังไม่พอ — ให้โมเดล{TierWord(tier, thai)}ลองต่อ"
                                               : $"{label}: the report fell short — trying again on the {TierWord(tier, thai)} tier");
                    // What the weak attempt already did goes along, so the stronger one does not read it all again.
                    string carry = Agents.DispatchPolicy.CarryOver(report, ToolCallsOf(thread.Messages));
                    if (carry.Length > 0)
                        request = request with { Context = string.IsNullOrWhiteSpace(request.Context) ? carry : request.Context.TrimEnd() + "\n\n" + carry };
                    continue;
                }
                if (auto && earlier == null && proposedTier != SubAgentTier.Deep)
                    TierStats.Record(request.Type, startTier, climbs > 0);
                return report == null || parent == null || !thread.Finished ? report : $"{report}\n\n(Sub-agent {n}: agent_spawn with continue_agent {n} carries on with it.)";
            }
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
        // A cheap one may run on the local model already loaded (SubAgentRunOptions.Provider); otherwise the run's.
        var provider = opts?.Provider ?? _providerManager.ActiveProvider;
        if (provider == null || !provider.SupportsNativeToolUse || !provider.IsReady)
        {
            if (opts?.Provider == null) return null;
            provider = _providerManager.ActiveProvider;   // the local one went away since it was picked: the run's model
            if (provider == null || !provider.SupportsNativeToolUse || !provider.IsReady) return null;
            opts = opts! with { Provider = null };
        }

        // A slot on this machine, across every chat. Only sub-agents queue here: the main loops keep moving.
        await Agents.AgentLoad.EnterAsync(ct);
        try { return await RunHeldSubAgentAsync(provider, request, ct, opts); }
        finally { Agents.AgentLoad.Leave(); }
    }

    /// <summary>One sub-agent's loop. With a <see cref="Agents.SubAgentControl"/>: the owner's Stop ends it with an ordinary
    /// result for the main agent (not a cancel of the run), and a message it never read is reported as such.</summary>
    private async Task<string?> RunHeldSubAgentAsync(Providers.IAiProvider provider, SubAgentRequest request, CancellationToken ct, SubAgentRunOptions? opts)
    {
        var control = opts?.Control;
        if (control == null) return await RunHeldSubAgentLoopAsync(provider, request, ct, opts);
        bool thai = _localizationService.CurrentLanguage == "th";
        try { return await RunHeldSubAgentLoopAsync(provider, request, ct, opts); }
        catch (OperationCanceledException) when (control.StopRequested && !control.ParentCancelled)
        {
            _debugLog?.Info("SubAgent", $"{opts!.Label}: stopped by the owner");
            ReportUnreadNotes(control, thai);
            if (control.StartEvent is SubAgentEvent ev) RaiseSubAgent(ev with { Phase = "stopped", Detail = "", CostUsd = opts.Scope?.CostUsd ?? 0 });
            if (opts.Thread != null) opts.Thread.Finished = false;
            return $"[Sub-agent {opts.Id} was stopped by the user before it finished; it wrote no report. Do not start it again "
                   + "unless the user asks — carry on with what you have.]";
        }
        // The run stopped, or the agent crashed: its node ends here. (RunRoutedSubAgentAsync's catch cannot do it — the
        // _subAgentLast this loop sets is an AsyncLocal, and a callee's change never flows back to its caller.)
        catch (OperationCanceledException)
        {
            ReportUnreadNotes(control, thai);
            if (control.StartEvent is SubAgentEvent ev) RaiseSubAgent(ev with { Phase = "stopped", Detail = "", CostUsd = opts!.Scope?.CostUsd ?? 0 });
            throw;
        }
        catch (Exception ex)
        {
            ReportUnreadNotes(control, thai);
            if (control.StartEvent is SubAgentEvent ev) RaiseSubAgent(ev with { Phase = "failed", Detail = Shorten(ex.Message, 120), CostUsd = opts!.Scope?.CostUsd ?? 0 });
            throw;
        }
        finally { ReportUnreadNotes(control, thai); }
    }

    /// <summary>The agent ended: no more stops or messages, and each message it never read says so in its window. Called
    /// before its done / stopped / failed event — the map closes a step still open at that event, so a later answer
    /// would land on nothing. Once is enough: Close hands the queue over only the first time.</summary>
    private void ReportUnreadNotes(Agents.SubAgentControl control, bool thai)
    {
        foreach (var (id, _) in control.Close())
            if (control.StartEvent is SubAgentEvent ev)
                RaiseSubAgent(ev with { Phase = "result", Detail = "", CallId = id, Ok = false,
                    Result = thai ? "ไม่ได้อ่าน — เอเจนต์จบก่อน" : "not read — the agent ended first" });
    }

    private async Task<string?> RunHeldSubAgentLoopAsync(Providers.IAiProvider provider, SubAgentRequest request, CancellationToken ct, SubAgentRunOptions? opts)
    {
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
        // A worker, fixer or isolated workflow agent works in its own worktree: that is its project folder, not the run's.
        string folder = opts?.Scope?.WorkingDirectory ?? _fileSystemService.WorkingDirectory ?? "";
        string system = SubAgentSystemPrompt(specialist ? (canEdit ? "general" : "explore") : type, folder, thai);
        if (specialist)
            system += $"\n\n## Your role: {type}\n{request.Persona!.Trim()}";
        // A sub-agent that may edit writes code too: the user's AI-comment setting (Code Editor toolbar) applies to it.
        string comments = canEdit
            ? CodeCommentPolicy.InstructionFor(_settingsService.Settings, _fileSystemService.WorkingDirectory) : "";
        if (comments.Length > 0) system += "\n\n" + comments;

        var first = new StringBuilder(task.Trim());
        if (!string.IsNullOrWhiteSpace(context)) first.Append("\n\nContext from the main agent:\n").Append(context.Trim());
        // Warm start: the cached repo map and the run's brain recall, once — not a re-read of the project. A fixer's or
        // worker's prompt already carries its own (Warm is the prompt itself then).
        if (!string.IsNullOrWhiteSpace(opts?.Warm) && opts!.Warm != task) first.Append("\n\n").Append(opts.Warm!.Trim());
        var messages = opts?.Thread?.Messages ?? new List<NativeMessage>();
        if (opts?.Thread != null) opts.Thread.Finished = false;
        messages.Add(new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "text", Text = first.ToString() } } });

        OnAgentStatus?.Invoke($"{label}: {(thai ? "เริ่มงาน" : "starting")} · {Shorten(task, 60)}");
        var ev = new SubAgentEvent(n, label, opts?.Kind ?? type, Shorten(task, 160), "start", "", 0, opts?.Model, WorkflowId: opts?.WorkflowId,
            Control: opts?.Control, Tier: opts?.Tier?.ToString().ToLowerInvariant(), Color: opts?.Color, Provider: provider.ProviderId);
        if (opts?.Control != null) opts.Control.StartEvent = ev with { Control = null };
        decimal Cost() => opts?.Scope?.CostUsd ?? 0;
        _subAgentLast.Value = ev;
        RaiseSubAgent(ev);
        _debugLog?.Info("SubAgent", $"{label} ({type}) start: {Shorten(task, 120)} · {tools.Count} tools");

        int timeoutSec = _settingsService.Settings.InteractiveRequestTimeoutSeconds;
        int maxSteps = opts?.MaxSteps is > 0 and var ms ? ms : SubAgentMaxSteps;
        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            // The owner pressed Upgrade in its window: from this step on, the run's own model, effort and step budget.
            // Its conversation carries over (this one agent's small cache is written once on the new model).
            if (opts?.Control?.TakeUpgrade() == true && opts.Upgrade != null)
            {
                var (upModel, upProvider) = opts.Upgrade();
                if (upProvider is { SupportsNativeToolUse: true, IsReady: true })
                {
                    provider = upProvider;
                    Agents.SubAgentModel.Use(upModel);
                    if (opts.Scope != null) { opts.Scope.EffortCap = null; opts.Scope.SubAgentTier = "deep"; }
                    if (opts.Thread != null) { opts.Thread.Tier = SubAgentTier.Deep; opts.Thread.Model = upModel; opts.Thread.Provider = null; }
                    // thinking blocks are signed by the model that wrote them: another model refuses them
                    foreach (var m in messages) m.Content.RemoveAll(b => b.Type is "thinking" or "redacted_thinking");
                    maxSteps = Math.Max(maxSteps, SubAgentMaxSteps);
                    ev = ev with { Model = upModel, Tier = "deep" };
                    if (opts.Control != null) opts.Control.StartEvent = ev with { Control = null };
                    _subAgentLast.Value = ev;
                    _debugLog?.Info("SubAgent", $"{label}: upgraded by the owner → {upModel ?? "run model"}");
                    RaiseSubAgent(ev with { Phase = "tool", Detail = thai ? $"↑ อัปเกรดเป็น {upModel ?? "โมเดลหลัก"}" : $"↑ upgraded to {upModel ?? "the run's model"}",
                        Steps = step, CostUsd = Cost() });
                }
            }
            // What the owner typed into its window since the last step: read now, and its row says so.
            if (opts?.Control is { PendingNotes: > 0 } ctl)
            {
                var notes = ctl.TakeNotes();
                messages[^1].Content.Add(new ContentBlock { Type = "text", Text = Agents.SubAgentControl.NoteBlock(notes) });
                foreach (var (id, _) in notes)
                    RaiseSubAgent(ev with { Phase = "result", Detail = "", CallId = id, Ok = true, Steps = step, CostUsd = Cost(),
                        Result = thai ? $"อ่านแล้วที่ขั้น {step}" : $"read at step {step}" });
            }
            // The run's spend guard holds for its sub-agents too: past the budget, report now instead of spending on.
            bool overBudget = step > 1 && opts != null && RunBudgetSpent();
            bool lastStep = step == maxSteps || overBudget;
            if (lastStep)
                messages[^1].Content.Add(new ContentBlock { Type = "text", Text = overBudget
                    ? "[The run's spend budget is used: no more tools. Write your report now with what you have.]"
                    : "[Step budget reached: no more tools. Write your report now.]" });

            NativeToolResponse response;
            for (int retry = 0; ; retry++)
            using (var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Idle timeout, reset by every stream event (the same keep-alive the main loop uses).
                if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    response = await provider.ChatWithToolsAsync(messages, system, tools,
                        onTextDelta: _ => { if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec)); },
                        ct: callCts.Token, toolChoice: lastStep ? "none" : null);
                    break;
                }
                // A full server (colibri's 429 queue_timeout, a busy llama-server) or a passing rate limit: ask this step
                // again after 2 / 5 / 10 s (or the wait it asked for). A local server still full after that hands the
                // agent to the run's own model, conversation and all, instead of failing it.
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested
                                           && Agents.ProviderErrors.Classify(ex) is Agents.ProviderErrorKind.RateLimited or Agents.ProviderErrorKind.Overloaded
                                           && !IsQuotaExhausted(ex.Message))
                {
                    var next = SubAgentRetry(retry, opts?.Provider != null && ReferenceEquals(provider, opts.Provider),
                        _providerManager.ActiveProvider is { SupportsNativeToolUse: true, IsReady: true } ap && !ReferenceEquals(ap, provider));
                    if (next == SubAgentRetryAction.Fail) throw;
                    if (next == SubAgentRetryAction.ToRunModel)
                    {
                        provider = _providerManager.ActiveProvider!;
                        foreach (var m in messages) m.Content.RemoveAll(b => b.Type is "thinking" or "redacted_thinking");
                        string? apiModel = Agents.DispatchPolicy.ModelForTier(SubAgentTier.Cheap, Agents.SubAgentKind.Explore,
                            _providerManager.ActiveProviderType, RunModel() is { Length: > 0 } rm ? rm : null);
                        Agents.SubAgentModel.Use(apiModel);
                        if (opts?.Thread != null) { opts.Thread.Provider = null; opts.Thread.Model = apiModel; }
                        _debugLog?.Warn("SubAgent", $"{label}: the local server stayed full — carrying on with {provider.ProviderId} {apiModel ?? "(run model)"}");
                        RaiseSubAgent(ev with { Phase = "tool", Detail = thai ? "เครื่องไม่ว่าง → ใช้โมเดลหลักต่อ" : "local busy → the run's model", Steps = step, CostUsd = Cost() });
                        retry = -1;
                        continue;
                    }
                    var wait = Agents.FailoverPolicy.RetryDelay(retry, Agents.ProviderErrors.RetryAfter(ex.Message));
                    _debugLog?.Info("SubAgent", $"{label}: {Agents.ProviderErrors.Classify(ex)} on {provider.ProviderId} — step {step} again in {wait.TotalSeconds:0}s");
                    RaiseSubAgent(ev with { Phase = "tool", Detail = thai ? $"รอคิว {wait.TotalSeconds:0} วิ" : $"busy — retry in {wait.TotalSeconds:0}s", Steps = step, CostUsd = Cost() });
                    await Task.Delay(wait, ct);
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
                    if (opts?.Control != null) ReportUnreadNotes(opts.Control, thai);
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
                if (opts?.Control != null) ReportUnreadNotes(opts.Control, thai);
                RaiseSubAgent(ev with { Phase = opts?.ReportOnDone == false ? "tool" : "done",
                    Detail = Shorten(gist, 140), Steps = step, CostUsd = Cost() });
                if (opts?.Thread != null)
                {
                    messages.Add(response.ToAssistantMessage(includeToolUse: false));
                    opts.Thread.Finished = true;
                }
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
                    string runName = _agentToolService.CanonicalToolName(c.tc.Name);   // "Read" → read_file, as in the main loop
                    if (c.type == null || !tools.Any(t => t.Name.Equals(runName, StringComparison.OrdinalIgnoreCase)))
                        return new ContentBlock { Type = "tool_result", ToolUseId = c.tc.Id, IsError = true,
                            Content = $"Tool '{c.tc.Name}' is not available to a sub-agent." };
                    var call = new ToolCall { ToolName = runName, Type = c.type.Value, Arguments = ParseJsonInputToArgs(c.tc.Input) };
                    string doing = GetToolStatusMessage(c.tc.Name, call.Arguments);
                    OnAgentStatus?.Invoke($"{label}: {doing}");
                    RaiseSubAgent(ev with { Phase = "tool", Detail = Shorten(doing, 90), Steps = step, CostUsd = Cost(), CallId = c.tc.Id, Tool = runName });
                    var r = await _agentToolService.ExecuteToolAsync(call, ct);
                    string text = r.Success ? (r.Output ?? "") : $"Error: {AgentToolService.ModelText(r)}";
                    RaiseSubAgent(ev with { Phase = "result", Detail = "", Steps = step, CostUsd = Cost(), CallId = c.tc.Id,
                        Result = ResultForMap(text), Ok = r.Success });
                    if (text.Length > SubAgentMaxResultChars)
                        text = text[..SubAgentMaxResultChars] + $"\n… (cut: {text.Length - SubAgentMaxResultChars} more characters — read a narrower range)";
                    return new ContentBlock { Type = "tool_result", ToolUseId = c.tc.Id, Content = text, IsError = !r.Success };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    RaiseSubAgent(ev with { Phase = "result", Detail = "", Steps = step, CostUsd = Cost(), CallId = c.tc.Id,
                        Result = ResultForMap("Error: " + ex.Message), Ok = false });
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
        if (opts?.Control != null) ReportUnreadNotes(opts.Control, thai);
        RaiseSubAgent(ev with { Phase = opts?.ReportOnDone == false ? "tool" : "done",
            Detail = thai ? "ใช้ขั้นครบโดยไม่ได้สรุป" : "step budget used, no report", Steps = maxSteps, CostUsd = Cost() });
        return thai ? "(เอเจนต์ย่อยใช้ขั้นตอนครบโดยไม่ได้สรุป)" : "(the sub-agent used its step budget without a report)";
    }

    public enum SubAgentRetryAction { Retry, ToRunModel, Fail }

    /// <summary>
    /// A sub-agent's request was refused as busy or rate-limited (retries before this one: <paramref name="retry"/>):
    /// ask again (up to <see cref="Agents.FailoverPolicy.MaxRetriesPerStep"/> times); then a local server still full hands
    /// the agent to the run's own model when there is one that runs a tool loop; else it fails. Pure.
    /// </summary>
    public static SubAgentRetryAction SubAgentRetry(int retry, bool onLocal, bool runModelUsable)
    {
        if (retry < Agents.FailoverPolicy.MaxRetriesPerStep) return SubAgentRetryAction.Retry;
        return onLocal && runModelUsable ? SubAgentRetryAction.ToRunModel : SubAgentRetryAction.Fail;
    }

    private int _subAgentCounter;

    static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
