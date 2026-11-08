using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>VB — the vibe-coding helpers on the real chat page (owner 2026-11-08): the change card a turn ends with,
    /// the error chip by the composer, "explain simply" on a reply, @-mentions in the box, a click on the side panel's
    /// picture, and the memory gauge by the cost. The pure parts are in apitest (VibeWire).</summary>
    static async Task TestVibe()
    {
        Log("\n## VB — change card, error chip, explain simply, @-mentions, click-to-ask, memory gauge");
        string root = Path.Combine(Path.GetTempPath(), "chattest-vibe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Views"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "Views", "Login.xaml"), "<Grid/>");
        File.WriteAllText(Path.Combine(root, "src", "Logger.cs"), "public class Logger\n{\n    public void LogLine(string s) { }\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "AuthService.cs"), "public class AuthService { public bool LoginAsync() => true; }");
        try
        {
            await FreshChat();
            Vm.OpenProjectQuietly(root);
            await Render();
            await VibeChangeCard(root);
            await VibeErrorChip(root);
            await VibeExplain();
            await VibeMentions(root);
            await VibeAskPicture(root);
            await VibeGauge();
            await VibeShots(root);
        }
        finally
        {
            BuildProblemsService.Current?.Clear();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static FrameworkElement ByAutomationId(DependencyObject under, string id) =>
        Find<FrameworkElement>(under).FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id);

    static string MakePng(string path, Color fill)
    {
        var bmp = new WriteableBitmap(160, 90, 96, 96, PixelFormats.Bgra32, null);
        int c = fill.A << 24 | fill.R << 16 | fill.G << 8 | fill.B;
        bmp.WritePixels(new Int32Rect(0, 0, 160, 90), Enumerable.Repeat(c, 160 * 90).ToArray(), 160 * 4, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    // ── VB1: the change card ──

    static async Task VibeChangeCard(string root)
    {
        var fire = typeof(ChatViewModel).GetMethod("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string before = MakePng(Path.Combine(root, "before.png"), Colors.SteelBlue);
        string after = MakePng(Path.Combine(root, "after.png"), Colors.Orange);
        void Edit(string rel, string tool, int add, int rem) => fire.Invoke(Vm, [new ToolResult
        {
            ToolName = tool, Type = ToolType.EditFile, Success = true, Output = "ok", FilePath = Path.Combine(root, rel),
            Arguments = new() { ["path"] = Path.Combine(root, rel) },
            Diff = new EditDiffResult
            {
                Added = add, Removed = rem,
                Lines = Enumerable.Range(0, add).Select(i => new EditDiffLine { Kind = EditDiffKind.Add, Text = "x" + i }).ToList(),
            },
        }]);
        void Shot(string path) => fire.Invoke(Vm, [new ToolResult
        {
            ToolName = "screenshot", Type = ToolType.McpTool, Success = true, Output = "[image]", Arguments = new(),
            Images = [new ToolImage("image/png", "", 160, 90, path)],
        }]);

        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor["[VB1]"] = new Queue<FakeStep>([FakeStep.Gated(["Changing the login screen"], gate, [" — done."])]);
        await Send("[VB1] make the login nicer");
        bool running = await WaitUntil(() => Vm.IsGenerating, 10000);
        Shot(before);
        Edit("Views/Login.xaml", "write_file", 30, 0);
        Edit("src/AuthService.cs", "edit_file", 6, 2);
        Edit("src/AuthService.cs", "edit_file", 1, 1);
        Shot(after);
        await Render(); await Task.Delay(60); await Render();
        gate.Release();
        bool idle = await WaitIdle();
        await Render();

        var card = Vm.Messages.LastOrDefault(m => m.IsTurnChanges);
        var view = card?.TurnChanges;
        Check("VB1 a turn that wrote files ends with ONE change card (its words in the row)",
            running && idle && Vm.Messages.Count(m => m.IsTurnChanges) == 1 && view != null && card!.Content.Contains("Login.xaml"),
            $"running={running} idle={idle} cards={Vm.Messages.Count(m => m.IsTurnChanges)} '{card?.Content}'");
        Check("VB1 it lists each file once with its +/− added up, the new one marked",
            view != null && view.Files.Count == 2 && view.Files[0].Name == "Login.xaml" && view.Files[0].IsNew && view.Files[0].AddedText == "+30"
            && view.Files[1].Name == "AuthService.cs" && !view.Files[1].IsNew && view.Files[1].AddedText == "+7" && view.Files[1].RemovedText == "−3"
            && view.AddedText == "+37" && view.RemovedText == "−3" && view.Files[0].Folder == "Views/",
            string.Join(" | ", view?.Files.Select(f => $"{f.Folder}{f.Name} {f.AddedText} {f.RemovedText} new={f.IsNew}") ?? []));
        Check("VB1 the plain line says what changed in people's words",
            view != null && view.Line == string.Format(L("changes.line.both"), 1, 1) + " — "
                + string.Format(L("changes.area.screen"), "Login.xaml") + L("changes.and") + string.Format(L("changes.area.logic"), "AuthService.cs"),
            view?.Line ?? "");
        Check("VB1 a screen change shows the picture before and after", view is { HasPictures: true } && view.BeforeImage == before && view.AfterImage == after,
            $"{view?.BeforeImage} → {view?.AfterImage}");

        ChatList.ScrollIntoView(card);
        await Render();
        var row = card == null ? null : Container(card);
        var cc = row == null ? null : ByAutomationId(row, "CxChat.TurnChanges");
        var texts = cc == null ? new List<string>() : Find<TextBlock>(cc).Select(TextOf).ToList();
        var images = cc == null ? new List<Image>() : Find<Image>(cc).Where(i => i.IsVisible).ToList();
        Check("VB1 on screen: the card is drawn (title, line, files, two pictures) and the bubble is not",
            cc is { IsVisible: true } && texts.Any(t => t.Contains(L("changes.title"))) && texts.Any(t => t.Contains("Login.xaml"))
            && images.Count == 2 && images.All(i => i.Source != null)
            && !Find<FrameworkElement>(row!).Any(e => e is Border b && b.Name == "Bubble" && b.IsVisible),
            $"cc={cc?.IsVisible} texts={texts.Count} images={images.Count}");
        bool drawn = await WaitUntil(() => images.Count == 2 && images.All(i => i.ActualHeight > 20), 3000);
        Check("VB1 the pictures are really drawn once read (not a flat line)", drawn,
            string.Join(", ", images.Select(i => $"{i.ActualWidth:0}x{i.ActualHeight:0}")));
        string opened = null;
        Action<string> onOpen = p => opened = p;
        Vm.OpenFileRequested += onOpen;
        try
        {
            var fileButton = cc == null ? null : Find<Button>(cc).FirstOrDefault(b => b.DataContext is TurnChangeRow r && r.Name == "AuthService.cs");
            if (fileButton != null) fileButton.Command.Execute(null);
            Check("VB1 a click on a file opens it in the code editor", opened != null && opened.EndsWith("AuthService.cs"), opened ?? "nothing opened");
        }
        finally { Vm.OpenFileRequested -= onOpen; }

        // an ordinary row pays nothing for it: collapsed and its template never applied
        var reply = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        var replyRow = reply == null ? null : Container(reply);
        var replyCc = replyRow == null ? null : ByAutomationId(replyRow, "CxChat.TurnChanges");
        Check("VB1 a reply row's card slot stays empty (no template, no elements)",
            replyCc is ContentControl { Visibility: Visibility.Collapsed } c0 && VisualTreeHelper.GetChildrenCount(c0) == 0,
            replyCc == null ? "slot not found" : $"{replyCc.Visibility} children={VisualTreeHelper.GetChildrenCount(replyCc)}");

        // a turn that wrote nothing has no card
        Fake.ScriptFor["[VB1b]"] = new Queue<FakeStep>([FakeStep.Reply("Just talking.")]);
        int cards = Vm.Messages.Count(m => m.IsTurnChanges);
        await Send("[VB1b] what is this?");
        await WaitIdle();
        Check("VB1 a turn that wrote nothing adds no card", Vm.Messages.Count(m => m.IsTurnChanges) == cards);
    }

    // ── VB2: the error chip ──

    static async Task VibeErrorChip(string root)
    {
        // the app makes it at startup (App.OnStartup); the harness's DI graph makes it on first ask — after the VM
        var problems = BuildProblemsService.Current ?? Sp.GetRequiredService<BuildProblemsService>();
        if (problems == null) { Check("VB2 (control) BuildProblemsService is running", false); return; }
        typeof(ChatViewModel).GetMethod("HookBuildProblems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        problems.Clear();
        await Task.Delay(400); await Render();
        var chip = ByAutomationId(View, "CxChat.ErrorChip");
        Check("VB2 no errors: no chip", chip != null && !chip.IsVisible && !Vm.HasErrorChip, $"chip={chip?.IsVisible}");

        string a = Path.Combine(root, "src", "AuthService.cs");
        string output = $"{a}(1,35): error CS0103: The name 'token' does not exist in the current context [{Path.Combine(root, "app.csproj")}]\n"
                      + $"{a}(1,10): warning CS0168: The variable 'e' is declared but never used [{Path.Combine(root, "app.csproj")}]\n"
                      + $"{Path.Combine(root, "src", "Logger.cs")}(1,20): error CS1002: ; expected [{Path.Combine(root, "app.csproj")}]\n"
                      + "Build FAILED.\n";
        problems.Ingest(output, root, "dotnet build", succeeded: false);
        bool shown = await WaitUntil(() => Vm.HasErrorChip, 3000);
        await Render();
        Check("VB2 a failed build's errors light the chip: '2 errors — fix' (the warning is not counted)",
            shown && chip!.IsVisible && Vm.ErrorChipText == LocF("vibe.errors.chip", 2), $"'{Vm.ErrorChipText}' visible={chip?.IsVisible}");

        Fake.ScriptFor["vibe.errors"] = new Queue<FakeStep>();   // keyed below by the prompt's own words
        Fake.Script.Enqueue(FakeStep.Reply("Fixed both."));
        int calls = Fake.Calls.Count;
        ((Button)chip!).Command.Execute(null);
        await WaitUntil(() => Fake.Calls.Count > calls, 10000);
        await WaitIdle();
        string sent = Fake.Calls.Count > calls ? Fake.Calls[calls].Input : "";
        Check("VB2 a click sends the fix request with each error's place and message (relative paths)",
            sent.StartsWith(LocF("vibe.errors.prompt", 2)) && sent.Contains("src/AuthService.cs:1:35 — CS0103: The name 'token' does not exist")
            && sent.Contains("src/Logger.cs:1:20 — CS1002: ; expected") && !sent.Contains("CS0168") && !sent.Contains(root),
            sent.Length > 300 ? sent[..300] : sent);
        Fake.ScriptFor.Remove("vibe.errors");

        problems.Ingest("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", root, "dotnet build", succeeded: true);
        bool gone = await WaitUntil(() => !Vm.HasErrorChip, 3000);
        await Render();
        Check("VB2 a clean build clears the chip", gone && !chip.IsVisible, $"'{Vm.ErrorChipText}'");
    }

    // ── VB3: explain simply ──

    static async Task VibeExplain()
    {
        var reply = new ChatMessage { Role = MessageRole.Assistant, Content = "I refactored the DI container and memoized the selector." };
        Vm.Messages.Add(reply);
        await Render();
        ChatList.ScrollIntoView(reply);
        await Render();
        reply.IsHovered = true;
        await Render();
        var row = Container(reply);
        var button = row == null ? null : ByAutomationId(row, "CxChat.ExplainSimply") as Button;
        Check("VB3 a finished reply shows 'Explain simply' on hover", button is { IsVisible: true } && (button.Content as string) == L("vibe.explain.button"),
            button == null ? "no button" : $"visible={button.IsVisible}");
        var streaming = new ChatMessage { Role = MessageRole.Assistant, Content = "half", IsStreaming = true };
        Check("VB3 not while the reply is still being written, nor on the owner's own message",
            !streaming.CanExplainSimply && !new ChatMessage { Role = MessageRole.User, Content = "hi" }.CanExplainSimply);

        Fake.Script.Enqueue(FakeStep.Reply("Plainly: the app is tidier inside."));
        int calls = Fake.Calls.Count;
        int users = Vm.Messages.Count(m => m.Role == MessageRole.User);
        if (button != null) { Click(button); Click(button); }   // a double click
        await WaitUntil(() => Fake.Calls.Count > calls, 10000);
        await WaitIdle();
        await Task.Delay(300);
        string sent = Fake.Calls.Count > calls ? Fake.Calls[calls].Input : "";
        string head = L("vibe.explain.prompt").Split("{0}")[0];
        Check("VB3 the click asks for it again in plain words, the reply quoted", sent.StartsWith(head) && sent.Contains("> I refactored the DI container"),
            sent.Length > 200 ? sent[..200] : sent);
        Check("VB3 a double click asks once", Fake.Calls.Count == calls + 1 && Vm.Messages.Count(m => m.Role == MessageRole.User) == users + 1,
            $"calls={Fake.Calls.Count - calls} users+={Vm.Messages.Count(m => m.Role == MessageRole.User) - users}");
        reply.IsHovered = false;
    }

    // ── VB4: @-mentions ──

    static async Task VibeMentions(string root)
    {
        var box = (TextBox)View.FindName("InputBox");
        var popup = (Popup)View.FindName("MentionPopup");
        var list = (ListBox)View.FindName("MentionList");
        int calls = Fake.Calls.Count;
        box.Focus();
        box.Text = "look at @Logg";
        box.CaretIndex = box.Text.Length;
        bool open = await WaitUntil(() => popup.IsOpen && list.Items.Count > 0, 8000);
        await Render();
        var items = list.Items.Cast<CluadeX.Services.Agents.MentionItem>().ToList();
        Check("VB4 '@Logg' opens the list with the project's Logger.cs first", open && items.Count > 0 && items[0].Insert == "@src/Logger.cs",
            $"open={popup.IsOpen} items={string.Join(" | ", items.Select(i => i.Insert))}");

        void Key(Key k)
        {
            var src = PresentationSource.FromVisual(box);
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        Key(System.Windows.Input.Key.Enter);
        await Render();
        Check("VB4 Enter takes the pick (the path goes in, nothing is sent)",
            box.Text == "look at @src/Logger.cs " && !popup.IsOpen && Fake.Calls.Count == calls && !Vm.IsGenerating, $"'{box.Text}' open={popup.IsOpen}");

        box.Text = "call @LogLi";
        box.CaretIndex = box.Text.Length;
        bool sym = await WaitUntil(() => popup.IsOpen && list.Items.Cast<CluadeX.Services.Agents.MentionItem>().Any(i => i.IsSymbol), 8000);
        var symItems = list.Items.Cast<CluadeX.Services.Agents.MentionItem>().ToList();
        Check("VB4 names declared in the project are listed too (LogLine in Logger.cs)",
            sym && symItems.Any(i => i.IsSymbol && i.Label == "LogLine" && i.Detail.StartsWith("src/Logger.cs:")),
            string.Join(" | ", symItems.Select(i => $"{i.Label}:{i.Detail}")));
        Key(System.Windows.Input.Key.Escape);
        await Render();
        Check("VB4 Esc closes the list and keeps the words", !popup.IsOpen && box.Text == "call @LogLi");

        box.Text = "mail me@site.com";
        box.CaretIndex = box.Text.Length;
        await Task.Delay(150); await Render();
        Check("VB4 an e-mail address opens nothing", !popup.IsOpen);
        box.Text = "";
    }

    // ── VB5: click the picture to ask about a spot ──

    static async Task VibeAskPicture(string root)
    {
        string shot = MakePng(Path.Combine(root, "shot.png"), Colors.White);
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.ToolAction, ToolKind = "screenshot", ToolName = "screenshot", ToolSuccess = true,
            Content = "screenshot", ThumbnailPath = shot });
        bool panel = await WaitUntil(() => Vm.LatestImagePath == shot, 3000);
        await Render();
        var pic = View.FindName("SidePicture") as Image;
        Check("VB5 the side panel shows the picture to click on", panel && pic is { IsVisible: true } && pic.Source != null, $"panel={panel} pic={pic?.IsVisible}");

        Vm.UserInput = "";
        int pending = Vm.PendingImages.Count;
        bool ok = Vm.AskAboutPictureSpot(shot, 0.25, 0.75);
        await Render();
        string attached = Vm.PendingImages.LastOrDefault();
        Check("VB5 a click circles the spot, attaches it and starts the question in the box",
            ok && Vm.PendingImages.Count == pending + 1 && File.Exists(attached) && Vm.UserInput == LocF("vibe.ask.prefill", 25, 75),
            $"ok={ok} pending={Vm.PendingImages.Count} '{Vm.UserInput}'");
        if (attached != null && File.Exists(attached))
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(attached); bmp.EndInit();
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var px = new byte[160 * 90 * 4];
            conv.CopyPixels(px, 160 * 4, 0);
            // the spot is (40, 67); the ring is ~14 px from it
            bool red = Enumerable.Range(48, 12).Any(x => { int i = (67 * 160 + x) * 4; return px[i + 2] > 180 && px[i + 1] < 120; });
            Check("VB5 the attached picture has the red ring at the clicked spot", red);
        }
        Vm.RemovePendingImageCommand.Execute(attached);
        Vm.UserInput = "";
    }

    // ── pictures of it all, dark and light, for the owner (vibe-<theme>.png next to the harness) ──

    static async Task VibeShots(string root)
    {
        var themes = Sp.GetRequiredService<CluadeX.Services.ThemeService>();
        string themeBefore = themes.CurrentThemeId;
        LocSvc.SetLanguage("th");
        try { await VibeShotsIn(root, themes); }
        finally
        {
            themes.Apply(themeBefore, persist: false);
            LocSvc.SetLanguage("en");   // the tests after this one read English text
        }
    }

    static async Task VibeShotsIn(string root, CluadeX.Services.ThemeService themes)
    {
        await FreshChat();
        string before = MakePng(Path.Combine(root, "shot-before.png"), Colors.SlateGray);
        string after = MakePng(Path.Combine(root, "shot-after.png"), Colors.MediumSeaGreen);
        var t = new CluadeX.Services.Agents.TurnChanges();
        t.NoteImage(before);
        t.Note(Path.Combine(root, "Views", "Login.xaml"), "Views/Login.xaml", 42, 0, true);
        t.Note(Path.Combine(root, "src", "AuthService.cs"), "src/AuthService.cs", 12, 5, false);
        t.Note(Path.Combine(root, "appsettings.json"), "appsettings.json", 2, 1, false);
        t.NoteImage(after);
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ทำหน้าล็อกอินให้สวยขึ้น แล้วเช็คว่ารหัสผิดแจ้งเตือนถูก" });
        var reply = new ChatMessage { Role = MessageRole.Assistant, Content = "เสร็จแล้วค่ะ ปรับหน้าล็อกอินใหม่ เพิ่มการแจ้งเตือนเมื่อรหัสผิด และย้ายค่าตั้งเวลาออกไปไว้ในไฟล์ตั้งค่า" };
        Vm.Messages.Add(reply);
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.TurnChangesTag, ToolOutput = t.ToJson(), Content = "📝 changes" });
        var problems = BuildProblemsService.Current;
        string a = Path.Combine(root, "src", "AuthService.cs");
        problems?.Ingest($"{a}(1,35): error CS0103: The name 'token' does not exist [{Path.Combine(root, "app.csproj")}]\n"
            + $"{a}(1,12): error CS1002: ; expected [{Path.Combine(root, "app.csproj")}]\nBuild FAILED.\n", root, "dotnet build", succeeded: false);
        await WaitUntil(() => Vm.HasErrorChip, 3000);
        await Task.Delay(700);
        Vm.ContextUsageText = "84,000 / 200,000";
        Vm.ContextUsagePercent = 42;
        reply.IsHovered = true;
        ChatList.ScrollIntoView(Vm.Messages[^1]);
        await Render();
        var cardRow = Container(Vm.Messages[^1]);
        bool shotsDrawn = await WaitUntil(() => cardRow != null && Find<Image>(cardRow).Count(i => i.IsVisible && i.ActualHeight > 20) == 2, 3000);
        if (!shotsDrawn)
            Log("     (pictures not drawn: " + (cardRow == null ? "no row" : string.Join(", ", Find<Image>(cardRow).Select(i =>
                $"{i.ActualWidth:0}x{i.ActualHeight:0} vis={i.IsVisible} src={i.Source?.GetType().Name}:{(i.Source == null ? 0 : i.Source.Width):0}"))) + $" exists={File.Exists(before)})");
        foreach (var id in new[] { CluadeX.Services.ThemeIds.Midnight, CluadeX.Services.ThemeIds.Lavender, CluadeX.Services.ThemeIds.Classic })
        {
            themes.Apply(id, persist: false);
            await Task.Delay(150); await Render();
            ChatList.ScrollIntoView(Vm.Messages[^1]);
            await Render();
            SaveShot(Path.Combine(AppContext.BaseDirectory, $"vibe-{id.ToLowerInvariant()}.png"));
        }
        reply.IsHovered = false;
        Log("     (pictures: vibe-midnight.png, vibe-lavender.png, vibe-classic.png in " + AppContext.BaseDirectory + ")");
    }

    // ── VB6: the memory gauge ──

    static async Task VibeGauge()
    {
        var gauge = ByAutomationId(View, "CxChat.ContextGauge");
        Field<System.Collections.IDictionary>(Vm, "_fillAtTurns").Clear();   // VB1's real turns set a pace of their own
        await Task.Delay(700);   // the rows' own recount (400 ms after they change) is done before the values below are set
        await Render();
        Vm.ContextUsageText = "40,000 / 200,000";
        Vm.ContextUsagePercent = 20;
        Vm.SessionTurnCount = 101;
        Vm.ContextUsagePercent = 30;
        Vm.SessionTurnCount = 102;
        await Render();
        Check("VB6 the composer's strip shows how full the memory is", gauge is { IsVisible: true } && Vm.ContextGaugeText == LocF("vibe.ctx.chip", 30),
            $"'{Vm.ContextGaugeText}' visible={gauge?.IsVisible}");
        Check("VB6 its tooltip says when a compaction comes (3 more turns at this pace)",
            Vm.ContextGaugeTip == LocF("vibe.ctx.tip", "40,000 / 200,000", 60, 3), Vm.ContextGaugeTip.Replace('\n', '|'));
        Vm.ContextUsagePercent = 72;
        Check("VB6 past the line it says to compact now", Vm.ContextGaugeTip == LocF("vibe.ctx.tipPast", "40,000 / 200,000", 60), Vm.ContextGaugeTip);
    }
}
