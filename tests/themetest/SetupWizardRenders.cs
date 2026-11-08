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
        }
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
