using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Providers;

/// <summary>
/// What Codex's Windows sandbox takes away from the commands it runs, and how an OpenAI Dev turn works around it.
/// The installed Codex (0.167, <c>[windows] sandbox = "elevated"</c>) runs every shell command as a separate restricted
/// account (<c>CodexSandboxOffline</c>), checked on this machine 2026-11-09 with <c>codex sandbox</c>: it cannot read
/// %APPDATA%\NuGet\NuGet.Config, cannot write %APPDATA%, %LOCALAPPDATA% or ~/.nuget/packages, has DPAPI keys of its own
/// (not the user's, so the user's encrypted blobs do not open), cannot create processes through WMI, and has no
/// <c>rg</c> on PATH; %TEMP% and the project stay writable, and PATH is inherited. In the BrainX chat of 2026-11-09 this
/// made `dotnet build` fail at restore and a test harness report 68 false failures, which the model read as code bugs.
/// The sandbox stays (it is what keeps a Codex turn inside the project): builds and tests go through CluadeX's own
/// verify_change, which runs as the user under the user's approval rules, and a failure that is the sandbox's is said so.
/// </summary>
public static class CodexSandbox
{
    private static readonly Regex NuGetDenied = new(
        @"Failed to read NuGet\.Config due to unauthorized access|NuGet\.Config'? is denied",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DpapiProfile = new(
        @"user profile loaded for the current thread's user context|when the thread is impersonating",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Wmi = new(
        @"(?:Win32_Process|CimMethod|WmiObject|ManagementException|\bWMI\b)[^\r\n]{0,200}Access (?:is )?denied|Access (?:is )?denied[^\r\n]{0,200}(?:Win32_Process|CimMethod|WmiObject|\bWMI\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        if (NuGetDenied.IsMatch(output)) return "codex.sandbox.why.nuget";
        if (DpapiProfile.IsMatch(output)) return "codex.sandbox.why.dpapi";
        if (Wmi.IsMatch(output)) return "codex.sandbox.why.wmi";
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
    /// CluadeX's MCP server (verify_change). <paramref name="hasRipgrep"/>: `rg` is on the turn's PATH. Pure.
    /// </summary>
    public static string PromptSection(bool chatTools, bool hasRipgrep)
    {
        string s = "Your shell commands run inside Codex's Windows sandbox, as a separate restricted Windows account: it cannot read "
            + "the user's AppData (%APPDATA%\\NuGet\\NuGet.Config among it), cannot write the user's profile folders (AppData, "
            + "~/.nuget/packages), does not have the user's DPAPI keys, and cannot create processes through WMI. A command that "
            + "fails for one of those reasons (NuGet.Config unauthorized access, \"Access to the path 'C:\\Users\\…' is denied\", "
            + "DPAPI \"user profile not loaded\" / \"impersonating\", WMI \"Access denied\") failed because of the sandbox, not "
            + "because of the code: never fix code for it or count it as a test failure; tell the user plainly that the sandbox "
            + "blocked it. ";
        s += chatTools
            ? "Build and test with CluadeX's verify_change tool (MCP server cluadex): it runs as the user, outside the sandbox, under "
              + "the user's own approval rules, so restore, DPAPI and WMI work there. Pass test_command for a project's own harness; "
              + "for a run that may take longer than 5 minutes pass timeout_minutes, or tests_background=\"true\" to have it run in "
              + "the background and report in a later turn (then end your turn and wait; do not poll). "
              + "Inside the sandbox, `dotnet build --no-restore` works when the packages were restored before. "
            : "Inside the sandbox, `dotnet build --no-restore` works when the packages were restored before; a restore or a test "
              + "that needs the user's profile has to be run by the user. ";
        s += hasRipgrep
            ? "To search, `rg` is on PATH (Codex's own copy); `git grep -n` and PowerShell `Select-String` work too."
            : "`rg` is not installed here: search with `git grep -n`, or PowerShell `Get-ChildItem -Recurse | Select-String`.";
        return s;
    }
}
