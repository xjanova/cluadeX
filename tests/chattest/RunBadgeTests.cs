using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>RB — the sidebar's run badges on real runs (owner 2026-11-08: "ไอค่อนที่แสดงการทำงานแบบอนิเมชั่น ถ้าอันไหนผ่านหมด
    /// แล้วก็สีเทา"): a background chat that finishes while another is on screen is an unread result, one that fails is
    /// red and stays red after a look, a Stop is "stopped", the live-runs ledger holds a chat only while it runs, and a run
    /// CluadeX never saw end (a crash) shows as stopped after the restart — never as still working. Her mood follows.</summary>
    static async Task TestRunBadges()
    {
        Log("\n## RB — run badges on real runs: unread result, failed, stopped, the restart ledger, the buddy's mood");
        string ledger = Path.Combine(Settings.DataRoot, "live-runs.json");
        string Ledger() => File.Exists(ledger) ? File.ReadAllText(ledger) : "";
        // Earlier sections can leave a chat running in the background: she is busy then, whatever this section does.
        var runsField = typeof(CluadeX.ViewModels.ChatViewModel).GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int OtherRuns() => ((System.Collections.ICollection)runsField.GetValue(Vm)!).Count;
        bool Mood(string calm) => OtherRuns() > 0 ? Vm.BuddyMoodState is BuddyMood.Working or BuddyMood.Waiting : Vm.BuddyMoodState == calm;

        // RB1 — a chat that finishes in the background while another is on screen
        await FreshChat();
        var bg = Vm.CurrentSession!;
        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor["[RB1]"] = new Queue<FakeStep>([FakeStep.Gated(["RB1 working"], gate, [" — RB1 done."])]);
        await Send("[RB1] slow job");
        bool started = await WaitUntil(() => bg.RunStatus == RunBadge.Working && Vm.Messages.Any(m => m.Content.StartsWith("RB1 working")));
        Check("RB1a a running chat's badge is working", started, bg.RunStatus);
        Check("RB1b the ledger holds it while it runs", Ledger().Contains(bg.Id), Ledger());
        Check("RB1c the buddy works too", Vm.BuddyMoodState == BuddyMood.Working, Vm.BuddyMoodState);
        await FreshChat();   // another chat on screen; RB1 carries on in the background
        gate.Release();
        bool done = await WaitUntil(() => bg.RunStatus == RunBadge.Done, 15000);
        Check("RB1d it finishes in the background: an unread result", done, bg.RunStatus);
        Check("RB1e …struck off the ledger", !Ledger().Contains(bg.Id), Ledger());
        Check("RB1f …and the buddy is pleased (busy instead while another chat still works)", Mood(BuddyMood.Happy), $"{Vm.BuddyMoodState} · {OtherRuns()} other runs");
        await Open(bg.Id);
        await Render();
        Check("RB1g opening it reads the result: grey idle", bg.RunStatus == RunBadge.Idle, bg.RunStatus);

        // RB2 — a background chat whose turn fails
        await FreshChat();
        var bad = Vm.CurrentSession!;
        Fake.ScriptFor["[RB2]"] = new Queue<FakeStep>([FakeStep.Throw(new HttpRequestException("invalid request", null, HttpStatusCode.BadRequest))]);
        await Send("[RB2] will fail");
        bool failed = await WaitUntil(() => bad.RunStatus == RunBadge.Failed, 15000);
        Check("RB2a a turn that ends on an error is red", failed, $"{bad.RunStatus} · {string.Join(" | ", Vm.Messages.Select(m => m.Role + ":" + m.HasError))}");
        Check("RB2b the buddy is sad for a moment (busy instead while another chat still works)", Mood(BuddyMood.Sad), $"{Vm.BuddyMoodState} · {OtherRuns()} other runs");
        await FreshChat();
        await Open(bad.Id);
        await Render();
        Check("RB2c it stays red after a look (until the next turn)", bad.RunStatus == RunBadge.Failed, bad.RunStatus);
        Fake.ScriptFor["[RB2-again]"] = new Queue<FakeStep>([FakeStep.Reply("fine now")]);
        await Send("[RB2-again] retry");
        await WaitIdle();
        Check("RB2d the next good turn clears it", bad.RunStatus == RunBadge.Idle, bad.RunStatus);

        // RB3 — the owner's Stop
        await FreshChat();
        var stop = Vm.CurrentSession!;
        Fake.ScriptFor["[RB3]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        await Send("[RB3] long one");
        await WaitUntil(() => stop.RunStatus == RunBadge.Working);
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle(5000);
        Check("RB3 a stopped turn shows stopped, not done", stop.RunStatus == RunBadge.Stopped, stop.RunStatus);
        Check("RB3b …and is off the ledger", !Ledger().Contains(stop.Id), Ledger());

        // RB4 — the restart: a run whose CluadeX died is stopped, a run of a CluadeX still alive is left alone
        await FreshChat();
        var crashed = Vm.CurrentSession!;
        await FreshChat();
        crashed.RunStatus = "";
        File.WriteAllText(ledger, $"{{\"{crashed.Id}\":999999}}");
        var liveRuns = typeof(CluadeX.ViewModels.ChatViewModel).GetField("_liveRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm) as LiveRunLedger;
        if (liveRuns != null) liveRuns.IsAlive = pid => false;
        typeof(CluadeX.ViewModels.ChatViewModel).GetMethod("MarkOrphanRuns", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        await Render();
        Check("RB4a after a crash the chat that was running shows stopped — not working, not done", crashed.RunStatus == RunBadge.Stopped, crashed.RunStatus);
        Check("RB4b it is told once (taken off the ledger)", !Ledger().Contains(crashed.Id), Ledger());
        if (liveRuns != null) liveRuns.IsAlive = pid => pid == Environment.ProcessId;

        // RB5 — her clock: nothing for a minute → asleep; typing wakes her
        typeof(CluadeX.ViewModels.ChatViewModel).GetField("_lastActivityUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(Vm, DateTime.UtcNow - BuddyMood.SleepAfter - TimeSpan.FromSeconds(1));
        typeof(CluadeX.ViewModels.ChatViewModel).GetMethod("RefreshBuddy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        Check("RB5a nothing ran for a minute: the buddy sleeps (never while a chat works)", Mood(BuddyMood.Sleep), $"{Vm.BuddyMoodState} · {OtherRuns()} other runs");
        Vm.UserInput = "hello?";
        Check("RB5b typing wakes her", Mood(BuddyMood.Idle), $"{Vm.BuddyMoodState} · {OtherRuns()} other runs");
        Vm.UserInput = "";

        // RB6 — clicking her goes Home (owner 2026-11-08: "กดที่ไอค่อนแมว หรือ หน้า น้อง cluadex ควรเป็นการกลับไปหน้า home ตอนรอเปิด
        // โปรเจค"): the Chat page, an empty chat, no folder open — and the chat that was running keeps running.
        await FreshChat();
        Vm.SetWorkingDirectory(ProjA);
        var busy = Vm.CurrentSession!;
        var gate6 = new SemaphoreSlim(0);
        Fake.ScriptFor["[RB6]"] = new Queue<FakeStep>([FakeStep.Gated(["RB6 working"], gate6, [" — RB6 done."])]);
        await Send("[RB6] long job, then go home");
        await WaitUntil(() => busy.RunStatus == RunBadge.Working && Vm.Messages.Any(m => m.Content.StartsWith("RB6 working")));
        int shown = 0;
        void OnShow() => shown++;
        Vm.NavigateToChatRequested += OnShow;
        try
        {
            Vm.GoHomeCommand.Execute(null);
            await Render();
            var home = Vm.CurrentSession;
            Check("RB6a Home: a new empty chat on screen", home != null && !ReferenceEquals(home, busy) && Vm.Messages.Count == 0,
                $"{home?.Title} · {Vm.Messages.Count} rows");
            Check("RB6b …with no folder open (the page that asks which project)", string.IsNullOrEmpty(Vm.WorkingDirectory), Vm.WorkingDirectory);
            Check("RB6c …and the Chat page is brought forward", shown == 1, $"{shown}");
            Check("RB6d the chat that was running is still running", busy.RunStatus == RunBadge.Working, busy.RunStatus);
            Check("RB6e …and still in the sidebar", Vm.Sessions.Any(s => s.Id == busy.Id), busy.Id);
            Vm.GoHomeCommand.Execute(null);
            await Render();
            Check("RB6f clicking her again stacks no second empty chat", ReferenceEquals(Vm.CurrentSession, home)
                && Vm.Sessions.Count(s => s.Id == home!.Id) == 1, Vm.CurrentSession?.Id);
            Check("RB6g the tooltip says what a click does", Vm.BuddyTip.Contains(Vm.BuddyMoodTip) && Vm.BuddyTip.Contains("Home"), Vm.BuddyTip);
            gate6.Release();
            bool finished = await WaitUntil(() => busy.RunStatus == RunBadge.Done, 15000);
            Check("RB6h it finishes in the background: an unread result", finished, busy.RunStatus);
            await Open(busy.Id);
            await Render();
            Check("RB6i opening it shows the whole reply — nothing lost by going Home",
                Vm.Messages.Any(m => m.Content.Contains("RB6 working") && m.Content.Contains("RB6 done")),
                string.Join(" | ", Vm.Messages.Select(m => m.Role + ":" + m.Content)));
        }
        finally { Vm.NavigateToChatRequested -= OnShow; gate6.Release(); Vm.SetWorkingDirectory(""); }
    }
}
