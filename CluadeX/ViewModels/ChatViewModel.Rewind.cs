using System.IO;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Going back to any point of a chat (owner 2026-10-31: "ย้อนไปคุยใหม่ในแชทที่ผ่านมาได้ทุกจุด โดยมันจะไม่สนที่ต่อจากนั้น").
/// Rewind cuts this chat there; Fork opens a new chat holding what came before and leaves this one as it is. Every turn
/// sends the model the transcript (BuildModelHistory, the CLI's transcript or handoff), nothing is kept on a provider's
/// side, so what was cut is gone from the model's view too. On a user message the cut falls before it and the message
/// returns to the input box to be changed; on anything else the cut falls after it.
/// </summary>
public partial class ChatViewModel
{
    public ICommand RewindToMessageCommand => _rewindToMessageCommand ??= new RelayCommand<ChatMessage>(m => Detached(() => RewindToAsync(m)));
    private ICommand? _rewindToMessageCommand;

    public ICommand ForkFromMessageCommand => _forkFromMessageCommand ??= new RelayCommand<ChatMessage>(m => Detached(() => ForkFromAsync(m)));
    private ICommand? _forkFromMessageCommand;

    /// <summary>The rewind's question when files changed after the point: true = put the files back too, false = the
    /// chat only, null = cancel. A seam like <see cref="Confirm"/>.</summary>
    public Func<string, string, bool?> AskRewindFiles { get; set; } = (text, title) =>
        MessageBox.Show(text, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    /// <summary>How many rows stay when the chat is cut at <paramref name="at"/>; -1 when it is not in the list.</summary>
    private static int RowsKept(IList<ChatMessage> list, ChatMessage at)
    {
        int i = list.IndexOf(at);
        return i < 0 ? -1 : at.Role == MessageRole.User ? i : i + 1;
    }

    /// <summary>The restore points this chat made from <paramref name="since"/> on, oldest first.</summary>
    private static List<CheckpointService.Point> PointsSince(ChatSession session, DateTime since)
    {
        if (CheckpointService.Current is not { } cp || string.IsNullOrWhiteSpace(session.ProjectPath)) return new();
        var utc = since.ToUniversalTime();
        return cp.PointsForSession(session.ProjectPath.TrimEnd('\\', '/'), session.Id).Where(p => p.TimeUtc >= utc).ToList();
    }

    private async Task RewindToAsync(ChatMessage? at)
    {
        var session = CurrentSession;
        if (at == null || session == null) return;
        if (!await StopRunOfAsync(session.Id) || !ReferenceEquals(CurrentSession, session)) return;
        int keep = RowsKept(Messages, at);
        if (keep < 0) return;
        int dropped = Messages.Count - keep;
        if (dropped == 0) { StatusText = Loc("chat.rewind.nothing"); return; }

        var points = await Task.Run(() => PointsSince(session, Messages[keep].Timestamp));
        if (!ReferenceEquals(CurrentSession, session)) return;
        bool restore = false;
        if (points.Count == 0)
        {
            if (!Confirm(LocF("chat.rewind.confirm", dropped), Loc("chat.rewind.title"))) return;
        }
        else
        {
            int files = points.SelectMany(p => p.Files).Select(f => f.File.ToLowerInvariant()).Distinct().Count();
            if (AskRewindFiles(LocF("chat.rewind.confirmFiles", dropped, files), Loc("chat.rewind.title")) is not { } answer) return;
            restore = answer;
        }

        int restored = 0;
        if (restore && CheckpointService.Current is { } cp)
        {
            var first = points[0];
            var r = await Task.Run(() => cp.RestoreTo(first.Project, first.Id));
            if (!r.Ok)
            {
                // The chat stays whole: a chat cut back with its files left ahead would be worse than no rewind.
                StatusText = LocF("chat.rewind.filesFailed", r.Message);
                return;
            }
            restored = r.Files;
        }
        if (!ReferenceEquals(CurrentSession, session)) return;

        keep = RowsKept(Messages, at);
        if (keep < 0) return;
        for (int i = Messages.Count - 1; i >= keep; i--) Messages.RemoveAt(i);
        if (restored > 0)
            Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⏪ " + LocF("chat.rewind.filesNote", restored) });
        // A plan written after the point belongs to what was cut.
        _lastPlans.Remove(session.Id);
        _planAuthors.Remove(session.Id);
        _toolActionsAtLastCompact = CountToolActionsSinceMarker(Messages);
        PutBackInInput(at);
        SaveNow();
        UpdateContextInfo();
        RefreshStickyPlan();
        RaiseRunStateChanged();
        ScrollToBottom?.Invoke();
        StatusText = LocF("chat.rewind.done", dropped);
    }

    private async Task ForkFromAsync(ChatMessage? at)
    {
        var parent = CurrentSession;
        if (at == null || parent == null) return;
        int keep = RowsKept(Messages, at);
        if (keep < 0) return;
        var kept = Messages.Take(keep).Select(m => m.Id).ToHashSet();

        // From the store: its rows are the chat's own new objects, without a run's live rows and callbacks.
        SaveNow();
        var stored = _persistenceService.LoadSession(parent.Id);
        var copy = stored?.Messages.Where(m => kept.Contains(m.Id)).ToList() ?? new();
        if (copy.Count == 0 && keep > 0 && Messages.Take(keep).Any(m => m.Role is MessageRole.User or MessageRole.Assistant))
        {
            StatusText = Loc("chat.fork.failed");
            return;
        }
        foreach (var m in copy) m.Id = Guid.NewGuid().ToString();

        var child = new ChatSession { Title = LocF("chat.fork.title", parent.Title), ProjectPath = parent.ProjectPath, Messages = copy };
        child.ExtraFolders.AddRange(parent.ExtraFolders);
        if (copy.Count > 0) _persistenceService.SaveSession(child);
        Sessions.Insert(0, child);
        RecordBranch(child.Id, parent.Id, copy.Count(m => m.Role == MessageRole.User) + (at.Role == MessageRole.User ? 1 : 0));

        await GoToChatAsync(child.Id);
        if (CurrentSession?.Id != child.Id) return;
        PutBackInInput(at);
        StatusText = Loc("chat.fork.done");
    }

    /// <summary>A user message the cut went through comes back to be changed, with what the owner has typed since and the
    /// files it carried. Other rows leave the input as it is.</summary>
    private void PutBackInInput(ChatMessage at)
    {
        if (at.Role != MessageRole.User) return;
        UserInput = string.IsNullOrWhiteSpace(UserInput) ? at.Content : at.Content + "\n" + UserInput;
        foreach (var p in at.AttachedImages)
            if (File.Exists(p) && !PendingImages.Contains(p) && PendingImages.Count < MaxPendingImages) PendingImages.Add(p);
        foreach (var p in at.AttachedFiles)
            if (File.Exists(p) && !PendingFiles.Contains(p) && PendingFiles.Count < MaxPendingFiles) PendingFiles.Add(p);
    }
}
