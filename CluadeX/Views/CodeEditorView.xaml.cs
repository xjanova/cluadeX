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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            if (!_scrollHooked) { vm.ScrollToLineRequested += OnScrollToLine; _scrollHooked = true; }
            if (vm.Tree.Count == 0) await vm.RefreshTreeAsync();
        }
    }

    // Live-follow: scroll the editor to (and select) the line the agent just changed.
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
