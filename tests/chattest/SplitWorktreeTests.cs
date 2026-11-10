using System.IO;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;

namespace ChatTest;

/// <summary>
/// SWT — a split chat in its own git worktree (new_chat isolation "worktree"). A chat split off in the SAME project used
/// to share its parent's checkout, so both agents edited and built the same files at once. Through the real chat page,
/// in a throwaway git repository (no network, the fake provider): the split gets its own branch and worktree cut from the
/// parent's HEAD and its turn runs there; its "ended a turn" notice names the branch; close_chat keeps a worktree that
/// holds unmerged work (and the chat open), and removes it — worktree and branch — once the branch is merged. Also the
/// git side on its own (SplitWorktree) and the calls that must be refused.
/// </summary>
internal static partial class Program
{
    static async Task<(int Code, string Out)> Git(string cwd, params string[] args) =>
        await WorktreeSandbox.GitAsync(cwd, CancellationToken.None, null,
            new[] { "-c", "user.name=swt", "-c", "user.email=swt@localhost", "-c", "commit.gpgsign=false" }.Concat(args).ToArray());

    static async Task<string> NewRepo(string name)
    {
        string repo = Path.Combine(Settings.DataRoot, "swt", name + "-" + Guid.NewGuid().ToString("N")[..6], "repo");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\n");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "init");
        return repo;
    }

    static async Task<bool> BranchExists(string repo, string branch) =>
        (await Git(repo, "rev-parse", "--verify", "-q", "refs/heads/" + branch)).Code == 0;

    static async Task TestSplitWorktrees()
    {
        Log("\n## SWT — a split chat in its own git worktree (new_chat isolation \"worktree\")");
        await SwtGitSide();
        await SwtThroughTheChat();
    }

    /// <summary>SplitWorktree on its own: made from HEAD on a branch, what counts as unmerged, removal only when merged.</summary>
    static async Task SwtGitSide()
    {
        string repo = await NewRepo("git");
        string head = (await Git(repo, "rev-parse", "HEAD")).Out.Trim();
        var wt = await SplitWorktree.CreateAsync(repo, "Fix the parser (ทดสอบ)");
        Check("SWT1 a worktree on a new branch cut from HEAD, beside the repository (never inside it)",
            Directory.Exists(wt.Root) && wt.Branch.StartsWith(SplitWorktree.BranchPrefix + "fix-the-parser-") && wt.BaseCommit == head
            && await BranchExists(repo, wt.Branch) && !ProjectToolModule.IsSameOrInside(repo, wt.Root)
            && (await Git(wt.Root, "rev-parse", "HEAD")).Out.Trim() == head,
            $"{wt.Root} · {wt.Branch} · {wt.BaseCommit}");
        Check("SWT1 a fresh worktree holds nothing to lose", await SplitWorktree.HeldWorkAsync(wt) == null);

        File.WriteAllText(Path.Combine(wt.Root, "parser.txt"), "fixed\n");
        string? dirty = await SplitWorktree.HeldWorkAsync(wt);
        Check("SWT2 an uncommitted change counts as held", dirty != null && dirty.Contains("uncommitted"), dirty ?? "null");
        await Git(wt.Root, "add", "-A");
        await Git(wt.Root, "commit", "-q", "-m", "fix parser");
        string? ahead = await SplitWorktree.HeldWorkAsync(wt);
        Check("SWT2 a commit no other branch has counts as unmerged, and names the branch",
            ahead != null && ahead.Contains("1 commit") && ahead.Contains(wt.Branch) && !ahead.Contains("uncommitted"), ahead ?? "null");
        string? kept = await SplitWorktree.RemoveIfMergedAsync(wt);
        Check("SWT2 an unmerged worktree is not removed (nor its branch)",
            kept != null && Directory.Exists(wt.Root) && await BranchExists(repo, wt.Branch), kept ?? "removed");

        await Git(repo, "merge", "-q", "--no-edit", wt.Branch);
        Check("SWT3 once merged into another branch, nothing is held", await SplitWorktree.HeldWorkAsync(wt) == null);
        string? gone = await SplitWorktree.RemoveIfMergedAsync(wt);
        Check("SWT3 …and the worktree and its branch are removed",
            gone == null && !Directory.Exists(wt.Root) && !await BranchExists(repo, wt.Branch), gone ?? "removed");

        string plain = Path.Combine(Path.GetTempPath(), "cluadex-swt-plain-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(plain);
        string? refused = null;
        try { await SplitWorktree.CreateAsync(plain, "x"); } catch (Exception ex) { refused = ex.Message; }
        Check("SWT4 a folder that is not a git repository gets no worktree, and says why",
            refused != null && refused.Contains("not a git repository"), refused ?? "made one");
        try { Directory.Delete(plain, true); } catch { }
    }

    /// <summary>The whole path on the real chat page: new_chat → its own worktree → notice with the branch → close_chat.</summary>
    static async Task SwtThroughTheChat()
    {
        // A link store of its own: earlier split checks used up the 10-minute split ceiling (SplitLimits).
        var linksBefore = ChatLinkStore.Current;
        var store = ChatLinkStore.Current = new ChatLinkStore(Path.Combine(Settings.DataRoot, "swt-links-" + Guid.NewGuid().ToString("N")[..6]));
        string repo = await NewRepo("chat");
        string? folderBefore = Vm.WorkingDirectory;
        try
        {
            Vm.SetWorkingDirectory(repo);
            var parentChat = ChatInDb("SWT parent", "plan the parser work", "ok");
            await Open(parentChat.Id);
            string parentId = Vm.CurrentSession!.Id;
            var module = new ProjectToolModule();
            var ctx = new ToolModuleContext { WorkingDirectory = repo };
            Task<ToolResult> NewChat(params (string Key, string Value)[] args)
            {
                var call = new ToolCall { Type = ToolType.NewChat, ToolName = "new_chat" };
                foreach (var (k, v) in args) call.Arguments[k] = v;
                return module.ExecuteAsync(call, ctx, default);
            }

            // Calls that must be refused, before anything is made.
            var withFork = await NewChat(("isolation", "worktree"), ("fork", "true"), ("message", "x"));
            string other = Path.Combine(AppContext.BaseDirectory, "swt-other");
            Directory.CreateDirectory(other);
            var otherFolder = await NewChat(("isolation", "worktree"), ("path", other), ("message", "x"));
            var noMessage = await NewChat(("isolation", "worktree"));
            var badValue = await NewChat(("isolation", "sandbox"), ("message", "x"));
            Check("SWT5 isolation \"worktree\" is refused with fork, for another folder, without a message, and a wrong value is named",
                !withFork.Success && withFork.Output.Contains("fork") && !otherFolder.Success && otherFolder.Output.Contains("another folder")
                && !noMessage.Success && !badValue.Success && badValue.Output.Contains("sandbox")
                && store.ChildrenOf(parentId).Count == 0,
                $"{withFork.Output} | {otherFolder.Output} | {noMessage.Output} | {badValue.Output}");

            int callsBefore = Fake.Calls.Count;
            var opened = await NewChat(("isolation", "worktree"), ("title", "SWT parser split"), ("message", "Fix the parser and commit it."));
            var child = Vm.Sessions.FirstOrDefault(s => s.Title == "SWT parser split");
            var wt = child == null ? null : store.WorktreeOf(child.Id);
            Check("SWT6 new_chat isolation \"worktree\" opens a split chat in its own worktree, on its own branch",
                opened.Success && child != null && child.ParentId == parentId && wt != null && Directory.Exists(wt.Folder)
                && await BranchExists(repo, wt.Branch) && opened.Output.Contains(wt.Branch) && opened.Output.Contains(wt.Folder),
                opened.Output);
            if (child == null || wt == null) return;
            await WaitIdle();
            string? taskFolder = (string?)CallVm("TaskFolderFor", child);
            Check("SWT6 every turn of that chat runs in its worktree, not the parent's checkout",
                taskFolder != null && string.Equals(taskFolder, wt.Folder, StringComparison.OrdinalIgnoreCase), taskFolder ?? "null");
            var sent = Fake.Calls.Skip(callsBefore).FirstOrDefault(c => c.Input.Contains("Fix the parser"));
            Check("SWT6 its agent is told where it works and to stay out of the parent's folder",
                sent != null && sent.Input.Contains(wt.Folder) && sent.Input.Contains(wt.Branch) && sent.Input.Contains("never in " + wt.ParentFolder),
                sent?.Input ?? $"no call ({Fake.Calls.Count - callsBefore} new)");
            var childList = Vm.CurrentSession?.Id == child.Id ? Vm.Messages.ToList() : (Persist.LoadSession(child.Id)?.Messages ?? new());
            Check("SWT6 the split chat shows the owner which worktree and branch it works on",
                childList.Any(m => m.IsNotice && m.Content.StartsWith("🌿") && m.Content.Contains(wt.Branch)),
                string.Join(" | ", childList.Select(m => m.Content)));

            // The split chat did work and committed it on its branch.
            File.WriteAllText(Path.Combine(wt.Root, "parser.txt"), "fixed\n");
            await Git(wt.Root, "add", "-A");
            await Git(wt.Root, "commit", "-q", "-m", "fix parser");

            // It ends a turn: the parent hears the branch.
            await Open(parentId);
            CallVm("TellParentSplitFinished", child,
                new List<ChatMessage> { new() { Role = MessageRole.Assistant, Content = "Parser fixed and committed." } });
            await Render();
            var note = Vm.Messages.LastOrDefault(m => m.IsNotice);
            Check("SWT7 the split chat's \"ended a turn\" notice names its branch (owner's text, and so the parent's next turn)",
                note != null && note.Content.Contains(wt.Branch) && note.Content.Contains(LocF("chat.split.worktreeNote", wt.Branch, wt.Root).Trim()),
                note?.Content ?? "no notice");
            Check("SWT7 the parent's agent reads it as a notice in its history",
                ChatViewModel.BuildModelHistory(Vm.Messages).Any(h => h.Role == MessageRole.User && h.Content.StartsWith(ChatViewModel.NoticePrefix) && h.Content.Contains(wt.Branch)));

            // close_chat while the branch is not merged: refused, chat and worktree kept.
            var closeAsync = typeof(ChatViewModel).GetMethod("CloseChatsWithWorktreesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            async Task<string> Close(string target) => await (Task<string>)closeAsync.Invoke(Vm, new object[] { parentId, target })!;
            string listed = await Close("list");
            Check("SWT8 close_chat lists the split chat with its worktree, branch and what is unmerged",
                listed.Contains(wt.Branch) && listed.Contains("unmerged"), listed);
            string refused = await Close("done");
            Check("SWT8 close_chat refuses to put away a split chat whose worktree holds unmerged work, and says so",
                !child.IsArchived && Directory.Exists(wt.Root) && await BranchExists(repo, wt.Branch) && store.WorktreeOf(child.Id) != null
                && refused.Contains("not merged") && refused.Contains(wt.Branch), refused);

            // Merged: close_chat puts it away and removes the worktree and the branch.
            await Git(repo, "merge", "-q", "--no-edit", wt.Branch);
            string closed = await Close("done");
            Check("SWT9 once its branch is merged, close_chat puts it away and removes the worktree and its branch",
                child.IsArchived && !Directory.Exists(wt.Root) && !await BranchExists(repo, wt.Branch) && store.WorktreeOf(child.Id) == null
                && closed.Contains("Removed the worktree"), closed);
            Check("SWT9 the merged work is in the parent's checkout", File.Exists(Path.Combine(repo, "parser.txt")));

            // Not a git repository: nothing opens, and the agent hears why.
            string plain = Path.Combine(Path.GetTempPath(), "cluadex-swt-plain-chat-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(plain);
            Vm.SetWorkingDirectory(plain);
            await Open(parentId);
            int kids = store.ChildrenOf(parentId).Count;
            var notGit = await new ProjectToolModule().ExecuteAsync(new ToolCall
            {
                Type = ToolType.NewChat, ToolName = "new_chat",
                Arguments = { ["isolation"] = "worktree", ["message"] = "x", ["title"] = "SWT not git" },
            }, new ToolModuleContext { WorkingDirectory = plain }, default);
            Check("SWT10 outside a git repository the call fails with the reason and opens no chat",
                !notGit.Success && notGit.Output.Contains("not a git repository") && store.ChildrenOf(parentId).Count == kids
                && !Vm.Sessions.Any(s => s.Title == "SWT not git"), notGit.Output);
            try { Directory.Delete(plain, true); } catch { }
        }
        finally
        {
            ChatLinkStore.Current = linksBefore;
            Vm.SetWorkingDirectory(folderBefore ?? "");
            await Render();
        }
    }
}
