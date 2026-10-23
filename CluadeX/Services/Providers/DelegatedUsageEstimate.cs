using System.Text.Json;
using System.Text.Json.Nodes;

namespace CluadeX.Services.Providers;

/// <summary>Prefer final CLI usage/cost, otherwise a documented text-token lower-bound. No paid requests.</summary>
public sealed class DelegatedUsageEstimate
{
    public static readonly AsyncLocal<DelegatedUsageEstimate?> Current = new();
    public DelegatedUsageEstimate(string? model = null) { Model = model; }
    public string? Model { get; private set; }
    public decimal? ReportedEquivalentUsd { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }
    public bool HasReportedUsage => ReportedEquivalentUsd != null || InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens > 0;

    public void Feed(string line)
    {
        if (!line.Contains("\"result\"", StringComparison.Ordinal) && !line.Contains("\"system\"", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String && model.GetString() is { Length: > 0 } id && id is not ("codex" or "default" or "auto")) Model = id;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "result") return;
            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var value) && value >= 0) ReportedEquivalentUsd = value;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                int Number(string key) => usage.TryGetProperty(key, out var n) && n.TryGetInt32(out var i) ? Math.Max(0, i) : 0;
                InputTokens = Number("input_tokens"); OutputTokens = Number("output_tokens");
                CacheReadTokens = Number("cache_read_input_tokens"); CacheWriteTokens = Number("cache_creation_input_tokens");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
    }

    public (decimal EquivalentUsd, int Input, int Output, string Method) Estimate(string provider, string input, string output)
    {
        int i = InputTokens, o = OutputTokens;
        string method = "cli-tokens";
        if (!HasReportedUsage) { i = TextTokens(input); o = TextTokens(output); method = "text-lower-bound"; }
        string model = !string.IsNullOrWhiteSpace(Model) && Model is not ("default" or "auto" or "codex") ? Model : provider switch
        {
            "ClaudeDev" => AnthropicProvider.DefaultModel,
            "OpenAIDev" => OpenAiProvider.DefaultModel,
            _ => GeminiProvider.DefaultModel,
        };
        decimal equivalent = ReportedEquivalentUsd ?? CostTrackingService.Price(model, i, o, CacheReadTokens, CacheWriteTokens).Actual;
        if (ReportedEquivalentUsd != null) method = "cli-reported-equivalent";
        return (equivalent, i + CacheWriteTokens, o, method);
    }

    public static JsonObject? NormalizeUsage(JsonObject? source, bool inputIncludesCache = false)
    {
        if (source == null) return null;
        int Number(params string[] keys)
        {
            foreach (string key in keys) if (source[key] is JsonValue v && v.TryGetValue(out int n)) return Math.Max(0, n);
            return 0;
        }
        int cached = Number("cache_read_input_tokens", "cached_input_tokens", "cache_read_tokens", "cached");
        int input = Number("input_tokens", "input");
        return new JsonObject
        {
            ["input_tokens"] = inputIncludesCache ? Math.Max(0, input - cached) : input,
            ["output_tokens"] = Number("output_tokens", "output"),
            ["cache_read_input_tokens"] = cached,
            ["cache_creation_input_tokens"] = Number("cache_creation_input_tokens"),
        };
    }

    private static int TextTokens(string value)
    {
        long ascii = 0, other = 0;
        foreach (var c in value) if (c <= 127) ascii++; else other++;
        return (int)Math.Min(int.MaxValue, (ascii + 3) / 4 + other);
    }
}
