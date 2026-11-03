using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

public partial class CodeEditorView : UserControl
{
    /// <summary>Midnight's caret, for the (theoretical) case of no theme being loaded at all.</summary>
    private static readonly Brush CaretFallback = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xDF, 0xFF)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>The theme service this view listens to while it is loaded (null when detached).</summary>
    private ThemeService? _themes;

    private readonly AgentGlowRenderer _glow = new();
    private System.Windows.Threading.DispatcherTimer? _glowTimer;
    private CodeEditorViewModel? _vm;
    private OpenFileTab? _boundTab;
    private string _lastSynced = "";
    /// <summary>The agent's typing reveal inserted into the editor since <see cref="_lastSynced"/> was taken: the
    /// editor's own text is the truth until the next Content change.</summary>
    private bool _lastSyncedStale;
    /// <summary>The reveal whose frames are in the editor (its insert text), and how much of it is shown.</summary>
    private string? _revealInsert;
    private int _revealShown;
    private bool _suppressSync;

    /// <summary>
    /// Large files, Notepad++ style: above this many characters autocomplete stops opening by itself (Ctrl+Space
    /// still works) and folding and the live word count switch off.
    /// </summary>
    private const int LargeBufferChars = 2_000_000;
    /// <summary>
    /// Above this many characters a keystroke no longer copies the whole buffer into the tab: typing marks the tab
    /// dirty at once and the text is copied after a short pause, before a save, and when the tab is left. The copy
    /// per key used to start only at 2 M chars (a 50 MB file stuttered on every letter); any file past about 42 K
    /// chars made a Large Object Heap string per key, half a megabyte per letter in a file like ChatViewModel.cs,
    /// and regular gen-2 collections while typing (2026-11-02).
    /// </summary>
    private const int DeferredSyncChars = 32_000;
    /// <summary>Beyond this the file opens without syntax colours, as Notepad++ does: colouring is lazy, but the
    /// first jump to the end would have to colour everything before it.</summary>
    private const int NoHighlightChars = 24_000_000;
    private bool _largeBuffer;
    private System.Windows.Threading.DispatcherTimer? _deferredSync;
    private bool _hooked;
    private CompletionWindow? _completionWindow;
    private int _completionRequest;
    /// <summary>How far back from the caret the completion prefix is read. No real identifier is longer.</summary>
    private const int MaxPrefixChars = 256;
    /// <summary>The word (and where it starts) whose automatic completion found nothing in a file past
    /// <see cref="DeferredSyncChars"/>: more letters on it are not asked again, since each ask copies the file.</summary>
    private string? _noMatchStem;
    private int _noMatchStart = -1;
    /// <summary>The document version the stem was read from: the skip holds only while every edit since then
    /// appended to that word, so a paste elsewhere that brings a match into the buffer asks again.</summary>
    private ICSharpCode.AvalonEdit.Document.ITextSourceVersion? _noMatchVersion;
    private readonly BreakpointMargin _breakpointMargin = new();
    private readonly ExecutionLineRenderer _executionLine = new();

    public CodeEditorView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        // The view model plays the agent's "typing" reveal only while someone can see it (60 fps of editor inserts
        // are pure cost on a hidden page).
        IsVisibleChanged += (_, e) =>
        {
            if (_vm != null) _vm.EditorOnScreen = e.NewValue is true;
            if (e.NewValue is not true) PauseMedia();   // another page: a song or a clip does not play on unseen
        };
        // Preview, so Ctrl+S reaches us even when the focused control (editor, terminal box) would
        // otherwise swallow the key.
        PreviewKeyDown += OnPagePreviewKeyDown;
        SizeChanged += (_, _) => FitSidePanels();
        // A dragged grip changes Width: refit, so a drag past the room left shows as far as it can go.
        var width = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(WidthProperty, typeof(FrameworkElement));
        Loaded += (_, _) =>
        {
            width.AddValueChanged(ExplorerPanel, OnPanelWidthChanged);
            width.AddValueChanged(EditorChatPanel, OnPanelWidthChanged);
            FitSidePanels();
            WatchMediaOverlays();   // (again in a window of its own: that window's overlays, if any)
        };
        Unloaded += (_, _) =>
        {
            width.RemoveValueChanged(ExplorerPanel, OnPanelWidthChanged);
            width.RemoveValueChanged(EditorChatPanel, OnPanelWidthChanged);
            UnwatchMediaOverlays();
        };
    }

    private void OnPanelWidthChanged(object? sender, EventArgs e) => FitSidePanels();

    /// <summary>Narrow window: the side panels give way so the editor keeps room. Only MaxWidth changes — the
    /// widths the user dragged (and the grips remember) stay, and come back as soon as the window widens.</summary>
    private void FitSidePanels()
    {
        if (ActualWidth <= 0) return;
        if (ApplyOrientation()) return;
        var (explorer, chat) = SidePanelWidths(ActualWidth, ExplorerPanel.Width, _chatHidden ? 0 : EditorChatPanel.Width);
        ExplorerPanel.MaxWidth = explorer;
        ExplorerPanel.Visibility = explorer < 1 ? Visibility.Collapsed : Visibility.Visible;
        EditorChatPanel.MaxWidth = chat;
    }

    private bool _stacked;
    private double _chatSideWidth = double.NaN;

    /// <summary>A portrait window (taller than wide, not very wide): the chat leaves the right-hand column and sits under
    /// the editor across the whole width, with a draggable line between them; side by side again once the window turns
    /// landscape. True while stacked (the side-panel fitting then only concerns the file tree).</summary>
    private bool ApplyOrientation()
    {
        bool stacked = !_chatHidden && IsPortrait(ActualWidth, ActualHeight);   // (no chat in its own window: nothing to stack)
        if (stacked != _stacked)
        {
            _stacked = stacked;
            if (stacked)
            {
                _chatSideWidth = EditorChatPanel.Width;
                Grid.SetRow(EditorChatPanel, 1); Grid.SetColumn(EditorChatPanel, 0); Grid.SetColumnSpan(EditorChatPanel, 3);
                EditorChatPanel.Width = double.NaN;
                EditorChatPanel.MaxWidth = double.PositiveInfinity;
                EditorChatPanel.BorderThickness = new Thickness(0, 1, 0, 0);
                EditorRow.Height = new GridLength(1.15, GridUnitType.Star);
                ChatRow.Height = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                Grid.SetRow(EditorChatPanel, 0); Grid.SetColumn(EditorChatPanel, 2); Grid.SetColumnSpan(EditorChatPanel, 1);
                if (!double.IsNaN(_chatSideWidth)) EditorChatPanel.Width = _chatSideWidth;
                EditorChatPanel.BorderThickness = new Thickness(1, 0, 0, 0);
                EditorRow.Height = new GridLength(1, GridUnitType.Star);
                ChatRow.Height = new GridLength(0);
            }
        }
        ChatGrip.Visibility = stacked || _chatHidden ? Visibility.Collapsed : Visibility.Visible;
        ChatSplitter.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
        if (!stacked) return false;
        // Stacked: only the file tree shares the editor's row.
        var (explorer, _) = SidePanelWidths(ActualWidth, ExplorerPanel.Width, 0);
        ExplorerPanel.MaxWidth = explorer;
        ExplorerPanel.Visibility = explorer < 1 ? Visibility.Collapsed : Visibility.Visible;
        return true;
    }

    /// <summary>Taller than wide by a margin, and not a wide screen: stack the chat under the editor.</summary>
    public static bool IsPortrait(double width, double height) => height > width * 1.15 && width < 1400;

    public const double EditorMinWidth = 320, ExplorerMinWidth = 160, ChatMinWidth = 280, ChatFloorWidth = 220;

    /// <summary>How wide the file tree and the chat may be in a page <paramref name="total"/> wide, given the widths
    /// the user picked. The chat is the main surface, so the tree gives way first: it shrinks to its minimum, then the
    /// chat shrinks to its own, then the tree folds away, and last the chat shrinks to a floor and the editor takes
    /// what is left. Infinity = no limit.</summary>
    public static (double Explorer, double Chat) SidePanelWidths(double total, double explorer, double chat)
    {
        double spare = total - EditorMinWidth;
        if (explorer + chat <= spare) return (double.PositiveInfinity, double.PositiveInfinity);
        double e = Math.Max(Math.Min(explorer, ExplorerMinWidth), spare - chat);
        if (e + chat <= spare) return (e, double.PositiveInfinity);
        double c = Math.Max(Math.Min(chat, ChatMinWidth), spare - e);
        if (e + c <= spare) return (e, c);
        return (0, Math.Max(Math.Min(chat, ChatFloorWidth), spare));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not CodeEditorViewModel vm) return;
            InitializeEditorOnce();
            AttachVm(vm);
            if (vm.Tree.Count == 0) await vm.RefreshTreeAsync();
        }
        catch (Exception ex)
        {
            // async void — an escaped exception would hit the global crash dialog.
            CommandErrorSink.Report(nameof(OnLoaded), ex);
        }
    }

    /// <summary>
    /// The view model is a SINGLETON; this view is rebuilt on every visit to the Code page. Every
    /// subscription to the VM therefore has to be undone when the view leaves — the old code
    /// subscribed 8 VM events (and the active tab's PropertyChanged) in Loaded and never removed
    /// them, so each visit left another invisible editor alive, still syncing text, still scrolling,
    /// still repainting on every agent edit.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Moved to another window (the page popped out or brought back, Views/PopOut): it is loaded there before this
        // runs, so it keeps its text, undo, caret and scroll. Really gone: detached as before.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) DetachVm();
        }));
    }

    // ─── in a window of its own (Views/PopOut): no docked chat — the owner types in the main window's chat ───

    private bool _chatHidden;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PopOut.PagePopOut.IsFloatingProperty) ApplyFloating(e.NewValue is true);
    }

    /// <summary>Floating: the chat panel, its grip and its splitter go, and the docked ChatView with them (it is not
    /// built at all there); back in the main window the chat docks again.</summary>
    private void ApplyFloating(bool floating)
    {
        if (floating == _chatHidden) return;
        _chatHidden = floating;
        if (floating) EditorChatHost.ClearValue(ContentControl.ContentProperty);
        else EditorChatHost.SetBinding(ContentControl.ContentProperty, new Binding(nameof(CodeEditorViewModel.ChatVM)));
        EditorChatPanel.Visibility = floating ? Visibility.Collapsed : Visibility.Visible;
        ChatGrip.Visibility = floating ? Visibility.Collapsed : Visibility.Visible;
        if (floating) ChatSplitter.Visibility = Visibility.Collapsed;
        FitSidePanels();   // (lays the panels out again: a portrait window stacks the chat only when there is one)
    }

    /// <summary>Is the docked chat shown (false in the page's own window)?</summary>
    public bool ShowsDockedChat => !_chatHidden;

    private void AttachVm(CodeEditorViewModel vm)
    {
        if (ReferenceEquals(_vm, vm) && _hooked) return;
        DetachVm();
        _hooked = true;
        _vm = vm;
        vm.EditorOnScreen = IsVisible;
        CluadeX.Services.Pet.PetZones.Register(Editor);   // the desktop pet keeps off the code being typed
        vm.ScrollToLineRequested += OnScrollToLine;
        vm.AgentEditFlashRequested += OnAgentEditFlash;
        vm.AgentTypingFrame += OnAgentTypingFrame;
        // The terminal box has no Text binding: it is fed chunk by chunk from here (see OnTerminalText).
        TerminalOut.IsUndoEnabled = false;   // each AppendText would otherwise keep an undo record forever
        TerminalOut.Text = vm.IsTerminalOpen ? vm.TerminalOutput : "";
        vm.TerminalTextAppended += OnTerminalText;
        vm.SelectRangeRequested += OnSelectRange;
        vm.SearchFocusRequested += OnSearchFocusRequested;
        vm.RenameFocusRequested += OnRenameFocusRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.BreakpointsChanged += OnBreakpointsChanged;
        vm.ExecutionPointerMoved += OnExecutionPointerMoved;
        // Theme switches while this page is open; undone in DetachVm (the service is a singleton, a forgotten
        // handler would keep every rebuilt editor alive).
        _themes = ThemeService.Current;
        if (_themes != null) _themes.ThemeChanged += OnThemeChanged;
        BindActiveTab(TextTab(vm.ActiveTab));
        AttachMedia(vm);
        ApplyViewPrefs();
        // A theme picked while this view was not loaded: catch up on what the resource system cannot reach.
        ApplyTheme();

        // A finding opened from Security Shield before this view existed: jump to its line now.
        int reveal = vm.ConsumePendingReveal(_boundTab?.FullPath);
        if (reveal > 0) OnSelectRange(reveal, 0, 0);
    }

    private void DetachVm()
    {
        var vm = _vm;
        if (vm != null)
        {
            vm.ScrollToLineRequested -= OnScrollToLine;
            vm.AgentEditFlashRequested -= OnAgentEditFlash;
            vm.AgentTypingFrame -= OnAgentTypingFrame;
            vm.TerminalTextAppended -= OnTerminalText;
            vm.SelectRangeRequested -= OnSelectRange;
            vm.SearchFocusRequested -= OnSearchFocusRequested;
            vm.RenameFocusRequested -= OnRenameFocusRequested;
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.BreakpointsChanged -= OnBreakpointsChanged;
            vm.ExecutionPointerMoved -= OnExecutionPointerMoved;
        }
        DetachMedia();
        if (_themes != null) _themes.ThemeChanged -= OnThemeChanged;
        _themes = null;
        // The tab objects belong to the VM too — let go of the one we listen to. The editor is
        // emptied so a re-attach starts from the VM's state, not from this view's leftovers.
        FlushDeferredSync();   // typing still waiting to be copied would be emptied with the editor
        if (_boundTab != null) _boundTab.PropertyChanged -= OnTabContentChanged;
        _boundTab = null;
        _suppressSync = true;
        try { Editor.Clear(); _lastSynced = ""; }
        catch { /* the control may already be torn down */ }
        finally { _suppressSync = false; }
        _vm = null;
        _hooked = false;
        _glowTimer?.Stop();
        _glowTimer = null;
        _completionWindow?.Close();
        StopToolTimers();
    }

    private bool _editorInitialized;

    /// <summary>Editor chrome and the editor's own event handlers — per view instance, once. These
    /// only reference this view's controls, so they die with it.</summary>
    private void InitializeEditorOnce()
    {
        if (_editorInitialized) return;
        _editorInitialized = true;

        // Code navigation lives on the editor's own key handler so it can read the caret.
        Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
        // A keystroke during the agent's typing reveal: settle the reveal first (see OnUserEdits).
        Editor.TextArea.PreviewTextInput += (_, _) => SettleAgentTyping();
        // TextEntered fires only for real keyboard input, never for the agent's programmatic
        // Document.Replace — so the popup can't gatecrash a live-typing reveal.
        Editor.TextArea.TextEntered += OnTextEntered;

        // Editor chrome XAML can't reach (TextArea exists only at runtime). The selection is a dependency
        // property, so a resource reference follows theme switches by itself; the caret brush is a plain
        // property and is re-applied in ApplyTheme.
        Editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "Editor.Selection");
        Editor.TextArea.Caret.CaretBrush = ThemeService.BrushOf("Editor.Caret", CaretFallback);
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_glow);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_executionLine);

        // Breakpoint gutter. State lives in the VM (keyed by path) so breakpoints survive
        // closing and reopening a file — the margin only asks and reports.
        _breakpointMargin.IsBreakpoint = line =>
            _boundTab != null && _vm != null && _vm.BreakpointsFor(_boundTab.FullPath).Contains(line);
        _breakpointMargin.ToggleRequested = line =>
        {
            if (_boundTab != null) _vm?.ToggleBreakpoint(_boundTab.FullPath, line);
        };
        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);

        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextChanged += OnEditorTextChanged;
        Minimap.Attach(Editor);
        InitializeTools();
    }

    private void OnSearchFocusRequested() => FocusBox(SearchBox, selectAll: true);
    private void OnRenameFocusRequested() => FocusBox(RenameBox, selectAll: true);

    private void OnThemeChanged(string themeId)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ApplyTheme)); return; }
        ApplyTheme();
    }

    /// <summary>
    /// The editor colours the resource system cannot reach by itself: the caret (a plain property, not a
    /// dependency property), the syntax colours (AvalonEdit's shared highlighting definitions, re-tinted from the
    /// Syntax.* tokens), and everything painted in code — breakpoint gutter, minimap, agent glow, execution line —
    /// which reads its token when it draws, so a repaint is all it needs. XAML-side colours are DynamicResource
    /// and follow the switch on their own.
    /// </summary>
    private void ApplyTheme()
    {
        if (!_editorInitialized) return;
        Editor.TextArea.Caret.CaretBrush = ThemeService.BrushOf("Editor.Caret", CaretFallback);
        if (_boundTab != null)
        {
            var definition = HighlightingFor(_boundTab);   // re-tints for a new palette
            // The same definition object comes back, so clear first: the colorizer re-reads its colours only
            // when the highlighting is replaced.
            Editor.SyntaxHighlighting = null;
            Editor.SyntaxHighlighting = definition;
        }
        ApplyToolTheme();
        Editor.TextArea.TextView.Redraw();
        _breakpointMargin.InvalidateVisual();
        Minimap.InvalidateVisual();
    }

    private void OnBreakpointsChanged(string path)
    {
        if (_boundTab != null
            && string.Equals(path, _boundTab.FullPath, StringComparison.OrdinalIgnoreCase))
            _breakpointMargin.Refresh();
    }

    /// <summary>
    /// The user is about to change the buffer while the agent's typing reveal is animating it: jump
    /// the reveal to its final text first. The reveal ends by setting the whole final text, so a
    /// keystroke typed mid-animation would be overwritten by it.
    /// </summary>
    private void SettleAgentTyping()
    {
        if (_vm != null && _vm.IsAgentTypingInto(_boundTab)) _vm.FinishAgentTyping();
    }

    /// <summary>Ctrl+S saves the active tab, Ctrl+Shift+S saves every tab — anywhere on the page.</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandlePageToolKeys(e)) { e.Handled = true; return; }
        if (_vm == null || e.Key != Key.S) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) == 0 || (mods & ModifierKeys.Alt) != 0) return;
        e.Handled = true;
        var cmd = (mods & ModifierKeys.Shift) != 0 ? _vm.SaveAllCommand : _vm.SaveActiveCommand;
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveTab))
        {
            BindActiveTab(TextTab(_vm?.ActiveTab));
            ShowMediaFor(_vm?.ActiveTab);
        }
        else OnMediaVmPropertyChanged(e.PropertyName);
    }

    /// <summary>The tab AvalonEdit shows: a media tab has no text, so the editor is emptied for it (the media area shows).</summary>
    private static OpenFileTab? TextTab(OpenFileTab? tab) => tab is MediaFileTab ? null : tab;

    /// <summary>Point the single editor control at a different tab: swap content, highlighting, glow.</summary>
    private void BindActiveTab(OpenFileTab? tab)
    {
        if (ReferenceEquals(_boundTab, tab)) return;
        FlushDeferredSync();   // typing still waiting to be copied belongs to the tab we are leaving
        if (_boundTab != null)
        {
            _boundTab.PropertyChanged -= OnTabContentChanged;
            if (_boundTab.PendingEdits == FlushDeferredSync) _boundTab.PendingEdits = null;
        }
        _boundTab = tab;
        // The editor is reloaded from the tab below: a reveal still running starts over from what Content holds.
        _lastSyncedStale = false;
        _revealInsert = null;
        _revealShown = 0;
        _noMatchStem = null;   // a word with no completions belongs to the file we are leaving
        _noMatchVersion = null;   // a held version keeps every later change (and its text) alive
        _glow.Clear();
        Minimap.ClearChangeMarkers();   // markers belong to the file we're leaving

        // Breakpoints and the execution pointer are per-file — repaint for the new one.
        _breakpointMargin.Refresh();
        bool stoppedHere = tab != null && _vm != null
            && string.Equals(_executionFile, tab.FullPath, StringComparison.OrdinalIgnoreCase);
        _executionLine.Line = stoppedHere ? _vm!.ExecutionLine : 0;
        _breakpointMargin.ExecutionLine = _executionLine.Line;

        _suppressSync = true;
        try
        {
            if (tab == null)
            {
                Editor.Clear();
                _lastSynced = "";
                return;
            }
            tab.PropertyChanged += OnTabContentChanged;
            tab.PendingEdits = FlushDeferredSync;
            _largeBuffer = tab.Content.Length >= LargeBufferChars;
            Editor.Text = tab.Content;
            _lastSynced = tab.Content;
            Editor.SyntaxHighlighting = HighlightingFor(tab);
            Editor.ScrollToHome();
        }
        finally
        {
            _suppressSync = false;
            OnToolsTabBound();
        }
    }

    // ── VM → editor ──
    // Agent edits and the typewriter reveal's first and last states arrive here as Content changes (the frames
    // between come through OnAgentTypingFrame). Applied as an
    // INCREMENTAL Document.Replace (prefix/suffix diff) so highlighting stays cheap, scroll state
    // survives, and the caret rides the insertion point — which is what makes the reveal look typed.
    private void OnTabContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenFileTab.Content) || _suppressSync || _boundTab == null) return;

        string newText = _boundTab.Content;
        // After reveal frames, or while typing waits for the deferred copy, the editor holds more than _lastSynced:
        // diff against what it really shows (one copy per reveal, on its last frame). The pending copy is dropped:
        // the incoming Content replaces exactly what the editor shows, and the timer must not later write a buffer
        // that mixes the two back into the tab.
        bool typingPending = _deferredSync is { IsEnabled: true };
        _deferredSync?.Stop();
        string oldText = _lastSyncedStale || typingPending ? Editor.Text : _lastSynced;
        _lastSyncedStale = false;
        _revealShown = int.MaxValue;   // later frames of the reveal now shown (if any) no longer apply
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) { _lastSynced = newText; return; }
        _noMatchStem = null;   // the text shifts under the stored offset
        _noMatchVersion = null;

        int limit = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < limit && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix
            && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        int oldMid = oldText.Length - prefix - suffix;
        int newMid = newText.Length - prefix - suffix;

        _suppressSync = true;
        try
        {
            Editor.Document.Replace(prefix, oldMid, newText.Substring(prefix, newMid));
            _lastSynced = newText;

            // Agent-driven change (keyboard focus is elsewhere): follow the typing point and glow
            // the chunk that just landed. Each frame restarts the fade, so the glow stays bright
            // while typing and dissolves on its own when the reveal stops.
            if (!Editor.TextArea.IsKeyboardFocusWithin)
            {
                int caret = Math.Min(prefix + newMid, Editor.Document.TextLength);
                Editor.TextArea.Caret.Offset = caret;
                Editor.TextArea.Caret.BringCaretToView();
                if (newMid > 0)
                {
                    _glow.ShowFlash(prefix, newMid);
                    EnsureGlowTimer();
                }
            }
        }
        catch
        {
            // Diff/replace mismatch (e.g. concurrent change) — hard resync rather than desync.
            try { Editor.Text = newText; _lastSynced = newText; } catch { }
        }
        finally { _suppressSync = false; }
    }

    /// <summary>
    /// One frame of the agent's typing reveal: insert the part of <paramref name="insert"/> this editor has not shown
    /// yet at its offset, as a small Document.Insert — no full-file string and no diff per frame. Caret and glow follow
    /// the typing point as they do for a Content change.
    /// </summary>
    private void OnAgentTypingFrame(OpenFileTab tab, int at, string insert, int pos)
    {
        if (!ReferenceEquals(tab, _boundTab)) return;
        if (!ReferenceEquals(insert, _revealInsert)) { _revealInsert = insert; _revealShown = 0; }
        if (pos <= _revealShown) return;

        int from = _revealShown;
        _revealShown = pos;
        _lastSyncedStale = true;
        _suppressSync = true;
        try
        {
            int offset = at + from;
            Editor.Document.Insert(offset, insert.Substring(from, pos - from));
            if (!Editor.TextArea.IsKeyboardFocusWithin)
            {
                Editor.TextArea.Caret.Offset = Math.Min(at + pos, Editor.Document.TextLength);
                Editor.TextArea.Caret.BringCaretToView();
                _glow.ShowFlash(offset, pos - from);
                EnsureGlowTimer();
            }
        }
        catch
        {
            // The buffer moved under the reveal (an edit typed mid-animation): skip the rest of its frames; the
            // final text arrives as a Content change and resyncs the editor.
            _revealShown = int.MaxValue;
        }
        finally { _suppressSync = false; }
    }

    // ── editor → VM ── (the human typing in the editor; preserves IsDirty tracking + Save commands)
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressSync || _boundTab == null) return;
        if (_largeBuffer || Editor.Document.TextLength >= DeferredSyncChars)
        {
            _boundTab.IsDirty = true;
            _deferredSync ??= NewDeferredSyncTimer();
            _deferredSync.Stop();      // restart: copy once the typing pauses
            _deferredSync.Start();
            return;
        }
        _suppressSync = true;
        try
        {
            _lastSynced = Editor.Text;
            _boundTab.Content = _lastSynced;
        }
        finally { _suppressSync = false; }
    }

    private System.Windows.Threading.DispatcherTimer NewDeferredSyncTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        t.Tick += (_, _) => FlushDeferredSync();
        return t;
    }

    /// <summary>Copy typing that is still only in the editor into the tab (large files). Runs after a pause, before
    /// a save (OpenFileTab.FlushPendingEdits) and when the editor switches tab — a no-op when nothing is pending.</summary>
    private void FlushDeferredSync()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(FlushDeferredSync); return; }   // a save off the UI thread
        if (_deferredSync is not { IsEnabled: true }) return;
        _deferredSync.Stop();
        if (_boundTab == null) return;
        _suppressSync = true;
        try
        {
            _lastSynced = Editor.Text;
            _boundTab.Content = _lastSynced;
        }
        finally { _suppressSync = false; }
    }

    private static ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition? HighlightingFor(OpenFileTab tab)
        => tab.Content.Length > NoHighlightChars || HasLongLine(tab.Content, NoHighlightLineChars)
            ? null : NeonHighlighting.ForExtension(tab.Extension);

    /// <summary>A line longer than this opens without syntax colours (VS Code does the same). AvalonEdit colours a
    /// line by splitting its visual elements once per highlighted span — on a 300 KB minified bundle or a one-line
    /// JSON that is quadratic, and the UI thread spun in HighlightingColorizer.ColorizeLine until the app was killed
    /// (2026-10-14: "เปิดไฟล์แล้วค้าง"). A small file can hit this; the size limit above never sees it.</summary>
    private const int NoHighlightLineChars = 10_000;

    /// <summary>True when some line of <paramref name="text"/> is longer than <paramref name="max"/> characters.</summary>
    internal static bool HasLongLine(string text, int max)
    {
        if (text.Length <= max) return false;
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl;
            if (end - start > max) return true;
            if (nl < 0) break;
            start = nl + 1;
        }
        return false;
    }

    // Live-follow: bring the line the agent just changed into view.
    private void OnScrollToLine(int line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                int clamped = Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount));
                Editor.ScrollToLine(clamped);
            }
            catch { /* best-effort scroll */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Reveal complete: flash the whole freshly-typed region (fades out via the renderer).
    private void OnAgentEditFlash(int charStart, int charLength)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (charStart < 0 || charLength <= 0) return;
                _glow.ShowFlash(charStart, charLength);
                EnsureGlowTimer();

                // Flag the changed range on the minimap rail so the edit stays findable
                // after the glow fades.
                int docLen = Editor.Document.TextLength;
                int s = Math.Clamp(charStart, 0, docLen);
                int en = Math.Clamp(charStart + charLength, 0, docLen);
                Minimap.AddChangeMarker(
                    Editor.Document.GetLineByOffset(s).LineNumber,
                    Editor.Document.GetLineByOffset(en).LineNumber);
            }
            catch { /* cosmetic */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Repaints the background layer at ~20fps while the glow is fading, then stops.</summary>
    private void EnsureGlowTimer()
    {
        if (_glowTimer != null) return;
        _glowTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _glowTimer.Tick += (_, _) =>
        {
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            if (!_glow.IsActive)
            {
                _glowTimer?.Stop();
                _glowTimer = null;
            }
        };
        _glowTimer.Start();
    }

    private async void OnNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FileTreeNode node
            && DataContext is CodeEditorViewModel vm)
        {
            if (vm.ToggleNodeCommand is ICommand cmd && cmd.CanExecute(node))
                cmd.Execute(node);
            await Task.Yield();
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.ActiveTab = tab;
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OpenFileTab tab
            && DataContext is CodeEditorViewModel vm)
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // ── Branch switcher: commit the choice deliberately ──

    private static void CommitBranchChoice(object sender)
    {
        if (sender is ComboBox box)
            BindingOperations.GetBindingExpression(box, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.UpdateSource();
    }

    private void OnBranchDropDownClosed(object? sender, EventArgs e) => CommitBranchChoice(sender!);

    private void OnBranchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitBranchChoice(sender);
        e.Handled = true;
    }

    /// <summary>Walked away after arrowing to another branch without committing: show the real one again.</summary>
    private void OnBranchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false } box)
            BindingOperations.GetBindingExpression(box, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.UpdateTarget();
    }

    // ── Embedded terminal ──

    private void OnTerminalOutputChanged(object sender, TextChangedEventArgs e)
    {
        // Follow the tail like a real terminal.
        TerminalOut.ScrollToEnd();
    }

    /// <summary>New terminal output from the view model: appended to the box, or the whole text after the terminal
    /// opened or its oldest output was trimmed. Binding the 200 K transcript instead rebuilt the box 20 times a
    /// second under a noisy command, open or closed (2026-11-02).</summary>
    private void OnTerminalText(string text, bool replace)
    {
        if (replace) TerminalOut.Text = text;
        else TerminalOut.AppendText(text);
    }

    // ── Debug console ──

    private void OnDebugOutputChanged(object sender, TextChangedEventArgs e) => DebugOut.ScrollToEnd();

    private void OnDebugEvalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not CodeEditorViewModel vm) return;
        vm.EvaluateDebugCommand.Execute(null);
        e.Handled = true;
    }

    private void OnTerminalInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is CodeEditorViewModel vm)
        {
            vm.RunTerminalCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+C in the input line stops what is running (like a console), unless text is
    /// selected — then it is the usual Copy. Preview: the TextBox's own Copy binding would eat it.</summary>
    private void OnTerminalInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0
            || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (sender is TextBox box && box.SelectionLength > 0) return;
        if (DataContext is CodeEditorViewModel vm)
        {
            vm.InterruptTerminalCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Debugger: execution pointer ──

    /// <summary>File the debugger is currently stopped in ("" = not stopped).</summary>
    private string _executionFile = "";

    private void OnExecutionPointerMoved(string file, int line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _executionFile = file ?? "";
                bool here = _boundTab != null && line > 0
                    && string.Equals(_executionFile, _boundTab.FullPath, StringComparison.OrdinalIgnoreCase);

                _executionLine.Line = here ? line : 0;
                _breakpointMargin.ExecutionLine = _executionLine.Line;
                _breakpointMargin.Refresh();
                Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

                if (here) Editor.ScrollToLine(Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount)));
            }
            catch { /* the pointer is a hint, never a crash */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Search panel + code navigation ──

    /// <summary>The symbol the navigation commands act on: the selection if there is one, else the
    /// word under the caret (so F12 works from "put the cursor in the name and press it").</summary>
    private string SymbolAtCaret()
    {
        string selected = Editor.SelectedText;
        if (!string.IsNullOrWhiteSpace(selected) && selected.Length <= 200 && !selected.Contains('\n'))
            return selected.Trim();
        return CluadeX.Services.CodeIntelligenceService.WordAt(Editor.Text, Editor.CaretOffset);
    }

    private async void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // Editing keys that never produce TextInput (Backspace, Delete, Enter, Ctrl+V/X, Tab) must
        // also settle a running reveal before they act.
        if (e.Key is Key.Back or Key.Delete or Key.Enter or Key.Tab
            || (ctrl && e.Key is Key.V or Key.X or Key.Z or Key.Y))
            SettleAgentTyping();

        // Line tools, comment, case, zoom (CodeEditorView.Tools.cs).
        if (HandleEditorToolKeys(e)) { e.Handled = true; return; }

        try
        {
            // Shift+F12 = find references, F12 = go to definition.
            if (e.Key == Key.F12 && !ctrl)
            {
                e.Handled = true;
                string symbol = SymbolAtCaret();
                if (shift) await vm.FindReferencesAsync(symbol);
                else
                    await vm.GoToDefinitionAsync(symbol, _boundTab?.FullPath,
                        Editor.TextArea.Caret.Line - 1, Editor.TextArea.Caret.Column - 1);
            }
            else if (e.Key == Key.F2 && !ctrl && !shift)
            {
                e.Handled = true;
                vm.BeginRename(SymbolAtCaret());
            }
            else if (e.Key == Key.Space && ctrl && !shift)
            {
                e.Handled = true;
                await ShowCompletionAsync(explicitRequest: true);
            }
        }
        catch (Exception ex)
        {
            // async void — an escaped exception would hit the global crash dialog.
            CommandErrorSink.Report(nameof(OnEditorPreviewKeyDown), ex);
        }
    }

    // ── Autocomplete ──

    /// <summary>
    /// Typing a word character opens the popup once there are 2+ characters to go on. Deliberately
    /// conservative: firing on the first keystroke turns every variable name into a fight with a
    /// popup, and firing on punctuation pops it open in the middle of strings and operators.
    /// </summary>
    private async void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        try
        {
            if (_completionWindow != null) return;             // already open — it filters itself
            if (e.Text.Length != 1) return;
            char c = e.Text[0];
            if (!char.IsLetter(c) && c != '_') return;

            await ShowCompletionAsync(explicitRequest: false);
        }
        catch (Exception ex)
        {
            CommandErrorSink.Report(nameof(OnTextEntered), ex);
        }
    }

    /// <summary>True when every edit between the stored no-match version and <paramref name="current"/> is an
    /// insertion at the end of the stem's word, running from <paramref name="stemEnd"/> up to the caret — the user
    /// only kept typing that word. Any other edit (a paste, a deletion, typing elsewhere) may have added a match.
    /// The change log walk is cheap: it lists the edits, it does not copy the text.</summary>
    private bool OnlyGrewStem(ICSharpCode.AvalonEdit.Document.ITextSourceVersion? current, int stemEnd, int caret)
    {
        var stored = _noMatchVersion;
        if (stored == null || current == null || !stored.BelongsToSameDocumentAs(current)) return false;
        if (stored.CompareAge(current) > 0) return false;
        int end = stemEnd;
        foreach (var change in stored.GetChangesTo(current))
        {
            if (change.RemovalLength != 0 || change.Offset != end) return false;
            end += change.InsertionLength;
        }
        return end == caret;
    }

    private async Task ShowCompletionAsync(bool explicitRequest)
    {
        if (DataContext is not CodeEditorViewModel vm || _boundTab == null) return;
        if (_largeBuffer && !explicitRequest) return;   // each try would copy and scan the whole file

        // The prefix is read char by char back from the caret, capped at MaxPrefixChars (no real identifier is
        // longer): an automatic try that stops here copies neither the file nor the line, so a one-line minified
        // .js or JSON costs no Large Object Heap string per letter.
        int caret = Editor.CaretOffset;
        var doc = Editor.Document;
        int wordStart = caret;
        int floor = Math.Max(0, caret - MaxPrefixChars);
        while (wordStart > floor && IsWordChar(doc.GetCharAt(wordStart - 1))) wordStart--;
        string prefix = doc.GetText(wordStart, caret - wordStart);
        if (!explicitRequest)
        {
            if (prefix.Length < 2) return;
            // More letters on a word that already found nothing find nothing either (every candidate starts with,
            // or hump-matches, what was typed), so a new name, a comment or Thai text costs one copy per word.
            if (_noMatchStem != null && wordStart == _noMatchStart && !vm.IsLspConnected
                && prefix.StartsWith(_noMatchStem, StringComparison.Ordinal)
                && OnlyGrewStem(doc.Version, wordStart + _noMatchStem.Length, caret)) return;
        }
        _noMatchStem = null;
        _noMatchVersion = null;

        // Copied only now that a request is really sent.
        string text = Editor.Text;
        var version = Editor.Document.Version;
        var tab = _boundTab;

        // Only the newest request may open a window — the user keeps typing while the scan runs.
        int request = ++_completionRequest;

        var items = await vm.GetCompletionsAsync(tab.FullPath, text, caret);

        if (request != _completionRequest) return;             // superseded by a later keystroke
        if (items.Count == 0)
        {
            // Only the buffer, keyword and symbol-index sources stay the same while the word grows; a language
            // server still loading the project (or a call that failed) can answer nothing now and something later.
            if (!explicitRequest && text.Length >= DeferredSyncChars && ReferenceEquals(tab, _boundTab)
                && !vm.IsLspConnected)
            {
                _noMatchStem = prefix;
                _noMatchStart = wordStart;
                _noMatchVersion = version;   // the version the prefix and the copied text were read from
            }
            return;
        }
        if (_completionWindow != null) return;                 // one opened in the meantime

        // The caret moved (arrow keys, a click, the agent edited) or the text changed — the candidates are for a
        // position that no longer exists, so showing them would insert text in the wrong place. The document's
        // version object changes with every edit, so this needs no second copy of the file.
        if (!ReferenceEquals(tab, _boundTab) || Editor.CaretOffset != caret) return;
        if (!ReferenceEquals(Editor.Document.Version, version)) return;

        var window = new CompletionWindow(Editor.TextArea)
        {
            // Replace the whole partial word, not just the character that triggered us.
            StartOffset = caret - prefix.Length,
            EndOffset = caret,
            CloseAutomatically = true,
            SizeToContent = System.Windows.SizeToContent.Height,
            MaxHeight = 260,
            Width = 340,
        };

        foreach (var item in items)
            window.CompletionList.CompletionData.Add(new CompletionData(item));

        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);

        window.Closed += (_, _) => _completionWindow = null;
        _completionWindow = window;
        window.Show();
    }

    /// <summary>Ctrl+Shift+F anywhere on the page opens Search with the editor selection prefilled.</summary>
    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm) return;

        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0
                           && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = true;
            string selected = Editor.SelectedText;
            if (!string.IsNullOrWhiteSpace(selected) && !selected.Contains('\n'))
                vm.SearchText = selected.Trim();
            vm.ShowSearchCommand.Execute(null);
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.RunSearchCommand.Execute(null);   // re-run immediately instead of waiting out the debounce
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.ClearSearchCommand.Execute(null);
            Editor.TextArea.Focus();
        }
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // Enter previews; it never writes to disk on its own — applying is a separate,
            // confirmed click. A rename that fires on one keystroke is how repos get shredded.
            vm.PreviewRenameCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelRenameCommand.Execute(null);
            Editor.TextArea.Focus();
        }
    }

    private void FocusBox(TextBox box, bool selectAll)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                box.Focus();
                Keyboard.Focus(box);
                if (selectAll) box.SelectAll();
            }
            catch { /* focus is best-effort */ }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Jump to a search result: scroll the line into view and select the matched span so the
    /// hit is visible in the editor, not just in the list.</summary>
    private void OnSelectRange(int line, int column, int length)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var doc = Editor.Document;
                if (doc.LineCount == 0) return;

                int clampedLine = Math.Clamp(line, 1, doc.LineCount);
                var docLine = doc.GetLineByNumber(clampedLine);
                int offset = docLine.Offset + Math.Clamp(column, 0, docLine.Length);
                int len = Math.Clamp(length, 0, Math.Max(0, docLine.EndOffset - offset));

                Editor.ScrollToLine(clampedLine);
                Editor.CaretOffset = offset;
                if (len > 0) Editor.Select(offset, len);
                else Editor.TextArea.ClearSelection();
                Editor.TextArea.Caret.BringCaretToView();
                Editor.TextArea.Focus();
            }
            catch { /* the file may have changed under us — never crash on a navigation */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Review the result like a human would — HTML/SVG/Markdown/images in their viewer, anything
    // else pointed at in Explorer. The VM owns the rule (SafeShell): this used to shell-open ANY
    // file, which RUNS a .js/.vbs/.bat/.py from whatever repo is open.
    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm && vm.PreviewActiveCommand.CanExecute(null))
            vm.PreviewActiveCommand.Execute(null);
    }
}
