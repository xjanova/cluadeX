namespace CluadeX.Services.Billing;

/// <summary>How a provider is paid for — what its dollars on the Billing page mean.</summary>
public enum BillingKind
{
    /// <summary>Metered per token (Anthropic, OpenAI, Gemini API).</summary>
    Api,
    /// <summary>A CLI on the user's own plan: tokens and requests; any dollars are an estimate at API prices.</summary>
    Subscription,
    /// <summary>A model on this machine: $0, tokens and time.</summary>
    Local,
}

/// <summary>One provider tab: its name, logo (Assets/Logos, see ATTRIBUTION.md there), colour and which token counts
/// its usage reports. <see cref="Logo"/> null = a neutral text badge (no official mark bundled).</summary>
public sealed record ProviderBrand(string Key, string Name, string? Logo, string Badge, string Color, BillingKind Kind,
    bool ReportsCacheRead, bool ReportsCacheWrite, bool ReportsThinking)
{
    public const string AllKey = "All";

    /// <summary>Grok's colours on every chart (xAI's black mark would vanish on the dark themes): the API silver, the plan lime.</summary>
    public const string XaiColor = "#94A3B8", GrokDevColor = "#A3E635";

    /// <summary>Tab order. Keys are <see cref="Models.AiProviderType"/> names; "Other" catches the rest.</summary>
    public static IReadOnlyList<ProviderBrand> All { get; } =
    [
        // Anthropic reports cache reads and writes; extended thinking is billed inside output_tokens, never apart.
        new("Anthropic", "Anthropic API", "anthropic.png", "A", "#D97757", BillingKind.Api, true, true, false),
        // OpenAI: cached input, and reasoning tokens (output_tokens_details / completion_tokens_details).
        new("OpenAI", "OpenAI API", null, "OpenAI", "#10A37F", BillingKind.Api, true, false, true),
        // Gemini: cachedContentTokenCount, thoughtsTokenCount.
        new("Gemini", "Gemini API", "gemini.png", "G", "#3186FF", BillingKind.Api, true, false, true),
        new("ClaudeDev", "Claude Dev", "claude-code.png", "C", "#D97757", BillingKind.Subscription, true, true, false),
        new("OpenAIDev", "Codex (OpenAI Dev)", null, "Codex", "#10A37F", BillingKind.Subscription, true, false, false),
        new("GeminiDev", "Gemini Dev", "gemini.png", "G", "#3186FF", BillingKind.Subscription, true, false, false),
        new("AntigravityDev", "Antigravity", "antigravity.png", "A", "#3186FF", BillingKind.Subscription, true, false, false),
        // Grok Build: its result line's usage (DelegatedUsageEstimate) carries cache reads; thinking is not kept apart.
        new("GrokDev", "Grok Dev", "xai.png", "xAI", GrokDevColor, BillingKind.Subscription, true, false, false),
        new("BrainX", "BrainX", null, "BrainX", "#A16207", BillingKind.Subscription, false, false, false),
        new("Ollama", "Ollama", "ollama.png", "O", "#6B7280", BillingKind.Local, false, false, false),
        new("LlamaServer", "llama-server", "llamacpp.png", "L", "#FF8236", BillingKind.Local, false, false, false),
        new("Local", "Local GGUF", "llamacpp.png", "L", "#FF8236", BillingKind.Local, false, false, false),
        .. CompatibleBrands(),
        new("Other", "Other", null, "?", "#8B8B99", BillingKind.Api, true, true, true),
    ];

    /// <summary>The OpenAI-compatible services each get their own tab (OpenRouter's "anthropic/…" ids are OpenRouter's
    /// bill, not Anthropic's); a server on this PC (LM Studio, vLLM) is a local tab. Cached tokens are read from the
    /// chat-completions usage of every one of them; reasoning tokens are recorded apart only for xAI (Grok), so for the
    /// others that column says "not reported".</summary>
    private static IEnumerable<ProviderBrand> CompatibleBrands()
    {
        string[] palette = ["#6366F1", "#F55036", "#4D6BFE", "#FA520F", "#111827", "#0F6FFF", "#7C3AED", "#F97316",
            "#0EA5E9", "#14B8A6", "#EC4899", "#84CC16", "#A855F7", "#EAB308", "#22C55E", "#EF4444"];
        int i = 0;
        // xAI's own black would vanish on the dark themes' chart background: Grok keeps its slot, with a colour seen on both.
        string Next(string? own) { string c = own ?? palette[i % palette.Length]; i++; return c; }
        foreach (var p in Providers.CompatiblePresets.All)
            yield return new(p.Type.ToString(), p.Name, p.Logo, p.Badge is { Length: > 0 } b ? b : p.Name[..1],
                p.Local ? "#6B7280" : p.Type == Models.AiProviderType.XAI ? Next(XaiColor) : Next(null), p.Local ? BillingKind.Local : BillingKind.Api,
                // xAI reports reasoning tokens apart (completion_tokens_details), recorded as thinking.
                !p.Local, false, p.Type == Models.AiProviderType.XAI);
    }

    public static ProviderBrand For(string key) => All.FirstOrDefault(b => b.Key == key) ?? All[^1];

    public static BillingKind KindOf(string key) => For(key).Kind;

    /// <summary>The cost kind of a row from <paramref name="provider"/>.</summary>
    public static UsageCostKind CostKindOf(string provider) => KindOf(provider) switch
    {
        BillingKind.Subscription => UsageCostKind.Estimated,
        BillingKind.Local => UsageCostKind.Free,
        _ => UsageCostKind.Billed,
    };

    /// <summary>The tab of a CostTrackingService ledger key ("Google" was the Gemini API's name there).</summary>
    public static string FromLedgerKey(string key) => key switch
    {
        "Google" => "Gemini",
        CostTrackingService.UnattributedProvider => "Other",
        _ => All.Any(b => b.Key == key) ? key : "Other",
    };

    /// <summary>pack URI of the logo, or null for a text badge.</summary>
    public Uri? LogoUri => Logo == null ? null : new Uri($"pack://application:,,,/CluadeX;component/Assets/Logos/{Logo}");
}
