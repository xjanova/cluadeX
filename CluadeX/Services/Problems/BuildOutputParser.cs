using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Services.Lsp;

namespace CluadeX.Services.Problems;

/// <summary>One error a compiler, linter or analyzer printed, with where it is. Line and column are 1-based (as the
/// tools print them); column 0 = the tool gave none.</summary>
public sealed record BuildProblem(
    string Path, int Line, int Column, int EndLine, int EndColumn,
    LspSeverity Severity, string Message, string Code, string Tool)
{
    /// <summary>As an editor diagnostic (0-based LSP positions). No column → the whole line; no end → the word at the
    /// column (DiagnosticMarkerRenderer widens an empty range to its word).</summary>
    public EditorDiagnostic ToDiagnostic()
    {
        int line = Math.Max(0, Line - 1);
        if (Column <= 0) return new(line, 0, line, int.MaxValue / 2, Severity, Message, Tool, Code);
        int col = Column - 1;
        int endLine = EndLine > 0 ? EndLine - 1 : line;
        int endCol = EndColumn > 0 ? EndColumn - 1 : col;
        return new(line, col, endLine, endCol, Severity, Message, Tool, Code);
    }
}

/// <summary>
/// Errors out of the text the build, test and check tools already print — no language server, nothing to install.
/// Every format below is the tool's default when its output is not a terminal (what the agent's tools and the editor's
/// terminal capture):
/// <list type="bullet">
/// <item>MSBuild / dotnet / csc / tsc: <c>path(12,5): error CS1002: ; expected [proj]</c> (also <c>(l,c,el,ec)</c>, a
///   <c>1&gt;</c> node prefix)</item>
/// <item>tsc --pretty, gcc, clang, go, pyflakes, mypy, ruff, eslint -f unix, cargo --message-format=short:
///   <c>path:12:5: error: message</c> / <c>path:12:5 - error TS2322: message</c> / <c>path:12: message</c></item>
/// <item>eslint (stylish): a file line, then <c>  12:5  error  message  rule</c></item>
/// <item>dart / flutter analyze: <c>error - lib\main.dart:12:5 - message - code</c>, the older <c>error • message •
///   path:12:5 • code</c>, and <c>--format=machine</c></item>
/// <item>rustc / cargo: <c>error[E0308]: message</c> then <c>  --&gt; src/main.rs:4:18</c></item>
/// <item>Python tracebacks / py_compile: <c>File "path", line 3</c> … <c>SyntaxError: message</c></item>
/// </list>
/// A location must name a source file (a known extension): <c>12:30:45: started</c> or a URL is not an error.
/// </summary>
public static class BuildOutputParser
{
    /// <summary>Above this the output is cut (a runaway log): the first errors are what matters.</summary>
    public const int MaxChars = 4_000_000;
    public const int MaxProblems = 2000;

    /// <summary>The file name of the editor's own Python syntax check — how its runs are told apart.</summary>
    public const string PythonSyntaxCheckMarker = "cluadex-pysyntax";

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".xaml", ".razor", ".cshtml", ".csproj", ".props", ".targets",
        ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro", ".json",
        ".dart", ".py", ".pyi", ".rs", ".go", ".java", ".kt", ".kts", ".swift", ".c", ".cc", ".cpp", ".cxx", ".h", ".hh",
        ".hpp", ".m", ".mm", ".php", ".rb", ".lua", ".yaml", ".yml", ".toml", ".css", ".scss", ".less", ".html",
    };

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    // path(12,5): error CS1002: message [project]   — MSBuild, csc, tsc (non-pretty)
    private static readonly Regex MsBuild = new(
        @"^(?:\d+>)?\s*(?<file>[^\r\n]*?[^\s(])\((?<line>\d+)(?:,(?<col>\d+)(?:,(?<eline>\d+),(?<ecol>\d+))?)?\)\s*:\s*(?:[\w.-]+\s+)?(?<sev>error|warning|info|message)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]\r\n]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // path:12:5: error: message  |  path:12:5 - error TS2322: message  |  path:12: message  (gcc, go, pyflakes, mypy…)
    private static readonly Regex Colon = new(
        @"^\s*(?:\./)?(?<file>(?:[A-Za-z]:[\\/])?[^:\r\n]*?[^\s:]):(?<line>\d+)(?::(?<col>\d+))?(?::\d+:\d+)?(?:\s*-\s*|:\s*)(?<rest>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex SeverityPrefix = new(
        @"^(?<sev>fatal error|error|warning|warn|note|info|hint)\b(?:\[(?<code1>[^\]]+)\])?(?:\s+(?<code2>[A-Za-z]+\d+))?\s*:\s*(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // eslint stylish: "  12:5  error  'x' is defined but never used  no-unused-vars"
    private static readonly Regex EslintRow = new(
        @"^\s+(?<line>\d+):(?<col>\d+)\s+(?<sev>error|warning)\s+(?<msg>.+?)(?:\s{2,}(?<code>[@\w/-]+))?\s*$",
        RegexOptions.Compiled);

    // dart analyze: "  error - lib\main.dart:12:5 - Undefined name 'x'. - undefined_identifier"
    private static readonly Regex DartDash = new(
        @"^\s*(?<sev>error|warning|info|lint|hint)\s+-\s+(?<file>.+?):(?<line>\d+):(?<col>\d+)\s+-\s+(?<msg>.+?)(?:\s+-\s+(?<code>[a-z0-9_]+))?\s*$",
        RegexOptions.Compiled);

    // flutter analyze (older): "  error • Undefined name 'x' • lib/main.dart:12:5 • undefined_identifier"
    private static readonly Regex DartBullet = new(
        @"^\s*(?<sev>error|warning|info|lint|hint)\s+•\s+(?<msg>.+?)\s+•\s+(?<file>.+?):(?<line>\d+):(?<col>\d+)(?:\s+•\s+(?<code>[a-z0-9_]+))?\s*$",
        RegexOptions.Compiled);

    // dart analyze --format=machine: "ERROR|COMPILE_TIME_ERROR|UNDEFINED_IDENTIFIER|C:\p\main.dart|12|5|3|Undefined name 'x'."
    private static readonly Regex DartMachine = new(
        @"^(?<sev>ERROR|WARNING|INFO)\|[A-Z_]+\|(?<code>[A-Z_0-9]+)\|(?<file>[^|]+)\|(?<line>\d+)\|(?<col>\d+)\|(?<len>\d+)\|(?<msg>.*)$",
        RegexOptions.Compiled);

    // rustc: "error[E0308]: mismatched types" … "  --> src/main.rs:4:18"
    private static readonly Regex RustHead = new(@"^(?<sev>error|warning)(?:\[(?<code>[A-Za-z]*\d+)\])?:\s*(?<msg>.+)$", RegexOptions.Compiled);
    private static readonly Regex RustArrow = new(@"^\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)\s*$", RegexOptions.Compiled);

    // Python: '  File "C:\x\a.py", line 3' … 'SyntaxError: invalid syntax'
    private static readonly Regex PyFile = new(@"^\s*File ""(?<file>[^""]+)"", line (?<line>\d+)", RegexOptions.Compiled);
    private static readonly Regex PyError = new(@"^(?:Sorry:\s*)?(?<code>[A-Za-z_.]*(?:Error|Exception|Warning)):\s*(?<msg>.*)$", RegexOptions.Compiled);

    /// <summary>Every problem in <paramref name="output"/>; relative paths are resolved against
    /// <paramref name="workingDirectory"/>. <paramref name="tool"/> labels them (Problems shows it).</summary>
    public static List<BuildProblem> Parse(string? output, string? workingDirectory, string tool = "build")
        => Parse(output, string.IsNullOrEmpty(workingDirectory) ? Array.Empty<string>() : new[] { workingDirectory }, tool);

    /// <summary>As above, with several folders the tool may have run in (a <c>cd</c> before it, a project folder below
    /// the root): a relative path is taken in the first folder where that file exists, else in the first folder.</summary>
    public static List<BuildProblem> Parse(string? output, IReadOnlyList<string> folders, string tool = "build")
    {
        var found = new List<BuildProblem>();
        if (string.IsNullOrEmpty(output)) return found;
        string? home = folders.Count > 0 ? folders[0] : null;
        // pyflakes / flake8 / ruff print no severity: most of what they say is style ("imported but unused").
        bool lintWithoutSeverity = tool is "pyflakes" or "flake8" or "ruff";
        if (output.Length > MaxChars) output = output[..MaxChars];
        output = Ansi.Replace(output, "");
        bool goOutput = tool == "go" || Regex.IsMatch(output, @"^#\s+\S+\s*$", RegexOptions.Multiline);
        var seen = new HashSet<string>();
        string? eslintFile = null;
        (LspSeverity Sev, string Code, string Msg)? rustHead = null;
        (string File, int Line)? pyFrame = null;

        void Add(string file, int line, int col, int eline, int ecol, LspSeverity sev, string msg, string code)
        {
            if (found.Count >= MaxProblems || line <= 0) return;
            string? path = Resolve(file, folders);
            if (path == null) return;
            msg = msg.Trim();
            string key = $"{path}|{line}|{col}|{code}|{msg}";
            if (!seen.Add(key.ToLowerInvariant())) return;   // MSBuild prints every error twice (inline + summary)
            found.Add(new BuildProblem(path, line, col, eline, ecol, sev, msg, code, tool));
        }

        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.Length > 4000) continue;
            Match m;

            if (rustHead is { } head && (m = RustArrow.Match(line)).Success)
            {
                Add(m.Groups["file"].Value, Int(m, "line"), Int(m, "col"), 0, 0, head.Sev, head.Msg, head.Code);
                rustHead = null;
                continue;
            }
            if ((m = RustHead.Match(line)).Success && !line.Contains(" -->"))
            {
                rustHead = (Sev(m.Groups["sev"].Value), m.Groups["code"].Value, m.Groups["msg"].Value);
                // cargo also prints "error: could not compile …" with no location — harmless: no arrow follows.
                continue;
            }

            if (pyFrame is { } frame && (m = PyError.Match(line.Trim())).Success && !line.StartsWith(' '))
            {
                Add(frame.File, frame.Line, 0, 0, 0, m.Groups["code"].Value.EndsWith("Warning") ? LspSeverity.Warning : LspSeverity.Error,
                    $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}", m.Groups["code"].Value);
                pyFrame = null;
                continue;
            }
            if ((m = PyFile.Match(line)).Success)
            {
                // The innermost frame IN THE PROJECT: a crash inside a library is reported where the project called it.
                var at = (m.Groups["file"].Value, Int(m, "line"));
                if (pyFrame == null || IsUnder(Resolve(at.Item1, folders), home) || !IsUnder(Resolve(pyFrame.Value.File, folders), home))
                    pyFrame = at;
                continue;
            }

            if ((m = DartMachine.Match(line)).Success)
            {
                int col = Int(m, "col");
                Add(m.Groups["file"].Value, Int(m, "line"), col, Int(m, "line"), col + Int(m, "len"), Sev(m.Groups["sev"].Value),
                    m.Groups["msg"].Value, m.Groups["code"].Value.ToLowerInvariant());
                continue;
            }
            if ((m = DartDash.Match(line)).Success || (m = DartBullet.Match(line)).Success)
            {
                Add(m.Groups["file"].Value, Int(m, "line"), Int(m, "col"), 0, 0, Sev(m.Groups["sev"].Value), m.Groups["msg"].Value, m.Groups["code"].Value);
                continue;
            }

            if ((m = MsBuild.Match(line)).Success && IsSourceFile(m.Groups["file"].Value))
            {
                string sev = m.Groups["sev"].Value;
                Add(m.Groups["file"].Value, Int(m, "line"), Int(m, "col"), Int(m, "eline"), Int(m, "ecol"),
                    Sev(sev.Equals("message", StringComparison.OrdinalIgnoreCase) ? "info" : sev), m.Groups["msg"].Value, m.Groups["code"].Value);
                continue;
            }

            if (eslintFile != null && (m = EslintRow.Match(line)).Success)
            {
                Add(eslintFile, Int(m, "line"), Int(m, "col"), 0, 0, Sev(m.Groups["sev"].Value), m.Groups["msg"].Value, m.Groups["code"].Value);
                continue;
            }

            if ((m = Colon.Match(line)).Success && IsSourceFile(m.Groups["file"].Value))
            {
                string rest = m.Groups["rest"].Value.Trim();
                var sevMatch = SeverityPrefix.Match(rest);
                LspSeverity sev = lintWithoutSeverity && !Regex.IsMatch(rest, @"undefined name|SyntaxError|invalid syntax|E999|F821", RegexOptions.IgnoreCase)
                    ? LspSeverity.Warning : LspSeverity.Error;
                string code = "", msg = rest;
                if (sevMatch.Success)
                {
                    sev = Sev(sevMatch.Groups["sev"].Value);
                    code = sevMatch.Groups["code1"].Success ? sevMatch.Groups["code1"].Value : sevMatch.Groups["code2"].Value;
                    msg = sevMatch.Groups["msg"].Value;
                }
                // mypy / ruff / eslint-unix put the rule at the end: "… [arg-type]" / "… [Error/no-undef]"
                var tail = Regex.Match(msg, @"\s+\[(?:(?<sev>Error|Warning)/)?(?<rule>[\w./@-]+)\]\s*$");
                bool goDiagnostic = goOutput && Path.GetExtension(m.Groups["file"].Value).Equals(".go", StringComparison.OrdinalIgnoreCase);
                if (!sevMatch.Success && !lintWithoutSeverity && !goDiagnostic && !tail.Groups["sev"].Success) continue;
                if (tail.Success)
                {
                    if (code.Length == 0) code = tail.Groups["rule"].Value;
                    if (msg.Contains("[Warning/")) sev = LspSeverity.Warning;
                    msg = msg[..tail.Index];
                }
                Add(m.Groups["file"].Value, Int(m, "line"), Int(m, "col"), 0, 0, sev, msg, code);
                continue;
            }

            // eslint stylish names the file on a line of its own, then its rows.
            string trimmed = line.Trim();
            if (!line.StartsWith(' ') && IsSourceFile(trimmed) && (Path.IsPathRooted(trimmed) || trimmed.Contains('/') || trimmed.Contains('\\')))
                eslintFile = trimmed;
            else if (trimmed.Length == 0 || line.StartsWith('✖')) eslintFile = null;
        }
        return found;
    }

    private static int Int(Match m, string group) => m.Groups[group].Success && int.TryParse(m.Groups[group].Value, out int v) ? v : 0;

    private static LspSeverity Sev(string s) => s.ToLowerInvariant() switch
    {
        "error" or "fatal error" => LspSeverity.Error,
        "warning" or "warn" => LspSeverity.Warning,
        "lint" or "hint" or "note" => LspSeverity.Hint,
        _ => LspSeverity.Information,
    };

    private static bool IsSourceFile(string file)
    {
        file = file.Trim().Trim('"');
        if (file.Length == 0 || file.Contains("://")) return false;
        string ext;
        try { ext = Path.GetExtension(file); } catch { return false; }
        return SourceExtensions.Contains(ext);
    }

    private static readonly Regex GitBashDrive = new(@"^/(?<d>[A-Za-z])/(?<rest>.*)$", RegexOptions.Compiled);

    /// <summary>A printed path as a full path: relative ones against the folder the tool ran in. Null = not a path.</summary>
    public static string? Resolve(string file, string? workingDirectory)
        => Resolve(file, string.IsNullOrEmpty(workingDirectory) ? Array.Empty<string>() : new[] { workingDirectory });

    public static string? Resolve(string file, IReadOnlyList<string> folders)
    {
        file = file.Trim().Trim('"');
        if (file.StartsWith("./") || file.StartsWith(".\\")) file = file[2..];
        if (file.Length == 0) return null;
        // Git Bash (the agent's default shell) prints /c/Users/… for C:\Users\….
        if (GitBashDrive.Match(file) is { Success: true } gb) file = $"{gb.Groups["d"].Value.ToUpperInvariant()}:\\{gb.Groups["rest"].Value}";
        try
        {
            if (Path.IsPathRooted(file) || folders.Count == 0) return Path.GetFullPath(file);
            string? first = null;
            foreach (var dir in folders)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string full = Path.GetFullPath(Path.Combine(dir, file));
                first ??= full;
                if (folders.Count == 1 || File.Exists(full)) return full;
            }
            return first;
        }
        catch { return null; }
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> (false when either is unknown).</summary>
    public static bool IsUnder(string? path, string? folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        string f = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A run that finished checking and found nothing — the only kind of run allowed to clear what an earlier
    /// run found when it reports no problems (a run that died before compiling says nothing about the code).</summary>
    public static bool LooksClean(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        return Regex.IsMatch(output,
            @"Build succeeded|\b0 Error\(s\)|No issues found|Found 0 errors|^\s*Finished\b.*\btarget|\b(Passed|Failed)!\s+-\s+Failed:|^\s*Total tests:|\d+ passed|^OK \(\d+ files? checked\)|^Success: no issues found",
            RegexOptions.Multiline);
    }

    /// <summary>
    /// Which toolchain a command runs (<c>dotnet build</c>, <c>npx tsc</c>, <c>flutter analyze</c>…), or null. A run of
    /// a known toolchain is its whole verdict: what it did not report is fixed, so its earlier problems are replaced —
    /// a clean build clears them. Output of anything else only adds to what is there.
    /// </summary>
    public static string? Toolchain(string? command, string? output = null)
    {
        string c = (command ?? "").ToLowerInvariant();
        if (c.Length > 0)
        {
            if (Regex.IsMatch(c, @"\b(dotnet|msbuild)(\.exe)?\b.*\b(build|test|run|publish|pack|msbuild)\b") || Regex.IsMatch(c, @"\bmsbuild(\.exe)?\b|\bcsc(\.exe)?\b"))
                return "dotnet";
            if (Regex.IsMatch(c, @"\btsc\b|\bvue-tsc\b")) return "tsc";
            if (Regex.IsMatch(c, @"\beslint\b")) return "eslint";
            if (Regex.IsMatch(c, @"\b(flutter|dart)(\.bat|\.exe)?\s+analy[sz]e\b")) return "dart";
            if (Regex.IsMatch(c, @"\bcargo\s+(build|check|clippy|test)\b")) return "cargo";
            if (Regex.IsMatch(c, @"\bgo\s+(build|vet|test)\b")) return "go";
            if (c.Contains(PythonSyntaxCheckMarker)) return "pysyntax";
            foreach (var py in new[] { "pyflakes", "flake8", "ruff", "mypy", "pytest" })
                if (Regex.IsMatch(c, $@"\b{py}\b")) return py;
            if (Regex.IsMatch(c, @"\b(py_compile|compileall)\b")) return "pysyntax";
        }
        string o = output ?? "";
        if (o.Contains("Build succeeded.") || o.Contains("Build FAILED.") || Regex.IsMatch(o, @"\d+ Error\(s\)")) return "dotnet";
        if (Regex.IsMatch(o, @"^Analyzing \S+", RegexOptions.Multiline) && (o.Contains("No issues found!") || Regex.IsMatch(o, @"\d+ issues? found"))) return "dart";
        if (Regex.IsMatch(o, @"^\s*Finished .*target\(s\)|^error: could not compile", RegexOptions.Multiline)) return "cargo";
        return null;
    }
}
