using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Agents;

namespace CluadeX.Services;

/// <summary>
/// Effort-aware dispatch, wired into the agent (owner, 2026-10-17: "ระบบส่งเอเจนแยกทำงาน ต้องฉลาดตามความพยายาม …
/// เพื่อแก้ไขงานได้รวดเร็ว"): <see cref="DispatchPolicy"/> decides per step whether sub-agents may run and how far
/// (read-only fan-out, workers in worktrees, speculative fixes), on which model each kind runs, how many at once; the
/// run's spend guard caps it. Sub-agents start warm (the cached repo map and the run's brain recall, once) and every
/// finished run is recorded under the strategy it used, so the thresholds can be tuned from real numbers.
/// </summary>
public partial class CodeAgentService
{
    // ─── the decision for this step ───

    /// <summary>What the policy allows right now, for the run in this flow.</summary>
    internal DispatchDecision CurrentDispatch()
    {
        var s = _settingsService.Settings;
        var scope = AgentRunScope.Current;
        var root = RootOf(scope);
        var provider = _providerManager.ActiveProvider;
        string? folder = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null;
        return DispatchPolicy.Decide(new DispatchInput
        {
            Effort = DispatchPolicy.ResolveEffort(s.ReasoningEffort, scope?.StepEffort),
            PolicyEnabled = s.DispatchPolicyEnabled,
            ProviderCanRunSubAgents = provider != null && provider.SupportsNativeToolUse && provider.IsReady,
            RunBudgetUsd = s.RunBudgetUsd,
            BudgetGrantedUsd = root?.BudgetGrantedUsd ?? 0,
            RunSpentUsd = root?.CostUsd ?? 0,
            DailyBudgetUsd = s.DailyBudgetUsd,
            TodaySpentUsd = _costTrackingService?.TodayCostUsd ?? 0,
            MaxParallel = s.MaxParallelAgents,
            SpeculativeFixEnabled = s.SpeculativeFixEnabled,
            SpeculativeFixCount = s.SpeculativeFixCount,
            CanEditProject = DecideWorkerGrant(_permissionService, folder)?.Edit == true,
            IsGitRepo = folder != null && HasGitFolder(folder),
        });
    }

    private static AgentRunScope? RootOf(AgentRunScope? s)
    {
        while (s?.Parent != null) s = s.Parent;
        return s;
    }

    /// <summary>A .git folder or file (worktree) here or above — a quick check; worktree creation verifies for real.</summary>
    private static bool HasGitFolder(string folder)
    {
        try
        {
            for (var d = new System.IO.DirectoryInfo(folder); d != null; d = d.Parent)
            {
                string g = System.IO.Path.Combine(d.FullName, ".git");
                if (System.IO.Directory.Exists(g) || System.IO.File.Exists(g)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>The run's own model id (the provider's default when none is picked).</summary>
    private string RunModel()
    {
        string m = ModelIdFor(_providerManager.ActiveProvider);
        if (m.Length > 0) return m;
        return _providerManager.ActiveProviderType switch
        {
            AiProviderType.Anthropic => Providers.AnthropicProvider.DefaultModel,
            AiProviderType.OpenAI => Providers.OpenAiProvider.DefaultModel,
            AiProviderType.Gemini => Providers.GeminiProvider.DefaultModel,
            _ => "",
        };
    }

    private string? RoutedModel(SubAgentKind kind, int failedFixers = 0) =>
        DispatchPolicy.ModelFor(kind, _providerManager.ActiveProviderType, RunModel(),
            _settingsService.Settings.SubAgentModelOverrides, failedFixers);

    // ─── at most N at once per run ───

    private readonly ConditionalWeakTable<AgentRunScope, SemaphoreSlim> _runGates = new();
    private readonly SemaphoreSlim _noRunGate = new(3);

    private SemaphoreSlim GateFor(AgentRunScope? scope, int max)
    {
        var root = RootOf(scope);
        return root == null ? _noRunGate : _runGates.GetValue(root, _ => new SemaphoreSlim(Math.Max(1, max)));
    }

    // ─── quota: sub-agents go to Claude Dev like the main loop's failover ───

    private DateTime _quotaExhaustedUtc = DateTime.MinValue;

    /// <summary>The API said its credit/quota ran out within the last hour.</summary>
    private bool QuotaRecentlyExhausted => DateTime.UtcNow - _quotaExhaustedUtc < TimeSpan.FromHours(1);

    private void NoteQuotaExhausted() => _quotaExhaustedUtc = DateTime.UtcNow;

    /// <summary>One sub-agent's brief through Claude Dev (the user's Claude login): read-only, or edits in its own folder
    /// (a worktree) when <paramref name="canEdit"/>. Null when the CLI is not there.</summary>
    private async Task<string?> RunSubAgentOnClaudeDevAsync(string brief, bool canEdit, CancellationToken ct)
    {
        var cd = _providerManager.GetProvider(AiProviderType.ClaudeDev);
        try { await cd.InitializeAsync(ct); } catch (OperationCanceledException) { throw; } catch { }
        if (!cd.IsReady) return null;
        Providers.ClaudeDevProvider.Grant.Value = canEdit ? new Providers.ClaudeDevProvider.WorkerGrant(true, false) : null;
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in cd.ChatAsync(new List<ChatMessage>(), brief, null, ct)) sb.Append(chunk);
            return sb.ToString().Trim();
        }
        finally { Providers.ClaudeDevProvider.Grant.Value = null; }
    }

    // ─── warm start: the repo map and the run's brain recall, once ───

    private sealed class WarmStart { public string? RepoMap; public string? Recall; }
    private readonly ConditionalWeakTable<AgentRunScope, WarmStart> _warm = new();

    private static readonly Regex RecallBlock = new(@"<brainx_recall_(\w+)>.*?</brainx_recall_\1>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The run's message after auto-recall: its recall block is kept for the run's sub-agents.</summary>
    private void RememberRecallForSubAgents(string? messageWithRecall)
    {
        if (AgentRunScope.Current is not { } scope || string.IsNullOrEmpty(messageWithRecall)) return;
        var m = RecallBlock.Match(messageWithRecall);
        if (m.Success) _warm.GetOrCreateValue(scope).Recall = m.Value;
    }

    /// <summary>The context a sub-agent starts with instead of re-reading the project: the repo map (built once per
    /// run, cached by RepoMapService) and the brain recall the run already did. Empty when there is neither.</summary>
    internal string WarmContext(int mapChars = 4000)
    {
        var root = RootOf(AgentRunScope.Current);
        if (root == null) return "";
        var w = _warm.GetOrCreateValue(root);
        if (w.RepoMap == null)
        {
            try { w.RepoMap = _repoMap?.GetRepoMap(mapChars) ?? ""; } catch { w.RepoMap = ""; }
        }
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(w.RepoMap))
            sb.Append("## Repo map (cached — search or read only what you need; do not re-scan the project)\n").Append(w.RepoMap.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(w.Recall))
            sb.Append("## The brain's recall for this task (already searched — do not search the same again)\n").Append(w.Recall.Trim()).Append('\n');
        return sb.ToString().Trim();
    }

    private string WarmRepoMap()
    {
        var root = RootOf(AgentRunScope.Current);
        if (root == null) { try { return _repoMap?.GetRepoMap(4000) ?? ""; } catch { return ""; } }
        WarmContext();
        return _warm.GetOrCreateValue(root).RepoMap ?? "";
    }

    private string? WarmRecall() => RootOf(AgentRunScope.Current) is { } r ? _warm.GetOrCreateValue(r).Recall : null;

    // ─── the strategy ledger ───

    /// <summary>A finished paid run, under the furthest strategy it used (CostTrackingService.RecordRun).</summary>
    private void RecordRunStrategy(AgentLoopResult result)
    {
        if (_costTrackingService == null || AgentRunScope.Current is not { } scope || scope.Parent != null) return;
        bool paid = _providerManager.ActiveProviderType is AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini;
        if (!paid && scope.CostUsd == 0) return;
        var strategy = (DispatchStrategy)scope.Strategy;
        double seconds = (DateTime.Now - scope.StartedAt).TotalSeconds;
        _costTrackingService.RecordRun(DispatchPolicy.Tag(strategy), scope.CostUsd, seconds, result.Success);
        _debugLog?.Info("Dispatch", $"run recorded · {DispatchPolicy.Tag(strategy)} · ${scope.CostUsd:0.000} · {seconds:0}s · success={result.Success}");
    }

    // ─── workers (agent_spawn type "workers") ───

    /// <summary>Pieces from the model's agent_spawn call: a JSON array of {task, files[]} (files may be a comma list).</summary>
    public static List<DispatchPolicy.WorkPiece> ParsePieces(string? json)
    {
        var list = new List<DispatchPolicy.WorkPiece>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("pieces", out var p) ? p : doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                string task = e.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var files = new List<string>();
                if (e.TryGetProperty("files", out var f))
                {
                    if (f.ValueKind == JsonValueKind.Array)
                        files.AddRange(f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()));
                    else if (f.ValueKind == JsonValueKind.String)
                        files.AddRange(f.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                list.Add(new DispatchPolicy.WorkPiece(task, files.Where(x => x.Length > 0).ToList()));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private async Task<string> RunWorkersAsync(SubAgentRequest request, CancellationToken ct)
    {
        var dispatch = CurrentDispatch();
        if (DispatchPolicy.Gate(dispatch, "workers", true) is { } refusal) return refusal;
        var pieces = ParsePieces(request.Pieces);
        if (DispatchPolicy.CheckPieces(pieces) is { } bad)
            return "Workers not started: " + bad + " Pass pieces as a JSON array: [{\"task\":\"…\",\"files\":[\"src/A.cs\"]}, …].";
        string folder = _fileSystemService.WorkingDirectory;
        var parent = AgentRunScope.Current;
        parent?.MarkStrategy((int)DispatchStrategy.Workers);
        bool thai = _localizationService.CurrentLanguage == "th";
        string codeModel = RoutedModel(SubAgentKind.Code) ?? "";
        var ids = pieces.Select(_ => Interlocked.Increment(ref _subAgentCounter)).ToArray();
        var scopes = new AgentRunScope?[pieces.Count];
        int reviewId = Interlocked.Increment(ref _subAgentCounter);
        AgentRunScope? reviewScope = null;
        string reviewModel = RoutedModel(SubAgentKind.Review) ?? "";
        _debugLog?.Info("Dispatch", $"workers · {pieces.Count} piece(s) · model {codeModel} · max {dispatch.MaxConcurrency} at once · {dispatch.Reason}");

        async Task<string> RunWorker(WorkerPool.WorkerContext wc, CancellationToken t)
        {
            var child = parent?.CreateChild(wc.WorkingDirectory, acceptEditsInFolder: true);
            scopes[wc.Index] = child;
            AgentRunScope.Enter(child);
            SubAgentModel.Use(codeModel);
            string label = (thai ? "ผู้ทำงาน #" : "Worker #") + (wc.Index + 1);
            var opts = new SubAgentRunOptions(ids[wc.Index], label, "worker", codeModel, child, wc.Prompt);
            return await RunSubAgentCoreAsync(new SubAgentRequest(wc.Prompt, "", "general"), t, opts)
                   ?? (QuotaRecentlyExhausted ? await RunSubAgentOnClaudeDevAsync(wc.Prompt, true, t) : null)
                   ?? "(the worker could not run on this model)";
        }

        async Task<string> RunReviewer(string prompt, CancellationToken t)
        {
            reviewScope = parent?.CreateChild();
            AgentRunScope.Enter(reviewScope);
            SubAgentModel.Use(reviewModel);
            var opts = new SubAgentRunOptions(reviewId, thai ? "ผู้ตรวจ" : "Reviewer", "reviewer", reviewModel, reviewScope, prompt);
            return await RunSubAgentCoreAsync(new SubAgentRequest(prompt, "", "explore"), t, opts)
                   ?? (QuotaRecentlyExhausted ? await RunSubAgentOnClaudeDevAsync(prompt, false, t) : null)
                   ?? "REJECT — the reviewer could not run";
        }

        var result = await WorkerPool.RunAsync(folder, request.Task, pieces, request.Context, WarmRepoMap(), dispatch.MaxConcurrency,
            RunWorker, dispatch.Reviewer ? RunReviewer : null, e =>
            {
                if (e.Index < 0 || e.Phase is "start") return;   // the sub-agent loop raises start/tool/done itself
                if (e.Phase == "failed")
                    RaiseSubAgent(new SubAgentEvent(ids[e.Index], (thai ? "ผู้ทำงาน #" : "Worker #") + (e.Index + 1), "worker",
                        Shorten(pieces[e.Index].Task, 160), "failed", Shorten(e.Detail, 120), 0, codeModel, scopes[e.Index]?.CostUsd ?? 0));
            }, ct);
        _debugLog?.Info("Dispatch", $"workers done · merged={result.Merged} · {result.AppliedFiles.Count} file(s) · {result.Took.TotalSeconds:0}s");
        return result.Summary;
    }

    // ─── speculative fix ───

    /// <summary>The command a run_build / run_tests result ran (its output starts with "$ cmd").</summary>
    public static string? FailingCommandOf(ToolResult r)
    {
        string first = (r.Output ?? "").Replace("\r", "").Split('\n')[0];
        if (!first.StartsWith("$ ")) return r.Arguments?.GetValueOrDefault("command");
        string cmd = first[2..];
        int det = cmd.IndexOf("   (detected:", StringComparison.Ordinal);
        return (det >= 0 ? cmd[..det] : cmd).Trim();
    }

    /// <summary>The same failure (its first error line), so one failure is raced once.</summary>
    public static string FailureSignature(string output)
    {
        var line = (output ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => Regex.IsMatch(l, @"\berror\b|\bfailed\b", RegexOptions.IgnoreCase)) ?? (output ?? "").Trim();
        return Regex.Replace(line, @"\d+", "#");
    }

    /// <summary>
    /// Race fixers on a red build/test when the policy says so. Returns the line for the main agent (null = no race):
    /// the winner's change is already applied, or what the fixers learned. <paramref name="fixed"/> = the project now
    /// holds a change that turned the command green.
    /// </summary>
    private async Task<(string? Note, bool Fixed)> MaybeSpeculativeFixAsync(ToolResult lastBuild, int mainFixFailures,
        HashSet<string> raced, CancellationToken ct)
    {
        var dispatch = CurrentDispatch();
        string effort = DispatchPolicy.ResolveEffort(_settingsService.Settings.ReasoningEffort, AgentRunScope.Current?.StepEffort);
        string output = ((lastBuild.Error ?? "") + "\n" + (lastBuild.Output ?? "")).Trim();
        string sig = FailureSignature(output);
        if (!DispatchPolicy.ShouldSpeculate(dispatch, effort, true, mainFixFailures, raced.Contains(sig))) return (null, false);
        string? cmd = FailingCommandOf(lastBuild);
        if (string.IsNullOrWhiteSpace(cmd)) return (null, false);
        raced.Add(sig);

        string folder = _fileSystemService.WorkingDirectory;
        var parent = AgentRunScope.Current;
        parent?.MarkStrategy((int)DispatchStrategy.SpeculativeFix);
        bool thai = _localizationService.CurrentLanguage == "th";
        int failedBefore = _failedFixers.GetOrCreateValue(RootOf(parent) ?? new AgentRunScope("", null)).Count;
        var req = new SpeculativeFixer.Request(folder, cmd, output, SpeculativeFixer.FilesNamedIn(output, folder),
            WarmRepoMap(), WarmRecall(), dispatch.FixerCount,
            TimeSpan.FromMinutes(Math.Clamp(_settingsService.Settings.SpeculativeFixMinutes, 1, 30)), failedBefore);
        string model = RoutedModel(SubAgentKind.Fix, failedBefore) ?? "";
        var ids = Enumerable.Range(0, req.Fixers).Select(_ => Interlocked.Increment(ref _subAgentCounter)).ToArray();
        var scopes = new AgentRunScope?[req.Fixers];
        OnAgentStatus?.Invoke(thai ? $"ส่ง {req.Fixers} ตัวแก้ไปแข่งกันแก้ {cmd}…" : $"Racing {req.Fixers} fixers on {cmd}…");
        _debugLog?.Info("Dispatch", $"speculative fix · {req.Fixers} fixer(s) on `{cmd}` · model {model} · files {string.Join(",", req.Files)} · {dispatch.Reason}");

        string Label(int i) => (thai ? "ตัวแก้ #" : "Fixer #") + (i + 1) + " · " + SpeculativeFixer.ApproachName(SpeculativeFixer.Approaches[i]);

        async Task<string> RunFixer(SpeculativeFixer.FixerContext fc, CancellationToken t)
        {
            var child = parent?.CreateChild(fc.WorkingDirectory, acceptEditsInFolder: true);
            scopes[fc.Index] = child;
            AgentRunScope.Enter(child);
            SubAgentModel.Use(model);
            var opts = new SubAgentRunOptions(ids[fc.Index], Label(fc.Index), "fixer", model, child, fc.Prompt, ReportOnDone: false);
            return await RunSubAgentCoreAsync(new SubAgentRequest(fc.Prompt, "", "general"), t, opts)
                   ?? (QuotaRecentlyExhausted ? await RunSubAgentOnClaudeDevAsync(fc.Prompt, true, t) : null)
                   ?? "(the fixer could not run on this model)";
        }

        static async Task<(bool, string)> RunCommand(string command, string cwd, CancellationToken t)
        {
            var r = await ShellRunner.RunAsync(ShellRunner.DefaultShell, command, cwd, 300_000, t);
            return (r.ExitCode == 0 && !r.TimedOut && !r.Cancelled, r.Output);
        }

        SpeculativeFixer.Result result;
        try
        {
            result = await SpeculativeFixer.RunAsync(req, RunFixer, RunCommand, e =>
            {
                string phase = e.Phase switch
                {
                    "verify" => "tool", "green" => "done", "red" or "failed" => "failed", "cancelled" => "stopped", _ => "",
                };
                if (phase.Length == 0) return;
                string detail = e.Phase == "verify" ? (thai ? "รันซ้ำ: " : "re-run: ") + e.Detail
                    : e.Phase == "green" ? (thai ? "ผ่านแล้ว ✓ " : "green ✓ ") + e.Detail : e.Detail;
                RaiseSubAgent(new SubAgentEvent(ids[e.Index], Label(e.Index), "fixer", Shorten(cmd, 160), phase,
                    Shorten(detail, 120), 0, model, scopes[e.Index]?.CostUsd ?? 0));
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _debugLog?.Warn("Dispatch", "speculative fix could not start", ex);
            return ($"[speculative fix] could not start: {ex.Message}. Fix it yourself.", false);
        }

        var failed = _failedFixers.GetOrCreateValue(RootOf(parent) ?? new AgentRunScope("", null));
        if (!result.Fixed) failed.Count += result.All.Count(o => !o.Green);
        _debugLog?.Info("Dispatch", $"speculative fix done · fixed={result.Fixed} · winner={(result.Winner?.Index + 1)?.ToString() ?? "-"} · {result.Took.TotalSeconds:0}s"
            + $" · cost {string.Join("/", scopes.Select(s => (s?.CostUsd ?? 0).ToString("0.000")))}");
        string note = result.Fixed
            ? $"[speculative fix] {result.Summary}\nRe-run the {cmd} once to confirm, then carry on."
            : $"[speculative fix] {result.Summary}";
        return (note, result.Fixed);
    }

    private sealed class FixerTally { public int Count; }
    private readonly ConditionalWeakTable<AgentRunScope, FixerTally> _failedFixers = new();
}
