using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// MO — the plan and the agent map play their real state (owner 2026-11-03): the step at work turns and sweeps, a step
/// that finishes on screen pops once, a failed sub-agent reads as an error; every endless storyboard stops when its item
/// stops or leaves the screen; the rows change in place. Renders the agent map, the plan bar and the plan window in
/// every theme (CHATTEST_ONLY=MOTION writes them to out\renders when CHATTEST_RENDERS is set).
/// </summary>
internal static partial class Program
{
    static async Task TestPlanAgentMotion()
    {
        Log("\n## MO — the plan and the agent map animate by their real state");
        string renders = Environment.GetEnvironmentVariable("CHATTEST_RENDERS") is { Length: > 0 } r ? r : AppContext.BaseDirectory;
        Directory.CreateDirectory(renders);
        var themes = Sp.GetRequiredService<ThemeService>();
        LocSvc.SetLanguage("th");
        PlanWindow win = null;
        try
        {
            await FreshChat();
            await Task.Delay(300);
            int idle = EndlessRunning();
            Check("MO a chat with no plan and no sub-agents runs none of these loops", idle == 0, idle.ToString());

            // ── the agent map: three sub-agents start, one finishes, one fails, one keeps working ──
            var running = new AgentNode { Id = 1, Label = "Sub-agent #1", Kind = "explore", Task = "อ่านโครงสร้างโปรเจกต์และหาไฟล์ที่เกี่ยวกับการชน", Action = "อ่าน scripts/Ball.gd", ToolCalls = 7, Elapsed = "13s", Model = "claude-haiku-5-5" };
            var done = new AgentNode { Id = 2, Label = "Sub-agent #2", Kind = "general", Task = "แก้ค่าแรงเสียดทานใน main.gd", Action = "อ่าน main.gd", ToolCalls = 12, Elapsed = "21s", Model = "claude-sonnet-5-5" };
            var failed = new AgentNode { Id = 3, Label = "Sub-agent #3", Kind = "reviewer", Task = "ตรวจ diff แล้วรันเทสต์", Action = "เริ่ม…", ToolCalls = 2, Elapsed = "4s" };
            var map = new ChatMessage { Role = MessageRole.System, Content = "🧭 map", AgentNodes = new() };
            map.AgentMapHeader = LocF("agentmap.headerRunning", 3, 3, 0);
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยตรวจฟิสิกส์ลูกสนุกแล้วแก้ให้สมจริงขึ้น" });
            Vm.Messages.Add(map);
            map.AgentNodes.Add(running); map.AgentNodes.Add(done); map.AgentNodes.Add(failed);
            map.NotifyAgentMap();
            await Render(); await Task.Delay(250); await Render();
            int threeRunning = EndlessRunning();
            Check("MO a running sub-agent animates (ring, line, rim, foot: 4 endless clocks each)", threeRunning - idle >= 12, $"{idle} → {threeRunning}");

            done.State = "done"; done.Action = "แก้แล้ว: friction 0.96 · bounce 0.85";
            failed.State = "failed"; failed.Action = "Cannot create agent worktree: fatal: 'E:/proj-agent-3' already exists";
            map.AgentMapHeader = LocF("agentmap.headerRunning", 3, 1, 1);
            map.NotifyAgentMap();
            Check("MO a node that ends on screen plays its end once (JustDone / JustFailed)", done.JustDone && failed.JustFailed && !running.JustDone);
            await Render(); await Task.Delay(150);
            int oneRunning = EndlessRunning();
            Check("MO the ended nodes stop their endless clocks (only the working one and the root keep going)",
                oneRunning - idle >= 4 && oneRunning - idle <= 7, $"{idle} → {oneRunning}");
            var card = Container(map);
            var failText = card == null ? null : Find<TextBlock>(card).FirstOrDefault(t => t.Text == failed.Action);
            Check("MO a failed sub-agent's error reads in the danger colour, not the accent",
                failText != null && ReferenceEquals(failText.Foreground, View.FindResource("Status.Danger")), failText?.Foreground?.ToString() ?? "no text");
            var runText = card == null ? null : Find<TextBlock>(card).FirstOrDefault(t => t.Text == running.Action);
            Check("MO the working one's action stays in the accent", runText != null && ReferenceEquals(runText.Foreground, View.FindResource("Accent.1")));
            Check("MO the header's line counts the two that came back", Math.Abs(map.AgentMapProgress - 2.0 / 3) < 0.01 && map.AgentMapRunning);
            await Task.Delay(900);   // let the pop, the wash and the shake land before the pictures

            map.IsAgentMapOpen = false;
            await Render(); await Task.Delay(150);
            if (EndlessRunning() != idle) DumpEndless("folded map");
            Check("MO folding the map stops its endless clocks", EndlessRunning() == idle, $"{EndlessRunning()} vs idle {idle}");
            map.IsAgentMapOpen = true;
            await Render(); await Task.Delay(150);
            Check("MO unfolding it starts them again", EndlessRunning() - idle >= 4, $"{EndlessRunning()} vs idle {idle}");

            // ── the plan bar over the chat: two done, one at work, two waiting ──
            var session = Vm.CurrentSession!;
            var plans = (System.Collections.IDictionary)typeof(ChatViewModel).GetField("_lastPlans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            var todos = new List<TodoItem>
            {
                new() { Content = "ตรวจสอบโครงสร้างโปรเจกต์และไฟล์ที่เกี่ยวข้อง", Status = "completed" },
                new() { Content = "วิเคราะห์โค้ดฟิสิกส์ลูกสนุก", Status = "completed" },
                new() { Content = "แก้ไขค่าการชนและแรงเสียดทาน", Status = "in_progress" },
                new() { Content = "รันโปรเจกต์เพื่อทดสอบ", Status = "pending" },
                new() { Content = "สรุปผลการแก้ไข", Status = "pending" },
            };
            plans[session.Id] = todos;
            Vm.RefreshStickyPlan();
            Vm.IsStickyPlanExpanded = true;
            Vm.IsInlinePlanVisible = false;   // no plan card in this chat: the floating one stays past its intro
            await Render();
            var rows = Vm.StickyPlanItems;
            foreach (var row in rows) row.IsLive = true;   // as while its run works (no run in this harness)
            await Render(); await Task.Delay(200);
            var bar = Find<Border>(Win).FirstOrDefault(b => b.Name == "StickyPlanBar");
            var pendingText = bar == null ? null : Find<TextBlock>(bar).FirstOrDefault(t => t.Text == "รันโปรเจกต์เพื่อทดสอบ");
            Check("MO a waiting step's words are the secondary colour (colour follows state only)",
                pendingText != null && ReferenceEquals(pendingText.Foreground, View.FindResource("Text.Secondary")), pendingText?.Foreground?.ToString());

            foreach (var id in ThemeIds.All)
            {
                themes.Apply(id, persist: false);
                await Render(); await Task.Delay(120); await Render();
                SaveCrop(card, Path.Combine(renders, $"agent-map-{id}.png"));
                SaveCrop(bar, Path.Combine(renders, $"plan-bar-{id}.png"));
            }
            themes.Apply(ThemeIds.Midnight, persist: false);

            // A step finishes: the same rows, state set in place — the bar plays the change instead of redrawing.
            var third = rows[2];
            todos[2].Status = "completed"; todos[3].Status = "in_progress";
            Vm.RefreshStickyPlan();
            Check("MO the bar keeps its rows when a step finishes (no redraw)", ReferenceEquals(Vm.StickyPlanItems, rows) && ReferenceEquals(Vm.StickyPlanItems[2], third));
            Check("MO …and the finished step pops once (JustDone), the next one is at work", third.IsDone && third.JustDone && Vm.StickyPlanItems[3].IsActive);
            Check("MO the plan bar's count moves on", Vm.StickyPlanHeader.EndsWith("4/5"), Vm.StickyPlanHeader);
            foreach (var row in Vm.StickyPlanItems) row.IsLive = true;   // a refresh reads "no run" in this harness
            await Render();
            Vm.IsStickyPlanExpanded = false;
            await Render(); await Task.Delay(150);
            int folded = EndlessRunning();
            Vm.IsStickyPlanExpanded = true;
            await Render(); await Task.Delay(150);
            Check("MO the step at work turns and sweeps only while the plan is open", EndlessRunning() - folded == 2, $"{folded} folded → {EndlessRunning()} open");
            Vm.StickyPlanItems[3].IsLive = false;
            await Render(); await Task.Delay(150);
            Check("MO a step its run left \"in progress\" stops moving", EndlessRunning() == folded, $"{EndlessRunning()} vs {folded}");

            // ── the plan window: done / active / pending, and a queued item typed while the chat is idle ──
            todos[3].Status = "in_progress";
            Vm.OpenPlanWindow(session);
            win = Application.Current.Windows.OfType<PlanWindow>().FirstOrDefault();
            Check("MO the plan window opens", win != null);
            if (win == null) return;
            var pvm = (PlanWindowViewModel)win.DataContext;
            win.Left = -12000;
            await Task.Delay(300); win.UpdateLayout();
            pvm.NewItem = "อัปเดต README ให้ตรงกับค่าใหม่";
            pvm.AddItemCommand.Execute(null);
            await Task.Delay(200); win.UpdateLayout();
            var before = pvm.Rows.ToList();
            Check("MO the window shows done / active / pending / queued rows",
                pvm.Rows.Count == 6 && pvm.Rows[0].IsDone && pvm.Rows[3].IsActive && pvm.Rows[4].IsPending && pvm.Rows[5].IsQueued,
                string.Join(" | ", pvm.Rows.Select(x => x.State)));
            var queuedText = Find<TextBlock>((DependencyObject)win.Content).FirstOrDefault(t => t.Text == "อัปเดต README ให้ตรงกับค่าใหม่");
            Check("MO a queued row is not painted in an accent (its words keep the secondary colour, a dashed outline marks it)",
                queuedText != null && ReferenceEquals(queuedText.Foreground, win.FindResource("Text.Secondary")) && queuedText.FontStyle == FontStyles.Italic,
                queuedText?.Foreground?.ToString());
            foreach (var id in ThemeIds.All)
            {
                themes.Apply(id, persist: false);
                await Task.Delay(150); win.UpdateLayout();
                SaveWindow(win, Path.Combine(renders, $"plan-window-{id}.png"));
            }
            themes.Apply(ThemeIds.Midnight, persist: false);
            todos[3].Status = "completed"; todos[4].Status = "in_progress";
            pvm.Refresh();
            Check("MO the window changes its rows in place when a step finishes (it pops there too)",
                pvm.Rows.Count == before.Count && pvm.Rows.Select((x, i) => ReferenceEquals(x, before[i])).All(b => b) && pvm.Rows[3].JustDone);

            // ── end of the agent map: the last one finishes → nothing endless is left ──
            running.State = "done"; running.Action = "พบ 3 ไฟล์";
            map.NotifyAgentMap();
            await Render(); await Task.Delay(150);
            Vm.IsStickyPlanExpanded = false;
            win.Close(); win = null;
            await Render(); await Task.Delay(150);
            Check("MO once every sub-agent is back the map runs no endless clock", EndlessRunning() == idle, $"{EndlessRunning()} vs idle {idle}");
            Check("MO and its line is full", Math.Abs(map.AgentMapProgress - 1) < 0.001 && !map.AgentMapRunning && map.AgentMapHasFailures);

            // ── minimised: the state-driven loops pause with the window ──
            var again = new AgentNode { Id = 4, Label = "Sub-agent #4", Kind = "explore", Task = "x", Action = "y" };
            map.AgentNodes.Add(again);
            map.NotifyAgentMap();
            await Render(); await Task.Delay(150);
            int live = EndlessRunning();
            if (live - idle != 6) DumpEndless("node added on screen");
            // Its line, rim, ring and foot (4) and the root's halo and line (2) — each begun once: a trigger that entered
            // again used to leave the clock before it ticking until the GC.
            Check("MO a node added to a map on screen begins each loop once (6 clocks, no orphans)", live - idle == 6, $"{idle} → {live}");
            map.IsAgentMapOpen = false; await Render(); await Task.Delay(150);
            map.IsAgentMapOpen = true; await Render(); await Task.Delay(150);
            live = EndlessRunning();
            Check("MO folding and unfolding it again still leaves one clock per loop", live - idle == 6, $"{idle} → {live}");
            AmbientAnimation.SetMinimized(Win, true);
            await Task.Delay(150);   // a pause lands on the next frame
            int whileMin = EndlessRunning();
            if (whileMin != idle) DumpEndless("minimised");
            AmbientAnimation.SetMinimized(Win, false);
            await Task.Delay(150);
            int restored = EndlessRunning();
            Check("MO minimising pauses a running node's loops, restoring resumes them", live - idle >= 4 && whileMin == idle && restored == live,
                $"live {live} · minimised {whileMin} · restored {restored} · idle {idle}");
            again.State = "stopped";
            map.NotifyAgentMap();
        }
        finally
        {
            win?.Close();
            themes.Apply(ThemeIds.Midnight, persist: false);
            Vm.IsStickyPlanExpanded = false;
            LocSvc.SetLanguage("en");
        }
    }

    /// <summary>
    /// MF — the floating plan card (owner 2026-11-03: "เมื่อมีการสร้างแผนงาน ให้ลอย สักครู่แล้วหายไป และถ้าแชทสกรอจนแผนงาน
    /// ในแชทหลุดจากจอ การ์ดแผนงานลอยจะแสดงขึ้น"): the rule as a pure function, the view's measurement with its
    /// hysteresis, the intro timer (and the mouse holding it), progress not popping it back, chats keeping their own
    /// state. Renders the floating card and the chat's plan card narrow and wide in Midnight and Lavender.
    /// </summary>
    static async Task TestFloatingPlan()
    {
        Log("\n## MF — the floating plan card: a new plan floats a moment, then only while the chat's card is off screen");
        string renders = Environment.GetEnvironmentVariable("CHATTEST_RENDERS") is { Length: > 0 } r ? r : AppContext.BaseDirectory;
        Directory.CreateDirectory(renders);
        var themes = Sp.GetRequiredService<ThemeService>();
        double widthBefore = Win.Width;
        LocSvc.SetLanguage("th");
        try
        {
            // ── the rule, pure: (has plan, inline card seen, plan is new, intro elapsed) ──
            bool allRight = true;
            var wrong = new List<string>();
            foreach (bool has in new[] { false, true })
            foreach (bool seen in new[] { false, true })
            foreach (bool isNew in new[] { false, true })
            foreach (bool elapsed in new[] { false, true })
            {
                bool expect = has && (!seen || (isNew && !elapsed));
                if (ChatViewModel.FloatingPlanShows(has, seen, isNew, elapsed) != expect) { allRight = false; wrong.Add($"{has}/{seen}/{isNew}/{elapsed}"); }
            }
            Check("MF the rule: no plan → never; a new plan → until its time is up; then only while the chat's card is off screen",
                allRight, string.Join(", ", wrong));
            Check("MF a step moving on is not a new plan; a different list is; items added after the old ones are not",
                !ChatViewModel.IsNewPlan(new[] { "a", "b" }, new[] { "a", "b" })
                && ChatViewModel.IsNewPlan(null, new[] { "a" })
                && ChatViewModel.IsNewPlan(new[] { "a", "b" }, new[] { "a", "c" })
                && !ChatViewModel.IsNewPlan(new[] { "a", "b" }, new[] { "a", "b", "c" })
                && ChatViewModel.IsNewPlan(new[] { "a", "b", "c" }, new[] { "a", "b" })
                && !ChatViewModel.IsNewPlan(new[] { "a" }, Array.Empty<string>()));
            Check("MF hysteresis: a card counts as seen once a good part shows, and as gone only when nearly gone",
                ChatViewModel.InlinePlanSeen(false, 100, 300, 600)            // all of it
                && !ChatViewModel.InlinePlanSeen(false, 570, 770, 600)        // 30 px peeking at the bottom: not yet seen
                && ChatViewModel.InlinePlanSeen(true, 570, 770, 600)          // …but if it was, it still is
                && !ChatViewModel.InlinePlanSeen(true, 590, 790, 600)         // 10 px left: gone
                && !ChatViewModel.InlinePlanSeen(true, -400, -200, 600)       // above the top
                && ChatViewModel.InlinePlanSeen(false, -20, 50, 600));        // a short card mostly in view

            // ── in the chat: a new plan floats in over the chat's own card, then goes ──
            await FreshChat();
            var session = Vm.CurrentSession!;
            var bar = Find<Border>(Win).First(b => b.Name == "StickyPlanBar");
            await Task.Delay(400); await Render();   // the card the chat before had fades out first
            Check("MF a chat with no plan shows no card", !Vm.IsFloatingPlanShown && !Vm.HasStickyBar && !bar.IsVisible,
                $"shown={Vm.IsFloatingPlanShown} bar={Vm.HasStickyBar} branches={Vm.HasStickyBranches} visible={bar.IsVisible}");
            var plans = (System.Collections.IDictionary)typeof(ChatViewModel).GetField("_lastPlans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            var todos = new List<TodoItem>
            {
                new() { Content = "อ่านโค้ดฟิสิกส์ของลูกบอล", Status = "completed" },
                new() { Content = "ปรับค่าแรงเสียดทานและการกระเด้งให้สมจริงขึ้น โดยดูจากค่าที่วัดได้ในเกมจริงและเทียบกับสูตรในเอกสาร", Status = "in_progress" },
                new() { Content = "รันเกมแล้วเทียบผลก่อนและหลัง", Status = "pending" },
                new() { Content = "สรุปผลให้เจ้าของ", Status = "pending" },
            };
            plans[session.Id] = todos;
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยแก้ฟิสิกส์ลูกบอลให้สมจริงขึ้น" });
            Vm.RefreshStickyPlan();
            // The chat's own card, as TodoChanged adds it.
            var inlineRows = Vm.StickyPlanItems.Select(p => new PlanItemView(p.Number, p.Text, p.State)).ToList();
            var planCard = new ChatMessage { Role = MessageRole.System, Content = "📋 plan", PlanItems = inlineRows,
                                             PlanHeader = LocF("chat.plan.working", 1, 4), PlanElapsed = "42s" };
            Vm.Messages.Add(planCard);
            await Render(); await Task.Delay(300); await Render();
            Check("MF a new plan floats in (its intro), even with the chat's card on screen",
                Vm.IsPlanIntroOn && Vm.IsFloatingPlanShown && bar.IsVisible, $"intro={Vm.IsPlanIntroOn} shown={Vm.IsFloatingPlanShown} visible={bar.IsVisible}");
            View.CheckInlinePlan();
            Check("MF the view measures the chat's card as on screen (it is the last row, the list at the bottom)", Vm.IsInlinePlanVisible);

            // the mouse on it holds it past its time; leaving lets it go a little later
            Vm.IsFloatingPlanHovered = true;
            await Task.Delay(ChatViewModel.PlanIntroLength + TimeSpan.FromMilliseconds(500));
            Check("MF the mouse on the card keeps it past its four seconds", Vm.IsPlanIntroOn && Vm.IsFloatingPlanShown);
            Vm.IsFloatingPlanHovered = false;
            await Task.Delay(ChatViewModel.PlanIntroAfterHover + TimeSpan.FromMilliseconds(500));
            await Render(); await Task.Delay(400); await Render();
            Check("MF …then it goes (its time is up, the chat's card is on screen) and fades out of the way",
                !Vm.IsPlanIntroOn && !Vm.IsFloatingPlanShown && !bar.IsVisible, $"intro={Vm.IsPlanIntroOn} shown={Vm.IsFloatingPlanShown} visible={bar.IsVisible}");

            // a step moving on does not bring it back while the chat's card is in view
            todos[1].Status = "completed"; todos[2].Status = "in_progress";
            Vm.RefreshStickyPlan();
            planCard.PlanItems![1].State = "done"; planCard.PlanItems[2].State = "active";
            planCard.PlanHeader = LocF("chat.plan.working", 2, 4);
            await Render(); await Task.Delay(300);
            Check("MF a step finishing does not pop the card back up", !Vm.IsPlanIntroOn && !Vm.IsFloatingPlanShown && !bar.IsVisible);

            // scrolled away from the chat's card → it shows; back → it goes
            for (int i = 0; i < 24; i++)
                Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"กำลังตรวจผลรอบที่ {i + 1} — ค่าแรงเสียดทาน 0.9{i % 10}" });
            await Render(); await Task.Delay(200);
            var sv = Find<ScrollViewer>(ChatList).First();
            sv.ScrollToEnd(); await Render(); await Task.Delay(200);
            View.CheckInlinePlan(); await Render(); await Task.Delay(400); await Render();
            Check("MF scrolled past the chat's card: the floating card shows", !Vm.IsInlinePlanVisible && Vm.IsFloatingPlanShown && bar.IsVisible,
                $"inline={Vm.IsInlinePlanVisible} shown={Vm.IsFloatingPlanShown}");
            // as the reader does: a wheel step up first (no growth in it, so the list stops following the bottom)
            sv.ScrollToVerticalOffset(sv.VerticalOffset - 60); await Render();
            typeof(ChatView).GetField("_stickToBottom", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(View, false);   // the reader left the bottom
            sv.ScrollToTop(); await Render(); await Task.Delay(200); await Render();   // the card is the chat's second row
            View.CheckInlinePlan(); await Render(); await Task.Delay(400); await Render();
            var cardRow = Container(planCard);
            string where = cardRow == null ? $"not realised, offset {sv.VerticalOffset}/{sv.ScrollableHeight}" : cardRow.TransformToAncestor(sv).TransformBounds(new Rect(cardRow.RenderSize)).ToString() + " in " + sv.ActualHeight + $" offset {sv.VerticalOffset}/{sv.ScrollableHeight}";
            Check("MF scrolled back to it: the floating card goes", Vm.IsInlinePlanVisible && !Vm.IsFloatingPlanShown && !bar.IsVisible,
                $"inline={Vm.IsInlinePlanVisible} shown={Vm.IsFloatingPlanShown} card {where}");

            // the chat's card is put back at the bottom on each update (TodoChanged); scrolling throttles to one check
            int checks = 0;
            System.ComponentModel.PropertyChangedEventHandler count = (_, e) => { if (e.PropertyName == nameof(ChatViewModel.IsInlinePlanVisible)) checks++; };
            Vm.PropertyChanged += count;
            for (int i = 0; i < 30; i++) { sv.ScrollToVerticalOffset(sv.VerticalOffset + (i % 2 == 0 ? 3 : -3)); await Dispatcher.Yield(DispatcherPriority.Render); }
            await Task.Delay(300);
            Vm.PropertyChanged -= count;
            Check("MF scrolling back and forth at the card's edge does not flip it (hysteresis, throttled checks)", checks == 0, $"{checks} flips");

            // a different plan: floats in again, over the card in view
            todos = new List<TodoItem>
            {
                new() { Content = "เขียนเทสต์การชนของลูกบอล", Status = "in_progress" },
                new() { Content = "แก้ตามผลเทสต์", Status = "pending" },
            };
            plans[session.Id] = todos;
            Vm.RefreshStickyPlan();
            await Render(); await Task.Delay(400); await Render();
            Check("MF a brand-new plan floats in again", Vm.IsPlanIntroOn && Vm.IsFloatingPlanShown && bar.IsVisible);

            // renders: the floating card (expanded) and the chat's card, narrow and wide, Midnight and Lavender
            planCard.PlanItems = todos.Select((t, i) => new PlanItemView(i + 1, t.Content, i == 0 ? "active" : "pending")).ToList();
            planCard.PlanHeader = LocF("chat.plan.working", 0, 2);
            plans[session.Id] = new List<TodoItem>
            {
                new() { Content = "อ่านโค้ดฟิสิกส์ของลูกบอล", Status = "completed" },
                new() { Content = "ปรับค่าแรงเสียดทานและการกระเด้งให้สมจริงขึ้น โดยดูจากค่าที่วัดได้ในเกมจริงและเทียบกับสูตรในเอกสาร", Status = "completed" },
                new() { Content = "รันเกมแล้วเทียบผลก่อนและหลัง ทั้งบนพื้นหญ้า พื้นไม้ และพื้นน้ำแข็ง", Status = "in_progress" },
                new() { Content = "สรุปผลให้เจ้าของ", Status = "pending" },
            };
            Vm.RefreshStickyPlan();
            foreach (var row in Vm.StickyPlanItems) row.IsLive = true;
            Vm.IsStickyPlanExpanded = true;
            planCard.PlanItems = Vm.StickyPlanItems.Select(p => new PlanItemView(p.Number, p.Text, p.State) { IsLive = true }).ToList();
            planCard.PlanHeader = LocF("chat.plan.working", 2, 4);
            // The new plan's intro, held by the mouse, keeps the floating card up for its pictures.
            Vm.IsFloatingPlanHovered = true;
            foreach (var id in new[] { ThemeIds.Midnight, ThemeIds.Lavender })
                foreach (var (label, width) in new[] { ("narrow", 640.0), ("wide", 1500.0) })
                {
                    themes.Apply(id, persist: false);
                    Win.Width = width;
                    await Task.Delay(150); await Render(); await Task.Delay(500); await Render();
                    Check($"MF the floating card is up for its picture ({id}, {label})", bar.IsVisible && Vm.IsPlanIntroOn);
                    SaveCrop(bar, Path.Combine(renders, $"floating-plan-{id}-{label}.png"));
                }
            // Then the chat's own card, with the floating one gone (it would cover it).
            Vm.IsFloatingPlanHovered = false;
            Vm.EndPlanIntro();
            foreach (var id in new[] { ThemeIds.Midnight, ThemeIds.Lavender })
                foreach (var (label, width) in new[] { ("narrow", 640.0), ("wide", 1500.0) })
                {
                    themes.Apply(id, persist: false);
                    Win.Width = width;
                    await Task.Delay(150); await Render();
                    ChatList.ScrollIntoView(planCard); await Render(); await Task.Delay(500); await Render();
                    View.CheckInlinePlan(); await Render(); await Task.Delay(400); await Render();
                    SaveCrop(Container(planCard), Path.Combine(renders, $"inline-plan-{id}-{label}.png"));
                }
            Check("MF with the chat's card in view and the intro over, the floating card is gone", Vm.IsInlinePlanVisible && !bar.IsVisible,
                $"inline={Vm.IsInlinePlanVisible} visible={bar.IsVisible}");

            // ── chats keep their own state: another chat has no plan and no card; back here, the plan is not new ──
            var first = session.Id;
            await FreshChat();
            await Render(); await Task.Delay(400); await Render();
            Check("MF switching to a chat with no plan: no card (the intro does not follow it)",
                !Vm.IsFloatingPlanShown && !Vm.IsPlanIntroOn && !bar.IsVisible, $"shown={Vm.IsFloatingPlanShown} visible={bar.IsVisible}");
            if (Vm.Sessions.Any(s => s.Id == first))
            {
                Vm.GoToChatCommand.Execute(first);
                await WaitUntil(() => Vm.CurrentSession?.Id == first, 8000);
                await Render();
                Check("MF back in the first chat its plan is not new again (no second intro)", Vm.CurrentSession?.Id == first && !Vm.IsPlanIntroOn,
                    $"chat={Vm.CurrentSession?.Id == first} intro={Vm.IsPlanIntroOn}");
            }
            else Log("  (the first chat was not saved, so switching back is not exercised)");
        }
        finally
        {
            Vm.IsFloatingPlanHovered = false;
            Vm.IsStickyPlanExpanded = false;
            Win.Width = widthBefore;
            themes.Apply(ThemeIds.Midnight, persist: false);
            LocSvc.SetLanguage("en");
            await Render();
        }
    }

    /// <summary>Endless clocks that run now and ask WPF for frames (active, not paused) — read from WPF's own clock tree.</summary>
    static int EndlessRunning()
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var mc = typeof(UIElement).Assembly.GetType("System.Windows.Media.MediaContext")!.GetProperty("CurrentMediaContext", Any)!.GetValue(null)!;
        var tm = mc.GetType().GetProperty("TimeManager", Any)!.GetValue(mc)!;
        var root = tm.GetType().GetField("_timeManagerClock", Any)!.GetValue(tm)!;
        int n = 0;
        foreach (var w in (System.Collections.IEnumerable)typeof(ClockGroup).GetField("_rootChildren", Any)!.GetValue(root)!)
            if (w is WeakReference { Target: Clock { Timeline: ParallelTimeline { Children.Count: > 0 } t } c } && t.RepeatBehavior == RepeatBehavior.Forever
                && c.CurrentState == ClockState.Active && c.IsPaused != true && IsMotionLoop(t)) n++;
        return n;
    }

    /// <summary>Diagnostics: the endless clocks running now (what they animate), and the styled elements holding a
    /// named state storyboard with its state.</summary>
    static void DumpEndless(string label)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var mc = typeof(UIElement).Assembly.GetType("System.Windows.Media.MediaContext")!.GetProperty("CurrentMediaContext", Any)!.GetValue(null)!;
        var tm = mc.GetType().GetProperty("TimeManager", Any)!.GetValue(mc)!;
        var root = tm.GetType().GetField("_timeManagerClock", Any)!.GetValue(tm)!;
        Log($"  [{label}] endless clocks:");
        foreach (var w in (System.Collections.IEnumerable)typeof(ClockGroup).GetField("_rootChildren", Any)!.GetValue(root)!)
            if (w is WeakReference { Target: Clock { Timeline: { } t } c } && t.RepeatBehavior == RepeatBehavior.Forever && c.CurrentState == ClockState.Active)
            {
                string first = t is ParallelTimeline p && p.Children.Count > 0 ? Storyboard.GetTargetProperty(p.Children[0])?.Path + " child " + p.Children[0].Duration : "";
                Log($"    paused={c.IsPaused} fps={Timeline.GetDesiredFrameRate(t)} sb {t.Duration} {first} t={c.CurrentTime}");
            }
        foreach (var fe in Find<FrameworkElement>(Win).Where(Motion.IsLooping))
            Log($"    looping: {fe.GetType().Name} vis={fe.IsVisible} on {fe.DataContext?.GetType().Name}");
    }

    /// <summary>One of this change's loops (the rest of the window has endless clocks of its own that come and go): a
    /// Views.Motion double ("(0)"), a flowing line, or a 24 fps breathing opacity.</summary>
    static bool IsMotionLoop(ParallelTimeline t)
    {
        string path = Storyboard.GetTargetProperty(t.Children[0])?.Path ?? "";
        return path == "(0)" || path == "StrokeDashOffset" || (path == "Opacity" && Timeline.GetDesiredFrameRate(t) == 24);
    }

    /// <summary>The window drawn at twice the size, cut to <paramref name="el"/> with a margin — the real background behind it.</summary>
    static void SaveCrop(FrameworkElement el, string path)
    {
        if (el == null) { Log($"  (no element for {Path.GetFileName(path)})"); return; }
        var rootEl = (FrameworkElement)Win.Content;
        const double scale = 2;
        var bmp = new RenderTargetBitmap((int)(rootEl.ActualWidth * scale), (int)(rootEl.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(rootEl);
        var at = el.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        at.Inflate(10, 10);
        at.Intersect(new Rect(0, 0, rootEl.ActualWidth, rootEl.ActualHeight));
        var crop = new CroppedBitmap(bmp, new Int32Rect((int)(at.X * scale), (int)(at.Y * scale), (int)(at.Width * scale), (int)(at.Height * scale)));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(crop));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log($"  render: {path}");
    }
}
