using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Background;
using CluadeX.Services.Providers;
using CluadeX.Services.Watch;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// WJ — the AI hands a wait to CluadeX and is woken with the result (owner 2026-11-07: "ตัวเฝ้าติดตาม งาน … การรอ ci หรือ
/// commit ให้ผ่าน"). Through the real chat page, the real native loop and the real watch_process tool on a real background
/// process (cmd), with a scripted provider: the card, the turn the end starts (chat on screen and off it), the note a
/// working run reads instead, the owner's switch, the wake limit, Cancel and Ctrl+Alt+End. No network, no model, no gh.
/// </summary>
internal static partial class Program
{
    static async Task TestWatchJobs()
    {
        Log("\n## WJ — watch_process: the chat waits off the model's clock and carries on when the work ends");
        var provider = new NativeScript();
        object before = Field<object>(Providers, "_activeProvider");
        bool agenticWas = Vm.AgenticMode;
        bool permBefore = Settings.Settings.Features.PermissionSystem;
        bool autoBefore = Settings.Settings.WatchAutoContinue;
        var svc = RunWatchService.Current;
        var bg = Sp.GetRequiredService<BackgroundProcessService>();
        Check("WJ0 the app made the watch service with its tools", svc != null);
        if (svc == null) return;
        var intervalWas = svc.ProcessInterval;
        int wakesWas = svc.MaxWakes;
        string proj = Path.Combine(PipeRoot, "watch-jobs");

        static NativeToolResponse Say(string text) => new() { StopReason = "end_turn", TextContent = text };
        static NativeToolResponse Tool(string id, string name, object args) => new()
        {
            StopReason = "tool_use",
            ToolCalls = { new NativeToolCall { Id = id, Name = name, Input = JsonSerializer.SerializeToElement(args) } },
        };
        async Task<int> StartJob(string name, int seconds, int exit) => (await bg.StartAsync(new BgStartRequest
        {
            Command = $"ping -n {seconds + 1} 127.0.0.1 >nul & echo {name}-out & exit /b {exit}", Cwd = proj, Name = name,
            Shell = ShellRunner.Shell.Cmd, WaitForReady = false, StartedBy = "user",
        })).Process!.Id;
        ChatMessage? CardRow(IEnumerable<ChatMessage> rows) => rows.LastOrDefault(m => m.ToolName == ChatMessage.WatchTag);

        try
        {
            if (Directory.Exists(proj)) Directory.Delete(proj, true);
            Directory.CreateDirectory(proj);
            SetField(Providers, "_activeProvider", provider);
            Vm.AgenticMode = true;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = false; s.WatchAutoContinue = true; });
            svc.ProcessInterval = TimeSpan.FromMilliseconds(100);
            svc.CancelAll();

            // ── WJ1–WJ6: on screen — the watch's card, then a turn of its own with the result ──
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            int id = await StartJob("wj1", 3, 2);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t1", "watch_process", new { process_id = $"bg#{id}", note = "fix the failure" })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting for the job to end.")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("It failed with exit 2 — fixing it.")));
            await Send("[WJ1] run the job and carry on when it ends");
            await WaitIdle();
            var card = CardRow(Vm.Messages);
            Check("WJ1 watch_process leaves a card in the chat (not a plain text row)", card is { ShowsWatchCard: true, IsOrchestrationCard: true },
                card?.Content ?? "(no card)");
            await Render();
            var cancel = card == null ? null : ById<Button>(card, "WatchCancel");
            Check("WJ2 the card is drawn with its Cancel button while it watches", card?.WatchCard?.IsActive == true
                && cancel is { IsVisible: true }, $"active {card?.WatchCard?.IsActive}; " + (cancel == null ? "(no button)" : cancel.Visibility.ToString()));
            Check("WJ3 the model was told to end its turn (the tool result)", provider.Texts.Count == 2);
            bool woke = await WaitUntil(() => provider.Texts.Count >= 3, 20000);
            await WaitIdle();
            string wake = provider.Texts.Count >= 3 ? provider.Texts[2] : "";
            Check("WJ4 the process's end starts a turn: the model gets the watch's report (exit 2, its output, its own plan)",
                woke && wake.Contains("[CluadeX watch") && wake.Contains("exit 2") && wake.Contains("wj1-out") && wake.Contains("fix the failure"),
                wake.Length > 500 ? wake[..500] : wake);
            Check("WJ5 the chat shows that turn: the result as its message, then the reply",
                Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content.StartsWith("❌"))
                && Vm.Messages.Any(m => m.Role == MessageRole.Assistant && m.Content.Contains("fixing it")),
                string.Join(" | ", Vm.Messages.TakeLast(4).Select(m => m.Role + ":" + (m.Content.Length > 60 ? m.Content[..60] : m.Content))));
            Check("WJ6 the card says how it ended (failed, exit 2) and has no Cancel any more",
                card?.WatchCard is { IsActive: false } wc && wc.StateText.Contains("failed") && wc.StateText.Contains("exit 2")
                && card.Content.StartsWith("❌"), card?.WatchCard?.StateText);
            Check("WJ6b the plain 'background command ended' notice stepped aside (one message, not two)",
                !Vm.Messages.Any(m => m.IsNotice && m.Content.StartsWith("⏹") && m.Content.Contains("wj1")));

            // ── WJ7–WJ8: the chat is no longer on screen — the turn runs in its own list ──
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            id = await StartJob("wj7", 3, 0);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t7", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting.")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("WJ7 passed, moving on.")));
            await Send("[WJ7] wait for the job");
            await WaitIdle();
            var chatA = Vm.CurrentSession!;
            int calls = provider.Texts.Count;
            await FreshChat();   // another chat on screen
            var chatB = Vm.CurrentSession!;
            int rowsB = Vm.Messages.Count;
            woke = await WaitUntil(() => provider.Texts.Count > calls, 20000);
            await WaitUntil(() => chatA.RunStatus != "working", 10000);
            await Render();
            Check("WJ7 off screen: the watch's chat gets its turn (the report says passed)", woke && provider.Texts[^1].Contains("passed"),
                woke ? provider.Texts[^1][..Math.Min(200, provider.Texts[^1].Length)] : "(no turn)");
            var stored = Persist.LoadSession(chatA.Id);
            Check("WJ8 …the reply is in THAT chat; the chat on screen is untouched",
                ReferenceEquals(Vm.CurrentSession, chatB) && Vm.Messages.Count == rowsB
                && stored?.Messages.Any(m => m.Role == MessageRole.Assistant && m.Content.Contains("WJ7 passed")) == true,
                $"rows on screen {Vm.Messages.Count} vs {rowsB}; stored {stored?.Messages.Count}");

            // ── WJ9–WJ10: it ends while the chat's AI still works — a note at its next step, no extra turn ──
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            id = await StartJob("wj9", 1, 0);
            WatchJob? job9 = null;
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t9", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue(async (_, _) =>
            {
                job9 = svc.Jobs.LastOrDefault(j => j.ProcessId == id);
                var end = DateTime.Now.AddSeconds(15);
                while (job9 is { IsActive: true } && DateTime.Now < end) await Task.Delay(50);
                await Task.Delay(300);   // the delivery is posted to the UI thread
                return Tool("t9b", "watch_list", new { });
            });
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Saw it end.")));
            int c9 = provider.Texts.Count;
            await Send("[WJ9] keep working while it runs");
            await WaitIdle();
            string third = provider.Texts.Count >= c9 + 3 ? provider.Texts[c9 + 2] : "";
            Check("WJ9 the result reaches the working AI at its next step (a CluadeX notice in that request)",
                job9 is { IsActive: false } && third.Contains("[CluadeX notice]") && third.Contains("[CluadeX watch"),
                third.Length > 300 ? third[..300] : third);
            await Task.Delay(800);
            Check("WJ10 …and no second turn starts for it", provider.Texts.Count == c9 + 3, (provider.Texts.Count - c9).ToString());

            // ── WJ11: the owner's switch off — a notice, no turn ──
            Settings.UpdateSettings(s => s.WatchAutoContinue = false);
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            id = await StartJob("wj11", 1, 1);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t11", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting.")));
            await Send("[WJ11] wait");
            await WaitIdle();
            int c11 = provider.Texts.Count;
            bool noticed = await WaitUntil(() => Vm.Messages.Any(m => m.IsNotice && m.Content.Contains("Carry on when a watch ends")), 15000);
            await Task.Delay(500);
            Check("WJ11 'carry on' switched off: a notice that names the switch, and no turn", noticed && provider.Texts.Count == c11,
                string.Join(" | ", Vm.Messages.TakeLast(2).Select(m => m.Content)));
            Settings.UpdateSettings(s => s.WatchAutoContinue = true);

            // ── WJ12: the wake limit — a notice that it waits for the owner now ──
            svc.MaxWakes = 0;
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            id = await StartJob("wj12", 1, 1);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t12", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting.")));
            await Send("[WJ12] wait");
            await WaitIdle();
            int c12 = provider.Texts.Count;
            noticed = await WaitUntil(() => Vm.Messages.Any(m => m.IsNotice && m.Content.Contains("waits for you now")), 15000);
            await Task.Delay(500);
            Check("WJ12 past the wake limit: a notice, no turn (a fail → fix → fail loop stops for the owner)", noticed && provider.Texts.Count == c12);
            svc.MaxWakes = wakesWas;

            // ── WJ13–WJ14: Cancel on the card; Ctrl+Alt+End ──
            await FreshChat();
            Vm.SetWorkingDirectory(proj);
            id = await StartJob("wj13", 30, 0);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t13", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting.")));
            await Send("[WJ13] wait for the long one");
            await WaitIdle();
            card = CardRow(Vm.Messages);
            await Render();
            cancel = card == null ? null : ById<Button>(card, "WatchCancel");
            if (cancel != null && cancel.Command?.CanExecute(null) == true) cancel.Command.Execute(null);
            await Render();
            int c13 = provider.Texts.Count;
            Check("WJ13 the card's Cancel stops the watch: it says cancelled, the button goes", card?.WatchCard is { IsActive: false } w13
                && w13.StateText.Contains("cancelled") && cancel?.IsVisible != true, card?.WatchCard?.StateText ?? "(no card)");
            await bg.StopAsync(id, TimeSpan.FromSeconds(3));
            await Task.Delay(500);
            Check("WJ13b …and nothing starts when the process ends after", provider.Texts.Count == c13);

            id = await StartJob("wj14", 30, 0);
            provider.Steps.Enqueue((_, _) => Task.FromResult(Tool("t14", "watch_process", new { process_id = id.ToString() })));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Waiting.")));
            await Send("[WJ14] wait again");
            await WaitIdle();
            var j14 = svc.Jobs.LastOrDefault(j => j.ProcessId == id);
            Vm.StopAllRuns();
            Check("WJ14 Ctrl+Alt+End (StopAllRuns) cancels every watch too", j14 is { State: WatchState.Cancelled } && svc.Jobs.All(j => !j.IsActive),
                j14?.State.ToString());
            await bg.StopAsync(id, TimeSpan.FromSeconds(3));
        }
        finally
        {
            svc.CancelAll();
            svc.ProcessInterval = intervalWas;
            svc.MaxWakes = wakesWas;
            SetField(Providers, "_activeProvider", before);
            Vm.AgenticMode = agenticWas;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = permBefore; s.WatchAutoContinue = autoBefore; });
            await bg.StopAllAsync(TimeSpan.FromSeconds(3));
            Vm.SetWorkingDirectory("");
            try { Directory.Delete(proj, true); } catch { }
        }
    }
}
