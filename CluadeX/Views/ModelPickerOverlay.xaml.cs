using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.ViewModels.ModelPicker;

namespace CluadeX.Views;

/// <summary>The full-screen model picker (DataContext: <see cref="ModelPickerViewModel"/>). The view only lays the cards
/// out (how many per row, from its width), keeps the keyboard, and scrolls the card under the cursor into sight;
/// everything else is the view model's.</summary>
public partial class ModelPickerOverlay : UserControl
{
    private ModelPickerViewModel? _vm;
    private const double ProviderCardWidth = 250, ModelCardWidth = 340;

    public ModelPickerOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ModelPickerViewModel);
        SizeChanged += (_, _) => UpdateColumns();
        PreviewKeyDown += OnKey;
    }

    private void Attach(ModelPickerViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.OpenChanged -= OnOpenChanged;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        if (_vm == null) return;
        _vm.OpenChanged += OnOpenChanged;
        _vm.PropertyChanged += OnVmChanged;
    }

    private void OnOpenChanged(bool open)
    {
        if (!open) return;
        // after the overlay is laid out: its width decides the columns, and it takes the keys
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            UpdateColumns();
            Focus();
            Keyboard.Focus(this);
        });
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelPickerViewModel.IsModelStep))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateColumns);
        else if (e.PropertyName == nameof(ModelPickerViewModel.HighlightIndex))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringHighlightIntoView);
    }

    private void UpdateColumns()
    {
        if (_vm == null) return;
        double pw = ProviderScroll.ActualWidth > 0 ? ProviderScroll.ActualWidth : ActualWidth - 90;
        double mw = ModelScroll.ActualWidth > 0 ? ModelScroll.ActualWidth : ActualWidth - 360;
        if (pw > 0) _vm.ProviderColumns = Math.Clamp((int)(pw / ProviderCardWidth), 1, 6);
        if (mw > 0) _vm.ModelColumns = Math.Clamp((int)(mw / ModelCardWidth), 1, 4);
    }

    private void BringHighlightIntoView()
    {
        if (_vm == null || _vm.HighlightIndex < 0) return;
        var list = _vm.IsProviderStep ? ProviderList : ModelList;
        if (list.ItemContainerGenerator.ContainerFromIndex(_vm.HighlightIndex) is FrameworkElement item) item.BringIntoView();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_vm.HandleKey(key)) e.Handled = true;
    }
}
