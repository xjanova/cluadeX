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

    /// <summary>The provider this run started on, kept for its whole life: another chat may pick another one meanwhile.
    /// Null = the provider picked (AiProviderManager).</summary>
    public Providers.IAiProvider? Provider { get; set; }
    public Models.AiProviderType? ProviderType { get; set; }

    /// <summary>Accept edits for THIS run only (a pipe task approved for its folder that runs in the background): the
    /// user's own switch stays on their project. Checked by PermissionService.AcceptsEditsIn; set only for a folder
    /// IsUnsafeAcceptEditsRoot allows.</summary>
    public string? AcceptEditsRoot { get; set; }

    /// <summary>Told while the model is still writing a tool call's input: (tool, characters so far, file path once
    /// the input names one). A provider that streams tool input calls <see cref="ReportToolDraft"/>.</summary>
    public Action<string, int, string?>? ToolDrafting { get; set; }

    /// <summary>Report a tool call being written in the current run (no-op outside a run or without a listener).</summary>
    public static void ReportToolDraft(string tool, int chars, string? path)
    {
        try { Current?.ToolDrafting?.Invoke(tool, chars, path); }
        catch { /* the chat's status row must never break the stream */ }
    }

    /// <summary>Told when the model's service says how big the request it just took was: the whole prompt in tokens
    /// (fresh input + cache read + cache write — system prompt, tool definitions and files read included).</summary>
    public Action<int>? PromptSized { get; set; }

    /// <summary>Report the real prompt size of a request in the current run (no-op outside a run or for an empty
    /// report). The status bar's context count used to be only a word-count guess over the chat's rows (owner
    /// 2026-11-04: "มันถูกต้องแม่นยำไหม ทำไมฉันรู้สึกมันขึ้นช้า").</summary>
    public static void ReportPromptSize(int promptTokens)
    {
        if (promptTokens <= 0) return;
        try { Current?.PromptSized?.Invoke(promptTokens); }
        catch { /* the context bar must never break the stream */ }
    }

    /// <summary>The file path a tool input names, read from the JSON streamed so far (null until that value is whole).</summary>
    public static string? DraftPath(string partialJson)
    {
        var m = DraftPathRx.Match(partialJson.Length > 2000 ? partialJson[..2000] : partialJson);
        if (!m.Success) return null;
        try { return System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value); }
        catch { return m.Groups[1].Value; }
    }

    private static readonly System.Text.RegularExpressions.Regex DraftPathRx =
        new(@"""(?:path|file_path|filePath|notebook_path)""\s*:\s*""((?:[^""\\]|\\.)*)""");

    /// <summary>The run was started by another program over the cluadex-mcp pipe (BrainX's cowork room, a script) —
    /// not typed by the owner. The BrainX model never relays such a run back into the room: that is a loop.</summary>
    public bool FromPipe { get; set; }

    /// <summary>The chat's first request, word for word from the transcript (null = none). A model taking the chat
    /// over gets this as "the task": after a compaction the model's view holds only a summary that cuts it short.</summary>
    public string? TaskRequest { get; set; }

    /// <summary>The owner's words of the turn this run is doing (before brain recall or a skill list was added), what a
    /// skill that runs in it learns its words from (SkillService.LearnTerms).</summary>
    public string? TurnText { get; set; }

    /// <summary>The skills this turn offered the model (Skills/SkillPicker), or null when smart picking was off for it —
    /// then the system prompt lists every switched-on skill, as before.</summary>
    public Skills.SkillPick? SkillPick { get; set; }

    /// <summary>This turn already runs a skill (/command): no pick.</summary>
    public bool NoSkillPick { get; set; }

    /// <summary>Told once the turn's skills were picked, before the model starts (the chat shows what was picked).</summary>
    public Action<Skills.SkillPick>? SkillsPicked { get; set; }

    private readonly List<string> _extraFolders = new();
    /// <summary>Related project folders opened beside <see cref="WorkingDirectory"/> (open_project add=true, or the
    /// chat naming another project). Tools reach them by absolute path; relative paths stay in the main folder.</summary>
    public IReadOnlyList<string> ExtraFolders { get { lock (_extraFolders) return _extraFolders.ToList(); } }

    /// <summary>Add a related folder (ignored when it is the main folder or already open). True = newly added.</summary>
    public bool AddExtraFolder(string path)
    {
        lock (_extraFolders)
        {
            if (string.Equals(path, WorkingDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            if (_extraFolders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return false;
            _extraFolders.Add(path);
            return true;
        }
    }

    /// <summary>The folder (main or related) that contains <paramref name="fullPath"/>, or null.</summary>
    public static string? ContainingFolder(string fullPath, IEnumerable<string?> folders)
    {
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string root = System.IO.Path.GetFullPath(f).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string cmp = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (cmp.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    public List<string>? ActiveSkillAllowedTools { get; set; }

    /// <summary>Where the run's shell is after a `cd` (null = the project root).</summary>
    public string? ShellCwd { get; set; }

    /// <summary>The run's plan checklist (todo_write).</summary>
    public List<TodoItem> Todos { get; } = new();

    /// <summary>The plan's scope tag as the model last gave it (first_task / continuation / new_task), or null.</summary>
    public string? PlanScope { get; set; }
    /// <summary>The model wrote or moved the plan in this run (wrap_up checks off only a plan this run worked on).</summary>
    public bool PlanTouched { get; set; }
    /// <summary>The plan came over from the chat's last turn with work left (PlanRecord.CarryNote tells the model).</summary>
    public bool PlanCarried { get; set; }

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

    /// <summary>Effort "auto": the agent loop's pick for its next request (low / medium / high / xhigh), read by the
    /// providers through <see cref="Models.EffortLevels.Effective"/>. Null = none picked (the provider's default).</summary>
    public string? StepEffort { get; set; }

    /// <summary>The most effort this flow's requests may go out with, whatever the setting or the loop's pick — a
    /// sub-agent's tier ceiling (cheap → low, mid → medium; <see cref="Agents.DispatchPolicy.EffortCapFor"/>). Null = no
    /// ceiling. Read through <see cref="Models.EffortLevels.Effective"/>; the sub-agent's own loop never resets it.</summary>
    public string? EffortCap { get; set; }

    /// <summary>Signs the model is stuck on this run's task (kept on the run's root scope; DispatchPolicy.AssessHardTask):
    /// its own fixes in a row that left the build red (0 once it went green), speculative fixers that all failed on it,
    /// and a stop by the loop guard. They decide whether CluadeX offers to send the task to Claude Fable 5.2.</summary>
    public int FailedOwnFixes { get; set; }
    public bool SpeculativeFixFailed { get; set; }
    public bool LoopGuardStopped { get; set; }

    /// <summary>A sub-agent's tier ("cheap" / "mid" / "deep"; null = not a tiered sub-agent) and the model the run itself is
    /// on — the Billing page prices this flow's tokens on that model too, and shows the difference as the routing saving
    /// (<see cref="CostTrackingService.RecordUsage"/>).</summary>
    public string? SubAgentTier { get; set; }
    public string? TierRunModel { get; set; }

    private decimal _routingSaved, _routingBaseline;
    /// <summary>Of this flow's requests: the routing saving booked so far, and what they would have cost on the run's
    /// model — taken back when the attempt fell short and climbed (it saved nothing: it was paid for twice).</summary>
    public decimal RoutingSavedUsd { get { lock (_costLock) return _routingSaved; } }
    public decimal RoutingBaselineUsd { get { lock (_costLock) return _routingBaseline; } }
    public void AddRouting(decimal saved, decimal onRunModel) { lock (_costLock) { _routingSaved += saved; _routingBaseline += onRunModel; } }

    private decimal _localSaved;
    /// <summary>The part of <see cref="RoutingSavedUsd"/> a model on this PC earned (it answered instead of the API).</summary>
    public decimal LocalSavedUsd { get { lock (_costLock) return _localSaved; } }
    public void AddLocalSaving(decimal saved) { lock (_costLock) _localSaved += saved; }

    /// <summary>This turn's id and the agent loop's current step — what a restore point is named by.</summary>
    public string TurnId { get; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public int Step { get; set; }

    /// <summary>The loop's result so far (an AgentLoopResult) — read when the run is stopped mid-task.</summary>
    public object? PartialResult { get; set; }

    /// <summary>Pause asked for: the loop finishes the step in hand (its tools complete, their results are kept) and
    /// ends the turn cleanly before the next request — the chat is saved and "continue" picks it up, even after a
    /// restart (to update the app mid-task).</summary>
    public volatile bool PauseRequested;

    /// <summary>The first line of a reply that ended on Pause — the chat shows Resume while it is the last reply.</summary>
    public const string PausedMarker = "⏸";

    private string? _liveEffort;
    /// <summary>The effort this run's requests really go out with now (top-level, or the latest per-message change) —
    /// what the chat shows next to the effort picker. Raises <see cref="LiveEffortChanged"/>.</summary>
    public string? LiveEffort
    {
        get => _liveEffort;
        set { if (_liveEffort == value) return; _liveEffort = value; LiveEffortChanged?.Invoke(this); }
    }
    public static event Action<AgentRunScope>? LiveEffortChanged;

    /// <summary>Why effort "auto" is at its current level, in a few words of the UI language ("bug fix", "build is red"):
    /// shown beside the level. Null = not "auto", or nothing to say.</summary>
    public string? EffortWhy { get; set; }

    private LiveModelReport.Snapshot? _liveReport;
    /// <summary>What this run's last request really used: the served model and the effort on the wire
    /// (<see cref="LiveModelReport"/>). Raises <see cref="LiveEffortChanged"/>.</summary>
    public LiveModelReport.Snapshot? LiveReport
    {
        get => _liveReport;
        set { if (Equals(_liveReport, value)) return; _liveReport = value; LiveEffortChanged?.Invoke(this); }
    }

    private decimal _baselineUsd;
    /// <summary>What this run would have cost with no saving: no prompt cache, no tool search, no microcompact, no batch.</summary>
    public decimal BaselineUsd { get { lock (_costLock) return _baselineUsd; } }

    /// <summary>One usage record of a request made inside the run (a request reports input at its start, output at its end).</summary>
    public void AddUsage(decimal usd, int input, int output, int cacheRead, int cacheWrite, bool newRequest, decimal baselineUsd = 0m)
    {
        lock (_costLock)
        {
            _costUsd += usd;
            _baselineUsd += baselineUsd;
            _inTok += input; _outTok += output; _cacheReadTok += cacheRead; _cacheWriteTok += cacheWrite;
            if (newRequest) _requests++;
        }
        // A sub-agent's own scope: the run that spawned it pays too (its budget guard and its total see the spend).
        Parent?.AddUsage(usd, input, output, cacheRead, cacheWrite, newRequest, baselineUsd);
    }

    /// <summary>The run a sub-agent's scope belongs to (null for a run itself).</summary>
    public AgentRunScope? Parent { get; private set; }

    /// <summary>
    /// A scope for one sub-agent of this run: same chat, owner and related folders, its own cost (which also adds up in
    /// this run), its own folder when it works in a git worktree — then Accept edits covers that worktree only.
    /// </summary>
    public AgentRunScope CreateChild(string? workingDirectory = null, bool acceptEditsInFolder = false)
    {
        var child = new AgentRunScope(SessionKey, workingDirectory ?? WorkingDirectory, Owner)
        {
            Parent = this,
            Provider = Provider,
            ProviderType = ProviderType,
            AcceptEditsRoot = acceptEditsInFolder ? workingDirectory : AcceptEditsRoot,
            FromPipe = FromPipe,
            TaskRequest = TaskRequest,
            ActiveSkillAllowedTools = ActiveSkillAllowedTools,
            StepEffort = StepEffort,
            EffortCap = EffortCap,
            SubAgentTier = SubAgentTier,
            TierRunModel = TierRunModel,
            EffortWhy = EffortWhy,
        };
        foreach (var f in ExtraFolders) child.AddExtraFolder(f);
        return child;
    }

    private int _strategy;
    /// <summary>The furthest the run fanned out (Agents.DispatchStrategy as int): what its cost is recorded under.</summary>
    public int Strategy => Volatile.Read(ref _strategy);

    /// <summary>The run used <paramref name="strategy"/>; keeps the highest (a sub-agent's scope reports to its run).</summary>
    public void MarkStrategy(int strategy)
    {
        int seen;
        do { seen = Volatile.Read(ref _strategy); if (strategy <= seen) break; }
        while (Interlocked.CompareExchange(ref _strategy, strategy, seen) != seen);
        Parent?.MarkStrategy(strategy);
    }

    /// <summary>When the run started (its wall time for the strategy ledger).</summary>
    public DateTime StartedAt { get; } = DateTime.Now;

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

    /// <summary>The images the user attached to the message this run answers, decoded for a model
    /// (ChatViewModel → native tool loop, which puts them on the user message).</summary>
    public List<ToolImage>? UserImages { get; set; }

    /// <summary>The folders that hold the files the user attached to this message (and what was unpacked from them).
    /// A CLI agent (Claude Dev, Gemini Dev) gets each with --add-dir, so its own Read can open them.</summary>
    public IReadOnlyList<string> AttachmentFolders { get; set; } = Array.Empty<string>();

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

    /// <summary>Raised on the run's thread with the bubbles (tags) of the notes the agent just took, so the chat can stop
    /// showing them as waiting.</summary>
    public event Action<IReadOnlyList<object?>>? UserNotesTaken;

    /// <summary>Everything typed since the agent last looked, as one block for the model; null when nothing waits.
    /// <paramref name="planTool"/>: the plan tool this agent really has — a CLI reached through CluadeX's MCP server has
    /// update_plan, not the native loop's todo_write (a Codex turn was told to call a tool it did not have, 2026-11-09).</summary>
    public string? TakeUserNotes(string planTool = "todo_write")
    {
        bool urgent;
        lock (_callGate) { urgent = _interrupt; _interrupt = false; }
        var notes = DrainUserNotes();
        if (notes.Count == 0) return null;
        try { UserNotesTaken?.Invoke(notes.Select(n => n.Tag).ToList()); }
        catch { /* the chat's rows must never break the run */ }
        // Owner 2026-11-02: what is typed mid-run is taken in as soon as it can be and goes into the plan where it fits
        // ("มีการเพิ่มเข้าแผนกลางทางพิจารณาได้ว่าจะเพิ่มอย่างไร"), not only after the whole plan. A correction or a "stop"
        // still applies at once, and a request that stands on its own still runs in parallel in its own chat (owner
        // 2026-10-18) — it shows under this chat in the sidebar, and close_chat puts it away once its result is in.
        return (urgent
                   ? "[The user pressed \"Send now\": your reply in progress, or tool calls not started yet, were stopped so you "
                     + "read this first. Decide which it is, "
                   : "[The user sent this while you were working. Decide which it is, ")
               + "say so in one short line, then carry on:\n"
               + "- It corrects the current work, or says stop/cancel: apply it now.\n"
               + "- It adds to the current work: put it into your plan with " + planTool + " where it belongs — next, when it "
               + "changes what you were about to do; otherwise after the items already there — say in one line where you put "
               + "it, and do it in this turn.\n"
               + "- It is the work of one of your split chats that are not put away (same or close to its task): send it "
               + "there with message_chat, and carry on here.\n"
               + "- It is a NEW request that stands on its own (it does not need what you are doing and touches other files "
               + "or another project): start it now in its own chat with new_chat — a complete message, since that chat does "
               + "not see this one — so both run in parallel, and keep going with your plan here. Once its result is in, put "
               + "it away with close_chat.]\n"
               + string.Join("\n\n", notes.Select(n => n.Text));
    }

    // ─── "Send now": stop the model's reply in flight so it reads what was typed at once ───

    private readonly object _callGate = new();
    private CancellationTokenSource? _call;
    private bool _interrupt;

    /// <summary>The run's loop can stop one model reply and carry on with the next step (the native tool loop). When
    /// false, "Send now" stops the run and sends what was typed as the next turn.</summary>
    public bool AcceptsInterrupts { get; set; }

    /// <summary>The loop is waiting on the model: <see cref="InterruptStep"/> cancels <paramref name="call"/>.</summary>
    public void BeginCall(CancellationTokenSource call)
    {
        lock (_callGate)
        {
            _call = call;
            // Pressed in the gap between two steps, with the note still waiting: this reply is the one to stop.
            if (_interrupt && HasUserNotes) call.Cancel();
        }
    }

    public void EndCall(CancellationTokenSource call)
    {
        lock (_callGate) { if (ReferenceEquals(_call, call)) _call = null; }
    }

    /// <summary>Send now: what was typed is read before anything else. The model's reply in flight is stopped (the run
    /// goes on); tool calls not started yet are skipped. True when a reply was stopped.</summary>
    public bool InterruptStep()
    {
        lock (_callGate)
        {
            if (!HasUserNotes) return false;
            _interrupt = true;
            if (_call is not { IsCancellationRequested: false } call) return false;
            call.Cancel();
            return true;
        }
    }

    /// <summary>Send now was pressed and its note is not read yet: a tool call that has not started waits for it.</summary>
    public bool InterruptPending { get { lock (_callGate) return _interrupt && HasUserNotes; } }

    /// <summary>The loop caught its reply's cancel: was it Send now (true, and the flag stays for TakeUserNotes), or a
    /// timeout / the user's Stop?</summary>
    public bool WasInterrupted(CancellationTokenSource call)
    {
        lock (_callGate) return _interrupt && call.IsCancellationRequested;
    }

    /// <summary>What the agent never got to (the run ended first): the caller sends it as the next turn.</summary>
    public List<(string Text, object? Tag)> DrainUserNotes()
    {
        var notes = new List<(string Text, object? Tag)>();
        while (_userNotes.TryDequeue(out var n)) notes.Add(n);
        return notes;
    }
}
