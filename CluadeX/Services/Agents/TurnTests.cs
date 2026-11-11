using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Tools;

namespace CluadeX.Services.Agents;

/// <summary>How a failing test is read at the end of the turn.</summary>
public enum TestFailureKind
{
    /// <summary>It still fails and it touches what this turn changed (or nothing says otherwise): a real failure.</summary>
    Real,
    /// <summary>It failed, then a rerun of the same suite passed with no file changed in between.</summary>
    Flaky,
    /// <summary>It still fails, but it names none of the files this turn changed and none of them names it.</summary>
    Unrelated,
    /// <summary>It failed, files were changed, and a later run of the same suite passed: fixed in this turn.</summary>
    Fixed,
}

/// <summary>One test run the turn made: a verify_change, run_tests, run_build or a shell command that runs a test runner.</summary>
public sealed record TestRunRecord(string Suite, string Command, int? Passed, int? Failed, int? Skipped, List<string> Failing,
    bool Success, double? Seconds, bool Pending, int EditsBefore, string Tool);

/// <summary>One suite's line on the card: its last run, and how many times it ran.</summary>
public sealed record TestSuiteLine(string Suite, string Command, int? Passed, int? Failed, int? Skipped, bool Success,
    bool Pending, double? Seconds, int Runs);

/// <summary>One failing test on the card, with how it was read.</summary>
public sealed record TestFailureLine(string Suite, string Test, TestFailureKind Kind);

/// <summary>The card's data, kept in the row as JSON.</summary>
public sealed record TurnTestSummary(List<TestSuiteLine> Suites, List<TestFailureLine> Failures, List<string> Skipped, int ChangedFiles)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool AllPassed => Suites.Count > 0 && Suites.All(s => s.Success && !s.Pending);
    public int FailedSuites => Suites.Count(s => !s.Success && !s.Pending);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TurnTestSummary? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TurnTestSummary>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The per-turn test summary (owner 2026-11-11: the table Claude Code sessions end with — every suite run, its pass/fail,
/// and a word on each failure: a flake that passed on rerun, a failure that has nothing to do with the change, or a real
/// one; and the suites left out as out of scope). Every tool row of the turn passes <see cref="Note"/> — the native loop
/// and the CLI agents alike, since all of them come through the chat's one tool-result path — and the card is drawn
/// once at the end from <see cref="Summarize"/>. The card is the source of truth; the model is told to name the same
/// runs and skips in its answer. Pure: the harness drives it without the app.
/// </summary>
public sealed class TurnTests
{
    /// <summary>Failing tests the card lists per suite; more are "+N".</summary>
    public const int MaxFailuresPerSuite = 6;

    private readonly List<TestRunRecord> _runs = new();
    private readonly List<string> _skipped = new();
    private int _edits;

    public IReadOnlyList<TestRunRecord> Runs => _runs;
    public IReadOnlyList<string> Skipped => _skipped;
    public bool IsEmpty => _runs.Count == 0 && _skipped.Count == 0;

    /// <summary>A tool row of the turn. A file write counts as an edit (a later pass is then a fix, not a flake); a test
    /// run is recorded. <paramref name="seconds"/>: how long the call took (null = unknown).</summary>
    public void Note(ToolResult r, double? seconds)
    {
        if (r == null) return;
        if (r.Success && !string.IsNullOrEmpty(r.FilePath) && r.Type is not (ToolType.RunBuild or ToolType.RunTests)) { _edits++; return; }
        if (FromToolResult(r, seconds, _edits) is { } run) _runs.Add(run);
    }

    /// <summary>Tests the model said it left out as out of scope (wrap_up's tests_skipped, or its answer).</summary>
    public void NoteSkipped(IEnumerable<string>? items)
    {
        if (items == null) return;
        foreach (var raw in items)
        {
            string s = Regex.Replace(raw ?? "", @"\s+", " ").Trim().TrimStart('-', '*', '•').Trim();
            if (s.Length == 0) continue;
            if (s.Length > 160) s = s[..159].TrimEnd() + "…";
            if (!_skipped.Contains(s, StringComparer.OrdinalIgnoreCase)) _skipped.Add(s);
            if (_skipped.Count >= 12) break;
        }
    }

    // ─── reading one tool row ───

    private static readonly Regex VerifyTests = new(@"^\s*3\. Tests: (?<state>PASSED|FAILED|RUNNING|NOT STARTED)\b(?<rest>[^\n]*?)\$ (?<cmd>[^\n]+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex VerifyTally = new(@"(?<p>\d+) passed, (?<f>\d+) failed(?:, (?<s>\d+) skipped)?", RegexOptions.Compiled);
    private static readonly Regex DollarLine = new(@"^\$ (?<cmd>[^\n]+?)(?:\s{3}\(detected:[^\n]*\))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The test run in one tool row, or null when the row ran no tests. Pure.</summary>
    public static TestRunRecord? FromToolResult(ToolResult r, double? seconds, int editsBefore = 0)
    {
        if (r == null) return null;
        string name = Leaf(r.ToolName);
        string output = r.Output ?? "";
        string all = string.IsNullOrEmpty(r.Error) || output.Contains(r.Error, StringComparison.Ordinal) ? output : output + "\n" + r.Error;

        if (name is "verify_change" or "verify")
        {
            var m = VerifyTests.Match(output.Replace("\r\n", "\n"));
            if (!m.Success) return null;   // tests skipped, none found, or the build failed first
            string cmd = m.Groups["cmd"].Value.Trim();
            string state = m.Groups["state"].Value;
            if (state == "NOT STARTED") return Record(cmd, null, null, null, new(), false, seconds, false, editsBefore, name);
            if (state == "RUNNING") return Record(cmd, null, null, null, new(), true, seconds, true, editsBefore, name);
            var tally = VerifyTally.Match(m.Groups["rest"].Value);
            var failing = EngineeringToolModule.ParseTestCounts(output).Failing;
            return tally.Success
                ? Record(cmd, Int(tally.Groups["p"]), Int(tally.Groups["f"]), Int(tally.Groups["s"]), failing, state == "PASSED", seconds, false, editsBefore, name)
                : Record(cmd, null, null, null, failing, state == "PASSED", seconds, false, editsBefore, name);
        }

        string? command = CommandOf(r, output);
        bool testTool = r.Type == ToolType.RunTests || name == "run_tests";
        if (!testTool)
        {
            bool shell = r.Type is ToolType.RunCommand or ToolType.PowerShell or ToolType.RunBuild
                         || name is "run_command" or "powershell" or "bash" or "run_build" or "run_shell_command" or "shell" or "exec_command";
            if (!shell || command == null || !LooksLikeTestCommand(command)) return null;
        }
        if (string.IsNullOrWhiteSpace(command)) command = name;
        // A refused or never-run command is no test run (the gate said no, the runner is missing).
        if (!r.Success && output.Length == 0 && (r.Error ?? "").Length > 0 && !LooksLikeTestOutput(r.Error!)) return null;
        var counts = EngineeringToolModule.ParseTestCounts(all);
        bool ok = r.Success && counts.Failed is null or 0;
        return Record(command!, counts.Passed, counts.Failed, counts.Skipped, counts.Failing, ok, seconds, false, editsBefore, name);
    }

    private static TestRunRecord Record(string cmd, int? p, int? f, int? s, List<string> failing, bool ok, double? secs, bool pending,
        int edits, string tool) =>
        new(SuiteName(cmd), cmd, p, f, s, failing, ok, secs, pending, edits, tool);

    private static int? Int(Group g) => g.Success && int.TryParse(g.Value, out int n) ? n : null;

    /// <summary>"mcp__cluadex__verify_change" / "cluadex__run_tests" → the tool's own name.</summary>
    public static string Leaf(string? toolName)
    {
        string n = (toolName ?? "").Trim().ToLowerInvariant();
        int i = n.LastIndexOf("__", StringComparison.Ordinal);
        return i >= 0 ? n[(i + 2)..] : n;
    }

    private static string? CommandOf(ToolResult r, string output)
    {
        if (r.Arguments != null)
            foreach (var key in new[] { "command", "cmd", "script" })
                if (r.Arguments.TryGetValue(key, out var c) && !string.IsNullOrWhiteSpace(c)) return c.Trim();
        var m = DollarLine.Match(output.Replace("\r\n", "\n"));
        return m.Success && m.Index == 0 ? m.Groups["cmd"].Value.Trim() : null;
    }

    private static bool LooksLikeTestOutput(string text) =>
        Regex.IsMatch(text, @"\d+\s+(?:passed|pass|failed|FAIL)\b", RegexOptions.IgnoreCase);

    // ─── what counts as a test run ───

    private static readonly Regex TestCommand = new(
        @"(?:^|[\s;&|(""':/\\])(?:" +
        @"dotnet\s+test\b|dotnet\s+(?:run|exec)\b[^;&|]*?\btests?[\\/]|" +
        @"(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?test(?::[\w-]+)?\b|npx\s+(?:jest|vitest|mocha|playwright\s+test)\b|(?:jest|vitest|mocha)\b|" +
        @"pytest\b|py\.test\b|python[\d.]*\s+-m\s+(?:pytest|unittest)\b|tox\b|nox\b|" +
        @"go\s+test\b|cargo\s+(?:test|nextest)\b|flutter\s+test\b|dart\s+test\b|" +
        @"php\s+artisan\s+test\b|(?:vendor[\\/]bin[\\/])?(?:phpunit|pest)\b|" +
        @"mvn\w*\s+(?:[\w:-]+\s+)*(?:test|verify)\b|gradlew?(?:\.bat)?\s+(?:[\w:-]+\s+)*test\b|" +
        @"rspec\b|bundle\s+exec\s+rspec\b|rake\s+test\b|mix\s+test\b|swift\s+test\b|ctest\b|deno\s+test\b|" +
        @"[\w.\\/-]*try-branch\.ps1\b[^;&|]*-Smoke\b|" +
        @"[A-Z]+TEST_ONLY\s*=" +
        @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Does this shell command run a test runner or a project's test harness? Pure.</summary>
    public static bool LooksLikeTestCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string c = command.Trim();
        // installing or listing is not running: "npm install jest", "dotnet test --list-tests", "pip install pytest"
        if (Regex.IsMatch(c, @"\b(?:install|add|uninstall|remove)\s+[\w@/.-]*(?:jest|vitest|pytest|mocha|phpunit|pest)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(c, @"--list-tests\b|--collect-only\b|--help\b", RegexOptions.IgnoreCase)) return false;
        return TestCommand.IsMatch(c);
    }

    private static readonly Regex HarnessDir = new(@"(?:^|[\s""'=\\/])tests?[\\/](?<name>[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OnlyEnv = new(@"(?<![A-Za-z])(?<var>[A-Z]+)TEST_ONLY\s*=\s*['""]?(?<v>[\w,.-]+)", RegexOptions.Compiled);

    /// <summary>A short name for the suite a command runs: "apitest --offline", "chattest (TT)", "dotnet test App.sln". Pure.</summary>
    public static string SuiteName(string command)
    {
        string c = Regex.Replace((command ?? "").Trim(), @"\s+", " ");
        // a leading "cd x &&" / "Set-Location x;" says where, not what
        c = Regex.Replace(c, @"^(?:cd|Set-Location|pushd)\s+(?:/d\s+)?(?:""[^""]*""|'[^']*'|\S+)\s*(?:&&|;)\s*", "", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(c, @"try-branch\.ps1", RegexOptions.IgnoreCase)) return "try-branch -Smoke";
        var only = OnlyEnv.Match(c);
        var dir = HarnessDir.Match(c);
        if (dir.Success && !Regex.IsMatch(c, @"\bdotnet\s+test\b", RegexOptions.IgnoreCase))
        {
            string name = dir.Groups["name"].Value.Trim('.', '"', '\'');
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
            int dashes = c.IndexOf(" -- ", StringComparison.Ordinal);
            string args = dashes >= 0 ? c[(dashes + 4)..].Trim() : "";
            args = StripRedirects(Regex.Replace(args, @"\s*\|.*$", ""));
            if (args.Length > 28) args = args[..27] + "…";
            string suite = name + (args.Length > 0 ? " " + args : "");
            if (only.Success) suite += $" ({only.Groups["v"].Value})";
            return suite;
        }
        if (only.Success) return (only.Groups["var"].Value.ToLowerInvariant() + "test") + $" ({only.Groups["v"].Value})";
        // what the output is piped through or redirected to is not part of the suite
        c = StripRedirects(Regex.Replace(c, @"\s*\|\s*(?:tail|head|Select-Object|findstr|grep|tee|more|Out-String)\b.*$", "", RegexOptions.IgnoreCase));
        return c.Length > 48 ? c[..47] + "…" : c;
    }

    private static string StripRedirects(string s) => Regex.Replace(s, @"(?:\s*(?:2>&1|\*>&1|2>\s*\S+|>\s*\S+))+\s*$", "").Trim();

    // ─── the summary ───

    /// <summary>
    /// The card's data: one line per suite (its last run, how many times it ran), one line per failing test read as
    /// <see cref="TestFailureKind"/>, and the skipped list. <paramref name="changedFiles"/>: the files this turn wrote
    /// (full or project-relative paths); <paramref name="readFile"/>: their text, to see whether one of them names a
    /// failing test (null = do not look). Pure apart from what <paramref name="readFile"/> does.
    /// </summary>
    public TurnTestSummary Summarize(IReadOnlyCollection<string> changedFiles, Func<string, string?>? readFile = null)
    {
        var suites = new List<TestSuiteLine>();
        var failures = new List<TestFailureLine>();
        var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Text(string f)
        {
            if (readFile == null) return null;
            if (!texts.TryGetValue(f, out var t)) texts[f] = t = SafeRead(readFile, f);
            return t;
        }

        foreach (var group in _runs.GroupBy(r => Key(r.Command)))
        {
            var runs = group.ToList();
            var last = runs[^1];
            suites.Add(new TestSuiteLine(last.Suite, last.Command, last.Passed, last.Failed, last.Skipped, last.Success, last.Pending,
                last.Seconds, runs.Count));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                var names = run.Failing.Count > 0 ? run.Failing
                    : !run.Success && !run.Pending ? new List<string> { "" } : new List<string>();
                foreach (var test in names)
                {
                    if (!seen.Add(test)) continue;
                    // the first later run of this suite in which the test no longer fails
                    var cleared = runs.Skip(i + 1).FirstOrDefault(l => !l.Pending
                        && (l.Success || (test.Length > 0 && l.Failing.Count > 0 && !l.Failing.Contains(test))));
                    TestFailureKind kind;
                    if (cleared != null) kind = cleared.EditsBefore > run.EditsBefore ? TestFailureKind.Fixed : TestFailureKind.Flaky;
                    else kind = test.Length > 0 && changedFiles.Count > 0 && !Touches(test, changedFiles, Text)
                        ? TestFailureKind.Unrelated : TestFailureKind.Real;
                    failures.Add(new TestFailureLine(last.Suite, test, kind));
                }
            }
        }
        // the list keeps the worst first: real, then unrelated, flaky, fixed; a suite's own order within each
        failures = failures.Select((f, i) => (f, i)).OrderBy(x => Rank(x.f.Kind)).ThenBy(x => x.i).Select(x => x.f).ToList();
        return new TurnTestSummary(suites, failures, _skipped.ToList(), changedFiles.Count);
    }

    private static int Rank(TestFailureKind k) => k switch
    {
        TestFailureKind.Real => 0, TestFailureKind.Unrelated => 1, TestFailureKind.Flaky => 2, _ => 3,
    };

    /// <summary>Two runs are the same suite when their commands match, spacing and case aside.</summary>
    private static string Key(string command) => Regex.Replace((command ?? "").Trim(), @"\s+", " ").ToLowerInvariant();

    private static string? SafeRead(Func<string, string?> read, string path)
    {
        try { var t = read(path); return t is { Length: > 600_000 } ? t[..600_000] : t; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // Stems too common to say anything about a test ("Program", "index", "test").
    private static readonly HashSet<string> CommonStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "program", "index", "main", "app", "test", "tests", "spec", "util", "utils", "helper", "helpers", "common", "core",
        "readme", "changelog", "package", "config", "settings", "types", "model", "models", "base", "init", "__init__",
    };

    /// <summary>
    /// Does the failing test touch what the turn changed? Yes when its name holds a changed file's name ("LoginTests" and
    /// Login.xaml, "AuthService_Rejects…" and AuthService.cs), or a changed file names the test (the turn edited the test
    /// itself). Without that, nothing ties the two together. Pure apart from <paramref name="text"/>.
    /// </summary>
    public static bool Touches(string test, IEnumerable<string> changedFiles, Func<string, string?>? text = null)
    {
        if (string.IsNullOrWhiteSpace(test)) return true;
        string t = test.ToLowerInvariant();
        string leaf = LeafName(test);
        foreach (var file in changedFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(file ?? "");
            foreach (var part in stem.Split('.', '-', '_').Prepend(stem))
            {
                string p = Regex.Replace(part, @"(?i)(?:tests?|specs?)$", "");
                if (p.Length >= 4 && !CommonStems.Contains(p) && t.Contains(p.ToLowerInvariant(), StringComparison.Ordinal)) return true;
            }
            if (leaf.Length >= 4 && text?.Invoke(file!) is { } body && body.Contains(leaf, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>"Ns.Class.Method(x: 1)" → "Method"; "tests/a.py::test_b[1]" → "test_b"; "suite › case" → "case"; a harness
    /// line's name stays whole.</summary>
    public static string LeafName(string test)
    {
        string s = (test ?? "").Trim();
        s = Regex.Replace(s, @"[\(\[].*$", "").Trim();
        foreach (var sep in new[] { "::", " › ", " > ", "/" })
        {
            int i = s.LastIndexOf(sep, StringComparison.Ordinal);
            if (i >= 0 && i + sep.Length < s.Length) s = s[(i + sep.Length)..];
        }
        if (!s.Contains(' ') && s.Contains('.')) s = s[(s.LastIndexOf('.') + 1)..];
        return s.Trim();
    }

    // ─── what the model said it skipped ───

    private static readonly Regex SkippedLine = new(
        @"^\s*(?:[-*•>]\s*)?(?:\*\*|__)?(?:tests?\s+)?(?:skipped|not\s+run|did\s+not\s+run|out\s+of\s+scope|ข้าม(?:การ)?(?:เทส(?:ต์)?|ทดสอบ)?|ไม่ได้รัน(?:เทส(?:ต์)?)?|ไม่ได้ทดสอบ|นอกขอบเขต)(?:\*\*|__)?\s*(?:\([^)]*\))?\s*(?:\*\*|__)?\s*[:：—–-]\s*(?<what>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The "Skipped: …" / "ข้าม: …" lines of the model's closing answer, each item once. Pure.</summary>
    public static List<string> SkippedFromReply(string? reply)
    {
        var list = new List<string>();
        foreach (Match m in SkippedLine.Matches((reply ?? "").Replace("\r\n", "\n")))
        {
            string what = m.Groups["what"].Value.Trim().TrimEnd('.', '。').Trim('*', '_', ' ');
            if (what.Length == 0 || Regex.IsMatch(what, @"^(?:none|nothing|ไม่มี|-)$", RegexOptions.IgnoreCase)) continue;
            list.Add(what);
        }
        return list;
    }

    /// <summary>wrap_up's tests_skipped: a JSON array of strings, or one string with items on lines or after ";". Pure.</summary>
    public static List<string> SkippedFromArg(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return new();
        if (raw.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                    .Where(s => s.Trim().Length > 0).ToList();
            }
            catch (JsonException) { /* plain text after all */ }
        }
        return raw.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
