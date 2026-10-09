using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CluadeX.Services;
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

/// <summary>
/// Catalog category key ("Coding" / "General" / "Reasoning") → its label in the UI language. The cards are
/// rebuilt when the language changes, so a one-shot lookup is enough.
/// </summary>
public sealed class ModelCategoryLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value as string ?? "";
        string label = LocalizedResources.Instance["modelsm.cat" + key];
        return label.StartsWith("modelsm.", StringComparison.Ordinal) ? key : label;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
