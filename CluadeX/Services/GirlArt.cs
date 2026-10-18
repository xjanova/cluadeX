using System.Collections;
using System.IO;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CluadeX.Services;

/// <summary>
/// The girls (BrainX, CluadeX, Claude, Codex, Gemini, Grok, MAI cut-outs) who stand beside the startup card and the
/// agent's questions. Every PNG under Assets\Girls is one of them — the csproj takes the folder by wildcard, so a new
/// PNG there is a new girl with no code change. Each pick is random but never the same girl twice in a row.
/// </summary>
public static class GirlArt
{
    private static readonly Random Rng = new();
    private static List<string>? _names;
    private static string? _last;
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resource names of the girls ("cluadex", "claude", …), read once from the assembly's .g.resources.</summary>
    public static IReadOnlyList<string> Names => _names ??= ReadNames();

    private static List<string> ReadNames()
    {
        var names = new List<string>();
        try
        {
            var asm = typeof(GirlArt).Assembly;
            using var stream = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources");
            if (stream == null) return names;
            using var reader = new ResourceReader(stream);
            foreach (DictionaryEntry e in reader)
                if (e.Key is string key && key.StartsWith("assets/girls/", StringComparison.OrdinalIgnoreCase)
                    && key.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    names.Add(Path.GetFileNameWithoutExtension(key));
        }
        catch { /* no girls: callers hide the picture */ }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>A random girl, not the one picked last time (across restarts too, via <paramref name="rememberIn"/>).
    /// Null when the build has none.</summary>
    public static string? PickName(string? rememberIn = null)
    {
        var names = Names;
        if (names.Count == 0) return null;
        string? previous = _last;
        if (previous == null && rememberIn != null)
            try { if (File.Exists(rememberIn)) previous = File.ReadAllText(rememberIn).Trim(); } catch { }
        var pool = names.Count > 1 ? names.Where(n => !string.Equals(n, previous, StringComparison.OrdinalIgnoreCase)).ToList() : names.ToList();
        string pick = pool[Rng.Next(pool.Count)];
        _last = pick;
        if (rememberIn != null)
            try { File.WriteAllText(rememberIn, pick); } catch { }
        return pick;
    }

    /// <summary>The girl's picture (decoded once, frozen so any thread may hold it), or null.</summary>
    public static ImageSource? Image(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/CluadeX;component/Assets/Girls/{name}.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return Cache[name] = bmp;
            }
            catch { return null; }
        }
    }

    /// <summary>Each girl's own glow, so the halo matches her outfit (her ChatCharacter colour).</summary>
    public static Color Glow(string? name) => ChatCharacter.ForGirl(name).Accent;
}
