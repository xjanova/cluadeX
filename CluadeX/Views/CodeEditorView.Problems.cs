using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Services;
using CluadeX.Services.Lsp;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>
/// Vibe-coding upgrades to the editor (owner 2026-11-03: make what exists useful when the AI writes the code):
/// <list type="bullet">
/// <item>errors from the builds and checks the app already runs (<see cref="BuildProblemsService"/>) in the same
///   squiggles and Problems list as the language server's — no install needed — plus a Check button and a check of
///   the changed languages when an AI run ends;</item>
/// <item>the editor talking to the chat in one step: "fix / explain / change this" on a selection, "fix this error" on a
///   problem, "to chat" on search and reference results (<see cref="ChatViewModel.SendFromEditor"/>).</item>
/// </list>
/// </summary>
public partial class CodeEditorView
{
    private BuildProblemsService? _build;
    private CodeEditorViewModel? _buildVm;
    private bool _wasLocked;
    private IReadOnlyList<ProblemCheck> _checks = Array.Empty<ProblemCheck>();
    private string? _checksRoot;

    /// <summary>From InitializeLsp: this view's own controls only.</summary>
    private void InitializeBuildProblems()
    {
        _build = BuildProblemsService.Current;
        Editor.TextArea.ContextMenu = BuildEditorMenu();
    }

    private void HookBuildProblems()
    {
        if (_build != null) _build.Changed += OnBuildChanged;
        if (_vm != null && !ReferenceEquals(_buildVm, _vm))
        {
            _buildVm = _vm;
            _vm.PropertyChanged += OnVmLockChanged;
            _vm.SearchResults.CollectionChanged += OnSearchResultsChanged;
            _wasLocked = _vm.IsLockedByAgent;
            OnSearchResultsChanged(null, null);
            // The view did not exist when a run ended (the user was on the chat page): check now that they look.
            if (!_wasLocked) _ = AutoCheckAsync();
        }
    }

    private void DetachBuildProblems()
    {
        if (_build != null) _build.Changed -= OnBuildChanged;
        if (_buildVm != null)
        {
            _buildVm.PropertyChanged -= OnVmLockChanged;
            _buildVm.SearchResults.CollectionChanged -= OnSearchResultsChanged;
            _buildVm = null;
        }
    }

    private void OnBuildChanged() => OnLspEvent("");

    private void OnVmLockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CodeEditorViewModel.IsLockedByAgent) || _vm == null) return;
        bool locked = _vm.IsLockedByAgent;
        if (_wasLocked && !locked) _ = AutoCheckAsync();   // the AI run in this project ended
        _wasLocked = locked;
    }

    /// <summary>After an AI run: the safe checkers (no project code runs) of the languages it changed, unless it already
    /// ran them after its last edit. Builds that run project code stay with the agent's own verify step.</summary>
    private async Task AutoCheckAsync()
    {
        try
        {
            if (_build == null || _build.IsChecking || ProjectRoot is not { } root) return;
            var due = await Task.Run(() => _build.PendingAutoChecks(root));
            if (due.Count > 0) await Task.Run(() => _build.RunChecksAsync(root, due));
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(AutoCheckAsync), ex); }
    }

    private string? ProjectRoot => _vm?.ChatVM.WorkingDirectory is { Length: > 0 } dir ? dir : null;

    private bool InProject(string path) => ProjectRoot is not { } root || ChatViewModel.IsInsideFolder(path, root);

    /// <summary>The Problems item is worth showing even for a file no language server covers.</summary>
    private bool HasBuildContext => _checks.Count > 0 || (_build?.Count ?? 0) > 0;

    /// <summary>The server's diagnostics plus the runs' problems, without the same error twice (a build and the server
    /// both report CS1002 on line 12; a build and a test run both report the same compile error).</summary>
    internal static List<EditorDiagnostic> MergeDiagnostics(IReadOnlyList<EditorDiagnostic> server, IEnumerable<BuildProblem> build)
    {
        var merged = new List<EditorDiagnostic>(server);
        foreach (var p in build)
        {
            var d = p.ToDiagnostic();
            bool dup = merged.Any(x => x.StartLine == d.StartLine
                && (!string.IsNullOrEmpty(x.Code) && x.Code == d.Code || x.Message == d.Message));
            if (!dup) merged.Add(d);
        }
        return merged;
    }

    // ───────────────────────── the Check button ─────────────────────────

    private void UpdateBuildHeader()
    {
        if (_build == null) return;
        string? root = ProjectRoot;
        if (!string.Equals(root, _checksRoot, StringComparison.OrdinalIgnoreCase)) _ = RefreshChecksAsync(root);

        bool checking = _build.IsChecking;
        ProblemsCheck.Visibility = _checks.Count > 0 || checking ? Visibility.Visible : Visibility.Collapsed;
        ProblemsCheck.Content = checking ? L("problems.checkStop") : L("problems.checkNow");
        ProblemsCheck.ToolTip = _checks.Count > 0 ? LF("problems.checkTip", string.Join(" + ", _checks.Select(c => c.Label))) : null;

        var last = _build.LastOutcome;
        ProblemsBuildStatus.Text = checking ? LF("problems.checking", _build.CheckingLabel ?? "")
            : last == null ? ""
            : last.Cancelled ? L("problems.lastCheckCancelled")
            : last.TimedOut ? LF("problems.lastCheckTimeout", last.Label)
            : last.Note != null && last.Errors + last.Warnings == 0 ? LF("problems.lastCheckFailed", last.Label, last.Note)
            : LF("problems.lastCheck", last.Label, last.Errors, last.Warnings, last.FinishedUtc.ToLocalTime().ToString("HH:mm"));
        ProblemsBuildStatus.ToolTip = string.IsNullOrEmpty(ProblemsBuildStatus.Text) ? null : ProblemsBuildStatus.Text;
    }

    /// <summary>Which checkers the project has — looked up off the UI thread (it reads PATH and a few files).</summary>
    private async Task RefreshChecksAsync(string? root)
    {
        _checksRoot = root;
        _checks = Array.Empty<ProblemCheck>();
        if (_build == null || root == null) return;
        try
        {
            var build = _build;
            var found = await Task.Run(() => build.ChecksFor(root));
            if (!string.Equals(root, _checksRoot, StringComparison.OrdinalIgnoreCase)) return;   // the project changed meanwhile
            _checks = found;
            OnLspEvent("");
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(RefreshChecksAsync), ex); }
    }

    private void OnProblemsCheck(object sender, RoutedEventArgs e)
    {
        if (_build == null) return;
        if (_build.IsChecking) { _build.CancelCheck(); return; }
        if (ProjectRoot is not { } root) return;
        var build = _build;
        _ = Task.Run(async () =>
        {
            try { await build.CheckNowAsync(root); }
            catch (Exception ex) { CommandErrorSink.Report(nameof(OnProblemsCheck), ex); }
        });
        OnLspEvent("");
    }

    // ───────────────────────── editor → chat ─────────────────────────

    /// <summary>Send to the chat (into the run working here, if any) and say where it went.</summary>
    private void SendToChat(string prompt)
    {
        if (_vm == null) return;
        var result = _vm.ChatVM.SendFromEditor(prompt, ProjectRoot);
        string? said = result switch
        {
            EditorSendResult.Sent => L("problems.sent"),
            EditorSendResult.Queued => L("problems.queued"),
            EditorSendResult.InBox => L("problems.inBox"),
            _ => null,
        };
        if (said != null) Say(said);
    }

    private ContextMenu BuildEditorMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, ICommand? command = null, RoutedEventHandler? click = null)
        {
            var item = new MenuItem { Header = L(key) };
            if (command != null) { item.Command = command; item.CommandTarget = Editor.TextArea; }
            if (click != null) item.Click += click;
            menu.Items.Add(item);
            return item;
        }
        Item("editor.menu.cut", ApplicationCommands.Cut);
        Item("editor.menu.copy", ApplicationCommands.Copy);
        Item("editor.menu.paste", ApplicationCommands.Paste);
        menu.Items.Add(new Separator());
        var ai = new List<MenuItem>
        {
            Item("editor.menu.askFix", click: (_, _) => AskAboutSelection("editor.ask.fix", send: true)),
            Item("editor.menu.askExplain", click: (_, _) => AskAboutSelection("editor.ask.explain", send: true)),
            Item("editor.menu.askChange", click: (_, _) => AskAboutSelection("editor.ask.change", send: false)),
            Item("editor.menu.toChat", click: (_, _) => AskAboutSelection("editor.ask.context", send: false)),
        };
        menu.Opened += (_, _) =>
        {
            foreach (var item in ai) item.IsEnabled = _boundTab != null && _vm != null;
        };
        return menu;
    }

    /// <summary>The selection (or the caret's line), with its file and lines, as a chat message: sent for fix / explain,
    /// put in the box for "change…" (the user says what to change) and "add as context".</summary>
    private void AskAboutSelection(string templateKey, bool send)
    {
        try
        {
            if (_boundTab == null || _vm == null) return;
            var doc = Editor.Document;
            int start = Editor.SelectionLength > 0 ? Editor.SelectionStart : doc.GetLineByOffset(Editor.CaretOffset).Offset;
            int end = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : doc.GetLineByOffset(Editor.CaretOffset).EndOffset;
            var first = doc.GetLineByOffset(start);
            var last = doc.GetLineByOffset(end);
            // A selection ending at the start of a line does not include that line.
            if (last.LineNumber > first.LineNumber && end == last.Offset) last = last.PreviousLine;
            string code = doc.GetText(start, end - start);
            if (code.Trim().Length == 0) return;
            string prompt = EditorChatPrompts.Selection(L(templateKey), RelativeName(_boundTab.FullPath), first.LineNumber, last.LineNumber,
                code, EditorChatPrompts.LanguageOf(_boundTab.FullPath));
            if (send) SendToChat(prompt);
            else
            {
                _vm.ChatVM.AddToChatBox(prompt);
                Say(L(templateKey == "editor.ask.change" ? "editor.askChangeHint" : "editor.contextAdded"));
            }
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(AskAboutSelection), ex); }
    }

    private void OnSearchResultsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        if (_buildVm == null) return;
        SearchToChat.Visibility = _buildVm.SearchResults.Count > 0 && !_buildVm.IsRenameMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Search / Find References results into the chat box as context — the user adds what to do with them.</summary>
    private void OnSearchToChat(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm == null || _vm.SearchResults.Count == 0) return;
            var rows = _vm.SearchResults.SelectMany(g => g.Matches)
                .Select(m => (RelativeName(m.FullPath), m.Line, m.LineText ?? "")).ToList();
            string what = _vm.HasSearchContext ? _vm.SearchContextLabel : LF("search.toChatQuery", _vm.SearchText ?? "");
            string text = EditorChatPrompts.Results(LF("search.toChatHeader", what, rows.Count), rows, L("search.toChatMore"));
            _vm.ChatVM.AddToChatBox(text);
            Say(L("editor.contextAdded"));
        }
        catch (Exception ex) { CommandErrorSink.Report(nameof(OnSearchToChat), ex); }
    }
}
