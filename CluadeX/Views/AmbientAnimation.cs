using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CluadeX.Views;

/// <summary>
/// Pauses and resumes the decorative storyboards an element tree starts from its own triggers
/// (<c>&lt;EventTrigger RoutedEvent="Loaded"&gt;&lt;BeginStoryboard&gt;</c> with RepeatBehavior="Forever"). Loaded fires for a
/// Collapsed element too, so such a storyboard runs — and keeps WPF's render loop ticking at 60 fps — while nothing
/// of it is on screen. A paused clock asks for no frames. A BeginStoryboard begins its storyboard controllable only when
/// it has a NAME (an unnamed one ignores Pause and Resume without a word), keyed by the element whose Triggers hold it,
/// so every such BeginStoryboard carries an x:Name and Pause/Resume on that element reach it.
/// </summary>
public static class AmbientAnimation
{
    /// <summary>Set on the element whose Triggers start an endless storyboard: it runs only while the element is on
    /// screen, and is gone with the element, so a hidden chat row or a closed bubble asks for no frames.</summary>
    public static readonly DependencyProperty WhileVisibleProperty = DependencyProperty.RegisterAttached(
        "WhileVisible", typeof(bool), typeof(AmbientAnimation), new PropertyMetadata(false, OnWhileVisibleChanged));

    public static bool GetWhileVisible(DependencyObject d) => (bool)d.GetValue(WhileVisibleProperty);
    public static void SetWhileVisible(DependencyObject d, bool value) => d.SetValue(WhileVisibleProperty, value);

    private static void OnWhileVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        fe.IsVisibleChanged += (_, _) => SetOwnPaused(fe, !fe.IsVisible);
        // After the Loaded trigger has begun the storyboard, whichever handler runs first.
        fe.Loaded += (_, _) => fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SetOwnPaused(fe, !fe.IsVisible));
        // Loaded begins it again if the element comes back; a clock left behind would tick until the GC finds it.
        fe.Unloaded += (_, _) => ForEachStoryboard(fe, sb => sb.Remove(fe));
    }

    /// <summary>Set on the element whose Triggers start an endless flourish that only matters to someone using the
    /// window: it stops while another app has the focus or the window is minimised.</summary>
    public static readonly DependencyProperty WhileActiveProperty = DependencyProperty.RegisterAttached(
        "WhileActive", typeof(bool), typeof(AmbientAnimation), new PropertyMetadata(false, OnWhileActiveChanged));

    public static bool GetWhileActive(DependencyObject d) => (bool)d.GetValue(WhileActiveProperty);
    public static void SetWhileActive(DependencyObject d, bool value) => d.SetValue(WhileActiveProperty, value);

    private static void OnWhileActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        Window? hooked = null;
        void Sync(object? sender, EventArgs args) => SetOwnPaused(fe, hooked is not { IsActive: true });
        fe.Loaded += (_, _) =>
        {
            if (hooked == null && Window.GetWindow(fe) is { } w)
            {
                hooked = w;
                w.Activated += Sync;
                w.Deactivated += Sync;
            }
            fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Sync(fe, EventArgs.Empty));
        };
    }

    /// <summary>Pause (or resume) every trigger-started storyboard in <paramref name="root"/>'s visual tree,
    /// not descending into <paramref name="skip"/>.</summary>
    public static void SetPaused(DependencyObject root, bool paused, DependencyObject? skip = null)
    {
        if (ReferenceEquals(root, skip)) return;
        if (root is FrameworkElement fe) SetOwnPaused(fe, paused);

        int count = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < count; i++)
            SetPaused(VisualTreeHelper.GetChild(root, i), paused, skip);
    }

    /// <summary>The window was minimised (or came back): a minimised window shows nothing, yet every running
    /// storyboard kept WPF's frame clock ticking — the sidebar's pulses, the split-chat twinkle, the buddy. Paused all
    /// at once; on restore each resumes by its own rule (WhileVisible: if on screen, WhileActive: if the window is
    /// active, anything else: as it was).</summary>
    public static void SetMinimized(Window window, bool minimized)
    {
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement fe && fe.Triggers.Count > 0)
            {
                bool paused = minimized
                    || (GetWhileVisible(fe) && !fe.IsVisible)
                    || (GetWhileActive(fe) && !window.IsActive);
                SetOwnPaused(fe, paused);
            }
            // State-driven loops (a plan step at work, a sub-agent running) live in Style triggers: they stop by their
            // own ExitActions, but a minimised window must not tick them either.
            if (d is FrameworkElement styled && styled.Style is { } style) SetStylePaused(styled, style, minimized);
            if (d is FrameworkElement looping) Motion.SetLoopPaused(looping, minimized);
            int count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(window);
        // Owned windows (a chat's plan window) go away with their owner; their loops pause with it.
        foreach (Window owned in window.OwnedWindows) SetMinimized(owned, minimized || owned.WindowState == WindowState.Minimized);
    }

    /// <summary>Pause (or resume) the endless storyboards <paramref name="style"/>'s triggers began on <paramref name="fe"/>
    /// — only the named ones are controllable. Pause / Resume on one that never began does nothing (GetCurrentState
    /// would throw for it, so it is not asked); the pause lands on the next frame.</summary>
    private static void SetStylePaused(FrameworkElement fe, Style style, bool paused)
    {
        for (var s = style; s != null; s = s.BasedOn)
            foreach (var trigger in s.Triggers)
            {
                if (trigger.EnterActions.Count == 0) continue;
                foreach (var action in trigger.EnterActions)
                {
                    if (action is not BeginStoryboard { Name: { Length: > 0 }, Storyboard: { RepeatBehavior: { HasCount: false, HasDuration: false } } sb })
                        continue;
                    try { if (paused) sb.Pause(fe); else sb.Resume(fe); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AmbientAnimation] {ex.Message}"); }
                }
            }
    }

    private static void SetOwnPaused(FrameworkElement fe, bool paused) =>
        ForEachStoryboard(fe, sb => { if (paused) sb.Pause(fe); else sb.Resume(fe); });

    private static void ForEachStoryboard(FrameworkElement fe, Action<Storyboard> act)
    {
        foreach (var trigger in fe.Triggers)
        {
            if (trigger is not EventTrigger et) continue;
            foreach (var action in et.Actions)
            {
                if (action is not BeginStoryboard { Storyboard: { } sb }) continue;
                try { act(sb); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AmbientAnimation] {ex.Message}"); }
            }
        }
    }
}
