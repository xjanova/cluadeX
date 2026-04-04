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

    private McpServerDisplayItem? _selectedServer;
    private string _newServerName = "";
    private string _newServerCommand = "";
    private string _newServerArgs = "";
    private bool _isAddingServer;
    private bool _isEditing;
    private string _statusText = "";

    // Editing backup
    private string _editCommand = "";
    private string _editArgs = "";
    private string _editEnv = "";

    public ObservableCollection<McpServerDisplayItem> Servers { get; } = new();
    public ObservableCollection<McpTool> SelectedServerTools { get; } = new();

    public McpServerDisplayItem? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                IsEditing = false;
                RefreshSelectedTools();
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

    public McpServersViewModel(McpServerManager mcpManager, LocalizationService loc)
    {
        _mcpManager = mcpManager;
        _loc = loc;

        StartServerCommand = new AsyncRelayCommand(StartSelectedServer);
        StopServerCommand = new AsyncRelayCommand(StopSelectedServer);
        RestartServerCommand = new AsyncRelayCommand(RestartSelectedServer);
        AddServerCommand = new RelayCommand(AddServer);
        RemoveServerCommand = new RelayCommand(RemoveServer);
        ToggleEnabledCommand = new RelayCommand(ToggleEnabled);
        BeginEditCommand = new RelayCommand(BeginEdit);
        SaveEditCommand = new RelayCommand(SaveEdit);
        CancelEditCommand = new RelayCommand(CancelEdit);
        ShowAddFormCommand = new RelayCommand(() => IsAddingServer = !IsAddingServer);
        RefreshCommand = new RelayCommand(Refresh);
        StartAllCommand = new AsyncRelayCommand(StartAllServers);

        _mcpManager.OnServerLog += OnServerLog;
        _mcpManager.OnToolsChanged += OnToolsChanged;
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        };

        Refresh();
    }

    // ─── Refresh ───

    private void Refresh()
    {
        string? selectedName = SelectedServer?.Name;
        Servers.Clear();

        foreach (var (name, config) in _mcpManager.Configs)
        {
            bool running = _mcpManager.IsServerRunning(name);
            int toolCount = _mcpManager.ToolRegistry.GetToolsForServer(name).Count;
            var item = McpServerDisplayItem.FromConfig(name, config, running, toolCount);
            Servers.Add(item);
        }

        if (selectedName != null)
            SelectedServer = Servers.FirstOrDefault(s => s.Name == selectedName);

        StatusText = $"{Servers.Count} server(s), {_mcpManager.GetRunningServers().Count} running";
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
        if (SelectedServer == null) return;
        SelectedServer.Status = "starting";
        StatusText = $"Starting {SelectedServer.Name}...";

        bool ok = await _mcpManager.StartServerAsync(SelectedServer.Name);
        SelectedServer.Status = ok ? "running" : "error";
        SelectedServer.ToolCount = _mcpManager.ToolRegistry.GetToolsForServer(SelectedServer.Name).Count;
        RefreshSelectedTools();
        StatusText = ok ? $"{SelectedServer.Name} started ({SelectedServer.ToolCount} tools)" : $"{SelectedServer.Name} failed to start";
    }

    private async Task StopSelectedServer()
    {
        if (SelectedServer == null) return;
        StatusText = $"Stopping {SelectedServer.Name}...";

        await _mcpManager.StopServerAsync(SelectedServer.Name);
        SelectedServer.Status = "stopped";
        SelectedServer.ToolCount = 0;
        RefreshSelectedTools();
        StatusText = $"{SelectedServer.Name} stopped";
    }

    private async Task RestartSelectedServer()
    {
        if (SelectedServer == null) return;
        await StopSelectedServer();
        await StartSelectedServer();
    }

    private async Task StartAllServers()
    {
        StatusText = "Starting all enabled servers...";
        await _mcpManager.StartAllEnabledAsync();
        Refresh();
    }

    // ─── Add / Remove ───

    private void AddServer()
    {
        string name = NewServerName.Trim();
        string command = NewServerCommand.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(command)) return;

        var config = new McpServerConfig
        {
            Name = name,
            Command = command,
            Args = string.IsNullOrWhiteSpace(NewServerArgs)
                ? new List<string>()
                : NewServerArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Enabled = true,
        };

        _mcpManager.SetConfig(name, config);
        _mcpManager.SaveConfig();

        NewServerName = "";
        NewServerCommand = "";
        NewServerArgs = "";
        IsAddingServer = false;

        Refresh();
        SelectedServer = Servers.FirstOrDefault(s => s.Name == name);
        StatusText = $"Added server: {name}";
    }

    private async void RemoveServer()
    {
        if (SelectedServer == null) return;
        string name = SelectedServer.Name;

        if (_mcpManager.IsServerRunning(name))
            await _mcpManager.StopServerAsync(name);

        _mcpManager.RemoveConfig(name);
        _mcpManager.SaveConfig();
        Refresh();
        StatusText = $"Removed server: {name}";
    }

    // ─── Toggle Enabled ───

    private void ToggleEnabled()
    {
        if (SelectedServer == null) return;
        SelectedServer.Enabled = !SelectedServer.Enabled;

        var config = SelectedServer.ToConfig();
        _mcpManager.SetConfig(SelectedServer.Name, config);
        _mcpManager.SaveConfig();
        StatusText = $"{SelectedServer.Name}: {(SelectedServer.Enabled ? "enabled" : "disabled")}";
    }

    // ─── Edit ───

    private void BeginEdit()
    {
        if (SelectedServer == null) return;
        _editCommand = SelectedServer.Command;
        _editArgs = SelectedServer.ArgsString;
        _editEnv = SelectedServer.EnvString;
        IsEditing = true;
    }

    private void SaveEdit()
    {
        if (SelectedServer == null) return;
        var config = SelectedServer.ToConfig();
        _mcpManager.SetConfig(SelectedServer.Name, config);
        _mcpManager.SaveConfig();
        IsEditing = false;
        StatusText = $"Saved config: {SelectedServer.Name}";
    }

    private void CancelEdit()
    {
        if (SelectedServer == null) return;
        SelectedServer.Command = _editCommand;
        SelectedServer.ArgsString = _editArgs;
        SelectedServer.EnvString = _editEnv;
        IsEditing = false;
    }

    // ─── Events ───

    private void OnServerLog(string serverName, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var item = Servers.FirstOrDefault(s => s.Name == serverName);
            if (item != null)
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                item.LogOutput += $"[{ts}] {message}\n";
                // Trim log to last 10KB to prevent unbounded growth
                if (item.LogOutput.Length > 10240)
                    item.LogOutput = "... (trimmed)\n" + item.LogOutput[^8192..];
            }
        });
    }

    private void OnToolsChanged()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var s in Servers)
                s.ToolCount = _mcpManager.ToolRegistry.GetToolsForServer(s.Name).Count;
            RefreshSelectedTools();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mcpManager.OnServerLog -= OnServerLog;
        _mcpManager.OnToolsChanged -= OnToolsChanged;
    }
}
