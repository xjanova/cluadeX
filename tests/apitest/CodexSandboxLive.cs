// The real Codex sandbox, run through `codex sandbox` on this machine (no model call, nothing billed): every workaround
// CodexSandboxEnv gives the shell must still work, and every limit CodexSandbox.Limits tells the model about must still be
// there. Run it after a Codex update (`apitest --codex-sandbox-live`): a workaround that broke, or a limit that went away,
// fails here with the line to change — instead of in a user's chat. The survey this pins down: 2026-11-11, Codex 0.167.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class CodexSandboxLive
{
    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("live · Codex's Windows sandbox (codex sandbox, no model call)");
        string? exe = FindCodex();
        if (exe == null) { check("cx-live", "codex.exe found", false, "no Codex CLI on this machine"); return; }
        string ws = Path.Combine(Path.GetTempPath(), "cx-sbx-live-" + Guid.NewGuid().ToString("N")[..8]);
        string home = Path.Combine(ws, "home");
        string work = Path.Combine(ws, "work");
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "cprobe"));
            File.WriteAllText(Path.Combine(work, "a.txt"), "hi\n");
            File.WriteAllText(Path.Combine(work, "cprobe", "cprobe.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(work, "cprobe", "Program.cs"), "System.Console.WriteLine(\"cprobe-ok\");");
            // The repository is made as the user, like a real project (a .git the sandbox made would break the sandbox).
            foreach (var g in new[] { "init -q .", "add a.txt", "-c user.email=t@t -c user.name=t commit -qm init" })
                await RunAsync("git", g.Split(' '), work, TimeSpan.FromSeconds(30));
            bool made = CodexSandboxEnv.Prepare(home);
            check("cx-live", "the sandbox's home is made", made, home);
            var overrides = CodexSandboxEnv.ConfigOverrides(CodexSandboxEnv.Vars(home,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            File.WriteAllText(Path.Combine(work, "probe.ps1"), Script, new UTF8Encoding(true));
            var args = new List<string> { "sandbox", "-c", "sandbox_mode=\"workspace-write\"", "-c", "sandbox_workspace_write.network_access=true" };
            foreach (var o in overrides) { args.Add("-c"); args.Add(o); }
            args.AddRange(new[] { "--", "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "probe.ps1" });
            var (code, output) = await RunAsync(exe, args, work, TimeSpan.FromMinutes(6));
            var results = Regex.Matches(output, @"^\[(?<id>[\w-]+)\] exit=(?<exit>-?\d*) :: (?<out>.*)$", RegexOptions.Multiline)
                .ToDictionary(m => m.Groups["id"].Value, m => (Exit: m.Groups["exit"].Value, Out: m.Groups["out"].Value.Trim()));
            check("cx-live", "the probe ran as the sandbox account", results.TryGetValue("whoami", out var who) && who.Out.Contains("codexsandbox", StringComparison.OrdinalIgnoreCase),
                results.Count == 0 ? $"exit {code}: {Clip(output)}" : who.Out);
            if (results.Count == 0) return;

            void Works(string id, string what, Func<string, bool>? ok = null)
            {
                var r = results.GetValueOrDefault(id);
                bool pass = r.Exit == "0" && (ok == null || ok(r.Out));
                check("cx-live-fix", what, pass, pass ? Clip(r.Out)
                    : $"exit={r.Exit} {Clip(r.Out)} — the workaround in CodexSandboxEnv no longer works; fix it or drop the sentence that promises it");
            }
            void StillBlocked(string id, string limit, string what)
            {
                var r = results.GetValueOrDefault(id);
                string key = CodexSandbox.CauseKey(r.Out) ?? "(none)";
                bool pass = r.Exit is not ("0" or "") && key == "codex.sandbox.why." + limit;
                check("cx-live-limit", what, pass, pass ? Clip(r.Out) : r.Exit == "0" ? $"it works now: Codex lifted this limit — drop or reword \"{limit}\" in CodexSandbox.Limits"
                                  : $"exit={r.Exit}, notice {key}: {Clip(r.Out)} — the fingerprint in CodexSandbox.Limits[\"{limit}\"] no longer matches");
            }

            Works("git-status", "git status in the user's repository (safe.directory)");
            Works("git-https", "git ls-remote over https (OpenSSL)", o => Regex.IsMatch(o, @"\b[0-9a-f]{40}\b"));
            Works("dotnet-build", "dotnet build of a fresh project (APPDATA → the home's NuGet.Config)", o => !o.Contains("NuGet.Config"));
            Works("dotnet-run", "dotnet run prints its line", o => o.Contains("cprobe-ok"));
            Works("node-fetch", "Node's fetch over https", o => o.Contains("ok 200"));
            Works("py", "py (the Python launcher) runs", o => o.Contains("ok"));
            Works("junction", "a directory junction can be made");

            StillBlocked("git-add", "git", ".git stays read-only (git add)");
            StillBlocked("curl", "tls", "curl.exe's schannel still fails (SEC_E_NO_CREDENTIALS)");
            if (results.GetValueOrDefault("python-store").Out is { } ps && !ps.StartsWith("SKIP"))
                StillBlocked("python-store", "python", "the Store python still cannot start");
            StillBlocked("tasklist", "wmi", "tasklist is still denied");
            StillBlocked("symlink", "symlink", "a symbolic link still needs admin");
            StillBlocked("gh", "gh", "gh still cannot read its config");
        }
        finally
        {
            try { foreach (var f in new DirectoryInfo(ws).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; } catch { }
            try { Directory.Delete(ws, true); } catch { }
        }
    }

    const string Script = """
        $ErrorActionPreference='Continue'
        function T($name,[scriptblock]$b){ $global:LASTEXITCODE=0; $ErrorActionPreference='Stop'; try { $o = & $b 2>&1 | Out-String; $e = $LASTEXITCODE } catch { $o = $_.Exception.Message; $e = 1 }; $o = ($o -replace '\s+',' ').Trim(); if($o.Length -gt 400){$o=$o.Substring(0,400)}; "[$name] exit=$e :: $o" }
        T 'whoami' { whoami }
        T 'git-status' { git status --short }
        T 'git-https' { git ls-remote https://github.com/git/git.git HEAD }
        T 'dotnet-build' { dotnet build cprobe -nologo -v q }
        T 'dotnet-run' { dotnet run --project cprobe --no-build }
        T 'node-fetch' { node -e "fetch('https://api.github.com').then(r=>console.log('ok',r.status)).catch(e=>{console.log('ERR',e.message);process.exit(1)})" }
        T 'py' { py -c "print('ok')" }
        T 'junction' { cmd /c mklink /J jdir cprobe }
        T 'git-add' { git add probe.ps1 }
        T 'curl' { C:\Windows\System32\curl.exe -sS -o NUL https://api.github.com }
        $store = (Get-Command python -ErrorAction SilentlyContinue).Source
        if ($store -like '*WindowsApps*') { T 'python-store' { python -c "print('ok')" } } else { "[python-store] exit=0 :: SKIP not the Store python: $store" }
        T 'tasklist' { tasklist /FI "IMAGENAME eq explorer.exe" /NH }
        T 'symlink' { New-Item -ItemType SymbolicLink -Path slnk -Target a.txt }
        T 'gh' { gh auth status }
        """;

    static string Clip(string s) => s.Length > 300 ? s[..300] + "…" : s;

    static string? FindCodex()
    {
        var where = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("where.exe", "codex") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p != null) { where.AddRange(p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())); p.WaitForExit(5000); }
        }
        catch { }
        return OpenAIDevProvider.FindCli(where,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists,
            d => Directory.Exists(d) ? new DirectoryInfo(d).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc).Select(x => x.FullName) : Array.Empty<string>());
    }

    static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, string cwd, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = cwd, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, await so + await se);
    }
}
