using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CluadeX.Services;
using CluadeX.ViewModels.Setup;

namespace CluadeX.Views.Setup;

/// <summary>
/// The borderless 960x600 uninstall page. State lives in <see cref="UninstallViewModel"/>; this file does what a view
/// must: the title bar, Enter/Escape, the girl's glow and float, and refusing to close while files are being deleted
/// (a half-run uninstall is worse than either outcome).
/// </summary>
public partial class UninstallWindow : Window
{
    public UninstallViewModel ViewModel { get; }

    public UninstallWindow(UninstallViewModel vm)
    {
        InitializeComponent();
        FontFamily = TypographyService.BundledFamily;
        ViewModel = vm;
        DataContext = vm;

        vm.CloseRequested += () => { if (IsLoaded) Close(); };
        vm.PropertyChanged += OnVmChanged;
        Root.SizeChanged += (_, _) => Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), 16, 16);
        Loaded += (_, _) => { ApplyGlow(); StartFloat(); };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UninstallViewModel.Girl)) { ApplyGlow(); SlideGirlIn(); }
        else if (e.PropertyName == nameof(UninstallViewModel.ShowConfirm) && ViewModel.ShowConfirm)
            // the safe answer has the focus: Enter on the dialog never deletes by accident
            Dispatcher.BeginInvoke(() => ConfirmNo.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ApplyGlow() => GirlGlowFx.Color = ViewModel.GirlGlow;

    private void StartFloat()
    {
        if (!SetupWizardWindow.Animations || !SystemParameters.ClientAreaAnimation) return;
        var a = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        GirlFloat.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void SlideGirlIn()
    {
        if (!SetupWizardWindow.Animations || !SystemParameters.ClientAreaAnimation || !IsLoaded) return;
        GirlFloat.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase() });
        GirlImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ViewModel.ShowConfirm) Exec(ViewModel.BackToChooseCommand);
            else if (ViewModel.IsChoose) Exec(ViewModel.CancelCommand);
            else return;
            e.Handled = true;
        }
    }

    private static void Exec(ICommand c) { if (c.CanExecute(null)) c.Execute(null); }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) { try { DragMove(); } catch (InvalidOperationException) { } }
    }
    /// <summary>A click anywhere on a tile ticks it (the box itself handles its own click).</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.CheckBox>(d) != null) return;
        if (sender is FrameworkElement { DataContext: KeepRowVm row }) row.Keep = !row.Keep;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        // a Run (a ContentElement) has no visual parent: walk the logical tree there
        for (var cur = d; cur != null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // the X on the done page is the same as its Close button: the program is removed after it
        if (ViewModel.IsDone) Exec(ViewModel.CloseCommand);
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.Running) { e.Cancel = true; return; }
        ViewModel.PropertyChanged -= OnVmChanged;
    }
}
