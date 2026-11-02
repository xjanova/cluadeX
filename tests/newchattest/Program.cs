using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace NewChatTest;

/// <summary>
/// The new chat page (an empty chat: the girls behind it, "what do you want to make?" and its starters), driven for
/// real: CluadeX's own DI graph builds the shipping MainWindow OFF-SCREEN at 1440×900 and this
///   · clicks the starters' own buttons: a starter asks which project first, fills an editable prompt, never sends;
///   · tries the corners: typed text about to be replaced, a project folder deleted after it was listed, the folder
///     picker cancelled, "no project" with one open, another chat opened mid-question, a double click, Thai;
///   · checks what it costs: the pictures decoded at display height, the entrance paused while the page is hidden or
///     the window minimised, nothing running once it is in, the idle CPU with and without the page;
///   · renders it in every theme (and narrow, short, Thai, the project question) into PNGs for a human look.
/// No network (the update check, license, Hugging Face and DGX Spark get an HttpClient that refuses every request),
/// no model, no ~/.cluadex (portable.txt → &lt;exe&gt;\Data, wiped every run). The folder picker is replaced, never shown.
///
///   dotnet run [-- --out &lt;dir&gt;]     report + pictures in &lt;dir&gt; (default: &lt;exe&gt;\newchat-out)
/// </summary>
internal static class Program
{
    const int W = 1440, H = 900;
    static string Out = Path.Combine(AppContext.BaseDirectory, "newchat-out");
    static int Pass, Fail;
    static readonly List<string> Failures = new();
    static readonly StringBuilder Report = new();
    static readonly List<string> BlockedRequests = new();
    static ServiceProvider Sp = null!;
    static MainViewModel MainVm = null!;
    static ChatViewModel Chat = null!;
    static Window MainWin = null!;
    static LocalizationService Loc = null!;
    static ThemeService Themes = null!;

    /// <summary>The starters the owner asked for (2026-11-01), by id: every one must be on the page.</summary>
    static readonly string[] Asked = ["app", "web", "program", "game", "design", "bug", "analyze", "comment", "test", "research", "deploy"];

    [STAThread]
    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--out" && i + 1 < args.Length) Out = args[++i];

        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Out);
                    File.WriteAllText(Path.Combine(Out, "newchat-report.md"), Report.ToString(), new UTF8Encoding(false));
                }
                catch { }
                Console.WriteLine($"\nreport + pictures: {Out}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        Environment.Exit(rc);
        return rc;
    }

    // ═══════════════════════════════════════════ plumbing ═══════════════════════════════════════════

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else { Fail++; Failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    }

    static async Task Idle(int ms = 0)
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(40);
        }
        return cond();
    }

    static object? GetField(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } f) return f.GetValue(o);
        return null;
    }

    static void SetField(object o, string name, object? value)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } f) { f.SetValue(o, value); return; }
    }

    sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (BlockedRequests) BlockedRequests.Add(request.RequestUri?.Host ?? "?");
            throw new HttpRequestException("newchattest-network-blocked: " + request.RequestUri?.Host);
        }
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    static bool IsUnder(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    /// <summary>What a hit landed on, up to the window: "Border ← Grid(Name) ← …" (the first eight).</summary>
    static string Chain(DependencyObject? d)
    {
        var parts = new List<string>();
        for (; d != null && parts.Count < 8; d = VisualTreeHelper.GetParent(d))
            parts.Add(d.GetType().Name + (d is FrameworkElement { Name.Length: > 0 } fe ? $"({fe.Name})" : ""));
        return parts.Count == 0 ? "nothing" : string.Join(" ← ", parts);
    }

    /// <summary>A real click: the button must be the thing under its own centre (nothing covers it), then the
    /// automation peer presses it — Command and all, as the mouse would.</summary>
    static async Task<bool> Click(Button b)
    {
        var root = (Visual)VisualTreeHelper.GetChild(MainWin, 0);
        var centre = b.TransformToAncestor(root).Transform(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
        var hit = ((UIElement)root).InputHitTest(centre) as DependencyObject;
        bool reachable = b.IsVisible && b.IsEnabled && IsUnder(hit, b);
        if (!reachable) return false;
        ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)!).Invoke();
        await Idle(60);
        return true;
    }

    static RenderTargetBitmap Snapshot(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    static string Shot(string name)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(MainWin, 0);
        var bmp = Snapshot(root, (int)Math.Round(root.ActualWidth), (int)Math.Round(root.ActualHeight));
        Directory.CreateDirectory(Out);
        string path = Path.Combine(Out, name + ".png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        Log($"      picture: {path}");
        return path;
    }

    /// <summary>The average colour of a box of the window (to tell one theme's tile from another's).</summary>
    static Color Average(FrameworkElement e)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(MainWin, 0);
        var bmp = Snapshot(root, (int)Math.Round(root.ActualWidth), (int)Math.Round(root.ActualHeight));
        var tl = e.TransformToAncestor(root).Transform(new Point(0, 0));
        var rect = new Int32Rect((int)tl.X + 2, (int)tl.Y + 2, Math.Max(1, (int)e.ActualWidth - 4), Math.Max(1, (int)e.ActualHeight - 4));
        var px = new byte[rect.Width * rect.Height * 4];
        bmp.CopyPixels(rect, px, rect.Width * 4, 0);
        long r = 0, g = 0, b = 0;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
        long n = px.Length / 4;
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>The Chat page's own view (found while it is on screen; kept while another page is shown).</summary>
    static ChatView? _view;
    static ChatView View => _view ??= Find<ChatView>(MainWin).First(v => v.IsVisible);
    static T Named<T>(string name) where T : class => (T)View.FindName(name);
    static GirlsBackdrop Backdrop => Named<GirlsBackdrop>("NewChatBackdrop");
    static ScrollViewer Page => Named<ScrollViewer>("NewChatPage");
    static List<Button> Tiles() => Find<Button>(Named<ItemsControl>("StarterList")).Where(b => b.DataContext is StarterTemplate).ToList();
    static Button Tile(string id) => Tiles().First(b => ((StarterTemplate)b.DataContext).Id == id);
    static List<Button> ProjectRows() => Find<Button>(Named<ItemsControl>("StarterProjectList")).Where(b => b.DataContext is StarterProject).ToList();
    static Button ProjectRow(string name) => ProjectRows().First(b => ((StarterProject)b.DataContext).Name == name);
    static Button CardButton(string name) => Named<Button>(name);

    static ClockState EntranceState() { try { return Backdrop.Entrance.GetCurrentState(Backdrop); } catch (InvalidOperationException) { return ClockState.Stopped; } }
    static bool EntrancePaused() { try { return Backdrop.Entrance.GetIsPaused(Backdrop); } catch (InvalidOperationException) { return false; } }
    static bool EntranceTicking() => EntranceState() == ClockState.Active && !EntrancePaused();

    /// <summary>The chat's own list grows with one message (the page goes), or is emptied (it comes back).</summary>
    static void AddMessage() => Chat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "hello" });

    /// <summary>Every clock that asks WPF for frames right now (active, not paused), read from WPF's TimeManager:
    /// "Storyboard rep=Forever dur=… fps=… DoubleAnimation Opacity" — what to blame when idle CPU is not idle.</summary>
    static List<string> RunningClocks()
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var mc = typeof(UIElement).Assembly.GetType("System.Windows.Media.MediaContext")!.GetProperty("CurrentMediaContext", Any)!.GetValue(null)!;
        var tm = mc.GetType().GetProperty("TimeManager", Any)!.GetValue(mc)!;
        var root = tm.GetType().GetField("_timeManagerClock", Any)!.GetValue(tm)!;
        var lines = new List<string>();
        foreach (var w in (System.Collections.IEnumerable)typeof(ClockGroup).GetField("_rootChildren", Any)!.GetValue(root)!)
        {
            if (w is not WeakReference { Target: Clock c } || c.Timeline == null || c.CurrentState != ClockState.Active || c.IsPaused) continue;
            var first = c.Timeline is ParallelTimeline p && p.Children.Count > 0 ? p.Children[0] : c.Timeline;
            lines.Add($"{c.Timeline.GetType().Name} rep={c.Timeline.RepeatBehavior} dur={c.Timeline.Duration} fps={Timeline.GetDesiredFrameRate(c.Timeline)} "
                      + $"{first.GetType().Name} {Storyboard.GetTargetName(first)} {Storyboard.GetTargetProperty(first)?.Path}");
        }
        return lines;
    }

    static async Task<string> Cpu(int ms = 5000)
    {
        var p = Process.GetCurrentProcess();
        var before = p.TotalProcessorTime;
        await Task.Delay(ms);
        p.Refresh();
        double cores = (p.TotalProcessorTime - before).TotalSeconds / (ms / 1000.0);
        return cores.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ═══════════════════════════════════════════ setup ═══════════════════════════════════════════

    /// <summary>WPF's own binding errors, caught as they happen ("BindingExpression path error: 'X' property not found").</summary>
    sealed class BindingErrors : TraceListener
    {
        public readonly List<string> Lines = new();
        private readonly StringBuilder _line = new();
        public override void Write(string? message) => _line.Append(message);
        public override void WriteLine(string? message)
        {
            _line.Append(message);
            lock (Lines) Lines.Add(_line.ToString());
            _line.Clear();
        }
    }
    static readonly BindingErrors Bindings = new();

    static async Task<int> RunAll()
    {
        Log("# New chat page harness — the empty chat, its starters and its backdrop");
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(Bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            Log("FATAL portable.txt is missing next to the exe — refusing to run against the real ~/.cluadex");
            return 2;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        if (Directory.Exists(Out)) foreach (var f in Directory.GetFiles(Out, "*.png")) File.Delete(f);

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        Sp = services.BuildServiceProvider();
        var settings = Sp.GetRequiredService<SettingsService>();
        bool portable = settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        Check("setup: portable data root (never the user's ~/.cluadex)", portable, settings.DataRoot);
        if (!portable) return 2;
        Directory.CreateDirectory(settings.DataRoot);

        var swaps = new (object Svc, string Field)[]
        {
            (Sp.GetRequiredService<AutoUpdateService>(), "_httpClient"), (Sp.GetRequiredService<XmanLicenseService>(), "_httpClient"),
            (Sp.GetRequiredService<HuggingFaceService>(), "_httpClient"), (Sp.GetRequiredService<DgxSparkService>(), "_probe"),
            (Sp.GetRequiredService<DgxSparkService>(), "_pull"),
        };
        bool allBlocked = true;
        foreach (var (svc, field) in swaps)
        {
            SetField(svc, field, new HttpClient(new BlockingHandler()) { Timeout = TimeSpan.FromSeconds(5) });
            allBlocked &= GetField(svc, field) is HttpClient;
        }
        Check("setup: update check, license, Hugging Face and DGX Spark clients refuse every request", allBlocked);
        if (!allBlocked) return 2;

        settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.SelectedModelPath = null;
            s.SelectedModelName = null;
            s.AutoExecuteCode = false;
            s.Language = "en";
        });
        Loc = Sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(Loc);
        Loc.SetLanguage("en");
        Themes = Sp.GetRequiredService<ThemeService>();
        Themes.ApplySaved();

        // Projects opened before: three folders, and a fourth deleted since (the list must never offer it).
        var registry = Sp.GetRequiredService<ProjectRegistryService>();
        string projects = Path.Combine(dataRoot, "projects-fixture");
        string alpha = Path.Combine(projects, "alpha-shop"), beta = Path.Combine(projects, "beta-api"),
               gamma = Path.Combine(projects, "gamma-game"), gone = Path.Combine(projects, "deleted-site");
        foreach (var d in new[] { gone, gamma, beta, alpha }) { Directory.CreateDirectory(d); registry.Record(d); await Task.Delay(15); }
        Directory.Delete(gone);

        MainWin = Sp.GetRequiredService<CluadeX.MainWindow>();
        MainVm = (MainViewModel)MainWin.DataContext;
        MainWin.WindowStartupLocation = WindowStartupLocation.Manual;
        MainWin.Left = -20000; MainWin.Top = -20000;
        MainWin.Width = W; MainWin.Height = H;
        MainWin.ShowActivated = false;
        MainWin.ShowInTaskbar = false;
        // The entrance is the subject here: watch it from the first frame the page is on screen.
        MainWin.Show();
        Chat = MainVm.ChatVM;
        Check("setup: chat VM finished its startup load", await WaitUntil(() => Chat.CurrentSession != null));
        (GetField(MainVm, "_gpuLiveTimer") as DispatcherTimer)?.Stop();
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(50);

        await FirstLook();
        await StarterFlow(alpha, beta, gamma, gone);
        await CornerCases(alpha, gamma);
        await Costs();
        await Renders();
        StaticChecks();

        Log($"\n{Pass} passed, {Fail} failed");
        if (BlockedRequests.Count > 0) Log($"(network refused {BlockedRequests.Count}×: {string.Join(", ", BlockedRequests.Distinct())})");
        foreach (var f in Failures) Log("  ✗ " + f);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════════════════ the page ═══════════════════════════════════════════

    static async Task FirstLook()
    {
        Log("\n## The page in a new chat (no project open)");
        Check("a new chat opens with no project", !Chat.HasProject, Chat.WorkingDirectory);
        Check("the new chat page is on screen", Page.IsVisible && Chat.Messages.Count == 0);
        // Started with the page (Loaded); on a slow start it may be over already — the girls start at opacity 0, so all of
        // them at 1 with the clock gone means it played. The replays below catch it running.
        var began = EntranceState();
        bool played = began == ClockState.Active || Backdrop.Figures.All(f => Math.Abs(f.Opacity - 1) < 0.01);
        Check("the girls' entrance began when the page appeared", played, $"{began} paused={EntrancePaused()}");

        var tiles = Tiles();
        var ids = tiles.Select(b => ((StarterTemplate)b.DataContext).Id).ToList();
        Check("every starter the owner asked for is a button on the page", Asked.All(ids.Contains), string.Join(",", ids));
        Check("each starter has a label and an icon", tiles.All(b => b.DataContext is StarterTemplate t && t.Label.Length > 0
            && !t.Label.StartsWith("starter.") && t.Glyph.Length > 0), $"{tiles.Count} tiles");
        var unreachable = new List<string>();
        var root = (Visual)VisualTreeHelper.GetChild(MainWin, 0);
        foreach (var b in tiles)
        {
            var c = b.TransformToAncestor(root).Transform(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
            var hitAt = ((UIElement)root).InputHitTest(c) as DependencyObject;
            if (!IsUnder(hitAt, b))
            {
                if (unreachable.Count == 0) Log("      covered by: " + Chain(hitAt));
                unreachable.Add(((StarterTemplate)b.DataContext).Id);
            }
        }
        Check("every starter is reachable by a click (nothing — the girls included — covers it)", unreachable.Count == 0, string.Join(",", unreachable));

        var bd = Backdrop;
        Check("the backdrop holds every girl the app ships", bd.Figures.Count == GirlArt.Names.Count && bd.Figures.Count >= 7, $"{bd.Figures.Count} of {GirlArt.Names.Count}");
        Check("the backdrop is never hit-tested", !bd.IsHitTestVisible);
        double dpi = VisualTreeHelper.GetDpi(bd).DpiScaleY;
        int bound = Math.Min(820, (int)Math.Ceiling(GirlsBackdrop.MaxFigureHeight * dpi));
        var heights = bd.Figures.Select(f => (f.Source as BitmapSource)?.PixelHeight ?? 0).ToList();
        Check("the pictures are decoded at display height, not the 820-px cut-outs", bd.DecodedHeight == bound && heights.All(h => h == bound),
            $"decoded {string.Join("/", heights.Distinct())} px for at most {GirlsBackdrop.MaxFigureHeight} DIP × {dpi:0.##}");
        Check("…and they stand no taller than that on screen", bd.Figures.All(f => f.ActualHeight <= GirlsBackdrop.MaxFigureHeight + 0.5),
            string.Join("/", bd.Figures.Select(f => f.ActualHeight.ToString("0")).Distinct()));

        bool settled = await WaitUntil(() => EntranceState() != ClockState.Active, 6000);
        Check("the entrance ends on its own (≈2 s) and nothing keeps running", settled, $"{EntranceState()}");
        Check("…with every girl fully in", bd.Figures.All(f => Math.Abs(f.Opacity - 1) < 0.01), string.Join(",", bd.Figures.Select(f => f.Opacity.ToString("0.00"))));
        await Idle(100);
        Check("…and its clock is gone, not held at its end (a held clock lives as long as the page)", EntranceState() == ClockState.Stopped,
            $"{EntranceState()}");
        Check("…the girls stay in once it is gone", bd.Figures.All(f => Math.Abs(f.Opacity - 1) < 0.01 && ((TranslateTransform)f.RenderTransform).Y == 0));
        Shot("01-midnight-new-chat");
    }

    static async Task StarterFlow(string alpha, string beta, string gamma, string gone)
    {
        Log("\n## A starter: the project first, then an editable prompt — never sent");
        int sessionsBefore = Chat.Sessions.Count;
        Check("Build an app: its button is clicked", await Click(Tile("app")));
        Check("…the project question comes up in place of the starters",
            Named<Border>("StarterProjectCard").IsVisible && !Named<ItemsControl>("StarterList").IsVisible);
        Check("…its title names the starter", Chat.StarterProjectTitle.StartsWith("Build an app"), Chat.StarterProjectTitle);
        Check("…nothing was put in the box or sent yet", Chat.UserInput.Length == 0 && Chat.Messages.Count == 0 && !Chat.IsGenerating);
        var names = ProjectRows().Select(b => ((StarterProject)b.DataContext).Name).ToList();
        Check("…the recent projects are offered, newest first", names.SequenceEqual(["alpha-shop", "beta-api", "gamma-game"]), string.Join(",", names));
        Check("…a folder deleted since it was opened is not offered", !names.Contains("deleted-site"));
        Check("…no row says 'open now' (none is open)", ProjectRows().All(b => !((StarterProject)b.DataContext).IsCurrent));
        Check("…no warning about replacing text (the box is empty)", !Named<TextBlock>("StarterReplaceWarning").IsVisible);
        Shot("02-midnight-project-question");

        Check("alpha-shop: its row is clicked", await Click(ProjectRow("alpha-shop")));
        string prompt = Loc.T("starter.app.prompt");
        Check("…the project opens for this chat", Chat.HasProject && string.Equals(Chat.WorkingDirectory, alpha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Chat.CurrentSession?.ProjectPath, alpha, StringComparison.OrdinalIgnoreCase), Chat.WorkingDirectory);
        Check("…the prompt is in the box", Chat.UserInput == prompt, Chat.UserInput);
        await Idle(300);
        Check("…and NOT sent: no message, no reply running, the page still on screen",
            Chat.Messages.Count == 0 && !Chat.IsGenerating && Page.IsVisible, $"{Chat.Messages.Count} messages");
        Check("…no 'Opened project' line either (it would take the page away)", Chat.Messages.Count == 0);
        Check("…still on the Chat page (the workbench did not jump forward)", ReferenceEquals(MainVm.CurrentView, Chat),
            MainVm.CurrentView?.GetType().Name ?? "?");
        var box = Named<TextBox>("InputBox");
        var (ps, pl) = ChatView.StarterPlaceholder(prompt);
        Check("…the [bracketed] part is selected, ready to type over", box.SelectionStart == ps && box.SelectionLength == pl && box.SelectedText.StartsWith("["),
            $"'{box.SelectedText}'");
        Check("…the starters are back", Named<ItemsControl>("StarterList").IsVisible && !Named<Border>("StarterProjectCard").IsVisible);
        Check("…no extra chat was made", Chat.Sessions.Count == sessionsBefore, $"{sessionsBefore} → {Chat.Sessions.Count}");
        Shot("03-midnight-prompt-filled");

        Log("\n## Typed text, the warning, Cancel");
        Chat.UserInput = "my own words";
        await Click(Tile("bug"));
        await Idle();
        Check("typed text in the box: the question warns it will be replaced", Chat.StarterWillReplaceInput && Named<TextBlock>("StarterReplaceWarning").IsVisible);
        Check("alpha-shop is now listed first as 'open now'", ProjectRows().FirstOrDefault()?.DataContext is StarterProject { Name: "alpha-shop", IsCurrent: true });
        Shot("04-midnight-replace-warning");
        Check("Cancel: its button is clicked", await Click(CardButton("StarterCancelButton")));
        Check("…the question goes, the typed words stay", !Chat.IsChoosingStarterProject && Chat.UserInput == "my own words", Chat.UserInput);
        Chat.UserInput = prompt;   // the starter's own text: replacing it is no loss
        await Click(Tile("web"));
        Check("the box holds the last starter's own text: no warning", !Chat.StarterWillReplaceInput);
        Chat.CancelStarterCommand.Execute(null);

        Log("\n## A project folder deleted after it was listed");
        await Click(Tile("game"));
        Directory.Delete(beta);
        string before = Chat.WorkingDirectory;
        await Click(ProjectRow("beta-api"));
        Check("its row is refused: the project stays as it was", Chat.WorkingDirectory == before && Chat.UserInput == prompt, Chat.WorkingDirectory);
        Check("…the question stays up, the status says why", Chat.IsChoosingStarterProject && Chat.StatusText.Contains("beta-api"), Chat.StatusText);
        Check("…and the list no longer offers it", !ProjectRows().Any(b => ((StarterProject)b.DataContext).Name == "beta-api"));
        Directory.CreateDirectory(beta);

        Log("\n## Choose a folder…");
        string? asked = null;
        Chat.StarterFolderPicker = (title, start) => { asked = title; return null; };
        await Click(CardButton("StarterBrowseButton"));
        Check("the picker is asked, and a cancelled picker changes nothing", asked != null && Chat.IsChoosingStarterProject && Chat.WorkingDirectory == before, asked ?? "not asked");
        Chat.StarterFolderPicker = (_, _) => gamma;
        await Click(CardButton("StarterBrowseButton"));
        Check("a folder chosen in the picker opens and the game prompt fills in",
            string.Equals(Chat.WorkingDirectory, gamma, StringComparison.OrdinalIgnoreCase) && Chat.UserInput == Loc.T("starter.game.prompt") && Chat.Messages.Count == 0,
            Chat.WorkingDirectory);

        Log("\n## No project");
        await Click(Tile("research"));
        await Click(CardButton("StarterNoProjectButton"));
        Check("'No project' with one open: it closes, the chat is unbound, the prompt fills in",
            !Chat.HasProject && Chat.CurrentSession?.ProjectPath == "" && Chat.UserInput == Loc.T("starter.research.prompt") && Chat.Messages.Count == 0,
            $"{Chat.WorkingDirectory}|{Chat.CurrentSession?.ProjectPath}");
        _ = gone;
    }

    static async Task CornerCases(string alpha, string gamma)
    {
        Log("\n## A double click on a project");
        Chat.UserInput = "";
        await Click(Tile("test"));
        var row = ProjectRow("alpha-shop");
        Chat.UseStarterProjectCommand.Execute(row.DataContext);
        Chat.UseStarterProjectCommand.Execute(row.DataContext);   // the second press of a double click
        await Idle();
        Check("the second press does nothing: the prompt once, still not sent", Chat.UserInput == Loc.T("starter.test.prompt") && Chat.Messages.Count == 0);

        Log("\n## Another chat opened while the question is up");
        await Click(Tile("deploy"));
        var other = new ChatSession { Title = "other chat" };
        Chat.Sessions.Add(other);
        Chat.LoadSessionCommand.Execute(other.Id);
        await WaitUntil(() => Chat.CurrentSession?.Id == other.Id, 5000);
        await Idle(100);
        Check("the question belonged to the chat that was left: it is gone", Chat.CurrentSession?.Id == other.Id && !Chat.IsChoosingStarterProject);
        Check("…and the new chat shows the starters", Page.IsVisible && Named<ItemsControl>("StarterList").IsVisible);

        Log("\n## Thai");
        Loc.SetLanguage("th");
        await Idle(100);
        var app = (StarterTemplate)Tile("app").DataContext;
        var label = Find<TextBlock>(Tile("app")).FirstOrDefault(t => t.Text == app.Label);
        Check("switching to Thai relabels the starters on screen", app.Label == "สร้างแอป" && label != null, app.Label);
        Check("…the title too", Find<TextBlock>(Page).Any(t => t.Text == "วันนี้อยากทำอะไรดี?"));
        Shot("05-midnight-thai");
        await Click(Tile("comment"));
        Check("…and the question", Chat.StarterProjectTitle == "คอมเมนต์โค้ด — ทำในโปรเจกต์ไหน?", Chat.StarterProjectTitle);
        Shot("06-midnight-thai-project-question");
        Chat.CancelStarterCommand.Execute(null);
        Loc.SetLanguage("en");
        await Idle(100);

        Log("\n## Every starter's words, both languages");
        var missing = new List<string>();
        foreach (var t in StarterTemplate.All)
            foreach (var suffix in new[] { "", ".hint", ".prompt" })
            {
                string key = $"starter.{t.Id}{suffix}";
                string en = Translate(key, "en"), th = Translate(key, "th");
                if (en == key || th == key || en.Length == 0 || th.Length == 0) missing.Add(key);
                else if (suffix != "" && en == th) missing.Add(key + " (th = en)");
                else if (suffix != "" && !th.Any(c => c >= '฀' && c <= '๿')) missing.Add(key + " (th has no Thai)");
                if (suffix == ".prompt" && (ChatView.StarterPlaceholder(en).Length == 0 || ChatView.StarterPlaceholder(th).Length == 0)) missing.Add(key + " (no [part] to type over)");
            }
        Check("every label, hint and prompt has English and Thai, and each prompt a [part] to type over", missing.Count == 0, string.Join(", ", missing));
        _ = alpha; _ = gamma;
    }

    /// <summary>The key's text in <paramref name="lang"/>, read from LocalizationService's own table (the key itself
    /// when it has none) — without switching the whole app's language for every key.</summary>
    static string Translate(string key, string lang)
    {
        var table = (Dictionary<string, Dictionary<string, string>>)typeof(LocalizationService)
            .GetField("Translations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return table.TryGetValue(key, out var byLang) && byLang.TryGetValue(lang, out var text) ? text : key;
    }

    // ═══════════════════════════════════════════ what it costs ═══════════════════════════════════════════

    static async Task Costs()
    {
        Log("\n## Hidden, minimised, idle");
        // A new empty chat brings the girls in again.
        AddMessage();
        await Idle(50);
        Check("a chat with a message: no page, no backdrop", !Page.IsVisible && !Backdrop.IsVisible);
        Chat.Messages.Clear();
        await Idle(30);
        Check("emptied again: the entrance plays again", EntranceTicking(), $"{EntranceState()} paused={EntrancePaused()}");

        MainVm.NavigateToCommand.Execute("Settings");
        await Idle(30);
        Check("another page shown mid-entrance: the backdrop is hidden and its entrance paused",
            !Backdrop.IsVisible && EntranceState() == ClockState.Active && EntrancePaused(), $"{EntranceState()} paused={EntrancePaused()}");
        await Task.Delay(2500);
        Check("…and it stays paused, however long (no frame asked for)", EntrancePaused() && EntranceState() == ClockState.Active);
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(30);
        Check("back on the chat: it carries on", EntranceTicking(), $"{EntranceState()} paused={EntrancePaused()}");

        // The chat starts empty while another page is shown (a new chat from the palette on the Code page).
        MainVm.NavigateToCommand.Execute("Settings");
        await Idle(30);
        await WaitUntil(() => EntranceState() != ClockState.Active || EntrancePaused(), 3000);
        AddMessage();
        await Idle(30);
        Chat.Messages.Clear();
        await Idle(30);
        Check("a chat emptied while another page is shown: the entrance waits, paused", EntrancePaused() || EntranceState() != ClockState.Active,
            $"{EntranceState()} paused={EntrancePaused()}");
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(30);

        // Minimised mid-entrance.
        AddMessage(); await Idle(30); Chat.Messages.Clear(); await Idle(30);
        Check("(a fresh entrance is running)", EntranceTicking());
        MainWin.WindowState = WindowState.Minimized;
        await Idle(30);
        Check("the window minimised: the entrance is paused", EntrancePaused(), $"{EntranceState()} paused={EntrancePaused()}");
        await Task.Delay(1500);
        Check("…and still paused a while later", EntrancePaused() && EntranceState() == ClockState.Active);
        MainWin.WindowState = WindowState.Normal;
        await Idle(30);
        Check("restored: it carries on", EntranceTicking(), $"{EntranceState()} paused={EntrancePaused()}");
        await WaitUntil(() => EntranceState() != ClockState.Active, 6000);

        // Idle: the empty chat with the girls in, against the same window with the page gone.
        // What decides it: the clocks asking for frames are the same with the page as without (the app's own, like the
        // logo halo) — the page adds none. The CPU is reported beside it, four rounds each taken in turn after a
        // warm-up (a restored window, a GC or a timer landing in one 3-s window is noise), compared by the median.
        await Task.Delay(2500);
        var withPage = new List<double>();
        var withoutPage = new List<double>();
        List<string> clocksWith = new(), clocksWithout = new();
        static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        for (int round = 0; round < 4; round++)
        {
            await Idle(500);
            withPage.Add(Num(await Cpu(3000)));
            Check($"(round {round + 1}: the entrance is not running while measured)", EntranceState() != ClockState.Active);
            if (round == 0) clocksWith = RunningClocks();
            AddMessage();
            await Idle(500);
            withoutPage.Add(Num(await Cpu(3000)));
            if (round == 0) clocksWithout = RunningClocks();
            Chat.Messages.Clear();
            await WaitUntil(() => EntranceState() != ClockState.Active, 6000);
        }
        foreach (var c in clocksWith) Log("      running with the page on screen: " + c);
        Check("the page on screen adds no running clock (the same ones run without it)",
            clocksWith.OrderBy(x => x).SequenceEqual(clocksWithout.OrderBy(x => x)), $"{clocksWith.Count} vs {clocksWithout.Count}");
        static double Median(List<double> v) { var o = v.OrderBy(x => x).ToList(); return (o[1] + o[2]) / 2; }
        string F(IEnumerable<double> v) => string.Join("/", v.Select(x => x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
        Log($"  idle CPU (cores, 4 × 3 s): {F(withPage)} with the new chat page on screen, {F(withoutPage)} with a message instead");
        Check("idle CPU with the page left open is no more than without it (medians ≤ 0.03 cores apart)",
            Median(withPage) - Median(withoutPage) <= 0.03, $"{Median(withPage):0.000} vs {Median(withoutPage):0.000}");
    }

    // ═══════════════════════════════════════════ words and bindings ═══════════════════════════════════════════

    /// <summary>What compiles but renders blank: a {services:Loc} key with no Thai (it shows the raw key), and a
    /// binding on the page whose path does not exist (WPF only says so in its trace, caught above for the whole run).</summary>
    static void StaticChecks()
    {
        Log("\n## The chat view's words and the page's bindings");
        string xaml = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\Views\ChatView.xaml"));
        if (!File.Exists(xaml)) { Check("the chat view's XAML is found next to the harness", false, xaml); return; }
        var keys = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(xaml), @"\{services:Loc\s+([A-Za-z0-9_.\-]+)\}")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var noThai = keys.Where(k => { string th = Translate(k, "th"); return th == k || th.Length == 0; }).ToList();
        var noEnglish = keys.Where(k => Translate(k, "en") == k).ToList();
        Check($"every {{services:Loc}} key in ChatView.xaml has a Thai value ({keys.Count} keys)", noThai.Count == 0, string.Join(", ", noThai));
        Check("…and an English one", noEnglish.Count == 0, string.Join(", ", noEnglish));

        string[] ours = ["Starter", "PendingStarter", "IsChoosingStarterProject", "HasStarterProjects", "StarterTemplate", "StarterProject",
                         "Glyph", "Tone", "Hint", "IsCurrent", "NewChat"];
        List<string> errors;
        lock (Bindings.Lines) errors = Bindings.Lines.ToList();
        var mine = errors.Where(e => ours.Any(e.Contains)).Distinct().ToList();
        Log($"  binding errors in the whole run: {errors.Count} ({mine.Count} on the new chat page)");
        foreach (var e in mine.Take(8)) Log("      " + e);
        Check("no binding on the new chat page fails", mine.Count == 0, $"{mine.Count}");
    }

    // ═══════════════════════════════════════════ renders ═══════════════════════════════════════════

    static async Task Renders()
    {
        Log("\n## Every theme, the page on screen (a live switch, the way the picker does it)");
        var tileColour = new Dictionary<string, Color>();
        foreach (string id in ThemeIds.All)
        {
            Themes.Apply(id, persist: false);
            await Idle(250);
            bool flourish = !id.Equals(ThemeIds.Classic, StringComparison.OrdinalIgnoreCase);
            Check($"{id}: switched live with the page on screen — the page is still there", Page.IsVisible && Tiles().Count == StarterTemplate.All.Count);
            Check($"{id}: the girls {(flourish ? "are behind it" : "are off (Classic keeps it plain)")}", Backdrop.IsVisible == flourish, $"visible={Backdrop.IsVisible}");
            tileColour[id] = Average(Tile("app"));
            Shot($"10-theme-{id.ToLowerInvariant()}");
            await Click(Tile("web"));
            Shot($"11-theme-{id.ToLowerInvariant()}-project-question");
            Chat.CancelStarterCommand.Execute(null);
            await Idle();
        }
        Check("the tiles take each theme's own colours", tileColour.Values.Distinct().Count() == tileColour.Count,
            string.Join(" ", tileColour.Select(kv => $"{kv.Key}=#{kv.Value.R:X2}{kv.Value.G:X2}{kv.Value.B:X2}")));
        Themes.Apply(ThemeIds.Midnight, persist: false);
        await Idle(250);

        Log("\n## Narrow and short windows");
        // The window stops at 900 × 600 (MainWindow MinWidth/MinHeight); the docked chat below is narrower still.
        foreach (var (w, h, name) in new[] { (1100, 900, "1100"), (900, 900, "900-min"), (1440, 600, "short") })
        {
            MainWin.Width = w; MainWin.Height = h;
            await Idle(300);
            var page = Page;
            var root = (Visual)page;
            var outside = Tiles().Where(t =>
            {
                var tl = t.TransformToAncestor(root).Transform(new Point(0, 0));
                return tl.X < -0.5 || tl.X + t.ActualWidth > page.ViewportWidth + 0.5;
            }).Select(t => ((StarterTemplate)t.DataContext).Id).ToList();
            Check($"{name}: every starter fits across (they wrap, nothing is cut off)", outside.Count == 0 && page.ExtentWidth <= page.ViewportWidth + 0.5,
                $"{page.ViewportWidth:0} wide · {string.Join(",", outside)}");
            Check($"{name}: the girls stand within the area, {GirlsBackdrop.MinFigureHeight:0}–{GirlsBackdrop.MaxFigureHeight:0} DIP",
                Backdrop.Figures.All(f => f.ActualHeight >= GirlsBackdrop.MinFigureHeight - 0.5 && f.ActualHeight <= GirlsBackdrop.MaxFigureHeight + 0.5),
                string.Join("/", Backdrop.Figures.Select(f => f.ActualHeight.ToString("0")).Distinct()));
            if (name == "short") Check("short: the page scrolls rather than cutting the starters off", page.ScrollableHeight > 0 || page.ExtentHeight <= page.ViewportHeight + 0.5,
                $"extent {page.ExtentHeight:0} / viewport {page.ViewportHeight:0}");
            Shot($"20-midnight-{name}");
        }
        MainWin.Width = W; MainWin.Height = H;
        await Idle(200);

        Log("\n## The same chat docked beside the editor (the narrowest it gets)");
        MainVm.NavigateToCommand.Execute("Code");
        await Idle(800);
        var docked = Find<ChatView>(MainWin).FirstOrDefault(v => v.IsVisible && !ReferenceEquals(v, View));
        if (docked == null) Log("INFO  the Code page shows no docked chat in this layout");
        else
        {
            var page = (ScrollViewer)docked.FindName("NewChatPage");
            var tiles = Find<Button>((ItemsControl)docked.FindName("StarterList")).Where(b => b.DataContext is StarterTemplate).ToList();
            var outside = tiles.Where(t =>
            {
                var tl = t.TransformToAncestor(page).Transform(new Point(0, 0));
                return tl.X < -0.5 || tl.X + t.ActualWidth > page.ViewportWidth + 0.5;
            }).Select(t => ((StarterTemplate)t.DataContext).Id).ToList();
            Check("docked: the page is there and every starter fits across", page.IsVisible && tiles.Count == StarterTemplate.All.Count && outside.Count == 0,
                $"{page.ViewportWidth:0} wide · {tiles.Count} tiles · cut: {string.Join(",", outside)}");
            int across = tiles.Select(t => Math.Round(t.TransformToAncestor(page).Transform(new Point(0, 0)).X)).Distinct().Count();
            Check("docked: the starters go compact, three across (not two big ones and a long scroll)",
                tiles.All(t => t.ActualWidth < 134) && across == 3, $"{tiles[0].ActualWidth:0} wide · {across} across");
            Check("the full-size chat page keeps the big tiles", Tiles().All(t => Math.Abs(t.ActualWidth - 134) < 0.5), $"{Tiles()[0].ActualWidth:0}");
            var bd = (GirlsBackdrop)docked.FindName("NewChatBackdrop");
            Check("docked: its girls share the decoded pictures (no second decode)", bd.Figures.Zip(Backdrop.Figures).All(p => ReferenceEquals(p.First.Source, p.Second.Source)));
            Shot("21-midnight-docked-in-editor");
        }
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(200);
    }
}
