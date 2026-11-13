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
/// build is told a new version exists. Checks: xman4289.com only — its update feed (<see cref="FeedUrl"/>, Velopack) and
/// its version API.
///
/// Owner 2026-11-09: "ดูรายละเอียดการอัพเดท ต้องคัดมาไว้ในตัวอัพเดทเลย … อย่ามีอะไร โยงไปถึง github เด็ดขาด ไม่ต้องการให้รู้
/// repo". Nothing here names GitHub any more: the packages, the feed and the release notes all come from
/// https://xman4289.com/cluadex/download/ (the site streams them from the release and strips GitHub from the notes), the
/// "all the details" button opens the notes inside the update window (<see cref="FetchReleaseNotesAsync"/>), and the
/// portable copy's fallback to the GitHub releases API is gone.
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
    /// <summary>The Velopack feed and its packages: xmanstudio CluadeXUpdateController (the feed's notes are cleaned there).</summary>
    public const string FeedBase = "https://xman4289.com/cluadex/download/";
    public const string FeedUrl = FeedBase + "releases.win.json";
    /// <summary>The last ten versions with their notes, already free of GitHub links (xmanstudio VersionController::all).</summary>
    public const string VersionsApi = BaseUrl + "/versions";

    /// <summary>How often a running app looks for a new release.</summary>
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(10);
    /// <summary>A full check runs at least this often even when the probe says nothing (or cannot reach the feed).</summary>
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
    private volatile Velopack.UpdateInfo? _vpkPending;
    // ~/.cluadex, not the Velopack install root — a failure counter stored inside the
    // directory the installer empties would be wiped by the very event it exists to
    // survive. See SettingsService.MigrateLegacyDataRoot.
    private readonly UpdateAttemptLog _attempts =
        UpdateAttemptLog.Load(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex"));

    /// <summary>Set to a folder (or http URL) holding a Velopack release feed — the Releases/ output of
    /// <c>vpk pack</c> — to test a self-update end to end before anything is published.</summary>
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
        // xman4289.com/cluadex/download/: Velopack asks for releases.win.json there and downloads the packages it lists
        // from the same folder — the site streams them, so the app never learns where they are kept.
        return new Velopack.Sources.SimpleWebSource(FeedBase, _downloader);
    }

    // ─── progress ──────────────────────────────────────────────────────────────────────

    // One figure for everyone (splash, update bar): bytes over a size known up front — UpdateProgressComposer.
    private readonly object _progressLock = new();
    private readonly UpdateProgressComposer _composer = new();
    private UpdatePhase _phase = UpdatePhase.Idle;
    private string? _phaseVersion, _phaseError;
    private bool _downloadBegun;          // the composer holds this download's figures (Downloading → Staged / Failed)
    private bool _stagedWithoutBytes;     // Velopack found the package already on disk: nothing was downloaded
    private long _seq;
    private DateTime _lastPublish = DateTime.MinValue;

    /// <summary>The latest state, for a window that opens in the middle of a download (stall seconds are as of now).</summary>
    public UpdateProgressInfo Progress { get { lock (_progressLock) return Compose(DateTime.UtcNow); } }

    /// <summary>The snapshot at <paramref name="now"/>. Under <see cref="_progressLock"/>. Every snapshot gets a higher
    /// <see cref="UpdateProgressInfo.Seq"/>, so a listener can drop one that arrives after a newer one.</summary>
    private UpdateProgressInfo Compose(DateTime now)
    {
        var info = new UpdateProgressInfo(_phase, _phaseVersion, 0, 0, 0, 0, false, _phaseError) { Seq = ++_seq };
        if (!_downloadBegun) return _phase == UpdatePhase.Staged ? info with { Percent = 100, FromCache = _stagedWithoutBytes } : info;
        var s = _composer.Read(now);
        info = info with
        {
            Bytes = s.Bytes, Total = s.Total,
            Delta = s.Route == UpdateProgressComposer.Route.Delta, DeltaCount = s.DeltaCount, FellBackToFull = s.FellBackToFull,
        };
        return _phase switch
        {
            UpdatePhase.Downloading when s.Stage == UpdateProgressComposer.Stage.Preparing =>
                info with { Phase = UpdatePhase.Preparing, Percent = 100, PhaseSeconds = s.StageSeconds },
            UpdatePhase.Downloading => info with
            {
                Percent = s.Percent, BytesPerSecond = s.BytesPerSecond, Resuming = s.Resuming, Error = s.Error,
                StalledSeconds = s.StalledSeconds, Redownloading = s.Redownloading,
            },
            UpdatePhase.Staged => info with { Percent = 100, FromCache = _stagedWithoutBytes },
            _ => info with { Percent = s.Percent },   // Failed: how far it got
        };
    }

    private void SetPhase(UpdatePhase phase, string? version, string? error = null)
    {
        lock (_progressLock)
        {
            _phase = phase; _phaseVersion = version; _phaseError = error;
            if (phase is UpdatePhase.Idle or UpdatePhase.Checking) _downloadBegun = false;
        }
        Publish(force: true);
    }

    /// <summary>A download starts: the composer learns the sizes up front, and the route Velopack will take.</summary>
    private void BeginDownload(Velopack.UpdateInfo info, string target)
    {
        var full = new UpdateProgressComposer.Asset(info.TargetFullRelease.FileName ?? "", info.TargetFullRelease.Size);
        var deltas = (info.DeltasToTarget ?? []).Select(d => new UpdateProgressComposer.Asset(d.FileName ?? "", d.Size)).ToArray();
        // The manager is built without UpdateOptions, so Velopack uses its default limit (its property is protected).
        BeginTracked(full, deltas, info.BaseRelease?.FileName != null, UpdateProgressComposer.DefaultMaximumDeltas, target);
    }

    private void BeginTracked(UpdateProgressComposer.Asset full, UpdateProgressComposer.Asset[] deltas, bool hasBase, int maxDeltas, string? target)
    {
        lock (_progressLock)
        {
            _composer.Begin(full, deltas, hasBase, maxDeltas, DateTime.UtcNow);
            _downloadBegun = true;
            _stagedWithoutBytes = false;
            _phase = UpdatePhase.Downloading; _phaseVersion = target; _phaseError = null;
        }
        Publish(force: true);
    }

    /// <summary>Velopack's own percent: a hint of its stage (patching), not the figure — see UpdateProgressComposer.</summary>
    private void OnVelopackPercent(int percent)
    {
        lock (_progressLock) { if (_phase == UpdatePhase.Downloading) _composer.OnVelopackPercent(percent, DateTime.UtcNow); }
        Publish(force: false);
    }

    /// <summary>The downloader's bytes: the figure, the speed, "resuming".</summary>
    private void OnFileProgress(ResumableDownloader.FileProgress f)
    {
        lock (_progressLock)
        {
            if (_phase != UpdatePhase.Downloading) return;   // the zip path (portable) has its own bar
            _composer.OnFile(f.FileName, f.Bytes, f.Total, f.FailuresInARow, f.LastError, DateTime.UtcNow);
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
            snap = Compose(now);
        }
        try { ProgressChanged?.Invoke(snap); } catch (Exception ex) { _log?.Warn("Update", $"progress listener failed: {ex.Message}"); }
        if (snap.Phase is UpdatePhase.Downloading or UpdatePhase.Preparing)
            try { OnDownloadProgress?.Invoke(snap.Percent, snap.Describe(thai: false)); } catch { }
    }

    // ─── one update job at a time ──────────────────────────────────────────────────────

    private readonly object _jobLock = new();
    private Task<string?>? _job;
    private volatile bool _applying;

    // ─── who gets updates by themselves (installer contract 2.1) ──────────────────────

    /// <summary>Set once at startup (MainViewModel): <see cref="AutoUpdateEntitlement.Automatic"/> — a valid paid or trial
    /// license, or a linked Xman Studio account, AND the user's switch on. Null = allowed (harnesses, older callers).
    /// When it says no, a new release is still FOUND and announced (<see cref="UpdatePhase.Available"/>), but nothing is
    /// downloaded, and nothing installs at startup or on exit; "อัปเดตเลย" (<see cref="DownloadNowAsync"/>) does it by hand.</summary>
    public Func<bool>? AutomaticAllowed { get; set; }

    /// <summary>May updates download and install without being asked right now?</summary>
    public bool Automatic
    {
        get { try { return AutomaticAllowed?.Invoke() ?? true; } catch { return true; } }
    }

    /// <summary>A newer release that was found but not downloaded because <see cref="Automatic"/> said no; null otherwise.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>
    /// The user pressed "อัปเดตเลย": check and download now even when <see cref="Automatic"/> says no. A check-only job
    /// that is running is awaited first (it ends at Available), then the download starts. The splash and the update bar
    /// show its progress as usual; the caller installs when it is staged. Returns the staged version, or null (nothing
    /// newer after all, or it failed — the phase says which).
    /// </summary>
    public async Task<string?> DownloadNowAsync()
    {
        Task<string?>? running;
        lock (_jobLock) running = _job is { IsCompleted: false } ? _job : null;
        if (running != null)
        {
            string? v = await running.ConfigureAwait(false);
            if (v != null || HasStagedUpdate) return v ?? StagedVersion;
        }
        Task<string?> job;
        lock (_jobLock)
        {
            // Someone else started a job meanwhile: join it (it reports progress the same way).
            if (_job is not { IsCompleted: false }) _job = Task.Run(() => CheckAndStageCoreAsync(manual: true));
            job = _job;
        }
        return await job.ConfigureAwait(false);
    }

    /// <summary>True while a check or download runs.</summary>
    public bool IsBusy { get { lock (_jobLock) return _job is { IsCompleted: false }; } }

    /// <summary>
    /// Check the update feed (xman4289.com) for a Velopack release and download it in the background. Returns the staged version, or null
    /// when there is nothing to stage. Everyone who asks while a check or download runs gets that same job — the
    /// splash, the main window and the periodic probe never start a second one (Velopack's lock would fail it).
    /// The job outlives its callers; it stops only when the app exits.
    /// </summary>
    public Task<string?> VelopackCheckAndStageAsync(CancellationToken ct = default)
    {
        lock (_jobLock)
        {
            if (_job is { IsCompleted: false }) return _job;
            _job = Task.Run(() => CheckAndStageCoreAsync(manual: false));
            return _job;
        }
    }

    private async Task<string?> CheckAndStageCoreAsync(bool manual)
    {
        if (!CanSelfUpdate) { _log?.Info("Update", "Velopack: not an installed build — self-update unavailable (portable/dev)"); return null; }
        AdoptStagedFromDisk();   // a package an earlier run downloaded is known before (and without) the network
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
            // Not entitled to automatic updates (or the user turned them off): say a new version is out, download nothing.
            if (!manual && !Automatic)
            {
                AvailableVersion = target;
                _log?.Info("Update", $"Velopack: v{target} is out — automatic update is off for this install (no paid/trial license, no linked account, or switched off); waiting for \"Update now\"");
                SetPhase(UpdatePhase.Available, target);
                return null;
            }
            // The circuit breaker. Without it a package that cannot be applied gets
            // retried on every launch forever — which is exactly how BrainX ended up
            // relaunching itself every 18 seconds. It stops the AUTOMATIC install only (splash, exit): the package is
            // still downloaded, so the update bar's Install & Restart is the manual way to try again.
            if (_attempts.ShouldStopTrying(target))
                _log?.Warn("Update", $"Velopack: v{target} already failed {UpdateAttemptLog.MaxConsecutiveFailures}× — automatic install paused; it is downloaded for a manual Install & Restart");

            int deltas = info.DeltasToTarget?.Length ?? 0;
            _log?.Info("Update", deltas > 0
                ? $"Velopack: downloading v{target} as {deltas} delta package(s) ({(info.DeltasToTarget ?? []).Sum(d => d.Size) / (1024.0 * 1024):0.0} MB; full package {info.TargetFullRelease.Size / (1024.0 * 1024):0} MB)…"
                : $"Velopack: downloading v{target} (full package {info.TargetFullRelease.Size / (1024.0 * 1024):0} MB)…");
            BeginDownload(info, target);
            var sw = Stopwatch.StartNew();
            // Nothing reports while a line is stalled or Update.exe patches: a heartbeat keeps the stall seconds and the
            // "preparing" seconds moving on the splash and the update bar.
            using (new System.Threading.Timer(_ => Publish(force: true), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
                await Vpk.DownloadUpdatesAsync(info, OnVelopackPercent, _shutdown.Token).ConfigureAwait(false);
            _vpkPending = info;
            AvailableVersion = null;   // it is downloaded now: Staged says the rest
            MarkDownloadStaged(target, sw.Elapsed);
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
            // Offline, but an earlier run downloaded an update: that one is still ready to install.
            if (target == null && _vpkPending != null)
            {
                SetPhase(UpdatePhase.Staged, StagedVersion);
                return StagedVersion;
            }
            SetPhase(UpdatePhase.Failed, target, why);
            return null;
        }
    }

    /// <summary>The download ended and the package is staged. No byte arrived = Velopack found it already on disk, which
    /// the snapshot says as <see cref="UpdateProgressInfo.FromCache"/> ("already downloaded" on the splash). True = from
    /// disk. Its own method so the harness drives this real step (tests/apitest UpdateProgressWire).</summary>
    private bool MarkDownloadStaged(string target, TimeSpan took)
    {
        bool fromCache;
        lock (_progressLock) fromCache = _stagedWithoutBytes = !_composer.Read(DateTime.UtcNow).AnyBytes;
        _log?.Info("Update", fromCache
            ? $"Velopack: v{target} was already downloaded — applies on exit or on Install"
            : $"Velopack: v{target} staged in {took.TotalSeconds:0} s — applies on exit or on Install");
        SetPhase(UpdatePhase.Staged, target);
        return fromCache;
    }

    /// <summary>
    /// An update an earlier run downloaded and never installed (Velopack's packages folder holds a full package newer
    /// than this version — read from disk, no network). Taken as the staged update; its version, or null. The splash
    /// installs it before the app opens, also offline.
    /// </summary>
    public string? AdoptStagedFromDisk()
    {
        if (!CanSelfUpdate) return null;
        try
        {
            if (_vpkPending != null) return StagedVersion;
            var asset = Vpk.UpdatePendingRestart;
            if (asset?.Version == null) return null;
            _vpkPending = new Velopack.UpdateInfo(asset, false, null, []);
            string v = asset.Version.ToString();
            _log?.Info("Update", $"Velopack: v{v} was downloaded by an earlier run and is waiting to be installed");
            lock (_progressLock) { _downloadBegun = false; _stagedWithoutBytes = true; }
            SetPhase(UpdatePhase.Staged, v);
            return v;
        }
        catch (Exception ex)
        {
            _log?.Warn("Update", $"Velopack: could not read the downloaded packages: {ex.Message}");
            return null;
        }
    }

    /// <summary>May <paramref name="version"/> be installed without the user asking (splash, exit)? Not when the
    /// circuit breaker tripped for it — see <see cref="StartupUpdatePolicy.MayAutoInstall"/>.</summary>
    public bool MayAutoInstall(string? version) => Automatic && StartupUpdatePolicy.MayAutoInstall(_attempts, version);

    // ─── finding a new release while the app runs ─────────────────────────────────────

    private CancellationTokenSource? _loop;
    private DateTime _lastFullCheckUtc = DateTime.MinValue;
    private string? _lastProbed;
    private volatile string? _lastOffered;   // the release a portable copy was last told about

    /// <summary>Start looking for new releases every <see cref="ProbeEvery"/> (idempotent). A dev build never polls.</summary>
    public void StartBackgroundChecks()
    {
        if (CurrentBuildKind == BuildKind.Dev && Environment.GetEnvironmentVariable(FeedOverrideVariable) == null) return;
        if (SettingsService.IsTrialCopy) return;   // a trial copy is the branch being tried — a release must not replace it
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
        string? inHand = CanSelfUpdate ? StagedVersion ?? AvailableVersion : _lastOffered;
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

    /// <summary>The newest release's version, read from the update feed (<see cref="FeedUrl"/>, cached by the site, a
    /// few KB); null when the site cannot be reached or the answer is not a feed.</summary>
    public async Task<string?> ProbeLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(FeedUrl, ct).ConfigureAwait(false);
            string? v = resp.IsSuccessStatusCode ? LatestFeedVersion(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
            if (v != _lastProbed) { _lastProbed = v; _log?.Info("Update", $"probe: newest release {(v != null ? "v" + v : $"unknown (HTTP {(int)resp.StatusCode})")}"); }
            return v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The highest version a Velopack feed (<c>{"Assets":[{"Version":"3.0.278",…}]}</c>, keys in any case)
    /// lists; null when it is not a feed or lists none. Public for the harness.</summary>
    public static string? LatestFeedVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement assets = default;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "Assets", StringComparison.OrdinalIgnoreCase)) assets = p.Value;
            if (assets.ValueKind != JsonValueKind.Array) return null;
            string? best = null;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in a.EnumerateObject())
                {
                    if (!string.Equals(p.Name, "Version", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.String) continue;
                    string v = (p.Value.GetString() ?? "").Trim().TrimStart('v', 'V');
                    if (VersionCore(v) is { } core && Version.TryParse(core, out _) && (best == null || IsNewer(v, best))) best = v;
                }
            }
            return best;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// What changed between the running version and <paramref name="to"/>, newest first — every version in between, each
    /// with its own notes — from the site's version list (<see cref="VersionsApi"/>, notes already free of GitHub).
    /// Empty when the site cannot be reached; the caller then shows the notes that came with the update.
    /// </summary>
    public async Task<List<ReleaseNoteEntry>> FetchReleaseNotesAsync(string from, string to, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(VersionsApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();
            return ParseVersionNotes(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), from, to);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Info("Update", $"release notes not loaded: {ex.GetType().Name}");
            return new();
        }
    }

    /// <summary><c>{ versions: [ { version, changelog, released_at } ] }</c> → the versions after <paramref name="from"/>
    /// up to and including <paramref name="to"/> that carry notes, newest first. A note that still holds a link to GitHub
    /// (an old release) loses that line. Public for the harness.</summary>
    public static List<ReleaseNoteEntry> ParseVersionNotes(string? json, string from, string to)
    {
        var list = new List<ReleaseNoteEntry>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) return list;
            foreach (var v in versions.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                string version = v.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.String ? (vv.GetString() ?? "").TrimStart('v', 'V') : "";
                if (VersionCore(version) is not { } core || !Version.TryParse(core, out _)) continue;
                if (!IsNewer(version, from) || IsNewer(version, to)) continue;
                string notes = v.TryGetProperty("changelog", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                notes = WhatsNewOnly(WithoutGitHub(notes));
                if (string.IsNullOrWhiteSpace(notes)) continue;
                DateTime? when = v.TryGetProperty("released_at", out var r) && r.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
                if (list.Any(e => e.Version == version)) continue;
                list.Add(new ReleaseNoteEntry(version, notes, when));
            }
        }
        catch (JsonException) { }
        list.Sort((a, b) => IsNewer(a.Version, b.Version) ? -1 : IsNewer(b.Version, a.Version) ? 1 : 0);
        return list;
    }

    /// <summary>Release notes without a link to GitHub: " ([#104](…github…))" goes, "[text](…github…)" keeps its text,
    /// a bare github URL goes, and the "Full Changelog" line goes. The site cleans notes already; this is the app's own
    /// guard for an older release or a feed served by an override. A feature named "GitHub" in the words stays.</summary>
    public static string WithoutGitHub(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return "";
        const System.Text.RegularExpressions.RegexOptions I = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        string s = notes.Replace("\r\n", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(\s*\[[^\]]*\]\([^)]*github[^)]*\)\s*\)", "", I);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*github[^)]*\)", "$1", I);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<?https?://[^\s)>]*github[^\s)>]*>?", "", I);
        var keep = s.Split('\n').Where(l => l.IndexOf("Full Changelog", StringComparison.OrdinalIgnoreCase) < 0)
            .Select(l => l.TrimEnd())
            .Where(l => l.Trim() is not ("-" or "*" or "" ) || l.Length == 0);
        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", keep), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>A release's whole text → only what is new: the "Install" / "Requirements" / "การติดตั้ง" sections of the
    /// download page are not news, so they stop at their heading. Public for the harness.</summary>
    public static string WhatsNewOnly(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return "";
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        int cut = Array.FindIndex(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l,
            @"^\s{0,3}#{1,6}\s*(install|installation|requirements|system requirements|download|การติดตั้ง|ความต้องการของระบบ|ดาวน์โหลด)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return (cut >= 0 ? string.Join("\n", lines.Take(cut)) : notes).Trim();
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
        var pending = _vpkPending;
        if (_applying || pending == null) return;
        string target = pending.TargetFullRelease.Version.ToString();
        if (!MayAutoInstall(target)) return;
        try
        {
            _applying = true;
            Vpk.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
            // Update.exe is running and waiting for this process: only now is it an attempt. Nothing of ours runs
            // afterwards to record it, so it is written here, before the exit goes on.
            _attempts.RecordAttempt(target);
            _log?.Info("Update", $"Velopack: v{target} installs after exit");
        }
        catch (Exception ex)
        {
            _applying = false;
            _log?.Warn("Update", $"Velopack: install on exit failed to start: {ex.Message}");
        }
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

    /// <summary>The staged release's notes (markdown, as packed with vpk --releaseNotes), or null.</summary>
    public string? StagedNotes
    {
        get { try { return _vpkPending?.TargetFullRelease?.NotesMarkdown; } catch { return null; } }
    }

    /// <summary>The staged package's size in bytes (0 when unknown).</summary>
    public long StagedSize
    {
        get { try { return _vpkPending?.TargetFullRelease?.Size ?? 0; } catch { return 0; } }
    }

    /// <summary>
    /// Up to <paramref name="max"/> short lines for "what's new" out of release-notes markdown: list items first
    /// (headings, links, emphasis, code ticks and trailing commit hashes stripped); plain lines when there is no list.
    /// Empty when there is nothing readable. Pure, for the test harness.
    /// </summary>
    public static IReadOnlyList<string> ReleaseNotesBullets(string? markdown, int max = 4)
    {
        var items = new List<string>();
        var plain = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown) || max <= 0) return items;
        foreach (string raw in WithoutGitHub(markdown).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("---") || line.StartsWith("```") || line.StartsWith('>')) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?:[-*+]|\d+[.)])\s+(.*)$");
            string text = CleanNote(m.Success ? m.Groups[1].Value : line);
            if (text.Length < 3) continue;
            if (m.Success) { if (!raw.StartsWith("  ") && !raw.StartsWith('\t')) items.Add(text); }   // nested items are detail
            else plain.Add(text);
        }
        var pick = items.Count > 0 ? items : plain;
        return pick.Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    private static string CleanNote(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");                 // images
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");              // [text](url)
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(?\b(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b\)?\s*$", "");      // trailing commit hash
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(#\d+\)\s*$", "");                       // trailing (#123)
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![\w*])[*_](\S(?:[^*_]*\S)?)[*_](?![\w*])", "$1");   // *one* _one_
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\[[ xX]\]\s*", "");                         // task-list box
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 120 ? s[..117].TrimEnd() + "…" : s;
    }

    /// <summary>Why automatic updating is paused (or what the last attempt did), in the UI's language; null when there
    /// is nothing to say.</summary>
    public string? PausedReason => _attempts.Describe(thai: IsThai);

    private bool IsThai
    {
        get { try { return _settingsService.Settings.Language?.StartsWith("th", StringComparison.OrdinalIgnoreCase) ?? false; } catch { return false; } }
    }

    /// <summary>Why the last <see cref="VelopackApplyAndRestart"/> did not restart, or null.</summary>
    public string? LastApplyError { get; private set; }

    /// <summary>
    /// Call once at startup, before any check. Whatever version we are running now
    /// is the verdict on the last attempt: if it is the one we were trying to reach
    /// the update landed and the failure history is wiped; if not, the count stands.
    /// </summary>
    public void NoteRunningVersion() => _attempts.NoteRunningVersion(CurrentVersion);

    /// <summary>
    /// Apply the staged update and restart. Update.exe is started first (it waits for this process to exit); the
    /// attempt is recorded once it really runs — a start that throws is no attempt — and then this process exits.
    /// Does not return on success. False (and <see cref="LastApplyError"/>) when nothing was handed over.
    /// </summary>
    public bool VelopackApplyAndRestart()
    {
        var pending = _vpkPending;
        if (pending == null) { LastApplyError = "no update is downloaded"; return false; }
        string target = pending.TargetFullRelease.Version.ToString();
        try
        {
            _applying = true;
            LastApplyError = null;
            _log?.Info("Update", $"Velopack: applying v{target} and restarting");
            Vpk.WaitExitThenApplyUpdates(pending, silent: false, restart: true, null);   // ApplyUpdatesAndRestart, minus its exit
            _attempts.RecordAttempt(target);           // flushed BEFORE we exit — nothing of ours runs afterwards
            try { _log?.Flush(); } catch { }
            Environment.Exit(0);
            return true;                               // unreachable
        }
        catch (Exception ex)
        {
            _applying = false;
            LastApplyError = ex.Message;
            _log?.Error("Update", $"Velopack apply failed: {ex.Message}");
            OnUpdateStatus?.Invoke($"Could not apply the update: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ask xman4289.com's version API (the only source; there is no GitHub fallback any more).</summary>
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

        _log?.Info("Update", $"no update offered — running v{CurrentVersion} is at or ahead of every published release");
        return null;
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

    /// <summary>The update the site announces is the installer (CluadeX-win-Setup.exe — the only download since
    /// 3.0.325), not a zip of files: a copy that Velopack does not update (a developer build) cannot unpack it over
    /// itself. Looked at before 170 MB are downloaded for nothing.</summary>
    public static bool IsInstallerFile(string? fileName, string? downloadUrl = null)
    {
        string name = !string.IsNullOrWhiteSpace(fileName) ? fileName!.Trim()
            : Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
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

            BeginTracked(new UpdateProgressComposer.Asset(ResumableDownloader.FileNameOf(info.DownloadUrl), info.FileSize), [], false, 0, info.NewVersion);
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

/// <summary>Preparing (appended, so the other values keep their numbers): every byte is in and Velopack verifies the
/// package / Update.exe applies the delta patches — no bytes move, so it is its own step, not a bar stuck at a split.</summary>
/// <summary>Available (appended last: the order is part of the harness's wire) = a newer release is out but this install
/// does not update by itself; it waits for "อัปเดตเลย".</summary>
public enum UpdatePhase { Idle, Checking, Downloading, Staged, Failed, Preparing, Available }

/// <summary>Where the update stands, for the splash and the update bar. Percent, Bytes and Total come from one source
/// (<see cref="UpdateProgressComposer"/>): bytes over the sizes the release feed states up front, never backwards within
/// a step. The positional shape is pinned by tests/apitest UpdateDownloadWire; the rest are init properties.</summary>
public sealed record UpdateProgressInfo(UpdatePhase Phase, string? Version, double Percent, long Bytes, long Total,
    double BytesPerSecond, bool Resuming, string? Error)
{
    public static readonly UpdateProgressInfo Idle = new(UpdatePhase.Idle, null, 0, 0, 0, 0, false, null);

    /// <summary>Order of the snapshots (higher = newer). A listener drops one older than what it already shows.</summary>
    public long Seq { get; init; }
    /// <summary>Seconds without a byte, once that is long enough to say (<see cref="UpdateProgressComposer.StallAfter"/>).</summary>
    public double? StalledSeconds { get; init; }
    /// <summary>Seconds in <see cref="UpdatePhase.Preparing"/>.</summary>
    public double PhaseSeconds { get; init; }
    /// <summary>The delta packages failed and Velopack is downloading the full package: the figure started again.</summary>
    public bool FellBackToFull { get; init; }
    /// <summary>A file restarted from its first byte (the server would not resume it): the figure holds until it is back.</summary>
    public bool Redownloading { get; init; }
    /// <summary>The download is a chain of <see cref="DeltaCount"/> delta packages.</summary>
    public bool Delta { get; init; }
    public int DeltaCount { get; init; }
    /// <summary>Staged without a byte downloaded now (an earlier run downloaded it).</summary>
    public bool FromCache { get; init; }

    /// <summary>Seconds left at the current speed, or null when it cannot be told.</summary>
    public double? EtaSeconds => Phase == UpdatePhase.Downloading && Total > 0 && BytesPerSecond > 1 && Bytes <= Total
        ? (Total - Bytes) / BytesPerSecond : null;

    /// <summary>"12.3 / 27.0 MB · 2.1 MB/s" (+ "· resuming", a stall, a fall-back to the full package), the preparing
    /// step, or the percent alone before the sizes are known.</summary>
    public string Describe(bool thai)
    {
        static string Mb(long b) => (b / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        static string N(double v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (Phase == UpdatePhase.Preparing)
            return Delta
                ? thai ? $"โหลดครบแล้ว — กำลังประกอบอัปเดตจาก {DeltaCount} แพตช์… {N(PhaseSeconds)} วินาที" : $"downloaded — applying {DeltaCount} patch(es)… {N(PhaseSeconds)} s"
                : thai ? $"โหลดครบแล้ว — กำลังตรวจไฟล์… {N(PhaseSeconds)} วินาที" : $"downloaded — verifying the package… {N(PhaseSeconds)} s";
        if (Total <= 0) return $"{Percent:0}%";
        string s = $"{Mb(Bytes)} / {Mb(Total)} MB";
        if (FellBackToFull) s = (thai ? "แพตช์ใช้ไม่ได้ — โหลดตัวเต็มแทน · " : "patches failed — downloading the full package · ") + s;
        if (BytesPerSecond > 1)
            s += BytesPerSecond >= 1024 * 1024
                ? $" · {(BytesPerSecond / (1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB/s"
                : $" · {(BytesPerSecond / 1024).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} KB/s";
        if (Redownloading) s += thai ? " · เซิร์ฟเวอร์ไม่ให้โหลดต่อ กำลังโหลดส่วนนั้นใหม่" : " · the server would not resume — fetching that part again";
        if (StalledSeconds is double st) s += thai ? $" · สัญญาณค้าง {N(st)} วินาที — กำลังลองใหม่…" : $" · connection stalled {N(st)} s — retrying…";
        if (Resuming) s += thai ? " · สัญญาณหลุด กำลังต่อจากเดิม" : " · connection dropped, resuming";
        return s;
    }
}

/// <summary>One version's notes for the update window (newest first).</summary>
public sealed record ReleaseNoteEntry(string Version, string Notes, DateTime? ReleasedAt);
