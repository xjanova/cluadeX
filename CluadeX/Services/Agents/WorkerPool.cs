using System.Diagnostics;
using System.Text;

namespace CluadeX.Services.Agents;

/// <summary>
/// Workers (effort high and up): the main agent, as planner, splits a task into pieces that touch disjoint files; each
/// piece runs as a worker sub-agent in its own git worktree, in parallel (at most the concurrency setting at a time);
/// a reviewer sub-agent then reads the combined diff, and only an approved diff is applied to the run's folder. A worker
/// that changed a file outside its own list is dropped from the merge. The sub-agents are passed in, so the contract
/// harness runs fake workers against a real repository.
/// </summary>
public static class WorkerPool
{
    public sealed record WorkerContext(int Index, DispatchPolicy.WorkPiece Piece, string WorkingDirectory, string Prompt);

    public sealed record WorkerOutcome(int Index, DispatchPolicy.WorkPiece Piece, string Report, IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> OutsideFiles, string Patch, string? Error, TimeSpan Took)
    {
        public bool Usable => Error == null && OutsideFiles.Count == 0 && Patch.Length > 0;
    }

    public sealed record Review(bool Approved, string Notes);

    public sealed record Result(bool Merged, IReadOnlyList<WorkerOutcome> Workers, Review? Review, IReadOnlyList<string> AppliedFiles,
        string Summary, TimeSpan Took);

    public sealed record Event(int Index, string Phase, string Detail);

    public static string BuildPrompt(DispatchPolicy.WorkPiece piece, int index, int count, string context, string repoMap)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are worker {index + 1} of {count}. Other workers change other files at the same time, in their own copies.");
        sb.AppendLine();
        sb.AppendLine("## Your piece");
        sb.AppendLine(piece.Task.Trim());
        sb.AppendLine();
        sb.AppendLine("## Files you may change (ONLY these — a change to any other file is thrown away)");
        foreach (var f in piece.Files) sb.AppendLine("- " + f);
        if (!string.IsNullOrWhiteSpace(context)) { sb.AppendLine(); sb.AppendLine("## Context from the planner"); sb.AppendLine(context.Trim()); }
        if (!string.IsNullOrWhiteSpace(repoMap)) { sb.AppendLine(); sb.AppendLine("## Repo map (cached — do not re-scan the project)"); sb.AppendLine(repoMap.Trim()); }
        sb.AppendLine();
        sb.AppendLine("Make the change with your edit tools and stop. Do not commit. Report what you changed (file:line) and anything "
            + "the other pieces must agree with (a new signature, a renamed member).");
        return sb.ToString();
    }

    public static string BuildReviewPrompt(string task, IReadOnlyList<WorkerOutcome> usable)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review this combined change before it is merged into the project. It was made by parallel workers, each on its own files.");
        sb.AppendLine();
        sb.AppendLine("## The task");
        sb.AppendLine(task.Trim());
        foreach (var w in usable)
        {
            sb.AppendLine();
            sb.AppendLine($"## Worker {w.Index + 1}: {w.Piece.Task.Trim()}");
            sb.AppendLine("Report: " + w.Report.Trim());
            sb.AppendLine("```diff");
            sb.AppendLine(w.Patch.Length > 12000 ? w.Patch[..12000] + "\n… (diff cut)" : w.Patch.TrimEnd());
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("Check that the pieces fit together (signatures, names, call sites) and do what the task asks. Your FIRST line "
            + "must be exactly APPROVE or REJECT, then the reasons (file:line).");
        return sb.ToString();
    }

    /// <summary>The reviewer's verdict: approved only when its first word is APPROVE.</summary>
    public static Review ParseReview(string? text)
    {
        string t = (text ?? "").Trim().TrimStart('*', '#', ' ', '`');
        bool ok = t.StartsWith("APPROVE", StringComparison.OrdinalIgnoreCase);
        return new Review(ok, (text ?? "").Trim());
    }

    public static async Task<Result> RunAsync(string runFolder, string task, IReadOnlyList<DispatchPolicy.WorkPiece> pieces,
        string context, string repoMap, int maxConcurrency,
        Func<WorkerContext, CancellationToken, Task<string>> runWorker,
        Func<string, CancellationToken, Task<string>>? runReviewer,
        Action<Event>? onEvent, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (DispatchPolicy.CheckPieces(pieces) is { } bad)
            return new Result(false, Array.Empty<WorkerOutcome>(), null, Array.Empty<string>(), bad, clock.Elapsed);

        var boxes = new WorktreeSandbox?[pieces.Count];
        var outcomes = new WorkerOutcome[pieces.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        try
        {
            for (int i = 0; i < pieces.Count; i++) boxes[i] = await WorktreeSandbox.CreateAsync(runFolder, $"worker{i + 1}", ct);

            async Task One(int i)
            {
                await gate.WaitAsync(ct);
                var sw = Stopwatch.StartNew();
                try
                {
                    onEvent?.Invoke(new Event(i, "start", pieces[i].Task));
                    string prompt = BuildPrompt(pieces[i], i, pieces.Count, context, repoMap);
                    string report = await runWorker(new WorkerContext(i, pieces[i], boxes[i]!.WorkingDirectory, prompt), ct);
                    // Paths in the diff are repository-relative; a piece's list is relative to the run folder.
                    string sub = System.IO.Path.GetRelativePath(boxes[i]!.Root, boxes[i]!.WorkingDirectory).Replace('\\', '/');
                    string Rel(string p) => sub == "." ? p : p.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase) ? p[(sub.Length + 1)..] : "../" + p;
                    var changed = (await boxes[i]!.ChangedFilesAsync(ct)).Select(Rel).ToList();
                    var outside = DispatchPolicy.OutsideFiles(changed, pieces[i].Files);
                    string patch = await boxes[i]!.DiffAsync(ct);
                    outcomes[i] = new WorkerOutcome(i, pieces[i], report, changed, outside, patch, null, sw.Elapsed);
                    onEvent?.Invoke(new Event(i, outside.Count > 0 ? "failed" : "done",
                        outside.Count > 0 ? "changed files outside its piece: " + string.Join(", ", outside) : string.Join(", ", changed)));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    outcomes[i] = new WorkerOutcome(i, pieces[i], "", Array.Empty<string>(), Array.Empty<string>(), "", ex.Message, sw.Elapsed);
                    onEvent?.Invoke(new Event(i, "failed", ex.Message));
                }
                finally { gate.Release(); }
            }

            await Task.WhenAll(Enumerable.Range(0, pieces.Count).Select(One));
            var usable = outcomes.Where(o => o.Usable).ToList();
            if (usable.Count == 0)
                return new Result(false, outcomes, null, Array.Empty<string>(), Describe(outcomes, null, Array.Empty<string>(), "no worker produced a usable change"), clock.Elapsed);

            Review? review = null;
            if (runReviewer != null)
            {
                onEvent?.Invoke(new Event(-1, "review", $"{usable.Count} diff(s)"));
                review = ParseReview(await runReviewer(BuildReviewPrompt(task, usable), ct));
                onEvent?.Invoke(new Event(-1, review.Approved ? "approved" : "rejected", FirstLine(review.Notes)));
                if (!review.Approved)
                    return new Result(false, outcomes, review, Array.Empty<string>(), Describe(outcomes, review, Array.Empty<string>(), "the reviewer rejected the combined diff — nothing was merged"), clock.Elapsed);
            }

            var applied = new List<string>();
            var errors = new List<string>();
            foreach (var w in usable)
            {
                string? err = await WorktreeSandbox.ApplyAsync(runFolder, w.Patch, ct);
                if (err == null) applied.AddRange(w.ChangedFiles);
                else errors.Add($"worker {w.Index + 1}: {err}");
            }
            string note = errors.Count == 0 ? "merged" : "partly merged — " + string.Join("; ", errors);
            return new Result(applied.Count > 0, outcomes, review, applied, Describe(outcomes, review, applied, note), clock.Elapsed);
        }
        finally
        {
            foreach (var b in boxes) if (b != null) await b.DisposeAsync();
        }
    }

    private static string Describe(IReadOnlyList<WorkerOutcome> all, Review? review, IReadOnlyList<string> applied, string outcome)
    {
        var sb = new StringBuilder($"Workers: {outcome}.\n");
        foreach (var w in all)
        {
            sb.Append($"- worker {w.Index + 1} ({string.Join(", ", w.Piece.Files)}): ");
            if (w.Error != null) sb.Append("failed — " + w.Error);
            else if (w.OutsideFiles.Count > 0) sb.Append("dropped — it changed files outside its piece: " + string.Join(", ", w.OutsideFiles));
            else if (w.Patch.Length == 0) sb.Append("changed nothing");
            else sb.Append($"changed {string.Join(", ", w.ChangedFiles)} in {w.Took.TotalSeconds:0}s");
            string r = w.Report.Trim();
            if (r.Length > 0) sb.Append(" · report: " + (r.Length > 400 ? r[..400] + "…" : r));
            sb.AppendLine();
        }
        if (review != null) sb.AppendLine("Review: " + (review.Notes.Length > 800 ? review.Notes[..800] + "…" : review.Notes));
        if (applied.Count > 0) sb.Append("Now in the project: " + string.Join(", ", applied.Distinct()) + ". Build and test it before you finish.");
        return sb.ToString().TrimEnd();
    }

    private static string FirstLine(string s)
    {
        string l = (s ?? "").Trim().Split('\n')[0].Trim();
        return l.Length > 140 ? l[..140] + "…" : l;
    }
}
