using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

/// <summary>
/// How many card columns fit: values[0] = the panel's width, values[1] (optional) = grid view on (false → 1 column).
/// Parameter "minWidth" or "minWidth,maxColumns". Keeps cards readable in a narrow window instead of squeezing two
/// columns into it (the old fixed UniformGrid Columns="2" clipped every badge row below ~900 px).
/// </summary>
public sealed class AdaptiveColumnsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double width = values.Length > 0 && values[0] is double w && !double.IsNaN(w) ? w : 0;
        if (values.Length > 1 && values[1] is bool grid && !grid) return 1;
        string[] p = (parameter as string ?? "420").Split(',');
        double min = double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 420;
        int max = p.Length > 1 && int.TryParse(p[1], out var mx) ? mx : 4;
        if (width <= 0) return 1;
        return Math.Clamp((int)(width / min), 1, max);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string / non-zero number / true → Visible. "Invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d > 0.0001,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "Invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
