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
    private readonly LocalizationService _loc;
    private readonly DebugLogService _log;
    private readonly SettingsService? _settings;

    /// <summary>A choice in the tier picker: "" = the agent file's own tier.</summary>
    public sealed record TierChoice(string Key, string Label);
    /// <summary>The picker's choices, in the app's language (the file's tier first).</summary>
    public List<TierChoice> TierChoices =>
    [
        new("", _selected == null ? _loc.T("subag.tierFile") : string.Format(_loc.T("subag.tierFileIs"), _selected.Tier.ToString().ToLowerInvariant())),
        new("cheap", _loc.T("usage.tier.cheap")),
        new("mid", _loc.T("usage.tier.mid")),
        new("deep", _loc.T("usage.tier.deep")),
    ];
    /// <summary>The tier the selected agent runs on when auto model is on (owner 2026-11-08: a tier per agent on the
    /// Sub-agents page). Saved at once in SubAgentTierOverrides; "" removes the override.</summary>
    public string? SelectedTierOverride
    {
        get => _selected == null ? "" : _settings?.Settings.SubAgentTierOverrides?.GetValueOrDefault(_selected.Name) ?? "";
        set
        {
            // null = the ComboBox dropped its selection while its choices were rebuilt (another agent picked, the
            // language switched) — not the owner's choice, so it must not wipe the saved tier.
            if (value == null || _selected == null || _settings == null) return;
            string name = _selected.Name, tier = value is "cheap" or "mid" or "deep" ? value : "";
            if (tier == SelectedTierOverride) return;
            bool ok = _settings.UpdateSettings(st =>
            {
                var map = new Dictionary<string, string>(st.SubAgentTierOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
                if (tier.Length == 0) map.Remove(name); else map[name] = tier;
                st.SubAgentTierOverrides = map;
            });
            StatusMessage = ok ? string.Format(_loc.T("subag.tierSaved"), name) : _loc.T("subag.tierSaveFailed");
            OnPropertyChanged();
        }
    }

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
                OnPropertyChanged(nameof(TierChoices));
                OnPropertyChanged(nameof(SelectedTierOverride));
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
                ? _loc.T("subag.inheritsTools")
                : string.Join(", ", _selected.AllowedTools);
        }
    }

    public string SelectedInvokeSnippet
    {
        get
        {
            if (_selected == null) return "";
            return $"[ACTION: subagent_invoke]\ntype: {_selected.Name}\nprompt: <your task here>\n[/ACTION]";
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

    public SubAgentsViewModel(SubAgentService service, LocalizationService loc, DebugLogService log, SettingsService? settings = null)
    {
        _service = service;
        _loc = loc;
        _log = log;
        _settings = settings;
        _loc.LanguageChanged += () => { OnPropertyChanged(nameof(SelectedToolsDisplay)); OnPropertyChanged(nameof(TierChoices)); };
        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<SubAgentDefinition>(a => { if (a != null) Selected = a; });
        CopyInvokeCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedInvokeSnippet);
                StatusMessage = string.Format(_loc.T("subag.copied"), _selected.Name);
            }
            catch { StatusMessage = _loc.T("subag.clipboardFailed"); }
        });
        OpenUserAgentsFolderCommand = new RelayCommand(() =>
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cluadex", "agents");
            try
            {
                Directory.CreateDirectory(dir);
                // A folder path: the shell opens it in Explorer (it can't "run" a directory).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir, UseShellExecute = true,
                });
                StatusMessage = string.Format(_loc.T("subag.openedFolder"), dir);
            }
            catch (Exception ex)
            {
                _log.Warn("SubAgents", "Open agents folder failed", ex);
                StatusMessage = _loc.T("subag.openFolderFailed");
            }
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
        StatusMessage = string.Format(_loc.T("subag.summary"), Agents.Count, BuiltInCount, CustomCount);
    }
}
