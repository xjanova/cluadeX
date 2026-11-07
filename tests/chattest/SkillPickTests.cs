using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.Services.Skills;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// SP — smart skill picking through the real chat page and the real native tool loop (owner 2026-11-07: "ระบบ เลือกเปิดสกิล
/// อัตโนมัติที่สอดคล้องกับงาน ควรมี ให้เลือกปิดได้ในช่องแชท"). A scripted provider records what each request carried; no
/// network, no model, no brain.
/// </summary>
internal static partial class Program
{
    /// <summary>A turn's text without the skill block smart picking adds after it (the block is checked in SP).</summary>
    internal static string WithoutSkills(string text)
    {
        if (text == null) return null;
        string nl = Environment.NewLine.Length == 2 ? "\n" : Environment.NewLine;
        foreach (var head in new[] { nl + nl + "[Skill ที่เข้ากับงานนี้", nl + nl + "[Skills that fit this task" })
        {
            int at = text.IndexOf(head, StringComparison.Ordinal);
            if (at >= 0) return text[..at];
        }
        return text;
    }

    static async Task TestSkillPick()
    {
        Log("\n## SP — each turn is given only the skills that fit it; the composer's switch turns that off");

        var provider = new NativeScript();
        object before = Field<object>(Providers, "_activeProvider");
        bool agenticWas = Vm.AgenticMode;
        bool permBefore = Settings.Settings.Features.PermissionSystem;
        bool defaultWas = Settings.Settings.SkillAutoPickEnabled;
        var skills = Sp.GetRequiredService<SkillService>();
        ChatMessage? PickRow() => Vm.Messages.LastOrDefault(m => m.Role == MessageRole.System && m.Content.StartsWith("✦ "));
        int Rows() => Vm.Messages.Count(m => m.Role == MessageRole.System && m.Content.StartsWith("✦ "));
        try
        {
            SetField(Providers, "_activeProvider", provider);
            Vm.AgenticMode = true;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = false; s.SkillAutoPickEnabled = true; });
            skills.ForgetLearnedTerms();   // words learned by an earlier harness run must not decide this one
            await FreshChat();

            // ── SP1: a security task gets security-scan, attached to the turn's message, and the chat says so ──
            Check("SP1 a new chat follows the default: the composer's Skill switch is on", Vm.IsSkillAutoPick);
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "Scanned." }));
            await Send("[SP1] scan this code for security holes and hardcoded secrets");
            await WaitIdle();
            string sent = provider.Texts.LastOrDefault() ?? "";
            string prompt = provider.Prompts.LastOrDefault() ?? "";
            var row = PickRow();
            Check("SP1 the turn's message carries the picked skills, security-scan among them",
                sent.Contains("`security-scan`") && sent.Contains("skill_invoke"), sent.Length > 400 ? sent[^400..] : sent);
            Check("SP1 …and not the skills that do not fit (no content-engine, no market-research)",
                !sent.Contains("`content-engine`") && !sent.Contains("`market-research`"), sent.Length > 900 ? sent[^900..] : sent);
            Check("SP1 the chat shows the pick under the message, with what it saved",
                row != null && row.Content.Contains("security-scan"), row?.Content ?? "(no row)");
            Check("SP1 the system prompt does not list every skill while picking is on",
                !prompt.Contains("`content-engine`:") && !prompt.Contains("`multi-execute`:"));

            // ── SP2: the switch off for this chat = the old way: no block, and only this chat ──
            Vm.IsSkillAutoPick = false;
            int rowsBefore = Rows();
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "ok" }));
            await Send("[SP2] scan this code for security holes again");
            await WaitIdle();
            sent = provider.Texts.LastOrDefault() ?? "";
            Check("SP2 switched off in the composer: no skill block on the turn and no ✦ row",
                !sent.Contains("`security-scan`:") && Rows() == rowsBefore, sent.Length > 300 ? sent[^300..] : sent);
            Check("SP2 the switch is this chat's: the default is still on", Settings.Settings.SkillAutoPickEnabled
                && SkillPickGate.IsOn("some-other-chat", Settings.Settings.SkillAutoPickEnabled));
            Vm.IsSkillAutoPick = true;

            // ── SP3: a small talk turn: nothing fits, no row ──
            rowsBefore = Rows();
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "hi!" }));
            await Send("hi");
            await WaitIdle();
            sent = provider.Texts.LastOrDefault() ?? "";
            Check("SP3 \"hi\" picks nothing: no block, no row", !sent.Contains("skill_invoke") && Rows() == rowsBefore, sent);

            // ── SP4: the model runs a skill: the turn's words are learned for it ──
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse
            {
                StopReason = "tool_use",
                ToolCalls = { new NativeToolCall { Id = "s1", Name = "skill_invoke",
                    Input = JsonDocument.Parse("{\"skill\":\"e2e-testing\"}").RootElement.Clone() } },
            }));
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "triaged" }));
            await Send("[SP4] the checkout flow keeps breaking on staging");
            await WaitIdle();
            var learned = skills.LearnedTerms("e2e-testing");
            Check("SP4 a skill the model ran learns the turn's words (checkout, staging)",
                learned.ContainsKey("checkout") && learned.ContainsKey(SkillPicker.Stem("staging")), string.Join(",", learned.Keys));
            Check("SP4 …the owner's words only, not the skill block or [SP4] tag noise",
                !learned.ContainsKey("skill_invoke") && !learned.ContainsKey("invoke"), string.Join(",", learned.Keys));

            // ── SP5: said that way again, the learned skill is picked ──
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "ok" }));
            await Send("[SP5] checkout on staging is broken again");
            await WaitIdle();
            sent = provider.Texts.LastOrDefault() ?? "";
            Check("SP5 words learned from an earlier turn pick e2e-testing", sent.Contains("`e2e-testing`"),
                sent.Length > 300 ? sent[^300..] : sent);

            // ── SP6: a /command turn runs its skill: no pick on top ──
            rowsBefore = Rows();
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "planned" }));
            await Send("/multi-plan add a dark mode");
            await WaitIdle();
            sent = provider.Texts.LastOrDefault() ?? "";
            Check("SP6 /multi-plan runs its own prompt with no skill block and no ✦ row",
                sent.Contains("THREE distinct plans") && !sent.Contains("[Skill") && !sent.Contains("[Skills that fit") && Rows() == rowsBefore,
                sent.Length > 200 ? sent[..200] : sent);
            Check("SP6 …and what it was asked for is learned for it", skills.LearnedTerms("multi-plan").ContainsKey("dark"));
            prompt = provider.Prompts.LastOrDefault() ?? "";
            Check("SP6 …and its system prompt keeps the one fixed skills line (not the full list): the chat's prompt cache holds",
                !prompt.Contains("`content-engine`:") && !prompt.Contains("`security-scan`:"));

            // ── SP7: the default off on the Skills page = chats with no override follow it ──
            await FreshChat();
            Settings.UpdateSettings(s => s.SkillAutoPickEnabled = false);
            SkillPickGate.DefaultChanged();
            Check("SP7 the Skills page's default off: a new chat's switch is off", !Vm.IsSkillAutoPick);
            provider.Steps.Enqueue((_, _) => Task.FromResult(new NativeToolResponse { StopReason = "end_turn", TextContent = "ok" }));
            await Send("[SP7] scan for security holes");
            await WaitIdle();
            prompt = provider.Prompts.LastOrDefault() ?? "";
            sent = provider.Texts.LastOrDefault() ?? "";
            Check("SP7 …and the turn has no block (the system prompt lists skills the old way when it lists any)",
                !sent.Contains("`security-scan`:"));
        }
        finally
        {
            SetField(Providers, "_activeProvider", before);
            Vm.AgenticMode = agenticWas;
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = permBefore; s.SkillAutoPickEnabled = defaultWas; });
            SkillPickGate.DefaultChanged();
        }
    }
}
