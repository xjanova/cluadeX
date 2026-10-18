using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Safety;

/// <summary>One line of code that would delete or wipe something it must never touch.</summary>
public sealed record DestructiveFinding(string File, int Line, string Text, string Why)
{
    public override string ToString() => $"{File}:{Line}  {Text}   ← {Why}";
}

/// <summary>
/// Before anything RUNS code (a test, a build, a script), read the code that was just written or changed and look for
/// a delete aimed at a drive root, a home folder or a system folder — the 2026-10-17 incident was a new test that
/// passed <c>cmd.exe /c rd /s /q C:\</c> to a tool "to prove the blocker refuses it". A command denylist never sees
/// that: the dangerous text is inside a .cs file, and only <c>dotnet run</c> reaches the shell.
///
/// It is aimed at the TARGET, so ordinary clean-up passes (<c>rm -rf dist</c>, <c>Directory.Delete(tempDir, true)</c>),
/// and the wipes that are dangerous whatever the target (format, diskpart, cipher /w …) are always flagged.
/// </summary>
public static class DestructiveCodeScan
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Shell and API deletes.</summary>
    private static readonly Regex DeleteVerb = new(
        @"\b(rd|rmdir)\s+/s\b|\bdel\s+(/[a-z]\s*)*/s\b|\berase\s+(/[a-z]\s*)*/s\b|(?<!\bgit\s+)\brm\s+-[a-z]*(rf|fr|r)[a-z]*\b|\bRemove-Item\b[^\r\n]*-(Recurse|r)\b|\brimraf\b|\bshutil\.rmtree\b|\brmtree\(|\bfs(\.promises)?\.(rm|rmdir)(Sync)?\(|\bDirectory\.Delete\(|\bFileSystem\.DeleteDirectory\(|\bos\.RemoveAll\(|\bremove_dir_all\(|\bRemove-Item\b",
        Opt);

    /// <summary>A home's vital folders (the list of ProtectedPaths.ProfileChildren): deleting one is deleting part of the
    /// home, whether the home is written as ~ / %USERPROFILE% or spelled out as C:\Users\name, /c/Users/name.</summary>
    private const string HomeVital =
        @"(\\\\|\\|/)+(AppData((\\\\|\\|/)+(Local|Roaming|LocalLow))?|Desktop|Documents|Downloads|Pictures|Music|Videos|OneDrive|Favorites|Contacts|Links|Saved Games|Searches|\.ssh|\.gnupg|\.git|\.config|\.claude|\.cluadex|\.brainx|\.codex|\.gitconfig)";

    /// <summary>A target that must never be deleted: a drive root, a home, a system folder.</summary>
    private static readonly Regex ProtectedTarget = new(
        // a bare drive root: C:  C:\  C:\\  C:/  C:\*  (followed by a quote, space, bracket or the end)
        @"(?<![\w.%$-])[A-Za-z]:(\\\\|\\|/)?\*?(?=[""'`\s)\];,]|$)" +
        // a profile, Users, Windows, Program Files, ProgramData directly
        @"|(?<![\w.-])[A-Za-z]:(\\\\|\\|/)+(Users|Windows|Program Files( \(x86\))?|ProgramData)((\\\\|\\|/)+[^\\/""'\s]+)?(\\\\|\\|/)?(?=[""'`\s)\];,]|$)" +
        // a vital folder of a profile spelled out: C:\Users\name\AppData, C:\Users\name\.cluadex … (AppData\Local\Temp\x is fine)
        @"|(?<![\w.-])[A-Za-z]:(\\\\|\\|/)+Users(\\\\|\\|/)+[^\\/""'\s]+" + HomeVital + @"(\\\\|\\|/)*\*?(?=[""'`\s)\];,]|$)" +
        // The same in Git Bash / WSL form: /c/ (the drive itself — the trailing / tells it from a cmd switch like /c or
        // /s), /c/Users, /c/Windows, /c/Program Files … and one folder in them, /mnt/c; and `rm … /c` (rm takes no
        // switches with a slash, so a bare /c there is the drive).
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+\*?(?=[""'`\s)\];,]|$)" +
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+(Users|Windows|Program Files( \(x86\))?|ProgramData)(/+[^/""'\s]+)?/*\*?(?=[""'`\s)\];,]|$)" +
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+Users/+[^/""'\s]+" + HomeVital + @"/*\*?(?=[""'`\s)\];,]|$)" +
        @"|(?<![\w.~$-])/mnt/[A-Za-z](?=[""'`\s)\];,]|$)" +
        @"|\brm\s+(-\S+\s+)*/[A-Za-z](?=[""'`\s)\];,|&]|$)" +
        // a home or system folder by variable — itself, or one of its vital folders (not any sub-folder: ~/tmp/x is fine)
        @"|(?<![\w.-])(~|\$HOME|\$\{HOME\}|%USERPROFILE%|%HOMEPATH%|%HOMEDRIVE%|%SYSTEMDRIVE%|%SYSTEMROOT%|%WINDIR%|%APPDATA%|%LOCALAPPDATA%|%PROGRAMFILES%|%PROGRAMDATA%|\$env:(USERPROFILE|HOMEPATH|HOMEDRIVE|SystemDrive|SystemRoot|windir|APPDATA|LOCALAPPDATA|ProgramFiles|ProgramData))" +
        "(" + HomeVital + @")?(\\\\|\\|/)*\*?(?=[""'`\s)\];,]|$)" +
        // a home or a drive root handed straight to a delete API
        @"|(Delete|rmtree|rmSync|rm|RemoveAll|remove_dir_all|DeleteDirectory)\(\s*(Environment\.GetFolderPath\(\s*(Environment\.)?SpecialFolder\.(UserProfile|MyDocuments|Personal|Desktop|DesktopDirectory|ApplicationData|LocalApplicationData|Windows|System|ProgramFiles|ProgramFilesX86|CommonApplicationData)\s*\)|os\.homedir\(\)|Path\.home\(\)|os\.path\.expanduser\(\s*[""']~[""']\s*\)|Path\.GetPathRoot\([^)]*\)|os\.UserHomeDir\(\))\s*[,)]" +
        // POSIX root
        @"|(?<=\s)/\*?(?=[""'`\s;]|$)|[""']/\*?[""']",
        Opt);

    /// <summary>"Everything here" — ., .., *, ./*, ../* as a delete's target: harmless in a build folder, a wipe in a home
    /// or a drive root, and the command does not say which. So it is put to the user like a protected target.</summary>
    private static readonly Regex BroadTarget = new(@"(?<=\s)(\.{1,2}/?\*?|\*|\.\./\*)(?=[""'`\s;|&)]|$)", Opt);

    /// <summary>Wipes that are dangerous whatever they point at.</summary>
    private static readonly Regex AlwaysWipe = new(
        @"\bformat\s+[a-z]:|\bdiskpart\b|\bFormat-Volume\b|\bClear-Disk\b|\bRemove-Partition\b|\binitialize-disk\b|\bcipher\s+/w\b|\bvssadmin\s+delete\b|\bwbadmin\s+delete\b|\bbcdedit\b|\bmkfs(\.\w+)?\s|\bdd\s+if=[^\r\n]*of=/dev/|\breg\s+delete\s+HK(LM|EY_LOCAL_MACHINE)|\btakeown\s+/f\s+[a-z]:\\?\s+/r",
        Opt);

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".psm1", ".bat", ".cmd",
        ".sh", ".bash", ".zsh", ".rb", ".go", ".rs", ".java", ".kt", ".php", ".pl", ".lua", ".dart", ".json", ".csproj",
        ".props", ".targets", ".yml", ".yaml", ".toml", ".mk", ".gradle", ".swift", ".c", ".cpp", ".h",
    };

    /// <summary>The finding for one line, or null.</summary>
    public static string? JudgeLine(string line)
    {
        if (line.Length > 4000) line = line[..4000];
        if (AlwaysWipe.Match(line) is { Success: true } w) return $"wipes a disk or system state ({w.Value.Trim()})";
        if (DeleteVerb.Match(line) is { Success: true } v)
        {
            if (ProtectedTarget.Match(line) is { Success: true } t) return $"deletes ({v.Value.Trim()}) aimed at {t.Value.Trim()}";
            if (BroadTarget.Match(line) is { Success: true } b) return $"deletes ({v.Value.Trim()}) everything in the folder it runs in ({b.Value.Trim()})";
        }
        return null;
    }

    public static List<DestructiveFinding> ScanText(string text, string fileLabel)
    {
        var found = new List<DestructiveFinding>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            if (JudgeLine(raw) is { } why)
            {
                string shown = raw.Trim();
                found.Add(new DestructiveFinding(fileLabel, n, shown.Length > 160 ? shown[..160] + "…" : shown, why));
                if (found.Count >= 50) break;
            }
        }
        return found;
    }

    /// <summary>
    /// Scan what changed in the project: with git, the modified and untracked files under <paramref name="root"/>
    /// (only when the repository is the project itself or a folder of it — a repository rooted at a home or a drive is
    /// treated as "no git" so nothing outside the project is read); without git, files written in the last
    /// <paramref name="recent"/>. Bounded: 2,000 files, 1 MB each.
    /// </summary>
    public static List<DestructiveFinding> ScanChanged(string root, TimeSpan? recent = null)
    {
        var findings = new List<DestructiveFinding>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return findings;
        string fullRoot = ProtectedPaths.Normalize(root);

        var files = ChangedByGit(fullRoot) ?? RecentlyWritten(fullRoot, recent ?? TimeSpan.FromHours(6));
        foreach (var file in files.Take(2000))
        {
            if (!CodeExtensions.Contains(Path.GetExtension(file))) continue;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > 1_000_000) continue;
                string rel = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
                findings.AddRange(ScanText(File.ReadAllText(file), rel));
                if (findings.Count >= 50) break;
            }
            catch { /* unreadable: nothing to judge */ }
        }
        return findings;
    }

    private static List<string>? ChangedByGit(string root)
    {
        try
        {
            var top = Git(root, "rev-parse", "--show-toplevel");
            if (top == null) return null;
            string repo = ProtectedPaths.Normalize(top.Trim());
            // A repository that is a home or a drive (the owner's profile WAS one): never let it widen the scan.
            if (ProtectedPaths.WhyProtected(repo) != null) return null;
            var status = Git(root, "status", "--porcelain", "-z", "--untracked-files=all", "--", ".");
            if (status == null) return null;
            var list = new List<string>();
            foreach (var entry in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.Length < 4) continue;
                string code = entry[..2];
                if (code.Contains('D')) continue;
                string path = Path.GetFullPath(Path.Combine(repo, entry[3..]));
                if (ProtectedPaths.IsInside(path, root) && File.Exists(path)) list.Add(path);
            }
            return list;
        }
        catch { return null; }
    }

    private static IEnumerable<string> RecentlyWritten(string root, TimeSpan window)
    {
        var since = DateTime.UtcNow - window;
        var stack = new Stack<string>();
        stack.Push(root);
        int seen = 0;
        while (stack.Count > 0 && seen < 20000)
        {
            string dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                seen++;
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(f); } catch { continue; }
                if (t >= since) yield return f;
            }
            foreach (var d in dirs)
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "dist" or "build" or "target" or "vendor") continue;
                try { if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                stack.Push(d);
            }
        }
    }

    private static string? Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        string o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return null; }
        return p.ExitCode == 0 ? o : null;
    }

    private static readonly Regex RunsCode = new(
        @"(^|[;&|\s(])(dotnet\s+(run|test|exec|build|watch|msbuild|vstest)|npm\s+(run|test|start|exec|t)\b|npx\b|pnpm\b|yarn\b|bun\b|node\b|deno\b|tsx\b|ts-node\b|python3?\b|py\b|pytest\b|cargo\s+(run|test|build)|go\s+(run|test|build)|java\b|gradle\w*\b|mvn\b|make\b|cmake\b|msbuild\b|ruby\b|php\b|perl\b|bash\s+\S+\.sh|sh\s+\S+\.sh|\./\S+|powershell(\.exe)?\s+(-\w+\s+)*-File|pwsh(\.exe)?\s+(-\w+\s+)*-File|cmd(\.exe)?\s+/c\s+\S+\.(bat|cmd)|\S+\.(bat|cmd|ps1|sh|exe)\b)",
        Opt);

    /// <summary>The command runs code someone may just have written (a test, a build, a script) — the moment to scan.</summary>
    public static bool RunsProjectCode(string command) => RunsCode.IsMatch(command ?? "");

    /// <summary>The message an agent (or the user) sees when a run is refused.</summary>
    public static string BlockMessage(IReadOnlyList<DestructiveFinding> findings) =>
        "CluadeX safety: refused to run this — code changed in this project would delete a drive, a home folder or a system folder:\n"
        + string.Join("\n", findings.Take(10).Select(f => "  " + f))
        + (findings.Count > 10 ? $"\n  … and {findings.Count - 10} more" : "")
        + "\nRemove these lines (a test of a safety check must use an inert sentinel, never a real target) and run again. "
        + "Only the user may run this code as it is, by hand.";
}
