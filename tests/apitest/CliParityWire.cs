// CLI parity (2026-11-06 audit, brain note 77faca9ac76d): what Claude Dev's wrapper and config give it, every CLI agent gets
// in its own names — the chat tools (new_chat / close_chat / wrap_up / engineering / add_code_comments, update_plan for
// Antigravity), the Ultra split guide, the brain's safe tools (Gemini, Codex, Grok) and Codex's pictures (--image).
// All pure: no CLI is started, nothing reaches a network.
using System.IO;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Attachments;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;

namespace ApiTest;

internal static class CliParityWire
{
    const string Folder = @"C:\work\demo";
    const string BrainJson = """{"type":"stdio","command":"C:\\b\\brainx-mcp.exe","args":["G:\\Vault"],"env":{"BRAINX_VAULT":"G:\\Vault"}}""";

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · CLI parity: chat tools, split guide, brain tools, Codex pictures");
        var worker = new ClaudeDevProvider.WorkerGrant(true, true);

        // ─── the chat-tools line ───
        string plain = ChatToolBridge.ChatToolsHint(ChatToolBridge.CliToolNames.Plain, splitChats: true);
        var missing = EngineeringToolModule.ToolNames.Append("new_chat").Append("close_chat").Append("wrap_up").Append("add_code_comments")
            .Where(t => !plain.Contains(t)).ToList();
        check("cp-hint", "Codex/Gemini/Antigravity line names new_chat, close_chat, wrap_up, every engineering tool and add_code_comments",
            missing.Count == 0 && plain.Contains("(MCP server cluadex)") && !plain.Contains("update_plan") && !plain.Contains('\n'),
            missing.Count > 0 ? "missing: " + string.Join(",", missing) : plain);
        check("cp-hint", "…update_plan only for Antigravity (plan: true)",
            ChatToolBridge.ChatToolsHint(ChatToolBridge.CliToolNames.Plain, true, plan: true).Contains("update_plan"), "");
        string claude = ChatToolBridge.ChatToolsHint(ChatToolBridge.CliToolNames.Claude, splitChats: false);
        check("cp-hint", "Claude names: mcp__cluadex__wrap_up / mcp__cluadex__verify_change; no second new_chat line",
            claude.Contains("mcp__cluadex__wrap_up") && claude.Contains("mcp__cluadex__verify_change") && !claude.Contains("new_chat"), claude);
        string grok = ChatToolBridge.ChatToolsHint(ChatToolBridge.CliToolNames.Grok, splitChats: false);
        check("cp-hint", "Grok names: cluadex__add_code_comments through use_tool",
            grok.Contains("cluadex__add_code_comments") && grok.Contains("use_tool") && !grok.Contains("mcp__"), grok);

        string codexPrompt = OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go", chatTools: true);
        check("cp-wrap", "Codex wrapper carries the line when the turn has the chat tools, not without",
            codexPrompt.Contains("close_chat lists") && !OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go").Contains("close_chat lists"), "");
        string geminiP = GeminiDevProvider.BuildArgs(Folder, worker, "default", null, chatTools: true).Last();
        check("cp-wrap", "Gemini's -p wrapper carries it", geminiP.Contains("close_chat lists") && geminiP.Contains("wrap_up"), "");
        string agy = AntigravityDevProvider.BuildPrompt(Folder, worker, null, "", "go", chatTools: true);
        check("cp-wrap", "Antigravity's wrapper carries it with the plan card", agy.Contains("close_chat lists") && agy.Contains("update_plan keeps the plan card"), "");
        string claudeArgs = ClaudeDevProvider.BuildArgs(Folder, worker, "default", "auto", toolsConfig: @"C:\t\tools.json");
        check("cp-wrap", "Claude Dev's wrapper now names wrap_up and the engineering tools", claudeArgs.Contains("mcp__cluadex__wrap_up") && claudeArgs.Contains("mcp__cluadex__impact_of"), "");
        string grokHint = GrokDevProvider.ChatToolsHint(null, true) ?? "";
        check("cp-wrap", "Grok Dev's tools line now names wrap_up and the engineering tools", grokHint.Contains("cluadex__wrap_up") && grokHint.Contains("cluadex__gh_pr_checks"), grokHint);

        // ─── the Ultra split guide in each CLI's names ───
        check("cp-split", "Claude Dev's guide is the one it always had", WorkflowGate.SplitGuide(AiProviderType.ClaudeDev) == WorkflowGate.SplitGuide(true), "");
        string gCodex = WorkflowGate.SplitGuide(AiProviderType.OpenAIDev), gGrok = WorkflowGate.SplitGuide(AiProviderType.GrokDev),
            gGem = WorkflowGate.SplitGuide(AiProviderType.GeminiDev), gAgy = WorkflowGate.SplitGuide(AiProviderType.AntigravityDev);
        check("cp-split", "Codex: spawn_agent + workflow on cluadex; Grok: spawn_subagent + cluadex__workflow; Gemini: codebase_investigator",
            gCodex.Contains("spawn_agent") && gCodex.Contains("workflow (MCP server cluadex)") && !gCodex.Contains("Agent tool")
            && gGrok.Contains("spawn_subagent") && gGrok.Contains("cluadex__workflow") && gGrok.Contains("cluadex__new_chat")
            && gGem.Contains("codebase_investigator"), gGrok);
        check("cp-split", "Antigravity (no sub-agent tool) is sent straight to the workflow", gAgy.Contains("no sub-agent tool") && !gAgy.Contains("agent_spawn"), gAgy);
        check("cp-split", "the guide reaches a Codex worker turn only with the chat tools",
            OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go", chatTools: true, splitGuide: gCodex).Contains("Ultra on")
            && !OpenAIDevProvider.BuildPrompt(Folder, null, null, "", "go", chatTools: true, splitGuide: gCodex).Contains("Ultra on")
            && !OpenAIDevProvider.BuildPrompt(Folder, worker, null, "", "go", splitGuide: gCodex).Contains("Ultra on"), "");
        check("cp-split", "a chat that did not opt in gets no guide", WorkflowGate.SplitGuideFor("cp-no-ultra-" + Guid.NewGuid(), AiProviderType.GrokDev) == null, "");

        // ─── the brain's safe tools ───
        var readTools = ClaudeDevProvider.BrainToolsFor(null);
        var gem = GeminiDevProvider.BuildTurnSettings(null, null, BrainJson, brainTools: readTools);
        var gb = gem["mcpServers"]?["brainx-brain"] as JsonObject;
        var include = (gb?["includeTools"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToList() ?? new();
        check("cp-brain", "Gemini: the brain is trusted but only for Claude Dev's unasked set (no ssh_run, no writes read-only)",
            gb?["trust"]?.GetValue<bool>() == true && include.Contains("brain_search") && include.Contains("task_queue")
            && !include.Contains("ssh_run") && !include.Contains("brain_remember") && gb["type"] == null, gb?.ToJsonString() ?? "(none)");
        var codexArgs = OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null, brainInCodexConfig: false, brainServerJson: BrainJson);
        string? whole = codexArgs.FirstOrDefault(a => a.StartsWith("mcp_servers.brainx-brain={"));
        check("cp-brain", "Codex without its own brain: the whole server from ~/.claude.json, with only the safe tools approved",
            whole != null && whole.Contains("command='C:\\b\\brainx-mcp.exe'") && whole.Contains("brain_search={approval_mode='approve'}")
            && !whole.Contains("ssh_run") && !whole.Contains("brain_remember"), whole ?? string.Join(" ", codexArgs));
        var codexOwn = OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null, brainInCodexConfig: true, brainServerJson: BrainJson);
        check("cp-brain", "…and when Codex's config has it: per-tool approvals only, no second server",
            !codexOwn.Any(a => a.StartsWith("mcp_servers.brainx-brain={")) && codexOwn.Any(a => a.StartsWith("mcp_servers.brainx-brain.tools.brain_search")), "");
        string? grokToml = GrokTurnHome.BrainToml(BrainJson, "model = 'x'\n");
        check("cp-brain", "Grok: the brain as a config.toml table, none when the user's file has one",
            grokToml != null && grokToml.Contains("[mcp_servers.brainx-brain]") && grokToml.Contains("BRAINX_VAULT")
            && GrokTurnHome.BrainToml(BrainJson, "[mcp_servers.brainx-brain]\ncommand = 'x'\n") == null, grokToml ?? "(none)");
        var grokArgs = GrokDevProvider.BuildArgs(Folder, null, "default", "auto", null, @"C:\t\p.txt", brainTools: readTools);
        check("cp-brain", "Grok: each safe brain tool allowed by name (MCPTool(brainx-brain__brain_search)), never the whole server",
            grokArgs.Contains("MCPTool(brainx-brain__brain_search)") && !grokArgs.Any(a => a.Contains("brainx-brain__*") || a.Contains("ssh_run")),
            string.Join(" ", grokArgs));

        // ─── Codex's pictures ───
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-apitest-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "shot one.png"), doc = Path.Combine(dir, "notes.txt");
            File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            File.WriteAllText(doc, "hello");
            string cliText = AttachmentDigest.Build(new[] { png }, new[] { doc }, AttachmentAudience.CliAgent, DigestBudget.Cli).Text;
            var back = AttachmentDigest.CliImagePaths("ทำให้หน่อย" + cliText);
            check("cp-image", "the CLI digest's image lines read back to their paths (files are not pictures)",
                back.Count == 1 && back[0] == png, string.Join(" | ", back) + " ← " + cliText);
            string blind = AttachmentDigest.Build(new[] { png }, Array.Empty<string>(), AttachmentAudience.LoopBlind, DigestBudget.Api).Text;
            check("cp-image", "another audience's lines, a typed non-image path and plain text give nothing",
                AttachmentDigest.CliImagePaths(blind).Count == 0
                && AttachmentDigest.CliImagePaths("1. a.exe — image, 1 B — C:\\x\\a.exe" + AttachmentDigest.CliImageNote).Count == 0
                && AttachmentDigest.CliImagePaths(null).Count == 0, "");
            var withImages = OpenAIDevProvider.BuildArgs(Folder, null, "default", "auto", null, images: new[] { png, @"C:\a,b.png" });
            check("cp-image", "Codex: --image=<path> per picture before the stdin '-', a path with a comma left out",
                withImages.Contains("--image=" + png) && !withImages.Any(a => a.Contains("a,b.png")) && withImages[^1] == "-"
                && withImages.IndexOf("--image=" + png) == withImages.Count - 2, string.Join(" ", withImages));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }
}
