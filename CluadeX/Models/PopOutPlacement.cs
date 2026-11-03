namespace CluadeX.Models;

/// <summary>Where a page's own window was when it last closed (Views/PopOut), in WPF units (1/96 inch). Saved per page
/// in <see cref="AppSettings.PopOutWindows"/>; <see cref="Maximized"/> = it was maximised over these normal bounds.</summary>
public sealed class PopOutPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Every number is a real, finite size (a hand-edited or damaged settings file can hold anything).</summary>
    public bool IsUsable =>
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) && Width >= 1 && Height >= 1;
}
