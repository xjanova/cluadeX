using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
        try
        {
            Protected();
            Scanner();
            Deletes(work);
            Hook(work);
            HookProcess();
            ClaudeDevArgs();
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
        string exe = Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");
        if (!File.Exists(exe)) exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\bin\DebugBuild\net8.0-windows\CluadeX.exe"));
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
    }

    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
