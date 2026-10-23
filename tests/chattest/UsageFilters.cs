using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    static async Task TestUsageFilters()
    {
        Log("\n## U8 filters — all providers, local date boundaries, no double counting, small/high-DPI displays");
        string ledger = Path.Combine(Settings.DataRoot, "usage-daily.json");
        string? before = File.Exists(ledger) ? File.ReadAllText(ledger) : null;
        string language = LocSvc.CurrentLanguage;
        var savedBilling = Settings.Settings.ProviderSpending;
        Settings.Settings.ProviderSpending = new();
        UsageWindow? window = null;
        UsageViewModel? vm = null;
        try
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
                if (i == 2) { day.Usd += 3; day.BaselineUsd += 3; day.Requests++; } // mixed older and attributed data
                if (i == 110) day = new DailyUsage { Usd = 7, Requests = 1 }; // before provider/baseline tracking
                seed[DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = day;
            }
            File.WriteAllText(ledger, JsonSerializer.Serialize(seed));
            var tracker = new CostTrackingService(Settings);
            var from = DateTime.Today.AddDays(-3); var to = DateTime.Today.AddDays(-1);
            var queried = tracker.Between(from, to);
            Check("U8 range: both endpoints included, oldest first, missing dates zero-filled", queried.Count == 3 && queried[0].Day == from && queried[^1].Day == to
                && tracker.Between(DateTime.Today.AddYears(-2), DateTime.Today.AddYears(-2)).Single().Usd == 0);
            Check("U8 history: provider discovery includes custom and legacy rows", tracker.RecordedProviders.Contains("CustomCloud") && tracker.RecordedProviders.Contains(CostTrackingService.UnattributedProvider));
            Check("U8 mixed history: attributed shares plus residual equal total exactly", tracker.RecordedProviders.Sum(p => tracker.Between(from, to, p).Sum(d => d.Usd)) == queried.Sum(d => d.Usd)
                && tracker.Between(from, to, CostTrackingService.UnattributedProvider).Sum(d => d.Usd) == 3m);
            vm = new UsageViewModel(tracker, Settings, null);
            Check("U8 providers: all 11 routes offered with distinct stable colours", vm.Providers.Count >= 11 && vm.Providers.Take(11).Select(p => p.Color.ToString()).Distinct().Count() == 11);
            Check("U8 missing billing: unmetered routes have no invented zero-cost series", !vm.Providers.Single(p => p.Key == "ClaudeDev").HasData
                && vm.Providers.Single(p => p.Key == "ClaudeDev").TotalText != "$0.00" && vm.ProviderLines.Count == 5);
            decimal expected = tracker.LastDays(14).Sum(d => d.Usd);
            Check("U8 totals: selected providers include all older spend exactly once", vm.Periods[0].Paid == UsageViewModel.Usd(expected)
                && vm.Providers.Sum(p => p.Total) == expected);
            var colours = vm.Providers.ToDictionary(p => p.Key, p => p.Color.ToString());
            vm.SelectNoneCommand.Execute(null);
            Check("U8 empty selection: no series, zero totals, no auto-reset to all", !vm.HasUsage && vm.ProviderLines.Count == 0 && vm.Periods[0].Paid == "$0.00" && vm.Providers.All(p => !p.IsSelected));
            vm.Providers.Single(p => p.Key == "OpenAI").IsSelected = true;
            Check("U8 single provider: graph and totals both follow selection", vm.ProviderLines.Count == 1 && vm.ProviderLines[0].Label == "OpenAI API"
                && vm.Periods[0].Paid == UsageViewModel.Usd(tracker.LastDays(14, "OpenAI").Sum(d => d.Usd)));
            vm.StartDate = from; vm.EndDate = to; vm.ApplyDatesCommand.Execute(null);
            Check("U8 custom dates: graph and totals use inclusive range", vm.Bars.Count == 3 && vm.Periods[0].Paid == UsageViewModel.Usd(tracker.Between(from, to, "OpenAI").Sum(d => d.Usd)));
            string kept = vm.RangeLabel;
            vm.StartDate = DateTime.Today; vm.EndDate = DateTime.Today.AddDays(-1); vm.ApplyDatesCommand.Execute(null);
            Check("U8 invalid dates: a message, previous applied chart preserved", vm.RangeError.Length > 0 && vm.RangeLabel == kept);
            vm.StartDate = null; vm.ApplyDatesCommand.Execute(null);
            Check("U8 empty dates: refused safely", vm.RangeError.Length > 0);
            vm.StartDate = from; vm.EndDate = DateTime.Today.AddDays(1); vm.ApplyDatesCommand.Execute(null);
            Check("U8 future dates: refused safely", vm.RangeError.Length > 0);
            vm.SelectedRange = vm.Ranges.First(r => r.Key == "all");
            vm.SelectAllCommand.Execute(null);
            Check("U8 all history: at most 90 points, no spend dropped", vm.Bars.Count <= 90 && vm.ProviderLines.All(p => p.Points.Count <= 90)
                && vm.Periods[0].Paid == UsageViewModel.Usd(seed.Values.Sum(d => d.Usd)));
            Check("U8 filters keep provider colours stable", vm.Providers.All(p => colours[p.Key] == p.Color.ToString()));
            vm.IsCumulative = false; vm.ShowSavings = false; vm.UpdateChartWidth(260);
            Check("U8 daily charts: finite points stay within resized plotting area", vm.ProviderLines.SelectMany(p => p.Points).All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= 260 && p.Y >= 0 && p.Y <= UsageViewModel.LineHeight));
            vm.SelectedRange = vm.Ranges.First(r => r.Days == 1);
            Check("U8 one-day plot: visible point centred, no division by zero", vm.Bars.Count == 1 && vm.ProviderLines.All(p => p.Points.Count == 1 && Math.Abs(p.Points[0].X - 130) < 0.01));
            foreach (var area in new[] { new Rect(0, 0, 1366, 728), new Rect(0, 0, 683, 364), new Rect(-1280, 0, 1280, 984), new Rect(1920, 0, 960, 520) })
            {
                var bounds = UsageWindow.InitialBounds(area, new Rect(5000, 1000, 1200, 900));
                Check("U8 work-area fit: " + area, area.Contains(bounds));
            }
            TestProviderBilling(tracker, vm);
            LocSvc.SetLanguage("th");
            vm.SelectedRange = vm.Ranges.First(r => r.Days == 14);
            vm.IsCumulative = true; vm.ShowSavings = true;
            window = new UsageWindow(vm) { Left = -12000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check("U8 billing selector renders the provider name rather than its class name", Find<TextBlock>((ComboBox)window.FindName("BillingProviderPicker")).Any(t => t.Text == vm.SelectedBillingProvider.Label));
            var checks = Find<CheckBox>(window).Where(c => c.DataContext is UsageViewModel.ProviderChoice).ToList();
            var checkbox = checks.Single(c => ((UsageViewModel.ProviderChoice)c.DataContext).Key == "Anthropic");
            checkbox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check("U8 live selector: checkbox hides that provider's line", !vm.Providers.Single(p => p.Key == "Anthropic").IsSelected && vm.ProviderLines.All(p => p.Label != "Claude API"));
            vm.SelectAllCommand.Execute(null);
            var picker = (DatePicker)window.FindName("FromPicker");
            picker.SelectedDate = DateTime.Today.AddDays(-2);
            ((DatePicker)window.FindName("ToPicker")).SelectedDate = DateTime.Today;
            ((Button)window.FindName("ApplyRangeButton")).Command.Execute(null);
            Check("U8 live dates: picker + Apply updates the plotted days", vm.Bars.Count == 3);
            vm.SelectedRange = vm.Ranges.First(r => r.Days == 14);
            var themes = Sp.GetRequiredService<ThemeService>();
            foreach (var (width, height, theme) in new[] { (1050d, 790d, ThemeIds.Midnight), (640d, 600d, ThemeIds.Classic), (480d, 360d, ThemeIds.NeonCyber) })
            {
                themes.Apply(theme, persist: false);
                window.Width = width; window.Height = height;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("UsageScroll");
                var plot = (FrameworkElement)window.FindName("PlotArea");
                Check($"U8 {width}x{height}: bounded viewport, vertical scroll, responsive chart", scroll.ScrollableHeight > 0 && scroll.ViewportHeight < height
                    && scroll.ExtentWidth <= scroll.ViewportWidth + 1 && vm.ProviderLines.SelectMany(p => p.Points).All(p => p.X <= plot.ActualWidth + 1));
                scroll.ScrollToVerticalOffset(width > 900 ? 280 : 600);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SaveUsageImage(window, $"usage-responsive-{width:0}-{theme}.png");
                if (width > 900)
                {
                    ((FrameworkElement)window.FindName("MonthlyTotalsCard")).BringIntoView();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    SaveUsageImage(window, "usage-monthly-midnight.png");
                    ((FrameworkElement)window.FindName("ProviderBillingCard")).BringIntoView();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    SaveUsageImage(window, "usage-provider-billing-midnight.png");
                }
                scroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check($"U8 {width}x{height}: bottom sections reachable", Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
            }
            themes.Apply(ThemeIds.Midnight, persist: false);
        }
        finally
        {
            window?.Close(); vm?.Detach();
            LocSvc.SetLanguage(language);
            Settings.UpdateSettings(s => s.ProviderSpending = savedBilling);
            if (before == null) File.Delete(ledger); else File.WriteAllText(ledger, before);
        }
    }

    static void TestProviderBilling(CostTrackingService tracker, UsageViewModel vm)
    {
        decimal originalApi = tracker.TodayCostUsd;
        tracker.RecordPackageUsage("ClaudeDev", 40m, 10000, 1000, "cli-reported-equivalent");
        Check("U8 package telemetry is not billed again as API or counted against the API spend guard", tracker.TodayCostUsd == originalApi
            && tracker.TotalCostUsd == 0 && tracker.Between(DateTime.Today, DateTime.Today, "ClaudeDev").Single().BaselineUsd == 40m);
        vm.Refresh();
        vm.SelectedBillingProvider = vm.BillingProviders.Single(p => p.Key == "Anthropic");
        vm.BalanceInput = "100"; vm.SetBalanceCommand.Execute(null);
        vm.SelectedBillingProvider = vm.BillingProviders.Single(p => p.Key == "OpenAI");
        vm.BalanceInput = "50"; vm.SetBalanceCommand.Execute(null);
        decimal anthropic = CostTrackingService.Price("claude-opus-5-5", 1000000, 0).Actual;
        decimal openai = CostTrackingService.Price("gpt-6.1-sol", 10000, 0).Actual;
        tracker.RecordUsage("claude-opus-5-5", 1000000, 0);
        tracker.RecordUsage("gpt-6.1-sol", 10000, 0);
        vm.Refresh();
        Check("U8 API credit anchors deduct only that provider, including streamed updates since the anchor",
            vm.BalanceText.Contains(UsageViewModel.Usd(50m - openai)) && Settings.Settings.ProviderSpending["Anthropic"].CreditBalanceUsd == 100m);
        vm.SelectedBillingProvider = vm.BillingProviders.Single(p => p.Key == "Anthropic");
        Check("U8 switching credit account restores its independent remaining balance", vm.BalanceText.Contains(UsageViewModel.Usd(100m - anthropic)));
        vm.ClearBalanceCommand.Execute(null);
        Check("U8 clearing one credit account preserves the other", !vm.HasBalance && Settings.Settings.ProviderSpending["OpenAI"].CreditBalanceUsd == 50m);
        vm.SelectedBillingProvider = vm.BillingProviders.Single(p => p.Key == "ClaudeDev");
        vm.PackagePriceInput = "20"; vm.PackageStartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        vm.SavePackageCommand.Execute(null);
        var reloaded = new SettingsService();
        Check("U8 provider billing survives settings reload", reloaded.Settings.ProviderSpending["ClaudeDev"].MonthlyPackageUsd == 20m
            && reloaded.Settings.ProviderSpending["OpenAI"].CreditBalanceUsd == 50m);
        var month = vm.MonthlyTotals.Single(m => m.Month.Year == DateTime.Today.Year && m.Month.Month == DateTime.Today.Month);
        Check("U8 monthly total includes full package fee once, alongside API spend", month.PackageUsd == 20m
            && month.ApiUsd == vm.Providers.Where(p => !ProviderCostProjection.IsPackage(p.Key)).Sum(p => tracker.Between(month.Month, DateTime.Today, p.Key).Sum(d => d.Usd))
            && !month.MissingUsage);
        Check("U8 monthly savings compares estimated full-price work with API plus package spend", month.BaselineUsd - month.ApiUsd - month.PackageUsd >= 20m
            && month.Saved.Contains(UsageViewModel.Usd(month.BaselineUsd - month.ApiUsd - month.PackageUsd)));
        decimal dailyFee = 20m / DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
        Check("U8 graph allocates package fees by day without adding API-equivalent charges a second time",
            ProviderCostProjection.Project("ClaudeDev", tracker.Between(DateTime.Today, DateTime.Today, "ClaudeDev"), Settings.Settings.ProviderSpending["ClaudeDev"]).Single().Usd == dailyFee);
        vm.PackagePriceInput = "abc"; vm.SavePackageCommand.Execute(null);
        Check("U8 invalid package fee preserves the entered plan", vm.BalanceError.Length > 0 && Settings.Settings.ProviderSpending["ClaudeDev"].MonthlyPackageUsd == 20m);
        vm.PackagePriceInput = "20"; vm.SavePackageCommand.Execute(null);
        tracker.RecordPackageUsage("OpenAIDev", 2m, 100, 100, "cli-tokens"); vm.Refresh();
        Check("U8 package without entered price is explicitly an equivalent-value proxy", vm.MonthlyTotals.Any(m => m.HasProxy)
            && vm.Providers.Single(p => p.Key == "OpenAIDev").TotalText.StartsWith("≈"));
        var negative = new UsageViewModel.MonthlyTotal(DateTime.Today, 2, 20, 10, false, false);
        Check("U8 monthly savings can be negative instead of falsely clamped to zero", negative.Saved.Contains("-12.00") && !negative.IsSaving);
        var estimator = new CluadeX.Services.Providers.DelegatedUsageEstimate();
        estimator.Feed("""{"type":"result","total_cost_usd":4.5,"usage":{"input_tokens":100,"output_tokens":20}}""");
        Check("U8 package estimation prefers CLI-reported equivalent, never calls a model", estimator.Estimate("ClaudeDev", "", "").EquivalentUsd == 4.5m);
        var codex = new CluadeX.Services.Providers.CodexStreamTranslator(@"C:\fixture");
        var estimate = new CluadeX.Services.Providers.DelegatedUsageEstimate("gpt-6.1-sol");
        foreach (var line in codex.Translate("""{"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":50}}""")) estimate.Feed(line);
        Check("U8 Codex usage preserves cached tokens and prices them only once", estimate.InputTokens == 200 && estimate.CacheReadTokens == 800
            && estimate.Estimate("OpenAIDev", "", "").EquivalentUsd == CostTrackingService.Price("gpt-6.1-sol", 200, 50, 800).Actual);
        var fallback = new CluadeX.Services.Providers.DelegatedUsageEstimate().Estimate("GeminiDev", "test", "answer");
        Check("U8 fallback is labelled a text lower-bound estimate", fallback.Method == "text-lower-bound" && fallback.Input > 0 && fallback.Output > 0);
        Check("U8 monthly history survives new usage beyond the old 90-day retention", new CostTrackingService(Settings).FirstUsageDay <= DateTime.Today.AddDays(-119));
        Check("U8 USD entries distinguish thousands from decimal commas", UsageViewModel.TryParseUsd("1,000", out var thousand) && thousand == 1000m
            && UsageViewModel.TryParseUsd("1,234.56", out var grouped) && grouped == 1234.56m && !UsageViewModel.TryParseUsd("1,2,3", out _));
        Check("U8 tiny API costs remain nonzero on screen", UsageViewModel.Usd(0.0000001m) == "$0.0000001");
        vm.SelectedRange = vm.Ranges.Single(r => r.Key == "previousMonth");
        var previous = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        Check("U8 calendar-month preset matches both boundaries", vm.StartDate == previous && vm.EndDate == previous.AddMonths(1).AddDays(-1)
            && vm.MonthlyTotals.Count == 1 && vm.MonthlyTotals[0].Month == previous);
    }

    static void SaveUsageImage(UsageWindow window, string name)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
}
