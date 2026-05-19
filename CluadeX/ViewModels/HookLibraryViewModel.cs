using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Hook Library page — browse 15 bundled .ps1 hooks,
/// toggle each on/off, preview the script content, and see which phase
/// it runs in. Sprint 3 #2.
/// </summary>
public class HookLibraryViewModel : ViewModelBase
{
    private readonly HookBundleService _service;

    public ObservableCollection<HookBundle> Bundles { get; } = new();

    private HookBundle? _selected;
    public HookBundle? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedScriptContent));
                OnPropertyChanged(nameof(SelectedScriptPath));
            }
        }
    }

    public bool HasSelection => _selected != null;

    public string SelectedScriptContent =>
        _selected == null ? "(select a hook to preview)" : _service.ReadScript(_selected);

    public string SelectedScriptPath =>
        _selected == null ? "" : Path.Combine(_service.BundleDir, _selected.ScriptFile);

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) Refresh(); }
    }

    public int EnabledCount => Bundles.Count(b => b.IsEnabled);
    public int TotalCount => Bundles.Count;

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenBundleFolderCommand { get; }

    public HookLibraryViewModel(HookBundleService service)
    {
        _service = service;
        _service.Changed += Refresh;
        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<HookBundle>(b => { if (b != null) Selected = b; });
        ToggleCommand = new RelayCommand<HookBundle>(b =>
        {
            if (b == null) return;
            _service.SetEnabled(b.Id, !b.IsEnabled);
            StatusMessage = b.IsEnabled
                ? $"Disabled {b.Name}"
                : $"Enabled {b.Name}";
        });
        OpenBundleFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(_service.BundleDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _service.BundleDir, UseShellExecute = true,
                });
                StatusMessage = $"Opened {_service.BundleDir}";
            }
            catch (Exception ex) { StatusMessage = $"Open folder failed: {ex.Message}"; }
        });

        Refresh();
    }

    public void Refresh()
    {
        var prevId = _selected?.Id;
        Bundles.Clear();
        var catalog = _service.GetCatalog();
        IEnumerable<HookBundle> view = catalog;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            string f = _filter.Trim().ToLowerInvariant();
            view = catalog.Where(b =>
                b.Name.ToLowerInvariant().Contains(f) ||
                b.Description.ToLowerInvariant().Contains(f) ||
                b.Phase.ToLowerInvariant().Contains(f) ||
                b.Category.ToLowerInvariant().Contains(f));
        }
        foreach (var b in view.OrderBy(b => PhaseRank(b.Phase)).ThenBy(b => b.Name))
            Bundles.Add(b);

        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(TotalCount));

        if (prevId != null)
        {
            Selected = Bundles.FirstOrDefault(b => b.Id == prevId) ?? Bundles.FirstOrDefault();
        }
        else
        {
            Selected ??= Bundles.FirstOrDefault();
        }
        StatusMessage = $"{EnabledCount}/{TotalCount} enabled";
    }

    private static int PhaseRank(string phase) => phase switch
    {
        "PreToolUse"   => 0,
        "PostToolUse"  => 1,
        "Stop"         => 2,
        "SessionStart" => 3,
        "PreCompact"   => 4,
        _              => 5,
    };
}
