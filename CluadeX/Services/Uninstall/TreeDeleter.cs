using System.IO;
using CluadeX.Services.Safety;

namespace CluadeX.Services.Uninstall;

/// <summary>Why a file or folder could not be deleted, for the page and the report.</summary>
public enum DeleteFailure { InUse, Denied, Other }

public sealed record DeleteProblem(string Path, DeleteFailure Kind, string Message);

/// <summary>
/// Deletes a tree the way an uninstaller must: everything it can, never stopping at the first locked file, never
/// following a junction or symlink into its target (the link is removed, the target stays), never outside the folder
/// it was given (<see cref="SafeDelete.CheckInside"/>). Counts what it freed and collects what it could not delete.
/// </summary>
public sealed class TreeDeleter
{
    private readonly string _ownedRoot;
    public long BytesFreed { get; private set; }
    public List<DeleteProblem> Problems { get; } = new();

    public TreeDeleter(string ownedRoot) { _ownedRoot = ownedRoot; }

    /// <summary>Delete a file or a folder inside the owned root. Returns true when it is gone afterwards.</summary>
    public bool Delete(string path)
    {
        // CheckInside, not Check: the data folder (~/.cluadex) and %TEMP% are protected themselves, and the uninstall
        // empties what CluadeX put in them (v3.0.275 refused every one: "the owning folder is protected").
        if (SafeDelete.CheckInside(path, _ownedRoot) is { } why)
        {
            Problems.Add(new DeleteProblem(path, DeleteFailure.Denied, "refused: " + why));
            return false;
        }
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.Directory))
            {
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) { TryRun(path, () => Directory.Delete(path, recursive: false)); }
                else DeleteDir(new DirectoryInfo(path));
            }
            else DeleteFile(new FileInfo(path));
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) { Problems.Add(Classify(path, ex)); }
        return !File.Exists(path) && !Directory.Exists(path);
    }

    private void DeleteDir(DirectoryInfo dir)
    {
        IEnumerable<FileSystemInfo> children;
        try { children = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) { Problems.Add(Classify(dir.FullName, ex)); return; }

        foreach (var child in children)
        {
            if (child is DirectoryInfo sub)
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) TryRun(sub.FullName, () => sub.Delete(recursive: false));
                else DeleteDir(sub);
            }
            else if (child is FileInfo file) DeleteFile(file);
        }
        TryRun(dir.FullName, () => dir.Delete(recursive: false), quietWhenNotEmpty: true);
    }

    private void DeleteFile(FileInfo file)
    {
        long size = 0;
        try { size = file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? 0 : file.Length; } catch { }
        if (TryRun(file.FullName, () =>
            {
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
            }))
            BytesFreed += size;
    }

    /// <summary>A folder whose files could not all be deleted cannot be removed either: that is already reported by
    /// the file, so the folder itself is not reported twice.</summary>
    private bool TryRun(string path, Action act, bool quietWhenNotEmpty = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { act(); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (FileNotFoundException) { return true; }
            catch (IOException ex) when (attempt < 2 && IsSharing(ex))
            {
                // A file another process is closing (an antivirus scan, the app that just exited): give it a moment.
                Thread.Sleep(150 * (attempt + 1));
            }
            catch (IOException) when (quietWhenNotEmpty && HasChildren(path)) { return false; }
            catch (Exception ex) { Problems.Add(Classify(path, ex)); return false; }
        }
    }

    private static bool HasChildren(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); } catch { return true; }
    }

    // ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33), ERROR_USER_MAPPED_FILE (1224)
    private static bool IsSharing(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224;

    internal static DeleteProblem Classify(string path, Exception ex) => ex switch
    {
        UnauthorizedAccessException => new DeleteProblem(path, DeleteFailure.Denied, ex.Message),
        IOException io when IsSharing(io) => new DeleteProblem(path, DeleteFailure.InUse, ex.Message),
        _ => new DeleteProblem(path, DeleteFailure.Other, ex.Message),
    };

    /// <summary>Bytes under a path (a junction counts as nothing — its target is not ours).</summary>
    public static long SizeOf(string path, CancellationToken ct = default)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (!attrs.HasFlag(FileAttributes.Directory)) return attrs.HasFlag(FileAttributes.ReparsePoint) ? 0 : new FileInfo(path).Length;
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) return 0;
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(path));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                IEnumerable<FileSystemInfo> items;
                try { items = d.EnumerateFileSystemInfos(); } catch { continue; }
                foreach (var i in items)
                {
                    if (i.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (i is DirectoryInfo sub) stack.Push(sub);
                    else if (i is FileInfo f) { try { total += f.Length; } catch { } }
                }
            }
            return total;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }
}
