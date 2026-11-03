using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.PopOut;
using CluadeX.Views.UiStudio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace PagesTest;

/// <summary>
/// PO — a page in a window of its own (Views/PopOut), on the shipping MainWindow with the real browser:
///   · the pure screen fit: bounds on a screen stay, bounds on a monitor that is gone come back on screen;
///   · the UI Studio moves (not copied): the same view, the same WebView2 control and browser, the page still alive in
///     the new window — selection and the AI lock work there; the main window shows the placeholder; a second pop-out
///     only brings the window forward; Bring back returns the same view and browser; saved bounds that are off every
///     screen open on screen next time;
///   · the Node Graph: one view at a time (the main one goes before the window's loads, IsVisible stays right);
///   · the Code Editor: the same view, no docked chat in its window, files and an AI edit shown live there while the main
///     window stays on the chat; the chat docks again when it comes back;
///   · a theme switch while floating; the main window closing closes every page window.
/// Each window is drawn in a dark and a light theme to out\renders\ for a human look.
/// Runs last: it closes the main window.
/// </summary>
internal static partial class Program
{
    static string RendersDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "CluadeX", "Views")) && Directory.Exists(Path.Combine(d.FullName, "tests")))
                return Path.Combine(d.FullName, "out", "renders");
        return Path.Combine(Out, "renders");
    }

    static string Lc(string key) => LocalizedResources.Instance[key];

    static async Task<bool> WaitUntilAsync(Func<Task<bool>> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (await cond()) return true; } catch { }
            await Task.Delay(80);
        }
        try { return await cond(); } catch { return false; }
    }

    static string? TipOf(FrameworkElement e) => e.ToolTip as string;

    static CoreWebView2? CoreOf(object? web)
    {
        try { return web?.GetType().GetProperty("CoreWebView2")!.GetValue(web) as CoreWebView2; }
        catch { return null; }
    }

    static async Task PopOutChecks(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## PO — pages in a window of their own (Views/PopOut)");
        var main = (CluadeX.MainWindow)MainWin;
        var pops = main.PopOuts;
        var host = (PageHost)main.FindName("PageArea");
        var studio = Studio(vm);
        var themes = Sp.GetService<ThemeService>();
        string renders = RendersDir();
        Directory.CreateDirectory(renders);
        Log($"  renders: {renders}");

        // remembered bounds (the app keeps them in settings; here in memory)
        var saved = new Dictionary<string, PopOutPlacement>();
        PagePopOut.LoadPlacement = k => saved.TryGetValue(k, out var p) ? p : null;
        PagePopOut.SavePlacement = (k, p) => saved[k] = p;
        var placedAt = new Dictionary<string, Rect>();
        // a harness window stays off the owner's screen and never takes the focus (placed first: recorded for PO-fit)
        pops.Opening += w =>
        {
            placedAt[w.PageKey] = new Rect(w.Left, w.Top, w.Width, w.Height);
            w.ShowActivated = false;
            w.WindowState = WindowState.Normal;
            w.Left = -20000; w.Top = -20000; w.Width = 1280; w.Height = 820;
        };

        // ── PO0: the keys and the pure screen fit ──
        var keyType = typeof(LocalizationService).Assembly.GetType("CluadeX.Services.Localization.PopOutKeys");
        var keys = keyType?.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
        var bad = keys?.Where(k => !k.Value.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || !k.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th)).Select(k => k.Key).ToList();
        Check("PO0 every popout.* string has English and Thai, and the app finds it", keys is { Count: > 8 } && bad?.Count == 0 && Lc("popout.placeholder.back") == keys["popout.placeholder.back"]["th"],
            $"keys={keys?.Count} missing={string.Join(",", bad ?? new())}");

        var areas = new List<Rect> { new(0, 0, 1920, 1040), new(1920, 0, 2560, 1400) };
        var onSecond = new Rect(2000, 100, 1200, 800);
        Check("PO0 fit: bounds on a screen (the second monitor) stay as they were", ScreenFit.Fit(onSecond, areas, areas[0]) == onSecond);
        var gone = ScreenFit.Fit(new Rect(5200, 200, 1200, 800), areas, areas[0]);   // a third monitor, unplugged
        Check("PO0 fit: bounds on a monitor that is gone open on the main window's screen, whole",
            areas[0].Contains(gone) && gone.Width == 1200 && gone.Height == 800, gone.ToString());
        var mostlyOff = ScreenFit.Fit(new Rect(-1150, 300, 1200, 800), areas, areas[0]);   // only 50 px of the title bar on screen
        Check("PO0 fit: a title bar almost off the left edge is pulled back onto that screen", areas[0].Contains(mostlyOff), mostlyOff.ToString());
        var above = ScreenFit.Fit(new Rect(300, -600, 1000, 700), areas, areas[0]);   // its title bar above every screen
        Check("PO0 fit: a title bar above every screen comes down to the top", above.Top >= 0 && areas[0].Contains(above), above.ToString());
        var tooBig = ScreenFit.Fit(new Rect(9000, 9000, 5000, 4000), areas, areas[0]);
        Check("PO0 fit: a window bigger than the screen it lands on is made to fit", areas[0].Contains(tooBig), tooBig.ToString());
        Check("PO0 a damaged saved position is not used (NaN / zero size)",
            !new PopOutPlacement { Left = double.NaN, Top = 0, Width = 800, Height = 600 }.IsUsable && !new PopOutPlacement { Width = 0, Height = 600 }.IsUsable
            && new PopOutPlacement { Left = -5, Top = 3, Width = 800, Height = 600 }.IsUsable);

        // ── PO1–PO6: the UI Studio, with a real page ──
        string site = Path.Combine(fx, "popout-site");
        W(site, "index.html", Page("Float", "#3949AB", "<p id=\"p1\" style=\"color:#fff;font:20px sans-serif;margin:24px\">a page that moves between windows</p>"));
        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(200);
        vm.ShowPages = false;
        vm.OpenProject(site, autoStart: true);
        bool ready = await WaitUntil(() => vm.IsServing && studio.PageLoadState == PageLoadState.Loaded && GetField(view, "_engineReady") is true, 30000)
                     && await WaitUntilAsync(async () => studio.Evaluate != null && await studio.Evaluate("document.title") == "\"Float\"", 15000)
                     && await HasOverlay(studio);
        Check("PO1 (control) the Studio shows the fixture page with its overlay", ready, $"{studio.PageLoadState} url={vm.Url}");
        if (!ready) return;

        var web0 = GetField(view, "Web");
        var core0 = CoreOf(web0);
        var button = Find<PopOutButton>(view).FirstOrDefault();
        Check("PO1 the Studio's toolbar has the pop-out button (tooltip in Thai)",
            button != null && TipOf(button) == Lc("popout.button.tip") && button.Command == PagePopOut.PopOutCommand && button.Command.CanExecute(vm),
            $"button={button != null} tip='{(button == null ? "" : TipOf(button))}'");
        if (button == null) return;

        // pop out mid-load: the page is reloading as the button is pressed (and pressed twice)
        studio.ReloadPage(hard: false);
        PagePopOut.PopOutCommand.Execute(vm, button);
        var again = pops.PopOut(vm);
        PagePopOut.PopOutCommand.Execute(vm, MainWin);
        await Idle(600);
        var win = pops.WindowOf(vm);
        Check("PO1 double pop-out (and one mid-reload) opens ONE window — the second only brings it forward",
            win != null && pops.Windows.Count == 1 && ReferenceEquals(again, win), $"windows={pops.Windows.Count}");
        if (win == null) return;
        Check("PO1 the page MOVED: the same view is in the window, none is left in the main window",
            ReferenceEquals(win.View, view) && Find<UiStudioView>(win).SingleOrDefault() == view && !Find<UiStudioView>(MainWin).Any(),
            $"inWindow={Find<UiStudioView>(win).Count()} inMain={Find<UiStudioView>(MainWin).Count()}");
        Check("PO1 the main window shows the placeholder there (still on the Studio's page)",
            host.Placeholder is { IsVisible: true } && MainVm.SelectedNavItem == "UiStudio" && ReferenceEquals(host.Placeholder.DataContext, vm),
            $"placeholder={host.Placeholder?.IsVisible} nav={MainVm.SelectedNavItem}");
        Check("PO1 the window: localised title, the app icon, its own taskbar button, not owned by the main window",
            win.Title == Lc("popout.title.studio") && win.Icon != null && win.ShowInTaskbar && win.Owner == null && win.ResizeMode == ResizeMode.CanResize,
            $"title='{win.Title}' icon={win.Icon != null} owner={win.Owner != null}");
        Check("PO1 …and the toolbar button turned into 'put back'",
            PagePopOut.GetIsFloating(view) && button.Command == PagePopOut.BringBackCommand && TipOf(button) == Lc("popout.back.tip"), TipOf(button) ?? "");

        bool sameBrowser = ReferenceEquals(GetField(view, "Web"), web0) && ReferenceEquals(CoreOf(web0), core0);
        bool loadedAgain = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded, 20000) && await HasOverlay(studio);
        string? title = await studio.Evaluate!("document.title");
        Check("PO2 the WebView2 survived the move: the same control and browser, the page alive and verified in the new window",
            sameBrowser && loadedAgain && title == "\"Float\"" && ((FrameworkElement)view.FindName("Frame")).IsVisible,
            $"same={sameBrowser} {studio.PageLoadState} title={title}");
        studio.RunScript("__cx.selectBySelector('#p1')");
        bool picked = await WaitUntil(() => studio.Selection != null, 8000);
        Check("PO2 selection works in the window (a pick in the page reaches the Studio)", picked, studio.Selection?.Selector ?? "none");
        SetField(vm, "_lockedByAi", true);
        studio.ReloadPage(hard: false);
        bool relocked = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded, 20000) && await HasOverlay(studio)
                        && await WaitUntilAsync(async () => await studio.Evaluate!("__cx.isLocked()") == "true", 8000);
        Check("PO2 the AI edit lock works in the window (a page loaded there while a run edits is locked)", relocked);
        SetField(vm, "_lockedByAi", false);
        studio.RunScript("__cx.setLocked(false)");

        // the main window navigates on while the Studio floats
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(300);
        bool chatHere = Find<ChatView>(MainWin).Any(c => c.IsVisible) && host.Placeholder?.IsVisible != true;
        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(300);
        Check("PO3 the main window still navigates: Chat shows, back on the Studio's page it is the placeholder (one view still)",
            chatHere && host.Placeholder?.IsVisible == true && !Find<UiStudioView>(MainWin).Any() && Find<UiStudioView>(win).Count() == 1);
        // the Studio's own page prewarm must not build a second view while it is out
        Invoke(host, "Prewarm", vm);
        await Idle(100);
        Check("PO3 a prewarm while it floats builds no second Studio", !host.KeptViews.OfType<UiStudioView>().Any() && !Find<UiStudioView>(MainWin).Any());

        // a theme switch while it floats + the renders (dark, light)
        if (themes != null)
        {
            themes.Apply(ThemeIds.Midnight, persist: false);
            await Idle(500);
            await RenderWindow(win, Path.Combine(renders, "popout-studio-dark.png"));
            themes.Apply(ThemeIds.Classic, persist: false);
            await Idle(500);
            var light = (win.Background as SolidColorBrush)?.Color;
            Check("PO4 a theme switch while it floats reaches the window (Classic's light background)", light == (Color)ColorConverter.ConvertFromString("#ECECF0"), light?.ToString() ?? "");
            await RenderWindow(win, Path.Combine(renders, "popout-studio-light.png"));
            SavePngTo(host, Path.Combine(renders, "popout-placeholder-light-th.png"));
            themes.Apply(ThemeIds.Midnight, persist: false);
            await Idle(300);
            SavePngTo(host, Path.Combine(renders, "popout-placeholder-dark-th.png"));
        }

        // Bring back (the window's own button)
        PagePopOut.BringBackCommand.Execute(vm, button);
        await Idle(600);
        bool back = pops.WindowOf(vm) == null && !win.IsVisible && host.KeptViews.Contains(view) && Find<UiStudioView>(MainWin).SingleOrDefault() == view;
        Check("PO5 Bring back: the window closes and the SAME view is in the main window again (no placeholder)",
            back && host.Placeholder?.IsVisible != true && view.IsVisible && !PagePopOut.GetIsFloating(view) && button.Command == PagePopOut.PopOutCommand,
            $"window={pops.WindowOf(vm) != null} kept={host.KeptViews.Contains(view)} visible={view.IsVisible}");
        bool stillSame = ReferenceEquals(GetField(view, "Web"), web0) && ReferenceEquals(CoreOf(web0), core0);
        bool aliveBack = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded, 15000) && await studio.Evaluate!("document.title") == "\"Float\"";
        Check("PO5 …with the same browser, the page alive (it was never torn down)", stillSame && aliveBack, $"same={stillSame} {studio.PageLoadState}");
        Check("PO5 its size and position were remembered for the Studio", saved.TryGetValue("UiStudio", out var place) && place.IsUsable, saved.Count.ToString());

        // the remembered place was off every screen (the harness moved it to -20000): the next window opens on screen
        PagePopOut.PopOutCommand.Execute(vm, button);
        await Idle(500);
        var win2 = pops.WindowOf(vm);
        var monitors = ScreenFit.WorkAreas(VisualTreeHelper.GetDpi(MainWin).DpiScaleX, VisualTreeHelper.GetDpi(MainWin).DpiScaleY);
        bool onScreen = placedAt.TryGetValue("UiStudio", out var at) && monitors.Any(m => !Rect.Intersect(new Rect(at.Left, at.Top, at.Width, 24), m).IsEmpty);
        Check("PO6 saved bounds off every screen (-20000) open on a real screen next time", win2 != null && onScreen,
            $"saved=({saved.GetValueOrDefault("UiStudio")?.Left},{saved.GetValueOrDefault("UiStudio")?.Top}) placed={at} monitors={string.Join(" ", monitors)}");
        // close during load: the window's close box while the page reloads
        studio.ReloadPage(hard: false);
        win2?.Close();
        await Idle(500);
        bool closedMidLoad = pops.WindowOf(vm) == null && Find<UiStudioView>(MainWin).SingleOrDefault() == view
                             && await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded, 20000) && await HasOverlay(studio);
        Check("PO6 the close box mid-load also brings it back, and the page finishes loading here", closedMidLoad, studio.PageLoadState.ToString());

        // ── PO7: the Node Graph ──
        var graph = MainVm.NodeGraphVM!;
        // (windows off screen are "covered" to Chromium, which then stops drawing a page; the GPU graph's browser starts here)
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=CalculateNativeWinOcclusion");
        Invoke(MainVm, "NavigateTo", "NodeGraph");
        var gview = await WaitFor(() => Find<NodeGraphView>(MainWin).FirstOrDefault(v => v.IsVisible));
        await Idle(300);
        var gbutton = gview == null ? null : Find<PopOutButton>(gview).FirstOrDefault();
        Check("PO7 the Node Graph's toolbar has the pop-out button (added to its right-hand buttons)", gbutton != null && graph.IsVisible);
        if (gbutton != null)
        {
            PagePopOut.PopOutCommand.Execute(graph, gbutton);
            await Idle(800);
            var gwin = pops.WindowOf(graph);
            var inWindow = gwin == null ? new List<NodeGraphView>() : Find<NodeGraphView>(gwin).ToList();
            Check("PO7 the graph moved: one view, in the window; none in the main window; the view model says it is on screen",
                gwin != null && inWindow.Count == 1 && !Find<NodeGraphView>(MainWin).Any() && graph.IsVisible && ReferenceEquals(inWindow[0].DataContext, graph),
                $"window={inWindow.Count} main={Find<NodeGraphView>(MainWin).Count()} visible={graph.IsVisible}");
            Check("PO7 the window's graph has the 'put back' button, the title is localised",
                gwin != null && Find<PopOutButton>(gwin).Count() == 1 && Find<PopOutButton>(gwin).First().Command == PagePopOut.BringBackCommand
                && gwin.Title == Lc("popout.title.graph"));
            // the floating graph draws on the GPU too (the WebGL2 page in its own window; a shot of the page itself,
            // since a WPF render of the window cannot see into a WebView2)
            var fgpu = gwin == null ? null : Find<NodeGraphGpu>(gwin).FirstOrDefault();
            for (int i = 0; i < 150 && fgpu != null && !fgpu.IsReady && !NodeGraphGpu.Disabled; i++) await Idle(100);
            Check($"PO7 the floating graph draws on the GPU ({fgpu?.Renderer ?? NodeGraphGpu.DisabledWhy ?? "no GPU view"})",
                fgpu is { IsReady: true }, $"graph {graph.Graph?.Nodes.Count} nodes");
            if (fgpu is { IsReady: true } && graph.Graph != null
                && typeof(NodeGraphGpu).GetField("_web", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(fgpu) is Microsoft.Web.WebView2.Wpf.WebView2 gweb)
            {
                await Idle(600);
                string nodes = await gweb.CoreWebView2.ExecuteScriptAsync("window.__graphStats.nodes");
                Check("PO7 …with the project's graph on it", nodes == graph.Graph.Nodes.Count(n => !n.Hidden).ToString() || int.TryParse(nodes, out int nn) && nn > 0, $"{nodes} drawn of {graph.Graph.Nodes.Count}");
                using (var shot = File.Create(Path.Combine(renders, "popout-graph-gpu.png")))
                    await gweb.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, shot);
            }
            if (gwin != null && themes != null)
            {
                await RenderWindow(gwin, Path.Combine(renders, "popout-graph-dark.png"));
                themes.Apply(ThemeIds.Classic, persist: false);
                await Idle(500);
                await RenderWindow(gwin, Path.Combine(renders, "popout-graph-light.png"));
                themes.Apply(ThemeIds.Midnight, persist: false);
                await Idle(300);
            }
            Invoke(MainVm, "NavigateTo", "Chat");
            await Idle(300);
            Check("PO7 the main window going to the chat leaves the floating graph on screen", graph.IsVisible && gwin?.IsVisible == true);
            gwin?.Close();
            await Idle(500);
            Check("PO7 closed while the main window is on the chat: no graph view anywhere, not on screen", !graph.IsVisible && pops.WindowOf(graph) == null);
            Invoke(MainVm, "NavigateTo", "NodeGraph");
            await Idle(600);
            Check("PO7 back on the graph's page: one view in the main window again, on screen",
                Find<NodeGraphView>(MainWin).Count(v => v.IsVisible) == 1 && graph.IsVisible && host.Placeholder?.IsVisible != true);
            var mgpu = Find<NodeGraphGpu>(MainWin).FirstOrDefault();
            for (int i = 0; i < 100 && mgpu != null && !mgpu.IsReady && !NodeGraphGpu.Disabled; i++) await Idle(100);
            Check("PO7 …and it draws on the GPU again, without the closed window's browser", mgpu is { IsReady: true } && fgpu != null && (ReferenceEquals(mgpu, fgpu) || !fgpu.IsReady),
                $"main gpu {mgpu?.IsReady} same={ReferenceEquals(mgpu, fgpu)} floating ready={fgpu?.IsReady}");
            // (PageHost: a rebuilt page left for a kept one — the chat — really goes; it used to stay loaded, hidden)
            Invoke(MainVm, "NavigateTo", "Chat");
            await Idle(400);
            Check("PO7 leaving the graph for the chat unloads its view (no hidden graph left loaded and 'on screen')",
                !Find<NodeGraphView>(MainWin).Any() && !graph.IsVisible, $"views={Find<NodeGraphView>(MainWin).Count()} visible={graph.IsVisible}");
        }

        // ── PO8: the Code Editor without its chat ──
        string proj = Path.Combine(fx, "popout-code");
        string file = Path.Combine(proj, "hello.cs");
        W(proj, "hello.cs", "class Hello { }\n");
        var fs = Sp.GetRequiredService<FileSystemService>();
        string oldWd = fs.WorkingDirectory;
        fs.WorkingDirectory = proj;
        Invoke(MainVm, "NavigateTo", "Code");
        var ed = await WaitFor(() => Find<CodeEditorView>(MainWin).FirstOrDefault(v => v.IsVisible));
        await Idle(300);
        Check("PO8 (control) the editor in the main window docks the chat", ed != null && ed.ShowsDockedChat && Find<ChatView>(ed).Any());
        if (ed != null)
        {
            var edVm = MainVm.CodeEditorVM;
            await edVm.OpenPathAsync(file);
            await Idle(300);
            var te = (ICSharpCode.AvalonEdit.TextEditor)ed.FindName("Editor");
            var ebutton = (PopOutButton)ed.FindName("PopOutButton");
            PagePopOut.PopOutCommand.Execute(edVm, ebutton);
            await Idle(800);
            var ewin = pops.WindowOf(edVm);
            var panel = (FrameworkElement)ed.FindName("EditorChatPanel");
            Check("PO8 the editor moved: the same view in its window, with NO chat panel (no ChatView at all in that window)",
                ewin != null && ReferenceEquals(ewin.View, ed) && !ed.ShowsDockedChat && !Find<ChatView>(ewin).Any() && panel.Visibility == Visibility.Collapsed
                && ((FrameworkElement)ed.FindName("ChatGrip")).Visibility == Visibility.Collapsed,
                $"window={ewin != null} chatViews={(ewin == null ? -1 : Find<ChatView>(ewin).Count())} panel={panel.Visibility}");
            Check("PO8 …the open file kept its text in the move", te.Text.Contains("class Hello"), te.Text);
            Invoke(MainVm, "NavigateTo", "Chat");
            await Idle(300);
            Check("PO8 the main window's chat works while the editor floats (the Chat page is on screen there)",
                Find<ChatView>(MainWin).Any(c => c.IsVisible) && MainVm.SelectedNavItem == "Chat");
            // an AI edit: the file changes on disk, the chat reports it and follows the code — the window shows it live
            File.WriteAllText(file, "class Hello { int x = 1; }\n");
            RaiseEvent(MainVm.ChatVM, "FileMutatedByAgent", "hello.cs", 1, null);
            Invoke(MainVm, "FollowEditToCode");
            bool live = await WaitUntil(() => { edVm.FinishAgentTyping(); return te.Text.Contains("int x = 1"); }, 10000);
            Check("PO8 an AI edit shows live in the floating editor, and the main window stays on the chat (follow mode does not take it)",
                live && MainVm.SelectedNavItem == "Chat" && edVm.EditorOnScreen, $"text='{te.Text.Trim()}' nav={MainVm.SelectedNavItem} onScreen={edVm.EditorOnScreen}");
            if (ewin != null && themes != null)
            {
                await RenderWindow(ewin, Path.Combine(renders, "popout-editor-dark.png"));
                themes.Apply(ThemeIds.Classic, persist: false);
                await Idle(500);
                await RenderWindow(ewin, Path.Combine(renders, "popout-editor-light.png"));
                themes.Apply(ThemeIds.Midnight, persist: false);
                await Idle(300);
            }
            // a file double-clicked in the chat while the editor floats opens there, the main window keeps the chat
            RaiseEvent(MainVm.ChatVM, "OpenFileRequested", file);
            await Idle(300);
            Check("PO8 a file opened from the chat goes to the floating editor (the main window stays on the chat)", MainVm.SelectedNavItem == "Chat" && pops.WindowOf(edVm) != null);

            PagePopOut.BringBackCommand.Execute(edVm, ebutton);
            await Idle(600);
            Invoke(MainVm, "NavigateTo", "Code");
            await Idle(500);
            var chatHost = (ContentControl)ed.FindName("EditorChatHost");
            Check("PO8 back in the main window: the same editor, the chat docked again, the AI's edit still there",
                pops.WindowOf(edVm) == null && Find<CodeEditorView>(MainWin).SingleOrDefault() == ed && ed.ShowsDockedChat && panel.Visibility == Visibility.Visible
                && ReferenceEquals(chatHost.Content, MainVm.ChatVM) && te.Text.Contains("int x = 1"),
                $"docked={ed.ShowsDockedChat} content={chatHost.Content?.GetType().Name} text='{te.Text.Trim()}'");
        }
        fs.WorkingDirectory = oldWd;

        // ── PO9: the main window closes → every page window closes, its page handed back ──
        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(300);
        var w1 = pops.PopOut(vm);
        var w2 = pops.PopOut(graph);
        await Idle(600);
        Check("PO9 (control) two pages float at once", w1 != null && w2 != null && pops.Windows.Count == 2);
        MainWin.Close();
        await Idle(800);
        Check("PO9 the main window closing closes every page window (nothing keeps the app running)",
            pops.Windows.Count == 0 && w1?.IsVisible != true && w2?.IsVisible != true && host.KeptViews.Contains(view),
            $"open={pops.Windows.Count}");
        Check("PO9 …and no page can pop out of a closed main window", pops.PopOut(vm) == null);
        PagePopOut.LoadPlacement = null;
        PagePopOut.SavePlacement = null;
    }

    /// <summary>A window as it looks: its WPF content, with each WebView2's real page (a native window, which a WPF
    /// bitmap cannot see) drawn in its place.</summary>
    static async Task RenderWindow(Window w, string path)
    {
        try
        {
            if (w.Content is not FrameworkElement root) return;
            root.UpdateLayout();
            int wpx = (int)Math.Ceiling(root.ActualWidth), hpx = (int)Math.Ceiling(root.ActualHeight);
            if (wpx <= 0 || hpx <= 0) { Log($"  (no picture: {Path.GetFileName(path)} has no size)"); return; }
            var pages = new List<(Rect At, BitmapSource Shot)>();
            foreach (var web in Find<Microsoft.Web.WebView2.Wpf.WebView2>(root).Where(x => x.IsVisible && x.ActualWidth > 0))
            {
                var core = CoreOf(web);
                if (core == null) continue;
                using var ms = new MemoryStream();
                try { await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms); } catch { continue; }
                ms.Position = 0;
                var shot = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var at = web.TranslatePoint(new Point(0, 0), root);
                pages.Add((new Rect(at, new Size(web.ActualWidth, web.ActualHeight)), shot));
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background ?? Brushes.Black, null, new Rect(0, 0, wpx, hpx));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, wpx, hpx));
                foreach (var (at, shot) in pages) dc.DrawImage(shot, at);
            }
            var bmp = new RenderTargetBitmap(wpx, hpx, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
            Log($"  picture: {path} ({wpx}×{hpx}, {pages.Count} browser page(s))");
        }
        catch (Exception ex) { Log($"  (picture {Path.GetFileName(path)} failed: {ex.Message})"); }
    }

    static void SavePngTo(FrameworkElement el, string path)
    {
        try
        {
            el.UpdateLayout();
            int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Application.Current.TryFindResource("Bg.Window") as Brush ?? Brushes.Black, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            Log($"  picture: {path} ({w}×{h})");
        }
        catch (Exception ex) { Log($"  (picture {Path.GetFileName(path)} failed: {ex.Message})"); }
    }
}
