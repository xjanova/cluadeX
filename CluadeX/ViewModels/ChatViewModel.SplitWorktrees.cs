using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// Split chats in a git worktree of their own (new_chat isolation "worktree"). A chat split off in the SAME project used
/// to share its parent's checkout: both agents edited and built the same files at once, and the only guard was a line in
/// the model's guidance. With isolation "worktree" the split chat gets its own worktree on its own branch, cut from the
/// asking chat's HEAD (<see cref="SplitWorktree"/>); every turn of it runs there (<see cref="TaskFolderFor"/>), the parent
/// hears the branch when it ends a turn, and close_chat removes the worktree only once nothing on it is unmerged.
/// </summary>
public partial class ChatViewModel
{
    private void InitSplitWorktrees()
    {
        // Read on the tool's own flow (its run scope): the asking chat and the folder its run works in.
        Services.Tools.ProjectToolModule.WorktreeChatHandler = (path, message, title) =>
        {
            var run = FlowRun;
            string? parentFolder = path ?? run?.Scope?.WorkingDirectory ?? _fileSystemService.WorkingDirectory;
            return OpenWorktreeChatAsync(parentFolder, message, title, run?.Session.Id);
        };
    }

    /// <summary>new_chat isolation "worktree": the split limits first (a refused split leaves no worktree behind), then
    /// the worktree, then the chat in it. A chat that does not open takes its fresh worktree with it.</summary>
    private async Task<string> OpenWorktreeChatAsync(string? parentFolder, string? message, string? title, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
            throw new InvalidOperationException("this chat has no project folder to cut a worktree from");
        if (await App.Current.Dispatcher.InvokeAsync(() => SplitRefusal(parentId ?? CurrentSession?.Id)).Task is { } refusal)
            throw new InvalidOperationException(refusal);
        var wt = await SplitWorktree.CreateAsync(parentFolder, title ?? FirstLineOf(message));
        _debugLog?.Info("Chat", $"new_chat worktree · {wt.Root} · branch {wt.Branch} · from {wt.BaseCommit}");
        try
        {
            return await App.Current.Dispatcher.InvokeAsync(() => OpenChatForAgent(null, message, title, parentId, false, wt)).Task;
        }
        catch
        {
            await SplitWorktree.DiscardAsync(wt);
            throw;
        }
    }

    /// <summary>The split chat's worktree folder when it still exists — where every turn of it runs.</summary>
    private static string? WorktreeFolderOf(ChatSession session) =>
        ChatLinkStore.Current?.WorktreeOf(session.Id) is { } wt && Directory.Exists(wt.Folder) ? wt.Folder : null;

    /// <summary>What a split chat in a worktree adds to the parent's "ended a turn" notice: its branch, for the owner
    /// (localized) and for the parent's agent. Empty for a chat that shares its parent's checkout.</summary>
    private (string Shown, string ForModel) WorktreeNoticeOf(ChatSession child) =>
        ChatLinkStore.Current?.WorktreeOf(child.Id) is { } wt
            ? (" " + LocF("chat.split.worktreeNote", wt.Branch, wt.Root), SplitWorktree.NoticeFor(wt))
            : ("", "");

    /// <summary>Split chats under <paramref name="parentId"/> whose worktree holds work that is not merged: chat id → what.
    /// Runs git, so off the UI thread's work (before close_chat hops there).</summary>
    private static async Task<Dictionary<string, string>> HeldSplitWorktreesAsync(string? parentId)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parentId == null || ChatLinkStore.Current is not { } store) return held;
        foreach (var (id, _) in store.DescendantsOf(parentId))
            if (store.WorktreeOf(id) is { } wt && await SplitWorktree.HeldWorkAsync(wt) is { } why)
                held[id] = why;
        return held;
    }

    /// <summary>Remove the worktrees of put-away split chats under <paramref name="parentId"/> that hold nothing
    /// unmerged; keep the others. Returns what the agent is told (empty when nothing had a worktree) and the branches kept.</summary>
    private static async Task<(string Said, List<string> KeptBranches)> RemovePutAwayWorktreesAsync(string? parentId)
    {
        var kept = new List<string>();
        if (parentId == null || ChatLinkStore.Current is not { } store) return ("", kept);
        var said = new System.Text.StringBuilder();
        foreach (var (id, link) in store.DescendantsOf(parentId))
        {
            if (link.ArchivedAt == null || store.WorktreeOf(id) is not { } wt) continue;
            if (await SplitWorktree.RemoveIfMergedAsync(wt) is { } why)
            {
                kept.Add(wt.Branch);
                said.Append($" Kept the worktree of \"{link.Task}\" ({wt.Root}): {why}.");
                continue;
            }
            store.SetWorktree(id, null);
            said.Append($" Removed the worktree of \"{link.Task}\" and its branch {wt.Branch} (nothing on it was unmerged).");
        }
        return (said.ToString(), kept);
    }

    /// <summary>close_chat: which worktrees hold work is read first (git, on the tool's flow), the chats are put away on
    /// the UI thread, then the worktrees of the put-away ones go — only those with nothing unmerged.</summary>
    private async Task<string> CloseChatsWithWorktreesAsync(string? parentId, string target)
    {
        var held = await HeldSplitWorktreesAsync(parentId);
        string said = await App.Current.Dispatcher.InvokeAsync(() => CloseChatsForAgent(parentId, target, held)).Task;
        var (removed, _) = await RemovePutAwayWorktreesAsync(parentId);
        return said + removed;
    }

    /// <summary>The owner put split chats away from the sidebar: their worktrees go too, unless one holds unmerged work —
    /// then it stays, and the status line says which branch.</summary>
    private async Task SweepWorktreesAfterOwnerArchiveAsync(string? parentId)
    {
        try
        {
            var (_, kept) = await RemovePutAwayWorktreesAsync(parentId);
            if (kept.Count > 0) StatusText = LocF("chat.split.worktreeKept", string.Join(", ", kept));
        }
        catch (Exception ex) { _debugLog?.Warn("Chat", "split worktree sweep failed: " + ex.Message); }
    }
}
