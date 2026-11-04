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

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
