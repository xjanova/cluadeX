using System.IO;
using System.Reflection;
using CluadeX.Models;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>SW — a sidebar chat still loading (LoadSessionAsync reads it on the thread pool) when the user moves on.
    /// The user's own move wins: New Chat, the title-bar New Chat and deleting the chat on screen are never replaced by
    /// the late load, which used to land over the new chat and leave it behind as a stray empty "New Chat". A pipe task
    /// or an agent's split opening a chat meanwhile does NOT cancel the chat the user picked. And a load that is
    /// overtaken never leaves the chat on screen without its sidebar row.
    ///
    /// Deterministic: the load's commit is a dispatcher continuation, and every move below is made synchronously on the
    /// dispatcher right after the click — so it always comes before the commit. The "(control)" checks prove the load
    /// really was still in flight. SW7 (a run started in the chat being left) races a 6000-row load against the send's
    /// own awaits; its control check says when the run lost that race.</summary>
    static async Task TestSwitchRace()
    {
        Log("\n## SW — a chat still loading never lands over the user's next move");
        string folderBefore = Vm.WorkingDirectory;

        ChatSession Saved(string title, int rows = 2)
        {
            var chat = new ChatSession { Title = title, UpdatedAt = DateTime.Now };
            for (int i = 0; i < rows; i++)
                chat.Messages.Add(new ChatMessage { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = $"{title} row {i}: " + new string('x', 200) });
            Persist.SaveSession(chat);
            Vm.Sessions.Insert(0, chat);
            return chat;
        }
        // Empty chats in the sidebar that are not on screen: what a lost race left behind.
        HashSet<string> Strays() => Vm.Sessions
            .Where(s => s.Id != Vm.CurrentSession?.Id && s.Messages.Count == 0 && !Persist.SessionHasMessages(s.Id))
            .Select(s => s.Id).ToHashSet();
        List<string> NewStrays(HashSet<string> before) => Strays().Where(id => !before.Contains(id)).ToList();
        bool HasRow(ChatSession c) => c != null && Vm.Sessions.Count(s => s.Id == c.Id) == 1;
        async Task Landed(int ms = 700) { await Task.Delay(ms); await Render(); }   // any late load has landed by now

        var x = Saved("sw x");
        var a = Saved("sw a");
        var b = Saved("sw b");

        // ── SW1: New Chat (chat button / context menu) while A loads ──
        await Open(x.Id);
        var before = Strays();
        Vm.LoadSessionCommand.Execute(a.Id);
        bool inFlight = Vm.CurrentSession?.Id == x.Id;
        Vm.NewSessionCommand.Execute(null);
        var fresh = Vm.CurrentSession;
        await Landed();
        Check("SW1 (control) A was still loading when New Chat was pressed, and New Chat made a new chat",
            inFlight && fresh != null && fresh.Id != x.Id && fresh.Id != a.Id, $"inFlight={inFlight} fresh={fresh?.Title}");
        Check("SW1 New Chat pressed while a sidebar chat loads: the new chat stays on screen (the late load never lands)",
            fresh != null && ReferenceEquals(Vm.CurrentSession, fresh) && Vm.Messages.Count == 0, $"on screen: {Vm.CurrentSession?.Title}");
        Check("SW1 …with its sidebar row, and no stray empty \"New Chat\" left behind", HasRow(fresh) && NewStrays(before).Count == 0,
            $"row={HasRow(fresh)} strays={NewStrays(before).Count}");

        // ── SW2: the title bar / Ctrl+N / palette New Chat while A loads ──
        await Open(x.Id);   // the untouched chat of SW1 is dropped on the way
        before = Strays();
        Vm.LoadSessionCommand.Execute(a.Id);
        inFlight = Vm.CurrentSession?.Id == x.Id;
        Vm.NewChatAndShowCommand.Execute(null);
        fresh = Vm.CurrentSession;
        await Landed();
        Check("SW2 the title-bar New Chat (Ctrl+N, palette) while a chat loads: the new chat stays, no stray row",
            inFlight && fresh != null && fresh.Id != a.Id && ReferenceEquals(Vm.CurrentSession, fresh) && HasRow(fresh) && NewStrays(before).Count == 0,
            $"inFlight={inFlight} on screen={Vm.CurrentSession?.Title} strays={NewStrays(before).Count}");

        // ── SW3: New Chat on an untouched chat (it is reused) while A loads: the pick is still cancelled ──
        var untouched = Vm.CurrentSession;
        before = Strays();
        Vm.LoadSessionCommand.Execute(a.Id);
        inFlight = ReferenceEquals(Vm.CurrentSession, untouched);
        Vm.NewSessionCommand.Execute(null);
        await Landed();
        Check("SW3 New Chat on an untouched chat while another loads: that chat is reused and stays on screen, with its row",
            inFlight && untouched != null && ReferenceEquals(Vm.CurrentSession, untouched) && HasRow(untouched) && NewStrays(before).Count == 0,
            $"inFlight={inFlight} on screen={Vm.CurrentSession?.Title} row={HasRow(untouched)}");

        // ── SW4: an overtaken load from an empty chat: it stays on screen WITH its row, or it is gone ──
        var empty = Vm.CurrentSession;
        Vm.LoadSessionCommand.Execute(a.Id);
        Vm.LoadSessionCommand.Execute(empty!.Id);   // clicked straight back to the chat on screen: A's load loses
        await Landed();
        Check("SW4 an empty chat whose switch away was overtaken (clicked back) stays on screen with its sidebar row",
            ReferenceEquals(Vm.CurrentSession, empty) && HasRow(empty), $"on screen={Vm.CurrentSession?.Title} row={HasRow(empty)}");

        foreach (var id in new[] { a.Id, b.Id, empty.Id }) Vm.LoadSessionCommand.Execute(id);   // a burst that ends back on it
        await Landed();
        Check("SW4 a burst of clicks ending on the empty chat on screen: it is still there, with its row",
            ReferenceEquals(Vm.CurrentSession, empty) && HasRow(empty), $"on screen={Vm.CurrentSession?.Title} row={HasRow(empty)}");

        before = Strays();
        Vm.LoadSessionCommand.Execute(a.Id);
        Vm.LoadSessionCommand.Execute(b.Id);   // overtakes A; B lands
        await WaitUntil(() => Vm.CurrentSession?.Id == b.Id, 5000);
        await Landed();
        Check("SW4 an empty chat left for a chat that does land is gone (no row, not on screen) — never on screen without a row",
            Vm.CurrentSession?.Id == b.Id && !Vm.Sessions.Any(s => s.Id == empty.Id) && NewStrays(before).Count == 0,
            $"on screen={Vm.CurrentSession?.Title} rowLeft={Vm.Sessions.Any(s => s.Id == empty.Id)}");

        // ── SW5: deleting the chat on screen while a (big, slow) chat loads ──
        var big = Saved("sw big", rows: 3000);
        var doomed = Saved("sw doomed");
        await Open(doomed.Id);
        before = Strays();
        bool confirmWas = ConfirmAnswer;
        ConfirmAnswer = true;
        Vm.LoadSessionCommand.Execute(big.Id);
        inFlight = Vm.CurrentSession?.Id == doomed.Id;
        Vm.DeleteSessionCommand.Execute(doomed.Id);
        var afterDelete = Vm.CurrentSession;   // the new chat, when the delete ran through before the load could land
        bool deleteRanFirst = afterDelete != null && afterDelete.Id != doomed.Id && afterDelete.Id != big.Id;
        await WaitUntil(() => !Vm.Sessions.Any(s => s.Id == doomed.Id), 5000);
        await Landed(1500);
        ConfirmAnswer = confirmWas;
        Check("SW5 (control) the big chat was still loading when the chat on screen was deleted", inFlight);
        Check("SW5 deleting the chat on screen while another loads: it is gone from the sidebar and the database",
            !Vm.Sessions.Any(s => s.Id == doomed.Id) && Persist.LoadSession(doomed.Id) == null);
        Check("SW5 …the new chat it opened stays on screen (the late load never lands over it), no stray row",
            (deleteRanFirst ? ReferenceEquals(Vm.CurrentSession, afterDelete) && HasRow(afterDelete) : Vm.CurrentSession?.Id == big.Id)
            && NewStrays(before).Count == 0,
            $"deleteRanFirst={deleteRanFirst} on screen={Vm.CurrentSession?.Title} strays={NewStrays(before).Count}");

        // ── SW6: a pipe task / an agent's split opening a chat does NOT cancel the user's pick ──
        await Open(x.Id);
        before = Strays();
        Vm.LoadSessionCommand.Execute(a.Id);
        inFlight = Vm.CurrentSession?.Id == x.Id;
        Vm.NewSession();   // the pipe's entry (McpHostService): not the user
        await WaitUntil(() => Vm.CurrentSession?.Id == a.Id, 5000);
        await Landed();
        Check("SW6 a pipe task's new chat while the user's pick loads: the user's pick lands",
            inFlight && Vm.CurrentSession?.Id == a.Id, $"inFlight={inFlight} on screen={Vm.CurrentSession?.Title}");
        Check("SW6 …and the pipe's chat it replaced (still untouched) leaves no stray row", NewStrays(before).Count == 0, $"strays={NewStrays(before).Count}");

        await Open(x.Id);
        Vm.LoadSessionCommand.Execute(b.Id);
        inFlight = Vm.CurrentSession?.Id == x.Id;
        var openForAgent = typeof(CluadeX.ViewModels.ChatViewModel).GetMethod("OpenChatForAgent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // A link store of its own: the earlier split checks used up the 10-minute split ceiling (SplitLimits).
        var linksBefore = CluadeX.Services.ChatLinkStore.Current;
        CluadeX.Services.ChatLinkStore.Current = new CluadeX.Services.ChatLinkStore(Path.Combine(Settings.DataRoot, "sw-links-" + Guid.NewGuid().ToString("N")[..6]));
        string report;
        try { report = (string)openForAgent.Invoke(Vm, new object[] { null, null, "sw agent split", null, false, null })!; }
        finally { CluadeX.Services.ChatLinkStore.Current = linksBefore; }
        var split = Vm.Sessions.FirstOrDefault(s => s.Title == "sw agent split");
        await WaitUntil(() => Vm.CurrentSession?.Id == b.Id, 5000);
        await Landed();
        Check("SW6 an agent's new_chat while the user's pick loads: the user's pick lands",
            inFlight && split != null && Vm.CurrentSession?.Id == b.Id, $"inFlight={inFlight} split={split?.Id} on screen={Vm.CurrentSession?.Title} · {report}");
        Check("SW6 …and the agent's chat keeps its sidebar row and is saved (its note is not lost)",
            split != null && HasRow(split) && await WaitUntil(() => Persist.SessionHasMessages(split.Id), 5000),
            $"row={HasRow(split)} saved={split != null && Persist.SessionHasMessages(split.Id)}");

        // ── SW7: Send pressed in the chat being left while the pick still loads ──
        // The switch decides on the chat on screen when it lands, not at the click: deciding on the click-time capture
        // ("no run here") answered the new run's permission prompt "dismissed" as the picked chat came on screen.
        var slow = Saved("sw slow", rows: 6000);
        await Open(x.Id);
        var gate7 = new SemaphoreSlim(0);
        Fake.ScriptFor["[SW7]"] = new Queue<FakeStep>([FakeStep.Gated(["SW7 working"], gate7, [" — SW7 done."])]);
        Vm.LoadSessionCommand.Execute(slow.Id);
        Vm.UserInput = "[SW7] run the tests";
        Vm.SendMessageCommand.Execute(null);
        await WaitUntil(() => Vm.IsGenerating || Vm.CurrentSession?.Id != x.Id, 5000);
        bool runFirst = Vm.IsGenerating && Vm.CurrentSession?.Id == x.Id;
        var answers7 = new List<bool>();
        ChatMessage? prompt7 = runFirst ? AddPrompt(answers7, "Allow run_command to run `npm test`? (SW7)") : null;
        await WaitUntil(() => Vm.CurrentSession?.Id == slow.Id, 10000);
        await Landed();
        Check("SW7 (control) the run started in the chat on screen while the picked chat was still loading", runFirst,
            $"generating={Vm.IsGenerating} on screen={Vm.CurrentSession?.Title}");
        Check("SW7 the pick lands over a chat whose run started meanwhile: that run's open permission prompt is NOT dismissed",
            runFirst && Vm.CurrentSession?.Id == slow.Id && prompt7 is { PermissionAnswered: false } && answers7.Count == 0,
            $"on screen={Vm.CurrentSession?.Title} answered={prompt7?.PermissionAnswered} callbacks={answers7.Count}");
        Check("SW7 …and the run goes on in the background", Vm.HasRunningElsewhere && !Vm.IsGenerating);
        prompt7?.ResolvePermission(PermissionOutcome.Dismissed);
        gate7.Release();
        bool done7 = await WaitUntil(() => !Vm.HasRunningElsewhere, 20000);
        var storedX = Persist.LoadSession(x.Id);
        Check("SW7 …and finishes into its own chat, never into the chat that landed",
            done7 && storedX != null && storedX.Messages.Any(m => m.Content.Contains("SW7 done")) && !Vm.Messages.Any(m => m.Content.Contains("SW7")),
            $"done={done7} elsewhere={Vm.HasRunningElsewhere} on screen={Vm.CurrentSession?.Title} SW7 rows on screen="
            + string.Join(" / ", Vm.Messages.Where(m => m.Content.Contains("SW7")).Select(m => m.Role + ":" + (m.Content.Length > 60 ? m.Content[..60] : m.Content)))
            + " · stored: " + (storedX == null ? "not stored" : string.Join(" | ", storedX.Messages.Select(m => m.Content.Length > 40 ? m.Content[..40] : m.Content))));

        // ── SW8: the UI Studio opens a project into the chat on screen (reused, untouched) while a big chat loads ──
        // The Studio's own move wins like New Chat does: only the branch that made a new chat used to cancel the load, so a
        // reused chat had the late chat land over it and the Studio followed that chat's project instead.
        var bigQuiet = Saved("sw quiet big", rows: 3000);
        string quiet = Path.Combine(PipeRoot, "sw-quiet-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(quiet);
        Vm.NewSession(keepProject: false);
        await Render();
        var reused = Vm.CurrentSession;
        Vm.LoadSessionCommand.Execute(bigQuiet.Id);
        inFlight = ReferenceEquals(Vm.CurrentSession, reused);
        Vm.OpenProjectQuietly(quiet);
        bool reusedIt = ReferenceEquals(Vm.CurrentSession, reused);
        await Landed(1500);
        Check("SW8 (control) the big chat was still loading when the Studio opened a project into the untouched chat on screen (reused)",
            inFlight && reusedIt, $"inFlight={inFlight} reused={reusedIt}");
        Check("SW8 the Studio's project open wins: the late load never lands, the chat on screen keeps the project just opened",
            ReferenceEquals(Vm.CurrentSession, reused) && SamePath(Vm.CurrentSession?.ProjectPath ?? "", quiet) && SamePath(Vm.ChatProject, quiet),
            $"on screen={Vm.CurrentSession?.Title} project={Vm.ChatProject}");

        Vm.NewSession(keepProject: false);
        await Render();
        Vm.SetWorkingDirectory(folderBefore);
        await Render();
    }
}
