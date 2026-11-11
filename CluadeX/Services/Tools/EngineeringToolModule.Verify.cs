using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Helpers;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>verify_change: a build from the latest source, fresh binaries, the tests, the baseline, and the gaps.</summary>
public sealed partial class EngineeringToolModule
{
    /// <summary>What one verify found, kept per project as the baseline of the next one (only a run that passed).</summary>
    public sealed record VerifyRecord(DateTime TimeUtc, string? Commit, int DirtyFiles, int? Warnings, string? TestCommand,
        int? Passed, int? Failed, int? Skipped, List<string> Failing);

    /// <summary>A test run's counts, read from the runner's output. Null counts = the runner's summary was not found.</summary>
    public sealed record TestCounts(int? Passed, int? Failed, int? Skipped, List<string> Failing);

    // "  App -> C:\…\bin\Debug\net8.0\App.dll" — MSBuild's line for each project it built.
    private static readonly Regex BuiltBinary = new(@"^\s*(?<name>[^\s>]+)\s+->\s+(?<path>(?:[A-Za-z]:[\\/]|\\\\)[^\r\n]+?\.(?:dll|exe))\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex WarningCount = new(@"^\s*(\d+)\s+Warning\(s\)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".resx", ".razor", ".cshtml",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".rs", ".go", ".py", ".java", ".kt", ".c", ".cc", ".cpp", ".h", ".hpp", ".swift",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "target", "dist", "build", "out", ".idea", "packages", "__pycache__",
    };

    private async Task<ToolResult> VerifyAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        var tools = _tools();
        if (tools == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string root = ctx.WorkingDirectory!;
        var sb = new StringBuilder();
        var gaps = new List<string>();
        string? command = Arg(call, "command") is { Length: > 0 } c ? c : null;
        int timeoutMs = TimeoutMsOf(call);
        bool testsInBackground = IsTrue(Arg(call, "tests_background"));

        // 1. Build from the current source. A locked output is rebuilt into scratch — nothing is killed.
        var build = await tools.RunBuildCoreAsync(call, command, scratch: false, ct, timeoutMs);
        var lockedFirst = build.Locked;
        if (!build.Result.Success && build.Locked.Count > 0 && LockedFiles.AcceptsOutDir(build.Command))
        {
            sb.AppendLine("1. Build: the normal output is LOCKED by a running program — "
                + string.Join("; ", build.Locked.Select(l => $"{Path.GetFileName(l.Path)} held by "
                    + (l.Holders.Count == 0 ? "an unnamed process" : string.Join(", ", l.Holders.Select(h => $"PID {h.Pid} ({h.Name})")))))
                + ". Nothing was killed; rebuilt into a scratch folder instead.");
            build = await tools.RunBuildCoreAsync(call, command, scratch: true, ct, timeoutMs);
        }
        if (!build.Result.Success)
        {
            sb.AppendLine($"VERDICT: FAILED — the build did not succeed.");
            sb.AppendLine();
            sb.AppendLine(build.Result.Output.Length > 0 ? build.Result.Output : build.Result.Error);
            sb.AppendLine();
            sb.AppendLine("NOT VERIFIED: everything after the build — no binary was checked and no test ran.");
            var failed = Fail(call, sb.ToString().TrimEnd());
            failed.Summary = "verify: FAILED (build)";
            return failed;
        }
        string buildLine = build.ScratchDir != null ? $"OK (into scratch {build.ScratchDir})" : "OK";
        int? warnings = SumMatches(WarningCount, build.RawOutput);
        if (lockedFirst.Count == 0) sb.AppendLine($"1. Build: {buildLine} — $ {build.Command}{(warnings is int w ? $" — {w} warning(s)" : "")}");
        else sb.AppendLine($"   Build: {buildLine}{(warnings is int w2 ? $" — {w2} warning(s)" : "")}");

        // 2. Is what will be tested or run as new as the source?
        var checks = new List<(string Path, string Label, string? Name)>();
        foreach (Match m in BuiltBinary.Matches(build.RawOutput))
            checks.Add((m.Groups["path"].Value.Trim(), "built", m.Groups["name"].Value));
        if (build.ScratchDir != null)
            foreach (var l in lockedFirst)
                if (l.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || l.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    checks.Add((l.Path, "normal output, locked", null));
        if (Arg(call, "binary") is { Length: > 0 } named)
        {
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(named) ? named : Path.Combine(root, named)); }
            catch { full = named; }
            checks.Add((full, "named", null));
        }
        var stale = new List<string>();
        var projectDirs = new List<string>();
        sb.AppendLine("2. Binaries:");
        if (checks.Count == 0)
        {
            sb.AppendLine("   (the build named no output binary — not a .NET build, or nothing was built)");
            gaps.Add("No binary was checked for staleness (the build output names none). Pass 'binary' to check the file you will run or load.");
        }
        foreach (var (path, label, name) in checks.DistinctBy(x => x.Path.ToLowerInvariant()))
        {
            string shown = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Rel(root, path) : path;
            if (!File.Exists(path)) { sb.AppendLine($"   {shown} [{label}]: MISSING"); stale.Add(shown); continue; }
            string srcDir = SourceDirFor(path, name, root);
            projectDirs.Add(srcDir);
            var newer = NewerSources(srcDir, File.GetLastWriteTimeUtc(path), build.ScratchDir);
            string where = Rel(root, srcDir) is "." ? "the project" : Rel(root, srcDir) + "/";
            if (newer.Count == 0)
                sb.AppendLine($"   {shown} [{label}]: fresh ({File.GetLastWriteTime(path):HH:mm:ss}, no source in {where} is newer)");
            else
            {
                stale.Add(shown);
                sb.AppendLine($"   {shown} [{label}]: STALE — {newer.Count} source file(s) changed after it was written: "
                    + string.Join(", ", newer.Take(5).Select(f => Rel(root, f))) + (newer.Count > 5 ? ", …" : ""));
            }
        }

        // 3. Tests.
        string testsMode = Arg(call, "tests").ToLowerInvariant();
        string? testCommand = Arg(call, "test_command") is { Length: > 0 } tc ? tc : tools.DetectedProjectCommand(test: true);
        TestCounts? counts = null;
        bool testsFailed = false;
        string? testsPending = null, testsNotStarted = null;
        if (testsMode == "skip")
        {
            sb.AppendLine("3. Tests: skipped on request");
            gaps.Add("Tests were skipped (tests=\"skip\").");
        }
        else if (testCommand == null)
        {
            sb.AppendLine("3. Tests: no test runner found in the project");
            gaps.Add("No automated test ran — the project has no test runner CluadeX can find (pass test_command if it has one).");
        }
        else if (testsInBackground)
        {
            if (build.ScratchDir != null && LockedFiles.AcceptsOutDir(testCommand))
                testCommand = LockedFiles.WithOutDir(testCommand, build.ScratchDir);
            (testsPending, testsNotStarted) = await StartTestsInBackgroundAsync(tools, call, testCommand, root, ct);
            if (testsPending != null)
            {
                sb.AppendLine($"3. Tests: RUNNING in the background as {testsPending} — $ {testCommand}");
                sb.AppendLine("   Its output is live on CluadeX's Background page. When it ends, its exit code and last lines come to "
                              + "this chat as a turn of their own: end your turn now with one short line saying you are waiting for "
                              + "it. Do not poll, sleep or start it again.");
                gaps.Add("The tests have not finished yet: nothing about them is proven until their result arrives.");
            }
            else
            {
                testsFailed = true;
                sb.AppendLine($"3. Tests: NOT STARTED in the background — {testsNotStarted} — $ {testCommand}");
            }
        }
        else
        {
            if (build.ScratchDir != null && LockedFiles.AcceptsOutDir(testCommand))
                testCommand = LockedFiles.WithOutDir(testCommand, build.ScratchDir);
            var (test, raw) = await tools.RunTestsCoreAsync(call, testCommand, ct, timeoutMs);
            counts = ParseTestCounts(raw);
            testsFailed = !test.Success;
            string tally = counts.Passed != null || counts.Failed != null
                ? $"{counts.Passed ?? 0} passed, {counts.Failed ?? 0} failed{(counts.Skipped is > 0 ? $", {counts.Skipped} skipped" : "")}"
                : "counts not found in the runner's output";
            sb.AppendLine($"3. Tests: {(test.Success ? "PASSED" : "FAILED")} — {tally} — $ {testCommand}");
            if (!test.Success)
                sb.AppendLine("   " + OutputBudget.HeadAndTail(test.Output.Length > 0 ? test.Output : test.Error, 6000).Replace("\n", "\n   "));
            // Stopped at the limit, not failed: a long harness gets the time it needs, or runs on in the background.
            if (test.Error == "Tests timed out")
                sb.AppendLine($"   The run was STOPPED at the {timeoutMs / 60_000} min limit before it finished — that is not a test "
                              + $"failure. Run it again with timeout_minutes (up to {MaxVerifyMinutes}) or tests_background=\"true\".");
            if (counts.Skipped is > 0) gaps.Add($"{counts.Skipped} test(s) were skipped.");
            if (test.Success && counts.Passed is 0) gaps.Add("The test runner ran but reported 0 passed tests.");
        }

        // 4. Baseline: the last verify of this project that passed.
        string? commit = (await RunProcessAsync("git", new[] { "rev-parse", "--short", "HEAD" }, root, 10_000, ct)).Text.Trim();
        if (commit is { Length: 0 } || commit?.Contains(' ') == true) commit = null;
        var dirty = await ChangedFilesAsync(root, ct);
        var baseline = LoadBaseline(root);
        sb.AppendLine("4. Baseline: " + CompareBaseline(baseline, warnings, testCommand, counts));

        // 5. What this did not prove.
        if (dirty != null)
        {
            var outside = dirty.Where(f => SourceExtensions.Contains(Path.GetExtension(f))
                    && projectDirs.Count > 0
                    && !projectDirs.Any(d => (Path.GetFullPath(Path.Combine(root, f))).StartsWith(d.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (outside.Count > 0)
                gaps.Add($"{outside.Count} changed source file(s) are outside every project this build produced, so nothing here compiled them: "
                    + string.Join(", ", outside.Take(6)) + (outside.Count > 6 ? ", …" : ""));
        }
        else gaps.Add("Not a git repository (or git is missing): the changed files could not be listed.");
        if (build.ScratchDir != null)
            gaps.Add($"The build went to the scratch folder {build.ScratchDir}: whatever loads the normal output (the running app, a harness that links it) still runs the OLD code until it is closed and rebuilt normally.");
        gaps.Add("Nothing here ran the app itself: UI, runtime and integration behaviour are unchecked unless a test covers them.");

        string verdict = testsNotStarted != null ? "FAILED — the tests could not start in the background"
            : testsFailed ? "FAILED — tests failed"
            : stale.Count > 0 ? "STALE — a binary is older than its source, so testing or running it checks old code"
            : testsPending != null ? "BUILT — the tests are still running in the background; their result comes in a later turn"
            : gaps.Count > 1 ? "VERIFIED WITH GAPS"
            : "VERIFIED";
        bool success = !testsFailed && stale.Count == 0;
        if (success && testsPending == null)
            SaveBaseline(root, new VerifyRecord(DateTime.UtcNow, commit, dirty?.Count ?? 0, warnings, testCommand,
                counts?.Passed, counts?.Failed, counts?.Skipped, counts?.Failing ?? new List<string>()));

        var report = new StringBuilder();
        report.AppendLine($"VERDICT: {verdict}");
        report.AppendLine();
        report.Append(sb);
        report.AppendLine();
        report.AppendLine("NOT VERIFIED:");
        foreach (var g in gaps) report.AppendLine("  - " + g);
        if (stale.Count > 0)
            report.AppendLine("Do not report this as done: rebuild so the stale binary is replaced (close what locks it), then verify again.");

        var result = success ? Ok(call, report.ToString().TrimEnd(), "verify: " + verdict.Split(' ')[0] + (gaps.Count > 1 ? " (gaps)" : ""))
            : Fail(call, report.ToString().TrimEnd());
        if (!success) result.Summary = "verify: " + verdict.Split(' ')[0];
        return result;
    }

    /// <summary>The longest verify_change may wait for its build or tests (timeout_minutes).</summary>
    public const int MaxVerifyMinutes = 120;

    /// <summary>timeout_minutes as milliseconds (1 to <see cref="MaxVerifyMinutes"/>); the 5-minute default without it.</summary>
    public static int TimeoutMsOf(ToolCall call) =>
        int.TryParse(Arg(call, "timeout_minutes"), out int m) && m > 0
            ? Math.Clamp(m, 1, MaxVerifyMinutes) * 60_000
            : AgentToolService.DefaultRunTimeoutMs;

    /// <summary>
    /// tests_background: the test command as a background process of CluadeX's, watched for this chat (RunWatchService),
    /// after the same guards as a test run. Returns its "bg#N" label, or why it did not start.
    /// </summary>
    private static async Task<(string? Started, string? Error)> StartTestsInBackgroundAsync(AgentToolService tools, ToolCall call,
        string testCommand, string root, CancellationToken ct)
    {
        var svc = Watch.RunWatchService.Current;
        if (svc == null) return (null, "this CluadeX has no watch service, so no turn could carry the result — run them without tests_background");
        if (WatchChat() is not { } chat) return (null, "this call came from no chat, so no turn could carry the result");
        if (await tools.GateProjectCommandAsync(testCommand) is { } refused) return (null, refused);
        var r = await svc.StartCommandAsync(chat, testCommand, root, "verify_change tests", "verify_change tests: " + testCommand,
            Minutes(call), ct);
        if (r.Job == null) return (null, r.Error ?? "the background run did not start");
        return ($"bg#{r.Job.ProcessId} (watch {r.Job.Id})", null);
    }

    private static int? SumMatches(Regex re, string text)
    {
        int? sum = null;
        foreach (Match m in re.Matches(text ?? ""))
            if (int.TryParse(m.Groups[1].Value, out int n)) sum = (sum ?? 0) + n;
        return sum;
    }

    /// <summary>The folder whose sources a binary is built from: the project named <paramref name="name"/> in the
    /// workspace, else the nearest folder above the binary that holds a project file, else the workspace.</summary>
    private static string SourceDirFor(string binary, string? name, string root)
    {
        if (!string.IsNullOrEmpty(name))
            foreach (var ext in new[] { ".csproj", ".fsproj", ".vbproj" })
            {
                var found = FindFile(root, name + ext, depth: 6);
                if (found != null) return Path.GetDirectoryName(found)!;
            }
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(binary));
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            while (dir != null && dir.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFiles(dir, "*.*proj").Any() || File.Exists(Path.Combine(dir, "package.json"))
                    || File.Exists(Path.Combine(dir, "Cargo.toml")) || File.Exists(Path.Combine(dir, "go.mod")))
                    return dir;
                if (string.Equals(dir.TrimEnd('\\', '/'), r, StringComparison.OrdinalIgnoreCase)) break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* fall back to the workspace */ }
        return root;
    }

    private static string? FindFile(string dir, string fileName, int depth)
    {
        try
        {
            string direct = Path.Combine(dir, fileName);
            if (File.Exists(direct)) return direct;
            if (depth <= 0) return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (SkippedFolders.Contains(Path.GetFileName(sub))) continue;
                if (FindFile(sub, fileName, depth - 1) is { } hit) return hit;
            }
        }
        catch { /* unreadable folder */ }
        return null;
    }

    /// <summary>Source files under <paramref name="dir"/> written after <paramref name="sinceUtc"/>, newest first.</summary>
    private static List<string> NewerSources(string dir, DateTime sinceUtc, string? skip)
    {
        var newer = new List<(string File, DateTime Time)>();
        var stack = new Stack<string>();
        stack.Push(dir);
        int visited = 0;
        while (stack.Count > 0 && visited < 20_000)
        {
            string d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    visited++;
                    if (!SourceExtensions.Contains(Path.GetExtension(f))) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > sinceUtc) newer.Add((f, t));
                }
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    if (SkippedFolders.Contains(Path.GetFileName(sub))) continue;
                    if (skip != null && sub.StartsWith(skip.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(sub);
                }
            }
            catch { /* unreadable folder */ }
        }
        return newer.OrderByDescending(x => x.Time).Select(x => x.File).ToList();
    }

    /// <summary>Uncommitted files (git status), as project-relative paths; null when git cannot say.</summary>
    private async Task<List<string>?> ChangedFilesAsync(string root, CancellationToken ct)
    {
        var r = await RunProcessAsync("git", new[] { "-c", "core.fsmonitor=false", "status", "--porcelain", "--untracked-files=all" }, root, 15_000, ct);
        if (r.ExitCode != 0) return null;
        var list = new List<string>();
        foreach (var line in r.Text.Split('\n'))
        {
            if (line.Length < 4) continue;
            string path = line[3..].Trim().Trim('"');
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            list.Add(path.Replace('\\', '/'));
        }
        return list;
    }

    // ── Test counts ──

    private static readonly Regex DotnetSummary = new(@"(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex DotnetFailed = new(@"^\s*Failed\s+(?<name>[\w.<>`,\[\] ]+?)\s+\[[^\]]*\]\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex CargoSummary = new(@"test result:\s*\w+\.\s*(\d+)\s+passed;\s*(\d+)\s+failed;\s*(\d+)\s+ignored", RegexOptions.Compiled);
    private static readonly Regex CargoFailed = new(@"^test\s+(?<name>\S+)\s+\.\.\.\s+FAILED", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PytestFailed = new(@"^FAILED\s+(?<name>\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex GoFailed = new(@"^\s*--- FAIL:\s+(?<name>\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex JestFailed = new(@"^\s*[✕×]\s+(?<name>.+?)(?:\s+\(\d+\s*m?s\))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex FlutterSummary = new(@"\+(?<p>\d+)(?: ~(?<s>\d+))?(?: -(?<f>\d+))?: (?:All tests passed|Some tests failed)", RegexOptions.Compiled);
    // A project harness's own line: "  FAIL name   detail" (apitest, chattest, tooltest …) — the name ends at a wide gap.
    private static readonly Regex HarnessFailed = new(@"^\s*FAIL\s+(?<name>\S[^\n]*?)(?:\s{2,}|\s*\[|\s*$)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Passed / failed / skipped and the failing tests' names from a runner's output (dotnet test, pytest,
    /// jest/vitest, cargo, go test). Counts are null when no summary is found.</summary>
    public static TestCounts ParseTestCounts(string raw)
    {
        raw = (raw ?? "").Replace("\r\n", "\n");
        int? passed = null, failed = null, skipped = null;
        void Add(ref int? slot, string v) { if (int.TryParse(v, out int n)) slot = (slot ?? 0) + n; }

        var dotnet = DotnetSummary.Matches(raw);
        var cargo = CargoSummary.Matches(raw);
        if (dotnet.Count > 0)
            foreach (Match m in dotnet) { Add(ref failed, m.Groups[1].Value); Add(ref passed, m.Groups[2].Value); Add(ref skipped, m.Groups[3].Value); }
        else if (cargo.Count > 0)
            foreach (Match m in cargo) { Add(ref passed, m.Groups[1].Value); Add(ref failed, m.Groups[2].Value); Add(ref skipped, m.Groups[3].Value); }
        else if (FlutterSummary.Matches(raw) is { Count: > 0 } flutter)
        {
            // flutter/dart test's last progress line: "00:04 +12 ~1 -2: Some tests failed."
            var m = flutter[^1];
            Add(ref passed, m.Groups["p"].Value);
            if (m.Groups["s"].Success) Add(ref skipped, m.Groups["s"].Value);
            failed = 0;
            if (m.Groups["f"].Success) Add(ref failed, m.Groups["f"].Value);
        }
        else
        {
            // pytest "=== 3 failed, 10 passed, 1 skipped in 0.5s ===", jest/vitest "Tests: 1 failed, 5 passed, 6 total",
            // a project harness "36 passed, 0 failed" / "═══ 36 pass · 0 FAIL ═══" / "## Result: 33 pass / 0 FAIL".
            var line = raw.Split('\n').LastOrDefault(l =>
                Regex.IsMatch(l, @"^\s*(?:=+.*\bin\s+[\d.]+s|Tests?:\s+.*\btotal\b|.*\d+\s+(?:passed|failed)\b|.*\d+\s+pass\b.*\d+\s+fail\b)", RegexOptions.IgnoreCase));
            if (line != null)
            {
                foreach (Match m in Regex.Matches(line, @"(\d+)\s+(passed|pass|failed|fail|skipped|errors?)\b", RegexOptions.IgnoreCase))
                    switch (m.Groups[2].Value.ToLowerInvariant())
                    {
                        case "passed" or "pass": Add(ref passed, m.Groups[1].Value); break;
                        case "skipped": Add(ref skipped, m.Groups[1].Value); break;
                        default: Add(ref failed, m.Groups[1].Value); break;
                    }
            }
        }

        var failing = new List<string>();
        foreach (var re in new[] { DotnetFailed, CargoFailed, PytestFailed, GoFailed, JestFailed, HarnessFailed })
            foreach (Match m in re.Matches(raw))
            {
                string n = m.Groups["name"].Value.Trim();
                if (n.Length > 0 && !failing.Contains(n)) failing.Add(n);
                if (failing.Count >= 50) break;
            }
        return new TestCounts(passed, failed, skipped, failing);
    }

    // ── Baseline ──

    private string BaselinePath(string root)
    {
        string dataRoot = _settings?.DataRoot is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "cluadex");
        return Path.Combine(dataRoot, "verify-baselines", MemoryService.ProjectSlug(root) + ".json");
    }

    private VerifyRecord? LoadBaseline(string root)
    {
        try
        {
            string p = BaselinePath(root);
            return File.Exists(p) ? JsonSerializer.Deserialize<VerifyRecord>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    private void SaveBaseline(string root, VerifyRecord record)
    {
        try
        {
            string p = BaselinePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(record));
        }
        catch { /* a missing baseline only means no comparison next time */ }
    }

    private static string CompareBaseline(VerifyRecord? b, int? warnings, string? testCommand, TestCounts? now)
    {
        if (b == null) return "none yet for this project — this run becomes the baseline if it passes.";
        var sb = new StringBuilder($"last passing verify {b.TimeUtc.ToLocalTime():d MMM HH:mm}"
            + (b.Commit != null ? $" at {b.Commit}" : "") + (b.DirtyFiles > 0 ? $" (+{b.DirtyFiles} uncommitted)" : "") + ": ");
        var parts = new List<string>();
        if (warnings is int w && b.Warnings is int bw && w != bw) parts.Add($"warnings {bw} → {w}");
        if (now != null && string.Equals(b.TestCommand, testCommand, StringComparison.Ordinal))
        {
            if (now.Passed is int p && b.Passed is int bp && p != bp) parts.Add($"passed {bp} → {p}");
            if (now.Failed is int f && (b.Failed ?? 0) != f) parts.Add($"failed {b.Failed ?? 0} → {f}");
            var added = now.Failing.Except(b.Failing).ToList();
            var fixedOnes = b.Failing.Except(now.Failing).ToList();
            if (added.Count > 0) parts.Add("NEW failures: " + string.Join(", ", added.Take(8)));
            if (fixedOnes.Count > 0) parts.Add("now passing: " + string.Join(", ", fixedOnes.Take(8)));
        }
        else if (now != null) parts.Add("tests ran with a different command than the baseline, so their counts are not compared");
        sb.Append(parts.Count == 0 ? "no difference." : string.Join("; ", parts) + ".");
        return sb.ToString();
    }
}
