using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CluadeX.Services.Media;

public enum FfmpegOutcome { Done, Failed, Cancelled, TimedOut, Missing }

/// <summary>How a cut ended. <see cref="Detail"/> is ffmpeg's own last lines (for the Debug Log, never shown raw).</summary>
public sealed record FfmpegResult(FfmpegOutcome Outcome, string Detail = "");

/// <summary>
/// The optional ffmpeg the media editor uses for a lossless cut (same format, no re-encode). CluadeX does not ship
/// it: when the user has one on PATH, an audio trim and a video cut keep their format and quality; without it an
/// audio edit is saved as WAV from the page and a video cut is recorded by the page (WebM, in real time).
/// Run with an argument list (never a shell string — a file name with quotes, &amp; or Thai letters is one argument),
/// off the UI thread, cancellable, with a timeout; a stopped or failed run leaves no half-written file behind.
/// </summary>
public static class Ffmpeg
{
    /// <summary>For the harness: a path to use instead of looking ("" = act as if there were none).</summary>
    public static string? PathOverride { get; set; }

    private static string? _found;
    private static DateTime _lookedAt;

    /// <summary>The ffmpeg.exe to run, or null. Looked up on this process's PATH, then on the PATH saved for the user
    /// and the machine (installing ffmpeg with winget while CluadeX runs changes only those — the app's own copy of
    /// PATH stays as it was at launch), then winget's Links folder, then the newest ffmpeg winget unpacked (an upgrade
    /// from 8.0.1 to 9.0.2 removes the old folder but leaves PATH naming it — seen on the owner's machine), then the
    /// usual scoop / Chocolatey / Program Files places. Remembered for a minute.</summary>
    public static string? Locate()
    {
        if (PathOverride != null) return PathOverride.Length == 0 || !File.Exists(PathOverride) ? null : PathOverride;
        if (_found != null && File.Exists(_found)) return _found;
        if (DateTime.UtcNow - _lookedAt < TimeSpan.FromMinutes(1)) return _found;
        _lookedAt = DateTime.UtcNow;
        _found = Search();
        return _found;
    }

    private static string? Search()
    {
        var dirs = new List<string>();
        void AddPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (var d in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                dirs.Add(Environment.ExpandEnvironmentVariables(d.Trim('"')));
        }
        AddPath(Environment.GetEnvironmentVariable("PATH"));
        try { AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)); } catch { }
        try { AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)); } catch { }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string exe = Path.Combine(d, "ffmpeg.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { /* a malformed PATH entry */ }
        }
        return WingetPackage(Path.Combine(local, "Microsoft", "WinGet", "Packages")) ?? WellKnown();
    }

    /// <summary>winget's portable FFmpeg builds (Gyan.FFmpeg*, BtbN.FFmpeg*): Packages\&lt;id&gt;\&lt;build&gt;\bin\ffmpeg.exe,
    /// the newest one when several are there.</summary>
    private static string? WingetPackage(string packages)
    {
        try
        {
            if (!Directory.Exists(packages)) return null;
            return Directory.EnumerateDirectories(packages, "*FFmpeg*")
                .SelectMany(p => Directory.EnumerateDirectories(p))
                .Select(build => Path.Combine(build, "bin", "ffmpeg.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? WellKnown()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var exe in new[]
                 {
                     Path.Combine(home, "scoop", "shims", "ffmpeg.exe"),
                     Path.Combine(data, "chocolatey", "bin", "ffmpeg.exe"),
                     Path.Combine(programs, "ffmpeg", "bin", "ffmpeg.exe"),
                 })
        {
            try { if (File.Exists(exe)) return exe; } catch { }
        }
        return null;
    }

    /// <summary>Seconds as ffmpeg reads them: invariant "12.345" (a Thai or German locale must not write "12,345").</summary>
    public static string Seconds(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) s = 0;
        return s.ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The arguments for a lossless cut of [<paramref name="start"/>, <paramref name="end"/>) seconds. -ss before -i
    /// seeks by the index (fast on a 2 GB file); with -c copy the video starts at the keyframe at or before the start
    /// (the page says so). Video, audio and subtitle streams are kept; data streams (timecode tracks) are dropped
    /// because many containers refuse to copy them. -n: never overwrite — the output name is always a new one.
    /// </summary>
    public static IReadOnlyList<string> CutArguments(string input, string output, double start, double end)
    {
        if (end < start) (start, end) = (end, start);
        return new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", Seconds(start),
            "-i", input,
            "-t", Seconds(end - start),
            "-map", "0:v?", "-map", "0:a?", "-map", "0:s?",
            "-c", "copy",
            "-map_metadata", "0",
            "-avoid_negative_ts", "make_zero",
            "-n",
            output,
        };
    }

    /// <summary>How long a cut may take before it is stopped: copying is disk-bound, so a minute per GB plus two.</summary>
    public static TimeSpan TimeoutFor(long inputBytes)
    {
        double minutes = 2 + Math.Max(0, inputBytes) / (1024.0 * 1024 * 1024);
        return TimeSpan.FromMinutes(Math.Min(30, minutes));
    }

    /// <summary>Cut <paramref name="input"/> into <paramref name="output"/> (which must not exist). On anything but
    /// success the partial output is deleted. Never throws for an ffmpeg problem — the outcome says what happened.</summary>
    public static async Task<FfmpegResult> CutAsync(string input, string output, double start, double end,
        TimeSpan timeout, CancellationToken ct)
    {
        string? exe = Locate();
        if (exe == null) return new FfmpegResult(FfmpegOutcome.Missing);
        return await RunAsync(exe, CutArguments(input, output, start, end), output, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Run ffmpeg with <paramref name="args"/>; <paramref name="output"/> is removed unless it succeeded.
    /// An <paramref name="output"/> that already exists is refused before ffmpeg starts: it is someone's file, and
    /// ffmpeg 9 answers "-n" on an existing name with exit code 0 — "done" would then report their file as the cut.</summary>
    public static async Task<FfmpegResult> RunAsync(string exe, IReadOnlyList<string> args, string? output, TimeSpan timeout, CancellationToken ct)
    {
        if (output != null && (File.Exists(output) || Directory.Exists(output)))
            return new FfmpegResult(FfmpegOutcome.Failed, "output already exists: " + Path.GetFileName(output));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(output) is { Length: > 0 } d && Directory.Exists(d) ? d : Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var tail = new StringBuilder();
        void Keep(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (tail)
            {
                tail.AppendLine(line);
                if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);   // the last lines are the useful ones
            }
        }

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        Process? p = null;
        try
        {
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, e) => Keep(e.Data);
            p.OutputDataReceived += (_, e) => Keep(e.Data);
            if (!p.Start()) return Fail(FfmpegOutcome.Failed, "did not start");
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            p.WaitForExit();   // flushes the redirected streams
            if (p.ExitCode == 0 && (output == null || File.Exists(output))) return new FfmpegResult(FfmpegOutcome.Done, Tail());
            return Fail(FfmpegOutcome.Failed, $"exit {p.ExitCode}: {Tail()}");
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            return Fail(ct.IsCancellationRequested ? FfmpegOutcome.Cancelled : FfmpegOutcome.TimedOut, Tail());
        }
        catch (Exception ex)
        {
            Kill(p);
            return Fail(FfmpegOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            p?.Dispose();
        }

        string Tail() { lock (tail) return tail.ToString().Trim(); }

        FfmpegResult Fail(FfmpegOutcome outcome, string detail)
        {
            if (output != null) DeleteQuietly(output);
            return new FfmpegResult(outcome, detail);
        }
    }

    private static void Kill(Process? p)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);   // the output file stays locked until it is really gone
            }
        }
        catch { /* already exited */ }
    }

    private static void DeleteQuietly(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
