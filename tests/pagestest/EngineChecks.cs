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
    }
}
