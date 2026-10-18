using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.ViewModels;

/// <summary>The chat on screen as the UI Studio's AI.</summary>
internal sealed class ChatStudioAi : IStudioAi
{
    private readonly ChatViewModel _chat;

    public ChatStudioAi(ChatViewModel chat)
    {
        _chat = chat;
        chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatViewModel.IsGenerating)) BusyChanged?.Invoke(); };
    }

    public bool IsBusy => _chat.IsStudioRunGoing;
    public bool Send(string prompt) => _chat.SendFromStudio(prompt);
    public event Action? BusyChanged;
}

/// <summary>
/// A round of changes to a simulated (Flutter) app's screens — by the AI or with the Studio's tools — that the user
/// reviews before it counts: the screens are drawn again live while the AI edits (the stage first), then the user
/// keeps it, takes all of it back, or asks for more. Keeping it runs the save check: <c>dart analyze</c> on the
/// changed files and every screen drawn again; what is wrong (and, for widgets the user placed with the tools, how
/// they fit the screen) goes to the AI to fix, then the check runs again — up to <see cref="MaxFixRounds"/> times.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private sealed class ChangeSession
    {
        /// <summary>Each file's text before the round (null: it did not exist).</summary>
        public readonly Dictionary<string, string?> Before = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every file of lib/ was read at the start (the AI may change any; a new file is one not read).</summary>
        public bool FullSnapshot;
        public bool AiAsked;
        /// <summary>The AI has looked over the tools' changes since the last one.</summary>
        public bool Polished;
        public int ToolEdits;
        public DateTime Started = DateTime.UtcNow;
        /// <summary>Files of lib/ the snapshot left out (too big, past the cap): never taken for new files.</summary>
        public readonly HashSet<string> Unread = new(StringComparer.OrdinalIgnoreCase);
        public ImageSource? BeforeImage;
        /// <summary>The screen <see cref="BeforeImage"/> shows.</summary>
        public string? BeforeScreen;
        /// <summary>What Flutter reported on each screen when the round began — only new problems count against it.</summary>
        public readonly Dictionary<string, HashSet<string>> BeforeProblems = new();
    }

    private ChangeSession? _session;
    /// <summary>Files the tools wrote (path → the text written): their watcher events are the Studio's own.</summary>
    private readonly Dictionary<string, string> _selfWrites = new(StringComparer.OrdinalIgnoreCase);
    internal const int MaxFixRounds = 3;

    private IStudioAi? _ai;
    /// <summary>The AI the Studio asks: the chat on screen (a harness puts a scripted one here).</summary>
    public IStudioAi StudioAi
    {
        get => _ai ??= Hook(new ChatStudioAi(_chat));
        set
        {
            if (_ai != null) _ai.BusyChanged -= OnAiBusyChanged;
            _ai = Hook(value);
            OnAiBusyChanged();
        }
    }

    private IStudioAi Hook(IStudioAi ai)
    {
        ai.BusyChanged += OnAiBusyChanged;
        return ai;
    }

    private void OnAiBusyChanged()
    {
        RaiseSession();
        if (!AiWorking) RefreshTools();
    }

    private static readonly string[] SessionProps =
    {
        nameof(AiWorking), nameof(HasSession), nameof(SessionReview), nameof(SessionText), nameof(HasBeforeImage), nameof(ShowBefore),
        nameof(StageRenderShown), nameof(StageBoxesShown), nameof(ShowSessionBar), nameof(HasCheckResult), nameof(CheckFailed), nameof(CheckOk),
    };

    private void RaiseSession()
    {
        foreach (var n in SessionProps) OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ───────────────────────────── state shown ─────────────────────────────

    /// <summary>The AI is writing the change the Studio asked for (the screens are drawn again as it saves).</summary>
    public bool AiWorking => _session?.AiAsked == true && StudioAi.IsBusy;
    /// <summary>A round with changes to review (or the AI still on it).</summary>
    public bool HasSession => _session != null && (_session.Changed.Count > 0 || AiWorking);
    public bool SessionReview => HasSession && !AiWorking && !IsChecking;
    public bool ShowSessionBar => HasSession || IsChecking || HasCheckResult;
    public string SessionText
    {
        get
        {
            var s = _session;
            if (s == null) return "";
            int files = s.Changed.Count;
            if (AiWorking) return files == 0 ? T("studio.session.aiStarting") : T("studio.session.aiWorking", files);
            return s.ToolEdits > 0 ? T("studio.session.changedTools", files, s.ToolEdits) : T("studio.session.changed", files);
        }
    }

    private bool _showBefore;
    /// <summary>The stage shows the screen as it was when the round began.</summary>
    public bool ShowBefore
    {
        get => _showBefore && HasBeforeImage;
        set { if (SetProperty(ref _showBefore, value)) { OnPropertyChanged(nameof(StageRenderShown)); OnPropertyChanged(nameof(StageBoxesShown)); } }
    }
    public bool HasBeforeImage => _session?.BeforeImage != null && _session.Changed.Count > 0 && _session.BeforeScreen == _stageCard?.Page.Id;
    public ImageSource? StageRenderShown => ShowBefore ? _session!.BeforeImage : _stageRender;
    public IReadOnlyList<WidgetBox> StageBoxesShown => ShowBefore ? Array.Empty<WidgetBox>() : _stageBoxes;

    private string _reviseText = "";
    public string ReviseText { get => _reviseText; set => SetProperty(ref _reviseText, value ?? ""); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) RaiseSession(); } }
    private string _checkStatus = "";
    public string CheckStatus { get => _checkStatus; private set => SetProperty(ref _checkStatus, value); }
    /// <summary>What the last save check found (problems, or notes on what it could not check).</summary>
    public ObservableCollection<string> CheckFindings { get; } = new();
    private bool? _checkPassed;
    public bool? CheckPassed { get => _checkPassed; private set { if (SetProperty(ref _checkPassed, value)) RaiseSession(); } }
    public bool HasCheckResult => _checkPassed != null && !IsChecking;
    public bool CheckFailed => _checkPassed == false && !IsChecking;
    public bool CheckOk => _checkPassed == true && !IsChecking;
    private CancellationTokenSource? _checkCts;

    // ───────────────────────────── the round ─────────────────────────────

    /// <summary>The round in progress (a new one when the last ended or changed nothing).</summary>
    private ChangeSession Session()
    {
        if (_session is { } cur && (cur.Changed.Count > 0 || AiWorking || IsChecking)) return cur;
        var s = new ChangeSession { BeforeImage = _stageRender, BeforeScreen = _stageCard?.Page.Id };
        if (_store != null)
            foreach (var c in _allCards.Where(c => c.IsScreen))
                if (_store.Get(c.Page.Id, "app") is { } e)
                    s.BeforeProblems[c.Page.Id] = ReadRenderSidecar(_store.ReadSidecar(e)).Problems.Select(ProblemKey).ToHashSet();
        _session = s;
        CheckPassed = null;
        return s;
    }

    private void EndSession()
    {
        _session = null;
        _showBefore = false;
        RaiseSession();
    }

    /// <summary>The same problem drawn twice reads the same ("overflowed by 31 pixels" vs "by 29 pixels").</summary>
    private static string ProblemKey(string p) => Regex.Replace(p, @"\d+(\.\d+)?", "#").Trim();

    /// <summary>An edit made with the Studio's tools: the file joins the round (its text before the first edit is kept).</summary>
    private void NoteToolEdit(string path, string before, string after)
    {
        var s = Session();
        if (!s.Before.ContainsKey(path)) s.Before[path] = before;
        s.Changed.Add(path);
        s.ToolEdits++;
        s.Polished = false;
        _selfWrites[path] = after;
        RaiseSession();
    }

    /// <summary>Read every file of lib/ (and pubspec.yaml) once, before the AI may change any of them.</summary>
    private void SnapshotLib(ChangeSession s)
    {
        if (s.FullSnapshot || !_studio.HasProject) return;
        int n = 0;
        string root = Path.GetFullPath(_studio.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // (translations kept outside lib/ too: arb-dir: l10n)
        var arbs = (_l10n?.ArbFiles ?? Array.Empty<string>()).Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        foreach (var f in AppFiles(_studio.Root).Concat(arbs).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (s.Before.ContainsKey(f)) continue;
            try
            {
                if (++n > 6000 || new FileInfo(f).Length > 3_000_000) { s.Unread.Add(f); continue; }
                s.Before[f] = CluadeX.Helpers.TextFileCodec.Read(f).Text;
            }
            catch { s.Unread.Add(f); }
        }
        s.FullSnapshot = true;
    }

    private bool IsSnapshotKind(string path)
    {
        string root = _studio.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.Equals(path, root + "pubspec.yaml", StringComparison.OrdinalIgnoreCase)) return true;
        if (_l10n?.IsArb(path) == true && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(root + "lib" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               && (path.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".arb", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> AppFiles(string root)
    {
        string lib = Path.Combine(root, "lib");
        if (Directory.Exists(lib))
            foreach (var f in Directory.EnumerateFiles(lib, "*.*", SearchOption.AllDirectories))
                if (f.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".arb", StringComparison.OrdinalIgnoreCase)) yield return f;
        string pub = Path.Combine(root, "pubspec.yaml");
        if (File.Exists(pub)) yield return pub;
    }

    /// <summary>A file of the app changed (seen by the watcher, on the UI thread).</summary>
    private void NoteSourceChanged(string path)
    {
        if (_selfWrites.TryGetValue(path, out var mine))
        {
            if (ReadSource(path) == mine) return;   // the Studio's own edit
            _selfWrites.Remove(path);
        }
        // changed by someone else (the AI, the editor): a pick in that file may point at another widget now
        if (_stageBox is { } b && SameFile(b.File, path)) { _pendingFocus = null; StageBox = null; }
        if (_session is not { } s) return;
        // a new file (undo all deletes it): one of the kinds the snapshot reads, not read at the start, and made since —
        // anything else (an asset, a file the snapshot skipped) is never taken for new
        if (!s.Before.ContainsKey(path) && s.FullSnapshot && !s.Unread.Contains(path) && IsSnapshotKind(path) && !path.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path) && File.GetCreationTimeUtc(path) >= s.Started.AddSeconds(-2)) s.Before[path] = null;
            }
            catch { }
        }
        if (s.Before.ContainsKey(path)) s.Changed.Add(path);
        RaiseSession();
    }

    // ───────────────────────────── asking the AI ─────────────────────────────

    /// <summary>Send a request about the stage screen; the round begins (every file read first) and the stage shows the
    /// change live.</summary>
    private bool AskAi(string prompt)
    {
        if (!IsFlutterApp)
        {
            // a WPF app's views are wireframes from its XAML: no live redraw, no review round — the request goes as is
            if (!StudioAi.Send(prompt)) { Status(T("studio.ai.busy"), error: true); return false; }
            Status(T("studio.ai.sent"));
            return true;
        }
        var s = Session();
        SnapshotLib(s);
        if (!StudioAi.Send(prompt))
        {
            Status(T("studio.ai.busy"), error: true);
            return false;
        }
        s.AiAsked = true;
        ShowBefore = false;
        CheckPassed = null;
        RaiseSession();
        Status(T("studio.session.aiStarted"));
        return true;
    }

    private void Revise()
    {
        if (_stageCard == null || string.IsNullOrWhiteSpace(ReviseText)) return;
        if (AskAi(BuildStagePrompt(ReviseText.Trim(), revising: true))) ReviseText = "";
    }

    // ───────────────────────────── undo all ─────────────────────────────

    private void UndoAll()
    {
        var s = _session;
        if (s == null || AiWorking || IsChecking) return;
        var changed = s.Changed.Where(p => s.Before.ContainsKey(p)).ToList();
        if (changed.Count == 0) { EndSession(); return; }
        int created = changed.Count(p => s.Before[p] == null && File.Exists(p));
        bool go = ToolConfirmOverride ?? Views.Editor.ChoiceDialog.Show(T("studio.session.undoTitle"), T("studio.session.undoBody", changed.Count - created, created),
            new[] { T("studio.session.undoAll"), T("studio.cancel") }, 1) == 0;
        if (!go) return;
        var failed = _studio.RestoreFiles(changed.ToDictionary(p => p, p => s.Before[p], StringComparer.OrdinalIgnoreCase));
        foreach (var p in changed) _selfWrites.Remove(p);
        _pendingFocus = null;
        StageBox = null;
        EndSession();
        CheckPassed = null;
        OnPropertyChanged(nameof(CanUndo));
        Status(failed.Count == 0 ? T("studio.session.undone", changed.Count) : T("studio.session.undoFailed", failed.Count), error: failed.Count > 0);
    }

    // ───────────────────────────── the save check ─────────────────────────────

    private sealed record Issue(string Text, bool Blocking);

    /// <summary>Keep the round: check it (dart analyze, every screen drawn again), let the AI fix what is wrong and fit
    /// what the user placed, check again — then the round is over.</summary>
    private async Task KeepAsync()
    {
        var s = _session;
        if (s == null || IsChecking) return;
        if (_catalog?.Flutter is not { } flutter) { EndSession(); return; }
        IsChecking = true;
        CheckPassed = null;
        CheckFindings.Clear();
        ShowBefore = false;
        var cts = _checkCts = CancellationTokenSource.CreateLinkedTokenSource(_pagesCts.Token);
        var ct = cts.Token;
        bool passed = false;
        string? note = null;
        try
        {
            for (int round = 0; ; round++)
            {
                CheckStatus = T("studio.check.waiting");
                await WaitAiIdleAsync(ct);
                await WaitRenderIdleAsync(ct);
                var (issues, notes) = await FindIssuesAsync(s, flutter, ct);
                CheckFindings.Clear();
                foreach (var i in issues) CheckFindings.Add(i.Text);
                foreach (var n in notes) CheckFindings.Add(n);
                bool polish = s.ToolEdits > 0 && !s.Polished;
                var blocking = issues.Where(i => i.Blocking).ToList();
                if (blocking.Count == 0 && !polish) { passed = true; break; }
                if (round >= MaxFixRounds) break;
                CheckStatus = blocking.Count == 0 ? T("studio.check.polishing") : T("studio.check.fixing", round + 1, MaxFixRounds);
                SnapshotLib(s);
                if (!StudioAi.Send(BuildCheckPrompt(s, blocking, polish)))
                {
                    // no AI to ask: what the checks found is the answer
                    note = T("studio.check.noAi");
                    passed = blocking.Count == 0;
                    break;
                }
                s.AiAsked = true;
                if (polish) s.Polished = true;
                RaiseSession();
                await WaitAiRunAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_checkCts, cts)) _checkCts = null;
            cts.Dispose();
            IsChecking = false;
            CheckStatus = T("studio.check.stopped");
            return;
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "save check failed", ex); note = T("studio.check.error"); }
        if (ReferenceEquals(_checkCts, cts)) _checkCts = null;
        cts.Dispose();
        if (note != null) CheckFindings.Insert(0, note);
        IsChecking = false;
        CheckPassed = passed;
        if (passed)
        {
            CheckStatus = T("studio.check.passed");
            Status(T("studio.check.passedStatus"));
            EndSession();
        }
        else CheckStatus = T("studio.check.failed", CheckFindings.Count);
    }

    /// <summary>What is wrong with the round now: analyzer errors in the changed files (and warnings on the lines it
    /// changed), a compile error, problems Flutter reported on a screen that it did not have before. Notes say what
    /// could not be checked.</summary>
    private async Task<(List<Issue> Issues, List<string> Notes)> FindIssuesAsync(ChangeSession s, FlutterProject flutter, CancellationToken ct)
    {
        var issues = new List<Issue>();
        var notes = new List<string>();
        var dart = s.Changed.Where(p => p.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (dart.Count > 0)
        {
            CheckStatus = T("studio.check.analyzing", dart.Count);
            var an = await FlutterRenderer.AnalyzeAsync(flutter, dart, ct, line => _log?.Info("UiStudio", "analyze: " + line));
            ct.ThrowIfCancellationRequested();
            if (an.Error != null) notes.Add(T("studio.check.noAnalyze", T(an.Error)));
            var changedLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in an.Issues.OrderBy(i => i.Severity == "ERROR" ? 0 : 1).ThenBy(i => i.File).ThenBy(i => i.Line))
            {
                if (i.Severity == "INFO") continue;
                if (i.Severity == "WARNING")
                {
                    if (i.Code is "UNUSED_IMPORT" or "UNUSED_LOCAL_VARIABLE" or "UNUSED_ELEMENT" or "UNUSED_FIELD" or "DEAD_CODE" or "UNUSED_SHOWN_NAME") continue;
                    if (!changedLines.TryGetValue(i.File, out var lines)) changedLines[i.File] = lines = ChangedLines(s, i.File);
                    if (!lines.Contains(i.Line)) continue;   // a warning the round did not make
                }
                issues.Add(new Issue($"{Rel(i.File)}:{i.Line}:{i.Column} — {i.Message} ({i.Code.ToLowerInvariant()})", Blocking: true));
                if (issues.Count >= 25) break;
            }
        }
        if (RenderAllowed(ask: false))
        {
            CheckStatus = T("studio.check.drawing");
            var cards = ScreenCards.ToList();
            RenderScreens(cards);
            await WaitRenderIdleAsync(ct);
            if (cards.FirstOrDefault(c => _compileErrors.ContainsKey(c)) is { } broken)
                issues.Add(new Issue(T("studio.check.compile") + "\n" + Clip(_compileErrors[broken], 1500), Blocking: true));
            else if (_store != null)
                foreach (var c in cards)
                {
                    if (_store.Get(c.Page.Id, "app") is not { } e) continue;
                    s.BeforeProblems.TryGetValue(c.Page.Id, out var before);
                    foreach (var p in ReadRenderSidecar(_store.ReadSidecar(e)).Problems.Where(p => before == null || !before.Contains(ProblemKey(p))).Take(3))
                        issues.Add(new Issue(T("studio.check.screenProblem", c.Title, c.Page.Route ?? c.Page.Detail ?? "") + " " + Clip(p, 500), Blocking: true));
                }
        }
        else notes.Add(T("studio.check.noRender"));
        return (issues, notes);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>The lines of a file the round changed (lines of the file now).</summary>
    private HashSet<int> ChangedLines(ChangeSession s, string file)
    {
        var set = new HashSet<int>();
        if (!s.Before.TryGetValue(file, out var before)) return set;
        string now = ReadSource(file) ?? "";
        if (before == null) { for (int i = 1; i <= now.Split('\n').Length; i++) set.Add(i); return set; }
        foreach (var row in DiffService.BuildSideBySide(before, now).Rows)
            if (row.IsChange && row.RightLine is int r) set.Add(r);
        return set;
    }

    private async Task WaitAiIdleAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (StudioAi.IsBusy && sw.Elapsed < TimeSpan.FromMinutes(30)) await Task.Delay(300, ct);
    }

    /// <summary>After a request: until the AI has started and finished (a reply that fails at once is over at once).</summary>
    private async Task WaitAiRunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!StudioAi.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100, ct);
        await WaitAiIdleAsync(ct);
    }

    /// <summary>Until no draw is running or waiting (a save's watcher event and its pause count as waiting).</summary>
    private async Task WaitRenderIdleAsync(CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var sw = Stopwatch.StartNew();
        while ((_rendering || _refreshing || _renderWanted.Count > 0 || _renderDebounce?.IsEnabled == true || _sourcesChanged) && sw.Elapsed < TimeSpan.FromMinutes(10))
            await Task.Delay(200, ct);
    }

    /// <summary>What the AI gets at the save check: the round's diff, what is wrong, and how to fit the user's widgets.</summary>
    private string BuildCheckPrompt(ChangeSession s, List<Issue> issues, bool polish)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[UI Studio — save check] The user is saving changes to screens of this Flutter app. " +
                      (issues.Count > 0 ? "The check found problems: fix them." : "Look over what the user placed and changed with the UI Studio's tools, and make it fit the screen."));
        sb.AppendLine();
        if (_stageCard is { } c)
            sb.AppendLine($"Screen on the stage: {c.Page.Detail} — \"{c.Title}\"" + (c.Page.Route != null ? $", route {c.Page.Route}" : "") + (c.Page.SourcePath != null ? $", file {Rel(c.Page.SourcePath)}" : ""));
        sb.AppendLine();
        sb.AppendLine("What changed in this round (unified diff, before → now):");
        sb.AppendLine("```diff");
        int budget = 14_000;
        foreach (var path in s.Changed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!s.Before.TryGetValue(path, out var before)) continue;
            string now = File.Exists(path) ? ReadSource(path) ?? "" : "";
            string d = UnifiedDiff(Rel(path), before, now, File.Exists(path));
            if (d.Length == 0) continue;
            if (d.Length > budget) { sb.AppendLine($"--- {Rel(path)} (diff too long — read the file)"); continue; }
            budget -= d.Length;
            sb.Append(d);
        }
        sb.AppendLine("```");
        if (issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Problems found (dart analyze, and drawing every screen with flutter test):");
            foreach (var i in issues) sb.AppendLine("- " + i.Text.Replace("\n", "\n  "));
        }
        sb.AppendLine();
        sb.AppendLine("What to do:");
        sb.AppendLine("1. Fix every problem listed (a compile error first).");
        if (polish || s.ToolEdits > 0)
            sb.AppendLine("2. The user placed or changed widgets with the UI Studio's tools (in the diff). Keep what they meant — the widgets, their text, " +
                          "their order — and make them fit: spacing consistent with the widgets around them, the app's theme (Theme.of(context).colorScheme / " +
                          "textTheme where the screen already uses the theme, instead of hard-coded colours and sizes), alignment, and no overflow on a 390-wide phone. " +
                          "Use const where the lints want it.");
        sb.AppendLine((polish || s.ToolEdits > 0 ? "3" : "2") + ". Change only what that needs — no redesign, no other screens. Save the files: the UI Studio draws the screens and checks again by itself.");
        return sb.ToString();
    }

    /// <summary>A compact unified diff of one file (2 lines of context; a new or deleted file as a whole).</summary>
    internal static string UnifiedDiff(string rel, string? before, string now, bool exists)
    {
        var sb = new StringBuilder();
        if (before == null) { sb.AppendLine($"+++ {rel} (new file)"); foreach (var l in now.Split('\n').Take(200)) sb.AppendLine("+" + l.TrimEnd('\r')); return sb.ToString(); }
        if (!exists) { sb.AppendLine($"--- {rel} (deleted)"); return sb.ToString(); }
        var rows = DiffService.BuildSideBySide(before, now).Rows;
        if (!rows.Any(r => r.IsChange)) return "";
        sb.AppendLine($"--- a/{rel}");
        sb.AppendLine($"+++ b/{rel}");
        var keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsChange)
                for (int k = Math.Max(0, i - 2); k <= Math.Min(rows.Count - 1, i + 2); k++) keep[k] = true;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!keep[i]) continue;
            if (i == 0 || !keep[i - 1]) sb.AppendLine($"@@ line {rows[i].RightLine ?? rows[i].LeftLine ?? 1} @@");
            var r = rows[i];
            switch (r.Kind)
            {
                case DiffRowKind.Unchanged: sb.AppendLine(" " + r.RightText); break;
                case DiffRowKind.Added: sb.AppendLine("+" + r.RightText); break;
                case DiffRowKind.Removed: sb.AppendLine("-" + r.LeftText); break;
                default: sb.AppendLine("-" + r.LeftText); sb.AppendLine("+" + r.RightText); break;
            }
        }
        return sb.ToString();
    }

    // ───────────────────────────── commands ─────────────────────────────

    private ICommand? _keepChangesCommand, _undoAllCommand, _reviseCommand, _stopCheckCommand, _dismissCheckCommand, _toggleBeforeCommand;

    public ICommand KeepChangesCommand => _keepChangesCommand ??= new AsyncRelayCommand(KeepAsync, () => SessionReview);
    public ICommand UndoAllCommand => _undoAllCommand ??= new RelayCommand(UndoAll, () => SessionReview);
    public ICommand ReviseCommand => _reviseCommand ??= new RelayCommand(Revise, () => SessionReview && !string.IsNullOrWhiteSpace(ReviseText));
    public ICommand StopCheckCommand => _stopCheckCommand ??= new RelayCommand(() => _checkCts?.Cancel(), () => IsChecking);
    public ICommand DismissCheckCommand => _dismissCheckCommand ??= new RelayCommand(() => { CheckPassed = null; CheckFindings.Clear(); CheckStatus = ""; });
    public ICommand ToggleBeforeCommand => _toggleBeforeCommand ??= new RelayCommand(() => ShowBefore = !ShowBefore, () => HasBeforeImage);
}
