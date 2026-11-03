using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using CluadeX.Services;
using CluadeX.Services.Billing;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.Billing;
using Microsoft.Extensions.DependencyInjection;

namespace BillingTest;

/// <summary>
/// The old spend &amp; savings window, merged into the Billing page (owner: "merge then remove"). The same guarantees its
/// chattest checks (U8) proved, now against <see cref="BillingSavingsViewModel"/> / <see cref="BillingAccountViewModel"/>
/// and the page itself, on a seeded day ledger in this harness's portable data root:
///   · totals once per provider (older unattributed spend included exactly once), inclusive custom dates, refused dates;
///   · chart points finite and inside the plot, one-day plot centred, long ranges bucketed without dropping spend;
///   · savings by cache / tool search / compaction / batch from the ledger's Saved* parts;
///   · monthly totals (API vs package fee vs baseline), package fee allocation, a proxy when no fee is entered;
///   · per-provider credit independent, Clear asks first, TryParseUsd cases, the Billing tabs driving both sections;
///   · rendered on the page in three themes with no binding errors, scrollable, and opened from the title bar's menu.
/// </summary>
internal static partial class Program
{
    sealed class BindingErrors : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    static string L(string key) => LocalizedResources.Instance[key];

    static string LedgerPath(SettingsService settings) => Path.Combine(settings.DataRoot, "usage-daily.json");

    static async Task SavingsSection(SettingsService settings)
    {
        Log("\n## Savings & comparison (the old spend window, now on the Billing page)");
        string ledger = LedgerPath(settings);
        string? ledgerBefore = File.Exists(ledger) ? File.ReadAllText(ledger) : null;
        var savedBilling = settings.Settings.ProviderSpending;
        var legacyBefore = (settings.Settings.CreditBalanceUsd, settings.Settings.CreditBalanceSetAt, settings.Settings.CreditBalanceSpentThatDay);
        string language = Sp.GetRequiredService<LocalizationService>().CurrentLanguage;
        try
        {
            settings.UpdateSettings(s => s.ProviderSpending = new());
            SavingsFourteenDays(settings);
            SavingsFilters(settings, out var tracker, out var vm);
            AccountChecks(settings, tracker, vm);
            await TabsDriveSections(settings, tracker);
            await SavingsOnThePage(settings, tracker);
        }
        finally
        {
            Sp.GetRequiredService<LocalizationService>().SetLanguage(language);
            settings.UpdateSettings(s =>
            {
                s.ProviderSpending = savedBilling;
                (s.CreditBalanceUsd, s.CreditBalanceSetAt, s.CreditBalanceSpentThatDay) = legacyBefore;
            });
            if (ledgerBefore != null) File.WriteAllText(ledger, ledgerBefore); else File.Delete(ledger);
        }
    }

    // ── U8 (was TestUsageWindow): 14 seeded days, paid vs without savings, where the savings came from ──

    static void SavingsFourteenDays(SettingsService settings)
    {
        var days = new Dictionary<string, DailyUsage>();
        var rnd = new Random(7);
        for (int i = 13; i >= 0; i--)
        {
            decimal paid = i % 5 == 3 ? 0 : Math.Round(0.5m + (decimal)rnd.NextDouble() * 6m, 2);
            decimal cache = paid * 2.4m, ts = paid * 0.6m, comp = paid * 0.3m, batch = i % 4 == 0 ? paid * 0.2m : 0;
            days[DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new DailyUsage
            {
                Usd = paid, Requests = paid == 0 ? 0 : 20 + i * 3, BaselineUsd = paid + cache + ts + comp + batch,
                SavedCacheUsd = cache, SavedToolSearchUsd = ts, SavedCompactionUsd = comp, SavedBatchUsd = batch,
            };
        }
        File.WriteAllText(LedgerPath(settings), JsonSerializer.Serialize(days));
        var tracker = new CostTrackingService(settings);
        settings.UpdateSettings(s => { s.CreditBalanceUsd = 50m; s.CreditBalanceSetAt = DateTime.Today.AddDays(-6).AddHours(9); s.CreditBalanceSpentThatDay = 0.25m; });

        var vm = new BillingSavingsViewModel(tracker, settings);
        vm.SetVisible(true);   // as on screen: every change redraws at once
        vm.SelectedRange = vm.Ranges.First(r => r.Days == 14);
        Check("SV1 three summary cards, 14 daily bars, four saving rows", vm.Summary.Count == 3 && vm.DailyBars.Count == 14 && vm.Savings.Count == 4,
            $"{vm.Summary.Count}/{vm.DailyBars.Count}/{vm.Savings.Count}");
        var ledgerDays = tracker.LastDays(14);
        bool barsMatch = vm.DailyBars.Select((b, i) => Math.Abs(b.Total - (double)ledgerDays[i].BaselineUsd) < 1e-9).All(x => x);
        Check("SV1 each bar = paid + saved = the ledger's \"without savings\" for that day; a day with no use has no bar",
            barsMatch && vm.DailyBars.Any(b => b.Parts.Count == 0), string.Join(",", vm.DailyBars.Select(b => b.Total.ToString("0.##", CultureInfo.InvariantCulture))));
        Check("SV1 summary: paid, without savings and saved are the ledger's sums", vm.Summary[0].Amount == UsdText.Format(ledgerDays.Sum(d => d.Usd))
            && vm.Summary[1].Amount == UsdText.Format(ledgerDays.Sum(d => d.BaselineUsd))
            && vm.Summary[2].Amount == UsdText.Format(ledgerDays.Sum(d => d.BaselineUsd) - ledgerDays.Sum(d => d.Usd)) && vm.Summary[2].IsSaving,
            string.Join(" | ", vm.Summary.Select(s => s.Amount)));
        Check("SV1 where the savings came from: cache, tool search, compaction, batch from the ledger's parts",
            vm.Savings[0].Amount == UsdText.Format(ledgerDays.Sum(d => d.SavedCacheUsd)) && vm.Savings[1].Amount == UsdText.Format(ledgerDays.Sum(d => d.SavedToolSearchUsd))
            && vm.Savings[2].Amount == UsdText.Format(ledgerDays.Sum(d => d.SavedCompactionUsd)) && vm.Savings[3].Amount == UsdText.Format(ledgerDays.Sum(d => d.SavedBatchUsd))
            && vm.Savings[0].Fraction == 1 && vm.Savings.All(s => s.Fraction is >= 0 and <= 1), string.Join(" | ", vm.Savings.Select(s => s.Amount)));
        vm.ShowSavings = false;
        Check("SV1 \"compare savings\" off: the bars are the paid part only", vm.DailyBars.Select((b, i) => Math.Abs(b.Total - (double)ledgerDays[i].Usd) < 1e-9).All(x => x));
        vm.SetVisible(false);

        var account = new BillingAccountViewModel(tracker, settings) { Confirm = _ => true };
        Check("SV2 old shared credit is preserved without silently assigning it to a provider", !account.HasBalance && account.HasLegacyCredit
            && settings.Settings.CreditBalanceUsd == 50m && account.LegacyCreditText.Contains("$50.00"));
        Check("SV2 credit input: '$12.50', '12,50' and '25' parse; 'abc' and '-3' don't",
            BillingViewModel.TryParseUsd("$12.50", out var a1) && a1 == 12.5m && BillingViewModel.TryParseUsd("12,50", out var a2) && a2 == 12.5m
            && BillingViewModel.TryParseUsd("25", out var a3) && a3 == 25m && !BillingViewModel.TryParseUsd("abc", out _) && !BillingViewModel.TryParseUsd("-3", out _));
        account.BalanceInput = "abc";
        account.SetBalanceCommand.Execute(null);
        bool refused = account.BalanceError.Length > 0 && settings.Settings.ProviderSpending?.ContainsKey("Anthropic") != true;
        account.BalanceInput = "20";
        account.SetBalanceCommand.Execute(null);
        Check("SV2 credit: a bad entry is refused; '20' anchors only this provider and leaves legacy credit intact",
            refused && settings.Settings.ProviderSpending!["Anthropic"].CreditBalanceUsd == 20m && settings.Settings.CreditBalanceUsd == 50m
            && account.BalanceError.Length == 0 && account.BalanceText.Contains("$20.00"), account.BalanceText);
        account.SetBalanceCommand.Execute(null);   // the second click of a double-click: the box is already empty
        Check("SV2 a double-click on Set saves once and shows no error", account.BalanceError.Length == 0 && settings.Settings.ProviderSpending!["Anthropic"].CreditBalanceUsd == 20m);
        settings.UpdateSettings(s => s.ProviderSpending = new());
    }

    // ── U8 filters (was TestUsageFilters): providers, dates, no double counting, the plot ──

    static void SavingsFilters(SettingsService settings, out CostTrackingService tracker, out BillingSavingsViewModel vm)
    {
        var seed = new Dictionary<string, DailyUsage>();
        for (int i = 0; i < 120; i++)
        {
            var parts = new Dictionary<string, DailyUsage>
            {
                ["Anthropic"] = new() { Usd = 1 + i % 4, BaselineUsd = 5 + i % 4, Requests = 2, InputTokens = 1000, SavedCacheUsd = 4 },
                ["OpenAI"] = new() { Usd = 0.5m + i % 3, BaselineUsd = 1 + i % 3, Requests = 1, InputTokens = 500, SavedCacheUsd = 0.5m },
                ["Google"] = new() { Usd = 0.25m + i % 2, BaselineUsd = 0.5m + i % 2, Requests = 1, InputTokens = 250, SavedCacheUsd = 0.25m },
                ["CustomCloud"] = new() { Usd = 0.1m, BaselineUsd = 0.1m, Requests = 1 },
            };
            var day = new DailyUsage
            {
                ByProvider = parts, Usd = parts.Values.Sum(p => p.Usd), BaselineUsd = parts.Values.Sum(p => p.BaselineUsd),
                Requests = 5, InputTokens = 1750, SavedCacheUsd = 4.75m,
            };
            if (i == 2) { day.Usd += 3; day.BaselineUsd += 3; day.Requests++; }   // mixed older and attributed data
            if (i == 110) day = new DailyUsage { Usd = 7, Requests = 1 };       // before provider/baseline tracking
            seed[DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = day;
        }
        File.WriteAllText(LedgerPath(settings), JsonSerializer.Serialize(seed));
        tracker = new CostTrackingService(settings);
        var from = DateTime.Today.AddDays(-3); var to = DateTime.Today.AddDays(-1);
        var queried = tracker.Between(from, to);
        Check("SF1 ledger range: both endpoints included, oldest first, missing dates zero-filled", queried.Count == 3 && queried[0].Day == from && queried[^1].Day == to
            && tracker.Between(DateTime.Today.AddYears(-2), DateTime.Today.AddYears(-2)).Single().Usd == 0);
        Check("SF1 provider discovery includes custom and legacy rows", tracker.RecordedProviders.Contains("CustomCloud") && tracker.RecordedProviders.Contains(CostTrackingService.UnattributedProvider));
        var t = tracker;
        Check("SF1 mixed history: attributed shares plus residual equal the total exactly", t.RecordedProviders.Sum(p => t.Between(from, to, p).Sum(d => d.Usd)) == queried.Sum(d => d.Usd)
            && t.Between(from, to, CostTrackingService.UnattributedProvider).Sum(d => d.Usd) == 3m);

        vm = new BillingSavingsViewModel(tracker, settings);
        vm.SetVisible(true);   // as on screen: every change redraws at once
        var v = vm;
        Check("SF2 chips: all 11 routes offered, each line in its own colour", vm.Providers.Count >= 11
            && vm.Providers.Select(p => p.Color.ToString()).Distinct().Count() == vm.Providers.Count, string.Join(",", vm.Providers.Select(p => p.Key + "=" + p.Color)));
        Check("SF2 chips use the Billing tabs' names (Gemini API for the ledger's \"Google\") and logos", vm.Providers.Single(p => p.Key == "Google").Label == "Gemini API"
            && vm.Providers.Single(p => p.Key == "Anthropic").HasLogo && vm.Providers.Single(p => p.Key == "CustomCloud").Label == "CustomCloud"
            && vm.Providers.Single(p => p.Key == CostTrackingService.UnattributedProvider).Label == L("usage.unattributed"));
        Check("SF2 an unmetered route with no use has no invented zero-cost series", !vm.Providers.Single(p => p.Key == "ClaudeDev").HasData
            && vm.Providers.Single(p => p.Key == "ClaudeDev").TotalText != "$0.00" && vm.ProviderLines.Count == 5, vm.ProviderLines.Count.ToString());
        decimal expected = tracker.LastDays(30).Sum(d => d.Usd);
        Check("SF3 totals: every provider's spend, older unattributed spend included exactly once (30 days)", vm.Summary[0].Amount == UsdText.Format(expected)
            && vm.Providers.Sum(p => p.Total) == expected && vm.DailyBars.Count == 30, vm.Summary[0].Amount + " vs " + UsdText.Format(expected));
        var colours = vm.Providers.ToDictionary(p => p.Key, p => p.Color.ToString());
        vm.SelectNoneCommand.Execute(null);
        Check("SF4 empty selection: no series, zero totals, no auto-reset to all", !vm.HasUsage && vm.ProviderLines.Count == 0 && vm.Summary[0].Amount == "$0.00"
            && vm.Providers.All(p => !p.IsSelected));
        vm.Providers.Single(p => p.Key == "OpenAI").IsSelected = true;
        Check("SF4 one provider: the lines and the totals both follow the selection", vm.ProviderLines.Count == 1 && vm.ProviderLines[0].Label == "OpenAI API"
            && vm.Summary[0].Amount == UsdText.Format(tracker.LastDays(30, "OpenAI").Sum(d => d.Usd)));
        vm.StartDate = from; vm.EndDate = to; vm.ApplyDatesCommand.Execute(null);
        Check("SF5 custom dates: chart and totals use the inclusive range", vm.DailyBars.Count == 3 && vm.SelectedRange?.Key == "custom"
            && vm.Summary[0].Amount == UsdText.Format(tracker.Between(from, to, "OpenAI").Sum(d => d.Usd)) && vm.From == from && vm.To == to);
        string kept = vm.RangeLabel;
        vm.StartDate = DateTime.Today; vm.EndDate = DateTime.Today.AddDays(-1); vm.ApplyDatesCommand.Execute(null);
        Check("SF5 start after end: a message, the previous chart kept", vm.RangeError.Length > 0 && vm.RangeLabel == kept && vm.DailyBars.Count == 3);
        vm.StartDate = null; vm.ApplyDatesCommand.Execute(null);
        Check("SF5 an empty date is refused safely", vm.RangeError.Length > 0);
        vm.StartDate = from; vm.EndDate = DateTime.Today.AddDays(1); vm.ApplyDatesCommand.Execute(null);
        Check("SF5 a future date is refused safely", vm.RangeError.Length > 0);
        vm.SelectedRange = vm.Ranges.First(r => r.Key == "all");
        vm.SelectAllCommand.Execute(null);
        Check("SF6 all history: at most 90 points, no spend dropped", vm.RangeError.Length == 0 && vm.DailyBars.Count <= 90 && vm.ProviderLines.All(p => p.Points.Count <= 90)
            && vm.Summary[0].Amount == UsdText.Format(seed.Values.Sum(d => d.Usd)) && vm.BucketText != L("usage.dailyBuckets"), vm.Summary[0].Amount);
        Check("SF6 filters keep provider colours stable", v.Providers.All(p => colours[p.Key] == p.Color.ToString()));
        vm.IsCumulative = false; vm.ShowSavings = false; vm.UpdateChartWidth(260);
        Check("SF7 daily lines: finite points stay inside the resized plot", vm.ProviderLines.SelectMany(p => p.Points)
            .All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= 260 && p.Y >= 0 && p.Y <= BillingSavingsViewModel.LineHeight));
        vm.IsCumulative = true; vm.ShowSavings = true;
        Check("SF7 cumulative with savings: total, without-savings and every provider line inside the plot", new[] { vm.PaidLine, vm.BaseLine }
            .Concat(vm.ProviderLines.Select(p => p.Points)).SelectMany(p => p)
            .All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= 260 && p.Y >= 0 && p.Y <= BillingSavingsViewModel.LineHeight));
        vm.SelectedRange = vm.Ranges.First(r => r.Days == 1);
        Check("SF7 one-day plot: the point centred, no division by zero", vm.DailyBars.Count == 1 && vm.ProviderLines.All(p => p.Points.Count == 1 && Math.Abs(p.Points[0].X - 130) < 0.01));
        vm.SelectedRange = vm.Ranges.Single(r => r.Key == "previousMonth");
        var previous = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        Check("SF8 previous-month preset matches both boundaries", vm.StartDate == previous && vm.EndDate == previous.AddMonths(1).AddDays(-1)
            && vm.MonthlyTotals.Count == 1 && vm.MonthlyTotals[0].Month == previous);
        tracker.RecordRun("single", 0.40m, 30, true, DateTime.Today.AddDays(-2));
        tracker.RecordRun("single", 0.20m, 90, false, DateTime.Today.AddDays(-2));
        tracker.RecordRun("fan-out", 1.00m, 60, true, DateTime.Today.AddDays(-40));
        vm.SelectedRange = vm.Ranges.First(r => r.Days == 7);
        Check("SF9 strategies: only runs inside the dates, with runs, success share and cost per run", vm.HasStrategies && vm.Strategies.Count == 1
            && vm.Strategies[0].Runs == "2" && vm.Strategies[0].Success == "50%" && vm.Strategies[0].CostPerRun == "$0.30" && vm.Strategies[0].Total == "$0.60",
            string.Join(" | ", vm.Strategies.Select(s => $"{s.Name} {s.Runs} {s.Success} {s.CostPerRun} {s.Total}")));
        vm.SelectedRange = vm.Ranges.First(r => r.Days == 30);
    }

    // ── Credit & plan price (was TestProviderBilling) ──

    static void AccountChecks(SettingsService settings, CostTrackingService tracker, BillingSavingsViewModel vm)
    {
        var account = new BillingAccountViewModel(tracker, settings);
        var asked = new List<string>();
        bool answer = true;
        account.Confirm = q => { asked.Add(q); return answer; };
        decimal originalApi = tracker.TodayCostUsd;
        tracker.RecordPackageUsage("ClaudeDev", 40m, 10000, 1000, "cli-reported-equivalent");
        Check("SA1 package use is never billed as API nor counted against the API spend guard", tracker.TodayCostUsd == originalApi
            && tracker.TotalCostUsd == 0 && tracker.Between(DateTime.Today, DateTime.Today, "ClaudeDev").Single().BaselineUsd == 40m);
        account.Refresh();
        account.SelectedProvider = account.Providers.Single(p => p.Key == "Anthropic");
        account.BalanceInput = "100"; account.SetBalanceCommand.Execute(null);
        account.SelectedProvider = account.Providers.Single(p => p.Key == "OpenAI");
        account.BalanceInput = "50"; account.SetBalanceCommand.Execute(null);
        decimal anthropic = CostTrackingService.Price("claude-opus-5-5", 1000000, 0).Actual;
        decimal openai = CostTrackingService.Price("gpt-6.1-sol", 10000, 0).Actual;
        tracker.RecordUsage("claude-opus-5-5", 1000000, 0);
        tracker.RecordUsage("gpt-6.1-sol", 10000, 0);
        account.Refresh();
        Check("SA2 a credit counts down only its own provider's API spend since it was entered",
            account.BalanceText.Contains(UsdText.Format(50m - openai)) && settings.Settings.ProviderSpending!["Anthropic"].CreditBalanceUsd == 100m, account.BalanceText);
        account.SelectedProvider = account.Providers.Single(p => p.Key == "Anthropic");
        Check("SA2 switching account shows its own remaining credit", account.BalanceText.Contains(UsdText.Format(100m - anthropic)), account.BalanceText);
        answer = false;
        account.ClearBalanceCommand.Execute(null);
        Check("SA3 Clear asks first; \"No\" keeps the credit", asked.Count == 1 && asked[0].Contains("Anthropic API") && account.HasBalance
            && settings.Settings.ProviderSpending!["Anthropic"].CreditBalanceUsd == 100m);
        answer = true;
        account.ClearBalanceCommand.Execute(null);
        Check("SA3 \"Yes\" clears that account only, the other keeps its credit", !account.HasBalance && asked.Count == 2
            && settings.Settings.ProviderSpending!["Anthropic"].CreditBalanceUsd == null && settings.Settings.ProviderSpending["OpenAI"].CreditBalanceUsd == 50m);
        account.SelectedProvider = account.Providers.Single(p => p.Key == "ClaudeDev");
        Check("SA4 a plan CLI shows the plan-price editor, not a credit", account.IsPackageBilling && !account.IsApiBilling);
        account.PackagePriceInput = "20"; account.PackageStartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        account.SavePackageCommand.Execute(null);
        var reloaded = new SettingsService();
        Check("SA4 credit and plan price survive a settings reload", reloaded.Settings.ProviderSpending["ClaudeDev"].MonthlyPackageUsd == 20m
            && reloaded.Settings.ProviderSpending["OpenAI"].CreditBalanceUsd == 50m);
        vm.SelectedRange = vm.Ranges.First(r => r.Key == "month");
        vm.SelectAllCommand.Execute(null);
        var month = vm.MonthlyTotals.Single(m => m.Month.Year == DateTime.Today.Year && m.Month.Month == DateTime.Today.Month);
        Check("SA5 monthly total counts the full plan fee once, beside API spend", month.PackageUsd == 20m
            && month.ApiUsd == vm.Providers.Where(p => !ProviderCostProjection.IsPackage(p.Key)).Sum(p => tracker.Between(month.Month, DateTime.Today, p.Key).Sum(d => d.Usd))
            && !month.MissingUsage, $"pkg {month.PackageUsd} api {month.ApiUsd}");
        Check("SA5 monthly net savings = full-price work − (API + plan fee)", month.BaselineUsd - month.ApiUsd - month.PackageUsd >= 20m
            && month.Saved.Contains(UsdText.Format(month.BaselineUsd - month.ApiUsd - month.PackageUsd)));
        decimal dailyFee = 20m / DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
        Check("SA5 the chart allocates the plan fee by day, never adding the API-equivalent a second time",
            ProviderCostProjection.Project("ClaudeDev", tracker.Between(DateTime.Today, DateTime.Today, "ClaudeDev"), settings.Settings.ProviderSpending!["ClaudeDev"]).Single().Usd == dailyFee);
        account.PackagePriceInput = "abc"; account.SavePackageCommand.Execute(null);
        Check("SA6 a bad plan price is refused, the saved one kept", account.BalanceError.Length > 0 && settings.Settings.ProviderSpending!["ClaudeDev"].MonthlyPackageUsd == 20m);
        account.PackagePriceInput = "20"; account.PackageStartDate = DateTime.Today.AddDays(1); account.SavePackageCommand.Execute(null);
        Check("SA6 a start date after today is refused", account.BalanceError.Length > 0 && settings.Settings.ProviderSpending!["ClaudeDev"].PackageStartDate == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        account.PackageStartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); account.SavePackageCommand.Execute(null);
        Check("SA6 saving the same price again changes nothing and shows no error", account.BalanceError.Length == 0 && account.HasPackage);
        tracker.RecordPackageUsage("OpenAIDev", 2m, 100, 100, "cli-tokens");
        vm.Refresh();
        Check("SA7 a plan with no price entered is shown as an API-equivalent proxy (≈)", vm.MonthlyTotals.Any(m => m.HasProxy)
            && vm.Providers.Single(p => p.Key == "OpenAIDev").TotalText.StartsWith("≈"));
        var negative = new BillingSavingsViewModel.MonthlyTotal(DateTime.Today, 2, 20, 10, false, false);
        Check("SA7 monthly net savings can be negative, never clamped to zero", negative.Saved.Contains("-12.00") && !negative.IsSaving);
        var estimator = new CluadeX.Services.Providers.DelegatedUsageEstimate();
        estimator.Feed("""{"type":"result","total_cost_usd":4.5,"usage":{"input_tokens":100,"output_tokens":20}}""");
        Check("SA8 a plan's estimate prefers the CLI-reported equivalent, never calls a model", estimator.Estimate("ClaudeDev", "", "").EquivalentUsd == 4.5m);
        var codex = new CluadeX.Services.Providers.CodexStreamTranslator(@"C:\fixture");
        var estimate = new CluadeX.Services.Providers.DelegatedUsageEstimate("gpt-6.1-sol");
        foreach (var line in codex.Translate("""{"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":50}}""")) estimate.Feed(line);
        Check("SA8 Codex usage keeps cached tokens and prices them once", estimate.InputTokens == 200 && estimate.CacheReadTokens == 800
            && estimate.Estimate("OpenAIDev", "", "").EquivalentUsd == CostTrackingService.Price("gpt-6.1-sol", 200, 50, 800).Actual);
        var fallback = new CluadeX.Services.Providers.DelegatedUsageEstimate().Estimate("GeminiDev", "test", "answer");
        Check("SA8 the text fallback is labelled a lower-bound estimate", fallback.Method == "text-lower-bound" && fallback.Input > 0 && fallback.Output > 0);
        Check("SA9 monthly history survives new usage beyond the old 90-day retention", new CostTrackingService(settings).FirstUsageDay <= DateTime.Today.AddDays(-119));
        Check("SA9 USD entries tell thousands from decimal commas", UsdText.TryParse("1,000", out var thousand) && thousand == 1000m
            && UsdText.TryParse("1,234.56", out var grouped) && grouped == 1234.56m && !UsdText.TryParse("1,2,3", out _));
        UsdEntryCases();
        Check("SA9 tiny API costs stay nonzero on screen", BillingViewModel.Usd(0.0000001m) == "$0.0000001" && BillingViewModel.Usd(12.5m) == "$12.50");
        vm.SetVisible(false);
    }

    // ── The Billing tabs drive both sections; the header range carries over; budgets read a saved credit ──

    static async Task TabsDriveSections(SettingsService settings, CostTrackingService tracker)
    {
        // Usage-log rows so the page has its provider tabs (the day ledger already has the comparison's numbers).
        foreach (var r in Fixture()) tracker.Log.Add(r);
        var vm = new BillingViewModel(tracker, settings, null) { Clock = () => Now, Zone = Bangkok };
        vm.Account.Confirm = _ => true;
        await vm.RefreshAsync();
        vm.Savings.Refresh();
        Check("ST1 All tab: the provider chips choose, the credit editor has a picker", vm.Savings.ShowProviderFilter && vm.Account.CanPick && vm.Account.HasEditor);
        vm.Savings.Providers.Single(p => p.Key == "Anthropic").IsSelected = false;
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "OpenAI");
        vm.Savings.Refresh();
        Check("ST2 OpenAI tab: only OpenAI is compared, no chips, the editor is OpenAI's credit", !vm.Savings.ShowProviderFilter && vm.Savings.FilterNote.Contains("OpenAI API")
            && vm.Savings.ProviderLines.Count == 1 && vm.Savings.ProviderLines[0].Label == "OpenAI API" && !vm.Account.CanPick
            && vm.Account.SelectedProvider.Key == "OpenAI" && vm.Account.IsApiBilling,
            string.Join(",", vm.Savings.ProviderLines.Select(p => p.Label)) + " · " + vm.Account.SelectedProvider.Key);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Gemini");
        Check("ST2 Gemini tab: the editor is the ledger's \"Google\" account", vm.Account.SelectedProvider.Key == "Google" && vm.Account.Title.Contains("Gemini API"));
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "ClaudeDev");
        Check("ST2 Claude Dev tab: the plan-price editor", vm.Account.SelectedProvider.Key == "ClaudeDev" && vm.Account.IsPackageBilling);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Ollama");
        Check("ST2 a local tab has nothing to enter", !vm.Account.HasEditor);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "All");
        vm.Savings.Refresh();
        Check("ST3 back on All: the chips are as the user left them", vm.Savings.ShowProviderFilter && !vm.Savings.Providers.Single(p => p.Key == "Anthropic").IsSelected
            && vm.Savings.ProviderLines.All(p => p.Label != "Anthropic API"));
        vm.RangeDays = 7;
        await vm.RefreshAsync();
        Check("ST4 the header's 7 days carry over to the comparison", vm.Savings.SelectedRange?.Days == 7 && vm.Savings.From == DateTime.Today.AddDays(-6));
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Anthropic");
        vm.Account.BalanceInput = "7";
        await vm.RefreshAsync(force: true);   // new usage arrives while the owner is typing: the page refreshes
        Check("ST5 a refresh of the page keeps what is being typed into the credit box", vm.Account.BalanceInput == "7" && vm.Account.SelectedProvider.Key == "Anthropic");
        vm.Account.BalanceInput = "75";
        vm.Account.SetBalanceCommand.Execute(null);
        string creditTitle = string.Format(L("billing.budget.credit"), "Anthropic API");
        Check("ST5 a credit saved on the page shows in Budgets at once", vm.Budgets.Any(b => b.Title == creditTitle && b.HasMeter), string.Join(" | ", vm.Budgets.Select(b => b.Title)));
        settings.UpdateSettings(s => s.ProviderSpending!["OpenRouter"] = new() { CreditBalanceUsd = 9m, CreditSetAt = DateTime.Now });
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "All");
        Check("ST5 …and a compatible service's credit (OpenRouter) is listed too", vm.Budgets.Any(b => b.Title == string.Format(L("billing.budget.credit"), "OpenRouter")),
            string.Join(" | ", vm.Budgets.Select(b => b.Title)));
        settings.UpdateSettings(s => s.ProviderSpending!.Remove("OpenRouter"));

        // Hidden: new spend only marks the comparison stale.
        var hidden = new BillingSavingsViewModel(tracker, settings);
        hidden.SetVisible(true);
        var before = hidden.Summary.FirstOrDefault()?.Amount;
        hidden.SetVisible(false);
        tracker.RecordUsage("claude-opus-5-5", 200000, 0);
        hidden.FollowTab("All");
        bool stillOld = hidden.Summary.FirstOrDefault()?.Amount == before;
        hidden.SetVisible(true);
        Check("ST6 hidden: new spend does not redraw; shown again, it does", stillOld && hidden.Summary[0].Amount != before, $"{before} → {hidden.Summary[0].Amount}");
        hidden.SetVisible(false);

        // The page lives as long as the app. Left open over midnight (simulated: the dates were worked out a day ago),
        // "last 7 days" moves on to end today — today's spend is not left out — while typed dates stay as they are.
        var overnight = new BillingSavingsViewModel(tracker, settings);
        overnight.SelectedRange = overnight.Ranges.First(r => r.Days == 7);
        FieldInfo F(string name) => typeof(BillingSavingsViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        F("_from").SetValue(overnight, DateTime.Today.AddDays(-7));
        F("_to").SetValue(overnight, DateTime.Today.AddDays(-1));
        F("_appliedOn").SetValue(overnight, DateTime.Today.AddDays(-1));
        overnight.SetVisible(true);
        Check("ST7 over midnight a preset follows the calendar (shown again: last 7 days end today)", overnight.To == DateTime.Today
            && overnight.From == DateTime.Today.AddDays(-6) && overnight.DailyBars.Count == 7 && overnight.DailyBars[^1].Day == DateTime.Today,
            $"{overnight.From:d} – {overnight.To:d}");
        overnight.StartDate = DateTime.Today.AddDays(-5); overnight.EndDate = DateTime.Today.AddDays(-3); overnight.ApplyDatesCommand.Execute(null);
        F("_appliedOn").SetValue(overnight, DateTime.Today.AddDays(-1));
        overnight.Refresh();
        Check("ST7 …and dates the user chose stay as they are", overnight.SelectedRange?.Key == "custom" && overnight.From == DateTime.Today.AddDays(-5)
            && overnight.To == DateTime.Today.AddDays(-3), $"{overnight.From:d} – {overnight.To:d}");
        overnight.SetVisible(false);
    }

    // ── The empty state (no usage yet, in the real MainWindow): a credit can still be entered, through the panel ──

    static async Task EmptyStateEditor(BillingView view, MainViewModel main)
    {
        var settings = Sp.GetRequiredService<SettingsService>();
        var before = settings.Settings.ProviderSpending;
        try
        {
            settings.UpdateSettings(s => s.ProviderSpending = new());
            var vm = main.BillingVM!;
            vm.Account.Refresh();
            await Idle(100);
            var panel = Find<AccountPanel>(view).SingleOrDefault(p => p.IsVisible);
            Check("N4b the empty state still offers the credit / plan editor (the old spend window did, before any use)", vm.IsEmpty && panel != null
                && ((FrameworkElement)panel.FindName("AccountPicker")).IsVisible, $"empty {vm.IsEmpty}, visible editors {Find<AccountPanel>(view).Count(p => p.IsVisible)}");
            if (panel == null) return;
            vm.Account.SelectedProvider = vm.Account.Providers.Single(p => p.Key == "OpenAI");
            await Idle(50);
            var box = (TextBox)panel.FindName("BalanceBox");
            box.Text = "42";
            var set = Find<Button>((FrameworkElement)panel.FindName("CreditEditor")).First(b => ReferenceEquals(b.Command, vm.Account.SetBalanceCommand));
            set.Command.Execute(null);
            await Idle(50);
            Check("N4b …typed and saved there, the credit is kept for that account", settings.Settings.ProviderSpending?.GetValueOrDefault("OpenAI")?.CreditBalanceUsd == 42m
                && vm.Account.HasBalance && box.Text == "", vm.Account.BalanceText);
        }
        finally { settings.UpdateSettings(s => s.ProviderSpending = before); main.BillingVM?.Account.Refresh(); }
    }

    // ── On the page: three themes, no binding errors, live controls, scroll ──

    static async Task SavingsOnThePage(SettingsService settings, CostTrackingService tracker)
    {
        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var themes = Sp.GetRequiredService<ThemeService>();
        var loc = Sp.GetRequiredService<LocalizationService>();
        loc.SetLanguage("th");
        var vm = new BillingViewModel(tracker, settings, null);
        vm.Account.Confirm = _ => true;
        var view = new BillingView { DataContext = vm };
        var win = new Window
        {
            Content = view, Width = 1280, Height = 900, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        try
        {
            // The probe: a binding to nothing must be heard, or "no binding errors" would prove nothing.
            var probe = new TextBlock();
            probe.SetBinding(TextBlock.TextProperty, new Binding("NoSuchPropertyAnywhere"));
            probe.DataContext = vm;
            await Idle(50);
            Check("SP0 the binding-error listener hears a broken binding (probe)", errors.Lines.Any(l => l.Contains("NoSuchPropertyAnywhere")), errors.Lines.Count.ToString());
            lock (errors.Lines) errors.Lines.Clear();

            // The menu's first open: the request comes before the page has its numbers (the cards above the section grow
            // when they arrive) — it must wait for them, or the section ends up pushed below the screen.
            vm.ShowSavings();
            Check("SP1a a request made before the page has its numbers waits for them", !vm.TakeSavingsRequest() && vm.Snapshot == null);
            vm.ShowSavings();
            win.Show();
            await vm.RefreshAsync();
            await Idle(250);
            var panel = Find<SavingsPanel>(view).Single();
            var pageScroll = Find<ScrollViewer>(view).Where(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).OrderByDescending(s => s.ExtentHeight).First();
            double firstTop = panel.TranslatePoint(new Point(0, 0), pageScroll).Y;
            Check("SP1a …then the page opens with the savings section's top at the top of the screen", pageScroll.VerticalOffset > 0 && Math.Abs(firstTop) < 2,
                $"offset {pageScroll.VerticalOffset:0}, section at {firstTop:0}");
            pageScroll.ScrollToTop();
            await Idle(50);
            // Two editors on the page: the Budgets card's, and the empty state's (only one of them is ever shown).
            var accountPanels = Find<AccountPanel>(view).ToList();
            var accountPanel = accountPanels.Single(p => p.IsVisible);
            Check("SP1 the page carries the savings section and the credit / plan editor (in the Budgets card)", panel.IsVisible && accountPanel.IsVisible
                && ReferenceEquals(panel.DataContext, vm.Savings) && ReferenceEquals(accountPanel.DataContext, vm.Account) && accountPanels.Count == 2
                && Ancestor<Border>(accountPanel) is { } card && Find<ItemsControl>(card).Any(i => ReferenceEquals(i.ItemsSource, vm.Budgets)));
            var plot = (FrameworkElement)panel.FindName("PlotArea");
            Check("SP2 the comparison lines are laid out to the plot's real width", plot.ActualWidth > 200 && vm.Savings.ProviderLines.Count > 0
                && vm.Savings.ProviderLines.SelectMany(p => p.Points).All(p => p.X >= 0 && p.X <= plot.ActualWidth + 1 && p.Y >= 0 && p.Y <= plot.ActualHeight + 1),
                $"plot {plot.ActualWidth:0}x{plot.ActualHeight:0}, {vm.Savings.ProviderLines.Count} lines");
            var daily = (SpendChart)panel.FindName("DailyChart");
            Check("SP2 the daily bars are drawn by the page's own chart", daily.ActualHeight > 100 && daily.Bars?.Count == vm.Savings.DailyBars.Count && daily.Bars.Count > 0);
            var picker = (ComboBox)accountPanel.FindName("AccountPicker");
            Check("SP3 the account picker shows the provider's name, not a class name", picker.IsVisible
                && Find<TextBlock>(picker).Any(t => t.Text == vm.Account.SelectedProvider.Label), vm.Account.SelectedProvider.Label);

            var checkbox = Find<CheckBox>(panel).Single(c => c.DataContext is BillingSavingsViewModel.ProviderChoice { Key: "Anthropic" });
            checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Idle();
            Check("SP4 live chip: unticking hides that provider's line", !vm.Savings.Providers.Single(p => p.Key == "Anthropic").IsSelected
                && vm.Savings.ProviderLines.All(p => p.Label != "Anthropic API") && vm.Savings.ProviderLines.Count > 0);
            vm.Savings.SelectAllCommand.Execute(null);
            ((DatePicker)panel.FindName("FromPicker")).SelectedDate = DateTime.Today.AddDays(-2);
            ((DatePicker)panel.FindName("ToPicker")).SelectedDate = DateTime.Today;
            ((Button)panel.FindName("ApplyRangeButton")).Command.Execute(null);
            await Idle();
            Check("SP4 live dates: the pickers + Apply redraw the chosen days", vm.Savings.DailyBars.Count == 3 && daily.Bars?.Count == 3);
            vm.Savings.SelectedRange = vm.Savings.Ranges.First(r => r.Days == 30);

            var scroll = Find<ScrollViewer>(view).Where(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).OrderByDescending(s => s.ExtentHeight).First();
            foreach (var (width, height, theme) in new[] { (1280d, 900d, ThemeIds.Midnight), (900d, 700d, ThemeIds.Classic), (760d, 620d, ThemeIds.NeonCyber), (1280d, 900d, ThemeIds.Lavender) })
            {
                themes.Apply(theme, persist: false);
                win.Width = width; win.Height = height;
                scroll.ScrollToTop();
                await Idle(150);
                double plotRight = plot.TranslatePoint(new Point(plot.ActualWidth, 0), scroll).X;
                Check($"SP5 {width}x{height} {theme}: the page scrolls, the plot fits the page's width, lines inside the plot", scroll.ScrollableHeight > 0
                    && plotRight <= scroll.ViewportWidth + 1 && vm.Savings.ProviderLines.SelectMany(p => p.Points).All(p => p.X <= plot.ActualWidth + 1),
                    $"plot right {plotRight:0} / viewport {scroll.ViewportWidth:0}");
                panel.BringIntoView();
                await Idle(80);
                int colours = SavePng(view, $"{theme.ToLowerInvariant()}/billing-savings-{width:0}.png");
                ((FrameworkElement)panel.FindName("MonthlyTotalsCard")).BringIntoView();
                await Idle(80);
                SavePng(view, $"{theme.ToLowerInvariant()}/billing-monthly-{width:0}.png");
                scroll.ScrollToBottom();
                await Idle(80);
                Check($"SP5 {width}x{height} {theme}: rendered, and the bottom of the page is reachable", colours > 12
                    && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, $"{colours} colours");
            }
            themes.Apply(ThemeIds.Midnight, persist: false);
            foreach (var tab in vm.Tabs.ToList())
            {
                vm.SelectedTab = tab;
                await Idle(60);
            }
            vm.SelectedTab = vm.Tabs.First();
            scroll.ScrollToTop();
            await Idle(100);
            vm.ShowSavings();
            await Idle(200);
            var top = panel.TranslatePoint(new Point(0, 0), scroll);
            // At the top of the view, not one pixel above its bottom edge (or as far as the page scrolls, if it is short).
            Check("SP6 \"Spend & savings\" request: the savings section's top is at the top of the page", scroll.VerticalOffset > 0
                && (Math.Abs(top.Y) < 2 || (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && top.Y > 0)),
                $"offset {scroll.VerticalOffset:0} / {scroll.ScrollableHeight:0}, section at {top.Y:0} of {scroll.ViewportHeight:0}");
            vm.ShowSavings();
            vm.SetVisible(false);   // left before it scrolled: the request is dropped
            vm.SetVisible(true);
            await vm.RefreshAsync();
            Check("SP6 a request left behind by leaving the page is not carried into the next visit", !vm.TakeSavingsRequest());
            List<string> found;
            lock (errors.Lines) found = errors.Lines.Where(l => l.Contains("BindingExpression")).Distinct().ToList();
            Check("SP7 the page, both sections, every tab and theme: no binding errors", found.Count == 0, string.Join(" || ", found.Take(4)));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            win.Close();
            vm.SetVisible(false);
        }
    }

    // ── The title bar's "today" chip ▸ Spend & savings, in the real MainWindow ──

    static async Task SavingsMenu(CluadeX.MainWindow win, MainViewModel main)
    {
        main.NavigateToCommand.Execute("Chat");
        await Idle(200);
        var anchor = Find<RadioButton>(win).First(r => r.GroupName == "Activity");
        typeof(CluadeX.MainWindow).GetMethod("TodayChip_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(win, [anchor, new RoutedEventArgs()]);
        await Idle(150);
        MenuItem? item = null;
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            if (source.RootVisual is DependencyObject root)
                item ??= Find<MenuItem>(root).FirstOrDefault(m => (m.Header as string) == L("usage.menu"));
        Check("SM1 the today chip's menu offers Spend & savings", item != null);
        if (item == null) return;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (Ancestor<ContextMenu>(item) is { } menu) menu.IsOpen = false;
        else foreach (var cm in PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<DependencyObject>().SelectMany(Find<ContextMenu>)) cm.IsOpen = false;
        await Idle(400);
        var view = Find<BillingView>(win).FirstOrDefault(v => v.IsVisible);
        var scroll = view == null ? null : Find<ScrollViewer>(view).Where(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).OrderByDescending(s => s.ExtentHeight).FirstOrDefault();
        var panel = view == null ? null : Find<SavingsPanel>(view).FirstOrDefault();
        var y = panel != null && scroll != null ? panel.TranslatePoint(new Point(0, 0), scroll).Y : double.NaN;
        Check("SM2 …which opens the Billing page at its savings section (no separate window)", main.SelectedNavItem == "Billing" && view != null
            && scroll != null && scroll.VerticalOffset > 0 && (Math.Abs(y) < 2 || (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && y > 0))
            && !Application.Current.Windows.OfType<Window>().Any(w => w.GetType().Name == "UsageWindow"),
            $"{main.SelectedNavItem} · offset {scroll?.VerticalOffset:0} · section at {y:0}");
    }

    // ── Static: the new XAML, its keys, and nothing left of the old window ──

    static void SavingsStatic(LocalizationService loc)
    {
        Log("\n## Static checks (SavingsPanel.xaml, AccountPanel.xaml, BillingStyles.xaml; the old window gone)");
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "Views", "BillingView.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("static: repository found next to the harness", false); return; }
        string R(params string[] p) => File.ReadAllText(Path.Combine(new[] { repo, "CluadeX" }.Concat(p).ToArray()));
        string styles = R("Views", "Billing", "BillingStyles.xaml");
        var panels = new (string File, string Xaml, Type[] Types)[]
        {
            ("SavingsPanel.xaml", R("Views", "Billing", "SavingsPanel.xaml"), new[] { typeof(BillingSavingsViewModel), typeof(BillingSavingsViewModel.ProviderChoice),
                typeof(BillingSavingsViewModel.SummaryCard), typeof(BillingSavingsViewModel.SavingRow), typeof(BillingSavingsViewModel.StrategyRow),
                typeof(BillingSavingsViewModel.LinePoint), typeof(BillingSavingsViewModel.ProviderLine), typeof(BillingSavingsViewModel.RangeChoice),
                typeof(BillingSavingsViewModel.MonthlyTotal) }),
            ("AccountPanel.xaml", R("Views", "Billing", "AccountPanel.xaml"), new[] { typeof(BillingAccountViewModel), typeof(BillingAccountViewModel.AccountChoice) }),
            ("BillingStyles.xaml", styles, new[] { typeof(BillingSavingsViewModel.ProviderChoice) }),
        };
        string code = R("ViewModels", "BillingSavingsViewModel.cs") + R("ViewModels", "BillingAccountViewModel.cs");
        var keys = panels.SelectMany(p => Regex.Matches(p.Xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(code, @"""((?:billing|usage)\.[\w.]+)""").Select(m => m.Groups[1].Value).Where(k => !k.EndsWith('.')))
            .Concat(Regex.Matches(code, @"L\(""(usage\.strategy\.)""\s*\+").Select(_ => "usage.strategy.single"))
            .Append("usage.menu").Distinct().ToList();
        loc.SetLanguage("th");
        // A pure format ("≈ {0}") has no words to translate.
        bool Wordless(string k) => !Regex.IsMatch(loc.T(k), @"\p{L}");
        var missing = keys.Where(k => loc.T(k) == k || (!Regex.IsMatch(loc.T(k), "[฀-๿]") && !Wordless(k))).ToList();
        Check($"SS1 every localized key ({keys.Count}) the two sections use has a Thai value", missing.Count == 0, string.Join(", ", missing));
        loc.SetLanguage("en");
        var missingEn = keys.Where(k => loc.T(k) == k || Regex.IsMatch(loc.T(k), "[฀-๿]")).ToList();
        Check("SS1 …and an English one", missingEn.Count == 0, string.Join(", ", missingEn));
        loc.SetLanguage("th");
        string resources = R("Resources", "Theme.xaml") + R("App.xaml") + styles;
        foreach (var (file, xaml, types) in panels)
        {
            var missingRes = Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
                .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal) && !xaml.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
            Check($"SS2 {file}: every {{StaticResource}} exists", missingRes.Count == 0, string.Join(", ", missingRes));
            var missingDyn = ThemeIds.All.SelectMany(theme =>
            {
                string t = R("Resources", "Themes", theme + ".xaml");
                return Regex.Matches(xaml, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
                    .Where(k => !t.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).Select(k => theme + ":" + k);
            }).ToList();
            Check($"SS3 {file}: every {{DynamicResource}} is a token of every theme", missingDyn.Count == 0, string.Join(", ", missingDyn));
            var bad = Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)([^}]*)\}").Where(m => !m.Groups[2].Value.Contains("ElementName"))
                .Select(m => m.Groups[1].Value.Split('.')[0]).Distinct().Where(p => !types.Any(t => t.GetProperty(p) != null)).ToList();
            Check($"SS4 {file}: every {{Binding}} names a real property", bad.Count == 0, string.Join(", ", bad));
        }
        string billingXaml = R("Views", "BillingView.xaml");
        Check("SS5 BillingView hosts both sections on its view model's Savings / Account", billingXaml.Contains("<billing:SavingsPanel") && billingXaml.Contains("DataContext=\"{Binding Savings}\"")
            && billingXaml.Contains("<billing:AccountPanel") && billingXaml.Contains("DataContext=\"{Binding Account}\""));
        var gone = new[] { Path.Combine(repo, "CluadeX", "Views", "UsageWindow.xaml"), Path.Combine(repo, "CluadeX", "Views", "UsageWindow.xaml.cs"),
            Path.Combine(repo, "CluadeX", "ViewModels", "UsageViewModel.cs") };
        var refs = Directory.EnumerateFiles(Path.Combine(repo, "CluadeX"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "tests"), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.EndsWith("SavingsChecks.cs"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\b(UsageWindow|UsageViewModel|OpenUsageWindow)\b")).ToList();
        Check("SS6 the old window is gone and nothing refers to it (CluadeX/ and tests/)", gone.All(f => !File.Exists(f)) && refs.Count == 0, string.Join(", ", refs));
        string mainCode = R("MainWindow.xaml.cs");
        Check("SS7 the today chip's menu opens the Billing page's savings", mainCode.Contains("NavigateToCommand.Execute(\"Billing\")") && mainCode.Contains("ShowSavings()"));
    }

    // ── The credit / plan-fee box: the dollar habit, the comma-decimal habit, and refusals instead of guesses ──

    static void UsdEntryCases()
    {
        (string Text, decimal? Usd)[] cases =
        [
            ("1,234.56", 1234.56m), ("1.234,56", 1234.56m), ("12,50", 12.50m), ("$12.50", 12.50m), ("25", 25m),
            ("1,000", 1000m), ("1.234.567,8", 1234567.8m), ("1,234,567", 1234567m), ("0,125", 0.125m), ("0.125", 0.125m),
            ("1 234,56", 1234.56m), (" $ 7 ", 7m),
            ("1.000", null), ("1,2,3", null), ("1,23.45", null), ("1.234.56,7", null), ("1,234,56", null), ("1,234.5,6", null),
            ("1.2,3", null), ("-3", null), ("abc", null), ("", null), (".", null), ("12.3.4", null), ("๑๒", null),
        ];
        var saved = CultureInfo.CurrentCulture;
        var wrong = new List<string>();
        try
        {
            foreach (var culture in new[] { "en-US", "th-TH", "de-DE" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                foreach (var (text, usd) in cases)
                {
                    bool ok = UsdText.TryParse(text, out var got);
                    if (ok != usd.HasValue || (ok && got != usd)) wrong.Add($"{culture} \"{text}\" → {(ok ? got.ToString(CultureInfo.InvariantCulture) : "refused")}");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Check("SA10 USD entries: \"1.234,56\" is 1234.56, never $1.23; mixed or misplaced separators and \"1.000\" are refused, under en-US / th-TH / de-DE",
            wrong.Count == 0, string.Join("; ", wrong));
    }
}
