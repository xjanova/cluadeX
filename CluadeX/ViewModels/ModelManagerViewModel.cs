using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Helpers;
using CluadeX.Services.Providers;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

/// <summary>
/// A filter / sort choice: the key the code uses + its localized label (+ a Segoe MDL2 glyph for chips). The label is
/// updated in place on a language change: replacing the option lists cleared the chips' and the sort box's selection.
/// </summary>
public sealed class ModelCategoryOption : System.ComponentModel.INotifyPropertyChanged
{
    public ModelCategoryOption(string key, string label, string glyph = "")
    {
        Key = key;
        _label = label;
        Glyph = glyph;
    }

    public string Key { get; }
    public string Glyph { get; }
    private string _label;
    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Label)));
        }
    }
    public override string ToString() => Label;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public class ModelManagerViewModel : ViewModelBase
{
    private readonly HuggingFaceService _huggingFaceService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly LlamaInferenceService _inferenceService;
    private readonly LocalGgufProvider _localProvider;
    private readonly SettingsService _settingsService;
    private readonly AiProviderManager _providerManager;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _searchCts;
    private HardwareProfile? _hw;
    private RecommendedModel? _downloadingModel;

    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadStatus = string.Empty;
    private string _modelLoadStatus = string.Empty;
    private bool _isLoadingModel;
    private string? _selectedModelPath;
    private string _gpuRecommendation = string.Empty;
    private int _availableVramMB;
    private string _searchResultInfo = string.Empty;

    public ObservableCollection<ModelInfo> LocalModels { get; } = new();
    /// <summary>The catalog after the job filter, text filter, "older models" switch and sort.</summary>
    public ObservableCollection<RecommendedModel> RecommendedModels { get; } = new();
    /// <summary>Up to three "Best for your PC" picks for the selected job.</summary>
    public ObservableCollection<RecommendedModel> TopPicks { get; } = new();
    public ObservableCollection<ModelInfo> SearchResults { get; } = new();
    public ObservableCollection<ModelSource> ModelSources { get; } = new();

    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value); }
    public bool IsSearching { get => _isSearching; set => SetProperty(ref _isSearching, value); }
    public bool IsDownloading { get => _isDownloading; set => SetProperty(ref _isDownloading, value); }
    public double DownloadProgress { get => _downloadProgress; set => SetProperty(ref _downloadProgress, value); }
    public string DownloadStatus { get => _downloadStatus; set => SetProperty(ref _downloadStatus, value); }
    public string ModelLoadStatus { get => _modelLoadStatus; set => SetProperty(ref _modelLoadStatus, value); }
    public bool IsLoadingModel { get => _isLoadingModel; set => SetProperty(ref _isLoadingModel, value); }
    public string? SelectedModelPath { get => _selectedModelPath; set => SetProperty(ref _selectedModelPath, value); }
    /// <summary>One-line hardware verdict (kept for the status line and older callers).</summary>
    public string GpuRecommendation { get => _gpuRecommendation; set => SetProperty(ref _gpuRecommendation, value); }
    public int AvailableVramMB { get => _availableVramMB; set => SetProperty(ref _availableVramMB, value); }
    public string SearchResultInfo { get => _searchResultInfo; set => SetProperty(ref _searchResultInfo, value); }

    // ─── Hardware card ───
    private string _hwGpuText = "", _hwGpuDetail = "", _hwRamText = "", _hwRamDetail = "", _hwBackendText = "", _hwBackendDetail = "", _hwNote = "";
    private bool _isDetecting = true;
    public string HwGpuText { get => _hwGpuText; private set => SetProperty(ref _hwGpuText, value); }
    public string HwGpuDetail { get => _hwGpuDetail; private set => SetProperty(ref _hwGpuDetail, value); }
    public string HwRamText { get => _hwRamText; private set => SetProperty(ref _hwRamText, value); }
    public string HwRamDetail { get => _hwRamDetail; private set => SetProperty(ref _hwRamDetail, value); }
    public string HwBackendText { get => _hwBackendText; private set => SetProperty(ref _hwBackendText, value); }
    public string HwBackendDetail { get => _hwBackendDetail; private set => SetProperty(ref _hwBackendDetail, value); }
    /// <summary>A warning about the hardware (non-NVIDIA GPU, low VRAM free right now); empty when none.</summary>
    public string HwNote { get => _hwNote; private set => SetProperty(ref _hwNote, value); }
    public bool IsDetecting { get => _isDetecting; private set => SetProperty(ref _isDetecting, value); }
    /// <summary>VRAM in use (0-100) for the meter on the hardware card.</summary>
    private double _vramUsedPercent;
    public double VramUsedPercent { get => _vramUsedPercent; private set => SetProperty(ref _vramUsedPercent, value); }

    // ─── Filters ───
    private static readonly (string Key, ModelTasks Task, string Glyph)[] TaskDefs =
    {
        ("All", ModelTasks.None, ""),
        ("Coding", ModelTasks.Coding, ""),
        ("Chat", ModelTasks.Chat, ""),
        ("Reasoning", ModelTasks.Reasoning, ""),
        ("Vision", ModelTasks.Vision, ""),
        ("Thai", ModelTasks.Thai, ""),
    };
    private static readonly string[] SortKeys = { "Best", "Newest", "Fastest", "Smallest", "Name" };

    private List<ModelCategoryOption> _taskOptions = new();
    public List<ModelCategoryOption> TaskOptions { get => _taskOptions; private set => SetProperty(ref _taskOptions, value); }
    private List<ModelCategoryOption> _sortOptions = new();
    public List<ModelCategoryOption> SortOptions { get => _sortOptions; private set => SetProperty(ref _sortOptions, value); }

    private string _selectedTask = "All";
    /// <summary>The job the recommendations are for: All / Coding / Chat / Reasoning / Vision / Thai.</summary>
    public string SelectedTask
    {
        get => _selectedTask;
        set { if (value != null && SetProperty(ref _selectedTask, value)) RebuildCatalog(); }
    }

    private string _selectedSort = "Best";
    public string SelectedSort
    {
        get => _selectedSort;
        set { if (value != null && SetProperty(ref _selectedSort, value)) RebuildCatalog(); }
    }

    private string _catalogFilter = "";
    /// <summary>Text filter over the curated catalog (name, maker, description, architecture).</summary>
    public string CatalogFilter
    {
        get => _catalogFilter;
        set { if (SetProperty(ref _catalogFilter, value ?? "")) RebuildCatalog(); }
    }

    private bool _showLegacy;
    /// <summary>Also list the older generation (Qwen2.5, Llama 3.x, Gemma 3 …).</summary>
    public bool ShowLegacy
    {
        get => _showLegacy;
        set { if (SetProperty(ref _showLegacy, value)) RebuildCatalog(); }
    }

    private string _catalogCountText = "";
    public string CatalogCountText { get => _catalogCountText; private set => SetProperty(ref _catalogCountText, value); }
    private string _topPicksTitle = "";
    public string TopPicksTitle { get => _topPicksTitle; private set => SetProperty(ref _topPicksTitle, value); }
    public bool HasTopPicks => TopPicks.Count > 0;
    /// <summary>"Nothing runs well here" — only once the hardware is known (not while it is still being read).</summary>
    public bool ShowNoPicks => TopPicks.Count == 0 && _hw != null && !IsDetecting;
    public string InstalledCountText => LocalModels.Count > 0 ? LocalModels.Count.ToString(CultureInfo.InvariantCulture) : "";

    // ─── Search Pagination ───
    private int _searchPage = 1;
    private int _searchTotalPages = 1;
    private int _searchItemsPerPage = 12;
    private List<ModelInfo> _allSearchResults = new();
    public int SearchPage
    {
        get => _searchPage;
        set { if (SetProperty(ref _searchPage, value)) ApplySearchPagination(); }
    }
    public int SearchTotalPages { get => _searchTotalPages; set => SetProperty(ref _searchTotalPages, value); }
    public string SearchPageInfo => $"{_searchPage} / {_searchTotalPages}";
    /// <summary>Drives the visibility of the search pagination bar.</summary>
    public bool HasSearchResults => _allSearchResults.Count > 0;
    public ICommand NextSearchPageCommand => new RelayCommand(() => { if (_searchPage < SearchTotalPages) SearchPage++; }, () => _searchPage < SearchTotalPages);
    public ICommand PrevSearchPageCommand => new RelayCommand(() => { if (_searchPage > 1) SearchPage--; }, () => _searchPage > 1);

    // Catalog layout: List (one card per row) or Grid (as many columns as fit).
    // Toggled from the filter bar; persisted via SettingsService so the user's choice survives restarts.
    private bool _isGridView;
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
            {
                OnPropertyChanged(nameof(IsListView));
                _settingsService.UpdateSettings(s => s.ModelCatalogGridView = value);
            }
        }
    }
    public bool IsListView => !IsGridView;
    public ICommand SwitchToListViewCommand => new RelayCommand(() => IsGridView = false);
    public ICommand SwitchToGridViewCommand => new RelayCommand(() => IsGridView = true);

    public ICommand RefreshLocalModelsCommand { get; }
    public ICommand RefreshHardwareCommand { get; }
    public ICommand LoadModelCommand { get; }
    public ICommand UnloadModelCommand { get; }
    public ICommand DownloadRecommendedCommand { get; }
    public ICommand DownloadProjectorCommand { get; }
    public ICommand SearchModelsCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand DownloadSearchResultCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand DeleteModelCommand { get; }
    public ICommand BrowseAndLoadModelCommand { get; }
    public ICommand AddModelDirectoryCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand OpenModelFolderCommand { get; }
    public ICommand ApplyFitCommand { get; }
    public ICommand ClearCatalogFilterCommand { get; }

    /// <summary>One-click "Apply recommended settings": persist the card's suggested -ngl
    /// (GpuLayerCount) so the next model load uses it — closes the old do-it-manually gap.</summary>
    private void ApplyFit(RecommendedModel? model)
    {
        if (model == null) return;
        int layers = model.RecommendedGpuLayers;
        _settingsService.UpdateSettings(s => s.GpuLayerCount = layers);
        ModelLoadStatus = layers == -1
            ? _loc.T("modelsm.fitAppliedAll", model.DisplayName)
            : _loc.T("modelsm.fitAppliedLayers", layers, model.DisplayName);
    }

    private List<RecommendedModel> _allRecommended = new();

    public ModelManagerViewModel(
        HuggingFaceService huggingFaceService,
        GpuDetectionService gpuDetectionService,
        LlamaInferenceService inferenceService,
        LocalGgufProvider localProvider,
        SettingsService settingsService,
        AiProviderManager providerManager,
        LocalizationService loc,
        DebugLogService? log = null)
    {
        _huggingFaceService = huggingFaceService;
        _gpuDetectionService = gpuDetectionService;
        _inferenceService = inferenceService;
        _localProvider = localProvider;
        _settingsService = settingsService;
        _providerManager = providerManager;
        _loc = loc;
        _log = log;
        _isGridView = settingsService.Settings.ModelCatalogGridView;
        _selectedModelPath = settingsService.Settings.SelectedModelPath;

        RefreshLocalModelsCommand = new RelayCommand(RefreshLocalModels);
        RefreshHardwareCommand = new RelayCommand(() => DetectHardwareAsync(clearCache: true), () => !IsDetecting);
        LoadModelCommand = new AsyncRelayCommand<string>(LoadModel);
        UnloadModelCommand = new AsyncRelayCommand(UnloadModel);
        DownloadRecommendedCommand = new AsyncRelayCommand<RecommendedModel>(DownloadRecommended);
        DownloadProjectorCommand = new AsyncRelayCommand<RecommendedModel>(DownloadProjector);
        SearchModelsCommand = new AsyncRelayCommand(SearchModels);
        CancelSearchCommand = new RelayCommand(() => _searchCts?.Cancel(), () => IsSearching);
        DownloadSearchResultCommand = new AsyncRelayCommand<ModelInfo>(DownloadSearchResult);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        DeleteModelCommand = new AsyncRelayCommand<ModelInfo>(DeleteModel);
        BrowseAndLoadModelCommand = new AsyncRelayCommand(BrowseAndLoadModel);
        AddModelDirectoryCommand = new RelayCommand(AddModelDirectory);
        OpenUrlCommand = new RelayCommand<string>(OpenUrl);
        OpenModelFolderCommand = new RelayCommand(OpenModelFolder);
        ApplyFitCommand = new RelayCommand<RecommendedModel>(ApplyFit);
        ClearCatalogFilterCommand = new RelayCommand(() => CatalogFilter = "");

        BuildModelSources();
        BuildOptions();
        _loc.LanguageChanged += () =>
        {
            BuildModelSources();
            BuildOptions();   // relabels in place — the selection stays
            UpdateHardwareText();
            RefreshLocalModels();   // re-labels the fit badges, then rebuilds the catalog
        };

        _allRecommended = _huggingFaceService.GetRecommendedModels(null);
        Initialize();
    }

    /// <summary>The "Where to get models" cards, with descriptions in the UI language.</summary>
    private void BuildModelSources()
    {
        ModelSources.Clear();
        foreach (var source in HuggingFaceService.PopularSources)
        {
            string key = "modelsm.src." + source.Name.Replace(" ", "");
            string text = _loc.T(key);
            ModelSources.Add(new ModelSource
            {
                Name = source.Name,
                Url = source.Url,
                Icon = source.Icon,
                Description = text == key ? source.Description : text,
            });
        }
    }

    private void BuildOptions()
    {
        if (TaskOptions.Count == 0)
        {
            TaskOptions = TaskDefs.Select(t => new ModelCategoryOption(t.Key, "", t.Glyph)).ToList();
            SortOptions = SortKeys.Select(k => new ModelCategoryOption(k, "")).ToList();
        }
        foreach (var t in TaskOptions) t.Label = _loc.T("modelsm.task" + t.Key);
        foreach (var o in SortOptions) o.Label = _loc.T("modelsm.sort" + o.Key);
    }

    private void Initialize()
    {
        RefreshLocalModels();
        DetectHardwareAsync(clearCache: false);
    }

    /// <summary>Read GPU / RAM off the UI thread (nvidia-smi and WMI can take a second), then re-rank.</summary>
    private void DetectHardwareAsync(bool clearCache)
    {
        IsDetecting = true;
        OnPropertyChanged(nameof(ShowNoPicks));
        HwGpuText = _loc.T("modelsm.hwDetecting");
        string backend = _settingsService.Settings.GpuBackend;
        bool loaded = _localProvider.IsReady;
        Task.Run(() =>
        {
            HardwareProfile hw;
            try
            {
                if (clearCache) _gpuDetectionService.ClearCache();
                hw = _gpuDetectionService.DetectHardware(backend, loaded);
            }
            catch (Exception ex)
            {
                _log?.Warn("Models", "Hardware detection failed", ex);
                hw = new HardwareProfile { SystemRamMB = 8192 };
            }
            try { App.Current?.Dispatcher.Invoke(() => ApplyHardware(hw)); }
            catch (Exception ex) { _log?.Warn("Models", "Applying the hardware reading failed", ex); }   // app closing
        });
    }

    /// <summary>Use a hardware profile (also the harness's way in: no nvidia-smi, any PC it likes).</summary>
    public void ApplyHardware(HardwareProfile hw)
    {
        _hw = hw;
        AvailableVramMB = hw.VramTotalMB;
        IsDetecting = false;
        UpdateHardwareText();
        RefreshLocalModels();
        CommandManager.InvalidateRequerySuggested();
    }

    public HardwareProfile? Hardware => _hw;

    private int ContextTokens => (int)Math.Clamp(_settingsService.Settings.ContextSize, 2048u, 262144u);

    private static string Gb(int mb) => (mb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);

    private void UpdateHardwareText()
    {
        var hw = _hw;
        if (hw == null) return;
        if (hw.VramTotalMB > 0)
        {
            HwGpuText = hw.GpuCount > 1 ? _loc.T("modelsm.hwGpuMulti", hw.GpuCount, hw.GpuName) : hw.GpuName;
            HwGpuDetail = hw.VramFreeMB > 0
                ? _loc.T("modelsm.hwVramFree", Gb(hw.VramTotalMB), Gb(hw.VramFreeMB))
                : _loc.T("modelsm.hwVram", Gb(hw.VramTotalMB));
            VramUsedPercent = hw.VramFreeMB > 0 ? Math.Clamp(100.0 * (hw.VramTotalMB - hw.VramFreeMB) / hw.VramTotalMB, 0, 100) : 0;
        }
        else
        {
            HwGpuText = _loc.T("modelsm.hwNoGpu");
            HwGpuDetail = _loc.T("modelsm.hwNoGpuDetail");
            VramUsedPercent = 0;
        }
        HwRamText = _loc.T("modelsm.hwRam", Gb(hw.SystemRamMB));
        HwRamDetail = _loc.T("modelsm.hwRamDetail", hw.CpuThreads, Math.Round(hw.RamBandwidthGBs));
        HwBackendText = hw.CanOffload ? _loc.T("modelsm.hwBackendGpu") : _loc.T("modelsm.hwBackendCpu");
        HwBackendDetail = _loc.T("modelsm.hwContext", ContextTokens.ToString("N0", CultureInfo.InvariantCulture), Gb(hw.UsableVramMB), Gb(hw.RamBudgetMB));

        string note = "";
        if (hw.VramTotalMB > 0 && !hw.CanOffload)
            note = string.Equals(_settingsService.Settings.GpuBackend, "CPU", StringComparison.OrdinalIgnoreCase)
                ? _loc.T("modelsm.hwNoteCpuChosen")
                : _loc.T("modelsm.hwNoteNotNvidia", hw.GpuName);
        else if (hw.CanOffload && hw.VramFreeMB > 0 && !hw.LocalModelLoaded && hw.VramFreeMB < hw.VramTotalMB * 0.6)
            note = _loc.T("modelsm.hwNoteVramBusy", Gb(hw.VramTotalMB - hw.VramFreeMB));
        HwNote = note;

        GpuRecommendation = hw.VramTotalMB > 0
            ? $"{HwGpuText} — {HwGpuDetail} · {HwRamText}"
            : $"{HwGpuText} · {HwRamText}";
    }

    private static ModelTasks TaskOf(string key) => TaskDefs.FirstOrDefault(t => t.Key == key).Task;

    // ─── Catalog ───

    /// <summary>Evaluate every catalog model on this PC, then filter / sort / pick. Cheap: pure arithmetic.</summary>
    private void RebuildCatalog()
    {
        var installed = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in LocalModels)
            if (!string.IsNullOrEmpty(m.FileName)) installed.TryAdd(m.FileName, m);

        var hw = _hw;
        var task = TaskOf(_selectedTask);
        var today = DateTime.Today;
        string? customBackend = _settingsService.Settings.CustomLlamaCppBackendPath;

        foreach (var model in _allRecommended)
        {
            model.ArchSupported = LlamaBackendInfo.SupportsArchitecture(model.Architecture, customBackend);
            model.DescriptionText = LocalizedOr("modelsm.desc." + model.Id, model.Description);
            model.ParamsText = model.IsMoE
                ? _loc.T("modelsm.paramsMoe", FmtB(model.TotalParamsB), FmtB(model.ActiveParamsB))
                : _loc.T("modelsm.params", FmtB(model.TotalParamsB > 0 ? model.TotalParamsB : model.ParameterBillions));
            model.ContextText = model.NativeContext > 0 ? _loc.T("modelsm.ctx", ModelAdvisor.ContextShort(model.NativeContext)) : "";
            model.ReleasedText = FormatReleased(model.Released);
            model.TaskChips = TaskDefs.Where(t => t.Task != ModelTasks.None && model.Tasks.HasFlag(t.Task))
                                      .Select(t => _loc.T("modelsm.task" + t.Key)).ToList();

            // Installed: any quant of this model in the model folders.
            var file = model.AllQuants.Select(q => installed.TryGetValue(Path.GetFileName(q.FileName), out var lm) ? (q, lm) : default)
                                      .FirstOrDefault(x => x.lm != null);
            model.IsInstalled = file.lm != null;
            model.InstalledPath = file.lm?.LocalPath;
            model.InstalledQuant = file.q?.Name ?? "";
            model.IsLoaded = model.IsInstalled && SamePath(model.InstalledPath, SelectedModelPath) && _localProvider.IsReady;
            model.ProjectorSupported = LlamaBackendInfo.SupportsProjectorType(model.ProjectorType, customBackend);
            model.VisionReady = model.IsInstalled && model.Projector != null && VisionProjector.FindFor(model.InstalledPath) != null;

            if (hw == null) { model.Plans = new(); model.SelectedPlan = null; model.Reasons = new(); model.Score = 0; continue; }

            var (pick, all) = ModelAdvisor.PickQuant(model, hw, ContextTokens);
            var plans = all.OrderBy(e => e.Quant.SizeBytes).Select(e => ToPlan(e, ReferenceEquals(e, pick), hw)).ToList();
            // A quant the user picked by hand survives a refresh (new hardware reading, language, a finished download).
            string? userChoice = model.SelectedPlan is { IsAuto: false } chosen ? chosen.Name : null;
            model.Plans = plans;
            // An installed quant is what the card should describe; otherwise the advisor's pick.
            model.SelectedPlan = plans.FirstOrDefault(p => model.IsInstalled && p.Name == model.InstalledQuant)
                                 ?? plans.FirstOrDefault(p => p.Name == userChoice)
                                 ?? plans.First(p => p.IsAuto);
            model.Score = ModelAdvisor.Score(model, pick, task, today);
            model.Reasons = ModelAdvisor.Reasons(model, pick, hw, task, today).Select(r => _loc.T(r.Key, r.Args)).ToList();
            model.PickRank = 0;
        }

        // Top picks for the job.
        TopPicks.Clear();
        if (hw != null)
        {
            var picks = _allRecommended
                .Where(m => m.SelectedPlan != null)
                .Select(m => (m, est: ModelAdvisor.PickQuant(m, hw, ContextTokens).Pick))
                .Where(x => ModelAdvisor.IsPickable(x.m, x.est, task))
                .OrderByDescending(x => x.m.Score)
                .Take(3)
                .ToList();
            for (int i = 0; i < picks.Count; i++)
            {
                picks[i].m.PickRank = i + 1;
                TopPicks.Add(picks[i].m);
            }
        }
        TopPicksTitle = _loc.T("modelsm.topPicksFor", _loc.T("modelsm.taskFor" + _selectedTask));
        OnPropertyChanged(nameof(HasTopPicks));
        OnPropertyChanged(nameof(ShowNoPicks));

        // The list.
        string q = _catalogFilter.Trim();
        var list = _allRecommended.Where(m => _showLegacy || !m.IsLegacy)
                                  .Where(m => task == ModelTasks.None || m.Tasks.HasFlag(task))
                                  .Where(m => q.Length == 0 || Matches(m, q))
                                  .ToList();
        IEnumerable<RecommendedModel> sorted = _selectedSort switch
        {
            "Newest" => list.OrderByDescending(m => m.Released).ThenByDescending(m => m.Score),
            "Fastest" => list.OrderByDescending(m => m.SelectedPlan?.TokensPerSecond ?? 0),
            "Smallest" => list.OrderBy(m => m.SelectedPlan?.SizeBytes ?? m.ApproxSizeBytes),
            "Name" => list.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(m => m.IsLegacy).ThenByDescending(m => m.Score),
        };
        RecommendedModels.Clear();
        foreach (var m in sorted) RecommendedModels.Add(m);

        int hiddenLegacy = _showLegacy ? 0 : _allRecommended.Count(m => m.IsLegacy && (task == ModelTasks.None || m.Tasks.HasFlag(task)));
        CatalogCountText = hiddenLegacy > 0
            ? _loc.T("modelsm.countHidden", RecommendedModels.Count, hiddenLegacy)
            : _loc.T("modelsm.count", RecommendedModels.Count);
    }

    private static bool Matches(RecommendedModel m, string q) =>
        m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || m.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)
        || m.RepoId.Contains(q, StringComparison.OrdinalIgnoreCase)
        || m.Architecture.Contains(q, StringComparison.OrdinalIgnoreCase)
        || m.DescriptionText.Contains(q, StringComparison.OrdinalIgnoreCase)
        || m.TaskChips.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase));

    private string LocalizedOr(string key, string fallback)
    {
        string text = _loc.T(key);
        return text == key ? fallback : text;
    }

    private static string FmtB(double b) =>
        b.ToString(b >= 10 || b == Math.Floor(b) ? "0" : "0.#", CultureInfo.InvariantCulture);

    private string FormatReleased(string released)
    {
        if (!DateTime.TryParseExact(released, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return released;
        var culture = _loc.CurrentLanguage == "th" ? new CultureInfo("th-TH") { DateTimeFormat = { Calendar = new GregorianCalendar() } } : CultureInfo.GetCultureInfo("en-US");
        return d.ToString("MMM yyyy", culture);
    }

    private QuantPlan ToPlan(QuantEstimate e, bool isAuto, HardwareProfile hw)
    {
        var (label, hint) = FitText(e, hw);
        string speed = SpeedText(e.TokensPerSecond, e.Tier);
        string memory = e.Tier == ModelFitTier.TooLarge || e.GpuFraction >= 1 || hw.UsableVramMB <= 0
            ? _loc.T("modelsm.memNeed", Gb(e.NeedMB))
            : _loc.T("modelsm.memSplit", Gb(e.NeedMB), (int)Math.Round(e.GpuFraction * 100));
        return new QuantPlan
        {
            Quant = e.Quant,
            NeedMB = e.NeedMB,
            GpuFraction = e.GpuFraction,
            TokensPerSecond = e.TokensPerSecond,
            FitTier = e.Tier,
            SuggestedGpuLayers = e.SuggestedGpuLayers,
            IsAuto = isAuto,
            FitLabel = label,
            FitHint = hint,
            SpeedText = speed,
            MemoryText = memory,
            Label = (isAuto ? "★ " : "") + $"{e.Quant.Name} · {ModelInfo.FormatFileSize(e.Quant.SizeBytes)} · {label}",
        };
    }

    private string SpeedText(double tps, ModelFitTier tier) =>
        tier == ModelFitTier.TooLarge || tps <= 0 ? "" : _loc.T("modelsm.speed", tps >= 10 ? Math.Round(tps).ToString(CultureInfo.InvariantCulture) : tps.ToString("0.#", CultureInfo.InvariantCulture));

    /// <summary>Badge text + its explanation for an estimate.</summary>
    private (string Label, string Hint) FitText(QuantEstimate e, HardwareProfile hw)
    {
        string need = Gb(e.NeedMB), usable = Gb(hw.UsableVramMB);
        if (hw.UsableVramMB <= 0)
        {
            return e.Tier == ModelFitTier.TooLarge
                ? (_loc.T("modelsm.fitTooLarge"), _loc.T("modelsm.fitTooLargeRamHint", need, Gb(hw.RamBudgetMB)))
                : (_loc.T("modelsm.fitCpuOnly"), _loc.T("modelsm.fitCpuHint", need));
        }
        return e.Tier switch
        {
            ModelFitTier.Excellent => (_loc.T("modelsm.fitFast"), _loc.T("modelsm.fitFastHint2", need, usable)),
            ModelFitTier.Good => (_loc.T("modelsm.fitGood"), _loc.T("modelsm.fitGoodHint2", need, usable)),
            ModelFitTier.Partial => (_loc.T("modelsm.fitPartial"), _loc.T("modelsm.fitPartialHint2", (int)Math.Round(e.GpuFraction * 100), e.SuggestedGpuLayers)),
            ModelFitTier.Poor => (_loc.T("modelsm.fitSlow"), _loc.T("modelsm.fitSlowHint2", (int)Math.Round(e.GpuFraction * 100))),
            _ => (_loc.T("modelsm.fitTooLarge"), _loc.T("modelsm.fitTooLargeHint2", need, usable, Gb(hw.RamBudgetMB))),
        };
    }

    /// <summary>Badge + speed for a GGUF file that is not (or not necessarily) in the catalog.</summary>
    private void DescribeFile(ModelInfo info)
    {
        var hw = _hw;
        if (hw == null || info.FileSize <= 0) return;
        string leaf = Path.GetFileName(info.FileName);
        var catalog = _allRecommended.FirstOrDefault(m => m.AllQuants.Any(q => string.Equals(Path.GetFileName(q.FileName), leaf, StringComparison.OrdinalIgnoreCase)));
        QuantEstimate e;
        if (catalog != null)
        {
            var q = catalog.AllQuants.First(x => string.Equals(Path.GetFileName(x.FileName), leaf, StringComparison.OrdinalIgnoreCase));
            e = ModelAdvisor.Estimate(catalog, new QuantOption(q.Name, q.FileName, info.FileSize), hw, ContextTokens);
            info.CatalogName = catalog.DisplayName;
        }
        else
        {
            double.TryParse(info.ParameterDisplay.TrimEnd('B'), NumberStyles.Float, CultureInfo.InvariantCulture, out double paramsB);
            e = ModelAdvisor.EstimateFile(info.FileSize, paramsB, hw, ContextTokens);
        }
        var (label, hint) = FitText(e, hw);
        info.FitTier = e.Tier;
        info.FitLabel = label;
        info.FitHint = hint;
        info.SpeedText = SpeedText(e.TokensPerSecond, e.Tier);
    }

    private void RefreshLocalModels()
    {
        List<ModelInfo> models;
        try { models = _huggingFaceService.GetLocalModels(); }
        catch (Exception ex)
        {
            _log?.Warn("Models", "Scanning model folders failed", ex);
            models = new List<ModelInfo>();
        }
        LocalModels.Clear();
        foreach (var model in models)
        {
            model.IsLoaded = SamePath(model.LocalPath, SelectedModelPath) && _localProvider.IsReady;
            model.HasVision = VisionProjector.FindFor(model.LocalPath) != null;
            DescribeFile(model);
            LocalModels.Add(model);
        }
        OnPropertyChanged(nameof(InstalledCountText));

        // Re-evaluate the catalog: installed state, fit badges.
        RebuildCatalog();
    }

    private async Task LoadModel(string? path)
    {
        if (string.IsNullOrEmpty(path) || _localProvider.IsLoading) return;
        string name = Path.GetFileNameWithoutExtension(path);
        IsLoadingModel = true;
        ModelLoadStatus = _loc.T("modelsm.loading", name);
        try
        {
            // Ensure active provider is Local so events flow to MainViewModel (overlay)
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);

            // Created on the UI thread, so Progress<T> posts back to it — no blocking Dispatcher.Invoke.
            var progress = new Progress<string>(s => ModelLoadStatus = s);

            // Route through LocalGgufProvider — it inspects the GGUF arch and
            // transparently picks LLamaSharp or llama-server.exe (Gemma 4 etc.)
            await _localProvider.LoadModelAsync(path, progress);

            SelectedModelPath = path;
            ModelLoadStatus = _localProvider.LoadedProjectorPath != null ? _loc.T("modelsm.loadedVision", name)
                : _localProvider.VisionFallbackReason.Length > 0 ? _loc.T("modelsm.loadedNoVision", name, _localProvider.VisionFallbackReason)
                : _loc.T("modelsm.loaded", name);
            _settingsService.UpdateSettings(s =>
            {
                s.SelectedModelPath = path;
                s.SelectedModelName = name;
            });
        }
        catch (Exception ex)
        {
            _log?.Error("Models", $"Loading {path} failed", ex);
            ModelLoadStatus = ex switch
            {
                FileNotFoundException => _loc.T("modelsm.loadFailedMissing", name),
                OutOfMemoryException or InsufficientMemoryException => _loc.T("modelsm.loadFailedMemory", name),
                _ => _loc.T("modelsm.loadFailed", name),
            };
        }
        finally
        {
            IsLoadingModel = false;
            RefreshLocalModels();
        }
    }

    private async Task UnloadModel()
    {
        // Through the router: the default GPU path runs llama-server, and unloading only LLamaSharp (as this did)
        // left the server — and its VRAM — running under a "Model unloaded" message.
        try { await _localProvider.UnloadAsync(); }
        catch (Exception ex) { _log?.Warn("Models", "Unload failed", ex); }
        try { _inferenceService.UnloadModel(); } catch { /* already unloaded */ }
        SelectedModelPath = null;
        ModelLoadStatus = _loc.T("modelsm.unloaded");

        // Clear settings so the model doesn't auto-load on next app restart
        _settingsService.UpdateSettings(s =>
        {
            s.SelectedModelPath = null;
            s.SelectedModelName = null;
        });
        RefreshLocalModels();
    }

    /// <summary>Browse for any GGUF file on disk and load it immediately.</summary>
    private async Task BrowseAndLoadModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = _loc.T("modelsm.browseTitle"),
            Filter = _loc.T("modelsm.browseFilter"),
        };
        if (Directory.Exists(_settingsService.Settings.ModelDirectory))
            dialog.InitialDirectory = _settingsService.Settings.ModelDirectory;

        if (dialog.ShowDialog() == true)
        {
            string path = dialog.FileName;

            // If file is not in known directories, offer to add the parent directory
            string? parentDir = Path.GetDirectoryName(path);
            if (parentDir != null)
            {
                var settings = _settingsService.Settings;
                bool isKnown = string.Equals(parentDir, settings.ModelDirectory, StringComparison.OrdinalIgnoreCase)
                    || (settings.AdditionalModelDirectories?.Any(d =>
                        parentDir.StartsWith(d, StringComparison.OrdinalIgnoreCase)) == true);

                if (!isKnown)
                {
                    settings.AdditionalModelDirectories ??= new();
                    if (!settings.AdditionalModelDirectories.Contains(parentDir, StringComparer.OrdinalIgnoreCase))
                    {
                        settings.AdditionalModelDirectories.Add(parentDir);
                        _settingsService.Save();
                    }
                }
            }

            RefreshLocalModels();
            await LoadModel(path);
        }
    }

    /// <summary>Add an extra directory to scan for models.</summary>
    private void AddModelDirectory()
    {
        string? dir = FolderPicker.ShowDialog(_loc.T("modelsm.pickFolderTitle"),
            _settingsService.Settings.ModelDirectory);

        if (!string.IsNullOrEmpty(dir))
        {
            var settings = _settingsService.Settings;
            settings.AdditionalModelDirectories ??= new();

            if (!settings.AdditionalModelDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(dir, settings.ModelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                settings.AdditionalModelDirectories.Add(dir);
                _settingsService.Save();
                RefreshLocalModels();
                DownloadStatus = _loc.T("modelsm.folderAdded", dir);
            }
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log?.Warn("Models", $"Opening {url} failed", ex); }
    }

    private void OpenModelFolder()
    {
        try
        {
            string dir = _settingsService.Settings.ModelDirectory;
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { _log?.Warn("Models", "Opening the model folder failed", ex); }
    }

    // ─── Downloads ───

    /// <summary>
    /// Downloads the quant the card shows (the advisor's pick unless the user chose another) and, for a model that can
    /// see images with this backend, its vision projector next to it ("&lt;stem&gt;.mmproj-F16.gguf").
    /// </summary>
    private Task DownloadRecommended(RecommendedModel? model)
    {
        if (model == null) return Task.CompletedTask;
        string file = model.SelectedPlan?.FileName ?? model.FileName;
        string label = model.SelectedPlan != null ? $"{model.DisplayName} {model.SelectedPlan.Name}" : model.DisplayName;
        string target = Path.Combine(_settingsService.Settings.ModelDirectory, Path.GetFileName(file));
        var jobs = new List<DownloadJob> { new(model.RepoId, file, target, label, IsProjector: false) };
        if (model is { CanSeeImages: true, Projector: { } pj })
            jobs.Add(new(model.RepoId, pj.FileName, ProjectorTarget(target, pj), label, IsProjector: true));
        return RunDownloadsAsync(jobs, model);
    }

    /// <summary>"Add vision": only the projector, next to the model file that is already there.</summary>
    private Task DownloadProjector(RecommendedModel? model)
    {
        if (model is not { CanSeeImages: true, Projector: { } pj } || string.IsNullOrEmpty(model.InstalledPath)) return Task.CompletedTask;
        var job = new DownloadJob(model.RepoId, pj.FileName, ProjectorTarget(model.InstalledPath, pj), model.DisplayName, IsProjector: true);
        return RunDownloadsAsync(new List<DownloadJob> { job }, model);
    }

    private static string ProjectorTarget(string modelPath, QuantOption projector) =>
        Path.Combine(Path.GetDirectoryName(modelPath) ?? "", VisionProjector.LocalFileName(Path.GetFileName(modelPath), projector.FileName));

    private Task DownloadSearchResult(ModelInfo? model) =>
        model == null || string.IsNullOrEmpty(model.RepoId)
            ? Task.CompletedTask
            : RunDownloadsAsync(new List<DownloadJob>
              {
                  new(model.RepoId, model.FileName, Path.Combine(_settingsService.Settings.ModelDirectory, Path.GetFileName(model.FileName)),
                      Path.GetFileName(model.FileName), IsProjector: false),
              }, null);

    private sealed record DownloadJob(string RepoId, string RepoFileName, string TargetPath, string Label, bool IsProjector);

    /// <summary>
    /// One download job (the model, then its projector). Files already on disk are skipped. A failed projector never
    /// undoes the model: the model stays, the status says vision is missing, and "Add vision" offers it again.
    /// </summary>
    private async Task RunDownloadsAsync(List<DownloadJob> jobs, RecommendedModel? card)
    {
        if (IsDownloading) return;
        var todo = jobs.Where(j => !File.Exists(j.TargetPath)).ToList();
        if (todo.Count == 0) { DownloadStatus = _loc.T("modelsm.alreadyDownloaded", jobs[0].Label); return; }

        IsDownloading = true;
        DownloadProgress = 0;
        _downloadingModel = card;
        if (card != null) card.IsDownloadingThis = true;
        _downloadCts = new CancellationTokenSource();
        bool modelDone = false;
        DownloadJob current = todo[0];
        try
        {
            foreach (var job in todo)
            {
                current = job;
                string shown = job.IsProjector ? _loc.T("modelsm.visionPartOf", job.Label) : job.Label;
                DownloadProgress = 0;
                DownloadStatus = _loc.T("modelsm.downloading", shown);
                // Created on the UI thread → callbacks are posted back to it.
                var progress = new Progress<HuggingFaceService.DownloadProgressInfo>(p =>
                {
                    DownloadProgress = p.Fraction * 100;
                    DownloadStatus = FormatProgress(shown, p);
                });
                try
                {
                    await _huggingFaceService.DownloadModelAsync(job.RepoId, job.RepoFileName, job.TargetPath, progress, _downloadCts.Token);
                }
                catch (Exception ex) when (job.IsProjector && modelDone && ex is not OperationCanceledException)
                {
                    _log?.Error("Models", $"Vision projector {job.RepoId}/{job.RepoFileName} failed", ex);
                    DownloadStatus = _loc.T("modelsm.dlVisionFailed", job.Label, DownloadFailureMessage(ex));
                    RefreshLocalModels();
                    return;
                }
                if (!job.IsProjector) modelDone = true;
            }
            DownloadStatus = todo.Any(j => j.IsProjector) && todo.Any(j => !j.IsProjector)
                ? _loc.T("modelsm.downloadDoneVision", jobs[0].Label)
                : todo.All(j => j.IsProjector) ? _loc.T("modelsm.visionAdded", jobs[0].Label)
                : _loc.T("modelsm.downloadDone", jobs[0].Label);
            RefreshLocalModels();
        }
        catch (OperationCanceledException)
        {
            // The partial file is invisible in "My Models" (it is not a .gguf yet) — so say what is left behind
            // and let the user choose instead of silently keeping gigabytes.
            string shown = current.IsProjector ? _loc.T("modelsm.visionPartOf", current.Label) : current.Label;
            var answer = MessageBox.Show(_loc.T("modelsm.cancelKeepConfirm", shown), "CluadeX",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.No)
            {
                try { HuggingFaceService.DeletePartialDownload(current.TargetPath); DownloadStatus = _loc.T("modelsm.cancelDeleted"); }
                catch (Exception ex) { _log?.Warn("Models", "Deleting a partial download failed", ex); DownloadStatus = _loc.T("modelsm.cancelDeleteFailed"); }
            }
            else DownloadStatus = _loc.T("modelsm.cancelKept");
            if (modelDone) RefreshLocalModels();
        }
        catch (Exception ex)
        {
            _log?.Error("Models", $"Download of {current.RepoId}/{current.RepoFileName} failed", ex);
            DownloadStatus = DownloadFailureMessage(ex);
        }
        finally
        {
            IsDownloading = false;
            if (_downloadingModel != null) _downloadingModel.IsDownloadingThis = false;
            _downloadingModel = null;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private string FormatProgress(string name, HuggingFaceService.DownloadProgressInfo p)
    {
        string part = p.ShardCount > 1 ? _loc.T("modelsm.shardPart", p.Shard, p.ShardCount) : "";
        if (p.Verifying)
            return _loc.T("modelsm.verifying", name, part, p.Fraction.ToString("P0"));
        return p.Total > 0
            ? _loc.T("modelsm.progress", name, part, Mb(p.Bytes), Mb(p.Total), p.Fraction.ToString("P1"))
            : _loc.T("modelsm.progressNoTotal", name, part, Mb(p.Bytes));
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("N1");
    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("N1");

    private string DownloadFailureMessage(Exception ex) => ex switch
    {
        HfAccessDeniedException d when d.IsGated => _loc.T("modelsm.dlGated"),
        HfAccessDeniedException => _loc.T("modelsm.dlDenied"),
        HfNotFoundException => _loc.T("modelsm.dlNotFound"),
        InsufficientDiskSpaceException s => _loc.T("modelsm.dlNoSpace", Gb(s.BytesNeeded), Gb(s.BytesAvailable), s.Drive),
        DownloadVerificationException { Kind: "incomplete" } => _loc.T("modelsm.dlIncomplete"),
        DownloadVerificationException => _loc.T("modelsm.dlCorrupt"),
        HttpRequestException or TimeoutException => _loc.T("modelsm.dlNetwork"),
        UnauthorizedAccessException => _loc.T("modelsm.dlNoPermission"),
        IOException => _loc.T("modelsm.dlDiskError"),
        _ => _loc.T("modelsm.dlFailed"),
    };

    // ─── Search ───

    private async Task SearchModels()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        IsSearching = true;
        SearchResults.Clear();
        _allSearchResults.Clear();
        SearchResultInfo = "";
        _searchPage = 1;
        OnPropertyChanged(nameof(SearchPage));
        try
        {
            var results = await _huggingFaceService.SearchModelsAsync(SearchQuery, ct);

            // File lists for up to 20 repos: fetched 4 at a time instead of one after another.
            using var gate = new SemaphoreSlim(4);
            var perRepo = await Task.WhenAll(results.Select(async result =>
            {
                await gate.WaitAsync(ct);
                try { return (result, files: await _huggingFaceService.GetModelFilesAsync(result.ModelId, ct)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log?.Debug("Models", $"Listing files of {result.ModelId} failed: {ex.Message}");
                    return (result, files: new List<HuggingFaceSibling>());
                }
                finally { gate.Release(); }
            }));
            ct.ThrowIfCancellationRequested();

            int fileCount = 0;
            foreach (var (result, files) in perRepo)
            {
                // Files come back best quant first (Q4_K_M, Q5_K_M, …) with split models grouped — the top five
                // are the ones worth offering.
                foreach (var file in files.Take(5))
                {
                    // Extract author from modelId if API didn't return it
                    string author = !string.IsNullOrEmpty(result.Author) ? result.Author
                        : result.ModelId.Contains('/') ? result.ModelId.Split('/')[0] : "";
                    string shortName = result.ModelId.Contains('/')
                        ? result.ModelId.Split('/')[1] : result.ModelId;
                    string leaf = Path.GetFileName(file.RFilename);

                    // Check if already downloaded locally
                    var local = LocalModels.FirstOrDefault(lm =>
                        string.Equals(lm.FileName, leaf, StringComparison.OrdinalIgnoreCase));

                    var info = new ModelInfo
                    {
                        Id = $"{result.ModelId}/{file.RFilename}",
                        Name = shortName,
                        FileName = file.RFilename,
                        RepoId = result.ModelId,
                        FileSize = file.Size ?? 0,
                        Author = author,
                        Downloads = result.Downloads,
                        Likes = result.Likes,
                        Tags = result.Tags,
                        LastModified = result.LastModified,
                        QuantizationType = ExtractQuantFromFile(file.RFilename),
                        IsDownloaded = local != null,
                        LocalPath = local?.LocalPath,
                    };
                    DescribeFile(info);
                    _allSearchResults.Add(info);
                    fileCount++;
                }
            }

            // Apply pagination
            SearchTotalPages = Math.Max(1, (int)Math.Ceiling(_allSearchResults.Count / (double)_searchItemsPerPage));
            ApplySearchPagination();

            SearchResultInfo = fileCount > 0
                ? _loc.T("modelsm.searchFound", fileCount, results.Count)
                : _loc.T("modelsm.searchNone");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_searchCts, cts)) SearchResultInfo = _loc.T("modelsm.searchCancelled");
        }
        catch (Exception ex)
        {
            _log?.Error("Models", $"Hugging Face search for '{SearchQuery}' failed", ex);
            SearchResultInfo = ex is HttpRequestException or TimeoutException
                ? _loc.T("modelsm.searchNetwork")
                : _loc.T("modelsm.searchFailed");
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                _searchCts = null;
            }
            cts.Dispose();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplySearchPagination()
    {
        SearchResults.Clear();
        var paged = _allSearchResults
            .Skip((_searchPage - 1) * _searchItemsPerPage)
            .Take(_searchItemsPerPage);
        foreach (var item in paged)
            SearchResults.Add(item);

        SearchTotalPages = Math.Max(1, (int)Math.Ceiling(_allSearchResults.Count / (double)_searchItemsPerPage));
        OnPropertyChanged(nameof(SearchPageInfo));
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private static string ExtractQuantFromFile(string filename)
    {
        string upper = filename.ToUpperInvariant();
        string[] quantTypes = ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M", "Q3_K_S", "Q2_K", "IQ4_XS", "IQ3_XXS", "MXFP4", "BF16", "F16", "F32"];
        foreach (string q in quantTypes)
        {
            if (upper.Contains(q)) return q;
        }
        return "";
    }

    private void CancelDownload() => _downloadCts?.Cancel();

    // ─── Delete ───

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Delete a model file — any .gguf the page lists, including ones in the user's own folders, so it asks
    /// first. A model that is loaded (by this page or by the startup auto-load, which uses the saved
    /// SelectedModelPath) is unloaded first: llama-server holds the file open, and File.Delete used to throw
    /// straight into an English crash dialog.
    /// </summary>
    private async Task DeleteModel(ModelInfo? model)
    {
        if (model?.LocalPath == null) return;
        string path = model.LocalPath;
        // Its vision projector goes too — unless another model file in the folder pairs with the same one.
        string? projector = VisionProjector.FindFor(path);
        if (projector != null && LocalModels.Any(m => m != model && !SamePath(m.LocalPath, path)
                                                      && SamePath(VisionProjector.FindFor(m.LocalPath), projector)))
            projector = null;
        string question = projector == null
            ? _loc.T("modelsm.deleteConfirm", model.FileName, model.FileSizeDisplay, path)
            : _loc.T("modelsm.deleteConfirmVision", model.FileName, model.FileSizeDisplay, path, Path.GetFileName(projector));
        var answer = MessageBox.Show(question, "CluadeX", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (SamePath(path, SelectedModelPath) || SamePath(path, _settingsService.Settings.SelectedModelPath))
                await UnloadModel();

            _huggingFaceService.DeleteModel(path);
            if (projector != null)
                try { File.Delete(projector); }
                catch (Exception ex) { _log?.Warn("Models", $"Deleting the projector {projector} failed", ex); }
            RefreshLocalModels();
            DownloadStatus = _loc.T("modelsm.deleted", model.FileName);
        }
        catch (Exception ex)
        {
            _log?.Error("Models", $"Deleting {path} failed", ex);
            DownloadStatus = ex switch
            {
                UnauthorizedAccessException => _loc.T("modelsm.deleteNoPermission", model.FileName),
                IOException => _loc.T("modelsm.deleteInUse", model.FileName),
                _ => _loc.T("modelsm.deleteFailed", model.FileName),
            };
            RefreshLocalModels();
        }
    }
}
