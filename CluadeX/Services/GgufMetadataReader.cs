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
    /// Only consulted when the in-process llama.dll cannot be read (<see cref="LlamaBackendInfo.SupportsArchitectureInProcess"/>
    /// is the real answer — this list went stale the moment Qwen 3.5 shipped). Keep it conservative: an arch here
    /// forces the llama-server fallback.
    /// </remarks>
    private static readonly HashSet<string> RequiresFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemma3", "gemma3n",
        "gemma4", "gemma4n",
        "llama4",
        "qwen3", // Qwen 3 (Sep 2025) also post-dates the bundled snapshot
        "qwen35", "qwen35moe",
        "deepseek-v3", "deepseek-r1",
        "phi4",
    };

    /// <summary>The hardcoded guess, for when no llama.dll can be scanned.</summary>
    public static bool IsOnFallbackList(string? architecture)
        => !string.IsNullOrEmpty(architecture) && RequiresFallback.Contains(architecture);

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

    /// <summary>What the load tuner needs from a GGUF header: the architecture, layers, experts and KV width.</summary>
    public sealed record ModelHeader(string Architecture, int BlockCount, int ExpertCount, int ExpertUsedCount,
        int HeadCountKv, int KeyLength, int ValueLength, int EmbeddingLength, int HeadCount);

    /// <summary>
    /// Read the shape keys (<c>{arch}.block_count</c>, <c>.expert_count</c>, <c>.expert_used_count</c>,
    /// <c>.attention.head_count_kv</c>, <c>.attention.key_length</c>, …). They sit before the tokenizer arrays, so the
    /// walk stops after a few KB. Null when the file is not a GGUF or has no architecture; a missing key reads as 0.
    /// </summary>
    public static ModelHeader? TryReadHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46554747u) return null;
            if (br.ReadUInt32() < 2) return null;
            br.ReadUInt64();
            ulong kvCount = br.ReadUInt64();

            string? arch = null;
            var ints = new Dictionary<string, int>(StringComparer.Ordinal);
            int scanned = 0;
            while (kvCount-- > 0 && scanned++ < 512)
            {
                string key = ReadGgufString(br);
                var type = (ValueType)br.ReadUInt32();
                if (key == "general.architecture" && type == ValueType.String) { arch = ReadGgufString(br); continue; }
                if (key.StartsWith("tokenizer.", StringComparison.Ordinal) && arch != null && ints.Count > 0) break; // the shape keys are behind us
                if (type is ValueType.UInt32 or ValueType.Int32) { ints[key] = (int)br.ReadUInt32(); continue; }
                if (type is ValueType.UInt64 or ValueType.Int64) { ints[key] = (int)Math.Min(int.MaxValue, br.ReadUInt64()); continue; }
                if (type is ValueType.UInt16 or ValueType.Int16) { ints[key] = br.ReadUInt16(); continue; }
                if (!SkipValue(br, type)) break;
            }
            if (string.IsNullOrEmpty(arch)) return null;
            int I(string k) => ints.TryGetValue($"{arch}.{k}", out var v) ? v : 0;
            return new ModelHeader(arch, I("block_count"), I("expert_count"), I("expert_used_count"),
                I("attention.head_count_kv"), I("attention.key_length"), I("attention.value_length"),
                I("embedding_length"), I("attention.head_count"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read <c>{arch}.block_count</c> (the number of transformer blocks) — what a GPU share is scaled from
    /// (Services/Gpu/GpuShare.cs). Returns 0 on any error or when absent. Reads only the metadata head; stops at the key.
    /// </summary>
    public static int TryReadBlockCount(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46554747u) return 0;
            if (br.ReadUInt32() < 2) return 0;
            br.ReadUInt64();
            ulong kvCount = br.ReadUInt64();
            string? arch = null;
            const int maxKvToScan = 512;
            int scanned = 0;
            while (kvCount-- > 0 && scanned++ < maxKvToScan)
            {
                string key = ReadGgufString(br);
                var type = (ValueType)br.ReadUInt32();
                if (key == "general.architecture" && type == ValueType.String) { arch = ReadGgufString(br); continue; }
                bool isBlockCount = key.EndsWith(".block_count", StringComparison.Ordinal)
                    && (arch == null || key == arch + ".block_count");
                if (isBlockCount)
                {
                    long n = ReadInteger(br, type);
                    return n is > 0 and < 100_000 ? (int)n : 0;
                }
                if (!SkipValue(br, type)) return 0;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long ReadInteger(BinaryReader br, ValueType type) => type switch
    {
        ValueType.UInt8 => br.ReadByte(),
        ValueType.Int8 => br.ReadSByte(),
        ValueType.UInt16 => br.ReadUInt16(),
        ValueType.Int16 => br.ReadInt16(),
        ValueType.UInt32 => br.ReadUInt32(),
        ValueType.Int32 => br.ReadInt32(),
        ValueType.UInt64 => (long)Math.Min(br.ReadUInt64(), long.MaxValue),
        ValueType.Int64 => br.ReadInt64(),
        _ => -1,
    };

    /// <summary>
    /// True if the in-process LLamaSharp llama.dll does not know the architecture. Read from the dll itself; the
    /// hardcoded list decides only when that dll is missing or unreadable.
    /// </summary>
    public static bool RequiresLlamaServerFallback(string? architecture)
    {
        if (string.IsNullOrEmpty(architecture)) return false;
        return LlamaBackendInfo.SupportsArchitectureInProcess(architecture) switch
        {
            true => false,
            false => true,
            null => IsOnFallbackList(architecture),
        };
    }

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
