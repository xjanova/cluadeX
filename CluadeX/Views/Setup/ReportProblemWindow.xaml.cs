using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.BugReports;

namespace CluadeX.Views.Setup;

/// <summary>
/// "แจ้งปัญหา" (owner 2026-11-09): the user writes what happened, ticks what goes with it (this session's log, version
/// and PC details, a settings summary, recent errors, a screenshot, the open chat — the chat off unless ticked), may
/// look at exactly what will be sent, and sends it to Xman Studio. The answer is a ticket number, or "kept to send
/// later" when the server cannot be reached — never an exception's text.
///
/// One click sends once: the button is off while a send is under way, and every send of this window carries the same
/// client_report_id, so the server files it once even if the answer was lost and it is sent again. Closing the window
/// while it sends keeps the report in the queue (it may already have arrived — the id makes a second arrival a no-op).
/// </summary>
public partial class ReportProblemWindow : Window
{
    private readonly BugReportService _service;
    private readonly DebugLogService? _log;
    private readonly SettingsService? _settings;
    private readonly GpuDetectionService? _gpu;
    private readonly Func<string?>? _chat;
    private readonly byte[]? _screenshot;
    private readonly Dictionary<string, object?> _display;
    private readonly string _clientReportId = BugReportService.NewClientReportId();
    private readonly CancellationTokenSource _life = new();
    private Task<IReadOnlyDictionary<string, object?>>? _systemProbe;
    private string _category;
    private bool _busy;
    private bool _closed;

    /// <param name="screenshot">The main window as it was when "แจ้งปัญหา" was pressed (PNG/JPEG), or null.</param>
    /// <param name="chatText">The open chat as text, read only when the chat is ticked; null = no chat to offer.</param>
    /// <param name="ownerVisual">For the screen's DPI.</param>
    public ReportProblemWindow(BugReportService service, DebugLogService? log, SettingsService? settings, GpuDetectionService? gpu,
        byte[]? screenshot, Func<string?>? chatText, Visual? ownerVisual, string category = "bug", string? message = null)
    {
        InitializeComponent();
        FontFamily = TypographyService.BundledFamily;
        _service = service;
        _log = log;
        _settings = settings;
        _gpu = gpu;
        _chat = chatText;
        _screenshot = screenshot;
        _category = BugReportService.Categories.Contains(category) ? category : "bug";
        _display = DiagnosticsCollector.Display(ownerVisual ?? this);

        foreach (var c in BugReportService.Categories)
        {
            var chip = new RadioButton { Style = (Style)FindResource("CatChip"), GroupName = "cat", Tag = c, Content = L("bug.cat." + c), IsChecked = c == _category };
            chip.Checked += (_, _) => _category = c;
            CategoryPanel.Children.Add(chip);
        }
        if (!string.IsNullOrEmpty(message)) MessageInput.Text = message;
        Message_TextChanged(this, null!);

        var entries = log?.Snapshot() ?? new List<LogEntry>();
        LogCheckText.Text = LF("bug.attach.log", entries.Count);
        ErrorsCheckText.Text = LF("bug.attach.errors", entries.Count(e => e.Level >= LogLevel.Warning));
        if (log == null) { LogCheck.IsChecked = false; LogCheck.IsEnabled = false; ErrorsCheck.IsChecked = false; ErrorsCheck.IsEnabled = false; }
        if (screenshot == null) { ScreenshotCheck.IsChecked = false; ScreenshotCheck.IsEnabled = false; }
        if (chatText == null) { ChatCheck.IsEnabled = false; ChatCheckText.Text = L("bug.attach.chat.none"); }

        string? key = settings?.Settings.ActivationKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            LicenseCheck.Visibility = Visibility.Visible;
            LicenseCheckText.Text = LF("bug.attach.license", Services.Account.ConnectionDoctor.MaskKey(key));
        }

        int queued = service.QueuedCount;
        if (queued > 0) { QueueText.Text = LF("bug.queue.pending", queued); QueueText.Visibility = Visibility.Visible; }

        string? girl = GirlArt.PickName();
        GirlImage.Source = GirlArt.PoseImage(girl, 1);
        if (GirlImage.Source == null) GirlImage.Visibility = Visibility.Collapsed;
        else GirlGlowFx.Color = GirlArt.Glow(girl);

        Root.SizeChanged += (_, _) => Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), 16, 16);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (PreviewPane.Visibility == Visibility.Visible) ShowPane(FormPane);
            else Close();
        };
        Loaded += (_, _) =>
        {
            MessageInput.Focus();
            // the GPU probe can take a few seconds (nvidia-smi): it starts now and is awaited at preview/send
            _systemProbe = Task.Run<IReadOnlyDictionary<string, object?>>(() => DiagnosticsCollector.System(_gpu));
            _ = SendQueuedAsync();
        };
        Closed += (_, _) => { _closed = true; _life.Cancel(); };
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(LocalizedResources.Instance[key], args);

    /// <summary>Earlier reports still waiting go first, quietly; the line under the ticks says what happened.</summary>
    private async Task SendQueuedAsync()
    {
        if (_service.QueuedCount == 0) return;
        try
        {
            var sent = await _service.RetryQueuedAsync(_life.Token);
            if (_closed) return;
            int left = _service.QueuedCount;
            QueueText.Text = sent.Count > 0 ? LF("bug.queue.sentLater", string.Join(", ", sent)) : LF("bug.queue.pending", left);
            QueueText.Visibility = sent.Count > 0 || left > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { _log?.Warn("BugReport", "sending the queued reports failed", ex); }
    }

    // ─── building ────────────────────────────────────────────────────

    private async Task<BugReportDraft> DraftAsync()
    {
        IReadOnlyDictionary<string, object?> system;
        try { system = _systemProbe != null ? await _systemProbe : DiagnosticsCollector.System(null); }
        catch { system = new Dictionary<string, object?>(); }

        bool wantChat = ChatCheck.IsChecked == true;
        string? chat = null;
        if (wantChat) { try { chat = _chat?.Invoke(); } catch { chat = null; } }

        return new BugReportDraft
        {
            ClientReportId = _clientReportId,
            MachineId = XmanLicenseService.GetMachineFingerprint(),
            Category = _category,
            Message = MessageInput.Text,
            ContactEmail = EmailBox.Text,
            AppVersion = AutoUpdateService.CurrentVersion,
            OsVersion = system.TryGetValue("os", out var os) && os is string s ? s : Environment.OSVersion.VersionString,
            LicenseKey = _settings?.Settings.ActivationKey,
            LinkLicense = LicenseCheck.Visibility == Visibility.Visible && LicenseCheck.IsChecked == true,
            IncludeLog = LogCheck.IsChecked == true,
            IncludeSystem = SystemCheck.IsChecked == true,
            IncludeSettings = SettingsCheck.IsChecked == true,
            IncludeErrors = ErrorsCheck.IsChecked == true,
            IncludeScreenshot = ScreenshotCheck.IsChecked == true,
            IncludeChat = wantChat,
            App = DiagnosticsCollector.App(_settings),
            System = system,
            Display = _display,
            Process = DiagnosticsCollector.ProcessInfo(),
            Settings = DiagnosticsCollector.Settings(_settings?.Settings),
            RecentErrors = DiagnosticsCollector.RecentErrors(_log),
            LogText = LogCheck.IsChecked == true ? DiagnosticsCollector.LogTail(_log, BugReportService.MaxLogChars) : null,
            Screenshot = _screenshot,
            ChatText = chat,
        };
    }

    private async Task<JsonObject> PayloadAsync()
    {
        var draft = await DraftAsync();
        var scrub = ScrubContext.ForThisPc(_settings?.DataRoot, _settings?.Settings.AccountName);
        return await Task.Run(() => BugReportService.BuildPayload(draft, scrub));
    }

    private bool Validate()
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(MessageInput.Text)) error = L("bug.need.message");
        else if (EmailBox.Text.Trim() is { Length: > 0 } email && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")) error = L("bug.email.invalid");
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) (string.IsNullOrWhiteSpace(MessageInput.Text) ? (Control)MessageInput : EmailBox).Focus();
        return error == null;
    }

    // ─── the buttons ─────────────────────────────────────────────────

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Validate()) return;
        SetBusy(true);
        try
        {
            var payload = await PayloadAsync();
            if (_closed) return;
            var result = await _service.SubmitAsync(payload, _life.Token);
            if (_closed) return;
            ShowResult(result);
        }
        catch (Exception ex)
        {
            // building the report failed (not the network: SubmitAsync never throws) — say so in the UI's own words
            _log?.Error("BugReport", "building the report failed", ex);
            if (_closed) return;
            ShowResult(new BugReportResult(BugReportOutcome.Rejected, null, "bug.result.server", "CLIENT_ERROR"));
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (PreviewPane.Visibility == Visibility.Visible) { ShowPane(FormPane); return; }
        SetBusy(true, preparing: true);
        try
        {
            var payload = await PayloadAsync();
            if (_closed) return;
            PreviewBox.Text = PreviewText(payload, out long shotBytes);
            PreviewExtra.Text = shotBytes > 0 ? LF("bug.preview.screenshot", shotBytes / 1024) : "";
            PreviewExtra.Visibility = shotBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowPane(PreviewPane);
        }
        catch (Exception ex)
        {
            _log?.Warn("BugReport", "building the preview failed", ex);
            if (!_closed) { ErrorText.Text = L("bug.result.server"); ErrorText.Visibility = Visibility.Visible; }
        }
        finally { if (!_closed) SetBusy(false); }
    }

    /// <summary>The payload as people read it: indented, Thai as Thai, the screenshot as its size.</summary>
    public static string PreviewText(JsonObject payload, out long screenshotBytes)
    {
        var copy = (JsonObject)payload.DeepClone();
        screenshotBytes = 0;
        if (copy["screenshot"]?.GetValue<string>() is { } b64)
        {
            screenshotBytes = b64.Length * 3L / 4;
            copy["screenshot"] = $"<{screenshotBytes / 1024} KB image>";
        }
        string? log = null;
        if (copy["log_excerpt"]?.GetValue<string>() is { } l) { log = l; copy.Remove("log_excerpt"); }
        string json = copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // the log as lines after the rest, not one long escaped string; a very long one shows its last part
        if (log == null) return json;
        const int ShowChars = 60_000;
        string shown = log.Length > ShowChars ? "…\n" + log[^ShowChars..] : log;
        return json + "\n\n── log_excerpt (" + log.Split('\n').Length + " lines) ──\n" + shown;
    }

    private void SetBusy(bool busy, bool preparing = false)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy;
        SendSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SendIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        SendText.Text = busy ? L(preparing ? "bug.preparing" : "bug.sending") : L("bug.send");
    }

    private void ShowPane(FrameworkElement pane)
    {
        FormPane.Visibility = pane == FormPane ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = pane == PreviewPane ? Visibility.Visible : Visibility.Collapsed;
        ResultPane.Visibility = pane == ResultPane ? Visibility.Visible : Visibility.Collapsed;
        FormButtons.Visibility = pane == ResultPane ? Visibility.Collapsed : Visibility.Visible;
        PreviewButton.Content = L(pane == PreviewPane ? "bug.preview.hide" : "bug.preview");
    }

    public void ShowResult(BugReportResult result)
    {
        ShowPane(ResultPane);
        bool sent = result.Outcome == BugReportOutcome.Sent;
        bool queued = result.Outcome == BugReportOutcome.Queued;
        ResultTitle.Text = L(sent ? "bug.done.title" : queued ? "bug.queued.title" : "bug.failed.title");
        ResultText.Text = L(result.MessageKey);
        TicketCard.Visibility = sent && result.Ticket != null ? Visibility.Visible : Visibility.Collapsed;
        TicketText.Text = result.Ticket ?? "";
        BackButton.Visibility = result.Outcome == BugReportOutcome.Rejected ? Visibility.Visible : Visibility.Collapsed;
        var (fill, line, ink, icon) = sent
            ? ("#3322C55E", "#8822C55E", "#86EFAC", "M5,12.5 L10,17.5 L19,7.5")
            : queued
                ? ("#33F59E0B", "#88F59E0B", "#FCD34D", "M12,6 L12,12 L16,14 M12,3 A9,9 0 1 0 12.01,3")
                : ("#33EF4444", "#88EF4444", "#FCA5A5", "M12,7 L12,13 M12,16.5 L12,17 M12,3 A9,9 0 1 0 12.01,3");
        ResultBadge.Background = Brush(fill);
        ResultBadge.BorderBrush = Brush(line);
        ResultIcon.Stroke = Brush(ink);
        ResultIcon.Data = Geometry.Parse(icon);
        (sent || queued ? DoneButton : BackButton).Focus();
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPane(FormPane);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TicketText.Text); CopyButton.Content = L("bug.copied"); }
        catch { /* the clipboard is busy in another app: the number is on screen to read */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Message_TextChanged(object sender, TextChangedEventArgs e)
    {
        MessageHint.Visibility = MessageInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CounterText.Text = $"{MessageInput.Text.Length}/{BugReportService.MaxMessageChars}";
        if (ErrorText.Visibility == Visibility.Visible && MessageInput.Text.Trim().Length > 0) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsControl(d)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private static bool IsControl(DependencyObject d)
    {
        for (var p = d; p != null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    // ─── the screenshot ──────────────────────────────────────────────

    /// <summary>
    /// The window as drawn now, as PNG — or JPEG, then smaller, until it fits the server's cap. Null when it cannot be
    /// drawn (minimised, zero size). UI thread.
    /// </summary>
    public static byte[]? Capture(Window? window)
    {
        try
        {
            if (window == null || window.WindowState == WindowState.Minimized || window.ActualWidth < 10 || window.ActualHeight < 10) return null;
            var dpi = VisualTreeHelper.GetDpi(window);
            var element = (window.Content as FrameworkElement) ?? (FrameworkElement)window;
            int w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
            if (w < 10 || h < 10) return null;
            var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(element);
            bmp.Freeze();

            byte[] png = Encode(new PngBitmapEncoder(), bmp);
            if (png.Length <= BugReportService.MaxScreenshotBytes) return png;
            BitmapSource src = bmp;
            foreach (double scale in new[] { 1.0, 0.75, 0.5, 0.35 })
            {
                BitmapSource s = scale == 1.0 ? src : new TransformedBitmap(src, new ScaleTransform(scale, scale));
                byte[] jpg = Encode(new JpegBitmapEncoder { QualityLevel = 80 }, s);
                if (jpg.Length <= BugReportService.MaxScreenshotBytes) return jpg;
            }
            return null;
        }
        catch { return null; }
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
