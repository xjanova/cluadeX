using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Which model architectures the llama.cpp that CluadeX runs (llama-backend\llama.dll, or the custom backend folder
/// from Settings) can load. llama.cpp keeps every architecture name ("qwen35", "gemma4", …) as a NUL-terminated string
/// in llama.dll, so the answer comes straight from the binary — no process started, no model loaded. A catalog model
/// whose architecture is missing gets "needs a newer llama.cpp" instead of a Download button that ends in a load error.
/// </summary>
public static class LlamaBackendInfo
{
    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static DateTime _cachedStamp;
    private static byte[]? _cachedBytes;

    /// <summary>llama.dll of the backend that would run a model, or null when there is none.</summary>
    public static string? FindLlamaDll(string? customBackendDir)
    {
        if (!string.IsNullOrEmpty(customBackendDir))
        {
            string custom = Path.Combine(customBackendDir, "llama.dll");
            if (File.Exists(custom)) return custom;
        }
        string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "llama-backend", "llama.dll");
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>True / false when the backend was found and read, null when it could not be checked.</summary>
    public static bool? SupportsArchitecture(string? architecture, string? customBackendDir)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return null;
        string? dll = FindLlamaDll(customBackendDir);
        return dll == null ? null : SupportsArchitectureIn(dll, architecture);
    }

    /// <summary>Look for "\0&lt;arch&gt;\0" in the given llama.dll (read once, re-read when the file changes).</summary>
    public static bool? SupportsArchitectureIn(string llamaDllPath, string architecture)
    {
        byte[]? bytes;
        try
        {
            lock (Gate)
            {
                var stamp = File.GetLastWriteTimeUtc(llamaDllPath);
                if (_cachedBytes == null || _cachedPath != llamaDllPath || _cachedStamp != stamp)
                {
                    _cachedBytes = File.ReadAllBytes(llamaDllPath);
                    _cachedPath = llamaDllPath;
                    _cachedStamp = stamp;
                }
                bytes = _cachedBytes;
            }
        }
        catch { return null; }

        // "<arch>\0", inside a C string made only of identifier characters. Not "\0<arch>\0": the linker merges a
        // literal into the tail of a longer one, so "qwen2" lives at the end of "rwkv6qwen2" in the real llama.dll.
        byte[] needle = Encoding.ASCII.GetBytes(architecture.Trim() + "\0");
        var span = bytes.AsSpan();
        int offset = 0;
        while (offset < span.Length)
        {
            int hit = span[offset..].IndexOf(needle);
            if (hit < 0) return false;
            int at = offset + hit;
            if (IsInsideIdentifierString(span, at)) return true;
            offset = at + 1;
        }
        return false;
    }

    // ─── Vision projectors (mtmd.dll) ───

    private static string? _mtmdPath;
    private static DateTime _mtmdStamp;
    private static byte[]? _mtmdBytes;

    /// <summary>
    /// Can the backend's mtmd.dll read a projector of this type (GGUF "clip.projector_type", e.g. "qwen3vl_merger",
    /// "pixtral", "gemma4v")? The names are not all plain strings there — the compiler builds short ones from 8-byte
    /// immediates ("mov rax, 'qwen3vl_'") — so any of these counts: "&lt;type&gt;\0", the source file
    /// "models/&lt;type&gt;.cpp", or the immediate of its first 8 characters. Null when the dll is missing or none of the
    /// reference types can be found that way either (an unfamiliar build: cannot tell, the load fallback decides).
    /// </summary>
    public static bool? SupportsProjectorType(string? projectorType, string? customBackendDir)
    {
        if (string.IsNullOrWhiteSpace(projectorType)) return null;
        string? llama = FindLlamaDll(customBackendDir);
        string? mtmd = llama == null ? null : Path.Combine(Path.GetDirectoryName(llama)!, "mtmd.dll");
        if (mtmd == null || !File.Exists(mtmd)) return null;
        byte[] bytes;
        try
        {
            lock (Gate)
            {
                var stamp = File.GetLastWriteTimeUtc(mtmd);
                if (_mtmdBytes == null || _mtmdPath != mtmd || _mtmdStamp != stamp)
                {
                    _mtmdBytes = File.ReadAllBytes(mtmd);
                    _mtmdPath = mtmd;
                    _mtmdStamp = stamp;
                }
                bytes = _mtmdBytes;
            }
        }
        catch { return null; }
        if (FoundProjector(bytes, projectorType.Trim())) return true;
        // Calibration: if not even the long-standing types are visible, this build's layout is unknown.
        bool layoutKnown = FoundProjector(bytes, "pixtral") || FoundProjector(bytes, "qwen2vl_merger") || FoundProjector(bytes, "idefics3");
        return layoutKnown ? false : null;
    }

    public static bool FoundProjector(byte[] bytes, string type)
    {
        var span = bytes.AsSpan();
        if (span.IndexOf(Encoding.ASCII.GetBytes(type + "\0")) >= 0) return true;
        if (span.IndexOf(Encoding.ASCII.GetBytes("/" + type + ".cpp")) >= 0) return true;
        if (span.IndexOf(Encoding.ASCII.GetBytes("\\" + type + ".cpp")) >= 0) return true;
        if (type.Length >= 8)
        {
            var imm = new byte[10];
            imm[0] = 0x48; imm[1] = 0xB8;   // mov rax, imm64
            Encoding.ASCII.GetBytes(type, 0, 8, imm, 2);
            if (span.IndexOf(imm) >= 0) return true;
        }
        return false;
    }

    /// <summary>Everything from the previous NUL up to <paramref name="at"/> is [a-z0-9._-] (at most 64 bytes back).</summary>
    private static bool IsInsideIdentifierString(ReadOnlySpan<byte> span, int at)
    {
        for (int i = at - 1, n = 0; i >= 0 && n < 64; i--, n++)
        {
            byte b = span[i];
            if (b == 0) return true;
            bool ident = b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-' or (byte)'.';
            if (!ident) return false;
        }
        return at == 0;
    }
}
