using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AnimTest;

/// <summary>
/// What the shipping MainWindow costs while nothing happens: CluadeX's own DI graph builds it off-screen with a long
/// chat list, then this counts the endless storyboards running on elements nobody can see and measures the CPU the
/// idle process takes. No network, no model, no ~/.cluadex (portable.txt → &lt;exe&gt;\Data, wiped every run).
/// </summary>
internal static class Program
{
    static int Pass, Fail, Uncapped;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    }

    [STAThread]
    static int Main()
    {
        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Console.WriteLine("FATAL " + ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        Environment.Exit(rc);
        return rc;
    }

    static async Task<int> RunAll()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))) { Console.WriteLine("FATAL portable.txt is missing"); return 2; }
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        var sp = services.BuildServiceProvider();
        var settings = sp.GetRequiredService<SettingsService>();
        if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FATAL not portable"); return 2; }
        Directory.CreateDirectory(settings.DataRoot);
        settings.UpdateSettings(s => { s.ActiveProvider = AiProviderType.Local; s.SelectedModelPath = null; s.SelectedModelName = null; s.Language = "th"; });
        var loc = sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("th");

        var win = sp.GetRequiredService<CluadeX.MainWindow>();
        var vm = (MainViewModel)win.DataContext;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000; win.Top = -20000; win.Width = 1440; win.Height = 900;
        win.ShowActivated = false; win.ShowInTaskbar = false;
        win.Show();
        var wait = Stopwatch.StartNew();
        while (vm.ChatVM.CurrentSession == null && wait.ElapsedMilliseconds < 20000) await Task.Delay(60);
        await Task.Delay(1500);

        Dump();
        var empty = Count(win);
        Console.WriteLine($"few chats ({vm.ChatVM.VisibleSessions.Count}): {empty.Running} running, {empty.Hidden} of them hidden");
        Console.WriteLine($"  idle: {await Cpu()}");

        for (int i = 0; i < 200; i++) vm.ChatVM.Sessions.Add(new ChatSession { Title = "chat " + i });
        vm.ChatVM.ShowAllProjectSessions = true;
        await Task.Delay(2500);
        Dump();
        var many = Count(win);
        Console.WriteLine($"many chats ({vm.ChatVM.VisibleSessions.Count}): {many.Running} running, {many.Hidden} of them hidden");
        foreach (var g in many.HiddenKinds.GroupBy(k => k).OrderByDescending(g => g.Count())) Console.WriteLine($"  {g.Count(),4} {g.Key}");
        Console.WriteLine($"  idle: {await Cpu()}");

        Check("no endless storyboard runs on an element nobody can see", many.Hidden == 0, $"{many.Hidden} hidden");
        Check("no clock runs uncapped while the app sits idle", Uncapped == 0, $"{Uncapped} uncapped");
        Check("the storyboards can be paused at all (they are found by name)", many.Paused > 0, $"{many.Paused} paused");

        // A row that starts working shows its spinner turning, and it stops again with the run.
        var row = vm.ChatVM.VisibleSessions[^1];
        row.RunStatus = "working";
        row.RunStep = "working on it";
        await Task.Delay(400);
        var working = Count(win);
        Check("a working chat's row animates", working.Running >= 2, $"{working.Running} running");
        Check("and nothing hidden wakes up with it", working.Hidden == 0, $"{working.Hidden} hidden");
        row.RunStatus = "waiting";
        await Task.Delay(400);
        var waiting = Count(win);
        Check("a waiting chat's row animates its ripple", waiting.Running >= 1, $"{waiting.Running} running");
        row.RunStatus = ""; row.RunStep = "";
        await Task.Delay(400);
        var after = Count(win);
        Check("it stops with the run", after.Running == many.Running, $"{many.Running} → {after.Running} running");
        vm.ChatVM.VisibleSessions.Clear();
        await Task.Delay(400);
        int left = AllClocks().Count(c => c.CurrentState != ClockState.Stopped && Timeline.GetDesiredFrameRate(c.Timeline) == 30);
        Check("rows taken off the list leave no clock behind", left == 0, $"{left} left");

        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        return Fail == 0 ? 0 : 1;
    }

    /// <summary>Everything that asks WPF for frames, read from WPF itself: the clocks under the TimeManager and the
    /// CompositionTarget.Rendering subscribers.</summary>
    static void Dump()
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var pc = typeof(UIElement).Assembly;
        var mc = pc.GetType("System.Windows.Media.MediaContext")!.GetProperty("CurrentMediaContext", Any)!.GetValue(null)!;
        var tm = mc.GetType().GetProperty("TimeManager", Any)!.GetValue(mc)!;
        var root = (Clock)tm.GetType().GetField("_timeManagerClock", Any)!.GetValue(tm)!;
        var lines = new List<string>();
        void Walk(Clock c, int depth)
        {
            if (depth == 1 && c.Timeline != null)
            {
                string first = c.Timeline is ParallelTimeline p && p.Children.Count > 0 ? p.Children[0].GetType().Name + " " + Storyboard.GetTargetName(p.Children[0]) + " " + Storyboard.GetTargetProperty(p.Children[0])?.Path : "";
                lines.Add($"{c.Timeline.GetType().Name} rep={c.Timeline.RepeatBehavior} dur={c.Timeline.Duration} fps={Timeline.GetDesiredFrameRate(c.Timeline)} state={c.CurrentState} paused={c.IsPaused} {first}");
            }
            if (c is ClockGroup g) foreach (var k in g.Children) Walk(k, depth + 1);
            if (depth == 0 && typeof(ClockGroup).GetField("_rootChildren", Any)?.GetValue(c) is System.Collections.IEnumerable weak)
                foreach (var w in weak) if (w is WeakReference { Target: Clock live }) Walk(live, 1);
        }
        Walk(root, 0);
        Uncapped = lines.Count(l => l.Contains("fps= ") && l.Contains("state=Active") && l.Contains("paused=False"));
        Console.WriteLine($"clocks: {lines.Count}, uncapped and running: {Uncapped}");
        foreach (var g in lines.GroupBy(l => l).OrderByDescending(g => g.Count()).Take(30)) Console.WriteLine($"  {g.Count(),3} {g.Key}");
        foreach (var f in mc.GetType().GetFields(Any))
            if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(mc) is Delegate d)
                foreach (var h in d.GetInvocationList()) Console.WriteLine($"  {f.Name}: {h.Method.DeclaringType?.FullName}.{h.Method.Name}");
    }

    static IEnumerable<DependencyObject> Tree(DependencyObject d)
    {
        yield return d;
        int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
        for (int i = 0; i < n; i++) foreach (var k in Tree(VisualTreeHelper.GetChild(d, i))) yield return k;
    }

    static List<Clock> AllClocks()
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var mc = typeof(UIElement).Assembly.GetType("System.Windows.Media.MediaContext")!.GetProperty("CurrentMediaContext", Any)!.GetValue(null)!;
        var tm = mc.GetType().GetProperty("TimeManager", Any)!.GetValue(mc)!;
        var root = tm.GetType().GetField("_timeManagerClock", Any)!.GetValue(tm)!;
        var all = new List<Clock>();
        foreach (var w in (System.Collections.IEnumerable)typeof(ClockGroup).GetField("_rootChildren", Any)!.GetValue(root)!)
            if (w is WeakReference { Target: Clock c } && c.Timeline != null) all.Add(c);
        return all;
    }

    static async Task<string> Cpu()
    {
        var p = Process.GetCurrentProcess();
        var before = p.TotalProcessorTime;
        var t0 = new Dictionary<int, double>();
        foreach (ProcessThread t in p.Threads) t0[t.Id] = t.TotalProcessorTime.TotalMilliseconds;
        await Task.Delay(6000);
        p.Refresh();
        double cores = (p.TotalProcessorTime - before).TotalSeconds / 6;
        var top = p.Threads.Cast<ProcessThread>().Select(t => t.TotalProcessorTime.TotalMilliseconds - t0.GetValueOrDefault(t.Id)).OrderDescending().Take(3).Select(ms => $"{ms / 60:N0}%");
        return $"{cores:N2} cores, busiest threads {string.Join(" ", top)}";
    }

    static (int Running, int Hidden, int Paused, List<string> HiddenKinds) Count(DependencyObject root)
    {
        int running = 0, hidden = 0, pausedCount = 0;
        var kinds = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement fe)
                foreach (var t in fe.Triggers)
                {
                    if (t is not EventTrigger et) continue;
                    foreach (var a in et.Actions)
                    {
                        if (a is not BeginStoryboard { Storyboard: { RepeatBehavior: { HasDuration: false, HasCount: false } } sb }) continue;
                        try
                        {
                            if (sb.GetCurrentState(fe) != ClockState.Active) continue;
                            if (sb.GetIsPaused(fe)) { pausedCount++; continue; }
                        }
                        catch (InvalidOperationException) { continue; }
                        running++;
                        if (!fe.IsVisible) { hidden++; kinds.Add(fe.GetType().Name); }
                    }
                }
            int n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return (running, hidden, pausedCount, kinds);
    }
}
