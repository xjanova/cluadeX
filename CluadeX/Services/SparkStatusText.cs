using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CluadeX.Services;

/// <summary>NVIDIA's own logo (horizontal format, Assets/Logos/nvidia.png — source and rules in ATTRIBUTION.md there),
/// for the places that bind an image from code (the command palette row). XAML references the pack URI directly.</summary>
public static class SparkLogo
{
    public const string PackUri = "pack://application:,,,/CluadeX;component/Assets/Logos/nvidia.png";
    private static readonly Lazy<ImageSource?> Loaded = new(() =>
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(PackUri);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }   // no WPF application (a test harness): the row keeps its glyph
    });

    /// <summary>The logo as a frozen image, or null when the resource can't be read.</summary>
    public static ImageSource? Image => Loaded.Value;
}

/// <summary>
/// The DGX Spark link status in the UI language — one place for the title-bar chip, its tooltip and the Spark window,
/// so they always say the same. Only localized sentences: the exception behind an Unreachable reading is in the Debug Log.
/// </summary>
public static class SparkStatusText
{
    /// <summary>The chip's short label: "DGX Spark" before any Spark is set up, then "DGX Spark · ออนไลน์" etc.</summary>
    public static string Chip(LocalizationService loc, SparkLinkStatus s) => s.State switch
    {
        SparkLinkState.Checking => loc.T("spark.link.chip", loc.T("spark.link.checking")),
        SparkLinkState.Online => loc.T("spark.link.chip", loc.T("spark.link.online")),
        SparkLinkState.InUse => loc.T("spark.link.chip", s.Model ?? loc.T("spark.link.online")),
        SparkLinkState.Unreachable => loc.T("spark.link.chip", loc.T("spark.link.offline")),
        _ => loc.T("spark.chip"),
    };

    /// <summary>The full sentence: where, what, and — when it doesn't answer — why and what to do.</summary>
    public static string Tip(LocalizationService loc, SparkLinkStatus s) => s.State switch
    {
        SparkLinkState.Checking => loc.T("spark.link.tip.checking", s.Host ?? ""),
        SparkLinkState.Online => loc.T("spark.link.tip.online", s.Host ?? "", s.Version ?? "?"),
        SparkLinkState.InUse => loc.T("spark.link.tip.inUse", s.Model ?? "", s.Host ?? "", s.Version ?? "?"),
        SparkLinkState.Unreachable => loc.T("spark.link.tip.unreachable", Reason(loc, s.Host ?? "", s.Reason)),
        _ => loc.T("spark.link.tip.notConfigured"),
    };

    /// <summary>Why <paramref name="host"/> didn't answer, and what to check — the window's error line and the tooltip.</summary>
    public static string Reason(LocalizationService loc, string host, SparkFailure reason) => reason switch
    {
        SparkFailure.HostNotFound => loc.T("spark.reason.hostNotFound", host),
        SparkFailure.Refused => loc.T("spark.reason.refused", host),
        SparkFailure.Timeout => loc.T("spark.reason.timeout", host),
        SparkFailure.Server or SparkFailure.BadResponse => loc.T("spark.reason.notOllama", host),
        _ => loc.T("spark.unreachable", host),
    };
}
