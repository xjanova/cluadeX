using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class ModelManagerView : UserControl
{
    public ModelManagerView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ModelManagerViewModel vm
            && vm.SearchModelsCommand.CanExecute(null))
        {
            vm.SearchModelsCommand.Execute(null);
            e.Handled = true;
        }
    }
}
