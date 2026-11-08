using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.ViewModels.Setup;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CluadeX.Views.Setup;

/// <summary>
/// "เข้าสู่ระบบในหน้าต่างนี้" (installer contract 2.3): the device page (verification_uri_complete) in a WebView2 window,
/// so a user without a phone can approve the code here — LINE login works inside it. Google refuses embedded sign-in,
/// so the footer says so and offers the system browser. The wizard keeps polling the whole time; whichever approves
/// first wins, and the wizard closes this window when the code is approved.
/// </summary>
public sealed class WebSignInWindow : Window
{
    private readonly string _url;
    private readonly string _dataRoot;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xA9, 0xDD)), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 12, 18, 0) };

    public WebSignInWindow(string url, SetupWizardViewModel vm)
    {
        _url = url;
        _dataRoot = vm.DataRoot;
        var loc = LocalizedResources.Instance;
        Title = loc["wiz.web.title"];
        Width = 520; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x0A, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xFF));
        FontFamily = TypographyService.BundledFamily;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/CluadeX;component/Assets/app.ico"));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status.Text = loc["wiz.web.loading"];
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        _web.Margin = new Thickness(0, 10, 0, 0);
        grid.Children.Add(_web);

        var footer = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 12, 18, 14), Foreground = _status.Foreground };
        footer.Inlines.Add(new Run(loc["wiz.web.google"] + " "));
        var link = new Hyperlink(new Run(loc["wiz.web.browser"])) { Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xB4, 0xFE)) };
        link.Click += (_, _) => OpenInBrowser(_url);
        footer.Inlines.Add(link);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        Content = grid;

        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { try { _web.Dispose(); } catch { } };
    }

    private async Task StartAsync()
    {
        try
        {
            string folder = Path.Combine(_dataRoot, "webview-signin");
            var env = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // A sign-in popup (LINE / Google) opens in this same window instead of a bare WebView2 popup.
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (IsHttps(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationStarting += (_, e) => { if (!IsHttps(e.Uri)) e.Cancel = true; };
            core.NavigationCompleted += (_, e) => _status.Text = e.IsSuccess ? "" : LocalizedResources.Instance["wiz.web.failed"];
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            // No WebView2 runtime (or it would not start): say so; the phone and the browser still work.
            Debug.WriteLine("web sign-in: " + ex.Message);
            _status.Text = LocalizedResources.Instance["wiz.web.noRuntime"];
            _web.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsHttps(string? uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == "about");

    private static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
