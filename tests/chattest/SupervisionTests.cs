using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// What a supervisor driving CluadeX over the pipe could not see or got wrong (TPIX run, 2026-11-01), through
/// App.HandleMcpToolAsync with a scripted Claude Dev: the CLI's stream-json lines go into the real stream reader the
/// way the real provider sends them (ClaudeDevProvider.StreamSink), so the turn, its rows, its sub-agents and its end
/// are the shipping code's. No CLI runs and nothing is spent.
/// </summary>
internal static partial class Program
{
    static string SupProj => Path.Combine(PipeRoot, "sup-project");

    static async Task TestSupervision()
    {
        Log("\n## SUP — pipe supervision: late sub-agent results, the consent wait, progress, who started a turn, the dialog's truth");
        Directory.CreateDirectory(SupProj);
        File.WriteAllText(Path.Combine(SupProj, "readme.txt"), "hello");
        var perms = Sp.GetRequiredService<PermissionService>();
        var cli = new ScriptedCli();
        var before = Settings.Settings.ActiveProvider;
        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.ClaudeDev);
        SetField(Providers, "_activeProvider", cli);
        var confirm = Vm.Confirm;
        try
        {
            await SupLateReports(cli);
            await SupConsentWait(cli);
            await SupProgress(cli);
            await SupDialogTruth(cli, perms);
            await SupPollLimit();
        }
        finally
        {
            Vm.Confirm = confirm;
            Settings.UpdateSettings(s => s.ActiveProvider = before);
            SetField(Providers, "_activeProvider", Fake);
            perms.SetAcceptEdits(null);
            ConfirmAnswer = true;
        }
    }

    // ── stream-json lines, in the shape Claude Code 2.1.29x writes them ──
    static string CliText(string msgId, string text) => JsonSerializer.Serialize(new
    {
        type = "assistant", message = new { id = msgId, role = "assistant", content = new object[] { new { type = "text", text } } },
    });
    static string CliTool(string msgId, string id, string name, object input) => JsonSerializer.Serialize(new
    {
        type = "assistant", message = new { id = msgId, role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
    });
    static string CliBgAgent(string msgId, string id, string description) =>
        CliTool(msgId, id, "Agent", new { description, subagent_type = "general-purpose", run_in_background = true, prompt = "do it" });
    static string CliToolResult(string id, string text, object? structured = null) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", content = new object[] { new { type = "text", text } } } } },
        tool_use_result = structured,
    });
    static string CliLaunched(string id) => CliToolResult(id, "Async agent launched", new { status = "async_launched", agentId = "a-" + id });
    static string CliNotice(string id, string summary, string? file, string status = "completed") =>
        JsonSerializer.Serialize(new { type = "system", subtype = "task_notification", tool_use_id = id, status, summary, output_file = file });
    static string CliResult(string text) => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = text, num_turns = 3 });

    static string WriteCode(string taskId, bool? wait = null, bool acceptEdits = true) =>
        "{\"taskId\":" + Js(taskId) + ",\"spec\":\"Fix the explorer badge, then commit.\",\"workingDirectory\":" + Js(SupProj)
        + ",\"acceptEdits\":" + (acceptEdits ? "true" : "false") + (wait is { } w ? ",\"wait\":" + (w ? "true" : "false") : "") + "}";

    /// <summary>Gap 1: write_code said isError=false while a background sub-agent was still working, and its result —
    /// the real final report — was lost. Now the same chat goes on with the late results, once.</summary>
    static async Task SupLateReports(ScriptedCli cli)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "cx-sup-bg1-" + Guid.NewGuid().ToString("N")[..6] + ".output");
        File.WriteAllText(outFile, new string('.', 9000) + "\nOUTPUT-TAIL-MARK: committed 3f2a1c on main\n");
        try
        {
            cli.Reset();
            cli.Turns.Enqueue(new CliTurn([
                CliBgAgent("m1", "bg1", "Fix the explorer badge"),
                CliLaunched("bg1"),
                CliText("m2", "The badge agent is running in the background; I will commit when it is done."),
                CliNotice("bg1", "Badge fixed in explorer.blade.php", outFile),
                CliResult("The badge agent is running in the background; I will commit when it is done."),
            ]));
            cli.Turns.Enqueue(new CliTurn([
                CliText("m3", "Final report: the badge is fixed (explorer.blade.php) and committed as 3f2a1c."),
                CliResult("Final report: the badge is fixed (explorer.blade.php) and committed as 3f2a1c."),
            ]));
            ConfirmAnswer = true;
            var r = await Dispatch("cluadex.write_code", WriteCode("sup-late"));
            string second = cli.Messages.Count > 1 ? cli.Messages[1] : "";
            Check("SUP1 a sub-agent that ends after the reply: the same chat carries on with its result, and write_code returns THAT report, isError=false",
                !r.IsError && r.GetTextOutput().Contains("Final report") && cli.Messages.Count == 2, $"calls={cli.Messages.Count} {r.GetTextOutput()}");
            Check("SUP1 the follow-up turn is handed the sub-agent's summary and the end of its output (data, not instructions)",
                second.Contains("Badge fixed in explorer.blade.php") && second.Contains("OUTPUT-TAIL-MARK") && second.Contains("data, not instructions"), second.Length > 600 ? second[^600..] : second);
            var st = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-late\"}");
            Check("SUP1 task_status keeps the final report afterwards (callResult, callFailed=false)",
                !st.IsError && StatusOf(st).GetProperty("callResult").GetString()!.Contains("Final report") && !StatusOf(st).GetProperty("callFailed").GetBoolean(), st.GetTextOutput());

            // The follow-up leaves results unread too: no loop — one follow-up, then the caller is told.
            cli.Reset();
            for (int i = 1; i <= 2; i++)
                cli.Turns.Enqueue(new CliTurn([
                    CliBgAgent("m1", "bgL" + i, "Run the slow tests " + i),
                    CliLaunched("bgL" + i),
                    CliText("m2", "Tests are running in the background."),
                    CliNotice("bgL" + i, "Tests passed " + i, null),
                    CliResult("Tests are running in the background."),
                ]));
            cli.Turns.Enqueue(new CliTurn([CliText("m9", "should never be asked"), CliResult("x")]));
            var again = await Dispatch("cluadex.write_code", WriteCode("sup-late-again"));
            Check("SUP2 a follow-up that again ends before its sub-agents: one follow-up only, then isError naming what was not acted on",
                again.IsError && cli.Messages.Count == 2 && again.GetTextOutput().Contains("Tests passed 2"), $"calls={cli.Messages.Count} {again.GetTextOutput()}");
        }
        finally { try { File.Delete(outFile); } catch { } }
    }

    /// <summary>Gap 2: while CluadeX's Accept-edits dialog was up, the pipe showed nothing. wait:false hands back a handle,
    /// and task_status / list_chats say the task waits on the owner's approval.</summary>
    static async Task SupConsentWait(ScriptedCli cli)
    {
        string folder = Path.Combine(PipeRoot, "sup-consent-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        cli.Reset();
        cli.Turns.Enqueue(new CliTurn([CliText("m1", "Edited the file. Done."), CliResult("Edited the file. Done.")]));
        DispatcherFrame? held = null;
        int asked = 0;
        Vm.Confirm = (text, title) =>
        {
            asked++;
            held = new DispatcherFrame();
            Dispatcher.PushFrame(held);   // the owner has not answered yet
            return true;
        };
        string args = "{\"taskId\":\"sup-consent\",\"spec\":\"Edit it.\",\"workingDirectory\":" + Js(folder) + ",\"acceptEdits\":true,\"wait\":false}";
        var start = await Dispatch("cluadex.write_code", args);
        var handle = start.IsError ? default : StatusOf(start);
        Check("SUP3 write_code wait:false answers at once with a handle: taskId, status waiting_consent, how to poll",
            !start.IsError && asked == 1 && handle.GetProperty("taskId").GetString() == "sup-consent"
            && handle.GetProperty("status").GetString() == "waiting_consent" && handle.GetProperty("poll").GetString()!.Contains("task_status"), start.GetTextOutput());
        var st = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-consent\"}");
        var list = await Dispatch("cluadex.list_chats", "{\"query\":\"sup-consent\"}");
        Check("SUP3 task_status: waiting_consent, waiting for the owner's approval of Accept edits in that folder",
            !st.IsError && StatusOf(st).GetProperty("activity").GetString() == "waiting_consent"
            && StatusOf(st).GetProperty("waitingFor").GetString()!.Contains("owner's approval") && StatusOf(st).GetProperty("waitingFor").GetString()!.Contains(folder), st.GetTextOutput());
        Check("SUP3 list_chats lists the task while it waits, with what it waits on",
            !list.IsError && ChatsOf(list).Any(c => c.GetProperty("activity").GetString() == "waiting_consent" && c.GetProperty("waitingFor").GetString()!.Contains("owner's approval")), list.GetTextOutput());
        var dup = await Dispatch("cluadex.write_code", args);
        Check("SUP3 the same taskId again while it is going is refused (no second run)", dup.IsError && dup.GetTextOutput().Contains("still going"), dup.GetTextOutput());
        if (held != null) held.Continue = false;   // the owner says yes
        JsonElement done = default;
        bool ended = false;
        for (var until = DateTime.UtcNow.AddSeconds(20); !ended && DateTime.UtcNow < until; )
        {
            await Task.Delay(600);   // a supervisor's pace, inside the poll limit
            var s = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-consent\"}");
            if (s.IsError) continue;
            done = StatusOf(s);
            ended = done.TryGetProperty("callResult", out var cr) && cr.ValueKind == JsonValueKind.String;
        }
        Check("SUP3 once approved the run goes on without the caller; task_status hands over the final report",
            ended && done.GetProperty("callResult").GetString()!.Contains("Done") && !done.GetProperty("callFailed").GetBoolean() && cli.Messages.Count == 1,
            ended ? done.GetRawText() : "never ended");
        await Task.Delay(5200);   // the poll loop above spent the limiter: refill before the next checks
    }

    /// <summary>Gaps 3 and 4: a 47-minute run showed nothing on the pipe, and list_chats did not say who started a turn.
    /// Now: who, the last tool, elapsed, since the last progress, sub-agents running — names and counts only.</summary>
    static async Task SupProgress(ScriptedCli cli)
    {
        const string secret = "sk-ant-api03-QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";
        var gate = new SemaphoreSlim(0);
        cli.Reset();
        cli.Turns.Enqueue(new CliTurn([
            CliTool("m1", "r1", "Read", new { file_path = Path.Combine(SupProj, "readme.txt") }),
            CliToolResult("r1", "hello"),
            CliBgAgent("m2", "bgP", "Review the diff"),
            CliLaunched("bgP"),
            CliTool("m3", "b1", "Bash", new { command = "curl -H \"Authorization: Bearer " + secret + "\" https://example.test" }),
        ], gate, [
            CliToolResult("b1", "ok"),
            CliNotice("bgP", "Review: looks fine", null),
            CliText("m4", "All done: read, reviewed, checked."),
            CliResult("All done: read, reviewed, checked."),
        ]));
        var run = Dispatch("cluadex.write_code", WriteCode("sup-progress"));
        bool atGate = await WaitUntil(() => cli.AtGate, 20000);
        await Task.Delay(300);
        var list = await Dispatch("cluadex.list_chats", "{\"query\":\"sup-progress\"}");
        var st = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-progress\"}");
        var row = list.IsError ? default : ChatsOf(list).FirstOrDefault(c => c.GetProperty("running").GetBoolean());
        var s = st.IsError ? default : StatusOf(st);
        Check("SUP4 list_chats: who started the running turn (pipe), the last tool, step, elapsed, since the last progress, sub-agents running",
            atGate && row.ValueKind == JsonValueKind.Object && row.GetProperty("startedBy").GetString() == "pipe"
            && row.GetProperty("lastTool").GetString() is { Length: > 0 } && row.GetProperty("elapsedSeconds").GetDouble() >= 0
            && row.GetProperty("sinceProgressSeconds").GetDouble() >= 0 && row.GetProperty("subAgentsRunning").GetInt32() == 1, list.GetTextOutput());
        Check("SUP4 task_status: the same progress view, cheap to poll (answered from memory while it runs)",
            atGate && s.ValueKind == JsonValueKind.Object && s.GetProperty("startedBy").GetString() == "pipe" && s.GetProperty("lastTool").GetString() is { Length: > 0 }
            && s.GetProperty("elapsedSeconds").GetDouble() >= 0 && s.GetProperty("activity").GetString() == "running"
            && s.GetProperty("subAgents").EnumerateArray().Any(a => a.GetProperty("State").GetString() == "running"), st.GetTextOutput());
        string lastTool = row.ValueKind == JsonValueKind.Object ? row.GetProperty("lastTool").GetString() ?? "" : "";
        Check("SUP4 lastTool is a tool name only — never its arguments", !lastTool.Contains(' ') && !lastTool.Contains("curl") && !lastTool.Contains("Bearer"), lastTool);
        string doing = s.ValueKind == JsonValueKind.Object && s.GetProperty("doing").ValueKind == JsonValueKind.String ? s.GetProperty("doing").GetString()! : "";
        Log("      doing while the command runs: " + doing);
        Check("SUP4 a key in a running command never reaches the status calls (the line still says what runs, the key masked)",
            !list.GetTextOutput().Contains(secret) && !st.GetTextOutput().Contains(secret) && (!doing.Contains("Bearer") || doing.Contains("«redacted»")),
            list.GetTextOutput() + " / " + st.GetTextOutput());
        gate.Release();
        var r = await Task.WhenAny(run, Task.Delay(20000)) == run ? await run : null;
        Check("SUP4 the run ends normally with its report", r is { IsError: false } && r.GetTextOutput().Contains("All done"), r?.GetTextOutput() ?? "timed out");
        var after = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-progress\"}");
        Check("SUP4 after the run task_status still says who started it", !after.IsError && StatusOf(after).GetProperty("startedBy").GetString() == "pipe", after.GetTextOutput());

        // The owner's own turn: startedBy user.
        var gate2 = new SemaphoreSlim(0);
        cli.Reset();
        cli.Turns.Enqueue(new CliTurn([CliTool("u1", "r2", "Read", new { file_path = Path.Combine(SupProj, "readme.txt") })], gate2,
            [CliToolResult("r2", "hello"), CliText("u2", "Read it."), CliResult("Read it.")]));
        Vm.SetWorkingDirectory(SupProj);
        await FreshChat();
        string title = Vm.CurrentSession!.Title ?? "";
        string ownerChat = Vm.CurrentSession.Id;
        await Send("Read the readme please.");
        bool ownerAtGate = await WaitUntil(() => cli.AtGate, 20000);
        var mine = await Dispatch("cluadex.list_chats", "{\"limit\":50}");
        var ownerRow = mine.IsError ? default : ChatsOf(mine).FirstOrDefault(c => c.GetProperty("id").GetString() == ownerChat);
        Check("SUP5 a turn the owner typed: startedBy user", ownerAtGate && ownerRow.ValueKind == JsonValueKind.Object && ownerRow.GetProperty("running").GetBoolean()
            && ownerRow.GetProperty("startedBy").GetString() == "user", ownerRow.ValueKind == JsonValueKind.Object ? ownerRow.GetRawText() : mine.GetTextOutput());
        Check("SUP5 an idle chat carries no progress fields", ChatsOf(mine).Where(c => !c.GetProperty("running").GetBoolean()).All(c => c.GetProperty("startedBy").ValueKind == JsonValueKind.Null), "");
        gate2.Release();
        await WaitIdle();

        // A chat another chat's agent opened with new_chat: startedBy split_parent. A link store of its own (split limits).
        var links = ChatLinkStore.Current;
        ChatLinkStore.Current = new ChatLinkStore(Path.Combine(PipeRoot, "sup-links-" + Guid.NewGuid().ToString("N")[..6]));
        try
        {
            var gate3 = new SemaphoreSlim(0);
            cli.Reset();
            cli.Turns.Enqueue(new CliTurn([CliTool("s1", "r3", "Read", new { file_path = Path.Combine(SupProj, "readme.txt") })], gate3,
                [CliToolResult("r3", "hello"), CliText("s2", "Split done."), CliResult("Split done.")]));
            await CluadeX.Services.Tools.ProjectToolModule.NewChatHandler!(null, "Read the readme in a split chat.", "sup-split", false);
            bool splitAtGate = await WaitUntil(() => cli.AtGate, 20000);
            var split = await Dispatch("cluadex.list_chats", "{\"query\":\"sup-split\"}");
            var splitRow = split.IsError ? default : ChatsOf(split).FirstOrDefault(c => c.GetProperty("running").GetBoolean());
            Check("SUP5 a turn new_chat started from another chat: startedBy split_parent",
                splitAtGate && splitRow.ValueKind == JsonValueKind.Object && splitRow.GetProperty("startedBy").GetString() == "split_parent", split.GetTextOutput());
            gate3.Release();
            await WaitIdle();
        }
        finally { ChatLinkStore.Current = links; }
        await Task.Delay(5200);   // the limiter again
    }

    /// <summary>Gap 6: the Accept-edits dialog said every command would still ask, while the owner's own Allow rule for
    /// commands let them run without asking. The dialog now says what is true for that folder.</summary>
    static async Task SupDialogTruth(ScriptedCli cli, PermissionService perms)
    {
        var texts = new List<string>();
        Vm.Confirm = (text, title) => { texts.Add(text); return false; };
        async Task<string> Ask(string id)
        {
            string folder = Path.Combine(PipeRoot, id + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            cli.Reset();
            cli.Turns.Enqueue(new CliTurn([CliText("m1", "ok"), CliResult("ok")]));
            await Dispatch("cluadex.write_code", "{\"taskId\":" + Js(id) + ",\"spec\":\"x\",\"workingDirectory\":" + Js(folder) + ",\"acceptEdits\":true}");
            return folder;
        }
        string plain = await Ask("sup-dialog-plain");
        string runs = "";
        var rule = new PermissionRule { Pattern = "*", Scope = "*", ToolName = "run_command", Action = PermAction.Allow };
        perms.AddRule(rule);
        try { runs = await Ask("sup-dialog-runs"); }
        finally { perms.Rules.Remove(rule); perms.SaveRules(); }
        Check("SUP6 no Allow rule for commands: the usual dialog (commands still ask)",
            texts.Count == 2 && texts[0] == LocF("chat.acceptEdits.pipeConfirm", "sup-dialog-plain", plain), texts.FirstOrDefault() ?? "no dialog");
        Check("SUP6 with the owner's Allow rule for commands, the dialog says commands already run without asking and this answer does not change that",
            texts.Count == 2 && texts[1] == LocF("chat.acceptEdits.pipeConfirmRuns", "sup-dialog-runs", runs) && !texts[1].Contains("Commands, MCP tools"),
            texts.Count == 2 ? texts[1] : string.Join(" | ", texts));
    }

    /// <summary>A poll loop with no pause is slowed down, not served without end (the security review: every polling
    /// call is rate-limited).</summary>
    static async Task SupPollLimit()
    {
        var results = new List<McpToolResult>();
        for (int i = 0; i < 25; i++) results.Add(await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-progress\"}"));
        int refused = results.Count(r => r.IsError && r.GetTextOutput().Contains("too often"));
        Check("SUP7 25 task_status calls with no pause: the first ones answer, the rest are told to slow down (with how long)",
            refused >= 10 && !results[0].IsError && results.Last().GetTextOutput().Contains(" ms"), $"refused={refused} last={results.Last().GetTextOutput()}");
        await Task.Delay(5200);
        var later = await Dispatch("cluadex.task_status", "{\"taskId\":\"sup-progress\"}");
        Check("SUP7 after a pause it answers again", !later.IsError, later.GetTextOutput());
    }
}

/// <summary>One scripted CLI turn: lines before an optional gate, then the rest. <paramref name="Before"/> runs first, on
/// the turn's own flow (its run scope) — what a CLI's call to one of CluadeX's chat tools does (ChatToolBridge runs it
/// through AgentToolService.ExecuteToolAsync in that scope).</summary>
internal sealed record CliTurn(string[] Lines, SemaphoreSlim? Gate = null, string[]? After = null, Func<Task>? Before = null);

/// <summary>A Claude Dev stand-in: each turn's stream-json lines go to the real stream reader (StreamSink), exactly the
/// path the real provider takes when the turn is a worker turn. Records the message of every call.</summary>
internal sealed class ScriptedCli : IAiProvider
{
    public readonly Queue<CliTurn> Turns = new();
    public readonly List<string> Messages = new();
    /// <summary>The grant and the run's folder each call started with (ClaudeDevProvider.Grant, AgentRunScope).</summary>
    public readonly List<(ClaudeDevProvider.WorkerGrant? Grant, string? Folder)> Starts = new();
    public volatile bool AtGate;
    public void Reset() { lock (Turns) { Turns.Clear(); Messages.Clear(); Starts.Clear(); AtGate = false; } }
    public string ProviderId => "ClaudeDev";
    public string DisplayName => "Scripted Claude Dev";
    public bool IsReady => true;
    public bool IsLoading => false;
    public string StatusMessage => "ready";
#pragma warning disable CS0067
    public event Action<string> OnStatusChanged;
    public event Action<bool> OnLoadingChanged;
    public event Action<string> OnError;
#pragma warning restore CS0067
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<string> ChatAsync(List<ChatMessage> history, string userMessage, string systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CliTurn turn;
        lock (Turns)
        {
            Messages.Add(userMessage);
            Starts.Add((ClaudeDevProvider.Grant.Value, AgentRunScope.Current?.WorkingDirectory));
            turn = Turns.Count > 0 ? Turns.Dequeue() : new CliTurn([]);
        }
        if (turn.Before != null) await turn.Before();
        var sink = ClaudeDevProvider.StreamSink.Value;
        foreach (var l in turn.Lines) { await Task.Delay(15, ct); sink?.Invoke(l); }
        if (turn.Gate != null)
        {
            AtGate = true;
            await turn.Gate.WaitAsync(ct);
            AtGate = false;
            foreach (var l in turn.After ?? []) { await Task.Delay(15, ct); sink?.Invoke(l); }
        }
        // The real provider yields nothing on a worker turn: the stream reader is the reply.
        if (sink == null)
            foreach (var l in turn.Lines.Concat(turn.After ?? []))
                if (l.Contains("\"result\"") && JsonDocument.Parse(l).RootElement.GetProperty("type").GetString() == "result")
                    yield return JsonDocument.Parse(l).RootElement.GetProperty("result").GetString() ?? "";
    }

    public async Task<string> GenerateAsync(List<ChatMessage> history, string userMessage, string systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var t in ChatAsync(history, userMessage, systemPrompt, ct)) sb.Append(t);
        return sb.ToString();
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public void Dispose() { }
}
