using System.ComponentModel;
using System.Windows.Data;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace CluadeX.ViewModels;

/// <summary>
/// What the Settings page SHOWS for the provider being configured — never what is saved (owner 2026-11-10: "หน้า
/// settings ดูรก … ตั้งค่า provider ซ้ำซ้อน มีตัวเลือกที่ไม่จำเป็น"). The connection card only configures a provider;
/// the chat's model box is what switches which one answers. Each field shows only for the providers that read it:
/// a key for the ones that take one (optional on servers on this PC), a base URL where it has to be typed (Azure, a
/// custom endpoint, Ollama, LM Studio / vLLM) and behind "use another address" elsewhere, no model box for llama.cpp,
/// the built-in GGUF runner, BrainX and Colibri (none of them read ProviderConfigs' model). Display-only: nothing
/// here is in <see cref="Writers"/> or <see cref="ProviderFields"/>, so none of it marks the page as edited.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>How a provider is connected — decides which fields of the connection card it needs.</summary>
    public enum ProviderCardKind
    {
        /// <summary>Anthropic, OpenAI, Gemini: a key, a fixed address.</summary>
        CloudKey,
        /// <summary>An OpenAI-compatible cloud preset (OpenRouter, Groq …): a key, a fixed address.</summary>
        CompatibleCloud,
        /// <summary>Azure OpenAI: a key and the resource's own address.</summary>
        Azure,
        /// <summary>Any other OpenAI-compatible endpoint: its address, a key if it wants one.</summary>
        Custom,
        /// <summary>LM Studio, vLLM: a server on this PC, no key needed.</summary>
        LocalServer,
        /// <summary>Ollama on this PC (or another machine's address).</summary>
        Ollama,
        /// <summary>An installed CLI (Claude Code, Gemini, Antigravity, Codex, Grok Build): its own sign-in, no key.</summary>
        Cli,
        /// <summary>The BrainX cowork room: nothing to set here.</summary>
        BrainX,
        /// <summary>Colibri: the plugin writes its address and model while it runs.</summary>
        Plugin,
        /// <summary>llama.cpp server or the built-in GGUF runner: loaded on the Models page.</summary>
        LocalGguf,
    }

    public static ProviderCardKind KindOf(AiProviderType t) => t switch
    {
        AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini => ProviderCardKind.CloudKey,
        AiProviderType.ClaudeDev or AiProviderType.GeminiDev or AiProviderType.AntigravityDev
            or AiProviderType.OpenAIDev or AiProviderType.GrokDev => ProviderCardKind.Cli,
        AiProviderType.BrainX => ProviderCardKind.BrainX,
        AiProviderType.Colibri => ProviderCardKind.Plugin,
        AiProviderType.Local or AiProviderType.LlamaServer => ProviderCardKind.LocalGguf,
        AiProviderType.Ollama => ProviderCardKind.Ollama,
        AiProviderType.AzureOpenAI => ProviderCardKind.Azure,
        AiProviderType.OpenAICompatible => ProviderCardKind.Custom,
        _ => CompatiblePresets.For(t)?.Local == true ? ProviderCardKind.LocalServer : ProviderCardKind.CompatibleCloud,
    };

    public ProviderCardKind SelectedKind => KindOf(SelectedProvider);

    // ── the picker, in groups ──

    /// <summary>A group of the provider picker; its title follows the UI language.</summary>
    public sealed class ProviderGroup : INotifyPropertyChanged
    {
        public ProviderGroup(string key) => Key = key;
        public string Key { get; }
        private string _title = "";
        public string Title { get => _title; set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One row of the picker: the provider's mark and the name the card uses, inside its group.</summary>
    public sealed record ProviderChoice(AiProviderType Type, string Name, System.Windows.Media.ImageSource? Logo, string Badge, ProviderGroup Group)
    {
        public bool HasLogo => Logo != null;
    }

    private static readonly ProviderGroup GroupCloud = new("settings.provGroup.cloud"), GroupCli = new("settings.provGroup.cli"),
        GroupCompatible = new("settings.provGroup.compatible"), GroupLocal = new("settings.provGroup.local"),
        GroupOther = new("settings.provGroup.other");

    private static ProviderGroup GroupOf(AiProviderType t) => KindOf(t) switch
    {
        ProviderCardKind.CloudKey => GroupCloud,
        ProviderCardKind.Cli => GroupCli,
        ProviderCardKind.CompatibleCloud or ProviderCardKind.Azure or ProviderCardKind.Custom => GroupCompatible,
        ProviderCardKind.LocalServer or ProviderCardKind.Ollama or ProviderCardKind.Plugin or ProviderCardKind.LocalGguf => GroupLocal,
        _ => GroupOther,
    };

    /// <summary>The card's own name for the providers whose enum name is all the picker had ("ClaudeDev", "Local").</summary>
    private static string CardName(AiProviderType t) => t switch
    {
        AiProviderType.Anthropic => "Anthropic (Claude API)",
        AiProviderType.OpenAI => "OpenAI API",
        AiProviderType.Gemini => "Google Gemini API",
        AiProviderType.ClaudeDev => "Claude Code (CLI)",
        AiProviderType.GeminiDev => "Gemini CLI",
        AiProviderType.AntigravityDev => "Antigravity CLI",
        AiProviderType.OpenAIDev => "Codex CLI (ChatGPT)",
        AiProviderType.GrokDev => "Grok Build CLI",
        AiProviderType.BrainX => "BrainX (cowork)",
        AiProviderType.LlamaServer => "llama.cpp server (GGUF)",
        AiProviderType.Local => "GGUF built-in (LLamaSharp)",
        _ => ProviderLogos.Name(t),
    };

    /// <summary>Picker order: cloud APIs, CLIs, OpenAI-compatible services (Azure and a custom endpoint last), servers
    /// on this PC, then BrainX. Within a group, the enum's order.</summary>
    private static readonly ProviderCardKind[] GroupOrder =
    [
        ProviderCardKind.CloudKey, ProviderCardKind.Cli, ProviderCardKind.CompatibleCloud, ProviderCardKind.Azure,
        ProviderCardKind.Custom, ProviderCardKind.Ollama, ProviderCardKind.LocalServer, ProviderCardKind.Plugin,
        ProviderCardKind.LocalGguf, ProviderCardKind.BrainX,
    ];

    /// <summary>The picker's rows: every provider with its logo (or badge), grouped.</summary>
    public List<ProviderChoice> ProviderChoices { get; } = BuildProviderChoices();

    public static List<ProviderChoice> BuildProviderChoices() => Enum.GetValues<AiProviderType>()
        .OrderBy(t => Array.IndexOf(GroupOrder, KindOf(t))).ThenBy(t => (int)t)
        .Select(t => new ProviderChoice(t, CardName(t), ProviderLogos.Image(t), ProviderLogos.Badge(t), GroupOf(t)))
        .ToList();

    private ListCollectionView? _providerChoicesView;
    /// <summary><see cref="ProviderChoices"/> grouped for the combo box (GroupStyle headers).</summary>
    public ListCollectionView ProviderChoicesView
    {
        get
        {
            if (_providerChoicesView == null)
            {
                _providerChoicesView = new ListCollectionView(ProviderChoices);
                _providerChoicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProviderChoice.Group)));
            }
            return _providerChoicesView;
        }
    }

    private void RefreshProviderGroupTitles()
    {
        foreach (var g in new[] { GroupCloud, GroupCli, GroupCompatible, GroupLocal, GroupOther }) g.Title = T(g.Key);
    }

    // ── which fields the card shows ──

    /// <summary>The key is optional: a server on this PC, or a custom endpoint that may run without one.</summary>
    public bool ApiKeyOptional => NeedsApiKey && SelectedPreset is { KeyRequired: false };
    public string ApiKeyLabel => T(ApiKeyOptional ? "settings.apiKeyOptional" : "settings.apiKey");
    public string ApiKeyHelp => T(ApiKeyOptional ? "settings.apiKeyOptionalDesc" : "settings.apiKeyDesc");
    /// <summary>The key page under the key box — not when the guide above already links it.</summary>
    public bool HasApiKeyLink => ApiKeyLink.Length > 0 && !ShowProviderGuide;

    /// <summary>The address must (or usually does) get typed: Azure, a custom endpoint, Ollama, LM Studio / vLLM.</summary>
    public bool BaseUrlNeeded => NeedsBaseUrl(SelectedProvider);
    public static bool NeedsBaseUrl(AiProviderType t) => KindOf(t) is ProviderCardKind.Azure or ProviderCardKind.Custom
        or ProviderCardKind.Ollama or ProviderCardKind.LocalServer;
    /// <summary>A provider with a fixed address that still reads BaseUrl (a proxy): the box hides behind a switch.</summary>
    public bool BaseUrlIsAdvanced => SupportsBaseUrl && !BaseUrlNeeded && SelectedKind != ProviderCardKind.Plugin;

    private bool _showBaseUrlOverride;
    /// <summary>"Use another address (proxy)" — opened by itself when the provider already has one. Unticking it means
    /// the service's own address again, so it empties the box: a hidden address that still routed every request through
    /// the old proxy would be a setting nobody can see.</summary>
    public bool ShowBaseUrlOverride
    {
        get => _showBaseUrlOverride;
        set
        {
            if (!SetProperty(ref _showBaseUrlOverride, value)) return;
            if (!value && BaseUrlIsAdvanced && !string.IsNullOrEmpty(ProviderBaseUrl)) ProviderBaseUrl = "";
            OnPropertyChanged(nameof(ShowBaseUrlBox));
        }
    }
    public bool ShowBaseUrlBox => SupportsBaseUrl && SelectedKind != ProviderCardKind.Plugin && (BaseUrlNeeded || ShowBaseUrlOverride);
    public string BaseUrlLabel => T(SelectedKind is ProviderCardKind.Azure or ProviderCardKind.Custom
        ? "settings.baseUrlRequired" : BaseUrlNeeded ? "settings.baseUrlServer" : "settings.baseUrl");
    public string BaseUrlHelp => T(SelectedKind switch
    {
        ProviderCardKind.Azure or ProviderCardKind.Custom => "settings.baseUrlRequiredDesc",
        ProviderCardKind.Ollama or ProviderCardKind.LocalServer => "settings.baseUrlServerDesc",
        _ => "settings.baseUrlDesc",
    });

    /// <summary>The model box: every provider that reads ProviderConfigs' model — not llama.cpp / the GGUF runner
    /// (the Models page loads a file), BrainX (the room answers) or Colibri (its plugin writes the model).</summary>
    public bool ShowModelFields => ShowsModelFields(SelectedProvider);
    public static bool ShowsModelFields(AiProviderType t) => KindOf(t) is not (ProviderCardKind.LocalGguf or ProviderCardKind.BrainX or ProviderCardKind.Plugin);

    /// <summary>One line for the providers with little or nothing to set here.</summary>
    public string ProviderNote => SelectedKind switch
    {
        ProviderCardKind.Cli => T("settings.provNote.cli"),
        ProviderCardKind.BrainX => T("settings.provNote.brainx"),
        ProviderCardKind.Plugin => T("settings.provNote.colibri"),
        ProviderCardKind.LocalGguf => T("settings.provNote.gguf"),
        _ => "",
    };
    public bool ShowModelsPageButton => SelectedKind == ProviderCardKind.LocalGguf;
    public bool ShowPluginsPageButton => SelectedKind == ProviderCardKind.Plugin;

    /// <summary>The provider card changed provider: everything above, the base URL switch as the saved address says.</summary>
    private void RaiseCardLayoutChanged()
    {
        // the field, not the property: the switch follows the address just loaded and must not empty anything
        _showBaseUrlOverride = !string.IsNullOrWhiteSpace(ProviderBaseUrl);
        OnPropertyChanged(nameof(ShowBaseUrlOverride));
        foreach (var name in new[] { nameof(SelectedKind), nameof(ApiKeyOptional), nameof(ApiKeyLabel), nameof(ApiKeyHelp),
                     nameof(HasApiKeyLink), nameof(BaseUrlNeeded), nameof(BaseUrlIsAdvanced), nameof(ShowBaseUrlBox),
                     nameof(BaseUrlLabel), nameof(BaseUrlHelp), nameof(ShowModelFields), nameof(ProviderNote),
                     nameof(ShowModelsPageButton), nameof(ShowPluginsPageButton) })
            OnPropertyChanged(name);
    }

    // ── Model & output: sampling only where the provider in use reads it ──

    /// <summary>Providers that send Temperature: the cloud APIs, every OpenAI-compatible service, Ollama and the local
    /// GGUF runners. The CLIs and BrainX ignore it.</summary>
    public static bool ReadsTemperature(AiProviderType t) => KindOf(t) is not (ProviderCardKind.Cli or ProviderCardKind.BrainX);
    /// <summary>Top P: Gemini, Ollama and the local GGUF runners only (OpenAI-style and Claude requests leave it out).</summary>
    public static bool ReadsTopP(AiProviderType t) => t is AiProviderType.Gemini or AiProviderType.Ollama
        or AiProviderType.LlamaServer or AiProviderType.Local;

    public bool ShowTemperature => ReadsTemperature(_providerManager.ActiveProviderType);
    public bool ShowTopP => ReadsTopP(_providerManager.ActiveProviderType);
    /// <summary>Said when the provider in use ignores one of the sliders (they come back when it is switched).</summary>
    public string SamplingNote => ShowTemperature && ShowTopP ? ""
        : string.Format(T(ShowTemperature ? "settings.samplingNoteTopP" : "settings.samplingNoteBoth"), ActiveProviderName);

    // ── Agent ▸ dispatch: the per-kind model fields fold away ──

    private bool _showDispatchAdvanced;
    /// <summary>The explore / code / review / hard-fix model boxes — opened by itself when one is filled in.</summary>
    public bool ShowDispatchAdvanced { get => _showDispatchAdvanced; set => SetProperty(ref _showDispatchAdvanced, value); }

    /// <summary>Called once from the constructor, after the page's values are loaded.</summary>
    private void InitLayout()
    {
        RefreshProviderGroupTitles();
        _localizationService.LanguageChanged += () =>
        {
            RefreshProviderGroupTitles();
            foreach (var name in new[] { nameof(ApiKeyLabel), nameof(ApiKeyHelp), nameof(BaseUrlLabel), nameof(BaseUrlHelp),
                         nameof(ProviderNote), nameof(SamplingNote) })
                OnPropertyChanged(name);
            RelocalizeChromeStatus();
        };
        _providerManager.OnProviderChanged += _ => App.Current?.Dispatcher.BeginInvoke(RaiseSamplingChanged);
        ShowDispatchAdvanced = new[] { ExploreModelOverride, CodeModelOverride, ReviewModelOverride, HardFixModelOverride }
            .Any(s => !string.IsNullOrWhiteSpace(s));
        RaiseCardLayoutChanged();
    }

    private void RaiseSamplingChanged()
    {
        OnPropertyChanged(nameof(ShowTemperature));
        OnPropertyChanged(nameof(ShowTopP));
        OnPropertyChanged(nameof(SamplingNote));
    }
}
