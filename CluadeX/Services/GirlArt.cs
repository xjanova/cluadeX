using System.Collections;
using System.IO;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CluadeX.Services;

/// <summary>
/// The girls (Antigravity, BrainX, CluadeX, Claude, Codex, Gemini, Grok, MAI cut-outs) who stand beside the startup card
/// and the agent's questions. Every PNG directly under Assets\Girls is one of them — the csproj takes the folder by
/// wildcard, so a new PNG there is a new girl with no code change. Each pick is random but never the same girl twice in
/// a row.
///
/// Each girl has two poses (owner's art, 2026-11-07 — "ใช้ภาพ พวกเธอแทนภาพชุดเก่าที่ตัดมาไม่ชัดทั้งหมด"): pose 1 is
/// Assets\Girls\{name}.png (her face is measured on it, <see cref="ChatCharacter"/>), pose 2 is
/// Assets\Girls\Pose2\{name}.png. A PNG in a subfolder is never a girl of its own. Cut-outs are 1000 px tall.
/// </summary>
public static class GirlArt
{
    private static readonly Random Rng = new();
    private static List<string>? _names;
    private static string? _last;
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    private const string Folder = "assets/girls/";

    /// <summary>How many poses each girl has: pose 1 (her main picture) and pose 2.</summary>
    public const int PoseCount = 2;

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
                if (e.Key is string key && key.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)
                    && key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && key.IndexOf('/', Folder.Length) < 0)   // Pose2/… is a pose, not another girl
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

    /// <summary>The pack URI of a girl's picture in a pose (1 = her main picture, 2 = Pose2\).</summary>
    public static Uri UriOf(string name, int pose = 1) => new(pose == 2
        ? $"pack://application:,,,/CluadeX;component/Assets/Girls/Pose2/{name}.png"
        : $"pack://application:,,,/CluadeX;component/Assets/Girls/{name}.png", UriKind.Absolute);

    /// <summary>A random pose (1 or 2).</summary>
    public static int PickPose() { lock (Rng) return Rng.Next(1, PoseCount + 1); }

    /// <summary>The girl's picture (decoded once, frozen so any thread may hold it), or null.</summary>
    public static ImageSource? Image(string? name) => PoseImage(name, 1);

    /// <summary>The girl's picture in a pose (1 or 2; pose 2 falls back to pose 1 when the build has none), or null.</summary>
    public static ImageSource? PoseImage(string? name, int pose)
    {
        if (string.IsNullOrEmpty(name)) return null;
        pose = pose == 2 ? 2 : 1;
        string key = pose == 1 ? name : name + "#2";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = UriOf(name, pose);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return Cache[key] = bmp;
            }
            catch { return pose == 2 ? PoseImage(name, 1) : null; }
        }
    }

    private static readonly Dictionary<string, ImageSource> Small = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The girl's picture decoded at most <paramref name="pixelHeight"/> pixels tall — what a backdrop shows,
    /// not the full 1000-pixel cut-out. Cached per name, pose and height, frozen; null when there is no such girl.</summary>
    public static ImageSource? Image(string? name, int pixelHeight, int pose = 1)
    {
        if (string.IsNullOrEmpty(name) || pixelHeight <= 0) return null;
        pose = pose == 2 ? 2 : 1;
        string key = name + "#" + pose + "@" + pixelHeight;
        lock (Small)
        {
            if (Small.TryGetValue(key, out var cached)) return cached;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = UriOf(name, pose);
                bmp.DecodePixelHeight = pixelHeight;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return Small[key] = bmp;
            }
            catch { return pose == 2 ? Image(name, pixelHeight, 1) : null; }
        }
    }

    /// <summary>Each girl's own glow, so the halo matches her outfit (her ChatCharacter colour).</summary>
    public static Color Glow(string? name) => ChatCharacter.ForGirl(name).Accent;
}
