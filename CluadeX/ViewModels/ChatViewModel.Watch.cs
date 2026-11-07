using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Watch;

namespace CluadeX.ViewModels;

/// <summary>
/// A watch's card in its chat (watch_ci / watch_process): what it waits for, where it stands, a Cancel button. Reads the
/// job itself, so the card moves while the watch looks (from any thread, marshalled here).
/// </summary>
public sealed class WatchCardView : INotifyPropertyChanged
{
    private readonly WatchJob _job;
    private readonly Action<string> _cancel;

    public WatchCardView(WatchJob job, Action<string> cancel)
    {
        _job = job;
        _cancel = cancel;
        PropertyChangedEventManager.AddHandler(job, (_, _) =>
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Raise(); else d.BeginInvoke(Raise);
        }, "");
        // The card's words follow a language switch (weak: a card scrolled away is not kept alive by it).
        PropertyChangedEventManager.AddHandler(LocalizedResources.Instance, (_, _) => Raise(), "");
        CancelCommand = new RelayCommand(() => _cancel(_job.Id), () => _job.IsActive);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string L(string key) => LocalizedResources.Instance[key];

    public string Id => _job.Id;
    public bool IsActive => _job.IsActive;
    public string Glyph => _job.State switch
    {
        WatchState.Watching => "👁", WatchState.Passed => "✅", WatchState.Failed => "❌",
        WatchState.Cancelled => "⊘", WatchState.TimedOut => "⏱", WatchState.Error => "⚠", _ => "⏹",
    };
    public string Title => string.Format(L(_job.Kind == WatchKind.Ci ? "watch.card.ci" : "watch.card.process"), _job.Target);
    public string StateText => L("watch.state." + _job.State.ToString().ToLowerInvariant())
                               + (_job.Summary.Length > 0 ? " — " + _job.Summary : "");
    public string Times => _job.IsActive
        ? string.Format(L("watch.card.until"), _job.Deadline.ToString("HH:mm"))
          + (_job.LastCheckedAt is { } t ? " · " + string.Format(L("watch.card.lastLook"), t.ToString("HH:mm:ss")) : "")
        : "";
    public string? Note => _job.Note;
    public bool HasNote => !string.IsNullOrEmpty(_job.Note);
    public ICommand CancelCommand { get; }
}

/// <summary>
/// Watches in the chat (owner 2026-11-07: "ตัวเฝ้าติดตามงาน … การรอ ci หรือ commit ให้ผ่าน"): the card a watch_ci /
/// watch_process call leaves, and what happens when the watch ends —
/// <list type="bullet">
/// <item>the chat's AI is working: a notice row, and the result as a note it reads at its next step (a note it never
///   took is delivered again when its turn ends);</item>
/// <item>nobody is working there: a new turn starts in that chat with the result (on screen or not), unless the owner
///   switched that off (Settings ▸ <c>WatchAutoContinue</c>) or the chat already had <see cref="RunWatchService.MaxWakes"/>
///   such turns in <see cref="RunWatchService.WakeWindow"/> — then a notice row only, for the owner to act on.</item>
/// </list>
/// A cancelled watch tells nobody. Ctrl+Alt+End and deleting the chat cancel its watches.
/// </summary>
public partial class ChatViewModel
{
    private RunWatchService? _watch;

    private void InitWatch()
    {
        _watch = RunWatchService.Current;
        if (_watch == null) return;
        ChatMessage.WatchCardFor = m => _watch.Get(m.ToolOutput) is { } job ? new WatchCardView(job, id => CancelWatch(id)) : null;
        _watch.Started += job => Application.Current?.Dispatcher.BeginInvoke(() => OnWatchStarted(job));
        _watch.Finished += job => Application.Current?.Dispatcher.BeginInvoke(() => DeliverWatch(job));
        _watch.Changed += job =>
        {
            if (!job.IsActive) Application.Current?.Dispatcher.BeginInvoke(() => SettleWatchRow(job));
        };
    }

    /// <summary>The card's Cancel (the owner's click).</summary>
    public bool CancelWatch(string id) => _watch?.Cancel(id, null, Loc("watch.cancelledByYou")) == true;

    private void OnWatchStarted(WatchJob job)
    {
        if (Sessions.FirstOrDefault(s => s.Id == job.ChatId) is not { } session) return;
        PostTurnMessage(session, new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.WatchTag,
            ToolOutput = job.Id,
            Content = "👁 " + LocF(job.Kind == WatchKind.Ci ? "watch.card.ci" : "watch.card.process", job.Target),
        });
    }

    /// <summary>The card row's own text once the watch is over (a reloaded chat whose job left the file shows it).</summary>
    private void SettleWatchRow(WatchJob job)
    {
        var row = LiveListOf(job.ChatId)?.FirstOrDefault(m => m.ToolName == ChatMessage.WatchTag && m.ToolOutput == job.Id);
        if (row == null) return;
        row.Content = $"{(job.State == WatchState.Passed ? "✅" : job.State == WatchState.Failed ? "❌" : "⏹")} "
                      + LocF(job.Kind == WatchKind.Ci ? "watch.card.ci" : "watch.card.process", job.Target)
                      + " — " + Loc("watch.state." + job.State.ToString().ToLowerInvariant())
                      + (job.Summary.Length > 0 ? ": " + job.Summary : "");
    }

    /// <summary>The one line the owner sees when a watch ends.</summary>
    private string WatchShown(WatchJob job) =>
        $"{(job.State == WatchState.Passed ? "✅" : job.State == WatchState.Failed ? "❌" : "⏰")} "
        + LocF("watch.ended", LocF(job.Kind == WatchKind.Ci ? "watch.card.ci" : "watch.card.process", job.Target),
            Loc("watch.state." + job.State.ToString().ToLowerInvariant()) + (job.Summary.Length > 0 ? " — " + job.Summary : ""));

    /// <summary>A watch ended (UI thread). <paramref name="rowShown"/>: the notice row is already in the chat (a note its
    /// run did not take, handed on at the turn's end).</summary>
    public void DeliverWatch(WatchJob job, bool rowShown = false)
    {
        if (_watch == null || job.State is WatchState.Watching or WatchState.Cancelled) return;
        SettleWatchRow(job);
        var session = Sessions.FirstOrDefault(s => s.Id == job.ChatId);
        if (session == null)
        {
            _debugLog?.Info("Watch", $"{job.Id} ended for chat {job.ChatId}, which is gone — nobody told");
            return;
        }
        string shown = WatchShown(job);
        string forModel = RunWatchService.Report(job);
        ChatMessage Notice(string text) => new() { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = text };

        // Someone works there: the row now, the result at the agent's next step.
        if (_runs.FirstOrDefault(r => r.Session.Id == job.ChatId) is { Scope: { } scope } run)
        {
            if (!rowShown) run.Messages.Add(Notice(shown));
            scope.AddUserNote(NoticePrefix + forModel, job);
            return;
        }
        if (!_settingsService.Settings.WatchAutoContinue)
        {
            if (!rowShown) PostTurnMessage(session, Notice(shown + "\n" + Loc("watch.noAuto")));
            return;
        }
        if (!_watch.TryTakeWake(job.ChatId))
        {
            PostTurnMessage(session, Notice((rowShown ? "" : shown + "\n") + LocF("watch.wakeLimit", _watch.MaxWakes,
                RunWatchService.Elapsed(_watch.WakeWindow))));
            return;
        }
        string text = shown + "\n\n" + NoticePrefix + forModel;
        _debugLog?.Info("Watch", $"{job.Id} ended ({job.State}): starting a turn in chat {job.ChatId}");
        Detached(() => WakeChatAsync(job, text));
    }

    /// <summary>A turn in the watch's chat with the result as its message (the pipe's continue_chat path: off screen in the
    /// chat's own list, or through Send when it is the one on screen). Refused because a turn began meanwhile: the result
    /// goes to that turn as a note. Refused for another reason: a notice row says so.</summary>
    private async Task WakeChatAsync(WatchJob job, string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            McpTaskOutcome outcome;
            try { outcome = await ContinueChatForPipeAsync(job.ChatId, null, text, null, null, null, CancellationToken.None, fromWatch: true); }
            catch (Exception ex) { outcome = new McpTaskOutcome(ex.Message, IsError: true); }
            if (!outcome.IsError || !NotSent(outcome.Text)) return;   // it ran (a failed turn says so in its own chat)
            bool handed = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_runs.FirstOrDefault(r => r.Session.Id == job.ChatId) is not { Scope: { } scope } run) return false;
                run.Messages.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = WatchShown(job) });
                scope.AddUserNote(NoticePrefix + RunWatchService.Report(job), job);
                return true;
            });
            if (handed) return;
            if (attempt == 0) { await Task.Delay(1500); continue; }
            _debugLog?.Warn("Watch", $"{job.Id}: the result could not start a turn: {outcome.Text}");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Sessions.FirstOrDefault(s => s.Id == job.ChatId) is { } s)
                    PostTurnMessage(s, new ChatMessage
                    {
                        Role = MessageRole.System, ToolName = ChatMessage.NoticeTag,
                        Content = WatchShown(job) + "\n" + LocF("watch.notStarted", RunWatchService.Clip(outcome.Text, 200)),
                    });
            });
        }
    }

    /// <summary>continue_chat said no before any turn began (as opposed to a turn that ran and failed).</summary>
    private static bool NotSent(string text) =>
        text.Contains("nothing was sent", StringComparison.OrdinalIgnoreCase)
        || text.Contains("already running", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("No reply was started", StringComparison.Ordinal);

    /// <summary>EndTurn: results handed to a run as notes it never took. On screen they ride the leftover-notes turn
    /// (SendLeftoverNotes); elsewhere that would drop them, so they are delivered again here.</summary>
    private void HandOnUntakenWatchNotes(ChatRun run, List<(string Text, object? Tag)> leftover)
    {
        if (ReferenceEquals(run.Session, CurrentSession)) return;
        var watched = leftover.Where(n => n.Tag is WatchJob).Select(n => (WatchJob)n.Tag!).ToList();
        if (watched.Count == 0) return;
        leftover.RemoveAll(n => n.Tag is WatchJob);
        foreach (var job in watched)
            Application.Current?.Dispatcher.BeginInvoke(() => DeliverWatch(job, rowShown: true));
    }
}
