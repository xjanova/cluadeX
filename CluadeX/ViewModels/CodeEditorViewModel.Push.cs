using System.Text;
using System.Windows.Input;
using CluadeX.Services;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// "Push" beside Commit in the source-control panel (owner 2026-10-20: "การพุช หลาย repo หรือเชื่อมโยงต้องดี"). The panel
/// could only commit. Now one button pushes every repo of the project — its own, the folders the chat opened beside it,
/// submodules, nested repos — after showing what each will do (a new branch gets its upstream, a detached HEAD or a repo
/// without a remote is skipped and says why). Never --force. A push sends work to a server, so the list is confirmed.
/// </summary>
public partial class CodeEditorViewModel
{
    private ICommand? _pushAllCommand;
    public ICommand PushAllCommand => _pushAllCommand ??= new AsyncRelayCommand(PushAllAsync);

    private bool _isPushing;
    public bool IsPushing { get => _isPushing; set => SetProperty(ref _isPushing, value); }

    private async Task PushAllAsync()
    {
        if (!_workspace.HasWorkingDirectory || IsPushing) return;
        IsPushing = true;
        try
        {
            string root = _workspace.WorkingDirectory;
            var besides = ChatVM.CurrentSession?.ExtraFolders.ToList() ?? new List<string>();
            StatusMessage = T("ide.pushChecking");
            var repos = await GitMultiRepo.DiscoverAsync(root, besides);
            if (repos.Count == 0) { StatusMessage = T("ide.pushNoRepo"); return; }

            var plans = new List<(GitMultiRepo.RepoState State, IReadOnlyList<string>? Args, GitMultiRepo.Outcome Outcome, string Reason)>();
            foreach (var repo in repos)
            {
                var state = await GitMultiRepo.StateAsync(repo);
                var (args, outcome, reason) = GitMultiRepo.Plan(state);
                plans.Add((state, args, outcome, reason));
            }
            int toPush = plans.Count(p => p.Args != null);
            var body = new StringBuilder();
            foreach (var p in plans)
            {
                string mark = p.Args != null ? "⬆" : p.Outcome == GitMultiRepo.Outcome.UpToDate ? "=" : "–";
                body.AppendLine($"{mark} {p.State.Path}{(p.State.Branch != null ? $" ({p.State.Branch})" : "")}: {p.Reason}");
            }
            if (toPush == 0)
            {
                StatusMessage = T("ide.pushNothing");
                ChoiceDialog.Show(T("ide.pushTitle"), body.ToString().TrimEnd(), new[] { T("ide.ok") });
                return;
            }
            int choice = ChoiceDialog.Show(T("ide.pushTitle"), body.ToString().TrimEnd(),
                new[] { T("ide.pushConfirmButton", toPush), T("ide.cancel") });
            if (choice != 0) { StatusMessage = T("ide.pushCancelled"); return; }

            StatusMessage = T("ide.pushing", toPush);
            var results = new List<GitMultiRepo.PushResult>();
            foreach (var p in plans)
                results.Add(p.Args != null
                    ? await GitMultiRepo.PushOneAsync(p.State.Path)
                    : new GitMultiRepo.PushResult(p.State.Path, p.State.Branch, p.Outcome, p.Reason));
            int pushed = results.Count(r => r.Outcome == GitMultiRepo.Outcome.Pushed);
            int failed = results.Count(r => r.Outcome == GitMultiRepo.Outcome.Failed);
            StatusMessage = failed == 0 ? T("ide.pushDone", pushed) : T("ide.pushSomeFailed", pushed, failed);
            if (failed > 0) ChoiceDialog.Show(T("ide.pushTitle"), GitMultiRepo.Summary(results), new[] { T("ide.ok") });
            _ = RefreshGitAsync();
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "push all failed", ex);
            StatusMessage = T("ide.pushError", ex.Message);
        }
        finally { IsPushing = false; }
    }
}
