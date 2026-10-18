using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Safety;

namespace SafetyTest;

/// <summary>
/// The 2026-10-17 safety layer, through the REAL built CluadeX.dll / CluadeX.exe.
///
/// THIS HARNESS NEVER RUNS A DANGEROUS COMMAND. It asks the guards for their DECISION only (ProtectedPaths.WhyProtected,
/// SafeDelete.Check, DestructiveCodeScan.JudgeLine, GuardHook.Decide, CluadeX.exe --cx-guard which only parses). The one
/// real delete it makes is a folder it created itself under %TEMP%. The dangerous inputs are assembled at run time from
/// pieces, so this source file holds no runnable dangerous line — the scanner it tests would refuse it otherwise.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {name}"); return; }
        Fail++; Console.WriteLine($"  FAIL {name}");
        if (detail.Length > 0) Console.WriteLine("       " + detail);
    }

    // Pieces, joined only in memory.
    static readonly string Root = "C" + ":" + "\\";
    static readonly string RdSQ = "rd" + " /s /q ";
    static readonly string RmRf = "rm" + " -rf ";

    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string work = Path.Combine(Path.GetTempPath(), "cx-safetytest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        // A private approval pipe, inherited by the CluadeX.exe --cx-guard children below: the owner's running CluadeX
        // must never get a red prompt from a test. The stand-in "user" says yes only to actions containing APPROVE.
        Environment.SetEnvironmentVariable("CLUADEX_DANGER_PIPE", "cx-safetytest-" + Guid.NewGuid().ToString("N"));
        DangerApproval.Handler = req => Task.FromResult(req.Action.Contains("APPROVE"));
        DangerApprovalPipe.StartServer();
        try
        {
            Protected();
            Scanner();
            Deletes(work);
            Hook(work);
            HookProcess();
            ClaudeDevArgs();
            ChatTools(work);
            RunGates(work);
            DangerGate();
            Cleanup(work);
        }
        catch (Exception ex) { Fail++; Console.WriteLine("FATAL " + ex); }
        finally { try { SafeDelete.Directory(work, work); } catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); } }
        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        return Fail == 0 ? 0 : 1;
    }

    static void Protected()
    {
        Console.WriteLine("ProtectedPaths (decisions only)");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (label, path) in new[]
        {
            ("drive root C", Root), ("drive root D", "D" + ":\\"), ("Users folder", Path.GetDirectoryName(profile)!),
            ("the profile", profile), ("AppData", Path.Combine(profile, "AppData")), ("AppData\\Local", Path.Combine(profile, "AppData", "Local")),
            ("Desktop", Path.Combine(profile, "Desktop")), (".ssh", Path.Combine(profile, ".ssh")), ("Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), ("%TEMP% itself", Path.GetTempPath()),
            ("the app's folder", AppContext.BaseDirectory), ("profile via ..", Path.Combine(profile, "AppData", "..")),
        })
            Check($"protected: {label}", ProtectedPaths.WhyProtected(path) != null, path);

        Check("not protected: a folder inside %TEMP%", ProtectedPaths.WhyProtected(Path.Combine(Path.GetTempPath(), "cx-x", "y")) == null);
        Check("not protected: a project sub-folder", ProtectedPaths.WhyProtected(@"E:\Code\SomeProject\dist") == null);
        Check("SafeDelete.Check: outside the owned folder is refused",
            SafeDelete.Check(Path.Combine(Path.GetTempPath(), "a", "b"), Path.Combine(Path.GetTempPath(), "c")) != null);
        Check("SafeDelete.Check: owned root that is protected is refused", SafeDelete.Check(Path.Combine(Path.GetTempPath(), "x"), Path.GetTempPath()) != null);
        Check("SafeDelete.Check: drive root refused even when 'owned'", SafeDelete.Check(Root, Root) != null);
    }

    static void Scanner()
    {
        Console.WriteLine("DestructiveCodeScan (strings only, nothing runs)");
        string q = "\"";
        foreach (var (label, line) in new[]
        {
            ("the 2026-10-17 test line", $"var r = await Launch({q}cmd.exe{q}, {q}/c {RdSQ}{Root.Replace("\\", "\\\\")}{q});"),
            ("rd on a drive root", $"{RdSQ}{Root}"),
            ("rd on C: without slash", $"{RdSQ}C:"),
            ("rm -rf /", $"{RmRf}/"),
            ("rm -rf ~", $"{RmRf}~"),
            ("rm -rf ~/.ssh", $"{RmRf}~/.ssh"),
            ("rm -rf $HOME", $"{RmRf}$HOME"),
            ("Remove-Item USERPROFILE", "Remove-Item -Recurse -Force $env:USERPROFILE"),
            ("del /s on a profile", "del /s /q " + Root + "Users\\xman\\"),
            // A profile's vital folders spelled out, not only by variable (after 374b331 these ran unasked)
            ("rd on a spelled-out AppData", RdSQ + Root + "Users\\xman\\AppData"),
            ("rd on a spelled-out AppData\\Roaming", RdSQ + Root + "Users\\xman\\AppData\\Roaming\\"),
            ("rd on a spelled-out .cluadex", RdSQ + Root + "Users\\xman\\.cluadex"),
            ("Remove-Item on a spelled-out Desktop", "Remove-Item -Recurse -Force \"" + Root + "Users\\xman\\Desktop\""),
            ("rm -rf on a Git Bash .cluadex", RmRf + "/c/Users/xman/.cluadex"),
            ("rm -rf ~/AppData/Roaming", RmRf + "~/AppData/Roaming"),
            ("Directory.Delete(home)", "Directory.Delete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), true);"),
            ("shutil.rmtree(~)", "shutil.rmtree(os.path.expanduser('~'))"),
            ("fs.rmSync(homedir)", "fs.rmSync(os.homedir(), { recursive: true })"),
            ("format a drive", "format " + "D:" + " /q"),
            ("diskpart", "diskpart /s wipe.txt"),
            ("cipher /w", "cipher /w:" + Root),
        })
            Check($"flags: {label}", DestructiveCodeScan.JudgeLine(line) != null, line);

        foreach (var (label, line) in new[]
        {
            ("rm -rf dist", RmRf + "dist"),
            ("rm -rf ./build/*", RmRf + "./build/*"),
            ("rm -rf ~/tmp/cache", RmRf + "~/tmp/cache"),
            ("rd in a spelled-out AppData\\Local\\Temp", RdSQ + Root + "Users\\xman\\AppData\\Local\\Temp\\cx-x"),
            ("rm -rf a folder inside ~/.cluadex", RmRf + "~/.cluadex/projects/cx-x"),
            ("Directory.Delete(tempDir)", "Directory.Delete(tempDir, true);"),
            ("rmdir on a project folder", "rmdir /s /q node_modules"),
            ("the recommended inert sentinel", "echo CX-SENTINEL " + RdSQ + "Z:\\cx-sentinel-nonexistent"),
            ("a URL", "fetch('https://example.com/rm -rf')"),
            ("CSS", ".a { color: red; } /* format */"),
        })
            Check($"passes: {label}", DestructiveCodeScan.JudgeLine(line) == null, line + " → " + DestructiveCodeScan.JudgeLine(line));

        Check("runs code: dotnet run", DestructiveCodeScan.RunsProjectCode("dotnet run --project tests/x"));
        Check("runs code: npm test", DestructiveCodeScan.RunsProjectCode("npm test"));
        Check("runs code: python script", DestructiveCodeScan.RunsProjectCode("python tools/fix.py"));
        Check("runs code: a .bat", DestructiveCodeScan.RunsProjectCode("build.bat"));
        Check("does not run code: git status", !DestructiveCodeScan.RunsProjectCode("git status"));
        Check("does not run code: ls", !DestructiveCodeScan.RunsProjectCode("ls -la src"));
    }

    static void Deletes(string work)
    {
        Console.WriteLine("SafeDelete (one real delete: a folder this test made)");
        string owned = Path.Combine(work, "owned");
        string victim = Path.Combine(work, "victim");
        Directory.CreateDirectory(Path.Combine(owned, "a", "b"));
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(owned, "a", "b", "f.txt"), "x");
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        File.SetAttributes(Path.Combine(owned, "a", "b", "f.txt"), FileAttributes.ReadOnly);
        // A junction inside the tree pointing OUT of it (created with mklink /J — creating, not deleting).
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(owned, "link")}\" \"{victim}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        mk!.WaitForExit();
        bool junction = Directory.Exists(Path.Combine(owned, "link"));

        bool refused = false;
        try { SafeDelete.Directory(owned, Path.Combine(work, "other")); } catch (UnsafeDeleteException) { refused = true; }
        Check("refuses a folder outside the owned root (nothing deleted)", refused && Directory.Exists(owned));

        SafeDelete.Directory(owned, work);
        Check("deletes its own tree (read-only file included)", !Directory.Exists(owned));
        Check("a junction inside is unlinked, its target untouched", !junction || File.Exists(Path.Combine(victim, "keep.txt")), $"junction={junction}");
    }

    static string HookJson(string tool, object input, string cwd) =>
        JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = tool, tool_input = input, cwd });

    static void Hook(string work)
    {
        Console.WriteLine("GuardHook.Decide (decisions only)");
        string repo = Path.Combine(work, "proj");
        Directory.CreateDirectory(Path.Combine(repo, "tests"));
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "app.cs"), "class A { }\n");
        Check("Bash: a plain command is allowed", GuardHook.Decide(HookJson("Bash", new { command = "git status" }, repo)) == null);
        Check("Bash: rd on a drive root is refused", GuardHook.Decide(HookJson("Bash", new { command = RdSQ + Root }, repo)) != null);
        Check("Bash: rm -rf ~ is refused", GuardHook.Decide(HookJson("Bash", new { command = RmRf + "~" }, repo)) != null);
        Check("Bash: dotnet run on clean code is allowed", GuardHook.Decide(HookJson("Bash", new { command = "dotnet run" }, repo)) == null);

        string bad = "var r = Launch(\"cmd.exe\", \"/c " + RdSQ + Root.Replace("\\", "\\\\") + "\");";
        Check("Write: putting the incident line in a file is refused",
            GuardHook.Decide(HookJson("Write", new { file_path = Path.Combine(repo, "tests", "t.cs"), content = "// test\n" + bad + "\n" }, repo)) is { } w && w.Contains("refused to write"));
        Check("Edit: same through an edit", GuardHook.Decide(HookJson("Edit", new { file_path = Path.Combine(repo, "app.cs"), old_string = "x", new_string = bad }, repo)) != null);
        Check("Write: an ordinary file is allowed", GuardHook.Decide(HookJson("Write", new { file_path = Path.Combine(repo, "b.cs"), content = "class B { }" }, repo)) == null);

        // The code is on disk already (written by hand / another tool): running it is refused until it is gone.
        File.WriteAllText(Path.Combine(repo, "tests", "t.cs"), bad + "\n");
        string? run = GuardHook.Decide(HookJson("Bash", new { command = "dotnet run --project tests" }, repo));
        Check("Bash: running code that holds the line is refused, naming file:line", run != null && run.Contains("tests/t.cs:1"), run ?? "");
        Check("Bash: a command that runs nothing is still allowed", GuardHook.Decide(HookJson("Bash", new { command = "git diff" }, repo)) == null);
        Check("CodeRunGuard says the same for CluadeX's own run_tests", CodeRunGuard.Check("dotnet test", repo) != null);
        File.WriteAllText(Path.Combine(repo, "tests", "t.cs"), "// fixed\n");
        Check("…and allows it once the line is gone", GuardHook.Decide(HookJson("Bash", new { command = "dotnet run --project tests" }, repo)) == null);
        Check("CodeRunGuard.CheckCode refuses a code block that would wipe home", CodeRunGuard.CheckCode("import shutil, os\nshutil.rmtree(os.path.expanduser('~'))") != null);
    }

    static void HookProcess()
    {
        Console.WriteLine("CluadeX.exe --cx-guard (the real hook process; it only parses)");
        // A CluadeX.exe in this harness's own bin is runnable only with its runtimeconfig beside it: a stray copy without
        // one (left by a build pointed at another CluadeXBin) starts as "self-contained" and fails before Main.
        string exe = Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "CluadeX.runtimeconfig.json"))) exe =Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\bin\DebugBuild\net8.0-windows\CluadeX.exe"));
        if (!File.Exists(exe)) { Check("CluadeX.exe found", false, exe); return; }
        (int Code, string Err) Ask(string json)
        {
            var psi = new ProcessStartInfo(exe, GuardHook.Flag) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(json);
            p.StandardInput.Close();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); return (-1, "timeout"); }
            return (p.ExitCode, err);
        }
        var sw = Stopwatch.StartNew();
        var ok = Ask(HookJson("Bash", new { command = "echo hi" }, Path.GetTempPath()));
        long ms = sw.ElapsedMilliseconds;
        Check($"allows a harmless command (exit 0, {ms} ms, no window)", ok.Code == 0, $"{ok.Code} {ok.Err}");
        var no = Ask(HookJson("Bash", new { command = RdSQ + Root }, Path.GetTempPath()));
        Check("blocks rd on a drive root (exit 2 + reason on stderr)", no.Code == 2 && no.Err.Contains("CluadeX safety"), $"{no.Code} {no.Err}");
        var broken = Ask("{not json");
        Check("fails closed on a broken payload (exit 2)", broken.Code == 2, $"{broken.Code}");
        var yes = Ask(HookJson("Bash", new { command = "echo APPROVE && " + RdSQ + Root }, Path.GetTempPath()));
        Check("the same delete runs once the user says yes on the red prompt (exit 0, via the app's pipe)", yes.Code == 0, $"{yes.Code} {yes.Err}");
    }

    static void ClaudeDevArgs()
    {
        Console.WriteLine("Claude Dev carries the guard");
        // The app's layout: CluadeX.exe beside CluadeX.dll (this harness only gets the dll).
        string real = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\bin\DebugBuild\net8.0-windows\CluadeX.exe"));
        string beside = Path.Combine(Path.GetDirectoryName(typeof(GuardHook).Assembly.Location)!, "CluadeX.exe");
        if (File.Exists(real) && !File.Exists(beside)) File.Copy(real, beside);
        var grant = new CluadeX.Services.Providers.ClaudeDevProvider.WorkerGrant(Edit: true, Run: true);
        string args = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(@"E:\Code\X", grant, "default", "auto");
        Check("a worker run gets --settings", args.Contains("--settings"), args);
        int at = args.IndexOf("--settings ", StringComparison.Ordinal) + "--settings ".Length;
        string file = args[at..].TrimStart('"');
        file = file[..file.IndexOf(".json", StringComparison.OrdinalIgnoreCase)] + ".json";
        string settings = File.ReadAllText(file);
        Check("the hook calls CluadeX.exe, not the calling process", settings.Contains("CluadeX.exe") && !settings.Contains("safetytest.exe"), settings);
        Check("the settings hold the PreToolUse hook on Bash/Write/Edit with --cx-guard",
            settings.Contains("PreToolUse") && settings.Contains("Bash|PowerShell|Write|Edit") && settings.Contains("--cx-guard"), settings);
        string advice = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(@"E:\Code\X", null, "default", "auto");
        Check("a read-only advice run has no shell and needs no hook", !advice.Contains(",Bash"));
        // Owner 2026-10-18: Claude Dev fans out by itself (its sub-agents share the hook), and `rm -rf build` just runs —
        // a root / home / system delete goes to the red prompt through the hook instead of a blanket deny.
        Check("a worker run may start sub-agents (Task / Agent)", args.Contains("Task,Agent"), args);
        Check("a read-only advice run starts no sub-agents", !advice.Contains("Task,Agent"), advice);
        Check("recursive deletes are not denied wholesale (the hook asks for the dangerous ones)", !args.Contains("rm -rf") && !args.Contains("Remove-Item -Recurse"), args);
        Check("rewriting git history stays denied", args.Contains("git push --force") && args.Contains("git reset --hard"), args);
    }

    /// <summary>Owner 2026-10-18: Claude Dev opens / splits / closes chats through CluadeX (an MCP server it starts:
    /// CluadeX.exe --cx-tools), works in the folders the user allowed (--add-dir), and has the brain's agent tools.</summary>
    static void ChatTools(string work)
    {
        Console.WriteLine("Claude Dev: CluadeX chat tools, allowed folders, brain agent tools");
        var grant = new CluadeX.Services.Providers.ClaudeDevProvider.WorkerGrant(Edit: true, Run: true);
        string a1 = Path.Combine(work, "libA"), a2 = Path.Combine(work, "libB"), proj = Path.Combine(work, "proj");
        foreach (var d in new[] { a1, a2, proj, Path.Combine(proj, "sub"), Path.Combine(a1, "inner") }) Directory.CreateDirectory(d);

        // ── which folders go to --add-dir ──
        var dirs = CluadeX.Services.Providers.ClaudeDevProvider.ExtraDirs(proj,
            new[] { Path.Combine(a1, "inner"), a1, a1.ToUpperInvariant(), Path.Combine(proj, "sub"), Path.Combine(work, "missing"), a2 + "\\", Root, null, "" },
            Directory.Exists, p => PermissionService.IsUnsafeAcceptEditsRoot(p));
        Check("add-dir: a parent replaces its child, case-insensitive duplicates once, a trailing slash trimmed",
            dirs.Count == 2 && dirs[0].Equals(a1, StringComparison.OrdinalIgnoreCase) && dirs[1] == a2, string.Join(" | ", dirs));
        Check("add-dir: nothing inside the project, nothing missing, never a drive root",
            !dirs.Any(d => d.StartsWith(proj, StringComparison.OrdinalIgnoreCase)) && !dirs.Any(d => d.Contains("missing")) && !dirs.Contains(Root.TrimEnd('\\')), string.Join(" | ", dirs));
        var many = CluadeX.Services.Providers.ClaudeDevProvider.ExtraDirs(proj, Enumerable.Range(0, 40).Select(_ => a1).Concat(new[] { a2 }), _ => true, _ => false);
        Check("add-dir: the same folder forty times is one", many.Count == 2, string.Join(" | ", many));

        // ── the CLI's arguments ──
        string plain = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(proj, grant, "default", "auto");
        string advice = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(proj, null, "default", "auto");
        foreach (var t in new[] { "task_queue", "agent_inbox", "task_handoff", "cowork_join", "cowork_say", "cowork_read", "cowork_task" })
            Check($"brain agent tool {t} allowed (worker and advice turns)",
                plain.Contains("mcp__brainx-brain__" + t) && advice.Contains("mcp__brainx-brain__" + t));
        Check("agent_send (a DM to any peer) is not opened", !plain.Contains("agent_send"), plain);
        Check("no chat tools and no --add-dir unless the turn has them", !plain.Contains("--mcp-config") && !plain.Contains("--add-dir") && !plain.Contains("mcp__cluadex__"), plain);
        string cfg = Path.Combine(work, "tools.json");
        string full = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(proj, grant, "default", "auto", cfg, dirs);
        Check("chat tools: --mcp-config with the turn's file, and new_chat / open_project / close_chat allowed",
            full.Contains("--mcp-config \"" + cfg + "\"") && full.Contains("mcp__cluadex__new_chat") && full.Contains("mcp__cluadex__open_project") && full.Contains("mcp__cluadex__close_chat"), full);
        Check("one --add-dir per allowed folder, each quoted", full.Contains("--add-dir \"" + dirs[0] + "\"") && full.Contains("--add-dir \"" + dirs[1] + "\""), full);
        Check("the wrapper names the allowed folders and says never to answer it cannot open a chat",
            full.Contains(dirs[1]) && full.Contains("Never answer that you cannot open a chat"), full);
        Check("the guard hook stays on a turn with chat tools", full.Contains("--settings") && full.Contains("git push --force"), full);
        string adviceTools = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(proj, null, "default", "auto", cfg, dirs);
        Check("an advice turn gets the chat tools but still no shell or edits", adviceTools.Contains("mcp__cluadex__new_chat") && !adviceTools.Contains(",Bash") && !adviceTools.Contains("Edit,Write"), adviceTools);

        // ── the protocol, without a pipe ──
        var noRelay = new Func<string, string, JsonElement, (bool, string)>((_, _, _) => (true, "relayed"));
        Check("a notification is never answered", CluadeX.Services.Tools.ChatToolBridge.Respond("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", "t", noRelay) == null);
        string? init = CluadeX.Services.Tools.ChatToolBridge.Respond("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}", "t", noRelay);
        Check("initialize echoes the client's protocol version and the id", init != null && init.Contains("\"2025-06-18\"") && init.Contains("\"id\":1") && init.Contains("tools"), init ?? "");
        string? list = CluadeX.Services.Tools.ChatToolBridge.Respond("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"tools/list\"}", "t", noRelay);
        Check("tools/list: exactly new_chat, open_project, close_chat", list != null && list.Contains("\"new_chat\"") && list.Contains("\"open_project\"") && list.Contains("\"close_chat\"") && list.Contains("\"id\":\"x\""), list ?? "");
        string? noTok = CluadeX.Services.Tools.ChatToolBridge.Respond("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"new_chat\"}}", "", noRelay);
        Check("without a turn token nothing is relayed", noTok != null && noTok.Contains("\"isError\":true") && !noTok.Contains("relayed"), noTok ?? "");
        string? unknown = CluadeX.Services.Tools.ChatToolBridge.Respond("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/list\"}", "t", noRelay);
        Check("an unknown method is an error, not a crash", unknown != null && unknown.Contains("-32601"), unknown ?? "");

        // ── end to end: the real CluadeX.exe --cx-tools, a private pipe, a stand-in chat page ──
        Environment.SetEnvironmentVariable("CLUADEX_TOOLS_PIPE", "cx-safetytest-tools-" + Guid.NewGuid().ToString("N"));
        CluadeX.Services.Tools.ChatToolBridge.StartServer();
        var scope = new AgentRunScope("safetytest-chat", proj);
        var seen = new List<string>();
        AgentRunScope? scopeSeen = null;
        using var turnCts = new CancellationTokenSource();
        var ticket = CluadeX.Services.Tools.ChatToolBridge.Register(scope, (call, ct) =>
        {
            lock (seen) { seen.Add(call.ToolName + ":" + call.GetArg("path") + ":" + call.GetArg("message")); scopeSeen = AgentRunScope.Current; }
            return Task.FromResult(new CluadeX.Models.ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = "opened chat #7" });
        }, proj, turnCts.Token);
        Check("a turn gets a ticket with its config file", ticket != null && File.Exists(ticket.ConfigFile), ticket?.ConfigFile ?? "null");
        if (ticket == null) return;
        string cfgText = File.ReadAllText(ticket.ConfigFile);
        Check("the config starts CluadeX.exe --cx-tools with the turn's token", cfgText.Contains("CluadeX.exe") && cfgText.Contains("--cx-tools") && cfgText.Contains(ticket.Token), cfgText);

        string exe = Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "CluadeX.runtimeconfig.json")))
            exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\bin\DebugBuild\net8.0-windows\CluadeX.exe"));
        List<string> Talk(string token, params string[] lines)
        {
            var psi = new ProcessStartInfo(exe, CluadeX.Services.Tools.ChatToolBridge.Flag)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.Environment["CLUADEX_TOOLS_TOKEN"] = token;
            using var p = Process.Start(psi)!;
            using (var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
                foreach (var l in lines) w.Write(l + "\n");
            string outText = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) p.Kill(true);
            return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        string newChat = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "new_chat", arguments = new { path = a1, message = "สวัสดี ทำงานแยก" } } });
        string bad = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "run_command", arguments = new { command = "echo hi" } } });
        var sw = Stopwatch.StartNew();
        var replies = Talk(ticket.Token,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", newChat, bad);
        string Reply(int id) => replies.FirstOrDefault(r => r.Contains("\"id\":" + id + ",") || r.Contains("\"id\":" + id + "}")) ?? "";
        Check($"the process answers every request and no notification ({sw.ElapsedMilliseconds} ms)", replies.Count == 4, string.Join("\n", replies));
        Check("new_chat reached the chat page, inside the asking chat's run, Thai intact",
            Reply(3).Contains("opened chat #7") && Reply(3).Contains("\"isError\":false") && seen.Contains("new_chat:" + a1 + ":สวัสดี ทำงานแยก") && ReferenceEquals(scopeSeen, scope),
            Reply(3) + " | " + string.Join(",", seen));
        Check("any other tool is refused by the app (only the three chat tools pass)", Reply(4).Contains("\"isError\":true") && !seen.Any(s => s.StartsWith("run_command")), Reply(4));
        var forged = Talk("0000", newChat);
        Check("a wrong token opens nothing", forged.Count == 1 && forged[0].Contains("\"isError\":true") && seen.Count == 1, string.Join("\n", forged));
        ticket.Dispose();
        var after = Talk(ticket.Token, newChat);
        Check("after the turn ends its token opens nothing, and its config file is gone",
            after.Count == 1 && after[0].Contains("\"isError\":true") && seen.Count == 1 && !File.Exists(ticket.ConfigFile), string.Join("\n", after));
    }

    static void RunGates(string work)
    {
        Console.WriteLine("Run gates: desktop, scripts, dry-run, blanket allow (decisions only)");
        // desktop_type / set_value text goes through the same finding as run_command, then the red prompt.
        Check("desktop_type: the incident line is a finding", CodeRunGuard.CheckCode(RdSQ + Root) != null);
        Check("desktop_type: flutter analyze is not", CodeRunGuard.CheckCode("flutter analyze") == null && CodeRunGuard.Check("flutter analyze", null) == null);
        Check("desktop_type: rm -rf build is not", CodeRunGuard.CheckCode(RmRf + "build") == null);

        string bad = Path.Combine(work, "wipe.bat"), clean = Path.Combine(work, "hello.bat");
        File.WriteAllText(bad, "@echo off\r\n" + RdSQ + Root + "\r\n");
        File.WriteAllText(clean, "@echo off\r\necho hello\r\n");
        Check("desktop_launch: a script that wipes a drive is refused", CodeRunGuard.CheckScriptFile(bad) != null);
        Check("desktop_launch: a clean script is allowed", CodeRunGuard.CheckScriptFile(clean) == null);
        Check("desktop_launch: an app is not read as a script", CodeRunGuard.CheckScriptFile(Environment.ProcessPath) == null);
        Check("repl: code that wipes home is refused", CodeRunGuard.CheckCode("import shutil\nshutil.rmtree(\"" + Root.Replace("\\", "/") + "\")") != null);

        Check("dry-run is on by default in a harness", ProcessLaunchPolicy.DryRun);
        Check("dry-run blocks a program that is not allowed", ProcessLaunchPolicy.Blocks("cmd.exe"));
        ProcessLaunchPolicy.Allowed.Add("cx-allowed-child.exe");
        Check("dry-run lets the harness's own named child start", !ProcessLaunchPolicy.Blocks("C:\\x\\cx-allowed-child.exe"));
        ProcessLaunchPolicy.Allowed.Remove("cx-allowed-child.exe");
        var r = CluadeX.Services.ShellRunner.RunAsync(CluadeX.Services.ShellRunner.Shell.Cmd, "echo CX-REAL-RUN", work, 10_000, CancellationToken.None).GetAwaiter().GetResult();
        Check("dry-run: run_command's shell starts nothing", r.Output.StartsWith("[dry-run]"), r.Output);

        var blanket = new CluadeX.Models.PermissionRule { Pattern = "*", Scope = "*", Action = CluadeX.Models.PermAction.Allow };
        var rules = new[] { blanket };
        // CodeAgentService.DecideWorkerGrant gives Claude Dev its shell exactly when this rule answers Allow. On 3.0.127
        // a blanket Allow stopped covering run tools, the grant came back read-only and flutter / git / gh could not run.
        Check("Allow * covers run_command (Claude Dev keeps its shell)", CluadeX.Services.PermissionService.FirstMatch(rules, "git status", "execute", "run_command") == CluadeX.Models.PermAction.Allow);
        Check("Allow * covers repl", CluadeX.Services.PermissionService.FirstMatch(rules, "repl", "execute", "repl") == CluadeX.Models.PermAction.Allow);
        Check("Allow * covers desktop_type", CluadeX.Services.PermissionService.FirstMatch(rules, "cmd", "desktop", "desktop_type") == CluadeX.Models.PermAction.Allow);
        Check("Allow * covers reading a file", CluadeX.Services.PermissionService.FirstMatch(rules, "a.cs", "file", "read_file") == CluadeX.Models.PermAction.Allow);
        var named = new CluadeX.Models.PermissionRule { Pattern = "*", Scope = "*", Action = CluadeX.Models.PermAction.Allow, ToolName = "run_command" };
        Check("a rule naming run_command still allows it", CluadeX.Services.PermissionService.FirstMatch(new[] { named }, "git status", "execute", "run_command") == CluadeX.Models.PermAction.Allow);
        var denyAll = new CluadeX.Models.PermissionRule { Pattern = "*", Scope = "*", Action = CluadeX.Models.PermAction.Deny };
        Check("Deny * still denies run_command", CluadeX.Services.PermissionService.FirstMatch(new[] { denyAll }, "git status", "execute", "run_command") == CluadeX.Models.PermAction.Deny);
    }

    static void DangerGate()
    {
        Console.WriteLine("DangerApproval: dangerous runs need the user's explicit yes");
        string finding = CodeRunGuard.CheckCode(RdSQ + Root)!;
        var saved = DangerApproval.Handler;
        DangerApproval.Handler = null;
        Check("no UI to ask (a harness, headless) → refused", DangerApproval.GateAsync("run_command", RdSQ + Root, finding).GetAwaiter().GetResult() != null);
        DangerApproval.Handler = _ => Task.FromResult(false);
        Check("the user says no → refused", DangerApproval.GateAsync("run_command", RdSQ + Root, finding).GetAwaiter().GetResult() != null);
        DangerApproval.Handler = _ => Task.FromResult(true);
        Check("the user says yes → it may run", DangerApproval.GateAsync("run_command", RdSQ + Root, finding).GetAwaiter().GetResult() == null);
        DangerApproval.Handler = _ => throw new InvalidOperationException("prompt failed");
        Check("the prompt fails → refused", DangerApproval.GateAsync("run_command", RdSQ + Root, finding).GetAwaiter().GetResult() != null);
        int asked = 0;
        DangerApproval.Handler = _ => { asked++; return Task.FromResult(true); };
        Check("nothing dangerous → no prompt at all", DangerApproval.GateAsync("run_command", "flutter test", null).GetAwaiter().GetResult() == null && asked == 0);
        DangerApproval.Handler = saved;
        Check("pipe: an approved request comes back allow", DangerApprovalPipe.Ask("t", "APPROVE x", "r"));
        Check("pipe: a refused request comes back deny", !DangerApprovalPipe.Ask("t", "no", "r"));
        string keep = Environment.GetEnvironmentVariable("CLUADEX_DANGER_PIPE")!;
        Environment.SetEnvironmentVariable("CLUADEX_DANGER_PIPE", "cx-nobody-listens-" + Guid.NewGuid().ToString("N"));
        var sw = Stopwatch.StartNew();
        Check("pipe: no app listening → deny, quickly", !DangerApprovalPipe.Ask("t", "APPROVE x", "r") && sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
        Environment.SetEnvironmentVariable("CLUADEX_DANGER_PIPE", keep);
    }

    static void Cleanup(string work)
    {
        Console.WriteLine("Cleanup deletes run; root / home / system / everything-here deletes ask (owner, 2026-10-18)");
        string D = "C" + ":";
        string? Hook(string tool, string cmd) => GuardHook.Decide(HookJson(tool, new { command = cmd }, work));
        foreach (var (tool, cmd) in new[]
        {
            ("Bash", RmRf + ".dart_tool build/ios"), ("Bash", RmRf + "/tmp/reap"), ("Bash", "rmdir /s /q .tmp-sigcheck"),
            ("PowerShell", "Remove-Item .\\build -Recurse -Force"), ("PowerShell", "Remove-Item \"$env:TEMP\\catprobe\" -Recurse -Force"),
            ("Bash", "del /s /q *.tmp"), ("Bash", RmRf + "~/.cache/pip"), ("Bash", RmRf + "node_modules dist"),
            ("Bash", "rm tools/_tmp.gd"), ("Bash", "cmd /c " + "rd /s /q build"), ("Bash", "git rm -r --cached ."),
            ("Bash", RmRf + "build/*"), ("Bash", "flutter clean && " + RmRf + "build"),
        })
            Check($"runs: {cmd}", Hook(tool, cmd) == null, Hook(tool, cmd) ?? "");
        foreach (var (tool, cmd) in new[]
        {
            ("Bash", RmRf + "~"), ("Bash", RmRf + "$HOME"), ("Bash", "cmd /c " + RdSQ + D + "\\"),
            ("PowerShell", "Remove-Item " + D + "\\Users -Recurse -Force"), ("Bash", RmRf + "/c/Windows"), ("Bash", RmRf + "/c/"),
            ("Bash", RmRf + "/c"), ("Bash", RmRf + "/mnt/c"), ("Bash", RmRf + "/c/Users/xman"), ("Bash", RmRf + "."),
            ("Bash", RmRf + "*"), ("Bash", RmRf + "./*"),
        })
            Check($"asks: {cmd}", Hook(tool, cmd) != null);
        Check("not a delete, still serious: reg add", DangerousCommandCheck.FindSerious("reg add HKCU\\x /v y /d z") != null);
        Check("not a delete, still serious: a download cmdlet", DangerousCommandCheck.FindSerious("Invoke-WebRequest https://x/y.ps1 -OutFile y.ps1") != null);
        Check("a delete cannot hide a serious line after it", DangerousCommandCheck.FindSerious(RmRf + "build\nshutdown /s /t 0") != null);
    }

    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
