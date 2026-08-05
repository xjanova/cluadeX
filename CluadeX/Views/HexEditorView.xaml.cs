using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class HexEditorView : UserControl
{
    public HexEditorView()
    {
        InitializeComponent();
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is HexRow row
            && DataContext is HexEditorViewModel vm)
        {
            vm.SelectedOffset = row.Offset;
            vm.SelectedLength = row.Length;
        }
    }
}
