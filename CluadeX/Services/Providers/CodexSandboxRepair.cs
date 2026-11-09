using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CluadeX.Services.Providers;

/// <summary>
/// Codex's Windows sandbox cannot start in a folder whose <c>.git</c> belongs to the sandbox's own account, and this puts
/// that right. Before each command Codex's setup step ("setup refresh", codex-windows-sandbox-setup.exe, running as the
/// user) puts a deny-write ACE on every write root's <c>.git</c>. That needs WRITE_DAC on <c>.git</c>, which only its
/// owner (or an elevated admin) has. A <c>git init</c> run inside the sandbox makes <c>.git</c> as
/// <c>CodexSandboxOnline</c>. From then on every command, file edit and node_repl call fails with
/// "helper_unknown_error: setup refresh had errors", every turn, until someone fixes the owner. Plain git as the user
/// refuses the repository too ("dubious ownership"). This happened in the XSurLab chat of 2026-11-09: 13 failed writes,
/// and the model wandered off to desktop tools. A non-elevated user cannot take ownership, but can read, rename and delete
/// the folder (Authenticated Users: Modify). So the repair copies it: the copy belongs to the user, the copy takes the
/// original's place, and the original is deleted once the copy matches it file for file.
/// </summary>
public static class CodexSandboxRepair
{
    /// <summary>What Codex prints for a command when its sandbox setup failed.</summary>
    public const string SetupRefreshFailed = "setup refresh had errors";

    /// <summary>The names the setup step protects inside a write root (seen in ~/.codex/.sandbox logs: only .git).</summary>
    public static readonly string[] ProtectedNames = [".git"];

    private static readonly object Gate = new();

    public sealed record Outcome(string Path, bool Repaired, string? Error);

    /// <summary>Does a command's output show the sandbox failing to start (rather than the command failing)?</summary>
    public static bool LooksLikeSetupFailure(string? output) =>
        output?.Contains(SetupRefreshFailed, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>One of Codex's sandbox accounts (CodexSandboxOnline, CodexSandboxOffline, …), as DOMAIN\name or name.</summary>
    public static bool IsSandboxAccount(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        string name = owner[(owner.LastIndexOf('\\') + 1)..];
        return name.StartsWith("CodexSandbox", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The owner of a file or folder as DOMAIN\name (or a SID string when the name cannot be mapped); null when unreadable.</summary>
    public static string? OwnerOf(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            var sid = sec.GetOwner(typeof(SecurityIdentifier));
            if (sid == null) return null;
            try { return sid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { return sid.Value; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return null; }
    }

    /// <summary>The protected entries (a .git folder, or a worktree's .git file) under <paramref name="roots"/> that a
    /// sandbox account owns. <paramref name="ownerOf"/> is for the harness; by default the real owner.</summary>
    public static List<string> SandboxOwned(IEnumerable<string?> roots, Func<string, string?>? ownerOf = null)
    {
        ownerOf ??= OwnerOf;
        var found = new List<string>();
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in ProtectedNames)
            {
                string p = Path.Combine(root!, name);
                if ((Directory.Exists(p) || File.Exists(p)) && IsSandboxAccount(ownerOf(p))) found.Add(p);
            }
        return found;
    }

    /// <summary>Finds and rebuilds every sandbox-owned protected entry under <paramref name="roots"/>. Empty = nothing to do.</summary>
    public static List<Outcome> RepairRoots(IEnumerable<string?> roots, Func<string, string?>? ownerOf = null) =>
        SandboxOwned(roots, ownerOf).Select(Rebuild).ToList();

    /// <summary>
    /// Replaces <paramref name="path"/> with a copy owned by this process's user. The original is renamed aside, never
    /// deleted before the copy has the same files and bytes; if the swap fails half-way it is put back. Links inside
    /// (junctions, symlinks) stop the repair: copying them would follow them out of the folder.
    /// </summary>
    public static Outcome Rebuild(string path)
    {
        lock (Gate)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string copy = path + ".cx-rebuild-" + stamp;
            string aside = path + ".cx-sandbox-" + stamp;
            try
            {
                if (File.Exists(path) && !Directory.Exists(path))
                {
                    File.Copy(path, copy);
                    File.SetAttributes(copy, File.GetAttributes(path));
                    File.Move(path, aside);
                    try { File.Move(copy, path); }
                    catch { File.Move(aside, path); throw; }
                    TryDelete(aside);
                    return new Outcome(path, true, File.Exists(aside) ? "the old copy is still at " + aside : null);
                }

                var src = new DirectoryInfo(path);
                if (HasLink(src)) return new Outcome(path, false, "it contains a link (junction or symlink)");
                var (files, bytes) = CopyTree(src, new DirectoryInfo(copy));
                var (cFiles, cBytes) = Measure(new DirectoryInfo(copy));
                if (cFiles != files || cBytes != bytes)
                {
                    TryDelete(copy);
                    return new Outcome(path, false, $"the copy does not match ({cFiles}/{files} files, {cBytes}/{bytes} bytes)");
                }
                Directory.Move(path, aside);
                try { Directory.Move(copy, path); }
                catch { Directory.Move(aside, path); throw; }
                TryDelete(aside);
                return new Outcome(path, true, Directory.Exists(aside) ? "the old copy is still at " + aside : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
            {
                // The copy goes only while the original is back in place; otherwise it is the only .git at the path's name.
                if (Directory.Exists(path) || File.Exists(path)) TryDelete(copy);
                return new Outcome(path, false, ex.Message);
            }
        }
    }

    private static bool HasLink(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static (int Files, long Bytes) CopyTree(DirectoryInfo src, DirectoryInfo dst)
    {
        dst.Create();
        int files = 0;
        long bytes = 0;
        foreach (var f in src.EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            string target = Path.Combine(dst.FullName, f.Name);
            f.CopyTo(target);
            File.SetAttributes(target, f.Attributes);
            files++;
            bytes += f.Length;
        }
        foreach (var d in src.EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            var (n, b) = CopyTree(d, new DirectoryInfo(Path.Combine(dst.FullName, d.Name)));
            files += n;
            bytes += b;
        }
        dst.Attributes = src.Attributes;
        return (files, bytes);
    }

    private static (int Files, long Bytes) Measure(DirectoryInfo dir)
    {
        int files = 0;
        long bytes = 0;
        foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            files++;
            bytes += f.Length;
        }
        return (files, bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
            if (!Directory.Exists(path)) return;
            var dir = new DirectoryInfo(path);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { dir.Delete(false); return; }
            foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
            dir.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The note a failed row gets after the sandbox could not start in a turn over <paramref name="roots"/>: repairs what it
    /// finds and says so, or says it could not, or (for a setup failure with no sandbox-owned .git) points at Codex's log.
    /// Null when the failure was not the sandbox's start-up and nothing was sandbox-owned.
    /// </summary>
    public static string? NoteAfterFailure(IEnumerable<string?> roots, bool setupFailed, Func<string, string?>? ownerOf = null)
    {
        var outcomes = RepairRoots(roots, ownerOf);
        var loc = LocalizedResources.Instance;
        if (outcomes.Count == 0)
            return setupFailed ? loc["codex.sandbox.setupFailed"] : null;
        return string.Join("\n", outcomes.Select(o => o.Repaired
            ? string.Format(loc["codex.sandbox.repaired"], o.Path)
            : string.Format(loc["codex.sandbox.repairFailed"], o.Path, o.Error)));
    }
}
