using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The open project's files as a tree: double-click opens a file in the code editor.</summary>
public partial class ProjectFilesPanel : UserControl
{
    public ProjectFilesPanel() => InitializeComponent();

    private void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ChatViewModel vm && Tree.SelectedItem is FileNode { IsDirectory: false } node)
        {
            vm.RequestOpenFile(node.FullPath);
            e.Handled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm) vm.RefreshProjectTree(force: true);
    }
}
