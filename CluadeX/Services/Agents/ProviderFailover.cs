using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Agents;

// Switching model or provider in the middle of a task (owner 2026-11-08: "การเปลี่ยนโมเดลเจ้าใหม่กลางงานในทุกกรณีให้มันสมูธ
// ที่สุด ทำงานต่อกันได้ดี หรือหมดโควต้ากันก็เปลี่ยนกันได้ทันที โดยที่เราไม่ต้องมาสั่ง").
// Everything here is pure (no I/O, no settings, no UI): the agent loop asks, these answer. Contract: tests/apitest/FailoverWire.cs.
//
//   ProviderErrors       what a failed request means — out of quota, a passing rate limit, a busy server, an overflow…
//   FailoverPolicy       what to do about it (retry this step, fail over, give up) and which provider is next
//   QuotaBook            who ran out recently: the next turn starts elsewhere, and goes back once the cooldown is over
//   ProviderHistory      the conversation made valid for the next provider/model (signed blocks, ids, images, window)
//   HandoffBrief         the conversation as text, for a CLI agent that cannot take native tool turns
//   ProviderSwitchBoard  a switch asked for while a run is going: the loop picks it up between two requests

/// <summary>What a failed provider request means for the run.</summary>
public enum ProviderErrorKind
{
    None,
    /// <summary>The account cannot pay for more: credit, quota, a usage or spend limit (a daily one included).</summary>
    QuotaExhausted,
    /// <summary>Too many requests right now (per-minute limits): waiting fixes it.</summary>
    RateLimited,
    /// <summary>The service is busy or failing (5xx, 529 overloaded).</summary>
    Overloaded,
    /// <summary>No answer at all: the connection dropped, the request timed out.</summary>
    Network,
    /// <summary>The prompt does not fit the model's window — the loop compacts.</summary>
    ContextTooLong,
    /// <summary>The key or sign-in was refused.</summary>
    Auth,
    Other,
}

public static class ProviderErrors
{
    /// <summary>The kind of a provider error from its HTTP status (null = none known) and message. Checked in this
    /// order because the cases overlap: OpenAI's insufficient_quota is a 429, Gemini's per-minute limits say "quota
    /// exceeded", Anthropic's overflow is a 400.</summary>
    public static ProviderErrorKind Classify(int? status, string? message)
    {
        string e = (message ?? "").ToLowerInvariant();
        if (e.Length == 0 && status == null) return ProviderErrorKind.None;

        if (status == 413 || e.Contains("prompt is too long") || e.Contains("context_length_exceeded")
            || e.Contains("maximum context length") || e.Contains("context window too small") || e.Contains("input is too long")
            || e.Contains("exceeds the maximum number of tokens") || e.Contains("exceeds the context"))
            return ProviderErrorKind.ContextTooLong;

        bool perMinute = e.Contains("per minute") || e.Contains("perminute") || e.Contains("per-minute") || e.Contains("per_minute")
                         || e.Contains("requests per second") || e.Contains("tokens per min");
        bool perDay = e.Contains("per day") || e.Contains("perday") || e.Contains("per-day") || e.Contains("daily");
        bool quotaWord = e.Contains("quota") || e.Contains("โควต้า") || e.Contains("โควตา");
        bool creditWord = e.Contains("credit") || e.Contains("เครดิต") || e.Contains("insufficient balance")
                          || e.Contains("insufficient_balance") || e.Contains("payment required") || e.Contains("billing")
                          || e.Contains("spending limit") || e.Contains("spend limit") || e.Contains("usage limit")
                          || e.Contains("insufficient_quota") || e.Contains("5-hour limit") || e.Contains("weekly limit")
                          || e.Contains("limit reached|") || e.Contains("hit your usage limit") || e.Contains("out of credits");
        if (status == 402 || creditWord || (quotaWord && (perDay || !perMinute)))
            return perMinute && !perDay && !creditWord ? ProviderErrorKind.RateLimited : ProviderErrorKind.QuotaExhausted;

        if (status == 401 || e.Contains("invalid api key") || e.Contains("incorrect api key") || e.Contains("api key not valid")
            || e.Contains("api_key_invalid") || e.Contains("authentication_error") || e.Contains("invalid x-api-key")
            || e.Contains("not logged in") || e.Contains("please run /login") || e.Contains("ปฏิเสธ api key"))
            return ProviderErrorKind.Auth;

        if (status == 429 || e.Contains("rate limit") || e.Contains("rate_limit") || e.Contains("ratelimit") || e.Contains("too many requests")
            || e.Contains("resource_exhausted") || e.Contains("ถี่เกินกำหนด") || perMinute)
            return ProviderErrorKind.RateLimited;

        if (status is 500 or 502 or 503 or 504 or 529 || e.Contains("overloaded") || e.Contains("server_error")
            || e.Contains("service unavailable") || e.Contains("bad gateway") || e.Contains("internal server error")
            || e.Contains("เซิร์ฟเวอร์") && e.Contains("ไม่ว่าง"))
            return ProviderErrorKind.Overloaded;

        if (status == null && (e.Contains("timed out") || e.Contains("timeout") || e.Contains("connection") || e.Contains("no such host")
            || e.Contains("unreachable") || e.Contains("refused")))
            return ProviderErrorKind.Network;

        return ProviderErrorKind.Other;
    }

    /// <summary>The kind of an exception a provider threw. A user Stop is not an error (None).</summary>
    public static ProviderErrorKind Classify(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException:
                    return ProviderErrorKind.Network;
                case HttpRequestException { StatusCode: { } code } h:
                    return Classify((int)code, h.Message);
                case HttpRequestException h:
                    if (h.InnerException is System.Net.Sockets.SocketException or IOException) return ProviderErrorKind.Network;
                    return Classify(null, h.Message);
                case System.Net.Sockets.SocketException:
                    return ProviderErrorKind.Network;
                case OperationCanceledException:
                    return ProviderErrorKind.None;
            }
        }
        return ProviderErrorKind.Other;
    }

    private static readonly Regex RetryIn = new(
        @"(?:retry|try again)\s*(?:in|after)\s*(?:(?<m>\d+)\s*m(?:in(?:ute)?s?)?\s*)?(?:(?<s>\d+(?:\.\d+)?)\s*s(?:ec(?:ond)?s?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetryDelayField = new(@"""retryDelay""\s*:\s*""(?<s>\d+(?:\.\d+)?)s""", RegexOptions.Compiled);

    /// <summary>How long the service asked us to wait ("Please try again in 20.5s", Gemini's retryDelay), or null.</summary>
    public static TimeSpan? RetryAfter(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var f = RetryDelayField.Match(message);
        if (f.Success && double.TryParse(f.Groups["s"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fs))
            return TimeSpan.FromSeconds(fs);
        foreach (Match m in RetryIn.Matches(message))
        {
            double sec = 0;
            if (m.Groups["m"].Success) sec += int.Parse(m.Groups["m"].Value) * 60;
            if (m.Groups["s"].Success && double.TryParse(m.Groups["s"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double s)) sec += s;
            if (sec > 0) return TimeSpan.FromSeconds(sec);
        }
        return null;
    }
}

/// <summary>What kind of provider a failover candidate is: how a run can carry on with it.</summary>
public enum CandidateKind
{
    /// <summary>A cloud API with native tool turns: the native loop carries on in place, same step.</summary>
    Api,
    /// <summary>A CLI agent (Claude Dev, Codex, Gemini CLI, Grok Build): takes the task with a handoff brief.</summary>
    Cli,
    /// <summary>A model on this PC: native tool turns, a small window — last resort.</summary>
    Local,
}

/// <summary>One provider the run could carry on with.</summary>
/// <param name="Model">The model it would answer with (its configured model; null = its default).</param>
/// <param name="Ready">Set up and answering (a key, a login, a running server).</param>
public sealed record FailoverCandidate(AiProviderType Type, string? Model, bool Ready, CandidateKind Kind);

public enum StepAction { Retry, FailOver, Compact, Fail }

/// <summary>What the loop does with one failed request.</summary>
public readonly record struct StepDecision(StepAction Action, TimeSpan Delay, ProviderErrorKind Kind);

public static class FailoverPolicy
{
    /// <summary>A passing rate limit or a busy server: this step is asked again, at most this many times, before the
    /// run moves on to another provider (or stops).</summary>
    public const int MaxRetriesPerStep = 3;

    /// <summary>A wait the service asks for beyond this is not "a moment": fail over instead when there is somewhere to go.</summary>
    public static readonly TimeSpan LongWait = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    /// <param name="retriesThisStep">How many times this step was already asked again.</param>
    /// <param name="haveFallback">A ready provider to go to exists (FailoverPolicy.Chain is not empty).</param>
    public static StepDecision Decide(ProviderErrorKind kind, int retriesThisStep, bool autoFailover, bool haveFallback,
        TimeSpan? retryAfter = null)
    {
        bool canMove = autoFailover && haveFallback;
        switch (kind)
        {
            case ProviderErrorKind.QuotaExhausted:
            case ProviderErrorKind.Auth:
                return new(canMove ? StepAction.FailOver : StepAction.Fail, TimeSpan.Zero, kind);
            case ProviderErrorKind.RateLimited:
            case ProviderErrorKind.Overloaded:
            case ProviderErrorKind.Network:
                if (retryAfter is { } wait && wait > LongWait && canMove) return new(StepAction.FailOver, TimeSpan.Zero, kind);
                if (retriesThisStep < MaxRetriesPerStep) return new(StepAction.Retry, RetryDelay(retriesThisStep, retryAfter), kind);
                return new(canMove ? StepAction.FailOver : StepAction.Fail, TimeSpan.Zero, kind);
            case ProviderErrorKind.ContextTooLong:
                return new(StepAction.Compact, TimeSpan.Zero, kind);
            default:
                return new(StepAction.Fail, TimeSpan.Zero, kind);
        }
    }

    /// <summary>The wait before asking again: what the service asked for (1–60 s), else 2 s, 5 s, 10 s.</summary>
    public static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } r && r > TimeSpan.Zero)
            return r < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : r > LongWait ? LongWait : r;
        return Backoff[Math.Clamp(attempt, 0, Backoff.Length - 1)];
    }

    /// <summary>How long a provider that failed this way is passed over: new turns start elsewhere meanwhile, and go
    /// back to the owner's pick once it is over (the first request then tells whether it recovered).</summary>
    public static TimeSpan CooldownFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.QuotaExhausted => TimeSpan.FromMinutes(60),
        ProviderErrorKind.Auth => TimeSpan.FromMinutes(60),
        ProviderErrorKind.RateLimited => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(3),
    };

    /// <summary>The automatic order after a cloud API failed: the owner's other cloud APIs first (they carry on in
    /// place), then their CLI logins (a handoff brief), then a model on this PC (small window). After a CLI login
    /// failed it is the CLI logins first: both ways are a handoff there, and a login runs on its plan while an API
    /// bills every token (2026-11-12: five chats whose Codex login ran out went on with the Anthropic API for about
    /// $40 while a signed-in Claude login stood by). Never the BrainX room.</summary>
    private static readonly AiProviderType[] DefaultApiOrder =
    {
        AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini, AiProviderType.XAI, AiProviderType.AzureOpenAI,
        AiProviderType.OpenRouter, AiProviderType.DeepSeek, AiProviderType.Mistral, AiProviderType.Qwen, AiProviderType.Moonshot,
        AiProviderType.Zhipu, AiProviderType.Groq, AiProviderType.Together, AiProviderType.Fireworks, AiProviderType.Cerebras,
        AiProviderType.DeepInfra, AiProviderType.HuggingFace, AiProviderType.Novita, AiProviderType.SambaNova, AiProviderType.Nebius,
        AiProviderType.OpenAICompatible,
    };
    private static readonly AiProviderType[] DefaultCliOrder =
    {
        AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.GeminiDev, AiProviderType.GrokDev, AiProviderType.AntigravityDev,
    };

    public static bool IsCli(AiProviderType t) => t is AiProviderType.ClaudeDev or AiProviderType.GeminiDev or AiProviderType.AntigravityDev
        or AiProviderType.OpenAIDev or AiProviderType.GrokDev;

    /// <summary>
    /// Where the run goes next after <paramref name="failed"/>, best first: ready candidates only, none that is
    /// cooling (ran out recently), never the one that failed nor the BrainX room. <paramref name="ownerOrder"/>
    /// (Settings.FailoverOrder: provider names, best first) goes before the automatic order; "-Name" leaves one out.
    /// <paramref name="nativeOnly"/>: only those the native loop can carry on with in place (no CLI).
    /// </summary>
    public static List<FailoverCandidate> Chain(AiProviderType failed, IEnumerable<FailoverCandidate> available,
        IReadOnlyList<string>? ownerOrder = null, Func<AiProviderType, bool>? cooling = null, bool nativeOnly = false)
    {
        var excluded = new HashSet<AiProviderType> { failed, AiProviderType.BrainX };
        var wanted = new List<AiProviderType>();
        foreach (var raw in ownerOrder ?? Array.Empty<string>())
        {
            string name = (raw ?? "").Trim();
            bool leaveOut = name.StartsWith('-');
            if (leaveOut) name = name[1..].Trim();
            int slash = name.IndexOf('/');
            if (slash > 0) name = name[..slash];
            if (!Enum.TryParse<AiProviderType>(name, ignoreCase: true, out var t)) continue;
            if (leaveOut) excluded.Add(t);
            else if (!wanted.Contains(t)) wanted.Add(t);
        }
        var pool = available.Where(c => c.Ready && !excluded.Contains(c.Type) && !(cooling?.Invoke(c.Type) ?? false)
                                         && (!nativeOnly || c.Kind != CandidateKind.Cli))
            .GroupBy(c => c.Type).Select(g => g.First()).ToList();

        bool planFirst = IsCli(failed);   // a login ran out: the other logins (plan-priced) before a pay-per-token API
        int Rank(FailoverCandidate c)
        {
            int owner = wanted.IndexOf(c.Type);
            if (owner >= 0) return owner;
            int baseRank = 1000;
            return c.Kind switch
            {
                CandidateKind.Api => baseRank + (planFirst ? 200 : 0) + IndexOr(DefaultApiOrder, c.Type, 99),
                CandidateKind.Cli => baseRank + (planFirst ? 0 : 200) + IndexOr(DefaultCliOrder, c.Type, 99),
                _ => baseRank + 400 + (int)c.Type,
            };
        }
        return pool.OrderBy(Rank).ToList();
    }

    private static int IndexOr(AiProviderType[] order, AiProviderType t, int fallback)
    {
        int i = Array.IndexOf(order, t);
        return i < 0 ? fallback : i;
    }
}

/// <summary>Providers that failed recently (out of quota, rate-limited past the retries), and until when.</summary>
public sealed class QuotaBook
{
    public static QuotaBook Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<AiProviderType, (DateTime Until, ProviderErrorKind Kind)> _until = new();

    /// <summary><paramref name="t"/> failed this way at <paramref name="nowUtc"/>: passed over until the cooldown (or
    /// the wait the service asked for, if longer) is over.</summary>
    public void Note(AiProviderType t, ProviderErrorKind kind, DateTime nowUtc, TimeSpan? retryAfter = null)
    {
        var cool = FailoverPolicy.CooldownFor(kind);
        if (retryAfter is { } r && r > cool) cool = r;
        lock (_gate)
        {
            var until = nowUtc + cool;
            if (_until.TryGetValue(t, out var cur) && cur.Until > until) return;
            _until[t] = (until, kind);
        }
    }

    public bool IsCooling(AiProviderType t, DateTime nowUtc)
    {
        lock (_gate) return _until.TryGetValue(t, out var u) && u.Until > nowUtc;
    }

    /// <summary>Why <paramref name="t"/> is cooling (None when it is not).</summary>
    public ProviderErrorKind KindOf(AiProviderType t, DateTime nowUtc)
    {
        lock (_gate) return _until.TryGetValue(t, out var u) && u.Until > nowUtc ? u.Kind : ProviderErrorKind.None;
    }

    /// <summary>When <paramref name="t"/> is tried again (UTC), or null when it is not passed over.</summary>
    public DateTime? Until(AiProviderType t, DateTime nowUtc)
    {
        lock (_gate) return _until.TryGetValue(t, out var u) && u.Until > nowUtc ? u.Until : null;
    }

    /// <summary>It answered: no longer passed over.</summary>
    public void Clear(AiProviderType t)
    {
        lock (_gate) _until.Remove(t);
    }

    public void ClearAll()
    {
        lock (_gate) _until.Clear();
    }
}

/// <summary>The provider and model a conversation goes to next, and what that model can take.</summary>
/// <param name="ToContextTokens">The new model's window (ContextMemoryService.ContextWindowFor); 0 = do not fit.</param>
/// <param name="ToPerMessageEffort">The new model takes Anthropic's effort-only system messages.</param>
/// <param name="ReserveTokens">What else the request carries beside the messages: system prompt, tool schemas, the answer.</param>
public sealed record SwitchTarget(AiProviderType From, string? FromModel, AiProviderType To, string? ToModel,
    bool ToSeesImages, int ToContextTokens, bool ToPerMessageEffort = false, int ReserveTokens = 0);

/// <summary>What <see cref="ProviderHistory.ForTarget"/> changed — for the debug log and the tests.</summary>
public sealed class TranslationReport
{
    public bool SameModel;
    public int ThinkingDropped, ProviderItemsDropped, EffortMarksDropped, ImagesDropped, IdsRemapped, OrphansDropped;
    public int ExchangesFlattened, ToolResultsTrimmed, MessagesLeftOut;
    public int TokensBefore, TokensAfter, Budget;

    public override string ToString() => SameModel && TokensBefore <= Budget
        ? "same model — unchanged"
        : $"thinking -{ThinkingDropped} · provider items -{ProviderItemsDropped} · effort marks -{EffortMarksDropped} · images -{ImagesDropped}"
          + $" · ids remapped {IdsRemapped} · orphans -{OrphansDropped} · flattened {ExchangesFlattened} · trimmed {ToolResultsTrimmed}"
          + $" · left out {MessagesLeftOut} · ~{TokensBefore}→{TokensAfter} tok (budget {Budget})";
}

/// <summary>
/// The agent loop's conversation (<see cref="NativeMessage"/>s) made valid for another provider or model, so the run
/// carries on from the same step. What breaks a request after a switch, and what is done about it:
/// <list type="bullet">
/// <item>thinking / redacted_thinking: signed by the model that wrote them (Anthropic) — dropped for any other model;</item>
/// <item>provider_item: OpenAI reasoning (encrypted for that model), Gemini parts (thoughtSignature), a compatible
///   service's reasoning — dropped for any other model; Anthropic's own server-tool blocks stay for an Anthropic model;</item>
/// <item>Anthropic with the thinking it had dropped: a tool loop must continue from a signed thinking block, so the
///   last call and its result become plain text (the model reads what happened; nothing is redone);</item>
/// <item>effort-only system messages: only for a model that takes per-message effort;</item>
/// <item>tool ids: one form every API takes (letters, digits, _ and -, ≤ 40), the same mapping on both sides;</item>
/// <item>a call without its result (or the reverse) after anything was cut: removed in pairs;</item>
/// <item>images: a model that cannot see gets the note that says so, with the files they are saved in;</item>
/// <item>the window: old tool output is shortened, then the oldest steps are left out — never the task itself.</item>
/// </list>
/// The same model on the same provider gets the list back as it was (the prompt cache stays valid).
/// </summary>
public static class ProviderHistory
{
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled);

    public static bool SameModel(SwitchTarget t) =>
        t.From == t.To && string.Equals((t.FromModel ?? "").Trim(), (t.ToModel ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The provider_item family a provider writes and reads ("anthropic", "openai", "gemini", "compat:…").</summary>
    public static string Family(AiProviderType t) => t switch
    {
        AiProviderType.Anthropic => "anthropic",
        AiProviderType.OpenAI or AiProviderType.AzureOpenAI => "openai",
        AiProviderType.Gemini => "gemini",
        AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama => "local",
        _ when FailoverPolicy.IsCli(t) || t == AiProviderType.BrainX => "cli",
        _ => "compat",
    };

    public static List<NativeMessage> ForTarget(IReadOnlyList<NativeMessage> messages, SwitchTarget t, out TranslationReport report)
    {
        report = new TranslationReport { SameModel = SameModel(t) };
        int budget = t.ToContextTokens > 0 ? Math.Max(2048, (int)(t.ToContextTokens * 0.8) - Math.Max(0, t.ReserveTokens)) : int.MaxValue;
        report.Budget = budget;
        report.TokensBefore = EstimateTokens(messages);
        if (report.SameModel && report.TokensBefore <= budget)
        {
            report.TokensAfter = report.TokensBefore;
            return messages.ToList();
        }

        string toFamily = Family(t.To);
        var list = new List<NativeMessage>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Role == "system")
            {
                // Per-message effort: only an Anthropic model that takes it (the same model keeps what it had).
                if ((t.To == AiProviderType.Anthropic && t.ToPerMessageEffort) || report.SameModel)
                    list.Add(new NativeMessage { Role = "system", Effort = m.Effort });
                else report.EffortMarksDropped++;
                continue;
            }
            var copy = new NativeMessage { Role = m.Role };
            foreach (var b in m.Content)
            {
                if (!report.SameModel && b.Type is "thinking" or "redacted_thinking") { report.ThinkingDropped++; continue; }
                if (b.Type == "provider_item")
                {
                    bool keep = report.SameModel || (toFamily == "anthropic" && b.Provider == "anthropic");
                    if (!keep) { report.ProviderItemsDropped++; continue; }
                }
                var c = Clone(b);
                if (!t.ToSeesImages && c.Images is { Count: > 0 } imgs)
                {
                    string note = Vision.ToolImageWire.OmittedForBlindModel(imgs, null, "image");
                    if (c.Type == "tool_result") c.Content = Vision.ToolImageWire.AppendNote(c.Content, note);
                    else c.Text = Vision.ToolImageWire.AppendNote(c.Text, note);
                    report.ImagesDropped += imgs.Count;
                    c.Images = null;
                }
                if (c.Type == "text" && string.IsNullOrEmpty(c.Text) && c.Images is not { Count: > 0 }) continue;
                copy.Content.Add(c);
            }
            list.Add(copy);
        }

        report.IdsRemapped = RemapToolIds(list);
        report.OrphansDropped = StripOrphans(list);
        list = FixAlternation(list);

        if (t.To == AiProviderType.Anthropic && !report.SameModel && FlattenTrailingExchange(list))
            report.ExchangesFlattened++;

        if (report.TokensBefore > budget || EstimateTokens(list) > budget)
            FitWindow(ref list, budget, report);

        report.TokensAfter = EstimateTokens(list);
        return list;
    }

    private static ContentBlock Clone(ContentBlock b) => new()
    {
        Type = b.Type, Text = b.Text, Signature = b.Signature, Data = b.Data, Provider = b.Provider,
        Id = b.Id, Name = b.Name, Input = b.Input, ToolUseId = b.ToolUseId, Content = b.Content, IsError = b.IsError,
        Images = b.Images,
    };

    /// <summary>
    /// Tool ids every API takes, unique across the conversation: an id with other characters or over 40, an empty one
    /// (paired with the results that follow, in order), and one used again by a later step (Ollama numbers each step's
    /// calls from call_0) get a fresh id; the tool_results answering them follow. Returns how many were changed.
    /// </summary>
    public static int RemapToolIds(List<NativeMessage> list)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        int changed = 0, n = 0;
        Dictionary<string, string>? renamed = null;   // the last assistant turn's old id → new id
        Queue<string>? unnamed = null;                // its calls that had no id, in order
        foreach (var m in list)
        {
            if (m.Role == "assistant")
            {
                renamed = new Dictionary<string, string>(StringComparer.Ordinal);
                unnamed = new Queue<string>();
                foreach (var b in m.Content)
                {
                    if (b.Type != "tool_use") continue;
                    n++;
                    string id = b.Id ?? "";
                    string fresh = id;
                    if (!SafeId.IsMatch(id) || used.Contains(id))
                    {
                        fresh = "call_" + Hash($"{n}:{id}:{b.Name}");
                        changed++;
                    }
                    used.Add(fresh);
                    if (id.Length == 0) unnamed.Enqueue(fresh);
                    else if (fresh != id) renamed[id] = fresh;
                    b.Id = fresh;
                }
            }
            else if (m.Role == "user" && renamed != null)
            {
                foreach (var b in m.Content)
                {
                    if (b.Type != "tool_result") continue;
                    if (!string.IsNullOrEmpty(b.ToolUseId) && renamed.TryGetValue(b.ToolUseId!, out var nid)) b.ToolUseId = nid;
                    else if (string.IsNullOrEmpty(b.ToolUseId) && unnamed!.Count > 0) b.ToolUseId = unnamed.Dequeue();
                }
                renamed = null;
                unnamed = null;
            }
        }
        return changed;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    /// <summary>A call without a result after it, or a result without its call before it: both go (a request with
    /// either is refused). Messages left empty go too. Returns how many blocks were removed.</summary>
    public static int StripOrphans(List<NativeMessage> list)
    {
        int removed = 0;
        var openCalls = new HashSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        // results answer calls made BEFORE them
        foreach (var m in list)
            foreach (var b in m.Content)
            {
                if (b.Type == "tool_use" && !string.IsNullOrEmpty(b.Id)) openCalls.Add(b.Id!);
                else if (b.Type == "tool_result" && b.ToolUseId != null && openCalls.Contains(b.ToolUseId)) answered.Add(b.ToolUseId);
            }
        foreach (var m in list)
            removed += m.Content.RemoveAll(b =>
                (b.Type == "tool_use" && (string.IsNullOrEmpty(b.Id) || !answered.Contains(b.Id!)))
                || (b.Type == "tool_result" && (string.IsNullOrEmpty(b.ToolUseId) || !answered.Contains(b.ToolUseId!))));
        list.RemoveAll(m => m.Role != "system" && m.Content.Count == 0);
        return removed;
    }

    /// <summary>user / assistant in turn (two in a row are merged), the first one a user turn, nothing empty.</summary>
    public static List<NativeMessage> FixAlternation(List<NativeMessage> list)
    {
        var result = new List<NativeMessage>();
        foreach (var m in list)
        {
            if (m.Role == "system")
            {
                if (result.Count > 0 && result[^1].Role == "system") result[^1] = m; else result.Add(m);
                continue;
            }
            if (m.Content.Count == 0) continue;
            var lastReal = result.LastOrDefault(r => r.Role != "system");
            if (lastReal != null && lastReal.Role == m.Role) lastReal.Content.AddRange(m.Content);
            else result.Add(m);
        }
        if (result.FirstOrDefault(r => r.Role != "system") is { Role: "assistant" })
            result.Insert(0, new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "text", Text = "[Conversation continues]" } } });
        // A tool_result answers the assistant turn right before it: results merged into a user turn go first.
        foreach (var m in result.Where(r => r.Role == "user"))
        {
            var results = m.Content.Where(b => b.Type == "tool_result").ToList();
            if (results.Count == 0 || results.Count == m.Content.Count) continue;
            var rest = m.Content.Where(b => b.Type != "tool_result").ToList();
            m.Content.Clear();
            m.Content.AddRange(results);
            m.Content.AddRange(rest);
        }
        return result;
    }

    /// <summary>The conversation ends on tool results answering the last assistant turn: that call and its results
    /// become plain text, so the next request is not a tool-loop continuation (which Anthropic, with thinking on,
    /// requires to start from the signed thinking block another model never wrote). True when it did.</summary>
    public static bool FlattenTrailingExchange(List<NativeMessage> list)
    {
        int last = list.FindLastIndex(m => m.Role != "system");
        if (last < 1 || list[last].Role != "user" || !list[last].Content.Any(b => b.Type == "tool_result")) return false;
        int asst = list.FindLastIndex(last - 1, m => m.Role != "system");
        if (asst < 0 || list[asst].Role != "assistant") return false;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var a = list[asst];
        for (int i = 0; i < a.Content.Count; i++)
        {
            var b = a.Content[i];
            if (b.Type != "tool_use") continue;
            if (b.Id != null) names[b.Id] = b.Name ?? "tool";
            a.Content[i] = new ContentBlock { Type = "text", Text = $"[I called {b.Name} {ArgsText(b.Input, 1200)}]" };
        }
        var u = list[last];
        for (int i = 0; i < u.Content.Count; i++)
        {
            var b = u.Content[i];
            if (b.Type != "tool_result") continue;
            string name = b.ToolUseId != null && names.TryGetValue(b.ToolUseId, out var nm) ? nm : "the tool";
            u.Content[i] = new ContentBlock
            {
                Type = "text",
                Text = $"[{name} {(b.IsError == true ? "failed" : "returned")}:]\n{(string.IsNullOrEmpty(b.Content) ? "(no output)" : b.Content)}",
                Images = b.Images,
            };
        }
        u.Content.Add(new ContentBlock { Type = "text", Text = "[The model changed mid-task. Carry on from here — the steps above are done; do not redo them.]" });
        return true;
    }

    /// <summary>A tool call's arguments as short JSON.</summary>
    public static string ArgsText(JsonElement? input, int max)
    {
        string s = input is { } j && j.ValueKind != JsonValueKind.Undefined ? j.GetRawText() : "{}";
        return s.Length > max ? s[..max] + "…}" : s;
    }

    private static void FitWindow(ref List<NativeMessage> list, int budget, TranslationReport report)
    {
        // 1. Old tool output, shortened (the last two messages — what the model must answer now — stay whole).
        for (int pass = 0; pass < 2 && EstimateTokens(list) > budget; pass++)
        {
            int head = pass == 0 ? 1500 : 400, tail = pass == 0 ? 500 : 200;
            for (int i = 0; i < list.Count - 2; i++)
                foreach (var b in list[i].Content)
                {
                    if (b.Type == "tool_result" && (b.Content?.Length ?? 0) > head + tail + 80)
                    {
                        b.Content = Shorten(b.Content!, head, tail);
                        report.ToolResultsTrimmed++;
                    }
                    if (b.Type == "text" && i > 0 && (b.Text?.Length ?? 0) > (head + tail) * 2)
                        b.Text = Shorten(b.Text!, head * 2, tail * 2);
                }
        }
        // 2. The oldest steps, left out — the first message (the task) stays, and a note says what happened.
        int leftOut = 0;
        while (EstimateTokens(list) > budget && list.Count > 4)
        {
            int victim = list.FindIndex(1, m => m.Role != "system");
            if (victim < 0 || victim >= list.Count - 3) break;
            list.RemoveAt(victim);
            leftOut++;
        }
        if (leftOut > 0)
        {
            report.OrphansDropped += StripOrphans(list);
            list = FixAlternation(list);
            report.MessagesLeftOut = leftOut;
            var first = list.FirstOrDefault(m => m.Role == "user");
            first?.Content.Add(new ContentBlock
            {
                Type = "text",
                Text = $"[{leftOut} earlier message(s) of this task were left out to fit the new model's context window. "
                     + "The work they did is on disk — re-read a file if you need it, do not redo finished steps.]",
            });
        }
        // 3. Still too big: the remaining tool output, hard.
        if (EstimateTokens(list) > budget)
            foreach (var m in list)
                foreach (var b in m.Content)
                    if (b.Type == "tool_result" && (b.Content?.Length ?? 0) > 1200)
                    {
                        b.Content = Shorten(b.Content!, 800, 300);
                        report.ToolResultsTrimmed++;
                    }
    }

    private static string Shorten(string s, int head, int tail) =>
        s.Length <= head + tail ? s : s[..head] + $"\n…[{s.Length - head - tail} chars left out on the model switch]…\n" + s[^tail..];

    /// <summary>A rough token count of what the messages carry (text, tool input and output, thinking, images).</summary>
    public static int EstimateTokens(IEnumerable<NativeMessage> messages)
    {
        int n = 0;
        foreach (var m in messages)
            foreach (var b in m.Content)
            {
                n += CluadeX.Helpers.TokenBudget.EstimateTokens(b.Text) + CluadeX.Helpers.TokenBudget.EstimateTokens(b.Content);
                if (b.Input is { } j && j.ValueKind != JsonValueKind.Undefined) n += CluadeX.Helpers.TokenBudget.EstimateTokens(j.GetRawText());
                if (b.Type == "provider_item") n += CluadeX.Helpers.TokenBudget.EstimateTokens(b.Data) / 2;
                if (b.Images is { Count: > 0 } imgs) n += Vision.ToolImageWire.EstimateTokens(imgs);
            }
        return n;
    }
}

/// <summary>The run so far as text, for a CLI agent (Claude Dev, Codex, Gemini CLI, Grok Build) that takes over: the
/// task, every step with its arguments and what came back, the model's own notes — newest steps kept when it is long.</summary>
public static class HandoffBrief
{
    public static string FromNative(IReadOnlyList<NativeMessage> messages, string fromLabel, int maxChars = 14000)
    {
        var real = messages.Where(m => m.Role != "system").ToList();
        string task = real.FirstOrDefault(m => m.Role == "user")?.Content
            .Where(b => b.Type == "text").Select(b => b.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        if (task.Length > 4000) task = task[..4000] + "…";

        var names = real.SelectMany(m => m.Content).Where(b => b.Type == "tool_use" && b.Id != null)
            .GroupBy(b => b.Id!).ToDictionary(g => g.Key, g => g.First());
        var steps = new List<string>();
        bool firstUser = true;
        foreach (var m in real)
        {
            if (m.Role == "user" && firstUser) { firstUser = false; continue; }
            foreach (var b in m.Content)
            {
                switch (b.Type)
                {
                    case "text" when m.Role == "assistant" && !string.IsNullOrWhiteSpace(b.Text):
                        steps.Add("- model: " + Clip(b.Text!.Trim(), 600));
                        break;
                    case "tool_result":
                        names.TryGetValue(b.ToolUseId ?? "", out var call);
                        string head = call != null ? $"{call.Name} {ProviderHistory.ArgsText(call.Input, 300)}" : "a tool";
                        steps.Add($"- {head} → {(b.IsError == true ? "FAILED" : "ok")}: {Clip((b.Content ?? "").Trim(), 700)}");
                        break;
                }
            }
        }
        var sb = new StringBuilder();
        sb.Append("[Handed over mid-task from ").Append(fromLabel)
          .Append(". The steps below are DONE — their changes are on disk. Do not redo them; carry on from the next step.]\n\n");
        sb.Append("## The task\n").Append(task.Trim()).Append("\n\n## Steps done so far (oldest first)\n");
        int room = Math.Max(1000, maxChars - sb.Length - 200);
        var kept = new List<string>();
        int used = 0, skipped = 0;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            if (used + steps[i].Length + 1 > room) { skipped = i + 1; break; }
            kept.Insert(0, steps[i]);
            used += steps[i].Length + 1;
        }
        if (skipped > 0) sb.Append($"- ({skipped} earlier step(s) left out for length)\n");
        foreach (var s in kept) sb.Append(s).Append('\n');
        if (steps.Count == 0) sb.Append("- (none yet)\n");
        sb.Append("\nCarry on with the task from where it stopped, then report what you did.");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s.Replace("\r", "") : s[..max].Replace("\r", "") + "…";
}

/// <summary>
/// A switch asked for while a run is going (the owner picked another model, or another provider, mid-task). The
/// native loop registers its run here; a request stops only the reply in flight (the run goes on), and the loop
/// takes the switch before its next request — the conversation goes along through <see cref="ProviderHistory"/>.
/// </summary>
public static class ProviderSwitchBoard
{
    public sealed record PendingSwitch(AiProviderType Type, string? Model, string? Reason = null);

    private sealed class Slot
    {
        public readonly object Gate = new();
        public CancellationTokenSource? Call;
        public PendingSwitch? Pending;
        public bool Cancelled;
    }

    private static readonly ConditionalWeakTable<AgentRunScope, Slot> Slots = new();

    /// <summary>A native loop is running for this scope and can change provider where it stands.</summary>
    public static void Register(AgentRunScope? scope)
    {
        if (scope != null) Slots.AddOrUpdate(scope, new Slot());
    }

    public static void Unregister(AgentRunScope? scope)
    {
        if (scope != null) Slots.Remove(scope);
    }

    public static bool CanHotSwap(AgentRunScope? scope) => scope != null && Slots.TryGetValue(scope, out _);

    /// <summary>Ask the run to switch. The reply in flight (if any) is stopped — its tool calls never ran, nothing is
    /// half done — and the loop asks the new model the same step. False when no native loop runs for the scope.</summary>
    public static bool Request(AgentRunScope? scope, PendingSwitch to)
    {
        if (scope == null || !Slots.TryGetValue(scope, out var slot)) return false;
        lock (slot.Gate)
        {
            slot.Pending = to;
            if (slot.Call is { IsCancellationRequested: false } call)
            {
                slot.Cancelled = true;
                try { call.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        return true;
    }

    public static void BeginCall(AgentRunScope? scope, CancellationTokenSource call)
    {
        if (scope == null || !Slots.TryGetValue(scope, out var slot)) return;
        lock (slot.Gate)
        {
            slot.Call = call;
            if (slot.Pending != null) { slot.Cancelled = true; call.Cancel(); }   // asked for between two steps
        }
    }

    public static void EndCall(AgentRunScope? scope, CancellationTokenSource call)
    {
        if (scope == null || !Slots.TryGetValue(scope, out var slot)) return;
        lock (slot.Gate) { if (ReferenceEquals(slot.Call, call)) slot.Call = null; }
    }

    /// <summary>The loop caught its reply's cancel: was it this board (a switch), not a timeout or the user's Stop?</summary>
    public static bool WasSwitch(AgentRunScope? scope, CancellationTokenSource call)
    {
        if (scope == null || !Slots.TryGetValue(scope, out var slot)) return false;
        lock (slot.Gate) return slot.Cancelled && slot.Pending != null && call.IsCancellationRequested;
    }

    /// <summary>The switch waiting for the loop (null = none), taken: asked once.</summary>
    public static PendingSwitch? Take(AgentRunScope? scope)
    {
        if (scope == null || !Slots.TryGetValue(scope, out var slot)) return null;
        lock (slot.Gate)
        {
            var p = slot.Pending;
            slot.Pending = null;
            slot.Cancelled = false;
            return p;
        }
    }
}
