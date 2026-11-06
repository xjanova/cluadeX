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

        Guard(check);
        return Task.CompletedTask;
    }

    /// <summary>The turn's guard, chat tools and sub-agent gate (GrokTurnHome + the flags), all pure here; the real exe,
    /// junctions and CLI are in tests/safetytest (GrokCliReal).</summary>
    static void Guard(Action<string, string, bool, string> check)
    {
        string A(ClaudeDevProvider.WorkerGrant? g, bool guarded = true, bool noSub = false, bool chat = false) =>
            string.Join(" ", GrokDevProvider.BuildArgs(Folder, g, "default", "auto", null, @"C:\t\p.txt", guarded, noSub, chat));
        check("gd-guard", "a run grant without the guard → no shell (run_terminal_cmd removed); with it, the shell stays",
            A(new(true, true), guarded: false).Contains("--disallowed-tools run_terminal_cmd") && !A(new(true, true)).Contains("run_terminal_cmd"), A(new(true, true), guarded: false));
        check("gd-guard", "run without edit and no guard → one list: the file writers and the shell",
            A(new(false, true), guarded: false).Contains("--disallowed-tools search_replace,hashline_edit,write,run_terminal_cmd"), A(new(false, true), guarded: false));
        check("gd-guard", "no run grant, no guard → nothing removed (dontAsk / acceptEdits refuse commands already)",
            !A(null, guarded: false).Contains("--disallowed-tools") && !A(new(true, false), guarded: false).Contains("--disallowed-tools"), A(new(true, false), guarded: false));
        check("gd-guard", "sub-agents off → --no-subagents; chat tools → --allow MCPTool(cluadex__*)",
            A(null, noSub: true).Contains("--no-subagents") && !A(null).Contains("--no-subagents")
            && A(null, chat: true).Contains("--allow MCPTool(cluadex__*)") && !A(null).Contains("--allow"), A(null, noSub: true, chat: true));

        // The hook file: Grok's own tool ids and the Claude names it maps, one program per command, no shell in between.
        string hooks = GrokTurnHome.HooksJson(@"C:\h\guard.cmd", @"C:\h\loaded.cmd");
        var hj = System.Text.Json.Nodes.JsonNode.Parse(hooks)!;
        var pre = hj["hooks"]?["PreToolUse"]?[0];
        string matcher = pre?["matcher"]?.GetValue<string>() ?? "";
        check("gd-guard", "PreToolUse: the shell, the three writers and the spawn, by Grok's ids and the Claude names",
            matcher.Split('|').ToHashSet().IsSupersetOf(new[] { "run_terminal_cmd", "run_terminal_command", "search_replace", "hashline_edit", "write", "spawn_subagent", "Bash", "Edit", "Write" })
            && System.Text.RegularExpressions.Regex.IsMatch(matcher, @"^[A-Za-z0-9_|]+$"), matcher);
        check("gd-guard", "…runs guard.cmd as one program (args: []), 300 s to answer the red prompt",
            pre?["hooks"]?[0]?["command"]?.GetValue<string>() == @"C:\h\guard.cmd" && pre?["hooks"]?[0]?["args"]?.AsArray().Count == 0
            && pre?["hooks"]?[0]?["timeout"]?.GetValue<int>() == 300, hooks);
        check("gd-guard", "SessionStart runs loaded.cmd, the batch file that leaves the marker beside it",
            hj["hooks"]?["SessionStart"]?[0]?["hooks"]?[0]?["command"]?.GetValue<string>() == @"C:\h\loaded.cmd"
            && GrokTurnHome.LoadedCmd.Contains("%~dp0hooks.loaded") && GrokTurnHome.LoadedCmd.All(c => c < 128), GrokTurnHome.LoadedCmd);

        // config.toml: the bridge's server appended as TOML, escaped, never a second table of the same name.
        string bridge = CluadeX.Services.Tools.ChatToolBridge.ConfigJson(@"C:\Program Files\CluadeX\CluadeX.exe", "tok\"en\\x", @"\\.\pipe\cx-1");
        string? toml = GrokTurnHome.McpToml(bridge, "[mcp_servers.brainx-brain]\ncommand = 'x'\n");
        check("gd-mcp", "the chat-tools server becomes [mcp_servers.cluadex] with its command, args and env",
            toml != null && toml.Contains("[mcp_servers.cluadex]") && toml.Contains(@"command = ""C:\\Program Files\\CluadeX\\CluadeX.exe""")
            && toml.Contains("args = [\"--cx-tools\"]") && toml.Contains("[mcp_servers.cluadex.env]") && toml.Contains(@"CLUADEX_TOOLS_TOKEN = ""tok\""en\\x"""), toml ?? "(null)");
        check("gd-mcp", "a user config that already has [mcp_servers.cluadex] (or its env table) → nothing appended",
            GrokTurnHome.McpToml(bridge, "[mcp_servers.cluadex]\ncommand='y'") == null && GrokTurnHome.McpToml(bridge, "  [ mcp_servers.\"cluadex\" .env]") == null
            && GrokTurnHome.McpToml(bridge, "[mcp_servers.cluadex-other]") != null, "");
        check("gd-mcp", "no bridge config, or one that is not JSON → nothing", GrokTurnHome.McpToml(null, "") == null && GrokTurnHome.McpToml("{oops", "") == null, "");
        check("gd-mcp", "TOML strings: backslash, quote, line break and control characters escaped; Thai kept",
            GrokTurnHome.TomlString("a\\b\"c\nd\u0001ไทย") == "\"a\\\\b\\\"c\\nd\\u0001ไทย\"", GrokTurnHome.TomlString("a\\b\"c\nd\u0001ไทย"));

        // The wrapper: how Grok reaches CluadeX's tools, and the sub-agent line in Grok's words.
        string hint = GrokDevProvider.ChatToolsHint(null, true) ?? "";
        check("gd-mcp", "with chat tools the wrapper names search_tool / use_tool and cluadex__open_project / new_chat / ask_user; without, nothing",
            hint.Contains("search_tool") && hint.Contains("use_tool") && hint.Contains("cluadex__open_project") && hint.Contains("cluadex__new_chat")
            && hint.Contains("ask_user") && GrokDevProvider.ChatToolsHint(null, false) == null, hint);
        var d = new CluadeX.Services.Agents.DispatchDecision(CluadeX.Services.Agents.DispatchStrategy.FanOut, SubAgents: true, ReadOnlyOnly: false,
            Workers: true, Reviewer: false, SpeculativeFix: false, MaxConcurrency: 3, FixerCount: 0, Reason: "test");
        string? line = CluadeX.Services.Agents.CliAgentPolicy.PromptLine(d, AiProviderType.GrokDev);
        string w = GrokDevProvider.BuildPrompt(Folder, new(true, true), null, "", "go", null, hint, line);
        check("gd-agents", "the sub-agent line names spawn_subagent and CluadeX's read-only type, and reaches the worker wrapper",
            line != null && line.Contains("spawn_subagent") && line.Contains(GrokTurnHome.ExploreAgent) && w.Contains(line.Trim()) && w.Contains("use_tool"), line ?? "(null)");
        check("gd-agents", "the policy: cluadex-explore is read-only, a general-purpose writer is refused (no worktree for Grok), no model forced",
            CluadeX.Services.Agents.CliAgentPolicy.Decide(d, AiProviderType.GrokDev, GrokTurnHome.ExploreAgent, 0) is { Allow: true, Model: null, Isolate: false }
            && !CluadeX.Services.Agents.CliAgentPolicy.Decide(d, AiProviderType.GrokDev, "general-purpose", 0).Allow
            && !CluadeX.Services.Agents.CliAgentPolicy.CanIsolate(AiProviderType.GrokDev), "");
        check("gd-agents", "the read-only agent file: Grok's writers and further spawns removed, the chat tools not inherited",
            GrokTurnHome.ExploreAgentMd.StartsWith("---\nname: cluadex-explore\n") && GrokTurnHome.ExploreAgentMd.Contains("  - search_replace\n  - hashline_edit\n  - write\n  - spawn_subagent")
            && GrokTurnHome.ExploreAgentMd.Contains("except:\n    - cluadex"), GrokTurnHome.ExploreAgentMd);
        var tr = new GrokStreamTranslator();
        tr.Translate(ToolUse("s1", "read_file", new { target_file = "a" }));
        bool readOnlyFirst = !tr.SawGuardedTool;
        string? spawn = tr.Translate(ToolUse("s2", "spawn_subagent", new { prompt = "look around", description = "survey", subagent_type = GrokTurnHome.ExploreAgent }));
        check("gd-agents", "a read is not a guarded call; spawn_subagent is, and shows as a Task row with its description and type",
            readOnlyFirst && tr.SawGuardedTool && spawn != null && spawn.Contains("\"name\":\"Task\"") && spawn.Contains("\"description\":\"survey\"")
            && spawn.Contains(GrokTurnHome.ExploreAgent), spawn ?? "");

        // The guard reads Grok's hook input (camelCase, Claude's snake_case beside it) and Grok's tools.
        string camel = J(new { hookEventName = "pre_tool_use", toolName = "run_terminal_cmd", toolInput = new { command = "rd /s /q C:\\" }, cwd = Folder });
        check("gd-guard", "camelCase-only input is read under the snake_case names the guard uses",
            CluadeX.Services.Safety.GuardHook.NormalizeInput(camel).Contains("\"tool_name\":\"run_terminal_cmd\"")
            && CluadeX.Services.Safety.GuardHook.NormalizeInput("not json") == "not json", "");
        check("gd-guard", "run_terminal_cmd that wipes a drive → the red prompt (refused without a yes)",
            CluadeX.Services.Safety.GuardHook.Decide(camel) != null, CluadeX.Services.Safety.GuardHook.Decide(camel) ?? "(allowed)");
        check("gd-guard", "an ordinary Grok command passes",
            CluadeX.Services.Safety.GuardHook.Decide(J(new { tool_name = "run_terminal_cmd", tool_input = new { command = "git status" }, cwd = Folder })) == null, "");
        string hook = J(new { tool_name = "write", tool_input = new { path = @"C:\r\.git\hooks\pre-commit", content = "rd /s /q C:\\" }, cwd = Folder });
        check("gd-guard", "Grok's write of a git hook that would wipe a drive → refused (path under Grok's own argument name)",
            CluadeX.Services.Safety.GuardHook.Decide(hook) != null, CluadeX.Services.Safety.GuardHook.Decide(hook) ?? "(allowed)");
        using (var iso = JsonDocument.Parse(J(new { tool_name = "spawn_subagent", tool_input = new { prompt = "x", isolation = "worktree" } })))
        using (var plain = JsonDocument.Parse(J(new { tool_name = "spawn_subagent", tool_input = new { prompt = "x" } })))
            check("gd-guard", "a spawn in its own worktree is refused (Grok keeps it in the turn's home, deleted after); without isolation it goes to the gate",
                CluadeX.Services.Safety.GuardHook.GrokSpawnRefusal(iso.RootElement) != null && CluadeX.Services.Safety.GuardHook.GrokSpawnRefusal(plain.RootElement) == null, "");
        using (var sub = JsonDocument.Parse(J(new { tool_name = "write", subagentType = GrokTurnHome.ExploreAgent })))
        using (var main = JsonDocument.Parse(J(new { tool_name = "write" })))
            check("gd-guard", "a call from inside cluadex-explore is known as such (its writes are refused)",
                CluadeX.Services.Safety.GuardHook.InReadOnlyGrokAgent(sub.RootElement) && !CluadeX.Services.Safety.GuardHook.InReadOnlyGrokAgent(main.RootElement), "");
    }
}
