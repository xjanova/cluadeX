using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EditorTest;

/// <summary>
/// Contract harness for the editor / IDE / hooks / security-shield fixes, run against the BUILT
/// CluadeX.dll (not mocks). Each check reproduces a defect the way it happened to a user and
/// asserts it no longer does:
///   H*  hook contract (env vars + payload file), the bundled Secret Scan, matcher alternation,
///       quote-aware substitution; exit 2 = block that stderr can't undo; cmd /s quoting and %VAR% /
///       PowerShell-code injection; over-long lines and an unwritable temp folder block a PreToolUse
///       call instead of skipping the hook; shell-safety hooks cover task_create / powershell
///   W*  editor save: changed-on-disk detection, atomic write, no false conflicts after a reload
///   L*  large files (open past 2 MB, save, write-time stamp, lazy-sync flush), the typing reveal for a file
///       that was not open, minimap sampling of a million lines, panel resize grips
///   G*  git status parsing (spaces, Thai names, renames, new repo, dotted branches) + HEAD blobs
///   R*  rename applies exactly the previewed occurrences
///   D*  side-by-side diff of a huge file without gigabytes of trace
///   T*  terminal folder switch can't execute code from a folder name
///   S*  security scan: cancellation is reported, junction loops are skipped
///   B*  C# debugging builds the project and finds the output .dll
///   I*  fabricated demo instincts are removed only when untouched
/// Run: build CluadeX, then `dotnet run -p:CluadeXBin=&lt;OutDir&gt;\`.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    static string Scratch = "";

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
        finally
        {
            try { if (Scratch.Length > 0 && Directory.Exists(Scratch)) ForceDelete(Scratch); } catch { }
        }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 600).Replace("\n", "\n            "));
    }

    static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { Check(id, "threw", false, ex.ToString()); }
    }

    /// <summary>WPF elements need an STA thread; after the first await, Run() continues on the thread pool.</summary>
    static Task OnSta(Action body)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { body(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    static string Dir(params string[] parts)
    {
        string p = Path.Combine(new[] { Scratch }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    static (int code, string output) RunProcess(string exe, IEnumerable<string> args, string cwd, string? stdin = null, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
        return (p.ExitCode, o.Result + e.Result);
    }

    static void Git(string cwd, params string[] args)
    {
        var all = new List<string> { "-c", "user.name=editortest", "-c", "user.email=t@example.invalid", "-c", "core.autocrlf=false" };
        all.AddRange(args);
        RunProcess("git", all, cwd);
    }

    static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        // Junctions first, so the recursive delete can't follow one out of the scratch folder.
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d); } catch { }
        Directory.Delete(dir, true);
    }

    /// <summary>The worktree root: tests/editortest/bin/Debug/net8.0-windows → up five.</summary>
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "CluadeX", "Resources", "HooksBundled"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static async Task<int> Run()
    {
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        var provider = services.BuildServiceProvider();

        // SAFETY: refuse to run unless the data root is isolated next to this exe.
        var settings = provider.GetRequiredService<SettingsService>();
        if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing).");
            return 3;
        }

        Scratch = Path.Combine(Path.GetTempPath(), "cluadex-editortest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Scratch);
        var fs = provider.GetRequiredService<FileSystemService>();
        var hooks = provider.GetRequiredService<HookService>();   // NOT HookBundleService: it writes ~/.cluadex
        var loc = provider.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);               // hook messages resolve to text, not raw keys
        var hookDbg = provider.GetRequiredService<DebugLogService>();
        var hookLog = new List<string>();
        hookDbg.OnEntry += e => { if (e.Category == "Hooks") lock (hookLog) hookLog.Add(e.Message ?? ""); };
        int HookLogMark() { lock (hookLog) return hookLog.Count; }
        List<string> HookLogSince(int mark) { lock (hookLog) return hookLog.Skip(mark).ToList(); }
        string repo = RepoRoot();
        string bundled = Path.Combine(repo, "CluadeX", "Resources", "HooksBundled");
        Console.WriteLine($"scratch : {Scratch}\nscripts : {bundled}\n");

        // ═══════════════════════ hooks ═══════════════════════
        Console.WriteLine("── hook contract ──");
        string hookProject = Dir("R&D project");
        fs.WorkingDirectory = hookProject;

        string bigContent = string.Concat(Enumerable.Repeat("สวัสดี line of ordinary code; // ไม่มีความลับ\n", 1500));   // ~60 KB
        var writeCall = new ToolCall
        {
            Type = ToolType.WriteFile, ToolName = "write_file",
            Arguments = new() { ["path"] = "R&D notes/plan 100%.md", ["content"] = bigContent },
        };

        await Case("H1", async () =>
        {
            string dump = Path.Combine(Scratch, "env-dump.json");
            string copy = Path.Combine(Scratch, "payload-copy.json");
            string script = Path.Combine(Scratch, "dump-env.ps1");
            File.WriteAllText(script,
                "$o = [ordered]@{ tool=$env:CLUADEX_HOOK_TOOL; path=$env:CLUADEX_HOOK_PATH; cwd=$env:CLUADEX_HOOK_CWD; payload=$env:CLUADEX_HOOK_PAYLOAD_FILE; phase=$env:CLUADEX_HOOK_PHASE }\r\n" +
                $"($o | ConvertTo-Json) | Out-File -Encoding utf8 -LiteralPath '{dump}'\r\n" +
                $"Copy-Item -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE -Destination '{copy}'\r\n",
                new UTF8Encoding(true));
            var hook = new HookDefinition { Phase = "PreToolUse", Matcher = "*", TimeoutMs = 30000,
                Command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"" };
            var r = await hooks.RunHookAsync(hook, new HookSessionContext { ToolCall = writeCall }, CancellationToken.None);
            var env = JsonDocument.Parse(File.ReadAllText(dump).TrimStart('\uFEFF')).RootElement;
            string payloadPath = env.GetProperty("payload").GetString() ?? "";
            var payload = JsonDocument.Parse(File.ReadAllText(copy, Encoding.UTF8)).RootElement;
            Check("H1", "every hook gets CLUADEX_HOOK_TOOL / PATH / CWD / PHASE exactly (path with & and %)",
                r.Success && env.GetProperty("tool").GetString() == "write_file"
                && env.GetProperty("path").GetString() == "R&D notes/plan 100%.md"
                && env.GetProperty("cwd").GetString() == hookProject
                && env.GetProperty("phase").GetString() == "PreToolUse",
                File.ReadAllText(dump));
            Check("H1b", "…and CLUADEX_HOOK_PAYLOAD_FILE holds the FULL 60 KB arguments as UTF-8 JSON",
                payload.GetProperty("content").GetString() == bigContent && payload.GetProperty("path").GetString() == "R&D notes/plan 100%.md",
                $"payload {new FileInfo(copy).Length} bytes");
            Check("H1c", "…which is deleted once the hook exits", payloadPath.Length > 0 && !File.Exists(payloadPath), payloadPath);
        });

        var secretBundle = new HookBundle { Id = "pretool-secret-scan", ScriptFile = "pretool-secret-scan.ps1",
            SubstitutionTokens = new() { "{path}", "{arguments}" } };
        string secretCmd = HookBundleService.BuildShellCommand(Path.Combine(bundled, "pretool-secret-scan.ps1"), secretBundle);
        var secretHook = new HookDefinition { Phase = "PreToolUse", Matcher = HookBundleService.FileWriteTools, Command = secretCmd, TimeoutMs = 30000 };
        Task<HookResult> Scan(ToolCall c) => hooks.RunHookAsync(secretHook, new HookSessionContext { ToolCall = c }, CancellationToken.None);

        await Case("H2", async () =>
        {
            var r = await Scan(writeCall);
            Check("H2", "Secret Scan lets a clean 60 KB write_file through (was: 'command line is too long' → blocked)",
                r.Success && !secretCmd.Contains("{arguments}") && secretCmd.Length < 300, $"{r.ExitCode} {r.Message} · cmd={secretCmd}");
        });
        await Case("H2b", async () =>
        {
            var c = new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = "cfg.py", ["content"] = bigContent + "KEY = 'AKIA" + "ABCDEFGHIJKLMNOP'\n" } };
            var r = await Scan(c);
            Check("H2b", "…and still blocks an AWS key buried at the end of a large file — with the deliberate-block exit code 2",
                !r.Success && r.Message.Contains("AWS Access Key") && r.ExitCode == HookService.BlockExitCode, $"{r.ExitCode} {r.Message}");
        });
        await Case("H2c", async () =>
        {
            var c = new ToolCall { Type = ToolType.EditFile, ToolName = "edit_file",
                Arguments = new() { ["path"] = "app.cs",
                    ["find"] = "password = \"abcdefghijklmnopqrst" + "uvwxyz0123456789ABCD\"",
                    ["replace"] = "password = Environment.GetEnvironmentVariable(\"APP_PW\")" } };
            var r = await Scan(c);
            Check("H2c", "…an edit that REMOVES a hard-coded password is not blocked for the text it removes", r.Success, r.Message);
        });
        await Case("H2d", async () =>
        {
            var c = new ToolCall { Type = ToolType.EditFile, ToolName = "edit_file",
                Arguments = new() { ["path"] = "app.cs", ["find"] = "x", ["replace"] = "token = \"" + new string('Q', 40) + "\"" } };
            var r = await Scan(c);
            Check("H2d", "…an edit that ADDS a quoted secret is blocked (quotes are decoded, not \\\" in JSON)", !r.Success && r.Message.Contains("Generic"), r.Message);
        });
        await Case("H2e", async () =>
        {
            string edits = JsonSerializer.Serialize(new[] { new { find = "a", replace = "gh = \"ghp_" + new string('a', 36) + "\"" } });
            var c = new ToolCall { Type = ToolType.MultiEdit, ToolName = "multi_edit", Arguments = new() { ["path"] = "x.js", ["edits"] = edits } };
            var r = await Scan(c);
            Check("H2e", "…multi_edit's nested edit list is scanned too", !r.Success && r.Message.Contains("GitHub Token"), r.Message);
        });

        await Case("H3", () =>
        {
            Check("H3", "matcher 'write_file|edit_file|multi_edit' matches edit_file and multi_edit, not run_command",
                HookService.MatchesToolName(HookBundleService.FileWriteTools, "edit_file")
                && HookService.MatchesToolName(HookBundleService.FileWriteTools, "multi_edit")
                && !HookService.MatchesToolName(HookBundleService.FileWriteTools, "run_command")
                && HookService.MatchesToolName("git_*", "git_status") && !HookService.MatchesToolName("write_file", "edit_file"));
            return Task.CompletedTask;
        });
        await Case("H4", () =>
        {
            string quoted = HookService.SubstituteToken("tool \"{path}\"", "{path}", @"C:\R&D\a.md");
            string bare = HookService.SubstituteToken("tool {path}", "{path}", @"C:\R&D (x)\a^b.md");
            string hostile = HookService.SubstituteToken("tool \"{path}\"", "{path}", "a\" & calc & \"");
            Check("H4", "{path} inside quotes stays 'R&D' (was 'R^&D'); outside quotes & | < > ( ) ^ are DROPPED, never caret-escaped",
                quoted == "tool \"C:\\R&D\\a.md\"" && bare == "tool C:\\RD x\\ab.md", quoted + " | " + bare);
            Check("H4b", "…and a quoted value cannot close the quote to inject a command", hostile == "tool \"a & calc & \"", hostile);
            return Task.CompletedTask;
        });
        await Case("H4c", async () =>
        {
            // Through the real cmd.exe: the value tries to close the quote and chain a second command.
            var hook = new HookDefinition { Phase = "PreToolUse", Command = "echo \"{path}\"", TimeoutMs = 10000 };
            var call = new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = "a\" & echo INJECTED & \"", ["content"] = "" } };
            var r = await hooks.RunHookAsync(hook, new HookSessionContext { ToolCall = call }, CancellationToken.None);
            var lines = r.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            Check("H4c", "…run through cmd.exe it stays ONE literal argument — the chained command never runs",
                r.Success && lines.Count == 1 && lines[0] == "\"a & echo INJECTED & \"", string.Join(" ⏎ ", lines));
        });
        Task<HookResult> RunHook(string phase, string command, ToolCall call, int timeoutMs = 30000) =>
            hooks.RunHookAsync(new HookDefinition { Phase = phase, Matcher = "*", Command = command, TimeoutMs = timeoutMs },
                new HookSessionContext { ToolCall = call }, CancellationToken.None);
        ToolCall Shell(string tool, string key, string text) => new ToolCall
        {
            Type = tool switch
            {
                "task_create" => ToolType.TaskCreate, "powershell" => ToolType.PowerShell,
                "run_build" => ToolType.RunBuild, "run_tests" => ToolType.RunTests, _ => ToolType.RunCommand,
            },
            ToolName = tool, Arguments = new() { [key] = text },
        };
        ToolCall WriteAt(string path, string content = "") => new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
            Arguments = new() { ["path"] = path, ["content"] = content } };
        // hook.scriptError carries an excerpt of the hook's first stderr line ({0}).
        string scriptErrPrefix = loc.T("hook.scriptError").Split("{0}")[0];
        bool IsScriptError(string m) => m.StartsWith(scriptErrPrefix, StringComparison.Ordinal);
        string Ps1(string name, string body)
        {
            string p = Path.Combine(Scratch, name);
            File.WriteAllText(p, body, new UTF8Encoding(true));
            return p;
        }
        string PsFile(string script, string args = "") =>
            $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"" + (args.Length > 0 ? " " + args : "");
        (int code, string output) RawCmd(string arguments, string cwd, Dictionary<string, string> env = null)
        {
            // The OLD launch form (cmd /c <line>) — used only as a control, to prove a check is meaningful.
            var psi = new ProcessStartInfo("cmd.exe", arguments)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } }
            return (p.ExitCode, o.Result + e.Result);
        }
        var catalog = (List<HookBundle>)typeof(HookBundleService).GetMethod("BuildCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        HookDefinition Bundled(string id)
        {
            var b = catalog.First(x => x.Id == id);
            return new HookDefinition { Phase = b.Phase, Matcher = b.Matcher, TimeoutMs = 30000,
                Command = HookBundleService.BuildShellCommand(Path.Combine(bundled, b.ScriptFile), b) };
        }
        Task<HookResult> RunBundled(string id, ToolCall c) => hooks.RunHookAsync(Bundled(id), new HookSessionContext { ToolCall = c }, CancellationToken.None);

        await Case("H0", () =>
        {
            var injected = typeof(HookService).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hooks);
            Check("H0", "DI hands HookService the DebugLogService (so 'not blocking' results are logged, not dropped)", ReferenceEquals(injected, hookDbg));
            return Task.CompletedTask;
        });

        // ── finding 3: over-long command lines ──
        await Case("H5", async () =>
        {
            string longCmd = "echo " + new string('x', 9000);
            int mark = HookLogMark();
            var pre = await RunHook("PreToolUse", longCmd, writeCall);
            var post = await RunHook("PostToolUse", longCmd, writeCall);
            Check("H5", "a PreToolUse hook whose OWN command is too long (9,000 chars) blocks, with a localized reason (was: let through)",
                !pre.Success && pre.Message == loc.T("hook.blockedTooLong") && !pre.Message.StartsWith("hook."), pre.Message);
            var logged = HookLogSince(mark);
            Check("H5a", "…in PostToolUse it is 'not blocking' — and both outcomes are in the Debug Log, not dropped",
                post.Success && post.Message.Contains("not blocking")
                && logged.Any(m => m.Contains("BLOCKED") && m.Contains("chars (limit"))
                && logged.Any(m => m.Contains("not blocking")), string.Join(" ⏎ ", logged));
            var b = await RunHook("PreToolUse", "exit 1", writeCall);
            Check("H5b", "…while a hook that deliberately exits 1 (clean stderr) still blocks", !b.Success && b.ExitCode == 1, b.Message);
        });
        await Case("H7", async () =>
        {
            string outFile = Path.Combine(Scratch, "h7-seen.json");
            // [{command}] \u2014 powershell.exe drops an empty "" argument, the brackets keep it visible.
            string script = Ps1("h7-dump.ps1",
                "$p = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json\r\n" +
                "$o = [ordered]@{ arg = [string]$args[0]; omitted = $env:CLUADEX_HOOK_OMITTED; payload = ([string]$p.command).Length }\r\n" +
                $"($o | ConvertTo-Json) | Out-File -Encoding utf8 -LiteralPath '{outFile}'\r\nexit 0\r\n");
            string padded = "rm -rf / " + new string(' ', 9000) + "#";
            var call = Shell("run_command", "command", padded);
            var r = await RunHook("PreToolUse", PsFile(script, "\"[{command}]\""), call);
            var seen = JsonDocument.Parse(File.ReadAllText(outFile).TrimStart('\uFEFF')).RootElement;
            Check("H7", "a {command} padded past 8,000 chars no longer skips the hook: it runs with that token empty, told so, payload intact",
                r.Success && seen.GetProperty("arg").GetString() == "[]" && seen.GetProperty("omitted").GetString() == "command"
                && seen.GetProperty("payload").GetInt32() == padded.Length, $"{r.Message} · {File.ReadAllText(outFile)}");
            var block = await RunHook("PreToolUse", "exit 1 {command}", call);
            Check("H7b", "…and a user hook with {command} on its line that does NOT read the payload fails closed on the padded call (was: 'could not run — not blocking')",
                !block.Success && block.Message == loc.T("hook.blockedTooLong"), $"{block.ExitCode} {block.Message}");

            // Round 2: the hook used to run with {command} emptied — a hook that reads ONLY {command} then judged
            // an empty string and allowed the padded `rm -rf /`.
            string only = Ps1("h7-cmd-only.ps1",
                "param([string]$c)\r\nif ($c -match 'rm\\s+-rf') { [Console]::Error.WriteLine('no rm -rf here'); exit 2 }\r\nexit 0\r\n");
            string onlyCmd = PsFile(only, "\"{command}\"");
            var shortR = await RunHook("PreToolUse", onlyCmd, Shell("run_command", "command", "rm -rf /"));
            var padR = await RunHook("PreToolUse", onlyCmd, call);
            Check("H7c", "a {command}-only blocking hook + a padded dangerous command → BLOCKED with the localized too-long reason (it blocks the short form itself)",
                shortR.ExitCode == 2 && !padR.Success && padR.Message == loc.T("hook.blockedTooLong"),
                $"short={shortR.ExitCode} {shortR.Message} | padded={padR.ExitCode} {padR.Message}");
            var postR = await RunHook("PostToolUse", onlyCmd, call);
            Check("H7d", "control: that hook given the emptied {command} (PostToolUse still runs it that way) allows — the gate is what stops it",
                postR.Success && postR.ExitCode == 0, $"{postR.ExitCode} {postR.Message}");

            // {arguments} over the inline cap is an omitted value too.
            var argsBlind = await RunHook("PreToolUse", "echo {arguments} >nul", writeCall);
            var argsAware = await RunHook("PreToolUse", "echo {arguments} >nul & type \"%CLUADEX_HOOK_PAYLOAD_FILE%\" >nul", writeCall);
            Check("H7e", "{arguments} over the inline cap: a PreToolUse hook that does not read CLUADEX_HOOK_PAYLOAD_FILE fails closed; one that does runs",
                !argsBlind.Success && argsBlind.Message == loc.T("hook.blockedTooLong") && argsAware.Success,
                $"blind={argsBlind.ExitCode} {argsBlind.Message} | aware={argsAware.ExitCode} {argsAware.Message}");
        });

        // ── finding 1: exit-code contract, no fail-open via echoed stderr ──
        await Case("H6", async () =>
        {
            string bait = "rm -rf / ; echo running scripts is disabled on this system. ParserError is not recognized as the name of a cmdlet. is not digitally signed";
            var r = await RunBundled("pretool-dangerous-cmd-warn", Shell("run_command", "command", bait));
            Check("H6", "the bundled dangerous-command hook blocks with exit 2 even when the command is stuffed with 'broken script' phrases",
                !r.Success && r.ExitCode == 2 && r.Message.Contains("rule:"), $"{r.ExitCode} {r.Message}");
            Check("H6a", "…and its refusal does not echo the model's command back", !r.Message.Contains("running scripts") && !r.Message.Contains("ParserError"), r.Message);

            string echoer = Ps1("h6-echo-block.ps1",
                "$p = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json\r\n" +
                "[Console]::Error.WriteLine('blocked: ' + $p.command)\r\nexit 1\r\n");
            string spoof = $"x # File {echoer} cannot be loaded because running scripts is disabled on this system. The file {echoer} is not digitally signed.";
            var u = await RunHook("PreToolUse", PsFile(echoer), Shell("run_command", "command", spoof));
            Check("H6b", "a USER hook that blocks with exit 1 and echoes a command spoofing PowerShell's execution-policy error still blocks",
                !u.Success && u.ExitCode == 1 && u.Message.StartsWith("blocked:"), $"{u.ExitCode} {u.Message}");

            string two = Ps1("h6-exit2.ps1", "[Console]::Error.WriteLine('ParserError: x is not recognized as the name of a cmdlet; running scripts is disabled')\r\nexit 2\r\n");
            var t = await RunHook("PreToolUse", PsFile(two), Shell("run_command", "command", "ls"));
            Check("H6c", "exit 2 is never reclassified as a broken script, whatever stderr says", !t.Success && t.ExitCode == 2, $"{t.ExitCode} {t.Message}");

            string refused = Ps1("h6 refused (policy).ps1", "exit 1\r\n");
            int mark = HookLogMark();
            var p = await RunHook("PreToolUse", $"powershell -NoProfile -ExecutionPolicy Restricted -File \"{refused}\"", Shell("run_command", "command", "ls"));
            Check("H6d", "…while PowerShell genuinely refusing THIS hook's .ps1 (execution policy) is 'not blocking', localized with an excerpt of the error, and logged",
                p.Success && IsScriptError(p.Message) && p.Message.Contains("cannot be loaded") && !p.Message.Contains('\n')
                && HookLogSince(mark).Any(m => m.Contains("script error")),
                $"{p.ExitCode} {p.Message} · {string.Join(" ⏎ ", HookLogSince(mark))}");

            // Round 2: a -Command hook that calls its script with & 'C:\…\x.ps1' is refused as "& : File <ps1> cannot be loaded".
            var amp = await RunHook("PreToolUse", $"powershell -NoProfile -ExecutionPolicy Restricted -Command \"& '{refused}' x\"", Shell("run_command", "command", "ls"));
            Check("H6g", "PowerShell refusing THIS hook's .ps1 called from -Command (\"& : File <ps1> cannot be loaded\") is 'not blocking' too",
                amp.Success && IsScriptError(amp.Message), $"{amp.ExitCode} {amp.Message}");
            string talkerPath = Path.Combine(Scratch, "h6-talker.ps1");
            Ps1("h6-talker.ps1", $"[Console]::Error.WriteLine('blocked x : File {talkerPath} cannot be loaded because running scripts is disabled on this system.')\r\nexit 1\r\n");
            var talk = await RunHook("PreToolUse", PsFile(talkerPath), Shell("run_command", "command", "ls"));
            string otherPs1 = Path.Combine(Scratch, "some-other-hook.ps1");
            string other = Ps1("h6-other.ps1", $"[Console]::Error.WriteLine('& : File {otherPs1} cannot be loaded because running scripts is disabled on this system.')\r\nexit 1\r\n");
            var oth = await RunHook("PreToolUse", PsFile(other), Shell("run_command", "command", "ls"));
            Check("H6h", "…but that shape still BLOCKS when the prefix is not one PowerShell prints ('blocked x : File <its ps1>') or the .ps1 is not this hook's",
                !talk.Success && talk.ExitCode == 1 && !oth.Success && oth.ExitCode == 1, $"talker={talk.ExitCode} {talk.Message} | other={oth.ExitCode} {oth.Message}");

            // The curated path-guard plugin refuses with exit 1 and quotes the (model-chosen) full path.
            string guard = Ps1("h6-guard.ps1",
                "[Console]::Error.WriteLine('[path-guard] Blocked: ' + $env:CLUADEX_HOOK_PATH + ' is outside the project folder.')\r\nexit 1\r\n");
            var g = await RunHook("PreToolUse", PsFile(guard), new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = @"C:\Users\x\Startup\ParserError ParseException is not recognized as the name of a cmdlet\'evil' is not recognized as an internal or external command.bat", ["content"] = "" } });
            Check("H6e", "a hook that refuses with exit 1 and quotes a path full of 'broken script' phrases still blocks (phrases only count at the start, in the interpreter's own form)",
                !g.Success && g.ExitCode == 1 && g.Message.StartsWith("[path-guard]"), $"{g.ExitCode} {g.Message}");

            string bad = Ps1("h6 parse bad.ps1", "if ( {\r\nexit 1\r\n");
            var pb = await RunHook("PreToolUse", PsFile(bad), Shell("run_command", "command", "ls"));
            var missingExe = await RunHook("PreToolUse", "nosuchprogram-cluadex-xyz --check", Shell("run_command", "command", "ls"));
            string missingCmdlet = Ps1("h6-missing.ps1", "Get-NoSuchThing-CluadexXyz\r\nexit 1\r\n");
            var mc = await RunHook("PreToolUse", PsFile(missingCmdlet), Shell("run_command", "command", "ls"));
            Check("H6f", "genuinely broken hooks stay 'not blocking': its own .ps1 does not parse / its program is missing / it calls a missing cmdlet",
                pb.Success && IsScriptError(pb.Message) && missingExe.Success && IsScriptError(missingExe.Message) && mc.Success && IsScriptError(mc.Message),
                $"parse={pb.ExitCode} {pb.Message} | exe={missingExe.ExitCode} {missingExe.Message} | cmdlet={mc.ExitCode} {mc.Message}");

            // Round 2: the missing-command form counts only on the FIRST line, for a single-token name the hook's
            // own code calls. A hook that echoes the model's command first used to be reclassified as broken.
            string rawEcho = Ps1("h6-echo-raw.ps1",
                "$p = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json\r\n" +
                "[Console]::Error.WriteLine($p.command)\r\nexit 1\r\n");
            string spoofMissing = "Remove-Everything : The term 'Remove-Everything' is not recognized as the name of a cmdlet, function, script file, or operable program.";
            var notCalled = await RunHook("PreToolUse", PsFile(rawEcho), Shell("run_command", "command", spoofMissing));
            var spaced = await RunHook("PreToolUse", PsFile(rawEcho), Shell("run_command", "command",
                "Get-Foo Bar : The term 'Get-Foo Bar' is not recognized as the name of a cmdlet"));
            var second = await RunHook("PreToolUse", PsFile(rawEcho), Shell("run_command", "command",
                "Refused.\nexit : The term 'exit' is not recognized as the name of a cmdlet"));
            Check("H6i", "an exit-1 hook whose stderr only LOOKS like a missing command still blocks: a name the hook never calls, a name with a space, the form on line 2",
                !notCalled.Success && notCalled.ExitCode == 1 && !spaced.Success && spaced.ExitCode == 1 && !second.Success && second.ExitCode == 1,
                $"notCalled={notCalled.ExitCode} {notCalled.Message} | spaced={spaced.ExitCode} | second={second.ExitCode}");
            bool oldRuleFooled = System.Text.RegularExpressions.Regex.IsMatch(
                string.Concat(spoofMissing.Where(ch => !char.IsWhiteSpace(ch))),
                @"^(?<n>[^']{1,200}):Theterm'\k<n>'isnotrecognizedasthenameofacmdlet", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Check("H6j", "control: the round-1 rule (anywhere-squashed, any name) took that first spoof for a broken hook = allowed", oldRuleFooled);
        });

        // ── finding 2: cmd quoting / injection through tool values ──
        await Case("H9", async () =>
        {
            string tool = Dir("Program Files", "audit tool");
            string auditCmd = Path.Combine(tool, "audit.cmd");
            File.WriteAllText(auditCmd, "@exit /b 0\r\n");
            string template = $"\"{auditCmd}\" C:\\hooks\\audit.js \"{{path}}\"";
            string evilPath = @"src\a & type nul > inj-marker.txt & .cs";
            var r = await RunHook("PreToolUse", template, new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = evilPath, ["content"] = "x" } });
            bool injected = File.Exists(Path.Combine(hookProject, "inj-marker.txt"));
            Check("H9", "a template starting with a quoted exe (\"C:\\Program Files\\…\" … \"{path}\") keeps {path} quoted — the marker command never runs",
                r.Success && !injected, $"injected={injected} {r.ExitCode} {r.Message}");
            string ctl = Dir("h9-control");
            RawCmd("/c " + HookService.SubstituteToken(template, "{path}", evilPath), ctl);
            Check("H9a", "control: the same line through the old `cmd /c` DID run the marker command", File.Exists(Path.Combine(ctl, "inj-marker.txt")));
        });
        await Case("H9b", async () =>
        {
            string evil = "a & type nul > env-marker.txt";
            var r = await RunHook("PreToolUse", "echo %CLUADEX_HOOK_PATH%", new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = evil, ["content"] = "" } });
            Check("H9b", "%CLUADEX_HOOK_PATH% in a hook command is substituted safely, never expanded raw by cmd",
                r.Success && !File.Exists(Path.Combine(hookProject, "env-marker.txt")), $"{r.Message}");
            string ctl = Dir("h9b-control");
            RawCmd("/c echo %HX%", ctl, new() { ["HX"] = "a & type nul > env-ctl.txt" });
            Check("H9c", "control: a raw %VAR% expansion of that value DID run the marker command", File.Exists(Path.Combine(ctl, "env-ctl.txt")));
        });
        await Case("H9d", async () =>
        {
            var call = new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command",
                Arguments = new() { ["path"] = "x\"", ["command"] = "y & type nul > parity-marker.txt" } };
            var r = await RunHook("PreToolUse", "echo {path} \"{command}\"", call);
            Check("H9d", "a quote in one value cannot flip cmd's quote state and expose a later quoted value",
                r.Success && !File.Exists(Path.Combine(hookProject, "parity-marker.txt")), r.Message);
        });
        await Case("H9e", async () =>
        {
            var c1 = new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = "a' ; New-Item -ItemType File -Path ps-marker.txt ; '", ["content"] = "" } };
            var r1 = await RunHook("PreToolUse", "powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"", c1);
            var c2 = new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = "(New-Item -ItemType File -Path ps-marker2.txt)", ["content"] = "" } };
            var r2 = await RunHook("PreToolUse", "powershell -NoProfile -Command \"Test-Path {path} | Out-Null\"", c2);
            // r2 may fail (Test-Path gets stray words as parameters) — what matters is that nothing ran.
            Check("H9e", "{path} inside a powershell -Command template cannot break out as code (quote / ; / ( ) stripped)",
                r1.Success && !File.Exists(Path.Combine(hookProject, "ps-marker.txt")) && !File.Exists(Path.Combine(hookProject, "ps-marker2.txt")),
                $"{r1.ExitCode} {r1.Message} | {r2.ExitCode} {r2.Message}");
            string ctl = Dir("h9e-control");
            RunProcess("powershell.exe", new[] { "-NoProfile", "-Command", "Test-Path (New-Item -ItemType File -Path ps-ctl.txt) | Out-Null" }, ctl);
            Check("H9f", "control: unsanitized, that value DID run as PowerShell code", File.Exists(Path.Combine(ctl, "ps-ctl.txt")));
        });
        await Case("H9h", () =>
        {
            var stop = catalog.First(b => b.Id == "stop-pattern-extract");
            string stopCmd = HookBundleService.BuildShellCommand(@"C:\x\stop-pattern-extract.ps1", stop);
            string model = HookService.SubstituteToken(stopCmd, "{model}", "qwen2.5-coder (Q4_K_M)");
            string code = HookService.SubstituteToken("powershell -NoProfile -Command \"Get-Item '{path}'\"", "{path}", "a (b)'; c");
            string second = HookService.SubstituteToken("powershell -File x.ps1 & powershell -Command \"Test-Path '{path}'\"", "{path}", "a (b)'; c");
            Check("H9h", "PowerShell-code stripping applies only where the token IS code: a -File script's -Cost/-Model args keep '(…)'",
                stopCmd.Contains("-Cost") && model.Contains("qwen2.5-coder (Q4_K_M)")
                && code.EndsWith("Get-Item 'a b c'\"") && second.EndsWith("Test-Path 'a b c'\""), $"{model} | {code} | {second}");
            return Task.CompletedTask;
        });
        await Case("H9g", async () =>
        {
            var call = new ToolCall { Type = ToolType.WriteFile, ToolName = "write_file",
                Arguments = new() { ["path"] = "evil\0.txt", ["content"] = "" } };
            var r = await RunHook("PreToolUse", "exit 1", call);
            Check("H9g", "a NUL in the path no longer makes the hook fail to start (= allowed): a blocking hook still blocks",
                !r.Success && r.ExitCode == 1, $"{r.ExitCode} {r.Message}");
        });
        await Case("H9i", async () =>
        {
            // The classic plain-/c idiom: one more pair of quotes around the whole line.
            string tool = Dir("Program Files", "echo tool");
            string echoArgs = Path.Combine(tool, "echo-args.cmd");
            File.WriteAllText(echoArgs, "@echo ARGS=%*\r\n@exit /b 0\r\n");
            string doubled = $"\"\"{echoArgs}\" \"{{path}}\"\"";
            string doubledBare = $"\"\"{echoArgs}\" --x {{path}}\"";
            int mark = HookLogMark();
            var r = await RunHook("PreToolUse", doubled, WriteAt(@"R&D\x y.cs"));
            var r2 = await RunHook("PreToolUse", doubledBare, WriteAt(@"R&D\x.cs"));
            var evil = await RunHook("PreToolUse", doubled, WriteAt("a\" & type nul > dq-marker.txt & \""));
            bool injected = File.Exists(Path.Combine(hookProject, "dq-marker.txt"));
            Check("H9i", "a doubled-quote template \"\"C:\\Program Files\\…\\tool.cmd\" \"{path}\"\" runs and gets {path} quoted (was: broken under cmd /s = not blocking); the bare-token form too",
                r.Success && !IsScriptError(r.Message) && r.Message == "ARGS=\"R&D\\x y.cs\""
                && r2.Success && r2.Message == "ARGS=--x RD\\x.cs" && evil.Success && !injected
                && !HookLogSince(mark).Any(m => m.Contains("script error")),
                $"r={r.ExitCode} {r.Message} | r2={r2.ExitCode} {r2.Message} | evil={evil.ExitCode} {evil.Message} injected={injected}");
            string ctl = Dir("h9i-control");
            string plain = $"\"\"{echoArgs}\" \"plain.cs\"\"";
            var oldWay = RawCmd("/c " + plain, ctl);
            var unfixed = RawCmd("/v:off /s /c \"" + plain + "\"", ctl);
            Check("H9j", "control: that line works under the old `cmd /c` and breaks under `cmd /s /c` when the extra pair is left on",
                oldWay.code == 0 && oldWay.output.Contains("ARGS=\"plain.cs\"") && unfixed.code != 0 && unfixed.output.Contains("is not recognized"),
                $"old={oldWay.code} {oldWay.output.Trim()} | unfixed={unfixed.code} {unfixed.output.Trim()}");
        });

        // ── finding 5: payload temp folder ──
        await Case("H8", async () =>
        {
            string oldTmp = Environment.GetEnvironmentVariable("TMP") ?? "", oldTemp = Environment.GetEnvironmentVariable("TEMP") ?? "";
            string outFile = Path.Combine(Scratch, "h8-seen.txt");
            string script = Ps1("h8-payload.ps1",
                "$f = $env:CLUADEX_HOOK_PAYLOAD_FILE\r\n" +
                $"($f + '|' + (Test-Path -LiteralPath $f)) | Out-File -Encoding utf8 -LiteralPath '{outFile}'\r\nexit 0\r\n");
            string folderName = (string)typeof(HookService).GetField("PayloadFolderName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            void SetTemp(string p) { Environment.SetEnvironmentVariable("TMP", p); Environment.SetEnvironmentVariable("TEMP", p); }
            try
            {
                string squat = Dir("tmp-squat");
                File.WriteAllText(Path.Combine(squat, "cluadex-hooks"), "a FILE where the old fixed folder went");
                SetTemp(squat);
                var r = await RunHook("PreToolUse", PsFile(script), writeCall);
                string[] seen = File.ReadAllText(outFile).Trim().TrimStart('\uFEFF').Split('|');
                Check("H8", "a FILE named %TEMP%\\cluadex-hooks no longer breaks hooks — the payload goes to a per-process random folder",
                    r.Success && seen.Length == 2 && seen[1] == "True" && seen[0].Contains(folderName) && !File.Exists(seen[0]), $"{r.Message} · {string.Join("|", seen)}");

                string squat2 = Dir("tmp-squat2");
                File.WriteAllText(Path.Combine(squat2, folderName), "even the random folder name is taken");
                SetTemp(squat2);
                File.Delete(outFile);
                var f = await RunHook("PreToolUse", PsFile(script), writeCall);
                seen = File.ReadAllText(outFile).Trim().TrimStart('\uFEFF').Split('|');
                Check("H8a", "…and if even that folder can't be created, the payload falls back to a unique temp file",
                    f.Success && seen.Length == 2 && seen[1] == "True" && !seen[0].Contains(folderName) && !File.Exists(seen[0]), $"{f.Message} · {string.Join("|", seen)}");

                string blocker = Path.Combine(Scratch, "blocker.txt");
                File.WriteAllText(blocker, "x");
                SetTemp(Path.Combine(blocker, "sub"));   // a temp root under a FILE: nothing can be written there
                int mark = HookLogMark();
                var pre = await RunHook("PreToolUse", "exit 0", writeCall);
                var post = await RunHook("PostToolUse", "exit 0", writeCall);
                Check("H8b", "when the payload cannot be written at all, a PreToolUse hook BLOCKS with a clear localized message (was: silently allowed)",
                    !pre.Success && pre.Message == loc.T("hook.blockedPayload") && HookLogSince(mark).Any(m => m.Contains("payload could not be written")), pre.Message);
                Check("H8c", "…while a PostToolUse hook still runs (without the payload file) and the gap is logged",
                    post.Success && HookLogSince(mark).Any(m => m.Contains("WITHOUT CLUADEX_HOOK_PAYLOAD_FILE")), post.Message);
            }
            finally { Environment.SetEnvironmentVariable("TMP", oldTmp); Environment.SetEnvironmentVariable("TEMP", oldTemp); }
        });

        // ── finding 4: shell-safety hooks cover task_create and powershell ──
        await Case("H10", async () =>
        {
            string[] shellIds = { "pretool-dangerous-cmd-warn", "pretool-git-push-confirm", "pretool-pre-commit-quality", "pretool-block-dev-server" };
            Check("H10", "the four shell-safety bundles match run_command, task_create, powershell, run_build and run_tests",
                shellIds.All(id => catalog.First(b => b.Id == id).Matcher == HookBundleService.ShellCommandTools)
                && new[] { "run_command", "task_create", "powershell", "run_build", "run_tests" }.All(t => HookService.MatchesToolName(HookBundleService.ShellCommandTools, t)),
                string.Join(" | ", shellIds.Select(id => catalog.First(b => b.Id == id).Matcher)));

            var canon = typeof(HookService).GetMethod("CanonicalToolName", BindingFlags.NonPublic | BindingFlags.Static)!;
            string viaAlias = (string)canon.Invoke(null, new object[] { new ToolCall { Type = ToolType.PowerShell, ToolName = "pwsh" } })!;
            Check("H10a", "a powershell call made through the 'pwsh' alias is matched as 'powershell' (was 'power_shell')", viaAlias == "powershell", viaAlias);
            string buildAlias = (string)canon.Invoke(null, new object[] { new ToolCall { Type = ToolType.RunBuild, ToolName = "typecheck" } })!;
            string testAlias = (string)canon.Invoke(null, new object[] { new ToolCall { Type = ToolType.RunTests, ToolName = "test" } })!;
            var rb = await RunBundled("pretool-dangerous-cmd-warn", Shell("run_build", "command", "rm -rf /"));
            var rt = await RunBundled("pretool-dangerous-cmd-warn", Shell("run_tests", "command", "format c:"));
            var rbOk = await RunBundled("pretool-dangerous-cmd-warn", new ToolCall { Type = ToolType.RunBuild, ToolName = "run_build", Arguments = new() });
            Check("H10f", "run_build / run_tests (also via their 'typecheck' / 'test' aliases) are covered: a dangerous 'command' override is refused with exit 2, the auto-detected build is not",
                buildAlias == "run_build" && testAlias == "run_tests" && rb.ExitCode == 2 && rt.ExitCode == 2 && rbOk.Success,
                $"alias={buildAlias}/{testAlias} build={rb.ExitCode} {rb.Message} | tests={rt.ExitCode} {rt.Message} | none={rbOk.ExitCode} {rbOk.Message}");
            var editorKeys = (Dictionary<string, Dictionary<string, string>>)typeof(LocalizationService).Assembly
                .GetType("CluadeX.Services.Localization.EditorKeys")!.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            // bg_start (a background dev server; task_create is its alias) is matched too since T2 — HookBundleService.ShellCommandTools.
            var coveredTools = HookBundleService.ShellCommandTools.Split('|');
            Check("H10g", "the four shell-safety descriptions (en + th, and the English fallback) name every tool they cover (bg_start included)",
                coveredTools.Contains("bg_start")
                && shellIds.All(id => new[] { "en", "th" }.All(lang => coveredTools.All(editorKeys[$"hookb.{id}.desc"][lang].Contains)))
                && shellIds.All(id => coveredTools.All(catalog.First(b => b.Id == id).Description.Contains)),
                string.Join(" | ", shellIds.SelectMany(id => new[] { "en", "th" }.Select(lang =>
                    $"{id}.{lang} missing [{string.Join(",", coveredTools.Where(t => !editorKeys[$"hookb.{id}.desc"][lang].Contains(t)))}]"))));

            var t = await RunBundled("pretool-dangerous-cmd-warn", Shell("task_create", "command", "rm -rf /"));
            var ps = await RunBundled("pretool-dangerous-cmd-warn", Shell("powershell", "script", "Format-Volume -DriveLetter D"));
            var ok = await RunBundled("pretool-dangerous-cmd-warn", Shell("powershell", "script", "Get-ChildItem"));
            Check("H10b", "the dangerous-command hook now refuses task_create's command and powershell's script (exit 2), allows a harmless script",
                t.ExitCode == 2 && ps.ExitCode == 2 && ok.Success, $"task={t.ExitCode} {t.Message} | ps={ps.ExitCode} {ps.Message} | ok={ok.Message}");

            string oldProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", Dir("fake-profile"));   // no allow-push-main marker here
                var push = await RunBundled("pretool-git-push-confirm", Shell("powershell", "script", "git push origin main"));
                Check("H10c", "the push gate reads a powershell 'script' too, and blocks with exit 2 without quoting it",
                    push.ExitCode == 2 && !push.Message.Contains("origin"), $"{push.ExitCode} {push.Message}");
            }
            finally { Environment.SetEnvironmentVariable("USERPROFILE", oldProfile); }

            // An existing install: the state file only says which hooks are ON; the projection written at
            // every start takes the matcher from the catalog.
            string projDir = Dir("projection");
            string json = HookBundleService.BuildProjectedConfig(catalog,
                new Dictionary<string, bool> { ["pretool-dangerous-cmd-warn"] = true, ["pretool-git-push-confirm"] = true }, projDir);
            string projFile = Path.Combine(projDir, "hooks-bundled.json");
            File.WriteAllText(projFile, json);
            var parsed = (List<HookDefinition>)typeof(HookService).GetMethod("ParseHooksFile", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { projFile })!;
            var pre = parsed.Where(h => h.Phase == "PreToolUse").ToList();
            Check("H10d", "an existing install's re-projected hooks-bundled.json carries the new matcher for its enabled shell hooks",
                pre.Count(h => h.Command.Contains("dangerous-cmd-warn") || h.Command.Contains("git-push-confirm")) == 2
                && pre.Where(h => h.Command.Contains("dangerous-cmd-warn") || h.Command.Contains("git-push-confirm"))
                      .All(h => HookService.MatchesToolName(h.Matcher, "task_create") && HookService.MatchesToolName(h.Matcher, "powershell")),
                string.Join(" | ", pre.Select(h => h.Matcher)));

            // …and its deployed scripts are replaced even when the deployed copy has the NEWER timestamp.
            string deployDir = Dir("deployed");
            string stale = Path.Combine(deployDir, "pretool-dangerous-cmd-warn.ps1");
            File.WriteAllText(stale, "Write-Error \"Refusing dangerous command: $Command\"\r\nexit 1\r\n");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(1));
            int dmark = HookLogMark();
            int first = HookBundleService.DeployScripts(bundled, deployDir, hookDbg);
            int again = HookBundleService.DeployScripts(bundled, deployDir, hookDbg);
            bool same = File.ReadAllBytes(stale).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(bundled, "pretool-dangerous-cmd-warn.ps1")));
            var baks = Directory.GetFiles(deployDir, "*.bak");
            var dlog = HookLogSince(dmark);
            bool stamped = baks.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(baks[0]),
                @"^pretool-dangerous-cmd-warn\.ps1\." + DateTime.Now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture) + @"\d{4}-\d{6}\.bak$");
            Check("H10e", "deploy replaces an outdated deployed script even when it looks newer, keeps it as a TIMESTAMPED .bak, logs the file and where the .bak went, and is idempotent",
                same && stamped && File.ReadAllText(baks[0]).Contains("exit 1") && first == Directory.GetFiles(bundled, "*.ps1").Length && again == 0
                && dlog.Any(m => m.Contains("pretool-dangerous-cmd-warn.ps1") && m.Contains(baks[0])),
                $"same={same} baks={string.Join(",", baks.Select(Path.GetFileName))} first={first} again={again} · {string.Join(" ⏎ ", dlog)}");

            // A copy CluadeX itself wrote in an older release is replaced quietly; a hand-edited one is backed up.
            string deployDir2 = Dir("deployed2");
            HookBundleService.DeployScripts(bundled, deployDir2, hookDbg);
            string hashes = Path.Combine(deployDir2, HookBundleService.DeployedHashesFile);
            string own = Path.Combine(deployDir2, "pretool-git-push-confirm.ps1");
            string edited = Path.Combine(deployDir2, "pretool-block-dev-server.ps1");
            File.WriteAllText(own, "# an older CluadeX release of this script\r\nexit 0\r\n");
            var rec = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(hashes))!;
            rec["pretool-git-push-confirm.ps1"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(own)));
            File.WriteAllText(hashes, JsonSerializer.Serialize(rec));
            File.AppendAllText(edited, "\r\n# my local tweak\r\n");
            int dmark2 = HookLogMark();
            int n2 = HookBundleService.DeployScripts(bundled, deployDir2, hookDbg);
            var baks2 = Directory.GetFiles(deployDir2, "*.bak").Select(Path.GetFileName).ToList();
            var dlog2 = HookLogSince(dmark2);
            Check("H10h", "…a script CluadeX deployed in an older release is replaced with no backup (logged as such); a hand-edited one is backed up",
                n2 == 2 && baks2.Count == 1 && baks2[0]!.StartsWith("pretool-block-dev-server.ps1.")
                && dlog2.Any(m => m.Contains("pretool-git-push-confirm.ps1") && m.Contains("older CluadeX release"))
                && dlog2.Any(m => m.Contains("pretool-block-dev-server.ps1") && m.Contains(".bak")),
                $"n={n2} baks={string.Join(",", baks2)} · {string.Join(" ⏎ ", dlog2)}");
        });
        await Case("H11", async () =>
        {
            string gitDir = Dir("commitrepo");
            string sneaky = "running scripts is disabled.env";
            File.WriteAllText(Path.Combine(gitDir, sneaky), "SECRET=1\n");
            Git(gitDir, "init", "-q");
            Git(gitDir, "add", "-A");
            string prev = fs.WorkingDirectory;
            fs.WorkingDirectory = gitDir;
            try
            {
                var r = await RunBundled("pretool-pre-commit-quality", Shell("run_command", "command", "git commit -m wip"));
                Check("H11", "pre-commit check blocks a staged .env with exit 2 and a count — never the (repo-chosen) file name",
                    r.ExitCode == 2 && r.Message.Contains("1 sensitive file") && !r.Message.Contains("running scripts"), $"{r.ExitCode} {r.Message}");
            }
            finally { fs.WorkingDirectory = prev; }
        });
        await Case("H13", async () =>
        {
            // The shape of the curated branch-protector command: quotes, a pipe, redirection, && / || and a
            // parenthesized block — it must behave exactly as it did under the old cmd /c.
            string Protector(string branch) => $"echo {branch}| findstr /x /i \"main master develop\" >nul && (echo [branch-protector] Blocked: protected branch. 1>&2 & exit 1) || exit 0";
            var onMain = await RunHook("PreToolUse", Protector("main"), Shell("run_command", "command", "git commit -m x"));
            var onFeature = await RunHook("PreToolUse", Protector("feature"), Shell("run_command", "command", "git commit -m x"));
            var oldMain = RawCmd("/c " + Protector("main"), hookProject);
            var oldFeature = RawCmd("/c " + Protector("feature"), hookProject);
            Check("H13", "a complex existing hook command (quotes, |, >nul, && ( … ) ||) behaves under cmd /s /c exactly as under the old cmd /c",
                !onMain.Success && onMain.ExitCode == 1 && onMain.Message.StartsWith("[branch-protector]") && onFeature.Success
                && oldMain.code == 1 && oldFeature.code == 0,
                $"new: main={onMain.ExitCode} {onMain.Message} feature={onFeature.ExitCode} | old: main={oldMain.code} feature={oldFeature.code}");
        });

        // ── round 2: values that cmd parses a SECOND time ──
        await Case("H14", async () =>
        {
            fs.WorkingDirectory = hookProject;
            var pipe = await RunHook("PreToolUse", "echo {command} | sort", Shell("run_command", "command", "a & type nul > pipe-marker.txt"));
            var forf = await RunHook("PreToolUse", "for /f \"delims=\" %a in ('echo {command}') do @echo [%a]",
                Shell("run_command", "command", "a & type nul > forf-marker.txt"));
            bool pipeHit = File.Exists(Path.Combine(hookProject, "pipe-marker.txt")), forHit = File.Exists(Path.Combine(hookProject, "forf-marker.txt"));
            Check("H14", "an unquoted tool value LEFT of a pipe, or inside for /f ('…'), cannot chain a command (metacharacters dropped, not caret-escaped)",
                pipe.Success && forf.Success && !pipeHit && !forHit, $"pipe={pipeHit} {pipe.ExitCode} {pipe.Message} | for/f={forHit} {forf.ExitCode} {forf.Message}");
            string ctl = Dir("h14-control");
            RawCmd("/v:off /s /c \"echo a ^& type nul ^> pipe-ctl.txt | sort\"", ctl);
            RawCmd("/v:off /s /c \"for /f \"delims=\" %a in ('echo a ^& type nul ^> forf-ctl.txt') do @echo [%a]\"", ctl);
            Check("H14a", "control: the round-1 caret-escaped form of that value DID run the marker command in both places",
                File.Exists(Path.Combine(ctl, "pipe-ctl.txt")) && File.Exists(Path.Combine(ctl, "forf-ctl.txt")));

            // Quoted values are re-parsed too when the line hands itself to a cmd that strips quotes.
            var nested = await RunHook("PreToolUse", "cmd /c \"echo {path}\"", WriteAt("a & type nul > nested-marker.txt"));
            var forq = await RunHook("PreToolUse", "for /f \"delims=\" %a in ('\"{path}\"') do @echo [%a]", WriteAt("a & type nul > forq-marker.txt"));
            var piped = await RunHook("PreToolUse", "echo \"{path}\" | sort", WriteAt("R&D & type nul > pq-marker.txt"));
            bool nHit = File.Exists(Path.Combine(hookProject, "nested-marker.txt")), qHit = File.Exists(Path.Combine(hookProject, "forq-marker.txt"));
            bool pqHit = File.Exists(Path.Combine(hookProject, "pq-marker.txt"));
            Check("H14b", "a QUOTED value inside a nested cmd /c \"…\" or a for /f ('\"…\"') cannot chain a command either; left of a pipe it stays literal (R&D kept)",
                !nHit && !qHit && !pqHit && nested.Success && nested.Message.StartsWith("a ") && piped.Success
                && piped.Message.Contains("\"R&D & type nul > pq-marker.txt\""),
                $"nested={nHit} {nested.ExitCode} {nested.Message} | for/f={qHit} {forq.ExitCode} | pipe={pqHit} {piped.Message}");
            RawCmd("/v:off /s /c \"cmd /c \"echo a & type nul > nested-ctl.txt\"\"", ctl);
            RawCmd("/v:off /s /c \"for /f \"delims=\" %a in ('\"a & type nul > forq-ctl.txt\"') do @echo [%a]\"", ctl);
            Check("H14c", "control: with round-1's quoted sanitizing (& kept) both of those lines DID run the marker command",
                File.Exists(Path.Combine(ctl, "nested-ctl.txt")) && File.Exists(Path.Combine(ctl, "forq-ctl.txt")));
        });
        await Case("H14d", async () =>
        {
            fs.WorkingDirectory = hookProject;
            const string v = "R&D; it's (1).md";
            string keep = HookService.SubstituteToken("tool \"{path}\"", "{path}", v);
            string arg = HookService.SubstituteToken("node check.js --shell powershell \"{path}\"", "{path}", v);
            string code = HookService.SubstituteToken("powershell -NoProfile -Command \"Test-Path '{path}'\"", "{path}", v);
            string later = HookService.SubstituteToken("powershell -NoProfile -Command \"Get-Date\" & node x.js \"{path}\"", "{path}", v);
            Check("H14d", "a quoted value keeps ; ' ( ) & unless it is PowerShell code; 'powershell' as an ARGUMENT, or a host in an earlier command, is not its host",
                keep == "tool \"R&D; it's (1).md\"" && arg == "node check.js --shell powershell \"R&D; it's (1).md\""
                && code == "powershell -NoProfile -Command \"Test-Path 'RD its 1.md'\"" && later.EndsWith("node x.js \"R&D; it's (1).md\""),
                $"{keep} | {arg} | {code} | {later}");

            // …while a powershell that IS a command's first word behind a launcher is still recognized.
            string[] launchers =
            {
                "cmd /c powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "cmd.exe /s /c \"powershell -NoProfile -Command Test-Path '{path}'\"",
                "if exist C:\\ powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "call powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "echo x & powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
            };
            var hits = new List<string>();
            for (int n = 0; n < launchers.Length; n++)
            {
                string marker = $"ps-launch-{n}.txt";
                await RunHook("PreToolUse", launchers[n], WriteAt($"a'; New-Item -ItemType File -Path {marker}; '"));
                if (File.Exists(Path.Combine(hookProject, marker))) hits.Add(launchers[n]);
            }
            Check("H14e", "behind cmd /c, cmd /s /c \"…\", if, call, a quoted full path or after & — a {path} in powershell -Command still cannot run code",
                hits.Count == 0, string.Join(" ⏎ ", hits));
            // Control: each of those lines, with the value spliced in raw and run the way HookService runs a hook,
            // DOES execute it as PowerShell — so every case above was a real host position, not a dead line.
            string ctl = Dir("h14e-control");
            var missed = new List<string>();
            for (int n = 0; n < launchers.Length; n++)
            {
                string marker = $"ps-ctl-{n}.txt";
                string line = launchers[n].Replace("{path}", $"a'; New-Item -ItemType File -Path {marker}; '");
                RawCmd("/v:off /s /c \"" + line + "\"", ctl);
                if (!File.Exists(Path.Combine(ctl, marker))) missed.Add(launchers[n]);
            }
            Check("H14f", "control: spliced in unsanitized, the value DID run as PowerShell code in every one of those positions",
                missed.Count == 0, string.Join(" ⏎ ", missed));
        });

        // ── round 3: start's switches before its title, sudo / gsudo wrappers ──
        await Case("H16", async () =>
        {
            // Fake wrappers that just run their arguments. The REAL C:\Windows\System32\sudo.exe must never run (it
            // elevates): the shim folder goes FIRST on PATH, the hooks run in folders that hold no sudo.*, and `where`
            // must resolve both names to the shims there before anything is launched.
            string shims = Dir("wrap-shims");
            File.WriteAllText(Path.Combine(shims, "sudo.cmd"), "@%*\r\n");
            File.WriteAllText(Path.Combine(shims, "gsudo.cmd"), "@%*\r\n");
            string wrapRun = Dir("wrap-run"), wrapCtl = Dir("wrap-ctl");
            string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string prevWd = fs.WorkingDirectory;
            Environment.SetEnvironmentVariable("PATH", shims + ";" + oldPath);
            try
            {
                bool Resolves(string name, string cwd)
                {
                    var w = RunProcess("where", new[] { name }, cwd);
                    string firstHit = w.output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                    return w.code == 0 && firstHit.Equals(Path.Combine(shims, name + ".cmd"), StringComparison.OrdinalIgnoreCase);
                }
                bool safe = new[] { wrapRun, wrapCtl }.All(d => Resolves("sudo", d) && Resolves("gsudo", d));
                Check("H16g", "guard: in the test folders `sudo` / `gsudo` resolve to the harness shims, never the real sudo.exe", safe);
                if (!safe) return;

                string[] lines =
                {
                    "start /b /wait \"\" powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                    "start /wait \"\" /b powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                    "sudo powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                    "gsudo powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                    $"\"{Path.Combine(shims, "gsudo.cmd")}\" powershell -NoProfile -Command \"Test-Path '{{path}}' | Out-Null\"",
                };
                fs.WorkingDirectory = wrapRun;
                var hits = new List<string>();
                for (int n = 0; n < lines.Length; n++)
                {
                    string marker = $"wrap-{n}.txt";
                    var r = await RunHook("PreToolUse", lines[n], WriteAt($"a'; New-Item -ItemType File -Path {marker}; '"));
                    if (File.Exists(Path.Combine(wrapRun, marker))) hits.Add($"{lines[n]} ({r.ExitCode} {r.Message})");
                }
                Check("H16", "start with /switches BEFORE (or around) its title, sudo, gsudo (bare or by full path): {path} in their powershell -Command cannot run code",
                    hits.Count == 0, string.Join(" ⏎ ", hits));
                var missed = new List<string>();
                for (int n = 0; n < lines.Length; n++)
                {
                    string marker = $"wrap-ctl-{n}.txt";
                    RawCmd("/v:off /s /c \"" + lines[n].Replace("{path}", $"a'; New-Item -ItemType File -Path {marker}; '") + "\"", wrapCtl);
                    if (!File.Exists(Path.Combine(wrapCtl, marker))) missed.Add(lines[n]);
                }
                Check("H16a", "control: spliced in raw, the value DID run as PowerShell code behind every one of those launchers",
                    missed.Count == 0, string.Join(" ⏎ ", missed));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
                fs.WorkingDirectory = prevWd;
            }
        });
        await Case("H16b", () =>
        {
            const string v = "a'; b (c)";
            string[] forms =
            {
                "gsudo -n --integrity High powershell -NoProfile -Command \"Test-Path '{path}'\"",
                "sudo --inline -E powershell -NoProfile -Command \"Test-Path '{path}'\"",
                "runas /user:Administrator \"powershell -NoProfile -Command Test-Path '{path}'\"",
                "start /d \"C:\\x y\" /b \"\" /affinity 3 pwsh -c \"Test-Path '{path}'\"",
                "start/b powershell -NoProfile -Command \"Test-Path '{path}'\"",
                "@sudo.exe pwsh -c \"Test-Path '{path}'\"",
            };
            var leaked = forms.Select(f => HookService.SubstituteToken(f, "{path}", v))
                              .Where(s => s.Contains("a'") || s.Contains(';') || s.Contains('(')).ToList();
            string notPs = HookService.SubstituteToken("sudo node check.js \"{path}\"", "{path}", v);
            Check("H16b", "…with the wrappers' own options (gsudo -n --integrity High, sudo --inline -E), runas \"…\", start /d \"dir\" /affinity n, start/b: PowerShell syntax stripped; a wrapper running node keeps it",
                leaked.Count == 0 && notPs == "sudo node check.js \"a'; b (c)\"", string.Join(" ⏎ ", leaked) + " | " + notPs);
            return Task.CompletedTask;
        });

        // ── round 3: a ^" in the template flips quote parity for a pipe's child cmd ──
        await Case("H17", async () =>
        {
            fs.WorkingDirectory = hookProject;
            const string evil = "a & type nul > caretq-marker.txt & rem";
            var r = await RunHook("PreToolUse", "echo ^\" \"{path}\" | sort", WriteAt(evil));
            bool hit = File.Exists(Path.Combine(hookProject, "caretq-marker.txt"));
            string unit = HookService.SubstituteToken("tool ^\" \"{path}\"", "{path}", "R&D (x) | y");
            string plain = HookService.SubstituteToken("tool \"{path}\"", "{path}", "R&D (x) | y");
            Check("H17", "a template with ^\" anywhere sanitizes every token as unquoted: left of a pipe the child cmd's flipped quote parity cannot expose & (no marker); without ^\" a quoted value keeps R&D",
                r.Success && !hit && unit == "tool ^\" \"RD x  y\"" && plain == "tool \"R&D (x) | y\"", $"hit={hit} {r.ExitCode} {r.Message} | {unit} | {plain}");
            string ctl = Dir("h17-control");
            RawCmd("/v:off /s /c \"echo ^\" \"" + evil.Replace("caretq-marker", "caretq-ctl") + "\" | sort\"", ctl);
            Check("H17a", "control: with round-2's quoted sanitizing (& kept inside the \"…\") that line DID run the marker command",
                File.Exists(Path.Combine(ctl, "caretq-ctl.txt")));
        });

        // ── round 3: only the hook's OWN script can earn the fail-closed exemption ──
        await Case("H18", async () =>
        {
            fs.WorkingDirectory = hookProject;
            // A project file the model can write, mentioning the payload variable, named on the hook's line as an argument.
            File.WriteAllText(Path.Combine(hookProject, "notes.ps1"), "# reads $env:CLUADEX_HOOK_PAYLOAD_FILE\r\n");
            string own = Ps1("h18-own.ps1", "exit 0\r\n");
            string ownAware = Ps1("h18-own-aware.ps1", "$null = $env:CLUADEX_HOOK_PAYLOAD_FILE\r\nexit 0\r\n");
            var padded = Shell("run_command", "command", "rm -rf / " + new string(' ', 9000) + "#");
            var r = await RunHook("PreToolUse", PsFile(own, "\"{command}\" notes.ps1"), padded);
            var aware = await RunHook("PreToolUse", PsFile(ownAware, "\"{command}\" notes.ps1"), padded);
            Check("H18", "a padded call is BLOCKED although a project-relative notes.ps1 on the hook's line mentions CLUADEX_HOOK_PAYLOAD_FILE — only the script the hook runs counts",
                !r.Success && r.Message == loc.T("hook.blockedTooLong"), $"{r.ExitCode} {r.Message}");
            Check("H18a", "control: when the hook's OWN script reads CLUADEX_HOOK_PAYLOAD_FILE the same line runs",
                aware.Success && aware.ExitCode == 0, $"{aware.ExitCode} {aware.Message}");
        });

        // ── round 3: PowerShell 7 (ConciseView) forms of the broken-hook signatures ──
        await Case("H19", async () =>
        {
            fs.WorkingDirectory = hookProject;
            // pwsh is not on the dev machine, so each hook prints what PowerShell 7 prints, then exits 1.
            const string notRecognized = "is not recognized as a name of a cmdlet, function, script file, or executable program.";
            const string spelling = "Check the spelling of the name, or if a path was included, verify that the path is correct and try again.";
            const string policy = "cannot be loaded because running scripts is disabled on this system. For more information, see about_Execution_Policies at https://go.microsoft.com/fwlink/?LinkID=135170.";
            string W(string s) => $"[Console]::Error.WriteLine(\"{s.Replace("\"", "`\"")}\")\r\n";
            string Box(params string[] l) => W("Line |") + string.Concat(l.Select(W));
            var hooksBroken = new Dictionary<string, string>
            {
                ["missing (-Command)"] = Ps1("h19-missing.ps1", W($"Get-CluadexGone7: The term 'Get-CluadexGone7' {notRecognized}") + W(spelling) + "exit 1\r\n"),
                ["missing (in script)"] = Ps1("h19-missing-at.ps1", W("Get-CluadexGone7: ${PSCommandPath}:3")
                    + Box("   3 |  Get-CluadexGone7", "     |  ~~~~~~~~~~~~~~~~", "     | The term 'Get-CluadexGone7' is not recognized as a name of a", "     | cmdlet, function, script file, or executable program.") + "exit 1\r\n"),
                ["parse error"] = Ps1("h19-parse.ps1", W("ParserError: ${PSCommandPath}:1")
                    + Box("   1 |  if ( {", "     |        ~", "     | Missing closing ')' after expression in 'if' statement.") + "exit 1\r\n"),
                ["policy (path)"] = Ps1("h19-policy.ps1", W("${PSCommandPath}: File ${PSCommandPath} " + policy) + "exit 1\r\n"),
                ["policy (&)"] = Ps1("h19-policy-amp.ps1", W("&: File ${PSCommandPath} " + policy) + "exit 1\r\n"),
            };
            var wrong = new List<string>();
            foreach (var (name, script) in hooksBroken)
            {
                var r = await RunHook("PreToolUse", PsFile(script), Shell("run_command", "command", "ls"));
                if (!(r.Success && IsScriptError(r.Message))) wrong.Add($"{name}: {r.ExitCode} {r.Message}");
            }
            Check("H19", "PowerShell 7's ConciseView forms of a broken hook are 'not blocking': 'X: The term …', 'X: <its .ps1>:3' + Line box, 'ParserError: <its .ps1>:1', '<its .ps1>: File … cannot be loaded', '&: File …'",
                wrong.Count == 0, string.Join(" ⏎ ", wrong));

            string otherPs1 = Path.Combine(Scratch, "some-other-hook.ps1");
            string rawEcho = Ps1("h19-echo-raw.ps1",
                "$p = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json\r\n" +
                "[Console]::Error.WriteLine($p.command)\r\nexit 1\r\n");
            var stillBlocks = new Dictionary<string, (string Hook, string Command)>
            {
                ["a name the hook never calls"] = (PsFile(rawEcho), $"Remove-Everything: The term 'Remove-Everything' {notRecognized}"),
                ["another script's parse error"] = (PsFile(Ps1("h19-other-parse.ps1", W($"ParserError: {otherPs1}:1") + Box("     | x") + "exit 1\r\n")), "ls"),
                ["its own path without the Line box"] = (PsFile(Ps1("h19-nobox.ps1", W("ParserError: ${PSCommandPath}:1") + W("blocked") + "exit 1\r\n")), "ls"),
                ["'blocked x: File <its .ps1>'"] = (PsFile(Ps1("h19-talker.ps1", W("blocked x: File ${PSCommandPath} " + policy) + "exit 1\r\n")), "ls"),
                ["another script refused"] = (PsFile(Ps1("h19-other-policy.ps1", W($"{otherPs1}: File {otherPs1} " + policy) + "exit 1\r\n")), "ls"),
                ["the form on line 2"] = (PsFile(Ps1("h19-second.ps1", W("Refused.") + W($"Get-CluadexGone7: The term 'Get-CluadexGone7' {notRecognized}") + "exit 1\r\n")), "ls"),
            };
            var opened = new List<string>();
            foreach (var (name, (hook, cmd)) in stillBlocks)
            {
                var r = await RunHook("PreToolUse", hook, Shell("run_command", "command", cmd));
                if (r.Success || r.ExitCode != 1) opened.Add($"{name}: {r.ExitCode} {r.Message}");
            }
            Check("H19a", "…while look-alikes still BLOCK: a name the hook never calls, another script's path, no Line box, 'blocked x: File …', the form on line 2",
                opened.Count == 0, string.Join(" ⏎ ", opened));
        });

        // ── round 3 (review): reversed host default — an unenumerated launcher still makes the next
        //    powershell/pwsh a host, so a {path} in its -Command cannot run code ──
        await Case("H20", async () =>
        {
            fs.WorkingDirectory = hookProject;
            string[] lines =
            {
                "%ComSpec% /c powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "2>nul powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                ">nul powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
                "if not exist \"C:\\Program Files\\no such dir\" powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",
            };
            var hits = new List<string>();
            for (int n = 0; n < lines.Length; n++)
            {
                string marker = $"rev-{n}.txt";
                var r = await RunHook("PreToolUse", lines[n], WriteAt($"a'; New-Item -ItemType File -Path {marker}; '"));
                if (File.Exists(Path.Combine(hookProject, marker))) hits.Add($"{lines[n]} ({r.ExitCode} {r.Message})");
            }
            Check("H20", "a powershell word behind %ComSpec% /c, a leading 2>nul / >nul redirect, or if not exist \"spaced path\" is a HOST: {path} in its -Command cannot run code",
                hits.Count == 0, string.Join(" ⏎ ", hits));
            string ctl = Dir("h20-control");
            var missed = new List<string>();
            for (int n = 0; n < lines.Length; n++)
            {
                string marker = $"rev-ctl-{n}.txt";
                RawCmd("/v:off /s /c \"" + lines[n].Replace("{path}", $"a'; New-Item -ItemType File -Path {marker}; '") + "\"", ctl);
                if (!File.Exists(Path.Combine(ctl, marker))) missed.Add(lines[n]);
            }
            Check("H20a", "control: spliced in raw, the value DID run as PowerShell code behind every one of those forms",
                missed.Count == 0, string.Join(" ⏎ ", missed));
            string arg = HookService.SubstituteToken("node check.js --shell powershell \"{path}\"", "{path}", "R&D; it's (1).md");
            Check("H20b", "…but 'powershell' as an option's argument (node --shell powershell) is still not a host — the quoted value keeps ; ' ( )",
                arg == "node check.js --shell powershell \"R&D; it's (1).md\"", arg);
        });

        // ── round 3 (review): %ComSpec% /c "…" re-parses a quoted value exactly like cmd /c ──
        await Case("H21", async () =>
        {
            fs.WorkingDirectory = hookProject;
            string[] lines =
            {
                "%ComSpec% /c \"echo {path}\"",
                "%COMSPEC% /d /s /c \"echo {path}\"",
                "\"%ComSpec%\" /c \"echo {path}\"",
            };
            var hits = new List<string>();
            for (int n = 0; n < lines.Length; n++)
            {
                string marker = $"comspec-{n}.txt";
                var r = await RunHook("PreToolUse", lines[n], WriteAt($"R&D & type nul > {marker}"));
                if (File.Exists(Path.Combine(hookProject, marker))) hits.Add($"{lines[n]} ({r.ExitCode} {r.Message})");
            }
            Check("H21", "a QUOTED value inside %ComSpec% /c \"…\" (any case, quoted, with /d /s switches) is re-parsed like cmd /c: & cannot chain a command",
                hits.Count == 0, string.Join(" ⏎ ", hits));
            string ctl = Dir("h21-control");
            RawCmd("/v:off /s /c \"%ComSpec% /c \"echo R&D & type nul > comspec-ctl.txt\"\"", ctl);
            Check("H21a", "control: the same %ComSpec% /c line with & kept inside the quotes DID run the marker command",
                File.Exists(Path.Combine(ctl, "comspec-ctl.txt")));
            string viaComspec = HookService.SubstituteToken("%ComSpec% /c \"echo {path}\"", "{path}", "R&D (x).md");
            string viaCmd = HookService.SubstituteToken("cmd /c \"echo {path}\"", "{path}", "R&D (x).md");
            Check("H21b", "…%ComSpec% /c strips & ( ) from the quoted value exactly as cmd /c does",
                viaComspec == "%ComSpec% /c \"echo RD x.md\"" && viaCmd == "cmd /c \"echo RD x.md\"", $"{viaComspec} | {viaCmd}");
        });

        // ── round 3 (review): a ^ made adjacent to a " by an EMPTY token is a run-time ^" ──
        var expandTemplateMi = typeof(HookService).GetMethod("ExpandTemplate", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Expand(string tmpl, Dictionary<string, string> v) => (string)expandTemplateMi.Invoke(null, new object[] { tmpl, v })!;
        await Case("H22", async () =>
        {
            fs.WorkingDirectory = hookProject;
            var call = new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command",
                Arguments = new() { ["path"] = "", ["command"] = "a & type nul > caret-empty-marker.txt & rem" } };
            var r = await RunHook("PreToolUse", "echo ^{path}\" \"{command}\" | sort", call);
            bool hit = File.Exists(Path.Combine(hookProject, "caret-empty-marker.txt"));
            string exp = Expand("echo ^{path}\" \"{command}\" | sort",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["{path}"] = "", ["{command}"] = "a & type nul > m.txt & rem" });
            Check("H22", "a ^ made adjacent to a \" by an empty token is treated as a run-time ^\": every token is sanitized as unquoted, so & / > cannot reach the piped child cmd (no marker)",
                r.Success && !hit && !exp.Contains('&') && !exp.Contains('>'), $"hit={hit} {r.ExitCode} {r.Message} | exp={exp}");
        });

        // ── round 3 (review): a missing-command name counts only against the hook's OWN script ──
        await Case("H23", async () =>
        {
            fs.WorkingDirectory = hookProject;
            const string notRec = "is not recognized as a name of a cmdlet, function, script file, or executable program.";
            File.WriteAllText(Path.Combine(hookProject, "proj.ps1"), "# a project file the model controls; it calls Get-ProjFn\r\n");
            // The own script PRINTS the PS7 missing-command error but its SOURCE never spells the whole word
            // Get-ProjFn (built at run time), so only the project file proj.ps1 mentions it. Before the fix that
            // project file earned the hook a "broken, not blocking" pass; now only the own script counts.
            string own = Ps1("h23-own.ps1",
                "$n = 'Get-Proj' + 'Fn'\r\n" +
                $"[Console]::Error.WriteLine(\"${{n}}: The term '${{n}}' {notRec}\")\r\nexit 1\r\n");
            var r = await RunHook("PreToolUse", PsFile(own, "\"{command}\" proj.ps1"), Shell("run_command", "command", "ls"));
            Check("H23", "a missing-command name is matched only against the hook's OWN script, not a project file named as an argument: the exit-1 still BLOCKS",
                !r.Success && r.ExitCode == 1, $"{r.ExitCode} {r.Message}");
            string ownCalls = Ps1("h23-own-calls.ps1",
                $"function demo {{ Get-ProjFn }}\r\n[Console]::Error.WriteLine(\"Get-ProjFn: The term 'Get-ProjFn' {notRec}\")\r\nexit 1\r\n");
            var r2 = await RunHook("PreToolUse", PsFile(ownCalls, "\"{command}\" proj.ps1"), Shell("run_command", "command", "ls"));
            Check("H23a", "control: when the hook's own script DOES call the missing cmdlet, it is 'not blocking' (a genuinely broken hook)",
                r2.Success && IsScriptError(r2.Message), $"{r2.ExitCode} {r2.Message}");
        });

        // ── round 3 (review): no regression vs the committed baseline aa8726c. A corpus of injections must be
        //    neutralized by THIS build everywhere the baseline neutralized them; benign values stay intact. ──
        await Case("HD", async () =>
        {
            string baselineBin = Environment.GetEnvironmentVariable("CLUADEX_BASELINE_BIN")
                ?? @"D:\Temp\User\claude\E--Code-ClaudeClient\47cfc86e-4b5a-4f93-931f-f28701d3987f\scratchpad\baseline-aa8726c\bld";
            string baselineDll = Path.Combine(baselineBin, "CluadeX.dll");
            if (!File.Exists(baselineDll))
            {
                Check("HD", $"differential vs baseline aa8726c SKIPPED — baseline CluadeX.dll not found (set CLUADEX_BASELINE_BIN)", true, baselineDll);
                return;
            }
            // Load the baseline into its OWN context so an identical assembly identity cannot unify to the
            // already-loaded new build (Assembly.LoadFrom would return the loaded one and test nothing).
            var alc = new System.Runtime.Loader.AssemblyLoadContext("baseline-aa8726c", isCollectible: false);
            var baseAsm = alc.LoadFromAssemblyPath(baselineDll);
            var baseType = baseAsm.GetType("CluadeX.Services.HookService")!;
            var baseSub = baseType.GetMethod("SubstituteToken", BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(string), typeof(string), typeof(string) })!;
            string BaseSub(string c, string t, string v) => (string)baseSub.Invoke(null, new object[] { c, t, v })!;
            // Prove we really loaded the baseline file, not the running build unified by identity.
            Check("HD0", "the baseline build loaded from its own path (differential compares two distinct binaries)",
                string.Equals(baseAsm.Location, baselineDll, StringComparison.OrdinalIgnoreCase)
                && !ReferenceEquals(baseType, typeof(HookService)), $"{baseAsm.Location}");

            // Each case injects through a single {path} token and tries to create its marker.
            var corpus = new (string Tmpl, string Val)[]
            {
                ("cmd /c \"echo {path}\"",                                              "R&D & type nul > hd-0.txt"),
                ("%ComSpec% /c \"echo {path}\"",                                        "R&D & type nul > hd-1.txt"),
                ("echo {path} | sort",                                                  "a & type nul > hd-2.txt"),
                ("for /f \"delims=\" %a in ('echo {path}') do @echo [%a]",              "a & type nul > hd-3.txt"),
                ("powershell -NoProfile -Command \"Test-Path '{path}' | Out-Null\"",    "a'; New-Item -ItemType File -Path hd-4.txt; '"),
                ("echo ^\" \"{path}\" | sort",                                          "a & type nul > hd-5.txt & rem"),
                ("echo {path}",                                                         "x ; type nul > hd-6.txt"),
            };
            string newDir = Dir("hd-new"), baseDir = Dir("hd-base");
            var newAllows = new List<string>();        // THIS build let an injection run — a hard FAIL
            var regressions = new List<string>();      // baseline neutralized, this build did not — the differential FAIL
            for (int n = 0; n < corpus.Length; n++)
            {
                var (tmpl, val) = corpus[n];
                string marker = $"hd-{n}.txt";
                string newOut = HookService.SubstituteToken(tmpl, "{path}", val);
                string baseOut = BaseSub(tmpl, "{path}", val);
                RawCmd("/v:off /s /c \"" + newOut + "\"", newDir);   // this build's launch form
                bool newHit = File.Exists(Path.Combine(newDir, marker));
                RawCmd("/c " + baseOut, baseDir);                   // baseline's launch form (plain /c)
                bool baseHit = File.Exists(Path.Combine(baseDir, marker));
                if (newHit) newAllows.Add($"[{n}] {tmpl}  →  {newOut}");
                if (!baseHit && newHit) regressions.Add($"[{n}] {tmpl}: baseline blocked, new allows");
            }
            Check("HD", "no injection in the corpus runs on this build (and none runs that the baseline aa8726c blocked)",
                newAllows.Count == 0 && regressions.Count == 0,
                "new allows: " + string.Join(" ⏎ ", newAllows) + "  |  regressions: " + string.Join(" ⏎ ", regressions));

            // Benign values the new quote-aware code intentionally keeps intact inside a safely quoted arg.
            var benign = new (string Tmpl, string Val, string Expect)[]
            {
                ("tool \"{path}\"", "R&D (x).md",                 "tool \"R&D (x).md\""),
                ("tool \"{path}\"", "report (final) & notes.md",  "tool \"report (final) & notes.md\""),
                ("tool \"{path}\"", "a; b (c).md",                "tool \"a; b (c).md\""),
            };
            var mangled = new List<string>();
            foreach (var (tmpl, val, expect) in benign)
            {
                string got = HookService.SubstituteToken(tmpl, "{path}", val);
                if (got != expect) mangled.Add($"{val} → {got} (want {expect})");
            }
            Check("HDb", "benign values stay intact inside a safely quoted arg (R&D (x).md, report (final) & notes.md, a; b (c).md)",
                mangled.Count == 0, string.Join(" ⏎ ", mangled));
        });

        // ── round 2: curated plugin hooks block with exit 2, never quote model values; branch-protector works ──
        await Case("H15", async () =>
        {
            var writeHooks = typeof(PluginService).GetMethod("WriteCatalogHookFiles", BindingFlags.NonPublic | BindingFlags.Static)!;
            var parse = typeof(HookService).GetMethod("ParseHooksFile", BindingFlags.NonPublic | BindingFlags.Static)!;
            HookDefinition PluginHook(string id)
            {
                var cat = PluginService.CuratedCatalog.First(c => c.Id == id);
                string dir = Dir("plugins", id);
                writeHooks.Invoke(null, new object[] { cat, dir });
                var defs = (List<HookDefinition>)parse.Invoke(null, new object[] { Path.Combine(dir, "hooks.json") })!;
                var d = defs.First(h => h.Phase == "PreToolUse");
                d.TimeoutMs = 30000;
                return d;
            }
            Task<HookResult> Run(HookDefinition h, ToolCall c) => hooks.RunHookAsync(h, new HookSessionContext { ToolCall = c }, CancellationToken.None);

            fs.WorkingDirectory = hookProject;
            var guard = PluginHook("path-guard");
            var g1 = await Run(guard, WriteAt(@"..\..\ZQXoutside dir\evil.txt", "x"));
            var g2 = await Run(guard, WriteAt(@"src\ok.txt", "x"));
            Check("H15", "curated path-guard blocks a path outside the project with exit 2 and does not quote the (model-chosen) path",
                g1.ExitCode == 2 && g1.Message.StartsWith("[path-guard]") && !g1.Message.Contains("ZQX") && g2.Success,
                $"{g1.ExitCode} {g1.Message} | inside={g2.ExitCode} {g2.Message}");

            var size = PluginHook("size-guard");
            var s1 = await Run(size, WriteAt("big.txt", new string('x', 600 * 1024)));
            Check("H15a", "curated size-guard blocks a 600 KB write with exit 2", s1.ExitCode == 2 && s1.Message.StartsWith("[size-guard]"), $"{s1.ExitCode} {s1.Message}");

            var sec = PluginHook("secret-scanner");
            var s2 = await Run(sec, WriteAt("ZQXname.py", "key = 'AKIA" + "ABCDEFGHIJKLMNOP'\n"));
            Check("H15b", "curated secret-scanner blocks a leaked AWS key with exit 2, naming only the kind of secret",
                s2.ExitCode == 2 && s2.Message.Contains("AWS") && !s2.Message.Contains("AKIA") && !s2.Message.Contains("ZQX"), $"{s2.ExitCode} {s2.Message}");

            var bp = PluginHook("branch-protector");
            string repo = Dir("bp-repo");
            Git(repo, "-c", "init.defaultBranch=main", "init", "-q");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");
            var commit = new ToolCall { Type = ToolType.GitCommit, ToolName = "git_commit", Arguments = new() { ["message"] = "x" } };
            var push = new ToolCall { Type = ToolType.GitPush, ToolName = "git_push", Arguments = new() };
            fs.WorkingDirectory = repo;
            try
            {
                var onMain = await Run(bp, commit);
                var pushMain = await Run(bp, push);
                var old = RawCmd("/c git branch --show-current | findstr /x /i \"main master develop\" >nul && (echo [branch-protector] Blocked 1>&2 & exit 1) || exit 0", repo);
                Git(repo, "checkout", "-q", "-b", "feature/x");
                var onFeature = await Run(bp, commit);
                Check("H15c", "curated branch-protector now BLOCKS git_commit / git_push on main (exit 2) and allows a feature branch",
                    onMain.ExitCode == 2 && onMain.Message.StartsWith("[branch-protector]") && pushMain.ExitCode == 2 && onFeature.Success
                    && HookService.MatchesToolName(bp.Matcher, "git_commit") && HookService.MatchesToolName(bp.Matcher, "git_push"),
                    $"main={onMain.ExitCode} {onMain.Message} push={pushMain.ExitCode} feature={onFeature.ExitCode} {onFeature.Message} matcher={bp.Matcher}");
                Check("H15d", "control: the old findstr one-liner exited 0 on main — it never blocked", old.code == 0, $"{old.code} {old.output}");
            }
            finally { fs.WorkingDirectory = hookProject; }
        });
        await Case("H12", () =>
        {
            var files = Directory.GetFiles(bundled, "*.ps1");
            var noBom = files.Where(f => { var b = File.ReadAllBytes(f); return !(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF); }).ToList();
            var pre = files.Where(f => Path.GetFileName(f).StartsWith("pretool-")).ToList();
            var oldBlock = pre.Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f),
                @"(?mi)^\s*(exit\s+1\b|Write-Error\b)")).ToList();
            Check("H12", "every bundled .ps1 keeps its UTF-8 BOM; every PreToolUse script blocks with exit 2, none with exit 1 / Write-Error",
                files.Length >= 16 && noBom.Count == 0 && pre.Count == 5 && oldBlock.Count == 0,
                "noBOM: " + string.Join(", ", noBom.Select(Path.GetFileName)) + " · old-style: " + string.Join(", ", oldBlock.Select(Path.GetFileName)));
            return Task.CompletedTask;
        });
        fs.WorkingDirectory = hookProject;

        // ═══════════════════════ editor save ═══════════════════════
        Console.WriteLine("\n── editor save ──");
        string saveDir = Dir("save");
        fs.WorkingDirectory = saveDir;
        var ws = new CodeWorkspaceService(fs, new GitService(fs));
        string f1 = Path.Combine(saveDir, "a.txt");
        await Case("W1", async () =>
        {
            File.WriteAllText(f1, "original\n");
            var tab = await ws.OpenFileAsync(f1);
            tab!.Content = "my edit\n";
            File.WriteAllText(f1, "agent edit\n");                         // someone else writes the file
            var r = await ws.SaveTabCheckedAsync(tab, overwriteExternalChanges: false);
            Check("W1", "Save does NOT overwrite a file that changed on disk since it was opened",
                r.Outcome == SaveOutcome.ChangedOnDisk && File.ReadAllText(f1) == "agent edit\n" && tab.IsDirty, $"{r.Outcome} disk='{File.ReadAllText(f1)}'");
            var o = await ws.SaveTabCheckedAsync(tab, overwriteExternalChanges: true);
            Check("W1b", "…Overwrite writes it, atomically (no temp file left behind)",
                o.Outcome == SaveOutcome.Saved && File.ReadAllText(f1) == "my edit\n" && !tab.IsDirty
                && !Directory.EnumerateFiles(saveDir, "*.cluadex-tmp").Any(), o.Outcome.ToString());
            tab.Content = "second edit\n";
            var again = await ws.SaveTabCheckedAsync(tab, false);
            Check("W1c", "…and the next save of our own file is not a false conflict", again.Outcome == SaveOutcome.Saved, again.Outcome.ToString());
        });
        await Case("W2", async () =>
        {
            File.WriteAllText(f1, "v1\n");
            var tab = await ws.OpenFileAsync(f1);
            File.WriteAllText(f1, "v2 from the agent\n");
            var fresh = await ws.OpenFileAsync(f1);                         // live-follow reload
            tab!.MarkOpened(fresh!.Content);
            ws.AdoptDiskState(tab, fresh);
            tab.Content = "v2 from the agent\nplus mine\n";
            var r = await ws.SaveTabCheckedAsync(tab, false);
            Check("W2", "a tab reloaded from disk adopts the new fingerprint (no false 'changed on disk')", r.Outcome == SaveOutcome.Saved, r.Outcome.ToString());
        });
        await Case("W3", async () =>
        {
            File.WriteAllText(f1, "doomed\n");
            var tab = await ws.OpenFileAsync(f1);
            tab!.Content = "keep me\n";
            File.Delete(f1);
            var r = ws.SaveTabChecked(tab, false);
            Check("W3", "synchronous save (app exit) detects a file deleted on disk", r.Outcome == SaveOutcome.ChangedOnDisk && r.DeletedOnDisk, r.Outcome.ToString());
        });
        await Case("W4", async () =>
        {
            string ro = Path.Combine(saveDir, "ro.txt");
            File.WriteAllText(ro, "locked\n");
            var tab = await ws.OpenFileAsync(ro);
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            tab!.Content = "nope\n";
            var r = await ws.SaveTabCheckedAsync(tab, false);
            File.SetAttributes(ro, FileAttributes.Normal);
            Check("W4", "a read-only file reports Failed (with a diagnostic) instead of pretending", r.Outcome == SaveOutcome.Failed && File.ReadAllText(ro) == "locked\n" && tab.IsDirty, $"{r.Outcome} {r.Error}");
        });

        // ═══════════════════════ large files, typing reveal, panel grips ═══════════════════════
        Console.WriteLine("\n── large files / typing reveal / resize grips ──");
        string big = Path.Combine(saveDir, "big.log");
        await Case("L1", async () =>
        {
            // ~30 MB of log lines: the editor used to refuse anything over 2 MB.
            var sb = new StringBuilder();
            for (int i = 0; sb.Length < 30_000_000; i++) sb.Append("2026-10-11 12:00:00.000 [INFO] line ").Append(i).Append(" payload payload payload\n");
            File.WriteAllText(big, sb.ToString());
            var sw = Stopwatch.StartNew();
            var tab = await ws.OpenFileAsync(big);
            sw.Stop();
            Check("L1", "a 30 MB text file opens in the editor (Notepad++-style cap, was 2 MB)",
                tab != null && tab.Content.Length == sb.Length, $"tab={(tab == null ? "null" : tab.Content.Length.ToString())} expected={sb.Length} in {sw.ElapsedMilliseconds} ms");
            string bin = Path.Combine(saveDir, "blob.bin");
            var bytes = new byte[3_000_000]; bytes[10] = 0; bytes[0] = (byte)'M';
            File.WriteAllBytes(bin, bytes);
            Check("L1b", "…a binary file is still refused at any size", await ws.OpenFileAsync(bin) == null);
        });
        await Case("L2", async () =>
        {
            var tab = await ws.OpenFileAsync(big);
            Check("L2", "a large tab is not 'changed on disk' right after opening (stamp = length + write time)", !ws.HasChangedOnDisk(tab!));
            tab!.Content = tab.Content.Replace("line 0 ", "LINE 0 ");
            var r = await ws.SaveTabCheckedAsync(tab, false);
            Check("L2b", "…saving it works and the next check is clean", r.Outcome == SaveOutcome.Saved && !ws.HasChangedOnDisk(tab), r.Outcome.ToString());
            var text = File.ReadAllText(big);
            File.WriteAllText(big, text.Replace("LINE 0 ", "line 0 "));   // same length, someone else's write
            File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddMinutes(1));
            Check("L2c", "…and another program's same-length rewrite is still caught", ws.HasChangedOnDisk(tab));
        });
        await Case("L3", async () =>
        {
            File.WriteAllText(f1, "before\n");
            var tab = await ws.OpenFileAsync(f1);
            tab!.IsDirty = true;                                          // large-file typing: dirty now, text later
            tab.PendingEdits = () => tab.Content = "typed but not yet synced\n";
            var r = await ws.SaveTabCheckedAsync(tab, false);
            Check("L3", "Save copies the editor's pending keystrokes into the tab first (large-file lazy sync)",
                r.Outcome == SaveOutcome.Saved && File.ReadAllText(f1) == "typed but not yet synced\n", $"{r.Outcome} disk='{File.ReadAllText(f1)}'");
        });
        await Case("L4", () =>
        {
            var diff = new EditDiffResult
            {
                Lines =
                [
                    new EditDiffLine { Kind = EditDiffKind.Context, Text = "b" },
                    new EditDiffLine { Kind = EditDiffKind.Remove, Text = "old" },
                    new EditDiffLine { Kind = EditDiffKind.Add, Text = "NEW1" },
                    new EditDiffLine { Kind = EditDiffKind.Add, Text = "NEW2" },
                    new EditDiffLine { Kind = EditDiffKind.Context, Text = "c" },
                ],
                FirstChangedLine = 3,
            };
            string before = CluadeX.ViewModels.CodeEditorViewModel.AddedBlockOf("a\nb\nNEW1\nNEW2\nc\n", diff, 3);
            Check("L4", "typing reveal for a file that was not open: the diff's added lines are taken out to be typed back in",
                before == "a\nb\nc\n", before ?? "null");
            string crlf = CluadeX.ViewModels.CodeEditorViewModel.AddedBlockOf("a\r\nb\r\nNEW1\r\nNEW2\r\nc\r\n", diff, 3);
            Check("L4b", "…CRLF files too", crlf == "a\r\nb\r\nc\r\n", crlf ?? "null");
            Check("L4c", "…and no guess when the block is not in the file (instant refresh instead)",
                CluadeX.ViewModels.CodeEditorViewModel.AddedBlockOf("a\nb\nc\n", diff, 3) == null);
            return Task.CompletedTask;
        });
        await Case("L5", () => OnSta(() =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 1_000_000; i++) sb.Append(i % 7 == 0 ? "    // comment\n" : "\tcode(line);\n");
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = sb.ToString() };
            var minimap = new CluadeX.Views.Editor.MinimapControl();
            var sw = Stopwatch.StartNew();
            minimap.Attach(editor);                                       // builds the line metrics synchronously
            sw.Stop();
            var f = typeof(CluadeX.Views.Editor.MinimapControl).GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance);
            int samples = (f?.GetValue(minimap) as System.Collections.ICollection)?.Count ?? -1;
            Check("L5", "the minimap of a 1,000,000-line file samples ≤ 4000 lines instead of copying the text",
                samples is > 0 and <= 4001 && sw.ElapsedMilliseconds < 500, $"samples={samples} in {sw.ElapsedMilliseconds} ms");
        }));
        await Case("L6", () => OnSta(() =>
        {
            var saved = new Dictionary<string, double>();
            CluadeX.Views.ResizeGrip.Load = k => k == "t.left" ? 300 : null;
            CluadeX.Views.ResizeGrip.Save = (k, w) => saved[k] = w;
            try
            {
                var panel = new System.Windows.Controls.Border { Width = 240 };
                var grip = new CluadeX.Views.ResizeGrip { Key = "t.left", MinSize = 160, MaxSize = 500, Target = panel };
                Check("L6", "a panel comes back at the width it was dragged to", panel.Width == 300, panel.Width.ToString());
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(50, 0));
                Check("L6b", "dragging the right edge widens it", panel.Width == 350, panel.Width.ToString());
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(1000, 0));
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false));
                Check("L6c", "…clamped to its maximum, and the width is remembered", panel.Width == 500 && saved.GetValueOrDefault("t.left") == 500, $"{panel.Width} saved={saved.GetValueOrDefault("t.left")}");
                grip.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent });
                Check("L6d", "double-click puts it back to its designed width", panel.Width == 240 && saved["t.left"] == 240, panel.Width.ToString());

                var right = new System.Windows.Controls.Border { Width = 400 };
                var rgrip = new CluadeX.Views.ResizeGrip { Key = "t.right", OnLeftEdge = true, MinSize = 280, MaxSize = 900, Target = right };
                rgrip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-60, 0));
                Check("L6e", "a right-hand panel's left-edge grip widens it when dragged left", right.Width == 460, right.Width.ToString());
                rgrip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(400, 0));
                Check("L6f", "…and never below its minimum", right.Width == 280, right.Width.ToString());
            }
            finally
            {
                CluadeX.Views.ResizeGrip.Load = null;
                CluadeX.Views.ResizeGrip.Save = null;
            }
        }));

        // ═══════════════════════ git status ═══════════════════════
        Console.WriteLine("\n── git status / blobs ──");
        await Case("G1", () =>
        {
            var s = new GitStatusSnapshot { RepoRoot = @"C:\repo" };
            GitService.ParsePorcelainZ("## No commits yet on master\0?? a b.txt\0?? docs/ก ข.md\0R  new name.cs\0old name.cs\0 M R&D/x.cs\0", s);
            Check("G1", "porcelain -z: spaces, Thai names and renames parse unquoted; a new repo's branch is 'master' (was 'No')",
                s.Branch == "master" && s.NoCommits && s.Entries.Count == 4
                && s.Entries[0].RepoPath == "a b.txt" && s.Entries[0].IsUntracked
                && s.Entries[1].RepoPath == "docs/ก ข.md"
                && s.Entries[2].RepoPath == "new name.cs" && s.Entries[2].OrigRepoPath == "old name.cs"
                && s.Entries[3].Code == "M" && s.Entries[3].FullPath == @"C:\repo\R&D\x.cs",
                string.Join(" | ", s.Entries.Select(e => $"{e.XY}:{e.RepoPath}<{e.OrigRepoPath}>")) + " branch=" + s.Branch);
            var s2 = new GitStatusSnapshot();
            GitService.ParsePorcelainZ("## release/1.2...origin/release/1.2 [ahead 1]\0", s2);
            Check("G1b", "a branch with a dot is not cut short ('release/1.2', was 'release/1')", s2.Branch == "release/1.2", s2.Branch);
            return Task.CompletedTask;
        });
        await Case("G2", async () =>
        {
            string gitRepo = Dir("gitrepo");
            string sub = Dir("gitrepo", "R&D sub");
            File.WriteAllText(Path.Combine(sub, "ไฟล์ ทดสอบ.txt"), "head text\n");
            File.WriteAllText(Path.Combine(gitRepo, "root.txt"), "root\n");
            Git(gitRepo, "init", "-q");
            Git(gitRepo, "add", "-A");
            Git(gitRepo, "commit", "-q", "-m", "init");
            File.WriteAllText(Path.Combine(sub, "ไฟล์ ทดสอบ.txt"), "working text\n");
            File.WriteAllText(Path.Combine(gitRepo, "root.txt"), "root changed\n");

            fs.WorkingDirectory = sub;                                     // project = a SUBFOLDER of the repo
            var git = new GitService(fs);
            var st = await git.StatusEntriesAsync();
            var e = st.Entries.FirstOrDefault();
            Check("G2", "status of a project opened in a repo subfolder lists only that folder, with real paths",
                st.Success && st.Entries.Count == 1 && e != null && e.RepoPath == "R&D sub/ไฟล์ ทดสอบ.txt"
                && File.Exists(e.FullPath), $"ok={st.Success} err={st.Error} " + string.Join(" | ", st.Entries.Select(x => x.RepoPath + " → " + x.FullPath)));
            var blob = await git.ReadBlobAsync("HEAD", e?.RepoPath ?? "", 2 * 1024 * 1024);
            Check("G2b", "…and HEAD's version of that file loads (Thai name, '&', space — diff no longer empty)",
                blob.Exists && blob.Text.Replace("\r", "") == "head text\n", $"exists={blob.Exists} text='{blob.Text}'");
            File.WriteAllBytes(Path.Combine(gitRepo, "img.bin"), new byte[] { 1, 0, 2, 0, 3 });
            fs.WorkingDirectory = gitRepo;
            Git(gitRepo, "add", "img.bin");
            Git(gitRepo, "commit", "-q", "-m", "bin");
            var bin = await git.ReadBlobAsync("HEAD", "img.bin", 1024);
            var big = await git.ReadBlobAsync("HEAD", "root.txt", 2);
            Check("G2c", "…a binary blob and an over-limit blob are reported, not read", bin.Binary && big.TooLarge, $"bin={bin.Binary} big={big.TooLarge}");
        });

        // ═══════════════════════ rename ═══════════════════════
        Console.WriteLine("\n── rename ──");
        await Case("R1", async () =>
        {
            string rdir = Dir("rename");
            fs.WorkingDirectory = rdir;
            string longLine = "var x = OldSym + " + new string('y', 1200) + " + OldSym;";
            File.WriteAllText(Path.Combine(rdir, "A.cs"), "class OldSym { }\n" + longLine + "\n");
            File.WriteAllText(Path.Combine(rdir, "B.cs"), "OldSym a;\nOldSym b;\n");
            var intel = new CodeIntelligenceService(fs, new RepoMapService(fs));
            var plan = await intel.PrepareRenameAsync("OldSym", "NewSym");
            Check("R1", "the rename preview lists occurrences on lines over 1,000 chars (was: skipped, then renamed anyway)",
                plan.Error == null && plan.TotalEdits == 5, $"err={plan.Error} total={plan.TotalEdits}");

            // B.cs changes after the preview: the previewed positions no longer hold.
            File.WriteAllText(Path.Combine(rdir, "B.cs"), "// a new first line\nOldSym a;\nOldSym b;\n");
            var res = await intel.ApplyRenameAsync(plan);
            string a = File.ReadAllText(Path.Combine(rdir, "A.cs"));
            string b = File.ReadAllText(Path.Combine(rdir, "B.cs"));
            Check("R1b", "apply rewrites exactly the previewed occurrences and reports the real count",
                res.EditsApplied == 3 && res.FilesChanged == 1 && !a.Contains("OldSym") && a.Split("NewSym").Length == 4, $"applied={res.EditsApplied} files={res.FilesChanged}");
            Check("R1c", "…and leaves a file that changed since the preview untouched (and says so)",
                b == "// a new first line\nOldSym a;\nOldSym b;\n" && res.Skipped.Count == 1, string.Join(" | ", res.Skipped));
        });

        // ═══════════════════════ diff ═══════════════════════
        Console.WriteLine("\n── diff ──");
        await Case("D1", async () =>
        {
            var rng = new Random(7);
            var a = Enumerable.Range(0, 60_000).Select(i => $"\"pkg-{i}\": \"1.{i % 97}.{i % 13}\",").ToArray();
            var b = (string[])a.Clone();
            foreach (var i in Enumerable.Range(0, 900).Select(_ => rng.Next(b.Length)).Distinct()) b[i] += " // bumped";
            string left = string.Join("\n", a), right = string.Join("\n", b);
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var diff = DiffService.BuildSideBySide(left, right);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("D1", $"a 60,000-line lockfile diff with ~900 changes allocates {allocated / 1_048_576} MB (was ≈1.7 GB of trace)",
                !diff.Truncated && allocated < 400L * 1024 * 1024, $"truncated={diff.Truncated} note={diff.Note} {sw.ElapsedMilliseconds} ms");
            var l = diff.Rows.Where(r => r.LeftLine != null).Select(r => r.LeftText);
            var rt = diff.Rows.Where(r => r.RightLine != null).Select(r => r.RightText);
            Check("D1b", "…and the rows still reproduce both files exactly", l.SequenceEqual(a) && rt.SequenceEqual(b), $"rows={diff.Rows.Count}");
            await Task.CompletedTask;
        });

        // ═══════════════════════ terminal ═══════════════════════
        Console.WriteLine("\n── terminal ──");
        await Case("T1", () =>
        {
            string parent = Dir("term");
            string evil = Dir("term", "x$(New-Item -ItemType File pwned.txt)'y");
            string cmd = CluadeX.Services.EmbeddedTerminal.BuildSetLocationCommand(evil);
            var (_, output) = RunProcess("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-Command", "-" }, parent,
                cmd + "\n(Get-Location).ProviderPath\n");
            bool pwned = Directory.EnumerateFiles(parent, "pwned.txt", SearchOption.AllDirectories).Any();
            Check("T1", "switching the terminal to a folder named x$(New-Item …)'y lands there and runs nothing",
                !pwned && output.Contains(evil), $"cmd={cmd}\noutput={output}");

            // Control: the OLD form really did execute it — so T1 is proving something.
            string ctl = Dir("term-control");
            string evil2 = Dir("term-control", "x$(New-Item -ItemType File pwned.txt)");
            RunProcess("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-Command", "-" }, ctl, $"cd \"{evil2}\"\n");
            bool oldRan = Directory.EnumerateFiles(ctl, "pwned.txt", SearchOption.AllDirectories).Any();
            Check("T1b", "control: the old `cd \"…\"` line DID execute the folder name's $(…)", oldRan);
            return Task.CompletedTask;
        });

        // ═══════════════════════ security scan ═══════════════════════
        Console.WriteLine("\n── security shield ──");
        var log = new DebugLogService();
        var shield = new SecurityShieldService(fs, log);
        await Case("S1", async () =>
        {
            string sdir = Dir("shield");
            File.WriteAllText(Path.Combine(sdir, "a.py"), "api_key = 'sk-" + new string('a', 30) + "'\n");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool threw = false;
            try { await shield.ScanFolderAsync(sdir, cts.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check("S1", "a cancelled scan throws (was: reported as 'Done: 0 findings')", threw);
        });
        await Case("S2", async () =>
        {
            string sdir = Dir("shield2");
            Dir("shield2", "src");
            File.WriteAllText(Path.Combine(sdir, "src", "k.py"), "api_key = 'sk-" + new string('b', 30) + "'\n");
            RunProcess("cmd.exe", new[] { "/c", "mklink", "/J", Path.Combine(sdir, "src", "loop"), sdir }, sdir);
            bool junction = Directory.Exists(Path.Combine(sdir, "src", "loop"));
            var sw = Stopwatch.StartNew();
            var r = await shield.ScanFolderAsync(sdir);
            Check("S2", "a junction looping back to the project root is skipped (one file scanned, not a walk to OOM)",
                junction && r.FilesScanned == 1 && r.Findings.Count >= 1 && sw.ElapsedMilliseconds < 20000,
                $"junction={junction} scanned={r.FilesScanned} findings={r.Findings.Count} {sw.ElapsedMilliseconds} ms");
        });

        // ═══════════════════════ C# debugging ═══════════════════════
        Console.WriteLine("\n── C# debugging: build the project ──");
        await Case("B1", async () =>
        {
            string proj = Dir("dbgapp");
            Dir("dbgapp", "Sub");
            File.WriteAllText(Path.Combine(proj, "dbgapp.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            string src = Path.Combine(proj, "Sub", "Program.cs");
            File.WriteAllText(src, "System.Console.WriteLine(42);\n");
            Dir("dbglib");
            File.WriteAllText(Path.Combine(Scratch, "dbglib", "lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

            string? found = DebugAdapterService.FindProjectFile(src, Scratch);
            Check("B1", "the .csproj that owns a .cs file is found (walking up from a subfolder)",
                found != null && Path.GetFileName(found) == "dbgapp.csproj", found ?? "(none)");
            Check("B1b", "…an Exe project is runnable, a class library is not",
                DebugAdapterService.IsRunnableProject(found!) && !DebugAdapterService.IsRunnableProject(Path.Combine(Scratch, "dbglib", "lib.csproj")));

            fs.WorkingDirectory = proj;
            var dap = new DebugAdapterService(fs);
            var build = typeof(DebugAdapterService).GetMethod("BuildDotnetProgramAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)build.Invoke(dap, new object?[] { src, proj, CancellationToken.None })!;
            await task;
            var result = (DebugAdapterService.DotnetBuildResult)task.GetType().GetProperty("Result")!.GetValue(task)!;
            Check("B1c", "…and builds to the .dll netcoredbg must launch (it was handed the .cs and could never start)",
                result.Error == null && result.AssemblyPath != null && result.AssemblyPath.EndsWith("dbgapp.dll", StringComparison.OrdinalIgnoreCase)
                && File.Exists(result.AssemblyPath), result.Error ?? result.AssemblyPath ?? "");
        });

        // ═══════════════════════ instincts ═══════════════════════
        Console.WriteLine("\n── instincts ──");
        await Case("I1", () =>
        {
            var store = new InstinctStore
            {
                Instincts = new()
                {
                    new Instinct { Pattern = "Always commit + push when user says 'push' (no further confirmation)", OccurrenceCount = 5, AcceptCount = 2 },
                    new Instinct { Pattern = "Run dotnet build after every C# edit before declaring done", OccurrenceCount = 8, AcceptCount = 4 },   // user accepted once more
                    new Instinct { Pattern = "A real one", OccurrenceCount = 1 },
                },
            };
            var m = typeof(InstinctService).GetMethod("RemoveUntouchedDemoSeeds", BindingFlags.NonPublic | BindingFlags.Static)!;
            int removed = (int)m.Invoke(null, new object[] { store })!;
            Check("I1", "the fabricated 'push without asking' seed is removed; a seed the user voted on and real instincts stay",
                removed == 1 && store.Instincts.Count == 2 && store.Instincts.All(i => !i.Pattern.StartsWith("Always commit")),
                string.Join(" | ", store.Instincts.Select(i => i.Pattern)));
            return Task.CompletedTask;
        });

        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL ═══");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        return Fail == 0 ? 0 : 1;
    }
}
