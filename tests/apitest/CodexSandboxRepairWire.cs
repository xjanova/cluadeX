// A .git that Codex's sandbox account owns stops the sandbox from starting in that folder (XSurLab chat 1e9d333d,
// 2026-11-09): `git init` ran inside the sandbox, then every command, edit and node_repl call failed with
// "helper_unknown_error: setup refresh had errors" until the folder was fixed by hand. CodexSandboxRepair rebuilds such a
// .git as the user's, before a turn and as soon as a failed row shows it. The live part runs the real `codex sandbox`.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class CodexSandboxRepairWire
{
    static string J(object o) => JsonSerializer.Serialize(o);
    static string Item(string kind, object item) => J(new { type = "item." + kind, item });

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline + live · Codex sandbox — a .git owned by the sandbox account is rebuilt as the user's");

        check("cx-git", "sandbox accounts are recognised, the user is not",
            CodexSandboxRepair.IsSandboxAccount(@"DESKTOP-2797V5R\CodexSandboxOnline") && CodexSandboxRepair.IsSandboxAccount("CodexSandboxOffline")
            && !CodexSandboxRepair.IsSandboxAccount(@"DESKTOP-2797V5R\xman") && !CodexSandboxRepair.IsSandboxAccount(null)
            && !CodexSandboxRepair.IsSandboxAccount(@"PC\CodexUser"), "");
        const string realFailure = "Failed to create unified exec process: helper_unknown_error: setup refresh had errors";
        check("cx-git", "the real failure text is a sandbox start-up failure; a build error is not",
            CodexSandboxRepair.LooksLikeSetupFailure(realFailure) && !CodexSandboxRepair.LooksLikeSetupFailure("error CS1002: ; expected")
            && !CodexSandboxRepair.LooksLikeSetupFailure(null), "");

        // ─── the translator asks for the repair on a failed row, and shows its note ───
        var calls = new List<bool>();
        var tr = new CodexStreamTranslator(@"C:\work\demo", afterFailure: setup => { calls.Add(setup); return setup ? "REPAIRED-NOTE" : "EDIT-NOTE"; });
        var turn = new ClaudeDevStreamTurn(@"C:\work\demo");
        var rows = new List<ToolResult>();
        turn.ToolExecuted += rows.Add;
        void Feed(string line) { foreach (var c in tr.Translate(line)) turn.Feed(c); }
        Feed(Item("completed", new { id = "c1", type = "command_execution", command = "Get-ChildItem", aggregated_output = realFailure, exit_code = -1, status = "failed" }));
        Feed(Item("completed", new { id = "c2", type = "command_execution", command = "dir", aggregated_output = "ok", exit_code = 0, status = "completed" }));
        Feed(Item("started", new { id = "f1", type = "file_change", changes = new[] { new { path = @"C:\work\demo\a.txt", kind = "add" } }, status = "in_progress" }));
        Feed(Item("completed", new { id = "f1", type = "file_change", changes = new[] { new { path = @"C:\work\demo\a.txt", kind = "add" } }, status = "failed" }));
        Feed(Item("completed", new
        {
            id = "m1", type = "mcp_tool_call", server = "node_repl", tool = "js", arguments = new { },
            error = new { message = "node_repl kernel exited unexpectedly: windows sandbox failed: helper_unknown_error: setup refresh had errors" }, status = "failed",
        }));
        check("cx-git", "a failed command with the setup failure → repair asked (setup=true), its note on the row",
            rows.Count >= 1 && !rows[0].Success && rows[0].Error.Contains("REPAIRED-NOTE") && calls.FirstOrDefault(), rows.FirstOrDefault()?.Error ?? "(no row)");
        check("cx-git", "a successful command asks nothing", rows.Count >= 2 && rows[1].Success && (rows[1].Error ?? "").Length == 0, "");
        check("cx-git", "a failed edit (no reason given) → the owner is checked (setup=false), note on the row",
            rows.Count >= 3 && !rows[2].Success && rows[2].Error.Contains("The change was not applied") && rows[2].Error.Contains("EDIT-NOTE"),
            rows.ElementAtOrDefault(2)?.Error ?? "(no row)");
        check("cx-git", "a node_repl call that died on the sandbox → repair asked, note on the row",
            rows.Count >= 4 && rows[3].Error.Contains("REPAIRED-NOTE") && calls.Count == 3, $"rows={rows.Count} calls={calls.Count}");
        var throwing = new CodexStreamTranslator(@"C:\work\demo", afterFailure: _ => throw new InvalidOperationException("boom"));
        bool survived = true;
        try { throwing.Translate(Item("completed", new { id = "c9", type = "command_execution", command = "x", aggregated_output = realFailure, exit_code = 1, status = "failed" })); }
        catch { survived = false; }
        check("cx-git", "a repair that throws never breaks the stream", survived, "");
        check("cx-git", "CluadeX's notice is an assistant text line", new CodexStreamTranslator("C:\\x").Notice("hi").Single().Contains("\"text\":\"hi\""), "");

        string prompt = OpenAIDevProvider.BuildPrompt(@"C:\work\demo", new ClaudeDevProvider.WorkerGrant(true, true), null, "", "go", chatTools: true, hasRipgrep: true);
        check("cx-git", "the worker prompt: retry once after 'setup refresh had errors', then stop and tell the user",
            prompt.Contains("setup refresh had errors") && prompt.Contains("run the command once more") && prompt.Contains("stop and tell the user"), "");

        // ─── the rebuild itself, on an ordinary folder that the harness calls sandbox-owned ───
        string tmp = Path.Combine(Path.GetTempPath(), "cx-gitown-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string git = Path.Combine(tmp, ".git");
            Directory.CreateDirectory(Path.Combine(git, "objects", "ab"));
            Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllBytes(Path.Combine(git, "objects", "ab", "cdef"), new byte[] { 1, 2, 3, 4, 5 });
            File.SetAttributes(Path.Combine(git, "objects", "ab", "cdef"), FileAttributes.ReadOnly);
            File.SetAttributes(git, FileAttributes.Directory | FileAttributes.Hidden);
            string? Fake(string p) => p.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? @"PC\CodexSandboxOnline" : @"PC\xman";
            check("cx-git", "a sandbox-owned .git is found; a user-owned one is not",
                CodexSandboxRepair.SandboxOwned(new[] { tmp }, Fake).SequenceEqual(new[] { git })
                && CodexSandboxRepair.SandboxOwned(new[] { tmp }, _ => @"PC\xman").Count == 0
                && CodexSandboxRepair.SandboxOwned(new string?[] { null, Path.Combine(tmp, "nope") }, Fake).Count == 0, "");
            var outs = CodexSandboxRepair.RepairRoots(new[] { tmp }, Fake);
            var siblings = Directory.GetFileSystemEntries(tmp).Select(Path.GetFileName).ToList();
            check("cx-git", "rebuilt: same files and bytes, read-only and hidden kept, nothing left beside it",
                outs.Count == 1 && outs[0].Repaired && outs[0].Error == null
                && File.ReadAllText(Path.Combine(git, "HEAD")) == "ref: refs/heads/main\n"
                && File.ReadAllBytes(Path.Combine(git, "objects", "ab", "cdef")).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 })
                && File.GetAttributes(Path.Combine(git, "objects", "ab", "cdef")).HasFlag(FileAttributes.ReadOnly)
                && new DirectoryInfo(git).Attributes.HasFlag(FileAttributes.Hidden)
                && Directory.Exists(Path.Combine(git, "refs", "heads"))
                && siblings.SequenceEqual(new[] { ".git" }),
                $"{outs.FirstOrDefault()} · beside: {string.Join(",", siblings)}");

            string? note = CodexSandboxRepair.NoteAfterFailure(new[] { tmp }, setupFailed: true, ownerOf: _ => @"PC\xman");
            check("cx-git", "a setup failure with nothing sandbox-owned → points at Codex's sandbox log", note?.Contains(".sandbox") == true, note ?? "");
            check("cx-git", "an ordinary failed edit with nothing sandbox-owned → no note",
                CodexSandboxRepair.NoteAfterFailure(new[] { tmp }, setupFailed: false, ownerOf: _ => @"PC\xman") == null, "");

            // A link inside is never followed: the repair refuses and the original stays.
            string target = Path.Combine(tmp, "elsewhere");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(git, "linked")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            mk.WaitForExit();
            var refused = CodexSandboxRepair.Rebuild(git);
            check("cx-git", "a .git with a junction inside: refused, untouched, the link's target untouched",
                !refused.Repaired && refused.Error?.Contains("link") == true && File.Exists(Path.Combine(git, "HEAD"))
                && File.ReadAllText(Path.Combine(target, "keep.txt")) == "keep"
                && Directory.GetFileSystemEntries(tmp).Length == 2, $"{refused} · exit {mk.ExitCode}");
            try { Directory.Delete(Path.Combine(git, "linked"), false); } catch { }
        }
        finally { ForceDelete(tmp); }

        // ─── live: the real sandbox, the real failure, the real repair ───
        string? codex = FindCodex();
        if (codex == null)
        {
            Console.WriteLine("  SKIP live sandbox — no codex.exe under %LOCALAPPDATA%\\OpenAI\\Codex\\bin");
            return;
        }
        string live = Path.Combine(Path.GetTempPath(), "cx-gitlive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(live);
        try
        {
            var init = await Sandboxed(codex, live, "git", "init", "-q");
            string? owner = CodexSandboxRepair.OwnerOf(Path.Combine(live, ".git"));
            check("cx-git-live", "git init inside the sandbox makes a .git the sandbox account owns", CodexSandboxRepair.IsSandboxAccount(owner),
                $"exit {init.Exit} · owner {owner} · {Cap(init.Output)}");
            var broken = await Sandboxed(codex, live, "cmd", "/c", "echo ok> probe.txt");
            check("cx-git-live", "…after which the sandbox cannot start there (setup refresh had errors)",
                broken.Exit != 0 && !File.Exists(Path.Combine(live, "probe.txt")) && CodexSandboxRepair.LooksLikeSetupFailure(broken.Output),
                $"exit {broken.Exit} · {Cap(broken.Output)}");
            var fixedOut = CodexSandboxRepair.RepairRoots(new[] { live });
            string? after = CodexSandboxRepair.OwnerOf(Path.Combine(live, ".git"));
            check("cx-git-live", "the repair rebuilds it as the user's", fixedOut.Count == 1 && fixedOut[0].Repaired && !CodexSandboxRepair.IsSandboxAccount(after)
                && Directory.GetFileSystemEntries(live).Select(Path.GetFileName).SequenceEqual(new[] { ".git" }),
                $"{string.Join(";", fixedOut)} · owner {after}");
            var works = await Sandboxed(codex, live, "cmd", "/c", "echo ok> probe.txt");
            check("cx-git-live", "…and the same command runs in the sandbox again", works.Exit == 0 && File.Exists(Path.Combine(live, "probe.txt")),
                $"exit {works.Exit} · {Cap(works.Output)}");
            var status = await Run("git", live, "-C", live, "status", "--porcelain");
            check("cx-git-live", "plain git as the user takes the repository (no 'dubious ownership')",
                status.Exit == 0 && !status.Output.Contains("dubious", StringComparison.OrdinalIgnoreCase), Cap(status.Output));
        }
        finally { ForceDelete(live); }
    }

    static string Cap(string s) => s.Length > 300 ? s[..300] + "…" : s.Trim();

    static string? FindCodex()
    {
        string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(bin)) return null;
        return new DirectoryInfo(bin).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => Path.Combine(d.FullName, "codex.exe")).FirstOrDefault(File.Exists);
    }

    static Task<(int Exit, string Output)> Sandboxed(string codex, string cwd, params string[] command) =>
        Run(codex, cwd, new[] { "sandbox", "-c", "sandbox_mode=\"workspace-write\"", "--" }.Concat(command).ToArray());

    static async Task<(int Exit, string Output)> Run(string exe, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-999, "timed out"); }
        return (p.ExitCode, (await o) + (await e));
    }

    static void ForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                try { f.Attributes = FileAttributes.Normal; } catch { }
            Directory.Delete(dir, true);
        }
        catch { }
    }
}
