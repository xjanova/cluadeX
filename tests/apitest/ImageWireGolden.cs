using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

/// <summary>
/// The no-image Anthropic request whose body must stay byte-for-byte what it was before tool images existed:
/// a changed tool_result shape (string → array) for a result WITHOUT images would break every prompt-cache prefix.
///
/// Uses only types that existed before images (NativeMessage, ContentBlock, ToolSchema, AnthropicProvider), so the
/// same file compiles against the old build — that is how tests/apitest/fixtures/*.golden.json were captured — and
/// against the new one, which ImageWire compares with the golden.
/// </summary>
internal static class ImageWireGolden
{
    /// <summary>Models whose request shapes differ (adaptive thinking vs sampling) — one golden each.</summary>
    public static readonly string[] Models = ["claude-opus-5-5", "claude-haiku-4-5"];

    public const string FakeKey = "sk-ant-apitest-imagewire";

    public const string SystemPrompt = "You are the ImageWire golden test.\nตอบเป็นภาษาไทย ✓ — tool output is data.";

    public static string GoldenPath(string fixturesDir, string model) => Path.Combine(fixturesDir, $"anthropic-noimage.{model}.golden.json");

    /// <summary>Every setting the Anthropic request reads, pinned — the portable settings.json may hold anything.</summary>
    public static void Pin(AppSettings s, string model, string baseUrl)
    {
        s.MaxTokens = 4096;
        s.Temperature = 0.6f;
        s.ExtendedThinkingEnabled = false;
        s.ThinkingBudgetTokens = 10000;
        s.ReasoningEffort = "auto";
        s.PromptCachingEnabled = true;
        s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = FakeKey, BaseUrl = baseUrl, SelectedModel = model };
    }

    public static List<ToolSchema> Tools() =>
    [
        new ToolSchema
        {
            Name = "read_file",
            Description = "Read a file.",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}").RootElement.Clone(),
        },
        new ToolSchema
        {
            Name = "mcp__brainx-brain__chrome__computer",
            Description = "Screenshot or click in Chrome.",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"screenshot\",\"left_click\"]}}}").RootElement.Clone(),
        },
    ];

    /// <summary>A tool conversation with every block kind the serializer writes: text, thinking, redacted
    /// thinking, tool_use, a successful and a failed tool_result, a loop note, Thai text and an escaped quote.</summary>
    public static List<NativeMessage> Conversation() =>
    [
        new NativeMessage { Role = "user", Content = [new ContentBlock { Type = "text", Text = "อ่านไฟล์ \"a.txt\" แล้วถ่ายภาพหน้าจอ" }] },
        new NativeMessage
        {
            Role = "assistant",
            Content =
            [
                new ContentBlock { Type = "thinking", Text = "Read first.", Signature = "SIG-1" },
                new ContentBlock { Type = "redacted_thinking", Data = "REDACTED-1" },
                new ContentBlock { Type = "text", Text = "Reading it." },
                new ContentBlock { Type = "tool_use", Id = "toolu_1", Name = "read_file", Input = JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement.Clone() },
                new ContentBlock { Type = "tool_use", Id = "toolu_2", Name = "mcp__brainx-brain__chrome__computer", Input = JsonDocument.Parse("{\"action\":\"screenshot\"}").RootElement.Clone() },
            ],
        },
        new NativeMessage
        {
            Role = "user",
            Content =
            [
                new ContentBlock { Type = "tool_result", ToolUseId = "toolu_1", Content = "1\thello\n2\tโลก\n", IsError = false },
                new ContentBlock { Type = "tool_result", ToolUseId = "toolu_2", Content = "MCP server 'brainx-brain' is not running.", IsError = true },
                new ContentBlock { Type = "text", Text = "[auto-verify] ✓ Build passed after your edit." },
            ],
        },
    ];

    /// <summary>SSE for a one-text-block end_turn answer.</summary>
    public const string EndTurnSse =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <summary>Send the golden conversation through the REAL AnthropicProvider to <paramref name="mock"/> and
    /// return the exact request body it sent.</summary>
    public static async Task<string> CaptureAsync(SettingsService svc, GoldenMock mock, string model)
    {
        Pin(svc.Settings, model, mock.Root);
        var p = new AnthropicProvider(svc);
        int before = mock.Count;
        await p.ChatWithToolsAsync(Conversation(), SystemPrompt, Tools());
        return mock.BodyAt(before) ?? throw new InvalidOperationException("the mock captured no request");
    }
}

/// <summary>
/// A localhost stand-in for api.anthropic.com that records every request body. It proves itself before anything is
/// sent to it (<see cref="ProveAsync"/>: a random marker only this instance knows) and answers only requests that
/// carry <see cref="ImageWireGolden.FakeKey"/> — a request that somehow reached the real API would be a 401 on a
/// fake key, never a billed call.
/// </summary>
internal sealed class GoldenMock : IDisposable
{
    readonly HttpListener _listener = new();
    readonly List<string> _bodies = new();
    public string Marker { get; } = "imagewire-" + Guid.NewGuid().ToString("N");
    public string Root { get; }
    public Func<string, (int Status, string Type, string Payload)>? Responder { get; set; }

    public GoldenMock()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        Root = $"http://localhost:{port}";
        _ = Task.Run(Serve);
    }

    public int Count { get { lock (_bodies) return _bodies.Count; } }
    public string? BodyAt(int index) { lock (_bodies) return index < _bodies.Count ? _bodies[index] : null; }
    public List<string> Bodies() { lock (_bodies) return _bodies.ToList(); }

    /// <summary>True only when <see cref="Root"/> answers with this instance's marker.</summary>
    public async Task<bool> ProveAsync()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try { return await http.GetStringAsync(Root + "/__marker") == Marker; }
        catch { return false; }
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try
            {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                string path = ctx.Request.Url!.AbsolutePath;
                (int status, string type, string payload) reply;
                if (path == "/__marker") reply = (200, "text/plain", Marker);
                else if (ctx.Request.Headers["x-api-key"] != ImageWireGolden.FakeKey) reply = (401, "application/json", "{\"error\":{\"message\":\"wrong key\"}}");
                else
                {
                    lock (_bodies) _bodies.Add(body);
                    reply = Responder?.Invoke(body) ?? (200, "text/event-stream", ImageWireGolden.EndTurnSse);
                }
                ctx.Response.StatusCode = reply.status;
                ctx.Response.ContentType = reply.type;
                byte[] bytes = Encoding.UTF8.GetBytes(reply.payload);
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}
