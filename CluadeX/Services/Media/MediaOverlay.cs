using System.Globalization;
using System.IO;

namespace CluadeX.Services.Media;

/// <summary>One text layer burned into a video: a transparent PNG at the video's own size, shown from
/// <see cref="Start"/> to <see cref="End"/> (seconds on the ORIGINAL clip's clock).</summary>
public sealed record OverlaySpan(string Png, double Start, double End);

/// <summary>
/// Text on a video (owner 2026-11-04: "อยากให้ใส่ข้อความลงในไฟล์ภาพและวีดีโอได้ เลือกฟ้อน สี"). The page draws each text
/// layer itself — Chromium picks the font and shapes Thai (tone marks stacked right, ที่นี่ ผู้ใหญ่ น้ำ) — into a PNG at
/// the video's real size; ffmpeg only lays the pictures over the frames for their time windows. ffmpeg's drawtext is
/// not used: it needs a font FILE and shapes Thai badly. The picture is re-encoded (libx264 / VP9), the sound copied.
/// </summary>
public static class MediaOverlay
{
    /// <summary>At most this many text layers in one save (each is an ffmpeg input).</summary>
    public const int MaxLayers = 64;

    /// <summary>One layer's PNG may be at most this big (an 8K frame of text is far less).</summary>
    public const long MaxPngBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The window a layer shows in the saved clip, which starts at <paramref name="cutStart"/> of the original and is
    /// <paramref name="cutEnd"/> − <paramref name="cutStart"/> long: shifted to the new clock and clamped to it. Null when
    /// the layer is not on screen in the saved part at all (or its times are not numbers / are backwards).
    /// </summary>
    public static (double From, double To)? WindowInCut(double layerStart, double layerEnd, double cutStart, double cutEnd)
    {
        if (!double.IsFinite(layerStart) || !double.IsFinite(layerEnd) || !double.IsFinite(cutStart) || !double.IsFinite(cutEnd)) return null;
        if (cutEnd <= cutStart || layerEnd <= layerStart) return null;
        double from = Math.Max(layerStart, cutStart) - cutStart;
        double to = Math.Min(layerEnd, cutEnd) - cutStart;
        if (to - from < 0.001) return null;
        return (Math.Max(0, from), to);
    }

    /// <summary>The video encoder for the container of <paramref name="ext"/>. <paramref name="attempt"/> 1 is the
    /// fallback for an ffmpeg built without the first choice (an LGPL build has no libx264).</summary>
    public static IReadOnlyList<string> VideoEncoder(string ext, long pixels, int attempt = 0)
    {
        bool big = pixels > 1920L * 1088;   // above 1080p: a faster preset, or a 4K clip takes many times its length
        switch (MediaFiles.Canonical(ext))
        {
            case "webm":
                return attempt == 0
                    ? new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", big ? "5" : "4", "-pix_fmt", "yuv420p" }
                    : new[] { "-c:v", "libvpx", "-crf", "10", "-b:v", "8M", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p" };
            case "ogv":
                return new[] { "-c:v", "libtheora", "-q:v", "7", "-pix_fmt", "yuv420p" };
            default:   // mp4 · m4v · mov · mkv
                return attempt == 0
                    ? new[] { "-c:v", "libx264", "-preset", big ? "faster" : "medium", "-crf", "20", "-pix_fmt", "yuv420p" }
                    : new[] { "-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p" };
        }
    }

    /// <summary>How many encoder choices <see cref="VideoEncoder"/> has for <paramref name="ext"/>.</summary>
    public static int EncoderAttempts(string ext) => MediaFiles.Canonical(ext) == "ogv" ? 1 : 2;

    /// <summary>
    /// ffmpeg arguments that cut [<paramref name="cutStart"/>, <paramref name="cutEnd"/>) of <paramref name="input"/> (frame
    /// exact: the picture is decoded anyway) and lay each overlay over the frames of its window. Layers that are not
    /// on screen in the cut are left out. Sound is copied as it is. Each path is one argument (ArgumentList): spaces,
    /// Thai names and quotes need no escaping. <paramref name="output"/> must not exist (-n).
    /// </summary>
    public static IReadOnlyList<string> BurnArguments(string input, IReadOnlyList<OverlaySpan> overlays, string output,
        double cutStart, double cutEnd, long pixels, int attempt = 0)
    {
        double start = Math.Max(0, cutStart), length = Math.Max(0.001, cutEnd - start);
        var shown = overlays
            .Select(o => (o.Png, Window: WindowInCut(o.Start, o.End, start, start + length)))
            .Where(o => o.Window != null)
            .Take(MaxLayers)
            .ToList();

        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        if (start > 0) { a.Add("-ss"); a.Add(Ffmpeg.Seconds(start)); }
        a.Add("-t"); a.Add(Ffmpeg.Seconds(length));
        a.Add("-i"); a.Add(input);
        foreach (var o in shown) { a.Add("-i"); a.Add(o.Png); }

        // [0:v][1:v]overlay=…[v1];[v1][2:v]overlay=…[v2];…;[vN]crop=even[vout] — a 4:2:0 encoder needs even sides
        var graph = new System.Text.StringBuilder();
        string last = "0:v";
        for (int i = 0; i < shown.Count; i++)
        {
            var (from, to) = shown[i].Window!.Value;
            string next = "v" + (i + 1);
            graph.Append('[').Append(last).Append("][").Append(i + 1).Append(":v]overlay=0:0:format=auto:enable='between(t,")
                 .Append(Ffmpeg.Seconds(from)).Append(',').Append(Ffmpeg.Seconds(to)).Append(")'[").Append(next).Append("];");
            last = next;
        }
        graph.Append('[').Append(last).Append("]crop=trunc(iw/2)*2:trunc(ih/2)*2[vout]");
        a.Add("-filter_complex"); a.Add(graph.ToString());
        a.Add("-map"); a.Add("[vout]");
        a.Add("-map"); a.Add("0:a?");
        string ext = MediaFiles.Canonical(Path.GetExtension(output));   // the container ffmpeg writes (from the name)
        a.AddRange(VideoEncoder(ext, pixels, attempt));
        a.Add("-c:a"); a.Add("copy");
        a.Add("-map_metadata"); a.Add("0");
        if (ext is "mp4" or "m4v" or "mov") { a.Add("-movflags"); a.Add("+faststart"); }
        a.Add("-n");
        a.Add(output);
        return a;
    }

    /// <summary>How long a re-encode may take before it is stopped: 5 minutes, plus 4 minutes per minute of clip
    /// (12 above 1080p — a 4K software encode runs well below real time), at most 6 hours.</summary>
    public static TimeSpan TimeoutForEncode(double clipSeconds, long pixels)
    {
        double perMinute = pixels > 1920L * 1088 ? 12 : 4;
        double minutes = 5 + Math.Max(0, double.IsFinite(clipSeconds) ? clipSeconds : 0) / 60 * perMinute;
        return TimeSpan.FromMinutes(Math.Min(360, minutes));
    }

    /// <summary>
    /// One line of ffmpeg's "-progress pipe:1" output → how far it is (0..1), or null when the line does not say.
    /// "out_time_us=…" (and the misnamed "out_time_ms=…", also microseconds) against <paramref name="totalSeconds"/>;
    /// "progress=end" is 1. "N/A" and garbage are null.
    /// </summary>
    public static double? ProgressOf(string? line, double totalSeconds)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
        if (key == "progress") return value == "end" ? 1 : null;
        if (!(totalSeconds > 0)) return null;
        double seconds;
        if (key is "out_time_us" or "out_time_ms")
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) || us < 0) return null;
            seconds = us / 1_000_000.0;
        }
        else if (key == "out_time")
        {
            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) || ts < TimeSpan.Zero) return null;
            seconds = ts.TotalSeconds;
        }
        else return null;
        return Math.Clamp(seconds / totalSeconds, 0, 1);
    }

    /// <summary>ffmpeg said it has no such encoder (an LGPL or minimal build): try the next choice.</summary>
    public static bool IsMissingEncoder(string detail) =>
        detail.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("Encoder not found", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("Unrecognized option 'crf'", StringComparison.OrdinalIgnoreCase);
}
