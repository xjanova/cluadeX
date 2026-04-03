using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Helpers;

namespace CluadeX.ViewModels;

public class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginService _pluginService;

    private PluginInfo? _selectedPlugin;

    public ObservableCollection<PluginInfo> Plugins { get; } = new();

    public PluginInfo? SelectedPlugin
    {
        get => _selectedPlugin;
        set => SetProperty(ref _selectedPlugin, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand AddPluginCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public PluginManagerViewModel(PluginService pluginService)
    {
        _pluginService = pluginService;

        RefreshCommand = new RelayCommand(Refresh);
        EnableCommand = new RelayCommand(EnableSelected, () => SelectedPlugin != null && !SelectedPlugin.Enabled);
        DisableCommand = new RelayCommand(DisableSelected, () => SelectedPlugin != null && SelectedPlugin.Enabled);
        AddPluginCommand = new RelayCommand(AddPlugin);
        OpenFolderCommand = new RelayCommand(OpenFolder);

        Refresh();
    }

    private void Refresh()
    {
        var plugins = _pluginService.ScanPlugins();
        Plugins.Clear();
        foreach (var plugin in plugins)
            Plugins.Add(plugin);

        // Re-select if the previously selected plugin still exists
        if (_selectedPlugin != null)
        {
            SelectedPlugin = Plugins.FirstOrDefault(p =>
                string.Equals(p.Name, _selectedPlugin.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void EnableSelected()
    {
        if (SelectedPlugin == null) return;
        _pluginService.EnablePlugin(SelectedPlugin.Name);
        Refresh();
    }

    private void DisableSelected()
    {
        if (SelectedPlugin == null) return;
        _pluginService.DisablePlugin(SelectedPlugin.Name);
        Refresh();
    }

    private void AddPlugin()
    {
        string? dir = FolderPicker.ShowDialog("Select a plugin folder to install",
            _pluginService.PluginsDirectory);

        if (!string.IsNullOrEmpty(dir))
        {
            _pluginService.InstallPlugin(dir);
            Refresh();
        }
    }

    private void OpenFolder()
    {
        try
        {
            string dir = _pluginService.PluginsDirectory;
            if (System.IO.Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }
}
