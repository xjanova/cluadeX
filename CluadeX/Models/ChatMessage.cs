using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CluadeX.Models;

public enum MessageRole
{
    System,
    User,
    Assistant,
    CodeExecution,
    ToolAction,
    AgentStatus,
    Thinking,
    PermissionRequest,
    /// <summary>Compaction marker: the model's context starts here. Its Content is the summary of everything
    /// above it, which replaces those messages in what is SENT to the model — the transcript itself (screen and
    /// database) keeps every message. Stored as role 8; appended last so older rows keep their numbers.</summary>
    ContextSummary,
    /// <summary>The agent asks the user (ask_user): the question, its options and a free answer box. Stored as role 9.
    /// Never sent back to the model as a turn — the answer reaches it as the tool's result.</summary>
    Question,
}

/// <summary>How an inline permission prompt was answered.</summary>
public enum PermissionOutcome
{
    Pending,
    Allowed,
    Denied,
    /// <summary>The turn was stopped while the prompt was open.</summary>
    Cancelled,
    /// <summary>The chat was cleared or switched while the prompt was open.</summary>
    Dismissed,
}

public class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isStreaming;
    private bool _hasError;
    private bool _isExpanded;
    private bool _isInterrupted;
    private bool _isThinkingExpanded;
    private string _thinkingContent = string.Empty;
    private string? _retryCountdown;
    private bool _isHovered;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; set; }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
            if (Role == MessageRole.Assistant) NoteRoomSpeaker();
        }
    }

    // ─── who is talking (ChatCharacter) ───

    private Services.ChatCharacter? _character;
    /// <summary>The girl this box belongs to — the one who asks (question / permission prompt): her portrait, her colour.
    /// Set when the box is made, from the provider answering (or the room member speaking); a row read back from the
    /// database has none and stands as its <see cref="QuestionGirl"/>, else CluadeX's own girl.</summary>
    public Services.ChatCharacter Character
    {
        get => _character ?? Services.ChatCharacter.ForGirl(QuestionGirl);
        set { _character = value; OnPropertyChanged(); OnPropertyChanged(nameof(QuestionGirlGlow)); }
    }

    private string? _roomSpeaker;
    /// <summary>A BrainX cowork-room reply: the member speaking last in it ("### 💬 codex" — BrainXRoomProvider), or null.</summary>
    public string? RoomSpeaker => _roomSpeaker;

    /// <summary>Any reply's room speaker changed (the composer's frame follows whoever answers now).</summary>
    public static event Action<ChatMessage>? RoomSpeakerChanged;

    private void NoteRoomSpeaker()
    {
        string? s = Services.ChatCharacter.LastRoomSpeakerIn(_content);
        if (string.Equals(s, _roomSpeaker, StringComparison.Ordinal)) return;
        _roomSpeaker = s;
        OnPropertyChanged(nameof(RoomSpeaker));
        RoomSpeakerChanged?.Invoke(this);
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsStreaming
    {
        get => _isStreaming;
        set { if (_isStreaming != value) { _isStreaming = value; OnPropertyChanged(); } }
    }

    public List<CodeBlock> CodeBlocks { get; set; } = new();
    public string? ExecutionResult { get; set; }

    public bool HasError
    {
        get => _hasError;
        set { if (_hasError != value) { _hasError = value; OnPropertyChanged(); } }
    }

    /// <summary>Whether this message was interrupted by the user.</summary>
    public bool IsInterrupted
    {
        get => _isInterrupted;
        set { if (_isInterrupted != value) { _isInterrupted = value; OnPropertyChanged(); } }
    }

    // ─── Thinking Block ───
    /// <summary>Extended thinking content (collapsible).</summary>
    public string ThinkingContent
    {
        get => _thinkingContent;
        set { if (_thinkingContent != value) { _thinkingContent = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThinking)); } }
    }

    public bool HasThinking => !string.IsNullOrEmpty(_thinkingContent);

    /// <summary>Whether the thinking block is expanded.</summary>
    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set { if (_isThinkingExpanded != value) { _isThinkingExpanded = value; OnPropertyChanged(); } }
    }

    // ─── Retry Countdown ───
    /// <summary>Retry countdown text (e.g. "Retrying in 3s...").</summary>
    public string? RetryCountdown
    {
        get => _retryCountdown;
        set { if (_retryCountdown != value) { _retryCountdown = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsRetrying)); } }
    }

    public bool IsRetrying => !string.IsNullOrEmpty(_retryCountdown);

    private bool _canRetry;
    /// <summary>A failed REPLY that the hover Retry can re-run (it knows which question it answered). Not set
    /// on tool rows — retrying one re-ran the whole agent turn — nor on notices like "not configured".</summary>
    public bool CanRetry
    {
        get => _canRetry;
        set { if (_canRetry != value) { _canRetry = value; OnPropertyChanged(); } }
    }

    // ─── Hover State (for action buttons) ───
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered != value) { _isHovered = value; OnPropertyChanged(); } }
    }

    // ─── Token Usage Tracking ───
    public int TokenCount { get; set; }
    public long GenerationTimeMs { get; set; }
    public double TokensPerSecond => GenerationTimeMs > 0 ? TokenCount / (GenerationTimeMs / 1000.0) : 0;
    public int ToolCallCount { get; set; }

    // ─── Tool Action Fields ───
    public string? ToolName { get; set; }
    public string? ToolSummary { get; set; }
    public string? ToolOutput { get; set; }
    public bool ToolSuccess { get; set; }
    public string? ToolArguments { get; set; }
    public string? ToolInputSummary { get; set; }
    public int ToolOutputLines { get; set; }

    /// <summary>Whether the tool output contains diff content.</summary>
    public bool IsDiffContent { get; set; }

    // ─── Tool image (screenshot) thumbnail ───
    /// <summary>The JPEG a tool saved (ToolResult.Images[i].FilePath), shown as a thumbnail on the tool row; click
    /// opens the full image. A PATH only — image bytes never live on a message, and it is not written to codex.db
    /// (the screenshots folder is pruned, so a reloaded chat shows the row's text without the picture).</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>Images the user attached to this message (pasted with Ctrl+V, dropped, or picked from "+") — copies
    /// under the data folder's attachments\. Shown in the bubble; sent to a vision model with the message; a model that
    /// cannot see gets their paths in words. Stored with the chat (DatabaseService), so a reloaded chat shows them.</summary>
    public List<string> AttachedImages { get; set; } = new();
    public bool HasAttachedImages => AttachedImages.Count > 0;

    /// <summary>Other files the user attached (PDF, zip, Office, text, code…) — copies under attachments\, each in its own
    /// folder with its own name. Shown as chips in the bubble; their paths and content go to the model with the message
    /// (AttachmentDigest). Stored with the chat like the images.</summary>
    public List<string> AttachedFiles { get; set; } = new();
    public bool HasAttachedFiles => AttachedFiles.Count > 0;

    /// <summary>How many images the tool returned (the row shows the first).</summary>
    public int ImageCount { get; set; }

    // ─── Inline Diff (file edit/write) ───
    /// <summary>Colorized diff lines for a file-mutating tool action. Null/empty = no diff to show.</summary>
    public List<EditDiffLine>? DiffLines { get; set; }
    /// <summary>Short stat for the diff header (e.g. "+12  -3").</summary>
    public string? DiffStat { get; set; }
    /// <summary>True when there is a diff to render inline.</summary>
    public bool HasDiff => DiffLines is { Count: > 0 };

    /// <summary>Whether the tool details are expanded in UI.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private bool _diffExpanded;
    /// <summary>The edit's side-by-side diff is open. Closed by default: a long chat built every diff line of every edit
    /// (a 500-line write = thousands of elements) each time the row scrolled into view — the chat stuttered and froze.</summary>
    public bool DiffExpanded
    {
        get => _diffExpanded;
        set { if (_diffExpanded != value) { _diffExpanded = value; OnPropertyChanged(); } }
    }

    /// <summary>Elapsed time for tool execution display (e.g. "1.2s").</summary>
    public string? ToolElapsedTime { get; set; }

    // ─── Tool row look: icon, verb, target ───
    /// <summary>The tool this row is for (read_file, edit_file, mcp__…): picks its icon.</summary>
    public string? ToolKind { get; set; }
    /// <summary>What the tool did in the user's language ("อ่านไฟล์") and on what ("scripts/main.gd"). Rows saved
    /// before these existed have neither and show <see cref="ToolName"/> instead.</summary>
    private string? _toolVerb;
    public string? ToolVerb { get => _toolVerb; set { _toolVerb = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasToolVerb)); } }
    private string? _toolTarget;
    public string? ToolTarget { get => _toolTarget; set { _toolTarget = value; OnPropertyChanged(); } }
    public bool HasToolVerb => !string.IsNullOrEmpty(ToolVerb);
    public string ToolGlyph => ToolLook.Of(ToolKind ?? (CollapsedToolNames.Count > 0 ? CollapsedToolNames[0] : null)).Glyph;

    /// <summary>The diff's +/- counts for the row header ("+27  −12").</summary>
    public int DiffAdded { get; set; }
    public int DiffRemoved { get; set; }
    public string DiffAddedText => DiffAdded > 0 ? $"+{DiffAdded}" : "";
    public string DiffRemovedText => DiffRemoved > 0 ? $"−{DiffRemoved}" : "";

    private List<DiffPairRow>? _diffPairs;
    /// <summary>The diff as before/after columns (built once, on first display).</summary>
    public List<DiffPairRow>? DiffPairs => DiffLines is { Count: > 0 } lines ? _diffPairs ??= Models.DiffPairs.SideBySide(lines) : null;

    // ─── The agent map: the sub-agents one command started, as a tree card (open by default, folds away) ───
    public System.Collections.ObjectModel.ObservableCollection<AgentNode>? AgentNodes { get; set; }
    public bool IsAgentMap => AgentNodes != null;
    private string _agentMapHeader = "";
    public string AgentMapHeader { get => _agentMapHeader; set { _agentMapHeader = value; OnPropertyChanged(); } }
    private bool _agentMapOpen = true;
    public bool IsAgentMapOpen { get => _agentMapOpen; set { _agentMapOpen = value; OnPropertyChanged(); } }

    // ─── The agent's plan (todo_write), shown as a checklist card ───
    private List<PlanItemView>? _planItems;
    private string _planHeader = "";
    public List<PlanItemView>? PlanItems
    {
        get => _planItems;
        set { _planItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPlan)); }
    }
    public bool IsPlan => _planItems is { Count: > 0 };
    /// <summary>"กำลังดำเนินการ (4/5)" · and how long the run has been going.</summary>
    public string PlanHeader { get => _planHeader; set { _planHeader = value; OnPropertyChanged(); } }
    private string _planElapsed = "";
    public string PlanElapsed { get => _planElapsed; set { _planElapsed = value; OnPropertyChanged(); } }

    // ─── Tool Call Grouping ───
    /// <summary>Whether this ToolAction is collapsed into a group summary.</summary>
    private bool _isCollapsedGroup;
    public bool IsCollapsedGroup
    {
        get => _isCollapsedGroup;
        set { if (_isCollapsedGroup != value) { _isCollapsedGroup = value; OnPropertyChanged(); } }
    }
    /// <summary>Number of tool calls collapsed in this group.</summary>
    public int CollapsedCount { get; set; }
    /// <summary>Individual tool names in this collapsed group.</summary>
    public List<string> CollapsedToolNames { get; set; } = new();
    private bool _foldedIntoBalloon;
    /// <summary>A short line the agent wrote just before its next background call ("I'll open Grok and download scene
    /// D"): it became that call's caption inside the running balloon, so the row itself is hidden. Kept in the list
    /// (it is still the model's own words in the history). Not stored.</summary>
    public bool FoldedIntoBalloon { get => _foldedIntoBalloon; set { if (_foldedIntoBalloon != value) { _foldedIntoBalloon = value; OnPropertyChanged(); } } }

    private bool _isLive;
    /// <summary>The running balloon: the agent's background calls are folding into this row right now (its live line
    /// says what runs). Cleared when something else comes after it or the run ends. Not stored.</summary>
    public bool IsLive { get => _isLive; set { if (_isLive != value) { _isLive = value; OnPropertyChanged(); } } }
    private string _liveText = "";
    public string LiveText { get => _liveText; set { if (_liveText != value) { _liveText = value; OnPropertyChanged(); } } }

    /// <summary>A help line dropped in beside the tool rows (ChromeHelp): tool rows on either side of it still fold together.</summary>
    public bool IsSideNote { get; set; }
    /// <summary>The group's calls, one small box each, shown when the group row is opened (not stored with the chat:
    /// a reloaded group shows its merged output instead).</summary>
    public System.Collections.ObjectModel.ObservableCollection<ToolStepView> GroupSteps { get; } = new();
    public bool HasGroupSteps => GroupSteps.Count > 0;
    public void AddGroupStep(ToolStepView step)
    {
        GroupSteps.Add(step);
        if (GroupSteps.Count == 1) OnPropertyChanged(nameof(HasGroupSteps));
    }

    /// <summary>How many edits this row holds (1 for a plain edit row; more once later edits to the same file folded in).</summary>
    public int EditCount { get; set; } = 1;

    /// <summary>The diff lines one row keeps: a long run of edits to one file stops growing the row's diff here
    /// (the +/− counts stay exact).</summary>
    public const int MaxFoldedDiffLines = 1500;

    /// <summary>A later edit to the same file folds into this row: the +/− add up, its diff follows the earlier ones after
    /// a separator, the times add up and its output follows. Raises change for everything the row shows.</summary>
    public void AppendEdit(EditDiffResult diff, string? elapsed, string? output, int outputLines, string? target)
    {
        EditCount++;
        DiffAdded += diff.Added;
        DiffRemoved += diff.Removed;
        var lines = new List<EditDiffLine>(DiffLines ?? new());
        if (lines.Count < MaxFoldedDiffLines)
        {
            lines.Add(new EditDiffLine { Kind = EditDiffKind.Hunk, Text = $"── #{EditCount} ──" });
            lines.AddRange(diff.Lines.Take(MaxFoldedDiffLines - lines.Count));
        }
        DiffLines = lines;
        _diffPairs = null;
        DiffStat = $"+{DiffAdded}  -{DiffRemoved}";
        ToolElapsedTime = SumElapsed(ToolElapsedTime, elapsed);
        ToolOutput = string.IsNullOrEmpty(ToolOutput) ? output : ToolOutput + "\n---\n" + output;
        ToolOutputLines += outputLines;
        ToolTarget = target == null ? ToolTarget : $"{target} ×{EditCount}";
        foreach (var name in new[] { nameof(DiffAdded), nameof(DiffRemoved), nameof(DiffAddedText), nameof(DiffRemovedText),
                     nameof(DiffLines), nameof(DiffPairs), nameof(HasDiff), nameof(DiffStat), nameof(ToolElapsedTime),
                     nameof(ToolOutput), nameof(ToolOutputLines), nameof(EditCount) })
            OnPropertyChanged(name);
    }

    /// <summary>"1.5s" + "1.7s" → "3.2s" (at or over 10 s without the decimal, like a single row). An unreadable one is dropped.</summary>
    public static string? SumElapsed(string? a, string? b)
    {
        static double? Sec(string? s) => s != null && s.EndsWith('s')
            && double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
        double? x = Sec(a), y = Sec(b);
        if (x == null && y == null) return a ?? b;
        double sum = (x ?? 0) + (y ?? 0);
        return sum < 10 ? sum.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : sum.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    // ─── ask_user question ───
    public List<string> QuestionOptions { get; set; } = new();
    /// <summary>Resolves the agent's waiting ask_user with the answer (null on a row read back from the database).</summary>
    public Action<string>? QuestionCallback { get; set; }
    private bool _questionAnswered;
    /// <summary>Answered, cancelled, or a row from an earlier session (nobody is waiting for it any more).</summary>
    public bool QuestionAnswered => _questionAnswered || QuestionCallback == null;
    public bool HasQuestionOptions => QuestionOptions.Count > 0;
    /// <summary>The girl standing beside this question (an Assets\Girls name), picked at random when it was asked.</summary>
    public string? QuestionGirl { get; set; }
    public System.Windows.Media.ImageSource? QuestionGirlImage => Services.GirlArt.Image(QuestionGirl);
    public System.Windows.Media.Color QuestionGirlGlow => Character.Accent;
    private string _answerDraft = "";
    public string AnswerDraft { get => _answerDraft; set { _answerDraft = value; OnPropertyChanged(); } }

    /// <summary>Answer once: the answer is kept under the question (it is saved with it) and the agent continues.</summary>
    public bool AnswerQuestion(string? answer)
    {
        answer = answer?.Trim() ?? "";
        if (QuestionAnswered || answer.Length == 0) return false;
        _questionAnswered = true;
        Content += "\n→ " + answer;
        OnPropertyChanged(nameof(QuestionAnswered));
        try { QuestionCallback?.Invoke(answer); } catch { /* already resolved */ }
        return true;
    }

    /// <summary>Nobody will answer it any more (the run was stopped): say so under the question.</summary>
    public void CloseQuestion(string note)
    {
        if (_questionAnswered) return;
        _questionAnswered = true;
        Content += "\n→ " + note;
        OnPropertyChanged(nameof(QuestionAnswered));
    }

    // ─── Permission Request Fields ───
    /// <summary>Callback invoked when user clicks Allow/Deny on a permission request.</summary>
    public Action<bool>? PermissionCallback { get; set; }

    /// <summary>A prompt that always asks (the user's own Ask rule, code run in their browser): switching the chat to
    /// "allow everything" leaves it for the user to answer.</summary>
    public bool PermissionAlwaysAsk { get; set; }

    /// <summary>The prompt offers "Allow in this chat" (an MCP tool no policy judged, and not one that always asks).</summary>
    public bool PermissionCanAllowForChat { get; set; }

    /// <summary>Non-null = a red, dangerous-action prompt (deletes a drive, a home or a system folder): the guard's
    /// finding, shown in full under the request so the user sees exactly what would be deleted.</summary>
    public string? PermissionDanger { get; set; }

    /// <summary>The prompt is the red dangerous-action kind.</summary>
    public bool IsPermissionDanger => !string.IsNullOrEmpty(PermissionDanger);

    /// <summary>The request in people's words (what, where, the result, the risk, the dangerous parts) with the real
    /// command for the folded technical details. Null = only <see cref="Content"/> (the old one-line prompt).</summary>
    public Services.Safety.PlainPermission? PermissionPlain { get; set; }

    public bool HasPermissionPlain => PermissionPlain != null;
    /// <summary>The prompt's bold first line: the plain headline, or the old one-line prompt.</summary>
    public string PermissionHeadlineText => PermissionPlain?.Headline ?? Content;
    public bool HasPermissionNote => PermissionPlain is { Note.Length: > 0 };
    public bool HasPermissionWarnings => PermissionPlain is { Warnings.Count: > 0 };

    /// <summary>"Low" / "Medium" / "High" / "Danger" — what the risk chip's colour keys on.</summary>
    public string PermissionRiskLevel => PermissionPlain?.Risk.ToString() ?? "";

    /// <summary>It was answered with "Allow in this chat" (the outcome label says so).</summary>
    public bool PermissionAllowedForChat { get; private set; }

    /// <summary>Answer "Allow in this chat": allowed, and the asking tool remembers it for the rest of the chat.
    /// False when the prompt does not offer it or was already answered.</summary>
    public bool AllowPermissionForChat()
    {
        if (!PermissionCanAllowForChat || PermissionAnswered) return false;
        PermissionAllowedForChat = true;
        OnPropertyChanged(nameof(PermissionAllowedForChat));
        return ResolvePermission(PermissionOutcome.Allowed);
    }

    /// <summary>It was answered with "Allow for today" (the outcome label says so).</summary>
    public bool PermissionAllowedForDay { get; private set; }

    /// <summary>Answer "Allow for today": allowed, and the tool runs without asking in every chat until midnight.
    /// Offered exactly where "Allow in this chat" is. False when not offered or already answered.</summary>
    public bool AllowPermissionForDay()
    {
        if (!PermissionCanAllowForChat || PermissionAnswered) return false;
        PermissionAllowedForDay = true;
        OnPropertyChanged(nameof(PermissionAllowedForDay));
        return ResolvePermission(PermissionOutcome.Allowed);
    }

    /// <summary>The prompt offers "Always allow" (a folder outside the project: remembered, never asked again).</summary>
    public bool PermissionCanAllowAlways { get; set; }

    /// <summary>It was answered with "Always allow".</summary>
    public bool PermissionAllowedAlways { get; private set; }

    /// <summary>Answer "Always allow". False when the prompt does not offer it or was already answered.</summary>
    public bool AllowPermissionAlways()
    {
        if (!PermissionCanAllowAlways || PermissionAnswered) return false;
        PermissionAllowedAlways = true;
        OnPropertyChanged(nameof(PermissionAllowedAlways));
        return ResolvePermission(PermissionOutcome.Allowed);
    }

    private PermissionOutcome _permissionOutcome;
    /// <summary>How the prompt was answered. Notifies, so the Allow/Deny buttons collapse and the outcome label
    /// appears the moment it is answered — they used to stay clickable and the answer was appended to the
    /// prompt text as an English "*Allowed*".</summary>
    public PermissionOutcome PermissionOutcome
    {
        get => _permissionOutcome;
        private set
        {
            if (_permissionOutcome == value) return;
            _permissionOutcome = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PermissionAnswered));
        }
    }

    /// <summary>Whether this permission request has been answered (in any way).</summary>
    public bool PermissionAnswered => _permissionOutcome != PermissionOutcome.Pending;

    /// <summary>Answer the prompt once: records the outcome and resolves the waiting agent (only Allowed lets
    /// the tool run). Returns false when it was already answered — a second click, or Stop racing a click.</summary>
    public bool ResolvePermission(PermissionOutcome outcome)
    {
        if (outcome == PermissionOutcome.Pending || PermissionAnswered) return false;
        PermissionOutcome = outcome;
        try { PermissionCallback?.Invoke(outcome == PermissionOutcome.Allowed); }
        catch { /* resolving an already-completed prompt is a no-op */ }
        return true;
    }

    /// <summary>Status lines older builds saved as ordinary Assistant messages ("⏵ Reading…", "⏳ Waiting…",
    /// "✓ 120 tokens · 3.1s · 38.7 tok/s"). They were reloaded and sent back to the model as fake assistant
    /// turns; the database layer reclassifies them as AgentStatus when it reads them.</summary>
    public static bool IsLegacyStatusLine(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        return content.StartsWith("⏵ ", StringComparison.Ordinal)
            || content.StartsWith("⏳", StringComparison.Ordinal)
            || content.StartsWith("⚡ Generating", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(content, @"^✓ [\d,]+ tokens · [\d.]+s · [\d.]+ tok/s$");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class CodeBlock
{
    public string Language { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
}

public class ChatSession : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private string _runStatus = "";
    /// <summary>The sidebar's live badge: "" idle, "working" (a run goes on), "waiting" (it asks the owner something),
    /// "done" (finished while the owner was in another chat — cleared when it is opened). Not stored.</summary>
    public string RunStatus
    {
        get => _runStatus;
        set { if (_runStatus != value) { _runStatus = value; Raise(nameof(RunStatus)); Raise(nameof(HasRunStatus)); } }
    }
    public bool HasRunStatus => _runStatus.Length > 0;

    private string _runStep = "";
    /// <summary>What the run is doing now ("⏵ อ่านไฟล์ main.gd"), under the title while it works.</summary>
    public string RunStep
    {
        get => _runStep;
        set { if (_runStep != value) { _runStep = value; Raise(nameof(RunStep)); } }
    }

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "New Chat";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>Absolute path of the project folder this session was created in (empty = no project).</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Related folders open beside <see cref="ProjectPath"/> for this chat (kept while the app runs).</summary>
    public List<string> ExtraFolders { get; } = new();

    /// <summary>Display-only: last path segment of ProjectPath, or empty if no project.</summary>
    public string ProjectName => string.IsNullOrEmpty(ProjectPath)
        ? ""
        : System.IO.Path.GetFileName(ProjectPath.TrimEnd('\\', '/'));

    // ─── split chats (the agent's new_chat): read live from the link store, so a reloaded chat object keeps them ───

    /// <summary>The chat this one was split off, or null.</summary>
    public string? ParentId => CluadeX.Services.ChatLinkStore.Current?.Get(Id)?.ParentId;

    /// <summary>What this chat was split off to do (the task the asking chat gave it), or "".</summary>
    public string SplitTask => CluadeX.Services.ChatLinkStore.Current?.Get(Id)?.Task ?? "";

    /// <summary>It was split off another chat.</summary>
    public bool IsSplit => ParentId != null;

    /// <summary>A finished split chat that was put away: hidden from the list, kept, can be brought back.</summary>
    public bool IsArchived => CluadeX.Services.ChatLinkStore.Current?.Get(Id)?.ArchivedAt != null;

    private int _splitDepth;
    /// <summary>Its level in the sidebar's tree (0 = top, 1 = split off a chat shown above it, …). Set by the sidebar.</summary>
    public int SplitDepth
    {
        get => _splitDepth;
        set { if (_splitDepth != value) { _splitDepth = value; Raise(nameof(SplitDepth)); Raise(nameof(SplitIndent)); } }
    }

    /// <summary>The row's left indent for <see cref="SplitDepth"/>.</summary>
    public System.Windows.Thickness SplitIndent => new(Math.Min(_splitDepth, 4) * 14, 0, 0, 0);

    private string _splitSummary = "";
    /// <summary>On a chat that split work off: "↳ 3 split chats · 2 done · 1 put away". "" when none.</summary>
    public string SplitSummary
    {
        get => _splitSummary;
        set { if (_splitSummary != value) { _splitSummary = value; Raise(nameof(SplitSummary)); } }
    }

    private bool _isLastSplit;
    /// <summary>The last split chat under its parent: its tree line ends in a rounded corner (╰) instead of going on (├).</summary>
    public bool IsLastSplit
    {
        get => _isLastSplit;
        set { if (_isLastSplit != value) { _isLastSplit = value; Raise(nameof(IsLastSplit)); } }
    }

    private bool _projectAsParent;
    /// <summary>A split chat working in the same project as its parent: its project line would only repeat the parent's.</summary>
    public bool ProjectAsParent
    {
        get => _projectAsParent;
        set { if (_projectAsParent != value) { _projectAsParent = value; Raise(nameof(ProjectAsParent)); Raise(nameof(ShowProjectLine)); } }
    }

    /// <summary>The row shows the project: it has one, and (for a split chat) it is not the parent's — that line then says
    /// WHERE the work went.</summary>
    public bool ShowProjectLine => ProjectName.Length > 0 && !_projectAsParent;

    private int _openSplitCount;
    /// <summary>Split chats of this chat that are not put away — the number on its branch badge (0 = no badge).</summary>
    public int OpenSplitCount
    {
        get => _openSplitCount;
        set { if (_openSplitCount != value) { _openSplitCount = value; Raise(nameof(OpenSplitCount)); Raise(nameof(HasOpenSplits)); } }
    }
    public bool HasOpenSplits => _openSplitCount > 0;

    private bool _hasFinishedSplits;
    /// <summary>It has split chats that are finished and not put away yet (offers "put the finished ones away").</summary>
    public bool HasFinishedSplits
    {
        get => _hasFinishedSplits;
        set { if (_hasFinishedSplits != value) { _hasFinishedSplits = value; Raise(nameof(HasFinishedSplits)); } }
    }

    private string? _groupOverride;
    /// <summary>A split chat sits in its parent's day group, under it, not in its own.</summary>
    public string? GroupOverride
    {
        get => _groupOverride;
        set { if (_groupOverride != value) { _groupOverride = value; Raise(nameof(DateGroupLabel)); } }
    }

    /// <summary>The link store changed for this chat: re-read everything derived from it.</summary>
    public void RaiseSplitChanged()
    {
        Raise(nameof(ParentId)); Raise(nameof(SplitTask)); Raise(nameof(IsSplit)); Raise(nameof(IsArchived));
    }

    /// <summary>Display-only: the sidebar's group — today, yesterday, this week, earlier (in the UI language).</summary>
    public string DateGroupLabel
    {
        get
        {
            if (_groupOverride != null) return _groupOverride;
            var day = UpdatedAt.Date;
            var today = DateTime.Today;
            string key = day >= today ? "sidebar.group.today"
                : day >= today.AddDays(-1) ? "sidebar.group.yesterday"
                : day >= today.AddDays(-6) ? "sidebar.group.week"
                : "sidebar.group.earlier";
            return CluadeX.Services.LocalizedResources.Instance[key];
        }
    }

    /// <summary>Display-only: "21:58" today, "Oct 9" before.</summary>
    public string ListTimeLabel => UpdatedAt.Date >= DateTime.Today
        ? UpdatedAt.ToString("HH:mm")
        : UpdatedAt.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
}
