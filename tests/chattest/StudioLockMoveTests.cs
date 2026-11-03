using System.Collections;
using System.IO;
using System.Reflection;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// DL — the UI Studio's AI lock when the run or the Studio moves (run from <see cref="TestDesignMode"/>, CHATTEST_ONLY=DESIGN):
///  · the Studio, locked by a run in project A, follows the owner to project B where another run works: the lock is B's
///    now, so B's run ending is what unlocks and catches up (it used to keep A, and B's end did nothing);
///  · a running chat opens the Studio's project mid-run (open_project add=true, or a background chat moving its folder):
///    the lock engages at once, and lets go when the run moves away (no run started or ended, so nothing used to tell it);
///  · a design turn in a Vite project: a dev server whose packages are installed starts beside the run (dry-run here: the
///    start is seen, nothing runs); one that needs an install waits for the run's end, and the design view waits until the
///    Studio can show something.
/// Fake provider only: no model, no network, no npm.
/// </summary>
internal static partial class Program
{
    static async Task TestStudioLockMoves(string root)
    {
        Log("\n## DL — the Studio's AI lock when the run or the Studio moves");
        await TestLockFollowsStudio(root);
        await TestLockOnRunScopeChange(root);
        await TestDesignViteDuringLock(root);
    }

    static object? RunTitled(string title) =>
        Field<IEnumerable>(Vm, "_runs").Cast<object>().FirstOrDefault(r => ((CluadeX.Models.ChatSession)r.GetType().GetProperty("Session")!.GetValue(r)!).Title == title);

    static AgentRunScope? ScopeOf(object? run) => run?.GetType().GetProperty("Scope")!.GetValue(run) as AgentRunScope;

    static (SemaphoreSlim Gate, Task Run) StartBackgroundRun(string tag, string title, string folder)
    {
        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor[tag] = new Queue<FakeStep>([FakeStep.Gated(["Working"], gate, [" — done."])]);
        SetField(Vm, "_lastUserActivityUtc", DateTime.UtcNow);   // (the owner is using the window: the chat on screen stays)
        var run = Task.Run(() => Vm.RunMcpTaskAsync(title, tag + " Edit the page.", null, null, folder, CancellationToken.None, maxSteps: 3));
        return (gate, run);
    }

    // ── the Studio moves from one locked project to another ──

    static async Task TestLockFollowsStudio(string root)
    {
        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        string a = DmSite(root, "dl-alpha"), b = DmSite(root, "dl-beta");
        var chatA = DmChat("dl alpha", a);
        var chatB = DmChat("dl beta", b);
        int reloads = 0;
        Action<bool> onReload = _ => reloads++;
        studio.PageReloadRequested += onReload;
        var runA = StartBackgroundRun("[DLA]", "dl-run-a", a);
        var runB = StartBackgroundRun("[DLB]", "dl-run-b", b);
        try
        {
            await Open(chatA.Id);
            bool lockedA = await WaitUntil(() => studio.HasProject && SamePath(studio.Root, a) && ui.IsLockedByAi, 20000)
                           && await WaitUntil(() => RunTitled("dl-run-b") != null, 10000);
            Check("DL1 (control) the Studio on A is locked by A's run, while another run works in B", lockedA, $"root={studio.Root} locked={ui.IsLockedByAi}");

            await Open(chatB.Id);
            bool onB = await WaitUntil(() => SamePath(studio.Root, b), 10000);
            Check("DL1 the owner opens B's chat: the Studio follows, still locked — and the lock is B's now (not A's)",
                onB && ui.IsLockedByAi && SamePath(Field<string>(ui, "_lockRoot"), b), $"root={studio.Root} locked={ui.IsLockedByAi} lockRoot={Field<string>(ui, "_lockRoot")}");

            await WaitUntil(() => studio.IsServing, 10000);
            int before = reloads;
            ui.Status("(before B's run ends)");
            runB.Gate.Release();
            await Task.WhenAny(runB.Run, Task.Delay(20000));
            bool unlocked = await WaitUntil(() => !ui.IsLockedByAi, 10000);
            await Render();
            Check("DL1 B's run ends (A's still works elsewhere): the Studio on B unlocks and catches up — 'AI finished' and the page reloaded",
                unlocked && ui.StatusText == L("studio.lock.done") && reloads > before, $"unlocked={unlocked} status='{ui.StatusText}' reloads {before} → {reloads}");
        }
        finally
        {
            runA.Gate.Release();
            runB.Gate.Release();
            await Task.WhenAny(Task.WhenAll(runA.Run, runB.Run), Task.Delay(20000));
            studio.PageReloadRequested -= onReload;
            Fake.ScriptFor.Remove("[DLA]");
            Fake.ScriptFor.Remove("[DLB]");
        }
    }

    // ── a running chat opens the Studio's project mid-run ──

    static async Task TestLockOnRunScopeChange(string root)
    {
        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        string b = DmSite(root, "dl-shown"), c = DmSite(root, "dl-elsewhere");
        var chatB = DmChat("dl shown", b);
        await Open(chatB.Id);
        await WaitUntil(() => studio.HasProject && SamePath(studio.Root, b));
        var added = typeof(ChatViewModel).GetMethod("OnAgentAddedFolder", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var opened = typeof(ChatViewModel).GetMethod("OnAgentOpenedProject", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // open_project add=true: the run in C opens B beside it
        var run1 = StartBackgroundRun("[DLC]", "dl-run-c", c);
        try
        {
            var scope = await WaitUntil(() => ScopeOf(RunTitled("dl-run-c")) != null, 10000) ? ScopeOf(RunTitled("dl-run-c")) : null;
            Check("DL2 (control) a run in another folder does not lock the Studio", scope != null && !ui.IsLockedByAi, $"scope={scope != null} locked={ui.IsLockedByAi}");
            if (scope != null)
            {
                scope.AddExtraFolder(b);             // what open_project add=true does to the run's scope…
                added.Invoke(Vm, [scope, b]);        // …and then on the UI thread
                Check("DL2 the run opens the Studio's project beside its own (open_project add=true): the Studio locks at once",
                    ui.IsLockedByAi && !ui.CanEditNow, $"locked={ui.IsLockedByAi}");
            }
            run1.Gate.Release();
            await Task.WhenAny(run1.Run, Task.Delay(20000));
            Check("DL2 (control) …and its end unlocks it", await WaitUntil(() => !ui.IsLockedByAi, 10000));
        }
        finally { run1.Gate.Release(); Fake.ScriptFor.Remove("[DLC]"); }

        // open_project without add, in a background chat: the run's main folder moves to B, then away again
        var run2 = StartBackgroundRun("[DLD]", "dl-run-d", c);
        try
        {
            var scope = await WaitUntil(() => ScopeOf(RunTitled("dl-run-d")) != null, 10000) ? ScopeOf(RunTitled("dl-run-d")) : null;
            if (scope != null)
            {
                scope.WorkingDirectory = b;
                opened.Invoke(Vm, [scope, b]);
                bool lockedNow = ui.IsLockedByAi;
                scope.WorkingDirectory = c;
                opened.Invoke(Vm, [scope, c]);
                bool freed = !ui.IsLockedByAi;
                Check("DL2 a background chat's run moves its folder to the Studio's project (open_project): locked at once; moved away again: unlocked at once",
                    lockedNow && freed, $"locked={lockedNow} freed={freed}");
            }
            else Check("DL2 (control) the second run is found", false);
            run2.Gate.Release();
            await Task.WhenAny(run2.Run, Task.Delay(20000));
        }
        finally { run2.Gate.Release(); Fake.ScriptFor.Remove("[DLD]"); }
    }

    // ── a design turn in a Vite project ──

    static async Task TestDesignViteDuringLock(string root)
    {
        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        string v = Path.Combine(root, "dl-vite");
        Directory.CreateDirectory(Path.Combine(v, "node_modules"));
        File.WriteAllText(Path.Combine(v, "package.json"), "{\"scripts\":{\"dev\":\"vite\"},\"devDependencies\":{\"vite\":\"^5.0.0\"}}");
        File.WriteAllText(Path.Combine(v, "index.html"), "<html><body><div id=\"app\"></div></body></html>\n");
        var lines = new List<string>();
        Action<string> onOut = l => lines.Add(l);
        int ready = 0;
        Action onReady = () => ready++;
        bool oldDry = CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun;
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = true;   // (a dev server's start is seen, never run)
        studio.ServerOutput += onOut;
        ui.DesignViewReady += onReady;
        var start = typeof(UiStudioViewModel).GetMethod("StartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var canShow = typeof(UiStudioViewModel).GetProperty("CanShowForDesign", BindingFlags.NonPublic | BindingFlags.Instance)!;
        SemaphoreSlim? gate = null;
        try
        {
            var chatV = DmChat("dl vite", v);
            await Open(chatV.Id);
            await WaitUntil(() => studio.HasProject && SamePath(studio.Root, v) && studio.Project.Kind == StudioProjectKind.Vite);
            await WaitUntil(() => !studio.IsStarting, 10000);
            await Task.Delay(300);

            // the design turn: the owner's own run, in this very folder (so the Studio is locked by it)
            Vm.IsDesignMode = true;
            gate = new SemaphoreSlim(0);
            Fake.ScriptFor["[DLV]"] = new Queue<FakeStep>([FakeStep.Gated(["Designing"], gate, [" — done."])]);
            await Send("[DLV] make the header bolder");
            bool locked = await WaitUntil(() => ui.IsLockedByAi, 10000);
            await WaitUntil(() => ready > 0, 3000);
            Check("DL3 (control) a design turn in a Vite project with its packages installed: the Studio is locked by it and comes forward",
                locked && ready == 1, $"locked={locked} ready={ready}");

            int seen = lines.Count(l => l.Contains("[dry-run]"));
            await (Task)start.Invoke(ui, null)!;
            await Task.Delay(200);
            Check("DL3 packages installed: the dev server starts beside the run (no install) — not 'waits for the AI'",
                lines.Count(l => l.Contains("[dry-run]")) == seen + 1 && ui.StatusText != L("studio.lock.devWaits"),
                $"dry-run lines {seen} → {lines.Count(l => l.Contains("[dry-run]"))}, status '{ui.StatusText}'");

            Directory.Delete(Path.Combine(v, "node_modules"));
            seen = lines.Count(l => l.Contains("[dry-run]"));
            await (Task)start.Invoke(ui, null)!;
            await Task.Delay(200);
            Check("DL3 packages missing: nothing is installed or run beside the run — it waits for the run's end",
                lines.Count(l => l.Contains("[dry-run]")) == seen && ui.StatusText == L("studio.lock.devWaits") && Field<bool>(ui, "_startOnShow"),
                $"dry-run lines {seen} → {lines.Count(l => l.Contains("[dry-run]"))}, status '{ui.StatusText}'");
            Check("DL3 …and the design view would show an empty Studio, so it waits (CanShowForDesign false until it serves or starts)",
                canShow.GetValue(ui) is false, $"{canShow.GetValue(ui)}");

            // a design wish that waits for a Node project without packages comes when the Studio can show something
            SetField(ui, "_designChat", chatV.Id);
            Directory.CreateDirectory(Path.Combine(v, "node_modules"));
            studio.StopServing();   // (raises ServingChanged, as a start that begins does)
            Check("DL3 …the waiting design view comes forward on the Studio's serving change once it can show the page",
                ready == 2 && !ui.DesignViewPending, $"ready={ready} pending={ui.DesignViewPending}");

            gate.Release();
            await WaitIdle();
        }
        finally
        {
            gate?.Release();
            await WaitIdle();
            // the run's end starts the dev server that waited (still dry-run): stopped, and its start let go, before
            // dry-run is put back — a start caught between its safety check and the dry-run test would run npm for real
            await WaitUntil(() => !studio.IsStarting, 10000);
            studio.StopServing();
            await Task.Delay(300);
            Vm.IsDesignMode = false;
            SetField(ui, "_designChat", null!);
            studio.ServerOutput -= onOut;
            ui.DesignViewReady -= onReady;
            Fake.ScriptFor.Remove("[DLV]");
            CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = oldDry;
        }
    }
}
