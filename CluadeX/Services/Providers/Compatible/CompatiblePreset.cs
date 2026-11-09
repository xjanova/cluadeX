using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>How much of OpenAI's tool_choice a service takes.</summary>
public enum ToolChoiceSupport
{
    /// <summary>"auto", "none", "required" and a named function.</summary>
    Full,
    /// <summary>Only "auto" (and "none"): a forced pick goes out as "auto" — Z.ai, DeepInfra, DeepSeek in thinking mode.</summary>
    AutoOnly,
}

/// <summary>
/// One OpenAI-compatible Chat Completions service: where it lives, how it is signed in to, and the quirks the shared
/// client (<see cref="CompatibleProvider"/>) must respect. Facts checked against each service's docs on 2026-11-02;
/// anything a service turns out to reject anyway is learned at run time (CompatibleProvider drops or adapts the field
/// and retries), so a wrong guess here costs one extra request, not a broken turn.
/// </summary>
public sealed record CompatiblePreset
{
    public required AiProviderType Type { get; init; }
    public required string Name { get; init; }
    /// <summary>Appended with /chat/completions and /models. Empty = the user must type one (Azure, custom).</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>Shown in the empty Base URL box as an example (Azure's resource URL, a custom server).</summary>
    public string BaseUrlExample { get; init; } = "";
    public bool KeyRequired { get; init; } = true;
    /// <summary>Where a key is created (opened in the browser from Settings).</summary>
    public string KeysUrl { get; init; } = "";
    /// <summary>Where credit is added — named in the "out of credit" explanation.</summary>
    public string BillingUrl { get; init; } = "";
    /// <summary>Assets/Logos file, or null for a text badge.</summary>
    public string? Logo { get; init; }
    public string Badge { get; init; } = "";
    public string DefaultModel { get; init; } = "";
    /// <summary>Offline starting list; Test Connection replaces it with GET /models when the service has one.</summary>
    public string[] Models { get; init; } = [];
    public bool ListsModels { get; init; } = true;
    /// <summary>"Authorization" (Bearer) or Azure's "api-key".</summary>
    public string AuthHeader { get; init; } = "Authorization";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public ToolChoiceSupport ToolChoice { get; init; } = ToolChoiceSupport.Full;
    /// <summary>Send stream_options.include_usage (only where documented — usage is read from any chunk anyway).</summary>
    public bool StreamUsage { get; init; }
    /// <summary>Send the model's earlier reasoning back on its assistant turns (DeepSeek 400s without it once tools are
    /// in the request; Kimi, Together, Fireworks and Cerebras ask for the whole turn unchanged).</summary>
    public bool EchoReasoning { get; init; }
    /// <summary>Mistral: a tool call id must be exactly 9 letters/digits — ids from another provider are re-mapped.</summary>
    public bool ShortToolCallIds { get; init; }
    /// <summary>The service's hard max_tokens ceiling (DeepInfra 16384, Z.ai 131072); null = the setting goes out.</summary>
    public int? MaxOutputCap { get; init; }
    /// <summary>A server on this PC (LM Studio, vLLM): no key needed, plain http to loopback allowed, $0.</summary>
    public bool Local { get; init; }

    /// <summary>pack URI of the logo, or null for a text badge.</summary>
    public Uri? LogoUri => Logo == null ? null : new Uri($"pack://application:,,,/CluadeX;component/Assets/Logos/{Logo}");

    /// <summary>Localization prefix of this service's key guide: "{prefix}.steps" and "{prefix}.free".</summary>
    public string GuideKey => "agg." + Type.ToString().ToLowerInvariant();
}

/// <summary>Every OpenAI-compatible service CluadeX ships a preset for, in the order Settings lists them.</summary>
public static class CompatiblePresets
{
    // OpenRouter asks apps to identify themselves; X-OpenRouter-Title is the current name, X-Title the old one.
    private static readonly Dictionary<string, string> OpenRouterHeaders = new()
    {
        ["HTTP-Referer"] = XmanLicenseService.DefaultPurchaseUrl,   // the product page, not the source repository
        ["X-OpenRouter-Title"] = "CluadeX",
        ["X-Title"] = "CluadeX",
    };

    public static IReadOnlyList<CompatiblePreset> All { get; } = new CompatiblePreset[]
    {
        new()
        {
            Type = AiProviderType.OpenRouter, Name = "OpenRouter", Logo = "openrouter.png", Badge = "OR",
            BaseUrl = "https://openrouter.ai/api/v1",
            KeysUrl = "https://openrouter.ai/settings/keys", BillingUrl = "https://openrouter.ai/settings/credits",
            DefaultModel = "openrouter/free",
            Models = ["openrouter/free", "openrouter/auto", "openai/gpt-oss-120b"],
            Headers = OpenRouterHeaders, EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.Groq, Name = "Groq", Logo = "groq.png", Badge = "G",
            BaseUrl = "https://api.groq.com/openai/v1",
            KeysUrl = "https://console.groq.com/keys", BillingUrl = "https://console.groq.com/settings/billing",
            DefaultModel = "openai/gpt-oss-120b",
            Models = ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "qwen/qwen3.8-27b"],
        },
        new()
        {
            Type = AiProviderType.DeepSeek, Name = "DeepSeek", Logo = "deepseek.png", Badge = "DS",
            BaseUrl = "https://api.deepseek.com",
            KeysUrl = "https://platform.deepseek.com/api_keys", BillingUrl = "https://platform.deepseek.com/top_up",
            DefaultModel = "deepseek-v4-pro",
            Models = ["deepseek-v4-pro", "deepseek-flash"],
            // Thinking mode is on by default: a forced tool_choice is a 400 there, and with tools in the request every
            // earlier reasoning_content must come back.
            ToolChoice = ToolChoiceSupport.AutoOnly, StreamUsage = true, EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.Mistral, Name = "Mistral AI", Logo = "mistral.png", Badge = "M",
            BaseUrl = "https://api.mistral.ai/v1",
            KeysUrl = "https://console.mistral.ai/api-keys", BillingUrl = "https://console.mistral.ai/billing",
            DefaultModel = "mistral-large-4",
            Models = ["mistral-large-4", "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest"],
            ShortToolCallIds = true,
        },
        new()
        {
            // First-class beside the presets: its own wizard, prices, reasoning_effort and context windows (GrokModels).
            Type = AiProviderType.XAI, Name = "Grok (xAI API)", Logo = "xai.png", Badge = "xAI",
            BaseUrl = "https://api.x.ai/v1",
            KeysUrl = "https://console.x.ai", BillingUrl = "https://console.x.ai",
            DefaultModel = GrokModels.DefaultModel,
            Models = GrokModels.Fallback,
        },
        new()
        {
            Type = AiProviderType.Together, Name = "Together AI", Logo = "together.png", Badge = "T",
            BaseUrl = "https://api.together.ai/v1",
            KeysUrl = "https://api.together.ai/settings/api-keys", BillingUrl = "https://api.together.ai/settings/billing",
            DefaultModel = "zai-org/GLM-5.3",
            Models = ["zai-org/GLM-5.3", "deepseek-ai/DeepSeek-V4-Pro-0813", "deepseek-ai/DeepSeek-V4.1-Flash", "moonshotai/Kimi-K3",
                      "Qwen/Qwen3.7-Plus", "openai/gpt-oss-120b"],
            EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.Fireworks, Name = "Fireworks AI", Logo = "fireworks.png", Badge = "F",
            BaseUrl = "https://api.fireworks.ai/inference/v1",
            KeysUrl = "https://fireworks.ai/account/api-keys", BillingUrl = "https://fireworks.ai/account/billing",
            DefaultModel = "accounts/fireworks/models/glm-5p3",
            Models = ["accounts/fireworks/models/glm-5p3", "accounts/fireworks/models/deepseek-v4p1-flash"],
            StreamUsage = true, EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.Cerebras, Name = "Cerebras", Logo = "cerebras.png", Badge = "C",
            BaseUrl = "https://api.cerebras.ai/v1",
            KeysUrl = "https://cloud.cerebras.ai", BillingUrl = "https://cloud.cerebras.ai",
            DefaultModel = "gpt-oss-120b",
            Models = ["gpt-oss-120b", "qwen-3.8-27b", "kimi-k3", "zai-glm-5.3-flash"],
            EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.DeepInfra, Name = "DeepInfra", Logo = "deepinfra.png", Badge = "DI",
            BaseUrl = "https://api.deepinfra.com/v1/openai",
            KeysUrl = "https://deepinfra.com/dash/api_keys", BillingUrl = "https://deepinfra.com/dash/billing",
            DefaultModel = "deepseek-ai/DeepSeek-V4-Flash-0731",
            Models = ["deepseek-ai/DeepSeek-V4-Flash-0731", "moonshotai/Kimi-K3", "Qwen/Qwen3.8-27B"],
            ToolChoice = ToolChoiceSupport.AutoOnly, MaxOutputCap = 16384,
        },
        new()
        {
            Type = AiProviderType.HuggingFace, Name = "Hugging Face", Logo = "huggingface.png", Badge = "HF",
            BaseUrl = "https://router.huggingface.co/v1",
            KeysUrl = "https://huggingface.co/settings/tokens/new?ownUserPermissions=inference.serverless.write&tokenType=fineGrained",
            BillingUrl = "https://huggingface.co/settings/billing",
            DefaultModel = "openai/gpt-oss-120b",
            Models = ["openai/gpt-oss-120b", "zai-org/GLM-5.3", "deepseek-ai/DeepSeek-V4.1-Flash", "Qwen/Qwen3.8-27B"],
            StreamUsage = true,
        },
        new()
        {
            Type = AiProviderType.Novita, Name = "Novita AI", Logo = "novita.png", Badge = "N",
            BaseUrl = "https://api.novita.ai/openai/v1",
            KeysUrl = "https://novita.ai/settings/key-management", BillingUrl = "https://novita.ai/billing",
            DefaultModel = "deepseek/deepseek-v4.1-flash",
            Models = ["deepseek/deepseek-v4.1-flash", "meta-llama/llama-3.1-8b-instruct"],
        },
        new()
        {
            Type = AiProviderType.SambaNova, Name = "SambaNova", Logo = "sambanova.png", Badge = "S",
            BaseUrl = "https://api.sambanova.ai/v1",
            KeysUrl = "https://cloud.sambanova.ai/apis", BillingUrl = "https://cloud.sambanova.ai/plans",
            DefaultModel = "gpt-oss-120b",
            Models = ["gpt-oss-120b", "DeepSeek-V3.1", "Meta-Llama-3.3-70B-Instruct"],
        },
        new()
        {
            Type = AiProviderType.Nebius, Name = "Nebius Token Factory", Logo = "nebius.png", Badge = "NB",
            BaseUrl = "https://api.tokenfactory.nebius.com/v1",
            KeysUrl = "https://tokenfactory.nebius.com/project/api-keys", BillingUrl = "https://tokenfactory.nebius.com",
            DefaultModel = "openai/gpt-oss-120b",
            Models = ["openai/gpt-oss-120b"],
        },
        new()
        {
            Type = AiProviderType.Qwen, Name = "Qwen (Alibaba Model Studio)", Logo = "qwen.png", Badge = "Q",
            BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            KeysUrl = "https://modelstudio.console.alibabacloud.com/model/settings/api-key",
            BillingUrl = "https://modelstudio.console.alibabacloud.com",
            DefaultModel = "qwen-plus",
            Models = ["qwen-plus", "qwen-max", "qwen3.8-max", "qwen-flash"],
            StreamUsage = true,
        },
        new()
        {
            Type = AiProviderType.Moonshot, Name = "Moonshot (Kimi)", Logo = "kimi.png", Badge = "K",
            BaseUrl = "https://api.moonshot.ai/v1",
            KeysUrl = "https://platform.kimi.ai/console/api-keys", BillingUrl = "https://platform.kimi.ai/console",
            DefaultModel = "kimi-k3.1-pro",
            Models = ["kimi-k3.1-pro", "kimi-k3.1-pro-highspeed"],
            EchoReasoning = true,
        },
        new()
        {
            Type = AiProviderType.Zhipu, Name = "Z.ai (GLM)", Logo = "zai.png", Badge = "Z",
            BaseUrl = "https://api.z.ai/api/paas/v4",
            KeysUrl = "https://z.ai/manage-apikey/apikey-list", BillingUrl = "https://z.ai/manage-apikey/billing",
            DefaultModel = "glm-5.3",
            Models = ["glm-5.3", "glm-5.3-flash"],
            ToolChoice = ToolChoiceSupport.AutoOnly, MaxOutputCap = 131072,
        },
        new()
        {
            // The v1 API: the resource URL is typed in Settings, the model is the deployment name, and the key goes in
            // Azure's own api-key header.
            Type = AiProviderType.AzureOpenAI, Name = "Azure OpenAI", Logo = "azure.png", Badge = "Az",
            BaseUrlExample = "https://<resource>.openai.azure.com/openai/v1",
            KeysUrl = "https://portal.azure.com/#view/Microsoft_Azure_ProjectOxford/CognitiveServicesHub/~/OpenAI",
            BillingUrl = "https://portal.azure.com/#view/Microsoft_Azure_CostManagement/Menu/~/overview",
            AuthHeader = "api-key", ListsModels = false,
        },
        new()
        {
            Type = AiProviderType.LMStudio, Name = "LM Studio", Logo = "lmstudio.png", Badge = "LM",
            BaseUrl = "http://localhost:1234/v1", KeyRequired = false, Local = true,
            KeysUrl = "https://lmstudio.ai/download",
        },
        new()
        {
            Type = AiProviderType.Vllm, Name = "vLLM", Logo = "vllm.png", Badge = "v",
            BaseUrl = "http://localhost:8000/v1", KeyRequired = false, Local = true,
            KeysUrl = "https://docs.vllm.ai/en/latest/features/tool_calling/",
        },
        new()
        {
            // The engine the Plugins page installs and starts (ColibriService), which then writes the base URL and model
            // id `coli status` reports into this preset's config. Its port moves on when 8000 is taken (vLLM's default).
            Type = AiProviderType.Colibri, Name = "Colibri (local)", Logo = "colibri.png", Badge = "Co",
            BaseUrl = "http://localhost:8000/v1", KeyRequired = false, Local = true, StreamUsage = true,
            KeysUrl = "https://github.com/JustVugg/colibri",
        },
        new()
        {
            Type = AiProviderType.OpenAICompatible, Name = "OpenAI-compatible (custom)", Badge = "API",
            BaseUrlExample = "https://example.com/v1", KeyRequired = false,
        },
    };

    private static readonly Dictionary<AiProviderType, CompatiblePreset> ByType = All.ToDictionary(p => p.Type);

    public static CompatiblePreset? For(AiProviderType type) => ByType.GetValueOrDefault(type);

    public static bool IsCompatible(AiProviderType type) => ByType.ContainsKey(type);
}
