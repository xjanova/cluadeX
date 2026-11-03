using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Agents;

/// <summary>One agent's run as the engine asks for it.</summary>
public sealed record WorkflowAgentCall(string WorkflowId, string Key, string Phase, string Label, string Prompt, string Type, int Attempt,
    string SessionKey = "");

/// <summary>What one agent answered and what it cost.</summary>
public sealed record WorkflowAgentResult(string Text, decimal CostUsd);

/// <summary>One agent of a workflow, as the card and the state file show it. Changed only under the run's lock.</summary>
public sealed class WorkflowAgentState
{
    public string Key { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"agent", "stage" or "refuter".</summary>
    public string Kind { get; set; } = "agent";
    /// <summary>queued · running · done · failed · stopped · skipped (budget) · cached (from an earlier run).</summary>
    public string State { get; set; } = "queued";
    public decimal CostUsd { get; set; }
    public string Prompt { get; set; } = "";
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

/// <summary>A refuted-or-kept claim of a verify step.</summary>
public sealed class WorkflowVerdict
{
    public string Phase { get; set; } = "";
    public string Claim { get; set; } = "";
    public int Refuted { get; set; }
    public int Votes { get; set; }
    public int Refuters { get; set; }
    /// <summary>More than half of the refuters refuted it.</summary>
    public bool Killed { get; set; }
}

/// <summary>A finished agent's answer, reused by Resume.</summary>
public sealed class WorkflowCached
{
    public string Output { get; set; } = "";
    public decimal CostUsd { get; set; }
}

/// <summary>A workflow run: its plan, every agent, the cache Resume reads. Saved as DataRoot/workflows/&lt;id&gt;.json.</summary>
public sealed class WorkflowRunState
{
    public string Id { get; set; } = "wf-" + Guid.NewGuid().ToString("N")[..10];
    public string SessionKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string PlanJson { get; set; } = "";
    /// <summary>running · done · stopped · failed.</summary>
    public string Status { get; set; } = "running";
    public string? StopReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    /// <summary>Seconds spent in earlier attempts (before a Resume).</summary>
    public double ElapsedBefore { get; set; }
    public decimal EstimateUsd { get; set; }
    public long EstimateRamMb { get; set; }
    public int EstimateAgents { get; set; }
    public List<WorkflowAgentState> Agents { get; set; } = new();
    public Dictionary<string, WorkflowCached> Cache { get; set; } = new();
    public List<WorkflowVerdict> Verdicts { get; set; } = new();
    /// <summary>What each phase produced (survivors of a verify), in order — the tool's result is built from it.</summary>
    public Dictionary<string, List<string>> PhaseItems { get; set; } = new();
    public string? Summary { get; set; }

    internal readonly object Gate = new();

    public double ElapsedSeconds => ElapsedBefore + ((EndedAt ?? DateTime.Now) - StartedAt).TotalSeconds;
    public decimal SpentUsd { get { lock (Gate) return Agents.Sum(a => a.CostUsd); } }
    public bool IsRunning => Status == "running";
}

/// <summary>
/// Runs a <see cref="WorkflowPlan"/> deterministically on whatever runs one agent (the app: a native sub-agent; the
/// harness: a mock). Owns the workflow's concurrency, the machine-wide slot, the budget check before every start,
/// Stop (one token for every agent) and the cache that lets Resume skip what already finished.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly Func<WorkflowAgentCall, CancellationToken, Task<WorkflowAgentResult>> _runner;

    /// <summary>The most agent rows one run may have, items from earlier phases included (twice what a plan may declare).</summary>
    public const int HardMaxAgents = WorkflowPlan.MaxAgents * 2;

    /// <summary>Takes one machine-wide slot (the app: <see cref="AgentLoad.EnterAsync"/>); each needs one LeaveSlot.</summary>
    public Func<CancellationToken, Task> EnterSlot { get; init; } = AgentLoad.EnterAsync;
    public Action LeaveSlot { get; init; } = AgentLoad.Leave;
    /// <summary>The run's spend guard: true = start nothing more (each agent not started is "skipped").</summary>
    public Func<bool> BudgetSpent { get; init; } = () => false;
    /// <summary>Called after every finished agent (the app writes the state file).</summary>
    public Action<WorkflowRunState>? Save { get; init; }

    /// <summary>A row changed (any thread). The card coalesces these.</summary>
    public event Action<WorkflowRunState, WorkflowAgentState>? RowChanged;

    public WorkflowEngine(Func<WorkflowAgentCall, CancellationToken, Task<WorkflowAgentResult>> runner) => _runner = runner;

    /// <summary>Run (or resume) <paramref name="st"/>. Never throws for a stop: the state says "stopped".</summary>
    public async Task RunAsync(WorkflowRunState st, WorkflowPlan plan, CancellationToken ct)
    {
        lock (st.Gate)
        {
            st.Status = "running";
            st.StopReason = null;
            st.EndedAt = null;
            st.StartedAt = DateTime.Now;
            st.Name = plan.Name;
            // A resume starts the rows again: what finished comes back as "cached" when its turn comes.
            st.Agents.Clear();
            st.Verdicts.Clear();
            st.PhaseItems.Clear();
        }
        using var sem = new SemaphoreSlim(plan.Concurrency);
        try
        {
            foreach (var phase in plan.Phases)
            {
                ct.ThrowIfCancellationRequested();
                List<string> items;
                if (phase.Agents is { Count: > 0 } agents)
                {
                    var jobs = agents.Select(spec => AgentThenVerifyAsync(st, phase, spec, sem, ct)).ToList();
                    items = (await WhenAllSettled(jobs)).SelectMany(x => x).ToList();
                }
                else if (phase.Pipeline is { } pipe)
                {
                    var source = pipe.Items is { Count: > 0 } given ? given : ItemsFrom(st, pipe.From!, pipe.Field);
                    var jobs = source.Select((item, i) => PipelineItemAsync(st, phase, pipe, item, i + 1, sem, ct)).ToList();
                    items = (await WhenAllSettled(jobs)).Where(x => x != null).Select(x => x!).ToList();
                }
                else
                {
                    var v = phase.Verify!;
                    items = await VerifyAsync(st, phase.Title, phase.Title, ItemsFrom(st, v.From!, v.Field), v, sem, ct);
                }
                lock (st.Gate) st.PhaseItems[phase.Title] = items;
            }
            Finish(st, "done", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(st, "stopped", st.StopReason ?? "stopped");
        }
        catch (Exception ex)
        {
            Finish(st, "failed", ex.Message);
        }
    }

    private void Finish(WorkflowRunState st, string status, string? reason)
    {
        List<WorkflowAgentState> touched;
        lock (st.Gate)
        {
            st.Status = status;
            st.StopReason = reason;
            st.EndedAt = DateTime.Now;
            touched = st.Agents.Where(a => a.State is "queued" or "running").ToList();
            foreach (var a in touched) { a.State = "stopped"; a.EndedAt = DateTime.Now; }
            st.Summary = Summarize(st);
        }
        foreach (var a in touched) Raise(st, a);
        Save?.Invoke(st);
    }

    /// <summary>Every job to its end (a stop ends them all quickly), then the first stop rethrown.</summary>
    private static async Task<T[]> WhenAllSettled<T>(List<Task<T>> jobs)
    {
        try { return await Task.WhenAll(jobs); }
        catch
        {
            // WhenAll already waited for all of them; surface a cancellation as such.
            if (jobs.FirstOrDefault(j => j.IsCanceled) is { } c) await c;
            throw;
        }
    }

    private async Task<List<string>> AgentThenVerifyAsync(WorkflowRunState st, WorkflowPhase phase, WorkflowAgentSpec spec, SemaphoreSlim sem, CancellationToken ct)
    {
        string? output = await RunOneAsync(st, phase.Title, spec.Label, "agent", spec.Prompt, spec.Type, spec.Schema, sem, ct);
        if (output == null) return new();
        if (phase.Verify is not { } v) return new() { output };
        // No barrier: this agent's claims are refuted now, while the phase's other agents still work.
        var claims = Extract(output, v.Field);
        return await VerifyAsync(st, phase.Title, spec.Label, claims, v, sem, ct);
    }

    private async Task<string?> PipelineItemAsync(WorkflowRunState st, WorkflowPhase phase, WorkflowPipeline pipe, string item, int n,
        SemaphoreSlim sem, CancellationToken ct)
    {
        string? prev = null;
        foreach (var stage in pipe.Stages)
        {
            string prompt = Fill(stage.Prompt, item, prev);
            prev = await RunOneAsync(st, phase.Title, $"{stage.Label} · {n}", "stage", prompt, stage.Type, stage.Schema, sem, ct);
            if (prev == null) return null;   // a failed stage ends this item; the other items go on
        }
        return prev;
    }

    private async Task<List<string>> VerifyAsync(WorkflowRunState st, string phase, string source, List<string> claims, WorkflowVerify v,
        SemaphoreSlim sem, CancellationToken ct)
    {
        var jobs = claims.Select((claim, ci) => VerifyOneAsync(st, phase, source, claim, ci + 1, v, sem, ct)).ToList();
        var kept = await WhenAllSettled(jobs);
        return kept.Where(k => k != null).Select(k => k!).ToList();
    }

    private async Task<string?> VerifyOneAsync(WorkflowRunState st, string phase, string source, string claim, int ci, WorkflowVerify v,
        SemaphoreSlim sem, CancellationToken ct)
    {
        var votes = Enumerable.Range(1, v.Refuters).Select(r => RunOneAsync(st, phase, $"{source} · #{ci} refute {r}/{v.Refuters}", "refuter",
            RefutePrompt(claim, r, v), "explore", RefuterSchema, sem, ct)).ToList();
        var answers = await WhenAllSettled(votes);
        int refuted = 0, counted = 0;
        foreach (var a in answers)
        {
            if (a == null) continue;
            counted++;
            try
            {
                using var doc = JsonDocument.Parse(a);
                if (doc.RootElement.TryGetProperty("refuted", out var r) && r.ValueKind == JsonValueKind.True) refuted++;
            }
            catch (JsonException) { }
        }
        // Majority kills: more than half of the refuters asked (an abstaining one does not save the claim either way).
        bool killed = refuted * 2 > v.Refuters;
        lock (st.Gate)
            st.Verdicts.Add(new WorkflowVerdict { Phase = phase, Claim = claim, Refuted = refuted, Votes = counted, Refuters = v.Refuters, Killed = killed });
        return killed ? null : claim;
    }

    /// <summary>One agent: from the cache, or under the workflow's concurrency, the machine's slot and the budget.
    /// Null = it did not produce an answer (failed, skipped); a stop throws.</summary>
    private async Task<string?> RunOneAsync(WorkflowRunState st, string phase, string label, string kind, string prompt, string type,
        JsonElement? schema, SemaphoreSlim sem, CancellationToken ct)
    {
        string key = KeyOf(phase, label, prompt, schema);
        var row = new WorkflowAgentState { Key = key, Phase = phase, Label = label, Kind = kind, Prompt = prompt };
        WorkflowCached? cached;
        bool overLimit;
        lock (st.Gate)
        {
            // The plan's check counts what it can see; items an earlier phase produces (a pipeline or verify "from") are
            // only known now. Past the ceiling, nothing more starts.
            overLimit = st.Agents.Count >= HardMaxAgents;
            st.Agents.Add(row);
            st.Cache.TryGetValue(key, out cached);
            if (cached != null) { row.State = "cached"; row.Output = cached.Output; row.EndedAt = DateTime.Now; }
            else if (overLimit) { row.State = "skipped"; row.Error = $"agent limit ({HardMaxAgents})"; row.EndedAt = DateTime.Now; }
        }
        Raise(st, row);
        if (cached != null) return cached.Output;
        if (overLimit) return null;

        try { await sem.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            Set(st, row, r => { r.State = "stopped"; r.EndedAt = DateTime.Now; });
            throw;
        }
        try
        {
            // A Stop releases the running agents' places as it cancels them, and a queued agent can be handed one before its
            // own wait sees the stop: it must not start then.
            ct.ThrowIfCancellationRequested();
            if (BudgetSpent())
            {
                Set(st, row, r => { r.State = "skipped"; r.Error = "budget"; r.EndedAt = DateTime.Now; });
                return null;
            }
            await EnterSlot(ct);
            try
            {
                Set(st, row, r => { r.State = "running"; r.StartedAt = DateTime.Now; });
                string full = prompt + SchemaInstruction(schema);
                var res = await _runner(new WorkflowAgentCall(st.Id, key, phase, label, full, type, 1, st.SessionKey), ct);
                decimal cost = res.CostUsd;
                string? answer = res.Text;
                string? problem = null;
                if (schema is { } sch && !Conforms(res.Text, sch, out answer, out problem))
                {
                    // One repair turn: the agent sees what was wrong with its answer.
                    var again = await _runner(new WorkflowAgentCall(st.Id, key, phase, label,
                        full + "\n\nYour previous answer did not match the required JSON (" + problem + "). Previous answer:\n"
                        + Clip(res.Text, 4000) + "\n\nAnswer again with ONLY the JSON object.", type, 2, st.SessionKey), ct);
                    cost += again.CostUsd;
                    if (!Conforms(again.Text, sch, out answer, out problem)) answer = null;
                }
                if (answer == null)
                {
                    Set(st, row, r => { r.State = "failed"; r.CostUsd = cost; r.Error = "answer did not match the schema: " + problem; r.Output = res.Text; r.EndedAt = DateTime.Now; });
                    return null;
                }
                Set(st, row, r => { r.State = "done"; r.CostUsd = cost; r.Output = answer; r.EndedAt = DateTime.Now; });
                lock (st.Gate) st.Cache[key] = new WorkflowCached { Output = answer, CostUsd = cost };
                Save?.Invoke(st);
                return answer;
            }
            finally { LeaveSlot(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set(st, row, r => { r.State = "stopped"; r.EndedAt = DateTime.Now; });
            throw;
        }
        catch (Exception ex)
        {
            Set(st, row, r => { r.State = "failed"; r.Error = ex.Message; r.EndedAt = DateTime.Now; });
            return null;
        }
        finally { sem.Release(); }
    }

    private void Set(WorkflowRunState st, WorkflowAgentState row, Action<WorkflowAgentState> change)
    {
        lock (st.Gate) change(row);
        Raise(st, row);
    }

    private void Raise(WorkflowRunState st, WorkflowAgentState row)
    {
        try { RowChanged?.Invoke(st, row); } catch { /* a view must not break the run */ }
    }

    // ─── items, prompts, schema ───

    /// <summary>The items an earlier phase left, with <paramref name="field"/> taken out of each answer that has it.</summary>
    private static List<string> ItemsFrom(WorkflowRunState st, string phase, string? field)
    {
        List<string> outputs;
        lock (st.Gate) outputs = st.PhaseItems.TryGetValue(phase, out var list) ? list.ToList() : new();
        return outputs.SelectMany(o => Extract(o, field)).ToList();
    }

    /// <summary>An answer's claims: the array at <paramref name="field"/> (each element as text), a top-level array's
    /// elements, or the answer itself. Public for the harness.</summary>
    public static List<string> Extract(string output, string? field)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            JsonElement arr = default;
            if (!string.IsNullOrWhiteSpace(field) && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var f)
                && f.ValueKind == JsonValueKind.Array) arr = f;
            else if (root.ValueKind == JsonValueKind.Array) arr = root;
            if (arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                          .Where(s => s.Length > 0).ToList();
        }
        catch (JsonException) { }
        return new() { output };
    }

    private static string Fill(string template, string item, string? prev)
    {
        string s = template.Replace("{{item}}", item).Replace("{{prev}}", prev ?? "");
        // A stage that names neither still needs its input.
        if (!template.Contains("{{item}}") && !template.Contains("{{prev}}"))
            s += "\n\nInput:\n" + (prev ?? item);
        return s;
    }

    private static string RefutePrompt(string claim, int n, WorkflowVerify v) =>
        "You are an adversarial verifier. Another agent made the claim below. Try hard to REFUTE it: check the code, the "
        + "files and the facts yourself; do not trust the claim's own reasoning. Refute it only with concrete evidence "
        + "(file:line, exact text) that it is wrong or does not hold; if you cannot, it stands."
        + (n > 1 ? $" You are refuter {n}: look at it from a different angle than the obvious one." : "")
        + (string.IsNullOrWhiteSpace(v.Prompt) ? "" : "\n\n" + v.Prompt!.Trim())
        + "\n\nClaim:\n" + claim;

    /// <summary>What every refuter answers.</summary>
    public static readonly JsonElement RefuterSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"refuted":{"type":"boolean"},"reason":{"type":"string"}},"required":["refuted","reason"]}""").RootElement.Clone();

    private static string SchemaInstruction(JsonElement? schema) => schema is { } s
        ? "\n\nEnd your work with your answer as ONE JSON object and nothing else (no prose around it), matching this JSON schema:\n" + s.GetRawText()
        : "";

    /// <summary>Does <paramref name="text"/> hold one JSON object that matches <paramref name="schema"/> (required
    /// properties present, top-level types right)? <paramref name="json"/>: the object alone. Public for the harness.</summary>
    public static bool Conforms(string text, JsonElement schema, out string? json, out string? problem)
    {
        json = null;
        problem = null;
        string t = (text ?? "").Trim();
        int a = t.IndexOf('{'), b = t.LastIndexOf('}');
        if (a < 0 || b <= a) { problem = "no JSON object"; return false; }
        string candidate = t[a..(b + 1)];
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "not an object"; return false; }
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var r in req.EnumerateArray())
                        if (r.GetString() is { } name && !root.TryGetProperty(name, out _)) { problem = $"missing \"{name}\""; return false; }
                if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                    foreach (var p in props.EnumerateObject())
                        if (root.TryGetProperty(p.Name, out var val) && p.Value.TryGetProperty("type", out var ty) && ty.GetString() is { } want
                            && !KindMatches(val, want))
                        { problem = $"\"{p.Name}\" should be {want}"; return false; }
            }
            json = candidate;
            return true;
        }
        catch (JsonException ex) { problem = "invalid JSON: " + ex.Message; return false; }
    }

    private static bool KindMatches(JsonElement v, string type) => type switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    public static string KeyOf(string phase, string label, string prompt, JsonElement? schema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(phase + "\u001f" + label + "\u001f" + prompt + "\u001f" + (schema?.GetRawText() ?? "")));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>One line per phase: what it produced, what the verifiers killed, the spend.</summary>
    private static string Summarize(WorkflowRunState st)
    {
        var sb = new StringBuilder();
        int done = st.Agents.Count(a => a.State is "done" or "cached"), failed = st.Agents.Count(a => a.State == "failed"),
            skipped = st.Agents.Count(a => a.State == "skipped" && a.Error == "budget"), stopped = st.Agents.Count(a => a.State == "stopped"),
            limited = st.Agents.Count(a => a.State == "skipped" && a.Error != "budget");
        sb.Append($"{st.Name}: {st.Status} · {st.Agents.Count} agent(s): {done} done");
        if (failed > 0) sb.Append($", {failed} failed");
        if (skipped > 0) sb.Append($", {skipped} skipped (budget)");
        if (limited > 0) sb.Append($", {limited} skipped (agent limit {HardMaxAgents})");
        if (stopped > 0) sb.Append($", {stopped} stopped");
        sb.Append($" · ${st.Agents.Sum(a => a.CostUsd):0.00}");
        int killed = st.Verdicts.Count(v => v.Killed);
        if (st.Verdicts.Count > 0) sb.Append($" · verify: {st.Verdicts.Count - killed} kept, {killed} refuted");
        return sb.ToString();
    }
}
