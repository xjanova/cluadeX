using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CluadeX.Helpers;

/// <summary>
/// A TextBlock whose text can be drag-selected and copied (Ctrl+C, or the right-click menu), like a read-only text box but
/// keeping everything a TextBlock draws (runs, links, code chips, pictures). Owner 2026-10-29: "ควรก๊อปปี้คำพูดในแชทแบบ
/// ลากคลุมได้". WPF has the selection engine (the internal TextEditor that TextBox and RichTextBox use) but no public way to
/// put it on a TextBlock, so it is attached by reflection — on the first press, not for every block of every reply. If a
/// future WPF moves those internals, <see cref="IsAvailable"/> is false and the block is an ordinary TextBlock again
/// (links still click; only the selection is gone).
/// </summary>
public class SelectableTextBlock : TextBlock
{
    private static readonly Type? EditorType = typeof(TextBlock).Assembly.GetType("System.Windows.Documents.TextEditor");
    private static readonly PropertyInfo? EditorReadOnly = EditorType?.GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? EditorTextView = EditorType?.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? BlockContainer = typeof(TextBlock).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type? ContainerType = typeof(TextBlock).Assembly.GetType("System.Windows.Documents.ITextContainer");
    private static readonly PropertyInfo? ContainerTextView = ContainerType?.GetProperty("TextView");

    /// <summary>Could the selection engine be found (the class handlers are in place)?</summary>
    public static bool IsAvailable { get; }

    static SelectableTextBlock()
    {
        FocusableProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(null));
        KeyboardNavigation.IsTabStopProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(false));
        try
        {
            var register = EditorType?.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(Type), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (register == null || EditorReadOnly == null || EditorTextView == null || BlockContainer == null || ContainerTextView == null) return;
            // (type, acceptsRichContent, readOnly, registerEventListeners): mouse selection, Ctrl+A, Ctrl+C — no editing
            register.Invoke(null, new object[] { typeof(SelectableTextBlock), true, true, true });
            IsAvailable = true;
        }
        catch (Exception) { IsAvailable = false; }
    }

    private object? _editor;

    /// <summary>The selection engine, attached on first use (a long chat has thousands of blocks nobody selects).</summary>
    public bool EnsureEditor()
    {
        if (_editor != null) return true;
        if (!IsAvailable) return false;
        try
        {
            var container = BlockContainer!.GetValue(this);
            if (container == null) return false;
            var editor = Activator.CreateInstance(EditorType!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance,
                null, new[] { container, this, (object)false }, null);
            if (editor == null) return false;
            EditorReadOnly!.SetValue(editor, true);
            EditorTextView!.SetValue(editor, ContainerTextView!.GetValue(container));
            _editor = editor;
            return true;
        }
        catch (Exception) { return false; }
    }

    private ChatLinkTarget? _pressedLink;
    private Point _pressedAt;

    // The press tunnels here before the engine's own (bubbling) handler runs, so it is in place in time for the first drag.
    // A press on a link or picture is remembered: the engine takes the mouse, so the link may never see its release.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        EnsureEditor();
        _pressedLink = e.ClickCount == 1 ? LinkAt(e.OriginalSource as DependencyObject) : null;
        _pressedAt = e.GetPosition(this);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    // Released where it was pressed (not a drag to select): open it — after the link's own Click, if that came, so once.
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var link = _pressedLink;
        _pressedLink = null;
        var moved = e.GetPosition(this) - _pressedAt;
        if (link != null && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            Dispatcher.BeginInvoke(() => link.Open(this), DispatcherPriority.Input);
        base.OnPreviewMouseLeftButtonUp(e);
    }

    private ChatLinkTarget? LinkAt(DependencyObject? d)
    {
        while (d != null && !ReferenceEquals(d, this))
        {
            if (d is FrameworkElement { Tag: ChatLinkTarget onElement }) return onElement;
            if (d is FrameworkContentElement { Tag: ChatLinkTarget onRun }) return onRun;
            d = d is Visual ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // Copy / Select all on a right-click, made on the first one (the menu's Copy is the engine's own command)
        if (EnsureEditor() && ContextMenu == null)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = CluadeX.Services.LocalizedResources.Instance["chat.copy"], Command = ApplicationCommands.Copy, CommandTarget = this, InputGestureText = "Ctrl+C" });
            menu.Items.Add(new MenuItem { Header = CluadeX.Services.LocalizedResources.Instance["chat.selectAll"], Command = ApplicationCommands.SelectAll, CommandTarget = this, InputGestureText = "Ctrl+A" });
            ContextMenu = menu;
        }
        base.OnPreviewMouseRightButtonDown(e);
    }
}
