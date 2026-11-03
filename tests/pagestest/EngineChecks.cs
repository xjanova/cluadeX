using System.Windows;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views;

namespace PagesTest;

/// <summary>The Studio's browser engine when it has to start again, in the real browser: a start retried on the same
/// control hooks the browser's events once (else every overlay message — an in-place edit — is handled twice), and a
/// browser that keeps dying is given up until Try again (a navigation, a reload or the gallery does not bring the loop
/// back, and nothing is left showing as loading).</summary>
internal static partial class Program
{
    static async Task EngineChecks(UiStudioViewModel vm, UiStudioView view)
    {
        Log("\n## Browser engine (start retried, rebuilds given up)");
        var studio = Studio(vm);
        bool loaded = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && GetField(view, "_engineReady") is true, 20000);
        string page = vm.Url;
        Check("engine: (control) a page is loaded", loaded && page.Length > 0, $"{studio.PageLoadState} url={page}");
        if (!loaded) return;

        // (1) the start runs again on the same control (Try again after the overlay script failed to register)
        SetField(view, "_engineReady", false);
        if (Invoke(view, "StartEngine") is Task again) { try { await again; } catch { } }
        bool reReady = GetField(view, "_engineReady") is true
                       && await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url == page, 15000);
        await Idle(700);
        int trees = 0;
        Action onTree = () => trees++;
        studio.TreeChanged += onTree;
        studio.RequestTree();
        await WaitUntil(() => trees > 0, 5000);
        await Idle(800);
        studio.TreeChanged -= onTree;
        Check("engine: a start retried on the same control hooks the browser once (one tree message is handled once)",
            reReady && trees == 1, $"ready={reReady} trees={trees}");

        // (2) a browser that keeps dying: the fourth rebuild within a minute gives up
        (GetField(view, "_rebuilds") as List<DateTime>)?.Clear();
        for (int i = 0; i < 4; i++) Invoke(view, "RebuildEngine");
        await Idle(300);
        var engineError = (FrameworkElement)view.FindName("EngineError");
        Check("engine: the fourth rebuild within a minute gives up — the error card shows and nothing is left loading",
            GetField(view, "_engineGaveUp") is true && engineError.Visibility == Visibility.Visible && studio.PageLoadState == PageLoadState.Idle && !vm.IsPageLoading,
            $"gaveUp={GetField(view, "_engineGaveUp")} card={engineError.Visibility} {studio.PageLoadState} loading={vm.IsPageLoading}");
        studio.NavigatePage(page);
        studio.ReloadPage(hard: false);
        await Idle(1500);
        Check("engine: …a navigation or a reload does not start it again (only Try again does)",
            GetField(view, "_engineReady") is false && GetField(view, "_init") == null && engineError.Visibility == Visibility.Visible && !vm.IsPageLoading,
            $"ready={GetField(view, "_engineReady")} init={GetField(view, "_init") != null} card={engineError.Visibility} loading={vm.IsPageLoading}");
        Invoke(view, "EngineRetry_Click", null, null);
        bool back = await WaitUntil(() => studio.PageLoadState == PageLoadState.Loaded && vm.Url == page, 30000) && await HasOverlay(studio);
        Check("engine: Try again starts it and opens the page asked for, verified, with the overlay",
            back && engineError.Visibility != Visibility.Visible && GetField(view, "_engineGaveUp") is false, $"{studio.PageLoadState} url={vm.Url} card={engineError.Visibility}");
        if (back) await BrowserProcessDies(vm, view, studio, page);
    }

    /// <summary>(3) The browser process itself dies (a runtime update, a GPU or driver crash, a kill): the control marks
    /// itself crashed and its CoreWebView2 getter throws from then on. The Studio must still replace it and bring the page
    /// back — and nothing the service raises meanwhile (the AI lock's script runs from the chat's run start and end) may
    /// throw into its caller or onto the dispatcher.</summary>
    static async Task BrowserProcessDies(UiStudioViewModel vm, UiStudioView view, StudioService studio, string page)
    {
        object? web = GetField(view, "Web");
        int pid = 0;
        try
        {
            object? core = web?.GetType().GetProperty("CoreWebView2")!.GetValue(web);
            pid = Convert.ToInt32(core?.GetType().GetProperty("BrowserProcessId")!.GetValue(core));
        }
        catch { }
        Check("engine: (control) the Studio's browser process is known", pid > 0, pid.ToString());
        if (pid <= 0) return;
        (GetField(view, "_rebuilds") as List<DateTime>)?.Clear();
        int onDispatcher = 0, toCaller = 0;
        string? first = null;
        System.Windows.Threading.DispatcherUnhandledExceptionEventHandler onError = (_, e) =>
        {
            onDispatcher++;
            first ??= e.Exception.GetType().Name + ": " + e.Exception.Message;
            e.Handled = true;   // (counted as a failure here; the app would show an error box)
        };
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += onError;
        bool rebuilt;
        try
        {
            using (var browser = System.Diagnostics.Process.GetProcessById(pid)) browser.Kill();
            rebuilt = await WaitUntil(() =>
            {
                // what an AI run starting or ending in this project does to the Studio, every 60 ms while it recovers
                try { studio.RunScript("void 0"); }
                catch (Exception ex) { toCaller++; first ??= "RunScript: " + ex.GetType().Name + ": " + ex.Message; }
                return !ReferenceEquals(GetField(view, "Web"), web) && studio.PageLoadState == PageLoadState.Loaded && vm.Url == page;
            }, 30000) && await HasOverlay(studio);
            await Idle(300);
        }
        finally { dispatcher.UnhandledException -= onError; }
        Check("engine: the browser process killed → the crashed control is replaced and the page comes back, verified, with the overlay",
            rebuilt, $"replaced={!ReferenceEquals(GetField(view, "Web"), web)} {studio.PageLoadState} url={vm.Url} first={first}");
        Check("engine: …and nothing threw meanwhile — not onto the dispatcher (an error box), not into RunScript's caller (the chat's run start/end)",
            onDispatcher == 0 && toCaller == 0, $"dispatcher={onDispatcher} caller={toCaller} first={first}");
    }
}
