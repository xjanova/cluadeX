using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#nullable enable
namespace ManagerTest;

/// <summary>
/// Contract harness for the manager pages (MCP servers, hex editor, plugins, models, permissions, time machine,
/// settings, tasks). Links the BUILT CluadeX.dll and drives the real services with real files, real processes,
/// real git and real PowerShell — no mocks. portable.txt keeps every write next to this exe.
///
///   dotnet run -p:CluadeXBin=&lt;OutDir&gt;\                — offline checks
///   dotnet run -p:CluadeXBin=&lt;OutDir&gt;\ -- --network   — also checks every catalog model against huggingface.co
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    static IServiceProvider Sp = null!;
    static string Tmp = "";

    [STAThread]
    static int Main(string[] args)
    {
        // Child mode for the MCP launch test: print argv exactly as this process received it.
        if (args.Length > 0 && args[0] == "--echo-args")
        {
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            o.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
            return 0;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run(args.Contains("--network")).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-6} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-6} {name}");
        if (detail.Length > 0) Console.WriteLine("             " + (detail.Length > 600 ? detail[..600] + "…" : detail).Replace("\n", "\n             "));
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-6} harness case threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void CaseSync(string id, Action body) => Case(id, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    static async Task<int> Run(bool network)
    {
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        Sp = services.BuildServiceProvider();

        var settings = Sp.GetRequiredService<SettingsService>();
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }
        Tmp = Path.Combine(Path.GetTempPath(), "cluadex-managertest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Tmp);
        Console.WriteLine($"scratch   : {Tmp}\n");

        Console.WriteLine("── DI: every changed page still resolves from the app's container ──");
        CaseSync("D1", () =>
        {
            var types = new[]
            {
                typeof(HexEditorViewModel), typeof(PermissionsViewModel), typeof(McpServersViewModel),
                typeof(PluginManagerViewModel), typeof(ModelManagerViewModel), typeof(TimeMachineViewModel),
                typeof(TaskManagerViewModel), typeof(SettingsViewModel), typeof(FeaturesViewModel),
            };
            var failed = new List<string>();
            foreach (var t in types)
                try { _ = Sp.GetRequiredService(t); } catch (Exception ex) { failed.Add($"{t.Name}: {ex.GetType().Name}: {ex.Message}"); }
            Check("D1", "the 9 changed view-models resolve with their new constructor parameters", failed.Count == 0, string.Join("\n", failed));
        });

        Console.WriteLine("\n── MCP: arguments round-trip, Toggle, launch ──");
        await McpTests();
        Console.WriteLine("\n── Hex editor: >64 MB files, backup, unsaved edits, Go to ──");
        await HexTests();
        Console.WriteLine("\n── Plugins: hook scripts (env/payload, no splicing), install guards ──");
        PluginTests();
        Console.WriteLine("\n── Models: Hugging Face file ranking / shards ──");
        HfTests();
        if (network)
        {
            Console.WriteLine("\n── Models: catalog vs huggingface.co (--network) ──");
            await HfNetworkTests();
        }
        Console.WriteLine("\n── Permissions: scopes the agent really checks ──");
        PermissionTests();
        Console.WriteLine("\n── Time Machine: Thai paths, renames, snapshot-first rewind ──");
        await TimeMachineTests();
        Console.WriteLine("\n── Settings: undecryptable keys kept, save failure reported ──");
        SettingsTests();
        Console.WriteLine("\n── Tasks: bounded output, project folder ──");
        await TaskTests();

        try { Directory.Delete(Tmp, true); } catch { }
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL ═══");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    // ════════════════════════════ MCP ════════════════════════════

    static readonly List<string> TrickyArgs = new()
    {
        "-y", "@modelcontextprotocol/server-filesystem", @"C:\Program Files\My Data", "two  spaces",
        "a\"b", @"trail\", @"dir with space\", "&calc", "50%", "%PATH%", "^caret", "(paren)", "ไทย ทดสอบ",
        "semi;colon", "comma,list", "", "--name=My Server",
    };

    static async Task McpTests()
    {
        CaseSync("M1", () =>
        {
            var cfg = new McpServerConfig { Name = "x", Command = "npx", Args = new List<string>(TrickyArgs), Enabled = true };
            var item = McpServerDisplayItem.FromConfig("x", cfg, false, 0);
            var back = item.ToConfig();
            Check("M1", "an untouched server round-trips its arguments exactly (spaces, quotes, trailing \\, empty)",
                back.Args.SequenceEqual(TrickyArgs), string.Join(" | ", back.Args));
        });
        CaseSync("M2", () =>
        {
            var want = TrickyArgs.Where(a => a.Trim().Length > 0).ToList();
            var item = McpServerDisplayItem.FromConfig("x", new McpServerConfig { Command = "npx" }, false, 0);
            item.ArgsText = string.Join("\r\n", want) + "\r\n";   // what a user pastes/types, CRLF + trailing Enter
            var back = item.ToConfig();
            Check("M2", "edited one-per-line arguments come back exactly (CRLF and a trailing blank line ignored)",
                back.Args.SequenceEqual(want), string.Join(" | ", back.Args));
        });
        CaseSync("M3", () =>
        {
            var got = McpServerDisplayItem.ParseArgsLine(@"-y @scope/pkg ""C:\Program Files\x"" --flag");
            Check("M3", "the Add box splits on spaces but keeps a \"quoted path with spaces\" whole",
                got.SequenceEqual(new[] { "-y", "@scope/pkg", @"C:\Program Files\x", "--flag" }), string.Join(" | ", got));
        });

        await Case("M4", async () =>
        {
            var mgr = Sp.GetRequiredService<McpServerManager>();
            var loc = Sp.GetRequiredService<LocalizationService>();
            var settings = Sp.GetRequiredService<SettingsService>();
            string path = Path.Combine(settings.DataRoot, "mcp_servers.json");
            var file = new McpConfigFile();
            file.McpServers["fs"] = new McpServerConfig { Command = "npx", Args = new List<string>(TrickyArgs), Env = new() { ["API_KEY"] = "k e y" }, Enabled = true };
            File.WriteAllText(path, JsonSerializer.Serialize(file));
            mgr.LoadConfig();

            var vm = new McpServersViewModel(mgr, loc);
            vm.SelectedServer = vm.Servers.First(s => s.Name == "fs");
            vm.ToggleEnabledCommand.Execute(null);
            var saved = JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path))!.McpServers["fs"];
            Check("M4", "Toggle only flips Enabled — the saved args and env are untouched",
                !saved.Enabled && saved.Args.SequenceEqual(TrickyArgs) && saved.Env?["API_KEY"] == "k e y",
                $"enabled={saved.Enabled} args={string.Join(" | ", saved.Args)}");

            vm.BeginEditCommand.Execute(null);
            vm.SelectedServer!.ArgsText = "one\ntwo with space";
            vm.SaveEditCommand.Execute(null);
            saved = JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path))!.McpServers["fs"];
            Check("M4b", "Edit → Save writes exactly the lines typed, env kept",
                saved.Args.SequenceEqual(new[] { "one", "two with space" }) && saved.Env?["API_KEY"] == "k e y",
                string.Join(" | ", saved.Args));

            vm.SelectedServer.LogOutput = "keep me";
            var before = vm.SelectedServer;
            // Simulate the manager telling the page a state changed (what used to rebuild every item).
            typeof(McpServersViewModel).GetMethod("OnServerStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vm, new object[] { "fs" });
            Check("M4c", "a state change updates the server in place — same item, log kept",
                ReferenceEquals(vm.Servers.First(s => s.Name == "fs"), before) && before.LogOutput == "keep me");
            File.Delete(path);
            mgr.LoadConfig();
        });

        await Case("M5", async () =>
        {
            string bin = Path.Combine(Tmp, "mcp bin");
            Directory.CreateDirectory(bin);
            string self = Environment.ProcessPath!;
            File.WriteAllText(Path.Combine(bin, "echoargs.cmd"), $"@\"{self}\" --echo-args %*\r\n");
            File.WriteAllText(Path.Combine(bin, "echoargs"), "#!/bin/sh\necho posix shim\n");   // like npm's extension-less npx

            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = bin + ";" + Environment.GetEnvironmentVariable("SystemRoot") + @"\System32",
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
                ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC"),
                ["TEMP"] = Path.GetTempPath(), ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
                ["ProgramFiles"] = Environment.GetEnvironmentVariable("ProgramFiles"),
                ["ANTHROPIC_API_KEY"] = "sk-ant-should-not-leak",
            };
            // cmd.exe cannot carry an argument that mixes a quote with & | < > through a .cmd shim (Node's
            // cross-spawn has the same limit) — the realistic set: paths with spaces, %, ^, parens, Thai, empties.
            var args = TrickyArgs.Where(a => !(a.Contains('"'))).ToList();
            var cfg = new McpServerConfig { Command = "echoargs", Args = args, Env = new() { ["MY_SETTING"] = "1" } };
            var psi = McpStdioTransport.BuildStartInfo(cfg, env);
            Check("M5", "a bare .cmd name (npx-style) resolves through PATH + PATHEXT and runs via cmd.exe",
                psi.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && psi.Arguments.Contains("echoargs.cmd", StringComparison.OrdinalIgnoreCase),
                psi.FileName + " " + psi.Arguments);
            Check("M5b", "the child gets PATH + config env but NOT the parent's API keys",
                psi.Environment.ContainsKey("PATH") && psi.Environment["MY_SETTING"] == "1" && !psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            var got = await RunEcho(psi);
            Check("M5c", "every argument reaches the program behind the .cmd shim exactly", got.SequenceEqual(args),
                "got: " + string.Join(" | ", got));

            var cfgExe = new McpServerConfig { Command = self, Args = new List<string> { "--echo-args" }.Concat(TrickyArgs).ToList() };
            var psiExe = McpStdioTransport.BuildStartInfo(cfgExe, env);
            var gotExe = await RunEcho(psiExe);
            Check("M5d", "a real .exe gets its arguments through ArgumentList — quotes and trailing backslashes intact",
                gotExe.SequenceEqual(TrickyArgs), "got: " + string.Join(" | ", gotExe));

            bool threw = false;
            try { McpStdioTransport.BuildStartInfo(new McpServerConfig { Command = "definitely-not-a-command-xyz" }, env); }
            catch (InvalidOperationException ex) { threw = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase); }
            Check("M5e", "an unknown command fails with \"was not found\" instead of a Win32 error", threw);
        });
    }

    static async Task<string[]> RunEcho(ProcessStartInfo psi)
    {
        psi.RedirectStandardInput = false;
        using var p = Process.Start(psi)!;
        string outText = await p.StandardOutput.ReadToEndAsync();
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        try { return JsonSerializer.Deserialize<string[]>(outText) ?? Array.Empty<string>(); }
        catch { return new[] { "<unparseable>", outText, err }; }
    }

    // ════════════════════════════ HEX ════════════════════════════

    static byte Pattern(long i) => (byte)((i * 31 + 7) & 0xFF);

    static async Task HexTests()
    {
        string big = Path.Combine(Tmp, "big.bin");
        long size = HexEditorService.InMemoryLimit + 3 * 1024 * 1024 + 123;   // 64 MB + ~3 MB
        await using (var fs = new FileStream(big, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[1 << 20];
            for (long off = 0; off < size; off += buf.Length)
            {
                int n = (int)Math.Min(buf.Length, size - off);
                for (int i = 0; i < n; i++) buf[i] = Pattern(off + i);
                await fs.WriteAsync(buf.AsMemory(0, n));
            }
        }

        await Case("H1", async () =>
        {
            var hex = new HexEditorService();
            await hex.OpenAsync(big);
            Check("H1", "a file over 64 MB opens truncated and says so", hex.IsTruncated && hex.Size == HexEditorService.InMemoryLimit && hex.FileLength == size);
            hex.Patch(10, new byte[] { 0xAA, 0xBB });
            await hex.SaveAsync(createBackup: true);
            var fi = new FileInfo(big);
            byte[] head = new byte[12], tail = new byte[1];
            await using (var fs = File.OpenRead(big)) { fs.Read(head, 0, 12); fs.Position = size - 1; fs.Read(tail, 0, 1); }
            Check("H1b", "saving patches in place: same length, patch applied, bytes past 64 MB untouched",
                fi.Length == size && head[10] == 0xAA && head[11] == 0xBB && tail[0] == Pattern(size - 1),
                $"len={fi.Length} (want {size}) b10={head[10]:X2} last={tail[0]:X2}");
            var bak = new FileInfo(big + ".bak");
            byte[] b10 = new byte[1];
            await using (var fs = File.OpenRead(bak.FullName)) { fs.Position = 10; fs.Read(b10, 0, 1); }
            Check("H1c", "the .bak is a full copy of the original", bak.Exists && bak.Length == size && b10[0] == Pattern(10));

            // Backup that cannot be written → nothing saved.
            File.Delete(bak.FullName);
            Directory.CreateDirectory(bak.FullName);   // a FOLDER named big.bin.bak: File.Copy onto it fails
            hex.Patch(20, new byte[] { 0xCC });
            bool aborted = false;
            try { await hex.SaveAsync(createBackup: true); } catch (HexBackupFailedException) { aborted = true; }
            byte[] b20 = new byte[1];
            await using (var fs = File.OpenRead(big)) { fs.Position = 20; fs.Read(b20, 0, 1); }
            Check("H2", "when the .bak can't be written the save is aborted and the file is untouched", aborted && b20[0] == Pattern(20));
            Directory.Delete(bak.FullName);

            // Unsaved edits are not thrown away by another open (the agent's hex_open shares this service).
            string small = Path.Combine(Tmp, "small.bin");
            File.WriteAllBytes(small, new byte[] { 1, 2, 3, 4 });
            bool refused = false;
            try { await hex.OpenAsync(small); } catch (HexUnsavedChangesException) { refused = true; }
            Check("H3", "opening another file with unsaved edits is refused when nobody confirms", refused && hex.CurrentPath == big && hex.IsDirty);
            hex.ConfirmDiscardChanges = (_, _) => Task.FromResult(true);
            await hex.OpenAsync(small);
            Check("H3b", "…and allowed once the user agrees to discard", hex.CurrentPath == small && !hex.IsDirty);

            // Save As of a truncated file = full copy + patch.
            hex.ConfirmDiscardChanges = null;
            await hex.OpenAsync(big);
            hex.Patch(5, new byte[] { 0xEE });
            string copy = Path.Combine(Tmp, "copy.bin");
            await hex.SaveAsAsync(copy);
            byte[] c5 = new byte[1], cLast = new byte[1], o5 = new byte[1];
            await using (var fs = File.OpenRead(copy)) { fs.Position = 5; fs.Read(c5, 0, 1); fs.Position = size - 1; fs.Read(cLast, 0, 1); }
            await using (var fs = File.OpenRead(big)) { fs.Position = 5; fs.Read(o5, 0, 1); }
            Check("H4", "Save As of a large file writes the whole file with the patch (not a 64 MB stub)",
                new FileInfo(copy).Length == size && c5[0] == 0xEE && cLast[0] == Pattern(size - 1) && o5[0] == Pattern(5));
            hex.ConfirmDiscardChanges = (_, _) => Task.FromResult(true);
            hex.Close();
        });

        CaseSync("H5", () =>
        {
            bool ok1 = HexEditorViewModel.TryParseOffset("100", out long a) && a == 100;
            bool ok2 = HexEditorViewModel.TryParseOffset("0x100", out long b) && b == 256;
            bool ok3 = !HexEditorViewModel.TryParseOffset("FF", out _);
            bool ok4 = HexEditorViewModel.TryParseOffset("1,024", out long c) && c == 1024;
            bool ok5 = !HexEditorViewModel.TryParseOffset("-5", out _);
            Check("H5", "Go to: \"100\" is decimal 100, \"0x100\" is 256, bare \"FF\" is rejected", ok1 && ok2 && ok3 && ok4 && ok5,
                $"{ok1} {ok2} {ok3} {ok4} {ok5}");
        });
        try { File.Delete(big); File.Delete(big + ".bak"); } catch { }
    }

    // ════════════════════════════ PLUGINS ════════════════════════════

    /// <summary>Run a hook command exactly like HookService: cmd /c, cwd = project, CLUADEX_HOOK_* env.</summary>
    static (int Exit, string StdErr) RunHook(string command, string cwd, string tool, string path, object payload)
    {
        string payloadFile = Path.Combine(Tmp, "payload-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(payloadFile, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
        var psi = new ProcessStartInfo("cmd", $"/c {command}")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["CLUADEX_HOOK_TOOL"] = tool;
        psi.Environment["CLUADEX_HOOK_PATH"] = path;
        psi.Environment["CLUADEX_HOOK_CWD"] = cwd;
        psi.Environment["CLUADEX_HOOK_PAYLOAD_FILE"] = payloadFile;
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        try { File.Delete(payloadFile); } catch { }
        return (p.ExitCode, errTask.Result);
    }

    static string HookCommand(PluginService svc, string id, string matcher)
    {
        var catalog = PluginService.CuratedCatalog.First(c => c.Id == id);
        svc.InstallCatalogPlugin(catalog);
        string hooksJson = File.ReadAllText(Path.Combine(svc.PluginsDirectory, id, "hooks.json"));
        var root = JsonNode.Parse(hooksJson)!["hooks"]!;
        foreach (var phase in new[] { "PreToolUse", "PostToolUse" })
            if (root[phase] is JsonArray arr)
                foreach (var h in arr)
                    if ((string?)h!["matcher"] == matcher) return (string)h["command"]!;
        throw new Exception($"no {matcher} hook in {id}");
    }

    static void PluginTests()
    {
        var svc = Sp.GetRequiredService<PluginService>();
        string proj = Path.Combine(Tmp, "proj");
        Directory.CreateDirectory(proj);

        CaseSync("P1", () =>
        {
            string cmd = HookCommand(svc, "size-guard", "write_file");
            Check("P1", "the size-guard hook command contains no {path} splice", !cmd.Contains("{path}") && cmd.Contains("size-guard.ps1"), cmd);
            // The file name from the audit: under the old  Get-Item '{path}'  this ran New-Item.
            string evil = "a'+(New-Item -ItemType File -Force -Path pwned.txt)+'.txt";
            var r = RunHook(cmd, proj, "write_file", evil, new { path = evil, content = "hello" });
            Check("P1b", "a file named a'+(New-Item …)+'.txt runs nothing and is allowed", r.Exit == 0 && !File.Exists(Path.Combine(proj, "pwned.txt")), r.StdErr);
            var big = RunHook(cmd, proj, "write_file", "big.txt", new { path = "big.txt", content = new string('x', 600 * 1024) });
            Check("P1c", "600 KB of NEW content is blocked with exit 2 (the old guard measured the file on disk)", big.Exit == 2 && big.StdErr.Contains("size-guard"), big.StdErr);
        });

        CaseSync("P2", () =>
        {
            string cmd = HookCommand(svc, "secret-scanner", "write_file");
            string cmdEdit = HookCommand(svc, "secret-scanner", "edit_file");
            File.WriteAllText(Path.Combine(proj, "old.txt"), "AKIAABCDEFGHIJKLMNOP leaked earlier");
            var clean = RunHook(cmd, proj, "write_file", "old.txt", new { path = "old.txt", content = "nothing secret here" });
            Check("P2", "clean new content passes even though the file on disk still holds a key", clean.Exit == 0, clean.StdErr);
            var task = RunHook(cmd, proj, "write_file", "t.md", new { path = "t.md", content = "run task-1234567890abcdefghijkl then disk-cleanup-abcdefghijklmnopqrst" });
            Check("P2b", "\"task-…\" / \"disk-…\" are not mistaken for sk- API keys", task.Exit == 0, task.StdErr);
            var key = RunHook(cmd, proj, "write_file", "cfg.js", new { path = "cfg.js", content = "const k = 'sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx0123';" });
            Check("P2c", "a real sk- key in the new content is blocked with exit 2", key.Exit == 2 && key.StdErr.Contains("secret-scanner"), key.StdErr);
            var removing = RunHook(cmdEdit, proj, "edit_file", "old.txt", new { path = "old.txt", find = "AKIAABCDEFGHIJKLMNOP", replace = "REDACTED" });
            Check("P2d", "an edit that REMOVES a key is not blocked (old text is not new content)", removing.Exit == 0, removing.StdErr);
            var adding = RunHook(cmdEdit, proj, "edit_file", "old.txt", new { path = "old.txt", find = "x", replace = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" });
            Check("P2e", "an edit that ADDS a GitHub token is blocked with exit 2", adding.Exit == 2, adding.StdErr);
        });

        CaseSync("P3", () =>
        {
            string cmd = HookCommand(svc, "backup-before-edit", "edit_file");
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "src", "a.cs"), "original");
            string backups = Path.Combine(proj, ".cluadex-backups");
            string stale = Path.Combine(backups, DateTime.Now.AddDays(-9).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            string keepMe = Path.Combine(backups, "my-notes");
            Directory.CreateDirectory(stale); Directory.CreateDirectory(keepMe);
            var r = RunHook(cmd, proj, "edit_file", @"src\a.cs", new { path = @"src\a.cs", find = "original", replace = "new" });
            var copies = Directory.Exists(backups) ? Directory.GetFiles(backups, "a.cs", SearchOption.AllDirectories) : Array.Empty<string>();
            bool stamped = copies.Any(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"\\\d{8}-\d{6}\\src\\a\.cs$") && File.ReadAllText(c) == "original");
            Check("P3", "the original is copied to .cluadex-backups/<yyyyMMdd-HHmmss>/src/a.cs", r.Exit == 0 && stamped, string.Join("\n", copies) + r.StdErr);
            Check("P3b", "backup folders older than 7 days are removed; other folders are left alone", !Directory.Exists(stale) && Directory.Exists(keepMe));
        });

        CaseSync("P4", () =>
        {
            string cmd = HookCommand(svc, "path-guard", "read_file");
            var outside = RunHook(cmd, proj, "read_file", @"..\secret.txt", new { path = @"..\secret.txt" });
            var inside = RunHook(cmd, proj, "read_file", @"src\a.cs", new { path = @"src\a.cs" });
            var weird = RunHook(cmd, proj, "read_file", "a'+(calc)+'.txt", new { path = "a'+(calc)+'.txt" });
            Check("P4", "path-guard blocks ..\\ outside the project with exit 2 (without quoting the path) and allows paths inside it (odd names included)",
                outside.Exit == 2 && !outside.StdErr.Contains("secret.txt") && inside.Exit == 0 && weird.Exit == 0, $"{outside.Exit} {inside.Exit} {weird.Exit} {outside.StdErr}");
        });

        CaseSync("P5", () =>
        {
            var hookless = PluginService.CuratedCatalog.Where(c => !PluginService.IsImplemented(c)).ToList();
            bool refused = false;
            try { svc.InstallCatalogPlugin(hookless[0]); } catch (InvalidOperationException) { refused = true; }
            Check("P5", $"the {hookless.Count} catalog entries without hooks can't be installed as \"enabled\" no-ops", refused && hookless.Count > 0);
        });

        CaseSync("P6", () =>
        {
            string installed = Path.Combine(svc.PluginsDirectory, "size-guard");
            Check("P6", "picking a folder inside the plugins folder is refused (it used to delete the plugin)",
                svc.CheckInstallSource(installed) == PluginService.InstallCheck.InsidePluginsFolder);
            Check("P6b", "picking the data folder that CONTAINS the plugins folder is refused",
                svc.CheckInstallSource(Path.GetDirectoryName(svc.PluginsDirectory)!) == PluginService.InstallCheck.ContainsPluginsFolder);

            string src = Path.Combine(Tmp, "myplugin");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "manifest.json"), "{\"name\":\"My Plugin\",\"version\":\"1.0\"}");
            File.WriteAllText(Path.Combine(src, "hooks.json"), "{\"hooks\":{}}");
            svc.InstallPlugin(src);
            File.WriteAllText(Path.Combine(src, "manifest.json"), "{\"name\":\"My Plugin\",\"version\":\"2.0\"}");
            Check("P6c", "re-installing a plugin of the same name needs an explicit overwrite",
                svc.CheckInstallSource(src) == PluginService.InstallCheck.AlreadyInstalled);
            bool refused = false;
            try { svc.InstallPlugin(src); } catch (InvalidOperationException) { refused = true; }
            svc.InstallPlugin(src, overwrite: true);
            string v = File.ReadAllText(Path.Combine(svc.InstallTargetFor(src), "manifest.json"));
            Check("P6d", "…refused without it, replaced with it", refused && v.Contains("2.0"));
            Check("P6e", "no half-copied staging folder is left inside plugins\\",
                !Directory.GetDirectories(svc.PluginsDirectory).Any(d => Path.GetFileName(d).StartsWith('.')));
            svc.UninstallPlugin("My Plugin");
        });

        foreach (var id in new[] { "size-guard", "secret-scanner", "backup-before-edit", "path-guard" })
            try { svc.UninstallPlugin(PluginService.CuratedCatalog.First(c => c.Id == id).Name); } catch { }
    }

    // ════════════════════════════ HUGGING FACE ════════════════════════════

    static void HfTests()
    {
        CaseSync("F1", () =>
        {
            var files = new List<(string, long?)>
            {
                ("Qwen-IQ2_M.gguf", 600), ("Qwen-IQ3_M.gguf", 700), ("Qwen-Q2_K.gguf", 500), ("Qwen-Q2_K_L.gguf", 520),
                ("Qwen-Q4_K_M.gguf", 1000), ("Qwen-Q4_K_S.gguf", 950), ("Qwen-Q8_0.gguf", 1600), ("Qwen-f16.gguf", 3000),
                ("mmproj-Qwen-f16.gguf", 300),
                ("big/Model-Q4_K_M-00001-of-00002.gguf", 5), ("big/Model-Q4_K_M-00002-of-00002.gguf", 6),
                ("Part-Q5_K_M-00001-of-00003.gguf", 1),
            };
            var ranked = HuggingFaceService.GroupAndRankGgufFiles(files);
            var names = ranked.Select(r => r.RFilename).ToList();
            Check("F1", "Q4_K_M files come first (the old list was alphabetical: IQ2, IQ3, … and Q4_K_M never made the top 5)",
                ranked.Take(2).All(r => r.RFilename.Contains("Q4_K_M")), string.Join(", ", names));
            var sharded = ranked.FirstOrDefault(r => r.RFilename.StartsWith("big/"));
            Check("F1b", "a split model is ONE entry: first shard, total size", sharded?.RFilename == "big/Model-Q4_K_M-00001-of-00002.gguf" && sharded.Size == 11);
            Check("F1c", "vision projectors and incomplete shard sets are not offered",
                !names.Any(n => n.Contains("mmproj")) && !names.Any(n => n.StartsWith("Part-")));
            Check("F1d", "Q4_K_S ranks after Q4_K_M and before Q2_K (token match, not substring)",
                names.IndexOf("Qwen-Q4_K_S.gguf") > names.IndexOf("Qwen-Q4_K_M.gguf") && names.IndexOf("Qwen-Q4_K_S.gguf") < names.IndexOf("Qwen-Q2_K.gguf"));
            var shards = HuggingFaceService.ShardFileNames("big/Model-Q4_K_M-00001-of-00002.gguf");
            Check("F1e", "shard names are derived for the download", shards.SequenceEqual(new[] { "big/Model-Q4_K_M-00001-of-00002.gguf", "big/Model-Q4_K_M-00002-of-00002.gguf" }));
        });
    }

    static async Task HfNetworkTests()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-catalogtest");
        int bad = 0;
        foreach (var m in HuggingFaceService.Catalog)
        {
            await Case("N:" + m.DisplayName, async () =>
            {
                var api = await http.GetAsync($"https://huggingface.co/api/models/{m.RepoId}?blobs=true");
                string problem = "";
                if (!api.IsSuccessStatusCode) problem = $"API {(int)api.StatusCode}";
                else
                {
                    using var doc = JsonDocument.Parse(await api.Content.ReadAsStringAsync());
                    bool gated = doc.RootElement.TryGetProperty("gated", out var g) && g.ValueKind == JsonValueKind.String;
                    var sib = doc.RootElement.GetProperty("siblings").EnumerateArray()
                        .FirstOrDefault(s => s.GetProperty("rfilename").GetString() == m.FileName);
                    if (sib.ValueKind == JsonValueKind.Undefined) problem = "file not in repo";
                    else if (sib.TryGetProperty("size", out var sz) && sz.GetInt64() != m.ApproxSizeBytes) problem = $"size {sz.GetInt64()} ≠ catalog {m.ApproxSizeBytes}";
                    if (gated != HuggingFaceService.IsGatedRepo(m.RepoId)) problem += $" gated={gated} but IsGatedRepo={HuggingFaceService.IsGatedRepo(m.RepoId)}";
                }
                using var head = new HttpRequestMessage(HttpMethod.Head, $"https://huggingface.co/{m.RepoId}/resolve/main/{m.FileName}");
                var resp = await http.SendAsync(head);
                int code = (int)resp.StatusCode;
                bool reachable = HuggingFaceService.IsGatedRepo(m.RepoId) ? code is 401 or 403 : resp.IsSuccessStatusCode;
                if (!reachable) problem += $" HEAD {code}";
                if (problem.Length > 0) bad++;
                Check("N", $"{m.DisplayName}: {m.RepoId}/{m.FileName}", problem.Length == 0, problem.Trim());
            });
        }
        Console.WriteLine($"  catalog: {HuggingFaceService.Catalog.Count} models, {bad} with problems");

        // Real download of a 1.2 MB public GGUF: verification, resume from a kept partial, corrupt partial.
        await Case("N2", async () =>
        {
            var hf = Sp.GetRequiredService<HuggingFaceService>();
            const string repo = "ggml-org/test-model-stories260K", file = "stories260K-f32.gguf";
            var meta = await hf.GetFileMetaAsync(repo, file);
            var progress = new Progress<HuggingFaceService.DownloadProgressInfo>(_ => { });

            string d1 = Path.Combine(Tmp, "dl1"); Directory.CreateDirectory(d1);
            string t1 = Path.Combine(d1, file);
            await hf.DownloadModelAsync(repo, file, t1, progress);
            byte[] full = File.ReadAllBytes(t1);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(full)).ToLowerInvariant();
            Check("N2", "a download ends as a verified .gguf (size and SHA-256 as published)",
                full.Length == meta.Size && sha == meta.Sha256 && !File.Exists(t1 + ".downloading") && !File.Exists(t1 + ".downloading.json"));

            string ResumeJson() => JsonSerializer.Serialize(new { RepoId = repo, FileName = file, Revision = meta.Revision, Size = meta.Size, Sha256 = meta.Sha256 });

            string d2 = Path.Combine(Tmp, "dl2"); Directory.CreateDirectory(d2);
            string t2 = Path.Combine(d2, file);
            File.WriteAllBytes(t2 + ".downloading", full[..500_000]);
            File.WriteAllText(t2 + ".downloading.json", ResumeJson());
            await hf.DownloadModelAsync(repo, file, t2, progress);
            Check("N2b", "a kept partial resumes (Range from byte 500000 at the pinned commit) and verifies",
                File.Exists(t2) && File.ReadAllBytes(t2).AsSpan().SequenceEqual(full));

            string d3 = Path.Combine(Tmp, "dl3"); Directory.CreateDirectory(d3);
            string t3 = Path.Combine(d3, file);
            var bad = full[..500_000]; bad[1234] ^= 0xFF;
            File.WriteAllBytes(t3 + ".downloading", bad);
            File.WriteAllText(t3 + ".downloading.json", ResumeJson());
            string kind = "";
            try { await hf.DownloadModelAsync(repo, file, t3, progress); }
            catch (DownloadVerificationException ex) { kind = ex.Kind; }
            Check("N2c", "a corrupted partial is caught by the SHA-256 check and deleted — never renamed to .gguf",
                kind == "sha256" && !File.Exists(t3) && !File.Exists(t3 + ".downloading"));

            string d4 = Path.Combine(Tmp, "dl4"); Directory.CreateDirectory(d4);
            string t4 = Path.Combine(d4, file);
            File.WriteAllBytes(t4 + ".downloading", new byte[300_000]);   // no sidecar: unknown origin
            await hf.DownloadModelAsync(repo, file, t4, progress);
            Check("N2d", "a partial without a resume record is not trusted — the download starts over",
                File.Exists(t4) && File.ReadAllBytes(t4).AsSpan().SequenceEqual(full));
        });
    }

    // ════════════════════════════ PERMISSIONS ════════════════════════════

    static void PermissionTests()
    {
        CaseSync("R1", () =>
        {
            var rules = new List<PermissionRule>
            {
                new() { Pattern = @"C:\secrets\*", Scope = "file", Action = PermAction.Deny },
                new() { Pattern = "src/*", Scope = "file", Action = PermAction.Allow },
                new() { Pattern = "npm *", Scope = "command", Action = PermAction.Allow },
                new() { Pattern = "https://*", Scope = "network", Action = PermAction.Ask },
                new() { Pattern = "*", Scope = "read", Action = PermAction.Allow },
            };
            bool changed = PermissionsViewModel.MigrateLegacyScopes(rules);
            string got = string.Join(",", rules.Select(r => r.Scope));
            Check("R1", "old scopes become ones the agent checks: deny file→*, allow file→write, command/network→execute",
                changed && got == "*,write,execute,execute,read", got);
            Check("R1b", "every offered scope is one the agent asks for",
                PermissionsViewModel.AgentScopes.SequenceEqual(new[] { "*", "read", "write", "execute" }));
        });
        CaseSync("R2", () =>
        {
            var perms = Sp.GetRequiredService<PermissionService>();
            var saved = perms.Rules.ToList();
            perms.Rules.Clear();
            perms.Rules.Add(new PermissionRule { Pattern = "*.env", Scope = "write", Action = PermAction.Deny });
            var deny = perms.FindRule("prod.env", "write", "write_file");
            var other = perms.FindRule("prod.env", "read", "read_file");
            perms.Rules.Clear(); perms.Rules.AddRange(saved);
            Check("R2", "a rule made with the new \"write\" scope matches the agent's write check (and not reads)",
                deny == PermAction.Deny && other == null);
        });
    }

    // ════════════════════════════ TIME MACHINE ════════════════════════════

    static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        return o.Trim();
    }

    static async Task TimeMachineTests()
    {
        string repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.email", "t@example.com"); Git(repo, "config", "user.name", "Tester");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "ไฟล์ทดสอบ.txt"), "บรรทัดแรก\n");
        File.WriteAllText(Path.Combine(repo, "a & b.txt"), "line1\nline2\nline3\nline4\nline5\n");
        Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "first");
        string first = Git(repo, "rev-parse", "HEAD");
        File.AppendAllText(Path.Combine(repo, "ไฟล์ทดสอบ.txt"), "บรรทัดที่สอง\n");
        Git(repo, "mv", "a & b.txt", "renamed.txt");
        File.AppendAllText(Path.Combine(repo, "renamed.txt"), "line6\n");
        Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "second");
        string second = Git(repo, "rev-parse", "HEAD");

        var fs = Sp.GetRequiredService<FileSystemService>();
        fs.WorkingDirectory = repo;
        var tm = Sp.GetRequiredService<TimeMachineService>();

        await Case("T1", async () =>
        {
            var files = await tm.GetCommitFilesAsync(second);
            var thai = files.FirstOrDefault(f => f.Path == "ไฟล์ทดสอบ.txt");
            var ren = files.FirstOrDefault(f => f.Path == "renamed.txt");
            Check("T1", "a Thai file name comes back as itself, not \"\\340\\270…\"", thai != null && thai.Additions == 1,
                string.Join(" | ", files.Select(f => $"{f.Status}:{f.Path}")));
            Check("T1b", "a rename is listed under its new path with status R (not \"{a => b}\")", ren != null && ren.Status == 'R');
            var thaiDiff = await tm.GetFileDiffAsync(second, "ไฟล์ทดสอบ.txt");
            var renDiff = await tm.GetFileDiffAsync(second, "renamed.txt");
            Check("T1c", "the Thai file's diff is not empty", thaiDiff.Any(l => l.Kind == '+' && l.Content == "บรรทัดที่สอง"));
            Check("T1d", "the renamed file shows the real change (one added line), not a whole-file add",
                renDiff.Count(l => l.Kind == '+') == 1 && renDiff.Any(l => l.Content == "line6"), string.Join("\n", renDiff.Select(l => l.Kind + l.Content)));
            var firstFiles = await tm.GetCommitFilesAsync(first);
            var amp = await tm.GetFileDiffAsync(first, "a & b.txt");
            Check("T1e", "a name with & still shows its diff (it used to be stripped from the path)", firstFiles.Any(f => f.Path == "a & b.txt") && amp.Count(l => l.Kind == '+') == 5);
        });

        await Case("T2", async () =>
        {
            File.WriteAllText(Path.Combine(repo, "dirty.txt"), "work in progress");
            string blocker = Path.Combine(repo, ".git", "refs", "heads", "snapshot");
            File.WriteAllText(blocker, "");   // a FILE named refs/heads/snapshot: "git branch snapshot/…" must fail
            var r = await tm.RewindToCommitAsync(first, createSnapshot: true);
            Check("T2", "if the snapshot branch can't be made, nothing is rewound or stashed",
                !r.Success && Git(repo, "rev-parse", "HEAD") == second && File.Exists(Path.Combine(repo, "dirty.txt")), r.Message);
            File.Delete(blocker);
            var ok = await tm.RewindToCommitAsync(first, createSnapshot: true);
            Check("T2b", "with a snapshot possible: rewound, snapshot branch at the old HEAD, work stashed",
                ok.Success && Git(repo, "rev-parse", "HEAD") == first && ok.SnapshotBranch != null
                && Git(repo, "rev-parse", ok.SnapshotBranch) == second && ok.StashRef != null && ok.Message.Contains(ok.SnapshotBranch),
                ok.Message);
        });
        fs.WorkingDirectory = Tmp;
    }

    // ════════════════════════════ SETTINGS ════════════════════════════

    static void SettingsTests()
    {
        var shared = Sp.GetRequiredService<SettingsService>();
        string path = Path.Combine(shared.DataRoot, "settings.json");
        shared.Save();
        string original = File.ReadAllText(path);
        try
        {
            CaseSync("S1", () =>
            {
                var node = JsonNode.Parse(original)!;
                string foreignBlob = "ENC:" + Convert.ToBase64String(Enumerable.Range(0, 180).Select(i => (byte)(i * 7)).ToArray());
                node["HuggingFaceToken"] = foreignBlob;
                File.WriteAllText(path, node.ToJsonString());

                var s = new SettingsService();
                Check("S1", "a key saved on another PC loads empty and is reported", s.Settings.HuggingFaceToken is null or ""
                    && s.UndecryptableSecrets.Contains("HuggingFaceToken"));
                s.UpdateSettings(x => x.Temperature = 0.33f);   // any unrelated save
                string after = (string?)JsonNode.Parse(File.ReadAllText(path))!["HuggingFaceToken"] ?? "";
                Check("S1b", "an unrelated save writes the ORIGINAL blob back (it used to be overwritten with \"\")", after == foreignBlob, after);
                s.UpdateSettings(x => x.HuggingFaceToken = "hf_new_token_value");
                string replaced = (string?)JsonNode.Parse(File.ReadAllText(path))!["HuggingFaceToken"] ?? "";
                Check("S1c", "typing a new key replaces the blob and clears the notice",
                    replaced.StartsWith("ENC:") && replaced != foreignBlob && s.UndecryptableSecrets.Count == 0);
            });
            CaseSync("S2", () =>
            {
                var s = new SettingsService();
                File.SetAttributes(path, FileAttributes.ReadOnly);
                bool ok;
                try { ok = s.UpdateSettings(x => x.Temperature = 0.44f); }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
                Check("S2", "a save that cannot be written returns false with a reason (it used to report success)", !ok && s.LastSaveError != null, s.LastSaveError ?? "");
            });
            CaseSync("S3", () =>
            {
                var s = new SettingsService();
                string ghost = Path.Combine(Tmp, "ghost-models-" + Guid.NewGuid().ToString("N")[..6]);
                s.UpdateSettings(x => x.ModelDirectory = ghost);
                Check("S3", "saving a folder path no longer creates the folder (typed half-paths like D:\\Mod used to appear)", !Directory.Exists(ghost));
            });
            CaseSync("S4", () => SparkContextReloadTest(shared));
        }
        finally { File.WriteAllText(path, original); shared.Load(); }
    }

    /// <summary>
    /// The Settings page ignores the SettingsChanged event of its own save. Leaving the DGX Spark gives the context
    /// window back from INSIDE that save (a SettingsChanged handler writing in turn), so the page kept showing 32768
    /// while the setting was 12288. The real page (the app's singleton) and the real DgxSparkService, no network:
    /// the hosts are .invalid and the page is saved through its own Save().
    /// </summary>
    static void SparkContextReloadTest(SettingsService shared)
    {
        _ = Sp.GetRequiredService<DgxSparkService>();   // listens for "left the Spark", as in the app
        var vm = Sp.GetRequiredService<SettingsViewModel>();
        var save = typeof(SettingsViewModel).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var load = typeof(SettingsViewModel).GetMethod("LoadPageValues", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // On the Spark: Ollama active, its address the remembered Spark, the window raised to 32k (12k remembered).
        shared.UpdateSettings(x =>
        {
            x.ActiveProvider = AiProviderType.Ollama;
            x.DgxSparkHost = "spark-s4.invalid";
            x.ProviderConfigs["Ollama"] = new ProviderConfig { BaseUrl = "http://spark-s4.invalid:11434", SelectedModel = "llama3" };
            x.ContextSize = 32768;
            x.DgxSparkSavedContextSize = 12288;
        });
        load.Invoke(vm, new object[] { shared.Settings, true });   // the page as it would show after that change
        if (vm.SelectedProvider == AiProviderType.Ollama) vm.SelectedProvider = AiProviderType.Local;
        vm.SelectedProvider = AiProviderType.Ollama;               // loads the Ollama card from the settings
        Check("S4", "the page shows the Spark's 32k before the edit", vm.ContextSize == 32768 && vm.ProviderBaseUrl == "http://spark-s4.invalid:11434",
            $"{vm.ContextSize} {vm.ProviderBaseUrl}");

        // Control: an own save nothing else reacts to does not reload the page (a value changed behind its back,
        // with no event, stays as the page shows it).
        int pageMaxTokens = vm.MaxTokens;
        shared.Settings.MaxTokens = pageMaxTokens + 1;   // in memory only — no SettingsChanged
        vm.Temperature = vm.Temperature == 0.5f ? 0.6f : 0.5f;
        save.Invoke(vm, null);
        Check("S4b", "an own save with no outside change does not reload the page (control)", vm.MaxTokens == pageMaxTokens,
            $"page MaxTokens {vm.MaxTokens}, settings {shared.Settings.MaxTokens}");
        shared.Settings.MaxTokens = pageMaxTokens;

        // Ollama re-pointed at this PC on the page, then saved: the window is given back inside that save.
        vm.ProviderBaseUrl = "http://127.0.0.1:11434";
        save.Invoke(vm, null);
        Check("S4c", "saving the Ollama address off the Spark gives the window back in the settings", shared.Settings.ContextSize == 12288
            && shared.Settings.DgxSparkSavedContextSize == null, $"{shared.Settings.ContextSize} saved={shared.Settings.DgxSparkSavedContextSize}");
        Check("S4d", "…and the page shows it (it used to keep showing 32768)", vm.ContextSize == 12288, vm.ContextSize.ToString());
    }

    // ════════════════════════════ TASKS ════════════════════════════

    static async Task TaskTests()
    {
        var fs = Sp.GetRequiredService<FileSystemService>();
        string proj = Path.Combine(Tmp, "taskproj");
        Directory.CreateDirectory(proj);
        fs.WorkingDirectory = proj;
        var svc = new TaskManagerService(fs);

        await Case("K1", async () =>
        {
            int id = svc.CreateTask("cwd", "cd");
            var t = await WaitTask(svc, id);
            Check("K1", "tasks run in the open project folder (they used to run in %USERPROFILE%)",
                t.Output.Contains(proj, StringComparison.OrdinalIgnoreCase), t.Output);
        });
        await Case("K2", async () =>
        {
            int id = svc.CreateTask("chatty", "for /L %i in (1,1,40000) do @echo line %i of a chatty task");
            var t = await WaitTask(svc, id, 120);
            Check("K2", "a chatty task keeps only the newest output (bounded) and its last line is there",
                t.Status == "completed" && t.Output.Length <= TaskManagerService.MaxOutputChars + 16 && t.Output.Contains("line 40000 of"),
                $"status={t.Status} len={t.Output.Length}");
        });
        fs.WorkingDirectory = Tmp;
    }

    static async Task<TaskInfo> WaitTask(TaskManagerService svc, int id, int seconds = 30)
    {
        var t = svc.Tasks.First(x => x.Id == id);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (t.Status == "running" && DateTime.UtcNow < until) await Task.Delay(100);
        await Task.Delay(400);
        svc.FlushPendingOutput();
        return t;
    }
}
