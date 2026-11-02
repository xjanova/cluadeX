using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// The one client behind every OpenAI-compatible Chat Completions service (OpenRouter, Groq, DeepSeek, Mistral, …):
/// streamed chat, native tool calls with streamed tool_call deltas, model listing, error explanations and cost
/// recording. What differs per service is a <see cref="CompatiblePreset"/>.
///
/// These services agree on the wire but not on the edges, and their docs don't list every field they refuse. So a 400
/// that names a field this client sent optionally (stream_options, max_tokens, temperature, tool_choice, echoed
/// reasoning) drops or adapts that field and sends again — once per field, remembered for the session. A request that
/// is refused because of the tools themselves switches that model to the [ACTION:] text protocol, like the other API
/// providers do.
/// </summary>
public sealed class CompatibleProvider : ApiProviderBase, IModelLister
{
    private readonly CostTrackingService? _costTracker;

    public CompatiblePreset Preset { get; }
    public override string ProviderId => Preset.Type.ToString();
    public override string DisplayName => Preset.Name;

    public CompatibleProvider(SettingsService settingsService, CompatiblePreset preset, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        Preset = preset;
        _costTracker = costTracker;
    }

    // ═══════════════════════════════════════════
    // What GET /models taught us (shared by every instance: Settings and the chat use the same facts)
    // ═══════════════════════════════════════════

    /// <summary>One model as its service's /models described it. Null = not reported.</summary>
    public sealed record ModelInfo(int? ContextWindow, bool? Tools, decimal? InputPerM, decimal? OutputPerM);

    private static readonly ConcurrentDictionary<string, ModelInfo> Known = new(StringComparer.OrdinalIgnoreCase);

    private static string InfoKey(AiProviderType type, string model) => type + "|" + model.Trim();

    /// <summary>What <paramref name="type"/>'s /models said about <paramref name="model"/>, or null.</summary>
    public static ModelInfo? InfoOf(AiProviderType type, string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : Known.GetValueOrDefault(InfoKey(type, model));

    /// <summary>The context window the service reported for the model, or null (the caller uses its default).</summary>
    public static int? ContextWindowOf(AiProviderType type, string? model) => InfoOf(type, model)?.ContextWindow;

    // ═══════════════════════════════════════════
    // Configuration
    // ═══════════════════════════════════════════

    private string BaseUrlOf(ProviderConfig config) =>
        (string.IsNullOrWhiteSpace(config.BaseUrl) ? Preset.BaseUrl : config.BaseUrl.Trim()).TrimEnd('/');

    private string ModelOf(ProviderConfig config) => Agents.SubAgentModel.Pick(
        string.IsNullOrWhiteSpace(config.EffectiveModelId) ? Preset.DefaultModel : config.EffectiveModelId.Trim());

    /// <summary>The model a request goes out with now (the setting, else the preset's default).</summary>
    public string CurrentModel => ModelOf(GetConfig());

    /// <summary>Why no request can be sent (no key, no or unsafe base URL, no model), or null when one can.</summary>
    private string? ConfigProblem(ProviderConfig config)
    {
        string L(string k) => LocalizedResources.Instance[k];
        if (Preset.KeyRequired && string.IsNullOrWhiteSpace(config.ApiKey)) return L("anthropic.err.noKey");
        string url = BaseUrlOf(config);
        if (url.Length == 0) return string.Format(L("agg.err.baseUrl"), Preset.Name, Preset.BaseUrlExample);
        if (!Mcp.McpHttpClient.IsAllowedEndpoint(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null, out var why))
            return $"{Preset.Name}: {why}";
        if (ModelOf(config).Length == 0) return string.Format(L("agg.err.noModel"), Preset.Name);
        return null;
    }

    private void Authorize(HttpRequestMessage request, ProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            if (Preset.AuthHeader == "Authorization") request.Headers.Add("Authorization", $"Bearer {config.ApiKey.Trim()}");
            else request.Headers.Add(Preset.AuthHeader, config.ApiKey.Trim());
        }
        foreach (var (k, v) in Preset.Headers) request.Headers.TryAddWithoutValidation(k, v);
    }

    private string Explain(int status, string body, ProviderConfig config, string model) =>
        DescribeHttpError(Preset.Name, status, body, config.ApiKey, model,
            KeyPage(Preset.KeysUrl), KeyPage(Preset.BillingUrl.Length > 0 ? Preset.BillingUrl : Preset.KeysUrl));

    // A custom endpoint has no key page we know of: name the service's own console rather than leave "at ." behind.
    private static string KeyPage(string url) =>
        url.Length > 0 ? url.Replace("https://", "") : LocalizedResources.Instance["agg.err.ownConsole"];

    private string Redact(string text, ProviderConfig config) =>
        string.IsNullOrEmpty(config.ApiKey) ? text : text.Replace(config.ApiKey.Trim(), "[REDACTED]");

    // ═══════════════════════════════════════════
    // Learned at run time (per provider instance = per service, for this session)
    // ═══════════════════════════════════════════

    private readonly ConcurrentDictionary<string, byte> _dropped = new(StringComparer.OrdinalIgnoreCase);   // "model|field"
    private readonly ConcurrentDictionary<string, byte> _maxCompletion = new(StringComparer.OrdinalIgnoreCase);   // models
    private readonly ConcurrentDictionary<string, byte> _toolsOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _toolsConfirmed = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _forcedChoiceOff, _echoOff, _toolStreamOff;

    private bool Dropped(string model, string field) => _dropped.ContainsKey(model + "|" + field);

    /// <summary>Native tools unless this model refused them this session, or its service's /models said it has none.</summary>
    public override bool SupportsNativeToolUse
    {
        get
        {
            string model = CurrentModel;
            return !_toolsOff.ContainsKey(model) && InfoOf(Preset.Type, model)?.Tools != false;
        }
    }

    // ═══════════════════════════════════════════
    // Requests
    // ═══════════════════════════════════════════

    /// <summary>The Chat Completions body (public for the contract harness). <paramref name="tools"/> null or empty = a
    /// plain chat request.</summary>
    public Dictionary<string, object> BuildRequest(string model, List<object> messages, List<ToolSchema>? tools,
        string? toolChoice, bool stream)
    {
        var settings = _settingsService.Settings;
        var req = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
        };
        if (stream && Preset.StreamUsage && !Dropped(model, "stream_options"))
            req["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        // OpenAI's reasoning models (reached through OpenRouter / Azure) refuse temperature.
        if (OpenAiProvider.MaxEffort(model) == null && !Dropped(model, "temperature"))
            req["temperature"] = (double)settings.Temperature;
        if (!Dropped(model, "max_tokens"))
        {
            int budget = Math.Max(1, settings.MaxTokens);
            if (Preset.MaxOutputCap is int cap) budget = Math.Min(budget, cap);
            req[_maxCompletion.ContainsKey(model) ? "max_completion_tokens" : "max_tokens"] = budget;
        }
        if (tools is { Count: > 0 })
        {
            req["tools"] = BuildOpenAiToolDefs(tools);
            // "auto" is the default once tools are sent, so it is never written: a server started without automatic
            // tool choice (vLLM) or one that takes only "auto" (Z.ai) has nothing to refuse.
            string pick = toolChoice?.Trim().ToLowerInvariant() ?? "";
            if (pick == "none") req["tool_choice"] = "none";
            else if (pick is not ("" or "auto") && Preset.ToolChoice == ToolChoiceSupport.Full && !_forcedChoiceOff)
                req["tool_choice"] = BuildOpenAiToolChoice(toolChoice);
        }
        LiveModelReport.Sent(Preset.Type, model, null, false);
        return req;
    }

    /// <summary>The chat messages for a native tool conversation, with this service's quirks applied (public for the
    /// contract harness): earlier reasoning echoed where the service wants it, empty assistant turns dropped (Mistral
    /// refuses them), tool-call ids re-mapped to 9 letters/digits for Mistral.</summary>
    public List<object> BuildToolMessages(List<NativeMessage> messages, string? systemPrompt, bool withTools)
    {
        var list = BuildOpenAiToolMessages(messages, systemPrompt);

        // BuildOpenAiToolMessages writes exactly one dictionary per assistant message, in order.
        var assistants = messages.Where(m => m.Role == "assistant").ToList();
        int a = 0;
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object> d || !Equals(d.GetValueOrDefault("role"), "assistant")) continue;
            var source = a < assistants.Count ? assistants[a] : null;
            a++;
            if (!Preset.EchoReasoning || _echoOff || source == null) continue;
            var reasoning = source.Content.FirstOrDefault(b => b.Type == "provider_item" && b.Provider?.StartsWith(ReasoningProvider) == true);
            if (reasoning != null && !string.IsNullOrEmpty(reasoning.Data))
                d[reasoning.Provider![ReasoningProvider.Length..]] = reasoning.Data;
            else if (withTools && Preset.Type == AiProviderType.DeepSeek)
                d["reasoning_content"] = "";   // a turn another provider wrote: DeepSeek wants the field on every turn
        }

        list.RemoveAll(item => item is Dictionary<string, object> d && Equals(d.GetValueOrDefault("role"), "assistant")
            && !d.ContainsKey("tool_calls") && string.IsNullOrEmpty(d.GetValueOrDefault("content") as string));

        if (Preset.ShortToolCallIds)
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                if (item.GetValueOrDefault("tool_call_id") is string tid) item["tool_call_id"] = ShortId(tid);
                if (item.GetValueOrDefault("tool_calls") is List<object> calls)
                    foreach (var c in calls.OfType<Dictionary<string, object>>())
                        if (c.GetValueOrDefault("id") is string cid) c["id"] = ShortId(cid);
            }
        return list;
    }

    /// <summary>ContentBlock.Provider prefix of the reasoning a model returned; the rest is the field it came in.</summary>
    public const string ReasoningProvider = "compat:";

    private static readonly Regex NineAlnum = new("^[A-Za-z0-9]{9}$");

    /// <summary>Mistral's tool-call id form: the id itself when it already is 9 letters/digits, else a stable hash.</summary>
    public static string ShortId(string id)
    {
        if (NineAlnum.IsMatch(id)) return id;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(id)));
        return hash[..9].ToLowerInvariant();
    }

    private static readonly string[] OverflowWords =
        ["context length", "context window", "context_length", "maximum context", "too long", "too many tokens", "prompt is too long"];

    /// <summary>A 400/422 that names a field this client sent optionally: drop or adapt it and say so (the caller sends
    /// again). Never for a context overflow — that goes to the agent loop, which compacts.</summary>
    private bool Adapt(int status, string error, Dictionary<string, object> req, string model, bool echoed)
    {
        if (status is not (400 or 422)) return false;
        string e = error.ToLowerInvariant();
        if (OverflowWords.Any(e.Contains)) return false;

        if (req.ContainsKey("stream_options") && e.Contains("stream_options"))
            return _dropped.TryAdd(model + "|stream_options", 0);
        if (req.ContainsKey("max_tokens") && e.Contains("max_completion_tokens"))
            return _maxCompletion.TryAdd(model, 0);
        if ((req.ContainsKey("max_tokens") && e.Contains("max_tokens"))
            || (req.ContainsKey("max_completion_tokens") && e.Contains("max_completion_tokens")))
            return _dropped.TryAdd(model + "|max_tokens", 0);
        if (req.ContainsKey("temperature") && e.Contains("temperature"))
            return _dropped.TryAdd(model + "|temperature", 0);
        if (req.ContainsKey("tool_choice") && e.Contains("tool_choice") && !_forcedChoiceOff)
            return _forcedChoiceOff = true;
        if (echoed && e.Contains("reasoning") && !_echoOff)
            return _echoOff = true;
        if (req.ContainsKey("tools") && Equals(req["stream"], true) && e.Contains("stream") && !_toolStreamOff)
            return _toolStreamOff = true;
        return false;
    }

    /// <summary>A refusal that is about the tools themselves (before this model ever answered a tool request): the
    /// model then goes to the text protocol. OpenRouter answers a model without tool support with a 404.</summary>
    private bool ToolsRefused(int status, string error, string model)
    {
        if (_toolsConfirmed.ContainsKey(model)) return false;
        string e = error.ToLowerInvariant();
        if (OverflowWords.Any(e.Contains) || e.Contains("quota") || e.Contains("credit") || e.Contains("balance")) return false;
        bool aboutTools = e.Contains("tool") || e.Contains("function");
        if (status == 404) return aboutTools;
        if (status is not (400 or 422)) return false;
        // A wrong model id is not a tool problem.
        if (e.Contains("model") && (e.Contains("not found") || e.Contains("does not exist") || e.Contains("not a valid") || e.Contains("invalid model")))
            return false;
        return true;
    }

    private async Task<(HttpResponseMessage? Response, int Status, string Error)> SendAsync(
        ProviderConfig config, string model, Func<Dictionary<string, object>> build, Func<bool> echoed, CancellationToken ct)
    {
        int status = 0;
        string error = "";
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var req = build();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/chat/completions");
            Authorize(request, config);
            request.Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            UsageMeter.Report(UsageMeter.FromOpenAi(response, model, Preset.Name));
            if (response.IsSuccessStatusCode) return (response, 200, "");

            status = (int)response.StatusCode;
            error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (!Adapt(status, error, req, model, echoed())) break;
        }
        return (null, status, error);
    }

    /// <summary>The request could not be sent at all (no server, DNS, TLS): one sentence, the key never in it.</summary>
    private string NetworkFailure(HttpRequestException ex, ProviderConfig config) => Preset.Local
        ? string.Format(LocalizedResources.Instance["agg.err.localDown"], Preset.Name, BaseUrlOf(config))
        : $"{Preset.Name}: {Redact(ex.Message, config)}";

    // ═══════════════════════════════════════════
    // IAiProvider
    // ═══════════════════════════════════════════

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (ConfigProblem(config) is { } problem)
        {
            IsReady = false;
            SetStatus(problem);
            return;
        }
        // A service without a model list would be asked a (billed) chat request just to start up — it is taken as
        // ready, and the first message says what is wrong if anything is.
        if (!Preset.ListsModels && !Preset.Local)
        {
            IsReady = true;
            SetStatus($"{Preset.Name} ready ({ModelOf(config)})");
            return;
        }
        SetLoading(true);
        try
        {
            var (success, msg) = await TestConnectionAsync(ct);
            IsReady = success;
            SetStatus(success ? $"{Preset.Name} ready ({ModelOf(config)})" : msg);
        }
        finally { SetLoading(false); }
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        if (ConfigProblem(config) is { } problem) { RaiseError(problem); yield break; }
        string model = ModelOf(config);
        var messages = BuildChatMessages(history, userMessage, systemPrompt);

        HttpResponseMessage? response;
        int status;
        string error;
        string? networkError = null;
        try
        {
            (response, status, error) = await SendAsync(config, model, () => BuildRequest(model, messages, null, null, true), () => false, ct);
        }
        catch (HttpRequestException ex)
        {
            (response, status, error) = (null, 0, "");
            networkError = NetworkFailure(ex, config);
        }
        if (networkError != null) { RaiseError(networkError); yield break; }
        if (response == null) { RaiseError(Explain(status, error, config, model)); yield break; }

        using var owned = response;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var state = new StreamState();
        bool thoughtShown = false;
        string? streamError = null;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!TryData(line, out var data)) continue;
            if (data == "[DONE]") break;
            int before = state.Text.Length;
            try { using var doc = JsonDocument.Parse(data); Apply(doc.RootElement, state, null); }
            catch (JsonException) { continue; }
            catch (HttpRequestException ex) { streamError = Redact(ex.Message, config); break; }

            if (state.Text.Length > before)
            {
                // Reasoning goes out once, wrapped, before the reply — per-token wrappers would split it into lines.
                if (!thoughtShown && state.Reasoning.Length > 0)
                {
                    thoughtShown = true;
                    yield return $"<thinking>{state.Reasoning}</thinking>";
                }
                yield return state.Text.ToString(before, state.Text.Length - before);
            }
        }
        if (!thoughtShown && state.Reasoning.Length > 0) yield return $"<thinking>{state.Reasoning}</thinking>";
        if (state.ServedModel != null) LiveModelReport.Served(Preset.Type, state.ServedModel);
        Record(model, state);
        if (streamError != null) RaiseError(streamError);
    }

    public override async Task<string> GenerateAsync(List<ChatMessage> history, string userMessage, string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var token in ChatAsync(history, userMessage, systemPrompt, ct))
            if (!token.StartsWith("<thinking>", StringComparison.Ordinal)) sb.Append(token);
        return sb.ToString();
    }

    public override async Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default,
        string? toolChoice = null)
    {
        var config = GetConfig();
        if (ConfigProblem(config) is { } problem) return new NativeToolResponse { TextContent = problem, StopReason = "error" };
        string model = ModelOf(config);
        tools = UsableTools(tools);
        bool withTools = tools.Count > 0;

        bool echoed = false;
        Dictionary<string, object> Build()
        {
            var msgs = BuildToolMessages(messages, systemPrompt, withTools);
            echoed = Preset.EchoReasoning && !_echoOff && msgs.OfType<Dictionary<string, object>>()
                .Any(d => d.ContainsKey("reasoning_content") || d.ContainsKey("reasoning"));
            return BuildRequest(model, msgs, tools, toolChoice, !(withTools && _toolStreamOff));
        }

        HttpResponseMessage? response;
        int status;
        string error;
        try { (response, status, error) = await SendAsync(config, model, Build, () => echoed, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            throw new HttpRequestException(NetworkFailure(ex, config), null);
        }

        if (response == null)
        {
            string explained = Explain(status, error, config, model);
            if (RetryWithoutMcpTools(status, explained, tools))
                return await ChatWithToolsAsync(messages, systemPrompt, UsableTools(tools), onTextDelta, ct, toolChoice);
            if (withTools && ToolsRefused(status, error, model))
            {
                _toolsOff.TryAdd(model, 0);
                return new NativeToolResponse
                {
                    StopReason = "error",
                    TextContent = LocalizedResources.Instance["api.err.nativeToolsOff"] + "\n" + explained,
                };
            }
            throw new HttpRequestException(explained, null, (System.Net.HttpStatusCode)status);
        }

        using var owned = response;
        NativeToolResponse result;
        try
        {
            result = Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream") || !_toolStreamOff || !withTools
                ? await ParseStreamAsync(response.Content, onTextDelta, ct)
                : ParseCompletion(await response.Content.ReadAsStringAsync(ct), onTextDelta);
        }
        catch (HttpRequestException ex) { throw new HttpRequestException(Redact(ex.Message, config), null); }

        if (result.InputTokens + result.OutputTokens + result.CacheReadTokens > 0)
            Record(model, result.InputTokens, result.OutputTokens, result.CacheReadTokens);
        if (withTools) _toolsConfirmed.TryAdd(model, 0);
        return result;
    }

    // ═══════════════════════════════════════════
    // Response parsing (static + public: the contract harness feeds it recorded streams)
    // ═══════════════════════════════════════════

    private sealed class CallAcc { public string Id = "", Name = ""; public readonly StringBuilder Args = new(); }

    private sealed class StreamState
    {
        public readonly StringBuilder Text = new(), Reasoning = new();
        public string ReasoningField = "reasoning_content";
        public readonly SortedDictionary<int, CallAcc> Calls = new();
        public string? Finish, ServedModel;
        public int Prompt, Completion, Cached;
    }

    private static bool TryData(string line, out string data)
    {
        data = "";
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;   // ": OPENROUTER PROCESSING" keep-alives too
        data = line[5..].Trim();
        return data.Length > 0;
    }

    /// <summary>One streamed chunk, or a whole non-streamed completion (its choices carry "message", not "delta").</summary>
    private static void Apply(JsonElement root, StreamState st, Action<string>? onTextDelta)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("error", out var err) && err.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            string msg = err.ValueKind == JsonValueKind.String ? err.GetString() ?? ""
                : err.TryGetProperty("message", out var m) ? m.ToString() : err.GetRawText();
            throw new HttpRequestException($"Streaming error: {msg}");
        }
        if (root.TryGetProperty("model", out var served) && served.ValueKind == JsonValueKind.String) st.ServedModel = served.GetString();
        ReadUsage(root, st);
        if (root.TryGetProperty("x_groq", out var xg) && xg.ValueKind == JsonValueKind.Object) ReadUsage(xg, st);

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return;
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) st.Finish = fr.GetString();
            if (!(choice.TryGetProperty("delta", out var delta) || choice.TryGetProperty("message", out delta))
                || delta.ValueKind != JsonValueKind.Object) continue;

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } text)
            {
                st.Text.Append(text);
                onTextDelta?.Invoke(text);
            }
            foreach (var field in new[] { "reasoning_content", "reasoning" })
                if (delta.TryGetProperty(field, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } thought)
                {
                    st.Reasoning.Append(thought);
                    st.ReasoningField = field;
                    break;
                }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                foreach (var tc in calls.EnumerateArray())
                {
                    string id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    int index;
                    if (tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i)) index = i;
                    else
                    {
                        // No index (some servers send each call whole): the call with this id, else a new one.
                        var same = st.Calls.FirstOrDefault(kv => id.Length > 0 && kv.Value.Id == id);
                        index = same.Value != null ? same.Key : (st.Calls.Count == 0 ? 0 : st.Calls.Keys.Max() + 1);
                    }
                    if (!st.Calls.TryGetValue(index, out var acc)) st.Calls[index] = acc = new CallAcc();
                    if (id.Length > 0) acc.Id = id;
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name && acc.Name.Length == 0) acc.Name = name;
                        if (fn.TryGetProperty("arguments", out var args))
                        {
                            if (args.ValueKind == JsonValueKind.String) acc.Args.Append(args.GetString());
                            else if (args.ValueKind == JsonValueKind.Object) { acc.Args.Clear(); acc.Args.Append(args.GetRawText()); }
                        }
                    }
                }
        }
    }

    private static void ReadUsage(JsonElement holder, StreamState st)
    {
        if (!holder.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
        int prompt = I(u, "prompt_tokens"), completion = I(u, "completion_tokens");
        if (prompt == 0 && completion == 0) return;
        st.Prompt = prompt;
        st.Completion = completion;
        st.Cached = u.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object
            ? I(pd, "cached_tokens") : I(u, "prompt_cache_hit_tokens");   // DeepSeek reports its cache this way
    }

    private static NativeToolResponse Finish(StreamState st)
    {
        var result = new NativeToolResponse
        {
            TextContent = st.Text.Length > 0 ? st.Text.ToString() : null,
            ThinkingContent = st.Reasoning.Length > 0 ? st.Reasoning.ToString() : null,
            InputTokens = Math.Max(0, st.Prompt - st.Cached),
            OutputTokens = st.Completion,
            CacheReadTokens = st.Cached,
        };
        if (st.Reasoning.Length > 0)
            result.Blocks.Add(new ContentBlock { Type = "provider_item", Provider = ReasoningProvider + st.ReasoningField, Data = st.Reasoning.ToString() });
        if (result.TextContent != null) result.Blocks.Add(new ContentBlock { Type = "text", Text = result.TextContent });
        int n = 0;
        foreach (var acc in st.Calls.Values)
        {
            if (acc.Name.Length == 0) continue;
            string id = acc.Id.Length > 0 ? acc.Id : $"call_{n}";
            n++;
            var input = ParseArgumentsText(acc.Args.ToString());
            result.ToolCalls.Add(new NativeToolCall { Id = id, Name = acc.Name, Input = input });
            result.Blocks.Add(new ContentBlock { Type = "tool_use", Id = id, Name = acc.Name, Input = input });
        }
        result.StopReason = result.ToolCalls.Count > 0 ? "tool_use"
            : st.Finish switch
            {
                "length" or "model_context_window_exceeded" => "max_tokens",
                "content_filter" or "sensitive" => "refusal",
                _ => "end_turn",
            };
        return result;
    }

    /// <summary>Parse a streamed Chat Completions answer into a NativeToolResponse.</summary>
    public static async Task<NativeToolResponse> ParseStreamAsync(HttpContent content, Action<string>? onTextDelta, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var st = new StreamState();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0) continue;
            onTextDelta?.Invoke(string.Empty);   // keep-alive for the loop's idle timeout (see IAiProvider)
            if (!TryData(line, out var data)) continue;
            if (data == "[DONE]") break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc) Apply(doc.RootElement, st, onTextDelta);
        }
        return Finish(st);
    }

    /// <summary>Parse a non-streamed completion (the fallback for a service that refuses streamed tool calls).</summary>
    public static NativeToolResponse ParseCompletion(string json, Action<string>? onTextDelta = null)
    {
        var st = new StreamState();
        using var doc = JsonDocument.Parse(json);
        Apply(doc.RootElement, st, onTextDelta);
        return Finish(st);
    }

    // ═══════════════════════════════════════════
    // Cost
    // ═══════════════════════════════════════════

    private void Record(string model, StreamState st)
    {
        if (st.Prompt + st.Completion > 0) Record(model, Math.Max(0, st.Prompt - st.Cached), st.Completion, st.Cached);
    }

    /// <summary>Tokens always. Dollars at the price the service itself reported (OpenRouter, Together and Hugging Face
    /// list theirs), else the built-in table when it knows the model (an "anthropic/…" id through OpenRouter); a model
    /// neither knows is counted at $0 — the table's unknown-model fallback is Claude Sonnet's price, which made a
    /// $0.10 open model read 30x too dear. A server on this PC is $0.</summary>
    private void Record(string model, int input, int output, int cached)
    {
        if (_costTracker == null) return;
        bool builtIn = BuiltInPriced(model);
        if (!CostTrackingService.HasRegisteredPricing(model) && !builtIn)
            CostTrackingService.RegisterPricing(model, new ModelPricing(0m, 0m, 0m, 0m));
        decimal factor = Preset.Local && builtIn ? 0m : 1m;
        _costTracker.RecordUsage(model, input, output, cached, 0, priceFactor: factor);
    }

    /// <summary>Does the built-in price table know this model, rather than falling back to its default row?</summary>
    public static bool BuiltInPriced(string model) =>
        model.Contains("sonnet", StringComparison.OrdinalIgnoreCase)
        || CostTrackingService.PricingOf(model) != CostTrackingService.PricingOf("~no-such-model~");

    // ═══════════════════════════════════════════
    // Test Connection + model listing
    // ═══════════════════════════════════════════

    public override async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (ConfigProblem(config) is { } problem) return (false, problem);
        string model = ModelOf(config);
        try
        {
            if (Preset.ListsModels || Preset.Local)
            {
                var (ids, status, error) = await FetchModelsAsync(config, ct);
                if (ids != null)
                    return ids.Count == 0 || ids.Contains(model, StringComparer.OrdinalIgnoreCase)
                        ? (true, $"Connected to {Preset.Name} ({model})")
                        : (true, $"{Preset.Name}: {string.Format(LocalizedResources.Instance["api.err.modelMissing"], model)}");
                // No model list on this server (404/405): a one-word chat request proves the key instead.
                if (status is not (404 or 405)) return (false, error!);
            }
            return await PingAsync(config, model, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == null) { return (false, NetworkFailure(ex, config)); }
        catch (Exception ex) { return (false, $"Connection failed: {Redact(ex.Message, config)}"); }
    }

    private async Task<(bool, string)> PingAsync(ProviderConfig config, string model, CancellationToken ct)
    {
        var msgs = new List<object> { new Dictionary<string, object> { ["role"] = "user", ["content"] = "ping" } };
        var (response, status, error) = await SendAsync(config, model, () =>
        {
            var req = BuildRequest(model, msgs, null, null, false);
            if (req.ContainsKey("max_tokens")) req["max_tokens"] = 16;
            if (req.ContainsKey("max_completion_tokens")) req["max_completion_tokens"] = 16;
            return req;
        }, () => false, ct);
        if (response == null) return (false, Explain(status, error, config, model));
        response.Dispose();
        return (true, $"Connected to {Preset.Name} ({model})");
    }

    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (ConfigProblem(config) != null || !(Preset.ListsModels || Preset.Local)) return null;
        try { return (await FetchModelsAsync(config, ct)).Ids; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // Not chat models: embeddings, speech, images, rerankers, safety classifiers.
    private static readonly Regex NonChatId = new(
        @"(embed|whisper|tts|orpheus|transcribe|speech|-audio|rerank|moderation|guard|safeguard|flux|stable-diffusion|sdxl|dall-e|playai|-image)",
        RegexOptions.IgnoreCase);
    private static readonly HashSet<string> NonChatTypes = new(StringComparer.OrdinalIgnoreCase)
        { "embedding", "embeddings", "image", "audio", "moderation", "rerank", "transcribe", "tts", "video" };

    /// <summary>GET /models: chat model ids (newest first when the service dates them), recording each one's context
    /// window, tool support and price as reported. Status 0 = success.</summary>
    private async Task<(List<string>? Ids, int Status, string? Error)> FetchModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/models");
        Authorize(request, config);
        using var response = await _httpClient.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (null, (int)response.StatusCode, Explain((int)response.StatusCode, body, config, ModelOf(config)));
        return (ReadModelList(Preset.Type, body), 0, null);
    }

    /// <summary>The chat model ids in a /models answer (public for the contract harness), learning each model's facts.</summary>
    public static List<string> ReadModelList(AiProviderType type, string body)
    {
        var models = new List<(string Id, long Created)>();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root      // Together answers with a bare array
            : root.TryGetProperty("data", out var d) ? d : root.TryGetProperty("models", out var ms) ? ms : default;
        if (data.ValueKind != JsonValueKind.Array) return [];

        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
            if (m.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && NonChatTypes.Contains(t.GetString()!)) continue;
            if (m.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.False) continue;
            if (m.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
                && arch.TryGetProperty("output_modalities", out var om) && om.ValueKind == JsonValueKind.Array
                && !om.EnumerateArray().Any(x => x.GetString() == "text")) continue;
            if (m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("completion_chat", out var cc) && cc.ValueKind == JsonValueKind.False) continue;
            if (NonChatId.IsMatch(id)) continue;

            long created = m.TryGetProperty("created", out var cr) && cr.TryGetInt64(out var c) ? c : 0;
            models.Add((id, created));
            Learn(type, id, m);
        }
        return models.Any(x => x.Created > 0)
            ? models.OrderByDescending(x => x.Created).Select(x => x.Id).ToList()
            : models.Select(x => x.Id).ToList();
    }

    private static void Learn(AiProviderType type, string id, JsonElement m)
    {
        static int? Int(JsonElement e, string n) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) && i > 0 ? i : null;
        static decimal? Dec(JsonElement e, string n)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var x)) return x;
            return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;
        }
        JsonElement Obj(JsonElement e, string n) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

        // Hugging Face's router lists each model's providers; the first live one stands for it.
        JsonElement provider = default;
        if (m.TryGetProperty("providers", out var provs) && provs.ValueKind == JsonValueKind.Array)
            provider = provs.EnumerateArray().FirstOrDefault(p => p.ValueKind == JsonValueKind.Object
                && (!p.TryGetProperty("status", out var s) || s.GetString() == "live"));

        int? window = Int(m, "context_length") ?? Int(m, "context_window") ?? Int(m, "max_context_length")
            ?? Int(Obj(m, "top_provider"), "context_length") ?? Int(Obj(m, "metadata"), "context_length") ?? Int(provider, "context_length");

        bool? tools = null;
        if (m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array)
            tools = sp.EnumerateArray().Any(x => x.GetString() == "tools");
        else if (Obj(m, "capabilities") is { ValueKind: JsonValueKind.Object } capsObj
                 && capsObj.TryGetProperty("function_calling", out var fc) && fc.ValueKind is JsonValueKind.True or JsonValueKind.False)
            tools = fc.GetBoolean();
        else if (m.TryGetProperty("supports_tools", out var st) && st.ValueKind is JsonValueKind.True or JsonValueKind.False)
            tools = st.GetBoolean();
        else if (m.TryGetProperty("supportsTools", out var st2) && st2.ValueKind is JsonValueKind.True or JsonValueKind.False)
            tools = st2.GetBoolean();
        else if (provider.ValueKind == JsonValueKind.Object && provider.TryGetProperty("supports_tools", out var pst)
                 && pst.ValueKind is JsonValueKind.True or JsonValueKind.False)
            tools = pst.GetBoolean();

        // Prices: OpenRouter quotes USD per token as strings ("prompt"/"completion"); Together and Hugging Face quote
        // USD per million tokens ("input"/"output").
        decimal? inM = null, outM = null;
        var pricing = Obj(m, "pricing");
        if (pricing.ValueKind != JsonValueKind.Object) pricing = Obj(provider, "pricing");
        decimal? cacheM = null;
        if (Dec(pricing, "prompt") is decimal pp && Dec(pricing, "completion") is decimal pc)
        {
            inM = pp * 1_000_000m;
            outM = pc * 1_000_000m;
            if (Dec(pricing, "input_cache_read") is decimal pr) cacheM = pr * 1_000_000m;
        }
        else if (Dec(pricing, "input") is decimal pi && Dec(pricing, "output") is decimal po) { inM = pi; outM = po; }
        if (inM is < 0 || outM is < 0) inM = outM = null;   // OpenRouter's routers quote -1 ("varies")

        Known[InfoKey(type, id)] = new ModelInfo(window, tools, inM, outM);
        if (inM is decimal i2 && outM is decimal o2)
            CostTrackingService.RegisterPricing(id, new ModelPricing(i2, o2, cacheM is >= 0 ? cacheM.Value : i2, i2));
    }
}
