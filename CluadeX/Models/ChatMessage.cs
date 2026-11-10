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
            if (Role == MessageRole.Question) OnPropertyChanged(nameof(QuestionShown));
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
        set { if (_isStreaming != value) { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanExplainSimply)); OnPropertyChanged(nameof(CanReadAloud)); } }
    }

    // ─── Read aloud (ReplyVoiceQueue) ───
    private bool _isVoiceCurrent, _isVoicePlaying;
    /// <summary>A finished reply with words in it: the hover strip's ▶ reads it aloud, and so does a click on the bubble.</summary>
    public bool CanReadAloud => Role == MessageRole.Assistant && !IsStreaming && !string.IsNullOrWhiteSpace(Content);
    /// <summary>The voice player's current reply is this bubble's (lit while it plays).</summary>
    public bool IsVoiceCurrent { get => _isVoiceCurrent; set { if (_isVoiceCurrent != value) { _isVoiceCurrent = value; OnPropertyChanged(); } } }
    /// <summary>This bubble is being read aloud right now (its ▶ shows pause).</summary>
    public bool IsVoicePlaying { get => _isVoicePlaying; set { if (_isVoicePlaying != value) { _isVoicePlaying = value; OnPropertyChanged(); } } }

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

    private bool _isQueuedNote;
    /// <summary>Typed while the agent was running and not read by it yet: the bubble offers "Send now". Not saved.</summary>
    public bool IsQueuedNote
    {
        get => _isQueuedNote;
        set { if (_isQueuedNote != value) { _isQueuedNote = value; OnPropertyChanged(); } }
    }

    /// <summary>The line the app writes when a turn ended with no answer of the model's own ("ran 5 tools, no summary"):
    /// it sits where a reply would, but it is not one, so it is never read aloud. Not saved.</summary>
    public bool IsTurnNote { get; set; }

    /// <summary>The system row under a queued bubble that says where it stands (queued, sending now, read).</summary>
    public ChatMessage? QueueRow { get; set; }

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

    /// <summary>A point of the conversation a chat can go back to or fork from (the bubble's hover actions).</summary>
    public bool CanRewind => Role is MessageRole.User or MessageRole.Assistant or MessageRole.ToolAction;

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

    /// <summary>ContextMemoryService's token estimate of this row, with the Content and ToolOutput strings it was
    /// counted from. Every recount re-read every character of every row (owner 2026-11-02: the chat hitched every 5 s);
    /// a finished row never changes, so its count is kept until either string is replaced. One object, swapped whole,
    /// so a reader never pairs a new string with an old count.</summary>
    internal ContextTokenCount? ContextTokens;

    internal sealed record ContextTokenCount(string Content, string? Output, int Tokens);

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
    /// <summary>0..1: how many of its sub-agents came back (done, failed or stopped) — the header's line.</summary>
    public double AgentMapProgress => AgentNodes is { Count: > 0 } n ? (double)n.Count(x => !x.IsRunning) / n.Count : 0;
    /// <summary>Any sub-agent still working: the root breathes and its line flows only then.</summary>
    public bool AgentMapRunning => AgentNodes?.Any(x => x.IsRunning) == true;
    /// <summary>All came back and at least one failed: the header's line ends amber, not green.</summary>
    public bool AgentMapHasFailures => AgentNodes?.Any(x => x.IsFailed) == true;
    private Services.Agents.AgentGraphState? _agentGraph;
    /// <summary>The map's canvas view (zoom, pan, dragged nodes): on the row, not the control, so a row the chat list
    /// recycles comes back as the owner left it. Not saved — a reloaded chat lays its map out afresh.</summary>
    public Services.Agents.AgentGraphState AgentGraph => _agentGraph ??= new();
    /// <summary>A node changed state (ChatViewModel.OnSubAgentEvent): the card's live parts re-read.</summary>
    public void NotifyAgentMap()
    {
        OnPropertyChanged(nameof(AgentMapProgress));
        OnPropertyChanged(nameof(AgentMapRunning));
        OnPropertyChanged(nameof(AgentMapHasFailures));
    }

    // ─── The agent's plan (todo_write), shown as a checklist card ───
    private List<PlanItemView>? _planItems;
    private string _planHeader = "";
    public List<PlanItemView>? PlanItems
    {
        get => _planItems;
        set { _planItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPlan)); OnPropertyChanged(nameof(PlanProgress)); }
    }
    public bool IsPlan => _planItems is { Count: > 0 };
    /// <summary>0..1: how much of the plan is done — the card's progress line (its rows change in place, so this is
    /// re-read with the header, which every plan update sets).</summary>
    public double PlanProgress => _planItems is { Count: > 0 } l ? (double)l.Count(p => p.IsDone) / l.Count : 0;
    /// <summary>"กำลังดำเนินการ (4/5)" · and how long the run has been going.</summary>
    public string PlanHeader { get => _planHeader; set { _planHeader = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlanProgress)); } }
    private string _planElapsed = "";
    public string PlanElapsed { get => _planElapsed; set { _planElapsed = value; OnPropertyChanged(); } }
    private string? _planScope;
    /// <summary>What the request was to the chat when the plan was written: first_task, continuation or new_task
    /// (PlanOps.Scope) — the card's chip. Null = the model did not say.</summary>
    public string? PlanScope { get => _planScope; set { _planScope = value; OnPropertyChanged(); } }
    /// <summary>Its run was stopped with a step in progress (saved with the card: a reloaded one still says "หยุดไว้").</summary>
    public bool PlanStopped { get; set; }

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

    // ─── Orchestration rows (docs/agent-orchestration.md): System rows tagged in ToolName, their data in ToolOutput — the
    //     columns every row already has, so a reloaded chat draws them again with no schema change. The view each one
    //     shows is built on first use by the chat page (it holds the services), from the row's own data. ───

    public const string WorkflowTag = "cx:workflow", SplitsTag = "cx:splits", NextStepTag = "cx:nextstep", NoticeTag = "cx:notice",
        WatchTag = "cx:watch", TurnChangesTag = "cx:changes";

    /// <summary>Builds a row's view (set by the chat page). Null result = the row shows its text.</summary>
    public static Func<ChatMessage, ViewModels.WorkflowCard?>? WorkflowCardFor;
    public static Func<ChatMessage, System.Collections.ObjectModel.ObservableCollection<ChatSession>?>? SplitChipsFor;
    public static Func<ChatMessage, ViewModels.NextStepView?>? NextStepFor;
    public static Func<ChatMessage, ViewModels.WatchCardView?>? WatchCardFor;
    public static Func<ChatMessage, ViewModels.TurnChangesView?>? TurnChangesFor;

    private ViewModels.WorkflowCard? _workflow;
    /// <summary>A workflow card (ToolOutput = the workflow's id).</summary>
    public ViewModels.WorkflowCard? Workflow => _workflow ??= ToolName == WorkflowTag ? WorkflowCardFor?.Invoke(this) : null;
    public bool IsWorkflow => ToolName == WorkflowTag;

    private System.Collections.ObjectModel.ObservableCollection<ChatSession>? _splitChips;
    /// <summary>The split chats this command opened (ToolOutput = their ids, one per line) — the sidebar's own session
    /// objects, so each chip's state is the sidebar's RunStatus.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ChatSession>? SplitChips =>
        _splitChips ??= ToolName == SplitsTag ? SplitChipsFor?.Invoke(this) : null;
    public bool IsSplitChips => ToolName == SplitsTag;

    private ViewModels.NextStepView? _nextStep;
    /// <summary>The next-step box (ToolOutput = the wrap_up's JSON, with "picked" once answered).</summary>
    public ViewModels.NextStepView? NextStep => _nextStep ??= ToolName == NextStepTag ? NextStepFor?.Invoke(this) : null;
    public bool IsNextStep => ToolName == NextStepTag;

    private ViewModels.WatchCardView? _watchCard;
    /// <summary>A watch's card (ToolOutput = the watch's id) — watch_ci / watch_process.</summary>
    public ViewModels.WatchCardView? WatchCard => _watchCard ??= ToolName == WatchTag ? WatchCardFor?.Invoke(this) : null;
    public bool IsWatch => ToolName == WatchTag;
    public bool ShowsWatchCard => IsWatch && WatchCard != null;

    private ViewModels.TurnChangesView? _turnChanges;
    /// <summary>What a turn changed (ToolOutput = the files as JSON, TurnChanges.ToJson): built on first use, so a row
    /// that is not one costs nothing; the card is a ContentControl that only draws when this is not null.</summary>
    public ViewModels.TurnChangesView? TurnChanges => _turnChanges ??= ToolName == TurnChangesTag ? TurnChangesFor?.Invoke(this) : null;
    public bool IsTurnChanges => ToolName == TurnChangesTag;
    public bool ShowsTurnChanges => IsTurnChanges && TurnChanges != null;

    /// <summary>"Explain simply" on the hover strip: a finished reply with words in it.</summary>
    public bool CanExplainSimply => Role == MessageRole.Assistant && !IsStreaming && !string.IsNullOrWhiteSpace(Content);

    /// <summary>A notice the model also sees on its next turn (a background command or a split chat finished).</summary>
    public bool IsNotice => ToolName == NoticeTag;

    /// <summary>The card can be drawn (a workflow whose state file is gone, or an unreadable box, shows its text instead).</summary>
    public bool ShowsWorkflowCard => IsWorkflow && Workflow != null;
    public bool ShowsNextStep => IsNextStep && NextStep != null;

    /// <summary>A row with its own card instead of the chat bubble.</summary>
    public bool IsOrchestrationCard => ShowsWorkflowCard || IsSplitChips || ShowsNextStep || ShowsWatchCard || ShowsTurnChanges;
    /// <summary>The group's calls, one small box each, shown when the group row is opened (not stored with the chat:
    /// a reloaded group shows its merged output instead).</summary>
    public System.Collections.ObjectModel.ObservableCollection<ToolStepView> GroupSteps { get; } = new();
    public bool HasGroupSteps => GroupSteps.Count > 0;
    public void AddGroupStep(ToolStepView step)
    {
        GroupSteps.Add(step);
        if (GroupSteps.Count == 1) OnPropertyChanged(nameof(HasGroupSteps));
    }

    /// <summary>How many edits this row holds (1 for a plain edit row; more once later edits folded in).</summary>
    public int EditCount { get; set; } = 1;

    /// <summary>The diff lines one row keeps: a long run of edits stops growing the row's diff here
    /// (the +/− counts stay exact).</summary>
    public const int MaxFoldedDiffLines = 1500;

    /// <summary>A later edit folds into this row: the +/− add up, its diff follows the earlier ones after a separator
    /// naming its file when the row holds several (<paramref name="file"/>), the times add
    /// up and its output follows. <paramref name="target"/> is what the row now says it touched. Raises change for
    /// everything the row shows.</summary>
    public void AppendEdit(EditDiffResult diff, string? elapsed, string? output, int outputLines, string? target, string? file = null)
    {
        string held = ToolTarget ?? "";   // before this edit: "a", "a ×3" or "a ×3, b"
        bool severalFiles = !string.IsNullOrEmpty(file) && held.Length > 0
            && (held.Contains(", ") || held != file && !held.StartsWith(file + " ×", StringComparison.Ordinal));
        EditCount++;
        DiffAdded += diff.Added;
        DiffRemoved += diff.Removed;
        var lines = new List<EditDiffLine>(DiffLines ?? new());
        if (lines.Count < MaxFoldedDiffLines)
        {
            // The second file is when the first one needs naming, however many edits it already had.
            if (severalFiles && (lines.Count == 0 || !lines[0].Text.StartsWith("── #1 ", StringComparison.Ordinal)))
                lines.Insert(0, new EditDiffLine { Kind = EditDiffKind.Hunk, Text = $"── #1 · {System.Text.RegularExpressions.Regex.Replace(held, @" ×\d+$", "")} ──" });
            lines.Add(new EditDiffLine { Kind = EditDiffKind.Hunk, Text = severalFiles ? $"── #{EditCount} · {file} ──" : $"── #{EditCount} ──" });
            lines.AddRange(diff.Lines.Take(MaxFoldedDiffLines - lines.Count));
        }
        DiffLines = lines;
        _diffPairs = null;
        DiffStat = $"+{DiffAdded}  -{DiffRemoved}";
        ToolElapsedTime = SumElapsed(ToolElapsedTime, elapsed);
        ToolOutput = string.IsNullOrEmpty(ToolOutput) ? output : ToolOutput + "\n---\n" + output;
        ToolOutputLines += outputLines;
        ToolTarget = target ?? ToolTarget;
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
    public bool QuestionAnswered => _questionAnswered || (QuestionCallback == null && StepsCallback == null);
    public bool HasQuestionOptions => QuestionOptions.Count > 0 && !UsesSteps;
    /// <summary>The girl standing beside this question (an Assets\Girls name), picked at random when it was asked.</summary>
    public string? QuestionGirl { get; set; }
    public System.Windows.Media.ImageSource? QuestionGirlImage => Services.GirlArt.Image(QuestionGirl);
    public System.Windows.Media.Color QuestionGirlGlow => Character.Accent;
    private string _answerDraft = "";
    public string AnswerDraft { get => _answerDraft; set { _answerDraft = value; OnPropertyChanged(); } }

    /// <summary>Answer once: the answer is kept under the question (it is saved with it) and the agent continues.
    /// On a card with steps it answers the step on screen (and moves on) — the composer's answer box and Enter work the
    /// same on both.</summary>
    public bool AnswerQuestion(string? answer)
    {
        if (UsesSteps) return StepAnswerTyped();
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
        RaiseSteps();
    }

    // ─── a question card with steps (several questions, or one with several picks) ───

    /// <summary>The steps of a card that asks several questions at once, or one multi-select question. Empty = the
    /// plain one-question card (<see cref="QuestionOptions"/>, <see cref="QuestionCallback"/>).</summary>
    public List<AskStepState> Steps { get; private set; } = new();
    /// <summary>Resolves the agent's waiting question with one answer per step.</summary>
    public Action<IReadOnlyList<string>>? StepsCallback { get; private set; }

    public bool UsesSteps => Steps.Count > 0;
    private int _stepIndex;
    /// <summary>The step on screen: 0..Steps.Count-1, or Steps.Count = the review of every answer before they are sent.</summary>
    public int StepIndex => _stepIndex;
    public bool OnReview => Steps.Count > 1 && _stepIndex >= Steps.Count;
    public AskStepState? CurrentStep => UsesSteps && _stepIndex < Steps.Count ? Steps[_stepIndex] : null;
    /// <summary>The bar over the card: "2 / 3" while it is open and asks more than one question.</summary>
    public bool ShowsStepBar => Steps.Count > 1 && !QuestionAnswered;
    public string StepLabel => OnReview ? $"{Steps.Count} / {Steps.Count}" : $"{_stepIndex + 1} / {Steps.Count}";
    public bool CanStepBack => UsesSteps && !QuestionAnswered && _stepIndex > 0;
    /// <summary>The step's option buttons are shown (not on the review, not once answered).</summary>
    public bool ShowsStepChoices => CurrentStep is { Choices.Count: > 0 } && !QuestionAnswered;
    public IReadOnlyList<AskChoice> StepChoices => CurrentStep?.Choices ?? (IReadOnlyList<AskChoice>)Array.Empty<AskChoice>();
    /// <summary>The answer box: the plain card, or a step (the review has none).</summary>
    public bool ShowsAnswerBox => !QuestionAnswered && !OnReview;
    /// <summary>The primary button's text: Next on a step, Send on the last one / on the review.</summary>
    public bool StepIsLast => !UsesSteps || OnReview || (Steps.Count == 1);

    /// <summary>What the card shows: the step on screen (or the review) while it is open, else the saved text.</summary>
    /// Empty on every other row: the (collapsed) question card is in every row's template, and its Markdown panel would
    /// otherwise render each reply a second time.
    public string QuestionShown =>
        Role != MessageRole.Question ? "" : UsesSteps && !QuestionAnswered ? (CurrentStep?.Text ?? ReviewText) : Content;

    /// <summary>The review step: every question with its answer, Back to change one.</summary>
    public string ReviewText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Steps.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                string a = Steps[i].Answer;
                // "**2.**", not "2.": a Markdown list item would restart at 1 in every paragraph.
                sb.Append("**").Append(i + 1).Append(".** ").Append(Steps[i].Step.Question.Trim()).Append("\n→ ").Append(a.Length > 0 ? a : "—");
            }
            return sb.ToString();
        }
    }

    /// <summary>Make this a card with steps. Its saved text lists the questions (a chat closed while it waits still shows
    /// them); once answered it holds every question with its answer.</summary>
    public void SetSteps(IReadOnlyList<AskStep> steps, Action<IReadOnlyList<string>> answered)
    {
        Steps = steps.Select(s => new AskStepState(s)).ToList();
        StepsCallback = answered;
        _stepIndex = 0;
        AnswerDraft = "";
        Content = string.Join("\n\n", steps.Select(s => "❓ " + s.Question.Trim()));
        RaiseSteps();
    }

    /// <summary>An option button on the step on screen: a single-choice step takes it as the answer and moves on; a
    /// multi-select step ticks or unticks it.</summary>
    public void StepPick(AskChoice choice)
    {
        if (CurrentStep is not { } step || QuestionAnswered || !step.Choices.Contains(choice)) return;
        if (step.Step.Multi) { choice.Picked = !choice.Picked; RaiseSteps(); return; }
        foreach (var c in step.Choices) c.Picked = ReferenceEquals(c, choice);
        step.Draft = "";
        AnswerDraft = "";
        StepNext();
    }

    /// <summary>The answer box / Enter / the primary button: keep what was typed and go on (send on the last step).</summary>
    private bool StepAnswerTyped()
    {
        if (QuestionAnswered) return false;
        if (CurrentStep is { } step)
        {
            step.Draft = AnswerDraft ?? "";
            // A single-choice step answered by typing: the typed answer replaces an option picked before.
            if (!step.Step.Multi && step.Draft.Trim().Length > 0) foreach (var c in step.Choices) c.Picked = false;
            if (step.Answer.Length == 0) return false;
        }
        return StepNext();
    }

    /// <summary>Next step; after the last one the review (more than one question) or the answer itself.</summary>
    public bool StepNext()
    {
        if (!UsesSteps || QuestionAnswered) return false;
        if (CurrentStep is not { } step) return SendSteps();   // on the review
        step.Draft = AnswerDraft ?? "";
        if (step.Answer.Length == 0) return false;
        if (Steps.Count == 1) return SendSteps();
        _stepIndex++;
        AnswerDraft = CurrentStep?.Draft ?? "";
        RaiseSteps();
        return true;
    }

    /// <summary>Back one step, with its answer as it was left.</summary>
    public void StepBack()
    {
        if (!CanStepBack) return;
        if (CurrentStep is { } step) step.Draft = AnswerDraft ?? "";
        _stepIndex--;
        AnswerDraft = CurrentStep?.Draft ?? "";
        RaiseSteps();
    }

    /// <summary>Every step answered: the card keeps the questions with their answers and the agent continues.</summary>
    private bool SendSteps()
    {
        var answers = Steps.Select(s => s.Answer).ToList();
        if (answers.Any(a => a.Length == 0))
        {
            _stepIndex = answers.FindIndex(a => a.Length == 0);   // back to the first one left open
            AnswerDraft = CurrentStep?.Draft ?? "";
            RaiseSteps();
            return false;
        }
        _questionAnswered = true;
        Content = string.Join("\n\n", Steps.Select((s, i) => "❓ " + s.Step.Question.Trim() + "\n→ " + answers[i]));
        OnPropertyChanged(nameof(QuestionAnswered));
        RaiseSteps();
        try { StepsCallback?.Invoke(answers); } catch { /* already resolved */ }
        return true;
    }

    private void RaiseSteps()
    {
        foreach (var name in new[] { nameof(StepIndex), nameof(OnReview), nameof(CurrentStep), nameof(ShowsStepBar), nameof(StepLabel),
                     nameof(CanStepBack), nameof(ShowsStepChoices), nameof(StepChoices), nameof(ShowsAnswerBox), nameof(StepIsLast),
                     nameof(QuestionShown), nameof(ReviewText), nameof(HasQuestionOptions) })
            OnPropertyChanged(name);
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

    /// <summary>The prompt size the model's service last reported for this chat (rows after its anchor are estimated on
    /// top). Null until a service reports one. Not stored.</summary>
    public MeasuredContext? LastMeasuredContext { get; set; }

    /// <summary>A real prompt size: <paramref name="Tokens"/> as the service counted them, from
    /// <paramref name="Provider"/>. <paramref name="Anchor"/>: the chat's last user / reply / tool row at that moment —
    /// a row, not an index, because status rows come and go around it.</summary>
    public sealed record MeasuredContext(int Tokens, ChatMessage? Anchor, AiProviderType Provider);

    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The chat's short reference ("CX-8cf58c2a"), computed from <see cref="Id"/> (Services.ChatRef): shown at
    /// the top of the chat and in the sidebar row's tip, so the owner can name this chat to the team or the AI.</summary>
    public string Ref => Services.ChatRef.Of(Id);

    private string _title = "New Chat";
    /// <summary>Raises its change: opening a chat copies the stored title onto the sidebar's own row (the row is no longer
    /// swapped for a new object, which is what redrew it before).</summary>
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(nameof(Title)); } }
    }

    /// <summary>The owner named this chat (Rename): an automatic title never replaces it, and a save never writes
    /// over the stored name (DatabaseService.WriteSession).</summary>
    public bool TitleIsCustom { get; set; }

    private string _projectId = "";
    /// <summary>The project (a named group of chats, <see cref="ChatProject"/>) this chat is in; "" = none. Only the
    /// group: the chat's folder (<see cref="ProjectPath"/>) never changes with it — rewind points and memory key off that.</summary>
    public string ProjectId
    {
        get => _projectId;
        set { if (_projectId != value) { _projectId = value ?? ""; Raise(nameof(ProjectId)); } }
    }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    private DateTime _updatedAt = DateTime.Now;
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { if (_updatedAt != value) { _updatedAt = value; RaiseTimeLabels(); } }
    }

    /// <summary>The row's time and day group are drawn again (UpdatedAt changed, or midnight passed).</summary>
    public void RaiseTimeLabels() { Raise(nameof(UpdatedAt)); Raise(nameof(ListTimeLabel)); Raise(nameof(DateGroupLabel)); }

    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>Absolute path of the project folder this session was created in (empty = no project).</summary>
    public string ProjectPath
    {
        get => _projectPath;
        set { if (_projectPath != value) { _projectPath = value; Raise(nameof(ProjectPath)); Raise(nameof(ProjectName)); Raise(nameof(ShowProjectLine)); } }
    }
    private string _projectPath = "";

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

    /// <summary>The model this chat runs on when it has one of its own (a split chat opened on Fable); null = the app's
    /// pick in the model button, as for every other chat. Picking a model in the button clears it.</summary>
    public string? OwnModel => CluadeX.Services.ChatLinkStore.Current?.Get(Id)?.Model;

    /// <summary>It was split off another chat.</summary>
    public bool IsSplit => ParentId != null;

    /// <summary>A finished split chat that was put away: hidden from the list, kept, can be brought back.</summary>
    public bool IsArchived => CluadeX.Services.ChatLinkStore.Current?.Get(Id)?.ArchivedAt != null;

    private int _splitDepth;
    /// <summary>Its level in the sidebar's tree (0 = top, 1 = split off a chat shown above it, …). Set by the sidebar.</summary>
    public int SplitDepth
    {
        get => _splitDepth;
        set { if (_splitDepth != value) { _splitDepth = value; Raise(nameof(SplitDepth)); Raise(nameof(SplitIndent)); Raise(nameof(IsTreeChild)); } }
    }

    /// <summary>The row's left indent for <see cref="SplitDepth"/>.</summary>
    public System.Windows.Thickness SplitIndent => new(Math.Min(_splitDepth, 4) * 14, 0, 0, 0);

    /// <summary>It sits under another chat in the tree, so its row draws the line up to it. A split chat at the top (its
    /// parent not in the list, or pinned on its own) has no line to draw.</summary>
    public bool IsTreeChild => _splitDepth > 0;

    private bool _isPinned;
    /// <summary>Pinned to the top of the sidebar by the owner (kept in settings, not in the chat database).</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; Raise(nameof(IsPinned)); Raise(nameof(DateGroupLabel)); } }
    }

    private bool _hasWorkingSplits;
    /// <summary>One of its split chats is working now: the branch badge twinkles (they work together).</summary>
    public bool HasWorkingSplits
    {
        get => _hasWorkingSplits;
        set { if (_hasWorkingSplits != value) { _hasWorkingSplits = value; Raise(nameof(HasWorkingSplits)); } }
    }

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
        Raise(nameof(ParentId)); Raise(nameof(SplitTask)); Raise(nameof(IsSplit)); Raise(nameof(IsArchived)); Raise(nameof(OwnModel));
    }

    /// <summary>Display-only: the sidebar's group — pinned, today, yesterday, this week, earlier (in the UI language).</summary>
    public string DateGroupLabel
    {
        get
        {
            if (_groupOverride != null) return _groupOverride;
            if (_isPinned) return CluadeX.Services.LocalizedResources.Instance["sidebar.group.pinned"];
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
