#nullable enable
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Desktop;
using CluadeX.Services.Mcp;
using CluadeX.Services.Providers;
using CluadeX.Services.Safety;
using CluadeX.Services.Tools;

namespace SafetyTest;

/// <summary>
/// The gaps a remote supervisor hit driving CluadeX over the pipe (TPIX run, 2026-11-01), through the REAL dll: the
/// run grant opening PowerShell as well as Bash, the refusals that named the wrong cause, the bridge's lanes under
/// parallel calls in one turn, the sub-agent results that came after the last reply, and the status calls' rate limit.
/// No CLI runs and nothing is spent.
/// </summary>
internal static class SupervisionChecks
{
    public static void Run(Action<string, bool, string> check, Func<string?> matchingExe)
    {
        Console.WriteLine("Supervision: shell grant, refusal wording, parallel lanes, late sub-agent results, poll limit");
        ShellGrant(check);
        RealHook(check, matchingExe);
        Refusals(check);
        Lanes(check);
        LateReports(check);
        Polls(check);
    }

    static string Allowed(string args) => args.Contains("--allowedTools") ? args.Split("--allowedTools")[1].Split('"')[1] : "";
    static string Refused(string args) => args.Contains("--disallowedTools") ? args.Split("--disallowedTools")[1].Split('"')[1] : "";

    static void ShellGrant(Action<string, bool, string> check)
    {
        string run = ClaudeDevProvider.BuildArgs(@"E:\Code\X", new ClaudeDevProvider.WorkerGrant(Edit: true, Run: true), "default", "auto");
        var allowed = Allowed(run).Split(',');
        check("run grant: Claude Dev gets PowerShell as well as Bash (the TPIX refusals)", allowed.Contains("Bash") && allowed.Contains("PowerShell"), Allowed(run));
        check("run grant: what is never run is refused in PowerShell's words too",
            Refused(run).Contains("PowerShell(git push --force:*)") && Refused(run).Contains("PowerShell(Stop-Computer:*)") && Refused(run).Contains("Bash(git push --force:*)"),
            Refused(run));
        string edit = ClaudeDevProvider.BuildArgs(@"E:\Code\X", new ClaudeDevProvider.WorkerGrant(Edit: true, Run: false), "default", "auto");
        var editAllowed = Allowed(edit).Split(',');
        check("edit-only grant: no shell at all", !editAllowed.Contains("Bash") && !editAllowed.Contains("PowerShell"), Allowed(edit));
        string advice = ClaudeDevProvider.BuildArgs(@"E:\Code\X", null, "default", "auto");
        check("advice only: no shell, no edit", !Allowed(advice).Split(',').Any(t => t is "Bash" or "PowerShell" or "Edit" or "Write"), Allowed(advice));

        foreach (var ok in new[] { "./shutdown_server.sh", "bash scripts/shutdown-hooks.sh", "git status", "git push origin main", "Get-ChildItem shutdown" })
            check($"NeverRun lets `{ok}` through (a whole word, not a substring)", GuardHook.NeverRun(ok) == null, GuardHook.NeverRun(ok) ?? "");
        foreach (var bad in new[] { "shutdown /s /t 0", "Stop-Computer -Force", "Restart-Computer", "git status; git push -f", "cd x && git reset --hard HEAD~1", "git clean -fdx" })
            check($"NeverRun refuses `{bad}`", GuardHook.NeverRun(bad) != null, "");
    }

    /// <summary>The real hook process on Claude Dev's PowerShell: the run grant opens it, so the guard must read it.</summary>
    static void RealHook(Action<string, bool, string> check, Func<string?> matchingExe)
    {
        string? exe = matchingExe();
        if (exe == null) return;
        (int Code, string Err) Ask(string tool, string command)
        {
            var psi = new ProcessStartInfo(exe, GuardHook.Flag) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = tool, tool_input = new { command }, cwd = Path.GetTempPath() }));
            p.StandardInput.Close();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); return (-1, "timeout"); }
            return (p.ExitCode, err);
        }
        var stop = Ask("PowerShell", "Stop-Computer -Force");
        check("the real hook refuses Claude Dev's PowerShell Stop-Computer (exit 2, the reason)", stop.Code == 2 && stop.Err.Contains("never run"), $"{stop.Code} {stop.Err}");
        var push = Ask("PowerShell", "git push origin main --force");
        check("the real hook refuses a PowerShell force push written any way", push.Code == 2, $"{push.Code} {push.Err}");
        var fine = Ask("PowerShell", "Get-ChildItem; dotnet build");
        check("the real hook lets a normal PowerShell command run", fine.Code == 0, $"{fine.Code} {fine.Err}");
    }

    static void Refusals(Action<string, bool, string> check)
    {
        string? edit = ChatToolBridge.RefusalFor("Edit", true, @"E:\Code\tpix\.claude\settings.json");
        check("an Edit the CLI asked about names the file and the protected-path cause, not 'not open in this turn'",
            edit != null && edit.Contains(@".claude\settings.json") && edit.Contains("protects") && !edit.Contains("not open in this turn"), edit ?? "");
        string? ps = ChatToolBridge.RefusalFor("PowerShell", true);
        check("a PowerShell the CLI asked about says why it asked, not 'not open in this turn'",
            ps != null && ps.Contains("PowerShell command") && !ps.Contains("not open in this turn"), ps ?? "");
        string? bash = ChatToolBridge.RefusalFor("Bash", true);
        check("Bash gets the same answer as PowerShell", bash != null && bash.Contains("Bash command"), bash ?? "");
        string? web = ChatToolBridge.RefusalFor("WebFetch", true);
        check("a CLI tool the turn was not given still says so", web != null && web.Contains("not open in this turn"), web ?? "");
        check("an outside MCP tool is put to the user", ChatToolBridge.RefusalFor("mcp__brainx-brain__ssh_run", true) == null, "");
        string? nl = ChatToolBridge.RefusalFor("Write", true, "a\nb");
        check("a path with a line break stays on one line in the refusal", nl != null && !nl.Contains('\n'), nl ?? "");
    }

    static string Line(string token, string tool, object args) => JsonSerializer.Serialize(new { token, tool, args });

    /// <summary>The bridge under parallel calls in one turn — the shape Claude Code sends when one assistant message holds
    /// several tool_use blocks. Browser calls one at a time, desktop calls one at a time, the two lanes (and the project
    /// tools, and the permission prompt) never waiting on each other, a second turn's lanes untouched, and a turn ended
    /// mid-flight stopping only its own calls.</summary>
    static void Lanes(Action<string, bool, string> check)
    {
        string browserTool = BrowserToolModule.ToolNames.First();
        string desktopTool = DesktopToolModule.ToolNames.First();
        int browserNow = 0, browserMax = 0, desktopNow = 0, desktopMax = 0, allNow = 0, allMax = 0, ran = 0;
        var gate = new object();
        void Enter(ref int now, ref int max)
        {
            lock (gate) { now++; max = Math.Max(max, now); allNow++; allMax = Math.Max(allMax, allNow); ran++; }
        }
        void Leave(ref int now) { lock (gate) { now--; allNow--; } }

        int approvesNow = 0, approvesMax = 0;
        using var turn1 = new CancellationTokenSource();
        var ticket1 = ChatToolBridge.Register(new AgentRunScope("lanes-1", Path.GetTempPath()), async (call, ct) =>
        {
            bool b = BrowserToolModule.ToolNames.Contains(call.ToolName), d = DesktopToolModule.ToolNames.Contains(call.ToolName);
            if (b) Enter(ref browserNow, ref browserMax); else if (d) Enter(ref desktopNow, ref desktopMax); else lock (gate) { allNow++; allMax = Math.Max(allMax, allNow); ran++; }
            try { await Task.Delay(120, ct); }
            finally { if (b) Leave(ref browserNow); else if (d) Leave(ref desktopNow); else lock (gate) allNow--; }
            return new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = "ok " + call.ToolName };
        }, Path.GetTempPath(), turn1.Token, async (name, a) =>
        {
            lock (gate) { approvesNow++; approvesMax = Math.Max(approvesMax, approvesNow); }
            await Task.Delay(120);
            lock (gate) approvesNow--;
            return (true, null);
        });
        if (ticket1 == null) { check("a turn gets a ticket", false, ""); return; }

        var calls = new List<Task<ChatToolBridge.Reply>>();
        for (int i = 0; i < 3; i++)
        {
            calls.Add(ChatToolBridge.HandleAsync(Line(ticket1.Token, browserTool, new { })));
            calls.Add(ChatToolBridge.HandleAsync(Line(ticket1.Token, desktopTool, new { })));
        }
        calls.Add(ChatToolBridge.HandleAsync(Line(ticket1.Token, "new_chat", new { path = "x", message = "m" })));
        calls.Add(ChatToolBridge.HandleAsync(Line(ticket1.Token, "close_chat", new { chat_id = "1" })));
        for (int i = 0; i < 2; i++)
            calls.Add(ChatToolBridge.HandleAsync(Line(ticket1.Token, ChatToolBridge.ApproveToolName,
                new { tool_name = "mcp__brainx-brain__ssh_run", input = new { host = "h" + i } })));
        var sw = Stopwatch.StartNew();
        bool done = Task.WaitAll(calls.ToArray(), TimeSpan.FromSeconds(20));
        long ms = sw.ElapsedMilliseconds;
        var replies = calls.Where(c => c.IsCompletedSuccessfully).Select(c => c.Result).ToList();
        check($"10 parallel calls in one turn all answer ({ms} ms)", done && replies.Count == 10, string.Join(" | ", replies.Select(r => r.Text)));
        check("every tool call ran and none was refused (no 'not open in this turn', no 'turn is over')",
            replies.Take(8).All(r => r.Ok && r.Text.StartsWith("ok ")) && ran == 8 && !replies.Any(r => r.Text.Contains("not open") || r.Text.Contains("turn is over")),
            string.Join(" | ", replies.Select(r => r.Text)));
        check("both permission prompts were allowed, and asked side by side", replies.Skip(8).All(r => r.Text.Contains("allow")) && approvesMax == 2,
            $"approvesMax={approvesMax} " + string.Join(" | ", replies.Skip(8).Select(r => r.Text)));
        check("browser calls ran one at a time", browserMax == 1, $"browserMax={browserMax}");
        check("desktop calls ran one at a time", desktopMax == 1, $"desktopMax={desktopMax}");
        check("the lanes do not wait on each other (browser, desktop and chat tools overlapped)", allMax >= 3, $"allMax={allMax}");
        check("one lane at a time would take ≥ 3×120 ms per lane; the whole set did not run end to end", ms < 8 * 120, $"{ms} ms");

        // ── two turns: one turn's busy lane does not hold the other; ending a turn stops only its calls ──
        var hold = new TaskCompletionSource();
        int heldRuns = 0;
        using var turnA = new CancellationTokenSource();
        var a = ChatToolBridge.Register(new AgentRunScope("lanes-a", Path.GetTempPath()), async (call, ct) =>
        {
            Interlocked.Increment(ref heldRuns);
            await hold.Task.WaitAsync(ct);
            return new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = "a done" };
        }, Path.GetTempPath(), turnA.Token);
        using var turnB = new CancellationTokenSource();
        var b = ChatToolBridge.Register(new AgentRunScope("lanes-b", Path.GetTempPath()),
            (call, ct) => Task.FromResult(new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = "b done" }),
            Path.GetTempPath(), turnB.Token);
        if (a == null || b == null) { check("two turns get tickets", false, ""); return; }
        var aFirst = ChatToolBridge.HandleAsync(Line(a.Token, browserTool, new { }));
        var aQueued = ChatToolBridge.HandleAsync(Line(a.Token, browserTool, new { }));
        SpinWait.SpinUntil(() => Volatile.Read(ref heldRuns) == 1, 3000);
        var bCall = ChatToolBridge.HandleAsync(Line(b.Token, browserTool, new { }));
        bool bDone = bCall.Wait(3000);
        check("another turn's browser call does not wait for this turn's lane", bDone && bCall.Result.Ok && bCall.Result.Text == "b done", bDone ? bCall.Result.Text : "still waiting");
        check("this turn's second browser call waits behind its first", !aQueued.IsCompleted && heldRuns == 1, $"heldRuns={heldRuns}");
        a.Dispose();
        bool aEnded = Task.WaitAll(new Task[] { aFirst, aQueued }, 3000);
        check("ending the turn stops its running call and the one queued behind it, saying the turn stopped",
            aEnded && !aFirst.Result.Ok && !aQueued.Result.Ok && aFirst.Result.Text.Contains("turn was stopped") && aQueued.Result.Text.Contains("turn was stopped") && heldRuns == 1,
            aEnded ? aFirst.Result.Text + " | " + aQueued.Result.Text : "hung");
        var late = ChatToolBridge.HandleAsync(Line(a.Token, browserTool, new { })).GetAwaiter().GetResult();
        check("a call with the ended turn's token: turn over, nothing run", !late.Ok && late.Text.Contains("turn is over") && heldRuns == 1, late.Text);
        var bAfter = ChatToolBridge.HandleAsync(Line(b.Token, desktopTool, new { })).GetAwaiter().GetResult();
        check("the other turn keeps working", bAfter.Ok && bAfter.Text == "b done", bAfter.Text);
        hold.TrySetResult();
        b.Dispose();
        ticket1.Dispose();
    }

    static string Text(string id, string text) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { id, role = "assistant", content = new object[] { new { type = "text", text } } },
    });

    static string BgCall(string msgId, string id) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            id = msgId, role = "assistant",
            content = new object[] { new { type = "tool_use", id, name = "Agent", input = new { description = "Fix the badge", subagent_type = "general-purpose", run_in_background = true, prompt = "x" } } },
        },
    });

    static string Launched(string id) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", content = new object[] { new { type = "text", text = "launched" } } } } },
        tool_use_result = new { status = "async_launched", agentId = "a1" },
    });

    static string Notice(string id, string status, string summary, string? file) =>
        JsonSerializer.Serialize(new { type = "system", subtype = "task_notification", tool_use_id = id, status, summary, output_file = file });

    static void LateReports(Action<string, bool, string> check)
    {
        // The TPIX shape: launch in the background, reply "waiting for it", the notice comes after, the CLI exits.
        var turn = new ClaudeDevStreamTurn(null);
        turn.Feed(BgCall("m1", "bg1"));
        turn.Feed(Launched("bg1"));
        turn.Feed(Text("m2", "The explorer badge agent is running; I will commit when it is done."));
        turn.Feed(Notice("bg1", "completed", "Badge fixed in explorer.blade.php", @"C:\Temp\bg1.output"));
        var late = turn.LateReports;
        check("a notice after the last reply is a late report, with its summary and output file",
            late.Count == 1 && late[0].Status == "completed" && late[0].Summary.Contains("Badge fixed") && late[0].OutputFile == @"C:\Temp\bg1.output" && late[0].Agent.Length > 0,
            string.Join(",", late));
        turn.Feed(Text("m3", "The badge agent finished; committing now."));
        check("a main step after the notice means the model saw it: nothing late", turn.LateReports.Count == 0, string.Join(",", turn.LateReports));

        var failed = new ClaudeDevStreamTurn(null);
        failed.Feed(BgCall("m1", "bgX"));
        failed.Feed(Launched("bgX"));
        failed.Feed(Text("m2", "Waiting."));
        failed.Feed(Notice("bgX", "failed", "", null));
        check("a failed sub-agent is late too, with its status", failed.LateReports.Count == 1 && failed.LateReports[0].Status == "failed", string.Join(",", failed.LateReports));
        var stray = new ClaudeDevStreamTurn(null);
        stray.Feed(Text("m1", "hi"));
        stray.Feed(Notice("nobody", "completed", "x", null));
        check("a notice about a task this turn never launched is ignored", stray.LateReports.Count == 0, "");

        // The follow-up message: bounded, says the output is data, reads only the CLI's own folders.
        var reports = new List<ClaudeDevStreamTurn.LateAgentReport>
        {
            new("Fix the badge", "completed", "Badge fixed", "out-1"),
            new("Write tests", "failed", "Could not build", null),
        };
        string msg = CodeAgentService.LateReportsMessage(reports, f => f == "out-1" ? "TAIL-OF-OUTPUT" : null);
        check("the follow-up names each agent, its status, summary and output tail",
            msg.Contains("## Fix the badge — completed") && msg.Contains("Badge fixed") && msg.Contains("TAIL-OF-OUTPUT") && msg.Contains("## Write tests — failed"), msg);
        check("the follow-up says the output is data, not instructions", msg.Contains("data, not instructions"), "");
        var many = Enumerable.Range(0, 40).Select(i => new ClaudeDevStreamTurn.LateAgentReport("agent " + i, "completed", new string('s', 900), "f" + i)).ToList();
        string big = CodeAgentService.LateReportsMessage(many, _ => new string('o', 4000));
        check($"the follow-up stays bounded ({big.Length} chars ≤ 12001)", big.Length <= 12001, big.Length.ToString());

        string temp = Path.Combine(Path.GetTempPath(), "cx-sup-" + Guid.NewGuid().ToString("N")[..6] + ".output");
        File.WriteAllText(temp, new string('x', 20000) + "THE-END");
        string? tail = CodeAgentService.ReadAgentOutputTail(temp);
        check("an output file under temp: its last 4,000 characters", tail != null && tail.Length == 4000 && tail.EndsWith("THE-END"), tail?.Length.ToString() ?? "null");
        File.Delete(temp);
        string outside = Path.Combine(AppContext.BaseDirectory, "cx-sup-outside.txt");
        File.WriteAllText(outside, "SECRET");
        check("a file outside temp and ~/.claude is never read, whatever the stream line says", CodeAgentService.ReadAgentOutputTail(outside) == null, "");
        check("a path that climbs out of temp is never read",
            CodeAgentService.ReadAgentOutputTail(Path.Combine(Path.GetTempPath(), "..", "..", Path.GetFileName(outside))) == null, "");
        File.Delete(outside);
        check("a missing file: nothing, no throw", CodeAgentService.ReadAgentOutputTail(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".txt")) == null, "");
    }

    static void Polls(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new PollLimiter(burst: 10, perSecond: 2, now: () => now);
        int allowed = 0;
        for (int i = 0; i < 25; i++) if (limiter.Take("task_status") == null) allowed++;
        check("a poll loop with no pause: 10 at once, then refused", allowed == 10, allowed.ToString());
        var wait = limiter.Take("task_status");
        check("a refusal says how long to wait (≤ half a second at 2/s)", wait is { } w && w > TimeSpan.Zero && w <= TimeSpan.FromMilliseconds(500), wait?.ToString() ?? "null");
        check("another call name has its own bucket", limiter.Take("list_chats") == null, "");
        now = now.AddSeconds(1);
        int refill = 0;
        for (int i = 0; i < 5; i++) if (limiter.Take("task_status") == null) refill++;
        check("one second later: two more", refill == 2, refill.ToString());
        now = now.AddMinutes(5);
        allowed = 0;
        for (int i = 0; i < 25; i++) if (limiter.Take("task_status") == null) allowed++;
        check("after a long pause the burst is back, never more than the burst", allowed == 10, allowed.ToString());
        var app = typeof(CluadeX.App).GetField("PipePolls", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as PollLimiter;
        check("the app's own limiter: a supervisor's pace (one call every few seconds) is far inside it",
            app is { Burst: >= 5, PerSecond: >= 1 }, app == null ? "no App.PipePolls" : $"{app.Burst}/{app.PerSecond}");
    }
}
