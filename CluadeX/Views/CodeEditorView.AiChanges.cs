using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

/// <summary>
/// What changed in the file on screen (owner 2026-11-03, vibe coding: "show what the AI changed"; 2026-11-09: "ป้ายแถบสีที่มี
/// การเปลี่ยนแปลง … อยากให้ชัดเจน … มี compare ก่อนหลังได้ … ถ้าคุณแก้โค๊ดเอง ก็ต้องระบายไว้อีกสี"):
///   • gutter bars and a line tint on the lines the AI added (green) or changed (blue), a red wedge and rule where it
///     deleted lines — against the file before THIS CHAT first touched it (<see cref="CheckpointService.BeforeSession"/>),
///     so the marks stay after a save or a reopen;
///   • the lines the user edited by hand in a second colour (orange) — [user-edit] comments, the remembered lines
///     (<see cref="UserEditStore"/>) and unsaved typing — kept apart from the AI's (<see cref="ChangeMarks"/>);
///   • status-bar counts, Alt+F5 / Shift+Alt+F5 through every mark, a peek with the lines before and after where one AI
///     change can be put back (after a confirmation, as one undo step, never while the AI is still working here);
///   • "⇄ Before / after": the whole file in the diff panel, F7 / Shift+F7 through its changes;
///   • the user-edit menu: next one, every one in the project (search panel), the [user-edit] comment setting.
/// </summary>
public partial class CodeEditorView
{
    private const int AiChangesMaxChars = 2_000_000;

    /// <summary>Marks the user hid, per file: hidden until the AI changes that file in another turn (their own stay).</summary>
    private static readonly Dictionary<string, string> s_aiHidden = new(StringComparer.OrdinalIgnoreCase);

    private AiChangeMargin? _aiMargin;
    private ChangeLineRenderer? _changeLines;
    private ChangeMarkFx? _marksFx;
    private string? _marksFxPath;
    private IReadOnlyList<AiChangeHunk> _aiHunks = Array.Empty<AiChangeHunk>();
    private CheckpointService.Baseline? _aiBaseline;
    private string? _aiTurn;
    private int _aiGeneration;
    private DispatcherTimer? _aiTimer;
    private Popup? _aiPeek;
    private CodeEditorViewModel? _marksVm;
    private int _diffCursor = -1;

    /// <summary>The marks on screen (the harness reads them).</summary>
    internal IReadOnlyList<AiChangeHunk> ChangeHunks => _aiHunks;

    /// <summary>The marks' motion (the harness checks it lets go of the render loop).</summary>
    internal ChangeMarkFx? ChangeMarksFx => _marksFx;

    /// <summary>From InitializeBuildProblems: this view's own controls only.</summary>
    private void InitializeAiChanges()
    {
        _aiMargin = new AiChangeMargin();
        _aiMargin.HunkClicked += ShowAiPeek;
        Editor.TextArea.LeftMargins.Add(_aiMargin);
        _changeLines = new ChangeLineRenderer();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_changeLines);
        _marksFx = new ChangeMarkFx(
            () => { _aiMargin?.InvalidateVisual(); Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background); },
            () => Editor.IsVisible);
        _aiMargin.Fx = _changeLines.Fx = _marksFx;
        _aiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _aiTimer.Tick += (_, _) => { _aiTimer.Stop(); _ = RefreshAiChangesAsync(); };
        Editor.Document.Changed += (_, _) => ScheduleAiChanges();
        Editor.TextArea.PreviewKeyDown += OnAiChangeKeys;
    }

    /// <summary>After a pause: typing, an agent edit reloading the file, a tab switch, a save or a run ending all land here.</summary>
    private void ScheduleAiChanges()
    {
        HookChangeMarksVm();
        if (_aiTimer == null) return;
        _aiTimer.Stop();
        _aiTimer.Start();
    }

    /// <summary>Listen to the view model the marks depend on: a save (user edits remembered) and the diff panel's rows.</summary>
    private void HookChangeMarksVm()
    {
        if (ReferenceEquals(_marksVm, _vm)) return;
        UnhookChangeMarksVm();
        _marksVm = _vm;
        if (_marksVm == null) return;
        _marksVm.ChangeMarksChanged += OnChangeMarksChanged;
        _marksVm.PropertyChanged += OnMarksVmPropertyChanged;
    }

    private void UnhookChangeMarksVm()
    {
        if (_marksVm == null) return;
        _marksVm.ChangeMarksChanged -= OnChangeMarksChanged;
        _marksVm.PropertyChanged -= OnMarksVmPropertyChanged;
        _marksVm = null;
    }

    private void OnChangeMarksChanged(string path)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnChangeMarksChanged(path)); return; }
        if (_boundTab != null && string.Equals(_boundTab.FullPath, path, StringComparison.OrdinalIgnoreCase)) ScheduleAiChanges();
    }

    private void OnMarksVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CodeEditorViewModel.DiffRows)) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnMarksVmPropertyChanged(sender, e)); return; }
        _diffCursor = -1;
        int blocks = DiffBlocks().Count;
        DiffPos.Text = blocks > 0 ? LF("cm.diffPos", "–", blocks) : "";
        DiffPrevBtn.Visibility = DiffNextBtn.Visibility = blocks > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DetachAiChanges()
    {
        _aiTimer?.Stop();
        _aiGeneration++;
        CloseAiPeek();
        UnhookChangeMarksVm();
        _marksFx?.Finish();
    }

    private async Task RefreshAiChangesAsync()
    {
        try
        {
            var tab = _boundTab;
            string? root = ProjectRoot;
            var checkpoints = CheckpointService.Current;
            int generation = ++_aiGeneration;
            if (tab == null || Editor.Document.TextLength > AiChangesMaxChars)
            {
                ApplyAiChanges(Array.Empty<AiChangeHunk>(), null);
                return;
            }
            string path = tab.FullPath;
            var snapshot = Editor.Document.CreateSnapshot();
            // While the AI types into this tab the editor runs ahead of the tab's text: that is not the user typing.
            bool agentTyping = _vm?.IsAgentTypingInto(tab) == true || _lastSyncedStale;
            string? disk = agentTyping ? null : tab.OriginalContent;
            var (hunks, before) = await Task.Run(() =>
            {
                var b = root != null && checkpoints != null ? checkpoints.BeforeSession(root, path) : null;
                bool hidden;
                lock (s_aiHidden) hidden = b != null && s_aiHidden.TryGetValue(path, out var h) && h == b.Turn;
                var record = UserEditStore.Shared.Get(path);
                string? baseline = b == null || hidden ? null : b.Text ?? "";
                return (ChangeMarks.Build(baseline, disk, snapshot.Text, record), b);
            });
            // Another refresh started meanwhile, or the editor shows another file now.
            if (generation != _aiGeneration || !ReferenceEquals(tab, _boundTab)) return;
            ApplyAiChanges(hunks, before);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(RefreshAiChangesAsync), ex); }
    }

    private void ApplyAiChanges(IReadOnlyList<AiChangeHunk> hunks, CheckpointService.Baseline? before)
    {
        _aiHunks = hunks;
        _aiBaseline = before;
        _aiTurn = before?.Turn;
        _aiMargin?.Set(hunks);
        _changeLines?.Set(hunks);
        string? path = _boundTab?.FullPath;
        bool fresh = !string.Equals(path, _marksFxPath, StringComparison.OrdinalIgnoreCase);
        _marksFxPath = path;
        _marksFx?.Track(MarkKeys(hunks), fresh);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        var (ai, user) = ChangeMarks.Count(hunks);
        StAiChanges.Visibility = ai > 0 ? Visibility.Visible : Visibility.Collapsed;
        StAiChanges.Content = LF("ai.changes", ai);
        StUserEdits.Visibility = user > 0 ? Visibility.Visible : Visibility.Collapsed;
        StUserEdits.Content = LF("cm.user", user);
        StUserEdits.Foreground = AiChangeMargin.BrushFor(AiChangeKind.Modified, byUser: true);
        bool unsaved = _boundTab != null && !string.Equals(_boundTab.OriginalContent, Editor.Document.Text, StringComparison.Ordinal);
        StCompare.Visibility = before != null || unsaved ? Visibility.Visible : Visibility.Collapsed;
        if (hunks.Count == 0) CloseAiPeek();
    }

    /// <summary>
    /// Each mark's identity for the motion: owner, kind and the text of the line above it (and which of the marks with that
    /// same context it is) — the same change keeps its key while lines are typed or added above it, so it draws itself in
    /// once, not on every refresh.
    /// </summary>
    private List<(AiChangeHunk Hunk, string Key)> MarkKeys(IReadOnlyList<AiChangeHunk> hunks)
    {
        var doc = Editor.Document;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<(AiChangeHunk, string)>(hunks.Count);
        foreach (var h in hunks)
        {
            int above = Math.Min(h.StartLine - 1, doc.LineCount);
            string context = above >= 1 ? doc.GetText(doc.GetLineByNumber(above)).Trim() : "";
            if (context.Length > 200) context = context[..200];
            string key = $"{(h.ByUser ? 'U' : 'A')}{(int)h.Kind}|{context}";
            seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 0;
            list.Add((h, $"{key}#{seen[key]}"));
        }
        return list;
    }

    /// <summary>A theme switch: the marks read their colours when drawn (the cached paint is rebuilt).</summary>
    private void RepaintChangeMarks()
    {
        ChangeMarkPaint.Reset();
        _aiMargin?.InvalidateVisual();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        if (StUserEdits != null) StUserEdits.Foreground = AiChangeMargin.BrushFor(AiChangeKind.Modified, byUser: true);
    }

    private void OnAiChangesNext(object sender, RoutedEventArgs e) => GoToAiChange(forward: true, h => !h.ByUser);

    private void OnAiChangeKeys(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.F5 || (Keyboard.Modifiers & ModifierKeys.Alt) == 0 || _aiHunks.Count == 0) return;
        GoToAiChange(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    /// <summary>The next / previous mark from the caret (or from the open peek's mark), optionally only some kind.</summary>
    private void GoToAiChange(bool forward, Func<AiChangeHunk, bool>? which = null)
    {
        var list = which == null ? _aiHunks : _aiHunks.Where(which).ToList();
        int line = Editor.Document.GetLineByOffset(Math.Min(Editor.CaretOffset, Editor.Document.TextLength)).LineNumber;
        // From an open peek, step from its change (the caret may sit elsewhere).
        if (_aiPeek?.IsOpen == true && _aiPeek.Tag is AiChangeHunk open) line = open.AnchorLine;
        var next = AiChanges.Step(list, line, forward);
        if (next == null) return;
        OnSelectRange(Math.Min(next.AnchorLine, Editor.Document.LineCount), 0, 0);
        Dispatcher.BeginInvoke(() => ShowAiPeek(next), DispatcherPriority.Background);
    }

    // ───────────────────────── the peek ─────────────────────────

    private void ShowAiPeek(AiChangeHunk hunk)
    {
        try
        {
            int index = IndexOf(hunk);
            if (index < 0) return;
            var view = Editor.TextArea.TextView;
            int anchor = Math.Clamp(hunk.LineCount > 0 ? hunk.EndLine : hunk.StartLine - 1, 1, Editor.Document.LineCount);
            view.EnsureVisualLines();
            var vl = view.GetVisualLine(anchor);
            double y = vl != null ? vl.VisualTop - view.VerticalOffset + vl.Height : 0;

            var panel = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            Button Btn(string content, string tip, RoutedEventHandler click, bool glyph = true)
            {
                var b = new Button
                {
                    Content = content, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0),
                    Style = TryFindResource(glyph ? "EdToolBtn" : "EdTextBtn") as Style,
                };
                if (glyph) b.FontFamily = new FontFamily("Segoe MDL2 Assets");
                b.Click += click;
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            header.Children.Add(Btn("", L("ai.close"), (_, _) => CloseAiPeek()));
            header.Children.Add(Btn("", L("ai.next"), (_, _) => GoToAiChange(true)));
            header.Children.Add(Btn("", L("ai.prev"), (_, _) => GoToAiChange(false)));
            if (hunk.CanRevert) header.Children.Add(Btn(L("ai.revert"), L("ai.revertTip"), (_, _) => RevertAiChange(hunk), glyph: false));
            if (!hunk.ByUser) header.Children.Add(Btn(L("ai.hide"), L("ai.hideTip"), (_, _) => HideAiChanges(), glyph: false));
            header.Children.Add(Btn(L("cm.compare"), L("cm.compareTip"), (_, _) => { CloseAiPeek(); OnCompareChanges(this, new RoutedEventArgs()); }, glyph: false));
            string title = hunk.ByUser
                ? hunk.LineCount > 0
                    ? LF("cm.peekUser", index + 1, _aiHunks.Count, hunk.StartLine, hunk.EndLine)
                    : LF("cm.peekUserRemoved", index + 1, _aiHunks.Count, hunk.StartLine)
                : hunk.LineCount > 0
                    ? LF("ai.peekTitle", index + 1, _aiHunks.Count, hunk.StartLine, hunk.EndLine)
                    : LF("ai.peekTitleRemoved", index + 1, _aiHunks.Count, hunk.StartLine);
            header.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = AiChangeMargin.BrushFor(hunk.Kind, hunk.ByUser),
            });
            panel.Children.Add(header);

            var body = new StackPanel();
            var mono = (FontFamily)(TryFindResource("Font.Mono.Display") ?? new FontFamily("Consolas"));
            void Lines(string label, IReadOnlyList<string> lines, string mark, Brush tint)
            {
                if (lines.Count == 0) return;
                body.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = ThemeService.BrushOf("Text.Muted"), Margin = new Thickness(0, 4, 0, 2) });
                var text = string.Join("\n", lines.Take(200).Select(l => mark + l)) + (lines.Count > 200 ? $"\n… +{lines.Count - 200}" : "");
                body.Children.Add(new Border
                {
                    Background = tint, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock { Text = text, FontFamily = mono, FontSize = 12, Foreground = ThemeService.BrushOf("Text.Primary") },
                });
            }
            void Note(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.BrushOf("Text.Muted"), Margin = new Thickness(0, 0, 0, 4) });
            if (hunk.ByUser)
            {
                Lines(L("ai.before"), hunk.OldLines, "- ", ThemeService.BrushOf("Status.Danger.Tint"));
                Lines(L("cm.yourLines"), hunk.NewLines, "✎ ", ThemeService.BrushOf("Editor.Change.User.Tint"));
            }
            else
            {
                if (hunk.Partial) Note(L("cm.partial"));
                else if (!hunk.CanRevert) Note(L("ai.tooLarge"));
                Lines(L("ai.before"), hunk.OldLines, "- ", ThemeService.BrushOf("Status.Danger.Tint"));
                Lines(L("ai.after"), hunk.NewLines, "+ ", AiChangeMargin.TintFor(hunk) ?? ThemeService.BrushOf("Status.Success.Tint"));
            }
            panel.Children.Add(new ScrollViewer
            {
                Content = body, MaxHeight = 300,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            _aiPeek ??= new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Relative, PopupAnimation = ChangeMarkFx.MotionOn ? PopupAnimation.Fade : PopupAnimation.None };
            _aiPeek.PlacementTarget = view;
            _aiPeek.HorizontalOffset = 24;
            _aiPeek.VerticalOffset = Math.Max(0, y);
            _aiPeek.Tag = hunk;
            _aiPeek.Child = new Border
            {
                Child = panel, Padding = new Thickness(10, 8, 10, 10), CornerRadius = new CornerRadius(6),
                Background = ThemeService.BrushOf("Bg.Panel"), BorderBrush = AiChangeMargin.BrushFor(hunk.Kind, hunk.ByUser), BorderThickness = new Thickness(1),
                MaxWidth = Math.Max(360, view.ActualWidth - 48), MinWidth = 360,
            };
            _aiPeek.IsOpen = true;
            _marksFx?.Pulse(hunk);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(ShowAiPeek), ex); }
    }

    private int IndexOf(AiChangeHunk hunk)
    {
        for (int i = 0; i < _aiHunks.Count; i++)
            if (ReferenceEquals(_aiHunks[i], hunk) || _aiHunks[i] == hunk) return i;
        return -1;
    }

    private void CloseAiPeek()
    {
        if (_aiPeek != null) _aiPeek.IsOpen = false;
    }

    /// <summary>Put one change back — asked first, one undo step, and only while the AI is not working in the project
    /// (it would be editing the same file).</summary>
    private void RevertAiChange(AiChangeHunk hunk)
    {
        try
        {
            if (_vm == null || _boundTab == null || !hunk.CanRevert) return;
            if (_vm.IsLockedByAgent || Editor.IsReadOnly) { Say(L("ai.revertLocked")); return; }
            CloseAiPeek();
            string question = hunk.LineCount > 0
                ? LF("ai.revertConfirm", hunk.StartLine, hunk.EndLine)
                : LF("ai.revertConfirmRemoved", hunk.OldLines.Count, hunk.StartLine);
            var owner = Window.GetWindow(this);
            var answer = owner != null
                ? MessageBox.Show(owner, question, L("ai.revertTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(question, L("ai.revertTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            // The AI may have started again while the question was up.
            if (_vm.IsLockedByAgent || Editor.IsReadOnly) { Say(L("ai.revertLocked")); return; }

            var edit = AiChanges.RevertEdit(Editor.Document.Text, hunk);
            if (edit == null) { Say(L("ai.revertGone")); ScheduleAiChanges(); return; }
            Editor.Document.Replace(edit.Value.Offset, edit.Value.Length, edit.Value.Replacement);
            Say(L("ai.reverted"));
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(RevertAiChange), ex); }
    }

    /// <summary>Hide this file's AI marks until the AI changes it in another turn (the user's own stay).</summary>
    private void HideAiChanges()
    {
        if (_boundTab == null || _aiTurn == null) return;
        lock (s_aiHidden) s_aiHidden[_boundTab.FullPath] = _aiTurn;
        CloseAiPeek();
        ScheduleAiChanges();
    }

    // ───────────────────────── before / after ─────────────────────────

    /// <summary>The whole file against how it was before this chat changed it — or, when no chat did, against the disk.</summary>
    private void OnCompareChanges(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || _boundTab == null) return;
            FlushDeferredSync();
            var tab = _boundTab;
            var b = _aiBaseline;
            Task shown;
            if (b != null)
                shown = _vm.ShowChangesDiffAsync(tab, b.Text ?? "",
                    LF("cm.leftCaption", b.TimeUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)), againstDisk: false);
            else if (!string.Equals(tab.OriginalContent, tab.Content, StringComparison.Ordinal))
                shown = _vm.ShowChangesDiffAsync(tab, tab.OriginalContent, L("cm.leftCaptionDisk"), againstDisk: true);
            else { Say(L("cm.noBaseline")); return; }
            // Land on the first change, with the keyboard in the list (F7 / Shift+F7).
            shown.ContinueWith(_ => Dispatcher.BeginInvoke(() => { StepDiff(forward: true); DiffList.Focus(); }, DispatcherPriority.Background),
                TaskScheduler.Default);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnCompareChanges), ex); }
    }

    /// <summary>Where each run of changed rows starts in the diff panel.</summary>
    private List<int> DiffBlocks()
    {
        var blocks = new List<int>();
        var rows = _vm?.DiffRows;
        if (rows == null) return blocks;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsChange && (i == 0 || !rows[i - 1].IsChange)) blocks.Add(i);
        return blocks;
    }

    private void OnDiffPrev(object sender, RoutedEventArgs e) => StepDiff(forward: false);
    private void OnDiffNext(object sender, RoutedEventArgs e) => StepDiff(forward: true);

    private void OnDiffKeys(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F7) return;
        StepDiff(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    /// <summary>Scroll the diff panel to the next / previous run of changes, wrapping around.</summary>
    internal void StepDiff(bool forward)
    {
        var blocks = DiffBlocks();
        if (blocks.Count == 0 || _vm == null) { DiffPos.Text = ""; return; }
        int n = blocks.Count;
        _diffCursor = _diffCursor < 0 ? (forward ? 0 : n - 1) : ((forward ? _diffCursor + 1 : _diffCursor - 1) % n + n) % n;
        int row = blocks[_diffCursor];
        DiffPos.Text = LF("cm.diffPos", _diffCursor + 1, n);
        DiffList.UpdateLayout();
        var sv = FindChild<ScrollViewer>(DiffList);
        if (sv != null && sv.CanContentScroll) sv.ScrollToVerticalOffset(Math.Max(0, row - 3));   // item-based: a few rows of context above
        else if (row < _vm.DiffRows.Count) DiffList.ScrollIntoView(_vm.DiffRows[row]);
    }

    internal int DiffCursor => _diffCursor;

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    // ───────────────────────── the user's own edits ─────────────────────────

    private void OnUserEditsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || _vm == null) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Top };
        bool any = _aiHunks.Any(h => h.ByUser);
        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }
        Item(L("cm.menuNext"), () => GoToAiChange(true, h => h.ByUser), any);
        Item(L("cm.menuPrev"), () => GoToAiChange(false, h => h.ByUser), any);
        menu.Items.Add(new Separator());
        Item(L("cm.menuList"), () => OnShowUserEdits(this, new RoutedEventArgs()));
        menu.Items.Add(new Separator());
        var markers = Item(L("cm.menuMarkers"), () => { if (_vm != null) _vm.UserEditMarkers = !_vm.UserEditMarkers; });
        markers.IsCheckable = true;
        markers.IsChecked = _vm.UserEditMarkers;
        markers.ToolTip = L("cm.menuMarkersNote");
        menu.IsOpen = true;
    }

    private async void OnShowUserEdits(object sender, RoutedEventArgs e)
    {
        try { if (_vm != null) await _vm.ShowUserEditsAsync(); }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnShowUserEdits), ex); }
    }

    private void OnToggleUserEditMarkers(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.UserEditMarkers = MiUserEditMarkers.IsChecked;
    }
}
