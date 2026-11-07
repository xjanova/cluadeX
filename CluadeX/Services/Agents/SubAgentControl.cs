using System.Collections.Concurrent;

namespace CluadeX.Services.Agents;

/// <summary>
/// The owner's hold on one running sub-agent (owner 2026-11-07: "สั่งยกเลิกหรือหยุดเอเจนต์ หรือพิมพ์แทรกคำสั่งเข้าไปในเอเจนต์นั้น
/// ได้ ในหน้าต่างลอยของเอเจนต์แต่ละตัว"). Made by the engine for a sub-agent CluadeX runs itself (agent_spawn on the native
/// tool loop) and handed to the map on its "start" event; a CLI's own sub-agent (Claude Dev's Task, Codex spawn_agent …)
/// and the dispatcher's workers and fixers have none, so their window says it cannot be steered instead of offering a
/// button that does nothing.
/// <para><see cref="Stop"/> cancels this agent only: its token is linked to the run's, never the other way round. The
/// engine turns that cancel into an ordinary tool result for the main agent. <see cref="Inject"/> queues a message the
/// agent reads at its next step (as the chat's own queue does for the main agent).</para>
/// </summary>
public sealed class SubAgentControl : IDisposable
{
    public const int MaxNoteChars = 4000, MaxQueuedNotes = 20;

    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _parent;
    private readonly ConcurrentQueue<(string Id, string Text)> _notes = new();
    private int _stopRequested, _closed, _noteSeq;

    public SubAgentControl(CancellationToken parent, bool canEdit = false)
    {
        _parent = parent;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        CanEdit = canEdit;
    }

    /// <summary>The token the agent runs on: cancelled by <see cref="Stop"/> or by the run's own stop.</summary>
    public CancellationToken Token => _cts.Token;
    /// <summary>It may edit files: stopping it mid-way can leave an edit half done, so the window asks first.</summary>
    public bool CanEdit { get; }
    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;
    /// <summary>The run itself was stopped (not just this agent).</summary>
    public bool ParentCancelled => _parent.IsCancellationRequested;
    /// <summary>The agent ended (any way): nothing more can reach it.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) == 1;
    public int PendingNotes => _notes.Count;
    /// <summary>The agent's "start" event (the engine's), so its stop and unread messages are reported as that agent.</summary>
    internal object? StartEvent { get; set; }

    /// <summary>Stop this agent. True only for the press that stopped it — a second press, or one after it ended, does
    /// nothing (idempotent).</summary>
    public bool Stop()
    {
        if (IsClosed || Interlocked.Exchange(ref _stopRequested, 1) == 1) return false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Queue a message for its next step. Returns the note's id (its row in the window is matched by it), or
    /// null when nothing was queued: empty text, the agent ended or is stopping, or too many messages wait already.</summary>
    public string? Inject(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || IsClosed || StopRequested || _notes.Count >= MaxQueuedNotes) return null;
        if (text.Length > MaxNoteChars) text = text[..MaxNoteChars];
        string id = "note-" + Interlocked.Increment(ref _noteSeq);
        _notes.Enqueue((id, text));
        return id;
    }

    /// <summary>The engine takes what waits (in the order it was typed).</summary>
    public List<(string Id, string Text)> TakeNotes()
    {
        var list = new List<(string, string)>();
        while (_notes.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    /// <summary>The agent ended: no more stops or messages; returns the messages it never read.</summary>
    public List<(string Id, string Text)> Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        return TakeNotes();
    }

    /// <summary>The text the agent reads at its next step.</summary>
    public static string NoteBlock(IEnumerable<(string Id, string Text)> notes) =>
        "[Message from the user, typed while you work — read it and act on it before going on]\n"
        + string.Join("\n", notes.Select(n => "• " + n.Text));

    public void Dispose()
    {
        Close();
        _cts.Dispose();
    }
}
