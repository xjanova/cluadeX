using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;

namespace CluadeX.Views;

/// <summary>The NVIDIA DGX Spark window — see <see cref="DgxSparkViewModel"/>.</summary>
public partial class DgxSparkWindow : Window
{
    public DgxSparkWindow(DgxSparkViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(vm.HostInput)) HostBox.Focus(); };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A model download onto the Spark is many GB; closing the window cancels it. While one runs, ask first —
    /// the safe answer (keep downloading) is the default, and Esc / the dialog's close box. A "stop" answer covers
    /// the downloads running at that moment only (a "stop" given when the main window closed is not asked again when
    /// this owned window closes with it, but a download started afterwards is); see
    /// <see cref="DgxSparkViewModel.CloseQuestion"/>.
    /// </summary>
    public bool ConfirmClose()
    {
        if (DataContext is not DgxSparkViewModel vm || vm.CloseQuestion() is not { } q) return true;
        int answer = ChoiceDialog.Show(q.Title, q.Body, new[] { q.Stop, q.Keep }, defaultIndex: 1);
        if (answer != 0) return false;
        vm.AgreeToStopRunningDownloads();
        return true;
    }

    /// <summary>The close a "stop" answer was given for did not happen (the update failed or was cancelled): the next
    /// close asks again.</summary>
    public void ResetCloseConfirmation() => (DataContext as DgxSparkViewModel)?.ResetCloseConfirmation();

    /// <summary>The app is restarting for an update now: closing must not ask (Application.Shutdown ignores Cancel).</summary>
    public void SetClosingForRestart(bool restarting) => (DataContext as DgxSparkViewModel)?.SetClosingForRestart(restarting);

    /// <summary>X, the footer's Close (also Esc, via IsCancel) and the owner closing all come through here.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as DgxSparkViewModel)?.Detach();
        base.OnClosed(e);
    }
}
