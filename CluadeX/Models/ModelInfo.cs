using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    // ─── How it runs here (set by ModelManagerViewModel through ModelAdvisor; not persisted) ───

    /// <summary>Fit of this file on the detected hardware.</summary>
    public ModelFitTier FitTier { get; set; } = ModelFitTier.Unknown;
    /// <summary>Short badge text, e.g. "Fast" / "Partial".</summary>
    public string FitLabel { get; set; } = "";
    /// <summary>Tooltip / second line explaining the badge.</summary>
    public string FitHint { get; set; } = "";
    /// <summary>"≈ 24 tok/s" — empty when unknown.</summary>
    public string SpeedText { get; set; } = "";
    /// <summary>Catalog name when the file is one of the curated models ("Qwen3.5 9B"), else empty.</summary>
    public string CatalogName { get; set; } = "";
    /// <summary>True for the model the local backend has loaded right now.</summary>
    public bool IsLoaded { get; set; }
    /// <summary>A vision projector sits next to this file (VisionProjector.FindFor): it will see images.</summary>
    public bool HasVision { get; set; }
    public string FitColorKey => RecommendedModel.ColorKeyOf(FitTier);

    /// <summary>Display-friendly downloads count (e.g. "1.2M", "345K")</summary>
    public string DownloadsDisplay => FormatCount(Downloads);

    /// <summary>Display-friendly likes count</summary>
    public string LikesDisplay => FormatCount(Likes);

    /// <summary>Estimated parameter size from filename (e.g. "7B", "14B")</summary>
    public string ParameterDisplay => GuessParameterSize(FileName);

    /// <summary>Direct link to the repo page on HuggingFace. Used by the "Read more" chip
    /// on each search-result card so users can open model cards for license, readme, etc.</summary>
    public string HuggingFaceUrl => string.IsNullOrEmpty(RepoId) ? "" : $"https://huggingface.co/{RepoId}";

    internal static string FormatFileSize(long bytes)
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
        return $"{size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {suffixes[order]}";
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

/// <summary>What a catalog model is good at. A model can have several.</summary>
[Flags]
public enum ModelTasks
{
    None = 0,
    Chat = 1,
    Coding = 2,
    Reasoning = 4,
    Vision = 8,
    Thai = 16,
}

/// <summary>One downloadable file of a catalog model (a quantization level).</summary>
public sealed class QuantOption
{
    public QuantOption(string name, string fileName, long sizeBytes)
    {
        Name = name;
        FileName = fileName;
        SizeBytes = sizeBytes;
    }

    /// <summary>"Q4_K_M", "Q5_K_M", "IQ4_XS", …</summary>
    public string Name { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
}

/// <summary>
/// One quant of a catalog model, evaluated against this PC: memory it needs, where it runs, how fast.
/// Built by ModelManagerViewModel from a <c>ModelAdvisor</c> estimate; the strings are already localized.
/// </summary>
public sealed class QuantPlan
{
    public required QuantOption Quant { get; init; }
    public string Name => Quant.Name;
    public string FileName => Quant.FileName;
    public long SizeBytes => Quant.SizeBytes;
    public string SizeDisplay => ModelInfo.FormatFileSize(SizeBytes);

    public int NeedMB { get; init; }
    public double GpuFraction { get; init; }
    public double TokensPerSecond { get; init; }
    public ModelFitTier FitTier { get; init; }
    public int SuggestedGpuLayers { get; init; } = -1;
    /// <summary>The quant ModelAdvisor would pick for this PC.</summary>
    public bool IsAuto { get; init; }

    /// <summary>Drop-down text: "★ Q5_K_M · 6.1 GB · Fast".</summary>
    public string Label { get; init; } = "";
    public string FitLabel { get; init; } = "";
    public string FitHint { get; init; } = "";
    public string SpeedText { get; init; } = "";
    public string MemoryText { get; init; } = "";
    public string FitColorKey => RecommendedModel.ColorKeyOf(FitTier);

    /// <summary>What a ComboBox shows in its closed box (its template ignores DisplayMemberPath there).</summary>
    public override string ToString() => Label;
}

public class RecommendedModel : INotifyPropertyChanged
{
    /// <summary>Stable key: description lookups ("modelsm.desc.&lt;Id&gt;") and tests.</summary>
    public string Id { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    /// <summary>"yyyy-MM" the weights were published.</summary>
    public string Released { get; set; } = string.Empty;
    /// <summary>GGUF general.architecture ("qwen35", "gemma4", …) — checked against the bundled llama.cpp.</summary>
    public string Architecture { get; set; } = string.Empty;
    /// <summary>Context length the model was trained for (tokens).</summary>
    public int NativeContext { get; set; }
    public double TotalParamsB { get; set; }
    /// <summary>Parameters used per token for a mixture-of-experts model; 0 for a dense model.</summary>
    public double ActiveParamsB { get; set; }
    public bool IsMoE => ActiveParamsB > 0 && ActiveParamsB < TotalParamsB;

    public ModelTasks Tasks { get; set; }
    // 0-5 strength per task (0 = cannot do it). Used to rank recommendations.
    public int CodingScore { get; set; }
    public int ChatScore { get; set; }
    public int ReasoningScore { get; set; }
    public int ThaiScore { get; set; }
    public int VisionScore { get; set; }
    /// <summary>Older generation: hidden unless the user asks for older models.</summary>
    public bool IsLegacy { get; set; }
    /// <summary>Every file the catalog offers, smallest quant first.</summary>
    public List<QuantOption> Quants { get; set; } = new();

    /// <summary>The repo's vision projector (mmproj, F16) — null for a text-only model. Downloaded next to the model
    /// as "&lt;stem&gt;.mmproj-F16.gguf" (VisionProjector.LocalFileName) and passed to llama-server with --mmproj.</summary>
    public QuantOption? Projector { get; set; }
    /// <summary>GGUF "clip.projector_type" of <see cref="Projector"/> ("qwen3vl_merger", "pixtral", "gemma4v", …).</summary>
    public string ProjectorType { get; set; } = "";
    /// <summary>The backend's mtmd.dll reads that projector type (null = could not tell).</summary>
    public bool? ProjectorSupported { get; set; }
    /// <summary>Images can work: there is a projector and the backend is not known to reject it.</summary>
    public bool CanSeeImages => Projector != null && ProjectorSupported != false;
    public bool VisionNeedsNewerBackend => Projector != null && ProjectorSupported == false;
    public string ProjectorSizeText => Projector == null ? "" : ModelInfo.FormatFileSize(Projector.SizeBytes);

    private bool _visionReady;
    /// <summary>Installed together with its projector: the model will see images when loaded.</summary>
    public bool VisionReady { get => _visionReady; set { if (Set(ref _visionReady, value)) OnPropertyChanged(nameof(CanAddVision)); } }
    /// <summary>Installed without its projector and the backend can read it: offer "Add vision".</summary>
    public bool CanAddVision => IsInstalled && CanSeeImages && !VisionReady;
    /// <summary>Not installed yet: the projector comes with the download.</summary>
    public bool VisionComesWithDownload => !IsInstalled && CanSeeImages;

    public string Description { get; set; } = string.Empty;
    /// <summary>Default file (Q4_K_M, or the only 4-bit file).</summary>
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

    /// <summary>"★★★★☆" — plain symbols that every UI font draws (the ⭐ emoji rendered as dots).</summary>
    public string StarText => new string('★', Math.Clamp(Stars, 1, 5)) + new string('☆', 5 - Math.Clamp(Stars, 1, 5));

    /// <summary>The quant options (falls back to the single default file for an entry without a list).</summary>
    public IReadOnlyList<QuantOption> AllQuants =>
        Quants.Count > 0 ? Quants : new[] { new QuantOption(QuantNameOf(FileName), FileName, ApproxSizeBytes) };

    /// <summary>"Q4_K_M" out of "…-UD-Q4_K_M.gguf" ("" when the name carries no known quant).</summary>
    public static string QuantNameOf(string fileName)
    {
        string upper = (fileName ?? "").ToUpperInvariant();
        foreach (var q in KnownQuants)
            if (System.Text.RegularExpressions.Regex.IsMatch(upper, $@"(^|[-_.]){q}([.-]|$)")) return q;
        return "";
    }

    private static readonly string[] KnownQuants =
        ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q4_K_M", "Q4_K_S", "Q4_0", "IQ4_XS", "IQ4_NL", "Q3_K_L", "Q3_K_M", "Q3_K_S",
         "IQ3_M", "IQ3_XS", "IQ3_XXS", "Q2_K_L", "Q2_K", "MXFP4_MOE", "BF16", "F16", "F32"];

    // ─── State the page shows (set by ModelManagerViewModel; recomputed on every refresh) ───

    private bool _isInstalled;
    /// <summary>Whether a file of this model is already downloaded locally.</summary>
    public bool IsInstalled
    {
        get => _isInstalled;
        set { if (Set(ref _isInstalled, value)) { OnPropertyChanged(nameof(CanAddVision)); OnPropertyChanged(nameof(VisionComesWithDownload)); } }
    }

    private string? _installedPath;
    /// <summary>Local path if installed.</summary>
    public string? InstalledPath { get => _installedPath; set => Set(ref _installedPath, value); }

    private string _installedQuant = "";
    /// <summary>Quant of the installed file ("Q4_K_M").</summary>
    public string InstalledQuant { get => _installedQuant; set => Set(ref _installedQuant, value); }

    private bool _isDownloadingThis;
    /// <summary>A download of this model is running.</summary>
    public bool IsDownloadingThis { get => _isDownloadingThis; set => Set(ref _isDownloadingThis, value); }

    private bool _isLoaded;
    /// <summary>The installed file is the model loaded right now.</summary>
    public bool IsLoaded { get => _isLoaded; set => Set(ref _isLoaded, value); }

    /// <summary>False when the bundled llama.cpp does not know this architecture (null = could not tell).</summary>
    public bool? ArchSupported { get; set; }
    public bool NeedsNewerBackend => ArchSupported == false;

    /// <summary>Localized text the card shows.</summary>
    public string DescriptionText { get; set; } = "";
    public string ParamsText { get; set; } = "";
    public string ContextText { get; set; } = "";
    public string ReleasedText { get; set; } = "";
    public List<string> TaskChips { get; set; } = new();
    /// <summary>Why this model is recommended for this PC (localized, best reason first).</summary>
    public List<string> Reasons { get; set; } = new();
    public string ReasonsText => string.Join("  ·  ", Reasons);
    public double Score { get; set; }
    /// <summary>1-3 for the "Best for your PC" picks, 0 otherwise.</summary>
    public int PickRank { get; set; }

    private List<QuantPlan> _plans = new();
    public List<QuantPlan> Plans { get => _plans; set { if (Set(ref _plans, value)) OnPropertyChanged(nameof(HasQuantChoice)); } }
    public bool HasQuantChoice => _plans.Count > 1;

    private QuantPlan? _selectedPlan;
    /// <summary>The quant the card shows and Download fetches. Starts at the advisor's pick.</summary>
    public QuantPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!Set(ref _selectedPlan, value)) return;
            foreach (var p in new[] { nameof(FitTier), nameof(FitLabel), nameof(FitHint), nameof(FitColorKey), nameof(SpeedText),
                                      nameof(MemoryText), nameof(DownloadSizeText), nameof(RecommendedGpuLayers), nameof(SelectedFileName) })
                OnPropertyChanged(p);
        }
    }

    // ─── Fit Indicator — follows the selected quant ───

    /// <summary>Fit tier against the user's hardware for the selected quant.</summary>
    public ModelFitTier FitTier => _selectedPlan?.FitTier ?? ModelFitTier.Unknown;
    /// <summary>Short human label for the badge, e.g. "Fast" / "Partial" / "Slow".</summary>
    public string FitLabel => _selectedPlan?.FitLabel ?? "";
    /// <summary>One-line description shown in the tooltip.</summary>
    public string FitHint => _selectedPlan?.FitHint ?? "";
    public string SpeedText => _selectedPlan?.SpeedText ?? "";
    public string MemoryText => _selectedPlan?.MemoryText ?? "";
    public string DownloadSizeText => _selectedPlan?.SizeDisplay ?? ModelInfo.FormatFileSize(ApproxSizeBytes);
    public string SelectedFileName => _selectedPlan?.FileName ?? FileName;
    /// <summary>Suggested -ngl value for llama.cpp. -1 means "all layers on GPU".</summary>
    public int RecommendedGpuLayers => _selectedPlan?.SuggestedGpuLayers ?? -1;

    /// <summary>Color key used by the XAML DataTrigger for the badge/border accent.</summary>
    public string FitColorKey => ColorKeyOf(FitTier);

    public static string ColorKeyOf(ModelFitTier tier) => tier switch
    {
        ModelFitTier.Excellent => "Green",
        ModelFitTier.Good      => "Teal",
        ModelFitTier.Partial   => "Yellow",
        ModelFitTier.Poor      => "Peach",
        ModelFitTier.TooLarge  => "Red",
        _                      => "Overlay0",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>How well a model fits into the user's hardware.</summary>
public enum ModelFitTier
{
    Unknown,
    Excellent, // fits entirely with comfortable headroom — full GPU, fast
    Good,      // fits but tight — full GPU, may need to reduce context size
    Partial,   // most layers on GPU, some on CPU — medium speed
    Poor,      // heavy CPU offload — usable but slow
    TooLarge,  // does not fit in VRAM + RAM at this quantization
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
