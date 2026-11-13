using System.IO;
using System.Reflection;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;

namespace ChatTest;

/// <summary>
/// SR — a split chat reports back to the chat that opened it, and the two can message each other (owner 2026-11-11:
/// "เมื่อแยกย่อยไปทำงานแล้วเสร็จก็กลับไปรายงาน แชทหลัก … ต้องใช้ได้กับทุกโมเดล"). Through the real chat page and the real
/// native loop with a scripted provider (no model, no network): new_chat from a working chat → the split is told who
/// opened it → its whole report reaches the idle parent, which gets a turn of its own to carry on; an owner-driven turn
/// leaves a row only; the parent waits for its last working split; a stopped parent is not woken; message_chat both ways
/// and its refusals and limit; close_chat read; a split opened while the owner is in another chat does not take the screen.
/// </summary>
internal static partial class Program
{
    static async Task TestSplitReports()
    {
        Log("\n## SR — split chats report back to their parent, and message each other");
        var provider = new SplitScript();
        object before = Field<object>(Providers, "_activeProvider");
        bool agenticWas = Vm.AgenticMode;
        bool permBefore = Settings.Settings.Features.PermissionSystem;
        bool autoBefore = Settings.Settings.SplitAutoContinue;
        var linksBefore = ChatLinkStore.Current;
        var store = ChatLinkStore.Current = new ChatLinkStore(Path.Combine(Settings.DataRoot, "sr-links-" + Guid.NewGuid().ToString("N")[..6]));
        string proj = Path.Combine(PipeRoot, "split-reports");
        var gateB = new SemaphoreSlim(0);

        static NativeToolResponse Say(string text) => new() { StopReason = "end_turn", TextContent = text };
        static NativeToolResponse Tool(string id, string name, object args) => new()
        {
            StopReason = "tool_use",
            ToolCalls = { new NativeToolCall { Id = id, Name = name, Input = JsonSerializer.SerializeToElement(args) } },
        };
        // A long report: its end is far past what the parent's row shows (400 chars).
        string longReport = "SR1 report: " + string.Join(" ", Enumerable.Range(1, 160).Select(i => $"step{i}")) + " SR1-END";
        provider.Respond = async (messages, last, ct) =>
        {
            if (messages[^1].Content.Any(b => b.Type == "tool_result")) return Say("Started it; its report comes back here.");
            if (last.Contains("has ended its turn") || last.Contains("have ended their turns")) return Say("Carried on from the reports.");
            if (last.Contains("[SR1-child]")) return Say(longReport);
            if (last.Contains("[SR1]"))
                return Tool("n1", "new_chat", new { title = "SR1 docs split", message = "[SR1-child] write the docs" });
            if (last.Contains("[SR3-B]")) { await gateB.WaitAsync(ct); return Say("B done SR3-B-END"); }
            if (last.Contains("[SR3-A]")) return Say("A done SR3-A-END");
            if (last.Contains("[SR4-child]")) return Say("SR4 child done");
            if (last.Contains("[SR7-child]")) return Say("SR7 child done");
            return Say("ok");
        };

        ChatSession StoredChat(string title)
        {
            var c = new ChatSession { Title = title, ProjectPath = proj };
            c.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "plan " + title });
            c.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "ok" });
            Persist.SaveSession(c);
            Vm.Sessions.Insert(0, new ChatSession { Id = c.Id, Title = c.Title, ProjectPath = proj });
            return Vm.Sessions[0];
        }
        ChatSession Split(ChatSession parent, string title)
        {
            var kid = StoredChat(title);
            store.Link(kid.Id, parent.Id, title);
            return kid;
        }
        List<ChatMessage> RowsOf(string id) =>
            Vm.CurrentSession?.Id == id ? Vm.Messages.ToList() : (Persist.LoadSession(id)?.Messages ?? new());
        bool Running(string id) => Vm.Sessions.FirstOrDefault(s => s.Id == id) is { } s && CallVm("RunOf", s) != null;
        string Message(string fromId, string target, string text)
        {
            try { return (string)CallVm("MessageChatForAgent", fromId, target, text); }
            catch (TargetInvocationException ex) { return "THREW " + ex.InnerException?.Message; }
        }
        var lastEnd = Field<Dictionary<string, (bool, bool)>>(Vm, "_lastTurnEnd");

        try
        {
            if (Directory.Exists(proj)) Directory.Delete(proj, true);
            Directory.CreateDirectory(proj);
            SetField(Providers, "_activeProvider", provider);
            Vm.AgenticMode = true;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = false; s.SplitAutoContinue = true; });

            // ── SR1: new_chat from a working chat on screen → the split's report wakes the idle parent ──
            await FreshChat();
            FreeVm();
            Vm.SetWorkingDirectory(proj);
            var parent = Vm.CurrentSession!;
            await Send("[SR1] split the docs work off");
            var child = await WaitUntil(() => Vm.Sessions.Any(s => s.Title == "SR1 docs split"), 15000)
                ? Vm.Sessions.First(s => s.Title == "SR1 docs split") : null;
            Check("SR1 new_chat opens a split chat under the asking chat", child != null && child.ParentId == parent.Id,
                child == null ? "(no split)" : child.ParentId ?? "no parent");
            if (child == null) return;
            bool childRan = await WaitUntil(() => provider.Lasts.Any(l => l.Contains("[SR1-child]")), 15000);
            string childCall = provider.Lasts.FirstOrDefault(l => l.Contains("[SR1-child]")) ?? "";
            Check("SR1 the split's agent is told who opened it and that its answer goes back there by itself",
                childRan && childCall.Contains("This chat was opened by chat") && childCall.Contains("goes back to that chat by itself")
                && childCall.Contains("message_chat"), childCall.Length > 400 ? childCall[..400] : childCall);
            int wakeAt = -1;
            bool woke = await WaitUntil(() => (wakeAt = provider.Lasts.FindIndex(l => l.Contains("has ended its turn"))) >= 0, 20000);
            string wakeAll = woke ? provider.Alls[wakeAt] : "";
            Check("SR1 the parent was idle: a turn starts there by itself once the split ends its turn", woke,
                string.Join(" | ", provider.Lasts.TakeLast(3).Select(l => l.Length > 80 ? l[..80] : l)));
            Check("SR1 …and its agent reads the split's WHOLE report (past the 400 characters the row shows), who sent it and how it ended",
                wakeAll.Contains("SR1-END") && wakeAll.Contains("Split chat \"SR1 docs split\"") && wakeAll.Contains("ended a turn"),
                wakeAll.Length > 300 ? wakeAll[^300..] : wakeAll);
            await WaitUntil(() => !Running(parent.Id) && !Running(child.Id), 15000);
            await Render();
            var pRows = RowsOf(parent.Id);
            var note = pRows.LastOrDefault(m => m.IsNotice && m.Content.Contains("SR1 docs split"));
            Check("SR1 the parent keeps the report as a row: its start for the owner, the whole of it for the model",
                note != null && note.Content.StartsWith("✅") && note.Content.Length < longReport.Length && (note.ToolOutput ?? "").Contains("SR1-END"),
                note?.Content ?? string.Join(" | ", pRows.Select(m => m.Content.Length > 50 ? m.Content[..50] : m.Content)));
            Check("SR1 the woken turn shows as a 🔔 message in the parent, then its reply",
                pRows.Any(m => m.Role == MessageRole.User && m.Content.StartsWith(LocF("chat.split.wake", 1)))
                && pRows.Any(m => m.Role == MessageRole.Assistant && m.Content.Contains("Carried on from the reports")),
                string.Join(" | ", pRows.TakeLast(4).Select(m => m.Role + ":" + (m.Content.Length > 50 ? m.Content[..50] : m.Content))));
            int wakesSr1 = provider.Lasts.Count(l => l.Contains("has ended its turn"));
            await Task.Delay(2600);
            Check("SR1 one wake for one report (the parent's own reply does not wake anything again)",
                provider.Lasts.Count(l => l.Contains("has ended its turn")) == wakesSr1 && wakesSr1 == 1, wakesSr1.ToString());

            // ── SR1n: the report has a box of its own in the parent (owner 2026-11-13: "การส่งโน๊ตกลับมาในห้องแชทของแชทย่อย
            //    ควรมีกล่องข้อความที่แตกต่างด้วยไม่ใช่ไม่มีกล่องอะไรเลย") ──
            if (Vm.CurrentSession?.Id != parent.Id) { Vm.LoadSessionCommand.Execute(parent.Id); await WaitUntil(() => Vm.CurrentSession?.Id == parent.Id, 8000); }
            await Render();
            var noteRow = Vm.Messages.LastOrDefault(m => m.IsNotice && m.Content.Contains("SR1 docs split"));
            Check("SR1n a split's report is a note from another chat (it carries what the model reads), not the app's own notice",
                noteRow is { IsChatNote: true } && !new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = "🌿 a worktree" }.IsChatNote,
                noteRow == null ? "row not on screen" : $"IsChatNote {noteRow.IsChatNote}");
            var noteCards = Find<System.Windows.FrameworkElement>(View).Where(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "CxChat.NoticeCard" && e.IsVisible).ToList();
            var noteCard = noteCards.FirstOrDefault(c => ReferenceEquals(c.DataContext, noteRow));
            Check("SR1n it is drawn in the notice card: a filled, bordered box with the \"note from another chat\" caption and its text",
                noteCard is System.Windows.Controls.Border { BorderThickness.Left: > 0, Background: not null } && noteCard.ActualHeight > 30
                && Find<System.Windows.Controls.TextBlock>(noteCard).Any(t => t.IsVisible && t.Text == LocalizedResources.Instance["chat.note.fromChat"]),
                noteCard == null ? $"{noteCards.Count} card(s) on screen, none for the row" : $"{noteCard.ActualWidth:0}×{noteCard.ActualHeight:0}");
            if (Environment.GetEnvironmentVariable("CHATTEST_RENDERS") is { Length: > 0 } shotDir)
            {
                Directory.CreateDirectory(shotDir);
                SaveShot(Path.Combine(shotDir, "notice-card.png"));
            }

            // ── SR2: the owner's own turn in a split: a row in the parent (off screen, saved), no turn ──
            await FreshChat();
            var p2 = StoredChat("SR2 parent");
            var c2 = Split(p2, "SR2 split");
            int calls2 = provider.Lasts.Count;
            CallVm("TellParentSplitFinished", c2, new List<ChatMessage> { new() { Role = MessageRole.Assistant, Content = "SR2 owner-driven answer" } });
            await Task.Delay(2600);
            var row2 = RowsOf(p2.Id).LastOrDefault(m => m.IsNotice && m.Content.Contains("SR2 split"));
            Check("SR2 an idle parent that is not on screen still gets the report, saved with it (it used to be dropped)",
                row2 != null && row2.Content.Contains("SR2 owner-driven answer"), RowsOf(p2.Id).Count.ToString());
            Check("SR2 …and the owner's own turn starts nothing in the parent", provider.Lasts.Count == calls2,
                (provider.Lasts.Count - calls2).ToString());

            // ── SR3 + SR5: message_chat starts idle splits; the parent waits for the last one, then carries on from both ──
            var p3 = StoredChat("SR3 parent");
            var a3 = Split(p3, "SR3 A");
            var b3 = Split(p3, "SR3 B");
            string toB = Message(p3.Id, ChatRef.Of(b3.Id), "[SR3-B] do B");
            await WaitUntil(() => Running(b3.Id), 10000);
            string toA = Message(p3.Id, "SR3 A", "[SR3-A] do A");
            Check("SR5 message_chat to an idle split starts a turn there (by reference or by title)",
                toA.StartsWith("Started a turn") && toB.StartsWith("Started a turn"), toA + " | " + toB);
            bool aRan = await WaitUntil(() => provider.Lasts.Any(l => l.Contains("[SR3-A]")), 15000);
            string aCall = provider.Lasts.FirstOrDefault(l => l.Contains("[SR3-A]")) ?? "";
            Check("SR5 the split reads it as a message from the chat that opened it", aRan && aCall.Contains("Message from the chat that opened you")
                && aCall.Contains("SR3 parent"), aCall.Length > 300 ? aCall[..300] : aCall);
            await WaitUntil(() => RowsOf(p3.Id).Any(m => m.IsNotice && m.Content.Contains("SR3 A")), 15000);
            await Task.Delay(2600);
            var rowA = RowsOf(p3.Id).LastOrDefault(m => m.IsNotice && m.Content.Contains("SR3 A"));
            Check("SR3 a split ends while another split of the parent still works: its row says the parent carries on later, no turn yet",
                rowA != null && rowA.Content.Contains(LocF("chat.split.waitOthers", 1)) && !provider.Lasts.Any(l => l.Contains("have ended their turns"))
                && !Running(p3.Id), rowA?.Content ?? "(no row)");
            gateB.Release();
            woke = await WaitUntil(() => provider.Lasts.Any(l => l.Contains("have ended their turns")), 20000);
            int i3 = provider.Lasts.FindIndex(l => l.Contains("have ended their turns"));
            string all3 = i3 >= 0 ? provider.Alls[i3] : "";
            Check("SR3 the last split to finish wakes the parent ONCE, with both reports",
                woke && provider.Lasts[i3].Contains("The 2 split chats") && all3.Contains("SR3-A-END") && all3.Contains("SR3-B-END"),
                i3 >= 0 ? provider.Lasts[i3][..Math.Min(200, provider.Lasts[i3].Length)] : "(no wake)");
            await WaitUntil(() => !Running(p3.Id), 15000);

            // ── SR6: close_chat read gives a split's whole last answer; without a chat it is refused ──
            string read = (string)CallVm("ReadSplitForAgent", p3.Id, "SR3 B");
            Check("SR6 close_chat read: the split's whole last answer and its state", read.Contains("B done SR3-B-END") && read.Contains("finished"), read);
            var noChat = await new ProjectToolModule().ExecuteAsync(new ToolCall
            {
                Type = ToolType.CloseChat, ToolName = "close_chat", Arguments = { ["read"] = "true" },
            }, new ToolModuleContext { WorkingDirectory = proj }, default);
            Check("SR6 close_chat read without a chat is refused, and says so", !noChat.Success && noChat.Output.Contains("chat"), noChat.Output);

            // ── SR4: the parent's own last turn was stopped (the owner took over): a report does not wake it ──
            var p4 = StoredChat("SR4 parent");
            var c4 = Split(p4, "SR4 split");
            lastEnd[p4.Id] = (true, false);
            int wakes4 = provider.Lasts.Count(l => l.Contains("has ended its turn"));
            Message(p4.Id, "SR4 split", "[SR4-child] go");
            await WaitUntil(() => RowsOf(p4.Id).Any(m => m.IsNotice && m.Content.Contains("SR4 split")), 15000);
            await Task.Delay(2600);
            Check("SR4 a parent whose last turn was stopped keeps the report as a row and is not woken",
                RowsOf(p4.Id).Any(m => m.IsNotice && m.Content.Contains("SR4 child done"))
                && provider.Lasts.Count(l => l.Contains("has ended its turn")) == wakes4 && !Running(p4.Id));

            // ── SR5: what message_chat refuses, and its limit per pair ──
            string noParent = Message(p4.Id, "parent", "hello");
            string stranger = Message(p4.Id, "SR3 A", "hello");
            Check("SR5 \"parent\" from a chat nobody split off, and a chat that is not its split, are refused with the reason",
                noParent.StartsWith("THREW") && noParent.Contains("not a split chat")
                && stranger.StartsWith("THREW") && stranger.Contains("No chat") && stranger.Contains("SR4 split"), noParent + " | " + stranger);
            string up = Message(c4.Id, "parent", "SR5 a question for the parent");
            Check("SR5 a split reaches its parent with \"parent\" (stopped parent: left there, not started)",
                up.StartsWith("Not started") && RowsOf(p4.Id).Any(m => m.IsNotice && m.Content.Contains("SR5 a question")), up);
            lastEnd[c4.Id] = (true, false);
            var answers = new List<string>();
            for (int i = 0; i < 12; i++) answers.Add(Message(p4.Id, "SR4 split", $"note {i}"));
            int started = answers.Count(a => a.StartsWith("Not started")) + 1;   // + the [SR4-child] message above
            Check("SR5 a stopped split is not started by a message (it is left there for the owner)", answers[0].StartsWith("Not started"), answers[0]);
            Check("SR5 at most 6 messages per pair in 30 minutes, then refused with the reason (no endless back-and-forth)",
                started == 6 && answers[^1].StartsWith("THREW") && answers[^1].Contains("already sent 6"), $"{started}: {answers[^1]}");

            // ── SR7: new_chat from a chat that runs off screen opens the split beside the owner's work ──
            await FreshChat();
            var onScreen = Vm.CurrentSession!;
            var p7 = StoredChat("SR7 parent");
            string opened = (string)CallVm("OpenModelChatForAgent", null, "[SR7-child] go", "SR7 split", p7.Id, false, null, null, null);
            var c7 = Vm.Sessions.FirstOrDefault(s => s.Title == "SR7 split");
            Check("SR7 the owner is in another chat: the split opens in the sidebar under its parent, the screen stays",
                c7 != null && c7.ParentId == p7.Id && ReferenceEquals(Vm.CurrentSession, onScreen) && opened.Contains("in the background"), opened);
            bool ran7 = c7 != null && await WaitUntil(() => RowsOf(c7.Id).Any(m => m.Role == MessageRole.Assistant && m.Content.Contains("SR7 child done")), 20000);
            Check("SR7 …and its first turn runs there (it used to never start when the owner had moved on)", ran7 && ReferenceEquals(Vm.CurrentSession, onScreen));
            bool woke7 = await WaitUntil(() => RowsOf(p7.Id).Any(m => m.Role == MessageRole.Assistant && m.Content.Contains("Carried on from the reports")), 20000);
            Check("SR7 …and its report wakes the parent off screen too", woke7 && ReferenceEquals(Vm.CurrentSession, onScreen));

            // ── SR8: the tool is there for every model: in-app tools, and the CLI bridge's allow-list ──
            var mod = new ProjectToolModule();
            Check("SR8 message_chat (and its aliases) is one of the agent's tools, with chat and message",
                mod.ResolveToolType("message_chat") == ToolType.MessageChat && mod.ResolveToolType("message_parent") == ToolType.MessageChat
                && mod.GetToolSchemas().FirstOrDefault(s => s.Name == "message_chat") is { } sch
                && JsonSerializer.Serialize(sch.InputSchema).Contains("\"chat\"") && JsonSerializer.Serialize(sch.InputSchema).Contains("\"message\""));
            Check("SR8 CLI agents reach it through the chat tool bridge", ChatToolBridge.QualifiedToolNames.Any(n => n.EndsWith("message_chat"))
                && Enum.GetValues<ChatToolBridge.CliToolNames>().All(n => ChatToolBridge.ChatToolsHint(n, splitChats: false).Contains("message_chat")));

            // ── SR9: which chat new work belongs to — the parent's turn lists its open splits; new_chat refuses a lookalike ──
            var p9 = StoredChat("SR9 parent");
            var c9a = Split(p9, "SR9 แก้ปุ่ม login หน้าแรก");
            var c9b = Split(p9, "SR9 build the sales dashboard");
            var c9old = Split(p9, "SR9 old docs work");
            store.SetArchived(c9old.Id, true);
            string? note9 = (string?)CallVm("SplitTurnInstruction", p9);
            Check("SR9 a parent's turn lists its open split chats (not the put-away ones) and says where new work goes",
                note9 != null && note9.Contains(ChatRef.Of(c9a.Id)) && note9.Contains(ChatRef.Of(c9b.Id)) && !note9.Contains("old docs")
                && note9.Contains("message_chat") && note9.Contains("how things stand"), note9 ?? "null");
            Check("SR9 a chat with no split chats gets no note (no tokens spent on it)",
                CallVm("SplitTurnInstruction", StoredChat("SR9 lonely")) == null);
            string? childNote9 = (string?)CallVm("SplitTurnInstruction", c9a);
            Check("SR9 a split chat still hears who opened it (and no children list it does not have)",
                childNote9 != null && childNote9.Contains("This chat was opened by chat") && !childNote9.Contains("not put away"), childNote9 ?? "null");
            Check("SR9 new_chat finds the open split that already has the work (other wording around it), and a sibling's too",
                ProjectToolModule.SameWorkAs(p9.Id, "SR9 แก้ปุ่ม login หน้าแรกให้กดได้บนมือถือ")?.Id == c9a.Id
                && ProjectToolModule.SameWorkAs(c9b.Id, "SR9 แก้ปุ่ม login หน้าแรก") is { Sibling: true } sib9 && sib9.Id == c9a.Id
                && ProjectToolModule.SameWorkAs(p9.Id, "write the release notes") == null
                && ProjectToolModule.SameWorkAs(p9.Id, "SR9 old docs work") == null);
            lastEnd[c9a.Id] = (true, false);
            string toSib = Message(c9b.Id, ChatRef.Of(c9a.Id), "SR9 the button also needs a mobile size");
            Check("SR9 a split chat can hand work to its sibling with message_chat (it is that chat's work now)",
                toSib.StartsWith("Not started")
                && RowsOf(c9a.Id).Any(m => m.IsNotice && m.Content.Contains("SR9 the button")), toSib);
            await WaitUntil(() => Vm.Sessions.All(s => !Running(s.Id)), 15000);
        }
        finally
        {
            gateB.Release(5);
            SetField(Providers, "_activeProvider", before);
            Vm.AgenticMode = agenticWas;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = permBefore; s.SplitAutoContinue = autoBefore; });
            ChatLinkStore.Current = linksBefore;
            Vm.SetWorkingDirectory("");
            await Render();
            try { Directory.Delete(proj, true); } catch { }
        }
    }
}

/// <summary>A native-tool provider that answers by what each request is (a marker in its last message), so several
/// chats can run at once: <see cref="Lasts"/> is each request's last message, <see cref="Alls"/> the whole of it.</summary>
internal sealed class SplitScript : FakeProvider, IAiProvider
{
    public Func<List<NativeMessage>, string, CancellationToken, Task<NativeToolResponse>> Respond =
        (_, _, _) => Task.FromResult(new NativeToolResponse { TextContent = "ok" });
    public readonly List<string> Lasts = new();
    public readonly List<string> Alls = new();
    public bool SupportsNativeToolUse => true;

    static string TextOf(NativeMessage m) =>
        string.Join("\n", m.Content.Select(b => b.Type == "tool_result" ? b.Content : b.Text).Where(t => !string.IsNullOrEmpty(t)));

    public async Task<NativeToolResponse> ChatWithToolsAsync(List<NativeMessage> messages, string systemPrompt, List<ToolSchema> tools,
        Action<string> onTextDelta = null, CancellationToken ct = default, string toolChoice = null)
    {
        string last = TextOf(messages[^1]);
        lock (Lasts) { Lasts.Add(last); Alls.Add(string.Join("\n", messages.Select(TextOf))); }
        await Task.Delay(20, ct);
        return await Respond(messages, last, ct);
    }
}
