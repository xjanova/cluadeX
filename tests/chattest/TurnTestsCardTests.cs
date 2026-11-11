using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>TT — the test card a turn ends with (owner 2026-11-11: the table Claude Code sessions end with), on the
    /// real chat page: filled from the turn's own tool rows, each failure read, the skipped suites from wrap_up and from
    /// the answer, drawn in place of a bubble, and a turn that ran no tests has none. The pure parts are in apitest
    /// (--turntests).</summary>
    static async Task TestTurnTests()
    {
        Log("\n## TT — the test card at the end of a turn");
        string root = Path.Combine(Path.GetTempPath(), "chattest-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "AuthService.cs"), "public class AuthService { }");
        try
        {
            await FreshChat();
            Vm.OpenProjectQuietly(root);
            await Render();
            await TurnTestsCard(root);
            await TurnTestsShots();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static async Task TurnTestsCard(string root)
    {
        var fire = typeof(ChatViewModel).GetMethod("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var starting = typeof(ChatViewModel).GetMethod("OnToolStarting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task Tool(ToolResult r)
        {
            starting.Invoke(Vm, [r.ToolName, "⏵ " + r.ToolName]);
            await Task.Delay(30);
            fire.Invoke(Vm, [r]);
        }
        ToolResult Cmd(string command, bool ok, string output) => new()
        {
            ToolName = "run_command", Type = ToolType.RunCommand, Success = ok, Output = output, Error = ok ? "" : "Exit code: 1",
            Arguments = new() { ["command"] = command },
        };
        string auth = Path.Combine(root, "src", "AuthService.cs");

        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor["[TT1]"] = new Queue<FakeStep>([FakeStep.Gated(["Fixing the login check"], gate,
            [" — done.\n\nTests: apitest 41/0, chattest 33/0.\nSkipped: voicetest — no voice code touched"])]);
        await Send("[TT1] fix the empty password check");
        bool running = await WaitUntil(() => Vm.IsGenerating, 10000);
        // apitest fails, the code is fixed, it passes → FIXED; chattest fails once and passes on a rerun → FLAKY;
        // the offline suite keeps one failure that names nothing this turn changed → UNRELATED
        await Tool(Cmd("dotnet run --project tests/apitest -c Debug -- --auth", false, "  FAIL AuthService rejects empty   x\n\n40 passed, 1 failed"));
        await Tool(new ToolResult
        {
            ToolName = "edit_file", Type = ToolType.EditFile, Success = true, Output = "ok", FilePath = auth, Arguments = new() { ["path"] = auth },
            Diff = new EditDiffResult { Added = 2, Removed = 1, Lines = [new EditDiffLine { Kind = EditDiffKind.Add, Text = "x" }] },
        });
        await Tool(Cmd("dotnet run --project tests/apitest -c Debug -- --auth", true, "\n41 passed, 0 failed"));
        await Tool(Cmd("CHATTEST_ONLY=RR dotnet run --project tests/chattest -c Debug", false, "FAIL  RR2d reopens   [timing]\n## Result (CHATTEST_ONLY=RR): 32 pass / 1 FAIL"));
        await Tool(Cmd("CHATTEST_ONLY=RR dotnet run --project tests/chattest -c Debug", true, "## Result (CHATTEST_ONLY=RR): 33 pass / 0 FAIL"));
        await Tool(Cmd("dotnet run --project tests/apitest -c Debug -- --offline", false, "  FAIL real ~/.cluadex untouched   x\n\n2213 passed, 1 failed"));
        await Tool(Cmd("git status", true, "On branch main"));
        // wrap_up's tests_skipped, as the tool raises it for this chat
        var skipField = typeof(OrchestrationToolModule).GetField("TestsSkippedNoted", BindingFlags.NonPublic | BindingFlags.Static);
        (skipField?.GetValue(null) as Action<string?, IReadOnlyList<string>>)?.Invoke(Vm.CurrentSession!.Id, ["desktoptest — Services/Desktop unchanged"]);
        await Render();
        gate.Release();
        bool idle = await WaitIdle();
        await Render();

        var card = Vm.Messages.LastOrDefault(m => m.IsTurnTests);
        var view = card?.TurnTests;
        int at = card == null ? -1 : Vm.Messages.IndexOf(card);
        int changesAt = Vm.Messages.ToList().FindLastIndex(m => m.IsTurnChanges);
        Check("TT1 a turn that ran tests ends with ONE test card, after the change card",
            running && idle && Vm.Messages.Count(m => m.IsTurnTests) == 1 && view != null && changesAt >= 0 && at > changesAt,
            $"running={running} idle={idle} cards={Vm.Messages.Count(m => m.IsTurnTests)} at={at} changes={changesAt}");
        Check("TT1 one line per suite, its last run; git status is no suite",
            view != null && view.Suites.Count == 3
            && view.Suites[0].Suite == "apitest --auth" && view.Suites[0].ResultText == "41 passed" && view.Suites[0].RunsText == "×2" && view.Suites[0].State == "pass"
            && view.Suites[1].Suite == "chattest (RR)" && view.Suites[1].State == "pass"
            && view.Suites[2].Suite == "apitest --offline" && view.Suites[2].ResultText == "2213 passed · 1 failed" && view.Suites[2].State == "fail"
            && view.Suites.All(s => s.TimeText.Length > 0),
            string.Join(" | ", view?.Suites.Select(s => $"{s.Suite} {s.ResultText} {s.TimeText} {s.RunsText} {s.State}") ?? []));
        string KindOf(string test) => view?.Failures.FirstOrDefault(f => f.Test == test)?.Kind ?? "-";
        Check("TT1 each failure read: fixed after the edit, flaky on rerun, unrelated to the changed file",
            KindOf("AuthService rejects empty") == "fixed" && KindOf("RR2d reopens") == "flaky" && KindOf("real ~/.cluadex untouched") == "unrelated"
            && view!.Failures[0].Kind == "unrelated",
            string.Join(" | ", view?.Failures.Select(f => $"{f.Kind} {f.Test}") ?? []));
        Check("TT1 the header says one suite failed", view is { State: "fail" } && view.StatusText == string.Format(L("tests.status.someFailed"), 1, 3), view?.StatusText ?? "");
        Check("TT1 skipped: from wrap_up and from the answer",
            view != null && view.Skipped.SequenceEqual(["desktoptest — Services/Desktop unchanged", "voicetest — no voice code touched"]),
            string.Join(" | ", view?.Skipped ?? []));
        Check("TT1 the row's words name the suites (a reloaded chat, a copy)", card != null && card.Content.StartsWith("🧪") && card.Content.Contains("apitest --offline"),
            card?.Content ?? "");

        ChatList.ScrollIntoView(card);
        await Render();
        var row = card == null ? null : Container(card);
        var cc = row == null ? null : ByAutomationId(row, "CxChat.TurnTests");
        var texts = cc == null ? new List<string>() : Find<TextBlock>(cc).Where(t => t.IsVisible).Select(TextOf).ToList();
        Check("TT1 on screen: title, every suite, the chips and the skips are drawn, the bubble is not",
            cc is { IsVisible: true } && texts.Contains(L("tests.title")) && texts.Contains("apitest --offline") && texts.Contains(L("tests.kind.unrelated"))
            && texts.Contains(L("tests.kind.flaky")) && texts.Contains(L("tests.kind.fixed")) && texts.Contains(L("tests.skippedTitle"))
            && !Find<FrameworkElement>(row!).Any(e => e is Border b && b.Name == "Bubble" && b.IsVisible),
            $"cc={cc?.IsVisible} texts={string.Join(",", texts.Take(14))}");
        // the result column lines up: every suite's result starts at the same x
        var results = cc == null ? new List<TextBlock>() : Find<TextBlock>(cc).Where(t => t.Name == "Result" && t.IsVisible).ToList();
        var xs = results.Select(t => Math.Round(t.TranslatePoint(new Point(0, 0), cc!).X)).Distinct().ToList();
        Check("TT1 the table's result column lines up", results.Count == 3 && xs.Count == 1, string.Join(",", xs));

        // an ordinary row pays nothing for it
        var reply = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        var replyRow = reply == null ? null : Container(reply);
        var replyCc = replyRow == null ? null : ByAutomationId(replyRow, "CxChat.TurnTests");
        Check("TT1 a reply row's test-card slot stays empty",
            replyCc is ContentControl { Visibility: Visibility.Collapsed } c0 && VisualTreeHelper.GetChildrenCount(c0) == 0,
            replyCc == null ? "slot not found" : $"{replyCc.Visibility} children={VisualTreeHelper.GetChildrenCount(replyCc)}");

        // a turn that ran no tests and changed nothing has no card — even when its answer happens to say "Skipped:"
        Fake.ScriptFor["[TT2]"] = new Queue<FakeStep>([FakeStep.Reply("Skipped: the intro, it was slow.")]);
        int cards = Vm.Messages.Count(m => m.IsTurnTests);
        await Send("[TT2] summarise the video");
        await WaitIdle();
        Check("TT2 a turn that ran no tests adds no card", Vm.Messages.Count(m => m.IsTurnTests) == cards, $"{Vm.Messages.Count(m => m.IsTurnTests) - cards} new");

        // a stored chat draws the card again from its row
        var copy = new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.TurnTestsTag, ToolOutput = card?.ToolOutput, Content = card?.Content ?? "" };
        Check("TT3 a reloaded row draws the same card", copy.TurnTests is { } again && again.Suites.Count == 3 && again.Failures.Count == view?.Failures.Count,
            $"{copy.TurnTests?.Suites.Count}");
    }

    /// <summary>The card in Thai, in each theme (dark and light), as pictures for the owner.</summary>
    static async Task TurnTestsShots()
    {
        var themes = Sp.GetRequiredService<CluadeX.Services.ThemeService>();
        string themeBefore = themes.CurrentThemeId;
        try
        {
            foreach (var lang in new[] { "th", "en" })
            {
                LocSvc.SetLanguage(lang);
                await FreshChat();
                var t = new TurnTests();
                ToolResult Cmd(string command, bool ok, string output) => new()
                {
                    ToolName = "run_command", Type = ToolType.RunCommand, Success = ok, Output = output, Arguments = new() { ["command"] = command },
                };
                t.Note(Cmd("dotnet run --project tests/apitest -c Debug -- --turntests", true, "\n58 passed, 0 failed"), 9.4);
                t.Note(Cmd("CHATTEST_ONLY=RR dotnet run --project tests/chattest -c Debug", false, "FAIL  RR2d reopens the question   [timing]\n## Result: 32 pass / 1 FAIL"), 61);
                t.Note(Cmd("CHATTEST_ONLY=RR dotnet run --project tests/chattest -c Debug", true, "## Result: 33 pass / 0 FAIL"), 58);
                t.Note(Cmd("dotnet run --project tests/apitest -c Debug -- --offline", false, "  FAIL real ~/.cluadex untouched   x\n\n2213 passed, 1 failed"), 132);
                t.Note(Cmd("pwsh Scripts/try-branch.ps1 feat/turn-test-summary -Smoke", true, "\n12 passed, 0 failed"), 214);
                t.NoteSkipped(lang == "th"
                    ? ["desktoptest — ไม่ได้แก้ Services/Desktop", "voicetest — ไม่ได้แตะโค้ดเสียง"]
                    : ["desktoptest — Services/Desktop unchanged", "voicetest — no voice code touched"]);
                var sum = t.Summarize([@"C:\p\CluadeX\Services\Agents\TurnTests.cs", @"C:\p\CluadeX\Views\ChatView.xaml"]);
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = lang == "th" ? "เพิ่มสรุปผลเทสตอนจบเทิร์น" : "Add the end-of-turn test summary" });
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = lang == "th"
                    ? "เสร็จแล้วค่ะ เพิ่มการ์ดผลการทดสอบใต้คำตอบ รันเฉพาะชุดที่เกี่ยวกับงานนี้"
                    : "Done — the test card now ends each turn; only the suites this change touches ran." });
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.TurnTestsTag, ToolOutput = sum.ToJson(),
                    Content = TurnTestsView.PlainLine(sum, L) });
                foreach (var id in new[] { CluadeX.Services.ThemeIds.Midnight, CluadeX.Services.ThemeIds.Lavender, CluadeX.Services.ThemeIds.Classic })
                {
                    themes.Apply(id, persist: false);
                    await Task.Delay(150); await Render();
                    ChatList.ScrollIntoView(Vm.Messages[^1]);
                    await Render();
                    SaveShot(Path.Combine(AppContext.BaseDirectory, $"tests-card-{lang}-{id.ToLowerInvariant()}.png"));
                }
            }
            Log("     (pictures: tests-card-{th,en}-{midnight,lavender,classic}.png in " + AppContext.BaseDirectory + ")");
        }
        finally
        {
            themes.Apply(themeBefore, persist: false);
            LocSvc.SetLanguage("en");
        }
    }
}
