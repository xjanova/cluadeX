using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Routines;

namespace CluadeX.Services.Remote;

/// <summary>What the bridge keeps in telegram.json beside codex.db. The token is DPAPI-encrypted ("ENC:…").</summary>
public sealed class TelegramConfig
{
    public string Token { get; set; } = "";
    public string BotName { get; set; } = "";
    /// <summary>The paired private chat (= the owner's Telegram user id); null = not paired.</summary>
    public long? ChatId { get; set; }
    public string UserName { get; set; } = "";
    public bool NotifyRoutines { get; set; } = true;
    /// <summary>Every chat's permission cards go to the phone, not only the Telegram chat's and the routines'.</summary>
    public bool ForwardAllCards { get; set; }
    /// <summary>The CluadeX chat messages from Telegram go into (null = the next one opens a new chat).</summary>
    public string? ChatSessionId { get; set; }
    /// <summary>The project that chat opens in ("" = none) — /use picks it.</summary>
    public string Project { get; set; } = "";
    /// <summary>getUpdates offset: the next update to read.</summary>
    public long Offset { get; set; }
}

/// <summary>
/// The owner's phone, through a Telegram bot they made with BotFather (they paste its token on the Routines page):
/// - pairing: the page shows a 6-digit code for 10 minutes; "/pair CODE" from Telegram ties the bot to that one private
///   chat. Anyone else who writes to the bot is ignored. Five wrong codes close the pairing.
/// - a plain message goes into a CluadeX chat (the "Telegram chat"; /new starts another, /use N picks the project) as if
///   typed there; the reply comes back. /status, /routines, /run, /stop, /help.
/// - a routine's run ends → a short summary (NotifyRoutines).
/// - a permission card in the Telegram chat or a routine's run (every chat with ForwardAllCards) comes with Allow / Deny
///   buttons. Danger, strict and BrainX cards go as a notice only: they are answered on the PC.
/// Messages that waited while CluadeX was closed are not acted on (<see cref="StaleAfter"/>). Long polling only: no port.
/// </summary>
public sealed class TelegramBridge : IDisposable
{
    public static TelegramBridge? Current { get; set; }

    public static readonly TimeSpan PairFor = TimeSpan.FromMinutes(10);
    public const int PairTries = 5;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    public const int MaxText = 3900;

    private readonly string? _path;
    private readonly Func<string, ITelegramApi> _apiFor;
    private readonly object _gate = new();
    private TelegramConfig _cfg = new();
    private ITelegramApi? _api;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private string? _pairCode;
    private DateTime _pairUntil;
    private int _pairTriesLeft;
    private CancellationTokenSource? _turn;
    private readonly HashSet<string> _remoteChats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardLink> _cards = new(StringComparer.Ordinal);

    // ─── the host (the chat page) ───

    /// <summary>Send text into a chat (null = open a new one in the project) and wait for the turn: (chat id, reply, error).</summary>
    public Func<string?, string, string, CancellationToken, Task<(string ChatId, string Reply, bool Error)>>? AskChat { get; set; }
    /// <summary>What runs now and waits for the owner, in a few lines.</summary>
    public Func<Task<string>>? StatusText { get; set; }
    /// <summary>The recent project folders (/projects, /use).</summary>
    public Func<List<string>>? RecentProjects { get; set; }
    /// <summary>Run on the UI thread (answering a card).</summary>
    public Func<Action, Task> OnUi { get; set; } = a => { a(); return Task.CompletedTask; };
    /// <summary>The UI's words.</summary>
    public Func<string, string> Loc { get; set; } = k => { try { return LocalizedResources.Instance[k]; } catch { return k; } };
    /// <summary>The clock (tests set it).</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>False = never read on its own (tests call <see cref="PollOnceAsync"/>).</summary>
    public bool AutoRead { get; set; } = true;

    /// <summary>Raised (any thread) when the state the page shows changed.</summary>
    public event Action? Changed;

    public TelegramBridge(string? dataRoot, Func<string, ITelegramApi>? apiFor = null)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "telegram.json");
        _apiFor = apiFor ?? (token => new TelegramHttpApi(token));
        Load();
        if (RoutineService.Current is { } routines) routines.RunFinished += OnRoutineFinished;
    }

    // ─── what the page shows ───

    public bool HasToken { get { lock (_gate) return _cfg.Token.Length > 0; } }
    public bool IsPaired { get { lock (_gate) return _cfg.ChatId != null; } }
    public string BotName { get { lock (_gate) return _cfg.BotName; } }
    public string PairedName { get { lock (_gate) return _cfg.UserName; } }
    public bool NotifyRoutines { get { lock (_gate) return _cfg.NotifyRoutines; } }
    public bool ForwardAllCards { get { lock (_gate) return _cfg.ForwardAllCards; } }
    public bool IsRunning => _loopTask is { IsCompleted: false };
    /// <summary>The pairing code while it is open, else null.</summary>
    public string? PairCode { get { lock (_gate) return _pairCode != null && UtcNow() < _pairUntil ? _pairCode : null; } }
    public DateTime PairUntilUtc { get { lock (_gate) return _pairUntil; } }
    private string _lastError = "";
    public string LastError { get { lock (_gate) return _lastError; } }

    private void SetError(string e)
    {
        bool moved;
        lock (_gate) { moved = _lastError != e; _lastError = e; }
        if (moved) Changed?.Invoke();
    }

    // ─── the store ───

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try { _cfg = JsonSerializer.Deserialize<TelegramConfig>(File.ReadAllText(_path), Json) ?? new(); }
        catch { _cfg = new(); }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_cfg, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }
        catch { /* kept in memory; the next change saves it */ }
    }

    private static string Protect(string token) =>
        "ENC:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith("ENC:", StringComparison.Ordinal)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[4..]), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }   // another Windows user's file: unreadable
    }

    /// <summary>What a bot token looks like ("123456789:AA…"). Only the shape: <see cref="ConnectAsync"/> asks Telegram.</summary>
    public static bool LooksLikeToken(string? token) =>
        System.Text.RegularExpressions.Regex.IsMatch((token ?? "").Trim(), @"^\d{5,15}:[A-Za-z0-9_-]{30,60}$");

    // ─── connect, pair ───

    /// <summary>Check <paramref name="token"/> with Telegram (getMe), keep it encrypted, start reading. Returns the
    /// localization key of what went wrong, or null.</summary>
    public async Task<string?> ConnectAsync(string token, CancellationToken ct = default)
    {
        token = (token ?? "").Trim();
        if (!LooksLikeToken(token)) return "tg.err.tokenShape";
        var api = _apiFor(token);
        string bot;
        try
        {
            var me = await api.CallAsync("getMe", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
            bot = me.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            // What waited for the bot before now is not acted on.
            await DropPendingAsync(api, ct).ConfigureAwait(false);
        }
        catch (TelegramApiException ex)
        {
            (api as IDisposable)?.Dispose();
            SetError(ex.Message);
            return ex.Code == 401 || ex.Code == 404 ? "tg.err.tokenWrong" : "tg.err.network";
        }
        Stop();
        lock (_gate)
        {
            _cfg.Token = Protect(token);
            _cfg.BotName = bot;
            _cfg.ChatId = null;
            _cfg.UserName = "";
            _cfg.ChatSessionId = null;
            _api = api;
        }
        Save();
        SetError("");
        if (AutoRead) StartLoop();
        Changed?.Invoke();
        return null;
    }

    private async Task DropPendingAsync(ITelegramApi api, CancellationToken ct)
    {
        var last = await api.CallAsync("getUpdates", new Dictionary<string, object?> { ["offset"] = -1, ["timeout"] = 0 }, ct).ConfigureAwait(false);
        long offset = 0;
        if (last.ValueKind == JsonValueKind.Array)
            foreach (var u in last.EnumerateArray())
                if (u.TryGetProperty("update_id", out var id) && id.TryGetInt64(out long n)) offset = Math.Max(offset, n + 1);
        lock (_gate) _cfg.Offset = offset;
    }

    /// <summary>Forget the token and the pairing; stop reading.</summary>
    public void Disconnect()
    {
        Stop();
        lock (_gate)
        {
            _cfg = new TelegramConfig { NotifyRoutines = _cfg.NotifyRoutines, ForwardAllCards = _cfg.ForwardAllCards };
            (_api as IDisposable)?.Dispose();
            _api = null;
            _pairCode = null;
            _cards.Clear();
        }
        Save();
        SetError("");
        Changed?.Invoke();
    }

    /// <summary>Open pairing: a fresh 6-digit code, good for <see cref="PairFor"/> and <see cref="PairTries"/> wrong tries.</summary>
    public string StartPairing()
    {
        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        lock (_gate) { _pairCode = code; _pairUntil = UtcNow() + PairFor; _pairTriesLeft = PairTries; }
        Changed?.Invoke();
        return code;
    }

    public void Unpair()
    {
        lock (_gate) { _cfg.ChatId = null; _cfg.UserName = ""; _cfg.ChatSessionId = null; _cards.Clear(); }
        Save();
        Changed?.Invoke();
    }

    public void SetOptions(bool notifyRoutines, bool forwardAllCards)
    {
        lock (_gate) { _cfg.NotifyRoutines = notifyRoutines; _cfg.ForwardAllCards = forwardAllCards; }
        Save();
        Changed?.Invoke();
    }

    // ─── reading ───

    /// <summary>Start reading when a token is kept (the app's start).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cfg.Token.Length == 0) return;
            if (_api == null)
            {
                string token = Unprotect(_cfg.Token);
                if (token.Length == 0) { _lastError = "The kept token cannot be read on this Windows account — connect again."; return; }
                _api = _apiFor(token);
            }
        }
        if (AutoRead) StartLoop();
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false } || _api == null) return;
            _loop = new CancellationTokenSource();
            var ct = _loop.Token;
            _loopTask = Task.Run(() => LoopAsync(ct));
        }
    }

    private void Stop()
    {
        CancellationTokenSource? loop;
        lock (_gate) { loop = _loop; _loop = null; }
        try { loop?.Cancel(); } catch { }
        try { _turn?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        if (RoutineService.Current is { } routines) routines.RunFinished -= OnRoutineFinished;
        (_api as IDisposable)?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(25, ct).ConfigureAwait(false);
                SetError("");
                wait = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (TelegramApiException ex) when (ex.Code == 401 || ex.Code == 404)
            {
                SetError(Loc("tg.err.tokenWrong"));
                return;   // a revoked token: nothing more to read until the owner connects again
            }
            catch (Exception ex)
            {
                // 409: another CluadeX (or program) reads this bot — wait longer.
                SetError(ex is TelegramApiException { Code: 409 } ? Loc("tg.err.conflict") : ex.Message);
                try { await Task.Delay(ex is TelegramApiException { Code: 409 } ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
        }
    }

    /// <summary>One getUpdates (long-polls up to <paramref name="timeoutSeconds"/>) and what it brought. Returns how many
    /// updates were read. Public for tests.</summary>
    public async Task<int> PollOnceAsync(int timeoutSeconds, CancellationToken ct)
    {
        ITelegramApi api;
        long offset;
        lock (_gate)
        {
            api = _api ?? throw new InvalidOperationException("not connected");
            offset = _cfg.Offset;
        }
        var updates = await api.CallAsync("getUpdates", new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "message", "callback_query" },
        }, ct).ConfigureAwait(false);
        if (updates.ValueKind != JsonValueKind.Array) return 0;
        int n = 0;
        foreach (var u in updates.EnumerateArray())
        {
            n++;
            if (u.TryGetProperty("update_id", out var id) && id.TryGetInt64(out long uid))
                lock (_gate) _cfg.Offset = Math.Max(_cfg.Offset, uid + 1);
            try
            {
                if (u.TryGetProperty("message", out var m)) await OnMessageAsync(api, m, ct).ConfigureAwait(false);
                else if (u.TryGetProperty("callback_query", out var q)) await OnCallbackAsync(api, q, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { SetError(ex.Message); }
        }
        if (n > 0) Save();
        return n;
    }

    // ─── messages ───

    private async Task OnMessageAsync(ITelegramApi api, JsonElement m, CancellationToken ct)
    {
        if (!m.TryGetProperty("chat", out var chat) || Str(chat, "type") != "private") return;   // groups, channels: never
        long chatId = Long(chat, "id");
        string text = Str(m, "text").Trim();
        if (text.Length == 0) return;
        long? paired;
        lock (_gate) paired = _cfg.ChatId;

        if (text.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            await PairAsync(api, chatId, m, text, ct).ConfigureAwait(false);
            return;
        }
        if (paired != chatId) return;   // a stranger: no answer at all

        // What waited while CluadeX was closed is not done now, hours later.
        if (Long(m, "date") is > 0 and var date && UtcNow() - DateTimeOffset.FromUnixTimeSeconds(date).UtcDateTime > StaleAfter)
        {
            await SendAsync(api, chatId, Loc("tg.stale"), ct).ConfigureAwait(false);
            return;
        }

        string cmd = text.Split(' ', 2)[0].ToLowerInvariant();
        int at = cmd.IndexOf('@');   // "/status@MyBot"
        if (at > 0) cmd = cmd[..at];
        string arg = text.Contains(' ') ? text.Split(' ', 2)[1].Trim() : "";
        switch (cmd)
        {
            case "/start" or "/help":
                await SendAsync(api, chatId, Loc("tg.help"), ct).ConfigureAwait(false);
                return;
            case "/status":
                await SendAsync(api, chatId, StatusText == null ? Loc("tg.noApp") : await StatusText().ConfigureAwait(false), ct).ConfigureAwait(false);
                return;
            case "/routines":
                await SendAsync(api, chatId, RoutinesText(), ct).ConfigureAwait(false);
                return;
            case "/run":
                await SendAsync(api, chatId, RunRoutine(arg), ct).ConfigureAwait(false);
                return;
            case "/new":
                lock (_gate) _cfg.ChatSessionId = null;
                Save();
                await SendAsync(api, chatId, Loc("tg.newChat"), ct).ConfigureAwait(false);
                return;
            case "/projects":
                await SendAsync(api, chatId, ProjectsText(), ct).ConfigureAwait(false);
                return;
            case "/use":
                await SendAsync(api, chatId, UseProject(arg), ct).ConfigureAwait(false);
                return;
            case "/stop":
            {
                var turn = _turn;
                bool stopped = turn != null && !turn.IsCancellationRequested;
                try { turn?.Cancel(); } catch { }
                await SendAsync(api, chatId, Loc(stopped ? "tg.stopped" : "tg.nothingToStop"), ct).ConfigureAwait(false);
                return;
            }
        }
        if (text.StartsWith('/')) { await SendAsync(api, chatId, Loc("tg.unknown"), ct).ConfigureAwait(false); return; }
        await AskAsync(api, chatId, text, ct).ConfigureAwait(false);
    }

    private async Task PairAsync(ITelegramApi api, long chatId, JsonElement m, string text, CancellationToken ct)
    {
        string given = text.Length > 5 ? text[5..].Trim() : "";
        string reply;
        lock (_gate)
        {
            if (_pairCode == null || UtcNow() >= _pairUntil) reply = Loc("tg.pairClosed");
            else if (Same(given, _pairCode))
            {
                _cfg.ChatId = chatId;
                _cfg.UserName = m.TryGetProperty("from", out var from)
                    ? (Str(from, "username") is { Length: > 0 } un ? "@" + un : Str(from, "first_name")) : "";
                _cfg.ChatSessionId = null;
                _pairCode = null;
                reply = Loc("tg.paired") + "\n\n" + Loc("tg.help");
            }
            else
            {
                _pairTriesLeft--;
                if (_pairTriesLeft <= 0) { _pairCode = null; reply = Loc("tg.pairLocked"); }
                else reply = string.Format(Loc("tg.pairWrong"), _pairTriesLeft);
            }
        }
        Save();
        Changed?.Invoke();
        await SendAsync(api, chatId, reply, ct).ConfigureAwait(false);
    }

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a.PadRight(16)[..16]), Encoding.UTF8.GetBytes(b.PadRight(16)[..16])) && a.Length == b.Length;

    /// <summary>A plain message: into the Telegram chat, one turn at a time; the reply comes back.</summary>
    private async Task AskAsync(ITelegramApi api, long chatId, string text, CancellationToken ct)
    {
        if (AskChat == null) { await SendAsync(api, chatId, Loc("tg.noApp"), ct).ConfigureAwait(false); return; }
        var turn = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _turn, turn, null) != null)
        {
            turn.Dispose();
            await SendAsync(api, chatId, Loc("tg.busy"), ct).ConfigureAwait(false);
            return;
        }
        // The turn runs on its own: reading Telegram goes on (a card's buttons, /stop) while it works.
        _ = Task.Run(async () =>
        {
            using var typing = new CancellationTokenSource();
            _ = TypingAsync(api, chatId, typing.Token);
            try
            {
                string? session;
                string project;
                lock (_gate) { session = _cfg.ChatSessionId; project = _cfg.Project; }
                string message = "[Sent from the user's phone over Telegram: they read the reply there, so keep it short and plain — "
                                 + "no tables, no long code.]\n\n" + text;
                var (id, reply, error) = await AskChat(session, message, project, turn.Token).ConfigureAwait(false);
                if (id.Length > 0)
                {
                    lock (_gate) _cfg.ChatSessionId = id;
                    MarkRemote(id);
                    Save();
                }
                typing.Cancel();
                string body = (reply ?? "").Trim();
                if (body.Length == 0) body = Loc("tg.noReply");
                await SendLongAsync(api, chatId, error ? "❌ " + body : body, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { await SafeSendAsync(api, chatId, Loc("tg.stopped")).ConfigureAwait(false); }
            catch (Exception ex) { await SafeSendAsync(api, chatId, "❌ " + ex.Message).ConfigureAwait(false); }
            finally
            {
                typing.Cancel();
                Interlocked.Exchange(ref _turn, null);
                turn.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task TypingAsync(ITelegramApi api, long chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await api.CallAsync("sendChatAction", new Dictionary<string, object?> { ["chat_id"] = chatId, ["action"] = "typing" }, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch { /* only a hint */ }
    }

    private string RoutinesText()
    {
        var all = RoutineService.Current?.All() ?? new List<Routine>();
        if (all.Count == 0) return Loc("tg.noRoutines");
        var sb = new StringBuilder(Loc("tg.routines")).Append('\n');
        foreach (var r in all.Take(30))
            sb.Append(r.Enabled ? "• " : "◦ ").Append(r.Name).Append(" — ").Append(RoutineSchedule.Describe(r, Loc))
              .Append(r.LastStatus.Length > 0 ? $" ({r.LastStatus})" : "").Append("  [").Append(r.Id[..Math.Min(6, r.Id.Length)]).Append("]\n");
        return sb.ToString().TrimEnd();
    }

    private string RunRoutine(string arg)
    {
        if (RoutineService.Current is not { } routines) return Loc("tg.noApp");
        if (arg.Length == 0) return Loc("tg.runHow");
        var r = routines.Find(arg) ?? routines.All().FirstOrDefault(x => x.Id.StartsWith(arg, StringComparison.OrdinalIgnoreCase)
                                                                    || x.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
        if (r == null) return string.Format(Loc("tg.runNone"), arg);
        return string.Format(Loc(routines.RunNow(r.Id) ? "tg.runOk" : "tg.runBusy"), r.Name);
    }

    private string ProjectsText()
    {
        var list = RecentProjects?.Invoke() ?? new List<string>();
        if (list.Count == 0) return Loc("tg.noProjects");
        string now;
        lock (_gate) now = _cfg.Project;
        var sb = new StringBuilder(Loc("tg.projects")).Append('\n');
        for (int i = 0; i < Math.Min(list.Count, 15); i++)
            sb.Append(i + 1).Append(". ").Append(list[i]).Append(string.Equals(list[i], now, StringComparison.OrdinalIgnoreCase) ? "  ✓" : "").Append('\n');
        return sb.ToString().TrimEnd();
    }

    private string UseProject(string arg)
    {
        var list = RecentProjects?.Invoke() ?? new List<string>();
        if (!int.TryParse(arg, out int i) || i < 1 || i > Math.Min(list.Count, 15)) return Loc("tg.useHow");
        lock (_gate) { _cfg.Project = list[i - 1]; _cfg.ChatSessionId = null; }
        Save();
        return string.Format(Loc("tg.useOk"), list[i - 1]);
    }

    /// <summary>The host opened <paramref name="chatId"/> for Telegram's messages (before its first turn runs, so that
    /// turn's cards already come to the phone).</summary>
    public void UseChat(string chatId)
    {
        if (string.IsNullOrEmpty(chatId)) return;
        lock (_gate) { _cfg.ChatSessionId = chatId; _remoteChats.Add(chatId); }
        Save();
    }

    // ─── routines → the phone ───

    /// <summary>Cards in <paramref name="chatId"/> go to the phone (a routine's run, the Telegram chat).</summary>
    public void MarkRemote(string chatId)
    {
        if (string.IsNullOrEmpty(chatId)) return;
        lock (_gate) _remoteChats.Add(chatId);
    }

    private void OnRoutineFinished(Routine r, bool ok, string note, string? chatId)
    {
        long? chat;
        ITelegramApi? api;
        lock (_gate) { chat = _cfg.ChatId; api = _api; if (!_cfg.NotifyRoutines) chat = null; }
        if (chat is not { } id || api == null) return;
        string text = string.Format(Loc(ok ? "tg.routineDone" : "tg.routineFailed"), r.Name, note.Length == 0 ? "—" : note);
        _ = SafeSendAsync(api, id, text);
    }

    // ─── permission cards ───

    private sealed class CardLink
    {
        public required ChatMessage Card { get; init; }
        public long MessageId { get; set; }
        public string Text { get; init; } = "";
        public bool ByPhone { get; set; }
        public PropertyChangedEventHandler? Watch { get; set; }
    }

    /// <summary>
    /// A card was shown (UI thread) in <paramref name="chatId"/>. It goes to the phone when the chat is the Telegram chat
    /// or a routine's run (every chat with ForwardAllCards) and a phone is paired. Returns true when it was sent.
    /// </summary>
    public bool OfferCard(ChatMessage card, string chatId, string chatTitle)
    {
        long? chat;
        ITelegramApi? api;
        bool forward;
        lock (_gate)
        {
            chat = _cfg.ChatId;
            api = _api;
            forward = _cfg.ForwardAllCards || _remoteChats.Contains(chatId) || chatId == _cfg.ChatSessionId;
        }
        if (chat is not { } tgChat || api == null || !forward || card.PermissionAnswered) return false;

        bool phoneMayAnswer = !card.IsPermissionDanger && card.PermissionStrict == null && card.PermissionBrainx == null;
        string what = card.PermissionPlain?.Headline is { Length: > 0 } h
            ? h + (card.PermissionPlain.Note.Length > 0 ? "\n" + card.PermissionPlain.Note : "")
            : card.Content ?? "";
        string text = string.Format(Loc("tg.card"), chatTitle.Length == 0 ? "CluadeX" : chatTitle, Clip(what, 1500))
                      + (phoneMayAnswer ? "" : "\n\n" + Loc("tg.cardPcOnly"));
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var link = new CardLink { Card = card, Text = text };
        lock (_gate) _cards[key] = link;
        // Answered on the PC (or retired by Stop): the phone's message says so, its buttons go.
        // Runs inside ResolvePermission, before the card's own answer: it never throws there.
        link.Watch = (_, e) =>
        {
            try
            {
                if (e.PropertyName != nameof(ChatMessage.PermissionOutcome) || !card.PermissionAnswered) return;
                card.PropertyChanged -= link.Watch;
                bool mine;
                lock (_gate) { mine = link.ByPhone; _cards.Remove(key); }
                if (!mine && link.MessageId != 0)
                    _ = SafeEditAsync(api, tgChat, link.MessageId, link.Text + "\n\n" + string.Format(Loc("tg.cardDone"), OutcomeText(card.PermissionOutcome)));
            }
            catch (Exception ex) { SetError(ex.Message); }
        };
        card.PropertyChanged += link.Watch;

        object? keyboard = phoneMayAnswer
            ? new Dictionary<string, object?>
            {
                ["inline_keyboard"] = new[]
                {
                    new[]
                    {
                        new Dictionary<string, object?> { ["text"] = Loc("tg.allow"), ["callback_data"] = $"c:{key}:1" },
                        new Dictionary<string, object?> { ["text"] = Loc("tg.deny"), ["callback_data"] = $"c:{key}:0" },
                    },
                },
            }
            : null;
        _ = Task.Run(async () =>
        {
            try { link.MessageId = await SendAsync(api, tgChat, text, CancellationToken.None, keyboard).ConfigureAwait(false); }
            catch (Exception ex) { SetError(ex.Message); }
        });
        return true;
    }

    private string OutcomeText(PermissionOutcome o) => o switch
    {
        PermissionOutcome.Allowed => Loc("tg.outcome.allowed"),
        PermissionOutcome.Denied => Loc("tg.outcome.denied"),
        _ => Loc("tg.outcome.cancelled"),
    };

    private async Task OnCallbackAsync(ITelegramApi api, JsonElement q, CancellationToken ct)
    {
        string qid = Str(q, "id");
        long from = q.TryGetProperty("from", out var f) ? Long(f, "id") : 0;
        long? paired;
        lock (_gate) paired = _cfg.ChatId;
        string data = Str(q, "data");
        var parts = data.Split(':');
        string answer;
        if (paired == null || from != paired || parts.Length != 3 || parts[0] != "c") answer = "";
        else
        {
            CardLink? link;
            lock (_gate) _cards.TryGetValue(parts[1], out link);
            if (link == null || link.Card.PermissionAnswered) answer = Loc("tg.cardGone");
            else
            {
                bool allow = parts[2] == "1";
                lock (_gate) link.ByPhone = true;
                bool done = false;
                await OnUi(() => done = link.Card.ResolvePermission(allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied)).ConfigureAwait(false);
                if (!done) { lock (_gate) link.ByPhone = false; answer = Loc("tg.cardGone"); }
                else
                {
                    answer = Loc(allow ? "tg.cardAllowed" : "tg.cardDenied");
                    if (paired is { } chat && link.MessageId != 0)
                        await SafeEditAsync(api, chat, link.MessageId, link.Text + "\n\n" + answer).ConfigureAwait(false);
                }
            }
        }
        try
        {
            await api.CallAsync("answerCallbackQuery", new Dictionary<string, object?> { ["callback_query_id"] = qid, ["text"] = answer.Length == 0 ? null : answer }, ct)
                .ConfigureAwait(false);
        }
        catch (TelegramApiException) { /* a late answer: Telegram forgot the button press already */ }
    }

    // ─── sending ───

    /// <summary>Send plain text (clipped to <see cref="MaxText"/>); the new message's id.</summary>
    private static async Task<long> SendAsync(ITelegramApi api, long chatId, string text, CancellationToken ct, object? keyboard = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = Clip(text, MaxText),
            ["disable_web_page_preview"] = true,
            ["reply_markup"] = keyboard,
        };
        var sent = await api.CallAsync("sendMessage", payload, ct).ConfigureAwait(false);
        return Long(sent, "message_id");
    }

    /// <summary>A long reply in up to three messages; the rest is in CluadeX.</summary>
    private async Task SendLongAsync(ITelegramApi api, long chatId, string text, CancellationToken ct)
    {
        for (int part = 0; part < 3 && text.Length > 0; part++)
        {
            if (text.Length <= MaxText || part == 2)
            {
                await SendAsync(api, chatId, text.Length > MaxText ? text[..(MaxText - 80)] + "\n…" + Loc("tg.cut") : text, ct).ConfigureAwait(false);
                return;
            }
            int cut = text.LastIndexOf('\n', MaxText - 1);
            if (cut < MaxText / 2) cut = MaxText;
            await SendAsync(api, chatId, text[..cut], ct).ConfigureAwait(false);
            text = text[cut..].TrimStart('\n');
        }
    }

    private async Task SafeSendAsync(ITelegramApi api, long chatId, string text)
    {
        try { await SendAsync(api, chatId, text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { SetError(ex.Message); }
    }

    private async Task SafeEditAsync(ITelegramApi api, long chatId, long messageId, string text)
    {
        try
        {
            await api.CallAsync("editMessageText", new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
                ["text"] = Clip(text, MaxText),
                ["disable_web_page_preview"] = true,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { SetError(ex.Message); }
    }

    // ─── small helpers ───

    private static string Clip(string? s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;
}
