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

    /// <summary>Whether the tool details are expanded in UI.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    /// <summary>Elapsed time for tool execution display (e.g. "1.2s").</summary>
    public string? ToolElapsedTime { get; set; }

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

    // ─── Permission Request Fields ───
    /// <summary>Callback invoked when user clicks Allow/Deny on a permission request.</summary>
    public Action<bool>? PermissionCallback { get; set; }
    /// <summary>Whether this permission request has been answered.</summary>
    public bool PermissionAnswered { get; set; }

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
}
