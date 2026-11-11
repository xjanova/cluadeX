// What Codex's Windows sandbox takes away, and how an OpenAI Dev turn gets around it (BrainX chat 8cf58c2a, 2026-11-09).
// The sandbox runs shell commands as another account (CodexSandboxOffline): NuGet.Config unreadable, the profile not
// writable, DPAPI keys of its own, no WMI, no rg. Its failures were read as code bugs (68 harness "failures"), four
// verify_change reports showed as a bare "MCP tool error", brain_list was refused under approval_policy=never, and a
// 300 s cut ended the harness. The event shapes below are Codex 0.167's exec --json.
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;

namespace ApiTest;

internal static class CodexSandboxWire
{
    const string Folder = @"C:\work\demo";
    const string Profile = @"C:\Users\x";

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Item(string kind, object item) => J(new { type = "item." + kind, item });

    static List<ToolResult> Rows(params string[] lines)
    {
        var tr = new CodexStreamTranslator(Folder);
        var turn = new ClaudeDevStreamTurn(Folder);
        var done = new List<ToolResult>();
        turn.ToolExecuted += done.Add;
        foreach (var line in lines) foreach (var c in tr.Translate(line)) turn.Feed(c);
        return done;
    }

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · OpenAI Dev (Codex CLI) — the sandbox, MCP failures, the brain's reads, long tests");

        // ─── a failed MCP call keeps its real report ───
        const string report = "VERDICT: FAILED — tests failed\n\n3. Tests: FAILED — 2 passed, 1 failed";
        var failedVerify = Rows(Item("completed", new
        {
            id = "item_1", type = "mcp_tool_call", server = "cluadex", tool = "verify_change", arguments = new { tests = "auto" },
            result = new { content = new[] { new { type = "text", text = report } } }, error = (object?)null, status = "failed",
        }));
        check("cx-mcp", "isError=true (status failed, no error object) → the row shows the tool's own report, not \"MCP tool error\"",
            failedVerify.Count == 1 && !failedVerify[0].Success && failedVerify[0].Error.Contains("VERDICT: FAILED")
            && !failedVerify[0].Error.Contains("MCP tool error"), failedVerify.FirstOrDefault()?.Error ?? "(no row)");
        var failedTransport = Rows(Item("completed", new
        {
            id = "item_2", type = "mcp_tool_call", server = "cluadex", tool = "verify_change", arguments = new { },
            error = new { message = "tool call timed out after 60s" }, status = "failed",
        }));
        check("cx-mcp", "an error object's message wins", failedTransport.Count == 1 && failedTransport[0].Error.Contains("timed out after 60s"),
            failedTransport.FirstOrDefault()?.Error ?? "");
        var bare = new JsonObject_(new { status = "failed" });
        string none = CodexStreamTranslator.McpFailureText(bare.Node, "mcp__cluadex__verify_change");
        check("cx-mcp", "nothing at all → it names the tool, never a bare \"MCP tool error\"",
            none.Contains("mcp__cluadex__verify_change") && !none.Equals("MCP tool error"), none);

        // ─── a sandbox refusal is said to be one ───
        const string nuget = "C:\\Program Files\\dotnet\\sdk\\11.0.100\\NuGet.targets(784,5): error : Failed to read NuGet.Config due to unauthorized access. Path: 'C:\\Users\\xman\\AppData\\Roaming\\NuGet\\NuGet.Config'.";
        var restore = Rows(Item("completed", new
        {
            id = "item_3", type = "command_execution", command = "dotnet build BrainX.slnx", aggregated_output = nuget, exit_code = 1, status = "completed",
        }));
        check("cx-sandbox", "a build that died on NuGet.Config: the failed row says the sandbox blocked it",
            restore.Count == 1 && !restore[0].Success && restore[0].Error.Contains("NuGet.Config") && restore[0].Error.Contains("sandbox"),
            restore.FirstOrDefault()?.Error ?? "");
        var realFail = Rows(Item("completed", new
        {
            id = "item_4", type = "command_execution", command = "dotnet test", aggregated_output = "Failed: 1, Passed: 41", exit_code = 1, status = "completed",
        }));
        check("cx-sandbox", "a real test failure gets no sandbox note", realFail.Count == 1 && !realFail[0].Error.Contains("sandbox"), realFail.FirstOrDefault()?.Error ?? "");

        check("cx-sandbox", "DPAPI 'user profile not loaded … impersonating' → dpapi",
            CodexSandbox.CauseKey("The data protection operation was unsuccessful. This may have been caused by not having the user profile loaded for the current thread's user context, which may be the case when the thread is impersonating.", Folder, Profile) == "codex.sandbox.why.dpapi", "");
        check("cx-sandbox", "WMI process creation denied → wmi",
            CodexSandbox.CauseKey("Invoke-CimMethod : Access denied \r\n    + CategoryInfo : PermissionDenied: (Win32_Process:String)", Folder, Profile) == "codex.sandbox.why.wmi"
            && CodexSandbox.CauseKey("System.Management.ManagementException: Access denied", Folder, Profile) == "codex.sandbox.why.wmi", "");
        check("cx-sandbox", "a denied path under the profile → profile; inside the project, or elsewhere → nothing",
            CodexSandbox.CauseKey("Access to the path 'C:\\Users\\x\\AppData\\Roaming\\BrainX\\settings.json' is denied.", Folder, Profile) == "codex.sandbox.why.profile"
            && CodexSandbox.CauseKey("Access to the path 'C:\\Users\\x\\proj\\bin\\a.dll' is denied.", @"C:\Users\x\proj", Profile) == null
            && CodexSandbox.CauseKey("Access to the path 'D:\\other\\a.dll' is denied.", Folder, Profile) == null
            && CodexSandbox.CauseKey("Access to the path 'C:\\Users\\xman2\\a.txt' is denied.", Folder, Profile) == null, "");
        check("cx-sandbox", "ordinary output → nothing", CodexSandbox.CauseKey("error CS1002: ; expected", Folder, Profile) == null
            && CodexSandbox.CauseKey("", Folder, Profile) == null, "");
        // Seen in the thaiprompt chat of 2026-11-11 and reproduced with `codex sandbox` on this machine.
        check("cx-sandbox", "curl.exe's schannel SEC_E_NO_CREDENTIALS → tls (the server is not down)",
            CodexSandbox.CauseKey("curl: (35) schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS (0x8009030e) - No credentials are available in the security package", Folder, Profile) == "codex.sandbox.why.tls"
            && CodexSandbox.Notice("curl: (35) schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS", Folder, Profile) is { } tls && tls.Contains("Node"), "");
        check("cx-sandbox", "adb's \"Cannot mkdir '\\.android'\" → adb",
            CodexSandbox.CauseKey("11-11 10:16:04.247 29864 21316 F adb.exe : adb_utils.cpp:315 Cannot mkdir '\\.android': Permission denied", Folder, Profile) == "codex.sandbox.why.adb", "");
        check("cx-sandbox", "gh that cannot read its config → gh",
            CodexSandbox.CauseKey("failed to create root command: failed to read configuration: open C:\\Users\\xman\\AppData\\Roaming\\GitHub CLI\\config.yml: Access is denied.", Folder, Profile) == "codex.sandbox.why.gh"
            && CodexSandbox.CauseKey("gh: Not Found (HTTP 404)", Folder, Profile) == null, "");

        // ─── the wrapper: what the sandbox blocks, where builds go, how to search ───
        var worker = new ClaudeDevProvider.WorkerGrant(true, true);
        string withTools = OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go", chatTools: true, hasRipgrep: true);
        check("cx-prompt", "worker turn with the chat tools: the sandbox's limits, verify_change for builds/tests, background for long runs, rg on PATH",
            withTools.Contains("Codex's Windows sandbox") && withTools.Contains("NuGet.Config") && withTools.Contains("verify_change")
            && withTools.Contains("tests_background") && withTools.Contains("timeout_minutes") && withTools.Contains("`rg` is on PATH"), withTools);
        check("cx-prompt", "…HTTPS through node (never \"the server is down\" from curl), adb and gh need the user, rg globs on Windows",
            withTools.Contains("node file.js") && withTools.Contains("SEC_E_NO_CREDENTIALS") && withTools.Contains("Cannot mkdir '\\.android'")
            && withTools.Contains("gh_pr_view") && withTools.Contains("os error 123"), withTools);
        string noTools = OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go", chatTools: false, hasRipgrep: false);
        check("cx-prompt", "no chat tools: no verify_change promise; no rg: git grep / Select-String",
            noTools.Contains("Codex's Windows sandbox") && !noTools.Contains("verify_change") && noTools.Contains("git grep")
            && noTools.Contains("Select-String") && !noTools.Contains("`rg` is on PATH"), noTools);
        string advice = OpenAIDevProvider.BuildPrompt(Folder, null, null, "", "go", hasRipgrep: false);
        check("cx-prompt", "a read-only turn still learns how to search without rg", advice.Contains("git grep") && !advice.Contains("verify_change"), advice);

        // ─── Codex's own ripgrep onto PATH ───
        string tmp = Path.Combine(Path.GetTempPath(), "cx-rg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string bin = Path.Combine(tmp, "OpenAI", "Codex", "bin");
            string exeDir = Path.Combine(bin, "f71a"), rgDir = Path.Combine(bin, "8dbc");
            Directory.CreateDirectory(exeDir);
            Directory.CreateDirectory(rgDir);
            File.WriteAllText(Path.Combine(exeDir, "codex.exe"), "");
            File.WriteAllText(Path.Combine(rgDir, "rg.exe"), "");
            string? found = CodexSandbox.FindRipgrepDir(Path.Combine(exeDir, "codex.exe"), tmp);
            check("cx-rg", "the Codex app keeps rg.exe in a sibling build folder: found", found == rgDir, found ?? "(none)");
            string npm = Path.Combine(tmp, "npm", "vendor", "x86_64-pc-windows-msvc");
            Directory.CreateDirectory(Path.Combine(npm, "codex"));
            Directory.CreateDirectory(Path.Combine(npm, "path"));
            File.WriteAllText(Path.Combine(npm, "path", "rg.exe"), "");
            string? npmFound = CodexSandbox.FindRipgrepDir(Path.Combine(npm, "codex", "codex.exe"), Path.Combine(tmp, "none"));
            check("cx-rg", "npm's vendor\\<triple>\\path\\rg.exe: found", npmFound == Path.Combine(npm, "path"), npmFound ?? "(none)");
            check("cx-rg", "no copy anywhere: none", CodexSandbox.FindRipgrepDir(null, Path.Combine(tmp, "none")) == null, "");
            string path = CodexSandbox.PrependPath(@"C:\Windows;C:\Tools", rgDir);
            check("cx-rg", "PATH gets the folder in front, once", path.StartsWith(rgDir + ";") && CodexSandbox.PrependPath(path, rgDir) == path
                && CodexSandbox.OnPath("rg.exe", path) && !CodexSandbox.OnPath("rg.exe", @"C:\nowhere"), path);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        // ─── the brain's reads under approval_policy=never ───
        var reads = ClaudeDevProvider.BrainToolsFor(null);
        check("cx-brain", "every brain look-up is approved (brain_list was refused), still no writes, ssh or chrome",
            new[] { "brain_list", "brain_walk", "brain_lookup", "brain_recall", "brain_bundle", "brain_stats", "brain_get_backlinks", "brain_code_trace" }.All(reads.Contains)
            && !reads.Contains("brain_remember") && !reads.Contains("brain_synthesize") && !reads.Contains("brain_apply_audit_fix")
            && !reads.Any(t => t.StartsWith("ssh") || t.StartsWith("chrome")) && reads.Distinct().Count() == reads.Count, string.Join(",", reads));
        var codexArgs = OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null, null, brainInCodexConfig: true);
        check("cx-brain", "Codex gets brain_list approved by name", codexArgs.Contains("mcp_servers.brainx-brain.tools.brain_list.approval_mode=\"approve\""), "");

        // ─── a message typed mid-run names the plan tool the agent has ───
        var scope = new AgentRunScope("t", null);
        scope.AddUserNote("also fix the 202 errors");
        string? note = scope.TakeUserNotes(ChatToolBridge.PlanToolName);
        check("cx-steer", "a CLI is told update_plan (its plan tool), not todo_write; the text is in it",
            note != null && note.Contains("with update_plan") && !note.Contains("todo_write") && note.Contains("202 errors"), note ?? "");
        scope.AddUserNote("x");
        check("cx-steer", "the native loop keeps todo_write", scope.TakeUserNotes()?.Contains("with todo_write") == true, "");

        // ─── long tests: not cut at 300 s ───
        static ToolCall V(params (string K, string V)[] a) => new() { ToolName = "verify_change", Type = ToolType.VerifyChange, Arguments = a.ToDictionary(x => x.K, x => x.V) };
        check("cx-long", "timeout_minutes: default 5 min, 30 → 30 min, capped at 120, nonsense → default",
            EngineeringToolModule.TimeoutMsOf(V()) == 300_000 && EngineeringToolModule.TimeoutMsOf(V(("timeout_minutes", "30"))) == 1_800_000
            && EngineeringToolModule.TimeoutMsOf(V(("timeout_minutes", "999"))) == EngineeringToolModule.MaxVerifyMinutes * 60_000
            && EngineeringToolModule.TimeoutMsOf(V(("timeout_minutes", "x"))) == 300_000, "");
        var schema = EngineeringToolModule.Schemas().First(s => s.Name == "verify_change").InputSchema.GetProperty("properties");
        check("cx-long", "verify_change offers timeout_minutes and tests_background",
            schema.TryGetProperty("timeout_minutes", out _) && schema.TryGetProperty("tests_background", out _), schema.ToString());
        return Task.CompletedTask;
    }

    /// <summary>A System.Text.Json.Nodes object from an anonymous one.</summary>
    private sealed class JsonObject_
    {
        public System.Text.Json.Nodes.JsonObject Node { get; }
        public JsonObject_(object o) => Node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(o))!;
    }
}
