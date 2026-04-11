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

        if (DataContext is ChatViewModel vm)
        {
            if (_scrollHandler != null)
                vm.ScrollToBottom -= _scrollHandler;
            if (_collectionHandler != null)
                vm.Messages.CollectionChanged -= _collectionHandler;

            _scrollHandler = () =>
            {
                Dispatcher.InvokeAsync(
                    () => ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            _collectionHandler = (_, _) =>
            {
                Dispatcher.InvokeAsync(
                    () => ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            vm.ScrollToBottom += _scrollHandler;
            vm.Messages.CollectionChanged += _collectionHandler;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

    private void ScrollToEnd()
    {
        _chatScrollViewer ??= FindVisualChild<ScrollViewer>(ChatList);
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

    // ─── Retry — resend the last user message ───

    private void RetryMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
        {
            var lastUserMsg = vm.Messages.LastOrDefault(m => m.Role == MessageRole.User);
            if (lastUserMsg != null)
            {
                vm.UserInput = lastUserMsg.Content;
                if (vm.SendMessageCommand.CanExecute(null))
                    vm.SendMessageCommand.Execute(null);
            }
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

    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg && !msg.PermissionAnswered)
        {
            msg.PermissionAnswered = true;
            msg.Content += "\n*Allowed*";
            msg.PermissionCallback?.Invoke(true);
        }
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg && !msg.PermissionAnswered)
        {
            msg.PermissionAnswered = true;
            msg.Content += "\n*Denied*";
            msg.PermissionCallback?.Invoke(false);
        }
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
