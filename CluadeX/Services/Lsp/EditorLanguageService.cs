using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Lsp;

/// <summary>
/// Live errors and hover for the Code Editor, from a real language server when one is installed.
///
/// Why this and not full IntelliSense: in vibe coding the AI writes and the human reviews, so the moment that matters
/// is right after an edit — are there errors now? Completion / F12 / rename keep working off the heuristic scan in
/// <see cref="CodeIntelligenceService"/>; this service adds what a heuristic cannot fake: the compiler's own errors.
///
/// One server per language for the open project, started the first time a file of that language is shown, stopped
/// when the project changes and when the app exits (a polite shutdown, then the process tree is killed). The editor
/// reports each edit as it happens (<see cref="ChangeDocument"/>) and the service sends them after a pause in typing —
/// incrementally when the server accepts that, so a keystroke in a big file costs a few bytes, not the whole file.
/// </summary>
public sealed class EditorLanguageService : IDisposable, IAppShutdownHook
{
    /// <summary>The app's instance (set by the constructor) — the editor view is rebuilt per visit and finds it here.</summary>
    public static EditorLanguageService? Current { get; private set; }

    /// <summary>Above this the file is not checked live: a server that only takes the whole text would be sent megabytes
    /// on every pause in typing. The editor's own large-buffer threshold, for the same reason.</summary>
    public const int MaxLiveChars = 2_000_000;

    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(350);
    /// <summary>A longer pause for big files: the server re-checks the whole file each time.</summary>
    public TimeSpan LargeFileDebounce { get; set; } = TimeSpan.FromMilliseconds(1000);
    public int LargeFileChars { get; set; } = 200_000;
    /// <summary>How long a server may take to answer initialize (csharp-ls loads the whole solution first).</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan HoverTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan StopBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Finds a language's server; the catalog's PATH probe by default. Tests point it at a fake server.</summary>
    public Func<LanguageServerLanguage, ResolvedLanguageServer?> Resolver { get; set; } = LanguageServerCatalog.Resolve;

    /// <summary>Diagnostics for a file changed (full path). Raised on a background thread.</summary>
    public event Action<string>? DiagnosticsChanged;
    /// <summary>A language's server changed state (language key). Raised on a background thread.</summary>
    public event Action<string>? StatusChanged;

    private readonly object _gate = new();
    private readonly Dictionary<string, ServerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedDocument> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<EditorDiagnostic>> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tooLarge = new(StringComparer.OrdinalIgnoreCase);
    private string? _root;
    private bool _disposed;

    public EditorLanguageService(FileSystemService? fs = null)
    {
        Current = this;
        // The project closed or changed: its servers go with it (they hold that project's whole solution in memory).
        if (fs != null)
            fs.OnWorkingDirectoryChanged += () =>
            {
                string? now = fs.AppWorkingDirectory;
                bool moved;
                lock (_gate) moved = _root != null && !LspPaths.SamePath(_root, now);
                if (moved) _ = Task.Run(() => StopAllAsync());
            };
    }

    // ═══════════════════════════ Documents ═══════════════════════════

    /// <summary>The editor shows <paramref name="path"/> (of project <paramref name="root"/>). <paramref name="text"/>
    /// returns its current text and may be called from any thread (the editor passes an immutable snapshot).</summary>
    public void OpenDocument(string? root, string path, Func<string> text, int length)
    {
        var lang = LanguageServerCatalog.ForPath(path);
        if (lang == null || _disposed) return;
        path = LspPaths.Normalize(path);
        root = LspPaths.Normalize(string.IsNullOrEmpty(root) ? Path.GetDirectoryName(path) ?? path : root);

        bool switched;
        lock (_gate) switched = _root != null && !LspPaths.SamePath(_root, root);
        // Another project: its servers go. StopAllAsync forgets them before its first await, so this does not wait
        // for the old processes to end — the shutdown handshake finishes in the background.
        if (switched) _ = StopAllAsync();

        bool start = false;
        lock (_gate)
        {
            _root = root;
            if (_docs.Remove(path, out var old)) CloseCore(old);
            if (length > MaxLiveChars)
            {
                _tooLarge.Add(path);
            }
            else
            {
                _tooLarge.Remove(path);
                if (!_sessions.TryGetValue(lang.Key, out var session))
                {
                    session = new ServerSession(lang, root);
                    _sessions[lang.Key] = session;
                    start = true;
                }
                var doc = new TrackedDocument(path, lang.LanguageIdFor(path), session) { Text = text, Length = length };
                _docs[path] = doc;
                if (session.State == LspServerState.Running) SendOpen(doc);
            }
        }
        if (start) _ = Task.Run(() => StartAsync(lang.Key));
        StatusChanged?.Invoke(lang.Key);
    }

    /// <summary>One edit to an open document. <paramref name="change"/> is the edit in LSP terms (null = "too big to
    /// describe, send the whole text"); <paramref name="text"/> returns the text after it.</summary>
    public void ChangeDocument(string path, LspTextChange? change, Func<string> text, int length)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(LspPaths.Normalize(path), out var doc)) return;
            doc.Text = text;
            doc.Length = length;
            if (change == null || doc.PendingFull) { doc.PendingFull = true; doc.Pending.Clear(); }
            else
            {
                doc.Pending.Add(change);
                // A long burst (a paste of many lines, a replace-all) is cheaper to send as the text itself.
                if (doc.Pending.Count > 500) { doc.PendingFull = true; doc.Pending.Clear(); }
            }
            var wait = length >= LargeFileChars ? LargeFileDebounce : Debounce;
            doc.Timer ??= new Timer(_ => Flush(doc));
            doc.Timer.Change(wait, Timeout.InfiniteTimeSpan);   // restart: send once the typing pauses
        }
    }

    /// <summary>Send what is waiting now instead of after the pause (before a hover, before a switch).</summary>
    public void FlushNow(string path)
    {
        TrackedDocument? doc;
        lock (_gate) _docs.TryGetValue(LspPaths.Normalize(path), out doc);
        if (doc != null) Flush(doc);
    }

    /// <summary>The editor no longer shows <paramref name="path"/>.</summary>
    public void CloseDocument(string path)
    {
        path = LspPaths.Normalize(path);
        bool hadDiagnostics;
        lock (_gate)
        {
            _tooLarge.Remove(path);
            if (_docs.Remove(path, out var doc)) CloseCore(doc);
            hadDiagnostics = _diagnostics.Remove(path);
        }
        if (hadDiagnostics) DiagnosticsChanged?.Invoke(path);
    }

    private void CloseCore(TrackedDocument doc)
    {
        doc.Timer?.Dispose();
        doc.Timer = null;
        doc.Closed = true;
        if (doc.Opened && doc.Session.Connection is { IsAlive: true } c)
            c.Notify("textDocument/didClose", new { textDocument = new { uri = doc.Uri } });
        doc.Opened = false;
    }

    private void Flush(TrackedDocument doc)
    {
        lock (_gate)
        {
            doc.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (doc.Closed || (doc.Pending.Count == 0 && !doc.PendingFull)) return;
            var c = doc.Session.Connection;
            if (!doc.Opened || c is not { IsAlive: true } || doc.Session.State != LspServerState.Running)
            {
                // Not open with the server yet: didOpen will carry the latest text, these edits are already in it.
                doc.Pending.Clear();
                doc.PendingFull = false;
                return;
            }
            doc.Version++;
            object[] changes = doc.Session.SyncKind == 2 && !doc.PendingFull
                ? doc.Pending.Select(p => (object)new
                {
                    range = new
                    {
                        start = new { line = p.StartLine, character = p.StartCharacter },
                        end = new { line = p.EndLine, character = p.EndCharacter },
                    },
                    text = p.Text,
                }).ToArray()
                : new object[] { new { text = doc.Text() } };
            doc.Pending.Clear();
            doc.PendingFull = false;
            c.Notify("textDocument/didChange", new { textDocument = new { uri = doc.Uri, version = doc.Version }, contentChanges = changes });
        }
    }

    private static void SendOpen(TrackedDocument doc)
    {
        doc.Pending.Clear();
        doc.PendingFull = false;
        doc.Session.Connection?.Notify("textDocument/didOpen", new
        {
            textDocument = new { uri = doc.Uri, languageId = doc.LanguageId, version = doc.Version, text = doc.Text() },
        });
        doc.Opened = true;
    }

    // ═══════════════════════════ What the editor reads ═══════════════════════════

    public IReadOnlyList<EditorDiagnostic> DiagnosticsFor(string path)
    {
        lock (_gate) return _diagnostics.TryGetValue(LspPaths.Normalize(path), out var list) ? list : Array.Empty<EditorDiagnostic>();
    }

    /// <summary>Every file with problems right now (the open one, and any other the server chose to report).</summary>
    public IReadOnlyList<(string Path, IReadOnlyList<EditorDiagnostic> Items)> AllDiagnostics()
    {
        lock (_gate) return _diagnostics.Where(kv => kv.Value.Count > 0).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public LanguageServerStatus StatusFor(string? path)
    {
        var lang = LanguageServerCatalog.ForPath(path);
        if (lang == null || path == null) return LanguageServerStatus.None;
        lock (_gate)
        {
            string key = LspPaths.Normalize(path);
            if (_tooLarge.Contains(key))
                return new(lang.Key, lang.Name, LspServerState.TooLarge, null, null, null, null);
            // Not open with a server (the project it belonged to was closed): nothing to report, rather than a
            // "starting…" that never ends.
            if (!_docs.ContainsKey(key) || !_sessions.TryGetValue(lang.Key, out var s))
                return LanguageServerStatus.None;
            return new(lang.Key, lang.Name, s.State, s.Server?.Name, lang.InstallCommand, lang.InstallUrl, s.Detail);
        }
    }

    /// <summary>Process ids of the servers running now (the harness checks they are gone after a stop).</summary>
    public IReadOnlyList<int> ServerProcessIds()
    {
        lock (_gate) return _sessions.Values.Where(s => s.Connection != null).Select(s => s.Connection!.ProcessId).ToList();
    }

    /// <summary>Hover text at a 0-based position, or null. Edits still waiting for the pause are sent first, so the
    /// server answers about the text on screen.</summary>
    public async Task<string?> HoverAsync(string path, int line, int character, CancellationToken ct = default)
    {
        TrackedDocument? doc;
        lock (_gate) _docs.TryGetValue(LspPaths.Normalize(path), out doc);
        if (doc == null || doc.Session.State != LspServerState.Running || !doc.Session.HoverSupported) return null;
        Flush(doc);
        var c = doc.Session.Connection;
        if (c == null) return null;
        try
        {
            var result = await c.RequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = doc.Uri },
                position = new { line, character },
            }, HoverTimeout, ct).ConfigureAwait(false);
            return HoverText(result);
        }
        catch { return null; /* a slow or failing hover is no hover, never an error box */ }
    }

    /// <summary>Hover contents (string | MarkupContent | MarkedString | an array of them) as plain text: code fences
    /// dropped, the code inside kept.</summary>
    public static string? HoverText(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents)) return null;
        var sb = new StringBuilder();
        void Add(JsonElement e)
        {
            string? s = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object when e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(s)) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(s.Trim());
        }
        if (contents.ValueKind == JsonValueKind.Array) foreach (var e in contents.EnumerateArray()) Add(e);
        else Add(contents);

        var lines = sb.ToString().Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .Select(l => l.TrimEnd());
        string text = string.Join("\n", lines).Trim();
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        if (text.Length > 4000) text = text[..4000] + " …";
        return text.Length == 0 ? null : text;
    }

    // ═══════════════════════════ Servers ═══════════════════════════

    private async Task StartAsync(string languageKey)
    {
        ServerSession? session;
        lock (_gate) _sessions.TryGetValue(languageKey, out session);
        if (session == null) return;

        ResolvedLanguageServer? server;
        try { server = Resolver(session.Language); }
        catch { server = null; }
        if (server == null)
        {
            SetState(session, LspServerState.NotInstalled, null);
            return;
        }
        session.Server = server;

        LspConnection conn;
        try { conn = LspConnection.Start(server.Executable, server.Arguments, session.Root); }
        catch (Exception ex)
        {
            SetState(session, LspServerState.Failed, ex.Message);
            return;
        }
        string rootUri = LspPaths.PathToUri(session.Root);
        conn.WorkspaceFolderUris = new[] { rootUri };
        conn.Notification += (method, p) => OnNotification(session, method, p);
        conn.Exited += () => OnServerExited(session, conn);
        lock (_gate)
        {
            if (session.Stopping || !ReferenceEquals(_sessions.GetValueOrDefault(languageKey), session))
            {
                conn.Dispose();   // stopped while it was being found
                return;
            }
            session.Connection = conn;
        }

        try
        {
            var init = await conn.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "CluadeX" },
                rootPath = session.Root,
                rootUri,
                workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(session.Root.TrimEnd('\\')) } },
                capabilities = new
                {
                    general = new { positionEncodings = new[] { "utf-16" } },
                    workspace = new { configuration = true, workspaceFolders = true },
                    window = new { workDoneProgress = false },
                    textDocument = new
                    {
                        synchronization = new { dynamicRegistration = false, didSave = false, willSave = false },
                        publishDiagnostics = new { relatedInformation = false, versionSupport = true },
                        hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                    },
                },
            }, StartTimeout).ConfigureAwait(false);
            ReadCapabilities(session, init);
            conn.Notify("initialized", new { });
        }
        catch (Exception ex)
        {
            string why = conn.StderrTail is { Length: > 0 } tail ? LastLine(tail) : ex.Message;
            conn.Dispose();
            SetState(session, LspServerState.Failed, why);
            return;
        }

        lock (_gate)
        {
            if (session.Stopping) return;
            session.State = LspServerState.Running;
            session.Detail = null;
            foreach (var doc in _docs.Values.Where(d => ReferenceEquals(d.Session, session) && !d.Opened))
                SendOpen(doc);
        }
        StatusChanged?.Invoke(languageKey);
    }

    private static void ReadCapabilities(ServerSession session, JsonElement init)
    {
        session.SyncKind = 1;
        session.HoverSupported = false;
        if (init.ValueKind != JsonValueKind.Object || !init.TryGetProperty("capabilities", out var caps)) return;
        if (caps.TryGetProperty("textDocumentSync", out var sync))
        {
            if (sync.ValueKind == JsonValueKind.Number) session.SyncKind = sync.GetInt32();
            else if (sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("change", out var ch) && ch.ValueKind == JsonValueKind.Number)
                session.SyncKind = ch.GetInt32();
        }
        // 0 = "send nothing" — then the server never sees an edit and its errors go stale; the whole text it is.
        if (session.SyncKind is not (1 or 2)) session.SyncKind = 1;
        if (caps.TryGetProperty("hoverProvider", out var hover))
            session.HoverSupported = hover.ValueKind == JsonValueKind.Object || hover.ValueKind == JsonValueKind.True;
    }

    private void OnNotification(ServerSession session, string method, JsonElement p)
    {
        if (method != "textDocument/publishDiagnostics" || p.ValueKind != JsonValueKind.Object) return;
        string path = LspPaths.UriToPath(p.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "");
        if (path.Length == 0) return;
        var list = ParseDiagnostics(p);
        lock (_gate)
        {
            if (session.Stopping) return;
            if (_docs.TryGetValue(path, out var doc))
            {
                // Results for a text we have already changed again: the next publish is on its way — showing these
                // would put squiggles on the wrong characters for a moment.
                if (p.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() < doc.Version) return;
                if (doc.Pending.Count > 0 || doc.PendingFull) return;
            }
            _diagnostics[path] = list;
        }
        DiagnosticsChanged?.Invoke(path);
    }

    public static IReadOnlyList<EditorDiagnostic> ParseDiagnostics(JsonElement p)
    {
        var list = new List<EditorDiagnostic>();
        if (!p.TryGetProperty("diagnostics", out var diags) || diags.ValueKind != JsonValueKind.Array) return list;
        foreach (var d in diags.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("range", out var range)) continue;
            var (sl, sc) = Position(range, "start");
            var (el, ec) = Position(range, "end");
            int sev = d.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1;
            string code = d.TryGetProperty("code", out var c)
                ? c.ValueKind switch { JsonValueKind.String => c.GetString() ?? "", JsonValueKind.Number => c.GetRawText(), _ => "" }
                : "";
            list.Add(new EditorDiagnostic(sl, sc, el, ec,
                sev is >= 1 and <= 4 ? (LspSeverity)sev : LspSeverity.Error,
                d.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                d.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() ?? "" : "",
                code));
        }
        list.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine) : a.StartCharacter.CompareTo(b.StartCharacter));
        return list;
    }

    private static (int Line, int Character) Position(JsonElement range, string name)
    {
        if (!range.TryGetProperty(name, out var pos) || pos.ValueKind != JsonValueKind.Object) return (0, 0);
        int line = pos.TryGetProperty("line", out var l) && l.TryGetInt32(out int li) ? Math.Max(0, li) : 0;
        int ch = pos.TryGetProperty("character", out var c) && c.TryGetInt32(out int ci) ? Math.Max(0, ci) : 0;
        return (line, ch);
    }

    private void OnServerExited(ServerSession session, LspConnection conn)
    {
        var cleared = new List<string>();
        lock (_gate)
        {
            if (session.Stopping || !ReferenceEquals(session.Connection, conn)) return;
            session.Connection = null;
            session.State = LspServerState.Failed;
            session.Detail = conn.StderrTail is { Length: > 0 } tail ? LastLine(tail) : null;
            foreach (var doc in _docs.Values.Where(d => ReferenceEquals(d.Session, session)))
            {
                doc.Opened = false;
                if (_diagnostics.Remove(doc.Path)) cleared.Add(doc.Path);
            }
        }
        // Its errors are no longer being kept up to date — leaving them on screen would show a fixed bug as still there.
        foreach (var path in cleared) DiagnosticsChanged?.Invoke(path);
        StatusChanged?.Invoke(session.Language.Key);
    }

    private void SetState(ServerSession session, LspServerState state, string? detail)
    {
        lock (_gate)
        {
            if (session.Stopping) return;
            session.State = state;
            session.Detail = detail;
        }
        StatusChanged?.Invoke(session.Language.Key);
    }

    /// <summary>"Check again" after installing a server, or "Restart" after one stopped: look for it afresh and
    /// re-open the language's files with it.</summary>
    public async Task RestartAsync(string languageKey)
    {
        ServerSession? old;
        lock (_gate)
        {
            _sessions.TryGetValue(languageKey, out old);
            if (old == null) return;
            old.Stopping = true;
        }
        if (old.Connection is { } c) await c.StopAsync(StopBudget).ConfigureAwait(false);
        var fresh = new ServerSession(old.Language, old.Root);
        lock (_gate)
        {
            _sessions[languageKey] = fresh;
            foreach (var doc in _docs.Values.Where(d => ReferenceEquals(d.Session, old)).ToList())
            {
                doc.Timer?.Dispose();
                var moved = new TrackedDocument(doc.Path, doc.LanguageId, fresh) { Text = doc.Text, Length = doc.Length };
                _docs[doc.Path] = moved;
                doc.Closed = true;
                _diagnostics.Remove(doc.Path);
            }
        }
        StatusChanged?.Invoke(languageKey);
        await StartAsync(languageKey).ConfigureAwait(false);
    }

    /// <summary>Stop every server of the project (it closed, or another opened): shutdown politely, kill what lingers.</summary>
    public async Task StopAllAsync(TimeSpan? budget = null)
    {
        List<ServerSession> sessions;
        List<string> cleared;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            foreach (var s in sessions) s.Stopping = true;
            _sessions.Clear();
            foreach (var doc in _docs.Values) { doc.Timer?.Dispose(); doc.Closed = true; }
            _docs.Clear();
            _tooLarge.Clear();
            cleared = _diagnostics.Keys.ToList();
            _diagnostics.Clear();
            _root = null;
        }
        await Task.WhenAll(sessions.Where(s => s.Connection != null).Select(s => s.Connection!.StopAsync(budget ?? StopBudget))).ConfigureAwait(false);
        foreach (var path in cleared) DiagnosticsChanged?.Invoke(path);
        foreach (var s in sessions) StatusChanged?.Invoke(s.Language.Key);
    }

    private static string LastLine(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string last = lines.Length > 0 ? lines[^1].Trim() : text.Trim();
        return last.Length > 300 ? last[..300] + " …" : last;
    }

    /// <summary>App exit: one second for the shutdown handshake, then the process trees are killed (a hook must be
    /// quick; the job object kills anything left once CluadeX itself is gone).</summary>
    public void OnAppShutdown()
    {
        try { StopAllAsync(TimeSpan.FromSeconds(1)).Wait(TimeSpan.FromMilliseconds(1500)); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<LspConnection> left;
        lock (_gate) left = _sessions.Values.Where(s => s.Connection != null).Select(s => s.Connection!).ToList();
        if (left.Count > 0) OnAppShutdown();
        foreach (var c in left) c.Dispose();   // anything that outlived the polite stop
        if (ReferenceEquals(Current, this)) Current = null;
    }

    // ═══════════════════════════ State ═══════════════════════════

    private sealed class ServerSession(LanguageServerLanguage language, string root)
    {
        public LanguageServerLanguage Language { get; } = language;
        public string Root { get; } = root;
        public ResolvedLanguageServer? Server { get; set; }
        public LspConnection? Connection { get; set; }
        public LspServerState State { get; set; } = LspServerState.Starting;
        public string? Detail { get; set; }
        /// <summary>TextDocumentSyncKind: 1 = whole text, 2 = incremental.</summary>
        public int SyncKind { get; set; } = 1;
        public bool HoverSupported { get; set; }
        public bool Stopping { get; set; }
    }

    private sealed class TrackedDocument(string path, string languageId, ServerSession session)
    {
        public string Path { get; } = path;
        public string Uri { get; } = LspPaths.PathToUri(path);
        public string LanguageId { get; } = languageId;
        public ServerSession Session { get; } = session;
        public Func<string> Text { get; set; } = () => "";
        public int Length { get; set; }
        public int Version { get; set; } = 1;
        public bool Opened { get; set; }
        public bool Closed { get; set; }
        public List<LspTextChange> Pending { get; } = new();
        public bool PendingFull { get; set; }
        public Timer? Timer { get; set; }
    }
}
