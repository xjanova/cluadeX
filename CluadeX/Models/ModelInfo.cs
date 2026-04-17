namespace CluadeX.Models;

public class ModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileSizeDisplay => FormatFileSize(FileSize);
    public string? LocalPath { get; set; }
    public bool IsDownloaded { get; set; }
    public string QuantizationType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ParameterSize { get; set; }
    public int RecommendedVramMB { get; set; }
    public double DownloadProgress { get; set; }
    public bool IsDownloading { get; set; }
    public string Category { get; set; } = "General";

    // Rich details for search results
    public string Author { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime LastModified { get; set; }

    /// <summary>Display-friendly downloads count (e.g. "1.2M", "345K")</summary>
    public string DownloadsDisplay => FormatCount(Downloads);

    /// <summary>Display-friendly likes count</summary>
    public string LikesDisplay => FormatCount(Likes);

    /// <summary>Estimated parameter size from filename (e.g. "7B", "14B")</summary>
    public string ParameterDisplay => GuessParameterSize(FileName);

    /// <summary>Direct link to the repo page on HuggingFace. Used by the "Read more" chip
    /// on each search-result card so users can open model cards for license, readme, etc.</summary>
    public string HuggingFaceUrl => string.IsNullOrEmpty(RepoId) ? "" : $"https://huggingface.co/{RepoId}";

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "Unknown";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {suffixes[order]}";
    }

    private static string FormatCount(int count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:0.#}M";
        if (count >= 1_000) return $"{count / 1_000.0:0.#}K";
        return count.ToString();
    }

    /// <summary>Try to extract parameter size from filename like "7B", "14B", "70b".</summary>
    public static string GuessParameterSize(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return "";
        // Match patterns like -7B-, _14B_, .3b- (parameter counts, not version numbers)
        var match = System.Text.RegularExpressions.Regex.Match(
            filename, @"[-_.](\d{1,3}\.?\d*)[Bb][-_.\s]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // Fallback: end of name before extension
            match = System.Text.RegularExpressions.Regex.Match(
                filename, @"[-_.](\d{1,3}\.?\d*)[Bb]$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return match.Success ? $"{match.Groups[1].Value}B" : "";
    }
}

public class HuggingFaceModelResult
{
    public string ModelId { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public DateTime LastModified { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<HuggingFaceSibling> Siblings { get; set; } = new();
}

public class HuggingFaceSibling
{
    public string RFilename { get; set; } = string.Empty;
    public long? Size { get; set; }
}

public class RecommendedModel
{
    public string RepoId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int ParameterBillions { get; set; }
    public int RequiredVramMB { get; set; }
    public long ApproxSizeBytes { get; set; }
    public string Category { get; set; } = "Coding";
    public string Url { get; set; } = string.Empty;

    /// <summary>Star rating 1-5 for model quality. 5=best, 1=basic.</summary>
    public int Stars { get; set; } = 3;

    /// <summary>Star display string (e.g. "⭐⭐⭐⭐⭐").</summary>
    public string StarDisplay => new string('⭐', Math.Clamp(Stars, 1, 5));

    /// <summary>Whether this model is already downloaded locally.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Local path if installed.</summary>
    public string? InstalledPath { get; set; }

    // ─── Fit Indicator (set by ModelManagerViewModel when user GPU is detected) ───
    // These drive a colored badge on each card so users immediately see whether a
    // model will run entirely on GPU (fast), partially offload to CPU (medium), or
    // require heavy CPU offload (slow). Not serialized — recomputed on every refresh.

    /// <summary>Fit tier against the user's available VRAM. Set by the VM, not persisted.</summary>
    public ModelFitTier FitTier { get; set; } = ModelFitTier.Unknown;

    /// <summary>Short human label for the badge, e.g. "Fast" / "Partial" / "Slow".</summary>
    public string FitLabel { get; set; } = "";

    /// <summary>One-line description shown in the tooltip, e.g. "Fits in 8GB VRAM — full GPU".</summary>
    public string FitHint { get; set; } = "";

    /// <summary>Suggested -ngl value for llama.cpp given the user's VRAM. -1 means "all layers on GPU".</summary>
    public int RecommendedGpuLayers { get; set; } = -1;

    /// <summary>Color key used by the XAML DataTrigger for the badge/border accent.</summary>
    public string FitColorKey => FitTier switch
    {
        ModelFitTier.Excellent => "Green",
        ModelFitTier.Good      => "Teal",
        ModelFitTier.Partial   => "Yellow",
        ModelFitTier.Poor      => "Peach",
        ModelFitTier.TooLarge  => "Red",
        _                      => "Overlay0",
    };
}

/// <summary>How well a model fits into the user's available VRAM.</summary>
public enum ModelFitTier
{
    Unknown,
    Excellent, // fits entirely with comfortable headroom — full GPU, fast
    Good,      // fits but tight — full GPU, may need to reduce context size
    Partial,   // most layers on GPU, some on CPU — medium speed
    Poor,      // heavy CPU offload — usable but slow
    TooLarge,  // won't fit even with full CPU offload at this quantization
}

/// <summary>
/// Represents a source where users can find GGUF models.
/// </summary>
public class ModelSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}
