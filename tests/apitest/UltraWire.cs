// Ultra must split, and Colibri must serve sub-agents (owner 2026-11-09: "ทำไม cluadeX ไม่แยกเอเจนย่อยแก้บัค ทั้งที่ติ๊ก
// Ultra ไว้" · "เอเจนเมื่อแยก ถ้ามีปลั๊กอิน colibri ต้องใช้ได้ด้วย ทุกโมเดลที่รองรับ"). A Codex chat with Ultra on, fixing
// 152–202 build errors, never split: Ultra never reached the dispatch decision (auto → medium = read-only only), Codex's
// prompt said every edit stays with it, and a workflow agent in a worktree threw on a CLI chat. Offline: the policy with
// Ultra, the Codex prompt and split guide, the catalog's model sizes, the KV slot count, the local slot limit and the
// busy-server retry. In the app's graph (Loop): a workflow agent in its own worktree on a Codex chat runs as
// `codex exec -C <worktree>` (a fake codex) and does not throw.
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Colibri;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class UltraWire
{
    static DispatchInput Base(string effort) => new()
    {
        Effort = effort, PolicyEnabled = true, ProviderCanRunSubAgents = true, MaxParallel = 3,
        SpeculativeFixEnabled = true, SpeculativeFixCount = 3, CanEditProject = true, IsGitRepo = true,
    };

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Ultra splits the work, Codex can split edits, Colibri serves sub-agents");
        Policy(check);
        Codex(check);
        Catalog(check);
        Slots(check);
        return Task.CompletedTask;
    }

    static void Policy(Action<string, string, bool, string> check)
    {
        foreach (var e in new[] { "low", "medium", "auto" })
        {
            var off = DispatchPolicy.Decide(Base(DispatchPolicy.ResolveEffort(e, null)));
            var on = DispatchPolicy.Decide(Base(DispatchPolicy.ResolveEffort(e, null)) with { Ultra = true });
            check("ultra", $"effort {e}: Ultra lifts the step to writing workers (off: {(off.Workers ? "workers" : "none")})",
                !off.Workers && on.SubAgents && on.Workers && !on.ReadOnlyOnly && on.Reason.StartsWith("Ultra"), on.Reason);
        }
        var max = DispatchPolicy.Decide(Base("max") with { Ultra = true });
        check("ultra", "Ultra never lowers a higher effort (max keeps speculative fixes on the first red build)",
            max.SpeculativeFix && max.Reason.StartsWith("effort max"), max.Reason);
        var noGit = DispatchPolicy.Decide(Base("medium") with { Ultra = true, IsGitRepo = false });
        check("ultra", "Ultra in a folder that is not a repo: read-only fan-out, and the reason says why",
            noGit.ReadOnlyOnly && !noGit.Workers && noGit.Reason.Contains("Ultra") && noGit.Reason.Contains("git"), noGit.Reason);
        var spent = DispatchPolicy.Decide(Base("medium") with { Ultra = true, RunBudgetUsd = 1m, RunSpentUsd = 1.2m });
        check("ultra", "Ultra does not lift a spent budget", !spent.SubAgents, spent.Reason);
        var near = DispatchPolicy.Decide(Base("medium") with { Ultra = true, RunBudgetUsd = 1m, RunSpentUsd = 0.9m });
        check("ultra", "…and near the budget it is read-only fan-out, cheap", near.ReadOnlyOnly && near.BudgetCapped, near.Reason);
    }

    static void Codex(Action<string, string, bool, string> check)
    {
        var ultra = DispatchPolicy.Decide(Base("auto") with { Effort = "medium", Ultra = true });
        string line = CliAgentPolicy.PromptLine(ultra, AiProviderType.OpenAIDev, workflow: true) ?? "";
        check("ultra-codex", "an Ultra bug-fix turn on Codex is told how to split edits: the workflow, each agent in its own worktree",
            line.Contains("workflow tool") && line.Contains("isolation \"worktree\"") && line.Contains("codex exec") && !line.Contains("every edit stays with you"), line);
        string noWf = CliAgentPolicy.PromptLine(ultra, AiProviderType.OpenAIDev, workflow: false) ?? "";
        check("ultra-codex", "without the workflow tool (no chat tools) the old read-only line stands", noWf.Contains("every edit stays with you"), noWf);
        check("ultra-codex", "Codex's read-only agent is its own \"explorer\", not Gemini's codebase_investigator",
            CliAgentPolicy.ReadOnlyType(AiProviderType.OpenAIDev) == "explorer" && !line.Contains("codebase_investigator")
            && CliAgentPolicy.IsReadOnly(AiProviderType.OpenAIDev, "explorer")
            && CliAgentPolicy.ReadOnlyType(AiProviderType.GeminiDev) == "codebase_investigator", line);
        string guide = WorkflowGate.SplitGuide(AiProviderType.OpenAIDev);
        check("ultra-codex", "Codex's split guide: group the errors by file, then a workflow with worktree isolation, a gate and merge",
            guide.Contains("group the errors by file") && guide.Contains("isolation \"worktree\"") && guide.Contains("gate")
            && guide.Contains("merge") && guide.Contains("workflow (MCP server cluadex)") && guide.Contains("spawn_agent")
            && !guide.Contains("every edit stays with you"), guide);
        check("ultra-codex", "a Codex chat's workflow agents run on Codex; the other CLIs' on Claude Dev",
            CodeAgentService.WorkflowCliFor(AiProviderType.OpenAIDev) == AiProviderType.OpenAIDev
            && CodeAgentService.WorkflowCliFor(AiProviderType.GeminiDev) == AiProviderType.ClaudeDev
            && CodeAgentService.WorkflowCliFor(AiProviderType.ClaudeDev) == AiProviderType.ClaudeDev, "");
        var args = OpenAIDevProvider.BuildArgs(@"C:\wt\agent-1", new ClaudeDevProvider.WorkerGrant(true, false, @"C:\wt\agent-1"), "", "medium", null);
        check("ultra-codex", "a writing workflow agent's codex exec: -C its worktree, workspace-write",
            args.SkipWhile(a => a != "-C").Skip(1).FirstOrDefault() == @"C:\wt\agent-1" && args.Contains("workspace-write"), string.Join(" ", args));
    }

    // colibri v2.1.0's catalog (setup_catalog.py), as `coli setup --list --json` gives it
    const string CatalogJson = """
    [
      {"id":"qwen36-35b","name":"Qwen3.6-35B-A3B","download_gb":23.1,"kind":"text","summary":"general chat with thinking and tools; int4-gs64 container","fits":true},
      {"id":"qwen3-coder-30b","name":"Qwen3-Coder-30B-A3B","download_gb":19.4,"kind":"text","summary":"coding model with tool calls, no thinking; int4-gs64 container","fits":true},
      {"id":"deepseek-v4-flash-reap","name":"DeepSeek V4 Flash REAP 150B","download_gb":84.7,"kind":"text","summary":"pruned DeepSeek V4 Flash (132 of 256 experts), official fp4/fp8 weights"},
      {"id":"deepseek-v4-flash","name":"DeepSeek V4 Flash","download_gb":166.9,"kind":"text","summary":"284B, official checkpoint, no conversion"},
      {"id":"qwen38-flash-next","name":"Qwen3.8-Flash-Next","download_gb":185.6,"kind":"text","summary":"125B + 51B n-gram, official FP8 checkpoint (pinned revision)"},
      {"id":"mimo-v2.6-flash","name":"MiMo-V2.6 Flash","download_gb":171.8,"kind":"text","summary":"309B with vision and tools, official checkpoint"},
      {"id":"glm-5.2","name":"GLM-5.2","download_gb":429.3,"kind":"text","summary":"744B, the reference model; int4-gs64 with the int8 MTP head"},
      {"id":"glm-5.3","name":"GLM-5.3","download_gb":419.3,"kind":"text","summary":"744B, int4-gs64, no MTP head (no speculative decoding)"},
      {"id":"mimo-v2.6-pro","name":"MiMo-V2.6 Pro","download_gb":563.6,"kind":"text","summary":"1.02T with vision and tools, official checkpoint"},
      {"id":"inkling","name":"Inkling","download_gb":514.1,"kind":"text","summary":"975B, int4 experts with bf16 dense weights"},
      {"id":"kimi-k3","name":"Kimi K3","download_gb":1561.0,"kind":"text","summary":"2.8T, original checkpoint with native MXFP4 experts"},
      {"id":"qwen-image-2.1","name":"Qwen-Image-2.1","download_gb":33.1,"kind":"image","summary":"text to image (pictures, not chat)"}
    ]
    """;

    static void Catalog(Action<string, string, bool, string> check)
    {
        var models = ColibriFacts.ParseModels(CatalogJson).ToDictionary(m => m.Id);
        var want = new Dictionary<string, double>
        {
            ["qwen36-35b"] = 35, ["qwen3-coder-30b"] = 30, ["deepseek-v4-flash-reap"] = 150, ["deepseek-v4-flash"] = 284,
            ["qwen38-flash-next"] = 125, ["mimo-v2.6-flash"] = 309, ["glm-5.2"] = 744, ["glm-5.3"] = 744,
            ["mimo-v2.6-pro"] = 1020, ["inkling"] = 975, ["kimi-k3"] = 2800,
        };
        var wrong = want.Where(w => models[w.Key].Billions != w.Value).Select(w => $"{w.Key}={models[w.Key].Billions}").ToList();
        check("colibri-size", "every catalog chat model has its size from the catalog (name, else the summary's stated size)",
            wrong.Count == 0, string.Join(", ", wrong));
        var nameOnly = want.Keys.Where(id => ModelCapability.ParseBillions(models[id].Name) == null).ToList();
        check("colibri-size", "…most of them say nothing in their name (what made them 'unknown' before)", nameOnly.Count >= 8, string.Join(",", nameOnly));
        var refused = want.Keys.Where(id =>
        {
            var b = models[id].Billions;
            return !(DispatchPolicy.UseLocalFor(true, SubAgentTier.Cheap, false, false, true, true, ModelCapability.ForBillions(b).Tier, b)
                     && DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, true, true, ModelCapability.ForBillions(b).Tier, b));
        }).ToList();
        check("colibri-size", "UseLocalFor takes every catalog chat model for cheap and mid read-only work by its catalog size",
            refused.Count == 0, string.Join(",", refused));
        var img = models["qwen-image-2.1"];
        check("colibri-size", "the image model is left out on purpose: not a chat kind", !img.IsChat && !ColibriFacts.IsChatKind("image")
            && ColibriFacts.IsChatKind("text") && ColibriFacts.IsChatKind("vision") && ColibriFacts.IsChatKind(""), img.Kind);
        check("colibri-size", "no stated size: a floor from the download (≥ N/2 billion for N GB)",
            ColibriFacts.CatalogBillions("Mystery", "a model", 40) == 20 && ColibriFacts.CatalogBillions("Mystery", "", 0) == null, "");

        // plugin.json + catalog.json → what a sub-agent reads
        string root = Path.Combine(Path.GetTempPath(), "cx-ultra-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string dir = Path.Combine(root, "plugins", "colibri");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "catalog.json"), CatalogJson);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"InstalledTag":"v2.1.0","ModelId":"glm-5.3","KvSlots":3}""");
            var f = ColibriService.ModelFacts(root);
            check("colibri-size", "a set-up model's facts come from the cached catalog (GLM-5.3 = 744B, chat, 3 slots)",
                f is { Billions: 744, Chat: true, Slots: 3 } && ColibriService.IsPluginInstalled(root), $"{f}");
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"InstalledTag":"v2.1.0","ModelId":"qwen-image-2.1"}""");
            var fi = ColibriService.ModelFacts(root);
            check("colibri-size", "the image model reads as not chat, and a server CluadeX did not start as 1 slot",
                fi is { Chat: false, Slots: 1 }, $"{fi}");
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"InstalledTag":"v2.1.0","ModelId":null}""");
            check("colibri-size", "no model set up → no facts (the owner's PC today)", ColibriService.ModelFacts(root) == null, "");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void Slots(Action<string, string, bool, string> check)
    {
        check("colibri-slots", "KV slots = the parallel-agent limit, bounded by RAM (32 GB → 4, 16 GB → 2, 64 GB → 8)",
            ColibriFacts.KvSlots(3, 31.9, 8) == 3 && ColibriFacts.KvSlots(5, 31.9, 8) == 4 && ColibriFacts.KvSlots(5, 16, 8) == 2
            && ColibriFacts.KvSlots(8, 64, 24) == 8 && ColibriFacts.KvSlots(0, 64, 24) == 1, "");
        check("colibri-slots", "a small card keeps it at 2; a picture model takes 1",
            ColibriFacts.KvSlots(5, 64, 4) == 2 && ColibriFacts.KvSlots(5, 64, 24, chat: false) == 1, "");
        var env = ColibriService.StartEnv(3);
        check("colibri-slots", "Colibri's start carries the slot count (COLI_KV_SLOTS, which `web --kv-slots` reads)",
            env.TryGetValue("COLI_KV_SLOTS", out var v) && v == "3" && ColibriService.StartEnv(40)["COLI_KV_SLOTS"] == "16", v ?? "");

        string key = "loopback:" + Random.Shared.Next(20000, 60000);
        var a = LocalSlots.TryTake(key, 2);
        var b = LocalSlots.TryTake(key, 2);
        var c = LocalSlots.TryTake(key, 2);
        check("colibri-slots", "a 2-slot server takes 2 sub-agents; the third goes to the API instead of the queue",
            a != null && b != null && c == null && LocalSlots.InUse(key) == 2, $"in use {LocalSlots.InUse(key)}");
        a!.Dispose(); a.Dispose();
        var d = LocalSlots.TryTake(key, 2);
        check("colibri-slots", "a finished one gives its place back (once, however often it is disposed)", d != null && LocalSlots.InUse(key) == 2, "");
        b!.Dispose(); d!.Dispose();
        check("colibri-slots", "…and all of them back leaves nothing held", LocalSlots.InUse(key) == 0, "");
        var u1 = LocalSlots.TryTake(key, 0); var u2 = LocalSlots.TryTake(key, 0);
        check("colibri-slots", "no known limit (Ollama, llama-server): never refused", u1 != null && u2 != null, "");
        u1!.Dispose(); u2!.Dispose();

        check("colibri-retry", "a busy server (429 queue_timeout) is asked again 3 times",
            Enumerable.Range(0, FailoverPolicy.MaxRetriesPerStep).All(r => CodeAgentService.SubAgentRetry(r, true, true) == CodeAgentService.SubAgentRetryAction.Retry), "");
        check("colibri-retry", "…then a local one hands the agent to the run's model; with none, or on the API, it fails",
            CodeAgentService.SubAgentRetry(3, true, true) == CodeAgentService.SubAgentRetryAction.ToRunModel
            && CodeAgentService.SubAgentRetry(3, true, false) == CodeAgentService.SubAgentRetryAction.Fail
            && CodeAgentService.SubAgentRetry(3, false, true) == CodeAgentService.SubAgentRetryAction.Fail, "");
        var q = ProviderErrors.Classify(new System.Net.Http.HttpRequestException("HTTP 429: the request waited 300 s in the queue",
            null, (System.Net.HttpStatusCode)429));
        check("colibri-retry", "colibri's 429 reads as a rate limit (waiting fixes it), not a quota", q == ProviderErrorKind.RateLimited, q.ToString());
    }

    // ─────────────── in the app's graph: a Codex chat's worktree agent ───────────────

    /// <summary>A workflow agent with its own worktree, on a chat whose active provider is Codex (a fake codex.cmd):
    /// it runs `codex exec -C &lt;worktree&gt;` there and returns its report — no throw, nothing written in the project.</summary>
    public static async Task Loop(Action<string, string, bool, string> check, AiProviderManager providers, SettingsService settings,
        CodeAgentService agent, string work)
    {
        string repo = Path.Combine(work, "ultra-codex-repo");
        string tree = Path.Combine(work, "ultra-codex-wt");
        string fake = Path.Combine(work, "fake-codex");
        var before = providers.ActiveProviderType;
        var cx = providers.GetProvider(AiProviderType.OpenAIDev);
        var exeField = typeof(OpenAIDevProvider).GetField("_exe", BindingFlags.NonPublic | BindingFlags.Instance);
        object? exeBefore = exeField?.GetValue(cx);
        bool localBefore = settings.Settings.SubAgentLocalCheap;
        try
        {
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
            await Git(repo, "init", "-q");
            await Git(repo, "add", ".");
            await Git(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
            await Git(repo, "worktree", "add", "-q", tree);
            Directory.CreateDirectory(fake);
            string cmd = Path.Combine(fake, "codex.cmd");
            File.WriteAllText(cmd, "@echo off\r\n"
                + "findstr \"^\" > nul\r\n"
                + "echo %* > \"%~dp0args.txt\"\r\n"
                + "echo ran> codex-was-here.txt\r\n"
                + "echo {\"type\":\"thread.started\"}\r\n"
                + "echo {\"type\":\"item.completed\",\"item\":{\"id\":\"m1\",\"type\":\"agent_message\",\"text\":\"fake codex report\"}}\r\n"
                + "echo {\"type\":\"turn.completed\",\"usage\":{}}\r\n");
            exeField?.SetValue(cx, cmd);
            settings.Settings.SubAgentLocalCheap = false;
            await providers.SwitchProviderAsync(AiProviderType.OpenAIDev);
            check("ultra-loop", "the chat runs on Codex (a fake codex.cmd)", providers.ActiveProviderType == AiProviderType.OpenAIDev, providers.ActiveProviderType.ToString());

            var run = typeof(CodeAgentService).GetMethod("RunWorkflowAgentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            AgentRunScope.Enter(new AgentRunScope("ultra-chat", repo));
            WorkflowAgentResult? result = null;
            Exception? thrown = null;
            try
            {
                var call = new WorkflowAgentCall("wf-1", "fix", "Fix", "Fixer #1", "fix the errors in a.txt", "general", 1, "ultra-chat", tree);
                result = await (Task<WorkflowAgentResult>)run.Invoke(agent, [call, CancellationToken.None])!;
            }
            catch (Exception ex) { thrown = ex; }
            finally { AgentRunScope.Enter(null); }
            check("ultra-loop", "RunWorkflowAgentAsync with a worktree on Codex does not throw, and returns codex's report",
                thrown == null && result?.Text == "fake codex report", thrown?.Message ?? result?.Text ?? "(none)");
            string args = File.Exists(Path.Combine(fake, "args.txt")) ? File.ReadAllText(Path.Combine(fake, "args.txt")) : "";
            check("ultra-loop", "…it ran as codex exec -C <the worktree>, workspace-write",
                args.Contains("exec") && args.Contains("-C " + tree) && args.Contains("workspace-write"), args.Trim());
            check("ultra-loop", "…in the worktree, never the project", File.Exists(Path.Combine(tree, "codex-was-here.txt"))
                && !File.Exists(Path.Combine(repo, "codex-was-here.txt")), "");
        }
        catch (Exception ex) { check("ultra-loop", "the Codex worktree section ran", false, ex.Message); }
        finally
        {
            settings.Settings.SubAgentLocalCheap = localBefore;
            await providers.SwitchProviderAsync(before);
            exeField?.SetValue(cx, exeBefore);
            try { await Git(repo, "worktree", "remove", "--force", tree); } catch { }
        }
    }

    static async Task<string> Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return o;
    }
}
