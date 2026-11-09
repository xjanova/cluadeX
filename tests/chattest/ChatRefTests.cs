using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;

namespace ChatTest;

/// <summary>
/// CR — the chat's reference (owner 2026-11-09: "เพิ่ม รหัสอ้างอิงแชท ให้เห็น … ก๊อปวางได้ ตรง หัวห้องบนสุดของหน้าแชท … และเอไอก็นำเลขนี้มา
/// อิงใช้ได้เช่นกัน"). A short "CX-8cf58c2a" per chat, computed from its id: its format and stability, the forms a pasted
/// one is read in, the history search finding the chat by it, the line every turn carries to the model (an API-style
/// provider and a CLI agent), and the chip pinned at the top of the chat page — hidden on the new-chat page, staying put
/// while the messages scroll, copying on a click.
/// </summary>
internal static partial class Program
{
    static async Task TestChatRef()
    {
        Log("\n## CR — the chat's reference (CX-…): format, parsing, search, the model's line, the pinned chip");
        ChatRefPure();
        await ChatRefSearch();
        await ChatRefTurnLine();
        await ChatRefCliLine();
        await ChatRefChip();
    }

    static void ChatRefPure()
    {
        const string id = "8cf58c2a-1b2c-4d3e-9f00-0123456789ab";
        string r = ChatRef.Of(id);
        Check("CR1 a chat's reference is CX- and the id's first 8 hex digits, lowercase", r == "CX-8cf58c2a", r);
        var many = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid().ToString()).ToList();
        Check("CR1 every reference has the form CX-xxxxxxxx", many.All(g => Regex.IsMatch(ChatRef.Of(g), "^CX-[0-9a-f]{8}$")));
        Check("CR2 it is stable: the same id gives the same reference however it is written",
            ChatRef.Of(id) == ChatRef.Of(id.ToUpperInvariant()) && ChatRef.Of(id) == ChatRef.Of("{" + id + "}") && ChatRef.Of(id) == ChatRef.Of(id.Replace("-", "")));
        Check("CR2 different chats get different references", many.Select(ChatRef.Of).Distinct().Count() >= 499);
        string odd = ChatRef.Of("legacy-import-42");
        Check("CR2 an id that is not a GUID still gets a stable CX-xxxxxxxx", Regex.IsMatch(odd, "^CX-[0-9a-f]{8}$") && odd == ChatRef.Of("legacy-import-42"), odd);
        Check("CR2 no id, no reference", ChatRef.Of(null) == "" && ChatRef.Of(" ") == "");
        var chat = new ChatSession { Id = id };
        Check("CR3 ChatSession.Ref is the reference of its id", chat.Ref == "CX-8cf58c2a", chat.Ref);

        foreach (var typed in new[] { "CX-8cf58c2a", "cx-8CF58C2A", "  CX-8cf58c2a  ", "#CX-8cf58c2a", "# cx-8cf58c2a", "CX 8cf58c2a", "CX_8cf58c2a", "cx:8cf58c2a", "CX8cf58c2a", "8cf58c2a", "8CF58C2A", id, id.ToUpperInvariant() })
            Check($"CR4 \"{typed}\" is read as CX-8cf58c2a", ChatRef.TryParse(typed, out var got) && got == "CX-8cf58c2a", got);
        foreach (var notRef in new[] { null, "", "   ", "CX-", "CX-8cf58c2", "CX-8cf58c2ab", "CX-8cf58c2g", "8cf58c2", "hello", "chat CX-8cf58c2a", "CX-8cf5 8c2a", "XC-8cf58c2a" })
            Check($"CR4 \"{notRef}\" is not a reference", !ChatRef.TryParse(notRef, out var got) && got == "", got);
        Check("CR4 Matches: the chat's own reference in any form, nobody else's",
            ChatRef.Matches(id, "cx-8CF58C2A") && ChatRef.Matches(id, "8cf58c2a") && !ChatRef.Matches(id, "CX-8cf58c2b") && !ChatRef.Matches(null, "CX-8cf58c2a"));

        // The note the model gets: the reference and what to do with it, in one line.
        string note = ChatRef.TurnNote("CX-8cf58c2a");
        Check("CR5 the model's line names the reference and says the user may quote it",
            note.StartsWith(ChatRef.TurnNoteHead) && note.Contains("CX-8cf58c2a") && note.Contains("the user may quote it") && !note.Contains('\n'), note);
        // Only a chat's own run carries it: no run, a scope that is not a chat, a sub-agent → nothing.
        AgentRunScope.Enter(null);
        bool outside = ChatRef.TurnNoteForCurrentRun() == null && ChatRef.WithTurnNote("hi") == "hi";
        var root = new AgentRunScope(id, null);
        AgentRunScope.Enter(root);
        bool inRun = ChatRef.TurnNoteForCurrentRun() == note && ChatRef.WithTurnNote("hi") == "hi\n\n" + note;
        AgentRunScope.Enter(root.CreateChild());
        bool child = ChatRef.TurnNoteForCurrentRun() == null;
        AgentRunScope.Enter(new AgentRunScope("auto-comment", null));
        bool notChat = ChatRef.TurnNoteForCurrentRun() == null;
        AgentRunScope.Enter(null);
        Check("CR5 a chat's run carries the line; no run, a sub-agent or a non-chat run does not",
            outside && inRun && child && notChat, $"outside={outside} run={inRun} child={child} notChat={notChat}");

        // The words on the chip, in both languages (the owner's Thai tip, verbatim).
        Check("CR6 the chip's tip, 'Copied' and the search hit are localized in Thai and English",
            LocalizationService.Lookup("chat.ref.tip", "th") == "รหัสอ้างอิงแชทนี้ — คลิกเพื่อคัดลอก ใช้บอกทีมหรือ AI ว่าหมายถึงแชทไหน"
            && LocalizationService.Lookup("chat.ref.copied", "th") == "คัดลอกแล้ว" && LocalizationService.Lookup("chat.ref.copied", "en") == "Copied"
            && new[] { "chat.ref.copyTip", "chat.ref.copiedStatus", "chat.ref.copyFailed", "sidebar.search.refHit", "sidebar.chat.refLabel" }
                .All(k => LocalizationService.Lookup(k, "th") is { Length: > 0 } th && LocalizationService.Lookup(k, "en") is { Length: > 0 } en && th != en)
            && L("sidebar.search.refHit").Contains("{0}"));
    }

    /// <summary>Typing or pasting a reference into the history search finds that chat FIRST, in any form.</summary>
    static async Task ChatRefSearch()
    {
        // Two stored chats; the one asked for is not the newest, and its words do not contain its reference.
        var wanted = new ChatSession { Title = "cr the one asked for", ProjectPath = Vm.WorkingDirectory ?? "", UpdatedAt = DateTime.Now.AddDays(-2) };
        wanted.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "cr hello" });
        var other = new ChatSession { Title = "cr another chat", ProjectPath = Vm.WorkingDirectory ?? "", UpdatedAt = DateTime.Now };
        other.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "cr hello again " + wanted.Ref });   // mentions it in a message
        Persist.SaveSession(wanted);
        Persist.SaveSession(other);
        Vm.Sessions.Add(wanted);
        Vm.Sessions.Add(other);
        try
        {
            string hex = wanted.Ref[3..];
            foreach (var typed in new[] { wanted.Ref, wanted.Ref.ToLowerInvariant(), hex.ToUpperInvariant(), " #" + wanted.Ref + " " })
            {
                Vm.HistorySearchQuery = "";
                Vm.HistorySearchQuery = typed;
                bool found = await WaitUntil(() => Vm.IsSearchingHistory && Vm.SearchResults.Count > 0, 5000);
                var first = Vm.SearchResults.FirstOrDefault();
                Check($"CR7 searching \"{typed}\" finds that chat first, marked as a reference hit",
                    found && first?.SessionId == wanted.Id && first.Snippet == LocF("sidebar.search.refHit", wanted.Ref)
                    && Vm.SearchResults.Count(r => r.SessionId == wanted.Id) == 1,
                    string.Join(" | ", Vm.SearchResults.Select(r => $"{r.SessionTitle}: {r.Snippet}")));
            }
            Check("CR7 the search result carries the reference for its tip", Vm.SearchResults.FirstOrDefault()?.Ref == wanted.Ref);
        }
        finally
        {
            Vm.HistorySearchQuery = "";
            Vm.Sessions.Remove(wanted);
            Vm.Sessions.Remove(other);
            Persist.DeleteSession(wanted.Id);
            Persist.DeleteSession(other.Id);
        }
    }

    /// <summary>The line reaches the model with the turn: what the (scripted) provider is actually sent.</summary>
    static async Task ChatRefTurnLine()
    {
        await FreshChat();
        int before = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Reply("cr ok"));
        await Send("[CR8] which chat is this?");
        bool idle = await WaitIdle();
        string sent = Fake.Calls.Skip(before).Select(c => c.Input).FirstOrDefault(i => i.Contains("[CR8]")) ?? "";
        string want = ChatRef.TurnNote(Vm.CurrentSession?.Ref ?? "?");
        Check("CR8 the turn sent to the model carries this chat's reference line", idle && sent.Contains(want), sent);
        Check("CR8 …once, after the owner's words; the chat on screen keeps only what the owner typed",
            sent.IndexOf(want, StringComparison.Ordinal) == sent.LastIndexOf(want, StringComparison.Ordinal)
            && sent.IndexOf("[CR8]", StringComparison.Ordinal) < sent.IndexOf(want, StringComparison.Ordinal)
            && Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "[CR8] which chat is this?")
            && !Vm.Messages.Any(m => m.Content.Contains(ChatRef.TurnNoteHead)));
        // (a project inside a git repository also gets its [git status] block before the line: cut from there too)
        string bare = WithoutSkills(sent);
        int git = bare.IndexOf("\n\n[git status", StringComparison.Ordinal);
        if (git >= 0) bare = bare[..git];
        Check("CR8 the harness's strip helper cuts the line off, leaving the owner's words", bare == "[CR8] which chat is this?", bare);
    }

    /// <summary>A CLI agent (Claude Dev, Codex, Gemini …) gets no CluadeX system prompt: the line rides on its message.</summary>
    static async Task ChatRefCliLine()
    {
        var cli = new ScriptedCli();
        var before = Settings.Settings.ActiveProvider;
        string folder = Vm.WorkingDirectory ?? "";
        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.ClaudeDev);
        SetField(Providers, "_activeProvider", cli);
        try
        {
            Vm.SetWorkingDirectory("");
            await FreshChat();
            cli.Reset();
            cli.Turns.Enqueue(new CliTurn([CliResult("cr cli answer")]));
            await Send("[CR9] tell me the chat's reference");
            bool idle = await WaitIdle(20000);
            string sent = cli.Messages.FirstOrDefault() ?? "";
            Check("CR9 a CLI agent's turn carries this chat's reference line too",
                idle && cli.Messages.Count == 1 && sent.Contains(ChatRef.TurnNote(Vm.CurrentSession?.Ref ?? "?")), sent.Length > 600 ? sent[^600..] : sent);
        }
        finally
        {
            Settings.UpdateSettings(s => s.ActiveProvider = before);
            SetField(Providers, "_activeProvider", Fake);
            Vm.SetWorkingDirectory(folder.Length > 0 ? folder : ProjA);
            await FreshChat();
        }
    }

    /// <summary>The chip at the top of the chat page: hidden on the new-chat page, the reference on it, pinned while the
    /// messages scroll, out of the way of the list, a click copies (and says so), the text selectable.</summary>
    static async Task ChatRefChip()
    {
        await FreshChat();
        var bar = (FrameworkElement)View.FindName("ChatRefBar");
        var chip = (FrameworkElement)View.FindName("ChatRefChip");
        var text = (TextBox)View.FindName("ChatRefText");
        var copy = (Button)View.FindName("ChatRefCopyButton");
        if (bar == null || chip == null || text == null || copy == null)
        {
            Check("CR10 the chip is on the chat page", false, $"bar={bar != null} chip={chip != null} text={text != null} copy={copy != null}");
            return;
        }
        Check("CR10 the new-chat page (no rows yet) shows no reference", !chip.IsVisible && Vm.Messages.Count == 0);

        for (int i = 0; i < 50; i++)
            Vm.Messages.Add(new ChatMessage { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = $"cr message {i}\nline two\nline three" });
        await Render();
        string r = Vm.CurrentSession!.Ref;
        Check("CR10 once the chat has rows, the chip shows its reference", chip.IsVisible && text.Text == r && Vm.CurrentChatRef == r, $"visible={chip.IsVisible} text={text.Text} ref={r}");
        Check("CR11 it sits in the page's top row, above the messages — not over them", Grid.GetRow(bar) == 0
            && chip.TransformToAncestor(View).TransformBounds(new Rect(0, 0, chip.ActualWidth, chip.ActualHeight)).Bottom
               <= ChatList.TransformToAncestor(View).Transform(new Point(0, 0)).Y + 0.5);

        var sv = Find<ScrollViewer>(ChatList).First();
        sv.ScrollToTop();
        await Render();
        var at = chip.TransformToAncestor(View).Transform(new Point(0, 0));
        sv.ScrollToEnd();
        await Render();
        var after = chip.TransformToAncestor(View).Transform(new Point(0, 0));
        Check("CR11 it stays put while the messages scroll", sv.VerticalOffset > 100 && at == after && chip.IsVisible, $"offset={sv.VerticalOffset:F0} {at} → {after}");
        Check("CR11 its copy button takes a click (nothing lies over it)", HitReaches(copy, out var chain), chain);
        Check("CR12 the text is selectable: a read-only box, not a label", text.IsReadOnly && text.IsEnabled && text.Focusable);

        // A click copies — the owner's clipboard is put back after.
        string saved = null;
        try { saved = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
        try
        {
            // A real button press (OnClick runs the button's command; a raised Click event alone would not).
            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(copy, null);
            await Render();
            string got = null;
            try { got = Clipboard.GetText(); } catch { }
            Check("CR12 the copy button puts the reference on the clipboard, and the chip says 'Copied'",
                got == r && Vm.ChatRefCopied && Vm.StatusText.Contains(r), $"clipboard={got} copied={Vm.ChatRefCopied} status={Vm.StatusText}");
            Check("CR12 …and the mark goes away by itself", await WaitUntil(() => !Vm.ChatRefCopied, 4000));

            // Another chat: the chip follows it, and a "Copied" left from the last one does not carry over.
            Vm.CopyChatRefCommand.Execute(null);
            await FreshChat();
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "cr second chat" });
            await Render();
            Check("CR13 a new chat shows its own reference, without the last chat's 'Copied'",
                chip.IsVisible && text.Text == Vm.CurrentSession!.Ref && text.Text != r && !Vm.ChatRefCopied, $"{text.Text} vs {r}");
        }
        finally
        {
            if (saved != null) try { Clipboard.SetText(saved); } catch { }
            await FreshChat();
        }
    }
}
