// Gemini Dev — the Gemini CLI as a CluadeX model (owner 2026-10-19: "พัฒนาให้ใช้กับ Gemini ได้ด้วย … ทำให้รองรับให้หมด").
// Its stream-json goes through GeminiStreamTranslator into Claude Code's shape and then through the SAME renderer as
// Claude Dev (ClaudeDevStreamTurn), so a Gemini Dev worker turn must come out with the same rows, plan, diffs and reply
// text. The event shapes are the CLI's own (gemini-cli 0.64 bundle: init / message{role,content,delta} /
// tool_use{tool_name,tool_id,parameters} / tool_result{tool_id,status,output,error} / error / result{status,error,stats}).
// The last part runs the real installed CLI once: on a machine where it is not signed in that costs nothing and proves
// the sign-in refusal arrives as a result event the provider explains.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class GeminiDevWire
{
    const string Folder = @"C:\work\demo";

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Msg(string text) => J(new { type = "message", timestamp = "t", role = "assistant", content = text, delta = true });
    static string Use(string id, string name, object parameters) => J(new { type = "tool_use", timestamp = "t", tool_name = name, tool_id = id, parameters });
    static string Ok(string id, string output) => J(new { type = "tool_result", timestamp = "t", tool_id = id, status = "success", output });
    static string Fail(string id, string message) => J(new { type = "tool_result", timestamp = "t", tool_id = id, status = "error", output = "", error = new { type = "TOOL_EXECUTION_ERROR", message } });

    const string FinalAnswer = "Done — the constant is fixed.\n\n```cs\nint x = 2;\n\nreturn x;\n```\n\nOne test still fails (tests/A.cs:3).";

    static readonly string[] Transcript =
    {
        J(new { type = "init", timestamp = "t", session_id = "s1", model = "gemini-3.8-flash" }),
        J(new { type = "message", timestamp = "t", role = "user", content = "fix the constant" }),
        Msg("I'll check the build"), Msg(" first.\n\nRunning"), Msg(" it now."),
        Use("t1", "run_shell_command", new { command = "dotnet build", description = "Build" }),
        Ok("t1", "Build succeeded."),
        Msg("The build passes. Fixing the constant."),
        Use("t2", "replace", new { file_path = Folder + @"\src\App.cs", old_string = "int x = 1;", new_string = "int x = 2;", instruction = "fix" }),
        Use("t3", "write_todos", new { todos = new object[] { new { description = "Fix the constant", status = "completed" }, new { description = "Run the tests", status = "in_progress" } } }),
        Ok("t2", "Successfully modified file."),
        Ok("t3", "Updated the todo list."),
        Use("t4", "mcp_brainx-brain_brain_search", new { query = "flaky timing" }),
        Ok("t4", "2 notes"),
        Use("t5", "run_shell_command", new { command = "dotnet test" }),
        Fail("t5", "Exit code 1\nFailed: 1, Passed: 41"),
        // The answer streams in pieces that cut through the code block (and its blank line).
        Msg("Done — the constant is fixed.\n\n```cs\nint x"), Msg(" = 2;\n\nreturn x;\n```\n\nOne test"), Msg(" still fails (tests/A.cs:3)."),
        J(new { type = "result", timestamp = "t", status = "success", stats = new { total_tokens = 900, input_tokens = 800, output_tokens = 100, cached = 0, input = 800, duration_ms = 5000, tool_calls = 5 } }),
    };

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Gemini Dev (Gemini CLI) → the same agent UI as Claude Dev");

        // ─── the turn, through the real renderer ───
        var tr = new GeminiStreamTranslator(new[] { "cluadex", GeminiDevProvider.BrainServerName });
        var turn = new ClaudeDevStreamTurn(Folder);
        var started = new List<ToolCall>();
        var done = new List<ToolResult>();
        var plans = new List<List<TodoItem>>();
        turn.ToolStarting += started.Add;
        turn.ToolExecuted += done.Add;
        turn.PlanChanged += p => plans.Add(p.ToList());
        foreach (var line in Transcript)
            foreach (var claudeLine in tr.Translate(line)) turn.Feed(claudeLine);
        foreach (var claudeLine in tr.Flush()) turn.Feed(claudeLine);

        check("gd-stream", "init: the model the CLI runs is known", tr.Model == "gemini-3.8-flash", tr.Model ?? "");
        check("gd-stream", "Gemini's tools become CluadeX rows: shell, edit, plan, MCP (mcp_server_tool → mcp__server__tool), shell",
            started.Select(c => c.ToolName).SequenceEqual(new[] { "run_command", "edit_file", "todo_write", "mcp__brainx-brain__brain_search", "run_command" }),
            string.Join(",", started.Select(c => c.ToolName)));
        check("gd-stream", "every started row gets its result", done.Count == started.Count, $"{done.Count}/{started.Count}");
        var bash = done[0];
        check("gd-stream", "run_shell_command → run_command row with its command and output",
            bash.Type == ToolType.RunCommand && bash.Success && bash.Arguments?.GetValueOrDefault("command") == "dotnet build" && bash.Output.Contains("Build succeeded"), bash.Output);
        var edit = done.First(r => r.ToolName == "edit_file");
        check("gd-stream", "replace → edit_file row: run-relative path (editor follows it) and a real diff",
            edit.Success && edit.FilePath == "src/App.cs" && edit.Diff is { Added: 1, Removed: 1 }, $"{edit.FilePath} · {edit.Diff?.Stat}");
        check("gd-stream", "write_todos → the run's plan (description → content, status kept)",
            plans.Count == 1 && turn.Plan.Count == 2 && turn.Plan[0].Content == "Fix the constant" && turn.Plan[0].Status == "completed"
            && turn.Plan[1].Status == "in_progress", string.Join(" | ", turn.Plan.Select(p => $"{p.Status}:{p.Content}")));
        var mcp = done.First(r => r.ToolName.StartsWith("mcp__"));
        check("gd-stream", "the MCP call keeps its arguments and output", mcp.Output == "2 notes" && mcp.Arguments?.GetValueOrDefault("query") == "flaky timing", mcp.Output);
        var fail = done.Last();
        check("gd-stream", "a failed tool_result is a failed row with the CLI's error text", !fail.Success && fail.Error.Contains("Failed: 1"), fail.Error);
        check("gd-stream", "one step per model turn (text + its tools; a new one after tool results)", turn.Steps.Count == 5, turn.Steps.Count.ToString());
        check("gd-stream", "streamed text is joined back exactly (deltas cut mid-sentence)",
            turn.Steps[0].ResponseText == "I'll check the build first.\n\nRunning it now.", turn.Steps[0].ResponseText ?? "");
        check("gd-stream", "the reply keeps its Markdown: a code block with a blank line inside survives the delta cuts",
            turn.Answer == FinalAnswer, turn.Answer.Replace("\n", "⏎"));
        check("gd-stream", "result success → the turn is done, not an error", turn.Done && !turn.ResultIsError, "");

        // A read-only turn streams plain text instead of rows: the same text, nothing lost or doubled.
        var plain = new GeminiStreamTranslator();
        var sb = new System.Text.StringBuilder();
        foreach (var line in Transcript) { plain.Translate(line); sb.Append(plain.TakeText()); }
        check("gd-stream", "a read-only turn's plain text is every assistant delta, once",
            sb.ToString().EndsWith(FinalAnswer) && sb.ToString().StartsWith("I'll check the build first.\n\nRunning it now."), sb.ToString().Length.ToString());

        // ─── refusals ───
        string authMsg = "Please set an Auth method in your C:\\Users\\x\\.gemini\\settings.json or specify one of the following environment variables before running: GEMINI_API_KEY, GOOGLE_GENAI_USE_VERTEXAI, GOOGLE_GENAI_USE_GCA";
        var refused = new GeminiStreamTranslator();
        var rturn = new ClaudeDevStreamTurn(Folder);
        foreach (var l in refused.Translate(J(new { type = "result", timestamp = "t", status = "error", error = new { type = "FatalAuthenticationError", message = authMsg }, stats = new { } })))
            rturn.Feed(l);
        check("gd-auth", "an error result reaches the renderer as an error with its text", rturn.ResultIsError && rturn.ResultText == authMsg && refused.ResultIsError, rturn.ResultText ?? "");
        string? help = GeminiDevProvider.AuthGuidance(authMsg, signedIn: false);
        check("gd-auth", "no sign-in → Thai steps: /login, Sign in with Google, Retry", help != null && help.Contains("/login") && help.Contains("Sign in with Google") && help.Contains("ลองใหม่"), help ?? "");
        string? quota = GeminiDevProvider.AuthGuidance("[API Error: 429 RESOURCE_EXHAUSTED quota exceeded]", signedIn: true);
        check("gd-auth", "signed in but out of quota → says it is the quota, not the sign-in", quota != null && quota.Contains("โควตา") && !quota.Contains("Sign in with Google"), quota ?? "");
        check("gd-auth", "an ordinary error is not mistaken for a sign-in problem", GeminiDevProvider.AuthGuidance("SyntaxError in the user's script", true) == null, "");

        // ─── arguments ───
        var advice = GeminiDevProvider.BuildArgs(Folder, null, "default", null);
        check("gd-args", "read-only: stream-json, --skip-trust, approval default (headless refuses every tool needing approval), no -m",
            advice.Contains("stream-json") && advice.Contains("--skip-trust") && Next(advice, "--approval-mode") == "default" && !advice.Contains("--model"),
            string.Join(" ", advice));
        var editor = GeminiDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, false), "pro", new[] { @"D:\lib" });
        check("gd-args", "edit grant → auto_edit, an explicit model goes as --model, allowed folders as --include-directories",
            Next(editor, "--approval-mode") == "auto_edit" && Next(editor, "--model") == "pro" && Next(editor, "--include-directories") == @"D:\lib", string.Join(" ", editor));
        var runner = GeminiDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, true), "auto", null);
        check("gd-args", "run grant → yolo (the guard hook is what stands between it and a dangerous command)", Next(runner, "--approval-mode") == "yolo" && !runner.Contains("--model"), string.Join(" ", runner));
        check("gd-args", "the prompt wrapper is the last argument after -p (appended to the conversation on stdin)",
            runner[^2] == "-p" && runner[^1].Contains("gemini-dev") && runner[^1].Contains(Folder), runner[^1]);

        // ─── the turn's settings: chat tools + the brain as MCP servers ───
        string bridge = J(new { mcpServers = new { cluadex = new { command = @"C:\x\CluadeX.exe", args = new[] { "--cx-tools" }, env = new { CLUADEX_TOOLS_TOKEN = "t" } } } });
        string brain = J(new { type = "stdio", command = @"C:\Users\x\.brainx\mcp\brainx-mcp.exe", args = new[] { @"G:\Obsidian" } });
        var settings = GeminiDevProvider.BuildSystemSettings(@"C:\x\CluadeX.exe", bridge, brain, guard: true);
        var servers = settings["mcpServers"]?.AsObject();
        check("gd-settings", "CluadeX's chat tools and the brain become the CLI's MCP servers (the brain without Claude's \"type\")",
            servers != null && servers["cluadex"]?["command"]?.GetValue<string>() == @"C:\x\CluadeX.exe"
            && servers[GeminiDevProvider.BrainServerName]?["command"] != null && servers[GeminiDevProvider.BrainServerName]?["type"] == null,
            settings.ToJsonString());

        // ─── finding the CLI ───
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\x\AppData\Roaming\npm\node_modules\@google\gemini-cli\bundle\gemini.js", @"C:\Program Files\nodejs\node.exe",
        };
        var found = GeminiDevProvider.FindCli(new[] { @"C:\Users\x\AppData\Roaming\npm\gemini.ps1" }, Array.Empty<string>(),
            @"C:\Users\x\AppData\Roaming", @"C:\Program Files", files.Contains);
        check("gd-find", "node + bundle\\gemini.js beside npm's shim; node from Program Files when not on PATH (never gemini.cmd)",
            found is { } f && f.Script.EndsWith(@"bundle\gemini.js") && f.Node == @"C:\Program Files\nodejs\node.exe", found?.ToString() ?? "null");
        check("gd-find", "no node → not found (a script alone cannot run)",
            GeminiDevProvider.FindCli(Array.Empty<string>(), Array.Empty<string>(), @"C:\Users\x\AppData\Roaming", @"C:\nowhere",
                p => p.EndsWith("gemini.js")) == null, "");

        await RealCliRefusal(check);
    }

    static string? Next(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    /// <summary>The installed CLI, headless, stream-json: on a machine where it is not signed in it refuses before any
    /// model call (free) — and that refusal must be a result event the translator reads as an auth error.</summary>
    static async Task RealCliRefusal(Action<string, string, bool, string> check)
    {
        if (!GeminiDevProvider.IsInstalled()) { check("gd-real", "the Gemini CLI is installed (skipped: not on this machine)", true, "skipped"); return; }
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        if (File.Exists(Path.Combine(home, "oauth_creds.json")) || Environment.GetEnvironmentVariable("GEMINI_API_KEY") != null)
        {
            check("gd-real", "the real CLI refusal (skipped: the CLI is signed in here — a real call would use the quota)", true, "skipped");
            return;
        }
        var provider = new GeminiDevProvider(null!, null!);
        await provider.InitializeAsync();
        if (provider.Cli is not { } cli) { check("gd-real", "the provider finds the installed CLI", false, provider.StatusMessage); return; }
        var (node, script) = cli;
        var psi = new ProcessStartInfo(node) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add(script);
        foreach (var a in GeminiDevProvider.BuildArgs(Path.GetTempPath(), null, "default", null)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync("say hi");
        p.StandardInput.Close();
        var tr = new GeminiStreamTranslator();
        string? line;
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while ((line = await p.StandardOutput.ReadLineAsync(cts.Token)) != null) { lines.Add(line); tr.Translate(line); }
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(cts.Token);
        string? refusal = tr.ResultText ?? err.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        check("gd-real", "the real, signed-out CLI refuses with a sign-in message the provider recognises",
            p.ExitCode != 0 && GeminiDevProvider.LooksLikeAuthRefusal(refusal),
            $"exit {p.ExitCode} · result={tr.ResultText} · stdout={string.Join(" ", lines).Replace('\n', ' ')[..Math.Min(300, string.Join(" ", lines).Length)]} · stderr={err.Replace('\n', ' ')[..Math.Min(300, err.Length)]}");
        check("gd-real", "…and the explanation tells the user how to sign in", GeminiDevProvider.AuthGuidance(refusal ?? "", false)?.Contains("/login") == true, refusal ?? "");
    }
}
