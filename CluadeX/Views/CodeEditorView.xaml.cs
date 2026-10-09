using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

public partial class CodeEditorView : UserControl
{
    private readonly AgentGlowRenderer _glow = new();
    private System.Windows.Threading.DispatcherTimer? _glowTimer;
    private CodeEditorViewModel? _vm;
    private OpenFileTab? _boundTab;
    private string _lastSynced = "";
    private bool _suppressSync;
    private bool _hooked;
    private CompletionWindow? _completionWindow;
    private int _completionRequest;
    private readonly BreakpointMargin _breakpointMargin = new();
    private readonly ExecutionLineRenderer _executionLine = new();

    public CodeEditorView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        // Preview, so Ctrl+S reaches us even when the focused control (editor, terminal box) would
        // otherwise swallow the key.
        PreviewKeyDown += OnPagePreviewKeyDown;
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
    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachVm();

    private void AttachVm(CodeEditorViewModel vm)
    {
        if (ReferenceEquals(_vm, vm) && _hooked) return;
        DetachVm();
        _hooked = true;
        _vm = vm;
        vm.ScrollToLineRequested += OnScrollToLine;
        vm.AgentEditFlashRequested += OnAgentEditFlash;
        vm.SelectRangeRequested += OnSelectRange;
        vm.SearchFocusRequested += OnSearchFocusRequested;
        vm.RenameFocusRequested += OnRenameFocusRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.BreakpointsChanged += OnBreakpointsChanged;
        vm.ExecutionPointerMoved += OnExecutionPointerMoved;
        BindActiveTab(vm.ActiveTab);

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
            vm.SelectRangeRequested -= OnSelectRange;
            vm.SearchFocusRequested -= OnSearchFocusRequested;
            vm.RenameFocusRequested -= OnRenameFocusRequested;
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.BreakpointsChanged -= OnBreakpointsChanged;
            vm.ExecutionPointerMoved -= OnExecutionPointerMoved;
        }
        // The tab objects belong to the VM too — let go of the one we listen to. The editor is
        // emptied so a re-attach starts from the VM's state, not from this view's leftovers.
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

        // Editor chrome XAML can't reach (TextArea exists only at runtime)
        Editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xDF, 0xFF));
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xA6, 0x72, 0xFF));
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
    }

    private void OnSearchFocusRequested() => FocusBox(SearchBox, selectAll: true);
    private void OnRenameFocusRequested() => FocusBox(RenameBox, selectAll: true);

    private void OnBreakpointsChanged(string path)
    {
        if (_boundTab != null
            && string.Equals(path, _boundTab.FullPath, StringComparison.OrdinalIgnoreCase))
            _breakpointMargin.Refresh();
    }

    /// <summary>
    /// The user is about to change the buffer while the agent's typing reveal is animating it: jump
    /// the reveal to its final text first. Each reveal frame replaces the whole buffer every 16 ms,
    /// so a keystroke typed mid-animation was overwritten by the next frame.
    /// </summary>
    private void SettleAgentTyping()
    {
        if (_vm != null && _vm.IsAgentTypingInto(_boundTab)) _vm.FinishAgentTyping();
    }

    /// <summary>Ctrl+S saves the active tab, Ctrl+Shift+S saves every tab — anywhere on the page.</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            BindActiveTab(_vm?.ActiveTab);
    }

    /// <summary>Point the single editor control at a different tab: swap content, highlighting, glow.</summary>
    private void BindActiveTab(OpenFileTab? tab)
    {
        if (ReferenceEquals(_boundTab, tab)) return;
        if (_boundTab != null) _boundTab.PropertyChanged -= OnTabContentChanged;
        _boundTab = tab;
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
            Editor.Text = tab.Content;
            _lastSynced = tab.Content;
            Editor.SyntaxHighlighting = NeonHighlighting.ForExtension(tab.Extension);
            Editor.ScrollToHome();
        }
        finally { _suppressSync = false; }
    }

    // ── VM → editor ──
    // Agent edits and the typewriter-reveal frames arrive here as Content changes. Applied as an
    // INCREMENTAL Document.Replace (prefix/suffix diff) so highlighting stays cheap, scroll state
    // survives, and the caret rides the insertion point — which is what makes the reveal look typed.
    private void OnTabContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenFileTab.Content) || _suppressSync || _boundTab == null) return;

        string newText = _boundTab.Content;
        string oldText = _lastSynced;
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;

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

    // ── editor → VM ── (the human typing in the editor; preserves IsDirty tracking + Save commands)
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressSync || _boundTab == null) return;
        _suppressSync = true;
        try
        {
            _lastSynced = Editor.Text;
            _boundTab.Content = _lastSynced;
        }
        finally { _suppressSync = false; }
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

    private async Task ShowCompletionAsync(bool explicitRequest)
    {
        if (DataContext is not CodeEditorViewModel vm || _boundTab == null) return;

        string text = Editor.Text;
        int caret = Editor.CaretOffset;
        string prefix = CluadeX.Services.CodeIntelligenceService.PrefixAt(text, caret);
        if (!explicitRequest && prefix.Length < 2) return;

        // Only the newest request may open a window — the user keeps typing while the scan runs.
        int request = ++_completionRequest;

        var items = await vm.GetCompletionsAsync(_boundTab.FullPath, text, caret);

        if (request != _completionRequest) return;             // superseded by a later keystroke
        if (_completionWindow != null) return;                 // one opened in the meantime
        if (items.Count == 0) return;

        // The caret moved (arrow keys, a click, the agent edited) — the candidates are for a
        // position that no longer exists, so showing them would insert text in the wrong place.
        if (Editor.CaretOffset != caret) return;
        if (!ReferenceEquals(Editor.Text, text) && Editor.Text != text) return;

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
