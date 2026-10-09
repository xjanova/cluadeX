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
    /// Anthropic pricing updated October 2026 (first-party API rates).
    /// Format: (InputPerMToken, OutputPerMToken, CacheReadPerMToken, CacheWritePerMToken)
    /// Cache write = 1.25x input price; cache read = 0.1x input, except where a model's row says otherwise.
    /// Order matters: "opus-5-5" must be tested before "opus-5", "fable-5-1" before "fable".
    /// </remarks>
    private static ModelPricing GetPricing(string model)
    {
        string m = model.ToLowerInvariant();
        return m switch
        {
            // ── Anthropic Models ── (ModelPricing = input, output, cacheRead, cacheWrite per MTok)
            // Fable 5.1 / Mythos 5.1: $10 / $50, cache reads $0.25 (0.025x)
            _ when m.Contains("fable-5-1") || m.Contains("mythos-5-1") => new(10.0m, 50.0m, 0.25m, 12.5m),
            // Fable 5 / Mythos 5: $10 in / $50 out (most capable, above Opus tier)
            _ when m.Contains("fable") || m.Contains("mythos") => new(10.0m, 50.0m, 1.0m, 12.5m),
            // Opus 5.5: $4 / $20, cache reads $0.20 (0.05x)
            _ when m.Contains("opus-5-5") => new(4.0m, 20.0m, 0.2m, 5.0m),
            // Opus 5 / 4.8 / 4.7 / 4.6: $5 in / $25 out (1M ctx, no long-context premium) — NOT the old $15/$75
            _ when m.Contains("opus-5") || m.Contains("opus-4-8") || m.Contains("opus-4-7") || m.Contains("opus-4-6")
                => new(5.0m, 25.0m, 0.5m, 6.25m),
            // Opus 4 / 4.1 / 4.5 (legacy): $15 in / $75 out
            _ when m.Contains("opus") => new(15.0m, 75.0m, 1.5m, 18.75m),
            // Sonnet 5.5 / 5: $2 / $10, cache reads $0.20
            _ when m.Contains("sonnet-5") => new(2.0m, 10.0m, 0.2m, 2.5m),
            // Sonnet 4/4.5/4.6: $3 input, $15 output
            _ when m.Contains("sonnet") => new(3.0m, 15.0m, 0.3m, 3.75m),
            // Haiku 5.5: $0.10 / $0.50 (prompts up to 100K tokens; $0.50 / $2.50 beyond — not modelled)
            _ when m.Contains("haiku-5") => new(0.10m, 0.50m, 0.01m, 0.125m),
            // Haiku 4.5: $1 input, $5 output
            _ when m.Contains("haiku-4") || m.Contains("haiku-3.5") => new(1.0m, 5.0m, 0.1m, 1.25m),
            // Haiku 3 (legacy): $0.25 input, $1.25 output
            _ when m.Contains("haiku") => new(0.25m, 1.25m, 0.025m, 0.3m),

            // ── OpenAI Models ── (October 2026; cached input in the cacheRead column)
            _ when m.Contains("gpt-6-astra") => new(10.0m, 50.0m, 1.0m, 0m),
            _ when m.Contains("gpt-6.1-sol") => new(2.0m, 10.0m, 0.10m, 0m),
            _ when m.Contains("gpt-6-sol") => new(2.0m, 10.0m, 0.20m, 0m),
            _ when m.Contains("gpt-6-luna") => new(0.10m, 0.50m, 0.01m, 0m),
            _ when m.Contains("gpt-5.6-sol") => new(4.0m, 20.0m, 0.40m, 0m),
            _ when m.Contains("gpt-5.6-terra") => new(2.0m, 12.0m, 0.20m, 0m),
            _ when m.Contains("gpt-5.6-luna") => new(0.20m, 1.20m, 0.02m, 0m),
            _ when m.Contains("gpt-5.5") => new(5.0m, 30.0m, 0.50m, 0m),
            _ when m.Contains("gpt-5.4-mini") => new(0.75m, 4.50m, 0.075m, 0m),
            _ when m.Contains("gpt-4.1") => new(2.0m, 8.0m, 0.50m, 0m),
            _ when m.Contains("gpt-5-nano") => new(0.05m, 0.40m, 0m, 0m),
            _ when m.Contains("gpt-5-mini") => new(0.25m, 2.0m, 0m, 0m),
            _ when m.Contains("gpt-5") => new(1.25m, 10.0m, 0m, 0m),
            _ when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o4") => new(10.0m, 40.0m, 0m, 0m),
            _ when m.Contains("o3-pro") => new(20.0m, 80.0m, 0m, 0m),
            _ when m.Contains("gpt-4o-mini") => new(0.15m, 0.6m, 0m, 0m),
            _ when m.Contains("gpt-4o") => new(2.5m, 10.0m, 0m, 0m),
            _ when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3") => new(10.0m, 40.0m, 0m, 0m),
            _ when m.Contains("o1-pro") => new(150.0m, 600.0m, 0m, 0m),
            _ when m.Contains("o1") => new(15.0m, 60.0m, 0m, 0m),

            // ── Google Gemini Models ── (paid tier, October 2026; 3.6–3.8 Flash promo price until 2026-12-31)
            _ when m.Contains("gemini-3.8-flash") || m.Contains("gemini-3.7-flash") || m.Contains("gemini-3.6-flash")
                => new(0.75m, 3.75m, 0.075m, 0m),
            _ when m.Contains("gemini-3.5-flash-lite") => new(0.30m, 2.50m, 0.03m, 0m),
            _ when m.Contains("gemini-3.5-flash") => new(1.50m, 9.0m, 0.15m, 0m),
            _ when m.Contains("gemini-3.1-flash-lite") => new(0.25m, 1.50m, 0.025m, 0m),
            _ when m.Contains("gemini-3.1-pro") => new(2.0m, 12.0m, 0.20m, 0m),
            _ when m.Contains("gemini-2.5-flash-lite") => new(0.10m, 0.40m, 0m, 0m),
            _ when m.Contains("gemini-3-pro") => new(2.0m, 12.0m, 0m, 0m),
            _ when m.Contains("gemini-3-flash") => new(0.30m, 2.5m, 0m, 0m),
            _ when m.Contains("gemini-2.5-pro") => new(1.25m, 10.0m, 0m, 0m),
            _ when m.Contains("gemini-2.5-flash") => new(0.30m, 2.50m, 0m, 0m),
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
