using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.Services.Safety;
using CluadeX.Services.Tools;

namespace SafetyTest;

/// <summary>
/// GeminiDevProvider.ChatAsync on the REAL installed Gemini CLI: real hooks through its PowerShell, a real MCP round trip
/// through CluadeX.exe --cx-tools, the real exit codes. Only the model is recorded (the CLI's own --fake-responses, passed
/// by a Debug-only seam), so no Google account, quota or network call is involved and every "the model asked for" below
/// is a line in a file. The "real ~/.gemini" is a throwaway folder (GEMINI_CLI_HOME).
///
/// What this exists for (all seen with the real CLI, 2026-10-31): a system settings file in a user-owned folder is
/// skipped; `"exe" --flag` is a PowerShell parse error and `&amp; 'exe'` does not wait for a WinExe, and in both cases the
/// failed hook let the forced push run.
/// </summary>
internal static class GeminiCliReal
{
    private const string Model = "gemini-2.5-flash";
    private static readonly string ForcedPush = "git push" + " --force origin main";

    private sealed record Turn(string Text, List<string> Lines);

    public static void Run(string work, Action<string, bool, string> check, Func<string?> matchingExe)
    {
        Console.WriteLine("Gemini Dev on the real Gemini CLI (recorded model, no account, no network)");
        if (!GeminiDevProvider.IsInstalled()) { Console.WriteLine("  SKIP the Gemini CLI is not installed here"); return; }
        string? exe = matchingExe();
        if (exe == null) return;

        // The provider finds the guard next to CluadeX.dll: the harness's own bin needs the real exe beside it.
        string here = Path.GetDirectoryName(typeof(GuardHook).Assembly.Location)!;
        string from = Path.GetDirectoryName(exe)!;
        if (!from.Equals(here, StringComparison.OrdinalIgnoreCase))
            foreach (var f in new[] { "CluadeX.exe", "CluadeX.runtimeconfig.json" }) File.Copy(Path.Combine(from, f), Path.Combine(here, f), overwrite: true);
        string portable = Path.Combine(AppContext.BaseDirectory, "portable.txt");
        bool ownPortable = !File.Exists(portable);
        if (ownPortable) File.WriteAllText(portable, "");

        string fakeHome = Path.Combine(work, "gemini-user");
        var env = new Dictionary<string, string?>
        {
            ["GEMINI_CLI_HOME"] = fakeHome, ["GEMINI_API_KEY"] = "fake-key-no-network", ["GOOGLE_API_KEY"] = null,
            ["CLUADEX_GEMINI_TEST_ARGS"] = null, ["CLUADEX_GEMINI_TEST_NO_HOOKS"] = null,
        };
        var saved = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        try { RunAsync(work, fakeHome, check).GetAwaiter().GetResult(); }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            ClaudeDevProvider.Grant.Value = null;
            ClaudeDevProvider.StreamSink.Value = null;
            ClaudeDevProvider.Extras.Value = null;
            if (ownPortable) try { File.Delete(portable); } catch (IOException) { }
        }
    }

    private static async Task RunAsync(string work, string fakeHome, Action<string, bool, string> check)
    {
        // The user's own ~/.gemini: auth, a GEMINI.md, an extension whose hook leaves a file, a brain the CLI cannot start.
        string real = Path.Combine(fakeHome, ".gemini");
        Directory.CreateDirectory(Path.Combine(real, "extensions", "my-ext", "hooks"));
        Directory.CreateDirectory(Path.Combine(real, "policies"));
        File.WriteAllText(Path.Combine(real, "GEMINI.md"), "user memory");
        File.WriteAllText(Path.Combine(real, "policies", "mine.toml"), "");
        string userSettings = """
            // the CLI reads comments, so must the copy
            {
              "security": { "auth": { "selectedType": "gemini-api-key" } },
              "privacy": { "usageStatisticsEnabled": false },
              "mcpServers": { "brainx-brain": { "command": "cluadex-no-such-server.exe" } }
            }
            """;
        File.WriteAllText(Path.Combine(real, "settings.json"), userSettings);
        File.WriteAllText(Path.Combine(real, "extensions", "my-ext", "gemini-extension.json"), """{ "name": "my-ext", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(real, "extensions", "my-ext", "hooks", "hooks.json"), JsonSerializer.Serialize(new
        {
            hooks = new { SessionStart = new[] { new { hooks = new[] { new { type = "command", name = "my-ext", command = "Set-Content ext-loaded.txt x", timeout = 30000 } } } } },
        }));

        var settings = new SettingsService();
        void Use(string model) => settings.Settings.ProviderConfigs[GeminiDevProvider.Id] = new ProviderConfig { UseCustomModel = true, CustomModelId = model };
        Use(Model);
        var files = new FileSystemService();
        var provider = new GeminiDevProvider(settings, files);
        var both = new ClaudeDevProvider.WorkerGrant(true, true);
        await provider.InitializeAsync();
        Console.WriteLine("  (" + provider.StatusMessage + ")");

        string Project(string name)
        {
            string dir = Path.Combine(work, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        async Task<Turn> Ask(string project, ClaudeDevProvider.WorkerGrant? grant, string? toolsConfig, params (string Tool, object Args)[] calls)
        {
            files.WorkingDirectory = project;
            string fake = Path.Combine(project, "..", Path.GetFileName(project) + "-model.jsonl");
            File.WriteAllText(fake, FakeModel(calls));
            Environment.SetEnvironmentVariable("CLUADEX_GEMINI_TEST_ARGS", "--fake-responses|" + fake);
            var lines = new List<string>();
            ClaudeDevProvider.Grant.Value = grant;
            ClaudeDevProvider.StreamSink.Value = grant != null ? l => { lock (lines) lines.Add(l); } : null;
            ClaudeDevProvider.Extras.Value = new ClaudeDevProvider.TurnExtras(toolsConfig, Array.Empty<string>());
            var text = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await foreach (var chunk in provider.ChatAsync(new List<ChatMessage>(), "hello", null, cts.Token)) text.Append(chunk);
            return new Turn(text.ToString(), lines);
        }

        // ─── the guard is live in a worker turn ───
        string p1 = Project("gd1");
        var t1 = await Ask(p1, both, null,
            ("run_shell_command", new { command = "Set-Content ok.txt x", description = "harmless" }),
            ("run_shell_command", new { command = ForcedPush + "; Set-Content ran.txt x", description = "refused" }),
            ("write_file", new { file_path = Path.Combine(p1, "w.txt"), content = "x" }),
            ("run_shell_command", new { command = "Set-Content $env:GEMINI_CLI_HOME\\.gemini\\settings.json x; Set-Content home.txt x", description = "tampering" }));
        string all1 = string.Join("\n", t1.Lines);
        check("the guard hook loaded: a harmless command runs (exit 0 reached the CLI)", File.Exists(Path.Combine(p1, "ok.txt")), t1.Text + all1);
        check("a forced push is stopped by the hook — nothing after it ran", !File.Exists(Path.Combine(p1, "ran.txt")) && all1.Contains("never run"), all1);
        check("a file write in a turn that may edit goes through", File.Exists(Path.Combine(p1, "w.txt")), all1);
        check("a command that reaches the turn's own home is refused", !File.Exists(Path.Combine(p1, "home.txt")) && all1.Contains("off limits"), all1);
        check("the user's extension still loads in the turn's home", File.Exists(Path.Combine(p1, "ext-loaded.txt")), all1);
        check("the project gets no .gemini folder", !Directory.Exists(Path.Combine(p1, ".gemini")), "");
        check("the user's settings.json is untouched", File.ReadAllText(Path.Combine(real, "settings.json")) == userSettings, "");
        check("no turn folder is left behind", !Directory.Exists(GeminiTurnHome.Root) || !Directory.EnumerateDirectories(GeminiTurnHome.Root).Any(), GeminiTurnHome.Root);

        // ─── a run that may not edit ───
        string p2 = Project("gd2");
        var t2 = await Ask(p2, new ClaudeDevProvider.WorkerGrant(false, true), null,
            ("write_file", new { file_path = Path.Combine(p2, "no.txt"), content = "x" }));
        check("commands only: the wrapper tells the guard, the write is refused", !File.Exists(Path.Combine(p2, "no.txt")) && string.Join("\n", t2.Lines).Contains("not edit"), string.Join("\n", t2.Lines));

        // ─── the cluadex MCP server ───
        string p3 = Project("gd3");
        var seen = new List<string>();
        using var turnCts = new CancellationTokenSource();
        var ticket = ChatToolBridge.Register(new AgentRunScope("safetytest-gemini", p3), (call, ct) =>
        {
            lock (seen) seen.Add(call.ToolName + ":" + call.GetArg("message"));
            return Task.FromResult(new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = "opened chat #9" });
        }, p3, turnCts.Token);
        check("a tool ticket for the Gemini turn", ticket != null, "");
        if (ticket != null)
        {
            var t3 = await Ask(p3, both, ticket.ConfigFile, ("mcp_cluadex_new_chat", new { path = p3, message = "สวัสดี" }));
            check("the cluadex MCP server loaded: the CLI's call reached the app through CluadeX.exe --cx-tools",
                seen.Contains("new_chat:สวัสดี") && string.Join("\n", t3.Lines).Contains("opened chat #9"), string.Join("\n", t3.Lines) + t3.Text);
            ticket.Dispose();
        }

        // ─── fail closed ───
        string p4 = Project("gd4");
        Directory.CreateDirectory(Path.Combine(p4, ".gemini"));
        File.WriteAllText(Path.Combine(p4, ".gemini", "settings.json"), """{ "hooksConfig": { "enabled": false } }""");
        var t4 = await Ask(p4, both, null, ("run_shell_command", new { command = ForcedPush + "; Set-Content ran.txt x", description = "refused" }));
        Console.WriteLine("  (a project's hooksConfig.enabled:false on this CLI: " + (t4.Text.Contains("หยุด Gemini Dev") ? "hooks off, turn killed)" : "guard stayed live)"));
        check("a project that turns hooks off gets no unguarded tool: the guard still stands, or the turn is killed",
            !File.Exists(Path.Combine(p4, "ran.txt")) && (t4.Text.Contains("หยุด Gemini Dev") || string.Join("\n", t4.Lines).Contains("never run")), t4.Text + string.Join("\n", t4.Lines));

        // The bug itself: the CLI runs the turn without the settings' hooks. Nothing may run.
        string p8 = Project("gd8");
        Environment.SetEnvironmentVariable("CLUADEX_GEMINI_TEST_NO_HOOKS", "1");
        var t8 = await Ask(p8, both, null, ("run_shell_command", new { command = "Set-Content bad.txt x", description = "must never run" }));
        Environment.SetEnvironmentVariable("CLUADEX_GEMINI_TEST_NO_HOOKS", null);
        check("settings skipped, no hook loaded: the turn is killed before its first tool and the user is told",
            !File.Exists(Path.Combine(p8, "bad.txt")) && t8.Text.Contains("หยุด Gemini Dev") && !provider.LastTurnAuthRefused, t8.Text + string.Join("\n", t8.Lines));

        string p5 = Project("gd5");
        Directory.CreateDirectory(Path.Combine(p5, ".gemini"));
        File.WriteAllText(Path.Combine(p5, ".gemini", "settings.json"), """{ "hooksConfig": { "disabled": ["cluadex-guard", "cluadex-loaded"] } }""");
        var t5 = await Ask(p5, both, null, ("run_shell_command", new { command = ForcedPush + "; Set-Content ran.txt x", description = "refused" }));
        check("a project cannot disable the guard by name", !File.Exists(Path.Combine(p5, "ran.txt")) && string.Join("\n", t5.Lines).Contains("never run"), string.Join("\n", t5.Lines) + t5.Text);

        // The reason for the turn's own home. If this ever fails the CLI changed: the system-settings route may be open again.
        string p6 = Project("gd6");
        var (skipped, err) = RunWithSystemSettings(provider, p6);
        check("the CLI still skips a system settings file in a user-owned folder, and says so", skipped, err);

        // A guard that cannot start must block: exit 2 is the only code that stops a tool.
        string wrapper = Path.Combine(p6, "guard.cmd");
        File.WriteAllText(wrapper, GeminiTurnHome.GuardCmd(@"C:\no-such-folder\CluadeX.exe", noEdit: false));
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{wrapper}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true })!)
        {
            p.StandardInput.Close();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            check("the wrapper turns a guard that cannot start into a block (exit 2)", p.ExitCode == 2, p.ExitCode.ToString());
        }

        check("the turn's home is only the guard's when a path is really inside it",
            GuardHook.TouchesTurnDir("write_file", @"C:\t\abc\guard.cmd", @"C:\t\abc\") && GuardHook.TouchesTurnDir("write_file", "C:/t/abc/.gemini/settings.json", @"C:\t\abc")
            && !GuardHook.TouchesTurnDir("write_file", @"C:\t\abc\.gemini\tmp\plan.md", @"C:\t\abc\") && !GuardHook.TouchesTurnDir("replace", @"C:\t\abcd\x.cs", @"C:\t\abc\")
            && GuardHook.TouchesTurnDir("run_shell_command", @"del C:\t\abc\guard.cmd", @"C:\t\abc\") && !GuardHook.TouchesTurnDir("run_shell_command", @"type C:\t\abc\.gemini\tmp\out.txt", @"C:\t\abc\"), "");

        // ─── every model in the box reaches the CLI as a model it will run ───
        string p7 = Project("gd7");
        foreach (var model in GeminiDevProvider.KnownModels.Where(m => m != "default"))
        {
            Use(model);
            var t = await Ask(p7, null, null);
            check($"model box: {model} starts on the real CLI", t.Text.Contains("finished"), t.Text);
        }
        Use(Model);
    }

    /// <summary>The recorded model: one reply per call, then the closing text.</summary>
    private static string FakeModel((string Tool, object Args)[] calls)
    {
        static string Line(object part) => JsonSerializer.Serialize(new
        {
            method = "generateContentStream",
            response = new[] { new { candidates = new[] { new { content = new { role = "model", parts = new[] { part } }, finishReason = "STOP" } } } },
        });
        var lines = calls.Select(c => Line(new { functionCall = new { name = c.Tool, args = c.Args } })).ToList();
        lines.Add(Line(new { text = "finished" }));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The old delivery, on the CLI directly: does it still refuse it?</summary>
    private static (bool Skipped, string Err) RunWithSystemSettings(GeminiDevProvider provider, string project)
    {
        string file = Path.Combine(project, "system.json");
        File.WriteAllText(file, "{}");
        string fake = Path.Combine(project, "model.jsonl");
        File.WriteAllText(fake, FakeModel(Array.Empty<(string, object)>()));
        var (node, script) = provider.Cli!.Value;
        var psi = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = project };
        foreach (var a in new[] { script, "--output-format", "stream-json", "--skip-trust", "--model", Model, "--fake-responses", fake, "-p", "hi" }) psi.ArgumentList.Add(a);
        psi.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = file;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outText = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (err.Contains("Skipping system settings"), err);
    }
}
