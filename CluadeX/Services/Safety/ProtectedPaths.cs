using System.IO;

namespace CluadeX.Services.Safety;

/// <summary>
/// The folders nothing in CluadeX may ever delete — nor any folder that CONTAINS one of them. Born from the
/// 2026-10-17 incident (a test ran <c>rd /s /q C:\</c> for 45 minutes and wiped the owner's home dot-folders,
/// laragon and parts of AppData): a delete is judged by WHERE it lands, never by who asked or why.
///
/// Protected: every drive root; Windows, Program Files, ProgramData; C:\Users and every user profile; the profile's
/// own top folders (AppData, Desktop, Documents, Downloads, Pictures, .ssh, .git …); the special folders Windows
/// knows; the folder CluadeX runs from. A path is refused when it IS one of them or is an ANCESTOR of one
/// (deleting C:\Users\xman\AppData\..\.. is deleting the profile).
/// </summary>
public static class ProtectedPaths
{
    private static readonly Lazy<List<string>> Roots = new(Build);

    /// <summary>Profile sub-folders that are a person's life, not scratch space.</summary>
    private static readonly string[] ProfileChildren =
    {
        "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", "Desktop", "Documents", "Downloads",
        "Pictures", "Music", "Videos", "OneDrive", "Favorites", "Contacts", "Links", "Saved Games", "Searches",
        ".ssh", ".gnupg", ".git", ".config", ".claude", ".cluadex", ".brainx", ".codex", ".gitconfig",
    };

    private static List<string> Build()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { set.Add(Normalize(p)); } catch { /* an unusable path protects nothing */ }
        }

        foreach (Environment.SpecialFolder f in Enum.GetValues(typeof(Environment.SpecialFolder)))
        {
            // These two are the folders projects and temp files live in — their CONTENTS are fair game, but the
            // folders themselves (and everything above them) still are not: handled by the ancestor rule below.
            try { Add(Environment.GetFolderPath(f)); } catch { }
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var child in ProfileChildren) Add(Path.Combine(profile, child));
        Add(Path.GetTempPath());
        Add(AppContext.BaseDirectory);
        foreach (var name in new[] { "SystemRoot", "windir", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "USERPROFILE", "PUBLIC" })
            Add(Environment.GetEnvironmentVariable(name));

        // Every profile on the machine, and the folder that holds them.
        try
        {
            string usersDir = Path.GetDirectoryName(Normalize(profile))!;
            Add(usersDir);
            foreach (var d in Directory.EnumerateDirectories(usersDir)) Add(d);
        }
        catch { }
        return set.ToList();
    }

    /// <summary>Full path, no trailing separator (a drive root keeps its "C:\").</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    /// <summary>
    /// Why <paramref name="path"/> must never be deleted, or null when it may be. Pure — never touches the disk
    /// beyond resolving the path, so tests can ask it about C:\ without anything at stake.
    /// </summary>
    public static string? WhyProtected(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "empty path";
        string full;
        try { full = Normalize(path); }
        catch { return "unresolvable path"; }

        string root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length || string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return $"{full} is a drive root";
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && full.Count(c => c == '\\') <= 4)
            return $"{full} is a network share root";

        foreach (var p in AllRoots())
        {
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase))
                return $"{full} is a protected folder";
            if (p.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"{full} contains the protected folder {p}";
        }
        return null;
    }

    /// <summary>
    /// Why the CONTENTS of <paramref name="root"/> may not be emptied even though <paramref name="root"/> is protected
    /// itself, or null when they may. A protected folder that is a leaf — CluadeX's own ~/.cluadex, the temp folder — keeps
    /// itself, but what CluadeX put inside it is CluadeX's to remove. Refused: a drive root or share root, and any folder
    /// that has another protected folder under it (the profile: emptying it would reach .ssh, Documents …).
    /// </summary>
    public static string? WhyContentsProtected(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "empty path";
        string full;
        try { full = Normalize(root); }
        catch { return "unresolvable path"; }

        string drive = Path.GetPathRoot(full) ?? "";
        if (full.Length <= drive.Length || string.Equals(full, drive.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return $"{full} is a drive root";
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && full.Count(c => c == '\\') <= 4)
            return $"{full} is a network share root";
        foreach (var p in AllRoots())
            if (p.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"{full} contains the protected folder {p}";
        return null;
    }

    // ─────────────── tests ───────────────

    private static readonly List<string> TestRoots = new();

    /// <summary>
    /// Tests only: treat <paramref name="path"/> as protected until the result is disposed — so a throwaway folder can
    /// stand in for ~/.cluadex or %TEMP%. It can only ADD protection, never remove any.
    /// </summary>
    public static IDisposable ProtectForTest(string path)
    {
        string n = Normalize(path);
        lock (TestRoots) TestRoots.Add(n);
        return new Release(() => { lock (TestRoots) TestRoots.Remove(n); });
    }

    private static IEnumerable<string> AllRoots()
    {
        List<string> extra;
        lock (TestRoots) extra = TestRoots.Count == 0 ? null! : TestRoots.ToList();
        return extra == null ? Roots.Value : Roots.Value.Concat(extra);
    }

    private sealed class Release(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or inside it.</summary>
    public static bool IsInside(string path, string root)
    {
        try
        {
            string p = Normalize(path), r = Normalize(root);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                   || p.StartsWith(r.TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
