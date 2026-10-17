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

        foreach (var p in Roots.Value)
        {
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase))
                return $"{full} is a protected folder";
            if (p.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"{full} contains the protected folder {p}";
        }
        return null;
    }

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
