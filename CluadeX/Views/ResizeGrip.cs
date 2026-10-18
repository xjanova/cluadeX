using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// A thin drag handle on the edge of a panel: dragging sets the panel's Width (clamped to MinSize..MaxSize), a
/// double-click puts it back to the width it was designed with, and the width is remembered per <see cref="Key"/>
/// across restarts. It sits in the same grid cell as the panel (aligned to the shared edge), so adding it never
/// shifts column indices. Invisible until hovered — then the accent shows where to grab.
/// </summary>
public sealed class ResizeGrip : Thumb
{
    /// <summary>Wired once at startup (App) to the settings file; null in harnesses, where nothing persists.</summary>
    public static Func<string, double?>? Load;
    public static Action<string, double>? Save;

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(FrameworkElement), typeof(ResizeGrip),
        new PropertyMetadata(null, (d, _) => ((ResizeGrip)d).ApplySaved()));

    /// <summary>The panel whose Width this grip drives (it must have an explicit Width).</summary>
    public FrameworkElement? Target
    {
        get => (FrameworkElement?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Settings key the width is remembered under; empty = not remembered.</summary>
    public string Key { get; set; } = "";
    public double MinSize { get; set; } = 160;
    public double MaxSize { get; set; } = 900;

    /// <summary>The grip is on the panel's LEFT edge (a right-hand panel): dragging left widens it. With
    /// <see cref="Vertical"/>: the TOP edge (a panel below the content): dragging up makes it taller.</summary>
    public bool OnLeftEdge { get; set; }

    private bool _vertical;
    /// <summary>Drives the target's Height instead of its Width (a grip lying along a horizontal edge).</summary>
    public bool Vertical
    {
        get => _vertical;
        set
        {
            _vertical = value;
            if (value) { Width = double.NaN; Height = 6; Cursor = Cursors.SizeNS; }
            else { Height = double.NaN; Width = 6; Cursor = Cursors.SizeWE; }
        }
    }

    private DependencyProperty SizeProperty => _vertical ? HeightProperty : WidthProperty;
    private double TargetSize(FrameworkElement t) => _vertical ? t.Height : t.Width;

    private double _designWidth = double.NaN;
    private bool _applied;

    public ResizeGrip()
    {
        Width = 6;
        Cursor = Cursors.SizeWE;
        Background = Brushes.Transparent;
        Focusable = false;
        Panel.SetZIndex(this, 50);
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        Template = new ControlTemplate(typeof(Thumb)) { VisualTree = bar };

        DragDelta += OnDragDelta;
        DragCompleted += (_, _) => { Persist(); Highlight(); };
        MouseDoubleClick += (_, e) =>
        {
            if (Target == null || double.IsNaN(_designWidth)) return;
            Target.SetValue(SizeProperty, _designWidth);
            Persist();
            e.Handled = true;
        };
        Loaded += (_, _) => ApplySaved();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Highlight(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Highlight(); }

    private void Highlight()
    {
        bool hot = IsMouseOver || IsDragging;
        Background = hot ? ThemeService.BrushOf("Accent.1", Brushes.SteelBlue) : Brushes.Transparent;
        Opacity = hot ? 0.7 : 1;
    }

    /// <summary>The remembered width, once the target is known (the design width is captured first).</summary>
    private void ApplySaved()
    {
        if (_applied || Target == null) return;
        _applied = true;
        _designWidth = TargetSize(Target);
        if (Key.Length > 0 && Load?.Invoke(Key) is double saved && !double.IsNaN(saved))
            Target.SetValue(SizeProperty, Math.Clamp(saved, MinSize, MaxSize));
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Target == null) return;
        double size = TargetSize(Target);
        double current = double.IsNaN(size) ? (_vertical ? Target.ActualHeight : Target.ActualWidth) : size;
        double change = _vertical ? e.VerticalChange : e.HorizontalChange;
        double next = current + (OnLeftEdge ? -change : change);
        Target.SetValue(SizeProperty, Math.Clamp(next, MinSize, MaxSize));
    }

    private void Persist()
    {
        if (Target == null || Key.Length == 0 || double.IsNaN(TargetSize(Target))) return;
        try { Save?.Invoke(Key, Math.Round(TargetSize(Target))); } catch { /* a size is not worth an error */ }
    }
}
