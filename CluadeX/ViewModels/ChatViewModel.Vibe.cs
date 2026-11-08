using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Lsp;
using CluadeX.Services.Problems;

namespace CluadeX.ViewModels;

/// <summary>
/// The chat's helpers for people who build apps without reading code (owner 2026-11-08: "หน้าแชทควรมีอะไรอีกที่ทำให้
/// ทำงานง่ายและสนุก"): what a turn changed, as a card at its end; the project's live errors as a chip by the composer
/// that hands them to the AI; "explain simply" on any reply; @-mentions of the project's files and names; a click on
/// the side panel's picture to ask about that spot; and how full the AI's memory is, next to the cost.
/// </summary>
public partial class ChatViewModel
{
    private void InitVibe()
    {
        ChatMessage.TurnChangesFor = m => TurnChanges.FromJson(m.ToolOutput) is { } data
            ? new TurnChangesView(data.Files, data.Before, data.After, RequestOpenFile)
            : null;
        HookBuildProblems();
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChatProject) or nameof(WorkingDirectory) or nameof(HasProject):
                    QueueErrorChip();
                    _mentionIndex = null;
                    break;
                case nameof(ContextUsagePercent) or nameof(ContextUsageText) or nameof(SessionTurnCount):
                    RefreshContextGauge(e.PropertyName == nameof(SessionTurnCount));
                    break;
                case nameof(CurrentSession):
                    RefreshContextGauge(false);
                    break;
            }
        };
        LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshErrorChip();
            RefreshContextGauge(false);
        });
    }

    // ═══ 1. What a turn changed ═══

    /// <summary>A file-writing tool of <paramref name="run"/> succeeded, or a tool returned a picture.</summary>
    private static void NoteTurnChange(ChatRun? run, ToolResult result, string? folder, string? thumbnail)
    {
        if (run == null) return;
        if (thumbnail != null) run.Changes.NoteImage(thumbnail);
        if (!result.Success || result.Diff is not { HasChanges: true } diff || string.IsNullOrEmpty(result.FilePath)) return;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(result.FilePath) || string.IsNullOrEmpty(folder) ? result.FilePath! : Path.Combine(folder, result.FilePath!)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        string display = result.FilePath!;
        if (!string.IsNullOrEmpty(folder))
        {
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    display = Path.GetRelativePath(root, full);
            }
            catch (ArgumentException) { }
        }
        // A whole-file write that removed nothing made the file (an overwrite shows its old lines as removed).
        bool created = result.ToolName is "write_file" or "create_file" or "Write" && diff.Removed == 0;
        run.Changes.Note(full, display, diff.Added, diff.Removed, created);
    }

    /// <summary>The newest picture in the chat when a turn begins: the "before" of a turn that takes none itself.</summary>
    private static string? NewestPicture(IEnumerable<ChatMessage> messages) =>
        messages.Reverse().Take(400).Select(m => m.ThumbnailPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));

    /// <summary>At the end of a turn that wrote files: the change card (before the next-step box).</summary>
    private void AddTurnChangesCard(ChatRun run)
    {
        if (run.Changes.Count == 0) return;
        run.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.TurnChangesTag,
            ToolOutput = run.Changes.ToJson(),
            // the row's text: what a reloaded chat without the card, a copy, or the model's history shows
            Content = "📝 " + TurnChanges.PlainLine(run.Changes.Files, Loc),
        });
    }

    // ═══ 2. The error chip by the composer ═══

    private BuildProblemsService? _problems;
    private DispatcherTimer? _errorChipTimer;
    private List<BuildProblem> _chipErrors = new();
    private string _errorChipText = "", _errorChipTip = "";

    /// <summary>"พบข้อผิดพลาด 3 จุด — ให้แก้" ("" = none in this chat's project).</summary>
    public string ErrorChipText { get => _errorChipText; private set => SetProperty(ref _errorChipText, value); }
    public string ErrorChipTip { get => _errorChipTip; private set => SetProperty(ref _errorChipTip, value); }
    public bool HasErrorChip => _errorChipText.Length > 0;

    private ICommand? _fixErrorsCommand;
    public ICommand FixErrorsCommand => _fixErrorsCommand ??= new RelayCommand(FixErrors, () => _chipErrors.Count > 0);

    /// <summary>Listen to the app's one BuildProblemsService (App makes it before the window). Called again whenever
    /// the chip is recounted, so one made later — a harness, a test host — is picked up too; it subscribes once.</summary>
    private void HookBuildProblems()
    {
        var now = BuildProblemsService.Current;
        if (now == null || ReferenceEquals(now, _problems)) return;
        if (_problems != null) _problems.Changed -= OnBuildProblemsChanged;
        _problems = now;
        _problems.Changed += OnBuildProblemsChanged;
        QueueErrorChip();
    }

    private void OnBuildProblemsChanged() => App.Current?.Dispatcher.BeginInvoke(QueueErrorChip);

    /// <summary>A build that reports hundreds of lines raises Changed for each batch: one recount per 300 ms.</summary>
    private void QueueErrorChip()
    {
        if (_errorChipTimer == null)
        {
            _errorChipTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _errorChipTimer.Tick += (_, _) => { _errorChipTimer!.Stop(); RefreshErrorChip(); };
        }
        _errorChipTimer.Stop();
        _errorChipTimer.Start();
    }

    /// <summary>The project the chip counts for: the chat's own, else the window's.</summary>
    private string ErrorRoot => ChatProject.Length > 0 ? ChatProject : HasProject ? WorkingDirectory : "";

    private void RefreshErrorChip()
    {
        HookBuildProblems();
        var errors = ErrorsIn(_problems?.All(BuildProblemsService.MaxEntries) ?? Array.Empty<BuildProblemEntry>(), ErrorRoot);
        _chipErrors = errors;
        ErrorChipText = errors.Count > 0 ? LocF("vibe.errors.chip", errors.Count) : "";
        ErrorChipTip = errors.Count > 0
            ? LocF("vibe.errors.tip", string.Join(", ", errors.Select(e => e.Tool).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(3)))
            : "";
        OnPropertyChanged(nameof(HasErrorChip));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The errors (not warnings, not stale — the file changed after the run that found them) in files under
    /// <paramref name="root"/>, one per place. Pure: the harness checks it.</summary>
    public static List<BuildProblem> ErrorsIn(IEnumerable<BuildProblemEntry> entries, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return new();
        string r;
        try { r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
        catch (ArgumentException) { return new(); }
        return entries.Where(e => !e.Stale && e.Problem.Severity == LspSeverity.Error)
            .Select(e => e.Problem)
            .Where(p =>
            {
                try { return Path.GetFullPath(p.Path).StartsWith(r, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            })
            .DistinctBy(p => (p.Path.ToLowerInvariant(), p.Line, p.Column, p.Message))
            .ToList();
    }

    private void FixErrors()
    {
        if (_chipErrors.Count == 0) return;
        string prompt = VibePrompts.FixErrors(Loc("vibe.errors.prompt"), Loc("vibe.errors.more"), ErrorRoot,
            _chipErrors.Select(e => (e.Path, e.Line, e.Column, e.Message, e.Code)).ToList());
        SendForOwner(prompt);
    }

    /// <summary>A message the owner asked for with a click (not typed): into the running reply when one runs, else as
    /// the next turn. The owner's draft in the box stays. The words are the app's, so they never count as the owner
    /// asking for a workflow or a design.</summary>
    private void SendForOwner(string text)
    {
        // a double click (or a second click while the first is still starting) sends it once
        var now = DateTime.UtcNow;
        if (text == _lastOwnerClickText && now - _lastOwnerClickUtc < TimeSpan.FromSeconds(3)) return;
        _lastOwnerClickText = text;
        _lastOwnerClickUtc = now;
        string draft = UserInput ?? "";
        if (CurrentSession is { } s)
        {
            WorkflowGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
            DesignGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
        }
        if (IsGenerating)
        {
            SendIntoRunningReply(text);
            StatusText = Loc("vibe.errors.busy");
        }
        else _ = SendMessageCore(text, pipe: null);
        if (draft.Length > 0 && string.IsNullOrEmpty(UserInput)) UserInput = draft;
    }

    private string? _lastOwnerClickText;
    private DateTime _lastOwnerClickUtc;

    // ═══ 3. Explain simply ═══

    /// <summary>The reply again in plain words, for someone who does not read code (the reply quoted, so the model
    /// knows which one even far up the chat).</summary>
    public void ExplainSimply(ChatMessage reply)
    {
        if (reply is not { Role: MessageRole.Assistant } || string.IsNullOrWhiteSpace(reply.Content)) return;
        SendForOwner(VibePrompts.ExplainSimply(Loc("vibe.explain.prompt"), reply.Content));
    }

    // ═══ 4. @-mentions ═══

    private sealed record MentionIndex(string Root, DateTime BuiltUtc, IReadOnlyList<string> Files, IReadOnlyList<(string Name, string File, int Line)> Symbols);
    private MentionIndex? _mentionIndex;
    private Task<MentionIndex?>? _mentionBuild;

    private static readonly HashSet<string> SymbolExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".dart", ".go", ".rs", ".java", ".kt", ".swift", ".php", ".vue" };

    /// <summary>The project's files and the names declared in them, for the composer's @ list. Built off the UI thread
    /// once per project and again after two minutes; null without a project.</summary>
    public async Task<(IReadOnlyList<string> Files, IReadOnlyList<(string Name, string File, int Line)> Symbols)?> MentionSourceAsync()
    {
        string root = ErrorRoot;
        if (root.Length == 0 || !Directory.Exists(root)) return null;
        var idx = _mentionIndex;
        if (idx != null && PathEquals(idx.Root, root) && DateTime.UtcNow - idx.BuiltUtc < TimeSpan.FromMinutes(2))
            return (idx.Files, idx.Symbols);
        var build = _mentionBuild;
        if (build == null || build.IsCompleted) _mentionBuild = build = Task.Run(() => BuildMentionIndex(root));
        var made = await build;
        if (made != null && PathEquals(made.Root, ErrorRoot)) _mentionIndex = made;
        return made == null ? idx == null ? null : (idx.Files, idx.Symbols) : (made.Files, made.Symbols);
    }

    private MentionIndex? BuildMentionIndex(string root)
    {
        try
        {
            var listing = WorkspaceFiles.List(root, root, 20_000);
            var symbols = new List<(string, string, int)>();
            {
                int scanned = 0;
                foreach (var rel in listing.Files)
                {
                    if (scanned >= 1500 || symbols.Count >= 20_000) break;
                    if (!SymbolExt.Contains(Path.GetExtension(rel))) continue;
                    string full = Path.Combine(root, rel);
                    try
                    {
                        var fi = new FileInfo(full);
                        if (fi.Length > 256 * 1024) continue;
                        scanned++;
                        foreach (var (symbol, line) in Mentions.SymbolsIn(File.ReadAllText(full)))
                            if (symbol.Length >= 3) symbols.Add((symbol, rel, line));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return new MentionIndex(root, DateTime.UtcNow, listing.Files, symbols);
        }
        catch (Exception ex)
        {
            _debugLog?.Info("Chat", "mention index failed: " + ex.Message);
            return null;
        }
    }

    // ═══ 5. Click the side panel's picture to ask about a spot ═══

    /// <summary>A click at (<paramref name="fx"/>, <paramref name="fy"/>) — 0–1 across and down — on the side panel's
    /// picture: a copy with that spot circled is attached and the box gets the start of the question, for the owner to
    /// finish (the same way the UI Studio's element request names one element). False + the status line when the
    /// picture could not be read.</summary>
    public bool AskAboutPictureSpot(string path, double fx, double fy)
    {
        if (!IsOpenableImage(path)) return false;
        fx = Math.Clamp(fx, 0, 1);
        fy = Math.Clamp(fy, 0, 1);
        byte[] marked;
        try { marked = MarkSpot(path, fx, fy); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = LocF("vibe.ask.failed", ex.Message);
            return false;
        }
        if (!AttachImageBytes(marked, "spot.png")) return false;
        string lead = LocF("vibe.ask.prefill", Math.Round(fx * 100), Math.Round(fy * 100));
        string now = (UserInput ?? "").TrimEnd();
        UserInput = now.Length == 0 ? lead : now + "\n" + lead;
        return true;
    }

    /// <summary>The picture as PNG with a red ring (and a soft fill) round the spot. Pure apart from reading the file.</summary>
    public static byte[] MarkSpot(string path, double fx, double fy)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, w, h));
            double r = Math.Max(14, Math.Min(w, h) * 0.045);
            var center = new Point(fx * w, fy * h);
            var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x30, 0x30));
            red.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x30, 0x30));
            fill.Freeze();
            var halo = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), Math.Max(5, r * 0.28));
            halo.Freeze();
            var ring = new Pen(red, Math.Max(3, r * 0.16));
            ring.Freeze();
            dc.DrawEllipse(null, halo, center, r, r);
            dc.DrawEllipse(fill, ring, center, r, r);
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ═══ 6. How full the AI's memory is, by the cost ═══

    /// <summary>The compaction toast asks from here (EvaluateCompactionTrigger: ctx60).</summary>
    public const double CompactSuggestPercent = 60;

    private string _contextGaugeText = "", _contextGaugeTip = "";
    /// <summary>"ความจำ 42%" in the composer's strip ("" before anything was said).</summary>
    public string ContextGaugeText { get => _contextGaugeText; private set => SetProperty(ref _contextGaugeText, value); }
    public string ContextGaugeTip { get => _contextGaugeTip; private set => SetProperty(ref _contextGaugeTip, value); }

    /// <summary>The fill at each end of a turn, per chat, newest last (in memory only): how fast a turn fills it.</summary>
    private readonly Dictionary<string, List<double>> _fillAtTurns = new();

    private void RefreshContextGauge(bool turnEnded)
    {
        double pct = ContextUsagePercent;
        string? chat = CurrentSession?.Id;
        if (turnEnded && chat != null && pct > 0)
        {
            if (!_fillAtTurns.TryGetValue(chat, out var list)) _fillAtTurns[chat] = list = new();
            if (list.Count > 0 && pct + 5 < list[^1]) list.Clear();   // compacted: the old pace says nothing now
            list.Add(pct);
            if (list.Count > 8) list.RemoveAt(0);
        }
        if (Messages.Count == 0 || pct <= 0)
        {
            ContextGaugeText = ContextGaugeTip = "";
            return;
        }
        ContextGaugeText = LocF("vibe.ctx.chip", Math.Round(pct));
        int? turns = TurnsUntil(pct, CompactSuggestPercent, chat != null && _fillAtTurns.TryGetValue(chat, out var seen) ? seen : null);
        ContextGaugeTip = pct >= CompactSuggestPercent ? LocF("vibe.ctx.tipPast", ContextUsageText, CompactSuggestPercent)
            : turns is { } n ? LocF("vibe.ctx.tip", ContextUsageText, CompactSuggestPercent, n)
            : LocF("vibe.ctx.tipFew", ContextUsageText, CompactSuggestPercent);
    }

    /// <summary>About how many more turns like the last ones fill the memory to <paramref name="threshold"/>%: the
    /// average rise over the last three turns. Null when there is no pace yet (fewer than two turns, or none rose).
    /// Pure: the harness checks it.</summary>
    public static int? TurnsUntil(double pct, double threshold, IReadOnlyList<double>? fillAtTurns)
    {
        if (pct >= threshold) return 0;
        if (fillAtTurns is not { Count: >= 2 }) return null;
        var rises = new List<double>();
        for (int i = Math.Max(1, fillAtTurns.Count - 3); i < fillAtTurns.Count; i++)
        {
            double d = fillAtTurns[i] - fillAtTurns[i - 1];
            if (d > 0) rises.Add(d);
        }
        if (rises.Count == 0) return null;
        double pace = rises.Average();
        return Math.Max(1, (int)Math.Ceiling((threshold - pct) / pace));
    }
}

/// <summary>The change card: the turn's files, their +/− and one plain line; before/after pictures for a screen.</summary>
public sealed class TurnChangesView : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The before/after pictures, read off the UI thread the first time the card asks for them. A new
    /// Source value — not a placeholder filled in later — so the Image measures itself again: WPF does not re-measure
    /// when only the inside of an ImageSource changes, and the picture stayed a flat line on a cold start.</summary>
    public System.Windows.Media.ImageSource? BeforePicture => Picture(BeforeImage, ref _before, nameof(BeforePicture));
    public System.Windows.Media.ImageSource? AfterPicture => Picture(AfterImage, ref _after, nameof(AfterPicture));
    private System.Windows.Media.ImageSource? _before, _after;
    private bool _beforeAsked, _afterAsked;

    private System.Windows.Media.ImageSource? Picture(string? path, ref System.Windows.Media.ImageSource? slot, string name)
    {
        if (slot != null || path == null) return slot;
        ref bool asked = ref name == nameof(BeforePicture) ? ref _beforeAsked : ref _afterAsked;
        if (asked) return null;
        asked = true;
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(() => DecodePicture(path)).ContinueWith(t =>
        {
            if (t.Result is not { } bmp) return;
            ui.BeginInvoke(() =>
            {
                if (name == nameof(BeforePicture)) _before = bmp; else _after = bmp;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            });
        }, TaskScheduler.Default);
        return null;
    }

    /// <summary>A small frozen copy of the picture (at most 280 px high: the card shows it at 140), the file not held open.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? DecodePicture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int height = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).PixelHeight;
            stream.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelHeight = Math.Clamp(height, 1, 280);
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }   // gone or unreadable: the card shows its empty frame
    }

    public string Line { get; }
    public IReadOnlyList<TurnChangeRow> Files { get; }
    public string MoreText { get; }
    public bool HasMore => MoreText.Length > 0;
    public string AddedText { get; }
    public string RemovedText { get; }
    public string? BeforeImage { get; }
    public string? AfterImage { get; }
    public bool HasPictures => AfterImage != null && BeforeImage != null;

    public TurnChangesView(IReadOnlyList<FileChange> files, string? before, string? after, Action<string>? open)
    {
        Line = TurnChanges.PlainLine(files, k => LocalizedResources.Instance[k]);
        Files = files.Take(TurnChanges.MaxListed).Select(f => new TurnChangeRow(f, open)).ToList();
        MoreText = files.Count > TurnChanges.MaxListed
            ? string.Format(LocalizedResources.Instance["changes.moreFiles"], files.Count - TurnChanges.MaxListed) : "";
        AddedText = "+" + files.Sum(f => f.Added);
        RemovedText = "−" + files.Sum(f => f.Removed);
        BeforeImage = ChatViewModel.IsOpenableImage(before) ? before : null;
        AfterImage = ChatViewModel.IsOpenableImage(after) ? after : null;
    }
}

public sealed class TurnChangeRow
{
    public string Name { get; }
    public string Folder { get; }
    public string AddedText { get; }
    public string RemovedText { get; }
    public bool IsNew { get; }
    /// <summary>Segoe MDL2: a screen, gears, a document…</summary>
    public string Glyph { get; }
    public ICommand OpenCommand { get; }

    public TurnChangeRow(FileChange f, Action<string>? open)
    {
        Name = Path.GetFileName(f.Display);
        string dir = Path.GetDirectoryName(f.Display)?.Replace('\\', '/') ?? "";
        Folder = dir.Length > 0 ? dir + "/" : "";
        AddedText = f.Added > 0 ? "+" + f.Added : "";
        RemovedText = f.Removed > 0 ? "−" + f.Removed : "";
        IsNew = f.Created;
        Glyph = f.Area switch
        {
            ChangeArea.Screen => "",
            ChangeArea.Data => "",
            ChangeArea.Test => "",
            ChangeArea.Docs => "",
            _ => "",
        };
        string path = f.Path;
        OpenCommand = new RelayCommand(() => open?.Invoke(path), () => File.Exists(path));
    }
}
