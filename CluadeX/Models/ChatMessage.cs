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
}

public class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isStreaming;
    private bool _hasError;
    private bool _isExpanded;

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

    // ─── Tool Action Fields ───
    /// <summary>Tool name for ToolAction messages (e.g. "read_file", "write_file")</summary>
    public string? ToolName { get; set; }
    /// <summary>Short summary for display in collapsed view</summary>
    public string? ToolSummary { get; set; }
    /// <summary>Full tool output for expanded view</summary>
    public string? ToolOutput { get; set; }
    /// <summary>Whether the tool action succeeded</summary>
    public bool ToolSuccess { get; set; }

    /// <summary>Whether the tool details are expanded in UI</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
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
}
