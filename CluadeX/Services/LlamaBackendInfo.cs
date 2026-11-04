using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Which model architectures the llama.cpp builds CluadeX can run a model with are able to load. There are two:
///   • the in-process one — LLamaSharp's runtimes\win-x64\native\&lt;variant&gt;\llama.dll, always shipped;
///   • the llama-server one — llama-backend\llama.dll (or the custom backend folder from Settings), often absent
///     from an installed copy.
/// llama.cpp keeps every architecture name ("qwen35", "gemma4", …) as a NUL-terminated string in llama.dll, so the
/// answer comes straight from the binary — no process started, no model loaded. A catalog model whose architecture
/// neither build knows gets "needs a newer llama.cpp" instead of a Download button that ends in a load error.
/// </summary>
public static class LlamaBackendInfo
{
    private static readonly object Gate = new();
    // Two dlls are asked in turn for every catalog model, so one slot per path (each llama.dll is ~2 MB).
    private static readonly Dictionary<string, (DateTime Stamp, long Length, byte[] Bytes)> DllCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// LLamaSharp's CPU/GPU variants of one release. All are built from the same llama.cpp commit, so their
    /// architecture tables are identical and any one of them answers for the variant LLamaSharp picks at run time.
    /// </summary>
    private static readonly string[] InProcessVariants = { "avx2", "avx", "avx512", "noavx", "cuda12", "cuda11", "vulkan" };

    /// <summary>
    /// llama.dll of the llama-server backend (custom folder from Settings, else llama-backend\ next to the app), or
    /// null when there is none. This is the build that reads vision projectors.
    /// </summary>
    public static string? FindServerLlamaDll(string? customBackendDir, string? appDir = null)
    {
        if (!string.IsNullOrEmpty(customBackendDir))
        {
            string custom = Path.Combine(customBackendDir, "llama.dll");
            if (File.Exists(custom)) return custom;
        }
        string bundled = Path.Combine(appDir ?? AppDomain.CurrentDomain.BaseDirectory, "llama-backend", "llama.dll");
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>
    /// llama.dll the in-process LLamaSharp backend loads (runtimes\win-x64\native\&lt;variant&gt;\, or a flat llama.dll
    /// next to the app), or null when none ships with this copy.
    /// </summary>
    public static string? FindInProcessLlamaDll(string? appDir = null)
    {
        string root = appDir ?? AppDomain.CurrentDomain.BaseDirectory;
        string native = Path.Combine(root, "runtimes", "win-x64", "native");
        foreach (var variant in InProcessVariants)
        {
            string p = Path.Combine(native, variant, "llama.dll");
            if (File.Exists(p)) return p;
        }
        string flat = Path.Combine(native, "llama.dll");
        if (File.Exists(flat)) return flat;
        flat = Path.Combine(root, "llama.dll");
        return File.Exists(flat) ? flat : null;
    }

    /// <summary>llama.dll of the backend that would run a model: the llama-server one, else the in-process one.</summary>
    public static string? FindLlamaDll(string? customBackendDir)
        => FindServerLlamaDll(customBackendDir) ?? FindInProcessLlamaDll();

    /// <summary>
    /// Can this copy of CluadeX load the architecture with either of its llama.cpp builds? The llama-server build
    /// only counts when its llama-server.exe sits next to it. True when one of them knows it, false when every build
    /// that could be read lacks it, null when neither could be checked.
    /// </summary>
    public static bool? SupportsArchitecture(string? architecture, string? customBackendDir, string? appDir = null)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return null;
        string? serverDll = FindServerLlamaDll(customBackendDir, appDir);
        bool? server = serverDll != null && File.Exists(Path.Combine(Path.GetDirectoryName(serverDll)!, "llama-server.exe"))
            ? SupportsArchitectureIn(serverDll, architecture)
            : null;
        bool? inProcess = SupportsArchitectureInProcess(architecture, appDir);
        if (server == true || inProcess == true) return true;
        if (server == false || inProcess == false) return false;
        return null;
    }

    /// <summary>Does the in-process (LLamaSharp) llama.dll know the architecture? Null when it could not be read.</summary>
    public static bool? SupportsArchitectureInProcess(string? architecture, string? appDir = null)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return null;
        string? dll = FindInProcessLlamaDll(appDir);
        return dll == null ? null : SupportsArchitectureIn(dll, architecture);
    }

    /// <summary>Forget every dll read so far (the next question reads the files again).</summary>
    public static void ClearCache()
    {
        lock (Gate) { DllCache.Clear(); _mtmdBytes = null; _mtmdPath = null; }
    }

    /// <summary>Look for "&lt;arch&gt;\0" in the given llama.dll (read once, re-read when the file changes).</summary>
    public static bool? SupportsArchitectureIn(string llamaDllPath, string architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return null;
        byte[]? bytes;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(llamaDllPath);
                if (!info.Exists) return null;
                var stamp = info.LastWriteTimeUtc;
                long length = info.Length;
                if (!DllCache.TryGetValue(llamaDllPath, out var hit) || hit.Stamp != stamp || hit.Length != length)
                {
                    hit = (stamp, length, File.ReadAllBytes(llamaDllPath));
                    DllCache[llamaDllPath] = hit;
                }
                bytes = hit.Bytes;
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
        // Only llama-server reads images (--mmproj); the in-process backend's mtmd.dll never gets a projector.
        string? llama = FindServerLlamaDll(customBackendDir);
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
