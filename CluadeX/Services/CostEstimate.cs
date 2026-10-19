namespace CluadeX.Services;

/// <summary>What one agent step should cost, before the run starts.</summary>
/// <param name="FirstStep">Step 1: the whole prefix is written to the prompt cache.</param>
/// <param name="LaterLow">An early later step (prefix read back from the cache, the step's new content written).</param>
/// <param name="LaterHigh">A step well into a long run (the conversation has grown).</param>
public sealed record StepCostEstimate(decimal FirstStep, decimal LaterLow, decimal LaterHigh,
    int PrefixTokens, int HistoryTokens, int OutputTokensPerStep, int GrowthTokensPerStep)
{
    /// <summary>Steps a budget covers: (fewest, most) — the long-run step price gives the fewest.</summary>
    public (int Fewest, int Most) StepsFor(decimal budgetUsd)
    {
        if (budgetUsd <= 0 || LaterHigh <= 0) return (0, 0);
        decimal left = budgetUsd - FirstStep;
        if (left <= 0) return (1, 1);
        return (1 + (int)(left / LaterHigh), 1 + (int)(left / Math.Max(LaterLow, 0.000001m)));
    }
}

/// <summary>
/// The cost arithmetic behind the chat strip's estimate: an agent run re-sends its prefix (tools + system prompt) and the
/// conversation on every step, so what a step costs is mostly how much of that is read back from the prompt cache versus
/// written to it, plus the answer. Prices come from <see cref="CostTrackingService.PricingOf"/> (list price per MTok).
/// </summary>
public static class CostEstimate
{
    /// <summary>Characters → tokens, roughly: code and English run ~3.5–4 chars a token, Thai ~2; 3.2 errs high.</summary>
    public static int TokensOf(long chars) => (int)Math.Ceiling(Math.Max(0, chars) / 3.2);

    /// <summary>Output a step typically produces at an effort (answer + thinking + tool input), before anything was measured.</summary>
    public static int DefaultOutputPerStep(string? effort, bool thinking) => (effort ?? "auto").ToLowerInvariant() switch
    {
        "low" => 600,
        "medium" => 1200,
        "high" => 2500,
        "xhigh" => 4000,
        "max" => 6000,
        _ => thinking ? 2500 : 1200,   // "auto": high while thinking is on, the model's default otherwise
    };

    /// <summary>New content a step adds to the conversation (tool call + result), before anything was measured.</summary>
    public const int DefaultGrowthPerStep = 3000;

    /// <param name="prefixTokens">Tools + system prompt: cached for an hour (written once at 2x input).</param>
    /// <param name="historyTokens">The chat so far plus the new message: written at the 5-minute price on step 1.</param>
    public static StepCostEstimate Estimate(string model, int prefixTokens, int historyTokens, int outputPerStep,
        int growthPerStep = DefaultGrowthPerStep, bool caching = true)
    {
        var p = CostTrackingService.PricingOf(model);
        const decimal M = 1_000_000m;
        decimal input = p.InputPerMToken / M, output = p.OutputPerMToken / M;
        // Providers without a cache-write premium (GPT-5.x and older, Gemini) bill a "write" as plain input; no cache price =
        // no discount. GPT-6 bills writes at 1.25x but has no 1-hour tier (its cache TTL is 30 minutes), so its prefix is a
        // plain write too — the 2x hour price is Claude's.
        decimal write5 = (p.CacheWritePerMToken > 0 ? p.CacheWritePerMToken : p.InputPerMToken) / M;
        bool hourTier = p.CacheWritePerMToken > 0 && !model.Contains("gpt-", StringComparison.OrdinalIgnoreCase);
        decimal write1h = hourTier ? input * 2 : write5;
        decimal read = (p.CacheReadPerMToken > 0 ? p.CacheReadPerMToken : p.InputPerMToken) / M;
        if (!caching) { write5 = write1h = read = input; }

        decimal first = prefixTokens * write1h + historyTokens * write5 + outputPerStep * output;
        decimal Later(int k) =>
            (prefixTokens + historyTokens + (long)k * (growthPerStep + outputPerStep)) * read   // everything before, read back
            + (growthPerStep + outputPerStep) * write5                                           // the last step's call + result
            + outputPerStep * output;                                                            // this step's answer
        return new StepCostEstimate(first, Later(1), Later(40), prefixTokens, historyTokens, outputPerStep, growthPerStep);
    }
}
