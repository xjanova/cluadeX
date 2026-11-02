using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Background;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;
using CluadeX.Views;

namespace ChatTest;

/// <summary>
/// OR — the orchestration views on the real chat page (docs/agent-orchestration.md): the workflow card (renders, updates
/// its rows in place, costs nothing while hidden, Stop / Resume, a double click resumes once), split-chat chips (the
/// sidebar's own state, a click opens the chat), notices (background commands, a split turn — and the model reads
/// them), the next-step box (under the closing message, a click sends the answer once and keeps the draft), Ultra.
/// Scripted sub-agents and the fake provider only: no model, no network.
/// </summary>
internal static partial class Program
{
    static object CallVm(string method, params object[] args) =>
        typeof(ChatViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, args);

    static async Task TestOrchestrationViews()
    {
        Log("\n## OR — orchestration views: workflow card, split chips, notices, next-step box, Ultra");
        await TestWorkflowCard();
        await TestSplitChips();
        await TestNotices();
        await TestNextStepBox();
        await TestUltraSwitch();
    }

    static async Task TestWorkflowCard()
    {
        await FreshChat();
        var svc = WorkflowService.Current;
        Check("OR1 the chat page connected the workflow service at startup", svc != null && svc.Runner != null);
        if (svc == null) return;
        var oldRunner = svc.Runner;
        var oldEnter = svc.EnterSlot;
        var oldLeave = svc.LeaveSlot;
        var gates = new Dictionary<string, SemaphoreSlim> { ["a1"] = new(0), ["a2"] = new(0), ["a3"] = new(0) };
        int calls = 0;
        svc.EnterSlot = _ => Task.CompletedTask;
        svc.LeaveSlot = () => { };
        svc.Runner = async (c, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (gates.TryGetValue(c.Label, out var g)) await g.WaitAsync(ct);
            return new WorkflowAgentResult("found by " + c.Label, 0.02m);
        };
        try
        {
            string json = """{"name":"Review the client","concurrency":3,"phases":[{"title":"scan","agents":[{"label":"a1","prompt":"p1"},{"label":"a2","prompt":"p2"},{"label":"a3","prompt":"p3"}]}]}""";
            var plan = WorkflowPlan.Parse(json, out _)!;
            string chat = Vm.CurrentSession!.Id;
            var run = Task.Run(() => svc.RunAsync(plan, json, chat, CancellationToken.None));
            bool shown = await WaitUntil(() => Vm.Messages.Any(m => m.IsWorkflow));
            await Render();
            var row = Vm.Messages.FirstOrDefault(m => m.IsWorkflow);
            var card = row?.Workflow;
            var view = row == null || Container(row) == null ? null : Find<WorkflowCardView>(Container(row)).FirstOrDefault();
            Check("OR1 a workflow started in the chat shows its card in the chat (not a bubble)",
                shown && card != null && view is { IsVisible: true } && row.IsOrchestrationCard, $"shown={shown} card={card != null} view={view?.IsVisible}");
            await WaitUntil(() => { card?.Flush(); return card?.Rows.Count == 3; });
            await Render();
            var dots = view == null ? null : (ItemsControl)view.FindName("Dots");
            Check("OR1 the card: name, agent count, a dot per agent, running",
                card?.Name == "Review the client" && card.AgentsText.Contains("3") && dots?.Items.Count == 3 && card.IsRunning
                && ((Button)view.FindName("StopButton")).IsVisible, $"{card?.Name} · {card?.AgentsText} · dots={dots?.Items.Count}");

            card!.IsExpanded = true;
            await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-workflow-card.png"));
            card.IsExpanded = false;
            var firstRow = card.Rows[0];
            int replaced = 0;
            card.Rows.CollectionChanged += (_, e) => { if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) replaced++; };
            gates["a1"].Release();
            await WaitUntil(() => { card.Flush(); return firstRow.State == "done"; });
            await Render();
            Check("OR2 an agent finishing updates its row in place (same objects, nothing rebuilt: no flash)",
                ReferenceEquals(card.Rows[0], firstRow) && firstRow.State == "done" && replaced == 0 && card.Rows.Count == 3, $"state={firstRow.State} replaced={replaced}");

            // Hidden (scrolled away, another chat on screen): no timer, no redraw.
            int elapsedTicks = 0;
            card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkflowCard.Elapsed)) elapsedTicks++; };
            card.SetVisible(false);
            elapsedTicks = 0;
            await Task.Delay(1300);
            int hiddenTicks = elapsedTicks;
            card.SetVisible(true);
            await Task.Delay(1300);
            Check("OR3 a hidden card costs nothing (its clock stops) and starts again when shown",
                hiddenTicks == 0 && elapsedTicks > 0, $"hidden={hiddenTicks} shown={elapsedTicks}");

            card.StopCommand.Execute(null);
            var st = await run;
            await WaitUntil(() => { card.Flush(); return card.StatusKey == "stopped"; });
            await Render();
            Check("OR4 Stop on the card stops every agent; the card says stopped and offers Resume",
                st.Status == "stopped" && card.CanResume && !card.IsRunning && ((Button)view.FindName("ResumeButton")).IsVisible
                && card.Rows.Count(r => r.State == "stopped") == 2, card.StatusText);

            int before = calls;
            gates.Clear();   // the resumed agents answer at once
            card.ResumeCommand.Execute(null);
            card.ResumeCommand.Execute(null);   // a double click
            bool ended = await WaitUntil(() => { card.Flush(); return card.StatusKey == "done"; });
            await Render();
            Check("OR5 Resume (double-clicked) runs once and reuses the finished agent from the cache",
                ended && calls - before == 2 && card.Rows.Count(r => r.State == "cached") == 1 && card.Rows.Count(r => r.State == "done") == 2,
                $"calls={calls - before} {string.Join(",", card.Rows.Select(r => r.State))}");
            bool told = await WaitUntil(() => Vm.Messages.Any(m => m.IsNotice && m.Content.Contains("Review the client")));
            Check("OR5 a workflow resumed from its card tells the chat when it ends (a notice the model reads too)", told);

            Vm.SaveNow();
            var stored = Persist.LoadSession(chat)?.Messages.FirstOrDefault(m => m.ToolName == ChatMessage.WorkflowTag);
            Check("OR6 the card row is saved with the chat (its id) with a text summary for where it cannot be drawn",
                stored?.ToolOutput == card.Id && stored.Content.Contains("done"), stored?.Content);
            var orphan = new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.WorkflowTag, ToolOutput = "wf-gone00000", Content = "🧩 old" };
            Check("OR6 a card whose state is gone shows its text instead of an empty card", orphan.Workflow == null && !orphan.IsOrchestrationCard);
        }
        finally
        {
            svc.Runner = oldRunner;
            svc.EnterSlot = oldEnter;
            svc.LeaveSlot = oldLeave;
        }
    }

    static async Task TestSplitChips()
    {
        // Each chat gets a row first: leaving an untouched chat drops it (LoadSession).
        await FreshChat();
        var parent = Vm.CurrentSession!;
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "split the work" });
        await FreshChat();
        var child = Vm.CurrentSession!;
        child.Title = "Fix the parser";
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "fix the parser" });
        Vm.GoToChatCommand.Execute(parent.Id);
        await WaitUntil(() => Vm.CurrentSession?.Id == parent.Id);
        var row = new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.SplitsTag, ToolOutput = child.Id, Content = "💬 Fix the parser" };
        Vm.Messages.Add(row);
        await Render();
        var item = Container(row);
        var chip = item == null ? null : Find<Button>(item).FirstOrDefault(b => b.CommandParameter as string == child.Id);
        string Words() => chip == null ? "" : string.Join(" ", Find<TextBlock>(chip).Where(t => t.IsVisible).Select(t => t.Text));
        Check("OR7 the command's split chats show as chips in its message, with their titles",
            chip is { IsVisible: true } && Words().Contains("Fix the parser") && ReferenceEquals(row.SplitChips?[0], child), Words());
        child.RunStatus = "working";
        await Render();
        var chipAfter = Find<Button>(item).FirstOrDefault(b => b.CommandParameter as string == child.Id);
        Check("OR7 a chip follows the sidebar's own state live (working), without being rebuilt",
            ReferenceEquals(chip, chipAfter) && Words().Contains(L("orch.splits.working")), Words());
        child.RunStatus = "";
        chip?.Command.Execute(chip.CommandParameter);
        bool opened = await WaitUntil(() => Vm.CurrentSession?.Id == child.Id);
        Check("OR7 a click opens that chat", opened);
        Vm.GoToChatCommand.Execute(parent.Id);
        await WaitUntil(() => Vm.CurrentSession?.Id == parent.Id);
        await Render();
    }

    static async Task TestNotices()
    {
        await FreshChat();
        var child = Vm.CurrentSession!;
        child.Title = "Run the tests";
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "run the tests" });
        await FreshChat();
        var parent = Vm.CurrentSession!;
        string chat = parent.Id;
        // Two background commands end within the 2-second window: one notice for both.
        BgSnapshot Snap(int id, string name, string status, int? exit) => new(id, name, BgKind.Shell, "npm test", "C:\\p", status, null, null, null,
            exit, DateTime.Now.AddSeconds(-12), null, DateTime.Now, "C:\\p\\log.txt", "done.", "agent", false);
        CallVm("OnBackgroundEnded", Snap(1, "tests", "completed", 0), chat);
        CallVm("OnBackgroundEnded", Snap(2, "lint", "failed", 1), chat);
        CallVm("OnBackgroundEnded", Snap(3, "server", "stopped", null), chat);
        Vm.FlushBackgroundNotices();
        await Render();
        var notices = Vm.Messages.Where(m => m.IsNotice).ToList();
        Check("OR8 background commands that end close together give ONE notice; an explicit stop gives none",
            notices.Count == 1 && notices[0].Content.Contains(LocF("orch.bg.many", 2)) && notices[0].Content.Contains("lint") && !notices[0].Content.Contains("server"),
            string.Join(" | ", notices.Select(n => n.Content)));

        // A split chat ends a turn: the parent hears the gist.
        ChatLinkStore.Current!.Link(child.Id, chat, "run the tests");
        var said = new List<ChatMessage> { new() { Role = MessageRole.Assistant, Content = "All 41 tests pass.\nNothing else changed." } };
        CallVm("TellParentSplitFinished", child, said);
        await Render();
        var note = Vm.Messages.LastOrDefault(m => m.IsNotice);
        Check("OR9 a split chat ending a turn sends its parent a short note with the gist of its answer",
            note != null && note.Content.Contains("Run the tests") && note.Content.Contains("All 41 tests pass. Nothing else changed."), note?.Content);

        var history = ChatViewModel.BuildModelHistory(Vm.Messages);
        Check("OR10 notices reach the model on its next turn ([CluadeX notice] …); the cards do not",
            history.Count(h => h.Role == MessageRole.User && h.Content.StartsWith(ChatViewModel.NoticePrefix)) == 2
            && !history.Any(h => h.Content.Contains("Fix the parser") && h.Role != MessageRole.User), string.Join(" | ", history.Select(h => h.Content)));
        Vm.SaveNow();
        var stored = Persist.LoadSession(chat)?.Messages.Where(m => m.ToolName == ChatMessage.NoticeTag).ToList();
        Check("OR10 notices are saved with the chat and read back as notices", stored?.Count == 2 && stored.All(m => m.IsNotice));
    }

    static async Task TestNextStepBox()
    {
        await FreshChat();
        string chat = Vm.CurrentSession!.Id;
        // What wrap_up does in the run's flow (OrchestrationToolModule raises it for its own chat).
        var raise = typeof(OrchestrationToolModule).GetField("NextStepRequested", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as Action<string, NextStep>;
        raise?.Invoke(chat, NextStep.FromArgs("Fixed the timeout; the build passes. Not run against the live API.", "Run the live test now?", "[\"Yes, run it\",\"Not now\"]", out _));
        Fake.ScriptFor["[NS1]"] = new Queue<FakeStep>([FakeStep.Reply("Done: ", "the timeout is fixed.")]);
        await Send("[NS1] fix the timeout");
        await WaitIdle();
        var last = Vm.Messages.LastOrDefault();
        Check("OR11 wrap_up's box shows under the closing message when the turn ends", last?.IsNextStep == true && last.ShowsNextStep
            && Vm.Messages.Reverse().Skip(1).FirstOrDefault(m => m.Role == MessageRole.Assistant)?.Content.Contains("timeout is fixed") == true,
            string.Join(" | ", Vm.Messages.TakeLast(3).Select(m => m.Role + ":" + m.Content)));
        var box = last?.NextStep;
        var item = last == null ? null : Container(last);
        var buttons = item == null ? new List<Button>() : Find<Button>(item).Where(b => b.IsVisible && b.DataContext is NextStepAction).ToList();
        Check("OR11 recap, the question, and two buttons (the pick first)",
            box?.Recap.StartsWith("Fixed the timeout") == true && box.Ask == "Run the live test now?" && buttons.Count == 2
            && ((NextStepAction)buttons[0].DataContext).IsPrimary && ((NextStepAction)buttons[0].DataContext).Label == "Yes, run it", $"buttons={buttons.Count}");

        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-next-step.png"));
        Vm.UserInput = "my half-typed draft";
        int before = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Reply("Running it."));
        buttons.FirstOrDefault()?.Command.Execute(null);
        buttons.FirstOrDefault()?.Command.Execute(null);   // a double click
        buttons.LastOrDefault()?.Command.Execute(null);    // and the other button after it
        await Task.Delay(100);
        await WaitIdle();
        Check("OR12 a click sends the label as the owner's message — once",
            Fake.Calls.Count - before == 1 && Fake.Calls.Last().Input.Contains("Yes, run it")
            && Vm.Messages.Count(m => m.Role == MessageRole.User && m.Content == "Yes, run it") == 1 && !Vm.Messages.Any(m => m.Content == "Not now"),
            $"calls={Fake.Calls.Count - before} last='{Fake.Calls.LastOrDefault()?.Input}'");
        await Render();
        Check("OR12 the box then says what was picked and drops its buttons; the owner's draft stays in the box",
            box!.IsAnswered && box.PickedText.Contains("Yes, run it") && !box.HasActions && Vm.UserInput == "my half-typed draft", $"input='{Vm.UserInput}'");
        Vm.UserInput = "";
        Vm.SaveNow();
        var stored = Persist.LoadSession(chat)?.Messages.FirstOrDefault(m => m.ToolName == ChatMessage.NextStepTag);
        var reread = stored == null ? null : new ChatMessage { Role = MessageRole.System, ToolName = stored.ToolName, ToolOutput = stored.ToolOutput, ToolSummary = stored.ToolSummary };
        Check("OR13 a reopened chat shows the box answered (no live buttons left to send twice)",
            reread?.NextStep is { IsAnswered: true, HasActions: false } && reread.NextStep.Picked == "Yes, run it");
        Check("OR13 the box is not sent to the model as a turn", !ChatViewModel.BuildModelHistory(Vm.Messages).Any(h => h.Content.Contains("Run the live test now?")));

        // A stopped turn shows no box (the owner moved on).
        raise?.Invoke(chat, NextStep.FromArgs("half done", "", "", out _));
        Fake.ScriptFor["[NS2]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        await Send("[NS2] long job");
        await WaitUntil(() => Vm.IsGenerating);
        Vm.StopGenerationCommand?.Execute(null);
        await WaitIdle();
        Check("OR14 a turn the owner stopped gets no next-step box", !Vm.Messages.SkipWhile(m => m.Content != "[NS2] long job").Any(m => m.IsNextStep));
    }

    static async Task TestUltraSwitch()
    {
        await FreshChat();
        var a = Vm.CurrentSession!;
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "a chat with something in it" });   // an untouched chat is reused / dropped
        var toggle = (CheckBox)View.FindName("UltraToggle");
        Check("OR15 the Ultra switch is beside the input, off by default", toggle is { IsVisible: true } && toggle.IsChecked == false && !Vm.IsUltra);
        Vm.IsUltra = true;
        await Render();
        Check("OR15 on: the chat may run workflows (the gate agrees)", toggle.IsChecked == true && WorkflowGate.Why(a.Id) == "ultra");
        await FreshChat();
        await Render();
        Check("OR15 it is per chat: a new chat starts off", toggle.IsChecked == false && !WorkflowGate.IsUltra(Vm.CurrentSession!.Id));
        Vm.GoToChatCommand.Execute(a.Id);
        await WaitUntil(() => Vm.CurrentSession?.Id == a.Id);
        await Render();
        Check("OR15 and comes back on with its chat", toggle.IsChecked == true);
        Vm.IsUltra = false;

        Fake.ScriptFor["[UL1]"] = new Queue<FakeStep>([FakeStep.Reply("ok")]);
        await Send("[UL1] review everything — use a workflow");
        await WaitIdle();
        Check("OR16 the owner asking for a workflow in their message opens it for that turn", WorkflowGate.Why(a.Id) == "asked");
        await Send("thanks");
        await WaitIdle();
        Check("OR16 their next message closes it again", WorkflowGate.Why(a.Id) == null);
    }
}
