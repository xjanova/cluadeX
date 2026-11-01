#nullable enable
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using CluadeX.Services.Safety;
using CluadeX.Services.Tools;

namespace SafetyTest;

/// <summary>
/// The dispatch policy over agents that run their own sub-agents, and the machine-wide limit, through the REAL dll and the
/// REAL hook process. No CLI runs here and nothing is spent: the stream lines are the shape Claude Code 2.1.296 wrote in
/// the live runs of 2026-11-01.
/// </summary>
internal static class AgentControlChecks
{
    static DispatchDecision Off => new(DispatchStrategy.Single, false, false, false, false, false, 3, 0, "effort low");
    static DispatchDecision ReadOnly => new(DispatchStrategy.FanOut, true, true, false, false, false, 2, 0, "effort medium");
    static DispatchDecision Writers => new(DispatchStrategy.Workers, true, false, true, true, false, 2, 0, "effort high");
    static DispatchDecision Legacy => new(DispatchStrategy.FanOut, true, false, false, false, false, 3, 0, "off") { Legacy = true };

    public static void Run(string proj, Action<string, bool, string> check, Func<string?> matchingExe)
    {
        Console.WriteLine("Sub-agents: the policy, the machine limit, the CLI's own spawns");
        MachineLimit(check);
        Policy(check);
        Gate(check);
        Stream(check);
        Args(check);
        RealHook(proj, check, matchingExe);
        Sweep(proj, check);
        if (Environment.GetEnvironmentVariable("CLUADEX_LIVE_CLI") == "1") LiveClaude(proj, check, matchingExe);
    }

    /// <summary>The whole chain with the real Claude Code CLI, on the user's login: the settings CluadeX writes, the real
    /// hook process, the gate in this process, the stream reader. Spends one small turn, so it runs only when asked.</summary>
    static void LiveClaude(string work, Action<string, bool, string> check, Func<string?> matchingExe)
    {
        string? exe = matchingExe();
        if (exe == null) return;
        Console.WriteLine("Live: Claude Code through CluadeX's hook");
        Free();
        string repo = Path.Combine(work, "live-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "one.txt"), "one");
        WorktreeSandbox.GitAsync(repo, CancellationToken.None, null, "init", "-q").GetAwaiter().GetResult();
        string settings = Path.Combine(work, "live-settings.json");
        File.WriteAllText(settings, GuardHook.SettingsJson(exe, true));

        var gate = new CliAgentGate(AiProviderType.ClaudeDev, () => ReadOnly, log: m => Console.WriteLine("       gate: " + m));
        using var turnCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var ticket = ChatToolBridge.Register(new AgentRunScope("live", repo),
            (call, ct) => Task.FromResult(new ToolResult { Type = call.Type, ToolName = call.ToolName }), repo, turnCts.Token, gate: gate)!;

        var stream = new ClaudeDevStreamTurn(repo);
        var models = new List<string>();
        var phases = new List<string>();
        stream.SubAgent += e => { lock (phases) { phases.Add(e.Phase); if (!string.IsNullOrEmpty(e.Model)) models.Add(e.Model!); } };
        stream.SubAgentEnded += gate.Finished;

        string shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var psi = new ProcessStartInfo(shell)
        {
            WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "/c", "claude", "-p", "--output-format", "stream-json", "--verbose", "--model", "sonnet",
                     "--settings", settings, "--permission-mode", "bypassPermissions" })
            psi.ArgumentList.Add(a);
        psi.Environment[GuardHook.AgentGateTokenEnv] = ticket.Token;
        using var p = Process.Start(psi)!;
        p.StandardInput.Write("Do exactly these two steps and nothing else. Step 1: call the Agent tool with subagent_type \"Explore\", "
            + "description \"count\", prompt \"List this folder and say how many files it has.\". Step 2: call the Agent tool with subagent_type "
            + "\"general-purpose\", description \"write\", prompt \"Create the file two.txt\". Then say in one line what each returned.");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string? line;
        var lines = new List<string>();
        while ((line = p.StandardOutput.ReadLine()) != null) { lines.Add(line); stream.Feed(line); }
        if (!p.WaitForExit(30000)) p.Kill(true);
        string all = string.Join("\n", lines);
        check("LIVE the search ran, on Haiku, chosen by the gate", models.Any(m => m.Contains("haiku")) && phases.Contains("done"), string.Join(",", models) + " · " + string.Join(",", phases));
        check("LIVE the writer was refused by the hook with the gate's reason", all.Contains("Only read-only sub-agents"), err.Result);
        check("LIVE nothing was written", !File.Exists(Path.Combine(repo, "two.txt")), "");
        check("LIVE every place came back", gate.Running == 0 && AgentLoad.Running == 0, $"{gate.Running} / {AgentLoad.Running}");
        var cost = lines.LastOrDefault(l => l.Contains("\"total_cost_usd\""));
        if (cost != null)
            using (var doc = JsonDocument.Parse(cost)) Console.WriteLine("       cost: $" + doc.RootElement.GetProperty("total_cost_usd"));
        gate.ReleaseAll();
    }

    /// <summary>A real repository with real worktrees, where the lab's Claude Code put them.</summary>
    static void Sweep(string work, Action<string, bool, string> check)
    {
        static (int Code, string Out) Git(string cwd, params string[] args) =>
            WorktreeSandbox.GitAsync(cwd, CancellationToken.None, null, args).GetAwaiter().GetResult();
        string repo = Path.Combine(work, "sweep-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@t");
        Git(repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", ".");
        Git(repo, "commit", "-qm", "first");
        string Add(string name)
        {
            string path = Path.Combine(repo, ".claude", "worktrees", "agent-" + name);
            Git(repo, "worktree", "add", "-q", "-b", "worktree-agent-" + name, path);
            return path;
        }
        string foreign = Add("foreign");
        string oldClean = Add("oldclean");
        Directory.SetLastWriteTimeUtc(oldClean, DateTime.UtcNow.AddDays(-3));
        var before = CliWorktreeSweep.ListAsync(repo, CancellationToken.None).GetAwaiter().GetResult();
        check("the sub-agent worktrees are listed", before.Count == 2, string.Join(" | ", before));

        string clean = Add("clean");
        string dirty = Add("dirty");
        File.WriteAllText(Path.Combine(dirty, "b.txt"), "the sub-agent's only copy");
        string committed = Add("committed");
        File.WriteAllText(Path.Combine(committed, "c.txt"), "c");
        Git(committed, "add", ".");
        Git(committed, "commit", "-qm", "work");
        string locked = Add("locked");
        Git(repo, "worktree", "lock", locked);

        var kept = CliWorktreeSweep.SweepAsync(repo, before, null, CancellationToken.None).GetAwaiter().GetResult();
        check("this turn's empty worktree is gone, branch too", !Directory.Exists(clean) && Git(repo, "branch", "--list", "worktree-agent-clean").Out.Trim().Length == 0, clean);
        check("uncommitted work stays, on disk", File.Exists(Path.Combine(dirty, "b.txt")), dirty);
        check("committed, unmerged work stays", File.Exists(Path.Combine(committed, "c.txt")) && Git(repo, "branch", "--list", "worktree-agent-committed").Out.Contains("committed"), committed);
        check("a locked one stays", Directory.Exists(locked), locked);
        check("a recent one from before this turn may be another session's: untouched", Directory.Exists(foreign), foreign);
        check("an empty one idle for days is cleared (the app closed mid-turn)", !Directory.Exists(oldClean), oldClean);
        check("the turn names exactly what stayed", kept.Select(k => k.Branch).OrderBy(b => b).SequenceEqual(new[] { "worktree-agent-committed", "worktree-agent-dirty", "worktree-agent-locked" }),
            string.Join(",", kept.Select(k => k.Branch)));
        check("no repository: nothing happens", CliWorktreeSweep.SweepAsync(work, new(), null, CancellationToken.None).GetAwaiter().GetResult().Count == 0
            || !Directory.Exists(Path.Combine(work, ".git")), "");
        check("the list parser skips the main checkout and other branches", CliWorktreeSweep.Parse(
            "worktree E:/x\nHEAD 1\nbranch refs/heads/main\n\nworktree E:/x/.claude/worktrees/agent-a\nHEAD 2\nbranch refs/heads/worktree-agent-a\nlocked\n\n"
            + "worktree E:/y\nHEAD 3\nbranch refs/heads/worktree-agent-z\n").Single() is { Branch: "worktree-agent-a", Locked: true }, "");
    }

    static void MachineLimit(Action<string, bool, string> check)
    {
        check("this machine's shape: 12 threads, 32 GB free -> 6 at once", AgentLoad.Cap(12, 32_000) == 6, AgentLoad.Cap(12, 32_000).ToString());
        check("memory is what binds on a full machine: 7 GB free -> 2", AgentLoad.Cap(12, 7_000) == 2, AgentLoad.Cap(12, 7_000).ToString());
        check("nothing free still leaves one: work slows, it is not refused", AgentLoad.Cap(12, 500) == 1, AgentLoad.Cap(12, 500).ToString());
        check("a two-thread machine runs one", AgentLoad.Cap(2, 32_000) == 1, AgentLoad.Cap(2, 32_000).ToString());
        check("a huge machine stops at the policy's ceiling", AgentLoad.Cap(128, 512_000) == DispatchPolicy.MaxParallelCap, AgentLoad.Cap(128, 512_000).ToString());
        check("free memory reads as a real number", AgentLoad.FreeRamMb() > 0, AgentLoad.FreeRamMb().ToString());

        int taken = 0;
        Parallel.For(0, 200, _ => { if (AgentLoad.TryEnter(3)) Interlocked.Increment(ref taken); });
        check("200 starts at once take exactly the 3 slots", taken == 3 && AgentLoad.Running == 3, $"{taken} / {AgentLoad.Running}");
        for (int i = 0; i < taken; i++) AgentLoad.Leave();
        check("every slot came back", AgentLoad.Running == 0, AgentLoad.Running.ToString());

        for (int i = 0; i < DispatchPolicy.MaxParallelCap; i++) AgentLoad.TryEnter(DispatchPolicy.MaxParallelCap);
        using var stop = new CancellationTokenSource(150);
        bool stopped = false;
        try { AgentLoad.EnterAsync(stop.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { stopped = true; }
        check("a sub-agent waiting for a slot stops on cancel and takes none", stopped && AgentLoad.Running == DispatchPolicy.MaxParallelCap, AgentLoad.Running.ToString());
        Free();
    }

    static void Free() { while (AgentLoad.Running > 0) AgentLoad.Leave(); }

    static void Policy(Action<string, bool, string> check)
    {
        var claude = AiProviderType.ClaudeDev;
        var gemini = AiProviderType.GeminiDev;
        check("sub-agents off: refused", !CliAgentPolicy.Decide(Off, claude, "Explore", 0).Allow, "");
        check("policy off: passes untouched", CliAgentPolicy.Decide(Legacy, claude, "general-purpose", 99) is { Allow: true, Model: null, Isolate: false }, "");
        var explore = CliAgentPolicy.Decide(ReadOnly, claude, "Explore", 0);
        check("read-only search runs on Haiku, no worktree", explore is { Allow: true, Model: "haiku", Isolate: false }, explore.ToString());
        check("a writer is refused on a read-only step", !CliAgentPolicy.Decide(ReadOnly, claude, "general-purpose", 0).Allow, "");
        var writer = CliAgentPolicy.Decide(Writers, claude, "general-purpose", 0);
        check("a writer gets a worktree and inherits the run's model", writer is { Allow: true, Model: null, Isolate: true }, writer.ToString());
        check("the concurrency limit refuses the next one", !CliAgentPolicy.Decide(Writers, claude, "Explore", 2).Allow, "");
        check("Gemini's writer is refused: it cannot be isolated", !CliAgentPolicy.Decide(Writers, gemini, "generalist", 0).Allow, "");
        check("Gemini's investigator runs", CliAgentPolicy.Decide(ReadOnly, gemini, "codebase_investigator", 0).Allow, "");
        var pick = new Dictionary<string, string> { ["explore"] = "claude-sonnet-5-5", ["code"] = "gpt-only" };
        check("Settings' pick wins, as an alias", CliAgentPolicy.ModelFor(claude, true, pick) == "sonnet", CliAgentPolicy.ModelFor(claude, true, pick) ?? "");
        check("a pick the CLI cannot take leaves the writer inheriting", CliAgentPolicy.ModelFor(claude, false, pick) == null, "");
        check("the prompt says sub-agents are off", CliAgentPolicy.PromptLine(Off, claude)!.Contains("off for this step"), "");
        check("no decision: the old wording stands", CliAgentPolicy.PromptLine(Legacy, claude) == null && CliAgentPolicy.PromptLine(null, claude) == null, "");

        using var doc = JsonDocument.Parse("""{"prompt":"p","subagent_type":"Explore"}""");
        string? rewritten = CliAgentPolicy.RewrittenInput(doc.RootElement, explore);
        check("the call keeps its own fields beside the model", rewritten != null && rewritten.Contains("\"prompt\":\"p\"") && rewritten.Contains("\"model\":\"haiku\""), rewritten ?? "");
        check("nothing to change: no rewrite", CliAgentPolicy.RewrittenInput(doc.RootElement, new CliSpawnVerdict(true, null)) == null, "");
    }

    static void Gate(Action<string, bool, string> check)
    {
        Free();
        var decision = Writers;
        var gate = new CliAgentGate(AiProviderType.ClaudeDev, () => decision);
        check("first spawn in", gate.Ask(new CliSpawn("Explore", "a")).Allow, "");
        check("second spawn in", gate.Ask(new CliSpawn("Explore", "b")).Allow, "");
        check("the machine count follows the turn", AgentLoad.Running == 2, AgentLoad.Running.ToString());
        check("third refused at the run's limit", !gate.Ask(new CliSpawn("Explore", "c")).Allow, "");
        gate.Finished("c");
        check("a refused spawn's end frees nothing", gate.Running == 2 && AgentLoad.Running == 2, $"{gate.Running} / {AgentLoad.Running}");
        gate.Finished("a");
        gate.Finished("a");
        check("an end reported twice frees one place", gate.Running == 1 && AgentLoad.Running == 1, $"{gate.Running} / {AgentLoad.Running}");
        check("a place freed lets the next in", gate.Ask(new CliSpawn("Explore", "d")).Allow, "");
        decision = Off;
        check("a setting changed mid-turn applies to the next spawn", !gate.Ask(new CliSpawn("Explore", "e")).Allow, "");
        decision = Writers;
        gate.ReleaseAll();
        check("the turn's end gives every place back", gate.Running == 0 && AgentLoad.Running == 0, $"{gate.Running} / {AgentLoad.Running}");

        for (int i = 0; i < DispatchPolicy.MaxParallelCap; i++) AgentLoad.TryEnter(DispatchPolicy.MaxParallelCap);
        var busy = gate.Ask(new CliSpawn("Explore", "f"));
        check("a full machine refuses, saying why, and holds nothing", !busy.Allow && busy.Reason!.Contains("machine") && gate.Running == 0, busy.ToString());
        Free();

        var legacy = new CliAgentGate(AiProviderType.ClaudeDev, () => Legacy);
        for (int i = 0; i < 12; i++) legacy.Ask(new CliSpawn("general-purpose", "l" + i));
        check("policy off: no counting at all, as before", legacy.Running == 0 && AgentLoad.Running == 0, $"{legacy.Running} / {AgentLoad.Running}");

        var unnamed = new CliAgentGate(AiProviderType.GeminiDev, () => ReadOnly);
        unnamed.Ask(new CliSpawn("codebase_investigator", ""));
        check("a spawn with no call id still counts", unnamed.Running == 1, unnamed.Running.ToString());
        unnamed.Finished("whatever-the-stream-called-it");
        check("and any end frees it", unnamed.Running == 0 && AgentLoad.Running == 0, $"{unnamed.Running} / {AgentLoad.Running}");
    }

    static string Call(string id, bool background) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "tool_use", id, name = "Agent", input = new { description = "Sleep then pong", subagent_type = "general-purpose", run_in_background = background, prompt = "x" } },
            },
        },
    });

    static string Result(string id, string status) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", content = new object[] { new { type = "text", text = "report" } } } } },
        tool_use_result = new { status, agentId = "a1", resolvedModel = "claude-haiku-5-5" },
    });

    static string Notified(string id, string status) => JsonSerializer.Serialize(new { type = "system", subtype = "task_notification", tool_use_id = id, status, summary = "finished" });

    static void Stream(Action<string, bool, string> check)
    {
        var turn = new ClaudeDevStreamTurn(null);
        var phases = new List<string>();
        var ended = new List<string>();
        turn.SubAgent += e => phases.Add(e.Phase);
        turn.SubAgentEnded += ended.Add;

        turn.Feed(Call("fg", false));
        turn.Feed(Result("fg", "completed"));
        check("a foreground agent ends with its result", ended.SequenceEqual(new[] { "fg" }) && phases.Contains("done"), string.Join(",", phases));

        phases.Clear(); ended.Clear();
        turn.Feed(Call("bg", true));
        turn.Feed(Result("bg", "async_launched"));
        check("a background launch is not its end (the map said done with it running)", ended.Count == 0 && !phases.Contains("done"), string.Join(",", phases));
        turn.Feed(Notified("someone-else", "completed"));
        check("a notice about another task changes nothing", ended.Count == 0, "");
        turn.Feed(Notified("bg", "completed"));
        check("the notice ends it", ended.SequenceEqual(new[] { "bg" }) && phases.Contains("done"), string.Join(",", phases));

        phases.Clear(); ended.Clear();
        turn.Feed(Call("gone", true));
        turn.Feed(Result("gone", "async_launched"));
        var left = turn.CloseUnfinishedAgents();
        check("a process that died leaves it stopped, and named", left.Count == 1 && phases.Contains("stopped") && ended.Contains("gone"), string.Join(",", phases));
        check("nothing is left to close twice", turn.CloseUnfinishedAgents().Count == 0, "");

        var spent = new ClaudeDevStreamTurn(null);
        spent.Feed(JsonSerializer.Serialize(new { type = "result", subtype = "error_max_budget_usd", is_error = true, total_cost_usd = 2.0 }));
        check("the CLI's budget stop is recognised", spent.StoppedAtBudget, "");
    }

    static string Refused(string args) => args.Contains("--disallowedTools") ? args.Split("--disallowedTools")[1].Split('"')[1] : "";

    static void Args(Action<string, bool, string> check)
    {
        var grant = new ClaudeDevProvider.WorkerGrant(Edit: true, Run: true);
        string off = ClaudeDevProvider.BuildArgs(@"E:\Code\X", grant, "default", "auto", dispatch: Off);
        check("Claude Dev: sub-agents off takes the tool away", off.Contains("--disallowedTools") && off.Contains("Task,Agent"), off);
        string on = ClaudeDevProvider.BuildArgs(@"E:\Code\X", grant, "default", "auto", dispatch: Writers, budgetUsd: 1.5m);
        check("Claude Dev: on keeps it, and carries the budget", !Refused(on).Contains("Agent") && on.Contains("--max-budget-usd"), on);
        check("Claude Dev: one refusal list, so neither overrides the other", off.Split("--disallowedTools").Length == 2 && Refused(off).Contains("git clean") && Refused(off).Contains("Task,Agent"), off);
        check("no budget: no flag", !ClaudeDevProvider.BuildArgs(@"E:\Code\X", grant, "default", "auto", dispatch: Writers).Contains("--max-budget-usd"), "");

        var codexOff = OpenAIDevProvider.BuildArgs(@"E:\Code\X", grant, "", "", null, dispatch: Off);
        check("Codex: sub-agents off in its own config", codexOff.Contains("features.multi_agent=false"), string.Join(" ", codexOff));
        var codexOn = OpenAIDevProvider.BuildArgs(@"E:\Code\X", grant, "", "", null, dispatch: Writers);
        check("Codex: the limit in its own config", codexOn.Contains("agents.max_threads=2"), string.Join(" ", codexOn));
        var codexPick = OpenAIDevProvider.BuildArgs(@"E:\Code\X", grant, "", "", null, dispatch: ReadOnly, readOnlyAgentModel: "gpt-small");
        check("Codex: Settings' search model on a read-only step", codexPick.Contains("agents.default_subagent_model='gpt-small'"), string.Join(" ", codexPick));
        var codexWriters = OpenAIDevProvider.BuildArgs(@"E:\Code\X", grant, "", "", null, dispatch: Writers, readOnlyAgentModel: "gpt-small");
        check("Codex: not where a sub-agent may write", !codexWriters.Any(a => a.Contains("default_subagent_model")), string.Join(" ", codexWriters));
        var codexLegacy = OpenAIDevProvider.BuildArgs(@"E:\Code\X", grant, "", "", null, dispatch: Legacy);
        check("Codex: policy off adds nothing", !codexLegacy.Any(a => a.Contains("multi_agent") || a.Contains("max_threads")), string.Join(" ", codexLegacy));

        var codex = new CodexStreamTranslator(@"E:\Code\X");
        string spawn = """{"type":"item.started","item":{"id":"i1","type":"collab_tool_call","tool":"spawn_agent","prompt":"look","status":"in_progress"}}""";
        check("Codex's spawn reaches the map as an Agent call", string.Join("", codex.Translate(spawn)).Contains("\"Agent\""), string.Join("", codex.Translate(spawn)));
        string wait = """{"type":"item.started","item":{"id":"i2","type":"collab_tool_call","tool":"wait","status":"in_progress"}}""";
        check("Codex's wait is not a sub-agent", !string.Join("", codex.Translate(wait)).Contains("\"Agent\""), "");

        var geminiOff = GeminiDevProvider.BuildTurnSettings(null, null, null, null, Off, "gemini-cheap").ToJsonString();
        check("Gemini: sub-agents off in its own settings", geminiOff.Contains("\"enableAgents\":false"), geminiOff);
        var hooks = new GeminiDevProvider.TurnHooks("a", "b", "c");
        var gemini = GeminiDevProvider.BuildTurnSettings(hooks, null, null, null, ReadOnly, "gemini-cheap").ToJsonString();
        check("Gemini: generalist off, investigator on the cheap model", gemini.Contains("\"generalist\":{\"enabled\":false}") && gemini.Contains("gemini-cheap") && !gemini.Contains("enableAgents"), gemini);
        check("Gemini: its spawn reaches the guard", gemini.Contains("invoke_agent"), gemini);
        string plain = GeminiDevProvider.BuildTurnSettings(null, null, null).ToJsonString();
        check("Gemini: no decision adds nothing", !plain.Contains("generalist") && !plain.Contains("enableAgents"), plain);

        string settings = GuardHook.SettingsJson(@"C:\x\CluadeX.exe", true);
        check("the turn's hooks carry the spawn gate", settings.Contains("Agent|Task") && settings.Contains(GuardHook.AgentGateFlag), settings);
    }

    static void RealHook(string proj, Action<string, bool, string> check, Func<string?> matchingExe)
    {
        string? exe = matchingExe();
        if (exe == null) return;
        (int Code, string Out, string Err) Ask(string flag, string? token, string json)
        {
            var psi = new ProcessStartInfo(exe, flag) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (token != null) psi.Environment[GuardHook.AgentGateTokenEnv] = token;
            else psi.Environment.Remove(GuardHook.AgentGateTokenEnv);
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(json);
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); return (-1, "", "timeout"); }
            return (p.ExitCode, o, err.Result);
        }
        string Spawn(string id, string type) => JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse", tool_name = "Agent", tool_use_id = id,
            tool_input = new { description = "d", prompt = "p", subagent_type = type },
        });

        Free();
        var decision = ReadOnly;
        var gate = new CliAgentGate(AiProviderType.ClaudeDev, () => decision);
        using var turnCts = new CancellationTokenSource();
        using var ticket = ChatToolBridge.Register(new AgentRunScope("safety", proj),
            (call, ct) => Task.FromResult(new ToolResult { Type = call.Type, ToolName = call.ToolName }), proj, turnCts.Token, gate: gate)!;

        var ok = Ask(GuardHook.AgentGateFlag, ticket.Token, Spawn("h1", "Explore"));
        check("the real hook lets a search through, rewritten to Haiku", ok.Code == 0 && ok.Out.Contains("updatedInput") && ok.Out.Contains("\"model\":\"haiku\"") && ok.Out.Contains("\"prompt\":\"p\""), $"{ok.Code} {ok.Out} {ok.Err}");
        check("the app counted it", gate.Running == 1, gate.Running.ToString());
        var no = Ask(GuardHook.AgentGateFlag, ticket.Token, Spawn("h2", "general-purpose"));
        check("the real hook refuses a writer: exit 2, the reason for the model", no.Code == 2 && no.Err.Contains("read-only"), $"{no.Code} {no.Err}");
        var none = Ask(GuardHook.AgentGateFlag, null, Spawn("h3", "general-purpose"));
        check("no gate behind the run: as before", none.Code == 0 && none.Out.Length == 0, $"{none.Code} {none.Out} {none.Err}");
        var dead = Ask(GuardHook.AgentGateFlag, "not-a-turn", Spawn("h4", "Explore"));
        check("a turn the app does not know: refused", dead.Code == 2, $"{dead.Code} {dead.Err}");
        var broken = Ask(GuardHook.AgentGateFlag, ticket.Token, "{not json");
        check("a call it cannot read: refused, no crash", broken.Code == 2, $"{broken.Code} {broken.Err}");

        string gemini = JsonSerializer.Serialize(new { hook_event_name = "BeforeTool", tool_name = "invoke_agent", tool_input = new { agent_name = "generalist", prompt = "p" } });
        var g = Ask(GuardHook.Flag, ticket.Token, gemini);
        check("Gemini's spawn through its guard hook reaches the same gate", g.Code == 2, $"{g.Code} {g.Err}");

        gate.ReleaseAll();
        check("nothing held after the turn", AgentLoad.Running == 0, AgentLoad.Running.ToString());
    }
}
