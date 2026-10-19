// AI comments (owner 2026-10-19: "เพิ่มออฟชั่นในหน้า editor ได้ว่าจะให้ ai ใส่คอมเม้นอธิบายโค๊ดด้วยไหม ระดับไหน … ใส่ภาษาอะไร").
// CodeCommentPolicy turns the Code Editor's choice into one instruction; this proves every level × language builds the
// right text, "none" builds nothing (the old prompt, byte for byte), a project's own choice wins over the global one,
// and that the text actually leaves the app on every provider's wire: Anthropic (inside the cached system block, the
// same bytes every turn), OpenAI (chat + Responses), Gemini (chat + tools), Ollama, llama-server, and the Claude Dev /
// Gemini Dev CLI wrappers. CodeAgentService.GetSystemPrompt itself (both tool loops) is proven in seamtest C1.
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class CommentPolicyWire
{
    static readonly string[] Levels = { "none", "key", "detailed" };
    static readonly string[] Languages = { "th", "en", "match" };

    // What each choice must say — and what the others must not.
    static string LevelMark(string level) => level == "detailed" ? "Level DETAILED" : "Level KEY POINTS ONLY";
    static string LanguageMark(string language) => language switch
    {
        "th" => "Write the comments in Thai",
        "en" => "Write the comments in English.",
        _ => "the language the file's existing comments already use",
    };

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · AI comments (Code Editor) — the instruction and every provider's wire");

        // ── the text, for each level × language ──
        foreach (var level in Levels)
            foreach (var language in Languages)
            {
                string text = CodeCommentPolicy.Build(level, language);
                if (level == "none")
                {
                    check("comments", $"{level}/{language}: no instruction at all", text.Length == 0, text);
                    continue;
                }
                bool levelOk = text.Contains(LevelMark(level))
                               && !text.Contains(LevelMark(level == "key" ? "detailed" : "key"));
                bool langOk = text.Contains(LanguageMark(language))
                              && Languages.Where(l => l != language).All(l => !text.Contains(LanguageMark(l)));
                bool limits = text.StartsWith("# Code comments")
                              && text.Contains("Only the code you add or change in this task gets comments")
                              && text.Contains("never rewrite, translate, reword or delete someone else's comment")
                              && text.Contains("JSON (.jsonc / .json5 can)")
                              && text.Contains("never change behaviour");
                check("comments", $"{level}/{language}: level, language and the three limits (only touched code · keep others' comments · no JSON)",
                    levelOk && langOk && limits, text);
                check("comments", $"{level}/{language}: deterministic — same bytes every call, \\n line ends on every OS (the prompt cache holds)",
                    text == CodeCommentPolicy.Build(level, language) && !text.Contains('\r'), "");
            }
        check("comments", "unknown / blank values fall back to the defaults (none, match)",
            CodeCommentPolicy.Build("verbose", "fr") == "" && CodeCommentPolicy.Build(null, null) == ""
            && CodeCommentPolicy.Build(" KEY ", "xx").Contains(LanguageMark("match")) && CodeCommentPolicy.Build("Detailed", "TH").Contains(LanguageMark("th")),
            "");
        check("comments", "a fresh AppSettings sends nothing (the default never changes the old behaviour)",
            CodeCommentPolicy.InstructionFor(new AppSettings(), @"C:\work\demo") == "", "");

        // ── global vs this project's own ──
        var s = new AppSettings { CodeCommentLevel = "key", CodeCommentLanguage = "th" };
        s.CodeCommentProjects[CodeCommentPolicy.ProjectKey(@"C:\Work\Demo\")] = new CodeCommentSetting { Level = "detailed", Language = "en" };
        var mine = CodeCommentPolicy.Resolve(s, @"c:\work\demo");
        var other = CodeCommentPolicy.Resolve(s, @"C:\work\other");
        var none = CodeCommentPolicy.Resolve(s, null);
        check("comments", "a project's own choice wins there (path case / trailing slash ignored); other projects and no project use the global",
            mine == ("detailed", "en", true) && other == ("key", "th", false) && none == ("key", "th", false),
            $"mine={mine} other={other} none={none}");
        s.CodeCommentProjects[CodeCommentPolicy.ProjectKey(@"C:\work\off")] = new CodeCommentSetting { Level = "none", Language = "th" };
        check("comments", "a project can switch it off while the global is on",
            CodeCommentPolicy.InstructionFor(s, @"C:\work\off") == "" && CodeCommentPolicy.InstructionFor(s, @"C:\work\other").Contains(LevelMark("key")), "");

        string policy = CodeCommentPolicy.Build("detailed", "th");
        string sys = "BASE-PROMPT\n\n" + policy;

        // ── Claude Dev: --append-system-prompt, one line (a .cmd shim ends an argument at a line break) ──
        foreach (var grant in new ClaudeDevProvider.WorkerGrant?[] { null, new(true, true) })
        {
            string kind = grant == null ? "advice" : "worker";
            string plain = ClaudeDevProvider.BuildArgs(@"D:\Game", grant, "", "auto");
            string withNone = ClaudeDevProvider.BuildArgs(@"D:\Game", grant, "", "auto", codeComments: "");
            string withPolicy = ClaudeDevProvider.BuildArgs(@"D:\Game", grant, "", "auto", codeComments: policy);
            string wrapper = Between(withPolicy, "--append-system-prompt \"", "\" --allowedTools");
            check("comments", $"Claude Dev {kind}: off = the arguments from before the option, byte for byte", plain == withNone, "");
            check("comments", $"Claude Dev {kind}: the instruction rides in --append-system-prompt, on one line",
                wrapper.Contains(LevelMark("detailed")) && wrapper.Contains("Write the comments in Thai")
                && wrapper.Contains("JSON (.jsonc / .json5 can)") && !wrapper.Contains('\n'), Trunc(wrapper, 300));
        }

        // ── Gemini Dev: the -p wrapper (ArgumentList keeps the line breaks) ──
        foreach (var grant in new ClaudeDevProvider.WorkerGrant?[] { null, new(true, false) })
        {
            string kind = grant == null ? "advice" : "editor";
            var plain = GeminiDevProvider.BuildArgs(@"C:\work\demo", grant, "default", null);
            var withPolicy = GeminiDevProvider.BuildArgs(@"C:\work\demo", grant, "default", null, policy);
            check("comments", $"Gemini Dev {kind}: off = the arguments from before the option",
                plain.SequenceEqual(GeminiDevProvider.BuildArgs(@"C:\work\demo", grant, "default", null, "")), "");
            check("comments", $"Gemini Dev {kind}: the instruction ends the -p prompt",
                withPolicy[^2] == "-p" && withPolicy[^1].EndsWith(policy) && withPolicy[^1].StartsWith(plain[^1]), Trunc(withPolicy[^1], 200));
        }

        // ── the API providers: the system prompt that carries the instruction reaches the wire as it is ──
        using var mock = new WireMock();
        mock.Responder = (_, _, _) => (400, "application/json", "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"mock\"}}");
        var svc = new SettingsService();   // portable — never the user's settings
        var set = svc.Settings;
        set.MaxTokens = 4096;
        set.ExtendedThinkingEnabled = false;
        set.ReasoningEffort = "auto";
        set.PromptCachingEnabled = true;
        set.LocalNativeToolUseEnabled = true;
        set.ContextSize = 32768;
        set.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = ImageWireGolden.FakeKey, BaseUrl = mock.Root, SelectedModel = "claude-opus-5-5" };
        set.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = mock.Root + "/v1", SelectedModel = "gpt-6-sol" };
        set.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-test", BaseUrl = mock.Root, SelectedModel = "gemini-3.8-flash" };
        set.ProviderConfigs["Ollama"] = new ProviderConfig { BaseUrl = mock.Root, SelectedModel = "qwen-coder" };

        // Anthropic: the whole system prompt is one cached block; two turns must send the same bytes for it.
        var anth = new AnthropicProvider(svc);
        await Quietly(() => anth.ChatWithToolsAsync(Turn(), sys, ImageWireGolden.Tools()));
        await Quietly(() => anth.ChatWithToolsAsync(Turn(), sys, ImageWireGolden.Tools()));
        var bodies = mock.Bodies("/v1/messages");
        string sys1 = bodies.Count >= 2 ? SystemOf(bodies[^2]) : "", sys2 = bodies.Count >= 2 ? SystemOf(bodies[^1]) : "";
        check("comments", "Anthropic tool loop: the instruction is inside the system block that carries cache_control",
            SystemText(bodies.Count > 0 ? bodies[^1] : "") == sys && sys2.Contains("\"cache_control\""), Trunc(sys2, 200));
        check("comments", "Anthropic: the cached system block is byte-identical on the next turn (cache read, not a rewrite)",
            sys1.Length > 0 && sys1 == sys2, $"{sys1.Length} vs {sys2.Length}");
        await Quietly(() => Drain(anth.ChatAsync(new List<ChatMessage>(), "ping", sys)));
        check("comments", "Anthropic chat: the instruction is in the system prompt",
            SystemText(mock.Last("/v1/messages")) == sys, Trunc(SystemOf(mock.Last("/v1/messages")), 200));

        // OpenAI: Responses "instructions" (tool loop) and chat completions' system message.
        var oa = new OpenAiProvider(svc);
        await Quietly(() => oa.ChatWithToolsAsync(Turn(), sys, ImageWireGolden.Tools()));
        check("comments", "OpenAI Responses: instructions = the system prompt with the instruction",
            Str(mock.Last("/v1/responses"), "instructions") == sys, Trunc(mock.Last("/v1/responses"), 200));
        set.ProviderConfigs["OpenAI"].SelectedModel = "gpt-5.5";
        await Quietly(() => Drain(new OpenAiProvider(svc).ChatAsync(new List<ChatMessage>(), "ping", sys)));
        check("comments", "OpenAI chat: the system message carries the instruction",
            FirstSystem(mock.Last("/v1/chat/completions")) == sys, Trunc(mock.Last("/v1/chat/completions"), 200));

        // Gemini: system_instruction / systemInstruction.
        var gm = new GeminiProvider(svc);
        await Quietly(() => gm.ChatWithToolsAsync(Turn(), sys, ImageWireGolden.Tools()));
        string gTools = mock.Last(":streamGenerateContent");
        await Quietly(() => Drain(new GeminiProvider(svc).ChatAsync(new List<ChatMessage>(), "ping", sys)));
        string gChat = mock.Last(":streamGenerateContent");
        check("comments", "Gemini tools + chat: the system instruction carries it",
            SystemInstruction(gTools).Contains(policy) && SystemInstruction(gChat).Contains(policy), Trunc(gTools, 200));

        // Ollama: /api/chat's system message (tool loop and chat).
        await Quietly(() => new OllamaProvider(svc).ChatWithToolsAsync(Turn(), sys, ImageWireGolden.Tools()));
        string olTools = mock.Last("/api/chat");
        await Quietly(() => Drain(new OllamaProvider(svc).ChatAsync(new List<ChatMessage>(), "ping", sys)));
        string olChat = mock.Last("/api/chat");
        check("comments", "Ollama tools + chat: the first message is the system prompt with the instruction",
            FirstSystem(olTools) == sys && FirstSystem(olChat) == sys, Trunc(olTools, 200));

        // llama-server (and in-process GGUF through it): the messages it would send.
        var lmsgs = new LlamaServerProvider(svc).PreviewToolMessages(Turn(), sys);
        check("comments", "llama-server: the system message is the prompt with the instruction",
            FirstSystem(JsonSerializer.Serialize(new { messages = lmsgs })) == sys, "");
    }

    static List<NativeMessage> Turn() => new()
    {
        new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "text", Text = "add a helper to utils.cs" } } },
    };

    static async Task Quietly(Func<Task> call)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await call().WaitAsync(cts.Token); } catch { /* the mock refuses on purpose: only the request matters */ }
    }

    static async Task Drain(IAsyncEnumerable<string> stream) { await foreach (var _ in stream) { } }

    static string SystemOf(string body)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.TryGetProperty("system", out var x) ? x.GetRawText() : ""; }
        catch { return ""; }
    }

    static string SystemInstruction(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            if (!r.TryGetProperty("systemInstruction", out var si) && !r.TryGetProperty("system_instruction", out si)) return "";
            return string.Concat(si.GetProperty("parts").EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
        }
        catch { return ""; }
    }

    static string FirstSystem(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var m = d.RootElement.GetProperty("messages")[0];
            if (m.GetProperty("role").GetString() != "system") return "";
            var c = m.GetProperty("content");
            return c.ValueKind == JsonValueKind.String ? c.GetString() ?? ""
                : string.Concat(c.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
        }
        catch { return ""; }
    }

    static string Str(string body, string name)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.TryGetProperty(name, out var x) ? x.GetString() ?? "" : ""; }
        catch { return ""; }
    }

    /// <summary>Anthropic's "system", decoded: a plain string, or the text of its blocks.</summary>
    static string SystemText(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (!d.RootElement.TryGetProperty("system", out var x)) return "";
            return x.ValueKind == JsonValueKind.String ? x.GetString() ?? ""
                : string.Concat(x.EnumerateArray().Select(b => b.TryGetProperty("text", out var t) ? t.GetString() : ""));
        }
        catch { return ""; }
    }

    static string Between(string s, string start, string end)
    {
        int i = s.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return "";
        i += start.Length;
        int j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }

    static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";
}
