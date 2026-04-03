using System.Collections.ObjectModel;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class PermissionsViewModel : ViewModelBase
{
    private readonly PermissionService _permissionService;

    public ObservableCollection<PermissionRule> Rules { get; } = new();

    private PermissionRule? _selectedRule;
    public PermissionRule? SelectedRule
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

    public List<string> ScopeOptions { get; } = new() { "*", "file", "command", "network" };

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddAllowCommand { get; }
    public RelayCommand AddDenyCommand { get; }

    public PermissionsViewModel(PermissionService permissionService)
    {
        _permissionService = permissionService;

        AddRuleCommand = new RelayCommand(AddRule, () => !string.IsNullOrWhiteSpace(NewPattern));
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => SelectedRule != null);
        SaveCommand = new RelayCommand(Save);
        RefreshCommand = new RelayCommand(Refresh);
        AddAllowCommand = new RelayCommand(() => { NewAction = PermAction.Allow; AddRule(); });
        AddDenyCommand = new RelayCommand(() => { NewAction = PermAction.Deny; AddRule(); });

        Refresh();
    }

    private void AddRule()
    {
        if (string.IsNullOrWhiteSpace(NewPattern)) return;

        var rule = new PermissionRule
        {
            Pattern = NewPattern.Trim(),
            Scope = NewScope,
            Action = NewAction,
        };

        _permissionService.AddRule(rule);
        Rules.Add(rule);

        // Reset form
        NewPattern = "*";
        NewAction = PermAction.Ask;
    }

    private void RemoveRule()
    {
        if (SelectedRule == null) return;

        _permissionService.RemoveRule(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
    }

    private void Save()
    {
        _permissionService.SaveRules();
    }

    private void Refresh()
    {
        _permissionService.LoadRules();
        Rules.Clear();
        foreach (var rule in _permissionService.Rules)
            Rules.Add(rule);
    }
}
