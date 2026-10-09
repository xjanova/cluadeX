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
    private readonly LocalizationService _loc;
    private readonly DebugLogService _log;

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
            ? _loc.T("skill.inheritsTools")
            : string.Join(", ", _selected.AllowedTools);

    public string SelectedInvokeSnippet => _selected == null ? "" : $"/{_selected.Name}";

    public string SelectedSource => _selected == null
        ? ""
        : _selected.IsBuiltIn
            ? _loc.T("skill.sourceBuiltIn")
            : (string.IsNullOrEmpty(_selected.FilePath) ? _loc.T("skill.sourceCustom") : _selected.FilePath);

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int BuiltInCount => Skills.Count(s => s.IsBuiltIn);
    public int CustomCount => Skills.Count(s => !s.IsBuiltIn);

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand CopyInvokeCommand { get; }
    public ICommand OpenUserSkillsFolderCommand { get; }

    public SkillsViewModel(SkillService service, LocalizationService loc, DebugLogService log)
    {
        _service = service;
        _loc = loc;
        _log = log;
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(SelectedToolsDisplay));
            OnPropertyChanged(nameof(SelectedSource));
        };
        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<SkillDefinition>(s => { if (s != null) Selected = s; });
        CopyInvokeCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedInvokeSnippet);
                StatusMessage = string.Format(_loc.T("skill.copied"), SelectedInvokeSnippet);
            }
            catch { StatusMessage = _loc.T("skill.clipboardFailed"); }
        });
        OpenUserSkillsFolderCommand = new RelayCommand(() =>
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cluadex", "skills");
            try
            {
                Directory.CreateDirectory(dir);
                // A folder path: the shell opens it in Explorer (it can't "run" a directory).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir, UseShellExecute = true,
                });
                StatusMessage = string.Format(_loc.T("skill.openedFolder"), dir);
            }
            catch (Exception ex)
            {
                _log.Warn("Skills", "Open skills folder failed", ex);
                StatusMessage = _loc.T("skill.openFolderFailed");
            }
        });

        Refresh();
    }

    public void Refresh()
    {
        // Re-select by NAME: ReloadSkills builds new objects, so the old `Selected ??=` kept the
        // previous (stale) instance — a skill deleted from disk stayed selected and previewable.
        var previouslySelectedName = _selected?.Name;
        _service.ReloadSkills();
        Skills.Clear();
        foreach (var s in _service.GetAllSkills().OrderBy(x => !x.IsBuiltIn).ThenBy(x => x.Name))
            Skills.Add(s);
        OnPropertyChanged(nameof(BuiltInCount));
        OnPropertyChanged(nameof(CustomCount));
        Selected = (previouslySelectedName == null
                       ? null
                       : Skills.FirstOrDefault(s => string.Equals(s.Name, previouslySelectedName, StringComparison.OrdinalIgnoreCase)))
                   ?? Skills.FirstOrDefault();
        StatusMessage = string.Format(_loc.T("skill.summary"), Skills.Count, BuiltInCount, CustomCount);
    }
}
