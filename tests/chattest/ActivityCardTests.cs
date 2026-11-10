using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>AC — the chat's activity card (owner 2026-11-10: "ทำ การ์ดอนิเมชั่นเท่ห์ๆ … ให้เห็นเป็นการจำลองการทำงาน … ว่ากำลังทำ
    /// อะไร … ปิดได้"). First the event → state mapping on the track alone; then real turns: the card follows the run's own
    /// events (a tool starting and ending, a failed tool, sub-agents, a question waiting), ends done / failed / stopped,
    /// belongs to its chat when the owner switches, and its ✕ turns it off until the "+" menu turns it on.</summary>
    static async Task TestActivityCard()
    {
        Log("\n## AC — the activity card: event → state, real turns, per chat, close / reopen");
        var now = new DateTime(2026, 11, 10, 9, 0, 0);

        // AC1 — the track alone
        var t = new ActivityTrack();
        Check("AC1a a new track is idle", t.Mood == ActivityMood.Idle && !t.IsLive && t.HeadlineKey == "activity.head.idle", t.HeadlineKey);
        t.ToolStarting("read_file", "Reading a.txt");
        t.ToolEnded("read_file", true, "Read", "a.txt", null, now);
        Check("AC1b events before a turn change nothing", t.Mood == ActivityMood.Idle && t.Reads == 0 && t.Feed.Count == 0, $"{t.Reads}");
        t.Begin(now);
        Check("AC1c a turn begins: live, at Think, reading the request", t.IsLive && t.Station == ActivityStation.Think && t.HeadlineKey == "activity.head.reading", t.HeadlineKey);
        t.WritingAnswer();
        Check("AC1d its answer streams: writing", t.Writing && t.HeadlineKey == "activity.head.writing", t.HeadlineKey);
        int v = t.Version;
        t.WritingAnswer();
        Check("AC1e another token changes nothing (no redraw per token)", t.Version == v, $"{v}→{t.Version}");
        t.ToolStarting("read_file", "Reading Views/ChatView.xaml");
        Check("AC1f read_file: Explore, the chat's own words as the detail", t.Station == ActivityStation.Explore && !t.Writing && t.Detail == "Reading Views/ChatView.xaml" && t.HeadlineKey == "activity.head.explore", $"{t.Station} '{t.Detail}'");
        t.ToolEnded("read_file", true, "Read", "Views/ChatView.xaml", null, now.AddSeconds(2));
        Check("AC1g it came back: counted, in the feed, thinking again", t.Reads == 1 && t.Feed.Count == 1 && t.Feed[0].Ok == true
              && t.Station == ActivityStation.Think && t.HeadlineKey == "activity.head.thinking" && t.Detail == "", $"{t.Reads} {t.Station} {t.HeadlineKey}");
        foreach (var (tool, st) in new[] { ("grep", ActivityStation.Explore), ("web_fetch", ActivityStation.Explore), ("mcp__brainx__brain_search", ActivityStation.Explore),
                                           ("edit_file", ActivityStation.Edit), ("write_file", ActivityStation.Edit), ("multi_edit", ActivityStation.Edit),
                                           ("run_command", ActivityStation.Run), ("run_build", ActivityStation.Run), ("run_tests", ActivityStation.Run), ("git_status", ActivityStation.Run),
                                           ("agent_spawn", ActivityStation.Team), ("todo_write", ActivityStation.Think) })
            Check($"AC1h {tool} lights {st}", ActivityTrack.StationOf(tool) == st, $"{ActivityTrack.StationOf(tool)}");
        Check("AC1i ask_user lights no station (the mood says it)", ActivityTrack.StationOf("ask_user") == null);
        t.ToolStarting("agent", "⤴ Auto-verifying the build");
        Check("AC1j a harness notice changes the words only", t.Station == ActivityStation.Think && t.Detail.Contains("Auto-verifying") && t.Tool == null, $"{t.Station} {t.Tool}");
        t.ToolStarting("edit_file", "Editing a.cs");
        t.ToolEnded("edit_file", false, "Edited", "a.cs", "a.cs", now.AddSeconds(3));
        Check("AC1k a failed edit: not counted as an edit, a failure, its station flashes", t.Edits == 0 && t.Failures == 1 && t.ErrorStation == ActivityStation.Edit
              && t.IsFlashing(now.AddSeconds(3.5)) && !t.IsFlashing(now.AddSeconds(5)) && t.Feed[0].Ok == false, $"{t.Edits} {t.Failures}");
        t.ToolStarting("edit_file", "Editing a.cs");
        t.ToolEnded("edit_file", true, "Edited", "a.cs", "a.cs", now.AddSeconds(4));
        t.ToolStarting("edit_file", "Editing a.cs");
        t.ToolEnded("edit_file", true, "Edited", "a.cs", "a.cs", now.AddSeconds(5));
        Check("AC1l two good edits of one file: 2 edits, 1 file", t.Edits == 2 && t.FilesEdited == 1, $"{t.Edits}/{t.FilesEdited}");
        t.ToolStarting("run_command", "Running dotnet build");
        t.ToolEnded("run_command", true, "Ran", "dotnet build", null, now.AddSeconds(6));
        Check("AC1m a command: counted", t.Commands == 1);
        Check("AC1n the feed keeps the last four, newest first", t.Feed.Count == ActivityTrack.FeedSize && t.Feed[0].Target == "dotnet build", string.Join(",", t.Feed.Select(f => f.Target)));

        // sub-agents
        t.ToolStarting("agent_spawn", "Starting 2 sub-agents");
        t.SubAgent("sa-1", "Sub-agent #1", "start", now.AddSeconds(7));
        t.SubAgent("sa-2", "Sub-agent #2", "start", now.AddSeconds(7));
        t.SubAgent("sa-1", "", "tool", now.AddSeconds(8));
        Check("AC1o two sub-agents: two satellites, both running, the Team station", t.Satellites.Count == 2 && t.RunningAgents == 2 && t.Agents == 2 && t.Station == ActivityStation.Team, $"{t.Satellites.Count} {t.RunningAgents} {t.Station}");
        Check("AC1p each keeps its own orbit slot", Math.Abs(t.Satellites[0].Slot - t.Satellites[1].Slot) > 0.2, $"{t.Satellites[0].Slot:0.00} {t.Satellites[1].Slot:0.00}");
        t.ToolEnded("agent_spawn", true, "Sub-agent", "", null, now.AddSeconds(9));
        Check("AC1q the spawn call returned while they work: still the Team", t.Station == ActivityStation.Team && t.HeadlineKey == "activity.head.team", $"{t.Station}");
        t.SubAgent("sa-1", "", "done", now.AddSeconds(10));
        t.SubAgent("sa-2", "", "failed", now.AddSeconds(10));
        Check("AC1r both ended: done / failed, back to Think, a failure counted", t.Satellites[0].State == "done" && t.Satellites[1].State == "failed"
              && t.RunningAgents == 0 && t.Station == ActivityStation.Think && t.Failures == 2, $"{t.Satellites[0].State} {t.Satellites[1].State} {t.Station}");
        t.SubAgent("sa-9", "", "done", now.AddSeconds(10));
        Check("AC1s a 'done' for an agent it never saw start adds nothing", t.Satellites.Count == 2);
        for (int i = 0; i < 12; i++) t.SubAgent("sa-x" + i, "x", "start", now.AddSeconds(11));
        Check("AC1t a big team: at most 8 satellites, every agent counted", t.Satellites.Count == ActivityTrack.MaxSatellites && t.Agents == 14, $"{t.Satellites.Count}/{t.Agents}");

        // waiting and the end
        t.SetWaiting(true);
        Check("AC1u a question waits: waiting, still live", t.Mood == ActivityMood.Waiting && t.IsLive && t.HeadlineKey == "activity.head.waiting");
        t.SetWaiting(false);
        Check("AC1v answered: live again", t.Mood == ActivityMood.Live);
        t.End(stopped: false, failed: false, now.AddSeconds(70));
        Check("AC1w the end: done, the clock stops, running agents end with it", t.Mood == ActivityMood.Done && !t.IsLive && t.Elapsed(now.AddHours(1)) == TimeSpan.FromSeconds(70)
              && t.RunningAgents == 0, $"{t.Mood} {t.Elapsed(now.AddHours(1))}");
        t.SetWaiting(true);
        Check("AC1x a late badge refresh after the end does not wake it", t.Mood == ActivityMood.Done);
        t.Begin(now.AddSeconds(80));
        Check("AC1y the next turn starts clean", t.Feed.Count == 0 && t.Reads + t.Edits + t.Commands + t.Agents + t.Failures == 0 && t.Satellites.Count == 0 && !t.Visited(ActivityStation.Edit));
        t.End(stopped: true, failed: true, now.AddSeconds(81));
        Check("AC1z a stop wins over a failure", t.Mood == ActivityMood.Stopped);

        // the card model's words: Thai differs from English for every key the card shows
        foreach (var key in ActivityKeyNames())
            Check($"AC1loc {key} is Thai and English", LocTh(key) is { Length: > 0 } a && LocEn(key) is { Length: > 0 } b && a != b, $"{LocTh(key)} / {LocEn(key)}");

        // AC2 — a real turn: the run's own events drive the card on screen
        var svcField = typeof(ChatViewModel).GetField("_agentService", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var agent = svcField.GetValue(Vm)!;
        void Raise(string evt, params object[] args)
        {
            var f = agent.GetType().GetField(evt, BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((Delegate?)f.GetValue(agent))?.DynamicInvoke(args);
        }
        Vm.ShowActivityCard = true;
        await FreshChat();
        Check("AC2a the new-chat page has no card", !Vm.ActivityCardVisible && Vm.Activity.Mood == "idle");
        var chat = Vm.CurrentSession!;
        var gate = new SemaphoreSlim(0);
        Fake.ScriptFor["[AC2]"] = new Queue<FakeStep>([FakeStep.Gated(["AC2 working"], gate, [" — AC2 done."])]);
        await Send("[AC2] build the thing");
        bool live = await WaitUntil(() => Vm.Activity.Mood == "live" && Vm.Messages.Any(m => m.Content.StartsWith("AC2 working")));
        await Render();
        Check("AC2b the turn began: the card is up and live", live && Vm.ActivityCardVisible && Vm.SidePanelVisible, $"{Vm.Activity.Mood} card={Vm.ActivityCardVisible} panel={Vm.SidePanelVisible}");
        Check("AC2c …it reads or writes (the model's own stream)", Vm.Activity.Headline == LocNow("activity.head.reading") || Vm.Activity.Headline == LocNow("activity.head.writing"), Vm.Activity.Headline);
        Raise("OnToolStarting", "read_file", "Reading src/app.cs");
        bool exploring = await WaitUntil(() => Vm.Activity.Station == "explore", 3000);
        Check("AC2d a tool starts: the card is at Explore with its words", exploring && Vm.Activity.Detail == "Reading src/app.cs", $"{Vm.Activity.Station} '{Vm.Activity.Detail}'");
        Raise("OnToolExecuted", new ToolResult { ToolName = "read_file", Success = true, Arguments = new() { ["path"] = "src/app.cs" } });
        bool counted = await WaitUntil(() => Vm.Activity.Reads == "1", 3000);
        Check("AC2e it came back: one look, in the feed with its target", counted && Vm.Activity.Feed.Count >= 1 && Vm.Activity.Feed[0].State == "ok" && Vm.Activity.Feed[0].Target.Contains("app.cs"),
              $"{Vm.Activity.Reads} {string.Join("|", Vm.Activity.Feed.Select(f => f.Verb + " " + f.Target))}");
        Raise("OnToolStarting", "run_command", "Running npm test");
        Raise("OnToolExecuted", new ToolResult { ToolName = "run_command", Success = false, Error = "exit 1", Arguments = new() { ["command"] = "npm test" } });
        bool failedTool = await WaitUntil(() => Vm.Activity.Commands == "1" && Vm.Activity.Feed.FirstOrDefault()?.State == "fail", 3000);
        Check("AC2f a failed command: counted, ✕ in the feed, its station flashes", failedTool && Vm.Activity.Track.IsFlashing(DateTime.Now), $"{Vm.Activity.Commands}");
        Raise("OnSubAgentEvent", new CodeAgentService.SubAgentEvent(41, "Sub-agent #41", "explore", "look", "start", "", 0));
        bool team = await WaitUntil(() => Vm.Activity.Agents == "1" && Vm.Activity.Track.RunningAgents == 1, 3000);
        Check("AC2g a sub-agent starts: one satellite", team, $"{Vm.Activity.Agents}");
        Raise("OnSubAgentEvent", new CodeAgentService.SubAgentEvent(41, "Sub-agent #41", "explore", "look", "done", "found", 3));
        await WaitUntil(() => Vm.Activity.Track.RunningAgents == 0, 3000);
        Check("AC2h …and ends done", Vm.Activity.Track.Satellites.FirstOrDefault()?.State == "done");

        // a question waits on the owner
        var q = new ChatMessage { Role = MessageRole.Question, Content = "Which folder?", QuestionCallback = _ => { } };
        Vm.Messages.Add(q);
        typeof(ChatViewModel).GetMethod("RefreshSessionBadges", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        bool waiting = await WaitUntil(() => Vm.Activity.Mood == "waiting", 3000);
        Check("AC2i a question waits: the card waits too", waiting, Vm.Activity.Mood);
        q.AnswerQuestion("src");
        typeof(ChatViewModel).GetMethod("RefreshSessionBadges", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        bool back = await WaitUntil(() => Vm.Activity.Mood == "live", 3000);
        Check("AC2j answered: live again", back, Vm.Activity.Mood);

        // another chat on screen: the card is that chat's; the first one carries on
        await FreshChat();
        await Render();
        Check("AC2k another chat: its own card (idle)", Vm.Activity.Mood == "idle" && Vm.Activity.Reads == "0", $"{Vm.Activity.Mood} {Vm.Activity.Reads}");
        gate.Release();
        await WaitUntil(() => chat.RunStatus == RunBadge.Done, 15000);
        await Open(chat.Id);
        await Render();
        Check("AC2l back on the first chat: done, its counts kept", Vm.Activity.Mood == "done" && Vm.Activity.Reads == "1" && Vm.Activity.Commands == "1"
              && Vm.Activity.Headline == LocNow("activity.head.done"), $"{Vm.Activity.Mood} {Vm.Activity.Reads} {Vm.Activity.Commands}");
        Check("AC2m the clock stopped at the end", Vm.Activity.Elapsed.Length > 0 && Vm.Activity.Track.EndedAt != null, Vm.Activity.Elapsed);

        // AC3 — a turn that fails, a turn that is stopped
        await FreshChat();
        Fake.ScriptFor["[AC3]"] = new Queue<FakeStep>([FakeStep.Throw(new System.Net.Http.HttpRequestException("invalid request", null, System.Net.HttpStatusCode.BadRequest))]);
        await Send("[AC3] will fail");
        bool fail = await WaitUntil(() => Vm.Activity.Mood == "failed", 15000);
        Check("AC3a an error ends it: failed", fail, Vm.Activity.Mood);
        await WaitIdle();
        await FreshChat();
        Fake.ScriptFor["[AC3s]"] = new Queue<FakeStep>([FakeStep.Hang()]);
        await Send("[AC3s] long one");
        await WaitUntil(() => Vm.Activity.Mood == "live");
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle(5000);
        Check("AC3b the owner's Stop: stopped", Vm.Activity.Mood == "stopped", Vm.Activity.Mood);

        // AC4 — ✕ turns it off (kept), the "+" menu turns it back on
        bool panelBefore = Vm.SidePanelVisible;
        Vm.CloseActivityCardCommand.Execute(null);
        await Render();
        Check("AC4a ✕: the card is off and kept off in the settings", !Vm.ShowActivityCard && !Vm.ActivityCardVisible && Settings.Settings.ChatActivityCard == false,
              $"{Vm.ShowActivityCard} {Settings.Settings.ChatActivityCard}");
        Check("AC4b …the panel goes with it when it showed nothing else", panelBefore && Vm.SidePanelVisible == (!string.IsNullOrEmpty(Vm.LatestImagePath) || !string.IsNullOrEmpty(Vm.LatestRunOutput)),
              $"{panelBefore}→{Vm.SidePanelVisible}");
        Vm.ShowActivityCard = true;
        await Render();
        Check("AC4c the + menu turns it on again", Vm.ActivityCardVisible && Settings.Settings.ChatActivityCard, $"{Vm.ActivityCardVisible}");
    }

    static string LocNow(string key) => LocalizedResources.Instance[key];
    static string LocTh(string key) => Dict(key, "th");
    static string LocEn(string key) => Dict(key, "en");
    static string Dict(string key, string lang)
    {
        var keys = (Dictionary<string, Dictionary<string, string>>)typeof(ChatViewModel).Assembly.GetType("CluadeX.Services.Localization.ActivityKeys")!
            .GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return keys.TryGetValue(key, out var d) && d.TryGetValue(lang, out var s) ? s : "";
    }

    static IEnumerable<string> ActivityKeyNames() =>
        ((Dictionary<string, Dictionary<string, string>>)typeof(ChatViewModel).Assembly.GetType("CluadeX.Services.Localization.ActivityKeys")!
            .GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Keys;
}
