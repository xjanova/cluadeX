using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

int checks = 0;
void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
string vault = Path.Combine(Path.GetTempPath(), "cx-persona-" + Guid.NewGuid().ToString("N"));
string? previousVault = Environment.GetEnvironmentVariable("BRAINX_VAULT");
Directory.CreateDirectory(vault);
Environment.SetEnvironmentVariable("BRAINX_VAULT", vault);
try
{
    var expected = new Dictionary<AiProviderType, string>
    {
        [AiProviderType.Anthropic] = "claude", [AiProviderType.ClaudeDev] = "claude",
        [AiProviderType.OpenAI] = "codex", [AiProviderType.OpenAIDev] = "codex",
        [AiProviderType.Gemini] = "gemini", [AiProviderType.GeminiDev] = "gemini",
        [AiProviderType.AntigravityDev] = "gemini", [AiProviderType.Local] = "mind",
        [AiProviderType.LlamaServer] = "mind", [AiProviderType.Ollama] = "mind",
        [AiProviderType.BrainX] = "cluadex",
    };
    foreach (var type in Enum.GetValues<AiProviderType>())
    {
        var p = AgentPersona.For(type);
        Check(p.Key == expected[type], type + " character mapping");
        foreach (bool thai in new[] { false, true })
        {
            string section = AgentPersona.Section(type, thai);
            Check(section.Contains(p.Name), type + " named in system instructions");
            Check(section.Contains(thai ? p.Thai : p.English), type + " description carried");
            Check(section.Contains(thai ? p.StyleThai : p.StyleEnglish), type + " style carried");
            Check(section.Contains("ค่ะ/คะ") && section.Contains("ฉัน"), type + " Thai female voice");
            Check(section.Contains(thai ? "ห้ามเลียนแบบตัวตนของผู้ตอบคนก่อน" : "previous speaker's identity"), type + " keeps current identity across history");
            Check(AgentPersona.ShortLine(type, thai).Contains(p.Name), type + " compact local voice");
            string measured = "brain: CONNECTED | server `brainx-brain` | 151 tools";
            string status = AgentPersona.MeasuredStatus(type, measured, thai);
            Check(status.EndsWith(measured, StringComparison.Ordinal), type + " status fact preserved verbatim");
            Check(status.Contains(p.Name), type + " status identifies current character");
        }
    }
    var grants = new ClaudeDevProvider.WorkerGrant?[] { null, new(false, false), new(true, false), new(false, true), new(true, true) };
    void CheckCli(string sentinel)
    {
        foreach (var grant in grants)
        {
            string cwd = @"C:\project";
            string claude = ClaudeDevProvider.BuildArgs(cwd, grant, "default", "high");
            string gemini = string.Join(" ", GeminiDevProvider.BuildArgs(cwd, grant, "default", null));
            string openai = OpenAIDevProvider.BuildPrompt(cwd, grant, null, "Previous speaker was Claude", "continue");
            string agy = AntigravityDevProvider.BuildPrompt(cwd, grant, null, "Previous speaker was Codex", "continue");
            foreach (var (type, prompt) in new[] { (AiProviderType.ClaudeDev, claude), (AiProviderType.GeminiDev, gemini),
                (AiProviderType.OpenAIDev, openai), (AiProviderType.AntigravityDev, agy) })
            {
                var p = AgentPersona.For(type);
                Check(prompt.Contains("# Who you are: " + p.Name), type + " sends identity for grant " + grant);
                Check(prompt.Contains(p.English) && prompt.Contains(p.StyleEnglish), type + " sends persona and style");
                Check(prompt.Contains("ค่ะ/คะ") && prompt.Contains("ฉัน"), type + " sends feminine Thai rules");
                Check(prompt.Contains("previous speaker's identity"), type + " resists old speaker identity");
                if (sentinel.Length > 0) Check(prompt.Contains(sentinel), type + " sends current cowork override");
            }
            // The character must never expand tool permissions.
            if (grant is null || grant is { Edit: false, Run: false })
            {
                Check(openai.Contains("not change it") && agy.Contains("read-only"), "read-only scope kept");
                var args = OpenAIDevProvider.BuildArgs(cwd, grant, "default", "high", null);
                Check(args[args.IndexOf("-s") + 1] == "read-only", "Codex sandbox unchanged");
            }
        }
    }
    CheckCli("");
    // Every model offered by the delegated providers follows its vendor's same persona route.
    foreach (var model in ClaudeDevProvider.KnownModels)
        foreach (var grant in grants)
            Check(ClaudeDevProvider.BuildArgs(@"C:\project", grant, model, "high").Contains("# Who you are: Claude"),
                "Claude Dev model identity: " + model);
    foreach (var model in GeminiDevProvider.KnownModels)
        foreach (var grant in grants)
            Check(string.Join(" ", GeminiDevProvider.BuildArgs(@"C:\project", grant, model, null)).Contains("# Who you are: Gemini"),
                "Gemini Dev model identity: " + model);
    foreach (var model in OpenAIDevProvider.KnownModels)
        foreach (var grant in grants)
        {
            var modelArgs = OpenAIDevProvider.BuildArgs(@"C:\project", grant, model, "high", null);
            Check(model == "default" ? !modelArgs.Contains("-m") : modelArgs[modelArgs.IndexOf("-m") + 1] == model,
                "OpenAI Dev model selection preserved: " + model);
            Check(OpenAIDevProvider.BuildPrompt(@"C:\project", grant, null, "", "hello").Contains("# Who you are: Codex"),
                "OpenAI Dev model identity: " + model);
        }
    foreach (var model in AntigravityDevProvider.KnownModels)
        foreach (var grant in grants)
            Check(AntigravityDevProvider.BuildPrompt(@"C:\project", grant, null, "", "hello").Contains("# Who you are: Gemini"),
                "Antigravity Dev model identity: " + model);
    // The real API payload builders retain the full persona, including on models with different request formats.
    var settings = new SettingsService();
    Check(settings.IsPortable, "API payload tests cannot touch the owner's settings");
    var turn = new List<NativeMessage> { new() { Role = "user", Content = { new() { Type = "text", Text = "hello" } } } };
    var chatBuilder = typeof(ApiProviderBase).GetMethod("BuildChatMessages", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    string ChatSystem(ApiProviderBase provider, string persona)
    {
        var messages = chatBuilder.Invoke(provider, new object[] { new List<ChatMessage>(), "hello", persona, "system", "user", "assistant" });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(messages));
        return json.RootElement[0].GetProperty("content").GetString()!;
    }
    foreach (var model in OpenAiProvider.KnownModels)
    {
        settings.Settings.ProviderConfigs["OpenAI"] = new() { SelectedModel = model };
        using var provider = new OpenAiProvider(settings);
        string persona = AgentPersona.Section(AiProviderType.OpenAI, false);
        Check((string)provider.BuildResponsesRequest(turn, persona, new(), null)["instructions"] == persona,
            "OpenAI Responses preserves persona: " + model);
        Check(ChatSystem(provider, persona) == persona, "OpenAI chat preserves persona: " + model);
    }
    var geminiChat = typeof(GeminiProvider).GetMethod("BuildGeminiRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    foreach (var model in GeminiProvider.KnownModels)
    {
        settings.Settings.ProviderConfigs["Gemini"] = new() { SelectedModel = model };
        using var provider = new GeminiProvider(settings);
        string persona = AgentPersona.Section(AiProviderType.Gemini, false);
        foreach (var request in new object[] { provider.BuildToolRequest(turn, persona, new(), null),
            geminiChat.Invoke(provider, new object[] { new List<ChatMessage>(), "hello", persona, model })! })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var root = json.RootElement;
            if (!root.TryGetProperty("systemInstruction", out var instruction)) instruction = root.GetProperty("system_instruction");
            Check(instruction.GetProperty("parts")[0].GetProperty("text").GetString() == persona,
                "Gemini chat/tool wire preserves persona: " + model);
        }
    }
    var anthSystem = typeof(AnthropicProvider).GetMethod("BuildSystem", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    foreach (var model in AnthropicProvider.KnownModels)
        foreach (bool cache in new[] { true, false })
        {
            settings.Settings.PromptCachingEnabled = cache;
            string persona = AgentPersona.Section(AiProviderType.Anthropic, false);
            var system = anthSystem.Invoke(null, new object[] { persona, settings.Settings, false });
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(system));
            var value = json.RootElement;
            Check((cache ? value[0].GetProperty("text").GetString() : value.GetString()) == persona,
                "Anthropic system preserves persona with caching on/off: " + model);
        }
    foreach (var (type, provider) in new (AiProviderType, ApiProviderBase)[] {
        (AiProviderType.Ollama, new OllamaProvider(settings)), (AiProviderType.LlamaServer, new LlamaServerProvider(settings)) })
    {
        using (provider)
        {
            string persona = AgentPersona.Section(type, true);
            Check(ChatSystem(provider, persona) == persona, type + " chat preserves persona");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(provider.PreviewToolMessages(turn, persona)));
            Check(json.RootElement[0].GetProperty("content").GetString() == persona, type + " native tools preserve persona");
        }
    }
    // Real file reload, without sleeping or changing the owner's configuration.
    string file = Path.Combine(vault, ".obsidianx", "agent-bus", "cowork", "personas.json");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    void WriteOverrides(string suffix)
    {
        var map = new Dictionary<string, object>();
        foreach (var key in new[] { "claude", "codex", "gemini", "mind", "cluadex" })
            map[key] = new { name = "Owner-" + key + suffix, persona = "OWNER_PERSONA_" + suffix, style = "OWNER_STYLE_" + suffix };
        File.WriteAllText(file, JsonSerializer.Serialize(map));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(suffix == "A" ? 1 : 2));
    }
    WriteOverrides("A"); CheckCli("OWNER_PERSONA_A");
    WriteOverrides("B"); CheckCli("OWNER_PERSONA_B");
    var originalWrite = File.GetLastWriteTimeUtc(file);
    File.WriteAllText(file, JsonSerializer.Serialize(new { codex = new { name = "Changed", persona = "CHANGED_LENGTH_SAME_TIMESTAMP" } }));
    File.SetLastWriteTimeUtc(file, originalWrite);
    Check(AgentPersona.For(AiProviderType.OpenAI).Name == "Changed", "changed length reloads even when timestamp is preserved");
    WriteOverrides("B");
    foreach (var type in Enum.GetValues<AiProviderType>())
        Check(AgentPersona.Section(type, true).Contains("OWNER_PERSONA_B"), type + " latest override in native/local prompt");
    File.WriteAllText(file, "invalid json"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(3));
    Check(AgentPersona.For(AiProviderType.OpenAI).Name == "Codex", "invalid override falls back safely");
    File.Delete(file);
    Check(!AgentPersona.Section(AiProviderType.OpenAI, false).Contains("OWNER_PERSONA"), "deleted override removes stale character");
    var room = BrainXRoomProvider.BuildRequest("@codex help", "topic", new());
    Check(room.Contains("OWN current cowork-room persona") && room.Contains("personas.json"), "room retains each responder's own character");
    Console.WriteLine($"PASS: {checks} checks; all {expected.Count} provider routes, every CLI grant mode, live room overrides, status facts and sandbox boundaries.");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("BRAINX_VAULT", previousVault);
    // Only the freshly created test directory is removed.
    Directory.Delete(vault, true);
}
