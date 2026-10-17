using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopTest;

/// <summary>
/// The harness's OWN test window, on its own STA thread: a button, a text box, a password box, a list, a check box,
/// a tab control, a list whose rows hold a child button (the WPF sidebar shape), a rich text box (no Value pattern —
/// key input), and a button whose AutomationId marks it as CluadeX approval UI. The tools never touch any other
/// window: every call names this window's title, pid or handle.
/// </summary>
internal sealed class Fixture
{
    public readonly string Title = "CX Desktop Test " + Guid.NewGuid().ToString("N")[..8];
    public Window Win = null!;
    public Dispatcher D = null!;
    public IntPtr Handle;
    public TextBlock Counter = null!;
    public TextBox Name = null!;
    public PasswordBox Pwd = null!;
    public ListBox List = null!;
    public ListBox Rows = null!;
    public CheckBox Chk = null!;
    public TabControl Tabs = null!;
    public RichTextBox Rich = null!;
    public StackPanel Panel = null!;
    public int Presses, GuardPresses, RowPresses;

    public static Fixture Start(int x = 120, int y = 120)
    {
        var f = new Fixture();
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            f.Build(x, y);
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "fixture-ui" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(20))) throw new Exception("fixture window did not start");
        return f;
    }

    private void Build(int x, int y)
    {
        D = Dispatcher.CurrentDispatcher;
        Panel = new StackPanel { Margin = new Thickness(8) };
        var press = new Button { Content = "Press Me", Margin = new Thickness(2) };
        AutomationProperties.SetAutomationId(press, "BtnPress");
        press.Click += (_, _) => { Presses++; Counter.Text = "Pressed " + Presses; };
        Counter = new TextBlock { Text = "Pressed 0" };
        AutomationProperties.SetAutomationId(Counter, "LblCounter");
        Name = new TextBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(Name, "TxtName");
        AutomationProperties.SetName(Name, "Your name");
        Pwd = new PasswordBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(Pwd, "PwdSecret");
        AutomationProperties.SetName(Pwd, "Secret");
        List = new ListBox { Height = 60 };
        foreach (var s in new[] { "Alpha", "Beta", "Gamma" }) List.Items.Add(new ListBoxItem { Content = s });
        AutomationProperties.SetAutomationId(List, "LstItems");
        Chk = new CheckBox { Content = "Enable thing" };
        AutomationProperties.SetAutomationId(Chk, "ChkEnable");
        Tabs = new TabControl { Height = 50 };
        Tabs.Items.Add(new TabItem { Header = "General", Content = new TextBlock { Text = "general page" } });
        Tabs.Items.Add(new TabItem { Header = "Advanced", Content = new TextBlock { Text = "advanced page" } });
        AutomationProperties.SetAutomationId(Tabs, "Tabs");
        Rows = new ListBox { Height = 60 };
        AutomationProperties.SetAutomationId(Rows, "LstRows");
        for (int i = 1; i <= 2; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Row " + i, Width = 80 });
            var open = new Button { Content = "Open " + i };
            open.Click += (_, _) => RowPresses++;
            row.Children.Add(open);
            Rows.Items.Add(new ListBoxItem { Content = row });
        }
        Rich = new RichTextBox { Height = 40 };
        AutomationProperties.SetAutomationId(Rich, "RichNotes");
        var guard = new Button { Content = "Allow" };
        AutomationProperties.SetAutomationId(guard, "CxGuard.PermissionAllow");
        guard.Click += (_, _) => GuardPresses++;
        foreach (UIElement e in new UIElement[] { press, Counter, Name, Pwd, List, Chk, Tabs, Rows, Rich, guard }) Panel.Children.Add(e);
        Win = new Window
        {
            Title = Title, Width = 520, Height = 620, Left = x, Top = y, Content = Panel, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = true,
            Background = System.Windows.Media.Brushes.White,
        };
        Win.Show();
        Handle = new WindowInteropHelper(Win).Handle;
    }

    public T On<T>(Func<T> f) => D.Invoke(f);
    public void On(Action a) => D.Invoke(a);

    /// <summary>Another window on this fixture's thread (the credential-title case, the Late button…).</summary>
    public Window Extra(string title)
    {
        return D.Invoke(() =>
        {
            var w = new Window { Title = title, Width = 300, Height = 160, Left = 700, Top = 120, Topmost = true, ShowActivated = false,
                Content = new Button { Content = "Inside" } };
            w.Show();
            return w;
        });
    }

    public void Close() { try { D.Invoke(() => Win.Close()); D.InvokeShutdown(); } catch { } }
}
