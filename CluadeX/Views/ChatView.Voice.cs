using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>
/// The chat's side of reading replies aloud (owner 2026-11-10: "กดที่กล้องคำพูดนั้นก็ ฟังเสียงนั้นได้กดปุ่ม play ฟังก็ได้"):
/// the ▶ on a reply's hover strip, a click on a reply's bubble, and the player box's "go to this reply".
/// </summary>
public partial class ChatView
{
    private Point _bubbleDown;
    private ChatMessage? _bubbleDownOn;
    private DispatcherTimer? _bubbleClickTimer;

    private void ListenMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is FrameworkElement { DataContext: ChatMessage reply })
            vm.PlayMessageVoice(reply);
    }

    /// <summary>Pressed on a bubble: remembered, so the release can tell a click from a drag that selects text. A second
    /// press soon after (a double click selects a word) cancels the click that was about to play.</summary>
    private void Bubble_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _bubbleClickTimer?.Stop();
        _bubblePending = null;
        _bubbleDownOn = null;
        if (e.ClickCount != 1 || sender is not FrameworkElement { DataContext: ChatMessage m } bubble) return;
        if (!m.CanReadAloud || OnControl(e.OriginalSource as DependencyObject, bubble)) return;
        _bubbleDown = e.GetPosition(bubble);
        _bubbleDownOn = m;
    }

    /// <summary>Released where it was pressed, on a finished reply, while replies are read aloud: that reply is read
    /// once the double-click time has passed with no second press.</summary>
    private void Bubble_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var m = _bubbleDownOn;
        _bubbleDownOn = null;
        if (m == null || sender is not FrameworkElement { DataContext: ChatMessage at } bubble || !ReferenceEquals(at, m)) return;
        if (DataContext is not ChatViewModel vm || !vm.SpeakReplies) return;
        var p = e.GetPosition(bubble);
        if (Math.Abs(p.X - _bubbleDown.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(p.Y - _bubbleDown.Y) > SystemParameters.MinimumVerticalDragDistance) return;   // a drag: text was selected
        if (_bubbleClickTimer == null)
        {
            // One handler for every click: a click a double click cancelled must not come back with the next one
            // (it did — two plays of the same reply, and the second paused it).
            _bubbleClickTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
            _bubbleClickTimer.Tick += (_, _) =>
            {
                _bubbleClickTimer.Stop();
                var play = _bubblePending;
                _bubblePending = null;
                if (play != null && DataContext is ChatViewModel now) now.PlayMessageVoice(play);
            };
        }
        _bubbleClickTimer.Stop();
        _bubbleClickTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(DoubleClickMs(), 200, 700));
        _bubblePending = m;
        _bubbleClickTimer.Start();
    }

    /// <summary>The reply a click is about to play (once the double-click time passes).</summary>
    private ChatMessage? _bubblePending;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int DoubleClickMs()
    {
        try { return (int)GetDoubleClickTime(); } catch { return 500; }
    }

    /// <summary>The press landed on something of its own inside the bubble: a button, a link, a box, a scroll bar, a
    /// picture, a chip — not on the reply's words.</summary>
    internal static bool OnControl(DependencyObject? hit, DependencyObject bubble)
    {
        for (var d = hit; d != null && !ReferenceEquals(d, bubble); d = Up(d))
            if (d is ButtonBase or Hyperlink or TextBoxBase or ScrollBar or Thumb or Selector or Image) return true;
        return false;
    }

    private static DependencyObject? Up(DependencyObject d) => d switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };

    /// <summary>The player box's first words were clicked: the chat scrolls to that reply's bubble.</summary>
    private void VoicePlayer_ShowMessage(ChatMessage message)
    {
        if (ChatList.Items.Contains(message)) ChatList.ScrollIntoView(message);
    }
}
