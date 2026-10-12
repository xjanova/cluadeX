using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Helpers;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

/// <summary>
/// The editor's standard tools (owner 2026-10-12: "หน้า editor ทำไมไม่มีเครื่องมือมาตรฐาน … เซฟตรงไหน … ถ้าเราทำฉลาดเหมือน
/// notepad++"): the toolbar, find / replace / go to line, the line tools, comment toggling, folding, smart
/// highlighting, zoom, word wrap, whitespace, and the status bar (position, selection, line ending, encoding).
/// Every text change goes through the document as ONE edit, so a single Ctrl+Z takes it back.
/// </summary>
public partial class CodeEditorView
{
    private const double BaseFontSize = 13;
    private const int TabSize = 4;

    private readonly SearchHighlightRenderer _marks = new();
    private FoldingManager? _folding;
    private EditorFolding.Kind _foldKind;
    private DispatcherTimer? _foldTimer;
    private DispatcherTimer? _countTimer;
    private Regex? _findRegex;
    private EditorOps.Eol _eol = EditorOps.Eol.CrLf;
    private bool _toolsReady;

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(L(key), args);

    // ───────────────────────── wiring ─────────────────────────

    /// <summary>From InitializeEditorOnce: hooks that only touch this view's controls.</summary>
    private void InitializeTools()
    {
        if (_toolsReady) return;
        _toolsReady = true;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_marks);
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatusBar();
        Editor.TextArea.SelectionChanged += (_, _) => { UpdateStatusBar(); UpdateSmartHighlight(); };
        Editor.TextChanged += (_, _) => OnToolsTextChanged();
        Editor.PreviewMouseWheel += OnEditorMouseWheel;
        // INS / OVR: the Insert key flips it. (Not DependencyPropertyDescriptor.AddValueChanged — that registry holds
        // the editor forever, and this view is rebuilt on every visit to the page.)
        Editor.TextArea.PreviewKeyUp += (_, e) => { if (e.Key == Key.Insert) UpdateStatusBar(); };
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableRectangularSelection = true;   // Alt+drag: column mode
        Editor.Options.AllowToggleOverstrikeMode = true;    // Insert key
        Editor.Options.IndentationSize = TabSize;
        _folding = FoldingManager.Install(Editor.TextArea);
        ApplyToolTheme();
    }

    /// <summary>The view toggles the user left on (word wrap, whitespace, zoom) — from the VM's settings.</summary>
    private void ApplyViewPrefs()
    {
        if (_vm == null) return;
        Editor.WordWrap = _vm.EditorWordWrap;
        TbWrap.IsChecked = Editor.WordWrap;
        SetWhitespace(_vm.EditorShowWhitespace);
        SetZoom(_vm.EditorZoom, remember: false);
    }

    /// <summary>The tab shown changed (BindActiveTab): fresh foldings, line ending, find marks, status.</summary>
    private void OnToolsTabBound()
    {
        if (!_toolsReady) return;
        _folding?.Clear();
        _foldKind = _boundTab == null || _largeBuffer ? EditorFolding.Kind.None : EditorFolding.For(_boundTab.FullPath);
        ScheduleFolding(immediate: true);
        _eol = _boundTab == null ? EditorOps.Eol.CrLf : EditorOps.DetectEol(_boundTab.Content);
        _marks.Smart = null;
        if (FindBar.Visibility == Visibility.Visible) RefreshFind(jump: false);
        UpdateStatusBar();
    }

    private void OnToolsTextChanged()
    {
        if (!_toolsReady) return;
        ScheduleFolding(immediate: false);
        if (FindBar.Visibility == Visibility.Visible) ScheduleCount();
        UpdateStatusBar();
    }

    /// <summary>Unloaded: timers stop (they hold this view).</summary>
    private void StopToolTimers()
    {
        _foldTimer?.Stop();
        _countTimer?.Stop();
    }

    /// <summary>Theme switch: the colours AvalonEdit holds as plain properties.</summary>
    private void ApplyToolTheme()
    {
        Editor.TextArea.TextView.CurrentLineBackground = ThemeService.BrushOf("State.Hover");
        Editor.TextArea.TextView.CurrentLineBorder = null;
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.SetResourceReference(FoldingMargin.FoldingMarkerBrushProperty, "Editor.LineNumber");
            margin.SetResourceReference(FoldingMargin.FoldingMarkerBackgroundBrushProperty, "Editor.Background");
            margin.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBrushProperty, "Accent.1");
            margin.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, "Editor.Background");
        }
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // ───────────────────────── keyboard ─────────────────────────

    /// <summary>Page-wide shortcuts (focus anywhere but the docked chat): file and find. True = handled.</summary>
    private bool HandlePageToolKeys(KeyEventArgs e)
    {
        if (_vm == null || EditorChatPanel.IsKeyboardFocusWithin) return false;
        var mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        bool tab = _boundTab != null;

        if (ctrl && alt && !shift && key == Key.S && tab) { OnSaveAs(this, new RoutedEventArgs()); return true; }
        if (alt) return false;
        if (ctrl && !shift && key == Key.N) { OnNewFile(this, new RoutedEventArgs()); return true; }
        if (ctrl && !shift && key == Key.O) { OnOpenFile(this, new RoutedEventArgs()); return true; }
        if (ctrl && !shift && key == Key.W && tab) { _vm.CloseActiveTab(); return true; }
        if (ctrl && !shift && key == Key.F && tab) { OpenFindBar(replace: false); return true; }
        if (ctrl && !shift && key == Key.H && tab) { OpenFindBar(replace: true); return true; }
        if (ctrl && !shift && key == Key.G && tab) { OpenGoTo(); return true; }
        if (!ctrl && key == Key.F3 && tab)
        {
            if (FindBox.Text.Length == 0) OpenFindBar(replace: false);
            else FindStep(backwards: shift);
            return true;
        }
        if (key == Key.Escape && FindBar.Visibility == Visibility.Visible
            && (FindBar.IsKeyboardFocusWithin || Editor.TextArea.IsKeyboardFocusWithin))
        {
            CloseFindBar();
            return true;
        }
        return false;
    }

    /// <summary>Editing shortcuts while the editor has the keyboard. True = handled.</summary>
    private bool HandleEditorToolKeys(KeyEventArgs e)
    {
        if (_boundTab == null) return false;
        var mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;

        if (alt && !ctrl && !shift && key == Key.Up) { MoveLines(up: true); return true; }
        if (alt && !ctrl && !shift && key == Key.Down) { MoveLines(up: false); return true; }
        if (alt && !ctrl && !shift && key == Key.Z) { ToggleWrap(); return true; }
        if (!ctrl || alt) return false;
        switch (key)
        {
            case Key.Up when shift: MoveLines(up: true); return true;
            case Key.Down when shift: MoveLines(up: false); return true;
            case Key.D when !shift: DuplicateLines(); return true;
            case Key.L when shift: DeleteLines(); return true;
            case Key.J when !shift: JoinSelectedLines(); return true;
            case Key.U: ChangeCase(shift ? EditorOps.CaseKind.Upper : EditorOps.CaseKind.Lower); return true;
            case Key.OemQuestion or Key.Divide or Key.Q when !shift: ToggleComment(); return true;
            case Key.OemPlus or Key.Add: SetZoom(Editor.FontSize / BaseFontSize + 0.1); return true;
            case Key.OemMinus or Key.Subtract: SetZoom(Editor.FontSize / BaseFontSize - 0.1); return true;
            case Key.D0 or Key.NumPad0 when !shift: SetZoom(1.0); return true;
        }
        return false;
    }

    private void OnEditorMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetZoom(Editor.FontSize / BaseFontSize + (e.Delta > 0 ? 0.1 : -0.1));
    }

    // ───────────────────────── toolbar: file ─────────────────────────

    private async void OnNewFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("edt.newTitle"),
                InitialDirectory = _vm.DefaultFolder,
                FileName = "untitled.txt",
                OverwritePrompt = false,   // an existing name is opened, never emptied
                Filter = L("edt.allFiles") + "|*.*",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true && await _vm.CreateFileAsync(dlg.FileName))
                FocusEditorSoon();
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnNewFile), ex); }
    }

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L("edt.openTitle"),
                InitialDirectory = _vm.DefaultFolder,
                Multiselect = true,
                Filter = L("edt.allFiles") + "|*.*",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            foreach (var path in dlg.FileNames) await _vm.OpenPathAsync(path);
            FocusEditorSoon();
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnOpenFile), ex); }
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || _boundTab == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("edt.saveAsTitle"),
                InitialDirectory = _vm.DefaultFolder,
                FileName = _boundTab.FileName,
                OverwritePrompt = true,
                Filter = L("edt.allFiles") + "|*.*",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) await _vm.SaveActiveAsAsync(dlg.FileName);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnSaveAs), ex); }
    }

    private void FocusEditorSoon() =>
        Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Background);

    // ───────────────────────── toolbar: edit ─────────────────────────

    private void OnUndo(object sender, RoutedEventArgs e) { if (Editor.CanUndo) Editor.Undo(); Editor.TextArea.Focus(); }
    private void OnRedo(object sender, RoutedEventArgs e) { if (Editor.CanRedo) Editor.Redo(); Editor.TextArea.Focus(); }
    private void OnCut(object sender, RoutedEventArgs e) => RunEditorCommand(ApplicationCommands.Cut);
    private void OnCopy(object sender, RoutedEventArgs e) => RunEditorCommand(ApplicationCommands.Copy);
    private void OnPaste(object sender, RoutedEventArgs e) => RunEditorCommand(ApplicationCommands.Paste);
    private void OnSelectAll(object sender, RoutedEventArgs e) => RunEditorCommand(ApplicationCommands.SelectAll);

    private void RunEditorCommand(RoutedUICommand command)
    {
        if (command.CanExecute(null, Editor.TextArea)) command.Execute(null, Editor.TextArea);
        Editor.TextArea.Focus();
    }

    private void OnToggleComment(object sender, RoutedEventArgs e) => ToggleComment();

    private void OnToolsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu == null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        b.ContextMenu.IsOpen = true;
    }

    private void OnDuplicateLines(object sender, RoutedEventArgs e) => DuplicateLines();
    private void OnDeleteLines(object sender, RoutedEventArgs e) => DeleteLines();
    private void OnMoveLinesUp(object sender, RoutedEventArgs e) => MoveLines(up: true);
    private void OnMoveLinesDown(object sender, RoutedEventArgs e) => MoveLines(up: false);
    private void OnJoinLines(object sender, RoutedEventArgs e) => JoinSelectedLines();
    private void OnUpperCase(object sender, RoutedEventArgs e) => ChangeCase(EditorOps.CaseKind.Upper);
    private void OnLowerCase(object sender, RoutedEventArgs e) => ChangeCase(EditorOps.CaseKind.Lower);
    private void OnTitleCase(object sender, RoutedEventArgs e) => ChangeCase(EditorOps.CaseKind.Title);
    private void OnSortAsc(object sender, RoutedEventArgs e) => ApplyToLines(l => EditorOps.SortLines(l, false, true), wholeIfNone: true);
    private void OnSortDesc(object sender, RoutedEventArgs e) => ApplyToLines(l => EditorOps.SortLines(l, true, true), wholeIfNone: true);
    private void OnRemoveDuplicates(object sender, RoutedEventArgs e) => ApplyToLines(EditorOps.RemoveDuplicateLines, wholeIfNone: true);
    private void OnRemoveEmpty(object sender, RoutedEventArgs e) => ApplyToLines(EditorOps.RemoveEmptyLines, wholeIfNone: true);
    private void OnTrimTrailing(object sender, RoutedEventArgs e) => ApplyToLines(EditorOps.TrimTrailing, wholeIfNone: true);
    private void OnTabsToSpaces(object sender, RoutedEventArgs e) => ApplyToLines(l => EditorOps.TabsToSpaces(l, TabSize), wholeIfNone: true);
    private void OnSpacesToTabs(object sender, RoutedEventArgs e) => ApplyToLines(l => EditorOps.LeadingSpacesToTabs(l, TabSize), wholeIfNone: true);

    private void OnFoldAll(object sender, RoutedEventArgs e) => SetAllFolded(true);
    private void OnUnfoldAll(object sender, RoutedEventArgs e) => SetAllFolded(false);

    private void SetAllFolded(bool folded)
    {
        if (_folding == null) return;
        foreach (var f in _folding.AllFoldings) f.IsFolded = folded;
    }

    // ───────────────────────── line tools ─────────────────────────

    /// <summary>The lines the selection touches (the caret's line when nothing is selected). A selection ending at
    /// the very start of a line does not take that line.</summary>
    private (DocumentLine First, DocumentLine Last) SelectedLines(bool wholeIfNone = false)
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength == 0)
        {
            if (wholeIfNone) return (doc.Lines[0], doc.Lines[^1]);
            var line = doc.GetLineByOffset(Editor.CaretOffset);
            return (line, line);
        }
        int start = Editor.SelectionStart, end = start + Editor.SelectionLength;
        var first = doc.GetLineByOffset(start);
        var last = doc.GetLineByOffset(end);
        if (last.Offset == end && last.LineNumber > first.LineNumber) last = last.PreviousLine;
        return (first, last);
    }

    private string LineBreakAfter(DocumentLine line) =>
        line.DelimiterLength > 0 ? Editor.Document.GetText(line.EndOffset, line.DelimiterLength) : EditorOps.EolText(_eol);

    private string[] TextOfLines(DocumentLine first, DocumentLine last)
    {
        var lines = new List<string>();
        for (var l = first; l != null; l = l.NextLine)
        {
            lines.Add(Editor.Document.GetText(l.Offset, l.Length));
            if (l == last) break;
        }
        return lines.ToArray();
    }

    /// <summary>Run a line operation on the selected lines (or the whole file) as one edit, and select the result.</summary>
    private void ApplyToLines(Func<string[], string[]> op, bool wholeIfNone = false)
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var (first, last) = SelectedLines(wholeIfNone);
        int start = first.Offset, end = last.EndOffset;
        string before = Editor.Document.GetText(start, end - start);
        string after = string.Join(LineBreakAfter(first), op(TextOfLines(first, last)));
        if (after == before) { Say(L("edt.nothingChanged")); return; }
        bool hadSelection = Editor.SelectionLength > 0;
        int caretInBlock = Editor.CaretOffset - start;
        Editor.Document.Replace(start, end - start, after);
        if (hadSelection) Editor.Select(start, after.Length);
        else
        {
            // No selection (Ctrl+/ on the caret's line): the caret rides along with its text — selecting the line
            // would make the next keystroke replace it.
            int shift = first == last ? after.Length - before.Length : 0;
            Editor.Select(start + Math.Clamp(caretInBlock + shift, 0, after.Length), 0);
        }
        Editor.TextArea.Focus();
    }

    private void ToggleComment()
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var syntax = EditorOps.CommentSyntaxFor(_boundTab.FullPath);
        if (syntax.IsNone) { Say(L("edt.noComment")); return; }
        if (syntax.Line != null) { ApplyToLines(l => EditorOps.ToggleLineComment(l, syntax.Line)); return; }
        var (first, last) = SelectedLines();
        int start = first.Offset, end = last.EndOffset;
        string block = Editor.Document.GetText(start, end - start);
        if (block.Trim().Length == 0) return;
        string toggled = EditorOps.ToggleBlockComment(block, syntax.BlockOpen!, syntax.BlockClose!);
        bool hadSelection = Editor.SelectionLength > 0;
        int caretInBlock = Editor.CaretOffset - start;
        Editor.Document.Replace(start, end - start, toggled);
        if (hadSelection) Editor.Select(start, toggled.Length);
        else Editor.Select(start + Math.Clamp(caretInBlock + (toggled.Length - block.Length) / 2, 0, toggled.Length), 0);
        Editor.TextArea.Focus();
    }

    private void DuplicateLines()
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var (first, last) = SelectedLines();
        string block = Editor.Document.GetText(first.Offset, last.EndOffset - first.Offset);
        string insert = LineBreakAfter(last) + block;
        int caretShift = insert.Length;
        bool hadSelection = Editor.SelectionLength > 0;
        int selStart = Editor.SelectionStart, selLen = Editor.SelectionLength, caret = Editor.CaretOffset;
        Editor.Document.Insert(last.EndOffset, insert);
        // The copy is what gets the caret, as in VS Code and Notepad++ — press again for another.
        if (hadSelection) Editor.Select(selStart + caretShift, selLen);
        else Editor.CaretOffset = Math.Min(caret + caretShift, Editor.Document.TextLength);
    }

    private void DeleteLines()
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var (first, last) = SelectedLines();
        int start = first.Offset, end = last.EndOffset + last.DelimiterLength;
        if (last.DelimiterLength == 0 && first.PreviousLine is { } prev)
            start = prev.EndOffset;   // the last line: take the break before it instead
        Editor.Document.Remove(start, end - start);
    }

    private void MoveLines(bool up)
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var (first, last) = SelectedLines();
        var neighbour = up ? first.PreviousLine : last.NextLine;
        if (neighbour == null) return;
        var doc = Editor.Document;
        string block = doc.GetText(first.Offset, last.EndOffset - first.Offset);
        string other = doc.GetText(neighbour.Offset, neighbour.Length);
        int selStart = Editor.SelectionStart, selLen = Editor.SelectionLength, caret = Editor.CaretOffset;
        if (up)
        {
            string brk = LineBreakAfter(neighbour);
            int start = neighbour.Offset, end = last.EndOffset;
            doc.Replace(start, end - start, block + brk + other);
            int shift = -(other.Length + brk.Length);
            Restore(shift);
        }
        else
        {
            string brk = LineBreakAfter(last);
            int start = first.Offset, end = neighbour.EndOffset;
            doc.Replace(start, end - start, other + brk + block);
            Restore(other.Length + brk.Length);
        }

        void Restore(int shift)
        {
            if (selLen > 0) Editor.Select(selStart + shift, selLen);
            else Editor.CaretOffset = Math.Clamp(caret + shift, 0, doc.TextLength);
        }
    }

    private void JoinSelectedLines()
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        var (first, last) = SelectedLines();
        if (first == last)
        {
            if (first.NextLine == null) return;
            last = first.NextLine;
        }
        int start = first.Offset, end = last.EndOffset;
        string joined = EditorOps.JoinLines(TextOfLines(first, last));
        Editor.Document.Replace(start, end - start, joined);
        Editor.CaretOffset = start + joined.Length;
    }

    /// <summary>Upper / lower / title case of the selection — or of the word at the caret when nothing is selected.</summary>
    private void ChangeCase(EditorOps.CaseKind kind)
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        int start = Editor.SelectionStart, length = Editor.SelectionLength;
        if (length == 0)
        {
            var doc = Editor.Document;
            int a = Editor.CaretOffset, b = a;
            while (a > 0 && IsWordChar(doc.GetCharAt(a - 1))) a--;
            while (b < doc.TextLength && IsWordChar(doc.GetCharAt(b))) b++;
            if (b == a) return;
            start = a; length = b - a;
        }
        string text = Editor.Document.GetText(start, length);
        string changed = EditorOps.ChangeCase(text, kind);
        if (changed == text) return;
        Editor.Document.Replace(start, length, changed);
        Editor.Select(start, changed.Length);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ───────────────────────── view toggles ─────────────────────────

    private void OnToggleWrap(object sender, RoutedEventArgs e) => ToggleWrap();

    private void ToggleWrap()
    {
        Editor.WordWrap = !Editor.WordWrap;
        TbWrap.IsChecked = Editor.WordWrap;
        if (_vm != null) _vm.EditorWordWrap = Editor.WordWrap;
    }

    private void OnToggleWhitespace(object sender, RoutedEventArgs e)
    {
        SetWhitespace(!Editor.Options.ShowSpaces);
        if (_vm != null) _vm.EditorShowWhitespace = Editor.Options.ShowSpaces;
    }

    private void SetWhitespace(bool on)
    {
        Editor.Options.ShowSpaces = on;
        Editor.Options.ShowTabs = on;
        Editor.Options.ShowEndOfLine = on;
        TbWhitespace.IsChecked = on;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(Editor.FontSize / BaseFontSize + 0.1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(Editor.FontSize / BaseFontSize - 0.1);
    private void OnZoomReset(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void SetZoom(double zoom, bool remember = true)
    {
        zoom = Math.Clamp(Math.Round(zoom, 1), 0.5, 3.0);
        Editor.FontSize = BaseFontSize * zoom;
        if (remember && _vm != null) _vm.EditorZoom = zoom;
        UpdateStatusBar();
    }

    // ───────────────────────── find / replace / go to ─────────────────────────

    private void OnFind(object sender, RoutedEventArgs e) => OpenFindBar(replace: false);
    private void OnReplace(object sender, RoutedEventArgs e) =>
        OpenFindBar(replace: !(FindBar.Visibility == Visibility.Visible && ReplaceRow.Visibility == Visibility.Visible));
    private void OnGoTo(object sender, RoutedEventArgs e) => OpenGoTo();
    private void OnCloseFindBar(object sender, RoutedEventArgs e) => CloseFindBar();
    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(backwards: false);
    private void OnFindPrev(object sender, RoutedEventArgs e) => FindStep(backwards: true);
    private void OnFindOptionChanged(object sender, RoutedEventArgs e) => RefreshFind(jump: true);
    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => RefreshFind(jump: true);
    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();
    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();
    private void OnGoToApply(object sender, RoutedEventArgs e) => ApplyGoTo();

    public void OpenFindBar(bool replace)
    {
        if (_boundTab == null) return;
        FindBar.Visibility = Visibility.Visible;
        FindRow.Visibility = Visibility.Visible;
        GoToRow.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        string selected = Editor.SelectedText;
        if (selected.Length is > 0 and < 300 && !selected.Contains('\n')) FindBox.Text = selected;
        bool focusReplace = replace && FindBox.Text.Length > 0 && selected.Length > 0;
        Dispatcher.BeginInvoke(() =>
        {
            var box = focusReplace ? ReplaceBox : FindBox;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
        RefreshFind(jump: false);
    }

    private void OpenGoTo()
    {
        if (_boundTab == null) return;
        FindBar.Visibility = Visibility.Visible;
        FindRow.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = Visibility.Collapsed;
        GoToRow.Visibility = Visibility.Visible;
        GoToHint.Text = LF("edt.goToRange", Editor.Document.LineCount.ToString("N0", CultureInfo.CurrentCulture));
        GoToBox.Text = Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
        Dispatcher.BeginInvoke(() => { GoToBox.Focus(); GoToBox.SelectAll(); }, DispatcherPriority.Input);
    }

    public void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _marks.Find = null;
        _countTimer?.Stop();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        Editor.TextArea.Focus();
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; FindStep(backwards: (Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseFindBar(); }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ReplaceAll(); else ReplaceOne();
        }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseFindBar(); }
    }

    private void OnGoToBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ApplyGoTo(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseFindBar(); }
    }

    private EditorOps.FindOptions CurrentFindOptions() =>
        new(FindCase.IsChecked == true, FindWord.IsChecked == true, FindRegex.IsChecked == true);

    /// <summary>The pattern changed (typing, an option): rebuild it, mark the matches on screen, and — as you type —
    /// jump to the first match from where the selection starts.</summary>
    private void RefreshFind(bool jump)
    {
        if (!_toolsReady) return;
        _findRegex = EditorOps.BuildFindRegex(FindBox.Text, CurrentFindOptions(), out string? error);
        _marks.Find = _findRegex;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        if (error != null)
        {
            ShowFindCount(L("edt.regexError"), problem: true);
            FindCount.ToolTip = error;
            return;
        }
        FindCount.ToolTip = null;
        if (_findRegex == null) { ShowFindCount("", problem: false); return; }
        if (jump && FindBar.Visibility == Visibility.Visible)
        {
            var hit = EditorOps.FindNext(Editor.Text, _findRegex, Editor.SelectionStart, backwards: false);
            if (hit is { } h) SelectMatch(h.Index, h.Length);
        }
        ScheduleCount(immediate: true);
    }

    /// <summary>Next / previous match, wrapping around the file.</summary>
    public void FindStep(bool backwards)
    {
        if (_boundTab == null) return;
        if (_findRegex == null) { RefreshFind(jump: false); if (_findRegex == null) return; }
        string text = Editor.Text;
        int selStart = Editor.SelectionStart, selLen = Editor.SelectionLength;
        int from = backwards ? selStart : selLen > 0 ? selStart + selLen : Editor.CaretOffset;
        var hit = EditorOps.FindNext(text, _findRegex, from, backwards);
        // An empty match where the caret already is would never move on.
        if (hit is { Length: 0 } z && z.Index == selStart && !backwards && from < text.Length)
            hit = EditorOps.FindNext(text, _findRegex, from + 1, backwards);
        if (hit is not { } h)
        {
            ShowFindCount(L("edt.noResults"), problem: true);
            return;
        }
        bool wrapped = backwards ? h.Index >= selStart : h.Index < from;
        SelectMatch(h.Index, h.Length);
        ScheduleCount(immediate: true, wrapped: wrapped);
    }

    private void SelectMatch(int index, int length)
    {
        Editor.Select(index, length);
        var loc = Editor.Document.GetLocation(index);
        Editor.ScrollTo(loc.Line, loc.Column);
    }

    /// <summary>Replace the match that is selected and move on to the next one. With no match selected the first
    /// press only finds it, so nothing is replaced unseen.</summary>
    public void ReplaceOne()
    {
        if (_boundTab == null || _findRegex == null) return;
        SettleAgentTyping();
        string text = Editor.Text;
        int selStart = Editor.SelectionStart, selLen = Editor.SelectionLength;
        Match m;
        try { m = _findRegex.Match(text, selStart); }
        catch (RegexMatchTimeoutException) { ShowFindCount(L("edt.regexTimeout"), problem: true); return; }
        if (m.Success && m.Index == selStart && m.Length == selLen && (selLen > 0 || FindRegex.IsChecked == true))
        {
            string with = EditorOps.ReplacementFor(m, ReplaceBox.Text, FindRegex.IsChecked == true);
            Editor.Document.Replace(selStart, selLen, with);
            Editor.Select(selStart + with.Length, 0);
        }
        FindStep(backwards: false);
    }

    /// <summary>Replace every match in the file as one edit (one Ctrl+Z puts them all back).</summary>
    public void ReplaceAll()
    {
        if (_boundTab == null || _findRegex == null) return;
        SettleAgentTyping();
        string text = Editor.Text;
        List<(int Index, int Length, string Text)> edits;
        try { edits = EditorOps.Replacements(text, _findRegex, ReplaceBox.Text, FindRegex.IsChecked == true); }
        catch (RegexMatchTimeoutException) { ShowFindCount(L("edt.regexTimeout"), problem: true); return; }
        if (edits.Count == 0) { ShowFindCount(L("edt.noResults"), problem: true); return; }
        var doc = Editor.Document;
        int caret = Editor.CaretOffset;
        using (doc.RunUpdate())
        {
            if (edits.Count > 20_000)
            {
                var (all, _) = EditorOps.ReplaceAll(text, _findRegex, ReplaceBox.Text, FindRegex.IsChecked == true);
                doc.Replace(0, doc.TextLength, all);
            }
            else
            {
                for (int i = edits.Count - 1; i >= 0; i--) doc.Replace(edits[i].Index, edits[i].Length, edits[i].Text);
            }
        }
        Editor.CaretOffset = Math.Min(caret, doc.TextLength);
        string done = LF("edt.replacedCount", edits.Count.ToString("N0", CultureInfo.CurrentCulture));
        ShowFindCount(done, problem: false);
        Say(done);
    }

    private void ApplyGoTo()
    {
        if (_boundTab == null) return;
        if (!EditorOps.TryParseGoTo(GoToBox.Text, out int line, out int column))
        {
            GoToHint.Text = L("edt.goToBad");
            return;
        }
        var doc = Editor.Document;
        line = Math.Clamp(line, 1, doc.LineCount);
        var target = doc.GetLineByNumber(line);
        column = Math.Clamp(column, 1, target.Length + 1);
        CloseFindBar();
        Editor.TextArea.Caret.Location = new TextLocation(line, column);
        Editor.Select(Editor.CaretOffset, 0);
        Editor.ScrollTo(line, column);
        Editor.TextArea.Focus();
    }

    private void ScheduleCount(bool immediate = false, bool wrapped = false)
    {
        _countWrapped = wrapped;
        _countTimer ??= NewTimer(TimeSpan.FromMilliseconds(250), CountNow);
        _countTimer.Stop();
        if (immediate && !_largeBuffer) CountNow();
        else _countTimer.Start();
    }

    private bool _countWrapped;

    /// <summary>"3 of 17": the matches in the file and which one is selected.</summary>
    private void CountNow()
    {
        _countTimer?.Stop();
        if (_findRegex == null || FindBar.Visibility != Visibility.Visible) return;
        string text = Editor.Text;
        int total = 0, current = 0, selStart = Editor.SelectionStart, selLen = Editor.SelectionLength;
        const int cap = 100_000;
        try
        {
            for (var m = _findRegex.Match(text); m.Success && total < cap; m = m.NextMatch())
            {
                total++;
                if (m.Index == selStart && m.Length == selLen) current = total;
            }
        }
        catch (RegexMatchTimeoutException) { ShowFindCount(L("edt.regexTimeout"), problem: true); return; }
        string n = total >= cap ? cap.ToString("N0", CultureInfo.CurrentCulture) + "+" : total.ToString("N0", CultureInfo.CurrentCulture);
        string label = total == 0 ? L("edt.noResults")
            : current > 0 ? LF("edt.matchOf", current.ToString("N0", CultureInfo.CurrentCulture), n)
            : LF("edt.matchCount", n);
        if (_countWrapped && total > 0) label += " · " + L("edt.wrapped");
        ShowFindCount(label, problem: total == 0);
    }

    private void ShowFindCount(string text, bool problem)
    {
        FindCount.Text = text;
        FindCount.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Status.Danger" : "Text.Muted");
        FindBoxFrame.SetResourceReference(Border.BorderBrushProperty, problem && FindBox.Text.Length > 0 ? "Status.Danger" : "Input.Border");
    }

    // ───────────────────────── smart highlighting ─────────────────────────

    /// <summary>A whole word selected: every other place it appears on screen gets an outline (Notepad++).</summary>
    private void UpdateSmartHighlight()
    {
        Regex? smart = null;
        if (Editor.SelectionLength is > 1 and <= 100)
        {
            string selected = Editor.SelectedText;
            if (EditorOps.IsSmartHighlightWord(selected))
                smart = new Regex(@"(?<![\w])" + Regex.Escape(selected) + @"(?![\w])", RegexOptions.CultureInvariant, EditorOps.MatchTimeout);
        }
        if (smart == null && _marks.Smart == null) return;
        _marks.Smart = smart;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // ───────────────────────── folding ─────────────────────────

    private void ScheduleFolding(bool immediate)
    {
        if (_folding == null) return;
        _foldTimer ??= NewTimer(TimeSpan.FromMilliseconds(700), UpdateFoldingsNow);
        _foldTimer.Stop();
        if (immediate) Dispatcher.BeginInvoke(UpdateFoldingsNow, DispatcherPriority.Background);
        else _foldTimer.Start();
    }

    private void UpdateFoldingsNow()
    {
        _foldTimer?.Stop();
        if (_folding == null) return;
        try
        {
            if (_boundTab == null || _largeBuffer || _foldKind == EditorFolding.Kind.None) { _folding.Clear(); return; }
            EditorFolding.Update(_folding, Editor.Document, _foldKind);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(UpdateFoldingsNow), ex); }
    }

    // ───────────────────────── status bar ─────────────────────────

    private void UpdateStatusBar()
    {
        if (!_toolsReady || _boundTab == null) return;
        var caret = Editor.TextArea.Caret;
        var nf = CultureInfo.CurrentCulture;
        StPosition.Text = LF("edt.position", caret.Line.ToString("N0", nf), caret.Column.ToString("N0", nf));
        if (Editor.SelectionLength > 0)
        {
            var sel = Editor.TextArea.Selection;
            int lines = sel.EndPosition.Line - sel.StartPosition.Line + 1;
            StSelection.Text = LF("edt.selection", Editor.SelectionLength.ToString("N0", nf), Math.Abs(lines).ToString("N0", nf));
        }
        else StSelection.Text = "";
        StLines.Text = LF("edt.lines", Editor.Document.LineCount.ToString("N0", nf), Editor.Document.TextLength.ToString("N0", nf));
        StEol.Content = EditorOps.EolLabel(_eol);
        StLanguage.Text = Editor.SyntaxHighlighting?.Name ?? L("edt.plainText");
        StOverwrite.Text = Editor.TextArea.OverstrikeMode ? "OVR" : "INS";
        StZoom.Content = Math.Round(Editor.FontSize / BaseFontSize * 100).ToString(nf) + "%";
        TbUndo.IsEnabled = Editor.CanUndo;
        TbRedo.IsEnabled = Editor.CanRedo;
    }

    private void OnEolMenu(object sender, RoutedEventArgs e)
    {
        if (_boundTab == null || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (eol, key) in new[] { (EditorOps.Eol.CrLf, "edt.eolWindows"), (EditorOps.Eol.Lf, "edt.eolUnix"), (EditorOps.Eol.Cr, "edt.eolMac") })
        {
            var item = new MenuItem { Header = L(key), IsCheckable = true, IsChecked = _eol == eol };
            item.Click += (_, _) => ConvertEol(eol);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Every line ending in the file becomes <paramref name="to"/> — one edit; the caret stays on its line.</summary>
    public void ConvertEol(EditorOps.Eol to)
    {
        if (_boundTab == null) return;
        SettleAgentTyping();
        string text = Editor.Text;
        string converted = EditorOps.ConvertEol(text, to);
        _eol = to;
        if (converted != text)
        {
            var loc = Editor.TextArea.Caret.Location;
            Editor.Document.Replace(0, Editor.Document.TextLength, converted);
            Editor.TextArea.Caret.Location = loc;
            Say(LF("edt.eolConverted", EditorOps.EolLabel(to)));
        }
        UpdateStatusBar();
    }

    private void OnEncodingMenu(object sender, RoutedEventArgs e)
    {
        if (_boundTab == null || _vm == null || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        if (!_boundTab.DecodedFaithfully)
        {
            menu.Items.Add(new MenuItem { Header = L("edt.encodingUnknown"), IsEnabled = false });
        }
        else
        {
            var choices = new List<(int Cp, bool Bom)> { (0, false), (0, true), (TextFileCodec.Utf16Le, false), (TextFileCodec.Thai, false) };
            if (_boundTab.CodePage is not (0 or TextFileCodec.Utf16Le or TextFileCodec.Thai)) choices.Add((_boundTab.CodePage, false));
            foreach (var (cp, bom) in choices)
            {
                var item = new MenuItem
                {
                    Header = TextFileCodec.EncodingLabel(cp, bom),
                    IsCheckable = true,
                    IsChecked = _boundTab.CodePage == cp && (cp != 0 || _boundTab.HasBom == bom),
                };
                item.Click += (_, _) => _vm.SetActiveEncoding(cp, bom);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = L("edt.encodingNote"), IsEnabled = false });
        }
        menu.IsOpen = true;
    }

    // ───────────────────────── helpers ─────────────────────────

    private void Say(string message)
    {
        if (_vm != null) _vm.StatusMessage = message;
    }

    private DispatcherTimer NewTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        t.Tick += (_, _) => tick();
        return t;
    }
}
