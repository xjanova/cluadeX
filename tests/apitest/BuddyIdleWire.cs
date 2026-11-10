// น้อง CluadeX idle (owner 2026-11-10: "idle อวาต้า น้อยไป เธอหลับบ่อยเกินไป"). Pure: when she may fall asleep (only after a long
// quiet with no run AND no mouse or keyboard input), that any activity wakes her, and that her idle gestures vary — never
// the same one twice running, none of the last few again, and over time all of them.
using CluadeX.Models;
using CluadeX.Services;

namespace ApiTest;

internal static class BuddyIdleWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · the buddy's sleep clock and idle variety");
        var never = TimeSpan.FromHours(1);
        var after = BuddyMood.SleepAfter;
        var justUnder = after - TimeSpan.FromSeconds(1);

        // ── sleep only after a genuinely long quiet ──
        check("sleep", "the threshold is minutes, not one minute", after >= TimeSpan.FromMinutes(5), $"{after}");
        bool anyEarly = false;
        for (int s = 0; s < (int)after.TotalSeconds; s += 5)
        {
            var t = TimeSpan.FromSeconds(s);
            anyEarly |= BuddyMood.Decide(0, 0, null, never, t, never) == BuddyMood.Sleep
                     || BuddyMood.Decide(0, 0, null, never, never, t) == BuddyMood.Sleep
                     || BuddyMood.Decide(0, 0, null, never, t, t) == BuddyMood.Sleep;
        }
        check("sleep", "never before the threshold (runs quiet, input quiet, or both)", !anyEarly, "");
        check("sleep", "1 s under the threshold: still awake", BuddyMood.Decide(0, 0, null, never, justUnder, justUnder) == BuddyMood.Idle, "");
        check("sleep", "both quiet for the threshold: asleep", BuddyMood.Decide(0, 0, RunBadge.Done, never, after, after) == BuddyMood.Sleep, "");

        // ── any activity resets it ──
        check("wake", "the mouse or keyboard a moment ago keeps her awake however long nothing ran",
              BuddyMood.Decide(0, 0, null, never, never, TimeSpan.FromSeconds(2)) == BuddyMood.Idle, "");
        check("wake", "a run a moment ago keeps her awake however long nobody typed",
              BuddyMood.Decide(0, 0, null, never, TimeSpan.FromSeconds(2), never) == BuddyMood.Idle, "");
        check("wake", "a chat that works wakes her at once", BuddyMood.Decide(1, 0, null, never, never, never) == BuddyMood.Working, "");
        check("wake", "a chat that waits for the owner wakes her at once", BuddyMood.Decide(1, 1, null, never, never, never) == BuddyMood.Waiting, "");
        check("wake", "input unknown (null): the run clock alone decides, as before",
              BuddyMood.Decide(0, 0, null, never, after) == BuddyMood.Sleep && BuddyMood.Decide(0, 0, null, never, justUnder) == BuddyMood.Idle, "");

        // ── idle picks vary ──
        var pool = Enumerable.Range(0, 40).Select(i => "g" + i).ToArray();
        var picker = new VariedPicker(new Random(7));
        var picks = Enumerable.Range(0, 2000).Select(_ => picker.Next(pool)!).ToList();
        int memory = VariedPicker.MemoryFor(pool.Length);
        bool immediate = false, soon = false;
        for (int i = 1; i < picks.Count; i++)
        {
            immediate |= picks[i] == picks[i - 1];
            for (int k = 1; k <= memory && k <= i; k++) soon |= picks[i] == picks[i - k];
        }
        check("vary", "never the same gesture twice running", !immediate, "");
        check("vary", $"none of the last {memory} comes back", memory >= 10 && !soon, $"memory {memory}");
        check("vary", "every gesture is used", picks.Distinct().Count() == pool.Length, $"{picks.Distinct().Count()}/{pool.Length}");
        var counts = picks.GroupBy(p => p).Select(g => g.Count()).ToList();
        check("vary", "roughly even (none hogs or starves)", counts.Min() >= 25 && counts.Max() <= 80, $"{counts.Min()}..{counts.Max()}");

        // the pool shrinks or grows (lunch/night extras, a clip that failed): still no immediate repeat
        var p2 = new VariedPicker(new Random(3));
        string? last = null; bool rep = false, unusable = false;
        for (int i = 0; i < 500; i++)
        {
            var now = i % 3 == 0 ? pool.Take(5).ToArray() : pool;
            string? n = p2.Next(now, name => name != "g1", not: last);
            rep |= n == last;
            unusable |= n == "g1";
            last = n;
        }
        check("vary", "a changing pool: still no immediate repeat", !rep, "");
        check("vary", "an unusable clip is never picked", !unusable, "");

        var two = new VariedPicker(new Random(1));
        var alt = Enumerable.Range(0, 20).Select(_ => two.Next(new[] { "a", "b" })).ToList();
        check("vary", "two to choose from: they alternate", alt.Zip(alt.Skip(1)).All(z => z.First != z.Second), string.Join("", alt));
        var one = new VariedPicker();
        check("vary", "one to choose from: that one, every time", one.Next(new[] { "x" }) == "x" && one.Next(new[] { "x" }, not: "x") == "x", "");
        check("vary", "nothing usable: none", new VariedPicker().Next(new[] { "x" }, _ => false) == null && new VariedPicker().Next(Array.Empty<string>()) == null, "");
        check("vary", "the one on screen is not picked when there is another",
              Enumerable.Range(0, 50).All(_ => new VariedPicker().Next(new[] { "idle", "wink" }, not: "idle") == "wink"), "");
        return Task.CompletedTask;
    }
}
