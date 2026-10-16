using System.IO;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;

namespace ApiTest;

/// <summary>
/// CluadeX must never run brainx-mcp from BrainX's Velopack install (%LOCALAPPDATA%\BrainX\current or app-*):
/// a running exe there locks the folder Velopack renames, and on 2026-10-16 BrainX's Setup failed twice with
/// "Access is denied" until CluadeX was closed. CluadeX had (a) found BrainX itself and picked current\mcp, and
/// (b) kept respawning the copy of mcp_servers.json it loaded at startup after BrainX had healed the file to the
/// mirror %USERPROFILE%\.brainx\mcp. This proves the pure rules, then the REAL start path: a stale entry pointing
/// into a fake install is launched from the mirror, a missing mirror means no brain (never the install), and an
/// entry healed on disk while the app runs is what the next start uses.
///
/// Nothing real is touched: the Debug-only overrides CLUADEX_BRAINX_EXE (the mirror) and CLUADEX_BRAINX_LOCALAPPDATA
/// (where the install is looked for) point into a temp folder; the "install" exe is a text file that is not a
/// program, and the "mirror" is this harness serving as a stub MCP server (<see cref="McpConfigSafety.ServerStubFlag"/>).
/// </summary>
internal static class BrainLaunchPath
{
    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · brainx-mcp never runs from BrainX's install folder (fake install + stub mirror)");

        // ── Pure rules ──
        string lad = @"C:\Users\u\AppData\Local";
        string mirror = @"C:\Users\u\.brainx\mcp\brainx-mcp.exe";
        foreach (var (p, want) in new (string, bool)[]
        {
            (@"C:\Users\u\AppData\Local\BrainX\current\mcp\brainx-mcp.exe", true),
            (@"c:\users\U\appdata\local\brainx\CURRENT\mcp\brainx-mcp.exe", true),
            ("\"C:\\Users\\u\\AppData\\Local\\BrainX\\current\\mcp\\brainx-mcp.exe\"", true),
            ("C:/Users/u/AppData/Local/BrainX/current/mcp/brainx-mcp.dll", true),
            (@"C:\Users\u\AppData\Local\BrainX\app-2.0.505\mcp\brainx-mcp.exe", true),
            (@"C:\Users\u\AppData\Local\BrainX\..\BrainX\current\mcp\brainx-mcp.exe", true),
            (mirror, false),
            (@"C:\Users\u\AppData\Local\BrainXOther\current\mcp\brainx-mcp.exe", false),
            (@"C:\Users\u\AppData\Local\CluadeX\current\x.exe", false),
            ("npx", false), ("", false), (null, false),
        })
            check("brainpath", $"install folder? {p ?? "null"} → {want}", McpServerManager.IsInBrainXInstallFolder(p, lad) == want, "");

        var fromInstall = new McpServerConfig
        {
            Name = "brainx-brain", Command = $@"{lad}\BrainX\current\mcp\brainx-mcp.exe", Args = new() { "--x" },
            Env = new() { ["BRAINX_VAULT"] = @"G:\Vault" }, Enabled = true,
        };
        var onMirror = new McpServerConfig { Name = "brainx-brain", Command = mirror, Args = new() };
        check("brainpath", "an entry already on the mirror is honoured as it is",
            ReferenceEquals(McpServerManager.RouteBrainLaunch(onMirror, lad, mirror, _ => true), onMirror), "");
        var routed = McpServerManager.RouteBrainLaunch(fromInstall, lad, mirror, f => f == mirror);
        check("brainpath", "an entry inside BrainX\\current is redirected to the mirror (args + env kept)",
            routed != null && routed.Command == mirror && routed.Args.SequenceEqual(new[] { "--x" })
            && routed.Env?["BRAINX_VAULT"] == @"G:\Vault" && fromInstall.Command.Contains(@"\current\"), routed?.Command ?? "null");
        check("brainpath", "mirror missing → refused (null), never the install folder as a fallback",
            McpServerManager.RouteBrainLaunch(fromInstall, lad, mirror, _ => false) == null, "");
        var viaDotnet = new McpServerConfig { Command = "dotnet", Args = new() { $@"{lad}\BrainX\app-2.0.505\mcp\brainx-mcp.dll", "--serve" } };
        var routedDotnet = McpServerManager.RouteBrainLaunch(viaDotnet, lad, mirror, _ => true);
        check("brainpath", "dotnet + a dll inside app-* → the mirror, the install-folder argument dropped",
            routedDotnet?.Command == mirror && routedDotnet.Args.SequenceEqual(new[] { "--serve" }), string.Join(" ", routedDotnet?.Args ?? new()));

        // ── The real start path ──
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!File.Exists(Path.Combine(exeDir, "portable.txt")))
        {
            check("brainpath", "STOPPED: not portable (no portable.txt next to the harness)", false, exeDir);
            return;
        }
        var svc = new SettingsService();
        if (!svc.IsPortable || !svc.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            check("brainpath", "STOPPED: not portable", false, svc.DataRoot);
            return;
        }
        Directory.CreateDirectory(svc.DataRoot);
        string path = Path.Combine(svc.DataRoot, "mcp_servers.json");
        string root = Path.Combine(Path.GetTempPath(), "cluadex-apitest-brainpath-" + Guid.NewGuid().ToString("N"));
        string fakeLad = Path.Combine(root, "Local");
        string installExe = Path.Combine(fakeLad, "BrainX", "current", "mcp", "brainx-mcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installExe)!);
        File.WriteAllText(installExe, "not a program: CluadeX apitest stand-in for BrainX\\current\\mcp\\brainx-mcp.exe\r\n");
        string self = Environment.ProcessPath!;
        string counter = Path.Combine(root, "starts.txt");

        string prevExe = Environment.GetEnvironmentVariable("CLUADEX_BRAINX_EXE");
        string prevLad = Environment.GetEnvironmentVariable("CLUADEX_BRAINX_LOCALAPPDATA");
        Environment.SetEnvironmentVariable("CLUADEX_BRAINX_LOCALAPPDATA", fakeLad);
        void Write(string command) => File.WriteAllText(path, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["brainx-brain"] = new JsonObject
                {
                    ["command"] = command,
                    ["args"] = new JsonArray(McpConfigSafety.ServerStubFlag, counter, "always", "8000"),
                    ["enabled"] = true,
                },
            },
        }.ToJsonString());

        try
        {
            // 1. Stale entry pointing into the install, mirror present (= this harness) → the mirror runs.
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", self);
            check("brainpath", "guard: the mirror lookup answers with the harness (Debug override honoured)",
                McpServerManager.FindInstalledBrainExe() == self, McpServerManager.FindInstalledBrainExe() ?? "null");
            if (McpServerManager.FindInstalledBrainExe() != self) return;
            Write(installExe);
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                var started = new List<string>();
                mgr.OnServerLog += (n, m) => { if (n == "brainx-brain" && m.StartsWith("Starting:", StringComparison.Ordinal)) lock (started) started.Add(m); };
                bool up = await mgr.StartServerAsync("brainx-brain");
                string log = string.Join(" | ", started);
                check("brainpath", "entry in BrainX\\current: started from the mirror, the install exe never launched",
                    up && started.Count == 1 && started[0].Contains(self, StringComparison.OrdinalIgnoreCase)
                    && !started.Any(s => s.Contains(@"\BrainX\current\", StringComparison.OrdinalIgnoreCase)), log);
                check("brainpath", "...and mcp_servers.json is left as it was (BrainX's onboarding owns the entry)",
                    File.ReadAllText(path).Contains("current"), "");
                await mgr.StopServerAsync("brainx-brain");
            }

            // 2. Same entry, mirror missing → no brain: Failed with the localized reason, nothing spawned.
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", Path.Combine(root, "missing", "brainx-mcp.exe"));
            // Guard (a 2026-10-16 run of this very check started the REAL mirror before the override covered the
            // redirect): stop here unless "missing" is what the app sees.
            check("brainpath", "guard: a missing override stub = not installed, never the real mirror",
                McpServerManager.FindInstalledBrainExe() == null, McpServerManager.FindInstalledBrainExe() ?? "null");
            if (McpServerManager.FindInstalledBrainExe() != null) return;
            Write(installExe);
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                int starts = 0;
                mgr.OnServerLog += (n, m) => { if (m.StartsWith("Starting:", StringComparison.Ordinal)) Interlocked.Increment(ref starts); };
                bool up = await mgr.StartServerAsync("brainx-brain");
                string err = mgr.GetServerLastError("brainx-brain") ?? "";
                check("brainpath", "mirror missing: not started at all — no fallback to BrainX\\current",
                    !up && starts == 0 && mgr.GetServerState("brainx-brain") == McpServerState.Failed, $"starts={starts}");
                check("brainpath", "mirror missing: the reason names ~\\.brainx\\mcp (en)", err.Contains(@".brainx\mcp"), err);
                await Task.Delay(1500);   // a supervisor respawn would have come by now
                check("brainpath", "mirror missing: the supervisor does not keep retrying the install folder", Volatile.Read(ref starts) == 0, $"starts={starts}");
                check("brainpath", "FindInstalledBrainExe: mirror missing = not installed (never BrainX\\current)",
                    McpServerManager.FindInstalledBrainExe() == null, "");
            }

            // 3. Loaded stale, healed on disk while the app runs (BrainX's onboarding) → the next start uses the file.
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", Path.Combine(root, "missing", "brainx-mcp.exe"));
            Write(installExe);
            using (var mgr = new McpServerManager(svc))
            {
                mgr.LoadConfig();
                bool first = await mgr.StartServerAsync("brainx-brain");
                Write(self);   // the heal: the entry now points at the mirror (here: the harness)
                var started = new List<string>();
                mgr.OnServerLog += (n, m) => { if (n == "brainx-brain" && m.StartsWith("Starting:", StringComparison.Ordinal)) lock (started) started.Add(m); };
                bool second = await mgr.StartServerAsync("brainx-brain");
                check("brainpath", "entry healed on disk after load: the next start runs what the file says now",
                    !first && second && started.Count == 1 && started[0].Contains(self, StringComparison.OrdinalIgnoreCase)
                    && mgr.Configs["brainx-brain"].Command == self, string.Join(" | ", started));
                await mgr.StopServerAsync("brainx-brain");
            }

            var th = new LocalizationService();
            th.SetLanguage("th");
            check("brainpath", "the refusal has its own Thai text", th.T("mcp.brain.mirrorMissing") != "mcp.brain.mirrorMissing"
                && th.T("mcp.brain.mirrorMissing").Contains(@".brainx\mcp"), "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", prevExe);
            Environment.SetEnvironmentVariable("CLUADEX_BRAINX_LOCALAPPDATA", prevLad);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
