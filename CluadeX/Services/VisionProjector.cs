using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// The multimodal projector ("mmproj") that lets a local GGUF model see images. llama-server only reads images when it
/// is started with <c>--mmproj &lt;file&gt;</c>; without it every image is dropped and the model is told it is blind.
///
/// Pairing a projector with a model file, most specific first:
///   1. the name CluadeX gives a downloaded projector: "&lt;model stem&gt;.mmproj-F16.gguf" next to the model;
///   2. any "*mmproj*.gguf" in the folder whose name contains the model stem ("mmproj-MiMo-V2.6-…-f16.gguf");
///   3. LM Studio / per-model folder layout: the folder holds exactly ONE model (split shards count once) and a
///      projector with a generic name ("mmproj-F16.gguf").
/// A generic "mmproj-F16.gguf" in a shared folder with several models is NOT paired — it could belong to any of them,
/// and a wrong projector makes llama-server refuse to start.
/// </summary>
public static class VisionProjector
{
    private static readonly Regex QuantSuffix = new(
        @"[-_.](?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32|MXFP4[A-Z0-9_]*)(?:-\d{5}-of-\d{5})?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShardSuffix = new(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Qwen3.5-9B" out of "Qwen3.5-9B-Q4_K_M.gguf" / "Qwen3.8-27B-UD-Q4_K_M.gguf" / "x.Q4_K_M.gguf".</summary>
    public static string BaseStem(string modelFileName)
    {
        string name = Path.GetFileNameWithoutExtension(Path.GetFileName(modelFileName ?? ""));
        name = ShardSuffix.Replace(name, "");
        string stripped = QuantSuffix.Replace(name, "");
        return stripped.Length > 0 ? stripped : name;
    }

    public static bool IsProjectorFile(string path) =>
        Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// File name to save a catalog projector under, next to its model. Repos name it generically ("mmproj-F16.gguf"),
    /// so several models in one folder would overwrite each other; the model stem in front keeps them apart.
    /// </summary>
    public static string LocalFileName(string modelFileName, string repoProjectorFileName)
    {
        string stem = BaseStem(modelFileName);
        string leaf = Path.GetFileName(repoProjectorFileName);
        if (leaf.Contains(stem, StringComparison.OrdinalIgnoreCase)) return leaf;   // already specific (bartowski)
        return $"{stem}.{leaf}";
    }

    /// <summary>The projector for a model file on disk, or null (rules in the class summary).</summary>
    public static string? FindFor(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(modelPath)); }
        catch { return null; }
        if (dir == null || !Directory.Exists(dir)) return null;

        string stem = BaseStem(modelPath);
        string[] all;
        try { all = Directory.GetFiles(dir, "*.gguf"); }
        catch { return null; }
        var projectors = all.Where(IsProjectorFile).OrderBy(Preference).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (projectors.Count == 0) return null;

        var own = projectors.FirstOrDefault(p => Path.GetFileName(p).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase));
        if (own != null) return own;

        var named = projectors.FirstOrDefault(p => Path.GetFileName(p).Contains(stem, StringComparison.OrdinalIgnoreCase));
        if (named != null) return named;

        // One model in the folder (shards of one model count once) → a generic projector there is its own.
        int models = all.Where(p => !IsProjectorFile(p))
                        .Select(p => ShardSuffix.Replace(Path.GetFileNameWithoutExtension(p), ""))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (models != 1) return null;
        return projectors.FirstOrDefault(p => !LooksLikeAnotherModelsProjector(p, stem));
    }

    /// <summary>F16 first (what the catalog downloads), then BF16, F32, quantized ones.</summary>
    private static int Preference(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        if (n.EndsWith("F16") && !n.EndsWith("BF16")) return 0;
        if (n.EndsWith("BF16")) return 1;
        if (n.EndsWith("F32")) return 2;
        return 3;
    }

    /// <summary>"OtherModel.mmproj-F16.gguf" — CluadeX's own naming for a different model's projector.</summary>
    private static bool LooksLikeAnotherModelsProjector(string path, string stem)
    {
        string leaf = Path.GetFileName(path);
        int dot = leaf.IndexOf(".mmproj", StringComparison.OrdinalIgnoreCase);
        return dot > 0 && !leaf[..dot].Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Put the projector on the GPU too? Only when every layer goes there anyway AND model + projector + KV cache + a
    /// 1 GB margin fit the VRAM free right now. Otherwise it stays on the CPU (<c>--no-mmproj-offload</c>): image
    /// encoding gets slower, text generation does not, and an 8 GB card does not run out of memory at load.
    /// </summary>
    public static bool ShouldOffload(long modelBytes, long projectorBytes, int freeVramMB, int contextTokens, bool allLayersOnGpu)
    {
        if (!allLayersOnGpu || freeVramMB <= 0) return false;
        var guess = new RecommendedModel { TotalParamsB = Math.Max(0.5, modelBytes / 1e9 / 0.6) };
        int kv = ModelAdvisor.KvCacheMB(guess, contextTokens);
        long needMB = (modelBytes + projectorBytes) / (1024 * 1024) + kv + 1024;
        return needMB <= freeVramMB;
    }

    // ─── Projectors that failed to start with the current backend: not tried again this session ───

    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string projectorPath, string? serverExe)
    {
        long size = 0;
        try { size = new FileInfo(projectorPath).Length; } catch { }
        string stamp = "";
        try { if (serverExe != null) stamp = File.GetLastWriteTimeUtc(serverExe).Ticks.ToString(); } catch { }
        return $"{Path.GetFullPath(projectorPath)}|{size}|{serverExe}|{stamp}";
    }

    public static void MarkFailed(string projectorPath, string? serverExe) { lock (Failed) Failed.Add(Key(projectorPath, serverExe)); }
    public static bool HasFailed(string projectorPath, string? serverExe) { lock (Failed) return Failed.Contains(Key(projectorPath, serverExe)); }
    /// <summary>Forget earlier failures (the harness; a new backend build changes the key by itself).</summary>
    public static void ResetFailures() { lock (Failed) Failed.Clear(); }
}
