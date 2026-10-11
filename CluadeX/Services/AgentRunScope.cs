using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// The state that belongs to ONE agent run rather than to the app: the chat it works for, the project folder it
/// started in, the skill's tool limits, the shell's cd, the plan checklist. It flows with the run's async calls
/// (AsyncLocal), so several chats can run agents at the same time — and the user can open another chat or another
/// project while one runs — without one run's tools working in another's folder or writing into another's chat.
/// Code outside any run (the UI, a typed command) sees no scope and uses the app-wide values, as before.
/// </summary>
public sealed class AgentRunScope
{
    private static readonly AsyncLocal<AgentRunScope?> Slot = new();

    /// <summary>The run this code is part of, or null outside any run.</summary>
    public static AgentRunScope? Current => Slot.Value;

    /// <summary>Make <paramref name="scope"/> the current run for this async flow (and everything it starts).
    /// Called in the method that starts the run: the value flows into awaited calls, Task.Run and events raised
    /// from there, and is restored for the caller when an async method returns.</summary>
    public static void Enter(AgentRunScope? scope) => Slot.Value = scope;

    public AgentRunScope(string sessionKey, string? workingDirectory, object? owner = null)
    {
        SessionKey = sessionKey;
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        Owner = owner;
    }

    /// <summary>The chat this run works for, as a safe file-name token (tool screenshots, "allow once").</summary>
    public string SessionKey { get; }

    /// <summary>The project folder the run started in — its tools stay there even if the UI opens another.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Whoever started the run (the chat page's run record), for routing its events back.</summary>
    public object? Owner { get; }

    public List<string>? ActiveSkillAllowedTools { get; set; }

    /// <summary>Where the run's shell is after a `cd` (null = the project root).</summary>
    public string? ShellCwd { get; set; }

    /// <summary>The run's plan checklist (todo_write).</summary>
    public List<TodoItem> Todos { get; } = new();

    private readonly object _costLock = new();
    private decimal _costUsd;

    /// <summary>What this run's API requests cost so far (USD, list price) — sub-agents included, they run in its flow.</summary>
    public decimal CostUsd { get { lock (_costLock) return _costUsd; } }

    /// <summary>Added by <see cref="CostTrackingService.RecordUsage"/> for every request made inside the run.</summary>
    public void AddCost(decimal usd) { lock (_costLock) _costUsd += usd; }

    private int _requests;
    private long _inTok, _outTok, _cacheReadTok, _cacheWriteTok;
    /// <summary>Paid requests (agent steps, sub-agent steps) made for this run.</summary>
    public int Requests { get { lock (_costLock) return _requests; } }
    public long OutputTokens { get { lock (_costLock) return _outTok; } }
    public long CacheReadTokens { get { lock (_costLock) return _cacheReadTok; } }
    public long CacheWriteTokens { get { lock (_costLock) return _cacheWriteTok; } }
    public long InputTokens { get { lock (_costLock) return _inTok; } }

    /// <summary>One usage record of a request made inside the run (a request reports input at its start, output at its end).</summary>
    public void AddUsage(decimal usd, int input, int output, int cacheRead, int cacheWrite, bool newRequest)
    {
        lock (_costLock)
        {
            _costUsd += usd;
            _inTok += input; _outTok += output; _cacheReadTok += cacheRead; _cacheWriteTok += cacheWrite;
            if (newRequest) _requests++;
        }
    }

    /// <summary>Share of the prompt read back from the cache (0–1): the higher, the cheaper each step.</summary>
    public double CacheHitShare
    {
        get
        {
            lock (_costLock)
            {
                long all = _inTok + _cacheReadTok + _cacheWriteTok;
                return all == 0 ? 0 : (double)_cacheReadTok / all;
            }
        }
    }

    /// <summary>What the user allowed beyond the per-run budget by answering "go on" (USD). The run stops to ask once
    /// <see cref="CostUsd"/> reaches the setting plus this — so a budget changed mid-run applies at once.</summary>
    public decimal BudgetGrantedUsd { get; set; }

    // ─── What the user typed while the run was working ───

    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, object? Tag)> _userNotes = new();

    /// <summary>
    /// The user sent this while the run was working. Like Claude Code, it reaches the agent at its next step — with
    /// the next tool results, or instead of ending the turn — rather than being dropped: the input box used to be
    /// disabled for the whole run. <paramref name="tag"/> is the chat bubble that shows it.
    /// </summary>
    public void AddUserNote(string text, object? tag = null)
    {
        if (!string.IsNullOrWhiteSpace(text)) _userNotes.Enqueue((text.Trim(), tag));
    }

    public bool HasUserNotes => !_userNotes.IsEmpty;

    /// <summary>Everything typed since the agent last looked, as one block for the model; null when nothing waits.</summary>
    public string? TakeUserNotes()
    {
        var notes = DrainUserNotes();
        if (notes.Count == 0) return null;
        return "[The user sent this while you were working. Take it into account from here on — it may change or add to the task:]\n"
               + string.Join("\n\n", notes.Select(n => n.Text));
    }

    /// <summary>What the agent never got to (the run ended first): the caller sends it as the next turn.</summary>
    public List<(string Text, object? Tag)> DrainUserNotes()
    {
        var notes = new List<(string Text, object? Tag)>();
        while (_userNotes.TryDequeue(out var n)) notes.Add(n);
        return notes;
    }
}
