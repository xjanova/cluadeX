using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// What a GGUF says about where its bytes would sit on the GPU: the routed-expert weights of every layer (what
/// llama-server's <c>--n-cpu-moe N</c> keeps in system RAM for the first N layers), everything else, and the KV cache
/// one token of context costs. Read from the file's header and tensor table only — no weights are read, no model is
/// loaded. Sizes come from the tensor offsets (the gap to the next tensor), so no quant-type table is needed and a new
/// quant type still measures right.
/// </summary>
public sealed record GgufMoeFacts(
    string Architecture,
    int BlockCount,
    int ExpertCount,
    /// <summary>Routed-expert bytes per layer, index = layer. Empty for a dense model.</summary>
    long[] ExpertBytesPerLayer,
    /// <summary>Bytes that go to the GPU with <c>-ngl all</c> besides the routed experts (attention, shared experts,
    /// norms, the output head). The input embedding stays in RAM in llama.cpp, so it is left out — unless the
    /// model has no separate output head and reuses it there.</summary>
    long NonExpertGpuBytes,
    /// <summary>K and V elements one token of context stores, all layers added up.</summary>
    long KElemsPerToken,
    long VElemsPerToken)
{
    public bool IsMoE => ExpertCount > 1 && ExpertBytesPerLayer.Any(b => b > 0);
    public long ExpertBytesTotal => ExpertBytesPerLayer.Sum();
}

public static class GgufMoeReader
{
    private enum VType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12,
    }

    /// <summary>The tensors llama.cpp's --n-cpu-moe moves: blk.N.ffn_{up,down,gate,gate_up}_(ch)exps.</summary>
    private static readonly Regex ExpertTensor = new(@"^blk\.(\d+)\.ffn_(up|down|gate|gate_up)_(ch)?exps", RegexOptions.CultureInvariant);

    /// <summary>The facts, or null when the file is not a readable GGUF (callers then launch as before).
    /// A split model (<c>-00001-of-00003.gguf</c>) is read across all its parts; a missing part gives null.</summary>
    public static GgufMoeFacts? TryRead(string path)
    {
        try
        {
            var first = ReadFile(path);
            if (first == null) return null;
            var tensors = new List<(string Name, long Bytes)>(first.Tensors);
            if (first.SplitCount > 1)
            {
                var parts = SplitParts(path, first.SplitCount);
                if (parts == null) return null;
                foreach (var part in parts.Skip(1))
                {
                    var f = ReadFile(part);
                    if (f == null) return null;
                    tensors.AddRange(f.Tensors);
                }
            }
            return Summarise(first, tensors);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The paths of a split model's parts, in order, or null when one is missing / the name is not a split name.</summary>
    public static List<string>? SplitParts(string firstPath, int count)
    {
        var m = Regex.Match(Path.GetFileName(firstPath), @"^(.*)-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string dir = Path.GetDirectoryName(firstPath) ?? "";
        var list = new List<string>();
        for (int i = 1; i <= count; i++)
        {
            string p = Path.Combine(dir, $"{m.Groups[1].Value}-{i:00000}-of-{count:00000}.gguf");
            if (!File.Exists(p)) return null;
            list.Add(p);
        }
        return list;
    }

    private sealed class FileFacts
    {
        public string Arch = "";
        public int SplitCount = 1;
        public readonly Dictionary<string, object> Values = new(StringComparer.Ordinal);
        public readonly List<(string Name, long Bytes)> Tensors = new();
    }

    private static FileFacts? ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x46554747u) return null;   // "GGUF"
        if (br.ReadUInt32() < 2) return null;
        ulong tensorCount = br.ReadUInt64();
        ulong kvCount = br.ReadUInt64();
        if (tensorCount > 1_000_000 || kvCount > 100_000) return null;

        var f = new FileFacts();
        long alignment = 32;
        for (ulong i = 0; i < kvCount; i++)
        {
            string key = ReadString(br);
            var type = (VType)br.ReadUInt32();
            object? v = ReadValue(br, type, keep: Wanted(key));
            if (key == "general.architecture" && v is string a) f.Arch = a;
            else if (key == "general.alignment" && v is long al && al is > 0 and <= 1 << 20) alignment = al;
            else if (key == "split.count" && v is long sc && sc is > 0 and < 10_000) f.SplitCount = (int)sc;
            if (v != null) f.Values[key] = v;
        }

        var infos = new List<(string Name, ulong Offset)>((int)tensorCount);
        for (ulong i = 0; i < tensorCount; i++)
        {
            string name = ReadString(br);
            uint dims = br.ReadUInt32();
            if (dims > 8) return null;
            for (uint d = 0; d < dims; d++) br.ReadUInt64();
            br.ReadUInt32();                                   // ggml type
            infos.Add((name, br.ReadUInt64()));
        }
        long dataStart = (fs.Position + alignment - 1) / alignment * alignment;
        long dataBytes = fs.Length - dataStart;
        if (dataBytes < 0) return null;

        var byOffset = infos.OrderBy(t => t.Offset).ToList();
        for (int i = 0; i < byOffset.Count; i++)
        {
            ulong start = byOffset[i].Offset;
            ulong end = i + 1 < byOffset.Count ? byOffset[i + 1].Offset : (ulong)dataBytes;
            if (end < start || end > (ulong)dataBytes) return null;
            f.Tensors.Add((byOffset[i].Name, (long)(end - start)));
        }
        return f;
    }

    /// <summary>Only the values the summary needs are kept; everything else (the tokenizer's 150k strings) is skipped.</summary>
    private static bool Wanted(string key) =>
        key is "general.architecture" or "general.alignment" or "split.count"
        || key.EndsWith(".block_count", StringComparison.Ordinal)
        || key.EndsWith(".expert_count", StringComparison.Ordinal)
        || key.EndsWith(".embedding_length", StringComparison.Ordinal)
        || key.EndsWith(".rope.dimension_count", StringComparison.Ordinal)
        || key.Contains(".attention.", StringComparison.Ordinal);

    private static GgufMoeFacts Summarise(FileFacts f, List<(string Name, long Bytes)> tensors)
    {
        string a = f.Arch;
        long Int(string key) => f.Values.TryGetValue($"{a}.{key}", out var v) && v is long n ? n : 0;

        int blocks = (int)Math.Clamp(Int("block_count"), 0, 100_000);
        int experts = (int)Math.Clamp(Int("expert_count"), 0, 1_000_000);

        var perLayer = new long[blocks];
        long nonExpert = 0, embd = 0;
        bool hasOutput = false;
        foreach (var (name, bytes) in tensors)
        {
            var m = ExpertTensor.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int layer) && layer >= 0 && layer < blocks)
            {
                perLayer[layer] += bytes;
                continue;
            }
            if (name.StartsWith("token_embd.", StringComparison.Ordinal)) { embd += bytes; continue; }
            if (name.StartsWith("output.", StringComparison.Ordinal)) hasOutput = true;
            nonExpert += bytes;
        }
        if (!hasOutput) nonExpert += embd;                    // tied head: the embedding is the output layer, on the GPU
        if (!perLayer.Any(b => b > 0)) perLayer = Array.Empty<long>();

        // KV per token. head_count_kv may be one number or one per layer (hybrid models: 0 on the recurrent layers).
        long embdLen = Int("embedding_length");
        long heads = Int("attention.head_count");
        long headDim = heads > 0 ? embdLen / heads : 0;
        long kLen = Int("attention.key_length") is > 0 and var kl ? kl : headDim;
        long vLen = Int("attention.value_length") is > 0 and var vl ? vl : headDim;
        long kvHeadsAll = f.Values.TryGetValue($"{a}.attention.head_count_kv", out var hkv) switch
        {
            true when hkv is long one => one * blocks,
            true when hkv is long[] each => each.Where(x => x > 0).Sum(),
            _ => heads * blocks,
        };
        long kElems = kvHeadsAll * kLen, vElems = kvHeadsAll * vLen;
        // Multi-head latent attention (DeepSeek, Kimi, GLM-5): the cache holds the compressed latent, not every head.
        long lora = Int("attention.kv_lora_rank");
        if (lora > 0)
        {
            long rope = Int("rope.dimension_count");
            kElems = (lora + rope) * blocks;
            vElems = lora * blocks;
        }
        return new GgufMoeFacts(a, blocks, experts, perLayer, nonExpert, Math.Max(0, kElems), Math.Max(0, vElems));
    }

    private static string ReadString(BinaryReader br)
    {
        ulong len = br.ReadUInt64();
        if (len > 1 << 20) throw new InvalidDataException("GGUF string length absurd");
        return Encoding.UTF8.GetString(br.ReadBytes((int)len));
    }

    /// <summary>Read (keep) or skip one value. Integers come back as long, an integer array as long[], a string as string.</summary>
    private static object? ReadValue(BinaryReader br, VType type, bool keep)
    {
        switch (type)
        {
            case VType.UInt8: return (long)br.ReadByte();
            case VType.Int8: return (long)br.ReadSByte();
            case VType.Bool: br.ReadByte(); return null;
            case VType.UInt16: return (long)br.ReadUInt16();
            case VType.Int16: return (long)br.ReadInt16();
            case VType.UInt32: return (long)br.ReadUInt32();
            case VType.Int32: return (long)br.ReadInt32();
            case VType.Float32: br.ReadSingle(); return null;
            case VType.UInt64: return (long)Math.Min(br.ReadUInt64(), long.MaxValue);
            case VType.Int64: return br.ReadInt64();
            case VType.Float64: br.ReadDouble(); return null;
            case VType.String:
                if (!keep)
                {
                    ulong len = br.ReadUInt64();
                    if (len > 1 << 20) throw new InvalidDataException("GGUF string length absurd");
                    br.BaseStream.Seek((long)len, SeekOrigin.Current);
                    return null;
                }
                return ReadString(br);
            case VType.Array:
            {
                var elem = (VType)br.ReadUInt32();
                ulong count = br.ReadUInt64();
                if (count > 10_000_000) throw new InvalidDataException("GGUF array length absurd");
                int fixedSize = elem switch
                {
                    VType.UInt8 or VType.Int8 or VType.Bool => 1,
                    VType.UInt16 or VType.Int16 => 2,
                    VType.UInt32 or VType.Int32 or VType.Float32 => 4,
                    VType.UInt64 or VType.Int64 or VType.Float64 => 8,
                    _ => 0,
                };
                bool intElem = elem is VType.UInt8 or VType.Int8 or VType.UInt16 or VType.Int16 or VType.UInt32 or VType.Int32 or VType.UInt64 or VType.Int64;
                if (keep && intElem && count <= 100_000)
                {
                    var arr = new long[count];
                    for (ulong i = 0; i < count; i++) arr[i] = (long)ReadValue(br, elem, true)!;
                    return arr;
                }
                if (fixedSize > 0) { br.BaseStream.Seek((long)count * fixedSize, SeekOrigin.Current); return null; }
                for (ulong i = 0; i < count; i++) ReadValue(br, elem, false);
                return null;
            }
            default:
                throw new InvalidDataException($"GGUF value type {(uint)type} unknown");
        }
    }
}
