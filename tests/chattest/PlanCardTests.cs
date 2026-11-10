using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ChatTest;

/// <summary>
/// PC — the plan card under the chat (owner 2026-11-07, a screenshot: "ทำไปสักพัก มันค้าง แต่มันก็ทำต่อนะ แต่การแสดงผลมัน
/// ค้างไปหรือเปล่า"). A 70-minute Claude Dev run ticked its plan for the first 12 minutes only: the card's clock moved only
/// when the plan changed, so it stood at "12m 26s", and when the run ended the card still said "กำลังดำเนินการ (2/6)" —
/// it read as a hang. Through the real chat page and the real native tool loop, with a scripted provider (no network).
/// </summary>
internal static partial class Program
{
    static async Task TestPlanCardClock()
    {
        Log("\n## PC — the plan card's clock runs on its own; a run that ends says so");

        // PC0 — the words, pure
        Check("PC0 the clock text: nothing under a second, then 42s, 12m 26s, 1h 10m",
            ChatViewModel.PlanElapsedText(TimeSpan.FromMilliseconds(400)) == ""
            && ChatViewModel.PlanElapsedText(TimeSpan.FromSeconds(42)) == "42s"
            && ChatViewModel.PlanElapsedText(new TimeSpan(0, 12, 26)) == "12m 26s"
            && ChatViewModel.PlanElapsedText(new TimeSpan(1, 10, 5)) == "1h 10m");
        Check("PC0 the header: working while it runs, done when all are ticked, ended when the run stops with items left",
            Vm.PlanCardHeader(2, 6, running: true) == LocF("chat.plan.working", 2, 6)
            && Vm.PlanCardHeader(6, 6, running: true) == LocF("chat.plan.done", 6, 6)
            && Vm.PlanCardHeader(6, 6, running: false) == LocF("chat.plan.done", 6, 6)
            && Vm.PlanCardHeader(2, 6, running: false) == LocF("chat.plan.ended", 2, 6)
            && LocF("chat.plan.ended", 2, 6) != LocF("chat.plan.working", 2, 6));

        var provider = new NativeScript();
        object before = Field<object>(Providers, "_activeProvider");
        bool agenticWas = Vm.AgenticMode;
        bool permBefore = Settings.Settings.Features.PermissionSystem;
        static NativeToolResponse Plan(string id, params (string Text, string Status)[] todos) => new()
        {
            StopReason = "tool_use",
            ToolCalls = { new NativeToolCall { Id = id, Name = "todo_write", Input = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                todos = todos.Select(t => new { content = t.Text, status = t.Status }),
            })).RootElement.Clone() } },
        };
        ChatMessage? Card() => Vm.Messages.LastOrDefault(m => m.IsPlan);
        try
        {
            SetField(Providers, "_activeProvider", provider);
            Vm.AgenticMode = true;
            Settings.UpdateSettings(s => s.Features.PermissionSystem = false);
            await FreshChat();

            // ── PC1–PC2: a plan written once, then a long step that never touches it ──
            var gate = new TaskCompletionSource();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Plan("p1", ("Read the client", "completed"), ("Fix the timeout", "in_progress"), ("Run the tests", "pending"))));
            provider.Steps.Enqueue(async (_, ct) =>
            {
                await gate.Task.WaitAsync(ct);
                return new NativeToolResponse { StopReason = "end_turn", TextContent = "Fixed the timeout." };
            });
            await Send("[PC1] fix the timeout");
            bool shown = await WaitUntil(() => Card() != null, 10000);
            var card = Card();
            // The clock shows from one second on; a busy dispatcher (the full harness) can be late with the first tick.
            await WaitUntil(() => card?.PlanElapsed.Length > 0, 5000);
            string t0 = card?.PlanElapsed ?? "";
            await Task.Delay(2200);
            string t1 = card?.PlanElapsed ?? "";
            Check("PC1 while the agent works without touching its plan, the card's clock still moves",
                shown && card != null && t0.Length > 0 && t1 != t0 && card.PlanHeader == LocF("chat.plan.working", 1, 3),
                $"shown={shown} {t0} → {t1} · {card?.PlanHeader}");

            gate.SetResult();
            await WaitIdle();
            string tEnd = card?.PlanElapsed ?? "";
            Check("PC2 the run ended with items unticked: the card says it ended (1/3), not \"working\", and no step turns",
                card != null && card.PlanHeader == LocF("chat.plan.ended", 1, 3) && card.PlanItems!.All(p => !p.IsLive) && tEnd.Length > 0,
                $"{card?.PlanHeader} · live={card?.PlanItems?.Count(p => p.IsLive)} · {tEnd}");
            await Task.Delay(1300);
            Check("PC2 …and its clock stops at the run's whole time", card?.PlanElapsed == tEnd, $"{tEnd} → {card?.PlanElapsed}");

            // ── PC3: a plan ticked to the end says done ──
            provider.Steps.Enqueue((_, _) => Task.FromResult(Plan("p2", ("Rename the file", "in_progress"), ("Update the imports", "pending"))));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Plan("p3", ("Rename the file", "completed"), ("Update the imports", "completed"))));
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "Renamed." }));
            await Send("[PC3] rename the file");
            await WaitIdle();
            var done = Card();
            Check("PC3 a plan ticked to the end says done, with the run's time",
                done != null && !ReferenceEquals(done, card) && done.PlanHeader == LocF("chat.plan.done", 2, 2) && done.PlanElapsed.Length > 0,
                $"{done?.PlanHeader} · {done?.PlanElapsed}");

            // ── PC4–PC6: the next turn carries the plan on; wrap_up ticks it off; its button answers (owner 2026-11-08:
            //    Claude Code's planning — a pinned list, a wrap-up box, a scope tag) ──
            static NativeToolResponse Call(string id, string name, object input) => new()
            {
                StopReason = "tool_use",
                ToolCalls = { new NativeToolCall { Id = id, Name = name, Input = JsonDocument.Parse(JsonSerializer.Serialize(input)).RootElement.Clone() } },
            };
            provider.Steps.Enqueue((_, _) => Task.FromResult(Plan("p4", ("Write the parser", "in_progress"), ("Test the parser", "pending"))));
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "Paused here." }));
            await Send("[PC4] write the parser");
            await WaitIdle();
            var left = Card();

            int callsBefore = provider.Texts.Count;
            provider.Steps.Enqueue((_, _) => Task.FromResult(Call("p5", "todo_write", new { advance = true, scope = "continuation" })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Call("w5", "wrap_up", new { recap = "The parser is written and tested.", ask = "Commit it?", actions = new[] { "Yes, commit", "Not now" } })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "Done." }));
            await Send("[PC5] carry on");
            await WaitIdle();
            string told = provider.Texts.Count > callsBefore ? provider.Texts[callsBefore] : "";
            Check("PC4 the next turn is told the plan it left open",
                told.Contains("[CluadeX: your plan from the last turn") && told.Contains("1. Write the parser (stopped mid-way)"), told.Length > 300 ? told[^300..] : told);
            var carried = Card();
            Check("PC5 advance carries the plan on in the same card; wrap_up ticks the rest off; the chip says continuation",
                left != null && ReferenceEquals(carried, left) && carried!.PlanHeader == LocF("chat.plan.done", 2, 2)
                && carried.PlanItems!.All(p => p.IsDone) && carried.PlanScope == "continuation"
                && Vm.Messages.Count(m => m.IsPlan) == 3,   // PC1's, PC3's, and PC4's carried on (no second card for it)
                $"same={ReferenceEquals(carried, left)} · {carried?.PlanHeader} · {carried?.PlanScope}");

            var box = Vm.Messages.LastOrDefault(m => m.IsNextStep)?.NextStep;
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "Committed." }));
            box?.Actions[0].Command.Execute(null);
            await WaitUntil(() => Vm.IsGenerating, 3000);
            await WaitIdle();
            var reply = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.User);
            Check("PC6 the box's button sends its label as the owner's reply, once",
                box != null && box.Ask == "Commit it?" && box.IsAnswered && reply?.Content == "Yes, commit"
                && Vm.Messages.Count(m => m.Role == MessageRole.User && m.Content == "Yes, commit") == 1,
                $"box={box != null} · last user: {reply?.Content}");

            // ── PC7: Stop mid-plan: the step it was on says stopped, no box ──
            var hold = new TaskCompletionSource();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Plan("p7", ("Migrate the table", "in_progress"), ("Check the rows", "pending"))));
            provider.Steps.Enqueue(async (_, ct) => { await hold.Task.WaitAsync(ct); return new NativeToolResponse { StopReason = "end_turn", TextContent = "late" }; });
            await Send("[PC7] migrate the table");
            await WaitUntil(() => Card()?.PlanItems?.FirstOrDefault()?.Text == "Migrate the table", 10000);
            int boxes = Vm.Messages.Count(m => m.IsNextStep);
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            var stopped = Card();
            Check("PC7 Stop mid-plan: the step says stopped, the header says so, no next-step box",
                stopped?.PlanItems?[0].IsStopped == true && stopped.PlanStopped && stopped.PlanHeader == LocF("plan.card.stopped", 0, 2)
                && Vm.Messages.Count(m => m.IsNextStep) == boxes,
                $"{stopped?.PlanHeader} · stopped={stopped?.PlanItems?[0].IsStopped}");
        }
        finally
        {
            SetField(Providers, "_activeProvider", before);
            Vm.AgenticMode = agenticWas;
            Settings.UpdateSettings(s => s.Features.PermissionSystem = permBefore);
        }
    }
}
