using System.IO;
using Microsoft.Data.Sqlite;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// SQLite-backed persistence for chat sessions with full-text search.
/// Replaces the old JSON file-based ChatPersistenceService.
/// DB file lives at {DataRoot}/codex.db — portable-friendly.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private bool _disposed;

    /// <summary>Current schema version stored in PRAGMA user_version.</summary>
    private const int SchemaVersion = 2;

    public DatabaseService(SettingsService settingsService)
    {
        _dbPath = Path.Combine(settingsService.DataRoot, "codex.db");
        _connectionString = $"Data Source={_dbPath}";

        InitializeDatabase();
        MigrateFromJson(settingsService);
    }

    // ──────────────────────────── Schema ────────────────────────────

    private void InitializeDatabase()
    {
        using var conn = Open();

        // WAL mode for better concurrent read performance
        Execute(conn, "PRAGMA journal_mode=WAL;");
        Execute(conn, "PRAGMA foreign_keys=ON;");

        int currentVersion = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            var result = cmd.ExecuteScalar();
            if (result != null) currentVersion = Convert.ToInt32(result);
        }

        if (currentVersion < 1)
        {
            Execute(conn, @"
                CREATE TABLE IF NOT EXISTS sessions (
                    id           TEXT PRIMARY KEY,
                    title        TEXT NOT NULL DEFAULT 'New Chat',
                    created_at   TEXT NOT NULL,
                    updated_at   TEXT NOT NULL,
                    project_path TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS messages (
                    id            TEXT PRIMARY KEY,
                    session_id    TEXT NOT NULL,
                    role          INTEGER NOT NULL,
                    content       TEXT NOT NULL DEFAULT '',
                    timestamp     TEXT NOT NULL,
                    tool_name     TEXT,
                    tool_summary  TEXT,
                    tool_output   TEXT,
                    tool_success  INTEGER NOT NULL DEFAULT 0,
                    has_error     INTEGER NOT NULL DEFAULT 0,
                    sort_order    INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_messages_session
                    ON messages(session_id, sort_order);

                -- FTS5 virtual table for full-text search across messages
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                    content,
                    session_id UNINDEXED,
                    message_id UNINDEXED,
                    content=messages,
                    content_rowid=rowid
                );

                -- Triggers to keep FTS in sync
                CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                    INSERT INTO messages_fts(rowid, content, session_id, message_id)
                    VALUES (new.rowid, new.content, new.session_id, new.id);
                END;

                CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                    INSERT INTO messages_fts(messages_fts, rowid, content, session_id, message_id)
                    VALUES ('delete', old.rowid, old.content, old.session_id, old.id);
                END;

                CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
                    INSERT INTO messages_fts(messages_fts, rowid, content, session_id, message_id)
                    VALUES ('delete', old.rowid, old.content, old.session_id, old.id);
                    INSERT INTO messages_fts(rowid, content, session_id, message_id)
                    VALUES (new.rowid, new.content, new.session_id, new.id);
                END;
            ");

            Execute(conn, $"PRAGMA user_version = {SchemaVersion};");
        }

        // ─── Schema v2: project_path column on sessions ───
        // Added so the sidebar can filter session list by the currently-open folder.
        // SQLite ALTER TABLE ADD COLUMN is fine on live DBs — just ignore "duplicate column" if the user
        // upgraded out of order.
        if (currentVersion < 2)
        {
            try
            {
                Execute(conn, "ALTER TABLE sessions ADD COLUMN project_path TEXT NOT NULL DEFAULT '';");
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Already added — proceed.
            }
            Execute(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_project_path ON sessions(project_path);");
            Execute(conn, $"PRAGMA user_version = {SchemaVersion};");
        }
    }

    // ──────────────────────────── Sessions CRUD ────────────────────────────

    /// <summary>Save or update a chat session and all its messages.</summary>
    public void SaveSession(ChatSession session)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        try
        {
            // Upsert session
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO sessions (id, title, created_at, updated_at, project_path)
                    VALUES ($id, $title, $created, $updated, $project)
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title,
                        updated_at = excluded.updated_at,
                        project_path = excluded.project_path;";
                cmd.Parameters.AddWithValue("$id", session.Id);
                cmd.Parameters.AddWithValue("$title", session.Title);
                cmd.Parameters.AddWithValue("$created", session.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("o"));
                cmd.Parameters.AddWithValue("$project", session.ProjectPath ?? "");
                cmd.ExecuteNonQuery();
            }

            // Delete old messages and re-insert (simple & reliable)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM messages WHERE session_id = $sid;";
                cmd.Parameters.AddWithValue("$sid", session.Id);
                cmd.ExecuteNonQuery();
            }

            // Batch insert messages
            for (int i = 0; i < session.Messages.Count; i++)
            {
                var msg = session.Messages[i];
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO messages (id, session_id, role, content, timestamp,
                        tool_name, tool_summary, tool_output, tool_success, has_error, sort_order)
                    VALUES ($id, $sid, $role, $content, $ts,
                        $tn, $tsm, $to, $tsuc, $err, $order);";
                cmd.Parameters.AddWithValue("$id", msg.Id);
                cmd.Parameters.AddWithValue("$sid", session.Id);
                cmd.Parameters.AddWithValue("$role", (int)msg.Role);
                cmd.Parameters.AddWithValue("$content", msg.Content ?? "");
                cmd.Parameters.AddWithValue("$ts", msg.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("$tn", (object?)msg.ToolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tsm", (object?)msg.ToolSummary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$to", (object?)msg.ToolOutput ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tsuc", msg.ToolSuccess ? 1 : 0);
                cmd.Parameters.AddWithValue("$err", msg.HasError ? 1 : 0);
                cmd.Parameters.AddWithValue("$order", i);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Load session list (metadata only, no messages) ordered by last updated.</summary>
    public List<ChatSession> LoadSessionList()
    {
        var sessions = new List<ChatSession>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, title, created_at, updated_at, project_path
            FROM sessions ORDER BY updated_at DESC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new ChatSession
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                CreatedAt = DateTime.Parse(reader.GetString(2)),
                UpdatedAt = DateTime.Parse(reader.GetString(3)),
                ProjectPath = reader.IsDBNull(4) ? "" : reader.GetString(4),
            });
        }
        return sessions;
    }

    /// <summary>Load a full session with all messages.</summary>
    public ChatSession? LoadSession(string sessionId)
    {
        using var conn = Open();

        // Load session header
        ChatSession? session = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, created_at, updated_at, project_path FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                session = new ChatSession
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    CreatedAt = DateTime.Parse(reader.GetString(2)),
                    UpdatedAt = DateTime.Parse(reader.GetString(3)),
                    ProjectPath = reader.IsDBNull(4) ? "" : reader.GetString(4),
                };
            }
        }

        if (session == null) return null;

        // Load messages
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT id, role, content, timestamp,
                       tool_name, tool_summary, tool_output, tool_success, has_error
                FROM messages WHERE session_id = $sid
                ORDER BY sort_order;";
            cmd.Parameters.AddWithValue("$sid", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                session.Messages.Add(new ChatMessage
                {
                    Id = reader.GetString(0),
                    Role = (MessageRole)reader.GetInt32(1),
                    Content = reader.GetString(2),
                    Timestamp = DateTime.Parse(reader.GetString(3)),
                    ToolName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ToolSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ToolOutput = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ToolSuccess = reader.GetInt32(7) == 1,
                    HasError = reader.GetInt32(8) == 1,
                });
            }
        }

        return session;
    }

    /// <summary>Delete a session and all its messages (cascade).</summary>
    public void DeleteSession(string sessionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Get the ID of the most recently updated session.</summary>
    public string? GetLastSessionId()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM sessions ORDER BY updated_at DESC LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    // ──────────────────────────── Full-Text Search ────────────────────────────

    /// <summary>
    /// Search across all sessions using FTS5.
    /// Returns sessions whose messages match the query, with matching snippet.
    /// </summary>
    public List<SearchResult> SearchSessions(string query)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        // Sanitize query for FTS5 - escape special characters and add wildcards
        string safeQuery = SanitizeFtsQuery(query);

        cmd.CommandText = @"
            SELECT DISTINCT
                s.id,
                s.title,
                s.updated_at,
                snippet(messages_fts, 0, '»', '«', '...', 48) AS snippet,
                (SELECT COUNT(*) FROM messages_fts
                 WHERE messages_fts MATCH $q AND session_id = s.id) AS match_count
            FROM messages_fts fts
            JOIN sessions s ON s.id = fts.session_id
            WHERE messages_fts MATCH $q
            GROUP BY s.id
            ORDER BY match_count DESC, s.updated_at DESC
            LIMIT 50;";
        cmd.Parameters.AddWithValue("$q", safeQuery);

        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResult
                {
                    SessionId = reader.GetString(0),
                    SessionTitle = reader.GetString(1),
                    UpdatedAt = DateTime.Parse(reader.GetString(2)),
                    Snippet = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    MatchCount = reader.GetInt32(4),
                });
            }
        }
        catch (SqliteException)
        {
            // FTS query syntax error — try plain LIKE fallback
            return SearchSessionsFallback(conn, query);
        }

        return results;
    }

    private List<SearchResult> SearchSessionsFallback(SqliteConnection conn, string query)
    {
        var results = new List<SearchResult>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT
                s.id, s.title, s.updated_at,
                SUBSTR(m.content, MAX(1, INSTR(LOWER(m.content), LOWER($q)) - 30), 100) AS snippet,
                COUNT(*) AS match_count
            FROM messages m
            JOIN sessions s ON s.id = m.session_id
            WHERE m.content LIKE '%' || $q || '%'
            GROUP BY s.id
            ORDER BY match_count DESC, s.updated_at DESC
            LIMIT 50;";
        cmd.Parameters.AddWithValue("$q", query);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult
            {
                SessionId = reader.GetString(0),
                SessionTitle = reader.GetString(1),
                UpdatedAt = DateTime.Parse(reader.GetString(2)),
                Snippet = reader.IsDBNull(3) ? "" : reader.GetString(3),
                MatchCount = reader.GetInt32(4),
            });
        }
        return results;
    }

    /// <summary>Get basic stats about the database.</summary>
    public (int SessionCount, int MessageCount, long DbSizeBytes) GetStats()
    {
        using var conn = Open();
        int sessions = 0, messages = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
            sessions = Convert.ToInt32(cmd.ExecuteScalar());
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM messages;";
            messages = Convert.ToInt32(cmd.ExecuteScalar());
        }

        long size = 0;
        try { size = new FileInfo(_dbPath).Length; } catch { }

        return (sessions, messages, size);
    }

    // ──────────────────────────── JSON Migration ────────────────────────────

    /// <summary>
    /// Auto-import existing JSON session files into SQLite on first run.
    /// Moves processed files to a backup folder.
    /// </summary>
    private void MigrateFromJson(SettingsService settingsService)
    {
        string sessionsDir = settingsService.Settings.SessionDirectory;
        if (string.IsNullOrEmpty(sessionsDir) || !Directory.Exists(sessionsDir))
            return;

        var jsonFiles = Directory.GetFiles(sessionsDir, "session_*.json");
        if (jsonFiles.Length == 0) return;

        // Check if we already migrated (sessions table not empty)
        using var checkConn = Open();
        using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sessions;";
        int existing = Convert.ToInt32(checkCmd.ExecuteScalar());
        if (existing > 0) return; // Already have data, skip migration

        System.Diagnostics.Debug.WriteLine($"[DB] Migrating {jsonFiles.Length} JSON sessions to SQLite...");

        var jsonOpts = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        int migrated = 0;
        foreach (var file in jsonFiles)
        {
            try
            {
                string json = File.ReadAllText(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonSessionData>(json, jsonOpts);
                if (data == null) continue;

                var session = new ChatSession
                {
                    Id = data.Id,
                    Title = data.Title,
                    CreatedAt = data.CreatedAt,
                    UpdatedAt = data.UpdatedAt,
                    Messages = data.Messages.Select(m => new ChatMessage
                    {
                        Id = m.Id,
                        Role = m.Role,
                        Content = m.Content,
                        Timestamp = m.Timestamp,
                        ToolName = m.ToolName,
                        ToolSummary = m.ToolSummary,
                        ToolOutput = m.ToolOutput,
                        ToolSuccess = m.ToolSuccess,
                        HasError = m.HasError,
                    }).ToList(),
                };

                SaveSession(session);
                migrated++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] Failed to migrate {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (migrated > 0)
        {
            // Move JSON files to backup folder
            string backupDir = Path.Combine(sessionsDir, "_json_backup");
            try
            {
                Directory.CreateDirectory(backupDir);
                foreach (var file in jsonFiles)
                {
                    string dest = Path.Combine(backupDir, Path.GetFileName(file));
                    File.Move(file, dest, overwrite: true);
                }
                // Move index too
                string indexFile = Path.Combine(sessionsDir, "_index.json");
                if (File.Exists(indexFile))
                    File.Move(indexFile, Path.Combine(backupDir, "_index.json"), overwrite: true);

                System.Diagnostics.Debug.WriteLine($"[DB] Migrated {migrated} sessions. JSON files moved to _json_backup/");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] Backup move failed: {ex.Message}");
            }
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sanitize user input for FTS5 query. Wraps terms in quotes to prevent syntax errors.</summary>
    private static string SanitizeFtsQuery(string query)
    {
        // Split into tokens, wrap each in quotes, join with space (implicit AND)
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return "\"\"";

        // Escape quotes within tokens and wrap each in double quotes
        var sanitized = tokens.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"");
        return string.Join(" ", sanitized);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // SQLite connections are opened/closed per operation, nothing to dispose globally
    }

    // ─── JSON migration data models ───

    private class JsonSessionData
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<JsonMessageData> Messages { get; set; } = new();
    }

    private class JsonMessageData
    {
        public string Id { get; set; } = "";
        public MessageRole Role { get; set; }
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string? ToolName { get; set; }
        public string? ToolSummary { get; set; }
        public string? ToolOutput { get; set; }
        public bool ToolSuccess { get; set; }
        public bool HasError { get; set; }
    }
}

/// <summary>Result from full-text search across sessions.</summary>
public class SearchResult
{
    public string SessionId { get; set; } = "";
    public string SessionTitle { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string Snippet { get; set; } = "";
    public int MatchCount { get; set; }
}
