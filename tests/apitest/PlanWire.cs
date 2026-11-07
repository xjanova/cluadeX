// The plan card and the next-step box the way Claude Code's desktop app keeps them (owner 2026-11-08: "การวางแผนงาน
// ของคุณก่อนเริ่มแชททำงานอย่างไรดีจัง ทำให้ cluadex ทำได้ไหม"): a whole plan replaces the list, advance / done + start move
// it, exactly one step is in progress while work is left, the scope tag, wrap_up checking off the plan its run worked on,
// the plan a chat's next turn carries on from, a button of the box sending its label once, and the card saved with the
// chat and read back with its running step shown as stopped. No model is called.
using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class PlanWire
{
    static List<TodoItem> Plan(params string[] statuses)
        => statuses.Select((s, i) => new TodoItem { Content = "step " + (i + 1), Status = s }).ToList();

    static string Shape(List<TodoItem> items) => string.Join(",", items.Select(t => t.Status switch
    {
        "completed" => "c", "in_progress" => "ip", _ => "p",
    }));

    static int InProgress(List<TodoItem> items) => items.Count(t => t.Status == "in_progress");

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · plan card (update_plan: replace, advance, out of order, one in progress)");
        Ops(check);
        Invariant(check);
        Scope(check);
        WrapUp(check);
        section("offline · plan carried to the next turn, prompt guidance");
        Carry(check);
        section("offline · next-step box buttons");
        Buttons(check);
        section("offline · plan card saved with the chat (round trip, stopped step)");
        Records(check);
        RoundTrip(check);
        return Task.CompletedTask;
    }

    static void Ops(Action<string, string, bool, string> check)
    {
        const string S = "plan";
        var p = Plan("in_progress", "pending", "pending");
        check(S, "PL1 advance moves on", PlanOps.Apply(p, true, Array.Empty<int>(), null) == null && Shape(p) == "c,ip,p", Shape(p));

        p = Plan("completed", "completed", "in_progress");
        PlanOps.Apply(p, true, Array.Empty<int>(), null);
        string once = Shape(p);
        string? again = PlanOps.Apply(p, true, Array.Empty<int>(), null);
        check(S, "PL2 double advance at the end", once == "c,c,c" && again == null && Shape(p) == "c,c,c" && InProgress(p) == 0,
            once + " then " + Shape(p) + " " + again);

        p = Plan("in_progress", "pending", "pending");
        PlanOps.Apply(p, false, new[] { 3 }, 2);
        check(S, "PL3 out of order: done 3, start 2", Shape(p) == "p,ip,c", Shape(p));

        var empty = new List<TodoItem>();
        string? err = PlanOps.Apply(empty, true, Array.Empty<int>(), null);
        check(S, "PL4 empty plan: advance refused", err != null && empty.Count == 0, err ?? "(no error)");

        p = Plan("in_progress", "pending");
        err = PlanOps.Apply(p, false, Array.Empty<int>(), 9);
        check(S, "PL5 no step 9: nothing changed", err != null && Shape(p) == "ip,p", err ?? "(no error)");

        p = Plan("in_progress", "in_progress", "pending", "in_progress");
        string note = PlanOps.Normalize(p);
        check(S, "PL6 replace with three in progress", Shape(p) == "ip,p,p,p" && note.Contains("kept step 1"), Shape(p) + " · " + note);

        p = Plan("completed", "pending", "pending");
        note = PlanOps.Normalize(p);
        check(S, "PL7 replace with none in progress", Shape(p) == "c,ip,p" && note.Contains("step 2 started"), Shape(p) + " · " + note);

        p = Plan("completed", "completed");
        note = PlanOps.Normalize(p);
        check(S, "PL8 all done: none in progress", Shape(p) == "c,c" && note == "", Shape(p) + " · " + note);

        // A changed plan (the user interrupted) replaces the list: the tool clears and adds, then normalizes.
        var live = Plan("completed", "in_progress", "pending");
        var fresh = new List<TodoItem> { new() { Content = "Do the new thing", Status = "pending" }, new() { Content = "Check it", Status = "pending" } };
        live.Clear(); live.AddRange(fresh); PlanOps.Normalize(live);
        check(S, "PL9 interrupt: new list replaces", live.Count == 2 && live[0].Content == "Do the new thing" && Shape(live) == "ip,p", Shape(live));
    }

    /// <summary>Random plans and random step operations: after each one at most one step is in progress, and a whole list
    /// with work left always has exactly one.</summary>
    static void Invariant(Action<string, string, bool, string> check)
    {
        var rnd = new Random(20261108);
        string[] st = { "pending", "in_progress", "completed" };
        int broken = 0; string first = "";
        for (int round = 0; round < 500; round++)
        {
            var p = Enumerable.Range(0, rnd.Next(1, 8)).Select(i => new TodoItem { Content = "s" + i, Status = st[rnd.Next(3)] }).ToList();
            PlanOps.Normalize(p);
            bool workLeft = p.Any(t => t.Status != "completed");
            if (InProgress(p) != (workLeft ? 1 : 0)) { broken++; first = first.Length > 0 ? first : "normalize " + Shape(p); }
            for (int op = 0; op < 6; op++)
            {
                switch (rnd.Next(3))
                {
                    case 0: PlanOps.Apply(p, true, Array.Empty<int>(), null); break;
                    case 1: PlanOps.Apply(p, false, Array.Empty<int>(), rnd.Next(1, p.Count + 1)); break;
                    default: PlanOps.Apply(p, rnd.Next(2) == 0, new[] { rnd.Next(1, p.Count + 1) }, null); break;
                }
                if (InProgress(p) > 1) { broken++; first = first.Length > 0 ? first : "op " + Shape(p); }
            }
        }
        check("plan", "PL10 one in progress (500 random plans)", broken == 0, broken == 0 ? "" : $"{broken} broken, first: {first}");
    }

    static void Scope(Action<string, string, bool, string> check)
    {
        bool ok = PlanOps.Scope("first_task") == PlanOps.FirstTask && PlanOps.Scope("Continuation") == PlanOps.Continuation
                  && PlanOps.Scope("new-task") == PlanOps.NewTask && PlanOps.Scope("new") == PlanOps.NewTask
                  && PlanOps.Scope("whatever") == null && PlanOps.Scope(null) == null;
        check("plan", "PL11 scope tags", ok, "");
    }

    static void WrapUp(Action<string, string, bool, string> check)
    {
        var run = new AgentRunScope("chat-a", null);
        run.Todos.AddRange(Plan("completed", "in_progress", "pending"));
        int untouched = PlanOps.FinishForWrapUp(run);
        run.PlanTouched = true;
        int n = PlanOps.FinishForWrapUp(run);
        int twice = PlanOps.FinishForWrapUp(run);
        check("plan", "PL12 wrap_up checks off its plan", untouched == 0 && n == 2 && twice == 0 && Shape(run.Todos) == "c,c,c",
            $"untouched {untouched}, then {n}, again {twice}: {Shape(run.Todos)}");

        var child = run.CreateChild();
        child.Todos.AddRange(Plan("in_progress"));
        child.PlanTouched = true;
        check("plan", "PL13 a sub-agent's wrap_up finishes nothing", PlanOps.FinishForWrapUp(child) == 0 && PlanOps.FinishForWrapUp(null) == 0, "");
    }

    static void Carry(Action<string, string, bool, string> check)
    {
        var items = new List<TodoItem>
        {
            new() { Content = "Read the client", Status = "completed" },
            new() { Content = "Fix the timeout", Status = "in_progress" },
            new() { Content = "Run the tests", Status = "pending" },
        };
        string note = PlanRecord.CarryNote(items);
        check("plan", "PL14 carry note names each step", note.Contains("1. Read the client (done)") && note.Contains("2. Fix the timeout (stopped mid-way)")
              && note.Contains("3. Run the tests (pending)") && note.Contains("new_task"), note);

        var before = AgentRunScope.Current;
        try
        {
            var run = new AgentRunScope("chat-b", null);
            run.Todos.AddRange(items);
            AgentRunScope.Enter(run);
            string none = CodeAgentService.WithCarriedPlan("hello");
            run.PlanCarried = true;
            string carried = CodeAgentService.WithCarriedPlan("hello");
            run.PlanTouched = true;
            string touched = CodeAgentService.WithCarriedPlan("hello");
            AgentRunScope.Enter(run.CreateChild());
            string child = CodeAgentService.WithCarriedPlan("hello");
            check("plan", "PL15 carried plan rides on the turn only", none == "hello" && carried.StartsWith("hello\n\n[CluadeX: your plan")
                  && touched == "hello" && child == "hello", carried);
        }
        finally { AgentRunScope.Enter(before); }

        var module = new OrchestrationToolModule();
        string withPlan = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "todo_write", "wrap_up" }, NativeTools = true }) ?? "";
        string without = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "wrap_up" }, NativeTools = true }) ?? "";
        check("plan", "PL16 prompt: plan first, with scope", withPlan.Contains("FIRST tool call is todo_write") && withPlan.Contains("first_task")
              && withPlan.Contains("checks off") && !without.Contains("PLAN:"), withPlan.Length + " chars");

        string local = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "todo_write", "wrap_up" }, NativeTools = true, IsLocalModel = true }) ?? "";
        check("plan", "PL17L a local model gets the short plan rule",
            local.Contains(OrchestrationToolModule.PlanGuideShort) && !local.Contains(OrchestrationToolModule.PlanGuide)
            && OrchestrationToolModule.PlanGuideShort.Length * 3 < OrchestrationToolModule.PlanGuide.Length
            && OrchestrationToolModule.PlanGuideShort.Contains("advance") && OrchestrationToolModule.PlanGuideShort.Contains("new_task"),
            $"{OrchestrationToolModule.PlanGuideShort.Length} vs {OrchestrationToolModule.PlanGuide.Length} chars");

        // The step-3 reminder: no plan → write one; a carried plan this turn has not touched → carry it on or replace it.
        var fresh = new AgentRunScope("chat-r", null);
        var carriedRun = new AgentRunScope("chat-r", null) { PlanCarried = true };
        carriedRun.Todos.AddRange(items);
        var workedRun = new AgentRunScope("chat-r", null) { PlanCarried = true, PlanTouched = true };
        var sub = carriedRun.CreateChild();
        sub.PlanCarried = true;
        string? noPlan = PlanOps.Reminder(fresh, 0, thai: false);
        string? carriedNudge = PlanOps.Reminder(carriedRun, 3, thai: true);
        check("plan", "PL18L step-3 reminder: none, carried, worked on",
            noPlan != null && noPlan.Contains("no plan yet") && carriedNudge != null && carriedNudge.Contains("แผนจากเทิร์นก่อน")
            && PlanOps.Reminder(workedRun, 3, thai: false) == null && PlanOps.Reminder(fresh, 2, thai: false) == null
            && PlanOps.Reminder(sub, 1, thai: false) == null,
            $"{noPlan} · {carriedNudge}");
    }

    static void Buttons(Action<string, string, bool, string> check)
    {
        var step = NextStep.FromArgs("Fixed the timeout; the build passes. Not run live.", "Run the live test now?", "[\"Yes, run it\",\"Not now\"]", out var err);
        var sent = new List<string>();
        var box = new NextStepView(step!, null, (_, label) => sent.Add(label));
        box.Actions[0].Command.Execute(null);
        box.Actions[0].Command.Execute(null);   // a double click
        box.Actions[1].Command.Execute(null);   // the other button after it
        check("plan", "PL17 a button sends its label once", err == null && sent.Count == 1 && sent[0] == "Yes, run it"
              && box.IsAnswered && !box.HasActions && box.Actions[0].IsPrimary, string.Join("|", sent));

        var back = NextStep.FromJson(step!.ToJson());
        var reopened = new NextStepView(back!, "Yes, run it", (_, l) => sent.Add(l));
        reopened.Actions[1].Command.Execute(null);
        check("plan", "PL18 a reopened answered box sends nothing", back!.Ask == step.Ask && back.Actions.SequenceEqual(step.Actions)
              && sent.Count == 1 && !reopened.HasActions, "");

        NextStep.FromArgs("Done.", "", "[\"Yes\"]", out var noAsk);
        NextStep.FromArgs("Done.", "Which?", "[\"A\",\"B\",\"C\"]", out var three);
        NextStep.FromArgs("", "", "", out var noRecap);
        check("plan", "PL19 box rules (ask, 2 buttons, recap)", noAsk != null && three != null && noRecap != null, $"{noAsk} · {three} · {noRecap}");
    }

    static void Records(Action<string, string, bool, string> check)
    {
        var rows = new List<PlanItemView>
        {
            new(1, "Read the client", "done"),
            new(2, "Fix the timeout", "active"),
            new(3, "Run the tests", "pending"),
        };
        var rec = PlanRecord.FromCard(rows, PlanOps.Continuation);
        var back = PlanRecord.FromJson(rec.ToJson());
        var read = back!.ToRows(live: false);
        check("plan", "PL20 record round trip", back.Scope == PlanOps.Continuation && back.Stopped && back.Items.Count == 3
              && read[1].IsStopped && !read[1].IsWorking && read[0].IsDone && read[2].IsPending && back.HasWorkLeft,
            rec.ToJson());

        Func<string, string> loc = k => k switch
        {
            "chat.plan.done" => "Done ({0}/{1})", "plan.card.stopped" => "Stopped ({0}/{1})", _ => "Run ended ({0}/{1} ticked)",
        };
        string stopped = PlanRecord.EndedHeader(read, stopped: true, loc);
        string ended = PlanRecord.EndedHeader(read, stopped: false, loc);
        string done = PlanRecord.EndedHeader(new List<PlanItemView> { new(1, "a", "done") }, stopped: true, loc);
        check("plan", "PL21 ended headers", stopped == "Stopped (1/3)" && ended == "Run ended (1/3 ticked)" && done == "Done (1/1)",
            $"{stopped} · {ended} · {done}");

        check("plan", "PL22 junk is not a plan", PlanRecord.FromJson("{}") == null && PlanRecord.FromJson("not json") == null
              && PlanRecord.FromJson("{\"items\":[]}") == null && PlanRecord.FromJson(null) == null, "");
    }

    /// <summary>The real store: a chat with a plan card still running and an answered next-step box, saved and read back.</summary>
    static void RoundTrip(Action<string, string, bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cx-planwire-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new DatabaseService(Path.Combine(dir, "chats.db"));
            var session = new ChatSession { Title = "plan round trip" };
            session.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "fix the timeout" });
            var card = new ChatMessage
            {
                Role = MessageRole.System, Content = "📋 plan",
                PlanItems = new List<PlanItemView> { new(1, "Read the client", "done"), new(2, "Fix the timeout", "active"), new(3, "Run the tests", "pending") },
                PlanHeader = "Working on it (1/3)", PlanElapsed = "42s", PlanScope = PlanOps.FirstTask,
            };
            session.Messages.Add(card);
            var step = NextStep.FromArgs("Fixed it.", "Run the tests?", "[\"Yes\",\"No\"]", out _)!;
            session.Messages.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.NextStepTag, ToolOutput = step.ToJson(),
                                                   ToolSummary = "Yes", Content = "⏭ Fixed it." });
            db.SaveSession(session);

            var loaded = db.LoadSession(session.Id);
            var plan = loaded?.Messages.FirstOrDefault(m => m.IsPlan);
            var rows = plan?.PlanItems;
            check("plan", "PL23 plan card read back", plan != null && rows!.Count == 3 && plan.PlanScope == PlanOps.FirstTask
                  && rows[1].IsStopped && !rows.Any(r => r.IsWorking) && plan.PlanStopped && plan.PlanElapsed == "42s"
                  && plan.PlanHeader.Contains("1/3") && !plan.PlanHeader.Contains("Working"),
                plan == null ? "no plan row" : $"{plan.PlanHeader} · {plan.PlanScope} · {string.Join(",", rows!.Select(r => r.State + (r.IsLive ? "+live" : "")))}");

            var box = loaded?.Messages.FirstOrDefault(m => m.IsNextStep);
            check("plan", "PL24 answered box read back", box != null && box.ToolSummary == "Yes" && NextStep.FromJson(box.ToolOutput)?.Ask == "Run the tests?", "");

            // Saved again after it was read back: nothing is lost or doubled.
            db.SaveSession(loaded!);
            var twice = db.LoadSession(session.Id);
            check("plan", "PL25 second save keeps one card", twice!.Messages.Count(m => m.IsPlan) == 1
                  && twice.Messages.First(m => m.IsPlan).PlanItems!.Select(r => r.State).SequenceEqual(new[] { "done", "active", "pending" }), "");
        }
        catch (Exception ex) { check("plan", "PL23 plan card read back", false, ex.ToString()); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
