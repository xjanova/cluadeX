using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    private static readonly List<WeakReference<ChatView>> Instances = new();

    /// <summary>Perf probe: realized message rows per live chat view ("main 12 / panel 0").</summary>
    public static string RealizedRows()
    {
        var parts = new List<string>();
        foreach (var w in Instances)
            if (w.TryGetTarget(out var v))
            {
                int n = 0;
                for (int i = 0; i < v.ChatList.Items.Count; i++)
                    if (v.ChatList.ItemContainerGenerator.ContainerFromIndex(i) != null) n++;
                parts.Add($"{(v.IsVisible ? "visible" : "hidden")} {n}/{v.ChatList.Items.Count}");
            }
        return string.Join(" · ", parts);
    }

    public ChatView()
    {
        InitializeComponent();
        Instances.Add(new WeakReference<ChatView>(this));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // The chat page is kept and only hidden when another page is shown (Views/PageHost): coming back is not a
        // Loaded any more, so give the input box its focus back here — on the chat page, not the one docked in the
        // editor (the editor keeps the caret).
        IsVisibleChanged += (_, e) =>
        {
            // A hidden chat view KEEPS its message list (2026-10-20). It used to let go of it, so every Chat ⇄ Code switch
            // rebuilt every visible row and re-parsed each bubble's Markdown — the biggest UI stall in the log (Chat
            // shown in ~330 ms, up to 800 ms, many times an hour). Hidden, the list is collapsed: no new rows are laid
            // out, and a streamed update waits in MarkdownHelper until its row is visible again — so the work that
            // made the unbinding worthwhile is gone, and coming back is instant.
            if (e.NewValue is true && BindingOperations.GetBinding(ChatList, ItemsControl.ItemsSourceProperty) == null)
                ChatList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Messages"));
            if (e.NewValue is true && !_hostedInEditor)
                Dispatcher.InvokeAsync(() => InputBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        SizeChanged += (_, _) => UpdateSidePanelRoom();
        // A dragged grip (or the grip restoring its saved width) changes Width: refit, as the Edit page does.
        var panelWidth = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(WidthProperty, typeof(FrameworkElement));
        var panelHeight = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(HeightProperty, typeof(FrameworkElement));
        Loaded += (_, _) =>
        {
            panelWidth.AddValueChanged(SidePanel, OnSidePanelWidthChanged);
            panelHeight.AddValueChanged(SidePanel, OnSidePanelWidthChanged);
        };
        Unloaded += (_, _) =>
        {
            panelWidth.RemoveValueChanged(SidePanel, OnSidePanelWidthChanged);
            panelHeight.RemoveValueChanged(SidePanel, OnSidePanelWidthChanged);
        };
        // The panel's own show/hide (the toggle): the height grip follows it, or a hidden panel leaves a 6 px strip.
        SidePanel.IsVisibleChanged += (_, _) => UpdateSidePanelRoom();

        InputBox.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+V with a picture on the clipboard attaches it; text still pastes as text.
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && DataContext is ChatViewModel pasteVm
                && TryPasteImages(pasteVm))
            {
                e.Handled = true;
                return;
            }
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

    /// <summary>The same chat is also hosted in the Edit page's narrow right pane, which already shows the project's
    /// files and a terminal: there the side panel only squeezed the conversation into a sliver.</summary>
    private bool _hostedInEditor;

    /// <summary>The conversation keeps at least this much width beside the side panel (whatever width it was dragged to).</summary>
    public const double MinConversationWidth = 420, MinSidePanelWidth = 220;

    // The panel used to vanish whole below 600 + its width (owner 2026-10-18: "รายชื่อไฟล์หรือโฟลเดอร์ ด้านขวาก็หายไปเลย
    // ซึ่งมันควรมีการจัดคอลัมน์เองแบบฉลาด หน้า editor มีการทำเรื่องนี้แล้ว"). Now, like the Edit page: it gives up width
    // first (MaxWidth only — the dragged width comes back when the window widens), and when even its minimum does not fit
    // beside the conversation, or the window stands portrait, it moves under the composer across the whole width.
    private bool _panelStacked;
    private double _panelSideWidth = 300, _panelStackedHeight = double.NaN;
    private bool _fittingPanel;

    /// <summary>Stacked, the conversation and composer keep at least this much height above the files.</summary>
    private const double MinStackedConversationHeight = 320;

    /// <summary>Side by side, or stacked under the chat? Too narrow for both minimums, or a portrait window.</summary>
    public static bool StackSidePanel(double width, double height) =>
        width < MinConversationWidth + MinSidePanelWidth || CodeEditorView.IsPortrait(width, height);

    private void UpdateSidePanelRoom()
    {
        if (ActualWidth <= 0 || _fittingPanel) return;
        if (_hostedInEditor) { SidePanelHost.Visibility = Visibility.Collapsed; return; }
        SidePanelHost.Visibility = Visibility.Visible;
        _fittingPanel = true;
        try
        {
            bool stack = StackSidePanel(ActualWidth, ActualHeight);
            if (stack)
            {
                // Stacked it stretches (Width NaN). Any width that shows up here (the side width on the way in, or the
                // grip restoring its saved one on Loaded while stacked) is the side width to come back to.
                if (!double.IsNaN(SidePanel.Width)) _panelSideWidth = SidePanel.Width;
                // Column 0 only: the Auto column 1 then holds nothing and folds to zero, so the chat keeps the full width.
                Grid.SetColumn(SidePanelHost, 0); Grid.SetColumnSpan(SidePanelHost, 1);
                Grid.SetRow(SidePanelHost, 4); Grid.SetRowSpan(SidePanelHost, 1);
                SidePanel.Width = double.NaN;
                SidePanel.MaxWidth = double.PositiveInfinity;
                // The height is the user's (dragged on the top-edge grip, remembered across restarts — owner 2026-10-18:
                // "ไฟล์ในโปรเจคอยากให้ย้ายมาช่องแชทด้านล่างเมื่อหน้าจอแคบ ปรับขนาดกันได้"). Any height that shows up
                // here is theirs; a first stack gets about a third of the window. MaxHeight keeps the conversation readable.
                if (!double.IsNaN(SidePanel.Height)) _panelStackedHeight = SidePanel.Height;
                SidePanel.MaxHeight = Math.Max(SidePanelHeightGrip.MinSize, ActualHeight - MinStackedConversationHeight);
                SidePanel.Height = double.IsNaN(_panelStackedHeight) ? Math.Clamp(ActualHeight * 0.32, 160, 320) : _panelStackedHeight;
                SidePanel.BorderThickness = new Thickness(0, 1, 0, 0);
                SidePanelGrip.Opacity = 0; SidePanelGrip.IsHitTestVisible = false;
                SidePanelHeightGrip.Visibility = SidePanel.Visibility;
            }
            else
            {
                if (_panelStacked) SidePanel.Width = _panelSideWidth;
                Grid.SetColumn(SidePanelHost, 1); Grid.SetColumnSpan(SidePanelHost, 1);
                Grid.SetRow(SidePanelHost, 0); Grid.SetRowSpan(SidePanelHost, 4);
                // Beside the chat it runs the full height. A height here (the height grip restoring its saved one on
                // Loaded) is kept for the next time it stacks.
                if (!double.IsNaN(SidePanel.Height)) _panelStackedHeight = SidePanel.Height;
                SidePanel.Height = double.NaN;
                SidePanel.MaxHeight = double.PositiveInfinity;
                SidePanel.MaxWidth = Math.Max(MinSidePanelWidth, ActualWidth - MinConversationWidth);
                SidePanel.BorderThickness = new Thickness(1, 0, 0, 0);
                SidePanelGrip.Opacity = 1; SidePanelGrip.IsHitTestVisible = true;
                SidePanelHeightGrip.Visibility = Visibility.Collapsed;
            }
            _panelStacked = stack;
        }
        finally { _fittingPanel = false; }
    }

    private void OnSidePanelWidthChanged(object? sender, EventArgs e) => UpdateSidePanelRoom();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostedInEditor = FindAncestor<CodeEditorView>(this) != null;
        // A reply's relative link (`[app.js](src/app.js)`) is a file of the chat's project (Helpers/ChatLinks).
        if (DataContext is ChatViewModel linkVm) Helpers.ChatLinks.ProjectFolder = () => linkVm.WorkingDirectory;
        // The desktop pet steps out of the box the user types in (Services/Pet/PetZones).
        CluadeX.Services.Pet.PetZones.Register(ComposerCard);
        UpdateSidePanelRoom();
        InputBox.Focus();
        HookScrollViewer();

        if (DataContext is ChatViewModel vm)
        {
            if (_scrollHandler != null)
                vm.ScrollToBottom -= _scrollHandler;
            if (_collectionHandler != null && _watched != null)
                _watched.CollectionChanged -= _collectionHandler;

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
            WatchMessages(vm);
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.DictationApplied -= OnDictationApplied;
            vm.DictationApplied += OnDictationApplied;
        }
    }

    /// <summary>Spoken words just landed in the box: keep the caret (and the view) at the end, where they arrive.</summary>
    private void OnDictationApplied()
    {
        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.ScrollToEnd();
    }

    private System.Collections.ObjectModel.ObservableCollection<ChatMessage>? _watched;

    /// <summary>Follow the list on screen: each chat has its own list, swapped in when another chat is opened.</summary>
    private void WatchMessages(ChatViewModel vm)
    {
        if (_watched != null && _collectionHandler != null) _watched.CollectionChanged -= _collectionHandler;
        _watched = vm.Messages;
        if (_collectionHandler != null) _watched.CollectionChanged += _collectionHandler;
        _stickToBottom = true;
        Dispatcher.InvokeAsync(ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.Messages) && sender is ChatViewModel vm) WatchMessages(vm);
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
            vm.PropertyChanged -= OnVmPropertyChanged;
            if (_collectionHandler != null)
            {
                if (_watched != null) _watched.CollectionChanged -= _collectionHandler;
                _watched = null;
                _collectionHandler = null;
            }
        }
    }

    // ─── ScrollToEnd via ListBox's internal ScrollViewer ───

    private ScrollViewer? _chatScrollViewer;
    /// <summary>True while the reader is at (or within a few lines of) the bottom — only then does new output
    /// pull the view down.</summary>
    private bool _stickToBottom = true;
    // 160, not 48: with pixel virtualization a jump to the end lands up to ~120 px short until the rows it realized are
    // measured — that read as "the reader scrolled up" and new output stopped being followed.
    private const double BottomSlackPx = 160;

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
        else if (e.VerticalChange != 0 && sv.ScrollableHeight - sv.VerticalOffset <= BottomSlackPx)
        {
            // The reader came back to the bottom while rows were being re-measured (a jump to the end realizes rows and
            // changes the extent in the same event) — they are following again.
            _stickToBottom = true;
        }
    }

    private void ScrollToEnd()
    {
        HookScrollViewer();
        _chatScrollViewer?.ScrollToEnd();
        // Pixel virtualization: the rows realized by that jump are measured after it and the extent grows — settle on
        // the real end once layout is idle (it stopped ~100 px short).
        Dispatcher.InvokeAsync(() => { if (_stickToBottom) _chatScrollViewer?.ScrollToEnd(); },
            System.Windows.Threading.DispatcherPriority.ContextIdle);
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

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(child); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
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
    /// <summary>Right panel: a double-clicked file opens in the code editor.</summary>
    /// <summary>The composer's "+": its menu opens on a left click, above the button.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    private void QuestionOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatMessage msg } fe && fe.DataContext is string option)
            msg.AnswerQuestion(option);
    }

    private void QuestionSend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.AnswerQuestion(msg.AnswerDraft);
    }

    private void QuestionBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
        {
            msg.AnswerQuestion(msg.AnswerDraft);
            e.Handled = true;
        }
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.ResolvePermission(PermissionOutcome.Allowed);
    }

    /// <summary>Under this width the composer's buttons get a row of their own.</summary>
    internal const double NarrowComposerWidth = 640;

    private void ComposerStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyComposerLayout(e.NewSize.Width);
    }

    /// <summary>A narrow composer (the chat docked beside the editor — owner's screenshot, 2026-10-13): the mic / send /
    /// stop column kept its width, the options were left ~270 px and each took a line of its own. Under
    /// <see cref="NarrowComposerWidth"/> the buttons move to their own row on the right, the options get the full width,
    /// and the "effort" / "budget" words give way to the boxes' tooltips. Internal for the harness.</summary>
    internal void ApplyComposerLayout(double width)
    {
        bool narrow = width > 0 && width < NarrowComposerWidth;
        Grid.SetColumnSpan(ComposerOptions, narrow ? 2 : 1);
        Grid.SetRow(ComposerButtons, narrow ? 1 : 0);
        Grid.SetColumn(ComposerButtons, narrow ? 0 : 1);
        Grid.SetColumnSpan(ComposerButtons, narrow ? 2 : 1);
        ComposerButtons.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ComposerButtons.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        EffortLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        // The "model · effort in use" chip needs room; a narrow composer keeps it in the effort box's tooltip instead.
        LiveEffortChip.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        BudgetLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ChatModelBox.MinWidth = narrow ? 150 : 180;
        ApprovalBox.MinWidth = narrow ? 120 : 150;
    }

    private void PermissionAllowChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.AllowPermissionForChat();
    }

    private void PermissionAllowDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.AllowPermissionForDay();
    }

    private void PermissionAllowAlways_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.AllowPermissionAlways();
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            msg.ResolvePermission(PermissionOutcome.Denied);
    }

    // ─── Attaching images: Ctrl+V, drop, "+" ───

    /// <summary>
    /// A picture on the clipboard — a screenshot, an image copied in a browser, image files copied in Explorer — is
    /// attached to the next message. The "PNG" format comes first (browsers and the Snipping Tool put one there with
    /// its alpha intact); the bitmap form is flattened onto opaque, since many apps hand WPF a 32-bit DIB whose alpha
    /// channel is garbage and the picture would arrive invisible. False when there is no picture: paste as usual.
    /// </summary>
    private static bool TryPasteImages(ChatViewModel vm)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                // Files copied in Explorer — pictures and any other file (PDF, zip, Office, code…).
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0) { _ = vm.AttachFilesAsync(files); return true; }
            }
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is System.IO.MemoryStream png)
            {
                vm.AttachImageBytes(png.ToArray());
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                var opaque = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(opaque));
                using var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                vm.AttachImageBytes(ms.ToArray());
                return true;
            }
        }
        catch { /* the clipboard is held by another app right now — paste normally */ }
        return false;
    }

    /// <summary>Every file (and folder — refused later, with a hint) dragged in from Explorer: pictures become image
    /// attachments, anything else a file the AI can read, unpack or copy.</summary>
    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : Array.Empty<string>();

    /// <summary>Files dragged over the composer (Preview: the text box would refuse a file drop).</summary>
    private void Composer_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e).Length == 0) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Composer_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0 || DataContext is not ChatViewModel vm) return;
        _ = vm.AttachFilesAsync(files);
        e.Handled = true;
        InputBox.Focus();
    }

    /// <summary>"+" › Attach image…: pick one or more pictures.</summary>
    private void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.tif;*.tiff",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) vm.AttachImageFiles(dlg.FileNames);
        InputBox.Focus();
    }

    /// <summary>"+" › Attach file…: pick any files (PDF, zip, Word, Excel, code, pictures…).</summary>
    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "*.*|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = vm.AttachFilesAsync(dlg.FileNames);
        InputBox.Focus();
    }

    /// <summary>A file chip in a sent message: shows the attached copy selected in Explorer. Never opens or runs the
    /// file itself (an attached zip may hold anything).</summary>
    private void AttachedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path) || !System.IO.File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch { /* Explorer could not be started — nothing else to do */ }
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

/// <summary>An attachment chip's label: the file's name without its folder.</summary>
public sealed class FileNameOfConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string p ? System.IO.Path.GetFileName(p) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>An attachment chip's icon (Segoe MDL2 Assets) from the file's extension.</summary>
public sealed class FileGlyphConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        string ext = value is string p ? System.IO.Path.GetExtension(p).ToLowerInvariant() : "";
        return ext switch
        {
            ".zip" or ".tar" or ".gz" or ".tgz" or ".7z" or ".rar" => "\uF012",       // ZipFolder
            ".pdf" => "\uEA90",                                                        // PDF
            ".xlsx" or ".xlsm" or ".xls" or ".csv" or ".tsv" => "\uE80A",              // grid
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "\uEB9F",     // picture
            ".docx" or ".doc" or ".txt" or ".md" or ".rtf" => "\uE8A5",               // document
            _ => "\uE7C3",                                                             // page
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
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
