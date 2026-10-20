using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;

namespace CluadeX.ViewModels;

/// <summary>
/// The editor follows the files on disk (owner 2026-10-20: "ไม่จำเป็นต้องสลับไปดูแบบ real time อัตโนมัติแล้ว แต่ให้อัพเดท
/// ตามกันจริงๆ เมื่อเขียนไฟล์"). The live-follow of an agent edit only ran for the agent's own write tools; a file the AI
/// changed through a shell command, the UI Studio wrote, or another program saved stayed stale in its open tab until a
/// branch switch. Now one watcher on the project: an open, clean tab takes the new text once the write has settled
/// (the view applies it as a small replace — caret and scroll stay); a tab with unsaved typing is never touched, its
/// Save asks first (overwrite / reload / compare) as before.
///
/// And while an AI run works in this project the editor is read-only (owner 2026-10-20: "แก้ไขใน editor ก็ได้ แต่ต้อง
/// เอไอไม่ทำงานอยู่") — typing into a file the agent is rewriting is how one of the two copies gets lost.
/// </summary>
public partial class CodeEditorViewModel
{
    private FileSystemWatcher? _diskWatcher;
    private string? _diskWatchRoot;
    private readonly ConcurrentDictionary<string, byte> _diskChanged = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _diskDebounce;
    private bool _diskReloading;

    /// <summary>Folders whose changes never matter to an open tab (installed packages, build output, VCS data).</summary>
    private static readonly HashSet<string> DiskIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", ".dart_tool", "build", "dist", "vendor", ".next", ".nuxt", "__pycache__",
    };

    /// <summary>Watch the window's project (called at start and whenever the project changes). UI thread.</summary>
    private void WatchProjectFiles()
    {
        string? root = _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : null;
        if (string.Equals(root, _diskWatchRoot, StringComparison.OrdinalIgnoreCase) && _diskWatcher != null) return;
        StopWatchingProjectFiles();
        _diskWatchRoot = root;
        if (root == null || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (_, e) => NoteDiskChange(root, e.FullPath);
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, e) => NoteDiskChange(root, e.FullPath);   // an atomic save lands as a rename onto the file
            w.Error += (_, _) => NoteDiskChange(root, "*");             // overflow: look at every open tab
            w.EnableRaisingEvents = true;
            _diskWatcher = w;
        }
        catch (Exception ex) { _log?.Warn("Editor", "watching the project's files failed", ex); }
    }

    private void StopWatchingProjectFiles()
    {
        if (_diskWatcher != null)
        {
            try { _diskWatcher.EnableRaisingEvents = false; _diskWatcher.Dispose(); } catch { }
            _diskWatcher = null;
        }
        _diskChanged.Clear();
    }

    /// <summary>The watcher's thread: note the path (cheap), the dispatcher batches them.</summary>
    private void NoteDiskChange(string root, string path)
    {
        if (path != "*" && !IsWatchedPath(root, path)) return;
        _diskChanged[path] = 0;
        App.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _diskDebounce ??= NewDiskDebounce();
            _diskDebounce.Stop();
            _diskDebounce.Start();
        });
    }

    /// <summary>A path inside <paramref name="root"/> that is not under an ignored folder. Pure.</summary>
    public static bool IsWatchedPath(string root, string path)
    {
        if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var seg in path[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (DiskIgnoredDirs.Contains(seg)) return false;
        return true;
    }

    private DispatcherTimer NewDiskDebounce()
    {
        // A write lands in bursts (truncate, write, rename); 350 ms after the last event the file is whole.
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += async (_, _) => { t.Stop(); await ReloadChangedTabsAsync(); };
        return t;
    }

    /// <summary>Open tabs whose file changed on disk: a clean one takes the new text, a dirty one is left as typed.</summary>
    private async Task ReloadChangedTabsAsync()
    {
        if (_diskReloading) { _diskDebounce?.Start(); return; }   // one pass at a time; the next tick picks up the rest
        _diskReloading = true;
        try
        {
            var changed = _diskChanged.Keys.ToList();
            foreach (var k in changed) _diskChanged.TryRemove(k, out _);
            bool all = changed.Contains("*");
            var set = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
            foreach (var tab in Tabs.Where(t => all || set.Contains(t.FullPath)).ToList())
            {
                if (IsAgentTypingInto(tab)) continue;   // the reveal ends on the final text itself
                if (!File.Exists(tab.FullPath)) continue;   // mid-replace, or deleted: the tab keeps what it shows
                var fresh = await _workspace.OpenFileAsync(tab.FullPath);   // read off the UI thread
                if (fresh == null) continue;
                if (!Tabs.Contains(tab) || IsAgentTypingInto(tab)) continue;
                if (string.Equals(fresh.Content, tab.Content, StringComparison.Ordinal))
                {
                    if (!tab.IsDirty) _workspace.AdoptDiskState(tab, fresh);   // our own save, or the same text again
                    continue;
                }
                if (tab.IsDirty)
                {
                    StatusMessage = T("ide.changedOnDiskDirty", tab.FileName);
                    continue;
                }
                tab.MarkOpened(fresh.Content);
                _workspace.AdoptDiskState(tab, fresh);
                if (ReferenceEquals(tab, ActiveTab)) StatusMessage = T("ide.changedOnDisk", tab.FileName);
            }
        }
        catch (Exception ex) { _log?.Warn("Editor", "reloading tabs changed on disk failed", ex); }
        finally { _diskReloading = false; }
    }

    // ─── read-only while an AI run works in this project ───

    private bool _lockedByAgent;
    /// <summary>An AI run is working in the window's project: the editor shows the files but does not take typing.</summary>
    public bool IsLockedByAgent
    {
        get => _lockedByAgent;
        private set { if (_lockedByAgent != value) { _lockedByAgent = value; OnPropertyChanged(); } }
    }

    private void RefreshAgentLock() =>
        IsLockedByAgent = _workspace.HasWorkingDirectory && ChatVM.IsAgentWorkingIn(_workspace.WorkingDirectory);

    /// <summary>Hooked once from the constructor: the project's watcher and the run lock follow the chat.</summary>
    private void HookDiskSync()
    {
        WatchProjectFiles();
        RefreshAgentLock();
        ChatVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.WorkingDirectory))
            {
                WatchProjectFiles();
                RefreshAgentLock();
            }
            else if (e.PropertyName is nameof(ChatViewModel.IsGenerating) or nameof(ChatViewModel.HasRunningElsewhere))
                RefreshAgentLock();
        };
    }
}
