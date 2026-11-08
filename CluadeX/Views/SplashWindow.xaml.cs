using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Views;

/// <summary>
/// The startup screen: version, then each system the app depends on, checked for real and shown as it resolves —
/// data folder, chat database, license (online), update (Velopack: checked, downloaded to the end with one accurate
/// gauge, and installed with a restart before the app opens — see <see cref="RunUpdateAsync"/>), BrainX, the AI provider,
/// the GPU. It closes by itself when everything has answered; "Enter" appears early if something is slow, and is "Skip"
/// while an update downloads. Owner requests 2026-10-16, 2026-11-03.
/// </summary>
public partial class SplashWindow : Window
{
    public sealed class Check : INotifyPropertyChanged
    {
        private string _mark = "…", _detail = "";
        private Brush _brush = Pending;
        public required string Label { get; init; }
        public string Mark { get => _mark; set { _mark = value; On(); } }
        public Brush MarkBrush { get => _brush; set { _brush = value; On(); } }
        public string Detail { get => _detail; set { _detail = value; On(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
        public void Ok(string d) { Mark = "✓"; MarkBrush = Good; Detail = d; }
        public void Warn(string d) { Mark = "!"; MarkBrush = WarnBrush; Detail = d; }
        public void Bad(string d) { Mark = "✕"; MarkBrush = BadBrush; Detail = d; }
    }

    /// <summary>One of the four steps of the update mode (design board UpdateSplash).</summary>
    public sealed class UpStep : INotifyPropertyChanged
    {
        private int _state;   // 0 pending · 1 running · 2 done
        private string _detail = "", _label = "";
        public string Label { get => _label; set { _label = value; On(); } }
        public string Detail { get => _detail; set { _detail = value; On(); } }
        public int State
        {
            get => _state;
            set { _state = value; On(nameof(Mark)); On(nameof(RingBrush)); On(nameof(FillBrush)); On(nameof(MarkBrush)); On(nameof(LabelBrush)); On(nameof(Weight)); }
        }
        public string Mark => _state switch { 2 => "✓", 1 => "↓", _ => "" };
        public Brush RingBrush => _state == 0 ? UpRing0 : _state == 1 ? UpRing1 : Brushes.Transparent;
        public Brush FillBrush => _state == 1 ? UpFill1 : _state == 2 ? UpFill2 : Brushes.Transparent;
        public Brush MarkBrush => _state == 2 ? Good : Brushes.White;
        public Brush LabelBrush => _state == 0 ? UpLabel0 : UpLabel1;
        public FontWeight Weight => _state == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    }
    private static readonly Brush UpRing0 = Frozen("#40FFFFFF"), UpRing1 = Frozen("#C084FC"), UpFill1 = Frozen("#A855F7"), UpFill2 = Frozen("#2E4ADE80"),
        UpLabel0 = Frozen("#BBA9DD"), UpLabel1 = Frozen("#F4EEFF");

    private static readonly Brush Pending = Frozen("#9B87C9"), Good = Frozen("#6EE7B7"), WarnBrush = Frozen("#FCD34D"), BadBrush = Frozen("#FB7185");
    private static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    private readonly IServiceProvider _sp;
    private readonly bool _thai;
    private readonly ObservableCollection<Check> _checks = new();
    private bool _entered;
    private int _done, _total;
    private double _updateShare, _overallShown;   // the update row's share of the overall bar while it downloads (0..1)
    private bool _updateRowDone;                  // the update row shows its final state
    private long _updateSeq;                      // the newest update snapshot drawn
    private bool _handingOver;                    // the restart into the new version has begun: no Enter/Skip any more

    private readonly ObservableCollection<UpStep> _upSteps = new();
    private bool _updateMode;

    /// <summary>The test renders build the window without running the checks (no network, no MCP, no restart).</summary>
    public static bool PreviewOnly { get; set; }

    /// <summary>The splash ran the startup update check (MainViewModel skips its own silent one).</summary>
    public static bool StartupUpdateHandled { get; private set; }

    public SplashWindow(IServiceProvider sp)
    {
        InitializeComponent();
        _sp = sp;
        _thai = sp.GetService<SettingsService>()?.Settings.Language?.StartsWith("th", StringComparison.OrdinalIgnoreCase) ?? true;
        CheckList.ItemsSource = _checks;
        var asm = typeof(App).Assembly;
        string info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        VersionText.Text = "v" + (info.Length > 0 ? info.Replace("+", " · ") : asm.GetName().Version?.ToString(3));
        HeadText.Text = CheckingHead;
        EnterTitle.Text = T("เข้าใช้งาน", "Enter");
        // A different girl each start (never the last one), in either of her poses; the original splash girl when the
        // build has none.
        string? girl = GirlArt.PickName(sp.GetService<SettingsService>() is { } ss ? System.IO.Path.Combine(ss.DataRoot, "splash-girl.txt") : null);
        if (GirlArt.PoseImage(girl, GirlArt.PickPose()) is { } art)
        {
            SplashGirl.Source = art;
            SplashGirlGlow.Color = GirlArt.Glow(girl);
        }
        UpSteps.ItemsSource = _upSteps;
        Loaded += async (_, _) => { if (!PreviewOnly) await RunAsync(); };
        // Any close counts as going in — Alt+F4 included: the update must never restart under a user who is in the app.
        Closing += (_, _) => _entered = true;
    }

    private string T(string th, string en) => _thai ? th : en;

    private Check Add(string th, string en) { var c = new Check { Label = T(th, en) }; _checks.Add(c); return c; }

    /// <summary>The overall bar: checks done, plus the update's own progress while it downloads. Never backwards.</summary>
    private void Progress()
    {
        _overallShown = Math.Max(_overallShown, StartupUpdatePolicy.OverallPercent(_done, _total, _updateShare));
        OverallBar.Value = _overallShown;
    }

    private async Task RunAsync()
    {
        var data = Add("ข้อมูลและตั้งค่า", "Data & settings");
        var chats = Add("ฐานข้อมูลแชท", "Chat database");
        var license = Add("ไลเซนส์", "License");
        var update = Add("อัปเดต", "Update");
        var brain = Add("BrainX", "BrainX");
        var ai = Add("โมเดล AI", "AI model");
        var gpu = Add("การ์ดจอ", "GPU");
        _total = _checks.Count;
        void Step() { _done++; Progress(); }

        // Slow so the Enter button shows if the network is: 6 s into the checks, the user may go in. (An update that
        // downloads shows it at once, as Skip.)
        _ = ShowEnterLaterAsync();

        // Data + chats
        try
        {
            var settings = _sp.GetRequiredService<SettingsService>();
            data.Ok(settings.DataRoot);
        }
        catch (Exception ex) { data.Bad(ex.Message); }
        Step();
        try
        {
            int n = await Task.Run(() => _sp.GetRequiredService<ChatPersistenceService>().LoadSessionList().Count);
            chats.Ok(T($"{n} แชท", $"{n} chats"));
        }
        catch (Exception ex) { chats.Warn(ex.Message); }
        Step();

        // License, update, BrainX — in parallel (network)
        // The license check the app runs at startup anyway (App.LicenseStartup) — waited for, not repeated.
        var licenseTask = Task.Run(async () =>
        {
            var act = _sp.GetRequiredService<ActivationService>();
            for (int i = 0; i < 20 && App.LicenseStartup == null; i++) await Task.Delay(100);   // started a moment after us
            if (App.LicenseStartup is { } running) await Task.WhenAny(running, Task.Delay(8000));
            return act;
        });
        var updateTask = RunUpdateAsync(update);
        var brainTask = Task.Run(async () =>
        {
            var mcp = _sp.GetRequiredService<McpServerManager>();
            // The MCP list (mcp_servers.json) is read by the app's startup task, which may not have got to it when the
            // splash opens: it said "not configured" on every start while BrainX connected two seconds later (owner
            // 2026-10-29). "Not configured" is said only once that work (the load, an installed BrainX registered) is
            // done and found none; otherwise this waits for the connection, within the time this row always had.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var startup = mcp.WaitForStartupAsync(TimeSpan.FromSeconds(10));
            bool StartupDone() => startup.IsCompletedSuccessfully && startup.Result;   // false: still running when the wait ended
            while (!mcp.IsBrainConnected && DateTime.UtcNow < deadline)
            {
                if (StartupDone() && mcp.BrainServerName == null) break;
                await Task.Delay(250);
            }
            if (mcp.BrainServerName == null && StartupDone()) return (false, T("ยังไม่ได้ตั้งค่า", "not configured"));
            return (mcp.IsBrainConnected, mcp.IsBrainConnected ? T($"เชื่อมต่อแล้ว · {mcp.BrainToolCount} เครื่องมือ", $"connected · {mcp.BrainToolCount} tools")
                                                             : T("กำลังเชื่อมต่อ…", "connecting…") + " " + mcp.BrainStatusText);
        });

        try
        {
            var act = await licenseTask;
            string tier = act.TierDisplayName;
            string exp = act.ExpiresAtUtc is { } e ? T($" · ถึง {e.ToLocalTime():d MMM yyyy}", $" · until {e.ToLocalTime():d MMM yyyy}") : "";
            if (act.IsActivated) license.Ok(tier + exp);
            else license.Warn(T($"{tier} — ยังไม่ได้เปิดใช้ Pro", $"{tier} — Pro not active"));
        }
        catch (Exception ex) { license.Warn(T("ตรวจออนไลน์ไม่ได้: ", "online check failed: ") + ex.Message); }
        Step();

        try
        {
            var (ok, msg) = await brainTask;
            if (ok) brain.Ok(msg); else brain.Warn(msg);
        }
        catch (Exception ex) { brain.Warn(ex.Message); }
        Step();

        // AI + GPU (local, fast)
        try
        {
            var mgr = _sp.GetRequiredService<AiProviderManager>();
            var s = _sp.GetRequiredService<SettingsService>().Settings;
            string model = s.ProviderConfigs.TryGetValue(mgr.ActiveProviderType.ToString(), out var cfg) && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId)
                ? cfg.EffectiveModelId! : s.SelectedModelName ?? "";
            string effort = Models.EffortLevels.Normalize(s.ReasoningEffort);
            ai.Ok($"{mgr.ActiveProvider.DisplayName} · {model} · effort {effort}");
        }
        catch (Exception ex) { ai.Warn(ex.Message); }
        Step();
        try
        {
            var g = await Task.Run(() => _sp.GetRequiredService<GpuDetectionService>().DetectGpu());
            if (g.VramTotalBytes > 0) gpu.Ok($"{g.Name} · {g.VramTotalBytes / (1024 * 1024 * 1024.0):0.#} GB");
            else gpu.Warn(T("ไม่พบ GPU สำหรับโมเดลในเครื่อง", "no GPU for local models") + (g.Name != "Unknown" ? $" ({g.Name})" : ""));
        }
        catch (Exception ex) { gpu.Warn(ex.Message); }
        Step();

        bool restarting = await updateTask;
        _updateShare = 0;
        Step();
        if (restarting) return;   // the update applies and the app restarts — this window goes with it

        HeadText.Text = T("พร้อมใช้งาน", "Ready");
        await Task.Delay(900);
        Enter();
    }

    /// <summary>
    /// The startup update (installed builds only — a dev or portable copy is told, never updated from here). Owner
    /// 2026-11-03: "หน้า สแปรช หากเจออัพเดท ให้โหลดให้เสร็จแล้วติดตั้งก่อน ระบบโหลดต้องแม่นยำเปอเซ็น ทำให้ดี อย่าค้าง".
    /// An update an earlier run downloaded installs at once (no network needed). Otherwise: check (bounded by
    /// <see cref="StartupUpdatePolicy.CheckCap"/>), download to the end with one accurate figure, then save, install and
    /// restart into the new version before the app opens. Skip is there the whole time; a failure enters the app with the
    /// reason on the row. True = restarting into the new version.
    /// </summary>
    private async Task<bool> RunUpdateAsync(Check update)
    {
        var up = _sp.GetService<AutoUpdateService>();
        if (up == null) { update.Warn("—"); return false; }
        try
        {
            up.NoteRunningVersion();
            if (!up.CanSelfUpdate)
            {
                update.Warn(T("รุ่น dev/พกพา — ไม่อัปเดตอัตโนมัติ", "dev/portable build — no auto-update"));
                return false;
            }

            // Downloaded by an earlier run and never installed: read from disk (off this thread — it opens the package),
            // so this works offline too.
            if (await Task.Run(up.AdoptStagedFromDisk) is { } onDisk && up.MayAutoInstall(onDisk))
                return await InstallNowAsync(up, update, onDisk, downloadedBefore: true);

            // From a download thread: BeginInvoke, never Invoke — a download thread must not wait on this window.
            void OnProgress(UpdateProgressInfo p) => Dispatcher.BeginInvoke(() => RenderUpdate(update, p));
            up.ProgressChanged += OnProgress;
            try
            {
                update.Detail = T("กำลังตรวจหาเวอร์ชันใหม่…", "checking for a new version…");
                var job = up.VelopackCheckAndStageAsync();   // the same job the main window and the probe join
                var sinceCheck = System.Diagnostics.Stopwatch.StartNew();
                bool finished = false;
                while (!finished)
                {
                    var now = up.Progress;
                    switch (StartupUpdatePolicy.Decide(job.IsCompleted, now.Phase, sinceCheck.Elapsed, _entered))
                    {
                        case StartupUpdatePolicy.Next.Skipped:
                            // In the app: the download goes on (the update bar shows it) and installs when the app closes.
                            // StartupUpdateHandled stays false, so the main window joins the same job.
                            return false;
                        case StartupUpdatePolicy.Next.CheckTooSlow:
                            EndUpdateRow();
                            update.Warn(T("เน็ตช้า — ตรวจอัปเดตต่อเบื้องหลัง", "slow network — the update check goes on in the background"));
                            return false;
                        case StartupUpdatePolicy.Next.Finished:
                            finished = true;
                            continue;
                    }
                    if (now.Phase is UpdatePhase.Downloading or UpdatePhase.Preparing && now.Version is { } v && !up.MayAutoInstall(v))
                    {
                        // The circuit breaker holds this version: it would not install from here, so the app is not held
                        // for its download. It goes on in the background; the update bar offers Install & Restart.
                        EndUpdateRow();
                        update.Warn(up.PausedReason ?? T($"v{v} รอติดตั้งเอง", $"v{v} waits for a manual install"));
                        return false;
                    }
                    RenderUpdate(update, now);   // also between reports: the stall seconds keep counting
                    await Task.WhenAny(job, Task.Delay(500));
                }
                string? staged = await job;
                var end = up.Progress;
                if (staged == null)
                {
                    StartupUpdateHandled = true;   // the background probe retries; the main window does not start a second check
                    EndUpdateRow();
                    // Found but not downloaded: this install does not update by itself (installer contract 2.1).
                    if (end.Phase == UpdatePhase.Available && up.AvailableVersion is { } avail)
                        update.Warn(string.Format(LocalizedResources.Instance["update.available.splash"], avail));
                    else if (end.Phase == UpdatePhase.Failed)
                        update.Bad(T($"อัปเดตไม่สำเร็จ: {end.Error} — จะลองใหม่เองเบื้องหลัง", $"update failed: {end.Error} — it retries in the background"));
                    // A downloaded version the circuit breaker holds is waiting — not "up to date".
                    else if (up.StagedVersion is { } held && !up.MayAutoInstall(held))
                        update.Warn(!up.Automatic ? string.Format(LocalizedResources.Instance["update.available.splash"], held)
                            : up.PausedReason ?? T($"v{held} รอติดตั้งเอง", $"v{held} waits for a manual install"));
                    else update.Ok(T("เป็นเวอร์ชันล่าสุดแล้ว", "up to date"));
                    return false;
                }
                // Already in the app (Skip, Enter, or the splash was closed): never restart under the user.
                if (_entered) return false;
                if (!up.MayAutoInstall(staged))
                {
                    // The circuit breaker: this version already failed to install too often. The update bar offers it.
                    StartupUpdateHandled = true;
                    EndUpdateRow();
                    update.Warn(up.PausedReason ?? T($"v{staged} รอติดตั้งเอง", $"v{staged} waits for a manual install"));
                    return false;
                }
                return await InstallNowAsync(up, update, staged, downloadedBefore: end.FromCache);
            }
            finally { up.ProgressChanged -= OnProgress; }
        }
        catch (Exception ex)
        {
            EndUpdateRow();
            update.Warn(ex.Message);
            return false;
        }
    }

    /// <summary>Draw one snapshot of the update on the row, its gauge, the head line and the overall bar. UI thread.</summary>
    private void RenderUpdate(Check row, UpdateProgressInfo p)
    {
        if (_updateRowDone || p.Seq < _updateSeq) return;   // the flow wrote the final state / an older snapshot arrived late
        _updateSeq = p.Seq;
        switch (p.Phase)
        {
            case UpdatePhase.Checking:
                row.Detail = T("กำลังตรวจหาเวอร์ชันใหม่…", "checking for a new version…");
                break;
            case UpdatePhase.Downloading:
                ShowUpdateMode(p.Version, p.Delta);
                if (p.FellBackToFull && _upSteps.Count > 0) _upSteps[0].Label = L("splash.up.s1");   // the deltas failed: the whole package now
                SetRing(p.Percent, p.Total > 0 ? $"{p.Bytes / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB" : "");
                SetUpStep(0, p.StalledSeconds is double sst ? string.Format(L("splash.up.stalled"), sst.ToString("0"))
                    : p.Resuming ? L("splash.up.resuming")
                    : p.BytesPerSecond > 1 ? (p.BytesPerSecond >= 1048576 ? $"{p.BytesPerSecond / 1048576:0.0} MB/s" : $"{p.BytesPerSecond / 1024:0} KB/s") : "");
                ShowSkip(true, downloading: true);
                HeadText.Text = T($"กำลังดาวน์โหลดอัปเดต v{p.Version} — ติดตั้งก่อนเปิดโปรแกรม", $"Downloading update v{p.Version} — it installs before the app opens");
                UpdatePanel.Visibility = Visibility.Visible;
                UpdateBar.IsIndeterminate = false;
                UpdateBar.Value = p.Percent;
                // Describe is the percent alone when the feed stated no sizes: not twice.
                UpdateText.Text = p.Total > 0 ? $"v{p.Version} · {p.Percent:0}% · {p.Describe(_thai)}" : $"v{p.Version} · {p.Describe(_thai)}";
                bool stalled = p.StalledSeconds != null || p.Resuming;
                row.Mark = "…";
                row.MarkBrush = stalled ? WarnBrush : Pending;
                row.Detail = p.StalledSeconds is double st
                    ? T($"สัญญาณค้าง {st:0} วินาที — กำลังลองใหม่…", $"connection stalled {st:0} s — retrying…")
                    : p.Resuming ? T("สัญญาณหลุด — กำลังต่อจากเดิม…", "connection dropped — resuming…")
                    : T($"กำลังดาวน์โหลด v{p.Version} · {p.Percent:0}%", $"downloading v{p.Version} · {p.Percent:0}%");
                _updateShare = 0.95 * p.Percent / 100;   // the rest: preparing, saving, the restart
                break;
            case UpdatePhase.Preparing:
                ShowUpdateMode(p.Version, p.Delta);
                SetRing(100, _upMb);
                SetUpStep(1, $"{p.PhaseSeconds:0} s");
                ShowSkip(true, downloading: true);
                UpdatePanel.Visibility = Visibility.Visible;
                UpdateBar.Value = 100;
                UpdateBar.IsIndeterminate = true;        // its own step: nothing to count, but it is moving
                UpdateText.Text = $"v{p.Version} · {p.Describe(_thai)}";
                row.Mark = "…";
                row.MarkBrush = Pending;
                row.Detail = T("โหลดครบแล้ว — กำลังเตรียมติดตั้ง…", "downloaded — preparing to install…");
                _updateShare = 0.96;
                break;
        }
        Progress();
    }

    /// <summary>The update row is final: no late snapshot redraws it, the gauge goes, Enter is Enter again, and the head
    /// line stops promising an install that will not happen before the app opens (the other checks may still run).</summary>
    private void EndUpdateRow()
    {
        HideUpdateMode();
        _updateRowDone = true;
        UpdateBar.IsIndeterminate = false;
        UpdatePanel.Visibility = Visibility.Collapsed;
        ShowSkip(false, downloading: false);
        _updateShare = 0;
        HeadText.Text = CheckingHead;
    }

    private string CheckingHead => T("กำลังตรวจระบบ…", "Checking the system…");

    /// <summary>Save what OnExit would save (Velopack exits without it), install, restart. Skip still works while saving.</summary>
    private async Task<bool> InstallNowAsync(AutoUpdateService up, Check update, string version, bool downloadedBefore)
    {
        _updateRowDone = true;
        StartupUpdateHandled = true;
        if (_entered) return false;
        ShowSkip(true, downloading: false);
        HeadText.Text = T($"กำลังอัปเดตเป็น v{version}…", $"Updating to v{version}…");
        update.Mark = "…";
        update.MarkBrush = Pending;
        update.Detail = downloadedBefore
            ? T($"v{version} ดาวน์โหลดไว้แล้ว — กำลังเตรียมติดตั้ง…", $"v{version} is already downloaded — preparing to install…")
            : T($"ดาวน์โหลด v{version} ครบแล้ว — กำลังเตรียมติดตั้ง…", $"v{version} downloaded — preparing to install…");
        UpdatePanel.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = false;
        UpdateBar.Value = 100;
        UpdateText.Text = T("กำลังบันทึกการตั้งค่าและแชทก่อนเริ่มใหม่…", "saving settings and chats before the restart…");
        ShowUpdateMode(version, delta: false);
        SetRing(100, _upMb);
        SetUpStep(2, "");
        _updateShare = 0.98;
        Progress();

        await App.PrepareForUpdateRestartAsync(_sp, StartupUpdatePolicy.RestartPrepCap);   // bounded
        if (_entered) return false;   // skipped while saving: it installs when the app closes

        _handingOver = true;
        EnterButton.Visibility = Visibility.Collapsed;   // from here it is the restart
        update.Detail = T($"กำลังติดตั้ง v{version} และเปิดใหม่…", $"installing v{version} and restarting…");
        UpdateText.Text = T("CluadeX จะเปิดขึ้นมาใหม่เองในเวอร์ชันใหม่", "CluadeX opens again by itself in the new version");
        SetUpStep(3, "");
        UpdateBar.IsIndeterminate = true;
        await Task.Delay(400);   // drawn before the process hands over
        if (_entered) return false;
        if (up.VelopackApplyAndRestart()) return true;   // in practice the process has already exited

        // Nothing was handed over (the attempt is not counted): say why, and open this version.
        _handingOver = false;
        EndUpdateRow();   // also takes back "Updating to v…" from the head line
        string why = up.LastApplyError ?? "?";
        update.Bad(T($"ติดตั้ง v{version} ไม่สำเร็จ: {why} — ลองอีกครั้งได้ด้วย “ติดตั้งแล้วเริ่มใหม่” ในแอป",
                     $"could not install v{version}: {why} — try again with Install & Restart in the app"));
        return false;
    }

    /// <summary>Enter, or — while the startup update downloads or installs — Skip, saying what skipping does.</summary>
    private void ShowSkip(bool on, bool downloading)
    {
        if (on && _updateMode)
        {
            // the update view: "เข้าใช้งานก่อน", and the line beside it says what that means
            EnterTitle.Text = L("splash.up.enter");
            EnterSub.Visibility = Visibility.Collapsed;
            if (!_entered && !_handingOver) EnterButton.Visibility = Visibility.Visible;
        }
        else if (on)
        {
            EnterTitle.Text = T("ข้าม", "Skip");
            EnterSub.Text = downloading
                ? T("โหลดต่อเบื้องหลัง แล้วติดตั้งตอนปิดโปรแกรม", "keep downloading in the background, install when you close")
                : T("ติดตั้งตอนปิดโปรแกรม", "install when you close");
            EnterSub.Visibility = Visibility.Visible;
            if (!_entered) EnterButton.Visibility = Visibility.Visible;
        }
        else
        {
            EnterTitle.Text = T("เข้าใช้งาน", "Enter");
            EnterSub.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowEnterLaterAsync()
    {
        await Task.Delay(6000);   // on this window's thread: no Invoke from a pool thread
        if (!_entered && !_handingOver) EnterButton.Visibility = Visibility.Visible;
    }

    private void Enter_Click(object sender, RoutedEventArgs e) => Enter();

    // ── update mode ──

    private static string L(string key) => LocalizedResources.Instance[key];
    private string _upMb = "";

    /// <summary>The card turns into the update view (once): brand and checks step aside, the four steps appear.</summary>
    private void ShowUpdateMode(string? version, bool delta)
    {
        if (_updateMode) return;
        _updateMode = true;
        _upSteps.Clear();
        foreach (string k in new[] { delta ? "splash.up.s1delta" : "splash.up.s1", "splash.up.s2", "splash.up.s3", "splash.up.s4" })
            _upSteps.Add(new UpStep { Label = L(k) });
        UpHead.Text = L("splash.up.head");
        UpVersions.Text = $"{AutoUpdateService.CurrentVersion} → {version}";
        UpSafe.Text = L("splash.up.safe");
        UpSlowHint.Text = L("splash.up.slow");
        BrandPanel.Visibility = Visibility.Hidden;
        ChecksPanel.Visibility = Visibility.Hidden;
        UpdateMode.Visibility = Visibility.Visible;
        OverallBar.Visibility = Visibility.Collapsed;
        UpSlowHint.Visibility = Visibility.Visible;
        SetRing(0, "");
    }

    private void HideUpdateMode()
    {
        if (!_updateMode) return;
        _updateMode = false;
        UpdateMode.Visibility = Visibility.Collapsed;
        BrandPanel.Visibility = Visibility.Visible;
        ChecksPanel.Visibility = Visibility.Visible;
        OverallBar.Visibility = Visibility.Visible;
        UpSlowHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Step <paramref name="running"/> runs (earlier ones done); its detail is the speed, a stall, the seconds.</summary>
    private void SetUpStep(int running, string detail)
    {
        for (int i = 0; i < _upSteps.Count; i++)
        {
            _upSteps[i].State = i < running ? 2 : i == running ? 1 : 0;
            _upSteps[i].Detail = i == running ? detail : "";
        }
    }

    /// <summary>The ring's arc from 12 o'clock, clockwise, and the figure in its middle.</summary>
    private void SetRing(double percent, string mb)
    {
        double pct = Math.Clamp(percent, 0, 100);
        _upMb = mb;
        UpPct.Text = $"{pct:0}%";
        UpMb.Text = mb;
        UpMb.Visibility = mb.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        const double size = 168, r = (size - 10) / 2 - 5, c = size / 2;
        if (pct <= 0) { UpArc.Data = null; return; }
        double a = Math.Min(pct, 99.99) / 100 * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        UpArc.Data = g;
    }

    /// <summary>For the renders only: the update view as it looks at <paramref name="p"/>.</summary>
    public void PreviewUpdate(UpdateProgressInfo p) => RenderUpdate(new Check { Label = "update" }, p);

    private void Enter()
    {
        if (_entered) return;
        _entered = true;
        DialogResult = true;
    }
}
