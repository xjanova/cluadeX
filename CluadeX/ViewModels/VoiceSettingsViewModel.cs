using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice.Tts;

namespace CluadeX.ViewModels;

/// <summary>One engine in the picker.</summary>
// ToString = Label: the app's ComboBox template shows SelectionBoxItem as text and ignores DisplayMemberPath.
public sealed record VoiceEngineOption(string Key, string Label, string Note)
{
    public override string ToString() => Label;
}

/// <summary>One ElevenLabs model in the picker, with what it can do.</summary>
public sealed record VoiceModelOption(ElevenModel Model, string Label, string Badges)
{
    public override string ToString() => Label;
}

/// <summary>A simple choice (OpenAI / browser / Windows voice, an output format) — Value saved, Label shown.</summary>
public sealed record VoiceChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A girl's row in the per-girl voice table: her face, how she sounds, and her own voice on the current engine.</summary>
public sealed class GirlVoiceRow : ViewModelBase
{
    private readonly VoiceSettingsViewModel _owner;
    public string Girl { get; }
    public string Name { get; }
    public string Note { get; }
    public ImageBrush? Face { get; }
    public Brush Accent { get; }

    public GirlVoiceRow(VoiceSettingsViewModel owner, string girl)
    {
        _owner = owner;
        Girl = girl;
        var c = ChatCharacter.ForGirl(girl);
        Name = c.Name;
        Face = c.FaceBrush;
        Accent = c.AccentBrush;
        Note = VoicePersona.For(girl).ThaiNote;
    }

    /// <summary>Her own voice on the engine shown now (empty = the main voice).</summary>
    public string Voice
    {
        get => _owner.GirlVoice(Girl);
        set { _owner.SetGirlVoice(Girl, value ?? ""); OnPropertyChanged(); }
    }

    public void Refresh() => OnPropertyChanged(nameof(Voice));
}

/// <summary>
/// Settings › Voice (2026-11-09): the engine (free browser / free Windows / ElevenLabs / OpenAI with the shared key),
/// its key, model and voice, the fine settings the chosen model can take, voice cloning, a test, and each girl's own
/// voice. Every change is saved a moment after the last one.
/// </summary>
public sealed class VoiceSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly Func<string, string> _t;
    private readonly DispatcherTimer _saveTimer;
    private VoicePlayer? _player;
    private CancellationTokenSource? _testCts;
    private bool _loading;

    private VoiceSettings V => _settings.Settings.Voice ??= new VoiceSettings();

    /// <summary>How loud she reads, 0–100: AppSettings.BuddyVolume (not a voice-engine setting), saved as the slider
    /// moves and heard at once — MainWindow passes it to the players (Services.Voice.BuddyVolume). The same setting as
    /// her right-click menu and Settings › General › Appearance.</summary>
    public double Volume   // a double: the Slider's Value binds to it both ways
    {
        get => _settings.Settings.BuddyVolume;
        set
        {
            int v = double.IsNaN(value) ? Services.Voice.BuddyVolume.Default : (int)Math.Round(Math.Clamp(value, 0, 100));
            if (_settings.Settings.BuddyVolume == v) return;
            try { _settings.UpdateSettings(s => s.BuddyVolume = v); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Voice] volume: {ex.Message}"); }
            OnPropertyChanged();
        }
    }

    public VoiceSettingsViewModel(SettingsService settings, Func<string, string> t)
    {
        _settings = settings;
        _t = t;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };

        EngineOptions =
        [
            new(VoiceSettings.Edge, t("voice.engine.edge"), t("voice.engine.edgeNote")),
            new(VoiceSettings.Browser, t("voice.engine.browser"), t("voice.engine.browserNote")),
            new(VoiceSettings.Windows, t("voice.engine.windows"), t("voice.engine.windowsNote")),
            new(VoiceSettings.ElevenLabs, "ElevenLabs", t("voice.engine.elevenNote")),
            new(VoiceSettings.OpenAI, "OpenAI", t("voice.engine.openaiNote")),
        ];
        OutputFormats =
        [
            new("mp3_44100_128", "MP3 44.1 kHz 128 kbps"), new("mp3_44100_192", "MP3 44.1 kHz 192 kbps (Creator+)"),
            new("mp3_44100_64", "MP3 44.1 kHz 64 kbps"), new("mp3_22050_32", "MP3 22 kHz 32 kbps"),
            new("pcm_24000", "PCM 24 kHz (WAV)"), new("pcm_44100", "PCM 44.1 kHz (WAV, Pro)"),
        ];
        Normalizations = [new("auto", t("voice.norm.auto")), new("on", t("voice.norm.on")), new("off", t("voice.norm.off"))];

        CheckElevenCommand = new AsyncRelayCommand(CheckElevenAsync, () => !Busy);
        RefreshVoicesCommand = new AsyncRelayCommand(RefreshVoicesAsync, () => !Busy && HasElevenKey);
        PreviewVoiceCommand = new RelayCommand(PreviewVoice, () => SelectedVoice?.PreviewUrl != null);
        DeleteVoiceCommand = new AsyncRelayCommand(DeleteVoiceAsync, () => !Busy && SelectedVoice?.IsOwn == true);
        AddCloneFilesCommand = new RelayCommand(AddCloneFiles);
        ClearCloneFilesCommand = new RelayCommand(() => CloneFiles.Clear(), () => CloneFiles.Count > 0);
        CloneCommand = new AsyncRelayCommand(CloneAsync, () => !Busy && CanClone);
        RefreshOpenAiModelsCommand = new AsyncRelayCommand(RefreshOpenAiModelsAsync, () => !Busy && OpenAiHasKey);
        TestCommand = new AsyncRelayCommand(() => TestAsync(TestGirl), () => !Busy);
        TestGirlCommand = new AsyncRelayCommand<GirlVoiceRow>(r => TestAsync(r?.Girl), _ => !Busy);
        StopCommand = new RelayCommand(StopPlaying);
        RefreshFreeVoicesCommand = new RelayCommand(LoadFreeVoices);

        GirlRows = new ObservableCollection<GirlVoiceRow>(VoicePersona.Girls.Select(g => new GirlVoiceRow(this, g)));
        CloneFiles.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(CloneFilesText)); OnPropertyChanged(nameof(CanClone)); };
        Load();
    }

    // ─── load / save ───

    /// <summary>Read the saved settings into the page (on open, and when Settings › Reset ran).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var v = V;
            _engine = Array.IndexOf(VoiceSettings.Engines, v.Engine) >= 0 ? v.Engine : VoiceSettings.Edge;
            _personaVoices = v.PersonaVoices;
            _maxChars = Math.Clamp(v.MaxChars, 100, 10000);
            _elevenKey = v.ElevenLabsApiKey ?? "";
            _stability = v.Stability; _similarity = v.Similarity; _style = v.Style; _speakerBoost = v.SpeakerBoost; _speed = v.Speed;
            _outputFormat = v.OutputFormat; _normalization = v.TextNormalization; _audioTags = v.AudioTags;
            _openAiSpeed = v.OpenAiSpeed; _openAiInstructions = v.OpenAiInstructions ?? "";
            _browserVoice = v.BrowserVoice ?? ""; _browserRate = v.BrowserRate; _browserPitch = v.BrowserPitch;
            _windowsVoice = v.WindowsVoice ?? ""; _windowsRate = v.WindowsRate;
            _edgeVoice = v.EdgeVoice ?? ""; _edgeRate = v.EdgeRate; _edgePitch = v.EdgePitch;

            FillModels(VoiceSpeaker.FetchedModels ?? ElevenLabsClient.KnownModels);
            _selectedModel = Models.FirstOrDefault(m => m.Model.Id == ElevenLabsClient.Successor(v.ElevenLabsModel)) ?? Models.FirstOrDefault();
            if (Voices.Count == 0 && !string.IsNullOrWhiteSpace(v.ElevenLabsVoiceId))
                Voices.Add(new ElevenVoice(v.ElevenLabsVoiceId, string.IsNullOrWhiteSpace(v.ElevenLabsVoiceName) ? v.ElevenLabsVoiceId : v.ElevenLabsVoiceName,
                    "", "", null, [], ""));
            _selectedVoice = Voices.FirstOrDefault(x => x.Id == v.ElevenLabsVoiceId);

            OpenAiModels.Clear();
            foreach (var m in OpenAiTtsClient.KnownModels) OpenAiModels.Add(m);
            if (!string.IsNullOrWhiteSpace(v.OpenAiModel) && !OpenAiModels.Contains(v.OpenAiModel)) OpenAiModels.Insert(0, v.OpenAiModel);
            _openAiModel = string.IsNullOrWhiteSpace(v.OpenAiModel) ? OpenAiModels[0] : v.OpenAiModel;
            _openAiVoice = v.OpenAiVoice;
            LoadFreeVoices();
        }
        finally { _loading = false; }
        OnPropertyChanged(string.Empty);
        foreach (var r in GirlRows) r.Refresh();
    }

    private void Changed() { if (!_loading) { _saveTimer.Stop(); _saveTimer.Start(); } }

    /// <summary>Write the page into settings.json now (also run when the page closes with a save pending).</summary>
    public void Save()
    {
        _saveTimer.Stop();
        bool ok = _settings.UpdateSettings(s =>
        {
            var v = s.Voice ??= new VoiceSettings();
            v.Engine = _engine;
            v.PersonaVoices = _personaVoices;
            v.MaxChars = Math.Clamp(_maxChars, 100, 10000);
            v.ElevenLabsApiKey = string.IsNullOrWhiteSpace(_elevenKey) ? null : _elevenKey.Trim();
            if (_selectedModel != null) v.ElevenLabsModel = _selectedModel.Model.Id;
            if (_selectedVoice != null) { v.ElevenLabsVoiceId = _selectedVoice.Id; v.ElevenLabsVoiceName = _selectedVoice.Name; }
            v.Stability = Math.Round(Math.Clamp(_stability, 0, 1), 2);
            v.Similarity = Math.Round(Math.Clamp(_similarity, 0, 1), 2);
            v.Style = Math.Round(Math.Clamp(_style, 0, 1), 2);
            v.SpeakerBoost = _speakerBoost;
            v.Speed = Math.Round(Math.Clamp(_speed, 0.7, 1.2), 2);
            v.OutputFormat = OutputFormats.Any(f => f.Value == _outputFormat) ? _outputFormat : "mp3_44100_128";
            v.TextNormalization = _normalization is "on" or "off" ? _normalization : "auto";
            v.AudioTags = _audioTags;
            v.OpenAiModel = string.IsNullOrWhiteSpace(_openAiModel) ? OpenAiTtsClient.KnownModels[0] : _openAiModel;
            v.OpenAiVoice = string.IsNullOrWhiteSpace(_openAiVoice) ? "marin" : _openAiVoice;
            v.OpenAiSpeed = Math.Round(Math.Clamp(_openAiSpeed, 0.25, 4), 2);
            v.OpenAiInstructions = _openAiInstructions.Trim();
            v.BrowserVoice = _browserVoice.Trim();
            v.BrowserRate = Math.Round(Math.Clamp(_browserRate, 0.5, 2), 2);
            v.BrowserPitch = Math.Round(Math.Clamp(_browserPitch, 0.5, 2), 2);
            v.WindowsVoice = _windowsVoice.Trim();
            v.WindowsRate = Math.Clamp(_windowsRate, -10, 10);
            v.EdgeVoice = _edgeVoice.Trim();
            v.EdgeRate = Math.Round(Math.Clamp(_edgeRate, 0.5, 2), 2);
            v.EdgePitch = Math.Round(Math.Clamp(_edgePitch, 0.5, 2), 2);
            foreach (var k in v.Characters.Where(kv => kv.Value == null || kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) v.Characters.Remove(k);
        });
        SaveText = ok ? "" : _t("voice.saveFailed") + " " + _settings.LastSaveError;
    }

    // ─── engine ───

    public IReadOnlyList<VoiceEngineOption> EngineOptions { get; }

    private string _engine = VoiceSettings.Edge;
    public string Engine
    {
        get => _engine;
        set
        {
            if (Array.IndexOf(VoiceSettings.Engines, value) < 0 || !SetProperty(ref _engine, value)) return;
            OnPropertyChanged(nameof(IsBrowser)); OnPropertyChanged(nameof(IsWindows));
            OnPropertyChanged(nameof(IsEdge)); OnPropertyChanged(nameof(WindowsNoThai));
            OnPropertyChanged(nameof(IsEleven)); OnPropertyChanged(nameof(IsOpenAi)); OnPropertyChanged(nameof(IsFree));
            OnPropertyChanged(nameof(EngineNote)); OnPropertyChanged(nameof(GirlVoiceChoices));
            MascotChanged();
            foreach (var r in GirlRows) r.Refresh();
            Changed();
        }
    }
    public bool IsEdge => _engine == VoiceSettings.Edge;
    public bool IsBrowser => _engine == VoiceSettings.Browser;
    public bool IsWindows => _engine == VoiceSettings.Windows;
    public bool IsEleven => _engine == VoiceSettings.ElevenLabs;
    public bool IsOpenAi => _engine == VoiceSettings.OpenAI;
    public bool IsFree => IsEdge || IsBrowser || IsWindows;
    public string EngineNote => EngineOptions.FirstOrDefault(e => e.Key == _engine)?.Note ?? "";

    // ─── the girl who presents the page: ElevenLabs' own, Codex for OpenAI, CluadeX for the free voices ───

    private string MascotGirl => _engine switch
    {
        VoiceSettings.ElevenLabs => "elevenlabs",
        VoiceSettings.OpenAI => "codex",
        VoiceSettings.Windows => "mai",
        _ => "cluadex",
    };
    public ImageSource? MascotImage => GirlArt.Image(ChatCharacter.ForGirl(MascotGirl).Girl);
    public string MascotName => ChatCharacter.ForGirl(MascotGirl).Name;
    public Brush MascotAccent => ChatCharacter.ForGirl(MascotGirl).AccentBrush;
    public string MascotLine => _t(IsEdge ? "voice.mascot.edge" : "voice.mascot." + MascotGirl);
    private void MascotChanged()
    {
        OnPropertyChanged(nameof(MascotImage)); OnPropertyChanged(nameof(MascotName));
        OnPropertyChanged(nameof(MascotAccent)); OnPropertyChanged(nameof(MascotLine));
    }

    private bool _personaVoices = true;
    public bool PersonaVoices { get => _personaVoices; set { if (SetProperty(ref _personaVoices, value)) Changed(); } }

    private int _maxChars = 1200;
    public int MaxChars { get => _maxChars; set { if (SetProperty(ref _maxChars, value)) Changed(); } }

    private bool _busy;
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    private string _status = "";
    /// <summary>The last check / clone / test result, in a line.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    private string _saveText = "";
    public string SaveText { get => _saveText; private set => SetProperty(ref _saveText, value); }

    private void Say(string text, bool error = false) { Status = text; StatusIsError = error; }

    // ─── ElevenLabs: key and account ───

    private string _elevenKey = "";
    public string ElevenKey
    {
        get => _elevenKey;
        set
        {
            if (!SetProperty(ref _elevenKey, value ?? "")) return;
            OnPropertyChanged(nameof(MaskedElevenKey)); OnPropertyChanged(nameof(HasElevenKey));
            Changed();
        }
    }
    public bool HasElevenKey => !string.IsNullOrWhiteSpace(_elevenKey);
    public string MaskedElevenKey => string.IsNullOrEmpty(_elevenKey) ? ""
        : _elevenKey.Length <= 8 ? new string('•', _elevenKey.Length) : _elevenKey[..4] + new string('•', 12) + _elevenKey[^4..];
    private bool _showElevenKey;
    public bool ShowElevenKey { get => _showElevenKey; set => SetProperty(ref _showElevenKey, value); }

    private string _accountText = "";
    /// <summary>"Creator · 12,340 / 100,000 characters · can clone".</summary>
    public string AccountText { get => _accountText; private set => SetProperty(ref _accountText, value); }
    private bool? _accountCanClone;

    private ElevenLabsClient Eleven() => new(_elevenKey);

    public ICommand CheckElevenCommand { get; }
    public ICommand RefreshVoicesCommand { get; }

    /// <summary>Check the key: the plan and usage, every model and the account's voices.</summary>
    private async Task CheckElevenAsync()
    {
        if (!HasElevenKey) { Say(_t("voice.eleven.noKey"), true); return; }
        Save();
        Busy = true;
        Say(_t("voice.eleven.checking"));
        try
        {
            var client = Eleven();
            var account = await client.AccountAsync();
            _accountCanClone = account.CanClone;
            AccountText = AccountLine(account);
            var models = await client.ModelsAsync();
            var tts = models.Where(m => m.CanTts).ToList();
            if (tts.Count > 0)
            {
                VoiceSpeaker.FetchedModels = tts;
                string keep = _selectedModel?.Model.Id ?? V.ElevenLabsModel;
                FillModels(tts);
                SelectedModel = Models.FirstOrDefault(m => m.Model.Id == ElevenLabsClient.Successor(keep)) ?? Models.FirstOrDefault();
            }
            await LoadVoicesAsync(client);
            Say(string.Format(_t("voice.eleven.ok"), tts.Count, Voices.Count));
            OnPropertyChanged(nameof(CanClone));
        }
        catch (Exception ex) { Say(_t("voice.eleven.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    private string AccountLine(ElevenAccount a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Tier)) parts.Add(a.Tier!);
        if (a.CharacterLimit is long lim && lim > 0)
            parts.Add(string.Format(_t("voice.eleven.usage"), (a.CharactersUsed ?? 0).ToString("N0"), lim.ToString("N0")));
        if (a.ResetsUtc is DateTime r) parts.Add(string.Format(_t("voice.eleven.resets"), r.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)));
        if (a.CanClone is bool c) parts.Add(c ? _t("voice.eleven.canClone") : _t("voice.eleven.noClone"));
        return string.Join(" · ", parts);
    }

    private async Task RefreshVoicesAsync()
    {
        Busy = true;
        try { await LoadVoicesAsync(Eleven()); Say(string.Format(_t("voice.eleven.voices"), Voices.Count)); }
        catch (Exception ex) { Say(_t("voice.eleven.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    private async Task LoadVoicesAsync(ElevenLabsClient client)
    {
        var list = await client.VoicesAsync(string.IsNullOrWhiteSpace(VoiceSearch) ? null : VoiceSearch);
        string keep = _selectedVoice?.Id ?? V.ElevenLabsVoiceId;
        Voices.Clear();
        foreach (var v in list.OrderByDescending(v => v.IsOwn).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)) Voices.Add(v);
        _selectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedVoice)); OnPropertyChanged(nameof(VoiceInfo));
        OnPropertyChanged(nameof(GirlVoiceChoices));
        foreach (var r in GirlRows) r.Refresh();
        Changed();
    }

    // ─── ElevenLabs: model and what it can do ───

    public ObservableCollection<VoiceModelOption> Models { get; } = new();

    private void FillModels(IEnumerable<ElevenModel> models)
    {
        Models.Clear();
        foreach (var m in models) Models.Add(new VoiceModelOption(m, m.Name, Badges(m)));
    }

    private string Badges(ElevenModel m)
    {
        var b = new List<string> { m.Thai ? _t("voice.model.thai") : _t("voice.model.noThai") };
        if (m.AudioTags) b.Add(_t("voice.model.tags"));
        if (m.CanUseStyle) b.Add(_t("voice.model.style"));
        if (m.CanBeFinetuned) b.Add(_t("voice.model.clone"));
        b.Add(string.Format(_t("voice.model.max"), m.MaxChars.ToString("N0")));
        if (Math.Abs(m.CostFactor - 1) > 0.01) b.Add(string.Format(_t("voice.model.cost"), m.CostFactor.ToString("0.##")));
        if (m.AlphaOnly) b.Add(_t("voice.model.alpha"));
        return string.Join(" · ", b);
    }

    private VoiceModelOption? _selectedModel;
    public VoiceModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (value == null || !SetProperty(ref _selectedModel, value)) return;
            OnPropertyChanged(nameof(ModelInfo)); OnPropertyChanged(nameof(ModelBadges)); OnPropertyChanged(nameof(StyleEnabled));
            OnPropertyChanged(nameof(BoostEnabled)); OnPropertyChanged(nameof(TagsEnabled)); OnPropertyChanged(nameof(ThaiWarning));
            OnPropertyChanged(nameof(StabilitySteps)); OnPropertyChanged(nameof(StabilityText));
            if (StabilitySteps) Stability = _stability < 0.25 ? 0 : _stability < 0.75 ? 0.5 : 1;
            Changed();
        }
    }
    public string ModelInfo => _selectedModel?.Model.Description ?? "";
    public string ModelBadges => _selectedModel?.Badges ?? "";
    public bool StyleEnabled => _selectedModel?.Model.CanUseStyle == true;
    public bool BoostEnabled => _selectedModel?.Model.CanUseSpeakerBoost == true;
    public bool TagsEnabled => _selectedModel?.Model.AudioTags == true;
    public bool StabilitySteps => _selectedModel?.Model.StabilitySteps == true;
    /// <summary>The chosen model cannot speak Thai — a Thai reply would come out wrong.</summary>
    public bool ThaiWarning => _selectedModel != null && !_selectedModel.Model.Thai;

    // ─── ElevenLabs: voice ───

    public ObservableCollection<ElevenVoice> Voices { get; } = new();
    private string _voiceSearch = "";
    public string VoiceSearch { get => _voiceSearch; set => SetProperty(ref _voiceSearch, value ?? ""); }

    private ElevenVoice? _selectedVoice;
    public ElevenVoice? SelectedVoice
    {
        get => _selectedVoice;
        set { if (value != null && SetProperty(ref _selectedVoice, value)) { OnPropertyChanged(nameof(VoiceInfo)); Changed(); } }
    }
    public string VoiceInfo => _selectedVoice == null ? "" : string.Join(" · ", new[]
    {
        _selectedVoice.Category, _selectedVoice.Labels,
        _selectedVoice.Languages.Count > 0 ? string.Join(", ", _selectedVoice.Languages) : "",
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public ICommand PreviewVoiceCommand { get; }
    public ICommand DeleteVoiceCommand { get; }

    private void PreviewVoice()
    {
        if (_selectedVoice?.PreviewUrl is not { } url) return;
        Player().PlayUrl(url);
        Say(string.Format(_t("voice.previewing"), _selectedVoice.Name));
    }

    private async Task DeleteVoiceAsync()
    {
        var voice = _selectedVoice;
        if (voice == null || !voice.IsOwn) return;
        var answer = MessageBox.Show(string.Format(_t("voice.clone.deleteAsk"), voice.Name), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        Busy = true;
        try
        {
            await Eleven().DeleteVoiceAsync(voice.Id);
            foreach (var kv in V.Characters.Where(kv => kv.Value.ElevenLabsVoiceId == voice.Id)) { kv.Value.ElevenLabsVoiceId = null; kv.Value.ElevenLabsVoiceName = null; }
            if (V.ElevenLabsVoiceId == voice.Id) { V.ElevenLabsVoiceId = ""; V.ElevenLabsVoiceName = ""; }
            await LoadVoicesAsync(Eleven());
            Say(string.Format(_t("voice.clone.deleted"), voice.Name));
        }
        catch (Exception ex) { Say(_t("voice.eleven.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    // ─── ElevenLabs: fine settings ───

    private double _stability = 0.5, _similarity = 0.75, _style, _speed = 1;
    private bool _speakerBoost = true, _audioTags = true;
    private string _outputFormat = "mp3_44100_128", _normalization = "auto";

    public double Stability
    {
        get => _stability;
        set
        {
            if (StabilitySteps) value = value < 0.25 ? 0 : value < 0.75 ? 0.5 : 1;
            if (SetProperty(ref _stability, Math.Round(value, 2))) { OnPropertyChanged(nameof(StabilityText)); Changed(); }
        }
    }
    /// <summary>The number, or v3's name for it.</summary>
    public string StabilityText => StabilitySteps
        ? _t(_stability < 0.25 ? "voice.stab.creative" : _stability < 0.75 ? "voice.stab.natural" : "voice.stab.robust")
        : _stability.ToString("0.00");
    public double Similarity { get => _similarity; set { if (SetProperty(ref _similarity, Math.Round(value, 2))) Changed(); } }
    public double Style { get => _style; set { if (SetProperty(ref _style, Math.Round(value, 2))) Changed(); } }
    public double Speed { get => _speed; set { if (SetProperty(ref _speed, Math.Round(value, 2))) Changed(); } }
    public bool SpeakerBoost { get => _speakerBoost; set { if (SetProperty(ref _speakerBoost, value)) Changed(); } }
    public bool AudioTags { get => _audioTags; set { if (SetProperty(ref _audioTags, value)) Changed(); } }
    public IReadOnlyList<VoiceChoice> OutputFormats { get; }
    public string OutputFormat { get => _outputFormat; set { if (SetProperty(ref _outputFormat, value ?? "mp3_44100_128")) Changed(); } }
    public IReadOnlyList<VoiceChoice> Normalizations { get; }
    public string Normalization { get => _normalization; set { if (SetProperty(ref _normalization, value ?? "auto")) Changed(); } }

    // ─── ElevenLabs: instant voice cloning ───

    public ObservableCollection<string> CloneFiles { get; } = new();
    public string CloneFilesText => CloneFiles.Count == 0 ? _t("voice.clone.noFiles")
        : string.Format(_t("voice.clone.files"), CloneFiles.Count, string.Join(", ", CloneFiles.Select(Path.GetFileName)));
    private string _cloneName = "";
    public string CloneName { get => _cloneName; set { if (SetProperty(ref _cloneName, value ?? "")) OnPropertyChanged(nameof(CanClone)); } }
    private string _cloneDescription = "";
    public string CloneDescription { get => _cloneDescription; set => SetProperty(ref _cloneDescription, value ?? ""); }
    private bool _cloneRemoveNoise = true;
    public bool CloneRemoveNoise { get => _cloneRemoveNoise; set => SetProperty(ref _cloneRemoveNoise, value); }
    private bool _cloneConsent;
    /// <summary>"This is my own voice, or I have the speaker's permission" — cloning stays off until it is ticked.</summary>
    public bool CloneConsent { get => _cloneConsent; set { if (SetProperty(ref _cloneConsent, value)) OnPropertyChanged(nameof(CanClone)); } }
    public bool CanClone => HasElevenKey && _cloneConsent && CloneFiles.Count > 0 && !string.IsNullOrWhiteSpace(_cloneName) && _accountCanClone != false;

    public ICommand AddCloneFilesCommand { get; }
    public ICommand ClearCloneFilesCommand { get; }
    public ICommand CloneCommand { get; }

    private void AddCloneFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = _t("voice.clone.pick"),
            Filter = _t("voice.clone.filter") + "|" + string.Join(";", ElevenLabsClient.SampleExtensions.Select(e => "*" + e)),
        };
        if (dlg.ShowDialog() != true) return;
        var skipped = new List<string>();
        foreach (var f in dlg.FileNames)
        {
            if (CloneFiles.Count >= ElevenLabsClient.MaxSamples) { skipped.Add(Path.GetFileName(f)); continue; }
            long size;
            try { size = new FileInfo(f).Length; } catch { skipped.Add(Path.GetFileName(f)); continue; }
            if (size == 0 || size > ElevenLabsClient.MaxSampleBytes) { skipped.Add(Path.GetFileName(f)); continue; }
            if (!CloneFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) CloneFiles.Add(f);
        }
        if (skipped.Count > 0) Say(string.Format(_t("voice.clone.skipped"), string.Join(", ", skipped)), true);
    }

    private async Task CloneAsync()
    {
        if (!CanClone) return;
        Save();
        Busy = true;
        Say(_t("voice.clone.working"));
        try
        {
            string name = _cloneName.Trim();
            string id = await Eleven().CloneAsync(name, CloneFiles.ToList(), _cloneDescription, _cloneRemoveNoise);
            await LoadVoicesAsync(Eleven());
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == id) ?? SelectedVoice;
            CloneFiles.Clear();
            CloneName = "";
            CloneConsent = false;
            Say(string.Format(_t("voice.clone.done"), name));
        }
        catch (Exception ex) { Say(_t("voice.clone.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    // ─── OpenAI (the AI Provider key) ───

    public bool OpenAiHasKey => VoiceSpeaker.OpenAiKey(_settings.Settings).Key.Length > 0;
    public ObservableCollection<string> OpenAiModels { get; } = new();
    private string _openAiModel = "gpt-4o-mini-tts", _openAiVoice = "marin", _openAiInstructions = "";
    private double _openAiSpeed = 1;
    public string OpenAiModel
    {
        get => _openAiModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SetProperty(ref _openAiModel, value)) return;
            OnPropertyChanged(nameof(OpenAiVoices)); OnPropertyChanged(nameof(OpenAiTakesInstructions)); OnPropertyChanged(nameof(GirlVoiceChoices));
            if (!OpenAiTtsClient.VoicesFor(value).Contains(_openAiVoice)) OpenAiVoice = OpenAiTtsClient.VoicesFor(value)[0];
            Changed();
        }
    }
    public IReadOnlyList<string> OpenAiVoices => OpenAiTtsClient.VoicesFor(_openAiModel);
    public bool OpenAiTakesInstructions => OpenAiTtsClient.TakesInstructions(_openAiModel);
    public string OpenAiVoice { get => _openAiVoice; set { if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _openAiVoice, value)) Changed(); } }
    public double OpenAiSpeed { get => _openAiSpeed; set { if (SetProperty(ref _openAiSpeed, Math.Round(value, 2))) Changed(); } }
    public string OpenAiInstructions { get => _openAiInstructions; set { if (SetProperty(ref _openAiInstructions, value ?? "")) Changed(); } }
    public ICommand RefreshOpenAiModelsCommand { get; }

    private async Task RefreshOpenAiModelsAsync()
    {
        var (key, apiBase) = VoiceSpeaker.OpenAiKey(_settings.Settings);
        Busy = true;
        try
        {
            var list = await new OpenAiTtsClient(key, apiBase).ModelsAsync();
            string keep = _openAiModel;
            OpenAiModels.Clear();
            foreach (var m in list) OpenAiModels.Add(m);
            OpenAiModel = OpenAiModels.Contains(keep) ? keep : OpenAiModels.FirstOrDefault() ?? keep;
            OnPropertyChanged(nameof(OpenAiModel));
            Say(string.Format(_t("voice.openai.models"), OpenAiModels.Count));
        }
        catch (Exception ex) { Say(_t("voice.eleven.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    // ─── free voices: the browser's (reported by the mic window) and Windows' own ───

    public ObservableCollection<string> BrowserVoices { get; } = new();
    public ObservableCollection<string> WindowsVoices { get; } = new();
    public bool NoBrowserVoices => BrowserVoices.Count == 0;
    public ICommand RefreshFreeVoicesCommand { get; }

    private void LoadFreeVoices()
    {
        BrowserVoices.Clear();
        foreach (var v in V.KnownBrowserVoices.OrderByDescending(n => n.Contains("th-TH", StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(n => n.Contains("Natural", StringComparison.OrdinalIgnoreCase)).ThenBy(n => n))
            BrowserVoices.Add(v);
        WindowsVoices.Clear();
        foreach (var v in WindowsTts.Voices()) WindowsVoices.Add($"{v.Name}");
        OnPropertyChanged(nameof(NoBrowserVoices)); OnPropertyChanged(nameof(GirlVoiceChoices));
    }

    private string _browserVoice = "", _windowsVoice = "";
    private double _browserRate = 1, _browserPitch = 1;
    private int _windowsRate;
    public string BrowserVoice { get => _browserVoice; set { if (SetProperty(ref _browserVoice, value ?? "")) Changed(); } }
    public double BrowserRate { get => _browserRate; set { if (SetProperty(ref _browserRate, Math.Round(value, 2))) Changed(); } }
    public double BrowserPitch { get => _browserPitch; set { if (SetProperty(ref _browserPitch, Math.Round(value, 2))) Changed(); } }
    public string WindowsVoice { get => _windowsVoice; set { if (SetProperty(ref _windowsVoice, value ?? "")) Changed(); } }
    public int WindowsRate { get => _windowsRate; set { if (SetProperty(ref _windowsRate, Math.Clamp(value, -10, 10))) Changed(); } }

    /// <summary>Windows has no Thai voice (most PCs: David and Zira only) — the page says so; Thai replies are read
    /// with Edge's free voices instead.</summary>
    public bool WindowsNoThai => IsWindows && !WindowsTts.Speaks("th");

    // ─── free: Edge's online voices ───

    /// <summary>"" first = the voice for the reply's language (Premwadee for Thai).</summary>
    public IReadOnlyList<VoiceChoice> EdgeVoices =>
        [new("", _t("voice.edge.auto")), .. EdgeTts.Voices.Select(v => new VoiceChoice(v.Name, v.Label))];
    private string _edgeVoice = "";
    private double _edgeRate = 1, _edgePitch = 1;
    public string EdgeVoice { get => _edgeVoice; set { if (SetProperty(ref _edgeVoice, value ?? "")) Changed(); } }
    public double EdgeRate { get => _edgeRate; set { if (SetProperty(ref _edgeRate, Math.Round(value, 2))) Changed(); } }
    public double EdgePitch { get => _edgePitch; set { if (SetProperty(ref _edgePitch, Math.Round(value, 2))) Changed(); } }

    // ─── each girl's own voice ───

    public ObservableCollection<GirlVoiceRow> GirlRows { get; }

    /// <summary>The voices a girl can be given on the current engine ("" first = the main voice).</summary>
    public IReadOnlyList<VoiceChoice> GirlVoiceChoices
    {
        get
        {
            var list = new List<VoiceChoice> { new("", _t("voice.girls.main")) };
            switch (_engine)
            {
                case VoiceSettings.ElevenLabs: list.AddRange(Voices.Select(v => new VoiceChoice(v.Id, v.Name))); break;
                case VoiceSettings.OpenAI: list.AddRange(OpenAiVoices.Select(v => new VoiceChoice(v, v))); break;
                case VoiceSettings.Windows: list.AddRange(WindowsVoices.Select(v => new VoiceChoice(v, v))); break;
                case VoiceSettings.Edge: list.AddRange(EdgeTts.Voices.Select(v => new VoiceChoice(v.Name, v.Label))); break;
                default: list.AddRange(BrowserVoices.Select(v => new VoiceChoice(v, v))); break;
            }
            // A saved voice no longer in the list still shows (by its saved name).
            foreach (var r in VoicePersona.Girls)
            {
                string cur = GirlVoice(r);
                if (cur.Length > 0 && list.All(c => c.Value != cur))
                    list.Add(new VoiceChoice(cur, _engine == VoiceSettings.ElevenLabs ? V.For(r)?.ElevenLabsVoiceName ?? cur : cur));
            }
            return list;
        }
    }

    internal string GirlVoice(string girl)
    {
        var c = V.For(girl);
        return (_engine switch
        {
            VoiceSettings.ElevenLabs => c?.ElevenLabsVoiceId,
            VoiceSettings.OpenAI => c?.OpenAiVoice,
            VoiceSettings.Windows => c?.WindowsVoice,
            VoiceSettings.Edge => c?.EdgeVoice,
            _ => c?.BrowserVoice,
        }) ?? "";
    }

    internal void SetGirlVoice(string girl, string voice)
    {
        string key = girl.ToLowerInvariant();
        if (!V.Characters.TryGetValue(key, out var c)) V.Characters[key] = c = new CharacterVoice();
        string? value = string.IsNullOrWhiteSpace(voice) ? null : voice;
        switch (_engine)
        {
            case VoiceSettings.ElevenLabs:
                c.ElevenLabsVoiceId = value;
                c.ElevenLabsVoiceName = value == null ? null : Voices.FirstOrDefault(v => v.Id == value)?.Name ?? c.ElevenLabsVoiceName;
                break;
            case VoiceSettings.OpenAI: c.OpenAiVoice = value; break;
            case VoiceSettings.Windows: c.WindowsVoice = value; break;
            case VoiceSettings.Edge: c.EdgeVoice = value; break;
            default: c.BrowserVoice = value; break;
        }
        Changed();
    }

    // ─── test ───

    /// <summary>Who reads the test line — shown by name, saved by id.</summary>
    public IReadOnlyList<VoiceChoice> TestGirls => GirlRows.Select(r => new VoiceChoice(r.Girl, r.Name)).ToList();
    private string _testGirl = "cluadex";
    public string TestGirl { get => _testGirl; set => SetProperty(ref _testGirl, value ?? "cluadex"); }
    private string _testText = "";
    public string TestText { get => _testText; set => SetProperty(ref _testText, value ?? ""); }

    public ICommand TestCommand { get; }
    public ICommand TestGirlCommand { get; }
    public ICommand StopCommand { get; }

    private VoicePlayer Player()
    {
        if (_player != null) return _player;
        _player = new VoicePlayer(_settings.DataRoot);
        _player.Finished += err => { if (err != null) Say(_t("voice.playFailed") + " " + err, true); };
        return _player;
    }

    /// <summary>Speak a sample in <paramref name="girl"/>'s voice with the settings on the page (saved first).</summary>
    private async Task TestAsync(string? girl)
    {
        Save();
        if (IsBrowser) { Say(_t("voice.test.browser")); return; }
        girl ??= _testGirl;
        bool thai = _settings.Settings.Language == "th";
        string text = string.IsNullOrWhiteSpace(_testText)
            ? string.Format(_t("voice.test.sample"), ChatCharacter.ForGirl(girl).Name)
            : _testText.Trim();
        if (text.Length > 500) text = text[..500];
        thai = thai || text.Any(ch => ch >= '฀' && ch <= '๿');
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        Busy = true;
        Say(string.Format(_t("voice.test.making"), ChatCharacter.ForGirl(girl).Name));
        try
        {
            var speaker = new VoiceSpeaker(() => _settings.Settings);
            // Windows with no voice for this language reads it as silence: Edge's free voices read it, as in the chat.
            bool edge = IsWindows && !WindowsTts.Speaks(thai ? "th-TH" : "en-US");
            SpeechAudio audio;
            try
            {
                audio = edge ? await speaker.SynthesizeEdgeAsync(text, thai, girl, cts.Token)
                             : await speaker.SynthesizeAsync(text, thai, girl, cts.Token);
            }
            catch (WindowsVoiceMissingException)
            {
                edge = true;
                audio = await speaker.SynthesizeEdgeAsync(text, thai, girl, cts.Token);
            }
            if (cts.IsCancellationRequested) return;
            Player().Play(audio);
            Say(string.Format(_t(edge ? "voice.test.playingEdge" : "voice.test.playing"), ChatCharacter.ForGirl(girl).Name,
                (audio.Bytes.Length / 1024.0).ToString("0")));
        }
        catch (VoiceKeyMissingException ex) { Say(_t(ex.Engine == VoiceSettings.OpenAI ? "voice.openai.noKey" : "voice.eleven.noKey"), true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say(_t("voice.test.failed") + " " + ex.Message, true); }
        finally { Busy = false; }
    }

    private void StopPlaying()
    {
        _testCts?.Cancel();
        _player?.Stop();
        Say("");
    }

    /// <summary>The page is going away: save what is pending, stop the sound.</summary>
    public void Close()
    {
        if (_saveTimer.IsEnabled) Save();
        _testCts?.Cancel();
        _player?.Dispose();
        _player = null;
    }
}
