// The sidebar's run badges and น้อง CluadeX's mood (owner 2026-11-08: "เปลี่ยนให้ไอค่อนบับเบิ้ลหน้าแชท เป็นไอค่อนที่แสดงการทำงาน
// แบบอนิเมชั่น ถ้าอันไหนผ่านหมดแล้วก็สีเทา" + "หน้าของน้อง cluadex … ขยับยุกยิกได้ตามงาน … ไม่มีไรทำก็ทำหลับ").
// Pure: how a turn's end becomes a badge, what opening the chat clears, the mood from the runs and the clock, and the
// live-runs ledger that shows a run CluadeX never saw end (a crash, a kill) as stopped after the restart.
using System.IO;
using CluadeX.Models;
using CluadeX.Services;

namespace ApiTest;

internal static class RunBadgeWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · sidebar run badges, the buddy's mood, the live-runs ledger");

        // ── the end of a turn ──
        check("badge", "a quiet finish on screen leaves the grey idle node", RunBadge.AtEnd(false, false, onScreen: true) == RunBadge.Idle, "");
        check("badge", "a finish in a background chat is an unread result", RunBadge.AtEnd(false, false, onScreen: false) == RunBadge.Done, "");
        check("badge", "a failure is red, on screen or not", RunBadge.AtEnd(false, true, true) == RunBadge.Failed && RunBadge.AtEnd(false, true, false) == RunBadge.Failed, "");
        check("badge", "a stop is stopped, on screen or not", RunBadge.AtEnd(true, false, true) == RunBadge.Stopped && RunBadge.AtEnd(true, false, false) == RunBadge.Stopped, "");
        check("badge", "an error then a stop reads as the failure", RunBadge.AtEnd(true, true, false) == RunBadge.Failed, "");

        check("open", "opening a chat reads its unread result", RunBadge.OnOpen(RunBadge.Done) == RunBadge.Idle, "");
        check("open", "…but a failure or a stop stays until the next turn", RunBadge.OnOpen(RunBadge.Failed) == RunBadge.Failed && RunBadge.OnOpen(RunBadge.Stopped) == RunBadge.Stopped, "");
        check("open", "a live run keeps its badge", RunBadge.OnOpen(RunBadge.Working) == RunBadge.Working && RunBadge.OnOpen(null) == RunBadge.Idle, "");
        check("live", "only working and waiting animate", RunBadge.IsLive(RunBadge.Working) && RunBadge.IsLive(RunBadge.Waiting)
            && !RunBadge.IsLive(RunBadge.Done) && !RunBadge.IsLive(RunBadge.Failed) && !RunBadge.IsLive(RunBadge.Idle), "");

        // ── how the turn ended, from its own rows ──
        var oldError = new ChatMessage { Role = MessageRole.Assistant, Content = "boom", HasError = true };
        var user = new ChatMessage { Role = MessageRole.User, Content = "again" };
        var fine = new ChatMessage { Role = MessageRole.Assistant, Content = "all done" };
        var rows = new List<ChatMessage> { oldError, user, fine };
        var o = RunBadge.Outcome(rows, firstRow: 1, cancelled: false);
        check("outcome", "an error of an EARLIER turn does not make this one fail", !o.Failed && !o.Stopped, $"{o}");
        rows.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "429 quota", HasError = true });
        rows.Add(new ChatMessage { Role = MessageRole.System, Content = "note" });
        o = RunBadge.Outcome(rows, 1, false);
        check("outcome", "this turn's last reply is an error: failed (a side note after it does not hide it)", o.Failed, $"{o}");
        var paused = new List<ChatMessage> { user, new() { Role = MessageRole.Assistant, Content = AgentRunScope.PausedMarker + " paused" } };
        check("outcome", "a reply that ends on Pause counts as stopped", RunBadge.Outcome(paused, 0, false).Stopped, "");
        var cut = new List<ChatMessage> { user, new() { Role = MessageRole.Assistant, Content = "half", IsInterrupted = true } };
        check("outcome", "an interrupted reply counts as stopped", RunBadge.Outcome(cut, 0, false).Stopped, "");
        check("outcome", "the owner's Stop with nothing written yet counts as stopped", RunBadge.Outcome(new List<ChatMessage> { user }, 0, cancelled: true) is { Stopped: true, Failed: false }, "");

        // ── her mood ──
        var never = TimeSpan.FromHours(1);
        check("mood", "a waiting chat beats a working one", BuddyMood.Decide(2, 1, null, never, TimeSpan.Zero) == BuddyMood.Waiting, "");
        check("mood", "busy while any chat works", BuddyMood.Decide(1, 0, RunBadge.Failed, TimeSpan.FromSeconds(1), TimeSpan.Zero) == BuddyMood.Working, "");
        check("mood", "pleased right after a good finish", BuddyMood.Decide(0, 0, RunBadge.Done, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)) == BuddyMood.Happy
            && BuddyMood.Decide(0, 0, RunBadge.Idle, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)) == BuddyMood.Happy, "");
        check("mood", "sad right after a failure", BuddyMood.Decide(0, 0, RunBadge.Failed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)) == BuddyMood.Sad, "");
        check("mood", "a stop is neither pleased nor sad", BuddyMood.Decide(0, 0, RunBadge.Stopped, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)) == BuddyMood.Idle, "");
        check("mood", "the afterglow ends: idle", BuddyMood.Decide(0, 0, RunBadge.Done, BuddyMood.AfterglowFor, BuddyMood.AfterglowFor) == BuddyMood.Idle, "");
        check("mood", "nothing for SleepAfter: asleep", BuddyMood.Decide(0, 0, RunBadge.Done, BuddyMood.SleepAfter, BuddyMood.SleepAfter) == BuddyMood.Sleep
            && BuddyMood.Decide(0, 0, null, never, never) == BuddyMood.Sleep, "");
        check("mood", "a chat that works wakes her at once", BuddyMood.Decide(1, 0, null, never, never) == BuddyMood.Working, "");

        // ── the live-runs ledger ──
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-apitest-liveruns-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var mine = new LiveRunLedger(dir);
            mine.Began("chat-a");
            mine.Began("chat-b");
            mine.Ended("chat-a");
            string file = Path.Combine(dir, "live-runs.json");
            check("ledger", "a run that begins is written down, one that ends is struck off", File.Exists(file)
                && File.ReadAllText(file).Contains("chat-b") && !File.ReadAllText(file).Contains("chat-a"), File.Exists(file) ? File.ReadAllText(file) : "no file");
            check("ledger", "its own runs are no orphans (this CluadeX still runs them)", mine.TakeOrphans().Count == 0, "");

            // The process that wrote chat-b is gone: the next CluadeX sees it as stopped, once.
            File.WriteAllText(file, "{\"chat-b\":999999,\"chat-c\":424242}");
            var next = new LiveRunLedger(dir) { IsAlive = pid => pid == 424242 };
            var orphans = next.TakeOrphans();
            check("ledger", "after a crash: the run of the dead CluadeX is an orphan", orphans.SequenceEqual(new[] { "chat-b" }), string.Join(",", orphans));
            check("ledger", "a run another CluadeX still has is left alone", File.ReadAllText(file).Contains("chat-c") && !File.ReadAllText(file).Contains("chat-b"), File.ReadAllText(file));
            check("ledger", "an orphan is told once", next.TakeOrphans().Count == 0, "");
            next.IsAlive = _ => false;
            next.TakeOrphans();
            check("ledger", "with nothing left the file is removed", !File.Exists(file), "");
            File.WriteAllText(file, "not json");
            check("ledger", "a damaged file starts empty, no throw", new LiveRunLedger(dir).TakeOrphans().Count == 0, "");
            var mem = new LiveRunLedger(null) { IsAlive = _ => false };
            mem.Began("x");
            check("ledger", "no data root: memory only, nothing on disk", mem.TakeOrphans().Count == 0, "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // ── words, both languages ──
        var loc = new LocalizationService();
        var keys = new[] { "idle", "working", "waiting", "done", "failed", "stopped" }.Select(k => "sidebar.run." + k)
            .Concat(new[] { BuddyMood.Idle, BuddyMood.Working, BuddyMood.Waiting, BuddyMood.Happy, BuddyMood.Sad, BuddyMood.Sleep }.Select(m => "buddy.mood." + m))
            .Concat(new[] { "orch.splits.failed", "orch.splits.stopped" }).ToList();
        var missing = new List<string>();
        foreach (var lang in new[] { "en", "th" })
        {
            loc.SetLanguage(lang);
            foreach (var k in keys) if (loc.T(k) == k || string.IsNullOrWhiteSpace(loc.T(k))) missing.Add(lang + ":" + k);
        }
        loc.SetLanguage("en");
        check("loc", "every badge and mood tooltip has English and Thai", missing.Count == 0, string.Join(", ", missing));
        return Task.CompletedTask;
    }
}
