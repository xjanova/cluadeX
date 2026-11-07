using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.AgentGraph;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// AG11–AG16 — stopping one sub-agent and writing to it from its own window (owner 2026-11-07: "สั่งยกเลิกหรือหยุดเอเจนต์
/// หรือพิมพ์แทรกคำสั่งเข้าไปในเอเจนต์นั้นได้ ในหน้าต่างลอยของเอเจนต์แต่ละตัว"). The control itself (one-way link, idempotent
/// stop, the queue's limits), then the real engine loop (CodeAgentService's held sub-agent loop, a scripted native-tool
/// provider, no network) feeding the real chat page through the real event handler — Stop ends that agent only and the
/// main agent gets an ordinary result, a message is read at its next step, one it never read says so, a finished agent and
/// a CLI's own agent show disabled controls with the reason.
/// </summary>
internal static partial class Program
{
    static async Task TestAgentSteer()
    {
        Log("\n## AG11–AG16 — stop one agent, or write to it, from its window");
        TestSubAgentControl();
        await TestAgentSteerLive();
    }

    static void TestSubAgentControl()
    {
        using var run = new CancellationTokenSource();
        var c = new SubAgentControl(run.Token, canEdit: true);
        bool first = c.Stop();
        Check("AG11 Stop cancels that agent's token, never the run's", first && c.Token.IsCancellationRequested && !run.IsCancellationRequested && !c.ParentCancelled);
        Check("AG11 a second Stop does nothing (idempotent)", !c.Stop() && c.StopRequested);
        Check("AG11 nothing can be sent to an agent that is stopping", c.Inject("look at B.cs") == null);
        c.Dispose();

        using var run2 = new CancellationTokenSource();
        var d = new SubAgentControl(run2.Token);
        string? empty = d.Inject("   "), id1 = d.Inject("  look at A.cs  "), id2 = d.Inject(new string('x', 5000));
        int accepted = 2;
        for (int i = 0; i < 25; i++) if (d.Inject("m" + i) != null) accepted++;
        var taken = d.TakeNotes();
        Check("AG11 an empty message is refused; a message gets its own id, trimmed",
            empty == null && id1 == "note-1" && id2 == "note-2" && taken[0] == ("note-1", "look at A.cs"), $"{empty}|{id1}|{id2}");
        Check("AG11 a long message is cut to 4000 characters, and at most 20 wait at once",
            taken[1].Text.Length == SubAgentControl.MaxNoteChars && accepted == SubAgentControl.MaxQueuedNotes && taken.Count == 20 && d.PendingNotes == 0,
            $"{taken.Count} taken, {accepted} accepted");
        Check("AG11 what it reads names it as the user's message, in the order typed",
            SubAgentControl.NoteBlock(taken.Take(2)).StartsWith("[Message from the user") && SubAgentControl.NoteBlock(taken).IndexOf("• m0") < SubAgentControl.NoteBlock(taken).IndexOf("• m1"));
        d.Inject("late");
        var unread = d.Close();
        Check("AG11 an agent that ended takes nothing more and hands back what it never read",
            unread.Count == 1 && unread[0].Text == "late" && d.Inject("again") == null && !d.Stop() && d.IsClosed);
        run2.Cancel();
        Check("AG11 the run's stop reaches its agents (the link goes one way)", d.Token.IsCancellationRequested && d.ParentCancelled && !d.StopRequested);
        d.Dispose();
    }

    static async Task TestAgentSteerLive()
    {
        var agent = Sp.GetRequiredService<CodeAgentService>();
        var held = typeof(CodeAgentService).GetMethod("RunHeldSubAgentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var optsType = typeof(CodeAgentService).GetNestedType("SubAgentRunOptions", BindingFlags.NonPublic)!;
        var confirm = AgentDetailViewModel.ConfirmStop;
        var asked = new List<string>();
        bool answer = false;
        AgentDetailViewModel.ConfirmStop = (title, text) => { asked.Add(text); return answer; };
        var provider = new NativeScript();
        LocSvc.SetLanguage("th");
        // One sub-agent the way agent_spawn runs it: its own control on the run's token, its loop on the control's token.
        Task<string> Spawn(int id, SubAgentControl ctl) => (Task<string>)held.Invoke(agent, [provider,
            new SubAgentRequest($"Steer task {id}", "", "explore"), ctl.Token,
            Activator.CreateInstance(optsType, id, $"Steer #{id}", "explore", "claude-haiku-5-5", null, null, true, null, null, ctl)])!;
        var runs = (System.Collections.IList)typeof(ChatViewModel).GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
        object chatRun = null;
        AgentNode NodeOf(int id) => Vm.Messages.Where(m => m.AgentNodes != null).SelectMany(m => m.AgentNodes!).LastOrDefault(n => n.Id == id);
        try
        {
            await FreshChat();
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยตรวจ A.cs กับ B.cs" });
            // The chat's run, as a command makes it: the engine's events find it the way a real spawn's do (RunOf the chat).
            var runType = typeof(ChatViewModel).GetNestedType("ChatRun", BindingFlags.NonPublic)!;
            chatRun = Activator.CreateInstance(runType)!;
            runType.GetProperty("Session")!.SetValue(chatRun, Vm.CurrentSession);
            runType.GetProperty("Messages")!.SetValue(chatRun, Vm.Messages);
            runs.Add(chatRun);

            // ── agent 901: it gets a message mid-step, then the owner stops it ──
            using var run = new CancellationTokenSource();
            var ctl = new SubAgentControl(run.Token, canEdit: true);
            var gate1 = new TaskCompletionSource();
            provider.Steps.Enqueue(async (_, ct) =>
            {
                await gate1.Task.WaitAsync(ct);
                return new NativeToolResponse { StopReason = "tool_use", ToolCalls = { new NativeToolCall { Id = "t1", Name = "zz_not_a_tool",
                    Input = System.Text.Json.JsonDocument.Parse("{}").RootElement } } };
            });
            provider.Steps.Enqueue(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
            var task = Spawn(901, ctl);
            await Render(); await Task.Delay(80); await Render();
            var node = NodeOf(901);
            Check("AG12 the engine hands the agent's control to its node on the map", node != null && ReferenceEquals(node.Control, ctl) && node.IsRunning);
            if (node == null) return;
            var win = AgentDetailWindow.Open(node, Win);
            win.Left = -12000;
            await Render(); win.UpdateLayout();
            var vm = win.Vm;
            var stopBtn = (Button)win.FindName("StopButton");
            var sendBtn = (Button)win.FindName("SendButton");
            var draftBox = (TextBox)win.FindName("DraftBox");
            Check("AG12 a running agent CluadeX runs itself: Stop and the message box are live, no reason shown",
                vm.CanStop && vm.CanSend && !vm.HasControlNote && stopBtn.IsEnabled && draftBox.IsEnabled && !sendBtn.IsEnabled,
                $"stop={stopBtn.IsEnabled} box={draftBox.IsEnabled} send={sendBtn.IsEnabled} (send waits for text)");

            vm.Draft = "  check B.cs too  ";
            await Render();
            bool sent = vm.Send();
            await Render(); win.UpdateLayout();
            var noteRow = node.Activity.LastOrDefault();
            Check("AG13 a message typed into its window goes in its log at once, waiting, and the box clears",
                sent && noteRow is { IsRunning: true, Text: "💬 check B.cs too" } && vm.Draft == "" && ctl.PendingNotes == 1
                && Find<TextBlock>((DependencyObject)win.Content).Any(t => t.Text == "💬 check B.cs too"), noteRow?.Text);
            gate1.SetResult();
            for (int i = 0; i < 40 && provider.Texts.Count < 2; i++) await Task.Delay(25);
            await Render(); await Task.Delay(50); await Render();
            Check("AG13 …it reads it at its next step (the model gets it as the user's message), and its row says so",
                provider.Texts.Count >= 2 && provider.Texts[1].Contains("[Message from the user") && provider.Texts[1].Contains("• check B.cs too")
                && noteRow!.IsOk && noteRow.Result == "อ่านแล้วที่ขั้น 2" && ctl.PendingNotes == 0,
                $"{noteRow?.State} · {noteRow?.Result} · calls {provider.Texts.Count}");

            // Stop: it may edit files, so the owner is asked first — a no stops nothing.
            answer = false;
            bool stoppedOnNo = vm.Stop();
            Check("AG14 Stop on an agent that may edit asks first; a no stops nothing",
                !stoppedOnNo && asked.Count == 1 && asked[0].Contains(node.Label) && !ctl.StopRequested && vm.CanStop, $"{asked.Count} asked");
            answer = true;
            bool stopped = vm.Stop();
            bool again = vm.Stop();
            string noteNow = vm.ControlNote;   // read before the engine's "stopped" event lands
            Check("AG14 a yes stops it; pressing Stop again does nothing (not asked again, stopped once), the window says stopping",
                stopped && !again && ctl.StopRequested && asked.Count == 2 && !vm.CanStop && !vm.CanSend
                && noteNow == L("agentdetail.ctl.stopping"), $"asked {asked.Count} · {noteNow}");
            string? result = null; Exception? thrown = null;
            try { result = await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) { thrown = ex; }
            Check("AG14 the main agent gets an ordinary result (no cancel, no crash), and the run goes on",
                thrown == null && result != null && result.StartsWith("[Sub-agent 901 was stopped by the user") && !run.IsCancellationRequested,
                thrown?.GetType().Name ?? result);
            await Render(); await Task.Delay(50); await Render(); win.UpdateLayout();
            var graph = Container(Vm.Messages.Last(m => m.AgentNodes != null)) is { } card ? Find<AgentGraphView>(card).FirstOrDefault() : null;
            var edge = graph?.EdgeFor(node);
            Check("AG14 its node and window say you stopped it; its edge goes quiet (muted, dashed, no light)",
                node.State == "stopped" && node.StoppedByOwner && node.Action == L("agentmap.stoppedByYou")
                && vm.StateText == L("agentdetail.state.stopped") && vm.ControlNote == L("agentdetail.ctl.stoppedByYou")
                && edge != null && ReferenceEquals(edge.Stroke, View.FindResource("Text.Muted")) && edge.StrokeDashArray is { Count: 2 }
                && !Motion.IsLooping(graph!.PulseFor(node)!),
                $"{node.State} · {node.Action} · {vm.ControlNote}");
            Check("AG14 …and every control is off, with that reason in the window",
                !stopBtn.IsEnabled && !draftBox.IsEnabled && !sendBtn.IsEnabled && ctl.IsClosed
                && Find<TextBlock>((DependencyObject)win.Content).Any(t => t.IsVisible && t.Text == L("agentdetail.ctl.stoppedByYou")));
            vm.Draft = "too late";
            Check("AG14 a message to a stopped agent is refused (nothing queued); the reason is the one line under the box",
                !vm.Send() && !vm.HasSendError && vm.HasControlNote && ctl.PendingNotes == 0);

            // ── agent 902: a message it never read (it finished first) ──
            using var run2 = new CancellationTokenSource();
            var ctl2 = new SubAgentControl(run2.Token);
            var gate2 = new TaskCompletionSource();
            provider.Steps.Enqueue(async (_, ct) => { await gate2.Task.WaitAsync(ct); return new NativeToolResponse { TextContent = "A.cs and B.cs look fine." }; });
            var task2 = Spawn(902, ctl2);
            await Render(); await Task.Delay(80); await Render();
            var node2 = NodeOf(902);
            var win2 = AgentDetailWindow.Open(node2, Win);
            win2.Left = -12000;
            asked.Clear();
            win2.Vm.Draft = "also check C.cs";
            bool sent2 = win2.Vm.Send();
            gate2.SetResult();
            string r2 = await task2.WaitAsync(TimeSpan.FromSeconds(10));
            await Render(); await Task.Delay(50); await Render(); win2.UpdateLayout();
            var row2 = node2.Activity.LastOrDefault();
            Check("AG15 a message the agent never read (it finished first) is marked so in its log",
                sent2 && r2.Contains("look fine") && row2 is { IsError: true } && row2.Result == "ไม่ได้อ่าน — เอเจนต์จบก่อน", $"{row2?.State} · {row2?.Result}");
            Check("AG15 a finished agent: controls off, the window says why; a Stop does nothing (and asks nothing)",
                node2.IsDone && !win2.Vm.CanStop && !win2.Vm.CanSend && win2.Vm.ControlNote == L("agentdetail.ctl.done")
                && !((Button)win2.FindName("StopButton")).IsEnabled && !win2.Vm.Stop() && asked.Count == 0 && !ctl2.StopRequested);
            win2.Close();

            // ── agent 903: the run itself stops → the cancel goes up as before, the node ends as stopped (not by you) ──
            using var run3 = new CancellationTokenSource();
            var ctl3 = new SubAgentControl(run3.Token);
            provider.Steps.Enqueue(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
            var task3 = Spawn(903, ctl3);
            await Render(); await Task.Delay(80); await Render();
            run3.Cancel();
            Exception? t3 = null;
            try { await task3.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) { t3 = ex; }
            await Render(); await Task.Delay(50); await Render();
            var node3 = NodeOf(903);
            Check("AG16 the run's own stop still cancels the run, and the agent's node ends as stopped — not 'by you'",
                t3 is OperationCanceledException && node3 is { State: "stopped", StoppedByOwner: false } && node3.Action == L("agentmap.stopped"),
                $"{t3?.GetType().Name} · {node3?.State} · {node3?.Action}");

            // ── agents with no control: a CLI's own (Claude Dev's Task …) and the dispatcher's worker ──
            var cli = new AgentNode { Id = 904, Label = "Explore", Kind = "explore", Task = "CLI's own agent" };
            var worker = new AgentNode { Id = 905, Label = "Worker #1", Kind = "worker", Task = "part 1" };
            var wc = AgentDetailWindow.Open(cli, Win); wc.Left = -12000;
            var ww = AgentDetailWindow.Open(worker, Win); ww.Left = -12000;
            await Render(); wc.UpdateLayout(); ww.UpdateLayout();
            Check("AG16 a CLI's own sub-agent: Stop and the box are off and the window says why (never a dead button)",
                !wc.Vm.CanStop && !wc.Vm.CanSend && wc.Vm.ControlNote == L("agentdetail.ctl.cli") && !((Button)wc.FindName("StopButton")).IsEnabled
                && !((TextBox)wc.FindName("DraftBox")).IsEnabled && ((TextBlock)wc.FindName("ControlNoteText")).IsVisible, wc.Vm.ControlNote);
            Check("AG16 a dispatcher's worker says the splitter runs it with its siblings", !ww.Vm.CanStop && ww.Vm.ControlNote == L("agentdetail.ctl.dispatch"));
            wc.Vm.Draft = "hello";
            Check("AG16 …a message to it is refused, the reason said once (no red copy)", !wc.Vm.Send() && !wc.Vm.HasSendError && wc.Vm.HasControlNote);

            // a picture of the steer box (live agent, a message waiting) for the notes
            var ctl6 = new SubAgentControl(CancellationToken.None, canEdit: true);
            var live = new AgentNode { Id = 906, Label = "General", Kind = "general", Task = "Fix friction in main.gd", Model = "claude-sonnet-5-5", Control = ctl6 };
            live.BeginStep("x1", "read_file main.gd");
            live.EndStep("x1", true, "extends RigidBody2D\nvar friction = 0.9");
            live.BeginStep(ctl6.Inject("use 0.96, not 0.9"), "💬 use 0.96, not 0.9");
            live.BeginStep("x2", "edit_file main.gd");
            var wl = AgentDetailWindow.Open(live, Win); wl.Left = -12000;
            wl.Vm.Draft = "then run the tests";
            await Render(); wl.UpdateLayout();
            string renders = Environment.GetEnvironmentVariable("CHATTEST_RENDERS") is { Length: > 0 } r ? r : AppContext.BaseDirectory;
            SaveWindow(wl, System.IO.Path.Combine(renders, "agent-window-steer.png"));
            ww.Vm.Draft = "";
            SaveWindow(wc, System.IO.Path.Combine(renders, "agent-window-cli.png"));
            ctl6.Dispose();
        }
        finally
        {
            AgentDetailViewModel.ConfirmStop = confirm;
            if (chatRun != null) runs.Remove(chatRun);
            foreach (var w in Application.Current.Windows.OfType<AgentDetailWindow>().ToList()) w.Close();
            LocSvc.SetLanguage("en");
        }
    }
}

/// <summary>An offline native-tool provider: each call runs the next scripted step (which may wait on the call's token);
/// <see cref="Texts"/> keeps the text of the last message each call was sent.</summary>
internal sealed class NativeScript : FakeProvider, IAiProvider
{
    public readonly Queue<Func<List<NativeMessage>, CancellationToken, Task<NativeToolResponse>>> Steps = new();
    public readonly List<string> Texts = new();
    public bool SupportsNativeToolUse => true;

    public async Task<NativeToolResponse> ChatWithToolsAsync(List<NativeMessage> messages, string systemPrompt, List<ToolSchema> tools,
        Action<string> onTextDelta = null, CancellationToken ct = default, string toolChoice = null)
    {
        Texts.Add(string.Join("\n", messages[^1].Content.Where(b => b.Type == "text").Select(b => b.Text)));
        var step = Steps.Count > 0 ? Steps.Dequeue() : (_, _) => Task.FromResult(new NativeToolResponse { TextContent = "ok" });
        return await step(messages, ct);
    }
}
