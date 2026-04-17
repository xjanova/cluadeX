using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Helpers;
using CluadeX.Services.Providers;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

public class ModelManagerViewModel : ViewModelBase
{
    private readonly HuggingFaceService _huggingFaceService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly LlamaInferenceService _inferenceService;
    private readonly LocalGgufProvider _localProvider;
    private readonly SettingsService _settingsService;
    private readonly AiProviderManager _providerManager;
    private CancellationTokenSource? _downloadCts;

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
    public ObservableCollection<string> Categories { get; } = new() { "All", "Coding", "General", "Reasoning" };

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
    public ICommand NextSearchPageCommand => new RelayCommand(() => { if (_searchPage < SearchTotalPages) SearchPage++; }, () => _searchPage < SearchTotalPages);
    public ICommand PrevSearchPageCommand => new RelayCommand(() => { if (_searchPage > 1) SearchPage--; }, () => _searchPage > 1);

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
                FilterRecommendedModels();
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
    public ICommand DownloadSearchResultCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand DeleteModelCommand { get; }
    public ICommand BrowseAndLoadModelCommand { get; }
    public ICommand AddModelDirectoryCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand OpenModelFolderCommand { get; }

    private List<RecommendedModel> _allRecommended = new();

    public ModelManagerViewModel(
        HuggingFaceService huggingFaceService,
        GpuDetectionService gpuDetectionService,
        LlamaInferenceService inferenceService,
        LocalGgufProvider localProvider,
        SettingsService settingsService,
        AiProviderManager providerManager)
    {
        _huggingFaceService = huggingFaceService;
        _gpuDetectionService = gpuDetectionService;
        _inferenceService = inferenceService;
        _localProvider = localProvider;
        _settingsService = settingsService;
        _providerManager = providerManager;
        _isGridView = settingsService.Settings.ModelCatalogGridView;

        RefreshLocalModelsCommand = new RelayCommand(RefreshLocalModels);
        LoadModelCommand = new AsyncRelayCommand<string>(LoadModel);
        UnloadModelCommand = new RelayCommand(UnloadModel);
        DownloadRecommendedCommand = new AsyncRelayCommand<RecommendedModel>(DownloadRecommended);
        SearchModelsCommand = new AsyncRelayCommand(SearchModels);
        DownloadSearchResultCommand = new AsyncRelayCommand<ModelInfo>(DownloadSearchResult);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        DeleteModelCommand = new RelayCommand<ModelInfo>(DeleteModel);
        BrowseAndLoadModelCommand = new AsyncRelayCommand(BrowseAndLoadModel);
        AddModelDirectoryCommand = new RelayCommand(AddModelDirectory);
        OpenUrlCommand = new RelayCommand<string>(OpenUrl);
        OpenModelFolderCommand = new RelayCommand(OpenModelFolder);

        // Populate model sources
        foreach (var source in HuggingFaceService.PopularSources)
            ModelSources.Add(source);

        Initialize();
    }

    private void Initialize()
    {
        Task.Run(() =>
        {
            var gpu = _gpuDetectionService.DetectGpu();
            var rec = gpu.GetRecommendation();
            App.Current?.Dispatcher.Invoke(() =>
            {
                AvailableVramMB = gpu.VramTotalMB;
                GpuRecommendation = $"{gpu.Name} - {gpu.VramTotalGB:F1} GB VRAM\n{rec.Description}";
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
    private static void ComputeFit(RecommendedModel model, int availableVramMB)
    {
        if (availableVramMB <= 0)
        {
            // No GPU detected → everything is CPU-only. Small models are still usable.
            model.FitTier = model.ParameterBillions <= 3 ? ModelFitTier.Partial : ModelFitTier.Poor;
            model.FitLabel = "CPU only";
            model.FitHint = "No dedicated GPU detected — runs on CPU only (slow).";
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
            model.FitLabel = "Fast";
            model.FitHint = $"Runs entirely on GPU with headroom ({usable} MB usable vs {needed} MB needed).";
            model.RecommendedGpuLayers = -1;
        }
        // Good: fits but tight
        else if (usable >= needed)
        {
            model.FitTier = ModelFitTier.Good;
            model.FitLabel = "Good";
            model.FitHint = $"Fits on GPU but tight — you may need to lower context size. ({usable} MB usable vs {needed} MB needed)";
            model.RecommendedGpuLayers = -1;
        }
        // Partial: model bigger than VRAM but within ~1.5× — partial offload works OK
        else if (needed <= availableVramMB * 1.5)
        {
            double fraction = (double)usable / needed;
            int suggestedLayers = Math.Clamp((int)Math.Round(fraction * 40), 10, 40);
            model.FitTier = ModelFitTier.Partial;
            model.FitLabel = "Partial";
            model.FitHint = $"Needs CPU offload — suggest -ngl {suggestedLayers} (about {(int)(fraction*100)}% on GPU). Slower but runs.";
            model.RecommendedGpuLayers = suggestedLayers;
        }
        // Poor: heavy CPU offload
        else if (needed <= availableVramMB * 2.5)
        {
            double fraction = (double)usable / needed;
            int suggestedLayers = Math.Clamp((int)Math.Round(fraction * 40), 5, 20);
            model.FitTier = ModelFitTier.Poor;
            model.FitLabel = "Slow";
            model.FitHint = $"Much larger than your VRAM — will be significantly slower. Try a smaller quant or a different model.";
            model.RecommendedGpuLayers = suggestedLayers;
        }
        // Too large — not recommended
        else
        {
            model.FitTier = ModelFitTier.TooLarge;
            model.FitLabel = "Too large";
            model.FitHint = $"Model needs ~{needed} MB VRAM but you only have {availableVramMB} MB. Not practical — pick a smaller quant/size.";
            model.RecommendedGpuLayers = 5;
        }
    }

    private void RefreshLocalModels()
    {
        var models = _huggingFaceService.GetLocalModels();
        LocalModels.Clear();
        foreach (var model in models) LocalModels.Add(model);

        // Re-filter recommended models to update IsInstalled status
        if (_allRecommended.Count > 0)
            FilterRecommendedModels();
    }

    private async Task LoadModel(string? path)
    {
        if (string.IsNullOrEmpty(path) || _localProvider.IsLoading) return;
        IsLoadingModel = true;
        ModelLoadStatus = "Loading model...";
        try
        {
            // Ensure active provider is Local so events flow to MainViewModel (overlay)
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);

            var progress = new Progress<string>(s =>
                App.Current?.Dispatcher.Invoke(() => ModelLoadStatus = s));

            // Route through LocalGgufProvider — it inspects the GGUF arch and
            // transparently picks LLamaSharp or llama-server.exe (Gemma 4 etc.)
            await _localProvider.LoadModelAsync(path, progress);

            SelectedModelPath = path;
            ModelLoadStatus = $"Loaded: {System.IO.Path.GetFileNameWithoutExtension(path)}";
            _settingsService.UpdateSettings(s =>
            {
                s.SelectedModelPath = path;
                s.SelectedModelName = System.IO.Path.GetFileNameWithoutExtension(path);
            });
        }
        catch (Exception ex) { ModelLoadStatus = $"Failed: {ex.Message}"; }
        finally { IsLoadingModel = false; }
    }

    private void UnloadModel()
    {
        // LocalGgufProvider doesn't expose an explicit unload — but unloading both
        // backends is safe (each is a no-op if it wasn't loaded).
        _inferenceService.UnloadModel();
        SelectedModelPath = null;
        ModelLoadStatus = "Model unloaded.";

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
            Title = "Select a GGUF Model File",
            Filter = "GGUF Models (*.gguf)|*.gguf|All Files (*.*)|*.*",
            InitialDirectory = _settingsService.Settings.ModelDirectory,
        };

        if (dialog.ShowDialog() == true)
        {
            string path = dialog.FileName;

            // If file is not in known directories, offer to add the parent directory
            string? parentDir = System.IO.Path.GetDirectoryName(path);
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
        string? dir = FolderPicker.ShowDialog("Select a folder containing GGUF models",
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
                DownloadStatus = $"Added model folder: {dir}";
            }
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void OpenModelFolder()
    {
        try
        {
            string dir = _settingsService.Settings.ModelDirectory;
            if (System.IO.Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private async Task DownloadRecommended(RecommendedModel? model)
    {
        if (model == null || IsDownloading) return;
        string targetPath = System.IO.Path.Combine(_settingsService.Settings.ModelDirectory, model.FileName);
        if (System.IO.File.Exists(targetPath)) { DownloadStatus = "Model already downloaded!"; return; }

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = $"Downloading {model.DisplayName}...";
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(double progress, string status)>(p =>
                App.Current?.Dispatcher.Invoke(() => { DownloadProgress = p.progress * 100; DownloadStatus = p.status; }));
            await _huggingFaceService.DownloadModelAsync(model.RepoId, model.FileName, targetPath, progress, _downloadCts.Token);
            DownloadStatus = "Download complete!";
            RefreshLocalModels();
        }
        catch (OperationCanceledException) { DownloadStatus = "Download cancelled."; }
        catch (Exception ex) { DownloadStatus = $"Download failed: {ex.Message}"; }
        finally { IsDownloading = false; _downloadCts?.Dispose(); _downloadCts = null; }
    }

    private async Task SearchModels()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        IsSearching = true;
        SearchResults.Clear();
        _allSearchResults.Clear();
        SearchResultInfo = "";
        _searchPage = 1;
        try
        {
            var results = await _huggingFaceService.SearchModelsAsync(SearchQuery);
            int fileCount = 0;
            foreach (var result in results)
            {
                try
                {
                    var files = await _huggingFaceService.GetModelFilesAsync(result.ModelId);
                    foreach (var file in files.Take(5))
                    {
                        // Extract author from modelId if API didn't return it
                        string author = !string.IsNullOrEmpty(result.Author) ? result.Author
                            : result.ModelId.Contains('/') ? result.ModelId.Split('/')[0] : "";
                        string shortName = result.ModelId.Contains('/')
                            ? result.ModelId.Split('/')[1] : result.ModelId;

                        // Check if already downloaded locally
                        bool isLocal = LocalModels.Any(lm =>
                            string.Equals(lm.FileName, file.RFilename, StringComparison.OrdinalIgnoreCase));
                        string? localPath = isLocal
                            ? LocalModels.First(lm => string.Equals(lm.FileName, file.RFilename, StringComparison.OrdinalIgnoreCase)).LocalPath
                            : null;

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
                            Description = $"Downloads: {result.Downloads:N0} | Likes: {result.Likes}",
                            IsDownloaded = isLocal,
                            LocalPath = localPath,
                        });
                        fileCount++;
                    }
                }
                catch { }
            }

            // Apply pagination
            SearchTotalPages = Math.Max(1, (int)Math.Ceiling(_allSearchResults.Count / (double)_searchItemsPerPage));
            ApplySearchPagination();

            SearchResultInfo = fileCount > 0
                ? $"Found {fileCount} GGUF files from {results.Count} repositories"
                : "No GGUF files found. Try a different search term.";
        }
        catch (Exception ex) { SearchResultInfo = $"Search failed: {ex.Message}"; }
        finally { IsSearching = false; }
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

    private async Task DownloadSearchResult(ModelInfo? model)
    {
        if (model == null || IsDownloading || string.IsNullOrEmpty(model.RepoId)) return;
        string targetPath = System.IO.Path.Combine(_settingsService.Settings.ModelDirectory, model.FileName);

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = $"Downloading {model.FileName}...";
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(double progress, string status)>(p =>
                App.Current?.Dispatcher.Invoke(() => { DownloadProgress = p.progress * 100; DownloadStatus = p.status; }));
            await _huggingFaceService.DownloadModelAsync(model.RepoId, model.FileName, targetPath, progress, _downloadCts.Token);
            DownloadStatus = "Download complete!";
            RefreshLocalModels();
        }
        catch (OperationCanceledException) { DownloadStatus = "Download cancelled."; }
        catch (Exception ex) { DownloadStatus = $"Download failed: {ex.Message}"; }
        finally { IsDownloading = false; _downloadCts?.Dispose(); _downloadCts = null; }
    }

    private void CancelDownload() => _downloadCts?.Cancel();

    private void DeleteModel(ModelInfo? model)
    {
        if (model?.LocalPath == null) return;
        if (model.LocalPath == SelectedModelPath) UnloadModel();
        _huggingFaceService.DeleteModel(model.LocalPath);
        RefreshLocalModels();
        DownloadStatus = $"Deleted: {model.FileName}";
    }
}
