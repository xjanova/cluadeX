using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Helpers;

namespace CluadeX.ViewModels;

/// <summary>A catalog card: the plugin plus its text in the current UI language.</summary>
public sealed class CatalogPluginItem
{
    public CatalogPlugin Plugin { get; }
    public string Name { get; }
    public string Description { get; }
    public string HookSummary { get; }
    public string Icon => Plugin.Icon;
    public string Category => Plugin.Category;
    public bool IsInstalled => Plugin.IsInstalled;
    /// <summary>No hooks yet — installing it would do nothing, so it is offered as "coming soon".</summary>
    public bool IsComingSoon { get; }

    public CatalogPluginItem(CatalogPlugin plugin, bool thai)
    {
        Plugin = plugin;
        Name = thai && !string.IsNullOrWhiteSpace(plugin.NameTh) ? plugin.NameTh : plugin.Name;
        Description = thai && !string.IsNullOrWhiteSpace(plugin.DescriptionTh) ? plugin.DescriptionTh : plugin.Description;
        HookSummary = thai && !string.IsNullOrWhiteSpace(plugin.HookSummaryTh) ? plugin.HookSummaryTh : plugin.HookSummary;
        IsComingSoon = !PluginService.IsImplemented(plugin);
    }
}

/// <summary>An installed-plugin card: the plugin plus localized text (catalog plugins get their Thai text).</summary>
public sealed class InstalledPluginItem
{
    public PluginInfo Plugin { get; }
    public string Name { get; }
    public string Description { get; }
    public string Version => Plugin.Version;
    public bool Enabled => Plugin.Enabled;
    public string EnabledLabel { get; }
    /// <summary>Installed from the catalog by an older version, but the catalog entry has no hooks: it does nothing.</summary>
    public bool IsPlaceholder { get; }

    public InstalledPluginItem(PluginInfo plugin, CatalogPlugin? catalog, bool thai, string enabledLabel)
    {
        Plugin = plugin;
        Name = thai && catalog != null && !string.IsNullOrWhiteSpace(catalog.NameTh) ? catalog.NameTh : plugin.Name;
        Description = thai && catalog != null && !string.IsNullOrWhiteSpace(catalog.DescriptionTh) ? catalog.DescriptionTh : plugin.Description;
        EnabledLabel = enabledLabel;
        IsPlaceholder = catalog != null && !PluginService.IsImplemented(catalog);
    }
}

public class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginService _pluginService;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;

    private PluginInfo? _selectedPlugin;
    private CatalogPlugin? _selectedCatalogPlugin;

    public ObservableCollection<InstalledPluginItem> Plugins { get; } = new();
    public ObservableCollection<CatalogPluginItem> CatalogPlugins { get; } = new();

    public PluginInfo? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            if (SetProperty(ref _selectedPlugin, value))
                OnPropertyChanged(nameof(SelectedPluginItem));
        }
    }

    /// <summary>The selected installed plugin's card (localized text) for the detail panel.</summary>
    public InstalledPluginItem? SelectedPluginItem =>
        _selectedPlugin == null ? null : Plugins.FirstOrDefault(p => ReferenceEquals(p.Plugin, _selectedPlugin));

    public CatalogPlugin? SelectedCatalogPlugin
    {
        get => _selectedCatalogPlugin;
        set
        {
            SetProperty(ref _selectedCatalogPlugin, value);
            OnPropertyChanged(nameof(SelectedCatalogItem));
            OnPropertyChanged(nameof(CanInstallSelected));
            OnPropertyChanged(nameof(CanUninstallSelected));
        }
    }

    /// <summary>The selected catalog plugin's card (localized text, coming-soon flag) for the detail panel.</summary>
    public CatalogPluginItem? SelectedCatalogItem =>
        _selectedCatalogPlugin == null ? null : CatalogPlugins.FirstOrDefault(p => ReferenceEquals(p.Plugin, _selectedCatalogPlugin));

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

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

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

    public bool CanInstallSelected => _selectedCatalogPlugin != null && !_selectedCatalogPlugin.IsInstalled
                                      && PluginService.IsImplemented(_selectedCatalogPlugin);
    public bool CanUninstallSelected => _selectedCatalogPlugin != null && _selectedCatalogPlugin.IsInstalled;

    // ─── Commands ───
    public ICommand RefreshCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand AddPluginCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand InstallCatalogCommand { get; }
    public ICommand UninstallCatalogCommand { get; }

    public PluginManagerViewModel(PluginService pluginService, LocalizationService loc, DebugLogService? log = null)
    {
        _pluginService = pluginService;
        _loc = loc;
        _log = log;

        RefreshCommand = new RelayCommand(Refresh);
        EnableCommand = new RelayCommand(EnableSelected, () => SelectedPlugin != null && !SelectedPlugin.Enabled);
        DisableCommand = new RelayCommand(DisableSelected, () => SelectedPlugin != null && SelectedPlugin.Enabled);
        AddPluginCommand = new RelayCommand(AddPlugin);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        InstallCatalogCommand = new RelayCommand(InstallSelected, () => CanInstallSelected);
        UninstallCatalogCommand = new RelayCommand(UninstallSelected, () => CanUninstallSelected);

        _loc.LanguageChanged += () => { RefreshLabels(); Refresh(); };

        Refresh();
        RefreshLabels();
    }

    private bool IsThai => _loc.CurrentLanguage == "th";

    private void RefreshLabels()
    {
        PageTitle = _loc.T("plugins.title");
        PageSubtitle = _loc.T("plugins.subtitle");
        InstalledLabel = _loc.T("plugins.installed");
        CatalogLabel = _loc.T("plugins.catalog");
    }

    private void Refresh()
    {
        List<PluginInfo> plugins;
        try { plugins = _pluginService.ScanPlugins(); }
        catch (Exception ex)
        {
            _log?.Error("Plugins", "Scan failed", ex);
            plugins = new List<PluginInfo>();
        }

        var byName = PluginService.CuratedCatalog.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        string? keep = _selectedPlugin?.Name;
        Plugins.Clear();
        foreach (var plugin in plugins)
        {
            byName.TryGetValue(plugin.Name, out var catalog);
            Plugins.Add(new InstalledPluginItem(plugin, catalog, IsThai,
                _loc.T(plugin.Enabled ? "pluginsm.on" : "pluginsm.off")));
        }

        // Re-select if the previously selected plugin still exists
        SelectedPlugin = keep == null ? null : Plugins.Select(p => p.Plugin).FirstOrDefault(p =>
            string.Equals(p.Name, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedPluginItem));

        RefreshCatalog();
    }

    private void RefreshCatalog()
    {
        HashSet<string> installedIds;
        try { installedIds = _pluginService.ScanPlugins().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        CatalogPlugins.Clear();
        // Only plugins that do something: an entry without hooks installed nothing that runs, and showing it as
        // "coming soon" put a feature on screen that does not exist.
        foreach (var cp in PluginService.CuratedCatalog.Where(PluginService.IsImplemented))
        {
            // Apply filter (both languages, so a Thai search finds a plugin too)
            if (!string.IsNullOrWhiteSpace(_catalogFilter))
            {
                var f = _catalogFilter.Trim();
                bool match = cp.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.NameTh.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.DescriptionTh.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Category.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || cp.Tags.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
            }

            cp.IsInstalled = installedIds.Contains(cp.Name);
            CatalogPlugins.Add(new CatalogPluginItem(cp, IsThai));
        }

        // Re-select catalog item
        if (_selectedCatalogPlugin != null)
        {
            SelectedCatalogPlugin = CatalogPlugins.Select(p => p.Plugin).FirstOrDefault(p =>
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
        // Start in Documents, NOT in the plugins folder: picking an already-installed plugin there made the
        // source and the destination the same folder, and installing deleted it.
        string? dir = FolderPicker.ShowDialog(_loc.T("pluginsm.pickFolderTitle"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (string.IsNullOrEmpty(dir)) return;

        bool overwrite = false;
        switch (_pluginService.CheckInstallSource(dir))
        {
            case PluginService.InstallCheck.NotFound:
                StatusMessage = _loc.T("pluginsm.folderMissing");
                return;
            case PluginService.InstallCheck.InsidePluginsFolder:
                StatusMessage = _loc.T("pluginsm.alreadyInPluginsFolder");
                return;
            case PluginService.InstallCheck.ContainsPluginsFolder:
                StatusMessage = _loc.T("pluginsm.containsPluginsFolder");
                return;
            case PluginService.InstallCheck.NoManifest:
                StatusMessage = _loc.T("pluginsm.noManifest");
                return;
            case PluginService.InstallCheck.AlreadyInstalled:
                string name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
                var answer = MessageBox.Show(_loc.T("pluginsm.overwriteConfirm", name), "CluadeX",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
                overwrite = true;
                break;
        }

        try
        {
            _pluginService.InstallPlugin(dir, overwrite);
            StatusMessage = _loc.T("pluginsm.installedOk", System.IO.Path.GetFileName(dir.TrimEnd('\\', '/')));
        }
        catch (Exception ex)
        {
            _log?.Error("Plugins", $"Install from {dir} failed", ex);
            StatusMessage = _loc.T("pluginsm.installFailed");
        }
        Refresh();
    }

    private void InstallSelected()
    {
        if (_selectedCatalogPlugin == null) return;
        var item = SelectedCatalogItem;
        try
        {
            _pluginService.InstallCatalogPlugin(_selectedCatalogPlugin);
            StatusMessage = _loc.T("pluginsm.installedOk", item?.Name ?? _selectedCatalogPlugin.Name);
        }
        catch (Exception ex)
        {
            _log?.Error("Plugins", $"Catalog install of {_selectedCatalogPlugin.Id} failed", ex);
            StatusMessage = _loc.T("pluginsm.installFailed");
        }
        Refresh();
    }

    private void UninstallSelected()
    {
        if (_selectedCatalogPlugin == null) return;
        string display = SelectedCatalogItem?.Name ?? _selectedCatalogPlugin.Name;
        // The folder holds the plugin's hooks.json and scripts — anything the user changed there goes with it.
        var answer = MessageBox.Show(_loc.T("pluginsm.uninstallConfirm", display), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _pluginService.UninstallPlugin(_selectedCatalogPlugin.Name);
            StatusMessage = _loc.T("pluginsm.uninstalledOk", display);
        }
        catch (Exception ex)
        {
            _log?.Error("Plugins", $"Uninstall of {_selectedCatalogPlugin.Id} failed", ex);
            StatusMessage = _loc.T("pluginsm.uninstallFailed");
        }
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
        catch (Exception ex) { _log?.Warn("Plugins", "Open plugins folder failed", ex); }
    }
}
