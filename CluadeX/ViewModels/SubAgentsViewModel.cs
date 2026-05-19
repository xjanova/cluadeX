using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Subagents page — browse, preview, and (later) test
/// every subagent the registry knows about. v1 is read-only: scroll the
/// catalog, click a card to see the full system prompt, copy the invoke
/// snippet to clipboard so the user can paste it into chat.
/// </summary>
public class SubAgentsViewModel : ViewModelBase
{
    private readonly SubAgentService _service;

    public ObservableCollection<SubAgentDefinition> Agents { get; } = new();

    private SubAgentDefinition? _selected;
    public SubAgentDefinition? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedToolsDisplay));
                OnPropertyChanged(nameof(SelectedInvokeSnippet));
            }
        }
    }

    public bool HasSelection => _selected != null;

    public string SelectedToolsDisplay
    {
        get
        {
            if (_selected == null) return "";
            return _selected.AllowedTools.Count == 0
                ? "(inherits all tools from parent)"
                : string.Join(", ", _selected.AllowedTools);
        }
    }

    public string SelectedInvokeSnippet
    {
        get
        {
            if (_selected == null) return "";
            return $"[ACTION: subagent_invoke]\ntype: {_selected.Name}\nprompt: <your task here>";
        }
    }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int BuiltInCount => Agents.Count(a => a.IsBuiltIn);
    public int CustomCount => Agents.Count(a => !a.IsBuiltIn);

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand CopyInvokeCommand { get; }
    public ICommand OpenUserAgentsFolderCommand { get; }

    public SubAgentsViewModel(SubAgentService service)
    {
        _service = service;
        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<SubAgentDefinition>(a => { if (a != null) Selected = a; });
        CopyInvokeCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedInvokeSnippet);
                StatusMessage = $"Copied invoke snippet for {_selected.Name}";
            }
            catch { StatusMessage = "Clipboard write failed"; }
        });
        OpenUserAgentsFolderCommand = new RelayCommand(() =>
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cluadex", "agents");
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
        // FIX (audit HIGH #8): preserve selection across Refresh so the
        // user doesn't lose their place every time the registry reloads.
        // Match by name (the only stable identifier — IsBuiltIn / FilePath
        // can change between reloads).
        var previouslySelectedName = _selected?.Name;
        _service.Reload();
        Agents.Clear();
        foreach (var a in _service.GetAll().OrderBy(x => x.Tier).ThenBy(x => x.Name))
            Agents.Add(a);
        OnPropertyChanged(nameof(BuiltInCount));
        OnPropertyChanged(nameof(CustomCount));
        if (previouslySelectedName != null)
        {
            Selected = Agents.FirstOrDefault(a =>
                string.Equals(a.Name, previouslySelectedName, StringComparison.OrdinalIgnoreCase))
                ?? Agents.FirstOrDefault();
        }
        else
        {
            Selected ??= Agents.FirstOrDefault();
        }
        StatusMessage = $"{Agents.Count} subagents · {BuiltInCount} built-in · {CustomCount} custom";
    }
}
