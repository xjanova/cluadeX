using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services.UiStudio;

namespace CluadeX.ViewModels;

/// <summary>
/// While an AI run works in the Studio's project the Studio is look-only (owner 2026-11-03: "ai กำลังแก้อยู่เราก็แก้ไขอะไร
/// ไม่ได้จนกว่า เอไอจะใช้เสร็จ"): the page can still be hovered, picked and inspected, but nothing writes — not the inspector,
/// the page's in-place edits, the widget tools, an undo, an undo-all, or a second request to the AI. A banner says why (and
/// when the run waits for the owner's answer in its chat). Any chat's run counts, its main folder or one it opened beside
/// it, the same check that makes the code editor read-only. When the run ends the page is reloaded, the project is looked
/// at again (the AI may have turned a static site into a Vite one) and the screens are drawn again.
/// The hard stop is <see cref="StudioService.WriteBlocked"/>, asked at the moment of every write; the buttons and the
/// overlay only follow it.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private bool _lockedByAi, _lockWaiting;
    private string _lockRoot = "";
    private DispatcherTimer? _lockTimer;
    private bool _reloadAfterLock;
    private ICommand? _openLockChatCommand;

    /// <summary>An AI run is editing this project: the Studio shows it but takes no edit.</summary>
    public bool IsLockedByAi => _lockedByAi;
    /// <summary>For bindings: the inspector and the tools take input.</summary>
    public bool CanEditNow => !_lockedByAi;
    /// <summary>The run editing this project waits for the owner (a permission card, a question) in its chat.</summary>
    public bool AiLockWaiting => _lockWaiting;
    public string AiLockText => _lockWaiting ? T("studio.lock.waiting") : T("studio.lock.banner");

    /// <summary>The banner's button: the chat of the run working here (the one that waits first), on the Chat page.</summary>
    public ICommand OpenLockChatCommand => _openLockChatCommand ??= new AsyncRelayCommand(async () =>
    {
        try { await _chat.ShowRunChatInAsync(_studio.Root); }
        catch (Exception ex) { _log?.Warn("UiStudio", "opening the chat of the run failed", ex); }
        OpenPageRequested?.Invoke("Chat");
    });

    /// <summary>Is a run working in this project right now? (Live: the run list, not the last refresh.) UI thread.</summary>
    private bool AiWorkingHere() => _studio.HasProject && _chat.IsAgentWorkingIn(_studio.Root);

    /// <summary>The Studio's own tools wait for its AI round, and for any run in this project.</summary>
    private bool AiBlocksEdits => AiWorking || AiWorkingHere();

    /// <summary>Hooked once from the constructor (the view model lives as long as the app).</summary>
    private void HookAiLock()
    {
        _studio.WriteBlocked = () => AiWorkingHere() ? "studio.err.aiWorking" : null;
        _chat.PropertyChanged += (_, e) =>
        {
            // every run start and end in any chat raises these (RaiseRunStateChanged); a chat switch moves the project
            if (e.PropertyName is nameof(ChatViewModel.IsGenerating) or nameof(ChatViewModel.HasRunningElsewhere)
                or nameof(ChatViewModel.WorkingDirectory) or nameof(ChatViewModel.ChatProject))
                RefreshAiLock();
        };
        _studio.ProjectChanged += RefreshAiLock;
        RefreshAiLock();
    }

    /// <summary>Read the lock again (UI thread). Public for the harness, which cannot wait for the timer.</summary>
    public void RefreshAiLock()
    {
        bool now = AiWorkingHere();
        bool waiting = now && _chat.IsAgentWaitingIn(_studio.Root);
        if (waiting != _lockWaiting)
        {
            _lockWaiting = waiting;
            OnPropertyChanged(nameof(AiLockWaiting));
            OnPropertyChanged(nameof(AiLockText));
        }
        if (now == _lockedByAi) return;
        string lockedRoot = _lockRoot;
        _lockedByAi = now;
        _lockRoot = now ? _studio.Root : "";
        OnPropertyChanged(nameof(IsLockedByAi));
        OnPropertyChanged(nameof(CanEditNow));
        OnPropertyChanged(nameof(AiLockText));
        CommandManager.InvalidateRequerySuggested();
        // (a page that reloads gets it again on its "ready": every load starts the overlay unlocked)
        _studio.RunScript($"window.__cx && __cx.setLocked && __cx.setLocked({(now ? "true" : "false")})");
        if (now) OnLocked();
        else OnUnlocked(lockedRoot);
    }

    private void OnLocked()
    {
        _log?.Info("UiStudio", $"an AI run is editing {_studio.Root}: the Studio is look-only");
        // Unsaved changes in the inspector are only on the page: the AI may move the very line they would be written to.
        // They go, and the user is told why — never written later over the AI's work.
        if (IsDirty)
        {
            DiscardPreviewEdits();
            Status(T("studio.lock.reverted"), error: true);
        }
        // The waiting line changes without a run starting or ending (a permission card comes up): looked at while locked.
        _lockTimer ??= NewLockTimer();
        _lockTimer.Start();
    }

    private DispatcherTimer NewLockTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { if (_lockedByAi) RefreshAiLock(); else t.Stop(); };
        return t;
    }

    /// <summary>The run is done. Only for the project that was locked — another project shown now (the chat moved) was
    /// never locked and has nothing to catch up on.</summary>
    private void OnUnlocked(string lockedRoot)
    {
        _lockTimer?.Stop();
        if (lockedRoot.Length == 0 || !_studio.HasProject || !SamePath(lockedRoot, _studio.Root)) return;
        _log?.Info("UiStudio", $"the AI run in {lockedRoot} ended: the Studio takes edits again");
        Status(T("studio.lock.done"));
        // the page as the AI left it: a real reload (a hidden page is reloaded when it is shown)
        if (WantsPrewarm && _studio.IsServing)
        {
            if (IsOnScreen || _studio.KeepPageAwake) _studio.ReloadPage(hard: false);
            else _reloadAfterLock = true;
        }
        // a dev server that waited for the run (its npm install would have fought the AI's)
        if (_startOnShow && IsOnScreen) CatchUpOnShow();
        // the screens: code the watchers saw change is drawn again now
        if (_catalog?.Flutter != null && _sourcesChanged)
        {
            _renderDebounce ??= NewRenderDebounce();
            _renderDebounce.Stop();
            _renderDebounce.Start();
        }
        _ = RedetectAfterRunAsync(lockedRoot);
    }

    /// <summary>The page left for the run's end while hidden: reloaded on show (from CatchUpOnShow).</summary>
    private void ReloadAfterLockOnShow()
    {
        if (!_reloadAfterLock) return;
        _reloadAfterLock = false;
        if (_studio.IsServing && WantsPrewarm) _studio.ReloadPage(hard: false);
    }

    /// <summary>The project may be another kind now (a package.json written into a static site): look again off the UI
    /// thread, and start what the new kind shows. Same project, nothing else going on, else nothing changes. The caller
    /// discards this task: everything that can throw is caught here.</summary>
    internal async Task RedetectAfterRunAsync(string root)
    {
        int run;
        StudioProjectKind before;
        try
        {
            if (!_studio.HasProject || !SamePath(_studio.Root, root) || _session != null || IsChecking) return;
            before = _studio.Project.Kind;
            run = ++_redetectRun;
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "looking at the project after the run failed", ex); return; }
        StudioProjectKind kind;
        try { kind = await Task.Run(() => StudioProject.Detect(root).Kind); }
        catch (Exception ex) { _log?.Warn("UiStudio", "detecting the project after the run failed", ex); return; }
        try
        {
            if (run != _redetectRun || kind == before) return;   // a newer look is on its way, or nothing changed
            // the Studio may have moved on while it looked: another project, a round, another run, already detected
            if (!_studio.HasProject || !SamePath(_studio.Root, root) || _session != null || IsChecking || AiWorkingHere()
                || _studio.Project.Kind != before) return;
            _studio.StopServing();      // the old kind's preview (the static server of a site that is Vite now)
            _studio.OpenProject(root);  // the same root: detected again
            _log?.Info("UiStudio", $"after the run {root} is {_studio.Project.Kind} (was {before})");
            if (IsUnsupported) { Status(UnsupportedText, error: true); return; }
            Status(T("studio.lock.kindChanged", ProjectKindText));
            StartFollowed();
        }
        catch (Exception ex) { _log?.Warn("UiStudio", "starting the project after the run failed", ex); }
    }

    /// <summary>Put the inspector back to the saved values and the page with it (the live preview is undone).</summary>
    private void DiscardPreviewEdits()
    {
        _previewTimer.Stop();
        _pendingPreview.Clear();
        _textValue = _textOriginal;
        _classValue = _classOriginal;
        foreach (var f in StyleGroups.SelectMany(g => g.Fields)) f.Load(f.Original);
        foreach (var a in AttrFields) a.Reset();
        foreach (var name in new[] { nameof(TextValue), nameof(ClassValue), nameof(IsDirty) }) OnPropertyChanged(name);
        _studio.RunScript("window.__cx && __cx.revertPreview()");
        CommandManager.InvalidateRequerySuggested();
    }
}
