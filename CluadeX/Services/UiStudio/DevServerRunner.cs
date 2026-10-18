using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// Runs a Node project's dev server for the UI Studio: installs dependencies first when node_modules is missing,
/// then runs the "dev" script (or "start") with the project's package manager, and reports the first local URL the
/// server prints. Stop() and Dispose() kill the whole process tree, so no node.exe is left behind.
/// </summary>
public sealed class DevServerRunner : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex LocalUrl = new(@"https?://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0):\d+[^\s'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _root;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public event Action<string>? Output;      // one line (ANSI stripped), on a pool thread
    public event Action<string>? UrlFound;    // the first local URL, once
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
        if (Safety.ProcessLaunchPolicy.DryRun)
        {
            Output?.Invoke(Safety.ProcessLaunchPolicy.Report($"{pm} install && {runLine}"));
            return (false, "cancelled");
        }

        if (!Directory.Exists(Path.Combine(_root, "node_modules")))
        {
            Output?.Invoke($"$ {pm} install");
            int code;
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
            var p = NewProcess(run);
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

    private Process NewProcess(string command)
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
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
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

    private void OnLine(string? line)
    {
        if (line == null) return;
        string clean = Ansi.Replace(line, "");
        try { Output?.Invoke(clean); } catch { }
        if (Url == null)
        {
            var m = LocalUrl.Match(clean);
            if (m.Success)
            {
                Url = m.Value.Replace("0.0.0.0", "localhost").TrimEnd('.', ',', ')');
                try { UrlFound?.Invoke(Url); } catch { }
            }
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
        if (p != null) { Kill(p); p.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _cts?.Dispose();
    }
}
