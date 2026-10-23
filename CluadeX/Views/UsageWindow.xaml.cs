using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>A resizable, scrollable spend window, initially fitted to its own monitor's work area.</summary>
public partial class UsageWindow : Window
{
    public UsageWindow(UsageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => FitToMonitor();
    }

    public static Rect InitialBounds(Rect work, Rect? owner = null)
    {
        double width = Math.Min(1120, Math.Max(1, work.Width - 24));
        double height = Math.Min(860, Math.Max(1, work.Height - 24));
        var center = owner ?? work;
        double left = Math.Clamp(center.Left + (center.Width - width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
        double top = Math.Clamp(center.Top + (center.Height - height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new Rect(left, top, width, height);
    }

    private void FitToMonitor()
    {
        Rect area = SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2), ref info))
        {
            var physical = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            area = PresentationSource.FromVisual(this)?.CompositionTarget is { } target
                ? new System.Windows.Media.MatrixTransform(target.TransformFromDevice).TransformBounds(physical) : physical;
        }
        var bounds = InitialBounds(area, Owner is { } o ? new Rect(o.Left, o.Top, o.ActualWidth, o.ActualHeight) : null);
        MinWidth = Math.Min(420, bounds.Width); MinHeight = Math.Min(300, bounds.Height);
        Width = bounds.Width; Height = bounds.Height;
        if (WindowStartupLocation != WindowStartupLocation.Manual) { Left = bounds.Left; Top = bounds.Top; }
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e)
        => (DataContext as UsageViewModel)?.UpdateChartWidth(e.NewSize.Width);

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as UsageViewModel)?.Detach();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
