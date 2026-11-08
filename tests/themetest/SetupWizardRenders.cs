using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.Services.Account;
using CluadeX.ViewModels.Setup;
using CluadeX.Views.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool SetupWizardOnly;

    /// <summary>
    /// The first-run setup wizard, every page and state, rendered for the eye (installer contract 2.x; design boards Main,
    /// License, LicenseSignIn, Components, InstallProgress, InstallDone) — in Thai, then once in English. No network: the
    /// license and account clients refuse every request, and each state is set through the view model's Preview* hooks.
    /// Also checks what a picture cannot: no text is clipped by its own box, nothing sits outside the 960x600 window.
    ///
    ///   dotnet run -- --setup-wizard --out D:\...\renders\cluadex
    /// </summary>
    static async Task<int> SetupWizardShots()
    {
        Log("\n## Setup wizard (rendered for the eye)");
        var license = Sp.GetRequiredService<XmanLicenseService>();
        var account = Sp.GetRequiredService<XmanAccountService>();
        SetField(license, "_httpClient", Blocked());
        SetField(account, "_httpClient", Blocked());
        Check("wizard: license and account clients refuse every request",
            GetField(license, "_httpClient") is System.Net.Http.HttpMessageInvoker a && IsBlocking(a)
            && GetField(account, "_httpClient") is System.Net.Http.HttpMessageInvoker b && IsBlocking(b));
        var loc = Sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        SetupWizardWindow.Animations = false;

        var ready = new AppAuthHealth(AppAuthAvailability.Ready, true, true, true, DateTime.UtcNow, 200, null, null);
        var notDeployed = new AppAuthHealth(AppAuthAvailability.NotDeployed, false, false, false, DateTime.UtcNow, 404, "NOT_DEPLOYED", null);
        var offline = new AppAuthHealth(AppAuthAvailability.Unreachable, false, false, false, null, 0, "OFFLINE", null);

        foreach (string lang in new[] { "th", "en" })
        {
            loc.SetLanguage(lang);
            Settings.UpdateSettings(s => s.Language = lang);
            string dir = Path.Combine(OutRoot, lang);
            Log($"\n### {lang}");

            await Shot(dir, "01-welcome", vm => { });
            await Shot(dir, "02-license-signin", vm => vm.PreviewPage(WizardPage.License, ready));
            await Shot(dir, "03-license-not-deployed", vm => vm.PreviewPage(WizardPage.License, notDeployed), vm =>
                Check($"wizard[{lang}]: not deployed → the account form is hidden, the quick path is picked", !vm.ShowAccountForm && vm.UseQuickPath && vm.ServerNotDeployed));
            await Shot(dir, "04-license-offline", vm => vm.PreviewPage(WizardPage.License, offline));
            await Shot(dir, "05-license-wrong-password", vm => { vm.PreviewPage(WizardPage.License, ready); vm.Login = "buyer@example.com"; vm.PreviewLicenseError("INVALID_CREDENTIALS"); });
            await Shot(dir, "06-license-key", vm => { vm.PreviewPage(WizardPage.License, notDeployed); vm.PreviewKeyBox("ABCD-1234", "INVALID_LICENSE"); });
            await Shot(dir, "07-qr-line", vm => { vm.PreviewPage(WizardPage.License, ready); vm.PreviewQr("KQ7M-4TZP", "https://xman4289.com/device?code=KQ7M-4TZP", TimeSpan.FromSeconds(292)); });
            await Shot(dir, "08-qr-expired-google", vm => { vm.PreviewPage(WizardPage.License, ready); vm.QrGoogle = true; vm.PreviewQr("KQ7M-4TZP", "", TimeSpan.Zero, expired: true); });
            await Shot(dir, "09-components-nvidia", vm => vm.PreviewComponents(true, "NVIDIA GeForce GTX 1070 Ti", 440_401_920, brainXInstalled: false));
            await Shot(dir, "10-components-no-nvidia", vm => vm.PreviewComponents(false, "Intel(R) UHD Graphics 770", 0, brainXInstalled: true), vm =>
                Check($"wizard[{lang}]: no NVIDIA card → its switch is off and cannot be turned on", !vm.NvidiaOn && !vm.NvidiaSelectable));
            await Shot(dir, "11-progress", vm => vm.PreviewProgress(new[]
            {
                ("program", StepState.Done, AutoUpdateService.CurrentVersion, -1.0),
                ("license", StepState.Done, loc.T("wiz.prog.lic.tier", "Pro"), -1.0),
                ("autoupdate", StepState.Done, loc.T("wiz.prog.au.on"), -1.0),
                ("nvidia", StepState.Running, loc.T("wiz.prog.mb", 182, 420), 0.43),
                ("brainx", StepState.Pending, "", -1.0),
                ("shortcut", StepState.Pending, "", -1.0),
            }, finished: false));
            await Shot(dir, "12-progress-failed", vm => vm.PreviewProgress(new[]
            {
                ("program", StepState.Done, AutoUpdateService.CurrentVersion, -1.0),
                ("license", StepState.Done, loc.T("wiz.prog.lic.free"), -1.0),
                ("autoupdate", StepState.Skipped, loc.T("wiz.prog.au.off"), -1.0),
                ("nvidia", StepState.Failed, loc.T("llamacpp.err.network"), -1.0),
                ("brainx", StepState.Skipped, loc.T("wiz.prog.bx.comingSoon"), -1.0),
                ("shortcut", StepState.Done, loc.T("wiz.prog.sc.done"), -1.0),
            }, finished: true), vm => Check($"wizard[{lang}]: a failed step keeps the page with the doctor and the report", vm.InstallNeedsAttention));
            await Shot(dir, "13-doctor", vm => { vm.PreviewPage(WizardPage.License, offline); vm.PreviewDoctor(FakeReport(loc)); });
            await Shot(dir, "14-done", vm => vm.PreviewPage(WizardPage.Done));

            // the update screens (design board UpdateSplash)
            const string notes = "## What's new\n- **Setup wizard** on the first run: sign in, pick the parts, watch them install (#2701)\n"
                + "- Sign in with your Xman Studio account — a linked account updates by itself\n"
                + "- Connection doctor: tells you *why* the server cannot be reached a1b2c3d\n  - nested detail line\n"
                + "- The license button sits right after the CluadeX name\n- A fifth line that does not fit\n";
            await UpdateReadyShot(dir, "15-update-ready", notes, allowed: true, lang);
            await UpdateReadyShot(dir, "16-update-ready-link", null, allowed: false, lang);
            await SplashShot(dir, "17-update-splash-40", new UpdateProgressInfo(UpdatePhase.Downloading, "3.0.280", 40, 2_642_411, 6_606_029, 1_153_434, false, null)
                { Seq = 5, Delta = true, DeltaCount = 2 });
            await SplashShot(dir, "18-update-splash-stalled", new UpdateProgressInfo(UpdatePhase.Downloading, "3.0.280", 62, 4_095_738, 6_606_029, 0, false, null)
                { Seq = 6, StalledSeconds = 14 });
            await SplashShot(dir, "19-update-splash-preparing", new UpdateProgressInfo(UpdatePhase.Preparing, "3.0.280", 100, 6_606_029, 6_606_029, 0, false, null)
                { Seq = 7, PhaseSeconds = 3 });
        }
        var bullets = AutoUpdateService.ReleaseNotesBullets("## Head\n- **One** (#12)\n- [Two](https://x) a1b2c3d\n  - nested\n- `Three`\n- Four\n- Five", 4);
        Check("update notes: list items cleaned, nested skipped, at most four", string.Join("|", bullets) == "One|Two|Three|Four", string.Join("|", bullets));
        Check("update notes: plain text when there is no list", AutoUpdateService.ReleaseNotesBullets("Bug fixes.\n\nFaster start.").Count == 2);
        Check("update notes: nothing readable → empty", AutoUpdateService.ReleaseNotesBullets("## v3.0.280\n---").Count == 0 && AutoUpdateService.ReleaseNotesBullets(null).Count == 0);
        Check("update notes: *emphasis* and _emphasis_ lose their marks, snake_case keeps its",
            AutoUpdateService.ReleaseNotesBullets("- tells you *why* and _how_ in my_file_name").Single() == "tells you why and how in my_file_name",
            AutoUpdateService.ReleaseNotesBullets("- tells you *why* and _how_ in my_file_name").Single());
        Check("update notes: a word made of hex letters is not a commit hash", AutoUpdateService.ReleaseNotesBullets("- Fixed the defaced").Single() == "Fixed the defaced");
        await InstallerSplash(Path.Combine(OutRoot, "installer", "setup-splash.png"));
        loc.SetLanguage("en");
        return Finish();
    }

    static DoctorReport FakeReport(LocalizationService loc)
    {
        var steps = new List<DoctorStep>
        {
            new("network", DoctorStatus.Ok, null, ""),
            new("proxy", DoctorStatus.Ok, null, "none"),
            new("dns", DoctorStatus.Ok, null, "xman4289.com -> 104.21.5.6 (12 ms)"),
            new("tcp", DoctorStatus.Ok, null, "104.21.5.6:443 (31 ms)"),
            new("tls", DoctorStatus.Fail, loc.T("acct.doc.tls.interceptor", "Kaspersky"), "RemoteCertificateChainErrors; issuer \"CN=Kaspersky Anti-Virus Personal Root\""),
            new("api", DoctorStatus.Skip, null, ""),
            new("clock", DoctorStatus.Skip, null, ""),
            new("updates", DoctorStatus.Skip, null, ""),
        };
        return new DoctorReport("CX-20261108-7F3A", DateTimeOffset.Now, steps, loc.T("acct.doc.tls.interceptor", "Kaspersky"), "CluadeX connection report");
    }

    /// <summary>One wizard window in one state → PNG, plus the layout checks.</summary>
    static async Task Shot(string dir, string name, Action<SetupWizardViewModel> arrange, Action<SetupWizardViewModel> check = null)
    {
        var win = new SetupWizardWindow(Sp, WizardMode.FirstRun)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            win.Show();
            await Idle(150);
            arrange(win.ViewModel);
            await Idle(250);
            var bmp = await Stable(() => (RootVisual(win), 960, 600), "setup/" + name);
            string path = Path.Combine(dir, name + ".png");
            SavePng(bmp, path);
            Log($"  {name}: {path} · girl {win.ViewModel.GirlName}");
            check?.Invoke(win.ViewModel);
            CheckWizardLayout(win, name);
        }
        finally { win.Close(); await Idle(); }
    }

    /// <summary>"A new version is ready" → PNG, plus the layout checks and the switch's rule.</summary>
    static async Task UpdateReadyShot(string dir, string name, string notes, bool allowed, string lang)
    {
        var win = new UpdateReadyWindow("3.0.271", "3.0.280", notes, 6_606_029, allowed, null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            win.Show();
            await Idle(300);
            var bmp = await Stable(() => (RootVisual(win), 960, 600), "update/" + name);
            string path = Path.Combine(dir, name + ".png");
            SavePng(bmp, path);
            Log($"  {name}: {path}");
            var sw = (System.Windows.Controls.Primitives.ToggleButton)win.FindName("AutoSwitch");
            var link = (FrameworkElement)win.FindName("LinkRow");
            var list = (ItemsControl)win.FindName("Notes");
            Check($"update[{lang}] {name}: the switch follows the entitlement", sw.IsEnabled == allowed && (sw.IsChecked == true) == allowed && link.IsVisible == !allowed);
            Check($"update[{lang}] {name}: what's new shows {(notes == null ? "the generic line" : "four lines")}", list.Items.Count == (notes == null ? 1 : 4), $"{list.Items.Count}");
            CheckWizardLayout(win, name);
        }
        finally { win.Close(); await Idle(); }
    }

    /// <summary>The splash in update mode at one progress snapshot → PNG, plus the layout checks.</summary>
    static async Task SplashShot(string dir, string name, UpdateProgressInfo p)
    {
        CluadeX.Views.SplashWindow.PreviewOnly = true;
        var win = new CluadeX.Views.SplashWindow(Sp)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            win.Show();
            await Idle(200);
            win.PreviewUpdate(p);
            await Idle(300);
            var bmp = await Stable(() => (RootVisual(win), (int)win.Width, (int)win.Height), "splash/" + name);
            string path = Path.Combine(dir, name + ".png");
            SavePng(bmp, path);
            Log($"  {name}: {path}");
            var mode = (FrameworkElement)win.FindName("UpdateMode");
            Check($"splash {name}: the update view shows", mode?.IsVisible == true);
            CheckWizardLayout(win, name);
        }
        finally { win.Close(); CluadeX.Views.SplashWindow.PreviewOnly = false; await Idle(); }
    }

    /// <summary>
    /// The picture Setup.exe shows while it installs (vpk pack --splashImage; the release copies it from
    /// CluadeX/Assets/Installer/setup-splash.png): the purple card, the wordmark, the CluadeX girl. Opaque — the
    /// installer draws it as is — and in both languages, because it shows before a language is picked.
    /// </summary>
    static async Task InstallerSplash(string path)
    {
        const int w = 640, h = 400;
        var font = TypographyService.BundledFamily;
        BitmapImage Pack(string rel)
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri("pack://application:,,,/CluadeX;component/" + rel, UriKind.Absolute);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            return b;
        }
        var root = new Grid { Width = w, Height = h, ClipToBounds = true };
        root.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x1B, 0x0F, 0x2E), 0), new GradientStop(Color.FromRgb(0x2A, 0x16, 0x48), 0.55), new GradientStop(Color.FromRgb(0x3A, 0x1F, 0x5E), 1),
        }, new Point(0, 0), new Point(1, 1));
        root.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromArgb(0x66, 0x9B, 0x6B, 0xFF), Color.FromArgb(0, 0, 0, 0))
            { Center = new Point(0.78, 0.7), GradientOrigin = new Point(0.78, 0.7), RadiusX = 0.42, RadiusY = 0.6 },
        });
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x9B, 0x6B, 0xFF)), BorderThickness = new Thickness(1) });
        var girl = new Image
        {
            Source = GirlArt.PoseImage("cluadex", 1), Height = 430, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 18, 0), Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = GirlArt.Glow("cluadex"), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 },
        };
        RenderOptions.SetBitmapScalingMode(girl, BitmapScalingMode.HighQuality);
        root.Children.Add(girl);
        var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(44, 0, 0, 10), Width = 330 };
        var mascot = new Image { Source = Pack("Assets/mascot.png"), Width = 120, Height = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var mark = new Image { Source = Pack("Assets/logo-wordmark.png"), Width = 250, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        RenderOptions.SetBitmapScalingMode(mascot, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        left.Children.Add(mascot);
        left.Children.Add(mark);
        TextBlock T(string text, double size, Color c, FontWeight wgt, double top) => new()
        {
            Text = text, FontFamily = font, FontSize = size, Foreground = new SolidColorBrush(c), FontWeight = wgt, Margin = new Thickness(2, top, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        left.Children.Add(T("กำลังติดตั้ง CluadeX…", 19, Color.FromRgb(0xF4, 0xEE, 0xFF), FontWeights.SemiBold, 18));
        left.Children.Add(T("Installing CluadeX…", 13, Color.FromRgb(0xBB, 0xA9, 0xDD), FontWeights.Normal, 2));
        left.Children.Add(T("Xman Studio", 11, Color.FromRgb(0x9F, 0x8C, 0xC6), FontWeights.Normal, 26));
        root.Children.Add(left);

        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        await Idle(100);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        SavePng(rtb, path);
        Log($"  installer splash: {path}");
        Check("installer splash: the girl and the wordmark are there", girl.Source != null && mark.Source != null);
    }

    /// <summary>Every visible TextBlock fits its own box (no cut-off Thai), and nothing shown leaves the window.</summary>
    static void CheckWizardLayout(Window win, string name)
    {
        var root = (FrameworkElement)RootVisual(win);
        var clipped = new List<string>();
        var outside = new List<string>();
        foreach (var tb in Find<TextBlock>(root))
        {
            if (!tb.IsVisible || string.IsNullOrWhiteSpace(tb.Text) || tb.ActualWidth <= 0) continue;
            // a TextBlock trimmed with an ellipsis is allowed (a path); one that wraps must not be cut vertically
            // DesiredSize includes the margin; ActualHeight does not
            var m = tb.Margin;
            tb.Measure(new Size(tb.ActualWidth + m.Left + m.Right + 1, double.PositiveInfinity));   // +1: layout rounding
            double need = tb.DesiredSize.Height - m.Top - m.Bottom;
            if (tb.TextTrimming == TextTrimming.None && need > tb.ActualHeight + 1.5)
                clipped.Add($"'{Short(tb.Text)}' {need:0}>{tb.ActualHeight:0}");
            try
            {
                var p = tb.TransformToAncestor(root).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                if (p.Right > 961 || p.Bottom > 601 || p.Left < -1 || p.Top < -1) outside.Add($"'{Short(tb.Text)}' {p.Left:0},{p.Top:0}-{p.Right:0},{p.Bottom:0}");
            }
            catch (InvalidOperationException) { }
        }
        Check($"wizard {name}: no text cut off by its box", clipped.Count == 0, string.Join(" | ", clipped.Take(4)));
        Check($"wizard {name}: nothing outside the window", outside.Count == 0, string.Join(" | ", outside.Take(4)));
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
