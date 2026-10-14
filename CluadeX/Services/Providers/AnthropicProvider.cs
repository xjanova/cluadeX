using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public partial class AnthropicProvider : ApiProviderBase, IModelLister
{
    private readonly CostTrackingService? _costTracker;

    // Pre-cached empty JSON object to avoid allocating a new JsonDocument on every tool_use block
    private static readonly JsonElement _emptyJsonObject =
        JsonDocument.Parse("{}").RootElement.Clone();

    public override string ProviderId => "Anthropic";
    public override string DisplayName => "Anthropic Claude";

    /// <summary>Used when no model is picked. Claude Opus 5.5 is the current Opus.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    // Newest first. IDs are the exact strings the API expects (no date suffix). This is only the offline
    // starting list: once a key connects, Settings replaces it with GET /v1/models, so a new release shows
    // up without a code change.
    public static readonly string[] KnownModels =
    [
        "claude-opus-5-5",         // default — current Opus ($4/$20 per MTok)
        "claude-fable-5-1",        // most capable (1M ctx, $10/$50) — "มหาเทพ"
        "claude-sonnet-5-5",       // near-Opus quality, faster ($2/$10)
        "claude-haiku-5-5",        // fastest / cheapest ($0.10/$0.50)
        // previous generation (still served)
        "claude-opus-5", "claude-fable-5", "claude-opus-4-8", "claude-sonnet-5",
        "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5",
    ];

    // thinking.block_binding is only accepted with this beta; without the header the field is a 400.
    private const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    // Thinking tokens count against max_tokens. The shared MaxTokens setting is sized for local models
    // (4096) and would cut a thinking model off mid-answer; streaming means a high cap costs nothing.
    private const int ThinkingMaxTokensFloor = 32000;

    // Server-side tool search: deferred tools stay in the request (the API searches and expands them) but out of the
    // model's context until it looks for them. The brain alone is 128 MCP tools, ~59k tokens a request.
    public const string ToolSearchType = "tool_search_tool_bm25_20251119";
    public const string ToolSearchName = "tool_search_tool_bm25";
    /// <summary>Below this many deferrable tools the search step costs more than it saves.</summary>
    public const int ToolSearchMinDeferred = 8;
    /// <summary>The API refused tool search once (a proxy, an older deployment): send every tool up front from then on.</summary>
    private bool _toolSearchRefused;

    /// <summary>Models the tool search tool runs on: Opus/Sonnet/Haiku 4.5 and later, Fable and Mythos.</summary>
    public static bool SupportsToolSearch(string? model)
    {
        var m = Regex.Match(model?.ToLowerInvariant() ?? "", @"claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d)(?!\d))?");
        if (!m.Success) return false;
        if (m.Groups[1].Value is "fable" or "mythos") return true;
        int v = int.Parse(m.Groups[2].Value) * 10 + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return v >= 45;
    }

    /// <summary>Whether this request defers tools: enabled, a model that can search, Anthropic's own endpoint (a remote
    /// proxy may not run server tools; a loopback one is a test double or a pass-through, and a refusal falls back
    /// anyway), and enough deferrable tools to be worth a search. Public for the contract harness.</summary>
    public static bool UseToolSearch(string model, string? baseUrl, bool enabled, IReadOnlyCollection<ToolSchema> tools)
    {
        if (!enabled || !SupportsToolSearch(model)) return false;
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)
                 && (u.IsLoopback || u.Host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase))))
            return false;
        return tools.Count(t => t.DeferLoading) >= ToolSearchMinDeferred;
    }

    /// <summary>The "tools" array: with search, the search tool first and the deferrable tools marked defer_loading
    /// (except one a forced tool_choice names). Same order every step, so the cached prefix holds. Public for the harness.</summary>
    public static List<Dictionary<string, object>> BuildToolDefs(IEnumerable<ToolSchema> tools, bool search, string? forcedTool = null)
    {
        var defs = new List<Dictionary<string, object>>();
        if (search) defs.Add(new() { ["type"] = ToolSearchType, ["name"] = ToolSearchName });
        foreach (var t in tools)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema,
            };
            if (search && t.DeferLoading && t.Name != forcedTool) d["defer_loading"] = true;
            defs.Add(d);
        }
        return defs;
    }

    /// <summary>Estimated tokens of the full definitions tool search sends as names only (the forced tool stays loaded).</summary>
    public static int DeferredDefinitionTokens(IEnumerable<ToolSchema> tools, string? forcedTool = null) =>
        tools.Where(t => t.DeferLoading && t.Name != forcedTool)
            .Sum(t => CluadeX.Helpers.TokenBudget.EstimateTokens(t.Name + " " + t.Description + " "
                + (t.InputSchema.ValueKind == JsonValueKind.Undefined ? "" : t.InputSchema.GetRawText())));

    /// <summary>What the model is told about the tools it has to search for: their names, by server. Names only —
    /// a few tokens each, against the full definitions they replace. Public for the harness.</summary>
    public static string DeferredToolsNote(IEnumerable<ToolSchema> tools)
    {
        static string PrefixOf(string name)
        {
            if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return "";
            int e = name.IndexOf("__", 5, StringComparison.Ordinal);
            return e > 5 ? name[..(e + 2)] : "";
        }
        var groups = tools.Where(t => t.DeferLoading).GroupBy(t => PrefixOf(t.Name)).OrderBy(g => g.Key, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("# Tools loaded on demand");
        sb.AppendLine($"These tools exist but are not loaded yet. To use one, first call {ToolSearchName} with a short description of what you need "
            + "(e.g. \"open a browser tab\", \"brain create note\"); the tools it finds become callable by their full name. "
            + "Search before telling the user a tool is missing.");
        foreach (var g in groups)
        {
            var names = g.Select(t => g.Key.Length > 0 ? t.Name[g.Key.Length..] : t.Name);
            sb.AppendLine(g.Key.Length > 0 ? $"- {g.Key}*: {string.Join(", ", names)}" : $"- {string.Join(", ", names)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>What a model's request surface accepts. Every Claude generation moved these — sampling
    /// removed (4.7+), adaptive thinking (4.6+), thinking that can't be switched off (Opus 5+, Fable,
    /// Sonnet/Haiku 5.5), forced tool_choice rejected (Fable 5.1, Opus/Sonnet 5.5) — and each wrong guess
    /// is an HTTP 400 the user sees as "my key doesn't work". Derived from the ID, so a model released
    /// after this build (listed live from /v1/models) gets the newest family's rules, not the oldest.</summary>
    public readonly record struct ModelTraits(
        bool Adaptive,           // thinking:{type:"adaptive"}; otherwise legacy enabled + budget_tokens
        bool AlwaysThinks,       // keeps thinking when the toggle is off — disabling it is a 400 or misbehaves
        bool ExplicitDisable,    // toggle off → thinking:{type:"disabled"} (otherwise the field is omitted)
        bool Sampling,           // accepts temperature
        bool OmitsThinkingText,  // display defaults to "omitted" — ask for "summarized" to see the reasoning
        bool ForcedToolChoice,   // accepts tool_choice any / tool
        int EffortSupport,       // output_config.effort: 0 none, 1 low–high (Opus 4.5), 2 + max (4.6), 3 + xhigh
        bool BetweenToolsOff = false); // thinking can't be disabled, but {type:"between_tools"} turns it off (Sonnet 5.5+)

    public static ModelTraits Traits(string? model)
    {
        // claude-<family>-<major>[-<minor>][-<date>]; a date (claude-sonnet-4-20250514) is not a minor.
        var m = Regex.Match(model?.ToLowerInvariant() ?? "", @"claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d)(?!\d))?");
        if (!m.Success)   // custom / unrecognised name: the oldest, most permissive shape
            return new(Adaptive: false, AlwaysThinks: false, ExplicitDisable: false,
                       Sampling: true, OmitsThinkingText: false, ForcedToolChoice: true, EffortSupport: 0);

        string family = m.Groups[1].Value;
        int v = int.Parse(m.Groups[2].Value) * 10 + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        bool fableTier = family is "fable" or "mythos";
        bool modern = fableTier || v >= 47;
        bool alwaysThinks = fableTier
            || (family == "opus" && v >= 50)
            || ((family is "sonnet" or "haiku") && v >= 55);
        bool forced = fableTier ? v < 51 : !((family is "opus" or "sonnet") && v >= 55);
        int effort = modern ? 3 : v == 46 ? 2 : (family == "opus" && v == 45) ? 1 : 0;
        return new(Adaptive: fableTier || v >= 46, AlwaysThinks: alwaysThinks,
                   ExplicitDisable: modern && !alwaysThinks, Sampling: !modern,
                   OmitsThinkingText: modern, ForcedToolChoice: forced, EffortSupport: effort,
                   BetweenToolsOff: family == "sonnet" && v >= 55);
    }

    /// <summary>The user's effort pick as this model accepts it, or null to leave the field out ("auto",
    /// or a model without effort — Sonnet/Haiku 4.5 reject it). A level the model lacks rounds down.
    /// <paramref name="capAtHigh"/>: thinking is explicitly disabled, which isn't accepted above high.</summary>
    public static string? EffortFor(ModelTraits t, string? pick, bool capAtHigh = false)
    {
        string p = EffortLevels.Normalize(pick);
        if (t.EffortSupport == 0 || p == EffortLevels.Auto) return null;
        if (p == "xhigh" && (t.EffortSupport < 3 || capAtHigh)) p = "high";
        if (p == "max" && (t.EffortSupport < 2 || capAtHigh)) p = "high";
        return p;
    }

    private static void SetEffort(Dictionary<string, object> requestObj, string? effort)
    {
        if (effort != null) requestObj["output_config"] = new Dictionary<string, object> { ["effort"] = effort };
    }

    /// <summary>Set thinking, effort and sampling for the target model (see <see cref="Traits"/>).
    /// Returns true when the request thinks: forced tool_choice is a 400 alongside thinking, and the
    /// adaptive shape needs the block-binding beta header.</summary>
    public static bool ApplyThinkingAndSampling(Dictionary<string, object> requestObj, string model, AppSettings settings)
    {
        var t = Traits(model);
        bool wantThinking = settings.ExtendedThinkingEnabled;
        // "auto" inside an agent run = the loop's pick for this step (CodeAgentService.SmartEffort).
        string effortPick = EffortLevels.Effective(settings.ReasoningEffort);

        // Think off on Claude Sonnet 5.5: {type:"disabled"} is a 400 and omitting the field still thinks; between_tools
        // is its off switch. No other field may go with it (display, block_binding) and it is refused above high.
        if (!wantThinking && t.BetweenToolsOff)
        {
            requestObj["thinking"] = new Dictionary<string, object> { ["type"] = "between_tools" };
            SetEffort(requestObj, EffortFor(t, effortPick, capAtHigh: true));
            requestObj["max_tokens"] = Math.Max(settings.MaxTokens, ThinkingMaxTokensFloor);
            return false;
        }

        if (t.Adaptive && (wantThinking || t.AlwaysThinks))
        {
            var thinking = new Dictionary<string, object>
            {
                ["type"] = "adaptive",
                // A thinking block is bound to the exact history that produced it. Microcompact trims old
                // turns, which on enforced accounts (created on/after 2026-08-31) fails the whole request —
                // drop the stale blocks instead and keep going.
                ["block_binding"] = new Dictionary<string, object> { ["prefix_mismatch_behavior"] = "drop_block" },
            };
            if (wantThinking && t.OmitsThinkingText) thinking["display"] = "summarized";
            requestObj["thinking"] = thinking;
            // Depth is effort, not a token budget. "auto" outside a run = the model's own default: adaptive thinking
            // already decides how much each request needs. It used to mean "high" whenever Think was on — on
            // Claude Opus 5.5 (default medium) that doubled the thinking of every chat for nothing.
            SetEffort(requestObj, EffortFor(t, effortPick));
            requestObj["max_tokens"] = Math.Max(settings.MaxTokens, ThinkingMaxTokensFloor);
            return true;
        }

        if (!t.Adaptive && wantThinking)
        {
            int budgetTokens = Math.Max(1024, settings.ThinkingBudgetTokens);
            int maxTokens = settings.MaxTokens;
            if (maxTokens <= budgetTokens) maxTokens = budgetTokens + 4096; // API requires max_tokens > budget
            requestObj["max_tokens"] = maxTokens;
            requestObj["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budgetTokens,
            };
            SetEffort(requestObj, EffortFor(t, effortPick));
            return true;
        }

        if (t.ExplicitDisable)
            requestObj["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
        SetEffort(requestObj, EffortFor(t, effortPick, capAtHigh: t.ExplicitDisable));
        // temperature only: Opus 4.1+ / Sonnet 4.5+ / Haiku 4.5 reject temperature and top_p together.
        if (t.Sampling)   // the API's range is 0–1; the shared slider goes to 2 for other providers
            requestObj["temperature"] = Math.Min((double)settings.Temperature, 1.0);
        return false;
    }

    private static string BaseUrlOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.anthropic.com" : config.BaseUrl.TrimEnd('/');

    private static string ModelOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.EffectiveModelId) ? DefaultModel : config.EffectiveModelId.Trim();

    /// <summary>The workspace to run in: the configured one, else ANTHROPIC_WORKSPACE_ID.</summary>
    internal static string? ResolveWorkspaceId(ProviderConfig config)
    {
        string? ws = config.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(ws)) ws = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID")?.Trim();
        return string.IsNullOrEmpty(ws) ? null : ws;
    }

    /// <summary>Auth + version headers for every call. A key that isn't scoped to one workspace (keys made
    /// without picking a workspace) is rejected unless each request names the workspace.</summary>
    private static void AddAuthHeaders(HttpRequestMessage request, ProviderConfig config)
    {
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        if (ResolveWorkspaceId(config) is { } ws)
        {
            // A stray non-ASCII character (Thai keyboard layout left on) makes Headers.Add throw a raw
            // FormatException; say what is actually wrong instead.
            if (ws.Any(c => c < '!' || c > '~'))
                throw new ConfigException(Loc("anthropic.err.workspaceInvalid"));
            request.Headers.Add("anthropic-workspace-id", ws);
        }
    }

    /// <summary>A settings value the request can't be built from; the message is already user-facing.</summary>
    private sealed class ConfigException(string message) : Exception(message);

    private static void AddBetaHeader(HttpRequestMessage request, string model, bool thinking)
    {
        if (!thinking) return;
        // Adaptive requests carry block_binding; legacy budget thinking needs interleaving to think between tools.
        request.Headers.Add("anthropic-beta", Traits(model).Adaptive ? ThinkingBindingBeta : InterleavedThinkingBeta);
    }

    private static string Loc(string key) => LocalizedResources.Instance[key];

    /// <summary>Turn an API error into something the user can act on. The API's own message stays in the
    /// text (key redacted) so nothing is hidden, and so callers that look for "too long" still match.</summary>
    public static string DescribeApiError(int status, string body, ProviderConfig config, string model)
    {
        if (!string.IsNullOrEmpty(config.ApiKey)) body = body.Replace(config.ApiKey, "[REDACTED]");
        string apiMsg = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var em)
                && em.GetString() is { Length: > 0 } s)
                apiMsg = s;
        }
        catch (JsonException) { /* not JSON — keep the raw body */ }

        string? hint =
            apiMsg.Contains("not scoped to a workspace", StringComparison.OrdinalIgnoreCase)
            || apiMsg.Contains("anthropic-workspace-id is required", StringComparison.OrdinalIgnoreCase)
                ? Loc("anthropic.err.workspaceRequired")
            : apiMsg.Contains("anthropic-workspace-id header must be", StringComparison.OrdinalIgnoreCase)
              || (status == 404 && apiMsg.StartsWith("Workspace", StringComparison.Ordinal))
                ? Loc("anthropic.err.workspaceInvalid")
            : status == 401 ? Loc("anthropic.err.badKey")
            : apiMsg.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ? Loc("anthropic.err.credit")
            : status == 404 && apiMsg.Contains("model", StringComparison.OrdinalIgnoreCase)
                ? string.Format(Loc("anthropic.err.model"), model)
            : status == 403 ? Loc("anthropic.err.permission")
            : status == 429 ? Loc("anthropic.err.rateLimit")
            : status >= 500 ? Loc("anthropic.err.overloaded")
            : null;

        string head = $"HTTP {status}: {apiMsg}";
        return hint == null ? head : $"{hint}\n({head})";
    }

    public AnthropicProvider(SettingsService settingsService, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        _costTracker = costTracker;
    }

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
            SetStatus(success ? $"Claude ready ({ModelOf(config)})" : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    /// <summary>
    /// Build Anthropic messages with strictly alternating user/assistant roles.
    /// Uses Dictionary instead of anonymous types for reliable serialization.
    /// </summary>
    private List<Dictionary<string, string>> BuildAnthropicMessages(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<Dictionary<string, string>>();
        string? lastRole = null;
        var contentBuffer = new StringBuilder();

        void FlushBuffer()
        {
            if (lastRole != null && contentBuffer.Length > 0)
            {
                messages.Add(new Dictionary<string, string>
                {
                    ["role"] = lastRole,
                    ["content"] = contentBuffer.ToString(),
                });
                contentBuffer.Clear();
            }
        }

        foreach (var msg in history)
        {
            string role = msg.Role switch
            {
                MessageRole.Assistant => "assistant",
                _ => "user",
            };

            string content = msg.Role switch
            {
                MessageRole.System => $"[System]\n{msg.Content}",
                MessageRole.CodeExecution => $"[Code Execution Result]\n{msg.Content}",
                MessageRole.ToolAction => $"[Tool: {msg.ToolName}]\n{msg.Content}",
                _ => msg.Content,
            };

            if (string.IsNullOrWhiteSpace(content)) continue;

            // Anthropic requires strictly alternating roles — merge consecutive same-role
            if (role == lastRole)
            {
                contentBuffer.Append("\n\n").Append(content);
            }
            else
            {
                FlushBuffer();
                lastRole = role;
                contentBuffer.Append(content);
            }
        }

        FlushBuffer();

        // Add user message — merge if last was also user
        if (lastRole == "user" && messages.Count > 0)
        {
            var last = messages[^1];
            messages.RemoveAt(messages.Count - 1);
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = last["content"] + "\n\n" + userMessage,
            });
        }
        else
        {
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = userMessage,
            });
        }

        // Anthropic requires first message to be "user"
        if (messages.Count > 0 && messages[0]["role"] == "assistant")
        {
            messages.Insert(0, new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = "[Conversation continues]",
            });
        }

        return messages;
    }

    /// <param name="hour">Cache the prefix (tools + system) for an hour instead of five minutes: an agent run waits on
    /// approvals, builds and the browser, and every wait past five minutes used to re-write the whole prefix. The 1-hour
    /// write costs 2x input once; it comes before the conversation's 5-minute breakpoints, as the API requires.</param>
    private static object BuildSystem(string? systemPrompt, AppSettings settings, bool hour = false)
    {
        // Prompt caching is GA — cache_control needs no beta header.
        if (!settings.PromptCachingEnabled) return systemPrompt!;
        var cache = new Dictionary<string, string> { ["type"] = "ephemeral" };
        if (hour) cache["ttl"] = "1h";
        return new[]
        {
            new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = systemPrompt!,
                ["cache_control"] = cache,
            },
        };
    }

    /// <summary>
    /// Conversation cache breakpoints, as Claude Code places them: one on the last block of the last message (this
    /// request's prefix is written to the cache) and one on the last block of the previous user turn (the prefix the
    /// previous request wrote, read back). With only the system prompt marked, every agent step re-sent the whole
    /// conversation as uncached input. With the system breakpoint that is 3 of the API's 4. Thinking blocks never take
    /// cache_control; the nearest other block before them does. Public for the contract harness.
    /// </summary>
    public static void MarkConversationCache(List<Dictionary<string, object>> messages)
    {
        if (messages.Count == 0) return;
        var ephemeral = new Dictionary<string, string> { ["type"] = "ephemeral" };
        int marked = 0;
        // The last message, then the user message before the last assistant one.
        var targets = new List<int> { messages.Count - 1 };
        for (int i = messages.Count - 2; i >= 0; i--)
            if (Equals(messages[i]["role"], "user") && i < messages.Count - 1) { targets.Add(i); break; }
        foreach (int idx in targets.Distinct())
        {
            if (messages[idx]["content"] is not List<object> blocks) continue;
            for (int b = blocks.Count - 1; b >= 0; b--)
            {
                if (blocks[b] is not Dictionary<string, object> block) continue;
                string type = block.TryGetValue("type", out var t) ? t as string ?? "" : "";
                if (type is "thinking" or "redacted_thinking") continue;
                if (type == "text" && block.TryGetValue("text", out var tx) && string.IsNullOrEmpty(tx as string)) continue;
                block["cache_control"] = ephemeral;
                marked++;
                break;
            }
            if (marked >= 2) break;
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

        var messages = BuildAnthropicMessages(history, userMessage);

        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = settings.MaxTokens,
            ["stream"] = true,
        };

        bool thinking = ApplyThinkingAndSampling(requestObj, model, settings);

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            requestObj["system"] = BuildSystem(systemPrompt, settings);

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/v1/messages");
        AddAuthHeaders(request, config);
        AddBetaHeader(request, model, thinking);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        UsageMeter.Report(UsageMeter.FromAnthropic(response, model));   // rate-limit headroom for the usage strip

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError(DescribeApiError((int)response.StatusCode, error, config, model));
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Anthropic sends output_tokens in message_delta as a CUMULATIVE running total and may emit
        // multiple message_delta frames — track the last recorded value so we bill only the increment.
        int recordedOutputTokens = 0;
        int promptTokens = 0;   // input + cache reads + cache writes (message_start): prices the output records (Haiku 5.5 tiers)
        bool anyText = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("event: "))
                continue;

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta))
                    {
                        string deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() ?? "" : "";

                        if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinkingProp))
                        {
                            // Extended thinking content — emit with marker for UI
                            content = thinkingProp.GetString();
                            if (!string.IsNullOrEmpty(content))
                                content = $"<thinking>{content}</thinking>";
                        }
                        else if (deltaType == "text_delta" && delta.TryGetProperty("text", out var textProp))
                        {
                            content = textProp.GetString();
                            if (!string.IsNullOrEmpty(content)) anyText = true;
                        }
                    }
                    else if (type == "message_delta")
                    {
                        if (!anyText && root.TryGetProperty("delta", out var md)
                            && md.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "refusal")
                            RaiseError(Loc("anthropic.err.refusal"));
                        if (root.TryGetProperty("usage", out var usageDelta))
                        {
                            // output_tokens here is CUMULATIVE — record only the increment since the last
                            // frame so multiple message_delta events don't multiply-count cost.
                            int cumulative = usageDelta.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : recordedOutputTokens;
                            int outDelta = cumulative - recordedOutputTokens;
                            if (_costTracker != null && outDelta > 0)
                            {
                                _costTracker.RecordUsage(model, 0, outDelta, isNewRequest: false, promptTokens: promptTokens);
                                recordedOutputTokens = cumulative;
                            }
                        }
                    }
                    else if (type == "message_start" && root.TryGetProperty("message", out var msgStart))
                    {
                        // Record input token usage from message_start event
                        if (msgStart.TryGetProperty("usage", out var startUsage))
                        {
                            int inTokens = startUsage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                            int cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv) ? crv : 0;
                            int cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0;
                            // 1-hour writes bill at 2x input, not the 5-minute price (this path used to record them all at 5 minutes)
                            int cache1h = startUsage.TryGetProperty("cache_creation", out var cbd)
                                && cbd.ValueKind == JsonValueKind.Object
                                && cbd.TryGetProperty("ephemeral_1h_input_tokens", out var c1) && c1.TryGetInt32(out var c1v) ? c1v : 0;
                            promptTokens = inTokens + cacheRead + cacheCreate;
                            if (_costTracker != null && (inTokens > 0 || cacheRead > 0 || cacheCreate > 0))
                                _costTracker.RecordUsage(model, inTokens, 0, cacheRead, cacheCreate, cacheWrite1hTokens: cache1h);
                        }
                    }
                    else if (type == "message_stop")
                    {
                        break;
                    }
                    else if (type == "error")
                    {
                        var errorMsg = root.TryGetProperty("error", out var err)
                            && err.TryGetProperty("message", out var em) ? em.GetString() ?? "Unknown error" : "Unknown error";
                        RaiseError($"Anthropic error: {errorMsg}");
                        yield break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed SSE chunks */ }

            if (content != null)
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

    // ═══════════════════════════════════════════
    // Phase 5: Native Tool Use Support
    // ═══════════════════════════════════════════

    public override bool SupportsNativeToolUse => true;

    private static object SerializeBlock(ContentBlock block) => block.Type switch
    {
        "tool_use" => new Dictionary<string, object>
        {
            ["type"] = "tool_use",
            ["id"] = block.Id ?? "",
            ["name"] = block.Name ?? "",
            ["input"] = block.Input ?? _emptyJsonObject,
        },
        // No images: the plain string form, byte-for-byte what it always was, so prompt-cache prefixes built before
        // tool images existed still match. With images: [text, image…] — the Messages API accepts image blocks
        // inside a tool_result.
        "tool_result" when block.Images is { Count: > 0 } images => new Dictionary<string, object>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = block.ToolUseId ?? "",
            ["content"] = ToolResultParts(block.Content, images),
            ["is_error"] = block.IsError ?? false,
        },
        "tool_result" => new Dictionary<string, object>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = block.ToolUseId ?? "",
            ["content"] = ToolResultText(block),
            ["is_error"] = block.IsError ?? false,
        },
        // Echoed exactly as received — an altered or unsigned thinking block fails the request.
        "thinking" => new Dictionary<string, object>
        {
            ["type"] = "thinking",
            ["thinking"] = block.Text ?? "",
            ["signature"] = block.Signature ?? "",
        },
        "redacted_thinking" => new Dictionary<string, object>
        {
            ["type"] = "redacted_thinking",
            ["data"] = block.Data ?? "",
        },
        // A server tool's call and result (tool search): passed back unchanged, as the API requires.
        "provider_item" when block.Provider == "anthropic" && !string.IsNullOrEmpty(block.Data) =>
            JsonDocument.Parse(block.Data).RootElement.Clone(),
        _ => new Dictionary<string, object> { ["type"] = "text", ["text"] = block.Text ?? "" },
    };

    /// <summary>The blocks of one message as this request sends them: other providers' raw state never, and this
    /// provider's server-tool blocks only while tool search is in the request (without it the API can't read them).</summary>
    /// <summary>
    /// A tool_result's text. The Messages API refuses an EMPTY error result ("content cannot be empty if `is_error`
    /// is true") — and refuses the whole request, so a git_commit that failed without printing anything failed the
    /// turn, and every later turn of that chat too (the history carries the result). An empty error says so instead.
    /// </summary>
    public static string ToolResultText(ContentBlock block) =>
        block.IsError == true && string.IsNullOrWhiteSpace(block.Content)
            ? "(the tool failed without an error message)"
            : block.Content ?? "";

    private static List<object> WireContent(NativeMessage m, bool search) =>
        m.Content.Where(b => b.Type != "provider_item" || (search && b.Provider == "anthropic"))
            .SelectMany(WithAttachedImages).ToList();

    /// <summary>A user message's attached images go in front of its text, as image blocks — what the Messages API
    /// documents for "look at this". Every other block is serialized as it always was.</summary>
    private static IEnumerable<object> WithAttachedImages(ContentBlock b)
    {
        if (b.Type == "text" && b.Images is { Count: > 0 } images)
            foreach (var img in images) yield return ImageBlock(img);
        yield return SerializeBlock(b);
    }

    private static Dictionary<string, object> ImageBlock(ToolImage img) => new()
    {
        ["type"] = "image",
        ["source"] = new Dictionary<string, object>
        {
            ["type"] = "base64",
            ["media_type"] = img.MediaType,
            ["data"] = img.Base64,
        },
    };

    /// <summary>tool_result content with images: the text first (never an empty text block — the API rejects
    /// those), then one base64 image block per image, in order.</summary>
    private static List<object> ToolResultParts(string? text, List<ToolImage> images)
    {
        var parts = new List<object>();
        if (!string.IsNullOrEmpty(text))
            parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = text });
        foreach (var img in images)
            parts.Add(new Dictionary<string, object>
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object>
                {
                    ["type"] = "base64",
                    ["media_type"] = img.MediaType,
                    ["data"] = img.Base64,
                },
            });
        return parts;
    }

    /// <summary>Every Claude model the API serves sees images.</summary>
    public override bool SupportsToolResultImages(string modelId) => true;

    /// <summary>One content block being assembled from the stream.</summary>
    private sealed class StreamBlock
    {
        public string Type = "";
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Json = new();
        public string? Id, Name, Signature, Data;
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
        string model = ModelOf(config);
        var settings = _settingsService.Settings;

        // Build tool definitions. A forced tool_choice that names one tool keeps that tool loaded.
        bool search = !_toolSearchRefused && UseToolSearch(model, config.BaseUrl, settings.ToolSearchEnabled, tools);
        string? forcedTool = toolChoice is { Length: > 0 } tc && tc is not ("auto" or "none" or "required" or "any") ? tc : null;
        var toolDefs = BuildToolDefs(tools, search, forcedTool);
        // For the spend panel's "without savings": the definitions tool search kept out of this request, net of the
        // names note that replaces them. A retry without search (refused) zeroes it on the same hint.
        var savingsHint = CostTrackingService.NextRequest;
        if (search && (savingsHint is null || savingsHint.Used))
            CostTrackingService.NextRequest = savingsHint = new CostTrackingService.RequestSavingsHint();
        if (savingsHint is { Used: false })
            savingsHint.ToolSearchAvoidedTokens = search
                ? Math.Max(0, DeferredDefinitionTokens(tools, forcedTool) - CluadeX.Helpers.TokenBudget.EstimateTokens(DeferredToolsNote(tools)))
                : 0;
        string requestSystem = search
            ? (string.IsNullOrWhiteSpace(systemPrompt) ? DeferredToolsNote(tools) : systemPrompt + "\n\n" + DeferredToolsNote(tools))
            : systemPrompt;

        // Build request
        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new Dictionary<string, object>
            {
                ["role"] = m.Role,
                ["content"] = WireContent(m, search),
            }).ToList(),
            ["max_tokens"] = settings.MaxTokens,
            ["tools"] = toolDefs,
            ["stream"] = true,
        };

        bool thinking = ApplyThinkingAndSampling(requestObj, model, settings);

        if (!string.IsNullOrWhiteSpace(requestSystem))
            requestObj["system"] = BuildSystem(requestSystem, settings, hour: true);

        if (settings.PromptCachingEnabled)
            MarkConversationCache((List<Dictionary<string, object>>)requestObj["messages"]);

        // Forced tool choice (planner / forced recovery). Forcing is a 400 while the model thinks and on
        // models that dropped it — there the loop's own instruction steers instead, under "auto".
        if (tools.Count > 0 && !string.IsNullOrWhiteSpace(toolChoice)
            && !toolChoice.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (toolChoice.Equals("none", StringComparison.OrdinalIgnoreCase))
                requestObj["tool_choice"] = new Dictionary<string, object> { ["type"] = "none" };
            else if (!thinking && Traits(model).ForcedToolChoice)
                requestObj["tool_choice"] =
                    toolChoice.Equals("required", StringComparison.OrdinalIgnoreCase) || toolChoice.Equals("any", StringComparison.OrdinalIgnoreCase)
                        ? new Dictionary<string, object> { ["type"] = "any" }
                        : new Dictionary<string, object> { ["type"] = "tool", ["name"] = toolChoice };
        }

        var body = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { WriteIndented = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/v1/messages");
        AddAuthHeaders(request, config);
        AddBetaHeader(request, model, thinking);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        UsageMeter.Report(UsageMeter.FromAnthropic(response, model));   // rate-limit headroom for the usage strip

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            // A broken or oversized tool image must not end the turn: retry ONCE without images, telling the
            // model the API refused it. Only for a 400 that names the image — an overflow or a bad key is not
            // about the picture.
            if ((int)response.StatusCode == 400 && HasToolImages(messages)
                && error.Contains("image", StringComparison.OrdinalIgnoreCase)
                && !error.Contains("too long", StringComparison.OrdinalIgnoreCase))
            {
                string why = ShortApiMessage(error, config.ApiKey);
                var retried = await ChatWithToolsAsync(WithoutImages(messages, why),
                    systemPrompt, tools, onTextDelta, ct, toolChoice);
                // Tell the loop, so it drops the refused image from its conversation instead of resending it (and
                // retrying) on every later step.
                retried.ImagesRefused = true;
                retried.ImagesRefusedReason = why;
                return retried;
            }
            // Tool search refused (a proxy, a deployment without it): every tool up front from now on, and this
            // request again — once.
            if (search && (int)response.StatusCode == 400
                && (error.Contains("tool_search", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("defer_loading", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("tool_reference", StringComparison.OrdinalIgnoreCase)))
            {
                _toolSearchRefused = true;
                return await ChatWithToolsAsync(messages, systemPrompt, tools, onTextDelta, ct, toolChoice);
            }
            throw new HttpRequestException(
                DescribeApiError((int)response.StatusCode, error, config, model),
                null,
                response.StatusCode);
        }

        // ─── Streaming parse (SSE) ───
        // We request stream:true so text appears live via onTextDelta instead of the user staring at a
        // spinner until the whole turn lands. Anthropic emits each block as
        // content_block_start → content_block_delta* → content_block_stop, interleaving thinking, text and
        // tool_use. Every block is kept by stream index: thinking blocks (with their signature) have to go
        // back verbatim and in place when the tool loop continues.
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var result = new NativeToolResponse();
        var blocks = new SortedDictionary<int, StreamBlock>();
        int finalOutputTokens = 0;
        bool streamEnded = false;

        while (!streamEnded && !reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            onTextDelta?.Invoke(string.Empty);   // keep-alive: any event proves the stream is alive (see IAiProvider)
            if (line.StartsWith("event: ")) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                string evt = typeProp.GetString() ?? "";

                switch (evt)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msgStart)
                            && msgStart.TryGetProperty("usage", out var startUsage))
                        {
                            int inTokens = startUsage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv) ? itv : 0;
                            int cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv) ? crv : 0;
                            int cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt32(out var ccv) ? ccv : 0;
                            int cache1h = startUsage.TryGetProperty("cache_creation", out var cbd)
                                && cbd.ValueKind == JsonValueKind.Object
                                && cbd.TryGetProperty("ephemeral_1h_input_tokens", out var c1) && c1.TryGetInt32(out var c1v) ? c1v : 0;
                            result.InputTokens = inTokens;
                            result.CacheReadTokens = cacheRead;
                            result.CacheWriteTokens = cacheCreate;
                            if (_costTracker != null && (inTokens > 0 || cacheRead > 0 || cacheCreate > 0))
                                _costTracker.RecordUsage(model, inTokens, 0, cacheRead, cacheCreate, cacheWrite1hTokens: cache1h);
                        }
                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("index", out var sIdxEl) && sIdxEl.TryGetInt32(out int sIdx)
                            && root.TryGetProperty("content_block", out var cb)
                            && cb.TryGetProperty("type", out var cbTypeEl))
                        {
                            var sb = new StreamBlock { Type = cbTypeEl.GetString() ?? "" };
                            switch (sb.Type)
                            {
                                case "tool_use":
                                    sb.Id = cb.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                                    sb.Name = cb.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                                    break;
                                case "text":
                                    if (cb.TryGetProperty("text", out var tp) && tp.GetString() is { Length: > 0 } t0)
                                    { sb.Text.Append(t0); onTextDelta?.Invoke(t0); }
                                    break;
                                case "thinking":
                                    if (cb.TryGetProperty("thinking", out var thp)) sb.Text.Append(thp.GetString());
                                    if (cb.TryGetProperty("signature", out var sgp)) sb.Signature = sgp.GetString();
                                    break;
                                case "redacted_thinking":
                                    sb.Data = cb.TryGetProperty("data", out var dp) ? dp.GetString() : null;
                                    break;
                                case "server_tool_use":   // its input streams as input_json_delta, like tool_use
                                    sb.Id = cb.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
                                    sb.Name = cb.TryGetProperty("name", out var sname) ? sname.GetString() ?? "" : "";
                                    break;
                                default:   // a server tool's result (tool_search_tool_result …) arrives whole
                                    sb.Data = cb.GetRawText();
                                    break;
                            }
                            blocks[sIdx] = sb;
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("index", out var dIdxEl) && dIdxEl.TryGetInt32(out int dIdx)
                            && blocks.TryGetValue(dIdx, out var blk)
                            && root.TryGetProperty("delta", out var delta)
                            && delta.TryGetProperty("type", out var dtEl))
                        {
                            switch (dtEl.GetString())
                            {
                                case "text_delta":
                                    if (delta.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } s)
                                    { blk.Text.Append(s); onTextDelta?.Invoke(s); }
                                    break;
                                case "thinking_delta":
                                    if (delta.TryGetProperty("thinking", out var thd)) blk.Text.Append(thd.GetString());
                                    break;
                                case "signature_delta":
                                    if (delta.TryGetProperty("signature", out var sig)) blk.Signature = sig.GetString();
                                    break;
                                case "input_json_delta":
                                    if (delta.TryGetProperty("partial_json", out var pj)) blk.Json.Append(pj.GetString());
                                    break;
                            }
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var mdDelta)
                            && mdDelta.TryGetProperty("stop_reason", out var sr)
                            && sr.ValueKind == JsonValueKind.String)
                            result.StopReason = sr.GetString() ?? result.StopReason;
                        if (root.TryGetProperty("usage", out var mdUsage)
                            && mdUsage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv))
                            finalOutputTokens = otv;
                        break;

                    case "error":
                        string errMsg = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var em)
                            ? em.GetString() ?? "Unknown error" : "Unknown error";
                        // Throw (don't also RaiseError) so there's a SINGLE surfacing path — the agent loop's
                        // caller sanitizes it and may retry. Calling both would double-post the error bubble.
                        throw new HttpRequestException($"Anthropic streaming error: {errMsg}");

                    case "message_stop":
                        streamEnded = true;
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch (JsonException) { /* skip a malformed SSE chunk */ }
        }

        // Assemble the emitted blocks in stream order.
        var textParts = new List<string>();
        var thinkingParts = new List<string>();
        foreach (var b in blocks.Values)
        {
            switch (b.Type)
            {
                case "text":
                    if (b.Text.Length == 0) break;   // the API rejects an empty text block on the way back
                    textParts.Add(b.Text.ToString());
                    result.Blocks.Add(new ContentBlock { Type = "text", Text = b.Text.ToString() });
                    break;
                case "thinking":
                    if (b.Text.Length > 0) thinkingParts.Add(b.Text.ToString());
                    if (!string.IsNullOrEmpty(b.Signature))   // unsigned = cut off mid-stream; can't be replayed
                        result.Blocks.Add(new ContentBlock { Type = "thinking", Text = b.Text.ToString(), Signature = b.Signature });
                    break;
                case "redacted_thinking":
                    if (!string.IsNullOrEmpty(b.Data))
                        result.Blocks.Add(new ContentBlock { Type = "redacted_thinking", Data = b.Data });
                    break;
                case "tool_use":
                    string raw = b.Json.ToString();
                    JsonElement input = _emptyJsonObject;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try { using var d = JsonDocument.Parse(raw); input = d.RootElement.Clone(); }
                        catch { input = _emptyJsonObject; }
                    }
                    result.ToolCalls.Add(new NativeToolCall { Id = b.Id ?? "", Name = b.Name ?? "", Input = input });
                    result.Blocks.Add(new ContentBlock { Type = "tool_use", Id = b.Id, Name = b.Name, Input = input });
                    break;
                case "server_tool_use":
                {
                    JsonElement sInput = _emptyJsonObject;
                    string sRaw = b.Json.ToString();
                    if (!string.IsNullOrWhiteSpace(sRaw))
                    {
                        try { using var sd = JsonDocument.Parse(sRaw); sInput = sd.RootElement.Clone(); }
                        catch { sInput = _emptyJsonObject; }
                    }
                    if (b.Name == ToolSearchName) result.ToolSearches++;
                    result.Blocks.Add(new ContentBlock
                    {
                        Type = "provider_item", Provider = "anthropic",
                        Data = JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["type"] = "server_tool_use", ["id"] = b.Id ?? "", ["name"] = b.Name ?? "", ["input"] = sInput,
                        }),
                    });
                    break;
                }
                default:   // a server tool's result block: kept verbatim for the next request
                    if (!string.IsNullOrEmpty(b.Data))
                        result.Blocks.Add(new ContentBlock { Type = "provider_item", Provider = "anthropic", Data = b.Data });
                    break;
            }
        }

        result.TextContent = textParts.Count > 0 ? string.Concat(textParts) : null;
        result.ThinkingContent = thinkingParts.Count > 0 ? string.Concat(thinkingParts) : null;
        result.OutputTokens = finalOutputTokens;
        if (_costTracker != null && finalOutputTokens > 0)
            _costTracker.RecordUsage(model, 0, finalOutputTokens, isNewRequest: false,
                promptTokens: result.InputTokens + result.CacheReadTokens + result.CacheWriteTokens);

        if (string.IsNullOrEmpty(result.StopReason))
            result.StopReason = result.ToolCalls.Count > 0 ? "tool_use" : "end_turn";
        // A safety classifier declined (HTTP 200, stop_reason "refusal"): say so instead of an empty turn.
        if (result.StopReason == "refusal" && result.TextContent == null && result.ToolCalls.Count == 0)
            result.TextContent = Loc("anthropic.err.refusal");

        return result;
    }

    public override async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return (false, Loc("anthropic.err.noKey"));

        string model = ModelOf(config);
        try
        {
            // A real (1-token) message, not just /v1/models: it also proves the model is enabled for this
            // workspace and that billing works — the two things a listing call can't tell you.
            var requestObj = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = "Hi" } },
                ["max_tokens"] = 1,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/v1/messages");
            AddAuthHeaders(request, config);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            UsageMeter.Report(UsageMeter.FromAnthropic(response, model));
            if (response.IsSuccessStatusCode)
            {
                // Name the workspace the call actually ran in, so the user can see which one is billed.
                string ws = response.Headers.TryGetValues("anthropic-workspace-id", out var wsv) ? wsv.FirstOrDefault() ?? "" : "";
                return (true, ws.Length > 0 ? $"Connected to Anthropic ({model}) · {ws}" : $"Connected to Anthropic ({model})");
            }

            var error = await response.Content.ReadAsStringAsync(ct);
            return (false, DescribeApiError((int)response.StatusCode, error, config, model));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ConfigException ex) { return (false, ex.Message); }   // already explained (bad workspace ID)
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(config.ApiKey))
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }

    /// <summary>Model IDs this key can call (GET /v1/models), newest first. Null when the listing fails,
    /// so the caller keeps the built-in <see cref="KnownModels"/>.</summary>
    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey)) return null;
        try
        {
            var ids = new List<string>();
            string? afterId = null;
            for (int page = 0; page < 10; page++)
            {
                string url = $"{BaseUrlOf(config)}/v1/models?limit=100"
                             + (afterId != null ? $"&after_id={Uri.EscapeDataString(afterId)}" : "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeaders(request, config);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var m in arr.EnumerateArray())
                        if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                            ids.Add(s);

                bool hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                afterId = root.TryGetProperty("last_id", out var li) && li.ValueKind == JsonValueKind.String ? li.GetString() : null;
                if (!hasMore || afterId == null) break;
            }
            return ids;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
