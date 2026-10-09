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
///       quote-aware substitution, fail-open when a hook cannot even start
///   W*  editor save: changed-on-disk detection, atomic write, no false conflicts after a reload
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
            Check("H2b", "…and still blocks an AWS key buried at the end of a large file", !r.Success && r.Message.Contains("AWS Access Key"), r.Message);
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
            string bare = HookService.SubstituteToken("tool {path}", "{path}", @"C:\R&D\a.md");
            string hostile = HookService.SubstituteToken("tool \"{path}\"", "{path}", "a\" & calc & \"");
            Check("H4", "{path} inside quotes stays 'R&D' (was 'R^&D'); outside quotes it is caret-escaped",
                quoted == "tool \"C:\\R&D\\a.md\"" && bare == "tool C:\\R^&D\\a.md", quoted + " | " + bare);
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
        await Case("H5", async () =>
        {
            var hook = new HookDefinition { Phase = "PreToolUse", Command = "echo " + new string('x', 9000), TimeoutMs = 10000 };
            var r = await hooks.RunHookAsync(hook, new HookSessionContext { ToolCall = writeCall }, CancellationToken.None);
            var block = new HookDefinition { Phase = "PreToolUse", Command = "exit 1", TimeoutMs = 10000 };
            var b = await hooks.RunHookAsync(block, new HookSessionContext { ToolCall = writeCall }, CancellationToken.None);
            Check("H5", "a hook that cannot even be launched (9,000-char line) lets the tool through, says so",
                r.Success && r.Message.Contains("not blocking"), r.Message);
            Check("H5b", "…while a hook that deliberately exits 1 still blocks", !b.Success, b.Message);
        });

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
