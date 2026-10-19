// Antigravity Dev — Google's Antigravity CLI (agy) as a CluadeX model: where personal Google accounts (Google AI Pro
// included) went when the Gemini CLI stopped serving them ("IneligibleTierError … migrate to Antigravity", 2026-06-18).
// Its stream-json goes through AntigravityStreamTranslator into Claude Code's shape and then through the SAME renderer as
// Claude Dev (ClaudeDevStreamTurn). The event shapes follow Google's headless docs and the CRT project's recordings of
// agy 1.2.7 on Windows: init{conversation_id, init{cwd,tools,permission_mode,model?}} / step_update{step_update{step_index,
// state ACTIVE|DONE|ERROR, step_type, text_delta?, tool_name?, tool_info{name,parameters,output?,error?}}} /
// result{result{status,response,error?,num_turns,usage}}. agy is not installed on the machine this was written on, so the
// last part (the real CLI) skips there.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class AntigravityDevWire
{
    const string Folder = @"C:\work\demo";
    const string Conv = "cf36dd87-7481-4f37-ab4f-6b84a61d3fca";

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Step(int i, string state, string type, object? extra = null)
    {
        var s = new JsonObject { ["conversation_id"] = Conv, ["step_index"] = i, ["state"] = state, ["step_type"] = type };
        if (extra != null) foreach (var kv in JsonSerializer.SerializeToNode(extra)!.AsObject().ToList()) s[kv.Key] = kv.Value?.DeepClone();
        return new JsonObject { ["event"] = "step_update", ["step_update"] = s }.ToJsonString();
    }
    static string Text(int i, string delta, bool done = false) => Step(i, done ? "DONE" : "ACTIVE", "agent_response", new { text_delta = delta });
    static string ToolOn(int i, string name, object parameters) => Step(i, "ACTIVE", "tool", new { tool_name = name, tool_info = new { name, parameters } });
    static string ToolOk(int i, string name, object parameters, string output) => Step(i, "DONE", "tool", new { tool_name = name, tool_info = new { name, parameters, output } });
    static string ToolErr(int i, string name, object parameters, string message) => Step(i, "ERROR", "tool", new { tool_name = name, tool_info = new { name, parameters, error = new { type = "TOOL_ERROR", message } } });

    const string FinalAnswer = "Done — the constant is fixed.\n\n```cs\nint x = 2;\n\nreturn x;\n```\n\nOne command was refused by the guard.";

    static readonly object Build = new { CommandLine = "dotnet build" };
    static readonly object Fix = new { TargetFile = Folder + @"\src\App.cs", TargetContent = "int x = 1;", ReplacementContent = "int x = 2;" };
    static readonly object NewFile = new { TargetFile = Folder + @"\NOTES.md", CodeContent = "# notes\n" };
    static readonly object Mcp = new { ServerName = "cluadex_" + GeminiDevProvider.BrainServerName, ToolName = "brain_search", Arguments = new { query = "flaky timing" } };
    static readonly object Push = new { CommandLine = "git push --force origin main" };

    static readonly string[] Transcript =
    {
        J(new { @event = "init", conversation_id = Conv, init = new { cwd = Folder, tools = new[] { "run_command", "view_file", "write_to_file" }, permission_mode = "always-proceed", model = "gemini-3.8-flash-high" } }),
        Step(0, "DONE", "user_input"),
        Text(1, "I'll check the build"), Text(1, " first.\n\nRunning"), Text(1, " it now.", done: true),
        ToolOn(2, "run_command", Build), ToolOk(2, "run_command", Build, "Build succeeded."),
        Text(3, "The build passes. Fixing the constant.", done: true),
        ToolOn(4, "replace_file_content", Fix), ToolOk(4, "replace_file_content", Fix, "Edited."),
        ToolOn(5, "write_to_file", NewFile), ToolOk(5, "write_to_file", NewFile, "Created."),
        ToolOn(6, "call_mcp_tool", Mcp), ToolOk(6, "call_mcp_tool", Mcp, "2 notes"),
        ToolOn(7, "run_command", Push),
        ToolErr(7, "run_command", Push, "tool call denied by pre-tool hook: CluadeX safety: `git push --force` is never run by a headless agent"),
        // The answer streams in pieces that cut through the code block (and its blank line).
        Text(8, "Done — the constant is fixed.\n\n```cs\nint x"), Text(8, " = 2;\n\nreturn x;\n```\n\nOne command"), Text(8, " was refused by the guard.", done: true),
        J(new { @event = "result", result = new { conversation_id = Conv, status = "SUCCESS", response = "…", duration_seconds = 26.1, num_turns = 1, usage = new { input_tokens = 69182, output_tokens = 1842, thinking_tokens = 1486, cache_read_tokens = 0, total_tokens = 71024 } } }),
    };

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Antigravity Dev (agy) → the same agent UI as Claude Dev");

        // ─── the turn, through the real renderer ───
        var tr = new AntigravityStreamTranslator(AntigravityDevProvider.PluginName, new[] { "cluadex", GeminiDevProvider.BrainServerName });
        var turn = new ClaudeDevStreamTurn(Folder);
        var started = new List<ToolCall>();
        var done = new List<ToolResult>();
        turn.ToolStarting += started.Add;
        turn.ToolExecuted += done.Add;
        bool startedAfterInit = true;
        for (int i = 0; i < Transcript.Length; i++)
        {
            foreach (var claudeLine in tr.Translate(Transcript[i])) turn.Feed(claudeLine);
            if (i == 1) startedAfterInit = tr.ModelStarted;   // init + user_input only
        }
        foreach (var claudeLine in tr.Flush()) turn.Feed(claudeLine);

        check("agy-stream", "init: the conversation and the model agy runs are known", tr.Model == "gemini-3.8-flash-high" && tr.ConversationId == Conv, tr.Model ?? "");
        check("agy-stream", "the model has not started at init + user_input (the guard-loaded check waits for its first step)", !startedAfterInit && tr.ModelStarted, "");
        check("agy-stream", "agy's tools become CluadeX rows: command, edit, write, MCP (cluadex_<server> → mcp__server__tool), command",
            started.Select(c => c.ToolName).SequenceEqual(new[] { "run_command", "edit_file", "write_file", "mcp__brainx-brain__brain_search", "run_command" }),
            string.Join(",", started.Select(c => c.ToolName)));
        check("agy-stream", "one row per tool step (ACTIVE starts it once, DONE/ERROR ends it once)", done.Count == started.Count && started.Count == 5, $"{done.Count}/{started.Count}");
        var bash = done[0];
        check("agy-stream", "run_command (CommandLine) → run_command row with its command and output",
            bash.Type == ToolType.RunCommand && bash.Success && bash.Arguments?.GetValueOrDefault("command") == "dotnet build" && bash.Output.Contains("Build succeeded"), bash.Output);
        var edit = done.First(r => r.ToolName == "edit_file");
        check("agy-stream", "replace_file_content (TargetFile/TargetContent/ReplacementContent) → edit_file row: run-relative path and a real diff",
            edit.Success && edit.FilePath == "src/App.cs" && edit.Diff is { Added: 1, Removed: 1 }, $"{edit.FilePath} · {edit.Diff?.Stat}");
        var write = done.First(r => r.ToolName == "write_file");
        check("agy-stream", "write_to_file (TargetFile/CodeContent) → write_file row on the right file", write.Success && write.FilePath == "NOTES.md", write.FilePath ?? "");
        var mcp = done.First(r => r.ToolName.StartsWith("mcp__"));
        check("agy-stream", "the MCP call keeps its Arguments and output", mcp.Output == "2 notes" && mcp.Arguments?.GetValueOrDefault("query") == "flaky timing", mcp.Output);
        var refused = done.Last();
        check("agy-stream", "a step the guard refused (state ERROR) is a failed row carrying the guard's reason",
            !refused.Success && refused.Error.Contains("never run by a headless agent"), refused.Error);
        check("agy-stream", "streamed text is joined back exactly (deltas cut mid-sentence)",
            turn.Steps[0].ResponseText == "I'll check the build first.\n\nRunning it now.", turn.Steps[0].ResponseText ?? "");
        check("agy-stream", "the reply keeps its Markdown: a code block with a blank line inside survives the delta cuts",
            turn.Answer == FinalAnswer, turn.Answer.Replace("\n", "⏎"));
        check("agy-stream", "result SUCCESS → the turn is done, not an error", turn.Done && !turn.ResultIsError && !tr.ResultIsError, "");

        // A read-only turn streams plain text instead of rows: the same text, nothing lost or doubled.
        var plain = new AntigravityStreamTranslator();
        var sb = new System.Text.StringBuilder();
        foreach (var line in Transcript) { plain.Translate(line); sb.Append(plain.TakeText()); }
        check("agy-stream", "a read-only turn's plain text is every agent_response delta, once",
            sb.ToString().EndsWith(FinalAnswer) && sb.ToString().StartsWith("I'll check the build first.\n\nRunning it now."), sb.ToString().Length.ToString());
        check("agy-stream", "noise is ignored (stderr text on stdout, bad JSON, unknown events)",
            new AntigravityStreamTranslator().Translate("warning: conversation \"x\" not found").Count == 0
            && new AntigravityStreamTranslator().Translate("{not json").Count == 0
            && new AntigravityStreamTranslator().Translate(J(new { @event = "bogus" })).Count == 0, "");

        // ─── refusals ───
        string notLoggedIn = "Print mode: not logged in and no controlling terminal; cannot complete interactive login";
        var rtr = new AntigravityStreamTranslator();
        var rturn = new ClaudeDevStreamTurn(Folder);
        foreach (var l in rtr.Translate(J(new { @event = "result", result = new { conversation_id = "", status = "ERROR", response = "", error = notLoggedIn, num_turns = 0 } })))
            rturn.Feed(l);
        check("agy-auth", "an ERROR result reaches the renderer as an error with agy's text", rturn.ResultIsError && rturn.ResultText == notLoggedIn && rtr.ResultIsError, rturn.ResultText ?? "");
        string? help = AntigravityDevProvider.AuthGuidance(notLoggedIn, signedIn: false);
        check("agy-auth", "not signed in → Thai steps: /login, the Google AI Pro account in the browser, Retry",
            help != null && help.Contains("/login") && help.Contains("Google AI Pro") && help.Contains("ลองใหม่") && !help.Contains("API key แทน"), help ?? "");
        check("agy-auth", "agy's other sign-in strings are recognised",
            AntigravityDevProvider.LooksLikeAuthRefusal("You are not logged into Antigravity")
            && AntigravityDevProvider.LooksLikeAuthRefusal("Print mode: auth timed out")
            && AntigravityDevProvider.LooksLikeAuthRefusal("error: authentication required"), "");
        string? quota = AntigravityDevProvider.AuthGuidance("AGY_ERROR: {\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}", signedIn: true);
        check("agy-auth", "signed in but out of the plan's quota → says it is the quota, not the sign-in", quota != null && quota.Contains("โควตา") && !quota.Contains("/login"), quota ?? "");
        check("agy-auth", "an ordinary error is not mistaken for a sign-in problem",
            AntigravityDevProvider.AuthGuidance("error: invalid model selection (--model \"x\")", true) == null
            && !AntigravityDevProvider.LooksLikeAuthRefusal("Build failed: 4291 warnings"), "");
        string stderr = "warning: conversation \"00000000\" not found\nWarning: something cosmetic\n" + notLoggedIn + "\n";
        check("agy-stderr", "stderr: agy's own line is picked over its warnings (GeminiDevProvider.LastMeaningfulLine rules)",
            GeminiDevProvider.LastMeaningfulLine(stderr) == notLoggedIn, GeminiDevProvider.LastMeaningfulLine(stderr) ?? "");
        check("agy-auth", "/usage answering SUCCESS = signed in; an auth error or garbage = not",
            AntigravityDevProvider.UsageSaysSignedIn("{\"conversation_id\":\"\",\"status\":\"SUCCESS\",\"response\":\"Gemini Models\\tWeekly Limit Remaining\\t99%\"}")
            && !AntigravityDevProvider.UsageSaysSignedIn("{\"status\":\"ERROR\",\"error\":\"" + notLoggedIn + "\"}")
            && !AntigravityDevProvider.UsageSaysSignedIn("Print mode: auth error"), "");

        // ─── arguments and stdin ───
        string turnDir = @"C:\Temp\cluadex-guard\agy-abc";
        var advice = AntigravityDevProvider.BuildArgs(turnDir, false, "default", null);
        check("agy-args", "stream-json both ways, --print with an EMPTY prompt (the turn is on stdin), the guard decides (skip-permissions), no --model",
            Next(advice, "--output-format") == "stream-json" && Next(advice, "--input-format") == "stream-json" && Next(advice, "--print") == ""
            && advice.Contains("--dangerously-skip-permissions") && !advice.Contains("--model") && !advice.Contains("-p") && !advice.Contains("--effort"),
            string.Join(" ", advice.Select(a => a.Length == 0 ? "\"\"" : a)));
        check("agy-args", "read-only turn: 10m print timeout; the plugin folder is the --add-dir", Next(advice, "--print-timeout") == "10m" && Next(advice, "--add-dir") == turnDir, "");
        var worker = AntigravityDevProvider.BuildArgs(turnDir, true, "gemini-3.8-flash-high", new[] { @"D:\lib" });
        check("agy-args", "worker: 90m timeout, explicit model as --model, the user's folder AND the plugin folder — the plugin last",
            Next(worker, "--print-timeout") == "90m" && Next(worker, "--model") == "gemini-3.8-flash-high"
            && worker[^2] == "--add-dir" && worker[^1] == turnDir && worker.Contains(@"D:\lib"), string.Join(" ", worker));
        string prompt = AntigravityDevProvider.BuildPrompt(Folder, null, null, "User: hi\nAssistant: hello", "explain App.cs");
        check("agy-args", "read-only prompt: conversation, the message, then CluadeX's read-only note",
            prompt.StartsWith("<conversation_so_far>") && prompt.IndexOf("explain App.cs") < prompt.IndexOf("[CluadeX]") && prompt.Contains("read-only") && prompt.Contains(Folder), prompt);
        string workerPrompt = AntigravityDevProvider.BuildPrompt(Folder, new ClaudeDevProvider.WorkerGrant(true, false), new[] { @"D:\lib" }, "", "fix it");
        check("agy-args", "edit-only prompt says it may edit, may not run commands, names the extra folder",
            workerPrompt.Contains("may create and edit") && workerPrompt.Contains("may not run commands") && workerPrompt.Contains(@"D:\lib"), workerPrompt);
        var stdinLine = JsonNode.Parse(AntigravityDevProvider.StdinLine("สวัสดี \"quoted\"\nline 2"))!;
        check("agy-args", "stdin: one {\"event\":\"user\",\"message\":{role,content}} line, text kept exactly",
            stdinLine["event"]!.GetValue<string>() == "user" && stdinLine["message"]!["role"]!.GetValue<string>() == "user"
            && stdinLine["message"]!["content"]!.GetValue<string>() == "สวัสดี \"quoted\"\nline 2" && !AntigravityDevProvider.StdinLine("a\nb").Contains('\n'), "");

        // ─── the turn's plugin (the guard + the MCP servers) ───
        string pluginDir = turnDir + @"\.agents\plugins\cluadex";
        string bridge = J(new { mcpServers = new { cluadex = new { type = "stdio", command = @"C:\x\CluadeX.exe", args = new[] { "--cx-tools" }, env = new { CLUADEX_TOOLS_TOKEN = "t" } } } });
        string brain = J(new { type = "stdio", command = @"C:\Users\x\.brainx\mcp\brainx-mcp.exe", args = new[] { @"G:\Obsidian" } });
        var files = AntigravityDevProvider.BuildPlugin(pluginDir, @"C:\PROGRA~1\CluadeX\CluadeX.exe", new ClaudeDevProvider.WorkerGrant(true, false), Folder, bridge, brain);
        var hooks = JsonNode.Parse(files["hooks.json"])!["cluadex-guard"]!;
        var pre = hooks["PreToolUse"]![0]!;
        check("agy-plugin", "plugin.json names the plugin (agy prefixes its MCP servers with it)",
            JsonNode.Parse(files["plugin.json"])!["name"]!.GetValue<string>() == AntigravityDevProvider.PluginName, files["plugin.json"]);
        check("agy-plugin", "PreToolUse on EVERY tool (matcher *) → the absolute guard.cmd, 300 s for the red prompt",
            pre["matcher"]!.GetValue<string>() == "*" && pre["hooks"]![0]!["command"]!.GetValue<string>() == pluginDir + @"\guard.cmd"
            && pre["hooks"]![0]!["timeout"]!.GetValue<int>() == 300, files["hooks.json"]);
        check("agy-plugin", "PreInvocation → loaded.cmd (the marker that proves agy loaded the hooks)",
            hooks["PreInvocation"]![0]!["command"]!.GetValue<string>() == pluginDir + @"\loaded.cmd", "");
        check("agy-plugin", "the .cmd wrappers: the exe quoted, --cx-agy-guard, the rest relative to the .cmd (%~dp0), ASCII, CRLF",
            files["guard.cmd"] == "@\"C:\\PROGRA~1\\CluadeX\\CluadeX.exe\" --cx-agy-guard --config \"%~dp0guard.json\"\r\n"
            && files["loaded.cmd"].Contains("--loaded \"%~dp0hooks.loaded\"") && files["guard.cmd"].All(c => c < 128), files["guard.cmd"]);
        var gcfg = JsonNode.Parse(files["guard.json"])!;
        check("agy-plugin", "guard.json carries the grant (edit yes, run no), the project and the plugin folder",
            gcfg["Edit"]!.GetValue<bool>() && !gcfg["Run"]!.GetValue<bool>() && gcfg["Cwd"]!.GetValue<string>() == Folder
            && gcfg["PluginDir"]!.GetValue<string>() == pluginDir, files["guard.json"]);
        var servers = JsonNode.Parse(files["mcp_config.json"])!["mcpServers"]!.AsObject();
        check("agy-plugin", "CluadeX's chat tools and the brain become the plugin's MCP servers (env kept, Claude's \"type\" dropped)",
            servers["cluadex"]?["env"]?["CLUADEX_TOOLS_TOKEN"]?.GetValue<string>() == "t" && servers["cluadex"]?["type"] == null
            && servers[GeminiDevProvider.BrainServerName]?["command"] != null && servers[GeminiDevProvider.BrainServerName]?["type"] == null,
            files["mcp_config.json"]);
        check("agy-plugin", "no servers → no mcp_config.json (still the guard)",
            AntigravityDevProvider.BuildPlugin(pluginDir, "x.exe", null, Folder, null, null) is var bare && !bare.ContainsKey("mcp_config.json") && bare.ContainsKey("hooks.json"), "");

        // ─── finding the CLI, its models ───
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\x\AppData\Local\agy\bin\agy.exe" };
        check("agy-find", "agy.exe from the installer's place when `where` finds nothing",
            AntigravityDevProvider.FindCli(Array.Empty<string>(), @"C:\Users\x\AppData\Local", have.Contains) == @"C:\Users\x\AppData\Local\agy\bin\agy.exe", "");
        check("agy-find", "a .cmd/.ps1 alias on PATH is never used (cmd.exe would re-parse the arguments)",
            AntigravityDevProvider.FindCli(new[] { @"C:\tools\agy.cmd" }, @"C:\nowhere", p => p.EndsWith(".cmd")) == null, "");
        check("agy-find", "not installed → null", AntigravityDevProvider.FindCli(Array.Empty<string>(), @"C:\nowhere", _ => false) == null, "");
        var models = AntigravityDevProvider.ParseModels("Fetching available models...\ngemini-3.8-flash-high\nclaude-opus-4-6-thinking  (Claude)\ngpt-oss-120b-medium\n\nDone.\n");
        check("agy-find", "`agy models` → the slugs only", models.SequenceEqual(new[] { "gemini-3.8-flash-high", "claude-opus-4-6-thinking", "gpt-oss-120b-medium" }), string.Join(",", models));

        await RealCli(check);
    }

    static string? Next(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    /// <summary>The installed agy, if any: `--version` answers. (A model turn is not run here — it would use the plan's
    /// quota, and a signed-out agy is the user's to fix.)</summary>
    static async Task RealCli(Action<string, string, bool, string> check)
    {
        var provider = new AntigravityDevProvider(null!, null!);
        await provider.InitializeAsync();
        if (provider.Exe is not { } exe) { check("agy-real", "agy is installed (skipped: not on this machine)", true, "skipped"); return; }
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--version");
        using var p = Process.Start(psi)!;
        string v = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        check("agy-real", "the installed agy answers --version", p.ExitCode == 0 && v.Length > 0, v);
    }
}
