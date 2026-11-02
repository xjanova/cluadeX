using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Billing;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BillingTest;

/// <summary>
/// The Billing &amp; cost page, proved against the built CluadeX.dll:
///   · UsageLog: rows written, merged per 15-minute window, read back incrementally, a damaged line skipped;
///   · the recorders: CostTrackingService.RecordUsage / RecordPackageUsage / RecordLocalUsage land as billed /
///     estimated / free rows with their chat, sub-agent flag, cache and thinking tokens;
///   · UsageAggregator on fixture rows: per provider, per model, per day, billed vs estimated, top chats, and the
///     calendar — a row at 23:59 vs 00:00 local, the same rows under another time zone, a DST change, week start;
///   · BillingViewModel: tabs, estimate / local wording, budgets, the cache, no work while hidden, aggregation off the
///     UI thread;
///   · every tab rendered in every theme (PNG next to the report), the empty state, the static XAML checks, and the
///     page opened from the real MainWindow's activity bar.
/// No network, no model, no ~/.cluadex (portable.txt → &lt;exe&gt;\Data, wiped every run).
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Failures = new();
    static readonly StringBuilder Report = new();
    static ServiceProvider Sp = null!;
    static readonly string Out = Path.Combine(AppContext.BaseDirectory, "billing-out");

    [STAThread]
    static int Main()
    {
        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
            finally
            {
                try { File.WriteAllText(Path.Combine(Out, "billing-report.md"), Report.ToString(), new UTF8Encoding(false)); } catch { }
                Console.WriteLine($"\nreport + pictures: {Out}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        Environment.Exit(rc);
        return rc;
    }

    // ═══════════════════════════════════════════ plumbing ═══════════════════════════════════════════

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else { Fail++; Failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    }

    static async Task Idle(int ms = 0)
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(60);
        }
        return cond();
    }

    static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    /// <summary>Render to a PNG; returns how many distinct colours a coarse sample of it has (a blank page has ~1).</summary>
    static int SavePng(FrameworkElement el, string name)
    {
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        if (w <= 0 || h <= 0) { Log($"  (no picture: {name} has no size)"); return 0; }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string path = Path.Combine(Out, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path)) enc.Save(fs);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        var colours = new HashSet<int>();
        for (int y = 0; y < h; y += 7) for (int x = 0; x < w; x += 7) { int i = (y * w + x) * 4; colours.Add(px[i] >> 4 | (px[i + 1] >> 4) << 4 | (px[i + 2] >> 4) << 8); }
        return colours.Count;
    }

    static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    static UsageEntry Row(string at, string provider, string model, UsageCostKind kind, decimal usd, long input = 1000, long output = 200,
        string? chat = null, bool sub = false, long cacheRead = 0, long cacheWrite = 0, long thinking = 0, double seconds = 0, int requests = 1) => new()
    {
        At = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), Provider = provider, Model = model, Kind = kind, Usd = usd,
        Input = input, Output = output, Chat = chat, SubAgent = sub, CacheRead = cacheRead, CacheWrite = cacheWrite, Thinking = thinking,
        Seconds = seconds, Requests = requests,
    };

    static readonly TimeZoneInfo Bangkok = TimeZoneInfo.CreateCustomTimeZone("Test+7", TimeSpan.FromHours(7), "Test+7", "Test+7");
    // Thursday 5 November 2026, 10:00 in Bangkok.
    static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(7));

    /// <summary>The fixture: a little of every provider kind, rows on both sides of local midnight and of the month.</summary>
    static List<UsageEntry> Fixture() => new()
    {
        Row("2026-11-04T16:59:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 1.00m, chat: "c1", cacheRead: 5000, cacheWrite: 800),   // 4 Nov 23:59 local
        Row("2026-11-04T17:00:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 2.00m, chat: "c1", sub: true),                         // 5 Nov 00:00 local = today
        Row("2026-10-31T17:30:00Z", "ClaudeDev", "claude-opus-5-5", UsageCostKind.Estimated, 2.90m, input: 40000, output: 9000, chat: "c3"),      // 1 Nov 00:30 local
        Row("2026-11-05T01:00:00Z", "Ollama", "qwen3:8b", UsageCostKind.Free, 0m, input: 1000, output: 500, chat: "c4", seconds: 12.5),           // today
        Row("2026-10-31T16:59:00Z", "OpenAI", "gpt-6-sol", UsageCostKind.Billed, 0.50m, thinking: 120, chat: "c2"),                               // 31 Oct 23:59 local
        Row("2026-11-03T05:00:00Z", "Gemini", "gemini-3.8-flash", UsageCostKind.Billed, 0.25m, cacheRead: 3000, thinking: 64, chat: "c2"),
        Row("2026-06-01T05:00:00Z", "Anthropic", "claude-opus-4-8", UsageCostKind.Billed, 10.00m, chat: "c9"),                                   // outside 90 days
        Row("2026-11-02T03:00:00Z", "LlamaServer", "gemma-4-9b", UsageCostKind.Free, 0m, input: 3000, output: 900, seconds: 40, requests: 2),
    };

    static async Task<int> RunAll()
    {
        if (Directory.Exists(Out)) Directory.Delete(Out, true);
        Directory.CreateDirectory(Out);
        Log("# Billing & cost page — harness\n");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))) { Log("FATAL portable.txt is missing — refusing to run against the real ~/.cluadex"); return 2; }
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        Sp = services.BuildServiceProvider();
        var settings = Sp.GetRequiredService<SettingsService>();
        bool portable = settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        Check("setup: portable data root (never the user's ~/.cluadex)", portable, settings.DataRoot);
        if (!portable) return 2;
        Directory.CreateDirectory(settings.DataRoot);
        settings.UpdateSettings(s => { s.ActiveProvider = AiProviderType.Local; s.SelectedModelPath = null; s.SelectedModelName = null; s.Language = "th"; });
        var loc = Sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("th");

        UsageLogFile();
        Recorders();
        Aggregation();
        TimeZones();
        EmptyAggregation();
        await ViewModel(settings);
        await Renders(settings);
        StaticXaml(loc);
        await Navigation();

        Log($"\n## Result: {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════════════════ the log file ═══════════════════════════════════════════

    static void UsageLogFile()
    {
        Log("\n## UsageLog — the file behind the page");
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "log-test", "usage-log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = new UsageLog(path);
        // A request reports several times (input at its start, output at its end): one row per window.
        log.Add(Row("2026-11-05T03:01:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 0.10m, 1000, 0, chat: "c1"));
        log.Add(Row("2026-11-05T03:02:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 0.05m, 0, 300, chat: "c1", requests: 0));
        log.Add(Row("2026-11-05T03:16:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 0.20m, 2000, 100, chat: "c1"));   // next window
        log.Add(Row("2026-11-05T03:03:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 0.01m, 10, 1, chat: "c2"));        // other chat
        log.Add(Row("2026-11-05T03:04:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 0m, 0, 0, requests: 0));          // nothing: dropped
        var before = log.ReadAll();
        Check("L1 rows of one request in one 15-minute window merge (4 adds → 3 rows, the empty one dropped)", before.Count == 3, before.Count.ToString());
        var merged = before.Single(r => r.Chat == "c1" && r.Input == 1000);
        Check("L1 the merged row sums tokens, dollars and requests", merged.Output == 300 && merged.Usd == 0.15m && merged.Requests == 1,
            $"out {merged.Output} usd {merged.Usd} n {merged.Requests}");
        Check("L2 nothing is written before a flush", !File.Exists(path));
        log.Flush();
        string text = File.ReadAllText(path);
        Check("L2 a flush writes one JSON line per row, LF, no BOM", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3
            && !text.Contains('\r') && File.ReadAllBytes(path)[0] == (byte)'{', text.Length + " chars");
        var reread = new UsageLog(path).ReadAll();
        Check("L3 a fresh log reads the same rows back (UTC instant, decimal dollars)", reread.Count == 3
            && reread.Sum(r => r.Usd) == before.Sum(r => r.Usd) && reread.Any(r => r.At == DateTimeOffset.Parse("2026-11-05T03:01:00Z")),
            string.Join(", ", reread.Select(r => r.At.ToString("o") + " " + r.Usd)));
        // Appended by someone else (another flush), a damaged line, and half a line still being written.
        File.AppendAllText(path, "{not json\n" + System.Text.Json.JsonSerializer.Serialize(Row("2026-11-05T04:00:00Z", "Gemini", "gemini-3.8-flash", UsageCostKind.Billed, 0.3m)) + "\n{\"t\":\"2026-11");
        var log2 = new UsageLog(path);
        var a = log2.ReadAll();
        Check("L4 a damaged line is skipped, the rows after it still read, a half-written line waits", a.Count == 4 && a.Any(r => r.Provider == "Gemini"), a.Count.ToString());
        File.AppendAllText(path, "-05T05:00:00Z\",\"p\":\"OpenAI\",\"m\":\"gpt-6-sol\",\"k\":\"Billed\",\"in\":5,\"out\":5,\"usd\":0.02,\"n\":1}\n");
        var b = log2.ReadAll();
        Check("L4 …and is read once it is whole (incremental read, nothing counted twice)", b.Count == 5 && b.Count(r => r.Provider == "Gemini") == 1, b.Count.ToString());
        Check("L5 Version moves on every add (a cached aggregation knows it is stale)", log.Version >= 4, log.Version.ToString());
    }

    static void Recorders()
    {
        Log("\n## The recorders → rows (billed / estimated / free, chat, sub-agent, cache, thinking)");
        var cost = new CostTrackingService();
        var scope = new AgentRunScope("chat-42", null);
        AgentRunScope.Enter(scope);
        try
        {
            cost.RecordUsage("claude-opus-5-5", 1000, 0, cacheReadTokens: 20000, cacheCreationTokens: 3000);
            cost.RecordUsage("claude-opus-5-5", 0, 500, isNewRequest: false);
            AgentRunScope.Enter(scope.CreateChild());
            cost.RecordUsage("gpt-6-sol", 2000, 800, cacheReadTokens: 100, thinkingTokens: 300);
            AgentRunScope.Enter(scope);
            cost.RecordUsage("gemini-3.8-flash", 1500, 400, 700, 0, thinkingTokens: 90);
            cost.RecordUsage("deepseek-chat", 10, 10);   // an unknown model on the run's provider
            cost.RecordPackageUsage("ClaudeDev", 0.42m, 5000, 1200, "cli-tokens", 30000, model: "claude-sonnet-5-5");
            cost.RecordLocalUsage("Ollama", "qwen3:8b", 900, 300, 7.25);
        }
        finally { AgentRunScope.Enter(null); }
        cost.RecordUsage("claude-haiku-5-5", 100, 20);   // outside any run: a title
        var rows = cost.Log.ReadAll();
        var claude = rows.Single(r => r.Provider == "Anthropic" && r.Model == "claude-opus-5-5");
        var expected = CostTrackingService.Price("claude-opus-5-5", 1000, 0, 20000, 3000).Actual + CostTrackingService.Price("claude-opus-5-5", 0, 500).Actual;
        Check("R1 Anthropic: input, output, cache read and cache write on one billed row of the chat, priced like the ledger",
            claude.Kind == UsageCostKind.Billed && claude.Chat == "chat-42" && claude.Input == 1000 && claude.Output == 500 && claude.CacheRead == 20000
            && claude.CacheWrite == 3000 && claude.Requests == 1 && claude.Usd == expected && !claude.SubAgent,
            $"{claude.Kind} {claude.Chat} in {claude.Input} out {claude.Output} cr {claude.CacheRead} cw {claude.CacheWrite} n {claude.Requests} ${claude.Usd} vs ${expected}");
        var gpt = rows.Single(r => r.Model == "gpt-6-sol");
        Check("R2 OpenAI: reasoning tokens kept apart, made by a sub-agent (flagged, still the chat's)",
            gpt.Provider == "OpenAI" && gpt.Thinking == 300 && gpt.SubAgent && gpt.Chat == "chat-42", $"{gpt.Provider} th {gpt.Thinking} sub {gpt.SubAgent}");
        var gem = rows.Single(r => r.Model == "gemini-3.8-flash");
        Check("R3 Gemini: the tab is \"Gemini\" (the ledger called it Google), cache read and thoughts kept",
            gem.Provider == "Gemini" && gem.CacheRead == 700 && gem.Thinking == 90, $"{gem.Provider} cr {gem.CacheRead} th {gem.Thinking}");
        Check("R4 an unknown model id lands under \"Other\" outside a run of an API provider", rows.Single(r => r.Model == "deepseek-chat").Provider == "Other");
        var cli = rows.Single(r => r.Provider == "ClaudeDev");
        Check("R5 Claude Dev: an ESTIMATE row (API-price equivalent), with its model and cache reads",
            cli.Kind == UsageCostKind.Estimated && cli.Usd == 0.42m && cli.Model == "claude-sonnet-5-5" && cli.CacheRead == 30000 && cli.Chat == "chat-42",
            $"{cli.Kind} ${cli.Usd} {cli.Model}");
        var local = rows.Single(r => r.Provider == "Ollama");
        Check("R6 Ollama: a FREE row — $0, tokens and the time it took", local.Kind == UsageCostKind.Free && local.Usd == 0 && local.Input == 900
            && local.Output == 300 && Math.Abs(local.Seconds - 7.25) < 1e-9, $"{local.Kind} ${local.Usd} {local.Seconds}s");
        Check("R6 …and a local model costs the spend ledger nothing (its budget counts money only)", cost.Between(DateTime.Today, DateTime.Today, "Ollama").All(d => d.Requests == 0));
        Check("R7 usage outside any run has no chat", rows.Single(r => r.Model == "claude-haiku-5-5").Chat == null);
        var legacy = cost.LegacyRows(null);
        Check("R8 the day ledger turns into rows for days before the log (Google → Gemini, CLI as an estimate)",
            legacy.Any(r => r.Provider == "Gemini" && r.Legacy) && legacy.Any(r => r.Provider == "ClaudeDev" && r.Kind == UsageCostKind.Estimated && r.Usd == 0.42m),
            string.Join(", ", legacy.Select(r => r.Provider + ":" + r.Kind + ":" + r.Usd)));
        Check("R8 …and none for days the log already has (no double count)", cost.LegacyRows(DateTime.Today).Count == 0);
    }

    // ═══════════════════════════════════════════ aggregation ═══════════════════════════════════════════

    static void Aggregation()
    {
        Log("\n## UsageAggregator — per provider, model, day; billed vs estimated (Bangkok, Thu 5 Nov 2026 10:00)");
        var titles = new Dictionary<string, string> { ["c1"] = "Fix the build", ["c2"] = "Gemini vs GPT", ["c3"] = "CLI refactor" };
        var s = UsageAggregator.Build(Fixture(), new BillingQuery(Now, Bangkok, 30), id => titles.GetValueOrDefault(id));
        Check("A1 tabs: All first, then providers with use in brand order", string.Join(",", s.Reports.Select(r => r.Key))
            == "All,Anthropic,OpenAI,Gemini,ClaudeDev,Ollama,LlamaServer", string.Join(",", s.Reports.Select(r => r.Key)));
        Check("A2 calendar: today 5 Nov, week from Monday 2 Nov, month from 1 Nov, range 7 Oct–5 Nov",
            s.Today == new DateTime(2026, 11, 5) && s.WeekStart == new DateTime(2026, 11, 2) && s.MonthStart == new DateTime(2026, 11, 1)
            && s.RangeStart == new DateTime(2026, 10, 7), $"{s.Today:d} {s.WeekStart:d} {s.MonthStart:d} {s.RangeStart:d}");
        var all = s.Get("All")!;
        Check("A3 All · today: only the 00:00 row ($2 billed) — the 23:59 row is yesterday", all.Today.Spend == new Spend(2.00m, 0), all.Today.Spend.ToString());
        Check("A3 All · this week: $3.25 billed (Mon–Thu), nothing estimated", all.Week.Spend == new Spend(3.25m, 0), all.Week.Spend.ToString());
        Check("A3 All · this month: $3.25 billed + ≈$2.90 estimated (Claude Dev on 1 Nov 00:30); 31 Oct 23:59 left out",
            all.Month.Spend == new Spend(3.25m, 2.90m), all.Month.Spend.ToString());
        Check("A3 All · all time: $13.75 billed + ≈$2.90 estimated", all.AllTime.Spend == new Spend(13.75m, 2.90m), all.AllTime.Spend.ToString());
        Check("A4 requests and tokens follow the same periods (today: 2 requests, local tokens included)", all.Today.Tokens.Requests == 2
            && all.Today.Tokens.Input == 2000 && all.Today.Tokens.Output == 700, $"{all.Today.Tokens.Requests} {all.Today.Tokens.Input}/{all.Today.Tokens.Output}");
        var anth = s.Get("Anthropic")!;
        var d4 = anth.Days.Single(d => d.Day == new DateTime(2026, 11, 4)); var d5 = anth.Days.Single(d => d.Day == new DateTime(2026, 11, 5));
        Check("A5 Anthropic per day: 4 Nov $1, 5 Nov $2; running total $3 at today", d4.Spend.Total == 1m && d5.Spend.Total == 2m
            && anth.Days[^1].CumulativeUsd == 3m && anth.Days.Count == 30, $"{d4.Spend.Total} {d5.Spend.Total} {anth.Days[^1].CumulativeUsd} {anth.Days.Count}");
        Check("A5 the June row counts in all time but not in the chart range or its models", anth.AllTime.Spend.Total == 13m && anth.Range.Spend.Total == 3m
            && anth.Models.Count == 1 && anth.Models[0].Model == "claude-opus-5-5", $"{anth.AllTime.Spend.Total} {anth.Range.Spend.Total} {anth.Models.Count}");
        Check("A6 Anthropic tokens: cache read and write counted apart", anth.Range.Tokens.CacheRead == 5000 && anth.Range.Tokens.CacheWrite == 800);
        var cli = s.Get("ClaudeDev")!;
        Check("A7 Claude Dev: everything is ESTIMATED, nothing billed", cli.AllTime.Spend == new Spend(0, 2.90m) && cli.Models[0].Spend.Billed == 0);
        var oll = s.Get("Ollama")!;
        Check("A8 Ollama: $0, 1,500 tokens, 12.5 s of model time", oll.AllTime.Spend.Total == 0 && oll.AllTime.Tokens.All == 1500
            && Math.Abs(oll.AllTime.Tokens.Seconds - 12.5) < 1e-9);
        Check("A9 thinking tokens: OpenAI 120 + Gemini 64 in the range", all.Range.Tokens.Thinking == 184, all.Range.Tokens.Thinking.ToString());
        Check("A10 All's models: every model in the range, costliest first", string.Join(",", all.Models.Select(m => m.Model))
            == "claude-opus-5-5,claude-opus-5-5,gpt-6-sol,gemini-3.8-flash,gemma-4-9b,qwen3:8b"
            || string.Join(",", all.Models.Take(4).Select(m => m.Provider)) == "Anthropic,ClaudeDev,OpenAI,Gemini",
            string.Join(",", all.Models.Select(m => m.Provider + "/" + m.Model + "=" + m.Spend.Total)));
        Check("A11 top chats by cost: c1 $3 (sub-agents $2), c3 ≈$2.90, c2 $0.75, then the free one", string.Join(",", all.Chats.Select(c => c.ChatId))
            == "c1,c3,c2,c4" && all.Chats[0].SubAgentUsd == 2m && all.Chats[0].Title == "Fix the build" && all.Chats[2].Spend.Total == 0.75m
            && all.Chats[2].Providers.SetEquals(new[] { "OpenAI", "Gemini" }), string.Join(",", all.Chats.Select(c => $"{c.ChatId}={c.Spend.Total}")));
        Check("A12 the All bar of a day is split by provider", all.Days.Single(d => d.Day == new DateTime(2026, 11, 5)).UsdByProvider.GetValueOrDefault("Anthropic") == 2m
            && all.Days.Single(d => d.Day == new DateTime(2026, 11, 1)).UsdByProvider.GetValueOrDefault("ClaudeDev") == 2.90m);
        var s90 = UsageAggregator.Build(Fixture(), new BillingQuery(Now, Bangkok, 90));
        Check("A13 the 90-day range still leaves out June, 7-day keeps only 30 Oct–5 Nov", s90.Get("Anthropic")!.Range.Spend.Total == 3m
            && UsageAggregator.Build(Fixture(), new BillingQuery(Now, Bangkok, 7)).Get("OpenAI")!.Range.Spend.Total == 0.50m
            && UsageAggregator.Build(Fixture(), new BillingQuery(Now, Bangkok, 7)).Get("All")!.Days.Count == 7);
        var many = Enumerable.Range(0, 20).Select(i => Row("2026-11-05T01:00:00Z", "Anthropic", "m", UsageCostKind.Billed, i, chat: "x" + i)).ToList();
        Check("A14 top chats keep the 8 costliest", UsageAggregator.Build(many, new BillingQuery(Now, Bangkok)).Get("All")!.Chats.Select(c => c.Spend.Total).SequenceEqual(new decimal[] { 19, 18, 17, 16, 15, 14, 13, 12 }));
    }

    static void TimeZones()
    {
        Log("\n## Day boundaries under other time zones");
        var utcNow = new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero);   // the same instant as Now
        var s = UsageAggregator.Build(Fixture(), new BillingQuery(utcNow, TimeZoneInfo.Utc, 30));
        var anth = s.Get("Anthropic")!;
        Check("Z1 under UTC both Anthropic rows are 4 Nov ($3 that day), today has none",
            anth.Days.Single(d => d.Day == new DateTime(2026, 11, 4)).Spend.Total == 3m && anth.Today.Spend.Total == 0,
            $"{anth.Days.Single(d => d.Day == new DateTime(2026, 11, 4)).Spend.Total} {anth.Today.Spend.Total}");
        Check("Z1 …and the Claude Dev row (31 Oct 17:30 UTC) falls in October: this month has no estimate", s.Get("All")!.Month.Spend.Estimated == 0);
        var nepal = TimeZoneInfo.CreateCustomTimeZone("Test+5:45", new TimeSpan(5, 45, 0), "Test+5:45", "Test+5:45");
        var n = UsageAggregator.Build(new List<UsageEntry>
        {
            Row("2026-11-04T18:14:00Z", "Anthropic", "m", UsageCostKind.Billed, 1m),   // 23:59 local
            Row("2026-11-04T18:15:00Z", "Anthropic", "m", UsageCostKind.Billed, 2m),   // 00:00 local
        }, new BillingQuery(Now, nepal, 7));
        Check("Z2 a +5:45 zone splits at its own midnight (18:14Z vs 18:15Z)", n.Get("All")!.Today.Spend.Total == 2m
            && n.Get("All")!.Days.Single(d => d.Day == new DateTime(2026, 11, 4)).Spend.Total == 1m);
        TimeZoneInfo? pacific = null;
        try { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch { }
        if (pacific == null) { Check("Z3 Pacific time zone available on this PC", false); return; }
        // DST ends 1 Nov 2026 at 02:00 PDT: 01:30 happens twice; the day is 25 hours long.
        var dst = UsageAggregator.Build(new List<UsageEntry>
        {
            Row("2026-11-01T06:59:00Z", "OpenAI", "m", UsageCostKind.Billed, 1m),     // 31 Oct 23:59 PDT
            Row("2026-11-01T08:30:00Z", "OpenAI", "m", UsageCostKind.Billed, 2m),     // 1 Nov 01:30 PDT
            Row("2026-11-01T09:30:00Z", "OpenAI", "m", UsageCostKind.Billed, 4m),     // 1 Nov 01:30 PST (again)
            Row("2026-11-02T07:59:00Z", "OpenAI", "m", UsageCostKind.Billed, 8m),     // 1 Nov 23:59 PST
            Row("2026-11-02T08:00:00Z", "OpenAI", "m", UsageCostKind.Billed, 16m),    // 2 Nov 00:00 PST
        }, new BillingQuery(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(-8)), pacific, 7));
        var days = dst.Get("OpenAI")!.Days;
        Check("Z3 DST end (Pacific): 1 Nov gets both 01:30s and 23:59 ($14), 31 Oct $1, 2 Nov $16",
            days.Single(d => d.Day == new DateTime(2026, 10, 31)).Spend.Total == 1m && days.Single(d => d.Day == new DateTime(2026, 11, 1)).Spend.Total == 14m
            && days.Single(d => d.Day == new DateTime(2026, 11, 2)).Spend.Total == 16m,
            string.Join(" ", days.Where(d => d.Spend.Total > 0).Select(d => $"{d.Day:MM-dd}={d.Spend.Total}")));
        var sunday = UsageAggregator.Build(Fixture(), new BillingQuery(Now, Bangkok, 30, DayOfWeek.Sunday));
        Check("Z4 a Sunday-first week starts 1 Nov and so takes in the Claude Dev estimate", sunday.WeekStart == new DateTime(2026, 11, 1)
            && sunday.Get("All")!.Week.Spend == new Spend(3.25m, 2.90m), sunday.Get("All")!.Week.Spend.ToString());
    }

    static void EmptyAggregation()
    {
        Log("\n## No usage at all");
        var s = UsageAggregator.Build(new List<UsageEntry>(), new BillingQuery(Now, Bangkok, 30));
        Check("E1 nothing recorded: one tab (All), IsEmpty, 30 zero days, no models, no chats", s.IsEmpty && s.Reports.Count == 1
            && s.Reports[0].Days.Count == 30 && s.Reports[0].Days.All(d => d.Spend.Total == 0 && d.Tokens == 0)
            && s.Reports[0].Models.Count == 0 && s.Reports[0].Chats.Count == 0);
    }

    // ═══════════════════════════════════════════ the view model ═══════════════════════════════════════════

    static BillingViewModel NewVm(SettingsService settings, CostTrackingService cost) =>
        new(cost, settings, null) { Clock = () => Now, Zone = Bangkok };

    static CostTrackingService FixtureCost()
    {
        var cost = new CostTrackingService();
        foreach (var r in Fixture()) cost.Log.Add(r);
        return cost;
    }

    static async Task ViewModel(SettingsService settings)
    {
        Log("\n## BillingViewModel");
        var vm = NewVm(settings, FixtureCost());
        await vm.RefreshAsync();
        Check("V1 tabs built from the snapshot, All selected", vm.Tabs.Count == 7 && vm.SelectedTab?.Key == "All", string.Join(",", vm.Tabs.Select(t => t.Key)));
        Check("V2 every tab with a bundled logo decoded it (pack resource present)", vm.Tabs.Where(t => ProviderBrand.For(t.Key).Logo != null)
            .All(t => t.Logo is BitmapSource { PixelWidth: > 0 }), string.Join(",", vm.Tabs.Select(t => t.Key + ":" + (t.Logo != null))));
        Check("V2 …and every logo file named in ProviderBrand loads", ProviderBrand.All.Where(b => b.Logo != null)
            .All(b => BillingViewModel.BillingTab.LoadLogo(b.LogoUri) is BitmapSource { PixelWidth: > 0 }));
        Check("V3 All: this month reads \"$3.25 + ≈ $2.90\"", vm.Periods[2].Amount == "$3.25 + ≈ $2.90", vm.Periods[2].Amount);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "ClaudeDev");
        Check("V4 Claude Dev: amounts carry ≈ and the note says estimate", vm.IsEstimate && vm.Periods[2].Amount == "≈ $2.90"
            && vm.Models.All(m => m.Kind == LocalizedResources.Instance["billing.chip.estimate"]), vm.Periods[2].Amount + " | " + vm.KindNote);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Ollama");
        Check("V5 Ollama: tokens instead of dollars, model time shown, chart in tokens", vm.IsLocal && vm.Periods[0].Amount.Contains("1.5k")
            && vm.TokenCells.Any(c => c.Label == LocalizedResources.Instance["billing.tok.time"] && c.Value == "12.5s")
            && vm.ChartTitle == LocalizedResources.Instance["billing.chart.tokens"], vm.Periods[0].Amount);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Anthropic");
        Check("V6 Anthropic: thinking is \"not reported\", cache write is", vm.TokenCells.Single(c => c.Label == LocalizedResources.Instance["billing.tok.thinking"]).Value == "—"
            && vm.TokenCells.Single(c => c.Label == LocalizedResources.Instance["billing.tok.cacheWrite"]).Value == "800");
        Check("V7 chart: 30 bars, running total ends at the range spend", vm.Bars.Count == 30 && vm.Bars[^1].Cumulative == 3.0);
        settings.UpdateSettings(s => { s.DailyBudgetUsd = 5m; s.RunBudgetUsd = 2m; s.CliDailyBudgetUsd = 3m; });
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "All");
        Check("V8 budgets: the daily and per-run caps show on All", vm.Budgets.Any(b => b.Title == LocalizedResources.Instance["billing.budget.daily"] && b.HasMeter)
            && vm.Budgets.Any(b => b.Title == LocalizedResources.Instance["billing.budget.run"]), string.Join(" | ", vm.Budgets.Select(b => b.Title)));
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Ollama");
        Check("V8 …and a local tab says no budget is needed", vm.Budgets.Count == 1 && vm.Budgets[0].Title == LocalizedResources.Instance["billing.budget.localTitle"]);
        settings.UpdateSettings(s => { s.DailyBudgetUsd = 0; s.RunBudgetUsd = 0; s.CliDailyBudgetUsd = 0; });
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Anthropic");
        Check("V8 …and with none set, \"No budget set\"", vm.Budgets.Count == 1 && vm.Budgets[0].Title == LocalizedResources.Instance["billing.budget.noneTitle"]);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "All");
        // Anthropic API and Claude Dev share a brand colour: on All the plan CLI is the faint "≈" one, so the two stay apart.
        var api = vm.Legend.Single(l => l.Label == "Anthropic API");
        var plan = vm.Legend.Single(l => l.Label == "≈ Claude Dev");
        Check("V9 All chart: Claude Dev is the faint ≈ series, Anthropic API the solid one", api.Color!.Opacity == 1 && plan.Color!.Opacity < 1,
            string.Join(", ", vm.Legend.Select(l => l.Label + "@" + l.Color?.Opacity)));
        Check("V10 no chat list to read: chats are untitled, never called deleted",
            vm.Chats.Count > 0 && vm.Chats.All(c => c.Title == LocalizedResources.Instance["billing.chat.untitled"]), string.Join(" | ", vm.Chats.Select(c => c.Title)));

        // The cache: same log version, range and day → the same snapshot; a tab switch never re-aggregates.
        var snap = vm.Snapshot;
        await vm.RefreshAsync();
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Gemini");
        Check("C1 nothing changed → the cached snapshot is reused (also across a tab switch)", ReferenceEquals(snap, vm.Snapshot));
        vm.RangeDays = 7;
        await WaitUntil(() => !ReferenceEquals(snap, vm.Snapshot), 5000);
        Check("C2 a range change re-aggregates (7 bars)", !ReferenceEquals(snap, vm.Snapshot) && vm.Bars.Count == 7, vm.Bars.Count.ToString());

        // Hidden: new usage marks it stale but nothing runs; shown again, it refreshes.
        var hidden = NewVm(settings, FixtureCost());
        hidden.SetVisible(true);
        await WaitUntil(() => hidden.Snapshot != null, 5000);
        hidden.SetVisible(false);
        var before = hidden.Snapshot;
        hidden.SelectedTab = hidden.Tabs.First();
        var costField = typeof(BillingViewModel).GetField("_cost", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((CostTrackingService)costField.GetValue(hidden)!).Log.Add(Row("2026-11-05T02:00:00Z", "Anthropic", "claude-opus-5-5", UsageCostKind.Billed, 1m));
        await Idle(3600);
        Check("H1 hidden page: new usage does not re-aggregate", ReferenceEquals(before, hidden.Snapshot));
        hidden.SetVisible(true);
        await WaitUntil(() => !ReferenceEquals(before, hidden.Snapshot), 5000);
        Check("H2 shown again: it refreshes with the new row", hidden.Snapshot!.Get("All")!.Today.Spend.Billed == 3m, hidden.Snapshot.Get("All")!.Today.Spend.ToString());
        hidden.SetVisible(false);

        // Off the UI thread: a large log aggregates on a worker; the UI thread is handed back at once.
        var big = new CostTrackingService();
        var rnd = new Random(7);
        var provs = new[] { "Anthropic", "OpenAI", "Gemini", "ClaudeDev", "Ollama" };
        for (int i = 0; i < 150_000; i++)
        {
            string p = provs[i % provs.Length];
            big.Log.Add(Row(Now.AddMinutes(-15 * i).ToString("o"), p, p + "-model-" + (i % 3), ProviderBrand.CostKindOf(p), 0.01m, chat: "chat" + (i % 400)));
        }
        var bvm = NewVm(settings, big);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = bvm.RefreshAsync();
        long handBack = sw.ElapsedMilliseconds;
        bool pendingThen = !task.IsCompleted;
        await task;
        Check("P1 150,000 rows: the UI thread is handed back before the aggregation ends", pendingThen && handBack < 150, $"{handBack} ms to hand back, {sw.ElapsedMilliseconds} ms in all");
        Check("P2 …and the whole refresh stays quick", sw.ElapsedMilliseconds < 4000, sw.ElapsedMilliseconds + " ms");
    }

    // ═══════════════════════════════════════════ pictures ═══════════════════════════════════════════

    static async Task Renders(SettingsService settings)
    {
        Log("\n## Every tab in every theme (pictures)");
        var themes = Sp.GetRequiredService<ThemeService>();
        var vm = NewVm(settings, FixtureCost());
        settings.UpdateSettings(s => { s.DailyBudgetUsd = 5m; s.RunBudgetUsd = 2m; s.CliDailyBudgetUsd = 3m; });
        var view = new BillingView { DataContext = vm };
        var win = new Window
        {
            Content = view, Width = 1360, Height = 1500, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        win.Show();
        await vm.RefreshAsync();
        await Idle(200);
        var shots = new List<string>();
        foreach (var theme in ThemeIds.All)
        {
            themes.Apply(theme, persist: false);
            foreach (var tab in vm.Tabs.ToList())
            {
                vm.SelectedTab = tab;
                await Idle(120);
                string name = $"{theme.ToLowerInvariant()}/billing-{tab.Key.ToLowerInvariant()}.png";
                int colours = SavePng(view, name);
                shots.Add(name);
                bool chartDrawn = Find<CluadeX.Views.Billing.SpendChart>(view).Any(c => c.ActualHeight > 100 && c.Bars?.Count == vm.Bars.Count);
                Check($"G {theme} · {tab.Key}: rendered (not blank) with its chart", colours > 12 && chartDrawn, $"{colours} colours · {name}");
            }
        }
        // The tab strip itself: each logo tile shows the bundled image, the others their badge.
        var images = Find<Image>(view).Where(i => i.Source is BitmapSource && i.ActualWidth > 10).ToList();
        Check("G the tab strip draws a logo image for every tab that has one", images.Count >= vm.Tabs.Count(t => t.HasLogo), $"{images.Count} images");

        // English, Midnight, All.
        LocalizedResources.Instance.Initialize(Sp.GetRequiredService<LocalizationService>());
        Sp.GetRequiredService<LocalizationService>().SetLanguage("en");
        themes.Apply(ThemeIds.Midnight, persist: false);
        var vmEn = NewVm(settings, FixtureCost());
        view.DataContext = vmEn;
        await vmEn.RefreshAsync();
        await Idle(150);
        Check("G English · All rendered", SavePng(view, "midnight/billing-all-en.png") > 12);
        Sp.GetRequiredService<LocalizationService>().SetLanguage("th");

        // Empty state: no usage anywhere.
        var empty = NewVm(settings, new CostTrackingService());
        view.DataContext = empty;
        await empty.RefreshAsync();
        await Idle(150);
        int ec = SavePng(view, "midnight/billing-empty.png");
        var emptyCard = Find<TextBlock>(view).FirstOrDefault(t => t.Text == LocalizedResources.Instance["billing.empty.title"] && t.IsVisible);
        Check("E2 empty state: the page says so (and has only the All tab)", empty.IsEmpty && emptyCard != null && empty.Tabs.Count == 1, $"{ec} colours");
        // The card sits at the top at its own height, not stretched down the whole page.
        var cardBorder = emptyCard == null ? null : Ancestor<Border>(emptyCard);
        Check("E3 …in a compact card, not one stretched to the page height", cardBorder != null && cardBorder.ActualHeight < 300,
            $"{cardBorder?.ActualHeight:0} px");
        themes.Apply(ThemeIds.Lavender, persist: false);
        await Idle(150);
        SavePng(view, "lavender/billing-empty.png");
        themes.Apply(ThemeIds.Midnight, persist: false);
        settings.UpdateSettings(s => { s.DailyBudgetUsd = 0; s.RunBudgetUsd = 2m; s.CliDailyBudgetUsd = 0; });
        win.Close();
        Log($"  {shots.Count + 3} pictures under {Out}");
    }

    // ═══════════════════════════════════════════ static checks ═══════════════════════════════════════════

    static void StaticXaml(LocalizationService loc)
    {
        Log("\n## Static checks (BillingView.xaml, BillingViewModel.cs, MainWindow.xaml)");
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "Views", "BillingView.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("static: repository found next to the harness", false); return; }
        string R(params string[] p) => File.ReadAllText(Path.Combine(new[] { repo, "CluadeX" }.Concat(p).ToArray()));
        string xaml = R("Views", "BillingView.xaml");
        string code = R("ViewModels", "BillingViewModel.cs");
        string main = R("MainWindow.xaml");
        loc.SetLanguage("th");
        var keys = Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(code, @"""(billing\.[\w.]+)""").Select(m => m.Groups[1].Value))
            .Append("nav.billingTip").Distinct().ToList();
        var missing = keys.Where(k => loc.T(k) == k || !Regex.IsMatch(loc.T(k), "[฀-๿]")).ToList();
        Check($"S1 every localized key ({keys.Count}) the page uses has a Thai value", missing.Count == 0, string.Join(", ", missing));
        loc.SetLanguage("en");
        var missingEn = keys.Where(k => loc.T(k) == k).ToList();
        Check("S1 …and an English one", missingEn.Count == 0, string.Join(", ", missingEn));
        loc.SetLanguage("th");
        string resources = R("Resources", "Theme.xaml") + R("App.xaml") + xaml;
        var missingRes = Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("S2 every {StaticResource} exists", missingRes.Count == 0, string.Join(", ", missingRes));
        foreach (var theme in ThemeIds.All)
        {
            string t = R("Resources", "Themes", theme + ".xaml");
            var missingDyn = Regex.Matches(xaml, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
                .Where(k => !t.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
            Check($"S3 every {{DynamicResource}} is a token of {theme}", missingDyn.Count == 0, string.Join(", ", missingDyn));
        }
        var types = new[] { typeof(BillingViewModel), typeof(BillingViewModel.BillingTab), typeof(BillingViewModel.RangeOption), typeof(BillingViewModel.StatCard),
            typeof(BillingViewModel.TokenCell), typeof(BillingViewModel.LegendItem), typeof(BillingViewModel.ModelLine), typeof(BillingViewModel.ChatLine),
            typeof(BillingViewModel.BudgetLine) };
        var bad = Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value)
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p)
            .Select(p => p.Split('.')[0]).Distinct().Where(p => !types.Any(t => t.GetProperty(p) != null)).ToList();
        Check("S4 every {Binding} names a real property", bad.Count == 0, string.Join(", ", bad));
        Check("S5 MainWindow: the Billing page has a DataTemplate and an activity-bar button", main.Contains("vm:BillingViewModel")
            && main.Contains("CommandParameter=\"Billing\"") && main.Contains("{services:Loc nav.billingTip}"));
        string attribution = R("Assets", "Logos", "ATTRIBUTION.md");
        var logos = ProviderBrand.All.Where(b => b.Logo != null).Select(b => b.Logo!).Distinct().ToList();
        Check("S6 every bundled logo is in the repo and named in ATTRIBUTION.md", logos.All(l => File.Exists(Path.Combine(repo, "CluadeX", "Assets", "Logos", l))
            && attribution.Contains("`" + l + "`")), string.Join(",", logos));
    }

    // ═══════════════════════════════════════════ the real window ═══════════════════════════════════════════

    static async Task Navigation()
    {
        Log("\n## The page in the real MainWindow");
        var win = Sp.GetRequiredService<CluadeX.MainWindow>();
        var main = (MainViewModel)win.DataContext;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000; win.Top = -20000; win.Width = 1440; win.Height = 900;
        win.ShowActivated = false; win.ShowInTaskbar = false;
        win.Show();
        await WaitUntil(() => main.ChatVM.CurrentSession != null);
        Check("N1 DI gives MainViewModel the Billing page", main.BillingVM != null);
        var rail = Find<RadioButton>(win).Where(r => r.GroupName == "Activity").ToList();
        var btn = rail.FirstOrDefault(r => r.CommandParameter?.ToString() == "Billing");
        Check("N2 the activity bar has a Billing button", btn != null, string.Join(",", rail.Select(r => r.CommandParameter)));
        if (btn == null) { win.Close(); return; }
        btn.Command.Execute(btn.CommandParameter);
        await Idle(300);
        var view = Find<BillingView>(win).FirstOrDefault(v => v.IsVisible);
        Check("N3 clicking it opens the Billing page", main.SelectedNavItem == "Billing" && view != null, main.SelectedNavItem);
        if (view != null)
        {
            await WaitUntil(() => main.BillingVM!.Snapshot != null, 10000);
            Check("N4 opened with no usage: the empty state", main.BillingVM!.IsEmpty);
            var cost = Sp.GetRequiredService<CostTrackingService>();
            cost.RecordUsage("claude-opus-5-5", 1200, 300);
            await WaitUntil(() => main.BillingVM!.Snapshot?.IsEmpty == false, 10000);
            Check("N5 new usage while the page is open shows within seconds (Anthropic tab appears)",
                main.BillingVM!.Tabs.Any(t => t.Key == "Anthropic"), string.Join(",", main.BillingVM.Tabs.Select(t => t.Key)));
            await Idle(200);
            SavePng((FrameworkElement)win.Content, "midnight/billing-in-app.png");
            cost.FlushLedger();
            string file = Path.Combine(Sp.GetRequiredService<SettingsService>().DataRoot, "usage-log.jsonl");
            Check("N6 the app's log file is in its data folder after a flush", File.Exists(file) && File.ReadAllText(file).Contains("claude-opus-5-5"), file);
        }
        win.Close();
    }
}
