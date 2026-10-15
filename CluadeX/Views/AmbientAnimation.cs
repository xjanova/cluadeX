using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CluadeX.Views;

/// <summary>
/// Pauses and resumes the decorative storyboards an element tree starts from its own triggers
/// (<c>&lt;EventTrigger RoutedEvent="Loaded"&gt;&lt;BeginStoryboard&gt;</c> with RepeatBehavior="Forever"). Loaded fires for a
/// Collapsed element too, so such a storyboard runs — and keeps WPF's render loop ticking at 60 fps — while nothing
/// of it is on screen. A paused clock asks for no frames. BeginStoryboard always begins its storyboard controllable,
/// keyed by the element whose Triggers hold it, so Pause/Resume on that element reach it.
/// </summary>
public static class AmbientAnimation
{
    /// <summary>Pause (or resume) every trigger-started storyboard in <paramref name="root"/>'s visual tree,
    /// not descending into <paramref name="skip"/>.</summary>
    public static void SetPaused(DependencyObject root, bool paused, DependencyObject? skip = null)
    {
        if (ReferenceEquals(root, skip)) return;
        if (root is FrameworkElement fe && fe.Triggers.Count > 0)
        {
            foreach (var trigger in fe.Triggers)
            {
                if (trigger is not EventTrigger et) continue;
                foreach (var action in et.Actions)
                {
                    if (action is not BeginStoryboard { Storyboard: { } sb }) continue;
                    try
                    {
                        if (paused) sb.Pause(fe);
                        else sb.Resume(fe);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AmbientAnimation] {ex.Message}"); }
                }
            }
        }

        int count = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < count; i++)
            SetPaused(VisualTreeHelper.GetChild(root, i), paused, skip);
    }
}
