using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// Runs a Node project's dev server for the UI Studio: installs dependencies first when node_modules is missing,
/// then runs the "dev" script (or "start") with the project's package manager, and reports the first local URL the
/// server prints — once that port accepts connections (some servers print their address before they listen). Only
/// the dev process's own output is read for it: an install that names a local registry is not the page.
/// Stop() and Dispose() kill the whole process tree, so no node.exe is left behind.
/// </summary>
public sealed class DevServerRunner : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex LocalUrl = new(@"https?://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0):\d+[^\s'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _root;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private int _urlSeen;

    /// <summary>How long the port of the printed URL is waited for before UrlFound is raised anyway (the page's own
    /// retries take it from there).</summary>
    public static readonly TimeSpan PortWait = TimeSpan.FromSeconds(20);

    public event Action<string>? Output;      // one line (ANSI stripped), on a pool thread
    public event Action<string>? UrlFound;    // the first local URL, once, when its port answers (on a pool thread)
    public event Action<int>? Exited;         // exit code, on a pool thread

    public bool IsRunning => _process is { HasExited: false };
    public string? Url { get; private set; }

    public DevServerRunner(string root) => _root = root;

    /// <summary>pnpm / yarn / bun when their lock file is there, else npm.</summary>
    public static string PackageManagerFor(string root) =>
        File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm"
        : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn"
        : File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")) ? "bun"
        : "npm";

    /// <summary>Install (when needed) and start. Returns false with a reason when it could not start.</summary>
    public async Task<(bool Ok, string? Error)> StartAsync(string script, CancellationToken ct)
    {
        if (IsRunning) return (true, null);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string pm = PackageManagerFor(_root);

        // Installing and starting run the project's own code (install hooks, the dev script) — code an agent may just
        // have written. The same always-on check as run_command reads what changed first (package.json included).
        string runLine = pm == "npm" ? $"npm run {script}" : $"{pm} {script}";
        if (await Safety.DangerApproval.GateAsync("UI Studio", $"{pm} install && {runLine}",
                await Safety.CodeRunGuard.CheckAsync($"{pm} install && {runLine}", _root)) is { } unsafeRun)
        {
            foreach (var line in unsafeRun.Split('\n')) Output?.Invoke(line);
            return (false, "unsafe");
        }
        // stopped during the check (the chat moved to another project): nothing may start now, it would never be stopped
        if (ct.IsCancellationRequested) return (false, "cancelled");
        if (Safety.ProcessLaunchPolicy.DryRun)
        {
            Output?.Invoke(Safety.ProcessLaunchPolicy.Report($"{pm} install && {runLine}"));
            return (false, "cancelled");
        }

        if (!Directory.Exists(Path.Combine(_root, "node_modules")))
        {
            Output?.Invoke($"$ {pm} install");
            int code;   // (its output is shown, never searched for the page's URL)
            try { code = await RunToEndAsync($"{pm} install", _cts.Token); }
            catch (OperationCanceledException) { return (false, "cancelled"); }
            catch (Exception ex) { return (false, "noNode:" + ex.Message); }
            if (code == 9009) return (false, "noNode:" + pm);   // cmd: "'npm' is not recognized"
            if (code != 0) return (false, $"install:{code}");
        }

        string run = pm == "npm" ? $"npm run {script}" : $"{pm} {script}";
        Output?.Invoke("$ " + run);
        try
        {
            var p = NewProcess(run, findUrl: true);
            p.Exited += (_, _) => { try { Exited?.Invoke(p.ExitCode); } catch { } };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "noNode:" + ex.Message);
        }
    }

    private Process NewProcess(string command, bool findUrl = false)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // no console of ours to read from; an install's stdin is closed (EOF)
        };
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["BROWSER"] = "none";   // CRA / Vite must not open the user's browser
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data, findUrl);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data, findUrl);
        return p;
    }

    private async Task<int> RunToEndAsync(string command, CancellationToken ct)
    {
        using var p = NewProcess(command);
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { Kill(p); throw; }
        return p.ExitCode;
    }

    private void OnLine(string? line, bool findUrl)
    {
        if (line == null) return;
        string clean = Ansi.Replace(line, "");
        try { Output?.Invoke(clean); } catch { }
        if (!findUrl || Volatile.Read(ref _urlSeen) != 0 || MatchLocalUrl(clean) is not { } url) return;
        if (Interlocked.Exchange(ref _urlSeen, 1) != 0) return;
        CancellationToken token;
        try { token = _cts?.Token ?? CancellationToken.None; }
        catch (ObjectDisposedException) { return; }   // (a last line after Dispose: on a reader thread, nothing may throw)
        _ = AnnounceAsync(url, token);
    }

    /// <summary>Wait (bounded) for the printed address to accept connections, then report it — unless the server
    /// was stopped meanwhile.</summary>
    private async Task AnnounceAsync(string url, CancellationToken ct)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) await WaitForPortAsync(u, PortWait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { /* could not even try: report it anyway */ }
        if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;   // stopped while it waited
        Url = url;
        try { UrlFound?.Invoke(url); } catch { }
    }

    /// <summary>The first local address in a line of a dev server's output (ANSI codes removed, 0.0.0.0 → localhost,
    /// trailing punctuation dropped); null when there is none.</summary>
    public static string? MatchLocalUrl(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = LocalUrl.Match(Ansi.Replace(line, ""));
        if (!m.Success) return null;
        return m.Value.Replace("0.0.0.0", "localhost").TrimEnd('.', ',', ')', ';');
    }

    /// <summary>Wait until something accepts TCP connections on the URL's port (localhost: IPv4 and IPv6 both tried),
    /// checking every 250 ms for at most <paramref name="timeout"/>. True when it answered, false when the time ran
    /// out; cancelling throws <see cref="OperationCanceledException"/>. Only loopback hosts are dialled.</summary>
    public static async Task<bool> WaitForPortAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        var addresses = url.IsLoopback || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? url.HostNameType == UriHostNameType.IPv6 ? new[] { IPAddress.IPv6Loopback }
              : url.HostNameType == UriHostNameType.IPv4 ? new[] { IPAddress.Parse(url.Host) }
              : new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
            : Array.Empty<IPAddress>();
        if (addresses.Length == 0) return false;
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var address in addresses)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    // (inside the try: a machine with IPv6 turned off refuses even to make the IPv6 socket — the other
                    // address is still waited for instead of the URL being reported before anything listens)
                    using var client = new TcpClient(address.AddressFamily);
                    await client.ConnectAsync(address, url.Port, attempt.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (SocketException) { }
            }
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        var p = _process;
        _process = null;
        Url = null;
        Interlocked.Exchange(ref _urlSeen, 0);
        if (p != null) { Kill(p); p.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _cts?.Dispose();
    }
}
