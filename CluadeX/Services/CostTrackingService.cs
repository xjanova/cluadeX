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
    private static ModelPricing GetPricing(string model)
    {
        // Pricing as of 2025 (approximate)
        return model switch
        {
            var m when m.Contains("opus") => new(15.0m, 75.0m, 1.5m, 18.75m),
            var m when m.Contains("sonnet") => new(3.0m, 15.0m, 0.3m, 3.75m),
            var m when m.Contains("haiku") => new(0.25m, 1.25m, 0.025m, 0.3m),
            var m when m.Contains("gpt-4o-mini") => new(0.15m, 0.6m, 0m, 0m),
            var m when m.Contains("gpt-4o") => new(2.5m, 10.0m, 0m, 0m),
            var m when m.Contains("o1") => new(15.0m, 60.0m, 0m, 0m),
            var m when m.Contains("o3") => new(10.0m, 40.0m, 0m, 0m),
            var m when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            var m when m.Contains("gemini-2.5-pro") => new(1.25m, 10.0m, 0m, 0m),
            var m when m.Contains("gemini-2.5-flash") => new(0.15m, 0.6m, 0m, 0m),
            _ => new(3.0m, 15.0m, 0.3m, 3.75m), // Default to Sonnet pricing
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
