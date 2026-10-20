using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Auto-update: an installed build (Velopack) finds a new release while it runs, downloads it in the background —
/// resuming after any drop — and installs it when the app closes or when the user presses Install. A portable or dev
/// build is told a new version exists. Checks: xman4289.com's version API, then GitHub.
///
/// Owner 2026-10-20: "ระบบอัพเดทอัตโนมัติยังไม่โอเค มีเวอร์ชั่นใหม่ก็เจอช้า โหลดช้าก็ตัด". What the log showed that day:
/// the app checked only at launch; the splash and the main window started two Velopack downloads at the same moment
/// and the second died on Velopack's lock ("Failed to acquire exclusive lock file") and fell through to the portable
/// zip offer, whose download had a 30-second cap for 470 MB; and every release was a full ~500 MB package (no deltas).
/// Now: one download at a time, shared by everyone who asks (<see cref="VelopackCheckAndStageAsync"/>); a cheap probe of
/// the latest release every <see cref="ProbeEvery"/>; <see cref="ResumableDownloader"/> for every byte; installed on exit
/// (<see cref="OnAppShutdown"/>); and release.yml ships delta packages.
/// </summary>
public class AutoUpdateService : IAppShutdownHook
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    // The store's slug for CluadeX. It was "cluadex", which the store does not have: every check got 404 and fell
    // through to GitHub (checked 2026-10-13 — /api/v1/products/cluadex-ai-coding-assistant/version answers).
    private const string BaseUrl = "https://xman4289.com/api/v1/products/cluadex-ai-coding-assistant";
    private const string GitHubReleasesApi = "https://api.github.com/repos/xjanova/cluadeX/releases/latest";
    /// <summary>Redirects to <c>…/releases/tag/vX.Y.Z</c> of the newest release. A HEAD on it costs no GitHub API quota
    /// (60 an hour per IP, shared with everything else on the machine), so it can be asked often.</summary>
    public const string LatestReleasePage = "https://github.com/xjanova/cluadeX/releases/latest";

    /// <summary>How often a running app looks for a new release.</summary>
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(10);
    /// <summary>A full check runs at least this often even when the probe says nothing (or cannot reach GitHub's page).</summary>
    public static readonly TimeSpan FullCheckEvery = TimeSpan.FromHours(1);

    public event Action<UpdateInfo>? OnUpdateAvailable;
    public event Action<double, string>? OnDownloadProgress; // percent, status
    public event Action<string>? OnUpdateStatus;
    /// <summary>Where the update stands (checking, downloading with sizes and speed, ready, failed). Raised from a
    /// background thread, at most a few times a second while downloading.</summary>
    public event Action<UpdateProgressInfo>? ProgressChanged;

    private readonly DebugLogService? _log;
    private readonly ResumableDownloader _downloader;
    private readonly CancellationTokenSource _shutdown = new();

    public AutoUpdateService(SettingsService settingsService, DebugLogService? log = null)
    {
        _settingsService = settingsService;
        _log = log;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"CluadeX/{CurrentVersion}");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);   // checks only; downloads go through _downloader, which has no total cap
        _downloader = new ResumableDownloader(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "update-cache"));
        _downloader.Progress += OnFileProgress;
    }

    public static string CurrentVersion
    {
        get
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            return asm.GetName().Version?.ToString(3) ?? "2.0.0";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Velopack — the real self-update path
    //
    //  The zip-download-and-swap path below it is what this app had before, and
    //  it can only ever ask the user to restart into a batch script that
    //  overwrites files under a running process. Velopack does the same job the
    //  way BrainX does it: staged package, atomic directory swap, rollback, and
    //  delta downloads instead of the whole 300 MB payload every time.
    //
    //  Velopack only works on a build INSTALLED via its Setup.exe. A dev or
    //  portable run reports IsInstalled=false, and the zip path stays as the
    //  fallback so those builds still learn a new version exists.
    // ════════════════════════════════════════════════════════════════════════

    private Velopack.UpdateManager? _vpk;
    private Velopack.UpdateInfo? _vpkPending;
    // ~/.cluadex, not the Velopack install root — a failure counter stored inside the
    // directory the installer empties would be wiped by the very event it exists to
    // survive. See SettingsService.MigrateLegacyDataRoot.
    private readonly UpdateAttemptLog _attempts =
        UpdateAttemptLog.Load(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex"));

    /// <summary>Set to a folder (or http URL) holding a Velopack release feed — the Releases/ output of
    /// <c>vpk pack</c> — to test a self-update end to end before anything is published to GitHub.</summary>
    public const string FeedOverrideVariable = "CLUADEX_UPDATE_FEED";

    private Velopack.UpdateManager Vpk => _vpk ??= new Velopack.UpdateManager(CreateSource());

    private Velopack.Sources.IUpdateSource CreateSource()
    {
        string? feed = Environment.GetEnvironmentVariable(FeedOverrideVariable)?.Trim();
        if (!string.IsNullOrEmpty(feed))
        {
            _log?.Warn("Update", $"update feed overridden by {FeedOverrideVariable}: {feed}");
            return feed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Velopack.Sources.SimpleWebSource(feed, _downloader)
                : new Velopack.Sources.SimpleFileSource(new DirectoryInfo(feed));
        }
        return new Velopack.Sources.GithubSource("https://github.com/xjanova/cluadeX", null, false, _downloader);
    }

    // ─── progress ──────────────────────────────────────────────────────────────────────

    private readonly object _progressLock = new();
    private UpdateProgressInfo _progress = UpdateProgressInfo.Idle;
    private readonly Dictionary<string, (long Bytes, long Total)> _files = new(StringComparer.OrdinalIgnoreCase);
    private double _speed;                         // bytes/s, smoothed
    private long _speedBytes; private DateTime _speedAt = DateTime.MinValue;
    private DateTime _lastPublish = DateTime.MinValue;

    /// <summary>The latest state, for a window that opens in the middle of a download.</summary>
    public UpdateProgressInfo Progress { get { lock (_progressLock) return _progress; } }

    private void SetPhase(UpdatePhase phase, string? version, string? error = null)
    {
        lock (_progressLock)
        {
            if (phase == UpdatePhase.Downloading && _progress.Phase != UpdatePhase.Downloading)
            {
                _files.Clear(); _speed = 0; _speedBytes = 0; _speedAt = DateTime.MinValue;   // the first report only sets the baseline (a resumed file starts far from 0)
                _progress = _progress with { Bytes = 0, Total = 0, Percent = 0 };
            }
            _progress = _progress with { Phase = phase, Version = version, Error = error, Percent = phase == UpdatePhase.Staged ? 100 : phase == UpdatePhase.Downloading ? _progress.Percent : 0 };
            if (phase != UpdatePhase.Downloading) _progress = _progress with { Resuming = false, BytesPerSecond = 0 };
        }
        Publish(force: true);
    }

    /// <summary>Velopack's own percent (download, then delta patching) drives the bar.</summary>
    private void OnVelopackPercent(int percent)
    {
        lock (_progressLock) _progress = _progress with { Percent = Math.Clamp(percent, 0, 100) };
        Publish(force: false);
    }

    /// <summary>The downloader's bytes drive the text: sizes, speed, "resuming".</summary>
    private void OnFileProgress(ResumableDownloader.FileProgress f)
    {
        lock (_progressLock)
        {
            _files[f.FileName] = (f.Bytes, f.Total);
            long bytes = 0, total = 0;
            foreach (var (b, t) in _files.Values) { bytes += b; total += t; }
            var now = DateTime.UtcNow;
            double secs = (now - _speedAt).TotalSeconds;
            if (_speedAt == DateTime.MinValue || bytes < _speedBytes) { _speedAt = now; _speedBytes = bytes; }
            else if (secs >= 1)
            {
                double inst = (bytes - _speedBytes) / secs;
                _speed = _speed <= 0 ? inst : _speed * 0.6 + inst * 0.4;
                _speedAt = now; _speedBytes = bytes;
            }
            _progress = _progress with
            {
                Bytes = bytes, Total = total, BytesPerSecond = _speed,
                Resuming = f.FailuresInARow > 0,   // between a drop and the next byte
                Error = f.LastError,
            };
        }
        Publish(force: f.LastError != null);
    }

    private void Publish(bool force)
    {
        UpdateProgressInfo snap;
        lock (_progressLock)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastPublish < TimeSpan.FromMilliseconds(250)) return;
            _lastPublish = now;
            snap = _progress;
        }
        try { ProgressChanged?.Invoke(snap); } catch (Exception ex) { _log?.Warn("Update", $"progress listener failed: {ex.Message}"); }
        if (snap.Phase == UpdatePhase.Downloading)
            try { OnDownloadProgress?.Invoke(snap.Percent, snap.Describe(thai: false)); } catch { }
    }

    // ─── one update job at a time ──────────────────────────────────────────────────────

    private readonly object _jobLock = new();
    private Task<string?>? _job;
    private volatile bool _applying;

    /// <summary>True while a check or download runs.</summary>
    public bool IsBusy { get { lock (_jobLock) return _job is { IsCompleted: false }; } }

    /// <summary>
    /// Check GitHub for a Velopack release and download it in the background. Returns the staged version, or null
    /// when there is nothing to stage. Everyone who asks while a check or download runs gets that same job — the
    /// splash, the main window and the periodic probe never start a second one (Velopack's lock would fail it).
    /// The job outlives its callers; it stops only when the app exits.
    /// </summary>
    public Task<string?> VelopackCheckAndStageAsync(CancellationToken ct = default)
    {
        lock (_jobLock)
        {
            if (_job is { IsCompleted: false }) return _job;
            _job = Task.Run(CheckAndStageCoreAsync);
            return _job;
        }
    }

    private async Task<string?> CheckAndStageCoreAsync()
    {
        if (!CanSelfUpdate) { _log?.Info("Update", "Velopack: not an installed build — self-update unavailable (portable/dev)"); return null; }
        string? target = null;
        try
        {
            _lastFullCheckUtc = DateTime.UtcNow;
            SetPhase(UpdatePhase.Checking, null);
            var info = await Vpk.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                _log?.Info("Update", "Velopack: no newer release");
                SetPhase(_vpkPending != null ? UpdatePhase.Staged : UpdatePhase.Idle, StagedVersion);
                return null;
            }

            target = info.TargetFullRelease.Version.ToString();
            // The circuit breaker. Without it a package that cannot be applied gets
            // retried on every launch forever — which is exactly how BrainX ended up
            // relaunching itself every 18 seconds.
            if (_attempts.ShouldStopTrying(target))
            {
                _log?.Warn("Update", $"Velopack: v{target} already failed {UpdateAttemptLog.MaxConsecutiveFailures}× — automatic apply paused, waiting for a manual restart");
                SetPhase(UpdatePhase.Idle, null);
                return null;
            }

            int deltas = info.DeltasToTarget?.Length ?? 0;
            _log?.Info("Update", deltas > 0
                ? $"Velopack: downloading v{target} as {deltas} delta package(s)…"
                : $"Velopack: downloading v{target} (full package {info.TargetFullRelease.Size / (1024.0 * 1024):0} MB)…");
            SetPhase(UpdatePhase.Downloading, target);
            var sw = Stopwatch.StartNew();
            await Vpk.DownloadUpdatesAsync(info, OnVelopackPercent, _shutdown.Token).ConfigureAwait(false);
            _vpkPending = info;
            _log?.Info("Update", $"Velopack: v{target} staged in {sw.Elapsed.TotalSeconds:0} s — applies on exit or on Install");
            SetPhase(UpdatePhase.Staged, target);
            OnUpdateStatus?.Invoke($"Update v{target} downloaded — restart to apply.");
            return target;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _log?.Info("Update", "Velopack: download stopped by exit — it goes on from the same byte next time");
            return null;
        }
        catch (Exception ex)
        {
            string why = ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                ? "another update is running (a second CluadeX, or the installer)"
                : ex.Message;
            _log?.Warn("Update", $"Velopack check/stage failed: {ex.Message}");
            SetPhase(UpdatePhase.Failed, target, why);
            return null;
        }
    }

    // ─── finding a new release while the app runs ─────────────────────────────────────

    private CancellationTokenSource? _loop;
    private DateTime _lastFullCheckUtc = DateTime.MinValue;
    private string? _lastProbed;
    private volatile string? _lastOffered;   // the release a portable copy was last told about

    /// <summary>Start looking for new releases every <see cref="ProbeEvery"/> (idempotent). A dev build never polls.</summary>
    public void StartBackgroundChecks()
    {
        if (CurrentBuildKind == BuildKind.Dev && Environment.GetEnvironmentVariable(FeedOverrideVariable) == null) return;
        lock (_jobLock)
        {
            if (_loop != null || _shutdown.IsCancellationRequested) return;
            _loop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        }
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ProbeEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    try { await TickAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Warn("Update", $"background check failed: {ex.Message}"); }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (IsBusy || _applying) return;
        string? latest = await ProbeLatestReleaseAsync(ct).ConfigureAwait(false);
        // What this copy already has in hand: the downloaded update (installed copy) or the release it was told about (portable).
        string? inHand = CanSelfUpdate ? StagedVersion : _lastOffered;
        if (!ShouldCheckNow(latest, CurrentVersion, inHand, DateTime.UtcNow - _lastFullCheckUtc)) return;
        _lastFullCheckUtc = DateTime.UtcNow;
        _log?.Info("Update", latest != null ? $"background: v{latest} is out — checking" : "background: hourly check");
        if (CanSelfUpdate) await VelopackCheckAndStageAsync().ConfigureAwait(false);
        else await CheckForUpdateAsync(ct).ConfigureAwait(false);   // portable: OnUpdateAvailable tells the window
    }

    /// <summary>A full check is worth running when the newest release (from the probe) is newer than what this copy runs
    /// and than what is already downloaded — or, without a probe answer, once <see cref="FullCheckEvery"/> has passed.
    /// Public for the harness.</summary>
    public static bool ShouldCheckNow(string? latest, string running, string? staged, TimeSpan sinceFullCheck)
    {
        if (latest != null) return IsNewer(latest, staged != null && IsNewer(staged, running) ? staged : running);
        return sinceFullCheck >= FullCheckEvery;
    }

    /// <summary>The newest release's version, read from where <see cref="LatestReleasePage"/> redirects; null when GitHub
    /// cannot be reached (or the page did not redirect to a tag).</summary>
    public async Task<string?> ProbeLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, LatestReleasePage);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string? v = ParseReleaseTagUrl(resp.RequestMessage?.RequestUri);
            if (v != _lastProbed) { _lastProbed = v; _log?.Info("Update", $"probe: newest release {(v != null ? "v" + v : $"unknown (HTTP {(int)resp.StatusCode})")}"); }
            return v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>"https://github.com/o/r/releases/tag/v3.0.178" → "3.0.178"; anything else → null. Public for the harness.</summary>
    public static string? ParseReleaseTagUrl(Uri? url)
    {
        if (url == null) return null;
        string path = url.AbsolutePath.TrimEnd('/');
        int i = path.LastIndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string tag = Uri.UnescapeDataString(path[(i + "/releases/tag/".Length)..]).TrimStart('v', 'V');
        return VersionCore(tag) is { } core && Version.TryParse(core, out _) ? tag : null;
    }

    /// <summary>"3.0.178+abc" / "v3.0.178-beta" → "3.0.178".</summary>
    private static string? VersionCore(string v)
    {
        var s = v.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['+', '-', ' ']);
        s = cut > 0 ? s[..cut] : s;
        return s.Length > 0 ? s : null;
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/> (build
    /// metadata and pre-release tails ignored). Public for the harness.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        candidate != null && current != null
        && Version.TryParse(VersionCore(candidate), out var a) && Version.TryParse(VersionCore(current), out var b)
        && a > b;

    // ─── installing on exit ────────────────────────────────────────────────────────────

    /// <summary>
    /// App exit: stop any download (its partial file stays, the next launch goes on from the same byte), and if an
    /// update is downloaded and waiting, hand it to Velopack's updater, which installs it once this process is gone —
    /// the next launch is the new version without a restart in between. Never asks anything; bounded (starts a
    /// process).
    /// </summary>
    public void OnAppShutdown()
    {
        try { _shutdown.Cancel(); } catch { }
        if (_applying || _vpkPending == null) return;
        string target = _vpkPending.TargetFullRelease.Version.ToString();
        if (_attempts.ShouldStopTrying(target)) return;
        try
        {
            _attempts.RecordAttempt(target);   // nothing of ours runs afterwards to record it
            _applying = true;
            Vpk.WaitExitThenApplyUpdates(_vpkPending, silent: true, restart: false);
            _log?.Info("Update", $"Velopack: v{target} installs after exit");
        }
        catch (Exception ex) { _log?.Warn("Update", $"Velopack: install on exit failed to start: {ex.Message}"); }
    }

    /// <summary>How this copy was started — shown on the title bar so the installed app and a dev build
    /// sitting side by side are never mistaken for each other.</summary>
    public enum BuildKind { Installed, Portable, Dev }

    /// <summary>Installed = Velopack layout (…\current\CluadeX.exe with sq.version, Update.exe one level
    /// up); Portable = a portable marker next to the exe; anything else is a developer build.</summary>
    public static BuildKind CurrentBuildKind
    {
        get
        {
            try
            {
                string exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                string? parent = Path.GetDirectoryName(exeDir);
                if (File.Exists(Path.Combine(exeDir, "sq.version")) && parent != null && File.Exists(Path.Combine(parent, "Update.exe")))
                    return BuildKind.Installed;
                if (File.Exists(Path.Combine(exeDir, "portable")) || File.Exists(Path.Combine(exeDir, "portable.txt")))
                    return BuildKind.Portable;
            }
            catch { /* fall through */ }
            return BuildKind.Dev;
        }
    }

    /// <summary>True when this build can actually self-update (installed via Setup.exe).</summary>
    public bool CanSelfUpdate
    {
        get { try { return Vpk.IsInstalled; } catch { return false; } }
    }

    /// <summary>A staged update is downloaded and waiting for a restart.</summary>
    public bool HasStagedUpdate => _vpkPending != null;

    /// <summary>Version of the staged update, or null.</summary>
    public string? StagedVersion => _vpkPending?.TargetFullRelease?.Version?.ToString();

    /// <summary>Why automatic updating is paused, or null when it isn't.</summary>
    public string? PausedReason => _attempts.Describe();

    /// <summary>
    /// Call once at startup, before any check. Whatever version we are running now
    /// is the verdict on the last attempt: if it is the one we were trying to reach
    /// the update landed and the failure history is wiped; if not, the count stands.
    /// </summary>
    public void NoteRunningVersion() => _attempts.NoteRunningVersion(CurrentVersion);

    /// <summary>
    /// Apply the staged update and restart. Records the attempt FIRST — this call
    /// does not return, so anything written afterwards would never be written.
    /// </summary>
    public bool VelopackApplyAndRestart()
    {
        if (_vpkPending == null) return false;
        string target = _vpkPending.TargetFullRelease.Version.ToString();
        try
        {
            _attempts.RecordAttempt(target);          // must be flushed BEFORE we hand over
            _applying = true;
            _log?.Info("Update", $"Velopack: applying v{target} and restarting");
            Vpk.ApplyUpdatesAndRestart(_vpkPending);
            return true;                               // unreachable in practice
        }
        catch (Exception ex)
        {
            _applying = false;
            _log?.Error("Update", $"Velopack apply failed: {ex.Message}");
            OnUpdateStatus?.Invoke($"Could not apply the update: {ex.Message}");
            return false;
        }
    }

    /// <summary>Check xman API first, fallback to GitHub releases.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Both checks below swallow every exception on purpose — an update probe must
        // never break a launch. But swallowing the REASON is how this feature spent
        // months looking like "there is no auto-update": xman returns 404 for this
        // product and the newest GitHub release is 80+ versions behind the running
        // build, so a healthy check and a completely dead pipeline are indistinguishable
        // from the outside. One log line per attempt makes the difference visible.
        _log?.Info("Update", $"check start · current=v{CurrentVersion}");

        var info = await CheckXmanAsync(ct);
        if (info != null)
        {
            _log?.Info("Update", $"xman API: update available v{info.NewVersion}");
            return info;
        }

        info = await CheckGitHubAsync(ct);
        if (info != null)
            _log?.Info("Update", $"GitHub releases: update available v{info.NewVersion}");
        else
            _log?.Info("Update", $"no update offered — running v{CurrentVersion} is at or ahead of every published release");
        return info;
    }

    private async Task<UpdateInfo?> CheckXmanAsync(CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["current_version"] = CurrentVersion,
                ["machine_id"] = XmanLicenseService.GetMachineFingerprint(),
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{BaseUrl}/check-update", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (ParseXmanCheck(body, CurrentVersion) is { } info)
            {
                _lastOffered = info.NewVersion;
                OnUpdateAvailable?.Invoke(info);
                return info;
            }
            _log?.Info("Update", $"xman API: no update (HTTP {(int)response.StatusCode})");
        }
        catch (Exception ex) { _log?.Warn("Update", $"xman API check failed: {ex.Message}"); }
        return null;
    }

    /// <summary>The store's check-update reply → the update it offers, or null. The store answers
    /// <c>{ has_update, latest_version, update: { version, filename, file_size, changelog, download_url, sha256 } }</c>
    /// (Api/VersionController::check); the flat <c>update_available</c> shape this used to read never came back.
    /// Public for the harness.</summary>
    public static UpdateInfo? ParseXmanCheck(string body, string currentVersion)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        if (root.TryGetProperty("has_update", out var hu) && hu.ValueKind == JsonValueKind.True
            && root.TryGetProperty("update", out var u) && u.ValueKind == JsonValueKind.Object)
            return new UpdateInfo
            {
                NewVersion = Str(u, "version"), CurrentVersion = currentVersion, DownloadUrl = Str(u, "download_url"),
                Changelog = Str(u, "changelog"), FileSize = Num(u, "file_size"), FileName = Str(u, "filename"), Sha256 = Str(u, "sha256"),
            };
        if (root.TryGetProperty("update_available", out var ua) && ua.ValueKind == JsonValueKind.True)   // the old flat shape
            return new UpdateInfo
            {
                NewVersion = Str(root, "version"), CurrentVersion = currentVersion, DownloadUrl = Str(root, "download_url"),
                Changelog = Str(root, "changelog"), FileSize = Num(root, "file_size"), FileName = Str(root, "download_filename"), Sha256 = Str(root, "sha256"),
            };
        return null;
    }

    private async Task<UpdateInfo?> CheckGitHubAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(GitHubReleasesApi, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string version = tagName.TrimStart('v', 'V');

            if (IsNewer(version, CurrentVersion))
            {
                // Find zip asset
                string downloadUrl = "";
                string fileName = "";
                long fileSize = 0;
                string sha256 = "";

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            fileName = name;
                            fileSize = asset.GetProperty("size").GetInt64();
                            // GitHub asset digest is "sha256:HEX" (newer API) — use it for integrity.
                            string digest = asset.TryGetProperty("digest", out var dg) ? dg.GetString() ?? "" : "";
                            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                sha256 = digest["sha256:".Length..];
                            break;
                        }
                    }
                }

                // Fallback to zipball
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = root.TryGetProperty("zipball_url", out var zb) ? zb.GetString() ?? "" : "";
                    fileName = $"CluadeX-{version}.zip";
                }

                string changelog = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                var info = new UpdateInfo
                {
                    NewVersion = version,
                    CurrentVersion = CurrentVersion,
                    DownloadUrl = downloadUrl,
                    Changelog = changelog,
                    FileSize = fileSize,
                    FileName = fileName,
                    Sha256 = sha256,
                };
                _lastOffered = info.NewVersion;
                OnUpdateAvailable?.Invoke(info);
                return info;
            }
            _log?.Info("Update", $"GitHub: newest published release is v{version} — not newer than v{CurrentVersion}");
        }
        catch (Exception ex) { _log?.Warn("Update", $"GitHub release check failed: {ex.Message}"); }
        return null;
    }

    /// <summary>Download the portable zip (portable / dev builds — an installed build never takes this path: Velopack
    /// updates it). Resumes after a drop and has no total time cap (<see cref="ResumableDownloader"/>); the old loop
    /// here had the check client's 30-second cap, so 470 MB on a normal line was always cut off.</summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl)) return null;
        if (CanSelfUpdate)
        {
            // The zip path copies files over the running app's folder — under a Velopack install that breaks the layout
            // its updater relies on. The installed app updates through Velopack only.
            OnUpdateStatus?.Invoke("This copy is installed — it updates itself through the installer's updater.");
            return null;
        }

        try
        {
            OnUpdateStatus?.Invoke("Downloading update...");

            string tempDir = Path.Combine(Path.GetTempPath(), "CluadeX_Update");
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, info.FileName.Length > 0 ? Path.GetFileName(info.FileName) : "update.zip");

            SetPhase(UpdatePhase.Downloading, info.NewVersion);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            await _downloader.DownloadFile(info.DownloadUrl, zipPath, OnVelopackPercent, cancelToken: stop.Token);
            OnDownloadProgress?.Invoke(100, "Download complete!");

            // Integrity gate: verify the downloaded file's SHA-256 against the manifest hash when the
            // server provided one — closes the "tampered/MITM download → arbitrary exe overwrite → RCE"
            // vector. (A fully-compromised manifest server still needs code-signing; tracked separately.)
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                string actual = await ComputeSha256Async(zipPath, ct);
                if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(zipPath); } catch { }
                    SetPhase(UpdatePhase.Failed, info.NewVersion, "SHA-256 mismatch");
                    OnUpdateStatus?.Invoke("Update REJECTED: integrity check failed (SHA-256 mismatch). The download may be corrupt or tampered.");
                    return null;
                }
                OnUpdateStatus?.Invoke("Integrity verified ✓ — ready to install.");
            }
            else
            {
                OnUpdateStatus?.Invoke("Download complete (no checksum provided — integrity NOT verified). Ready to install.");
            }
            SetPhase(UpdatePhase.Idle, info.NewVersion);
            return zipPath;
        }
        catch (Exception ex)
        {
            _log?.Warn("Update", $"portable download failed: {ex.Message}");
            SetPhase(UpdatePhase.Failed, info.NewVersion, ex is OperationCanceledException ? "stopped" : ex.Message);
            OnUpdateStatus?.Invoke($"Download failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>Extract update and create a batch script to replace files and restart.</summary>
    public bool InstallAndRestart(string zipPath)
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string tempExtract = Path.Combine(Path.GetTempPath(), "CluadeX_Update", "extracted");
            string batchPath = Path.Combine(Path.GetTempPath(), "CluadeX_Update", "update.bat");

            // Extract zip
            OnUpdateStatus?.Invoke("Extracting update...");
            if (Directory.Exists(tempExtract))
                Safety.SafeDelete.Directory(tempExtract, Path.Combine(Path.GetTempPath(), "CluadeX_Update"));
            ZipFile.ExtractToDirectory(zipPath, tempExtract, true);

            // Find the actual files (might be in a subfolder)
            string sourceDir = tempExtract;
            var subDirs = Directory.GetDirectories(tempExtract);
            if (subDirs.Length == 1 && !File.Exists(Path.Combine(tempExtract, "CluadeX.exe")))
            {
                sourceDir = subDirs[0]; // zip contains a single folder
            }

            // Create batch script that waits for app to close, copies files, restarts
            string batchContent = $"""
                @echo off
                echo ========================================
                echo   CluadeX Auto-Update Installer
                echo ========================================
                echo.
                echo Waiting for CluadeX to close...
                timeout /t 3 /nobreak >nul

                :waitloop
                tasklist /FI "IMAGENAME eq CluadeX.exe" 2>nul | find /i "CluadeX.exe" >nul
                if not errorlevel 1 (
                    echo Still running, waiting...
                    timeout /t 2 /nobreak >nul
                    goto waitloop
                )

                echo.
                echo Copying new files...
                xcopy /E /Y /I "{sourceDir}\*" "{appDir}" >nul 2>&1

                echo.
                echo Update complete! Starting CluadeX...
                start "" "{Path.Combine(appDir, "CluadeX.exe")}"

                echo Cleaning up...
                rmdir /S /Q "{Path.Combine(Path.GetTempPath(), "CluadeX_Update")}" >nul 2>&1

                exit
                """;

            File.WriteAllText(batchPath, batchContent, Encoding.GetEncoding(874)); // Thai Windows codepage

            // Launch the updater batch and close the app
            OnUpdateStatus?.Invoke("Installing update... App will restart.");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            Process.Start(psi);

            // Close the app — the batch script will wait and replace files
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });

            return true;
        }
        catch (Exception ex)
        {
            OnUpdateStatus?.Invoke($"Install failed: {ex.Message}");
            return false;
        }
    }
}

public class UpdateInfo
{
    public string NewVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Changelog { get; set; } = "";
    public long FileSize { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>Expected SHA-256 (hex) of the download from the manifest. Verified before install.</summary>
    public string Sha256 { get; set; } = "";

    public bool IsNewer => !string.IsNullOrEmpty(NewVersion) && NewVersion != CurrentVersion;
    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F1} MB",
    };
}

public enum UpdatePhase { Idle, Checking, Downloading, Staged, Failed }

/// <summary>Where the update stands, for the splash and the update bar. Bytes/Total cover every file of the current
/// download (a delta chain is several); Percent is Velopack's own (download, then patching).</summary>
public sealed record UpdateProgressInfo(UpdatePhase Phase, string? Version, double Percent, long Bytes, long Total,
    double BytesPerSecond, bool Resuming, string? Error)
{
    public static readonly UpdateProgressInfo Idle = new(UpdatePhase.Idle, null, 0, 0, 0, 0, false, null);

    /// <summary>Seconds left at the current speed, or null when it cannot be told.</summary>
    public double? EtaSeconds => Phase == UpdatePhase.Downloading && Total > 0 && BytesPerSecond > 1 && Bytes <= Total
        ? (Total - Bytes) / BytesPerSecond : null;

    /// <summary>"12.3 / 27.0 MB · 2.1 MB/s" (+ "· resuming"), or the percent alone before the first byte.</summary>
    public string Describe(bool thai)
    {
        static string Mb(long b) => (b / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (Total <= 0) return $"{Percent:0}%";
        string s = $"{Mb(Bytes)} / {Mb(Total)} MB";
        if (BytesPerSecond > 1)
            s += BytesPerSecond >= 1024 * 1024
                ? $" · {(BytesPerSecond / (1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB/s"
                : $" · {(BytesPerSecond / 1024).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} KB/s";
        if (Resuming) s += thai ? " · สัญญาณหลุด กำลังต่อจากเดิม" : " · connection dropped, resuming";
        return s;
    }
}
