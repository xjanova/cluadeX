using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace AnimTest;

/// <summary>
/// The sidebar's "all projects" button in the shipping MainWindow: pressed the way a user presses it (the control's own
/// toggle), then read from what is on screen, not from the view model alone. Then what opening a chat costs.
/// </summary>
internal static class SidebarChecks
{
    const string ProjectA = @"D:\Code\AlphaProject", ProjectB = @"D:\Code\BetaProject", ProjectC = @"E:\Other\Gamma";

    public static async Task Run(Window win, MainViewModel vm, SettingsService settings, Action<string, bool, string> check)
    {
        var chat = vm.ChatVM;
        chat.ShowAllProjectSessions = false;
        chat.WorkingDirectory = "";
        chat.Sessions.Clear();
        await Settle();

        var now = DateTime.Now;
        chat.Sessions.Add(new ChatSession { Title = "alpha today", ProjectPath = ProjectA, UpdatedAt = now });
        chat.Sessions.Add(new ChatSession { Title = "alpha last month", ProjectPath = ProjectA, UpdatedAt = now.AddDays(-30) });
        chat.Sessions.Add(new ChatSession { Title = "beta yesterday", ProjectPath = ProjectB, UpdatedAt = now.AddDays(-1) });
        chat.Sessions.Add(new ChatSession { Title = "beta last month", ProjectPath = ProjectB, UpdatedAt = now.AddDays(-31) });
        chat.Sessions.Add(new ChatSession { Title = "gamma last year", ProjectPath = ProjectC, UpdatedAt = now.AddDays(-300) });
        await Settle();

        var sidebar = (FrameworkElement)win.FindName("ChatSidebar");
        var toggle = Toggle(win, "ChatVM.ShowAllProjectSessions");
        var awayToggle = Toggle(win, "ChatVM.ShowArchivedSplits");
        check("SB the all-projects button is on the page", toggle != null && awayToggle != null, "");
        if (toggle == null || awayToggle == null) return;
        var icon = Find<TextBlock>(toggle).First();
        var awayIcon = Find<TextBlock>(awayToggle).First();

        // A project open: the list is that project's, and the button widens it.
        chat.WorkingDirectory = ProjectA;
        await Settle();
        var offColour = Colour(icon);
        check("SB with a project open only its chats show", Rows(sidebar).SetEquals(["alpha today", "alpha last month"]), Join(Rows(sidebar)));
        check("SB the button can be pressed there", toggle.IsEnabled, "");
        check("SB its tooltip says how many more there are", Tip(toggle).Contains('3'), Tip(toggle));
        check("SB the line above the list says whose chats these are", ShowsText(sidebar, "AlphaProject") && ShowsText(sidebar, "2") && ShowsText(sidebar, "5"), chat.SidebarFilterText);
        Shot(sidebar, "sidebar-1-project.png");

        Press(toggle);
        await Settle();
        check("SB pressed: every project's chats are on screen", Rows(sidebar).Count == 5, Join(Rows(sidebar)));
        check("SB pressed: the button shows it is on", !Colour(icon).Equals(offColour), $"{offColour} → {Colour(icon)}");
        check("SB pressed: the line above the list changes", !chat.SidebarFilterText.Contains("AlphaProject") && ShowsText(sidebar, "5"), chat.SidebarFilterText);
        check("SB pressed: the tooltip offers the way back", Tip(toggle).Contains("AlphaProject"), Tip(toggle));
        Shot(sidebar, "sidebar-2-all.png");

        Press(toggle);
        await Settle();
        check("SB pressed again: back to the project's chats", Rows(sidebar).Count == 2, Join(Rows(sidebar)));
        check("SB pressed again: the button goes back", Colour(icon).Equals(offColour), $"{Colour(icon)}");

        var awayOff = Colour(awayIcon);
        Press(awayToggle);
        await Settle();
        check("SB the put-away button shows it is on too", !Colour(awayIcon).Equals(awayOff), $"{awayOff} → {Colour(awayIcon)}");
        Press(awayToggle);
        await Settle();

        // No project open: every chat already shows, so the button says that instead of pretending to work.
        chat.WorkingDirectory = "";
        await Settle();
        check("SB no project open: every chat shows", Rows(sidebar).Count == 5, Join(Rows(sidebar)));
        check("SB no project open: the button is off, not dead", !toggle.IsEnabled && ToolTipService.GetShowOnDisabled(toggle) && Tip(toggle).Length > 0, Tip(toggle));
        Shot(sidebar, "sidebar-3-none.png");

        // Pressed on, then no project, then a project again: the choice stands, and the button still shows it.
        chat.WorkingDirectory = ProjectA;
        await Settle();
        Press(toggle);
        chat.WorkingDirectory = "";
        await Settle();
        chat.WorkingDirectory = ProjectB;
        await Settle();
        check("SB the choice survives a change of project", Rows(sidebar).Count == 5 && !Colour(icon).Equals(offColour), Join(Rows(sidebar)));
        Press(toggle);
        await Settle();

        // The list is searched in its own place, and comes back afterwards.
        chat.HistorySearchQuery = "zzz-nothing";
        await Task.Delay(500);
        await Settle();
        check("SB searching hides the list and the line", Rows(sidebar).Count == 0 && !ShowsText(sidebar, chat.SidebarFilterText), Join(Rows(sidebar)));
        chat.ClearSearchCommand.Execute(null);
        await Settle();
        check("SB clearing the search brings the list back", Rows(sidebar).SetEquals(["beta yesterday", "beta last month"]), Join(Rows(sidebar)));

        // A long history: only what is in view gets built, the wheel still walks it, the last chat can be reached.
        for (int i = 0; i < 195; i++)
            chat.Sessions.Add(new ChatSession { Title = "old " + i, ProjectPath = ProjectC, UpdatedAt = now.AddDays(-40 - i) });
        await Settle();
        var clock = Stopwatch.StartNew();
        Press(toggle);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        clock.Stop();
        await Settle();
        check("SB 200 chats: the list counts them all", chat.VisibleSessions.Count == 200, $"{chat.VisibleSessions.Count}");
        check("SB 200 chats: only the rows in view are built", Rows(sidebar).Count < 40, $"{Rows(sidebar).Count} rows");
        check("SB 200 chats: the press takes well under a second", clock.ElapsedMilliseconds < 700, $"{clock.ElapsedMilliseconds} ms");

        var list = ChatList(sidebar);
        var firstRow = Find<Button>(list).First(b => b.DataContext is ChatSession);
        firstRow.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
        await Settle();
        check("SB the wheel over a row scrolls the list", list.VerticalOffset > 0, $"offset {list.VerticalOffset:0}");
        list.ScrollToEnd();
        await Settle();
        list.ScrollToEnd();
        await Settle();
        check("SB the oldest chat can be reached", Rows(sidebar).Contains("gamma last year") || Rows(sidebar).Contains("old 194"), Join(Rows(sidebar).TakeLast(3)));

        // Opening a chat swaps its row for the full copy; a run lifts a chat to the top. The list stays where it was read.
        double offset = list.VerticalOffset;
        var near = chat.VisibleSessions[^1];
        chat.Sessions[chat.Sessions.IndexOf(near)] = new ChatSession { Id = near.Id, Title = near.Title, ProjectPath = near.ProjectPath, UpdatedAt = near.UpdatedAt };
        await Settle();
        check("SB opening a chat low in the list leaves the list where it is", Math.Abs(list.VerticalOffset - offset) < 1 && Rows(sidebar).Contains(near.Title), $"{offset:0} → {list.VerticalOffset:0}");
        chat.Sessions.Move(chat.Sessions.Count - 1, 0);
        await Settle();
        check("SB a chat lifted to the top leaves it there too", Math.Abs(list.VerticalOffset - offset) < 100, $"{offset:0} → {list.VerticalOffset:0}");
        chat.Sessions.Move(0, chat.Sessions.Count - 1);
        await Settle();
        check("SB the list keeps the order it was given", chat.VisibleSessions.SequenceEqual(chat.Sessions.OrderBy(s => chat.VisibleSessions.IndexOf(s))) && chat.VisibleSessions.Count == 200, "");

        list.ScrollToHome();
        await Settle();
        check("SB and back to the newest, the rows redrawn with their own titles", Rows(sidebar).Contains("alpha today"), Join(Rows(sidebar).Take(3)));
        Press(toggle);
        await Settle();

        // Day groups keep their order however the list is reached: a filtered list with only old chats, then every project.
        chat.Sessions.Clear();
        chat.Sessions.Add(new ChatSession { Title = "new here", ProjectPath = ProjectA, UpdatedAt = now });
        chat.Sessions.Add(new ChatSession { Title = "old there", ProjectPath = ProjectB, UpdatedAt = now.AddDays(-60) });
        chat.WorkingDirectory = ProjectB;
        await Settle();
        Press(toggle);
        await Settle();
        var groups = CollectionViewSource.GetDefaultView(chat.VisibleSessions).Groups.Cast<CollectionViewGroup>().Select(g => (string)g.Name).ToList();
        check("SB today's group stays above the older one", groups.Count == 2 && groups[0] == chat.VisibleSessions[0].DateGroupLabel && chat.VisibleSessions[0].Title == "new here", Join(groups));

        // A chat that moved to another day joins that day's group.
        var moved = chat.VisibleSessions[1];
        moved.UpdatedAt = now;
        chat.Sessions.Add(new ChatSession { Title = "any change", ProjectPath = ProjectC, UpdatedAt = now });
        await Settle();
        groups = CollectionViewSource.GetDefaultView(chat.VisibleSessions).Groups.Cast<CollectionViewGroup>().Select(g => (string)g.Name).ToList();
        check("SB a chat that moved to today joins today's group", groups.Count == 1, Join(groups));
        Press(toggle);
        chat.Sessions.Clear();
        await Settle();

        await ModelScan(vm, settings, check);
    }

    /// <summary>Opening a chat asks for the Chat page it is already on: that must cost nothing. Really coming back to the
    /// page still picks up new models, off the UI thread.</summary>
    static async Task ModelScan(MainViewModel vm, SettingsService settings, Action<string, bool, string> check)
    {
        string dir = Path.Combine(settings.DataRoot, "scan-models", "deep", "er");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "first.gguf"), [0]);
        settings.UpdateSettings(s => s.ModelDirectory = Path.Combine(settings.DataRoot, "scan-models"));

        var chat = vm.ChatVM;
        vm.NavigateToCommand.Execute("Skills");
        await Settle();
        int before = chat.LocalModels.Count;
        vm.NavigateToCommand.Execute("Chat");
        check("SB coming back to Chat does not wait for the scan", chat.LocalModels.Count == before, $"{chat.LocalModels.Count}");
        await Until(() => chat.LocalModels.Count == 1);
        check("SB and the scan's answer lands", chat.LocalModels.Count == 1, $"{chat.LocalModels.Count}");

        File.WriteAllBytes(Path.Combine(dir, "second.gguf"), [0]);
        vm.NavigateToCommand.Execute("Chat");
        await Task.Delay(800);
        check("SB asking for the page it is on scans nothing", chat.LocalModels.Count == 1, $"{chat.LocalModels.Count}");

        vm.NavigateToCommand.Execute("Skills");
        await Settle();
        vm.NavigateToCommand.Execute("Chat");
        await Until(() => chat.LocalModels.Count == 2);
        check("SB really coming back finds the new model", chat.LocalModels.Count == 2, $"{chat.LocalModels.Count}");

        var kept = chat.LocalModels[0];
        vm.NavigateToCommand.Execute("Skills");
        await Settle();
        vm.NavigateToCommand.Execute("Chat");
        await Task.Delay(800);
        check("SB a scan that finds nothing new leaves the model box alone", chat.LocalModels.Count == 2 && ReferenceEquals(chat.LocalModels[0], kept), "");
    }

    /// <summary>The list a real chat store gives (--db &lt;copy of codex.db&gt;): how long the button takes, whether the oldest
    /// chat can be reached and opened. Prints what it finds; nothing here depends on one owner's data.</summary>
    public static async Task RealList(Window win, MainViewModel vm)
    {
        var chat = vm.ChatVM;
        var sidebar = (FrameworkElement)win.FindName("ChatSidebar");
        var toggle = Toggle(win, "ChatVM.ShowAllProjectSessions");
        await Settle();
        Console.WriteLine($"INFO store: {chat.Sessions.Count} chats · folder '{chat.WorkingDirectory}' · list {chat.VisibleSessions.Count} · built {Rows(sidebar).Count}");
        Console.WriteLine($"INFO line: {chat.SidebarFilterText} · tip: {Tip(toggle)}");
        Shot(sidebar, "real-1-before.png");

        for (int round = 1; round <= 3; round++)
        {
            var clock = Stopwatch.StartNew();
            Press(toggle);
            long pressed = clock.ElapsedMilliseconds;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            long laidOut = clock.ElapsedMilliseconds;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"INFO press {round}: rebuild {pressed} · layout@{laidOut} · idle@{clock.ElapsedMilliseconds} ms · list {chat.VisibleSessions.Count} · built {Rows(sidebar).Count} · {chat.SidebarFilterText}");
            await Task.Delay(1500);
            if (round == 1) Shot(sidebar, "real-2-all.png");
        }

        var list = ChatList(sidebar);
        list.ScrollToEnd();
        await Settle();
        list.ScrollToEnd();
        await Settle();
        var oldest = chat.Sessions.OrderBy(s => s.UpdatedAt).First();
        Console.WriteLine($"INFO list: viewport {list.ViewportHeight:0} · content {list.ExtentHeight:0} · at {list.VerticalOffset:0} of {list.ScrollableHeight:0}");
        Console.WriteLine($"INFO oldest chat drawn: {Rows(sidebar).Contains(oldest.Title)} ({oldest.UpdatedAt:yyyy-MM-dd})");
        Shot(sidebar, "real-3-end.png");

        var open = Stopwatch.StartNew();
        chat.LoadSessionCommand.Execute(oldest.Id);
        await Until(() => chat.CurrentSession?.Id == oldest.Id);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine($"INFO opened the oldest chat: {open.ElapsedMilliseconds} ms · {chat.Messages.Count} rows · folder '{chat.WorkingDirectory}' · list {chat.VisibleSessions.Count}");
        Console.WriteLine($"INFO line: {chat.SidebarFilterText} · tip: {Tip(toggle)}");
        await Settle();
        Shot(sidebar, "real-4-oldest.png");

        // The same few chats again: what a switch costs once the row templates are warm.
        var some = chat.Sessions.OrderByDescending(s => s.UpdatedAt).Take(4).Append(oldest).ToList();
        foreach (var s in some.Concat(some))
        {
            if (chat.CurrentSession?.Id == s.Id) continue;
            var clock = Stopwatch.StartNew();
            chat.LoadSessionCommand.Execute(s.Id);
            await Until(() => chat.CurrentSession?.Id == s.Id);
            long swapped = clock.ElapsedMilliseconds;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            Console.WriteLine($"INFO switch: logic {swapped} · layout@{clock.ElapsedMilliseconds} ms · {chat.Messages.Count} rows");
            await Task.Delay(1000);
        }
    }

    static async Task Settle()
    {
        await Task.Delay(250);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static async Task Until(Func<bool> done)
    {
        var wait = Stopwatch.StartNew();
        while (!done() && wait.ElapsedMilliseconds < 8000) await Task.Delay(40);
    }

    static void Press(ToggleButton button) =>
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(button)).Toggle();

    static ToggleButton Toggle(DependencyObject root, string path) =>
        Find<ToggleButton>(root).FirstOrDefault(t => BindingOperations.GetBinding(t, ToggleButton.IsCheckedProperty)?.Path.Path == path);

    static string Tip(ToggleButton button) => button.ToolTip as string ?? "";

    static ScrollViewer ChatList(FrameworkElement sidebar) =>
        Find<ScrollViewer>(sidebar).First(s => Find<ContentPresenter>(s).Any(c => c.DataContext is ChatSession));

    /// <summary>The chat titles actually drawn in the list.</summary>
    static HashSet<string> Rows(FrameworkElement sidebar) =>
        Find<ContentPresenter>(sidebar).Where(c => c.DataContext is ChatSession && c.IsVisible)
            .Select(c => ((ChatSession)c.DataContext).Title).ToHashSet();

    static bool ShowsText(FrameworkElement root, string part) =>
        part.Length > 0 && Find<TextBlock>(root).Any(t => t.IsVisible && t.Text.Contains(part));

    static string Join(IEnumerable<string> rows) => string.Join(" · ", rows);

    static Color Colour(TextBlock text) => text.Foreground is SolidColorBrush b ? b.Color : Colors.Transparent;

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Find<T>(child)) yield return deeper;
        }
    }

    static void Shot(FrameworkElement element, string name)
    {
        var page = new DrawingVisual();
        using (var dc = page.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var bmp = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(page);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        png.Save(file);
    }
}
