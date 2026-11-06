// Grok Dev — xAI's Grok Build CLI as a CluadeX model (owner 2026-11-06: "อย่าลืมเชื่อมกับ grok build desktop dev ได้ด้วยนะ").
// `grok --prompt-file … --output-format streaming-messages-json` is Claude Code's stream-json (docs.x.ai/build/cli/
// headless-scripting), so GrokStreamTranslator only renames Grok's tools and the SAME renderer as Claude Dev draws the turn.
// The transcript below is built from that documented shape with Grok Build 1.0.70's tool ids (read_file, search_replace,
// run_terminal_cmd, list_dir, grep, todo_write) — not captured from a signed-in run: the dev machine's CLI is not signed in,
// and signing in is the owner's click, never this harness's. No process is started here.
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class GrokDevWire
{
    const string Folder = @"C:\work\demo";

    static string J(object o) => JsonSerializer.Serialize(o);
    static string ToolUse(string id, string name, object input) =>
        J(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } } });
    static string ToolResult(string id, string text, bool error = false) =>
        J(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = text, is_error = error } } } });

    static readonly string[] Transcript =
    {
        J(new { type = "system", subtype = "init", model = "grok-4.7", cwd = Folder, session_id = "s1", tools = new[] { "read_file", "search_replace" } }),
        J(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "Let me look." } } } }),
        ToolUse("t1", "list_dir", new { target_directory = "src" }),
        ToolResult("t1", "App.cs"),
        ToolUse("t2", "read_file", new { target_file = Folder + @"\src\App.cs" }),
        ToolResult("t2", "int x = 1;"),
        ToolUse("t3", "grep", new { query = "x = 1", path = "src" }),
        ToolResult("t3", "src/App.cs:1"),
        ToolUse("t4", "search_replace", new { file_path = Folder + @"\src\App.cs", old_string = "x = 1", new_string = "x = 2" }),
        ToolResult("t4", "ok"),
        ToolUse("t5", "todo_write", new { todos = new[] { new { content = "Fix the constant", status = "completed", activeForm = "Fixing" } } }),
        ToolResult("t5", "ok"),
        ToolUse("t6", "run_terminal_cmd", new { command = "dotnet test", explanation = "run the tests" }),
        ToolResult("t6", "Passed: 42"),
        J(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "Fixed the constant; tests pass." } } } }),
        J(new { type = "result", subtype = "success", is_error = false, result = "Fixed the constant; tests pass.",
            usage = new { input_tokens = 1000, output_tokens = 1000, cache_read_input_tokens = 0 } }),
    };

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Grok Dev (Grok Build CLI) → the same agent UI as Claude Dev");

        // ─── the turn, through the real renderer ───
        var tr = new GrokStreamTranslator();
        var turn = new ClaudeDevStreamTurn(Folder);
        var started = new List<ToolCall>();
        turn.ToolStarting += started.Add;
        foreach (var line in Transcript)
            if (tr.Translate(line) is { } claudeLine) turn.Feed(claudeLine);
        string names = string.Join(",", started.Select(c => c.ToolName));
        check("gd-stream", "Grok's tools become CluadeX rows: list, read, grep, edit, plan, shell",
            names == "list_files,read_file,grep,edit_file,todo_write,run_command", names);
        var edit = started.FirstOrDefault(c => c.ToolName == "edit_file");
        var read = started.FirstOrDefault(c => c.ToolName == "read_file");
        check("gd-stream", "Grok's argument names (target_file, query) read as the renderer's (file_path, pattern)",
            read?.Arguments.GetValueOrDefault("path")?.Contains("App.cs") == true || read?.Arguments.Values.Any(v => v.Contains("App.cs")) == true,
            read == null ? "no read row" : string.Join(";", read.Arguments.Select(kv => kv.Key + "=" + kv.Value)));
        check("gd-stream", "the edit row carries its file", edit?.Arguments.Values.Any(v => v.Contains("App.cs")) == true,
            edit == null ? "no edit row" : string.Join(";", edit.Arguments.Select(kv => kv.Key + "=" + kv.Value)));
        string text = tr.TakeText();
        check("gd-stream", "the plain chat text has the answer, and the system line (its model, its tools) is not in it",
            text.Contains("Fixed the constant") && !text.Contains("session_id") && tr.HasOutput && !tr.ResultIsError, text);
        var bad = new GrokStreamTranslator();
        bad.Translate(J(new { type = "result", subtype = "error", is_error = true, result = "You are not authenticated. Run `grok login`." }));
        check("gd-stream", "a result line with is_error is flagged, its text kept for the refusal check",
            bad.ResultIsError && bad.ResultText?.Contains("not authenticated") == true && GrokDevProvider.LooksLikeAuthRefusal(bad.ResultText), bad.ResultText ?? "");
        check("gd-stream", "a line that is not JSON passes nothing on", tr.Translate("Loading grok…") == null && tr.Translate("") == null, "");
        var (rn, ri) = GrokStreamTranslator.Rename("hashline_edit", new System.Text.Json.Nodes.JsonObject { ["path"] = "a.cs" });
        var (un, _) = GrokStreamTranslator.Rename("some_new_tool", null);
        check("gd-stream", "hashline_edit is an edit too; an unknown tool passes through unchanged",
            rn == "Edit" && ri?["file_path"]?.GetValue<string>() == "a.cs" && un == "some_new_tool", rn);

        // ─── cost: the CLI's own usage, at grok-4.7's price, on the GrokDev tab ───
        var usage = new DelegatedUsageEstimate();
        foreach (var line in Transcript) usage.Feed(line);
        var est = usage.Estimate("GrokDev", "", "");
        check("gd-cost", "usage from the result line, model from the init line: 1K in + 1K out at $2 / $6 per M = $0.008",
            usage.Model == "grok-4.7" && est.Input == 1000 && est.Output == 1000 && est.EquivalentUsd == 0.008m && est.Method == "cli-tokens",
            $"model={usage.Model} {est}");
        var noModel = new DelegatedUsageEstimate();
        noModel.Feed(Transcript[^1]);
        check("gd-cost", "no model named → Grok Build's default (grok-4.7) prices it, never a Gemini fallback",
            noModel.Estimate("GrokDev", "", "").EquivalentUsd == 0.008m, noModel.Estimate("GrokDev", "", "").ToString());

        // ─── the arguments: Grok's own permission modes from the user's rules ───
        string A(ClaudeDevProvider.WorkerGrant? g, string model = "default", string effort = "auto", IReadOnlyList<string>? dirs = null) =>
            string.Join(" ", GrokDevProvider.BuildArgs(Folder, g, model, effort, dirs, @"C:\t\p.txt"));
        string ro = A(null), ed = A(new(true, false)), all = A(new(true, true)), runOnly = A(new(false, true));
        check("gd-args", "headless: prompt from a file, Claude-style stream, the project as cwd",
            ro.StartsWith(@"--prompt-file C:\t\p.txt --output-format streaming-messages-json --cwd C:\work\demo"), ro);
        check("gd-args", "no grant → dontAsk (asks would be refused: read-only), no file tools to remove",
            ro.Contains("--permission-mode dontAsk") && !ro.Contains("--disallowed-tools"), ro);
        check("gd-args", "edit grant → acceptEdits; edit + run → bypassPermissions",
            ed.Contains("--permission-mode acceptEdits") && all.Contains("--permission-mode bypassPermissions") && !all.Contains("--disallowed-tools"), ed + " | " + all);
        check("gd-args", "run without edit → bypassPermissions but its three file-writing tools removed",
            runOnly.Contains("--permission-mode bypassPermissions") && runOnly.Contains("--disallowed-tools search_replace,hashline_edit,write"), runOnly);
        check("gd-args", "never --always-approve / --yolo / a sandbox switch",
            new[] { ro, ed, all, runOnly }.All(a => !a.Contains("--always-approve") && !a.Contains("--yolo") && !a.Contains("--sandbox")), "");
        check("gd-args", "\"default\" model → no -m; a pick → -m <id>",
            !ro.Contains(" -m ") && A(null, "grok-4.6").Contains("-m grok-4.6"), A(null, "grok-4.6"));
        check("gd-args", "effort clamped per model: default (=4.7) max → xhigh, 4.5 max → high, auto → none",
            A(null, "default", "max").Contains("--reasoning-effort xhigh") && A(null, "grok-4.5", "max").Contains("--reasoning-effort high")
            && !ro.Contains("--reasoning-effort"), A(null, "grok-4.5", "max"));
        check("gd-args", "every allowed folder beside the project → --add-dir",
            A(null, dirs: new[] { @"D:\a", @"D:\b" }).Contains(@"--add-dir D:\a --add-dir D:\b"), A(null, dirs: new[] { @"D:\a", @"D:\b" }));

        string prompt = GrokDevProvider.BuildPrompt(Folder, null, null, "user: hi", "fix it");
        string worker = GrokDevProvider.BuildPrompt(Folder, new(true, false), null, "", "fix it");
        check("gd-args", "the prompt: grok-dev wrapper, read-only without a grant, transcript, then the request",
            prompt.Contains("'grok-dev'") && prompt.Contains("not change it") && prompt.Contains("<conversation_so_far>\nuser: hi")
            && prompt.EndsWith("fix it") && worker.Contains("create and edit files") && worker.Contains("Run only read-only commands"), prompt[..Math.Min(200, prompt.Length)]);

        // ─── finding the CLI: xAI's installer location first, never a script shim ───
        string home = @"C:\u\.grok";
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { home + @"\bin\grok.exe", @"C:\npm\grok.cmd", @"C:\other\grok.exe" };
        check("gd-find", "<grok home>\\bin\\grok.exe first",
            GrokDevProvider.FindCli(new[] { @"C:\other\grok.exe" }, home, exists.Contains) == home + @"\bin\grok.exe", "");
        check("gd-find", "else a grok.exe on PATH; a grok.cmd shim never",
            GrokDevProvider.FindCli(new[] { @"C:\npm\grok.cmd", @"C:\other\grok.exe" }, @"C:\none", exists.Contains) == @"C:\other\grok.exe"
            && GrokDevProvider.FindCli(new[] { @"C:\npm\grok.cmd" }, @"C:\none", exists.Contains) == null, "");

        // ─── sign-in: read, never shown ───
        check("gd-auth", "signed in = an account with a refresh token or key in auth.json, or XAI_API_KEY",
            GrokDevProvider.SignedIn("""{"default":{"refresh_token":"r","access_token":"a"}}""", null)
            && GrokDevProvider.SignedIn("""{"team":{"key":"xai-k"}}""", null)
            && GrokDevProvider.SignedIn(null, "xai-env")
            && !GrokDevProvider.SignedIn("""{"default":{"access_token":"a"}}""", null)
            && !GrokDevProvider.SignedIn("not json", null) && !GrokDevProvider.SignedIn("", ""), "");
        string? signIn = GrokDevProvider.AuthGuidance("Error: You are not authenticated. Run `grok login`.", signedIn: false);
        string? limit = GrokDevProvider.AuthGuidance("Error: 429 rate limit exceeded", signedIn: true);
        check("gd-auth", "not signed in → Thai steps with /login and auth.x.ai; a limit while signed in → wait / switch model; other text → nothing",
            signIn != null && signIn.Contains("/login") && signIn.Contains("auth.x.ai") && signIn.Contains("ล็อกอิน")
            && limit != null && limit.Contains("โควตา") && !limit.Contains("/login")
            && GrokDevProvider.AuthGuidance("the build failed: 3 errors", true) == null, signIn ?? "(null)");

        // ─── wired everywhere a CLI provider is ───
        check("gd-wire", "Grok Dev's logo is xAI's, its window grok-4.7's (500K)",
            CluadeX.Services.Providers.ProviderLogos.File(AiProviderType.GrokDev) == "xai.png"
            && ContextMemoryService.ContextWindowFor(AiProviderType.GrokDev, "grok-4.3", 4096) == 1_000_000, "");
        var loc = typeof(LocalizationService).GetField("Translations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
        var missing = new[] { "chat.model.grokPlan", "chat.usage.grokDefault", "chat.usage.grokTip", "chat.login.grokOpened", "chat.login.grokFailed",
                "setup.item.grokcli", "setup.why.grokcli", "setup.item.xai", "setup.why.xai", "wizard.hint.XAI", "wizard.hint.GrokDev" }
            .Where(k => loc == null || !loc.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v.GetValueOrDefault("en")) || string.IsNullOrWhiteSpace(v.GetValueOrDefault("th")))
            .ToList();
        check("gd-wire", "every Grok string has English and Thai", missing.Count == 0, string.Join(", ", missing));
        return Task.CompletedTask;
    }
}
