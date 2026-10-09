using System.Collections.ObjectModel;
using System.Diagnostics;
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

/// <summary>A category filter choice: the key the catalog uses + its localized label.</summary>
public sealed record ModelCategoryOption(string Key, string Label);

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
    private GpuInfo? _gpu;

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
    private string _selectedCategory = "All";
    private string _searchResultInfo = string.Empty;

    public ObservableCollection<ModelInfo> LocalModels { get; } = new();
    public ObservableCollection<RecommendedModel> RecommendedModels { get; } = new();
    public ObservableCollection<ModelInfo> SearchResults { get; } = new();
    public ObservableCollection<ModelSource> ModelSources { get; } = new();

    private static readonly string[] CategoryKeys = { "All", "Coding", "General", "Reasoning" };
    private List<ModelCategoryOption> _categories = new();
    /// <summary>Category filter choices (Key is what the catalog uses; Label is shown).</summary>
    public List<ModelCategoryOption> Categories { get => _categories; private set => SetProperty(ref _categories, value); }

    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value); }
    public bool IsSearching { get => _isSearching; set => SetProperty(ref _isSearching, value); }
    public bool IsDownloading { get => _isDownloading; set => SetProperty(ref _isDownloading, value); }
    public double DownloadProgress { get => _downloadProgress; set => SetProperty(ref _downloadProgress, value); }
    public string DownloadStatus { get => _downloadStatus; set => SetProperty(ref _downloadStatus, value); }
    public string ModelLoadStatus { get => _modelLoadStatus; set => SetProperty(ref _modelLoadStatus, value); }
    public bool IsLoadingModel { get => _isLoadingModel; set => SetProperty(ref _isLoadingModel, value); }
    public string? SelectedModelPath { get => _selectedModelPath; set => SetProperty(ref _selectedModelPath, value); }
    public string GpuRecommendation { get => _gpuRecommendation; set => SetProperty(ref _gpuRecommendation, value); }
    public int AvailableVramMB { get => _availableVramMB; set => SetProperty(ref _availableVramMB, value); }
    public string SearchResultInfo { get => _searchResultInfo; set => SetProperty(ref _searchResultInfo, value); }

    // ─── Pagination for Recommended Models ───
    private int _currentPage = 1;
    private int _itemsPerPage = 12;
    public int CurrentPage { get => _currentPage; set { if (SetProperty(ref _currentPage, value)) FilterRecommendedModels(); } }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_filteredCount / (double)_itemsPerPage));
    private int _filteredCount;
    public string PageInfo => $"{_currentPage} / {TotalPages}";
    public ICommand NextPageCommand => new RelayCommand(() => { if (_currentPage < TotalPages) CurrentPage++; }, () => _currentPage < TotalPages);
    public ICommand PrevPageCommand => new RelayCommand(() => { if (_currentPage > 1) CurrentPage--; }, () => _currentPage > 1);

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

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                // Back to page 1: on page 3 a smaller category showed an empty list and "3 / 1".
                _currentPage = 1;
                OnPropertyChanged(nameof(CurrentPage));
                FilterRecommendedModels();
            }
        }
    }

    // Catalog layout: List (vertical cards) or Grid (2-column tiles).
    // Toggled from the page header; persisted via SettingsService so the user's choice survives restarts.
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
    public ICommand LoadModelCommand { get; }
    public ICommand UnloadModelCommand { get; }
    public ICommand DownloadRecommendedCommand { get; }
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

    /// <summary>One-click "Apply recommended settings": persist the card's suggested -ngl
    /// (GpuLayerCount) so the next model load uses it — closes the old do-it-manually gap.</summary>
    private void ApplyFit(RecommendedModel? model)
    {
        if (model == null) return;
        _settingsService.UpdateSettings(s => s.GpuLayerCount = model.RecommendedGpuLayers);
        ModelLoadStatus = model.RecommendedGpuLayers == -1
            ? _loc.T("modelsm.fitAppliedAll", model.DisplayName)
            : _loc.T("modelsm.fitAppliedLayers", model.RecommendedGpuLayers, model.DisplayName);
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

        RefreshLocalModelsCommand = new RelayCommand(RefreshLocalModels);
        LoadModelCommand = new AsyncRelayCommand<string>(LoadModel);
        UnloadModelCommand = new AsyncRelayCommand(UnloadModel);
        DownloadRecommendedCommand = new AsyncRelayCommand<RecommendedModel>(DownloadRecommended);
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

        BuildModelSources();
        BuildCategories();
        _loc.LanguageChanged += () =>
        {
            BuildModelSources();
            string keep = SelectedCategory;
            BuildCategories();
            _selectedCategory = keep;
            OnPropertyChanged(nameof(SelectedCategory));
            UpdateGpuRecommendation();
            FilterRecommendedModels();   // re-labels the fit badges
        };

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

    private void BuildCategories() =>
        Categories = CategoryKeys.Select(k => new ModelCategoryOption(k, _loc.T("modelsm.cat" + k))).ToList();

    private void Initialize()
    {
        Task.Run(() =>
        {
            GpuInfo gpu;
            try { gpu = _gpuDetectionService.DetectGpu(); }
            catch (Exception ex)
            {
                _log?.Warn("Models", "GPU detection failed", ex);
                gpu = new GpuInfo();
            }
            App.Current?.Dispatcher.Invoke(() =>
            {
                _gpu = gpu;
                AvailableVramMB = gpu.VramTotalMB;
                UpdateGpuRecommendation();
                // Show the full catalog and use FitTier badges to signal which models run
                // well on this GPU. Previously we silently filtered out anything larger than
                // the user's VRAM — that hid useful options (bigger models still run via
                // CPU offload) and made the catalog look incomplete.
                _allRecommended = _huggingFaceService.GetRecommendedModels(null);
                FilterRecommendedModels();
            });
        });
        RefreshLocalModels();
    }

    private void UpdateGpuRecommendation()
    {
        if (_gpu == null) return;
        var rec = _gpu.GetRecommendation();
        GpuRecommendation = _gpu.VramTotalMB <= 0
            ? _loc.T("modelsm.gpuNone", rec.RecommendedParameterBillions)
            : _loc.T("modelsm.gpuLine", _gpu.Name, _gpu.VramTotalGB.ToString("F1"), rec.RecommendedParameterBillions,
                rec.RecommendedQuantization, rec.MaxParameterBillions);
    }

    private void FilterRecommendedModels()
    {
        // Build a set of local filenames for quick lookup
        var localFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in LocalModels)
        {
            if (!string.IsNullOrEmpty(m.FileName))
            {
                localFiles.Add(m.FileName);
                localPaths[m.FileName] = m.LocalPath ?? "";
            }
        }

        var filtered = _selectedCategory == "All"
            ? _allRecommended
            : _allRecommended.Where(m => m.Category == _selectedCategory).ToList();

        _filteredCount = filtered.Count;
        if (_currentPage > TotalPages) { _currentPage = TotalPages; OnPropertyChanged(nameof(CurrentPage)); }

        // Apply pagination
        var paged = filtered
            .Skip((_currentPage - 1) * _itemsPerPage)
            .Take(_itemsPerPage)
            .ToList();

        RecommendedModels.Clear();
        foreach (var model in paged)
        {
            model.IsInstalled = localFiles.Contains(model.FileName);
            model.InstalledPath = model.IsInstalled && localPaths.TryGetValue(model.FileName, out var p) ? p : null;
            ComputeFit(model, AvailableVramMB);
            RecommendedModels.Add(model);
        }

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageInfo));
    }

    /// <summary>
    /// Decide how well a model fits the user's GPU, and suggest a reasonable <c>-ngl</c>
    /// (gpu_layer_count) value so the UI can show a green/yellow/red fit badge.
    ///
    /// Heuristic: reserve ~15% of VRAM for KV cache + context, then compare the model's
    /// quoted RequiredVramMB against what's left. If the model is bigger, we estimate how
    /// much CPU offload would be needed. A model whose size is more than ~2× the available
    /// VRAM runs so slowly via CPU offload that we label it TooLarge.
    /// </summary>
    private void ComputeFit(RecommendedModel model, int availableVramMB)
    {
        if (availableVramMB <= 0)
        {
            // No GPU detected → everything is CPU-only. Small models are still usable.
            model.FitTier = model.ParameterBillions <= 3 ? ModelFitTier.Partial : ModelFitTier.Poor;
            model.FitLabel = _loc.T("modelsm.fitCpuOnly");
            model.FitHint = _loc.T("modelsm.fitCpuOnlyHint");
            model.RecommendedGpuLayers = 0;
            return;
        }

        // Reserve ~15% of VRAM for KV-cache and activations. Cap the reserve at 1.5 GB so
        // huge cards don't over-reserve, and floor at 512 MB so tiny cards still have a buffer.
        int reserved = Math.Clamp(availableVramMB / 7, 512, 1536);
        int usable = Math.Max(availableVramMB - reserved, 512);
        int needed = model.RequiredVramMB;

        // Excellent: comfortable headroom (usable is 130%+ of needed)
        if (needed > 0 && usable >= needed * 1.3)
        {
            model.FitTier = ModelFitTier.Excellent;
            model.FitLabel = _loc.T("modelsm.fitFast");
            model.FitHint = _loc.T("modelsm.fitFastHint", usable, needed);
            model.RecommendedGpuLayers = -1;
        }
        // Good: fits but tight
        else if (usable >= needed)
        {
            model.FitTier = ModelFitTier.Good;
            model.FitLabel = _loc.T("modelsm.fitGood");
            model.FitHint = _loc.T("modelsm.fitGoodHint", usable, needed);
            model.RecommendedGpuLayers = -1;
        }
        // Partial: model bigger than VRAM but within ~1.5× — partial offload works OK
        else if (needed <= availableVramMB * 1.5)
        {
            double fraction = (double)usable / needed;
            int suggestedLayers = Math.Clamp((int)Math.Round(fraction * 40), 10, 40);
            model.FitTier = ModelFitTier.Partial;
            model.FitLabel = _loc.T("modelsm.fitPartial");
            model.FitHint = _loc.T("modelsm.fitPartialHint", suggestedLayers, (int)(fraction * 100));
            model.RecommendedGpuLayers = suggestedLayers;
        }
        // Poor: heavy CPU offload
        else if (needed <= availableVramMB * 2.5)
        {
            double fraction = (double)usable / needed;
            int suggestedLayers = Math.Clamp((int)Math.Round(fraction * 40), 5, 20);
            model.FitTier = ModelFitTier.Poor;
            model.FitLabel = _loc.T("modelsm.fitSlow");
            model.FitHint = _loc.T("modelsm.fitSlowHint");
            model.RecommendedGpuLayers = suggestedLayers;
        }
        // Too large — not recommended
        else
        {
            model.FitTier = ModelFitTier.TooLarge;
            model.FitLabel = _loc.T("modelsm.fitTooLarge");
            model.FitHint = _loc.T("modelsm.fitTooLargeHint", needed, availableVramMB);
            model.RecommendedGpuLayers = 5;
        }
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
        foreach (var model in models) LocalModels.Add(model);

        // Re-filter recommended models to update IsInstalled status
        if (_allRecommended.Count > 0)
            FilterRecommendedModels();
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
            ModelLoadStatus = _loc.T("modelsm.loaded", name);
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
        finally { IsLoadingModel = false; }
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

    private Task DownloadRecommended(RecommendedModel? model) =>
        model == null ? Task.CompletedTask : RunDownloadAsync(model.RepoId, model.FileName, model.DisplayName);

    private Task DownloadSearchResult(ModelInfo? model) =>
        model == null || string.IsNullOrEmpty(model.RepoId)
            ? Task.CompletedTask
            : RunDownloadAsync(model.RepoId, model.FileName, Path.GetFileName(model.FileName));

    private async Task RunDownloadAsync(string repoId, string repoFileName, string displayName)
    {
        if (IsDownloading) return;
        string targetPath = Path.Combine(_settingsService.Settings.ModelDirectory, Path.GetFileName(repoFileName));
        if (File.Exists(targetPath)) { DownloadStatus = _loc.T("modelsm.alreadyDownloaded", displayName); return; }

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = _loc.T("modelsm.downloading", displayName);
        _downloadCts = new CancellationTokenSource();
        try
        {
            // Created on the UI thread → callbacks are posted back to it.
            var progress = new Progress<HuggingFaceService.DownloadProgressInfo>(p =>
            {
                DownloadProgress = p.Fraction * 100;
                DownloadStatus = FormatProgress(displayName, p);
            });
            await _huggingFaceService.DownloadModelAsync(repoId, repoFileName, targetPath, progress, _downloadCts.Token);
            DownloadStatus = _loc.T("modelsm.downloadDone", displayName);
            RefreshLocalModels();
        }
        catch (OperationCanceledException)
        {
            // The partial file is invisible in "My Models" (it is not a .gguf yet) — so say what is left behind
            // and let the user choose instead of silently keeping gigabytes.
            var answer = MessageBox.Show(_loc.T("modelsm.cancelKeepConfirm", displayName), "CluadeX",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.No)
            {
                try { HuggingFaceService.DeletePartialDownload(targetPath); DownloadStatus = _loc.T("modelsm.cancelDeleted"); }
                catch (Exception ex) { _log?.Warn("Models", "Deleting a partial download failed", ex); DownloadStatus = _loc.T("modelsm.cancelDeleteFailed"); }
            }
            else DownloadStatus = _loc.T("modelsm.cancelKept");
        }
        catch (Exception ex)
        {
            _log?.Error("Models", $"Download of {repoId}/{repoFileName} failed", ex);
            DownloadStatus = DownloadFailureMessage(ex);
        }
        finally { IsDownloading = false; _downloadCts?.Dispose(); _downloadCts = null; }
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

                    _allSearchResults.Add(new ModelInfo
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
                    });
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
        string[] quantTypes = ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M", "Q3_K_S", "Q2_K", "IQ4_XS", "IQ3_XXS", "F16", "F32"];
        foreach (string q in quantTypes)
        {
            if (upper.Contains(q)) return q;
        }
        return "";
    }

    private void CancelDownload() => _downloadCts?.Cancel();

    // ─── Delete ───

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
        var answer = MessageBox.Show(_loc.T("modelsm.deleteConfirm", model.FileName, model.FileSizeDisplay, path), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (SamePath(path, SelectedModelPath) || SamePath(path, _settingsService.Settings.SelectedModelPath))
                await UnloadModel();

            _huggingFaceService.DeleteModel(path);
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
