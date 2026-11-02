using System.IO;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Lsp;
using CluadeX.Services.Problems;

namespace LspTest;

/// <summary>
/// Errors without any install (BuildOutputParser + BuildProblemsService) and the editor's messages to the chat:
///   B*  every toolchain's real output shape → file / line / column / severity / code, in a Thai folder with spaces and
///       parentheses, CRLF line ends, ANSI colours, Git Bash paths; noise that must NOT become a problem
///   T*  which toolchain a command or an output is
///   R*  the service: a run replaces its toolchain's last verdict (a clean build clears), a run that died says nothing,
///       stale after the file changes, unknown tools add then drop, out-of-project paths dropped, per-project scope,
///       `cd X &&`, the agent's tool results, the Check button (fake runner: one at a time, cancel), auto-check due
///   E*  the chat messages: fences that code cannot break, selection / results wording, server + build merge
/// </summary>
internal static partial class Program
{
    static void BuildProblems()
    {
        Console.WriteLine("B  build / check output → problems");
        // A folder name with Thai, spaces and parentheses — the worst case for every path pattern below.
        string proj = Path.Combine(Scratch, "งาน โปรเจกต์ (ใหม่)");
        string Make(string rel, string text = "x\n")
        {
            string full = Path.Combine(proj, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-10));   // older than any run below
            return full;
        }
        string program = Make("App/Program.cs");
        string other = Make("Lib/Other.cs");
        string appTs = Make("src/แอป.ts");
        string appJs = Make("src/app.js");
        string mainDart = Make("lib/หน้าหลัก.dart");
        string mainPy = Make("main.py");
        string mainRs = Make("src/main.rs");
        string mainGo = Make("main.go");
        string mainC = Make("native/main.c");

        static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
        static string Show(List<BuildProblem> ps) => string.Join("\n", ps.Select(p => $"{p.Path}|{p.Line}:{p.Column}-{p.EndLine}:{p.EndColumn}|{p.Severity}|{p.Code}|{p.Message}"));

        // B1 MSBuild, absolute path, project suffix, printed twice (inline + summary), CRLF, ANSI colour.
        var ps = BuildOutputParser.Parse(Crlf(
            $"  Determining projects to restore...\n" +
            $"\u001b[31m{program}(12,5): error CS1002: ; expected [{proj}\\App\\App.csproj]\u001b[0m\n" +
            $"{program}(14,9): warning CS0168: The variable 'e' is declared but never used [{proj}\\App\\App.csproj]\n" +
            $"Build FAILED.\n\n" +
            $"    {program}(12,5): error CS1002: ; expected [{proj}\\App\\App.csproj]\n" +
            $"    1 Warning(s)\n    1 Error(s)\n"), proj);
        Check("B1", "MSBuild: Thai/space/paren path, [project] cut, summary repeat folded, CRLF + ANSI",
            ps.Count == 2 && ps[0].Path == program && ps[0].Line == 12 && ps[0].Column == 5 && ps[0].Severity == LspSeverity.Error
            && ps[0].Code == "CS1002" && ps[0].Message == "; expected" && ps[1].Severity == LspSeverity.Warning && ps[1].Code == "CS0168", Show(ps));

        // B2 node prefix + a full range; relative path (what run_build's distiller prints).
        ps = BuildOutputParser.Parse("3>App/Program.cs(3,9,3,10): error CS0103: The name 'x' does not exist in the current context\n", proj);
        Check("B2", "MSBuild: '3>' node prefix, (l,c,el,ec) range, relative path → the project's file",
            ps.Count == 1 && ps[0].Path == program && ps[0].EndLine == 3 && ps[0].EndColumn == 10 && ps[0].Code == "CS0103", Show(ps));

        // B3 tsc, both shapes.
        ps = BuildOutputParser.Parse(Crlf(
            "src/แอป.ts:3:7 - error TS2322: Type 'string' is not assignable to type 'number'.\n\n3 const n: number = 's';\n        ~\n\n" +
            "src/แอป.ts(5,1): error TS2304: Cannot find name 'foo'.\nFound 2 errors in the same file, starting at: src/แอป.ts:3\n"), proj);
        Check("B3", "tsc: --pretty 'file:l:c - error TSxxxx' and plain 'file(l,c): error TSxxxx'",
            ps.Count == 2 && ps.All(p => p.Path == appTs) && ps[0].Code == "TS2322" && ps[0].Line == 3 && ps[0].Column == 7
            && ps[1].Code == "TS2304" && ps[1].Line == 5 && ps[0].Message.StartsWith("Type 'string'"), Show(ps));

        // B4 eslint stylish: the file on its own line, then rows; then -f unix.
        ps = BuildOutputParser.Parse(Crlf(
            $"\n{appJs}\n  1:10  error    'x' is not defined           no-undef\n  4:3   warning  Unexpected console statement  no-console\n\n✖ 2 problems (1 error, 1 warning)\n"), proj);
        var unix = BuildOutputParser.Parse($"{appJs}:7:2: Missing semicolon. [Warning/semi]\n", proj, "eslint");
        Check("B4", "eslint: stylish block (file line + rows, rule kept) and -f unix ([Warning/semi])",
            ps.Count == 2 && ps[0].Path == appJs && ps[0].Line == 1 && ps[0].Column == 10 && ps[0].Code == "no-undef" && ps[0].Message == "'x' is not defined"
            && ps[1].Severity == LspSeverity.Warning && ps[1].Code == "no-console"
            && unix.Count == 1 && unix[0].Severity == LspSeverity.Warning && unix[0].Code == "semi" && unix[0].Message == "Missing semicolon.", Show(ps) + "\n" + Show(unix));

        // B5 dart / flutter analyze: today's dash format, the older bullet one, --format=machine.
        ps = BuildOutputParser.Parse(Crlf(
            "Analyzing งาน...\n\n" +
            "  error - lib\\หน้าหลัก.dart:12:5 - Undefined name 'x'. Try correcting the name - or defining it. - undefined_identifier\n" +
            "warning - lib\\หน้าหลัก.dart:3:8 - Unused import: 'dart:io'. - unused_import\n" +
            "   info - lib\\หน้าหลัก.dart:20:7 - Prefer const with constant constructors. - prefer_const_constructors\n\n3 issues found.\n"), proj);
        var bullet = BuildOutputParser.Parse("  error • Undefined name 'y' • lib/หน้าหลัก.dart:7:3 • undefined_identifier\n", proj);
        var machine = BuildOutputParser.Parse($"ERROR|COMPILE_TIME_ERROR|UNDEFINED_IDENTIFIER|{mainDart}|9|4|3|Undefined name 'z'.\n", proj);
        Check("B5", "dart analyze: dash (message with ' - ' inside), bullet, machine (length → end column)",
            ps.Count == 3 && ps.All(p => p.Path == mainDart) && ps[0].Line == 12 && ps[0].Code == "undefined_identifier"
            && ps[0].Message == "Undefined name 'x'. Try correcting the name - or defining it." && ps[1].Severity == LspSeverity.Warning
            && ps[2].Severity == LspSeverity.Information
            && bullet.Count == 1 && bullet[0].Line == 7 && bullet[0].Path == mainDart
            && machine.Count == 1 && machine[0].Column == 4 && machine[0].EndColumn == 7 && machine[0].Code == "undefined_identifier",
            Show(ps) + "\n" + Show(bullet) + "\n" + Show(machine));

        // B6 Python: a crash inside a library is put where the project called it; py_compile's SyntaxError.
        ps = BuildOutputParser.Parse(Crlf(
            "Traceback (most recent call last):\n" +
            $"  File \"{mainPy}\", line 5, in <module>\n    data = json.loads(s)\n" +
            "  File \"C:\\Python312\\Lib\\json\\__init__.py\", line 346, in loads\n    return _default_decoder.decode(s)\n" +
            "json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)\n"), proj);
        var syntax = BuildOutputParser.Parse(Crlf($"  File \"{mainPy}\", line 3\n    print(\"x\"\n         ^\nSyntaxError: '(' was never closed\n"), proj);
        Check("B6", "Python: traceback → the project's own frame (not json/__init__.py); py_compile SyntaxError",
            ps.Count == 1 && ps[0].Path == mainPy && ps[0].Line == 5 && ps[0].Message.Contains("Expecting value")
            && syntax.Count == 1 && syntax[0].Line == 3 && syntax[0].Code == "SyntaxError", Show(ps) + "\n" + Show(syntax));

        // B7 pyflakes (no severity: style → warning, undefined name → error), mypy (rule at the end, note hidden).
        var flakes = BuildOutputParser.Parse("main.py:1:1: 'os' imported but unused\nmain.py:3:7: undefined name 'y'\n", proj, "pyflakes");
        var mypy = BuildOutputParser.Parse("main.py:4: error: Incompatible types in assignment (expression has type \"str\", variable has type \"int\")  [assignment]\nmain.py:4: note: See https://mypy.rtfd.io\n", proj, "mypy");
        Check("B7", "pyflakes: unused import = warning, undefined name = error; mypy: [rule] → code, note → hint",
            flakes.Count == 2 && flakes[0].Severity == LspSeverity.Warning && flakes[1].Severity == LspSeverity.Error
            && mypy.Count == 2 && mypy[0].Code == "assignment" && mypy[0].Column == 0 && !mypy[0].Message.Contains("[assignment]")
            && mypy[1].Severity == LspSeverity.Hint, Show(flakes) + "\n" + Show(mypy));

        // B8 rustc / cargo: the human format (head line, then the arrow), the short one; "could not compile" is no problem.
        ps = BuildOutputParser.Parse(Crlf(
            "   Compiling demo v0.1.0\nerror[E0308]: mismatched types\n --> src\\main.rs:4:18\n  |\n4 |     let n: i32 = \"x\";\n  |                  ^^^ expected `i32`\n\n" +
            "warning: unused variable: `y`\n  --> src/main.rs:2:9\n\nerror: could not compile `demo` (bin \"demo\") due to 1 previous error\n"), proj);
        var shortRs = BuildOutputParser.Parse("src/main.rs:4:18: error[E0308]: mismatched types\n", proj);
        Check("B8", "cargo: human format (error[E0308] + --> line), short format; the closing 'could not compile' is ignored",
            ps.Count == 2 && ps[0].Path == mainRs && ps[0].Code == "E0308" && ps[0].Line == 4 && ps[0].Column == 18
            && ps[1].Severity == LspSeverity.Warning && ps[1].Line == 2
            && shortRs.Count == 1 && shortRs[0].Code == "E0308", Show(ps) + "\n" + Show(shortRs));

        // B9 go, gcc, and Git Bash's /c/… paths.
        string bashPath = "/" + char.ToLowerInvariant(mainGo[0]) + mainGo[2..].Replace('\\', '/');
        ps = BuildOutputParser.Parse($"# example.com/demo\n./main.go:12:5: undefined: x\nnative/main.c:3:10: fatal error: stdio.h: No such file or directory\n{bashPath}:8:1: missing return\n", proj);
        Check("B9", "go ('./x.go:l:c: msg' under '# pkg'), gcc 'fatal error', a Git Bash /e/… path",
            ps.Count == 3 && ps[0].Path == mainGo && ps[0].Message == "undefined: x" && ps[1].Path == mainC && ps[1].Severity == LspSeverity.Error
            && ps[2].Path == mainGo && ps[2].Line == 8, Show(ps));

        // B10 noise: none of these is a problem.
        ps = BuildOutputParser.Parse(Crlf(
            "12:30:45: build started\nNow listening on: http://localhost:5000/api:12\nBuild succeeded.\n    0 Warning(s)\n" +
            $"   at Demo.Tests.Run() in {proj}\\Tests\\FooTests.cs:line 42\nerror: could not compile\nwarning: 3 warnings emitted\n" +
            "Time Elapsed 00:00:01.23\nC:\\Program Files\\dotnet\\sdk\\8.0.100\\Sdks\\Foo:12: not a source file\n"), proj);
        Check("B10", "noise (timestamps, URLs, summaries, stack-trace ':line 42', locationless errors) → nothing", ps.Count == 0, Show(ps));

        // B11 problem → editor diagnostic (0-based; no column = the whole line).
        var d1 = new BuildProblem(program, 12, 5, 0, 0, LspSeverity.Error, "m", "CS1", "dotnet").ToDiagnostic();
        var d2 = new BuildProblem(program, 4, 0, 0, 0, LspSeverity.Error, "m", "", "mypy").ToDiagnostic();
        Check("B11", "to editor positions: 1-based → 0-based; no column = the whole line",
            d1.StartLine == 11 && d1.StartCharacter == 4 && d1.EndCharacter == 4 && d2.StartLine == 3 && d2.StartCharacter == 0 && d2.EndCharacter > 1000);

        Console.WriteLine("T  toolchains");
        var cases = new (string? Cmd, string Out, string? Want)[]
        {
            ("dotnet build \"App/App.csproj\" -nologo", "", "dotnet"),
            ("cd \"src/App\" && dotnet test", "", "dotnet"),
            ("npx tsc --noEmit", "", "tsc"),
            ("\"C:\\p\\node_modules\\.bin\\tsc.cmd\" --noEmit -p .", "", "tsc"),
            ("npx eslint src", "", "eslint"),
            ("flutter analyze --no-pub", "", "dart"),
            ("dart.bat analyze", "", "dart"),
            ("cargo check --message-format=short", "", "cargo"),
            ("go vet ./...", "", "go"),
            ("python -m pyflakes .", "", "pyflakes"),
            ("pytest -q", "", "pytest"),
            ("python -m py_compile main.py", "", "pysyntax"),
            ("echo hello", "", null),
            ("npm run build", "Build succeeded.\n", "dotnet"),
            (null, "Analyzing app...\nNo issues found!\n", "dart"),
        };
        var wrong = cases.Where(c => BuildOutputParser.Toolchain(c.Cmd, c.Out) != c.Want)
                         .Select(c => $"{c.Cmd ?? "(output)"} → {BuildOutputParser.Toolchain(c.Cmd, c.Out) ?? "null"} (want {c.Want ?? "null"})").ToList();
        Check("T1", "each command / output signature maps to its toolchain, an unknown one to none", wrong.Count == 0, string.Join("\n", wrong));
        Check("T2", "clean-run signatures: a finished check that found nothing may clear; a crash may not",
            BuildOutputParser.LooksClean("Build succeeded.") && BuildOutputParser.LooksClean("Analyzing x...\nNo issues found!")
            && BuildOutputParser.LooksClean("OK (12 files checked)") && BuildOutputParser.LooksClean("Passed!  - Failed:     0, Passed:    12")
            && !BuildOutputParser.LooksClean("'dotnet' is not recognized as an internal or external command") && !BuildOutputParser.LooksClean(""));

        ServiceRuns(proj, program, other, appTs, mainPy, Make);
        ChatPrompts(program);
    }

    static void ServiceRuns(string proj, string program, string other, string appTs, string mainPy, Func<string, string, string> make)
    {
        Console.WriteLine("R  the problems service");
        var fs = new FileSystemService { WorkingDirectory = proj };
        var svc = new BuildProblemsService(null, fs);
        int changes = 0;
        svc.Changed += () => Interlocked.Increment(ref changes);

        string failing = $"{program}(12,5): error CS1002: ; expected [{proj}\\App\\App.csproj]\n{other}(3,1): error CS0246: The type 'Foo' could not be found\nBuild FAILED.\n";
        int n = svc.Ingest(failing, proj, "dotnet build", succeeded: false);
        Check("R1", "a failing build's errors are stored per file, fresh", n == 2 && svc.ProblemsFor(program).Count == 1 && !svc.ProblemsFor(program)[0].Stale && changes > 0,
            $"n={n}");

        svc.Ingest("'dotnet' is not recognized as an internal or external command,\noperable program or batch file.\n", proj, "dotnet build", succeeded: false);
        Check("R2", "a run that died before compiling (no errors, not clean) leaves the last verdict alone", svc.ProblemsFor(program).Count == 1);

        // One project's build speaks only for that project's folder.
        svc.Ingest("Build succeeded.\n    0 Error(s)\n", proj, "dotnet build \"Lib/Lib.csproj\"", succeeded: true);
        Check("R3", "a clean build of Lib/Lib.csproj clears Lib's errors only", svc.ProblemsFor(other).Count == 0 && svc.ProblemsFor(program).Count == 1);

        svc.Ingest("Build succeeded.\n", proj, "dotnet build", succeeded: true);
        Check("R4", "a clean build of the whole project clears the rest", svc.ProblemsFor(program).Count == 0 && svc.Count == 0, $"count={svc.Count}");

        // Stale: the file changed after the run.
        svc.Ingest(failing, proj, "dotnet build", succeeded: false);
        File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddMinutes(1));
        var stale = svc.ProblemsFor(program);
        Check("R5", "after the file changes its problem is kept but marked stale (listed dimmed, not drawn)",
            stale.Count == 1 && stale[0].Stale && svc.All().Last().Stale, $"{stale.Count}");
        File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddMinutes(-10));
        svc.NoteEdited(program);   // the agent's write_file says so even within the file system's time resolution
        Check("R6", "an edit the agent reports makes it stale too", svc.ProblemsFor(program).Single().Stale);

        // An unknown tool's output: adds; replaced per file; dropped once the file changes (nothing would refresh it).
        svc.Ingest(Crlf2($"Traceback (most recent call last):\n  File \"{mainPy}\", line 2, in <module>\nNameError: name 'q' is not defined\n"), proj, "node run.js && python main.py", succeeded: false);
        bool added = svc.ProblemsFor(mainPy).Count == 1;
        File.SetLastWriteTimeUtc(mainPy, DateTime.UtcNow.AddMinutes(1));
        Check("R7", "an unknown command's error is added, then dropped when its file changes", added && svc.ProblemsFor(mainPy).Count == 0);

        // Out-of-project paths (the SDK's .targets) are not the user's problems; a `cd X &&` changes where paths start.
        svc.Clear();
        svc.Ingest("C:\\Program Files\\dotnet\\sdk\\8.0.100\\Microsoft.Common.CurrentVersion.targets(2413,5): warning MSB3277: conflicts\n", proj, "dotnet build", succeeded: true);
        svc.Ingest("Program.cs(7,3): error CS1513: } expected\n", proj, "cd \"App\" && dotnet build", succeeded: false);
        var app = svc.ProblemsFor(program);
        Check("R8", "SDK .targets warnings dropped; `cd \"App\" && …` resolves Program.cs inside App",
            svc.Count == 1 && app.Count == 1 && app[0].Problem.Line == 7, $"count={svc.Count}");

        // run_build's own result, through the agent's event: output starts "$ cmd", the root is the agent's folder.
        svc.Clear();
        svc.OnToolResult(new ToolResult
        {
            Type = ToolType.RunBuild, ToolName = "run_build", Success = false,
            Output = "$ dotnet build   (detected: the project root)\nBuild FAILED — 1 error(s):\nApp/Program.cs(3,9): error CS0103: The name 'x' does not exist in the current context",
            Error = "Build failed (exit code 1)",
        });
        Check("R9", "the agent's run_build result (via OnToolExecuted) lands in Problems off the calling thread",
            WaitFor(() => svc.ProblemsFor(program).Count == 1, 5000));
        svc.OnToolResult(new ToolResult { Type = ToolType.WriteFile, ToolName = "write_file", Success = true, FilePath = "App/Program.cs" });
        Check("R10", "the agent's write to that file marks it stale at once", svc.ProblemsFor(program).SingleOrDefault()?.Stale == true);

        // The Check button with a fake runner: what it prints is read; one check at a time; Stop stops it.
        make("tsconfig.json", "{}");
        make("node_modules/.bin/tsc.cmd", "@echo off\n");
        var checks = svc.ChecksFor(proj);
        var tsc = checks.FirstOrDefault(c => c.Key == "tsc");
        Check("R11", "a project with tsconfig.json + node_modules\\.bin\\tsc.cmd offers 'tsc --noEmit' (safe: runs no project code)",
            tsc != null && tsc.Safe && !checks.Any(c => c.Key == "dotnet" && c.Safe), string.Join(", ", checks.Select(c => c.Label)));
        if (tsc != null)
        {
            var gate = new TaskCompletionSource();
            string? ranIn = null;
            svc.Runner = async (cmd, dir, timeout, ct) =>
            {
                ranIn = dir;
                await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return (1, "", false);
                return (2, "src/แอป.ts(5,1): error TS2304: Cannot find name 'foo'.\n", false);
            };
            var first = svc.RunChecksAsync(proj, new[] { tsc });
            bool running = WaitFor(() => svc.IsChecking, 2000);
            var second = svc.RunChecksAsync(proj, new[] { tsc }).GetAwaiter().GetResult();
            gate.SetResult();
            var outcome = first.GetAwaiter().GetResult();
            Check("R12", "Check runs the checker in the project folder and lists what it prints; a second click while it runs does nothing",
                running && second == null && outcome != null && outcome.Errors == 1 && ranIn == proj && svc.ProblemsFor(appTs).Count == 1 && !svc.IsChecking,
                $"running={running} second={second} errors={outcome?.Errors} ranIn={ranIn}");

            var never = new TaskCompletionSource();
            svc.Runner = async (cmd, dir, timeout, ct) =>
            {
                await Task.WhenAny(never.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                return (1, "", false);
            };
            var stopped = svc.RunChecksAsync(proj, new[] { tsc });
            WaitFor(() => svc.IsChecking, 2000);
            svc.CancelCheck();
            bool ended = stopped.Wait(5000);
            Check("R13", "Stop check ends it (and leaves what was found alone)",
                ended && stopped.Result?.Cancelled == true && !svc.IsChecking && svc.ProblemsFor(appTs).Count == 1);

            // Auto-check after a run: due when a .ts file was written after tsc's last verdict, not after.
            svc.NoteEdited(appTs);
            bool due = svc.PendingAutoChecks(proj).Any(c => c.Key == "tsc");
            svc.Ingest("", proj, "npx tsc --noEmit", succeeded: true);   // empty output: nothing to read
            svc.Ingest("Found 0 errors.\n", proj, "npx tsc --noEmit", succeeded: true);
            bool doneAfter = !svc.PendingAutoChecks(proj).Any(c => c.Key == "tsc");
            svc.NoteEdited(program);
            Check("R14", "an AI edit to a .ts file makes tsc due; its own tsc run after the edit settles it; a .cs edit never makes a safe check due",
                due && doneAfter && !svc.PendingAutoChecks(proj).Any(), $"due={due} doneAfter={doneAfter}");
        }

        // The editor's Python check script is real Python: run it when this machine has one (GAP otherwise).
        make("bad.py", "def f(:\n    pass\n");
        make("ok.py", "print('สวัสดี')\n");
        var real = new BuildProblemsService(null, null);
        var check = real.ChecksFor(proj).FirstOrDefault(c => c.Key == "pysyntax");
        if (check == null) Console.WriteLine("  GAP  R15   no Python on this machine: the Python syntax check did not run");
        else
        {
            // A harness starts no process by default (ProcessLaunchPolicy); this check's whole point is real Python output.
            bool wasDry = CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun;
            CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
            ProblemCheckOutcome? outcome;
            string raw = "";
            bool ok;
            try
            {
                outcome = real.RunChecksAsync(proj, new[] { check }).GetAwaiter().GetResult();
                var bad = real.ProblemsFor(Path.Combine(proj, "bad.py"));
                ok = outcome != null && bad.Count == 1 && bad[0].Problem.Line == 1 && real.ProblemsFor(Path.Combine(proj, "ok.py")).Count == 0;
                if (!ok && ShellRunner.RunAsync(ShellRunner.Shell.Cmd, check.Command, proj, 60_000, default).GetAwaiter().GetResult() is { } r)
                    raw = $"exit={r.ExitCode} output={r.Output}";
            }
            finally { CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = wasDry; }
            Check("R15", "the Python syntax check (real python, AST only) finds bad.py:1 in a Thai folder and passes ok.py",
                ok, $"check={check.Command} outcome={outcome}\n{raw}");
        }
    }

    static string Crlf2(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    static void ChatPrompts(string program)
    {
        Console.WriteLine("E  editor → chat messages");
        var keysType = typeof(EditorLanguageService).Assembly.GetType("CluadeX.Services.Localization.LspKeys");
        var keys = (Dictionary<string, Dictionary<string, string>>)keysType!.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        string fenced = EditorChatPrompts.Fence("var s = \"```\";\r\nreturn s;", "csharp");
        Check("E1", "a fence the code cannot close early (code with ``` gets ````), CRLF → LF",
            fenced.StartsWith("````csharp\n") && fenced.EndsWith("\n````") && !fenced.Contains('\r'), fenced);

        string fix = EditorChatPrompts.Selection(keys["editor.ask.fix"]["th"], "App/Program.cs", 12, 14, "int x = 1\nint y = 2\nint z = 3", "csharp");
        Check("E2", "'fix this' names the project file and the line range, and fences the code with its language",
            fix.Contains("App/Program.cs") && fix.Contains("12–14") && fix.Contains("```csharp\nint x = 1"), fix);

        var rows = Enumerable.Range(1, 75).Select(i => ("src/a.ts", i, "  foo(" + i + ")  ")).ToList();
        string results = EditorChatPrompts.Results("References to foo — 75 results:", rows, keys["search.toChatMore"]["en"]);
        Check("E3", "results as context: one 'path:line: text' row each, capped at 60 with '… and 15 more'",
            results.Contains("src/a.ts:1: foo(1)") && results.Contains("src/a.ts:60: foo(60)") && !results.Contains("src/a.ts:61:") && results.EndsWith("… and 15 more"), results[^80..]);

        var merge = typeof(CluadeX.Views.CodeEditorView).GetMethod("MergeDiagnostics", BindingFlags.NonPublic | BindingFlags.Static)!;
        var server = new List<EditorDiagnostic> { new(11, 4, 11, 5, LspSeverity.Error, "; expected", "csharp", "CS1002") };
        var build = new[]
        {
            new BuildProblem(program, 12, 5, 0, 0, LspSeverity.Error, "; expected", "CS1002", "dotnet"),       // the same error
            new BuildProblem(program, 20, 1, 0, 0, LspSeverity.Error, "other", "CS0103", "dotnet"),
            new BuildProblem(program, 20, 1, 0, 0, LspSeverity.Error, "other", "CS0103", "dotnet"),            // build + test run
        };
        var merged = (List<EditorDiagnostic>)merge.Invoke(null, new object[] { server, build })!;
        Check("E4", "the server's and the build's errors merge without the same error twice", merged.Count == 2 && merged[1].StartLine == 19,
            string.Join(" | ", merged.Select(m => $"{m.StartLine}:{m.Code}")));
    }
}
