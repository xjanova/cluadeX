using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Batch;

namespace CluadeX.Services.Providers;

/// <summary>Batch API: the requests go up as a JSONL file (purpose "batch"), run within 24 hours at half price, and
/// the answers come back as another JSONL file. Each item is a plain /v1/chat/completions request — the same body
/// <see cref="ChatAsync"/> sends, minus streaming.</summary>
public partial class OpenAiProvider : IBatchProvider
{
    /// <summary>OpenAI's own API only: a custom BaseUrl (proxy, Azure, a compatible local server) usually has no
    /// /batches endpoint, and a refused batch would only show up long after the user stopped waiting.</summary>
    public bool SupportsBatch
    {
        get
        {
            var config = GetConfig();
            return !string.IsNullOrWhiteSpace(config.ApiKey)
                   && (string.IsNullOrWhiteSpace(config.BaseUrl) || config.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase));
        }
    }

    public string BatchModel => ModelOf(GetConfig());

    /// <summary>The body of one batch line. Public for the contract harness.</summary>
    public Dictionary<string, object> BuildBatchBody(BatchRequest r)
    {
        var settings = _settingsService.Settings;
        string model = BatchModel;
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildChatMessages(r.History.ToList(), r.UserMessage, r.SystemPrompt),
        };
        if (MaxEffort(model) != null)
        {
            body["max_completion_tokens"] = Math.Max(settings.MaxTokens, ReasoningMaxTokensFloor);
            if (EffortFor(model, Models.EffortLevels.Effective(settings.ReasoningEffort), settings.ExtendedThinkingEnabled) is { } effort)
                body["reasoning_effort"] = effort;
        }
        else
        {
            body["temperature"] = (double)settings.Temperature;
            body["max_tokens"] = settings.MaxTokens;
        }
        return body;
    }

    public async Task<string> SubmitBatchAsync(IReadOnlyList<BatchRequest> requests, CancellationToken ct = default)
    {
        var config = GetConfig();
        var jsonl = new StringBuilder();
        foreach (var r in requests)
            jsonl.Append(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["custom_id"] = r.CustomId,
                ["method"] = "POST",
                ["url"] = "/v1/chat/completions",
                ["body"] = BuildBatchBody(r),
            })).Append('\n');

        // 1. the input file
        string fileId;
        using (var upload = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/files"))
        {
            upload.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            var form = new MultipartFormDataContent { { new StringContent("batch"), "purpose" } };
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonl.ToString()));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
            form.Add(file, "file", "cluadex-batch.jsonl");
            upload.Content = form;
            using var doc = await SendBatchCallAsync(upload, config, ct);
            fileId = doc.RootElement.GetProperty("id").GetString() ?? throw new BatchException("The file upload returned no id.");
        }

        // 2. the batch over it
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/batches");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["input_file_id"] = fileId,
            ["endpoint"] = "/v1/chat/completions",
            ["completion_window"] = "24h",
            ["metadata"] = new Dictionary<string, string> { ["source"] = "cluadex" },
        }), Encoding.UTF8, "application/json");
        using var batch = await SendBatchCallAsync(request, config, ct);
        return batch.RootElement.GetProperty("id").GetString() ?? throw new BatchException("The batch API returned no id.");
    }

    public async Task<BatchStatus> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        using var doc = await GetBatchAsync(batchId, ct);
        var root = doc.RootElement;
        string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        int Count(string name) => root.TryGetProperty("request_counts", out var rc) && rc.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
        int total = Count("total"), done = Count("completed"), failed = Count("failed");
        var state = status switch
        {
            "completed" => BatchState.Ended,
            // Expired and cancelled batches still hand back whatever finished in an output file.
            "expired" => BatchState.Expired,
            "cancelled" => BatchState.Canceled,
            "failed" => BatchState.Failed,   // the input was refused as a whole (errors[] says why)
            _ => BatchState.InProgress,       // validating, in_progress, finalizing, cancelling
        };
        string detail = status;
        if (state == BatchState.Failed && root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object
            && errs.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            && data[0].TryGetProperty("message", out var m))
            detail = $"failed: {m.GetString()}";
        return new BatchStatus(state, done, failed, Math.Max(0, total - done - failed), detail);
    }

    public async Task<IReadOnlyList<BatchResult>> GetBatchResultsAsync(string batchId, CancellationToken ct = default)
    {
        var config = GetConfig();
        string? outputId, errorId;
        using (var doc = await GetBatchAsync(batchId, ct))
        {
            outputId = doc.RootElement.TryGetProperty("output_file_id", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            errorId = doc.RootElement.TryGetProperty("error_file_id", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        if (outputId == null && errorId == null) throw new BatchException("The batch has no results yet.");

        var results = new List<BatchResult>();
        foreach (var fileId in new[] { outputId, errorId })
        {
            if (string.IsNullOrEmpty(fileId)) continue;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/files/{Uri.EscapeDataString(fileId)}/content");
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            using var response = await _httpClient.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new BatchException(DescribeHttpError("OpenAI", (int)response.StatusCode, text, config.ApiKey, BatchModel, KeysUrl, BillingUrl));
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                results.Add(ParseResultLine(line));
        }
        return results;
    }

    BatchResult ParseResultLine(string line)
    {
        using var item = JsonDocument.Parse(line);
        var root = item.RootElement;
        string id = root.TryGetProperty("custom_id", out var cid) ? cid.GetString() ?? "" : "";
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            return new BatchResult(id, false, "", err.TryGetProperty("message", out var em) ? em.GetString() ?? "error" : "error", 0, 0);

        var response = root.GetProperty("response");
        int code = response.TryGetProperty("status_code", out var sc) && sc.TryGetInt32(out int c) ? c : 0;
        var body = response.TryGetProperty("body", out var b) ? b : default;
        if (code != 200 || body.ValueKind != JsonValueKind.Object)
        {
            string why = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var be)
                         && be.TryGetProperty("message", out var bm) ? bm.GetString() ?? $"HTTP {code}" : $"HTTP {code}";
            return new BatchResult(id, false, "", why, 0, 0);
        }

        string answer = body.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
        int inTok = 0, outTok = 0, cached = 0;
        if (body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inTok = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var x) ? x : 0;
            outTok = usage.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var y) ? y : 0;
            cached = usage.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object
                     && pd.TryGetProperty("cached_tokens", out var cd) && cd.TryGetInt32(out var z) ? z : 0;
        }
        _costTracker?.RecordUsage(BatchModel, inTok - cached, outTok, cached, 0, isNewRequest: true, priceFactor: 0.5m);
        return new BatchResult(id, true, answer, null, inTok, outTok);
    }

    public async Task CancelBatchAsync(string batchId, CancellationToken ct = default)
    {
        var config = GetConfig();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/batches/{Uri.EscapeDataString(batchId)}/cancel");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        using var _ = await SendBatchCallAsync(request, config, ct);
    }

    async Task<JsonDocument> GetBatchAsync(string batchId, CancellationToken ct)
    {
        var config = GetConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/batches/{Uri.EscapeDataString(batchId)}");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        return await SendBatchCallAsync(request, config, ct);
    }

    async Task<JsonDocument> SendBatchCallAsync(HttpRequestMessage request, ProviderConfig config, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BatchException(DescribeHttpError("OpenAI", (int)response.StatusCode, text, config.ApiKey, BatchModel, KeysUrl, BillingUrl));
        return JsonDocument.Parse(text);
    }
}
