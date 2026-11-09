using System.IO;
using System.Speech.Synthesis;

namespace CluadeX.Services.Voice.Tts;

/// <summary>
/// Windows' own offline voices (System.Speech / SAPI): free, no network, no key. Which languages exist depends on the
/// voices installed (Settings › Time &amp; language › Speech) — many PCs have English only.
/// </summary>
public static class WindowsTts
{
    public sealed record Voice(string Name, string Culture, string Gender);

    /// <summary>The installed, enabled voices. Empty when speech is not available here.</summary>
    public static IReadOnlyList<Voice> Voices()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices().Where(v => v.Enabled)
                .Select(v => new Voice(v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name ?? "", v.VoiceInfo.Gender.ToString()))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Speak <paramref name="text"/> into a WAV. <paramref name="voice"/> empty → a voice for <paramref name="lang"/>
    /// when one is installed, else the default.</summary>
    public static Task<byte[]> SpeakAsync(string text, string lang, string? voice, int rate, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var s = new SpeechSynthesizer();
            var installed = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            var pick = installed.FirstOrDefault(v => !string.IsNullOrWhiteSpace(voice) && v.Name.Equals(voice, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(v => v.Culture?.Name.StartsWith(lang[..Math.Min(2, lang.Length)], StringComparison.OrdinalIgnoreCase) == true);
            if (pick != null) s.SelectVoice(pick.Name);
            s.Rate = Math.Clamp(rate, -10, 10);
            using var ms = new MemoryStream();
            s.SetOutputToWaveStream(ms);
            using var reg = ct.Register(() => { try { s.SpeakAsyncCancelAll(); } catch { } });
            s.Speak(text);
            s.SetOutputToNull();
            ct.ThrowIfCancellationRequested();
            return ms.ToArray();
        }, ct);
}
