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
    private readonly LocalizationService _loc;

    private PluginInfo? _selectedPlugin;
    private CatalogPlugin? _selectedCatalogPlugin;

    public ObservableCollection<PluginInfo> Plugins { get; } = new();
    public ObservableCollection<CatalogPlugin> CatalogPlugins { get; } = new();

    public PluginInfo? SelectedPlugin
    {
        get => _selectedPlugin;
        set => SetProperty(ref _selectedPlugin, value);
    }

    public CatalogPlugin? SelectedCatalogPlugin
    {
        get => _selectedCatalogPlugin;
        set
        {
            SetProperty(ref _selectedCatalogPlugin, value);
            OnPropertyChanged(nameof(CanInstallSelected));
            OnPropertyChanged(nameof(CanUninstallSelected));
        }
    }

    // ─── Tab state ───
    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    // ─── Localized Labels ───
    private string _pageTitle = "";
    public string PageTitle { get => _pageTitle; set => SetProperty(ref _pageTitle, value); }
    private string _pageSubtitle = "";
    public string PageSubtitle { get => _pageSubtitle; set => SetProperty(ref _pageSubtitle, value); }
    private string _installedLabel = "";
    public string InstalledLabel { get => _installedLabel; set => SetProperty(ref _installedLabel, value); }
    private string _catalogLabel = "";
    public string CatalogLabel { get => _catalogLabel; set => SetProperty(ref _catalogLabel, value); }

    // ─── Filter ───
    private string _catalogFilter = "";
    public string CatalogFilter
    {
        get => _catalogFilter;
        set
        {
            SetProperty(ref _catalogFilter, value);
            RefreshCatalog();
        }
    }

    public bool CanInstallSelected => _selectedCatalogPlugin != null && !_selectedCatalogPlugin.IsInstalled;
    public bool CanUninstallSelected => _selectedCatalogPlugin != null && _selectedCatalogPlugin.IsInstalled;

    // ─── Commands ───
    public ICommand RefreshCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand AddPluginCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand InstallCatalogCommand { get; }
    public ICommand UninstallCatalogCommand { get; }

    public PluginManagerViewModel(PluginService pluginService, LocalizationService loc)
    {
        _pluginService = pluginService;
        _loc = loc;

        RefreshCommand = new RelayCommand(Refresh);
        EnableCommand = new RelayCommand(EnableSelected, () => SelectedPlugin != null && !SelectedPlugin.Enabled);
        DisableCommand = new RelayCommand(DisableSelected, () => SelectedPlugin != null && SelectedPlugin.Enabled);
        AddPluginCommand = new RelayCommand(AddPlugin);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        InstallCatalogCommand = new RelayCommand(InstallSelected, () => CanInstallSelected);
        UninstallCatalogCommand = new RelayCommand(UninstallSelected, () => CanUninstallSelected);

        _loc.LanguageChanged += RefreshLabels;

        Refresh();
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        PageTitle = _loc.T("plugins.title");
        PageSubtitle = _loc.T("plugins.subtitle");
        InstalledLabel = _loc.T("plugins.installed");
        CatalogLabel = _loc.T("plugins.catalog");
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

        RefreshCatalog();
    }

    private void RefreshCatalog()
    {
        var installed = _pluginService.ScanPlugins();
        var installedIds = installed.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        CatalogPlugins.Clear();
        foreach (var cp in PluginService.CuratedCatalog)
        {
            // Apply filter
            if (!string.IsNullOrWhiteSpace(_catalogFilter))
            {
                var f = _catalogFilter.Trim();
                bool match = cp.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Category.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Tags.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
            }

            cp.IsInstalled = installedIds.Contains(cp.Name);
            CatalogPlugins.Add(cp);
        }

        // Re-select catalog item
        if (_selectedCatalogPlugin != null)
        {
            SelectedCatalogPlugin = CatalogPlugins.FirstOrDefault(p =>
                string.Equals(p.Id, _selectedCatalogPlugin.Id, StringComparison.OrdinalIgnoreCase));
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

    private void InstallSelected()
    {
        if (_selectedCatalogPlugin == null) return;
        _pluginService.InstallCatalogPlugin(_selectedCatalogPlugin);
        Refresh();
    }

    private void UninstallSelected()
    {
        if (_selectedCatalogPlugin == null) return;
        _pluginService.UninstallPlugin(_selectedCatalogPlugin.Name);
        Refresh();
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
