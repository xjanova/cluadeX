using System.IO;
using System.Text;

namespace CluadeX.Helpers;

/// <summary>
/// Reads and writes text files without changing how they are encoded.
///
/// Every agent write used to go through <c>File.WriteAllText(path, text, Encoding.UTF8)</c>, and
/// <see cref="Encoding.UTF8"/> emits a byte-order mark — so each edit silently prepended EF BB BF to
/// every BOM-less file it touched. That breaks a shebang script ("#!/bin/bash: not found"), a
/// package.json (Node's JSON.parse rejects a BOM), a PHP file (output before headers), and shows a
/// phantom line-1 change in git. Worse, a legacy-encoded file (Windows-874 / TIS-620 Thai,
/// Windows-1252) was decoded as UTF-8 and written back with every non-ASCII byte replaced by U+FFFD,
/// which cannot be undone.
///
/// The rule here: keep a BOM exactly when the file already had one, never add one to a new file, and
/// report text that is not valid UTF-8 so callers can refuse to rewrite it.
/// </summary>
public static class TextFileCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <param name="Text">Decoded content, BOM removed.</param>
    /// <param name="HasBom">The file started with a UTF-8 BOM.</param>
    /// <param name="IsValidUtf8">False when the bytes are not UTF-8; <see cref="Text"/> then carries
    /// replacement characters and must not be written back.</param>
    public readonly record struct TextFile(string Text, bool HasBom, bool IsValidUtf8);

    public static TextFile Read(string fullPath) => Decode(File.ReadAllBytes(fullPath));

    public static TextFile Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int offset = bom ? 3 : 0;
        try
        {
            return new TextFile(StrictUtf8.GetString(bytes, offset, bytes.Length - offset), bom, true);
        }
        catch (DecoderFallbackException)
        {
            return new TextFile(LenientUtf8.GetString(bytes, offset, bytes.Length - offset), bom, false);
        }
    }

    // ───────────── the editor: every encoding Notepad++ users meet, kept as it was ─────────────

    static TextFileCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public const int Utf16Le = 1200, Utf16Be = 1201, Thai = 874;

    /// <param name="Text">The text as the editor shows it.</param>
    /// <param name="HasBom">UTF-8 only: the file started with a BOM.</param>
    /// <param name="CodePage">0 = UTF-8; 1200 / 1201 = UTF-16 LE / BE (always with a BOM); else the legacy code
    /// page the file is written in (874 = Thai TIS-620 / Windows-874, 1252 = Western, …).</param>
    /// <param name="Faithful">False when no encoding reproduces the bytes exactly: the text is a lossy guess
    /// and must never be saved over the file.</param>
    public readonly record struct EditorText(string Text, bool HasBom, int CodePage, bool Faithful);

    /// <summary>
    /// How the editor reads a file — Notepad++-style: UTF-8 (with or without BOM), UTF-16 with a BOM, else a
    /// legacy single-/double-byte code page (Thai first when the bytes look like TIS-620, else this PC's ANSI code
    /// page). A legacy reading counts only when encoding the text back gives exactly the same bytes, so a save can
    /// never change a byte the user did not edit.
    /// </summary>
    public static EditorText DecodeForEditor(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            int cp = bytes[0] == 0xFF ? Utf16Le : Utf16Be;
            try
            {
                if ((bytes.Length - 2) % 2 != 0) throw new DecoderFallbackException();
                return new EditorText(StrictEncoding(cp).GetString(bytes, 2, bytes.Length - 2), false, cp, true);
            }
            catch (DecoderFallbackException) { /* not really UTF-16: fall through */ }
        }
        var utf8 = Decode(bytes);
        if (utf8.IsValidUtf8) return new EditorText(utf8.Text, utf8.HasBom, 0, true);
        if (utf8.HasBom) return new EditorText(utf8.Text, true, 0, false);   // damaged UTF-8, not a legacy file

        int legacy = GuessLegacyCodePage(bytes);
        try
        {
            var enc = StrictEncoding(legacy);
            string text = enc.GetString(bytes);
            if (enc.GetBytes(text).AsSpan().SequenceEqual(bytes)) return new EditorText(text, false, legacy, true);
        }
        catch (DecoderFallbackException) { }
        catch (EncoderFallbackException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        return new EditorText(utf8.Text, false, 0, false);
    }

    /// <summary>The bytes the editor writes for <paramref name="text"/>. Throws <see cref="EncoderFallbackException"/>
    /// when a legacy code page cannot hold a character the user typed (an emoji in a TIS-620 file).</summary>
    public static byte[] EncodeForEditor(string text, int codePage, bool utf8Bom)
    {
        switch (codePage)
        {
            case 0:
                byte[] body = Encoding.UTF8.GetBytes(text);
                return utf8Bom ? Encoding.UTF8.GetPreamble().Concat(body).ToArray() : body;
            case Utf16Le:
                return new byte[] { 0xFF, 0xFE }.Concat(StrictEncoding(codePage).GetBytes(text)).ToArray();
            case Utf16Be:
                return new byte[] { 0xFE, 0xFF }.Concat(StrictEncoding(codePage).GetBytes(text)).ToArray();
            default:
                return StrictEncoding(codePage).GetBytes(text);
        }
    }

    /// <summary>A short name for the status bar.</summary>
    public static string EncodingLabel(int codePage, bool utf8Bom) => codePage switch
    {
        0 => utf8Bom ? "UTF-8 BOM" : "UTF-8",
        Utf16Le => "UTF-16 LE",
        Utf16Be => "UTF-16 BE",
        Thai => "TIS-620 (Windows-874)",
        _ => SafeWebName(codePage),
    };

    private static string SafeWebName(int codePage)
    {
        try { return Encoding.GetEncoding(codePage).WebName; }
        catch { return "CP" + codePage; }
    }

    private static Encoding StrictEncoding(int codePage) =>
        Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>
    /// Thai when the high bytes read like TIS-620 — consonants (A1–CE) followed by a following vowel or a mark
    /// written above / below them (D1–DA, E7–EE), which Western text in Windows-1252 essentially never has — else
    /// this PC's ANSI code page, else Windows-1252. Text that does not read like Thai is not called Thai even on a
    /// Thai Windows (ANSI 874): a Western file there would otherwise open as nonsense Thai.
    /// </summary>
    internal static int GuessLegacyCodePage(byte[] bytes)
    {
        int high = 0, thaiPairs = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b < 0x80) continue;
            high++;
            if (b >= 0xA1 && b <= 0xCE && i + 1 < bytes.Length)
            {
                byte n = bytes[i + 1];
                if ((n >= 0xD1 && n <= 0xDA) || (n >= 0xE7 && n <= 0xEE)) thaiPairs++;
            }
        }
        if (high > 0 && thaiPairs * 8 >= high) return Thai;
        int acp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return acp is > 0 and not 65001 and not 1200 and not 1201 and not Thai ? acp : 1252;
    }

    /// <summary>Write UTF-8, with a BOM only when <paramref name="withBom"/> is true.</summary>
    public static void Write(string fullPath, string text, bool withBom)
        => File.WriteAllText(fullPath, text, new UTF8Encoding(withBom));

    /// <summary>Whether an existing file starts with a UTF-8 BOM (false when missing or unreadable), so a
    /// whole-file overwrite can keep whatever the file already had.</summary>
    public static bool HasBom(string fullPath)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            Span<byte> head = stackalloc byte[3];
            return fs.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch { return false; }
    }

    /// <summary>The message every write path uses when it refuses a non-UTF-8 file.</summary>
    public static string NotUtf8Message(string relativePath) =>
        $"'{relativePath}' is not valid UTF-8 (it looks like a legacy encoding such as Windows-874/TIS-620 " +
        "or Windows-1252). Rewriting it as UTF-8 would corrupt every non-ASCII character, so it was left " +
        "untouched. Ask the user to convert it to UTF-8 first, or edit it with a command that preserves its encoding.";

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".obj", ".o", ".a", ".lib", ".class", ".jar", ".war",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".psd",
        ".zip", ".gz", ".7z", ".rar", ".tar", ".xz", ".bz2", ".nupkg", ".snupkg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".webm", ".flac", ".ogg",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".gguf", ".safetensors", ".onnx", ".pt", ".pth", ".db", ".sqlite", ".sqlite3", ".mdb",
    };

    /// <summary>A known binary extension, or a NUL byte in the first 8 KB.</summary>
    public static bool LooksBinary(string fullPath)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath))) return true;
        try
        {
            using var fs = File.OpenRead(fullPath);
            var buf = new byte[(int)Math.Min(8192, fs.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }
}
