using System.Net.Http;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Batch;

namespace CluadeX.Services.Providers;

/// <summary>Message Batches API: every token (cache reads and writes too) at half price, results within 24 hours.</summary>
public partial class AnthropicProvider : IBatchProvider
{
    public bool SupportsBatch => !string.IsNullOrWhiteSpace(GetConfig().ApiKey);

    public string BatchModel => ModelOf(GetConfig());

    /// <summary>The params of one batch item: the same model, system prompt, messages, thinking and sampling a live
    /// request would carry. No block_binding (a new conversation has no thinking history to bind), so no beta header.
    /// Public for the contract harness.</summary>
    public Dictionary<string, object> BuildBatchParams(BatchRequest r)
    {
        var settings = _settingsService.Settings;
        string model = BatchModel;
        var p = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = settings.MaxTokens,
            ["messages"] = BuildAnthropicMessages(r.History.ToList(), r.UserMessage),
        };
        ApplyThinkingAndSampling(p, model, settings);
        if (p.TryGetValue("thinking", out var t) && t is Dictionary<string, object> thinking)
            thinking.Remove("block_binding");
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt))
            p["system"] = BuildSystem(r.SystemPrompt, settings);
        return p;
    }

    public async Task<string> SubmitBatchAsync(IReadOnlyList<BatchRequest> requests, CancellationToken ct = default)
    {
        var config = GetConfig();
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["requests"] = requests.Select(r => new Dictionary<string, object>
            {
                ["custom_id"] = r.CustomId,
                ["params"] = BuildBatchParams(r),
            }).ToList(),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/v1/messages/batches");
        AddAuthHeaders(request, config);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var doc = await SendBatchCallAsync(request, config, ct);
        return doc.RootElement.GetProperty("id").GetString() ?? throw new BatchException("The batch API returned no id.");
    }

    public async Task<BatchStatus> GetBatchStatusAsync(string batchId, CancellationToken ct = default)
    {
        var config = GetConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/v1/messages/batches/{Uri.EscapeDataString(batchId)}");
        AddAuthHeaders(request, config);
        using var doc = await SendBatchCallAsync(request, config, ct);
        var root = doc.RootElement;
        string status = root.TryGetProperty("processing_status", out var ps) ? ps.GetString() ?? "" : "";
        int Count(string name) => root.TryGetProperty("request_counts", out var rc) && rc.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
        int succeeded = Count("succeeded"), errored = Count("errored") + Count("canceled") + Count("expired"), pending = Count("processing");
        // "ended" covers every way a batch finishes; the per-item results say which items failed or expired.
        var state = status switch
        {
            "ended" => BatchState.Ended,
            _ => BatchState.InProgress,   // in_progress, canceling
        };
        return new BatchStatus(state, succeeded, errored, pending, status);
    }

    public async Task<IReadOnlyList<BatchResult>> GetBatchResultsAsync(string batchId, CancellationToken ct = default)
    {
        var config = GetConfig();
        // The results live at the batch's results_url (JSONL, one line per item, any order).
        string? resultsUrl;
        using (var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/v1/messages/batches/{Uri.EscapeDataString(batchId)}"))
        {
            AddAuthHeaders(statusReq, config);
            using var doc = await SendBatchCallAsync(statusReq, config, ct);
            resultsUrl = doc.RootElement.TryGetProperty("results_url", out var u) ? u.GetString() : null;
        }
        if (string.IsNullOrEmpty(resultsUrl)) throw new BatchException("The batch has no results yet.");
        // The key goes only to the host it is configured for: a results_url elsewhere is refused, not followed.
        if (!Uri.TryCreate(resultsUrl, UriKind.Absolute, out var resultsUri)
            || !Uri.TryCreate(BaseUrlOf(config), UriKind.Absolute, out var apiUri)
            || !string.Equals(resultsUri.Host, apiUri.Host, StringComparison.OrdinalIgnoreCase)
            || resultsUri.Scheme != apiUri.Scheme)
            throw new BatchException($"The batch's results are on another host ({(resultsUri?.Host ?? "?")}); not sending the API key there.");

        using var request = new HttpRequestMessage(HttpMethod.Get, resultsUri);
        AddAuthHeaders(request, config);
        using var response = await _httpClient.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BatchException(DescribeApiError((int)response.StatusCode, text, config, BatchModel));

        var results = new List<BatchResult>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var item = JsonDocument.Parse(line);
            var root = item.RootElement;
            string id = root.GetProperty("custom_id").GetString() ?? "";
            var result = root.GetProperty("result");
            string type = result.GetProperty("type").GetString() ?? "";
            if (type == "succeeded")
            {
                var msg = result.GetProperty("message");
                var sb = new StringBuilder();
                foreach (var block in msg.GetProperty("content").EnumerateArray())
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text")
                        sb.Append(block.GetProperty("text").GetString());
                int inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
                if (msg.TryGetProperty("usage", out var usage))
                {
                    inTok = usage.TryGetProperty("input_tokens", out var a) ? a.GetInt32() : 0;
                    outTok = usage.TryGetProperty("output_tokens", out var b) ? b.GetInt32() : 0;
                    cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                    cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                }
                _costTracker?.RecordUsage(BatchModel, inTok, outTok, cacheRead, cacheWrite, isNewRequest: true, priceFactor: 0.5m);
                results.Add(new BatchResult(id, true, sb.ToString(), null, inTok + cacheRead + cacheWrite, outTok));
            }
            else
            {
                string why = type switch
                {
                    "errored" => result.TryGetProperty("error", out var e) && e.TryGetProperty("error", out var inner)
                                 && inner.TryGetProperty("message", out var m) ? m.GetString() ?? "errored" : "errored",
                    "expired" => "expired (not processed within 24 hours)",
                    "canceled" => "canceled",
                    _ => type,
                };
                results.Add(new BatchResult(id, false, "", why, 0, 0));
            }
        }
        return results;
    }

    public async Task CancelBatchAsync(string batchId, CancellationToken ct = default)
    {
        var config = GetConfig();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/v1/messages/batches/{Uri.EscapeDataString(batchId)}/cancel");
        AddAuthHeaders(request, config);
        using var _ = await SendBatchCallAsync(request, config, ct);
    }

    private async Task<JsonDocument> SendBatchCallAsync(HttpRequestMessage request, ProviderConfig config, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BatchException(DescribeApiError((int)response.StatusCode, text, config, BatchModel));
        return JsonDocument.Parse(text);
    }
}
