using System.Text;
using CluadeX.Models;
using CluadeX.Services.Background;
using CluadeX.Services.Providers;
using CluadeX.Services.Watch;

namespace CluadeX.Services.Tools;

/// <summary>
/// watch_ci / watch_process / watch_list / watch_cancel (owner 2026-11-07: "ตัวเฝ้าติดตาม งาน … การรอ ci หรือ commit ให้ผ่าน").
/// The agent hands the wait to <see cref="RunWatchService"/> and ends its turn; the chat is told when it ends and, with
/// nobody running there, a new turn starts with the result. No polling on the model's clock, no `sleep` in a shell.
/// </summary>
public sealed partial class EngineeringToolModule
{
    private static IEnumerable<ToolSchema> WatchSchemas()
    {
        yield return Schema("watch_ci",
            "Wait for CI without spending turns: CluadeX watches the checks (every 15–90 s) and, when they finish, starts your next "
            + "turn in this chat with the result (pass, or which checks failed and their links) — or hands it to you at your next "
            + "step if you are still working. Call it after a push, then END YOUR TURN with one short line; never poll "
            + "gh_pr_checks or sleep. With none of pr / branch / commit: the PR of the current branch, else the commit at HEAD "
            + "(push it first — CI only runs on what GitHub has). Needs gh signed in.",
            ("pr", "string", "PR number or github.com PR URL", false),
            ("commit", "string", "A pushed commit's hash: its GitHub Actions runs", false),
            ("branch", "string", "A branch: the runs of its newest commit", false),
            ("repo", "string", "owner/name (default: this repository)", false),
            ("note", "string", "What you will do when it ends (handed back to you with the result)", false),
            ("timeout_minutes", "integer", "Give up after this long (default 60, max 360)", false));
        yield return Schema("watch_process",
            "Wait for a background process (bg_start / a server or a long build or test run) to exit without spending turns: "
            + "when it ends CluadeX starts your next turn in this chat with its exit code and last lines. END YOUR TURN after "
            + "calling it; never poll bg_log or sleep.",
            ("process_id", "string", "The background process: 3 or bg#3 (bg_list lists them)", true),
            ("note", "string", "What you will do when it ends", false),
            ("timeout_minutes", "integer", "Give up after this long (default 120, max 720)", false));
        yield return Schema("watch_list",
            "This chat's watches (watch_ci / watch_process): what each waits for, its state and its last look.");
        yield return Schema("watch_cancel",
            "Stop one of this chat's watches (its id from watch_ci / watch_list), or all of them with id = \"all\". Nothing is "
            + "told to the chat afterwards.",
            ("id", "string", "The watch's id (w…) or \"all\"", true));
    }

    private string? WatchRefusal() => RunWatchService.Current == null
        ? "Watching is not available in this CluadeX (no watch service) — check CI once with gh_pr_checks instead."
        : null;

    private static string? WatchChat() => AgentRunScope.Current?.SessionKey is { Length: > 0 } k ? k : null;

    private static TimeSpan? Minutes(ToolCall call) =>
        int.TryParse(Arg(call, "timeout_minutes"), out int m) && m > 0 ? TimeSpan.FromMinutes(m) : null;

    private async Task<ToolResult> WatchCiAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        var svc = RunWatchService.Current;
        string? chat = WatchChat();
        if (svc == null || chat == null) return Fail(call, WatchRefusal() ?? "A watch belongs to a chat, and this call came from none.");
        string repo = Arg(call, "repo"), pr = Arg(call, "pr"), branch = Arg(call, "branch"), commit = Arg(call, "commit");
        if (pr.StartsWith('#')) pr = pr[1..];
        if (GhCiProbe.CheckArgs(repo, pr, branch, commit) is { } bad) return Fail(call, bad);

        string how = "";
        if (pr.Length == 0 && branch.Length == 0 && commit.Length == 0)
        {
            if (repo.Length > 0) return Fail(call, "With repo, say what to watch there: pr, branch or commit.");
            // The current branch's PR, else the commit at HEAD. Dry-run: gh is not asked (straight to HEAD).
            var view = GhCiProbe.GhBlocked
                ? new ProcessOutcome(-1, "", "", NotFound: false, TimedOut: false)
                : await RunProcessAsync("gh", new[] { "pr", "view", "--json", "number", "--jq", ".number" }, ctx.WorkingDirectory!, 30_000, ct);
            if (view.NotFound) return Fail(call, "The GitHub CLI (gh) is not installed — install it (winget install --id GitHub.cli), run `gh auth login`, then try again.");
            if (view.ExitCode == 0 && view.Text.Trim() is { Length: > 0 } n && n.All(char.IsDigit))
            {
                pr = n;
                how = "the PR of the current branch";
            }
            else
            {
                var head = await RunProcessAsync("git", new[] { "rev-parse", "HEAD" }, ctx.WorkingDirectory!, 15_000, ct);
                string sha = head.Text.Trim();
                if (head.ExitCode != 0 || GhCiProbe.CheckArgs(null, null, null, sha) != null)
                    return Fail(call, "This branch has no PR and HEAD could not be read — pass pr, branch or commit.");
                commit = sha;
                how = "the commit at HEAD (this branch has no PR)";
                var ahead = await RunProcessAsync("git", new[] { "status", "--porcelain=v1", "--branch" }, ctx.WorkingDirectory!, 15_000, ct);
                if (ahead.ExitCode == 0 && ahead.Text.Split('\n')[0].Contains("[ahead", StringComparison.Ordinal))
                    how += " — note: this branch has commits that are not pushed, so CI may never start on them";
            }
        }

        // gh run list --commit matches the full hash only (a short one lists nothing — the watch would wait for checks that
        // never appear). Expand it here; a hash this repository does not know must be given whole.
        if (commit.Length is > 0 and < 40)
        {
            string? full = await GhCiProbe.FullShaAsync(commit, ctx.WorkingDirectory!, ct);
            if (full == null)
                return Fail(call, $"commit {commit} is not a commit of this repository — give the full 40-character hash (gh matches only that).");
            commit = full;
        }

        var r = svc.StartCi(chat, ctx.WorkingDirectory!, repo, pr, branch, commit, Arg(call, "note"), Minutes(call));
        if (r.Job == null) return Fail(call, r.Error ?? "The watch did not start.");
        return Ok(call, Started(r, how), (r.Existing ? "already watching " : "watching ") + r.Job.Target);
    }

    private ToolResult WatchProcess(ToolCall call, ToolModuleContext ctx)
    {
        var svc = RunWatchService.Current;
        string? chat = WatchChat();
        if (svc == null || chat == null) return Fail(call, WatchRefusal() ?? "A watch belongs to a chat, and this call came from none.");
        string raw = Arg(call, "process_id");
        if (raw.Length == 0) raw = Arg(call, "id");
        if (!BackgroundProcessService.TryParseId(raw, out int id))
            return Fail(call, $"process_id must be a background process number such as 3 or bg#3, not '{RunWatchService.Clip(raw, 40)}'.");
        var r = svc.StartProcess(chat, id, Arg(call, "note"), Minutes(call));
        if (r.Job == null) return Fail(call, r.Error ?? "The watch did not start.");
        return Ok(call, Started(r, ""), (r.Existing ? "already watching " : "watching ") + r.Job.Target);
    }

    private static string Started(RunWatchService.StartResult r, string how)
    {
        var j = r.Job!;
        var sb = new StringBuilder();
        sb.Append(r.Existing ? $"Already watching {j.Target} (watch {j.Id})." : $"Watching {j.Target} (watch {j.Id}, until {j.Deadline:HH:mm}).");
        if (how.Length > 0) sb.Append(" That is ").Append(how).Append('.');
        sb.Append("\nEnd your turn now with one short line saying what you are waiting for. When it ends, CluadeX starts your next turn "
                  + "in this chat with the result (or gives it to you at your next step if you are still working). Do not poll, "
                  + "sleep or call this again for the same thing.");
        return sb.ToString();
    }

    private static ToolResult WatchList(ToolCall call)
    {
        var svc = RunWatchService.Current;
        string? chat = WatchChat();
        if (svc == null || chat == null) return Fail(call, "No watch service, or no chat.");
        var mine = svc.Jobs.Where(j => j.ChatId == chat).OrderByDescending(j => j.CreatedAt).ToList();
        if (mine.Count == 0) return Ok(call, "This chat watches nothing.", "no watches");
        var active = mine.Where(j => j.IsActive).ToList();
        var sb = new StringBuilder();
        foreach (var j in active.Concat(mine.Where(j => !j.IsActive).Take(5)))
        {
            sb.Append($"{j.Id} · {j.Target} · {RunWatchService.StateWord(j.State)}");
            if (j.Summary.Length > 0) sb.Append(" — ").Append(j.Summary);
            if (j.IsActive) sb.Append($" · until {j.Deadline:HH:mm}") .Append(j.LastCheckedAt is { } t ? $" · last look {t:HH:mm:ss}" : "");
            sb.Append('\n');
        }
        return Ok(call, sb.ToString().TrimEnd(), $"{active.Count} watching");
    }

    private static ToolResult WatchCancel(ToolCall call)
    {
        var svc = RunWatchService.Current;
        string? chat = WatchChat();
        if (svc == null || chat == null) return Fail(call, "No watch service, or no chat.");
        string id = Arg(call, "id");
        if (id.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            int n = svc.CancelChat(chat, "cancelled by the agent");
            return Ok(call, n == 0 ? "This chat watched nothing." : $"Cancelled {n} watch{(n == 1 ? "" : "es")}.", $"cancelled {n}");
        }
        return svc.Cancel(id, chat, "cancelled by the agent")
            ? Ok(call, $"Cancelled watch {id}.", "cancelled " + id)
            : Fail(call, $"This chat has no active watch '{RunWatchService.Clip(id, 40)}' — watch_list lists them.");
    }
}
