using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// DM — Design mode and the UI Studio while an AI run edits its project (owner 2026-11-03: "เพิ่มโหมด ดีไซด์ให้ดี … เมื่อ
/// ออกแบบ ก็พาผู้ใช้ไปหน้า ui studio … ai กำลังแก้อยู่เราก็แก้ไขอะไรไม่ได้จนกว่า เอไอจะใช้เสร็จ"). The words that ask for a design
/// (pure), the composer's switch, the lock (a run in another chat on the Studio's project: every write refused, the
/// inspector reverted, the overlay told; the run's end reloads the page and looks at the project again), the request to
/// show the Studio (only for the chat on screen; a brand-new site waits until it is something to show) and the follow
/// tick that must not pull the window to the Code page in a design turn. Fake provider only: no model, no network.
/// The window itself moving to the Studio (MainViewModel) is proven in tests/pagestest (DesignChecks), which runs the
/// real main window.
/// </summary>
internal static partial class Program
{
    /// <summary>CHATTEST_ONLY=DESIGN: just these checks.</summary>
    static async Task<int> RunDesignOnly()
    {
        await TestDesignMode();
        Log($"\n## Result (CHATTEST_ONLY=DESIGN): {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    static async Task TestDesignMode()
    {
        Log("\n## DM — Design mode, and the UI Studio locked while an AI run edits its project");
        TestDesignWords();
        await TestDesignSwitch();
        string root = Path.Combine(PipeRoot, "dm");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        await TestStudioLock(root);
        await TestDesignView(root);
        await TestDesignFollowTick(root);
    }

    static string DmSite(string root, string name)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<html><body>\n<h1>{name}</h1>\n</body></html>\n");
        return dir;
    }

    static ChatSession DmChat(string title, string project)
    {
        var chat = new ChatSession { Title = title, ProjectPath = project, UpdatedAt = DateTime.Now };
        chat.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "work on " + title });
        Persist.SaveSession(chat);
        Vm.Sessions.Insert(0, chat);
        return chat;
    }

    // ── the words ──

    static void TestDesignWords()
    {
        string[] yes =
        {
            "ออกแบบหน้าแรกใหม่ให้หน่อย", "ช่วยดีไซน์หน้า login", "ดีไซด์ให้สวยขึ้น", "ปรับหน้าตาเว็บให้ทันสมัย", "หน้าจอตั้งค่าดูรก",
            "จัดเลย์เอาต์ใหม่", "แก้UIหน้าแรก", "ออกแบบฐานข้อมูลและหน้าจอสมัครสมาชิก",
            "design a landing page for my bakery", "Redesign the header", "make a mock-up of the pricing table", "wireframe the settings screen",
            "a landing page for the app", "the layout breaks on mobile", "improve the UI", "UX review of the checkout",
            "ออกแบบหน้าจอใหม่แล้วดูใน UI Studio",
        };
        string[] no =
        {
            "design the database schema for orders", "API design for the orders service", "use the strategy design pattern here",
            "ออกแบบฐานข้อมูลลูกค้า", "ออกแบบ API ใหม่", "ออกแบบระบบล็อกอิน", "fix the login bug", "build the project and run the tests",
            "guide me through the build", "ช่วยแก้บั๊กหน่อย", "write the system design doc", "", null,
            // the app's own page by name, a screenshot, a screen that hangs: bug reports, not designs
            "UI Studio ค้างตอนเปิดโปรเจกต์", "the UI Studio crashes on open", "แคปหน้าจอมาให้ดูแล้ว", "หน้าจอดำหลังเปิดแอป",
        };
        var missed = yes.Where(t => !DesignGate.AsksForDesign(t)).ToList();
        Check("DM1 the owner's words that ask for a design count, Thai and English (ออกแบบ/ดีไซน์/ดีไซด์/หน้าตา/หน้าจอ/เลย์เอาต์/UI, design/redesign/mock-up/wireframe/landing page/layout/UX)",
            missed.Count == 0, string.Join(" | ", missed));
        var wrong = no.Where(t => DesignGate.AsksForDesign(t)).ToList();
        Check("DM1 'design' that is not about screens does not (database / API / schema / system design, design pattern), nor unrelated words",
            wrong.Count == 0, string.Join(" | ", wrong));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool huge = DesignGate.AsksForDesign(new string('x', 400_000) + " desig");
        Check("DM1 a huge message is answered fast (the regex has a time limit and no backtracking blow-up)", !huge && sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
    }

    // ── the composer's switch ──

    static async Task TestDesignSwitch()
    {
        await FreshChat();
        var a = Vm.CurrentSession!;
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "a chat with something in it" });
        var toggle = (CheckBox)View.FindName("DesignToggle");
        await Render();
        Check("DM2 the Design switch is beside the input (next to Ultra), off by default", toggle is { IsVisible: true } && toggle.IsChecked == false && !Vm.IsDesignMode);
        Vm.IsDesignMode = true;
        await Render();
        string file = Path.Combine(Settings.DataRoot, "design-chats.json");
        Check("DM2 on: the chat's turns are design turns, and it is kept on disk", toggle.IsChecked == true && DesignGate.Why(a.Id) == "switch"
            && File.Exists(file) && File.ReadAllText(file).Contains(a.Id));
        await FreshChat();
        await Render();
        Check("DM2 it is per chat: a new chat starts off", toggle.IsChecked == false && !DesignGate.IsOn(Vm.CurrentSession!.Id));
        await Open(a.Id);
        Check("DM2 and comes back on with its chat", toggle.IsChecked == true);
        Vm.IsDesignMode = false;
        var th = new LocalizationService();
        th.SetLanguage("th");
        Check("DM2 its words exist in Thai", th.T("chat.design") == "ดีไซน์" && th.T("chat.designTip").Contains("UI Studio") && th.T("studio.err.aiWorking").Contains("AI"),
            th.T("chat.designTip"));
    }

    // ── the lock ──

    static async Task TestStudioLock(string root)
    {
        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        var graph = Sp.GetRequiredService<NodeGraphViewModel>();
        string a = DmSite(root, "dm-alpha");
        string aHtml = Path.Combine(a, "index.html");
        var chatA = DmChat("dm alpha", a);
        await Open(chatA.Id);
        await WaitUntil(() => studio.HasProject && SamePath(studio.Root, a) && studio.IsServing);
        Check("DM3 (control) the Studio shows the chat's project, a static site, and serves it", SamePath(ui.ProjectPath, a) && studio.IsServing, ui.ProjectPath);

        var scripts = new List<string>();
        Action<string> onScript = s => scripts.Add(s);
        studio.ScriptRequested += onScript;
        int reloads = 0;
        Action<bool> onReload = _ => reloads++;
        studio.PageReloadRequested += onReload;
        try
        {
            ui.NotePage("http://localhost:1/index.html");
            studio.NotePageLoaded();
            StudioElement Heading() => new() { NodeId = 7, Tag = "h1", Src = "index.html:2:1", SrcKind = "locator", Selector = "h1", Styles = new() { ["color"] = "black" } };
            studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = Heading() });
            var color = ui.StyleGroups.SelectMany(g => g.Fields).First(f => f.Prop == "color");
            color.Value = "red";
            ui.AiRequest = "make it bold";
            graph.Mode = "live";
            Check("DM3 (control) no run here: the picked element can be saved, moved, asked about", !ui.IsLockedByAi && ui.IsDirty && ui.SaveCommand.CanExecute(null)
                && ui.DuplicateCommand.CanExecute(null) && ui.AskAiCommand.CanExecute(null) && graph.CanEditLive && ui.CanEditNow);

            // ── a run in ANOTHER chat on this project (a pipe task while the owner is using the window: its own chat) ──
            string before = File.ReadAllText(aHtml);
            var gate = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMBG]"] = new Queue<FakeStep>([FakeStep.Gated(["Working on the page"], gate, [" — done."])]);
            var mine = Vm.CurrentSession;
            SetField(Vm, "_lastUserActivityUtc", DateTime.UtcNow);
            var bg = Task.Run(() => Vm.RunMcpTaskAsync("dm-bg", "[DMBG] Edit the page.", null, null, a, CancellationToken.None, maxSteps: 3));
            bool locked = await WaitUntil(() => ui.IsLockedByAi, 20000);
            await Render();
            Check("DM3 a run in another chat on the Studio's project locks the Studio (the chat on screen stays)", locked && ReferenceEquals(Vm.CurrentSession, mine),
                $"locked={ui.IsLockedByAi} chat={Vm.CurrentSession?.Title}");
            Check("DM3 the unsaved inspector edit is taken back on the page and in the inspector, and the user is told why",
                !ui.IsDirty && color.Value == "black" && scripts.Any(s => s.Contains("revertPreview")) && ui.StatusText == L("studio.lock.reverted"), $"{color.Value} '{ui.StatusText}'");
            Check("DM3 the page's overlay is told (no in-place edit, resize or keys)", scripts.Any(s => s.Contains("setLocked(true)")));
            Check("DM3 a banner says why, not a full-screen spinner", ui.AiLockText == L("studio.lock.banner") && !ui.CanEditNow && !ui.AiLockWaiting);
            color.Value = "blue";   // (the field is disabled on screen; the command is what is checked)
            Check("DM3 Save, Move, Duplicate, Delete, Undo, Ask AI and the graph's live edits are all off",
                ui.IsDirty && !ui.SaveCommand.CanExecute(null) && !ui.MoveUpCommand.CanExecute(null) && !ui.DuplicateCommand.CanExecute(null)
                && !ui.DeleteCommand.CanExecute(null) && !ui.UndoCommand.CanExecute(null) && !ui.AskAiCommand.CanExecute(null) && !graph.CanEditLive
                && !ui.InstallLocatorCommand.CanExecute(null));
            string? edit = studio.EditSource("index.html:2:1", "h1", "inspector",
                (t, tag, d) => SourceTagEditor.SetStyle(t, tag, d, new Dictionary<string, string?> { ["color"] = "green" }));
            string? write = studio.WriteEdit(aHtml, before, before + "<!-- x -->", false, "tool");
            string? undo = studio.Undo(out _);
            var restore = studio.RestoreFiles(new Dictionary<string, string?> { [aHtml] = "gone" });
            Check("DM3 the hard stop: StudioService refuses every write (edit, tool edit, undo, undo-all) with studio.err.aiWorking",
                edit == "studio.err.aiWorking" && write == "studio.err.aiWorking" && undo == "studio.err.aiWorking" && restore.Count == 1
                && File.ReadAllText(aHtml) == before, $"edit={edit} write={write} undo={undo} restore={restore.Count}");
            ui.Structure("duplicate");
            ui.InlineText(Heading(), "typed in place");
            Check("DM3 the overlay's way in (a key, an in-place edit) writes nothing either", File.ReadAllText(aHtml) == before && ui.StatusText.Contains(L("studio.err.aiWorking")),
                ui.StatusText);
            Check("DM3 a second request to the AI from the Studio is refused while the run works here", !Vm.SendFromStudio("[UI Studio] make it bold"));

            // waiting for the owner: a permission card in the run's chat
            var runs = Field<IEnumerable>(Vm, "_runs").Cast<object>().ToList();
            var bgRun = runs.FirstOrDefault(r => ((ChatSession)r.GetType().GetProperty("Session")!.GetValue(r)!).Title == "dm-bg");
            if (bgRun != null)
            {
                var msgs = (ObservableCollection<ChatMessage>)bgRun.GetType().GetProperty("Messages")!.GetValue(bgRun)!;
                var ask = new ChatMessage { Role = MessageRole.PermissionRequest, Content = "Allow the edit?" };
                msgs.Add(ask);
                ui.RefreshAiLock();
                Check("DM3 a run waiting for the owner's answer says so on the banner", ui.AiLockWaiting && ui.AiLockText == L("studio.lock.waiting"), ui.AiLockText);
                msgs.Remove(ask);
                ui.RefreshAiLock();
            }
            else Check("DM3 (control) the run of the other chat is found", false, string.Join(",", runs.Select(r => r.GetType().Name)));

            // ── the run ends ──
            int reloadsBefore = reloads;
            gate.Release();
            await Task.WhenAny(bg, Task.Delay(20000));
            bool unlocked = await WaitUntil(() => !ui.IsLockedByAi, 10000);
            await Render();
            Check("DM4 the lock clears when the run ends (EndTurn), and the overlay is told", unlocked && ui.CanEditNow && scripts.Any(s => s.Contains("setLocked(false)")));
            Check("DM4 the page is reloaded for real (the page-load reload, not location.reload)", reloads > reloadsBefore, $"reloads {reloadsBefore} → {reloads}");
            color.Value = "green";
            Check("DM4 edits work again", ui.SaveCommand.CanExecute(null) && ui.DuplicateCommand.CanExecute(null) && graph.CanEditLive);
            ui.SaveCommand.Execute(null);
            Check("DM4 …and a save lands in the file", await WaitUntil(() => File.ReadAllText(aHtml).Contains("green"), 5000), File.ReadAllText(aHtml));

            // ── a run that turned the project into another kind ──
            var gate2 = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMKIND]"] = new Queue<FakeStep>([FakeStep.Gated(["Moving to Laravel"], gate2, [" — done."])]);
            SetField(Vm, "_lastUserActivityUtc", DateTime.UtcNow);
            var bg2 = Task.Run(() => Vm.RunMcpTaskAsync("dm-kind", "[DMKIND] Make it a Laravel site.", null, null, a, CancellationToken.None, maxSteps: 3));
            await WaitUntil(() => ui.IsLockedByAi, 20000);
            File.WriteAllText(Path.Combine(a, "composer.json"), "{\"require\":{\"laravel/framework\":\"^11.0\"}}");
            File.WriteAllText(Path.Combine(a, "artisan"), "<?php\n");
            await Task.Delay(700);
            Check("DM5 (control) while the run works the Studio keeps what it detected", studio.Project.Kind == StudioProjectKind.Static, studio.Project.Kind.ToString());
            gate2.Release();
            await Task.WhenAny(bg2, Task.Delay(20000));
            bool moved = await WaitUntil(() => studio.Project.Kind == StudioProjectKind.Server, 10000);
            Check("DM5 after the run the project is looked at again: a static site the AI turned into Laravel is shown as one",
                moved && !ui.IsLockedByAi && ui.ProjectKindText == L("studio.kind.server").Replace("{0}", "laravel"), $"{studio.Project.Kind} '{ui.ProjectKindText}'");
            Check("DM5 …the old kind's preview (the static server) is stopped and the new one is what serves", studio.IsServing && studio.CurrentBaseUrl == studio.ServerBaseUrl,
                $"{studio.CurrentBaseUrl} / {studio.ServerBaseUrl}");
        }
        finally
        {
            studio.ScriptRequested -= onScript;
            studio.PageReloadRequested -= onReload;
            graph.Mode = "project";
            studio.HandleOverlayMessage(new OverlayMessage { Type = "deselect" });
            ui.AiRequest = "";
            Fake.ScriptFor.Remove("[DMBG]");
            Fake.ScriptFor.Remove("[DMKIND]");
        }
    }

    // ── the way to the Studio ──

    static async Task TestDesignView(string root)
    {
        var ui = Sp.GetRequiredService<UiStudioViewModel>();
        var studio = Sp.GetRequiredService<StudioService>();
        int ready = 0;
        Action onReady = () => ready++;
        ui.DesignViewReady += onReady;
        try
        {
            string b = DmSite(root, "dm-beta");
            var chatB = DmChat("dm beta", b);
            await Open(chatB.Id);
            await WaitUntil(() => studio.HasProject && SamePath(studio.Root, b));
            Vm.IsDesignMode = true;
            var gate = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMN1]"] = new Queue<FakeStep>([FakeStep.Gated(["Designing"], gate, [" — done."])]);
            await Send("[DMN1] make the header bolder");
            await WaitUntil(() => ready > 0, 5000);
            await Render();
            Check("DM6 the owner's Send in a design chat asks for the UI Studio at once (its project is a site it can show)", ready == 1, $"ready={ready}");
            bool told = await WaitUntil(() => Fake.Calls.ToList().Any(c => c.Input.Contains("[DMN1]")), 5000)
                && Fake.Calls.ToList().Any(c => c.Input.Contains("[DMN1]") && c.Input.Contains("Design mode is on"));
            Check("DM6 the model is told how to work for the Studio in this turn's own input (never the shared system prompt)", told,
                Fake.Calls.LastOrDefault()?.Input is { } inp ? inp[..Math.Min(200, inp.Length)] : "(no call)");
            Check("DM6 the owner sees a design notice in the chat", Vm.Messages.Any(m => m.IsNotice && m.Content.Contains(L("chat.design.notice"))));
            gate.Release();
            await WaitIdle();

            // a brand-new site: the chat's folder is empty when the turn starts
            string e = Path.Combine(root, "dm-new");
            Directory.CreateDirectory(e);
            var chatE = DmChat("dm new site", e);
            await Open(chatE.Id);
            await WaitUntil(() => studio.HasProject && SamePath(studio.Root, e));
            Vm.IsDesignMode = true;
            var gateE = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMN2]"] = new Queue<FakeStep>([FakeStep.Gated(["Writing the site"], gateE, [" — done."])]);
            await Send("[DMN2] make me a page for my cafe");
            await WaitUntil(() => Vm.IsGenerating);
            await Task.Delay(300);
            await Render();
            Check("DM7 a brand-new (empty) project: nothing to show yet, the wish waits", ready == 1 && ui.DesignViewPending && ui.IsUnsupported, $"ready={ready} pending={ui.DesignViewPending}");
            File.WriteAllText(Path.Combine(e, "index.html"), "<html><body><h1>Cafe</h1></body></html>\n");
            bool shown = await WaitUntil(() => ready == 2, 8000);
            Check("DM7 …and the Studio comes forward the moment the AI writes its first page", shown && !ui.DesignViewPending && studio.Project.Kind == StudioProjectKind.Static,
                $"ready={ready} kind={studio.Project.Kind}");
            gateE.Release();
            await WaitIdle();

            // the owner moves to another chat before there is anything to show: nothing pulls them later
            string f = Path.Combine(root, "dm-left");
            Directory.CreateDirectory(f);
            var chatF = DmChat("dm left", f);
            await Open(chatF.Id);
            Vm.IsDesignMode = true;
            var gateF = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMN3]"] = new Queue<FakeStep>([FakeStep.Gated(["Writing"], gateF, [" — done."])]);
            await Send("[DMN3] design a page");
            await WaitUntil(() => Vm.IsGenerating);
            await Task.Delay(200);
            bool waited = ui.DesignViewPending;
            await Open(chatB.Id);
            File.WriteAllText(Path.Combine(f, "index.html"), "<html><body>left</body></html>\n");
            await Task.Delay(1200);
            Check("DM8 only for the chat on screen: switching away drops the wish, the page written later moves nothing", waited && !ui.DesignViewPending && ready == 2,
                $"waited={waited} pending={ui.DesignViewPending} ready={ready}");
            gateF.Release();
            await WaitUntil(() => !Vm.HasRunningElsewhere, 20000);

            // the owner's words, with the switch off
            Vm.IsDesignMode = false;
            var gateW = new SemaphoreSlim(0);
            Fake.ScriptFor["[DMN4]"] = new Queue<FakeStep>([FakeStep.Gated(["ok"], gateW, [" — done."])]);
            await Send("[DMN4] redesign the landing page");
            await WaitUntil(() => ready == 3, 5000);
            Check("DM9 switch off, the owner's words ask for a design: that turn is one", ready == 3, $"ready={ready}");
            gateW.Release();
            await WaitIdle();
            int notices = Vm.Messages.Count(m => m.IsNotice);
            Fake.ScriptFor["[DMN5]"] = new Queue<FakeStep>([FakeStep.Reply("ok")]);
            await Send("[DMN5] review the database schema design");
            await WaitIdle();
            await Task.Delay(200);
            Check("DM9 'database schema design' is not one: no Studio, no notice", ready == 3 && Vm.Messages.Count(m => m.IsNotice) == notices, $"ready={ready}");
        }
        finally
        {
            ui.DesignViewReady -= onReady;
            foreach (var k in new[] { "[DMN1]", "[DMN2]", "[DMN3]", "[DMN4]", "[DMN5]" }) Fake.ScriptFor.Remove(k);
        }
    }

    // ── the follow tick ──

    static async Task TestDesignFollowTick(string root)
    {
        string g = DmSite(root, "dm-follow");
        var chat = DmChat("dm follow", g);
        await Open(chat.Id);
        var fire = typeof(ChatViewModel).GetMethod("OnToolExecuted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int mutated = 0, shown = 0;
        Action<string, int, EditDiffResult?> onMutated = (_, _, _) => mutated++;
        Action onShown = () => shown++;
        Vm.FileMutatedByAgent += onMutated;
        Vm.ShowEditorRequested += onShown;
        try
        {
            async Task Edit()
            {
                fire.Invoke(Vm, [new ToolResult { ToolName = "edit_file", Type = ToolType.EditFile, Success = true, Output = "edited", FilePath = "index.html", Arguments = new() { ["path"] = "index.html" } }]);
                await Render(); await Task.Delay(50); await Render();
            }
            Vm.FollowEditsHere = true;
            Vm.IsDesignMode = true;
            await Edit();
            Check("DM10 a design turn with the follow tick on: the edit still opens in the editor behind, but the window stays (no jump to Code)",
                mutated == 1 && shown == 0, $"mutated={mutated} shown={shown}");
            Vm.IsDesignMode = false;
            DesignGate.NoteOwnerMessage(chat.Id, "thanks");
            await Edit();
            Check("DM10 (control) not a design turn: the tick brings the Code page forward as before", mutated == 2 && shown == 1, $"mutated={mutated} shown={shown}");
        }
        finally
        {
            Vm.FollowEditsHere = false;
            Vm.FileMutatedByAgent -= onMutated;
            Vm.ShowEditorRequested -= onShown;
        }
    }
}
