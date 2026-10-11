// Batch mode (2026-10-10): work nobody waits for, and chat questions sent with the Batch switch, go through the
// provider's batch API at half price. Checked here against the REAL providers and BatchService, on a localhost mock:
// the wire format of Anthropic Message Batches and OpenAI Batch (files → batches → output file), that results and
// errors come back per request, that the cost is recorded at half price, and that a job survives a restart and is
// delivered exactly once.
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Batch;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class BatchWire
{
    static JsonElement Json(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }

    const string AnthResultOk =
        "{\"custom_id\":\"a\",\"result\":{\"type\":\"succeeded\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\"," +
        "\"model\":\"claude-opus-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"ANSWER-1\"}],\"stop_reason\":\"end_turn\"," +
        "\"usage\":{\"input_tokens\":1000,\"output_tokens\":200}}}}";
    const string AnthResultErr =
        "{\"custom_id\":\"b\",\"result\":{\"type\":\"errored\",\"error\":{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"bad thing\"}}}}";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · batch API (half price, the answer comes later)");
        using var mock = new WireMock();
        check("batch", "batch mock proves its marker before any request", await mock.ProveAsync(), mock.Root);

        // ── state the mock serves ──
        string anthState = "in_progress", oaState = "in_progress";
        bool anthStatus500 = false, foreignResults = false;
        string anthResults = AnthResultOk + "\n" + AnthResultErr + "\n";
        mock.Responder = (method, path, body) =>
        {
            // Anthropic
            if (method == "POST" && path == "/v1/messages/batches")
                return (200, "application/json", "{\"id\":\"msgbatch_1\",\"type\":\"message_batch\",\"processing_status\":\"in_progress\",\"request_counts\":{\"processing\":2}}");
            if (method == "GET" && path == "/v1/messages/batches/msgbatch_1")
            {
                if (anthStatus500) return (500, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"overloaded\"}}");
                return anthState == "ended"
                    ? (200, "application/json", $"{{\"id\":\"msgbatch_1\",\"processing_status\":\"ended\",\"request_counts\":{{\"processing\":0,\"succeeded\":1,\"errored\":1,\"canceled\":0,\"expired\":0}},\"results_url\":\"{(foreignResults ? mock.Root.Replace("localhost", "127.0.0.1") : mock.Root)}/v1/messages/batches/msgbatch_1/results\"}}")
                    : (200, "application/json", "{\"id\":\"msgbatch_1\",\"processing_status\":\"in_progress\",\"request_counts\":{\"processing\":2,\"succeeded\":0,\"errored\":0},\"results_url\":null}");
            }
            if (method == "GET" && path == "/v1/messages/batches/msgbatch_1/results")
                return (200, "application/binary", anthResults);
            if (method == "POST" && path == "/v1/messages/batches/msgbatch_1/cancel")
                return (200, "application/json", "{\"id\":\"msgbatch_1\",\"processing_status\":\"canceling\"}");
            // OpenAI
            if (method == "POST" && path == "/v1/files")
                return (200, "application/json", "{\"id\":\"file-in\",\"object\":\"file\",\"purpose\":\"batch\"}");
            if (method == "POST" && path == "/v1/batches")
                return (200, "application/json", "{\"id\":\"batch_1\",\"object\":\"batch\",\"status\":\"validating\"}");
            if (method == "GET" && path == "/v1/batches/batch_1")
                return oaState == "completed"
                    ? (200, "application/json", "{\"id\":\"batch_1\",\"status\":\"completed\",\"output_file_id\":\"file-out\",\"error_file_id\":\"file-err\",\"request_counts\":{\"total\":2,\"completed\":1,\"failed\":1}}")
                    : (200, "application/json", "{\"id\":\"batch_1\",\"status\":\"in_progress\",\"output_file_id\":null,\"error_file_id\":null,\"request_counts\":{\"total\":2,\"completed\":0,\"failed\":0}}");
            if (method == "GET" && path == "/v1/files/file-out/content")
                return (200, "application/jsonl", "{\"id\":\"r1\",\"custom_id\":\"a\",\"response\":{\"status_code\":200,\"request_id\":\"q1\",\"body\":{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OA-ANSWER\"}}],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100,\"prompt_tokens_details\":{\"cached_tokens\":200}}}},\"error\":null}\n");
            if (method == "GET" && path == "/v1/files/file-err/content")
                return (200, "application/jsonl", "{\"id\":\"r2\",\"custom_id\":\"b\",\"response\":{\"status_code\":400,\"request_id\":\"q2\",\"body\":{\"error\":{\"message\":\"oa bad\"}}},\"error\":null}\n");
            if (method == "POST" && path == "/v1/batches/batch_1/cancel")
                return (200, "application/json", "{\"id\":\"batch_1\",\"status\":\"cancelling\"}");
            return (404, "application/json", "{\"error\":{\"message\":\"no route\"}}");
        };

        var svc = new SettingsService();
        var reqs = new List<BatchRequest>
        {
            new("a", "SYS-PROMPT", [new ChatMessage { Role = MessageRole.User, Content = "earlier q" }, new ChatMessage { Role = MessageRole.Assistant, Content = "earlier a" }], "QUESTION-A"),
            new("b", null, [], "QUESTION-B"),
        };

        // ── Anthropic Message Batches ──
        ImageWireGolden.Pin(svc.Settings, "claude-opus-5-5", mock.Root);
        svc.Settings.ExtendedThinkingEnabled = true;   // block_binding must not go into a batch
        var anthCost = new CostTrackingService();
        var anth = new AnthropicProvider(svc, anthCost);
        check("batch", "Anthropic: SupportsBatch with a key; the batch runs on the configured model",
            anth.SupportsBatch && anth.BatchModel == "claude-opus-5-5", anth.BatchModel);
        string id = await anth.SubmitBatchAsync(reqs);
        var sub = Json(mock.Last("/v1/messages/batches"));
        var items = sub.GetProperty("requests").EnumerateArray().ToList();
        var p0 = items[0].GetProperty("params");
        var msgs0 = p0.GetProperty("messages").EnumerateArray().ToList();
        var lastContent = msgs0[^1].GetProperty("content");
        string lastUser = lastContent.ValueKind == JsonValueKind.String ? lastContent.GetString() ?? ""
            : string.Concat(lastContent.EnumerateArray().Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : ""));
        check("batch", "Anthropic submit: one item per request (custom_id + params with model, system, history and the question)",
            id == "msgbatch_1" && items.Count == 2 && items[0].GetProperty("custom_id").GetString() == "a"
            && p0.GetProperty("model").GetString() == "claude-opus-5-5" && p0.GetProperty("system").GetRawText().Contains("SYS-PROMPT")
            && msgs0.Count == 3 && lastUser == "QUESTION-A" && !items[1].GetProperty("params").TryGetProperty("system", out _),
            $"items={items.Count} messages={msgs0.Count} last='{lastUser}'");
        string subRaw = mock.Last("/v1/messages/batches");
        check("batch", "Anthropic submit: no stream flag and no thinking block_binding (thinking itself kept)",
            !subRaw.Contains("\"stream\"") && !subRaw.Contains("block_binding") && p0.TryGetProperty("thinking", out _), Trunc(p0.TryGetProperty("thinking", out var th) ? th.GetRawText() : "(no thinking)", 120));

        var st = await anth.GetBatchStatusAsync(id);
        check("batch", "Anthropic status: in_progress → still running", st.State == BatchState.InProgress && st.Pending == 2, $"{st.State} pending={st.Pending}");
        anthState = "ended";
        st = await anth.GetBatchStatusAsync(id);
        var res = await anth.GetBatchResultsAsync(id);
        var ra = res.FirstOrDefault(r => r.CustomId == "a");
        var rb = res.FirstOrDefault(r => r.CustomId == "b");
        check("batch", "Anthropic results: the answer per custom_id; an errored item carries the API's reason",
            st.State == BatchState.Ended && ra is { Ok: true, Text: "ANSWER-1" } && rb is { Ok: false } && rb.Error == "bad thing",
            $"a={ra?.Text} b={rb?.Error}");
        var live = new CostTrackingService();
        live.RecordUsage("claude-opus-5-5", 1000, 200, 0, 0);
        check("batch", "Anthropic cost: recorded at exactly half the live price for the same usage",
            anthCost.TotalCostUsd > 0 && anthCost.TotalCostUsd == live.TotalCostUsd * 0.5m, $"batch ${anthCost.TotalCostUsd} · live ${live.TotalCostUsd}");
        foreignResults = true;
        int resultsBefore = mock.Count("/msgbatch_1/results");
        BatchException? foreign = null;
        try { await anth.GetBatchResultsAsync(id); } catch (BatchException ex) { foreign = ex; }
        check("batch", "Anthropic: a results_url on another host is refused — the API key is never sent there",
            foreign != null && mock.Count("/msgbatch_1/results") == resultsBefore, foreign?.Message ?? "(fetched it)");
        foreignResults = false;
        await anth.CancelBatchAsync(id);
        check("batch", "Anthropic cancel: POST …/cancel", mock.Count("/v1/messages/batches/msgbatch_1/cancel") == 1, "");

        // ── OpenAI Batch: JSONL file → batch → output + error files ──
        svc.Settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-batchwire", BaseUrl = mock.Root + "/v1", SelectedModel = "gpt-5.5" };
        svc.Settings.ReasoningEffort = "auto";
        svc.Settings.ExtendedThinkingEnabled = false;
        var oaCost = new CostTrackingService();
        var oa = new OpenAiProvider(svc, oaCost);
        check("batch", "OpenAI: no batch through a custom BaseUrl (proxies rarely have /batches)", !oa.SupportsBatch, "");
        svc.Settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-batchwire", SelectedModel = "gpt-5.5" };
        bool official = oa.SupportsBatch;
        svc.Settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-batchwire", BaseUrl = mock.Root + "/v1", SelectedModel = "gpt-5.5" };
        check("batch", "OpenAI: batch on OpenAI's own API with a key", official, "");

        string oid = await oa.SubmitBatchAsync(reqs);
        string upload = mock.Last("/v1/files");
        var lines = upload.Split('\n').Where(l => l.TrimStart().StartsWith("{\"custom_id\"")).Select(l => Json(l.Trim())).ToList();
        var b0 = lines.Count > 0 ? lines[0].GetProperty("body") : default;
        check("batch", "OpenAI upload: multipart purpose=batch with one JSONL line per request (POST /v1/chat/completions)",
            upload.Contains("name=purpose") && upload.Contains("batch") && lines.Count == 2
            && lines.All(l => l.GetProperty("method").GetString() == "POST" && l.GetProperty("url").GetString() == "/v1/chat/completions"),
            $"lines={lines.Count}");
        check("batch", "OpenAI line body: model, system + history + question; a reasoning model gets max_completion_tokens, no temperature, no stream",
            b0.ValueKind == JsonValueKind.Object && b0.GetProperty("model").GetString() == "gpt-5.5"
            && b0.GetProperty("messages").GetArrayLength() == 4 && b0.GetProperty("messages")[0].GetProperty("content").GetString() == "SYS-PROMPT"
            && b0.TryGetProperty("max_completion_tokens", out _) && !b0.TryGetProperty("temperature", out _) && !b0.TryGetProperty("stream", out _),
            b0.ValueKind == JsonValueKind.Object ? Trunc(b0.GetRawText(), 200) : "(none)");
        var bb = Json(mock.Last("/v1/batches"));
        check("batch", "OpenAI batch: input_file_id from the upload, endpoint /v1/chat/completions, completion_window 24h",
            oid == "batch_1" && bb.GetProperty("input_file_id").GetString() == "file-in"
            && bb.GetProperty("endpoint").GetString() == "/v1/chat/completions" && bb.GetProperty("completion_window").GetString() == "24h", bb.GetRawText());
        var ost = await oa.GetBatchStatusAsync(oid);
        oaState = "completed";
        var ost2 = await oa.GetBatchStatusAsync(oid);
        var ores = await oa.GetBatchResultsAsync(oid);
        var oa1 = ores.FirstOrDefault(r => r.CustomId == "a");
        var ob1 = ores.FirstOrDefault(r => r.CustomId == "b");
        check("batch", "OpenAI: in_progress → running; completed → answers from the output file, failures from the error file",
            ost.State == BatchState.InProgress && ost2.State == BatchState.Ended && oa1 is { Ok: true, Text: "OA-ANSWER" } && ob1 is { Ok: false, Error: "oa bad" },
            $"{ost.State}/{ost2.State} a={oa1?.Text} b={ob1?.Error}");
        var oaLive = new CostTrackingService();
        oaLive.RecordUsage("gpt-5.5", 800, 100, 200, 0);
        check("batch", "OpenAI cost: half the live price (cached tokens counted as cached)",
            oaCost.TotalCostUsd == oaLive.TotalCostUsd * 0.5m, $"batch ${oaCost.TotalCostUsd} · live ${oaLive.TotalCostUsd}");

        // ── BatchService: submit → poll → restart → deliver exactly once ──
        string store = Path.Combine(svc.DataRoot, "batches.json");
        string realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
        check("batch", "the batch store is the harness's portable data folder, never the real ~/.cluadex",
            !Path.GetFullPath(store).StartsWith(Path.GetFullPath(realHome), StringComparison.OrdinalIgnoreCase), store);
        File.Delete(store);
        anthState = "in_progress";
        svc.Settings.ActiveProvider = AiProviderType.Anthropic;
        var server = new LlamaServerProvider(svc);
        var mgr = new AiProviderManager(svc, new LocalGgufProvider(new LlamaInferenceService(svc), server, svc), server,
            new CostTrackingService(), new FileSystemService());
        var bs = new BatchService(svc, mgr);
        int delivered = 0;
        bool acceptDelivery = false;
        bs.RegisterHandler("test", job => { if (!acceptDelivery) return Task.FromResult(false); Interlocked.Increment(ref delivered); return Task.FromResult(true); });
        var job = await bs.SubmitAsync("test", "T", reqs, sessionId: "S1", projectPath: @"C:\proj");
        check("batch", "service: a submitted job is open and written to batches.json (batch id, session, project)",
            job.IsOpen && File.Exists(store) && File.ReadAllText(store).Contains("msgbatch_1") && File.ReadAllText(store).Contains("S1"), job.BatchId);
        await bs.PollOnceAsync(CancellationToken.None);
        check("batch", "service: still running → still open, nothing delivered", bs.Jobs[0].IsOpen && delivered == 0, bs.Jobs[0].Detail);

        anthStatus500 = true;
        await bs.PollOnceAsync(CancellationToken.None);
        check("batch", "service: a 5xx while checking keeps the job open and shows why (no crash, retried next poll)",
            bs.Jobs[0].IsOpen && !string.IsNullOrEmpty(bs.Jobs[0].LastError), Trunc(bs.Jobs[0].LastError ?? "", 120));
        anthStatus500 = false;
        bs.Dispose();

        // The poll timer: one quick first check, then the interval — not re-armed to the quick delay after every poll.
        var timed = new BatchService(svc, mgr) { FirstPollDelay = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromSeconds(60) };
        int statusBefore = mock.Count("/v1/messages/batches/msgbatch_1");
        timed.EnsurePolling();
        await Task.Delay(2500);
        int autoPolls = mock.Count("/v1/messages/batches/msgbatch_1") - statusBefore;
        timed.Dispose();
        check("batch", "service timer: one quick first poll, then it waits the interval (was re-armed every poll → every 5 s)",
            autoPolls == 1, $"{autoPolls} poll(s) in 2.5 s");

        // "Restart": a new service reads the job back and finishes it.
        anthState = "ended";
        var bs2 = new BatchService(svc, mgr);
        check("batch", "service after a restart: the open job is read back from batches.json", bs2.Jobs.Count == 1 && bs2.Jobs[0].IsOpen, $"{bs2.Jobs.Count} job(s)");
        bs2.RegisterHandler("test", j => { if (!acceptDelivery) return Task.FromResult(false); Interlocked.Increment(ref delivered); return Task.FromResult(true); });
        await bs2.PollOnceAsync(CancellationToken.None);
        var ended = bs2.Jobs[0];
        check("batch", "service: ended → results kept on the job; a handler that refuses leaves it undelivered (retried)",
            !ended.IsOpen && ended.Results?.Count == 2 && ended.Succeeded == 1 && ended.Errored == 1 && !ended.Delivered && delivered == 0,
            $"{ended.State} ok={ended.Succeeded} failed={ended.Errored} delivered={ended.Delivered}");
        acceptDelivery = true;
        await bs2.PollOnceAsync(CancellationToken.None);
        await bs2.PollOnceAsync(CancellationToken.None);
        check("batch", "service: delivered exactly once, and remembered as delivered in batches.json",
            delivered == 1 && bs2.Jobs[0].Delivered && new BatchService(svc, mgr).Jobs[0].Delivered, $"delivered={delivered}");
        check("batch", "service: an ended job can be removed from the list", bs2.Remove(bs2.Jobs[0]) && bs2.Jobs.Count == 0, "");
        var open = await bs2.SubmitAsync("test", "T2", reqs);
        anthState = "in_progress";
        check("batch", "service: an open job cannot just be dropped (it would keep billing with nobody to collect it)", !bs2.Remove(open), "");
        await bs2.CancelAsync(open);
        check("batch", "service: cancel goes to the provider", mock.Count("/v1/messages/batches/msgbatch_1/cancel") == 2, "");
        bs2.Dispose();
        File.Delete(store);

        // ── Session memory through batch: saved into the project the chat was in, not the one open now ──
        string proj = Path.Combine(Path.GetTempPath(), "cx-batchmem-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(proj);
        try
        {
            svc.Settings.BatchBackgroundWork = true;
            var bs3 = new BatchService(svc, mgr);
            var fsNow = new FileSystemService();   // no project open "now"
            var sms = new SessionMemoryService(new MemoryService(fsNow), mgr, svc, new LocalizationService(), batchService: bs3, fileSystem: fsNow);
            var chat = Enumerable.Range(0, 8).Select(i => new ChatMessage { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = $"turn {i}: the deploy key lives in vault X" }).ToList();
            int before = mock.Count("/v1/messages/batches");
            anthState = "in_progress";
            bool viaBatch = await sms.TrySubmitBatchAsync(chat, wantMemory: true, wantInstincts: false, proj, CancellationToken.None);
            var memReq = Json(mock.Last("/v1/messages/batches")).GetProperty("requests");
            check("memory", "session memory goes out as a batch (one request, custom_id \"memory\") when the provider has one",
                viaBatch && mock.Count("/v1/messages/batches") == before + 1 && memReq.GetArrayLength() == 1
                && memReq[0].GetProperty("custom_id").GetString() == SessionMemoryService.MemoryRequestId, $"requests={memReq.GetArrayLength()}");
            anthResults = "{\"custom_id\":\"memory\",\"result\":{\"type\":\"succeeded\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" +
                JsonSerializer.Serialize("[{\"name\":\"deploy_key_location\",\"type\":\"project\",\"description\":\"where the deploy key lives\",\"content\":\"vault X\",\"scope\":\"project\"}]") +
                "}],\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}}}\n";
            anthState = "ended";
            await bs3.PollOnceAsync(CancellationToken.None);
            string saved = Path.Combine(proj, ".cluadex", "memory", "deploy_key_location.md");
            check("memory", "its answer is saved as a project memory in the chat's own project folder",
                File.Exists(saved) && File.ReadAllText(saved).Contains("vault X") && bs3.Jobs.All(j => j.Delivered), saved);
            svc.Settings.BatchBackgroundWork = false;
            check("memory", "batch for background work switched off → it runs live instead (TrySubmit says no)",
                !await sms.TrySubmitBatchAsync(chat, true, false, proj, CancellationToken.None), "");
            bs3.Dispose();
        }
        finally
        {
            try { Directory.Delete(proj, recursive: true); } catch { }
            try { File.Delete(store); } catch { }
            anthResults = AnthResultOk + "\n" + AnthResultErr + "\n";
        }

        // ── the chat's batch question ──
        var transcript = new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = new string('a', 90_000) },
            new() { Role = MessageRole.Assistant, Content = new string('b', 40_000) },
            new() { Role = MessageRole.AgentStatus, Content = "reading files…" },
            new() { Role = MessageRole.Assistant, Content = "boom", HasError = true },
            new() { Role = MessageRole.User, Content = "short q" },
            new() { Role = MessageRole.Assistant, Content = "short a" },
        };
        var q = new ChatMessage { Role = MessageRole.User, Content = "NEW" };
        transcript.Add(q);
        var hist = ChatViewModel.BatchHistory(transcript, q);
        check("chat", "batch question history: status lines and error bubbles dropped, cut from the oldest end to 120k characters",
            hist.Count == 3 && hist[0].Content.StartsWith("b") && hist[^1].Content == "short a" && !hist.Any(h => h.Content == "NEW"),
            string.Join(" | ", hist.Select(h => $"{h.Role}:{h.Content.Length}")));
        string bp = ChatViewModel.BatchChatSystemPrompt(@"D:\proj");
        check("chat", "batch question system prompt: says it has no tools and names the project",
            bp.Contains("no tools") && bp.Contains(@"D:\proj"), "");
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
