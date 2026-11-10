using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The chat's activity card (see the XAML). DataContext: <see cref="ActivityCardModel"/>.</summary>
public partial class ActivityCardView : UserControl
{
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand), typeof(ICommand), typeof(ActivityCardView));
    /// <summary>The ✕: ChatViewModel.CloseActivityCardCommand.</summary>
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(ActivityCardView), new PropertyMetadata(false, (d, _) => ((ActivityCardView)d).ApplyCompact()));
    /// <summary>The panel is stacked under the composer (little height): header and headline only.</summary>
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    public ActivityCardView()
    {
        InitializeComponent();
        // the clock moves with the scene: when the scene stops (idle, hidden, minimised) so does the clock
        Scene.SecondTick += (_, _) => (DataContext as ActivityCardModel)?.Tick(DateTime.Now);
    }

    private void ApplyCompact()
    {
        var v = Compact ? Visibility.Collapsed : Visibility.Visible;
        Scene.Visibility = Counters.Visibility = FeedBox.Visibility = v;
    }
}
