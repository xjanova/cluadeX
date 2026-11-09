// A .git that Codex's sandbox account owns stops the sandbox from starting in that folder (XSurLab chat 1e9d333d,
// 2026-11-09): `git init` ran inside the sandbox, then every command, edit and node_repl call failed with
// "helper_unknown_error: setup refresh had errors" until the folder was fixed by hand. CodexSandboxRepair rebuilds such a
// .git as the user's, before a turn and as soon as a failed row shows it. The live part runs the real `codex sandbox`.
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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

        // ─── a work folder the sandbox made before its second write SID reached the root (XSurLab, 2026-11-09 evening) ───
        string stale = Path.Combine(Path.GetTempPath(), "cx-stale-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            const string X = "S-1-5-21-1111-2222-3333-4444";
            Directory.CreateDirectory(Path.Combine(stale, "src", "inner"));
            Directory.CreateDirectory(Path.Combine(stale, "docs"));
            Directory.CreateDirectory(Path.Combine(stale, "node_modules", "pkg"));
            Directory.CreateDirectory(Path.Combine(stale, ".git"));
            File.WriteAllText(Path.Combine(stale, "src", "inner", "a.cs"), "class A {}");
            File.WriteAllText(Path.Combine(stale, "old.txt"), "old");
            File.WriteAllText(Path.Combine(stale, "new.txt"), "new");
            // The root hands down X, as Codex's setup step does with a write SID.
            var di = new DirectoryInfo(stale);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(X), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
            check("cx-folder", "the root's inherited grants are read, for folders and for files",
                CodexSandboxRepair.InheritedGrants(stale, false).Contains(X) && CodexSandboxRepair.InheritedGrants(stale, true).Contains(X), "");

            string N(string p) => Path.GetRelativePath(stale, p).Replace('\\', '/');
            // The harness's world: src, src/inner, old.txt, node_modules/pkg and .git are the sandbox's and miss X; docs is
            // the sandbox's but has X; new.txt is the user's.
            var sandboxOwned = new HashSet<string> { "src", "src/inner", "old.txt", "docs", "node_modules/pkg", ".git" };
            var missing = new HashSet<string> { "src", "src/inner", "old.txt", "node_modules/pkg", ".git" };
            string? Owner(string p) => sandboxOwned.Contains(N(p)) ? @"PC\CodexSandboxOnline" : @"PC\xman";
            HashSet<string>? Grants(string p) => missing.Contains(N(p)) ? new() { "S-1-1-0" } : new() { X, "S-1-1-0" };
            HashSet<string> Root(string r, bool files) => new() { X };
            var found = CodexSandboxRepair.StaleEntries(stale, Owner, Root, Grants).Select(N).OrderBy(x => x, StringComparer.Ordinal).ToList();
            check("cx-folder", "stale: the highest sandbox-owned folder and file missing the root's grant — not what is below, not .git, not inside a package cache, not one that has it, not the user's",
                found.SequenceEqual(new[] { "old.txt", "src" }), string.Join(",", found));
            File.WriteAllText(Path.Combine(stale, "docs", "deep.txt"), "deep");
            sandboxOwned.Add("docs/deep.txt");
            missing.Add("docs/deep.txt");
            var quick = CodexSandboxRepair.StaleEntries(stale, Owner, Root, Grants, allFiles: false).Select(N).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var full = CodexSandboxRepair.StaleEntries(stale, Owner, Root, Grants).Select(N).OrderBy(x => x, StringComparer.Ordinal).ToList();
            check("cx-folder", "the quick look (before a turn) skips files below the root; the full one (after a failure) finds them",
                quick.SequenceEqual(new[] { "old.txt", "src" }) && full.SequenceEqual(new[] { "docs/deep.txt", "old.txt", "src" }),
                string.Join(",", quick) + " / " + string.Join(",", full));
            File.Delete(Path.Combine(stale, "docs", "deep.txt"));
            sandboxOwned.Remove("docs/deep.txt");
            check("cx-folder", "a root that hands nothing down, or no root → nothing to check",
                CodexSandboxRepair.StaleEntries(stale, Owner, (_, _) => new(), Grants).Count == 0
                && CodexSandboxRepair.StaleEntries(Path.Combine(stale, "nope"), Owner, Root, Grants).Count == 0, "");
            int looks = 0;
            string? Counting(string p) { looks++; return @"PC\xman"; }
            CodexSandboxRepair.RepairRoots(new[] { stale }, Counting, Root, Grants, beforeTurn: true);
            int first = looks;
            CodexSandboxRepair.RepairRoots(new[] { stale }, Counting, Root, Grants, beforeTurn: true);
            int second = looks - first;
            CodexSandboxRepair.RepairRoots(new[] { stale }, Counting, (_, _) => new() { X, "S-1-5-21-9-9-9-9" }, Grants, beforeTurn: true);
            int third = looks - first - second;
            CodexSandboxRepair.RepairRoots(new[] { stale }, Counting, (_, _) => new() { X, "S-1-5-21-9-9-9-9" }, Grants);
            int fourth = looks - first - second - third;
            check("cx-folder", "before a turn a root walked with the same grants is not walked again; a new grant on the root, or a failure, walks it again",
                first > 2 && second <= 1 && third > 2 && fourth > 2, $"{first}/{second}/{third}/{fourth}");
            check("cx-folder", "a folder whose ACL is protected (someone chose that) is left alone, and what is below it is still looked at",
                CodexSandboxRepair.StaleEntries(stale, Owner, Root, p => N(p) == "src" ? null : Grants(p)).Select(N).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "old.txt", "src/inner" }), "");

            var outs = CodexSandboxRepair.RepairRoots(new[] { stale }, p => N(p) == ".git" ? @"PC\xman" : Owner(p), Root, Grants);
            var beside = Directory.GetFileSystemEntries(stale).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
            check("cx-folder", "both rebuilt as work entries (the note says folder): same content, nothing left beside them",
                outs.Count == 2 && outs.All(o => o.Repaired && o.Folder && o.Error == null)
                && File.ReadAllText(Path.Combine(stale, "src", "inner", "a.cs")) == "class A {}"
                && File.ReadAllText(Path.Combine(stale, "old.txt")) == "old"
                && beside.SequenceEqual(new[] { ".git", "docs", "new.txt", "node_modules", "old.txt", "src" }),
                $"{string.Join("; ", outs)} · beside: {string.Join(",", beside)}");
            check("cx-folder", "the rebuilt folder, its file and the rebuilt file now carry the root's grant",
                CodexSandboxRepair.GrantsOf(Path.Combine(stale, "src"))?.Contains(X) == true
                && CodexSandboxRepair.GrantsOf(Path.Combine(stale, "src", "inner", "a.cs"))?.Contains(X) == true
                && CodexSandboxRepair.GrantsOf(Path.Combine(stale, "old.txt"))?.Contains(X) == true, "");
            string? folderNote = CodexSandboxRepair.NoteAfterFailure(new[] { stale }, false,
                p => N(p) == "docs" ? @"PC\CodexSandboxOnline" : @"PC\xman");
            check("cx-folder", "with the real ACLs nothing is stale any more → no note", folderNote == null, folderNote ?? "");

            // A turn still writing in the folder while it is copied: the original goes back, nothing is lost.
            string busy = Path.Combine(stale, "busy");
            Directory.CreateDirectory(busy);
            File.WriteAllText(Path.Combine(busy, "w.txt"), "being written");
            File.SetLastWriteTimeUtc(Path.Combine(busy, "w.txt"), DateTime.UtcNow.AddHours(1));
            var raced = CodexSandboxRepair.Rebuild(busy, sizeLimit: true);
            check("cx-folder", "a folder written while it was copied → put back as it was, the copy gone, the note says why",
                !raced.Repaired && raced.Error?.Contains("changed while") == true
                && File.ReadAllText(Path.Combine(busy, "w.txt")) == "being written"
                && Directory.GetFileSystemEntries(stale).Count(e => Path.GetFileName(e).StartsWith("busy", StringComparison.Ordinal)) == 1,
                raced.ToString());
        }
        finally { ForceDelete(stale); }

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

        // ─── live: a folder the sandbox made as the cwd, written as a writable root (how Codex's edit helper sees it) ───
        // Not under %TEMP%: Codex makes TEMP a writable root of every run, and that grant would hide the gap.
        string id = Guid.NewGuid().ToString("N")[..8];
        string home = Path.Combine(AppContext.BaseDirectory, "cx-sid-" + id);
        string elsewhere = Path.Combine(AppContext.BaseDirectory, "cx-sid-other-" + id);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(elsewhere);
        try
        {
            var made = await Sandboxed(codex, home, "cmd", "/c", "mkdir sub && echo one> sub\\a.txt");
            string asRoot = "sandbox_workspace_write.writable_roots=['" + home + "']";
            string target = Path.Combine(home, "sub", "b.txt");
            var denied = await Run(codex, elsewhere, "sandbox", "-c", "sandbox_mode=\"workspace-write\"", "-c", asRoot, "--", "cmd", "/c", "echo two> " + target);
            check("cx-folder-live", "the gap is real: a folder the sandbox made as the cwd cannot be written by a run that has it as a writable root",
                made.Exit == 0 && denied.Exit != 0 && !File.Exists(target), $"mkdir exit {made.Exit} · write exit {denied.Exit} · {Cap(denied.Output)}");
            var outs = CodexSandboxRepair.RepairRoots(new[] { home });
            check("cx-folder-live", "the repair finds that folder and rebuilds it as the user's",
                outs.Count == 1 && outs[0].Repaired && outs[0].Folder && Path.GetFileName(outs[0].Path) == "sub"
                && !CodexSandboxRepair.IsSandboxAccount(CodexSandboxRepair.OwnerOf(Path.Combine(home, "sub")))
                && File.ReadAllText(Path.Combine(home, "sub", "a.txt")).Trim() == "one", string.Join("; ", outs));
            var works = await Run(codex, elsewhere, "sandbox", "-c", "sandbox_mode=\"workspace-write\"", "-c", asRoot, "--", "cmd", "/c", "echo two> " + target);
            var asCwd = await Sandboxed(codex, home, "cmd", "/c", "echo three> sub\\c.txt");
            check("cx-folder-live", "…after which both kinds of run write in it",
                works.Exit == 0 && asCwd.Exit == 0 && File.Exists(target) && File.Exists(Path.Combine(home, "sub", "c.txt")),
                $"root exit {works.Exit} · cwd exit {asCwd.Exit} · {Cap(works.Output + asCwd.Output)}");
            check("cx-folder-live", "a second look finds nothing", CodexSandboxRepair.RepairRoots(new[] { home }).Count == 0, "");
        }
        finally { ForceDelete(home); ForceDelete(elsewhere); }
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
