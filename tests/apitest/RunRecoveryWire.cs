// A turn cut off by the app closing or crashing (owner 2026-11-11: "ถ้าเราออกจากโปรแกรมในขณะที่ แชทยังทำงาน หรือยังค้างใน
// บล๊อกคำถาม เราต้องมีวิธีจัดการเมื่อกลับมาใหม่อย่างไรให้ดี"). Pure: the ledger's two formats and what it remembers about a
// close, the recovery card and what it reads back, the question's own data on its row, and the start-up sweep of what the
// last run left (helpers matched by pid AND start time, turn folders only while no other CluadeX runs).
using System.Diagnostics;
using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Background;

namespace ApiTest;

internal static class RunRecoveryWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · after a restart: the run ledger, the recovery card, the leftover sweep");
        string root = Path.Combine(Path.GetTempPath(), "cx-recover-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Ledger(check, root);
            Card(check);
            Sweep(check, root);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return Task.CompletedTask;
    }

    static string L(string key) => "<" + key + ">";

    static void Ledger(Action<string, string, bool, string> check, string root)
    {
        string dir = Path.Combine(root, "ledger");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "live-runs.json");
        var me = new LiveRunLedger(dir);
        me.Began("a");
        me.Began("b");
        check("ledger", "a plain run is written as before ({id: pid}) — older builds still read it",
            File.ReadAllText(file).Contains($"\"a\":{Environment.ProcessId}"), File.ReadAllText(file));
        me.SetWaiting("b", LiveRunLedger.WaitQuestion);
        me.MarkClosing(new Dictionary<string, string?> { ["a"] = null });
        string text = File.ReadAllText(file);
        check("ledger", "closing marks every run of this CluadeX as closed, and keeps what one waits on",
            text.Contains("\"closed\":true") && text.Contains("\"waiting\":\"question\""), text);
        File.WriteAllText(file, text.Replace(Environment.ProcessId.ToString(), "424242"));
        var next = new LiveRunLedger(dir) { IsAlive = pid => pid != 424242 };
        var orphans = next.TakeOrphanRuns().OrderBy(o => o.ChatId).ToList();
        check("ledger", "the restart reads both runs as cut off by the close, one waiting on a question",
            orphans.Count == 2 && orphans.All(o => o.Closed) && orphans[0].Waiting == null && orphans[1].Waiting == LiveRunLedger.WaitQuestion,
            string.Join("; ", orphans));
        check("ledger", "…told once", next.TakeOrphanRuns().Count == 0 && !File.Exists(file), "");

        File.WriteAllText(file, "{\"old\":424242,\"live\":" + Environment.ProcessId + "}");
        var old = new LiveRunLedger(dir) { IsAlive = pid => pid != 424242 };
        var o1 = old.TakeOrphanRuns();
        check("ledger", "an old-format file: its dead run is a crash (not closed), the run of this CluadeX stays",
            o1.Count == 1 && o1[0] is { ChatId: "old", Closed: false, Waiting: null } && File.ReadAllText(file).Contains("\"live\""), string.Join("; ", o1));
        old.SetWaiting("live", LiveRunLedger.WaitPermission);
        old.SetWaiting("ghost", LiveRunLedger.WaitPermission);
        check("ledger", "waiting is written for a run on the ledger, never for one that is not",
            File.ReadAllText(file).Contains("permission") && !File.ReadAllText(file).Contains("ghost"), File.ReadAllText(file));
        old.Ended("live");
        check("ledger", "an ended run leaves an empty ledger: the file goes", !File.Exists(file), "");
    }

    static void Card(Action<string, string, bool, string> check)
    {
        var user = new ChatMessage { Role = MessageRole.User, Content = "build it" };
        var answered = new ChatMessage { Role = MessageRole.Question, Content = "❓ Which db?\n→ SQLite" };
        var oldUser = new ChatMessage { Role = MessageRole.User, Content = "first" };
        var oldOpen = new ChatMessage { Role = MessageRole.Question, Content = "❓ stale" };
        var open = new ChatMessage
        {
            Role = MessageRole.Question, Content = "❓ Which colour?",
            ToolOutput = RunRecovery.QuestionJson(new[] { "red", "blue" }, null),
        };
        var steps = new List<AskStep> { new("Name?", new[] { "a", "b" }), new("Tags?", new[] { "x", "y" }, Array.Empty<string>(), true) };
        var stepped = new ChatMessage { Role = MessageRole.Question, Content = "❓ Name?\n\n❓ Tags?", ToolOutput = RunRecovery.QuestionJson(null, steps) };
        var rows = new List<ChatMessage> { oldUser, oldOpen, user, answered, open, stepped };

        check("questions", "an answered card is not open, one with no answer under it is",
            !RunRecovery.IsOpenQuestion(answered) && RunRecovery.IsOpenQuestion(open), "");
        var opens = RunRecovery.OpenQuestions(rows, rows.Count);
        check("questions", "the open cards are those of the LAST turn only", opens.Count == 2 && opens[0] == open && opens[1] == stepped,
            string.Join(" | ", opens.Select(o => o.Content)));
        var (opts, none) = RunRecovery.ReadQuestion(open);
        check("questions", "a card's options come back from its row", opts.SequenceEqual(new[] { "red", "blue" }) && none.Count == 0, string.Join(",", opts));
        var (_, back) = RunRecovery.ReadQuestion(stepped);
        check("questions", "…and a card's steps, with their options and multi-select", back.Count == 2 && back[0].Question == "Name?"
            && back[1].Multi && back[1].Options.SequenceEqual(new[] { "x", "y" }), string.Join(" | ", back));
        check("questions", "a row from an older build (no data): a free question from its text",
            RunRecovery.ReadQuestion(oldOpen) is ({ Count: 0 }, { Count: 0 }) && RunRecovery.QuestionText(oldOpen) == "stale", RunRecovery.QuestionText(oldOpen));
        check("questions", "the question text drops the ❓ marks; steps are numbered",
            RunRecovery.QuestionText(open) == "Which colour?" && RunRecovery.QuestionText(stepped) == "1. Name?\n2. Tags?", RunRecovery.QuestionText(stepped));

        var info = new RunRecovery.Info(RunRecovery.WhyClosed, LiveRunLedger.WaitQuestion, new[] { "Which colour?" }, DateTime.UtcNow);
        var card = RunRecovery.Card(info, L);
        rows.Add(card);
        var read = RunRecovery.Read(card);
        check("card", "the card is a next-step box marked as CluadeX's own, read back whole",
            card.ToolName == ChatMessage.NextStepTag && read is { } r && r.Info.Why == RunRecovery.WhyClosed && r.Info.Waiting == LiveRunLedger.WaitQuestion
            && r.Info.Questions.SequenceEqual(new[] { "Which colour?" }) && r.Go == L("chat.recover.reask"), card.ToolOutput ?? "");
        var step = CluadeX.Services.Agents.NextStep.FromJson(card.ToolOutput);
        check("card", "…which the box itself can draw: recap, question, Ask again / Dismiss",
            step != null && step.Recap.Contains(L("chat.recover.closed")) && step.Recap.Contains(L("chat.recover.waitQuestion"))
            && step.Actions.SequenceEqual(new[] { L("chat.recover.reask"), L("chat.recover.dismiss") }), step?.Recap ?? "null");
        check("card", "a model's own wrap_up box is not a recovery card",
            RunRecovery.Read(new ChatMessage { ToolName = ChatMessage.NextStepTag, ToolOutput = "{\"recap\":\"done\",\"actions\":[]}" }) == null, "");
        check("card", "open while nothing was picked and no message came after it", RunRecovery.IsOpenCard(rows, rows.Count - 1), "");
        card.ToolSummary = "picked";
        check("card", "…not once picked", !RunRecovery.IsOpenCard(rows, rows.Count - 1), "");
        card.ToolSummary = null;
        rows.Add(new ChatMessage { Role = MessageRole.User, Content = "something else" });
        check("card", "…not once the owner wrote something else", !RunRecovery.IsOpenCard(rows, rows.Count - 2), "");

        var crash = RunRecovery.Read(RunRecovery.Card(new RunRecovery.Info(RunRecovery.WhyCrash, null, Array.Empty<string>(), DateTime.UtcNow), L))!.Value;
        var crashStep = CluadeX.Services.Agents.NextStep.FromJson(RunRecovery.Card(crash.Info, L).ToolOutput)!;
        check("card", "a crash while working: says so, offers Continue", crash.Go == L("chat.recover.continue")
            && crashStep.Recap.Contains(L("chat.recover.crash")) && crashStep.Recap.Contains(L("chat.recover.working")), crashStep.Recap);
        var perm = CluadeX.Services.Agents.NextStep.FromJson(RunRecovery.Card(new RunRecovery.Info(RunRecovery.WhyClosed, LiveRunLedger.WaitPermission, Array.Empty<string>(), DateTime.UtcNow), L).ToolOutput)!;
        check("card", "closed while a permission prompt waited: says it will be asked again", perm.Recap.Contains(L("chat.recover.waitPermission")), perm.Recap);
        check("card", "the card's text never goes to the model (a System row, not a notice)", ChatViewModelHistoryHasNo(card), "");

        check("message", "Continue carries on; after a permission wait it says to ask again",
            RunRecovery.ContinueMessage(crash.Info, L) == L("chat.recover.msgContinue")
            && RunRecovery.ContinueMessage(new RunRecovery.Info(RunRecovery.WhyClosed, LiveRunLedger.WaitPermission, Array.Empty<string>(), DateTime.UtcNow), L) == L("chat.recover.msgPermission"), "");
        check("message", "Ask again names the question that never reached the owner",
            RunRecovery.ContinueMessage(info, L).Contains("• Which colour?"), RunRecovery.ContinueMessage(info, L));
        check("message", "an answer carries the question and the answer (the ask_user never returned)",
            RunRecovery.AnswerMessage("Which colour?", "blue", L) == L("chat.recover.msgAnswer") + "\nWhich colour?\n→ blue", RunRecovery.AnswerMessage("Which colour?", "blue", L));
    }

    static bool ChatViewModelHistoryHasNo(ChatMessage card)
    {
        var history = CluadeX.ViewModels.ChatViewModel.BuildModelHistory(new[] { new ChatMessage { Role = MessageRole.User, Content = "hi" }, card });
        return history.Count == 1;
    }

    static void Sweep(Action<string, string, bool, string> check, string root)
    {
        string local = Path.Combine(root, "local"), temp = Path.Combine(root, "temp");
        string grokHome = Path.Combine(local, "cluadex-grok", "turn1");
        string gemHome = Path.Combine(local, "cluadex-gemini", "turn2");
        string agy = Path.Combine(temp, "cluadex-guard", "agy-123");
        string keepDir = Path.Combine(temp, "cluadex-guard", "not-a-turn");
        string target = Path.Combine(root, "users-own-folder");
        foreach (var d in new[] { grokHome, gemHome, agy, keepDir, target }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
        File.WriteAllText(Path.Combine(grokHome, "auth.json"), "{}");
        bool linked = false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(grokHome, "skills")}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            linked = Directory.Exists(Path.Combine(grokHome, "skills"));
        }
        catch { }
        string tools = Path.Combine(temp, "cluadex-guard", "claude-dev-tools-abc.json");
        string settingsOld = Path.Combine(temp, "cluadex-guard", "claude-dev-settings-OLD.json");
        string settingsNew = Path.Combine(temp, "cluadex-guard", "claude-dev-settings-NEW.json");
        string prompt = Path.Combine(temp, "cluadex-grok", "p.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
        foreach (var f in new[] { tools, settingsOld, settingsNew, prompt }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(settingsOld, DateTime.UtcNow.AddDays(-10));

        var wasOther = LeftoverSweep.OtherCluadexRunning;
        var wasBefore = LeftoverSweep.Before;
        var wasLedger = LeftoverSweep.LedgerPath;
        try
        {
            LeftoverSweep.Before = DateTime.UtcNow.AddSeconds(1);
            LeftoverSweep.LedgerPath = null;
            LeftoverSweep.OtherCluadexRunning = () => true;
            var kept = LeftoverSweep.Run(local, temp);
            check("sweep", "another CluadeX running: its turn folders are left alone", kept.FoldersRemoved == 0 && kept.FilesRemoved == 0 && Directory.Exists(grokHome), $"{kept}");

            LeftoverSweep.OtherCluadexRunning = () => false;
            var res = LeftoverSweep.Run(local, temp);
            check("sweep", "no other CluadeX: Grok / Gemini turn homes and Antigravity turn plugins go",
                !Directory.Exists(grokHome) && !Directory.Exists(gemHome) && !Directory.Exists(agy) && res.FoldersRemoved == 3, $"{res}");
            check("sweep", "a turn home's junction is unlinked, never entered: the folder it pointed at keeps its file",
                !linked || File.Exists(Path.Combine(target, "precious.txt")), $"linked={linked}");
            check("sweep", "only per-turn names: another folder beside them stays", Directory.Exists(keepDir), "");
            check("sweep", "tool tickets and prompt files go; a settings file goes only when a week old",
                !File.Exists(tools) && !File.Exists(prompt) && !File.Exists(settingsOld) && File.Exists(settingsNew), "");

            Directory.CreateDirectory(grokHome);
            LeftoverSweep.Before = DateTime.UtcNow.AddMinutes(-5);
            LeftoverSweep.Run(local, temp);
            check("sweep", "a folder newer than this run's start is this run's own turn: kept", Directory.Exists(grokHome), "");

            // helpers: one recorded by a CluadeX that is gone is ended; the same pid with another start time is not touched
            string ledger = Path.Combine(root, "app-children.json");
            LeftoverSweep.LedgerPath = ledger;
            using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            Thread.Sleep(300);
            long start = child.StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(ledger, "{\"999999\":{\"start\":1,\"children\":[" +
                $"{{\"pid\":{child.Id},\"start\":{start},\"name\":\"cmd\"}}," +
                $"{{\"pid\":{other.Id},\"start\":{start - TimeSpan.TicksPerHour},\"name\":\"cmd\"}}]}}}}");
            var killed = LeftoverSweep.Run(local, temp);
            bool childGone = child.WaitForExit(5000);
            check("sweep", "a helper the last (gone) CluadeX left running is ended", childGone && killed.Killed.Count == 1, string.Join(", ", killed.Killed));
            check("sweep", "a process with that pid but another start time is someone else's: untouched", !other.HasExited, "");
            check("sweep", "the gone CluadeX's entry is struck off", !File.Exists(ledger) || !File.ReadAllText(ledger).Contains("999999"), File.Exists(ledger) ? File.ReadAllText(ledger) : "");
            try { other.Kill(true); } catch { }

            LeftoverSweep.Note(other.HasExited ? Process.GetCurrentProcess() : other);
            check("sweep", "a helper start is written under this CluadeX", File.Exists(ledger) && File.ReadAllText(ledger).Contains($"\"{Environment.ProcessId}\""), File.Exists(ledger) ? File.ReadAllText(ledger) : "");
            var self = LeftoverSweep.Run(local, temp);
            check("sweep", "…and never ended by this CluadeX's own sweep", self.Killed.Count == 0, string.Join(", ", self.Killed));
        }
        finally
        {
            LeftoverSweep.OtherCluadexRunning = wasOther;
            LeftoverSweep.Before = wasBefore;
            LeftoverSweep.LedgerPath = wasLedger;
        }
    }
}
