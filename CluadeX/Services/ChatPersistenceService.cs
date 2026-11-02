using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Chat persistence backed by SQLite via DatabaseService.
/// Provides the same API surface as before so ChatViewModel doesn't break.
/// </summary>
public class ChatPersistenceService
{
    private readonly DatabaseService _db;
    /// <summary>For the chat's saved screenshots (DataRoot\screens\&lt;chat&gt;); null = not wired (old callers).</summary>
    private readonly SettingsService? _settings;

    public ChatPersistenceService(DatabaseService db, SettingsService? settings = null)
    {
        _db = db;
        _settings = settings;
    }

    // ── Writing ──
    // A save rewrote the whole chat on the UI thread every 10 seconds (autosave) and for every background run: a long
    // chat froze the window for a moment each time (owner 2026-11-02: "อย่าให้แอพกระตุก"). Now the UI thread only takes
    // a snapshot of the rows (DatabaseService.Snapshot, no SQL) and one background writer stores it. A chat saved again
    // before its last snapshot was written keeps only the newest; a write never lands over a newer one (Seq); a delete
    // drops the chat's pending snapshot and waits for one in flight.
    private readonly object _writeGate = new();      // one writer at a time: the background drain and the direct paths
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, DatabaseService.SessionSnapshot> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private bool _draining;
    private string? _inFlight;                       // the chat the background writer is storing right now

    /// <summary>Save a single chat session now, on the calling thread (a chat switch, the app closing, a copy).</summary>
    public void SaveSession(ChatSession session)
    {
        try
        {
            var snap = DatabaseService.Snapshot(session);
            lock (_pendingLock)
            {
                _pending.Remove(session.Id);
                _deleted.Remove(session.Id);
            }
            Write(snap, background: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save session: {ex.Message}");
        }
    }

    /// <summary>Save a chat without blocking the caller: the snapshot is taken here, the write happens on a background
    /// thread (the autosave tick, a chat running off screen).</summary>
    public void SaveSessionInBackground(ChatSession session)
    {
        DatabaseService.SessionSnapshot snap;
        try { snap = DatabaseService.Snapshot(session); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to snapshot session: {ex.Message}");
            return;
        }
        lock (_pendingLock)
        {
            _pending[session.Id] = snap;
            if (_draining) return;
            _draining = true;
        }
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            DatabaseService.SessionSnapshot next;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) { _draining = false; return; }
                var first = _pending.First();
                _pending.Remove(first.Key);
                next = first.Value;
                _inFlight = next.Id;
            }
            try { Write(next, background: true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Background save failed: {ex.Message}"); }
            finally { lock (_pendingLock) _inFlight = null; }
        }
    }

    private void Write(DatabaseService.SessionSnapshot snap, bool background)
    {
        lock (_writeGate)
        {
            lock (_pendingLock)
            {
                if (background && _deleted.Contains(snap.Id)) return;
                if (_written.TryGetValue(snap.Id, out long seq) && seq > snap.Seq) return;
            }
            _db.WriteSession(snap);
            lock (_pendingLock) _written[snap.Id] = snap.Seq;
        }
    }

    /// <summary>Wait (up to <paramref name="timeout"/>) until every background save has been written — before the
    /// app exits, and before anything reads a chat back from the database.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_pendingLock) if (!_draining && _pending.Count == 0) break;
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(10);
        }
        lock (_writeGate) { }   // a direct save in flight
        return true;
    }

    /// <summary>A background save of this chat is queued or being written.</summary>
    private bool IsUnwritten(string sessionId)
    {
        lock (_pendingLock) return _pending.ContainsKey(sessionId) || _inFlight == sessionId;
    }

    /// <summary>Drop a chat's pending background save and wait out one in flight (before deleting it).</summary>
    private void Forget(string sessionId)
    {
        lock (_pendingLock)
        {
            _pending.Remove(sessionId);
            _deleted.Add(sessionId);
        }
    }

    /// <summary>Load all saved sessions (with messages, ordered by last updated).</summary>
    public List<ChatSession> LoadSessionList()
    {
        try
        {
            return _db.LoadSessionList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load sessions: {ex.Message}");
            return new List<ChatSession>();
        }
    }

    /// <summary>Load a specific session by ID (full messages).</summary>
    public ChatSession? LoadSession(string sessionId)
    {
        try
        {
            // A chat saved a moment ago may still be on its way to the disk: read it back as it was saved.
            if (IsUnwritten(sessionId)) Flush(TimeSpan.FromSeconds(5));
            return _db.LoadSession(sessionId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Delete a session — and the screenshots its agent turns saved (browsing data: page captures of
    /// possibly logged-in sites must not outlive the chat the user deleted).</summary>
    public void DeleteSession(string sessionId)
    {
        bool deleted = false;
        try
        {
            Forget(sessionId);
            lock (_writeGate) _db.DeleteSession(sessionId);
            deleted = true;
        }
        catch { }
        // Only once the chat is really gone — a failed delete leaves the chat AND its screenshots.
        if (deleted && _settings != null && !string.IsNullOrWhiteSpace(sessionId))
        {
            try { Vision.ToolImageWire.DeleteSessionImages(Vision.ToolImageWire.StoreFor(_settings), sessionId); }
            catch { /* best-effort: the 7-day prune still removes them */ }
        }
    }

    /// <summary>False when the chat database failed its integrity check (automatic cleanup is then off).</summary>
    public bool IsDatabaseHealthy
    {
        get { try { return _db.IsHealthy; } catch { return false; } }
    }

    /// <summary>Remove sessions with no messages (all, or just <paramref name="sessionId"/>).</summary>
    public int DeleteEmptySessions(string? sessionId = null)
    {
        // A chat whose first messages are still on their way to the disk is not empty.
        if (!Flush(TimeSpan.FromSeconds(5))) return 0;
        try { lock (_writeGate) return _db.DeleteEmptySessions(sessionId); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to prune empty sessions: {ex.Message}");
            return 0;
        }
    }

    /// <summary>True when the session has stored messages. On a DB error answers true — the caller
    /// then leaves the session alone, which is the safe side.</summary>
    public bool SessionHasMessages(string sessionId)
    {
        if (IsUnwritten(sessionId)) return true;
        try { return _db.SessionHasMessages(sessionId); }
        catch { return true; }
    }

    /// <summary>The stored chat's last assistant reply (null = none, or the read failed).</summary>
    public string? LastAssistantReply(string sessionId)
    {
        try { return _db.LastAssistantReply(sessionId); }
        catch { return null; }
    }

    /// <summary>Auto-generate a title from the first user message.</summary>
    public static string GenerateTitle(List<ChatMessage> messages)
    {
        var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User);
        if (firstUser == null) return "New Chat";

        string content = firstUser.Content.Trim();
        if (content.Length <= 50) return content;

        // Truncate at word boundary
        int cutoff = content.LastIndexOf(' ', 47);
        if (cutoff < 20) cutoff = 47;
        return content[..cutoff] + "...";
    }

    /// <summary>Get the ID of the most recent session, or null if none.</summary>
    public string? GetLastSessionId()
    {
        try
        {
            return _db.GetLastSessionId();
        }
        catch { return null; }
    }

    /// <summary>Search across all sessions using full-text search.</summary>
    public List<SearchResult> SearchSessions(string query)
    {
        try
        {
            return _db.SearchSessions(query);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search failed: {ex.Message}");
            return new List<SearchResult>();
        }
    }

    /// <summary>Get database statistics.</summary>
    public (int Sessions, int Messages, long SizeBytes) GetStats()
    {
        try
        {
            return _db.GetStats();
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
