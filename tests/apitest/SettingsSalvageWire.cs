using CluadeX.Models;
using CluadeX.Services;

namespace ApiTest;

/// <summary>
/// settings.json that this build cannot fully read (2026-10-19: an older 3.0.153 met "ActiveProvider": "GeminiDev",
/// threw on that one enum and reset the whole file — the API keys with it). Every setting that still reads is kept.
/// </summary>
internal static class SettingsSalvageWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · settings.json a build cannot fully read keeps what it can");

        const string json = """
        {
          "ModelDirectory": "E:\\AI Model",
          "ActiveProvider": "SomeFutureProvider",
          "ContextSize": 32768,
          "ProviderConfigs": {
            "Anthropic": { "ApiKey": "ENC:anthropic", "SelectedModel": "claude-opus-5-5" },
            "OpenAI": { "ApiKey": "ENC:openai" },
            "Broken": { "UseCustomModel": "not a bool" }
          }
        }
        """;
        var s = SettingsService.Salvage(json, out var dropped);
        check("salvage", "an unknown provider name drops only that setting (left at its default)",
            s != null && dropped.Contains("ActiveProvider") && s.ActiveProvider == new AppSettings().ActiveProvider, string.Join(",", dropped));
        check("salvage", "the other settings are kept",
            s != null && s.ModelDirectory == @"E:\AI Model" && s.ContextSize == 32768, $"{s?.ModelDirectory} {s?.ContextSize}");
        check("salvage", "the API keys survive; only the provider entry that does not read is dropped",
            s != null && s.ProviderConfigs.TryGetValue("Anthropic", out var a) && a.ApiKey == "ENC:anthropic"
            && s.ProviderConfigs.TryGetValue("OpenAI", out var o) && o.ApiKey == "ENC:openai"
            && !s.ProviderConfigs.ContainsKey("Broken") && dropped.Contains("ProviderConfigs.Broken"),
            string.Join(",", dropped));
        check("salvage", "text that is not a JSON object salvages nothing",
            SettingsService.Salvage("[1,2]", out _) == null, "");
        return Task.CompletedTask;
    }
}
