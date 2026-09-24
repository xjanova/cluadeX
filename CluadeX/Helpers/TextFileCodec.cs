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
