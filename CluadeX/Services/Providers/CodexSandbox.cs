using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Providers;

/// <summary>
/// What Codex's Windows sandbox takes away from the commands it runs, and how an OpenAI Dev turn works around it.
/// The installed Codex (0.167, <c>[windows] sandbox = "elevated"</c>) runs every shell command as a separate restricted
/// account (<c>CodexSandboxOnline</c> / <c>CodexSandboxOffline</c>) with no profile of its own. The whole list was probed
/// with `codex sandbox` on this machine (2026-11-09, again in full 2026-11-11) and is <see cref="Limits"/>: each entry is
/// the failure's fingerprint, its notice and the wrapper's sentence. What could be fixed by environment is fixed in
/// <see cref="CodexSandboxEnv"/> (git HTTPS, NuGet, Gradle, pip, UTF-8); what cannot is said, so a sandbox refusal is
/// never taken for a bug in the code. %TEMP% and the project stay writable, PATH is inherited, Node's TLS works.
/// The sandbox stays (it is what keeps a Codex turn inside the project): builds and tests that need the user go through
/// CluadeX's own verify_change, which runs as the user under the user's approval rules.
/// </summary>
public static class CodexSandbox
{
    /// <summary>
    /// One thing the sandbox takes away. <see cref="Pattern"/>: what a failed command prints when this is why it failed
    /// (null = said in the prompt only); its notice is the localization key <c>codex.sandbox.why.&lt;Id&gt;</c>.
    /// <see cref="Prompt"/>: the wrapper's sentence about it (the argument: the turn has CluadeX's chat tools).
    /// Every limit lives in <see cref="Limits"/> alone, so the notice and the prompt cannot drift apart, and apitest
    /// checks each has both (2026-11-11: the fixes before this were made one symptom at a time, in two places).
    /// </summary>
    public sealed record Limit(string Id, Regex? Pattern, Func<bool, string> Prompt);

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The sandbox's limits, most specific first (the first match names a failure).</summary>
    public static readonly IReadOnlyList<Limit> Limits = new Limit[]
    {
        new("nuget", R(@"Failed to read NuGet\.Config due to unauthorized access|NuGet\.Config'? is denied"),
            _ => "NuGet: CluadeX points the sandbox's APPDATA at a home of its own with a copy of the user's NuGet.Config, and "
                 + "the user's downloaded packages are a read-only fallback, so `dotnet restore`, `build`, `test` and `run` work "
                 + "for the packages the user has; \"Failed to read NuGet.Config due to unauthorized access\" means a command "
                 + "ran with another APPDATA."),
        new("nugetNet", R(@"(?-i:\berror (?:NU1301\b|:[^\r\n]{0,80}Unable to load the service index))|Unable to find a resolved package"),
            tools => "A package not downloaded yet cannot download in the sandbox (.NET's HTTPS needs a profile it lacks: "
                     + "\"Unable to load the service index\", NU1301) — "
                     + (tools ? "run verify_change once, which restores as the user, then build in the sandbox again."
                              : "the user has to restore it once.")),
        new("dpapi", R(@"user profile loaded for the current thread's user context|when the thread is impersonating"),
            _ => "It does not have the user's DPAPI keys (\"user profile not loaded\" / \"impersonating\"): the user's "
                 + "encrypted settings and tokens do not open there."),
        new("wmi", R(@"(?:Win32_Process|CimMethod|CimInstance|WmiObject|ManagementException|\bWMI\b)[^\r\n]{0,200}Access (?:is )?denied|Access (?:is )?denied[^\r\n]{0,200}(?:Win32_Process|CimMethod|CimInstance|WmiObject|\bWMI\b)|^ERROR: Access denied\.?\s*$"),
            _ => "It cannot use WMI (Get-CimInstance, Invoke-CimMethod) or tasklist (\"Access denied\"): list processes "
                 + "with PowerShell `Get-Process`."),
        new("tls", R(@"SEC_E_NO_CREDENTIALS|schannel: AcquireCredentialsHandle failed"),
            _ => "Windows' own TLS needs a user profile the sandbox lacks, so HTTPS from curl.exe (SEC_E_NO_CREDENTIALS) and "
                 + "PowerShell Invoke-WebRequest fails there: to call a URL, use Node — write the script to a .js file and run "
                 + "`node file.js` (fetch works) — never conclude a server is down from curl or Invoke-WebRequest failing in the "
                 + "sandbox. git uses OpenSSL there (CluadeX sets it), so `git ls-remote` and `git clone` over https work."),
        new("git", R(@"\.git[\\/][^'""\r\n]*'?:? Permission denied|Access to the path '[^']*\\\.git\\[^']*' is denied"),
            _ => "Codex keeps `.git` read-only in its sandbox: git status, diff, log and ls-remote work, but nothing that "
                 + "writes the repository does (add, commit, checkout -b, stash, fetch — \".git/index.lock': Permission "
                 + "denied\"). Never delete lock files or retry it another way: leave the commit to the user and say which "
                 + "files changed."),
        new("python", R(@"(?:python|pip|py)[\w.]*'? failed to run: (?:The file cannot be accessed by the system|Access is denied)|WindowsApps\\[^\r\n]{0,200}(?:Access is denied|cannot be accessed)"),
            _ => "`python`/`pip` from the Microsoft Store (WindowsApps) cannot start there (\"The file cannot be accessed by "
                 + "the system\"): use `py` (the Python launcher), and `py -m pip` for pip."),
        new("ssh", R(@"\.ssh[\\/][^\r\n]{0,120}(?:Permission denied|Access is denied)|Access is denied[^\r\n]{0,120}\.ssh\b"),
            _ => "It cannot read ~/.ssh, so ssh, scp and git over ssh fail there: for the user's servers use the brain's "
                 + "ssh_profiles_list / ssh_tail tools when you have them, or ask the user."),
        new("symlink", R(@"Administrator privilege required for this operation|A required privilege is not held by the client|You do not have sufficient privilege to perform this operation"),
            _ => "It cannot make symbolic links (\"Administrator privilege required\"): use a directory junction "
                 + "(`cmd /c mklink /J link target`) or a copy."),
        new("adb", R(@"Cannot mkdir '\\?\.android'"),
            _ => "`adb` cannot run in the sandbox at all (\"Cannot mkdir '\\.android'\": the account has no profile "
                 + "folder): ask the user (ask_user) to run the adb command and paste its output."),
        // With CluadeX's APPDATA (CodexSandboxEnv) gh finds an empty config instead of a denied one: "not logged into any".
        new("gh", R(@"GitHub CLI\\config\.yml[^\r\n]{0,120}(?:denied|Access is denied)|failed to read configuration:[^\r\n]{0,200}GitHub CLI|You are not logged into any GitHub hosts"),
            tools => "`gh` cannot read the user's GitHub login there"
                     + (tools ? ": use the gh_pr_view, gh_pr_checks and gh_run_log tools (MCP server cluadex)." : ".")),
        new("registry", null,
            _ => "HKCU there is the sandbox account's own hive, not the user's: registry reads show nothing of the user's "
                 + "and writes are denied."),
    };

    private static readonly Regex PathDenied = new(
        @"Access to the path '(?<path>[^']+)' is denied", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The localization key of the reason a failed command's output shows the sandbox's hand, or null when it does not.
    /// <paramref name="userProfile"/>: the user's profile folder (default: this process's); <paramref name="cwd"/>: the
    /// turn's folder — a denied path inside it is not the sandbox's profile rule. Pure apart from the default profile.
    /// </summary>
    public static string? CauseKey(string? output, string? cwd = null, string? userProfile = null)
    {
        if (string.IsNullOrEmpty(output)) return null;
        foreach (var limit in Limits)
            if (limit.Pattern?.IsMatch(output) == true) return "codex.sandbox.why." + limit.Id;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return null;
        string profile = Norm(userProfile);
        string? project = string.IsNullOrEmpty(cwd) ? null : Norm(cwd);
        foreach (Match m in PathDenied.Matches(output))
        {
            string path = Norm(m.Groups["path"].Value);
            if (!Under(path, profile)) continue;
            if (project != null && Under(path, project)) continue;
            return "codex.sandbox.why.profile";
        }
        return null;
    }

    private static string Norm(string p)
    {
        try { p = Path.GetFullPath(p.Trim().Trim('"')); } catch { }
        return p.Replace('/', '\\').TrimEnd('\\');
    }

    private static bool Under(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The note a failed command's row gets when <see cref="CauseKey"/> found the sandbox's hand; null otherwise.</summary>
    public static string? Notice(string? output, string? cwd = null, string? userProfile = null)
    {
        if (CauseKey(output, cwd, userProfile) is not { } key) return null;
        var loc = LocalizedResources.Instance;
        return string.Format(loc["codex.sandbox.blocked"], loc[key]);
    }

    /// <summary>
    /// The folder of a ripgrep Codex ships, for the turn's PATH: Codex's own tools call it, but its shell finds no `rg`
    /// (every first search of the 2026-11-09 chat was CommandNotFound). Looked for next to codex.exe, in npm's
    /// vendor\&lt;triple&gt;\path, and in the Codex app's bin\&lt;build&gt; folders (newest first). Null = none found.
    /// </summary>
    public static string? FindRipgrepDir(string? codexExe, string? localAppData = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(codexExe) && Path.GetDirectoryName(codexExe) is { Length: > 0 } dir)
        {
            candidates.Add(dir);
            if (Path.GetDirectoryName(dir) is { Length: > 0 } parent) candidates.Add(Path.Combine(parent, "path"));
        }
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string bin = string.IsNullOrEmpty(localAppData) ? "" : Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            if (bin.Length > 0 && Directory.Exists(bin))
                candidates.AddRange(new DirectoryInfo(bin).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc).Select(d => d.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "rg.exe")));
    }

    /// <summary>Is a program on this PATH? Pure apart from the file checks.</summary>
    public static bool OnPath(string exeName, string? path)
    {
        foreach (var part in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(part.Trim().Trim('"'), exeName))) return true; }
            catch (ArgumentException) { }
        }
        return false;
    }

    /// <summary>PATH with <paramref name="dir"/> in front, once.</summary>
    public static string PrependPath(string? path, string dir)
    {
        var parts = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Any(p => string.Equals(p.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return path ?? "";
        return dir + Path.PathSeparator + (path ?? "");
    }

    /// <summary>
    /// The wrapper's lines about the sandbox for a turn that may run commands. <paramref name="chatTools"/>: the turn has
    /// CluadeX's MCP server (verify_change). <paramref name="hasRipgrep"/>: `rg` is on the turn's PATH.
    /// <paramref name="sandboxHome"/>: the home <see cref="CodexSandboxEnv"/> gave the turn's shell, or null. Pure.
    /// </summary>
    public static string PromptSection(bool chatTools, bool hasRipgrep, string? sandboxHome = null)
    {
        string s = "Your shell commands run inside Codex's Windows sandbox, as a separate restricted Windows account with no "
            + "profile of its own, not as the user. It can write only in the project (and the folders the user added) and in "
            + "%TEMP%"
            + (sandboxHome != null ? $"; CluadeX gives it a home of its own for caches at {sandboxHome} (APPDATA, NuGet, Gradle, pip)" : "")
            + ". It cannot read or write the user's profile folders (AppData, ~/.nuget, ~/.gradle, ~/.ssh: \"Access to the "
            + "path 'C:\\Users\\…' is denied\"). A command that fails for one of the reasons below failed because of the "
            + "sandbox, not because of the code: never change code for it or count it as a test failure; tell the user "
            + "plainly that the sandbox blocked it. "
            + "A command that fails with \"setup refresh had errors\" never ran: the sandbox itself could not start, usually because "
            + "a `.git` was made inside the sandbox (`git init` there makes it belong to the sandbox account). CluadeX rebuilds such "
            + "a `.git` as the user's as soon as that failure shows, so run the command once more; if it fails the same way again, "
            + "stop and tell the user rather than looking for another way to run it (desktop tools, terminals). ";
        foreach (var limit in Limits) s += limit.Prompt(chatTools) + " ";
        s += chatTools
            ? "Builds and tests that need the user (a new package, the user's DPAPI keys, WMI, a harness that reads the user's "
              + "AppData) go through CluadeX's verify_change tool (MCP server cluadex): it runs as the user, outside the "
              + "sandbox, under the user's own approval rules. Pass test_command for a project's own harness; for a run that "
              + "may take longer than 5 minutes pass timeout_minutes, or tests_background=\"true\" to have it run in the "
              + "background and report in a later turn (then end your turn and wait; do not poll). "
            : "A build or test that needs the user's profile has to be run by the user. ";
        s += hasRipgrep
            ? "To search, `rg` is on PATH (Codex's own copy); `git grep -n` and PowerShell `Select-String` work too. On Windows "
              + "rg does not expand a wildcard in a path (`rg x src/*.md` fails with os error 123): pass the folder and a glob, "
              + "`rg x src -g \"*.md\"`."
            : "`rg` is not installed here: search with `git grep -n`, or PowerShell `Get-ChildItem -Recurse | Select-String`.";
        return s;
    }
}
