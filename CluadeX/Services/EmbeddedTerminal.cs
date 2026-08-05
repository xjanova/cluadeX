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

    /// <summary>A line of terminal output (stdout and stderr interleaved). Raised on a worker thread.</summary>
    public event Action<string>? OutputReceived;
    /// <summary>Raised when the shell process exits (crash or user ran `exit`).</summary>
    public event Action? Exited;

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Start (or restart) the shell in the given directory.</summary>
    public void Start(string workingDir)
    {
        Stop();
        _workingDir = workingDir;
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
            if (_proc == null) { OutputReceived?.Invoke("[terminal] failed to start powershell"); return; }

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => Exited?.Invoke();
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // Make PowerShell's own output UTF-8 so Thai/emoji round-trip.
            Send("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[terminal] start failed: {ex.Message}");
        }
    }

    /// <summary>Run one command line in the persistent session.</summary>
    public void Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            if (!IsRunning) Start(_workingDir);
            _proc!.StandardInput.WriteLine(command);
            _proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"[terminal] send failed: {ex.Message}");
        }
    }

    /// <summary>Follow a project switch without killing the session.</summary>
    public void ChangeDirectory(string dir)
    {
        _workingDir = dir;
        if (IsRunning && Directory.Exists(dir))
            Send($"cd \"{dir}\"");
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }
    }

    public void Dispose() => Stop();
}
