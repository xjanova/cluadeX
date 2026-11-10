// --bugreport: "แจ้งปัญหา" (owner 2026-11-09) — what leaves the PC and what happens when it cannot.
// No network: a fake handler stands in for xman4289.com's /api/v1/product/cluadex-ai-coding-assistant/bug-reports.
// Proves: every secret and the user's own names are gone before sending; only what was ticked goes (the chat only when
// ticked); the parts fit the server's caps; the server's answers become the right outcome and line (never an exception's
// text); offline / timeout / 5xx / 429 keep the report in the queue and the next try sends it once with the same id;
// a refusal is not queued; the log files can be cleared and pruned by age and size; the settings summary holds no key.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.BugReports;

static class BugReportWire
{
    /// <summary>Plays the server: each request is recorded and answered by <see cref="Answer"/>.</summary>
    sealed class FakeInbox : HttpMessageHandler
    {
        public readonly List<JsonObject> Bodies = new();
        public Func<JsonObject, HttpResponseMessage> Answer = _ => Ok("BR-20261109-7KQ2M");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
            Bodies.Add(json);
            return Answer(json);
        }
        public static HttpResponseMessage Ok(string ticket, bool duplicate = false) => Json(duplicate ? 200 : 201,
            $"{{\"success\":true,\"message\":\"ได้รับรายงานแล้ว\",\"error_code\":null,\"data\":{{\"ticket\":\"{ticket}\",\"status\":\"new\",\"duplicate\":{(duplicate ? "true" : "false")}}}}}");
        public static HttpResponseMessage Fail(int status, string code) => Json(status, $"{{\"success\":false,\"message\":\"x\",\"error_code\":\"{code}\",\"data\":null}}");
        public static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        string root = Path.Combine(Path.GetTempPath(), "cx-apitest-bug-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Scrubber(check, section);
            Payload(check, section);
            Answers(check, section);
            await Queue(check, section, root);
            Settings(check, section);
            LogFiles(check, section, root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ─── the scrubber ────────────────────────────────────────────────

    static void Scrubber(Action<string, string, bool, string> check, Action<string> section)
    {
        section("bug report · secrets and names never leave the PC");
        var ctx = new ScrubContext(new[] { (@"C:\Users\somchai", "%USERPROFILE%"), (@"D:\Data\cx", "%DATA%") },
            new[] { ("somchai", "[USER]"), ("SOMCHAI-PC", "[PC]"), ("Somchai Jaidee", "[NAME]") });
        string dirty = string.Join("\n",
            "anthropic sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx-123456 openai sk-proj-ZYXWVUTSRQPONMLKJIH0987",
            "google AIzaSyA1234567890abcdefghijklmnopqrstu github ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "groq gsk_abcdefghijklmnopqrstuvwxyz012345 hf hf_abcdefghijklmnopqrstuvwxyz aws AKIAIOSFODNN7EXAMPLE",
            "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
            "{\"api_key\": \"plain-secret-value\", \"password\":\"hunter2\"} token=abcdef123456 &license_key=ABCD-EFGH-IJKL-MNOP",
            "license TRIAL-7F3A-99QX and ABCDE-FGHIJ-KLMNO-PQRST · device 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----",
            @"path C:\Users\somchai\.cluadex\logs\x.log and C:/Users/somchai/Desktop and D:\Data\cx\bug-reports",
            "user somchai on SOMCHAI-PC, signed in as Somchai Jaidee, mail somchai.j@gmail.com");
        string clean = ReportScrubber.Scrub(dirty, ctx);
        string[] gone = { "sk-ant-api03", "sk-proj-ZYX", "AIzaSyA", "ghp_ABC", "gsk_abc", "hf_abc", "AKIAIOSFODNN7", "eyJhbGci", "plain-secret-value",
            "hunter2", "abcdef123456", "ABCD-EFGH", "TRIAL-7F3A", "ABCDE-FGHIJ", "0123456789abcdef0123", "MIIEpAIBAAKCAQEA", "somchai", "SOMCHAI-PC",
            "Somchai Jaidee", "@gmail.com", @"C:\Users", "C:/Users" };
        var left = gone.Where(g => clean.Contains(g, StringComparison.OrdinalIgnoreCase)).ToList();
        check("bug-scrub", "API keys, tokens, JWTs, passwords, license keys, the machine id, private keys, e-mails, the user's name, PC and paths are gone",
            left.Count == 0, left.Count == 0 ? "" : "left: " + string.Join(", ", left) + "\n" + clean);
        check("bug-scrub", "the user's folders become %USERPROFILE% / %DATA%, so a path still reads",
            clean.Contains(@"%USERPROFILE%\.cluadex\logs\x.log") && clean.Contains("%USERPROFILE%/Desktop") && clean.Contains(@"%DATA%\bug-reports"), clean);
        check("bug-scrub", "a secret-named field keeps its name, so the developer knows what was there",
            clean.Contains("\"api_key\": \"[REDACTED]") && clean.Contains("token=[REDACTED]"), clean);

        string keep = "v3.0.278 · build 26100.2894 · ticket BR-20261109-7KQ2M · id 3F2504E0-4F89-11D3-9A0C-0305E82C3301 · sha 1a2b3c4 · 12.5 GB · HTTP 503";
        string kept = ReportScrubber.Scrub(keep, ctx);
        check("bug-scrub", "versions, ticket numbers, GUIDs, short hashes and numbers stay — what a developer needs", kept == keep, kept);
        check("bug-scrub", "empty and null stay empty", ReportScrubber.Scrub(null, ctx) == "" && ReportScrubber.Scrub("", ctx) == "", "");
    }

    // ─── the payload ─────────────────────────────────────────────────

    static BugReportDraft Draft() => new()
    {
        ClientReportId = "cxtest0000000000000001",
        MachineId = new string('a', 64),
        Category = "crash",
        Message = "  แอปค้างตอนกดส่ง key sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx-123456  ",
        ContactEmail = "me@example.com",
        AppVersion = "3.0.278",
        OsVersion = "Windows 11 Pro 24H2 (build 26100.2894)",
        LicenseKey = "ABCD-EFGH-IJKL-MNOP",
        App = new Dictionary<string, object?> { ["version"] = "3.0.278", ["data_root"] = @"C:\Users\somchai\.cluadex" },
        System = new Dictionary<string, object?> { ["cpu"] = "AMD Ryzen 9", ["ram_total_gb"] = 31.9 },
        Process = new Dictionary<string, object?> { ["handles"] = 1234 },
        Settings = new Dictionary<string, object?> { ["Theme"] = "Midnight", ["AutoUpdateEnabled"] = true },
        RecentErrors = new List<Dictionary<string, object?>> { new() { ["message"] = "POST failed Bearer abcdefghijklmnop" } },
        LogText = "2026-11-09 10:00:00.000  [INFO] [Chat] hello somchai\n2026-11-09 10:00:01.000  [ERROR] [Net] x-api-key: sk-proj-ZYXWVUTSRQPONMLKJIH0987",
        Screenshot = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 },
        ChatText = "[User] my secret plan",
    };

    static void Payload(Action<string, string, bool, string> check, Action<string> section)
    {
        section("bug report · only what was ticked goes, scrubbed and within the caps");
        var ctx = new ScrubContext(new[] { (@"C:\Users\somchai", "%USERPROFILE%") }, new[] { ("somchai", "[USER]") });
        var p = BugReportService.BuildPayload(Draft(), ctx);
        string all = p.ToJsonString();
        check("bug-payload", "the server's fields: id, machine, message (trimmed), category, version, OS, e-mail, license (to link the account)",
            p["client_report_id"]?.GetValue<string>() == "cxtest0000000000000001" && p["machine_id"]?.GetValue<string>()?.Length == 64
            && p["message"]?.GetValue<string>()?.StartsWith("แอปค้างตอนกดส่ง") == true && p["category"]?.GetValue<string>() == "crash"
            && p["app_version"]?.GetValue<string>() == "3.0.278" && p["contact_email"]?.GetValue<string>() == "me@example.com"
            && p["license_key"]?.GetValue<string>() == "ABCD-EFGH-IJKL-MNOP", all);
        check("bug-payload", "no key, no user name, no path in any part — message, diagnostics, log",
            !all.Contains("sk-ant") && !all.Contains("sk-proj") && !all.Contains("abcdefghijklmnop") && !all.Contains("somchai")
            && all.Contains("%USERPROFILE%"), all);
        check("bug-payload", "the chat is not sent unless ticked; the screenshot neither",
            p["diagnostics"]?["chat"] == null && p["screenshot"] == null
            && !(p["included"]?.AsArray().Any(n => n?.GetValue<string>() is "chat" or "screenshot") ?? true), all);
        var included = p["included"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        check("bug-payload", "included lists what went: app, system, process, settings, recent_errors, log",
            new[] { "app", "system", "process", "settings", "recent_errors", "log" }.All(included.Contains), string.Join(",", included));

        var d = Draft();
        d.IncludeChat = true; d.IncludeScreenshot = true; d.LinkLicense = false;
        d.IncludeLog = false; d.IncludeSettings = false; d.IncludeErrors = false; d.IncludeSystem = false;
        var q = BugReportService.BuildPayload(d, ctx);
        check("bug-payload", "ticked: the chat and the screenshot go; unticked: the log, settings, errors, system and the license do not",
            q["diagnostics"]?["chat"]?.GetValue<string>() == "[User] my secret plan" && q["screenshot"]?.GetValue<string>() == Convert.ToBase64String(d.Screenshot!)
            && q["log_excerpt"] == null && q["diagnostics"]?["settings"] == null && q["diagnostics"]?["recent_errors"] == null
            && q["diagnostics"]?["system"] == null && q["license_key"] == null, q.ToJsonString());

        var big = Draft();
        big.Message = new string('ก', 9000);
        big.LogText = string.Join("\n", Enumerable.Range(0, 40000).Select(i => $"2026-11-09 10:00:00.000  [INFO] [X] line {i} padding padding padding"));
        big.Category = "not-a-category";
        big.Screenshot = new byte[BugReportService.MaxScreenshotBytes + 1];
        big.IncludeScreenshot = true;
        var r = BugReportService.BuildPayload(big, ctx);
        string log = r["log_excerpt"]?.GetValue<string>() ?? "";
        check("bug-payload", "over the caps: the message is cut to 5000, the log keeps its newest lines, an unknown category is 'other', a too-big screenshot stays home",
            r["message"]!.GetValue<string>().Length == BugReportService.MaxMessageChars && log.Length <= BugReportService.MaxLogChars + 40
            && log.Contains("line 39999") && !log.Contains("line 0 ") && r["category"]!.GetValue<string>() == "other" && r["screenshot"] == null,
            $"message {r["message"]!.GetValue<string>().Length}, log {log.Length}");
        check("bug-payload", "the whole request fits the server's 3 MB", Encoding.UTF8.GetByteCount(r.ToJsonString()) < 3 * 1024 * 1024, "");
    }

    // ─── the server's answers ────────────────────────────────────────

    static void Answers(Action<string, string, bool, string> check, Action<string> section)
    {
        section("bug report · the server's answer → what the user reads");
        var sent = BugReportService.Classify(201, "{\"success\":true,\"data\":{\"ticket\":\"BR-20261109-7KQ2M\",\"duplicate\":false}}");
        var dup = BugReportService.Classify(200, "{\"success\":true,\"data\":{\"ticket\":\"BR-20261109-7KQ2M\",\"duplicate\":true}}");
        check("bug-answer", "201 → sent with the ticket; a duplicate → sent, the same ticket, its own line",
            sent is { Outcome: BugReportOutcome.Sent, Ticket: "BR-20261109-7KQ2M", MessageKey: "bug.result.sent" }
            && dup is { Outcome: BugReportOutcome.Sent, Ticket: "BR-20261109-7KQ2M", MessageKey: "bug.result.duplicate" }, $"{sent} {dup}");
        var cases = new (int Status, string Body, BugReportOutcome Outcome, string Key)[]
        {
            (429, "{\"error_code\":\"RATE_LIMIT_EXCEEDED\"}", BugReportOutcome.Queued, "bug.result.rateLimited"),
            (500, "{\"error_code\":\"SERVER_ERROR\"}", BugReportOutcome.Queued, "bug.result.queued"),
            (503, "<html>Service Unavailable</html>", BugReportOutcome.Queued, "bug.result.queued"),
            (404, "<html>Not Found</html>", BugReportOutcome.Queued, "bug.result.queued"),
            (200, "<html>Please log in to the hotel Wi-Fi</html>", BugReportOutcome.Queued, "bug.result.queued"),
            (413, "{\"error_code\":\"PAYLOAD_TOO_LARGE\"}", BugReportOutcome.Rejected, "bug.result.tooLarge"),
            (422, "{\"error_code\":\"INVALID_SCREENSHOT\"}", BugReportOutcome.Rejected, "bug.result.badScreenshot"),
            (422, "{\"error_code\":\"VALIDATION_FAILED\"}", BugReportOutcome.Rejected, "bug.result.invalid"),
            (404, "{\"error_code\":\"PRODUCT_NOT_FOUND\"}", BugReportOutcome.Rejected, "bug.result.server"),
        };
        var wrong = cases.Select(c => (c, r: BugReportService.Classify(c.Status, c.Body))).Where(x => x.r.Outcome != x.c.Outcome || x.r.MessageKey != x.c.Key).ToList();
        check("bug-answer", "busy / down / not deployed / a portal page → kept to send later; a refusal the same report would get again → not kept",
            wrong.Count == 0, string.Join("; ", wrong.Select(w => $"{w.c.Status} {w.c.Body} → {w.r.Outcome} {w.r.MessageKey}")));

        var loc = new LocalizationService();
        var keys = cases.Select(c => c.Key).Concat(new[] { "bug.result.sent", "bug.result.duplicate", "bug.result.offline", "bug.result.timeout" }).Distinct();
        var missing = new List<string>();
        foreach (var lang in new[] { "en", "th" })
        {
            loc.SetLanguage(lang);
            missing.AddRange(keys.Where(k => loc.T(k) == k).Select(k => lang + ":" + k));
        }
        loc.SetLanguage("en");
        check("bug-answer", "every line the user can get is written in English and Thai", missing.Count == 0, string.Join(", ", missing));
    }

    // ─── the queue ───────────────────────────────────────────────────

    static async Task Queue(Action<string, string, bool, string> check, Action<string> section, string root)
    {
        section("bug report · offline, timeouts and double clicks");
        var inbox = new FakeInbox();
        var svc = new BugReportService(handler: inbox, endpoint: "https://inbox.test/bug-reports", dataRoot: root);
        var ctx = new ScrubContext(Array.Empty<(string, string)>(), Array.Empty<(string, string)>());

        var first = await svc.SubmitAsync(BugReportService.BuildPayload(Draft(), ctx));
        check("bug-queue", "sent: the ticket comes back, nothing waits, the ticket is in the history",
            first.Outcome == BugReportOutcome.Sent && first.Ticket == "BR-20261109-7KQ2M" && svc.QueuedCount == 0
            && svc.History().FirstOrDefault()?.Ticket == "BR-20261109-7KQ2M", $"{first} queued={svc.QueuedCount}");

        inbox.Answer = _ => throw new HttpRequestException("No such host is known. (inbox.test:443)");
        var d = Draft(); d.ClientReportId = "cxtest0000000000000002";
        var offline = await svc.SubmitAsync(BugReportService.BuildPayload(d, ctx));
        check("bug-queue", "offline: kept to send later, the line says so — no exception text reaches the user",
            offline is { Outcome: BugReportOutcome.Queued, MessageKey: "bug.result.offline" } && svc.QueuedCount == 1, $"{offline} queued={svc.QueuedCount}");
        // the same report again while still offline (a second click on a window that was reopened): still one in the queue
        await svc.SubmitAsync(BugReportService.BuildPayload(d, ctx));
        check("bug-queue", "the same report twice while offline is one in the queue", svc.QueuedCount == 1, $"queued={svc.QueuedCount}");

        inbox.Answer = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        var e = Draft(); e.ClientReportId = "cxtest0000000000000003";
        var slow = await svc.SubmitAsync(BugReportService.BuildPayload(e, ctx));
        check("bug-queue", "a timeout: kept, its own line", slow is { Outcome: BugReportOutcome.Queued, MessageKey: "bug.result.timeout" } && svc.QueuedCount == 2, $"{slow}");

        using (var cts = new CancellationTokenSource())
        {
            inbox.Answer = _ => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return FakeInbox.Ok("never"); };
            var f = Draft(); f.ClientReportId = "cxtest0000000000000004";
            var closed = await svc.SubmitAsync(BugReportService.BuildPayload(f, ctx), cts.Token);
            check("bug-queue", "the window closed mid-send: kept (it may have arrived — the id makes a second arrival harmless)",
                closed.Outcome == BugReportOutcome.Queued && svc.QueuedCount == 3, $"{closed}");
        }

        int before = inbox.Bodies.Count;
        inbox.Answer = body => body["client_report_id"]?.GetValue<string>() == "cxtest0000000000000003"
            ? FakeInbox.Fail(503, "SERVER_ERROR") : FakeInbox.Ok("BR-20261109-" + body["client_report_id"]!.GetValue<string>()[^5..].ToUpperInvariant());
        var tickets = await svc.RetryQueuedAsync();
        check("bug-queue", "back online: the queue goes oldest first and stops at the first one that still cannot go",
            tickets.SequenceEqual(new[] { "BR-20261109-00002" }) && svc.QueuedCount == 2 && inbox.Bodies.Count - before == 2,
            $"tickets={string.Join(",", tickets)} queued={svc.QueuedCount} sent={inbox.Bodies.Count - before}");
        check("bug-queue", "a retried report carries the id it was first sent with",
            inbox.Bodies.Skip(before).First()["client_report_id"]?.GetValue<string>() == "cxtest0000000000000002", "");

        inbox.Answer = body => FakeInbox.Ok("BR-20261109-ZZZZZ", duplicate: true);
        tickets = await svc.RetryQueuedAsync();
        check("bug-queue", "the rest go once the server answers; a duplicate counts as sent",
            tickets.Count == 2 && svc.QueuedCount == 0, $"tickets={string.Join(",", tickets)} queued={svc.QueuedCount}");

        inbox.Answer = _ => FakeInbox.Fail(422, "VALIDATION_FAILED");
        var g = Draft(); g.ClientReportId = "cxtest0000000000000005";
        var refused = await svc.SubmitAsync(BugReportService.BuildPayload(g, ctx));
        check("bug-queue", "a refusal is not kept (it would be refused again)", refused.Outcome == BugReportOutcome.Rejected && svc.QueuedCount == 0, $"{refused}");

        // two retries at once (the start-up retry and the window's) send each report once
        inbox.Answer = _ => throw new HttpRequestException("offline");
        foreach (int i in Enumerable.Range(6, 3))
        {
            var h = Draft(); h.ClientReportId = $"cxtest000000000000000{i}";
            await svc.SubmitAsync(BugReportService.BuildPayload(h, ctx));
        }
        before = inbox.Bodies.Count;
        inbox.Answer = body => { Thread.Sleep(30); return FakeInbox.Ok("BR-20261109-AAAAA"); };
        await Task.WhenAll(svc.RetryQueuedAsync(), svc.RetryQueuedAsync(), svc.RetryQueuedAsync());
        var ids = inbox.Bodies.Skip(before).Select(b => b["client_report_id"]!.GetValue<string>()).ToList();
        check("bug-queue", "three retries at the same moment send each queued report once",
            ids.Count == 3 && ids.Distinct().Count() == 3 && svc.QueuedCount == 0, string.Join(",", ids));

        var text = await new BugReportService(handler: new FakeInbox(), endpoint: "https://inbox.test/bug-reports", dataRoot: root)
            .SubmitTextAsync("connection", "Connection report CX-20261109-7F3A\nproxy: none · key DEMO-AB12-CD34-WX99");
        check("bug-queue", "the connection doctor's report goes the same way, scrubbed", text.Outcome == BugReportOutcome.Sent, $"{text}");
    }

    // ─── the settings summary ────────────────────────────────────────

    static void Settings(Action<string, string, bool, string> check, Action<string> section)
    {
        section("bug report · the settings summary");
        var s = new AppSettings
        {
            ActivationKey = "ABCD-EFGH-IJKL-MNOP", HuggingFaceToken = "hf_abcdefghijklmnopqrstuvwxyz", AccountEmailMasked = "so***@gmail.com",
            AccountName = "Somchai Jaidee", ModelDirectory = @"C:\Users\somchai\models", Theme = "Midnight", Language = "th",
        };
        var d = DiagnosticsCollector.Settings(s);
        string all = JsonSerializer.Serialize(d);
        check("bug-settings", "switches, numbers and choices are there (theme, language, context size, auto-update)",
            d.TryGetValue("Theme", out var t) && (string?)t == "Midnight" && d.TryGetValue("Language", out var l) && (string?)l == "th"
            && d.ContainsKey("ContextSize") && d.ContainsKey("AutoUpdateEnabled"), all.Length > 400 ? all[..400] : all);
        check("bug-settings", "no key, token, license, e-mail, account name or folder — not even their names' values",
            !all.Contains("ABCD-EFGH") && !all.Contains("hf_abc") && !all.Contains("gmail") && !all.Contains("Somchai") && !all.Contains("somchai")
            && !d.ContainsKey("ActivationKey") && !d.ContainsKey("HuggingFaceToken") && !d.ContainsKey("AccountName"), all);
        check("bug-settings", "a folder setting says only whether it is set", !d.TryGetValue("ModelDirectory", out var md) || (string?)md is "(set)" or "(empty)", $"{md}");

        var sys = DiagnosticsCollector.System(null);
        check("bug-settings", "the PC's details: Windows, .NET, CPU, cores, memory", sys.ContainsKey("os") && sys.ContainsKey("dotnet") && sys.ContainsKey("cpu_logical_cores") && sys.ContainsKey("ram_total_gb"),
            string.Join(", ", sys.Keys));
        var proc = DiagnosticsCollector.ProcessInfo();
        check("bug-settings", "the process: memory, threads, handles", proc.ContainsKey("working_set_mb") && proc.ContainsKey("threads") && proc.ContainsKey("handles"), string.Join(", ", proc.Keys));
    }

    // ─── the log files ───────────────────────────────────────────────

    static void LogFiles(Action<string, string, bool, string> check, Action<string> section, string root)
    {
        section("debug log · clearing by the user and by itself");
        // a log folder of its own under the test's temp root — the real logs are never touched
        string dir = Path.Combine(root, "logs");
        var log = new DebugLogService(dir);
        string Make(int daysOld, int kb)
        {
            string p = Path.Combine(dir, $"cluadex-t{Guid.NewGuid():N}.log");
            File.WriteAllBytes(p, new byte[kb * 1024]);
            File.SetLastWriteTime(p, DateTime.Now.AddDays(-daysOld));
            return p;
        }
        var oldOne = Make(20, 1);
        var newOne = Make(1, 1);
        log.RetentionDays = 14;
        log.MaxTotalBytes = 0;
        log.PruneFiles();
        check("dbglog-files", "keep 14 days: a 20-day-old file goes, a 1-day-old one stays", !File.Exists(oldOne) && File.Exists(newOne), "");

        log.RetentionDays = 0;
        var a = Make(5, 300); var b = Make(4, 300); var c = Make(3, 300);
        long cap = log.MeasureFiles().Bytes - 300 * 1024 + 1024;   // room for everything but one 300 KB file
        log.MaxTotalBytes = cap;
        log.PruneFiles();
        check("dbglog-files", "over the size cap: the oldest goes first, the newer ones stay", !File.Exists(a) && File.Exists(b) && File.Exists(c), "");

        log.MaxTotalBytes = 0;
        var crashDir = Path.Combine(root, "crash-logs");
        Directory.CreateDirectory(crashDir);
        File.WriteAllText(Path.Combine(crashDir, "crash-1.log"), "x");
        File.WriteAllText(Path.Combine(crashDir, "keep.txt"), "not a log");
        log.Info("Test", "a line before the clear");
        var (n, bytes) = log.DeleteLogFiles(crashDir);
        check("dbglog-files", "\"ล้างไฟล์ล็อก\": every log file and crash log goes, other files stay, the size is reported",
            n >= 3 && bytes > 0 && log.MeasureFiles().Files == 0 && !File.Exists(Path.Combine(crashDir, "crash-1.log")) && File.Exists(Path.Combine(crashDir, "keep.txt")),
            $"{n} files, {bytes} B");
        check("dbglog-files", "after the clear the list starts again with one line that says so",
            log.Snapshot().Count == 1 && log.Snapshot()[0].Message.Contains("cleared"), string.Join(" | ", log.Snapshot().Select(e => e.Message)));
        log.Info("Test", "a line after the clear");
        log.Flush();
        check("dbglog-files", "logging goes on into a fresh file with its header", log.MeasureFiles().Files == 1, "");
        log.DeleteLogFiles();

        log.Verbose = true;
        bool traced = log.MinimumLevel == LogLevel.Trace;
        log.Verbose = false;
        check("dbglog-files", "detailed logging lowers the level to Trace and back", traced && log.MinimumLevel == LogLevel.Debug, "");
    }
}
