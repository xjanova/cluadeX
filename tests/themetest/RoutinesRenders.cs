using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CluadeX.Services.Routines;
using CluadeX.ViewModels;

namespace ThemeTest;

internal static partial class Program
{
    static bool RoutinesOnly;

    /// <summary>
    /// The Routines page (scheduled tasks, 2026-11-11) with two routines on it — one you made, one a chat made that
    /// failed last time — and the editor open, in every theme; then the delete confirm. Checks the rows, the badge and
    /// the editor are on screen. Saved to renders\routines\&lt;theme&gt;.png and delete.png for the eye.
    ///
    ///   dotnet run -- --routines --out D:\...\themes
    /// </summary>
    static async Task RoutinesShots(object themes)
    {
        Log("\n## Routines page — rows, the editor and the delete confirm, every theme");
        string dir = Path.Combine(OutRoot, "renders", "routines");
        Directory.CreateDirectory(dir);
        var svc = RoutineService.Current;
        var vm = MainVm.RoutinesVM;
        if (svc == null || vm == null) { Check("routines: the page and its service are in the app", false, $"service {svc != null} · page {vm != null}"); return; }

        var mine = new Routine { Name = "Nightly tests", Prompt = "Run the tests and summarise what failed.", Project = FixtureRoot,
            Kind = RoutineSchedule.Daily, Days = RoutineSchedule.Weekdays, Time = "02:00", SameChat = true };
        var fromAi = new Routine { Name = "Check open PRs", Prompt = "List the open PRs that need me.", Project = FixtureRoot,
            Kind = RoutineSchedule.Interval, IntervalMinutes = 120, CreatedBy = "ai",
            LastRunAt = DateTime.Now.AddMinutes(-30), LastStatus = "failed", LastNote = "gh: not signed in" };
        Check("routines: two routines saved", svc.Put(mine) == null && svc.Put(fromAi) == null, "");
        try
        {
            MainVm.NavigateToCommand.Execute("Routines");
            await Idle(200);
            var row = vm.Rows.First(r => r.Id == mine.Id);
            vm.EditCommand.Execute(row);
            await Idle(200);
            var ids = themes == null ? new List<string> { "current" }
                : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
            foreach (string id in ids)
            {
                if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(200); }
                var root = (FrameworkElement)RootVisual(MainWin);
                var texts = Find<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                var boxes = Find<TextBox>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Check($"routines {id}: both rows, the chat-made badge and the failure note show",
                    texts.Contains("Nightly tests") && texts.Contains("Check open PRs") && texts.Any(t => t.Contains("gh: not signed in"))
                    && texts.Any(t => t == "made by a chat" || t == "AI ตั้งจากแชท"), string.Join(" | ", texts.Where(t => t.Length > 0).Take(40)));
                Check($"routines {id}: the editor is open with the routine in it",
                    vm.IsEditing && boxes.Contains("Nightly tests") && boxes.Contains("02:00") && boxes.Contains(FixtureRoot), string.Join(" | ", boxes));
                // The editor's scroll area is where text gets cut: each option line must end inside it, on more than one line.
                var lines = Find<CheckBox>(root).Where(c => c.IsVisible && c.Content is TextBlock).Select(c => (TextBlock)c.Content).ToList();
                var area = lines.Count == 0 ? null : FindAncestor<ScrollViewer>(lines[0]);
                double Right(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)).Right;
                Check($"routines {id}: the two long option lines wrap inside the editor, not cut off",
                    lines.Count == 2 && area != null && lines.All(t => Right(t) <= Right(area) + 0.5 && t.ActualHeight > t.FontSize * 2),
                    string.Join(" | ", lines.Select(t => $"right {Right(t):0} of {(area == null ? 0 : Right(area)):0}, {t.ActualHeight:0} px high")));
                var full = await Stable(() => (root, W, H), $"routines/{id}");
                SavePng(full, Path.Combine(dir, id.ToLowerInvariant() + ".png"));
            }
            vm.CancelCommand.Execute(null);
            vm.AskDeleteCommand.Execute(vm.Rows.First(r => r.Id == fromAi.Id));
            await Idle(200);
            var root2 = (FrameworkElement)RootVisual(MainWin);
            Check("routines: delete asks first, naming the routine",
                vm.IsDeleteOpen && Find<TextBlock>(root2).Any(t => t.IsVisible && t.Text.Contains("Check open PRs") && t.Text.Contains("?")), vm.DeleteText);
            SavePng(await Stable(() => (root2, W, H), "routines/delete"), Path.Combine(dir, "delete.png"));
            vm.CancelDeleteCommand.Execute(null);
            await Idle();
            Check("routines: cancel keeps it", !vm.IsDeleteOpen && svc.Get(fromAi.Id) != null, "");
            await EventAndTelegramShots(svc, vm, dir);
        }
        finally
        {
            svc.Remove(mine.Id);
            svc.Remove(fromAi.Id);
        }
    }

    /// <summary>An event routine (when CI fails on main) on the list and in the editor; the Telegram panel before it is
    /// connected, then connected to a fake Telegram with pairing open. Saved as event.png and telegram.png.</summary>
    static async Task EventAndTelegramShots(RoutineService svc, RoutinesViewModel vm, string dir)
    {
        var ev = new Routine { Name = "Fix red CI", Prompt = "Find why CI failed and fix it.", Project = FixtureRoot,
            Kind = RoutineSchedule.Event, Trigger = RoutineSchedule.CiFailed, Branch = "main", CooldownMinutes = 30 };
        var realTg = CluadeX.Services.Remote.TelegramBridge.Current;
        Check("routines: an event routine is saved", svc.Put(ev) == null, "");
        try
        {
            await Idle(200);
            var root = (FrameworkElement)RootVisual(MainWin);
            // A line made of Runs has no Text of its own: read its runs.
            static string Said(TextBlock t) => t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : t.Text;
            var texts = Find<TextBlock>(root).Where(t => t.IsVisible).Select(Said).ToList();
            Check("routines: the event row says what starts it and that it waits",
                texts.Any(t => t.Contains("CI") && t.Contains("main") && (t.Contains("waits for the event") || t.Contains("รอเหตุการณ์"))),
                string.Join(" | ", texts.Where(t => t.Length > 0).Take(30)));
            Check("routines: Telegram's panel, not connected — the token box and how to make a bot",
                Find<PasswordBox>(root).Any(p => p.IsVisible) && texts.Any(t => t.Contains("BotFather")), "");

            vm.EditCommand.Execute(vm.Rows.First(r => r.Id == ev.Id));
            await Idle(200);
            root = (FrameworkElement)RootVisual(MainWin);
            var boxes = Find<TextBox>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var radios = Find<RadioButton>(root).Where(r => r.IsVisible && r.Content is string).ToList();
            var checks = Find<CheckBox>(root).Where(c => c.IsVisible && c.Content is TextBlock).Select(c => ((TextBlock)c.Content).Text).ToList();
            Check("routines: the editor shows the event — its trigger picked, the branch, the break between runs; no catch-up",
                vm.KindEvent && vm.TrigCi && radios.Any(r => r.IsChecked == true && (string)r.Content is "CI failed" or "CI พัง (รันไม่ผ่าน)")
                && radios.Count(r => (string)r.Content is "Files changed in the project folder" or "ไฟล์ในโฟลเดอร์โปรเจกต์เปลี่ยน") == 1
                && boxes.Contains("main") && boxes.Contains("30")
                && !checks.Any(c => c.Contains("CluadeX was closed") || c.Contains("ตอนปิด CluadeX")),
                $"radios {radios.Count} · boxes {string.Join(",", boxes)} · checks {string.Join(",", checks)}");
            vm.TrigComment = true;
            await Idle(100);
            root = (FrameworkElement)RootVisual(MainWin);
            Check("routines: a comment trigger offers \"anyone\", and the branch box goes",
                Find<CheckBox>(root).Any(c => c.IsVisible && c.Content is TextBlock tb && (tb.Text.Contains("no role") || tb.Text.Contains("ไม่มีสิทธิ์")))
                && !Find<TextBox>(root).Any(t => t.IsVisible && t.Text == "main"), "");
            vm.TrigCi = true;
            await Idle(100);
            SavePng(await Stable(() => ((FrameworkElement)RootVisual(MainWin), W, H), "routines/event"), Path.Combine(dir, "event.png"));
            vm.CancelCommand.Execute(null);

            // Telegram, connected to a fake (nothing reaches api.telegram.org), with pairing open.
            var fakeTg = new CluadeX.Services.Remote.TelegramBridge(null, _ => new FakeTelegramApi()) { AutoRead = false };
            CluadeX.Services.Remote.TelegramBridge.Current = fakeTg;
            string? problem = await fakeTg.ConnectAsync("123456789:" + new string('A', 35));
            vm.TgPairCommand.Execute(null);
            await Idle(200);
            root = (FrameworkElement)RootVisual(MainWin);
            var tgBoxes = Find<TextBox>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            texts = Find<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Check("routines: Telegram connected — the bot's name, the pairing message to send, the two options",
                problem == null && texts.Any(t => t.Contains("@ThemeBot")) && tgBoxes.Any(b => b.StartsWith("/pair ") && b.Length == 12)
                && Find<CheckBox>(root).Count(c => c.IsVisible && c.Content is TextBlock tb && (tb.Text.Contains("summary") || tb.Text.Contains("สรุป") || tb.Text.Contains("every chat") || tb.Text.Contains("ทุกแชท"))) == 2
                && !Find<PasswordBox>(root).Any(p => p.IsVisible),
                $"{problem} · {string.Join(",", tgBoxes)}");
            SavePng(await Stable(() => (root, W, H), "routines/telegram"), Path.Combine(dir, "telegram.png"));
            vm.TgAskDisconnectCommand.Execute(null);
            await Idle(100);
            Check("routines: Disconnect asks first", vm.TgDisconnectOpen, "");
            vm.TgCancelDisconnectCommand.Execute(null);
            fakeTg.Dispose();
        }
        finally
        {
            CluadeX.Services.Remote.TelegramBridge.Current = realTg;
            svc.Remove(ev.Id);
        }
    }

    /// <summary>Telegram as far as Connect needs it: getMe, and nothing waiting.</summary>
    sealed class FakeTelegramApi : CluadeX.Services.Remote.ITelegramApi
    {
        public Task<System.Text.Json.JsonElement> CallAsync(string method, IReadOnlyDictionary<string, object?> payload, CancellationToken ct) =>
            Task.FromResult(System.Text.Json.JsonDocument.Parse(method == "getMe" ? "{\"username\":\"ThemeBot\"}" : "[]").RootElement.Clone());
    }
}
