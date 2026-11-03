using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace ToolTest;

/// <summary>
/// The engineering tools (Services/Tools/EngineeringToolModule) through the real AgentToolService and the name resolver
/// the native loop uses, in a throwaway git repo of their own: verify_change (a stale binary, a locked exe rebuilt into
/// scratch, the baseline), run_build's locked-output answer (the PID from Restart Manager, nothing killed),
/// find_references / rename_symbol (preview, then apply) / impact_of, the checkpoint round trip, gh missing and gh
/// gated, git_blame / git_show argument checks, and debug with no adapter.
/// </summary>
internal static partial class Program
{
    static async Task EngineeringTools(IServiceProvider provider, SettingsService settings)
    {
        string root = Path.Combine(Path.GetTempPath(), "cluadex-eng-" + Guid.NewGuid().ToString("N")[..8]);
        string store = Path.Combine(Path.GetTempPath(), "cluadex-engcp-" + Guid.NewGuid().ToString("N")[..8]);
        string outside = Path.Combine(Path.GetTempPath(), "cluadex-eng-outside-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        string savedWd = Fs.WorkingDirectory;
        string savedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        Process? locky = null;
        var cp = new CheckpointService(null, store);   // CheckpointService.Current: what tools capture into
        int prompts = 0;
        bool answer = true;
        Func<PermissionRequestInfo, Task<bool>> counter = _ => { prompts++; return Task.FromResult(answer); };
        Svc.OnPermissionRequired += counter;   // last subscriber: its answer is the one the gate gets
        var perms = provider.GetRequiredService<PermissionService>();
        var keptRules = perms.Rules.ToList();   // no rule of an earlier case may answer these prompts
        foreach (var kept in keptRules) perms.RemoveRule(kept);
        try
        {
            void W(string rel, string text)
            {
                string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            }
            void G(string args)
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!; p.WaitForExit(20000);
            }
            Directory.CreateDirectory(root);
            W("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
            W("Lib/Calc.cs", "namespace Lib;\n\npublic class Calc\n{\n    public int Total(int a, int b) => a + b;\n\n    public int Twice(int a) => Total(a, a);\n}\n");
            W("Lib/Use.cs", "namespace Lib;\n\npublic static class Use\n{\n    public static int Run() => new Calc().Total(1, 2);\n}\n");
            W("Lib.Tests/CalcTests.cs", "namespace Lib.Tests;\n\npublic class CalcTests\n{\n    public void Adds() { var c = new Lib.Calc(); }\n}\n");
            W("Locky/Locky.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net8.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
            W("Locky/Program.cs", "Console.WriteLine(\"ready\");\nThread.Sleep(180_000);\n");
            W("app.py", "x = 1\nprint(x)\n");
            W("fake-test.sh", "cat test-results.txt\nexit $(cat test-exit.txt)\n");
            W("test-results.txt", "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 5 ms - Lib.Tests.dll (net8.0)\n");
            W("test-exit.txt", "0\n");
            W(".gitignore", "bin/\nobj/\n");
            G("init -q"); G("config user.email t@t"); G("config user.name t"); G("add -A"); G("commit -q -m init-engineering");
            File.WriteAllText(outside, "outside the project\n");
            Fs.WorkingDirectory = root;
            Console.WriteLine($"  (engineering fixture: {root})");

            // ── names, schemas, read-only flags, the CLI bridge ──
            await Case("EN1", async () =>
            {
                var unresolved = EngineeringToolModule.ToolNames.Where(n => Svc.ResolveToolTypePublic(n) != EngineeringToolModule.TypeOf(n)).ToList();
                var foreign = new (string Name, ToolType Type)[]
                {
                    ("get_pull_request", ToolType.GhPrView), ("get_pull_request_status", ToolType.GhPrChecks), ("get_job_logs", ToolType.GhRunLog),
                    ("find_referencing_symbols", ToolType.FindReferences), ("list_code_usages", ToolType.FindReferences),
                    ("functions.find_usages", ToolType.FindReferences), ("Rewind", ToolType.CheckpointRestore), ("Blame", ToolType.GitBlame),
                    ("VerifyChange", ToolType.VerifyChange), ("get_commit", ToolType.GitShow),
                };
                var wrong = foreign.Where(f => Svc.ResolveToolTypePublic(f.Name) != f.Type).Select(f => f.Name).ToList();
                Check("EN1", "every engineering tool and the other agents' names for them resolve", unresolved.Count == 0 && wrong.Count == 0,
                    "unresolved: " + string.Join(", ", unresolved) + " | wrong: " + string.Join(", ", wrong));
                await Task.CompletedTask;
            });
            await Case("EN2", async () =>
            {
                var offered = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToHashSet();
                var missing = new[] { "verify_change", "find_references", "rename_symbol", "impact_of", "checkpoint_list", "checkpoint_diff",
                    "checkpoint_restore", "git_blame", "git_show", "debug" }.Where(n => !offered.Contains(n)).ToList();
                Check("EN2", "the native catalogue offers them", missing.Count == 0, "missing: " + string.Join(", ", missing));
                var ro = new[] { ToolType.FindReferences, ToolType.ImpactOf, ToolType.CheckpointList, ToolType.CheckpointDiff, ToolType.GitBlame,
                    ToolType.GitShow, ToolType.GhPrView, ToolType.GhPrChecks, ToolType.GhRunLog };
                var rw = new[] { ToolType.VerifyChange, ToolType.RenameSymbol, ToolType.CheckpointRestore, ToolType.Debug };
                Check("EN3", "read-only tools are flagged read-only and concurrency-safe; the others are not",
                    ro.All(t => new ToolCall { Type = t }.IsReadOnly && new ToolCall { Type = t }.IsConcurrencySafe)
                    && rw.All(t => !new ToolCall { Type = t }.IsReadOnly));
                await Task.CompletedTask;
            });
            await Case("EN4", async () =>
            {
                string json = JsonSerializer.Serialize(ChatToolBridge.ToolList());
                using var doc = JsonDocument.Parse(json);
                var byName = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e);
                bool Hint(string n) => byName.TryGetValue(n, out var e) && e.TryGetProperty("annotations", out var a) && a.GetProperty("readOnlyHint").GetBoolean();
                bool all = EngineeringToolModule.ToolNames.All(byName.ContainsKey)
                           && EngineeringToolModule.ToolNames.All(n => ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__" + n));
                Check("EN4", "the CLI agents get every one (mcp__cluadex__…), readOnlyHint on the reads only",
                    all && Hint("find_references") && Hint("gh_run_log") && Hint("git_blame") && !Hint("rename_symbol") && !Hint("verify_change")
                    && !Hint("checkpoint_restore"), json.Length > 600 ? json[..600] : json);
                await Task.CompletedTask;
            });
            await Case("EN5", async () =>
            {
                var module = Svc.ToolModules.OfType<EngineeringToolModule>().First();
                string legacy = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "verify_change" }, NativeTools = false }) ?? "";
                var calls = Svc.ParseToolCalls("[ACTION: find_references]\nsymbol: Total\n[/ACTION]");
                Check("EN5", "the [ACTION:] protocol: the prompt shows the calls, and one parses to the tool with its argument",
                    legacy.Contains("[ACTION: verify_change]") && legacy.Contains("[ACTION: checkpoint_restore]")
                    && calls.Count == 1 && calls[0].Type == ToolType.FindReferences && calls[0].GetArg("symbol") == "Total",
                    $"calls={calls.Count} {(calls.Count > 0 ? calls[0].Type + " " + calls[0].GetArg("symbol") : "")}");
                await Task.CompletedTask;
            });
            bool debugpyHere = DebugAdapterService.DetectAdapters().Any(a => a.Id == "debugpy" && a.IsAvailable);
            await Case("PE1", async () =>
            {
                (PermAction, string, string) E(string tool, params (string k, string v)[] a) =>
                    Svc.EvaluatePermission(new ToolCall { Type = Svc.ResolveToolTypePublic(tool)!.Value, ToolName = tool, Arguments = a.ToDictionary(x => x.k, x => x.v) });
                bool ok = E("find_references", ("symbol", "Total")).Item1 == PermAction.Allow
                    && E("rename_symbol", ("old_name", "Total"), ("new_name", "Sum")).Item1 == PermAction.Allow
                    && E("rename_symbol", ("old_name", "Total"), ("new_name", "Sum"), ("apply", "true")).Item1 == PermAction.Ask
                    && E("checkpoint_list").Item1 == PermAction.Allow
                    && E("checkpoint_restore", ("id", "x")).Item1 == PermAction.Ask
                    && E("verify_change").Item1 == PermAction.Ask
                    && E("git_blame", ("path", "Lib/Calc.cs")).Item1 == PermAction.Allow
                    && E("debug", ("action", "adapters")).Item1 == PermAction.Allow
                    && E("debug", ("action", "start"), ("path", "app.py")).Item1 == (debugpyHere ? PermAction.Ask : PermAction.Allow);
                Check("PE1", "reads run free; rename apply, restore, verify and debug start ask (a start with no adapter runs nothing, so it does not)", ok);
                await Task.CompletedTask;
            });

            // ── run_build: a locked exe → PID named, nothing killed, scratch rebuild ──
            const string lockyBuild = "dotnet build Locky/Locky.csproj -nodeReuse:false";
            string lockyExe = Path.Combine(root, "Locky", "bin", "Debug", "net8.0", "Locky.exe");
            // Restore first, outside the timed tool calls: a first-ever restore on a machine busy with other builds
            // can pass the harness's 95 s hang limit, and that is NuGet's time, not the tool's.
            foreach (var proj in new[] { "Locky/Locky.csproj", "Lib/Lib.csproj" })
            {
                using var restore = Process.Start(new ProcessStartInfo("dotnet", $"restore {proj} -nodeReuse:false")
                    { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                _ = restore.StandardOutput.ReadToEndAsync(); _ = restore.StandardError.ReadToEndAsync();
                if (!restore.WaitForExit(300_000)) try { restore.Kill(true); } catch { }
            }
            await Case("LK1", async () =>
            {
                var r = await T("run_build", ("command", lockyBuild));
                Check("LK1", $"builds the small exe ({LastMs} ms)", r.Success && File.Exists(lockyExe), Seen(r));
            });
            if (File.Exists(lockyExe))
            {
                locky = Process.Start(new ProcessStartInfo(lockyExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                Thread.Sleep(800);
                W("Locky/Program.cs", "Console.WriteLine(\"ready, changed\");\nThread.Sleep(180_000);\n");
                await Case("LK2", async () =>
                {
                    var r = await T("run_build", ("command", lockyBuild));
                    string s = Seen(r);
                    Check("LK2", $"a locked exe: MSB3027/MSB3021 named, the holder's PID from Restart Manager, nothing killed, scratch offered ({LastMs} ms)",
                        !r.Success && (s.Contains("MSB3027") || s.Contains("MSB3021")) && s.Contains($"PID {locky!.Id}")
                        && s.Contains("Nothing was killed") && s.Contains("scratch=\"true\"") && !locky.HasExited, s);
                });
                await Case("LK3", async () =>
                {
                    var r = await T("run_build", ("command", lockyBuild), ("scratch", "true"));
                    string scratch = CluadeX.Helpers.LockedFiles.ScratchOutDir(root);
                    Check("LK3", "scratch=\"true\" builds into the scratch folder outside the project while the exe runs",
                        r.Success && File.Exists(Path.Combine(scratch, "Locky.dll")) && Seen(r).Contains("scratch") && !locky!.HasExited, Seen(r));
                });
                await Case("VC1", async () =>
                {
                    var r = await T("verify_change", ("command", lockyBuild), ("tests", "skip"));
                    string s = Seen(r);
                    Check("VC1", "verify_change on a locked exe: rebuilds into scratch, kills nothing, and calls the normal output STALE",
                        !r.Success && s.Contains("VERDICT: STALE") && s.Contains("LOCKED") && s.Contains("Nothing was killed")
                        && s.Contains("Locky/bin/Debug/net8.0/Locky") && s.Contains("NOT VERIFIED:") && !locky!.HasExited, s);
                });
                try { locky.Kill(true); locky.WaitForExit(5000); } catch { }
            }
            else Check("LK2", "(skipped: the small exe did not build)", false);

            // ── verify_change: a fresh build, tests, the baseline, a stale named binary, a failed build ──
            const string libBuild = "dotnet build Lib/Lib.csproj -nodeReuse:false";
            await Case("VC2", async () =>
            {
                var r = await T("verify_change", ("command", libBuild), ("test_command", "bash fake-test.sh"));
                string s = Seen(r);
                Check("VC2", $"a good change: VERIFIED, the binary fresh, 3 passed, a baseline started, the gaps listed ({LastMs} ms)",
                    r.Success && s.StartsWith("VERDICT: VERIFIED") && s.Contains("Lib.dll") && s.Contains("fresh")
                    && s.Contains("3 passed, 0 failed") && s.Contains("none yet") && s.Contains("NOT VERIFIED:") && s.Contains("Nothing here ran the app"), s);
            });
            await Case("VC3", async () =>
            {
                File.WriteAllText(Path.Combine(root, "test-results.txt"),
                    "  Failed Lib.Tests.CalcTests.Adds [3 ms]\n  Error Message: expected 3\nFailed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3\n");
                File.WriteAllText(Path.Combine(root, "test-exit.txt"), "1\n");
                var r = await T("verify_change", ("command", libBuild), ("test_command", "bash fake-test.sh"));
                string s = Seen(r);
                Check("VC3", "a regression: FAILED, and the baseline names the NEW failure and the count change",
                    !r.Success && s.Contains("VERDICT: FAILED") && s.Contains("NEW failures: Lib.Tests.CalcTests.Adds") && s.Contains("passed 3 → 2"), s);
                File.WriteAllText(Path.Combine(root, "test-results.txt"), "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3\n");
                File.WriteAllText(Path.Combine(root, "test-exit.txt"), "0\n");
            });
            await Case("VC4", async () =>
            {
                // Locky's source changed after its normal output was written (the scratch build above did not replace it).
                var r = await T("verify_change", ("command", libBuild), ("tests", "skip"), ("binary", "Locky/bin/Debug/net8.0/Locky.dll"));
                string s = Seen(r);
                Check("VC4", "a stale DLL named in 'binary' (what a harness links) is caught: STALE, with the newer source named",
                    !r.Success && s.Contains("VERDICT: STALE") && s.Contains("Locky/Program.cs") && s.Contains("[named]: STALE"), s);
            });
            await Case("VC5", async () =>
            {
                var r = await T("verify_change", ("command", "dotnet build Nope/Nope.csproj -nodeReuse:false"));
                string s = Seen(r);
                Check("VC5", "a failed build: FAILED, and it says nothing after the build was verified",
                    !r.Success && s.Contains("VERDICT: FAILED") && s.Contains("NOT VERIFIED: everything after the build"), s);
            });

            // ── find_references / rename_symbol / impact_of ──
            await Case("FR1", async () =>
            {
                var r = await T("find_references", ("symbol", "Total"));
                string s = Seen(r);
                var cs = await T("find_references", ("symbol", "Total"), ("include", "*.cs"));
                // Only the declaration is marked — not `Twice(int a) => Total(a, a)` nor a line declaring Run().
                Check("FR1", "find_references lists every use by file and line, only the declaration marked [definition]",
                    r.Success && s.Contains("Lib/Calc.cs") && s.Contains("Lib/Use.cs")
                    && System.Text.RegularExpressions.Regex.Matches(s, @"\[definition\]").Count == 1 && s.Contains("[definition]  public int Total(int a")
                    && cs.Success && Seen(cs).StartsWith("3 reference"), s + " | " + Seen(cs));
            });
            await Case("FR2", async () =>
            {
                var r = await T("find_usages", ("name", "Total"));
                var bad = await T("find_references", ("symbol", "Total; rm -rf"));
                Check("FR2", "another agent's name and argument (find_usages, name) work; a non-identifier is refused",
                    r.Success && Seen(r).Contains("Lib/Use.cs") && !bad.Success && Seen(bad).Contains("identifier"), Seen(r) + " | " + Seen(bad));
            });
            var scope = new AgentRunScope("chat-eng", root);
            AgentRunScope.Enter(scope);
            scope.Step = 1;
            await Case("RN1", async () =>
            {
                prompts = 0;
                string before = File.ReadAllText(Path.Combine(root, "Lib", "Calc.cs"));
                var r = await T("rename_symbol", ("old_name", "Total"), ("new_name", "Sum"));
                Check("RN1", "rename_symbol previews by default: files untouched, every line listed, the apply call spelled out, nobody asked",
                    r.Success && Seen(r).Contains("PREVIEW") && Seen(r).Contains("expected_edits=\"3\"") && prompts == 0
                    && Seen(r).Contains("Left alone — not source code: test-results.txt (1)")
                    && File.ReadAllText(Path.Combine(root, "Lib", "Calc.cs")) == before, Seen(r));
            });
            await Case("RN2", async () =>
            {
                var noCount = await T("rename_symbol", ("old_name", "Total"), ("new_name", "Sum"), ("apply", "true"));
                var wrong = await T("rename_symbol", ("old_name", "Total"), ("new_name", "Sum"), ("apply", "true"), ("expected_edits", "2"));
                Check("RN2", "apply without the preview's count, or with another count, renames nothing",
                    !noCount.Success && !wrong.Success && Seen(wrong).Contains("changed since the preview")
                    && File.ReadAllText(Path.Combine(root, "Lib", "Calc.cs")).Contains("Total"), Seen(noCount) + " | " + Seen(wrong));
            });
            await Case("RN3", async () =>
            {
                prompts = 0;
                var r = await T("rename_symbol", ("old_name", "Total"), ("new_name", "Sum"), ("apply", "true"), ("expected_edits", "3"));
                string calc = File.ReadAllText(Path.Combine(root, "Lib", "Calc.cs")), use = File.ReadAllText(Path.Combine(root, "Lib", "Use.cs"));
                Check("RN3", "apply with the preview's count asks once, renames all 3, and keeps a restore point",
                    r.Success && prompts == 1 && calc.Contains("Sum(int a") && calc.Contains("Sum(a, a)") && use.Contains(".Sum(1, 2)")
                    && !calc.Contains("Total") && cp.PointsForSession(root, "chat-eng").Any(p => p.Files.Any(f => f.File.EndsWith("Calc.cs"))), Seen(r));
            });
            await Case("IM1", async () =>
            {
                var r = await T("impact_of", ("symbol", "Calc"));
                var byPath = await T("impact_of", ("path", "Lib/Calc.cs"));
                string s = Seen(r);
                Check("IM1", "impact_of names the files that use it, the test file, and the project to rebuild",
                    r.Success && s.Contains("Lib/Use.cs") && s.Contains("Lib.Tests/CalcTests.cs (1) [test]") && s.Contains("Lib/Lib.csproj")
                    && byPath.Success && Seen(byPath).Contains("declares Calc"), s + " | " + Seen(byPath));
            });

            // ── checkpoint round trip ──
            scope.Step = 2;
            await T("read_file", ("path", "Lib/Use.cs"));
            await T("edit_file", ("path", "Lib/Use.cs"), ("find", "Sum(1, 2)"), ("replace", "Sum(5, 6)"));
            await T("write_file", ("path", "notes.txt"), ("content", "made in step 2\n"));
            string? step2 = cp.PointsForSession(root, "chat-eng").FirstOrDefault(p => p.Step == 2)?.Id;
            await Case("CPT1", async () =>
            {
                var r = await T("checkpoint_list");
                Check("CPT1", "checkpoint_list shows this chat's points, newest first, with their files",
                    r.Success && step2 != null && Seen(r).Contains(step2!) && Seen(r).Contains("Lib/Use.cs") && Seen(r).Contains("notes.txt (new)"), Seen(r));
            });
            await Case("CPT2", async () =>
            {
                var r = await T("checkpoint_diff", ("id", step2 ?? "?"));
                string s = Seen(r);
                Check("CPT2", "checkpoint_diff shows what a restore would change and which new file it would delete",
                    r.Success && s.Contains("-      public static int Run() => new Calc().Sum(5, 6);") && s.Contains("+      public static int Run() => new Calc().Sum(1, 2);")
                    && s.Contains("notes.txt: would be DELETED"), s);
            });
            string? undoId = null;
            await Case("CPT3", async () =>
            {
                prompts = 0; answer = false;
                var no = await T("checkpoint_restore", ("id", step2 ?? "?"));
                bool untouched = File.Exists(Path.Combine(root, "notes.txt"));
                answer = true;
                var yes = await T("checkpoint_restore", ("id", step2 ?? "?"));
                var m = System.Text.RegularExpressions.Regex.Match(Seen(yes), "checkpoint_restore id=\"(restore-\\d+)\"");
                undoId = m.Success ? m.Groups[1].Value : null;
                Check("CPT3", "checkpoint_restore asks every time: No changes nothing; Yes restores the file and removes the new one",
                    !no.Success && untouched && prompts == 2 && yes.Success
                    && File.ReadAllText(Path.Combine(root, "Lib", "Use.cs")).Contains("Sum(1, 2)") && !File.Exists(Path.Combine(root, "notes.txt"))
                    && undoId != null, Seen(no) + " | " + Seen(yes));
            });
            await Case("CPT4", async () =>
            {
                var r = await T("checkpoint_restore", ("id", undoId ?? "?"));
                Check("CPT4", "the id it returned undoes the restore", r.Success
                    && File.ReadAllText(Path.Combine(root, "Lib", "Use.cs")).Contains("Sum(5, 6)") && File.Exists(Path.Combine(root, "notes.txt")), Seen(r));
            });
            await Case("CPT5", async () =>
            {
                // A forged index line (the index is a file in the profile) must not steer a write outside the project.
                string index = Path.Combine(store, CheckpointService.Slug(root), "index.jsonl");
                var forged = new CheckpointService.Entry("forged-001", DateTime.UtcNow.AddMinutes(5), "chat-eng", 9, null, root, outside, false, null, 0, null);
                File.AppendAllText(index, JsonSerializer.Serialize(forged) + "\n");
                var r = await T("checkpoint_restore", ("id", "forged-001"));
                var unknown = await T("checkpoint_restore", ("id", "no-such-point"));
                Check("CPT5", "a point that covers a file outside the project restores nothing; an unknown id is a clear error",
                    !r.Success && Seen(r).Contains("outside this project") && File.Exists(outside)
                    && !unknown.Success && Seen(unknown).Contains("checkpoint_list"), Seen(r) + " | " + Seen(unknown));
            });
            AgentRunScope.Enter(null);

            // ── git_blame / git_show ──
            await Case("GB1", async () =>
            {
                var r = await T("git_blame", ("path", "Lib/Calc.cs"), ("start_line", "1"), ("end_line", "3"));
                var esc = await T("git_blame", ("path", "../" + Path.GetFileName(outside)));
                Check("GB1", "git_blame reads the lines' commits; a path outside the project is refused",
                    r.Success && Seen(r).Contains("namespace Lib") && !esc.Success && Seen(esc).Contains("outside the project"), Seen(r) + " | " + Seen(esc));
            });
            await Case("GS1", async () =>
            {
                var commit = await T("git_show", ("rev", "HEAD"), ("stat_only", "true"));
                var file = await T("git_show", ("rev", "HEAD"), ("path", "Lib/Calc.cs"));
                Check("GS1", "git_show: the commit's message and files; with path, the file as committed (before the rename)",
                    commit.Success && Seen(commit).Contains("init-engineering") && file.Success && Seen(file).Contains("Total(int a"), Seen(commit) + " | " + Seen(file));
            });
            await Case("GS2", async () =>
            {
                string marker = Path.Combine(root, "pwned.txt");
                var opt = await T("git_show", ("rev", "--output=" + marker));
                var shell = await T("git_show", ("rev", "HEAD; echo pwned > pwned.txt"));
                var blameRev = await T("git_blame", ("path", "Lib/Calc.cs"), ("rev", "-L1,2"));
                Check("GS2", "a rev that is an option or carries shell text is refused before git runs",
                    !opt.Success && !shell.Success && !blameRev.Success && !File.Exists(marker), Seen(opt) + " | " + Seen(shell) + " | " + Seen(blameRev));
            });

            // ── gh: gated, missing, injection ──
            var act = provider.GetRequiredService<ActivationService>();
            bool wasActive = act.IsActivated;
            await Case("GH0", async () =>
            {
                SetActivatedTo(act, false);
                var r = await T("gh_pr_checks");
                Check("GH0", "without the GitHub feature unlocked, gh_pr_checks says which switch and license it needs",
                    !r.Success && Seen(r).Contains("gh_pr_checks") && Seen(r).Contains("Features"), Seen(r));
            });
            SetActivatedTo(act, true);
            try
            {
                await Case("GH1", async () =>
                {
                    Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
                    ToolResult r;
                    try { r = await T("gh_pr_checks", ("pr", "1")); }
                    finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
                    Check("GH1", $"gh missing: a clear answer with the install command, at once ({LastMs} ms)",
                        !r.Success && Seen(r).Contains("not installed") && Seen(r).Contains("winget") && LastMs < 5000, Seen(r));
                });
                await Case("GH2", async () =>
                {
                    var opt = await T("gh_pr_view", ("pr", "--web"));
                    var repo = await T("gh_pr_checks", ("repo", "a/b --jq x"));
                    var run = await T("gh_run_log", ("run_id", "12; whoami"));
                    Check("GH2", "a pr / repo / run_id that is an option or carries extra text is refused before gh runs",
                        !opt.Success && Seen(opt).Contains("pr must be") && !repo.Success && Seen(repo).Contains("owner/name")
                        && !run.Success && Seen(run).Contains("run_id must be"), Seen(opt) + " | " + Seen(repo) + " | " + Seen(run));
                });
                await Case("GH3", async () =>
                {
                    // gh is installed here; the fixture has no GitHub remote, so gh usually answers locally in seconds. On a
                    // machine pinned at 100% CPU it may not — then the tool's own 30 s stop must answer instead, plus the
                    // time to kill the process tree. Either way: a clear failure, never the harness's 95 s hang.
                    var r = await T("gh_pr_view");
                    Check("GH3", $"gh with no GitHub remote (or signed out): a clear failure in time ({LastMs} ms)",
                        !r.Success && LastMs < 60000 && Seen(r).Length > 0 && !Seen(r).Contains("HARNESS"), Seen(r));
                });
            }
            finally { SetActivatedTo(act, wasActive); }

            // ── debug with no adapter ──
            await Case("DB1", async () =>
            {
                var adapters = await T("debug", ("action", "adapters"));
                var cs = await T("debug", ("action", "start"), ("path", "Lib/Calc.cs"));
                bool netcoredbgHere = CluadeX.Services.DebugAdapterService.DetectAdapters().Any(a => a.Id == "netcoredbg" && a.IsAvailable);
                Check("DB1", "debug: the adapters listed; C# with no netcoredbg → its install hint, nothing started",
                    adapters.Success && Seen(adapters).Contains("netcoredbg") && Seen(adapters).Contains("debugpy")
                    && (netcoredbgHere || (!cs.Success && Seen(cs).Contains("netcoredbg") && Seen(cs).Contains("nothing was started"))),
                    Seen(adapters) + " | " + Seen(cs));
            });
            await Case("DB2", async () =>
            {
                Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
                ToolResult r;
                try { r = await T("debug", ("action", "start"), ("path", "app.py"), ("breakpoints", "2")); }
                finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
                var stack = await T("debug", ("action", "stack"));
                var esc = await T("debug", ("action", "start"), ("path", "../" + Path.GetFileName(outside)));
                Check("DB2", "Python with no debugpy → the pip install hint; stack with no session and a path outside the project are clear errors",
                    !r.Success && Seen(r).Contains("pip install debugpy") && !stack.Success && Seen(stack).Contains("No debug session")
                    && !esc.Success && Seen(esc).Contains("outside the project"), Seen(r) + " | " + Seen(stack) + " | " + Seen(esc));
            });
        }
        finally
        {
            Svc.OnPermissionRequired -= counter;
            foreach (var kept in keptRules) perms.AddRule(kept);
            AgentRunScope.Enter(null);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            try { if (locky is { HasExited: false }) locky.Kill(true); } catch { }
            locky?.Dispose();
            Fs.WorkingDirectory = savedWd;
            ForceDelete(root);
            ForceDelete(store);
            ForceDelete(CluadeX.Helpers.LockedFiles.ScratchOutDir(root));
            try { File.Delete(outside); } catch { }
            try { File.Delete(Path.Combine(settings.DataRoot, "verify-baselines", CluadeX.Services.MemoryService.ProjectSlug(root) + ".json")); } catch { }
        }
    }

    static void SetActivatedTo(ActivationService a, bool on)
    {
        var t = typeof(ActivationService);
        t.GetField("_hasKey", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, on);
        t.GetField("_tier", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, on ? "pro" : "free");
        t.GetField("_expiresUtc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, null);
        t.GetField("_graceExpired", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, false);
        if (a.IsActivated != on) throw new Exception("activation toggle did not take");
    }
}
