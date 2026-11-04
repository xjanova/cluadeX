using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace EditorTest;

/// <summary>
/// SC*  the Source Control panel (owner 2026-11-04: "ทำให้ชัดเจนขึ้น เครื่องมือมากขึ้นหน่อย สมเป็นโปรแกรมชั้นเลิศ"): the git
///      argument builders, ahead/behind parsing, Staged / Changes grouping, commit-only-staged, discard (Cancel is the
///      default and does nothing; confirmed it restores tracked files and recycles only the listed new files), the
///      subject hint, the AI draft (capped, secrets withheld, never commits, cancellable, dropped after a project
///      switch), one operation at a time, Thai + English keys, and the real panel rendered headless.
/// Every repo is a real temporary git repository; the user's Recycle Bin and global git config are never touched.
/// </summary>
internal static partial class Program
{
    static object? CallPrivate(object o, string method, params object?[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    static Task CallAsync(object o, string method, params object?[] args) => (Task)CallPrivate(o, method, args)!;

    /// <summary>A repo whose commits never depend on the machine's global git config (identity, signing, hooks).</summary>
    static string NewRepo(params string[] parts)
    {
        string dir = Dir(parts);
        Git(dir, "init", "-q", "-b", "main");
        foreach (var (k, v) in new[] { ("user.name", "editortest"), ("user.email", "t@example.invalid"), ("core.autocrlf", "false"),
                                       ("commit.gpgsign", "false"), ("core.hooksPath", ".git/no-hooks") })
            Git(dir, "config", k, v);
        return dir;
    }

    static string GitOut(string cwd, params string[] args) => RunProcess("git", args, cwd).output;

    /// <summary>An STA thread with a running dispatcher: awaits resume on it, so collections bound to a view change on
    /// the view's own thread (as in the app).</summary>
    static Task OnStaDispatcher(Func<Task> body)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Task work;
            try { work = body(); }
            catch (Exception ex) { done.SetException(ex); return; }
            work.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (work.IsFaulted) done.SetException(work.Exception!.InnerExceptions);
            else done.SetResult();
            dispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    static async Task GitPanelChecks(IServiceProvider provider, FileSystemService fs, LocalizationService loc, string repoRoot)
    {
        Console.WriteLine("\n── source control panel ──");
        var vm = provider.GetRequiredService<CodeEditorViewModel>();
        var priorDialog = ChoiceDialog.OverrideWithDefault;
        var priorRecycle = CodeEditorViewModel.RecycleOverride;
        string priorDir = fs.WorkingDirectory;
        // A bound view on another thread must never see a collection change from this one.
        foreach (var c in new System.Collections.IEnumerable[] { vm.GitChanges, vm.GitStagedChanges, vm.GitUnstagedChanges, vm.GitBranches })
            BindingOperations.EnableCollectionSynchronization(c, new object());
        try
        {
            await Case("SC1", () =>
            {
                var hostile = new[] { "a b.txt", "-rf", "*.cs", "R&D/ไฟล์.md", "a[1].txt" };
                var stage = GitService.PanelArgs.Stage(hostile);
                var unstage = GitService.PanelArgs.Unstage(hostile, noCommits: false);
                var unstageNew = GitService.PanelArgs.Unstage(hostile, noCommits: true);
                var discard = GitService.PanelArgs.DiscardTracked(hostile);
                bool Shape(IReadOnlyList<IReadOnlyList<string>> calls, params string[] verb) =>
                    calls.Count == 1 && calls[0][0] == "--literal-pathspecs" && calls[0].Skip(1).Take(verb.Length).SequenceEqual(verb)
                    && calls[0][verb.Length + 1] == "--" && calls[0].Skip(verb.Length + 2).SequenceEqual(hostile);
                Check("SC1", "argument builders: every path is its own argv entry after --literal-pathspecs … -- ('-rf', '*.cs', 'a[1].txt' are file names)",
                    Shape(stage, "add") && Shape(unstage, "restore", "--staged") && Shape(unstageNew, "rm", "--cached", "-q")
                    && Shape(discard, "restore", "--worktree"),
                    string.Join(" | ", stage[0]) + " ;; " + string.Join(" | ", discard[0]));

                var many = Enumerable.Range(0, 2000).Select(i => $"src/folder-{i:D4}/file-name-{i:D4}.cs").ToList();
                var chunks = GitService.PanelArgs.Stage(many.Concat(many.Take(10)));
                var flat = chunks.SelectMany(c => c.SkipWhile(a => a != "--").Skip(1)).ToList();
                Check("SC1b", $"2,000 paths split into {chunks.Count} calls, each under the command-line budget, every path once",
                    chunks.Count > 1 && chunks.All(c => c.SkipWhile(a => a != "--").Skip(1).Sum(p => p.Length + 3) <= GitService.PanelArgs.MaxPathChars)
                    && flat.SequenceEqual(many), $"{chunks.Count} calls, {flat.Count} paths");

                var everything = new[] { GitService.PanelArgs.Fetch(), GitService.PanelArgs.PullFastForward(), GitService.PanelArgs.StashPush("x"),
                                         GitService.PanelArgs.StashPop(), GitService.PanelArgs.CreateBranch("b"), GitService.PanelArgs.CommitFromFile("f") }
                    .Concat(stage).Concat(unstage).Concat(discard).SelectMany(a => a).ToList();
                Check("SC1c", "pull is --ff-only; nothing the panel builds forces, resets or cleans",
                    GitService.PanelArgs.PullFastForward().Contains("--ff-only")
                    && !everything.Any(a => a is "--force" or "-f" or "reset" or "clean" or "--hard" or "-D"), string.Join(" ", everything.Distinct()));

                bool unsafeRefused = new[] { "../evil.txt", @"C:\Windows\x", ".git/config", "a/../../b", "", ".GIT/HEAD" }
                    .All(p => GitService.SafeRepoPath(@"C:\repo", p) == null);
                Check("SC1d", "a path outside the repository (or into .git) is never resolved for a delete",
                    unsafeRefused && GitService.SafeRepoPath(@"C:\repo", "src/a b.cs") == @"C:\repo\src\a b.cs");
                Check("SC1e", "untracked files are discarded only where a Recycle Bin exists: a network share has none (it would be a permanent delete)",
                    !CodeEditorViewModel.HasRecycleBin(@"\\server\share\repo") && CodeEditorViewModel.HasRecycleBin(Path.GetTempPath()));
                return Task.CompletedTask;
            });

            await Case("SC2", async () =>
            {
                var cases = new (string Line, string Up, int A, int B, bool Gone)[]
                {
                    ("## main...origin/main [ahead 2, behind 1]", "origin/main", 2, 1, false),
                    ("## release/1.2...upstream/release/1.2 [behind 3]", "upstream/release/1.2", 0, 3, false),
                    ("## feat...origin/feat [gone]", "origin/feat", 0, 0, true),
                    ("## solo", "", 0, 0, false),
                    ("## main...origin/main", "origin/main", 0, 0, false),
                };
                var wrong = new List<string>();
                foreach (var c in cases)
                {
                    var s = new GitStatusSnapshot();
                    GitService.ParsePorcelainZ(c.Line + "\0", s);
                    if (s.Upstream != c.Up || s.Ahead != c.A || s.Behind != c.B || s.UpstreamGone != c.Gone)
                        wrong.Add($"{c.Line} → up='{s.Upstream}' +{s.Ahead} -{s.Behind} gone={s.UpstreamGone} branch={s.Branch}");
                }
                Check("SC2", "branch line: upstream, ↑ahead ↓behind, [gone] and 'no upstream' parse", wrong.Count == 0, string.Join("\n", wrong));

                // Real: a bare remote, two clones, each a commit ahead → ↑1 ↓1 after a fetch.
                string remote = Dir("scm", "remote.git");
                Git(remote, "init", "-q", "--bare", "-b", "main");
                string work = NewRepo("scm", "work");
                File.WriteAllText(Path.Combine(work, "a.txt"), "a\n");
                Git(work, "add", "-A"); Git(work, "commit", "-q", "-m", "init");
                Git(work, "remote", "add", "origin", remote); Git(work, "push", "-q", "-u", "origin", "main");
                string other = Path.Combine(Scratch, "scm", "other");
                RunProcess("git", new[] { "clone", "-q", remote, other }, Scratch);
                Git(other, "config", "user.name", "o"); Git(other, "config", "user.email", "o@example.invalid"); Git(other, "config", "commit.gpgsign", "false");
                File.WriteAllText(Path.Combine(other, "b.txt"), "b\n");
                Git(other, "add", "-A"); Git(other, "commit", "-q", "-m", "theirs"); Git(other, "push", "-q");
                File.WriteAllText(Path.Combine(work, "c.txt"), "c\n");
                Git(work, "add", "-A"); Git(work, "commit", "-q", "-m", "mine");
                var fetch = await GitService.RunIn(work, GitService.PanelArgs.Fetch(), 60000);
                fs.WorkingDirectory = work;
                await vm.RefreshGitAsync();
                Check("SC2b", "a real repo one commit each side: the panel shows ↑1 ↓1 against origin/main after Fetch",
                    fetch.Success && vm.GitUpstream == "origin/main" && vm.GitAhead == 1 && vm.GitBehind == 1 && vm.GitSyncText == "↑1 ↓1",
                    $"fetch={fetch.Success} {fetch.Error} up={vm.GitUpstream} +{vm.GitAhead} -{vm.GitBehind} '{vm.GitSyncText}'");
            });

            await Case("SC3", () =>
            {
                var s = new GitStatusSnapshot { RepoRoot = @"C:\repo" };
                GitService.ParsePorcelainZ("## main\0M  staged.cs\0 M edited.cs\0MM both.cs\0A  new.cs\0?? untracked.txt\0R  new name.cs\0old name.cs\0UU conflict.cs\0 D gone.cs\0AD added-then-deleted.cs\0", s);
                var (staged, changes) = GitService.GroupChanges(s.Entries);
                string S(IEnumerable<GitPanelRow> rows) => string.Join(" ", rows.Select(r => $"{r.Letter}:{r.Entry.RepoPath}"));
                Check("SC3", "porcelain → Staged (index column) and Changes (working tree); MM is in both; untracked U, conflict !",
                    S(staged) == "M:staged.cs M:both.cs A:new.cs R:new name.cs A:added-then-deleted.cs"
                    && S(changes) == "M:edited.cs M:both.cs U:untracked.txt !:conflict.cs D:gone.cs D:added-then-deleted.cs"
                    && changes.Single(r => r.Letter == "U").Kind == GitChangeKind.Untracked
                    && changes.Single(r => r.Letter == "!").Kind == GitChangeKind.Conflict
                    && staged.Single(r => r.Letter == "R").Entry.OrigRepoPath == "old name.cs",
                    "staged: " + S(staged) + "\nchanges: " + S(changes));
                return Task.CompletedTask;
            });

            await Case("SC4", async () =>
            {
                string repo = NewRepo("scm", "commit");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "a1\n");
                File.WriteAllText(Path.Combine(repo, "b.txt"), "b1\n");
                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "init");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "a2\n");
                File.WriteAllText(Path.Combine(repo, "b.txt"), "b2\n");
                fs.WorkingDirectory = repo;
                await vm.RefreshGitAsync();
                var rowA = vm.GitUnstagedChanges.FirstOrDefault(i => i.RepoPath == "a.txt");
                await CallAsync(vm, "StageAsync", rowA, true);
                bool stagedA = vm.GitStagedChanges.Count == 1 && vm.GitStagedChanges[0].RepoPath == "a.txt" && vm.GitUnstagedChanges.Count == 1;

                string? shown = null; string? button = null;
                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => { shown = m; button = b[0]; return 0; };
                vm.CommitMessage = "only a";
                await CallAsync(vm, "CommitCoreAsync", false);
                string inHead = GitOut(repo, "show", "--name-only", "--format=", "HEAD").Trim();
                string left = GitOut(repo, "status", "--porcelain").Trim();
                Check("SC4", "something staged → only the staged file is committed; the other edit stays unstaged; the dialog says so",
                    stagedA && inHead == "a.txt" && left == "M b.txt" && vm.CommitMessage == ""
                    && (shown ?? "").Contains("Only the staged files are committed") && !(shown ?? "").Contains("git add ."),
                    $"stagedA={stagedA} head=[{inHead}] left=[{left}] msg='{vm.CommitMessage}'\n{shown}");

                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => { shown = m; return 0; };
                vm.CommitMessage = "the rest";
                await CallAsync(vm, "CommitCoreAsync", false);
                Check("SC4b", "nothing staged → today's behaviour: everything is staged (the dialog says git add .) and committed",
                    GitOut(repo, "show", "--name-only", "--format=", "HEAD").Trim() == "b.txt" && GitOut(repo, "status", "--porcelain").Trim() == ""
                    && (shown ?? "").Contains("git add .") && vm.IsGitClean, shown ?? "");

                File.WriteAllText(Path.Combine(repo, "a.txt"), "a3\n");
                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => d;   // Enter
                vm.CommitMessage = "never";
                File.WriteAllText(Path.Combine(repo, ".env"), "TOKEN=x\n");
                string before = GitOut(repo, "rev-parse", "HEAD").Trim();
                await CallAsync(vm, "CommitCoreAsync", false);
                Check("SC4c", "a secret-looking file in the list makes Cancel the default: Enter commits nothing",
                    GitOut(repo, "rev-parse", "HEAD").Trim() == before && vm.CommitMessage == "never");
                File.Delete(Path.Combine(repo, ".env"));
                vm.CommitMessage = "";
            });

            await Case("SC5", async () =>
            {
                string repo = NewRepo("scm", "discard");
                File.WriteAllText(Path.Combine(repo, "x.txt"), "committed\n");
                File.WriteAllText(Path.Combine(repo, "y.txt"), "y1\n");
                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "init");
                File.WriteAllText(Path.Combine(repo, "x.txt"), "edited\n");
                File.WriteAllText(Path.Combine(repo, "y.txt"), "y2 staged\n");
                Git(repo, "add", "y.txt");
                File.WriteAllText(Path.Combine(repo, "new file.txt"), "new\n");
                Directory.CreateDirectory(Path.Combine(repo, "newdir"));
                File.WriteAllText(Path.Combine(repo, "newdir", "one.txt"), "1\n");
                File.WriteAllText(Path.Combine(repo, "newdir", "two.txt"), "2\n");
                fs.WorkingDirectory = repo;
                await vm.RefreshGitAsync();

                var asked = new List<(string Msg, IReadOnlyList<string> Buttons, int Default)>();
                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => { asked.Add((m, b, d)); return d; };   // the user presses Enter
                var recycled = new List<string>();
                CodeEditorViewModel.RecycleOverride = p => { recycled.Add(p); File.Delete(p); return true; };
                await CallAsync(vm, "DiscardAsync", new object?[] { null });
                bool untouched = File.ReadAllText(Path.Combine(repo, "x.txt")) == "edited\n" && File.Exists(Path.Combine(repo, "new file.txt"))
                                 && File.Exists(Path.Combine(repo, "newdir", "one.txt")) && recycled.Count == 0;
                var q = asked.FirstOrDefault();
                Check("SC5", "Discard all asks, lists every file (a new folder's files one by one), and Cancel is the default: Enter changes nothing",
                    asked.Count == 1 && q.Default == q.Buttons.Count - 1 && q.Buttons.Count == 2 && untouched
                    && q.Msg.Contains("x.txt") && q.Msg.Contains("new file.txt") && q.Msg.Contains("newdir/one.txt") && q.Msg.Contains("newdir/two.txt")
                    && q.Msg.Contains("Recycle Bin") && !q.Msg.Contains("y.txt"),
                    $"asked={asked.Count} default={q.Default}/{q.Buttons?.Count} untouched={untouched}\n{q.Msg}");

                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => b.Count - 1;   // Esc
                await CallAsync(vm, "DiscardAsync", new object?[] { null });
                Check("SC5b", "…Esc changes nothing either",
                    File.ReadAllText(Path.Combine(repo, "x.txt")) == "edited\n" && File.Exists(Path.Combine(repo, "new file.txt")) && recycled.Count == 0);

                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => 0;            // the Discard button
                await CallAsync(vm, "DiscardAsync", new object?[] { null });
                string status = GitOut(repo, "-c", "core.quotepath=off", "status", "--porcelain").Trim();
                Check("SC5c", "confirmed: tracked edits restored, ONLY the listed new files recycled, the staged change kept",
                    File.ReadAllText(Path.Combine(repo, "x.txt")) == "committed\n" && !File.Exists(Path.Combine(repo, "new file.txt"))
                    && recycled.Count == 3 && recycled.All(p => p.StartsWith(repo, StringComparison.OrdinalIgnoreCase))
                    && status == "M  y.txt" && File.ReadAllText(Path.Combine(repo, "y.txt")) == "y2 staged\n",
                    $"status=[{status}] recycled={string.Join(", ", recycled)}");
            });

            await Case("SC6", () =>
            {
                string at72 = new string('a', 72), at73 = new string('b', 73);
                bool pure = GitService.CommitSubjectLength("fix\nbody that is very long " + at73) == 3
                            && GitService.CommitSubjectLength(at72 + "\r\nmore") == 72 && GitService.CommitSubjectLength("") == 0;
                vm.CommitMessage = at72 + "\n\nbody";
                bool ok72 = !vm.IsCommitSubjectLong && vm.CommitSubjectHint == "";
                vm.CommitMessage = at73;
                bool long73 = vm.IsCommitSubjectLong && vm.CommitSubjectHint.Contains("73") && vm.CommitSubjectHint.Contains("72");
                vm.CommitMessage = "";
                Check("SC6", "subject hint: line 1 only, silent at 72, shown past 72 (with the count)", pure && ok72 && long73, vm.CommitSubjectHint);
                return Task.CompletedTask;
            });

            await Case("SC7", async () =>
            {
                string repo = NewRepo("scm", "draft");
                File.WriteAllText(Path.Combine(repo, "big.txt"), "old\n");
                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "init");
                File.WriteAllText(Path.Combine(repo, "big.txt"), string.Concat(Enumerable.Range(0, 6000).Select(i => $"line {i} of the new text\n")));
                File.WriteAllText(Path.Combine(repo, ".env"), "API_KEY=sk-should-never-leave-1234\n");
                Git(repo, "add", "-A");
                fs.WorkingDirectory = repo;
                await vm.RefreshGitAsync();
                string head = GitOut(repo, "rev-parse", "HEAD").Trim();
                string? user = null;
                vm.CommitDraftModelOverride = (sys, u, ct) => { user = u; return Task.FromResult("```\nAdd the new text to big.txt\n```"); };
                vm.CommitMessage = "";
                await CallAsync(vm, "DraftCommitMessageAsync");
                Check("SC7", "✨ draft: fills the box (fences stripped), never commits, diff capped, a secret file's content never sent",
                    vm.CommitMessage == "Add the new text to big.txt" && GitOut(repo, "rev-parse", "HEAD").Trim() == head
                    && user != null && user.Length <= CodeEditorViewModel.MaxDraftDiffChars + 1500 && user.Contains("[diff cut here")
                    && user.Contains(".env") && !user.Contains("sk-should-never-leave"),
                    $"msg='{vm.CommitMessage}' len={user?.Length}");

                vm.CommitMessage = "";
                vm.CommitDraftModelOverride = async (sys, u, ct) => { await Task.Delay(Timeout.Infinite, ct); return "late"; };
                var running = CallAsync(vm, "DraftCommitMessageAsync");
                await Task.Delay(300);
                bool wasDrafting = vm.IsDraftingCommit;
                vm.CancelDraftCommand.Execute(null);
                await running;
                Check("SC7b", "…Stop cancels it: nothing written, a localized 'stopped' line, the button is back",
                    wasDrafting && !vm.IsDraftingCommit && vm.CommitMessage == "" && vm.StatusMessage == loc.T("scm.aiCancelled"), vm.StatusMessage);

                string elsewhere = NewRepo("scm", "draft-other");
                vm.CommitDraftModelOverride = (sys, u, ct) => { fs.WorkingDirectory = elsewhere; return Task.FromResult("Describes the OLD project"); };
                await CallAsync(vm, "DraftCommitMessageAsync");
                Check("SC7c", "…a draft that finishes after another project was opened is dropped (the box is not filled)",
                    vm.CommitMessage == "", vm.CommitMessage);

                fs.WorkingDirectory = repo;
                vm.CommitDraftModelOverride = (sys, u, ct) => throw new InvalidOperationException("Exception: provider exploded at 0xDEAD");
                await CallAsync(vm, "DraftCommitMessageAsync");
                Check("SC7d", "…a failing provider is a localized line, never the exception text",
                    vm.StatusMessage == loc.T("scm.aiFailed") && !vm.StatusMessage.Contains("Exception"), vm.StatusMessage);
                vm.CommitDraftModelOverride = null;
            });

            await Case("SC8", async () =>
            {
                string repo = NewRepo("scm", "busy");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "init");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "changed\n");
                string other = NewRepo("scm", "busy-other");
                fs.WorkingDirectory = repo;
                await vm.RefreshGitAsync();

                string? busyLine = null;
                vm.AskBranchNameOverride = () =>
                {
                    // A second operation while the first waits for the user: refused, says what is running.
                    var second = (Task)CallPrivate(vm, "StageAsync", null, true)!;
                    second.GetAwaiter().GetResult();
                    busyLine = vm.StatusMessage;
                    fs.WorkingDirectory = other;   // …and the user opens another project mid-operation
                    return "feature/panel";
                };
                await CallAsync(vm, "NewBranchAsync");
                vm.AskBranchNameOverride = null;
                string branchHere = GitOut(repo, "branch", "--list", "feature/panel").Trim();
                string branchThere = GitOut(other, "branch", "--list", "feature/panel").Trim();
                Check("SC8", "one git operation at a time: a second one is refused with 'wait — …' and stages nothing",
                    busyLine == loc.T("scm.busy", loc.T("scm.busy.branch")) && GitOut(repo, "diff", "--cached", "--name-only").Trim() == "",
                    busyLine ?? "");
                Check("SC8b", "an operation acts on the repo it started in, even after a project switch, and its result line is dropped",
                    branchHere.Contains("feature/panel") && branchThere == "" && vm.StatusMessage == busyLine && !vm.IsGitBusy,
                    $"here=[{branchHere}] there=[{branchThere}] status={vm.StatusMessage}");
            });

            await Case("SC9", async () =>
            {
                string plain = Dir("scm", "plain folder");
                fs.WorkingDirectory = plain;
                await vm.RefreshGitAsync();
                bool notRepo = vm.IsNotGitRepo && !vm.HasGitRepo && !vm.IsGitMissing && vm.GitStagedChanges.Count == 0;
                ChoiceDialog.OverrideWithDefault = (t, m, b, d) => 0;
                await CallAsync(vm, "InitRepoAsync");
                bool inited = Directory.Exists(Path.Combine(plain, ".git")) && vm.HasGitRepo && !vm.IsNotGitRepo;

                string clean = NewRepo("scm", "clean");
                File.WriteAllText(Path.Combine(clean, "a.txt"), "a\n");
                Git(clean, "add", "-A"); Git(clean, "commit", "-q", "-m", "first commit subject");
                fs.WorkingDirectory = clean;
                await vm.RefreshGitAsync();
                Check("SC9", "not a repo → explained, Initialize (confirmed) makes one; a clean repo shows its last commit",
                    notRepo && inited && vm.IsGitClean && vm.GitLastCommitSubject == "first commit subject"
                    && vm.GitLastCommitHash.Length >= 7 && vm.GitLastCommitWhen == loc.T("scm.time.now") && vm.GitSyncText == loc.T("scm.noUpstream"),
                    $"notRepo={notRepo} inited={inited} clean={vm.IsGitClean} '{vm.GitLastCommitHash} {vm.GitLastCommitSubject} {vm.GitLastCommitWhen}' sync='{vm.GitSyncText}'");
            });

            await Case("SC10", () =>
            {
                string root = Path.Combine(repoRoot, "CluadeX");
                var table = (Dictionary<string, Dictionary<string, string>>)typeof(LocalizationService).Assembly
                    .GetType("CluadeX.Services.Localization.GitPanelKeys")!.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                string code = File.ReadAllText(Path.Combine(root, "ViewModels", "CodeEditorViewModel.GitPanel.cs"))
                              + File.ReadAllText(Path.Combine(root, "ViewModels", "CodeEditorViewModel.cs"))
                              + File.ReadAllText(Path.Combine(root, "ViewModels", "CodeEditorViewModel.Push.cs"))
                              + File.ReadAllText(Path.Combine(root, "Views", "Editor", "SourceControlPanel.xaml.cs"));
                string xaml = File.ReadAllText(Path.Combine(root, "Views", "Editor", "SourceControlPanel.xaml"));
                var used = Regex.Matches(code, @"""(scm\.[\w.]+)""").Select(m => m.Groups[1].Value)
                    .Concat(Regex.Matches(xaml, @"Loc (scm\.[\w.]+)").Select(m => m.Groups[1].Value))
                    .Where(k => !k.EndsWith('.'))
                    .Concat(Enum.GetNames<GitChangeKind>().Select(n => "scm.kind." + n))
                    .Concat(Enum.GetNames<GitFailure>().Select(n => "scm.fail." + n))
                    .Distinct().ToList();
                var incomplete = table.Where(p => !p.Value.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en)
                                                  || !p.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th)).Select(p => p.Key).ToList();
                loc.SetLanguage("th");
                int thai = table.Keys.Count(k => Regex.IsMatch(loc.T(k), "[฀-๿]"));
                var unregistered = table.Keys.Where(k => loc.T(k) == k).ToList();
                string placeholder = loc.T("scm.commitPlaceholder");
                loc.SetLanguage("en");
                var undeclared = used.Where(k => !table.ContainsKey(k)).ToList();
                var formatBad = table.Where(p => Regex.Matches(p.Value["en"], @"\{(\d)").Select(m => m.Groups[1].Value).Distinct().Count()
                                                 != Regex.Matches(p.Value["th"], @"\{(\d)").Select(m => m.Groups[1].Value).Distinct().Count())
                    .Select(p => p.Key).ToList();
                Check("SC10", $"source-control keys: {table.Count} declared, all Thai + English, registered ({thai} show Thai); the {used.Count} the panel names all exist; placeholders match",
                    table.Count > 150 && incomplete.Count == 0 && unregistered.Count == 0 && undeclared.Count == 0 && formatBad.Count == 0
                    && thai > table.Count * 0.7 && placeholder == "ข้อความคอมมิต — Ctrl+Enter เพื่อคอมมิต",
                    "incomplete: " + string.Join(", ", incomplete) + " | unregistered: " + string.Join(", ", unregistered)
                    + " | undeclared: " + string.Join(", ", undeclared) + " | {n} mismatch: " + string.Join(", ", formatBad));
                Check("SC10b", "no user-facing text in the panel's XAML is hard-coded (every Text/ToolTip/Header is a binding or a glyph)",
                    !Regex.IsMatch(xaml, @"\s(Text|ToolTip|Header)=""(?!\{)[^""&#]*[A-Za-zก-๙]{2,}"), "");
                return Task.CompletedTask;
            });

            // Last: the real panel, rendered on an STA dispatcher thread (no app, no window).
            await Case("SC11", () => OnStaDispatcher(async () =>
            {
                string repo = NewRepo("scm", "view");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
                File.WriteAllText(Path.Combine(repo, "b.txt"), "b\n");
                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "view init");
                File.WriteAllText(Path.Combine(repo, "a.txt"), "a2\n");
                File.WriteAllText(Path.Combine(repo, "b.txt"), "b2\n");
                Git(repo, "add", "a.txt");
                fs.WorkingDirectory = repo;
                await vm.RefreshGitAsync();

                var panel = new SourceControlPanel { DataContext = vm };
                var host = new Grid { Width = 320, Height = 700 };
                host.Children.Add(panel);
                void Layout() { host.Measure(new Size(320, 700)); host.Arrange(new Rect(0, 0, 320, 700)); host.UpdateLayout(); }
                Layout();
                FrameworkElement N(string name) => (FrameworkElement)panel.FindName(name);
                // IsVisible needs a window; without one, "shown" is Visibility up to the panel.
                bool Shown(FrameworkElement e)
                {
                    for (DependencyObject? d = e; d != null && d != host; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? (d as FrameworkElement)?.Parent)
                        if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
                    return true;
                }
                int Rows(string group) => FindAll<Button>(N(group)).Count(b => b.Style == panel.Resources["ScmRow"]);
                bool groups = Shown(N("StagedGroup")) && Shown(N("ChangesGroup")) && !Shown(N("CleanState"))
                              && Rows("StagedGroup") == 1 && Rows("ChangesGroup") == 1 && Shown(N("CountBadge")) && Shown(N("BranchChip"));

                vm.CommitMessage = "";
                Layout();
                bool placeholder = FindAll<TextBlock>(N("CommitBoxFrame")).Any(t => Shown(t) && t.Text == loc.T("scm.commitPlaceholder"));

                Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "view clean");
                await vm.RefreshGitAsync();
                Layout();
                bool clean = Shown(N("CleanState")) && !Shown(N("StagedGroup")) && !Shown(N("ChangesGroup")) && !Shown(N("CountBadge"))
                             && FindAll<TextBlock>(N("CleanState")).Any(t => t.Text == loc.T("scm.clean.title"));

                fs.WorkingDirectory = Dir("scm", "view-plain");
                await vm.RefreshGitAsync();
                Layout();
                bool notRepo = Shown(N("NotRepoState")) && !Shown(N("RepoState")) && !Shown(N("BranchChip"));
                Check("SC11", "the real panel: Staged and Changes groups with one row each and the count badge; the placeholder; then 'Working tree clean'; then the not-a-repo state",
                    groups && placeholder && clean && notRepo, $"groups={groups} placeholder={placeholder} clean={clean} notRepo={notRepo}");
                panel.DataContext = null;
            }));
        }
        finally
        {
            ChoiceDialog.OverrideWithDefault = priorDialog;
            CodeEditorViewModel.RecycleOverride = priorRecycle;
            vm.CommitDraftModelOverride = null;
            vm.AskBranchNameOverride = null;
            loc.SetLanguage("en");
            fs.WorkingDirectory = priorDir;
        }
    }

    static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        void Walk(DependencyObject d)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (c is T t) list.Add(t);
                Walk(c);
            }
        }
        Walk(root);
        return list;
    }
}
