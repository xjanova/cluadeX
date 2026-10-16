using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// The plan window (<see cref="PlanWindowViewModel"/>): one per chat, reading that chat's plan whether or not it is on
/// screen, sending items the owner adds to its running agent, and going back to / branching from its restore points.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The last plan each chat's agent wrote (a finished run's plan stays readable). UI thread only.</summary>
    private readonly Dictionary<string, List<TodoItem>> _lastPlans = new();
    private readonly Dictionary<string, Views.PlanWindow> _planWindows = new();

    /// <summary>A chat's plan changed (session id). Raised on the UI thread.</summary>
    public event Action<string>? PlanChanged;

    public ICommand OpenPlanWindowCommand => _openPlanWindowCommand ??= new RelayCommand(() => OpenPlanWindow(CurrentSession));
    private ICommand? _openPlanWindowCommand;

    private void RememberPlan(ChatRun? run, List<TodoItem> items)
    {
        string? id = run?.Session.Id ?? CurrentSession?.Id;
        if (id == null) return;
        _lastPlans[id] = items.Select(t => new TodoItem { Content = t.Content, Status = t.Status, CreatedAt = t.CreatedAt }).ToList();
        PlanChanged?.Invoke(id);
    }

    public void OpenPlanWindow(ChatSession? session)
    {
        if (session == null) return;
        if (_planWindows.TryGetValue(session.Id, out var open)) { open.Activate(); return; }
        var vm = new PlanWindowViewModel(this, session.Id, CheckpointService.Current);
        var win = new Views.PlanWindow(vm);
        var owner = App.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != win) win.Owner = owner;
        win.Closed += (_, _) => _planWindows.Remove(session.Id);
        _planWindows[session.Id] = win;
        win.Show();
    }

    public sealed record PlanState(string Title, string Project, bool Running, int Step, IReadOnlyList<TodoItem> Items);

    /// <summary>What the plan window shows for one chat: its running agent's live plan, else the last one written.</summary>
    public PlanState PlanSnapshot(string sessionId)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == sessionId);
        var session = run?.Session ?? Sessions.FirstOrDefault(s => s.Id == sessionId)
                      ?? (CurrentSession?.Id == sessionId ? CurrentSession : null);
        List<TodoItem> items;
        if (run != null)
        {
            // The run's own thread edits this list; a copy that loses a race is simply taken again next tick.
            try { items = run.Scope.Todos.ToList(); } catch { items = new(); }
            if (items.Count == 0 && _lastPlans.TryGetValue(sessionId, out var kept)) items = kept;
        }
        else items = _lastPlans.TryGetValue(sessionId, out var kept) ? kept : new();
        string project = run?.Scope.WorkingDirectory ?? session?.ProjectPath ?? "";
        return new PlanState(session?.Title ?? "", project, run != null, run?.Scope.Step ?? 0, items);
    }

    /// <summary>An item the owner added in the plan window. Running → the agent gets it at its next step and adds it to
    /// its plan (true). Idle → false: the window keeps it for "start".</summary>
    public bool AddPlanItem(string sessionId, string text)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == sessionId);
        if (run == null) return false;
        run.Scope.AddUserNote("Add this to your plan (todo_write), after the items already there, and do it in this turn: " + text);
        return true;
    }

    /// <summary>Queued plan items → the chat's next turn (the chat is brought on screen first).</summary>
    public async Task StartPlanItemsAsync(string sessionId, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        if (CurrentSession?.Id != sessionId) await LoadSessionAsync(sessionId);
        if (CurrentSession?.Id != sessionId) return;
        NavigateToChatRequested?.Invoke();
        UserInput = Loc("plan.startMessage") + "\n" + string.Join("\n", items.Select(i => "- " + i));
        if (SendMessageCommand.CanExecute(null)) SendMessageCommand.Execute(null);
    }

    /// <summary>A line in the chat saying the files went back (so the agent's next turn knows too).</summary>
    public void NotePlanRewind(string sessionId, CheckpointService.Point p, bool ok, int files)
    {
        if (!ok || CurrentSession?.Id != sessionId) return;
        Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⏪ " + LocF("plan.rewind.note", p.Step, p.When, files) });
    }

    /// <summary>
    /// Branch: a git worktree beside the project (from the current work, uncommitted changes included), with the files
    /// put back to how they were before step <paramref name="p"/>, opened in a new chat. The original is not touched.
    /// </summary>
    public async Task<string> BranchFromPointAsync(string sessionId, CheckpointService.Point p)
    {
        var cp = CheckpointService.Current;
        string project = p.Project.TrimEnd('\\', '/');
        if (cp == null || !Directory.Exists(project)) return LocF("plan.branch.failed", "project folder not found");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string target = Path.Combine(Path.GetDirectoryName(project) ?? project, Path.GetFileName(project) + "-branch-" + stamp);
        string branch = "cluadex/branch-" + stamp;
        try
        {
            var result = await Task.Run(() =>
            {
                var inside = Git(project, "rev-parse", "--is-inside-work-tree");
                if (inside.Code != 0 || inside.Out.Trim() != "true") return (false, "not a git repository");
                // `stash create` makes a commit of the working tree without touching it (empty when nothing changed).
                string from = Git(project, "stash", "create").Out.Trim();
                if (from.Length == 0) from = "HEAD";
                var add = Git(project, "worktree", "add", "-b", branch, target, from);
                if (add.Code != 0) return (false, add.Err.Trim());
                var r = cp.RestoreInto(project, p.Id, target);
                return r.Ok ? (true, "") : (false, r.Message);
            });
            if (!result.Item1) return LocF("plan.branch.failed", result.Item2);

            string title = CurrentSession?.Id == sessionId ? CurrentSession.Title
                : Sessions.FirstOrDefault(s => s.Id == sessionId)?.Title ?? "";
            ApplyWorkingDirectory(target, announce: false, showEditor: false);
            NewSession();
            if (CurrentSession != null) CurrentSession.Title = LocF("plan.branch.chatTitle", p.Step, title);
            Messages.Add(new ChatMessage { Role = MessageRole.System,
                Content = "🌿 " + LocF("plan.branch.note", title, p.Step, p.When, target, branch) });
            NavigateToChatRequested?.Invoke();
            return LocF("plan.branch.done", target);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Plan", "branch failed", ex);
            return LocF("plan.branch.failed", ex.Message);
        }
    }

    private static (int Code, string Out, string Err) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                                                UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, output, err.Result);
    }
}
