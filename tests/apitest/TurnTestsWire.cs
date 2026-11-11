// The per-turn test card (owner 2026-11-11: the table Claude Code sessions end with). The pure parts are proven here —
// which commands are test runs, the suite names, the counts from each runner and project harness, one record per tool
// row (native and CLI names), how each failure is read (REAL / FLAKY / UNRELATED / FIXED), the skipped list and the
// card's words in en + th. The card on the real chat page is in chattest (CHATTEST_ONLY=TT).
using System.IO;
using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class TurnTestsWire
{
    static readonly Dictionary<string, string> En = new()
    {
        ["tests.passed"] = "{0} passed", ["tests.failed"] = "{0} failed", ["tests.skipped"] = "{0} skipped",
        ["tests.ok"] = "passed", ["tests.notOk"] = "failed", ["tests.running"] = "running…",
        ["tests.kind.real"] = "REAL", ["tests.kind.flaky"] = "FLAKY", ["tests.kind.unrelated"] = "UNRELATED", ["tests.kind.fixed"] = "FIXED",
        ["tests.plain"] = "Tests: {0}", ["tests.status.none"] = "No tests ran this turn", ["tests.skippedTitle"] = "Not run — out of scope",
        ["tests.title"] = "Tests", ["tests.status.allPassed"] = "{0} suite(s) · all passed", ["tests.status.someFailed"] = "{0} of {1} suite(s) failed",
        ["tests.status.pending"] = "still running in the background", ["tests.runs"] = "×{0}", ["tests.runsTip"] = "Ran {0} times",
        ["tests.commandTip"] = "Command: {0}", ["tests.moreFailures"] = "+{0} more failing",
        ["tests.why.real"] = "still fails, and it touches what this turn changed", ["tests.why.realNoChanges"] = "still fails",
        ["tests.why.flaky"] = "a rerun passed with nothing changed in between",
        ["tests.why.unrelated"] = "still fails, but it touches none of the {0} file(s) this turn changed",
        ["tests.why.fixed"] = "passed after a fix later in this turn", ["tests.unnamed"] = "the runner named no test",
    };

    static readonly Dictionary<string, string> Th = new()
    {
        ["tests.passed"] = "ผ่าน {0}", ["tests.failed"] = "ไม่ผ่าน {0}", ["tests.skipped"] = "ข้าม {0}",
        ["tests.status.someFailed"] = "ไม่ผ่าน {0} จาก {1} ชุด", ["tests.kind.unrelated"] = "ไม่เกี่ยว",
        ["tests.why.unrelated"] = "ยังไม่ผ่าน แต่ไม่เกี่ยวกับ {0} ไฟล์ที่เทิร์นนี้แก้",
    };

    static ToolResult Row(string tool, ToolType type, bool ok, string output, string? command = null, string? file = null) => new()
    {
        ToolName = tool, Type = type, Success = ok, Output = output, Error = ok ? "" : "exit code 1",
        Arguments = command == null ? new() : new() { ["command"] = command }, FilePath = file,
    };

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        const string S = "turntests";
        section("Turn test card — test runs, suite names, counts, failure reading, skipped, words");

        // ─── which commands are test runs ───
        string[] yes =
        {
            "dotnet test CluadeX.sln -c Debug", "dotnet run --project tests/apitest -c Debug -- --offline",
            "$env:CHATTEST_ONLY='TT'; dotnet run --project tests\\chattest -c Debug", "CHATTEST_ONLY=TT dotnet run --project tests/chattest",
            "npm test", "npm run test:unit", "pnpm test", "npx vitest run", "pytest -q tests/", "python -m pytest", "go test ./...",
            "cargo test", "flutter test", "php artisan test --filter=Wallet", "vendor/bin/phpunit", "./gradlew test", "mvn -q test",
            "cd web && npm test", "pwsh Scripts/try-branch.ps1 feat/x -Smoke",
        };
        string[] no =
        {
            "dotnet build -c Debug", "npm install jest", "pip install pytest", "git status", "dotnet test --list-tests",
            "echo latest", "cat tests/apitest/Program.cs", "npm run build", "ls tests",
        };
        var missed = yes.Where(c => !TurnTests.LooksLikeTestCommand(c)).ToList();
        check(S, "test runners are test runs", missed.Count == 0, string.Join(" | ", missed));
        var wrong = no.Where(TurnTests.LooksLikeTestCommand).ToList();
        check(S, "builds, installs, reads are not", wrong.Count == 0, string.Join(" | ", wrong));

        // ─── suite names ───
        var names = new (string Cmd, string Want)[]
        {
            ("dotnet run --project tests/apitest -c Debug -- --offline", "apitest --offline"),
            ("$env:CHATTEST_ONLY='TT'; dotnet run --project tests\\chattest -c Debug", "chattest (TT)"),
            ("CHATTEST_ONLY=RR dotnet run --project tests/chattest", "chattest (RR)"),
            ("cd D:/x && dotnet test App.sln", "dotnet test App.sln"),
            ("pwsh -File Scripts/try-branch.ps1 feat/x -Smoke", "try-branch -Smoke"),
            ("npm test 2>&1 | tail -20", "npm test"),
        };
        var badNames = names.Where(n => TurnTests.SuiteName(n.Cmd) != n.Want).Select(n => $"{n.Cmd} → '{TurnTests.SuiteName(n.Cmd)}'").ToList();
        check(S, "suite names are short and stable", badNames.Count == 0, string.Join(" | ", badNames));

        // ─── counts: the runners verify_change already read, and the project harnesses ───
        var dn = EngineeringToolModule.ParseTestCounts("  Failed App.Tests.LoginTests.Rejects_bad_password [12 ms]\nFailed!  - Failed:     1, Passed:    41, Skipped:     2, Total:    44");
        check(S, "dotnet test still read", dn is { Passed: 41, Failed: 1, Skipped: 2 } && dn.Failing.SequenceEqual(["App.Tests.LoginTests.Rejects_bad_password"]),
            $"{dn.Passed}/{dn.Failed}/{dn.Skipped} [{string.Join(",", dn.Failing)}]");
        var api = EngineeringToolModule.ParseTestCounts("  PASS ok one\n  FAIL real ~/.cluadex untouched   wrote 3 lines\n\n2213 passed, 1 failed\n  ✗ x");
        check(S, "apitest harness: counts and the failing name", api is { Passed: 2213, Failed: 1 } && api.Failing.Contains("real ~/.cluadex untouched"),
            $"{api.Passed}/{api.Failed} [{string.Join(",", api.Failing)}]");
        var chat = EngineeringToolModule.ParseTestCounts("PASS  RR1 card\nFAIL  RR2d reopens the question   [timing]\n\n## Result (CHATTEST_ONLY=RR): 32 pass / 1 FAIL");
        check(S, "chattest harness: 'N pass / M FAIL'", chat is { Passed: 32, Failed: 1 } && chat.Failing.SequenceEqual(["RR2d reopens the question"]),
            $"{chat.Passed}/{chat.Failed} [{string.Join(",", chat.Failing)}]");
        var bar = EngineeringToolModule.ParseTestCounts("\n═══ 50 pass · 0 FAIL ═══");
        check(S, "'═══ N pass · M FAIL ═══'", bar is { Passed: 50, Failed: 0 }, $"{bar.Passed}/{bar.Failed}");
        var fl = EngineeringToolModule.ParseTestCounts("00:03 +11 ~1 -2: loading\n00:04 +12 ~1 -2: Some tests failed.");
        check(S, "flutter test's last line", fl is { Passed: 12, Failed: 2, Skipped: 1 }, $"{fl.Passed}/{fl.Failed}/{fl.Skipped}");
        var py = EngineeringToolModule.ParseTestCounts("FAILED tests/test_auth.py::test_login - assert 1 == 2\n==== 1 failed, 10 passed in 0.52s ====");
        check(S, "pytest still read", py is { Passed: 10, Failed: 1 } && py.Failing.Contains("tests/test_auth.py::test_login"), $"{py.Passed}/{py.Failed}");

        // ─── one record per tool row ───
        string verifyOut = "VERDICT: VERIFIED WITH GAPS\n\n1. Build: OK — $ dotnet build\n2. Binaries:\n"
            + "3. Tests: PASSED — 36 passed, 0 failed — $ dotnet run --project tests/apitest -- --recover\n4. Baseline: none\n";
        var v = TurnTests.FromToolResult(Row("verify_change", ToolType.VerifyChange, true, verifyOut), 12.4);
        check(S, "verify_change row → its test step", v is { Suite: "apitest --recover", Passed: 36, Failed: 0, Success: true, Seconds: 12.4 },
            v == null ? "null" : $"{v.Suite} {v.Passed}/{v.Failed} ok={v.Success}");
        var vCli = TurnTests.FromToolResult(Row("mcp__cluadex__verify_change", ToolType.McpTool, false,
            verifyOut.Replace("PASSED — 36 passed, 0 failed", "FAILED — 35 passed, 1 failed") + "\n  FAIL broken one   x"), null);
        check(S, "a CLI's verify_change (MCP name) is read the same", vCli is { Passed: 35, Failed: 1, Success: false } && vCli.Failing.Contains("broken one"),
            vCli == null ? "null" : $"{vCli.Passed}/{vCli.Failed} [{string.Join(",", vCli.Failing)}]");
        var vBuildFail = TurnTests.FromToolResult(Row("verify_change", ToolType.VerifyChange, false, "VERDICT: FAILED — the build did not succeed.\n\nerror CS1002"), null);
        var vSkip = TurnTests.FromToolResult(Row("verify_change", ToolType.VerifyChange, true, "VERDICT: VERIFIED WITH GAPS\n3. Tests: skipped on request\n"), null);
        check(S, "a verify whose tests never ran is no test run", vBuildFail == null && vSkip == null, "");
        var vBg = TurnTests.FromToolResult(Row("verify_change", ToolType.VerifyChange, true,
            "3. Tests: RUNNING in the background as bg#12 (watch w1) — $ dotnet run --project tests/chattest\n"), null);
        check(S, "tests_background is pending, not passed", vBg is { Pending: true, Success: true, Passed: null }, v == null ? "null" : $"{vBg?.Pending}");
        var rt = TurnTests.FromToolResult(Row("run_tests", ToolType.RunTests, true, "$ dotnet test   (detected: App.sln)\nPassed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12"), 3);
        check(S, "run_tests: command from its '$' line", rt is { Suite: "dotnet test", Passed: 12, Failed: 0, Success: true }, rt == null ? "null" : $"{rt.Suite}");
        var bash = TurnTests.FromToolResult(Row("run_command", ToolType.RunCommand, false, "PASS a\nFAIL b   x\n\n10 passed, 1 failed",
            "dotnet run --project tests/tooltest -c Debug"), 40);
        check(S, "a CLI's Bash row running a harness is a run", bash is { Suite: "tooltest", Passed: 10, Failed: 1, Success: false }, bash == null ? "null" : bash.Suite);
        var notTest = TurnTests.FromToolResult(Row("run_command", ToolType.RunCommand, true, "On branch main", "git status"), 1);
        var edit = TurnTests.FromToolResult(Row("edit_file", ToolType.EditFile, true, "ok", file: "a.cs"), 1);
        check(S, "other rows are not test runs", notTest == null && edit == null, "");
        var exitOnly = TurnTests.FromToolResult(Row("run_command", ToolType.RunCommand, false, "something broke", "npm test"), 2);
        check(S, "a failed run with no counts still counts as failed", exitOnly is { Success: false, Passed: null, Failed: null }, "");
        var passedButFails = TurnTests.FromToolResult(Row("run_command", ToolType.RunCommand, true, "3 passed, 2 failed", "pytest"), 2);
        check(S, "exit 0 with failures reported is not a pass", passedButFails is { Success: false, Failed: 2 }, "");

        // ─── how failures are read ───
        string root = Path.Combine(Path.GetTempPath(), "cx-turntests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string login = Path.Combine(root, "LoginView.xaml"), auth = Path.Combine(root, "AuthService.cs"), testFile = Path.Combine(root, "SessionChecks.cs");
            File.WriteAllText(login, "<Grid/>");
            File.WriteAllText(auth, "class AuthService {}");
            File.WriteAllText(testFile, "Check(\"keeps the session alive\", …)");

            var t = new TurnTests();
            // chattest: RR2d fails, the same command reruns and passes, nothing edited between → FLAKY
            t.Note(Row("run_command", ToolType.RunCommand, false, "FAIL  RR2d reopens   [timing]\n## Result (CHATTEST_ONLY=RR): 32 pass / 1 FAIL",
                "CHATTEST_ONLY=RR dotnet run --project tests/chattest"), 50);
            t.Note(Row("run_command", ToolType.RunCommand, true, "## Result (CHATTEST_ONLY=RR): 33 pass / 0 FAIL",
                "CHATTEST_ONLY=RR  dotnet run --project tests/chattest"), 48);
            // apitest: fails, an edit, then passes → FIXED
            t.Note(Row("run_command", ToolType.RunCommand, false, "  FAIL AuthService rejects empty   x\n\n40 passed, 1 failed", "dotnet run --project tests/apitest -- --auth"), 9);
            t.Note(Row("edit_file", ToolType.EditFile, true, "ok", file: auth), null);
            t.Note(Row("run_command", ToolType.RunCommand, true, "\n41 passed, 0 failed", "dotnet run --project tests/apitest -- --auth"), 9);
            // offline: one failure naming none of the changed files → UNRELATED; one naming AuthService → REAL;
            // one the changed test file names → REAL
            t.Note(Row("run_command", ToolType.RunCommand, false,
                "  FAIL real ~/.cluadex untouched   x\n  FAIL AuthServiceTokenRoundTrip   y\n  FAIL keeps the session alive   z\n\n2210 passed, 3 failed",
                "dotnet run --project tests/apitest -- --offline"), 125);
            t.NoteSkipped(["desktoptest — Services/Desktop unchanged", "desktoptest — Services/Desktop unchanged", "  - voicetest — no voice code touched"]);

            var changed = new[] { login, auth, testFile };
            var sum = t.Summarize(changed, File.ReadAllText);
            check(S, "one line per suite (a rerun is the same suite)", sum.Suites.Count == 3 && sum.Suites[0].Runs == 2 && sum.Suites[1].Runs == 2,
                string.Join(" | ", sum.Suites.Select(x => $"{x.Suite} ×{x.Runs}")));
            check(S, "each line is the suite's last run", sum.Suites[0] is { Success: true, Passed: 33 } && sum.Suites[1] is { Success: true, Passed: 41 }
                && sum.Suites[2] is { Success: false, Passed: 2210, Failed: 3, Seconds: 125 }, "");
            TestFailureKind? KindOf(string test) => sum.Failures.FirstOrDefault(f => f.Test == test)?.Kind;
            check(S, "a rerun that passed with no edit → FLAKY", KindOf("RR2d reopens") == TestFailureKind.Flaky, $"{KindOf("RR2d reopens")}");
            check(S, "a pass after an edit → FIXED", KindOf("AuthService rejects empty") == TestFailureKind.Fixed, $"{KindOf("AuthService rejects empty")}");
            check(S, "names none of the changed files → UNRELATED", KindOf("real ~/.cluadex untouched") == TestFailureKind.Unrelated, $"{KindOf("real ~/.cluadex untouched")}");
            check(S, "names a changed file → REAL", KindOf("AuthServiceTokenRoundTrip") == TestFailureKind.Real, $"{KindOf("AuthServiceTokenRoundTrip")}");
            check(S, "a changed file names the test → REAL", KindOf("keeps the session alive") == TestFailureKind.Real, $"{KindOf("keeps the session alive")}");
            check(S, "the worst come first", sum.Failures.Select(f => f.Kind).SequenceEqual(sum.Failures.Select(f => f.Kind)
                .OrderBy(k => k switch { TestFailureKind.Real => 0, TestFailureKind.Unrelated => 1, TestFailureKind.Flaky => 2, _ => 3 })),
                string.Join(",", sum.Failures.Select(f => f.Kind)));
            check(S, "skipped: each once, tidied", sum.Skipped.SequenceEqual(["desktoptest — Services/Desktop unchanged", "voicetest — no voice code touched"]),
                string.Join(" | ", sum.Skipped));

            // no files changed: nothing can be called unrelated
            var t2 = new TurnTests();
            t2.Note(Row("run_command", ToolType.RunCommand, false, "  FAIL whatever   x\n\n1 passed, 1 failed", "npm test"), 2);
            var s2 = t2.Summarize(Array.Empty<string>());
            check(S, "without changed files a failure stays REAL", s2.Failures is [{ Kind: TestFailureKind.Real, Test: "whatever" }], $"{s2.Failures.FirstOrDefault()?.Kind}");
            // a run that failed with no names: one unnamed line, never dropped
            var t3 = new TurnTests();
            t3.Note(Row("run_command", ToolType.RunCommand, false, "boom", "npm test"), 2);
            var s3 = t3.Summarize(changed, File.ReadAllText);
            check(S, "a failed run that named no test still gets a line", s3.Failures is [{ Test: "", Kind: TestFailureKind.Real }], $"{s3.Failures.Count}");

            // ─── JSON and words ───
            var back = TurnTestSummary.FromJson(sum.ToJson());
            check(S, "the card's data round-trips", back != null && back.Suites.Count == 3 && back.Failures.Count == sum.Failures.Count
                && back.Failures.Any(f => f.Kind == TestFailureKind.Unrelated) && back.Skipped.Count == 2 && back.ChangedFiles == 3, "");
            check(S, "unreadable JSON is no card", TurnTestSummary.FromJson("{nope") == null && TurnTestSummary.FromJson("") == null, "");

            string LEn(string k) => En.TryGetValue(k, out var x) ? x : k;
            string LTh(string k) => Th.TryGetValue(k, out var x) ? x : LEn(k);
            var view = new TurnTestsView(sum, LEn);
            check(S, "header: how many suites failed", view.State == "fail" && view.StatusText == "1 of 3 suite(s) failed", view.StatusText);
            check(S, "result column like '2210 passed · 3 failed'", view.Suites[2].ResultText == "2210 passed · 3 failed" && view.Suites[0].ResultText == "33 passed"
                && view.Suites[0].RunsText == "×2" && view.Suites[2].TimeText == "2m 05s" && view.Suites[0].TimeText == "48s",
                string.Join(" | ", view.Suites.Select(x => $"{x.ResultText} {x.TimeText} {x.RunsText}")));
            var unrel = view.Failures.First(f => f.Kind == "unrelated");
            check(S, "an unrelated line says why", unrel.Why == "still fails, but it touches none of the 3 file(s) this turn changed" && unrel.KindText == "UNRELATED", unrel.Why);
            var viewTh = new TurnTestsView(sum, LTh);
            check(S, "Thai words", viewTh.StatusText == "ไม่ผ่าน 1 จาก 3 ชุด" && viewTh.Suites[2].ResultText == "ผ่าน 2210 · ไม่ผ่าน 3"
                && viewTh.Failures.First(f => f.Kind == "unrelated").Why == "ยังไม่ผ่าน แต่ไม่เกี่ยวกับ 3 ไฟล์ที่เทิร์นนี้แก้", viewTh.StatusText + " / " + viewTh.Suites[2].ResultText);
            string plain = TurnTestsView.PlainLine(sum, LEn);
            check(S, "the row's plain text names suites, failures and skips", plain.StartsWith("🧪 Tests: chattest (RR) 33 passed") && plain.Contains("UNRELATED real ~/.cluadex untouched")
                && plain.Contains("desktoptest"), plain);
            var allPass = new TurnTests();
            allPass.Note(Row("run_tests", ToolType.RunTests, true, "$ npm test\nTests: 5 passed, 5 total"), 1.5);
            var apView = new TurnTestsView(allPass.Summarize(changed), LEn);
            check(S, "all passed: green header, no failure lines", apView.State == "pass" && apView.StatusText == "1 suite(s) · all passed" && !apView.HasFailures
                && apView.Suites[0].TimeText == "1.5s", apView.StatusText);
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // ─── what the model said it skipped ───
        var fromReply = TurnTests.SkippedFromReply("Done.\n\n**Tests**\n- apitest --turntests: 40/0\n- **Skipped:** desktoptest (Services/Desktop unchanged)\n"
            + "ข้ามเทส: voicetest — ไม่ได้แตะโค้ดเสียง\nNot run: none\nThe skipped frames were fine.");
        check(S, "'Skipped:' / 'ข้ามเทส:' lines of the answer", fromReply.SequenceEqual(["desktoptest (Services/Desktop unchanged)", "voicetest — ไม่ได้แตะโค้ดเสียง"]),
            string.Join(" | ", fromReply));
        var fromArg = TurnTests.SkippedFromArg("[\"desktoptest — unchanged\",\"chattest — no chat code\"]");
        var fromText = TurnTests.SkippedFromArg("desktoptest — unchanged; chattest — no chat code");
        check(S, "wrap_up's tests_skipped: array or text", fromArg.Count == 2 && fromText.SequenceEqual(fromArg), string.Join(" | ", fromText));
        check(S, "wrap_up's schema has tests_skipped", OrchestrationToolModule.Schemas().First(s => s.Name == "wrap_up").InputSchema.GetRawText().Contains("tests_skipped"), "");
        string hint = ChatToolBridge.ChatToolsHint(ChatToolBridge.CliToolNames.Plain, splitChats: true);
        check(S, "every CLI is told to name the tests it ran and skipped", hint.Contains("tests_skipped") && hint.Contains("each suite you ran"), "");
        return Task.CompletedTask;
    }
}
