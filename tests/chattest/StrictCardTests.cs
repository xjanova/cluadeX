using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Safety;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>STRICT — the strict-approval card and BrainX's approval card (owner 2026-11-11: "ระบบการขออนุมัติแบบนี้
    /// cluadex มีไหม ฉันอยากให้มีแต่เลือกได้ว่าจะเปิดมันหรือไม่"). Through the app's real wiring (StrictApproval.Handler /
    /// BrainxHandler set by ChatViewModel): the card shows the exact command, where, the change and the code; the
    /// ordinary Allow buttons are gone; a wrong code says how many tries are left; the right code (typed into the card's
    /// box) approves exactly once; Deny refuses; "allow everything" does not answer it. Renders in a dark (Midnight) and the light (Classic)
    /// theme into renders\strict\. Nothing here runs a command. CHATTEST_ONLY=STRICT runs just these.</summary>
    static async Task TestStrictCards()
    {
        Log("\n## STRICT — the strict-approval card and BrainX's approval card");
        var themes = Sp.GetRequiredService<ThemeService>();
        string dir = Path.Combine(AppContext.BaseDirectory, "renders", "strict");
        Directory.CreateDirectory(dir);
        Check("STRICT0 the chat page wired the strict and BrainX handlers", StrictApproval.Handler != null && StrictApproval.BrainxHandler != null);
        if (StrictApproval.Handler == null || StrictApproval.BrainxHandler == null) return;
        string prune = "docker system" + " prune -af --volumes";
        var finding = new HardToUndoFinding(StrictScope.Local, "prune", "docker system" + " prune");

        string Visible(ChatMessage m) => m == null || Container(m) is not { } c ? ""
            : string.Join(" ", Find<TextBlock>(c).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text))
              + " " + string.Join(" ", Find<TextBox>(c).Where(t => t.IsVisible).Select(t => t.Text));
        void Click(ChatMessage m, string id) => ById<Button>(m, id)?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool Shows(ChatMessage m, string id) => ById<FrameworkElement>(m, id) is { IsVisible: true };
        async Task<ChatMessage> Card(Func<ChatMessage, bool> which)
        {
            ChatMessage p = null;
            await WaitUntil(() => (p = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered && which(m))) != null, 5000);
            await Task.Delay(400); await Render();
            if (p != null) { ChatList.ScrollIntoView(p); await Render(); }
            return p;
        }
        Task<bool> Raise(string command, string where) =>
            StrictApproval.ConfirmAsync(StrictApproval.Ledger, StrictApproval.Handler, new StrictRequest("run_command", command, where, finding));

        foreach (var (theme, lang) in new[] { (ThemeIds.Midnight, "th"), (ThemeIds.Classic, "en") })
        {
            themes.Apply(theme, persist: false);
            LocSvc.SetLanguage(lang);
            await FreshChat();
            await Render();
            string tag = $"{theme}-{lang}";

            // S1 — the card: command, where, the change, the code; no ordinary Allow buttons.
            var ask = Raise(prune, "local: D:\\Projects\\shop-app");
            var card = await Card(m => m.IsStrict);
            string text = Visible(card);
            Check($"STRICT1 [{tag}] the strict card shows the exact command, where it runs, the change and a one-time code",
                card != null && Shows(card, "CxGuard.StrictCard") && text.Contains(prune) && text.Contains("D:\\Projects\\shop-app")
                && card.StrictCode.Length == 7 && card.StrictCode[3] == '-' && text.Contains(card.StrictCode)
                && text.Contains(LocalizedResources.Instance["strict.kind.prune"]), text);
            Check($"STRICT1b [{tag}] …the AI-must-explain note is on it, and the ordinary Allow / Always buttons are not",
                card != null && text.Contains(LocalizedResources.Instance["strict.card.modelMustSay"]) && !card.ShowsDefaultPermissionButtons
                && card.ShowsStrictButtons && Shows(card, "CxGuard.StrictApprove") && Shows(card, "CxGuard.StrictDeny") && !Shows(card, "CxGuard.PermissionPlain"));
            SaveShot(Path.Combine(dir, $"{tag}-card.png"));

            // S2 — "allow everything" does not answer it.
            typeof(ChatViewModel).GetMethod("ApprovePendingPrompts", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [Vm.Messages]);
            await Task.Delay(200);
            Check($"STRICT2 [{tag}] switching to 'allow everything' leaves the strict card waiting", card is { PermissionAnswered: false } && !ask.IsCompleted);

            // S3 — a wrong code: the note says how many tries are left, still waiting.
            if (card != null)
            {
                var box = ById<TextBox>(card, "CxGuard.StrictInput");
                if (box != null) box.Text = "ZZZ-ZZZ";
                Click(card, "CxGuard.StrictApprove");
                await Task.Delay(200); await Render();
                SaveShot(Path.Combine(dir, $"{tag}-wrong-code.png"));
                Check($"STRICT3 [{tag}] a wrong code is refused with 'tries left', nothing approved",
                    !ask.IsCompleted && !card.PermissionAnswered && Shows(card, "CxGuard.StrictNote")
                    && card.StrictNote.Contains((StrictApprovalLedger.MaxWrongTries - 1).ToString()), card.StrictNote);

                // S4 — the right code typed into the card (lower case, no dash, as people type it) approves.
                if (box != null) box.Text = card.StrictCode.Replace("-", "").ToLowerInvariant();
                Click(card, "CxGuard.StrictApprove");
                bool done = await Task.WhenAny(ask, Task.Delay(5000)) == ask && ask.Result;
                await Render();
                SaveShot(Path.Combine(dir, $"{tag}-approved.png"));
                Check($"STRICT4 [{tag}] the right code typed on the card approves this one command", done && card.PermissionAnswered, $"done={done}");
            }

            // S5 — Deny refuses.
            var ask2 = Raise("git" + " clean -fdx", "local: D:\\Projects\\shop-app");
            var card2 = await Card(m => m.IsStrict);
            if (card2 != null) Click(card2, "CxGuard.StrictDeny");
            bool denied = await Task.WhenAny(ask2, Task.Delay(5000)) == ask2 && !ask2.Result;
            Check($"STRICT5 [{tag}] Deny on the strict card refuses (nothing runs)", denied && card2 is { PermissionAnswered: true });

            // S6 — BrainX's needs_confirmation: the command, the profile, the reasons; no ordinary Allow buttons.
            var bx = new BrainxConfirmation("y9pz5gdw6j", prune, "prod-251", ["prunes Docker volumes"], null);
            var bxAsk = StrictApproval.BrainxHandler(bx);
            var bxCard = await Card(m => m.IsBrainxConfirm);
            string bxText = Visible(bxCard);
            SaveShot(Path.Combine(dir, $"{tag}-brainx.png"));
            Check($"STRICT6 [{tag}] BrainX's card shows the command, the server profile and why, with its own buttons",
                bxCard != null && Shows(bxCard, "CxGuard.BrainxCard") && bxText.Contains(prune) && bxText.Contains("prod-251")
                && bxText.Contains("prunes Docker volumes") && !bxCard.ShowsDefaultPermissionButtons && Shows(bxCard, "CxGuard.BrainxDone")
                && Shows(bxCard, "CxGuard.BrainxDeny") && !Shows(bxCard, "CxGuard.PermissionPlain"), bxText);
            Check($"STRICT6b [{tag}] …nothing on it opens or approves by itself (it waits for the click)", !bxAsk.IsCompleted);
            if (bxCard != null) Click(bxCard, "CxGuard.BrainxDeny");
            bool bxDenied = await Task.WhenAny(bxAsk, Task.Delay(5000)) == bxAsk && !bxAsk.Result;
            Check($"STRICT6c [{tag}] Deny on BrainX's card answers no", bxDenied);
        }
        themes.Apply(ThemeIds.Midnight, persist: false);
        LocSvc.SetLanguage("th");
    }
}
