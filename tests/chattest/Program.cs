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
internal static class Program
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
    static readonly List<string> ConfirmTitles = new();
    static bool ConfirmAnswer = true;

    [STAThread]
    static int Main()
    {
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

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services]);
        var sp = services.BuildServiceProvider();

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
        await TestSwitchMidTurn();            // L2
        await TestContextWindow();            // L6 + L7 label
        await TestAutoRunToggle();            // U5
        await TestScrollFollow();             // U6
        await TestCodeBlockStop();            // L12
        await TestNewChatNavigation();        // MINOR: title-bar New chat
        TestPureHelpers();                    // L3 classifier, L8 dedupe, U9 redaction, U2 palette

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

    static async Task TestSwitchMidTurn()
    {
        Log("\n## L2 — switching chats mid-turn stops the turn; its output stays in its own chat");
        await FreshChat();
        var chatA = Vm.CurrentSession!;
        Fake.Script.Enqueue(FakeStep.Hang());
        await Send("long task in A");
        Check("L2 the turn in chat A is running", await WaitUntil(() => Vm.IsGenerating));

        var chatB = new ChatSession { Title = "chat B" };
        chatB.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "hi B" });
        chatB.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "hello B" });
        Persist.SaveSession(chatB);
        Vm.Sessions.Insert(0, new ChatSession { Id = chatB.Id, Title = chatB.Title });

        ConfirmAnswer = false;
        ConfirmTitles.Clear();
        Vm.LoadSessionCommand.Execute(chatB.Id);
        await Task.Delay(300);
        Check("L2 the user is asked before a running reply is stopped", ConfirmTitles.Contains(L("chat.busy.title")));
        Check("L2 declining keeps chat A and its running turn", ReferenceEquals(Vm.CurrentSession, chatA) && Vm.IsGenerating);

        ConfirmAnswer = true;
        Vm.LoadSessionCommand.Execute(chatB.Id);
        Check("L2 accepting stops the turn and opens chat B", await WaitUntil(() => Vm.CurrentSession?.Id == chatB.Id) && !Vm.IsGenerating);
        await Render();
        Check("L2 chat B shows only its own messages", Vm.Messages.Any(m => m.Content == "hello B") && !Vm.Messages.Any(m => m.Content.Contains("long task in A")));
        var storedA = Persist.LoadSession(chatA.Id);
        Check("L2 chat A keeps its question and the interrupted note", storedA != null
            && storedA.Messages.Any(m => m.Content == "long task in A") && storedA.Messages.Any(m => m.IsInterrupted || m.Content.Contains(L("chat.interruptedNote"))),
            storedA == null ? "not stored" : string.Join(" | ", storedA.Messages.Select(m => m.Content)));

        // A late tool callback from chat A's turn (queued before the switch) must land in chat A, not B.
        SetField(Vm, "_turnSession", chatA);
        var agent = Field<CodeAgentService>(Vm, "_agentService");
        var handler = (Action<ToolResult>)typeof(CodeAgentService).GetField("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(agent);
        handler?.Invoke(new ToolResult { ToolName = "read_file", Success = true, Output = "late output", Summary = "late tool row from A" });
        await Render();
        SetField(Vm, "_turnSession", null);
        Check("L2 a late tool row from chat A's turn does not appear in chat B", !Vm.Messages.Any(m => m.Content == "late tool row from A"));
        Check("L2 …it is stored in chat A instead", Persist.LoadSession(chatA.Id)?.Messages.Any(m => m.Content == "late tool row from A") == true);
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
        var labels = Find<CheckBox>(View).Select(c => c.Content as string).Where(s => s != null).ToList();
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
}

// ═══════════════════════════════════════════ fake provider ═══════════════════════════════════════════

internal sealed class FakeStep
{
    public string[] Tokens;
    public Exception Error;
    public bool HangUntilCancelled;
    public static FakeStep Reply(params string[] tokens) => new() { Tokens = tokens };
    public static FakeStep Throw(Exception ex) => new() { Error = ex };
    public static FakeStep Hang() => new() { HangUntilCancelled = true };
}

internal sealed record FakeCall(List<(MessageRole Role, string Content)> History, string Input);

/// <summary>An offline provider: replays a script of steps and records what it was sent.</summary>
internal sealed class FakeProvider : IAiProvider
{
    public readonly Queue<FakeStep> Script = new();
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
        var step = Script.Count > 0 ? Script.Dequeue() : FakeStep.Reply("ok");
        await Task.Delay(30, ct);
        if (step.HangUntilCancelled) await Task.Delay(Timeout.Infinite, ct);
        if (step.Error != null) throw step.Error;
        foreach (var t in step.Tokens)
        {
            await Task.Delay(10, ct);
            yield return t;
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
