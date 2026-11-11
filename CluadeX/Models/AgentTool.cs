namespace CluadeX.Models;

/// <summary>
/// Types of tools the agent can use to interact with the file system and environment.
/// </summary>
public enum ToolType
{
    // File operations
    ReadFile,
    WriteFile,
    EditFile,
    MultiEdit,       // Apply several find/replace edits to one file atomically (all-or-nothing)
    ListFiles,
    SearchFiles,
    SearchContent,
    RunCommand,
    RunBuild,        // Auto-detect the project's build/type-check command and run it (the verify loop)
    RunTests,        // Auto-detect + run the project's test suite; returns a distilled pass/fail + failing tests
    CreateDirectory,

    // Git operations
    GitStatus,
    GitAdd,
    GitCommit,
    GitPush,
    GitPull,
    GitBranch,
    GitCheckout,
    GitMerge,
    GitDiff,
    GitLog,
    GitClone,
    GitInit,
    GitStash,

    // GitHub operations
    GhPrCreate,
    GhPrList,
    GhIssueCreate,
    GhIssueList,
    GhRepoView,

    // Web operations
    WebFetch,
    WebSearch,

    // Interactive REPL
    Repl,

    // Task management
    TaskCreate,
    TaskList,
    TaskStop,
    TaskOutput,

    // Git worktree
    GitWorktreeCreate,
    GitWorktreeRemove,

    // Agent sub-task
    AgentSpawn,

    // MCP (external tool servers)
    McpTool,

    // Agent meta-tools
    TodoWrite,
    PlanMode,

    // Phase 3: New tools (Claude Code parity)
    GlobSearch,      // Pattern-based file matching (e.g., **/*.cs)
    Grep,            // Regex content search with context lines
    AskUser,         // Ask user a question via UI dialog
    Config,          // Get/set CluadeX settings from chat
    NotebookEdit,    // Edit Jupyter .ipynb notebook cells
    PowerShell,      // Explicit PowerShell execution

    // Skills system (Phase 4)
    SkillInvoke,     // Invoke a skill by name

    // Memory tools
    MemorySave,      // Save a memory entry
    MemoryList,      // List all memories
    MemoryDelete,    // Delete a memory

    // Hex editor tools (binary file inspection + patching)
    HexOpen,         // Open a binary file in the Hex Editor view + return file info
    HexRead,         // Read bytes from a file at offset (returns hex + ascii)
    HexSearch,       // Find a hex/text pattern in a file (returns offsets)
    HexPatch,        // Patch bytes at offset (in-place, with undo, optionally save)
    HexInfo,         // Get file size, sha256, magic bytes without loading into editor

    // Subagent system (Sprint 1 #1 — ECC parity)
    SubAgentInvoke,  // Spawn a specialised subagent (code-reviewer, security-reviewer, etc.)
    SubAgentList,    // List available subagents

    // BrainX knowledge base (Wave 4 — cross-machine memory)
    BrainRecall,     // Search the connected BrainX for relevant notes / coding-lessons

    // Code intelligence (LSP)
    LspDiagnostics,  // Get errors/warnings for a file from a language server

    // Codebase search (ranked retrieval)
    CodebaseSearch,  // Ranked relevance search across the repo (filename + symbol + content)

    // Code navigation (regex/symbol index — no language server required)
    ListSymbols,     // Outline one file's classes/methods/functions (line-numbered)
    FindSymbol,      // Find where a symbol is DEFINED across the repo (go-to-definition by name)

    // Instinct maintenance
    InstinctEvolve,  // Merge near-duplicate learned instincts (semantic clustering)

    // ── Members below are executed by tool MODULES (Services/Tools/IAgentToolModule), not by
    //    AgentToolService itself. Appended at the end so no existing member changes its value. ──

    // Background processes and dev servers (Services/Background — feature.devPreview)
    BgStart,         // Start a command in the background (dev server, watcher) and report when it is ready
    BgServe,         // Serve a folder over http://localhost with the built-in static file server
    BgOutput,        // Read a background process's output (tail / since last read)
    BgStop,          // Stop a background process with its whole process tree
    BgList,          // List background processes (id, port, url, status)

    // Built-in browser (Services/Browser — feature.devPreview)
    BrowserOpen,       // Open / navigate the built-in browser
    BrowserScreenshot, // Capture the page (image for vision models + a pixel digest in text)
    BrowserConsole,    // Read console messages and page errors
    BrowserNetwork,    // Read network requests
    BrowserRead,       // Read the page as text / element tree
    BrowserFind,       // Find elements and return refs
    BrowserClick,      // Click an element or a point
    BrowserPointer,    // Low-level pointer / touch sequences (press, hold, drag)
    BrowserType,       // Type text into the focused element
    BrowserKey,        // Press keys
    BrowserScroll,     // Scroll the page or an element
    BrowserEval,       // Run JavaScript in the page
    BrowserResize,     // Emulate a viewport (mobile / tablet / desktop)
    BrowserClose,      // Close the browser session

    // Project (Services/Tools/ProjectToolModule)
    OpenProject,       // Open a project folder for the run (works with no project open)

    // App lifecycle (Services/Tools/AppRestartToolModule)
    RestartApp,        // Close CluadeX and start it again (optionally from a new build)

    // Desktop control (Services/Desktop — UI Automation + Win32, Windows only)
    DesktopWindows,    // List top-level windows
    DesktopScreenshot, // Capture a window or a screen region (PNG + image for vision models)
    DesktopInspect,    // A window's UI Automation tree with element refs
    DesktopFind,       // Elements matching a query, as refs
    DesktopAct,        // invoke / select / toggle / expand / set_value / scroll / focus / click on a ref
    DesktopType,       // Type text into the focused element (or a ref)
    DesktopKeys,       // Press key chords
    DesktopLaunch,     // Start a program and wait for its main window
    DesktopWait,       // Wait until a window / element appears or disappears

    // Chats (Services/Tools/ProjectToolModule) — appended last: a ToolType is stored by number
    NewChat,           // Open a new chat (optionally in another project) and start a task there
    CloseChat,         // List this chat's split chats, or put the finished ones away

    // Attached files (Services/Tools/AttachmentToolModule) — appended last: a ToolType is stored by number
    ReadDocument,      // Text of a PDF / Word / Excel / PowerPoint / text file, a slice at a time
    ExtractArchive,    // Unpack a zip / tar / tar.gz / gz safely
    CopyFile,          // Copy a file or folder (an attachment) into the project

    // Desktop control, second set (Services/Desktop) — appended last: a ToolType is stored by number
    DesktopMouse,        // Real mouse at a screen point: move, click, double / right / middle click, drag, scroll
    DesktopWindowAction, // focus / maximize / minimize / restore / move / resize / close a window
    DesktopRead,         // An element's whole text (Text / Value pattern), a page at a time
    DesktopClipboard,    // Read or set the clipboard's text

    // Engineering (Services/Tools/EngineeringToolModule) — appended last: a ToolType is stored by number
    VerifyChange,        // Build from the latest source, check the tested binary is not stale, run tests, compare to a baseline
    FindReferences,      // Every whole-word use of a symbol, definitions marked
    RenameSymbol,        // Preview (default) or apply a whole-word rename across the project
    ImpactOf,            // What depends on a symbol or a file: referencing files, tests, projects
    CheckpointList,      // The project's restore points (what the agent changed, per step)
    CheckpointDiff,      // What restoring a point would change, as a diff
    CheckpointRestore,   // Put the files back to a restore point (asks the user every time)
    GhPrView,            // A pull request's title, state, body (gh, read-only)
    GhPrChecks,          // A pull request's CI checks (gh, read-only)
    GhRunLog,            // A workflow run's failed-step log, or the recent runs (gh, read-only)
    GitBlame,            // Who last changed each line of a file
    GitShow,             // A commit (stat + patch), or a file as it was at a revision
    Debug,               // Drive the debugger: adapters / start / stack / eval / step / continue / stop
    // Auto-comment (Services/Tools/AutoCommentToolModule) — appended last: a ToolType is stored by number
    AddCodeComments,     // The model names lines + comment text; AutoCommentService inserts and proves the code unchanged
    // Orchestration (Services/Tools/OrchestrationToolModule) — appended last: a ToolType is stored by number
    Workflow,            // A declarative multi-agent workflow (opt-in: the chat's Ultra switch or the owner's words)
    WrapUp,              // The next-step box at the end of a turn: recap, one question, one or two answer buttons
    // Watches (Services/Watch, tools in EngineeringToolModule.Watch) — appended last: a ToolType is stored by number
    WatchCi,             // Watch a PR's / commit's / branch's CI; the chat is told (and woken) when it ends
    WatchProcess,        // Watch a background process until it exits; the chat is told (and woken)
    WatchList,           // This chat's watches
    WatchCancel,         // Stop one of this chat's watches (or all)
    // Media transcripts (Services/Tools/MediaTranscriptToolModule) — appended last: a ToolType is stored by number
    MediaTranscript,     // What a video / audio says: its subtitles (yt-dlp, free) or speech-to-text (Whisper, paid, asks)
    // Chats talking to each other (Services/Tools/ProjectToolModule) — appended last: a ToolType is stored by number
    MessageChat,         // Send a message to the chat that opened this one, or to a chat this one split off
}

/// <summary>
/// Represents a parsed tool call from the model's output.
/// </summary>
public class ToolCall
{
    public ToolType Type { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new();
    public string RawText { get; set; } = string.Empty;

    /// <summary>For MCP tools: the server that provides this tool.</summary>
    public string? McpServerName { get; set; }
    /// <summary>For MCP tools: the original tool name on the server.</summary>
    public string? McpToolName { get; set; }

    public string GetArg(string key, string defaultValue = "")
        => Arguments.TryGetValue(key, out var val) ? val : defaultValue;

    /// <summary>Whether this tool is safe to execute concurrently with other tools.
    /// Read-only tools and queries are safe; write/execute tools are not.</summary>
    public bool IsConcurrencySafe => Type switch
    {
        ToolType.ReadFile or ToolType.ListFiles or ToolType.SearchFiles
        or ToolType.SearchContent or ToolType.GlobSearch or ToolType.Grep
        or ToolType.GitStatus or ToolType.GitLog
        or ToolType.GitDiff or ToolType.GitBranch
        or ToolType.GhPrList or ToolType.GhIssueList or ToolType.GhRepoView
        or ToolType.WebSearch or ToolType.WebFetch
        or ToolType.AgentSpawn   // each sub-agent has its own conversation; several run at once
        or ToolType.TaskList or ToolType.TaskOutput
        or ToolType.AskUser or ToolType.MemoryList or ToolType.BrainRecall
        or ToolType.LspDiagnostics or ToolType.CodebaseSearch
        or ToolType.ListSymbols or ToolType.FindSymbol
        or ToolType.BgOutput or ToolType.BgList
        or ToolType.BrowserScreenshot or ToolType.BrowserConsole or ToolType.BrowserNetwork
        or ToolType.BrowserRead or ToolType.BrowserFind
        or ToolType.DesktopWindows or ToolType.DesktopInspect or ToolType.DesktopFind or ToolType.DesktopRead
        or ToolType.FindReferences or ToolType.ImpactOf or ToolType.CheckpointList or ToolType.CheckpointDiff
        or ToolType.GhPrView or ToolType.GhPrChecks or ToolType.GhRunLog or ToolType.GitBlame or ToolType.GitShow
        // a watch only reads CI / a process list on a timer: nothing in the project or on the PC changes
        or ToolType.WatchCi or ToolType.WatchProcess or ToolType.WatchList or ToolType.WatchCancel => true,
        _ => false,
    };

    /// <summary>Whether this tool only reads data (no side effects).</summary>
    public bool IsReadOnly => Type switch
    {
        ToolType.ReadFile or ToolType.ListFiles or ToolType.SearchFiles
        or ToolType.SearchContent or ToolType.GlobSearch or ToolType.Grep
        or ToolType.GitStatus or ToolType.GitLog
        or ToolType.GitDiff or ToolType.GitBranch
        or ToolType.GhPrList or ToolType.GhIssueList or ToolType.GhRepoView
        or ToolType.TaskList or ToolType.TaskOutput
        or ToolType.AskUser or ToolType.MemoryList or ToolType.BrainRecall
        or ToolType.LspDiagnostics or ToolType.CodebaseSearch
        or ToolType.ListSymbols or ToolType.FindSymbol
        or ToolType.BgOutput or ToolType.BgList
        or ToolType.BrowserScreenshot or ToolType.BrowserConsole or ToolType.BrowserNetwork
        or ToolType.BrowserRead or ToolType.BrowserFind
        or ToolType.DesktopWindows or ToolType.DesktopInspect or ToolType.DesktopFind or ToolType.DesktopRead
        or ToolType.FindReferences or ToolType.ImpactOf or ToolType.CheckpointList or ToolType.CheckpointDiff
        or ToolType.GhPrView or ToolType.GhPrChecks or ToolType.GhRunLog or ToolType.GitBlame or ToolType.GitShow
        // a watch only reads CI / a process list on a timer: nothing in the project or on the PC changes
        or ToolType.WatchCi or ToolType.WatchProcess or ToolType.WatchList or ToolType.WatchCancel => true,
        _ => false,
    };
}

/// <summary>
/// Represents the result of executing a tool.
/// </summary>
public class ToolResult
{
    public ToolType Type { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    /// <summary>Short description for display in the chat UI.</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Input arguments passed to the tool (for UI display).</summary>
    public Dictionary<string, string>? Arguments { get; set; }

    /// <summary>Colorized before/after diff for file-mutating tools (edit/write). Null when N/A.</summary>
    public EditDiffResult? Diff { get; set; }

    /// <summary>For file-mutating tools: the (relative) path that changed — drives the live editor follow.</summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Images the tool produced, for models that can see them (null = none). The base64 lives ONLY here:
    /// <see cref="Output"/> carries a short "[image N: WxH attached, saved at …]" marker, so the text budgets
    /// can never cut an image in half and nothing that persists Output (chat history, codex.db) stores image
    /// bytes. Never serialized.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ToolImage>? Images { get; set; }
}

/// <summary>Payload for a human-in-the-loop permission prompt (carries an optional preview diff).</summary>
public class PermissionRequestInfo
{
    public string ToolName { get; set; } = string.Empty;
    /// <summary>Human-readable description of what is being requested (e.g. "write: src/foo.cs").</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>The call's real arguments (command, path, url…) — what the prompt's plain-language explanation is read
    /// from (Safety.PermissionExplainer). Null = only <see cref="Detail"/> is known (the guard hook's pipe).</summary>
    public Dictionary<string, string>? Args { get; set; }
    /// <summary>Preview of the file change to be applied, shown before the user approves. Null = no preview.</summary>
    public EditDiffResult? Diff { get; set; }
    /// <summary>Asked every time on purpose — the user's own "Ask" rule, or code run in their browser. A chat set to
    /// "allow everything" still asks this one.</summary>
    public bool AlwaysAsk { get; set; }
    /// <summary>Non-null = the red prompt (Safety.DangerApproval): this would delete a drive, a home or a system folder,
    /// and here is why. Always asks; never answered by a rule, the chat's "allow everything" or "Allow in this chat".</summary>
    public string? Danger { get; set; }
    /// <summary>Non-null = the strict-approval card (Safety.StrictApproval): only the owner's one-time code (or "yes")
    /// typed into the card, or their terminal, answers it. Never a rule, "allow everything" or "Allow in this chat".</summary>
    public Services.Safety.StrictTicket? Strict { get; set; }
    /// <summary>Non-null = BrainX's own needs_confirmation, shown as a card: the owner approves in BrainX's terminal.</summary>
    public Services.Safety.BrainxConfirmation? Brainx { get; set; }
    /// <summary>Set when the prompt may be answered "Allow in this chat": the tool's identity (server + tool). One
    /// ChatGPT session through a third-party MCP server asked 18 times for the same javascript_tool (Reverse X run,
    /// 2026-10-13), and the only way out was the chat's "allow everything", which lets every shell command through
    /// too. Null = not offered: a prompt that always asks, a call a policy judged, or not an MCP tool.</summary>
    public string? ChatScopeKey { get; set; }
    /// <summary>The user answered "Allow in this chat": the same tool runs without asking again in this chat.</summary>
    public bool AllowedForChat { get; set; }
    /// <summary>The user answered "Allow for today": the same tool runs without asking in every chat until local
    /// midnight (Safety.DayApprovals). Offered wherever "Allow in this chat" is.</summary>
    public bool AllowedForDay { get; set; }
    /// <summary>Set when the prompt may be answered "Always allow" (remembered in settings, never asked again) — a
    /// folder outside the project the agent wants to open. Null = not offered.</summary>
    public string? AlwaysKey { get; set; }
    /// <summary>The user answered "Always allow".</summary>
    public bool AllowedAlways { get; set; }
    /// <summary>Trips when the asking call itself is given up — a CLI agent cancelled it or stopped waiting
    /// (ChatToolBridge): the prompt is retired then, as on Stop, so a late "Allow" answers nobody. None = only the
    /// run's Stop retires it.</summary>
    public CancellationToken GivenUp { get; set; }
}

/// <summary>
/// Represents one step in the agent's agentic execution loop.
/// </summary>
public class AgentStep
{
    public int StepNumber { get; set; }
    public string? ThinkingText { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public List<ToolResult> ToolResults { get; set; } = new();
    public string? ResponseText { get; set; }
}

/// <summary>A TODO item managed by the agent's todo_write tool.</summary>
public class TodoItem
{
    public string Content { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending, in_progress, completed
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>One sub-agent to run: agent_spawn (Type explore | general) or subagent_invoke (Type = the specialist's
/// name, with its Persona and AllowedTools). <paramref name="ContinueId"/>: a sub-agent of this run to carry on with.
/// <paramref name="Tier"/>: cheap / mid / deep — what model and effort it runs on (agent_spawn's tier, a specialist's
/// file; null = by its kind). <paramref name="Model"/>: a specialist file's own model. <paramref name="Color"/>: its
/// accent on the agent map.</summary>
public sealed record SubAgentRequest(string Task, string Context, string Type, string? Persona = null,
    IReadOnlyCollection<string>? AllowedTools = null, string? Pieces = null, int? ContinueId = null,
    string? Tier = null, string? Model = null, string? Color = null);
