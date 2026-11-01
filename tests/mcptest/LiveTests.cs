using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;

namespace McpTest;

// The MCP page tells the truth now, and CluadeX follows BrainX to a new build. Real processes over real stdio against a
// fake BrainX: this exe copied to brainx-mcp.exe, so the program's path stays the same across "versions" exactly as
// ~\.brainx\mcp does. A version is the text of runtime.version beside it, and what a build does is decided per version
// (fakebrain\behave-<version>.txt), so a "new build that won't start" is a file, not a mock.
internal static partial class Program
{
    const string BrainName = "brainx-brain";
    static string _fakeState = "", _fakeExe = "", _marker = "";

    static void SetVersion(string version, string behaviour = "plain")
    {
        File.WriteAllText(Path.Combine(_fakeState, $"behave-{version}.txt"), behaviour);
        File.WriteAllText(_marker, version);
    }

    static McpServerManager NewManager(SettingsService settings) => new(settings)
    {
        SwapSoak = TimeSpan.FromMilliseconds(400),
        PingTimeout = TimeSpan.FromSeconds(2),
        PingEvery = TimeSpan.Zero,
        BrainSwapGrace = TimeSpan.Zero,
        HealCooldown = TimeSpan.Zero,
    };

    static async Task<bool> Until(Func<bool> done, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (done()) return true;
            await Task.Delay(40);
        }
        return done();
    }

    static IEnumerable<string> Spawned() => Directory.GetFiles(_fakeState, "spawn-*.txt");
    static int SpawnCount() => Spawned().Count();
    static int InitCount(int pid) => File.Exists(Path.Combine(_fakeState, $"init-{pid}.txt")) ? File.ReadAllText(Path.Combine(_fakeState, $"init-{pid}.txt")).Length : 0;
    static bool Alive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch { return false; } }

    /// <summary>"served-by 1.0.0 pid 1234".</summary>
    static async Task<(string Version, int Pid)> Who(McpServerManager m, string server = BrainName)
    {
        var r = await m.CallToolAsync(server, "who", new Dictionary<string, string>());
        var parts = r.GetTextOutput().Split(' ');
        return (parts[1], int.Parse(parts[3]));
    }

    static void ResetFake()
    {
        foreach (var f in Directory.GetFiles(_fakeState)) { try { File.Delete(f); } catch { } }
    }

    static async Task Connected(McpServerManager m, string name = BrainName, McpServerConfig? cfg = null)
    {
        m.SetConfig(name, cfg ?? new McpServerConfig { Command = _fakeExe });
        if (!await m.StartServerAsync(name)) throw new Exception($"{name} did not start: {m.GetServerLastError(name)}");
    }

    static async Task LiveTests(SettingsService settings, LocalizationService loc, string exeDir)
    {
        Console.WriteLine("\n── live status and following BrainX ──");
        // Same folder as mcptest.dll: the apphost finds it there, and the copy keeps its own name.
        _fakeExe = Path.Combine(exeDir, "brainx-mcp.exe");
        File.Copy(Path.Combine(exeDir, "mcptest.exe"), _fakeExe, overwrite: true);
        _fakeState = Path.Combine(exeDir, "fakebrain");
        Directory.CreateDirectory(_fakeState);
        _marker = Path.Combine(exeDir, "runtime.version");
        Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", _fakeExe);   // Debug builds only: never the real mirror

        await Case("L1", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m);
            await m.RunLiveChecksAsync();
            vm.RefreshLive();
            var item = vm.Servers.FirstOrDefault(s => s.Name == BrainName);
            Check("L1", "the page shows the server connected, its version and when it last answered",
                item is { Status: "running", VersionText: "v1.0.0" } && item.ConnectionText.Length > 0 && !item.ConnectionText.StartsWith("mcpm.") && !item.HasUpdateText,
                $"{item?.Status} {item?.VersionText} '{item?.ConnectionText}'");
            Check("L1b", "the BrainX line says running and installed", vm.BrainLine.Contains("1.0.0") && !vm.BrainLine.StartsWith("mcpm."), vm.BrainLine);

            string summaryBefore = vm.StatusText, connectedText = item!.ConnectionText;
            _ = m.CallToolAsync(BrainName, "die", new Dictionary<string, string>()).ContinueWith(t => t.Exception);
            bool lost = await Until(() => item!.Status == "reconnecting");
            Check("L2", "a server that dies moves the page by itself, with no Refresh, and its version badge goes", lost && item.VersionText == "", $"{item.Status} '{item.VersionText}'");
            Check("L2b", "…the page says the connection was lost and which attempt it is on", item.ConnectionText != connectedText && item.ConnectionText.Contains("/"), item.ConnectionText);
            Check("L2c", "…'1 running' does not outlive the server", vm.StatusText != summaryBefore, vm.StatusText);
            bool back = await Until(() => item.Status == "running", 15000);
            Check("L2d", "it comes back by itself and the page follows, tools and version included",
                back && item.VersionText == "v1.0.0" && item.ToolCount > 0 && vm.StatusText == summaryBefore, $"{item.Status} {item.VersionText} {item.ToolCount} '{vm.StatusText}'");
        });

        await Case("L3", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m, "plain-server");   // no "brain" in the name: nothing is replaced, this is only about the page
            var (_, pid) = await Who(m, "plain-server");
            await m.CheckLivenessAsync();         // it has answered a ping: from here a silence means something
            File.WriteAllText(Path.Combine(_fakeState, $"hang-{pid}"), "");
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            vm.RefreshLive();
            var item = vm.Servers.First(s => s.Name == "plain-server");
            Check("L3", "alive but answering nothing is not 'running': it reads not responding", item.Status == "unresponsive" && !m.IsResponding("plain-server"), item.Status);
            File.Delete(Path.Combine(_fakeState, $"hang-{pid}"));
            await m.CheckLivenessAsync();
            Check("L3b", "…and running again the moment it answers", item.Status == "running", item.Status);
            Check("L3c", "the process was left alone: a server outside the brain is reported, never replaced", SpawnCount() == 1, $"spawns {SpawnCount()}");
        });

        await Case("L4", async () =>
        {
            ResetFake();
            SetVersion("1.0.0", "noping");
            using var m = NewManager(settings);
            m.PingTimeout = TimeSpan.FromMilliseconds(300);
            await Connected(m, "old-server");
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            Check("L4", "a server that never answered a ping is an old server, not a hung one", m.IsResponding("old-server"));
        });

        await Case("L5", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            m.PingTimeout = TimeSpan.FromMilliseconds(500);
            await Connected(m, "busy-server");
            var (_, pid) = await Who(m, "busy-server");
            await m.CheckLivenessAsync();
            var slow = m.CallToolAsync("busy-server", "slow", new Dictionary<string, string> { ["ms"] = "1500" });
            await Task.Delay(100);
            File.WriteAllText(Path.Combine(_fakeState, $"hang-{pid}"), "");   // it would not answer a ping now
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            Check("L5", "a server working on a long call is left alone: busy is not hung", m.IsResponding("busy-server"));
            File.Delete(Path.Combine(_fakeState, $"hang-{pid}"));
            await slow;
        });

        await Case("L6", async () =>
        {
            // What the real launcher does: same pipe, new worker, tools/list_changed, the replayed answer swallowed.
            ResetFake();
            SetVersion("1.0.0", "launcher");
            using var m = NewManager(settings);
            m.BrainSwapGrace = TimeSpan.FromMinutes(5);   // nothing but the launcher may move it here
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m);
            var item = vm.Servers.First(s => s.Name == BrainName);
            var (_, pid) = await Who(m);
            var inFlight = m.CallToolAsync(BrainName, "slow", new Dictionary<string, string> { ["ms"] = "700" });
            await Task.Delay(100);
            SetVersion("2.0.0", "launcher");
            await m.RunLiveChecksAsync();
            vm.RefreshLive();
            Check("L6", "a new build on disk: the page says it is waiting for it, and the running version is still the truth",
                m.GetUpdateStatus(BrainName)?.Kind == McpUpdateKind.Waiting && item.HasUpdateText && item.VersionText == "v1.0.0", $"{m.GetUpdateStatus(BrainName)} '{item.UpdateText}' {item.VersionText}");
            var slow = await inFlight;
            Check("L6b", "the call that was running finished on the old build", slow.GetTextOutput().Contains("slow-done 1.0.0"), slow.GetTextOutput());
            bool moved = await Until(() => item.VersionText == "v2.0.0");
            vm.RefreshLive();
            Check("L6c", "the launcher swapped: the page shows the new version with no restart and no setting touched",
                moved && !item.HasUpdateText && m.ServerVersion(BrainName) == "2.0.0" && m.BrainStatusText.Contains("2.0.0"), $"{item.VersionText} {m.BrainStatusText}");
            Check("L6d", "…on the same process and pipe: CluadeX started nothing", SpawnCount() == 1 && (await Who(m)).Pid == pid, $"spawns {SpawnCount()}");
            Check("L6e", "…and the tools of the new build are registered", m.ToolRegistry.FindTool($"mcp__{BrainName}__v_2_0_0") != null && m.ToolRegistry.FindTool($"mcp__{BrainName}__v_1_0_0") == null);
            await Connected(m, "other-launcher");
            var (_, otherPid) = await Who(m, "other-launcher");
            SetVersion("2.1.0", "launcher");
            bool listed = await Until(() => m.ToolRegistry.FindTool("mcp__other-launcher__v_2_1_0") != null);
            await Task.Delay(300);
            Check("L6f", "only the brain is asked again: any other server gets one initialize in its life", listed && InitCount(otherPid) == 1, $"{listed} inits {InitCount(otherPid)}");
        });

        await Case("L7", async () =>
        {
            // The launcher did not move (it gave up, or this entry has none): CluadeX starts the new build beside the old.
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            m.SwapSoak = TimeSpan.FromSeconds(1);
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m);
            var item = vm.Servers.First(s => s.Name == BrainName);
            var (_, oldPid) = await Who(m);
            var states = new List<McpServerState>();
            int leastTools = int.MaxValue;
            m.OnServerStateChanged += (_, s) => { lock (states) states.Add(s); };
            using var stop = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    leastTools = Math.Min(leastTools, m.ToolRegistry.GetToolsForServer(BrainName).Count);
                    await Task.Delay(10);
                }
            });

            var inFlight = m.CallToolAsync(BrainName, "slow", new Dictionary<string, string> { ["ms"] = "3000" });
            await Task.Delay(100);
            SetVersion("2.0.0");
            await m.RunLiveChecksAsync();    // sees it, gives the launcher its grace
            await m.RunLiveChecksAsync();    // the grace (0 here) is over: moves
            var (now, newPid) = await Who(m);
            Check("L7", "new calls are answered by the new build while the old call is still running",
                now == "2.0.0" && newPid != oldPid && !inFlight.IsCompleted && Alive(oldPid), $"{now} pid {newPid} (old {oldPid}) done={inFlight.IsCompleted}");
            var slow = await inFlight;
            Check("L7b", "the call in flight was not killed: it got its answer from the old process", slow.GetTextOutput().Contains("slow-done 1.0.0"), slow.GetTextOutput());
            bool gone = await Until(() => !Alive(oldPid), 8000);
            Check("L7c", "the old process stops once its calls are done, not before and not never", gone);
            stop.Cancel();
            await watch;
            lock (states) Check("L7d", "the state never left Ready and the tools never went away", states.All(s => s == McpServerState.Ready) && leastTools > 0, $"{string.Join(",", states)} least={leastTools}");
            vm.RefreshLive();
            Check("L7e", "the page shows the new version and nothing pending", item.VersionText == "v2.0.0" && !item.HasUpdateText && vm.BrainLine.Contains("2.0.0"), $"{item.VersionText} '{item.UpdateText}' {vm.BrainLine}");
            _ = m.CallToolAsync(BrainName, "die", new Dictionary<string, string>()).ContinueWith(t => t.Exception);
            bool stillSupervised = await Until(() => m.GetServerState(BrainName) != McpServerState.Ready) && await Until(() => m.GetServerState(BrainName) == McpServerState.Ready, 15000);
            Check("L7f", "the new process is supervised like any other: it dies, it comes back", stillSupervised, m.GetServerState(BrainName).ToString());
        });

        foreach (var (id, behaviour, label) in new[]
        {
            ("L8", "die-start", "exits on start"),
            ("L9", "hang-init", "never answers initialize"),
            ("L10", "die-soon", "answers, then dies"),
            ("L11", "no-tools", "offers no tools"),
        })
        {
            await Case(id, async () =>
            {
                ResetFake();
                SetVersion("1.0.0");
                using var m = NewManager(settings);
                using var vm = new McpServersViewModel(m, loc);
                await Connected(m, cfg: new McpServerConfig { Command = _fakeExe, StartupTimeoutSeconds = 2 });
                var item = vm.Servers.First(s => s.Name == BrainName);
                var (_, oldPid) = await Who(m);
                SetVersion("2.0.0", behaviour);
                await m.RunLiveChecksAsync();
                await m.RunLiveChecksAsync();
                var (still, pid) = await Who(m);
                vm.RefreshLive();
                Check(id, $"a new build that {label}: the old one goes on serving, tools intact",
                    still == "1.0.0" && pid == oldPid && m.GetServerState(BrainName) == McpServerState.Ready && m.ToolRegistry.GetToolsForServer(BrainName).Count > 0,
                    $"{still} {pid}/{oldPid} {m.GetServerState(BrainName)}");
                Check(id + "b", "…and the page says so: which version failed, which stays, why",
                    m.GetUpdateStatus(BrainName)?.Kind == McpUpdateKind.Failed && item.UpdateText.Contains("2.0.0") && item.UpdateText.Contains("1.0.0") && item.VersionText == "v1.0.0",
                    $"'{item.UpdateText}'");
                int spawns = SpawnCount();
                await m.RunLiveChecksAsync();
                await m.RunLiveChecksAsync();
                Check(id + "c", "…one attempt per build: no spawn loop against a build that doesn't work", SpawnCount() == spawns, $"{spawns} → {SpawnCount()}");
                SetVersion("2.0.1");
                await m.RunLiveChecksAsync();
                await m.RunLiveChecksAsync();
                var (fixedVersion, _) = await Who(m);
                vm.RefreshLive();
                Check(id + "d", "…and the next build is tried, and clears the warning", fixedVersion == "2.0.1" && !item.HasUpdateText && item.VersionText == "v2.0.1", $"{fixedVersion} '{item.UpdateText}'");
            });
        }

        await Case("L12", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            await Connected(m);
            await m.StopServerAsync(BrainName);
            SetVersion("2.0.0");
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            Check("L12", "a brain the user stopped stays stopped when a new build arrives", m.GetServerState(BrainName) == McpServerState.Stopped && SpawnCount() == 1, $"{m.GetServerState(BrainName)} spawns {SpawnCount()}");
        });

        await Case("L13", async () =>
        {
            // Stop pressed while the new build is being tried: it must not come back behind the Stop.
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            m.SwapSoak = TimeSpan.FromSeconds(2);
            await Connected(m);
            SetVersion("2.0.0");
            await m.RunLiveChecksAsync();
            var swap = m.RunLiveChecksAsync();
            bool spawned = await Until(() => SpawnCount() == 2);
            await Task.Delay(300);
            await m.StopServerAsync(BrainName);
            await swap;
            var pids = Spawned().Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)["spawn-".Length..])).ToList();
            bool allGone = await Until(() => pids.All(p => !Alive(p)));
            Check("L13", "Stop during a switch wins: nothing keeps running and the state is Stopped",
                spawned && m.GetServerState(BrainName) == McpServerState.Stopped && allGone && m.ToolRegistry.GetToolsForServer(BrainName).Count == 0 && m.GetUpdateStatus(BrainName) == null,
                $"{m.GetServerState(BrainName)} {string.Join(",", pids.Where(Alive))} {m.GetUpdateStatus(BrainName)}");
        });

        await Case("L14", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m);
            var item = vm.Servers.First(s => s.Name == BrainName);
            var (_, pid) = await Who(m);
            await m.CheckLivenessAsync();
            File.WriteAllText(Path.Combine(_fakeState, $"zombie-{pid}"), "");   // a launcher with no worker answers everything with -32603
            await m.CheckLivenessAsync();
            await m.CheckLivenessAsync();
            Check("L14", "a launcher that lost its worker answers, with an error: that is not responding", !m.IsResponding(BrainName));
            await m.FollowBrainAsync();
            var (_, newPid) = await Who(m);
            Check("L14b", "the brain is replaced by itself, with the same version, and the page is green again",
                newPid != pid && m.IsResponding(BrainName) && item.Status == "running", $"{newPid}/{pid} {item.Status}");
            Check("L14c", "the broken process is stopped", await Until(() => !Alive(pid)));
        });

        await Case("L15", async () =>
        {
            // BrainX's launcher exited (a broken build) and the supervisor gave up: nothing to fall back on, so it says so,
            // and the next build gets one start.
            ResetFake();
            SetVersion("9.9.9", "die-start");
            using var m = NewManager(settings);
            m.SetConfig(BrainName, new McpServerConfig { Command = _fakeExe });
            Check("L15", "a brain that cannot start is Failed", !await m.StartServerAsync(BrainName) && m.GetServerState(BrainName) == McpServerState.Failed);
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            int spawns = SpawnCount();
            Check("L15b", "…with no retry loop on the same build", spawns == 1, $"{spawns}");
            SetVersion("10.0.0");
            await m.RunLiveChecksAsync();
            Check("L15c", "a new build on disk brings it up with no click", m.GetServerState(BrainName) == McpServerState.Ready && m.ServerVersion(BrainName) == "10.0.0", $"{m.GetServerState(BrainName)} {m.ServerVersion(BrainName)}");
        });

        await Case("L16", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            await Connected(m);
            File.Delete(_marker);    // the moment of a swap: the file is not there
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            Check("L16", "a build that can't be read right now changes nothing", m.GetUpdateStatus(BrainName) == null && SpawnCount() == 1, $"{m.GetUpdateStatus(BrainName)} {SpawnCount()}");
            SetVersion("1.0.0");
        });

        await Case("L17", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            await Connected(m, "my-brain-notes", new McpServerConfig { Command = Path.Combine(exeDir, "mcptest.exe"), Args = new List<string> { "--fake-brain" } });
            var (_, pid) = await Who(m, "my-brain-notes");
            SetVersion("2.0.0");
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            Check("L17", "a third-party server with 'brain' in its name is not BrainX: nothing to follow", m.GetUpdateStatus("my-brain-notes") == null && m.GetServerState("my-brain-notes") == McpServerState.Ready);
            await m.RunLiveChecksAsync();
            File.WriteAllText(Path.Combine(_fakeState, $"hang-{pid}"), "");
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            await m.RunLiveChecksAsync();
            Check("L17b", "…and hung, it is reported, not started a second time: two of a server we know nothing about may fight", !m.IsResponding("my-brain-notes") && SpawnCount() == 1, $"spawns {SpawnCount()}");
        });

        await Case("L18", async () =>
        {
            // The real mirror's layout: brainx-mcp.dll carries the version, and it wins over a marker that lags behind it
            // (a dev deploy doesn't stamp runtime.version).
            ResetFake();
            SetVersion("1.0.0");
            string dll = Path.Combine(exeDir, "brainx-mcp.dll");
            File.Copy(Path.Combine(exeDir, "CluadeX.dll"), dll, overwrite: true);
            try
            {
                using var m = NewManager(settings);
                m.BrainSwapGrace = TimeSpan.FromMinutes(5);
                await Connected(m);
                await m.FollowBrainAsync();
                string want = FileVersionInfo.GetVersionInfo(dll).ProductVersion!.Split('+')[0];
                Check("L18", "the installed version is read from brainx-mcp.dll first, without its +hash", m.InstalledBrainVersion == want, $"{m.InstalledBrainVersion} vs {want}");
            }
            finally { File.Delete(dll); }
        });

        await Case("L19", async () =>
        {
            // BrainX installed while CluadeX is running.
            ResetFake();
            SetVersion("1.0.0");
            string file = Path.Combine(settings.DataRoot, "mcp_servers.json");
            string before = File.Exists(file) ? File.ReadAllText(file) : "";
            bool declined = settings.Settings.BrainXAutoConnectDeclined;
            try
            {
                File.WriteAllText(file, """{ "mcpServers": {} }""");
                settings.UpdateSettings(s => s.BrainXAutoConnectDeclined = true);
                using (var m = NewManager(settings))
                {
                    m.LoadConfig();
                    await m.RunLiveChecksAsync();
                    Check("L19", "BrainX the user removed is not put back by the live check", m.BrainServerName == null && SpawnCount() == 0, $"{m.BrainServerName}");
                }
                settings.UpdateSettings(s => s.BrainXAutoConnectDeclined = false);
                using (var m = NewManager(settings))
                {
                    m.LoadConfig();
                    using var vm = new McpServersViewModel(m, loc);
                    await m.RunLiveChecksAsync();
                    Check("L19b", "BrainX installed while CluadeX runs connects by itself and the page lists it",
                        m.IsBrainConnected && vm.Servers.Any(s => s.Name == m.BrainServerName && s.Status == "running") && File.ReadAllText(file).Contains("brainx-mcp"),
                        $"{m.BrainStatusText} {m.LastBrainConnectProblem}");
                }
            }
            finally
            {
                File.WriteAllText(file, before);
                settings.UpdateSettings(s => s.BrainXAutoConnectDeclined = declined);
            }
        });

        await Case("L20", async () =>
        {
            ResetFake();
            SetVersion("1.0.0");
            using var m = NewManager(settings);
            m.LiveTick = TimeSpan.FromMilliseconds(100);
            using var vm = new McpServersViewModel(m, loc);
            await Connected(m);
            m.StartLiveMonitor();
            SetVersion("2.0.0");
            var item = vm.Servers.First(s => s.Name == BrainName);
            Check("L20", "with nothing but the running monitor, a release moves CluadeX to the new build", await Until(() => item.VersionText == "v2.0.0"), item.VersionText);
            var pids = Spawned().Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)["spawn-".Length..])).ToList();
            m.Dispose();
            Check("L20b", "closing the app stops every process, the one being replaced included", await Until(() => pids.All(p => !Alive(p))), string.Join(",", pids.Where(Alive)));
        });

        Environment.SetEnvironmentVariable("CLUADEX_BRAINX_EXE", null);
        var stray = Spawned().Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)["spawn-".Length..])).Where(Alive).ToList();
        Check("L99", "no fake brain is left running", stray.Count == 0 || await Until(() => stray.All(p => !Alive(p))), string.Join(",", stray));
    }
}

// Against the real thing, on demand: `dotnet run -- --real-launcher <folder>` where <folder> is a COPY of ~\.brainx\mcp holding
// an older brainx-mcp.dll, with the newer one in <folder>/next. Replaces the dll the way deploy-mcp.ps1 does, and proves the
// real launcher, the real worker and the running manager agree. BRAINX_SANDBOX + a throwaway vault: never the owner's notes.
internal static partial class Program
{
    static async Task<int> RealLauncher(SettingsService settings, LocalizationService loc, string folder)
    {
        string dll = Path.Combine(folder, "brainx-mcp.dll"), vault = Path.Combine(folder, "vault");
        string after = FileVersionInfo.GetVersionInfo(Path.Combine(folder, "next", "brainx-mcp.dll")).ProductVersion!.Split('+')[0];
        var cfg = new McpServerConfig
        {
            Command = Path.Combine(folder, "brainx-mcp.exe"),
            Args = new List<string> { vault },
            Env = new Dictionary<string, string> { ["BRAINX_SANDBOX"] = "1", ["BRAINX_CANARY"] = "0", ["BRAINX_VAULT"] = vault },
        };
        using var m = new McpServerManager(settings) { LiveTick = TimeSpan.FromSeconds(2), PingEvery = TimeSpan.FromSeconds(5) };
        using var vm = new McpServersViewModel(m, loc);
        await Connected(m, cfg: cfg);
        string before = m.ServerVersion(BrainName) ?? "";
        var item = vm.Servers.First(s => s.Name == BrainName);
        int toolsBefore = m.ToolRegistry.GetToolsForServer(BrainName).Count;
        Check("R1", "the real launcher connects and says which build it is", before.Length > 0 && before != after && toolsBefore > 0, $"{before} → {after}, {toolsBefore} tools");
        var states = new List<McpServerState>();
        m.OnServerStateChanged += (_, s) => { lock (states) states.Add(s); };
        m.StartLiveMonitor();

        File.Move(dll, dll + ".stale.harness", overwrite: true);       // locked by the running launcher: renaming still works
        File.Copy(Path.Combine(folder, "next", "brainx-mcp.dll"), dll);
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow);

        bool moved = await Until(() => m.ServerVersion(BrainName) == after, 80000);
        lock (states) Check("R2", "the real launcher moved and CluadeX knows, with no restart and the state never leaving Ready", moved && states.All(s => s == McpServerState.Ready),
            $"{m.ServerVersion(BrainName)} states {string.Join(",", states)}");
        vm.RefreshLive();
        var answer = await m.CallToolAsync(BrainName, "brain_stats", new Dictionary<string, string>());   // an empty vault has no stats: "no brain-export" is the worker talking
        Check("R3", "the page shows it and the new worker answers a real tool", item.VersionText == "v" + after && !item.HasUpdateText && answer.GetTextOutput().Contains("brain-export") && m.ToolRegistry.GetToolsForServer(BrainName).Count > 0,
            $"{item.VersionText} '{item.UpdateText}' error={answer.IsError} {answer.GetTextOutput()[..Math.Min(80, answer.GetTextOutput().Length)]}");
        await m.CheckLivenessAsync();
        Check("R4", "a repeated initialize and a ping are normal to the real server", m.IsResponding(BrainName) && m.GetUpdateStatus(BrainName) == null, $"{m.GetUpdateStatus(BrainName)}");
        await m.StopServerAsync(BrainName);
        m.RemoveConfig(BrainName);
        return Fail == 0 ? 0 : 1;
    }
}

/// <summary>This exe as BrainX's MCP: stdio JSON-RPC whose version is runtime.version at start. "launcher" also behaves as
/// the real launcher does on an update: it waits for its calls, moves to the new version on the same pipe, says
/// tools/list_changed — and only a second initialize reveals the new version.</summary>
static class FakeBrain
{
    static string _version = "";
    static int _inFlight;
    static readonly object Out = new();

    public static int Run()
    {
        string home = AppContext.BaseDirectory;
        string state = Path.Combine(home, "fakebrain");
        string marker = Path.Combine(home, "runtime.version");
        int pid = Environment.ProcessId;
        _version = File.ReadAllText(marker).Trim();
        string behave = Behaviour(state, _version);
        File.WriteAllText(Path.Combine(state, $"spawn-{pid}.txt"), _version);
        if (behave == "die-start")
        {
            Console.Error.WriteLine("fake brain: this build cannot start");
            return 3;
        }

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        void Send(string line) { lock (Out) stdout.WriteLine(line); }
        void Reply(string id, string result) => Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        void Fail(string id, int code, string message) => Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}");

        if (behave == "launcher")
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(100);
                    string now;
                    try { now = File.ReadAllText(marker).Trim(); } catch { continue; }
                    if (now.Length == 0 || now == _version || Volatile.Read(ref _inFlight) > 0) continue;
                    _version = now;
                    Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
                }
            });
        }

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (File.Exists(Path.Combine(state, $"hang-{pid}"))) continue;
            string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            if (!root.TryGetProperty("id", out var idEl)) continue;
            string id = idEl.GetRawText();
            if (File.Exists(Path.Combine(state, $"zombie-{pid}"))) { Fail(id, -32603, "brainx-mcp worker unavailable — retry"); continue; }
            switch (method)
            {
                case "initialize":
                    File.AppendAllText(Path.Combine(state, $"init-{pid}.txt"), "x");
                    if (behave == "hang-init") break;
                    Reply(id, $"{{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{{\"tools\":{{\"listChanged\":true}}}},\"serverInfo\":{{\"name\":\"fake-brain\",\"version\":\"{_version}\"}}}}");
                    break;
                case "ping":
                    if (behave == "noping") break;
                    Reply(id, "{}");
                    break;
                case "tools/list":
                    Reply(id, behave == "no-tools" ? "{\"tools\":[]}" : "{\"tools\":[" + string.Join(",", new[] { "who", "slow", "die", "v_" + _version.Replace('.', '_') }
                        .Select(n => $"{{\"name\":\"{n}\",\"description\":\"{n}\",\"inputSchema\":{{\"type\":\"object\",\"properties\":{{\"ms\":{{\"type\":\"string\"}}}}}}}}")) + "]}");
                    if (behave == "die-soon") _ = Task.Run(async () => { await Task.Delay(100); Environment.Exit(4); });
                    break;
                case "tools/call":
                    string name = root.GetProperty("params").GetProperty("name").GetString() ?? "";
                    if (name == "die") Environment.Exit(9);
                    if (name == "slow")
                    {
                        int ms = int.Parse(root.GetProperty("params").GetProperty("arguments").GetProperty("ms").GetString() ?? "0");
                        string startedOn = _version;
                        Interlocked.Increment(ref _inFlight);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(ms);
                            Reply(id, Text($"slow-done {startedOn}"));
                            Interlocked.Decrement(ref _inFlight);
                        });
                        break;
                    }
                    Reply(id, Text($"served-by {_version} pid {pid}"));
                    break;
                default:
                    Fail(id, -32601, "method not found");
                    break;
            }
        }
        return 0;
    }

    static string Text(string text) => "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    static string Behaviour(string state, string version)
    {
        string f = Path.Combine(state, $"behave-{version}.txt");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : "plain";
    }
}
