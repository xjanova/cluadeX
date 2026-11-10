using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>The five places the activity card's scene lights up: the model thinking or writing its answer, looking
/// around (reading, searching, the web), changing files, running something, and its team of sub-agents.</summary>
public enum ActivityStation { Think, Explore, Edit, Run, Team }

/// <summary>How the turn stands as a whole: nothing yet, working, waiting on the owner, or how it ended.</summary>
public enum ActivityMood { Idle, Live, Waiting, Done, Failed, Stopped }

/// <summary>One finished step in the card's feed (newest first). <see cref="Ok"/> null = a note, not a tool.</summary>
public sealed record ActivityEntry(DateTime At, ActivityStation Station, string Glyph, string Verb, string Target, bool? Ok);

/// <summary>A sub-agent orbiting the core: its id, its name, and "running" / "done" / "failed" / "stopped".</summary>
public sealed class ActivitySatellite
{
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    public string State { get; set; } = "running";
    /// <summary>Its place on the orbit, 0..1, fixed when it joins so the others do not jump.</summary>
    public double Slot { get; init; }
    public bool IsRunning => State == "running";
}

/// <summary>
/// The live picture behind the chat's activity card (owner 2026-11-10: "ทำ การ์ดอนิเมชั่นเท่ห์ๆ ในหน้าต่างแชทเพิ่ม เกี่ยวกับ
/// การสั่งงานการทำงานของเอไอให้เห็นเป็นการจำลองการทำงาน … ว่ากำลังทำอะไร"). One per chat, fed by the run's REAL events —
/// the turn beginning, thinking, the answer streaming, a tool starting and coming back, sub-agents starting and ending,
/// a question or permission waiting on the owner, the turn's end — never by a timer. Pure (no WPF): the view draws
/// what it says, and the tests feed it events and read it back. UI thread only.
/// </summary>
public sealed class ActivityTrack
{
    public const int FeedSize = 4;
    public const int MaxSatellites = 8;
    /// <summary>How long a failed tool keeps its station flashing red.</summary>
    public static readonly TimeSpan ErrorFlash = TimeSpan.FromSeconds(1.6);

    private readonly List<ActivityEntry> _feed = new();
    private readonly List<ActivitySatellite> _sats = new();
    private readonly bool[] _visited = new bool[5];
    private readonly HashSet<string> _editedFiles = new(StringComparer.OrdinalIgnoreCase);
    private int _satsJoined;

    public ActivityMood Mood { get; private set; } = ActivityMood.Idle;
    public ActivityStation Station { get; private set; } = ActivityStation.Think;
    /// <summary>The model is writing its answer (the Think station, with its own headline).</summary>
    public bool Writing { get; private set; }
    /// <summary>The running step in the chat's own words ("Reading file: Views/ChatView.xaml"), "" between steps.</summary>
    public string Detail { get; private set; } = "";
    /// <summary>The tool running now (null between tools).</summary>
    public string? Tool { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public int Reads { get; private set; }
    public int Edits { get; private set; }
    public int Commands { get; private set; }
    public int Agents { get; private set; }
    public int Failures { get; private set; }
    public int Steps { get; private set; }
    public int FilesEdited => _editedFiles.Count;
    /// <summary>The station of the last failed tool, flashing until <see cref="ErrorUntil"/>.</summary>
    public ActivityStation ErrorStation { get; private set; }
    public DateTime ErrorUntil { get; private set; }
    /// <summary>Bumped on every change: the view redraws its text when this moves.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<ActivityEntry> Feed => _feed;
    public IReadOnlyList<ActivitySatellite> Satellites => _sats;
    public int RunningAgents => _sats.Count(s => s.IsRunning);
    /// <summary>Whether a station was used in this turn (the scene keeps those lit faintly).</summary>
    public bool Visited(ActivityStation s) => _visited[(int)s];
    /// <summary>The scene moves: something is in flight now.</summary>
    public bool IsLive => Mood is ActivityMood.Live or ActivityMood.Waiting;
    public bool IsFlashing(DateTime now) => now < ErrorUntil;

    public TimeSpan Elapsed(DateTime now)
    {
        if (StartedAt == default) return TimeSpan.Zero;
        var d = (EndedAt ?? now) - StartedAt;
        return d > TimeSpan.Zero ? d : TimeSpan.Zero;
    }

    // ─── events ───

    /// <summary>A turn began: the last turn's picture is cleared, the model reads the request.</summary>
    public void Begin(DateTime now)
    {
        _feed.Clear();
        _sats.Clear();
        Array.Clear(_visited);
        _editedFiles.Clear();
        _satsJoined = 0;
        Reads = Edits = Commands = Agents = Failures = Steps = 0;
        ErrorUntil = default;
        StartedAt = now;
        EndedAt = null;
        Mood = ActivityMood.Live;
        Tool = null;
        Detail = "";
        Go(ActivityStation.Think, writing: false);
    }

    /// <summary>The model thinks (a thinking chunk, or a tool came back and it decides what next).</summary>
    public void Thinking()
    {
        if (!IsLive || Tool != null) return;
        if (Station == ActivityStation.Think && !Writing) return;
        Go(ActivityStation.Think, writing: false);
    }

    /// <summary>The model streams its answer text.</summary>
    public void WritingAnswer()
    {
        if (!IsLive || Tool != null || (Station == ActivityStation.Think && Writing)) return;
        Go(ActivityStation.Think, writing: true);
    }

    /// <summary>A tool is about to run. <paramref name="status"/> is the line the chat shows for it. "agent" is the
    /// harness's own notice (an auto-verify, a compaction): it changes the words, not the station.</summary>
    public void ToolStarting(string tool, string status)
    {
        if (!IsLive) return;
        status = (status ?? "").Trim();
        if (tool == "agent")
        {
            Detail = status;
            Version++;
            return;
        }
        Tool = tool;
        Detail = status;
        if (StationOf(tool) is { } st) Go(st, writing: false);
        else Version++;
    }

    /// <summary>A tool came back. <paramref name="verb"/> and <paramref name="target"/> are what the feed says ("Read",
    /// "Views/ChatView.xaml"); <paramref name="file"/> is the file an edit changed.</summary>
    public void ToolEnded(string tool, bool ok, string verb, string target, string? file, DateTime now)
    {
        if (Mood == ActivityMood.Idle) return;
        var st = StationOf(tool) ?? Station;
        Steps++;
        switch (st)
        {
            case ActivityStation.Explore: Reads++; break;
            case ActivityStation.Edit:
                if (ok) { Edits++; if (!string.IsNullOrWhiteSpace(file)) _editedFiles.Add(file); }
                break;
            case ActivityStation.Run: Commands++; break;
        }
        if (!ok)
        {
            Failures++;
            ErrorStation = st;
            ErrorUntil = now + ErrorFlash;
        }
        AddFeed(new ActivityEntry(now, st, ToolLook.Of(tool).Glyph, verb, target, ok));
        if (string.Equals(Tool, tool, StringComparison.OrdinalIgnoreCase)) Tool = null;
        if (Tool == null && IsLive)
        {
            Detail = "";
            // Back to thinking — unless its team still works: then the team is what runs.
            Go(RunningAgents > 0 ? ActivityStation.Team : ActivityStation.Think, writing: false);
        }
        else Version++;
    }

    /// <summary>A sub-agent's event (<see cref="CodeAgentService.SubAgentEvent.Phase"/>: start / tool / result / done /
    /// failed / stopped).</summary>
    public void SubAgent(string id, string label, string phase, DateTime now)
    {
        if (Mood == ActivityMood.Idle) return;
        var sat = _sats.FirstOrDefault(s => s.Id == id);
        if (sat == null)
        {
            if (phase != "start" && phase != "tool") return;
            if (_sats.Count >= MaxSatellites)
            {
                // the oldest finished one makes room; a full orbit of running agents keeps counting, unseen
                int old = _sats.FindIndex(s => !s.IsRunning);
                if (old < 0) { Agents++; Version++; return; }
                _sats.RemoveAt(old);
            }
            // golden-angle slots: each new one lands in the widest gap without moving the others
            sat = new ActivitySatellite { Id = id, Label = label ?? "", Slot = (_satsJoined++ * 0.381966) % 1.0 };
            _sats.Add(sat);
            Agents++;
            AddFeed(new ActivityEntry(now, ActivityStation.Team, ToolLook.Of("agent_spawn").Glyph, "+", sat.Label, null));
        }
        if (!string.IsNullOrEmpty(label)) sat.Label = label;
        switch (phase)
        {
            case "done": sat.State = "done"; break;
            case "failed": sat.State = "failed"; Failures++; ErrorStation = ActivityStation.Team; ErrorUntil = now + ErrorFlash; break;
            case "stopped": sat.State = "stopped"; break;
            default: sat.State = "running"; break;
        }
        if (IsLive && Tool == null) Go(RunningAgents > 0 ? ActivityStation.Team : ActivityStation.Think, writing: false);
        else if (IsLive && RunningAgents > 0 && Station != ActivityStation.Team && IsSpawnTool(Tool)) Go(ActivityStation.Team, writing: false);
        else Version++;
    }

    /// <summary>A question or a permission waits on the owner (true), or was answered (false).</summary>
    public void SetWaiting(bool waiting)
    {
        if (waiting && Mood == ActivityMood.Live) { Mood = ActivityMood.Waiting; Version++; }
        else if (!waiting && Mood == ActivityMood.Waiting) { Mood = ActivityMood.Live; Version++; }
    }

    /// <summary>The turn ended. A stop wins over a failure (the owner stopped it), a failure over done.</summary>
    public void End(bool stopped, bool failed, DateTime now)
    {
        if (Mood == ActivityMood.Idle && StartedAt == default) return;
        Mood = stopped ? ActivityMood.Stopped : failed ? ActivityMood.Failed : ActivityMood.Done;
        EndedAt = now;
        Tool = null;
        Detail = "";
        Writing = false;
        // a sub-agent killed with its run raises no "done": it stops with it
        foreach (var s in _sats.Where(s => s.IsRunning)) s.State = stopped || failed ? "stopped" : "done";
        Version++;
    }

    // ─── helpers ───

    private void Go(ActivityStation st, bool writing)
    {
        Station = st;
        Writing = writing;
        _visited[(int)st] = true;
        Version++;
    }

    private void AddFeed(ActivityEntry e)
    {
        _feed.Insert(0, e);
        if (_feed.Count > FeedSize) _feed.RemoveRange(FeedSize, _feed.Count - FeedSize);
    }

    private static bool IsSpawnTool(string? tool) => tool is "agent_spawn" or "subagent_invoke" or "workflow";

    /// <summary>Which station a tool lights. ask_user lights none (the owner is asked: the mood says so); null = keep
    /// the station it is at.</summary>
    public static ActivityStation? StationOf(string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return null;
        if (IsSpawnTool(tool)) return ActivityStation.Team;
        return ToolLook.Of(tool).VerbKey switch
        {
            "tool.verb.read" or "tool.verb.list" or "tool.verb.search" or "tool.verb.web" or "tool.verb.browser"
                or "tool.verb.memory" or "tool.verb.mcp" => ActivityStation.Explore,
            "tool.verb.write" or "tool.verb.edit" => ActivityStation.Edit,
            "tool.verb.run" or "tool.verb.build" or "tool.verb.test" or "tool.verb.background" or "tool.verb.git" => ActivityStation.Run,
            "tool.verb.agent" => ActivityStation.Team,
            "tool.verb.plan" or "tool.verb.skill" => ActivityStation.Think,
            "tool.verb.ask" => null,
            _ => ActivityStation.Run,
        };
    }

    /// <summary>The headline's localization key for how the turn stands now.</summary>
    public string HeadlineKey => Mood switch
    {
        ActivityMood.Idle => "activity.head.idle",
        ActivityMood.Waiting => "activity.head.waiting",
        ActivityMood.Done => "activity.head.done",
        ActivityMood.Failed => "activity.head.failed",
        ActivityMood.Stopped => "activity.head.stopped",
        _ => Station switch
        {
            ActivityStation.Think => Writing ? "activity.head.writing" : Steps == 0 ? "activity.head.reading" : "activity.head.thinking",
            ActivityStation.Explore => "activity.head.explore",
            ActivityStation.Edit => "activity.head.edit",
            ActivityStation.Run => "activity.head.run",
            _ => "activity.head.team",
        },
    };
}
