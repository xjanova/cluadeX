// Effort "auto" made smart (owner, 2026-10-13: "มันฉลาดในการเลือก คิด หรือไม่คิด หรือยัง"). Before: "auto" + Think on was
// "high" on every request (Opus 5.5's default is medium), every step of a run got the same effort, and Think off on
// Claude Sonnet 5.5 still thought (disabling is a 400 there; between_tools is its off switch).
// Now: the agent loop picks per step — low for a chat-sized ask, medium for real work, one or two levels up when the
// run shows it is hard, never down within a turn — and the providers read that pick only when the setting is "auto".
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class EffortWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · smart effort (\"auto\") and Think off on Sonnet 5.5");

        string E(bool nonTrivial, int red = 0, int falseFin = 0, int failSteps = 0, int failEdits = 0, string? cur = null)
            => CodeAgentService.SmartEffort(nonTrivial, red, falseFin, failSteps, failEdits, cur);
        check("effort", "a chat-sized ask starts low, real work starts medium", E(false) == "low" && E(true) == "medium", $"{E(false)}/{E(true)}");
        check("effort", "one sign of trouble (a red-build guard, two failing steps, two failed edits, a false finish) = one level up",
            E(true, red: 1) == "high" && E(true, failSteps: 2) == "high" && E(true, failEdits: 2) == "high" && E(true, falseFin: 1) == "high", "");
        check("effort", "it keeps going wrong (a second red build, four failing steps, four failed edits) = two levels up",
            E(true, red: 2) == "xhigh" && E(true, failSteps: 4) == "xhigh" && E(false, failEdits: 4) == "high", "");
        check("effort", "never down within a turn: a clean step after 'high' stays high", E(true, cur: "high") == "high", E(true, cur: "high"));
        check("effort", "never above xhigh", E(true, red: 5, failSteps: 9, failEdits: 9, cur: "xhigh") == "xhigh", "");

        static Dictionary<string, object> Req(string model, bool think, string effort)
        {
            var r = new Dictionary<string, object> { ["model"] = model, ["max_tokens"] = 4096 };
            AnthropicProvider.ApplyThinkingAndSampling(r, model, new AppSettings { ExtendedThinkingEnabled = think, ReasoningEffort = effort, MaxTokens = 4096 });
            return r;
        }
        static string? EffortOf(Dictionary<string, object> r) =>
            r.TryGetValue("output_config", out var oc) && oc is Dictionary<string, object> d && d.TryGetValue("effort", out var e) ? e as string : null;
        static string? ThinkingType(Dictionary<string, object> r) =>
            r.TryGetValue("thinking", out var t) && t is Dictionary<string, object> d && d.TryGetValue("type", out var ty) ? ty as string : null;

        var off = Req("claude-sonnet-5-5", think: false, effort: "max");
        var offThinking = (Dictionary<string, object>)off["thinking"];
        check("sonnet55", "Think off on Claude Sonnet 5.5 sends thinking {type: between_tools} and nothing else in it",
            ThinkingType(off) == "between_tools" && offThinking.Count == 1, string.Join(",", offThinking.Keys));
        check("sonnet55", "…with effort capped at high (between_tools is refused above it)", EffortOf(off) == "high", EffortOf(off) ?? "(omit)");
        var opus48Off = Req("claude-opus-4-8", think: false, effort: "medium");
        check("sonnet55", "negative control: Think off on Claude Opus 4.8 still sends {type: disabled}", ThinkingType(opus48Off) == "disabled", ThinkingType(opus48Off) ?? "(none)");
        var on = Req("claude-sonnet-5-5", think: true, effort: "medium");
        check("sonnet55", "Think on: adaptive as before", ThinkingType(on) == "adaptive" && EffortOf(on) == "medium", $"{ThinkingType(on)} {EffortOf(on)}");

        var autoChat = Req("claude-opus-5-5", think: true, effort: "auto");
        check("auto", "\"auto\" outside a run leaves effort to the model (it was forced to high whenever Think was on)",
            EffortOf(autoChat) == null && ThinkingType(autoChat) == "adaptive", EffortOf(autoChat) ?? "(omit)");
        var scope = new AgentRunScope("effort-test", null);
        AgentRunScope.Enter(scope);
        try
        {
            scope.StepEffort = "high";
            var inRun = Req("claude-opus-5-5", think: true, effort: "auto");
            check("auto", "\"auto\" inside a run sends the loop's pick for the step", EffortOf(inRun) == "high", EffortOf(inRun) ?? "(omit)");
            var explicitPick = Req("claude-opus-5-5", think: true, effort: "medium");
            check("auto", "an effort the user chose always wins over the loop's pick", EffortOf(explicitPick) == "medium", EffortOf(explicitPick) ?? "(omit)");
            check("auto", "OpenAI and Gemini read the same pick through EffortLevels.Effective",
                EffortLevels.Effective("auto") == "high" && EffortLevels.Effective("low") == "low", EffortLevels.Effective("auto"));
        }
        finally { AgentRunScope.Enter(null); }
        check("auto", "…and outside the run \"auto\" is \"auto\" again", EffortLevels.Effective("auto") == "auto", EffortLevels.Effective("auto"));
        return Task.CompletedTask;
    }
}
