using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>One row of the provider picker: what is shown for a provider (logo or text badge, and its name).</summary>
public sealed record ProviderOption(AiProviderType Type, string Name, ImageSource? Logo, string Badge)
{
    public bool HasLogo => Logo != null;
}

/// <summary>
/// Each provider's own mark from Assets\Logos (bundled, never fetched at run time; sources and licences in
/// Assets\Logos\ATTRIBUTION-providers.md). The six marks of the providers CluadeX had before are the same files the
/// Billing page uses. A provider with no bundled mark (OpenAI, BrainX, a custom endpoint) shows a text badge.
/// </summary>
public static class ProviderLogos
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static string? File(AiProviderType type) => type switch
    {
        AiProviderType.Anthropic => "anthropic.png",
        AiProviderType.ClaudeDev => "claude-code.png",
        AiProviderType.Gemini or AiProviderType.GeminiDev => "gemini.png",
        AiProviderType.AntigravityDev => "antigravity.png",
        AiProviderType.Ollama => "ollama.png",
        AiProviderType.Local or AiProviderType.LlamaServer => "llamacpp.png",
        _ => CompatiblePresets.For(type)?.Logo,
    };

    public static string Badge(AiProviderType type) => type switch
    {
        AiProviderType.OpenAI or AiProviderType.OpenAIDev => "AI",
        AiProviderType.BrainX => "BX",
        _ => CompatiblePresets.For(type)?.Badge is { Length: > 0 } b ? b : type.ToString()[..1],
    };

    /// <summary>The name the picker shows: the service's own name for the OpenAI-compatible ones, else the type name
    /// as the picker always showed it.</summary>
    public static string Name(AiProviderType type) => CompatiblePresets.For(type)?.Name ?? type.ToString();

    /// <summary>The logo as a frozen image, or null when none is bundled (or the resource cannot be read).</summary>
    public static ImageSource? Image(AiProviderType type)
    {
        if (File(type) is not { } file) return null;
        return Cache.GetOrAdd(file, f =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/CluadeX;component/Assets/Logos/{f}");
                bmp.DecodePixelWidth = 64;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }   // no WPF application (a test harness) or a missing file: the badge shows instead
        });
    }

    public static ProviderOption Option(AiProviderType type) => new(type, Name(type), Image(type), Badge(type));
}
