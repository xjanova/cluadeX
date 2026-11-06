using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// xAI's Grok models: what the shared Chat Completions client (<see cref="CompatibleProvider"/>, preset
/// <see cref="AiProviderType.XAI"/>) must know beyond the wire format. Checked against docs.x.ai on 2026-11-06:
/// <list type="bullet">
/// <item>reasoning_effort "low" / "medium" / "high" / "xhigh" on grok-4.5 and later (default "high", reasoning cannot be
/// turned off); "xhigh" from grok-4.6 on — grok-4.5 treats it as "high".</item>
/// <item>A tool call streams whole, in one chunk, without an index (CompatibleProvider reads that shape).</item>
/// <item>presence_penalty, frequency_penalty and stop are errors on reasoning models — CluadeX never sends them.</item>
/// <item>Usage: reasoning tokens are reported apart and billed as output (see CompatibleProvider.ReadUsage).</item>
/// </list>
/// A model id this table does not know gets no reasoning_effort, and a 400 that names the field drops it for the session,
/// so a newer Grok costs at most one extra request, never a broken turn.
/// </summary>
public static class GrokModels
{
    /// <summary>Offline starting list, newest first; Test Connection replaces it with GET /v1/models.</summary>
    public static readonly string[] Fallback =
        ["grok-4.7", "grok-4.6", "grok-4.5", "grok-4.3", "grok-4.20-0309-reasoning", "grok-4.20-0309-non-reasoning", "grok-build-0.1"];

    public const string DefaultModel = "grok-4.7";

    /// <summary>The id without a proxy's vendor prefix ("x-ai/grok-4.7" through OpenRouter), lower case.</summary>
    private static string Bare(string? model)
    {
        string m = model?.Trim().ToLowerInvariant() ?? "";
        int slash = m.LastIndexOf('/');
        return slash >= 0 ? m[(slash + 1)..] : m;
    }

    // "grok-4.7", "grok-4.7-latest", "grok-4.7-0910", "grok-5" — but not "grok-4.20-…" read as a minor of 20.
    private static readonly Regex Version = new(@"^grok-(\d+)(?:\.(\d+))?(?:$|-)", RegexOptions.CultureInvariant);

    private static (int Major, int Minor)? VersionOf(string? model)
    {
        var m = Version.Match(Bare(model));
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0);
    }

    /// <summary>The highest reasoning_effort the model takes, or null when it takes none (grok-4.3, grok-4.20, grok-build).
    /// xAI numbers its 4.x line 4.1 … 4.3, 4.20, 4.5 …: a two-digit minor is an older dated line, not "after 4.9".</summary>
    public static string? MaxEffort(string? model) => VersionOf(model) switch
    {
        ({ } major, _) when major >= 5 => "xhigh",
        (4, 5) => "high",
        (4, >= 6 and <= 9) => "xhigh",
        _ => null,
    };

    /// <summary>The user's effort pick as this model takes it (rounded down to its highest), or null to leave the field out.
    /// "auto" sends nothing: xAI's own default is "high".</summary>
    public static string? EffortFor(string? model, string? pick)
    {
        if (MaxEffort(model) is not { } max) return null;
        string p = EffortLevels.Normalize(pick);
        if (p == EffortLevels.Auto) return null;
        return Array.IndexOf(EffortLevels.All, p) > Array.IndexOf(EffortLevels.All, max) ? max : p;
    }

    /// <summary>The context window docs.x.ai lists, or null for a model this table does not know (the caller's default).
    /// xAI's /v1/models does not report one.</summary>
    public static int? ContextWindow(string? model)
    {
        string m = Bare(model);
        if (m.StartsWith("grok-build")) return 256_000;
        if (m.StartsWith("grok-4.20") || m.StartsWith("grok-4.3")) return 1_000_000;
        return VersionOf(m) is (4, >= 5 and <= 9) ? 500_000 : null;
    }

    /// <summary>A prompt this long or longer bills every token of the request at the long-context price (2x, docs.x.ai).</summary>
    public const int LongContextPromptTokens = 200_000;

    /// <summary>Per-million prices from docs.x.ai/docs/models (2026-11-06), below <see cref="LongContextPromptTokens"/>;
    /// at or above it every column doubles. Null = not a Grok model this table knows.</summary>
    public static ModelPricing? Pricing(string? model, int promptTokens)
    {
        string m = Bare(model);
        ModelPricing? p =
            m.StartsWith("grok-build") ? new(1.00m, 2.00m, 0.20m, 0m)
            : m.StartsWith("grok-4.20") || m.StartsWith("grok-4.3") ? new(1.25m, 2.50m, 0.20m, 0m)
            : VersionOf(m) is (4, 5) ? new(2.00m, 6.00m, 0.30m, 0m)
            : VersionOf(m) is (4, 6 or 7) ? new(2.00m, 6.00m, 0.50m, 0m)
            : null;
        if (p == null || promptTokens < LongContextPromptTokens) return p;
        return new(p.InputPerMToken * 2m, p.OutputPerMToken * 2m, p.CacheReadPerMToken * 2m, p.CacheWritePerMToken * 2m);
    }
}
