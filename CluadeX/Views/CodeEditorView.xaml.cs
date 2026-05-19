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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm && vm.Tree.Count == 0)
        {
            await vm.RefreshTreeAsync();
        }
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
