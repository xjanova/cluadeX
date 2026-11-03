using System.IO;
using System.Reflection;
using CluadeX.ViewModels;

namespace PagesTest;

/// <summary>Design mode in the real main window (owner 2026-11-03: "เมื่อออกแบบ ก็พาผู้ใช้ไปหน้า ui studio"; "ai กำลังแก้อยู่เรา
/// ก็แก้ไขอะไรไม่ได้จนกว่า เอไอจะใช้เสร็จ"): the window moves to the UI Studio when a design turn's project can be shown —
/// unless the user went to another page meanwhile — and the page's overlay takes no in-place edit while locked.
/// The chat side (which turns are design turns, the lock itself) is proven in tests/chattest (DesignModeTests).</summary>
internal static partial class Program
{
    static void RaiseEvent(object o, string name, params object?[] args)
    {
        var field = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        (field?.GetValue(o) as Delegate)?.DynamicInvoke(args);
    }

    static async Task DesignChecks(UiStudioViewModel vm, string fx)
    {
        Log("\n## Design mode (main window + overlay lock)");
        var chat = MainVm.ChatVM;

        // ── the window follows a design turn to the Studio ──
        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);
        // (an id of no chat on screen: the Studio's own handler drops the wish, so only the raised "ready" below counts)
        RaiseEvent(chat, nameof(ChatViewModel.DesignViewRequested), "no-such-chat");
        RaiseEvent(vm, nameof(UiStudioViewModel.DesignViewReady));
        await Idle(200);
        Check("design: the Studio being ready for a design turn moves the window from Chat to the UI Studio", MainVm.SelectedNavItem == "UiStudio", MainVm.SelectedNavItem);

        Invoke(MainVm, "NavigateTo", "Chat");
        await Idle(200);
        RaiseEvent(chat, nameof(ChatViewModel.DesignViewRequested), "no-such-chat");
        Invoke(MainVm, "NavigateTo", "Models");   // the user went elsewhere before the project could be shown
        await Idle(200);
        RaiseEvent(vm, nameof(UiStudioViewModel.DesignViewReady));
        await Idle(200);
        Check("design: …but not when the user went to another page while it waited (the window stays on Models)", MainVm.SelectedNavItem == "Models", MainVm.SelectedNavItem);

        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(300);

        // ── the overlay while an AI run edits the project ──
        var studio = Studio(vm);
        string site = Path.Combine(fx, "design-lock-site");
        W(site, "index.html", "<!doctype html><html><head><title>Lock</title></head><body><h1 id=\"t\">Locked heading</h1></body></html>");
        vm.OpenProject(site, autoStart: true);
        bool ready = await WaitUntil(() => TreeHas(studio, "h1") && vm.Url.Contains("/index.html", StringComparison.Ordinal), 20000) && await HasOverlay(studio);
        Check("design: (control) the lock site loads with the overlay", ready, vm.Url);
        if (!ready || studio.Evaluate == null) return;
        const string dbl = "(() => { const h = document.getElementById('t'); h.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true })); return h.hasAttribute('contenteditable'); })()";
        string? openEdit = await studio.Evaluate("__cx.setMode('select'); " + dbl);
        Check("design: (control) unlocked, a double-click starts the in-place text edit", openEdit == "true", openEdit ?? "(null)");
        string? endedByLock = await studio.Evaluate("__cx.setLocked(true); document.getElementById('t').hasAttribute('contenteditable') + ':' + document.getElementById('t').textContent");
        Check("design: locking ends an in-place edit without writing it", endedByLock == "\"false:Locked heading\"", endedByLock ?? "(null)");
        string? lockedEdit = await studio.Evaluate("__cx.isLocked() + ':' + " + dbl);
        Check("design: locked, a double-click starts no edit", lockedEdit == "\"true:false\"", lockedEdit ?? "(null)");
        await studio.Evaluate("__cx.setLocked(false)");
        string? after = await studio.Evaluate("__cx.isLocked() + ':' + " + dbl);
        Check("design: unlocked again, editing in place works", after == "\"false:true\"", after ?? "(null)");
        await studio.Evaluate("__cx.setLocked(true); __cx.setLocked(false)");   // (ends the edit without writing it)
    }
}
