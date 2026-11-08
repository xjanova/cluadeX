using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CluadeX.Services;
using CluadeX.ViewModels.Setup;

namespace CluadeX.Views.Setup;

/// <summary>
/// The borderless 960x600 setup wizard. All state lives in <see cref="SetupWizardViewModel"/>; this file only does what
/// a view must: the title bar (drag, minimise, close), the password box, Enter/Escape, the girl's glow and float, and
/// the in-app sign-in window. Closing it at any point lets the app start (the view model records that the wizard ran).
/// </summary>
public partial class SetupWizardWindow : Window
{
    /// <summary>The test renders switch the girl's float and slide-in off so a frame is the same every time.</summary>
    public static bool Animations { get; set; } = true;

    public SetupWizardViewModel ViewModel { get; }
    private WebSignInWindow? _web;

    public SetupWizardWindow(IServiceProvider sp, WizardMode mode)
    {
        InitializeComponent();
        FontFamily = TypographyService.BundledFamily;
        ViewModel = new SetupWizardViewModel(sp, mode);
        DataContext = ViewModel;

        ViewModel.CloseRequested += () => { if (IsLoaded) Close(); };
        ViewModel.PasswordCleared += () => PasswordBox.Clear();
        ViewModel.OpenWebSignIn += OpenWeb;
        ViewModel.CloseWebSignIn += CloseWeb;
        ViewModel.PropertyChanged += OnVmChanged;

        Root.SizeChanged += (_, _) => Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), 16, 16);
        Loaded += (_, _) => { ApplyGlow(); StartFloat(); };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetupWizardViewModel.Girl):
                ApplyGlow();
                SlideGirlIn();
                break;
            case nameof(SetupWizardViewModel.ShowKeyBox) when ViewModel.ShowKeyBox:
                Dispatcher.BeginInvoke(() => KeyBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
            case nameof(SetupWizardViewModel.ShowAccountForm) when ViewModel.ShowAccountForm && ViewModel.IsLicense:
                Dispatcher.BeginInvoke(() => { if (LoginBox.IsVisible && string.IsNullOrEmpty(LoginBox.Text)) LoginBox.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
                break;
        }
    }

    private void ApplyGlow() => GirlGlowFx.Color = ViewModel.GirlGlow;

    private void StartFloat()
    {
        if (!Animations || !SystemParameters.ClientAreaAnimation) return;
        var a = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        GirlFloat.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void SlideGirlIn()
    {
        if (!Animations || !SystemParameters.ClientAreaAnimation || !IsLoaded) return;
        GirlFloat.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase() });
        GirlImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
    }

    // ── keyboard: Enter does the page's main action, Escape closes an open dialog ──
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ViewModel.ShowDoctor) Exec(ViewModel.CloseDoctorCommand);
            else if (ViewModel.ShowKeyBox) Exec(ViewModel.CloseKeyBoxCommand);
            else if (ViewModel.ShowQr) Exec(ViewModel.CloseQrCommand);
            else return;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.FocusedElement is Button) return;
        if (ViewModel.ShowDoctor || ViewModel.ShowQr) return;
        if (ViewModel.ShowKeyBox) Exec(ViewModel.ActivateKeyCommand);
        else if (ViewModel.IsWelcome) Exec(ViewModel.StartCommand);
        else if (ViewModel.IsLicense) Exec(ViewModel.LicenseNextCommand);
        else if (ViewModel.IsComponents) Exec(ViewModel.InstallCommand);
        else return;
        e.Handled = true;
    }

    private static void Exec(ICommand c) { if (c.CanExecute(null)) c.Execute(null); }

    // ── title bar ──
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) { try { DragMove(); } catch (InvalidOperationException) { } }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CardAccount_Click(object sender, MouseButtonEventArgs e) => Exec(ViewModel.ChooseAccountCommand);
    private void CardQuick_Click(object sender, MouseButtonEventArgs e) => Exec(ViewModel.ChooseQuickCommand);

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.SetPassword(PasswordBox.Password);

    // ── the in-app sign-in (WebView2) ──
    private void OpenWeb(string url)
    {
        if (_web is { IsVisible: true }) { _web.Activate(); return; }
        _web = new WebSignInWindow(url, ViewModel) { Owner = this };
        _web.Closed += (_, _) => _web = null;
        _web.Show();
    }

    private void CloseWeb() { try { _web?.Close(); } catch { } _web = null; }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CloseWeb();
        ViewModel.PropertyChanged -= OnVmChanged;
        ViewModel.Dispose();
    }
}
