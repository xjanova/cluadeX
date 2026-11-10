using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CluadeX.Services.Media;

/// <summary>
/// Speech to text for media_transcript: ffmpeg makes the sound small (mono, 16 kHz, 32 kb/s AAC — what speech needs)
/// and cuts it into <see cref="ChunkSeconds"/> pieces (each far under the API's 25 MB), then each piece goes to OpenAI's
/// /audio/transcriptions (the key and endpoint of AI Provider › OpenAI, as the voices use). Pieces are sent one after
/// another and their times are shifted back into the whole recording. A run is refused above <see cref="MaxDuration"/>.
/// </summary>
public static class SpeechToText
{
    public const string Model = "whisper-1";

    /// <summary>OpenAI's price for <see cref="Model"/>, per minute of sound (2026-11).</summary>
    public const double UsdPerMinute = 0.006;

    public const int ChunkSeconds = 600;

    /// <summary>The longest recording transcribed in one call: 24 pieces of ten minutes.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    /// <summary>The API refuses an upload above 25 MB.</summary>
    public const long MaxChunkBytes = 25L * 1024 * 1024;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>For the harness: the client to post with (a localhost stand-in for the API).</summary>
    public static HttpClient? HttpOverride { get; set; }

    public static double CostUsd(TimeSpan duration) => Math.Ceiling(Math.Max(0, duration.TotalMinutes)) * UsdPerMinute;

    /// <summary>"$0.36", or three decimals under ten cents ("$0.006" a minute must not read as "$0.01"); invariant
    /// (a Thai or German locale must not write "0,36").</summary>
    public static string Usd(double usd) => "$" + usd.ToString(usd < 0.1 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>ffmpeg's arguments: <paramref name="input"/> (audio or video) → mono 16 kHz 32 kb/s AAC pieces
    /// chunk_000.m4a, chunk_001.m4a … in <paramref name="outDir"/>, each starting at time 0.</summary>
    public static IReadOnlyList<string> ChunkArgs(string input, string outDir) => new List<string>
    {
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-i", input,
        "-vn", "-ac", "1", "-ar", "16000", "-c:a", "aac", "-b:a", "32k",
        "-f", "segment", "-segment_time", ChunkSeconds.ToString(CultureInfo.InvariantCulture), "-reset_timestamps", "1",
        Path.Combine(outDir, "chunk_%03d.m4a"),
    };

    public static List<string> Chunks(string outDir)
    {
        try { return Directory.EnumerateFiles(outDir, "chunk_*.m4a").OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>What one piece said, or why it could not be read (<see cref="Error"/> set, <see cref="Lines"/> empty).</summary>
    public sealed record Piece(List<Subtitles.Line> Lines, string? Error, HttpStatusCode? Status = null);

    /// <summary>Send one piece. <paramref name="language"/>: an ISO 639-1 code ("th") or null to let the model detect it.</summary>
    public static async Task<Piece> TranscribeAsync(string file, string apiKey, string apiBase, string? language, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return new Piece(new(), "piece missing: " + fi.Name);
        if (fi.Length > MaxChunkBytes) return new Piece(new(), $"piece {fi.Name} is {fi.Length / (1024 * 1024)} MB — over the API's 25 MB");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language) && language.Trim().Length is 2 or 3)
            form.Add(new StringContent(language.Trim().ToLowerInvariant()), "language");
        var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        var audio = new ByteArrayContent(bytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        form.Add(audio, "file", Path.GetFileName(file));

        using var req = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + "/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        HttpResponseMessage resp;
        try { resp = await (HttpOverride ?? SharedHttp).SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { return new Piece(new(), "could not reach the speech service: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new Piece(new(), "the speech service did not answer in time"); }
        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Piece(new(), $"HTTP {(int)resp.StatusCode}: {ApiError(body)}", resp.StatusCode);
            return ParseVerbose(body) is { } lines ? new Piece(lines, null) : new Piece(new(), "the answer had no text");
        }
    }

    /// <summary>The lines of a verbose_json answer (segments with start times), else its plain text at 0:00.
    /// Null when there is neither.</summary>
    public static List<Subtitles.Line>? ParseVerbose(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var lines = new List<Subtitles.Line>();
            if (r.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    string t = s.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? (tx.GetString() ?? "").Trim() : "";
                    double start = s.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetDouble() : 0;
                    if (t.Length > 0) lines.Add(new Subtitles.Line(TimeSpan.FromSeconds(Math.Max(0, start)), t));
                }
            }
            if (lines.Count == 0 && r.TryGetProperty("text", out var all) && all.ValueKind == JsonValueKind.String
                && (all.GetString() ?? "").Trim() is { Length: > 0 } text)
                lines.Add(new Subtitles.Line(TimeSpan.Zero, text));
            return lines.Count > 0 ? lines : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The "message" of an OpenAI error body, never the request (the key is in its header, not here).</summary>
    private static string ApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Trim(m.GetString() ?? "");
        }
        catch (JsonException) { }
        return Trim(body);
        static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
    }
}
