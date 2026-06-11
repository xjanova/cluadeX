using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class CodeEditorView : UserControl
{
    public CodeEditorView()
    {
        InitializeComponent();
    }

    private bool _scrollHooked;
    private System.Windows.Threading.DispatcherTimer? _flashTimer;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            if (!_scrollHooked)
            {
                vm.ScrollToLineRequested += OnScrollToLine;
                vm.AgentEditFlashRequested += OnAgentEditFlash;
                _scrollHooked = true;
            }
            if (vm.Tree.Count == 0) await vm.RefreshTreeAsync();
        }
    }

    // Live-follow: scroll the editor to (and select) the line the agent just changed.
    // Line height is pinned to 20px in XAML (TextBlock.LineHeight + BlockLineHeight) to match the
    // gutter, so the idx*20 offset math is exact at any DPI/font fallback.
    private void OnScrollToLine(int line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                EditorTextBox.UpdateLayout();
                int idx = Math.Max(0, line - 1);
                int lc = EditorTextBox.LineCount;
                if (lc > 0 && idx < lc)
                {
                    int start = EditorTextBox.GetCharacterIndexFromLineIndex(idx);
                    int len = EditorTextBox.GetLineLength(idx);
                    if (start >= 0) EditorTextBox.Select(start, Math.Max(0, len));
                }
                EditorScroll?.ScrollToVerticalOffset(Math.Max(0, idx * 20.0 - 60));
            }
            catch { /* best-effort scroll */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // When a live-typing reveal completes, flash-select the freshly typed region for a moment
    // (IsInactiveSelectionHighlightEnabled keeps it visible while focus stays in the chat panel).
    private void OnAgentEditFlash(int charStart, int charLength)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                EditorTextBox.UpdateLayout();
                int max = EditorTextBox.Text.Length;
                if (charStart < 0 || charStart >= max || charLength <= 0) return;
                EditorTextBox.Select(charStart, Math.Min(charLength, max - charStart));

                _flashTimer?.Stop();
                _flashTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1100),
                };
                _flashTimer.Tick += (_, _) =>
                {
                    _flashTimer?.Stop();
                    _flashTimer = null;
                    try { EditorTextBox.Select(Math.Min(charStart + charLength, EditorTextBox.Text.Length), 0); }
                    catch { }
                };
                _flashTimer.Start();
            }
            catch { /* flash is cosmetic — never disrupt */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FileTreeNode node
            && DataContext is CodeEditorViewModel vm)
        {
            if (vm.ToggleNodeCommand is ICommand cmd && cmd.CanExecute(node))
                cmd.Execute(node);
            await Task.Yield();
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.ActiveTab = tab;
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }
}
