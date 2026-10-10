using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class ChatView : UserControl
{
    private Action? _scrollHandler;
    private NotifyCollectionChangedEventHandler? _collectionHandler;

    public ChatView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        InputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (DataContext is ChatViewModel vm && vm.SendMessageCommand.CanExecute(null))
                {
                    vm.SendMessageCommand.Execute(null);
                    e.Handled = true;
                }
            }
        };

        // Ctrl+F → toggle in-chat search
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (DataContext is ChatViewModel vm)
                {
                    vm.IsChatSearchVisible = !vm.IsChatSearchVisible;
                    if (vm.IsChatSearchVisible)
                        Dispatcher.BeginInvoke(() => ChatSearchBox.Focus(),
                            System.Windows.Threading.DispatcherPriority.Input);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape && DataContext is ChatViewModel vm2 && vm2.IsChatSearchVisible)
            {
                vm2.IsChatSearchVisible = false;
                vm2.ChatSearchQuery = "";
                InputBox.Focus();
                e.Handled = true;
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        HookScrollViewer();

        if (DataContext is ChatViewModel vm)
        {
            if (_scrollHandler != null)
                vm.ScrollToBottom -= _scrollHandler;
            if (_collectionHandler != null)
                vm.Messages.CollectionChanged -= _collectionHandler;

            // Follow new output only while the reader is already at the bottom. This used to jump to the end
            // on every change and every 20 streamed tokens, so nobody could scroll up to read while a reply
            // streamed — the view yanked them back down.
            _scrollHandler = () =>
            {
                Dispatcher.InvokeAsync(
                    () => { if (_stickToBottom) ScrollToEnd(); },
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            _collectionHandler = (_, args) =>
            {
                // The user just sent something, or a whole chat was swapped in: take them to the end.
                bool force = args.Action == NotifyCollectionChangedAction.Reset
                    || (args.NewItems?.OfType<ChatMessage>().Any(m => m.Role == MessageRole.User) ?? false);
                if (force) _stickToBottom = true;
                Dispatcher.InvokeAsync(
                    () => { if (_stickToBottom) ScrollToEnd(); },
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            vm.ScrollToBottom += _scrollHandler;
            vm.Messages.CollectionChanged += _collectionHandler;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_chatScrollViewer != null)
        {
            _chatScrollViewer.ScrollChanged -= OnChatScrollChanged;
            _chatScrollViewer = null;
        }

        if (DataContext is ChatViewModel vm)
        {
            if (_scrollHandler != null)
            {
                vm.ScrollToBottom -= _scrollHandler;
                _scrollHandler = null;
            }
            if (_collectionHandler != null)
            {
                vm.Messages.CollectionChanged -= _collectionHandler;
                _collectionHandler = null;
            }
        }
    }

    // ─── ScrollToEnd via ListBox's internal ScrollViewer ───

    private ScrollViewer? _chatScrollViewer;
    /// <summary>True while the reader is at (or within a few lines of) the bottom — only then does new output
    /// pull the view down.</summary>
    private bool _stickToBottom = true;
    private const double BottomSlackPx = 48;

    private void HookScrollViewer()
    {
        if (_chatScrollViewer != null) return;
        _chatScrollViewer = FindVisualChild<ScrollViewer>(ChatList);
        if (_chatScrollViewer != null)
            _chatScrollViewer.ScrollChanged += OnChatScrollChanged;
    }

    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The offset moved by itself: the user scrolled (wheel, drag, keys) — remember where they are.
            _stickToBottom = sv.ScrollableHeight - sv.VerticalOffset <= BottomSlackPx;
        }
        else if (_stickToBottom)
        {
            // Content grew (a message arrived, a reply streamed, a bubble re-measured): keep following.
            sv.ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        HookScrollViewer();
        _chatScrollViewer?.ScrollToEnd();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ─── Message hover — show/hide action buttons ───

    private void MessageBubble_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.IsHovered = true;
    }

    private void MessageBubble_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.IsHovered = false;
    }

    // ─── Copy message content to clipboard ───

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string content)
        {
            try { Clipboard.SetText(content); }
            catch { return; }
            MarkdownHelper.CopyButtonFeedback(btn, "\u2713", "\uE8C8");
        }
    }

    // ─── Retry — re-run the question THIS failed reply answered ───
    // (It used to resend the latest user message whatever bubble was hovered, as a duplicate.)

    private void RetryMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is FrameworkElement fe && fe.DataContext is ChatMessage failed
            && vm.RetryMessageCommand.CanExecute(failed))
        {
            vm.RetryMessageCommand.Execute(failed);
        }
    }

    // ─── Thinking block toggle ───

    private void ThinkingHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            // Walk up to find the ChatMessage DataContext
            var parent = fe;
            while (parent != null && parent.DataContext is not ChatMessage)
                parent = parent.Parent as FrameworkElement;

            if (parent?.DataContext is ChatMessage msg)
                msg.IsThinkingExpanded = !msg.IsThinkingExpanded;
        }
    }

    // ─── Permission request ───

    // Answering collapses the buttons and shows a localized outcome label (bound to PermissionOutcome) — they
    // used to stay clickable with a literal English "*Allowed*" appended to the prompt text.
    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.ResolvePermission(PermissionOutcome.Allowed);
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.ResolvePermission(PermissionOutcome.Denied);
    }

    // ─── Tool-row thumbnail → the full image ───

    /// <summary>Opens the image a tool saved, in the system's image viewer. Only an existing local .jpg/.png/.webp/.gif
    /// is handed to the shell (<see cref="ChatViewModel.IsOpenableImage"/>) — never a URL, a share or another file type.</summary>
    private void ToolThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !ChatViewModel.IsOpenableImage(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch { /* no viewer registered for images — nothing else to do */ }
    }

    // ─── Suggested prompts ───

    private void SuggestedPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string prompt && DataContext is ChatViewModel vm)
        {
            vm.UserInput = prompt;
            if (vm.SendMessageCommand.CanExecute(null))
                vm.SendMessageCommand.Execute(null);
        }
    }
}

/// <summary>
/// A tool-row thumbnail from an image file path: decoded small (≈240 px tall), read fully into memory and the file
/// closed at once — so a thumbnail never holds a lock on a screenshot the store later prunes — and nothing (no
/// image) when the file is gone or unreadable.
/// </summary>
public sealed class ThumbnailSourceConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string path || !ChatViewModel.IsOpenableImage(path)) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelHeight = 240;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
