using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Voice.Tts;

/// <summary>
/// OpenAI's speech endpoint (POST {base}/audio/speech) with the key already entered under AI Provider › OpenAI — the
/// user types it once. gpt-4o-mini-tts takes free-form <c>instructions</c> (tone, pace, emotion), so each girl can be
/// told how she sounds; tts-1 / tts-1-hd take a speed instead.
/// </summary>
public sealed class OpenAiTtsClient
{
    public const string DefaultBase = "https://api.openai.com/v1";
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _base;

    public OpenAiTtsClient(string apiKey, string? baseUrl = null, HttpClient? http = null)
    {
        _key = (apiKey ?? "").Trim();
        _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    /// <summary>The speech models before the live list arrives.</summary>
    public static readonly string[] KnownModels = ["gpt-4o-mini-tts", "tts-1-hd", "tts-1"];

    /// <summary>Every built-in voice (2026-11). tts-1 / tts-1-hd have only the first nine.</summary>
    public static readonly string[] Voices = ["alloy", "ash", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer",
        "ballad", "verse", "marin", "cedar"];

    public static bool TakesInstructions(string model) => model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The voices a model can use.</summary>
    public static IReadOnlyList<string> VoicesFor(string model) => TakesInstructions(model) ? Voices : Voices[..9];

    /// <summary>The account's speech models (GET {base}/models, ids with "tts"), newest first; the known list when it fails.</summary>
    public async Task<IReadOnlyList<string>> ModelsAsync(CancellationToken ct = default)
    {
        using var r = new HttpRequestMessage(HttpMethod.Get, _base + "/models");
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ErrorOf(resp, body));
        using var doc = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
            {
                string? id = ElevenLabsClient.Str(m, "id");
                if (id != null && id.Contains("tts", StringComparison.OrdinalIgnoreCase) && !id.Contains("realtime", StringComparison.OrdinalIgnoreCase))
                    ids.Add(id);
            }
        return ids.Count == 0 ? KnownModels
            : ids.OrderByDescending(TakesInstructions).ThenByDescending(i => i, StringComparer.Ordinal).ToList();
    }

    /// <summary>Text to speech as mp3.</summary>
    public async Task<byte[]> SpeakAsync(string text, string model, string voice, double speed, string? instructions, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "mp3" };
        if (Math.Abs(speed - 1) > 0.01) body["speed"] = Math.Round(Math.Clamp(speed, 0.25, 4), 2);
        if (TakesInstructions(model) && !string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions.Trim();
        using var r = new HttpRequestMessage(HttpMethod.Post, _base + "/audio/speech");
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorOf(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static string ErrorOf(HttpResponseMessage resp, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) message = ElevenLabsClient.Str(e, "message");
        }
        catch { }
        if (string.IsNullOrWhiteSpace(message)) message = body.Length > 200 ? body[..200] : body;
        return $"HTTP {(int)resp.StatusCode}: {(string.IsNullOrWhiteSpace(message) ? resp.ReasonPhrase : message)}";
    }
}
