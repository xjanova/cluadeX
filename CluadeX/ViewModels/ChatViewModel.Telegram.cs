using System.IO;
using System.Text;
using System.Windows;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Remote;

namespace CluadeX.ViewModels;

/// <summary>
/// The Telegram bridge's side here (Services/Remote/TelegramBridge): a message from the owner's phone goes into the
/// "Telegram chat" — opened off screen in the project /use picked, like a routine's — and the reply goes back; /status
/// reads the runs; a permission card goes to the phone from the card's own code (askPermission).
/// </summary>
public partial class ChatViewModel
{
    private void InitTelegram()
    {
        if (TelegramBridge.Current is not { } tg) return;
        tg.AskChat = AskFromTelegramAsync;
        tg.StatusText = TelegramStatusAsync;
        tg.RecentProjects = () => (ProjectRegistryService.Current?.Recent(30).Select(e => e.Path) ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        tg.OnUi = a => Application.Current?.Dispatcher.InvokeAsync(a).Task ?? Task.CompletedTask;
        tg.Start();
    }

    private async Task<(string ChatId, string Reply, bool Error)> AskFromTelegramAsync(string? chatId, string text, string project, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return ("", "CluadeX is closing", true);
        string? id = await dispatcher.InvokeAsync(() => ChatForTelegram(chatId, project));
        if (id == null) return ("", LocF("routine.err.noFolder", project), true);
        TelegramBridge.Current?.UseChat(id);
        _debugLog?.Info("Telegram", $"a message from the phone into chat {id} ({text.Length} chars)");
        McpTaskOutcome outcome;
        try { outcome = await ContinueChatForPipeAsync(id, null, text, null, null, null, ct, fromWatch: true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { outcome = new McpTaskOutcome(ex.Message, IsError: true); }
        return (id, outcome.Text ?? "", outcome.IsError);
    }

    /// <summary>The chat Telegram's messages go into (UI thread): the last one while it is still here, else a new chat in
    /// <paramref name="project"/>. Null = that folder is gone.</summary>
    private string? ChatForTelegram(string? chatId, string project)
    {
        if (chatId != null && Sessions.Any(s => s.Id == chatId)) return chatId;
        string folder = (project ?? "").Trim();
        if (folder.Length > 0 && !Directory.Exists(folder)) return null;
        var chat = new ChatSession { ProjectPath = folder, ProjectId = FolderProjectId(folder), Title = "📱 Telegram" };
        chat.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = Loc("tg.chat.opened") });
        Sessions.Insert(0, chat);
        _persistenceService.SaveSession(chat);
        RebuildVisibleSessions();
        return chat.Id;
    }

    /// <summary>/status: the chats running now, and how many cards wait.</summary>
    private Task<string> TelegramStatusAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.FromResult(Loc("tg.noApp"));
        return dispatcher.InvokeAsync(() =>
        {
            var runs = _runs.ToList();
            int waiting = runs.Sum(r => r.Messages.Count(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered))
                          + (runs.Any(r => ReferenceEquals(r.Messages, Messages)) ? 0
                              : Messages.Count(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered));
            if (runs.Count == 0 && waiting == 0) return Loc("tg.status.idle");
            var sb = new StringBuilder();
            if (runs.Count > 0)
            {
                sb.Append(Loc("tg.status.running")).Append('\n');
                foreach (var r in runs.Take(10))
                    sb.Append("• ").Append(r.Session.Title).Append(" — ").Append((int)r.Stopwatch.Elapsed.TotalMinutes).Append(" min\n");
            }
            if (waiting > 0) sb.Append(LocF("tg.status.waiting", waiting));
            return sb.ToString().TrimEnd();
        }).Task;
    }
}
