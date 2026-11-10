// Very hard tasks to Claude Fable 5.2 (owner 2026-11-10): "ส่วนนี้ให้ fable คิด" / "โยนไปแชทใหม่ใช้ fable" / "use fable for
// this". Never an automatic switch: the owner's words or a yes on CluadeX's card, and the spend guards still decide.
// Checked here, offline (no request leaves the machine): reading the owner's words, the "very hard" verdict, the price
// estimate and the budget refusal, the focused brief (never the whole chat), the task budget on the wire, the gate,
// a chat's own model in chat-links.json, and new_chat's refusals.
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;

namespace ApiTest;

internal static class FableWire
{
    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · very hard tasks to Claude Fable 5.2");

        // ─── the owner's words ───
        check("fable-ask", "\"ส่วนนี้ให้ fable คิด\" = one part on a Fable sub-agent",
            DispatchPolicy.ParseFableAsk("ส่วนนี้ให้ fable คิด") == DispatchPolicy.FableRoute.SubAgent, "");
        check("fable-ask", "\"โยนไปแชทใหม่ใช้ fable\" = the task in a new chat",
            DispatchPolicy.ParseFableAsk("โยนไปแชทใหม่ใช้ fable") == DispatchPolicy.FableRoute.NewChat, "");
        check("fable-ask", "\"use fable for this\" = a sub-agent",
            DispatchPolicy.ParseFableAsk("use fable for this") == DispatchPolicy.FableRoute.SubAgent, "");
        check("fable-ask", "\"send it to Fable in a new chat\" = a new chat",
            DispatchPolicy.ParseFableAsk("send it to Fable in a new chat") == DispatchPolicy.FableRoute.NewChat, "");
        check("fable-ask", "Thai spelling \"ให้เฟเบิลคิด\" counts",
            DispatchPolicy.ParseFableAsk("ส่วนนี้ให้เฟเบิลคิด") == DispatchPolicy.FableRoute.SubAgent, "");
        check("fable-ask", "a question about Fable, the word alone, or another word asks nothing",
            DispatchPolicy.ParseFableAsk("what is fable?") == DispatchPolicy.FableRoute.None
            && DispatchPolicy.ParseFableAsk("Fable") == DispatchPolicy.FableRoute.None
            && DispatchPolicy.ParseFableAsk("use the fabled cache for this") == DispatchPolicy.FableRoute.None
            && DispatchPolicy.ParseFableAsk("") == DispatchPolicy.FableRoute.None, "");

        // ─── model ids ───
        check("fable-id", "\"fable\" resolves to claude-fable-5-2 for agent_spawn and new_chat",
            DispatchPolicy.FableIdOf("fable") == "claude-fable-5-2" && DispatchPolicy.FableIdOf("Fable 5.2") == "claude-fable-5-2"
            && DispatchPolicy.ResolveAgentModel(AiProviderType.Anthropic, "fable") == "claude-fable-5-2", "");
        check("fable-id", "a full Fable id passes through; Opus and empty are not Fable",
            DispatchPolicy.FableIdOf("claude-fable-5-1") == "claude-fable-5-1" && DispatchPolicy.FableIdOf("opus") == null
            && DispatchPolicy.FableIdOf("") == null && DispatchPolicy.IsFable("claude-mythos-5-2") && !DispatchPolicy.IsFable("claude-opus-5-5"), "");

        // ─── "very hard" ───
        var none = new DispatchPolicy.HardTaskSignals();
        check("fable-hard", "a plain run is not hard", !DispatchPolicy.AssessHardTask(none, "claude-opus-5-5").Hard, "");
        var specFailed = DispatchPolicy.AssessHardTask(none with { FailedOwnFixes = 1, SpeculativeFixFailed = true }, "claude-opus-5-5");
        check("fable-hard", "own fix left the build red AND the speculative fixers failed = hard (with a Thai reason)",
            specFailed.Hard && specFailed.WhyTh.Contains("build") && specFailed.Why.Length > 0, specFailed.Why);
        check("fable-hard", "one red fix alone is not; three in a row are",
            !DispatchPolicy.AssessHardTask(none with { FailedOwnFixes = 1 }, "claude-opus-5-5").Hard
            && DispatchPolicy.AssessHardTask(none with { FailedOwnFixes = DispatchPolicy.StuckOwnFixes }, "claude-opus-5-5").Hard, "");
        check("fable-hard", "the loop guard stopping the run = hard",
            DispatchPolicy.AssessHardTask(none with { LoopGuardStopped = true }, "claude-opus-5-5").Hard, "");
        check("fable-hard", "a run already on Fable is never offered Fable",
            !DispatchPolicy.AssessHardTask(none with { LoopGuardStopped = true }, "claude-fable-5-2").Hard, "");
        var (crit, layered, multi) = DispatchPolicy.OwnerHardWords("งานนี้ซับซ้อนหลายชั้น ข้าม repo และห้ามผิดพลาด");
        check("fable-hard", "the owner's Thai words: layered, multi-repo, must not go wrong", crit && layered && multi, $"{crit} {layered} {multi}");
        check("fable-hard", "owner: layered + must not go wrong = hard; two repos alone = not",
            DispatchPolicy.AssessHardTask(none with { OwnerSaysLayered = true, OwnerSaysCritical = true }, "claude-opus-5-5").Hard
            && !DispatchPolicy.AssessHardTask(none with { Repos = 2 }, "claude-opus-5-5").Hard
            && DispatchPolicy.AssessHardTask(none with { Repos = 2, OwnerSaysCritical = true }, "claude-opus-5-5").Hard, "");

        // ─── the price ───
        var est = DispatchPolicy.EstimateFable(3000, 15_000, DispatchPolicy.FableChatTaskBudget);
        check("fable-cost", "the estimate is a range: low < high, both in dollars, from brief + base tokens",
            est.LowUsd > 0 && est.HighUsd > est.LowUsd && est.StartTokens == 18_000 && est.TaskBudget == DispatchPolicy.FableChatTaskBudget,
            $"${est.LowUsd}–${est.HighUsd}");
        check("fable-cost", "a short run is under $2; the whole 160K task budget under $15",
            est.LowUsd < 2m && est.HighUsd < 15m, $"${est.LowUsd}–${est.HighUsd}");
        check("fable-cost", "a 170K-token fork costs ~$1.70 in cache writes alone",
            DispatchPolicy.ForkWriteUsd(170_000) == 1.70m, DispatchPolicy.ForkWriteUsd(170_000).ToString());
        check("fable-cost", "a task budget below the API's 20K minimum is raised to it",
            DispatchPolicy.EstimateFable(1000, 1000, 5000).TaskBudget == DispatchPolicy.TaskBudgetMinimum, "");
        string? refuse = DispatchPolicy.FableBudgetRefusal(est, runBudgetUsd: 0.50m, 0, 0, 0, 0, freshRun: true);
        check("fable-cost", "a per-run budget below the low estimate refuses, and says the price",
            refuse != null && refuse.Contains("$"), refuse ?? "(null)");
        check("fable-cost", "today's cap nearly spent refuses too",
            DispatchPolicy.FableBudgetRefusal(est, 0, 0, 0, dailyBudgetUsd: 10m, todaySpentUsd: 9.9m, freshRun: true) != null, "");
        check("fable-cost", "room for it = go; room for the low case only = go with a warning",
            DispatchPolicy.FableBudgetRefusal(est, 50m, 0, 0, 100m, 0, true) == null
            && DispatchPolicy.FableBudgetWarning(est, 50m, 0, 0, 100m, 0, true) == null
            && DispatchPolicy.FableBudgetRefusal(est, est.LowUsd + 0.01m, 0, 0, 0, 0, true) == null
            && DispatchPolicy.FableBudgetWarning(est, est.LowUsd + 0.01m, 0, 0, 0, 0, true) != null, "");
        check("fable-cost", "a sub-agent shares its run's spend: a spent run refuses it",
            DispatchPolicy.FableBudgetRefusal(est, 5m, 0, runSpentUsd: 4.9m, 0, 0, freshRun: false) != null
            && DispatchPolicy.FableBudgetRefusal(est, 5m, 0, runSpentUsd: 4.9m, 0, 0, freshRun: true) == null, "");

        // ─── the focused brief ───
        var history = new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = "Fix the deadlock in the sync engine." },
            new() { Role = MessageRole.ToolAction, ToolName = "edit_file", ToolInputSummary = "src/Sync.cs", ToolSuccess = true, Content = "ok" },
            new() { Role = MessageRole.ToolAction, ToolName = "run_command", ToolInputSummary = "dotnet test", ToolSuccess = false,
                    ToolOutput = "Passed 10\nerror: SyncTests.Deadlock timed out after 30s" },
            new() { Role = MessageRole.Assistant, Content = new string('y', 200_000) },
        };
        string brief = FableBrief.Compose("The sync engine deadlocks under load.", "src/Sync.cs, src/Queue.cs", null, null,
            history, @"D:\Code\App", new[] { @"D:\Code\Lib" }, "branch: fix-sync\n M src/Sync.cs\n");
        string report = DispatchPolicy.FableReportInstruction(newChat: true);
        check("fable-brief", "the brief has the problem, files and repos, what failed, and done-when",
            brief.Contains("deadlocks under load") && brief.Contains("src/Queue.cs") && brief.Contains(@"D:\Code\Lib")
            && brief.Contains("SyncTests.Deadlock timed out") && brief.Contains("### Done when"), brief[..Math.Min(brief.Length, 600)]);
        check("fable-brief", "a 200K-char chat stays a brief (≤ the bound), never the transcript",
            brief.Length <= FableBrief.DefaultMaxChars + 10 && !brief.Contains(new string('y', 5000)), brief.Length.ToString());
        check("fable-brief", "the report instruction is last, whole", brief.EndsWith(report), "");
        check("fable-brief", "the report asks for ≤ 300 words: cause, files, check, left open",
            report.Contains(DispatchPolicy.FableReportWords.ToString()) && report.Contains("cause"), report);
        string tiny = FableBrief.Compose(new string('p', 50_000), null, "tried X", "tests green", null, null, null, "", maxChars: 3000);
        check("fable-brief", "a huge problem is cut and the report line still survives",
            tiny.Length <= 3010 && tiny.EndsWith(report) && tiny.Contains("tried X"), tiny.Length.ToString());

        // ─── the task budget on the wire ───
        var req = new Dictionary<string, object> { ["model"] = "claude-fable-5-2" };
        AnthropicProvider.ApplyThinkingAndSampling(req, "claude-fable-5-2",
            new AppSettings { ExtendedThinkingEnabled = true, ReasoningEffort = "high", MaxTokens = 4096 });
        bool applied = AnthropicProvider.ApplyTaskBudget(req, "claude-fable-5-2", 160_000);
        var wire = JsonDocument.Parse(JsonSerializer.Serialize(req)).RootElement;
        check("fable-wire", "output_config.task_budget = {type:tokens, total:160000}, no remaining; effort kept beside it",
            applied && wire.GetProperty("output_config").GetProperty("task_budget").GetProperty("type").GetString() == "tokens"
            && wire.GetProperty("output_config").GetProperty("task_budget").GetProperty("total").GetInt32() == 160_000
            && !wire.GetProperty("output_config").GetProperty("task_budget").TryGetProperty("remaining", out _)
            && wire.GetProperty("output_config").GetProperty("effort").GetString() == "high", wire.ToString());
        check("fable-wire", "the beta header is task-budgets-2026-03-13", AnthropicProvider.TaskBudgetBeta == "task-budgets-2026-03-13", "");
        var opus = new Dictionary<string, object>();
        check("fable-wire", "no task budget on a non-Fable model, or with none set",
            !AnthropicProvider.ApplyTaskBudget(opus, "claude-opus-5-5", 160_000) && !opus.ContainsKey("output_config")
            && !AnthropicProvider.ApplyTaskBudget(new Dictionary<string, object>(), "claude-fable-5-2", null), "");
        var low = new Dictionary<string, object>();
        AnthropicProvider.ApplyTaskBudget(low, "claude-fable-5-2", 5000);
        check("fable-wire", "a budget below 20K goes out at the minimum",
            ((Dictionary<string, object>)((Dictionary<string, object>)low["output_config"])["task_budget"])["total"] is int t && t == 20_000, "");

        // ─── the flow's model and budget ───
        await Task.Run(() =>
        {
            SubAgentModel.UseChat("claude-fable-5-2");
            SubAgentModel.UseTaskBudget(1000);
            check("fable-flow", "a Fable chat's flow picks Fable over the configured model, with the budget at the minimum",
                SubAgentModel.Pick("claude-opus-5-5") == "claude-fable-5-2" && SubAgentModel.TaskBudget == DispatchPolicy.TaskBudgetMinimum, "");
            SubAgentModel.Use("claude-haiku-5-5");
            check("fable-flow", "a sub-agent routed elsewhere still wins over the chat's model",
                SubAgentModel.Pick("claude-opus-5-5") == "claude-haiku-5-5", "");
        });
        check("fable-flow", "another flow never sees it", SubAgentModel.Pick("claude-opus-5-5") == "claude-opus-5-5" && SubAgentModel.TaskBudget == null, "");

        // ─── the gate ───
        FableGate.Reset();
        check("fable-gate", "nothing said = not allowed", !FableGate.Allowed("c1"), "");
        FableGate.NoteOwnerMessage("c1", "ส่วนนี้ให้ fable คิด");
        check("fable-gate", "the owner's words allow it this turn", FableGate.Allowed("c1") && FableGate.Asked("c1") == DispatchPolicy.FableRoute.SubAgent, "");
        FableGate.NoteOwnerMessage("c1", "ok carry on");
        check("fable-gate", "the next turn's words replace them", !FableGate.Allowed("c1"), "");
        FableGate.Grant("c1");
        check("fable-gate", "a yes on the card allows it until the owner's next message", FableGate.Allowed("c1"), "");
        FableGate.NoteOwnerMessage("c1", "more", append: true);
        bool keptOnAppend = FableGate.Allowed("c1");
        FableGate.NoteOwnerMessage("c1", "next");
        check("fable-gate", "typed into the running turn keeps the yes; a new turn uses it up", keptOnAppend && !FableGate.Allowed("c1"), "");
        check("fable-gate", "other chats are not affected", !FableGate.Allowed("c2") && !FableGate.Allowed(null), "");

        // ─── a chat's own model, kept beside codex.db ───
        string dir = Path.Combine(Path.GetTempPath(), "cx-fable-links-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var st = new ChatLinkStore(dir);
            st.Link("child", "parent", "fix the deadlock", "claude-fable-5-2");
            st.Link("plain", "parent", "other");
            var again = new ChatLinkStore(dir);
            check("fable-link", "a split chat's model survives a restart; a plain split has none",
                again.Get("child")?.Model == "claude-fable-5-2" && again.Get("plain")?.Model == null, "");
            check("fable-link", "the owner's pick clears it", again.SetModel("child", null) && new ChatLinkStore(dir).Get("child")?.Model == null, "");
            check("fable-link", "a chat that was never split has no model to set", !again.SetModel("nope", "claude-fable-5-2"), "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // ─── new_chat with model ───
        var mod = new ProjectToolModule();
        var keep = ProjectToolModule.ModelChatHandler;
        var keepPlain = ProjectToolModule.NewChatHandler;
        ProjectToolModule.ModelChatRequest? sent = null;
        bool plainOpened = false;
        ProjectToolModule.ModelChatHandler = r => { sent = r; return Task.FromResult("opened"); };
        ProjectToolModule.NewChatHandler = (_, _, _, _) => { plainOpened = true; return Task.FromResult("plain"); };
        try
        {
            var schema = mod.GetToolSchemas().First(s => s.Name == "new_chat");
            string schemaJson = JsonSerializer.Serialize(schema);
            check("fable-newchat", "new_chat offers model / files / tried / done_when",
                schemaJson.Contains("\"model\"") && schemaJson.Contains("done_when") && schemaJson.Contains("tried"), "");
            ToolCall Call(params (string K, string V)[] args)
            {
                var c = new ToolCall { ToolName = "new_chat", Type = ToolType.NewChat };
                foreach (var (k, v) in args) c.Arguments[k] = v;
                return c;
            }
            var ctx = new ToolModuleContext();
            FableGate.Reset();
            var scope = new AgentRunScope("fable-chat", null);
            AgentRunScope.Enter(scope);
            var notAsked = await mod.ExecuteAsync(Call(("model", "fable"), ("message", "fix it")), ctx, CancellationToken.None);
            check("fable-newchat", "not asked by the owner = refused, nothing opened", !notAsked.Success && sent == null, notAsked.Output ?? "");
            FableGate.NoteOwnerMessage("fable-chat", "โยนไปแชทใหม่ใช้ fable");
            var forked = await mod.ExecuteAsync(Call(("model", "fable"), ("message", "fix it"), ("fork", "true")), ctx, CancellationToken.None);
            check("fable-newchat", "fork with Fable = refused, and it says why (the cache cost)",
                !forked.Success && sent == null && (forked.Output ?? "").Contains("cache"), forked.Output ?? "");
            var other = await mod.ExecuteAsync(Call(("model", "opus"), ("message", "fix it")), ctx, CancellationToken.None);
            check("fable-newchat", "a model other than Fable = refused", !other.Success && sent == null, other.Output ?? "");
            var ok = await mod.ExecuteAsync(Call(("model", "fable"), ("message", "fix the deadlock"), ("files", "src/Sync.cs"),
                ("tried", "a lock around Flush"), ("done_when", "SyncTests green")), ctx, CancellationToken.None);
            check("fable-newchat", "asked = the request goes to the chat page with the brief's parts",
                ok.Success && sent is { Model: "claude-fable-5-2", Problem: "fix the deadlock", Files: "src/Sync.cs", Tried: "a lock around Flush",
                    DoneWhen: "SyncTests green" } && !plainOpened, ok.Output ?? "");
            var plain = await mod.ExecuteAsync(Call(("message", "fix it"), ("model", "default")), ctx, CancellationToken.None);
            check("fable-newchat", "model default / left out = the plain new chat, as before", plain.Success && plainOpened, plain.Output ?? "");
        }
        finally
        {
            ProjectToolModule.ModelChatHandler = keep;
            ProjectToolModule.NewChatHandler = keepPlain;
            AgentRunScope.Enter(null);
            FableGate.Reset();
        }
    }
}
