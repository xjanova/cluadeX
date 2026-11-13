// Switching provider or model in the middle of a task (owner 2026-11-08: "ดูเรื่อง การเปลี่ยน โมเดลเจ้าใหม่กลางงานในทุกกรณี
// ให้มันสมูธที่สุด ทำงานต่อกันได้ดี หรือหมดโควต้ากันก็เปลี่ยนกันได้ทันที โดยที่เราไม่ต้องมาสั่ง"). Checks the pure
// CluadeX.Services.Agents.ProviderFailover: what each provider's failure means (quota vs a passing rate limit), what the
// loop does about it, which provider comes next, the cooldown book, the conversation translated for every provider pair
// (no foreign signed block, ids every API takes and pairs, alternation, images, the window), the CLI handoff brief and
// the mid-run switch board. Loop(...) runs the real agent loop against localhost mocks (called from ImageWire's loop
// section, which owns the DI graph and its home/hook guards). No real API is called.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class FailoverWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · provider failover: what an error means, what the loop does, who is next");
        Classify(check);
        Decide(check);
        Chain(check);
        Book(check);

        section("offline · provider switch: the conversation translated for the next provider/model");
        Translate(check);
        Ids(check);
        Window(check);
        Brief(check);
        Board(check);
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  ProviderErrors
    // ══════════════════════════════════════════════════════════════════════════════════════════

    static void Classify(Action<string, string, bool, string> check)
    {
        var Q = ProviderErrorKind.QuotaExhausted;
        var R = ProviderErrorKind.RateLimited;
        var cases = new (string Who, int? Status, string Msg, ProviderErrorKind Want)[]
        {
            ("Anthropic credit", 400, "invalid_request_error: Your credit balance is too low to access the Anthropic API.", Q),
            ("Anthropic per-minute 429", 429, "rate_limit_error: Number of request tokens has exceeded your per-minute rate limit", R),
            ("Anthropic 529", 529, "overloaded_error: Overloaded", ProviderErrorKind.Overloaded),
            ("Anthropic overflow", 400, "prompt is too long: 210000 tokens > 200000 maximum", ProviderErrorKind.ContextTooLong),
            ("Anthropic bad key", 401, "authentication_error: invalid x-api-key", ProviderErrorKind.Auth),
            ("OpenAI insufficient_quota (a 429)", 429, "You exceeded your current quota, please check your plan and billing details. insufficient_quota", Q),
            ("OpenAI TPM 429", 429, "Rate limit reached for gpt-5 in organization org-x on tokens per min (TPM): Limit 30000. Please try again in 1.2s.", R),
            ("OpenAI overflow", 400, "context_length_exceeded: This model's maximum context length is 128000 tokens", ProviderErrorKind.ContextTooLong),
            ("Gemini per-minute RESOURCE_EXHAUSTED", 429, "RESOURCE_EXHAUSTED: Quota exceeded for metric: generate_content_free_tier_requests, limit: 15 per minute. \"retryDelay\":\"34s\"", R),
            ("Gemini per-day RESOURCE_EXHAUSTED", 429, "RESOURCE_EXHAUSTED: Quota exceeded for metric: generate_content_free_tier_requests, limit: 50 per day", Q),
            ("xAI credits 403", 403, "Your team has either used all available credits or reached its monthly spending limit", Q),
            ("DeepSeek 402", 402, "Insufficient Balance", Q),
            ("OpenRouter 402", 402, "This request requires more credits, or fewer max_tokens", Q),
            ("Claude CLI usage limit", null, "Claude AI usage limit reached|1762600000", Q),
            ("Codex usage limit", null, "You've hit your usage limit. Upgrade to Pro or try again later.", Q),
            ("Gemini CLI per-day", null, "Quota exceeded for quota metric 'Gemini 2.5 Pro Requests' and limit 'per day per user'", Q),
            ("Thai credit hint", null, "เครดิตหมดแล้ว เติมเงินก่อน", Q),
            ("Thai rate hint", null, "เรียกถี่เกินกำหนด", R),
            ("500", 500, "Internal server error", ProviderErrorKind.Overloaded),
            ("network, no status", null, "The SSL connection could not be established", ProviderErrorKind.Network),
            ("a plain 400", 400, "messages.1.content.0: unexpected tool_use_id found in tool_result blocks", ProviderErrorKind.Other),
            ("nothing", null, "", ProviderErrorKind.None),
        };
        var wrong = cases.Select(c => (c, got: ProviderErrors.Classify(c.Status, c.Msg))).Where(x => x.got != x.c.Want)
            .Select(x => $"{x.c.Who}: {x.got} (want {x.c.Want})").ToList();
        check("failover", $"Classify: {cases.Length} real provider errors — quota/credit vs a passing rate limit vs busy vs overflow vs key",
            wrong.Count == 0, string.Join("; ", wrong));

        var ex = new (string Who, Exception E, ProviderErrorKind Want)[]
        {
            ("HttpRequestException 402", new HttpRequestException("Payment Required", null, HttpStatusCode.PaymentRequired), Q),
            ("HttpRequestException 429 insufficient_quota", new HttpRequestException("insufficient_quota", null, HttpStatusCode.TooManyRequests), Q),
            ("HttpRequestException 429 plain", new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests), R),
            ("socket under HttpRequestException", new HttpRequestException("x", new System.Net.Sockets.SocketException(10061)), ProviderErrorKind.Network),
            ("TimeoutException", new TimeoutException("t"), ProviderErrorKind.Network),
            ("a Stop (OperationCanceledException)", new OperationCanceledException(), ProviderErrorKind.None),
            ("wrapped 529", new InvalidOperationException("outer", new HttpRequestException("overloaded", null, (HttpStatusCode)529)), ProviderErrorKind.Overloaded),
            ("anything else", new InvalidOperationException("z"), ProviderErrorKind.Other),
        };
        var wrongEx = ex.Select(c => (c, got: ProviderErrors.Classify(c.E))).Where(x => x.got != x.c.Want)
            .Select(x => $"{x.c.Who}: {x.got} (want {x.c.Want})").ToList();
        check("failover", "Classify(Exception): status from HttpRequestException, inner exceptions, a Stop is not an error",
            wrongEx.Count == 0, string.Join("; ", wrongEx));

        var waits = new (string Msg, double? Want)[]
        {
            ("Please try again in 1.2s.", 1.2),
            ("\"retryDelay\":\"34s\"", 34),
            ("Rate limit reached. Please try again in 1m30s", 90),
            ("retry after 20 seconds", 20),
            ("no wait here", null),
            ("", null),
        };
        var wrongWait = waits.Where(w => ProviderErrors.RetryAfter(w.Msg)?.TotalSeconds is var got && !(got == null ? w.Want == null : w.Want != null && Math.Abs(got.Value - w.Want.Value) < 0.01))
            .Select(w => $"\"{w.Msg}\" → {ProviderErrors.RetryAfter(w.Msg)} (want {w.Want})").ToList();
        check("failover", "RetryAfter: \"try again in 1.2s\", Gemini's retryDelay, \"1m30s\", \"after 20 seconds\"; none → null",
            wrongWait.Count == 0, string.Join("; ", wrongWait));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  FailoverPolicy.Decide
    // ══════════════════════════════════════════════════════════════════════════════════════════

    static void Decide(Action<string, string, bool, string> check)
    {
        var Q = ProviderErrorKind.QuotaExhausted;
        var R = ProviderErrorKind.RateLimited;
        var s = TimeSpan.FromSeconds;
        var cases = new (string Who, StepDecision Got, StepAction Want, TimeSpan Delay)[]
        {
            ("quota, a fallback → fail over at once", FailoverPolicy.Decide(Q, 0, true, true), StepAction.FailOver, TimeSpan.Zero),
            ("quota, nowhere to go → fail", FailoverPolicy.Decide(Q, 0, true, false), StepAction.Fail, TimeSpan.Zero),
            ("quota, auto failover off → fail", FailoverPolicy.Decide(Q, 0, false, true), StepAction.Fail, TimeSpan.Zero),
            ("bad key, a fallback → fail over", FailoverPolicy.Decide(ProviderErrorKind.Auth, 0, true, true), StepAction.FailOver, TimeSpan.Zero),
            ("rate limit, 1st → retry in 2 s (the step only, not the turn)", FailoverPolicy.Decide(R, 0, true, true), StepAction.Retry, s(2)),
            ("rate limit, 2nd → 5 s", FailoverPolicy.Decide(R, 1, true, true), StepAction.Retry, s(5)),
            ("rate limit, 3rd → 10 s", FailoverPolicy.Decide(R, 2, true, true), StepAction.Retry, s(10)),
            ("rate limit, retries used up → fail over", FailoverPolicy.Decide(R, 3, true, true), StepAction.FailOver, TimeSpan.Zero),
            ("rate limit, retries used up, nowhere → fail", FailoverPolicy.Decide(R, 3, true, false), StepAction.Fail, TimeSpan.Zero),
            ("rate limit, asked to wait 34 s → wait 34 s", FailoverPolicy.Decide(R, 0, true, true, s(34)), StepAction.Retry, s(34)),
            ("rate limit, asked to wait 2 min, a fallback → fail over", FailoverPolicy.Decide(R, 0, true, true, s(120)), StepAction.FailOver, TimeSpan.Zero),
            ("rate limit, asked to wait 2 min, nowhere → wait (clamped 60 s)", FailoverPolicy.Decide(R, 0, true, false, s(120)), StepAction.Retry, s(60)),
            ("asked to wait 0.2 s → 1 s", FailoverPolicy.Decide(R, 0, true, true, s(0.2)), StepAction.Retry, s(1)),
            ("busy 529 → retry", FailoverPolicy.Decide(ProviderErrorKind.Overloaded, 0, true, true), StepAction.Retry, s(2)),
            ("network → retry", FailoverPolicy.Decide(ProviderErrorKind.Network, 0, true, true), StepAction.Retry, s(2)),
            ("overflow → compact", FailoverPolicy.Decide(ProviderErrorKind.ContextTooLong, 0, true, true), StepAction.Compact, TimeSpan.Zero),
            ("another 400 → fail (nothing to fix by switching)", FailoverPolicy.Decide(ProviderErrorKind.Other, 0, true, true), StepAction.Fail, TimeSpan.Zero),
        };
        var wrong = cases.Where(c => c.Got.Action != c.Want || c.Got.Delay != c.Delay)
            .Select(c => $"{c.Who}: {c.Got.Action} {c.Got.Delay.TotalSeconds}s").ToList();
        check("failover", $"Decide: {cases.Length} cases — quota/key fail over, a passing limit retries the step, a long wait moves on",
            wrong.Count == 0, string.Join("; ", wrong));
        check("failover", "cooldowns: quota and key 60 min, rate limit 5 min, a busy server 3 min",
            FailoverPolicy.CooldownFor(ProviderErrorKind.QuotaExhausted) == TimeSpan.FromMinutes(60)
            && FailoverPolicy.CooldownFor(ProviderErrorKind.Auth) == TimeSpan.FromMinutes(60)
            && FailoverPolicy.CooldownFor(R) == TimeSpan.FromMinutes(5)
            && FailoverPolicy.CooldownFor(ProviderErrorKind.Overloaded) == TimeSpan.FromMinutes(3), "");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  FailoverPolicy.Chain
    // ══════════════════════════════════════════════════════════════════════════════════════════

    static void Chain(Action<string, string, bool, string> check)
    {
        var avail = new List<FailoverCandidate>
        {
            new(AiProviderType.Ollama, "llama", true, CandidateKind.Local),
            new(AiProviderType.ClaudeDev, null, true, CandidateKind.Cli),
            new(AiProviderType.DeepSeek, "deepseek-chat", true, CandidateKind.Api),
            new(AiProviderType.Anthropic, "claude-opus-5-5", true, CandidateKind.Api),
            new(AiProviderType.OpenAI, "gpt-5", true, CandidateKind.Api),
            new(AiProviderType.Gemini, "gemini-3-pro", false, CandidateKind.Api),   // not set up
            new(AiProviderType.BrainX, null, true, CandidateKind.Api),
            new(AiProviderType.OpenAIDev, null, true, CandidateKind.Cli),
        };
        string Names(IEnumerable<FailoverCandidate> l) => string.Join(",", l.Select(c => c.Type));

        var auto = FailoverPolicy.Chain(AiProviderType.Anthropic, avail);
        check("failover", "Chain (automatic): other cloud APIs first, then CLI logins, then this PC — never the failed one, BrainX or one not set up",
            Names(auto) == "OpenAI,DeepSeek,ClaudeDev,OpenAIDev,Ollama", Names(auto));
        var owner = FailoverPolicy.Chain(AiProviderType.Anthropic, avail, new[] { "ollama/llama3", " DeepSeek ", "nonsense", "-ClaudeDev" });
        check("failover", "Chain (owner's order): their names first (case, spaces and a /model ignored), \"-Name\" leaves one out, an unknown name is skipped",
            Names(owner) == "Ollama,DeepSeek,OpenAI,OpenAIDev", Names(owner));
        var afterCli = FailoverPolicy.Chain(AiProviderType.OpenAIDev, avail);
        check("failover", "Chain (a CLI login ran out): the other logins first — a plan before a pay-per-token API — then the APIs, then this PC",
            Names(afterCli) == "ClaudeDev,Anthropic,OpenAI,DeepSeek,Ollama", Names(afterCli));
        var afterCliOwner = FailoverPolicy.Chain(AiProviderType.OpenAIDev, avail, new[] { "Anthropic" });
        check("failover", "Chain (a CLI login ran out): the owner's own order still goes before it",
            Names(afterCliOwner) == "Anthropic,ClaudeDev,OpenAI,DeepSeek,Ollama", Names(afterCliOwner));
        // The run's own record of a switch it made by itself: what the chat's model button and its badge read.
        {
            var scope = new CluadeX.Services.AgentRunScope("failover-wire", null);
            var seen = new List<string>();
            void OnSwitch(CluadeX.Services.AgentRunScope sc) { if (ReferenceEquals(sc, scope)) seen.Add(sc.AutoSwitch?.ToLabel ?? "(cleared)"); }
            CluadeX.Services.AgentRunScope.AutoSwitchChanged += OnSwitch;
            try
            {
                check("failover", "AutoSwitch: a run on its pick records none", scope.AutoSwitch == null, "");
                scope.NoteAutoSwitch(AiProviderType.OpenAIDev, "OpenAI Dev · gpt-6.1-sol", AiProviderType.ClaudeDev, "Claude Dev · claude-opus-5-5", "claude-opus-5-5");
                check("failover", "AutoSwitch: the hop is recorded with both labels and the stand-in's model",
                    scope.AutoSwitch is { From: AiProviderType.OpenAIDev, To: AiProviderType.ClaudeDev, FromLabel: "OpenAI Dev · gpt-6.1-sol", ToModel: "claude-opus-5-5" }, scope.AutoSwitch?.ToString() ?? "null");
                scope.NoteAutoSwitch(AiProviderType.ClaudeDev, "Claude Dev · claude-opus-5-5", AiProviderType.Anthropic, "Anthropic Claude · claude-opus-5-5", null);
                check("failover", "AutoSwitch: a second hop keeps what the owner picked as \"from\" (not the stand-in in between)",
                    scope.AutoSwitch is { From: AiProviderType.OpenAIDev, FromLabel: "OpenAI Dev · gpt-6.1-sol", To: AiProviderType.Anthropic, ToModel: "" }, scope.AutoSwitch?.ToString() ?? "null");
                scope.NoteAutoSwitch(AiProviderType.Anthropic, "Anthropic Claude", AiProviderType.OpenAIDev, "OpenAI Dev · gpt-6.1-sol", "gpt-6.1-sol");
                check("failover", "AutoSwitch: back on the pick = no stand-in", scope.AutoSwitch == null, scope.AutoSwitch?.ToString() ?? "null");
                scope.NoteAutoSwitch(AiProviderType.OpenAIDev, "OpenAI Dev", AiProviderType.Anthropic, "Anthropic Claude", null);
                scope.ClearAutoSwitch();
                scope.ClearAutoSwitch();   // nothing to clear: no second event
                check("failover", "AutoSwitch: the owner's own pick clears it, and every change is told once",
                    scope.AutoSwitch == null && string.Join("|", seen) == "Claude Dev · claude-opus-5-5|Anthropic Claude · claude-opus-5-5|(cleared)|Anthropic Claude|(cleared)", string.Join("|", seen));
            }
            finally { CluadeX.Services.AgentRunScope.AutoSwitchChanged -= OnSwitch; }

            var book = new QuotaBook();
            var t0 = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            book.Note(AiProviderType.OpenAIDev, ProviderErrorKind.QuotaExhausted, t0);
            check("failover", "QuotaBook.Until: when the pick is tried again (the badge's \"back about HH:mm\"), null once it is",
                book.Until(AiProviderType.OpenAIDev, t0.AddMinutes(10)) == t0.AddMinutes(60)
                && book.Until(AiProviderType.OpenAIDev, t0.AddMinutes(61)) == null && book.Until(AiProviderType.Anthropic, t0) == null, "");
        }

        // How loud she is: one 0–100 setting for her clips, the read-aloud player and the mic window's page.
        check("volume", "BuddyVolume: 50 is the level she always had (0.5 in the app, full in the mic window's page)",
            CluadeX.Services.Voice.BuddyVolume.Level(50) == 0.5 && CluadeX.Services.Voice.BuddyVolume.PageLevelOf(50) == 1.0, "");
        check("volume", "BuddyVolume: 0 is silent, 100 is full, and a value out of range counts as its nearest end",
            CluadeX.Services.Voice.BuddyVolume.Level(0) == 0 && CluadeX.Services.Voice.BuddyVolume.Level(100) == 1.0
            && CluadeX.Services.Voice.BuddyVolume.Level(-5) == 0 && CluadeX.Services.Voice.BuddyVolume.Level(400) == 1.0
            && CluadeX.Services.Voice.BuddyVolume.PageLevelOf(20) == 0.4 && CluadeX.Services.Voice.BuddyVolume.PageLevelOf(0) == 0, "");
        check("volume", "the setting starts at that level", new CluadeX.Models.AppSettings().BuddyVolume == CluadeX.Services.Voice.BuddyVolume.Default, "");
        var cool = FailoverPolicy.Chain(AiProviderType.Anthropic, avail, null, t => t == AiProviderType.OpenAI);
        check("failover", "Chain: a provider that ran out recently (cooling) is passed over", Names(cool) == "DeepSeek,ClaudeDev,OpenAIDev,Ollama", Names(cool));
        var native = FailoverPolicy.Chain(AiProviderType.Anthropic, avail, null, null, nativeOnly: true);
        check("failover", "Chain(nativeOnly): only those the loop can carry on with in place (no CLI)", Names(native) == "OpenAI,DeepSeek,Ollama", Names(native));
        var none = FailoverPolicy.Chain(AiProviderType.Ollama, new[] { new FailoverCandidate(AiProviderType.Ollama, null, true, CandidateKind.Local) });
        check("failover", "Chain: only the failed one set up → empty (the run stops with the reason)", none.Count == 0, Names(none));
        check("failover", "IsCli: the five CLI agents, not the APIs",
            new[] { AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.GeminiDev, AiProviderType.GrokDev, AiProviderType.AntigravityDev }.All(FailoverPolicy.IsCli)
            && !FailoverPolicy.IsCli(AiProviderType.Anthropic) && !FailoverPolicy.IsCli(AiProviderType.Ollama), "");
    }

    static void Book(Action<string, string, bool, string> check)
    {
        var b = new QuotaBook();
        var t0 = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        b.Note(AiProviderType.OpenAI, ProviderErrorKind.QuotaExhausted, t0);
        b.Note(AiProviderType.Gemini, ProviderErrorKind.RateLimited, t0);
        b.Note(AiProviderType.XAI, ProviderErrorKind.RateLimited, t0, TimeSpan.FromHours(2));
        bool quota = b.IsCooling(AiProviderType.OpenAI, t0.AddMinutes(59)) && !b.IsCooling(AiProviderType.OpenAI, t0.AddMinutes(61))
                     && b.KindOf(AiProviderType.OpenAI, t0.AddMinutes(1)) == ProviderErrorKind.QuotaExhausted
                     && b.KindOf(AiProviderType.OpenAI, t0.AddMinutes(61)) == ProviderErrorKind.None;
        bool rate = b.IsCooling(AiProviderType.Gemini, t0.AddMinutes(4)) && !b.IsCooling(AiProviderType.Gemini, t0.AddMinutes(6));
        bool longer = b.IsCooling(AiProviderType.XAI, t0.AddMinutes(100));
        b.Note(AiProviderType.OpenAI, ProviderErrorKind.Overloaded, t0.AddMinutes(1));   // a shorter later note
        bool keeps = b.IsCooling(AiProviderType.OpenAI, t0.AddMinutes(59)) && b.KindOf(AiProviderType.OpenAI, t0.AddMinutes(30)) == ProviderErrorKind.QuotaExhausted;
        b.Clear(AiProviderType.OpenAI);
        bool cleared = !b.IsCooling(AiProviderType.OpenAI, t0.AddMinutes(1)) && b.IsCooling(AiProviderType.Gemini, t0.AddMinutes(1));
        b.ClearAll();
        bool all = !b.IsCooling(AiProviderType.Gemini, t0) && !b.IsCooling(AiProviderType.XAI, t0) && !b.IsCooling(AiProviderType.Anthropic, t0);
        check("failover", "QuotaBook: quota cools 60 min, a rate limit 5, a longer asked wait wins, a shorter later note does not shorten it, Clear/ClearAll",
            quota && rate && longer && keeps && cleared && all, $"quota={quota} rate={rate} longer={longer} keeps={keeps} cleared={cleared} all={all}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  ProviderHistory — every source × target
    // ══════════════════════════════════════════════════════════════════════════════════════════

    const string Task1 = "TASK-FAILOVER: fix the null check in a.cs";

    static JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    static ContentBlock Txt(string t) => new() { Type = "text", Text = t };
    static ContentBlock Use(string? id, string name, string args = "{\"path\":\"a.cs\"}") => new() { Type = "tool_use", Id = id, Name = name, Input = J(args) };
    static ContentBlock Res(string? id, string content, List<ToolImage>? img = null) => new() { Type = "tool_result", ToolUseId = id, Content = content, Images = img };
    static NativeMessage U(params ContentBlock[] b) => new() { Role = "user", Content = b.ToList() };
    static NativeMessage A(params ContentBlock[] b) => new() { Role = "assistant", Content = b.ToList() };
    static NativeMessage Effort(string e) => new() { Role = "system", Effort = e };

    static readonly List<ToolImage> Shot = new() { new ToolImage("image/png", "SHOT-B64-DATA", 10, 10, @"C:\shots\001.png") };

    /// <summary>A run on <paramref name="from"/>: the task, two steps (the second with a screenshot), the blocks only
    /// that provider writes, and an effort mark.</summary>
    static List<NativeMessage> Source(AiProviderType from)
    {
        ContentBlock[] Own(int step) => from switch
        {
            AiProviderType.Anthropic => step == 1
                ? new[] { new ContentBlock { Type = "thinking", Text = "THINK-ANTH", Signature = "SIG-ANTH" } }
                : new[] { new ContentBlock { Type = "redacted_thinking", Data = "REDACTED-ANTH" },
                          new ContentBlock { Type = "provider_item", Provider = "anthropic", Data = "{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_1\"}" } },
            AiProviderType.OpenAI => new[] { new ContentBlock { Type = "provider_item", Provider = "openai", Data = "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"encrypted_content\":\"ENC-OPENAI\"}" } },
            AiProviderType.Gemini => new[] { new ContentBlock { Type = "provider_item", Provider = "gemini", Data = "[{\"functionCall\":{\"name\":\"read_file\"},\"thoughtSignature\":\"GEM-SIG\"}]" } },
            AiProviderType.DeepSeek => new[] { new ContentBlock { Type = "provider_item", Provider = "compat:reasoning_content", Data = "DS-REASONING" } },
            _ => Array.Empty<ContentBlock>(),
        };
        // Each provider's own id forms: Anthropic toolu_…, OpenAI call_…|fc_… (the pair the Responses API keeps), Gemini
        // its synthetic names, Ollama call_0 on every step (it numbers each reply's calls from 0).
        (string, string) Ids() => from switch
        {
            AiProviderType.Anthropic => ("toolu_01ABCdef", "toolu_02GHIjkl"),
            AiProviderType.OpenAI => ("call_abc|fc_123", "call_def|fc_456"),
            AiProviderType.Gemini => ("gemini_call_0_read_file", "gemini_call_1_edit_file"),
            AiProviderType.Ollama => ("call_0", "call_0"),
            _ => ("call_xyz-1", "call_xyz-2"),
        };
        var (id1, id2) = Ids();
        var a1 = Own(1).Append(Txt("Looking at a.cs first.")).Append(Use(id1, "read_file")).ToArray();
        var a2 = Own(2).Append(Use(id2, "edit_file", "{\"path\":\"a.cs\",\"old\":\"x\",\"new\":\"y\"}")).ToArray();
        return new List<NativeMessage>
        {
            U(Txt(Task1)),
            A(a1),
            U(Res(id1, "STEP1-OUTPUT: class A { void F(string s) { s.Trim(); } }")),
            Effort("high"),
            A(a2),
            U(Res(id2, "STEP2-OUTPUT: edited", Shot)),
        };
    }

    static SwitchTarget To(AiProviderType from, AiProviderType to, string? toModel = null, bool sees = true, int ctx = 200_000, bool perMsg = false) =>
        new(from, ModelOf(from), to, toModel ?? ModelOf(to), sees, ctx, perMsg);

    static string ModelOf(AiProviderType t) => t switch
    {
        AiProviderType.Anthropic => "claude-opus-5-5",
        AiProviderType.OpenAI => "gpt-5",
        AiProviderType.Gemini => "gemini-3-pro",
        AiProviderType.DeepSeek => "deepseek-reasoner",
        AiProviderType.Ollama => "qwen3",
        _ => "m",
    };

    static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,40}$");

    /// <summary>Everything that makes a provider refuse the request, as a list of problems (empty = valid).</summary>
    static List<string> Problems(List<NativeMessage> list, SwitchTarget t)
    {
        var p = new List<string>();
        var real = list.Where(m => m.Role != "system").ToList();
        if (real.Count == 0 || real[0].Role != "user") p.Add("does not start with a user turn");
        for (int i = 1; i < real.Count; i++)
            if (real[i].Role == real[i - 1].Role) p.Add($"two {real[i].Role} turns in a row at {i}");
        var ids = new HashSet<string>();
        for (int i = 0; i < real.Count; i++)
        {
            var m = real[i];
            if (m.Content.Count == 0) p.Add($"empty message {i}");
            foreach (var b in m.Content)
            {
                if (b.Type is "thinking" or "redacted_thinking") p.Add($"{b.Type} block kept for {t.To}/{t.ToModel}");
                if (b.Type == "provider_item" && !(t.To == AiProviderType.Anthropic && b.Provider == "anthropic")) p.Add($"provider_item {b.Provider} kept for {t.To}");
                if (b.Type == "tool_use")
                {
                    if (!SafeId.IsMatch(b.Id ?? "")) p.Add($"tool id \"{b.Id}\" not every API takes");
                    if (!ids.Add(b.Id ?? "")) p.Add($"tool id \"{b.Id}\" used twice");
                    var next = i + 1 < real.Count ? real[i + 1] : null;
                    if (next == null || !next.Content.Any(r => r.Type == "tool_result" && r.ToolUseId == b.Id)) p.Add($"call {b.Id} has no result right after it");
                }
                if (b.Type == "tool_result")
                {
                    var prev = i > 0 ? real[i - 1] : null;
                    if (prev == null || !prev.Content.Any(u => u.Type == "tool_use" && u.Id == b.ToolUseId)) p.Add($"result {b.ToolUseId} answers no call right before it");
                }
                if (!t.ToSeesImages && b.Images is { Count: > 0 }) p.Add($"image sent to a model that cannot see ({t.To})");
            }
            if (m.Role == "user")
            {
                int lastResult = m.Content.FindLastIndex(b => b.Type == "tool_result");
                int firstOther = m.Content.FindIndex(b => b.Type != "tool_result");
                if (lastResult >= 0 && firstOther >= 0 && firstOther < lastResult) p.Add($"user turn {i}: text before a tool_result");
            }
        }
        if (!(real.FirstOrDefault()?.Content.Any(b => b.Text?.Contains(Task1) == true) ?? false)) p.Add("the task is gone from the first message");
        return p;
    }

    static string All(List<NativeMessage> list) => JsonSerializer.Serialize(list.Select(m => new
    {
        m.Role, m.Effort,
        Content = m.Content.Select(b => new { b.Type, b.Text, b.Signature, b.Data, b.Provider, b.Id, b.Name, Input = b.Input?.GetRawText(), b.ToolUseId, b.Content, Img = b.Images?.Select(x => x.Base64) }),
    }));

    static void Translate(Action<string, string, bool, string> check)
    {
        var sources = new[] { AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini, AiProviderType.DeepSeek, AiProviderType.Ollama };
        var foreign = new Dictionary<AiProviderType, string[]>
        {
            [AiProviderType.Anthropic] = new[] { "SIG-ANTH", "THINK-ANTH", "REDACTED-ANTH" },
            [AiProviderType.OpenAI] = new[] { "ENC-OPENAI", "rs_1" },
            [AiProviderType.Gemini] = new[] { "GEM-SIG" },
            [AiProviderType.DeepSeek] = new[] { "DS-REASONING" },
            [AiProviderType.Ollama] = Array.Empty<string>(),
        };
        var targets = new (string Label, Func<AiProviderType, SwitchTarget> Make)[]
        {
            ("Anthropic opus (thinking, per-message effort)", f => To(f, AiProviderType.Anthropic, f == AiProviderType.Anthropic ? "claude-sonnet-5-5" : null, perMsg: true)),
            ("OpenAI gpt-5", f => To(f, AiProviderType.OpenAI, f == AiProviderType.OpenAI ? "gpt-5-mini" : null)),
            ("Gemini 3 Pro", f => To(f, AiProviderType.Gemini, f == AiProviderType.Gemini ? "gemini-3-flash" : null)),
            ("DeepSeek (compatible)", f => To(f, AiProviderType.DeepSeek, f == AiProviderType.DeepSeek ? "deepseek-chat" : null)),
            ("Ollama blind, small window", f => To(f, AiProviderType.Ollama, f == AiProviderType.Ollama ? "llama3" : null, sees: false, ctx: 8192)),
        };
        int pairs = 0;
        var bad = new List<string>();
        var leaks = new List<string>();
        var wire = new List<string>();
        foreach (var from in sources)
            foreach (var (label, make) in targets)
            {
                var src = Source(from);
                string srcBefore = All(src);
                var t = make(from);
                var outList = ProviderHistory.ForTarget(src, t, out var rep);
                pairs++;
                string key = $"{from}→{label}";
                foreach (var pr in Problems(outList, t)) bad.Add($"{key}: {pr}");
                if (All(src) != srcBefore) bad.Add($"{key}: the loop's own conversation was changed (must be a copy)");
                string json = All(outList);
                // A model never gets another model's signed or encrypted state.
                foreach (var f in foreign[from])
                    if (json.Contains(f)) leaks.Add($"{key}: {f}");
                if (!json.Contains("STEP1-OUTPUT") && rep.MessagesLeftOut == 0) bad.Add($"{key}: step 1's output is gone");
                if (!json.Contains("STEP2-OUTPUT")) bad.Add($"{key}: the last step's output is gone");
                // Effort marks: only Anthropic with per-message effort.
                bool hasEffort = outList.Any(m => m.Role == "system");
                if (hasEffort != (t.To == AiProviderType.Anthropic && t.ToPerMessageEffort)) bad.Add($"{key}: effort mark {(hasEffort ? "kept" : "dropped")}");
                // Images: kept for a model that sees, a note with the saved file for one that cannot.
                if (t.ToSeesImages && !json.Contains("SHOT-B64-DATA")) bad.Add($"{key}: the screenshot was dropped for a model that sees");
                if (!t.ToSeesImages && (json.Contains("SHOT-B64-DATA") || !json.Contains(@"C:\\shots\\001.png"))) bad.Add($"{key}: blind model: image not replaced by its note");
                // Anthropic (another model): the trailing call/result is text, the earlier step stays a real tool turn.
                if (t.To == AiProviderType.Anthropic)
                {
                    var last = outList.Last(m => m.Role != "system");
                    bool flat = !last.Content.Any(b => b.Type == "tool_result") && last.Content.Any(b => b.Text?.Contains("do not redo them") == true)
                                && last.Content.Any(b => b.Text?.Contains("[edit_file returned:]") == true && b.Text.Contains("STEP2-OUTPUT"));
                    bool earlier = outList.Any(m => m.Content.Any(b => b.Type == "tool_use" && b.Name == "read_file"));
                    bool lastImage = last.Content.Any(b => b.Images is { Count: > 0 });
                    if (!flat || !earlier || !lastImage || rep.ExchangesFlattened != 1) bad.Add($"{key}: trailing exchange flat={flat} earlier-kept={earlier} image-kept={lastImage}");
                }
                // The provider's own builder takes it.
                try
                {
                    if (t.To == AiProviderType.OpenAI)
                    {
                        string w = JsonSerializer.Serialize(OpenAiProvider.BuildResponsesInput(outList));
                        var calls = Regex.Matches(w, "\"type\":\"function_call\",\"call_id\":\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToHashSet();
                        var outs = Regex.Matches(w, "\"type\":\"function_call_output\",\"call_id\":\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToHashSet();
                        if (calls.Count == 0 || !calls.SetEquals(outs)) wire.Add($"{key}: Responses calls {string.Join(",", calls)} vs outputs {string.Join(",", outs)}");
                        if (from != AiProviderType.OpenAI && w.Contains("\"reasoning\"")) wire.Add($"{key}: a reasoning item reached OpenAI");
                    }
                    if (t.To == AiProviderType.Gemini)
                    {
                        string w = JsonSerializer.Serialize(GeminiProvider.BuildToolContents(outList));
                        if (w.Contains("GEM-SIG") || !w.Contains("functionResponse") || !w.Contains("functionCall")) wire.Add($"{key}: Gemini parts {Trunc(w, 160)}");
                    }
                }
                catch (Exception ex) { wire.Add($"{key}: builder threw {ex.GetType().Name}: {ex.Message}"); }
            }
        check("switch", $"ForTarget: {pairs} provider/model pairs (5 sources × 5 targets) — alternation, paired safe unique ids, results first, task kept, the loop's list untouched",
            bad.Count == 0, string.Join("; ", bad.Take(8)));
        check("switch", "ForTarget: no signed thinking, encrypted reasoning, Gemini thoughtSignature or compatible reasoning reaches another model",
            leaks.Count == 0, string.Join("; ", leaks.Take(8)));
        check("switch", "the translated conversation goes through OpenAI's Responses builder (every call has its output) and Gemini's (calls + responses, no foreign signature)",
            wire.Count == 0, string.Join("; ", wire.Take(6)));

        // Anthropic → Anthropic, other model: its own server-tool block may stay; the signed thinking may not.
        var aa = ProviderHistory.ForTarget(Source(AiProviderType.Anthropic), To(AiProviderType.Anthropic, AiProviderType.Anthropic, "claude-sonnet-5-5", perMsg: true), out var aaRep);
        string aaJson = All(aa);
        check("switch", "Anthropic → another Anthropic model: thinking and redacted_thinking dropped, its own server-tool block kept, the effort mark kept",
            !aaJson.Contains("SIG-ANTH") && !aaJson.Contains("REDACTED-ANTH") && aaJson.Contains("srvtoolu_1") && aa.Any(m => m.Role == "system" && m.Effort == "high")
            && aaRep.ThinkingDropped == 2, aaRep.ToString());

        // The same model: the list as it was (same blocks: the prompt cache stays valid).
        var same = Source(AiProviderType.Anthropic);
        var sameOut = ProviderHistory.ForTarget(same, To(AiProviderType.Anthropic, AiProviderType.Anthropic, "CLAUDE-OPUS-5-5 "), out var sameRep);
        check("switch", "the same model (case and spaces aside): the conversation comes back as it was — same blocks, thinking and signatures included",
            sameRep.SameModel && sameOut.Count == same.Count && sameOut.Zip(same).All(p => ReferenceEquals(p.First, p.Second)) && All(sameOut).Contains("SIG-ANTH"),
            sameRep.ToString());
    }

    static void Ids(Action<string, string, bool, string> check)
    {
        // Ollama numbers every reply's calls from call_0; one reply with two calls; an id with characters no API takes;
        // a call with no id at all (paired with the result that follows, in order).
        var list = new List<NativeMessage>
        {
            U(Txt(Task1)),
            A(Use("call_0", "read_file"), Use("call_1", "list_dir", "{}")),
            U(Res("call_0", "R0"), Res("call_1", "R1")),
            A(Use("call_0", "read_file", "{\"path\":\"b.cs\"}")),
            U(Res("call_0", "R2")),
            A(Use("weird id/with:chars", "grep", "{\"q\":\"x\"}")),
            U(Res("weird id/with:chars", "R3")),
            A(Use(null, "run_command", "{\"cmd\":\"dotnet build\"}"), Use("", "git_status", "{}")),
            U(Res(null, "R4"), Res("", "R5")),
        };
        var t = To(AiProviderType.Ollama, AiProviderType.OpenAI);
        var outList = ProviderHistory.ForTarget(list, t, out var rep);
        var problems = Problems(outList, t);
        // Each result still answers the call it answered: pair them by name through the new ids.
        var byId = outList.SelectMany(m => m.Content).Where(b => b.Type == "tool_use").ToDictionary(b => b.Id!, b => b.Name + " " + b.Input?.GetRawText());
        string Pair(string output) => outList.SelectMany(m => m.Content).Where(b => b.Type == "tool_result" && b.Content == output)
            .Select(b => byId.TryGetValue(b.ToolUseId ?? "", out var n) ? n : "?").FirstOrDefault() ?? "missing";
        bool paired = Pair("R0").StartsWith("read_file {\"path\":\"a.cs\"}") && Pair("R1").StartsWith("list_dir") && Pair("R2").StartsWith("read_file {\"path\":\"b.cs\"}")
                      && Pair("R3").StartsWith("grep") && Pair("R4").StartsWith("run_command") && Pair("R5").StartsWith("git_status");
        check("switch", "tool ids: Ollama's repeated call_0, an id with other characters and calls with no id all get unique safe ids; every result keeps its own call",
            problems.Count == 0 && paired && rep.IdsRemapped == 4 && rep.OrphansDropped == 0,
            $"remapped {rep.IdsRemapped} orphans {rep.OrphansDropped} · R0→{Pair("R0")} R2→{Pair("R2")} R4→{Pair("R4")} R5→{Pair("R5")} · {string.Join("; ", problems)}");

        // A call whose result was lost (a Stop between the two) and a result whose call was lost: both go, in pairs.
        var broken = new List<NativeMessage>
        {
            U(Txt(Task1)),
            A(Txt("first"), Use("toolu_a", "read_file")),
            U(Res("toolu_zzz", "stray result"), Txt("go on")),
            A(Use("toolu_b", "read_file")),
            U(Res("toolu_b", "RB")),
            A(Use("toolu_c", "read_file")),
        };
        var bt = To(AiProviderType.Anthropic, AiProviderType.Gemini);
        var bOut = ProviderHistory.ForTarget(broken, bt, out var bRep);
        string bJson = All(bOut);
        var bProblems = Problems(bOut, bt);
        check("switch", "orphans: a call without its result and a result without its call are removed (the rest — text, the answered pair — stays)",
            bProblems.Count == 0 && !bJson.Contains("toolu_a") && !bJson.Contains("stray result") && !bJson.Contains("toolu_c")
            && bJson.Contains("RB") && bJson.Contains("first") && bJson.Contains("go on") && bRep.OrphansDropped == 3,
            $"orphans {bRep.OrphansDropped} · {string.Join("; ", bProblems)}");

        // A conversation that starts with the assistant (a trimmed history) gets a user turn first.
        var startsA = new List<NativeMessage> { A(Txt("hi")), U(Txt(Task1)), A(Txt("ok")), U(Txt("more")), U(Txt("and more")) };
        var sOut = ProviderHistory.FixAlternation(startsA.Select(m => new NativeMessage { Role = m.Role, Content = m.Content.ToList() }).ToList());
        check("switch", "alternation: an assistant-first history gets a user turn in front; two user turns in a row are merged",
            sOut[0].Role == "user" && sOut.Count == 5 && sOut[1].Role == "assistant" && sOut[^1].Role == "user" && sOut[^1].Content.Count == 2, string.Join(",", sOut.Select(m => $"{m.Role}:{m.Content.Count}")));
    }

    static void Window(Action<string, string, bool, string> check)
    {
        // A short run where shortening old tool output is enough: nothing is left out.
        var shortRun = new List<NativeMessage> { U(Txt(Task1)) };
        for (int i = 0; i < 6; i++)
        {
            shortRun.Add(A(Use($"toolu_s{i}", "read_file", $"{{\"path\":\"s{i}.cs\"}}")));
            shortRun.Add(U(Res($"toolu_s{i}", $"SOUT-{i} " + new string('q', 9000))));
        }
        var st = new SwitchTarget(AiProviderType.Anthropic, "claude-opus-5-5", AiProviderType.Ollama, "qwen3", false, 8192, false, 1500);
        var sOut = ProviderHistory.ForTarget(shortRun, st, out var sRep);
        check("switch", "a short run to a small window: old tool output shortened first, every step still there, the last one whole",
            Problems(sOut, st).Count == 0 && sRep.TokensAfter <= sRep.Budget && sRep.MessagesLeftOut == 0 && sRep.ToolResultsTrimmed > 0
            && sOut.Count == shortRun.Count && All(sOut).Contains("SOUT-5 " + new string('q', 9000)), sRep.ToString());

        // A long run on a 1M-window model, going to an 8K model on this PC: it must fit, keep the task and the last step.
        var list = new List<NativeMessage> { U(Txt(Task1)) };
        for (int i = 0; i < 60; i++)
        {
            list.Add(A(Txt($"step {i}"), Use($"toolu_{i:D2}", "read_file", $"{{\"path\":\"f{i}.cs\"}}")));
            list.Add(U(Res($"toolu_{i:D2}", $"OUT-{i:D2} " + new string((char)('a' + i % 26), 9000))));
        }
        var t = new SwitchTarget(AiProviderType.Anthropic, "claude-opus-5-5", AiProviderType.Ollama, "qwen3", false, 8192, false, 1500);
        var outList = ProviderHistory.ForTarget(list, t, out var rep);
        var problems = Problems(outList, t);
        string json = All(outList);
        check("switch", "a long run moving to a small window: fits the budget (80% − system prompt and tools), keeps the task and the last step whole, says what was left out",
            problems.Count == 0 && rep.TokensAfter <= rep.Budget && rep.Budget == (int)(8192 * 0.8) - 1500 && json.Contains("OUT-59 " + new string((char)('a' + 59 % 26), 9000))
            && json.Contains("earlier message(s) of this task were left out") && rep.MessagesLeftOut > 0,
            $"{rep} · {string.Join("; ", problems)}");

        var bigWindow = ProviderHistory.ForTarget(list, t with { ToContextTokens = 1_000_000, ReserveTokens = 0 }, out var bigRep);
        check("switch", "control: the same run to a big window keeps every step whole (nothing trimmed, nothing left out)",
            bigRep.MessagesLeftOut == 0 && bigRep.ToolResultsTrimmed == 0 && bigWindow.Count == list.Count, bigRep.ToString());
        var sameBig = ProviderHistory.ForTarget(list, new SwitchTarget(AiProviderType.Anthropic, "claude-opus-5-5", AiProviderType.Anthropic, "claude-opus-5-5", true, 8192), out var sameRep);
        check("switch", "the same model with a window that got smaller (a setting changed): it is still fitted",
            sameRep.SameModel && sameRep.TokensAfter <= sameRep.Budget && sameRep.TokensBefore > sameRep.Budget
            && (sameRep.ToolResultsTrimmed > 0 || sameBig.Count < list.Count), sameRep.ToString());
    }

    static void Brief(Action<string, string, bool, string> check)
    {
        var src = Source(AiProviderType.Anthropic);
        string b = HandoffBrief.FromNative(src, "Claude Opus 5.5");
        check("switch", "handoff brief for a CLI agent: the task, every step with its arguments and output, says they are DONE; no signature or base64",
            b.Contains(Task1) && b.Contains("read_file {\"path\":\"a.cs\"} → ok: STEP1-OUTPUT") && b.Contains("edit_file") && b.Contains("STEP2-OUTPUT")
            && b.Contains("are DONE") && b.Contains("Claude Opus 5.5") && !b.Contains("SIG-ANTH") && !b.Contains("SHOT-B64-DATA"),
            Trunc(b.Replace("\n", " ⏎ "), 300));
        var longRun = new List<NativeMessage> { U(Txt(Task1)) };
        for (int i = 0; i < 60; i++)
        {
            longRun.Add(A(Use($"t{i}", "read_file", $"{{\"path\":\"f{i}.cs\"}}")));
            longRun.Add(U(Res($"t{i}", $"OUT-{i:D2} " + new string('x', 600))));
        }
        string lb = HandoffBrief.FromNative(longRun, "GPT-5", 6000);
        check("switch", "a long run's brief stays within its size: the newest steps kept, the oldest counted as left out, the task always there",
            lb.Length <= 6000 && lb.Contains(Task1) && lb.Contains("OUT-59") && !lb.Contains("OUT-00 ") && lb.Contains("earlier step(s) left out for length"),
            $"{lb.Length} chars");
    }

    static void Board(Action<string, string, bool, string> check)
    {
        var scope = new AgentRunScope("failover-board", null);
        bool before = ProviderSwitchBoard.CanHotSwap(scope) || ProviderSwitchBoard.Request(scope, new(AiProviderType.OpenAI, "gpt-5"));
        ProviderSwitchBoard.Register(scope);
        using var call = new CancellationTokenSource();
        ProviderSwitchBoard.BeginCall(scope, call);
        bool asked = ProviderSwitchBoard.Request(scope, new(AiProviderType.OpenAI, "gpt-5", "owner"));
        bool stopped = call.IsCancellationRequested && ProviderSwitchBoard.WasSwitch(scope, call);
        ProviderSwitchBoard.EndCall(scope, call);
        var taken = ProviderSwitchBoard.Take(scope);
        var again = ProviderSwitchBoard.Take(scope);
        check("switch", "switch board: a run registers; a switch stops only the reply in flight (WasSwitch), is taken once; nothing before Register",
            !before && asked && stopped && taken?.Type == AiProviderType.OpenAI && taken.Model == "gpt-5" && again == null,
            $"before={before} asked={asked} stopped={stopped} taken={taken} again={again}");

        // Asked between two steps: the next request is stopped as it starts (it would go to the old model).
        ProviderSwitchBoard.Request(scope, new(AiProviderType.Gemini, null));
        using var next = new CancellationTokenSource();
        ProviderSwitchBoard.BeginCall(scope, next);
        bool early = next.IsCancellationRequested && ProviderSwitchBoard.WasSwitch(scope, next);
        ProviderSwitchBoard.EndCall(scope, next);
        ProviderSwitchBoard.Take(scope);
        // A user Stop / timeout is not a switch.
        using var timeout = new CancellationTokenSource();
        ProviderSwitchBoard.BeginCall(scope, timeout);
        timeout.Cancel();
        bool notSwitch = !ProviderSwitchBoard.WasSwitch(scope, timeout);
        ProviderSwitchBoard.EndCall(scope, timeout);
        ProviderSwitchBoard.Unregister(scope);
        bool gone = !ProviderSwitchBoard.CanHotSwap(scope) && !ProviderSwitchBoard.Request(scope, new(AiProviderType.OpenAI, null));
        check("switch", "switch board: asked between steps → the next request stops as it starts; a timeout/Stop is not taken for a switch; Unregister ends it",
            early && notSwitch && gone, $"early={early} notSwitch={notSwitch} gone={gone}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The real loop (called from ImageWire.LoopAndReadFile, which owns the DI graph and its guards)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An Anthropic reply with a signed thinking block and one tool call.</summary>
    static string ThinkingToolUseSse(string thinking, string signature, string id, string name, string inputJson) =>
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":" + JsonSerializer.Serialize(thinking) + "}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":" + JsonSerializer.Serialize(signature) + "}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{}}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(inputJson) + "}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":5}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    static string AnthText(string text) =>
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <param name="turn">The app's turn (ExecuteAgenticAsync behind ImageWire's hook guard).</param>
    /// <param name="executed">Every tool result the loop ran (CodeAgentService.OnToolExecuted), in order.</param>
    public static async Task Loop(Action<string, string, bool, string> check, AiProviderManager providers, SettingsService settingsSvc,
        CodeAgentService agent, GoldenMock anth, WireMock ollama, string project,
        Func<List<CluadeX.Models.ChatMessage>, string, Task<AgentLoopResult>> turn, List<ToolResult> executed)
    {
        // Whatever happens below, the sections after this one must not find a "quota ran out" flag: it would send their
        // sub-agents to the real Claude Dev CLI on this machine.
        var quotaFlag = typeof(CodeAgentService).GetField("_quotaExhaustedUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var s = settingsSvc.Settings;
        var keepOrder = s.FailoverOrder;
        bool keepAuto = s.AutoFailoverOnQuota;
        var keepAnthResponder = anth.Responder;
        var keepOllamaResponder = ollama.Responder;
        // Only the two mocks: never a CLI agent of the owner's (Claude Dev, Codex…) nor any other key in the harness settings.
        s.FailoverOrder = new List<string> { "Ollama", "-ClaudeDev", "-OpenAIDev", "-GeminiDev", "-GrokDev", "-AntigravityDev" };
        s.AutoFailoverOnQuota = true;
        QuotaBook.Shared.ClearAll();
        string marker = "FAILOVER-NOTES-" + Guid.NewGuid().ToString("N")[..10];
        File.WriteAllText(Path.Combine(project, "notes.txt"), marker + "\n");
        ollama.Responder = (method, path, body) => path switch
        {
            "/api/tags" => (200, "application/json", "{\"models\":[{\"name\":\"blindmodel\"},{\"name\":\"visionmodel\"}]}"),
            "/api/show" => (200, "application/json", "{\"capabilities\":[\"completion\",\"vision\",\"tools\"]}"),
            "/api/chat" => (200, "application/json",
                "{\"message\":{\"role\":\"assistant\",\"content\":\"OLLAMA-CARRIED-ON\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":10,\"eval_count\":2}"),
            _ => (404, "application/json", "{\"error\":\"no route\"}"),
        };
        try
        {
            await providers.SwitchProviderAsync(AiProviderType.Anthropic);
            bool ollamaUp = providers.GetProvider(AiProviderType.Ollama) is { } op && (op.IsReady || await Init(op)) && op.SupportsNativeToolUse;
            check("failover-loop", "set-up: the run is on the mocked Anthropic; the mocked Ollama is up with native tools (the only fallback)",
                providers.ActiveProvider is AnthropicProvider && ollamaUp, $"{providers.ActiveProvider?.ProviderId} ollama={ollamaUp}");
            if (!ollamaUp) return;

            // ── 1. Out of credit mid-run: the same turn carries on on Ollama, from the same step ──
            int anthCalls = 0;
            anth.Responder = body =>
            {
                int n = Interlocked.Increment(ref anthCalls);
                return n == 1
                    ? (200, "text/event-stream", ThinkingToolUseSse("THINK-SECRET-ANTH", "SIG-ANTHROPIC-XYZ", "toolu_fo1", "read_file", "{\"path\":\"notes.txt\"}"))
                    : (400, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.\"}}");
            };
            int a0 = anth.Count, o0 = ollama.Count("/api/chat"), e0 = executed.Count;
            AgentRunScope.Enter(new AgentRunScope("failover-quota", project));
            AgentLoopResult r1;
            try { r1 = await turn(new List<CluadeX.Models.ChatMessage>(), "Read notes.txt and tell me what it says."); }
            finally { AgentRunScope.Enter(null); }
            var oBodies = ollama.Bodies("/api/chat").Skip(o0).ToList();
            string ob = oBodies.FirstOrDefault() ?? "";
            int reads; lock (executed) reads = executed.Skip(e0).Count(x => x.ToolName == "read_file");
            check("failover-loop", "Anthropic out of credit on step 2 → the SAME turn carries on on Ollama: one answer, read_file ran once (not redone)",
                r1.Success && r1.FinalResponse.Contains("OLLAMA-CARRIED-ON") && anth.Count - a0 == 2 && oBodies.Count == 1 && reads == 1,
                $"success={r1.Success} stop={r1.StopReason} final={Trunc(r1.FinalResponse, 80)} anth={anth.Count - a0} ollama={oBodies.Count} reads={reads}");
            check("failover-loop", "Ollama's request carries step 1's tool call and its result (the file's marker), and none of Anthropic's signed thinking",
                ob.Contains(marker) && ob.Contains("\"role\":\"tool\"") && ob.Contains("read_file") && !ob.Contains("SIG-ANTHROPIC-XYZ") && !ob.Contains("THINK-SECRET-ANTH"),
                Trunc(ob, 300));
            string a2 = anth.BodyAt(a0 + 1) ?? "";
            check("failover-loop", "control: Anthropic's own step 2 did carry its signed thinking back (it is dropped only for another model)",
                a2.Contains("SIG-ANTHROPIC-XYZ"), Trunc(a2, 120));
            check("failover-loop", "Anthropic is noted as out of quota: passed over for the next while",
                QuotaBook.Shared.KindOf(AiProviderType.Anthropic, DateTime.UtcNow) == ProviderErrorKind.QuotaExhausted, "");
            // The run went on in place: its sub-agents go with it (the scope), never to the owner's real Claude Dev login.
            var quotaField = typeof(CodeAgentService).GetField("_quotaExhaustedUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var stamp = quotaField?.GetValue(agent) as DateTime?;
            check("failover-loop", "an in-place failover does not flag \"quota ran out\" for sub-agents (that flag sends them to Claude Dev)",
                quotaField != null && (stamp ?? default) == default, quotaField == null ? "_quotaExhaustedUtc not found" : $"stamp={stamp:O}");

            // ── 2. The next turn starts on Ollama straight away (no request spent on a refusal) ──
            int a1 = anth.Count, o1 = ollama.Count("/api/chat");
            AgentRunScope.Enter(new AgentRunScope("failover-next", project));
            AgentLoopResult r2;
            try { r2 = await turn(new List<CluadeX.Models.ChatMessage>(), "Say hello."); }
            finally { AgentRunScope.Enter(null); }
            check("failover-loop", "next turn while Anthropic cools: it starts on Ollama, Anthropic gets no request; the owner's pick is unchanged",
                r2.FinalResponse.Contains("OLLAMA-CARRIED-ON") && anth.Count == a1 && ollama.Count("/api/chat") == o1 + 1
                && providers.ActiveProviderType == AiProviderType.Anthropic,
                $"final={Trunc(r2.FinalResponse, 60)} anth +{anth.Count - a1} ollama +{ollama.Count("/api/chat") - o1} picked={providers.ActiveProviderType}");

            // ── 3. It recovered (the cooldown is over): the owner's pick is used again by itself ──
            QuotaBook.Shared.ClearAll();
            anth.Responder = _ => (200, "text/event-stream", AnthText("ANTH-IS-BACK"));
            int a2c = anth.Count, o2 = ollama.Count("/api/chat");
            AgentRunScope.Enter(new AgentRunScope("failover-back", project));
            AgentLoopResult r3;
            try { r3 = await turn(new List<CluadeX.Models.ChatMessage>(), "Say hello again."); }
            finally { AgentRunScope.Enter(null); }
            check("failover-loop", "after the cooldown the next turn goes back to Anthropic by itself (Ollama not asked)",
                r3.FinalResponse.Contains("ANTH-IS-BACK") && anth.Count == a2c + 1 && ollama.Count("/api/chat") == o2,
                $"final={Trunc(r3.FinalResponse, 60)} anth +{anth.Count - a2c} ollama +{ollama.Count("/api/chat") - o2}");

            // ── 4. The owner switches model while a reply is in flight: that reply stops, the new model takes the same step ──
            AgentRunScope? live = null;
            int hot = 0;
            anth.Responder = body =>
            {
                int n = Interlocked.Increment(ref hot);
                if (n == 1) return (200, "text/event-stream", ThinkingToolUseSse("THINK-HOT", "SIG-HOT", "toolu_hot1", "read_file", "{\"path\":\"notes.txt\"}"));
                // Step 2 is being answered when the owner picks Ollama in the model box.
                ProviderSwitchBoard.Request(live, new ProviderSwitchBoard.PendingSwitch(AiProviderType.Ollama, "visionmodel", "owner"));
                Thread.Sleep(300);
                return (200, "text/event-stream", AnthText("ANTH-REPLY-THAT-WAS-STOPPED"));
            };
            int a3 = anth.Count, o3 = ollama.Count("/api/chat"), e3 = executed.Count;
            live = new AgentRunScope("failover-hot", project);
            AgentRunScope.Enter(live);
            AgentLoopResult r4;
            try { r4 = await turn(new List<CluadeX.Models.ChatMessage>(), "Read notes.txt and tell me what it says."); }
            finally { AgentRunScope.Enter(null); }
            string hb = ollama.Bodies("/api/chat").Skip(o3).FirstOrDefault() ?? "";
            int hotReads; lock (executed) hotReads = executed.Skip(e3).Count(x => x.ToolName == "read_file");
            check("failover-loop", "a switch by hand mid-run: the reply in flight stops, Ollama answers the same step with step 1's result; nothing redone, no error",
                r4.Success && r4.FinalResponse.Contains("OLLAMA-CARRIED-ON") && !r4.FinalResponse.Contains("ANTH-REPLY-THAT-WAS-STOPPED")
                && anth.Count - a3 == 2 && hotReads == 1 && hb.Contains(marker) && !hb.Contains("SIG-HOT"),
                $"success={r4.Success} stop={r4.StopReason} final={Trunc(r4.FinalResponse, 80)} anth={anth.Count - a3} reads={hotReads}");
            check("failover-loop", "a switch by hand is not a failure: Anthropic is not put on the cooldown list",
                !QuotaBook.Shared.IsCooling(AiProviderType.Anthropic, DateTime.UtcNow), "");
        }
        finally
        {
            s.FailoverOrder = keepOrder;
            s.AutoFailoverOnQuota = keepAuto;
            anth.Responder = keepAnthResponder;
            ollama.Responder = keepOllamaResponder;
            QuotaBook.Shared.ClearAll();
            AgentRunScope.Enter(null);
            quotaFlag?.SetValue(agent, default(DateTime));
        }
    }

    static async Task<bool> Init(IAiProvider p)
    {
        try { await p.InitializeAsync(); } catch { }
        return p.IsReady;
    }

    static string Trunc(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";
}
