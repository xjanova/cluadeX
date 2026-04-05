namespace CluadeX.Services;

/// <summary>
/// Tracks API usage costs per session and per model.
/// Based on Claude Code's cost-tracker.ts pattern.
/// Tracks input/output tokens, cache read/creation, and USD cost.
/// </summary>
public class CostTrackingService
{
    private readonly object _lock = new();

    // Per-session totals
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public int TotalCacheReadTokens { get; private set; }
    public int TotalCacheCreationTokens { get; private set; }
    public int TotalRequests { get; private set; }
    public decimal TotalCostUsd { get; private set; }

    // Per-model tracking
    private readonly Dictionary<string, ModelUsage> _modelUsage = new();

    /// <summary>Fires when cost is updated.</summary>
    public event Action<CostSummary>? OnCostUpdated;

    /// <summary>Record usage from an API response.</summary>
    /// <param name="isNewRequest">True only for the first usage record of a request (avoids double-counting).</param>
    public void RecordUsage(string model, int inputTokens, int outputTokens,
        int cacheReadTokens = 0, int cacheCreationTokens = 0, bool isNewRequest = true)
    {
        lock (_lock)
        {
            TotalInputTokens += inputTokens;
            TotalOutputTokens += outputTokens;
            TotalCacheReadTokens += cacheReadTokens;
            TotalCacheCreationTokens += cacheCreationTokens;
            if (isNewRequest) TotalRequests++;

            // Calculate cost
            var pricing = GetPricing(model);
            decimal cost =
                (inputTokens * pricing.InputPerMToken / 1_000_000m) +
                (outputTokens * pricing.OutputPerMToken / 1_000_000m) +
                (cacheReadTokens * pricing.CacheReadPerMToken / 1_000_000m) +
                (cacheCreationTokens * pricing.CacheWritePerMToken / 1_000_000m);
            TotalCostUsd += cost;

            // Per-model tracking
            if (!_modelUsage.ContainsKey(model))
                _modelUsage[model] = new ModelUsage { Model = model };

            var mu = _modelUsage[model];
            mu.InputTokens += inputTokens;
            mu.OutputTokens += outputTokens;
            mu.CacheReadTokens += cacheReadTokens;
            mu.CacheCreationTokens += cacheCreationTokens;
            mu.Requests++;
            mu.CostUsd += cost;
        }

        OnCostUpdated?.Invoke(GetSummary());
    }

    /// <summary>Get current session cost summary.</summary>
    public CostSummary GetSummary()
    {
        lock (_lock)
        {
            return new CostSummary
            {
                TotalInputTokens = TotalInputTokens,
                TotalOutputTokens = TotalOutputTokens,
                TotalCacheReadTokens = TotalCacheReadTokens,
                TotalCacheCreationTokens = TotalCacheCreationTokens,
                TotalRequests = TotalRequests,
                TotalCostUsd = TotalCostUsd,
                ModelUsage = _modelUsage.Values.ToList(),
            };
        }
    }

    /// <summary>Reset session cost tracking.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            TotalInputTokens = 0;
            TotalOutputTokens = 0;
            TotalCacheReadTokens = 0;
            TotalCacheCreationTokens = 0;
            TotalRequests = 0;
            TotalCostUsd = 0;
            _modelUsage.Clear();
        }
        OnCostUpdated?.Invoke(GetSummary());
    }

    /// <summary>Format cost as human-readable string.</summary>
    public string FormatCost()
    {
        lock (_lock)
        {
            if (TotalRequests == 0) return "No API usage";
            return $"${TotalCostUsd:F4} ({TotalInputTokens:N0} in / {TotalOutputTokens:N0} out" +
                   (TotalCacheReadTokens > 0 ? $" / {TotalCacheReadTokens:N0} cache" : "") +
                   $" / {TotalRequests} requests)";
        }
    }

    /// <summary>Get per-model pricing (USD per million tokens).</summary>
    /// <remarks>
    /// Pricing updated April 2026. Based on Claude Code's utils/modelCost.ts.
    /// Format: (InputPerMToken, OutputPerMToken, CacheReadPerMToken, CacheWritePerMToken)
    /// Cache write = 1.25x input price; Cache read = 0.1x input price (Anthropic standard).
    /// </remarks>
    private static ModelPricing GetPricing(string model)
    {
        string m = model.ToLowerInvariant();
        return m switch
        {
            // ── Anthropic Models ──
            // Opus 4/4.5/4.6: $15 input, $75 output
            _ when m.Contains("opus") => new(15.0m, 75.0m, 1.5m, 18.75m),
            // Sonnet 4/4.5/4.6: $3 input, $15 output
            _ when m.Contains("sonnet") => new(3.0m, 15.0m, 0.3m, 3.75m),
            // Haiku 4.5: $1 input, $5 output
            _ when m.Contains("haiku-4") || m.Contains("haiku-3.5") => new(1.0m, 5.0m, 0.1m, 1.25m),
            // Haiku 3 (legacy): $0.25 input, $1.25 output
            _ when m.Contains("haiku") => new(0.25m, 1.25m, 0.025m, 0.3m),

            // ── OpenAI Models ──
            _ when m.Contains("gpt-4o-mini") => new(0.15m, 0.6m, 0m, 0m),
            _ when m.Contains("gpt-4o") => new(2.5m, 10.0m, 0m, 0m),
            _ when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3") => new(10.0m, 40.0m, 0m, 0m),
            _ when m.Contains("o1-pro") => new(150.0m, 600.0m, 0m, 0m),
            _ when m.Contains("o1") => new(15.0m, 60.0m, 0m, 0m),

            // ── Google Gemini Models ──
            _ when m.Contains("gemini-2.5-pro") => new(1.25m, 10.0m, 0m, 0m),
            _ when m.Contains("gemini-2.5-flash") => new(0.15m, 0.6m, 0m, 0m),
            _ when m.Contains("gemini-2.0") => new(0.10m, 0.40m, 0m, 0m),
            _ when m.Contains("gemma") => new(0m, 0m, 0m, 0m), // Open-weight, free via API

            // ── Local / Ollama (free) ──
            _ when m.Contains("ollama") || m.Contains("local") || m.Contains("gguf")
                => new(0m, 0m, 0m, 0m),

            // Default to Sonnet pricing for unknown models
            _ => new(3.0m, 15.0m, 0.3m, 3.75m),
        };
    }
}

public record ModelPricing(decimal InputPerMToken, decimal OutputPerMToken,
    decimal CacheReadPerMToken, decimal CacheWritePerMToken);

public class ModelUsage
{
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public int Requests { get; set; }
    public decimal CostUsd { get; set; }
}

public class CostSummary
{
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalCacheReadTokens { get; set; }
    public int TotalCacheCreationTokens { get; set; }
    public int TotalRequests { get; set; }
    public decimal TotalCostUsd { get; set; }
    public List<ModelUsage> ModelUsage { get; set; } = new();
}
