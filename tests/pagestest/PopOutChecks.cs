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

            await MediaTabChecks(ed, edVm, te, ebutton, proj, pops);
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

    /// <summary>
    /// MT: a picture opens as a picture (owner 2026-11-03: "เปิดไฟล์งานที่เป็นรูปภาพ ให้แสดงภาพ แก้ไขได้"). The media page
    /// takes AvalonEdit's place for an image tab and gives it back for a text tab; it survives the editor's pop-out; an
    /// edit saved with Ctrl+S lands in a NEW file beside the original (opened in its own tab); overwriting asks first
    /// and Cancel leaves the original alone; closing the tab closes its page.
    /// </summary>
    static async Task MediaTabChecks(CodeEditorView ed, CodeEditorViewModel edVm, ICSharpCode.AvalonEdit.TextEditor te,
        PopOutButton ebutton, string proj, CluadeX.Views.PopOut.PagePopOutManager pops)
    {
        string img = Path.Combine(proj, "รูป ทดสอบ.png");
        WritePng(img, 64, 48);
        byte[] original = File.ReadAllBytes(img);
        var textTab = edVm.ActiveTab;
        var findBar = (FrameworkElement)ed.FindName("FindBar");
        string[] textTools = { "TbUndo", "TbRedo", "TbCut", "TbCopy", "TbPaste", "TbFind", "TbReplace", "TbGoTo", "TbComment", "TbTools",
                               "TbWrap", "TbWhitespace", "TbZoomOut", "TbZoomIn", "TbAiComments", "TbAutoComment" };
        string[] fileTools = { "TbNew", "TbOpen", "TbSave", "TbSaveAs", "TbSaveAll" };
        string ToolsShown() => string.Join(" ", textTools.Concat(fileTools).Where(n => ((FrameworkElement)ed.FindName(n)).Visibility == Visibility.Visible));
        bool TextToolsHidden() => textTools.All(n => ((FrameworkElement)ed.FindName(n)).Visibility != Visibility.Visible);
        bool TextToolsShown() => textTools.All(n => ((FrameworkElement)ed.FindName(n)).Visibility == Visibility.Visible);
        bool FileToolsShown() => fileTools.All(n => ((FrameworkElement)ed.FindName(n)).Visibility == Visibility.Visible);
        ed.OpenFindBar(replace: false);   // open on the text tab: it must step aside for the picture and come back after
        await Idle(200);
        bool findWasOpen = findBar.Visibility == Visibility.Visible;

        await edVm.OpenPathAsync(img);
        var host = await WaitFor(() => ed.MediaHosts.FirstOrDefault(), 10000);
        bool loaded = host != null && await WaitUntil(() => host.PageState?.StartsWith("loaded") == true || host.ShowsMessage, 45000);
        await Idle(300);
        Check("MT1 an image (Thai name, space) opens as a media tab: the media page shows it at its size, AvalonEdit steps aside",
            edVm.ActiveTab is MediaFileTab { Kind: CluadeX.Services.Media.MediaKind.Image } && loaded && host!.PageState!.Contains("\"w\":64") && host.PageState.Contains("\"h\":48")
            && ed.ShowsMedia && te.Visibility != Visibility.Visible,
            $"tab={edVm.ActiveTab?.GetType().Name} state={host?.PageState} message={host?.ShowsMessage} media={ed.ShowsMedia} editor={te.Visibility}");
        Check("MT9 a media tab hides the text tools (undo … find … comment … wrap … zoom … AI comments) and the find bar; Save / Save As / New / Open stay",
            TextToolsHidden() && FileToolsShown() && findWasOpen && findBar.Visibility != Visibility.Visible,
            $"shown: {ToolsShown()} | find was {findWasOpen}, now {findBar.Visibility}");
        if (host == null || !loaded || host.ShowsMessage) return;
        var mediaTab = (MediaFileTab)edVm.ActiveTab!;

        edVm.ActiveTab = textTab;
        await Idle(300);
        Check("MT2 a text tab still shows AvalonEdit (the media page hides)", !ed.ShowsMedia && te.Visibility == Visibility.Visible && te.Text.Contains("class Hello"),
            $"media={ed.ShowsMedia} editor={te.Visibility}");
        Check("MT9b …and the next text tab brings every text tool back, the find bar too",
            TextToolsShown() && FileToolsShown() && findBar.Visibility == Visibility.Visible, $"shown: {ToolsShown()} | find {findBar.Visibility}");
        ed.CloseFindBar();
        edVm.ActiveTab = mediaTab;
        await Idle(300);

        // the editor floats with the picture open: the same page moves with it and still answers
        PagePopOut.PopOutCommand.Execute(edVm, ebutton);
        await Idle(1200);
        var web = (Microsoft.Web.WebView2.Wpf.WebView2)GetField(host, "_web")!;
        async Task<string> Js(string script) => await web.ExecuteScriptAsync(script);
        string alive = pops.WindowOf(edVm) != null ? await Js("1+1") : "";
        Check("MT3 popped out with the picture open: the same media page, still alive and showing the file",
            pops.WindowOf(edVm) != null && ed.MediaHosts.SingleOrDefault() == host && alive == "2" && ed.ShowsMedia,
            $"window={pops.WindowOf(edVm) != null} hosts={ed.MediaHosts.Count} js={alive}");
        PagePopOut.BringBackCommand.Execute(edVm, ebutton);
        await Idle(600);
        Invoke(MainVm, "NavigateTo", "Code");
        await Idle(600);

        // an edit (rotate right), then Ctrl+S → a copy beside the original, the original untouched, the copy opens
        await Js("document.querySelector('[data-tip=\"media.page.rotateRight\"]').click()");
        bool dirty = await WaitUntil(() => mediaTab.IsDirty, 5000);
        Task<bool> save = mediaTab.Session!.SaveCopyAsync();
        string copy = Path.Combine(proj, "รูป ทดสอบ-edit.png");
        bool saved = await save.WaitAsync(TimeSpan.FromSeconds(30));
        bool opened = await WaitUntil(() => edVm.Tabs.Any(t => string.Equals(t.FullPath, copy, StringComparison.OrdinalIgnoreCase)), 10000);
        var size = File.Exists(copy) ? PngSize(copy) : (0, 0);
        Check("MT4 rotate + Ctrl+S: the edit goes to 'รูป ทดสอบ-edit.png' (rotated 48×64), the original is untouched, the copy opens, no temp file left",
            dirty && saved && size == (48, 64) && File.ReadAllBytes(img).SequenceEqual(original) && opened && !mediaTab.IsDirty
            && Directory.GetFiles(proj, "*.cxpart.*").Length == 0,
            $"dirty={dirty} saved={saved} size={size} opened={opened} stillDirty={mediaTab.IsDirty}");

        // overwrite asks first; Cancel keeps the original byte for byte
        edVm.ActiveTab = mediaTab;
        await Idle(400);
        await Js("document.querySelector('[data-tip=\"media.page.flipH\"]').click()");
        await WaitUntil(() => mediaTab.IsDirty, 5000);
        int asked = 0;
        var prior = CluadeX.Views.Editor.ChoiceDialog.Override;
        CluadeX.Views.Editor.ChoiceDialog.Override = (_, _, _) => { asked++; return 1; };
        try
        {
            await Js("document.querySelector('[data-tip=\"media.page.overwriteTip\"]').click()");
            await WaitUntil(() => asked > 0 && !mediaTab.IsBusy, 15000);
            await Idle(500);
        }
        finally { CluadeX.Views.Editor.ChoiceDialog.Override = prior; }
        Check("MT5 'overwrite' asks first, and Cancel leaves the original as it was (the edit stays in the tab)",
            asked == 1 && File.ReadAllBytes(img).SequenceEqual(original) && mediaTab.IsDirty && !mediaTab.IsBusy,
            $"asked={asked} dirty={mediaTab.IsDirty} busy={mediaTab.IsBusy}");

        // …and Yes replaces it in place while the page is showing it (the page lets go of the file first), then shows the new file
        asked = 0;
        string before = host.PageState ?? "";
        CluadeX.Views.Editor.ChoiceDialog.Override = (_, _, _) => { asked++; return 0; };
        try
        {
            await Js("document.querySelector('[data-tip=\"media.page.overwriteTip\"]').click()");
            await WaitUntil(() => asked > 0 && !mediaTab.IsBusy && !mediaTab.IsDirty, 20000);
            await Idle(800);
        }
        finally { CluadeX.Views.Editor.ChoiceDialog.Override = prior; }
        byte[] now = File.ReadAllBytes(img);
        Check("MT5b 'overwrite' + Yes: the original is replaced by every edit so far (rotated + flipped, 48×64), the page shows the new file, the tab is clean, nothing left over",
            asked == 1 && !now.SequenceEqual(original) && PngSize(img) == (48, 64) && !mediaTab.IsDirty && !mediaTab.IsBusy
            && Directory.GetFiles(proj, "*.cxpart.*").Length == 0 && host.PageState != before && host.PageState?.Contains("\"w\":48") == true,
            $"asked={asked} changed={!now.SequenceEqual(original)} dirty={mediaTab.IsDirty} state={host.PageState}");

        await TextOnPictureCheck(ed, edVm, proj);

        // audio: a WAV plays and is decoded for editing (waveform), not play-only
        string wav = Path.Combine(proj, "เสียง ทดสอบ.wav");
        WriteWav(wav, seconds: 2);
        await edVm.OpenPathAsync(wav);
        var audioTab = edVm.ActiveTab as MediaFileTab;
        var audioHost = await WaitFor(() => audioTab == null ? null : ed.MediaHosts.FirstOrDefault(h => h.Tab == audioTab), 10000);
        bool audioLoaded = audioHost != null && await WaitUntil(() => audioHost.PageState?.StartsWith("loaded") == true || audioHost.PageState?.StartsWith("error") == true, 30000);
        Check("MT7 a WAV opens as audio: decoded for editing (channels, rate, ~2 s), not play-only",
            audioTab is { Kind: CluadeX.Services.Media.MediaKind.Audio } && audioLoaded && audioHost!.PageState!.Contains("\"channels\":1")
            && !audioHost.PageState.Contains("playOnly") && System.Text.RegularExpressions.Regex.IsMatch(audioHost.PageState, "\"duration\":(1\\.9|2)"),
            $"state={audioHost?.PageState}");

        // video (when this machine has ffmpeg to make one): it loads with its size
        string? ff = CluadeX.Services.Media.Ffmpeg.Locate();
        if (ff != null)
        {
            string clip = Path.Combine(proj, "คลิป ทดสอบ.webm");
            var psi = new System.Diagnostics.ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25", "-c:v", "libvpx-vp9", "-b:v", "200k", "-y", clip })
                psi.ArgumentList.Add(a);
            using (var p = System.Diagnostics.Process.Start(psi)!) { await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); }
            await edVm.OpenPathAsync(clip);
            var videoTab = edVm.ActiveTab as MediaFileTab;
            var videoHost = await WaitFor(() => videoTab == null ? null : ed.MediaHosts.FirstOrDefault(h => h.Tab == videoTab), 10000);
            bool videoLoaded = videoHost != null && await WaitUntil(() => videoHost.PageState?.StartsWith("loaded") == true || videoHost.PageState?.StartsWith("error") == true, 30000);
            Check("MT8 a WebM clip opens as video at its size (320×240, ~3 s)",
                videoTab is { Kind: CluadeX.Services.Media.MediaKind.Video } && videoLoaded && videoHost!.PageState!.Contains("\"w\":320") && videoHost.PageState.Contains("\"h\":240"),
                $"state={videoHost?.PageState}");
            if (videoLoaded && videoHost != null) await TextOnVideoCheck(videoHost, videoTab!, clip, ff);
        }
        else Log("SKIP  MT8 no ffmpeg on this machine to make a test clip");

        // close the media tabs (the edited one is dirty: the close question says "don't save")
        CluadeX.Views.Editor.ChoiceDialog.Override = (_, _, buttons) => buttons.Count > 1 ? 1 : 0;
        try
        {
            foreach (var t in edVm.Tabs.OfType<MediaFileTab>().ToList()) edVm.CloseTabCommand.Execute(t);
            await WaitUntil(() => !edVm.Tabs.OfType<MediaFileTab>().Any(), 5000);
        }
        finally { CluadeX.Views.Editor.ChoiceDialog.Override = prior; }
        await Idle(300);
        Check("MT6 closing the media tabs closes their pages; the text tab is back in AvalonEdit",
            !edVm.Tabs.OfType<MediaFileTab>().Any() && ed.MediaHosts.Count == 0 && !ed.ShowsMedia && host.Session_IsClosed(),
            $"tabs={edVm.Tabs.OfType<MediaFileTab>().Count()} hosts={ed.MediaHosts.Count} media={ed.ShowsMedia}");
    }

    static bool Session_IsClosed(this CluadeX.Views.Editor.MediaEditorHost host) => GetField(host, "_web") == null;

    /// <summary>The page's answer to a script that returns JSON.stringify(...): the JSON inside.</summary>
    static System.Text.Json.JsonElement JsJson(string raw)
    {
        try
        {
            string inner = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "null";
            return System.Text.Json.JsonDocument.Parse(inner).RootElement.Clone();
        }
        catch { return System.Text.Json.JsonDocument.Parse("null").RootElement.Clone(); }
    }

    /// <summary>
    /// MT10: text on a picture (owner 2026-11-04: "อยากให้ใส่ข้อความลงในไฟล์ภาพ … เลือกฟ้อน สี"). The text tool opens, a
    /// click on the picture adds a layer, Thai with tone marks + an emoji is typed, Tahoma / green / a blue outline are
    /// picked in the panel; Save copy writes a new file whose pixels changed where the text is and nowhere else, the
    /// original is untouched and the layer stays editable.
    /// </summary>
    static async Task TextOnPictureCheck(CodeEditorView ed, CodeEditorViewModel edVm, string proj)
    {
        string pic = Path.Combine(proj, "ข้อความ ทดสอบ.png");
        WritePng(pic, 240, 120);
        byte[] before = File.ReadAllBytes(pic);
        await edVm.OpenPathAsync(pic);
        var tab = edVm.ActiveTab as MediaFileTab;
        var host = await WaitFor(() => tab == null ? null : ed.MediaHosts.FirstOrDefault(h => h.Tab == tab), 10000);
        bool loaded = host != null && await WaitUntil(() => host.PageState?.StartsWith("loaded") == true, 30000);
        await Idle(500);
        if (!loaded || host == null || tab == null) { Check("MT10 a picture for the text test opens", false, host?.PageState ?? "no host"); return; }
        var web = (Microsoft.Web.WebView2.Wpf.WebView2)GetField(host, "_web")!;
        await WaitUntilAsync(async () => await web.ExecuteScriptAsync("FONTS.length") is string n && int.TryParse(n, out int c) && c > 0, 10000);
        var set = JsJson(await web.ExecuteScriptAsync(@"(() => { try {
          document.querySelector('[data-tip=""media.page.textTip""]').click();
          const c = document.querySelector('#imgwrap canvas'), r = c.getBoundingClientRect();
          c.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2, button: 0, pointerId: 1 }));
          const p = document.getElementById('tpanel'); if (!p) return JSON.stringify({ error: 'no panel' });
          const fire = (e, v, ev = 'input') => { e.value = v; e.dispatchEvent(new Event(ev)); };
          fire(p.querySelector('textarea'), 'ที่นี่ ผู้ใหญ่ น้ำ 👍');
          const font = p.querySelector('.fontpick input'); font.focus(); fire(font, 'tahoma'); font.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
          const nums = p.querySelectorAll('input[type=number]'), cols = p.querySelectorAll('input[type=color]');
          fire(nums[0], '30'); fire(nums[2], '3'); fire(cols[0], '#00ff00'); fire(cols[1], '#0000ff');
          const Ly = ED.text.layers[0];
          return JSON.stringify({ n: ED.text.layers.length, font: Ly.font, color: Ly.color, stroke: Ly.stroke, size: Ly.size, text: Ly.text, b: layerBounds(Ly),
            m: Ly.shadow.blur * 2 + Math.abs(Ly.shadow.dy) + Ly.stroke.width + 3, sel: document.querySelector('[data-tip=""media.page.saveCopyTip""]').disabled });
        } catch (e) { return JSON.stringify({ error: String(e && e.stack || e) }); } })()"));
        await Idle(300);
        if (set.ValueKind != System.Text.Json.JsonValueKind.Object || set.TryGetProperty("error", out _))
        { Check("MT10 text on a picture: the text tool adds and styles a layer", false, set.ToString()); return; }
        bool dirty = await WaitUntil(() => tab.IsDirty, 5000);
        bool saved = await tab.Session!.SaveCopyAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string copy = Path.Combine(proj, "ข้อความ ทดสอบ-edit.png");
        await WaitUntil(() => File.Exists(copy), 5000);
        string still = await web.ExecuteScriptAsync("ED.text.layers.length");

        int inside = 0, outside = 0, green = 0;
        string detail = set.ToString();
        if (File.Exists(copy) && set.ValueKind == System.Text.Json.JsonValueKind.Object && set.TryGetProperty("b", out var b))
        {
            double m = set.GetProperty("m").GetDouble();
            double x0 = b.GetProperty("x0").GetDouble() - m, y0 = b.GetProperty("y0").GetDouble() - m, x1 = b.GetProperty("x1").GetDouble() + m, y1 = b.GetProperty("y1").GetDouble() + m;
            byte[] A = Pixels(pic), B = Pixels(copy);
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 240; x++)
                {
                    int i = (y * 240 + x) * 4;
                    bool diff = Math.Abs(A[i] - B[i]) + Math.Abs(A[i + 1] - B[i + 1]) + Math.Abs(A[i + 2] - B[i + 2]) > 6;
                    bool inBox = x >= x0 && x <= x1 && y >= y0 && y <= y1;
                    if (diff && inBox) inside++;
                    if (diff && !inBox) outside++;
                    if (B[i + 1] > 200 && B[i + 2] < 90 && B[i] < 90) green++;   // BGRA
                }
            detail += $" | changed inside {inside}, outside {outside}, green {green}";
        }
        Check("MT10 text on a picture: Thai + emoji in Tahoma, green with a blue outline, saved into a copy — pixels change only where the text is, the original is untouched, the layer stays editable",
            dirty && saved && set.TryGetProperty("n", out var n1) && n1.GetInt32() == 1
            && set.GetProperty("font").GetString() == "Tahoma" && set.GetProperty("color").GetString() == "#00ff00"
            && set.GetProperty("stroke").GetProperty("width").GetDouble() == 3 && set.GetProperty("text").GetString() == "ที่นี่ ผู้ใหญ่ น้ำ 👍"
            && inside > 150 && outside == 0 && green > 20 && File.ReadAllBytes(pic).SequenceEqual(before) && still == "1" && !tab.IsDirty,
            $"dirty={dirty} saved={saved} still={still} {detail}");

        // an empty layer saves nothing (the save button goes back to "nothing to save" once the text is cleared)
        string emptied = await web.ExecuteScriptAsync(@"(() => { const ta = document.querySelector('#tpanel textarea'); ta.value = '  '; ta.dispatchEvent(new Event('input'));
          return document.querySelector('[data-tip=""media.page.overwriteTip""]').disabled; })()");
        Check("MT10b …a layer emptied of its text counts as nothing to save (overwrite off)", emptied == "true", emptied);
    }

    static byte[] Pixels(string path)
    {
        using var f = File.OpenRead(path);
        var d = BitmapDecoder.Create(f, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(d.Frames[0], PixelFormats.Bgra32, null, 0);
        var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        return px;
    }

    /// <summary>MT11: text burned into a video with ffmpeg (the page draws the layer to a PNG at the video's size,
    /// ffmpeg overlays it, VP9 for WebM, sound copied): the copy exists, same length (±1 frame) and size.</summary>
    static async Task TextOnVideoCheck(CluadeX.Views.Editor.MediaEditorHost host, MediaFileTab tab, string clip, string ff)
    {
        var web = (Microsoft.Web.WebView2.Wpf.WebView2)GetField(host, "_web")!;
        await Idle(400);
        var set = JsJson(await web.ExecuteScriptAsync(@"(() => { try {
          document.querySelector('[data-tip=""media.page.textTip""]').click();
          const v = document.querySelector('#vidbox video'), r = v.getBoundingClientRect();
          v.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, clientX: r.left + r.width / 2, clientY: r.top + r.height * .8, button: 0, pointerId: 1 }));
          const p = document.getElementById('tpanel'); if (!p) return JSON.stringify({ error: 'no panel' });
          const ta = p.querySelector('textarea'); ta.value = 'คำบรรยาย ทดสอบ'; ta.dispatchEvent(new Event('input'));
          const Ly = ED.text.layers[0];
          return JSON.stringify({ n: ED.text.layers.length, t0: Ly.t0, t1: Ly.t1, dur: document.querySelector('#vidbox video').duration,
            label: document.querySelector('[data-tip=""media.page.saveCopyTip""] span').dataset.s });
        } catch (e) { return JSON.stringify({ error: String(e && e.stack || e) }); } })()"));
        await Idle(300);
        if (set.ValueKind != System.Text.Json.JsonValueKind.Object || set.TryGetProperty("error", out _))
        { Check("MT11 text on a video: the text tool adds a layer", false, set.ToString()); return; }
        bool saved = await tab.Session!.SaveCopyAsync().WaitAsync(TimeSpan.FromSeconds(180));
        string copy = Path.Combine(Path.GetDirectoryName(clip)!, Path.GetFileNameWithoutExtension(clip) + "-edit.webm");
        (double secs, string size) Probe(string file)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-i", file }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd(); p.WaitForExit();
            var d = System.Text.RegularExpressions.Regex.Match(err, @"Duration: 00:00:(\d+\.\d+)");
            var s = System.Text.RegularExpressions.Regex.Match(err, @"Video: .*?(\d{2,5}x\d{2,5})");
            return (d.Success ? double.Parse(d.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1, s.Success ? s.Groups[1].Value : "");
        }
        string temp = Path.Combine(Path.GetTempPath(), "cluadex-media");
        bool tempClean = await WaitUntil(() => !Directory.Exists(temp) || Directory.GetDirectories(temp).Length == 0, 5000);
        var src = Probe(clip);
        var outp = File.Exists(copy) ? Probe(copy) : (-1, "");
        Check("MT11 text burned into a WebM with ffmpeg: whole-clip layer by default, the copy exists with the same length (±1 frame) and size, nothing left in %TEMP%",
            saved && set.ValueKind == System.Text.Json.JsonValueKind.Object && set.TryGetProperty("n", out var n) && n.GetInt32() == 1
            && set.GetProperty("t0").GetDouble() == 0 && Math.Abs(set.GetProperty("t1").GetDouble() - set.GetProperty("dur").GetDouble()) < 0.01
            && set.GetProperty("label").GetString() == "media.page.saveBurn"
            && outp.secs > 0 && Math.Abs(outp.secs - src.secs) <= 0.041 && outp.size == src.size && outp.size == "320x240"
            && !tab.IsBusy && tempClean,
            $"saved={saved} {set} | src {src.secs}s {src.size} → copy {outp.secs}s {outp.size}");
    }

    static void WritePng(string path, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (x < w / 2) { px[i + 2] = 230; px[i + 1] = 40; px[i] = 40; } else { px[i] = 220; px[i + 1] = 90; px[i + 2] = 30; }
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>A mono 16-bit 22.05 kHz WAV: a 440 Hz tone.</summary>
    static void WriteWav(string path, int seconds)
    {
        const int rate = 22050;
        int n = rate * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
    }

    static (int, int) PngSize(string path)
    {
        using var f = File.OpenRead(path);
        var d = BitmapDecoder.Create(f, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return (d.Frames[0].PixelWidth, d.Frames[0].PixelHeight);
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
