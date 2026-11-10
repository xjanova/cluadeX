using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using Kind = CluadeX.ViewModels.SettingsViewModel.ProviderCardKind;

namespace ApiTest;

/// <summary>
/// The Settings page clean-up (owner 2026-11-10: "หน้า settings ดูรก … ตั้งค่า provider ซ้ำซ้อน มีตัวเลือกที่ไม่จำเป็น"):
/// the connection card shows each field only for the providers that read it, the picker is grouped, the autonomous loop's
/// boxes cap where the loop does, and the settings that nothing read are gone without costing an old settings.json anything.
/// The view model is never constructed (it wants the whole app): the rules are static, the instance ones are read off an
/// uninitialized object with only the provider field set.
/// </summary>
internal static class SettingsLayoutWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Settings page layout (which field shows for which provider)");
        var all = Enum.GetValues<AiProviderType>();

        // ── the picker ──
        var rows = SettingsViewModel.BuildProviderChoices();
        check("layout", "the picker lists every provider exactly once",
            rows.Count == all.Length && rows.Select(r => r.Type).Distinct().Count() == all.Length, $"{rows.Count}/{all.Length}");
        var groupRuns = rows.Select(r => r.Group.Key).Where((g, i) => i == 0 || g != rows[i - 1].Group.Key).ToList();
        check("layout", "its groups are contiguous (each header once): cloud, CLI, compatible, this PC, other",
            groupRuns.SequenceEqual(new[] { "settings.provGroup.cloud", "settings.provGroup.cli", "settings.provGroup.compatible",
                "settings.provGroup.local", "settings.provGroup.other" }), string.Join(" > ", groupRuns));
        check("layout", "no row shows a bare enum name for the CLIs or the GGUF runners",
            rows.Where(r => r.Type is AiProviderType.ClaudeDev or AiProviderType.OpenAIDev or AiProviderType.Local or AiProviderType.LlamaServer)
                .All(r => r.Name != r.Type.ToString()), string.Join(", ", rows.Take(12).Select(r => r.Name)));
        var loc = new LocalizationService();
        loc.SetLanguage("th");
        check("layout", "every group title has a Thai text",
            rows.Select(r => r.Group.Key).Distinct().All(k => loc.T(k) != k && loc.T(k) != LocalizationService.Lookup(k, "en")), "");

        // ── the connection card's fields ──
        check("layout", "every provider has a kind (none falls to a default by accident)",
            all.All(t => Enum.IsDefined(SettingsViewModel.KindOf(t))), "");
        check("layout", "no model box for llama.cpp, the GGUF runner, BrainX or Colibri — the others keep it",
            new[] { AiProviderType.LlamaServer, AiProviderType.Local, AiProviderType.BrainX, AiProviderType.Colibri }.All(t => !SettingsViewModel.ShowsModelFields(t))
            && new[] { AiProviderType.Anthropic, AiProviderType.ClaudeDev, AiProviderType.Ollama, AiProviderType.OpenRouter, AiProviderType.LMStudio }
                .All(SettingsViewModel.ShowsModelFields), "");
        check("layout", "the address box is out in the open for Azure, a custom endpoint, Ollama, LM Studio and vLLM only",
            all.Where(SettingsViewModel.NeedsBaseUrl).OrderBy(t => t).SequenceEqual(new[] { AiProviderType.Ollama, AiProviderType.AzureOpenAI,
                AiProviderType.LMStudio, AiProviderType.Vllm, AiProviderType.OpenAICompatible }.OrderBy(t => t)),
            string.Join(",", all.Where(SettingsViewModel.NeedsBaseUrl)));

        bool Card(AiProviderType t, string prop)
        {
            var vm = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
            typeof(SettingsViewModel).GetField("_selectedProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, t);
            return (bool)typeof(SettingsViewModel).GetProperty(prop)!.GetValue(vm)!;
        }
        check("layout", "no key box for the CLIs, BrainX, Ollama or the GGUF runners",
            new[] { AiProviderType.ClaudeDev, AiProviderType.GeminiDev, AiProviderType.OpenAIDev, AiProviderType.GrokDev, AiProviderType.AntigravityDev,
                AiProviderType.BrainX, AiProviderType.Ollama, AiProviderType.LlamaServer, AiProviderType.Local }.All(t => !Card(t, "NeedsApiKey")), "");
        check("layout", "the key is marked optional on LM Studio, vLLM, Colibri and a custom endpoint, required on the clouds",
            new[] { AiProviderType.LMStudio, AiProviderType.Vllm, AiProviderType.Colibri, AiProviderType.OpenAICompatible }.All(t => Card(t, "ApiKeyOptional"))
            && new[] { AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini, AiProviderType.OpenRouter, AiProviderType.AzureOpenAI }
                .All(t => !Card(t, "ApiKeyOptional")), "");
        check("layout", "a fixed-address service keeps its proxy box behind a switch; Colibri (its plugin writes the address) has none",
            Card(AiProviderType.Anthropic, "BaseUrlIsAdvanced") && Card(AiProviderType.OpenRouter, "BaseUrlIsAdvanced")
            && !Card(AiProviderType.Anthropic, "ShowBaseUrlBox") && Card(AiProviderType.AzureOpenAI, "ShowBaseUrlBox")
            && !Card(AiProviderType.Colibri, "ShowBaseUrlBox") && !Card(AiProviderType.Colibri, "BaseUrlIsAdvanced")
            && !Card(AiProviderType.ClaudeDev, "ShowBaseUrlBox") && !Card(AiProviderType.LlamaServer, "ShowBaseUrlBox"), "");

        string Untick(AiProviderType t)
        {
            var vm = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
            var f = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(SettingsViewModel).GetField("_selectedProvider", f)!.SetValue(vm, t);
            typeof(SettingsViewModel).GetField("_providerBaseUrl", f)!.SetValue(vm, "https://proxy.example/v1");
            typeof(SettingsViewModel).GetField("_showBaseUrlOverride", f)!.SetValue(vm, true);
            vm.ShowBaseUrlOverride = false;
            return vm.ProviderBaseUrl;
        }
        check("layout", "unticking \"use another address\" empties the proxy (no hidden address left routing requests); a required address is never emptied",
            Untick(AiProviderType.Anthropic) == "" && Untick(AiProviderType.AzureOpenAI) == "https://proxy.example/v1",
            $"anthropic='{Untick(AiProviderType.Anthropic)}', azure='{Untick(AiProviderType.AzureOpenAI)}'");

        // ── Model & output: who reads the sliders (AnthropicProvider, CompatibleProvider, GeminiProvider, Ollama, llama-server) ──
        check("layout", "Temperature shows for the APIs, Ollama and the GGUF runners, not for the CLIs or BrainX",
            new[] { AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini, AiProviderType.Groq, AiProviderType.Ollama, AiProviderType.LlamaServer }
                .All(SettingsViewModel.ReadsTemperature)
            && new[] { AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.BrainX }.All(t => !SettingsViewModel.ReadsTemperature(t)), "");
        check("layout", "Top P shows for Gemini, Ollama and the GGUF runners only",
            all.Where(SettingsViewModel.ReadsTopP).OrderBy(t => t).SequenceEqual(new[] { AiProviderType.Local, AiProviderType.LlamaServer,
                AiProviderType.Gemini, AiProviderType.Ollama }.OrderBy(t => t)), string.Join(",", all.Where(SettingsViewModel.ReadsTopP)));

        // ── Autonomous: the boxes cap where the loop does ──
        var writers = (System.Collections.IDictionary)typeof(SettingsViewModel)
            .GetField("Writers", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var page = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
        page.AutoFixMaxIterations = 50;
        page.AutoReviewMaxRounds = 40;
        var saved = new AppSettings();
        ((Action<SettingsViewModel, AppSettings>)writers[nameof(SettingsViewModel.AutoFixMaxIterations)]!)(page, saved);
        ((Action<SettingsViewModel, AppSettings>)writers[nameof(SettingsViewModel.AutoReviewMaxRounds)]!)(page, saved);
        check("layout", "fix iterations and review rounds save at most the loop's own cap (25, AutonomousCodingService.MaxLoopCap)",
            AutonomousCodingService.MaxLoopCap == 25 && saved.AutoFixMaxIterations == 25 && saved.AutoReviewMaxRounds == 25,
            $"{saved.AutoFixMaxIterations}/{saved.AutoReviewMaxRounds}");

        // ── the dead settings are gone, and an old settings.json that has them still loads whole ──
        var gone = new[] { "StreamOutput", "ChromeActWithoutAsking", "PreferredLanguage", "FontSize" };
        check("layout", "StreamOutput, ChromeActWithoutAsking, PreferredLanguage and FontSize are no longer settings",
            gone.All(n => typeof(AppSettings).GetProperty(n) == null), "");
        check("layout", "the page no longer carries the unused ProviderOptions list or the temp-folder browse command",
            typeof(SettingsViewModel).GetProperty("ProviderOptions") == null && typeof(SettingsViewModel).GetProperty("BrowseTempDirCommand") == null, "");
        const string oldJson = """
        {
          "StreamOutput": false,
          "FontSize": 18,
          "PreferredLanguage": "Python",
          "ChromeActWithoutAsking": true,
          "ModelDirectory": "D:\\Models",
          "ContextSize": 49152,
          "ActiveProvider": "Anthropic",
          "ProviderConfigs": { "Anthropic": { "ApiKey": "ENC:kept", "SelectedModel": "claude-opus-5-5" } }
        }
        """;
        var opts = (JsonSerializerOptions)typeof(SettingsService).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        AppSettings? read = null;
        string why = "";
        try { read = JsonSerializer.Deserialize<AppSettings>(oldJson, opts); }
        catch (Exception ex) { why = ex.Message; }
        check("layout", "a settings.json still holding the removed keys loads with every other value and the key kept (nothing reset)",
            read != null && read.ModelDirectory == @"D:\Models" && read.ContextSize == 49152 && read.ActiveProvider == AiProviderType.Anthropic
            && read.ProviderConfigs.TryGetValue("Anthropic", out var a) && a.ApiKey == "ENC:kept", why);
        var salvaged = SettingsService.Salvage(oldJson, out var dropped);
        check("layout", "the salvage path drops none of it either", salvaged != null && dropped.Count == 0, string.Join(",", dropped));
        string rewritten = JsonSerializer.Serialize(read ?? new AppSettings(), opts);
        check("layout", "the next save leaves the removed keys out", gone.All(n => !rewritten.Contains($"\"{n}\"")), "");
        return Task.CompletedTask;
    }
}
