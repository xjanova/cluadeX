using CluadeX.Models;
using CluadeX.Services.Voice.Tts;

namespace VoiceTest;

/// <summary>
/// ReplyVoiceQueue against a silent stand-in output (nothing reaches the speakers): replies wait their turn and play in
/// order, a new one never cuts the one playing, back / forward / pause / stop, a reply clicked in the chat plays now and
/// the waiting ones follow it, a GPT-Live hold, failures, the history cap. Run: voicetest (all) or voicetest --queue.
/// </summary>
internal static partial class Program
{
    /// <summary>Records every call; <see cref="Finish"/> ends the sound that is "playing".</summary>
    sealed class FakeOutput : IReplyVoiceOutput
    {
        public readonly List<string> Log = new();
        public SpokenItem? Playing;
        public bool Pausable = true;
        public Func<SpokenItem, Task>? Making;   // a slow or failing synthesis
        public readonly Dictionary<int, int> Made = new();

        public event Action<SpokenItem, string?>? Ended;
        public bool CanPause => Pausable && Playing != null;

        public async Task PrepareAsync(SpokenItem item, CancellationToken ct)
        {
            if (item.Audio != null) return;
            Log.Add("make " + item.Preview);
            if (Making != null) await Making(item);
            ct.ThrowIfCancellationRequested();
            Made[item.Id] = Made.GetValueOrDefault(item.Id) + 1;
            item.Audio = new SpeechAudio([1, 2, 3], "audio/mpeg", ".mp3");
        }

        public bool Start(SpokenItem item) { Playing = item; Log.Add("start " + item.Preview); return true; }
        public void Pause() => Log.Add("pause");
        public void Resume() => Log.Add("resume");
        public void Stop() { if (Playing != null) Log.Add("stop " + Playing.Preview); Playing = null; }

        public void Finish(string? error = null)
        {
            var p = Playing ?? throw new InvalidOperationException("nothing playing");
            Playing = null;
            Ended?.Invoke(p, error);
        }

        public void Raise(SpokenItem item) => Ended?.Invoke(item, null);
        public List<string> Starts => Log.Where(l => l.StartsWith("start ")).Select(l => l[6..]).ToList();
    }

    static SpokenItem Say(string name, ChatMessage? m = null) => new(name, false, "claude", m, name);

    static async Task QueueTests()
    {
        Log("\n## The voice queue (ReplyVoiceQueue, silent stand-in output)");

        // 1. A new reply waits for the one playing; they play in order; nothing is cut.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            var a = Say("A"); var b = Say("B"); var c = Say("C");
            q.Enqueue(a);
            await Idle();
            Check("queue: the first reply plays at once", q.IsPlaying && ReferenceEquals(o.Playing, a), string.Join(", ", o.Log));
            q.Enqueue(b);
            q.Enqueue(c);
            await Idle();
            Check("queue: a new reply does not cut the one playing", ReferenceEquals(o.Playing, a) && !o.Log.Contains("stop A"), string.Join(", ", o.Log));
            Check("queue: two replies wait", q.Waiting == 2 && q.Position == "1 / 3", $"waiting {q.Waiting} · {q.Position}");
            o.Finish(); await Idle();
            o.Finish(); await Idle();
            o.Finish(); await Idle();
            Check("queue: they play in the order they came", string.Join("", o.Starts) == "ABC", string.Join(",", o.Starts));
            Check("queue: idle after the last, all heard", q.State == ReplyVoiceQueue.PlayState.Idle && q.Items.All(i => i.Heard) && q.Waiting == 0, q.State.ToString());
            Check("queue: the next reply was made ahead, once each", o.Made.Values.All(n => n == 1) && o.Made.Count == 3,
                string.Join(",", o.Made.Select(kv => $"{kv.Key}:{kv.Value}")));
        }

        // 2. Back and forward through what was read.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            foreach (var n in new[] { "A", "B", "C" }) q.Enqueue(Say(n));
            await Idle();
            o.Finish(); await Idle(); o.Finish(); await Idle(); o.Finish(); await Idle();
            o.Log.Clear();
            q.Previous(); await Idle();
            Check("back: the one before the last plays", o.Playing?.Preview == "B" && q.Position == "2 / 3", string.Join(", ", o.Log));
            q.Previous(); await Idle();
            Check("back: again, the first", o.Playing?.Preview == "A", string.Join(", ", o.Log));
            Check("back: going back costs nothing (the audio was kept)", !o.Log.Any(l => l.StartsWith("make ")), string.Join(", ", o.Log));
            q.Next(); await Idle();
            Check("forward: skips to the next one", o.Playing?.Preview == "B" && o.Log.Contains("stop A"), string.Join(", ", o.Log));
            o.Finish(); await Idle();
            Check("forward: after it ends, the next one plays by itself (C was played again by going back)", o.Playing?.Preview == "C", string.Join(", ", o.Log));
            q.Next(); await Idle();
            Check("forward at the end: silence", o.Playing == null && q.State == ReplyVoiceQueue.PlayState.Idle, q.State.ToString());
            q.TogglePause(); await Idle();
            Check("play after the end: the last one again", o.Playing?.Preview == "C", string.Join(", ", o.Log));
        }

        // 3. Pause holds; a reply that comes meanwhile waits; resume carries on, then it plays.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            q.Enqueue(Say("A")); await Idle();
            q.TogglePause(); await Idle();
            Check("pause: the sound is held where it is", q.IsPaused && o.Log.Last() == "pause", string.Join(", ", o.Log));
            q.Enqueue(Say("B")); await Idle();
            Check("pause: a new reply waits, does not start", q.IsPaused && o.Playing?.Preview == "A" && !o.Starts.Contains("B"), string.Join(", ", o.Log));
            q.TogglePause(); await Idle();
            Check("resume: carries on from where it was", q.IsPlaying && o.Log.Last() == "resume", string.Join(", ", o.Log));
            o.Finish(); await Idle();
            Check("resume: then the waiting one", o.Playing?.Preview == "B", string.Join(", ", o.Log));

            // The mic window cannot pause: pause stops it, play starts it again.
            o.Pausable = false;
            q.TogglePause(); await Idle();
            Check("pause (mic window): stops the sound", q.IsPaused && o.Playing == null && o.Log.Last() == "stop B", string.Join(", ", o.Log));
            q.TogglePause(); await Idle();
            Check("play (mic window): the same reply from its start", q.IsPlaying && o.Playing?.Preview == "B" && o.Log.Last() == "start B", string.Join(", ", o.Log));
        }

        // 4. Stop drops what was waiting; the next reply plays on its own.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            foreach (var n in new[] { "A", "B", "C" }) q.Enqueue(Say(n));
            await Idle();
            q.Stop(); await Idle();
            Check("stop: silence at once", o.Playing == null && q.State == ReplyVoiceQueue.PlayState.Idle && o.Log.Contains("stop A"), string.Join(", ", o.Log));
            Check("stop: the waiting replies are dropped", q.Waiting == 0 && q.Items.Count == 3, $"waiting {q.Waiting}");
            q.Enqueue(Say("D")); await Idle();
            Check("stop: a later reply plays by itself (not the dropped ones)", o.Playing?.Preview == "D", string.Join(", ", o.Starts));
            o.Finish(); await Idle();
            Check("stop: and nothing after it", o.Playing == null, string.Join(", ", o.Starts));
        }

        // 5. A reply clicked in the chat (its bubble or ▶): plays now, and what was waiting follows it.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            var mA = new ChatMessage { Role = MessageRole.Assistant, Content = "A" };
            var mOld = new ChatMessage { Role = MessageRole.Assistant, Content = "Z" };
            q.Enqueue(Say("A", mA)); q.Enqueue(Say("B")); await Idle();
            Check("bubble: the bubble being read is lit", mA.IsVoiceCurrent && mA.IsVoicePlaying, "");
            var z = Say("Z", mOld);
            q.Play(z); await Idle();
            Check("bubble: the clicked reply plays now", o.Playing?.Preview == "Z" && mOld.IsVoicePlaying && !mA.IsVoiceCurrent, string.Join(", ", o.Log));
            Check("bubble: it goes right after the one it interrupted", string.Join("", q.Items.Select(i => i.Preview)) == "AZB", string.Join("", q.Items.Select(i => i.Preview)));
            o.Finish(); await Idle();
            Check("bubble: then the reply that was waiting", o.Playing?.Preview == "B", string.Join(", ", o.Starts));
            Check("bubble: ItemOf finds a bubble's reply", ReferenceEquals(q.ItemOf(mOld), z), "");
            q.Play(q.ItemOf(mA)!); await Idle();
            Check("bubble: one already in the list plays again (no copy)", o.Playing?.Preview == "A" && q.Items.Count == 3, string.Join(", ", o.Starts));
            q.Play(q.ItemOf(mA)!); await Idle();
            Check("bubble: a click on the one playing pauses it", q.IsPaused, q.State.ToString());
            q.Clear(); await Idle();
            Check("close: the box empties, nothing plays, no bubble stays lit",
                q.Items.Count == 0 && o.Playing == null && !q.HasItems && !mA.IsVoiceCurrent && !mOld.IsVoiceCurrent, "");
        }

        // 6. A GPT-Live call: replies wait, then play in order when it ends.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o) { Hold = true };
            q.Enqueue(Say("A")); q.Enqueue(Say("B")); await Idle();
            Check("hold: nothing plays during a live call", o.Playing == null && q.Waiting == 2, $"waiting {q.Waiting}");
            q.Hold = false; await Idle();
            Check("hold: released, they play in order", o.Playing?.Preview == "A", string.Join(", ", o.Starts));
            o.Finish(); await Idle();
            Check("hold: …then the second", o.Playing?.Preview == "B", string.Join(", ", o.Starts));
        }

        // 7. A slow voice: the next reply is waited for, not made twice; a stale end is ignored; a failure stops the queue.
        {
            var o = new FakeOutput();
            var gate = new TaskCompletionSource();
            var q = new ReplyVoiceQueue(o);
            var a = Say("A"); var b = Say("B");
            q.Enqueue(a); await Idle();
            o.Making = it => it.Preview == "B" ? gate.Task : Task.CompletedTask;
            q.Enqueue(b); await Idle();
            o.Finish(); await Idle();
            Check("slow: while B is still being made it says so", q.IsPreparing && o.Playing == null, q.State.ToString());
            gate.SetResult(); await Idle(50);
            Check("slow: B plays once made — made once, not twice", o.Playing?.Preview == "B" && o.Made.GetValueOrDefault(b.Id) == 1,
                $"made {o.Made.GetValueOrDefault(b.Id)}× · {string.Join(", ", o.Log)}");
            o.Raise(a); await Idle();
            Check("stale: an end for a reply that is not playing is ignored", o.Playing?.Preview == "B" && q.IsPlaying, string.Join(", ", o.Starts));

            string? failed = null;
            q.Failed += (_, e) => failed = e;
            o.Making = it => it.Preview == "C" ? Task.FromException(new InvalidOperationException("no key")) : Task.CompletedTask;
            q.Enqueue(Say("C")); q.Enqueue(Say("D")); await Idle();
            o.Finish(); await Idle(50);
            Check("fail: the error reaches the status line", failed == "no key" && q.State == ReplyVoiceQueue.PlayState.Idle, failed ?? "(none)");
            Check("fail: the queue stops there (the rest would fail the same way)", o.Playing == null, string.Join(", ", o.Starts));
            o.Making = null;
            q.TogglePause(); await Idle(50);
            Check("fail: play goes on with the next waiting one", o.Playing?.Preview == "D", string.Join(", ", o.Starts));
        }

        // 8. A long session: the list keeps the last MaxItems, audio only near the one playing.
        {
            var o = new FakeOutput();
            var q = new ReplyVoiceQueue(o);
            for (int i = 0; i < ReplyVoiceQueue.MaxItems + 12; i++)
            {
                q.Enqueue(Say("R" + i)); await Idle();
                o.Finish(); await Idle();
            }
            Check("history: capped", q.Items.Count == ReplyVoiceQueue.MaxItems, $"{q.Items.Count}");
            Check("history: the newest kept", q.Items[^1].Preview == "R" + (ReplyVoiceQueue.MaxItems + 11), q.Items[^1].Preview);
            q.Previous(); await Idle();
            int withAudio = q.Items.Count(i => i.Audio != null);
            Check("history: audio kept only near the one playing", withAudio <= ReplyVoiceQueue.MaxCachedAudio + 1, $"{withAudio} with audio");
        }
    }
}
