using System.Globalization;
using System.IO;
using System.Windows;
using CluadeX.Models;
using CluadeX.Services.Routines;

namespace CluadeX.ViewModels;

/// <summary>
/// Scheduled tasks (Services/Routines): at a routine's time the chat page opens a chat for it — or carries on in the
/// one it used last (SameChat) — and sends its prompt, off screen, the way a split chat starts in the background. The
/// turn runs under the owner's ordinary rules: a card that asks waits for them (and rings, like any other card).
/// </summary>
public partial class ChatViewModel
{
    private void InitRoutines()
    {
        if (RoutineService.Current is not { } routines) return;
        routines.Runner = RunRoutineAsync;
        Services.Tools.ProjectToolModule.ScheduleTaskHandler = ScheduleTaskFromAgentAsync;
    }

    /// <summary>The Routines page's "Open chat": the chat a routine ran in, on screen (as a click in the sidebar).</summary>
    public Task OpenChatByIdAsync(string chatId) => LoadSessionAsync(chatId);

    /// <summary>The project open now ("" = none): the Routines page's default folder for a new routine.</summary>
    public string ProjectFolderNow() => CurrentProjectFolder();

    /// <summary>What the chat is told at a run: the prompt, after one line saying it is a scheduled run.</summary>
    public static string RoutineMessage(Routine r, DateTime at, bool missed) =>
        $"⏰ Scheduled task “{r.Name}” — run of {At(at)}{(missed ? " (making up for a time CluadeX was closed)" : "")}. "
        + "The user set this up to run on its own and may be away: do the work, and end with a short summary of what you "
        + "did and found.\n\n" + r.Prompt.Trim();

    /// <summary>A time as a model reads it: always the Gregorian year (a Thai culture would write 2569).</summary>
    private static string At(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";

    /// <summary>The service's runner: open or reuse the chat, send the prompt, wait for that turn.</summary>
    private async Task<(bool Ok, string Note, string? ChatId)> RunRoutineAsync(Routine r)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return (false, "CluadeX is closing", null);
        string? chatId = await dispatcher.InvokeAsync(() => ChatForRoutine(r));
        if (chatId == null) return (false, LocF("routine.err.noFolder", r.Project), null);
        _debugLog?.Info("Routine", $"{r.Id} “{r.Name}”: starting a turn in chat {chatId}");
        McpTaskOutcome outcome;
        try
        {
            outcome = await ContinueChatForPipeAsync(chatId, null, RoutineMessage(r, DateTime.Now, r.LastNote == "missed-run"),
                null, null, null, CancellationToken.None, fromWatch: true);
        }
        catch (Exception ex) { outcome = new McpTaskOutcome(ex.Message, IsError: true); }
        if (outcome.IsError) _debugLog?.Warn("Routine", $"{r.Id}: {outcome.Text}");
        string note = (outcome.Text ?? "").Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return (!outcome.IsError, note, chatId);
    }

    /// <summary>The chat a run goes into (UI thread): the one it used last when SameChat and it is still here, else a new
    /// chat in the routine's project beside the owner's work. Null = its project folder is gone.</summary>
    private string? ChatForRoutine(Routine r)
    {
        if (r.SameChat && r.ChatId != null && Sessions.Any(s => s.Id == r.ChatId)) return r.ChatId;
        string folder = (r.Project ?? "").Trim();
        if (folder.Length > 0 && !Directory.Exists(folder)) return null;
        var chat = new ChatSession { ProjectPath = folder, ProjectId = FolderProjectId(folder) };
        chat.Title = "⏰ " + r.Name;
        chat.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = "⏰ " + LocF("routine.chat.opened", r.Name, RoutineSchedule.Describe(r, Loc, RoutineSchedule.Culture(Services.LocalizedResources.Instance.Language)),
                folder.Length == 0 ? Loc("routine.noProject") : folder),
        });
        Sessions.Insert(0, chat);
        _persistenceService.SaveSession(chat);
        RebuildVisibleSessions();
        return chat.Id;
    }

    // ─── schedule_task: a chat sets one up (the owner allowed it on the permission card first) ───

    private Task<string> ScheduleTaskFromAgentAsync(Services.Tools.ProjectToolModule.ScheduleRequest req)
    {
        var routines = RoutineService.Current ?? throw new InvalidOperationException("Scheduled tasks are not running in this CluadeX.");
        switch (req.Action)
        {
            case "list":
            {
                var all = routines.All();
                if (all.Count == 0) return Task.FromResult("No scheduled tasks yet.");
                return Task.FromResult(string.Join("\n", all.Select(r =>
                    $"- {r.Id} “{r.Name}” · {RoutineSchedule.Describe(r, Loc)} · {(r.Enabled ? "on" : "off")}"
                    + (r.NextRunAt is { } n ? $" · next {At(n)}" : "")
                    + (r.LastStatus.Length > 0 ? $" · last {r.LastStatus}" : "")
                    + $" · project {(r.Project.Length == 0 ? "(none)" : r.Project)} · by {r.CreatedBy}")));
            }
            case "add":
            {
                if (routines.CountFromAi() >= RoutineService.MaxFromAi)
                    throw new InvalidOperationException($"There are {RoutineService.MaxFromAi} scheduled tasks made from chats already: the user removes some on the Routines page first.");
                var r = new Routine
                {
                    Name = req.Name ?? "",
                    Prompt = req.Prompt ?? "",
                    Project = req.Project ?? "",
                    SameChat = req.SameChat,
                    CatchUp = req.CatchUp,
                    CreatedBy = "ai",
                };
                if (RoutineSchedule.Parse(req.Schedule, r) is { } bad) throw new InvalidOperationException(Loc(bad));
                if (routines.Put(r) is { } problem) throw new InvalidOperationException(Loc(problem));
                var saved = routines.Get(r.Id)!;
                return Task.FromResult($"Scheduled “{saved.Name}” (id {saved.Id}): {RoutineSchedule.Describe(saved, Loc)}, next run "
                    + $"{At(saved.NextRunAt)}, in {(saved.Project.Length == 0 ? "no project" : saved.Project)}. It runs while "
                    + "CluadeX is open; the user sees and changes it on the Routines page.");
            }
            default:
            {
                var r = routines.Find(req.Id) ?? throw new InvalidOperationException($"No scheduled task '{req.Id}' — action \"list\" shows them.");
                switch (req.Action)
                {
                    case "remove": routines.Remove(r.Id); return Task.FromResult($"Removed “{r.Name}”.");
                    case "pause": routines.SetEnabled(r.Id, false); return Task.FromResult($"Paused “{r.Name}”.");
                    case "resume": routines.SetEnabled(r.Id, true); return Task.FromResult($"Resumed “{r.Name}”: next run {At(routines.Get(r.Id)?.NextRunAt)}.");
                    case "run_now":
                        return Task.FromResult(routines.RunNow(r.Id) ? $"Started “{r.Name}” now, in its own chat." : $"“{r.Name}” is running already.");
                    default: throw new InvalidOperationException("action is one of: list, add, remove, pause, resume, run_now.");
                }
            }
        }
    }
}
