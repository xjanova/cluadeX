using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Lightweight GGUF v3 metadata reader. Used to extract <c>general.architecture</c>
/// before handing a file to LLamaSharp, so we can detect models the bundled
/// llama.cpp doesn't recognize (e.g. Gemma 3/4, Llama 4) and route them to the
/// llama-server.exe fallback instead of crashing with a generic LoadWeightsFailedException.
/// </summary>
/// <remarks>
/// Spec: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md
///   magic            uint32 ("GGUF")
///   version          uint32 (we expect >= 2)
///   tensor_count     uint64
///   metadata_kv_count uint64
///   [metadata_kv]*   key (string) + value_type (uint32) + value
///
/// We only walk the metadata until we find "general.architecture", then bail out.
/// This keeps the read I/O bounded — typically a few KB at the head of the file.
/// </remarks>
public static class GgufMetadataReader
{
    private enum ValueType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3,
        UInt32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12,
    }

    /// <summary>Architectures known to require a newer llama.cpp than what LLamaSharp bundles.</summary>
    /// <remarks>
    /// Keep this list conservative — adding an arch here forces the llama-server fallback.
    /// Gemma 3 was added to llama.cpp in March 2025; Gemma 4 in late 2025 / early 2026.
    /// LLamaSharp pins to an older snapshot, so anything past Gemma 2 is unsafe.
    /// </remarks>
    private static readonly HashSet<string> RequiresFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemma3", "gemma3n",
        "gemma4", "gemma4n",
        "llama4",
        "qwen3", // Qwen 3 (Sep 2025) also post-dates the bundled snapshot
        "deepseek-v3", "deepseek-r1",
        "phi4",
    };

    /// <summary>
    /// Read <c>general.architecture</c> from a GGUF file. Returns null on any error
    /// (file is non-GGUF, truncated, etc.) — callers should treat null as "unknown".
    /// </summary>
    public static string? TryReadArchitecture(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            // Magic: must be "GGUF"
            uint magic = br.ReadUInt32();
            if (magic != 0x46554747u) return null; // "GGUF" little-endian

            uint version = br.ReadUInt32();
            if (version < 2) return null; // v1 had a different layout we don't bother with

            // tensor_count + metadata_kv_count
            ulong _tensorCount = br.ReadUInt64();
            ulong kvCount = br.ReadUInt64();

            // Walk metadata. Bail as soon as we find general.architecture, or after
            // a sensible cap so a corrupt file can't make us read megabytes.
            const int maxKvToScan = 256;
            int scanned = 0;

            while (kvCount-- > 0 && scanned++ < maxKvToScan)
            {
                string key = ReadGgufString(br);
                var type = (ValueType)br.ReadUInt32();

                if (key == "general.architecture")
                {
                    if (type != ValueType.String) return null;
                    return ReadGgufString(br);
                }

                // Skip the value to advance to the next key
                if (!SkipValue(br, type)) return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True if the architecture is known to be unsupported by the bundled LLamaSharp build.</summary>
    public static bool RequiresLlamaServerFallback(string? architecture)
        => !string.IsNullOrEmpty(architecture) && RequiresFallback.Contains(architecture);

    /// <summary>Convenience: open the file, read the arch, decide if fallback is needed.</summary>
    public static (bool NeedsFallback, string? Architecture) InspectModel(string path)
    {
        var arch = TryReadArchitecture(path);
        return (RequiresLlamaServerFallback(arch), arch);
    }

    private static string ReadGgufString(BinaryReader br)
    {
        ulong len = br.ReadUInt64();
        // GGUF strings are bounded but we still cap defensively to avoid OOM on a
        // corrupt header. 64 KB is comfortably larger than any real metadata field.
        if (len > 65536) throw new InvalidDataException("GGUF string length absurd");
        var bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Advance past a value of the given type. Returns false on a type we can't decode.</summary>
    private static bool SkipValue(BinaryReader br, ValueType type)
    {
        switch (type)
        {
            case ValueType.UInt8: case ValueType.Int8: case ValueType.Bool:
                br.ReadByte(); return true;
            case ValueType.UInt16: case ValueType.Int16:
                br.ReadUInt16(); return true;
            case ValueType.UInt32: case ValueType.Int32: case ValueType.Float32:
                br.ReadUInt32(); return true;
            case ValueType.UInt64: case ValueType.Int64: case ValueType.Float64:
                br.ReadUInt64(); return true;
            case ValueType.String:
                ReadGgufString(br); return true;
            case ValueType.Array:
                var elemType = (ValueType)br.ReadUInt32();
                ulong count = br.ReadUInt64();
                if (count > 1_000_000) return false; // sanity cap
                for (ulong i = 0; i < count; i++)
                    if (!SkipValue(br, elemType)) return false;
                return true;
            default:
                return false;
        }
    }
}
