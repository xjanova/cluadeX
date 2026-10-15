using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// Runs the agent's shell commands.
///
/// Models write POSIX shell — they were trained on Linux, macOS and Claude Code transcripts, and
/// nothing told them this machine is Windows. run_command used to hand that text to cmd.exe, where
/// the Unix tools on PATH (ls, cat, grep, rm) happen to work but cmd's BUILT-INS shadow the rest:
/// `mkdir -p a/b` failed with "The syntax of the command is incorrect", `echo สวัสดี` came back as
/// mojibake (cmd writes the OEM code page), and `cd App` was forgotten by the next call.
///
/// So on Windows the command runs in Git Bash when it is installed — the same choice Claude Code makes —
/// with the script passed through a UTF-8 temp file (no command-line quoting to get wrong), stdin
/// closed, and the working directory carried from one call to the next. cmd.exe (switched to UTF-8)
/// is the fallback. Output from BOTH streams is captured incrementally, so a command that times out
/// still returns what it printed, and a runaway command cannot exhaust memory.
/// </summary>
public static class ShellRunner
{
    public enum Shell { Bash, Cmd, PowerShell }

    public sealed record Result(int ExitCode, string Output, bool TimedOut, bool Cancelled, string? NewCwd, Shell Shell);

    private const string CwdSentinel = "__CLUADEX_CWD__";

    // ───────────────────────── shell discovery ─────────────────────────

    private static readonly object ProbeLock = new();
    private static bool _probed;
    private static string? _bashPath;

    /// <summary>
    /// Git Bash's bash.exe (never WSL's System32\bash.exe, which runs in a different filesystem), or
    /// /bin/bash off Windows. Null when not installed. Probed once; CLUADEX_BASH overrides.
    /// </summary>
    public static string? FindBash()
    {
        lock (ProbeLock)
        {
            if (_probed) return _bashPath;
            _probed = true;

            if (!OperatingSystem.IsWindows())
                return _bashPath = File.Exists("/bin/bash") ? "/bin/bash" : null;

            var candidates = new List<string>();
            string? env = Environment.GetEnvironmentVariable("CLUADEX_BASH");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env.Trim('"'));

            // git --exec-path → <git>/mingw64/libexec/git-core → <git>/bin/bash.exe
            try
            {
                var psi = new ProcessStartInfo("git", "--exec-path")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                string? exec = p?.StandardOutput.ReadToEnd().Trim();
                p?.WaitForExit(5000);
                if (!string.IsNullOrEmpty(exec))
                {
                    string root = Path.GetFullPath(Path.Combine(exec, "..", "..", ".."));
                    candidates.Add(Path.Combine(root, "bin", "bash.exe"));
                    candidates.Add(Path.Combine(root, "usr", "bin", "bash.exe"));
                }
            }
            catch { /* git not on PATH */ }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"));

            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            foreach (var c in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(c) || !File.Exists(c)) continue;
                    if (c.StartsWith(system32, StringComparison.OrdinalIgnoreCase)) continue;   // WSL
                    return _bashPath = c;
                }
                catch { }
            }
            return _bashPath = null;
        }
    }

    /// <summary>Which shell run_command will use — for the tool description the model reads.</summary>
    public static Shell DefaultShell => FindBash() != null ? Shell.Bash : Shell.Cmd;

    // ───────────────────────── running ─────────────────────────

    /// <param name="cwd">Absolute working directory for the command.</param>
    /// <param name="captureChars">Maximum characters kept in memory (head + tail) — not the model budget.</param>
    public static async Task<Result> RunAsync(Shell shell, string command, string cwd, int timeoutMs,
        CancellationToken ct, int captureChars = 2_000_000)
    {
        string? tempScript = null;
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // A build or git call left waiting on a prompt, a pager or a lingering MSBuild node would hold
        // the pipes open and look like a hang to the model.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        switch (shell)
        {
            case Shell.Bash:
            {
                string bash = FindBash() ?? throw new InvalidOperationException("bash is not installed");
                tempScript = Path.Combine(Path.GetTempPath(), $"cluadex-cmd-{Guid.NewGuid():N}.sh");
                string script =
                    $"cd {BashQuote(cwd.Replace('\\', '/'))} 2>/dev/null\n" +
                    "exec </dev/null\n" +
                    command.Replace("\r\n", "\n") + "\n" +
                    "__cx_ec=$?\n" +
                    $"printf '\\n{CwdSentinel}%s\\n' \"$(pwd -W 2>/dev/null || pwd)\"\n" +
                    "exit $__cx_ec\n";
                File.WriteAllText(tempScript, script, new UTF8Encoding(false));
                psi.FileName = bash;
                psi.ArgumentList.Add("--noprofile");
                psi.ArgumentList.Add("--norc");
                psi.ArgumentList.Add(tempScript);
                psi.Environment["LANG"] = "C.UTF-8";
                psi.Environment["LC_ALL"] = "C.UTF-8";
                break;
            }
            case Shell.PowerShell:
            {
                string ps = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe") ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "powershell.exe";
                // Progress records arrive on stderr as CLIXML when redirected, and the console encoding
                // defaults to the OEM code page — both mangle what the model reads.
                string prelude = "$ProgressPreference='SilentlyContinue';" +
                                 "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
                                 "$OutputEncoding=[System.Text.UTF8Encoding]::new($false);\n";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + command));
                psi.FileName = ps;
                psi.Arguments = $"-NoProfile -NonInteractive -NoLogo -OutputFormat Text -EncodedCommand {encoded}";
                break;
            }
            default:
            {
                psi.FileName = "cmd.exe";
                // /s + outer quotes: cmd keeps everything between them verbatim. chcp 65001 makes cmd
                // and console programs write UTF-8 instead of the OEM code page.
                psi.Arguments = $"/d /s /c \"chcp 65001 >nul & {command}\"";
                break;
            }
        }

        var capture = new BoundedCapture(captureChars);
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("the shell process did not start");
            try { process.StandardInput.Close(); } catch { }

            var outPump = Pump(process.StandardOutput, capture);
            var errPump = Pump(process.StandardError, capture);

            bool timedOut = false, cancelled = false;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    cancelled = ct.IsCancellationRequested;
                    timedOut = !cancelled;
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }

            // Children that inherited the pipes (a build server, a spawned daemon) can keep them open
            // after the shell exits; never wait on them for more than a moment.
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(timedOut || cancelled ? 1500 : 3000));

            string text = capture.ToString();
            string? newCwd = null;
            if (shell == Shell.Bash) (text, newCwd) = ExtractCwd(text);
            if (shell == Shell.PowerShell) text = StripClixml(text);

            int exit = timedOut || cancelled ? -1 : SafeExitCode(process);
            return new Result(exit, text, timedOut, cancelled, newCwd, shell);
        }
        finally
        {
            try { process?.Dispose(); } catch { }
            if (tempScript != null) { try { File.Delete(tempScript); } catch { } }
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static async Task Pump(StreamReader reader, BoundedCapture capture)
    {
        var buf = new char[8192];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                capture.Append(buf, n);
        }
        catch { /* the process was killed / the pipe closed */ }
    }

    private static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static (string text, string? cwd) ExtractCwd(string text)
    {
        int at = text.LastIndexOf(CwdSentinel, StringComparison.Ordinal);
        if (at < 0) return (text, null);
        int end = text.IndexOf('\n', at);
        string cwd = (end < 0 ? text[(at + CwdSentinel.Length)..] : text[(at + CwdSentinel.Length)..end]).Trim();
        string before = text[..at];
        if (before.EndsWith("\n")) before = before[..^1];                 // the \n the trailer printed
        string after = end < 0 ? "" : text[(end + 1)..];
        return (before + after, cwd.Length > 0 ? cwd : null);
    }

    private static readonly Regex ClixmlError = new(@"<S S=""Error"">(.*?)</S>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Replace PowerShell's CLIXML stderr envelopes with the error text they carry.</summary>
    internal static string StripClixml(string text)
    {
        int start = text.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (start < 0) return text;
        var sb = new StringBuilder(text[..start]);
        string rest = text[start..];
        foreach (Match m in ClixmlError.Matches(rest))
            sb.Append(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
                .Replace("_x000D__x000A_", "\n").Replace("_x000A_", "\n").Replace("_x000D_", ""));
        int close = rest.LastIndexOf("</Objs>", StringComparison.Ordinal);
        if (close >= 0) sb.Append(rest[(close + "</Objs>".Length)..]);
        return sb.ToString();
    }

    /// <summary>Keeps the first N/2 and the most recent N/2 characters of a stream, counting the rest.</summary>
    private sealed class BoundedCapture
    {
        private readonly object _lock = new();
        private readonly int _headCap;
        private readonly int _tailCap;
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private long _dropped;

        public BoundedCapture(int cap) { _headCap = cap / 2; _tailCap = cap - _headCap; }

        public void Append(char[] buf, int n)
        {
            lock (_lock)
            {
                int i = 0;
                if (_head.Length < _headCap)
                {
                    int take = Math.Min(n, _headCap - _head.Length);
                    _head.Append(buf, 0, take);
                    i = take;
                }
                if (i >= n) return;
                _tail.Append(buf, i, n - i);
                if (_tail.Length > 2 * _tailCap)
                {
                    int drop = _tail.Length - _tailCap;
                    _tail.Remove(0, drop);
                    _dropped += drop;
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_dropped == 0) return _head.ToString() + _tail;
                return _head + $"\n… [{_dropped:N0} characters not captured] …\n" + _tail;
            }
        }
    }
}
