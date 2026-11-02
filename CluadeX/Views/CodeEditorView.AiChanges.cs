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

namespace CluadeX.Views;

/// <summary>
/// What the AI changed in the file on screen (owner 2026-11-03, vibe coding: "show what the AI changed"): gutter marks
/// on the lines its last turn added or changed — against the file before that turn, from the restore points the agent
/// keeps (<see cref="CheckpointService.BeforeLatestTurn"/>) — a status-bar count, Alt+F5 / Shift+Alt+F5 to step through
/// them, and a peek with the lines before and after, where one change can be put back (after a confirmation, as one
/// undo step, never while the AI is still working here).
/// </summary>
public partial class CodeEditorView
{
    private const int AiChangesMaxChars = 2_000_000;

    /// <summary>Marks the user hid, per file: hidden until the AI changes that file in another turn.</summary>
    private static readonly Dictionary<string, string> s_aiHidden = new(StringComparer.OrdinalIgnoreCase);

    private AiChangeMargin? _aiMargin;
    private IReadOnlyList<AiChangeHunk> _aiHunks = Array.Empty<AiChangeHunk>();
    private string? _aiTurn;
    private int _aiGeneration;
    private DispatcherTimer? _aiTimer;
    private Popup? _aiPeek;

    /// <summary>From InitializeBuildProblems: this view's own controls only.</summary>
    private void InitializeAiChanges()
    {
        _aiMargin = new AiChangeMargin();
        _aiMargin.HunkClicked += ShowAiPeek;
        Editor.TextArea.LeftMargins.Add(_aiMargin);
        _aiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _aiTimer.Tick += (_, _) => { _aiTimer.Stop(); _ = RefreshAiChangesAsync(); };
        Editor.Document.Changed += (_, _) => ScheduleAiChanges();
        Editor.TextArea.PreviewKeyDown += OnAiChangeKeys;
    }

    /// <summary>After a pause: typing, an agent edit reloading the file, a tab switch or a run ending all land here.</summary>
    private void ScheduleAiChanges()
    {
        if (_aiTimer == null) return;
        _aiTimer.Stop();
        _aiTimer.Start();
    }

    private void DetachAiChanges()
    {
        _aiTimer?.Stop();
        _aiGeneration++;
        CloseAiPeek();
    }

    private async Task RefreshAiChangesAsync()
    {
        try
        {
            var tab = _boundTab;
            string? root = ProjectRoot;
            var checkpoints = CheckpointService.Current;
            int generation = ++_aiGeneration;
            if (tab == null || root == null || checkpoints == null || Editor.Document.TextLength > AiChangesMaxChars)
            {
                ApplyAiChanges(Array.Empty<AiChangeHunk>(), null);
                return;
            }
            string path = tab.FullPath;
            var snapshot = Editor.Document.CreateSnapshot();
            var (hunks, turn) = await Task.Run(() =>
            {
                var before = checkpoints.BeforeLatestTurn(root, path);
                if (before == null) return ((IReadOnlyList<AiChangeHunk>)Array.Empty<AiChangeHunk>(), (string?)null);
                lock (s_aiHidden)
                    if (s_aiHidden.TryGetValue(path, out var hidden) && hidden == before.Turn)
                        return (Array.Empty<AiChangeHunk>(), before.Turn);
                return (AiChanges.Hunks(before.Text ?? "", snapshot.Text), before.Turn);
            });
            // Another refresh started meanwhile, or the editor shows another file now.
            if (generation != _aiGeneration || !ReferenceEquals(tab, _boundTab)) return;
            ApplyAiChanges(hunks, turn);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(RefreshAiChangesAsync), ex); }
    }

    private void ApplyAiChanges(IReadOnlyList<AiChangeHunk> hunks, string? turn)
    {
        _aiHunks = hunks;
        _aiTurn = turn;
        _aiMargin?.Set(hunks);
        StAiChanges.Visibility = hunks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StAiChanges.Content = LF("ai.changes", hunks.Count);
        if (hunks.Count == 0) CloseAiPeek();
    }

    private void OnAiChangesNext(object sender, RoutedEventArgs e) => GoToAiChange(forward: true);

    private void OnAiChangeKeys(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.F5 || (Keyboard.Modifiers & ModifierKeys.Alt) == 0 || _aiHunks.Count == 0) return;
        GoToAiChange(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    private void GoToAiChange(bool forward)
    {
        int line = Editor.Document.GetLineByOffset(Math.Min(Editor.CaretOffset, Editor.Document.TextLength)).LineNumber;
        // From an open peek, step from its change (the caret may sit elsewhere).
        if (_aiPeek?.IsOpen == true && _aiPeek.Tag is AiChangeHunk open) line = open.AnchorLine;
        var next = AiChanges.Step(_aiHunks, line, forward);
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
            header.Children.Add(Btn("", L("ai.close"), (_, _) => CloseAiPeek()));
            header.Children.Add(Btn("", L("ai.next"), (_, _) => GoToAiChange(true)));
            header.Children.Add(Btn("", L("ai.prev"), (_, _) => GoToAiChange(false)));
            bool canRevert = hunk.OldLines.Count + hunk.NewLines.Count > 0;
            if (canRevert) header.Children.Add(Btn(L("ai.revert"), L("ai.revertTip"), (_, _) => RevertAiChange(hunk), glyph: false));
            header.Children.Add(Btn(L("ai.hide"), L("ai.hideTip"), (_, _) => HideAiChanges(), glyph: false));
            header.Children.Add(new TextBlock
            {
                Text = hunk.LineCount > 0
                    ? LF("ai.peekTitle", index + 1, _aiHunks.Count, hunk.StartLine, hunk.EndLine)
                    : LF("ai.peekTitleRemoved", index + 1, _aiHunks.Count, hunk.StartLine),
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeService.BrushOf("Text.Secondary"),
            });
            panel.Children.Add(header);

            var body = new StackPanel();
            var mono = (FontFamily)(TryFindResource("Font.Mono.Display") ?? new FontFamily("Consolas"));
            void Lines(string label, IReadOnlyList<string> lines, string mark, string tint)
            {
                if (lines.Count == 0) return;
                body.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = ThemeService.BrushOf("Text.Muted"), Margin = new Thickness(0, 4, 0, 2) });
                var text = string.Join("\n", lines.Take(200).Select(l => mark + l)) + (lines.Count > 200 ? $"\n… +{lines.Count - 200}" : "");
                body.Children.Add(new Border
                {
                    Background = ThemeService.BrushOf(tint), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock { Text = text, FontFamily = mono, FontSize = 12, Foreground = ThemeService.BrushOf("Text.Primary") },
                });
            }
            if (!canRevert) body.Children.Add(new TextBlock { Text = L("ai.tooLarge"), TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.BrushOf("Text.Muted") });
            Lines(L("ai.before"), hunk.OldLines, "- ", "Status.Danger.Soft");
            Lines(L("ai.after"), hunk.NewLines, "+ ", "Status.Success.Soft");
            panel.Children.Add(new ScrollViewer
            {
                Content = body, MaxHeight = 300,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            _aiPeek ??= new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Relative };
            _aiPeek.PlacementTarget = view;
            _aiPeek.HorizontalOffset = 24;
            _aiPeek.VerticalOffset = Math.Max(0, y);
            _aiPeek.Tag = hunk;
            _aiPeek.Child = new Border
            {
                Child = panel, Padding = new Thickness(10, 8, 10, 10), CornerRadius = new CornerRadius(6),
                Background = ThemeService.BrushOf("Bg.Panel"), BorderBrush = ThemeService.BrushOf("Border.Subtle"), BorderThickness = new Thickness(1),
                MaxWidth = Math.Max(360, view.ActualWidth - 48), MinWidth = 360,
            };
            _aiPeek.IsOpen = true;
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
            if (_vm == null || _boundTab == null) return;
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

    /// <summary>Hide this file's marks until the AI changes it in another turn.</summary>
    private void HideAiChanges()
    {
        if (_boundTab == null || _aiTurn == null) return;
        lock (s_aiHidden) s_aiHidden[_boundTab.FullPath] = _aiTurn;
        ApplyAiChanges(Array.Empty<AiChangeHunk>(), _aiTurn);
    }
}
