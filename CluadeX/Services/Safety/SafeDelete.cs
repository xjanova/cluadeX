using System.IO;

namespace CluadeX.Services.Safety;

/// <summary>A delete that <see cref="SafeDelete"/> refused. Never caught and ignored silently by design: callers log it.</summary>
public sealed class UnsafeDeleteException : IOException
{
    public UnsafeDeleteException(string message) : base(message) { }
}

/// <summary>
/// The one way CluadeX deletes a folder. Refuses a protected place (<see cref="ProtectedPaths"/>) and anything outside
/// the folder the caller says it owns, never follows a junction or symlink into its target, and logs every refusal.
/// A recursive <c>Directory.Delete</c> anywhere else in the app is a bug.
/// </summary>
public static class SafeDelete
{
    /// <summary>Set once at startup (DebugLogService) so refusals reach the Debug Log.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Null when <paramref name="path"/> may be deleted as part of <paramref name="ownedRoot"/>, else why not.
    /// Pure: the decision only (what tests use — they never hand a real delete a protected path).</summary>
    public static string? Check(string? path, string? ownedRoot)
    {
        if (ProtectedPaths.WhyProtected(path) is { } why) return why;
        if (string.IsNullOrWhiteSpace(ownedRoot)) return "no owning folder given";
        if (ProtectedPaths.WhyProtected(ownedRoot) is { } rootWhy) return "the owning folder is protected: " + rootWhy;
        if (!ProtectedPaths.IsInside(path!, ownedRoot)) return $"{path} is outside {ownedRoot}";
        return null;
    }

    /// <summary>
    /// Delete <paramref name="path"/> (recursively) — only when it lies inside <paramref name="ownedRoot"/>, a folder the
    /// caller created or owns (its temp folder, its plugins folder). Returns false when the folder is already gone.
    /// Throws <see cref="UnsafeDeleteException"/> instead of deleting anything protected or outside.
    /// </summary>
    public static bool Directory(string path, string ownedRoot)
    {
        if (Check(path, ownedRoot) is { } why)
        {
            string msg = "SafeDelete refused: " + why;
            try { Log?.Invoke(msg); } catch { }
            throw new UnsafeDeleteException(msg);
        }
        var info = new DirectoryInfo(path);
        if (!info.Exists) return false;

        // A junction / symlink: remove the link only, never what it points at.
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete(recursive: false);
            return true;
        }
        DeleteTree(info);
        return true;
    }

    /// <summary>Depth-first, never entering a reparse point (a link inside the tree is unlinked, not followed).</summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (var sub in dir.EnumerateDirectories())
        {
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) sub.Delete(recursive: false);
            else DeleteTree(sub);
        }
        foreach (var file in dir.EnumerateFiles())
        {
            if (file.IsReadOnly) file.IsReadOnly = false;
            file.Delete();
        }
        dir.Delete(recursive: false);
    }
}
