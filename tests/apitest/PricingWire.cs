// Prices and savings (owner, 2026-10-13: "ดูเรื่องการคำนวณค่าโทเค็นกับโมเดลที่ใช้ให้ถูกต้องที่สุด" + "กราฟว่าประหยัดได้เท่าไหร่").
// The table was checked against platform.claude.com/docs/en/about-claude/pricing that day and was wrong in four places:
// Sonnet 5.5 cache reads ($0.10, not $0.20 — the model CluadeX runs on, every cached step billed double), Opus 4.5
// ($5/$25, not $15/$75), Haiku 3.5 ("claude-3-5-haiku-…" fell through to Haiku 3's price), and Haiku 5.5's long-prompt
// tier (every token of a request whose prompt — cache reads and writes included — is over 100,000 at $0.50/$2.50).
// The output record of a streamed reply carries no prompt tokens, so it is priced with the prompt size passed along.
// Savings: baseline − actual must equal cache + tool search + microcompact + batch, to the last decimal.
using CluadeX.Services;

namespace ApiTest;

internal static class PricingWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · list prices (pricing page, 2026-10-13) and what the savings saved");

        var s55 = CostTrackingService.PricingOf("claude-sonnet-5-5");
        check("price", "Sonnet 5.5: $2 in, $10 out, cache reads $0.10 (0.05x), 5-minute writes $2.50",
            s55 == new ModelPricing(2m, 10m, 0.10m, 2.5m), s55.ToString());
        var s5 = CostTrackingService.PricingOf("claude-sonnet-5");
        check("price", "Sonnet 5 keeps 0.1x cache reads ($0.20) — the 0.05x is Sonnet 5.5's", s5.CacheReadPerMToken == 0.20m, s5.ToString());
        var o45 = CostTrackingService.PricingOf("claude-opus-4-5-20251101");
        check("price", "Opus 4.5: $5 / $25, cache reads $0.50 (it was billed as Opus 4.1's $15 / $75)",
            o45 == new ModelPricing(5m, 25m, 0.5m, 6.25m), o45.ToString());
        check("price", "Opus 4.1 stays $15 / $75", CostTrackingService.PricingOf("claude-opus-4-1").InputPerMToken == 15m, "");
        var h35 = CostTrackingService.PricingOf("claude-3-5-haiku-20241022");
        check("price", "Haiku 3.5 (claude-3-5-haiku-…): $0.80 / $4, not Haiku 3's $0.25", h35 == new ModelPricing(0.80m, 4m, 0.08m, 1m), h35.ToString());
        var o55 = CostTrackingService.PricingOf("claude-opus-5-5");
        check("price", "Opus 5.5: $4 / $20, cache reads $0.20, 5-minute writes $5", o55 == new ModelPricing(4m, 20m, 0.2m, 5m), o55.ToString());
        var f51 = CostTrackingService.PricingOf("claude-fable-5-1");
        check("price", "Fable 5.1: $10 / $50, cache reads $0.25", f51 == new ModelPricing(10m, 50m, 0.25m, 12.5m), f51.ToString());

        // Haiku 5.5's tier: the whole prompt decides, cache included; every token of that request pays the higher price.
        var hShort = CostTrackingService.PricingOf("claude-haiku-5-5", 100_000);
        var hLong = CostTrackingService.PricingOf("claude-haiku-5-5", 100_001);
        check("tier", "Haiku 5.5: a prompt of exactly 100,000 tokens is the low tier ($0.10 / $0.50)", hShort == new ModelPricing(0.10m, 0.50m, 0.01m, 0.125m), hShort.ToString());
        check("tier", "…100,001 is the high tier for every token ($0.50 / $2.50, reads $0.05, writes $0.625)",
            hLong == new ModelPricing(0.50m, 2.50m, 0.05m, 0.625m), hLong.ToString());
        var mostlyCached = CostTrackingService.Price("claude-haiku-5-5", 1_000, 0, cacheReadTokens: 120_000);
        check("tier", "…cache reads count toward the 100K: 1k fresh + 120k read is billed at the high tier",
            mostlyCached.Actual == 1_000m * 0.50m / 1_000_000m + 120_000m * 0.05m / 1_000_000m, mostlyCached.Actual.ToString());
        var outRecord = CostTrackingService.Price("claude-haiku-5-5", 0, 2_000, promptTokens: 121_000);
        check("tier", "…and the reply's output record (no prompt tokens of its own) is priced with the prompt size passed along",
            outRecord.Actual == 2_000m * 2.50m / 1_000_000m, outRecord.Actual.ToString());
        var outNoPrompt = CostTrackingService.Price("claude-haiku-5-5", 0, 2_000);
        check("tier", "negative control: without the prompt size the same output record is the low tier", outNoPrompt.Actual == 2_000m * 0.50m / 1_000_000m, outNoPrompt.Actual.ToString());

        // A typical agent step on Sonnet 5.5: 300 fresh, 150k read from the cache, 4k written for 5 minutes, 1k written
        // for an hour, 1,200 out; tool search kept 30k of definitions out, microcompact 12k of old results.
        var step = CostTrackingService.Price("claude-sonnet-5-5", 300, 1_200, cacheReadTokens: 150_000, cacheCreationTokens: 5_000,
            cacheWrite1hTokens: 1_000, toolSearchAvoidedTokens: 30_000, compactionAvoidedTokens: 12_000);
        decimal expectActual = (300m * 2m + 150_000m * 0.10m + 4_000m * 2.5m + 1_000m * 4m + 1_200m * 10m) / 1_000_000m;
        decimal expectBaseline = ((300m + 150_000m + 5_000m + 30_000m + 12_000m) * 2m + 1_200m * 10m) / 1_000_000m;
        check("savings", "actual = fresh + reads at $0.10 + 5-minute writes at $2.50 + 1-hour writes at 2x input + output",
            step.Actual == expectActual, $"{step.Actual} vs {expectActual}");
        check("savings", "baseline = every prompt token at the input price, plus what tool search and microcompact kept out, plus output",
            step.Baseline == expectBaseline, $"{step.Baseline} vs {expectBaseline}");
        check("savings", "the four parts add up to baseline − actual exactly",
            step.SavedByCache + step.SavedByToolSearch + step.SavedByCompaction + step.SavedByBatch == step.Baseline - step.Actual,
            $"{step.SavedByCache}+{step.SavedByToolSearch}+{step.SavedByCompaction}+{step.SavedByBatch} vs {step.Baseline - step.Actual}");
        check("savings", "tool search's share = 30k × input price; microcompact's = 12k × input price",
            step.SavedByToolSearch == 30_000m * 2m / 1_000_000m && step.SavedByCompaction == 12_000m * 2m / 1_000_000m, "");

        var batch = CostTrackingService.Price("claude-sonnet-5-5", 10_000, 1_000, priceFactor: 0.5m);
        check("savings", "batch: half the list price, the other half is the batch saving; the parts still add up",
            batch.Actual == (10_000m * 2m + 1_000m * 10m) / 1_000_000m / 2 && batch.SavedByBatch == batch.Actual
            && batch.SavedByCache + batch.SavedByToolSearch + batch.SavedByCompaction + batch.SavedByBatch == batch.Baseline - batch.Actual,
            $"{batch.Actual} saved {batch.SavedByBatch}");
        var write = CostTrackingService.Price("claude-sonnet-5-5", 0, 0, cacheCreationTokens: 20_000);
        check("savings", "a request that only writes the cache costs more than no cache: its cache saving is negative",
            write.SavedByCache < 0 && write.Baseline - write.Actual == write.SavedByCache, write.SavedByCache.ToString());

        // The recorder: the hint rides on the first record with prompt tokens, once.
        var tracker = new CostTrackingService();
        CostTrackingService.NextRequest = new CostTrackingService.RequestSavingsHint { CompactionAvoidedTokens = 10_000, ToolSearchAvoidedTokens = 5_000 };
        tracker.RecordUsage("claude-sonnet-5-5", 100, 0, 50_000, 0);
        tracker.RecordUsage("claude-sonnet-5-5", 0, 500, isNewRequest: false, promptTokens: 50_100);
        decimal oneActual = (100m * 2m + 50_000m * 0.10m + 500m * 10m) / 1_000_000m;
        decimal oneBaseline = ((100m + 50_000m + 15_000m) * 2m + 500m * 10m) / 1_000_000m;
        check("record", "a request's hint is counted once (on its input record) and the totals match Price",
            tracker.TotalCostUsd == oneActual && tracker.TotalBaselineUsd == oneBaseline, $"{tracker.TotalCostUsd}/{tracker.TotalBaselineUsd}");
        tracker.RecordUsage("claude-sonnet-5-5", 100, 0, 0, 0);
        check("record", "…a later request in the same flow without a new hint adds no avoided tokens",
            tracker.TotalBaselineUsd == oneBaseline + 100m * 2m / 1_000_000m, tracker.TotalBaselineUsd.ToString());
        CostTrackingService.NextRequest = null;
        return Task.CompletedTask;
    }
}
