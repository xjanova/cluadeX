using System.IO;
using System.Media;

namespace CluadeX.Services.Voice;

/// <summary>What a box waiting for the owner sounds like.</summary>
public enum ChimeKind
{
    /// <summary>Her question card (ask_user) or a permission prompt: one soft "ting".</summary>
    Ask,
    /// <summary>The red prompt (a delete aimed at a drive, the root or a home folder): a sharper two-tone alert, twice.</summary>
    Danger,
}

/// <summary>
/// A short sound when a box in a chat waits for the owner's answer (owner 2026-11-11: "มีเอฟเฟคเสียง ติ๊ง เมื่อต้องตอบ
/// คำถามจากกล่อง หรือกล่องแดง แจ้งเตือนให้เหมาะสม"). The sounds are made here (a WAV in memory, no file to ship) and
/// played with the system's wave player, beside a reply being read aloud (that one is WPF's MediaPlayer): a chime never
/// cuts her voice. Several boxes at once (sub-agents asking together) ring once: the same kind is not played again
/// within <see cref="Gap"/>.
/// </summary>
public static class AttentionChime
{
    /// <summary>The same kind is not played again sooner than this.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1500);

    /// <summary>A test harness takes the sound instead of the speaker (null = the speaker).</summary>
    public static Action<ChimeKind>? Sink { get; set; }

    private const int Rate = 44100;
    private static readonly Dictionary<ChimeKind, DateTime> _last = new();
    private static readonly Lazy<byte[]> _ask = new(() => Wav(AskNotes()));
    private static readonly Lazy<byte[]> _danger = new(() => Wav(DangerNotes()));
    // Kept so the stream lives while the player's own thread reads it.
    private static SoundPlayer? _player;

    /// <summary>Ring <paramref name="kind"/> now, unless the same kind rang within <see cref="Gap"/>. False = not played.
    /// Never throws: a PC with no sound device is just silent.</summary>
    public static bool Play(ChimeKind kind, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_last)
        {
            if (_last.TryGetValue(kind, out var at) && now - at < Gap && now >= at) return false;
            _last[kind] = now;
        }
        if (Sink is { } sink) { sink(kind); return true; }
        try
        {
            var player = new SoundPlayer(new MemoryStream(WavOf(kind), writable: false));
            _player = player;
            player.Play();   // its own thread: the UI does not wait
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Chime] " + ex.Message); }
        return true;
    }

    /// <summary>Forget when each kind last rang (tests).</summary>
    public static void Reset() { lock (_last) _last.Clear(); }

    /// <summary>The sound of <paramref name="kind"/>: a 16-bit mono PCM WAV.</summary>
    public static byte[] WavOf(ChimeKind kind) => kind == ChimeKind.Danger ? _danger.Value : _ask.Value;

    /// <summary>One note: when it starts (s), its pitch (Hz), how fast it fades (s) and how loud (0-1).</summary>
    private readonly record struct Note(double At, double Hz, double Fade, double Gain);

    // "ติ๊ง": E6 with a quiet bell partial, a quick fade — friendly, over in half a second.
    private static Note[] AskNotes() => [new(0, 1318.5, 0.13, 0.30)];

    // The red prompt: high-low (D6 → A5), twice — an alert that is not the ting, still short (under a second).
    private static Note[] DangerNotes() =>
    [
        new(0.00, 1174.7, 0.07, 0.38), new(0.16, 880.0, 0.09, 0.38),
        new(0.42, 1174.7, 0.07, 0.38), new(0.58, 880.0, 0.09, 0.38),
    ];

    private static byte[] Wav(Note[] notes)
    {
        double length = notes.Max(n => n.At + n.Fade * 6) + 0.02;
        int samples = (int)(length * Rate);
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)Rate, v = 0;
            foreach (var n in notes)
            {
                double dt = t - n.At;
                if (dt < 0) continue;
                double attack = Math.Min(1, dt / 0.004);                 // 4 ms in: no click
                double env = attack * Math.Exp(-dt / n.Fade);
                if (env < 1e-4) continue;
                // the note, a soft octave and a bell's inharmonic partial that fades faster
                v += n.Gain * env * (Math.Sin(2 * Math.PI * n.Hz * dt)
                                     + 0.25 * Math.Sin(2 * Math.PI * n.Hz * 2 * dt)
                                     + 0.12 * Math.Exp(-dt / (n.Fade / 3)) * Math.Sin(2 * Math.PI * n.Hz * 2.76 * dt));
            }
            double tail = Math.Min(1, (length - t) / 0.01);                // 10 ms out
            pcm[i] = (short)Math.Clamp(v * tail * 0.75 * short.MaxValue, short.MinValue, short.MaxValue);
        }
        using var ms = new MemoryStream(44 + samples * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
