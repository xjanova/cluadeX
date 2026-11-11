using System.IO;
using System.Reflection;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>RR — a turn cut off by the app closing or crashing, after the restart (owner 2026-11-11: "ถ้าเราออกจากโปรแกรม
    /// ในขณะที่ แชทยังทำงาน หรือยังค้างในบล๊อกคำถาม เราต้องมีวิธีจัดการเมื่อกลับมาใหม่อย่างไรให้ดี"). Driven through real turns on
    /// the fake provider: the close (the confirm, the ledger, the off-screen chat saved), then the restart as start-up runs
    /// it (the ledger's pid swapped for a dead one, RecoverOrphanRunsInStore + MarkOrphanRuns), then the chat opened.</summary>
    static async Task TestRestartRecovery()
    {
        Log("\n## RR — after a restart: a turn cut off mid-work or mid-question gets a card; its question can still be answered");
        string ledger = Path.Combine(Settings.DataRoot, "live-runs.json");
        string Ledger() => File.Exists(ledger) ? File.ReadAllText(ledger) : "";
        var persistence = Sp.GetRequiredService<ChatPersistenceService>();
        var liveRuns = Field<LiveRunLedger>(Vm, "_liveRuns");
        var vmType = typeof(ChatViewModel);
        void Closing(bool on) => vmType.GetField("_appClosing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, on);
        void Invoke(string name) => vmType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        // The restart, as start-up does it: the CluadeX that ran the turns is gone (its pid on the ledger is dead).
        async Task Restart()
        {
            File.WriteAllText(ledger, Ledger().Replace(":" + Environment.ProcessId, ":999999").Replace("\"pid\":" + Environment.ProcessId, "\"pid\":999999"));
            liveRuns.IsAlive = pid => pid == Environment.ProcessId;
            Closing(false);
            await FreshChat();
            await Task.Run(() => Invoke("RecoverOrphanRunsInStore"));
            Invoke("MarkOrphanRuns");
            await Render();
        }
        ChatMessage? CardOf() => Vm.Messages.LastOrDefault(m => RunRecovery.Read(m) != null);
        Vm.SetWorkingDirectory(ProjA);
        Vm.AgenticMode = true;
        var dialogWas = CluadeX.Views.Editor.ChoiceDialog.Override;

        try
        {
            // RR1 — closed while an ask_user card waits: the confirm names it, the ledger says so, the card comes back live
            await FreshChat();
            var c1 = Vm.CurrentSession!;
            Fake.ScriptFor["[RR1]"] = new Queue<FakeStep>([FakeStep.Reply("Let me ask.\n[ACTION: ask_user]\nquestion: Which colour?\noptions: red, blue\n[/ACTION]")]);
            await Send("[RR1] pick a colour");
            ChatMessage? q = null;
            bool asked = await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 20000);
            Check("RR1a the run asks (setup)", asked, string.Join(" | ", Vm.Messages.Select(m => m.Role + ":" + m.Content)));

            string? askedTitle = null, askedBody = null;
            CluadeX.Views.Editor.ChoiceDialog.Override = (t, m, b) => { askedTitle = t; askedBody = m; return 1; };
            bool closes = Vm.ConfirmCloseWithRuns();
            Check("RR1b closing with a chat waiting: asked first; 'keep working' keeps the app open", !closes && askedTitle == L("chat.recover.closeTitle"), askedTitle ?? "not asked");
            Check("RR1c …the question names the chat and that it waits for an answer, and says it can be picked up again",
                askedBody != null && askedBody.Contains(c1.Title) && askedBody.Contains(L("chat.recover.closeWaitsQ")) && askedBody.Contains(L("chat.recover.closeAfter")), askedBody ?? "");
            CluadeX.Views.Editor.ChoiceDialog.Override = (_, _, _) => 0;
            Check("RR1d 'close anyway' closes", Vm.ConfirmCloseWithRuns());

            Vm.NoteAppClosing();
            Check("RR1e the ledger keeps the run as cut off by the close, waiting on a question",
                Ledger().Contains(c1.Id) && Ledger().Contains("\"closed\":true") && Ledger().Contains("\"waiting\":\"question\""), Ledger());
            // The app goes down: the run's CLI dies with it. Its end must not strike it off nor close its card.
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            Vm.SaveNow();
            Check("RR1f a run that ends on the way out stays on the ledger, its card stays open in the saved chat",
                Ledger().Contains(c1.Id) && persistence.LoadSession(c1.Id)!.Messages.Any(RunRecovery.IsOpenQuestion), Ledger());

            await Restart();
            Check("RR1g after the restart its row in the sidebar says stopped (never working)", c1.RunStatus == RunBadge.Stopped, c1.RunStatus);
            Check("RR1h …and the ledger is empty again (told once)", !Ledger().Contains(c1.Id), Ledger());
            await Open(c1.Id);
            var card = CardOf();
            q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question);
            Check("RR1i the chat ends on the recovery card: its title, 'closed while it waited for your answer', Ask again / Dismiss",
                card is { ShowsNextStep: true } && card.NextStep!.Title == L("chat.recover.title")
                && card.NextStep.Recap.Contains(L("chat.recover.closed")) && card.NextStep.Recap.Contains(L("chat.recover.waitQuestion"))
                && card.NextStep.Actions.Select(a => a.Label).SequenceEqual(new[] { L("chat.recover.reask"), L("chat.recover.dismiss") }),
                card?.NextStep?.Recap ?? "no card");
            Check("RR1j the question card is answerable again, with its options", q is { QuestionAnswered: false } && q.QuestionOptions.SequenceEqual(new[] { "red", "blue" }),
                $"answered={q?.QuestionAnswered} options={string.Join(",", q?.QuestionOptions ?? new())}");
            await Render();
            var blue = q == null || Container(q) == null ? null : Find<Button>(Container(q)).FirstOrDefault(b => b.Content as string == "blue");
            Check("RR1k …its buttons are on screen", blue is { IsVisible: true });
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Reply("Blue it is — carrying on."));
            int calls = Fake.Calls.Count;
            if (blue != null) Click(blue); else q?.AnswerQuestion("blue");
            bool carried = await WaitIdle(20000);
            var sent = Fake.Calls.Skip(calls).FirstOrDefault();
            Check("RR1l answering it carries on: the model gets the question and the answer as the owner's message",
                carried && sent != null && sent.Input.Contains(L("chat.recover.msgAnswer")) && sent.Input.Contains("Which colour?") && sent.Input.Contains("→ blue")
                && Vm.Messages.Any(m => m.Content.Contains("carrying on")), sent?.Input ?? "no call");
            Check("RR1m …the card says it was answered, its buttons gone", card?.ToolSummary == L("chat.recover.answered") && card?.NextStep?.IsAnswered == true, card?.ToolSummary ?? "");
            Check("RR1n …the question keeps its answer", q?.Content.Contains("→ blue") == true, q?.Content ?? "");

            // RR2 — a crash mid-work (no close): the card says it ended on its own, Continue carries on
            await FreshChat();
            var c2 = Vm.CurrentSession!;
            Fake.ScriptFor["[RR2]"] = new Queue<FakeStep>([FakeStep.Hang()]);
            await Send("[RR2] long job");
            await WaitUntil(() => c2.RunStatus == RunBadge.Working);
            Closing(true);   // the process dies: nothing sees the run end (no NoteAppClosing — a crash writes nothing)
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            Vm.SaveNow();
            Check("RR2a a crashed run stays on the ledger, not marked closed", Ledger().Contains(c2.Id) && !Ledger().Contains("closed\":true"), Ledger());
            await Restart();
            await Open(c2.Id);
            var card2 = CardOf();
            Check("RR2b the card: 'ended unexpectedly', 'stopped part-way', Continue / Dismiss",
                card2?.NextStep is { } v2 && v2.Recap.Contains(L("chat.recover.crash")) && v2.Recap.Contains(L("chat.recover.working"))
                && v2.Actions[0].Label == L("chat.recover.continue"), card2?.NextStep?.Recap ?? "no card");
            Check("RR2c no spinner comes back: nothing streams, nothing runs", !Vm.IsGenerating && !Vm.Messages.Any(m => m.IsStreaming) && c2.RunStatus == RunBadge.Stopped,
                $"gen={Vm.IsGenerating} status={c2.RunStatus}");
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Reply("Picking up where I stopped."));
            calls = Fake.Calls.Count;
            card2?.NextStep?.Actions[0].Command.Execute(null);
            await WaitIdle(20000);
            sent = Fake.Calls.Skip(calls).FirstOrDefault();
            Check("RR2d Continue sends the carry-on message (the whole chat is the history) and the turn runs",
                sent != null && sent.Input.Contains(L("chat.recover.msgContinue")) && sent.History.Any(h => h.Content.Contains("[RR2] long job"))
                && Vm.Messages.Any(m => m.Content.Contains("Picking up")),
                $"history={string.Join(" | ", sent?.History.Select(h => h.Role + ":" + h.Content[..Math.Min(40, h.Content.Length)]) ?? [])} rows={string.Join(" | ", Vm.Messages.Select(m => m.Role + ":" + m.Content[..Math.Min(30, m.Content.Length)]))}");
            Check("RR2e …the box shows what was picked", card2?.NextStep?.IsAnswered == true && card2.ToolSummary == L("chat.recover.continue"), card2?.ToolSummary ?? "");
            Check("RR2f …and the card itself never went to the model", sent == null || !sent.History.Any(h => h.Content.Contains(L("chat.recover.crash"))));

            // RR3 — Dismiss on a card whose question waits: the question expires, nothing is sent; reopened, it stays closed
            await FreshChat();
            var c3 = Vm.CurrentSession!;
            Fake.ScriptFor["[RR3]"] = new Queue<FakeStep>([FakeStep.Reply("[ACTION: ask_user]\nquestion: Ship it?\noptions: yes, no\n[/ACTION]")]);
            await Send("[RR3] ask me");
            await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.Question && !m.QuestionAnswered), 20000);
            Vm.NoteAppClosing();
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            Vm.SaveNow();
            await Restart();
            await Open(c3.Id);
            var card3 = CardOf();
            var q3 = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question);
            calls = Fake.Calls.Count;
            card3?.NextStep?.Actions[1].Command.Execute(null);
            await Render();
            Check("RR3a Dismiss: the waiting question says it expired, no turn starts",
                q3 is { QuestionAnswered: true } && q3.Content.Contains(L("chat.recover.qExpired")) && !Vm.IsGenerating && Fake.Calls.Count == calls, q3?.Content ?? "");
            await FreshChat();
            await Open(c3.Id);
            var q3b = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question);
            var card3b = CardOf();
            Check("RR3b reopened later: the question is not offered again and the card shows Dismiss was picked",
                q3b is { QuestionAnswered: true } && card3b?.NextStep?.IsAnswered == true, $"{q3b?.QuestionAnswered} {card3b?.ToolSummary}");

            // RR4 — the owner writes something else instead: the card says so, the old question expires (not left live)
            await FreshChat();
            var c4 = Vm.CurrentSession!;
            Fake.ScriptFor["[RR4]"] = new Queue<FakeStep>([FakeStep.Reply("[ACTION: ask_user]\nquestion: Tabs or spaces?\n[/ACTION]")]);
            await Send("[RR4] ask");
            await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.Question && !m.QuestionAnswered), 20000);
            Vm.NoteAppClosing();
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            Vm.SaveNow();
            await Restart();
            await Open(c4.Id);
            var card4 = CardOf();
            var q4 = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question);
            Check("RR4a a free question (no options) comes back answerable too", q4 is { QuestionAnswered: false }, q4?.Content ?? "");
            Fake.ScriptFor["[RR4b]"] = new Queue<FakeStep>([FakeStep.Reply("ok, something else")]);
            await Send("[RR4b] never mind, do this instead");
            await WaitIdle();
            Check("RR4b a new message takes the card's place: it says so, the old question expires",
                card4?.ToolSummary == L("chat.recover.superseded") && q4 is { QuestionAnswered: true } && q4.Content.Contains(L("chat.recover.qExpired")),
                $"{card4?.ToolSummary} · {q4?.Content}");

            // RR5 — a split chat running off screen when the app closes: its rows are saved, its parent is told after the restart
            await FreshChat();
            var parent = Vm.CurrentSession!;
            Fake.ScriptFor["[RR5p]"] = new Queue<FakeStep>([FakeStep.Reply("I split the work.")]);
            await Send("[RR5p] plan");
            await WaitIdle();
            await FreshChat();
            var child = Vm.CurrentSession!;
            ChatLinkStore.Current!.Link(child.Id, parent.Id, "the child task");
            var gate = new SemaphoreSlim(0);
            Fake.ScriptFor["[RR5c]"] = new Queue<FakeStep>([FakeStep.Gated(["RR5 partial work"], gate, [" — RR5 done."])]);
            await Send("[RR5c] do the child task");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("RR5 partial work")));
            await FreshChat();   // the split works on off screen
            Vm.NoteAppClosing();
            var stored = persistence.LoadSession(child.Id);
            Check("RR5a closing saves a chat that runs off screen as it stands (it only reached the disk at its turn's end)",
                stored != null && stored.Messages.Any(m => m.Content.Contains("RR5 partial work")), string.Join(" | ", stored?.Messages.Select(m => m.Content) ?? []));
            gate.Release();
            await WaitUntil(() => child.RunStatus != RunBadge.Working, 15000);
            await Restart();
            var told = persistence.LoadSession(parent.Id)?.Messages.LastOrDefault();
            Check("RR5b after the restart the parent is told its split was cut off before it reported back",
                told is { IsNotice: true } && told.Content.Contains(child.Title), told?.Content ?? "");
            var history = ChatViewModel.BuildModelHistory(persistence.LoadSession(parent.Id)!.Messages);
            Check("RR5c …and the parent's model hears it on its next turn", history.Any(h => h.Content.StartsWith(ChatViewModel.NoticePrefix) && h.Content.Contains(child.Title)));
            await Open(child.Id);
            Check("RR5d the split itself ends on its recovery card", CardOf() != null);

            // RR6 — no runs: closing asks nothing
            await FreshChat();
            bool askedAgain = false;
            CluadeX.Views.Editor.ChoiceDialog.Override = (_, _, _) => { askedAgain = true; return 1; };
            var runsNow = (System.Collections.IEnumerable)vmType.GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            int running = runsNow.Cast<object>().Count();
            string names = string.Join(", ", runsNow.Cast<object>().Select(r => (r.GetType().GetProperty("Session")?.GetValue(r) as ChatSession)?.Title
                + "/" + (r.GetType().GetProperty("Session")?.GetValue(r) as ChatSession)?.RunStatus));
            Check("RR6 closing with nothing running asks nothing", running == 0 && Vm.ConfirmCloseWithRuns() && !askedAgain, $"runs: {names}");
        }
        finally
        {
            CluadeX.Views.Editor.ChoiceDialog.Override = dialogWas;
            Closing(false);
            liveRuns.IsAlive = pid => pid == Environment.ProcessId;
            Fake.Script.Clear();
        }
    }
}
