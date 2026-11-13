using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace ApiTest;

/// <summary>
/// The fixes the small-model bench (tests/smallbench, 2026-11-08) measured, checked without a model:
/// a build that found nothing to run is not red, a Python project has a build, wrap_up repairs a box it used to refuse,
/// the described-not-done guard only counts a file named right before its code block, a failed tool's output reaches the
/// model, and run_tests with no tests says how to check instead. The loop-level checks live in ImageWire (they need its
/// mocked Anthropic loop).
/// </summary>
internal static class SmallModelWire
{
    /// <summary>The loop-level checks, run inside ImageWire's mocked Anthropic loop (its hooks are silenced, its log and
    /// home are the harness's). <paramref name="project"/> is an open project folder with no build system in it.</summary>
    public static async Task LoopChecks(Action<string, bool, string> check, GoldenMock anth, Func<int> count, Func<List<string>> bodies,
        Func<string, string> plain, Func<string, string, string, string> toolUseSse, Func<string, string> textSse,
        FileSystemService fs, string project, CodeAgentService agent,
        Func<List<ChatMessage>, string, int?, Task<AgentLoopResult>> turn)
    {
        // ── a build that found nothing to run does not hold the turn open (red-build guard) ──
        bool noBuildHere = fs is not null && FileSystemService.PythonCompileCommand(project) == null
            && !Directory.EnumerateFiles(project, "*.*proj", SearchOption.AllDirectories).Any()
            && !File.Exists(Path.Combine(project, "package.json"));
        int k = 0;
        anth.Responder = _ => Interlocked.Increment(ref k) switch
        {
            1 => (200, "text/event-stream", toolUseSse("toolu_nb1", "run_build", "{}")),
            _ => (200, "text/event-stream", textSse("Done: the folder has no build, nothing to verify.")),
        };
        int start = count();
        fs.WorkingDirectory = project;
        agent.InvalidateSystemPromptCache();
        AgentLoopResult run;
        try { run = await turn(new List<ChatMessage>(), "check this folder builds", 6); }
        finally { fs.WorkingDirectory = ""; agent.InvalidateSystemPromptCache(); }
        var b = bodies().Skip(start).ToList();
        var buildResult = run.Steps.SelectMany(s => s.ToolResults).FirstOrDefault(r => r.Type == ToolType.RunBuild);
        check("smallbench: run_build that found no build system is not a red build — the next answer ends the turn (control: the build did report nothing to run)",
            noBuildHere && run.Success && b.Count == 2 && !b.Any(x => plain(x).Contains("STILL FAILING"))
            && buildResult != null && AgentToolService.FoundNothingToRun(buildResult),
            $"no build here={noBuildHere} · requests={b.Count} · success={run.Success} · stop={run.StopReason} · build: {buildResult?.Error?[..Math.Min(80, buildResult.Error.Length)]}");
        anth.Responder = null;
    }

    public static void Run(Action<string, string, bool, string> check, Action<string> section)
    {
        void Check(string name, bool ok, string detail = "") => check("smallmodel", name, ok, detail);
        section("Small-model fixes (smallbench 2026-11-08)");

        // ── no build system ≠ red build ──
        var nothing = new ToolResult { Type = ToolType.RunBuild, Success = false,
            Error = $"run_build: {AgentToolService.NothingToRunMarker} build system in the project or the folders below it (…)" };
        var red = new ToolResult { Type = ToolType.RunBuild, Success = false, Error = "Build failed (exit code 1)" };
        var noTests = new ToolResult { Type = ToolType.RunTests, Success = false,
            Error = $"run_tests: {AgentToolService.NothingToRunMarker} test runner in the project (…)" };
        var cmdSaysIt = new ToolResult { Type = ToolType.RunCommand, Success = false,
            Error = $"run_build: {AgentToolService.NothingToRunMarker} build system" };
        Check("S1 a build that found nothing to run is not a red build (control: a real failure is)",
            AgentToolService.FoundNothingToRun(nothing) && AgentToolService.FoundNothingToRun(noTests)
            && !AgentToolService.FoundNothingToRun(red) && !AgentToolService.FoundNothingToRun(cmdSaysIt)
            && !AgentToolService.FoundNothingToRun(new ToolResult { Type = ToolType.RunBuild, Success = true }),
            "");

        // ── a Python project has a build ──
        string root = Path.Combine(Path.GetTempPath(), "smallmodelwire-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string pkg = Path.Combine(root, "pkgproj"), docs = Path.Combine(root, "docsonly"), top = Path.Combine(root, "script");
            Directory.CreateDirectory(Path.Combine(pkg, "shopkit"));
            File.WriteAllText(Path.Combine(pkg, "shopkit", "__init__.py"), "");
            File.WriteAllText(Path.Combine(pkg, "README.md"), "x");
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, "README.md"), "x");
            Directory.CreateDirectory(Path.Combine(docs, "notes"));
            File.WriteAllText(Path.Combine(docs, "notes", "a.md"), "x");
            Directory.CreateDirectory(top);
            File.WriteAllText(Path.Combine(top, "main.py"), "print(1)");
            bool pythonHere = WrittenFileCheck.FindTool("python") != null;
            string? a = FileSystemService.PythonCompileCommand(pkg), b = FileSystemService.PythonCompileCommand(docs),
                c = FileSystemService.PythonCompileCommand(top);
            Check("S2 a Python package or script folder builds with compileall; a folder with no Python has no build",
                b == null && (pythonHere
                    ? a != null && a.StartsWith("python -m compileall") && c == a
                    : a == null && c == null),
                $"python={pythonHere} · package: {a ?? "null"} · docs: {b ?? "null"} · script: {c ?? "null"}");

            // the detector reaches it through the normal path (project root)
            var fsvc = new FileSystemService();
            fsvc.WorkingDirectory = pkg;
            var det = fsvc.DetectProjectCommand(test: false);
            Check("S3 DetectProjectCommand finds the Python build at the project root (when Python is installed)",
                pythonHere ? det?.Command == a : det == null, det?.Command ?? "null");

            // ── a Flutter / Dart project and a Laravel one have a build and tests (2026-11-13: "couldn't detect a build
            //    system" in the owner's Flutter apps) ──
            string app = Path.Combine(root, "flutterapp"), lib = Path.Combine(root, "dartlib"), web = Path.Combine(root, "laravel"),
                plainPhp = Path.Combine(root, "phplib");
            Directory.CreateDirectory(Path.Combine(app, "test"));
            File.WriteAllText(Path.Combine(app, "pubspec.yaml"), "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n");
            File.WriteAllText(Path.Combine(app, "test", "widget_test.dart"), "void main() {}");
            Directory.CreateDirectory(lib);
            File.WriteAllText(Path.Combine(lib, "pubspec.yaml"), "name: lib\nenvironment:\n  sdk: ^3.0.0\n");
            const string F = @"D:\SDK\Flutter\bin\flutter.bat", D = @"D:\SDK\Flutter\bin\dart.bat";
            Check("S3b a Flutter app builds with the analyzer (errors only, not its lints) and tests with flutter test",
                FileSystemService.DartCommand(app, false, F, D) == "flutter analyze --no-fatal-infos --no-fatal-warnings"
                && FileSystemService.DartCommand(app, true, F, D) == "flutter test",
                $"{FileSystemService.DartCommand(app, false, F, D)} · {FileSystemService.DartCommand(app, true, F, D)}");
            Check("S3c a plain Dart package: dart analyze; no test folder = no tests to run (not a command that fails)",
                FileSystemService.DartCommand(lib, false, F, D) == "dart analyze --no-fatal-warnings" && FileSystemService.DartCommand(lib, true, F, D) == null,
                $"{FileSystemService.DartCommand(lib, false, F, D)} · {FileSystemService.DartCommand(lib, true, F, D) ?? "null"}");
            Check("S3d no Flutter and no Dart on PATH → nothing is offered (a command that cannot start is no check); a Flutter app with only Dart still analyzes, but its tests need Flutter",
                FileSystemService.DartCommand(app, false, null, null) == null && FileSystemService.DartCommand(app, true, null, null) == null
                && FileSystemService.DartCommand(app, false, null, D) == "dart analyze --no-fatal-warnings" && FileSystemService.DartCommand(app, true, null, D) == null
                && FileSystemService.DartCommand(Path.Combine(root, "nowhere"), false, F, D) == null, "");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "artisan"), "<?php");
            File.WriteAllText(Path.Combine(web, "composer.json"), "{}");
            File.WriteAllText(Path.Combine(web, "package.json"), "{}");
            Directory.CreateDirectory(Path.Combine(plainPhp, "vendor", "bin"));
            File.WriteAllText(Path.Combine(plainPhp, "phpunit.xml.dist"), "<phpunit/>");
            File.WriteAllText(Path.Combine(plainPhp, "vendor", "bin", "phpunit"), "#!/usr/bin/env php");
            const string Laragon = @"C:\laragon\bin\php\php-8.3.33-Win32-vs16-x64\php.exe";
            Check("S3e a Laravel app tests with artisan test (PHP on PATH as \"php\", Laragon's by its quoted path); PHPUnit with its config and vendor copy",
                FileSystemService.PhpTestCommand(web, "php") == "php artisan test"
                && FileSystemService.PhpTestCommand(web, Laragon) == "\"" + Laragon + "\" artisan test"
                && FileSystemService.PhpTestCommand(plainPhp, "php") == "php vendor/bin/phpunit",
                $"{FileSystemService.PhpTestCommand(web, Laragon)} · {FileSystemService.PhpTestCommand(plainPhp, "php")}");
            Check("S3f no PHP on this PC, or a folder that is not a PHP project → no PHP test command",
                FileSystemService.PhpTestCommand(web, null) == null && FileSystemService.PhpTestCommand(web, "") == null
                && FileSystemService.PhpTestCommand(docs, "php") == null && FileSystemService.PhpTestCommand(app, "php") == null, "");
            // through the detector, on what this PC really has
            var fsApp = new FileSystemService { WorkingDirectory = app };
            string? realBuild = FileSystemService.DartCommand(app, false), realTest = FileSystemService.DartCommand(app, true);
            Check("S3g the detector hands a Flutter project's build and tests to run_build / run_tests (when Flutter is on PATH), from the root and from one folder up",
                fsApp.DetectProjectCommand(test: false)?.Command == realBuild && fsApp.DetectProjectCommand(test: true)?.Command == realTest
                && (realBuild == null || new FileSystemService { WorkingDirectory = Path.Combine(root, "holder") }.DetectProjectCommand(test: false) == null),
                $"flutter on PATH: {realBuild != null} · {fsApp.DetectProjectCommand(test: false)?.Command ?? "null"} · {fsApp.DetectProjectCommand(test: true)?.Command ?? "null"}");
            var fsWeb = new FileSystemService { WorkingDirectory = web };
            string? php = FileSystemService.FindPhpExe();
            Check("S3h a Laravel app's tests are artisan's, not \"npm test\" (when PHP is on this PC; else npm's, as before)",
                fsWeb.DetectProjectCommand(test: true)?.Command == (php != null ? FileSystemService.PhpTestCommand(web, php) : "npm test"),
                $"php: {php ?? "none"} · {fsWeb.DetectProjectCommand(test: true)?.Command ?? "null"}");

            // ── an MCP call that timed out is not a refusal: the model is told to look before sending it again ──
            string timedOut = AgentToolService.McpFailureText("brainx-brain", "ssh_run", new TimeoutException("MCP request 'tools/call' timed out after 180000ms"));
            string refused = AgentToolService.McpFailureText("brainx-brain", "ssh_run", new InvalidOperationException("host_key_mismatch"));
            string otherTool = AgentToolService.McpFailureText("xdreamer", "render", new TimeoutException("MCP request 'tools/call' timed out after 180000ms"));
            Check("S3i a timed-out MCP call says the work may still be running and to check before sending it again (ssh: ssh_tail); another failure stays as it was",
                timedOut.StartsWith("MCP call failed (brainx-brain/ssh_run): MCP request 'tools/call' timed out after 180000ms")
                && timedOut.Contains("NOT a refusal") && timedOut.Contains("may still be running") && timedOut.Contains("Do not send the same call again") && timedOut.Contains("ssh_tail")
                && otherTool.Contains("NOT a refusal") && !otherTool.Contains("ssh_tail")
                && refused == "MCP call failed (brainx-brain/ssh_run): host_key_mismatch", timedOut);

            // ── run_tests with no tests: says so, and says how to check instead of hunting for pytest ──
            string pyMsg = AgentToolService.NoTestsMessage(pkg), docMsg = AgentToolService.NoTestsMessage(docs),
                noDir = AgentToolService.NoTestsMessage(null);
            var asResult = new ToolResult { Type = ToolType.RunTests, Success = false, Error = pyMsg };
            Check("S13 run_tests with no test runner: 'no tests', do not install a runner, run the code once (a Python example only for Python); still 'nothing to run'",
                AgentToolService.FoundNothingToRun(asResult)
                && pyMsg.Contains("has no tests") && pyMsg.Contains("Do not look for or install a test runner")
                && (pythonHere ? pyMsg.Contains("python -c") : !pyMsg.Contains("python -c"))
                && !docMsg.Contains("python -c") && !noDir.Contains("python -c") && docMsg.Contains("run the code you changed"),
                $"{pyMsg.Length} chars");

            // ── described-not-done: only a file named right before its code block counts ──
            File.WriteAllText(Path.Combine(pkg, "shopkit", "cart.py"), "x");
            File.WriteAllText(Path.Combine(pkg, "shopkit", "pricing.py"), "x");
            string summary = "Added `remove_item` to cart.\n\n```python\n    def remove_item(self, name):\n        ...\n```\n\n"
                           + "Note: `total()` still calls `pricing.applyDiscount`, but pricing.py names it `apply_discount`.";
            string described = "Now update the cart in shopkit/cart.py:\n\n```python\n    def is_empty(self): ...\n```";
            string fenceLine = "Done.\n\n```python cart.py\nx = 1\n```";
            var s1 = CodeAgentService.ExtractMentionedProjectFiles(summary, pkg);
            var s2 = CodeAgentService.ExtractMentionedProjectFiles(described, pkg);
            var s3 = CodeAgentService.ExtractMentionedProjectFiles(fenceLine, pkg);
            var s4 = CodeAgentService.ExtractMentionedProjectFiles("cart.py is fine, no code here.", pkg);
            Check("S4 described-not-done: a file named after the code block is not 'described' (control: one named before it, or on the fence line, is)",
                s1.Count == 0 && s2.SetEquals(new[] { "cart.py" }) && s3.SetEquals(new[] { "cart.py" }) && s4.Count == 0,
                $"summary=[{string.Join(",", s1)}] described=[{string.Join(",", s2)}] fence=[{string.Join(",", s3)}] prose=[{string.Join(",", s4)}]");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // ── a failed tool's output reaches the model, not just "Exit code: 1" ──
        var compileFail = new ToolResult { Type = ToolType.RunCommand, Success = false, Error = "Exit code: 1",
            Output = "*** Error compiling 'shopkit\\shipping.py'...\n  File \"shopkit/shipping.py\", line 4\nSyntaxError: expected ':'\n[exit code 1]" };
        var bare = new ToolResult { Type = ToolType.ReadFile, Success = false, Error = "File not found: x.py" };
        var same = new ToolResult { Type = ToolType.RunCommand, Success = false, Error = "boom", Output = "step 1\nboom" };
        var onlyOut = new ToolResult { Type = ToolType.RunCommand, Success = false, Error = "", Output = "partial" };
        var ok = new ToolResult { Type = ToolType.ReadFile, Success = true, Output = "x = 1", Error = "ignored" };
        string t1 = AgentToolService.ModelText(compileFail);
        Check("S12 a failed command reaches the model with its output (the SyntaxError line), not just its exit code",
            t1.StartsWith("Exit code: 1\n") && t1.Contains("SyntaxError: expected ':'") && t1.Contains("shipping.py")
            && AgentToolService.ModelText(bare) == "File not found: x.py"
            && AgentToolService.ModelText(same) == "step 1\nboom"
            && AgentToolService.ModelText(onlyOut) == "partial"
            && AgentToolService.ModelText(ok) == "x = 1",
            t1.Replace("\n", " ⏎ "));

        // ── wrap_up repairs what it used to refuse ──
        var r1 = NextStep.FromArgsRepaired("Fixed slugify; the check passes.", "", "[\"Run tests\",\"Done\"]", out var e1, out var n1);
        var r2 = NextStep.FromArgsRepaired("Done.", "Push it?", "a|b|c", out var e2, out var n2);
        var r3 = NextStep.FromArgsRepaired(" ", "", "[\"Yes\"]", out var e3, out var n3);
        var r4 = NextStep.FromArgsRepaired("Done.", "Push it?", "[\"Yes\",\"No\"]", out var e4, out var n4);
        var strict = NextStep.FromArgs("Fixed slugify; the check passes.", "", "[\"Run tests\"]", out var eStrict);
        Check("S5 wrap_up: buttons without a question are dropped, not refused (the recap still shows)",
            r1 != null && e1 == null && r1.Actions.Count == 0 && r1.Ask == null && n1 != null && n1.Contains("left out")
            && strict == null && eStrict.Contains("ask"), $"note={n1} · strict FromArgs still refuses: {eStrict}");
        Check("S6 wrap_up: three buttons keep the first two; a missing recap is still refused; a good call is unchanged",
            r2 != null && r2.Actions.SequenceEqual(new[] { "a", "b" }) && n2 != null
            && r3 == null && e3 != null && e3.Contains("recap") && n3 == null
            && r4 != null && n4 == null && r4.Actions.Count == 2,
            $"r2={string.Join("|", r2?.Actions ?? new List<string>())} n2={n2} · e3={e3} · n4={n4 ?? "null"}");

    }
}
