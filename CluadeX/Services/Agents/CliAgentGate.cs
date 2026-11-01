using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>What a CLI agent asked to spawn: the type it named, and the tool call's id (what its end is reported by).</summary>
public sealed record CliSpawn(string Type, string ToolUseId);

/// <summary>The answer to one spawn. <paramref name="Model"/>: the model it runs on (null = leave the call's own);
/// <paramref name="Isolate"/>: it writes, so it gets its own git worktree.</summary>
public sealed record CliSpawnVerdict(bool Allow, string? Reason, string? Model = null, bool Isolate = false);

/// <summary>
/// The dispatch policy for an agent that runs its own sub-agents (Claude Code's Agent tool, Gemini CLI's invoke_agent).
/// CluadeX never sees those spawns as tool calls of its own, so the policy read SupportsNativeToolUse and said nothing:
/// on 2026-11-01 four read-only searches ran on Opus and cost more than the main loop. The CLI's PreToolUse hook now asks
/// here before each spawn (<see cref="CliAgentGate"/>), which holds in the CLI itself rather than in a prompt it may
/// ignore: refuse, cap how many run at once, pick the model, give a writer its own worktree. Pure.
/// </summary>
public static class CliAgentPolicy
{
    /// <summary>Types that cannot edit files: Claude Code's Explore, Plan and guide, Gemini's investigators.</summary>
    public static bool IsReadOnly(AiProviderType cli, string type) => cli switch
    {
        AiProviderType.ClaudeDev => type is "Explore" or "Plan" or "claude-code-guide",
        AiProviderType.GeminiDev or AiProviderType.AntigravityDev => type is "codebase_investigator" or "cli_help",
        _ => false,
    };

    /// <summary>The type this CLI's read-only search goes by, for a refusal to name.</summary>
    public static string ReadOnlyType(AiProviderType cli) => cli == AiProviderType.ClaudeDev ? "Explore" : "codebase_investigator";

    /// <summary>The CLI can give a writing sub-agent its own worktree (Claude Code's isolation: worktree, proven against
    /// 2.1.296 on 2026-11-01). Gemini's invoke_agent has no such option, so its writers stay refused.</summary>
    public static bool CanIsolate(AiProviderType cli) => cli == AiProviderType.ClaudeDev;

    /// <summary>Claude Code's Agent tool takes an alias, not a model id: the alias of an id or a pick from Settings.
    /// Null = no alias it would accept (the call keeps inheriting the run's model).</summary>
    public static string? ClaudeAlias(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var family in new[] { "haiku", "sonnet", "opus" })
            if (m.Contains(family)) return family;
        return null;
    }

    /// <summary>The model a spawn runs on: Settings' pick for its kind, else read-only work on the cheapest. A writer
    /// inherits the run's model. Null = leave it.</summary>
    public static string? ModelFor(AiProviderType cli, bool readOnly, IReadOnlyDictionary<string, string>? overrides)
    {
        var kind = readOnly ? SubAgentKind.Explore : SubAgentKind.Code;
        string? picked = overrides != null && overrides.TryGetValue(kind.ToString().ToLowerInvariant(), out var o) && !string.IsNullOrWhiteSpace(o)
            ? o.Trim() : null;
        if (cli == AiProviderType.ClaudeDev) return ClaudeAlias(picked) ?? (readOnly ? "haiku" : null);
        return picked;
    }

    /// <summary>The wrapper's sentence on sub-agents for this step, in the words of each CLI. It explains what the hook
    /// enforces, so the model plans for it instead of finding out from refusals. Null = this CLI's old wording stands.</summary>
    public static string? PromptLine(DispatchDecision? d, AiProviderType cli)
    {
        if (d == null || d.Legacy) return null;
        string tool = cli switch
        {
            AiProviderType.ClaudeDev => "the Agent tool",
            AiProviderType.OpenAIDev => "spawn_agent",
            _ => "your sub-agents",
        };
        if (!d.SubAgents) return $"Sub-agents are off for this step ({d.Reason}): do the work yourself in this conversation.";
        string most = $"At most {d.MaxConcurrency} at once; a start over that is refused until one finishes. ";
        if (d.ReadOnlyOnly || !d.Workers || !CanIsolate(cli))
            return $"Use {tool} for independent read-only investigation only ({ReadOnlyType(cli)}); every edit stays with you. " + most;
        return $"When the task has two or more independent parts, hand them to {tool} — several in one response run in parallel, and the " +
               "user watches each one on the agent map. Keep a small task to yourself. Read-only work goes to Explore. A sub-agent that " +
               "writes works in its own git worktree, which CluadeX sets up: its changes stay on branch worktree-agent-* until you bring " +
               "them into the project yourself" + (d.Reviewer
                   ? " and have one Explore sub-agent review the combined diff before you call it done. "
                   : " and review the combined diff. ") + most;
    }

    /// <summary>One spawn under the step's decision. <paramref name="running"/>: how many of this turn's sub-agents have
    /// not ended.</summary>
    public static CliSpawnVerdict Decide(DispatchDecision d, AiProviderType cli, string type, int running,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (d.Legacy) return new CliSpawnVerdict(true, null);
        if (!d.SubAgents) return new CliSpawnVerdict(false, $"Sub-agents are off for this step ({d.Reason}). Do it yourself in this conversation.");
        bool readOnly = IsReadOnly(cli, type);
        if (!readOnly && (d.ReadOnlyOnly || !d.Workers))
            return new CliSpawnVerdict(false, $"Only read-only sub-agents for this step ({d.Reason}): use {ReadOnlyType(cli)} to investigate, and make the edits yourself.");
        if (!readOnly && !CanIsolate(cli))
            return new CliSpawnVerdict(false, $"This CLI cannot give a writing sub-agent its own worktree: use {ReadOnlyType(cli)} to investigate, and make the edits yourself.");
        if (running >= d.MaxConcurrency)
            return new CliSpawnVerdict(false, $"{running} sub-agents are running, the most allowed at once. Wait for one to finish, then start this one again.");
        return new CliSpawnVerdict(true, null, ModelFor(cli, readOnly, overrides), !readOnly);
    }

    /// <summary>The tool input the hook hands back: the call's own, with the model and the worktree set. Null = as it was.</summary>
    public static string? RewrittenInput(JsonElement input, CliSpawnVerdict v)
    {
        if (v.Model == null && !v.Isolate) return null;
        if (JsonNode.Parse(input.GetRawText()) is not JsonObject o) return null;
        if (v.Model != null) o["model"] = v.Model;
        if (v.Isolate) o["isolation"] = "worktree";
        return o.ToJsonString();
    }
}

/// <summary>
/// One CLI turn's spawn gate, reached from the hook over the chat-tools pipe. Reserving and deciding happen under one
/// lock: a CLI sends several spawns in one message and their hooks run at once. A spawn holds its place until its end
/// shows in the stream (<see cref="Finished"/>); a turn that ends drops the gate with its ticket.
/// </summary>
public sealed class CliAgentGate
{
    private readonly object _lock = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    /// <summary>Spawns whose hook named no call id (Gemini CLI's hooks do not): they hold a place until any end shows.</summary>
    private int _unnamed;
    private readonly AiProviderType _cli;
    private readonly Func<DispatchDecision> _decision;
    private readonly IReadOnlyDictionary<string, string>? _overrides;
    private readonly Action<string>? _log;

    public CliAgentGate(AiProviderType cli, Func<DispatchDecision> decision, IReadOnlyDictionary<string, string>? overrides = null, Action<string>? log = null)
    {
        _cli = cli;
        _decision = decision;
        _overrides = overrides;
        _log = log;
    }

    public int Running { get { lock (_lock) return _running.Count + _unnamed; } }

    /// <summary>The turn ended: whatever it still held goes back to the machine's count.</summary>
    public void ReleaseAll()
    {
        int held;
        lock (_lock)
        {
            held = _running.Count + _unnamed;
            _running.Clear();
            _unnamed = 0;
        }
        for (int i = 0; i < held; i++) AgentLoad.Leave();
    }

    public CliSpawnVerdict Ask(CliSpawn spawn)
    {
        // The decision reads the settings and the run's spend, so a budget or a limit changed mid-turn applies here.
        var decision = _decision();
        CliSpawnVerdict verdict;
        lock (_lock)
        {
            verdict = CliAgentPolicy.Decide(decision, _cli, spawn.Type, _running.Count + _unnamed, _overrides);
            if (verdict.Allow && !decision.Legacy && !AgentLoad.TryEnter(AgentLoad.CurrentCap()))
                verdict = new CliSpawnVerdict(false, "This machine is running as many sub-agents as it can hold. Wait for one to finish, or do this part yourself.");
            else if (verdict.Allow && !decision.Legacy)
            {
                if (spawn.ToolUseId.Length > 0) _running.Add(spawn.ToolUseId);
                else _unnamed++;
            }
        }
        _log?.Invoke($"{_cli} spawn {spawn.Type}: {(verdict.Allow ? "allowed" : "refused")}"
            + (verdict.Model != null ? $" · model {verdict.Model}" : "") + (verdict.Isolate ? " · worktree" : "")
            + (verdict.Reason != null ? $" · {verdict.Reason}" : ""));
        return verdict;
    }

    /// <summary>The sub-agent of that tool call ended (or was refused: then nothing is held).</summary>
    public void Finished(string toolUseId)
    {
        bool freed;
        lock (_lock)
        {
            freed = _running.Remove(toolUseId);
            if (!freed && _unnamed > 0) { _unnamed--; freed = true; }
        }
        if (freed) AgentLoad.Leave();
    }
}
