using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CluadeX.Views.UiStudio;

/// <summary>A page as the gallery saw it: a JPEG, where it ended up, its title and HTTP status.</summary>
public sealed record PageShot(byte[] Jpeg, string FinalUrl, string? Title, int Status, int PixelWidth, int PixelHeight);

/// <summary>
/// Takes thumbnails of web pages with a second, hidden browser — the page the user is editing is never touched.
/// The browser lives in a borderless window parked far off-screen (rendered, never seen, not in Alt+Tab) and shares
/// the Studio's browser profile, so pages behind the user's login show as the user sees them.
///
/// It only LOOKS: downloads are cancelled, new windows, pop-ups, dialogs, print, external app links and permission
/// prompts (camera, location…) are refused, sound is muted. Each page is shot once it has settled (loaded, fonts
/// ready, no DOM changes for a moment — capped so an endless animation can't stall the queue).
/// </summary>
public sealed class PageShooter : IDisposable
{
    private readonly CoreWebView2Environment _env;
    private Window? _host;
    private WebView2? _web;
    private Task? _ready;
    private bool _disposed;

    /// <summary>Downloads the shooter refused (tests read it).</summary>
    public int BlockedDownloads { get; private set; }
    public int BlockedWindows { get; private set; }

    public PageShooter(CoreWebView2Environment env) => _env = env;

    private const string Guard = "(() => { try { window.print = function () {}; window.open = function () { return null; }; } catch (e) {} })();";

    private Task EnsureAsync()
    {
        return _ready ??= Create();

        async Task Create()
        {
            _host = new Window
            {
                Title = "CluadeX page shooter",
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Left = -32000, Top = -32000, Width = 1280, Height = 800, Focusable = false,
            };
            if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, _host)) { try { _host.Owner = owner; } catch { } }
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            _host.Content = _web;
            _host.Show();
            await _web.EnsureCoreWebView2Async(_env);
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            core.IsMuted = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Guard);
            core.NewWindowRequested += (_, e) => { e.Handled = true; BlockedWindows++; };
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; BlockedDownloads++; };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.ScriptDialogOpening += (_, _) => { /* not accepted = dismissed */ };
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "about" or "data")) e.Cancel = true;
            };
        }
    }

    /// <summary>Open <paramref name="url"/> at the given CSS size, wait for it to settle, and take a JPEG at most
    /// <paramref name="maxWidth"/> pixels wide. Null when it failed, was cancelled or timed out.</summary>
    public async Task<PageShot?> ShootAsync(string url, int width, int height, CancellationToken ct, int maxWidth = 960)
    {
        if (_disposed) return null;
        await EnsureAsync();
        if (_web?.CoreWebView2 is not { } core || _host == null) return null;
        if (Math.Abs(_host.Width - width) > 0.5 || Math.Abs(_host.Height - height) > 0.5)
        {
            _host.Width = width;
            _host.Height = height;
            await Task.Delay(60, ct).ConfigureAwait(true);
        }

        var done = new TaskCompletionSource<(bool Ok, int Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            int status = 0;
            try { status = e.HttpStatusCode; } catch { }
            done.TrySetResult((e.IsSuccess || status is >= 200 and < 600, status));
        }
        core.NavigationCompleted += OnDone;
        try
        {
            try { core.Navigate(url); }
            catch (ArgumentException) { return null; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var finished = await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(true);
            if (finished != done.Task) { try { core.Stop(); } catch { } return null; }
            var (ok, status) = done.Task.Result;
            if (!ok) return null;

            await SettleAsync(core, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            string? title = null;
            try { title = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.title")); } catch { }
            using var png = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
            png.Position = 0;
            var (jpeg, pw, ph) = ToJpeg(png, maxWidth);
            return new PageShot(jpeg, core.Source, string.IsNullOrWhiteSpace(title) ? null : title.Trim(), status, pw, ph);
        }
        catch (OperationCanceledException) { try { core.Stop(); } catch { } return null; }
        catch (Exception) { return null; }
        finally { core.NavigationCompleted -= OnDone; }
    }

    /// <summary>Wait until the page is still: fonts loaded, no DOM changes for 400 ms, two frames drawn (6 s cap).</summary>
    private static async Task SettleAsync(CoreWebView2 core, CancellationToken ct)
    {
        const string js = @"new Promise(done => {
  let quiet; const finish = () => { try { obs.disconnect(); } catch (e) {} requestAnimationFrame(() => requestAnimationFrame(() => done(true))); };
  const obs = new MutationObserver(() => { clearTimeout(quiet); quiet = setTimeout(finish, 400); });
  try { obs.observe(document, { subtree: true, childList: true, attributes: true, characterData: true }); } catch (e) {}
  const start = () => { quiet = setTimeout(finish, 400); };
  (document.fonts && document.fonts.ready ? document.fonts.ready : Promise.resolve()).then(start, start);
  setTimeout(finish, 5000);
  try { const s = document.createElement('style'); s.textContent = '::-webkit-scrollbar{display:none}'; document.documentElement.appendChild(s); } catch (e) {}
})";
        try
        {
            var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = js, awaitPromise = true, returnByValue = true }));
            await Task.WhenAny(call, Task.Delay(6500, ct)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch { await Task.Delay(800, ct).ConfigureAwait(true); }
    }

    /// <summary>PNG → JPEG, scaled down to <paramref name="maxWidth"/>.</summary>
    internal static (byte[] Jpeg, int Width, int Height) ToJpeg(Stream png, int maxWidth)
    {
        var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource src = frame;
        if (frame.PixelWidth > maxWidth)
        {
            double k = (double)maxWidth / frame.PixelWidth;
            src = new TransformedBitmap(frame, new ScaleTransform(k, k));
        }
        var enc = new JpegBitmapEncoder { QualityLevel = 86 };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return (ms.ToArray(), src.PixelWidth, src.PixelHeight);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); } catch { }
        _web = null;
        _host = null;
    }
}
