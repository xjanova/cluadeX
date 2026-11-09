namespace CluadeX.Models;

/// <summary>
/// Settings › Voice: who reads the replies aloud and how (2026-11-09). Five engines:
/// <list type="bullet">
/// <item><c>edge</c> — Microsoft Edge's online read-aloud voices (free, no key, natural Thai: Premwadee / Niwat). Plays
/// in the app; no window needed. The default, and what the free engines fall back to when they cannot speak the
/// reply's language.</item>
/// <item><c>browser</c> — the mic window's own speech synthesis (free; Edge has natural Thai voices). Needs the mic window.</item>
/// <item><c>windows</c> — Windows' own offline voices (free; System.Speech). Plays in the app.</item>
/// <item><c>elevenlabs</c> — ElevenLabs text-to-speech with its own key: every model, the account's voices, cloning.</item>
/// <item><c>openai</c> — OpenAI's speech endpoint with the OpenAI key already entered under AI Provider (entered once).</item>
/// </list>
/// With <see cref="PersonaVoices"/> each girl speaks in her own style (CluadeX soft and shy, Grok cheeky …) and may have
/// her own voice per engine (<see cref="Characters"/>).
/// </summary>
public sealed class VoiceSettings
{
    public const string Edge = "edge", Browser = "browser", Windows = "windows", ElevenLabs = "elevenlabs", OpenAI = "openai";
    public static readonly string[] Engines = [Edge, Browser, Windows, ElevenLabs, OpenAI];

    public string Engine { get; set; } = Edge;
    /// <summary>Each girl speaks in her own personality (speed, warmth, ElevenLabs audio tags, OpenAI instructions).</summary>
    public bool PersonaVoices { get; set; } = true;
    /// <summary>The longest reply read aloud, in characters (ElevenLabs and OpenAI bill per character). 100-10000.</summary>
    public int MaxChars { get; set; } = 1200;

    // ─── ElevenLabs ───
    /// <summary>The ElevenLabs API key (xi-api-key) — DPAPI-encrypted on disk like the provider keys.</summary>
    public string? ElevenLabsApiKey { get; set; }
    public string ElevenLabsModel { get; set; } = "eleven_v3";
    public string ElevenLabsVoiceId { get; set; } = "";
    public string ElevenLabsVoiceName { get; set; } = "";
    /// <summary>0-1. Low = more expressive, high = steadier. eleven_v3 takes 0 / 0.5 / 1 (Creative / Natural / Robust).</summary>
    public double Stability { get; set; } = 0.5;
    /// <summary>0-1, how closely the voice keeps to the original.</summary>
    public double Similarity { get; set; } = 0.75;
    /// <summary>0-1, style exaggeration (models with can_use_style).</summary>
    public double Style { get; set; } = 0.0;
    /// <summary>Speaker boost (models with can_use_speaker_boost).</summary>
    public bool SpeakerBoost { get; set; } = true;
    /// <summary>0.7-1.2.</summary>
    public double Speed { get; set; } = 1.0;
    public string OutputFormat { get; set; } = "mp3_44100_128";
    /// <summary>auto | on | off — numbers and dates spelled out.</summary>
    public string TextNormalization { get; set; } = "auto";
    /// <summary>On eleven_v3 / v4: the girl's mood as an audio tag ([softly], [playfully] …) before the text.</summary>
    public bool AudioTags { get; set; } = true;

    // ─── OpenAI (the AI Provider › OpenAI key) ───
    public string OpenAiModel { get; set; } = "gpt-4o-mini-tts";
    public string OpenAiVoice { get; set; } = "marin";
    /// <summary>0.25-4.</summary>
    public double OpenAiSpeed { get; set; } = 1.0;
    /// <summary>Extra tone instructions (gpt-4o-mini-tts only), added after the girl's own.</summary>
    public string OpenAiInstructions { get; set; } = "";

    // ─── Free: Edge's online voices ───
    /// <summary>An Edge voice ("th-TH-PremwadeeNeural"); empty = the one for the reply's language.</summary>
    public string EdgeVoice { get; set; } = "";
    /// <summary>0.5-2 (sent as a percentage).</summary>
    public double EdgeRate { get; set; } = 1.0;
    /// <summary>0.5-2 (sent in Hz around the voice's own pitch).</summary>
    public double EdgePitch { get; set; } = 1.0;

    // ─── Free: the browser's voices (mic window) ───
    /// <summary>A voice name the mic window reported; empty = the best one for the language.</summary>
    public string BrowserVoice { get; set; } = "";
    /// <summary>0.5-2.</summary>
    public double BrowserRate { get; set; } = 1.0;
    /// <summary>0.5-2.</summary>
    public double BrowserPitch { get; set; } = 1.0;
    /// <summary>The voices the mic window's browser reported last ("name | lang"), for the picker.</summary>
    public List<string> KnownBrowserVoices { get; set; } = new();

    // ─── Free: Windows' offline voices ───
    public string WindowsVoice { get; set; } = "";
    /// <summary>-10..10.</summary>
    public int WindowsRate { get; set; } = 0;

    /// <summary>A girl's own voice per engine, by her picture name ("cluadex", "codex" …, lower case).</summary>
    public Dictionary<string, CharacterVoice> Characters { get; set; } = new();

    public CharacterVoice? For(string? girl) =>
        string.IsNullOrWhiteSpace(girl) ? null : Characters.GetValueOrDefault(girl.Trim().ToLowerInvariant());
}

/// <summary>One girl's own voice on each engine. Empty = the engine's main voice.</summary>
public sealed class CharacterVoice
{
    public string? ElevenLabsVoiceId { get; set; }
    public string? ElevenLabsVoiceName { get; set; }
    public string? OpenAiVoice { get; set; }
    public string? BrowserVoice { get; set; }
    public string? WindowsVoice { get; set; }
    public string? EdgeVoice { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(ElevenLabsVoiceId) && string.IsNullOrWhiteSpace(OpenAiVoice)
        && string.IsNullOrWhiteSpace(BrowserVoice) && string.IsNullOrWhiteSpace(WindowsVoice) && string.IsNullOrWhiteSpace(EdgeVoice);
}
