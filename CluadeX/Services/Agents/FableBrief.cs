using System.Text;
using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>
/// The first message of a chat that takes a hard task to Claude Fable 5.2: a focused brief, never the whole chat. Forking
/// a 170K-token transcript into Fable writes it all to Fable's cache at once (~$1.70) and every step re-reads it; a brief
/// of a few thousand tokens carries what the work needs — the problem, the files and repos, what was tried and failed,
/// and what done looks like — and ends with the report Fable sends back (<see cref="DispatchPolicy.FableReportInstruction"/>).
/// The chat's background comes from the failover handoff brief (<see cref="AgentHandoff.Compose"/>), cut to a small share.
/// Pure (git is passed in), for the contract harness.
/// </summary>
public static class FableBrief
{
    /// <summary>The whole brief's bound in characters (~3K tokens): enough for a hard problem, a fraction of a fork.</summary>
    public const int DefaultMaxChars = 12_000;

    /// <param name="problem">What Fable must solve (the asking agent's message, or the owner's words).</param>
    /// <param name="files">Files and repos that matter (the asking agent's list; "" = none given).</param>
    /// <param name="tried">What was tried and did not work ("" = taken from the chat's failed steps).</param>
    /// <param name="doneWhen">What done looks like ("" = a default: the build / tests green and the cause explained).</param>
    /// <param name="history">The asking chat's rows (its background; failed steps when <paramref name="tried"/> is empty).</param>
    /// <param name="folder">The project folder, and <paramref name="extraFolders"/> open beside it (other repos).</param>
    /// <param name="git">The folder's git snapshot (AgentHandoff.GitSnapshotAsync), "" = none.</param>
    public static string Compose(string problem, string? files, string? tried, string? doneWhen,
        IReadOnlyList<ChatMessage>? history, string? folder, IReadOnlyList<string>? extraFolders, string git,
        int maxChars = DefaultMaxChars)
    {
        maxChars = Math.Max(2000, maxChars);
        var sb = new StringBuilder();
        sb.AppendLine("## A hard task, sent to you (Claude Fable 5.2) with a focused brief");
        sb.AppendLine("Another model worked on this and could not finish it. You get this brief, not its whole conversation: "
                      + "read the files yourself before you trust anything below.");
        sb.AppendLine().AppendLine("### The problem");
        sb.AppendLine(Cut((problem ?? "").Trim(), maxChars * 30 / 100));

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(folder)) where.Add("Project folder: " + folder);
        if (extraFolders is { Count: > 0 })
            foreach (var f in extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
                where.Add("Related repo (open beside it): " + f);
        if (!string.IsNullOrWhiteSpace(files)) where.Add(Cut(files!.Trim(), maxChars * 10 / 100));
        if (where.Count > 0)
        {
            sb.AppendLine().AppendLine("### Files and repos involved");
            foreach (var w in where) sb.AppendLine("- " + w);
        }

        string triedText = string.IsNullOrWhiteSpace(tried) ? FailedSteps(history, maxChars * 15 / 100) : Cut(tried!.Trim(), maxChars * 15 / 100);
        sb.AppendLine().AppendLine("### Tried already — it did not work");
        sb.AppendLine(triedText.Length > 0 ? triedText : "- (nothing recorded)");

        sb.AppendLine().AppendLine("### Done when");
        sb.AppendLine(string.IsNullOrWhiteSpace(doneWhen)
            ? "- the root cause is found and fixed, the build and tests that cover it pass, and nothing else broke."
            : Cut(doneWhen!.Trim(), maxChars * 8 / 100));

        // The chat's background: the failover brief, small — the task, where it stopped, its last steps, git.
        if (history is { Count: > 0 } && AgentHandoff.Compose(history, null, git, Math.Max(1200, maxChars * 25 / 100)) is { } bg)
        {
            string body = bg.Replace(AgentHandoff.StaleToolsNote, "").Trim();
            int head = body.IndexOf('\n');
            if (head > 0 && body.StartsWith("## Handoff", StringComparison.Ordinal)) body = body[(head + 1)..].Trim();
            sb.AppendLine().AppendLine("### Background from the chat that sent it (a summary, not the transcript)");
            sb.AppendLine(body);
        }
        else if (!string.IsNullOrWhiteSpace(git))
            sb.AppendLine().AppendLine("### What git says changed in the folder").AppendLine(Cut(git.Trim(), maxChars * 15 / 100));

        // The report line is last, so the overall bound must not be what cuts it.
        string report = DispatchPolicy.FableReportInstruction(newChat: true);
        return Cut(sb.ToString().TrimEnd(), Math.Max(1000, maxChars - report.Length - 2)) + "\n\n" + report;
    }

    /// <summary>The chat's failed tool steps, newest last, one line each ("" = none).</summary>
    public static string FailedSteps(IReadOnlyList<ChatMessage>? history, int maxChars)
    {
        if (history == null) return "";
        var failed = history.Where(m => m.Role == MessageRole.ToolAction && !m.ToolSuccess && !string.IsNullOrEmpty(m.ToolName))
            .TakeLast(8).ToList();
        if (failed.Count == 0) return "";
        int each = Math.Max(80, maxChars / failed.Count);
        var sb = new StringBuilder();
        foreach (var m in failed)
        {
            string what = (m.ToolInputSummary ?? "").Trim();
            string err = FirstErrorLine(m.ToolOutput ?? m.Content ?? "");
            sb.AppendLine("- " + Cut(OneLine($"{m.ToolName} {what}".Trim() + (err.Length > 0 ? " → " + err : " → failed")), each));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FirstErrorLine(string output)
    {
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                                          || l.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "";
    }

    private static string OneLine(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
}
