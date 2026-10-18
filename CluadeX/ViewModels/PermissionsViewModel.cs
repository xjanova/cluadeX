using System.Collections.ObjectModel;
using System.Windows;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>A scope choice in the Add Rule combo: the value the agent compares against + its localized label.</summary>
public sealed record PermissionScopeOption(string Value, string Label);

/// <summary>One row of the rules list — the rule plus localized text for its action and scope.</summary>
public sealed class PermissionRuleItem
{
    public PermissionRule Rule { get; }
    public PermAction Action => Rule.Action;
    public string Pattern => Rule.Pattern;
    public string ActionLabel { get; }
    public string ScopeLabel { get; }

    public PermissionRuleItem(PermissionRule rule, string actionLabel, string scopeLabel)
    {
        Rule = rule;
        ActionLabel = actionLabel;
        ScopeLabel = scopeLabel;
    }
}

public class PermissionsViewModel : ViewModelBase
{
    private readonly PermissionService _permissionService;
    private readonly LocalizationService _loc;

    /// <summary>
    /// The scopes AgentToolService actually compares a rule against (exact, case-insensitive): "read" for
    /// read_file/list/search/glob/grep, "write" for write/edit/multi_edit/mkdir, "execute" for run_command and
    /// every other tool. The page used to offer file/command/network — none of which the agent ever asks for —
    /// so every rule made with a non-* scope silently never matched and the user believed they were protected.
    /// </summary>
    public static readonly string[] AgentScopes = { "*", "read", "write", "execute" };

    public ObservableCollection<PermissionRuleItem> Rules { get; } = new();

    private PermissionRuleItem? _selectedRule;
    public PermissionRuleItem? SelectedRule
    {
        get => _selectedRule;
        set => SetProperty(ref _selectedRule, value);
    }

    private string _newPattern = "*";
    public string NewPattern
    {
        get => _newPattern;
        set => SetProperty(ref _newPattern, value);
    }

    private string _newScope = "*";
    public string NewScope
    {
        get => _newScope;
        set => SetProperty(ref _newScope, value);
    }

    private PermAction _newAction = PermAction.Ask;
    public PermAction NewAction
    {
        get => _newAction;
        set => SetProperty(ref _newAction, value);
    }

    private List<PermissionScopeOption> _scopeOptions = new();
    public List<PermissionScopeOption> ScopeOptions
    {
        get => _scopeOptions;
        private set => SetProperty(ref _scopeOptions, value);
    }

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddAllowCommand { get; }
    public RelayCommand AddDenyCommand { get; }

    // ─── Folders outside the project (open_project) ───
    private readonly SettingsService? _settings;

    /// <summary>On: the agent asks before it opens a folder outside the project (default). Off: it never asks.</summary>
    public bool AskBeforeOtherFolders
    {
        get => !string.Equals(_settings?.Settings.RelatedFolderAccess?.Trim(), "allow", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (_settings == null || value == AskBeforeOtherFolders) return;
            _settings.Settings.RelatedFolderAccess = value ? "ask" : "allow";
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Folders answered "Always allow this folder" on a prompt.</summary>
    public ObservableCollection<string> AllowedFolders { get; } = new();
    public RelayCommand<string> RemoveAllowedFolderCommand { get; }

    private void RebuildAllowedFolders()
    {
        AllowedFolders.Clear();
        foreach (var f in _settings?.Settings.AllowedProjectFolders ?? new()) AllowedFolders.Add(f);
    }

    private void RemoveAllowedFolder(string? folder)
    {
        if (_settings == null || string.IsNullOrEmpty(folder)) return;
        var answer = MessageBox.Show(_loc.T("perms.folders.removeConfirm", folder), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _settings.Settings.AllowedProjectFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildAllowedFolders();
    }

    public PermissionsViewModel(PermissionService permissionService, LocalizationService loc, SettingsService? settings = null)
    {
        _permissionService = permissionService;
        _loc = loc;
        _settings = settings;
        RemoveAllowedFolderCommand = new RelayCommand<string>(RemoveAllowedFolder);

        AddRuleCommand = new RelayCommand(AddRule, () => !string.IsNullOrWhiteSpace(NewPattern));
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => SelectedRule != null);
        SaveCommand = new RelayCommand(Save);
        RefreshCommand = new RelayCommand(Refresh);
        AddAllowCommand = new RelayCommand(() => { NewAction = PermAction.Allow; AddRule(); },
            () => !string.IsNullOrWhiteSpace(NewPattern));
        AddDenyCommand = new RelayCommand(() => { NewAction = PermAction.Deny; AddRule(); },
            () => !string.IsNullOrWhiteSpace(NewPattern));

        BuildScopeOptions();
        _loc.LanguageChanged += () =>
        {
            string keep = NewScope;
            BuildScopeOptions();
            NewScope = keep;
            RebuildItems();
        };

        Refresh();
    }

    private void BuildScopeOptions() =>
        ScopeOptions = AgentScopes.Select(s => new PermissionScopeOption(s, ScopeLabel(s))).ToList();

    private string ScopeLabel(string scope) => scope.ToLowerInvariant() switch
    {
        "*" => _loc.T("perms.scopeAny"),
        "read" => _loc.T("perms.scopeRead"),
        "write" => _loc.T("perms.scopeWrite"),
        "execute" => _loc.T("perms.scopeExecute"),
        _ => scope,
    };

    private string ActionLabel(PermAction a) => a switch
    {
        PermAction.Allow => _loc.T("perms.actionAllow"),
        PermAction.Deny => _loc.T("perms.actionDeny"),
        _ => _loc.T("perms.actionAsk"),
    };

    private PermissionRuleItem Wrap(PermissionRule r) => new(r, ActionLabel(r.Action), ScopeLabel(r.Scope));

    private void RebuildItems()
    {
        var selected = SelectedRule?.Rule;
        Rules.Clear();
        foreach (var rule in _permissionService.Rules.ToList())
            Rules.Add(Wrap(rule));
        SelectedRule = selected == null ? null : Rules.FirstOrDefault(i => ReferenceEquals(i.Rule, selected));
    }

    /// <summary>True for a pattern that matches everything (the agent's glob treats "*" and "**" alike for this).</summary>
    private static bool IsMatchAll(string pattern) => pattern.Trim() is "*" or "**";

    private void AddRule()
    {
        if (string.IsNullOrWhiteSpace(NewPattern)) return;
        string pattern = NewPattern.Trim();
        string scope = AgentScopes.Contains(NewScope) ? NewScope : "*";

        // A match-everything Allow on every scope (or on execute) turns the permission system off for the agent:
        // it may then run any command and overwrite any file without a single prompt. Make that a decision.
        if (NewAction == PermAction.Allow && IsMatchAll(pattern) && (scope == "*" || scope == "execute"))
        {
            var answer = MessageBox.Show(_loc.T("perms.allowAllConfirm"), "CluadeX",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { NewAction = PermAction.Ask; return; }
        }

        var rule = new PermissionRule
        {
            Pattern = pattern,
            Scope = scope,
            Action = NewAction,
        };

        _permissionService.AddRule(rule);
        Rules.Add(Wrap(rule));

        // Reset form
        NewPattern = "*";
        NewAction = PermAction.Ask;
    }

    private void RemoveRule()
    {
        if (SelectedRule == null) return;
        var item = SelectedRule;
        var answer = MessageBox.Show(
            _loc.T("perms.removeConfirm", item.ActionLabel, item.ScopeLabel, item.Pattern), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _permissionService.RemoveRule(item.Rule);
        Rules.Remove(item);
        SelectedRule = null;
    }

    private void Save()
    {
        _permissionService.SaveRules();
    }

    private void Refresh()
    {
        _permissionService.LoadRules();
        if (MigrateLegacyScopes(_permissionService.Rules))
            _permissionService.SaveRules();
        RebuildItems();
        RebuildAllowedFolders();
        OnPropertyChanged(nameof(AskBeforeOtherFolders));
    }

    /// <summary>
    /// Rewrite rules saved with the old page's scopes (file / command / network) to the scopes the agent checks.
    /// Without this, rules the user already made keep never matching even after the page is fixed.
    ///   command → execute (run_command is the only "command" tool).
    ///   network → execute (web_fetch / web_search / MCP fall in the agent's catch-all "execute" bucket).
    ///   file    → write for Allow rules (never widen a grant to reads the user did not mean to allow), but
    ///             "*" for Deny/Ask rules: a "file" deny was meant to keep the agent off that path entirely,
    ///             and narrowing it to writes only would quietly start letting the agent read it.
    /// Returns true when anything changed.
    /// </summary>
    public static bool MigrateLegacyScopes(IEnumerable<PermissionRule> rules)
    {
        bool changed = false;
        foreach (var r in rules)
        {
            string old = (r.Scope ?? "*").Trim();
            string mapped = old.ToLowerInvariant() switch
            {
                "command" or "network" => "execute",
                "file" => r.Action == PermAction.Allow ? "write" : "*",
                "" => "*",
                _ => old,
            };
            if (!string.Equals(mapped, r.Scope, StringComparison.Ordinal))
            {
                r.Scope = mapped;
                changed = true;
            }
        }
        return changed;
    }
}
