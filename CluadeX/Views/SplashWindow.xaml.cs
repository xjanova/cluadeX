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
/// data folder, chat database, license (online), update (Velopack: checked, downloaded with a gauge, and applied
/// with a restart when there is one), BrainX, the AI provider, the GPU. It closes by itself when everything has
/// answered; "Enter" appears early if something is slow. Owner request 2026-10-16.
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

    private static readonly Brush Pending = Frozen("#9B87C9"), Good = Frozen("#6EE7B7"), WarnBrush = Frozen("#FCD34D"), BadBrush = Frozen("#FB7185");
    private static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    private readonly IServiceProvider _sp;
    private readonly bool _thai;
    private readonly ObservableCollection<Check> _checks = new();
    private bool _entered;

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
        HeadText.Text = T("กำลังตรวจระบบ…", "Checking the system…");
        EnterButton.Content = T("เข้าใช้งาน", "Enter");
        Loaded += async (_, _) => await RunAsync();
    }

    private string T(string th, string en) => _thai ? th : en;

    private Check Add(string th, string en) { var c = new Check { Label = T(th, en) }; _checks.Add(c); return c; }

    private void Progress(int done, int total) => OverallBar.Value = 100.0 * done / total;

    private async Task RunAsync()
    {
        var data = Add("ข้อมูลและตั้งค่า", "Data & settings");
        var chats = Add("ฐานข้อมูลแชท", "Chat database");
        var license = Add("ไลเซนส์", "License");
        var update = Add("อัปเดต", "Update");
        var brain = Add("BrainX", "BrainX");
        var ai = Add("โมเดล AI", "AI model");
        var gpu = Add("การ์ดจอ", "GPU");
        int total = _checks.Count, done = 0;
        void Step() => Progress(++done, total);

        // Slow so the Enter button shows if the network is: 6 s into the checks, the user may go in.
        _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(() => { if (!_entered) EnterButton.Visibility = Visibility.Visible; }));

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
            if (mcp.BrainServerName == null) return (false, T("ยังไม่ได้ตั้งค่า", "not configured"));
            for (int i = 0; i < 40 && !mcp.IsBrainConnected; i++) await Task.Delay(250);
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
        Step();
        if (restarting) return;   // the update applies and the app restarts — this window goes with it

        HeadText.Text = T("พร้อมใช้งาน", "Ready");
        await Task.Delay(900);
        Enter();
    }

    /// <summary>Velopack: check, download with the gauge, apply + restart. True = restarting into the new version.</summary>
    private async Task<bool> RunUpdateAsync(Check update)
    {
        var up = _sp.GetService<AutoUpdateService>();
        if (up == null) { update.Warn("—"); return false; }
        try
        {
            up.NoteRunningVersion();
            if (up.PausedReason is { } paused) { update.Warn(paused); return false; }
            if (!up.CanSelfUpdate)
            {
                update.Warn(T("รุ่น dev/พกพา — ไม่อัปเดตอัตโนมัติ", "dev/portable build — no auto-update"));
                return false;
            }
            void OnProgress(double p, string s) => Dispatcher.Invoke(() =>
            {
                UpdatePanel.Visibility = Visibility.Visible;
                UpdateBar.Value = p;
                UpdateText.Text = s;
                update.Detail = $"{p:0}%";
            });
            up.OnDownloadProgress += OnProgress;
            try
            {
                update.Detail = T("กำลังตรวจหาเวอร์ชันใหม่…", "checking for a new version…");
                var check = up.VelopackCheckAndStageAsync();
                // A check that cannot reach GitHub in 20 s does not hold the app: it goes on in the background.
                if (await Task.WhenAny(check, Task.Delay(20000)) != check)
                {
                    update.Warn(T("เน็ตช้า — ตรวจต่อเบื้องหลัง", "slow network — checking in the background"));
                    return false;   // StartupUpdateHandled stays false: the app's own check runs after it opens
                }
                string? staged = await check;
                if (staged == null) { StartupUpdateHandled = true; update.Ok(T("เป็นเวอร์ชันล่าสุดแล้ว", "up to date")); return false; }
                // Already in the app (Enter was pressed): never restart under the user — the app's banner offers it.
                if (_entered) return false;
                StartupUpdateHandled = true;
                update.Ok(T($"ดาวน์โหลด v{staged} แล้ว — กำลังติดตั้งและเปิดใหม่", $"v{staged} downloaded — installing and restarting"));
                HeadText.Text = T($"กำลังอัปเดตเป็น v{staged}…", $"Updating to v{staged}…");
                await Task.Delay(1200);
                return up.VelopackApplyAndRestart();
            }
            finally { up.OnDownloadProgress -= OnProgress; }
        }
        catch (Exception ex) { update.Warn(ex.Message); return false; }
    }

    private void Enter_Click(object sender, RoutedEventArgs e) => Enter();

    private void Enter()
    {
        if (_entered) return;
        _entered = true;
        DialogResult = true;
    }
}
