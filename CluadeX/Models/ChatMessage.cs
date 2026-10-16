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
        set { if (_content != value) { _content = value; OnPropertyChanged(); } }
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
    public string? ToolVerb { get; set; }
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

    // ─── ask_user question ───
    public List<string> QuestionOptions { get; set; } = new();
    /// <summary>Resolves the agent's waiting ask_user with the answer (null on a row read back from the database).</summary>
    public Action<string>? QuestionCallback { get; set; }
    private bool _questionAnswered;
    /// <summary>Answered, cancelled, or a row from an earlier session (nobody is waiting for it any more).</summary>
    public bool QuestionAnswered => _questionAnswered || QuestionCallback == null;
    public bool HasQuestionOptions => QuestionOptions.Count > 0;
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

public class ChatSession
{
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

    /// <summary>Display-only: the sidebar's group — today, yesterday, this week, earlier (in the UI language).</summary>
    public string DateGroupLabel
    {
        get
        {
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
