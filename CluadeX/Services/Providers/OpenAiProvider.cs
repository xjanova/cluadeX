using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class OpenAiProvider : ApiProviderBase, IModelLister
{
    private readonly CostTrackingService? _costTracker;

    public override string ProviderId => "OpenAI";
    public override string DisplayName => "OpenAI";

    /// <summary>Used when no model is picked — OpenAI's "start here" flagship (October 2026).</summary>
    public const string DefaultModel = "gpt-6-astra";

    // Newest first (October 2026). Only the offline starting list: Test Connection replaces it with the
    // models this key can actually call (GET /models). Retiring 2026-10-23 and later: o-series, gpt-4,
    // gpt-4-turbo, gpt-3.5-turbo, the dated gpt-5 snapshots — deliberately not listed.
    public static readonly string[] KnownModels =
    [
        "gpt-6-astra",     // flagship
        "gpt-6.1-sol",     // balanced
        "gpt-6-sol",       // complex coding / agentic work
        "gpt-6-luna",      // cheapest
        "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
        "gpt-5.5", "gpt-5.4", "gpt-5.4-mini",
        "gpt-4.1", "gpt-4o", "gpt-4o-mini",
    ];

    private const string KeysUrl = "platform.openai.com/api-keys";
    private const string BillingUrl = "platform.openai.com/settings/organization/billing";
    // Reasoning tokens count against max_completion_tokens; a local-model-sized cap leaves nothing for the answer.
    private const int ReasoningMaxTokensFloor = 32000;

    /// <summary>Highest reasoning_effort the model accepts, or null for a non-reasoning model (gpt-4.1,
    /// gpt-4o, unknown names). Reasoning models take max_completion_tokens and reject temperature/top_p.</summary>
    public static string? MaxEffort(string? model)
    {
        string m = model?.Trim().ToLowerInvariant() ?? "";
        int slash = m.LastIndexOf('/');                       // proxies: "openai/gpt-6-sol"
        if (slash >= 0) m = m[(slash + 1)..];
        if (m.StartsWith("gpt-6") || m.StartsWith("gpt-5.6")) return "max";
        if (Regex.IsMatch(m, @"^gpt-5\.[2-5]")) return "xhigh";
        if (m.StartsWith("gpt-5") || Regex.IsMatch(m, @"^o[134](-|$)")) return "high";
        return null;
    }

    /// <summary>The user's effort pick as this model accepts it (rounded down), or null to omit the field.</summary>
    public static string? EffortFor(string model, string? pick, bool thinkingToggle)
    {
        string? max = MaxEffort(model);
        if (max == null) return null;
        string p = EffortLevels.Normalize(pick);
        if (p == EffortLevels.Auto) return thinkingToggle ? Clamp("high", max) : null;
        return Clamp(p, max);

        static string Clamp(string p, string max) =>
            Array.IndexOf(EffortLevels.All, p) > Array.IndexOf(EffortLevels.All, max) ? max : p;
    }

    public OpenAiProvider(SettingsService settingsService, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        _costTracker = costTracker;
    }

    private static string BaseUrlOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl.TrimEnd('/');

    private static string ModelOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.EffectiveModelId) ? DefaultModel : config.EffectiveModelId.Trim();

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            IsReady = false;
            SetStatus("API key not configured");
            return;
        }

        SetLoading(true);
        try
        {
            var (success, msg) = await TestConnectionAsync(ct);
            IsReady = success;
            SetStatus(success ? $"OpenAI ready ({ModelOf(config)})" : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        string model = ModelOf(config);
        var settings = _settingsService.Settings;

        var messages = BuildChatMessages(history, userMessage, systemPrompt);

        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            // Usage arrives in one last chunk only when asked for — that's what cost tracking reads.
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true },
        };

        if (MaxEffort(model) != null)
        {
            // Reasoning model: no temperature/top_p, max_completion_tokens (includes reasoning tokens).
            requestObj["max_completion_tokens"] = Math.Max(settings.MaxTokens, ReasoningMaxTokensFloor);
            if (EffortFor(model, settings.ReasoningEffort, settings.ExtendedThinkingEnabled) is { } effort)
                requestObj["reasoning_effort"] = effort;
        }
        else
        {
            requestObj["temperature"] = (double)settings.Temperature;
            requestObj["max_tokens"] = settings.MaxTokens;
        }

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        UsageMeter.Report(UsageMeter.FromOpenAi(response, model));   // rate-limit headroom for the usage strip

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError(DescribeHttpError("OpenAI", (int)response.StatusCode, error, config.ApiKey, model, KeysUrl, BillingUrl));
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();

                if (_costTracker != null && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    int inTok = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var a) ? a : 0;
                    int outTok = usage.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var b) ? b : 0;
                    int cached = usage.TryGetProperty("prompt_tokens_details", out var pd)
                                 && pd.TryGetProperty("cached_tokens", out var cd) && cd.TryGetInt32(out var cc) ? cc : 0;
                    if (inTok > 0 || outTok > 0)
                        _costTracker.RecordUsage(model, inTok - cached, outTok, cached, 0);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed chunks */ }

            if (!string.IsNullOrEmpty(content))
                yield return content;
        }
    }

    public override async Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var token in ChatAsync(history, userMessage, systemPrompt, ct))
            sb.Append(token);
        return sb.ToString();
    }

    public override async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return (false, LocalizedResources.Instance["anthropic.err.noKey"]);

        string model = ModelOf(config);
        try
        {
            var (ids, error) = await FetchModelsAsync(config, ct);
            if (ids == null) return (false, error!);
            // The key works; say so even when the picked model is missing, but say that too.
            return ids.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? (true, $"Connected to OpenAI ({model})")
                : (true, $"Connected to OpenAI — {string.Format(LocalizedResources.Instance["api.err.modelMissing"], model)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(config.ApiKey))
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }

    /// <summary>Chat-capable model IDs this key can call, newest first; null when the listing fails.</summary>
    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey)) return null;
        try { return (await FetchModelsAsync(config, ct)).Ids; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // GET /models has no capability field — keep text-chat families, drop audio/image/embedding/etc.
    private static readonly Regex ChatModelId = new(@"^(gpt-|o[134](-|$)|chatgpt-)", RegexOptions.IgnoreCase);
    private static readonly Regex NonChatModelId = new(
        @"(tts|transcribe|realtime|audio|image|embedding|moderation|search|instruct|dall-e|whisper|davinci|babbage|codex)",
        RegexOptions.IgnoreCase);

    /// <summary>True for a text-chat model ID from OpenAI's own /models list.</summary>
    public static bool IsChatModel(string id) => ChatModelId.IsMatch(id) && !NonChatModelId.IsMatch(id);

    private async Task<(List<string>? Ids, string? Error)> FetchModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/models");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        using var response = await _httpClient.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (null, DescribeHttpError("OpenAI", (int)response.StatusCode, body, config.ApiKey, ModelOf(config), KeysUrl, BillingUrl));

        var models = new List<(string Id, long Created)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                long created = m.TryGetProperty("created", out var cr) && cr.TryGetInt64(out var c) ? c : 0;
                models.Add((id, created));
            }

        // A custom BaseUrl (proxy / compatible server) may use any naming — only filter OpenAI's own list.
        bool officialApi = string.IsNullOrWhiteSpace(config.BaseUrl) || config.BaseUrl.Contains("api.openai.com");
        return (models
            .Where(x => !officialApi || IsChatModel(x.Id))
            .OrderByDescending(x => x.Created)
            .Select(x => x.Id)
            .ToList(), null);
    }
}
