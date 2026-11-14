using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Media;

/// <summary>How the covered part looks.</summary>
public enum PrivacyStyle { Blur, Pixelate, Black }

/// <summary>What to cover and how.</summary>
public sealed record PrivacyOptions(bool Faces = true, bool Plates = true, PrivacyStyle Style = PrivacyStyle.Blur);

/// <summary>How a blur run ended, and what it found.</summary>
public sealed record PrivacyResult(FfmpegOutcome Outcome, string Detail, int Frames = 0, int FramesCovered = 0,
    int MostFaces = 0, int MostPlates = 0);

/// <summary>A video's or picture's facts the blur needs (from ffprobe).</summary>
public sealed record PrivacyProbe(int Width, int Height, string Rate, double Fps, double Duration, bool HasAudio, bool IsStill);

/// <summary>
/// Keeps a box on screen through the frames the detector misses: a face turned for a moment, a plate behind a passing
/// pole. Each detection step returns what is found now plus what was found in the last <see cref="Hold"/> steps and not
/// matched since. Pure: the harness drives it with hand-made boxes.
/// </summary>
public sealed class PrivacyTracker
{
    private sealed class Track { public PrivacyBox Box; public int Missed; }
    private readonly List<Track> _tracks = new();

    /// <summary>Detection steps a lost box stays covered (≈ 0.6 s at five steps a second).</summary>
    public int Hold { get; init; } = 3;

    public List<PrivacyBox> Step(IReadOnlyList<PrivacyBox> found)
    {
        var used = new bool[_tracks.Count];
        foreach (var f in found)
        {
            int best = -1;
            float bestIou = 0.05f;
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (used[i] || _tracks[i].Box.Kind != f.Kind) continue;
                float iou = PrivacyBox.IoU(_tracks[i].Box, f);
                if (iou > bestIou) { bestIou = iou; best = i; }
            }
            if (best >= 0) { used[best] = true; _tracks[best].Box = f; _tracks[best].Missed = 0; }
        }
        for (int i = _tracks.Count - 1; i >= 0; i--)
            if (!used[i] && ++_tracks[i].Missed > Hold) _tracks.RemoveAt(i);
        foreach (var f in found)
            if (!_tracks.Any(t => t.Missed == 0 && t.Box.Equals(f))) _tracks.Add(new Track { Box = f });
        return _tracks.Select(t => t.Box).ToList();
    }
}

/// <summary>
/// The privacy blur: faces and licence plates found by <see cref="PrivacyDetector"/> are covered in every frame of a video
/// (or in a picture). ffmpeg decodes the picture to raw BGR frames on a pipe, this code covers the boxes, and a second
/// ffmpeg encodes the frames back into the same container with the original sound copied. Detection runs about five
/// times a second of video; between two detections a frame is covered with the boxes of both, so a moving face stays
/// under the blur. Never writes over an existing file; a stopped or failed run leaves no file behind.
/// </summary>
public static class PrivacyBlur
{
    /// <summary>Detections per second of video.</summary>
    public const double DetectionsPerSecond = 5;

    public static readonly HashSet<string> StillExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff" };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp" };

    public static bool CanBlur(string path) =>
        StillExtensions.Contains(Path.GetExtension(path)) || VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>The extension (no dot) a blurred copy is written as: a picture and an mp4 / mov / mkv / webm keep theirs;
    /// other video containers become mp4 (H.264 does not go into every old one).</summary>
    public static string OutputExt(string input)
    {
        string ext = MediaFiles.ExtOf(input);
        if (StillExtensions.Contains("." + ext)) return ext;
        return MediaFiles.Canonical(ext) is "mp4" or "m4v" or "mov" or "mkv" or "webm" ? ext : "mp4";
    }

    // ───────────────────────────── covering one box ─────────────────────────────

    /// <summary>The part of a found box that is covered: a face grows to take in hair, ears and chin; a plate a little.</summary>
    public static PrivacyBox Cover(PrivacyBox b, int w, int h) => b.Kind == PrivacyKind.Face
        ? b.Grow(0.22f, 0.32f, 0.22f, 0.18f, w, h)
        : b.Grow(0.12f, 0.2f, 0.12f, 0.2f, w, h);

    /// <summary>
    /// Cover <paramref name="box"/> (already grown) in the BGR24 <paramref name="frame"/>. Blur = a mosaic first (the
    /// detail is thrown away, not just softened — a plain blur of a plate can be read back), then three box-blur passes so
    /// it looks smooth. Faces are covered in an ellipse with a soft edge, plates in a rectangle.
    /// </summary>
    public static void Apply(byte[] frame, int w, int h, PrivacyBox box, PrivacyStyle style)
    {
        int x1 = Math.Clamp((int)MathF.Floor(box.X1), 0, w), y1 = Math.Clamp((int)MathF.Floor(box.Y1), 0, h);
        int x2 = Math.Clamp((int)MathF.Ceiling(box.X2), 0, w), y2 = Math.Clamp((int)MathF.Ceiling(box.Y2), 0, h);
        int bw = x2 - x1, bh = y2 - y1;
        if (bw < 2 || bh < 2) return;
        int stride = w * 3;

        // the covered picture of the box, in its own buffer
        var region = new byte[bw * bh * 3];
        for (int y = 0; y < bh; y++) Buffer.BlockCopy(frame, (y1 + y) * stride + x1 * 3, region, y * bw * 3, bw * 3);
        if (style == PrivacyStyle.Black) Array.Clear(region);
        else
        {
            int block = box.Kind == PrivacyKind.Face
                ? Math.Max(6, (int)Math.Ceiling(Math.Max(bw, bh) / 9.0))
                : Math.Max(4, (int)Math.Ceiling(bh / 2.2));
            Mosaic(region, bw, bh, block);
            if (style == PrivacyStyle.Blur)
            {
                int radius = Math.Max(2, block / 2 + 1);
                for (int pass = 0; pass < 3; pass++) BoxBlur(region, bw, bh, radius);
            }
        }

        bool ellipse = box.Kind == PrivacyKind.Face;
        float cx = bw / 2f, cy = bh / 2f, rx = bw / 2f, ry = bh / 2f;
        float feather = 0.12f;   // fraction of the radius that fades from covered to the picture
        for (int y = 0; y < bh; y++)
        {
            int fRow = (y1 + y) * stride + x1 * 3, rRow = y * bw * 3;
            for (int x = 0; x < bw; x++)
            {
                float a = 1f;
                if (ellipse)
                {
                    float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d >= 1f) continue;
                    if (d > 1f - feather) a = (1f - d) / feather;
                }
                int fi = fRow + x * 3, ri = rRow + x * 3;
                if (a >= 1f)
                {
                    frame[fi] = region[ri]; frame[fi + 1] = region[ri + 1]; frame[fi + 2] = region[ri + 2];
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                        frame[fi + c] = (byte)(frame[fi + c] + (region[ri + c] - frame[fi + c]) * a + 0.5f);
                }
            }
        }
    }

    /// <summary>Every <paramref name="block"/>² cell takes its average colour.</summary>
    public static void Mosaic(byte[] img, int w, int h, int block)
    {
        if (block < 2) return;
        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int ex = Math.Min(bx + block, w), ey = Math.Min(by + block, h);
            long b = 0, g = 0, r = 0;
            int n = 0;
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
            {
                int i = (y * w + x) * 3;
                b += img[i]; g += img[i + 1]; r += img[i + 2]; n++;
            }
            byte B = (byte)(b / n), G = (byte)(g / n), R = (byte)(r / n);
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++)
            {
                int i = (y * w + x) * 3;
                img[i] = B; img[i + 1] = G; img[i + 2] = R;
            }
        }
    }

    /// <summary>One separable box blur of <paramref name="radius"/> (edges clamped).</summary>
    public static void BoxBlur(byte[] img, int w, int h, int radius)
    {
        if (radius < 1) return;
        var tmp = new byte[img.Length];
        int win = radius * 2 + 1;
        // horizontal: img → tmp
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 3;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += img[row + Math.Clamp(k, 0, w - 1) * 3 + c];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x * 3 + c] = (byte)(sum / win);
                    sum += img[row + Math.Min(x + radius + 1, w - 1) * 3 + c] - img[row + Math.Max(x - radius, 0) * 3 + c];
                }
            }
        }
        // vertical: tmp → img
        for (int x = 0; x < w; x++)
        for (int c = 0; c < 3; c++)
        {
            int col = x * 3 + c, sum = 0;
            for (int k = -radius; k <= radius; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w * 3 + col];
            for (int y = 0; y < h; y++)
            {
                img[y * w * 3 + col] = (byte)(sum / win);
                sum += tmp[Math.Min(y + radius + 1, h - 1) * w * 3 + col] - tmp[Math.Max(y - radius, 0) * w * 3 + col];
            }
        }
    }

    /// <summary>Cover each box of <paramref name="boxes"/> (grown first) in the frame.</summary>
    public static void CoverAll(byte[] frame, int w, int h, IEnumerable<PrivacyBox> boxes, PrivacyStyle style)
    {
        foreach (var b in boxes) Apply(frame, w, h, Cover(b, w, h), style);
    }

    // ───────────────────────────── probing ─────────────────────────────

    /// <summary>ffprobe beside the ffmpeg in use (the winget / Gyan builds ship both), else on PATH.</summary>
    public static string? LocateFfprobe(string ffmpeg)
    {
        try
        {
            string beside = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (File.Exists(beside)) return beside;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = Path.Combine(dir.Trim(), "ffprobe.exe");
                if (File.Exists(p)) return p;
            }
        }
        catch { /* not found */ }
        return null;
    }

    public static IReadOnlyList<string> ProbeArguments(string input) => new[]
    {
        "-v", "error", "-print_format", "json",
        "-show_entries", "format=duration:stream=codec_type,width,height,avg_frame_rate,r_frame_rate"
            + ":stream_disposition=attached_pic:stream_tags=rotate:stream_side_data=rotation",
        input,
    };

    /// <summary>ffprobe's JSON → the picture's size as decoded (turned when the file says it is rotated by 90°), its
    /// frame rate and length, and whether there is sound. Pure: the harness checks it.</summary>
    public static PrivacyProbe? ParseProbe(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams)) return null;
            JsonElement? video = null;
            bool audio = false;
            foreach (var s in streams.EnumerateArray())
            {
                string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                bool attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video == null && !attached) video = s;
                if (type == "audio") audio = true;
            }
            if (video is not { } v) return null;
            int w = v.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
            int h = v.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
            if (w <= 0 || h <= 0) return null;
            int rotation = 0;
            if (v.TryGetProperty("side_data_list", out var sdl))
                foreach (var sd in sdl.EnumerateArray())
                    if (sd.TryGetProperty("rotation", out var rot) && rot.TryGetInt32(out int r)) rotation = r;
            if (rotation == 0 && v.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var tr)
                && int.TryParse(tr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r2)) rotation = r2;
            if (Math.Abs(rotation) % 180 == 90) (w, h) = (h, w);

            string rate = Rate(v, "avg_frame_rate") ?? Rate(v, "r_frame_rate") ?? "25";
            double fps = RateValue(rate);
            if (!(fps > 0) || fps > 240) { rate = "25"; fps = 25; }
            double duration = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var du))
                double.TryParse(du.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            bool still = StillExtensions.Contains(Path.GetExtension(path));
            return new PrivacyProbe(w, h, rate, fps, still ? 0 : duration, audio && !still, still);
        }
        catch { return null; }
    }

    private static string? Rate(JsonElement v, string name)
    {
        if (!v.TryGetProperty(name, out var r)) return null;
        string s = r.GetString() ?? "";
        return RateValue(s) > 0 ? s : null;
    }

    /// <summary>"30000/1001" → 29.97; "25" → 25; garbage or "0/0" → 0.</summary>
    public static double RateValue(string rate)
    {
        var parts = (rate ?? "").Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0;
        if (parts.Length == 1) return num;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) || den == 0) return 0;
        return num / den;
    }

    public static async Task<PrivacyProbe?> ProbeAsync(string ffmpeg, string input, CancellationToken ct)
    {
        string? ffprobe = LocateFfprobe(ffmpeg);
        if (ffprobe == null) return null;
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in ProbeArguments(input)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        var err = p.StandardError.ReadToEndAsync(ct);
        string json = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await err.ConfigureAwait(false);
        return p.ExitCode == 0 ? ParseProbe(json, input) : null;
    }

    // ───────────────────────────── the ffmpeg pair ─────────────────────────────

    /// <summary>The frame size the pipes carry: a video's sides rounded down to even (a 4:2:0 encoder needs it), a
    /// picture's as they are.</summary>
    public static (int W, int H) PipeSize(PrivacyProbe p) => p.IsStill ? (p.Width, p.Height) : (p.Width & ~1, p.Height & ~1);

    /// <summary>ffmpeg decoding the picture to raw BGR24 frames on stdout, at a constant frame rate (so the frames the
    /// encoder gets line up with the copied sound), cropped to <see cref="PipeSize"/>.</summary>
    public static IReadOnlyList<string> DecodeArguments(string input, PrivacyProbe p)
    {
        var (w, h) = PipeSize(p);
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-map", "0:v:0",
            "-vf", $"format=bgr24,crop={w}:{h}:0:0" };   // RGB first: a crop of 4:2:0 rounds an odd side down (a 1759×871 JPEG came out 1758×870)
        if (p.IsStill) { a.Add("-frames:v"); a.Add("1"); }
        else { a.Add("-fps_mode"); a.Add("cfr"); a.Add("-r"); a.Add(p.Rate); }
        a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" });
        return a;
    }

    /// <summary>ffmpeg taking raw BGR24 frames on stdin and writing <paramref name="output"/> (its container from the
    /// name), the original's sound copied in and its metadata kept. <paramref name="encoderAttempt"/> picks the fallback
    /// video encoder of <see cref="MediaOverlay.VideoEncoder"/>. Never overwrites (-n).</summary>
    public static IReadOnlyList<string> EncodeArguments(string input, string output, PrivacyProbe p, int encoderAttempt)
    {
        var (w, h) = PipeSize(p);
        string ext = MediaFiles.Canonical(Path.GetExtension(output));
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{w}x{h}" };
        if (!p.IsStill) { a.Add("-framerate"); a.Add(p.Rate); }
        a.AddRange(new[] { "-i", "pipe:0" });
        if (p.IsStill)
        {
            a.AddRange(new[] { "-frames:v", "1" });
            if (ext is "jpg" or "jpeg") a.AddRange(new[] { "-q:v", "2" });
            if (ext == "webp") a.AddRange(new[] { "-quality", "92" });
            a.Add("-n"); a.Add(output);
            return a;
        }
        a.AddRange(new[] { "-i", input, "-map", "0:v:0" });
        if (p.HasAudio) { a.Add("-map"); a.Add("1:a?"); }
        a.AddRange(MediaOverlay.VideoEncoder(ext, (long)w * h, encoderAttempt));
        if (p.HasAudio)
        {
            // the same container takes the same sound as it is; another one (an .avi saved as .mp4) gets AAC
            bool same = MediaFiles.Canonical(Path.GetExtension(input)) == ext;
            a.AddRange(same ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", "192k" });
        }
        a.AddRange(new[] { "-map_metadata", "1" });
        if (ext is "mp4" or "m4v" or "mov") a.AddRange(new[] { "-movflags", "+faststart" });
        a.Add("-shortest");
        a.Add("-n");
        a.Add(output);
        return a;
    }

    private static readonly Dictionary<string, string> EncoderListCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which of <see cref="MediaOverlay.VideoEncoder"/>'s choices this ffmpeg has (an LGPL build has no
    /// libx264): asked once per ffmpeg. The frames are streamed, so the encoder cannot be retried after a failure.</summary>
    public static async Task<int> EncoderAttemptAsync(string ffmpeg, string ext, CancellationToken ct)
    {
        string list;
        lock (EncoderListCache) EncoderListCache.TryGetValue(ffmpeg, out list!);
        if (list == null)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-encoders");
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEndAsync(ct);
                list = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                await err.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { list = ""; }
            lock (EncoderListCache) EncoderListCache[ffmpeg] = list;
        }
        for (int attempt = 0; attempt < MediaOverlay.EncoderAttempts(ext); attempt++)
        {
            var enc = MediaOverlay.VideoEncoder(ext, 0, attempt);
            int i = Array.IndexOf(enc.ToArray(), "-c:v");
            string name = i >= 0 && i + 1 < enc.Count ? enc[i + 1] : "";
            if (list.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(list, @"\s" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s"))
                return attempt;
        }
        return 0;
    }

    /// <summary>How long a blur may take: a re-encode's allowance (<see cref="MediaOverlay.TimeoutForEncode"/>) twice
    /// over, for the detection.</summary>
    public static TimeSpan TimeoutFor(PrivacyProbe p) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(8).Ticks, MediaOverlay.TimeoutForEncode(p.Duration, (long)p.Width * p.Height).Ticks * 2));

    /// <summary>Every how many frames the detector runs.</summary>
    public static int DetectEvery(double fps) => Math.Max(1, (int)Math.Round(fps / DetectionsPerSecond));

    /// <summary>
    /// Blur <paramref name="input"/> into <paramref name="output"/> (which must not exist). <paramref name="detect"/> finds
    /// the boxes in a BGR24 frame (<see cref="PrivacyDetector.Detect"/>; the harness passes its own). <paramref name="progress"/>
    /// gets 0..1 now and then. Never throws for an ffmpeg or model problem — the result says what happened.
    /// </summary>
    public static async Task<PrivacyResult> RunAsync(string ffmpeg, string input, string output, PrivacyOptions options,
        Func<byte[], int, int, List<PrivacyBox>> detect, Action<double>? progress, CancellationToken ct)
    {
        if (File.Exists(output) || Directory.Exists(output))
            return new PrivacyResult(FfmpegOutcome.Failed, "output already exists: " + Path.GetFileName(output));
        PrivacyProbe? probe;
        try { probe = await ProbeAsync(ffmpeg, input, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new PrivacyResult(FfmpegOutcome.Cancelled, ""); }
        if (probe == null) return new PrivacyResult(FfmpegOutcome.Failed, "ffprobe could not read the picture of " + Path.GetFileName(input));
        var (w, h) = PipeSize(probe);
        if (w < 2 || h < 2) return new PrivacyResult(FfmpegOutcome.Failed, "the picture is too small");
        int attempt;
        try { attempt = probe.IsStill ? 0 : await EncoderAttemptAsync(ffmpeg, MediaFiles.Canonical(Path.GetExtension(output)), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new PrivacyResult(FfmpegOutcome.Cancelled, ""); }

        using var timer = new CancellationTokenSource(TimeoutFor(probe));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked.Token;
        var decTail = new StringBuilder();
        var encTail = new StringBuilder();
        Process? dec = null, enc = null;
        int frames = 0, covered = 0, mostFaces = 0, mostPlates = 0;
        bool ok = false;
        try
        {
            dec = Start(ffmpeg, DecodeArguments(input, probe), stdin: false, decTail);
            enc = Start(ffmpeg, EncodeArguments(input, output, probe, attempt), stdin: true, encTail);
            var src = dec.StandardOutput.BaseStream;
            var dst = enc.StandardInput.BaseStream;
            int frameBytes = w * h * 3;
            int every = probe.IsStill ? 1 : DetectEvery(probe.Fps);
            double expected = probe.IsStill ? 1 : Math.Max(1, probe.Duration * probe.Fps);
            var tracker = new PrivacyTracker();
            var pending = new List<byte[]>();
            var free = new Stack<byte[]>();
            List<PrivacyBox> before = new();
            long lastTick = 0;

            async Task Flush(List<PrivacyBox> after)
            {
                var boxes = before.Concat(after).ToList();
                foreach (var f in pending)
                {
                    if (boxes.Count > 0) { CoverAll(f, w, h, boxes, options.Style); covered++; }
                    await dst.WriteAsync(f.AsMemory(0, frameBytes), token).ConfigureAwait(false);
                    free.Push(f);
                    frames++;
                }
                pending.Clear();
                long now = Environment.TickCount64;
                if (progress != null && now - lastTick > 250) { lastTick = now; progress(Math.Clamp(frames / expected, 0, 0.99)); }
            }

            for (int index = 0; ; index++)
            {
                var buf = free.Count > 0 ? free.Pop() : new byte[frameBytes];
                if (!await ReadFrameAsync(src, buf, frameBytes, token).ConfigureAwait(false)) break;
                if (index % every == 0)
                {
                    var found = await Task.Run(() => detect(buf, w, h), token).ConfigureAwait(false);
                    found = found.Where(b => b.Kind == PrivacyKind.Face ? options.Faces : options.Plates).ToList();
                    mostFaces = Math.Max(mostFaces, found.Count(b => b.Kind == PrivacyKind.Face));
                    mostPlates = Math.Max(mostPlates, found.Count(b => b.Kind == PrivacyKind.Plate));
                    var now = tracker.Step(found);
                    await Flush(now).ConfigureAwait(false);   // the frames since the last detection: both ends' boxes
                    before = now;
                }
                pending.Add(buf);
            }
            await Flush(new List<PrivacyBox>()).ConfigureAwait(false);
            dst.Close();
            await dec.WaitForExitAsync(token).ConfigureAwait(false);
            await enc.WaitForExitAsync(token).ConfigureAwait(false);
            dec.WaitForExit();
            enc.WaitForExit();
            if (frames == 0) return Fail(FfmpegOutcome.Failed, "no frames were decoded: " + Tail(decTail));
            if (dec.ExitCode != 0) return Fail(FfmpegOutcome.Failed, $"decode exit {dec.ExitCode}: {Tail(decTail)}");
            if (enc.ExitCode != 0 || !File.Exists(output)) return Fail(FfmpegOutcome.Failed, $"encode exit {enc.ExitCode}: {Tail(encTail)}");
            ok = true;
            progress?.Invoke(1);
            return new PrivacyResult(FfmpegOutcome.Done, Tail(encTail), frames, covered, mostFaces, mostPlates);
        }
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested ? FfmpegOutcome.Cancelled : FfmpegOutcome.TimedOut, Tail(encTail));
        }
        catch (IOException ex)
        {
            // the encoder closed its stdin: it failed — its own words say why
            Kill(enc); Kill(dec);
            try { enc?.WaitForExit(3000); } catch { }
            return Fail(FfmpegOutcome.Failed, $"{ex.Message} — {Tail(encTail)} {Tail(decTail)}".Trim());
        }
        catch (Exception ex)
        {
            return Fail(FfmpegOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (!ok) { Kill(dec); Kill(enc); }
            dec?.Dispose();
            enc?.Dispose();
            if (!ok) DeleteQuietly(output);
        }

        PrivacyResult Fail(FfmpegOutcome o, string detail) => new(o, detail, frames, covered, mostFaces, mostPlates);
    }

    private static Process Start(string exe, IReadOnlyList<string> args, bool stdin, StringBuilder tail)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = stdin, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (tail)
            {
                tail.AppendLine(e.Data);
                if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
            }
        };
        if (!p.Start()) throw new InvalidOperationException("ffmpeg did not start");
        p.BeginErrorReadLine();
        if (stdin) _ = p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);   // the encoder writes nothing useful there
        return p;
    }

    private static async Task<bool> ReadFrameAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n == 0) return false;   // the end (a partial last frame is dropped)
            got += n;
        }
        return true;
    }

    private static string Tail(StringBuilder sb) { lock (sb) return sb.ToString().Trim(); }

    private static void Kill(Process? p)
    {
        try
        {
            if (p != null && !p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
        }
        catch { /* already gone */ }
    }

    private static void DeleteQuietly(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
