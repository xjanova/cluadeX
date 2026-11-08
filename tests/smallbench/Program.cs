using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace SmallBench;

/// <summary>
/// The small-model bench: 11 real coding tasks on a throwaway copy of a tiny Python repo (fixture/), each run through
/// CluadeX's OWN agent loop (App.ConfigureServices DI, the real CodeAgentService, the real tools) on one model, graded
/// by hidden checks the model never sees. One JSON line per task is written as soon as the task ends (a power cut
/// must not lose the whole run), then a Markdown table.
///
///   dotnet run -- --provider ollama --model deepseek-r1:8b [--ctx 8192] [--label before] [--tasks T01,T04]
///   dotnet run -- --provider anthropic --model claude-haiku-5-5        (API stand-in: costs real money)
///   dotnet run -- --selfcheck                                           (offline: every check fails on the untouched
///                                                                        fixture and passes on a known-good result)
///   dotnet run -- --compare results/before.jsonl results/after.jsonl
///   dotnet run -- --regrade results/before-x.jsonl                     (re-grade the stored answers after a checker fix)
///
/// Per task it records: pass/fail, steps, tool calls, tool errors, the most-repeated identical call, a loop stop,
/// "ungrounded" (an answer about the code with no successful read/search this run), "wrong" (an answer task whose
/// stated facts fail the check), "false done" (an edit that fails its check while the answer claims success), and
/// wall time. BrainX is off: the bench measures the loop, not the owner's memory.
/// </summary>
internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static int Main(string[] args)
    {
        // The bench measures real coding: the model's commands (python, run_build, run_command) must really run, in the
        // throwaway copy of the fixture. With the default dry-run every command "succeeded" without running, which hid the
        // compiler error the model was meant to read (first bench run, 2026-11-08). Same opt-out as tests/apitest; every
        // safety guard on the command still applies.
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static string Arg(string[] args, string name, string fallback = null)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    static string FixtureDir()
    {
        // bin/<cfg>/net8.0-windows/ → project dir; or next to the exe when copied.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                foreach (var rel in new[] { "fixture", Path.Combine("tests", "smallbench", "fixture") })   // also from the repo root
                    if (File.Exists(Path.Combine(d.FullName, rel, "shopkit", "cart.py"))) return Path.Combine(d.FullName, rel);
        throw new DirectoryNotFoundException("fixture/shopkit not found above " + AppContext.BaseDirectory);
    }

    static async Task<int> Run(string[] args)
    {
        string fixture = FixtureDir();
        if (args.Contains("--compare")) return Compare(Arg(args, "--compare"), args[^1]);
        if (args.Contains("--selfcheck")) return SelfCheck(fixture);
        if (args.Contains("--regrade")) return Regrade(fixture, Arg(args, "--regrade"));

        string provider = Arg(args, "--provider", "ollama").ToLowerInvariant();
        string model = Arg(args, "--model") ?? throw new ArgumentException("--model is required");
        int ctx = int.Parse(Arg(args, "--ctx", "8192"));
        int maxSteps = int.Parse(Arg(args, "--max-steps", "15"));
        int timeoutMin = int.Parse(Arg(args, "--task-timeout-min", "6"));
        string label = Arg(args, "--label", "run");
        string outDir = Arg(args, "--out", Path.Combine(Path.GetDirectoryName(fixture)!, "results"));
        string workRoot = Arg(args, "--work", Path.Combine(Path.GetTempPath(), "cluadex-smallbench"));
        var only = Arg(args, "--tasks")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var tasks = Tasks.All(fixture).Where(t => only == null || only.Contains(t.Id)).ToList();

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(workRoot);
        string safeModel = Regex.Replace(model, @"[^A-Za-z0-9.\-]+", "_");
        string jsonl = Path.Combine(outDir, $"{label}-{provider}-{safeModel}.jsonl");
        if (File.Exists(jsonl) && !args.Contains("--append")) File.Delete(jsonl);
        Console.WriteLine($"# small-model bench · {provider} · {model} · ctx {ctx} · max steps {maxSteps} · {tasks.Count} tasks · label {label}");
        Console.WriteLine($"results → {jsonl}\n");

        var rows = new List<Row>();
        foreach (var task in tasks)
        {
            var row = await RunOne(task, fixture, workRoot, provider, model, ctx, maxSteps, timeoutMin, label);
            rows.Add(row);
            File.AppendAllText(jsonl, JsonSerializer.Serialize(row, Json) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"{row.Id} {(row.Pass ? "PASS" : "FAIL")} · {row.Seconds:0}s · steps {row.Steps} · calls {row.ToolCalls} · errors {row.ToolErrors} · maxRepeat {row.MaxRepeat}"
                + $" · stop {row.Stop}{(row.Ungrounded ? " · UNGROUNDED" : "")}{(row.Wrong ? " · WRONG" : "")}{(row.FalseDone ? " · FALSE-DONE" : "")} · {row.Why}\n");
        }

        string md = Path.ChangeExtension(jsonl, ".md");
        File.WriteAllText(md, Table($"{label} · {provider} · {model} · ctx {ctx}", rows), new UTF8Encoding(false));
        Console.WriteLine(Table($"{label} · {provider} · {model} · ctx {ctx}", rows));
        Console.WriteLine($"\ntable → {md}");
        return 0;
    }

    internal sealed record Row(string Id, string Kind, string Shape, bool Pass, string Why, int Steps, int ToolCalls, int ToolErrors,
        int MaxRepeat, string Stop, bool Loop, bool Ungrounded, bool Wrong, bool FalseDone, double Seconds,
        List<string> Calls, string Answer, string Model);

    static async Task<Row> RunOne(BenchTask task, string fixture, string workRoot, string provider, string model, int ctx,
        int maxSteps, int timeoutMin, string label)
    {
        string work = Path.Combine(workRoot, $"{label}-{task.Id}-{Guid.NewGuid().ToString("N")[..6]}");
        CopyDir(fixture, work);
        task.Setup?.Invoke(work);
        RunProcess("git", "init -q", work);
        RunProcess("git", "-c user.email=bench@localhost -c user.name=bench add -A", work);
        RunProcess("git", "-c user.email=bench@localhost -c user.name=bench commit -q -m fixture", work);
        Console.WriteLine($"── {task.Id} [{task.Shape}] {task.Prompt}");

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        using var sp = services.BuildServiceProvider();
        var settings = sp.GetRequiredService<SettingsService>();
        Directory.CreateDirectory(settings.DataRoot);
        File.WriteAllText(Path.Combine(settings.DataRoot, "mcp_servers.json"), "{\"mcpServers\":{}}");
        // Each task starts clean: the tool track record (Agents/ToolTrackRecord) learns per model, so without this an
        // "after" run would get hints learned during the "before" run.
        foreach (var f in new[] { "tool-record.json" }) try { File.Delete(Path.Combine(settings.DataRoot, f)); } catch { }
        SilenceHooks(sp);

        AiProviderType type = provider switch
        {
            "ollama" => AiProviderType.Ollama,
            "anthropic" => AiProviderType.Anthropic,
            "openai" => AiProviderType.OpenAI,
            _ => throw new ArgumentException("--provider ollama|anthropic|openai"),
        };
        string key = type == AiProviderType.Ollama ? null : ReadUserKey(type.ToString());
        if (type != AiProviderType.Ollama && string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"no {type} key saved in ~/.cluadex/settings.json");
        settings.UpdateSettings(s =>
        {
            s.ActiveProvider = type;
            s.ProviderConfigs[type.ToString()] = new ProviderConfig { ApiKey = key, SelectedModel = model };
            s.Language = "th";
            s.MaxAgentIterations = maxSteps;
            s.ContextSize = (uint)ctx;
            s.EscalationEnabled = false;
        });
        sp.GetRequiredService<FileSystemService>().WorkingDirectory = work;
        var agent = sp.GetRequiredService<CodeAgentService>();
        agent.InvalidateSystemPromptCache();
        sp.GetRequiredService<AgentToolService>().OnPermissionRequired += _ => Task.FromResult(true);
        var providers = sp.GetRequiredService<AiProviderManager>();
        await providers.SwitchProviderAsync(type);

        // A step keeps only each call's NAME (CodeAgentService rebuilds step.ToolCalls from the results), so the
        // argument shown and compared is the status line the app itself shows for the call ("Reading shopkit/cart.py").
        var starts = new List<(string Name, string Status)>();
        agent.OnToolStarting += (name, status) => { lock (starts) starts.Add((name, status ?? "")); };
        var sw = Stopwatch.StartNew();
        AgentLoopResult result = null;
        string stop;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin)))
        {
            try
            {
                var progress = new Progress<string>(p => Console.WriteLine("   · " + Cut(p.Replace('\n', ' '), 160)));
                result = await agent.ExecuteAgenticAsync(new List<ChatMessage>(), task.Prompt, progress, cts.Token);
                stop = result.StopReason;
            }
            catch (OperationCanceledException) { stop = "timeout"; }
            catch (Exception ex) { stop = "exception: " + Cut(ex.Message, 120); }
        }
        double secs = sw.Elapsed.TotalSeconds;

        var calls = new List<string>();
        var counts = new Dictionary<string, int>();
        int errors = 0;
        bool grounded = false;
        int next = 0;
        foreach (var step in result?.Steps ?? new List<AgentStep>())
            for (int i = 0; i < step.ToolCalls.Count; i++)
            {
                var c = step.ToolCalls[i];
                var r = i < step.ToolResults.Count ? step.ToolResults[i] : null;
                string name = c.McpToolName != null ? $"{c.McpServerName}:{c.McpToolName}" : c.ToolName;
                string status = "";
                lock (starts)
                    for (int k = next; k < starts.Count; k++)
                        if (string.Equals(starts[k].Name, c.ToolName, StringComparison.OrdinalIgnoreCase)
                            || starts[k].Name.EndsWith(c.ToolName, StringComparison.OrdinalIgnoreCase)
                            || c.ToolName.EndsWith(starts[k].Name, StringComparison.OrdinalIgnoreCase))
                        { status = starts[k].Status; next = k + 1; break; }
                string arg = Cut(string.Join(" ", new[] { status, r?.FilePath ?? "" }.Where(x => x.Length > 0).Distinct()), 90);
                bool ok = r?.Success == true;
                if (!ok) errors++;
                if (ok && IsReadOrSearch(name)) grounded = true;
                string sig = name + "|" + arg;
                counts[sig] = counts.GetValueOrDefault(sig) + 1;
                calls.Add($"{name}({arg}) {(ok ? "ok" : "ERR " + Cut(r?.Error ?? r?.Output ?? "", 80))}");
            }
        string answer = result?.FinalResponse ?? "";
        int maxRepeat = counts.Count == 0 ? 0 : counts.Values.Max();

        bool pass; string why;
        if (task.Kind == "answer") (pass, why) = Grade(task, answer);
        else (pass, why) = RunCheck(task, work);
        if (pass) why = "ok";

        bool ungrounded = task.Kind == "answer" && !grounded && answer.Trim().Length > 0;
        bool wrong = task.Kind == "answer" && !pass && answer.Trim().Length > 0 && !AdmitsFailure(answer);
        bool admitsFailure = AdmitsFailure(answer);
        bool falseDone = task.Kind == "edit" && !pass && !admitsFailure && stop is not ("loop" or "timeout" or "error" or "context");
        bool loop = stop == "loop" || maxRepeat >= 3;

        return new Row(task.Id, task.Kind, task.Shape, pass, why, result?.TurnCount ?? 0, calls.Count, errors, maxRepeat, stop, loop,
            ungrounded, wrong, falseDone, Math.Round(secs, 1), calls, Cut(answer, 1500), model);
    }

    /// <summary>No hook runs during the bench — not the owner's plugins', not the bundled ones: the bench measures the loop.
    /// Same approach as tests/apitest ImageWire: HookService's cache becomes an empty list and stays one.</summary>
    static void SilenceHooks(IServiceProvider sp)
    {
        var hooks = sp.GetRequiredService<HookService>();
        var cache = typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HookService._cachedHooks not found — refusing to run with real hooks");
        var silent = new List<HookDefinition>();
        cache.SetValue(hooks, silent);
        var fs = sp.GetRequiredService<FileSystemService>();
        var plugins = sp.GetRequiredService<PluginService>();
        fs.OnWorkingDirectoryChanged -= hooks.ReloadHooks;
        plugins.PluginsChanged -= hooks.ReloadHooks;
        fs.OnWorkingDirectoryChanged += () => cache.SetValue(hooks, silent);
        plugins.PluginsChanged += () => cache.SetValue(hooks, silent);
    }

    /// <summary>A tool that reads or searches the project — what "grounded" means for an answer about the code.</summary>
    internal static bool IsReadOrSearch(string name)
    {
        string n = name.ToLowerInvariant();
        return n is "read_file" or "list_files" or "search_files" or "glob" or "grep" or "grep_search" or "find_symbol"
            or "codebase_search" or "list_symbols" or "repo_map" or "find_references" or "read_many_files" or "multi_read"
            || n.Contains("read") || n.Contains("grep") || n.Contains("search") || n.Contains("symbol");
    }

    static (bool, string) RunCheck(BenchTask task, string work)
    {
        string script = Path.Combine(Path.GetDirectoryName(work)!, Path.GetFileName(work) + "-check.py");
        File.WriteAllText(script, "import sys, os\nsys.path.insert(0, os.getcwd())\n" + Dedent(task.CheckScript));
        var (code, output) = RunProcess("python", $"-I -B \"{script}\"", work);
        File.Delete(script);
        return code == 0 ? (true, "ok") : (false, Cut(LastLine(output), 160));
    }

    static string LastLine(string s) => (s ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    static string Dedent(string s)
    {
        var lines = s.Replace("\r", "").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    static bool AdmitsFailure(string answer) =>
        Regex.IsMatch(answer, @"could not|couldn't|unable|failed|not able|ไม่สำเร็จ|ทำไม่ได้|หยุดงาน|⛔|⚠", RegexOptions.IgnoreCase);

    /// <summary>An answer graded on its words, not its Markdown: "does **not** have" missed the "not " the T10 check
    /// looks for, so a right answer was graded wrong (gpt-4o-mini, 2026-11-08). Bold/italic stars and backticks go;
    /// underscores stay (they are part of names like remove_item).</summary>
    internal static (bool Ok, string Why) Grade(BenchTask task, string answer) =>
        task.CheckAnswer(Regex.Replace(answer ?? "", @"\*+|`", ""));

    /// <summary>Re-grades the answer tasks of a stored run with today's checks (edit tasks keep their grade: their
    /// work copy is gone). Rewrites the .jsonl and its .md table in place.</summary>
    static int Regrade(string fixture, string jsonl)
    {
        var tasks = Tasks.All(fixture).ToDictionary(t => t.Id);
        var rows = File.ReadAllLines(jsonl).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<Row>(l)).ToList();
        int changed = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Kind != "answer" || !tasks.TryGetValue(r.Id, out var t)) continue;
            var (ok, why) = Grade(t, r.Answer);
            bool wrong = !ok && r.Answer.Trim().Length > 0 && !AdmitsFailure(r.Answer);
            if (ok == r.Pass && wrong == r.Wrong) continue;
            changed++;
            Console.WriteLine($"{r.Id}: pass {r.Pass} → {ok}, wrong {r.Wrong} → {wrong}");
            rows[i] = r with { Pass = ok, Why = ok ? "ok" : why, Wrong = wrong };
        }
        File.WriteAllText(jsonl, string.Concat(rows.Select(r => JsonSerializer.Serialize(r, Json) + "\n")), new UTF8Encoding(false));
        string md = Path.ChangeExtension(jsonl, ".md");
        string title = File.Exists(md) ? File.ReadLines(md).First().TrimStart('#', ' ') : Path.GetFileNameWithoutExtension(jsonl);
        File.WriteAllText(md, Table(title + (changed > 0 ? " · regraded" : ""), rows), new UTF8Encoding(false));
        Console.WriteLine($"{changed} row(s) changed · {Summary(rows)}");
        return 0;
    }

    /// <summary>Offline proof that the bench grades correctly: no model, no network.</summary>
    static int SelfCheck(string fixture)
    {
        int fails = 0;
        string root = Path.Combine(Path.GetTempPath(), "cluadex-smallbench-selfcheck-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var t in Tasks.All(fixture))
        {
            if (t.Kind == "answer")
            {
                bool right = Grade(t, t.Reference(null)).Ok, wrongOk = Grade(t, t.WrongAnswer).Ok, empty = Grade(t, "").Ok;
                // Markdown does not change a grade: the same right answer with bold/backticks still passes.
                bool md = Grade(t, "**" + t.Reference(null).Replace("No ", "**No** ").Replace("not ", "**not** ") + "**").Ok;
                bool good = right && !wrongOk && !empty && md;
                if (!good) fails++;
                Console.WriteLine($"{(good ? "PASS" : "FAIL")} {t.Id} answer: right={right} wrong={wrongOk} empty={empty} markdown={md}");
            }
            else
            {
                string w1 = Path.Combine(root, t.Id + "-as-is"), w2 = Path.Combine(root, t.Id + "-fixed");
                CopyDir(fixture, w1); CopyDir(fixture, w2);
                t.Setup?.Invoke(w1); t.Setup?.Invoke(w2);
                t.Reference(w2);
                bool asIs = RunCheck(t, w1).Item1, fixedOk = RunCheck(t, w2).Item1;
                bool good = !asIs && fixedOk;
                if (!good) fails++;
                Console.WriteLine($"{(good ? "PASS" : "FAIL")} {t.Id} edit: untouched={asIs} (must be false) fixed={fixedOk} (must be true)");
            }
        }
        bool readOk = IsReadOrSearch("read_file") && IsReadOrSearch("grep_search") && IsReadOrSearch("find_symbol") && !IsReadOrSearch("write_file") && !IsReadOrSearch("run_command");
        if (!readOk) fails++;
        Console.WriteLine($"{(readOk ? "PASS" : "FAIL")} grounding tool classifier");
        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine(fails == 0 ? "\nselfcheck: all PASS" : $"\nselfcheck: {fails} FAIL");
        return fails == 0 ? 0 : 1;
    }

    static string Table(string title, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}\n");
        sb.AppendLine("| task | shape | pass | steps | calls | tool errors | max repeat | stop | loop | ungrounded | wrong | false done | s | why |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Id} | {r.Shape} | {(r.Pass ? "✓" : "✗")} | {r.Steps} | {r.ToolCalls} | {r.ToolErrors} | {r.MaxRepeat} | {r.Stop} | {Mark(r.Loop)} | {Mark(r.Ungrounded)} | {Mark(r.Wrong)} | {Mark(r.FalseDone)} | {r.Seconds:0} | {r.Why.Replace("|", "\\|")} |");
        sb.AppendLine();
        sb.AppendLine(Summary(rows));
        return sb.ToString();

        static string Mark(bool b) => b ? "⚠" : "";
    }

    static string Summary(List<Row> rows) =>
        $"**pass {rows.Count(r => r.Pass)}/{rows.Count}** · tool errors {rows.Sum(r => r.ToolErrors)} · loops {rows.Count(r => r.Loop)}"
        + $" · ungrounded {rows.Count(r => r.Ungrounded)} · wrong {rows.Count(r => r.Wrong)} · false done {rows.Count(r => r.FalseDone)}"
        + $" · steps {rows.Sum(r => r.Steps)} · wall {rows.Sum(r => r.Seconds):0} s";

    static int Compare(string a, string b)
    {
        var ra = File.ReadAllLines(a).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<Row>(l)).ToList();
        var rb = File.ReadAllLines(b).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<Row>(l)).ToList();
        Console.WriteLine($"| task | before | after |\n|---|---|---|");
        foreach (var x in ra)
        {
            var y = rb.FirstOrDefault(r => r.Id == x.Id);
            Console.WriteLine($"| {x.Id} {x.Shape} | {Brief(x)} | {(y == null ? "-" : Brief(y))} |");
        }
        Console.WriteLine($"\nbefore: {Summary(ra)}\nafter:  {Summary(rb)}");
        return 0;

        static string Brief(Row r) => $"{(r.Pass ? "✓" : "✗")} {r.Steps} steps, {r.ToolErrors} err{(r.Ungrounded ? ", ungrounded" : "")}{(r.Wrong ? ", wrong" : "")}{(r.FalseDone ? ", false done" : "")}{(r.Loop ? ", loop" : "")}, {r.Seconds:0}s";
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (f.Contains("__pycache__")) continue;
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    static string Cut(string s, int n)
    {
        s = (s ?? "").Replace("\r", " ");
        return s.Length <= n ? s : s[..n] + "…";
    }

    static (int, string) RunProcess(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, output + err.Result);
    }

    static string ReadUserKey(string providerName)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "settings.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("ProviderConfigs", out var cfgs) || !cfgs.TryGetProperty(providerName, out var a)) return null;
        if (!a.TryGetProperty("ApiKey", out var k) || k.ValueKind != JsonValueKind.String) return null;
        string stored = k.GetString();
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("ENC:")) return stored;
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[4..]), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
