using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace PagesTest;

/// <summary>
/// MT — the pages that are not on the activity bar are one click away again (owner 2026-11-07: "ส่วนจัดการ skill ยังไม่เห็น
/// เมนู"). Settings ▸ More tools was their only button and went with the "dead options" on 2026-10-16, so Skills,
/// Subagents, Plugins, Hooks … could be reached only by Ctrl+K or a Ctrl+number key. On the shipping MainWindow: the rail's
/// ⋯ button and its menu, and the Settings card, both from MainViewModel.MoreToolPages.
/// </summary>
internal static partial class Program
{
    static void ClickToggle(ButtonBase b) =>
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, null);

    static async Task MoreToolsChecks(LocalizationService loc)
    {
        Log("\n## MT — the pages off the activity bar: the rail's ⋯ menu and Settings ▸ More tools");
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);
        var targets = MainViewModel.MoreToolTargets;

        // MT1 — every one of them is a real page, its words in Thai (a missing key renders as the key itself)
        var badPages = targets.Where(t =>
        {
            Invoke(MainVm, "NavigateTo", t.Target);
            return MainVm.SelectedNavItem != t.Target || MainVm.CurrentView == null;
        }).Select(t => t.Target).ToList();
        Check("MT1 every page in the list opens (NavigateTo knows it)", badPages.Count == 0, string.Join(", ", badPages));
        var untranslated = targets.SelectMany(t => new[] { t.TitleKey, t.TipKey })
            .Where(k => { string th = loc.T(k); return th == k || string.IsNullOrWhiteSpace(th); }).ToList();
        Check("MT1 …and every title and tooltip has its text (no raw key)", untranslated.Count == 0, string.Join(", ", untranslated));
        var rail = Find<RadioButton>(MainWin).Where(r => r.GroupName == "Activity").Select(r => r.CommandParameter?.ToString()).ToList();
        var onRail = targets.Where(t => rail.Contains(t.Target)).Select(t => t.Target).ToList();
        Check("MT1 none of them is on the rail already (the list is what the rail lacks)", onRail.Count == 0, string.Join(", ", onRail));
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);

        // MT2 — the ⋯ button is on the rail, unlit on Chat; a click opens its menu beside it
        var more = (RadioButton)MainWin.FindName("MoreToolsButton");
        var menu = (ContextMenu)MainWin.FindName("MoreToolsMenu");
        Check("MT2 the rail has a ⋯ button with its menu, in a group of its own", more != null && menu != null && more.GroupName == "MoreTools");
        if (more == null || menu == null) return;
        Check("MT2 …unlit while the chat is on screen", more.IsChecked == false && more.IsVisible, $"checked={more.IsChecked} visible={more.IsVisible}");
        ClickToggle(more);
        await Idle(200);
        var items = menu.Items.Cast<object>().Select(i => menu.ItemContainerGenerator.ContainerFromItem(i) as MenuItem).ToList();
        var pages = menu.Items.Cast<MainViewModel.ToolPage>().ToList();
        Check("MT2 a click opens the menu beside the button, one item per page, Skill first, in Thai",
            menu.IsOpen && ReferenceEquals(menu.PlacementTarget, more) && menu.Placement == PlacementMode.Right
            && items.Count == targets.Length && items.All(i => i != null) && pages[0].Target == "Skills" && pages[0].Title == loc.T("nav.skills"),
            $"open={menu.IsOpen} items={items.Count} first={pages.FirstOrDefault()?.Title}");
        var skillItem = items.FirstOrDefault();
        var shown = skillItem != null ? Find<TextBlock>(skillItem).Select(t => t.Text).ToList() : new();
        Check("MT2 …each item shows its icon and its name", shown.Contains(pages[0].Glyph) && shown.Contains(pages[0].Title), string.Join(" | ", shown));

        // MT3 — choosing Skill opens the Skills page; the ⋯ button is lit, the rail's page buttons are not
        if (skillItem != null)
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.MenuItemAutomationPeer(skillItem)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Idle(300);
        bool anyRailLit = Find<RadioButton>(MainWin).Any(r => r.GroupName == "Activity" && r.IsChecked == true);
        Check("MT3 Skill in the menu opens the Skills page, the menu closes",
            MainVm.SelectedNavItem == "Skills" && MainVm.CurrentView is SkillsViewModel && !menu.IsOpen,
            $"{MainVm.SelectedNavItem} · open={menu.IsOpen}");
        Check("MT3 …the ⋯ button is lit and no rail page button is", more.IsChecked == true && !anyRailLit, $"more={more.IsChecked} railLit={anyRailLit}");

        // MT4 — back on Chat it goes out; opening the menu and closing it without a choice leaves it out
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);
        Check("MT4 back on the chat, the ⋯ button goes out", more.IsChecked == false, $"{more.IsChecked}");
        ClickToggle(more);
        await Idle(150);
        bool litWhileOpen = more.IsChecked == true;
        menu.IsOpen = false;
        await Idle(150);
        Check("MT4 a menu closed with no choice puts it back to unlit (the page did not change)",
            more.IsChecked == false && MainVm.SelectedNavItem == "Chat", $"while open={litWhileOpen} after={more.IsChecked}");

        // MT5 — Settings ▸ General ▸ More tools: the same pages as buttons
        Invoke(MainVm, "NavigateTo", "Settings");
        await Idle(400);
        var card = await WaitFor(() => Find<Border>(MainWin).FirstOrDefault(b => b.Name == "MoreToolsCard"));
        var buttons = card != null ? Find<Button>(card).ToList() : new();
        var labels = buttons.Select(b => string.Join(" ", Find<TextBlock>(b).Select(t => t.Text))).ToList();
        Check("MT5 Settings has the More tools card, one button per page, each with its name",
            card != null && buttons.Count == targets.Length && targets.All(t => labels.Any(l => l.Contains(loc.T(t.TitleKey)))),
            $"card={card != null} buttons={buttons.Count} · {string.Join(" | ", labels)}");
        var skillBtn = buttons.FirstOrDefault(b => b.CommandParameter?.ToString() == "Skills");
        if (skillBtn != null)
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(skillBtn)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Idle(300);
        Check("MT5 …its Skill button opens the Skills page", MainVm.SelectedNavItem == "Skills" && MainVm.CurrentView is SkillsViewModel, MainVm.SelectedNavItem);

        // MT6 — a language switch renames them (the list is read again, not a snapshot)
        loc.SetLanguage("en");
        await Idle(200);
        string enFirst = MainVm.MoreToolPages[0].Title;
        loc.SetLanguage("th");
        await Idle(200);
        Check("MT6 the names follow the language", enFirst == "Skills" && MainVm.MoreToolPages[0].Title == loc.T("nav.skills") && enFirst != MainVm.MoreToolPages[0].Title,
            $"en={enFirst} th={MainVm.MoreToolPages[0].Title}");
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);
    }
}
