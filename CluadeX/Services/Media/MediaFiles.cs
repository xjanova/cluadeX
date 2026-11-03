using System.IO;

namespace CluadeX.Services.Media;

/// <summary>What the editor does with a file it opens: text (AvalonEdit) or one of the media editors.</summary>
public enum MediaKind { None, Image, Audio, Video }

/// <summary>
/// Which files open in the media editor (Resources/MediaEditor/media.html) instead of as text, and the small rules
/// around saving them. Owner, 2026-11-03: "เปิดไฟล์งานที่เป็นรูปภาพ ต้องแสดงได้ และแก้ไขพื้นฐานได้ … ไฟล์เสียงต้องเล่นได้ …
/// ตัดต่อพื้นฐาน … video ก็เช่นกัน" — before this an image, a song or a clip in the project only said "can't open".
/// Pure (no UI, no WebView2) so the harness checks it directly.
/// </summary>
public static class MediaFiles
{
    /// <summary>By extension: what Chromium (WebView2) shows or plays. A file whose codec it cannot decode still opens —
    /// the page says so instead of the editor saying "can't open". (.ts is TypeScript here, never MPEG-TS.)</summary>
    private static readonly Dictionary<string, MediaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = MediaKind.Image, ["jpg"] = MediaKind.Image, ["jpeg"] = MediaKind.Image, ["jfif"] = MediaKind.Image,
        ["gif"] = MediaKind.Image, ["bmp"] = MediaKind.Image, ["webp"] = MediaKind.Image, ["ico"] = MediaKind.Image,
        ["avif"] = MediaKind.Image, ["svg"] = MediaKind.Image,

        ["mp3"] = MediaKind.Audio, ["wav"] = MediaKind.Audio, ["ogg"] = MediaKind.Audio, ["oga"] = MediaKind.Audio,
        ["m4a"] = MediaKind.Audio, ["aac"] = MediaKind.Audio, ["flac"] = MediaKind.Audio, ["opus"] = MediaKind.Audio,
        ["weba"] = MediaKind.Audio,

        ["mp4"] = MediaKind.Video, ["m4v"] = MediaKind.Video, ["webm"] = MediaKind.Video, ["mov"] = MediaKind.Video,
        ["mkv"] = MediaKind.Video, ["ogv"] = MediaKind.Video,
    };

    /// <summary>The media kind of <paramref name="path"/> by its extension; None = a text tab as before.</summary>
    public static MediaKind KindOf(string? path)
    {
        string ext = ExtOf(path);
        return ext.Length > 0 && Kinds.TryGetValue(ext, out var k) ? k : MediaKind.None;
    }

    public static bool IsMedia(string? path) => KindOf(path) != MediaKind.None;

    /// <summary>Lower-case extension without the dot ("" for none).</summary>
    public static string ExtOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetExtension(path).TrimStart('.').ToLowerInvariant(); }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>An SVG is shown, not edited: it is a drawing made of text (its source opens as text from the page).</summary>
    public static bool IsViewOnly(string? path) => ExtOf(path) == "svg";

    /// <summary>The image formats the page can write (canvas.toBlob): PNG, JPEG, WebP. GIF / BMP / ICO edits are
    /// saved as one of these.</summary>
    public static readonly IReadOnlyList<string> ImageSaveFormats = new[] { "png", "jpg", "webp" };

    /// <summary>One spelling per format, so "photo.jpeg" overwritten with a JPEG counts as the same format.</summary>
    public static string Canonical(string ext)
    {
        ext = (ext ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpeg" or "jfif" or "jpe" => "jpg",
            "oga" => "ogg",
            _ => ext,
        };
    }

    /// <summary>Overwriting the original keeps its format: a PNG written over "photo.jpg" would be a PNG with a .jpg
    /// name. Only a same-format result may replace the original (and only after the confirmation).</summary>
    public static bool SameFormat(string originalPath, string outputExt)
        => Canonical(ExtOf(originalPath)) == Canonical(outputExt) && Canonical(outputExt).Length > 0;

    /// <summary>
    /// "Save as copy": a new name next to the original that no file has yet — "photo.png" → "photo-edit.png", then
    /// "photo-edit-2.png", … A save never lands on an existing file without asking; this picks around it instead.
    /// <paramref name="ext"/> is the format written (may differ from the original's); <paramref name="suffix"/> names
    /// what the copy is ("edit", "cut", "frame").
    /// </summary>
    public static string CopyPathFor(string originalPath, string ext, string suffix = "edit", Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        string dir = Path.GetDirectoryName(originalPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(originalPath);
        if (stem.Length == 0) stem = "media";
        ext = (ext ?? "").TrimStart('.');
        if (ext.Length == 0) ext = ExtOf(originalPath);
        string dot = ext.Length > 0 ? "." + ext : "";
        suffix = string.IsNullOrWhiteSpace(suffix) ? "edit" : suffix;
        for (int n = 1; n < 10_000; n++)
        {
            string name = n == 1 ? $"{stem}-{suffix}{dot}" : $"{stem}-{suffix}-{n}{dot}";
            string candidate = Path.Combine(dir, name);
            if (!exists(candidate)) return candidate;
        }
        // 10 000 copies already: a unique name rather than a loop that never ends.
        return Path.Combine(dir, $"{stem}-{suffix}-{Guid.NewGuid():N}{dot}");
    }

    /// <summary>A file extension the page asked to write, made safe: letters and digits only, at most 5, else null.
    /// (It ends up in a path; "../x" or "png:stream" must never get there.)</summary>
    public static string? SafeExt(string? ext)
    {
        ext = (ext ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (ext.Length is 0 or > 5) return null;
        foreach (char c in ext) if (!char.IsAsciiLetterOrDigit(c)) return null;
        return ext;
    }

    /// <summary>The largest file the page decodes for a waveform and edits. Decoding holds the whole song as 32-bit
    /// samples (a 20-minute stereo track is ~460 MB) — past this it plays with the seek bar only.</summary>
    public const long MaxAudioEditBytes = 250L * 1024 * 1024;
    /// <summary>…and the longest (seconds); the page checks it once the player knows the duration.</summary>
    public const double MaxAudioEditSeconds = 20 * 60;

    /// <summary>The largest single file the page may send back (an edited image, a WAV, a recorded clip).</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>"2.3 MB" — the size shown in the status line (invariant digits, so Thai and English read the same).</summary>
    public static string SizeText(long bytes)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes < 1024) return bytes.ToString(ci) + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString(kb < 10 ? "0.0" : "0", ci) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString(mb < 10 ? "0.0" : "0", ci) + " MB";
        return (mb / 1024.0).ToString("0.00", ci) + " GB";
    }
}
