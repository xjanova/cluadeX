using System.IO;

namespace CluadeX.Helpers;

/// <summary>
/// The one folder outside the project the file tools may use for throwaway files (a base64 dump, a probe script, a
/// downloaded page). Failure audit 2026-11-05: agents kept writing scratch files to %TEMP% or D:\Temp, the file tools
/// refused ("outside the project root"), and the run spent a step or two finding another way — or wrote the file into
/// the user's project instead. The refusal now names this folder, and the file tools accept it.
/// Narrow on purpose: not all of %TEMP% (other programs' files live there), one subfolder CluadeX owns.
/// </summary>
public static class AgentScratch
{
    private static readonly object Gate = new();
    private static bool _pruned;

    /// <summary>%TEMP%\cluadex-agent — created on first use, files older than <see cref="KeepDays"/> days removed then.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "cluadex-agent");

    public const int KeepDays = 3;

    /// <summary><paramref name="fullPath"/> (already GetFullPath'd) lies inside <see cref="Root"/>, the root itself excluded.</summary>
    public static bool Contains(string fullPath)
    {
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && fullPath.Length > root.Length;
    }

    /// <summary>Make sure the folder exists (and once per process, drop what earlier runs left). Never throws.</summary>
    public static void Ensure()
    {
        try
        {
            Directory.CreateDirectory(Root);
            lock (Gate)
            {
                if (_pruned) return;
                _pruned = true;
            }
            var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
            // Top level only, files and folders by their own time: never follows a junction out of the folder.
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles())
                try { if (file.LastWriteTimeUtc < cutoff) file.Delete(); } catch { }
            foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
                try
                {
                    if (dir.LastWriteTimeUtc >= cutoff) continue;
                    if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) dir.Delete(false);   // unlink, keep the target
                    else dir.Delete(true);
                }
                catch { }
        }
        catch { }
    }
}
