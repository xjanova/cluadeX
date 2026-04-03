using System.IO;

namespace CluadeX.Models;

public class AppSettings
{
    // Directory paths - defaults are set by SettingsService based on portable vs installed mode
    public string ModelDirectory { get; set; } = string.Empty;
    public string CacheDirectory { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public string TempDirectory { get; set; } = string.Empty;
    public string SessionDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Additional directories to scan for GGUF models (besides the main ModelDirectory).
    /// Users can add folders where they already have models downloaded.
    /// </summary>
    public List<string> AdditionalModelDirectories { get; set; } = new();

    public string? SelectedModelPath { get; set; }
    public string? SelectedModelName { get; set; }

    // Inference settings
    public uint ContextSize { get; set; } = 4096;
    public int GpuLayerCount { get; set; } = -1; // -1 = auto (all layers)
    public float Temperature { get; set; } = 0.6f;
    public float TopP { get; set; } = 0.9f;
    public int MaxTokens { get; set; } = 4096;
    public int RepeatPenaltyTokens { get; set; } = 64;
    public float RepeatPenalty { get; set; } = 1.1f;

    // Backend settings
    public string GpuBackend { get; set; } = "Auto";
    public int BatchSize { get; set; } = 512;
    public int ThreadCount { get; set; } = 0;

    // Agent settings
    public bool AutoExecuteCode { get; set; } = false;
    public int MaxAutoFixAttempts { get; set; } = 3;
    public string PreferredLanguage { get; set; } = "C#";

    // UI settings
    public double FontSize { get; set; } = 14;
    public bool StreamOutput { get; set; } = true;

    // HuggingFace settings
    public string? HuggingFaceToken { get; set; }

    // AI Provider settings
    public AiProviderType ActiveProvider { get; set; } = AiProviderType.Local;
    public Dictionary<string, ProviderConfig> ProviderConfigs { get; set; } = new();
}
