// Effort "auto" made smart (owner, 2026-10-13: "มันฉลาดในการเลือก คิด หรือไม่คิด หรือยัง"). Before: "auto" + Think on was
// "high" on every request (Opus 5.5's default is medium), every step of a run got the same effort, and Think off on
// Claude Sonnet 5.5 still thought (disabling is a 400 there; between_tools is its off switch).
// Now: the agent loop picks per step — low for a chat-sized ask, medium for real work, one or two levels up when the
// run shows it is hard, never down within a turn — and the providers read that pick only when the setting is "auto".
using System.IO;
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

        // Per-message effort (Opus 5+, Sonnet 5.5+, Haiku 5.5+, Fable 5.1): the level moves both ways, no cache cost.
        string S(bool nt, bool big = false, int it = 3, bool wrap = false, bool red = false, int reds = 0, int fails = 0, bool ff = false)
            => CodeAgentService.StepEffortFor(nt, big, it, wrap, red, reds, fails, ff);
        check("step-effort", "a long brief plans at high, then real work runs at medium", S(true, big: true, it: 0) == "high" && S(true, big: true, it: 1) == "medium",
            $"{S(true, big: true, it: 0)}/{S(true, big: true, it: 1)}");
        check("step-effort", "a red build climbs; green again drops back (no ratchet)", S(true, red: true, reds: 1) == "high" && S(true, red: false, reds: 1) == "medium",
            $"{S(true, red: true, reds: 1)}/{S(true, red: false, reds: 1)}");
        check("step-effort", "still red after two guards, or four failing steps = xhigh", S(true, red: true, reds: 2) == "xhigh" && S(true, fails: 4) == "xhigh", "");
        check("step-effort", "the wrap-up summary runs at low, even after trouble", S(true, wrap: true, red: true, reds: 3) == "low", S(true, wrap: true, red: true, reds: 3));
        check("step-effort", "a chat-sized ask stays low", S(false) == "low" && S(false, it: 0) == "low", S(false));
        check("step-effort", "which models take effort-only messages",
            AnthropicProvider.Traits("claude-opus-5-5").PerMessageEffort && AnthropicProvider.Traits("claude-opus-5").PerMessageEffort
            && AnthropicProvider.Traits("claude-sonnet-5-5").PerMessageEffort && AnthropicProvider.Traits("claude-haiku-5-5").PerMessageEffort
            && AnthropicProvider.Traits("claude-fable-5-1").PerMessageEffort && !AnthropicProvider.Traits("claude-fable-5").PerMessageEffort
            && !AnthropicProvider.Traits("claude-sonnet-5").PerMessageEffort && !AnthropicProvider.Traits("claude-opus-4-8").PerMessageEffort, "");
        check("step-effort", "Sonnet 5.5 with Think off (between_tools) does not take them",
            !AnthropicProvider.PerMessageEffortOk("claude-sonnet-5-5", new AppSettings { ExtendedThinkingEnabled = false })
            && AnthropicProvider.PerMessageEffortOk("claude-sonnet-5-5", new AppSettings { ExtendedThinkingEnabled = true }), "");

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

        AutoPolicy(check, section);
        LiveLabel(check, section);
        ClaudeDevEffort(check, section);
        return Task.CompletedTask;
    }

    // Owner 2026-10-19: "effort auto มันฉลาดปรับค่าเองหรือยังทุกค่าย". Where auto STARTS is read from the task itself.
    static void AutoPolicy(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · effort \"auto\" reads the task (AutoEffortPolicy)");
        AutoEffortPolicy.Decision P(string m, int images = 0, int ctx = 0) => AutoEffortPolicy.Pick(new AutoEffortPolicy.Input(m, images, ctx));
        string D(AutoEffortPolicy.Decision d) => $"{d.Effort}/{d.ReasonKey}";

        var hi = P("สวัสดีค่ะ");
        check("auto-pick", "a greeting is low (short chat)", hi.Effort == "low" && hi.ReasonKey == "chat", D(hi));
        var why = P("ทำไม build ช้าจัง");
        check("auto-pick", "\"ทำไม\" (why) is a question, not \"ทำ\" (do): stays low", why.Effort == "low", D(why));
        var q = P("What does AgentRunScope do?");
        check("auto-pick", "a short question is low", q.Effort == "low" && q.ReasonKey == "question", D(q));
        var task = P("เพิ่มปุ่ม Export ในหน้า Settings");
        check("auto-pick", "a one-place change is medium (regular work)", task.Effort == "medium" && task.ReasonKey == "task", D(task));
        var bug = P("กดปุ่ม Save แล้วแอปแครช ช่วยแก้ที");
        check("auto-pick", "a crash to fix is high (bug)", bug.Effort == "high" && bug.ReasonKey == "bug", D(bug));
        var trace = P("fix this:\nSystem.NullReferenceException: Object reference not set\n   at CluadeX.ViewModels.ChatViewModel.Send()");
        check("auto-pick", "a pasted stack trace is a bug (high)", trace.Effort == "high" && trace.ReasonKey == "bug", D(trace));
        var multi = P("rename Foo to Bar in ChatViewModel.cs, ChatView.xaml and SettingsService.cs");
        check("auto-pick", "a change across several named files is high", multi.Effort == "high" && multi.ReasonKey == "multiFile", D(multi));
        var plan = P("วางแผน refactor ระบบ provider ทั้งระบบให้รองรับค่ายใหม่");
        check("auto-pick", "planning / architecture is high", plan.Effort == "high" && plan.ReasonKey == "plan", D(plan));
        string brief = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"{i}. implement part {i} of the feature with tests"));
        var spec = P(brief);
        check("auto-pick", "a five-plus-item brief is high and plans first", spec.Effort == "high" && spec.PlanFirst, D(spec) + " planFirst=" + spec.PlanFirst);
        string huge = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"{i}. fix the crash in module {i} — error when saving, the bug shows in the log")) + new string('x', 4000);
        var big = P(huge);
        check("auto-pick", "a big brief full of bugs is xhigh (never max on its own)", big.Effort == "xhigh" && big.ReasonKey == "bigTask", D(big));
        var retry = P("ยังไม่หาย ปุ่มยังไม่ทำงานเหมือนเดิม แก้ใหม่");
        check("auto-pick", "\"still broken\" after a try goes one level above the bug", retry.Effort == "xhigh" && retry.ReasonKey == "retry", D(retry));
        var img = P("ดูรูปนี้", images: 1);
        check("auto-pick", "a picture to read is at least medium", img.Effort == "medium" && img.ReasonKey == "image", D(img));
        var ctx = P("ok", ctx: 150_000);
        check("auto-pick", "a long conversation is at least medium", ctx.Effort == "medium" && ctx.ReasonKey == "bigContext", D(ctx));

        // The loop climbs from the task's own start, and says why.
        check("auto-climb", "from a bug's high: a red build climbs to xhigh, green drops back to high",
            CodeAgentService.StepEffortFor(2, false, 3, false, true, 1, 0, false) == "xhigh"
            && CodeAgentService.StepEffortFor(2, false, 3, false, false, 1, 0, false) == "high", "");
        check("auto-climb", "a planning first step goes one up (at least high); the wrap-up is low",
            CodeAgentService.StepEffortFor(1, true, 0, false, false, 0, 0, false) == "high"
            && CodeAgentService.StepEffortFor(3, false, 2, true, true, 3, 5, true) == "low", "");
        check("auto-climb", "old callers keep their meaning (bool overload = level 0 / 1)",
            CodeAgentService.StepEffortFor(true, true, 0, false, false, 0, 0, false) == CodeAgentService.StepEffortFor(1, true, 0, false, false, 0, 0, false)
            && CodeAgentService.SmartEffort(false, 0, 0, 0, 4, null) == CodeAgentService.SmartEffort(0, 0, 0, 0, 4, null), "");
        check("auto-why", "trouble in the run beats the task's own reason",
            CodeAgentService.EffortWhyFor("bug", false, false, true, 0, false, 0) == "buildRed"
            && CodeAgentService.EffortWhyFor("bug", false, true, true, 0, false, 0) == "wrapUp"
            && CodeAgentService.EffortWhyFor("task", false, false, false, 3, false, 0) == "failing"
            && CodeAgentService.EffortWhyFor("plan", true, false, false, 0, false, 0) == "planning"
            && CodeAgentService.EffortWhyFor("bug", false, false, false, 0, false, 0) == "bug", "");
    }

    // Owner 2026-10-19: "ส่วนที่ชี้ว่าใช้โมเดลอะไร effort อะไรต้องแสดงตามจริงที่กำลังใช้ เช่น opus 5.5 effort max".
    static void LiveLabel(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · the chat's model / effort label shows what really runs (LiveModelReport)");
        string T(string k) => k switch
        {
            "live.effort.none" => "no effort", "live.effort.modelDefault" => "model default", "live.effort.rounded" => "{0} (picked {1})", _ => k,
        };
        check("friendly", "model ids as people say them",
            LiveModelReport.FriendlyModel("claude-opus-5-5") == "Opus 5.5" && LiveModelReport.FriendlyModel("claude-sonnet-5-5-20261001") == "Sonnet 5.5"
            && LiveModelReport.FriendlyModel("claude-fable-5-1") == "Fable 5.1" && LiveModelReport.FriendlyModel("claude-opus-5") == "Opus 5"
            && LiveModelReport.FriendlyModel("claude-3-5-sonnet-20241022") == "Sonnet 3.5" && LiveModelReport.FriendlyModel("gpt-5.5") == "GPT-5.5"
            && LiveModelReport.FriendlyModel("gemini-3-pro-preview") == "Gemini 3 Pro Preview" && LiveModelReport.FriendlyModel("Qwen3-8B-Q4_K_M.gguf") == "Qwen3-8B-Q4_K_M.gguf",
            LiveModelReport.FriendlyModel("claude-sonnet-5-5-20261001") + " | " + LiveModelReport.FriendlyModel("claude-3-5-sonnet-20241022"));

        LiveModelReport.Reset();
        var scope = new AgentRunScope("live-test", null);
        AgentRunScope.Enter(scope);
        try
        {
            scope.EffortWhy = "bug fix";
            LiveModelReport.Sent(AiProviderType.Anthropic, "claude-opus-5-5", "high", true);
            LiveModelReport.Served(AiProviderType.Anthropic, "claude-opus-5-5-20261001");
            var snap = LiveModelReport.For(scope, AiProviderType.Anthropic);
            check("live", "a request's report lands on its run: served model + the effort on the wire + why",
                snap is { Model: "claude-opus-5-5-20261001", Effort: "high", Why: "bug fix" } && ReferenceEquals(scope.LiveReport, snap), snap?.ToString() ?? "null");
            check("live", "\"auto\" shows the level it really went out at and why: Opus 5.5 · auto → high (bug fix)",
                LiveModelReport.Describe("auto", snap, null, T) == "Opus 5.5 · auto → high (bug fix)", LiveModelReport.Describe("auto", snap, null, T));
            check("live", "an explicit pick that went out as is: Opus 5.5 · high", LiveModelReport.Describe("high", snap, null, T) == "Opus 5.5 · high",
                LiveModelReport.Describe("high", snap, null, T));
            LiveModelReport.Sent(AiProviderType.Anthropic, "claude-sonnet-4-5", null, false);
            check("live", "a model without effort says so (not the picker's value)",
                LiveModelReport.Describe("max", LiveModelReport.For(scope, AiProviderType.Anthropic), null, T) == "Sonnet 4.5 · no effort",
                LiveModelReport.Describe("max", LiveModelReport.For(scope, AiProviderType.Anthropic), null, T));
            LiveModelReport.Sent(AiProviderType.Anthropic, "claude-opus-5-5", null, true);
            check("live", "nothing sent under auto = the model's own default, said as such",
                LiveModelReport.Describe("auto", LiveModelReport.For(scope, AiProviderType.Anthropic), null, T).StartsWith("Opus 5.5 · auto → model default"),
                LiveModelReport.Describe("auto", LiveModelReport.For(scope, AiProviderType.Anthropic), null, T));
            LiveModelReport.Sent(AiProviderType.ClaudeDev, "", "high", true);
            LiveModelReport.Served(AiProviderType.ClaudeDev, "claude-opus-5-5");
            LiveModelReport.Reported(AiProviderType.ClaudeDev, "max");
            var cd = LiveModelReport.For(scope, AiProviderType.ClaudeDev);
            check("live", "Claude Dev: the CLI's own word wins over what was sent — Opus 5.5 · max (picked high), confirmed",
                cd is { Effort: "max", EffortConfirmed: true } && LiveModelReport.Describe("high", cd, "Claude Code default", T) == "Opus 5.5 · max (picked high)",
                LiveModelReport.Describe("high", cd, "Claude Code default", T));
        }
        finally { AgentRunScope.Enter(null); }
        check("live", "outside the run the provider's latest still answers (the label between turns)",
            LiveModelReport.For(null, AiProviderType.ClaudeDev)?.Effort == "max", LiveModelReport.For(null, AiProviderType.ClaudeDev)?.Effort ?? "null");
        check("live", "before any request: the setting and the set model", LiveModelReport.Describe("medium", null, "gpt-5.5", T) == "GPT-5.5 · medium",
            LiveModelReport.Describe("medium", null, "gpt-5.5", T));
        LiveModelReport.Forget(AiProviderType.ClaudeDev);
        check("live", "changing the effort pick forgets the old request's level", LiveModelReport.For(null, AiProviderType.ClaudeDev) == null, "");

        // Anthropic: the effort in force on the wire — the last effort-only system message beats the top-level field.
        var r = new Dictionary<string, object> { ["model"] = "claude-opus-5-5", ["max_tokens"] = 4096 };
        AnthropicProvider.ApplyThinkingAndSampling(r, "claude-opus-5-5", new AppSettings { ExtendedThinkingEnabled = true, ReasoningEffort = "max", MaxTokens = 4096 });
        check("live", "Anthropic Opus 5.5 takes max on the wire (what the label then shows)",
            r.TryGetValue("output_config", out var oc) && oc is Dictionary<string, object> d && d["effort"] as string == "max", "");
    }

    // Claude Dev: auto picks and sends --effort; the CLI's hooks say what it really ran at (probe 2026-10-19: Stop and
    // PreToolUse hooks carry "effort":{"level":"medium"}; UserPromptSubmit carries none; the init line names no level).
    static void ClaudeDevEffort(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Claude Dev effort: picked by auto, sent as --effort, confirmed by the CLI's hook");
        var (e1, w1) = ClaudeDevProvider.ResolveEffort("auto", "กดปุ่ม Save แล้วแอปแครช ช่วยแก้ที", 0);
        var (e2, w2) = ClaudeDevProvider.ResolveEffort("max", "hi", 0);
        check("cd-effort", "auto on Claude Dev picks from the task (bug → high) instead of sending nothing", e1 == "high" && w1 == "bug", $"{e1}/{w1}");
        check("cd-effort", "an explicit pick goes out as it is, no reason", e2 == "max" && w2 == null, $"{e2}/{w2}");
        check("cd-effort", "xhigh/max go out as they are only for a CLI that takes them (2.1.200+)",
            ClaudeDevProvider.SentEffort("max", ClaudeDevProvider.CliTakesAllLevels(ClaudeDevProvider.ParseCliVersion("2.1.296 (Claude Code)"))) == "max"
            && ClaudeDevProvider.SentEffort("max", ClaudeDevProvider.CliTakesAllLevels(ClaudeDevProvider.ParseCliVersion("2.0.14"))) == "high"
            && ClaudeDevProvider.SentEffort("xhigh", ClaudeDevProvider.CliTakesAllLevels(null)) == "high", "");
        string args = ClaudeDevProvider.BuildArgs(@"C:\work\demo", null, "", "max", allLevels: true);
        check("cd-effort", "a plain chat's args: --effort max, and the report-only settings (the Stop hook)",
            args.Contains("--effort max") && args.Contains("claude-dev-report-"), args.Length > 200 ? args[^200..] : args);

        string json = CluadeX.Services.Safety.GuardHook.SettingsJson("C:/x/CluadeX.exe", guard: false);
        check("cd-effort", "report-only settings: a Stop hook running --cx-effort, no PreToolUse guard",
            json.Contains("\"Stop\"") && json.Contains("--cx-effort") && !json.Contains("PreToolUse"), json);
        string guardJson = CluadeX.Services.Safety.GuardHook.SettingsJson("C:/x/CluadeX.exe", guard: true);
        check("cd-effort", "worker settings keep the guard and add the Stop report",
            guardJson.Contains("PreToolUse") && guardJson.Contains("--cx-guard") && guardJson.Contains("--cx-effort"), "");

        const string stopInput = "{\"session_id\":\"s\",\"permission_mode\":\"default\",\"effort\":{\"level\":\"medium\"},\"hook_event_name\":\"Stop\",\"stop_hook_active\":false}";
        check("cd-hook", "the hook input's effort.level is read", CluadeX.Services.Safety.GuardHook.EffortLevelOf(stopInput) == "medium", "");
        check("cd-hook", "an input with no effort (UserPromptSubmit) reports nothing",
            CluadeX.Services.Safety.GuardHook.EffortLevelOf("{\"hook_event_name\":\"UserPromptSubmit\",\"prompt\":\"hi\"}") == null, "");
        check("cd-hook", "junk in the level is refused", CluadeX.Services.Safety.GuardHook.EffortLevelOf("{\"effort\":{\"level\":\"max; rm\"}}") == null, "");

        string dir = Path.Combine(Path.GetTempPath(), "cx-effort-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string transcript = Path.Combine(dir, "t.jsonl");
            File.WriteAllText(transcript,
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n" +
                "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5-5\",\"role\":\"assistant\",\"content\":[]}}\n");
            string input = stopInput.Replace("\"session_id\":\"s\"", "\"session_id\":\"s\",\"transcript_path\":" + System.Text.Json.JsonSerializer.Serialize(transcript));
            check("cd-hook", "a plain (text-mode) chat learns its model from the transcript the hook names",
                CluadeX.Services.Safety.GuardHook.EffortReportOf(input) == "medium claude-opus-5-5", CluadeX.Services.Safety.GuardHook.EffortReportOf(input) ?? "null");
            string report = Path.Combine(dir, "effort.txt");
            Environment.SetEnvironmentVariable(CluadeX.Services.Safety.GuardHook.EffortReportEnv, report);
            try { CluadeX.Services.Safety.GuardHook.ReportEffort(input); }
            finally { Environment.SetEnvironmentVariable(CluadeX.Services.Safety.GuardHook.EffortReportEnv, null); }
            var (level, model) = CluadeX.Services.Safety.GuardHook.ReadEffortReport(report);
            check("cd-hook", "the report file round-trips (level + model)", level == "medium" && model == "claude-opus-5-5", $"{level} {model}");

            // The real exe in hook mode: stdin in, report out, exit 0 (a Stop hook must never block).
            string exe = CluadeX.Services.Safety.GuardHook.ExePath();
            // The harness copies only the DLL: find the built exe in the repo (CluadeX\bin\DebugBuild\net8.0-windows).
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && !exe.EndsWith("CluadeX.exe", StringComparison.OrdinalIgnoreCase); d = d.Parent)
            {
                string c = Path.Combine(d.FullName, "CluadeX", "bin", "DebugBuild", "net8.0-windows", "CluadeX.exe");
                if (File.Exists(c)) exe = c;
            }
            if (File.Exists(exe) && exe.EndsWith("CluadeX.exe", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(report);
                var psi = new System.Diagnostics.ProcessStartInfo(exe, CluadeX.Services.Safety.GuardHook.EffortFlag)
                { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
                psi.Environment[CluadeX.Services.Safety.GuardHook.EffortReportEnv] = report;
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardInput.Write(input);
                p.StandardInput.Close();
                bool exited = p.WaitForExit(20000);
                var (l2, m2) = CluadeX.Services.Safety.GuardHook.ReadEffortReport(report);
                check("cd-hook", "CluadeX.exe --cx-effort (the real Stop hook) writes the report and exits 0",
                    exited && p.ExitCode == 0 && l2 == "medium" && m2 == "claude-opus-5-5", $"exited={exited} code={(exited ? p.ExitCode : -1)} {l2} {m2}");
            }
            else check("cd-hook", "CluadeX.exe next to the DLL (skipped: not found)", true, exe);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
