using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;

namespace CluadeX.ViewModels;

public class McpServersViewModel : ViewModelBase, IDisposable
{
    private readonly McpServerManager _mcpManager;
    private bool _disposed;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;
    private readonly Action<string, McpServerState> _stateHandler;
    private readonly Action _configHandler;

    /// <summary>
    /// Per-server log text, owned here rather than by the list item. Items used to be rebuilt on every state
    /// change, which wiped the log — including the start-failure lines that explain why a server is red.
    /// </summary>
    private readonly Dictionary<string, string> _logBuffers = new(StringComparer.Ordinal);
    private const int LogKeepChars = 10240;

    private McpServerDisplayItem? _selectedServer;
    private string _newServerName = "";
    private string _newServerCommand = "";
    private string _newServerArgs = "";
    private bool _isAddingServer;
    private bool _isEditing;
    private string _statusText = "";

    public ObservableCollection<McpServerDisplayItem> Servers { get; } = new();
    public ObservableCollection<McpTool> SelectedServerTools { get; } = new();

    public McpServerDisplayItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            var previous = _selectedServer;
            if (SetProperty(ref _selectedServer, value))
            {
                // Leaving a server mid-edit: put its fields back to the saved config, so the list never shows
                // values that were not saved.
                if (IsEditing && previous != null) previous.LoadFrom(previous.Original);
                IsEditing = false;
                RefreshSelectedTools();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string NewServerName { get => _newServerName; set => SetProperty(ref _newServerName, value); }
    public string NewServerCommand { get => _newServerCommand; set => SetProperty(ref _newServerCommand, value); }
    public string NewServerArgs { get => _newServerArgs; set => SetProperty(ref _newServerArgs, value); }
    public bool IsAddingServer { get => _isAddingServer; set => SetProperty(ref _isAddingServer, value); }
    public bool IsEditing { get => _isEditing; set => SetProperty(ref _isEditing, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // Localized labels
    public string PageTitle => _loc.T("mcp.title");
    public string PageSubtitle => _loc.T("mcp.subtitle");

    // Commands
    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand RestartServerCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand RemoveServerCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand BeginEditCommand { get; }
    public ICommand SaveEditCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand ShowAddFormCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand StartAllCommand { get; }

    public McpServersViewModel(McpServerManager mcpManager, LocalizationService loc, DebugLogService? log = null)
    {
        _mcpManager = mcpManager;
        _loc = loc;
        _log = log;

        bool HasSel() => SelectedServer != null;
        StartServerCommand = new AsyncRelayCommand(StartSelectedServer,
            () => HasSel() && SelectedServer!.Status is not ("running" or "starting"));
        StopServerCommand = new AsyncRelayCommand(StopSelectedServer,
            () => HasSel() && (SelectedServer!.Status is "running" or "starting" or "reconnecting"));
        RestartServerCommand = new AsyncRelayCommand(RestartSelectedServer, HasSel);
        AddServerCommand = new RelayCommand(AddServer,
            () => !string.IsNullOrWhiteSpace(NewServerName) && !string.IsNullOrWhiteSpace(NewServerCommand));
        RemoveServerCommand = new AsyncRelayCommand(RemoveServer, HasSel);
        ToggleEnabledCommand = new RelayCommand(ToggleEnabled, HasSel);
        BeginEditCommand = new RelayCommand(BeginEdit, () => HasSel() && !IsEditing);
        SaveEditCommand = new RelayCommand(SaveEdit, () => HasSel() && IsEditing);
        CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        ShowAddFormCommand = new RelayCommand(() => IsAddingServer = !IsAddingServer);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        StartAllCommand = new AsyncRelayCommand(StartAllServers);

        _mcpManager.OnServerLog += OnServerLog;
        _mcpManager.OnToolsChanged += OnToolsChanged;
        // A server that crashes and reconnects on its own must move this page's
        // status text without the user pressing Refresh — the page showing "running"
        // for a corpse is the same lie the status bar was fixed to stop telling.
        // BeginInvoke, NOT Invoke: this fires from the Process.Exited callback
        // thread. A blocking hop to the UI thread from there is the same deadlock
        // shape already documented in InstinctsViewModel — the exit thread waits
        // on the UI thread while the UI thread is inside a refresh reading the
        // tool registry the exit path is mutating.
        // It updates the one item IN PLACE: rebuilding the list here is what used to wipe the log, overwrite
        // the error state and throw away an edit in progress every time any server changed state.
        _stateHandler = (name, _) =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnServerStateChanged(name)));
        _mcpManager.OnServerStateChanged += _stateHandler;
        // This page is built BEFORE the startup load (MainViewModel takes it at construction), so what its
        // constructor sees is the empty pre-load state: a file that then fails to load has to reach the status
        // line through this, or the notice "mcp_servers.json could not be read" never shows. Same when the
        // BrainX window's Connect reads the file again and it works.
        _configHandler = () => OnUi(OnConfigStatusChanged);
        _mcpManager.ConfigStatusChanged += _configHandler;
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            foreach (var s in Servers) ApplyState(s);
        };

        SyncServers();
        StatusText = Summary();
    }

    /// <summary>Run on the UI thread: inline when already there (keeps the order of status lines a page action
    /// sets), BeginInvoke from any other thread (never a blocking Invoke — see the state subscription above).
    /// With no WPF application at all (a headless test host) there is no UI thread to wait for: inline.</summary>
    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    private void OnConfigStatusChanged()
    {
        if (_disposed) return;
        SyncServers();
        RefreshSelectedTools();
        StatusText = Summary();
    }

    // ─── Sync (in place) ───

    /// <summary>The page's status line at rest: a config-file problem first (it is why the list may be empty
    /// or short, and why edits aren't saved), then servers a re-read brought back that are not started yet,
    /// otherwise the server count.</summary>
    private string Summary()
    {
        if (_mcpManager.ConfigLoadFailed) return _loc.T("mcp.config.unreadable");
        var unreadable = _mcpManager.UnreadableServers;
        if (unreadable.Count > 0) return _loc.T("mcp.config.someUnreadable", string.Join(", ", unreadable));
        var waiting = _mcpManager.ServersAwaitingStartAfterReload;
        if (waiting.Count > 0) return _loc.T("mcp.config.reloadedNotStarted", string.Join(", ", waiting));
        return _loc.T("mcpm.summary", Servers.Count, _mcpManager.GetRunningServers().Count);
    }

    /// <summary>A save that returned false: "can't be read" and "can't be written" are different fixes.</summary>
    private string SaveFailedText()
        => _mcpManager.LastConfigWriteProblem == McpServerManager.ConfigWriteProblem.Unreadable
            ? _loc.T("mcp.config.unreadable")
            : _loc.T("mcp.config.saveFailed");

    /// <summary>
    /// mcp_servers.json could not be read: say so instead of changing anything. Every edit on this page is
    /// checked against this first, so nothing is changed in memory that could not be saved.
    /// </summary>
    private bool RefuseWhileConfigUnreadable()
    {
        if (!_mcpManager.ConfigLoadFailed) return false;
        StatusText = _loc.T("mcp.config.unreadable");
        return true;
    }

    /// <summary>
    /// Explicit Refresh. When the file — or an entry in it — could not be read before, read it again (the user
    /// may have fixed it), and when that works do what startup would have done: start the enabled servers it
    /// never got to and register an installed BrainX that was skipped. Then re-sync and show the summary.
    /// The command's body; public so the contract harness can await it.
    /// </summary>
    public async Task RefreshAsync()
    {
        bool reloaded = false;
        if (_mcpManager.ConfigLoadFailed || _mcpManager.UnreadableServers.Count > 0
            || _mcpManager.ServersAwaitingStartAfterReload.Count > 0)
        {
            StatusText = _loc.T("mcp.config.rereading");
            try { reloaded = await _mcpManager.RecoverConfigAsync(); }
            catch (Exception ex)
            {
                _log?.Error("MCP", "Refresh: reading mcp_servers.json again threw", ex);
            }
        }
        if (_disposed) return;
        SyncServers();
        RefreshSelectedTools();
        string summary = Summary();
        StatusText = reloaded && !_mcpManager.ConfigLoadFailed && _mcpManager.UnreadableServers.Count == 0
                     && _mcpManager.ServersAwaitingStartAfterReload.Count == 0
            ? _loc.T("mcp.config.reloaded", Servers.Count, _mcpManager.GetRunningServers().Count)
            : summary;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Bring the list in line with the manager's configs WITHOUT recreating items: new servers are added,
    /// removed ones dropped, and existing items keep their log, their selection and any edit in progress.
    /// </summary>
    private void SyncServers()
    {
        var configs = _mcpManager.Configs.ToList();
        var names = new HashSet<string>(configs.Select(c => c.Key), StringComparer.Ordinal);

        foreach (var gone in Servers.Where(s => !names.Contains(s.Name)).ToList())
        {
            if (ReferenceEquals(gone, SelectedServer)) SelectedServer = null;
            Servers.Remove(gone);
        }

        foreach (var (name, config) in configs)
        {
            var item = Servers.FirstOrDefault(s => s.Name == name);
            if (item == null)
            {
                item = McpServerDisplayItem.FromConfig(name, config, false, 0);
                item.LogOutput = _logBuffers.TryGetValue(name, out var buf) ? buf : "";
                Servers.Add(item);
            }
            else if (!(IsEditing && ReferenceEquals(item, SelectedServer)) && !ReferenceEquals(item.Original, config))
            {
                item.LoadFrom(config);
            }
            item.Enabled = config.Enabled;
            ApplyState(item);
        }
    }

    /// <summary>Copy the manager's live state for one server onto its item (status, tools, localized labels).</summary>
    private void ApplyState(McpServerDisplayItem item)
    {
        var state = _mcpManager.GetServerState(item.Name);
        item.Status = state switch
        {
            McpServerState.Ready => "running",
            McpServerState.Starting => "starting",
            McpServerState.Reconnecting => "reconnecting",
            McpServerState.Failed => "error",
            _ => _mcpManager.IsServerRunning(item.Name) ? "running" : "stopped",
        };
        item.StatusLabel = item.Status switch
        {
            "running" => _loc.T("mcpm.statusRunning"),
            "starting" => _loc.T("mcpm.statusStarting"),
            "reconnecting" => _loc.T("mcpm.statusReconnecting"),
            "error" => _loc.T("mcpm.statusError"),
            _ => _loc.T("mcpm.statusStopped"),
        };
        item.ToolCount = _mcpManager.ToolRegistry.GetToolsForServer(item.Name).Count;
        item.ToolCountText = _loc.T("mcpm.toolCount", item.ToolCount);
        item.EnabledLabel = item.Enabled ? _loc.T("mcpm.enabledBadge") : _loc.T("mcpm.disabledBadge");
    }

    private void OnServerStateChanged(string name)
    {
        var item = Servers.FirstOrDefault(s => s.Name == name);
        if (item == null) SyncServers();
        else ApplyState(item);
        if (ReferenceEquals(item, SelectedServer)) RefreshSelectedTools();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshSelectedTools()
    {
        SelectedServerTools.Clear();
        if (SelectedServer == null) return;

        var tools = _mcpManager.ToolRegistry.GetToolsForServer(SelectedServer.Name);
        foreach (var t in tools)
            SelectedServerTools.Add(t);
    }

    // ─── Start / Stop / Restart ───

    private async Task StartSelectedServer()
    {
        var item = SelectedServer;
        if (item == null) return;
        string name = item.Name;
        item.Status = "starting";
        item.StatusLabel = _loc.T("mcpm.statusStarting");
        StatusText = _loc.T("mcpm.starting", name);

        try
        {
            bool ok = await _mcpManager.StartServerAsync(name);
            ApplyState(item);
            if (ReferenceEquals(item, SelectedServer)) RefreshSelectedTools();
            // The reason for a failure is already in the server log on this page (the manager writes it there);
            // the status line stays a short localized sentence instead of a raw exception message.
            StatusText = ok
                ? _loc.T("mcpm.started", name, item.ToolCount)
                : _loc.T("mcpm.startFailed", name);
        }
        catch (Exception ex)
        {
            _log?.Error("MCP", $"[{name}] start threw", ex);
            AppendLog(name, $"Start failed: {ex.Message}");
            item.Status = "error";
            item.StatusLabel = _loc.T("mcpm.statusError");
            StatusText = _loc.T("mcpm.startFailed", name);
        }
        finally { CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task StopSelectedServer()
    {
        var item = SelectedServer;
        if (item == null) return;
        string name = item.Name;
        StatusText = _loc.T("mcpm.stopping", name);
        try
        {
            await _mcpManager.StopServerAsync(name);
            ApplyState(item);
            if (ReferenceEquals(item, SelectedServer)) RefreshSelectedTools();
            StatusText = _loc.T("mcpm.stopped", name);
        }
        catch (Exception ex)
        {
            _log?.Error("MCP", $"[{name}] stop threw", ex);
            AppendLog(name, $"Stop failed: {ex.Message}");
            StatusText = _loc.T("mcpm.stopFailed", name);
        }
        finally { CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task RestartSelectedServer()
    {
        if (SelectedServer == null) return;
        await StopSelectedServer();
        await StartSelectedServer();
    }

    private async Task StartAllServers()
    {
        StatusText = _loc.T("mcpm.startingAll");
        try
        {
            await _mcpManager.StartAllEnabledAsync();
        }
        catch (Exception ex)
        {
            _log?.Error("MCP", "Start all threw", ex);
        }
        SyncServers();
        RefreshSelectedTools();
        StatusText = Summary();
        CommandManager.InvalidateRequerySuggested();
    }

    // ─── Add / Remove ───

    private void AddServer()
    {
        string name = NewServerName.Trim();
        string command = NewServerCommand.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(command)) return;
        if (RefuseWhileConfigUnreadable()) return;

        // Adding under an existing name used to silently replace that server's command, args and env.
        // An entry in the file that couldn't be read counts too — saving over it would destroy it.
        if (_mcpManager.IsServerNameTaken(name))
        {
            StatusText = _loc.T("mcpm.nameTaken", name);
            return;
        }

        var config = new McpServerConfig
        {
            Name = name,
            Command = command,
            // The Add box is one line: split like a command line, "quotes" keep an argument with spaces whole.
            Args = McpServerDisplayItem.ParseArgsLine(NewServerArgs),
            Enabled = true,
        };

        _mcpManager.SetConfig(name, config);
        // Only this server: the other entries on disk stay exactly as they are now, outside edits included.
        bool saved = _mcpManager.SaveConfig(name);

        NewServerName = "";
        NewServerCommand = "";
        NewServerArgs = "";
        IsAddingServer = false;

        SyncServers();
        SelectedServer = Servers.FirstOrDefault(s => s.Name == name);
        StatusText = saved ? _loc.T("mcpm.added", name) : SaveFailedText();
    }

    private async Task RemoveServer()
    {
        var item = SelectedServer;
        if (item == null) return;
        string name = item.Name;
        if (RefuseWhileConfigUnreadable()) return;

        // The server's env block usually holds API keys the user pasted once — removal loses them for good.
        var answer = MessageBox.Show(_loc.T("mcpm.removeConfirm", name), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (_mcpManager.IsServerRunning(name))
                await _mcpManager.StopServerAsync(name);

            _mcpManager.RemoveConfig(name);
            bool saved = _mcpManager.SaveConfig(name);   // writes this removal and nothing else
            // A brain the user removed by hand stays removed: without this the startup check found BrainX
            // installed-but-unlisted on the next launch and put it straight back.
            if (McpServerManager.LooksLikeBrain(name)) _mcpManager.SetBrainAutoConnectDeclined(true);
            _logBuffers.Remove(name);
            IsEditing = false;
            SyncServers();
            StatusText = saved ? _loc.T("mcpm.removed", name) : SaveFailedText();
        }
        catch (Exception ex)
        {
            _log?.Error("MCP", $"[{name}] remove failed", ex);
            StatusText = _loc.T("mcpm.removeFailed", name);
        }
    }

    // ─── Toggle Enabled ───

    /// <summary>
    /// Flip Enabled on the existing config object and nothing else. It used to rebuild the config from the
    /// display item — re-splitting the args on spaces — so pressing Toggle alone corrupted mcp_servers.json.
    /// </summary>
    private void ToggleEnabled()
    {
        var item = SelectedServer;
        if (item == null) return;
        if (RefuseWhileConfigUnreadable()) return;
        if (!_mcpManager.Configs.TryGetValue(item.Name, out var config)) return;

        config.Enabled = !config.Enabled;
        bool saved = _mcpManager.SaveConfig(item.Name);
        item.Enabled = config.Enabled;
        ApplyState(item);
        StatusText = !saved ? SaveFailedText()
            : _loc.T(config.Enabled ? "mcpm.enabledMsg" : "mcpm.disabledMsg", item.Name);
    }

    // ─── Edit ───

    private void BeginEdit()
    {
        if (SelectedServer == null) return;
        SelectedServer.LoadFrom(_mcpManager.Configs.TryGetValue(SelectedServer.Name, out var c) ? c : SelectedServer.Original);
        IsEditing = true;
    }

    private void SaveEdit()
    {
        var item = SelectedServer;
        if (item == null) return;
        if (RefuseWhileConfigUnreadable()) return;
        var config = item.ToConfig();
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            StatusText = _loc.T("mcpm.commandRequired");
            return;
        }
        _mcpManager.SetConfig(item.Name, config);
        bool saved = _mcpManager.SaveConfig(item.Name);
        item.LoadFrom(config);
        IsEditing = false;
        StatusText = !saved ? SaveFailedText()
            : _mcpManager.IsServerRunning(item.Name)
                ? _loc.T("mcpm.savedRestart", item.Name)
                : _loc.T("mcpm.saved", item.Name);
    }

    private void CancelEdit()
    {
        if (SelectedServer != null) SelectedServer.LoadFrom(SelectedServer.Original);
        IsEditing = false;
    }

    // ─── Events ───

    private void AppendLog(string serverName, string message)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        string text = (_logBuffers.TryGetValue(serverName, out var cur) ? cur : "") + $"[{ts}] {message}\n";
        // Bounded: keep the newest ~8 KB once past 10 KB.
        if (text.Length > LogKeepChars)
            text = "… (trimmed)\n" + text[^8192..];
        _logBuffers[serverName] = text;
        var item = Servers.FirstOrDefault(s => s.Name == serverName);
        if (item != null) item.LogOutput = text;
    }

    // Both of these are raised from the Process.Exited callback thread as well
    // as from the UI thread, so neither may block on the dispatcher — see the
    // OnServerStateChanged subscription above.
    private void OnServerLog(string serverName, string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(serverName, message));
    }

    private void OnToolsChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var s in Servers)
            {
                s.ToolCount = _mcpManager.ToolRegistry.GetToolsForServer(s.Name).Count;
                s.ToolCountText = _loc.T("mcpm.toolCount", s.ToolCount);
            }
            RefreshSelectedTools();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mcpManager.OnServerLog -= OnServerLog;
        _mcpManager.OnToolsChanged -= OnToolsChanged;
        _mcpManager.OnServerStateChanged -= _stateHandler;
        _mcpManager.ConfigStatusChanged -= _configHandler;
    }
}
