using System.Collections.Concurrent;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Gpu;

namespace CluadeX.ViewModels;

/// <summary>
/// The vertical GPU share slider that opens from the status-bar VRAM graph, and the hint that floats up from that graph
/// until the owner has used it (or the pet says it, when it is out and talks). The numbers come from
/// <see cref="GpuShare"/>, the stored state from <see cref="GpuShareSettings"/>.
/// </summary>
public sealed class GpuShareViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly AiProviderManager _providers;
    private readonly Services.Pet.PetService? _pet;
    private readonly Func<GpuLiveStats?> _stats;
    private readonly Func<Task<bool>>? _reload;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hintTimer;
    private readonly ConcurrentDictionary<string, (DateTime Stamp, int Blocks, long Bytes)> _modelFacts = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _graphVisibleFor;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private int _loadedAtPercent = -1;

    public GpuShareSettings Store { get; }

    public GpuShareViewModel(SettingsService settings, LocalizationService loc, AiProviderManager providers,
        Services.Pet.PetService? pet, Func<GpuLiveStats?> stats, Func<Task<bool>>? reload)
    {
        _settings = settings;
        _loc = loc;
        _providers = providers;
        _pet = pet;
        _stats = stats;
        _reload = reload;
        Store = new GpuShareSettings(settings);
        _percent = Store.Percent;
        // Dragging fires a value per tick; the file is written once the thumb rests.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Store.SetPercent(_percent); RaiseTexts(); };
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); HintOpen = false; };

        // The click on the graph that closes the (StaysOpen=False) popup also reaches the graph: don't reopen at once.
        ToggleCommand = new RelayCommand(() =>
        {
            if (!IsOpen && DateTime.UtcNow - _closedAtUtc < TimeSpan.FromMilliseconds(300)) return;
            IsOpen = !IsOpen;
        });
        CloseCommand = new RelayCommand(() => IsOpen = false);
        PresetCommand = new RelayCommand<object>(p => { if (int.TryParse(p?.ToString(), out int v)) Percent = v; });
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => ShowReload);
        DismissHintCommand = new RelayCommand(() => { HintOpen = false; Store.MarkUsed(); });
        _loc.LanguageChanged += RaiseTexts;
    }

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand DismissHintCommand { get; }

    // ─── popup ───
    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!SetProperty(ref _isOpen, value)) return;
            if (value)
            {
                HintOpen = false;
                Store.MarkUsed();
                _percent = Store.Percent;
                OnPropertyChanged(nameof(Percent));
                RaiseTexts();
                _ = LookModelAsync();
            }
            else { _closedAtUtc = DateTime.UtcNow; FlushSave(); }
        }
    }
    private DateTime _closedAtUtc = DateTime.MinValue;

    private int _percent;
    /// <summary>The slider, 0–100 in steps of 5 (the slider snaps), saved once the thumb rests.</summary>
    public int Percent
    {
        get => _percent;
        set
        {
            int v = GpuShare.Clamp(value);
            if (!SetProperty(ref _percent, v)) return;
            RaiseTexts();
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    /// <summary>Write a pending slider value now (popup closed, app closing).</summary>
    public void FlushSave()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        Store.SetPercent(_percent);
        RaiseTexts();
    }

    public string PercentText => $"{_percent}";
    public string ModeText => _percent >= 100 ? _loc.T("gpushare.full") : _percent == 0 ? _loc.T("gpushare.cpu") : _loc.T("gpushare.partial");
    public bool IsCapped => _percent < 100;

    public string BudgetText
    {
        get
        {
            var st = _stats();
            if (st is not { VramTotalMB: > 0 }) return _loc.T("gpushare.budgetNoGpu");
            var plan = GpuShare.Describe(_percent, -1, null, 0, st.VramTotalMB, 0);
            return _loc.T("gpushare.budget", Mb(plan.BudgetMb), Mb(st.VramTotalMB));
        }
    }

    private AiProviderType ActiveType => _settings.Settings.ActiveProvider;
    private bool IsGgufActive => ActiveType is AiProviderType.Local or AiProviderType.LlamaServer;

    public string LayersText
    {
        get
        {
            if (ActiveType == AiProviderType.Ollama)
                return _percent >= 100 ? _loc.T("gpushare.ollamaFull") : _loc.T("gpushare.ollama", _percent);
            string? path = _settings.Settings.SelectedModelPath;
            if (string.IsNullOrEmpty(path) || !_modelFacts.TryGetValue(path, out var f)) return _loc.T("gpushare.noModel");
            var s = _settings.Settings;
            var plan = GpuShare.Describe(_percent, s.GpuLayerCount, s.GpuBackend, f.Blocks, 0, f.Bytes);
            string name = Path.GetFileNameWithoutExtension(path);
            if (plan.Layers == 0) return _loc.T("gpushare.layersCpu", name);
            if (plan.CapUnknown || plan.TotalLayers <= 0) return _loc.T("gpushare.layersUnknown", name);
            if (plan.Layers < 0 || plan.Layers >= plan.TotalLayers) return _loc.T("gpushare.layersAll", name, plan.TotalLayers, Mb(plan.WeightsOnGpuMb));
            return _loc.T("gpushare.layers", name, plan.Layers, plan.TotalLayers, Mb(plan.WeightsOnGpuMb));
        }
    }

    public string ApplyNote => ActiveType == AiProviderType.Ollama ? _loc.T("gpushare.nextOllama") : _loc.T("gpushare.nextLoad");

    /// <summary>A GGUF is loaded with a different share than the slider now says: offer the reload (never automatic).</summary>
    public bool ShowReload => _reload != null && IsGgufActive && _providers.ActiveProvider.IsReady
                              && _loadedAtPercent >= 0 && _loadedAtPercent != _percent;
    public string LoadedAtText => _loadedAtPercent < 0 ? "" : _loc.T("gpushare.loadedAt", _loadedAtPercent);

    /// <summary>A local model finished loading: remember the share it was loaded with.</summary>
    public void NoteModelLoaded()
    {
        _loadedAtPercent = IsGgufActive ? Store.Percent : -1;
        RaiseTexts();
    }

    private async Task ReloadAsync()
    {
        FlushSave();
        if (_reload == null) return;
        IsOpen = false;
        if (await _reload()) NoteModelLoaded();
    }

    /// <summary>Block count and size of the picked GGUF, read off the UI thread, once per file version.</summary>
    private async Task LookModelAsync()
    {
        string? path = _settings.Settings.SelectedModelPath;
        if (string.IsNullOrEmpty(path) || !IsGgufActive) return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            if (_modelFacts.TryGetValue(path, out var known) && known.Stamp == fi.LastWriteTimeUtc) return;
            int blocks = await Task.Run(() => GgufMetadataReader.TryReadBlockCount(path));
            _modelFacts[path] = (fi.LastWriteTimeUtc, blocks, fi.Length);
            OnPropertyChanged(nameof(LayersText));
        }
        catch { /* the popup shows "no model" */ }
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsCapped));
        OnPropertyChanged(nameof(BudgetText));
        OnPropertyChanged(nameof(LayersText));
        OnPropertyChanged(nameof(ApplyNote));
        OnPropertyChanged(nameof(ShowReload));
        OnPropertyChanged(nameof(LoadedAtText));
        CommandManager.InvalidateRequerySuggested();
    }

    // ─── first-use hint ───
    private bool _hintOpen;
    public bool HintOpen
    {
        get => _hintOpen;
        set
        {
            if (!SetProperty(ref _hintOpen, value)) return;
            if (value) { _hintTimer.Stop(); _hintTimer.Start(); }
        }
    }

    /// <summary>Every GPU poll (~2 s): count how long the graph has been on screen and, when due, float the hint up
    /// (or let the pet say it).</summary>
    public void Tick(bool graphVisible, DateTime nowUtc)
    {
        var step = _lastTickUtc == DateTime.MinValue ? TimeSpan.Zero : nowUtc - _lastTickUtc;
        _lastTickUtc = nowUtc;
        if (!graphVisible || IsOpen || HintOpen) return;
        if (step > TimeSpan.Zero && step < TimeSpan.FromSeconds(30)) _graphVisibleFor += step;
        if (!Store.HintDue(_graphVisibleFor, nowUtc)) return;
        Store.NoteHintShown(nowUtc);
        var channel = GpuShareSettings.ChooseChannel(_pet?.Enabled ?? false, _pet?.IsOut ?? false, _pet?.Talks ?? false);
        if (channel == GpuShareHintChannel.Pet) _pet!.Raise(Services.Pet.PetMoment.GpuShareHint);
        else HintOpen = true;
    }

    private static string Mb(int mb) => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb} MB";
}
