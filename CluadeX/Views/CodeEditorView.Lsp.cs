using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Lsp;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views;

/// <summary>
/// Live errors in the editor (owner 2026-11-02: the one VS Code feature that matters when the AI writes the code): the
/// language server's diagnostics as squiggles, a Problems list (click = jump, sparkle = ask the AI to fix it), hover
/// info, and — when no server is installed — the install command instead of a feature that silently does nothing.
///
/// The editor reports each edit to <see cref="EditorLanguageService"/> as it happens (LSP range computed in
/// Document.Changing, before the text moves) together with an immutable snapshot (O(1) — AvalonEdit's rope is shared,
/// not copied); the service sends after a pause in typing. Only the file in front of the user is open with the server:
/// the editor has one document, and the other tabs are plain strings an agent may rewrite at any time.
/// </summary>
public partial class CodeEditorView
{
    /// <summary>An edit bigger than this (a reload, replace-all) is sent as the whole text: describing it costs more.</summary>
    private const int LspIncrementalLimit = 64_000;
    private const int MaxProblemRows = 500;

    /// <summary>Whether the Problems panel was open — kept across visits (this view is rebuilt every time).</summary>
    private static bool s_problemsOpen;

    private EditorLanguageService? _lsp;
    private readonly DiagnosticMarkerRenderer _diagMarks = new();
    private string? _lspPath;
    private LspTextChange? _lspChange;
    private bool _lspHooked, _lspRefreshQueued;
    private ToolTip? _hoverTip;
    private CancellationTokenSource? _hoverCts;

    /// <summary>From InitializeTools: renderer, document and hover hooks — this view's own controls only.</summary>
    private void InitializeLsp()
    {
        _lsp = EditorLanguageService.Current;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagMarks);
        Editor.Document.Changing += OnLspDocumentChanging;
        Editor.Document.Changed += OnLspDocumentChanged;
        Editor.DocumentChanged += (_, _) =>
        {
            // Nothing here swaps the document today; if something does, follow it rather than go deaf.
            Editor.Document.Changing -= OnLspDocumentChanging;
            Editor.Document.Changed -= OnLspDocumentChanged;
            Editor.Document.Changing += OnLspDocumentChanging;
            Editor.Document.Changed += OnLspDocumentChanged;
        };
        Editor.TextArea.TextView.MouseHover += OnEditorMouseHover;
        Editor.TextArea.TextView.MouseHoverStopped += (_, _) => CloseHoverTip();
        Editor.TextArea.TextEntering += (_, _) => CloseHoverTip();
        ProblemsPanel.Visibility = s_problemsOpen ? Visibility.Visible : Visibility.Collapsed;
        InitializeBuildProblems();
    }

    /// <summary>The service lives as long as the app; this view does not — subscribe on attach, unsubscribe on detach.</summary>
    private void HookLsp()
    {
        if (_lspHooked || _lsp == null) return;
        _lspHooked = true;
        _lsp.DiagnosticsChanged += OnLspEvent;
        _lsp.StatusChanged += OnLspEvent;
        HookBuildProblems();
    }

    /// <summary>From DetachVm (via StopToolTimers): let go of the service and close the file with the server.</summary>
    private void DetachLsp()
    {
        if (_lsp != null)
        {
            _lsp.DiagnosticsChanged -= OnLspEvent;
            _lsp.StatusChanged -= OnLspEvent;
            if (_lspPath != null) _lsp.CloseDocument(_lspPath);
        }
        DetachBuildProblems();
        _lspHooked = false;
        _lspPath = null;
        _diagMarks.Clear();
        CloseHoverTip();
    }

    /// <summary>From OnToolsTabBound: the editor shows another file (or none) — open it with its server.</summary>
    private void OnLspTabBound()
    {
        if (_lsp == null) return;
        HookLsp();
        string? path = _boundTab?.FullPath;
        if (_lspPath != null && !LspPaths.SamePath(_lspPath, path)) _lsp.CloseDocument(_lspPath);
        _lspPath = null;
        _diagMarks.Clear();
        CloseHoverTip();
        if (path != null && LanguageServerCatalog.ForPath(path) != null)
        {
            _lspPath = path;
            var snapshot = Editor.Document.CreateSnapshot();
            _lsp.OpenDocument(ProjectRootFor(path), path, () => snapshot.Text, Editor.Document.TextLength);
        }
        RefreshLsp();
    }

    /// <summary>The project folder when the file is in it (the server sees the whole project), else the file's folder.</summary>
    private string? ProjectRootFor(string path)
    {
        string? dir = _vm?.ChatVM.WorkingDirectory;
        return !string.IsNullOrEmpty(dir) && ChatViewModel.IsInsideFolder(path, dir) ? dir : Path.GetDirectoryName(path);
    }

    /// <summary>True while the text in the editor is the file open with the server (false during a tab switch: the
    /// switch replaces the whole text, and that is a different file, not an edit).</summary>
    private bool LspTracking => _lsp != null && _lspPath != null && _boundTab != null && LspPaths.SamePath(_lspPath, _boundTab.FullPath);

    private void OnLspDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        _lspChange = null;
        if (!LspTracking || e.RemovalLength + e.InsertionLength > LspIncrementalLimit) return;
        var doc = Editor.Document;
        // In Changing the text has not moved yet: these are positions in the text the server has, which is what an
        // LSP incremental change means.
        var start = doc.GetLocation(e.Offset);
        var end = doc.GetLocation(e.Offset + e.RemovalLength);
        _lspChange = new LspTextChange(start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1, e.InsertedText.Text);
    }

    private void OnLspDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (!LspTracking) return;
        var snapshot = Editor.Document.CreateSnapshot();
        _lsp!.ChangeDocument(_lspPath!, _lspChange, () => snapshot.Text, Editor.Document.TextLength);
        _lspChange = null;
    }

    // ───────────────────────── service → screen ─────────────────────────

    private void OnLspEvent(string _)
    {
        // Raised on the server's reader thread, often in bursts (one publish per file): one repaint per burst.
        if (_lspRefreshQueued) return;
        _lspRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lspRefreshQueued = false;
            if (_lspHooked) RefreshLsp();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshLsp()
    {
        if (_lsp == null) return;
        try
        {
            // The language server's errors (while typing) and the last build's / check's — the latter only while the
            // file has not changed since that run (stale ones are listed, not drawn: their lines may have moved).
            var fromServer = _lspPath != null && LspTracking ? _lsp.DiagnosticsFor(_lspPath) : Array.Empty<EditorDiagnostic>();
            var fromBuild = _build?.ProblemsFor(_boundTab?.FullPath) ?? Array.Empty<BuildProblemEntry>();
            var current = MergeDiagnostics(fromServer, fromBuild.Where(b => !b.Stale).Select(b => b.Problem));
            if (_boundTab != null) _diagMarks.Set(Editor.Document, current);
            else _diagMarks.Clear();
            Editor.TextArea.TextView.InvalidateLayer(_diagMarks.Layer);
            var status = _lsp.StatusFor(_boundTab?.FullPath);
            UpdateProblemsButton(status, current);
            UpdateProblemsPanel(status, current, fromBuild.Where(b => b.Stale).ToList());
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(RefreshLsp), ex); }
    }

    private void UpdateProblemsButton(LanguageServerStatus status, IReadOnlyList<EditorDiagnostic> current)
    {
        // Shown for any file once something can report on it: a language server, a checker, or a run that found problems.
        if (_boundTab == null || status.State == LspServerState.NotSupported && current.Count == 0 && !HasBuildContext)
        {
            StProblems.Visibility = Visibility.Collapsed;
            return;
        }
        StProblems.Visibility = Visibility.Visible;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Glyph(string glyph, Brush brush) => row.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
            Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        });
        void Text(string text, Brush? brush = null) => row.Children.Add(new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Foreground = brush ?? ThemeService.BrushOf("Text.Muted"),
        });
        // The counts for this file always come first — from the server and from the last build or check alike.
        int errors = current.Count(d => d.Severity == LspSeverity.Error);
        int warnings = current.Count(d => d.Severity == LspSeverity.Warning);
        Glyph("", errors > 0 ? DiagnosticMarkerRenderer.BrushFor(LspSeverity.Error) : ThemeService.BrushOf("Text.Muted"));
        Text(errors.ToString(), errors > 0 ? DiagnosticMarkerRenderer.BrushFor(LspSeverity.Error) : null);
        Glyph("", warnings > 0 ? DiagnosticMarkerRenderer.BrushFor(LspSeverity.Warning) : ThemeService.BrushOf("Text.Muted"));
        Text(warnings.ToString(), warnings > 0 ? DiagnosticMarkerRenderer.BrushFor(LspSeverity.Warning) : null);
        if (_build?.IsChecking == true) Text(LF("problems.checking", _build.CheckingLabel ?? ""));
        switch (status.State)
        {
            case LspServerState.Starting:
                Text(LF("lsp.st.starting", status.LanguageName));
                break;
            // Not installed is no longer a warning here: builds and checks report without it; the panel says how to add it.
            case LspServerState.Failed:
                Glyph("", DiagnosticMarkerRenderer.BrushFor(LspSeverity.Error));
                Text(LF("lsp.st.failed", status.LanguageName), DiagnosticMarkerRenderer.BrushFor(LspSeverity.Error));
                break;
            case LspServerState.TooLarge:
                Text(L("lsp.st.tooLarge"));
                break;
        }
        StProblems.Content = row;
    }

    private void UpdateProblemsPanel(LanguageServerStatus status, IReadOnlyList<EditorDiagnostic> current, IReadOnlyList<BuildProblemEntry> staleHere)
    {
        UpdateBuildHeader();
        if (ProblemsPanel.Visibility != Visibility.Visible) return;   // rebuilt when it opens

        ProblemsServer.Text = status.State switch
        {
            LspServerState.Running => LF("lsp.st.running", status.LanguageName, status.ServerName ?? ""),
            LspServerState.Starting => LF("lsp.st.starting", status.LanguageName),
            LspServerState.NotInstalled => LF("lsp.st.notInstalled", status.LanguageName),
            LspServerState.Failed => LF("lsp.st.failed", status.LanguageName),
            LspServerState.TooLarge => L("lsp.st.tooLarge"),
            _ => "",
        };

        // The install / restart box: only when there is something the user can do about it.
        bool notInstalled = status.State == LspServerState.NotInstalled;
        bool failed = status.State == LspServerState.Failed;
        ProblemsInstall.Visibility = notInstalled || failed ? Visibility.Visible : Visibility.Collapsed;
        if (notInstalled || failed)
        {
            bool hasCommand = notInstalled && !string.IsNullOrEmpty(status.InstallCommand);
            ProblemsInstallText.Text = failed
                ? LF("lsp.failedHint", status.ServerName ?? status.LanguageName, string.IsNullOrEmpty(status.Detail) ? L("lsp.failedNoDetail") : status.Detail)
                : hasCommand ? LF("lsp.installHint", status.LanguageName) : LF("lsp.installHintNoCommand", status.LanguageName);
            ProblemsCommand.Text = hasCommand ? status.InstallCommand : "";
            var commandVis = hasCommand ? Visibility.Visible : Visibility.Collapsed;
            ProblemsCommandBox.Visibility = commandVis;
            ProblemsCopy.Visibility = commandVis;
            ProblemsRunInstall.Visibility = commandVis;
            ProblemsOpenPage.Visibility = notInstalled && !string.IsNullOrEmpty(status.InstallUrl) ? Visibility.Visible : Visibility.Collapsed;
            ProblemsRecheck.Content = failed ? L("lsp.restart") : L("lsp.checkAgain");
            ProblemsRecheck.Tag = status.LanguageKey;
        }

        // Rows: this file first (then what an earlier run found in it before it changed), then other files — the
        // server's, then the builds' and checks'.
        var rows = new List<ProblemRow>();
        string? here = _boundTab?.FullPath;
        if (here != null)
        {
            rows.AddRange(current.Where(d => d.Severity != LspSeverity.Hint).Select(d => new ProblemRow(here, d)));
            rows.AddRange(staleHere.Where(b => b.Problem.Severity != LspSeverity.Hint).Select(b => new ProblemRow(here, b.Problem.ToDiagnostic(), stale: true)));
        }
        if (_lsp != null)
            foreach (var (path, items) in _lsp.AllDiagnostics().OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (LspPaths.SamePath(path, here)) continue;
                rows.AddRange(items.Where(d => d.Severity != LspSeverity.Hint).Select(d => new ProblemRow(path, d)));
                if (rows.Count >= MaxProblemRows) break;
            }
        if (_build != null && rows.Count < MaxProblemRows)
            foreach (var b in _build.All(MaxProblemRows))
            {
                if (LspPaths.SamePath(b.Problem.Path, here) || b.Problem.Severity == LspSeverity.Hint || !InProject(b.Problem.Path)) continue;
                rows.Add(new ProblemRow(b.Problem.Path, b.Problem.ToDiagnostic(), b.Stale));
                if (rows.Count >= MaxProblemRows) break;
            }
        ProblemsList.ItemsSource = rows.Count > MaxProblemRows ? rows.GetRange(0, MaxProblemRows) : rows;

        ProblemsEmpty.Text = rows.Count > 0 ? "" : status.State switch
        {
            LspServerState.Starting => L("lsp.waiting"),
            LspServerState.TooLarge => L("lsp.tooLargeHint"),
            LspServerState.NotSupported => L("lsp.unsupported"),
            _ => L("lsp.none"),
        };
        ProblemsEmpty.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────────────────── Problems panel ─────────────────────────

    private void OnProblemsToggle(object sender, RoutedEventArgs e)
    {
        s_problemsOpen = ProblemsPanel.Visibility != Visibility.Visible;
        ProblemsPanel.Visibility = s_problemsOpen ? Visibility.Visible : Visibility.Collapsed;
        RefreshLsp();
    }

    private void OnProblemsClose(object sender, RoutedEventArgs e)
    {
        s_problemsOpen = false;
        ProblemsPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnProblemClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // The sparkle button inside the row has its own job.
            for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
                if (d is ButtonBase) return;
            if ((sender as FrameworkElement)?.DataContext is not ProblemRow row || _vm == null) return;
            if (LspPaths.SamePath(row.Path, _boundTab?.FullPath))
                OnSelectRange(row.Line, row.Diagnostic.StartCharacter, 0);
            else
                await _vm.OpenAndRevealAsync(row.Path, row.Line);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnProblemClick), ex); }
    }

    /// <summary>"Fix this error": the problem and the numbered lines around it go to the chat — to the AI already working
    /// in the project if there is one (its next step reads it), else as a new turn; a draft in the box is never sent
    /// with it (the text is added below the draft instead). The button says what it does, so a click is the ask.</summary>
    private void OnProblemAskAi(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not ProblemRow row || _vm == null) return;
            string[] lines = LinesOf(row.Path);
            string snippet = Snippet(lines, row.Line, 6);
            var d = row.Diagnostic;
            string word = L(d.Severity switch { LspSeverity.Error => "lsp.word.error", LspSeverity.Warning => "lsp.word.warning", _ => "lsp.word.info" });
            string who = string.IsNullOrEmpty(d.Source) ? L("lsp.theServer") : d.Source;
            string message = string.IsNullOrEmpty(d.Code) ? d.Message : $"{d.Code}: {d.Message}";
            SendToChat(LF("lsp.askAiPrompt", word, who, RelativeName(row.Path), row.Line, message, snippet));
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnProblemAskAi), ex); }
    }

    /// <summary>The file's lines as the user sees them: the editor's text for the file on screen, an open tab's
    /// (unsaved) text, else the disk.</summary>
    private string[] LinesOf(string path)
    {
        string? text = null;
        if (LspPaths.SamePath(path, _boundTab?.FullPath)) text = Editor.Text;
        else if (_vm?.Tabs.FirstOrDefault(t => LspPaths.SamePath(t.FullPath, path)) is { } tab) text = tab.Content;
        else
        {
            try { if (File.Exists(path) && new FileInfo(path).Length < 4_000_000) text = File.ReadAllText(path); } catch { }
        }
        return (text ?? "").Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>Lines <paramref name="line"/> ± <paramref name="around"/>, numbered, the problem's line marked with "&gt;".</summary>
    internal static string Snippet(string[] lines, int line, int around)
    {
        if (lines.Length == 0) return "";
        int from = Math.Max(1, line - around), to = Math.Min(lines.Length, line + around);
        int width = to.ToString().Length;
        var sb = new StringBuilder();
        for (int n = from; n <= to; n++)
        {
            string text = lines[n - 1];
            if (text.Length > 400) text = text[..400] + " …";
            sb.Append(n == line ? "> " : "  ").Append(n.ToString().PadLeft(width)).Append(" | ").Append(text).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private string RelativeName(string path)
    {
        string? root = _vm?.ChatVM.WorkingDirectory;
        if (!string.IsNullOrEmpty(root) && ChatViewModel.IsInsideFolder(path, root))
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        return Path.GetFileName(path);
    }

    private void OnLspCopyInstall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ProblemsCommand.Text)) return;
        try { Clipboard.SetText(ProblemsCommand.Text); Say(L("lsp.copied")); }
        catch { /* the clipboard is busy (another app holds it) — the command is still selectable in the box */ }
    }

    /// <summary>Type the install command into the editor's own terminal and run it — the user asked, by clicking.</summary>
    private void OnLspRunInstall(object sender, RoutedEventArgs e)
    {
        if (_vm == null || string.IsNullOrEmpty(ProblemsCommand.Text)) return;
        _vm.IsTerminalOpen = true;
        _vm.TerminalInput = ProblemsCommand.Text;
        if (_vm.RunTerminalCommand.CanExecute(null)) _vm.RunTerminalCommand.Execute(null);
    }

    private void OnLspOpenPage(object sender, RoutedEventArgs e)
    {
        var status = _lsp?.StatusFor(_boundTab?.FullPath);
        // Only the catalog's own https pages — never a URL that came from a file or a server.
        if (status?.InstallUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { CommandErrorSink.Report(nameof(OnLspOpenPage), ex); }
        }
    }

    private async void OnLspRecheck(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lsp == null || (sender as FrameworkElement)?.Tag is not string key) return;
            await _lsp.RestartAsync(key);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnLspRecheck), ex); }
    }

    // ───────────────────────── hover ─────────────────────────

    private async void OnEditorMouseHover(object sender, MouseEventArgs e)
    {
        try
        {
            if (_boundTab == null) return;
            var view = Editor.TextArea.TextView;
            var pos = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
            if (pos == null) return;
            var doc = Editor.Document;
            int offset = doc.GetOffset(pos.Value.Location);
            var marks = _diagMarks.At(offset);

            _hoverCts?.Cancel();
            var cts = _hoverCts = new CancellationTokenSource();
            if (marks.Count > 0) ShowHoverTip(marks, null);

            // Past the end of the line there is no symbol to ask about.
            var line = doc.GetLineByOffset(offset);
            if (_lsp == null || _lspPath == null || !LspTracking || offset >= line.EndOffset) return;
            string? info = await _lsp.HoverAsync(_lspPath, pos.Value.Line - 1, pos.Value.Column - 1, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_hoverCts, cts)) return;
            if (info != null || marks.Count > 0) ShowHoverTip(marks, info);
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnEditorMouseHover), ex); }
    }

    private void ShowHoverTip(List<DiagnosticMarker> marks, string? info)
    {
        var panel = new StackPanel { MaxWidth = 640 };
        foreach (var m in marks.Take(6))
        {
            var d = m.Diagnostic;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = ProblemRow.GlyphFor(d.Severity), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                Foreground = DiagnosticMarkerRenderer.BrushFor(d.Severity), Margin = new Thickness(0, 2, 6, 0),
            });
            string tail = string.Join(" ", new[] { d.Source, d.Code }.Where(s => !string.IsNullOrEmpty(s)));
            row.Children.Add(new TextBlock
            {
                Text = tail.Length > 0 ? $"{d.Message}  ({tail})" : d.Message,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(row);
        }
        if (!string.IsNullOrEmpty(info))
        {
            if (panel.Children.Count > 0)
                panel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 6) });
            panel.Children.Add(new TextBlock
            {
                Text = info, TextWrapping = TextWrapping.Wrap, MaxHeight = 320,
                FontFamily = (FontFamily)(TryFindResource("Font.Mono.Display") ?? new FontFamily("Consolas")), FontSize = 12,
            });
        }
        _hoverTip ??= new ToolTip { Placement = PlacementMode.Mouse, PlacementTarget = Editor.TextArea, StaysOpen = true };
        _hoverTip.Content = panel;
        _hoverTip.IsOpen = true;
    }

    private void CloseHoverTip()
    {
        _hoverCts?.Cancel();
        _hoverCts = null;
        if (_hoverTip != null) _hoverTip.IsOpen = false;
    }
}

/// <summary>One row of the Problems list.</summary>
public sealed class ProblemRow
{
    public ProblemRow(string path, EditorDiagnostic diagnostic, bool stale = false)
    {
        Path = path;
        Diagnostic = diagnostic;
        Stale = stale;
    }

    public string Path { get; }
    public EditorDiagnostic Diagnostic { get; }
    /// <summary>Found by a run before the file last changed: maybe fixed, its line may have moved — listed dimmed.</summary>
    public bool Stale { get; }
    public double RowOpacity => Stale ? 0.55 : 1.0;
    public string? StaleTip => Stale ? LocalizedResources.Instance["problems.stale"] : null;
    /// <summary>1-based, as the editor numbers lines.</summary>
    public int Line => Diagnostic.StartLine + 1;
    public string Message => Diagnostic.Message.Replace("\r", "").Replace('\n', ' ');
    public string Location => $"{System.IO.Path.GetFileName(Path)} {Line}:{Diagnostic.StartCharacter + 1}"
                              + (string.IsNullOrEmpty(Diagnostic.Source) ? "" : " · " + Diagnostic.Source);
    public string Glyph => GlyphFor(Diagnostic.Severity);
    public Brush GlyphBrush => DiagnosticMarkerRenderer.BrushFor(Diagnostic.Severity);

    public static string GlyphFor(LspSeverity severity) => severity switch
    {
        LspSeverity.Error => "",     // ErrorBadge
        LspSeverity.Warning => "",   // Warning
        _ => "",                     // Info
    };
}
