using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;

namespace CluadeX.Services.Vision;

/// <summary>
/// What <see cref="ImageCodec"/> made of one image: the <see cref="Image"/> to send to a model (null on failure,
/// with <see cref="Error"/> saying why in one short English line the model can act on), and a JPEG copy for
/// <see cref="ToolImageStore"/> (null when none could be made — e.g. a WebP passed through without a decoder).
/// </summary>
public sealed record DecodedImage(ToolImage? Image, byte[]? JpegForStore, string? Error,
    int SourceWidth = 0, int SourceHeight = 0, bool Resized = false, string? SourceMediaType = null)
{
    public bool Ok => Image != null;
    public static DecodedImage Fail(string error) => new(null, null, error);

    /// <summary>The image goes out in another format than it came in (GIF/WebP/BMP → JPEG, an oversized PNG → JPEG).</summary>
    public bool Reencoded => Image != null && SourceMediaType != null && Image.MediaType != SourceMediaType;
}

/// <summary>
/// Turns image bytes from any tool (an MCP image item, a browser capture, a PNG in the project) into something a
/// vision model accepts:
/// <list type="bullet">
/// <item>the media type is SNIFFED from the bytes (APIs reject a media_type that doesn't match the data, and MCP
///   servers do mislabel), never trusted from the declared mime;</item>
/// <item>the long edge is at most maxEdge (Settings.BrowserScreenshotMaxEdge, 1568) and the area at most 1.15 MP —
///   what the models actually look at; anything bigger only costs tokens;</item>
/// <item>it is re-encoded as JPEG q80 when resized, when its base64 would pass the APIs' 5 MB per-image limit, or
///   when it is not a PNG or JPEG: an (animated) GIF goes out as its first frame — OpenAI refuses animated GIFs —
///   and a WebP as JPEG, since llama.cpp's mtmd and Ollama may not decode WebP. A PNG or JPEG that fits passes
///   through untouched.</item>
/// </list>
/// Guards: at most 20 MB of input, at most 50 MP / 20,000 px per edge of SOURCE pixels (a tiny PNG can claim
/// 100,000 × 100,000 — that is checked from the header before anything is decoded), malformed base64, SVG.
/// Every failure is a <see cref="DecodedImage.Fail"/> — nothing here throws into the agent loop.
///
/// WPF imaging objects are created on the calling thread and frozen; call <see cref="ProcessAsync"/> /
/// <see cref="FromBase64Async"/> from UI code so the decode runs on the thread pool.
/// </summary>
public static class ImageCodec
{
    public const int DefaultMaxEdge = 1568;
    public const double MaxMegapixels = 1.15;
    /// <summary>Above this many RAW bytes an image is re-encoded even when its size is fine. The APIs' 5 MB per-image
    /// limit is measured on the base64 (4/3 of the raw size): 3,750,000 raw bytes is 5,000,000 base64 characters.</summary>
    public const int MaxPassthroughBytes = 3_750_000;
    /// <summary>Largest image accepted at all (same as <see cref="ToolImageStore.MaxBytes"/>).</summary>
    public const int MaxInputBytes = ToolImageStore.MaxBytes;
    /// <summary>Decompression-bomb guard: source pixels and edge length checked from the header.</summary>
    public const long MaxSourcePixels = 50_000_000;
    public const int MaxSourceEdge = 20_000;
    public const int JpegQuality = 80;

    /// <summary>Decode base64 (a "data:…;base64," prefix, whitespace and URL-safe characters are tolerated).</summary>
    public static DecodedImage FromBase64(string? base64, string? declaredMime = null, int maxEdge = DefaultMaxEdge)
    {
        if (!TryDecodeBase64(base64, out var bytes, out var err)) return DecodedImage.Fail(err!);
        return FromBytes(bytes!, declaredMime, maxEdge);
    }

    public static Task<DecodedImage> FromBase64Async(string? base64, string? declaredMime = null, int maxEdge = DefaultMaxEdge) =>
        Task.Run(() => FromBase64(base64, declaredMime, maxEdge));

    public static Task<DecodedImage> ProcessAsync(byte[] bytes, string? declaredMime = null, int maxEdge = DefaultMaxEdge) =>
        Task.Run(() => FromBytes(bytes, declaredMime, maxEdge));

    /// <summary>Process raw image bytes. Never throws.</summary>
    public static DecodedImage FromBytes(byte[]? bytes, string? declaredMime = null, int maxEdge = DefaultMaxEdge)
    {
        try
        {
            if (bytes == null || bytes.Length < 8) return DecodedImage.Fail("the image data is empty");
            if (bytes.Length > MaxInputBytes)
                return DecodedImage.Fail($"the image is {bytes.Length / (1024.0 * 1024):0.#} MB (max {MaxInputBytes / (1024 * 1024)} MB)");
            maxEdge = Math.Clamp(maxEdge <= 0 ? DefaultMaxEdge : maxEdge, 64, 8000);

            string? sniffed = SniffMediaType(bytes);
            if (sniffed == "image/svg+xml" || (sniffed == null && (declaredMime ?? "").Contains("svg", StringComparison.OrdinalIgnoreCase)))
                return DecodedImage.Fail("SVG images are not supported — produce a PNG or JPEG instead");

            // Header only — no pixels yet.
            BitmapFrame frame;
            int w, h;
            try
            {
                using var probe = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return DecodedImage.Fail("the image has no frames");
                frame = decoder.Frames[0];
                w = frame.PixelWidth;
                h = frame.PixelHeight;
            }
            catch (Exception) when (sniffed == "image/webp")
            {
                // No WebP codec on this Windows (the Store "WebP Image Extensions" is missing): pass it through
                // unchanged when the header says it is already small enough, else give up.
                return WebPPassthrough(bytes, maxEdge);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                           or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException
                                           or OverflowException)
            {
                return DecodedImage.Fail(sniffed == null
                    ? "the data is not a PNG, JPEG, WebP or GIF image"
                    : $"the {sniffed} image is damaged and could not be decoded");
            }

            if (w <= 0 || h <= 0) return DecodedImage.Fail("the image has no pixels");
            if (w > MaxSourceEdge || h > MaxSourceEdge || (long)w * h > MaxSourcePixels)
                return DecodedImage.Fail($"the image is {w}x{h} — too large to decode safely (max {MaxSourcePixels / 1_000_000} MP)");

            var (tw, th) = FitSize(w, h, maxEdge, MaxMegapixels);
            bool resize = tw != w || th != h;
            // Only PNG and JPEG are taken by every vision backend as they are (GIF: OpenAI refuses animated ones;
            // WebP: llama.cpp / Ollama may not decode it) — everything else goes out as a JPEG of its first frame.
            bool modelFormat = sniffed is "image/png" or "image/jpeg";
            bool passthrough = !resize && modelFormat && bytes.Length <= MaxPassthroughBytes;

            if (passthrough)
            {
                // The model gets the original bytes; the store needs a JPEG.
                byte[]? jpeg = sniffed == "image/jpeg" ? bytes : TryEncodeJpeg(bytes, w, h, w, h);
                var img = new ToolImage(sniffed!, Convert.ToBase64String(bytes), w, h);
                return new DecodedImage(img, jpeg, null, w, h, false, sniffed);
            }

            byte[]? reencoded = TryEncodeJpeg(bytes, w, h, tw, th);
            if (reencoded == null)
                return DecodedImage.Fail($"the {sniffed ?? "image"} could not be re-encoded");
            var out1 = new ToolImage("image/jpeg", Convert.ToBase64String(reencoded), tw, th);
            return new DecodedImage(out1, reencoded, null, w, h, resize, sniffed);
        }
        catch (OutOfMemoryException)
        {
            return DecodedImage.Fail("not enough memory to decode the image");
        }
        catch (Exception ex)
        {
            // Never into the agent loop — and never the raw exception text (it can carry a temp path).
            System.Diagnostics.Debug.WriteLine($"[ImageCodec] {ex.GetType().Name}: {ex.Message}");
            return DecodedImage.Fail("the image could not be decoded");
        }
    }

    /// <summary>The size an image of <paramref name="w"/>×<paramref name="h"/> is sent at: long edge ≤ maxEdge and
    /// area ≤ maxMegapixels, aspect kept, never upscaled.</summary>
    public static (int Width, int Height) FitSize(int w, int h, int maxEdge, double maxMegapixels = MaxMegapixels)
    {
        if (w <= 0 || h <= 0) return (w, h);
        double scale = 1.0;
        int longEdge = Math.Max(w, h);
        if (longEdge > maxEdge) scale = Math.Min(scale, maxEdge / (double)longEdge);
        double maxPixels = maxMegapixels * 1_000_000;
        if ((double)w * h > maxPixels) scale = Math.Min(scale, Math.Sqrt(maxPixels / ((double)w * h)));
        if (scale >= 1.0) return (w, h);
        int tw = Math.Max(1, (int)Math.Floor(w * scale));
        int th = Math.Max(1, (int)Math.Floor(h * scale));
        // Floor can still leave the area a hair over the cap through rounding of the other edge — never over.
        while ((double)tw * th > maxPixels && tw > 1 && th > 1) { if (tw >= th) tw--; else th--; }
        return (tw, th);
    }

    /// <summary>Decode (scaled to tw×th when smaller), flatten any transparency onto white, encode JPEG q80.</summary>
    private static byte[]? TryEncodeJpeg(byte[] bytes, int w, int h, int tw, int th)
    {
        try
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream(bytes, writable: false);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = ms;
            if (tw != w || th != h)
            {
                bi.DecodePixelWidth = tw;
                bi.DecodePixelHeight = th;
            }
            bi.EndInit();
            bi.Freeze();

            BitmapSource src = bi;
            int sw = src.PixelWidth, sh = src.PixelHeight;
            // Always via BGRA: one path for every source format (indexed, gray, 16-bit, alpha).
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            int stride = sw * 4;
            var px = new byte[(long)stride * sh];
            bgra.CopyPixels(px, stride, 0);
            int rgbStride = (sw * 3 + 3) & ~3;
            var rgb = new byte[(long)rgbStride * sh];
            for (int y = 0; y < sh; y++)
            {
                int si = y * stride, di = y * rgbStride;
                for (int x = 0; x < sw; x++, si += 4, di += 3)
                {
                    int a = px[si + 3];
                    if (a == 255) { rgb[di] = px[si]; rgb[di + 1] = px[si + 1]; rgb[di + 2] = px[si + 2]; }
                    else
                    {
                        // Over white: transparent screenshot areas must not turn black.
                        int inv = 255 - a;
                        rgb[di] = (byte)((px[si] * a + 255 * inv) / 255);
                        rgb[di + 1] = (byte)((px[si + 1] * a + 255 * inv) / 255);
                        rgb[di + 2] = (byte)((px[si + 2] * a + 255 * inv) / 255);
                    }
                }
            }
            var flat = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr24, null, rgb, rgbStride);
            flat.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            enc.Frames.Add(BitmapFrame.Create(flat));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageCodec] encode failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static DecodedImage WebPPassthrough(byte[] bytes, int maxEdge)
    {
        if (!TryReadWebPSize(bytes, out int w, out int h))
            return DecodedImage.Fail("the WebP image could not be read (no WebP decoder on this PC)");
        var (tw, th) = FitSize(w, h, maxEdge, MaxMegapixels);
        if (tw != w || th != h || bytes.Length > MaxPassthroughBytes)
            return DecodedImage.Fail($"the WebP image is {w}x{h} and this PC has no WebP decoder to shrink it — install \"WebP Image Extensions\" from the Microsoft Store");
        return new DecodedImage(new ToolImage("image/webp", Convert.ToBase64String(bytes), w, h), null, null, w, h, false, "image/webp");
    }

    /// <summary>Width/height from a WebP header (VP8, VP8L, VP8X), without decoding.</summary>
    public static bool TryReadWebPSize(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 30 || SniffMediaType(b) != "image/webp") return false;
        string chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
        switch (chunk)
        {
            case "VP8X":
                w = 1 + (b[24] | b[25] << 8 | b[26] << 16);
                h = 1 + (b[27] | b[28] << 8 | b[29] << 16);
                return true;
            case "VP8L":
                if (b[20] != 0x2F) return false;
                int bits = b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24;
                w = (bits & 0x3FFF) + 1;
                h = ((bits >> 14) & 0x3FFF) + 1;
                return true;
            case "VP8 ":
                if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return false;
                w = (b[26] | b[27] << 8) & 0x3FFF;
                h = (b[28] | b[29] << 8) & 0x3FFF;
                return w > 0 && h > 0;
        }
        return false;
    }

    /// <summary>The media type the bytes really are (png, jpeg, gif, webp, bmp, tiff, svg), or null.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8') return "image/gif";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "image/webp";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
        if (b.Length >= 4 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0) || (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0 && b[3] == 0x2A))) return "image/tiff";
        // SVG is text: look for "<svg" near the start (after an optional XML prolog / BOM / whitespace).
        int n = Math.Min(b.Length, 512);
        if (n > 4)
        {
            string head = System.Text.Encoding.UTF8.GetString(b[..n]).TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
                return "image/svg+xml";
        }
        return null;
    }

    /// <summary>Strict-but-forgiving base64: a data-URL prefix, whitespace, URL-safe '-'/'_' and missing padding are
    /// accepted; anything else is a clear error. Length is checked BEFORE decoding.</summary>
    public static bool TryDecodeBase64(string? s, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = null;
        if (string.IsNullOrWhiteSpace(s)) { error = "the image data is empty"; return false; }
        int start = 0;
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = s.IndexOf(',');
            if (comma < 0 || !s.AsSpan(0, comma).Contains(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            { error = "the image data URL is not base64"; return false; }
            start = comma + 1;
        }
        long maxChars = (long)MaxInputBytes / 3 * 4 + 4096;   // + room for whitespace / line breaks
        if (s.Length - start > maxChars)
        { error = $"the image data is over {MaxInputBytes / (1024 * 1024)} MB"; return false; }

        var buf = new char[s.Length - start];
        int n = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n') continue;
            if (c == '-') c = '+';
            else if (c == '_') c = '/';
            else if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))
            { error = "the image data is not valid base64"; return false; }
            buf[n++] = c;
        }
        // Padding only at the end, and add what's missing.
        int firstPad = Array.IndexOf(buf, '=', 0, n);
        if (firstPad >= 0)
        {
            for (int i = firstPad; i < n; i++) if (buf[i] != '=') { error = "the image data is not valid base64"; return false; }
            if (n - firstPad > 2) { error = "the image data is not valid base64"; return false; }
        }
        int rem = n % 4;
        if (rem == 1) { error = "the image data is not valid base64 (truncated)"; return false; }
        string clean = new string(buf, 0, n) + (rem == 2 ? "==" : rem == 3 ? "=" : "");
        var outBuf = new byte[clean.Length / 4 * 3];
        if (!Convert.TryFromBase64String(clean, outBuf, out int written))
        { error = "the image data is not valid base64"; return false; }
        bytes = written == outBuf.Length ? outBuf : outBuf[..written];
        return true;
    }
}
