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
/// <para>The same block hits ordinary folders (XSurLab chat, 2026-11-09 evening: 20+ "write file … failed" rows). The setup
/// step grants the sandbox's write SID on the write root as an inheritable ACE, and Windows copies inherited ACEs into each
/// child when it is made or when the parent's ACL is set again. A child folder the sandbox account made earlier, under an
/// older write SID, can be re-stamped only by its owner — so it keeps the old SID: <c>src</c>, <c>tests</c>, <c>design</c>
/// … and everything under them refused every edit, while files at the root were written. Such a folder is rebuilt the same
/// way; the copy inherits the root's ACEs as it is made.</para>
/// </summary>
public static class CodexSandboxRepair
{
    /// <summary>What Codex prints for a command when its sandbox setup failed.</summary>
    public const string SetupRefreshFailed = "setup refresh had errors";

    /// <summary>The names the setup step protects inside a write root (seen in ~/.codex/.sandbox logs: only .git).</summary>
    public static readonly string[] ProtectedNames = [".git"];

    private static readonly object Gate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SidNames = new();

    /// <param name="Folder">A work file or folder missing the sandbox's write grant (not a .git) — the note says so.</param>
    public sealed record Outcome(string Path, bool Repaired, string? Error, bool Folder = false);

    /// <summary>How far the scan goes before a turn: it must stay quick on a large tree.</summary>
    public const int MaxEntriesScanned = 60000;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);
    /// <summary>After an edit failed the walk may take longer: the turn is already stuck on it.</summary>
    private static readonly TimeSpan FailureScanBudget = TimeSpan.FromSeconds(10);

    /// <summary>A rebuild bigger than this is refused (it would copy a whole package cache before the turn).</summary>
    public const int MaxRebuildFiles = 20000;
    public const long MaxRebuildBytes = 1L << 30;

    /// <summary>Folders the scan skips: package caches and build output. Shell commands (which have the cwd's SID) fill
    /// them, the edit helper never writes there, and they are far too big to copy.</summary>
    private static readonly HashSet<string> NotWalked = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".venv", "venv", "packages", ".gradle", "target", ".nuget", "bin", "obj", ".vs", ".next", ".nuxt",
          "__pycache__", ".pytest_cache", ".mypy_cache", ".turbo", ".parcel-cache", "vendor" };

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
            if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier sid) return null;
            // A name lookup costs about half a millisecond, and a walk asks for thousands of owners that are the same
            // two or three accounts: each SID is looked up once.
            return SidNames.GetOrAdd(sid.Value, _ =>
            {
                try { return sid.Translate(typeof(NTAccount)).Value; }
                catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return sid.Value; }
            });
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

    /// <summary>The SIDs <paramref name="root"/> hands down: its own (not inherited) allow ACEs that folders inherit
    /// (<paramref name="toFiles"/> false) or files inherit (true). Codex's setup step puts its write SIDs here.</summary>
    public static HashSet<string> InheritedGrants(string root, bool toFiles)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rules = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            var flag = toFiles ? InheritanceFlags.ObjectInherit : InheritanceFlags.ContainerInherit;
            foreach (FileSystemAccessRule r in rules)
                if (r.AccessControlType == AccessControlType.Allow && r.InheritanceFlags.HasFlag(flag))
                    sids.Add(r.IdentityReference.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { }
        return sids;
    }

    /// <summary>The SIDs a file or folder allows anything to (its own and inherited ACEs); null when its ACL is protected
    /// from inheritance (someone chose that) or cannot be read.</summary>
    public static HashSet<string>? GrantsOf(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            if (sec.AreAccessRulesProtected) return null;
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (r.AccessControlType == AccessControlType.Allow) sids.Add(r.IdentityReference.Value);
            return sids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return null; }
    }

    /// <summary>
    /// The highest files and folders under <paramref name="root"/> that a sandbox account owns and that miss one of the
    /// root's inherited grants. Codex gives one folder two write SIDs (~/.codex/cap_sid): one when it is the cwd (shell
    /// commands) and one when it is a writable root of a helper started elsewhere (the file-edit helper starts at the drive
    /// root). What the sandbox made before the second SID reached the root keeps only the first, and the setup step cannot
    /// add the second to something the sandbox account owns — so edits in it fail. A found folder is not walked into (its
    /// rebuild covers what is below). .git, links, package caches and build output are not walked; the scan stops after
    /// <see cref="MaxEntriesScanned"/> entries or <paramref name="budget"/> (two seconds). With <paramref name="allFiles"/>
    /// false only folders and the files at the root are looked at — an owner lookup per file is what makes a walk slow
    /// (7,283 entries took 1.9 s in CluadeX's own repository). The delegates are for the harness.
    /// </summary>
    public static List<string> StaleEntries(string root, Func<string, string?>? ownerOf = null,
        Func<string, bool, HashSet<string>>? rootGrants = null, Func<string, HashSet<string>?>? grantsOf = null,
        TimeSpan? budget = null, bool allFiles = true)
    {
        var limit = budget ?? ScanBudget;
        ownerOf ??= OwnerOf;
        grantsOf ??= GrantsOf;
        rootGrants ??= InheritedGrants;
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;
        var wantDirs = rootGrants(root, false);
        var wantFiles = rootGrants(root, true);
        if (wantDirs.Count == 0 && wantFiles.Count == 0) return found;
        var started = DateTime.UtcNow;
        var queue = new Queue<string>();
        queue.Enqueue(root);
        int seen = 0;
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        bool Stale(string path, HashSet<string> want) =>
            want.Count > 0 && IsSandboxAccount(ownerOf(path)) && grantsOf(path) is { } has && !want.IsSubsetOf(has);
        while (queue.Count > 0 && seen < MaxEntriesScanned && DateTime.UtcNow - started < limit)
        {
            string dir = queue.Dequeue();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { continue; }
            foreach (var e in entries)
            {
                seen++;
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (ProtectedNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (e.Name.Contains(".cx-rebuild-", StringComparison.Ordinal) || e.Name.Contains(".cx-sandbox-", StringComparison.Ordinal)) continue;
                bool isDir = e is DirectoryInfo;
                if (isDir && NotWalked.Contains(e.Name)) continue;
                if (!isDir && !allFiles && !string.Equals(dir, root, StringComparison.Ordinal)) continue;
                if (Stale(e.FullName, isDir ? wantDirs : wantFiles))
                {
                    found.Add(e.FullName);
                    continue;
                }
                if (isDir) queue.Enqueue(e.FullName);
            }
        }
        return found;
    }

    /// <summary>The roots already walked, with the grants their root handed down then. A file or folder goes stale only
    /// when a new write SID reaches the root, so a root whose grants are the same is not walked again before a turn.</summary>
    private static readonly Dictionary<string, string> Walked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The grants a root hands down, as one comparable string.</summary>
    private static string GrantsKey(string root, Func<string, bool, HashSet<string>> rootGrants) =>
        string.Join(",", rootGrants(root, false).Order(StringComparer.OrdinalIgnoreCase)) + "|"
        + string.Join(",", rootGrants(root, true).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Finds and rebuilds every sandbox-owned protected entry under <paramref name="roots"/>, then every work
    /// file or folder that misses one of the sandbox's write grants. Empty = nothing to do.
    /// <paramref name="beforeTurn"/>: a root walked before with the same grants is not walked again (a walk can take up to
    /// two seconds on a big tree, and what it found and could not fix is not reported on every turn). After a failure the
    /// walk always runs.</summary>
    public static List<Outcome> RepairRoots(IEnumerable<string?> roots, Func<string, string?>? ownerOf = null,
        Func<string, bool, HashSet<string>>? rootGrants = null, Func<string, HashSet<string>?>? grantsOf = null,
        bool beforeTurn = false)
    {
        var list = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var outcomes = SandboxOwned(list, ownerOf).Select(p => Rebuild(p)).ToList();
        var grants = rootGrants ?? InheritedGrants;
        foreach (var root in list)
        {
            string key = GrantsKey(root, grants);
            lock (Walked)
            {
                if (beforeTurn && Walked.TryGetValue(root, out var seen) && seen == key) continue;
            }
            // Before a turn: folders (and the root's files), quickly. After a failure: every file too.
            foreach (var entry in StaleEntries(root, ownerOf, rootGrants, grantsOf, beforeTurn ? ScanBudget : FailureScanBudget, allFiles: !beforeTurn))
                outcomes.Add(Rebuild(entry, sizeLimit: true) with { Folder = true });
            lock (Walked) Walked[root] = key;
        }
        return outcomes;
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with a copy owned by this process's user. The original is renamed aside, never
    /// deleted before the copy has the same files and bytes; if the swap fails half-way it is put back. Links inside
    /// (junctions, symlinks) stop the repair: copying them would follow them out of the folder.
    /// </summary>
    /// <param name="sizeLimit">Refuse a tree bigger than MaxRebuildFiles / MaxRebuildBytes (work folders; a .git is always rebuilt).</param>
    public static Outcome Rebuild(string path, bool sizeLimit = false)
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
                var (have, size) = sizeLimit ? Measure(src) : (0, 0L);
                if (have > MaxRebuildFiles || size > MaxRebuildBytes)
                    return new Outcome(path, false, $"it is too big to copy before a turn ({have} files, {size >> 20} MB)");
                var copyStarted = DateTime.UtcNow;
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
                // Something wrote in the folder while it was copied (a turn still running in it): the copy may miss that
                // write, so the original goes back and nothing is lost.
                if (ChangedSince(new DirectoryInfo(aside), copyStarted))
                {
                    Directory.Move(path, copy);
                    Directory.Move(aside, path);
                    TryDelete(copy);
                    return new Outcome(path, false, "it changed while it was being copied");
                }
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

    /// <summary>Was anything in the tree (a file, or a folder's list of entries) written at or after <paramref name="utc"/>?</summary>
    private static bool ChangedSince(DirectoryInfo dir, DateTime utc)
    {
        if (dir.LastWriteTimeUtc >= utc) return true;
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (e.LastWriteTimeUtc >= utc) return true;
        return false;
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
            ? string.Format(loc[o.Folder ? "codex.sandbox.folderRepaired" : "codex.sandbox.repaired"], o.Path)
            : string.Format(loc[o.Folder ? "codex.sandbox.folderRepairFailed" : "codex.sandbox.repairFailed"], o.Path, o.Error)));
    }
}
