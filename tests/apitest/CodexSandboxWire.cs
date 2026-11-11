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

        // ─── the full survey of 2026-11-11: the real outputs `codex sandbox` printed on this machine ───
        check("cx-sandbox", "git that writes .git (index.lock, FETCH_HEAD, a file in .git) → git",
            CodexSandbox.CauseKey("fatal: Unable to create 'D:/Code/App/videogirl/.git/index.lock': Permission denied", Folder, Profile) == "codex.sandbox.why.git"
            && CodexSandbox.CauseKey("error: cannot open '.git/FETCH_HEAD': Permission denied", Folder, Profile) == "codex.sandbox.why.git"
            && CodexSandbox.CauseKey("Set-Content : Access to the path 'D:\\w\\ws\\.git\\probe.txt' is denied.", @"D:\w\ws", Profile) == "codex.sandbox.why.git", "");
        check("cx-sandbox", "the Store python (WindowsApps alias) → python",
            CodexSandbox.CauseKey("Program 'python.exe' failed to run: The file cannot be accessed by the systemAt line:1 char:1", Folder, Profile) == "codex.sandbox.why.python"
            && CodexSandbox.CauseKey("Program 'python3.12.exe' failed to run: Access is deniedAt C:\\Program Files\\WindowsApps\\PythonSoftwareFoundation.Python.3.12\\python3.12.exe", Folder, Profile) is "codex.sandbox.why.python", "");
        check("cx-sandbox", "Get-CimInstance / tasklist \"Access denied\" → wmi",
            CodexSandbox.CauseKey("Get-CimInstance : Access denied \r\nAt line:1 char:2", Folder, Profile) == "codex.sandbox.why.wmi"
            && CodexSandbox.CauseKey("ERROR: Access denied\r\n", Folder, Profile) == "codex.sandbox.why.wmi", "");
        check("cx-sandbox", "a symlink (New-Item, mklink) → symlink; a junction is not refused",
            CodexSandbox.CauseKey("New-Item : Administrator privilege required for this operation.", Folder, Profile) == "codex.sandbox.why.symlink"
            && CodexSandbox.CauseKey("You do not have sufficient privilege to perform this operation.", Folder, Profile) == "codex.sandbox.why.symlink", "");
        check("cx-sandbox", "~/.ssh unreadable → ssh",
            CodexSandbox.CauseKey("Load key \"C:\\Users\\x\\.ssh\\id_ed25519\": Permission denied", Folder, Profile) == "codex.sandbox.why.ssh", "");
        check("cx-sandbox", "a new package that cannot download → nugetNet; the same text as a warning in a real compile failure → not the sandbox",
            CodexSandbox.CauseKey("C:\\p\\a.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.", Folder, Profile) == "codex.sandbox.why.nugetNet"
            && CodexSandbox.CauseKey("a.csproj : warning NU1900: Error occurred while getting package vulnerability data: Unable to load the service index for source https://api.nuget.org/v3/index.json.\r\nProgram.cs(3,1): error CS1002: ; expected", Folder, Profile) == null, "");
        check("cx-sandbox", "every limit has its notice in English and Thai, and a sentence in the wrapper with and without the chat tools",
            CodexSandbox.Limits.All(l => LocalizationService.Lookup("codex.sandbox.why." + l.Id, "en") is { Length: > 0 }
                && LocalizationService.Lookup("codex.sandbox.why." + l.Id, "th") is { Length: > 0 }
                && CodexSandbox.PromptSection(true, true).Contains(l.Prompt(true))
                && CodexSandbox.PromptSection(false, false).Contains(l.Prompt(false)))
            && LocalizationService.Lookup("codex.sandbox.why.profile", "th") is { Length: > 0 }
            && CodexSandbox.Limits.Select(l => l.Id).Distinct().Count() == CodexSandbox.Limits.Count,
            string.Join(",", CodexSandbox.Limits.Where(l => LocalizationService.Lookup("codex.sandbox.why." + l.Id, "th") is not { Length: > 0 }).Select(l => l.Id)));
        check("cx-sandbox", "without the chat tools no limit promises verify_change or gh_* tools",
            !CodexSandbox.PromptSection(false, true).Contains("verify_change") && !CodexSandbox.PromptSection(false, true).Contains("gh_pr_view"), "");

        // ─── the sandboxed shell's own environment (CodexSandboxEnv) ───
        var noEnv = new Dictionary<string, string?>();
        var vars = CodexSandboxEnv.Vars(@"D:\T\cluadex-codex-home", Profile, noEnv).ToDictionary(v => v.Key, v => v.Value);
        check("cx-env", "git: OpenSSL for https and safe.directory=*, through GIT_CONFIG_COUNT",
            vars.GetValueOrDefault("GIT_CONFIG_COUNT") == "2" && vars.GetValueOrDefault("GIT_CONFIG_KEY_0") == "http.sslBackend"
            && vars.GetValueOrDefault("GIT_CONFIG_VALUE_0") == "openssl" && vars.GetValueOrDefault("GIT_CONFIG_KEY_1") == "safe.directory"
            && vars.GetValueOrDefault("GIT_CONFIG_VALUE_1") == "*", string.Join(";", vars));
        check("cx-env", "NuGet: APPDATA and packages in the home, the user's packages as the fallback; Gradle, pip, UTF-8, no MSBuild node reuse",
            vars.GetValueOrDefault("APPDATA") == @"D:\T\cluadex-codex-home\Roaming" && vars.GetValueOrDefault("NUGET_PACKAGES") == @"D:\T\cluadex-codex-home\nuget\packages"
            && vars.GetValueOrDefault("RestoreFallbackFolders") == @"C:\Users\x\.nuget\packages" && vars.GetValueOrDefault("GRADLE_USER_HOME") == @"D:\T\cluadex-codex-home\gradle"
            && vars.GetValueOrDefault("PIP_CACHE_DIR") == @"D:\T\cluadex-codex-home\pip" && vars.GetValueOrDefault("PYTHONUTF8") == "1"
            && vars.GetValueOrDefault("MSBUILDDISABLENODEREUSE") == "1", string.Join(";", vars));
        var own = CodexSandboxEnv.Vars(@"D:\T\h", Profile, new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "1", ["NUGET_PACKAGES"] = @"E:\pkgs" })
            .ToDictionary(v => v.Key, v => v.Value);
        check("cx-env", "the user's own GIT_CONFIG_COUNT is left alone; their NUGET_PACKAGES becomes the fallback",
            !own.ContainsKey("GIT_CONFIG_COUNT") && !own.ContainsKey("GIT_CONFIG_KEY_0") && own.GetValueOrDefault("RestoreFallbackFolders") == @"E:\pkgs", string.Join(";", own));
        var noHome = CodexSandboxEnv.Vars(null, Profile, noEnv).Select(v => v.Key).ToList();
        check("cx-env", "no home (it could not be made): git and UTF-8 only, no APPDATA pointing nowhere",
            noHome.Contains("GIT_CONFIG_COUNT") && noHome.Contains("PYTHONUTF8") && !noHome.Contains("APPDATA") && !noHome.Contains("NUGET_PACKAGES"), string.Join(",", noHome));
        var overrides = CodexSandboxEnv.ConfigOverrides(new KeyValuePair<string, string>[] { new("APPDATA", @"D:\T\h\Roaming"), new("BAD", "it's"), new("bad-name", "x"), new("NL", "a\nb") });
        check("cx-env", "overrides are TOML literal strings; a value with a quote or a line break, or a bad name, is left out",
            overrides.Count == 1 && overrides[0] == @"shell_environment_policy.set.APPDATA='D:\T\h\Roaming'", string.Join(" | ", overrides));
        const string userConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /><add key=\"corp\" value=\"https://corp/nuget\" /></packageSources>"
            + "<packageSourceCredentials><corp><add key=\"Username\" value=\"u\" /><add key=\"ClearTextPassword\" value=\"hunter2\" /></corp></packageSourceCredentials>"
            + "<apikeys><add key=\"https://corp\" value=\"KEY123\" /></apikeys><config><add key=\"http_proxy\" value=\"http://p:8080\" /><add key=\"http_proxy.password\" value=\"pw9\" /></config></configuration>";
        string clean = CodexSandboxEnv.SanitizeNuGetConfig(userConfig);
        check("cx-env", "the sandbox's NuGet.Config keeps the sources and the proxy, never a password, token or API key",
            clean.Contains("https://corp/nuget") && clean.Contains("http://p:8080") && !clean.Contains("hunter2") && !clean.Contains("KEY123")
            && !clean.Contains("pw9") && !clean.Contains("packageSourceCredentials"), clean);
        check("cx-env", "no config or broken XML → plain nuget.org",
            CodexSandboxEnv.SanitizeNuGetConfig(null).Contains("api.nuget.org") && CodexSandboxEnv.SanitizeNuGetConfig("<config").Contains("api.nuget.org"), "");
        string envHome = Path.Combine(Path.GetTempPath(), "cx-env-" + Guid.NewGuid().ToString("N")[..8]);
        string fakeAppData = Path.Combine(envHome, "userAppData");
        try
        {
            Directory.CreateDirectory(Path.Combine(fakeAppData, "NuGet"));
            File.WriteAllText(Path.Combine(fakeAppData, "NuGet", "NuGet.Config"), userConfig);
            bool made = CodexSandboxEnv.Prepare(Path.Combine(envHome, "home"), fakeAppData);
            string written = File.ReadAllText(Path.Combine(envHome, "home", "Roaming", "NuGet", "NuGet.Config"));
            check("cx-env", "Prepare writes the cleaned copy into the home", made && written.Contains("https://corp/nuget") && !written.Contains("hunter2"), written);
        }
        finally { try { Directory.Delete(envHome, true); } catch { } }
        var envArgs = OpenAIDevProvider.BuildArgs(Folder, new ClaudeDevProvider.WorkerGrant(true, true), "default", "auto", null,
            sandboxEnv: CodexSandboxEnv.ConfigOverrides(CodexSandboxEnv.Vars(@"D:\T\h", Profile, noEnv)));
        int appIdx = envArgs.IndexOf(@"shell_environment_policy.set.APPDATA='D:\T\h\Roaming'");
        check("cx-env", "BuildArgs: each variable is a -c override before the stdin \"-\"",
            appIdx > 0 && envArgs[appIdx - 1] == "-c" && envArgs[^1] == "-" && envArgs.Contains("shell_environment_policy.set.GIT_CONFIG_VALUE_0='openssl'"), string.Join(" ", envArgs));
        string homePrompt = CodexSandbox.PromptSection(true, true, @"D:\T\cluadex-codex-home");
        check("cx-env", "the wrapper names the sandbox's home and says .git is read-only, py for python, Get-Process for tasklist",
            homePrompt.Contains(@"D:\T\cluadex-codex-home") && homePrompt.Contains("`.git` read-only") && homePrompt.Contains("`py`")
            && homePrompt.Contains("Get-Process") && !CodexSandbox.PromptSection(true, true).Contains("cluadex-codex-home"), homePrompt);

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
        return RunAsUser(check);
    }

    /// <summary>run_as_user (owner 2026-11-11 "ทำเลย"): what the sandbox cannot do, run as the user through run_command's gate.</summary>
    static async Task RunAsUser(Action<string, string, bool, string> check)
    {
        string? R(string c) => CodexUserShell.Refusal(c);
        var allowed = new[]
        {
            "git add -A", "git commit -m \"Fix the parser; and more\"", "git push -u origin feat/x", "git -C sub status", "git --no-pager log -5", "git fetch origin",
            "git switch -c feat/y", "git branch -d old", "git config --get user.name", "git remote -v", "git.exe status",
            "gh pr create --title \"T\" --body \"B\"", "gh pr view 12", "gh run view 5 --log-failed", "gh api repos/o/r/pulls",
            "dotnet restore", "dotnet add App.csproj package Newtonsoft.Json", "dotnet nuget locals all --list",
            "curl -s https://example.com/api", "curl -fsSL https://e.com", "curl -I https://example.com", "curl -X GET https://e.com", "adb devices", "adb logcat -d",
        };
        var bad = allowed.Where(c => R(c) != null).Select(c => c + " → " + R(c)).ToList();
        check("cx-user", "run_as_user lets through what the sandbox blocks: git writes, gh, dotnet restore/add package, curl GET, adb",
            bad.Count == 0, string.Join(" | ", bad));
        var refused = new[]
        {
            "", "git status && del x", "git status; rm x", "git log | more", "git diff > out.txt", "git commit -m $(whoami)", "git log `x`",
            "powershell -c Remove-Item x", "cmd /c del x", "node x.js", "rm -rf src",
            "git reset --hard", "git clean -fd", "git push --force", "git push -f origin main", "git checkout -- src", "git restore x",
            "git stash drop", "git rm x", "git branch -D x", "git switch -f main", "git tag -d v1", "git config user.name x",
            "git remote add up u", "gh auth token", "gh secret set X", "gh repo delete o/r", "gh release delete v1",
            "gh api -X DELETE repos/o/r", "gh api repos/o/r/issues -f title=x", "dotnet build", "dotnet test", "dotnet run",
            "dotnet add App.csproj reference ../L.csproj", "dotnet nuget push x.nupkg", "curl -o x.exe https://e.com/x",
            "curl -d a=b https://e.com", "curl -X POST https://e.com", "curl -XDELETE https://e.com", "curl -F f=@x https://e.com",
            "curl --output=x https://e.com", "curl -T x https://e.com", "curl -sSo x https://e.com", "git commit -m \"$(whoami)\"", "git commit -m \"a", "git -c core.fsmonitor=x status", "git -c core.sshCommand=x fetch", "git fetch --upload-pack=x origin", "git push --receive-pack=x", "git ls-remote -u x origin", "git --config-env=a=B status", "git status (Remove-Item x)", "git log {x}",
        };
        var slipped = refused.Where(c => R(c) == null).ToList();
        check("cx-user", "…and refuses chains, other programs, what throws work away, auth/secrets/deletes, builds, uploads",
            slipped.Count == 0, string.Join(" | ", slipped));
        check("cx-user", "a refusal says what to do instead",
            R("git reset --hard")!.Length > 10 && R("dotnet build")!.Contains("sandbox") && R("git status && x")!.Contains("ONE"), R("dotnet build") ?? "");

        // The tool list: only the server of a turn offered it (env), and Codex approves it by name.
        string Names(bool on) => string.Join(",", JsonSerializer.SerializeToElement(ChatToolBridge.ToolList(on)).EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        check("cx-user", "tools/list has run_as_user only when the turn's server is told to (CLUADEX_TOOLS_USERSHELL)",
            Names(true).Split(',').Contains("run_as_user") && !Names(false).Split(',').Contains("run_as_user")
            && !ChatToolBridge.QualifiedToolNames.Any(n => n.EndsWith("run_as_user")), "");
        string on = ChatToolBridge.ConfigJson(@"C:\x\CluadeX.exe", "t", "p", userShell: true), off = ChatToolBridge.ConfigJson(@"C:\x\CluadeX.exe", "t", "p");
        var mcpOn = OpenAIDevProvider.McpOverrides(on); var mcpOff = OpenAIDevProvider.McpOverrides(off);
        check("cx-user", "Codex's override approves run_as_user (CluadeX's own card asks) only when the server offers it",
            mcpOn.Count == 1 && mcpOn[0].Contains("run_as_user={approval_mode='approve'}") && mcpOn[0].Contains("CLUADEX_TOOLS_USERSHELL='1'")
            && mcpOff.Count == 1 && !mcpOff[0].Contains("run_as_user"), mcpOn.FirstOrDefault() ?? "");

        // The prompt: a run turn with the chat tools hears of it; an edit-only turn, or one without the tools, does not.
        var run = new ClaudeDevProvider.WorkerGrant(true, true);
        string withShell = OpenAIDevProvider.BuildPrompt(Folder, run, null, "", "go", chatTools: true, hasRipgrep: true);
        string editOnly = OpenAIDevProvider.BuildPrompt(Folder, new ClaudeDevProvider.WorkerGrant(true, false), null, "", "go", chatTools: true, hasRipgrep: true);
        string noTools = OpenAIDevProvider.BuildPrompt(Folder, run, null, "", "go", chatTools: false, hasRipgrep: true);
        check("cx-user", "run turn: commit/push, gh, adb, restore and curl go through run_as_user (not \"leave the commit to the user\")",
            withShell.Contains("run_as_user") && withShell.Contains("git add / commit / push") && !withShell.Contains("leave the commit to the user")
            && withShell.Contains("run `curl` (GET) through run_as_user") && withShell.Contains("`dotnet restore`") && !withShell.Contains("ask the user (ask_user) to run the adb"),
            withShell);
        check("cx-user", "edit-only or no chat tools: no run_as_user, the old advice stays",
            !editOnly.Contains("run_as_user") && editOnly.Contains("leave the commit to the user") && !noTools.Contains("run_as_user"), "");
        check("cx-user", "every limit that names run_as_user still has its plain sentence",
            CodexSandbox.Limits.Where(l => l.WithUserShell != null).All(l => l.WithUserShell!.Contains("run_as_user") && !l.Prompt(true).Contains("run_as_user"))
            && CodexSandbox.Limits.Count(l => l.WithUserShell != null) == 5, "");

        // The bridge: refused on a turn not offered it; otherwise it is run_command (the gate's rules, cards, red card).
        var seen = new List<ToolCall>();
        Task<ToolResult> Exec(ToolCall c, CancellationToken _) { seen.Add(c); return Task.FromResult(new ToolResult { Type = c.Type, ToolName = c.ToolName, Success = true, Output = "ran" }); }
        using var turnCts = new CancellationTokenSource();
        using (var plain = ChatToolBridge.Register(new AgentRunScope("u0", Folder), Exec, Folder, turnCts.Token)!)
        {
            var r = await ChatToolBridge.HandleAsync(J(new { token = plain.Token, tool = "run_as_user", args = new { command = "git status" } }));
            check("cx-user", "a turn not offered run_as_user (Claude / Gemini / Grok, or Codex without run) is refused, nothing runs",
                !r.Ok && r.Text.Contains("only for an OpenAI Dev") && seen.Count == 0, r.Text);
        }
        using (var codex = ChatToolBridge.Register(new AgentRunScope("u1", Folder), Exec, Folder, turnCts.Token, userShell: true)!)
        {
            string cfg = File.ReadAllText(codex.ConfigFile);
            var ok = await ChatToolBridge.HandleAsync(J(new { token = codex.Token, tool = "run_as_user", args = new { command = "git commit -m \"Fix\"" } }));
            check("cx-user", "a Codex run turn: the config carries the flag, and the call is run_command with the same command",
                cfg.Contains("CLUADEX_TOOLS_USERSHELL") && ok.Ok && ok.Text == "ran" && seen.Count == 1 && seen[0].Type == ToolType.RunCommand
                && seen[0].ToolName == "run_command" && seen[0].GetArg("command") == "git commit -m \"Fix\"", ok.Text);
            var no = await ChatToolBridge.HandleAsync(J(new { token = codex.Token, tool = "run_as_user", args = new { command = "git reset --hard" } }));
            check("cx-user", "…a refused command never reaches the gate", !no.Ok && seen.Count == 1, no.Text);
        }
    }

    /// <summary>A System.Text.Json.Nodes object from an anonymous one.</summary>
    private sealed class JsonObject_
    {
        public System.Text.Json.Nodes.JsonObject Node { get; }
        public JsonObject_(object o) => Node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(o))!;
    }
}
