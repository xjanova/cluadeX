using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>S — the screen belongs to the user while several chats work (owner 2026-10-17): agent edits move the
    /// editor only for the chat on screen with its "ตามดูโค้ด" tick, in the window's project; a pipe task arriving while
    /// the window is in use runs in its own chat in the background; the window always shows the project of the chat on
    /// screen; a long tool call being written says so.</summary>
    static async Task TestScreenGuard()
    {
        Log("\n## S — one screen, many chats: follow tick, screen guard, background pipe runs, draft rows");

        // S1 — when an edit may move the editor.
        string a = Path.Combine(PipeRoot, "proj"), a2 = Path.Combine(PipeRoot, "proj2");
        Check("S1 the chat on screen, ticked, same project → follows", ChatViewModel.ShouldFollowEdit(true, true, a, a));
        Check("S1 a chat NOT on screen never moves the editor (two chats used to fight over it)", !ChatViewModel.ShouldFollowEdit(false, true, a, a));
        Check("S1 tick off → the agent works on, the screen stays", !ChatViewModel.ShouldFollowEdit(true, false, a, a));
        Check("S1 a run in another project never opens its files under this project's title", !ChatViewModel.ShouldFollowEdit(true, true, a2, a));
        Check("S1 no project open → nothing to follow into", !ChatViewModel.ShouldFollowEdit(true, true, a, null));
        Check("S1 inside-folder is by folder, not by name prefix (proj2 is not inside proj)",
            ChatViewModel.IsInsideFolder(Path.Combine(a, "lib", "x.dart"), a) && !ChatViewModel.IsInsideFolder(Path.Combine(a2, "x.dart"), a)
            && ChatViewModel.IsInsideFolder(a + "\\", a));

        // S2 — when outside work may take the screen.
        var now = DateTime.UtcNow;
        var idle = now - TimeSpan.FromMinutes(10);
        Check("S2 nobody using the window, nothing running, no recent take-over → may take it", ChatViewModel.MayTakeScreen(now, idle, idle, false));
        Check("S2 the user typed a moment ago → no", !ChatViewModel.MayTakeScreen(now, now - TimeSpan.FromSeconds(5), idle, false));
        Check("S2 the chat on screen is running (the user watches it) → no", !ChatViewModel.MayTakeScreen(now, idle, idle, true));
        Check("S2 the screen was taken a moment ago → no second take-over yet", !ChatViewModel.MayTakeScreen(now, idle, now - TimeSpan.FromSeconds(30), false));
        Check("S2 after the cooldown → yes again", ChatViewModel.MayTakeScreen(now, idle, now - ChatViewModel.ScreenTakeCooldown - TimeSpan.FromSeconds(1), false));

        // S3 — the file named by a tool input that is still streaming.
        Check("S3 the path is read once its value is whole", AgentRunScope.DraftPath("{\"path\": \"lib/features/work/unpaid_bills.dart\", \"content\": \"import 'pa") == "lib/features/work/unpaid_bills.dart");
        Check("S3 a half-written path is not guessed", AgentRunScope.DraftPath("{\"path\": \"lib/featu") == null);
        Check("S3 escaped Windows paths come out plain", AgentRunScope.DraftPath("{\"file_path\":\"D:\\\\Code\\\\a.cs\",\"x\":1") == @"D:\Code\a.cs");

        // S4 — the Time Machine project list filter.
        var p = new TimeMachineViewModel.ProjectOption(@"D:\Code\thaiprompt admin app\thaipromptadminapp", "thaipromptadminapp", DateTime.Now, true);
        Check("S4 the project list narrows by name or path, case-insensitive; empty shows all",
            TimeMachineViewModel.ProjectMatches(p, "ADMIN") && TimeMachineViewModel.ProjectMatches(p, "D:\\Code") && TimeMachineViewModel.ProjectMatches(p, " ")
            && !TimeMachineViewModel.ProjectMatches(p, "godot"));

        // S5 — the group row's word is accurate.
        Check("S5 a folded row of calls is not called 'background work' (they ran in this turn)",
            !L("chat.tools.groupVerb").Contains("เบื้องหลัง") && L("chat.tools.groupVerb").Length > 0, L("chat.tools.groupVerb"));

        // S6 — the follow tick is per chat.
        Directory.CreateDirectory(ProjA);
        Directory.CreateDirectory(ProjB);
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();
        var first = Vm.CurrentSession!;
        Vm.FollowEditsHere = false;
        Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "hello" });   // kept when the next chat opens
        await FreshChat();
        bool newDefault = Vm.FollowEditsHere;
        await Open(first.Id);
        Check("S6 the tick is per chat: off in one chat, the next chat starts from Settings (on), back in the first it is still off",
            newDefault && !Vm.FollowEditsHere, $"new={newDefault} first={Vm.FollowEditsHere}");
        Vm.FollowEditsHere = true;

        // S7 — a pipe task while the user is typing: its own chat, in the background; the window does not move.
        await FreshChat();
        var mine = Vm.CurrentSession!;
        Vm.UserInput = "I am typing";
        var gate = new SemaphoreSlim(0);
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Gated(["Working"], gate, [" — done in the background."]));
        var run = Task.Run(() => Vm.RunMcpTaskAsync("s7-bg", "Do it quietly.", null, null, ProjB, CancellationToken.None, maxSteps: 3));
        bool started = await WaitUntil(() => Vm.Sessions.Any(s => s.Title == "s7-bg" && s.RunStatus is "working" or "waiting"), 20000);
        await Render();
        Check("S7 the task runs in its own chat while the user types: the chat on screen, its text and its project are untouched",
            started && ReferenceEquals(Vm.CurrentSession, mine) && Vm.UserInput == "I am typing" && SamePath(Vm.WorkingDirectory, ProjA) && WindowMatchesChat(),
            $"started={started} screen={Vm.CurrentSession?.Title} dir={Vm.WorkingDirectory}");
        Check("S7 …and the status bar says another chat is working", Vm.HasRunningElsewhere, Vm.RunningElsewhereText);
        bool filterOn = !Vm.ShowAllProjectSessions;
        var running = Vm.Sessions.FirstOrDefault(s => s.Title == "s7-bg");
        Check("S8 the sidebar keeps a working chat of ANOTHER project in view, at the top, with the project filter on",
            filterOn && running != null && Vm.VisibleSessions.Count > 0 && ReferenceEquals(Vm.VisibleSessions[0], running),
            $"filterOn={filterOn} first={Vm.VisibleSessions.FirstOrDefault()?.Title}");
        gate.Release();
        var outcome = await Task.WhenAny(run, Task.Delay(20000)) == run ? await run : new McpTaskOutcome("(timed out)", true);
        await Render();
        var bg = Vm.Sessions.FirstOrDefault(s => s.Title == "s7-bg");
        var stored = bg == null ? null : Persist.LoadSession(bg.Id);
        Check("S7 the background task finishes for its caller and its chat keeps the answer and its own project",
            !outcome.IsError && outcome.Text.Contains("background") && stored != null && SamePath(stored.ProjectPath, ProjB)
            && stored.Messages.Any(m => m.Content.Contains("done in the background")),
            $"{outcome.Text} / {stored?.ProjectPath}");
        Check("S7 afterwards the screen is still the user's chat and project", ReferenceEquals(Vm.CurrentSession, mine) && SamePath(Vm.WorkingDirectory, ProjA));
        Vm.UserInput = "";
        Fake.Script.Clear();
        Check("S7 a pipe chat is titled by its task alone (no repeated \"[ObsidianX]\")", bg != null && bg.Title == "s7-bg", bg?.Title);

        // S9 — old pipe titles lose the prefix when the database opens; a title that is only the prefix stays.
        var old = new ChatSession { Title = "[ObsidianX] rx-phase6-license" };
        old.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "x" });
        var bare = new ChatSession { Title = "[ObsidianX]" };
        bare.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "x" });
        var mine2 = new ChatSession { Title = "my [ObsidianX] notes" };
        mine2.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "x" });
        Persist.SaveSession(old); Persist.SaveSession(bare); Persist.SaveSession(mine2);
        _ = new DatabaseService(Sp.GetRequiredService<SettingsService>());   // opening the database runs the clean-up
        Check("S9 '[ObsidianX] task' → 'task'; a bare '[ObsidianX]' and a title with it in the middle are left alone",
            Persist.LoadSession(old.Id)?.Title == "rx-phase6-license" && Persist.LoadSession(bare.Id)?.Title == "[ObsidianX]"
            && Persist.LoadSession(mine2.Id)?.Title == "my [ObsidianX] notes",
            $"{Persist.LoadSession(old.Id)?.Title} / {Persist.LoadSession(bare.Id)?.Title} / {Persist.LoadSession(mine2.Id)?.Title}");

        // S10 — the Claude desktop app's CLI, in both folder layouts, newest version by number (merged FindCli).
        string root = Path.Combine(PipeRoot, "appdata", "Claude", "claude-code");
        if (Directory.Exists(Path.Combine(PipeRoot, "appdata"))) Directory.Delete(Path.Combine(PipeRoot, "appdata"), true);
        string Exe(params string[] parts) { var p = Path.Combine([root, .. parts, "claude.exe"]); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); return p; }
        Exe("2.1.99");
        string nested = Exe("2.1.299", "f0584be0fbaf");
        Directory.CreateDirectory(Path.Combine(root, "2.1.300"));   // a version folder without the exe (still unpacking)
        string? Find(string where) => CluadeX.Services.Providers.ClaudeDevProvider.FindCli(where, Path.Combine(PipeRoot, "appdata"),
            Path.Combine(PipeRoot, "nobody"), File.Exists, Directory.Exists, d => Directory.GetDirectories(d));
        Check(@"S10 the CLI is found one folder deeper (2.1.299\<hash>\claude.exe), and 2.1.299 beats 2.1.99; npm's shims are skipped",
            Find("C:\\npm\\claude\nC:\\npm\\claude.cmd") == nested, Find(""));
    }
}
