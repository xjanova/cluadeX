using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Services.Background;

namespace CluadeX.Services;

/// <summary>One app or project the owner watches: how to run it, how to test it, where it answers on the web, and
/// how each of those went last time.</summary>
public sealed class WatchedApp : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProblem)));
    }

    private string _name = "", _projectPath = "", _runCommand = "", _testCommand = "", _url = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ProjectPath { get => _projectPath; set => Set(ref _projectPath, value); }
    public string RunCommand { get => _runCommand; set => Set(ref _runCommand, value); }
    public string TestCommand { get => _testCommand; set => Set(ref _testCommand, value); }
    /// <summary>A page to check (http…); filled from the app's own output when it prints one.</summary>
    public string Url { get => _url; set => Set(ref _url, value); }

    // ── last test run (kept across restarts) ──
    private string _testState = "never", _testSummary = "", _testTail = "";
    private DateTime? _testWhen;
    /// <summary>never | running | passed | failed</summary>
    public string TestState { get => _testState; set => Set(ref _testState, value); }
    public string TestSummary { get => _testSummary; set => Set(ref _testSummary, value); }
    public string TestTail { get => _testTail; set => Set(ref _testTail, value); }
    public DateTime? TestWhen { get => _testWhen; set => Set(ref _testWhen, value); }

    // ── the running app (this session only) ──
    private string _runState = "idle", _runErrors = "", _urlState = "";
    private int? _runId;
    /// <summary>idle | running | stopped | crashed</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string RunState { get => _runState; set => Set(ref _runState, value); }
    [System.Text.Json.Serialization.JsonIgnore] public string RunErrors { get => _runErrors; set => Set(ref _runErrors, value); }
    [System.Text.Json.Serialization.JsonIgnore] public int? RunId { get => _runId; set => Set(ref _runId, value); }
    /// <summary>"" | up | down — and <see cref="UrlDetail"/> says how.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string UrlState { get => _urlState; set => Set(ref _urlState, value); }
    private string _urlDetail = "";
    [System.Text.Json.Serialization.JsonIgnore] public string UrlDetail { get => _urlDetail; set => Set(ref _urlDetail, value); }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasProblem => TestState == "failed" || RunState == "crashed" || UrlState == "down";
}

/// <summary>
/// The Watch page's engine (owner 2026-10-16: "ติดตามแอพหรืองานตัวเอง เพื่อทดสอบหรือลองต่อได้ว่าบัคไหม ใช้ได้ดีหรือยัง"):
/// the apps the owner watches (DataRoot\watch.json), their run / test commands found from the project itself, tests
/// and apps run through <see cref="BackgroundProcessService"/> (own log, whole-tree stop), a crash read from the app's
/// own output, and a page checked over HTTP. It reports; it never sends anything to the AI by itself — the page's
/// "ask the AI" puts the evidence in that project's chat for the owner to send.
/// </summary>
public sealed class WatchService
{
    private readonly BackgroundProcessService _bg;
    private readonly string _file;
    private readonly DebugLogService? _log;

    public ObservableCollection<WatchedApp> Apps { get; } = new();

    public WatchService(BackgroundProcessService bg, SettingsService settings, DebugLogService? log = null, string? file = null)
    {
        _bg = bg;
        _log = log;
        _file = file ?? Path.Combine(settings.DataRoot, "watch.json");
        Load();
        _bg.Changed += OnBackgroundChanged;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<WatchedApp>>(File.ReadAllText(_file), Json) ?? new();
            foreach (var a in list.Where(a => !string.IsNullOrWhiteSpace(a.ProjectPath)))
            {
                if (a.TestState == "running") a.TestState = "never";   // the app closed while it ran
                Apps.Add(a);
            }
        }
        catch (Exception ex) { _log?.Warn("Watch", "watch.json unreadable — starting empty", ex); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Apps.ToList(), Json));
        }
        catch (Exception ex) { _log?.Warn("Watch", "could not save watch.json", ex); }
    }

    /// <summary>Watch <paramref name="projectPath"/>, its commands found from the project. The same folder twice
    /// returns the one already watched.</summary>
    public WatchedApp Add(string projectPath)
    {
        string path = projectPath.TrimEnd('\\', '/');
        var existing = Apps.FirstOrDefault(a => string.Equals(a.ProjectPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var d = Detect(path);
        var app = new WatchedApp { Name = d.Name, ProjectPath = path, RunCommand = d.Run, TestCommand = d.Test, Url = d.Url };
        Apps.Add(app);
        Save();
        return app;
    }

    public void Remove(WatchedApp app)
    {
        if (app.RunId is int id) _ = _bg.StopAsync(id, TimeSpan.FromSeconds(5));
        Apps.Remove(app);
        Save();
    }

    public sealed record Detected(string Name, string Run, string Test, string Url);

    /// <summary>How to run and test a project, from the files it has: a solution / project (.NET), package.json
    /// scripts (Node), pytest (Python), and a launch.json server entry. Empty where nothing says.</summary>
    public static Detected Detect(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string run = "", test = "", url = "";
        try
        {
            var (entries, _) = LaunchConfig.ReadAll(path);
            if (entries.Count > 0)
            {
                run = LaunchConfig.BuildCommand(entries[0], ShellRunner.DefaultShell);
                if (!string.IsNullOrWhiteSpace(entries[0].Url)) url = entries[0].Url!;
                else if (entries[0].Port is int port) url = $"http://localhost:{port}/";
            }

            var sln = Directory.EnumerateFiles(path, "*.slnx").Concat(Directory.EnumerateFiles(path, "*.sln")).FirstOrDefault();
            var projects = Directory.EnumerateFiles(path, "*.csproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                            && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).ToList();
            if (sln != null || projects.Count > 0)
            {
                bool hasTests = projects.Any(p => File.ReadAllText(p).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
                if (hasTests) test = sln != null ? $"dotnet test \"{Path.GetFileName(sln)}\"" : "dotnet test";
                if (run.Length == 0)
                {
                    var app = projects.FirstOrDefault(p => Regex.IsMatch(File.ReadAllText(p), @"<OutputType>\s*(WinExe|Exe)\s*</OutputType>", RegexOptions.IgnoreCase)
                                                           && !File.ReadAllText(p).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
                    if (app != null) run = $"dotnet run --project \"{Path.GetRelativePath(path, app)}\"";
                }
            }

            string pkg = Path.Combine(path, "package.json");
            if (File.Exists(pkg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
                if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                {
                    bool Has(string s) => scripts.TryGetProperty(s, out _);
                    if (test.Length == 0 && Has("test")) test = "npm test";
                    if (run.Length == 0) run = Has("dev") ? "npm run dev" : Has("start") ? "npm start" : "";
                }
            }

            if (test.Length == 0 && (File.Exists(Path.Combine(path, "pytest.ini")) || File.Exists(Path.Combine(path, "pyproject.toml"))
                                     || Directory.Exists(Path.Combine(path, "tests")) && Directory.EnumerateFiles(Path.Combine(path, "tests"), "test_*.py").Any()))
                test = "python -m pytest -q";
        }
        catch { /* a folder we cannot read: the commands stay empty for the owner to fill */ }
        return new Detected(name, run, test, url);
    }

    /// <summary>"225 passed, 2 skipped" from the common runners' last summary line (dotnet test, jest/vitest, pytest).</summary>
    public static string SummarizeTests(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var m = Regex.Match(line, @"(Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)");
            if (m.Success) return $"{m.Groups[3].Value} passed, {m.Groups[2].Value} failed, {m.Groups[4].Value} skipped";
            m = Regex.Match(line, @"Tests:\s+(.*?(?:passed|failed).*?total)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            m = Regex.Match(line, @"=+\s*((?:\d+ (?:passed|failed|skipped|errors?)(?:, )?)+).*=+");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return "";
    }

    /// <summary>Run the app's tests to the end; the result stays on the app (and in watch.json).</summary>
    public async Task RunTestsAsync(WatchedApp app, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app.TestCommand) || app.TestState == "running") return;
        app.TestState = "running";
        app.TestSummary = "";
        try
        {
            var r = await _bg.StartAsync(new BgStartRequest
            {
                Command = app.TestCommand, Cwd = app.ProjectPath, Name = "test: " + app.Name,
                WaitForReady = false, StartedBy = "watch",
            }, ct).ConfigureAwait(false);
            if (r.Process == null)
            {
                Finish(app, "failed", "", r.Message);
                return;
            }
            int id = r.Process.Id;
            var limit = DateTime.Now.AddMinutes(30);
            BgSnapshot? s;
            while ((s = _bg.Get(id)) is { IsRunning: true })
            {
                if (DateTime.Now > limit) { await _bg.StopAsync(id, TimeSpan.FromSeconds(5)).ConfigureAwait(false); break; }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            _bg.FlushPendingOutput();
            var tail = _bg.Read(id, 400, fromStart: true, reader: "watch").Lines;
            string summary = SummarizeTests(tail);
            bool ok = s?.ExitCode == 0;
            Finish(app, ok ? "passed" : "failed", summary, string.Join("\n", tail.TakeLast(ok ? 15 : 60)));
        }
        catch (OperationCanceledException) { Finish(app, "never", "", ""); }
        catch (Exception ex)
        {
            _log?.Warn("Watch", $"tests of {app.Name} could not run", ex);
            Finish(app, "failed", "", ex.Message);
        }
    }

    private void Finish(WatchedApp app, string state, string summary, string tail)
    {
        void Apply()
        {
            app.TestState = state;
            app.TestSummary = summary;
            app.TestTail = tail;
            app.TestWhen = DateTime.Now;
            Save();
        }
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply); else Apply();
    }

    /// <summary>Start the app in the background; a page it prints becomes its URL. A crash is caught from its output.</summary>
    public async Task<string?> StartAppAsync(WatchedApp app, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app.RunCommand)) return null;
        if (app.RunId is int old && _bg.Get(old) is { IsRunning: true }) return null;
        var r = await _bg.StartAsync(new BgStartRequest
        {
            Command = app.RunCommand, Cwd = app.ProjectPath, Name = "app: " + app.Name,
            WaitForReady = true, ReadyTimeout = TimeSpan.FromSeconds(25), StartedBy = "watch",
        }, ct).ConfigureAwait(false);
        void Apply()
        {
            app.RunId = r.Process?.Id;
            app.RunErrors = "";
            if (!string.IsNullOrEmpty(r.Url)) app.Url = r.Url!;
            app.RunState = r.Status switch
            {
                BgStartStatus.Ready or BgStartStatus.Running or BgStartStatus.AlreadyRunning => "running",
                BgStartStatus.Finished => "stopped",
                _ => "crashed",
            };
            if (app.RunState == "crashed") app.RunErrors = ErrorsOf(r.Process?.Id, r.LastLines, r.Message);
            Save();
        }
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply); else Apply();
        return r.Message;
    }

    public async Task StopAppAsync(WatchedApp app)
    {
        if (app.RunId is not int id) return;
        await _bg.StopAsync(id, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        void Apply() { app.RunState = "stopped"; }
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply); else Apply();
    }

    private string ErrorsOf(int? id, IReadOnlyList<string>? lastLines, string? message)
    {
        var errors = id is int i ? _bg.Read(i, 40, fromStart: true, errorsOnly: true, reader: "watch-err").Lines : Array.Empty<string>();
        var lines = errors.Count > 0 ? errors : (lastLines ?? Array.Empty<string>());
        string text = string.Join("\n", lines.TakeLast(40));
        return string.IsNullOrWhiteSpace(text) ? message ?? "" : text;
    }

    /// <summary>A watched app that ended by itself with a non-zero code crashed: keep its error lines.</summary>
    private void OnBackgroundChanged()
    {
        foreach (var app in Apps.ToList())
        {
            if (app.RunId is not int id || app.RunState != "running") continue;
            var s = _bg.Get(id);
            if (s == null || s.IsRunning) continue;
            bool crashed = s.ExitCode is int code && code != 0 && s.Status != "stopped";
            string errors = crashed ? ErrorsOf(id, null, $"exit code {s.ExitCode}") : "";
            void Apply() { app.RunState = crashed ? "crashed" : "stopped"; app.RunErrors = errors; }
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(Apply); else Apply();
        }
    }

    /// <summary>Is the page up? GET-less HEAD with a 5 s limit; "up · 200 · 84 ms" or "down · why".</summary>
    public async Task CheckUrlAsync(WatchedApp app, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app.Url)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int? status = null;
        string why = "";
        try { status = await ReadinessProbe.TryHttpHeadAsync(app.Url, 5000, ct).ConfigureAwait(false); }
        catch (Exception ex) { why = ex.Message; }
        bool up = status is int code && code < 500;
        void Apply()
        {
            app.UrlState = up ? "up" : "down";
            app.UrlDetail = status is int c ? $"{c} · {sw.ElapsedMilliseconds} ms" : (why.Length > 0 ? why : LocalizedResources.Instance["watch.url.noAnswer"]);
        }
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply); else Apply();
    }

    /// <summary>What the AI is told when the owner asks it to fix this app: the failing evidence, nothing else.</summary>
    public static string Evidence(WatchedApp app, bool thai)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(thai ? $"หน้าติดตามพบปัญหาใน {app.Name} ({app.ProjectPath}) — ตรวจหาสาเหตุจริงแล้วแก้ จากนั้นรันซ้ำให้ผ่าน:"
                           : $"The Watch page found a problem in {app.Name} ({app.ProjectPath}) — find the real cause, fix it, then run it again until it passes:");
        if (app.TestState == "failed")
        {
            sb.AppendLine().AppendLine(thai ? $"เทสต์ไม่ผ่าน: `{app.TestCommand}` {app.TestSummary}" : $"Tests failed: `{app.TestCommand}` {app.TestSummary}");
            sb.AppendLine("```").AppendLine(app.TestTail.Length > 6000 ? app.TestTail[^6000..] : app.TestTail).AppendLine("```");
        }
        if (app.RunState == "crashed")
        {
            sb.AppendLine().AppendLine(thai ? $"แอปหยุดทำงานเอง: `{app.RunCommand}`" : $"The app stopped by itself: `{app.RunCommand}`");
            sb.AppendLine("```").AppendLine(app.RunErrors).AppendLine("```");
        }
        if (app.UrlState == "down")
            sb.AppendLine().AppendLine(thai ? $"หน้าเว็บไม่ตอบ: {app.Url} ({app.UrlDetail})" : $"The page does not answer: {app.Url} ({app.UrlDetail})");
        return sb.ToString().TrimEnd();
    }
}
