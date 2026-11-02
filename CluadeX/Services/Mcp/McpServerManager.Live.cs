using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>Where a server stands on a newer build of itself, for the MCP page.</summary>
public enum McpUpdateKind
{
    None,
    /// <summary>A different build is on disk; BrainX's launcher is expected to move to it when idle.</summary>
    Waiting,
    /// <summary>CluadeX is starting the new build beside the running one.</summary>
    Switching,
    /// <summary>The new build did not come up. The old one is still serving; this build is not tried again.</summary>
    Failed,
}

/// <param name="Stamp">Which build on disk this is about (time + size of the file): one attempt per build.</param>
public sealed record McpUpdateStatus(McpUpdateKind Kind, string TargetVersion, string Stamp, DateTime SinceUtc, string? Detail = null);

/// <summary>The connection as it is now: when it came up, when the server last answered, and whether it still does.</summary>
public sealed record McpConnectionInfo(DateTime? ConnectedSinceUtc, DateTime? LastHeardUtc, bool Responding);

/// <summary>A BrainX build on disk: its version, and a stamp that changes with every build.</summary>
public sealed record InstalledBrainBuild(string Version, string Stamp);

/// <summary>
/// The connection as it really is, and following BrainX to a new build.
///
/// BrainX keeps <c>~\.brainx\mcp\brainx-mcp.exe</c> at one path for every version: an update renames the files a live
/// session has mapped to <c>*.stale.*</c> and puts the new ones where they were. What CluadeX starts is a LAUNCHER that
/// watches brainx-mcp.dll; when it changes the launcher waits for its in-flight requests, swaps its worker, replays
/// the initialize handshake and sends tools/list_changed. CluadeX's pipe never closes, so on 2026-11-01 its worker
/// moved to 2.10.576 at 11:06 while the MCP page went on saying the version it read at 09:08: the launcher swallows
/// the replayed answer, so the version from the first handshake was the only one this app ever saw.
///
/// So the launcher does the switch, and this file does three things around it: asks the server which version it is
/// after a swap; watches the build on disk, and when the launcher did not move (it gives up after three failed swaps,
/// or the entry runs a server with no launcher) starts the new build BESIDE the old one and only moves over once it
/// has answered — a build that won't start costs nothing, and calls in flight finish on the old process; and pings,
/// because a process that is alive and answers nothing was still "running" on the page.
/// </summary>
public sealed partial class McpServerManager
{
    /// <summary>How often the live checks run.</summary>
    public TimeSpan LiveTick { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Between pings of a quiet local server.</summary>
    public TimeSpan PingEvery { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Between pings of a server on the network: each is a request to someone else's machine.</summary>
    public TimeSpan RemotePingEvery { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long BrainX's launcher gets to move by itself (it looks every 10 s and waits until idle).</summary>
    public TimeSpan BrainSwapGrace { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>A new build must still be alive this long after its handshake before anything moves to it.</summary>
    public TimeSpan SwapSoak { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Longest a replaced process is kept for its calls: a tool call's own limit, plus a little.</summary>
    public TimeSpan DrainLimit { get; set; } = TimeSpan.FromSeconds(200);
    /// <summary>Least time between two attempts to replace a brain that has stopped answering.</summary>
    public TimeSpan HealCooldown { get; set; } = TimeSpan.FromMinutes(5);

    private const int MissedPingsBeforeUnresponsive = 2;
    private const int JsonRpcMethodNotFound = -32601;

    private sealed class LiveState
    {
        public DateTime? HeardUtc;
        public DateTime PingedUtc;
        public int Missed;
        /// <summary>This connection has answered a ping, so a silence means something.</summary>
        public bool Answered;
        /// <summary>Never answered one: an old server without ping, not a hung one.</summary>
        public bool NoPing;
        public bool Unresponsive;
    }

    private sealed class CallCounter { public int Count; }

    private readonly ConcurrentDictionary<string, LiveState> _live = new();
    private readonly ConditionalWeakTable<IMcpTransport, CallCounter> _calls = new();
    private readonly ConcurrentDictionary<string, McpUpdateStatus> _updates = new();
    // Processes outside _transports that are still ours to stop: a new build being tried, an old one finishing its calls.
    private readonly ConcurrentDictionary<IMcpTransport, byte> _retiring = new();
    private readonly CancellationTokenSource _liveStop = new();
    private int _liveStarted;
    private int _autoConnectTried;
    private DateTime _lastHealUtc = DateTime.MinValue;
    // The build on disk when the brain was last seen Failed: a new one is worth one more start.
    private string? _failedOnStamp;
    private InstalledBrainBuild? _installedBrain;
    private Tuple<string, InstalledBrainBuild>? _installedCache;

    // ─── What the page reads ──────────────────────────────────────────

    public McpConnectionInfo GetConnectionInfo(string name)
    {
        DateTime? since = _readySinceUtc.TryGetValue(name, out var s) ? s : null;
        if (!_live.TryGetValue(name, out var live)) return new McpConnectionInfo(since, null, true);
        lock (live) return new McpConnectionInfo(since, live.HeardUtc, !live.Unresponsive);
    }

    /// <summary>False once an idle server has stopped answering its pings (it may be alive and hung).</summary>
    public bool IsResponding(string name) => GetConnectionInfo(name).Responding;

    public McpUpdateStatus? GetUpdateStatus(string name) => _updates.TryGetValue(name, out var u) ? u : null;

    /// <summary>The version of the BrainX build on disk, as of the last live check. Null until then, or if unknown.</summary>
    public string? InstalledBrainVersion => _installedBrain?.Version;

    /// <summary>The attempt a reconnecting server is on (the MCP page says "attempt 2/5").</summary>
    public int ReconnectAttempt(string name) => _reconnectAttempts.TryGetValue(name, out int a) ? a : 0;
    public static int ReconnectAttemptsMax => MaxReconnectAttempts;

    private void RaiseServerChanged(string name)
    {
        try { OnServerStateChanged?.Invoke(name, GetServerState(name)); } catch { }
        RaiseBrainStatus();
    }

    // ─── Loop ─────────────────────────────────────────────────────────

    /// <summary>Start the background checks. Called once, after the startup work has connected what it can.</summary>
    public void StartLiveMonitor()
    {
        if (_disposed || Interlocked.Exchange(ref _liveStarted, 1) != 0) return;
        var ct = _liveStop.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunLiveChecksAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log?.Warn("MCP", "A live check threw", ex); }
                try { await Task.Delay(LiveTick, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, CancellationToken.None);
    }

    /// <summary>One round of the checks. Public so the contract harness can run them without waiting for the clock.</summary>
    public async Task RunLiveChecksAsync(CancellationToken ct = default)
    {
        await CheckLivenessAsync(ct);
        await FollowBrainAsync(ct);
    }

    // ─── Is it still answering? ───────────────────────────────────────

    private LiveState Live(string name) => _live.GetOrAdd(name, _ => new LiveState());

    /// <summary>A connection came up: everything known about the one before it is over.</summary>
    private void NoteConnected(string name)
    {
        _live[name] = new LiveState { HeardUtc = DateTime.UtcNow, PingedUtc = DateTime.UtcNow };
    }

    /// <summary>The server answered something, so it is not hung — whatever the pings said.</summary>
    private void NoteHeard(string name, bool fromPing = false)
    {
        var live = Live(name);
        bool recovered;
        lock (live)
        {
            live.HeardUtc = DateTime.UtcNow;
            live.Missed = 0;
            if (fromPing) live.Answered = true;
            recovered = live.Unresponsive;
            live.Unresponsive = false;
        }
        if (!recovered) return;
        Log(name, "It answers again.");
        RaiseServerChanged(name);
    }

    private void NoteMissedPing(string name)
    {
        var live = Live(name);
        bool went = false;
        lock (live)
        {
            if (!live.Answered) { live.NoPing = true; return; }
            live.Missed++;
            if (live.Missed >= MissedPingsBeforeUnresponsive && !live.Unresponsive)
                live.Unresponsive = went = true;
        }
        if (!went) return;
        Log(name, $"Not answering: {MissedPingsBeforeUnresponsive} pings in a row got nothing back.");
        RaiseServerChanged(name);
    }

    private int CallsInFlight(IMcpTransport transport)
        => _calls.TryGetValue(transport, out var c) ? Volatile.Read(ref c.Count) : 0;

    /// <summary>
    /// Ping the servers that are due. A server with a call in flight is left alone: brainx-mcp reads its stdin on one
    /// thread, so during a long brain_synthesize a ping would wait for it and a working server would look hung.
    /// </summary>
    public async Task CheckLivenessAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = new List<Task>();
        foreach (var (name, transport) in _transports.ToArray())
        {
            if (RawState(name) != McpServerState.Ready || CallsInFlight(transport) > 0) continue;
            var live = Live(name);
            bool remote = _configs.TryGetValue(name, out var cfg) && cfg.IsRemote;
            lock (live)
            {
                if (live.NoPing || now - live.PingedUtc < (remote ? RemotePingEvery : PingEvery)) continue;
                live.PingedUtc = now;
            }
            due.Add(PingAsync(name, transport, ct));
        }
        await Task.WhenAll(due);
    }

    private async Task PingAsync(string name, IMcpTransport transport, CancellationToken ct)
    {
        bool answered = false;
        try
        {
            var reply = await transport.SendRequestAsync("ping", new { }, (int)PingTimeout.TotalMilliseconds, ct);
            // "No such method" is a server at work. Any other error is not: a BrainX launcher whose worker is gone
            // answers everything with -32603.
            answered = reply.IsSuccess || reply.Error?.Code == JsonRpcMethodNotFound;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }

        if (!_transports.TryGetValue(name, out var current) || !ReferenceEquals(current, transport)) return;
        if (answered) NoteHeard(name, fromPing: true);
        else if (CallsInFlight(transport) == 0) NoteMissedPing(name);   // a call that began meanwhile explains the silence
    }

    // ─── Which build is on disk? ──────────────────────────────────────

    private static string NormalizeVersion(string? version)
    {
        string v = (version ?? "").Trim().TrimStart('v', 'V');
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        if (v.Count(c => c == '.') == 3 && v.EndsWith(".0", StringComparison.Ordinal)) v = v[..^2];   // file versions have 4 parts
        return v;
    }

    private static bool SameVersion(string? a, string? b)
        => string.Equals(NormalizeVersion(a), NormalizeVersion(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder the brain runs from, or null when it isn't a brainx-mcp program (any server with "brain" in
    /// its name counts as the brain, and a third-party one has no such folder to follow).</summary>
    private static string? BrainProgramDir(McpServerConfig cfg)
    {
        var routed = RouteBrainLaunch(cfg, BrainXLocalAppData(), BrainMirrorPath(), File.Exists);
        string cmd = (routed?.Command ?? "").Trim().Trim('"');
        try
        {
            if (!Path.IsPathRooted(cmd)) return null;
            return Path.GetFileNameWithoutExtension(cmd).Equals("brainx-mcp", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(cmd) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The build on disk: brainx-mcp.dll's own version (what the launcher watches, and what a dev deploy changes
    /// without touching the marker), else runtime.version. Null when neither can be read, as in the moment of a swap.
    /// </summary>
    private InstalledBrainBuild? ReadInstalledBrain(McpServerConfig cfg)
    {
        if (BrainProgramDir(cfg) is not { } dir) return null;
        try
        {
            string dll = Path.Combine(dir, "brainx-mcp.dll");
            string marker = Path.Combine(dir, "runtime.version");
            bool hasDll = File.Exists(dll);
            string file = hasDll ? dll : marker;
            if (!File.Exists(file)) return null;
            var info = new FileInfo(file);
            string stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            string key = file + "|" + stamp;
            if (_installedCache is { } hit && hit.Item1 == key) return hit.Item2;
            string? version = hasDll ? FileVersionInfo.GetVersionInfo(dll).ProductVersion : File.ReadAllText(marker);
            version = NormalizeVersion(version);
            if (version.Length == 0) return null;
            var build = new InstalledBrainBuild(version, stamp);
            _installedCache = Tuple.Create(key, build);
            return build;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private bool IsFollowedBrain(string name, out McpServerConfig cfg)
    {
        if (LooksLikeBrain(name) && _configs.TryGetValue(name, out var c) && c.Enabled && !c.IsRemote && string.IsNullOrEmpty(c.ManagedBy)
            && BrainProgramDir(c) != null)
        {
            cfg = c;
            return true;
        }
        cfg = null!;
        return false;
    }

    // ─── Following BrainX ─────────────────────────────────────────────

    /// <summary>
    /// One look at the brain. Installed while CluadeX runs → connected by the startup rules. Down, and a new build
    /// arrived → one more start. Up on a build that isn't the one on disk → the launcher gets
    /// <see cref="BrainSwapGrace"/>, then the new build is started beside the old. A brain the user stopped stays stopped.
    /// </summary>
    public async Task FollowBrainAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        string? name = BrainServerName;
        if (name == null)
        {
            if (GetBrainInstallState() == BrainInstall.InstalledNotConfigured && !_settingsService.Settings.BrainXAutoConnectDeclined
                && !_configLoadFailed && Interlocked.Exchange(ref _autoConnectTried, 1) == 0)
            {
                Log("brainx-brain", "BrainX was installed while CluadeX was running — connecting it.");
                await AutoConnectInstalledBrainAsync(ct);
            }
            return;
        }
        if (!IsFollowedBrain(name, out var cfg)) return;

        var installed = ReadInstalledBrain(cfg);
        if (installed != null && !ReferenceEquals(installed, _installedBrain))
        {
            _installedBrain = installed;
            RaiseServerChanged(name);
        }

        var state = GetServerState(name);
        if (state == McpServerState.Failed && !_reconnecting.ContainsKey(name))
        {
            if (installed == null) return;
            if (_failedOnStamp == null) { _failedOnStamp = installed.Stamp; return; }
            if (_failedOnStamp == installed.Stamp) return;
            _failedOnStamp = installed.Stamp;
            Log(name, $"A new build ({installed.Version}) is on disk — starting it.");
            _rapidCrashes.TryRemove(name, out _);
            await StartServerAsync(name, ct);
            return;
        }
        if (state != McpServerState.Ready) return;
        _failedOnStamp = null;

        if (!IsResponding(name) && DateTime.UtcNow - _lastHealUtc >= HealCooldown)
        {
            _lastHealUtc = DateTime.UtcNow;
            Log(name, "It stopped answering — starting a new one beside it.");
            await ReplaceBrainAsync(name, installed, ct);
            return;
        }
        if (installed == null || ServerVersion(name) is not { } running) return;
        if (SameVersion(installed.Version, running))
        {
            if (IsResponding(name)) ClearUpdate(name);   // a healing that failed stays on the page until it is healed
            return;
        }

        var update = GetUpdateStatus(name);
        if (update == null || update.Stamp != installed.Stamp)
        {
            _updates[name] = new McpUpdateStatus(McpUpdateKind.Waiting, installed.Version, installed.Stamp, DateTime.UtcNow);
            Log(name, $"Version {installed.Version} is on disk, this connection runs {running} — BrainX's launcher moves to it when idle.");
            RaiseServerChanged(name);
            return;
        }
        if (update.Kind != McpUpdateKind.Waiting || DateTime.UtcNow - update.SinceUtc < BrainSwapGrace) return;

        // The launcher had its chance. Its swap may have gone unnoticed; only the server can say.
        await ConfirmBrainVersionAsync(name, ct);
        if (SameVersion(installed.Version, ServerVersion(name))) return;
        await ReplaceBrainAsync(name, installed, ct);
    }

    private void ClearUpdate(string name)
    {
        if (_updates.TryRemove(name, out _)) RaiseServerChanged(name);
    }

    /// <summary>tools/list_changed also means "the launcher swapped its worker": if the build on disk is not what this
    /// connection reported, ask.</summary>
    private async Task OnToolsListChangedAsync(string name, IMcpTransport from)
    {
        await RefreshToolsAsync(name, CancellationToken.None);
        if (!_transports.TryGetValue(name, out var current) || !ReferenceEquals(current, from)) return;
        if (!IsFollowedBrain(name, out var cfg) || ReadInstalledBrain(cfg) is not { } installed) return;
        if (!SameVersion(installed.Version, ServerVersion(name)))
            await ConfirmBrainVersionAsync(name, CancellationToken.None);
    }

    /// <summary>
    /// Ask the running brain its version with a second initialize — the launcher's replay is the only other one, and
    /// it swallows that answer. Not something to do to an arbitrary MCP server: BrainX's launcher keeps the latest
    /// handshake for its next replay, and the worker's initialize is safe to repeat (its heartbeat and canary guard
    /// themselves). brain_stats also says, but that is a journaled tool call that walks the whole brain.
    /// </summary>
    private async Task<bool> ConfirmBrainVersionAsync(string name, CancellationToken ct)
    {
        if (!_transports.TryGetValue(name, out var transport) || !transport.IsAlive) return false;
        try
        {
            var reply = await transport.SendRequestAsync("initialize", InitializeParams(), (int)PingTimeout.TotalMilliseconds, ct);
            if (!reply.IsSuccess) return false;
            await transport.SendNotificationAsync("notifications/initialized", ct: ct);
            if (ReadServerVersion(reply) is not { } version) return false;
            if (!_transports.TryGetValue(name, out var current) || !ReferenceEquals(current, transport)) return false;

            string? before = ServerVersion(name);
            _serverVersions[name] = version;
            NoteHeard(name);
            if (!SameVersion(before, version)) Log(name, $"Now running version {version} (was {before}).");
            if (GetUpdateStatus(name) is { } update && SameVersion(update.TargetVersion, version)) _updates.TryRemove(name, out _);
            RaiseServerChanged(name);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.Debug("MCP", $"[{name}] could not ask for its version: {ex.Message}");
            return false;
        }
    }

    // ─── Moving to a new build ────────────────────────────────────────

    /// <summary>
    /// Start the brain again BESIDE the running process and move to the new one only once it has answered, listed its
    /// tools and stayed up. Until that moment nothing has changed: the state stays Ready, the tools stay registered,
    /// and a build that fails leaves the old process serving. After it, new calls go to the new process and the old
    /// one finishes what it was given. Two brain processes at once is normal here (every agent session has one).
    /// </summary>
    private async Task<bool> ReplaceBrainAsync(string name, InstalledBrainBuild? target, CancellationToken ct)
    {
        var gate = _startGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        IMcpTransport? candidate = null;
        string? running = ServerVersion(name);
        string targetVersion = target?.Version ?? running ?? "";
        string stamp = target?.Stamp ?? "";
        try
        {
            if (_disposed || !_configs.TryGetValue(name, out var cfg)) return false;
            if (!_transports.TryGetValue(name, out var old) || !_supervised.ContainsKey(name)) return false;   // stopped, or crashing: not ours

            _updates[name] = new McpUpdateStatus(McpUpdateKind.Switching, targetVersion, stamp, DateTime.UtcNow);
            RaiseServerChanged(name);

            var launch = await PrepareBrainLaunchAsync(name, cfg, ct);
            candidate = CreateTransport(name, launch);
            _retiring[candidate] = 1;
            Log(name, $"Starting version {targetVersion} beside the running one: {launch.Command}");
            await candidate.StartAsync(launch);
            var reply = await candidate.SendRequestAsync("initialize", InitializeParams(), StartupTimeoutFor(name, launch) * 1000, ct);
            if (!reply.IsSuccess) throw new InvalidOperationException(reply.Error?.Message ?? "initialize failed");
            string? version = ReadServerVersion(reply);
            await candidate.SendNotificationAsync("notifications/initialized", ct: ct);

            var tools = await ListToolsAsync(candidate, name, ct)
                ?? throw new InvalidOperationException("it did not list its tools");
            if (tools.Count == 0 && _toolRegistry.GetToolsForServer(name).Count > 0)
                throw new InvalidOperationException("it offers no tools");

            await Task.Delay(SwapSoak, ct);
            if (!candidate.IsAlive)
                throw new InvalidOperationException($"it exited right after starting: {candidate.LastStartupError.Trim()}");

            // The one moment anything changes. Failing it means the old connection went away by itself (Stop, a crash):
            // the supervisor owns that, and a new process must not come back behind a Stop.
            if (!_supervised.ContainsKey(name) || !_transports.TryUpdate(name, candidate, old))
            {
                _updates.TryRemove(name, out _);
                await StopTransportAsync(candidate);
                candidate = null;
                RaiseServerChanged(name);
                return false;
            }
            _retiring.TryRemove(candidate, out _);
            _retiring[old] = 1;
            candidate = null;

            if (version != null) _serverVersions[name] = version;
            else _serverVersions.TryRemove(name, out _);
            _toolRegistry.UpdateServer(name, tools);
            _readySinceUtc[name] = DateTime.UtcNow;
            NoteConnected(name);
            StartHistory.Record(name, launch);
            _updates.TryRemove(name, out _);
            try { OnToolsChanged?.Invoke(); } catch { }
            Log(name, $"Now running version {version ?? "?"} (was {running ?? "?"}), {tools.Count} tools.");
            RaiseServerChanged(name);
            _ = Task.Run(() => DrainAndStopAsync(name, old));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            string reason = ex is TimeoutException ? string.Format(L("mcp.handshake.timedOut"), SwapTimeoutSeconds(name)) : ex.Message;
            if (candidate != null && candidate.LastStartupError.Trim() is { Length: > 0 } stderr && !reason.Contains(stderr)) reason += " — " + stderr;
            _updates[name] = new McpUpdateStatus(McpUpdateKind.Failed, targetVersion, stamp, DateTime.UtcNow, reason);
            _log?.Warn("MCP", $"[{name}] version {targetVersion} did not start; staying on {running}: {reason}");
            Log(name, $"Version {targetVersion} did not start; staying on {running}. {reason}");
            RaiseServerChanged(name);
            return false;
        }
        finally
        {
            if (candidate != null) await StopTransportAsync(candidate);
            gate.Release();
        }
    }

    private int SwapTimeoutSeconds(string name)
        => _configs.TryGetValue(name, out var cfg) ? StartupTimeoutFor(name, cfg) : DefaultStartupTimeoutSeconds;

    /// <summary>What a start of the brain would spawn right now, by the same rules as <see cref="StartServerAsync"/>.</summary>
    private async Task<McpServerConfig> PrepareBrainLaunchAsync(string name, McpServerConfig cfg, CancellationToken ct)
    {
        cfg = RefreshBrainEntryFromDisk(name, cfg);
        var routed = RouteBrainLaunch(cfg, BrainXLocalAppData(), BrainMirrorPath(), File.Exists)
            ?? throw new InvalidOperationException(string.Format(L("mcp.brain.mirrorMissing"), name));
        var (launch, refusal) = await ResolveLaunchAsync(name, routed, ct);
        return refusal != null ? throw new InvalidOperationException(refusal) : launch;
    }

    private async Task StopTransportAsync(IMcpTransport transport)
    {
        try { await transport.StopAsync(); } catch { }
        try { transport.Dispose(); } catch { }
        _retiring.TryRemove(transport, out _);
    }

    /// <summary>A replaced process keeps the calls it was given. Nothing new reaches it (see
    /// <see cref="CallToolWithObjectArgsAsync"/>), so the count only goes down; it stops when that reaches zero.</summary>
    private async Task DrainAndStopAsync(string name, IMcpTransport old)
    {
        var deadline = DateTime.UtcNow + DrainLimit;
        while (CallsInFlight(old) > 0 && DateTime.UtcNow < deadline && !_disposed)
        {
            try { await Task.Delay(100); } catch { return; }
        }
        if (_disposed) return;   // Dispose stops everything in _retiring
        await StopTransportAsync(old);
        Log(name, "The previous process has finished and stopped.");
    }

    /// <summary>serverInfo.version from an initialize reply, or null.</summary>
    private static string? ReadServerVersion(JsonRpcResponse reply)
    {
        try
        {
            if (reply.Result is { ValueKind: JsonValueKind.Object } res && res.TryGetProperty("serverInfo", out var si)
                && si.ValueKind == JsonValueKind.Object && si.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } version)
                return version;
        }
        catch { }
        return null;
    }

    private static McpInitializeParams InitializeParams() => new()
    {
        ClientInfo = new McpClientInfo { Name = "CluadeX", Version = AutoUpdateService.CurrentVersion },
    };
}
