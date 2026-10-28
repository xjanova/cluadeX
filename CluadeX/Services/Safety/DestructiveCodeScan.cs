using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Safety;

/// <summary>One line of code that would wipe a whole drive, the root, a home folder, Windows' backups or a database.</summary>
public sealed record DestructiveFinding(string File, int Line, string Text, string Why)
{
    public override string ToString() => $"{File}:{Line}  {Text}   ← {Why}";
}

/// <summary>
/// Before anything RUNS code (a test, a build, a script), read the code that was just written or changed and look for
/// what the red prompt is for — the 2026-10-17 incident was a new test that passed <c>cmd.exe /c rd /s /q C:\</c> to a
/// tool "to prove the blocker refuses it". A command denylist never sees that: the dangerous text is inside a .cs file,
/// and only <c>dotnet run</c> reaches the shell.
///
/// Owner 2026-10-28 ("ลดความเข้มของกล่องแดง", then "ใส่ home กลับ"): flagged are wiping a whole drive (format, diskpart,
/// Clear-Disk …), a delete aimed at the ROOT (a drive root, the POSIX root) or at a HOME folder (C:\Users, a profile,
/// its vital folders), deleting Windows' backups or boot settings, and wiping a database. Everything else — a system
/// folder, "everything here", clean-up — is not this scan's business.
///
/// Owner 2026-10-28 ("แยกให้ออกว่าเป็นเพียงโค๊ดในโปรแกรม หรือเป็นการรันเพื่อใช้คำสั่งจริงๆ"): only what would really run
/// counts. A command is judged statement by statement, and a statement that only prints, searches or records the text
/// runs nothing (<see cref="JudgeCommand"/>). In code, comments, regex patterns, printed / logged text and text handed
/// to a check are data — unless the same line starts a process (<see cref="ScanText"/>).
/// </summary>
public static class DestructiveCodeScan
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Shell and API deletes. cmd takes its switches in any order: rd /q /s is rd /s /q.</summary>
    private static readonly Regex DeleteVerb = new(
        @"\b(rd|rmdir)\s+(/[a-z]\s*)*/s\b|\bdel\s+(/[a-z]\s*)*/s\b|\berase\s+(/[a-z]\s*)*/s\b|(?<!\bgit\s+)\brm\s+-[a-z]*(rf|fr|r)[a-z]*\b|\bRemove-Item\b[^\r\n]*-(Recurse|r)\b|\brimraf\b|\bshutil\.rmtree\b|\brmtree\(|\bfs(\.promises)?\.(rm|rmdir)(Sync)?\(|\bDirectory\.Delete\(|\bFileSystem\.DeleteDirectory\(|\bos\.RemoveAll\(|\bremove_dir_all\(|\bRemove-Item\b",
        Opt);

    /// <summary>The ROOT as a delete's target: a drive root or the POSIX root — not a folder in it (/c/Windows and the
    /// like are no longer asked about, owner 2026-10-28; a home is <see cref="HomeTarget"/>).</summary>
    private static readonly Regex RootTarget = new(
        // a bare drive root: C:  C:\  C:\\  C:/  C:\*  C:\*.*  (followed by a quote, space, bracket or the end)
        @"(?<![\w.%$-])[A-Za-z]:(\\\\|\\|/)?(\*(\.\*)?)?(?=[""'`\s)\];,]|$)" +
        // The same in Git Bash / WSL form: /c/ (the drive itself — the trailing / tells it from a cmd switch like /c or
        // /s), /c/*, /mnt/c; and `rm … /c` (rm takes no switches with a slash, so a bare /c there is the drive).
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+(\*(\.\*)?)?(?=[""'`\s)\];,]|$)" +
        @"|(?<![\w.~$-])/mnt/[A-Za-z](?=[""'`\s)\];,]|$)" +
        @"|\brm\s+(-\S+\s+)*/[A-Za-z](?=[""'`\s)\];,|&]|$)" +
        // a variable that IS a drive root
        @"|(?<![\w.-])(%SYSTEMDRIVE%|%HOMEDRIVE%|\$env:(SystemDrive|HOMEDRIVE))(\\\\|\\|/)*(\*(\.\*)?)?(?=[""'`\s)\];,]|$)" +
        // a drive root handed straight to a delete API
        @"|(Delete|rmtree|rmSync|rm|RemoveAll|remove_dir_all|DeleteDirectory)\(\s*Path\.GetPathRoot\([^)]*\)\s*[,)]" +
        // POSIX root
        @"|(?<=\s)/\*?(?=[""'`\s;]|$)|[""']/\*?[""']",
        Opt);

    /// <summary>A home's vital folders (the list of ProtectedPaths.ProfileChildren): deleting one is deleting part of the
    /// home, whether the home is written as ~ / %USERPROFILE% or spelled out as C:\Users\name, /c/Users/name.</summary>
    private const string HomeVital =
        @"(\\\\|\\|/)+(AppData((\\\\|\\|/)+(Local|Roaming|LocalLow))?|Desktop|Documents|Downloads|Pictures|Music|Videos|OneDrive|Favorites|Contacts|Links|Saved Games|Searches|\.ssh|\.gnupg|\.git|\.config|\.claude|\.cluadex|\.brainx|\.codex|\.gitconfig)";

    /// <summary>A HOME as a delete's target (owner 2026-10-28, "ใส่ home กลับ" — the 2026-10-17 wipe took the home's
    /// dot-folders first): C:\Users, a profile, or one of its vital folders — spelled out, in Git Bash / WSL form, by
    /// variable or handed to a delete API. A folder deeper in them (AppData\Local\Temp\x, ~/tmp/cache) is not.</summary>
    private static readonly Regex HomeTarget = new(
        // C:\Users or a profile directly: C:\Users, C:\Users\name, C:\Users\name\*
        @"(?<![\w.-])[A-Za-z]:(\\\\|\\|/)+Users((\\\\|\\|/)+[^\\/""'\s]+)?(\\\\|\\|/)?\*?(?=[""'`\s)\];,]|$)" +
        // a vital folder of a profile spelled out: C:\Users\name\AppData, C:\Users\name\.cluadex …
        @"|(?<![\w.-])[A-Za-z]:(\\\\|\\|/)+Users(\\\\|\\|/)+[^\\/""'\s]+" + HomeVital + @"(\\\\|\\|/)*\*?(?=[""'`\s)\];,]|$)" +
        // the same in Git Bash / WSL form: /c/Users, /c/Users/name, /mnt/c/Users/name/.ssh …
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+Users(/+[^/""'\s]+)?/*\*?(?=[""'`\s)\];,]|$)" +
        @"|(?<![\w.~$-])/(mnt/)?[A-Za-z]/+Users/+[^/""'\s]+" + HomeVital + @"/*\*?(?=[""'`\s)\];,]|$)" +
        // a home by variable — itself, or one of its vital folders (not any sub-folder: ~/tmp/x is fine)
        @"|(?<![\w.-])(~|\$HOME|\$\{HOME\}|%USERPROFILE%|%HOMEPATH%|%APPDATA%|%LOCALAPPDATA%|\$env:(USERPROFILE|HOMEPATH|APPDATA|LOCALAPPDATA))" +
        "(" + HomeVital + @")?(\\\\|\\|/)*\*?(?=[""'`\s)\];,]|$)" +
        // a home handed straight to a delete API
        @"|(Delete|rmtree|rmSync|rm|RemoveAll|remove_dir_all|DeleteDirectory)\(\s*(Environment\.GetFolderPath\(\s*(Environment\.)?SpecialFolder\.(UserProfile|MyDocuments|Personal|Desktop|DesktopDirectory|ApplicationData|LocalApplicationData)\s*\)|os\.homedir\(\)|Path\.home\(\)|os\.path\.expanduser\(\s*[""']~[""']\s*\)|os\.UserHomeDir\(\))\s*[,)]",
        Opt);

    /// <summary>Wiping a whole drive, whatever the command points at.</summary>
    private static readonly Regex DriveWipe = new(
        @"\bformat\s+[a-z]:|\bdiskpart\b|\bFormat-Volume\b|\bClear-Disk\b|\bRemove-Partition\b|\binitialize-disk\b|\bmkfs(\.\w+)?\s|\bdd\s+if=[^\r\n]*of=/dev/",
        Opt);

    /// <summary>Deleting the backups Windows keeps (shadow copies, system-image backups) or rewriting how it boots — the
    /// way back after a wipe (owner 2026-10-28, kept with the home folder). Reading them (bcdedit /enum) is not.</summary>
    private static readonly Regex BackupOrBootWipe = new(
        @"\bvssadmin(\.exe)?\s+(delete\s+shadows|resize\s+shadowstorage)\b|\bwmic(\.exe)?\s+shadowcopy\s+delete\b|\bwbadmin(\.exe)?\s+delete\b" +
        @"|\bbcdedit(\.exe)?\s+/(set|delete|deletevalue|create|copy|import|default|displayorder|bootsequence|timeout|store)\b",
        Opt);

    /// <summary>Wiping a database: SQL's drop of a database or schema (with a name after it), the Postgres and Mongo drop
    /// commands, Redis's flush of everything, and the framework commands that drop every table (Laravel, Rails, Prisma,
    /// EF Core, Django). Not a single table, not a delete of rows.</summary>
    private static readonly Regex DatabaseWipe = new(
        @"\bdrop\s+(database|schema)\s+(if\s+exists\s+)?[\[`""']?[A-Za-z_]" +
        @"|\bdropdb\b|\bdropDatabase\s*\(|\bflushall\b|\bflushdb\b" +
        @"|\bdb:(wipe|drop|reset)\b|\bmigrate:(fresh|reset|refresh)\b|\bprisma\s+(migrate\s+reset|db\s+push\b[^\r\n]*--force-reset)" +
        @"|\bef\s+database\s+drop\b|\bmanage\.py\s+flush\b",
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
        if (DriveWipe.Match(line) is { Success: true } w) return $"wipes a whole drive ({w.Value.Trim()})";
        if (DatabaseWipe.Match(line) is { Success: true } db) return $"wipes a database ({db.Value.Trim()})";
        if (BackupOrBootWipe.Match(line) is { Success: true } bk) return $"deletes Windows' backups or boot settings ({bk.Value.Trim()})";
        if (DeleteVerb.Match(line) is not { Success: true } v) return null;
        // A shell delete anywhere on the line (rd /s, rm -rf, Remove-Item … even inside a string) is read strictly.
        for (var s = v; s.Success; s = s.NextMatch())
            if (!s.Value.EndsWith('(')) { v = s; break; }
        if (!v.Value.EndsWith('('))
        {
            if (RootTarget.Match(line) is { Success: true } t) return $"deletes ({v.Value.Trim()}) aimed at the root {t.Value.Trim()}";
            if (HomeTarget.Match(line) is { Success: true } h) return $"deletes ({v.Value.Trim()}) aimed at the home folder {h.Value.Trim()}";
            return null;
        }
        // A delete API in code (Directory.Delete(, rmtree(, fs.rm( …): judged without its comments, and a bare / or /*
        // there is a comment or a division, not the POSIX root (owner 2026-10-18: the red prompt came up on every
        // build for `try { Directory.Delete(Root, recursive: true); } catch { /* a child may still be exiting */ }`).
        string code = CodeComment.Replace(line, " ");
        if (DeleteVerb.Match(code) is not { Success: true } cv) return null;   // the delete itself was commented out
        for (var t = RootTarget.Match(code); t.Success; t = t.NextMatch())
            if (t.Value.Trim() is not ("/" or "/*")) return $"deletes ({cv.Value.Trim()}) aimed at the root {t.Value.Trim()}";
        if (HomeTarget.Match(code) is { Success: true } home) return $"deletes ({cv.Value.Trim()}) aimed at the home folder {home.Value.Trim()}";
        return null;
    }

    /// <summary>Where one statement of a command ends: a new line, ; &amp;&amp; || or a lone &amp; (not 2&gt;&amp;1 or &amp;&gt;).</summary>
    private static readonly Regex StatementBreak = new(@"\r?\n|&&|\|\||;|(?<![>&|])&(?![>&])", Opt);

    /// <summary>A statement that only prints, searches or records text: echo, printf, Write-Output / -Host, grep, rg,
    /// findstr, Select-String, a git commit / log / grep / show / diff / tag message, a gh issue or PR text.</summary>
    private static readonly Regex TextOnlyStart = new(
        @"^\s*(@?echo\b|printf\b|Write-(Output|Host|Information|Verbose|Warning)\b|e?grep\b|fgrep\b|rg\b|ag\b|ack\b|findstr(\.exe)?\b|Select-String\b|sls\b" +
        @"|git(\.exe)?\s+((?-i:-C)\s+(""[^""]*""|\S+)\s+)?(commit|log|grep|show|diff|tag|notes|blame)\b|gh\s+(pr|issue|release)\s+(create|comment|edit)\b)",
        Opt);

    /// <summary>…but that text goes on to run or into a file: piped anywhere (echo y | rm … runs the rm), redirected /
    /// written out (a script made and run in the same command line would never be read before it runs), or a part of it
    /// is run on the way: $( ), backticks, &lt;( ), a PowerShell ( ) expression, a pager or preprocessor flag
    /// (rg --pre, git grep -O, ack / ag --pager, git diff --ext-diff).</summary>
    private static readonly Regex HandsTextOn = new(
        @"[|>(`]|\btee\b|\bOut-File\b|\bSet-Content\b|\bAdd-Content\b" +
        @"|--pre\b|--pager\b|--open-files-in-pager\b|(?-i:\s-O)|--ext-diff\b",
        Opt);

    /// <summary>
    /// The finding for a command about to RUN, or null. Each statement is judged on its own: a delete in one and a lone /
    /// in another are not a delete of the root (2026-10-21: a multi-line script's clean-up and an awk division read as
    /// <c>rm -rf /</c>). A statement that only prints, searches or records the text runs nothing (owner 2026-10-28: tell
    /// code from a real run) — unless it pipes the text to a shell or writes it out.
    /// </summary>
    public static string? JudgeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        foreach (var statement in StatementBreak.Split(command))
        {
            if (JudgeLine(statement) is not { } why) continue;
            if (TextOnlyStart.IsMatch(statement) && !HandsTextOn.IsMatch(statement)) continue;
            return why;
        }
        return null;
    }

    /// <summary>Comments on a line of code: a closed /* … */, an unclosed /* to the end, a // to the end — each only where it
    /// starts outside a string (after a space, a bracket or ; , so "/*" and "C://x" in quotes stay).</summary>
    private static readonly Regex CodeComment = new(@"(?<=^|[\s{}();,])/\*.*?(\*/|$)|(?<=^|[\s{}();,])//.*$", Opt);

    /// <summary>Shell scripts: every line is a command, judged like <see cref="JudgeCommand"/>.</summary>
    private static readonly HashSet<string> ShellScripts = new(StringComparer.OrdinalIgnoreCase) { ".bat", ".cmd", ".ps1", ".psm1", ".sh", ".bash", ".zsh" };

    /// <summary>Languages whose comments start with // (and /* … */ blocks), and those whose comments start with #.</summary>
    private static readonly HashSet<string> SlashComments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".go", ".rs", ".swift", ".c",
        ".cpp", ".h", ".dart", ".php", ".gradle",
    };
    private static readonly HashSet<string> HashComments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".rb", ".pl", ".yml", ".yaml", ".toml", ".mk", ".ps1", ".psm1", ".sh", ".bash", ".zsh", ".php",
    };

    private static readonly Regex BatchComment = new(@"^(@?rem(\s|$)|::)", Opt);

    /// <summary>Code that only holds the text as data: a regex being built, text printed or logged, text handed to a check
    /// or an assertion (a test of what a guard decides).</summary>
    private static readonly Regex DataOnly = new(
        @"\bnew\s+Regex\s*\(|\bRegex\s+\w+\s*=\s*new\s*\(|\bRegex\.(IsMatch|Match|Matches|Replace|Split|Escape)\s*\(|\[GeneratedRegex\(|\bre\.(compile|search|match|fullmatch|findall|finditer|sub|split)\s*\(" +
        @"|\bnew\s+RegExp\s*\(|\bPattern\.compile\s*\(|\bregexp\.(MustCompile|Compile|MatchString)\s*\(|\bRegex::new\s*\(|\bpreg_(match|replace|split)\s*\(" +
        @"|\bConsole\.(Error\.)?Write(Line)?\s*\(|\b(Debug|Trace)\.(Write|WriteLine|Print)\s*\(|\bprint\s*\(|\bconsole\.(log|error|warn|info)\s*\(|\bprintln!?\s*\(" +
        @"|\bfmt\.(Print|Println|Printf|Sprintf)\s*\(|\b(_?logger|_?log)\.\w+\s*\(|\bputs\b" +
        @"|\b(Assert(\.\w+)?|Expect|expect|Check|Should\w*|JudgeLine|JudgeCommand|ScanText|CheckCode|CheckScriptFile|FindSerious|Decide)\s*\(|\bassert\b|\b(it|test|describe)\s*\(\s*[""'`]",
        Opt);

    /// <summary>A line that starts a process or hands text to a shell or an agent tool that runs it: text on it may really
    /// run, so it is judged even inside a check or a print.</summary>
    private static readonly Regex LaunchSink = new(
        @"Process\.Start|ProcessStartInfo|Start-Process|Invoke-Expression|\biex\b|\bsubprocess\b|\bos\.(system|popen|exec\w*|spawn\w*)\b|\bexec(Sync|File|FileSync)?\s*\(" +
        @"|\bspawn(Sync)?\s*\(|child_process|Runtime\.getRuntime|ShellExecute|CreateProcess|\bshell_exec\b|\bpassthru\b|\bproc_open\b|\bpopen\b|\bsystem\s*\(" +
        @"|\bShell\s*\(|WScript\.Shell|\bcmd(\.exe)?\b|\bpowershell(\.exe)?\b|\bpwsh\b|\bbash\b|\bsh\s+-c\b|/bin/sh|desktop_launch|desktop_type|run_command" +
        @"|bg_start|run_build|run_tests|\brepl\b|ExecuteAsync\s*\(|RunAsync\s*\(|\bLaunch\s*\(",
        Opt);

    /// <summary>A delete API called on the line (Directory.Delete(, rmtree(, fs.rm( …): a print or a check beside it does not
    /// make it data — print("bye"); shutil.rmtree(home) still deletes.</summary>
    private static readonly Regex DeleteApiCall = new(
        @"\b(rmtree|rmSync|rmdirSync|Directory\.Delete|DeleteDirectory|RemoveAll|remove_dir_all|rimraf(\.sync)?)\s*\(|\bfs(\.promises)?\.(rm|rmdir)\s*\(",
        Opt);

    /// <summary>
    /// The lines of <paramref name="text"/> that would really delete what the red prompt is for. How a line is read follows
    /// the file's kind (its extension in <paramref name="fileLabel"/>):
    /// <list type="bullet">
    /// <item>a shell script (.bat .cmd .ps1 .sh …): every line but a comment is a command (<see cref="JudgeCommand"/>);</item>
    /// <item>other code: comments are skipped, and so is a line that only holds the text as data (a regex, a print, a log,
    /// a check or an assertion) — unless the same line starts a process (<see cref="LaunchSink"/>) or calls a delete API;</item>
    /// <item>no extension (a code block, typed text): every line is a command.</item>
    /// </list>
    /// </summary>
    public static List<DestructiveFinding> ScanText(string text, string fileLabel)
    {
        var found = new List<DestructiveFinding>();
        string ext = Path.GetExtension(fileLabel.TrimEnd(')'));
        bool script = ShellScripts.Contains(ext), code = !script && CodeExtensions.Contains(ext);
        bool slash = SlashComments.Contains(ext), hash = HashComments.Contains(ext);
        bool batch = ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        bool ps = ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase) || ext.Equals(".psm1", StringComparison.OrdinalIgnoreCase);
        string? closer = null;   // the end of the block comment the scan is inside
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            string t = raw.Trim();
            if (closer != null)
            {
                int end = t.IndexOf(closer, StringComparison.Ordinal);
                if (end < 0) continue;
                t = t[(end + closer.Length)..].Trim();
                closer = null;
            }
            if ((slash && t.StartsWith("/*")) || (ps && t.StartsWith("<#")))
            {
                string close = t.StartsWith("/*") ? "*/" : "#>";
                int end = t.IndexOf(close, 2, StringComparison.Ordinal);
                if (end < 0) { closer = close; continue; }
                t = t[(end + close.Length)..].Trim();
            }
            if (t.Length == 0 || (slash && t.StartsWith("//")) || (hash && t.StartsWith('#')) || (batch && BatchComment.IsMatch(t))) continue;

            string? why;
            if (script || !code) why = JudgeCommand(t);
            else if (DataOnly.IsMatch(t) && !LaunchSink.IsMatch(t) && !DeleteApiCall.IsMatch(t)) why = null;
            else why = JudgeLine(t);
            if (why != null)
            {
                string shown = raw.Trim();
                found.Add(new DestructiveFinding(fileLabel, n, shown.Length > 160 ? shown[..160] + "…" : shown, why));
                if (found.Count >= 50) break;
            }
        }
        return found;
    }

    private const string ScriptExt = "ps1|psm1|bat|cmd|sh|bash|zsh|py|pyw|js|mjs|cjs|ts|rb|pl|php|lua|vbs";

    /// <summary>A script path in a command line: quoted, or a bare token ending in a script extension.</summary>
    private static readonly Regex ScriptToken = new(
        @"""(?<path>[^""]+\.(" + ScriptExt + @"))""|'(?<path>[^']+\.(" + ScriptExt + @"))'|(?<path>[^\s""'|&;<>()]+\.(" + ScriptExt + @"))\b",
        Opt);

    /// <summary>
    /// The scripts a command starts by name (powershell -File x.ps1, bash x.sh, python x.py, cmd /c x.bat, ./x.sh …), read
    /// before they run wherever they are: a script written outside the project is not among the project's changed files,
    /// and writing it no longer asks (owner 2026-10-28: writing code is not running it). Bounded: 20 files, 1 MB each.
    /// </summary>
    public static List<DestructiveFinding> ScanScriptsNamed(string? command, string? cwd)
    {
        var found = new List<DestructiveFinding>();
        if (string.IsNullOrWhiteSpace(command)) return found;
        int files = 0;
        foreach (Match m in ScriptToken.Matches(command))
        {
            if (++files > 20) break;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(m.Groups["path"].Value);
                if (path.StartsWith("~/") || path.StartsWith("~\\"))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
                if (path.Length > 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/') path = path[1] + ":" + path[2..];   // Git Bash /c/x
                if (!Path.IsPathRooted(path))
                {
                    if (string.IsNullOrWhiteSpace(cwd)) continue;
                    path = Path.Combine(cwd, path);
                }
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 1_000_000) continue;
                found.AddRange(ScanText(File.ReadAllText(info.FullName), info.Name));
                if (found.Count >= 50) break;
            }
            catch { /* not a readable path: nothing to judge */ }
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
        @"(^|[;&|\s(])(dotnet\s+(run|test|exec|build|watch|msbuild|vstest)|npm\s+(run|test|start|exec|t)\b|npx\b|pnpm\b|yarn\b|bun\b|node\b|deno\b|tsx\b|ts-node\b|python3?\b|py\b|pytest\b|cargo\s+(run|test|build)|go\s+(run|test|build)|java\b|gradle\w*\b|mvn\b|make\b|cmake\b|msbuild\b|ruby\b|php\b|perl\b|bash\s+\S+\.sh|sh\s+\S+\.sh|powershell(\.exe)?\s+(-\w+\s+)*-File|pwsh(\.exe)?\s+(-\w+\s+)*-File|cmd(\.exe)?\s+/c\s+\S+\.(bat|cmd)|\S+\.(bat|cmd|ps1|sh|exe)\b)" +
        // ./program where a program starts (not ./build or ./* as the argument of rm, cp …)
        @"|(^|[;&|(]|\b(sudo|nohup|time|exec|env|then|do|sh|bash|zsh|source)\s|\s-c\s)\s*\./[^\s*?]+",
        Opt);

    /// <summary>Files something runs by itself, with no command naming them: git hooks and git's config (a pager, an
    /// alias), shell start-up files, PowerShell profiles, the Startup folder.</summary>
    private static readonly Regex AutoRunPath = new(
        @"[\\/]\.git[\\/](hooks[\\/]|config$)|[\\/]\.husky[\\/]|[\\/]\.gitconfig$" +
        @"|[\\/]\.(bashrc|bash_profile|bash_login|bash_logout|profile|zshrc|zprofile|zshenv|zlogin)$" +
        @"|[\\/](Microsoft\.PowerShell_profile|Microsoft\.VSCode_profile|profile)\.ps1$|[\\/]Start Menu[\\/]Programs[\\/]Startup[\\/]",
        Opt);

    /// <summary>
    /// Writing code is not running it (owner 2026-10-28) — except into a file that runs by itself (<see cref="AutoRunPath"/>):
    /// a git hook runs on the next commit, a .bashrc or a PowerShell profile on the next shell, and no command names
    /// them, so they are read as they are written. Empty = nothing found, or not such a file.
    /// </summary>
    public static List<DestructiveFinding> ScanAutoRunWrite(string? file, string? text)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(text) || !AutoRunPath.IsMatch(file.Trim())) return new();
        return ScanText(text, Path.GetFileName(file.Trim()));
    }

    /// <summary>The command runs code someone may just have written (a test, a build, a script) — the moment to scan.</summary>
    public static bool RunsProjectCode(string command) => RunsCode.IsMatch(command ?? "");

    /// <summary>The message an agent (or the user) sees when a run is refused.</summary>
    public static string BlockMessage(IReadOnlyList<DestructiveFinding> findings) =>
        "CluadeX safety: refused to run this — code it would run would wipe a whole drive, delete the root or a home folder, delete Windows' backups or wipe a database:\n"
        + string.Join("\n", findings.Take(10).Select(f => "  " + f))
        + (findings.Count > 10 ? $"\n  … and {findings.Count - 10} more" : "")
        + "\nRemove these lines (a test of a safety check must use an inert sentinel, never a real target) and run again. "
        + "Only the user may run this code as it is, by hand.";
}
