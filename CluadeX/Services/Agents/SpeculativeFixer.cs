using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>
/// Speculative fix: a red build or test command the main loop could not fix goes to 2–3 fixer sub-agents at once, each
/// in its own git worktree, each told a different approach and given ONLY the failing command, its trimmed output, the
/// files it names and the repo map. The orchestrator — not the fixer's word — re-runs the command in each worktree; the
/// first green one wins: its diff is applied to the run folder, the others are cancelled, every worktree is removed.
/// None green in time → the main loop gets what they found. The sub-agent and the command runner are passed in, so the
/// contract harness races fake fixers against a real git repository.
/// </summary>
public static class SpeculativeFixer
{
    public sealed record Request(string RunFolder, string Command, string FailureOutput, IReadOnlyList<string> Files,
        string RepoMap, string? BrainRecall, int Fixers, TimeSpan Timeout, int FailedFixersBefore = 0);

    /// <summary>What one fixer is handed: its number and approach, the worktree folder it works in, its prompt.</summary>
    public sealed record FixerContext(int Index, string Approach, string WorkingDirectory, string Prompt, int FailedFixersBefore);

    public sealed record FixerOutcome(int Index, string Approach, bool Green, string Report, TimeSpan Took,
        IReadOnlyList<string> ChangedFiles, string? Error);

    public sealed record Result(bool Fixed, FixerOutcome? Winner, IReadOnlyList<FixerOutcome> All, string Patch,
        string Summary, TimeSpan Took, string? ApplyError = null);

    /// <summary>A fixer's progress for the agent map: Phase "start" | "verify" | "green" | "red" | "failed" | "cancelled".</summary>
    public sealed record Event(int Index, string Approach, string Phase, string Detail);

    public static readonly string[] Approaches =
    [
        "MINIMAL — fix exactly what the first error names, at the line it names; change as little as possible.",
        "ROOT CAUSE — trace the error to where it really comes from (a changed signature, a missing member, a wrong "
            + "assumption upstream) and fix it there, even in another file; then make the callers agree.",
        "ALTERNATIVE — if the failing code is wrong in approach, rewrite the smallest unit around it (one method or block) "
            + "another way so the command passes, keeping its behaviour and public signatures.",
    ];

    /// <summary>The fixer's whole brief: nothing from the main conversation.</summary>
    public static string BuildPrompt(Request r, int index, string approach)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are fixer #{index + 1} of {r.Fixers}, racing the others to turn ONE failing command green. "
            + "You work in your own copy of the project (a git worktree); the first copy whose command passes wins.");
        sb.AppendLine();
        sb.AppendLine("## Failing command");
        sb.AppendLine("```");
        sb.AppendLine(r.Command);
        sb.AppendLine("```");
        sb.AppendLine("## Its output (trimmed)");
        sb.AppendLine("```");
        sb.AppendLine(TrimOutput(r.FailureOutput));
        sb.AppendLine("```");
        if (r.Files.Count > 0)
        {
            sb.AppendLine("## Files the output names");
            foreach (var f in r.Files) sb.AppendLine("- " + f);
        }
        sb.AppendLine();
        sb.AppendLine("## Your approach");
        sb.AppendLine(approach);
        if (!string.IsNullOrWhiteSpace(r.RepoMap))
        {
            sb.AppendLine();
            sb.AppendLine("## Repo map (cached — do not re-scan the project)");
            sb.AppendLine(r.RepoMap.Trim());
        }
        if (!string.IsNullOrWhiteSpace(r.BrainRecall))
        {
            sb.AppendLine();
            sb.AppendLine("## Past lessons from the brain");
            sb.AppendLine(r.BrainRecall.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("Read the named files, make the fix with your edit tools, and stop. The command is re-run for you after you "
            + "finish. Do not commit. In your report: the cause in one line, and what you changed (file:line).");
        return sb.ToString();
    }

    /// <summary>The part of a long build log that matters: error lines first (deduplicated), then the tail.</summary>
    public static string TrimOutput(string output, int max = 4000)
    {
        output = (output ?? "").Replace("\r\n", "\n");
        if (output.Length <= max) return output.Trim();
        var errors = output.Split('\n').Where(l => ErrorLine.IsMatch(l)).Select(l => l.Trim()).Distinct().Take(40).ToList();
        string head = string.Join("\n", errors);
        if (head.Length > max / 2) head = head[..(max / 2)];
        int tail = max - head.Length - 40;
        return (head.Length > 0 ? head + "\n…\n" : "") + output[^Math.Max(200, tail)..].Trim();
    }

    private static readonly Regex ErrorLine = new(@"\b(error|failed|failure|exception|assert)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // path.ext followed by (line,col) / :line / ", line N" — the shapes compilers and test runners print.
    private static readonly Regex PathRx = new(@"(?<p>(?:[A-Za-z]:[\\/])?[\w.\-\\/]+\.(?:cs|xaml|fs|vb|py|js|jsx|ts|tsx|go|rs|java|kt|cpp|cc|c|h|hpp|rb|php|swift|dart|lua))(?=[(:,\s""']|$)",
        RegexOptions.Compiled);

    /// <summary>Files under <paramref name="root"/> that the output names (relative, '/', existing, at most <paramref name="max"/>).</summary>
    public static List<string> FilesNamedIn(string output, string root, int max = 8)
    {
        var found = new List<string>();
        foreach (Match m in PathRx.Matches(output ?? ""))
        {
            string p = m.Groups["p"].Value.Trim('"', '\'');
            try
            {
                string full = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(root, p));
                if (!File.Exists(full)) continue;
                string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (rel.StartsWith("..")) continue;
                if (!found.Contains(rel, StringComparer.OrdinalIgnoreCase)) found.Add(rel);
                if (found.Count >= max) break;
            }
            catch { /* not a path */ }
        }
        return found;
    }

    /// <summary>
    /// The race. <paramref name="runFixer"/> runs one fixer sub-agent in its worktree and returns its report;
    /// <paramref name="runCommand"/> runs the failing command in a folder and says whether it passed.
    /// </summary>
    public static async Task<Result> RunAsync(Request req,
        Func<FixerContext, CancellationToken, Task<string>> runFixer,
        Func<string, string, CancellationToken, Task<(bool Green, string Output)>> runCommand,
        Action<Event>? onEvent, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        int n = Math.Clamp(req.Fixers, 1, Approaches.Length);
        var boxes = new List<WorktreeSandbox>();
        var outcomes = new FixerOutcome?[n];
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(req.Timeout);
        int winner = -1;
        try
        {
            // git serializes worktree creation on its own lock — one at a time.
            for (int i = 0; i < n; i++) boxes.Add(await WorktreeSandbox.CreateAsync(req.RunFolder, $"fix{i + 1}", ct));

            async Task One(int i)
            {
                var sw = Stopwatch.StartNew();
                string approach = Approaches[i];
                string report = "";
                try
                {
                    onEvent?.Invoke(new Event(i, approach, "start", ""));
                    var ctx = new FixerContext(i, approach, boxes[i].WorkingDirectory, BuildPrompt(req, i, approach), req.FailedFixersBefore);
                    report = await runFixer(ctx, race.Token);
                    race.Token.ThrowIfCancellationRequested();
                    onEvent?.Invoke(new Event(i, approach, "verify", req.Command));
                    var (green, output) = await runCommand(req.Command, boxes[i].WorkingDirectory, race.Token);
                    var changed = await boxes[i].ChangedFilesAsync(CancellationToken.None);
                    outcomes[i] = new FixerOutcome(i, approach, green, report, sw.Elapsed, changed, green ? null : TrimOutput(output, 600));
                    if (green && changed.Count > 0 && Interlocked.CompareExchange(ref winner, i, -1) == -1)
                    {
                        onEvent?.Invoke(new Event(i, approach, "green", string.Join(", ", changed)));
                        race.Cancel();   // the others stop
                    }
                    else onEvent?.Invoke(new Event(i, approach, green ? "green" : "red", green ? "too late" : FirstLine(output)));
                }
                catch (OperationCanceledException)
                {
                    outcomes[i] ??= new FixerOutcome(i, approach, false, report, sw.Elapsed, Array.Empty<string>(),
                        ct.IsCancellationRequested ? "stopped" : winner >= 0 ? "cancelled — another fixer won" : "timed out");
                    onEvent?.Invoke(new Event(i, approach, "cancelled", outcomes[i]!.Error ?? ""));
                }
                catch (Exception ex)
                {
                    outcomes[i] = new FixerOutcome(i, approach, false, report, sw.Elapsed, Array.Empty<string>(), ex.Message);
                    onEvent?.Invoke(new Event(i, approach, "failed", ex.Message));
                }
            }

            await Task.WhenAll(Enumerable.Range(0, n).Select(One));
            ct.ThrowIfCancellationRequested();

            var all = outcomes.Select((o, i) => o ?? new FixerOutcome(i, Approaches[i], false, "", TimeSpan.Zero, Array.Empty<string>(), "no result")).ToList();
            if (winner < 0)
                return new Result(false, null, all, "", Learned(req, all), clock.Elapsed);

            string patch = await boxes[winner].DiffAsync(CancellationToken.None);
            string? applyError = await WorktreeSandbox.ApplyAsync(req.RunFolder, patch, CancellationToken.None);
            var win = all[winner];
            string summary = applyError == null
                ? $"Fixer #{winner + 1} ({ApproachName(win.Approach)}) turned `{req.Command}` green in {win.Took.TotalSeconds:0}s; its change "
                  + $"is now applied to the project: {string.Join(", ", win.ChangedFiles)}.\nIts report: {win.Report.Trim()}"
                : $"Fixer #{winner + 1} turned the command green, but its diff did not apply to the project: {applyError}";
            return new Result(applyError == null, win, all, patch, summary, clock.Elapsed, applyError);
        }
        finally
        {
            foreach (var b in boxes) await b.DisposeAsync();
        }
    }

    /// <summary>No fixer got it green: what each one found, for the main loop.</summary>
    public static string Learned(Request req, IReadOnlyList<FixerOutcome> all)
    {
        var sb = new StringBuilder($"{all.Count} speculative fixers could not turn `{req.Command}` green. What they found:\n");
        foreach (var o in all)
        {
            sb.Append($"- #{o.Index + 1} {ApproachName(o.Approach)}: ");
            string r = (o.Report ?? "").Trim();
            sb.Append(r.Length > 500 ? r[..500] + "…" : r.Length > 0 ? r : "(no report)");
            if (!string.IsNullOrWhiteSpace(o.Error)) sb.Append($" — still failing: {FirstLine(o.Error!)}");
            sb.AppendLine();
        }
        sb.Append("Their changes were thrown away; the project is as it was. Use what they learned for your own fix.");
        return sb.ToString();
    }

    public static string ApproachName(string approach) => approach.Split(' ', 2)[0].Trim('—', ' ');

    private static string FirstLine(string s)
    {
        var line = (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => ErrorLine.IsMatch(l)) ?? (s ?? "").Trim().Split('\n')[0];
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
