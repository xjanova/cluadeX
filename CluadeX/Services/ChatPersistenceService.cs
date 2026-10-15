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

    /// <summary>Save a single chat session.</summary>
    public void SaveSession(ChatSession session)
    {
        try
        {
            _db.SaveSession(session);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save session: {ex.Message}");
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
            _db.DeleteSession(sessionId);
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
        try { return _db.DeleteEmptySessions(sessionId); }
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
        try { return _db.SessionHasMessages(sessionId); }
        catch { return true; }
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
