using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Services.BugReports;

namespace CluadeX.Services;

/// <summary>
/// "แจ้งปัญหา" — the user's own words plus what they ticked (log, version and system, settings summary, recent errors,
/// a screenshot, the chat) go to Xman Studio's bug inbox:
///   POST https://xman4289.com/api/v1/product/cluadex-ai-coding-assistant/bug-reports
/// and come back as a ticket (BR-20261109-7KQ2M) the user can quote.
///
/// Every text goes through <see cref="ReportScrubber"/> first. A report that cannot go now (offline, a timeout, the
/// server busy) is kept in &lt;data&gt;\bug-reports\queue and sent again at the next start or the next report; the
/// same client_report_id travels with it, so the server files it once however often it arrives (a double click, a
/// retry after a lost answer). The UI gets a result and a localization key — never an exception's text.
/// </summary>
public class BugReportService
{
    public const string ProductSlug = XmanLicenseService.ProductSlug;

    /// <summary>The server's caps (xmanstudio AppBugReportController), kept a little under so a report always fits.</summary>
    public const int MaxMessageChars = 5000;
    public const int MaxLogChars = 380_000;
    public const int MaxDiagnosticsBytes = 240 * 1024;
    public const int MaxScreenshotBytes = 1500 * 1024;
    public const int MaxChatChars = 60_000;
    public const int MaxQueued = 20;
    public static readonly TimeSpan QueueMaxAge = TimeSpan.FromDays(14);
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    public static readonly string[] Categories = { "bug", "crash", "performance", "connection", "feature_request", "other" };

    private readonly SettingsService? _settings;
    private readonly DebugLogService? _log;
    private readonly string _endpoint;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly string? _dataRoot;

    public BugReportService(SettingsService? settings = null, XmanLicenseService? license = null, DebugLogService? log = null,
        HttpMessageHandler? handler = null, string? endpoint = null, string? dataRoot = null)
    {
        _settings = settings;
        _dataRoot = dataRoot;
        _log = log;
        string baseUrl = license?.BaseUrl ?? "https://xman4289.com/api/v1/product/" + ProductSlug;
        _endpoint = endpoint ?? baseUrl.TrimEnd('/') + "/bug-reports";
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = SendTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"CluadeX/{AutoUpdateService.CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public string Endpoint => _endpoint;

    private string DataRoot => _dataRoot ?? _settings?.DataRoot ?? SettingsService.ResolveDataRoot(out _);
    public string QueueDirectory => Path.Combine(DataRoot, "bug-reports", "queue");
    private string HistoryPath => Path.Combine(DataRoot, "bug-reports", "history.json");

    /// <summary>A new id for one report: the server's duplicate check keys on it. 24 characters, URL-safe.</summary>
    public static string NewClientReportId() => "cx" + Guid.NewGuid().ToString("N")[..22];

    // ─── building ────────────────────────────────────────────────────

    /// <summary>
    /// The request body for <paramref name="draft"/>: only what was ticked, every text scrubbed, every part cut to the
    /// server's caps. Pure apart from the probes the draft already carries — the harness checks it as is.
    /// </summary>
    public static JsonObject BuildPayload(BugReportDraft draft, ScrubContext? scrub = null)
    {
        var ctx = scrub ?? ScrubContext.Current;
        string S(string? t) => ReportScrubber.Scrub(t, ctx);

        string message = S(draft.Message.Trim());
        if (message.Length > MaxMessageChars) message = message[..MaxMessageChars];

        var included = new JsonArray();
        var diag = new JsonObject();
        void Section(string name, bool on, object? value)
        {
            if (!on || value == null) return;
            var node = JsonSerializer.SerializeToNode(value);
            if (node == null) return;
            diag[name] = ScrubNode(node, ctx);
            included.Add(name);
        }
        Section("app", draft.IncludeSystem, draft.App);
        Section("system", draft.IncludeSystem, draft.System);
        Section("display", draft.IncludeSystem, draft.Display);
        Section("process", draft.IncludeSystem, draft.Process);
        Section("settings", draft.IncludeSettings, draft.Settings);
        Section("recent_errors", draft.IncludeErrors, draft.RecentErrors);
        if (draft.IncludeChat && !string.IsNullOrWhiteSpace(draft.ChatText))
        {
            string chat = S(draft.ChatText);
            if (chat.Length > MaxChatChars) chat = "… (older messages left out)\n" + chat[^MaxChatChars..];
            diag["chat"] = chat;
            included.Add("chat");
        }
        // the diagnostics as a whole stay under the server's cap: the biggest optional parts go first
        foreach (var big in new[] { "chat", "recent_errors", "settings", "process", "display" })
        {
            if (Encoding.UTF8.GetByteCount(diag.ToJsonString()) <= MaxDiagnosticsBytes) break;
            if (diag.Remove(big)) diag["truncated"] = (diag["truncated"]?.GetValue<string>() is { } t ? t + "," : "") + big;
        }

        var body = new JsonObject
        {
            ["client_report_id"] = draft.ClientReportId,
            ["machine_id"] = draft.MachineId,
            ["message"] = message,
            ["category"] = Categories.Contains(draft.Category) ? draft.Category : "other",
            ["app_version"] = draft.AppVersion,
            ["os_version"] = draft.OsVersion is { Length: > 150 } os ? os[..150] : draft.OsVersion,
            ["diagnostics"] = diag,
        };
        string? email = draft.ContactEmail?.Trim();
        if (!string.IsNullOrEmpty(email)) body["contact_email"] = email;
        if (draft.LinkLicense && !string.IsNullOrWhiteSpace(draft.LicenseKey)) body["license_key"] = draft.LicenseKey.Trim();
        if (draft.IncludeLog && !string.IsNullOrEmpty(draft.LogText))
        {
            string log = S(draft.LogText);
            if (log.Length > MaxLogChars)
            {
                int cut = log.IndexOf('\n', log.Length - MaxLogChars);
                log = "… (older lines left out)\n" + log[(cut < 0 ? log.Length - MaxLogChars : cut + 1)..];
            }
            body["log_excerpt"] = log;
            included.Add("log");
        }
        if (draft.IncludeScreenshot && draft.Screenshot is { Length: > 0 and <= MaxScreenshotBytes } shot)
        {
            body["screenshot"] = Convert.ToBase64String(shot);
            included.Add("screenshot");
        }
        body["included"] = included;
        return body;
    }

    /// <summary>Scrub every string inside a JSON value (keys too — a dictionary key can be a path).</summary>
    private static JsonNode? ScrubNode(JsonNode? node, ScrubContext ctx)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o.ToList())
                    copy[ReportScrubber.Scrub(k, ctx)] = ScrubNode(v?.DeepClone(), ctx);
                return copy;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var v in a) arr.Add(ScrubNode(v?.DeepClone(), ctx));
                return arr;
            case JsonValue v when v.TryGetValue(out string? s):
                return JsonValue.Create(ReportScrubber.Scrub(s, ctx));
            default:
                return node?.DeepClone();
        }
    }

    // ─── sending ─────────────────────────────────────────────────────

    /// <summary>
    /// Send one report. Sent (or the server already had it): the ticket. No answer, a timeout, 429 or 5xx — and a send
    /// the user stopped by closing the window — : kept in the queue to go again (Queued). A refusal the same report
    /// would get again (validation, too large, unknown product): Rejected, not queued. Never throws but for nothing.
    /// </summary>
    public async Task<BugReportResult> SubmitAsync(JsonObject payload, CancellationToken ct = default)
    {
        string id = payload["client_report_id"]?.GetValue<string>() ?? NewClientReportId();
        var result = await PostAsync(payload, ct).ConfigureAwait(false);
        if (result.Outcome == BugReportOutcome.Queued) Enqueue(id, payload);
        else if (result.Outcome == BugReportOutcome.Sent) Forget(id);
        if (result.Outcome == BugReportOutcome.Sent && result.Ticket != null) Remember(result.Ticket, payload);
        _log?.Info("BugReport", $"report {id}: {result.Outcome}{(result.Ticket != null ? " " + result.Ticket : "")}{(result.ErrorCode != null ? " (" + result.ErrorCode + ")" : "")}");
        return result;
    }

    private async Task<BugReportResult> PostAsync(JsonObject payload, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(payload.ToJsonString(), new UTF8Encoding(false), "application/json");
            using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Classify((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BugReportResult(BugReportOutcome.Queued, null, "bug.result.timeout", "TIMEOUT");   // HttpClient's own timeout
        }
        catch (OperationCanceledException)
        {
            return new BugReportResult(BugReportOutcome.Queued, null, "bug.result.queued", "CANCELLED");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or WebException)
        {
            _log?.Warn("BugReport", "the report did not reach the server", ex);
            return new BugReportResult(BugReportOutcome.Queued, null, "bug.result.offline", "OFFLINE");
        }
    }

    /// <summary>The server's answer → the outcome and the line the UI shows (public for the harness).</summary>
    public static BugReportResult Classify(int status, string? body)
    {
        string? code = null, ticket = null;
        bool duplicate = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    if (d.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String) ticket = t.GetString();
                    if (d.TryGetProperty("duplicate", out var dup) && dup.ValueKind == JsonValueKind.True) duplicate = true;
                }
            }
        }
        catch (JsonException) { code ??= status is >= 200 and < 300 ? null : "NOT_JSON"; }

        if (status is >= 200 and < 300 && !string.IsNullOrEmpty(ticket))
            return new BugReportResult(BugReportOutcome.Sent, ticket, duplicate ? "bug.result.duplicate" : "bug.result.sent", null);
        if (status is >= 200 and < 300)   // a 2xx with no ticket is not the inbox (a captive portal, a proxy page): try again later
            return new BugReportResult(BugReportOutcome.Queued, null, "bug.result.queued", "NO_TICKET");

        return (status, code) switch
        {
            (429, _) or (_, "RATE_LIMIT_EXCEEDED") => new(BugReportOutcome.Queued, null, "bug.result.rateLimited", code ?? "RATE_LIMIT_EXCEEDED"),
            (413, _) or (_, "PAYLOAD_TOO_LARGE") => new(BugReportOutcome.Rejected, null, "bug.result.tooLarge", code ?? "PAYLOAD_TOO_LARGE"),
            (_, "INVALID_SCREENSHOT") => new(BugReportOutcome.Rejected, null, "bug.result.badScreenshot", code),
            (_, "VALIDATION_FAILED") or (422, _) => new(BugReportOutcome.Rejected, null, "bug.result.invalid", code ?? "VALIDATION_FAILED"),
            (_, "PRODUCT_NOT_FOUND") => new(BugReportOutcome.Rejected, null, "bug.result.server", code),
            (404, _) => new(BugReportOutcome.Queued, null, "bug.result.queued", code ?? "NOT_FOUND"),   // the inbox not deployed yet: keep it
            _ when status >= 500 || status == 408 => new(BugReportOutcome.Queued, null, "bug.result.queued", code ?? "SERVER_ERROR"),
            _ => new(BugReportOutcome.Rejected, null, "bug.result.server", code ?? $"HTTP_{status}"),
        };
    }

    // ─── the queue ───────────────────────────────────────────────────

    public int QueuedCount
    {
        get
        {
            try { return Directory.Exists(QueueDirectory) ? Directory.EnumerateFiles(QueueDirectory, "*.json").Count() : 0; }
            catch { return 0; }
        }
    }

    private void Enqueue(string id, JsonObject payload)
    {
        try
        {
            Directory.CreateDirectory(QueueDirectory);
            string path = Path.Combine(QueueDirectory, SafeName(id) + ".json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, payload.ToJsonString(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            // never more than MaxQueued: the oldest go
            foreach (var old in new DirectoryInfo(QueueDirectory).EnumerateFiles("*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxQueued))
                try { old.Delete(); } catch { }
        }
        catch (Exception ex) { _log?.Warn("BugReport", "could not keep the report to send later", ex); }
    }

    private void Forget(string id)
    {
        try { File.Delete(Path.Combine(QueueDirectory, SafeName(id) + ".json")); } catch { }
    }

    private static string SafeName(string id) => new(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());

    /// <summary>Send what is waiting in the queue, oldest first; stops at the first one that still cannot go (still
    /// offline). Reports older than <see cref="QueueMaxAge"/> and ones the server refuses are dropped. Returns the
    /// tickets of the ones that went.</summary>
    public async Task<List<string>> RetryQueuedAsync(CancellationToken ct = default)
    {
        var sent = new List<string>();
        if (!await _queueGate.WaitAsync(0, ct).ConfigureAwait(false)) return sent;   // one retry at a time
        try
        {
            if (!Directory.Exists(QueueDirectory)) return sent;
            foreach (var f in new DirectoryInfo(QueueDirectory).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - f.LastWriteTimeUtc > QueueMaxAge) { TryDelete(f); continue; }
                JsonObject? payload;
                try { payload = JsonNode.Parse(File.ReadAllText(f.FullName)) as JsonObject; }
                catch { payload = null; }
                if (payload == null) { TryDelete(f); continue; }

                var r = await PostAsync(payload, ct).ConfigureAwait(false);
                if (r.Outcome == BugReportOutcome.Queued) break;   // still cannot go: the rest wait too
                TryDelete(f);
                if (r.Outcome == BugReportOutcome.Sent && r.Ticket != null) { Remember(r.Ticket, payload); sent.Add(r.Ticket); }
                _log?.Info("BugReport", $"queued report {Path.GetFileNameWithoutExtension(f.Name)}: {r.Outcome} {r.Ticket ?? r.ErrorCode}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("BugReport", "sending the queued reports failed", ex); }
        finally { _queueGate.Release(); }
        return sent;
    }

    private static void TryDelete(FileInfo f) { try { f.Delete(); } catch { } }

    // ─── the history (tickets the user can quote) ────────────────────

    public List<BugReportHistoryItem> History()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new();
            return JsonSerializer.Deserialize<List<BugReportHistoryItem>>(File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { return new(); }
    }

    private void Remember(string ticket, JsonObject payload)
    {
        try
        {
            var list = History();
            if (list.Any(h => h.Ticket == ticket)) return;
            string msg = payload["message"]?.GetValue<string>() ?? "";
            list.Insert(0, new BugReportHistoryItem
            {
                Ticket = ticket,
                SentUtc = DateTime.UtcNow,
                Category = payload["category"]?.GetValue<string>() ?? "other",
                Summary = msg.Length > 80 ? msg[..80] + "…" : msg,
            });
            if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(list), new UTF8Encoding(false));
        }
        catch { }
    }

    // ─── a text-only report (the connection doctor's "ส่งให้ทีมงาน") ──

    /// <summary>A report with a ready-made text (already free of keys) and no other attachments.</summary>
    public Task<BugReportResult> SubmitTextAsync(string category, string message, IReadOnlyDictionary<string, object?>? extra = null, CancellationToken ct = default)
    {
        var draft = new BugReportDraft
        {
            ClientReportId = NewClientReportId(),
            MachineId = XmanLicenseService.GetMachineFingerprint(),
            Category = category,
            Message = message,
            AppVersion = AutoUpdateService.CurrentVersion,
            OsVersion = Environment.OSVersion.VersionString,
            IncludeSystem = extra != null,
            App = extra,
        };
        return SubmitAsync(BuildPayload(draft), ct);
    }
}

/// <summary>What the user wrote and ticked, plus the probes already taken (the window fills it on the UI thread).</summary>
public sealed class BugReportDraft
{
    public string ClientReportId { get; set; } = BugReportService.NewClientReportId();
    public string MachineId { get; set; } = "";
    public string Category { get; set; } = "bug";
    public string Message { get; set; } = "";
    public string? ContactEmail { get; set; }
    public string AppVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string? LicenseKey { get; set; }
    public bool LinkLicense { get; set; } = true;

    public bool IncludeLog { get; set; } = true;
    public bool IncludeSystem { get; set; } = true;
    public bool IncludeSettings { get; set; } = true;
    public bool IncludeErrors { get; set; } = true;
    public bool IncludeScreenshot { get; set; }
    public bool IncludeChat { get; set; }

    public IReadOnlyDictionary<string, object?>? App { get; set; }
    public IReadOnlyDictionary<string, object?>? System { get; set; }
    public IReadOnlyDictionary<string, object?>? Display { get; set; }
    public IReadOnlyDictionary<string, object?>? Process { get; set; }
    public IReadOnlyDictionary<string, object?>? Settings { get; set; }
    public IReadOnlyList<Dictionary<string, object?>>? RecentErrors { get; set; }
    public string? LogText { get; set; }
    public byte[]? Screenshot { get; set; }
    public string? ChatText { get; set; }
}

public enum BugReportOutcome { Sent, Queued, Rejected }

/// <param name="MessageKey">The localization key of the line the UI shows.</param>
/// <param name="ErrorCode">The server's error_code (or OFFLINE / TIMEOUT / CANCELLED), for the log.</param>
public sealed record BugReportResult(BugReportOutcome Outcome, string? Ticket, string MessageKey, string? ErrorCode);

public sealed class BugReportHistoryItem
{
    public string Ticket { get; set; } = "";
    public DateTime SentUtc { get; set; }
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
}
