using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Attachments;

/// <summary>
/// What an attached audio or video file is — length, picture size, codecs — so the model can talk about it and knows
/// how to work with it, instead of being told to hex-dump it (owner 2026-11-06: "ลากไฟล์มาวางได้ทุกรูปแบบ … งาน media").
/// Read by ffprobe when it is installed, else from the properties Windows' own media handlers give Explorer.
/// Never throws and never waits long: a file it cannot read is still described by its type and size.
/// </summary>
public static class MediaInfo
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".amr", ".mid", ".midi",
    };

    // Not ".ts": that is TypeScript far more often than an MPEG transport stream.
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".m4v", ".flv", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts",
    };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsAudio(path) || IsVideo(path);

    /// <summary>What was found out about a media file. Any part can be missing.</summary>
    public sealed record Facts(TimeSpan? Duration, string? Video, string? Audio);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>The file described for the model: what it is, what is in it, and how to work with it.</summary>
    public static string Describe(string path)
    {
        bool video = IsVideo(path);
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var facts = Probe(path);
        var sb = new StringBuilder($"{name} is {(video ? "a video" : "an audio")} file ({ext})");
        if (facts?.Duration is { } d) sb.Append(", ").Append(Length(d)).Append(" long");
        if (facts?.Video is { Length: > 0 } v) sb.Append(", video ").Append(v);
        if (facts?.Audio is { Length: > 0 } a) sb.Append(", audio ").Append(a);
        sb.Append(". There is no text in it to read here. ");
        bool ffmpeg = FindOnPath("ffmpeg.exe") != null;
        sb.Append(ffmpeg
            ? "ffmpeg is installed: run it through the shell to cut it (-ss / -to), convert it, pull the sound out (-vn)"
              + (video ? ", or take frames as pictures (-vf fps=1 frame_%03d.png) and look at them" : "")
              + ". "
            : "ffmpeg is not installed on this PC (winget install Gyan.FFmpeg would add it); without it, ask the user what they need from the file. ");
        sb.Append("A model whose own file reader takes audio or video (Gemini) can open it by its path.");
        return sb.ToString();
    }

    /// <summary>ffprobe if it is on PATH, else the Windows property system; null when neither could read it.</summary>
    public static Facts? Probe(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (FindOnPath("ffprobe.exe") is { } ffprobe && FromFfprobe(ffprobe, path) is { } f) return f;
            return FromShell(path);
        }
        catch { return null; }
    }

    /// <summary>"2:31", "1:02:05", "0:04".</summary>
    public static string Length(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    /// <summary>ffprobe's JSON (-show_format -show_streams) read into facts. Pure: the harness checks it.</summary>
    public static Facts? ParseFfprobe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            TimeSpan? duration = null;
            if (root.TryGetProperty("format", out var fmt) && Str(fmt, "duration") is { } ds
                && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) && sec > 0)
                duration = TimeSpan.FromSeconds(sec);
            string? video = null, audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var s in streams.EnumerateArray())
                {
                    string type = Str(s, "codec_type") ?? "";
                    string codec = Str(s, "codec_name") ?? "?";
                    // An mp3's cover picture is a "video" stream too: not the file's picture.
                    if (type == "video" && video == null && !IsCoverArt(s))
                    {
                        video = codec;
                        if (Int(s, "width") is int w && Int(s, "height") is int h) video += $" {w}×{h}";
                    }
                    else if (type == "audio" && audio == null)
                    {
                        audio = codec;
                        if (Str(s, "sample_rate") is { } sr && int.TryParse(sr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hz))
                            audio += " " + (hz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";
                        if (Int(s, "channels") is int ch) audio += ch switch { 1 => " mono", 2 => " stereo", _ => $" {ch} channels" };
                    }
                }
            return duration == null && video == null && audio == null ? null : new Facts(duration, video, audio);
        }
        catch (JsonException) { return null; }

        static bool IsCoverArt(JsonElement s) =>
            s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap)
            && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;
    }

    private static Facts? FromFfprobe(string ffprobe, string path)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi);
        if (p == null) return null;
        // Both pipes read at once: a full stderr pipe would stall ffprobe while we wait on stdout.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        Task.WaitAll(new Task[] { stdout, stderr }, 1000);
        return p.ExitCode == 0 && stdout.IsCompletedSuccessfully ? ParseFfprobe(stdout.Result) : null;
    }

    /// <summary>The length and picture size Windows shows in Explorer (its own media handlers), on an STA thread of
    /// its own, given a few seconds at most.</summary>
    private static Facts? FromShell(string path)
    {
        Facts? found = null;
        var t = new Thread(() =>
        {
            object? shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                shell = Activator.CreateInstance(type);
                dynamic sh = shell!;
                dynamic? folder = sh.NameSpace(Path.GetDirectoryName(Path.GetFullPath(path)));
                dynamic? item = folder?.ParseName(Path.GetFileName(path));
                if (item == null) return;
                TimeSpan? duration = null;
                object? d = item.ExtendedProperty("System.Media.Duration");
                if (d != null && ulong.TryParse(Convert.ToString(d, CultureInfo.InvariantCulture), out ulong ticks) && ticks > 0)
                    duration = TimeSpan.FromTicks((long)Math.Min(ticks, (ulong)TimeSpan.MaxValue.Ticks));
                string? video = null;
                object? w = item.ExtendedProperty("System.Video.FrameWidth");
                object? h = item.ExtendedProperty("System.Video.FrameHeight");
                if (w != null && h != null && Convert.ToInt32(w, CultureInfo.InvariantCulture) > 0)
                    video = $"{Convert.ToInt32(w, CultureInfo.InvariantCulture)}×{Convert.ToInt32(h, CultureInfo.InvariantCulture)}";
                if (duration != null || video != null) found = new Facts(duration, video, null);
            }
            catch { /* no handler for this format, or the shell is unavailable */ }
            finally
            {
                if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }) { IsBackground = true, Name = "CluadeX media facts" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t.Join(ProbeTimeout) ? found : null;
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry */ }
        }
        return null;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
}
