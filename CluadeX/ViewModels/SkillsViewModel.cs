using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Skills;

namespace CluadeX.ViewModels;

/// <summary>One row of the Skills page: the definition, its on/off switch and how often it was used.</summary>
public class SkillItem : ViewModelBase
{
    private readonly SkillService _service;
    private readonly Action<SkillItem, bool> _onToggled;

    public SkillItem(SkillDefinition def, SkillService service, Action<SkillItem, bool> onToggled)
    {
        Definition = def;
        _service = service;
        _onToggled = onToggled;
        _isEnabled = service.IsEnabled(def.Name);
        _isPinned = service.IsPinned(def.Name);
    }

    public SkillDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string? WhenToUse => Definition.WhenToUse;
    public bool HasWhenToUse => !string.IsNullOrWhiteSpace(Definition.WhenToUse);
    public string? ArgumentHint => Definition.ArgumentHint;
    public bool HasArgumentHint => !string.IsNullOrWhiteSpace(Definition.ArgumentHint);
    public string PromptContent => Definition.PromptContent;
    public bool IsBuiltIn => Definition.IsBuiltIn;
    public bool IsInstalled => !string.IsNullOrEmpty(Definition.SourceUrl);
    public string SourceTag => Definition.Source switch
    {
        SkillSource.BuiltIn => "built-in",
        SkillSource.Project => "project",
        _ => IsInstalled ? "installed" : "user",
    };

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(CardOpacity));
            _onToggled(this, value);
        }
    }

    public double CardOpacity => _isEnabled ? 1.0 : 0.5;

    // ── what it costs (SkillBudget) and whether every turn offers it ──

    private string _costText = "";
    /// <summary>"≈ 40 tok per turn · ≈ 600 tok when it runs".</summary>
    public string CostText { get => _costText; private set => SetProperty(ref _costText, value); }

    private bool _isHeavy;
    /// <summary>Its prompt alone takes over a tenth of the model's window.</summary>
    public bool IsHeavy { get => _isHeavy; private set => SetProperty(ref _isHeavy, value); }

    public int ListingTokens { get; private set; }

    public void RefreshCost(int contextWindow, bool isThai, Func<string, string> t)
    {
        ListingTokens = SkillBudget.ListingTokens(Definition, isThai);
        CostText = string.Format(t("skill.cost"), ListingTokens.ToString("N0"), SkillBudget.LoadTokens(Definition).ToString("N0"));
        IsHeavy = SkillBudget.HeavyToLoad(Definition, contextWindow);
    }

    private bool _isPinned;
    /// <summary>"Every task": offered on every turn while smart picking is on.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (!SetProperty(ref _isPinned, value)) return;
            _service.SetPinned(Name, value);
        }
    }

    public void SyncPinned()
    {
        _isPinned = _service.IsPinned(Name);
        OnPropertyChanged(nameof(IsPinned));
    }

    private string _usageText = "";
    public string UsageText { get => _usageText; set => SetProperty(ref _usageText, value); }

    private int _useCount;
    public int UseCount { get => _useCount; set => SetProperty(ref _useCount, value); }

    public void RefreshUsage(Func<string, string> t)
    {
        var u = _service.GetUsage(Name);
        UseCount = u.Total;
        UsageText = u.Total == 0 && u.Refused == 0
            ? t("skill.neverUsed")
            : string.Format(t("skill.usage"), u.Total, u.Slash, u.Tool, u.Pipe,
                  u.LastUsed?.ToString("yyyy-MM-dd HH:mm") ?? "-")
              + (u.Refused > 0 ? string.Format(t("skill.refusedCount"), u.Refused) : "");
    }

    /// <summary>Re-read the switch after something else changed it (no write back).</summary>
    public void SyncEnabled()
    {
        _isEnabled = _service.IsEnabled(Name);
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(CardOpacity));
    }
}

/// <summary>A skill offered by a trusted source.</summary>
public class RemoteSkillItem
{
    public RemoteSkillItem(SkillInstaller.RemoteSkill remote, bool installed) { Remote = remote; IsInstalled = installed; }
    public SkillInstaller.RemoteSkill Remote { get; }
    public string Name => Remote.Name;
    public string HtmlUrl => Remote.HtmlUrl;
    public bool IsInstalled { get; }
}

/// <summary>
/// View model for the Skills page — browse the skills (built-in + user + project), switch each on or off, see how
/// often each was used, preview the prompt body, copy the /command, and install more from trusted sources.
/// </summary>
public class SkillsViewModel : ViewModelBase
{
    private readonly SkillService _service;
    private readonly SkillInstaller? _installer;
    private readonly SettingsService? _settings;
    private readonly LocalizationService _loc;
    private readonly DebugLogService _log;
    private readonly ContextMemoryService? _context;
    private readonly AgentToolService? _tools;
    private readonly SkillBrainIndex? _brainIndex;

    public ObservableCollection<SkillItem> Skills { get; } = new();

    // ── the gauge: what the switched-on skills cost per turn, against the model's ceiling ──

    private SkillBudget.Gauge _gauge = new(0, 250, 0, 0, SkillBudget.Level.Empty, 0);
    public SkillBudget.Gauge Gauge { get => _gauge; private set => SetProperty(ref _gauge, value); }
    /// <summary>"Empty" | "Light" | "Near" | "Over" — the bar's colour.</summary>
    public string GaugeLevel => _gauge.Level.ToString();
    public double GaugeFill => _gauge.Fill;
    private string _gaugeUsedText = "", _gaugeCapText = "", _gaugeLevelText = "", _gaugeNote = "", _brainStatusText = "";
    public string GaugeUsedText { get => _gaugeUsedText; private set => SetProperty(ref _gaugeUsedText, value); }
    public string GaugeCapText { get => _gaugeCapText; private set => SetProperty(ref _gaugeCapText, value); }
    public string GaugeLevelText { get => _gaugeLevelText; private set => SetProperty(ref _gaugeLevelText, value); }
    public string GaugeNote { get => _gaugeNote; private set => SetProperty(ref _gaugeNote, value); }
    public string BrainStatusText { get => _brainStatusText; private set => SetProperty(ref _brainStatusText, value); }

    /// <summary>Smart picking: the default for every chat (each chat's composer switch overrides it).</summary>
    public bool AutoPickEnabled
    {
        get => _settings?.Settings.SkillAutoPickEnabled ?? true;
        set
        {
            if (_settings == null || _settings.Settings.SkillAutoPickEnabled == value) return;
            _settings.UpdateSettings(x => x.SkillAutoPickEnabled = value);
            SkillPickGate.DefaultChanged();
            OnPropertyChanged();
            RefreshGauge();
        }
    }

    /// <summary>Let the brain's semantic search help pick (cards in Notes/CluadeX-Skills).</summary>
    public bool BrainIndexEnabled
    {
        get => _settings?.Settings.SkillBrainIndexEnabled ?? true;
        set
        {
            if (_settings == null || _settings.Settings.SkillBrainIndexEnabled == value) return;
            _settings.UpdateSettings(x => x.SkillBrainIndexEnabled = value);
            OnPropertyChanged();
            RefreshGauge();
        }
    }

    /// <summary>Re-measure the gauge, every row's cost and the brain line (the model or the switches changed).</summary>
    public void RefreshGauge()
    {
        bool isThai = _loc.CurrentLanguage == "th";
        int window = _context?.MaxContextTokens ?? 200_000;
        foreach (var s in Skills) s.RefreshCost(window, isThai, _loc.T);
        // What the prompt really lists: switched on, user-invocable, with a tool that can run (AgentToolService).
        var offered = _tools?.GetAdvertisedSkills() ?? Skills.Where(s => s.IsEnabled).Select(s => s.Definition).ToList();
        var g = SkillBudget.Measure(offered, isThai, window);
        Gauge = g;
        OnPropertyChanged(nameof(GaugeLevel));
        OnPropertyChanged(nameof(GaugeFill));
        string model = ModelLabel();
        GaugeUsedText = string.Format(_loc.T("skill.gauge.used"), g.Count, g.Used.ToString("N0"),
            g.PercentOfWindow.ToString("0.#"), model, window.ToString("N0"));
        GaugeCapText = string.Format(_loc.T("skill.gauge.cap"), g.Cap.ToString("N0"));
        GaugeLevelText = _loc.T(g.Level switch
        {
            SkillBudget.Level.Empty => "skill.gauge.empty",
            SkillBudget.Level.Light => "skill.gauge.light",
            SkillBudget.Level.Near => "skill.gauge.near",
            _ => AutoPickEnabled ? "skill.gauge.overPicking" : "skill.gauge.over",
        });
        var notes = new List<string>();
        int max = Math.Clamp(_settings?.Settings.SkillPickMax ?? SkillPicker.DefaultMax, 1, 20);
        if (AutoPickEnabled) notes.Add(string.Format(_loc.T("skill.gauge.picking"), max, g.Cap.ToString("N0")));
        var provider = _settings?.Settings.ActiveProvider;
        if (provider is Models.AiProviderType.ClaudeDev or Models.AiProviderType.GeminiDev or Models.AiProviderType.AntigravityDev
            or Models.AiProviderType.OpenAIDev or Models.AiProviderType.GrokDev)
            notes.Add(_loc.T("skill.gauge.cli"));
        else if (provider is Models.AiProviderType.Local or Models.AiProviderType.LlamaServer or Models.AiProviderType.Ollama
                 && (_settings?.Settings.ContextSize ?? 0) < 32000)
            notes.Add(_loc.T("skill.gauge.local"));
        int heavy = Skills.Count(s => s.IsEnabled && s.IsHeavy);
        if (heavy > 0) notes.Add(string.Format(_loc.T("skill.gauge.heavy"), heavy));
        GaugeNote = string.Join(Environment.NewLine, notes);

        if (!BrainIndexEnabled) BrainStatusText = _loc.T("skill.brain.off");
        else if (_brainIndex == null || !_brainIndex.BrainReady) BrainStatusText = _loc.T("skill.brain.notConnected");
        else
        {
            int stale = _brainIndex.Stale(offered).Count;
            BrainStatusText = stale == 0
                ? string.Format(_loc.T("skill.brain.ready"), offered.Count)
                : string.Format(_loc.T("skill.brain.pending"), stale);
        }
    }

    private string ModelLabel()
    {
        var st = _settings?.Settings;
        if (st == null) return "-";
        string? id = st.ProviderConfigs.TryGetValue(st.ActiveProvider.ToString(), out var cfg) ? cfg.EffectiveModelId : null;
        return string.IsNullOrWhiteSpace(id) ? st.ActiveProvider.ToString() : id!;
    }

    private SkillItem? _selected;
    public SkillItem? Selected
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
        : _selected.Definition.AllowedTools.Count == 0
            ? _loc.T("skill.inheritsTools")
            : string.Join(", ", _selected.Definition.AllowedTools);

    public string SelectedInvokeSnippet => _selected == null
        ? ""
        : $"/{_selected.Name}" + (_selected.HasArgumentHint ? " " + _selected.ArgumentHint : "");

    public string SelectedSource => _selected == null ? "" : SourceText(_selected.Definition);

    private string SourceText(SkillDefinition d) => d.Source switch
    {
        SkillSource.BuiltIn => _loc.T("skill.sourceBuiltIn"),
        SkillSource.Project => string.Format(_loc.T("skill.sourceProject"), d.FilePath),
        _ => string.IsNullOrEmpty(d.SourceUrl)
            ? string.Format(_loc.T("skill.sourceUser"), d.FilePath)
            : string.Format(_loc.T("skill.sourceInstalled"), d.SourceUrl, d.FilePath),
    };

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int BuiltInCount => Skills.Count(s => s.IsBuiltIn);
    public int CustomCount => Skills.Count(s => !s.IsBuiltIn);
    public int EnabledCount => Skills.Count(s => s.IsEnabled);

    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand CopyInvokeCommand { get; }
    public ICommand OpenUserSkillsFolderCommand { get; }
    public ICommand EnableAllCommand { get; }

    // ── install mode ────────────────────────────────────────────────────────

    private bool _isInstallMode;
    public bool IsInstallMode
    {
        get => _isInstallMode;
        set { if (SetProperty(ref _isInstallMode, value)) OnPropertyChanged(nameof(IsBrowseMode)); }
    }
    public bool IsBrowseMode => !_isInstallMode;
    public bool CanInstallSkills => _installer != null;

    public ObservableCollection<string> TrustedSources { get; } = new();
    private string? _selectedSourceUrl;
    public string? SelectedSourceUrl { get => _selectedSourceUrl; set => SetProperty(ref _selectedSourceUrl, value); }
    private string _newSourceUrl = "";
    public string NewSourceUrl { get => _newSourceUrl; set => SetProperty(ref _newSourceUrl, value); }

    public ObservableCollection<RemoteSkillItem> RemoteSkills { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    private SkillInstaller.Preview? _preview;
    public SkillInstaller.Preview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value)) return;
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasNoPreview));
            OnPropertyChanged(nameof(PreviewTools));
            OnPropertyChanged(nameof(PreviewProblems));
            OnPropertyChanged(nameof(PreviewWarnings));
            OnPropertyChanged(nameof(HasPreviewProblems));
            OnPropertyChanged(nameof(HasPreviewWarnings));
            OnPropertyChanged(nameof(PreviewLicense));
            ReplaceExisting = false;
        }
    }
    public bool HasPreview => _preview != null;
    public bool HasNoPreview => _preview == null;
    public string PreviewTools => _preview == null ? ""
        : _preview.Skill.AllowedTools.Count == 0 ? _loc.T("skill.inheritsTools") : string.Join(", ", _preview.Skill.AllowedTools);
    public string PreviewProblems => _preview == null ? "" : string.Join("\n", _preview.Problems.Select(p => "✖ " + p));
    public string PreviewWarnings => _preview == null ? "" : string.Join("\n", _preview.Warnings.Select(w => "⚠ " + w));
    public bool HasPreviewProblems => _preview?.Problems.Count > 0;
    public bool HasPreviewWarnings => _preview?.Warnings.Count > 0;
    public string PreviewLicense => string.IsNullOrWhiteSpace(_preview?.Skill.License) ? "-" : _preview!.Skill.License!;

    private bool _replaceExisting;
    public bool ReplaceExisting { get => _replaceExisting; set => SetProperty(ref _replaceExisting, value); }

    public ICommand ToggleInstallModeCommand { get; }
    public ICommand LoadRemoteCommand { get; }
    public ICommand PreviewRemoteCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand AddSourceCommand { get; }
    public ICommand RemoveSourceCommand { get; }

    private CancellationTokenSource? _previewCts;

    public SkillsViewModel(SkillService service, LocalizationService loc, DebugLogService log,
                           SkillInstaller? installer = null, SettingsService? settings = null,
                           ContextMemoryService? context = null, AgentToolService? tools = null, SkillBrainIndex? brainIndex = null)
    {
        _context = context;
        _tools = tools;
        _brainIndex = brainIndex;
        _service = service;
        _installer = installer;
        _settings = settings;
        _loc = loc;
        _log = log;
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(SelectedToolsDisplay));
            OnPropertyChanged(nameof(SelectedSource));
            foreach (var s in Skills) s.RefreshUsage(_loc.T);
            RefreshGauge();
        };
        _service.UsageChanged += () => OnUi(() => { foreach (var s in Skills) s.RefreshUsage(_loc.T); });
        _service.EnabledChanged += () => OnUi(() =>
        {
            foreach (var s in Skills) { s.SyncEnabled(); s.SyncPinned(); }
            OnPropertyChanged(nameof(EnabledCount));
            RefreshGauge();
        });

        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand<SkillItem>(s => { if (s != null) Selected = s; });
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
            string dir = _service.UserSkillsDirectory;
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
        EnableAllCommand = new RelayCommand(() =>
        {
            foreach (var s in Skills.Where(s => !s.IsEnabled).ToList()) s.IsEnabled = true;
        });

        ToggleInstallModeCommand = new RelayCommand(() =>
        {
            IsInstallMode = !IsInstallMode;
            if (IsInstallMode) LoadSources();
            else Refresh();
        });
        LoadRemoteCommand = new AsyncRelayCommand(LoadRemoteAsync);
        PreviewRemoteCommand = new AsyncRelayCommand<RemoteSkillItem>(PreviewRemoteAsync);
        InstallCommand = new RelayCommand(Install);
        AddSourceCommand = new RelayCommand(AddSource);
        RemoveSourceCommand = new RelayCommand(RemoveSource);

        Refresh();
    }

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public void Refresh()
    {
        // Re-select by NAME: ReloadSkills builds new objects, so the old `Selected ??=` kept the
        // previous (stale) instance — a skill deleted from disk stayed selected and previewable.
        var previouslySelectedName = _selected?.Name;
        _service.ReloadSkills();
        Skills.Clear();
        foreach (var s in _service.GetAllSkills().OrderBy(x => !x.IsBuiltIn).ThenBy(x => x.Name))
        {
            var item = new SkillItem(s, _service, OnToggled);
            item.RefreshUsage(_loc.T);
            Skills.Add(item);
        }
        OnPropertyChanged(nameof(BuiltInCount));
        OnPropertyChanged(nameof(CustomCount));
        OnPropertyChanged(nameof(EnabledCount));
        Selected = (previouslySelectedName == null
                       ? null
                       : Skills.FirstOrDefault(s => string.Equals(s.Name, previouslySelectedName, StringComparison.OrdinalIgnoreCase)))
                   ?? Skills.FirstOrDefault();
        StatusMessage = string.Format(_loc.T("skill.summary"), Skills.Count, BuiltInCount, CustomCount)
                        + " · " + string.Format(_loc.T("skill.enabledSummary"), EnabledCount);
        RefreshGauge();
    }

    private void OnToggled(SkillItem item, bool enabled)
    {
        bool ok = _service.SetEnabled(item.Name, enabled);
        OnPropertyChanged(nameof(EnabledCount));
        StatusMessage = !ok
            ? _loc.T("skill.saveFailed")
            : string.Format(_loc.T(enabled ? "skill.turnedOn" : "skill.turnedOff"), item.Name);
    }

    // ── install ─────────────────────────────────────────────────────────────

    private void LoadSources()
    {
        TrustedSources.Clear();
        foreach (var s in _settings?.Settings.TrustedSkillSources ?? new List<string>()) TrustedSources.Add(s);
        if (SelectedSourceUrl == null || !TrustedSources.Contains(SelectedSourceUrl))
            SelectedSourceUrl = TrustedSources.FirstOrDefault();
    }

    private async Task LoadRemoteAsync()
    {
        if (_installer == null || string.IsNullOrWhiteSpace(SelectedSourceUrl)) return;
        IsBusy = true;
        RemoteSkills.Clear();
        Preview = null;
        StatusMessage = string.Format(_loc.T("skill.loading"), SelectedSourceUrl);
        try
        {
            var list = await _installer.ListAsync(SelectedSourceUrl);
            var installed = _service.GetAllSkills().Where(s => s.Source == SkillSource.User)
                .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in list) RemoteSkills.Add(new RemoteSkillItem(r, installed.Contains(r.Name)));
            StatusMessage = string.Format(_loc.T("skill.loaded"), list.Count, SelectedSourceUrl);
        }
        catch (Exception ex)
        {
            _log.Warn("Skills", "List remote skills failed", ex);
            StatusMessage = string.Format(_loc.T("skill.loadFailed"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task PreviewRemoteAsync(RemoteSkillItem? item)
    {
        if (_installer == null || item == null) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = string.Format(_loc.T("skill.fetching"), item.Name);
        try
        {
            var p = await _installer.PreviewAsync(item.Remote, cts.Token);
            if (cts.IsCancellationRequested) return;
            Preview = p;
            StatusMessage = p.CanInstall
                ? string.Format(_loc.T("skill.previewReady"), p.Skill.Name)
                : string.Format(_loc.T("skill.previewBlocked"), p.Skill.Name);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn("Skills", "Preview remote skill failed", ex);
            StatusMessage = string.Format(_loc.T("skill.loadFailed"), ex.Message);
        }
        finally { if (_previewCts == cts) IsBusy = false; }
    }

    private void Install()
    {
        if (_installer == null || _preview == null) return;
        try
        {
            string path = _installer.Install(_preview, ReplaceExisting);
            StatusMessage = string.Format(_loc.T("skill.installed"), _preview.Skill.Name, path);
            _log.Info("Skills", $"Installed skill '{_preview.Skill.Name}' from {_preview.Remote.HtmlUrl} → {path}");
            Preview = _installer.Check(_preview.Remote, _preview.RawText, _preview.ExtraFiles);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_loc.T("skill.installFailed"), ex.Message);
        }
    }

    private void AddSource()
    {
        string url = NewSourceUrl.Trim();
        if (SkillInstaller.ParseSource(url) == null)
        {
            StatusMessage = _loc.T("skill.badSource");
            return;
        }
        if (_settings == null || _settings.Settings.TrustedSkillSources.Contains(url, StringComparer.OrdinalIgnoreCase)) return;
        _settings.UpdateSettings(s => s.TrustedSkillSources.Add(url));
        NewSourceUrl = "";
        LoadSources();
        SelectedSourceUrl = url;
        StatusMessage = string.Format(_loc.T("skill.sourceAdded"), url);
    }

    private void RemoveSource()
    {
        string? url = SelectedSourceUrl;
        if (_settings == null || url == null) return;
        _settings.UpdateSettings(s => s.TrustedSkillSources.RemoveAll(x => x.Equals(url, StringComparison.OrdinalIgnoreCase)));
        RemoteSkills.Clear();
        Preview = null;
        LoadSources();
        StatusMessage = string.Format(_loc.T("skill.sourceRemoved"), url);
    }
}
