using System.Diagnostics;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Manages a persistent interactive REPL process (Python, Node.js, etc.).
/// Keeps stdin/stdout open between calls for state persistence.
/// </summary>
public sealed class ReplSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _lock = new();
    private bool _disposed;

    // Sentinel markers to detect end of output
    private const string BeginMarker = "___REPL_BEGIN___";
    private const string EndMarker = "___REPL_END___";

    public bool HasExited => _disposed || _process.HasExited;

    public ReplSession(string executable, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Ensure Python doesn't buffer output
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_lock)
                    _outputBuffer.AppendLine(e.Data);
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_lock)
                    _outputBuffer.AppendLine(e.Data);
            }
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Wait briefly for the REPL to start
        Thread.Sleep(500);

        // Drain initial startup output (prompts, banners)
        lock (_lock)
            _outputBuffer.Clear();
    }

    /// <summary>
    /// Execute code in the REPL and return output.
    /// Uses sentinel markers to detect when execution is complete.
    /// </summary>
    public async Task<string> ExecuteAsync(string code, CancellationToken ct, int timeoutMs = 15000)
    {
        if (HasExited)
            throw new InvalidOperationException("REPL session has exited");

        // Clear buffer
        lock (_lock)
            _outputBuffer.Clear();

        // Send the code followed by a sentinel print
        foreach (var line in code.Split('\n'))
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        }

        // Send sentinel to detect end of execution
        string sentinelCmd = _process.StartInfo.FileName.Contains("python")
            ? $"print('{EndMarker}')"
            : $"console.log('{EndMarker}')";

        await _process.StandardInput.WriteLineAsync(sentinelCmd.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);

        // Wait for sentinel in output
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            string currentOutput;
            lock (_lock)
                currentOutput = _outputBuffer.ToString();

            if (currentOutput.Contains(EndMarker))
            {
                // Remove sentinel and clean up
                string result = currentOutput
                    .Replace(EndMarker, "")
                    .TrimEnd();

                // Remove Python/Node prompts
                result = CleanReplOutput(result);
                return result;
            }

            await Task.Delay(50, ct);
        }

        // Timeout — return what we have
        string partial;
        lock (_lock)
            partial = _outputBuffer.ToString();

        throw new TimeoutException($"REPL execution timed out after {timeoutMs}ms. Partial output:\n{CleanReplOutput(partial)}");
    }

    private static string CleanReplOutput(string output)
    {
        // Remove common REPL prompts
        var lines = output.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            string trimmed = line.TrimEnd();
            // Skip empty prompt lines
            if (trimmed is ">>>" or "..." or "> " or "undefined")
                continue;
            // Remove leading prompts
            if (trimmed.StartsWith(">>> "))
                trimmed = trimmed[4..];
            else if (trimmed.StartsWith("... "))
                trimmed = trimmed[4..];
            else if (trimmed.StartsWith("> "))
                trimmed = trimmed[2..];

            sb.AppendLine(trimmed);
        }
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000))
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
            _process.Dispose();
        }
        catch { /* ignore cleanup errors */ }
    }
}
