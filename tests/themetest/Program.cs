using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

/// <summary>
/// The look of CluadeX, rendered for real and compared pixel by pixel.
///
/// CluadeX's own DI graph (App.ConfigureServices) builds the shipping MainWindow + MainViewModel, the window is
/// shown OFF-SCREEN (never on the desktop, never activated) at 1440×900, and every main page and dialog is
/// rendered with RenderTargetBitmap into PNGs:
///
///   E:\cluadex-bld\themes\golden\&lt;page&gt;.png              the look BEFORE the theme work (captured once, --golden)
///   E:\cluadex-bld\themes\renders\&lt;theme&gt;\&lt;page&gt;.png    every theme the build ships (Midnight first, at startup)
///   ...\_contact.png                                         one contact sheet per set
///   ...\diff\&lt;page&gt;.png                                   changed pixels in red over the golden, when any changed
///
/// The pass/fail rule: Midnight (the default theme = the look before the theme work) may differ from the golden by
/// at most 0.1 % of a page's pixels, a pixel counting as changed when any channel moved by more than 8/255.
///
/// Everything theme-specific (ThemeService, ThemeTokens, AppSettings.Theme, the Settings picker) is reached by
/// REFLECTION: the same source runs against a build from before the theme work — that is how the golden was made.
///
/// No network (the update check, license pricing, Hugging Face and the DGX Spark probe get an HttpClient that
/// refuses every request and counts it), no model, no ~/.cluadex (portable.txt → &lt;exe&gt;\Data, wiped every run).
///
///   dotnet run -p:CluadeXBin=X\ -- --golden [--force]    capture the baseline (refuses to overwrite without --force)
///   dotnet run -p:CluadeXBin=X\                          render every theme, compare Midnight, run the theme checks
///   options: --out &lt;dir&gt;  --tol &lt;0-255&gt;  --max-pct &lt;percent&gt;  --themes Midnight,Classic
///            --all-pages  --only &lt;page,page&gt;  --probe   (Probe.cs)
/// </summary>
internal static partial class Program
{
    const int W = 1440, H = 900;
    static string OutRoot = @"E:\cluadex-bld\themes";
    const string FixtureRoot = @"E:\cluadex-bld\themes";
    static int Tolerance = 8;
    static double MaxChangedPct = 0.1;
    static bool GoldenMode, Force;
    static string[] OnlyThemes;

    static int Pass, Fail;
    static readonly StringBuilder Report = new();
    static readonly List<string> Failures = new();

    static ServiceProvider Sp;
    static string FixtureProject = "";
    static SettingsService Settings;
    static MainViewModel MainVm;
    static Window MainWin;
    static readonly List<string> BlockedRequests = new();

    /// <summary>Main pages: file name, the NavigateTo target, the label on the contact sheet.</summary>
    static (string Key, string Nav, string Label)[] Pages =
    {
        ("chat", "Chat", "Chat"),
        ("code-editor", "Code", "Code Editor"),
        ("settings", "Settings", "Settings"),
        ("settings-general", "Settings", "Settings › General"),
        ("features", "Features", "Features"),
        ("model-manager", "Models", "Model Manager"),
        ("mcp-servers", "McpServers", "MCP Servers"),
        ("plugins", "Plugins", "Plugins"),
        ("skills", "Skills", "Skills"),
        ("task-manager", "Tasks", "Task Manager"),
        ("time-machine", "TimeMachine", "Time Machine"),
        ("hex-editor", "HexEditor", "Hex Editor"),
        ("debug-log", "DebugLog", "Debug Log"),
    };
    static (string Key, string Label)[] Dialogs =
    {
        ("license", "License"), ("brainx-invite", "BrainX invite"), ("dgx-spark", "DGX Spark"), ("preview-pane", "Preview pane"),
    };
    /// <summary>Pages whose look is SUPPOSED to change against the golden (reported, never a failure):
    /// Settings › General gained the theme picker.</summary>
    static readonly HashSet<string> ExpectedToChange = new() { "settings-general" };

    [STAThread]
    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--golden": GoldenMode = true; break;
                case "--force": Force = true; break;
                case "--probe": ProbeMode = true; break;
                case "--out" when i + 1 < args.Length: OutRoot = args[++i]; break;
                case "--tol" when i + 1 < args.Length: Tolerance = int.Parse(args[++i]); break;
                case "--max-pct" when i + 1 < args.Length: MaxChangedPct = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--themes" when i + 1 < args.Length: OnlyThemes = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--all-pages": AllPages = true; break;
                case "--only" when i + 1 < args.Length: OnlyPages = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
            }
        }
        SelectPages();

        var app = new CluadeX.App();
        app.InitializeComponent();                    // App.xaml resources — the merged theme + styles
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
                    Directory.CreateDirectory(OutRoot);
                    File.WriteAllText(Path.Combine(OutRoot, GoldenMode ? "golden-report.md" : "theme-report.md"), Report.ToString(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "theme-report.md"), Report.ToString(), new UTF8Encoding(false));
                }
                catch { }
                Console.WriteLine($"\nreport: {Path.Combine(OutRoot, GoldenMode ? "golden-report.md" : "theme-report.md")}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        // Background work the app started (GPU probe, MCP manager) must not keep a finished harness alive.
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

    static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(40);
        }
        return cond();
    }

    static object GetField(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    static bool SetField(object o, string name, object value)
    {
        var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) return false;
        f.SetValue(o, value);
        return true;
    }

    static object Invoke(object o, string method, params object[] args)
    {
        var m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
        if (m == null) throw new MissingMethodException(o.GetType().Name, method);
        return m.Invoke(m.IsStatic ? null : o, args);
    }

    static object Prop(object o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);

    /// <summary>
    /// No dead buttons: every button a user can see on the page must be what a click at its centre reaches. Born from
    /// the Edit page's "Open Folder", which rendered fine while an empty, transparent file tree lay over it and took
    /// every click. Buttons scrolled out of view, faded to nothing or off the window are skipped.
    /// </summary>
    static void ClickableButtons(string set, string page)
    {
        var root = (UIElement)RootVisual(MainWin);
        var covered = new List<string>();
        int seen = 0;
        foreach (var b in Find<ButtonBase>(root))
        {
            if (!b.IsVisible || !b.IsEnabled || !b.IsHitTestVisible || b.ActualWidth < 8 || b.ActualHeight < 8) continue;
            Point c;
            try { c = b.TransformToAncestor(root).Transform(new Point(b.ActualWidth / 2, b.ActualHeight / 2)); }
            catch (InvalidOperationException) { continue; }
            if (c.X < 0 || c.Y < 0 || c.X >= W || c.Y >= H) continue;
            if (!InViewAndOpaque(b, c, root)) continue;
            seen++;
            var hit = root.InputHitTest(c) as DependencyObject;
            if (hit != null && IsWithin(hit, b)) continue;
            // Where, and inside what (a ComboBox's own toggle has no name of its own), so a failure can be found.
            string owner = b.TemplatedParent is FrameworkElement tp ? $" in {Describe(tp)}" : "";
            covered.Add($"{Describe(b)}{owner} @{c.X:0},{c.Y:0} covered by {Describe(hit)}");
        }
        Check($"{set}/{page}: every visible button is reachable by a click ({seen} checked)", covered.Count == 0,
              string.Join("; ", covered.Take(6)));
    }

    /// <summary>
    /// A click on a folder in the explorer opens it. Born from every folder doing nothing in the app: its entries were
    /// added to the tree on screen from a worker thread, which WPF refuses (the exception went to the debug log).
    /// Needs the real, bound TreeView — a view model alone accepts the cross-thread add.
    /// </summary>
    static bool EditorToolsDone;

    /// <summary>A real click on a button (its automation peer: Click handlers AND its Command run, as for a mouse).
    /// Toggle buttons get their Click event, which is what the editor's toggles listen to.</summary>
    static void Press(ButtonBase b)
    {
        if (b is ToggleButton) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return; }
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer((Button)b);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }

    /// <summary>
    /// The Code Editor's standard tools, driven on the real page (owner 2026-10-12: "เซฟตรงไหน ไม่มีเลย"): the
    /// toolbar's Save writes the file, Find counts and steps through matches, Replace All is one undo, Go to line,
    /// comment toggling, the line tools menu, folding, zoom, word wrap, and what the status bar says. Once, in the
    /// startup theme; everything is put back so later renders still match.
    /// </summary>
    static async Task EditorToolsCheck(string set)
    {
        if (EditorToolsDone) return;
        EditorToolsDone = true;
        var vm = MainVm.CodeEditorVM;
        string file = Path.Combine(FixtureProject, "src", "Greeter.cs");
        await vm.OpenPathAsync(file);
        await Idle(200);
        var view = Find<CluadeX.Views.CodeEditorView>((DependencyObject)RootVisual(MainWin)).FirstOrDefault();
        if (view == null) { Check($"{set}/editor tools: the code page is on screen", false); return; }
        T El<T>(string name) where T : class => view.FindName(name) as T;
        var editor = El<ICSharpCode.AvalonEdit.TextEditor>("Editor")!;
        var doc = editor.Document;
        string original = File.ReadAllText(file);
        int Count(string word) { int n = 0, i = 0; string t = doc.Text; while ((i = t.IndexOf(word, i, StringComparison.Ordinal)) >= 0) { n++; i += word.Length; } return n; }
        try
        {
            var save = El<Button>("TbSave")!;
            Check($"{set}/editor tools: a toolbar with Save on it is on screen", El<FrameworkElement>("EditorToolbar") is { IsVisible: true } && save.IsVisible);
            CommandManager.InvalidateRequerySuggested();
            await Idle(100);
            Check($"{set}/editor tools: Save is greyed out while there is nothing to save", !save.IsEnabled);
            doc.Insert(doc.TextLength, "// edited by the harness\n");
            CommandManager.InvalidateRequerySuggested();
            await Idle(150);
            Check($"{set}/editor tools: an edit lights Save up", save.IsEnabled);
            Press(save);
            await WaitUntil(() => vm.ActiveTab is { IsDirty: false }, 4000);
            Check($"{set}/editor tools: clicking Save writes the file to disk",
                File.ReadAllText(file).EndsWith("// edited by the harness\n") && vm.ActiveTab is { IsDirty: false });

            // ── find ──
            Press(El<Button>("TbFind")!);
            await Idle(100);
            var findBox = El<TextBox>("FindBox")!;
            Check($"{set}/editor tools: the Find button opens the find bar", El<FrameworkElement>("FindBar") is { IsVisible: true } && El<FrameworkElement>("ReplaceRow") is { IsVisible: false });
            findBox.Text = "count";
            await Idle(150);
            var countLabel = El<TextBlock>("FindCount")!;
            int firstHit = editor.SelectionStart;
            Check($"{set}/editor tools: typing in Find jumps to the first match and counts them all",
                editor.SelectedText == "count" && countLabel.Text.Contains('3'), $"sel='{editor.SelectedText}' label='{countLabel.Text}'");
            Press(El<Button>("FindNextBtn")!);
            await Idle(100);
            Check($"{set}/editor tools: Next moves to the next match", editor.SelectedText == "count" && editor.SelectionStart > firstHit,
                $"{firstHit} → {editor.SelectionStart} label='{countLabel.Text}'");

            // ── replace all = one undo ──
            Press(El<Button>("TbReplace")!);
            await Idle(100);
            El<TextBox>("ReplaceBox")!.Text = "total";
            Press(El<Button>("ReplaceAllBtn")!);
            await Idle(150);
            Check($"{set}/editor tools: Replace all replaces every match", Count("count") == 0 && Count("total") == 3, $"count={Count("count")} total={Count("total")}");
            Press(El<Button>("TbUndo")!);
            await Idle(100);
            Check($"{set}/editor tools: one Undo takes the whole Replace all back", Count("count") == 3 && Count("total") == 0, $"count={Count("count")}");

            // ── go to line ──
            Press(El<Button>("TbGoTo")!);
            await Idle(100);
            El<TextBox>("GoToBox")!.Text = "6";
            Press(El<Button>("GoToBtn")!);
            await Idle(100);
            Check($"{set}/editor tools: Go to line 6 puts the caret there and closes the bar",
                editor.TextArea.Caret.Line == 6 && El<FrameworkElement>("FindBar") is { IsVisible: false }, $"line {editor.TextArea.Caret.Line}");

            // ── comment toggle ──
            string LineText(int n) => doc.GetText(doc.GetLineByNumber(n));
            string line6 = LineText(6);
            Press(El<Button>("TbComment")!);
            await Idle(80);
            string commented = LineText(6);
            Press(El<Button>("TbComment")!);
            await Idle(80);
            Check($"{set}/editor tools: Comment puts // on the line, and a second click takes it off",
                commented == "// " + line6 && LineText(6) == line6, $"'{commented}' → '{LineText(6)}'");

            // ── status bar ──
            Check($"{set}/editor tools: the status bar shows line, encoding, line ending and language",
                (El<TextBlock>("StPosition")?.Text ?? "").Contains('6') && El<Button>("StEncoding")?.Content as string == "UTF-8"
                && El<Button>("StEol")?.Content as string == "LF" && (El<TextBlock>("StLanguage")?.Text ?? "") == "C#",
                $"'{El<TextBlock>("StPosition")?.Text}' '{El<Button>("StEncoding")?.Content}' '{El<Button>("StEol")?.Content}' '{El<TextBlock>("StLanguage")?.Text}'");

            // ── line tools menu ──
            var menu = El<ContextMenu>("ToolsMenu")!;
            MenuItem Item(string key) => menu.Items.OfType<MenuItem>().First(m => (m.Header as string) == CluadeX.Services.LocalizedResources.Instance[key]);
            Check($"{set}/editor tools: Comment with nothing selected leaves nothing selected (the next key must not replace the line)",
                editor.SelectionLength == 0 && editor.TextArea.Caret.Line == 6, $"selection {editor.SelectionLength}, line {editor.TextArea.Caret.Line}");
            editor.Select(doc.GetLineByNumber(3).Offset, 0);   // a click on line 3
            string line3 = LineText(3);
            int lines = doc.LineCount;
            Item("edt.dupLine").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle(60);
            Check($"{set}/editor tools: Tools ▸ Duplicate line", doc.LineCount == lines + 1 && LineText(4) == line3, $"'{LineText(4)}'");
            Press(El<Button>("TbUndo")!);
            await Idle(60);

            // ── folding ──
            await Idle(900);
            var folding = editor.TextArea.TextView.GetService(typeof(ICSharpCode.AvalonEdit.Folding.FoldingManager)) as ICSharpCode.AvalonEdit.Folding.FoldingManager;
            int folds = folding?.AllFoldings.Count() ?? 0;
            Item("edt.foldAll").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            bool allFolded = folding != null && folds > 0 && folding.AllFoldings.All(f => f.IsFolded);
            Item("edt.unfoldAll").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check($"{set}/editor tools: C# code folds (class, method) and Fold all / Unfold all work",
                folds >= 2 && allFolded && folding!.AllFoldings.All(f => !f.IsFolded), $"{folds} foldings");

            // ── view toggles ──
            Press(El<Button>("TbZoomIn")!);
            await Idle(60);
            bool bigger = editor.FontSize > 13 && (El<Button>("StZoom")?.Content as string) == "110%";
            Press(El<Button>("StZoom")!);
            await Idle(60);
            Check($"{set}/editor tools: zoom in, and the status bar's % puts it back", bigger && Math.Abs(editor.FontSize - 13) < 0.01, $"{editor.FontSize}");
            Press(El<ToggleButton>("TbWrap")!);
            bool wrapped = editor.WordWrap && El<ToggleButton>("TbWrap")!.IsChecked == true;
            Press(El<ToggleButton>("TbWrap")!);
            Check($"{set}/editor tools: word wrap toggles (and the button shows it)", wrapped && !editor.WordWrap);

            // A picture for the eye: the find + replace bar open on a match.
            Press(El<Button>("TbReplace")!);
            findBox.Text = "count";
            await Idle(200);
            SavePng(Snapshot((Visual)RootVisual(MainWin), W, H), Path.Combine(OutRoot, "editor-tools.png"));
            Log($"      picture: {Path.Combine(OutRoot, "editor-tools.png")}");
            ClickableButtons(set, "code-editor (toolbar + find/replace open)");
        }
        finally
        {
            // Back to the startup state: file as written, bar closed, caret at the top.
            try { view.CloseFindBar(); } catch { }
            doc.Text = original;
            await Idle(50);
            if (vm.SaveActiveCommand.CanExecute(null)) vm.SaveActiveCommand.Execute(null);
            await WaitUntil(() => vm.ActiveTab is { IsDirty: false }, 4000);
            editor.TextArea.Caret.Offset = 0;
            editor.Select(0, 0);
            editor.ScrollToHome();
            await Idle(100);
        }
    }

    /// <summary>Owner, 2026-10-13: "ถ้าเปลี่ยนธีมระหว่างทาง ไฟล์ในนี้จะหายไป และค้างช้า" — a theme switch with the editor
    /// open, a folder expanded in the explorer and a long agent chat beside it. The explorer must keep every row and
    /// the open folder, and the switch must not freeze the window.</summary>
    static async Task ThemeSwitchKeepsWork(object themes)
    {
        Log("\n## Theme switch with work on screen: the editor, an expanded folder, a long chat");
        var vm = MainVm.CodeEditorVM;
        var chat = MainVm.ChatVM;
        MainVm.NavigateToCommand.Execute("Code");
        await Idle(300);
        await WaitUntil(() => vm.Tree.Count > 0 && vm.Tree[0].Children.Any(n => n.IsDirectory), 5000);
        var folder = vm.Tree[0].Children.FirstOrDefault(n => n.IsDirectory);
        if (folder == null) { Check("theme switch: the fixture has a folder to expand", false); return; }
        if (!folder.IsExpanded) vm.ToggleNodeCommand.Execute(folder);
        await WaitUntil(() => folder.IsExpanded && folder.Children.Count > 0, 3000);
        // A pipe run's chat: 300 tool rows and a dozen answers, like Reverse X phase 4.
        int chatBefore = chat.Messages.Count;
        for (int i = 0; i < 300; i++)
            chat.Messages.Add(new ChatMessage
            {
                Role = MessageRole.ToolAction, ToolKind = i % 3 == 0 ? "edit_file" : "read_file", ToolName = "read_file",
                ToolVerb = "Read", ToolTarget = $"src/File{i}.cs", Content = $"src/File{i}.cs", ToolSuccess = true, ToolElapsedTime = "0.2s",
            });
        for (int i = 0; i < 12; i++)
            chat.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"Step {i}: **done** — `src/File{i}.cs` updated.\n\n- one\n- two" });
        await Idle(500);

        var root = RootVisual(MainWin);
        var tree = Find<System.Windows.Controls.TreeView>(root).FirstOrDefault(t => ReferenceEquals(t.ItemsSource, vm.Tree));
        int Rows() => tree == null ? -1 : Find<System.Windows.Controls.TreeViewItem>(tree).Count(i => i.IsVisible);
        int rowsBefore = Rows(), nodesBefore = vm.Tree.Count, childBefore = folder.Children.Count;
        Log($"  explorer: {rowsBefore} rows on screen · {nodesBefore} root node(s) · {folder.Name} open with {childBefore} entries · chat {chat.Messages.Count} messages");
        string start = (string)Prop(themes, "CurrentThemeId");
        try
        {
            foreach (var id in new[] { "Classic", "NeonCyber", "Midnight" })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Invoke(themes, "Apply", id, false);
                long applyMs = sw.ElapsedMilliseconds;
                await Idle();
                long settledMs = sw.ElapsedMilliseconds;
                int rows = Rows();
                Check($"theme switch to {id}: the explorer keeps every file row and the open folder",
                    tree != null && rows == rowsBefore && vm.Tree.Count == nodesBefore && folder.IsExpanded && folder.Children.Count == childBefore,
                    $"rows {rowsBefore}→{rows} · roots {nodesBefore}→{vm.Tree.Count} · {folder.Name} open={folder.IsExpanded} entries {childBefore}→{folder.Children.Count}");
                Check($"theme switch to {id}: the window is usable again within 1.5 s (apply {applyMs} ms, settled {settledMs} ms)",
                    settledMs < 1500, $"apply {applyMs} ms · settled {settledMs} ms");
            }
        }
        finally
        {
            Invoke(themes, "Apply", start, false);
            while (chat.Messages.Count > chatBefore) chat.Messages.RemoveAt(chat.Messages.Count - 1);
            if (folder.IsExpanded) vm.ToggleNodeCommand.Execute(folder);
            MainVm.NavigateToCommand.Execute("Chat");
            await Idle(200);
        }
    }

    static async Task ExpandFolderCheck(string set)
    {
        var vm = MainVm.CodeEditorVM;
        await WaitUntil(() => vm.Tree.Count > 0 && vm.Tree[0].Children.Any(n => n.IsDirectory), 5000);
        var folder = vm.Tree.FirstOrDefault()?.Children.FirstOrDefault(n => n.IsDirectory && !n.IsExpanded);
        if (folder == null) { Check($"{set}/code-editor: the explorer shows a folder to open", false, "the fixture project has none"); return; }
        await Idle(100);   // the folder's row (and its bound child list) is on screen
        vm.ToggleNodeCommand.Execute(folder);
        await WaitUntil(() => folder.IsExpanded, 3000);
        Check($"{set}/code-editor: clicking a folder in the explorer opens it ({folder.Name})",
            folder.IsExpanded && folder.Children.Count > 0, $"expanded={folder.IsExpanded} children={folder.Children.Count}");
        if (folder.IsExpanded) vm.ToggleNodeCommand.Execute(folder);   // closed again: later renders match the golden
        await Idle(100);
    }

    /// <summary>The point is inside every scroll viewport above the element, and nothing above it is see-through.</summary>
    static bool InViewAndOpaque(DependencyObject el, Point inRoot, UIElement root)
    {
        for (var p = VisualTreeHelper.GetParent(el); p != null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p))
        {
            if (p is UIElement { Opacity: < 0.05 }) return false;
            if (p is ScrollContentPresenter sp)
            {
                Rect view;
                try { view = sp.TransformToAncestor(root).TransformBounds(new Rect(0, 0, sp.ActualWidth, sp.ActualHeight)); }
                catch (InvalidOperationException) { return false; }
                if (!view.Contains(inRoot)) return false;
            }
        }
        return el is not UIElement { Opacity: < 0.05 };
    }

    static bool IsWithin(DependencyObject hit, DependencyObject ancestor)
    {
        for (var d = hit; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    static string Describe(DependencyObject d)
    {
        if (d == null) return "nothing";
        string name = (d as FrameworkElement)?.Name ?? "";
        string label = System.Windows.Automation.AutomationProperties.GetName(d);
        if (string.IsNullOrEmpty(label) && d is ContentControl { Content: string s }) label = s;
        if (string.IsNullOrEmpty(label) && d is FrameworkElement { ToolTip: string t }) label = t;
        if (string.IsNullOrEmpty(label) && d is DependencyObject)
            label = Find<TextBlock>(d).Select(tb => tb.Text).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        return $"{d.GetType().Name}{(name.Length > 0 ? "#" + name : "")}{(label.Length > 0 ? " '" + label + "'" : "")}";
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    /// <summary>Every HttpClient the harness hands to an app service: refuses the request and remembers it. A
    /// refused request throws like an offline network, which every one of those services already handles.</summary>
    sealed class BlockingHandler : HttpMessageHandler
    {
        public const string Marker = "themetest-network-blocked";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (BlockedRequests) BlockedRequests.Add(request.RequestUri?.Host ?? "?");
            throw new HttpRequestException(Marker + ": " + request.RequestUri?.Host);
        }
    }

    static HttpClient Blocked() => new(new BlockingHandler()) { Timeout = TimeSpan.FromSeconds(5) };

    // ═══════════════════════════════════════════ setup ═══════════════════════════════════════════

    static async Task<int> RunAll()
    {
        Log("# Theme harness — " + (GoldenMode ? "GOLDEN capture" : "render + compare"));
        Log($"out: {OutRoot} · tolerance {Tolerance}/255 per channel · max {MaxChangedPct.ToString(System.Globalization.CultureInfo.InvariantCulture)} % changed pixels per page\n");

        // A fresh portable data root every run: the same settings, an empty chat DB, the same buddy.
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            Log("FATAL portable.txt is missing next to the exe — refusing to run against the real ~/.cluadex");
            return 2;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services]);
        Sp = services.BuildServiceProvider();

        Settings = Sp.GetRequiredService<SettingsService>();
        bool portable = Settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        Check("setup: portable data root (never the user's ~/.cluadex)", portable, Settings.DataRoot);
        if (!portable) return 2;
        Directory.CreateDirectory(Settings.DataRoot);

        // The debug log writes to ~/.cluadex/logs from its constructor (one line, as in every harness) — from
        // here on it writes into the fixture area: a FIXED path, because the Debug Log page shows it and its golden
        // must not depend on where this harness was built.
        var dbg = Sp.GetRequiredService<DebugLogService>();
        string logDir = Path.Combine(FixtureRoot, "fixture", "logs");
        try { if (Directory.Exists(logDir)) Directory.Delete(logDir, true); } catch { }
        SetField(dbg, "_logDir", logDir);

        // No network. Each replaced client is checked BEFORE anything can use it (a mock must prove itself).
        var update = Sp.GetRequiredService<AutoUpdateService>();
        var license = Sp.GetRequiredService<XmanLicenseService>();
        var hf = Sp.GetRequiredService<HuggingFaceService>();
        var spark = Sp.GetRequiredService<DgxSparkService>();
        var swaps = new (object Svc, string Field)[] { (update, "_httpClient"), (license, "_httpClient"), (hf, "_httpClient"), (spark, "_probe"), (spark, "_pull") };
        bool allBlocked = true;
        foreach (var (svc, field) in swaps)
        {
            SetField(svc, field, Blocked());
            allBlocked &= GetField(svc, field) is HttpMessageInvoker inv && IsBlocking(inv);
        }
        Check("setup: update check, license, Hugging Face and DGX Spark clients refuse every request", allBlocked);
        if (!allBlocked) return 2;
        // The update-attempt counter lives in ~/.cluadex — point it at the harness's own data root.
        var attemptsType = typeof(AutoUpdateService).Assembly.GetType("CluadeX.Services.UpdateAttemptLog");
        var load = attemptsType?.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (load != null) SetField(update, "_attempts", load.Invoke(null, [Settings.DataRoot]));

        Settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.SelectedModelPath = null;
            s.SelectedModelName = null;
            s.AutoExecuteCode = false;
            s.Language = "en";
            s.SidebarNavExpanded = false;
        });

        var loc = Sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("en");

        // The theme the app would apply at startup (App.OnStartup does this before MainWindow exists; a harness
        // never runs OnStartup).
        object themes = ThemeServiceOrNull();
        if (themes != null) Invoke(themes, "ApplySaved");
        Log($"theme service: {(themes == null ? "none (a build from before the theme work)" : "present · startup theme " + Prop(themes, "CurrentThemeId"))}");

        // A small project, so the title bar, the explorer and the editor have something real to show.
        string project = WriteFixtureProject();

        MainWin = Sp.GetRequiredService<CluadeX.MainWindow>();
        MainVm = (MainViewModel)MainWin.DataContext;
        MainWin.WindowStartupLocation = WindowStartupLocation.Manual;
        MainWin.Left = -20000; MainWin.Top = -20000;
        MainWin.Width = W; MainWin.Height = H;
        MainWin.ShowActivated = false;
        MainWin.ShowInTaskbar = false;
        MainWin.Show();

        var chat = MainVm.ChatVM;
        Check("setup: chat VM finished its startup load", await WaitUntil(() => chat.CurrentSession != null));
        Invoke(chat, "ApplyWorkingDirectory", project, false, false);
        FixtureProject = project;
        await MainVm.CodeEditorVM.OpenPathAsync(Path.Combine(project, "src", "Greeter.cs"));
        SeedChat(chat);
        FreezeVolatile();
        await Idle(300);

        var root = RootVisual(MainWin);
        Check("setup: the main window lays out at 1440×900",
            Math.Abs(((FrameworkElement)root).ActualWidth - W) < 1 && Math.Abs(((FrameworkElement)root).ActualHeight - H) < 1,
            $"{((FrameworkElement)root).ActualWidth}×{((FrameworkElement)root).ActualHeight}");

        if (GoldenMode) return await CaptureGolden();

        // 1. Midnight at startup vs the golden.
        string startTheme = themes == null ? "midnight" : ((string)Prop(themes, "CurrentThemeId")).ToLowerInvariant();
        var midnight = await RenderSet(startTheme);
        CompareWithGolden(startTheme, midnight);

        // The probe (opt-in): every token inverted LIVE on each page — what does not invert ignores the theme.
        if (ProbeMode && themes != null) await RunProbe();

        if (themes == null)
        {
            Log("\n(no ThemeService in this build — only the current look was rendered)");
            return Finish();
        }

        // The picker in Thai (the strings are longer): rendered for the eye, not compared.
        await RenderThaiPicker(startTheme);

        // 2. The theme service itself.
        await TestThemeService(themes);
        await ThemeSwitchKeepsWork(themes);

        // 3. Every other theme, applied LIVE (the way the picker does it), then Midnight again: the live round trip
        //    must land on the startup look.
        var all = ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        foreach (string id in all.Where(id => !id.Equals("Midnight", StringComparison.OrdinalIgnoreCase)))
        {
            if (OnlyThemes != null && !OnlyThemes.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            Invoke(themes, "Apply", id, true);
            await Idle(200);
            var set = await RenderSet(id.ToLowerInvariant());
            Check($"{id}: every page and dialog rendered", set.Count == Pages.Length + Dialogs.Length, $"{set.Count}");
        }
        Invoke(themes, "Apply", "Midnight", true);
        await Idle(200);
        var back = await RenderSet("midnight-roundtrip");
        CompareWithGolden("midnight-roundtrip", back);


        return Finish();
    }

    static bool IsBlocking(HttpMessageInvoker inv) => GetFieldDeep(inv, "_handler") is BlockingHandler;

    static object GetFieldDeep(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        return null;
    }

    static int Finish()
    {
        lock (BlockedRequests)
            Log($"\nnetwork: {BlockedRequests.Count} request(s) refused{(BlockedRequests.Count > 0 ? " → " + string.Join(", ", BlockedRequests.Distinct()) : "")}");
        Log($"\n## Result: {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    static object ThemeServiceOrNull()
    {
        var t = typeof(CluadeX.App).Assembly.GetType("CluadeX.Services.ThemeService");
        return t == null ? null : Sp.GetService(t);
    }

    static string WriteFixtureProject()
    {
        // Always the same path, whatever --out says: the project path is ON the rendered pages (title bar, Task
        // Manager's working directory), and the golden was captured with this one.
        string dir = Path.Combine(FixtureRoot, "fixture", "demo-project");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "README.md"), "# demo-project\n\nA tiny project the theme harness opens.\n");
        File.WriteAllText(Path.Combine(dir, "src", "Greeter.cs"),
            "using System;\n\nnamespace Demo;\n\n/// <summary>Says hello — in two languages.</summary>\n" +
            "public sealed class Greeter\n{\n    private readonly string _name;\n\n    public Greeter(string name) => _name = name;\n\n" +
            "    public string Hello(bool thai = false)\n    {\n        // A comment, a string, a number: the editor colours them all.\n" +
            "        int count = 42;\n        return thai ? $\"สวัสดี {_name} ({count})\" : $\"Hello {_name} ({count})\";\n    }\n}\n");
        File.WriteAllText(Path.Combine(dir, "package.json"), "{\n  \"name\": \"demo-project\",\n  \"version\": \"1.0.0\"\n}\n");
        return dir;
    }

    static readonly DateTime FixedTime = new(2026, 10, 1, 9, 30, 0);

    static void SeedChat(ChatViewModel chat)
    {
        chat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "Give the settings page a theme switch, please.", Timestamp = FixedTime });
        chat.Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Timestamp = FixedTime.AddMinutes(1),
            Content = "Here is the plan:\n\n1. Keep every colour in **one dictionary per theme**.\n2. Swap that dictionary at runtime — no restart.\n3. Remember the choice in `settings.json`.\n\n" +
                      "```csharp\npublic bool Apply(string id)\n{\n    var dict = new ResourceDictionary { Source = Uri(id) };\n    Swap(dict);\n    return true;\n}\n```\n\n> The default theme stays exactly as it looks today.",
        });
        foreach (var s in chat.Sessions) { s.UpdatedAt = FixedTime; s.CreatedAt = FixedTime; }
        if (chat.CurrentSession != null) chat.CurrentSession.Title = "Theme switch";
        Invoke(chat, "RebuildVisibleSessions");
    }

    /// <summary>What changes from run to run and has nothing to do with the look: the build stamp, the GPU poll, the
    /// spinning logo halo.</summary>
    static void FreezeVolatile()
    {
        if (MainWin.FindName("VersionChip") is TextBlock chip) chip.Text = "v0.0.0 · themetest";
        if (MainWin.FindName("LogoHalo") is RotateTransform halo) { halo.BeginAnimation(RotateTransform.AngleProperty, null); halo.Angle = 0; }
        (GetField(MainVm, "_gpuLiveTimer") as DispatcherTimer)?.Stop();
        (GetField(MainVm, "_licenseTimer") as DispatcherTimer)?.Stop();
        MainVm.LiveGpuStats = null;
        MainVm.ShowUpdateBar = false;
        MainVm.GpuStatus = "GPU";
        MainVm.ModelStatus = "No model loaded";
        FreezeDebugLog();
        FreezeAnimations();
    }

    /// <summary>The Debug Log page shows the app's own live log (timestamps, startup lines): replace it with a fixed
    /// set — one line per level and per coloured category — and stop the live tail.</summary>
    static void FreezeDebugLog()
    {
        var svc = Sp.GetRequiredService<DebugLogService>();
        typeof(DebugLogService).GetField("OnEntry", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(svc, null);
        foreach (var (prop, n) in new[] { ("WarningCount", 1), ("ErrorCount", 1), ("CriticalCount", 1) })
            typeof(DebugLogService).GetProperty(prop)?.SetValue(svc, n);
        var vm = MainVm.DebugLogVM;
        if (vm.Entries.Count == FixedLog.Length && vm.Entries.Select(e => e.Message).SequenceEqual(FixedLog.Select(e => e.Message))) return;
        vm.Entries.Clear();
        foreach (var e in FixedLog) vm.Entries.Add(e);
        vm.StatusMessage = "";
        foreach (var p in new[] { "WarningCount", "ErrorCount", "CriticalCount", "TotalCount" })
            try { Invoke(vm, "OnPropertyChanged", p); } catch { }
    }

    static readonly LogEntry[] FixedLog =
    {
        new() { Timestamp = FixedTime, Level = LogLevel.Trace, Category = "UI", Message = "Layout pass: 3 panels" },
        new() { Timestamp = FixedTime.AddSeconds(1), Level = LogLevel.Debug, Category = "FileIO", Message = "Opened src/Greeter.cs (19 lines)" },
        new() { Timestamp = FixedTime.AddSeconds(2), Level = LogLevel.Info, Category = "Agent", Message = "Session started · model: none" },
        new() { Timestamp = FixedTime.AddSeconds(3), Level = LogLevel.Info, Category = "Tool", Message = "read_file src/Greeter.cs → 612 bytes" },
        new() { Timestamp = FixedTime.AddSeconds(4), Level = LogLevel.Info, Category = "MCP", Message = "brainx: 42 tools ready" },
        new() { Timestamp = FixedTime.AddSeconds(5), Level = LogLevel.Warning, Category = "Provider", Message = "Rate limit at 80 % — slowing down" },
        new() { Timestamp = FixedTime.AddSeconds(6), Level = LogLevel.Error, Category = "Git", Message = "git status failed: not a repository", Exception = "System.InvalidOperationException: not a git repository" },
        new() { Timestamp = FixedTime.AddSeconds(7), Level = LogLevel.Critical, Category = "Update", Message = "Update check refused (offline harness)" },
    };

    /// <summary>The Time Machine's backdrop orbs drift forever (storyboards on their transforms) and its logo halo spins —
    /// park them, so two renders of the page are the same picture.</summary>
    static void FreezeAnimations()
    {
        var page = Find<UserControl>(MainWin).FirstOrDefault(u => u.GetType().Name == "TimeMachineView");
        if (page == null) return;
        foreach (var fe in Find<FrameworkElement>(page))
        {
            switch (fe.RenderTransform)
            {
                case TranslateTransform t:
                    t.BeginAnimation(TranslateTransform.XProperty, null); t.BeginAnimation(TranslateTransform.YProperty, null);
                    break;
                case RotateTransform r:
                    r.BeginAnimation(RotateTransform.AngleProperty, null); r.Angle = 0;
                    break;
                case ScaleTransform s:
                    s.BeginAnimation(ScaleTransform.ScaleXProperty, null); s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    break;
            }
        }
    }

    // ═══════════════════════════════════════════ rendering ═══════════════════════════════════════════

    static Visual RootVisual(Window w) => (Visual)VisualTreeHelper.GetChild(w, 0);

    static RenderTargetBitmap Snapshot(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    static byte[] Pixels(BitmapSource b)
    {
        var src = b.Format == PixelFormats.Pbgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Pbgra32, null, 0);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return px;
    }

    /// <summary>Render until two consecutive frames are identical (late async loads, entry animations), at most ~4 s.</summary>
    static async Task<RenderTargetBitmap> Stable(Func<(Visual v, int w, int h)> target, string label)
    {
        RenderTargetBitmap prev = null;
        int lastDiff = -1;
        for (int i = 0; i < 16; i++)
        {
            await Idle(i == 0 ? 350 : 220);
            var (v, w, h) = target();
            var cur = Snapshot(v, w, h);
            if (prev != null && prev.PixelWidth == cur.PixelWidth && prev.PixelHeight == cur.PixelHeight)
            {
                lastDiff = CountDiff(Pixels(prev), Pixels(cur), 0);
                if (lastDiff == 0) return cur;
            }
            prev = cur;
        }
        Log($"  note: {label} never settled — the last two frames differ in {lastDiff} px (an animation?)");
        return prev;
    }

    static void SavePng(BitmapSource b, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static BitmapSource LoadPng(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return dec.Frames[0];
    }

    /// <summary>Render every page and dialog into renders\&lt;set&gt;\ (or golden\). Returns page → saved PNG path.</summary>
    static async Task<Dictionary<string, string>> RenderSet(string set, string dirOverride = null)
    {
        string dir = dirOverride ?? Path.Combine(OutRoot, "renders", set);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Log($"\n## Rendering {set} → {dir}");
        var saved = new Dictionary<string, string>();
        var settingsVm = MainVm.SettingsVM;

        string last = null;
        foreach (var (key, nav, _) in Pages)
        {
            if (nav != last)
            {
                // Leave and come back: the page view is rebuilt from its DataTemplate, as a user's click would.
                MainVm.NavigateToCommand.Execute(nav == "Permissions" ? "Chat" : "Permissions");
                await Idle();
                MainVm.NavigateToCommand.Execute(nav);
                last = nav;
            }
            if (nav == "Settings") settingsVm.SelectedCategory = SettingsCategoryOf(key);
            FreezeVolatile();
            var bmp = await Stable(() => (RootVisual(MainWin), W, H), $"{set}/{key}");
            string path = Path.Combine(dir, key + ".png");
            SavePng(bmp, path);
            saved[key] = path;
            ClickableButtons(set, key);
            if (key == "code-editor")
            {
                // The empty state too: with no folder open, the explorer offers "Open Folder" — and must take the click.
                Invoke(MainVm.ChatVM, "ApplyWorkingDirectory", "", false, false);
                await Idle(200);
                ClickableButtons(set, "code-editor (no folder)");
                Invoke(MainVm.ChatVM, "ApplyWorkingDirectory", FixtureProject, false, false);
                await Idle(200);
                await ExpandFolderCheck(set);
                await EditorToolsCheck(set);
            }
        }
        settingsVm.SelectedCategory = "provider";
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle();

        foreach (var (key, _) in Dialogs)
        {
            Window win = null;
            try
            {
                win = CreateDialog(key);
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = -20000; win.Top = -20000;
                win.ShowActivated = false;
                win.ShowInTaskbar = false;
                win.Show();
                var bmp = await Stable(() =>
                {
                    var r = (FrameworkElement)RootVisual(win);
                    return (r, Math.Max(1, (int)Math.Ceiling(r.ActualWidth)), Math.Max(1, (int)Math.Ceiling(r.ActualHeight)));
                }, $"{set}/{key}");
                string path = Path.Combine(dir, key + ".png");
                SavePng(bmp, path);
                saved[key] = path;
            }
            catch (Exception ex) { Log($"  FAIL to render dialog {key}: {ex.GetType().Name}: {ex.Message}"); }
            finally { try { win?.Close(); } catch { } }
            await Idle();
        }

        ContactSheet(saved, Path.Combine(dir, "_contact.png"), set);
        Log($"rendered {saved.Count} images + _contact.png");
        return saved;
    }

    static async Task RenderThaiPicker(string set)
    {
        var loc = Sp.GetRequiredService<LocalizationService>();
        loc.SetLanguage("th");
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle();
        MainVm.NavigateToCommand.Execute("Settings");
        MainVm.SettingsVM.SelectedCategory = "general";
        FreezeVolatile();
        var bmp = await Stable(() => (RootVisual(MainWin), W, H), $"{set}/settings-general-th");
        string path = Path.Combine(OutRoot, "renders", set, "settings-general-th.png");
        SavePng(bmp, path);
        Log($"rendered the picker in Thai → {path}");
        loc.SetLanguage("en");
        MainVm.SettingsVM.SelectedCategory = "provider";
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle();
    }

    static Window CreateDialog(string key)
    {
        var loc = Sp.GetRequiredService<LocalizationService>();
        switch (key)
        {
            case "license":
                return new CluadeX.Views.LicenseWindow(new LicenseViewModel(
                    Sp.GetRequiredService<ActivationService>(), Sp.GetRequiredService<XmanLicenseService>(), loc, null));
            case "brainx-invite":
                return new CluadeX.Views.BrainXInviteWindow(new BrainXInviteViewModel(
                    Sp.GetRequiredService<McpServerManager>(), Settings, loc));
            case "dgx-spark":
                return new CluadeX.Views.DgxSparkWindow(new DgxSparkViewModel(Sp.GetRequiredService<DgxSparkService>(), Settings, loc));
            case "preview-pane":
                _ = Sp.GetRequiredService<PreviewViewModel>();   // PreviewViewModel.Current — what every pane binds to
                return new Window
                {
                    Width = 1040, Height = 780, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Content = new CluadeX.Views.Preview.PreviewPane(),
                };
            default: throw new ArgumentException(key);
        }
    }

    // ═══════════════════════════════════════════ golden + diff ═══════════════════════════════════════════

    static string GoldenDir => Path.Combine(OutRoot, "golden");

    static async Task<int> CaptureGolden()
    {
        if (Directory.Exists(GoldenDir) && Directory.EnumerateFiles(GoldenDir, "*.png").Any() && !Force)
        {
            Log($"FAIL  a golden baseline already exists in {GoldenDir} — pass --force to replace it (only ever from a build BEFORE the theme work)");
            return 1;
        }
        var first = await RenderSet("golden", GoldenDir);
        Check("golden: every page and dialog rendered", first.Count == Pages.Length + Dialogs.Length, $"{first.Count}");

        // The baseline is only worth something if rendering is deterministic: render everything again, compare.
        var second = await RenderSet("golden-recheck");
        Log("\n## Golden stability (a second render of the same build)");
        foreach (var (key, path) in first)
        {
            if (!second.TryGetValue(key, out var p2)) { Check($"stable: {key}", false, "not rendered twice"); continue; }
            var d = Diff(path, p2, null);
            Check($"stable: {key} renders the same twice", d.Pct <= MaxChangedPct, $"{d.Pct:F4} % changed, max Δ {d.MaxDelta}");
        }
        try { Directory.Delete(Path.Combine(OutRoot, "renders", "golden-recheck"), true); } catch { }
        File.WriteAllText(Path.Combine(GoldenDir, "README.txt"),
            "The CluadeX look BEFORE the theme work — rendered by tests/themetest (--golden) from the build at the commit below.\n" +
            "Every later run compares the Midnight theme against these PNGs (≤ 0.1 % changed pixels per page, tolerance 8/255).\n" +
            $"captured {DateTime.Now:yyyy-MM-dd HH:mm} · CluadeX.dll {typeof(CluadeX.App).Assembly.GetName().Version} · " +
            $"{typeof(CluadeX.App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\n", new UTF8Encoding(false));
        return Finish();
    }

    static void CompareWithGolden(string set, Dictionary<string, string> rendered)
    {
        Log($"\n## {set} vs golden");
        if (!Directory.Exists(GoldenDir)) { Check($"{set}: a golden baseline exists", false, GoldenDir); return; }
        string diffDir = Path.Combine(OutRoot, "renders", set, "diff");
        foreach (var (key, label) in Pages.Select(p => (p.Key, p.Label)).Concat(Dialogs))
        {
            string golden = Path.Combine(GoldenDir, key + ".png");
            if (!File.Exists(golden)) { Log($"INFO  {key}: no golden image"); continue; }
            if (!rendered.TryGetValue(key, out var mine)) { Check($"{set}: {key} rendered", false); continue; }
            var d = Diff(golden, mine, Path.Combine(diffDir, key + ".png"));
            string detail = $"{d.Pct:F4} % changed ({d.Changed} px), max Δ {d.MaxDelta}{(d.SizeMismatch ? ", SIZE DIFFERS " + d.Sizes : "")}";
            if (ExpectedToChange.Contains(key))
                Log($"INFO  {set}: {key} — expected to change (theme picker added): {detail}");
            else
                Check($"{set}: {key} matches the golden (≤ {MaxChangedPct.ToString(System.Globalization.CultureInfo.InvariantCulture)} %)",
                    !d.SizeMismatch && d.Pct <= MaxChangedPct, detail);
        }
    }

    sealed record DiffResult(double Pct, int Changed, int MaxDelta, bool SizeMismatch, string Sizes);

    static DiffResult Diff(string goldenPath, string minePath, string diffPng)
    {
        var a = LoadPng(goldenPath);
        var b = LoadPng(minePath);
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return new DiffResult(100, a.PixelWidth * a.PixelHeight, 255, true, $"{a.PixelWidth}×{a.PixelHeight} vs {b.PixelWidth}×{b.PixelHeight}");
        var pa = Pixels(a); var pb = Pixels(b);
        int changed = 0, maxDelta = 0;
        var mask = diffPng != null ? new byte[pa.Length] : null;
        for (int i = 0; i < pa.Length; i += 4)
        {
            int d = Math.Max(Math.Max(Math.Abs(pa[i] - pb[i]), Math.Abs(pa[i + 1] - pb[i + 1])),
                             Math.Max(Math.Abs(pa[i + 2] - pb[i + 2]), Math.Abs(pa[i + 3] - pb[i + 3])));
            if (d > maxDelta) maxDelta = d;
            bool hit = d > Tolerance;
            if (hit) changed++;
            if (mask != null)
            {
                if (hit) { mask[i] = 0x30; mask[i + 1] = 0x30; mask[i + 2] = 0xFF; mask[i + 3] = 0xFF; }
                else { mask[i] = (byte)(pa[i] / 4); mask[i + 1] = (byte)(pa[i + 1] / 4); mask[i + 2] = (byte)(pa[i + 2] / 4); mask[i + 3] = 0xFF; }
            }
        }
        if (mask != null && changed > 0)
            SavePng(BitmapSource.Create(a.PixelWidth, a.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, mask, a.PixelWidth * 4), diffPng);
        return new DiffResult(100.0 * changed / (a.PixelWidth * a.PixelHeight), changed, maxDelta, false, "");
    }

    static int CountDiff(byte[] a, byte[] b, int tol)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) > tol || Math.Abs(a[i + 1] - b[i + 1]) > tol || Math.Abs(a[i + 2] - b[i + 2]) > tol || Math.Abs(a[i + 3] - b[i + 3]) > tol)
                n++;
        return n;
    }

    /// <summary>All renders of one set on one sheet: 4 columns of 1/3-scale thumbnails with their names.</summary>
    static void ContactSheet(Dictionary<string, string> images, string path, string title)
    {
        const int cols = 4, tw = 480, th = 300, pad = 16, label = 22, header = 44;
        var order = Pages.Select(p => (p.Key, p.Label)).Concat(Dialogs).Where(x => images.ContainsKey(x.Key)).ToList();
        int rows = (order.Count + cols - 1) / cols;
        int sw = pad + cols * (tw + pad), sh = header + rows * (th + label + pad) + pad;
        var dv = new DrawingVisual();
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)), null, new Rect(0, 0, sw, sh));
            dc.DrawText(new FormattedText($"CluadeX · {title}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 20, Brushes.White, 1.0), new Point(pad, 10));
            for (int i = 0; i < order.Count; i++)
            {
                var (key, lbl) = order[i];
                var img = LoadPng(images[key]);
                double x = pad + (i % cols) * (tw + pad), y = header + (i / cols) * (th + label + pad);
                double s = Math.Min((double)tw / img.PixelWidth, (double)th / img.PixelHeight);
                double iw = img.PixelWidth * s, ih = img.PixelHeight * s;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33)), null, new Rect(x, y, tw, th));
                dc.DrawImage(img, new Rect(x + (tw - iw) / 2, y + (th - ih) / 2, iw, ih));
                dc.DrawText(new FormattedText(lbl, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, 13, new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xDA)), 1.0), new Point(x, y + th + 3));
            }
        }
        var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        SavePng(rtb, path);
    }

    // ═══════════════════════════════════════════ theme service ═══════════════════════════════════════════

    static async Task TestThemeService(object themes)
    {
        Log("\n## ThemeService");
        var asm = typeof(CluadeX.App).Assembly;
        var res = Application.Current.Resources;

        // T1 startup: a fresh install starts on Midnight, and the theme dictionary in App.Resources says so.
        Check("T1 a fresh install starts on Midnight", (string)Prop(themes, "CurrentThemeId") == "Midnight", (string)Prop(themes, "CurrentThemeId"));
        Check("T1 the merged theme dictionary is Midnight's (Theme.Id marker)", res["Theme.Id"] as string == "Midnight", res["Theme.Id"] as string);
        var ids = ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        Check("T1 three themes: Classic, Midnight, Neon Cyber LED", ids.OrderBy(x => x).SequenceEqual(new[] { "Classic", "Midnight", "NeonCyber" }), string.Join(",", ids));

        // T2 the token list in code exists in EVERY theme file, with the same kind of value, and no theme has extra keys.
        var tokensType = asm.GetType("CluadeX.Services.ThemeTokens");
        var tokenList = ((IEnumerable)tokensType?.GetProperty("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))?.Cast<string>().ToList() ?? new();
        Check("T2 the token catalogue in code is not empty", tokenList.Count >= 60, $"{tokenList.Count} tokens");
        var midnightDict = LoadThemeDictionary(themes, "Midnight");
        var midnightKeys = Keys(midnightDict);
        var legacyList = ((IEnumerable)tokensType?.GetProperty("Legacy", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))?.Cast<string>().ToList() ?? new();
        var unlisted = midnightKeys.Where(k => k != "Theme.Id" && !tokenList.Contains(k) && !legacyList.Contains(k)).ToList();
        Check("T2 every key in Midnight.xaml is listed in ThemeTokens.All or ThemeTokens.Legacy", unlisted.Count == 0, string.Join(", ", unlisted.Take(12)));
        foreach (string id in ids)
        {
            var dict = LoadThemeDictionary(themes, id);
            var keys = Keys(dict);
            var missing = tokenList.Where(k => !keys.Contains(k)).ToList();
            Check($"T2 {id}: defines every token in the catalogue", missing.Count == 0, missing.Count == 0 ? $"{tokenList.Count} tokens" : string.Join(", ", missing.Take(12)));
            var extra = keys.Except(midnightKeys).ToList();
            var gone = midnightKeys.Except(keys).ToList();
            Check($"T2 {id}: the same keys as Midnight (no key only one theme has)", extra.Count == 0 && gone.Count == 0,
                $"extra: {string.Join(",", extra.Take(8))} · missing: {string.Join(",", gone.Take(8))}");
            var wrongKind = keys.Where(k => midnightDict[k]?.GetType() != dict[k]?.GetType() && !(midnightDict[k] is Brush && dict[k] is Brush)).ToList();
            Check($"T2 {id}: every key holds the same kind of value as in Midnight", wrongKind.Count == 0, string.Join(", ", wrongKind.Take(8)));
            Check($"T2 {id}: names itself in Theme.Id", dict["Theme.Id"] as string == id, dict["Theme.Id"] as string);
        }

        // T3 live swap: ThemeChanged once, settings persisted, the shell's DynamicResource brushes replaced in place.
        var raised = new List<string>();
        var ev = themes.GetType().GetEvent("ThemeChanged");
        Action<string> handler = id => raised.Add(id);
        ev.AddEventHandler(themes, handler);
        var bgBefore = MainWin.Background;
        var activity = Find<RadioButton>(MainWin).First(r => r.Style == (Style)res["ActivityBarButton"] && r.IsChecked != true);
        var fgBefore = activity.Foreground;
        bool switched = (bool)Invoke(themes, "Apply", "Classic", true);
        await Idle(100);
        Check("T3 Apply(Classic) switches", switched && (string)Prop(themes, "CurrentThemeId") == "Classic");
        Check("T3 ThemeChanged raised once with the new id", raised.SequenceEqual(new[] { "Classic" }), string.Join(",", raised));
        Check("T3 App.Resources now holds Classic's dictionary", res["Theme.Id"] as string == "Classic", res["Theme.Id"] as string);
        Check("T3 the window background was re-resolved live (DynamicResource in MainWindow)",
            !ReferenceEquals(bgBefore, MainWin.Background) && ReferenceEquals(MainWin.Background, res["Bg.Window"]));
        Check("T3 a Theme.xaml style re-resolved live (activity bar foreground)",
            !ReferenceEquals(fgBefore, activity.Foreground) && ReferenceEquals(activity.Foreground, res["ActivityBar.Foreground"]),
            activity.Foreground?.ToString());
        string json = File.ReadAllText(Path.Combine(Settings.DataRoot, "settings.json"));
        Check("T3 the choice is saved in settings.json", json.Contains("\"Theme\": \"Classic\""), "Theme in settings.json");

        // T4 idempotent + bad input.
        raised.Clear();
        bool again = (bool)Invoke(themes, "Apply", "Classic", true);
        Check("T4 applying the current theme again is a no-op (no event, no reload)", !again && raised.Count == 0);
        bool bogus = (bool)Invoke(themes, "Apply", "../../evil", true);
        Check("T4 an unknown theme id is refused and nothing changes", !bogus && (string)Prop(themes, "CurrentThemeId") == "Classic" && raised.Count == 0);

        // T5 a tampered settings value at startup falls back to Midnight instead of failing.
        Settings.UpdateSettings(s => typeof(AppSettings).GetProperty("Theme")!.SetValue(s, "<script>"));
        string applied = (string)Invoke(themes, "ApplySaved");
        Check("T5 a tampered Theme value in settings.json starts Midnight", applied == "Midnight" && (string)Prop(themes, "CurrentThemeId") == "Midnight", applied);

        // T6 the picker in Settings › General: three cards, picking one applies it live and marks it.
        var svm = MainVm.SettingsVM;
        var options = ((IEnumerable)Prop(svm, "ThemeOptions"))?.Cast<object>().ToList() ?? new();
        Check("T6 the picker offers three themes", options.Count == 3, $"{options.Count}");
        var cmd = Prop(svm, "SelectThemeCommand") as ICommand;
        cmd?.Execute("NeonCyber");
        await Idle(100);
        Check("T6 picking Neon Cyber LED applies it live", (string)Prop(themes, "CurrentThemeId") == "NeonCyber");
        Check("T6 exactly the picked card is marked", options.Count(o => (bool)Prop(o, "IsSelected")) == 1
            && options.Any(o => (bool)Prop(o, "IsSelected") && (string)Prop(o, "Id") == "NeonCyber"));
        cmd?.Execute("NeonCyber");
        Check("T6 picking the same card twice changes nothing", (string)Prop(themes, "CurrentThemeId") == "NeonCyber");
        cmd?.Execute(null);
        cmd?.Execute("nope");
        Check("T6 a null / unknown pick is ignored", (string)Prop(themes, "CurrentThemeId") == "NeonCyber");

        // T7 en + th for every picker string.
        var loc = Sp.GetRequiredService<LocalizationService>();
        var missingTh = new List<string>();
        foreach (var o in options)
        {
            foreach (var p in new[] { "NameKey", "DescKey" })
            {
                string k = (string)Prop(o, p);
                loc.SetLanguage("th"); string th = loc.T(k);
                loc.SetLanguage("en"); string en = loc.T(k);
                if (string.IsNullOrWhiteSpace(th) || th == k || string.IsNullOrWhiteSpace(en) || en == k) missingTh.Add(k);
            }
        }
        foreach (var k in new[] { "settings.card.appearance", "settings.theme", "settings.themeDesc" })
        {
            loc.SetLanguage("th"); string th = loc.T(k);
            loc.SetLanguage("en"); string en = loc.T(k);
            if (th == k || en == k || th == en) missingTh.Add(k);
        }
        Check("T7 every picker string has an English and a Thai value", missingTh.Count == 0, string.Join(", ", missingTh));

        // T8 C# that builds brushes asks the service: a token brush comes back, an unknown key gets the fallback.
        var getBrush = themes.GetType().GetMethod("GetBrush");
        var b1 = getBrush?.Invoke(themes, ["Status.Danger", null]) as Brush;
        var fb = Brushes.Magenta;
        var b2 = getBrush?.Invoke(themes, ["No.Such.Token", fb]) as Brush;
        Check("T8 GetBrush returns the current theme's token brush", b1 != null && ReferenceEquals(b1, res["Status.Danger"]));
        Check("T8 GetBrush falls back for an unknown key instead of throwing", ReferenceEquals(b2, fb));

        ev.RemoveEventHandler(themes, handler);
        Invoke(themes, "Apply", "Midnight", true);
        await Idle(100);
    }

    static ResourceDictionary LoadThemeDictionary(object themes, string id)
    {
        var uri = (Uri)themes.GetType().GetMethod("DictionaryUri", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [id]);
        return new ResourceDictionary { Source = uri };
    }

    static HashSet<string> Keys(ResourceDictionary d)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in d.Keys) if (k is string s) set.Add(s);
        foreach (var m in d.MergedDictionaries) set.UnionWith(Keys(m));
        return set;
    }
}
