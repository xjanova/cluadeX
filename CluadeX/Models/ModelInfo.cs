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

    /// <summary>Whether this model is already downloaded locally.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Local path if installed.</summary>
    public string? InstalledPath { get; set; }
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
