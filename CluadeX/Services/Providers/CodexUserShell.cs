using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Providers;

/// <summary>
/// run_as_user — the few commands Codex's Windows sandbox cannot run, run by CluadeX as the user instead (owner 2026-11-11:
/// "ควรทำไหม" → "ทำเลย", after the sandbox survey found Codex cannot commit, open a PR, run adb, restore a new package or
/// fetch over https). Only for an OpenAI Dev turn with the run grant; one plain command per call from the list below; it
/// then goes through the same gate as CluadeX's own run_command (the user's rules, the permission card, the red danger
/// card, strict approval), so Codex gets no more reach than the other CLIs already have. Everything else stays in the
/// sandbox. Pure.
/// </summary>
public static class CodexUserShell
{
    public const string ToolName = "run_as_user";

    /// <summary>The programs, and what each may do.</summary>
    public static readonly IReadOnlyList<string> Programs = new[] { "git", "gh", "adb", "dotnet", "curl" };

    // git: writes to the repository and talks to its remote. Not what throws work away (checkout -- / restore / reset /
    // clean / stash drop / rm) — GuardHook.NeverRun refuses force pushes, reset --hard and clean on top.
    private static readonly HashSet<string> GitVerbs = new(StringComparer.Ordinal)
    {
        "add", "commit", "push", "pull", "fetch", "status", "log", "diff", "show", "branch", "switch", "tag", "remote",
        "merge", "rev-parse", "ls-remote", "ls-files", "describe", "config",
    };
    // gh: pull requests, issues, CI runs and reading — not auth, secrets, or deleting repositories / releases.
    private static readonly HashSet<string> GhVerbs = new(StringComparer.Ordinal) { "pr", "issue", "run", "workflow", "repo", "release", "api", "browse", "search", "status", "label" };
    // dotnet: only what needs the network (the sandbox builds and tests fine).
    private static readonly HashSet<string> DotnetVerbs = new(StringComparer.Ordinal) { "restore", "add", "nuget", "tool", "workload", "list" };

    /// <summary>Null = the command may go to the gate; otherwise why it is refused (what the model reads).</summary>
    public static string? Refusal(string? command)
    {
        string c = (command ?? "").Trim();
        if (c.Length == 0) return "run_as_user needs command: one git, gh, adb, dotnet or curl command.";
        if (c.Length > 2000) return "run_as_user: that command is too long — run one plain command.";
        // One command: no chaining, piping, redirection or substitution (each part would be a command nobody checked).
        // ; & | < > are text inside quotes (a commit message); $( ${ and ` expand even inside PowerShell's double quotes.
        string unquoted = Regex.Replace(c, @"""[^""]*""|'[^']*'", "\"\"");
        // ( ) { } outside quotes: PowerShell runs a (sub-expression) or {script block} in an argument.
        if (Regex.IsMatch(c, @"[`\r\n]|\$\(|\$\{") || Regex.IsMatch(unquoted, @"[;&|<>(){}]") || c.Count(ch => ch == '"') % 2 == 1)
            return "run_as_user runs ONE plain command: no ; && || | > < ` $( — split it into separate calls, and do the rest in "
                 + "the sandbox.";
        var words = Words(c);
        string prog = Regex.Replace(Path.GetFileName(words[0]).ToLowerInvariant(), @"\.exe$", "");
        if (!Programs.Contains(prog))
            return $"run_as_user runs only {string.Join(", ", Programs)} — '{words[0]}' runs in your sandbox as usual.";
        if (Safety.GuardHook.NeverRun(c) is { } never) return never;
        var rest = words.Skip(1).ToList();
        switch (prog)
        {
            case "git":
            {
                // A config value or helper names a program git then runs (core.fsmonitor, core.sshCommand, --upload-pack…):
                // that would be any command at all, so none of them.
                // (-c only before the verb: `switch -c` / `commit -c` after it are ordinary options.)
                var lead = rest.TakeWhile(a => a.StartsWith('-')).ToList();
                if (lead.Any(a => a.StartsWith("-c") || a.StartsWith("--config-env") || a.StartsWith("--exec-path"))
                    || rest.Any(a => a.StartsWith("--upload-pack") || a.StartsWith("--receive-pack") || a.StartsWith("--exec=")
                                  // ls-remote's -u is --upload-pack (push's -u is --set-upstream: fine)
                                  || rest.Contains("ls-remote") && a.StartsWith("-u") && !a.StartsWith("--")))
                    return "run_as_user does not run git with -c, --config-env, --exec-path, --upload-pack or --receive-pack "
                         + "(they name a program for git to run): run the plain git command.";
                // -C <dir> / --no-pager before the verb.
                int i = 0;
                while (i < rest.Count && rest[i].StartsWith('-'))
                    i += rest[i] is "-C" ? 2 : 1;
                string verb = i < rest.Count ? rest[i] : "";
                if (!GitVerbs.Contains(verb))
                    return $"run_as_user does not run `git {verb}` — it runs git {string.Join(", ", GitVerbs.OrderBy(v => v))}. "
                         + "Nothing that throws work away (checkout --, restore, reset, clean, stash, rm): ask the user for that.";
                var args = rest.Skip(i + 1).ToList();
                if (verb == "branch" && args.Any(a => a is "-D" or "--force" or "-f" || a.StartsWith("-D")))
                    return "run_as_user does not force-delete or force-move a branch (-D, -f): ask the user.";
                if (verb == "switch" && args.Any(a => a is "-f" or "--force" or "--discard-changes" or "-C"))
                    return "run_as_user does not switch branches over uncommitted changes (-f, --discard-changes, -C): ask the user.";
                if (verb == "tag" && args.Any(a => a is "-d" or "--delete" or "-f" or "--force"))
                    return "run_as_user does not delete or move tags: ask the user.";
                if (verb == "config" && !args.Any(a => a is "--get" or "--list" or "-l" or "--get-all" or "--show-origin"))
                    return "run_as_user only reads git config (--get, --list): ask the user to change it.";
                if (verb == "remote" && args.Count > 0 && args[0] is not ("-v" or "show" or "get-url"))
                    return "run_as_user only shows remotes (remote -v, show, get-url): ask the user to change them.";
                return null;
            }
            case "gh":
            {
                string verb = rest.FirstOrDefault() ?? "";
                if (!GhVerbs.Contains(verb))
                    return $"run_as_user does not run `gh {verb}` (never auth, secret, ssh-key, gpg-key, extension): it runs gh "
                         + string.Join(", ", GhVerbs.OrderBy(v => v)) + ".";
                string sub = rest.Count > 1 ? rest[1] : "";
                if (verb is "repo" or "release" && sub is "delete" or "archive" or "rename" or "edit" or "transfer")
                    return $"run_as_user does not run `gh {verb} {sub}`: ask the user.";
                if (verb == "api" && rest.Skip(1).Any(a => Regex.IsMatch(a, @"^(-X|--method)$|^(-X|--method=)(?!GET)", RegexOptions.IgnoreCase)
                                                        || a is "-f" or "-F" or "--field" or "--raw-field" or "--input"))
                    return "run_as_user runs `gh api` for reading only (GET, no fields): use the gh command for the change, or ask the user.";
                return null;
            }
            case "dotnet":
            {
                string verb = rest.FirstOrDefault() ?? "";
                if (!DotnetVerbs.Contains(verb))
                    return $"run_as_user runs dotnet {string.Join(", ", DotnetVerbs.OrderBy(v => v))} (what needs the network) — "
                         + "build, test and run in your sandbox (or verify_change).";
                if (verb == "add" && !rest.Skip(1).Any(a => a.Equals("package", StringComparison.OrdinalIgnoreCase)))
                    return "run_as_user runs `dotnet add <project> package <name>` only.";
                if (verb == "nuget" && (rest.Count < 2 || rest[1] is not ("locals" or "why" or "search" or "list")))
                    return "run_as_user does not change NuGet's sources or push packages: ask the user.";
                return null;
            }
            case "curl":
            {
                // Reading a URL: the answer comes back as the tool's output. Nothing written, nothing sent.
                foreach (var a in rest)
                {
                    string l = a.ToLowerInvariant();
                    // Short flags are case-sensitive (-f is --fail, -F is a form upload) and bundle (-sSo file).
                    bool shortWrite = Regex.IsMatch(a, @"^-[A-Za-z]+") && !a.StartsWith("-X") && a[1..].TakeWhile(char.IsLetter).Any(ch => "oOdFTK".Contains(ch));
                    if (shortWrite || l is "--output" or "--remote-name" or "--remote-name-all" or "--upload-file" or "--config" or "--output-dir"
                        || l.StartsWith("--output") || l.StartsWith("--data") || l.StartsWith("--form") || l.StartsWith("--json")
                        || l.StartsWith("--upload-file") || l.StartsWith("--config"))
                        return "run_as_user runs curl to READ a URL only (its body comes back here): no -o, -d, -F, -T, -K.";
                    if (Regex.IsMatch(a, @"^(-X|--request)(=|$)") || Regex.IsMatch(a, @"^-X[A-Za-z]"))
                    {
                        int at = rest.IndexOf(a);
                        string method = a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : a.Length > 2 && a.StartsWith("-X") ? a[2..] : at + 1 < rest.Count ? rest[at + 1] : "";
                        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                            return "run_as_user runs curl with GET or HEAD only.";
                    }
                }
                return null;
            }
            default:   // adb: the permission card shows the command; the red card catches what deletes
                return null;
        }
    }

    /// <summary>The command split into words, quotes kept together (and removed). Pure.</summary>
    public static List<string> Words(string c)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(c, @"""([^""]*)""|'([^']*)'|(\S+)"))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return list.Count == 0 ? new List<string> { "" } : list;
    }

    /// <summary>The tool's description (the MCP tools/list entry).</summary>
    public const string Description =
        "Run ONE command as the user, outside your Windows sandbox, for what the sandbox cannot do: git add / commit / push / "
        + "pull / fetch / switch / branch / merge (the sandbox keeps .git read-only), gh (pull requests, issues, CI runs), adb, "
        + "dotnet restore / add package (a package not downloaded yet), and curl to read an https URL. CluadeX puts it through "
        + "the user's approval rules and cards first, exactly as its own commands, and runs it in the project folder; the "
        + "output comes back here. One plain command per call — no ; && | > or $( ). Everything else (building, testing, "
        + "editing, other programs) stays in your sandbox.";

    /// <summary>The tool's input schema.</summary>
    public static object InputSchema => new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "One git, gh, adb, dotnet or curl command, e.g. git commit -m \"Fix the parser\"" },
        },
        required = new[] { "command" },
    };
}
