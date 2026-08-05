using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Skills page — browse the registry of slash-command
/// skills (built-in + user + project), preview the prompt body, and copy
/// the /command snippet to clipboard.
/// </summary>
public class SkillsViewModel : ViewModelBase
{
    private readonly SkillService _service;

    public ObservableCollection<SkillDefinition> Skills { get; } = new();

    private SkillDefinition? _selected;
    public SkillDefinition? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedToolsDisplay));
                OnPropertyChanged(nameof(SelectedInvokeSnippet));
                OnPropertyChanged(nameof(SelectedSource));
            }
        }
    }

    public bool HasSelection => _selected != null;

    public string SelectedToolsDisplay => _selected == null
        ? ""
        : _selected.AllowedTools.Count == 0
            ? "(inherits all tools)"
            : string.Join(", ", _selected.AllowedTools);

    public string SelectedInvokeSnippet => _selected == null ? "" : $"/{_selected.Name}";

    public string SelectedSource => _selected == null
        ? ""
        : _selected.IsBuiltIn
            ? "built-in"
            : (string.IsNullOrEmpty(_selected.FilePath) ? "custom" : _selected.FilePath);

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int BuiltInCount => Skills.Count(s => s.IsBuiltIn);
    public int CustomCount => Skills.Count(s => !s.IsBuiltIn);

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand CopyInvokeCommand { get; }
    public ICommand OpenUserSkillsFolderCommand { get; }

    public SkillsViewModel(SkillService service)
    {
        _service = service;
        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<SkillDefinition>(s => { if (s != null) Selected = s; });
        CopyInvokeCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedInvokeSnippet);
                StatusMessage = $"Copied {SelectedInvokeSnippet}";
            }
            catch { StatusMessage = "Clipboard write failed"; }
        });
        OpenUserSkillsFolderCommand = new RelayCommand(() =>
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cluadex", "skills");
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir, UseShellExecute = true,
                });
                StatusMessage = $"Opened {dir}";
            }
            catch (Exception ex) { StatusMessage = $"Open folder failed: {ex.Message}"; }
        });

        Refresh();
    }

    public void Refresh()
    {
        _service.ReloadSkills();
        Skills.Clear();
        foreach (var s in _service.GetAllSkills().OrderBy(x => !x.IsBuiltIn).ThenBy(x => x.Name))
            Skills.Add(s);
        OnPropertyChanged(nameof(BuiltInCount));
        OnPropertyChanged(nameof(CustomCount));
        Selected ??= Skills.FirstOrDefault();
        StatusMessage = $"{Skills.Count} skills · {BuiltInCount} built-in · {CustomCount} custom";
    }
}
