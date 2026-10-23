using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>Package fees and usage-equivalent estimates remain distinct from metered API charges.</summary>
public static class ProviderCostProjection
{
    public static bool IsPackage(string key) => key is "ClaudeDev" or "OpenAIDev" or "GeminiDev" or "AntigravityDev" or "BrainX";

    public static IReadOnlyList<CostTrackingService.UsageDay> Project(string key, IReadOnlyList<CostTrackingService.UsageDay> days, ProviderSpendSettings? billing)
    {
        if (!IsPackage(key)) return days;
        return days.Select(d =>
        {
            // With no entered fee, the CLI's measured usage is a price-equivalent proxy, explicitly labelled in the UI.
            decimal estimate = billing?.MonthlyPackageUsd is { } fee && billing.PackageStartDate is { } start && d.Day >= start.Date
                ? fee / DateTime.DaysInMonth(d.Day.Year, d.Day.Month)
                : d.BaselineUsd;
            return d with { Usd = estimate, BaselineUsd = Math.Max(d.BaselineUsd, estimate) };
        }).ToList();
    }

    /// <summary>The committed package fee for a calendar month, not the accrued daily allocation shown in the chart.</summary>
    public static decimal? MonthlyFee(ProviderSpendSettings? billing, DateTime month, DateTime through)
        => billing?.MonthlyPackageUsd is { } fee && billing.PackageStartDate is { } start && start.Date <= through.Date
            && start.Date <= month.AddMonths(1).AddDays(-1).Date ? fee : null;
}
