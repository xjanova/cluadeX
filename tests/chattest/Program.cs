using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// The chat page, driven for real: CluadeX's own DI graph (App.ConfigureServices) builds the shipping
/// ChatViewModel, the shipping ChatView renders it in an off-screen window, and every check is made on what a
/// user would see or click — hit-tests at the centre of buttons, the rows the ListBox actually shows, the rows
/// the chat database actually holds, the history the provider is actually sent.
///
/// No network, no model: the active provider is replaced by <see cref="FakeProvider"/>, a scripted offline
/// provider (reply / throw a given HTTP status / hang until cancelled) that records every request it receives.
/// portable.txt keeps settings and codex.db next to this exe — never the user's ~/.cluadex.
///
///   dotnet build CluadeX/CluadeX.csproj [-p:OutDir=X\]   then   cd tests/chattest && dotnet run [-p:CluadeXBin=X\]
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;
    static readonly StringBuilder Report = new();
    static readonly List<string> Failures = new();

    static ChatViewModel Vm;
    static ChatView View;
    static Window Win;
    static ListBox ChatList;
    static FakeProvider Fake;
    static ChatPersistenceService Persist;
    static DatabaseService Db;
    static SettingsService Settings;
    static LocalizationService LocSvc;
    static AiProviderManager Providers;
    static ServiceProvider Sp;
    static Dictionary<string, DateTime?> ProfileBefore;
    static readonly List<string> ConfirmTitles = new();
    static bool ConfirmAnswer = true;

    [STAThread]
    static int Main()
    {
        // This harness checks real command output, so it opts out of the default dry-run on purpose (ProcessLaunchPolicy).
        // A new harness must not copy this line unless real runs are its point; the guards still check every command.
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);

        var app = new CluadeX.App();
        app.InitializeComponent();                       // App.xaml resources: the styles ChatView uses
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
            finally
            {
                try { Win?.Close(); } catch { }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chat-report.md"), Report.ToString(), new UTF8Encoding(false));
                Console.WriteLine($"\nreport: {Path.Combine(AppContext.BaseDirectory, "chat-report.md")}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return rc;
    }

    // ═══════════════════════════════════════════ plumbing ═══════════════════════════════════════════

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else { Fail++; Failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 && !ok ? "   [" + detail + "]" : "")}");
    }

    static async Task Render()
    {
        for (int i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Win.UpdateLayout();
        }
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

    static string L(string key) => LocalizedResources.Instance[key];
    static string LocF(string key, params object[] args) => string.Format(L(key), args);

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

    static ListBoxItem Container(ChatMessage m) => ChatList.ItemContainerGenerator.ContainerFromItem(m) as ListBoxItem;

    /// <summary>Does a mouse click at the centre of <paramref name="target"/> land on it (or inside it)? Uses the
    /// INPUT hit test — the one the mouse uses (it skips collapsed, hidden, disabled and non-hit-testable
    /// elements); VisualTreeHelper.HitTest still reports a collapsed element's stale geometry.</summary>
    static bool HitReaches(FrameworkElement target, out string chain)
    {
        var centre = target.TransformToAncestor(View).Transform(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        DependencyObject d = View.InputHitTest(centre) as DependencyObject;
        var names = new List<string>();
        while (d != null)
        {
            if (d == target) { chain = "reaches target"; return true; }
            if (names.Count < 6) names.Add(d.GetType().Name + (d is FrameworkElement fe && fe.DataContext is ChatMessage cm ? $"({cm.Role})" : ""));
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        chain = string.Join(" < ", names);
        return false;
    }

    /// <summary>A TextBlock's text, including one built from Inlines (markdown) — .Text is empty for those.</summary>
    static string TextOf(TextBlock t) => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text;

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

    static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o);

    static void SetField(object o, string name, object value) =>
        o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);

    /// <summary>The harness types and runs back to back: tell the screen guard nobody is using the window, so a pipe run
    /// brings its chat on screen (the behaviour these checks are about). ScreenTests covers the guard itself.</summary>
    static void ScreenFree()
    {
        SetField(Vm, "_lastUserActivityUtc", DateTime.MinValue);
        SetField(Vm, "_lastScreenTakeUtc", DateTime.MinValue);
    }

    static ChatViewModel FreeVm() { ScreenFree(); return Vm; }

    /// <summary>The window shows the project of the chat on screen: the VM's folder and the app's folder are that chat's.</summary>
    static bool WindowMatchesChat()
    {
        var fs = Sp.GetRequiredService<FileSystemService>();
        string? chat = Vm.CurrentSession?.ProjectPath;
        if (string.IsNullOrWhiteSpace(chat) || !Directory.Exists(chat)) return true;   // no folder of its own: nothing to match
        return SamePath(Vm.WorkingDirectory, chat) && SamePath(fs.AppWorkingDirectory, chat);
    }

    static async Task Send(string text)
    {
        Vm.UserInput = text;
        Vm.SendMessageCommand.Execute(null);
        await Task.Delay(30);
    }

    static async Task<bool> WaitIdle(int timeoutMs = 20000)
    {
        bool ok = await WaitUntil(() => !Vm.IsGenerating, timeoutMs);
        await Render();
        return ok;
    }

    static async Task FreshChat()
    {
        Vm.NewSession();
        await Render();
    }

    // ═══════════════════════════════════════════ setup ═══════════════════════════════════════════

    static async Task<int> RunAll()
    {
        Log("# Chat page harness\n");

        // ~/.cluadex must not change — this harness used to run the app's whole startup (see Z1 at the end).
        ProfileBefore = ProfileSnapshot();

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services]);
        var sp = services.BuildServiceProvider();
        Sp = sp;

        Settings = sp.GetRequiredService<SettingsService>();
        Check("setup: portable data root (never the user's ~/.cluadex)",
            Settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), Settings.DataRoot);
        if (!Settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) return 2;

        // A fresh chat database every run, and settings that can never load a model. A pooled SQLite handle
        // from anything that touched the file earlier in this process would block the delete — release them.
        Directory.CreateDirectory(Settings.DataRoot);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Settings.DataRoot, "codex.db*")) File.Delete(f);
        Settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.SelectedModelPath = null;
            s.SelectedModelName = null;
            s.AutoExecuteCode = false;
            s.ShowAgentInternals = false;
            s.AutonomousLoopEnabled = false;
            s.Language = "en";
        });

        LocSvc = sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(LocSvc);
        LocSvc.SetLanguage("en");

        Db = sp.GetRequiredService<DatabaseService>();
        Persist = sp.GetRequiredService<ChatPersistenceService>();
        Providers = sp.GetRequiredService<AiProviderManager>();
        Vm = sp.GetRequiredService<ChatViewModel>();
        Vm.Confirm = (text, title) => { ConfirmTitles.Add(title); return ConfirmAnswer; };

        View = new ChatView { DataContext = Vm };
        Win = new Window
        {
            Content = View, Width = 1100, Height = 800, Left = -5000, Top = -5000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false,
        };
        Win.Show();
        ChatList = (ListBox)View.FindName("ChatList");
        Check("setup: chat VM finished its startup load", await WaitUntil(() => Vm.CurrentSession != null));
        await Render();

        await TestProviderStartup();          // L1 (before the fake provider replaces the real one)

        Fake = new FakeProvider();
        SetField(Providers, "_activeProvider", Fake);

        await TestPermissionPrompt();         // C1
        await TestStatusRows();               // L4 (screen)
        await TestThinkingBlock();            // L8 (screen)
        await TestCompaction();               // C2 + L1 slash commands
        await TestPersistenceAndHistory();    // L4 (database + history)
        await TestRetryLogic();               // L3 + L7
        await TestHoverRetry();               // L10
        await TestBackgroundRuns();           // R (was L2: switching mid-turn stopped the turn)
        await TestTypeWhileWorking();         // W: typing while a reply runs reaches it
        await TestChatOpensItsProject();      // F: an old chat brings its project back
        await TestAttachedImages();           // X: images pasted / dropped / picked into a message
        await TestDictation();                // V: speaking instead of typing (the Chrome speech bridge)
        await TestContextWindow();            // L6 + L7 label
        await TestAutoRunToggle();            // U5
        await TestScrollFollow();             // U6
        await TestCodeBlockStop();            // L12
        await TestNewChatNavigation();        // MINOR: title-bar New chat
        TestPureHelpers();                    // L3 classifier, L8 dedupe, U9 redaction, U2 palette
        await TestPipeRuns();                 // T5: pipe runs watched live, isError, project restored
        await TestPipeChats();                // T6: list_chats / continue_chat over the pipe
        await TestAcceptEdits();              // T5: Accept edits switch
        await TestAutonomousBuildGate();      // T5: the Autonomous loop's build/test after unseen edits
        await TestThumbnail();                // T5: tool-row image thumbnail
        await TestAskUserAndPinnedFolder();   // Q + S
        await TestControlStrip();             // U7: model / approvals / budget / spend under the input box
        await TestUsageWindow();              // U8: the spend window — paid vs without savings, credit left
        TestAutoCompact();                    // U9: the compaction toast's "compact by itself" tick
        await TestNarrowComposer();           // U10: the composer docked beside the editor
        await TestLiveEffort();               // U11: "auto → level" shown while a run works; Pause → Resume
        await TestPlanWindow();               // U12: the plan window — live plan, add items, go back, branch
        await TestToolGrouping();             // G: tool rows fold together
        await TestScreenGuard();              // S: one screen, many chats (follow tick, screen guard, draft rows)
        await TestWatchPage();                // W: the Watch page
        await TestStartupFollowsProject();    // F6: the chat restored at startup opens its own project (last: a 2nd VM)

        var changed = ProfileBefore.Where(kv => ProfileSnapshot().TryGetValue(kv.Key, out var a) && a != kv.Value).Select(kv => kv.Key).ToList();
        Check("Z1 the harness never wrote ~/.cluadex (install.json, mcp-host.log, mcp_servers.json, settings.json)", changed.Count == 0, string.Join(", ", changed));

        Log($"\n## Result: {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════════════════ L1 ═══════════════════════════════════════════

    static async Task TestProviderStartup()
    {
        Log("\n## L1 — the active provider is initialised at startup; nothing loads a model implicitly");
        var t1 = Providers.InitializeActiveOnceAsync();
        var t2 = Providers.InitializeActiveOnceAsync();
        Check("L1 startup initialise is one shared task (idempotent)", ReferenceEquals(t1, t2));
        await t1;
        bool ready = await Providers.EnsureActiveReadyAsync();
        Check("L1 Local provider without a model reports not-ready instead of loading weights",
            !ready && !Providers.ActiveProvider.IsLoading);
    }

    // ═══════════════════════════════════════════ C1 ═══════════════════════════════════════════

    static (Button allow, Button deny) PermissionButtons(ChatMessage perm)
    {
        var c = Container(perm);
        var buttons = c == null ? new List<Button>() : Find<Button>(c).ToList();
        return (buttons.FirstOrDefault(b => Equals(b.Content, L("chat.allow"))),
                buttons.FirstOrDefault(b => Equals(b.Content, L("chat.deny"))));
    }

    static ChatMessage AddPrompt(List<bool> answers, string text = "Allow run_command to run `npm test`?")
    {
        var perm = new ChatMessage { Role = MessageRole.PermissionRequest, Content = text, PermissionCallback = answers.Add };
        Vm.Messages.Add(perm);
        return perm;
    }

    static async Task TestPermissionPrompt()
    {
        Log("\n## C1 — an inline permission prompt can be answered with the mouse");
        await FreshChat();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "run the tests please" });
        var answers = new List<bool>();
        var perm = AddPrompt(answers);
        await Render();

        var (allow, deny) = PermissionButtons(perm);
        Check("C1 Allow and Deny are rendered and visible", allow is { IsVisible: true } && deny is { IsVisible: true });
        if (allow == null || deny == null) return;

        Check("C1 click at the centre of Allow lands on Allow", HitReaches(allow, out var c1), c1);
        Check("C1 click at the centre of Deny lands on Deny", HitReaches(deny, out var c2), c2);

        // The bug: the normal bubble (opaque, #313244) was drawn over the prompt. No visible opaque bubble may remain.
        var container = Container(perm);
        bool opaqueBubble = Find<Border>(container).Any(b => b.IsVisible && b.Background is SolidColorBrush sb
            && sb.Color.A == 255 && sb.Color == Color.FromRgb(0x31, 0x32, 0x44));
        Check("C1 no normal chat bubble is drawn over the prompt", !opaqueBubble);

        Click(allow);
        await Render();
        Check("C1 Allow resolves the waiting agent with 'allow', exactly once", answers.SequenceEqual(new[] { true }), string.Join(",", answers));
        Check("C1 answered prompt hides its buttons", !allow.IsVisible && !deny.IsVisible);
        var outcome = Find<TextBlock>(Container(perm)).FirstOrDefault(t => t.IsVisible && t.Text == L("chat.perm.allowed"));
        Check("C1 answered prompt shows the localized outcome (no literal '*Allowed*' appended)",
            outcome != null && !perm.Content.Contains("*Allowed*"));
        Check("C1 a second answer is ignored", !perm.ResolvePermission(PermissionOutcome.Denied) && answers.Count == 1);

        var answers2 = new List<bool>();
        var perm2 = AddPrompt(answers2, "Allow edit_file to write: src/app.cs?");
        await Render();
        var (_, deny2) = PermissionButtons(perm2);
        Check("C1 second prompt: click at the centre of Deny lands on Deny", deny2 != null && HitReaches(deny2, out _));
        if (deny2 != null) Click(deny2);
        await Render();
        Check("C1 Deny resolves the agent with 'deny'", answers2.SequenceEqual(new[] { false }));

        // Thai UI: the buttons speak Thai
        LocSvc.SetLanguage("th");
        var answers3 = new List<bool>();
        var perm3 = AddPrompt(answers3);
        await Render();
        var (allowTh, _) = PermissionButtons(perm3);
        Check("C1 Thai UI: Allow button reads 'อนุญาต'", allowTh != null && Equals(allowTh.Content, "อนุญาต"),
            allowTh?.Content?.ToString() ?? "not found");
        LocSvc.SetLanguage("en");
        await Render();

        // Clearing the chat with a prompt still open dismisses it (the agent must not wait forever).
        ConfirmAnswer = true;
        Vm.ClearChatCommand.Execute(null);
        await WaitUntil(() => Vm.Messages.Count == 0);
        Check("C1 clearing the chat dismisses an open prompt → agent gets 'deny'",
            answers3.SequenceEqual(new[] { false }) && perm3.PermissionOutcome == PermissionOutcome.Dismissed);
    }

    // ═══════════════════════════════════════════ L4 (screen) ═══════════════════════════════════════════

    static async Task TestStatusRows()
    {
        Log("\n## L4 — status lines are AgentStatus rows: Steps hides finished ones, the live one stays");
        await FreshChat();
        Vm.ShowAgentInternals = false;
        var done = new ChatMessage { Role = MessageRole.AgentStatus, Content = "⏵ Reading Program.cs" };
        var live = new ChatMessage { Role = MessageRole.AgentStatus, Content = "⏳ Waiting for response...", IsStreaming = true };
        var answer = new ChatMessage { Role = MessageRole.Assistant, Content = "Here is the answer." };
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "q" });
        Vm.Messages.Add(done);
        Vm.Messages.Add(answer);
        Vm.Messages.Add(live);
        await Render();
        Check("L4 Steps off: a finished status line is hidden", Container(done)?.Visibility == Visibility.Collapsed);
        Check("L4 Steps off: the live status line stays visible", Container(live)?.Visibility == Visibility.Visible);
        Check("L4 Steps off: the answer is visible", Container(answer)?.Visibility == Visibility.Visible);
        Vm.ShowAgentInternals = true;
        await Render();
        Check("L4 Steps on: the finished status line shows", Container(done)?.Visibility == Visibility.Visible);
        Vm.ShowAgentInternals = false;
    }

    // ═══════════════════════════════════════════ L8 (screen) ═══════════════════════════════════════════

    static async Task TestThinkingBlock()
    {
        Log("\n## L8 — thinking renders as a collapsed block above the reply, not on top of it");
        await FreshChat();
        var reply = new ChatMessage { Role = MessageRole.Assistant, Content = "The answer is 42.", ThinkingContent = "Let me work it out step by step." };
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "q" });
        Vm.Messages.Add(reply);
        await Render();
        var c = Container(reply);
        var header = c == null ? null : Find<TextBlock>(c).FirstOrDefault(t => t.IsVisible && t.Text == L("chat.thinkingToggle"));
        var body = c == null ? null : Find<TextBlock>(c).FirstOrDefault(t => t.Text == "Let me work it out step by step.");
        Check("L8 the thinking header is visible on the reply", header != null);
        // Collapsed content inside a ScrollViewer is not even materialised — either way it is not on screen.
        Check("L8 the thinking text starts collapsed", body == null || !body.IsVisible);
        if (header != null)
        {
            Check("L8 the thinking header is clickable (not covered by the bubble)", HitReaches(header, out var chain), chain);
            double headerBottom = header.TransformToAncestor(View).Transform(new Point(0, header.ActualHeight)).Y;
            var replyText = Find<TextBlock>(c).FirstOrDefault(t => t.IsVisible && TextOf(t).Contains("The answer is 42"));
            Check("L8 the reply text is rendered", replyText != null);
            if (replyText != null)
            {
                double replyTop = replyText.TransformToAncestor(View).Transform(new Point(0, 0)).Y;
                Check("L8 the reply text sits below the thinking block", replyTop >= headerBottom, $"header bottom {headerBottom:F0}, reply top {replyTop:F0}");
                Check("L8 the reply text is reachable by the mouse", HitReaches(replyText, out var chain2), chain2);
            }

            // Clicking the header expands the block (the handler sits on the header's StackPanel).
            var headerRow = VisualTreeHelper.GetParent(header) as UIElement;
            headerRow?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = headerRow });
            await Render();
            var shown = Find<TextBlock>(Container(reply)).FirstOrDefault(t => t.Text == "Let me work it out step by step.");
            Check("L8 clicking the header shows the thinking text", reply.IsThinkingExpanded && shown is { IsVisible: true });
        }
    }

    // ═══════════════════════════════════════════ C2 ═══════════════════════════════════════════

    static async Task TestCompaction()
    {
        Log("\n## C2 — Compact never deletes: it adds a summary marker; only the model's view shrinks");
        await FreshChat();
        var originals = new List<ChatMessage>();
        for (int i = 1; i <= 15; i++)
        {
            originals.Add(new ChatMessage { Role = MessageRole.User, Content = $"question {i} about file{i}.cs" });
            if (i % 5 == 0)
                originals.Add(new ChatMessage { Role = MessageRole.ToolAction, Content = $"Read file{i}.cs", ToolName = "Read", ToolSummary = $"read file{i}.cs", ToolSuccess = true });
            originals.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"answer {i}" });
        }
        foreach (var m in originals) Vm.Messages.Add(m);
        Vm.SaveNow();
        string sessionId = Vm.CurrentSession!.Id;
        int before = Vm.Messages.Count;

        // /compact must work while the provider is NOT ready (it was blocked behind "not configured").
        Fake.IsReady = false;
        ConfirmTitles.Clear();
        await Send("/compact");
        await Render();
        Fake.IsReady = true;

        var markers = Vm.Messages.Where(m => m.Role == MessageRole.ContextSummary).ToList();
        Check("C2 /compact runs with the provider not ready (slash commands come first)", markers.Count == 1);
        Check("C2 no message was removed from the screen", originals.All(o => Vm.Messages.Contains(o)) && Vm.Messages.Count == before + 1,
            $"{Vm.Messages.Count} on screen, {before} before");
        Check("C2 nothing destructive → no confirmation dialog", ConfirmTitles.Count == 0);
        Check("C2 the 'not configured' error did not appear", !Vm.Messages.Any(m => m.HasError));

        var stored = Persist.LoadSession(sessionId);
        Check("C2 the database still holds every message + the marker",
            stored != null && stored.Messages.Count == before + 1 && stored.Messages.Count(m => m.Role == MessageRole.ContextSummary) == 1,
            $"stored {stored?.Messages.Count}");

        var modelView = ChatViewModel.BuildModelHistory(Vm.Messages);
        Check("C2 the model is sent the summary first", modelView.Count > 0 && modelView[0].Role == MessageRole.User
            && modelView[0].Content.Contains("Summary") && modelView[0].Content.Contains("question 1 about file1.cs"));
        Check("C2 the model is sent only the recent messages after it", modelView.Count <= 12 && modelView.Count >= 9,
            $"{modelView.Count} messages");
        Check("C2 the latest answer is still in the model's view", modelView.Last().Content == "answer 15");

        // The chat list virtualizes (only rows on screen exist): bring the marker into view first.
        ChatList.ScrollIntoView(markers[0]);
        await Render();
        var markerContainer = Container(markers[0]);
        await Render();
        Check("C2 the marker renders as a visible divider", markerContainer?.Visibility == Visibility.Visible
            && Find<TextBlock>(markerContainer).Any(t => t.IsVisible && t.Text == L("chat.compact.markerTitle")));

        // Compacting again carries the first summary forward.
        for (int i = 16; i <= 30; i++)
        {
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = $"question {i}" });
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"answer {i}" });
        }
        Vm.CompactCommand.Execute(null);
        await Render();
        var view2 = ChatViewModel.BuildModelHistory(Vm.Messages);
        Check("C2 a second compaction keeps the first summary", view2[0].Content.Contains("question 1 about file1.cs") && view2[0].Content.Contains("question 16"));
        Check("C2 two markers, transcript still complete", Vm.Messages.Count(m => m.Role == MessageRole.ContextSummary) == 2 && originals.All(o => Vm.Messages.Contains(o)));

        // /new and /clear also bypass the readiness check
        Fake.IsReady = false;
        string oldId = Vm.CurrentSession!.Id;
        await Send("/new");
        await Render();
        Check("C2/L1 /new works while the provider is not ready", Vm.CurrentSession!.Id != oldId && !Vm.Messages.Any(m => m.HasError));
        Fake.IsReady = true;
    }

    // ═══════════════════════════════════════════ L4 (db + history) ═══════════════════════════════════════════

    static async Task TestPersistenceAndHistory()
    {
        Log("\n## L4 — status lines, notices and error bubbles are neither saved nor sent to the model");
        await FreshChat();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "hello" });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.AgentStatus, Content = "⏵ Reading a.cs" });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "hi there" });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "**Error** boom", HasError = true });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "📁 Opened project" });
        Vm.SaveNow();
        var stored = Persist.LoadSession(Vm.CurrentSession!.Id);
        Check("L4 AgentStatus rows are not written to the chat database", stored != null && stored.Messages.All(m => m.Role != MessageRole.AgentStatus));

        // A database written by an older build: status lines stored as Assistant rows.
        var legacy = new ChatSession { Title = "legacy" };
        legacy.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "fix it" });
        legacy.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "⏵ Reading Program.cs" });
        legacy.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "✓ 120 tokens · 3.1s · 38.7 tok/s" });
        legacy.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "Done — I fixed it." });
        Db.SaveSession(legacy);
        var reread = Db.LoadSession(legacy.Id)!;
        Check("L4 legacy status rows are read back as AgentStatus", reread.Messages.Count(m => m.Role == MessageRole.AgentStatus) == 2);
        var hist = ChatViewModel.BuildModelHistory(reread.Messages);
        Check("L4 legacy status rows are not sent to the model", hist.Count == 2 && hist[1].Content == "Done — I fixed it.",
            string.Join(" | ", hist.Select(h => h.Content)));

        // An agent map card survives a reopen as a card (its nodes), not only as its text summary.
        var mapChat = new ChatSession { Title = "agent map" };
        var map = new ChatMessage { Role = MessageRole.System, Content = "🧭 map", AgentMapHeader = "Agent map · 2 sub-agents · 1 done",
            AgentNodes = new()
            {
                new AgentNode { Id = 1, Label = "Sub-agent #1", Kind = "explore", Task = "read auth", State = "done", Action = "found it", ToolCalls = 3, Elapsed = "7s" },
                new AgentNode { Id = 2, Label = "Sub-agent #2", Kind = "explore", Task = "read api", Action = "Reading api.dart", ToolCalls = 1, Elapsed = "2s" },
            } };
        mapChat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "split it" });
        mapChat.Messages.Add(map);
        Db.SaveSession(mapChat);
        var mapBack = Db.LoadSession(mapChat.Id)!.Messages.FirstOrDefault(m => m.IsAgentMap);
        Check("L4 an agent map card comes back as a card with its nodes and header after a reopen",
            mapBack != null && mapBack.AgentNodes!.Count == 2 && mapBack.AgentMapHeader == map.AgentMapHeader
            && mapBack.AgentNodes[0].IsDone && mapBack.AgentNodes[0].ToolCalls == 3 && mapBack.ToolName == null,
            mapBack == null ? "no card" : $"{mapBack.AgentNodes?.Count} nodes · '{mapBack.AgentMapHeader}'");
        Check("L4 a node saved mid-run comes back stopped, not spinning forever", mapBack?.AgentNodes![1].IsFailed == true && mapBack.AgentNodes[1].State == "stopped");
        Check("L4 the agent map card is not sent to the model", ChatViewModel.BuildModelHistory(mapChat.Messages).All(m => !m.IsAgentMap));

        var hist2 = ChatViewModel.BuildModelHistory(Vm.Messages);
        Check("L4 the model history has no status line, error bubble or UI notice",
            hist2.Count == 2 && hist2.All(m => m.Role is MessageRole.User or MessageRole.Assistant) && !hist2.Any(m => m.HasError),
            string.Join(" | ", hist2.Select(h => $"{h.Role}:{h.Content}")));
    }

    // ═══════════════════════════════════════════ L3 + L7 ═══════════════════════════════════════════

    static async Task TestRetryLogic()
    {
        Log("\n## L3 — auto-retry: decided by status/type, never duplicates the question, stops on Stop");
        await FreshChat();

        // 503 then success → retried once, same question, one user bubble
        Fake.Script.Enqueue(FakeStep.Throw(new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable)));
        Fake.Script.Enqueue(FakeStep.Reply("hello", " there"));
        int callsBefore = Fake.Calls.Count;
        await Send("q1");
        Check("L3 the turn finishes", await WaitIdle(30000));
        var calls = Fake.Calls.Skip(callsBefore).ToList();
        Check("L3 a 503 is retried once", calls.Count == 2, $"{calls.Count} calls");
        Check("L3 the retry sends the same question", calls.All(c => c.Input == "q1"));
        Check("L3 the question is not duplicated on screen", Vm.Messages.Count(m => m.Role == MessageRole.User && m.Content == "q1") == 1);
        Check("L3 the retry's history equals the first attempt's (no duplicate, no error bubble)",
            calls.Count == 2 && calls[0].History.SequenceEqual(calls[1].History), calls.Count == 2 ? $"{calls[0].History.Count} vs {calls[1].History.Count}" : "");
        Check("L3 the reply arrives after the retry", Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant && !m.HasError)?.Content == "hello there");
        Check("L7 per-turn stats are set", !string.IsNullOrWhiteSpace(Vm.LastTurnStats), Vm.LastTurnStats);

        // 400 "prompt is too long: 215000 tokens" → NOT retried (the old substring check matched "500")
        callsBefore = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Throw(new HttpRequestException("prompt is too long: 215000 tokens > 200000 maximum", null, HttpStatusCode.BadRequest)));
        await Send("q2");
        Check("L3 a 400 turn finishes", await WaitIdle());
        Check("L3 'prompt is too long: 215000 tokens' (HTTP 400) is not retried", Fake.Calls.Count - callsBefore == 1, $"{Fake.Calls.Count - callsBefore} calls");
        var err = Vm.Messages.LastOrDefault(m => m.HasError);
        Check("L3 the failed reply offers Retry and shows the provider's reason, not a raw exception",
            err != null && err.CanRetry && err.Content.Contains("prompt is too long") && !err.Content.Contains("HttpRequestException"));

        // Stop during the countdown → no retry
        callsBefore = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Throw(new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway)));
        Fake.Script.Enqueue(FakeStep.Reply("should not be sent"));
        await Send("q3");
        bool countingDown = await WaitUntil(() => Vm.Messages.Any(m => m.IsRetrying));
        Check("L3 a retry countdown is shown", countingDown);
        Vm.StopGenerationCommand.Execute(null);
        Check("L3 Stop ends the turn during the countdown", await WaitIdle(5000));
        await Task.Delay(3500);
        Check("L3 nothing is retried after Stop", Fake.Calls.Count - callsBefore == 1, $"{Fake.Calls.Count - callsBefore} calls");
        Fake.Script.Clear();

        // What the model is sent: no status lines / error bubbles / notices
        callsBefore = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Reply("ok"));
        await Send("q4");
        await WaitIdle();
        var last = Fake.Calls.Last();
        Check("L4 the history sent to the model has only User/Assistant turns from the conversation",
            last.History.All(h => h.Role is MessageRole.User or MessageRole.Assistant)
            && !last.History.Any(h => h.Content.StartsWith("⏳") || h.Content.StartsWith("⏵") || h.Content.StartsWith("✓") || h.Content.StartsWith("**")),
            string.Join(" | ", last.History.Select(h => $"{h.Role}:{h.Content}")));
        Check("L4 the waiting/stats rows of finished turns are AgentStatus", Vm.Messages.Where(m => m.Content.StartsWith("⏳") || m.Content.StartsWith("✓ ")).All(m => m.Role == MessageRole.AgentStatus));

        // L8 — thinking streamed inline (Anthropic / Gemini ChatAsync wrap each thinking delta in <thinking>…</thinking>)
        Fake.Script.Enqueue(FakeStep.Reply("<thinking>Weighing the options.</thinking>", "<thinking> B is simpler.</thinking>", "The answer", " is B."));
        await Send("which one?");
        await WaitIdle();
        var reply = Vm.Messages.Last(m => m.Role == MessageRole.Assistant);
        Check("L8 streamed thinking does not leak into the reply text", reply.Content == "The answer is B.", reply.Content);
        Check("L8 streamed thinking lands in the reply's Thinking block", reply.ThinkingContent == "Weighing the options. B is simpler.", reply.ThinkingContent);
        Fake.Script.Enqueue(FakeStep.Reply("ok"));
        await Send("thanks");
        await WaitIdle();
        Check("L8 thinking is not sent back to the model as part of the answer", !Fake.Calls.Last().History.Any(h => h.Content.Contains("<thinking>") || h.Content.Contains("Weighing")));
        var legacyHist = ChatViewModel.BuildModelHistory(new[]
        {
            new ChatMessage { Role = MessageRole.User, Content = "q" },
            new ChatMessage { Role = MessageRole.Assistant, Content = "<thinking>old thought</thinking>Old answer." },
        });
        Check("L8 replies saved with inline <thinking> by older builds are cleaned before sending", legacyHist[1].Content == "Old answer.", legacyHist[1].Content);
    }

    // ═══════════════════════════════════════════ L10 ═══════════════════════════════════════════

    static async Task TestHoverRetry()
    {
        Log("\n## L10 — hover Retry re-runs the question that failed, never a tool row");
        await FreshChat();
        Fake.Script.Enqueue(FakeStep.Throw(new HttpRequestException("invalid request", null, HttpStatusCode.BadRequest)));
        await Send("first question");
        await WaitIdle();
        var err = Vm.Messages.Last(m => m.HasError);
        Fake.Script.Enqueue(FakeStep.Reply("first answer"));
        int callsBefore = Fake.Calls.Count;
        Vm.RetryMessageCommand.Execute(err);
        await WaitIdle();
        Check("L10 retrying the latest failure re-runs that question", Fake.Calls.Count == callsBefore + 1 && Fake.Calls.Last().Input == "first question");
        Check("L10 …without a duplicate question bubble", Vm.Messages.Count(m => m.Role == MessageRole.User && m.Content == "first question") == 1);
        Check("L10 …and the error bubble is replaced by the answer", !Vm.Messages.Contains(err) && Vm.Messages.Last(m => m.Role == MessageRole.Assistant).Content == "first answer");

        // An OLDER failure retries ITS question, not the latest one
        Fake.Script.Enqueue(FakeStep.Throw(new HttpRequestException("invalid request", null, HttpStatusCode.BadRequest)));
        await Send("old question");
        await WaitIdle();
        var oldErr = Vm.Messages.Last(m => m.HasError);
        Fake.Script.Enqueue(FakeStep.Reply("newer answer"));
        await Send("newer question");
        await WaitIdle();
        Fake.Script.Enqueue(FakeStep.Reply("old answer"));
        Vm.RetryMessageCommand.Execute(oldErr);
        await WaitIdle();
        Check("L10 retrying an older failure asks THAT question again", Fake.Calls.Last().Input == "old question", Fake.Calls.Last().Input);

        // Tool rows: no Retry button
        var toolRow = new ChatMessage { Role = MessageRole.ToolAction, Content = "run_command failed", ToolName = "Ran npm test", HasError = true, ToolSuccess = false, IsHovered = true };
        Vm.Messages.Add(toolRow);
        await Render();
        var retryBtn = Container(toolRow) == null ? null : Find<Button>(Container(toolRow)).FirstOrDefault(b => Equals(b.Content, ""));
        Check("L10 a failed tool row shows no Retry button", retryBtn == null || !retryBtn.IsVisible);
        var failedReply = Vm.Messages.LastOrDefault(m => m.CanRetry);
        Check("L10 tool rows never get CanRetry", !toolRow.CanRetry);
        if (failedReply != null)
        {
            failedReply.IsHovered = true;
            await Render();
            var b = Find<Button>(Container(failedReply)).FirstOrDefault(x => Equals(x.Content, ""));
            Check("L10 a failed reply shows Retry on hover", b != null && b.IsVisible);
        }
    }

    // ═══════════════════════════════════════════ L2 ═══════════════════════════════════════════

    static ChatSession ChatInDb(string title, string user, string reply)
    {
        var c = new ChatSession { Title = title };
        c.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = user });
        c.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = reply });
        Persist.SaveSession(c);
        Vm.Sessions.Insert(0, new ChatSession { Id = c.Id, Title = c.Title });
        return c;
    }

    static async Task Open(string sessionId)
    {
        Vm.LoadSessionCommand.Execute(sessionId);
        await WaitUntil(() => Vm.CurrentSession?.Id == sessionId);
        await Render();
    }

    /// <summary>
    /// W — typing while a reply runs. The box was disabled for the whole run and Enter did nothing; now the message
    /// shows at once and reaches the run: an agent loop reads it at its next step (AgentRunScope.TakeUserNotes, unit
    /// checked here), and a reply that ends first (plain chat, claude-dev) gets it as the very next turn.
    /// </summary>
    static async Task TestTypeWhileWorking()
    {
        Log("\n## W — typing while a reply runs: shown at once, read by the run or sent right after it");

        var scope = new AgentRunScope("w-unit", null);
        Check("W1 the run's note queue starts empty", !scope.HasUserNotes && scope.TakeUserNotes() == null);
        scope.AddUserNote("  also add tests  ");
        scope.AddUserNote("and use English names");
        string note = scope.TakeUserNotes();
        Check("W1 the agent gets everything typed since it last looked, as one block that says what it is",
            note != null && note.StartsWith("[The user sent this while you were working") && note.Contains("also add tests\n\nand use English names")
            && !scope.HasUserNotes, note ?? "null");

        await FreshChat();
        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor["[W2]"] = new Queue<FakeStep>([FakeStep.Gated(["W2 working"], gate, [" — W2 done."])]);
        Fake.ScriptFor["[W2-more]"] = new Queue<FakeStep>([FakeStep.Reply("Added", " the tests.")]);
        await Send("[W2] write the parser");
        await WaitUntil(() => Vm.IsGenerating && Vm.Messages.Any(m => m.Content.StartsWith("W2 working")));
        var box = (TextBox)View.FindName("InputBox");
        Check("W2 the input box stays usable while the reply runs", box is { IsEnabled: true });

        Vm.UserInput = "[W2-more] also add tests";
        Vm.SendMessageCommand.Execute(null);
        await Task.Delay(100); await Render();
        Check("W2 Enter while it works shows the message at once, clears the box and stops nothing",
            Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "[W2-more] also add tests") && Vm.UserInput == "" && Vm.IsGenerating,
            $"input='{Vm.UserInput}' generating={Vm.IsGenerating}");

        Vm.UserInput = "/compact";
        Vm.SendMessageCommand.Execute(null);
        Check("W2 a slash command waits for the reply to finish (it stays in the box)", Vm.UserInput == "/compact" && Vm.IsGenerating, Vm.StatusText);
        Vm.UserInput = "";

        gate.Release();
        bool sent = await WaitUntil(() => Fake.Calls.Any(c => c.Input.Contains("[W2-more] also add tests")), 15000);
        await WaitUntil(() => !Vm.IsGenerating && Vm.Messages.Any(m => m.Content.Contains("Added the tests.")), 15000);
        var order = Vm.Messages.Where(m => m.Role is MessageRole.User or MessageRole.Assistant).Select(m => m.Content).ToList();
        int answer = order.FindIndex(c => c.Contains("W2 done")), more = order.FindIndex(c => c == "[W2-more] also add tests"),
            reply = order.FindIndex(c => c.Contains("Added the tests."));
        Check("W3 a reply that ended before reading it: the message moves after its answer and goes as the next turn",
            sent && answer >= 0 && more > answer && reply > more && order.Count(c => c == "[W2-more] also add tests") == 1,
            string.Join(" | ", order));
    }

    /// <summary>
    /// F — an old chat brings its project back. After a restart the app had no folder open, so carrying on in an old
    /// chat ran with no file or shell tools and the agent said it could not work.
    /// </summary>
    static async Task TestChatOpensItsProject()
    {
        Log("\n## F — opening an old chat opens its project; sending in it does too");
        Directory.CreateDirectory(ProjA);
        Directory.CreateDirectory(ProjB);
        var old = new ChatSession { Title = "old chat in A", ProjectPath = ProjA };
        old.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "fix the parser" });
        old.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "done" });
        Persist.SaveSession(old);
        Vm.Sessions.Insert(0, new ChatSession { Id = old.Id, Title = old.Title, ProjectPath = ProjA });

        Vm.SetWorkingDirectory("");
        await FreshChat();
        await Open(old.Id);
        Check("F1 opening a chat started in a project, with no folder open, opens that project",
            Vm.HasProject && string.Equals(Vm.WorkingDirectory, ProjA, StringComparison.OrdinalIgnoreCase), Vm.WorkingDirectory);

        Vm.SetWorkingDirectory(ProjB);
        await FreshChat();
        await Open(old.Id);
        Check("F2 …and with another project open, switches to the chat's own",
            string.Equals(Vm.WorkingDirectory, ProjA, StringComparison.OrdinalIgnoreCase), Vm.WorkingDirectory);

        var plain = ChatInDb("chat with no project", "hi", "hello");
        await Open(old.Id);
        await Open(plain.Id);
        Check("F3 a chat with no project shows no files — not the previous chat's (A)", !Vm.HasProject && Vm.ProjectTree.Count == 0, Vm.WorkingDirectory);

        var gone = new ChatSession { Title = "chat whose folder was deleted", ProjectPath = Path.Combine(PipeRoot, "deleted-folder") };
        gone.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "x" });
        Persist.SaveSession(gone);
        Vm.Sessions.Insert(0, new ChatSession { Id = gone.Id, Title = gone.Title, ProjectPath = gone.ProjectPath });
        await Open(old.Id);
        await Open(gone.Id);
        Check("F4 a chat whose folder no longer exists shows no files either (and says the folder is gone)",
            !Vm.HasProject && Vm.StatusText.Contains("deleted-folder"), $"{Vm.WorkingDirectory} · {Vm.StatusText}");

        // Opening a folder in a chat ties the chat to it: leave and come back, the same files.
        await Open(plain.Id);
        Vm.SetWorkingDirectory(ProjB);
        await Open(old.Id);
        await Open(plain.Id);
        Check("F7 a folder opened in a chat is remembered by that chat: back in it, its files (B) are shown",
            string.Equals(Vm.WorkingDirectory, ProjB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Persist.LoadSession(plain.Id)?.ProjectPath, ProjB, StringComparison.OrdinalIgnoreCase), Vm.WorkingDirectory);

        // The chat on screen at startup never went through the chat list: sending in it brings its folder back.
        await Open(old.Id);
        Vm.SetWorkingDirectory("");
        Fake.ScriptFor["[F5]"] = new Queue<FakeStep>([FakeStep.Reply("ok")]);
        await Send("[F5] carry on");
        Check("F5 sending in a chat whose project is closed opens it again before the reply",
            Vm.HasProject && string.Equals(Vm.WorkingDirectory, ProjA, StringComparison.OrdinalIgnoreCase), Vm.WorkingDirectory);
        await WaitUntil(() => !Vm.IsGenerating, 10000);
        Vm.SetWorkingDirectory("");
    }

    static byte[] TinyImage(System.Windows.Media.Imaging.BitmapEncoder enc)
    {
        var px = Enumerable.Range(0, 32 * 32 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 90 + i % 120)).ToArray();
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(32, 32, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, px, 32 * 4);
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>X — images attached to a message: pasted (Ctrl+V), dropped or picked, shown before sending and in the
    /// bubble, stored with the chat, and sent with it (a provider that can't see is told the file instead).</summary>
    static async Task TestAttachedImages()
    {
        Log("\n## X — images attached to a message: pasted, shown, stored with the chat, sent with it");
        await FreshChat();
        byte[] png = TinyImage(new System.Windows.Media.Imaging.PngBitmapEncoder());
        Check("X1 a pasted picture is attached — a copy under the data folder",
            Vm.AttachImageBytes(png) && Vm.PendingImages.Count == 1 && File.Exists(Vm.PendingImages[0]) && Vm.HasPendingImages, Vm.StatusText);
        Check("X1 bytes that are not a picture are refused, nothing attached",
            !Vm.AttachImageBytes(System.Text.Encoding.UTF8.GetBytes("not a picture at all")) && Vm.PendingImages.Count == 1, Vm.StatusText);
        string bmpPath = Path.Combine(PipeRoot, "dropped-" + Guid.NewGuid().ToString("N")[..6] + ".bmp");
        Directory.CreateDirectory(PipeRoot);
        File.WriteAllBytes(bmpPath, TinyImage(new System.Windows.Media.Imaging.BmpBitmapEncoder()));
        Check("X1 a dropped BMP is attached as a PNG", Vm.AttachImageFiles([bmpPath]) == 1 && Vm.PendingImages.Count == 2 && Vm.PendingImages[1].EndsWith(".png"),
            string.Join(", ", Vm.PendingImages));
        await Render();
        var strip = FindVisual<ItemsControl>(View, ic => ReferenceEquals(ic.ItemsSource, Vm.PendingImages));
        Check("X2 the strip above the input shows both thumbnails", strip is { IsVisible: true } && strip.Items.Count == 2);
        Vm.RemovePendingImageCommand.Execute(Vm.PendingImages[1]);
        Check("X2 × takes one off", Vm.PendingImages.Count == 1);

        string attached = Vm.PendingImages[0];
        Fake.ScriptFor["[X3]"] = new Queue<FakeStep>([FakeStep.Reply("I see it.")]);
        await Send("[X3] what is in this picture?");
        await WaitUntil(() => !Vm.IsGenerating, 10000);
        var userMsg = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.User);
        Check("X3 sent: the message carries the picture and the strip is empty",
            userMsg != null && userMsg.AttachedImages.SequenceEqual([attached]) && !Vm.HasPendingImages);
        string sentInput = Fake.Calls.LastOrDefault()?.Input ?? "";
        Check("X3 a provider that cannot see (the offline fake) is told the file instead",
            sentInput.Contains("[The user attached 1 image(s)") && sentInput.Contains(attached), sentInput);
        await Render();
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-attached-image.png"));
        var bubbleImage = FindVisual<Image>(View, im => im.Source != null && im.ActualHeight > 0 && im.ActualHeight <= 180);
        Check("X3 the bubble shows the picture", bubbleImage != null);

        var stored = Persist.LoadSession(Vm.CurrentSession!.Id)?.Messages.FirstOrDefault(m => m.Role == MessageRole.User && m.Content.StartsWith("[X3]"));
        Check("X4 stored with the chat: reloaded, the message still has its picture (and no tool columns)",
            stored != null && stored.AttachedImages.SequenceEqual([attached]) && stored.ToolName == null && stored.ToolOutput == null,
            stored == null ? "not stored" : string.Join(", ", stored.AttachedImages));

        Vm.AttachImageBytes(png);
        Fake.ScriptFor["Look at the attached"] = new Queue<FakeStep>([FakeStep.Reply("A grey square.")]);
        Vm.UserInput = "";
        Vm.SendMessageCommand.Execute(null);
        await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("A grey square.")), 10000);
        var onlyPicture = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.User);
        Check("X5 a picture with no text sends: the bubble has only the picture, the model gets a line with it",
            onlyPicture is { Content: "" } && onlyPicture.HasAttachedImages && (Fake.Calls.LastOrDefault()?.Input ?? "").StartsWith("Look at the attached image"),
            Fake.Calls.LastOrDefault()?.Input ?? "");
    }

    /// <summary>V — speaking instead of typing. The mic button opens Chrome's speech page (here a fake launcher: a
    /// test never opens a browser or a microphone); the page is driven over the real loopback bridge exactly as the
    /// browser would — words stream into the box, the guess is replaced as it firms up, nothing is sent until Send,
    /// and nothing but the page itself can type into the box or press Send.</summary>
    static async Task TestDictation()
    {
        Log("\n## V — speaking: the mic bridge, words into the box, never sent by itself");
        await FreshChat();
        string? url = null;
        Vm.DictationLauncher = u => { url = u; return true; };
        var connectWas = CluadeX.Services.Voice.ChromeDictationService.ConnectTimeout;
        var silenceWas = CluadeX.Services.Voice.ChromeDictationService.SilenceTimeout;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            Vm.UserInput = "";
            Vm.ToggleDictationCommand.Execute(null);
            Check("V1 the mic button opens the mic page on 127.0.0.1 with a token", Vm.IsDictating && url != null
                && url.StartsWith("http://127.0.0.1:478") && url.Contains("t=") && url.Contains("lang="), url ?? "(no url)");
            if (url == null) return;
            var u = new Uri(url);
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            string origin = $"{u.Scheme}://{u.Authority}", t = q["t"]!;
            int s = int.Parse(q["s"]!);
            await Render();
            var micOn = FindVisual<Button>(View, b => b.Name == "MicOnButton");
            var micOff = FindVisual<Button>(View, b => b.Name == "MicButton");
            Check("V1 the composer's mic shows it is listening", micOn is { IsVisible: true } && micOff is { IsVisible: false });
            SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-mic-listening.png"));

            var page = await http.GetAsync(url);
            string html = await page.Content.ReadAsStringAsync();
            Check("V1 the page is served and runs the browser's speech recognition (Thai by default here)",
                page.IsSuccessStatusCode && html.Contains("webkitSpeechRecognition") && html.Contains("<title>CluadeX Mic</title>")
                && page.Content.Headers.ContentType?.MediaType == "text/html", ((int)page.StatusCode).ToString());

            async Task<HttpStatusCode> Post(string path, object body, string? from)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, origin + path)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
                if (from != null) req.Headers.Add("Origin", from);
                return (await http.SendAsync(req)).StatusCode;
            }

            int usersBefore = Vm.Messages.Count(m => m.Role == MessageRole.User);
            var st = await Post("/text", new { t, s, final = "", interim = "เปิดไฟล์" }, origin);
            await WaitUntil(() => Vm.UserInput == "เปิดไฟล์", 3000);
            Check("V2 a guess shows in the box while it is heard", st == HttpStatusCode.NoContent && Vm.UserInput == "เปิดไฟล์", Vm.UserInput);
            await Post("/text", new { t, s, final = "เปิดไฟล์ main", interim = "" }, origin);
            await Post("/text", new { t, s, final = "", interim = "แล้วแก้บั๊ก" }, origin);
            await WaitUntil(() => Vm.UserInput == "เปิดไฟล์ main แล้วแก้บั๊ก", 3000);
            Check("V2 the guess is replaced by the final words and the next guess follows them",
                Vm.UserInput == "เปิดไฟล์ main แล้วแก้บั๊ก", Vm.UserInput);
            Check("V2 nothing is sent by speaking alone", Vm.Messages.Count(m => m.Role == MessageRole.User) == usersBefore && !Vm.IsGenerating);

            string boxBefore = Vm.UserInput;
            var wrongToken = await Post("/text", new { t = "0000", s, final = "rm -rf /", interim = "" }, origin);
            var foreign = await Post("/text", new { t, s, final = "rm -rf /", interim = "" }, "https://evil.example");
            var noOrigin = await Post("/text", new { t, s, final = "rm -rf /", interim = "" }, null);
            var foreignSend = await Post("/send", new { t, s }, "https://evil.example");
            var rebind = new HttpRequestMessage(HttpMethod.Get, url);
            rebind.Headers.Host = "evil.example";
            var rebound = (await http.SendAsync(rebind)).StatusCode;
            var stateNoToken = (await http.GetAsync($"{origin}/state?t=nope&s={s}&since=-1")).StatusCode;
            await Task.Delay(300);
            Check("V3 another web page cannot type or press Send: wrong token, foreign or missing origin → 403",
                wrongToken == HttpStatusCode.Forbidden && foreign == HttpStatusCode.Forbidden && noOrigin == HttpStatusCode.Forbidden
                && foreignSend == HttpStatusCode.Forbidden && Vm.UserInput == boxBefore && !Vm.IsGenerating,
                $"{wrongToken}/{foreign}/{noOrigin}/{foreignSend}");
            Check("V3 a DNS-rebinding Host and a poll without the token are refused",
                rebound == HttpStatusCode.Forbidden && stateNoToken == HttpStatusCode.Forbidden, $"{rebound}/{stateNoToken}");

            Fake.ScriptFor["แก้บั๊ก"] = new Queue<FakeStep>([FakeStep.Reply("ok, fixing it")]);
            var sent = await Post("/send", new { t, s }, origin);
            await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "เปิดไฟล์ main แล้วแก้บั๊ก"), 5000);
            Check("V4 the mic window's Send sends the box, as Enter would",
                sent == HttpStatusCode.NoContent && Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "เปิดไฟล์ main แล้วแก้บั๊ก")
                && Vm.UserInput == "", Vm.UserInput);
            string state = await http.GetStringAsync($"{origin}/state?t={t}&s={s}&since=-1");
            using (var doc = System.Text.Json.JsonDocument.Parse(state))
                Check("V4 the page is told the box was sent (it clears its words) and keeps listening",
                    doc.RootElement.GetProperty("active").GetBoolean() && doc.RootElement.GetProperty("sent").GetInt32() >= 1, state);
            await WaitUntil(() => !Vm.IsGenerating, 10000);

            await Post("/text", new { t, s, final = "อีกอย่าง", interim = "" }, origin);
            await WaitUntil(() => Vm.UserInput == "อีกอย่าง", 3000);
            Check("V5 after a send, the next words start a fresh box", Vm.UserInput == "อีกอย่าง", Vm.UserInput);
            Vm.UserInput = "พิมพ์เอง ";
            await Post("/text", new { t, s, final = "", interim = "แล้วพูดต่อ" }, origin);
            await WaitUntil(() => Vm.UserInput == "พิมพ์เอง แล้วพูดต่อ", 3000);
            Check("V5 typed text and spoken words mix: speech goes on after what was typed", Vm.UserInput == "พิมพ์เอง แล้วพูดต่อ", Vm.UserInput);

            long v;
            using (var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"{origin}/state?t={t}&s={s}&since=-1")))
                v = doc.RootElement.GetProperty("v").GetInt64();
            var held = http.GetStringAsync($"{origin}/state?t={t}&s={s}&since={v}");
            await Task.Delay(400);
            bool wasHeld = !held.IsCompleted;
            Vm.ToggleDictationCommand.Execute(null);
            bool released = await Task.WhenAny(held, Task.Delay(3000)) == held;
            Check("V6 the page's poll is held open, and the mic button releases it at once with active:false (the window closes itself)",
                wasHeld && released && held.Result.Contains("\"active\":false") && !Vm.IsDictating, released ? held.Result : "still held");
            Check("V6 the unfinished guess stays in the box as typed text", Vm.UserInput == "พิมพ์เอง แล้วพูดต่อ", Vm.UserInput);
            var late = await Post("/text", new { t, s, final = "late", interim = "" }, origin);
            await Task.Delay(200);
            Check("V6 words after the stop are refused (410) and never reach the box",
                late == HttpStatusCode.Gone && Vm.UserInput == "พิมพ์เอง แล้วพูดต่อ", $"{late} / {Vm.UserInput}");

            Vm.ToggleDictationCommand.Execute(null);
            int s2 = int.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["s"]!);
            string oldWindow = await http.GetStringAsync($"{origin}/state?t={t}&s={s}&since=-1");
            Check("V7 a new session: a window left from the last one is told to close", s2 > s && oldWindow.Contains("\"active\":false"), oldWindow);
            var bye = await Post("/bye", new { t, s = s2 }, null);   // sendBeacon from the closing window
            await WaitUntil(() => !Vm.IsDictating, 3000);
            Check("V7 closing the mic window turns the mic off in the chat",
                bye == HttpStatusCode.NoContent && !Vm.IsDictating && Vm.StatusText == L("chat.mic.closed"), Vm.StatusText);

            CluadeX.Services.Voice.ChromeDictationService.ConnectTimeout = TimeSpan.FromSeconds(1.5);
            Vm.ToggleDictationCommand.Execute(null);
            await WaitUntil(() => !Vm.IsDictating, 6000);
            Check("V8 a window that never opens: the mic turns itself off and says so",
                !Vm.IsDictating && Vm.StatusText == L("chat.mic.noConnect"), Vm.StatusText);

            CluadeX.Services.Voice.ChromeDictationService.SilenceTimeout = TimeSpan.FromSeconds(1);
            Vm.ToggleDictationCommand.Execute(null);
            int s3 = int.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["s"]!);
            await http.GetStringAsync($"{origin}/state?t={t}&s={s3}&since=-1");
            await WaitUntil(() => !Vm.IsDictating, 6000);
            Check("V9 a window that dies without a goodbye (crash, killed): the mic turns itself off",
                !Vm.IsDictating && Vm.StatusText == L("chat.mic.closed"), Vm.StatusText);

            Vm.DictationLauncher = _ => false;
            Vm.ToggleDictationCommand.Execute(null);
            Check("V10 the browser does not start: no listening state left behind, a message instead",
                !Vm.IsDictating && Vm.StatusText.StartsWith(L("chat.mic.failed").Split('{')[0]), Vm.StatusText);

            CluadeX.Services.Voice.ChromeDictationService.ConnectTimeout = connectWas;
            CluadeX.Services.Voice.ChromeDictationService.SilenceTimeout = silenceWas;
            await TestMicPageInRealChrome();
        }
        finally
        {
            CluadeX.Services.Voice.ChromeDictationService.ConnectTimeout = connectWas;
            CluadeX.Services.Voice.ChromeDictationService.SilenceTimeout = silenceWas;
            if (Vm.IsDictating) Vm.ToggleDictationCommand.Execute(null);
            Vm.DictationLauncher = null;
            Vm.UserInput = "";
        }
    }

    /// <summary>V11 — the mic page's own script, run by the real Google Chrome: headless, a throwaway profile on E:,
    /// a fake microphone and an auto-answered permission prompt (test-only flags) — the user's Chrome, profile and
    /// microphone are never touched. Proves the page loads, polls the bridge and starts recognition (or reports why
    /// it cannot), and closes itself when the chat turns the mic off.</summary>
    static async Task TestMicPageInRealChrome()
    {
        var browser = CluadeX.Services.Voice.ChromeDictationService.FindBrowser();
        if (browser is not { Engine: "google" }) { Log("SKIP  V11 Google Chrome is not installed"); return; }
        string profile = Path.Combine(AppContext.BaseDirectory, "chrome-mic-profile-" + Guid.NewGuid().ToString("N")[..6]);
        System.Diagnostics.Process? chrome = null;
        var events = new List<string>();
        Vm.PropertyChanged += Track;
        void Track(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatViewModel.StatusText)) events.Add(Vm.StatusText);
        }
        try
        {
            Vm.DictationLauncher = url =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo(browser.Path) { UseShellExecute = false };
                foreach (var a in new[] { "--headless=new", "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                                          "--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream", url })
                    psi.ArgumentList.Add(a);
                chrome = System.Diagnostics.Process.Start(psi);
                return chrome != null;
            };
            Vm.UserInput = "";
            Vm.ToggleDictationCommand.Execute(null);
            string listening = L("chat.mic.listening").Split('(')[0];
            string[] pageSaid = [listening, L("chat.mic.denied"), L("chat.mic.noMic"), L("chat.mic.network"), L("chat.mic.error").Split('{')[0]];
            bool spoke = await WaitUntil(() => events.Any(ev => pageSaid.Any(p => ev.StartsWith(p))), 20000);
            Check("V11 real Chrome runs the mic page: it loads, reaches the bridge and starts recognition (or says why not)",
                spoke && Vm.IsDictating, string.Join(" | ", events));
            Log("      page said: " + (events.LastOrDefault(ev => pageSaid.Any(p => ev.StartsWith(p))) ?? "(nothing)"));
            Vm.ToggleDictationCommand.Execute(null);
            bool closed = chrome != null && await Task.Run(() => chrome.WaitForExit(8000));
            Check("V11 the mic off in the chat ends the page's session", !Vm.IsDictating);
            Log("      headless Chrome exited by itself after the page closed: " + (closed ? "yes" : "no (killed by the test)"));
        }
        finally
        {
            Vm.PropertyChanged -= Track;
            if (Vm.IsDictating) Vm.ToggleDictationCommand.Execute(null);
            Vm.DictationLauncher = null;
            try { if (chrome is { HasExited: false }) chrome.Kill(entireProcessTree: true); } catch { }
            chrome?.Dispose();
            for (int i = 0; i < 10 && Directory.Exists(profile); i++)
            {
                try { Directory.Delete(profile, true); } catch { await Task.Delay(500); }
            }
        }
    }

    static async Task TestBackgroundRuns()
    {
        Log("\n## R — a run belongs to its chat: switching never stops it; several chats run at once");
        await FreshChat();
        var chatA = Vm.CurrentSession!;
        var gateA = new SemaphoreSlim(0);
        Fake.ScriptFor["[A1]"] = new Queue<FakeStep>([FakeStep.Gated(["A is", " working"], gateA, [" — A done."])]);
        await Send("[A1] long task in A");
        Check("R1 the run in chat A is streaming", await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.Assistant && m.Content.StartsWith("A is"))));

        var chatB = ChatInDb("chat B", "hi B", "hello B");
        ConfirmTitles.Clear();
        await Open(chatB.Id);
        Check("R1 opening another chat mid-run asks nothing and switches", Vm.CurrentSession?.Id == chatB.Id && ConfirmTitles.Count == 0, string.Join(",", ConfirmTitles));
        Check("R1 chat B shows only its own messages", Vm.Messages.Any(m => m.Content == "hello B") && !Vm.Messages.Any(m => m.Content.Contains("A is")));
        Check("R1 chat A's run goes on in the background; chat B is free to send", Vm.HasRunningElsewhere && Vm.RunningElsewhereText.Length > 0 && !Vm.IsGenerating && Vm.CanSend,
            Vm.RunningElsewhereText);

        gateA.Release();
        bool aDone = await WaitUntil(() => !Vm.HasRunningElsewhere, 20000);
        var storedA = Persist.LoadSession(chatA.Id);
        Check("R2 it finished in the background and its answer is stored in chat A", aDone && storedA != null && storedA.Messages.Any(m => m.Content.Contains("A done")),
            storedA == null ? "not stored" : string.Join(" | ", storedA.Messages.Select(m => m.Content)));
        Check("R2 …and never appeared in chat B", !Vm.Messages.Any(m => m.Content.Contains("A done")));
        await Open(chatA.Id);
        Check("R2 opening chat A shows that answer once", Vm.Messages.Count(m => m.Content.Contains("A done")) == 1, string.Join(" | ", Vm.Messages.Select(m => m.Content)));

        var gateC = new SemaphoreSlim(0);
        Fake.ScriptFor["[A3]"] = new Queue<FakeStep>([FakeStep.Gated(["live", " text"], gateC, [" — finished live."])]);
        await Send("[A3] again in A");
        await WaitUntil(() => Vm.Messages.Any(m => m.IsStreaming && m.Content.StartsWith("live")));
        var liveList = Vm.Messages;
        ConfirmTitles.Clear();
        Vm.NewSessionCommand.Execute(null);
        await Render();
        Check("R3 New chat mid-run: no question, an empty chat, the run goes on", ConfirmTitles.Count == 0 && Vm.Messages.Count == 0 && Vm.HasRunningElsewhere);
        await Open(chatA.Id);
        Check("R3 back in chat A: its live list (still streaming) and Stop offered", ReferenceEquals(Vm.Messages, liveList) && Vm.IsGenerating
            && Vm.Messages.Any(m => m.IsStreaming && m.Content.StartsWith("live")));
        gateC.Release();
        await WaitIdle();
        Check("R3 it finishes on screen with one final answer", Vm.Messages.Count(m => m.Content.Contains("finished live")) == 1);

        // Two chats at once.
        var gateD = new SemaphoreSlim(0);
        Fake.ScriptFor["[D]"] = new Queue<FakeStep>([FakeStep.Gated(["D working"], gateD, [" — D finished."])]);
        await Send("[D] slow task");
        await WaitUntil(() => Vm.Messages.Any(m => m.Content.StartsWith("D working")));
        var chatD = Vm.CurrentSession!;
        await FreshChat();
        Fake.ScriptFor["[E]"] = new Queue<FakeStep>([FakeStep.Reply("E answered", " quickly.")]);
        await Send("[E] quick question");
        bool eDone = await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("E answered quickly")) && !Vm.IsGenerating, 20000);
        await Render();
        Check("R4 a second chat runs and answers while the first is still working", eDone && Vm.HasRunningElsewhere,
            string.Join(" | ", Vm.Messages.Select(m => m.Content)));
        var eCall = Fake.Calls.LastOrDefault(c => c.Input.Contains("[E]"));
        Check("R4 each run sends only its own conversation", eCall != null && eCall.History.All(h => !h.Content.Contains("[D]")) && !eCall.Input.Contains("[D]"));
        var eBubble = Vm.Messages.LastOrDefault(m => m.Content.Contains("E answered"));
        Check("R4 the second chat's answer is on screen", eBubble != null && Container(eBubble)?.Visibility == Visibility.Visible);
        gateD.Release();
        bool dDone = await WaitUntil(() => !Vm.HasRunningElsewhere, 20000);
        Check("R4 the first finishes in its own chat, not on the screen showing the second", dDone
            && Persist.LoadSession(chatD.Id)?.Messages.Any(m => m.Content.Contains("D finished")) == true && !Vm.Messages.Any(m => m.Content.Contains("D finished")));

        // Stop is per chat.
        Fake.ScriptFor["[F]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        Fake.ScriptFor["[G]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        await FreshChat();
        await Send("[F] waits forever");
        var chatF = Vm.CurrentSession!;
        await WaitUntil(() => Vm.IsGenerating);
        await FreshChat();
        await Send("[G] waits forever too");
        await WaitUntil(() => Vm.IsGenerating);
        Vm.StopGenerationCommand.Execute(null);
        bool gStopped = await WaitUntil(() => !Vm.IsGenerating, 10000);
        Check("R5 Stop stops the run of the chat on screen only; the other keeps running", gStopped && Vm.HasRunningElsewhere);
        await Open(chatF.Id);
        Check("R5 …which is still running in its chat", Vm.IsGenerating);
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle();
        Check("R5 stopping it too leaves nothing running", !Vm.IsGenerating && !Vm.HasRunningElsewhere
            && Vm.Messages.Any(m => m.IsInterrupted || m.Content.Contains(L("chat.interruptedNote"))));

        // Deleting a chat whose run goes on elsewhere stops that run first (asked).
        Fake.ScriptFor["[H]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        await FreshChat();
        await Send("[H] hangs");
        var chatH = Vm.CurrentSession!;
        await WaitUntil(() => Vm.IsGenerating);
        await Open(chatB.Id);
        ConfirmTitles.Clear();
        ConfirmAnswer = true;
        Vm.DeleteSessionCommand.Execute(chatH.Id);
        bool gone = await WaitUntil(() => !Vm.HasRunningElsewhere && !Vm.Sessions.Any(x => x.Id == chatH.Id), 20000);
        Check("R6 deleting a chat whose run goes on elsewhere asks, stops it and deletes it", gone && ConfirmTitles.Contains(L("chat.busy.title"))
            && Persist.LoadSession(chatH.Id) == null && Vm.CurrentSession?.Id == chatB.Id, string.Join(",", ConfirmTitles));
        Fake.ScriptFor.Clear();
    }

    /// <summary>ask_user (nothing listened for it before: it failed every time) and a run's pinned folder.</summary>
    static async Task TestAskUserAndPinnedFolder()
    {
        Log("\n## Q — ask_user: the question in the chat, answered by a click; S — a run keeps its folder");
        var fs = Sp.GetRequiredService<FileSystemService>();
        Vm.SetWorkingDirectory(ProjA);
        Vm.AgenticMode = true;
        await FreshChat();
        Fake.ScriptFor["[Q]"] = new Queue<FakeStep>([
            FakeStep.Reply("Let me ask.\n[ACTION: ask_user]\nquestion: Which colour?\noptions: red, blue\n[/ACTION]"),
        ]);
        // The loop's follow-up request carries the tool result, not the chat's marker: it takes the general script.
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Reply("You chose blue — done."));
        await Send("[Q] pick a colour");
        ChatMessage q = null;
        bool asked = await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 20000);
        await Render();
        Check("Q1 ask_user shows the question and its options in the chat", asked && q.Content.Contains("Which colour?") && q.QuestionOptions.SequenceEqual(new[] { "red", "blue" }),
            $"agentic={Vm.AgenticMode} project={Vm.HasProject} msgs=" + string.Join(" | ", Vm.Messages.Select(m => $"{m.Role}:{m.Content.Replace('\n', '⏎')}")));
        var blue = q == null || Container(q) == null ? null : Find<Button>(Container(q)).FirstOrDefault(b => b.Content as string == "blue");
        Check("Q1 …as buttons on screen", blue is { IsVisible: true });
        await Task.Delay(700); await Render();   // the card's glide-in
        var girl = q == null || Container(q) == null ? null : Find<System.Windows.Controls.Image>(Container(q)).FirstOrDefault(i => i.Source != null && i.IsVisible);
        Check("Q1b a girl (one of Assets\\Girls) stands beside the open question", q?.QuestionGirl != null && girl != null,
            $"girl={q?.QuestionGirl} names={string.Join(",", CluadeX.Services.GirlArt.Names)}");
        Check("Q1c the chat's row in the history says it waits for the owner", Vm.CurrentSession?.RunStatus == "waiting",
            $"status={Vm.CurrentSession?.RunStatus} step={Vm.CurrentSession?.RunStep}");
        SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-question-girl.png"));
        if (blue != null) Click(blue);
        bool done = await WaitIdle(20000);
        bool toldAgent = Fake.Calls.Any(c => c.Input.Contains("User answered: blue") || c.History.Any(h => h.Content.Contains("User answered: blue")));
        Check("Q2 clicking an option answers it: the agent is told 'User answered: blue' and carries on", done && toldAgent && Vm.Messages.Any(m => m.Content.Contains("You chose blue")),
            $"done={done} told={toldAgent}");
        Check("Q2 the answer stays under the question (saved with the chat)", q != null && q.QuestionAnswered && q.Content.Contains("→ blue"));

        Fake.ScriptFor["[Q3]"] = new Queue<FakeStep>([FakeStep.Reply("[ACTION: ask_user]\nquestion: Continue?\n[/ACTION]")]);
        await Send("[Q3] ask, then be stopped");
        ChatMessage q3 = null;
        await WaitUntil(() => (q3 = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 20000);
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle();
        Check("Q3 Stop while a question is open withdraws it ('not answered') and the run ends", q3 != null && q3.QuestionAnswered && q3.Content.Contains(L("chat.ask.cancelled")) && !Vm.IsGenerating);

        // S — the user opens another project while a run waits; the run's write still lands in its own folder.
        string inA = Path.Combine(ProjA, "pinned.txt"), inB = Path.Combine(ProjB, "pinned.txt");
        File.Delete(inA); File.Delete(inB);
        await FreshChat();
        Fake.ScriptFor["[PIN]"] = new Queue<FakeStep>([
            FakeStep.Reply("[ACTION: write_file]\npath: pinned.txt\ncontent: hello\n[/ACTION]"),
            FakeStep.Reply("written."),
        ]);
        await Send("[PIN] write a file");
        ChatMessage perm = null;
        bool prompted = await WaitUntil(() => (perm = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered)) != null, 20000);
        Vm.SetWorkingDirectory(ProjB);
        await Render();
        bool appOnB = SamePath(fs.AppWorkingDirectory, ProjB);
        perm?.ResolvePermission(PermissionOutcome.Allowed);
        await WaitUntil(() => !Vm.IsGenerating && !Vm.HasRunningElsewhere, 20000);
        Check("S1 opening project B while a run in A waits: its write still lands in A, never in B", prompted && appOnB && File.Exists(inA) && !File.Exists(inB),
            $"prompted={prompted} appOnB={appOnB} A={File.Exists(inA)} B={File.Exists(inB)}");
        Vm.SetWorkingDirectory(ProjA);
        Fake.ScriptFor.Clear();
        await Render();
    }

    // ═══════════════════════════════════════════ L6 + L7 ═══════════════════════════════════════════

    static async Task TestContextWindow()
    {
        Log("\n## L6 — the context meter uses the answering model's real window");
        Check("L6 Claude Opus 5.5 → 1M", ContextMemoryService.ContextWindowFor(AiProviderType.Anthropic, "claude-opus-5-5", 12288) == 1_000_000);
        Check("L6 Claude Haiku 4.5 → 200K", ContextMemoryService.ContextWindowFor(AiProviderType.Anthropic, "claude-haiku-4-5", 12288) == 200_000);
        Check("L6 Claude Sonnet 4.6 → 1M", ContextMemoryService.ContextWindowFor(AiProviderType.Anthropic, "claude-sonnet-4-6", 12288) == 1_000_000);
        Check("L6 unknown Claude id → conservative 200K", ContextMemoryService.ContextWindowFor(AiProviderType.Anthropic, "my-proxy-model", 12288) == 200_000);
        Check("L6 GPT-4o → 128K", ContextMemoryService.ContextWindowFor(AiProviderType.OpenAI, "gpt-4o", 12288) == 128_000);
        Check("L6 Gemini → 1M", ContextMemoryService.ContextWindowFor(AiProviderType.Gemini, "gemini-2.5-pro", 12288) == 1_048_576);
        Check("L6 local GGUF → the configured ContextSize", ContextMemoryService.ContextWindowFor(AiProviderType.Local, null, 12288) == 12288);

        await FreshChat();
        Settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Anthropic;
            s.ProviderConfigs["Anthropic"] = new ProviderConfig { SelectedModel = "claude-opus-5-5" };
        });
        string words = string.Join(" ", Enumerable.Repeat("context", 8500));   // ≈11k estimated tokens
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = words });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "ok" });
        await Render();
        Check("L6 ~11k tokens with Claude is a sliver of the window, not FULL",
            Vm.ContextUsagePercent < 3 && !Vm.ShowContextWarning && Vm.ContextLevelLabel == L("chat.ctx.fresh"),
            $"{Vm.ContextUsagePercent:F1}% · {Vm.ContextLevelLabel} · {Vm.ContextUsageText}");

        Check("L7 the status-bar model label shows the API model, not a local GGUF",
            await WaitUntil(() => Vm.LoadedModelName == "claude-opus-5-5", 3000), Vm.LoadedModelName);
        Check("L9 the local model picker is hidden while Claude answers", !Vm.ShowLocalModelPicker);

        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
        await Render();
        Check("L6 back on the local provider the meter uses ContextSize again", Vm.ContextUsagePercent > 50, $"{Vm.ContextUsagePercent:F1}%");
        Check("L9 the local model picker shows for the local provider", await WaitUntil(() => Vm.ShowLocalModelPicker, 3000));
        await FreshChat();
    }

    // ═══════════════════════════════════════════ U5 ═══════════════════════════════════════════

    static async Task TestAutoRunToggle()
    {
        Log("\n## U5 — 'run AI code automatically' is confirmed when switched on");
        ConfirmTitles.Clear();
        ConfirmAnswer = false;
        Vm.AutoExecute = true;
        await Render();
        Check("U5 declining keeps auto-run OFF", !Vm.AutoExecute && ConfirmTitles.Contains(L("chat.autoRun.confirmTitle")));
        ConfirmAnswer = true;
        Vm.AutoExecute = true;
        Check("U5 confirming turns it on", Vm.AutoExecute);
        ConfirmTitles.Clear();
        Vm.AutoExecute = false;
        Check("U5 turning it off needs no confirmation", !Vm.AutoExecute && ConfirmTitles.Count == 0);
        // The switches live in the composer's "+" menu (and the view's own check boxes): every label once.
        var menu = ((Button)View.FindName("MoreButton")).ContextMenu;
        var labels = Find<CheckBox>(View).Select(c => c.Content as string)
            .Concat(menu.Items.OfType<MenuItem>().Where(m => m.IsCheckable).Select(m => m.Header as string))
            .Where(s => s != null).ToList();
        Check("U5 no two toggles share a label (two different 'Auto' switches before)", labels.Count == labels.Distinct().Count(), string.Join(", ", labels));
        Check("U5 'Steps' appears once", labels.Count(l => l == L("chat.stepsLabel")) == 1);
    }

    // ═══════════════════════════════════════════ U6 ═══════════════════════════════════════════

    static async Task TestScrollFollow()
    {
        Log("\n## U6 — new output follows only while the reader is at the bottom");
        await FreshChat();
        for (int i = 0; i < 60; i++)
            Vm.Messages.Add(new ChatMessage { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = $"message {i}\nline two\nline three" });
        await Render();
        var sv = Find<ScrollViewer>(ChatList).First();
        sv.ScrollToTop();
        await Render();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "new output while reading" });
        await Render();
        await Task.Delay(200);
        await Render();
        Check("U6 scrolled up to read: new output does not yank the view down", sv.VerticalOffset < 50, $"offset {sv.VerticalOffset:F0}");
        sv.ScrollToEnd();
        await Render();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "more output\nmore\nmore" });
        await Render();
        await Task.Delay(200);
        await Render();
        Check("U6 at the bottom: new output is followed", sv.ScrollableHeight - sv.VerticalOffset < 60, $"{sv.ScrollableHeight - sv.VerticalOffset:F0}px from the end");
        sv.ScrollToTop();
        await Render();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "my new question" });
        await Render();
        await Task.Delay(200);
        await Render();
        Check("U6 sending a message jumps to the bottom", sv.ScrollableHeight - sv.VerticalOffset < 60, $"{sv.ScrollableHeight - sv.VerticalOffset:F0}px from the end");
    }

    // ═══════════════════════════════════════════ L12 ═══════════════════════════════════════════

    static async Task TestCodeBlockStop()
    {
        Log("\n## L12 — running a code block can be stopped; an error stays visible");
        await FreshChat();
        var block = new CodeBlock { Language = "batch", Code = "@ping -n 40 127.0.0.1 > nul" };
        Vm.ExecuteCodeBlockCommand.Execute(block);
        Check("L12 a running code block shows as generating (Stop visible)", await WaitUntil(() => Vm.IsGenerating, 3000));
        await Task.Delay(500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Vm.StopGenerationCommand.Execute(null);
        bool stopped = await WaitUntil(() => !Vm.IsGenerating, 8000);
        Check("L12 Stop ends it within seconds", stopped && sw.ElapsedMilliseconds < 8000, $"{sw.ElapsedMilliseconds} ms");
        Check("L12 the status says it was stopped (not 'Ready')", Vm.StatusText == L("chat.exec.cancelled"), Vm.StatusText);

        Vm.ExecuteCodeBlockCommand.Execute(new CodeBlock { Language = "nosuchlang", Code = "x" });
        await WaitIdle();
        Check("L12 a failed run leaves its outcome in the status bar", Vm.StatusText == L("chat.exec.failed"), Vm.StatusText);
    }

    // ═══════════════════════════════════════════ MINOR: new chat from outside ═══════════════════════════════════════════

    static async Task TestNewChatNavigation()
    {
        Log("\n## MINOR — New chat from outside the chat page brings the page forward; the chat's own button stays put");
        int navigations = 0;
        Action onNav = () => navigations++;
        Vm.NavigateToChatRequested += onNav;
        try
        {
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "something" });
            string before = Vm.CurrentSession!.Id;
            Vm.NewChatAndShowCommand.Execute(null);
            await WaitUntil(() => Vm.CurrentSession!.Id != before, 3000);
            Check("MINOR NewChatAndShowCommand opens a new chat and asks to show the Chat page", Vm.CurrentSession!.Id != before && navigations == 1);
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "something else" });
            before = Vm.CurrentSession!.Id;
            Vm.NewSessionCommand.Execute(null);
            await WaitUntil(() => Vm.CurrentSession!.Id != before, 3000);
            Check("MINOR NewSessionCommand (the chat's own button) does not navigate (the Code page docks this chat)",
                Vm.CurrentSession!.Id != before && navigations == 1);
        }
        finally { Vm.NavigateToChatRequested -= onNav; }
    }

    // ═══════════════════════════════════════════ pure helpers ═══════════════════════════════════════════

    static void TestPureHelpers()
    {
        Log("\n## L3 / L8 / U9 / U2 — helpers");
        Check("L3 503 is transient", ChatViewModel.IsTransientError(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)));
        Check("L3 429 is transient", ChatViewModel.IsTransientError(new HttpRequestException("x", null, (HttpStatusCode)429)));
        Check("L3 529 (overloaded) is transient", ChatViewModel.IsTransientError(new HttpRequestException("x", null, (HttpStatusCode)529)));
        Check("L3 a timeout is transient", ChatViewModel.IsTransientError(new TimeoutException("timed out")));
        Check("L3 a refused connection is transient", ChatViewModel.IsTransientError(new HttpRequestException("refused", new System.Net.Sockets.SocketException(10061))));
        Check("L3 400 'prompt is too long: 215000 tokens' is NOT transient",
            !ChatViewModel.IsTransientError(new HttpRequestException("prompt is too long: 215000 tokens", null, HttpStatusCode.BadRequest)));
        Check("L3 401 is NOT transient", !ChatViewModel.IsTransientError(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)));
        Check("L3 a message merely containing '500' is NOT transient", !ChatViewModel.IsTransientError(new InvalidOperationException("Error 500 tokens")));
        Check("L3 a missing model file is NOT transient", !ChatViewModel.IsTransientError(new FileNotFoundException("model.gguf")));

        Check("L8 the streamed reply text is not mistaken for thinking",
            ChatViewModel.LooksAlreadyStreamed("Here is the fix.\n\nDone.", "Here is the fix.  \nDone."));
        Check("L8 real thinking text is recognised as new", !ChatViewModel.LooksAlreadyStreamed("I should check the null case first.", "Here is the fix."));

        Check("U9 credentials in a clone URL are redacted",
            ChatViewModel.RedactUrlCredentials("fatal: https://user:ghp_SECRET123@github.com/o/r.git not found") == "fatal: https://***@github.com/o/r.git not found");

        var items = CommandPaletteViewModel.BuildCoreCommandItems(Vm, _ => { }, new RelayCommand(() => { }), new RelayCommand(() => { })).ToList();
        Check("U2 the palette offers the core commands", items.Count >= 7 && items.Any(i => i.Title == L("palette.cmd.newChat")) && items.Any(i => i.Title == L("palette.cmd.settings")));
        var palette = new CommandPaletteViewModel(() => items);
        palette.Open();
        palette.Query = "new chat";
        Check("U2 typing 'new chat' finds New chat first", palette.Results.FirstOrDefault()?.Title == L("palette.cmd.newChat"));
        Check("U2 palette result count is localized", palette.ResultSummary.Length > 0 && !palette.ResultSummary.Contains("result{"));
    }

    // ═══════════════════════════════════════════ T5: pipe runs ═══════════════════════════════════════════

    static string PipeRoot => Path.Combine(AppContext.BaseDirectory, "pipe-projects");
    static string ProjA => Path.Combine(PipeRoot, "user-project");
    static string ProjB => Path.Combine(PipeRoot, "caller-project");

    static bool SamePath(string a, string b) =>
        string.Equals((a ?? "").TrimEnd('\\', '/'), (b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static Dictionary<string, DateTime?> ProfileSnapshot()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
        var d = new Dictionary<string, DateTime?>();
        foreach (var name in new[] { "install.json", "mcp-host.log", "mcp-host-token", "mcp_servers.json", "settings.json" })
        {
            string p = Path.Combine(dir, name);
            d[name] = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        }
        return d;
    }

    /// <summary>App.HandleMcpToolAsync — the pipe's real dispatcher — on this harness's container.</summary>
    static async Task<McpToolResult> Dispatch(string tool, string argsJson, CancellationToken ct = default)
    {
        ScreenFree();
        var app = (CluadeX.App)Application.Current;
        typeof(CluadeX.App).GetField("_serviceProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, Sp);
        var handler = typeof(CluadeX.App).GetMethod("HandleMcpToolAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
        var invocation = new CluadeX.Services.Mcp.McpToolInvocation(tool, doc.RootElement.Clone());
        return await (Task<McpToolResult>)handler.Invoke(app, [invocation, ct])!;
    }

    static async Task TestPipeRuns()
    {
        Log("\n## T5 — pipe runs (cluadex.write_code): watched live, failures are isError, the app ends on its own project");
        if (Directory.Exists(PipeRoot)) Directory.Delete(PipeRoot, true);
        Directory.CreateDirectory(ProjA);
        Directory.CreateDirectory(ProjB);
        File.WriteAllText(Path.Combine(ProjB, "readme.txt"), "hello");
        var fs = Sp.GetRequiredService<FileSystemService>();
        Vm.ShowAllProjectSessions = false;
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();

        int toChat = 0, toEditor = 0;
        Action onChat = () => toChat++, onEditor = () => toEditor++;
        Vm.NavigateToChatRequested += onChat;
        Vm.NavigateToEditorRequested += onEditor;
        try
        {
            // P1 — the reply streams into the run's chat while the run is going.
            var gate = new SemaphoreSlim(0);
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Gated(["Hello", " from", " the", " pipe"], gate, [" — the task is complete."]));
            var run = Task.Run(() => FreeVm().RunMcpTaskAsync("t5-live", "Say hello.", null, null, ProjB, CancellationToken.None, maxSteps: 5));
            bool streaming = await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.Assistant && m.IsStreaming && m.Content.StartsWith("Hello")), 20000);
            await Render();
            var bubble = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
            Check("P1 a pipe run streams its reply into the visible chat while it runs", streaming && bubble != null && Container(bubble)?.Visibility == Visibility.Visible,
                string.Join(" | ", Vm.Messages.Select(m => $"{m.Role}:{(m.Content.Length > 40 ? m.Content[..40] : m.Content)}")));
            Check("P1 the Chat page is brought forward for the run", toChat == 1, $"{toChat} navigations");
            Check("P1 during the run the app is on the caller's project", SamePath(Vm.WorkingDirectory, ProjB) && SamePath(fs.WorkingDirectory, ProjB), Vm.WorkingDirectory);
            Check("P1 the run's chat is tagged with that project and stays in the project-filtered sidebar",
                Vm.CurrentSession != null && SamePath(Vm.CurrentSession.ProjectPath, ProjB) && Vm.VisibleSessions.Contains(Vm.CurrentSession), Vm.CurrentSession?.ProjectPath);
            gate.Release();
            var outcome = await run;
            await Render();
            Check("P1 a finished run returns its final text with isError=false", !outcome.IsError && outcome.Text.Contains("task is complete"), outcome.Text);
            Check("P1 the streamed bubble becomes the final reply in place (no duplicate answer)",
                Vm.Messages.Count(m => m.Role == MessageRole.Assistant && m.Content.Contains("task is complete")) == 1 && bubble is { IsStreaming: false },
                string.Join(" | ", Vm.Messages.Where(m => m.Role == MessageRole.Assistant).Select(m => m.Content)));
            // The window shows the project of the chat on screen (owner 2026-10-17): the task's chat is still on screen,
            // so the task's project stays open — switching the project back under it put the user's folder in the title.
            Check("P2 afterwards the task's chat is still on screen with ITS project in the window (title, tree, tools agree)",
                Vm.CurrentSession != null && SamePath(Vm.CurrentSession.ProjectPath, ProjB) && WindowMatchesChat(), $"{Vm.WorkingDirectory} / {fs.AppWorkingDirectory}");
            Check("P2 neither switch dragged the user to the editor", toEditor == 0, $"{toEditor} editor navigations");
            var stored = Persist.LoadSession(Vm.CurrentSession!.Id);
            Check("P2 the run's chat is saved with its project tag", stored != null && SamePath(stored.ProjectPath, ProjB) && stored.Messages.Any(m => m.Content.Contains("task is complete")), stored?.ProjectPath);
            // UX (review): maxSteps was ignored silently until T1's per-run limit is in the build.
            bool limitInBuild = typeof(CodeAgentService).GetMethods().Any(m => m.Name == "ExecuteAgenticAsync" && m.GetParameters().Any(p => p.Name == "maxIterations"));
            Check("P2 maxSteps: applied when this build's agent loop takes a per-run limit, otherwise the caller is TOLD it was not applied",
                limitInBuild ? !outcome.Text.Contains("was not applied") : outcome.Text.Contains("maxSteps=5 was not applied"), $"limitInBuild={limitInBuild}: {outcome.Text}");

            // P3 — Stop in CluadeX → the pipe result is isError (through App's real dispatcher).
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Hang());
            var call = Dispatch("cluadex.write_code", "{\"taskId\":\"t5-stop\",\"spec\":\"wait for me\",\"workingDirectory\":" + System.Text.Json.JsonSerializer.Serialize(ProjB) + "}");
            bool running = await WaitUntil(() => Vm.IsGenerating, 10000);
            Vm.StopGenerationCommand.Execute(null);
            var stopped = await Task.WhenAny(call, Task.Delay(20000)) == call ? await call : null;
            Check("P3 Stop during a pipe run returns isError=true to the caller", running && stopped is { IsError: true } && stopped.GetTextOutput().Contains("Stopped"), stopped?.GetTextOutput() ?? "no result");
            Check("P3 …and the window shows the project of the chat on screen", WindowMatchesChat() && !Vm.IsGenerating);
            var bad = await Dispatch("cluadex.write_code", "{\"taskId\":\"t5-bad\",\"spec\":\"x\",\"maxSteps\":-1}");
            var missing = await Dispatch("cluadex.write_code", "{\"taskId\":\"t5-bad\",\"spec\":\"x\",\"workingDirectory\":\"Z:\\\\no\\\\such\\\\folder\"}");
            Check("P3 a bad maxSteps or a folder that does not exist is refused as isError (it ran in the open project before)",
                bad.IsError && bad.GetTextOutput().Contains("maxSteps") && missing.IsError && missing.GetTextOutput().Contains("does not exist") && WindowMatchesChat(),
                bad.GetTextOutput() + " / " + missing.GetTextOutput());

            // P4 — the caller going away (the pipe closes) stops the run.
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Hang());
            using (var gone = new CancellationTokenSource())
            {
                var t = Task.Run(() => FreeVm().RunMcpTaskAsync("t5-gone", "wait", null, null, ProjB, gone.Token));
                await WaitUntil(() => Vm.IsGenerating, 10000);
                gone.Cancel();
                var o = await Task.WhenAny(t, Task.Delay(20000)) == t ? await t : null;
                await Render();
                Check("P4 the caller disconnecting mid-run stops it: isError, and the chat says why",
                    o is { IsError: true } && Vm.Messages.Any(m => m.Content.Contains(L("chat.pipe.callerGone"))) && !Vm.IsGenerating && WindowMatchesChat(), o?.Text ?? "no outcome");
            }

            // P5 — Stop while a permission prompt is open: the prompt is retired, the run returns isError.
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Reply("I will run it.\n[ACTION: run_command]\ncommand: echo hi\n[/ACTION]"));
            var t5 = Task.Run(() => FreeVm().RunMcpTaskAsync("t5-prompt", "run echo hi", null, null, ProjB, CancellationToken.None));
            bool prompted = await WaitUntil(() => Vm.Messages.Any(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered), 20000);
            var prompt = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.PermissionRequest);
            Vm.StopGenerationCommand.Execute(null);
            var o5 = await Task.WhenAny(t5, Task.Delay(20000)) == t5 ? await t5 : null;
            await Render();
            Check("P5 Stop during a permission prompt retires the prompt and the run returns isError",
                prompted && prompt?.PermissionOutcome == PermissionOutcome.Cancelled && o5 is { IsError: true } && !Vm.IsGenerating,
                $"prompted={prompted} outcome={prompt?.PermissionOutcome} result={o5?.Text}");

            await TestPipeAcceptEdits();
            await TestPipeNoSummary();
        }
        finally
        {
            Vm.NavigateToChatRequested -= onChat;
            Vm.NavigateToEditorRequested -= onEditor;
            Fake.Script.Clear();
        }
    }

    /// <summary>One gated pipe run: returns what Accept edits was for its folder WHILE it ran, the chat's lines, the outcome.</summary>
    static async Task<(bool AcceptOn, List<string> Lines, McpTaskOutcome Outcome)> GatedPipeRun(string taskId, string folder, bool? acceptEdits)
    {
        var perms = Sp.GetRequiredService<PermissionService>();
        var gate = new SemaphoreSlim(0);
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Gated(["Working"], gate, [" — done."]));
        var run = Task.Run(() => FreeVm().RunMcpTaskAsync(taskId, "Edit something.", null, null, folder, CancellationToken.None, acceptEdits: acceptEdits));
        bool started = await WaitUntil(() => Vm.IsGenerating && Vm.Messages.Any(m => m.Role == MessageRole.Assistant && m.IsStreaming), 20000);
        bool on = started && perms.AcceptsEditsIn(folder) && Vm.AcceptEdits;
        var lines = Vm.Messages.Where(m => m.Role == MessageRole.System).Select(m => m.Content).ToList();
        gate.Release();
        var outcome = await Task.WhenAny(run, Task.Delay(20000)) == run ? await run : new McpTaskOutcome("(timed out)", true);
        await Render();
        return (on, lines, outcome);
    }

    static async Task TestPipeAcceptEdits()
    {
        Log("\n## T5 — a pipe caller's acceptEdits:true is the user's decision (confirmed once per folder, never for a drive/user/system folder)");
        var perms = Sp.GetRequiredService<PermissionService>();
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();

        // Declined: the run goes on with every edit asking, the chat and the caller are told.
        ConfirmTitles.Clear();
        ConfirmAnswer = false;
        var (on1, lines1, o1) = await GatedPipeRun("t5-accept-no", ProjB, true);
        Check("P6 acceptEdits:true asks the user in CluadeX first (it used to switch the prompts off for any folder, unasked)",
            ConfirmTitles.Count(t => t == L("chat.acceptEdits.pipeConfirmTitle")) == 1, string.Join(" | ", ConfirmTitles));
        Check("P6 declined → off during the run, the chat says so, the caller's result says so",
            !on1 && lines1.Any(l => l.Contains(LocF("chat.acceptEdits.pipeDeclined", ProjB))) && o1.Text.Contains("declined"), $"on={on1} result={o1.Text}");

        // Approved: on for exactly that folder during the run, the chat says who turned it on; asked once per folder.
        ConfirmTitles.Clear();
        ConfirmAnswer = true;
        var (on2, lines2, o2) = await GatedPipeRun("t5-accept-yes", ProjB, true);
        var (on3, _, _) = await GatedPipeRun("t5-accept-again", ProjB, true);
        Check("P6 approved → on during the run, and the task's chat says the caller turned it on",
            on2 && lines2.Any(l => l.Contains(LocF("chat.acceptEdits.pipeOn", ProjB))) && !o2.IsError, $"on={on2} lines={string.Join(" | ", lines2)}");
        Check("P6 the approval holds for that folder for this session: a second run asks nothing", on3 && ConfirmTitles.Count == 1, $"on={on3} confirms={ConfirmTitles.Count}");
        Check("P6 afterwards the user's own state is back (off), and the window shows the project of the chat on screen", WindowMatchesChat() && !Vm.AcceptEdits && !perms.AcceptsEditsIn(ProjB));

        // Negative control: no acceptEdits → no question, off.
        ConfirmTitles.Clear();
        var (on4, lines4, _) = await GatedPipeRun("t5-accept-none", ProjB, null);
        Check("P6 negative control: without acceptEdits nothing is asked and it stays off", !on4 && ConfirmTitles.Count == 0 && !lines4.Any(l => l.Contains(L("chat.acceptEdits.pipeConfirmTitle"))));

        // A folder it must never cover: refused without a question — CluadeX's own data folder here (a drive root would
        // make this harness open C:\ as a project).
        string unsafeDir = Settings.DataRoot;
        var (on5, lines5, o5) = await GatedPipeRun("t5-accept-unsafe", unsafeDir, true);
        Check("P6 a drive / user / system / data folder is refused without asking: off, a ⚠ line, and the caller is told",
            !on5 && ConfirmTitles.Count == 0 && lines5.Any(l => l.Contains(LocF("chat.acceptEdits.unsafeFolder", unsafeDir))) && o5.Text.Contains("refused"),
            $"on={on5} confirms={ConfirmTitles.Count} result={o5.Text}");
        Check("P6 …and the window shows the project of the chat on screen", WindowMatchesChat());

        // MINOR (re-review): when the user had Accept edits on for that folder themselves, the folder was remembered as
        // approved for pipe callers for the rest of the session — after the user turned it off, the next acceptEdits:true
        // switched it back on unasked, and the chat said "(you allowed it)" though nobody was asked.
        string projC = Path.Combine(PipeRoot, "own-accept-project");
        Directory.CreateDirectory(projC);
        Vm.SetWorkingDirectory(projC);
        bool userOn = perms.SetAcceptEdits(projC);   // the user's own switch, for the folder they have open
        ConfirmTitles.Clear();
        ConfirmAnswer = false;
        var (on6, lines6, _) = await GatedPipeRun("t5-accept-users-own", projC, true);
        Check("P6 the user has Accept edits on there themselves: on for the task, no question, and the chat says it is the user's own setting (not 'you allowed it')",
            userOn && on6 && ConfirmTitles.Count == 0 && lines6.Any(l => l.Contains(LocF("chat.acceptEdits.pipeUserOn", projC)))
            && !lines6.Any(l => l.Contains(LocF("chat.acceptEdits.pipeOn", projC))), $"userOn={userOn} on={on6} confirms={ConfirmTitles.Count} lines={string.Join(" | ", lines6)}");
        perms.SetAcceptEdits(null);                  // the user turns it off
        ConfirmTitles.Clear();
        var (on7, lines7, o7) = await GatedPipeRun("t5-accept-after-off", projC, true);
        Check("P6 …which grants the caller nothing lasting: after the user turns it off, the next acceptEdits:true asks again (declined → off)",
            !on7 && ConfirmTitles.Count(t => t == L("chat.acceptEdits.pipeConfirmTitle")) == 1 && lines7.Any(l => l.Contains(LocF("chat.acceptEdits.pipeDeclined", projC))) && o7.Text.Contains("declined"),
            $"on={on7} confirms={ConfirmTitles.Count} result={o7.Text}");
        perms.SetAcceptEdits(null);
        Vm.SetWorkingDirectory(ProjA);
        ConfirmAnswer = true;
    }

    /// <summary>The live view's end-of-run safety net, driven directly (a scripted provider can't end a run with tool
    /// calls and no text): tools ran → "no summary", not an error; nothing at all → the red "no reply".</summary>
    static async Task TestPipeNoSummary()
    {
        Log("\n## T5 — a pipe run that ran tools but wrote no summary is not shown as an error (same as a typed turn)");
        await FreshChat();
        var liveType = typeof(ChatViewModel).GetNestedType("LiveRunView", BindingFlags.NonPublic)!;
        object NewLive() => Activator.CreateInstance(liveType, BindingFlags.Public | BindingFlags.Instance, null, [Vm, Vm.CurrentSession, null], null)!;
        var finish = liveType.GetMethod("Finish")!;
        finish.Invoke(NewLive(), [null, 3]);
        var withTools = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        finish.Invoke(NewLive(), [null, 0]);
        var nothing = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        await Render();
        Check("P7 tools ran, no text → the 'no summary' note, not an error", withTools != null && withTools.Content.Contains(LocF("chat.agent.noSummary", 3)) && !withTools.HasError, withTools?.Content);
        Check("P7 negative control: nothing at all → the red 'no reply'", nothing != null && !ReferenceEquals(nothing, withTools) && nothing.HasError && nothing.Content.Contains(L("chat.agent.noReply")), nothing?.Content);
    }

    // ═══════════════════════════════════════════ T5: Accept edits ═══════════════════════════════════════════

    /// <summary>Run one tool through the shipping gate; a prompt that appears is answered Deny and counted.</summary>
    static async Task<(ToolResult Result, bool Prompted)> Gate(ToolCall call)
    {
        var tools = Sp.GetRequiredService<AgentToolService>();
        int before = Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest);
        var task = tools.ExecuteToolAsync(call);
        bool prompted = false;
        while (!task.IsCompleted)
        {
            var open = Vm.Messages.FirstOrDefault(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered);
            if (open != null) { prompted = true; open.ResolvePermission(PermissionOutcome.Denied); }
            await Task.Delay(30);
        }
        return (await task, prompted || Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest) > before);
    }

    /// <summary>The Approvals box read as the old Accept edits switch: checked = "edits".</summary>
    sealed class ApprovalView(ComboBox box)
    {
        public bool? IsChecked => box == null ? null : (string)box.SelectedValue == "edits";
    }

    static async Task TestControlStrip()
    {
        Log("\n## U7 — the control strip under the input: model, approvals, budget, what it costs");
        await FreshChat();
        var tools = Sp.GetRequiredService<AgentToolService>();
        var cost = Sp.GetRequiredService<CostTrackingService>();

        // Model box: the models of every API provider that has a key; a cloud pick is the paid tier.
        Settings.UpdateSettings(s => s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = "sk-ant-harness-not-a-key", SelectedModel = "claude-sonnet-5-5" });
        await Render();
        var modelBox = (ComboBox)View.FindName("ChatModelBox");
        Check("U7 model box: shown once a provider has a key, listing its models", modelBox is { IsVisible: true }
            && Vm.ChatModelChoices.Any(c => c.Model == "claude-sonnet-5-5") && Vm.ChatModelChoices.Any(c => c.Model == "claude-haiku-5-5"),
            $"{Vm.ChatModelChoices.Count} choices");
        Check("U7 model box: nothing selected while a model on this PC answers", Vm.SelectedChatModel == null);
        Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Model == "claude-haiku-5-5");
        await Task.Delay(100); await Render();
        Check("U7 model box: a cloud model without a licence is refused, says why, and changes nothing",
            Providers.ActiveProviderType == AiProviderType.Local && Settings.Settings.ProviderConfigs["Anthropic"].SelectedModel == "claude-sonnet-5-5"
            && Vm.StatusText == L("settings.activationRequired") && Vm.SelectedChatModel == null, Vm.StatusText);

        // Approvals: ask first → allow everything (confirmed) → back.
        Vm.SetWorkingDirectory(ProjA);
        await Render();
        var approvals = (ComboBox)View.FindName("ApprovalBox");
        Check("U7 approvals: three picks, \"ask first\" by default", Vm.ApprovalChoices.Count == 3 && Vm.ApprovalMode == "ask" && (string)approvals.SelectedValue == "ask");
        ConfirmTitles.Clear(); ConfirmAnswer = false;
        Vm.ApprovalMode = "all";
        await Render();
        Check("U7 approvals: \"allow everything\" asks first; declined, it stays on ask", ConfirmTitles.Contains(L("chat.approval.allConfirmTitle")) && Vm.ApprovalMode == "ask" && (string)approvals.SelectedValue == "ask");

        // A prompt already waiting is answered by switching to "allow everything".
        var waiting = tools.ExecuteToolAsync(new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command", Arguments = new() { ["command"] = "echo pending" } });
        ChatMessage pending = null;
        await WaitUntil(() => (pending = Vm.Messages.FirstOrDefault(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered)) != null, 5000);
        ConfirmAnswer = true;
        Vm.ApprovalMode = "all";
        await Render();
        bool pendingRan = await WaitUntil(() => waiting.IsCompleted, 10000) && waiting.Result.Success;
        Check("U7 approvals: confirmed — the chat says so, and the prompt that was waiting is approved",
            Vm.ApprovalMode == "all" && Vm.Messages.Any(m => m.Role == MessageRole.System && m.Content.Contains(L("chat.approval.allOn")))
            && pending?.PermissionOutcome == PermissionOutcome.Allowed && pendingRan, pending?.PermissionOutcome.ToString());
        var (cmd, cmdAsked) = await Gate(new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command", Arguments = new() { ["command"] = "echo hi" } });
        Check("U7 approvals: in \"allow everything\" a command runs without a prompt", !cmdAsked && cmd.Success, cmd.Error);
        bool auto = (bool)typeof(ChatViewModel).GetMethod("AutoApproves", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Vm, [new PermissionRequestInfo { ToolName = "mcp__chrome__javascript_tool", AlwaysAsk = true }, Vm.CurrentSession])!;
        bool asked = !auto;
        Check("U7 approvals: a request that always asks (code in the browser, the user's own Ask rule) still asks", asked);
        await FreshChat();
        Check("U7 approvals: per chat — a new chat starts on ask first", Vm.ApprovalMode == "ask");
        var (cmd2, cmd2Asked) = await Gate(new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command", Arguments = new() { ["command"] = "echo again" } });
        Check("U7 approvals: …and a command there asks again", cmd2Asked);

        // Budget box + the spend guard (the real CodeAgentService method, the real ask_user row).
        var budgetBox = (ComboBox)View.FindName("BudgetBox");
        Check("U7 budget: $0.50 … $20 and No limit, $2 by default", Vm.BudgetChoices.Count == 7 && Vm.RunBudgetUsd == 2m
            && Vm.BudgetChoices.Any(c => c.Usd == 0m && c.Label == L("chat.budget.none")), string.Join(",", Vm.BudgetChoices.Select(c => c.Label)));
        var agent = Sp.GetRequiredService<CodeAgentService>();
        var guard = typeof(CodeAgentService).GetMethod("ContinueWithinBudgetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<(bool Go, ChatMessage Q)> Guarded(AgentRunScope scope, string answer)
        {
            var t = Task.Run(async () =>
            {
                AgentRunScope.Enter(scope);
                return await (Task<bool>)guard.Invoke(agent, [false, CancellationToken.None])!;
            });
            ChatMessage q = null;
            if (answer != null)
            {
                await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 5000);
                q?.AnswerQuestion(answer);
            }
            await WaitUntil(() => t.IsCompleted, 5000);
            return (t.IsCompleted && t.Result, q);
        }
        var under = new AgentRunScope("budget-under", null);
        under.AddCost(1.2m);
        var (goUnder, qUnder) = await Guarded(under, null);
        Check("U7 budget: under it, the run goes on without a word", goUnder && !Vm.Messages.Any(m => m.Role == MessageRole.Question && !m.QuestionAnswered));
        var over = new AgentRunScope("budget-over", null);
        over.AddCost(2.3m);
        var (goStop, qStop) = await Guarded(over, "Stop here");
        Check("U7 budget: past it, the run asks in the chat with what it spent — \"Stop here\" stops it",
            !goStop && qStop != null && qStop.Content.Contains("$2.30") && qStop.QuestionOptions.Count == 2, qStop?.Content);
        var (goOn, _) = await Guarded(over, qStop?.QuestionOptions[0] ?? "Go on");
        Check("U7 budget: \"go on\" allows another full budget from there", goOn && over.BudgetGrantedUsd == 2.3m, over.BudgetGrantedUsd.ToString());
        var (goNext, qNext) = await Guarded(over, null);
        Check("U7 budget: …so the next step goes on without asking again", goNext && qNext == null);
        Vm.RunBudgetUsd = 0m;
        await Render();
        var (goNoLimit, _) = await Guarded(new AgentRunScope("budget-none", null) , null);
        Check("U7 budget: \"No limit\" never asks, and the pick is saved", goNoLimit && Settings.Settings.RunBudgetUsd == 0m && budgetBox.SelectedValue is decimal d0 && d0 == 0m);
        Vm.RunBudgetUsd = 2m;

        // Spend: a request inside a run counts for that run and for today; today survives a restart.
        var runScope = new AgentRunScope("spend", null);
        decimal todayBefore = cost.TodayCostUsd;
        await Task.Run(() =>
        {
            AgentRunScope.Enter(runScope);
            cost.RecordUsage("claude-sonnet-5-5", 1000, 1000, 0, 100_000, cacheWrite1hTokens: 100_000);
        });
        // 1000 in × $2 + 1000 out × $10 + 100k 1-hour writes × $4 (2x input) per MTok = $0.412
        Check("U7 spend: a request in a run is that run's cost; 1-hour cache writes are billed at 2x input",
            runScope.CostUsd == 0.412m && cost.TodayCostUsd - todayBefore == 0.412m, $"run={runScope.CostUsd} today+={cost.TodayCostUsd - todayBefore}");
        cost.RecordUsage("claude-sonnet-5-5", 1000, 0);
        Check("U7 spend: one made outside any run counts only for today", runScope.CostUsd == 0.412m);
        cost.FlushLedger();
        var reread = new CostTrackingService(Settings);
        Check("U7 spend: today's total is kept in usage-daily.json and read back after a restart",
            File.Exists(Path.Combine(Settings.DataRoot, "usage-daily.json")) && reread.TodayCostUsd == cost.TodayCostUsd, $"{reread.TodayCostUsd} vs {cost.TodayCostUsd}");
        await Task.Delay(50); await Render();
        Check("U7 spend: the top strip shows today's spend", Vm.TodayCostText.StartsWith("Today $"), Vm.TodayCostText);

        // A look at the real page, in both languages (bin/…/chat-strip-th.png, chat-strip-en.png) — as Claude answers
        // (the setting only: the fake provider keeps answering), so the model and budget boxes show.
        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Anthropic);
        await WaitUntil(() => Vm.EstimateText.Length > 0, 5000);   // the estimate is worked out off the UI thread
        foreach (var lang in new[] { "th", "en" })
        {
            LocSvc.SetLanguage(lang);
            await Task.Delay(150); await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, $"chat-strip-{lang}.png"));
        }
        // …and in every theme (Thai), with a short conversation on screen: the redesign has to hold in all three.
        var themes = Sp.GetRequiredService<ThemeService>();
        LocSvc.SetLanguage("th");
        bool stepsBefore = Vm.ShowAgentInternals;
        Vm.ShowAgentInternals = true;   // the tool rows are part of the look
        Vm.Messages.Clear();
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยตรวจฟิสิกส์ลูกสนุกให้สมจริงขึ้น แล้วรันเกมทดสอบ" });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "ได้ครับ ผมจะตรวจโครงสร้างโปรเจกต์ก่อน แล้วปรับค่าการชนของลูกให้สมจริงขึ้น จากนั้นรันเกมเพื่อทดสอบ" });
        Vm.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System, Content = "plan",
            PlanHeader = L("chat.plan.working").Replace("{0}", "4").Replace("{1}", "5"), PlanElapsed = "1m 24s",
            PlanItems =
            [
                new PlanItemView(1, "ตรวจสอบโครงสร้างโปรเจกต์และไฟล์ที่เกี่ยวข้อง", "done"),
                new PlanItemView(2, "วิเคราะห์โค้ดฟิสิกส์ลูกสนุก", "done"),
                new PlanItemView(3, "แก้ไขค่าการชนและแรงเสียดทาน", "done"),
                new PlanItemView(4, "รันโปรเจกต์เพื่อทดสอบ", "done"),
                new PlanItemView(5, "สรุปผลการแก้ไข", "active"),
            ],
        });
        ChatMessage Tool(string kind, string verbKey, string target, string time, bool ok = true) => new()
        {
            Role = MessageRole.ToolAction, ToolKind = kind, ToolVerb = L(verbKey), ToolTarget = target, ToolName = kind,
            Content = target, ToolSuccess = ok, ToolElapsedTime = time, CollapsedToolNames = [kind],
        };
        Vm.Messages.Add(Tool("read_file", "tool.verb.read", "project.godot", "0.3s"));
        Vm.Messages.Add(Tool("read_file", "tool.verb.read", "scripts/main.gd", "0.8s"));
        Vm.Messages.Add(Tool("read_file", "tool.verb.read", "scripts/Ball.gd", "0.7s"));
        var edit = Tool("edit_file", "tool.verb.edit", "scripts/main.gd", "1.2s");
        edit.DiffAdded = 27; edit.DiffRemoved = 12;
        edit.DiffLines =
        [
            new EditDiffLine { Kind = EditDiffKind.Hunk, Text = "@@ -45 +45 @@" },
            new EditDiffLine { Kind = EditDiffKind.Context, Text = "func _on_ball_collided(ball: RigidBody2D, other: RigidBody2D):" },
            new EditDiffLine { Kind = EditDiffKind.Remove, Text = "    var impulse = ball.linear_velocity.length()" },
            new EditDiffLine { Kind = EditDiffKind.Remove, Text = "    if impulse > 200:" },
            new EditDiffLine { Kind = EditDiffKind.Add, Text = "    var impulse = ball.linear_velocity.length()" },
            new EditDiffLine { Kind = EditDiffKind.Add, Text = "    var energy = clamp(impulse * 0.85, 50, 800)" },
            new EditDiffLine { Kind = EditDiffKind.Add, Text = "    ball.linear_velocity *= 0.96  # rolling friction" },
            new EditDiffLine { Kind = EditDiffKind.Context, Text = "        play_collision_sound()" },
        ];
        Vm.Messages.Add(edit);
        var runRow = Tool("run_command", "tool.verb.run", "godot --headless --export-release Web build/web", "12.4s");
        runRow.ToolOutput = "Godot Engine v4.2.2.stable.official\nExport complete: build/web/index.html";
        Vm.Messages.Add(runRow);
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "ปรับแล้วครับ:\n- แรงเสียดทานผ้า **0.96** (เดิม 0.9)\n- ลูกชนกันเสียพลังงาน 5%\n\nรันเกมผ่าน ไม่มี error" });
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "💸 รอบนี้ใช้ $0.42 · 12 ขั้น · ~$0.035/ขั้น · อ่านจาก cache 91%" });
        Vm.ShowCompactionToast = false;
        await Render();
        foreach (var id in new[] { ThemeIds.Classic, ThemeIds.Midnight, ThemeIds.NeonCyber })
        {
            themes.Apply(id, persist: false);
            await Task.Delay(200); await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, $"chat-{id.ToLowerInvariant()}-th.png"));
        }
        themes.Apply(ThemeIds.Midnight, persist: false);
        Vm.ShowAgentInternals = stepsBefore;
        LocSvc.SetLanguage("en");
        await Render();
        await WaitUntil(() => Vm.EstimateText.Length > 0, 5000);
        Check("U7 estimate: as Claude, the strip says what a step should cost and how many steps the budget buys",
            Vm.EstimateText.Contains("/step") && Vm.EstimateText.Contains("$2 ≈") && Vm.EstimateTip.Contains("claude-sonnet-5-5"),
            Vm.EstimateText + " | " + Vm.EstimateTip.Split('\n')[0]);
        Check("U7 estimate: every model in the box shows its price (input / output per MTok)",
            Vm.ChatModelChoices.Where(c => c.Provider is AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini)
                .All(c => c.PriceLabel.StartsWith("$") && c.PriceLabel.Contains(" / $"))
            && Vm.ChatModelChoices.First(c => c.Model == "claude-sonnet-5-5").PriceLabel == "$2 / $10",
            Vm.ChatModelChoices.First(c => c.Model == "claude-sonnet-5-5").PriceLabel);
        Check("U7 look: as Claude, the model box shows the saved model and the budget box shows $2",
            Vm.SelectedChatModel?.Model == "claude-sonnet-5-5" && budgetBox.IsVisible && budgetBox.SelectedValue is decimal d2 && d2 == 2m,
            $"{Vm.SelectedChatModel?.Model} budgetVisible={budgetBox.IsVisible}");

        // BrainX's cowork room picks through the same box (cluadex.choose_model over the cluadex-mcp pipe).
        string effortBefore = Vm.ReasoningEffort;
        string? why = await Vm.ChooseFromOutsideAsync("claude-haiku-5-5", "high");
        await Render();
        Check("U7 outside pick: BrainX's room switches the chat's model and effort through the model box",
            why == null && Vm.SelectedChatModel?.Model == "claude-haiku-5-5"
            && Settings.Settings.ProviderConfigs["Anthropic"].SelectedModel == "claude-haiku-5-5" && Vm.ReasoningEffort == "high",
            why ?? $"{Vm.SelectedChatModel?.Model} effort={Vm.ReasoningEffort}");
        why = await Vm.ChooseFromOutsideAsync("qwen2.5-coder-7b-q4_k_m.gguf", null);
        Check("U7 outside pick: a model the box does not list (one on this PC) is refused by name, nothing changes",
            why != null && why.Contains("qwen2.5-coder-7b-q4_k_m.gguf") && Vm.SelectedChatModel?.Model == "claude-haiku-5-5", why ?? "accepted");
        why = await Vm.ChooseFromOutsideAsync(null, "turbo");
        Check("U7 outside pick: an effort level CluadeX does not have is refused", why != null && Vm.ReasoningEffort == "high", why ?? "accepted");
        await Vm.ChooseFromOutsideAsync("claude-sonnet-5-5", effortBefore == EffortLevels.Auto ? null : effortBefore);
        Vm.ReasoningEffort = effortBefore;
        Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);

        // A paid run ends: one line in its chat with what it cost; its real numbers feed the next estimate.
        var runType = typeof(ChatViewModel).GetNestedType("ChatRun", BindingFlags.NonPublic)!;
        var fakeRun = Activator.CreateInstance(runType)!;
        runType.GetProperty("Session")!.SetValue(fakeRun, Vm.CurrentSession);
        runType.GetProperty("Messages")!.SetValue(fakeRun, Vm.Messages);
        var ended = new AgentRunScope("ended", null);
        ended.AddUsage(0.30m, 100, 2_000, 0, 30_000, true);
        ended.AddUsage(0.20m, 100, 1_000, 60_000, 6_000, true);
        runType.GetProperty("Scope")!.SetValue(fakeRun, ended);
        typeof(ChatViewModel).GetMethod("PostRunCostSummary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, [fakeRun]);
        var summary = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.System && m.Content.StartsWith("💸"));
        Check("U7 spend: a paid run ends with one line — cost, steps, per step, cache share, tokens out",
            summary != null && summary.Content.Contains("$0.50") && summary.Content.Contains("2 steps") && summary.Content.Contains("$0.25/step")
            && summary.Content.Contains("62%") && Field<int>(Vm, "_measuredOutPerStep") == 1500, summary?.Content);

        // Readable text: the strip and the bars use 12-13 px, and the Credit button shows its words.
        Check("U7 text: the strip's boxes use 13 px", approvals.FontSize >= 13 && budgetBox.FontSize >= 13 && modelBox.FontSize >= 13,
            $"{approvals.FontSize}/{budgetBox.FontSize}/{modelBox.FontSize}");
        Settings.UpdateSettings(s => s.ProviderConfigs.Remove("Anthropic"));
        await Task.Delay(50); await Render();
        var noKey = Vm.ChatModelChoices.FirstOrDefault(c => c.Model == "claude-sonnet-5-5");
        Check("U7 text: no key → Claude's models stay listed, marked as needing a key (every vendor is in the box)",
            noKey is { NeedsKey: true } && noKey.PriceLabel.Contains(L("chat.model.noKeyLabel")), noKey?.PriceLabel);
        // The credit link moved to the window's title bar (the "today" chip's menu): MainWindow, not this view.
        Check("U7 text: the composer's \"+\" menu carries the agent switches and the chat actions",
            ((Button)View.FindName("MoreButton")).ContextMenu.Items.OfType<MenuItem>().Count() >= 9);
    }

    // ═══════════════════════════════════════════ U8 ═══════════════════════════════════════════
    static async Task TestUsageWindow()
    {
        Log("\n## U8 — the spend window: paid vs without savings, 14 days of bars, where the savings came from, credit left");
        // 14 seeded days in the harness's portable data root (never ~/.cluadex), read by a fresh tracker.
        string ledger = Path.Combine(Settings.DataRoot, "usage-daily.json");
        string? ledgerBefore = File.Exists(ledger) ? File.ReadAllText(ledger) : null;
        var s0 = Settings.Settings;
        (decimal? bal, DateTime? at, decimal spentThatDay) balanceBefore = (s0.CreditBalanceUsd, s0.CreditBalanceSetAt, s0.CreditBalanceSpentThatDay);
        UsageWindow? win = null;
        try
        {
            var days = new Dictionary<string, DailyUsage>();
            var rnd = new Random(7);
            for (int i = 13; i >= 0; i--)
            {
                decimal paid = i % 5 == 3 ? 0 : Math.Round(0.5m + (decimal)rnd.NextDouble() * 6m, 2);
                decimal cache = paid * 2.4m, ts = paid * 0.6m, comp = paid * 0.3m, batch = i % 4 == 0 ? paid * 0.2m : 0;
                days[DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)] = new DailyUsage
                {
                    Usd = paid, Requests = paid == 0 ? 0 : 20 + i * 3, BaselineUsd = paid + cache + ts + comp + batch,
                    SavedCacheUsd = cache, SavedToolSearchUsd = ts, SavedCompactionUsd = comp, SavedBatchUsd = batch,
                };
            }
            File.WriteAllText(ledger, System.Text.Json.JsonSerializer.Serialize(days));
            var tracker = new CostTrackingService(Settings);
            var anchor = DateTime.Today.AddDays(-6).AddHours(9);
            Settings.UpdateSettings(s => { s.CreditBalanceUsd = 50m; s.CreditBalanceSetAt = anchor; s.CreditBalanceSpentThatDay = 0.25m; });

            var vm = new UsageViewModel(tracker, Settings, () => { });
            Check("U8 numbers: three periods, 14 bars, four saving rows", vm.Periods.Count == 3 && vm.Bars.Count == 14 && vm.Savings.Count == 4,
                $"{vm.Periods.Count}/{vm.Bars.Count}/{vm.Savings.Count}");
            double tallest = vm.Bars.Max(b => b.PaidHeight + b.SavedHeight);
            Check("U8 bars: the tallest bar (paid + saved = without savings) fills the chart; a day with no use is empty",
                Math.Abs(tallest - UsageViewModel.ChartHeight) < 0.01 && vm.Bars.Any(b => b.PaidHeight == 0 && b.SavedHeight == 0), $"tallest {tallest:F2}");
            var inv = System.Globalization.CultureInfo.InvariantCulture;   // ledger keys are Gregorian (a Thai UI culture is Buddhist-era)
            decimal spent = days.Where(kv => DateTime.ParseExact(kv.Key, "yyyy-MM-dd", inv) > anchor.Date).Sum(kv => kv.Value.Usd)
                            + Math.Max(0, days[anchor.Date.ToString("yyyy-MM-dd", inv)].Usd - 0.25m);
            Check("U8 credit: entered $50 six days ago, minus what was spent after it (that morning's $0.25 not counted)",
                vm.HasBalance && vm.BalanceText.Contains(UsageViewModel.Usd(50m - spent)), $"{vm.BalanceText} expected {UsageViewModel.Usd(50m - spent)}");
            Check("U8 credit input: '$12.50', '12,50' and '25' parse; 'abc' and '-3' don't",
                UsageViewModel.TryParseUsd("$12.50", out var a1) && a1 == 12.5m && UsageViewModel.TryParseUsd("12,50", out var a2) && a2 == 12.5m
                && UsageViewModel.TryParseUsd("25", out var a3) && a3 == 25m && !UsageViewModel.TryParseUsd("abc", out _) && !UsageViewModel.TryParseUsd("-3", out _));

            win = new UsageWindow(vm) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = 0 };
            win.Show();
            var themes = Sp.GetRequiredService<ThemeService>();
            LocSvc.SetLanguage("th");
            vm.Refresh();
            foreach (var id in new[] { ThemeIds.Classic, ThemeIds.Midnight, ThemeIds.NeonCyber })
            {
                themes.Apply(id, persist: false);
                await Task.Delay(200);
                win.UpdateLayout();
                var el = (FrameworkElement)win.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(el);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(AppContext.BaseDirectory, $"usage-{id.ToLowerInvariant()}-th.png"))) enc.Save(fs);
            }
            themes.Apply(ThemeIds.Midnight, persist: false);
            Check("U8 look: the window renders in all three themes (usage-*-th.png)", win.ActualHeight > 400, $"{win.ActualWidth:F0}x{win.ActualHeight:F0}");

            vm.BalanceInput = "abc";
            vm.SetBalanceCommand.Execute(null);
            bool refused = vm.BalanceError.Length > 0 && Settings.Settings.CreditBalanceUsd == 50m;
            vm.BalanceInput = "20";
            vm.SetBalanceCommand.Execute(null);
            Check("U8 credit: a bad entry is refused with a message; '20' sets the balance now and counts from here",
                refused && Settings.Settings.CreditBalanceUsd == 20m && vm.BalanceError.Length == 0 && vm.BalanceText.Contains("$20.00"), vm.BalanceText);
            LocSvc.SetLanguage("en");
        }
        finally
        {
            win?.Close();
            if (ledgerBefore != null) File.WriteAllText(ledger, ledgerBefore); else File.Delete(ledger);
            Settings.UpdateSettings(s => { s.CreditBalanceUsd = balanceBefore.bal; s.CreditBalanceSetAt = balanceBefore.at; s.CreditBalanceSpentThatDay = balanceBefore.spentThatDay; });
        }
    }

    // ═══════════════════════════════════════════ U9 ═══════════════════════════════════════════
    static void TestAutoCompact()
    {
        Log("\n## U9 — the compaction toast's tick: compact by itself instead of asking");
        bool autoBefore = Settings.Settings.AutoCompactWhenSuggested;
        try
        {
            Settings.UpdateSettings(s => s.AutoCompactWhenSuggested = false);
            Vm.Messages.Clear();
            for (int i = 0; i < 16; i++)
            {
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "question " + i });
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "answer " + i });
            }
            var show = typeof(ChatViewModel).GetMethod("ShowToast", BindingFlags.NonPublic | BindingFlags.Instance)!;
            show.Invoke(Vm, ["warn", "t", "r"]);
            Check("U9 tick off: the toast asks (shown, nothing compacted)",
                Vm.ShowCompactionToast && !Vm.Messages.Any(m => m.Role == MessageRole.ContextSummary));
            Vm.AutoCompactWhenSuggested = true;
            Check("U9 ticking it on the open toast answers it: the toast closes, the conversation is compacted, the setting is saved",
                !Vm.ShowCompactionToast && Vm.Messages.Any(m => m.Role == MessageRole.ContextSummary) && Settings.Settings.AutoCompactWhenSuggested,
                $"toast={Vm.ShowCompactionToast} markers={Vm.Messages.Count(m => m.Role == MessageRole.ContextSummary)}");
            for (int i = 0; i < 12; i++)
            {
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "more " + i });
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "reply " + i });
            }
            show.Invoke(Vm, ["warn", "t", "r"]);
            Check("U9 tick on: where the toast would ask, it compacts instead — no toast, a second summary marker",
                !Vm.ShowCompactionToast && Vm.Messages.Count(m => m.Role == MessageRole.ContextSummary) == 2,
                $"markers={Vm.Messages.Count(m => m.Role == MessageRole.ContextSummary)}");
        }
        finally
        {
            Vm.AutoCompactWhenSuggested = autoBefore;
            Settings.UpdateSettings(s => s.AutoCompactWhenSuggested = autoBefore);
            Vm.ShowCompactionToast = false;
            Vm.Messages.Clear();
        }
    }

    // ═══════════════════════════════════════════ U10 ══════════════════════════════════════════
    static async Task TestStartupFollowsProject()
    {
        Log("\n## F6 — at startup the restored chat opens its own project (the files panel showed the old folder until a message was sent)");
        Directory.CreateDirectory(ProjA);
        Directory.CreateDirectory(ProjB);
        var last = new ChatSession { Title = "startup chat in A", ProjectPath = ProjA, UpdatedAt = DateTime.Now.AddMinutes(5) };
        last.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "keep going on the parser" });
        last.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "ok" });
        Persist.SaveSession(last);
        string before = Vm.WorkingDirectory;
        await FreshChat();   // an empty chat on screen: tying it to B saves nothing, so "last" stays the newest chat
        Vm.SetWorkingDirectory(ProjB);
        Persist.SaveSession(last);   // saved last, so it is the newest chat whatever earlier tests left behind
        // A second chat page built the way the app builds it at startup (same services, same database).
        var vm2 = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<ChatViewModel>(Sp);
        await WaitUntil(() => vm2.CurrentSession != null, 15000);
        Check("F6 the newest chat is restored and its project (A) is open — not the folder left open before (B)",
            vm2.CurrentSession?.Id == last.Id && string.Equals(vm2.WorkingDirectory, ProjA, StringComparison.OrdinalIgnoreCase),
            $"chat={vm2.CurrentSession?.Title} folder={vm2.WorkingDirectory}");
        Vm.SetWorkingDirectory(before);
    }

    static async Task TestPlanWindow()
    {
        Log("\n## U12 — the plan window: this chat's plan, items added while it works or queued, go back, branch");
        string store = Path.Combine(Settings.DataRoot, "cp-store-" + Guid.NewGuid().ToString("N")[..6]);
        string proj = Path.Combine(Path.GetTempPath(), "cluadex-planwin-" + Guid.NewGuid().ToString("N")[..6]);
        string? branchDir = null;
        PlanWindow? win = null;
        Directory.CreateDirectory(proj);
        try
        {
            static (int, string) G(string dir, params string[] a)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var x in a) psi.ArgumentList.Add(x);
                using var pr = System.Diagnostics.Process.Start(psi)!;
                string o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd();
                pr.WaitForExit();
                return (pr.ExitCode, o);
            }
            string f = Path.Combine(proj, "main.txt");
            File.WriteAllText(f, "v1\n");
            G(proj, "init", "-q"); G(proj, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
            G(proj, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "v1");

            await FreshChat();
            var session = Vm.CurrentSession!;
            session.ProjectPath = proj;
            var cp = new CheckpointService(null, store);
            // Two agent steps of this chat: v1 → v2 (step 1), v2 → v3 (step 2).
            var scope = new AgentRunScope(session.Id, proj);
            AgentRunScope.Enter(scope);
            scope.Step = 1; cp.CaptureBeforeChange(f); File.WriteAllText(f, "v2\n");
            scope.Step = 2; cp.CaptureBeforeChange(f); File.WriteAllText(f, "v3\n");
            AgentRunScope.Enter(null);
            // The agent's last plan for this chat (what todo_write left).
            var plans = (System.Collections.IDictionary)typeof(ChatViewModel).GetField("_lastPlans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            plans[session.Id] = new List<TodoItem> { new() { Content = "Write the parser", Status = "completed" }, new() { Content = "Add tests", Status = "in_progress" } };

            Vm.OpenPlanWindow(session);
            win = Application.Current.Windows.OfType<PlanWindow>().FirstOrDefault();
            Check("U12 the plan window opens for the chat", win != null);
            if (win == null) return;
            var pvm = (PlanWindowViewModel)win.DataContext;
            win.Left = -12000;
            await Task.Delay(300); win.UpdateLayout();
            Check("U12 it shows the chat's plan with done / active marks", pvm.Rows.Count == 2 && pvm.Rows[0].IsDone && pvm.Rows[1].IsActive,
                string.Join(" | ", pvm.Rows.Select(r => r.State + ":" + r.Text)));
            Check("U12 and its steps, newest first", pvm.Steps.Count == 2 && pvm.Steps[0].Step == 2, string.Join(",", pvm.Steps.Select(x => x.Step)));
            Vm.OpenPlanWindow(session);
            Check("U12 opening it again brings the same window forward (one per chat)", Application.Current.Windows.OfType<PlanWindow>().Count() == 1);

            pvm.NewItem = "Update the README";
            pvm.AddItemCommand.Execute(null);
            Check("U12 an item added while the chat is idle is queued, with Start offered", pvm.Rows.Any(r => r.IsQueued && r.Text == "Update the README") && pvm.CanStartQueued,
                pvm.Message);

            LocSvc.SetLanguage("th");
            await Task.Delay(200); win.UpdateLayout();
            SaveWindow(win, Path.Combine(AppContext.BaseDirectory, "plan-window-th.png"));
            LocSvc.SetLanguage("en");

            // Go back to before step 2 → v2; the current v3 is kept as a point.
            pvm.AskRewindCommand.Execute(pvm.Steps[0]);
            Check("U12 going back asks first", pvm.IsConfirmOpen && File.ReadAllText(f) == "v3\n");
            pvm.ConfirmCommand.Execute(null);
            await WaitUntil(() => File.ReadAllText(f) == "v2\n");
            Check("U12 confirmed: the file is as it was before step 2", File.ReadAllText(f) == "v2\n", File.ReadAllText(f) + " " + pvm.Message);

            // Branch from step 1 → a worktree with v1, the project stays v2, a new chat opens there.
            var step1 = pvm.Steps.First(x => x.Step == 1);
            pvm.AskBranchCommand.Execute(step1);
            pvm.ConfirmCommand.Execute(null);
            await WaitUntil(() => Vm.CurrentSession?.ProjectPath is { Length: > 0 } pp && pp != proj, 30000);
            branchDir = Vm.CurrentSession?.ProjectPath;
            Check("U12 branching opens a new chat in a worktree with the files from before step 1",
                branchDir != null && branchDir != proj && File.Exists(Path.Combine(branchDir, "main.txt"))
                && File.ReadAllText(Path.Combine(branchDir, "main.txt")) == "v1\n", $"{branchDir} | {pvm.Message}");
            Check("U12 …and leaves the original project alone", File.ReadAllText(f) == "v2\n");

            // U13 — the plan bar pinned over the chat: progress + the item in progress; a branch shows a chip back to
            // where it came from, and the parent a chip to its branch; a chip opens that chat.
            var branchChat = Vm.CurrentSession!;
            plans[branchChat.Id] = new List<TodoItem> { new() { Content = "Re-check the parser", Status = "completed" }, new() { Content = "Port the fix", Status = "in_progress" }, new() { Content = "Run tests", Status = "pending" } };
            Vm.RefreshStickyPlan();
            await Render();
            var bar = Find<Border>(Win).FirstOrDefault(b => b.Name == "StickyPlanBar");
            Check("U13 the plan bar shows over the chat with 1/3 and the item in progress",
                bar is { IsVisible: true } && Vm.StickyPlanHeader.EndsWith("1/3") && Vm.StickyPlanNow == "Port the fix" && Math.Abs(Vm.StickyPlanProgress - 1.0 / 3) < 0.01,
                $"{Vm.StickyPlanHeader} · {Vm.StickyPlanNow} · visible={bar?.IsVisible}");
            Check("U13 the branch has a chip back to the chat it came from", Vm.StickyBranches.Count == 1 && Vm.StickyBranches[0].IsParent && Vm.StickyBranches[0].SessionId == session.Id,
                string.Join(" | ", Vm.StickyBranches.Select(b => b.Label + " " + b.Title)));
            Vm.IsStickyPlanExpanded = true;
            LocSvc.SetLanguage("th");
            await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-sticky-plan-th.png"));
            LocSvc.SetLanguage("en");
            Vm.IsStickyPlanExpanded = false;
            Vm.GoToChatCommand.Execute(session.Id);
            await WaitUntil(() => Vm.CurrentSession?.Id == session.Id);
            await Render();
            Check("U13 its chip opens the parent chat, which shows a chip to the branch (step 1)",
                Vm.CurrentSession?.Id == session.Id && Vm.StickyBranches.Any(b => !b.IsParent && b.SessionId == branchChat.Id && b.Label.Contains("1")),
                string.Join(" | ", Vm.StickyBranches.Select(b => b.Label + " " + b.Title)));
            await FreshChat();
            Check("U13 a new chat with no plan and no branches shows no bar", !Vm.HasStickyBar);
        }
        finally
        {
            win?.Close();
            AgentRunScope.Enter(null);
            try { if (branchDir != null && branchDir != proj) { var psi = new System.Diagnostics.ProcessStartInfo("git", $"worktree remove --force \"{branchDir}\"") { WorkingDirectory = proj, CreateNoWindow = true, UseShellExecute = false }; System.Diagnostics.Process.Start(psi)!.WaitForExit(); } } catch { }
            foreach (var d in new[] { proj, store, branchDir })
                try { if (d != null && Directory.Exists(d)) { foreach (var fi in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) fi.Attributes = FileAttributes.Normal; Directory.Delete(d, true); } } catch { }
        }
    }

    static void SaveWindow(Window w, string path)
    {
        var el = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static async Task TestLiveEffort()
    {
        Log("\n## U11 — the effort \"auto\" really uses is shown while the run works");
        await FreshChat();
        Vm.ReasoningEffort = "auto";
        Fake.Script.Enqueue(FakeStep.Hang());
        await Send("[U11] a long task");
        await WaitUntil(() => Vm.IsGenerating);
        // The loop's pick for this step (the fake provider runs no smart-effort loop, so the test sets it on the run's scope).
        var runs = (System.Collections.IList)typeof(ChatViewModel).GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Vm)!;
        var scope = (AgentRunScope)runs[0]!.GetType().GetProperty("Scope")!.GetValue(runs[0])!;
        scope.LiveEffort = "high";
        // The fake answers as a model on this PC, where the Effort box is hidden (it changes nothing there); show it to
        // test what it reads for a model that takes effort.
        typeof(ChatViewModel).GetProperty("EffortVisible")!.SetValue(Vm, true);
        await Render();
        var effortBox = Find<ComboBox>(Win).First(c => c.Name == "EffortBox");
        string shown = string.Join(" ", Find<TextBlock>(effortBox).Where(t => t.IsVisible).Select(t => t.Text));
        Check("U11 the effort box reads auto → high while the run uses high", shown.Contains("auto") && shown.Contains("high"), shown);
        Check("U11 the model box carries the live effort too", Vm.LiveEffortSuffix.Contains("auto → high"), Vm.LiveEffortSuffix);
        scope.LiveEffort = "medium";
        await Render();
        shown = string.Join(" ", Find<TextBlock>(effortBox).Where(t => t.IsVisible).Select(t => t.Text));
        Check("U11 …and follows the level as it moves (high → medium)", shown.Contains("medium"), shown);
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle();
        await Render();
        shown = string.Join(" ", Find<TextBlock>(effortBox).Where(t => t.IsVisible).Select(t => t.Text));
        Check("U11 with no run going it reads plain auto again", !shown.Contains("medium") && Vm.LiveEffortLevel == "", shown);
    }

    static async Task TestNarrowComposer()
    {
        Log("\n## U10 — a narrow composer (the chat docked beside the editor) keeps its options in tidy rows");
        double widthBefore = Win.Width;
        var buttons = (FrameworkElement)View.FindName("ComposerButtons");
        var options = (FrameworkElement)View.FindName("ComposerOptions");
        var effortLabel = (FrameworkElement)View.FindName("EffortLabel");
        try
        {
            LocSvc.SetLanguage("th");
            Win.Width = 440;
            await Task.Delay(150); await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, "composer-narrow-th.png"));
            Check("U10 narrow: the mic / send / stop buttons move to a row of their own and the options span the width",
                Grid.GetRow(buttons) == 1 && Grid.GetColumnSpan(options) == 2 && effortLabel.Visibility == Visibility.Collapsed,
                $"row={Grid.GetRow(buttons)} span={Grid.GetColumnSpan(options)} composer={((FrameworkElement)View.FindName("ComposerStrip")).ActualWidth:F0}");
            // Every option row is one height, so a wrapped strip reads as rows, not a staircase.
            var tops = new List<double>();
            foreach (FrameworkElement c in ((Panel)options).Children)
                if (c.IsVisible) tops.Add(Math.Round(c.TranslatePoint(new Point(0, 0), options).Y));
            int rows = tops.Distinct().Count();
            Check("U10 narrow: the options take at most four rows at 440 px (it was one per option)", rows is > 0 and <= 4, $"rows={rows} of {tops.Count} options");
            // The compaction toast in the same narrow chat (owner's screenshot): its actions sit under the text now.
            Vm.CompactionToastTitle = L("chat.toast.toolsTitle");
            Vm.CompactionToastReason = string.Format(L("chat.toast.toolsReason"), 50);
            Vm.CompactionToastSeverity = "info";
            Vm.ShowCompactionToast = true;
            await Task.Delay(150); await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, "toast-narrow-th.png"));
            var compactBtn = FindVisual<Button>(View, b => b.Command == Vm.AcceptCompactionToastCommand);
            var reasonText = FindVisual<TextBlock>(View, t => t.Text == Vm.CompactionToastReason);
            Check("U10 narrow toast: the reason gets the toast's width and \"Compact now\" stays a button, not a tall block",
                compactBtn.ActualHeight < 40 && reasonText.ActualWidth > 250, $"button {compactBtn.ActualHeight:F0}px high, reason {reasonText.ActualWidth:F0}px wide");
            Vm.ShowCompactionToast = false;
            Win.Width = 1100;
            await Task.Delay(150); await Render();
            Check("U10 wide: the buttons go back beside the options and the words come back",
                Grid.GetRow(buttons) == 0 && Grid.GetColumn(buttons) == 1 && effortLabel.Visibility == Visibility.Visible);
        }
        finally
        {
            Win.Width = widthBefore;
            LocSvc.SetLanguage("en");
            await Render();
        }
    }

    static void SaveShot(string path)
    {
        var el = (FrameworkElement)Win.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static T FindVisual<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && match(t)) return t;
            if (FindVisual(c, match) is { } found) return found;
        }
        return null;
    }

    static async Task TestAcceptEdits()
    {
        Log("\n## T5 — Accept edits: file edits inside the project run without a prompt; nothing else does");
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();
        File.WriteAllText(Path.Combine(ProjA, "app.txt"), "one\ntwo\n");
        await Render();
        var approvals = (ComboBox)View.FindName("ApprovalBox");
        var toggle = new ApprovalView(approvals);
        Check("A1 with a project open the Approvals box shows under the input, on \"ask first\" by default", approvals is { IsVisible: true } && toggle.IsChecked == false && !Vm.AcceptEdits);
        Check("A1 its tooltip states the scope", approvals?.ToolTip is string tip && tip.Contains("inside the open project") && tip.Contains("still ask"), approvals?.ToolTip?.ToString());

        ConfirmTitles.Clear();
        ConfirmAnswer = false;
        Vm.AcceptEdits = true;
        await Render();
        Check("A1 switching it on asks first; declining keeps it off", ConfirmTitles.Contains(L("chat.acceptEdits.confirmTitle")) && !Vm.AcceptEdits && toggle?.IsChecked == false);
        ConfirmAnswer = true;
        Vm.AcceptEdits = true;
        await Render();
        Check("A1 confirmed: on, and the switch shows it", Vm.AcceptEdits && toggle?.IsChecked == true);

        var (edit, editAsked) = await Gate(new ToolCall { Type = ToolType.EditFile, ToolName = "edit_file", Arguments = new() { ["path"] = "app.txt", ["find"] = "one", ["replace"] = "ONE" } });
        Check("A2 edit_file inside the project runs without a prompt", !editAsked && edit.Success && File.ReadAllText(Path.Combine(ProjA, "app.txt")).Contains("ONE"), edit.Error);
        var (_, cmdAsked) = await Gate(new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command", Arguments = new() { ["command"] = "echo hi" } });
        Check("A2 run_command still asks", cmdAsked);
        string outside = Path.Combine(PipeRoot, "outside.txt");
        var (_, outAsked) = await Gate(new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file", Arguments = new() { ["path"] = outside, ["content"] = "x" } });
        Check("A2 a write to a path outside the project still asks", outAsked && !File.Exists(outside));

        Vm.SetWorkingDirectory(ProjB);
        await Render();
        Check("A3 switching projects turns Accept edits off", !Vm.AcceptEdits && toggle?.IsChecked == false);
        var (_, bAsked) = await Gate(new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file", Arguments = new() { ["path"] = "new.txt", ["content"] = "x" } });
        Check("A3 …so an edit in the new project asks again", bAsked);
        Vm.SetWorkingDirectory(ProjA);
        Check("A3 back in the first project it stays off (it is per session, never re-enabled behind the user's back)", !Vm.AcceptEdits);

        // LOGIC (review): never for a drive root, the user folder, a system folder or CluadeX's own data — not even
        // when the user ticks it (CluadeX's data folder stands in for C:\ here, which this harness must not open).
        Vm.SetWorkingDirectory(Settings.DataRoot);
        await Render();
        ConfirmTitles.Clear();
        ConfirmAnswer = true;
        Vm.AcceptEdits = true;
        await Render();
        Check("A4 on CluadeX's data folder the switch refuses without asking, says why, and stays off",
            !Vm.AcceptEdits && ConfirmTitles.Count == 0 && toggle?.IsChecked == false
            && Vm.Messages.Any(m => m.Role == MessageRole.System && m.Content.Contains(LocF("chat.acceptEdits.unsafeFolder", Settings.DataRoot))),
            $"on={Vm.AcceptEdits} confirms={ConfirmTitles.Count}");
        Vm.SetWorkingDirectory(ProjA);
        await Render();
    }

    // ═══════════════════════════════════════════ T5: Autonomous build gate ═══════════════════════════════════════════

    /// <summary>
    /// CRITICAL (re-review): with Accept edits and the Autonomous switch on, the loop ran its build/test command over
    /// edits nobody saw. Through the real chat: the agent turn is a brain-status question the app answers itself (no
    /// model), then the loop's verify command — which runs the script the unseen edit wrote — must ask in the chat.
    /// </summary>
    static async Task TestAutonomousBuildGate()
    {
        Log("\n## T5 — the Autonomous loop asks before its build/test after unseen (Accept edits) edits; No → not run, said in the chat");
        var perms = Sp.GetRequiredService<PermissionService>();
        var tools = Sp.GetRequiredService<AgentToolService>();
        string proj = Path.Combine(PipeRoot, "autonomous-project");
        Directory.CreateDirectory(proj);
        string marker = Path.Combine(proj, "auto-ran.txt");
        const string verify = @"scripts\helper.cmd";
        var s = Settings.Settings;
        var keep = (s.AutoVerifyCommand, s.AutoFixMaxIterations, s.AutoReviewEnabled);
        Settings.UpdateSettings(x => { x.AutoVerifyCommand = verify; x.AutoFixMaxIterations = 1; x.AutoReviewEnabled = false; });
        try
        {
            Vm.SetWorkingDirectory(proj);
            await Render();
            async Task<(bool Asked, bool Idle, List<string> Lines)> Turn(bool allow)
            {
                perms.SetAcceptEdits(proj);
                var w = await tools.ExecuteToolAsync(new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                    Arguments = new() { ["path"] = "scripts/helper.cmd", ["content"] = $"@echo ran>> auto-ran.txt\r\n@rem {Guid.NewGuid():N}\r\n" } });
                if (!w.Success) Log("   (write_file failed: " + w.Error + ")");
                await FreshChat();
                Vm.AutonomousMode = true;
                await Send("is the brain connected?");
                ChatMessage prompt = null;
                bool asked = await WaitUntil(() => (prompt = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.PermissionRequest
                    && m.PermissionOutcome == PermissionOutcome.Pending)) != null, 20000);
                bool namesIt = prompt != null && prompt.Content.Contains(verify) && prompt.Content.Contains(L("perm.autoVerify.autonomous"));
                prompt?.ResolvePermission(allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied);
                bool idle = await WaitIdle(30000);
                Vm.AutonomousMode = false;
                return (asked && namesIt, idle, Vm.Messages.Where(m => m.Role == MessageRole.System).Select(m => m.Content).ToList());
            }
            if (File.Exists(marker)) File.Delete(marker);
            string declined = LocF("chat.auto.verifyDeclined", verify);
            var (asked1, idle1, lines1) = await Turn(allow: false);
            Check("T5 Autonomous: after an unseen edit the build/test asks in the chat (naming the command and why); Deny → not run, and a line that stays in the chat says so",
                asked1 && idle1 && !File.Exists(marker) && lines1.Any(l => l == "✋ " + declined),
                $"asked={asked1} idle={idle1} ran={File.Exists(marker)} lines={string.Join(" | ", lines1)}");
            var (asked2, idle2, lines2) = await Turn(allow: true);
            Check("T5 Autonomous control: Allow → the command runs, and no 'not run' line",
                asked2 && idle2 && File.Exists(marker) && !lines2.Any(l => l.Contains(declined)),
                $"asked={asked2} idle={idle2} ran={File.Exists(marker)} lines={string.Join(" | ", lines2)}");
        }
        finally
        {
            Vm.AutonomousMode = false;
            perms.SetAcceptEdits(null);
            Settings.UpdateSettings(x => (x.AutoVerifyCommand, x.AutoFixMaxIterations, x.AutoReviewEnabled) = keep);
            Vm.SetWorkingDirectory(ProjA);
            await Render();
        }
    }

    // ═══════════════════════════════════════════ T5: tool-row thumbnail ═══════════════════════════════════════════

    static byte[] MakeJpeg()
    {
        int w = 64, h = 48, stride = w * 4;
        var px = new byte[stride * h];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 40; px[i + 2] = 220; px[i + 3] = 255; }
        var src = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, stride);
        var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static async Task TestThumbnail()
    {
        Log("\n## T5 — a tool image shows as a thumbnail on its row (the saved file's path — never the bytes)");
        await FreshChat();
        string dir = Path.Combine(Settings.DataRoot, "screens", "chattest");
        Directory.CreateDirectory(dir);
        string jpg = Path.Combine(dir, "001.jpg");
        byte[] bytes = MakeJpeg();
        File.WriteAllBytes(jpg, bytes);
        string b64 = Convert.ToBase64String(bytes);

        var agent = Field<CodeAgentService>(Vm, "_agentService");
        var handler = (Action<ToolResult>)typeof(CodeAgentService).GetField("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(agent);
        handler?.Invoke(new ToolResult
        {
            Type = ToolType.McpTool, ToolName = "mcp__stub__take_screenshot", Success = true,
            Output = $"[image 1: 64x48 attached, saved at {jpg}]", Summary = "MCP (stub/take_screenshot): OK",
            Images = [new ToolImage("image/jpeg", b64, 64, 48, jpg)],
        });
        handler?.Invoke(new ToolResult
        {
            Type = ToolType.McpTool, ToolName = "mcp__stub__take_screenshot", Success = true, Output = "[image 1 attached]", Summary = "no file",
            Images = [new ToolImage("image/jpeg", b64, 64, 48, Path.Combine(dir, "gone.jpg"))],
        });
        await Render();
        var rows = Vm.Messages.Where(m => m.Role == MessageRole.ToolAction).ToList();
        var row = rows.FirstOrDefault(m => m.ThumbnailPath != null);
        Check("T1 the tool row carries the saved file's path", row != null && row.ThumbnailPath == jpg && row.ImageCount == 1, string.Join(" | ", rows.Select(r => r.ThumbnailPath ?? "-")));
        Check("T1 an image whose file is gone gets no thumbnail (no broken picture)", rows.Count == 2 && rows[1].ThumbnailPath == null);
        Check("T1 with Steps off the image row stays on screen (like a diff: something to verify), the plain row collapses",
            !Vm.ShowAgentInternals && row != null && Container(row)?.Visibility == Visibility.Visible && Container(rows[1])?.Visibility == Visibility.Collapsed);
        var img = row == null || Container(row) == null ? null : Find<Image>(Container(row)).FirstOrDefault(i => i.IsVisible && i.Source != null);
        Check("T1 the thumbnail is rendered on the row", img?.Source is System.Windows.Media.Imaging.BitmapSource bs && bs.PixelHeight > 0);
        var open = row == null || Container(row) == null ? null : Find<Button>(Container(row)).FirstOrDefault(b => Equals(b.Tag, jpg));
        Check("T1 the thumbnail is a button carrying the file to open full size; only an existing local image is ever opened",
            open is { IsVisible: true } && ChatViewModel.IsOpenableImage(jpg) && !ChatViewModel.IsOpenableImage("https://example.com/a.jpg")
            && !ChatViewModel.IsOpenableImage(@"\\server\share\a.jpg") && !ChatViewModel.IsOpenableImage(Path.Combine(dir, "a.exe")));
        bool onMessage = typeof(ChatMessage).GetProperties().Where(p => p.PropertyType == typeof(string))
            .Any(p => (p.GetValue(row) as string)?.Contains(b64[..60]) == true);
        Vm.SaveNow();
        var stored = Persist.LoadSession(Vm.CurrentSession!.Id);
        bool inDb = stored?.Messages.Any(m => (m.Content + m.ToolOutput + m.ToolSummary).Contains(b64[..60])) == true;
        Check("T1 the image bytes are on neither the chat message nor in codex.db", row != null && !onMessage && !inDb && stored != null);
    }
}

// ═══════════════════════════════════════════ fake provider ═══════════════════════════════════════════

internal sealed class FakeStep
{
    public string[] Tokens;
    public Exception Error;
    public bool HangUntilCancelled;
    /// <summary>Gated: <see cref="Tokens"/> stream, then the step waits for <see cref="Gate"/>, then <see cref="After"/>.</summary>
    public SemaphoreSlim Gate;
    public string[] After = [];
    public static FakeStep Reply(params string[] tokens) => new() { Tokens = tokens };
    public static FakeStep Throw(Exception ex) => new() { Error = ex };
    public static FakeStep Hang() => new() { HangUntilCancelled = true };
    public static FakeStep Gated(string[] before, SemaphoreSlim gate, string[] after) => new() { Tokens = before, Gate = gate, After = after };
}

internal sealed record FakeCall(List<(MessageRole Role, string Content)> History, string Input);

/// <summary>An offline provider: replays a script of steps and records what it was sent.</summary>
internal sealed class FakeProvider : IAiProvider
{
    public readonly Queue<FakeStep> Script = new();
    /// <summary>Scripts keyed by a marker in the conversation ("[A1]"): with several chats running at once, each
    /// conversation takes its own steps instead of whichever is next in <see cref="Script"/>.</summary>
    public readonly Dictionary<string, Queue<FakeStep>> ScriptFor = new();
    public readonly List<FakeCall> Calls = new();
    public string ProviderId => "Fake";
    public string DisplayName => "Fake provider";
    public bool IsReady { get; set; } = true;
    public bool IsLoading => false;
    public string StatusMessage => IsReady ? "ready" : "API key not configured";
    public event Action<string> OnStatusChanged;
    public event Action<bool> OnLoadingChanged;
    public event Action<string> OnError;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<string> ChatAsync(List<ChatMessage> history, string userMessage, string systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(new FakeCall(history.Select(h => (h.Role, h.Content)).ToList(), userMessage));
        FakeStep step;
        lock (Script)
        {
            // The marker in the new message first; else the newest one in the conversation that still has steps.
            var key = ScriptFor.Keys.FirstOrDefault(k => userMessage.Contains(k))
                ?? history.AsEnumerable().Reverse().SelectMany(h => ScriptFor.Keys.Where(k => h.Content?.Contains(k) == true))
                          .FirstOrDefault(k => ScriptFor[k].Count > 0);
            step = key != null && ScriptFor[key].Count > 0 ? ScriptFor[key].Dequeue()
                 : Script.Count > 0 ? Script.Dequeue() : FakeStep.Reply("ok");
        }
        await Task.Delay(30, ct);
        if (step.HangUntilCancelled) await Task.Delay(Timeout.Infinite, ct);
        if (step.Error != null) throw step.Error;
        foreach (var t in step.Tokens)
        {
            await Task.Delay(10, ct);
            yield return t;
        }
        if (step.Gate != null)
        {
            await step.Gate.WaitAsync(ct);
            foreach (var t in step.After)
            {
                await Task.Delay(10, ct);
                yield return t;
            }
        }
    }

    public async Task<string> GenerateAsync(List<ChatMessage> history, string userMessage, string systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var t in ChatAsync(history, userMessage, systemPrompt, ct)) sb.Append(t);
        return sb.ToString();
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
    public void Dispose() { }
}
