using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// The learning loop's chat side (owner 2026-11-10: "Instinct ใช้งานได้จริงไหม"). Instincts used to be extracted only
/// when New chat was pressed on a finished chat — never on a switch, never when a chat just sat finished, never for a
/// split or pipe chat — and nothing ever asked the owner whether a lesson was right. Now:
///   - a chat is read when it is left (New chat, another chat opened) and when it has sat idle
///     <see cref="LearnIdleDelay"/> after a turn ended — any chat, on screen or not (split chats, pipe tasks);
///     SessionMemoryService reads each chat at most once per few new messages;
///   - what it learned and the owner has not voted on comes back as a small card in the chat on screen —
///     "จำสิ่งนี้ไว้ใช้ครั้งหน้าไหม" with ✓ / ✗ per instinct. ✓ = Accept (into the prompt now), ✗ = Reject (never).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>How long a chat sits after its turn ended before it is read. Tests shorten it.</summary>
    internal static TimeSpan LearnIdleDelay = TimeSpan.FromMinutes(4);

    /// <summary>Instincts asked about on one card at most.</summary>
    private const int MaxInstinctsPerCard = 3;

    private readonly Dictionary<string, DispatcherTimer> _learnTimers = new();
    private readonly List<(string? FromSession, List<string> Ids)> _pendingInstinctCards = new();

    private void InitLearning()
    {
        ChatMessage.InstinctCardFor = m => InstinctService.Current is { } store
            ? InstinctCardView.FromRow(m, store, Loc, LocF) : null;
        if (_sessionMemoryService != null) _sessionMemoryService.InstinctsLearned += OnInstinctsLearned;
    }

    /// <summary>Read <paramref name="s"/> for memories and instincts (best-effort, in the background). Not while a run
    /// works in it — its end schedules the read.</summary>
    private void LearnFrom(ChatSession? s, IReadOnlyList<ChatMessage>? rows)
    {
        if (s == null || rows == null || rows.Count == 0 || _sessionMemoryService == null) return;
        if (RunOf(s) != null) return;
        string? folder = string.IsNullOrWhiteSpace(s.ProjectPath) ? null : s.ProjectPath;
        try { _sessionMemoryService.ExtractInBackground(s.Id, rows, folder); }
        catch { /* learning never gets in the way of the chat */ }
    }

    /// <summary>A turn ended in <paramref name="s"/>: read it once it has been quiet for <see cref="LearnIdleDelay"/>.
    /// A new turn there cancels the wait (<see cref="CancelLearning"/>).</summary>
    private void ScheduleLearning(ChatSession s, IReadOnlyList<ChatMessage> rows)
    {
        if (_sessionMemoryService == null) return;
        CancelLearning(s);
        var timer = new DispatcherTimer { Interval = LearnIdleDelay };
        string id = s.Id;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_learnTimers.TryGetValue(id, out var mine) && ReferenceEquals(mine, timer)) _learnTimers.Remove(id);
            // A deleted chat is not read; the one on screen is read from what the screen holds now.
            if (!Sessions.Any(x => x.Id == id)) return;
            LearnFrom(s, ReferenceEquals(s, CurrentSession) || CurrentSession?.Id == id ? Messages : rows);
        };
        _learnTimers[id] = timer;
        timer.Start();
    }

    private void CancelLearning(ChatSession s)
    {
        if (_learnTimers.Remove(s.Id, out var t)) t.Stop();
    }

    // ─── The card ────────────────────────────────────────────────────

    /// <summary>A pass recorded instincts (pool thread). The un-voted ones not asked about lately go on a card.</summary>
    private void OnInstinctsLearned(string? sessionId, IReadOnlyList<InstinctService.SightingResult> results)
    {
        var store = InstinctService.Current;
        if (store == null || InstinctService.PausedForBrain) return;   // BrainX is the memory: nothing to ask
        var ids = results
            .Where(r => r.Counted && !r.Instinct.IsVoted)
            .Select(r => r.Instinct.Id)
            .Distinct()
            .ToList();
        ids = store.TakeToAsk(ids, MaxInstinctsPerCard);
        if (ids.Count == 0) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pendingInstinctCards.Add((sessionId, ids));
            FlushInstinctCards();
        });
    }

    /// <summary>Put waiting cards into the chat on screen — only one with rows and no run working (a card in an empty
    /// new chat would make it a saved chat; one in a streaming turn would land in the middle of it).</summary>
    private void FlushInstinctCards()
    {
        if (_pendingInstinctCards.Count == 0 || CurrentSession == null || Messages.Count == 0 || RunOf(CurrentSession) != null) return;
        var store = InstinctService.Current;
        if (store == null || InstinctService.PausedForBrain) { _pendingInstinctCards.Clear(); return; }
        bool added = false;
        foreach (var (from, ids) in _pendingInstinctCards.ToList())
        {
            _pendingInstinctCards.Remove((from, ids));
            var live = ids.Select(store.GetById).Where(i => i != null && !i.IsVoted).Cast<Instinct>().ToList();
            if (live.Count == 0) continue;
            string? fromTitle = from != null && from != CurrentSession.Id
                ? Sessions.FirstOrDefault(s => s.Id == from)?.Title : null;
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                ToolName = ChatMessage.InstinctTag,
                ToolOutput = InstinctCardView.ToJson(live.Select(i => i.Id).ToList(), fromTitle),
                // the row's text: what a copy, an export or a build without the card shows
                Content = "🧠 " + Loc("instinct.card.title") + "\n" + string.Join("\n", live.Select(i => "• " + i.Pattern)),
            });
            added = true;
        }
        if (!added) return;
        _isDirty = true;
        AutoSave(background: true);
    }
}

/// <summary>The "remember this next time?" card: one row per instinct, ✓ / ✗ each. Its state comes from the store,
/// so a reloaded chat shows what was decided (here or on the Instincts page).</summary>
public sealed class InstinctCardView
{
    public string FromText { get; }
    public bool HasFrom => FromText.Length > 0;
    public IReadOnlyList<InstinctCardRow> Rows { get; }

    private InstinctCardView(string fromText, IReadOnlyList<InstinctCardRow> rows)
    {
        FromText = fromText;
        Rows = rows;
    }

    public static string ToJson(IReadOnlyList<string> ids, string? fromTitle) =>
        JsonSerializer.Serialize(new { ids, from = fromTitle });

    /// <summary>The card for a row (null when the row is not one, or its data is unreadable).</summary>
    public static InstinctCardView? FromRow(ChatMessage m, InstinctService store, Func<string, string> loc, Func<string, object[], string> locF)
    {
        if (m.ToolName != ChatMessage.InstinctTag || string.IsNullOrWhiteSpace(m.ToolOutput)) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.ToolOutput);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array) return null;
            var rows = idsEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => new InstinctCardRow(e.GetString()!, store, loc, locF))
                .ToList();
            if (rows.Count == 0) return null;
            string from = root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } t
                ? locF("instinct.card.from", new object[] { t }) : "";
            return new InstinctCardView(from, rows);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>One instinct on the card.</summary>
public sealed class InstinctCardRow : ViewModelBase
{
    private readonly InstinctService _store;
    private readonly Func<string, string> _loc;
    public string Id { get; }
    public string Pattern { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public string ScopeText { get; }

    public enum CardState { Open, Accepted, Rejected, Gone }
    private CardState _state;
    public CardState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(StateText));
        }
    }
    public bool IsOpen => _state == CardState.Open;
    public string StateText => _state switch
    {
        CardState.Accepted => " · " + _loc("instinct.card.accepted"),
        CardState.Rejected => " · " + _loc("instinct.card.rejected"),
        CardState.Gone => " · " + _loc("instinct.card.gone"),
        _ => "",
    };

    public ICommand AcceptCommand { get; }
    public ICommand RejectCommand { get; }

    /// <summary>The last vote failed to save (a locked file): the row stays open to try again.</summary>
    public string? Error { get; private set; }

    public InstinctCardRow(string id, InstinctService store, Func<string, string> loc, Func<string, object[], string> locF)
    {
        _store = store;
        _loc = loc;
        Id = id;
        var i = store.GetById(id);
        Pattern = i?.Pattern ?? "";
        string trigger = i?.Trigger?.Trim() ?? "", action = i?.Action?.Trim() ?? "";
        Detail = trigger.Length > 0 && action.Length > 0 ? trigger + " → " + action : trigger + action;
        ScopeText = i?.ProjectKey != null
            ? locF("instinct.card.scopeProject", new object[] { i.ProjectName ?? InstinctService.ProjectNameOf(i.ProjectKey) ?? "" })
            : loc("instinct.card.scopeGlobal");
        _state = i == null ? CardState.Gone
            : i.IsRejected ? CardState.Rejected
            : i.AcceptCount > 0 ? CardState.Accepted
            : CardState.Open;
        AcceptCommand = new RelayCommand(() => Vote(true));
        RejectCommand = new RelayCommand(() => Vote(false));
    }

    /// <summary>A click: once (a double click, or the other button after it, does nothing).</summary>
    internal void Vote(bool yes)
    {
        if (!IsOpen) return;
        if (_store.GetById(Id) == null) { State = CardState.Gone; return; }
        try
        {
            if (yes) _store.Accept(Id); else _store.Reject(Id);
            Error = null;
            State = yes ? CardState.Accepted : CardState.Rejected;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;   // the store could not be written: the buttons stay, the next click tries again
        }
    }
}
