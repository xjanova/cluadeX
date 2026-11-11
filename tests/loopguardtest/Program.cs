// loopguardtest — the agent loop guard and the model capability estimate, offline (no model is run).
//
// Build CluadeX first (the app may be running: build it with -p:OutDir=<dir>\), then:
//   dotnet run --project tests/loopguardtest -p:CluadeXBin=<dir>\
//
// The replay in G1 is the real run that motivated the guard: "สร้าง เกม sudoku แบบเล่นผ่านเว็บ" on Ollama
// llama3.2:3b, 2026-11-04 — an empty folder, a file that never existed, and the same failing read until killed.

using System.IO;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using Call = CluadeX.Services.Agents.AgentLoopGuard.Call;

int pass = 0, fail = 0;
void Check(string id, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS {id}"); }
    else { fail++; Console.WriteLine($"FAIL {id} {detail}"); }
}

const string NotFound = @"File not found: D:\GameProject\Sudoku\sudoku.cs";
const string NoBuild = "run_build: couldn't detect a build system in the project";
Call Read(string path, string? err = null) => new("read_file", $"{{\"path\":\"{path}\"}}", err == null, err ?? "");
Call Edit(string err) => new("edit_file", "{\"path\":\"sudoku.cs\",\"find\":\"x\",\"replace\":\"y\"}", false, err);
Call Build(string err) => new("run_build", "{}", false, err);
Call Search() => new("brain_search", "{\"query\":\"sudoku game project\"}", true, "");
Call Recall() => new("brain_recall", "{}", false, "brain_recall: 'query' is required");
Call Write(string path) => new("write_file", $"{{\"path\":\"{path}\",\"content\":\"<html></html>\"}}", true, "", Mutated: true);

// ─── G1: the Sudoku run, step by step ───
{
    var g = new AgentLoopGuard();
    var steps = new List<Call>[]
    {
        new() { Search() },             // 1
        new() { Recall() },             // 2
        new() { Search() },             // 3
        new() { Read("sudoku.cs", NotFound) }, // 4
        new() { Edit(NotFound) },       // 5
        new() { Build(NoBuild) },       // 6
        new() { Search() },             // 7  third identical search → nudge
        new() { Read("sudoku.cs", NotFound) }, // 8  second identical failure → nudge
        new() { Edit(NotFound) },       // 9
        new() { Build(NoBuild) },       // 10
        new() { Read("sudoku.cs", NotFound) }, // 11
        new() { Read("sudoku.cs", NotFound) }, // 12 fourth → stop
    };
    var verdicts = steps.Select(s => g.Observe(s)).ToList();
    Check("G1.1 no verdict on steps 1-6", verdicts.Take(6).All(v => v.Action == LoopGuardAction.None),
        string.Join(",", verdicts.Take(6).Select(v => v.Action)));
    Check("G1.2 step 7 nudges the repeated search", verdicts[6] is { Action: LoopGuardAction.Nudge, Kind: "same-call", Tool: "brain_search" },
        verdicts[6].ToString());
    Check("G1.3 step 8 nudges the repeated failing read", verdicts[7] is { Action: LoopGuardAction.Nudge, Kind: "same-error", Tool: "read_file", Repeats: 2 },
        verdicts[7].ToString());
    Check("G1.4 step 11 is not nudged twice", verdicts[10].Action == LoopGuardAction.None, verdicts[10].ToString());
    Check("G1.5 step 12 stops the run", verdicts[11] is { Action: LoopGuardAction.Stop, Kind: "same-error", Tool: "read_file", Repeats: 4 },
        verdicts[11].ToString());
    Check("G1.6 the stop keeps the error", verdicts[11].Error.Contains("File not found"), verdicts[11].Error);

    string nudge = AgentLoopGuard.NudgeText(verdicts[7]);
    Check("G1.7 a not-found nudge says write_file", nudge.Contains("write_file") && nudge.Contains("does not exist"), nudge);

    var cap = ModelCapability.ForLocal("llama3.2:3b");
    string th = AgentLoopGuard.StopMessage(verdicts[11], "Ollama · llama3.2:3b", isLocal: true, cap, escalated: false, steps: 12, isThai: true);
    Check("G1.8 Thai stop message names the model and the call", th.Contains("llama3.2:3b") && th.Contains("read_file") && th.Contains("sudoku.cs"), th);
    Check("G1.9 Thai stop message says why (small model) and what to do", th.Contains("3B") && th.Contains("7B") && th.Contains("ส่งต่อให้ Claude"), th);
    string en = AgentLoopGuard.StopMessage(verdicts[11], "Ollama · llama3.2:3b", true, cap, false, 12, isThai: false);
    Check("G1.10 English stop message", en.StartsWith("⛔ Stopped") && en.Contains("Escalate to Claude"), en);
}

// ─── G2: a real change is progress and clears the counts ───
{
    var g = new AgentLoopGuard();
    g.Observe(new[] { Read("a.js", "File not found: a.js") });
    g.Observe(new[] { Read("a.js", "File not found: a.js") });
    g.Observe(new[] { Read("a.js", "File not found: a.js") });
    var w = g.Observe(new[] { Write("a.js") });
    var after = g.Observe(new[] { Read("a.js", "File not found: a.js") });
    Check("G2.1 a write returns no verdict", w.Action == LoopGuardAction.None);
    Check("G2.2 after a write the count starts again", after.Action == LoopGuardAction.None, after.ToString());
    Check("G2.3 the failed-step streak is cleared too", g.FailedStepsInARow == 1, g.FailedStepsInARow.ToString());
}

// ─── G3: the same call, written differently, is still the same call ───
{
    var g = new AgentLoopGuard();
    g.Observe(new[] { new Call("read_file", "{\"path\":\"a.cs\",\"limit\":50}", false, "File not found: a.cs") });
    var v = g.Observe(new[] { new Call("read_file", "{ \"limit\": 50, \"path\": \"a.cs \" }", false, "File not found: a.cs") });
    Check("G3.1 key order and spaces do not hide a repeat", v.Action == LoopGuardAction.Nudge, v.ToString());
    Check("G3.2 normalized args are canonical", AgentLoopGuard.NormalizeArgs("{\"b\":1,\"a\":\" x \"}") == "{\"a\":\"x\",\"b\":1}",
        AgentLoopGuard.NormalizeArgs("{\"b\":1,\"a\":\" x \"}"));
    Check("G3.3 broken JSON is kept as text", AgentLoopGuard.NormalizeArgs(" not json ") == "not json");
}

// ─── G4: different calls are not a loop ───
{
    var g = new AgentLoopGuard();
    var vs = new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs" }
        .Select(p => g.Observe(new[] { Read(p) })).ToList();
    Check("G4.1 five different successful reads say nothing", vs.All(v => v.Action == LoopGuardAction.None));
    var g2 = new AgentLoopGuard();
    var v1 = g2.Observe(new[] { Read("a.cs", "File not found: a.cs") });
    var v2 = g2.Observe(new[] { Read("b.cs", "File not found: b.cs") });
    Check("G4.2 two different failing reads say nothing", v1.Action == LoopGuardAction.None && v2.Action == LoopGuardAction.None);
    Check("G4.3 a step with no tool calls says nothing", new AgentLoopGuard().Observe(Array.Empty<Call>()).Action == LoopGuardAction.None);
}

// ─── G5: every call failing, each a different one, still ends ───
{
    var g = new AgentLoopGuard();
    LoopGuardVerdict last = LoopGuardVerdict.None;
    for (int i = 1; i <= AgentLoopGuard.FailedStepsStopAt; i++)
        last = g.Observe(new[] { new Call("run_command", $"{{\"command\":\"try{i}\"}}", false, $"error {i}") });
    Check("G5.1 six all-failing steps stop the run", last is { Action: LoopGuardAction.Stop, Kind: "all-failing" }, last.ToString());
    string th = AgentLoopGuard.StopMessage(last, "Ollama · x", true, null, false, 6, true);
    Check("G5.2 the all-failing message counts the steps", th.Contains("6 ขั้นติดกัน"), th);

    var g2 = new AgentLoopGuard();
    for (int i = 1; i <= 8; i++)
        g2.Observe(new[] { new Call("run_command", $"{{\"command\":\"try{i}\"}}", false, $"error {i}"), Read($"ok{i}.cs") });
    Check("G5.3 a step with one success breaks the streak", g2.FailedStepsInARow == 0, g2.FailedStepsInARow.ToString());
}

// ─── G6: a repeated SUCCESSFUL call stops at six ───
{
    var g = new AgentLoopGuard();
    var vs = Enumerable.Range(0, AgentLoopGuard.SameStopAt).Select(_ => g.Observe(new[] { Search() })).ToList();
    Check("G6.1 nudge at three", vs[2].Action == LoopGuardAction.Nudge && vs[3].Action == LoopGuardAction.None);
    Check("G6.2 stop at six", vs[^1] is { Action: LoopGuardAction.Stop, Kind: "same-call" }, vs[^1].ToString());
}

// ─── G7: errors that differ only in timings are the same error ───
{
    Check("G7.1 timings do not make a new error",
        AgentLoopGuard.NormalizeError("Command timed out after 30.2 s") == AgentLoopGuard.NormalizeError("Command timed out after 31 s"));
    Check("G7.2 a different message stays different",
        AgentLoopGuard.NormalizeError("File not found: a") != AgentLoopGuard.NormalizeError("File not found: b"));
}

// ─── G8: escalation reset ───
{
    var g = new AgentLoopGuard();
    for (int i = 0; i < 3; i++) g.Observe(new[] { Read("x", "File not found: x") });
    g.Reset();
    var v = g.Observe(new[] { Read("x", "File not found: x") });
    Check("G8.1 after Reset the stronger model starts clean", v.Action == LoopGuardAction.None && g.FailedStepsInARow == 1);
    var cap = ModelCapability.ForLocal("llama3.2:3b");
    var stop = new LoopGuardVerdict(LoopGuardAction.Stop, "same-error", "read_file", "{\"path\":\"x\"}", 4, "File not found: x");
    string th = AgentLoopGuard.StopMessage(stop, "Anthropic · claude-opus-5-5", isLocal: false, null, escalated: true, 20, true);
    Check("G8.2 after escalation it does not advise turning escalation on", !th.Contains("ส่งต่อให้ Claude เมื่อ") && th.Contains("โมเดลที่แรงกว่าแล้ว"), th);
    Check("G8.3 an API model is not told to get a 7B model", !th.Contains("7B"), th);
}

// ─── G9: polling and screenshots — same arguments, a result that keeps changing — are not a loop ───
{
    var g = new AgentLoopGuard();
    var vs = Enumerable.Range(1, 10)
        .Select(i => g.Observe(new[]
        {
            new Call("desktop_screenshot", "{}", true, "", Output: $"[image 1: shot-{i}.png]"),
            new Call("desktop_click", $"{{\"x\":{i * 10},\"y\":40}}", true, ""),
        })).ToList();
    Check("G9.1 ten screenshots between clicks say nothing", vs.All(v => v.Action == LoopGuardAction.None),
        string.Join(",", vs.Select(v => v.Action)));
    var g2 = new AgentLoopGuard();
    var ps = Enumerable.Range(1, 8)
        .Select(i => g2.Observe(new[] { new Call("bg_output", "{\"id\":\"srv\"}", true, "", Output: $"line {i}") })).ToList();
    Check("G9.2 a log poll with new lines says nothing", ps.All(v => v.Action == LoopGuardAction.None));
    var g3 = new AgentLoopGuard();
    var same = Enumerable.Range(1, 6)
        .Select(_ => g3.Observe(new[] { new Call("bg_output", "{\"id\":\"srv\"}", true, "", Output: "Listening on :8080") })).ToList();
    Check("G9.3 the same poll with the same answer six times stops", same[^1].Action == LoopGuardAction.Stop, same[^1].ToString());
    Check("G9.4 timings in a result do not make it new",
        AgentLoopGuard.OutputDigest("built in 3.2 s") == AgentLoopGuard.OutputDigest("built in 4 s"));
}

// ─── M: model size from its name ───
{
    (string name, double? want)[] cases =
    {
        ("llama3.2:3b", 3), ("llama3.2:1b", 1), ("qwen2.5-coder:7b", 7), ("qwen2.5-coder:14b", 14),
        ("Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", 7), (@"D:\DataAiModel\Llama-3.2-3B-Instruct-Q4_K_M.gguf", 3),
        ("deepseek-r1:1.5b", 1.5), ("phi3:3.8b", 3.8), ("gemma3n:e4b", 4), ("gemma3:4b", 4), ("mixtral:8x7b", 56),
        ("llama3.3:70b", 70), ("gpt-oss:20b", 20), ("hf.co/bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M", 7),
        ("mistral:latest", null), ("llama3.2", null), ("nomic-embed-text:v1.5", null), ("", null), (null!, null),
        ("Phi-3-mini-4k-instruct-q4.gguf", null),
        // MoE names carry the active parameters too ("A3B"): the size is the total, not 3B (2026-11-06 Sudoku2)
        ("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", 30), ("Qwen3-235B-A22B-Instruct", 235), ("qwen3:30b-a3b", 30),
    };
    foreach (var (name, want) in cases)
    {
        double? got = ModelCapability.ParseBillions(name);
        Check($"M1 '{name}' → {want?.ToString() ?? "unknown"}", got == want, $"got {got?.ToString() ?? "null"}");
    }

    Check("M2.1 3B is tiny", ModelCapability.ForLocal("llama3.2:3b").Tier == ModelCapability.Tier.Tiny);
    Check("M2.2 4B is small", ModelCapability.ForLocal("gemma3:4b").Tier == ModelCapability.Tier.Small);
    Check("M2.3 7B is capable", ModelCapability.ForLocal("qwen2.5-coder:7b").Tier == ModelCapability.Tier.Capable);
    Check("M2.4 a name without a size is unknown", ModelCapability.ForLocal("mistral:latest").Tier == ModelCapability.Tier.Unknown);

    string? w = ModelCapability.PreRunWarning(ModelCapability.ForLocal("llama3.2:3b"), escalationOn: false, isThai: true);
    Check("M3.1 a tiny model is warned about before the run", w != null && w.StartsWith("⚠") && w.Contains("3B") && w.Contains("7B"), w ?? "null");
    string? we = ModelCapability.PreRunWarning(ModelCapability.ForLocal("llama3.2:3b"), escalationOn: true, isThai: true);
    Check("M3.2 with escalation on it says a stronger model takes over", we != null && we.Contains("ส่งต่อ") && !we.Contains("ในการตั้งค่า"), we ?? "null");
    Check("M3.3 a capable model gets no warning", ModelCapability.PreRunWarning(ModelCapability.ForLocal("qwen2.5-coder:14b"), false, true) == null);
    Check("M3.4 an unknown size gets no warning", ModelCapability.PreRunWarning(ModelCapability.ForLocal("mistral:latest"), false, true) == null);
}

// ─── E: empty project folder ───
{
    string dir = Path.Combine(Path.GetTempPath(), "loopguardtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var fs = new FileSystemService();
        Check("E1 no working directory is not 'empty'", !fs.IsWorkingDirectoryEmpty());
        fs.WorkingDirectory = dir;
        Check("E2 an empty folder is empty", fs.IsWorkingDirectoryEmpty());
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Check("E3 a folder with only .git is still empty", fs.IsWorkingDirectoryEmpty());
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        Check("E4 a folder with a file is not empty", !fs.IsWorkingDirectoryEmpty());
        string note = CodeAgentService.EmptyProjectNote(dir);
        Check("E5 the empty-project note says write_file and index.html", note.Contains("write_file") && note.Contains("index.html") && note.Contains(dir));
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

// ─── F: what the 2026-11-05 failure audit found (refusals, scratch folder, stall lines, checkpoint home) ───
{
    // F1–F3: a CLI refusal that a second try cannot fix. The strings are the CLIs' own wording.
    Check("F1.1 Codex usage limit", CliRefusals.IsLimit("You've hit your usage limit. Upgrade to Pro or try again in 2 hours."));
    Check("F1.2 a 429 status", CliRefusals.IsLimit("stream error: unexpected status 429 Too Many Requests"));
    Check("F1.3 Gemini RESOURCE_EXHAUSTED", CliRefusals.IsLimit("[API Error: {\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}]"));
    Check("F1.4 a sign-in refusal is not a limit", !CliRefusals.IsLimit("Not logged in · Please run /login"));
    Check("F1.5 429 inside another number is not a limit", !CliRefusals.IsLimit("exit code 14290 at line 3.429"));
    Check("F1.6 credit balance is a sign-in fix, not a wait", !CliRefusals.IsLimit("Credit balance is too low"));
    const string Flag = "API Error: Opus 5.5's safeguards flagged this session (https://www.anthropic.com/legal/aup). You may be seeing this for the first time";
    Check("F2.1 the safeguard refusal is recognised", CliRefusals.IsSafeguardFlag(Flag));
    Check("F2.2 a limit is not a safeguard flag", !CliRefusals.IsSafeguardFlag("usage limit"));
    Check("F2.3 the Thai hint names another model and a new chat", CliRefusals.SafeguardHint(true).Contains("Sonnet") && CliRefusals.SafeguardHint(true).Contains("แชทใหม่"));
    string quotaGuidance = CluadeX.Services.Providers.OpenAIDevProvider.AuthGuidance("You've hit your usage limit.", signedIn: true) ?? "";
    string loginGuidance = CluadeX.Services.Providers.OpenAIDevProvider.AuthGuidance("Not logged in. Run codex login", signedIn: false) ?? "";
    Check("F3.1 the shown quota guidance still says a retry cannot help", CliRefusals.RetryCannotHelp(quotaGuidance), quotaGuidance);
    Check("F3.2 the sign-in guidance keeps the retry", loginGuidance.Length > 0 && !CliRefusals.RetryCannotHelp(loginGuidance), loginGuidance);
    Check("F3.3 the safeguard flag is not retried", CliRefusals.RetryCannotHelp(Flag));
    Check("F3.4 no text is no reason to stop", !CliRefusals.RetryCannotHelp(null));

    // F4: the log line keeps the CLI's words without a credential.
    string logged = CliRefusals.ForLog("Invalid API key sk-ant-api03-abcdefghijklmnop\nAuthorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload");
    Check("F4.1 an API key is redacted", !logged.Contains("abcdefghijklmnop") && logged.Contains("[redacted]"), logged);
    Check("F4.2 a bearer token is redacted", !logged.Contains("eyJhbGciOiJIUzI1NiJ9"), logged);
    Check("F4.3 newlines are folded", !logged.Contains('\n'), logged);
    Check("F4.4 a long text is capped", CliRefusals.ForLog(new string('x', 900)).Length <= 401);
    Check("F4.5 no text says so", CliRefusals.ForLog("  ") == "(no text)");

    // F5–F6: the scratch folder the file tools accept, and nothing beside it.
    string root = CluadeX.Helpers.AgentScratch.Root;
    Check("F5.1 a file in the scratch folder", CluadeX.Helpers.AgentScratch.Contains(Path.Combine(root, "p.b64")));
    Check("F5.2 the folder itself is not a file in it", !CluadeX.Helpers.AgentScratch.Contains(root));
    Check("F5.3 .. out of it is not in it", !CluadeX.Helpers.AgentScratch.Contains(Path.GetFullPath(Path.Combine(root, "..", "evil.txt"))));
    Check("F5.4 a sibling with the same prefix is not in it", !CluadeX.Helpers.AgentScratch.Contains(root + "-evil" + Path.DirectorySeparatorChar + "x.txt"));
    string proj = Path.Combine(Path.GetTempPath(), "loopguardtest-f6-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(proj);
    try
    {
        var fs = new FileSystemService { WorkingDirectory = proj };
        string scratchFile = Path.Combine(root, "loopguardtest-" + Guid.NewGuid().ToString("N") + ".txt");
        string resolved = "";
        try { resolved = fs.ResolveSafePath(scratchFile); } catch (Exception ex) { resolved = "threw " + ex.Message; }
        Check("F6.1 the scratch folder resolves by absolute path", string.Equals(resolved, scratchFile, StringComparison.OrdinalIgnoreCase), resolved);
        Check("F6.2 resolving it creates the folder", Directory.Exists(root));
        string refusal = "";
        try { fs.ResolveSafePath(@"C:\Windows\Temp\p.b64"); refusal = "no throw"; }
        catch (UnauthorizedAccessException ex) { refusal = ex.Message; }
        Check("F6.3 outside the project is still refused", refusal.StartsWith("Access denied"), refusal);
        Check("F6.4 the refusal names the scratch folder and open_project", refusal.Contains(root) && refusal.Contains("open_project"), refusal);
        string traversal = "";
        try { fs.ResolveSafePath(Path.Combine(root, "..", "x.txt")); traversal = "no throw"; }
        catch (UnauthorizedAccessException ex) { traversal = ex.Message; }
        Check("F6.5 .. out of the scratch folder is refused", traversal.StartsWith("Access denied"), traversal);
    }
    finally { try { Directory.Delete(proj, recursive: true); } catch { } }

    // F7–F8: a stall line names a timer by its Tick handler and says how busy the UI really was.
    string? timerName = null;
    var ui = new Thread(() =>
    {
        var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        d.Hooks.OperationCompleted += (_, e) =>
        {
            string n = CluadeX.Helpers.UiStallWatchdog.NameOf(e.Operation);
            if (n.StartsWith("timer ")) timerName ??= n;
        };
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, d)
        { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += StallProbe.OnTick;
        timer.Tick += (_, _) => { timer.Stop(); d.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); };
        timer.Start();
        var guard = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => d.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal), d);
        System.Windows.Threading.Dispatcher.Run();
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join(TimeSpan.FromSeconds(10));
    Check("F7.1 a timer operation is named by its Tick handler", timerName != null && timerName.Contains("StallProbe.OnTick"), timerName ?? "null");
    Check("F7.2 and by its interval", timerName != null && timerName.Contains("every 30 ms"), timerName ?? "null");
    string idle = CluadeX.Helpers.UiStallWatchdog.Describe(TimeSpan.Zero, busyMs: 40, ops: 3, stallMs: 900);
    string busy = CluadeX.Helpers.UiStallWatchdog.Describe(TimeSpan.Zero, busyMs: 820, ops: 12, stallMs: 900);
    Check("F8.1 the busy time and count are in the line", idle.Contains("UI busy 40 ms in 3 ops"), idle);
    Check("F8.2 a mostly idle UI is said to be outside dispatcher work", idle.Contains("outside dispatcher work"), idle);
    Check("F8.3 a busy UI is not", !busy.Contains("outside dispatcher work"), busy);
    Check("F8.4 a GC pause is still named", CluadeX.Helpers.UiStallWatchdog.Describe(TimeSpan.FromMilliseconds(120), 500, 4, 600).Contains("GC 120 ms"));

    // F9: restore points of a portable copy (every harness has portable.txt) stay beside it, not in ~/.cluadex.
    var cpRoot = typeof(CheckpointService).GetField("_root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(new CheckpointService()) as string ?? "";
    string expected = Path.Combine(AppContext.BaseDirectory, "Data", "checkpoints");
    Check("F9.1 the default checkpoint home follows portable.txt", string.Equals(Path.GetFullPath(cpRoot), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase), cpRoot);
    string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
    Check("F9.2 and is not the user's real data folder", !cpRoot.StartsWith(real, StringComparison.OrdinalIgnoreCase), cpRoot);
}

// ─── W: WrittenFileCheck — what an agent wrote must parse (nothing of the project runs) ───
{
    string dir = Path.Combine(Path.GetTempPath(), "cx-filecheck-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    string F(string name, string text) { string p = Path.Combine(dir, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
    async Task<IReadOnlyList<FileCheckIssue>> Chk(Func<string, string?> find, WrittenFileCheck.Runner? run, params string[] files)
        => await WrittenFileCheck.CheckAsync(files, find, run, CancellationToken.None);
    Func<string, string?> none = _ => null;
    try
    {
        var j1 = await Chk(none, null, F("package.json", "{\n  \"name\": \"x\",\n}"));
        Check("W1.1 a trailing comma in package.json is invalid JSON, at its line", j1.Count == 1 && j1[0].Line == 3 && j1[0].Message.StartsWith("invalid JSON"),
            string.Join(" | ", j1.Select(i => $"{i.Line}:{i.Message}")));
        Check("W1.2 the same in tsconfig.json (comments + trailing commas allowed) is fine",
            (await Chk(none, null, F("tsconfig.json", "{\n // c\n \"compilerOptions\": {\"strict\": true,},\n}"))).Count == 0);
        Check("W1.3 an empty JSON file is reported", (await Chk(none, null, F("data.json", "  "))).Count == 1);
        Check("W1.4 good JSON says nothing", (await Chk(none, null, F("ok.json", "{\"a\":[1,2]}"))).Count == 0);
        var x1 = await Chk(none, null, F("App.xaml", "<Window>\n  <Grid>\n</Window>"));
        Check("W2.1 a mismatched XAML tag is invalid XML at its line", x1.Count == 1 && x1[0].Line == 3, string.Join(" | ", x1.Select(i => $"{i.Line}:{i.Message}")));
        Check("W2.2 a DTD is refused quietly (no entity expansion), not called broken",
            (await Chk(none, null, F("evil.xml", "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>"))).Count == 0);
        Check("W2.3 a file of a kind it does not know is skipped", (await Chk(none, null, F("notes.txt", "{{{"))).Count == 0 && !WrittenFileCheck.Covers("a.cs"));

        // node --check, scripted (the parse of node's answer), then the real node when installed.
        string? seen = null;
        WrittenFileCheck.Runner fakeNode = (exe, args, ct) =>
        {
            seen = string.Join(" ", args);
            string file = args[^1];
            string text = File.ReadAllText(file);
            if (text.Contains("<div>")) return Task.FromResult((1, $"{file}:2\n  return <div/>;\n         ^\nSyntaxError: Unexpected token '<'\n", false));
            if (text.Contains("export ") && !file.EndsWith(".mjs")) return Task.FromResult((1, $"{file}:1\nexport const a = 1;\n^^^^^^\nSyntaxError: Unexpected token 'export'\n", false));
            int at = text.IndexOf("function (", StringComparison.Ordinal);
            if (at >= 0) return Task.FromResult((1, $"{file}:{text[..at].Count(c => c == '\n') + 1}\nfunction (\n         ^\n\nSyntaxError: Function statements require a function name\n    at wrapSafe\n", false));
            return Task.FromResult((0, "", false));
        };
        Func<string, string?> fakeFind = n => n == "node" ? "node.exe" : null;
        var js1 = await Chk(fakeFind, fakeNode, F("bad.js", "let a = 1;\n\nfunction (\n"));
        Check("W3.1 a JS syntax error: node's line and message", js1.Count == 1 && js1[0].Line == 3 && js1[0].Message.StartsWith("SyntaxError: Function statements")
            && seen!.StartsWith("--check "), string.Join(" | ", js1.Select(i => $"{i.Line}:{i.Message}")));
        Check("W3.2 JSX in a .js is not node's to judge: nothing said", (await Chk(fakeFind, fakeNode, F("comp.js", "function A(){\n  return <div/>;\n}"))).Count == 0);
        Check("W3.3 an ES module in a .js is checked again as a module, and passes", (await Chk(fakeFind, fakeNode, F("mod.js", "export const a = 1;"))).Count == 0);
        Check("W3.4 with no node installed a script is skipped, never reported", (await Chk(none, fakeNode, F("bad2.js", "function ("))).Count == 0);

        string page = F("index.html", "<!doctype html>\n<html><head>\n<link rel=\"stylesheet\" href=\"style.css\">\n<link rel=\"canonical\" href=\"/about\">\n"
            + "<script src=\"https://cdn.example/x.js\"></script>\n<script src=\"game.js?v=2\"></script>\n</head><body>\n<img src=\"{{ logo }}\">\n<img src=\"data:image/png;base64,AA\">\n"
            + "<script>\nlet x = 1;\nfunction (\n</script>\n<script type=\"application/json\">{bad json</script>\n</body></html>");
        F("style.css", "body{}");
        var h1 = await Chk(fakeFind, fakeNode, page);
        Check("W4.1 a page: the missing local script is named at its line; the CDN, canonical, template and data: links are not",
            h1.Any(i => i.Line == 6 && i.Message.Contains("game.js?v=2") && i.Message.Contains("does not exist"))
            && !h1.Any(i => i.Message.Contains("style.css") || i.Message.Contains("cdn.example") || i.Message.Contains("about") || i.Message.Contains("logo")),
            string.Join(" | ", h1.Select(i => $"{i.Line}:{i.Message}")));
        Check("W4.2 …its inline script's syntax error is at the page's own line; a JSON block is not checked as script",
            h1.Count(i => i.Message.StartsWith("inline <script>")) == 1 && h1.First(i => i.Message.StartsWith("inline <script>")).Line == 12,
            string.Join(" | ", h1.Select(i => $"{i.Line}:{i.Message}")));
        Check("W4.3 no temp copy of a script is left behind",
            !Directory.EnumerateFiles(Path.GetTempPath(), "cluadex-filecheck-*").Any());
        string note = WrittenFileCheck.Note(h1, dir);
        Check("W4.4 the note names the file relative to the project and stays short",
            note.StartsWith("[file-check] ✗") && note.Contains("- index.html:6:") && note.Length < 600, note);

        // The real node, when this PC has one (the house standard: the real tool, not only a script of it).
        if (WrittenFileCheck.FindTool("node") is { } realNode)
        {
            // Its own folder: node reads the package.json above a file, and W1.1 left a broken one in `dir`.
            string realDir = Path.Combine(Path.GetTempPath(), "cx-filecheck-real-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(realDir);
            string F2(string name, string text) { string p = Path.Combine(realDir, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
            var brokenPkg = await Chk(WrittenFileCheck.FindTool, null, F("beside-broken-pkg.js", "const a = 1;\n"));
            Check("W5.0 real node: a good script beside a broken package.json is not blamed (the JSON check reports the JSON)", brokenPkg.Count == 0,
                string.Join(" | ", brokenPkg.Select(i => $"{i.Line}:{i.Message}")));
            try
            {
            var r1 = await Chk(WrittenFileCheck.FindTool, null, F2("real-bad.js", "const a = 1;\nif (a {\n}\n"));
            Check("W5.1 real node --check finds the error and its line", r1.Count == 1 && r1[0].Line == 2 && r1[0].Message.StartsWith("SyntaxError"),
                string.Join(" | ", r1.Select(i => $"{i.Line}:{i.Message}")));
            Check("W5.2 real node: a good script says nothing", (await Chk(WrittenFileCheck.FindTool, null, F2("real-ok.js", "const a = [1,2].map(x => x * 2);\nconsole.log(a);\n"))).Count == 0);
            Check("W5.3 real node: an ES module .js passes (re-checked as .mjs)", (await Chk(WrittenFileCheck.FindTool, null, F2("real-mod.js", "import fs from 'fs';\nexport const a = 1;\n"))).Count == 0);
            string sudoku = F2("sudoku\\index.html", "<html><body>\n<div id=b></div>\n<script>\nfunction solve(board) {\n  for (let i = 0; i < 81; i++ {\n  }\n}\n</script>\n</body></html>");
            var r2 = await Chk(WrittenFileCheck.FindTool, null, sudoku);
            Check("W5.4 real node: the page's inline script error is at the page's line", r2.Count == 1 && r2[0].Line == 5 && r2[0].Message.Contains("SyntaxError"),
                string.Join(" | ", r2.Select(i => $"{i.Line}:{i.Message}")));
            Check("W5.5 checking does not run the script (a script that would write a file did not)",
                (await Chk(WrittenFileCheck.FindTool, null, F2("side.js", $"require('fs').writeFileSync({System.Text.Json.JsonSerializer.Serialize(Path.Combine(realDir, "ran.txt"))}, 'x');"))).Count == 0
                && !File.Exists(Path.Combine(realDir, "ran.txt")));
            }
            finally { try { Directory.Delete(realDir, true); } catch { } }
        }
        else Console.WriteLine("SKIP W5 (node is not installed)");
        if (WrittenFileCheck.FindTool("python") is { })
        {
            var py = await Chk(WrittenFileCheck.FindTool, null, F("bad.py", "def f(:\n    pass\n"), F("ok.py", "print(1)\n"));
            Check("W6.1 real Python: the syntax error and its line, the good file says nothing", py.Count == 1 && py[0].Line == 1 && py[0].Path.EndsWith("bad.py"),
                string.Join(" | ", py.Select(i => $"{i.Path}:{i.Line}:{i.Message}")));
        }
        else Console.WriteLine("SKIP W6 (no real Python — the Store stub is not one)");
        Check("W8 a file over 2 MB (a bundle, a dump) is not checked", (await Chk(none, null, F("huge.json", "{" + new string(' ', 2 * 1024 * 1024 + 10)))).Count == 0);
        Check("W7 at most 10 issues, whatever was written", (await Chk(none, null, Enumerable.Range(0, 15).Select(i => F($"b{i}.json", "{")).ToArray())).Count == WrittenFileCheck.MaxIssues);
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ─── P: PageLoadCheck — a page that parses must also open (headless browser) ───
{
    string dir = Path.Combine(Path.GetTempPath(), "cx-pagecheck-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    string F(string name, string text) { string p = Path.Combine(dir, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
    string Url(string p) => new Uri(p).AbsoluteUri;
    try
    {
        // Which pages: the ones written, and a page that loads a script that was written (same folder or one up).
        string index = F("site\\index.html", "<html><body><script src=\"js/game.js\"></script></body></html>");
        string about = F("site\\about.html", "<html><body>no scripts</body></html>");
        string game = F("site\\js\\game.js", "let a = 1;");
        string solo = F("solo\\page.html", "<html></html>");
        var pp = PageLoadCheck.PagesFor([game]);
        Check("P1.1 a written script opens the page that loads it (one folder up), not the page that does not",
            pp.Count == 1 && pp[0].Equals(index, StringComparison.OrdinalIgnoreCase), string.Join(" | ", pp));
        Check("P1.2 a written page is opened itself", PageLoadCheck.PagesFor([solo]).SequenceEqual([solo]));
        var many = Enumerable.Range(0, 6).Select(i => F($"m\\p{i}.html", "<html></html>")).ToArray();
        Check("P1.3 at most 3 pages", PageLoadCheck.PagesFor(many).Count == PageLoadCheck.MaxPages);
        Check("P1.4 a missing file and a stylesheet open nothing", PageLoadCheck.PagesFor([Path.Combine(dir, "gone.html"), F("site\\s.css", "a{}")]).Count == 0
            && !pp.Contains(about));

        // The browser's answer, scripted: what Chrome 1xx really printed for these cases (2026-11-11).
        string? seenExe = null; IReadOnlyList<string>? seenArgs = null;
        string Log(string msg, string src, int line) => $"[50028:1124:1111/014434.493:INFO:CONSOLE:{line}] \"{msg}\", source: {src} ({line})";
        WrittenFileCheck.Runner Fake(string stderr, bool timedOut = false, bool dom = true) => (exe, args, ct) =>
        {
            seenExe = exe; seenArgs = args;
            return Task.FromResult((timedOut ? -1 : 0, stderr + "\n" + (dom ? "<html><head></head><body></body></html>" : ""), timedOut));
        };
        Func<string?> chrome = () => "chrome.exe";
        string jsFile = Path.Combine(dir, "site", "js", "game.js");
        string out1 = string.Join("\n",
            Log("Uncaught ReferenceError: undefinedThing is not defined", Url(index), 2),
            Log("Uncaught ReferenceError: undefinedThing is not defined", Url(index), 2),
            Log("custom error log", Url(index), 4),
            Log("Access to fetch at 'file:///D:/x/data.json' from origin 'null' has been blocked by CORS policy: Cross origin requests are only supported", Url(index), 0),
            Log("Uncaught (in promise) TypeError: Failed to fetch", Url(index), 3),
            Log("Uncaught TypeError: Cannot read properties of null (reading 'x')", Url(jsFile), 7),
            Log("Uncaught Error: thrown inside the library", "https://cdn.example/lib.js", 1));
        var p2 = await PageLoadCheck.CheckAsync([index], chrome, Fake(out1), CancellationToken.None);
        Check("P2.1 the page's own uncaught errors, once each, at their file and line",
            p2.Count == 2 && p2.Any(i => i.Path.Equals(index, StringComparison.OrdinalIgnoreCase) && i.Line == 2 && i.Message == "page load: Uncaught ReferenceError: undefinedThing is not defined")
            && p2.Any(i => i.Path.Equals(jsFile, StringComparison.OrdinalIgnoreCase) && i.Line == 7),
            string.Join(" | ", p2.Select(i => $"{Path.GetFileName(i.Path)}:{i.Line}:{i.Message}")));
        Check("P2.2 console.error, a file:// CORS block, 'Failed to fetch' and an error inside a CDN script are not the page's fault",
            !p2.Any(i => i.Message.Contains("custom") || i.Message.Contains("CORS") || i.Message.Contains("fetch") || i.Message.Contains("library")));
        Check("P2.3 the browser runs headless on its own throwaway profile, the page as a file URL",
            seenExe == "chrome.exe" && seenArgs!.Contains("--headless=new") && seenArgs.Contains("--disable-extensions")
            && seenArgs.Any(a => a.StartsWith("--user-data-dir=" + Path.Combine(Path.GetTempPath(), "cluadex-pagecheck-"), StringComparison.OrdinalIgnoreCase))
            && seenArgs[^1] == Url(index), string.Join(" ", seenArgs ?? []));
        Check("P2.4 the profile folder is gone afterwards", !Directory.EnumerateDirectories(Path.GetTempPath(), "cluadex-pagecheck-*").Any());
        var p3 = await PageLoadCheck.CheckAsync([index], chrome, Fake("", timedOut: true), CancellationToken.None);
        Check("P3.1 a page that never finishes loading is reported as a hang", p3.Count == 1 && p3[0].Message == PageLoadCheck.HangMessage);
        Check("P3.2 a browser that gives no page back says nothing (never blames the page)",
            (await PageLoadCheck.CheckAsync([index], chrome, Fake(out1, dom: false), CancellationToken.None)).Count == 0);
        Check("P3.3 no browser on the PC: nothing said", (await PageLoadCheck.CheckAsync([index], () => null, Fake(out1), CancellationToken.None)).Count == 0);
        string note = PageLoadCheck.Note(p2, Path.Combine(dir, "site"));
        Check("P3.4 the note names the files relative to the project and stays short",
            note.StartsWith("[page-check] ✗") && note.Contains("- index.html:2:") && note.Contains("- js/game.js:7:") && note.Length < 700, note);

        // The real browser, when this PC has one.
        if (CluadeX.Services.Voice.ChromeDictationService.FindBrowser()?.Path is { } real)
        {
            Func<string?> realFind = () => real;
            string ok = F("real\\ok.html", "<!doctype html><html><body><canvas id=c></canvas><script>\nlet n = 0;\nfunction f(){ n++; requestAnimationFrame(f); }\nrequestAnimationFrame(f);\nsetInterval(() => {}, 16);\n</script></body></html>");
            var r1 = await PageLoadCheck.CheckAsync([ok], realFind, null, CancellationToken.None);
            Check("P4.1 real browser: a game loop (requestAnimationFrame + setInterval) is not a hang", r1.Count == 0,
                string.Join(" | ", r1.Select(i => $"{i.Line}:{i.Message}")));
            string err = F("real\\err.html", "<!doctype html><html><body><script>\nfunction start() {\n  undefinedThing.go();\n}\nsetTimeout(start, 500);\nfetch('data.json').then(r => r.json());\n</script></body></html>");
            var r2 = await PageLoadCheck.CheckAsync([err], realFind, null, CancellationToken.None);
            Check("P4.2 real browser: an error thrown later (setTimeout) is caught at its line; the file:// fetch is not",
                r2.Count == 1 && r2[0].Line == 3 && r2[0].Message.Contains("undefinedThing"), string.Join(" | ", r2.Select(i => $"{i.Line}:{i.Message}")));
            string hang = F("real\\hang.html", "<!doctype html><html><body><script>\nfunction solve(i) { while (true) { i++; } }\nsolve(0);\n</script></body></html>");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r3 = await PageLoadCheck.CheckAsync([hang], realFind, null, CancellationToken.None);
            Check("P4.3 real browser: the Sudoku freeze (a script that never returns) is reported as a hang, in about 15 s",
                r3.Count == 1 && r3[0].Message == PageLoadCheck.HangMessage && sw.Elapsed < TimeSpan.FromSeconds(25), $"{sw.Elapsed.TotalSeconds:N1} s · " + string.Join(" | ", r3.Select(i => i.Message)));
            // A profile folder can only be deleted once every browser process on it has exited (the hang was killed as a tree).
            Check("P4.4 real browser: no profile is left behind (so no browser still runs on one)",
                !Directory.EnumerateDirectories(Path.GetTempPath(), "cluadex-pagecheck-*").Any());
        }
        else Console.WriteLine("SKIP P4 (no Chrome or Edge on this PC)");
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

// ─── T: ToolTrackRecord — the next run of a model hears what it keeps getting wrong here ───
{
    string file = Path.Combine(Path.GetTempPath(), "cx-track-" + Guid.NewGuid().ToString("N")[..8] + ".json");
    try
    {
        var rec = new ToolTrackRecord(file);
        const string M = "Ollama · qwen3.5:4b";
        Check("T1 no record: no hint", rec.HintsFor(M) == null);
        for (int i = 0; i < 7; i++) rec.Record(M, "edit_file", false, "Text not found in app.js. Matching is newline- and whitespace-tolerant, but the lines must exist.");
        for (int i = 0; i < 5; i++) rec.Record(M, "edit_file", true, null);
        for (int i = 0; i < 4; i++) rec.Record(M, "read_file", false, @"File not found: D:\p\sudoku.cs");
        for (int i = 0; i < 20; i++) rec.Record(M, "read_file", true, null);
        for (int i = 0; i < 10; i++) rec.Record(M, "run_command", false, "The user denied this command.");
        rec.Record(M, "run_command", true, null);
        string? h = rec.HintsFor(M);
        Check("T2 7 of 12 edits failed on 'text not found': hint with the counts and the read-first / write_file advice",
            h != null && h.Contains("edit_file: failed 7 of 12") && h.Contains("read_file the exact lines") && h.Contains("write_file"), h);
        Check("T3 4 of 24 reads failed (17%, under 30%): no hint for read_file", h != null && !h.Contains("read_file: failed"), h);
        Check("T4 the user's refusals are not the model's mistakes: not counted", rec.StatsFor(M)["run_command"] is { Calls: 1, Fails: 0 });
        Check("T5 another model has its own record", rec.HintsFor("Anthropic · claude-opus-5-5") == null);
        Check("T6 a tool not offered this run is not hinted", rec.HintsFor(M, ["read_file", "write_file"]) == null);
        Check("T7 the kinds", ToolTrackRecord.Classify("must read the file first") == "must-read"
            && ToolTrackRecord.Classify("Absolute paths are not allowed. Use relative paths") == "outside-project"
            && ToolTrackRecord.Classify("'query' is required") == "bad-args"
            && ToolTrackRecord.Classify("Command timed out after 120 s") == "timeout"
            && ToolTrackRecord.Classify("something odd") == "other");
        rec.Save();
        var again = new ToolTrackRecord(file);
        Check("T8 the record survives a restart (atomic file)", again.HintsFor(M) == h && !File.Exists(file + ".tmp"));
        for (int i = 0; i < 200; i++) again.Record(M, "edit_file", true, null);
        var st = again.StatsFor(M)["edit_file"];
        Check("T9 past 200 calls the counts halve: a model that learned is not reminded forever", st.Calls < 200 && st.Fails <= 4 && again.HintsFor(M) == null,
            $"{st.Calls}/{st.Fails}");
        File.WriteAllText(file, "{ not json");
        Check("T10 an unreadable file starts a fresh record, never fails", new ToolTrackRecord(file).HintsFor(M) == null);
        File.WriteAllText(file, "{\"Version\":1,\"Models\":{\"m\":{\"edit_file\":{\"Calls\":2,\"Fails\":9,\"Kinds\":{}}}}}");
        Check("T11 a hand-edited impossible count (fails > calls) is dropped", new ToolTrackRecord(file).StatsFor("m").Count == 0);
        var many = new ToolTrackRecord(null);
        for (int i = 0; i < ToolTrackRecord.MaxModels + 5; i++) many.Record("model-" + i, "read_file", true, null);
        Check("T12 at most 60 models are kept (the oldest goes)", many.StatsFor("model-0").Count == 0 && many.StatsFor("model-64").Count == 1);
    }
    finally { try { File.Delete(file); } catch { } }
}

// ─── R: SplitReports — a split chat's report goes back to its parent, and the two can message each other ───
{
    SplitReports.Decision D(bool running = false, bool auto = true, bool pStop = false, bool pFail = false, bool agent = true,
        bool cStop = false, int sib = 0, bool wake = true) => SplitReports.Decide(running, auto, pStop, pFail, agent, cStop, sib, wake);
    Check("R1 a working parent gets the report as a note at its next step (whatever else holds)",
        D(running: true).Delivery == SplitReports.Delivery.NoteToRun && D(running: true, auto: false, cStop: true, agent: false, sib: 3, wake: false).Delivery == SplitReports.Delivery.NoteToRun);
    Check("R2 an idle parent whose last split just finished is woken", D() == new SplitReports.Decision(SplitReports.Delivery.Wake, SplitReports.Reason.None));
    Check("R3 other splits still working: the report waits for the last one (one turn for all)",
        D(sib: 2) == new SplitReports.Decision(SplitReports.Delivery.Wait, SplitReports.Reason.SiblingsWorking));
    Check("R4 no wake when: the split was stopped, the owner drove it, auto-continue is off, the parent was stopped / failed, the wake limit is used",
        D(cStop: true).Why == SplitReports.Reason.ChildStopped && D(agent: false).Why == SplitReports.Reason.OwnerDrove
        && D(auto: false).Why == SplitReports.Reason.AutoOff && D(pStop: true).Why == SplitReports.Reason.TargetStopped
        && D(pFail: true).Why == SplitReports.Reason.TargetFailed && D(wake: false).Why == SplitReports.Reason.WakeLimit
        && new[] { D(cStop: true), D(agent: false), D(auto: false), D(pStop: true), D(pFail: true), D(wake: false) }.All(d => d.Delivery == SplitReports.Delivery.Wait));
    Check("R5 a message: a working chat gets a note, an idle one a turn, a stopped one or one at its limit keeps it as a row",
        SplitReports.DecideMessage(true, true, false).Delivery == SplitReports.Delivery.NoteToRun
        && SplitReports.DecideMessage(false, false, true).Delivery == SplitReports.Delivery.Wake
        && SplitReports.DecideMessage(false, true, true).Why == SplitReports.Reason.TargetStopped
        && SplitReports.DecideMessage(false, false, false).Why == SplitReports.Reason.WakeLimit);

    string longAnswer = string.Join(" ", Enumerable.Range(1, 1200).Select(i => "word" + i));
    string clipped = SplitReports.Clip(longAnswer, 400, oneLine: true);
    Check("R6 the shown report is cut at a word, marked, never longer than asked",
        clipped.Length <= 402 && clipped.EndsWith(" …") && longAnswer.StartsWith(clipped[..^2] + " "), clipped[^30..]);
    Check("R6 a short answer is kept as it is; one line squashes white space, otherwise lines stay",
        SplitReports.Clip("All 41 tests pass.", 400) == "All 41 tests pass." && SplitReports.Clip("a\n\n  b", 40, oneLine: true) == "a b"
        && SplitReports.Clip("a\r\nb", 40) == "a\nb" && SplitReports.Clip(null, 10) == "" && SplitReports.Clip("   ", 10) == "");
    string report = SplitReports.ReportForModel("API split", "id-1", "CX-12345678", SplitReports.Outcome(false, false), longAnswer, "Its work is on branch cluadex/split-api.");
    Check("R7 the parent's agent reads who, how it ended, the report (up to 4000 chars), the branch, and how to read the rest",
        report.StartsWith("Split chat \"API split\" (ref CX-12345678, id id-1) ended a turn. Its report:\n")
        && report.Contains("cluadex/split-api") && report.Contains("close_chat with chat \"CX-12345678\" and read \"true\"")
        && report.Length < SplitReports.ModelChars + 400, report.Length.ToString());
    Check("R7 a stopped or failed split says so; no answer says so",
        SplitReports.Outcome(true, false) == "was stopped" && SplitReports.Outcome(false, true) == "ended with an error"
        && SplitReports.ReportForModel("x", "i", "CX-1", "ended a turn", "", null).EndsWith("It gave no answer."));
    string instr = SplitReports.SplitInstruction("Main chat", "Fix the parser\nand commit");
    Check("R8 a split chat is told who opened it, for what, that its answer goes back by itself, and how to ask the parent",
        instr.Contains("\"Main chat\"") && instr.Contains("Fix the parser and commit") && instr.Contains("goes back to that chat by itself")
        && instr.Contains("message_chat with chat \"parent\""));
    Check("R9 a message names its sender and, from a split, how to answer it",
        SplitReports.MessageForModel(true, "Main", "CX-1", "p", "add tests").StartsWith("Message from the chat that opened you, \"Main\"")
        && SplitReports.MessageForModel(false, "API", "CX-2", "c", "which port?").Contains("answer it with message_chat chat \"CX-2\""));
    Check("R9 a message from a sibling split says it is this chat's work",
        SplitReports.MessageForModel(false, "UI", "CX-3", "s", "also fix the button", sibling: true).Contains("another split chat of the chat that opened you"));

    // R14–R16: which chat a piece of work belongs to (pure, no model call)
    var openKids = new List<SplitReports.OpenSplit>
    {
        new("a", "CX-A", "แก้ปุ่ม login หน้าแรก", "แก้ปุ่ม login หน้าแรก", Working: true),
        new("b", "CX-B", "Fix the API parser tests", "Fix the API parser tests", Working: false, Gist: "All 41 tests pass; branch cluadex/split-api."),
    };
    Check("R14 the same or close task (Thai or English, other wording around it) finds the open split chat",
        SplitReports.SameWork("แก้ปุ่ม login หน้าแรกให้กดได้บนมือถือด้วย", openKids)?.Id == "a"
        && SplitReports.SameWork("fix the api parser tests again", openKids)?.Id == "b"
        && SplitReports.SameWork("Fix the API parser tests", openKids)?.Id == "b",
        SplitReports.Likeness("แก้ปุ่ม login หน้าแรกให้กดได้บนมือถือด้วย", "แก้ปุ่ม login หน้าแรก").ToString("0.00"));
    Check("R14 different work finds none; short or empty text never matches by accident",
        SplitReports.SameWork("ทำหน้า dashboard ยอดขายรายเดือน", openKids) == null
        && SplitReports.SameWork("Write the release notes for 3.0.312", openKids) == null
        && SplitReports.SameWork("fix", openKids) == null && SplitReports.SameWork("", openKids) == null
        && SplitReports.SameWork("Fix the API parser tests", Array.Empty<SplitReports.OpenSplit>()) == null
        && SplitReports.Likeness("ab", "ab c") == 0);
    string refusal = SplitReports.SameWorkRefusal(openKids[0]);
    Check("R15 new_chat's refusal names the chat, says message_chat and how to open one anyway",
        refusal.Contains("\"CX-A\"") && refusal.Contains("message_chat") && refusal.Contains("again \"true\"") && refusal.Contains("working"));
    string? kidsNote = SplitReports.ChildrenNote(openKids);
    Check("R16 the parent's note lists each open split (working / finished + gist) and where new work goes; none = no note",
        SplitReports.ChildrenNote(Array.Empty<SplitReports.OpenSplit>()) == null
        && kidsNote != null && kidsNote.Contains("CX-A") && kidsNote.Contains("— working") && kidsNote.Contains("finished: All 41 tests pass")
        && kidsNote.Contains("message_chat") && kidsNote.Contains("how things stand") && kidsNote.Length < 900, kidsNote?.Length.ToString() ?? "null");
    var many = Enumerable.Range(1, 12).Select(i => new SplitReports.OpenSplit("i" + i, "CX-" + i, "task " + i, "task " + i, Working: true)).ToList();
    string manyNote = SplitReports.ChildrenNote(many)!;
    Check("R16 a long list stays short: the first 8, then a count",
        manyNote.Contains("CX-8") && !manyNote.Contains("CX-9 ") && manyNote.Contains("and 4 more"));

    Check("R10 the wake turn says the reports are above, to carry on from them and not redo them",
        SplitReports.WakeForModel(1).StartsWith("The split chat you started has ended its turn")
        && SplitReports.WakeForModel(3).StartsWith("The 3 split chats") && SplitReports.WakeForModel(2).Contains("do not redo"));

    var lim = new SplitWakeLimit { MaxWakes = 3, Window = TimeSpan.FromHours(2) };
    var t0 = new DateTime(2026, 11, 11, 10, 0, 0);
    bool[] took = Enumerable.Range(0, 4).Select(i => lim.TryTake("p", t0.AddMinutes(i))).ToArray();
    Check("R11 at most MaxWakes turns per chat in the window; another chat has its own count",
        took.SequenceEqual(new[] { true, true, true, false }) && !lim.WouldAllow("p", t0.AddMinutes(5)) && lim.TryTake("q", t0));
    Check("R11 the window slides: once the oldest is older than the window, one more is allowed",
        lim.WouldAllow("p", t0.AddHours(2).AddSeconds(1)) && lim.TryTake("p", t0.AddHours(2).AddSeconds(1)) && !lim.TryTake("p", t0.AddHours(2).AddSeconds(2)));
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

/// <summary>A named Tick handler, so F7 can see it in the stall line.</summary>
static class StallProbe
{
    public static void OnTick(object? sender, EventArgs e) { }
}
