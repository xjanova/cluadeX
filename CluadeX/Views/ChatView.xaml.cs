using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();

        if (DataContext is ChatViewModel vm)
        {
            // Unsubscribe first to prevent duplicates if Loaded fires multiple times
            if (_scrollHandler != null)
                vm.ScrollToBottom -= _scrollHandler;
            if (_collectionHandler != null)
                vm.Messages.CollectionChanged -= _collectionHandler;

            _scrollHandler = () =>
            {
                Dispatcher.InvokeAsync(
                    () => ChatScroller.ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            _collectionHandler = (_, _) =>
            {
                Dispatcher.InvokeAsync(
                    () => ChatScroller.ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            vm.ScrollToBottom += _scrollHandler;
            vm.Messages.CollectionChanged += _collectionHandler;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Clean up event subscriptions to prevent leaks
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
}
