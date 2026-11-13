// Each chat keeps its own provider and model (owner 2026-11-13: "บางแชทใช้ fable บางแชทใช้ opus หรือ codex มันกลายเป็นใช้
// ร่วมกันทั้งหมด … แชทใครแชทมัน"). The pick used to be one for the whole app and every provider read the model from
// Settings on every request, so a model picked in one chat reached every other chat's requests. Here: the store, the
// pin a run carries, and the real OpenAI provider asked under two runs against a localhost server that records the
// "model" of each request. No key, no network beyond localhost.
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class ChatModelWire
{
    const string Sse =
        "data: {\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}\n\n" +
        "data: [DONE]\n\n";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · each chat keeps its own provider and model");

        // ─── the store ───
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-chatmodel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            ChatModelStore.Load(dir);
            check("chat-model", "a chat with no pick has none", ChatModelStore.Get("a") == null && ChatModelStore.Count == 0, "");
            bool set = ChatModelStore.Set("a", AiProviderType.ClaudeDev, " fable ");
            bool again = ChatModelStore.Set("a", AiProviderType.ClaudeDev, "fable");
            ChatModelStore.Set("b", AiProviderType.OpenAIDev, "default");
            check("chat-model", "a pick is kept per chat, trimmed; the same pick again changes nothing",
                set && !again && ChatModelStore.Get("a") == new ChatModelPick("ClaudeDev", "fable") && ChatModelStore.Get("b") == new ChatModelPick("OpenAIDev", "default"),
                $"{ChatModelStore.Get("a")} {ChatModelStore.Get("b")}");
            ChatModelStore.Set("a", AiProviderType.Anthropic, "claude-opus-5-5");
            check("chat-model", "picking in chat a leaves chat b's pick alone",
                ChatModelStore.Get("a") == new ChatModelPick("Anthropic", "claude-opus-5-5") && ChatModelStore.Get("b") == new ChatModelPick("OpenAIDev", "default"), "");
            check("chat-model", "a model on this PC and the BrainX room are not pinned (one set of weights for the whole app)",
                !ChatModelStore.CanPin(AiProviderType.Local) && !ChatModelStore.CanPin(AiProviderType.LlamaServer) && !ChatModelStore.CanPin(AiProviderType.BrainX)
                && ChatModelStore.CanPin(AiProviderType.Ollama) && ChatModelStore.CanPin(AiProviderType.GrokDev), "");
            ChatModelStore.Set("b", AiProviderType.Local, "whatever.gguf");
            check("chat-model", "a chat moved to a model on this PC drops its pick (it follows the app)", ChatModelStore.Get("b") == null, $"{ChatModelStore.Get("b")}");
            ChatModelStore.Set("c", AiProviderType.GeminiDev, "pro");
            ChatModelStore.Load(dir);
            check("chat-model", "the picks are there after a restart (chat-models.json)",
                ChatModelStore.Count == 2 && ChatModelStore.Get("a") == new ChatModelPick("Anthropic", "claude-opus-5-5") && ChatModelStore.Get("c") == new ChatModelPick("GeminiDev", "pro"),
                File.Exists(Path.Combine(dir, "chat-models.json")) ? File.ReadAllText(Path.Combine(dir, "chat-models.json")) : "no file");
            check("chat-model", "a deleted chat's pick goes with it", ChatModelStore.Forget("c") && !ChatModelStore.Forget("c") && ChatModelStore.Get("c") == null, "");
            File.WriteAllText(Path.Combine(dir, "chat-models.json"), "{ not json");
            ChatModelStore.Load(dir);
            check("chat-model", "a damaged file = no chat is pinned, nothing thrown", ChatModelStore.Count == 0, "");
        }
        finally
        {
            ChatModelStore.Load(null);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        // ─── the pin a run carries ───
        var runA = new AgentRunScope("chat-a", null);
        runA.PinModel("OpenAI", " gpt-6-sol ");
        check("chat-model", "a run's pin answers for its own provider only (any letter case), trimmed",
            runA.ModelFor("OpenAI") == "gpt-6-sol" && runA.ModelFor("openai") == "gpt-6-sol" && runA.ModelFor("Anthropic") == null && runA.ModelFor(null) == null,
            $"{runA.ModelFor("OpenAI")}");
        var child = runA.CreateChild("sub");
        check("chat-model", "a sub-agent of the run carries the same pin", child.PinnedProviderId == "OpenAI" && child.ModelFor("OpenAI") == "gpt-6-sol", "");
        var noModel = new AgentRunScope("chat-x", null);
        noModel.PinModel("ClaudeDev", "");
        check("chat-model", "a pin with no model names the provider and leaves the model to Settings",
            noModel.PinnedProviderId == "ClaudeDev" && noModel.ModelFor("ClaudeDev") == null, "");
        runA.PinModel(null, null);
        check("chat-model", "the pin can be dropped (the chat follows the app's pick again)", runA.PinnedProviderId == null && runA.ModelFor("OpenAI") == null, "");
        runA.PinModel("OpenAI", "gpt-6-sol");

        try
        {
            AgentRunScope.Enter(runA);
            check("chat-model", "PickFor: the run's pin goes before what Settings hold", SubAgentModel.PickFor("OpenAI", "from-settings") == "gpt-6-sol", SubAgentModel.PickFor("OpenAI", "from-settings"));
            check("chat-model", "PickFor: a run that went on with another provider (a stand-in) gets that provider's model from Settings",
                SubAgentModel.PickFor("Anthropic", "from-settings") == "from-settings", SubAgentModel.PickFor("Anthropic", "from-settings"));
            SubAgentModel.UseChat("claude-fable-5-2");
            check("chat-model", "PickFor: a Fable chat's own model still goes first", SubAgentModel.PickFor("OpenAI", "from-settings") == "claude-fable-5-2", "");
            SubAgentModel.UseChat(null);
            SubAgentModel.Use("routed-sub-model");
            check("chat-model", "PickFor: a sub-agent routed to another model still goes first", SubAgentModel.PickFor("OpenAI", "from-settings") == "routed-sub-model", "");
            SubAgentModel.Use(null);
        }
        finally { SubAgentModel.Use(null); SubAgentModel.UseChat(null); AgentRunScope.Enter(null); }
        check("chat-model", "PickFor: no run = what Settings hold", SubAgentModel.PickFor("OpenAI", "from-settings") == "from-settings", "");

        // ─── the real provider, two chats, one Settings ───
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var models = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    lock (models) models.Add(doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "(none)");
                }
                catch (JsonException) { lock (models) models.Add("(not json)"); }
                byte[] bytes = Encoding.UTF8.GetBytes(Sse);
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var svc = new SettingsService();   // portable — never the user's settings
        svc.Settings.ExtendedThinkingEnabled = false;
        svc.Settings.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-test", BaseUrl = $"http://localhost:{port}/v1", SelectedModel = "gpt-5.5" };
        var oa = new OpenAiProvider(svc, new CostTrackingService());
        var chatA = new AgentRunScope("chat-a", null); chatA.PinModel("OpenAI", "gpt-6-sol");
        var chatB = new AgentRunScope("chat-b", null); chatB.PinModel("OpenAI", "gpt-6-astra");
        var standIn = new AgentRunScope("chat-c", null); standIn.PinModel("ClaudeDev", "fable");

        async Task<string> Ask(AgentRunScope? run)
        {
            int before; lock (models) before = models.Count;
            // its own flow, as a chat's run is: the scope set here does not leak to the next ask
            await Task.Run(async () =>
            {
                AgentRunScope.Enter(run);
                await foreach (var _ in oa.ChatAsync([], "ping", "sys")) { }
            });
            lock (models) return models.Count > before ? models[^1] : "(no request)";
        }

        string a1 = await Ask(chatA), b1 = await Ask(chatB);
        check("chat-model", "two chats on the same provider send their own model, with one model in Settings",
            a1 == "gpt-6-sol" && b1 == "gpt-6-astra", $"a={a1} b={b1}");
        // the owner picks another model while chat B is on screen: Settings change, chat A's run does not
        svc.Settings.ProviderConfigs["OpenAI"].SelectedModel = "gpt-6-luna";
        string a2 = await Ask(chatA);
        check("chat-model", "a model picked elsewhere (Settings changed) does not reach chat A's next request", a2 == "gpt-6-sol", a2);
        chatA.PinModel("OpenAI", "gpt-6-luna");
        string a3 = await Ask(chatA), b2 = await Ask(chatB);
        check("chat-model", "the owner's pick IN chat A reaches chat A's next request — and only chat A's", a3 == "gpt-6-luna" && b2 == "gpt-6-astra", $"a={a3} b={b2}");
        string c1 = await Ask(standIn), none = await Ask(null);
        check("chat-model", "a run pinned to another provider, and a call outside any run, send what Settings hold",
            c1 == "gpt-6-luna" && none == "gpt-6-luna", $"stand-in={c1} none={none}");
        listener.Stop();
    }
}
