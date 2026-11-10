using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Attachments;
using CluadeX.Services.Browser;
using CluadeX.Services.Media;
using CluadeX.Services.Providers;
using CluadeX.Services.Voice.Tts;

namespace CluadeX.Services.Tools;

/// <summary>
/// media_transcript — what a video or a recording says, as text the model can read and summarise (owner 2026-11-10:
/// "เราปรับปรุงเครื่องมือของเราดีกว่า", after comparing CluadeX with agent-reach: the one thing it really had that we
/// did not was a video's words).
/// <list type="bullet">
/// <item>A web link (YouTube and the other sites yt-dlp knows): its subtitles — made by a person in the wanted language
///   first, then the automatic captions in the language spoken. Free; only the video's site is asked.</item>
/// <item>No subtitles, or a file on this PC: speech to text (ffmpeg → 10-minute pieces → OpenAI Whisper, the key of AI
///   Provider › OpenAI). Paid and the sound leaves the PC, so it runs only when the model passes speech=true for a link
///   (a file needs it anyway), and the user is asked every time with the price, even with permissions off.</item>
/// </list>
/// Every transcript is saved under the data folder (transcripts\), so reading on (offset) or asking again costs nothing
/// and never downloads twice; the reply names the file. Nothing is installed: a missing yt-dlp / ffmpeg / key is named
/// with the one step that fixes it.
/// </summary>
public sealed class MediaTranscriptToolModule : IAgentToolModule
{
    private readonly Func<AppSettings?> _settings;
    private readonly Func<string?> _dataRoot;
    private readonly Func<bool> _webAllowed;

    public MediaTranscriptToolModule(SettingsService? settings, ActivationService? activation)
        : this(() => settings?.Settings, () => settings?.DataRoot,
            () => settings?.Settings.Features.WebFetch == true && activation?.IsFeatureUnlocked("feature.webFetch") != false) { }

    public MediaTranscriptToolModule(Func<AppSettings?> settings, Func<string?> dataRoot, Func<bool> webAllowed)
    {
        _settings = settings;
        _dataRoot = dataRoot;
        _webAllowed = webAllowed;
    }

    /// <summary>How long a saved transcript is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public string Id => "media";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.MediaTranscript };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "media_transcript" or "video_transcript" or "youtube_transcript" or "get_transcript" or "transcript"
            or "transcribe" or "transcribe_media" or "transcribe_video" or "transcribe_audio" => ToolType.MediaTranscript,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        var props = new Dictionary<string, object>
        {
            ["source"] = new Dictionary<string, string> { ["type"] = "string", ["description"] = "A video link (YouTube or another video site) or the path of an audio / video file on this PC" },
            ["language"] = new Dictionary<string, string> { ["type"] = "string", ["description"] = "Language wanted, as a code: th, en, ja… (default: Thai, then English, then what is spoken)" },
            ["speech"] = new Dictionary<string, string> { ["type"] = "boolean", ["description"] = "true = turn the speech into text when a link has no subtitles (paid, the user is asked). A file always needs it." },
            ["offset"] = new Dictionary<string, string> { ["type"] = "integer", ["description"] = "Character to start from, to read on in a long transcript (default 0)" },
            ["max_chars"] = new Dictionary<string, string> { ["type"] = "integer", ["description"] = "Most characters to return (default 20000)" },
        };
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props, ["required"] = new[] { "source" } };
        yield return new ToolSchema
        {
            Name = "media_transcript",
            Description = "What a video or a recording says, as text with [m:ss] time marks — to summarise a YouTube video, quote a "
                + "talk, find where something is said. A link: its subtitles (free). No subtitles, or a file: call with speech=true "
                + "to turn the speech into text (OpenAI Whisper, paid, the user is asked). Long ones come in parts: pass the offset "
                + "the reply names. Every transcript is saved and reading it again is free.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    public string? GetRefusal(ToolType type) => null;

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    public string? GetPromptSection(PromptSectionContext context) =>
        "To know what a video or a recording says (summarise a YouTube link, quote a talk), call media_transcript — never guess "
        + "a video's content from its title.";

    // ─── What the call is about ───

    /// <summary>A parsed source: a web link (<see cref="Url"/>) or a media file (<see cref="File"/>).</summary>
    private sealed record Source(string? Url, string? File, string Display);

    private static Source? ParseSource(string raw, ToolModuleContext ctx, out string? error)
    {
        error = null;
        raw = (raw ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0) { error = L("media.tr.noSource"); return null; }
        if (YouTubeId(raw) is { } bareId && !raw.Contains('/') && !raw.Contains('\\') && !raw.Contains('.'))
            raw = "https://www.youtube.com/watch?v=" + bareId;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return new Source(uri.AbsoluteUri, null, uri.AbsoluteUri);
        try
        {
            string path = Path.IsPathFullyQualified(raw) ? Path.GetFullPath(raw)
                : ctx.WorkingDirectory is { Length: > 0 } wd ? Path.GetFullPath(Path.Combine(wd, raw)) : "";
            if (path.Length > 0 && System.IO.File.Exists(path) && MediaInfo.IsMedia(path))
                return new Source(null, path, Path.GetFileName(path));
        }
        catch { /* not a path */ }
        error = F("media.tr.badSource", raw);
        return null;
    }

    /// <summary>The 11-character id of a YouTube link (watch?v=, youtu.be/, shorts/, live/, embed/) or of a bare id; else null.</summary>
    public static string? YouTubeId(string s)
    {
        s = (s ?? "").Trim();
        static bool IsId(string v) => v.Length == 11 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        if (IsId(s)) return s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        string host = u.Host.ToLowerInvariant();
        if (host == "youtu.be") { var id = u.AbsolutePath.Trim('/').Split('/')[0]; return IsId(id) ? id : null; }
        if (host != "youtube.com" && !host.EndsWith(".youtube.com")) return null;
        foreach (var part in u.Query.TrimStart('?').Split('&'))
            if (part.StartsWith("v=", StringComparison.Ordinal) && IsId(Uri.UnescapeDataString(part[2..]))) return Uri.UnescapeDataString(part[2..]);
        var seg = u.AbsolutePath.Trim('/').Split('/');
        return seg.Length >= 2 && seg[0] is "shorts" or "live" or "embed" or "v" && IsId(seg[1]) ? seg[1] : null;
    }

    private static bool Speech(ToolCall call) => call.GetArg("speech").Trim().ToLowerInvariant() is "true" or "1" or "yes";

    private static string Wanted(ToolCall call)
    {
        string l = call.GetArg("language").Trim().ToLowerInvariant();
        return l.Length is >= 2 and <= 8 && l.All(c => char.IsAsciiLetter(c) || c == '-') ? l : "";
    }

    /// <summary>The store key of a source's transcript: the video id for YouTube (any form of its link), the link
    /// otherwise; a file by its path, size and time (an edited file is a new recording).</summary>
    private static string KeyOf(Source src, string kind, string wanted)
    {
        string what;
        if (src.Url != null) what = YouTubeId(src.Url) is { } id ? "yt:" + id : src.Url;
        else
        {
            var fi = new FileInfo(src.File!);
            what = $"file:{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        return $"{kind}|{wanted}|{what}";
    }

    // ─── Permission ───

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        var src = ParseSource(call.GetArg("source"), context, out _);
        if (src == null) return ToolPermission.Free("read", call.GetArg("source"));   // fails in Execute with the reason
        string wanted = Wanted(call);
        bool speech = Speech(call) || src.File != null;
        // A transcript already saved: reading it is free (nothing is sent, nothing is paid).
        if (FindSaved(src, wanted, speech) != null) return ToolPermission.Free("read", src.Url ?? src.File!);
        if (!speech)
            return ToolPermission.Ask("browse", src.Url!, F("media.tr.permSubs", src.Display), onceKey: "media:" + (new Uri(src.Url!).Host));

        string length;
        TimeSpan? duration = src.File != null ? MediaInfo.Probe(src.File)?.Duration : null;
        length = duration is { } d
            ? F("media.tr.lengthKnown", Subtitles.Clock(d), SpeechToText.Usd(SpeechToText.CostUsd(d)))
            : F("media.tr.lengthUnknown", SpeechToText.Usd(SpeechToText.UsdPerMinute));
        return new ToolPermission
        {
            Scope = "execute",
            Resource = "media_transcript speech: " + (src.Url ?? src.File),
            Detail = F("media.tr.permSpeech", src.Display, SpeechToText.Model, length),
            // Money and the sound leaving the PC: asked about even with the permission system off. A user rule still decides.
            AskEvenWhenPermissionsOff = true,
        };
    }

    // ─── Run ───

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var src = ParseSource(call.GetArg("source"), context, out var error);
        if (src == null) return Fail(call, error!);
        string wanted = Wanted(call);
        bool speech = Speech(call) || src.File != null;
        int offset = int.TryParse(call.GetArg("offset"), out var off) && off > 0 ? off : 0;
        int budget = Math.Max(2_000, context.OutputBudgetChars - 600);
        int maxChars = int.TryParse(call.GetArg("max_chars"), out var mc) && mc > 0 ? Math.Clamp(mc, 500, budget) : Math.Min(20_000, budget);

        if (FindSaved(src, wanted, speech) is { } saved) return Reply(call, saved, offset, maxChars, fromStore: true);
        if (src.Url != null && !_webAllowed()) return Fail(call, L("media.tr.webOff"));

        string work = Path.Combine(Path.GetTempPath(), "cluadex-transcript-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(work);
        try
        {
            Saved? made;
            string? failure;
            if (src.Url != null)
            {
                string? ytdlp = YtDlp.Locate();
                if (ytdlp == null) return Fail(call, F("media.tr.noYtDlp", YtDlp.InstallCommand));
                var probe = await YtDlp.RunAsync(ytdlp, YtDlp.ProbeArgs(src.Url), TimeSpan.FromSeconds(90), ct);
                if (probe.Cancelled) return Fail(call, L("media.tr.cancelled"));
                var info = probe.Ok ? YtDlp.ParseInfo(probe.StdOut) : null;
                if (info == null)
                    return Fail(call, F("media.tr.probeFailed", src.Display, probe.TimedOut ? L("media.tr.timedOutShort") : YtDlp.ErrorLine(probe)));
                if (!speech)
                {
                    var track = YtDlp.ChooseTrack(info, wanted.Length > 0 ? wanted : null);
                    if (track == null)
                        return Fail(call, F("media.tr.noSubs", Title(info, src),
                            info.Duration is { } dd ? Math.Ceiling(dd.TotalMinutes).ToString("0") : "?",
                            info.Duration is { } d2 ? SpeechToText.Usd(SpeechToText.CostUsd(d2)) : SpeechToText.Usd(SpeechToText.UsdPerMinute) + "/min",
                            SpeechToText.Model));
                    (made, failure) = await FromSubtitlesAsync(ytdlp, src, info, track, wanted, work, ct);
                }
                else (made, failure) = await FromSpeechAsync(ytdlp, src, info, wanted, work, ct);
            }
            else (made, failure) = await FromSpeechAsync(null, src, null, wanted, work, ct);

            if (made == null) return Fail(call, failure ?? L("media.tr.cancelled"));
            return Reply(call, made, offset, maxChars, fromStore: false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* a file still held — the OS temp cleanup gets it */ }
        }
    }

    private static string Title(VideoInfo? info, Source src) =>
        info != null && info.Title.Length > 0 ? $"\"{info.Title}\"" : src.Display;

    private async Task<(Saved?, string?)> FromSubtitlesAsync(string ytdlp, Source src, VideoInfo info, SubtitleTrack track,
        string wanted, string work, CancellationToken ct)
    {
        var run = await YtDlp.RunAsync(ytdlp, YtDlp.SubtitleArgs(src.Url!, track, work), TimeSpan.FromSeconds(120), ct);
        if (run.Cancelled) return (null, L("media.tr.cancelled"));
        string? file = YtDlp.Output(work, "sub");
        if (!run.Ok || file == null)
            return (null, F("media.tr.subFailed", Title(info, src), run.TimedOut ? L("media.tr.timedOutShort") : YtDlp.ErrorLine(run)));
        var lines = Subtitles.Parse(await File.ReadAllTextAsync(file, ct));
        if (lines.Count == 0) return (null, F("media.tr.subFailed", Title(info, src), "the subtitle file had no text"));
        string from = track.Automatic
            ? (track.Translated ? $"automatic captions machine-translated to {track.Language} (not the spoken words)" : $"automatic captions ({track.Language})")
            : $"subtitles by a person ({track.Language})";
        return (Save(src, "subs", wanted, info, from, Subtitles.Format(lines)), null);
    }

    private async Task<(Saved?, string?)> FromSpeechAsync(string? ytdlp, Source src, VideoInfo? info, string wanted, string work, CancellationToken ct)
    {
        var settings = _settings();
        var (key, apiBase) = settings != null ? VoiceSpeaker.OpenAiKey(settings) : ("", OpenAiTtsClient.DefaultBase);
        if (key.Length == 0) return (null, L("media.tr.noKey"));
        string? ffmpeg = Ffmpeg.Locate();
        if (ffmpeg == null) return (null, L("media.tr.noFfmpeg"));

        TimeSpan? duration = info?.Duration ?? (src.File != null ? MediaInfo.Probe(src.File)?.Duration : null);
        if (duration is { } d && d > SpeechToText.MaxDuration)
            return (null, F("media.tr.tooLong", Title(info, src), Subtitles.Clock(d), Subtitles.Clock(SpeechToText.MaxDuration)));

        string input = src.File ?? "";
        if (src.Url != null)
        {
            var dl = await YtDlp.RunAsync(ytdlp!, YtDlp.AudioArgs(src.Url, work), TimeSpan.FromMinutes(15), ct);
            if (dl.Cancelled) return (null, L("media.tr.cancelled"));
            input = YtDlp.Output(work, "audio") ?? "";
            if (!dl.Ok || input.Length == 0)
                return (null, F("media.tr.audioFailed", Title(info, src), dl.TimedOut ? L("media.tr.timedOutShort") : YtDlp.ErrorLine(dl)));
        }

        string pieces = Path.Combine(work, "pieces");
        Directory.CreateDirectory(pieces);
        var cut = await Ffmpeg.RunAsync(ffmpeg, SpeechToText.ChunkArgs(input, pieces), null, TimeSpan.FromMinutes(20), ct);
        if (cut.Outcome == FfmpegOutcome.Cancelled) return (null, L("media.tr.cancelled"));
        var chunks = SpeechToText.Chunks(pieces);
        if (cut.Outcome != FfmpegOutcome.Done || chunks.Count == 0)
            return (null, F("media.tr.ffmpegFailed", cut.Outcome == FfmpegOutcome.TimedOut ? L("media.tr.timedOutShort") : Short(cut.Detail)));
        if (chunks.Count * SpeechToText.ChunkSeconds > SpeechToText.MaxDuration.TotalSeconds + SpeechToText.ChunkSeconds)
            return (null, F("media.tr.tooLong", Title(info, src), "> " + Subtitles.Clock(SpeechToText.MaxDuration), Subtitles.Clock(SpeechToText.MaxDuration)));

        var all = new List<Subtitles.Line>();
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var piece = await SpeechToText.TranscribeAsync(chunks[i], key, apiBase, wanted.Length is 2 or 3 ? wanted : null, ct);
            if (piece.Error != null)
            {
                string why = piece.Status == System.Net.HttpStatusCode.Unauthorized ? L("media.tr.keyRefused") : piece.Error;
                return (null, F("media.tr.sttFailed", i + 1, chunks.Count, why));
            }
            var shift = TimeSpan.FromSeconds((double)i * SpeechToText.ChunkSeconds);
            all.AddRange(piece.Lines.Select(l => l with { Start = l.Start + shift }));
        }
        string from = $"speech to text (OpenAI {SpeechToText.Model}, {chunks.Count} piece{(chunks.Count == 1 ? "" : "s")})";
        return (Save(src, "speech", wanted, info, from, Subtitles.Format(all)), null);
    }

    private static string Short(string s)
    {
        s = (s ?? "").Trim();
        var last = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? s;
        return last.Length > 300 ? last[..300] + "…" : last;
    }

    // ─── The store (DataRoot\transcripts) ───

    /// <summary>A saved transcript: its text file and what it is.</summary>
    public sealed record Saved(string TextPath, string Title, string Uploader, string? Duration, string From, string Source, DateTime MadeUtc);

    private string? StoreDir()
    {
        string? root = _dataRoot();
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "transcripts");
    }

    private static string FileStem(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();

    /// <summary>A saved transcript for this source. Asking for subtitles also takes a speech transcript made earlier
    /// (already paid for); asking for speech takes only speech.</summary>
    private Saved? FindSaved(Source src, string wanted, bool speech)
    {
        foreach (var kind in speech ? new[] { "speech" } : new[] { "subs", "speech" })
            if (Load(KeyOf(src, kind, wanted)) is { } s) return s;
        return null;
    }

    private Saved? Load(string key)
    {
        string? dir = StoreDir();
        if (dir == null) return null;
        string stem = FileStem(key);
        string meta = Path.Combine(dir, stem + ".json"), text = Path.Combine(dir, stem + ".txt");
        try
        {
            if (!File.Exists(meta) || !File.Exists(text)) return null;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(meta));
            if (s == null || DateTime.UtcNow - s.MadeUtc > KeepFor) return null;
            return s with { TextPath = text };
        }
        catch { return null; }
    }

    private Saved Save(Source src, string kind, string wanted, VideoInfo? info, string from, string transcript)
    {
        string title = info?.Title is { Length: > 0 } t ? t : src.Display;
        string? duration = info?.Duration is { } d ? Subtitles.Clock(d) : null;
        string? dir = StoreDir();
        string stem = FileStem(KeyOf(src, kind, wanted));
        string folder = dir ?? Path.Combine(Path.GetTempPath(), "CluadeX-transcripts");
        Directory.CreateDirectory(folder);
        string text = Path.Combine(folder, stem + ".txt");
        var saved = new Saved(text, title, info?.Uploader ?? "", duration, from, src.Url ?? src.File!, DateTime.UtcNow);
        File.WriteAllText(text, transcript, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, stem + ".json"), JsonSerializer.Serialize(saved));
        Prune(folder);
        return saved;
    }

    /// <summary>Transcripts older than <see cref="KeepFor"/> go.</summary>
    private static void Prune(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepFor) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    // ─── The reply ───

    private static ToolResult Reply(ToolCall call, Saved s, int offset, int maxChars, bool fromStore)
    {
        string transcript;
        try { transcript = File.ReadAllText(s.TextPath); }
        catch (Exception ex) { return Fail(call, F("media.tr.subFailed", s.Title, ex.Message)); }
        var part = WebFetchService.Slice(transcript, offset, maxChars);
        if (part.Total > 0 && part.Start >= part.Total)
            return Fail(call, F("media.tr.offsetPastEnd", offset, part.Total));
        var sb = new StringBuilder();
        sb.Append($"Transcript of \"{s.Title}\"");
        if (s.Uploader.Length > 0) sb.Append($" — {s.Uploader}");
        if (s.Duration != null) sb.Append($" · {s.Duration}");
        sb.Append($"\nFrom: {s.From}{(fromStore ? " · saved earlier, nothing was downloaded or paid" : "")}");
        sb.Append($"\nSource: {s.Source}\nSaved as: {s.TextPath}");
        sb.Append($"\nCharacters {part.Start:N0}–{part.End:N0} of {part.Total:N0}\n");
        sb.Append(UrlPolicy.Envelope(part.Text.Length > 0 ? part.Text : "(no words)"));
        if (part.End < part.Total)
            sb.Append($"\n({part.Total - part.End:N0} more characters — call media_transcript again with the same source and offset={part.End} to read on)");
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = sb.ToString(),
            Summary = $"{s.Title} · {part.Total:N0} chars{(fromStore ? " (saved)" : "")}",
        };
    }

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static string L(string key) => LocalizedResources.Instance[key];

    private static string F(string key, params object[] args)
    {
        try { return string.Format(L(key), args); }
        catch (FormatException) { return L(key); }
    }
}
