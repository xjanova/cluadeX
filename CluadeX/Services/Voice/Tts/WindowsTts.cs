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

    /// <summary>
    /// Whether an installed voice reads <paramref name="lang"/> ("th-TH", "th"). Windows' Thai voice is a OneCore one,
    /// which System.Speech does not see. An English voice given Thai text skips every Thai letter and makes an empty
    /// WAV, so a Thai reply was silent without a word (2026-11-09: Zira, 46 bytes, 0 s).
    /// </summary>
    public static bool Speaks(string lang) => Speaks(Voices(), lang);

    public static bool Speaks(IEnumerable<Voice> voices, string lang) =>
        voices.Any(v => SameLanguage(v.Culture, lang));

    private static bool SameLanguage(string? culture, string lang) =>
        !string.IsNullOrEmpty(culture) && culture.StartsWith(lang[..Math.Min(2, lang.Length)], StringComparison.OrdinalIgnoreCase);

    /// <summary>The voice to read <paramref name="lang"/> with: <paramref name="chosen"/> when it reads that language,
    /// else an installed one that does, else <paramref name="chosen"/> (null = the default).</summary>
    public static string? PickVoice(IReadOnlyList<Voice> installed, string? chosen, string lang)
    {
        var own = installed.FirstOrDefault(v => !string.IsNullOrWhiteSpace(chosen) && v.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        if (own != null && SameLanguage(own.Culture, lang)) return own.Name;
        return installed.FirstOrDefault(v => SameLanguage(v.Culture, lang))?.Name ?? own?.Name;
    }

    /// <summary>A WAV with no sound in it: only the header, or every sample under the noise floor.</summary>
    public static bool IsSilent(byte[] wav)
    {
        if (wav.Length <= 44 + 2205) return true;   // header + under 50 ms at 22 kHz 16-bit
        for (int i = 44; i + 1 < wav.Length; i += 2)
            if (Math.Abs((int)BitConverter.ToInt16(wav, i)) > 300) return false;
        return true;
    }

    /// <summary>Speak <paramref name="text"/> into a WAV, in the chosen voice when it reads <paramref name="lang"/>, else
    /// in an installed voice that does. Throws <see cref="WindowsVoiceMissingException"/> when none reads it, or when
    /// the voice made no sound.</summary>
    public static Task<byte[]> SpeakAsync(string text, string lang, string? voice, int rate, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var s = new SpeechSynthesizer();
            var installed = s.GetInstalledVoices().Where(v => v.Enabled)
                .Select(v => new Voice(v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name ?? "", v.VoiceInfo.Gender.ToString())).ToList();
            if (!Speaks(installed, lang)) throw new WindowsVoiceMissingException(lang);
            var pick = PickVoice(installed, voice, lang);
            if (pick != null) s.SelectVoice(pick);
            s.Rate = Math.Clamp(rate, -10, 10);
            using var ms = new MemoryStream();
            s.SetOutputToWaveStream(ms);
            using var reg = ct.Register(() => { try { s.SpeakAsyncCancelAll(); } catch { } });
            s.Speak(text);
            s.SetOutputToNull();
            ct.ThrowIfCancellationRequested();
            var wav = ms.ToArray();
            if (IsSilent(wav)) throw new WindowsVoiceMissingException(lang);
            return wav;
        }, ct);
}

/// <summary>No installed Windows voice reads this language (or the one picked made no sound).</summary>
public sealed class WindowsVoiceMissingException(string lang) : Exception("no Windows voice for " + lang)
{
    public string Language { get; } = lang;
}
