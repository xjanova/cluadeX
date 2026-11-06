using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.ModelPicker;

/// <summary>How a provider is reached — it decides what "connected" means and how a model is paid for.</summary>
public enum PickerProviderKind
{
    /// <summary>A vendor's API with the owner's key (Anthropic, OpenAI, Gemini).</summary>
    Api,
    /// <summary>A vendor's own CLI on the owner's plan (Claude Code, Codex, Gemini CLI, Antigravity).</summary>
    Cli,
    /// <summary>An OpenAI-compatible service (OpenRouter, xAI, DeepSeek, … or LM Studio on this PC).</summary>
    Compatible,
    /// <summary>Ollama on this PC.</summary>
    Ollama,
    /// <summary>A GGUF file on this PC (in-process LLamaSharp or llama-server).</summary>
    LocalGguf,
    /// <summary>The BrainX cowork room.</summary>
    Room,
}

/// <summary>How a model is paid for, for the price line of its card.</summary>
public enum PriceKind { PerToken, Free, Plan, Unknown, Room }

/// <summary>A model's list price per million tokens (null fields: not known).</summary>
public sealed record PriceFacts(PriceKind Kind, decimal? InputPerM = null, decimal? OutputPerM = null, decimal? CacheReadPerM = null);

/// <summary>
/// What the model picker says about a model — every fact read from the service that already owns it, never typed a
/// second time: the context window from <see cref="ContextMemoryService.ContextWindowFor"/>, prices from
/// <see cref="CostTrackingService"/>, thinking / effort from each provider's own request rules (the ones that decide
/// what a request sends), the girl from <see cref="ChatCharacter"/>. The only new facts are the "suits" lines: what
/// each family is for, in the owner's language (ModelPickerKeys).
/// </summary>
public static class ModelFacts
{
    public static PickerProviderKind KindOf(AiProviderType type) => type switch
    {
        AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini => PickerProviderKind.Api,
        AiProviderType.ClaudeDev or AiProviderType.OpenAIDev or AiProviderType.GeminiDev or AiProviderType.AntigravityDev => PickerProviderKind.Cli,
        AiProviderType.Ollama => PickerProviderKind.Ollama,
        AiProviderType.Local or AiProviderType.LlamaServer => PickerProviderKind.LocalGguf,
        AiProviderType.BrainX => PickerProviderKind.Room,
        _ => PickerProviderKind.Compatible,
    };

    /// <summary>The card a provider's models sit under: llama-server and in-process GGUF are one card ("this PC").</summary>
    public static AiProviderType GroupOf(AiProviderType type) => type == AiProviderType.LlamaServer ? AiProviderType.Local : type;

    /// <summary>Runs on this PC: free, and no Pro licence needed.</summary>
    public static bool IsOnThisPc(AiProviderType type) =>
        KindOf(type) is PickerProviderKind.Ollama or PickerProviderKind.LocalGguf || CompatiblePresets.For(type)?.Local == true;

    /// <summary>Is a Grok provider — the xAI preset today, or a provider of its own once one lands (matched by name so
    /// this file never has to change for it).</summary>
    public static bool IsGrok(AiProviderType type)
    {
        string n = type.ToString();
        return n.Contains("Grok", StringComparison.OrdinalIgnoreCase) || n.Equals("XAI", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The girl on a provider's card: <see cref="ChatCharacter.ForProvider"/>, and the Grok girl for Grok.</summary>
    public static ChatCharacter MascotOf(AiProviderType type) => IsGrok(type) ? ChatCharacter.Grok : ChatCharacter.ForProvider(type);

    /// <summary>Order of the provider cards: the big vendors (API then CLI), Grok, this PC, other services, the room.</summary>
    public static int RankOf(AiProviderType type) => type switch
    {
        AiProviderType.Anthropic => 0,
        AiProviderType.ClaudeDev => 1,
        AiProviderType.OpenAI => 2,
        AiProviderType.OpenAIDev => 3,
        AiProviderType.Gemini => 4,
        AiProviderType.GeminiDev => 5,
        AiProviderType.AntigravityDev => 6,
        _ when IsGrok(type) => 7,
        AiProviderType.Ollama => 8,
        AiProviderType.Local or AiProviderType.LlamaServer => 9,
        AiProviderType.BrainX => 99,
        _ => 20,
    };

    /// <summary>Context window in tokens — the same number the chat's context bar and auto-compaction use.</summary>
    public static int ContextOf(AiProviderType type, string model, uint localContextSize) =>
        ContextMemoryService.ContextWindowFor(type, model, localContextSize);

    /// <summary>"1.05M", "200K", "12K".</summary>
    public static string FormatTokens(int tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000.0).ToString(tokens % 1_000_000 == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000) + "K",
        _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The highest thinking effort a request for this model may carry ("max", "high", …), "on" for a model that
    /// thinks with no effort control, or null when the model does not think — the same rules the providers send by.</summary>
    public static string? ThinkingOf(AiProviderType type, string model)
    {
        string m = (model ?? "").Trim();
        switch (type)
        {
            case AiProviderType.Anthropic:
            case AiProviderType.ClaudeDev when !IsCliDefault(m):
            {
                string id = type == AiProviderType.ClaudeDev ? ClaudeAlias(m) : m;
                var t = AnthropicProvider.Traits(id);
                return t.EffortSupport switch { >= 2 => "max", 1 => "high", _ => t.Adaptive || t.AlwaysThinks ? "on" : null };
            }
            case AiProviderType.OpenAI:
                return OpenAiProvider.MaxEffort(m);
            case AiProviderType.OpenAIDev:
                return IsCliDefault(m) ? "max" : OpenAiProvider.MaxEffort(m) ?? "max";   // Codex takes low … max
            case AiProviderType.ClaudeDev:
                return "max";   // "default": the CLI's own pick, a current Claude
            case AiProviderType.Gemini:
            case AiProviderType.GeminiDev:
                return GeminiProvider.ThinkingOf(IsCliDefault(m) ? GeminiProvider.DefaultModel : m) switch
                {
                    GeminiProvider.ThinkingControl.Level => "high",
                    GeminiProvider.ThinkingControl.Budget => "on",
                    _ => null,
                };
            case AiProviderType.AntigravityDev:
                return "on";
            default:
                return null;
        }
    }

    private static bool IsCliDefault(string m) => m is "" or "default" or "auto";

    /// <summary>Claude Code's family aliases as the current API model, for the facts that are read from an id.</summary>
    private static string ClaudeAlias(string m) => m.ToLowerInvariant() switch
    {
        "opus" => AnthropicProvider.DefaultModel,
        "sonnet" => "claude-sonnet-5-5",
        "haiku" => "claude-haiku-5-5",
        "fable" => "claude-fable-5-1",
        _ => m,
    };

    /// <summary>The model's price: list prices for the API vendors, the price the service reported for an
    /// OpenAI-compatible one (else unknown — never the table's fallback), free on this PC, "your plan" for a CLI.</summary>
    public static PriceFacts PriceOf(AiProviderType type, string model)
    {
        string m = (model ?? "").Trim();
        switch (KindOf(type))
        {
            case PickerProviderKind.Api:
            {
                var p = CostTrackingService.PricingOf(m);
                return new(PriceKind.PerToken, p.InputPerMToken, p.OutputPerMToken, p.CacheReadPerMToken);
            }
            case PickerProviderKind.Cli: return new(PriceKind.Plan);
            case PickerProviderKind.Room: return new(PriceKind.Room);
            case PickerProviderKind.Ollama:
            case PickerProviderKind.LocalGguf: return new(PriceKind.Free);
            default:
            {
                if (CompatiblePresets.For(type)?.Local == true) return new(PriceKind.Free);
                if (!CostTrackingService.HasRegisteredPricing(m) && !CompatibleProvider.BuiltInPriced(m)) return new(PriceKind.Unknown);
                var p = CostTrackingService.PricingOf(m);
                return new(PriceKind.PerToken, p.InputPerMToken, p.OutputPerMToken, p.CacheReadPerMToken);
            }
        }
    }

    /// <summary>The plan line of a vendor CLI (the same words the old model box used).</summary>
    public static string? PlanKeyOf(AiProviderType type) => type switch
    {
        AiProviderType.ClaudeDev => "chat.model.claudeCodePlan",
        AiProviderType.GeminiDev => "chat.model.geminiCliPlan",
        AiProviderType.AntigravityDev => "chat.model.agyPlan",
        AiProviderType.OpenAIDev => "chat.model.codexPlan",
        _ => null,
    };

    /// <summary>"$1" / "$0.05" / "$12.5".</summary>
    /// <summary>What the first message after a switch pays to re-read a chat of <paramref name="tokens"/> on a model with
    /// <paramref name="price"/>: a new model has none of the old one's prompt cache, so the whole history goes at the full
    /// input price (later messages are cached again). Null when the price is not per token.</summary>
    public static decimal? RereadCost(int tokens, PriceFacts price) =>
        price.Kind == PriceKind.PerToken && price.InputPerM is { } i && tokens > 0 ? tokens * i / 1_000_000m : null;

    public static string Usd(decimal v) => "$" + v.ToString(v < 0.1m && v > 0 ? "0.0##" : "0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The localisation key of what a model is good for ("picker.suits.*"), from its family. A model this build does not
    /// know gets its provider's general line, never a guess at a family.
    /// </summary>
    public static string SuitsKeyOf(AiProviderType type, string model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        int slash = m.LastIndexOf('/');
        string bare = slash >= 0 ? m[(slash + 1)..] : m;
        switch (KindOf(type))
        {
            case PickerProviderKind.LocalGguf: return "picker.suits.local";
            case PickerProviderKind.Ollama: return "picker.suits.local";
            case PickerProviderKind.Room: return "picker.suits.room";
        }
        if (IsCliDefault(bare)) return "picker.suits.cliDefault";

        // Claude (API ids and Claude Code's aliases)
        if (Regex.IsMatch(bare, @"(^|-)(fable|mythos)(-|$)")) return "picker.suits.top";
        if (Regex.IsMatch(bare, @"(^|-)opus(-|$)")) return "picker.suits.deep";
        if (Regex.IsMatch(bare, @"(^|-)sonnet(-|$)")) return "picker.suits.balanced";
        if (Regex.IsMatch(bare, @"(^|-)haiku(-|$)")) return "picker.suits.fast";

        // OpenAI
        if (bare.StartsWith("gpt-") || Regex.IsMatch(bare, @"^o\d"))
        {
            if (bare.Contains("astra")) return "picker.suits.top";
            if (bare.Contains("terra")) return "picker.suits.everyday";
            if (bare.Contains("sol")) return "picker.suits.balanced";
            if (bare.Contains("luna") || bare.Contains("mini") || bare.Contains("nano")) return "picker.suits.volume";
            if (bare.StartsWith("gpt-oss")) return "picker.suits.open";
            if (bare.StartsWith("gpt-4")) return "picker.suits.legacy";
            return "picker.suits.balanced";
        }

        // Gemini / Gemma
        if (bare.StartsWith("gemma")) return "picker.suits.open";
        if (bare.StartsWith("gemini"))
        {
            if (bare.StartsWith("gemini-4")) return "picker.suits.top";
            if (bare.Contains("flash-lite")) return "picker.suits.volume";
            if (bare.Contains("flash")) return "picker.suits.fast";
            if (bare.Contains("pro")) return "picker.suits.deep";
        }

        if (bare.StartsWith("grok")) return bare.Contains("non-reasoning") ? "picker.suits.fast" : "picker.suits.balanced";
        if (Regex.IsMatch(bare, @"^(deepseek|glm|kimi|qwen|mistral|llama|gpt-oss)")) return "picker.suits.open";
        return "picker.suits.general";
    }
}
