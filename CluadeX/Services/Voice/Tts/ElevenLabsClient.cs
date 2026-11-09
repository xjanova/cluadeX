using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Voice.Tts;

/// <summary>One ElevenLabs model as GET /v1/models describes it — what it can do drives the Settings › Voice controls.</summary>
public sealed record ElevenModel(
    string Id, string Name, string Description, bool CanTts, bool CanUseStyle, bool CanUseSpeakerBoost,
    bool CanBeFinetuned, bool AlphaOnly, int MaxChars, double CostFactor, IReadOnlyList<string> Languages)
{
    /// <summary>eleven_v3 / v4: audio tags ([whispers], [laughs] …) in the text steer the delivery.</summary>
    public bool AudioTags => Id.StartsWith("eleven_v3", StringComparison.Ordinal) || Id.StartsWith("eleven_v4", StringComparison.Ordinal);
    /// <summary>eleven_v3 takes only three stabilities: 0 Creative, 0.5 Natural, 1 Robust.</summary>
    public bool StabilitySteps => Id.StartsWith("eleven_v3", StringComparison.Ordinal);
    public bool Speaks(string lang) => Languages.Any(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase));
    public bool Thai => Speaks("th");
}

/// <summary>One voice in the account's library (GET /v2/voices).</summary>
public sealed record ElevenVoice(string Id, string Name, string Category, string Description, string? PreviewUrl,
    IReadOnlyList<string> Languages, string Labels)
{
    /// <summary>A voice this account made itself (cloned or designed) — the only kind that can be deleted.</summary>
    public bool IsOwn => Category is "cloned" or "generated" or "professional";
    public override string ToString() => Name;
}

/// <summary>The account's plan (GET /v1/user/subscription). Fields missing from the answer stay null.</summary>
public sealed record ElevenAccount(string? Tier, long? CharactersUsed, long? CharacterLimit, bool? CanClone,
    int? VoiceLimit, DateTime? ResetsUtc);

public sealed class ElevenLabsException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The ElevenLabs REST API (api.elevenlabs.io, header <c>xi-api-key</c>; docs checked 2026-11-09): the models and what
/// each can do, the account's voices, its plan, text-to-speech and instant voice cloning. The base URL can be pointed
/// at a stand-in server for the tests.
/// </summary>
public sealed class ElevenLabsClient
{
    public const string DefaultBase = "https://api.elevenlabs.io";
    /// <summary>A premade voice every account has — used until the user picks one.</summary>
    public const string FallbackVoiceId = "EXAVITQu4vr4xnSDxMaL";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _base;

    public ElevenLabsClient(string apiKey, string? baseUrl = null, HttpClient? http = null)
    {
        _key = (apiKey ?? "").Trim();
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    // Declared before KnownModels: static fields start in text order, so the lists must exist first.
    private static readonly string[] Langs29 = ["en", "ja", "zh", "de", "hi", "fr", "ko", "pt", "it", "es", "id", "nl", "tr", "fil", "pl",
        "sv", "bg", "ro", "ar", "cs", "el", "fi", "hr", "ms", "sk", "da", "ta", "uk", "ru"];
    private static readonly string[] Langs32 = [.. Langs29, "hu", "no", "vi"];
    private static readonly string[] Langs70 = [.. Langs32, "th", "he", "fa", "bn", "ur", "sw", "af", "hy", "az", "be", "bs", "ca", "et",
        "gl", "ka", "gu", "is", "ga", "jv", "kn", "kk", "lv", "lt", "lb", "mk", "ml", "mr", "ne", "pa", "sr", "sl", "te", "cy", "my", "km", "lo"];
    private static readonly string[] Langs90 = [.. Langs70, "am", "as", "ceb", "ha", "ig", "ky", "ln", "mi", "mn", "ny", "ps", "sd", "so",
        "tg", "uz", "xh", "yo", "zu", "or", "si"];

    /// <summary>The models the API offered on 2026-11-09 — shown before a key is entered or when the list cannot be
    /// fetched. The live list replaces it (and adds any newer model).</summary>
    public static IReadOnlyList<ElevenModel> KnownModels { get; } =
    [
        new("eleven_v4", "Eleven v4", "The newest and most expressive — 90+ languages including Thai, audio tags.", true, false, false, false, false, 10000, 1, Langs90),
        new("eleven_v4_turbo", "Eleven v4 Turbo", "v4 with lower latency for live conversation, audio tags.", true, false, false, false, false, 10000, 0.5, Langs90),
        new("eleven_v3", "Eleven v3", "Emotional, dramatic delivery — 70+ languages including Thai, audio tags.", true, false, false, false, false, 5000, 1, Langs70),
        new("eleven_v3_conversational", "Eleven v3 Conversational", "v3 tuned for back-and-forth speech, audio tags.", true, false, false, false, false, 5000, 1, Langs70),
        new("eleven_multilingual_v2", "Eleven Multilingual v2", "Steady, lifelike long-form speech — 29 languages (no Thai).", true, true, true, true, false, 10000, 1, Langs29),
        new("eleven_flash_v2_5", "Eleven Flash v2.5", "Fastest (~75 ms), half the price — 32 languages (no Thai).", true, false, false, true, false, 40000, 0.5, Langs32),
        new("eleven_flash_v2", "Eleven Flash v2", "Fastest, English only.", true, false, false, true, false, 30000, 0.5, ["en"]),
    ];

    /// <summary>Old ids the API moved (2026): a saved choice of one of these is read as its successor.</summary>
    public static string Successor(string? model) => model switch
    {
        "eleven_turbo_v2_5" => "eleven_flash_v2_5",
        "eleven_turbo_v2" => "eleven_flash_v2",
        "eleven_monolingual_v1" or "eleven_multilingual_v1" => "eleven_multilingual_v2",
        null or "" => "eleven_v3",
        _ => model,
    };

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, _base + path);
        r.Headers.TryAddWithoutValidation("xi-api-key", _key);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var r = Request(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>The API's own words for a failure ({"detail":{"message":…}} or {"detail":"…"}), never the key.</summary>
    internal static ElevenLabsException Fail(HttpResponseMessage resp, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                if (d.ValueKind == JsonValueKind.String) message = d.GetString();
                else if (d.ValueKind == JsonValueKind.Object)
                    message = Str(d, "message") ?? Str(d, "status");
                else if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                    message = Str(d[0], "msg") ?? d[0].ToString();
            }
            message ??= Str(doc.RootElement, "message");
        }
        catch { /* not JSON */ }
        if (string.IsNullOrWhiteSpace(message)) message = body.Length > 200 ? body[..200] : body;
        if (string.IsNullOrWhiteSpace(message)) message = resp.ReasonPhrase ?? "error";
        return new ElevenLabsException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {message}");
    }

    /// <summary>Every model the account can use (GET /v1/models), text-to-speech ones first, newest first.</summary>
    public async Task<IReadOnlyList<ElevenModel>> ModelsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/v1/models", ct).ConfigureAwait(false);
        var list = new List<ElevenModel>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            string? id = Str(m, "model_id");
            if (string.IsNullOrEmpty(id)) continue;
            var langs = new List<string>();
            if (m.TryGetProperty("languages", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray()) { var code = Str(l, "language_id"); if (!string.IsNullOrEmpty(code)) langs.Add(code); }
            double cost = Num(m, "token_cost_factor") ?? 1;
            if (m.TryGetProperty("model_rates", out var rates) && rates.ValueKind == JsonValueKind.Object)
                cost = Num(rates, "character_cost_multiplier") ?? cost;
            list.Add(new ElevenModel(id, Str(m, "name") ?? id, Str(m, "description") ?? "",
                Bool(m, "can_do_text_to_speech") ?? false, Bool(m, "can_use_style") ?? false, Bool(m, "can_use_speaker_boost") ?? false,
                Bool(m, "can_be_finetuned") ?? false, Bool(m, "requires_alpha_access") ?? false,
                (int)(Num(m, "maximum_text_length_per_request") ?? 5000), cost, langs));
        }
        return list.OrderByDescending(m => m.CanTts).ThenBy(m => Rank(m.Id)).ToList();
    }

    /// <summary>Newest families first: v4, v3, multilingual, flash, then anything else by name.</summary>
    private static int Rank(string id) =>
        id.StartsWith("eleven_v4") ? 0 : id.StartsWith("eleven_v3") ? 1 : id.StartsWith("eleven_multilingual") ? 2 : id.StartsWith("eleven_flash") ? 3 : 4;

    /// <summary>The account's voices, every page of GET /v2/voices (at most <paramref name="max"/>).</summary>
    public async Task<IReadOnlyList<ElevenVoice>> VoicesAsync(string? search = null, int max = 500, CancellationToken ct = default)
    {
        var list = new List<ElevenVoice>();
        string? token = null;
        for (int page = 0; page < 20 && list.Count < max; page++)
        {
            string path = "/v2/voices?page_size=100&include_total_count=false"
                + (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim()))
                + (token == null ? "" : "&next_page_token=" + Uri.EscapeDataString(token));
            using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("voices", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                {
                    string? id = Str(v, "voice_id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var langs = new List<string>();
                    if (v.TryGetProperty("verified_languages", out var vl) && vl.ValueKind == JsonValueKind.Array)
                        foreach (var l in vl.EnumerateArray()) { var code = Str(l, "language"); if (!string.IsNullOrEmpty(code) && !langs.Contains(code)) langs.Add(code); }
                    string labels = "";
                    if (v.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Object)
                        labels = string.Join(" · ", lb.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)
                            .Where(x => !string.IsNullOrWhiteSpace(x)));
                    list.Add(new ElevenVoice(id, Str(v, "name") ?? id, Str(v, "category") ?? "", Str(v, "description") ?? "",
                        Str(v, "preview_url"), langs, labels));
                }
            bool more = Bool(root, "has_more") ?? false;
            token = Str(root, "next_page_token");
            if (!more || string.IsNullOrEmpty(token)) break;
        }
        return list;
    }

    /// <summary>The account's plan and how much of it is used (GET /v1/user/subscription).</summary>
    public async Task<ElevenAccount> AccountAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/v1/user/subscription", ct).ConfigureAwait(false);
        var r = doc.RootElement;
        DateTime? reset = Num(r, "next_character_count_reset_unix") is double unix && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime : null;
        return new ElevenAccount(Str(r, "tier"), (long?)Num(r, "character_count"), (long?)Num(r, "character_limit"),
            Bool(r, "can_use_instant_voice_cloning"), (int?)Num(r, "voice_limit"), reset);
    }

    /// <summary>The fine settings sent with one request.</summary>
    public sealed record SpeechOptions(double Stability, double Similarity, double Style, bool SpeakerBoost, double Speed,
        string? LanguageCode, string Normalization, string OutputFormat, bool SendStyle, bool SendBoost);

    /// <summary>Text to speech: POST /v1/text-to-speech/{voice}. Returns the audio bytes (mp3 by default).</summary>
    public async Task<byte[]> SpeakAsync(string text, string voiceId, string model, SpeechOptions o, CancellationToken ct = default)
    {
        var settings = new Dictionary<string, object>
        {
            ["stability"] = Math.Round(Math.Clamp(o.Stability, 0, 1), 2),
            ["similarity_boost"] = Math.Round(Math.Clamp(o.Similarity, 0, 1), 2),
            ["speed"] = Math.Round(Math.Clamp(o.Speed, 0.7, 1.2), 2),
        };
        if (o.SendStyle) settings["style"] = Math.Round(Math.Clamp(o.Style, 0, 1), 2);
        if (o.SendBoost) settings["use_speaker_boost"] = o.SpeakerBoost;
        var body = new Dictionary<string, object> { ["text"] = text, ["model_id"] = model, ["voice_settings"] = settings };
        if (!string.IsNullOrEmpty(o.LanguageCode)) body["language_code"] = o.LanguageCode;
        if (o.Normalization is "on" or "off") body["apply_text_normalization"] = o.Normalization;

        string format = string.IsNullOrWhiteSpace(o.OutputFormat) ? "mp3_44100_128" : o.OutputFormat;
        using var r = Request(HttpMethod.Post, $"/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format={Uri.EscapeDataString(format)}");
        r.Headers.Accept.Clear();
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Instant voice cloning: POST /v1/voices/add with the sample files. Returns the new voice's id.</summary>
    public async Task<string> CloneAsync(string name, IReadOnlyList<string> files, string? description, bool removeNoise,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "name");
        if (!string.IsNullOrWhiteSpace(description)) form.Add(new StringContent(description), "description");
        form.Add(new StringContent(removeNoise ? "true" : "false"), "remove_background_noise");
        foreach (var f in files)
        {
            var content = new ByteArrayContent(await File.ReadAllBytesAsync(f, ct).ConfigureAwait(false));
            content.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(f));
            form.Add(content, "files", Path.GetFileName(f));
        }
        using var r = Request(HttpMethod.Post, "/v1/voices/add");
        r.Content = form;
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp, json);
        using var doc = JsonDocument.Parse(json);
        return Str(doc.RootElement, "voice_id") ?? throw new ElevenLabsException(200, "no voice_id in the answer");
    }

    /// <summary>Delete one of the account's own voices: DELETE /v1/voices/{id}.</summary>
    public async Task DeleteVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        using var r = Request(HttpMethod.Delete, "/v1/voices/" + Uri.EscapeDataString(voiceId));
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Sample files the cloning form accepts.</summary>
    public static readonly string[] SampleExtensions = [".mp3", ".wav", ".m4a", ".ogg", ".flac", ".webm", ".aac", ".mp4"];
    /// <summary>Instant cloning takes up to 25 samples; 10 MB each keeps one upload sane.</summary>
    public const int MaxSamples = 25;
    public const long MaxSampleBytes = 10L * 1024 * 1024;

    private static string MimeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" or ".mp4" => "audio/mp4", ".ogg" => "audio/ogg",
        ".flac" => "audio/flac", ".webm" => "audio/webm", ".aac" => "audio/aac", _ => "application/octet-stream",
    };

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
