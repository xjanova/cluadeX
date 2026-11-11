using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// The test card at the end of a turn (TurnTests): a header with the overall state, one line per suite (result, time,
/// how many runs), one line per failing test with how it was read (REAL / FLAKY / UNRELATED / FIXED), and the suites
/// the model left out as out of scope. Built from the row's JSON in the current language; a still record of the turn.
/// </summary>
public sealed class TurnTestsView
{
    public string Title { get; }
    public string StatusText { get; }
    /// <summary>"pass" / "fail" / "pending" / "none" — the header's colour.</summary>
    public string State { get; }
    public IReadOnlyList<TestSuiteRow> Suites { get; }
    public bool HasSuites => Suites.Count > 0;
    public IReadOnlyList<TestFailureRow> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public string MoreFailuresText { get; }
    public bool HasMoreFailures => MoreFailuresText.Length > 0;
    public IReadOnlyList<string> Skipped { get; }
    public bool HasSkipped => Skipped.Count > 0;

    /// <summary>Failing-test lines the card shows; the rest are "+N more".</summary>
    public const int MaxFailures = 10;

    public TurnTestsView(TurnTestSummary s, Func<string, string>? loc = null)
    {
        loc ??= k => LocalizedResources.Instance[k];
        string L(string k) => loc(k);
        string F(string k, params object[] a) { try { return string.Format(L(k), a); } catch (FormatException) { return L(k); } }

        Title = L("tests.title");
        int done = s.Suites.Count(x => !x.Pending);
        int failed = s.FailedSuites;
        State = s.Suites.Count == 0 ? "none" : failed > 0 ? "fail" : done < s.Suites.Count ? "pending" : "pass";
        StatusText = State switch
        {
            "none" => L("tests.status.none"),
            "fail" => F("tests.status.someFailed", failed, s.Suites.Count),
            "pending" => L("tests.status.pending"),
            _ => F("tests.status.allPassed", s.Suites.Count),
        };
        Suites = s.Suites.Select(x => new TestSuiteRow(x, loc)).ToList();
        var failures = s.Failures.Select(f => new TestFailureRow(f, s.ChangedFiles, loc)).ToList();
        Failures = failures.Take(MaxFailures).ToList();
        MoreFailuresText = failures.Count > MaxFailures ? F("tests.moreFailures", failures.Count - MaxFailures) : "";
        Skipped = s.Skipped.ToList();
    }

    /// <summary>The row's plain text: a reloaded chat without the card, a copy, or the model's history. Pure.</summary>
    public static string PlainLine(TurnTestSummary s, Func<string, string> loc)
    {
        var parts = s.Suites.Select(x => $"{x.Suite} {TestSuiteRow.ResultOf(x, loc)}").ToList();
        foreach (var f in s.Failures.Take(MaxFailures))
            parts.Add($"{TestFailureRow.KindLabel(f.Kind, loc)} {(f.Test.Length > 0 ? f.Test : f.Suite)}");
        if (s.Skipped.Count > 0) parts.Add(loc("tests.skippedTitle") + ": " + string.Join(", ", s.Skipped));
        string body = parts.Count > 0 ? string.Join(" · ", parts) : loc("tests.status.none");
        try { return "🧪 " + string.Format(loc("tests.plain"), body); }
        catch (FormatException) { return "🧪 " + body; }
    }

    /// <summary>"4.2s", "42s", "3m 05s"; "" when unknown.</summary>
    public static string TimeText(double? seconds) => seconds switch
    {
        null or < 0 => "",
        < 10 => $"{seconds:0.0}s",
        < 60 => $"{seconds:0}s",
        _ => $"{(int)(seconds / 60)}m {(int)(seconds % 60):00}s",
    };
}

/// <summary>One suite's line: state, name, result, time, runs.</summary>
public sealed class TestSuiteRow
{
    public string Suite { get; }
    public string CommandTip { get; }
    public string ResultText { get; }
    public string TimeText { get; }
    /// <summary>"pass" / "fail" / "pending".</summary>
    public string State { get; }
    public string Glyph => State switch { "pass" => "", "fail" => "", _ => "" };
    public string RunsText { get; }
    public string RunsTip { get; }
    public bool HasRuns => RunsText.Length > 0;

    public TestSuiteRow(TestSuiteLine x, Func<string, string> loc)
    {
        Suite = x.Suite;
        CommandTip = Fmt(loc("tests.commandTip"), x.Command);
        State = x.Pending ? "pending" : x.Success ? "pass" : "fail";
        ResultText = ResultOf(x, loc);
        TimeText = TurnTestsView.TimeText(x.Seconds);
        RunsText = x.Runs > 1 ? Fmt(loc("tests.runs"), x.Runs) : "";
        RunsTip = x.Runs > 1 ? Fmt(loc("tests.runsTip"), x.Runs) : "";
    }

    /// <summary>"2213 passed · 1 failed · 3 skipped"; "passed" / "failed" when the runner gave no counts. Pure.</summary>
    public static string ResultOf(TestSuiteLine x, Func<string, string> loc)
    {
        if (x.Pending) return loc("tests.running");
        if (x.Passed is null && x.Failed is null) return loc(x.Success ? "tests.ok" : "tests.notOk");
        var parts = new List<string> { Fmt(loc("tests.passed"), x.Passed ?? 0) };
        if ((x.Failed ?? 0) > 0 || !x.Success) parts.Add(Fmt(loc("tests.failed"), x.Failed ?? 0));
        if (x.Skipped is > 0) parts.Add(Fmt(loc("tests.skipped"), x.Skipped));
        return string.Join(" · ", parts);
    }

    internal static string Fmt(string format, params object?[] args)
    {
        try { return string.Format(format, args); }
        catch (FormatException) { return format; }
    }
}

/// <summary>One failing test: its reading as a coloured chip, its name and why.</summary>
public sealed class TestFailureRow
{
    /// <summary>"real" / "flaky" / "unrelated" / "fixed" — the chip's colour.</summary>
    public string Kind { get; }
    public string KindText { get; }
    public string Suite { get; }
    public string Test { get; }
    public string Why { get; }

    public TestFailureRow(TestFailureLine f, int changedFiles, Func<string, string> loc)
    {
        Kind = f.Kind.ToString().ToLowerInvariant();
        KindText = KindLabel(f.Kind, loc);
        Suite = f.Suite;
        Test = f.Test.Length > 0 ? f.Test : loc("tests.unnamed");
        Why = f.Kind switch
        {
            TestFailureKind.Flaky => loc("tests.why.flaky"),
            TestFailureKind.Fixed => loc("tests.why.fixed"),
            TestFailureKind.Unrelated => TestSuiteRow.Fmt(loc("tests.why.unrelated"), changedFiles),
            _ => loc(changedFiles > 0 ? "tests.why.real" : "tests.why.realNoChanges"),
        };
    }

    public static string KindLabel(TestFailureKind k, Func<string, string> loc) => loc("tests.kind." + k.ToString().ToLowerInvariant());
}
