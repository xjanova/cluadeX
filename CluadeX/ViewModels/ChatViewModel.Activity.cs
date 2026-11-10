using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>One row of the activity card's feed: when, what, and whether it worked.</summary>
public sealed record ActivityFeedRow(string Time, string Glyph, string Verb, string Target, string State)
{
    public string Tip => string.IsNullOrEmpty(Target) ? Verb : Verb + " " + Target;
}

/// <summary>
/// What the activity card binds to: the chat on screen's <see cref="ActivityTrack"/> in words — the headline, the step
/// running now, the counters, the last steps, the time. <see cref="Refresh"/> re-reads the track after an event;
/// <see cref="Tick"/> only moves the clock (the card calls it once a second while its scene moves).
/// </summary>
public sealed class ActivityCardModel : ViewModelBase
{
    private static string Loc(string key) => LocalizedResources.Instance[key];

    private ActivityTrack _track = new();
    private int _seenVersion = -1;
    private string _headline = "", _detail = "", _elapsed = "", _mood = "idle", _station = "think", _stationName = "";
    private string _reads = "0", _edits = "0", _commands = "0", _agents = "0";
    private bool _hasDetail;

    public ActivityTrack Track { get => _track; private set => SetProperty(ref _track, value); }
    /// <summary>The track's <see cref="ActivityTrack.Version"/> as last shown: the scene redraws its still parts on it.</summary>
    public int Version { get => _seenVersion; private set => SetProperty(ref _seenVersion, value); }
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool HasDetail { get => _hasDetail; private set => SetProperty(ref _hasDetail, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    /// <summary>idle / live / waiting / done / failed / stopped — the card's colour triggers read it.</summary>
    public string Mood { get => _mood; private set => SetProperty(ref _mood, value); }
    /// <summary>think / explore / edit / run / team.</summary>
    public string Station { get => _station; private set => SetProperty(ref _station, value); }
    public string StationName { get => _stationName; private set => SetProperty(ref _stationName, value); }
    public string Reads { get => _reads; private set => SetProperty(ref _reads, value); }
    public string Edits { get => _edits; private set => SetProperty(ref _edits, value); }
    public string Commands { get => _commands; private set => SetProperty(ref _commands, value); }
    public string Agents { get => _agents; private set => SetProperty(ref _agents, value); }
    public ObservableCollection<ActivityFeedRow> Feed { get; } = new();
    private ActivityEntry? _feedTop;
    private int _feedCount = -1;

    /// <summary>Show another chat's track (switching chats).</summary>
    public void Show(ActivityTrack track)
    {
        if (!ReferenceEquals(track, _track)) { Track = track; _seenVersion = -1; _feedTop = null; _feedCount = -1; }
        Refresh(DateTime.Now);
    }

    public void Refresh(DateTime now)
    {
        var t = _track;
        Mood = t.Mood.ToString().ToLowerInvariant();
        Station = t.Station.ToString().ToLowerInvariant();
        StationName = Loc("activity.station." + Station);
        Headline = Loc(t.HeadlineKey);
        Detail = t.Detail;
        HasDetail = t.Detail.Length > 0;
        Reads = t.Reads.ToString();
        Edits = t.Edits.ToString();
        Commands = t.Commands.ToString();
        Agents = t.Agents.ToString();
        if (!ReferenceEquals(_feedTop, t.Feed.FirstOrDefault()) || _feedCount != t.Feed.Count)
        {
            _feedTop = t.Feed.FirstOrDefault();
            _feedCount = t.Feed.Count;
            Feed.Clear();
            foreach (var e in t.Feed)
                Feed.Add(new ActivityFeedRow(e.At.ToString("HH:mm:ss"), e.Glyph, e.Verb, e.Target,
                                             e.Ok switch { true => "ok", false => "fail", null => "note" }));
        }
        Tick(now);
        Version = t.Version;
    }

    /// <summary>The clock only (mm:ss, h:mm:ss past an hour; "" before the first turn).</summary>
    public void Tick(DateTime now)
    {
        if (_track.StartedAt == default) { Elapsed = ""; return; }
        var d = _track.Elapsed(now);
        Elapsed = d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";
    }
}

/// <summary>
/// The chat's activity card (owner 2026-11-10: "ทำ การ์ดอนิเมชั่นเท่ห์ๆ ในหน้าต่างแชทเพิ่ม … ว่ากำลังทำอะไรตามการ์ดที่กำลังทำ …
/// ปิดได้"): at the top of the right-hand panel, a live scene of what the run does now. Each chat keeps its own
/// <see cref="ActivityTrack"/>, fed from the same run events the chat rows come from; the card shows the one on screen.
/// Closed with its ✕, it is off until the "+" menu turns it on again (Settings ▸ ChatActivityCard).
/// </summary>
public partial class ChatViewModel
{
    private readonly Dictionary<string, ActivityTrack> _activityTracks = new(StringComparer.Ordinal);
    private bool _showActivityCard = true;
    private ObservableCollection<ChatMessage>? _activityRows;
    private bool _activityHadRows;

    public ActivityCardModel Activity { get; } = new();
    public ICommand CloseActivityCardCommand { get; private set; } = null!;

    /// <summary>The card is on (the "+" menu, kept in the settings).</summary>
    public bool ShowActivityCard
    {
        get => _showActivityCard;
        set
        {
            if (!SetProperty(ref _showActivityCard, value)) return;
            _settingsService.UpdateSettings(s => s.ChatActivityCard = value);
            OnPropertyChanged(nameof(ActivityCardVisible));
            OnPropertyChanged(nameof(SidePanelVisible));
        }
    }

    /// <summary>On, and the chat on screen has begun (the new-chat page has no card). It counts as something the right
    /// panel shows, so the panel stays put while a run works instead of opening with its first picture.</summary>
    public bool ActivityCardVisible => _showActivityCard && _activityHadRows;

    private void InitActivity()
    {
        _showActivityCard = _settingsService.Settings.ChatActivityCard;
        CloseActivityCardCommand = new RelayCommand(() => ShowActivityCard = false);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Messages)) WatchActivityRows();
            if (e.PropertyName is nameof(Messages) or nameof(CurrentSession)) ShowActivityFor(CurrentSession);
        };
        // a language switch: the headline and the station names are words of the old language until redrawn
        LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(() => Activity.Refresh(DateTime.Now));
        WatchActivityRows();
        ShowActivityFor(CurrentSession);
    }

    private void WatchActivityRows()
    {
        if (ReferenceEquals(_activityRows, Messages)) { SyncActivityRows(); return; }
        if (_activityRows != null) _activityRows.CollectionChanged -= OnActivityRowsChanged;
        _activityRows = Messages;
        _activityRows.CollectionChanged += OnActivityRowsChanged;
        SyncActivityRows();
    }

    private void OnActivityRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncActivityRows();

    private void SyncActivityRows()
    {
        bool has = _activityRows is { Count: > 0 };
        if (has == _activityHadRows) return;
        _activityHadRows = has;
        OnPropertyChanged(nameof(ActivityCardVisible));
        OnPropertyChanged(nameof(SidePanelVisible));
    }

    /// <summary>The chat's track (made on first use).</summary>
    internal ActivityTrack ActivityOf(string chatId)
    {
        if (!_activityTracks.TryGetValue(chatId, out var t)) _activityTracks[chatId] = t = new ActivityTrack();
        return t;
    }

    private void ShowActivityFor(ChatSession? s) => Activity.Show(s == null ? new ActivityTrack() : ActivityOf(s.Id));

    /// <summary>Apply an event to a chat's track on the UI thread (events come from the run's own thread) and redraw the
    /// card when that chat is on screen.</summary>
    private void OnActivity(string? chatId, Action<ActivityTrack> change)
    {
        if (string.IsNullOrEmpty(chatId)) return;
        void Apply()
        {
            try
            {
                var t = ActivityOf(chatId);
                change(t);
                // Show, not just Refresh: the chat on screen can change without a CurrentSession notice (a load that
                // sets it in place), and the card must never keep drawing another chat's track.
                if (CurrentSession?.Id == chatId) Activity.Show(t);
            }
            catch (Exception ex) { _debugLog?.Warn("Activity", "activity card update failed", ex); }   // never the run's problem
        }
        var d = App.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Apply();
        else d.BeginInvoke(Apply);
    }

    private void ActivityBegan(ChatSession s) => OnActivity(s.Id, t => t.Begin(DateTime.Now));
    private void ActivityEnded(ChatSession s, bool stopped, bool failed) => OnActivity(s.Id, t => t.End(stopped, failed, DateTime.Now));
    private void ActivityWaiting(string chatId, bool waiting) => OnActivity(chatId, t => t.SetWaiting(waiting));
    private void NoteActivityThinking(string chatId) => OnActivity(chatId, t => t.Thinking());
    private void NoteActivityWriting(string chatId) => OnActivity(chatId, t => t.WritingAnswer());
    private void NoteActivityTool(string chatId, string tool, string status) => OnActivity(chatId, t => t.ToolStarting(tool, status));

    private void NoteActivityDone(string? chatId, ToolResult r)
    {
        if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(r.ToolName)) return;
        string tool = r.ToolName;
        var (_, verbKey) = ToolLook.Of(tool);
        string verb = verbKey == "tool.verb.mcp" && ToolLook.McpServer(tool) is { } srv ? srv : Loc(verbKey);
        string target = ToolTargetOf(tool, r.Arguments, WorkingDirectory);
        if (target == tool) target = "";
        bool ok = r.Success;
        string? file = r.FilePath;
        OnActivity(chatId, t => t.ToolEnded(tool, ok, verb, target, file, DateTime.Now));
    }

    private void NoteActivityAgent(string? chatId, CodeAgentService.SubAgentEvent e) =>
        OnActivity(chatId, t => t.SubAgent((e.WorkflowId != null ? "wf-" + e.WorkflowId + "-" : "sa-") + e.Id,
                                           e.Label ?? "", e.Phase ?? "", DateTime.Now));
}
