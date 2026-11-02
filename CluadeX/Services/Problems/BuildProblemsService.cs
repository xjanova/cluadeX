using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Lsp;

namespace CluadeX.Services.Problems;

/// <summary>A problem as the Problems list shows it: stale = the file changed after the run that found it (it may be
/// fixed; its line may have moved), so it is listed dimmed and not drawn in the editor.</summary>
public sealed record BuildProblemEntry(BuildProblem Problem, bool Stale);

/// <summary>A checker the editor can run for a project. Safe = runs none of the project's own code (no MSBuild targets,
/// build.rs or package scripts), so it may run by itself after an AI run; the others run only when the user clicks.</summary>
public sealed record ProblemCheck(string Key, string Label, string Command, bool Safe, string[] Extensions);

/// <summary>How the last check went, for the line under the Problems header.</summary>
public sealed record ProblemCheckOutcome(string Label, DateTime FinishedUtc, int Errors, int Warnings, bool TimedOut, bool Cancelled, string? Note);

/// <summary>
/// Errors from the runs the app already makes — the agent's build, tests and commands, the editor's terminal, and the
/// Problems panel's own Check button — without any language server (owner 2026-11-03: "errors without any install").
///
/// A run of a known toolchain (<see cref="BuildOutputParser.Toolchain"/>) is that toolchain's whole verdict for the
/// folder it ran in: its earlier problems there are replaced, so a clean build clears them. Anything else only adds,
/// and what it added goes away once the file changes. A problem whose file changed after its run is kept but marked
/// stale until the next run.
/// </summary>
public sealed class BuildProblemsService : IAppShutdownHook, IDisposable
{
    public static BuildProblemsService? Current { get; private set; }

    public const int MaxEntries = 5000;
    public static TimeSpan CheckTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Something changed (a run was read, a check started or ended, a file the agent wrote). Background thread.</summary>
    public event Action? Changed;

    /// <summary>Runs a checker: (command, folder, timeout, cancel) → (exit code, output, timed out). Tests swap it.</summary>
    public Func<string, string, TimeSpan, CancellationToken, Task<(int Exit, string Output, bool TimedOut)>> Runner { get; set; } = RunWithShellAsync;

    private sealed record Entry(BuildProblem Problem, string Set, bool Complete, DateTime AtUtc);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, DateTime> _editedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemService? _fs;
    private CancellationTokenSource? _checkCts;
    private string? _checkingLabel;
    private ProblemCheckOutcome? _last;
    /// <summary>When each toolchain last gave a whole verdict — an edit after it means its check is due.</summary>
    private readonly Dictionary<string, DateTime> _verdictUtc = new(StringComparer.OrdinalIgnoreCase);

    public BuildProblemsService(CodeAgentService? agent = null, FileSystemService? fs = null)
    {
        _fs = fs;
        if (agent != null) agent.OnToolExecuted += OnToolResult;
        if (fs != null) fs.OnWorkingDirectoryChanged += () => { CancelCheck(); Clear(); };
        Current = this;
    }

    // ───────────────────────── reading runs ─────────────────────────

    private static readonly Regex RanIn = new(@"\[This ran in (?<dir>[^\]\r\n]+?)/? \(an earlier `cd` persists\)|\[This ran in (?<dir2>[^\]\r\n]+?)/ ", RegexOptions.Compiled);
    private static readonly Regex LeadingCd = new(@"^\s*cd\s+(?:/d\s+)?""?(?<dir>[^""&;|]+?)""?\s*(?:&&|;)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>From <see cref="CodeAgentService.OnToolExecuted"/> (the run's thread): read build / test / command output
    /// off that thread; note the files the agent wrote.</summary>
    public void OnToolResult(ToolResult r)
    {
        try
        {
            // Captured here, on the run's own thread: an agent working in another folder has its own root.
            string? root = _fs?.HasWorkingDirectory == true ? _fs.WorkingDirectory : null;
            if (r.Success && !string.IsNullOrEmpty(r.FilePath) && r.Type != ToolType.RunBuild && r.Type != ToolType.RunTests)
            {
                string? full = BuildOutputParser.Resolve(r.FilePath!, root);
                if (full != null) NoteEdited(full);
                return;
            }
            if (!IsCheckingTool(r)) return;
            string output = string.IsNullOrEmpty(r.Error) ? r.Output : r.Output + "\n" + r.Error;
            string? command = r.Arguments != null && r.Arguments.TryGetValue("command", out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;
            if (command == null && output.StartsWith("$ "))
            {
                int nl = output.IndexOf('\n');
                command = (nl > 0 ? output[2..nl] : output[2..]).Split("   (detected:")[0].Trim();
            }
            bool success = r.Success;
            _ = Task.Run(() =>
            {
                try { Ingest(output, root, command, success); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Problems] reading a tool result failed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Problems] {ex.Message}"); }
    }

    private static bool IsCheckingTool(ToolResult r)
    {
        if (r.Type is ToolType.RunBuild or ToolType.RunTests or ToolType.RunCommand or ToolType.PowerShell) return true;
        string n = (r.ToolName ?? "").ToLowerInvariant();
        return n.Contains("verify") || n.Contains("lint") || n.Contains("analy") || n.Contains("typecheck") || n.Contains("build") || n.Contains("check");
    }

    /// <summary>
    /// Read one run's output. <paramref name="succeeded"/>: the exit code said so (null = unknown, e.g. the terminal).
    /// Returns how many problems it found. Thread-safe; call it off the UI thread (it parses and looks at files).
    /// </summary>
    public int Ingest(string output, string? workingDirectory, string? command, bool? succeeded = null)
    {
        if (string.IsNullOrEmpty(output)) return 0;
        string? toolchain = BuildOutputParser.Toolchain(command, output);

        // Where relative paths start: a leading `cd X &&`, the agent's "[This ran in X/ …]" note, then the root.
        var folders = new List<string>();
        if (command != null && LeadingCd.Match(command) is { Success: true } cd && workingDirectory != null)
            TryAdd(folders, Path.Combine(workingDirectory, cd.Groups["dir"].Value.Trim()));
        if (RanIn.Match(output) is { Success: true } ran && workingDirectory != null)
            TryAdd(folders, Path.Combine(workingDirectory, (ran.Groups["dir"].Success ? ran.Groups["dir"] : ran.Groups["dir2"]).Value.Trim()));
        if (workingDirectory != null) TryAdd(folders, workingDirectory);

        var found = BuildOutputParser.Parse(output, folders, toolchain ?? "build");
        // Only the project's own files: MSBuild also reports on the SDK's .targets, Python on site-packages.
        string? home = workingDirectory;
        found = found.Where(p => home == null || BuildOutputParser.IsUnder(p.Path, home)).Where(p => SafeExists(p.Path)).ToList();

        var now = DateTime.UtcNow;
        bool changed = false;
        lock (_gate)
        {
            if (toolchain != null)
            {
                // A whole verdict replaces the last one for this folder — but only from a run that got as far as
                // checking: "dotnet: not found" or a failed restore says nothing about the code.
                bool verdict = found.Count > 0 || succeeded == true || BuildOutputParser.LooksClean(output);
                if (verdict)
                {
                    string scope = ScopeOf(command, folders, workingDirectory);
                    changed |= _entries.RemoveAll(e => e.Set == toolchain && (scope.Length == 0 || BuildOutputParser.IsUnder(e.Problem.Path, scope))) > 0;
                    foreach (var p in found) _entries.Add(new Entry(p, toolchain, true, now));
                    _verdictUtc[toolchain] = now;
                    changed |= found.Count > 0;
                }
            }
            else if (found.Count > 0)
            {
                // Output of an unknown tool: what it says about a file replaces what earlier unknown output said about it.
                var files = new HashSet<string>(found.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
                _entries.RemoveAll(e => !e.Complete && files.Contains(e.Problem.Path));
                foreach (var p in found) _entries.Add(new Entry(p, "other", false, now));
                changed = true;
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        if (changed) Raise();
        return found.Count;
    }

    private static void TryAdd(List<string> folders, string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir);
            if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
        }
        catch { /* not a path */ }
    }

    /// <summary>The folder a run speaks for: a `dotnet build path\App.csproj` only for that project's folder, a `cd X &&`
    /// for X, else the whole root.</summary>
    private static string ScopeOf(string? command, List<string> folders, string? root)
    {
        if (command != null && root != null)
        {
            var proj = Regex.Matches(command, @"""?(?<p>[^""\s&|;]+\.(?:csproj|fsproj|vbproj))""?", RegexOptions.IgnoreCase);
            if (proj.Count == 1)
            {
                try { return Path.GetDirectoryName(Path.GetFullPath(Path.Combine(folders.Count > 0 ? folders[0] : root, proj[0].Groups["p"].Value))) ?? ""; }
                catch { }
            }
        }
        return folders.Count > 0 ? folders[0] : root ?? "";
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>A file was written (by the agent, or saved): what was found in it before may be fixed now.</summary>
    public void NoteEdited(string fullPath)
    {
        lock (_gate) _editedUtc[fullPath] = DateTime.UtcNow;
        Raise();
    }

    /// <summary>The safe checks due after an AI run: a file of their language was written after their last verdict (an
    /// agent that ran `flutter analyze` itself after its last edit leaves nothing to do).</summary>
    public IReadOnlyList<ProblemCheck> PendingAutoChecks(string? root)
    {
        if (string.IsNullOrEmpty(root)) return Array.Empty<ProblemCheck>();
        var due = new List<ProblemCheck>();
        foreach (var check in ChecksFor(root).Where(c => c.Safe))
            lock (_gate)
            {
                var since = _verdictUtc.TryGetValue(check.Key, out var v) ? v : DateTime.MinValue;
                if (_editedUtc.Any(kv => kv.Value > since && BuildOutputParser.IsUnder(kv.Key, root)
                                         && check.Extensions.Contains(Path.GetExtension(kv.Key), StringComparer.OrdinalIgnoreCase)))
                    due.Add(check);
            }
        return due;
    }

    // ───────────────────────── reading problems ─────────────────────────

    private bool IsStale(Entry e)
    {
        if (_editedUtc.TryGetValue(e.Problem.Path, out var edited) && edited > e.AtUtc) return true;
        try { return File.GetLastWriteTimeUtc(e.Problem.Path) > e.AtUtc; }
        catch { return false; }
    }

    /// <summary>This file's problems. Stale ones from an unknown tool are dropped (nothing will ever refresh them).</summary>
    public IReadOnlyList<BuildProblemEntry> ProblemsFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<BuildProblemEntry>();
        lock (_gate)
            return _entries.Where(e => LspPaths.SamePath(e.Problem.Path, path))
                .Select(e => (e, stale: IsStale(e))).Where(x => x.e.Complete || !x.stale)
                .Select(x => new BuildProblemEntry(x.e.Problem, x.stale)).ToList();
    }

    /// <summary>Every problem (at most <paramref name="max"/>), errors first, then by file and line.</summary>
    public IReadOnlyList<BuildProblemEntry> All(int max = 1000)
    {
        lock (_gate)
            return _entries.Select(e => (e, stale: IsStale(e))).Where(x => x.e.Complete || !x.stale)
                .OrderBy(x => x.stale).ThenBy(x => x.e.Problem.Severity).ThenBy(x => x.e.Problem.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.e.Problem.Line)
                .Take(max).Select(x => new BuildProblemEntry(x.e.Problem, x.stale)).ToList();
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Forget everything (the project changed).</summary>
    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _editedUtc.Clear(); _verdictUtc.Clear(); _last = null; }
        Raise();
    }

    // ───────────────────────── the Check button ─────────────────────────

    public bool IsChecking => _checkingLabel != null;
    public string? CheckingLabel => _checkingLabel;
    public ProblemCheckOutcome? LastOutcome => _last;

    /// <summary>The checkers this project has (looks at files only, runs nothing). Empty = no Check button.</summary>
    public IReadOnlyList<ProblemCheck> ChecksFor(string? root)
    {
        var checks = new List<ProblemCheck>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return checks;
        bool Has(string name) => SafeExists(Path.Combine(root, name));
        bool HasGlob(string pattern)
        {
            try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any(); } catch { return false; }
        }

        if (Has("pubspec.yaml"))
        {
            bool flutter = false;
            try { flutter = Regex.IsMatch(File.ReadAllText(Path.Combine(root, "pubspec.yaml")), @"sdk:\s*flutter"); } catch { }
            if (flutter && OnPath("flutter.bat", "flutter.exe") != null)
                checks.Add(new ProblemCheck("dart", "flutter analyze", "flutter analyze --no-pub", true, new[] { ".dart" }));
            else if (OnPath("dart.exe", "dart.bat") != null)
                checks.Add(new ProblemCheck("dart", "dart analyze", "dart analyze", true, new[] { ".dart" }));
        }

        string tsc = Path.Combine(root, "node_modules", ".bin", "tsc.cmd");
        if (Has("tsconfig.json") && SafeExists(tsc))
            checks.Add(new ProblemCheck("tsc", "tsc --noEmit", $"\"{tsc}\" --noEmit --pretty false -p .", true,
                new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".vue" }));

        if ((Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py") || HasGlob("*.py")) && FindPython() is { } python)
            checks.Add(new ProblemCheck("pysyntax", "Python syntax", $"{python} -I -X utf8 \"{PythonCheckScript()}\" .", true, new[] { ".py", ".pyw" }));

        if (Has("Cargo.toml") && OnPath("cargo.exe") != null)
            checks.Add(new ProblemCheck("cargo", "cargo check", "cargo check --message-format=short", false, new[] { ".rs" }));
        if (Has("go.mod") && OnPath("go.exe") != null)
            checks.Add(new ProblemCheck("go", "go vet", "go vet ./...", false, new[] { ".go" }));

        // .NET: the agent's own detection (a solution below the root, several projects) when this is its project.
        string? dotnet = null;
        if (HasGlob("*.sln") || HasGlob("*.slnx") || HasGlob("*.csproj") || HasGlob("*.fsproj")) dotnet = "dotnet build -nologo";
        else if (_fs != null && _fs.HasWorkingDirectory && LspPaths.SamePath(_fs.WorkingDirectory, root)
                 && _fs.DetectProjectCommand(test: false) is { } detected && detected.Command.Contains("dotnet build"))
            dotnet = detected.Command;
        if (dotnet != null && OnPath("dotnet.exe") != null)
            checks.Add(new ProblemCheck("dotnet", "dotnet build", dotnet, false, new[] { ".cs", ".fs", ".vb", ".xaml", ".razor", ".csproj" }));
        return checks;
    }

    /// <summary>Every checker of the project (the Check button: the user asked, so the ones that build run too).</summary>
    public Task<ProblemCheckOutcome?> CheckNowAsync(string root) => RunChecksAsync(root, ChecksFor(root));

    /// <summary>
    /// Run <paramref name="checks"/> one after another and read what they print. A second call while one runs is
    /// ignored (returns null) — <see cref="CancelCheck"/> stops it.
    /// </summary>
    public async Task<ProblemCheckOutcome?> RunChecksAsync(string root, IReadOnlyList<ProblemCheck> checks)
    {
        if (checks.Count == 0) return null;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_checkCts != null) { cts.Dispose(); return null; }
            _checkCts = cts;
        }
        var started = DateTime.UtcNow;
        int errors = 0, warnings = 0;
        bool timedOut = false;
        string? note = null;
        var labels = new List<string>();
        try
        {
            foreach (var check in checks)
            {
                if (cts.IsCancellationRequested) break;
                _checkingLabel = check.Label;
                labels.Add(check.Label);
                Raise();
                var (exit, output, slow) = await Runner(check.Command, root, CheckTimeout, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) break;
                if (slow) { timedOut = true; continue; }
                Ingest(output, root, check.Command, exit == 0);
                lock (_gate)
                {
                    string set = BuildOutputParser.Toolchain(check.Command, output) ?? check.Key;
                    var mine = _entries.Where(e => e.Set == set && e.AtUtc >= started).ToList();
                    errors += mine.Count(e => e.Problem.Severity == LspSeverity.Error);
                    warnings += mine.Count(e => e.Problem.Severity == LspSeverity.Warning);
                }
                if (exit != 0 && errors + warnings == 0 && !BuildOutputParser.LooksClean(output))
                    note = LastLine(output);   // it failed without saying where: show why (a missing SDK, a restore error)
            }
        }
        catch (Exception ex) { note = ex.Message; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_checkCts, cts)) _checkCts = null;
            }
            _checkingLabel = null;
            cts.Dispose();
        }
        var outcome = new ProblemCheckOutcome(string.Join(" + ", labels), DateTime.UtcNow, errors, warnings, timedOut, cts.IsCancellationRequested, note);
        _last = outcome;
        Raise();
        return outcome;
    }

    public void CancelCheck()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _checkCts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static string? LastLine(string output)
    {
        var line = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return line == null ? null : line.Length > 200 ? line[..200] + "…" : line;
    }

    private static async Task<(int, string, bool)> RunWithShellAsync(string command, string folder, TimeSpan timeout, CancellationToken ct)
    {
        var r = await ShellRunner.RunAsync(ShellRunner.Shell.Cmd, command, folder, (int)timeout.TotalMilliseconds, ct, captureChars: BuildOutputParser.MaxChars)
            .ConfigureAwait(false);
        return (r.ExitCode, r.Output, r.TimedOut);
    }

    private static string? OnPath(params string[] names)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                try
                {
                    string p = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch { /* a malformed PATH entry */ }
            }
        return null;
    }

    /// <summary>How to start Python here (quoted, ready for a command line), or null. The WindowsApps python.exe is the
    /// Store's "get Python" stub unless the Store's Python is installed next to it; then the py launcher.</summary>
    private static string? FindPython()
    {
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string dir = raw.Trim().Trim('"');
                string p = Path.Combine(dir, "python.exe");
                if (!File.Exists(p)) continue;
                if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
                    && !Directory.EnumerateDirectories(dir, "PythonSoftwareFoundation.Python.*").Any()) continue;
                return $"\"{p}\"";
            }
            catch { /* a malformed PATH entry */ }
        }
        string py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        return File.Exists(py) ? $"\"{py}\" -3" : null;
    }

    /// <summary>Parses every .py file below the folder (the AST only — no imports, nothing of the project runs) and prints
    /// gcc-style lines. Rewritten before each use: what runs is always this text, not whatever a file there says.</summary>
    internal static string PythonCheckScript()
    {
        string path = Path.Combine(Path.GetTempPath(), BuildOutputParser.PythonSyntaxCheckMarker + "-v1.py");
        try { File.WriteAllText(path, PythonCheckSource, new UTF8Encoding(false)); }
        catch (IOException) { /* another check is reading it right now: the same text */ }
        return path;
    }

    internal const string PythonCheckSource = """
        import ast, os, sys
        SKIP = {'.git', 'node_modules', '.venv', 'venv', 'env', '__pycache__', 'build', 'dist', 'site-packages', '.tox', '.mypy_cache'}
        root = sys.argv[1] if len(sys.argv) > 1 else '.'
        bad = 0
        seen = 0
        for d, dirs, files in os.walk(root):
            dirs[:] = [x for x in dirs if x not in SKIP and not x.startswith('.')]
            for f in files:
                if not f.endswith(('.py', '.pyw')):
                    continue
                seen += 1
                if seen > 3000:
                    break
                p = os.path.join(d, f)
                try:
                    with open(p, 'rb') as h:
                        ast.parse(h.read(), p)
                except SyntaxError as e:
                    bad += 1
                    print(f"{p}:{e.lineno or 1}:{e.offset or 0}: error: {type(e).__name__}: {e.msg}")
                except (ValueError, OSError):
                    pass
        if bad == 0:
            print(f"OK ({seen} files checked)")
        sys.exit(1 if bad else 0)
        """;

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Problems] a listener failed: {ex.Message}"); }
    }

    public void OnAppShutdown() => CancelCheck();

    public void Dispose() => CancelCheck();
}
