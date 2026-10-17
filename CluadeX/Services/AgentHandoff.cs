using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// The brief a complete agent (Claude Dev, the BrainX cowork room) gets when it takes over a chat another model worked
/// in. Owner 2026-10-17: the API quota ran low mid-task (HIVE // BREACH, paused at step 101), the owner switched the
/// chat to Claude Dev / the cowork room and typed "ทำต่อ" — and the new model got the word "ทำต่อ" and a transcript
/// window filled with tool rows: no task, no plan, no files. "งานต้องต่อกันได้จริงๆ".
/// <para>The brief: the task (the chat's first request), later requests, where the last model stopped, its last steps,
/// and what git says changed in the folder. Built only when the chat already has work in it.</para>
/// </summary>
public static class AgentHandoff
{
    /// <summary>Does this history hold earlier work worth handing over (a reply or a tool step)?</summary>
    public static bool HasEarlierWork(IReadOnlyList<ChatMessage> history) =>
        history.Any(m => m.Role is MessageRole.Assistant or MessageRole.ToolAction && !string.IsNullOrWhiteSpace(m.Content));

    /// <summary>The brief, or null for a fresh chat. <paramref name="git"/> is the folder's git snapshot ("" = none);
    /// <paramref name="maxChars"/> bounds the whole text (the room's board takes ~4 KB, the CLI far more).</summary>
    public static string? Compose(IReadOnlyList<ChatMessage> history, string? folder, string git, int maxChars)
    {
        if (!HasEarlierWork(history)) return null;
        var users = history.Where(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content)).ToList();
        var lastReply = history.LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content));
        var steps = history.Where(m => m.Role == MessageRole.ToolAction && !string.IsNullOrWhiteSpace(m.Content)).ToList();

        // Shares of the budget: the task matters most, then what changed, then where it stopped.
        int taskBudget = Math.Max(600, maxChars * 45 / 100);
        int gitBudget = Math.Max(300, maxChars * 20 / 100);
        int stopBudget = Math.Max(300, maxChars * 15 / 100);
        int stepsBudget = Math.Max(200, maxChars * 10 / 100);
        int laterBudget = Math.Max(200, maxChars * 8 / 100);

        var sb = new StringBuilder();
        sb.AppendLine("## Handoff — you are continuing work another model started in this chat");
        if (!string.IsNullOrWhiteSpace(folder)) sb.AppendLine("Project folder: " + folder);
        if (users.Count > 0)
        {
            sb.AppendLine().AppendLine("### The task (the first request in this chat)");
            sb.AppendLine(Cut(users[0].Content.Trim(), taskBudget));
        }
        if (users.Count > 1)
        {
            sb.AppendLine().AppendLine("### Requests since then (oldest first)");
            var later = users.Skip(1).TakeLast(5).ToList();
            int each = Math.Max(80, laterBudget / later.Count);
            foreach (var u in later) sb.AppendLine("- " + Cut(OneLine(u.Content), each));
        }
        if (lastReply != null)
        {
            sb.AppendLine().AppendLine("### Where the previous model stopped (its last reply)");
            sb.AppendLine(Cut(lastReply.Content.Trim(), stopBudget));
        }
        if (steps.Count > 0)
        {
            sb.AppendLine().AppendLine($"### Its last steps ({steps.Count} in all)");
            var tail = steps.TakeLast(12).ToList();
            int each = Math.Max(60, stepsBudget / tail.Count);
            foreach (var s in tail) sb.AppendLine("- " + Cut(OneLine(s.Content), each));
        }
        if (!string.IsNullOrWhiteSpace(git))
        {
            sb.AppendLine().AppendLine("### What git says changed in the folder");
            sb.AppendLine(Cut(git.Trim(), gitBudget));
        }
        sb.AppendLine().AppendLine("Check the files as they are now before changing anything, keep the plan the task set, and do not redo finished work. " +
                                   "When you finish, say what you did, what is left, and the test result.");
        return Cut(sb.ToString().TrimEnd(), maxChars);
    }

    /// <summary>The user's words first (an "@codex" target must lead), the brief under them.</summary>
    public static string WithBrief(string userMessage, string brief) =>
        (userMessage ?? "").TrimEnd() + "\n\n" + brief;

    /// <summary>`git status --short` + `git diff --stat` of the folder, "" when it is not a repo or git is missing.</summary>
    public static async Task<string> GitSnapshotAsync(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return "";
        string branch = (await GitAsync(folder, "branch --show-current", ct)).Trim();
        string status = await GitAsync(folder, "status --short", ct);
        string stat = await GitAsync(folder, "diff --stat HEAD", ct);
        if (status.Length == 0 && stat.Length == 0 && branch.Length == 0) return "";
        var sb = new StringBuilder();
        if (branch.Length > 0) sb.AppendLine("branch: " + branch);
        if (status.Trim().Length > 0) sb.AppendLine("git status --short:").AppendLine(status.TrimEnd());
        else sb.AppendLine("git status: clean");
        if (stat.Trim().Length > 0) sb.AppendLine("git diff --stat HEAD:").AppendLine(stat.TrimEnd());
        return sb.ToString();
    }

    private static async Task<string> GitAsync(string folder, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-c core.quotepath=off " + args)
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var read = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 ? await read : "";
        }
        catch { return ""; }
    }

    private static string OneLine(string s)
    {
        string first = s.Trim().Split('\n', 2)[0].Trim();
        return first.Length > 0 ? first : s.Trim();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
}
