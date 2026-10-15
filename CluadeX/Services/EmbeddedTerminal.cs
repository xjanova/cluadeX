using System.Diagnostics;
using System.IO;

namespace CluadeX.Services;

/// <summary>
/// A real embedded terminal for the Code Editor drawer (replaces the old decorative status strip):
/// one persistent PowerShell process with redirected stdio, rooted at the project folder.
/// Commands are written to stdin; stdout/stderr stream back via <see cref="OutputReceived"/>.
/// Deliberately minimal — it is a working command lane, not a full PTY (no cursor apps like vim).
/// </summary>
public class EmbeddedTerminal : IDisposable
{
    private Process? _proc;
    private string _workingDir = "";
    private bool _disposed;

    /// <summary>Bumped on every Start/Stop. Output and Exited callbacks from a shell we already
    /// killed carry an older generation and are dropped — otherwise stopping a dev server printed
    /// "terminal exited" for a shell the user had just deliberately restarted.</summary>
    private int _generation;

    /// <summary>A line of terminal output (stdout and stderr interleaved). Raised on a worker thread.</summary>
    public event Action<string>? OutputReceived;
    /// <summary>Raised when the shell process exits on its own (crash or user ran `exit`) — not when
    /// <see cref="Stop"/> / <see cref="Interrupt"/> killed it.</summary>
    public event Action? Exited;

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Start (or restart) the shell in the given directory.</summary>
    public void Start(string workingDir)
    {
        if (_disposed) return;
        Stop();
        _workingDir = workingDir;
        int gen = Interlocked.Increment(ref _generation);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -NoExit keeps the pipeline session alive between commands.
                Arguments = "-NoLogo -NoProfile -NoExit -Command -",
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            _proc = Process.Start(psi);
            if (_proc == null) { OutputReceived?.Invoke(LocalizedResources.Instance["term.startFailed"]); return; }

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => { if (gen == Volatile.Read(ref _generation)) Exited?.Invoke(); };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null && gen == Volatile.Read(ref _generation)) OutputReceived?.Invoke(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null && gen == Volatile.Read(ref _generation)) OutputReceived?.Invoke(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // Make PowerShell's own output UTF-8 so Thai/emoji round-trip.
            Send("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'");
        }
        catch
        {
            OutputReceived?.Invoke(LocalizedResources.Instance["term.startFailed"]);
        }
    }

    /// <summary>Run one command line in the persistent session.</summary>
    public void Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || _disposed) return;
        try
        {
            if (!IsRunning) Start(_workingDir);
            _proc!.StandardInput.WriteLine(command);
            _proc.StandardInput.Flush();
        }
        catch
        {
            OutputReceived?.Invoke(LocalizedResources.Instance["term.sendFailed"]);
        }
    }

    /// <summary>
    /// The PowerShell line that moves the session to <paramref name="dir"/>. Single-quoted and
    /// -LiteralPath: inside double quotes PowerShell EXPANDS <c>$(...)</c> and <c>$var</c>, so a
    /// cloned repo in a folder named <c>x$(Start-Process calc)</c> ran code the moment the project
    /// was opened. In a single-quoted string the only special character is the quote itself,
    /// escaped by doubling; -LiteralPath also stops '[' ']' being read as wildcards.
    /// </summary>
    public static string BuildSetLocationCommand(string dir)
        => $"Set-Location -LiteralPath '{dir.Replace("'", "''")}'";

    /// <summary>Follow a project switch without killing the session.</summary>
    public void ChangeDirectory(string dir)
    {
        _workingDir = dir;
        if (IsRunning && Directory.Exists(dir))
            Send(BuildSetLocationCommand(dir));
    }

    /// <summary>
    /// Stop whatever is running (a dev server, a hung build) and give back a fresh prompt. With
    /// redirected stdio there is no console to deliver a real Ctrl+C to, so this kills the shell's
    /// whole process tree — the only reliable way to take a watcher or server down with it — and
    /// starts a new shell in the same folder.
    /// </summary>
    public void Interrupt()
    {
        if (_disposed) return;
        bool wasRunning = IsRunning;
        Stop();
        if (wasRunning) Start(_workingDir);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);   // silence the dying shell's callbacks
        var proc = _proc;
        _proc = null;
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary>Kills the shell and everything it started. Must run on app exit: a dev server started
    /// from this terminal is a child of OUR shell, and used to outlive the app holding its port.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
