using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Models;

namespace CluadeX.Views;

public partial class TaskManagerView : System.Windows.Controls.UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
        // The column-header dividers are drawn by the OS header template (Aero2) with its own fixed light-grey gradient,
        // which no style setter reaches. Once the headers exist, point each divider at the theme's token instead
        // (Midnight's List.Header.Divider is that same gradient, so the default look does not move).
        Loaded += (_, _) => Dispatcher.BeginInvoke(ThemeHeaderDividers, DispatcherPriority.Background);

        // The output pane follows the view model's SelectedTask through this binding (it moves with the DataContext).
        SetBinding(ShownTaskProperty, new Binding("SelectedTask") { Mode = BindingMode.OneWay });
        Loaded += (_, _) => Watch(ShownTask);
        Unloaded += (_, _) => Watch(null);
    }

    // ─── output pane ───
    // The pane is not bound to TaskInfo.Output: that would hand the TextBox the whole text (up to 256K chars) on every
    // push, and it lays all of it out again. It gets the whole text once — when a task is selected, or when the service
    // trims the front off (a full replace) — and after that only each push's new text through TaskInfo.OutputAppended.

    private static readonly DependencyProperty ShownTaskProperty = DependencyProperty.Register(
        nameof(ShownTask), typeof(TaskInfo), typeof(TaskManagerView),
        new PropertyMetadata(null, (d, e) => { var v = (TaskManagerView)d; if (v.IsLoaded) v.Watch((TaskInfo?)e.NewValue); }));

    private TaskInfo? ShownTask => (TaskInfo?)GetValue(ShownTaskProperty);

    private TaskInfo? _watched;

    /// <summary>Show <paramref name="task"/>'s whole output and follow it from now on (null: stop following).</summary>
    private void Watch(TaskInfo? task)
    {
        if (_watched != null)
        {
            _watched.OutputAppended -= OnOutputAppended;
            _watched.PropertyChanged -= OnTaskPropertyChanged;
        }
        // Unsubscribed while the page is away: the tasks live in a singleton service and would keep this view alive.
        _watched = task;
        if (task != null)
        {
            task.OutputAppended += OnOutputAppended;
            task.PropertyChanged += OnTaskPropertyChanged;
        }
        ShowWhole(task?.Output ?? "");
    }

    private void ShowWhole(string text)
    {
        OutputBox.Text = text;
        OutputBox.ScrollToEnd();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskInfo.Output)) return;
        if (!Dispatcher.CheckAccess()) { PostResync(); return; }
        // Output set as a whole: the front was trimmed off, or an ended task was shrunk.
        if (ReferenceEquals(sender, _watched)) ReplaceKeepingPlace(_watched!.Output, _watched.OutputShift);
    }

    private void OnOutputAppended(TaskInfo task, string text)
    {
        if (!Dispatcher.CheckAccess()) { PostResync(); return; }
        if (!ReferenceEquals(task, _watched)) return;
        // Follow the end only when the reader is already there; someone scrolled up to read is left in place.
        bool atEnd = AtEnd();
        OutputBox.AppendText(text);
        if (atEnd) OutputBox.ScrollToEnd();
    }

    private bool AtEnd() => OutputBox.VerticalOffset + OutputBox.ViewportHeight >= OutputBox.ExtentHeight - 2;

    private bool _resyncPosted;

    /// <summary>
    /// A change event raised off the UI thread (the service pushes on the UI thread, so this is only a safety net):
    /// the box may not be touched here. Re-posting the appended text could add it twice — a Watch that runs first
    /// already shows it — so the UI thread takes the watched task's whole text once instead, however many came in.
    /// </summary>
    private void PostResync()
    {
        if (_resyncPosted) return;
        _resyncPosted = true;
        Dispatcher.InvokeAsync(() =>
        {
            _resyncPosted = false;
            string text = _watched?.Output ?? "";
            // The box may be several changes behind here, so no shift is known: the anchor is looked for near
            // where the length change alone puts it.
            if (OutputBox.Text != text) ReplaceKeepingPlace(text, -1);
        });
    }

    /// <summary>
    /// Replace the whole text (a trim cut the front off). A reader at the end stays at the end; one who scrolled up
    /// to read keeps the same text at the top of the pane, found again in the new text — or the top, when the part
    /// they were reading is the part that was cut. <paramref name="shift"/> is how far the kept text moved toward the
    /// front (<see cref="TaskInfo.OutputShift"/>), -1 when not known.
    /// </summary>
    private void ReplaceKeepingPlace(string text, int shift)
    {
        int line = AtEnd() ? -1 : OutputBox.GetFirstVisibleLineIndex();
        if (line < 0) { ShowWhole(text); return; }   // at the end, or not laid out (nobody is reading it)
        string old = OutputBox.Text;
        int at = OutputBox.GetCharacterIndexFromLineIndex(line);
        string anchor = at >= 0 && at < old.Length ? old.Substring(at, Math.Min(512, old.Length - at)) : "";
        OutputBox.Text = text;
        // The copy nearest where the reader's text should now be, not the first one: a server that prints the same
        // block every few seconds has many copies of the anchor, and the first is far above the reader.
        int expected = at - (shift >= 0 ? shift : old.Length - text.Length);
        // With a known shift, a place before the new start means the reader's text was the part that was cut.
        int found = shift >= 0 && expected < 0 ? -1 : FindNear(text, anchor, expected);
        if (found < 0) { OutputBox.ScrollToHome(); return; }
        // Bring the line into view first (a rectangle is only known for laid-out text), then put it at the top.
        OutputBox.UpdateLayout();
        int newLine = OutputBox.GetLineIndexFromCharacterIndex(found);
        if (newLine >= 0) OutputBox.ScrollToLine(newLine);
        OutputBox.UpdateLayout();
        var rect = OutputBox.GetRectFromCharacterIndex(found);
        if (!rect.IsEmpty) OutputBox.ScrollToVerticalOffset(Math.Max(0, OutputBox.VerticalOffset + rect.Top - OutputBox.Padding.Top));
    }

    /// <summary>The start of the copy of <paramref name="anchor"/> in <paramref name="text"/> closest to
    /// <paramref name="expected"/> (an exact hit there first), or -1 when there is none.</summary>
    internal static int FindNear(string text, string anchor, int expected)
    {
        if (anchor.Length == 0 || anchor.Length > text.Length) return -1;
        expected = Math.Clamp(expected, 0, text.Length - anchor.Length);
        if (string.CompareOrdinal(text, expected, anchor, 0, anchor.Length) == 0) return expected;
        int after = text.IndexOf(anchor, expected, StringComparison.Ordinal);
        // LastIndexOf searches back from its start index and wants the whole match at or before it.
        int before = expected > 0 ? text.LastIndexOf(anchor, expected + anchor.Length - 1, StringComparison.Ordinal) : -1;
        if (before < 0) return after;
        if (after < 0) return before;
        return expected - before <= after - expected ? before : after;
    }

    private void ThemeHeaderDividers()
    {
        foreach (var header in Descendants<GridViewColumnHeader>(this))
        {
            header.ApplyTemplate();
            if (header.Template?.FindName("PART_HeaderGripper", header) is Thumb gripper)
                gripper.SetResourceReference(Control.BackgroundProperty, "List.Header.Divider");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
