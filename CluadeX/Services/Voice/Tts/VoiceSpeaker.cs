using System.Net.Http;
using CluadeX.Models;

namespace CluadeX.Services.Voice.Tts;

/// <summary>Synthesized speech ready to play.</summary>
public sealed record SpeechAudio(byte[] Bytes, string Mime, string Extension);

/// <summary>The browser voice, rate and pitch the mic window reads a reply with (the free engine).</summary>
public sealed record BrowserSpeech(string? Voice, double Rate, double Pitch);

/// <summary>
/// Turns a reply into speech with the engine chosen in Settings › Voice, in the voice and style of the girl who wrote
/// it. The browser engine is not synthesized here — the mic window speaks it (<see cref="BrowserStyle"/>); the other
/// three return audio bytes for the mic window or the in-app player.
/// </summary>
public sealed class VoiceSpeaker
{
    private readonly Func<AppSettings> _settings;
    private readonly string? _elevenBase;
    private readonly HttpClient? _http;

    /// <summary>The models the settings page fetched last — their capabilities decide which fine settings are sent.</summary>
    public static IReadOnlyList<ElevenModel>? FetchedModels { get; set; }

    public VoiceSpeaker(Func<AppSettings> settings, string? elevenLabsBase = null, HttpClient? http = null)
    {
        _settings = settings;
        _elevenBase = elevenLabsBase;
        _http = http;
    }

    public static bool NeedsMicWindow(string engine) => engine == VoiceSettings.Browser;

    public static string EngineOf(AppSettings s) =>
        Array.IndexOf(VoiceSettings.Engines, s.Voice?.Engine) >= 0 ? s.Voice!.Engine : VoiceSettings.Browser;

    /// <summary>The OpenAI key and endpoint from AI Provider › OpenAI (entered once, shared with GPT-Live).</summary>
    public static (string Key, string Base) OpenAiKey(AppSettings s)
    {
        s.ProviderConfigs.TryGetValue("OpenAI", out var openAi);
        string key = openAi?.ApiKey?.Trim() ?? "";
        string apiBase = string.IsNullOrWhiteSpace(openAi?.BaseUrl) ? OpenAiTtsClient.DefaultBase : openAi!.BaseUrl!.Trim();
        return (key, apiBase);
    }

    public static ElevenModel ModelInfo(string id)
    {
        id = ElevenLabsClient.Successor(id);
        return FetchedModels?.FirstOrDefault(m => m.Id == id)
            ?? ElevenLabsClient.KnownModels.FirstOrDefault(m => m.Id == id)
            // A model newer than this build: send only what every model takes.
            ?? new ElevenModel(id, id, "", true, false, false, false, false, 5000, 1, []);
    }

    private VoiceStyle StyleOf(VoiceSettings v, string? girl) => v.PersonaVoices ? VoicePersona.For(girl) : VoicePersona.Neutral;

    public BrowserSpeech BrowserStyle(string? girl)
    {
        var v = _settings().Voice ?? new VoiceSettings();
        var style = StyleOf(v, girl);
        string? own = v.PersonaVoices ? v.For(girl)?.BrowserVoice : null;
        string? voice = !string.IsNullOrWhiteSpace(own) ? own : string.IsNullOrWhiteSpace(v.BrowserVoice) ? null : v.BrowserVoice;
        return new BrowserSpeech(voice, Math.Clamp(v.BrowserRate * style.RateFactor, 0.5, 2), Math.Clamp(v.BrowserPitch * style.PitchFactor, 0.5, 2));
    }

    /// <summary>The ElevenLabs request for one reply — split out so the tests can check every field.</summary>
    public static (string Text, string Voice, string Model, ElevenLabsClient.SpeechOptions Options) ElevenRequest(
        VoiceSettings v, string text, bool thai, string? girl)
    {
        var style = v.PersonaVoices ? VoicePersona.For(girl) : VoicePersona.Neutral;
        var model = ModelInfo(v.ElevenLabsModel);
        string? own = v.PersonaVoices ? v.For(girl)?.ElevenLabsVoiceId : null;
        string voice = !string.IsNullOrWhiteSpace(own) ? own!.Trim()
            : !string.IsNullOrWhiteSpace(v.ElevenLabsVoiceId) ? v.ElevenLabsVoiceId.Trim() : ElevenLabsClient.FallbackVoiceId;

        double stability = Math.Clamp(v.Stability + style.StabilityShift, 0, 1);
        if (model.StabilitySteps) stability = stability < 0.25 ? 0 : stability < 0.75 ? 0.5 : 1;
        double styleAmt = Math.Clamp(v.Style + style.StyleShift, 0, 1);
        double speed = Math.Clamp(v.Speed * style.SpeedFactor, 0.7, 1.2);

        string lang = thai ? "th" : "en";
        // language_code only where the model says it speaks it (multilingual v2 ignores it; an unknown one may refuse it).
        string? code = model.Speaks(lang) && !model.Id.StartsWith("eleven_multilingual", StringComparison.Ordinal) ? lang : null;

        string said = text;
        if (model.AudioTags && v.AudioTags && style.AudioTag.Length > 0) said = style.AudioTag + " " + said;
        if (said.Length > model.MaxChars) said = said[..model.MaxChars];

        var options = new ElevenLabsClient.SpeechOptions(stability, Math.Clamp(v.Similarity, 0, 1), styleAmt, v.SpeakerBoost, speed,
            code, v.TextNormalization, string.IsNullOrWhiteSpace(v.OutputFormat) ? "mp3_44100_128" : v.OutputFormat,
            SendStyle: model.CanUseStyle, SendBoost: model.CanUseSpeakerBoost);
        return (said, voice, model.Id, options);
    }

    /// <summary>The OpenAI request for one reply.</summary>
    public static (string Model, string Voice, double Speed, string Instructions) OpenAiRequest(VoiceSettings v, string? girl)
    {
        var style = v.PersonaVoices ? VoicePersona.For(girl) : VoicePersona.Neutral;
        string model = string.IsNullOrWhiteSpace(v.OpenAiModel) ? OpenAiTtsClient.KnownModels[0] : v.OpenAiModel.Trim();
        string? own = v.PersonaVoices ? v.For(girl)?.OpenAiVoice : null;
        string voice = !string.IsNullOrWhiteSpace(own) ? own! : string.IsNullOrWhiteSpace(v.OpenAiVoice) ? "marin" : v.OpenAiVoice;
        if (!OpenAiTtsClient.VoicesFor(model).Contains(voice)) voice = OpenAiTtsClient.TakesInstructions(model) ? "marin" : "nova";
        string instructions = string.Join(" ", new[] { style.Instructions, v.OpenAiInstructions?.Trim() ?? "" }.Where(x => x.Length > 0));
        // gpt-4o-mini-tts reads the mood from the instructions; the tts-1 models only have a speed.
        double speed = Math.Clamp(v.OpenAiSpeed * (OpenAiTtsClient.TakesInstructions(model) ? 1 : style.SpeedFactor), 0.25, 4);
        return (model, voice, speed, instructions);
    }

    /// <summary>Speak <paramref name="text"/> with the chosen engine (not the browser one). Throws with a short message
    /// the user can act on (no key, the API's own error).</summary>
    public async Task<SpeechAudio> SynthesizeAsync(string text, bool thai, string? girl, CancellationToken ct = default)
    {
        var s = _settings();
        var v = s.Voice ?? new VoiceSettings();
        switch (EngineOf(s))
        {
            case VoiceSettings.ElevenLabs:
            {
                string key = v.ElevenLabsApiKey?.Trim() ?? "";
                if (key.Length == 0) throw new VoiceKeyMissingException(VoiceSettings.ElevenLabs);
                var (said, voice, model, options) = ElevenRequest(v, text, thai, girl);
                var bytes = await new ElevenLabsClient(key, _elevenBase, _http).SpeakAsync(said, voice, model, options, ct).ConfigureAwait(false);
                bool pcm = options.OutputFormat.StartsWith("pcm", StringComparison.Ordinal);
                return pcm ? new SpeechAudio(WavOfPcm(bytes, RateOf(options.OutputFormat)), "audio/wav", ".wav")
                    : options.OutputFormat.StartsWith("opus", StringComparison.Ordinal) ? new SpeechAudio(bytes, "audio/ogg", ".ogg")
                    : new SpeechAudio(bytes, "audio/mpeg", ".mp3");
            }
            case VoiceSettings.OpenAI:
            {
                var (key, apiBase) = OpenAiKey(s);
                if (key.Length == 0) throw new VoiceKeyMissingException(VoiceSettings.OpenAI);
                var (model, voice, speed, instructions) = OpenAiRequest(v, girl);
                var bytes = await new OpenAiTtsClient(key, apiBase, _http).SpeakAsync(text, model, voice, speed, instructions, ct).ConfigureAwait(false);
                return new SpeechAudio(bytes, "audio/mpeg", ".mp3");
            }
            case VoiceSettings.Windows:
            {
                var style = StyleOf(v, girl);
                string? own = v.PersonaVoices ? v.For(girl)?.WindowsVoice : null;
                int rate = Math.Clamp(v.WindowsRate + (int)Math.Round((style.RateFactor - 1) * 10), -10, 10);
                var bytes = await WindowsTts.SpeakAsync(text, thai ? "th-TH" : "en-US", string.IsNullOrWhiteSpace(own) ? v.WindowsVoice : own, rate, ct)
                    .ConfigureAwait(false);
                return new SpeechAudio(bytes, "audio/wav", ".wav");
            }
            default:
                throw new InvalidOperationException("the browser voice speaks in the mic window");
        }
    }

    private static int RateOf(string format) =>
        int.TryParse(format.Split('_').ElementAtOrDefault(1), out int hz) && hz > 0 ? hz : 24000;

    /// <summary>Raw 16-bit mono PCM (ElevenLabs pcm_*) wrapped in a WAV header so a player can open it.</summary>
    public static byte[] WavOfPcm(byte[] pcm, int sampleRate)
    {
        var wav = new byte[44 + pcm.Length];
        void Str(int at, string s) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)s[i]; }
        void I32(int at, int x) => BitConverter.GetBytes(x).CopyTo(wav, at);
        void I16(int at, short x) => BitConverter.GetBytes(x).CopyTo(wav, at);
        Str(0, "RIFF"); I32(4, 36 + pcm.Length); Str(8, "WAVE");
        Str(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1); I32(24, sampleRate); I32(28, sampleRate * 2); I16(32, 2); I16(34, 16);
        Str(36, "data"); I32(40, pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}

/// <summary>The chosen engine has no key yet.</summary>
public sealed class VoiceKeyMissingException(string engine) : Exception("no API key for " + engine)
{
    public string Engine { get; } = engine;
}
