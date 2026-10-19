// OpenAI's GPT-6 family (owner 2026-10-19: "chat gpt มีโมเดลใหม่ … เพิ่มโมเดลตัวใหม่ให้ด้วย"), checked against
// developers.openai.com/api/docs/models/{gpt-6-astra,gpt-6.1-sol,gpt-6-luna}, /pricing and /guides/reasoning on 2026-10-19:
// all three are reasoning models with a 1.05M window (922K in + 128K out); Astra $10/$1/$50, 6.1 Sol $2/$0.10/$10,
// Luna $0.10/$0.01/$0.50 per 1M (input/cached/output), cache writes 1.25x input, a prompt over 272K bills 2x in and 1.5x
// out; efforts low…max (Astra and 6.1 Sol refuse "none"); temperature/top_p go only with effort "none". GPT-Live
// (gpt-live-1), the realtime models and GPT Image 2.5 (sunburst / flare) are not chat models.
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class Gpt6Wire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · OpenAI GPT-6 Astra / 6.1 Sol / Luna");

        check("gpt6", "default model is gpt-6.1-sol; the list starts astra, 6.1-sol, 6-sol, luna",
            OpenAiProvider.DefaultModel == "gpt-6.1-sol"
            && string.Join(",", OpenAiProvider.KnownModels.Take(4)) == "gpt-6-astra,gpt-6.1-sol,gpt-6-sol,gpt-6-luna",
            string.Join(",", OpenAiProvider.KnownModels.Take(4)));

        // Effort: every level the picker has goes out as one of low…max, never "none"/"minimal" (a 400 on Astra / 6.1 Sol).
        string[] gpt6 = ["gpt-6-astra", "gpt-6.1-sol", "gpt-6-sol", "gpt-6-luna"];
        var sent = gpt6.SelectMany(m => EffortLevels.All.Select(p => OpenAiProvider.EffortFor(m, p, true))).ToList();
        check("gpt6", "efforts: every pick → low/medium/high/xhigh/max as picked (auto + Think → high)",
            gpt6.All(m => OpenAiProvider.EffortFor(m, "max", false) == "max" && OpenAiProvider.EffortFor(m, "xhigh", false) == "xhigh"
                          && OpenAiProvider.EffortFor(m, "low", false) == "low" && OpenAiProvider.EffortFor(m, "auto", true) == "high")
            && sent.All(e => e is "low" or "medium" or "high" or "xhigh" or "max"),
            string.Join(",", sent));
        check("gpt6", "auto + Think off sends no effort (the model's own default, medium)",
            gpt6.All(m => OpenAiProvider.EffortFor(m, "auto", false) == null), "");

        check("gpt6", "output budget: 128K ceiling, 32K reasoning floor; models with no known ceiling unchanged",
            OpenAiProvider.OutputBudget("gpt-6-astra", 200_000) == 128_000 && OpenAiProvider.OutputBudget("gpt-6-luna", 4096) == 32_000
            && OpenAiProvider.OutputBudget("openai/gpt-6.1-sol", 500_000) == 128_000 && OpenAiProvider.OutputBudget("gpt-5.5", 200_000) == 200_000
            && OpenAiProvider.OutputBudget("gpt-4o", 4096) == 4096,
            $"{OpenAiProvider.OutputBudget("gpt-6-astra", 200_000)}/{OpenAiProvider.OutputBudget("gpt-6-luna", 4096)}");

        int W(string id) => ContextMemoryService.ContextWindowFor(AiProviderType.OpenAI, id, 12288);
        check("gpt6", "context window 1,050,000 (was read as 128K); gpt-5.x stays 400K",
            gpt6.All(m => W(m) == 1_050_000) && W("openai/gpt-6-luna") == 1_050_000 && W("gpt-5.5") == 400_000,
            $"astra={W("gpt-6-astra")} sol={W("gpt-6.1-sol")} gpt-5.5={W("gpt-5.5")}");

        bool P(string id, decimal i, decimal o, decimal cr, decimal cw, int prompt = 0)
        {
            var p = CostTrackingService.PricingOf(id, prompt);
            return p.InputPerMToken == i && p.OutputPerMToken == o && p.CacheReadPerMToken == cr && p.CacheWritePerMToken == cw;
        }
        check("gpt6", "list prices: astra 10/50 (cache 1, write 12.50), 6.1 sol 2/10 (0.10, 2.50), 6 sol 2/10 (0.20), luna 0.10/0.50 (0.01, 0.125)",
            P("gpt-6-astra", 10m, 50m, 1m, 12.5m) && P("gpt-6.1-sol", 2m, 10m, 0.10m, 2.5m) && P("gpt-6-sol", 2m, 10m, 0.20m, 2.5m)
            && P("gpt-6-luna", 0.10m, 0.50m, 0.01m, 0.125m), "");
        check("gpt6", "a prompt over 272K: 2x input/cache, 1.5x output; exactly 272K stays standard",
            P("gpt-6.1-sol", 4m, 15m, 0.20m, 5m, 272_001) && P("gpt-6-astra", 20m, 75m, 2m, 25m, 300_000)
            && P("gpt-6-luna", 0.20m, 0.75m, 0.02m, 0.25m, 900_000) && P("gpt-6.1-sol", 2m, 10m, 0.10m, 2.5m, 272_000)
            && P("gpt-5.5", 5m, 30m, 0.50m, 0m, 900_000), "");
        // 100K fresh + 200K cached = a 300K prompt → long-context rates for the whole request.
        decimal cost = CostTrackingService.Price("gpt-6.1-sol", 100_000, 10_000, cacheReadTokens: 200_000).Actual;
        check("gpt6", "one 6.1 Sol request, 100K in + 200K cached + 10K out = $0.59 (long-context rates)", cost == 0.59m, cost.ToString());

        string[] notChat = ["gpt-live-1", "gpt-realtime-2.1", "gpt-realtime-2.1-mini", "gpt-realtime-translate", "gpt-transcribe",
            "gpt-live-transcribe", "gpt-realtime-whisper", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare-2026-09-08", "gpt-4o-mini-tts"];
        check("gpt6", "model list keeps GPT-6, drops GPT-Live / realtime / transcribe / GPT Image 2.5",
            gpt6.All(OpenAiProvider.IsChatModel) && !notChat.Any(OpenAiProvider.IsChatModel),
            string.Join(",", notChat.Where(OpenAiProvider.IsChatModel)));
        check("gpt6", "efforts / output ceiling only for chat models: gpt-live-1 and gpt-image-2.5 are neither",
            OpenAiProvider.MaxEffort("gpt-live-1") == null && OpenAiProvider.MaxOutputTokens("gpt-image-2.5-sunburst") == null, "");

        check("gpt6", "friendly names: GPT-6 Astra, GPT-6.1 Sol, GPT-6 Luna, GPT-5.4 Mini, GPT-5.5, GPT-4o",
            LiveModelReport.FriendlyModel("gpt-6-astra") == "GPT-6 Astra" && LiveModelReport.FriendlyModel("gpt-6.1-sol") == "GPT-6.1 Sol"
            && LiveModelReport.FriendlyModel("openai/gpt-6-luna") == "GPT-6 Luna" && LiveModelReport.FriendlyModel("gpt-5.4-mini") == "GPT-5.4 Mini"
            && LiveModelReport.FriendlyModel("gpt-5.5-2026-09-01") == "GPT-5.5" && LiveModelReport.FriendlyModel("gpt-4o") == "GPT-4o",
            LiveModelReport.FriendlyModel("gpt-6.1-sol") + " | " + LiveModelReport.FriendlyModel("gpt-5.4-mini"));

        check("gpt6", "dispatch tiers: explore on Luna, the default Sol escalates a hard fix to Astra, Astra stays",
            DispatchPolicy.CheapestModel(AiProviderType.OpenAI) == "gpt-6-luna"
            && DispatchPolicy.StrongerModel(AiProviderType.OpenAI, OpenAiProvider.DefaultModel) == "gpt-6-astra"
            && DispatchPolicy.StrongerModel(AiProviderType.OpenAI, "gpt-6-astra") == "gpt-6-astra", "");

        // The agent loop's request (Responses API): tools, no temperature, effort as picked, output held at 128K.
        var svc = new SettingsService();   // portable — never the user's settings
        var s = svc.Settings;
        int savedMax = s.MaxTokens; string savedEffort = s.ReasoningEffort; bool savedThink = s.ExtendedThinkingEnabled;
        try
        {
            s.MaxTokens = 300_000; s.ReasoningEffort = "xhigh"; s.ExtendedThinkingEnabled = true;
            s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-gpt6wire", SelectedModel = "gpt-6-astra" };
            var oa = new OpenAiProvider(svc);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(oa.BuildResponsesRequest(
                [new NativeMessage { Role = "user", Content = [new ContentBlock { Type = "text", Text = "hi" }] }],
                "sys", ImageWireGolden.Tools(), null)));
            var b = doc.RootElement;
            bool hasTemp = b.TryGetProperty("temperature", out _) || b.TryGetProperty("top_p", out _);
            check("gpt6", "Responses request for gpt-6-astra: max_output_tokens 128000, reasoning.effort xhigh + summary, encrypted reasoning, tools, no temperature/top_p",
                b.GetProperty("model").GetString() == "gpt-6-astra" && b.GetProperty("max_output_tokens").GetInt32() == 128_000
                && b.GetProperty("reasoning").GetProperty("effort").GetString() == "xhigh"
                && b.GetProperty("reasoning").GetProperty("summary").GetString() == "auto"
                && b.GetProperty("include")[0].GetString() == "reasoning.encrypted_content"
                && b.GetProperty("tools").GetArrayLength() > 0 && !hasTemp,
                b.GetRawText().Length > 400 ? b.GetRawText()[..400] : b.GetRawText());
        }
        finally
        {
            s.MaxTokens = savedMax; s.ReasoningEffort = savedEffort; s.ExtendedThinkingEnabled = savedThink;
        }
        return Task.CompletedTask;
    }
}
