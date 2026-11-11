// Scheduled tasks (owner 2026-11-11 "งานตั้งเวลาอัตโนมัติ"): CluadeX opens a chat and sends a prompt at a time, while it
// is open. Checked here, offline: the schedule math, the store, the clock's rules (busy, missed, catch-up, the limit of
// runs at once, "once"), and the schedule_task tool — listing is free, anything else asks every time.
using System.IO;
using CluadeX.Models;
using CluadeX.Services.Routines;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class RoutinesWire
{
    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · scheduled tasks (Routines)");
        Schedule(check);
        Store(check);
        await Clock(check);
        await Tool(check);
    }

    static Routine Daily(string time, int days) => new() { Prompt = "p", Kind = RoutineSchedule.Daily, Time = time, Days = days };

    static void Schedule(Action<string, string, bool, string> check)
    {
        var wed = new DateTime(2026, 11, 11, 10, 0, 0);   // a Wednesday
        check("routine", "daily 02:00 after 10:00 → tomorrow 02:00",
            RoutineSchedule.Next(Daily("02:00", RoutineSchedule.EveryDay), wed) == new DateTime(2026, 11, 12, 2, 0, 0), "");
        check("routine", "daily 10:30 after 10:00 → today",
            RoutineSchedule.Next(Daily("10:30", RoutineSchedule.EveryDay), wed) == new DateTime(2026, 11, 11, 10, 30, 0), "");
        var sat = new DateTime(2026, 11, 14, 12, 0, 0);
        var wd = RoutineSchedule.Next(Daily("09:00", RoutineSchedule.Weekdays), sat);
        check("routine", "weekdays 09:00 on a Saturday → Monday 09:00", wd == new DateTime(2026, 11, 16, 9, 0, 0), wd?.ToString("ddd yyyy-MM-dd HH:mm") ?? "null");
        check("routine", "no day picked → never", RoutineSchedule.Next(Daily("09:00", 0), wed) == null, "");

        var t0 = new DateTime(2026, 11, 11, 8, 0, 0);
        var every = new Routine { Prompt = "p", Kind = RoutineSchedule.Interval, IntervalMinutes = 60, CreatedAt = t0 };
        check("routine", "every 60 min from 08:00, asked at 10:30 → 11:00", RoutineSchedule.Next(every, t0.AddMinutes(150)) == t0.AddHours(3), "");
        every.LastRunAt = t0.AddMinutes(155);
        check("routine", "…and it counts from the last run", RoutineSchedule.Next(every, t0.AddMinutes(156)) == t0.AddMinutes(215), "");
        var once = new Routine { Prompt = "p", Kind = RoutineSchedule.Once, OnceAt = wed.AddDays(1) };
        check("routine", "once in the future → that time; once it ran → never",
            RoutineSchedule.Next(once, wed) == wed.AddDays(1) && RoutineSchedule.Next(new Routine { Kind = RoutineSchedule.Once, OnceAt = wed.AddDays(1), LastRunAt = wed }, wed) == null
            && RoutineSchedule.Next(once, wed.AddDays(2)) == null, "");

        string? P(string s, out Routine r) { r = new Routine { Prompt = "p" }; return RoutineSchedule.Parse(s, r); }
        check("routine", "parse \"every 2h\" / \"every 30m\"",
            P("every 2h", out var a) == null && a.Kind == RoutineSchedule.Interval && a.IntervalMinutes == 120
            && P("Every 30 minutes", out var b) == null && b.IntervalMinutes == 30, "");
        check("routine", "parse \"weekdays 09:00\", \"weekend 10:00\", \"daily 2:05\"",
            P("weekdays 09:00", out var c) == null && c.Days == RoutineSchedule.Weekdays && c.Time == "09:00"
            && P("weekend 10:00", out var d) == null && d.Days == RoutineSchedule.Weekend
            && P("daily 2:05", out var e) == null && e.Days == RoutineSchedule.EveryDay && e.Time == "02:05", "");
        check("routine", "parse \"mon,fri 8:30\"",
            P("mon,fri 8:30", out var f) == null && f.Kind == RoutineSchedule.Daily && f.Days == ((1 << 1) | (1 << 5)) && f.Time == "08:30", f.Days.ToString());
        check("routine", "parse \"once 2026-11-12 14:00\"",
            P("once 2026-11-12 14:00", out var g) == null && g.Kind == RoutineSchedule.Once && g.OnceAt == new DateTime(2026, 11, 12, 14, 0, 0), "");
        check("routine", "what cannot be read says how to write it",
            new[] { "", "sometimes", "daily 25:00", "funday 09:00", "once 2026-13-40 10:00" }.All(s => P(s, out _) == "routine.err.schedule"), "");

        check("routine", "a routine made by a chat runs at most hourly; one you make, every 15 min",
            RoutineSchedule.Problem(new Routine { Prompt = "p", Kind = RoutineSchedule.Interval, IntervalMinutes = 30 }, byAi: true) == "routine.err.intervalAi"
            && RoutineSchedule.Problem(new Routine { Prompt = "p", Kind = RoutineSchedule.Interval, IntervalMinutes = 30 }) == null
            && RoutineSchedule.Problem(new Routine { Prompt = "p", Kind = RoutineSchedule.Interval, IntervalMinutes = 10 }) == "routine.err.interval", "");
        check("routine", "no prompt, bad time, no day, no once time → the reason",
            RoutineSchedule.Problem(new Routine { Prompt = " " }) == "routine.err.prompt"
            && RoutineSchedule.Problem(Daily("9", RoutineSchedule.EveryDay)) == "routine.err.time"
            && RoutineSchedule.Problem(Daily("09:00", 0)) == "routine.err.days"
            && RoutineSchedule.Problem(new Routine { Prompt = "p", Kind = RoutineSchedule.Once }) == "routine.err.once", "");
        string msg = ChatViewModel.RoutineMessage(new Routine { Name = "Nightly tests", Prompt = "run the tests\n" }, wed, missed: true);
        check("routine", "the run's message names the task, says it may be unattended, and ends with the prompt",
            msg.Contains("Nightly tests") && msg.Contains("making up") && msg.Contains("summary") && msg.EndsWith("run the tests"), msg);
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // This machine is Thai: its culture writes the Buddhist year. What the model reads keeps 2026.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            string th = ChatViewModel.RoutineMessage(new Routine { Name = "n", Prompt = "p" }, wed, missed: false);
            var o2 = new Routine { Kind = RoutineSchedule.Once, OnceAt = wed };
            check("routine", "under a Thai culture the model still reads 2026-11-11, and \"once\" reads in the culture asked for",
                th.Contains("2026-11-11 10:00") && !th.Contains("2569")
                && RoutineSchedule.Describe(o2, k => k == "routine.describe.once" ? "{0}" : k) == "11 Nov 10:00"
                && RoutineSchedule.Describe(o2, k => k == "routine.describe.once" ? "{0}" : k, RoutineSchedule.Culture("th")).Contains("พ.ย."), th);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = was; }
    }

    static void Store(Action<string, string, bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cx-routines-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var svc = new RoutineService(dir) { Now = () => new DateTime(2026, 11, 11, 10, 0, 0) };
            var r = Daily("02:00", RoutineSchedule.EveryDay);
            r.Prompt = "  check the open PRs\nand say which need me  ";
            check("routine", "Put saves; an empty name is the prompt's first line; the next time is set",
                svc.Put(r) == null && svc.Get(r.Id) is { } got && got.Name == "check the open PRs" && got.NextRunAt == new DateTime(2026, 11, 12, 2, 0, 0)
                && File.Exists(Path.Combine(dir, "routines.json")), "");
            check("routine", "Put refuses what cannot be scheduled (and keeps nothing)",
                svc.Put(new Routine { Prompt = "" }) == "routine.err.prompt" && svc.All().Count == 1, "");
            var again = new RoutineService(dir);
            check("routine", "a new start reads them back", again.Get(r.Id)?.Prompt == "check the open PRs\nand say which need me", "");
            File.WriteAllText(Path.Combine(dir, "routines.json"), File.ReadAllText(Path.Combine(dir, "routines.json")).Replace("\"LastStatus\": \"\"", "\"LastStatus\": \"running\""));
            var crashed = new RoutineService(dir);
            check("routine", "a run that was going when CluadeX closed reads as failed", crashed.Get(r.Id)?.LastStatus == "failed", crashed.Get(r.Id)?.LastStatus ?? "");
            check("routine", "Find by id or by name (any case); Remove; SetEnabled clears the next time",
                svc.Find("CHECK THE OPEN PRS")?.Id == r.Id && svc.SetEnabled(r.Id, false) && svc.Get(r.Id)!.NextRunAt == null
                && svc.Remove(r.Id) && svc.All().Count == 0, "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static async Task<bool> Until(Func<bool> ok)
    {
        for (int i = 0; i < 100 && !ok(); i++) await Task.Delay(20);
        return ok();
    }

    static async Task Clock(Action<string, string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0);
        var svc = new RoutineService(null) { Now = () => now };
        var gates = new Dictionary<string, TaskCompletionSource<bool>>();
        var told = new List<Routine>();
        svc.Runner = r =>
        {
            var tcs = new TaskCompletionSource<bool>();
            lock (gates) { gates[r.Id] = tcs; told.Add(r); }
            return tcs.Task.ContinueWith(t => (t.Result, t.Result ? "done: 3 PRs" : "broke", (string?)"chat-" + r.Id));
        };
        void Finish(string id, bool ok) { lock (gates) gates[id].SetResult(ok); }

        var a = Daily("10:05", RoutineSchedule.EveryDay);
        svc.Put(a);
        check("routine", "nothing is due before its time", svc.Tick().Count == 0, "");
        now = now.AddMinutes(6);
        var started = svc.Tick();
        check("routine", "at its time it starts once: running, the next time is tomorrow",
            started.SequenceEqual(new[] { a.Id }) && svc.IsRunning(a.Id) && svc.Get(a.Id)!.LastStatus == "running"
            && svc.Get(a.Id)!.NextRunAt == new DateTime(2026, 11, 12, 10, 5, 0) && svc.Tick().Count == 0, "");
        now = new DateTime(2026, 11, 12, 10, 6, 0);
        check("routine", "a time that comes while the last run still works is skipped (busy)",
            svc.Tick().Count == 0 && svc.Get(a.Id)!.LastNote == "busy" && svc.Get(a.Id)!.NextRunAt == new DateTime(2026, 11, 13, 10, 5, 0), "");
        Finish(a.Id, true);
        check("routine", "when the run ends: ran, its note, its chat",
            await Until(() => !svc.IsRunning(a.Id)) && svc.Get(a.Id) is { LastStatus: "ran", LastNote: "done: 3 PRs" } done && done.ChatId == "chat-" + a.Id, "");

        // Missed while closed: catch-up runs it once; without catch-up it is skipped.
        var b = Daily("09:00", RoutineSchedule.EveryDay); b.CatchUp = false;
        var c = Daily("09:00", RoutineSchedule.EveryDay);
        var e = Daily("09:00", RoutineSchedule.EveryDay);
        svc.Put(b); svc.Put(c); svc.Put(e);
        now = new DateTime(2026, 11, 13, 11, 0, 0);   // two hours past 09:00 (and a's 10:05)
        var late = svc.Tick();
        check("routine", "missed with catch-up off → skipped, next time set; on → runs once, marked as a make-up",
            !late.Contains(b.Id) && svc.Get(b.Id)!.LastStatus == "missed" && svc.Get(b.Id)!.NextRunAt == new DateTime(2026, 11, 14, 9, 0, 0)
            && late.Contains(c.Id) && svc.Get(c.Id)!.LastNote == "missed-run" && told.Last(t => t.Id == c.Id).LastNote == "missed-run", string.Join(",", late));
        check("routine", $"at most {RoutineService.MaxRunning} run at once; the rest wait, still due",
            late.SequenceEqual(new[] { c.Id, e.Id }) && !svc.IsRunning(a.Id) && svc.Get(a.Id)!.NextRunAt == new DateTime(2026, 11, 13, 10, 5, 0)
            && svc.Tick().Count == 0,
            string.Join(" | ", svc.All().Select(r => $"{(r.Id == a.Id ? "a" : r.Id == b.Id ? "b" : r.Id == c.Id ? "c" : r.Id == e.Id ? "e" : r.Id)}:{r.LastStatus}/{r.LastNote}/{r.NextRunAt:dd HH:mm}/run={svc.IsRunning(r.Id)}")));
        foreach (var id in late) Finish(id, false);
        check("routine", "a failed run reads as failed", await Until(() => late.All(id => !svc.IsRunning(id))) && svc.Get(c.Id)!.LastStatus == "failed", "");
        check("routine", "…and the one that waited starts at the next check", svc.Tick().SequenceEqual(new[] { a.Id }), "");
        Finish(a.Id, true);
        await Until(() => !svc.IsRunning(a.Id));

        var o = new Routine { Prompt = "once", Kind = RoutineSchedule.Once, OnceAt = now.AddMinutes(1) };
        svc.Put(o);
        now = now.AddMinutes(2);
        bool ran = svc.Tick().Contains(o.Id);
        check("routine", "\"once\" runs and then switches itself off", ran && svc.Get(o.Id) is { Enabled: false, NextRunAt: null }, "");
        Finish(o.Id, true);
        check("routine", "run now starts it whatever the time; not twice at once",
            await Until(() => !svc.IsRunning(o.Id)) && svc.RunNow(o.Id) && !svc.RunNow(o.Id), "");
        Finish(o.Id, true);
        await Until(() => !svc.IsRunning(o.Id));
        var noPage = new RoutineService(null) { Now = () => now };
        var d = Daily("09:00", RoutineSchedule.EveryDay); noPage.Put(d);
        check("routine", "no chat page (no runner) → nothing starts", !noPage.RunNow(d.Id), "");
    }

    static async Task Tool(Action<string, string, bool, string> check)
    {
        var module = new ProjectToolModule();
        var schema = module.GetToolSchemas().FirstOrDefault(s => s.Name == ProjectToolModule.ScheduleTaskToolName);
        check("routine", "schedule_task: a schema with action (list…run_now), schedule, prompt, project",
            schema != null && schema.InputSchema.GetProperty("properties") is var p && p.TryGetProperty("schedule", out _) && p.TryGetProperty("prompt", out _)
            && p.TryGetProperty("project", out _) && p.GetProperty("action").GetProperty("enum").GetArrayLength() == 6, schema?.InputSchema.ToString() ?? "null");
        check("routine", "schedule_task / routine / cron resolve to the tool; it is one of the CLI's tools",
            module.ResolveToolType("schedule_task") == ToolType.ScheduleTask && module.ResolveToolType("cron") == ToolType.ScheduleTask
            && module.ToolTypes.Contains(ToolType.ScheduleTask) && ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__schedule_task"), "");

        ToolCall Call(params (string, string)[] args)
        {
            var call = new ToolCall { Type = ToolType.ScheduleTask, ToolName = "schedule_task" };
            foreach (var (k, v) in args) call.Arguments[k] = v;
            return call;
        }
        var ctx = new ToolModuleContext { WorkingDirectory = @"D:\Proj" };
        var list = module.DescribePermission(Call(("action", "list")), ctx);
        var add = module.DescribePermission(Call(("action", "add"), ("name", "Nightly"), ("schedule", "daily 02:00"), ("prompt", "run the tests")), ctx);
        var remove = module.DescribePermission(Call(("action", "remove"), ("id", "abc")), ctx);
        check("routine", "listing is free; add / remove ask every time (even under an allow rule or with permissions off)",
            list.IsFree && !add.IsFree && add.AlwaysAsk && add.Scope == "execute" && !remove.IsFree && remove.AlwaysAsk, add.Detail ?? "");
        check("routine", "the card shows what will run", (add.Detail ?? "").Contains("run the tests") && (add.Detail ?? "").Contains("daily 02:00"), add.Detail ?? "");

        var before = ProjectToolModule.ScheduleTaskHandler;
        try
        {
            ProjectToolModule.ScheduleRequest? got = null;
            ProjectToolModule.ScheduleTaskHandler = req => { got = req; return Task.FromResult("Scheduled"); };
            var res = await module.ExecuteAsync(Call(("action", "ADD"), ("prompt", "x"), ("schedule", "every 2h"), ("same_chat", "true")), ctx, CancellationToken.None);
            check("routine", "add goes to the chat page with the open project as default and the flags read",
                res.Success && got is { Action: "add", Project: @"D:\Proj", SameChat: true, CatchUp: true, Schedule: "every 2h" }, got?.ToString() ?? "null");
            ProjectToolModule.ScheduleTaskHandler = _ => throw new InvalidOperationException("No scheduled task 'zz'");
            var bad = await module.ExecuteAsync(Call(("action", "pause"), ("id", "zz")), ctx, CancellationToken.None);
            check("routine", "a refusal from the page fails the call with its reason", !bad.Success && (bad.Error ?? "").Contains("zz"), bad.Error ?? "");
            ProjectToolModule.ScheduleTaskHandler = null;
            var none = await module.ExecuteAsync(Call(("action", "list")), ctx, CancellationToken.None);
            check("routine", "no chat page → it says so", !none.Success, "");
        }
        finally { ProjectToolModule.ScheduleTaskHandler = before; }
    }
}
