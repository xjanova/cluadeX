// The chime when a box waits for the owner's answer (owner 2026-11-11: "มีเอฟเฟคเสียง ติ๊ง เมื่อต้องตอบคำถามจากกล่อง หรือ
// กล่องแดง แจ้งเตือนให้เหมาะสม"): the two sounds are real WAVs, short, not clipped, without a click at either end, and
// different; the same kind rings once per AttentionChime.Gap. Offline, silent (a sink takes the sound).
using CluadeX.Services.Voice;

namespace ApiTest;

internal static class ChimeWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Chime — a ting when a box waits for the owner");

        static (int rate, short[] pcm) Read(byte[] wav)
        {
            int rate = BitConverter.ToInt32(wav, 24);
            int bytes = BitConverter.ToInt32(wav, 40);
            var pcm = new short[bytes / 2];
            Buffer.BlockCopy(wav, 44, pcm, 0, bytes);
            return (rate, pcm);
        }
        foreach (var kind in new[] { ChimeKind.Ask, ChimeKind.Danger })
        {
            var wav = AttentionChime.WavOf(kind);
            bool header = wav.Length > 44 && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(wav, 8, 4) == "WAVE"
                && BitConverter.ToInt16(wav, 20) == 1 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16
                && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToInt32(wav, 40) == wav.Length - 44;
            var (rate, pcm) = Read(wav);
            double secs = pcm.Length / (double)rate;
            double peak = pcm.Max(s => Math.Abs((int)s)) / 32768.0;
            int edge = rate / 1000;   // 1 ms at each end
            int startMax = pcm.Take(edge).Max(s => Math.Abs((int)s)), endMax = pcm.TakeLast(edge).Max(s => Math.Abs((int)s));
            // it rises from silence (a 4 ms ramp) and has faded out at the end: no step a speaker would click on
            bool quietEnds = Math.Abs((int)pcm[0]) < 100 && Math.Abs((int)pcm[^1]) < 100 && startMax < peak * 32768 * 0.35 && endMax < 400;
            check("chime", $"{kind}: a 16-bit mono PCM WAV", header, $"{wav.Length} B");
            check("chime", $"{kind}: short ({(kind == ChimeKind.Ask ? "under 1 s" : "under 1.5 s")}), heard but not loud (peak 15-60 %)",
                secs > 0.2 && secs < (kind == ChimeKind.Ask ? 1.0 : 1.5) && peak > 0.15 && peak < 0.6, $"{secs:0.00} s · peak {peak:P0}");
            check("chime", $"{kind}: no click where it starts or ends", quietEnds, $"first {pcm[0]} · 1st ms max {startMax} · last ms max {endMax} · last {pcm[^1]}");
        }
        check("chime", "the red prompt's alert is not the ting (longer, more than one note)",
            AttentionChime.WavOf(ChimeKind.Danger).Length > AttentionChime.WavOf(ChimeKind.Ask).Length * 1.3, "");

        var rang = new List<ChimeKind>();
        var before = AttentionChime.Sink;
        AttentionChime.Sink = k => rang.Add(k);
        try
        {
            AttentionChime.Reset();
            var t0 = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            bool first = AttentionChime.Play(ChimeKind.Ask, t0);
            bool again = AttentionChime.Play(ChimeKind.Ask, t0.AddMilliseconds(300));
            bool other = AttentionChime.Play(ChimeKind.Danger, t0.AddMilliseconds(400));
            bool later = AttentionChime.Play(ChimeKind.Ask, t0 + AttentionChime.Gap + TimeSpan.FromMilliseconds(1));
            check("chime", "several boxes at once ring once; the red alert is not held back by a ting; after the gap it rings again",
                first && !again && other && later && rang.SequenceEqual(new[] { ChimeKind.Ask, ChimeKind.Danger, ChimeKind.Ask }), string.Join(",", rang));
            check("chime", "a clock set back does not silence it", AttentionChime.Play(ChimeKind.Ask, t0.AddSeconds(-30)), "");
        }
        finally { AttentionChime.Sink = before; AttentionChime.Reset(); }
        check("chime", "on by default (AppSettings.AttentionChime)", new CluadeX.Models.AppSettings().AttentionChime, "");
        return Task.CompletedTask;
    }
}
