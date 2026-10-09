using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

/// <summary>
/// Contract test for the Anthropic provider, in two parts:
///
///   offline (free, always runs) — the per-model request rules (<see cref="AnthropicProvider.Traits"/>),
///     the error explanations, and the assistant-turn rebuild that carries thinking blocks.
///   live (costs a little) — the exact request shapes CluadeX sends, against the real API, for every
///     model in the dropdown: Test Connection, plain chat, native tool_use with and without thinking,
///     a full tool round trip, forced and suppressed tool_choice, a round trip after the history was
///     edited (what microcompact does), and the live model listing.
///
/// Every model generation changes which parameters the API accepts, and a wrong shape is an HTTP 400 the
/// user sees as "the key doesn't work". Run this whenever <see cref="AnthropicProvider.KnownModels"/> or
/// the request building changes.
///
/// Key: ANTHROPIC_API_KEY, else READ (never written) from ~/.cluadex/settings.json.
/// Workspace: ANTHROPIC_WORKSPACE_ID, else the Workspace ID saved in Settings (keys that aren't scoped
/// to one workspace need it).
///
///   dotnet run                          — offline + every model in AnthropicProvider.KnownModels
///   dotnet run -- claude-opus-5-5,...   — offline + only the listed models
///   dotnet run -- --offline             — offline only
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly StringBuilder Report = new();
    static readonly List<string> Problems = new();

    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static void Check(string scope, string name, bool ok, string detail = "")
    {
        string line = $"  {(ok ? "PASS" : "FAIL")} {name,-30} {Trunc(detail, 300).Replace("\n", " ")}";
        Console.WriteLine(line);
        Report.AppendLine(line);
        if (ok) Pass++;
        else { Fail++; Problems.Add($"{scope} · {name} · {Trunc(detail, 300).Replace("\n", " ")}"); }
    }

    static void Section(string title)
    {
        Console.WriteLine($"\n== {title}");
        Report.AppendLine($"\n== {title}");
    }

    static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");

    /// <summary>The Anthropic entry of the user's real settings: (api key, workspace id). Read-only.</summary>
    static (string Key, string Workspace) ReadUserAnthropicConfig()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "settings.json");
        if (!File.Exists(path)) return (null, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("ProviderConfigs", out var cfgs) || !cfgs.TryGetProperty("Anthropic", out var a))
            return (null, null);
        string ws = a.TryGetProperty("WorkspaceId", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        if (!a.TryGetProperty("ApiKey", out var k) || k.ValueKind != JsonValueKind.String) return (null, ws);
        string stored = k.GetString();
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("ENC:")) return (stored, ws);
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[4..]), null, DataProtectionScope.CurrentUser);
        return (Encoding.UTF8.GetString(plain), ws);
    }

    static readonly List<ToolSchema> EchoTool =
    [
        new ToolSchema
        {
            Name = "echo",
            Description = "Echo a word back to the user.",
            InputSchema = JsonDocument.Parse(
                """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone(),
        },
    ];

    static List<NativeMessage> Ask(string text) =>
        [new NativeMessage { Role = "user", Content = [new ContentBlock { Type = "text", Text = text }] }];

    static NativeMessage ToolResults(NativeToolResponse r) => new()
    {
        Role = "user",
        Content = r.ToolCalls.Select(c => new ContentBlock { Type = "tool_result", ToolUseId = c.Id, Content = "ping" }).ToList(),
    };

    static async Task<int> Run(string[] args)
    {
        var loc = new LocalizationService();
        loc.SetLanguage("en");
        LocalizedResources.Instance.Initialize(loc);

        await Offline();

        // --brain: connect BrainX exactly the way the app does (the real McpServerManager + the user's own
        // mcp_servers.json, copied into the portable data folder) and run real searches through it.
        if (args.Contains("--brain"))
        {
            Section("BrainX through CluadeX's own MCP client");
            var svc = new SettingsService();
            Directory.CreateDirectory(svc.DataRoot);
            string userCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "mcp_servers.json");
            File.Copy(userCfg, Path.Combine(svc.DataRoot, "mcp_servers.json"), overwrite: true);
            using var mcp = new CluadeX.Services.Mcp.McpServerManager(svc);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await mcp.InitializeAsync();
            string brain = mcp.BrainServerName;
            Check("brain", "connects and lists its tools", mcp.IsBrainConnected && mcp.BrainToolCount > 0,
                $"server={brain} tools={mcp.BrainToolCount} in {sw.ElapsedMilliseconds} ms · {mcp.BrainStatusText}");
            if (mcp.IsBrainConnected)
            {
                var names = mcp.ToolRegistry.GetAllTools().Where(t => t.ServerName == brain).Select(t => t.Name).ToHashSet();
                foreach (var need in new[] { "brain_search", "brain_recall", "brain_semantic_search", "brain_get_note", "brain_create_note", "brain_remember" })
                    Check("brain", $"tool offered: {need}", names.Contains(need), "");
                // What the instinct sync sends must match what brain_create_note declares.
                var create = mcp.ToolRegistry.GetAllTools().First(t => t.ServerName == brain && t.Name == "brain_create_note");
                string tagsSchema = create.InputSchema is { } cs && cs.TryGetProperty("properties", out var props)
                    && props.TryGetProperty("tags", out var tg) ? tg.GetRawText() : "(none)";
                Console.WriteLine($"  info brain_create_note.tags schema: {Trunc(tagsSchema, 300)}");
                foreach (var tn in new[] { "brain_create_note", "brain_append_note" })
                {
                    var tdef = mcp.ToolRegistry.GetAllTools().FirstOrDefault(t => t.ServerName == brain && t.Name == tn);
                    if (tdef?.InputSchema is { } sch)
                        Console.WriteLine($"  info {tn} schema: {Trunc(sch.GetRawText(), 900)}");
                }
                // The agent dispatch path flattens arguments to strings; a numeric-looking query must stay a string.
                var numericQuery = await mcp.CallToolAsync(brain, "brain_search", new Dictionary<string, string> { ["query"] = "2026" });
                Check("brain", "query \"2026\" goes out as a string (agent dispatch path)", !numericQuery.IsError,
                    Trunc(numericQuery.GetTextOutput().Replace('\n', ' '), 200));
                foreach (var (tool, argsObj) in new (string, Dictionary<string, object>)[]
                {
                    ("brain_search", new() { ["query"] = "CluadeX workspace API key" }),
                    ("brain_recall", new() { ["query"] = "CluadeX settings save loop" }),
                    ("brain_semantic_search", new() { ["query"] = "how does CluadeX connect to Anthropic" }),
                })
                {
                    sw.Restart();
                    var res = await mcp.CallToolWithObjectArgsAsync(brain, tool, argsObj);
                    string text = res.GetTextOutput();
                    Check("brain", $"{tool} returns results", !res.IsError && text.Length > 50,
                        $"{sw.ElapsedMilliseconds} ms · {Trunc(text.Replace('\n', ' '), 220)}");
                }
            }
            return Finish();
        }

        // --db-health <codex.db>: open a COPY of a chat database through the real DatabaseService and prove the
        // automatic cleanup refuses to delete anything when the file fails its integrity check.
        int dbi = Array.IndexOf(args, "--db-health");
        if (dbi >= 0 && dbi + 1 < args.Length)
        {
            Section("chat DB health gate (copy of " + Path.GetFileName(args[dbi + 1]) + ")");
            var svc = new SettingsService();   // portable → <exe>\Data
            string target = Path.Combine(svc.DataRoot, "codex.db");
            Directory.CreateDirectory(svc.DataRoot);
            foreach (var ext in new[] { "", "-wal", "-shm" }) { var f = target + ext; if (File.Exists(f)) File.Delete(f); }
            File.Copy(args[dbi + 1], target);
            SQLitePCL.Batteries.Init();
            var db = new DatabaseService(svc);   // its schema init writes, exactly as the app does on startup
            string before = HashUnlocked(target);
            bool healthy = db.IsHealthy;
            int removed = db.DeleteEmptySessions();
            bool keeps = db.SessionHasMessages("no-such-session");
            string after = HashUnlocked(target);
            if (!healthy)
            {
                Check("db", "damaged file is reported unhealthy", true, "IsHealthy=False");
                Check("db", "cleanup deletes nothing on it", removed == 0, $"removed={removed}");
                Check("db", "every session counts as having messages", keeps, "");
                Check("db", "file bytes untouched by the checks", before == after, $"{before} → {after}");
            }
            else
            {
                // Healthy file: count independently (plain SQL) what the cleanup should remove, on a second copy.
                string probe = target + ".probe";
                File.Copy(args[dbi + 1], probe, overwrite: true);
                long Q(string sql) { using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={probe};Mode=ReadOnly"); c.Open();
                    using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar(); }
                long empty = Q("SELECT COUNT(*) FROM sessions s WHERE NOT EXISTS (SELECT 1 FROM messages m WHERE m.session_id = s.id)");
                long msgs = Q("SELECT COUNT(*) FROM messages");
                long withMsgs = Q("SELECT COUNT(DISTINCT session_id) FROM messages");
                long Q2(string sql) { using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Mode=ReadOnly"); c.Open();
                    using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar(); }
                Check("db", "healthy file: removes exactly the empty sessions", removed == empty, $"removed={removed}, empty={empty}");
                Check("db", "healthy file: every message kept", Q2("SELECT COUNT(*) FROM messages") == msgs, $"{msgs} messages");
                Check("db", "healthy file: every session with messages kept", Q2("SELECT COUNT(*) FROM sessions") == withMsgs, $"{withMsgs} sessions");
            }
            return Finish();
        }
        if (args.Contains("--offline")) return Finish();

        var (cfgKey, cfgWs) = ReadUserAnthropicConfig();
        string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string keySource = "ANTHROPIC_API_KEY";
        if (string.IsNullOrWhiteSpace(key)) { key = cfgKey; keySource = "~/.cluadex/settings.json"; }
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("\nSKIP live — no Anthropic key (set ANTHROPIC_API_KEY or save one in CluadeX Settings).");
            return Finish();
        }
        string ws = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID") is { Length: > 0 } e ? e : cfgWs;

        string[] models = args.Length > 0 && !args[0].StartsWith("--")
            ? args[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : AnthropicProvider.KnownModels;

        var settingsService = new SettingsService();   // portable.txt → <exe>\Data, never ~/.cluadex
        var s = settingsService.Settings;
        // The user's real sampling values — non-default temperature is exactly what newer models reject.
        s.Temperature = 0.6f; s.TopP = 0.9f;
        s.MaxTokens = 1024;
        s.ThinkingBudgetTokens = 1024;
        s.PromptCachingEnabled = true;

        Console.WriteLine($"\nkey from {keySource} · workspace {(string.IsNullOrEmpty(ws) ? "(none)" : ws)} · {models.Length} model(s)");
        const string sys = "You are a terse test assistant.";

        Section("model listing");
        s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = key, WorkspaceId = ws, SelectedModel = AnthropicProvider.DefaultModel };
        var listed = await new AnthropicProvider(settingsService).ListModelsAsync();
        Check("listing", "list-models", listed is { Count: > 0 },
            listed == null ? "listing failed" : string.Join(", ", listed.Take(14)));

        foreach (string model in models)
        {
            Section(model);
            s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = key, WorkspaceId = ws, SelectedModel = model };
            var p = new AnthropicProvider(settingsService);
            string lastError = null;
            p.OnError += err => lastError = err;

            // 1. Settings ▸ Test Connection
            var (ok, msg) = await p.TestConnectionAsync();
            Check(model, "test-connection", ok, msg);
            if (!ok)
            {
                // An account-level failure fails every model the same way — say it once and stop paying.
                bool accountLevel = msg.Contains("HTTP 401") || msg.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                                    || msg.Contains("credit", StringComparison.OrdinalIgnoreCase);
                if (accountLevel) { Console.WriteLine("  (account-level failure — skipping the remaining models)"); break; }
                continue;
            }

            foreach (bool thinking in new[] { false, true })
            {
                s.ExtendedThinkingEnabled = thinking;
                string tag = thinking ? "+think" : "";

                // 2. Plain streaming chat (ChatAsync)
                lastError = null;
                try
                {
                    var sb = new StringBuilder();
                    await foreach (var t in p.ChatAsync([], "Reply with the single word: pong", sys))
                        if (!t.StartsWith("<thinking>")) sb.Append(t);
                    Check(model, "chat" + tag, lastError == null && sb.Length > 0, lastError ?? sb.ToString().Trim());
                }
                catch (Exception ex) { Check(model, "chat" + tag, false, ex.Message); }

                // 3. Native tool_use + full round trip, the assistant turn rebuilt exactly as the agent loop does
                try
                {
                    var msgs = Ask("Call the echo tool with text \"ping\". Do not answer in prose.");
                    var r1 = await p.ChatWithToolsAsync(msgs, sys, EchoTool);
                    bool called = r1.ToolCalls.Count > 0;
                    int thinkingBlocks = r1.Blocks.Count(b => b.Type is "thinking" or "redacted_thinking");
                    Check(model, "tools" + tag, called,
                        called ? $"{r1.ToolCalls[0].Name}({r1.ToolCalls[0].Input.GetRawText()}) · {thinkingBlocks} thinking block(s)"
                               : $"no call: {r1.TextContent}");
                    if (called)
                    {
                        var turn = r1.ToAssistantMessage(includeToolUse: false);
                        foreach (var c in r1.ToolCalls)
                            turn.Content.Add(new ContentBlock { Type = "tool_use", Id = c.Id, Name = c.Name, Input = c.Input });
                        msgs.Add(turn);
                        msgs.Add(ToolResults(r1));
                        var r2 = await p.ChatWithToolsAsync(msgs, sys, EchoTool);
                        Check(model, "tools-roundtrip" + tag, true, $"stop={r2.StopReason} text={r2.TextContent}");

                        // 4. Same continuation after the earliest turn was edited — what microcompact does to
                        //    old turns. Thinking blocks are bound to the history; this must not be a 400.
                        if (thinking)
                        {
                            msgs[0] = Ask("Call the echo tool with text \"ping\". (edited)")[0];
                            var r3 = await p.ChatWithToolsAsync(msgs, sys, EchoTool);
                            Check(model, "edited-history" + tag, true, $"stop={r3.StopReason}");
                        }
                    }
                }
                catch (Exception ex) { Check(model, "tools" + tag, false, ex.Message); }

                // 4b. Explicit effort levels (the chat-bar picker): every level must be accepted or rounded down
                if (!thinking)
                {
                    foreach (string pick in new[] { "low", "max" })
                    {
                        s.ReasoningEffort = pick;
                        lastError = null;
                        try
                        {
                            var sb = new StringBuilder();
                            await foreach (var t in p.ChatAsync([], "Reply with the single word: pong", sys))
                                if (!t.StartsWith("<thinking>")) sb.Append(t);
                            Check(model, "effort=" + pick, lastError == null && sb.Length > 0,
                                (lastError ?? sb.ToString().Trim()) + $" (sent {AnthropicProvider.EffortFor(AnthropicProvider.Traits(model), pick, AnthropicProvider.Traits(model).ExplicitDisable) ?? "none"})");
                        }
                        catch (Exception ex) { Check(model, "effort=" + pick, false, ex.Message); }
                    }
                    s.ReasoningEffort = "auto";
                }

                // 5. Forced tool_choice (plan-first / forced-recovery path) — degrades to auto where forcing is a 400
                try
                {
                    var r = await p.ChatWithToolsAsync(Ask("Echo the word ping."), sys, EchoTool, toolChoice: "required");
                    Check(model, "forced-tool" + tag, r.ToolCalls.Count > 0, r.ToolCalls.Count > 0 ? "called" : $"no call: {r.TextContent}");
                }
                catch (Exception ex) { Check(model, "forced-tool" + tag, false, ex.Message); }

                // 6. tool_choice "none" (anti-wander wrap-up: must answer in prose)
                if (!thinking)
                {
                    try
                    {
                        var r = await p.ChatWithToolsAsync(Ask("Call the echo tool with text \"ping\"."), sys, EchoTool, toolChoice: "none");
                        Check(model, "no-tool", r.ToolCalls.Count == 0, r.ToolCalls.Count == 0 ? "prose" : "called a tool anyway");
                    }
                    catch (Exception ex) { Check(model, "no-tool", false, ex.Message); }
                }
            }
        }

        return Finish();
    }

    /// <summary>Free checks — no network.</summary>
    static async Task Offline()
    {
        Section("offline · per-model request rules");
        // (model, adaptive, alwaysThinks, explicitDisable, sampling, omitsThinkingText, forcedToolChoice)
        var expect = new (string, bool, bool, bool, bool, bool, bool)[]
        {
            ("claude-fable-5-1",           true,  true,  false, false, true,  false),
            ("claude-fable-5",             true,  true,  false, false, true,  true),
            ("claude-mythos-5-1",          true,  true,  false, false, true,  false),
            ("claude-opus-5-5",            true,  true,  false, false, true,  false),
            ("claude-opus-5",              true,  true,  false, false, true,  true),
            ("claude-opus-4-8",            true,  false, true,  false, true,  true),
            ("claude-opus-4-7",            true,  false, true,  false, true,  true),
            ("claude-opus-4-6",            true,  false, false, true,  false, true),
            ("claude-sonnet-5-5",          true,  true,  false, false, true,  false),
            ("claude-sonnet-5",            true,  false, true,  false, true,  true),
            ("claude-sonnet-4-6",          true,  false, false, true,  false, true),
            ("claude-haiku-5-5",           true,  true,  false, false, true,  true),
            ("claude-haiku-4-5",           false, false, false, true,  false, true),
            ("claude-sonnet-4-5-20250929", false, false, false, true,  false, true),
            ("claude-sonnet-4-20250514",   false, false, false, true,  false, true),   // a date is not a minor version
            ("claude-opus-6",              true,  true,  false, false, true,  false),  // a future model gets the newest rules
            ("my-proxy-model",             false, false, false, true,  false, true),   // unknown → most permissive
        };
        foreach (var (id, ad, at, ed, sa, om, fo) in expect)
        {
            var t = AnthropicProvider.Traits(id);
            bool ok = t.Adaptive == ad && t.AlwaysThinks == at && t.ExplicitDisable == ed
                      && t.Sampling == sa && t.OmitsThinkingText == om && t.ForcedToolChoice == fo;
            Check("traits", id, ok, ok ? "" : $"got {t}");
        }

        Section("offline · error explanations");
        var cfg = new ProviderConfig { ApiKey = "sk-ant-secret-123" };
        string Err(int status, string message) =>
            AnthropicProvider.DescribeApiError(status,
                JsonSerializer.Serialize(new { type = "error", error = new { type = "x", message } }), cfg, "claude-opus-5-5");

        string ws = Err(400, "This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use. Add the header, or use an API key that is scoped to a workspace.");
        Check("errors", "workspace-required → how to fix", ws.Contains("Workspace ID") && ws.Contains("Settings → Workspaces"), ws);
        string ws2 = Err(400, "anthropic-workspace-id is required when authenticating with an identity-linked API key; send the id of the workspace this request acts in.");
        Check("errors", "identity-linked key wording", ws2.Contains("Workspace ID field"), ws2);
        string bad = Err(404, "Workspace `wrkspc_nope` not found.");
        Check("errors", "unknown workspace", bad.Contains("isn't valid"), bad);
        string model = Err(404, "model: claude-opus-5-5");
        Check("errors", "model not available", model.Contains("isn't available to this key"), model);
        string leak = AnthropicProvider.DescribeApiError(401, "{\"error\":{\"message\":\"invalid x-api-key sk-ant-secret-123\"}}", cfg, "m");
        Check("errors", "key never echoed", !leak.Contains("sk-ant-secret-123") && leak.Contains("rejected"), leak);
        // The agent loop treats a 400 mentioning "context"/"too long" as prompt overflow and compacts.
        foreach (var k in new[] { "anthropic.err.workspaceRequired", "anthropic.err.workspaceInvalid", "anthropic.err.model", "anthropic.err.credit" })
        {
            string en = LocalizedResources.Instance[k];
            Check("errors", $"{k} avoids overflow words", !en.Contains("context", StringComparison.OrdinalIgnoreCase)
                                                          && !en.Contains("too long", StringComparison.OrdinalIgnoreCase), en);
        }

        Section("offline · workspace ID input");
        var probe = new SettingsService();   // portable — never the user's settings
        probe.Settings.ProviderConfigs["Anthropic"] = new ProviderConfig
            { ApiKey = "sk-ant-test", WorkspaceId = "wrkspc_01ทดสอบ", SelectedModel = "claude-opus-5-5" };
        var (wsOk, wsMsg) = await new AnthropicProvider(probe).TestConnectionAsync();   // fails before any network call
        Check("workspace", "non-ASCII ID → explained, not a raw FormatException",
            !wsOk && wsMsg.StartsWith("That Workspace ID isn't valid"), wsMsg);

        Section("offline · effort per model");
        var t55 = AnthropicProvider.Traits("claude-opus-5-5");
        var t46 = AnthropicProvider.Traits("claude-opus-4-6");
        var t45 = AnthropicProvider.Traits("claude-opus-4-5");
        var t48 = AnthropicProvider.Traits("claude-opus-4-8");
        var h45 = AnthropicProvider.Traits("claude-haiku-4-5");
        void Eff(string name, string got, string want) => Check("effort", name, got == want, $"got {got ?? "(omit)"}, want {want ?? "(omit)"}");
        Eff("Claude Opus 5.5 xhigh", AnthropicProvider.EffortFor(t55, "xhigh"), "xhigh");
        Eff("Claude Opus 4.6 xhigh → high", AnthropicProvider.EffortFor(t46, "xhigh"), "high");
        Eff("Claude Opus 4.6 max", AnthropicProvider.EffortFor(t46, "max"), "max");
        Eff("Claude Opus 4.5 max → high", AnthropicProvider.EffortFor(t45, "max"), "high");
        Eff("Claude Haiku 4.5 has no effort", AnthropicProvider.EffortFor(h45, "high"), null);
        Eff("Claude auto → omitted", AnthropicProvider.EffortFor(t55, "auto"), null);
        Eff("Claude Opus 4.8 thinking off caps at high", AnthropicProvider.EffortFor(t48, "max", capAtHigh: true), "high");
        Eff("OpenAI gpt-6-astra max", OpenAiProvider.EffortFor("gpt-6-astra", "max", false), "max");
        Eff("OpenAI gpt-5.5 max → xhigh", OpenAiProvider.EffortFor("gpt-5.5", "max", false), "xhigh");
        Eff("OpenAI gpt-5 xhigh → high", OpenAiProvider.EffortFor("gpt-5", "xhigh", false), "high");
        Eff("OpenAI proxy id openai/gpt-6-sol", OpenAiProvider.EffortFor("openai/gpt-6-sol", "low", false), "low");
        Eff("OpenAI gpt-4o not a reasoning model", OpenAiProvider.EffortFor("gpt-4o", "high", true), null);
        Eff("OpenAI auto + Think on → high", OpenAiProvider.EffortFor("gpt-6-sol", "auto", true), "high");
        Eff("OpenAI auto + Think off → omitted", OpenAiProvider.EffortFor("gpt-6-sol", "auto", false), null);
        Check("effort", "OpenAI model-list filter", OpenAiProvider.IsChatModel("gpt-6-sol") && OpenAiProvider.IsChatModel("o3")
            && !OpenAiProvider.IsChatModel("gpt-4o-realtime-preview") && !OpenAiProvider.IsChatModel("tts-1")
            && !OpenAiProvider.IsChatModel("text-embedding-3-large") && !OpenAiProvider.IsChatModel("gpt-image-1"), "");
        Check("effort", "Gemini control per family",
            GeminiProvider.ThinkingOf("gemini-3.8-flash") == GeminiProvider.ThinkingControl.Level
            && GeminiProvider.ThinkingOf("gemini-2.5-pro") == GeminiProvider.ThinkingControl.Budget
            && GeminiProvider.ThinkingOf("gemma-3-27b-it") == GeminiProvider.ThinkingControl.None, "");
        var gpro = GeminiProvider.ThinkingConfigFor("gemini-2.5-pro", "max", true);
        Check("effort", "Gemini 2.5 Pro max → budget 32768 + thoughts", gpro != null && (int)gpro["thinkingBudget"] == 32768
            && gpro.ContainsKey("includeThoughts") && !gpro.ContainsKey("thinkingLevel"), gpro == null ? "null" : string.Join(",", gpro.Keys));

        Section("offline · usage gauge (rate-limit headers)");
        var ar = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        ar.Headers.Add("anthropic-ratelimit-input-tokens-limit", "400000");
        ar.Headers.Add("anthropic-ratelimit-input-tokens-remaining", "300000");
        ar.Headers.Add("anthropic-ratelimit-output-tokens-limit", "80000");
        ar.Headers.Add("anthropic-ratelimit-output-tokens-remaining", "20000");
        ar.Headers.Add("anthropic-ratelimit-requests-limit", "4000");
        ar.Headers.Add("anthropic-ratelimit-requests-remaining", "3999");
        ar.Headers.Add("anthropic-ratelimit-input-tokens-reset", "2026-10-09T06:00:00Z");
        var asnap = UsageMeter.FromAnthropic(ar, "claude-opus-5-5");
        Check("usage", "Anthropic: tightest bucket wins (output 25%)", asnap?.PercentLeft is double ap && Math.Abs(ap - 25) < 0.01 && asnap.Reset != null,
            asnap == null ? "null" : $"{asnap.PercentLeft}% reset={asnap.Reset}");
        var orr = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        orr.Headers.Add("x-ratelimit-limit-tokens", "2000000");
        orr.Headers.Add("x-ratelimit-remaining-tokens", "1500000");
        orr.Headers.Add("x-ratelimit-limit-requests", "5000");
        orr.Headers.Add("x-ratelimit-remaining-requests", "4990");
        orr.Headers.Add("x-ratelimit-reset-tokens", "6m0s");
        var osnap = UsageMeter.FromOpenAi(orr, "gpt-6-sol");
        Check("usage", "OpenAI: 75% left, reset parsed from 6m0s", osnap?.PercentLeft is double op && Math.Abs(op - 75) < 0.01
            && osnap.Reset is { } rs && (rs - DateTimeOffset.Now).TotalMinutes is > 5.9 and < 6.1, osnap == null ? "null" : $"{osnap.PercentLeft}%");
        Check("usage", "no headers → no gauge", UsageMeter.FromAnthropic(new HttpResponseMessage(), "m") == null, "");

        Section("offline · tool cap for OpenAI / Gemini (128 per request)");
        JsonElement emptySchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        var many = Enumerable.Range(0, 70).Select(i => new ToolSchema { Name = $"builtin_{i}", InputSchema = emptySchema })
            .Concat(Enumerable.Range(0, 125).Select(i => new ToolSchema { Name = $"mcp__brainx-brain__tool_{i}", InputSchema = emptySchema }))
            .Concat(new[] { "brain_search", "brain_semantic_search", "brain_get_note" }.Select(n => new ToolSchema { Name = $"mcp__brainx-brain__{n}", InputSchema = emptySchema }))
            .ToList();
        var capped = CodeAgentService.CapToolSchemas(many, 128, out int droppedTools);
        Check("cap", "198 tools → 128, every built-in kept", capped.Count == 128 && capped.Count(t => t.Name.StartsWith("builtin_")) == 70 && droppedTools == 70,
            $"kept={capped.Count} dropped={droppedTools}");
        Check("cap", "brain recall tools survive the cut", capped.Any(t => t.Name.EndsWith("__brain_search")) && capped.Any(t => t.Name.EndsWith("__brain_get_note")), "");
        Check("cap", "original order kept", capped.Select(t => many.IndexOf(t)).SequenceEqual(capped.Select(t => many.IndexOf(t)).OrderBy(i => i)), "");

        await MockProviders.Run(Check, Section);

        Section("offline · assistant turn rebuild");
        var resp = new NativeToolResponse
        {
            TextContent = "a",
            ToolCalls = [new NativeToolCall { Id = "t1", Name = "echo", Input = default }],
            Blocks =
            [
                new ContentBlock { Type = "thinking", Text = "", Signature = "sig" },
                new ContentBlock { Type = "text", Text = "a" },
                new ContentBlock { Type = "tool_use", Id = "t1", Name = "echo" },
            ],
        };
        var noTools = resp.ToAssistantMessage(includeToolUse: false);
        Check("turn", "thinking first, tool_use left to the loop",
            noTools.Content.Select(b => b.Type).SequenceEqual(new[] { "thinking", "text" }) && noTools.Content[0].Signature == "sig",
            string.Join(",", noTools.Content.Select(b => b.Type)));
        var plain = new NativeToolResponse { TextContent = "hi", ToolCalls = resp.ToolCalls }.ToAssistantMessage();
        Check("turn", "providers without blocks: text then tool_use",
            plain.Content.Select(b => b.Type).SequenceEqual(new[] { "text", "tool_use" }), string.Join(",", plain.Content.Select(b => b.Type)));
    }

    /// <summary>MD5 of a SQLite file after releasing pooled connections (they keep it open).</summary>
    static string HashUnlocked(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs));
    }

    static int Finish()
    {
        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        foreach (var pr in Problems) Console.WriteLine("  ✗ " + pr);
        Report.Insert(0, $"# apitest — {DateTime.Now:yyyy-MM-dd HH:mm}\n{Pass} passed, {Fail} failed\n");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "api-report.md"), Report.ToString());
        return Fail == 0 ? 0 : 1;
    }
}
