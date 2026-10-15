using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;

namespace ApiTest;

/// <summary>
/// mcp_servers.json is the user's file — every MCP server they set up and the API keys in each env block.
/// It was lost once: a trailing comma made the strict load fail silently, the startup BrainX auto-connect then
/// "saved", and the file came back holding only brainx-brain. This runs the real McpServerManager (and the real
/// MCP page / BrainX window view-models) through the startup path, the page's actions and the supervisor against
/// a portable data folder and proves: a file that can't be read is never written (bytes compared) and the page
/// says so even though it is built before the load; a fixed file is read again by Refresh or an explicit Connect
/// (and Refresh then starts what startup never got to); hand-written JSON loads; unknown fields and unreadable
/// entries survive; a page action writes only its own server, so outside edits to the others survive; the first
/// write of a session (and any write over comments) backs the file up; "can't be written" is told apart from
/// "can't be read"; the chip, the BrainX window and the page agree during a respawn; supervisor messages offer the
/// 🧠 chip only for the brain.
///
/// BrainX is never started: the Debug-only override CLUADEX_BRAINX_EXE points the installed-BrainX lookup at a
/// stub file that is not a program, and the section stops before creating any manager if the override is not
/// honoured (a Release build would find the real BrainX) or the data folder is not the portable one.
/// The "servers" that do run are this harness itself in a child mode (<see cref="SleepStubFlag"/>,
/// <see cref="ServerStubFlag"/>) — never anything else.
/// </summary>
internal static class McpConfigSafety
{
    /// <summary>The stand-in "server" for the starting-state check: this harness, sleeping, never answering.</summary>
    public const string SleepStubFlag = "--mcp-stub-sleep";
    /// <summary>A minimal MCP server over stdio (this harness): answers initialize + tools/list, then "crashes".</summary>
    public const string ServerStubFlag = "--mcp-stub-server";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · mcp_servers.json safety + BrainX startup auto-connect (stub, never the real BrainX)");

        // ── Guard 0: portable mode FIRST. This section deletes and rewrites mcp_servers.json; outside the
        //    portable data folder that is the user's real file. Checked before SettingsService is even built
        //    (its non-portable constructor migrates and creates folders under the real profile). ──
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!File.Exists(Path.Combine(exeDir, "portable.txt")) && !File.Exists(Path.Combine(exeDir, "portable")))
        {
            check("mcpcfg", "STOPPED: not portable (no portable.txt next to the harness)", false, exeDir);
            return;
        }
        var svc = new SettingsService();   // portable.txt → <exe>\Data, never ~/.cluadex
        if (!svc.IsPortable || !svc.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            check("mcpcfg", "STOPPED: not portable", false, svc.DataRoot);
            return;
        }
        check("mcpcfg", "portable data folder (never the real ~/.cluadex)", true, svc.DataRoot);

        var loc = new LocalizationService();
        loc.SetLanguage("en");
        Directory.CreateDirectory(svc.DataRoot);
        string path = Path.Combine(svc.DataRoot, "mcp_servers.json");
        string bak = path + ".bak";
        string tmp = path + ".tmp";
        string stubDir = Path.Combine(Path.GetTempPath(), "cluadex-apitest-brainx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stubDir);
        string stub = Path.Combine(stubDir, "brainx-mcp.exe");
        File.WriteAllText(stub, "not a program: CluadeX apitest stand-in for BrainX's MCP exe\r\n");
        string self = Environment.ProcessPath!;

        bool startDeclined = svc.Settings.BrainXAutoConnectDeclined;
        bool startDismissed = svc.Settings.BrainXInviteDismissed;
        string prevOverride = Environment.GetEnvironmentVariable("CLUADEX_BRAINX_EXE");
        Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", stub);

        void Fresh(string text)
        {
            foreach (var f in new[] { path, bak, tmp }) if (File.Exists(f)) File.Delete(f);
            File.WriteAllText(path, text);
        }
        JsonObject Disk() => JsonNode.Parse(File.ReadAllText(path))!.AsObject();   // strict: the app writes plain JSON
        JsonObject DiskServers() => Disk()["mcpServers"]!.AsObject();
        bool SameBytes(string file, byte[] want) => File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(want);
        static async Task<bool> WaitUntil(Func<bool> cond, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!cond() && DateTime.UtcNow < until) await Task.Delay(20);
            return cond();
        }

        try
        {
            // ── Guard: the override must be in effect before anything that could start a server ──
            string found = McpServerManager.FindInstalledBrainExe();
            check("mcpcfg", "BrainX lookup answers with the stub (Debug override honoured)", found == stub, found ?? "null");
            if (found != stub)
            {
                check("mcpcfg", "STOPPED: override not in effect — auto-connect not run (it would start the real BrainX)", false, found ?? "null");
                return;
            }
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", Path.Combine(stubDir, "missing.exe"));
            string missing = McpServerManager.FindInstalledBrainExe();
            check("mcpcfg", "override read per call; a missing stub = not installed, never the real install", missing == null, missing ?? "null");
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", stub);

            // ── 1. A file that can't be read is never written — and the page, built BEFORE the load, says so ──
            const string broken = """
                {
                  "mcpServers": {
                    "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "env": { "GITHUB_TOKEN": "ghp_apitest_not_real" }, "enabled": false }
                    "search": { "command": "uvx", "enabled": false }
                  }
                }
                """;   // a missing comma between two entries: broken in any JSON reader
            // What the user puts there once they've fixed it: one enabled server (a command that doesn't exist, so
            // a start fails at once and nothing runs), one disabled, an API key that must survive every write.
            const string fixedText = """
                {
                  "mcpServers": {
                    "local": { "command": "cluadex-apitest-no-such-cmd", "args": ["--x"], "env": { "API_KEY": "k-123" }, "enabled": true },
                    "off": { "command": "cluadex-apitest-no-such-cmd", "enabled": false }
                  }
                }
                """;
            Fresh(broken);
            byte[] brokenBytes = File.ReadAllBytes(path);
            svc.UpdateSettings(s => s.BrainXAutoConnectDeclined = false);
            using (var mgr = new McpServerManager(svc))
            {
                // The app builds the MCP page with MainViewModel, before App.OnStartup runs the load.
                var vm = new McpServersViewModel(mgr, loc);
                string beforeLoad = vm.StatusText;
                await mgr.InitializeAsync();
                var auto = await mgr.AutoConnectInstalledBrainAsync();
                check("mcpcfg", "broken file: load reported as failed", mgr.ConfigLoadFailed, "");
                check("mcpcfg", "broken file: the page built before the load shows the notice once it fails",
                    beforeLoad != loc.T("mcp.config.unreadable") && vm.StatusText == loc.T("mcp.config.unreadable"),
                    $"{beforeLoad} → {vm.StatusText}");
                check("mcpcfg", "broken file: startup auto-connect skipped", auto == McpServerManager.BrainAutoConnect.SkippedConfigUnreadable, auto.ToString());
                check("mcpcfg", "broken file: no brain registered in memory", mgr.BrainServerName == null && mgr.Configs.Count == 0,
                    $"brain={mgr.BrainServerName ?? "none"} configs={mgr.Configs.Count}");
                check("mcpcfg", "broken file: SaveConfig refuses, as \"unreadable\"",
                    !mgr.SaveConfig("github") && mgr.LastConfigWriteProblem == McpServerManager.ConfigWriteProblem.Unreadable,
                    mgr.LastConfigWriteProblem.ToString());
                bool connected = await mgr.ConnectInstalledBrainAsync();
                check("mcpcfg", "broken file: an explicit Connect reads it again, still refuses, with a reason",
                    !connected && mgr.ConfigLoadFailed && mgr.LastBrainConnectProblem == McpServerManager.BrainConnectProblem.ConfigUnreadable,
                    mgr.LastBrainConnectProblem.ToString());
                string reason = loc.T("brainx.reasonConfig");
                check("mcpcfg", "the BrainX window's reason says to fix the file and try again / press Refresh",
                    reason.Contains("try again") && reason.Contains("Refresh"), reason);

                vm.NewServerName = "added-while-broken";
                vm.NewServerCommand = "cluadex-apitest-no-such-cmd";
                vm.AddServerCommand.Execute(null);
                check("mcpcfg", "broken file: Add on the MCP page changes nothing",
                    !mgr.Configs.ContainsKey("added-while-broken") && vm.StatusText == loc.T("mcp.config.unreadable"), vm.StatusText);

                // Negative control for Refresh: the file is still broken, so the re-read fails and nothing moves.
                await vm.RefreshAsync();
                check("mcpcfg", "broken file: Refresh while still broken keeps the notice, starts nothing",
                    mgr.ConfigLoadFailed && vm.StatusText == loc.T("mcp.config.unreadable") && mgr.GetRunningServers().Count == 0, vm.StatusText);
                vm.Dispose();
            }
            check("mcpcfg", "broken file: bytes on disk unchanged", SameBytes(path, brokenBytes),
                $"{brokenBytes.Length} → {new FileInfo(path).Length} bytes");
            check("mcpcfg", "broken file: no backup / temp file written (nothing was written at all)", !File.Exists(bak) && !File.Exists(tmp), "");

            // ── 1b. Fixed by hand while CluadeX runs → Connect in the BrainX window reads it again ──
            Fresh(broken);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                var vm = new McpServersViewModel(mgr, loc);
                File.WriteAllText(path, fixedText);   // the user fixes the file
                byte[] fixedBytes = File.ReadAllBytes(path);

                var auto = await mgr.AutoConnectInstalledBrainAsync();
                check("mcpcfg", "fixed by hand: the automatic startup path does not re-read on its own (negative control)",
                    auto == McpServerManager.BrainAutoConnect.SkippedConfigUnreadable && mgr.ConfigLoadFailed && SameBytes(path, fixedBytes),
                    auto.ToString());

                bool up = await mgr.ConnectInstalledBrainAsync();
                check("mcpcfg", "fixed by hand: Connect in the BrainX window reads it again — the flag clears",
                    !mgr.ConfigLoadFailed && mgr.Configs.ContainsKey("local") && mgr.Configs.ContainsKey("off"), string.Join(",", mgr.Configs.Keys));
                check("mcpcfg", "fixed by hand: … and registers BrainX (the stub's start fails: StartFailed, not \"unreadable\")",
                    !up && mgr.LastBrainConnectProblem == McpServerManager.BrainConnectProblem.StartFailed && mgr.Configs.ContainsKey("brainx-brain"),
                    mgr.LastBrainConnectProblem.ToString());
                var srv = DiskServers();
                check("mcpcfg", "fixed by hand: written by merging — the user's servers and key are still there",
                    srv.ContainsKey("brainx-brain") && srv.ContainsKey("off") && (string)srv["local"]!["env"]!["API_KEY"] == "k-123",
                    string.Join(",", srv.Select(p => p.Key)));
                check("mcpcfg", "fixed by hand: the page hears it from the BrainX window — notice gone, names what isn't started",
                    vm.StatusText == loc.T("mcp.config.reloadedNotStarted", "local"), vm.StatusText);
                check("mcpcfg", "fixed by hand: the page lists the servers without a Refresh",
                    vm.Servers.Select(s => s.Name).OrderBy(n => n).SequenceEqual(new[] { "brainx-brain", "local", "off" }),
                    string.Join(",", vm.Servers.Select(s => s.Name)));

                await vm.RefreshAsync();
                check("mcpcfg", "Refresh starts the enabled server startup never got to (and not the disabled one)",
                    mgr.GetServerState("local") == McpServerState.Failed && mgr.GetServerState("off") == McpServerState.Stopped
                    && mgr.ServersAwaitingStartAfterReload.Count == 0,
                    $"local={mgr.GetServerState("local")} off={mgr.GetServerState("off")}");
                check("mcpcfg", "Refresh: the status line says the file was read again", vm.StatusText == loc.T("mcp.config.reloaded", 3, 0), vm.StatusText);
                vm.Dispose();
            }

            // ── 1c. Fixed by hand → Refresh on the MCP page: re-read, start, and retry the skipped BrainX registration ──
            Fresh(broken);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                var skipped = await mgr.AutoConnectInstalledBrainAsync();
                var vm = new McpServersViewModel(mgr, loc);
                File.WriteAllText(path, fixedText);
                await vm.RefreshAsync();
                check("mcpcfg", "Refresh re-reads a fixed file (no restart needed)",
                    skipped == McpServerManager.BrainAutoConnect.SkippedConfigUnreadable && !mgr.ConfigLoadFailed && mgr.Configs.ContainsKey("local"),
                    $"{skipped} · failed={mgr.ConfigLoadFailed}");
                check("mcpcfg", "Refresh starts the enabled servers (the disabled one stays stopped)",
                    mgr.GetServerState("local") == McpServerState.Failed && mgr.GetServerState("off") == McpServerState.Stopped,
                    $"local={mgr.GetServerState("local")} off={mgr.GetServerState("off")}");
                var srv = DiskServers();
                check("mcpcfg", "Refresh retries the BrainX registration startup skipped — merged in, key kept",
                    mgr.Configs.ContainsKey("brainx-brain") && srv.ContainsKey("brainx-brain") && (string)srv["local"]!["env"]!["API_KEY"] == "k-123",
                    string.Join(",", srv.Select(p => p.Key)));
                check("mcpcfg", "Refresh: status line", vm.StatusText == loc.T("mcp.config.reloaded", 3, 0), vm.StatusText);
                vm.Dispose();
            }

            // ── 1d. Negative control for the page notice: a good file loaded after the page was built ──
            Fresh(fixedText.Replace("\"enabled\": true", "\"enabled\": false"));
            using (var mgr = new McpServerManager(svc))
            {
                var vm = new McpServersViewModel(mgr, loc);
                await mgr.InitializeAsync();
                check("mcpcfg", "a good file: the page built before the load shows the servers, not a notice",
                    vm.Servers.Count == 2 && vm.StatusText == loc.T("mcpm.summary", 2, 0), $"{vm.Servers.Count} · {vm.StatusText}");
                vm.Dispose();
            }

            // ── 2. Hand-written JSON loads; unknown fields and an unreadable entry survive a save ──
            const string handWritten = """
                {
                  // written by hand: comments and trailing commas are what people actually write
                  "globalShortcut": "Ctrl+Space",
                  "mcpServers": {
                    "remote": { "type": "http", "url": "https://example.invalid/mcp", "enabled": false, },
                    "local": { "command": "cluadex-apitest-no-such-cmd", "args": ["--x"], "cwd": "C:\\work", "env": { "API_KEY": "k-123" }, "enabled": false, "timeout": 30 },
                    "typo": { "command": "x", "enabled": "yes" },
                  },
                }
                """;
            Fresh(handWritten);
            byte[] handBytes = File.ReadAllBytes(path);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                check("mcpcfg", "comments + trailing commas: file loads", !mgr.ConfigLoadFailed
                    && mgr.Configs.ContainsKey("remote") && mgr.Configs.ContainsKey("local"), string.Join(",", mgr.Configs.Keys));
                check("mcpcfg", "an entry with a wrong type is reported, not dropped silently",
                    mgr.UnreadableServers.SequenceEqual(new[] { "typo" }), string.Join(",", mgr.UnreadableServers));
                check("mcpcfg", "its name can't be reused by Add (would overwrite it)", mgr.IsServerNameTaken("typo"), "");

                mgr.Configs["local"].Args.Add("--y");
                mgr.Configs["local"].Enabled = true;   // in memory only; not started
                check("mcpcfg", "a user save succeeds", mgr.SaveConfig("local"), "");
            }
            JsonObject saved = Disk();
            var servers = saved["mcpServers"]!.AsObject();
            var remote = servers["remote"]!.AsObject();
            var local = servers["local"]!.AsObject();
            var typo = servers["typo"]!.AsObject();
            check("mcpcfg", "round trip: top-level unknown setting kept", (string)saved["globalShortcut"] == "Ctrl+Space", saved.ToJsonString());
            check("mcpcfg", "round trip: \"type\" + \"url\" kept", (string)remote["type"] == "http" && (string)remote["url"] == "https://example.invalid/mcp", remote.ToJsonString());
            check("mcpcfg", "round trip: nothing invented for a server without a command",
                !remote.ContainsKey("command") && !remote.ContainsKey("args") && remote.Count == 3, remote.ToJsonString());
            check("mcpcfg", "round trip: \"cwd\" + \"timeout\" kept", (string)local["cwd"] == @"C:\work" && (int)local["timeout"] == 30, local.ToJsonString());
            check("mcpcfg", "round trip: env key kept, edit applied",
                (string)local["env"]!["API_KEY"] == "k-123" && (bool)local["enabled"]
                && local["args"]!.AsArray().Select(a => (string)a).SequenceEqual(new[] { "--x", "--y" }), local.ToJsonString());
            check("mcpcfg", "round trip: unreadable entry left exactly as written",
                typo.ToJsonString() == """{"command":"x","enabled":"yes"}""", typo.ToJsonString());
            check("mcpcfg", "the first save of a session backs the file up first — comments and all",
                SameBytes(bak, handBytes) && File.ReadAllText(bak).Contains("// written by hand") && !File.Exists(tmp), "");

            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                mgr.RemoveConfig("remote");
                bool ok = mgr.SaveConfig("remote");
                var after = DiskServers();
                check("mcpcfg", "remove deletes exactly that server",
                    ok && !after.ContainsKey("remote") && after.ContainsKey("local") && after.ContainsKey("typo"), string.Join(",", after.Select(p => p.Key)));
            }

            // ── 5. .bak: once per session, again when someone has put comments in since ──
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                byte[] sessionStart = File.ReadAllBytes(path);
                mgr.Configs["local"].Enabled = false;
                bool w1 = mgr.SaveConfig("local");
                check("mcpcfg", ".bak: the first write of a session copies the file as the session found it", w1 && SameBytes(bak, sessionStart), "");
                mgr.Configs["local"].Enabled = true;
                bool w2 = mgr.SaveConfig("local");
                check("mcpcfg", ".bak: a second write leaves it alone (not replaced by this session's own version)", w2 && SameBytes(bak, sessionStart), "");
                string commented = "// a note added by hand while CluadeX runs\n" + File.ReadAllText(path);
                File.WriteAllText(path, commented);
                mgr.Configs["local"].Enabled = false;
                bool w3 = mgr.SaveConfig("local");
                check("mcpcfg", ".bak: a file with comments is copied again before the save drops them",
                    w3 && File.ReadAllText(bak) == commented && !File.ReadAllText(path).Contains("a note added by hand"), "");
                // A "//" inside a string value is not a comment: no backup for that (negative control).
                var withUrl = Disk();
                withUrl["homepage"] = "https://example.invalid/x";
                File.WriteAllText(path, withUrl.ToJsonString());
                byte[] bakBefore = File.ReadAllBytes(bak);
                mgr.Configs["local"].Enabled = true;
                bool w4 = mgr.SaveConfig("local");
                check("mcpcfg", ".bak: a \"//\" inside a string (a URL) is not taken for a comment", w4 && SameBytes(bak, bakBefore), "");
            }

            // ── 4. A page action writes only its own server — outside edits to the others survive ──
            Fresh("""
                {
                  "mcpServers": {
                    "a": { "command": "cluadex-apitest-no-such-cmd", "args": ["1"], "enabled": false },
                    "b": { "command": "cluadex-apitest-no-such-cmd", "args": ["2"], "env": { "API_KEY": "kb" }, "enabled": false }
                  }
                }
                """);
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                var vm = new McpServersViewModel(mgr, loc);
                // Another program (an installer, an editor) changes b and adds c while CluadeX runs.
                var outside = Disk();
                outside["mcpServers"]!["b"]!["args"] = new JsonArray("2", "outside");
                outside["mcpServers"]!["c"] = new JsonObject { ["command"] = "z", ["enabled"] = false };
                File.WriteAllText(path, outside.ToJsonString());

                vm.SelectedServer = vm.Servers.First(s => s.Name == "a");
                vm.ToggleEnabledCommand.Execute(null);
                var d1 = DiskServers();
                check("mcpcfg", "Toggle writes only that server — another server's outside edit survives",
                    (bool)d1["a"]!["enabled"] && d1["b"]!["args"]!.AsArray().Select(x => (string)x).SequenceEqual(new[] { "2", "outside" })
                    && d1.ContainsKey("c") && (string)d1["b"]!["env"]!["API_KEY"] == "kb", d1.ToJsonString());

                vm.NewServerName = "c";
                vm.NewServerCommand = "cluadex-apitest-no-such-cmd";
                vm.AddServerCommand.Execute(null);
                check("mcpcfg", "Add refuses a name another program put in the file since the load",
                    vm.StatusText == loc.T("mcpm.nameTaken", "c") && (string)DiskServers()["c"]!["command"] == "z", vm.StatusText);

                vm.NewServerName = "d";
                vm.NewServerCommand = "cluadex-apitest-no-such-cmd";
                vm.AddServerCommand.Execute(null);
                var d2 = DiskServers();
                check("mcpcfg", "Add writes only the new server",
                    d2.ContainsKey("d") && d2["b"]!["args"]!.AsArray().Count == 2 && (string)d2["b"]!["args"]![1] == "outside", d2.ToJsonString());

                // Remove (the page's confirm dialog can't run headless — the same two calls it makes after "Yes").
                mgr.RemoveConfig("a");
                mgr.RemoveConfig("d");
                bool r1 = mgr.SaveConfig("a");
                var d3 = DiskServers();
                check("mcpcfg", "a removal is written only when named: a gone, d (not named yet) still there, b's edit kept",
                    r1 && !d3.ContainsKey("a") && d3.ContainsKey("d") && (string)d3["b"]!["args"]![1] == "outside", string.Join(",", d3.Select(p => p.Key)));
                bool r2 = mgr.SaveConfig("d");
                check("mcpcfg", "… and the other removal when it is named", r2 && !DiskServers().ContainsKey("d"), "");

                // Negative control: an edit the user makes to b on the page IS written over the outside one.
                vm.SelectedServer = vm.Servers.First(s => s.Name == "b");
                vm.BeginEditCommand.Execute(null);
                vm.SelectedServer!.ArgsText = "3";
                vm.SaveEditCommand.Execute(null);
                var d4 = DiskServers();
                check("mcpcfg", "editing b on the page writes b (env kept)",
                    d4["b"]!["args"]!.AsArray().Select(x => (string)x).SequenceEqual(new[] { "3" }) && (string)d4["b"]!["env"]!["API_KEY"] == "kb",
                    d4["b"]!.ToJsonString());
                vm.Dispose();
            }

            // ── 6. Read fine, can't be written (held open by another program) — not "unreadable" ──
            const string valid = """
                {
                  "mcpServers": {
                    "local": { "command": "cluadex-apitest-no-such-cmd", "args": ["--x"], "cwd": "C:\\work", "env": { "API_KEY": "k-123" }, "enabled": false }
                  }
                }
                """;
            Fresh(valid);
            byte[] lockedBytes = File.ReadAllBytes(path);
            svc.UpdateSettings(s => s.BrainXAutoConnectDeclined = false);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                var vm = new McpServersViewModel(mgr, loc);
                vm.SelectedServer = vm.Servers.First(s => s.Name == "local");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, can't be replaced
                {
                    vm.ToggleEnabledCommand.Execute(null);
                    check("mcpcfg", "locked file: Toggle says \"could not be saved\", not \"could not be read\"",
                        vm.StatusText == loc.T("mcp.config.saveFailed") && !mgr.ConfigLoadFailed
                        && mgr.LastConfigWriteProblem == McpServerManager.ConfigWriteProblem.NotSaved,
                        $"{mgr.LastConfigWriteProblem} · {vm.StatusText}");
                    var auto = await mgr.AutoConnectInstalledBrainAsync();
                    check("mcpcfg", "locked file: BrainX not registered, with its own reason",
                        auto == McpServerManager.BrainAutoConnect.SkippedConfigNotSaved
                        && mgr.LastBrainConnectProblem == McpServerManager.BrainConnectProblem.ConfigNotSaved && mgr.BrainServerName == null,
                        $"{auto} · {mgr.LastBrainConnectProblem}");

                    var invite = new BrainXInviteViewModel(mgr, svc, loc);
                    invite.PrimaryCommand.Execute(null);
                    await WaitUntil(() => !invite.IsBusy && invite.Status != loc.T("brainx.connecting"), 10_000);
                    string want = loc.T("brainx.connectFailed", loc.T("brainx.reasonSave"));
                    check("mcpcfg", "locked file: the BrainX window says \"could not be saved\"",
                        invite.Status == want && invite.StatusIsError, invite.Status);
                    invite.Detach();
                }
                check("mcpcfg", "locked file: bytes unchanged, no temp file left", SameBytes(path, lockedBytes) && !File.Exists(tmp), "");
                // Negative control: once the other program lets go, the same save goes through.
                bool ok = mgr.SaveConfig("local");
                check("mcpcfg", "released: the same save now works", ok && mgr.LastConfigWriteProblem == McpServerManager.ConfigWriteProblem.None
                    && (bool)DiskServers()["local"]!["enabled"] == mgr.Configs["local"].Enabled, mgr.LastConfigWriteProblem.ToString());
                vm.Dispose();
            }

            // ── 3. Declined: an installed-but-unlisted BrainX is not registered at startup ──
            Fresh(valid);
            byte[] validBytes = File.ReadAllBytes(path);
            svc.UpdateSettings(s => s.BrainXAutoConnectDeclined = true);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                var state = mgr.GetBrainInstallState();
                var auto = await mgr.AutoConnectInstalledBrainAsync();
                check("mcpcfg", "declined: the stub counts as installed-not-listed", state == McpServerManager.BrainInstall.InstalledNotConfigured, state.ToString());
                check("mcpcfg", "declined: startup auto-connect skipped", auto == McpServerManager.BrainAutoConnect.SkippedDeclined, auto.ToString());
                check("mcpcfg", "declined: nothing registered", mgr.BrainServerName == null, mgr.BrainServerName ?? "");
            }
            check("mcpcfg", "declined: file bytes unchanged, no backup", SameBytes(path, validBytes) && !File.Exists(bak), "");

            // "Not interested" in the BrainX window sets the same flag (and still hides the invite).
            svc.UpdateSettings(s => { s.BrainXAutoConnectDeclined = false; s.BrainXInviteDismissed = false; });
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                var invite = new BrainXInviteViewModel(mgr, svc, loc);
                invite.NotInterestedCommand.Execute(null);
                invite.Detach();
                check("mcpcfg", "\"Not interested\" declines the auto-connect too",
                    svc.Settings.BrainXAutoConnectDeclined && svc.Settings.BrainXInviteDismissed, "");
            }

            // ── 4b. Not declined: registration merges into the file and backs it up first ──
            Fresh(valid);
            svc.UpdateSettings(s => s.BrainXAutoConnectDeclined = false);
            using (var mgr = new McpServerManager(svc))
            {
                await mgr.InitializeAsync();
                var auto = await mgr.AutoConnectInstalledBrainAsync();   // starts the stub: not a program, fails at once
                check("mcpcfg", "registered: the stub (never the real BrainX) was the command",
                    mgr.Configs.TryGetValue("brainx-brain", out var b) && b.Command == stub, mgr.BrainServerName ?? "none");
                check("mcpcfg", "registered: start of the stub reported as failed, with a reason",
                    auto == McpServerManager.BrainAutoConnect.Failed && mgr.LastBrainConnectProblem == McpServerManager.BrainConnectProblem.StartFailed,
                    $"{auto} · {mgr.LastBrainConnectProblem}");
            }
            check("mcpcfg", "registered: .bak holds the file exactly as it was", SameBytes(bak, validBytes), "");
            var merged = DiskServers();
            var keptLocal = merged["local"]?.AsObject();
            var brainEnv = merged["brainx-brain"]?["env"] as JsonObject;
            check("mcpcfg", "registered: merged in — the other server and its key are still there",
                merged.ContainsKey("brainx-brain") && keptLocal != null && (string)keptLocal["env"]!["API_KEY"] == "k-123"
                && (string)keptLocal["cwd"] == @"C:\work", string.Join(",", merged.Select(p => p.Key)));
            check("mcpcfg", "registered: only BRAINX_* settings copied into its env",
                brainEnv == null || brainEnv.All(p => p.Key.StartsWith("BRAINX_", StringComparison.OrdinalIgnoreCase)),
                brainEnv == null ? "(no env)" : string.Join(",", brainEnv.Select(p => p.Key)));

            // ── 5b. A brain that is still starting is waited for, not started a second time ──
            Fresh(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["brainx-brain"] = new JsonObject
                    {
                        ["command"] = self,
                        ["args"] = new JsonArray(SleepStubFlag, "2500"),
                        ["enabled"] = false,
                    },
                },
            }.ToJsonString());
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                int starts = 0;
                mgr.OnServerLog += (_, m) => { if (m.StartsWith("Starting:", StringComparison.Ordinal)) Interlocked.Increment(ref starts); };
                var first = mgr.StartServerAsync("brainx-brain");
                await WaitUntil(() => mgr.BrainState == McpServerState.Starting, 5000);
                var during = mgr.GetBrainInstallState();
                check("mcpcfg", "starting: reported as Starting, not \"set up but not running\"", during == McpServerManager.BrainInstall.Starting, during.ToString());
                var invite = new BrainXInviteViewModel(mgr, svc, loc);
                check("mcpcfg", "starting: the BrainX window says it is starting", invite.Subtitle == loc.T("brainx.subStarting"), invite.Subtitle);
                invite.Detach();

                var sw = Stopwatch.StartNew();
                bool up = await mgr.ConnectInstalledBrainAsync();
                await first;
                check("mcpcfg", "starting: Connect waits for it instead of starting a second copy", starts == 1,
                    $"starts={starts} · waited {sw.ElapsedMilliseconds} ms · up={up} · {mgr.BrainState}");
            }

            // ── 9. During a respawn the chip, the BrainX window and the page agree ──
            section("offline · MCP supervisor: respawn status + restart routes (stub MCP server = this harness)");
            string brainCounter = Path.Combine(stubDir, "brain-starts.txt");
            Fresh(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    // Serves once, "crashes" 400 ms after listing its tools; every respawn dies at once.
                    ["brainx-brain"] = new JsonObject
                    {
                        ["command"] = self,
                        ["args"] = new JsonArray(ServerStubFlag, brainCounter, "once", "400"),
                        ["enabled"] = true,
                    },
                },
            }.ToJsonString());
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                int starts = 0;
                mgr.OnServerLog += (n, m) => { if (n == "brainx-brain" && m.StartsWith("Starting:", StringComparison.Ordinal)) Interlocked.Increment(ref starts); };
                bool up = await mgr.StartServerAsync("brainx-brain");
                var tools = mgr.ToolRegistry.GetToolsForServer("brainx-brain").Select(t => t.Name).ToList();
                // The stub proves itself before anything else runs against it: this harness, its one tool.
                check("mcpcfg", "stub MCP server: this harness, answering with its one stub tool",
                    up && mgr.Configs["brainx-brain"].Command == self && tools.SequenceEqual(new[] { "stub_echo" }), string.Join(",", tools));
                if (up)
                {
                    // It dies; respawn 1 (after 1 s) dies at once; we are now in the 2 s gap before respawn 2.
                    bool inGap = await WaitUntil(() => Volatile.Read(ref starts) >= 2
                                                      && mgr.GetServerState("brainx-brain") == McpServerState.Reconnecting, 25_000);
                    string chip = mgr.BrainStatusText;
                    var install = mgr.GetBrainInstallState();
                    var shown = mgr.BrainState;
                    bool starting = mgr.IsBrainStarting;
                    var invite = new BrainXInviteViewModel(mgr, svc, loc);
                    string window = invite.Subtitle;
                    invite.Detach();
                    check("mcpcfg", "respawn gap: the chip says \"reconnecting n/5\", not DOWN",
                        inGap && chip.StartsWith("brain · reconnecting ", StringComparison.Ordinal) && chip.EndsWith("/5", StringComparison.Ordinal),
                        $"{chip} (starts={starts})");
                    check("mcpcfg", "respawn gap: chip and BrainX window agree (IsBrainStarting · \"starting\")",
                        starting && install == McpServerManager.BrainInstall.Starting && window == loc.T("brainx.subStarting"), $"{install} · {window}");
                    check("mcpcfg", "respawn gap: the page / tooltip state is Reconnecting, not Failed", shown == McpServerState.Reconnecting, shown.ToString());

                    // Negative control: Stop in the gap ends the respawning, and the chip says so.
                    await mgr.StopServerAsync("brainx-brain");
                    int atStop = Volatile.Read(ref starts);
                    check("mcpcfg", "stopped in the gap: the chip says stopped, not reconnecting",
                        mgr.BrainStatusText == "brain · stopped" && !mgr.IsBrainStarting, mgr.BrainStatusText);
                    await Task.Delay(2600);   // past the respawn that was due
                    check("mcpcfg", "stopped in the gap: not started again behind the user's back",
                        Volatile.Read(ref starts) == atStop && mgr.GetServerState("brainx-brain") == McpServerState.Stopped,
                        $"starts {atStop} → {starts} · {mgr.GetServerState("brainx-brain")}");
                }
            }

            // ── 7. Restart routes: the 🧠 chip only for the brain; the Thai text names the palette entry as it is ──
            string crashCounter = Path.Combine(stubDir, "crashy-starts.txt");
            Fresh(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    // Comes up every time and "crashes" 250 ms later: the crash-loop brake must stop it.
                    ["crashy"] = new JsonObject
                    {
                        ["command"] = self,
                        ["args"] = new JsonArray(ServerStubFlag, crashCounter, "always", "250"),
                        ["enabled"] = true,
                    },
                },
            }.ToJsonString());
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                string give = null;
                mgr.OnServerLog += (n, m) => { if (n == "crashy" && m.StartsWith("Died ", StringComparison.Ordinal)) give = m; };
                bool up = await mgr.StartServerAsync("crashy");
                await WaitUntil(() => give != null && mgr.GetServerState("crashy") == McpServerState.Failed, 30_000);
                check("mcpcfg", "crash loop on a non-brain server: stopped, says where to restart it",
                    up && give != null && give.Contains("(Ctrl+K → MCP Servers)") && mgr.GetServerState("crashy") == McpServerState.Failed,
                    give ?? "(no crash-loop message)");
                check("mcpcfg", "… without sending the user to the 🧠 chip (that is BrainX's)", give != null && !give.Contains("🧠"), give ?? "");
            }
            check("mcpcfg", "restart route: the brain also gets the 🧠 chip (negative control)",
                McpServerManager.RestartRoute("brainx-brain").Contains("🧠") && !McpServerManager.RestartRoute("github").Contains("🧠"),
                McpServerManager.RestartRoute("brainx-brain"));

            var th = new LocalizationService();
            th.SetLanguage("th");
            var routeKeys = new[] { "mcp.route.page", "mcp.route.pageOrChip", "brain.sync.notRunning", "brainx.connectFailed" };
            var badTh = routeKeys.Where(k => !th.T(k).Contains("Ctrl+K → MCP Servers") || th.T(k).Contains("Ctrl+K → เซิร์ฟเวอร์")).ToList();
            check("mcpcfg", "Thai routes name the palette entry as it appears (\"Ctrl+K → MCP Servers\")", badTh.Count == 0, string.Join(", ", badTh));
            var genericTh = new[] { ("mcp.supervisor.crashLoop", "{2}"), ("mcp.supervisor.gaveUp", "{1}") };
            check("mcpcfg", "supervisor messages carry the route as a slot, no 🧠 of their own (en + th)",
                genericTh.All(p => th.T(p.Item1).Contains(p.Item2) && !th.T(p.Item1).Contains("🧠")
                                   && loc.T(p.Item1).Contains(p.Item2) && !loc.T(p.Item1).Contains("🧠")), "");
            var newKeys = new[] { "mcp.route.pageOrChip", "mcp.config.saveFailed", "mcp.config.rereading", "mcp.config.reloaded",
                                  "mcp.config.reloadedNotStarted", "brainx.reasonConfig", "brainx.reasonSave" };
            var untranslated = newKeys.Where(k => th.T(k) == k || th.T(k) == loc.T(k)).ToList();
            check("mcpcfg", "every new / changed message has its own Thai text", untranslated.Count == 0, string.Join(", ", untranslated));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", prevOverride);
            svc.UpdateSettings(s => { s.BrainXAutoConnectDeclined = startDeclined; s.BrainXInviteDismissed = startDismissed; });
            foreach (var f in new[] { path, bak, tmp }) try { if (File.Exists(f)) File.Delete(f); } catch { }
            try { Directory.Delete(stubDir, recursive: true); } catch { }
        }
    }

    /// <summary>The child-process side of the starting-state check: sit there, answer nothing, exit.</summary>
    public static int SleepStub(string[] args)
    {
        int ms = args.Length > 1 && int.TryParse(args[1], out int v) ? Math.Clamp(v, 0, 30_000) : 2000;
        Thread.Sleep(ms);
        return 0;
    }

    /// <summary>
    /// The child-process side of the supervisor checks: a minimal MCP server on stdin/stdout. args: flag, a
    /// counter file (how many times it has been started), "once" (later starts die at once) or "always", and how
    /// long to keep serving after tools/list before exiting on its own — what the supervisor sees as a crash.
    /// Touches nothing but its counter file; gives up after 20 s whatever happens.
    /// </summary>
    public static int ServerStub(string[] args)
    {
        if (args.Length < 4) return 2;
        string counter = args[1];
        string mode = args[2];
        int serveMs = int.TryParse(args[3], out int v) ? Math.Clamp(v, 0, 30_000) : 400;
        int n = 1;
        try
        {
            n = (File.Exists(counter) && int.TryParse(File.ReadAllText(counter).Trim(), out int c) ? c : 0) + 1;
            File.WriteAllText(counter, n.ToString());
        }
        catch { }
        if (mode == "once" && n > 1) return 3;

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var listed = new ManualResetEventSlim(false);
        var eof = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            try
            {
                using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    JsonNode req;
                    try { req = JsonNode.Parse(line); } catch { continue; }
                    var id = req?["id"];
                    if (id == null) continue;   // a notification
                    string method = req["method"]?.GetValue<string>() ?? "";
                    JsonNode result = method switch
                    {
                        "initialize" => new JsonObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "cluadex-apitest-stub", ["version"] = "1" },
                        },
                        "tools/list" => new JsonObject
                        {
                            ["tools"] = new JsonArray(new JsonObject
                            {
                                ["name"] = "stub_echo",
                                ["description"] = "apitest stub tool",
                                ["inputSchema"] = new JsonObject { ["type"] = "object" },
                            }),
                        },
                        _ => new JsonObject(),
                    };
                    lock (stdout)
                        stdout.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
                    if (method == "tools/list") listed.Set();
                }
            }
            catch { }
            eof.Set();
        }) { IsBackground = true };
        reader.Start();
        if (WaitHandle.WaitAny(new[] { listed.WaitHandle, eof.WaitHandle }, 20_000) == 0)
            eof.Wait(serveMs);
        return 0;
    }
}
