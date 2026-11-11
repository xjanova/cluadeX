using System.Text.RegularExpressions;

namespace CluadeX.Services.Safety;

/// <summary>Where a hard-to-undo command would act — the parts of strict approval the user can switch on one by one
/// (Features ▸ Strict approval).</summary>
[Flags]
public enum StrictScope
{
    None = 0,
    /// <summary>A command on this PC: deletes, prune, DROP / TRUNCATE, format, registry, services, deploys.</summary>
    Local = 1,
    /// <summary>A command on another machine: BrainX's ssh_run, or <c>ssh host …</c> from a local shell.</summary>
    Remote = 2,
    /// <summary>Git that throws work away: force push, reset --hard, clean -f, branch -D, push --delete …</summary>
    Git = 4,
    All = Local | Remote | Git,
}

/// <summary>One hard-to-undo statement: which scope it falls in, what kind of change, the words that matched, and the
/// remote host when it runs elsewhere.</summary>
public sealed record HardToUndoFinding(StrictScope Scope, string Kind, string Matched, string? Host = null);

/// <summary>
/// Strict approval's classifier (owner 2026-11-11: "ระบบการขอนุมัติแบบนี้ cluadex มีไหม ฉันอยากให้มีแต่เลือกได้ว่าจะเปิด
/// มันหรือไม่"): is a command hard to undo? Wider than <see cref="DestructiveCodeScan"/>, which only flags what wipes a
/// drive, the root, a home folder, backups or a database (the red prompt) — this one also flags a delete of any
/// folder, docker / kubectl prune and rm, DROP / TRUNCATE / DELETE without WHERE, git that throws work away, registry
/// and service changes, shutdown and deploys. It reuses DestructiveCodeScan's patterns (deletes, drive / database /
/// backup wipes, how a command splits into statements, which statements only print text) and
/// <see cref="GuardHook.IsForcePush"/>; only the verbs those do not cover are listed here.
///
/// Like JudgeCommand, a statement that only prints, searches or records text (echo, grep, git commit -m …) runs
/// nothing and is not flagged — unless it hands the text on (a pipe, a redirect, $( )). Pure.
/// </summary>
public static class HardToUndo
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Deletes the shared DeleteVerb does not cover: a plain rm / del of files, unlink, shred, find -delete,
    /// rsync --delete, Clear-Content. (DeleteVerb has rm -r, rd /s, Remove-Item and the delete APIs.)</summary>
    private static readonly Regex DeleteMore = new(
        @"(?<![\w.$-])(?<!\bgit\s+)(rm|del|erase|unlink|shred)(\.exe)?\s+[^\s|&;]" +
        @"|\bfind\b[^\r\n|;&]*\s-delete\b|\brsync\b[^\r\n|;&]*\s--delete(-\w+)?\b|\bClear-Content\b|\bri\s+-",
        Opt);

    /// <summary>Containers, clusters and caches: prune, rm, rmi, compose down -v, kubectl delete.</summary>
    private static readonly Regex Prune = new(
        @"\b(docker|podman)(-compose)?\s+(system|builder|buildx|image|container|volume|network)\s+(prune|rm)\b" +
        @"|\b(docker|podman)\s+(rm|rmi)\b|\b(docker|podman)[\s-]compose\s+down\b[^\r\n|;&]*\s(-v|--volumes)\b" +
        @"|\bkubectl\s+delete\b|\b(apt|apt-get)\s+(purge|autoremove)\b|\bwinget\s+uninstall\b|\bchoco\s+uninstall\b",
        Opt);

    /// <summary>SQL that destroys rows or tables (DatabaseWipe has whole databases / schemas): DROP TABLE, TRUNCATE,
    /// ALTER … DROP, DELETE FROM / UPDATE … SET with no WHERE in the same statement.</summary>
    private static readonly Regex SqlDestroy = new(
        @"\bdrop\s+(table|view|index|user|role|collection)\b|\btruncate\s+(table\s+)?[`""\[]?[A-Za-z_]" +
        @"|\balter\s+table\s+\S+\s+drop\b|\bdelete\s+from\s+[`""\[]?[\w.]+[`""\]]?(?![^;""'\r\n]*\bwhere\b)" +
        @"|\bupdate\s+[`""\[]?[\w.]+[`""\]]?\s+set\b(?![^;""'\r\n]*\bwhere\b)|\.drop\(\s*\)|\.deleteMany\(\s*\{\s*\}\s*\)",
        Opt);

    /// <summary>Registry writes and deletes.</summary>
    private static readonly Regex Registry = new(
        @"\breg(\.exe)?\s+(delete|add|import|restore|load|unload|copy)\b|\bregedit(\.exe)?\s+/s\b" +
        @"|\b(Remove|Set|New|Rename|Clear)-ItemProperty\b|\b(Remove-Item|New-Item|Set-Item)\b[^\r\n|;&]*\b(HK(LM|CU|CR|U|CC)|Registry)::?",
        Opt);

    /// <summary>Services, scheduled tasks, power and permissions on a whole tree.</summary>
    private static readonly Regex Service = new(
        @"\bsc(\.exe)?\s+(delete|stop|config|create|failure)\b|\b(Stop|Remove|Set|New|Restart|Suspend)-Service\b" +
        @"|\bnet(\.exe)?\s+stop\b|\bsystemctl\s+(stop|disable|mask|restart|kill)\b|\bservice\s+\S+\s+(stop|restart)\b" +
        @"|(?<![\w.-])(shutdown|reboot|poweroff|halt)(\.exe)?(\s|$)|\b(Stop|Restart)-Computer\b" +
        @"|\bschtasks(\.exe)?\s+/(delete|change|create)\b|\b(Unregister|Disable)-ScheduledTask\b" +
        @"|\bch(mod|own|grp)\s+(-\w*R\w*|--recursive)\b|\bicacls\b[^\r\n|;&]*\s/(reset|grant|remove|deny|setowner)\b[^\r\n|;&]*\s/t\b",
        Opt);

    /// <summary>Deploys and publishes: what reaches production or a public registry.</summary>
    private static readonly Regex Deploy = new(
        @"(?<![\w-])(\./|\.\\)?deploy[\w.-]*\.(sh|ps1|bat|cmd)\b|\b(terraform|tofu)\s+(apply|destroy)\b|\bpulumi\s+(up|destroy)\b" +
        @"|\bvercel\b[^\r\n|;&]*\s--prod\b|\bnetlify\s+deploy\b[^\r\n|;&]*\s--prod\b|\bfirebase\s+deploy\b|\bfly(ctl)?\s+deploy\b" +
        @"|\bkubectl\s+(apply|replace|scale|drain|rollout\s+(undo|restart))\b|\bhelm\s+(upgrade|install|uninstall|rollback)\b" +
        @"|\bnpm\s+publish\b|\bdotnet\s+nuget\s+push\b|\bcargo\s+publish\b|\btwine\s+upload\b|\bgh\s+release\s+create\b" +
        @"|\bheroku\s+(releases:rollback|apps:destroy|pg:reset)\b|\baws\s+\S+\s+(delete|terminate|rm|rb)[\w-]*\b|\baz\s+\S+(\s+\S+)?\s+delete\b|\bgcloud\b[^\r\n|;&]*\sdelete\b",
        Opt);

    /// <summary>Git that throws work away (force push is GuardHook.IsForcePush; reset --hard and clean are on its
    /// never-run list for headless CLIs too).</summary>
    private static readonly Regex GitLoss = new(
        @"\bgit\b[^\r\n|;&]*\s(reset\s+[^\r\n|;&]*--hard|clean\s+-\w*f|branch\s+(-\w*D\w*|--delete\s+--force|-d\s+-f)|stash\s+(drop|clear)" +
        @"|push\b[^\r\n|;&]*\s(--delete|-d|--mirror|--prune)\b|push\s+\S+\s+:\S|checkout\s+(-f\s|--\s+\S|\.\s*$)|restore\s+(?!--staged)[^\r\n|;&]*\S" +
        @"|reflog\s+expire|filter-(branch|repo)|update-ref\s+-d|tag\s+-d|gc\b[^\r\n|;&]*--prune=now)",
        Opt);

    /// <summary><c>ssh [options] host the-command</c> in a local shell: the command runs on host.</summary>
    private static readonly Regex SshRemote = new(
        @"(?<![\w.-])ssh(\.exe)?((\s+-[A-Za-z]+(\s+(?!-)[^\s""']+)?)*)\s+(?<host>[^\s""'-][^\s""']*)\s+(?<cmd>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The ssh options that take a value (the token after them is not the host).</summary>
    private const string SshValueOpts = "bcDEeFIiJLlmOopQRSWw";

    /// <summary>
    /// Approving one's own approval: BrainX's <c>brainx-mcp ssh-approve</c> and CluadeX's <c>--cx-approve</c>. Only the
    /// owner approves, in their own terminal — an agent's shell never runs these, with strict approval on or off.
    /// </summary>
    private static readonly Regex SelfApprovalWords = new(@"ssh-approve|--cx-approve\b|\bcluadex(\.exe)?\s+approve\b", Opt);

    /// <summary>Does the command try to approve a pending approval itself? Statements that only print or search the
    /// words (grep ssh-approve) are not. Pure.</summary>
    public static bool IsSelfApproval(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        foreach (var statement in DestructiveCodeScan.StatementBreak.Split(command))
        {
            if (!SelfApprovalWords.IsMatch(statement)) continue;
            if (OnlyText(statement)) continue;
            return true;
        }
        return false;
    }

    /// <summary>What the agent is told when it tries to approve itself.</summary>
    public const string SelfApprovalRefusal =
        "CluadeX safety: refused — only the owner approves a pending approval (BrainX's ssh-approve, CluadeX's strict-approval "
        + "code), in their own terminal or on CluadeX's card. Never run it yourself; tell the user what is waiting for their OK.";

    /// <summary>
    /// The first hard-to-undo statement of a command, or null. <paramref name="remoteHost"/> non-null = the command runs
    /// on that machine (BrainX's ssh_run): every finding is then <see cref="StrictScope.Remote"/>. Pure.
    /// </summary>
    public static HardToUndoFinding? Classify(string? command, string? remoteHost = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Length > 20_000) command = command[..20_000];
        var found = ClassifyLocal(command);
        if (found == null) return null;
        return remoteHost != null ? found with { Scope = StrictScope.Remote, Host = remoteHost } : found;
    }

    private static HardToUndoFinding? ClassifyLocal(string command)
    {
        foreach (var statement in DestructiveCodeScan.StatementBreak.Split(command))
        {
            if (string.IsNullOrWhiteSpace(statement) || OnlyText(statement)) continue;
            // `ssh host 'rm -rf x; …'`: the quoted part was cut at the ; above, so the remote command is read from the
            // ssh to the end of the whole command line.
            if (SshTarget(statement) is { } host)
            {
                int at = command.IndexOf(statement, StringComparison.Ordinal);
                string tail = at >= 0 ? command[at..] : statement;
                var m = SshRemote.Match(tail);
                if (m.Success && ClassifyLocal(Unquote(m.Groups["cmd"].Value)) is { } inner)
                    return inner with { Scope = StrictScope.Remote, Host = host };
                continue;
            }
            if (JudgeStatement(statement) is { } hit) return hit;
        }
        return null;
    }

    private static HardToUndoFinding? JudgeStatement(string s)
    {
        string lower = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        if (GuardHook.IsForcePush(lower)) return new(StrictScope.Git, "git-force", "git push --force");
        if (GitLoss.Match(s) is { Success: true } g) return new(StrictScope.Git, "git-loss", Trim(g.Value));
        if (DestructiveCodeScan.JudgeLine(s) is { } catastrophic) return new(StrictScope.Local, "wipe", catastrophic);
        if (DestructiveCodeScan.DriveWipe.Match(s) is { Success: true } dw) return new(StrictScope.Local, "format", Trim(dw.Value));
        if (DestructiveCodeScan.DatabaseWipe.Match(s) is { Success: true } db) return new(StrictScope.Local, "database", Trim(db.Value));
        if (SqlDestroy.Match(s) is { Success: true } sql) return new(StrictScope.Local, "database", Trim(sql.Value));
        if (DestructiveCodeScan.BackupOrBootWipe.Match(s) is { Success: true } bk) return new(StrictScope.Local, "backup", Trim(bk.Value));
        if (Prune.Match(s) is { Success: true } p) return new(StrictScope.Local, "prune", Trim(p.Value));
        if (Registry.Match(s) is { Success: true } r) return new(StrictScope.Local, "registry", Trim(r.Value));
        if (Service.Match(s) is { Success: true } sv) return new(StrictScope.Local, "service", Trim(sv.Value));
        if (Deploy.Match(s) is { Success: true } d) return new(StrictScope.Local, "deploy", Trim(d.Value));
        // Deletes last: a code-API delete (Directory.Delete( …) inside a one-liner is still a delete.
        if (DestructiveCodeScan.DeleteVerb.Match(s) is { Success: true } del) return new(StrictScope.Local, "delete", Trim(del.Value));
        if (DeleteMore.Match(s) is { Success: true } dm) return new(StrictScope.Local, "delete", Trim(dm.Value));
        return null;
    }

    /// <summary>The host of an <c>ssh … host command</c> statement, or null (no command after the host = an interactive
    /// login, which changes nothing by itself).</summary>
    private static string? SshTarget(string statement)
    {
        var m = Regex.Match(statement, @"(?<![\w.-])ssh(\.exe)?\s+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var toks = Regex.Matches(m.Groups["rest"].Value, @"""[^""]*""|'[^']*'|\S+").Select(x => x.Value).ToList();
        for (int i = 0; i < toks.Count; i++)
        {
            string t = toks[i];
            if (t.StartsWith('-'))
            {
                // -i key, -p 22, -o Opt=v: the option's value is the next token (unless glued: -p22).
                if (t.Length == 2 && SshValueOpts.Contains(t[1])) i++;
                continue;
            }
            return i + 1 < toks.Count ? t : null;
        }
        return null;
    }

    /// <summary>A statement that only prints, searches or records text — and does not hand it on.</summary>
    private static bool OnlyText(string statement) =>
        DestructiveCodeScan.TextOnlyStart.IsMatch(statement) && !DestructiveCodeScan.HandsTextOn.IsMatch(statement);

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) return s[1..^1];
        if (s.Length >= 1 && (s[0] == '"' || s[0] == '\'')) return s[1..];
        return s;
    }

    private static string Trim(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    /// <summary>The scope names in <see cref="StrictApproval.EnvName"/> ("local,remote,git").</summary>
    public static StrictScope ParseScopes(string? text)
    {
        var scope = StrictScope.None;
        foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            scope |= part.Trim().ToLowerInvariant() switch
            {
                "local" => StrictScope.Local,
                "remote" or "ssh" => StrictScope.Remote,
                "git" => StrictScope.Git,
                "all" => StrictScope.All,
                _ => StrictScope.None,
            };
        return scope;
    }

    public static string FormatScopes(StrictScope scope) =>
        string.Join(",", new[] { (StrictScope.Local, "local"), (StrictScope.Remote, "remote"), (StrictScope.Git, "git") }
            .Where(p => scope.HasFlag(p.Item1)).Select(p => p.Item2));
}
