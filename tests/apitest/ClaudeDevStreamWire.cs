// A Claude Dev worker turn must look and behave in the chat exactly like the API model's (owner, 2026-10-17: "ฉันยังไม่
// เห็นระบบเอเจนทำงานเลย" · "ต้องให้มันทำงานเหมือน api สิ"). The CLI's stream-json lines go through ClaudeDevStreamTurn,
// which raises the native loop's events: reply text per step, thinking, a tool row per call under CluadeX's own tool
// names (with result, diff and a run-relative path for the editor's live-follow), the plan, the sub-agent map, steps.
// Checked against a hand-written transcript that covers every event kind, and against a real CLI capture
// (fixtures/claude-dev-real.stream.jsonl — Claude Code 2.1.296, paths moved to C:\work\demo).
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class ClaudeDevStreamWire
{
    const string Folder = @"C:\work\demo";

    static string J(object o) => JsonSerializer.Serialize(o);
    static object Asst(string id, params object[] content) => new { type = "assistant", message = new { id, type = "message", role = "assistant", content }, parent_tool_use_id = (string)null };
    static object Result(string toolUseId, string content, bool isError = false, object structured = null) =>
        new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content, is_error = isError } } }, parent_tool_use_id = (string)null, tool_use_result = structured };

    /// <summary>Every event kind once: init, text, thinking, Bash ok, Edit with a diff, TodoWrite, a failing Bash, a
    /// sub-agent with its own tool, an MCP call, the hidden ToolSearch, the closing text, result.</summary>
    static readonly string[] Transcript =
    {
        J(new { type = "system", subtype = "init", cwd = Folder, session_id = "s1", model = "claude-opus-5-5" }),
        J(Asst("msg_A", new { type = "thinking", thinking = "Check the build first." })),
        J(Asst("msg_A", new { type = "text", text = "I'll build it first." })),
        J(Asst("msg_A", new { type = "tool_use", id = "tu_bash", name = "Bash", input = new { command = "dotnet build", description = "Build" } })),
        J(Result("tu_bash", "Build succeeded.\n    0 Warning(s)")),
        J(Asst("msg_B", new { type = "tool_use", id = "tu_edit", name = "Edit", input = new { file_path = Folder + @"\src\App.cs", old_string = "int x = 1;", new_string = "int x = 2;" } })),
        J(Asst("msg_B", new { type = "tool_use", id = "tu_todo", name = "TodoWrite", input = new { todos = new object[] {
            new { content = "Fix the constant", status = "completed", activeForm = "Fixing the constant" },
            new { content = "Run the tests", status = "in_progress", activeForm = "Running the tests" },
            new { content = "Commit", status = "pending", activeForm = "Committing" } } } })),
        J(Result("tu_edit", "The file " + Folder + @"\src\App.cs has been updated successfully.", structured:
            new { filePath = Folder + @"\src\App.cs", oldString = "int x = 1;", newString = "int x = 2;", originalFile = "class App {\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"// {i}\n")) + "int x = 1;\n}\n" })),
        J(Result("tu_todo", "Todos have been modified successfully.")),
        J(Asst("msg_C", new { type = "tool_use", id = "tu_fail", name = "Bash", input = new { command = "dotnet test" } })),
        J(Result("tu_fail", "Exit code 1\nFailed!  - Failed: 1, Passed: 41", isError: true)),
        J(Asst("msg_D", new { type = "tool_use", id = "tu_task", name = "Task", input = new { description = "Find the flaky test", prompt = "Look for…", subagent_type = "Explore" } })),
        J(new { type = "assistant", message = new { id = "msg_sub", content = new object[] { new { type = "tool_use", id = "tu_sub", name = "Grep", input = new { pattern = "Flaky", path = Folder + @"\tests" } } } }, parent_tool_use_id = "tu_task" }),
        J(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "tu_sub", content = "tests/A.cs:3" } } }, parent_tool_use_id = "tu_task" }),
        J(Result("tu_task", "The flaky one is tests/A.cs:3 (timing).")),
        J(Asst("msg_E", new { type = "tool_use", id = "tu_search", name = "ToolSearch", input = new { query = "select:x" } },
                        new { type = "tool_use", id = "tu_mcp", name = "mcp__brainx-brain__brain_search", input = new { query = "flaky timing" } })),
        J(new { type = "user", message = new { role = "user", content = new object[] {
            new { type = "tool_result", tool_use_id = "tu_search", content = new object[] { new { type = "tool_reference", tool_name = "x" } } },
            new { type = "tool_result", tool_use_id = "tu_mcp", content = new object[] { new { type = "text", text = "2 notes" } } } } }, parent_tool_use_id = (string)null }),
        J(Asst("msg_F", new { type = "text", text = "Fixed the constant; one test is still flaky (tests/A.cs:3)." })),
        J(new { type = "result", subtype = "success", is_error = false, result = "Fixed the constant; one test is still flaky (tests/A.cs:3).", total_cost_usd = 0.42, num_turns = 6 }),
    };

    sealed class Log
    {
        public readonly List<(string Text, int Step)> Text = new(), Thinking = new();
        public readonly List<int> Steps = new();
        public readonly List<ToolCall> Started = new();
        public readonly List<ToolResult> Done = new();
        public readonly List<List<TodoItem>> Plans = new();
        public readonly List<CodeAgentService.SubAgentEvent> Sub = new();

        public Log(ClaudeDevStreamTurn t)
        {
            t.Text += (s, n) => Text.Add((s, n));
            t.Thinking += (s, n) => Thinking.Add((s, n));
            t.StepStarted += Steps.Add;
            t.ToolStarting += Started.Add;
            t.ToolExecuted += Done.Add;
            t.PlanChanged += p => Plans.Add(p.ToList());
            t.SubAgent += Sub.Add;
        }
    }

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Claude Dev worker turn drives the native agent UI (stream-json → events)");

        // ─── the tool map ───
        var empty = JsonDocument.Parse("{}").RootElement;
        string NameOf(string cli) => ClaudeDevToolMap.Map(cli, empty, Folder).Name;
        var expect = new (string Cli, string Name)[]
        {
            ("Bash", "run_command"), ("Read", "read_file"), ("Write", "write_file"), ("Edit", "edit_file"), ("MultiEdit", "multi_edit"),
            ("NotebookEdit", "notebook_edit"), ("Grep", "grep"), ("Glob", "glob"), ("LS", "list_files"), ("TodoWrite", "todo_write"),
            ("TaskCreate", "todo_write"), ("TaskUpdate", "todo_write"), ("WebFetch", "web_fetch"), ("WebSearch", "web_search"),
            ("Task", "agent_spawn"), ("Agent", "agent_spawn"), ("mcp__brainx-brain__brain_search", "mcp__brainx-brain__brain_search"),
        };
        var wrong = expect.Where(e => NameOf(e.Cli) != e.Name).Select(e => $"{e.Cli}→{NameOf(e.Cli)}").ToList();
        check("cd-stream", "every Claude Code tool maps to the CluadeX tool its rows know", wrong.Count == 0, string.Join(", ", wrong));
        var read = ClaudeDevToolMap.Map("Read", JsonDocument.Parse("{\"file_path\":\"C:\\\\work\\\\demo\\\\src\\\\A.cs\",\"limit\":40}").RootElement, Folder);
        var outside = ClaudeDevToolMap.Map("Edit", JsonDocument.Parse("{\"file_path\":\"D:\\\\other\\\\B.cs\"}").RootElement, Folder);
        check("cd-stream", "a path inside the run folder becomes relative, one outside stays absolute",
            read.Args.GetValueOrDefault("path") == "src/A.cs" && read.Args.GetValueOrDefault("limit") == "40" && read.FilePath == null
            && outside.FilePath == @"D:\other\B.cs", $"{read.Args.GetValueOrDefault("path")} · {outside.FilePath}");
        check("cd-stream", "ToolSearch (the CLI loading schemas) is hidden", ClaudeDevToolMap.Map("ToolSearch", empty, Folder).Hidden, "");

        // ─── the hand-written transcript ───
        var turn = new ClaudeDevStreamTurn(Folder);
        var log = new Log(turn);
        foreach (var line in Transcript) turn.Feed(line);

        check("cd-stream", "one step per assistant message (6), numbered in order, announced as they start",
            turn.Steps.Count == 6 && log.Steps.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 }), string.Join(",", log.Steps));
        check("cd-stream", "reply text streams on its own step (a new bubble after the tools)",
            log.Text.Count == 2 && log.Text[0] == ("I'll build it first.", 1) && log.Text[1].Step == 6, string.Join(" | ", log.Text));
        check("cd-stream", "thinking reaches the thinking block of its step",
            log.Thinking.Count == 1 && log.Thinking[0] == ("Check the build first.", 1) && turn.Steps[0].ThinkingText == "Check the build first.", "");
        check("cd-stream", "a tool row starts at tool_use, under CluadeX's names, the sub-agent's own Grep and ToolSearch not among them",
            log.Started.Select(c => c.ToolName).SequenceEqual(new[] { "run_command", "edit_file", "todo_write", "run_command", "agent_spawn", "mcp__brainx-brain__brain_search" }),
            string.Join(",", log.Started.Select(c => c.ToolName)));
        check("cd-stream", "every started row gets its result (same count, ids matched)", log.Done.Count == log.Started.Count, $"{log.Done.Count}/{log.Started.Count}");

        var bash = log.Done.First(r => r.ToolName == "run_command");
        check("cd-stream", "Bash → run_command row: command argument, output, success",
            bash.Type == ToolType.RunCommand && bash.Success && bash.Arguments?.GetValueOrDefault("command") == "dotnet build" && bash.Output.Contains("Build succeeded"), bash.Output);
        var edit = log.Done.First(r => r.ToolName == "edit_file");
        check("cd-stream", "Edit → edit_file row with the run-relative FilePath (editor live-follow) and a real diff",
            edit.Type == ToolType.EditFile && edit.Success && edit.FilePath == "src/App.cs"
            && edit.Diff is { Added: 1, Removed: 1, FirstChangedLine: > 1 },$"{edit.FilePath} · {edit.Diff?.Stat} · line {edit.Diff?.FirstChangedLine}");
        var fail = log.Done.Where(r => r.ToolName == "run_command").Skip(1).First();
        check("cd-stream", "an is_error tool_result is a failed row with its error text",
            !fail.Success && fail.Error.Contains("Exit code 1") && fail.Error.Contains("Failed: 1"), fail.Error);
        var mcp = log.Done.First(r => r.ToolName.StartsWith("mcp__"));
        check("cd-stream", "an MCP call keeps mcp__server__tool (its server's row), text blocks become the output",
            mcp.Type == ToolType.McpTool && mcp.Output == "2 notes" && mcp.Arguments?.GetValueOrDefault("query") == "flaky timing", mcp.Output);

        var plan = turn.Plan;
        check("cd-stream", "TodoWrite → the run's plan (content + status), raised for the plan bar",
            log.Plans.Count == 1 && plan.Count == 3 && plan[0].Status == "completed" && plan[1].Status == "in_progress"
            && plan[2].Content == "Commit" && plan[2].Status == "pending", string.Join(" | ", plan.Select(p => $"{p.Status}:{p.Content}")));
        check("cd-stream", "the todo row says how far the plan is", log.Done.First(r => r.ToolName == "todo_write").Summary == "Plan: 1/3", "");

        check("cd-stream", "a Task call opens the sub-agent map: start → its own tool → done with its report",
            log.Sub.Select(e => e.Phase).SequenceEqual(new[] { "start", "tool", "done" })
            && log.Sub[0].Task == "Find the flaky test" && log.Sub[0].Kind == "explore"
            && log.Sub[1].Detail.StartsWith("grep Flaky") && log.Sub[1].Steps == 1 && log.Sub[2].Detail.Contains("tests/A.cs:3"),
            string.Join(" | ", log.Sub.Select(e => $"{e.Phase}:{e.Detail}")));

        check("cd-stream", "steps carry their calls and results (brain-save, cost summary, handoff see the work)",
            turn.Steps.Sum(s => s.ToolCalls.Count) == 6 && turn.Steps.Sum(s => s.ToolResults.Count) == 6
            && turn.Steps[1].ToolResults.Any(r => r.FilePath == "src/App.cs"), $"{turn.Steps.Sum(s => s.ToolCalls.Count)} calls");
        check("cd-stream", "the final reply is the model's own last text — no ⏵ quotes",
            turn.FinalText == "Fixed the constant; one test is still flaky (tests/A.cs:3)." && !turn.FinalText.Contains('⏵'), turn.FinalText);
        check("cd-stream", "result: done, not an error, cost and turns read",
            turn.Done && !turn.ResultIsError && turn.CostUsd == 0.42m && turn.NumTurns == 6 && turn.HasOutput, "");

        // ─── an error result after work, and a turn that never spoke ───
        var limited = new ClaudeDevStreamTurn(Folder);
        limited.Feed(Transcript[3]);
        limited.Feed(J(new { type = "result", subtype = "error_during_execution", is_error = true, result = "Claude AI usage limit reached" }));
        check("cd-stream", "an error result is caught; no text was written so the reply is empty (the loop adds the ⚠ line)",
            limited.Done && limited.ResultIsError && limited.ResultText == "Claude AI usage limit reached" && limited.FinalText == "" && limited.HasOutput, "");
        var silent = new ClaudeDevStreamTurn(Folder);
        silent.Feed("not json"); silent.Feed("{\"type\":\"rate_limit_event\"}"); silent.Feed("{\"type\":\"result\",\"is_error\":false,\"result\":\"ok\"}");
        check("cd-stream", "junk lines are ignored; a turn with no message falls back to the result text",
            !silent.HasOutput && silent.Steps.Count == 0 && silent.FinalText == "ok", silent.FinalText);

        // ─── the Stop hook's closing remark (2026-10-17: the pipe got "saved to the brain" instead of the summary) ───
        string summary = "Done. Changed src/App.cs (x = 2) and src/Lib.cs (null check); dotnet build is green and all 42 tests pass. "
            + "The flaky test in tests/A.cs:3 is timing-dependent and left as it was.";
        var hooked = new ClaudeDevStreamTurn(Folder);
        foreach (var line in new[]
        {
            J(Asst("msg_1", new { type = "tool_use", id = "tu_b", name = "Bash", input = new { command = "dotnet build" } })),
            J(Result("tu_b", "Build succeeded.")),
            J(Asst("msg_2", new { type = "text", text = summary })),
            J(Asst("msg_3", new { type = "tool_use", id = "tu_note", name = "mcp__brainx-brain__brain_create_note", input = new { title = "fix" } })),
            J(Result("tu_note", "saved")),
            J(Asst("msg_4", new { type = "text", text = "Saved the lesson to the brain." })),
            J(new { type = "result", subtype = "success", is_error = false, result = "Saved the lesson to the brain." }),
        }) hooked.Feed(line);
        check("cd-stream", "a short closing remark after the real summary: the answer keeps the summary, the remark follows it",
            hooked.FinalText == "Saved the lesson to the brain." && hooked.Answer == summary + "\n\nSaved the lesson to the brain.", hooked.Answer);
        check("cd-stream", "a closing answer that is the real one (longer than the narration before it) stands alone",
            turn.Answer == turn.FinalText, turn.Answer);

        // ─── a real capture from the CLI ───
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "claude-dev-real.stream.jsonl");
        if (!File.Exists(fixture)) { check("cd-stream", "real CLI capture present", false, fixture); return Task.CompletedTask; }
        var real = new ClaudeDevStreamTurn(Folder);
        var rlog = new Log(real);
        foreach (var line in File.ReadLines(fixture)) real.Feed(line);
        check("cd-stream", "real capture: 7 steps (one per API message id), 8 calls shown, ToolSearch hidden",
            real.Steps.Count == 7 && rlog.Started.Count == 8 && rlog.Done.Count == 8 && rlog.Started.All(c => c.ToolName != "tool_search"),
            $"steps {real.Steps.Count} · started {rlog.Started.Count} · done {rlog.Done.Count}");
        check("cd-stream", "real capture: TaskCreate/TaskUpdate (the newer CLI's plan tools) drive the plan to 2/2 done",
            real.Plan.Count == 2 && real.Plan.All(p => p.Status == "completed") && real.Plan[0].Content == "Create and edit hello.txt"
            && rlog.Plans.Count == 4, string.Join(" | ", real.Plan.Select(p => $"{p.Status}:{p.Content}")));
        var rWrite = rlog.Done.FirstOrDefault(r => r.ToolName == "write_file");
        var rEdit = rlog.Done.FirstOrDefault(r => r.ToolName == "edit_file");
        check("cd-stream", "real capture: Write and Edit rows point the editor at hello.txt, with diffs",
            rWrite is { Success: true, FilePath: "hello.txt", Diff.Added: 1 } && rEdit is { Success: true, FilePath: "hello.txt", Diff: { Added: 1, Removed: 1 } },
            $"{rWrite?.FilePath} {rWrite?.Diff?.Stat} · {rEdit?.FilePath} {rEdit?.Diff?.Stat}");
        var rFail = rlog.Done.FirstOrDefault(r => r.ToolName == "run_command" && !r.Success);
        check("cd-stream", "real capture: the failing `cat missing.txt` is a failed row; the good one shows its output",
            rFail != null && rFail.Error.Contains("No such file") && rlog.Done.Any(r => r.ToolName == "run_command" && r.Success && r.Output == "hello"), rFail?.Error ?? "");
        check("cd-stream", "real capture: redacted (empty) thinking shows nothing; reply = the last message's text, matching the result",
            rlog.Thinking.Count == 0 && real.FinalText.Length > 0 && real.FinalText == real.ResultText?.Trim() && !real.FinalText.Contains('⏵'), real.FinalText);
        check("cd-stream", "real capture: the older text-only renderer still reads the same lines (fallback kept)",
            File.ReadLines(fixture).Count(l => ClaudeDevProvider.RenderStreamLine(l, out _, out _)?.Contains("⏵ Bash") == true) == 2, "");
        return Task.CompletedTask;
    }
}
