using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Media;

/// <summary>What one yt-dlp (or ffmpeg) run left: exit code, stdout, the tail of stderr; TimedOut / Cancelled when it was stopped.</summary>
public sealed record ProcRun(int ExitCode, string StdOut, string StdErrTail, bool TimedOut = false, bool Cancelled = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>A video's facts from <c>yt-dlp -J</c>: title, length, its own language, and which subtitle tracks exist.</summary>
public sealed record VideoInfo(string Id, string Title, string Uploader, TimeSpan? Duration, string? Language,
    IReadOnlyCollection<string> Subtitles, IReadOnlyCollection<string> AutoCaptions, string WebPageUrl);

/// <summary>The subtitle track chosen for a transcript. <see cref="Translated"/>: YouTube's machine translation of
/// the automatic captions into another language — readable, but not what was said.</summary>
public sealed record SubtitleTrack(string Language, bool Automatic, bool Translated);

/// <summary>
/// yt-dlp, the optional program media_transcript uses to read a video's subtitles and, for speech-to-text, its audio.
/// CluadeX does not ship it (it must be updated often — sites change): it is looked up where winget, scoop, pip and
/// the user's PATH put it, like <see cref="Ffmpeg"/>. Every run is an argument list (never a shell string), starts
/// with --ignore-config (a user's yt-dlp.conf must not add options to the agent's run), --no-playlist, and puts the
/// URL after "--" so a URL can never be read as an option.
/// </summary>
public static class YtDlp
{
    /// <summary>For the harness: a path to use instead of looking ("" = act as if there were none).</summary>
    public static string? PathOverride { get; set; }

    /// <summary>For the harness: runs instead of the real process (exe, args, timeout, ct).</summary>
    public static Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcRun>>? RunOverride { get; set; }

    public const string InstallCommand = "winget install yt-dlp.yt-dlp";

    /// <summary>Largest audio file downloaded for speech-to-text.</summary>
    public const long MaxAudioBytes = 512L * 1024 * 1024;

    private static string? _found;
    private static DateTime _lookedAt;

    public static string? Locate()
    {
        if (PathOverride != null) return PathOverride.Length == 0 ? null : PathOverride;
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
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
        dirs.Add(Path.Combine(home, "scoop", "shims"));
        // pip install --user yt-dlp: %APPDATA%\Python\Python3xx\Scripts; python.org installs: %LOCALAPPDATA%\Programs\Python\Python3xx\Scripts.
        foreach (var root in new[] { Path.Combine(roaming, "Python"), Path.Combine(local, "Programs", "Python") })
        {
            try
            {
                if (Directory.Exists(root))
                    dirs.AddRange(Directory.EnumerateDirectories(root, "Python*").Select(d => Path.Combine(d, "Scripts")));
            }
            catch { }
        }
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string exe = Path.Combine(d, "yt-dlp.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { /* a malformed PATH entry */ }
        }
        try
        {
            string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(packages)) return null;
            return Directory.EnumerateDirectories(packages, "yt-dlp*")
                .SelectMany(p => Directory.EnumerateFiles(p, "yt-dlp.exe", SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>The options every run starts with.</summary>
    public static List<string> BaseArgs() => new()
    {
        "--ignore-config", "--no-playlist", "--no-warnings", "--no-progress", "--no-color",
    };

    // ─── Probe ───

    public static IReadOnlyList<string> ProbeArgs(string url)
    {
        var a = BaseArgs();
        a.AddRange(new[] { "-J", "--", url });
        return a;
    }

    /// <summary>The facts of yt-dlp's -J JSON, or null when it is not a single video's JSON.</summary>
    public static VideoInfo? ParseInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            TimeSpan? duration = r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.GetDouble() > 0
                ? TimeSpan.FromSeconds(d.GetDouble()) : null;
            static List<string> Keys(JsonElement root, string name)
                => root.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
                    ? o.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                        .Select(p => p.Name).Where(n => n != "live_chat").ToList()
                    : new List<string>();
            string uploader = S("uploader");
            if (uploader.Length == 0) uploader = S("channel");
            string lang = S("language");
            return new VideoInfo(S("id"), S("title"), uploader, duration, lang.Length > 0 ? lang : null,
                Keys(r, "subtitles"), Keys(r, "automatic_captions"), S("webpage_url"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Which track to read, best first: subtitles a person made in a wanted language; the automatic captions in the
    /// language the video is spoken in ("xx-orig", or "xx" when it is the video's own language) — what was really said;
    /// any human subtitles; then the automatic captions machine-translated into a wanted language. Wanted = the
    /// language the model asked for, else Thai, then English. Null = the video has no usable track.
    /// </summary>
    public static SubtitleTrack? ChooseTrack(VideoInfo info, string? wanted)
    {
        var want = new List<string>();
        if (!string.IsNullOrWhiteSpace(wanted)) want.Add(wanted.Trim().ToLowerInvariant());
        foreach (var l in new[] { "th", "en" }) if (!want.Contains(l)) want.Add(l);

        static string Base(string tag) => tag.Split('-', '_')[0].ToLowerInvariant();
        string? Match(IEnumerable<string> have, string lang)
            => have.FirstOrDefault(h => h.Equals(lang, StringComparison.OrdinalIgnoreCase))
               ?? have.FirstOrDefault(h => Base(h) == Base(lang) && !h.EndsWith("-orig", StringComparison.OrdinalIgnoreCase));

        foreach (var l in want)
            if (Match(info.Subtitles, l) is { } s) return new SubtitleTrack(s, false, false);

        string? spoken = info.Language != null ? Base(info.Language) : null;
        var orig = info.AutoCaptions.FirstOrDefault(a => a.EndsWith("-orig", StringComparison.OrdinalIgnoreCase));
        if (orig != null) return new SubtitleTrack(orig, true, false);
        if (spoken != null && Match(info.AutoCaptions, spoken) is { } own) return new SubtitleTrack(own, true, false);

        if (info.Subtitles.FirstOrDefault() is { } anyHuman) return new SubtitleTrack(anyHuman, false, false);

        foreach (var l in want)
            if (Match(info.AutoCaptions, l) is { } tr) return new SubtitleTrack(tr, true, spoken == null || Base(tr) != spoken);
        return info.AutoCaptions.FirstOrDefault() is { } first ? new SubtitleTrack(first, true, spoken != null && Base(first) != spoken) : null;
    }

    // ─── Subtitles and audio ───

    public static IReadOnlyList<string> SubtitleArgs(string url, SubtitleTrack track, string outDir)
    {
        var a = BaseArgs();
        a.AddRange(new[]
        {
            "--skip-download", track.Automatic ? "--write-auto-subs" : "--write-subs",
            "--sub-langs", track.Language, "--sub-format", "vtt/srt/best",
            "-o", Path.Combine(outDir, "sub.%(ext)s"), "--", url,
        });
        return a;
    }

    public static IReadOnlyList<string> AudioArgs(string url, string outDir)
    {
        var a = BaseArgs();
        a.AddRange(new[]
        {
            "-f", "bestaudio/best", "--max-filesize", (MaxAudioBytes / (1024 * 1024)) + "M", "--no-part",
            "-o", Path.Combine(outDir, "audio.%(ext)s"), "--", url,
        });
        return a;
    }

    /// <summary>The first file in <paramref name="dir"/> named <paramref name="stem"/>.*, or null.</summary>
    public static string? Output(string dir, string stem)
    {
        try { return Directory.EnumerateFiles(dir, stem + ".*").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault(); }
        catch { return null; }
    }

    // ─── Running ───

    /// <summary>Run <paramref name="exe"/> with <paramref name="args"/>: stdout kept whole (up to 32 MB — -J of a long video
    /// is large), stderr's last 4,000 characters. Stopped (process tree killed) on timeout or cancel. Never throws.</summary>
    public static async Task<ProcRun> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (RunOverride != null) return await RunOverride(exe, args, timeout, ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";   // a Thai title in an error line must not fail the run
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        const int MaxStdout = 32 * 1024 * 1024;
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        Process? p = null;
        try
        {
            p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) { if (stdout.Length < MaxStdout) stdout.AppendLine(e.Data); } };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderr) { stderr.AppendLine(e.Data); if (stderr.Length > 4000) stderr.Remove(0, stderr.Length - 4000); }
            };
            if (!p.Start()) return new ProcRun(-1, "", "did not start");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            p.WaitForExit();
            lock (stdout) lock (stderr) return new ProcRun(p.ExitCode, stdout.ToString(), stderr.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            lock (stderr) return new ProcRun(-1, "", stderr.ToString().Trim(), TimedOut: !ct.IsCancellationRequested, Cancelled: ct.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            Kill(p);
            return new ProcRun(-1, "", ex.GetType().Name + ": " + ex.Message);
        }
        finally { p?.Dispose(); }
    }

    private static void Kill(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } }
        catch { /* already gone */ }
    }

    /// <summary>yt-dlp's own "ERROR: …" line, the part a person can read, or the last stderr line.</summary>
    public static string ErrorLine(ProcRun run)
    {
        var lines = run.StdErrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var err = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "";
        return err.Length > 300 ? err[..300] + "…" : err;
    }
}
