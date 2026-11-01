// OpenAI Dev — OpenAI's Codex CLI as a CluadeX model (owner 2026-10-20: "อยากให้เพิ่ม openai DEV คือติดต่อตรงผ่าน codex").
// `codex exec --json` goes through CodexStreamTranslator into Claude Code's shape and then through the SAME renderer as
// Claude Dev (ClaudeDevStreamTurn). The event shapes are the CLI's own, captured from codex-cli 0.163.0-alpha.10.2 on
// 2026-10-20 (paths moved to C:\work\demo): thread.started / turn.started / item.started|completed (agent_message,
// command_execution with the PowerShell wrapper Codex adds on Windows, file_change with paths only) / turn.completed /
// error + turn.failed (an API error wrapped as JSON text).
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class OpenAIDevWire
{
    const string Folder = @"C:\work\demo";
    const string Ps = "\"C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\" -Command ";

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Item(string kind, object item) => J(new { type = "item." + kind, item });

    static readonly string App = Folder + @"\src\App.cs";

    static readonly string[] Transcript =
    {
        J(new { type = "thread.started", thread_id = "01a15768-868a-79c1-8b3c-2c6e06ca4306" }),
        J(new { type = "turn.started" }),
        Item("completed", new { id = "item_0", type = "agent_message", text = "1. Read `src/App.cs`.\n2. Fix the constant.\n" }),
        Item("started", new { id = "item_1", type = "command_execution", command = Ps + "'Get-Content -Raw -LiteralPath src/App.cs'", aggregated_output = "", exit_code = (int?)null, status = "in_progress" }),
        Item("completed", new { id = "item_1", type = "command_execution", command = Ps + "'Get-Content -Raw -LiteralPath src/App.cs'", aggregated_output = "int x = 1;\r\n", exit_code = 0, status = "completed" }),
        Item("started", new { id = "item_2", type = "file_change", changes = new[] { new { path = App, kind = "update" } }, status = "in_progress" }),
        Item("completed", new { id = "item_2", type = "file_change", changes = new[] { new { path = App, kind = "update" } }, status = "completed" }),
        Item("completed", new { id = "item_3", type = "todo_list", items = new[] { new { text = "Fix the constant", completed = true }, new { text = "Run the tests", completed = false } } }),
        Item("started", new { id = "item_4", type = "command_execution", command = Ps + "'dotnet test'", aggregated_output = "", exit_code = (int?)null, status = "in_progress" }),
        Item("completed", new { id = "item_4", type = "command_execution", command = Ps + "'dotnet test'", aggregated_output = "Failed: 1, Passed: 41\r\n", exit_code = 1, status = "completed" }),
        Item("completed", new { id = "item_5", type = "mcp_tool_call", server = "brainx-brain", tool = "brain_search", arguments = new { query = "flaky timing" }, result = new { content = new[] { new { type = "text", text = "2 notes" } } }, status = "completed" }),
        Item("completed", new { id = "item_6", type = "agent_message", text = "Fixed the constant. One test still fails (tests/A.cs:3)." }),
        J(new { type = "turn.completed", usage = new { input_tokens = 85676, cached_input_tokens = 77440, output_tokens = 222 } }),
    };

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · OpenAI Dev (Codex CLI) → the same agent UI as Claude Dev");

        // ─── the turn, through the real renderer; the file reads as before at item.started, after at item.completed ───
        string appText = "int x = 1;\n";
        var tr = new CodexStreamTranslator(Folder, p => p == App ? appText : null);
        var turn = new ClaudeDevStreamTurn(Folder);
        var started = new List<ToolCall>();
        var done = new List<ToolResult>();
        turn.ToolStarting += started.Add;
        turn.ToolExecuted += done.Add;
        foreach (var line in Transcript)
        {
            if (line.Contains("\"item.completed\"") && line.Contains("file_change")) appText = "int x = 2;\n";   // Codex applied it
            foreach (var claudeLine in tr.Translate(line)) turn.Feed(claudeLine);
        }

        check("od-stream", "Codex's items become CluadeX rows: shell, file edit, plan, shell, MCP",
            started.Select(c => c.ToolName).SequenceEqual(new[] { "run_command", "write_file", "todo_write", "run_command", "mcp__brainx-brain__brain_search" }),
            string.Join(",", started.Select(c => c.ToolName)));
        check("od-stream", "every started row gets its result", done.Count == started.Count, $"{done.Count}/{started.Count}");
        var read = done[0];
        check("od-stream", "the PowerShell wrapper Codex adds is taken off: the row shows the command itself",
            read.Type == ToolType.RunCommand && read.Success && read.Arguments?.GetValueOrDefault("command") == "Get-Content -Raw -LiteralPath src/App.cs"
            && read.Output.Contains("int x = 1;"), read.Arguments?.GetValueOrDefault("command") ?? "");
        var edit = done.First(r => r.ToolName == "write_file");
        check("od-stream", "a file_change (paths only) → an edit row with the run-relative path and a REAL diff (before/after read around it)",
            edit.Success && edit.FilePath == "src/App.cs" && edit.Diff is { Added: 1, Removed: 1 }, $"{edit.FilePath} · {edit.Diff?.Stat}");
        check("od-stream", "todo_list → the run's plan (completed kept)",
            turn.Plan.Count == 2 && turn.Plan[0].Status == "completed" && turn.Plan[1].Status == "pending", string.Join(" | ", turn.Plan.Select(p => $"{p.Status}:{p.Content}")));
        var test = done.First(r => r.ToolName == "run_command" && r.Arguments?.GetValueOrDefault("command") == "dotnet test");
        check("od-stream", "a non-zero exit code is a failed row with its output", !test.Success && test.Error.Contains("Failed: 1"), test.Error);
        var mcp = done.First(r => r.ToolName.StartsWith("mcp__"));
        check("od-stream", "an MCP call keeps its arguments and its text result", mcp.Output == "2 notes" && mcp.Arguments?.GetValueOrDefault("query") == "flaky timing", mcp.Output);
        check("od-stream", "the reply is the last message; the turn is done, not an error",
            turn.Answer == "Fixed the constant. One test still fails (tests/A.cs:3)." && turn.Done && !turn.ResultIsError, turn.Answer);

        // A read-only turn streams plain text instead of rows.
        var plain = new CodexStreamTranslator(Folder);
        var sb = new System.Text.StringBuilder();
        foreach (var line in Transcript) { plain.Translate(line); sb.Append(plain.TakeText()); }
        check("od-stream", "a read-only turn's plain text is every agent message, once",
            sb.ToString().StartsWith("1. Read `src/App.cs`.") && sb.ToString().EndsWith("(tests/A.cs:3)."), sb.ToString());

        // A repeated key (Codex sent "id" twice, 2026-10-23/25) used to throw ArgumentException on first read and fail the turn.
        var dup = new CodexStreamTranslator(Folder);
        var dturn = new ClaudeDevStreamTurn(Folder);
        var drows = new List<ToolResult>();
        dturn.ToolExecuted += drows.Add;
        string dupCmd = "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\",\"id\":\"item_9\",\"command\":\"Get-Content README.md\",\"aggregated_output\":\"# AntiX\",\"exit_code\":0,\"status\":\"completed\"}}";
        string dupMsg = "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"id\":\"item_1\",\"type\":\"agent_message\",\"text\":\"done\"}}";
        Exception? thrown = null;
        try { foreach (var l in new[] { dupCmd, dupMsg, J(new { type = "turn.completed", usage = new { input_tokens = 10, output_tokens = 2 } }) }) foreach (var c in dup.Translate(l)) dturn.Feed(c); }
        catch (Exception ex) { thrown = ex; }
        check("od-stream", "a duplicate key in an item does not fail the turn: last value wins, the row and the reply still land",
            thrown == null && drows.Count == 1 && drows[0].Success && drows[0].Output.Contains("# AntiX") && dturn.Answer == "done" && dturn.Done && !dturn.ResultIsError,
            thrown?.Message ?? $"{drows.Count} rows · {dturn.Answer}");

        // ─── refusals ───
        var failed = new CodexStreamTranslator(Folder);
        var fturn = new ClaudeDevStreamTurn(Folder);
        string api = "{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'no-such-model' model is not supported when using Codex with a ChatGPT account.\"}}";
        foreach (var l in new[] { J(new { type = "turn.started" }), J(new { type = "error", message = api }), J(new { type = "turn.failed", error = new { message = api } }) })
            foreach (var c in failed.Translate(l)) fturn.Feed(c);
        check("od-auth", "turn.failed → an error result with the API's own words (not its JSON wrapper)",
            failed.ResultIsError && fturn.ResultIsError && failed.ResultText == "The 'no-such-model' model is not supported when using Codex with a ChatGPT account.",
            failed.ResultText ?? "");
        string? login = OpenAIDevProvider.AuthGuidance("Not logged in. Run `codex login`.", signedIn: false);
        check("od-auth", "no sign-in → Thai steps: /login, Sign in with ChatGPT, Retry",
            login != null && login.Contains("/login") && login.Contains("Sign in with ChatGPT") && login.Contains("ลองใหม่"), login ?? "");
        string? limit = OpenAIDevProvider.AuthGuidance("You've hit your usage limit. Try again later.", signedIn: true);
        check("od-auth", "signed in but the plan's limit is used up → says it is the limit, not the sign-in",
            limit != null && limit.Contains("โควตา") && !limit.Contains("Sign in with ChatGPT"), limit ?? "");
        check("od-auth", "an ordinary error is not mistaken for a sign-in problem", OpenAIDevProvider.AuthGuidance("SyntaxError in the user's script", true) == null, "");

        // ─── arguments: the sandbox comes from the grant, never a bypass ───
        var advice = OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null);
        check("od-args", "read-only: exec --json, sandbox read-only, no network, no -m, no effort, the prompt on stdin (\"-\" last)",
            advice[0] == "exec" && advice.Contains("--json") && Next(advice, "-s") == "read-only" && Next(advice, "-C") == Folder
            && advice.Contains("sandbox_workspace_write.network_access=false") && !advice.Contains("-m") && !advice.Any(a => a.StartsWith("model_reasoning_effort"))
            && advice[^1] == "-", string.Join(" ", advice));
        var editor = OpenAIDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, false), "gpt-6-sol", "high", new[] { @"D:\lib" });
        check("od-args", "edit grant → workspace-write without network; the model as -m, the effort as model_reasoning_effort, allowed folders as --add-dir",
            Next(editor, "-s") == "workspace-write" && editor.Contains("sandbox_workspace_write.network_access=false") && Next(editor, "-m") == "gpt-6-sol"
            && editor.Contains("model_reasoning_effort=\"high\"") && Next(editor, "--add-dir") == @"D:\lib", string.Join(" ", editor));
        var runner = OpenAIDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, true), "auto", "max", null);
        check("od-args", "run grant → workspace-write WITH network; never danger-full-access or the approval bypass",
            Next(runner, "-s") == "workspace-write" && runner.Contains("sandbox_workspace_write.network_access=true")
            && !runner.Any(a => a.Contains("danger", StringComparison.OrdinalIgnoreCase) || a.Contains("bypass", StringComparison.OrdinalIgnoreCase)),
            string.Join(" ", runner));

        // ─── CluadeX's chat tools as Codex MCP servers (-c overrides in TOML) ───
        string bridge = J(new { mcpServers = new { cluadex = new { command = @"C:\x\CluadeX.exe", args = new[] { "--cx-tools" }, env = new { CLUADEX_TOOLS_TOKEN = "t" } } } });
        var mcpArgs = OpenAIDevProvider.McpOverrides(bridge);
        check("od-mcp", "the chat tools become mcp_servers.cluadex with the Windows path as a TOML literal string, each tool approved (the app asks itself)",
            mcpArgs.Count == 1 && mcpArgs[0].StartsWith(@"mcp_servers.cluadex={command='C:\x\CluadeX.exe', args=['--cx-tools'], env={CLUADEX_TOOLS_TOKEN='t'}, "
                + "tools={new_chat={approval_mode='approve'}, open_project={approval_mode='approve'}, close_chat={approval_mode='approve'}, ask_user={approval_mode='approve'}, ")
            && CluadeX.Services.Browser.BrowserToolModule.ToolNames.All(n => mcpArgs[0].Contains(n + "={approval_mode='approve'}")), mcpArgs.FirstOrDefault() ?? "");

        // ─── the brain under approval_policy=never: the same tools Claude Dev may call unasked, nothing more ───
        static List<string> Approved(List<string> a) => a.Select((x, i) => (x, i)).Where(p => p.x == "-c" && p.i + 1 < a.Count
            && a[p.i + 1].StartsWith("mcp_servers.brainx-brain.tools.")).Select(p => a[p.i + 1]["mcp_servers.brainx-brain.tools.".Length..].Split('.')[0]).ToList();
        var readBrain = Approved(OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null, null, brainInCodexConfig: true));
        var editBrain = Approved(OpenAIDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, false), "default", "auto", null, null, brainInCodexConfig: true));
        check("od-brain", "read-only turn: recall + agent bus approved; no brain writes, no ssh_run / chrome",
            readBrain.Contains("brain_search") && readBrain.Contains("task_queue") && !readBrain.Contains("brain_remember")
            && !readBrain.Any(t => t.StartsWith("ssh") || t.StartsWith("chrome")), string.Join(",", readBrain));
        check("od-brain", "edit grant adds the brain's write tools (brain_remember…)", editBrain.Contains("brain_remember") && editBrain.Contains("brain_append_note"), string.Join(",", editBrain));
        check("od-brain", "brain not in Codex's config → no override at all (a key under an undefined server breaks config loading)",
            Approved(OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null)).Count == 0, "");
        check("od-brain", "config.toml check: [mcp_servers.brainx-brain] yes, its .env sub-table alone no, another server no",
            OpenAIDevProvider.CodexConfigHasServer("brainx-brain", "model = 'x'\n[mcp_servers.brainx-brain]\ncommand = 'b.exe'\n")
            && OpenAIDevProvider.CodexConfigHasServer("brainx-brain", "[mcp_servers.\"brainx-brain\"]\n")
            && !OpenAIDevProvider.CodexConfigHasServer("brainx-brain", "[mcp_servers.brainx-brain.env]\nA='1'\n")
            && !OpenAIDevProvider.CodexConfigHasServer("brainx-brain", "[mcp_servers.node_repl]\n"), "");
        check("od-mcp", "a quote inside a value switches to an escaped basic string", OpenAIDevProvider.Toml("it's") == "\"it's\"", OpenAIDevProvider.Toml("it's"));

        // ─── the prompt on stdin ───
        string prompt = OpenAIDevProvider.BuildPrompt(Folder, new ClaudeDevProvider.WorkerGrant(true, false), null, "User: hi\nAssistant: hello", "fix it");
        check("od-prompt", "wrapper first (edit allowed, commands read-only), then the conversation, then the request",
            prompt.StartsWith("[CluadeX] You are 'openai-dev'") && prompt.Contains("You may create and edit files") && prompt.Contains("Run only read-only commands")
            && prompt.Contains("<conversation_so_far>") && prompt.EndsWith("fix it"), prompt);

        // ─── finding the CLI ───
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\x\AppData\Local\OpenAI\Codex\bin\new\codex.exe", @"C:\Users\x\AppData\Local\OpenAI\Codex\bin\old\codex.exe",
            @"C:\Users\x\AppData\Roaming\npm\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe",
        };
        IEnumerable<string> Builds(string dir) => dir.EndsWith(@"Codex\bin", StringComparison.OrdinalIgnoreCase)
            ? new[] { Path.Combine(dir, "new"), Path.Combine(dir, "old") } : Array.Empty<string>();
        check("od-find", "npm's shim → the native codex.exe beside it (never the .cmd)",
            OpenAIDevProvider.FindCli(new[] { @"C:\Users\x\AppData\Roaming\npm\codex.cmd" }, @"C:\Users\x\AppData\Roaming", @"C:\Users\x\AppData\Local", files.Contains, Builds)
                == @"C:\Users\x\AppData\Roaming\npm\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe", "");
        files.Remove(@"C:\Users\x\AppData\Roaming\npm\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe");
        check("od-find", "no npm install → the Codex app's copy in its newest build folder",
            OpenAIDevProvider.FindCli(Array.Empty<string>(), @"C:\Users\x\AppData\Roaming", @"C:\Users\x\AppData\Local", files.Contains, Builds)
                == @"C:\Users\x\AppData\Local\OpenAI\Codex\bin\new\codex.exe", "");
        check("od-find", "nothing installed = not found",
            OpenAIDevProvider.FindCli(Array.Empty<string>(), @"C:\a", @"C:\b", _ => false, _ => Array.Empty<string>()) == null, "");
        return Task.CompletedTask;
    }

    static string? Next(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
