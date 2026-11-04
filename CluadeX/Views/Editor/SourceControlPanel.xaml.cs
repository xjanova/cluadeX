using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace CluadeX.Views.Editor;

/// <summary>
/// The Code Editor's Source Control panel (owner 2026-11-04: "ทำให้ชัดเจนขึ้น เครื่องมือมากขึ้นหน่อย สมเป็นโปรแกรมชั้นเลิศ").
/// Its own control so the page's XAML only places it; everything it does is on <see cref="CodeEditorViewModel"/>
/// (CodeEditorViewModel.GitPanel.cs). This file only opens the menus, wires Ctrl+Enter, runs the busy strip, and keeps
/// the panel from squeezing the file tree to nothing in a short window.
/// </summary>
public partial class SourceControlPanel : UserControl
{
    /// <summary>The file tree above keeps at least this much height, however tall the panel was dragged.</summary>
    private const double TreeReserve = 170;

    public SourceControlPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        BusyRow.IsVisibleChanged += (_, _) => RunBusyStrip(BusyRow.IsVisible);
        BusyTrack.SizeChanged += (_, _) => RunBusyStrip(BusyRow.IsVisible);
    }

    private FrameworkElement? _host;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host != null || Parent is not FrameworkElement host) return;
        _host = host;
        host.SizeChanged += (_, _) => ClampToHost();
        ClampToHost();
    }

    private void ClampToHost()
    {
        if (_host == null || _host.ActualHeight <= 0) return;
        MaxHeight = Math.Max(MinHeight, _host.ActualHeight - TreeReserve);
    }

    private CodeEditorViewModel? Vm => DataContext as CodeEditorViewModel;

    // ── Keyboard: Ctrl+Enter commits, Ctrl+Shift+Enter commits and pushes ──

    private void OnCommitBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;   // never a new line on Ctrl+Enter
        var vm = Vm;
        if (vm == null) return;
        var cmd = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? vm.CommitAndPushCommand : vm.CommitCommand;
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }

    // ── Menus ──

    private static void Open(Button anchor, ContextMenu menu)
    {
        menu.DataContext = anchor.DataContext;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        FillBranchItems(SwitchBranchMenu.Items, vm, withHeader: false);
        Open(MoreButton, MoreMenu);
    }

    private void OnCommitMenu(object sender, RoutedEventArgs e) => Open(CommitMenuButton, CommitMenu);

    /// <summary>The branch chip: every local branch (the current one checked) and "New branch…". A switch is still
    /// confirmed by the view model and refused while tabs are unsaved.</summary>
    private void OnBranchMenu(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = new ContextMenu();
        FillBranchItems(menu.Items, vm, withHeader: true);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = LocalizedResources.Instance["scm.menu.newBranch"],
            Command = vm.NewBranchCommand,
            Icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets") },
        });
        Open(BranchChip, menu);
    }

    private static void FillBranchItems(ItemCollection items, CodeEditorViewModel vm, bool withHeader)
    {
        items.Clear();
        if (withHeader)
            items.Add(new MenuItem { Header = LocalizedResources.Instance["scm.menu.switchBranch"], IsEnabled = false, FontWeight = FontWeights.SemiBold });
        var others = vm.GitBranches.ToList();
        if (others.Count == 0 || others.All(b => b == vm.GitBranch))
        {
            items.Add(new MenuItem { Header = LocalizedResources.Instance["scm.menu.noOtherBranch"], IsEnabled = false });
            if (others.Count == 0) return;
        }
        foreach (var branch in others)
        {
            var item = new MenuItem
            {
                Header = branch,
                IsChecked = branch == vm.GitBranch,
                Command = vm.SwitchBranchCommand,
                CommandParameter = branch,
            };
            item.SetResourceReference(FontFamilyProperty, "Font.Mono.Display");
            items.Add(item);
        }
    }

    // ── Busy strip: a short bar sliding across while a git operation runs ──

    private void RunBusyStrip(bool on)
    {
        if (!on || BusyTrack.ActualWidth <= 0)
        {
            BusyShift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        var slide = new DoubleAnimation(-BusyBar.Width, BusyTrack.ActualWidth, TimeSpan.FromSeconds(1.1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        BusyShift.BeginAnimation(TranslateTransform.XProperty, slide);
    }
}

/// <summary>
/// The panel's one converter (its own, so the control does not depend on the page's resources): true / a non-zero
/// number / a non-empty string → Visible. ConverterParameter "Invert" flips it; "Bool" returns the bool itself.
/// </summary>
public sealed class ScmBoolVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        string p = parameter as string ?? "";
        if (p == "Invert") on = !on;
        if (p == "Bool" || targetType == typeof(bool)) return on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
