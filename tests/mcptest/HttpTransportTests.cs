using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;
using CluadeX.Services.Connections;
using CluadeX.Services.Mcp;

namespace McpTest;

// MCP over Streamable HTTP: url entries in mcp_servers.json (McpHttpTransport) and the connector relay that BrainX's
// bridges start for XDREAMER (CluadeX.exe --connector xdreamer → ConnectorLauncher.RelayHttpAsync). Both run against
// an in-process fake server that answers the way real ones do: a pretty-printed JSON answer, an SSE answer with a
// progress note in front, a session id, a 404 once the session is forgotten, a 401 for a wrong key. No network.
internal static partial class Program
{
    /// <summary>A Streamable HTTP MCP server in a handler. Key "axm-good"; Expire() forgets every session.</summary>
    sealed class FakeHttpMcp : HttpMessageHandler
    {
        readonly HashSet<string> _sessions = new();
        public int Posts;
        public void Expire() { lock (_sessions) _sessions.Clear(); }

        static HttpResponseMessage Json(HttpStatusCode code, string body, string type = "application/json") =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, type) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            Interlocked.Increment(ref Posts);
            if (request.Headers.Authorization?.ToString() != "Bearer axm-good")
                return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key."}}""");
            var msg = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            string? sid = request.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.First() : null;
            string method = (string?)msg["method"] ?? "";
            if (method == "initialize")
            {
                string fresh = Guid.NewGuid().ToString("N");
                lock (_sessions) _sessions.Add(fresh);
                var init = Json(HttpStatusCode.OK, $$"""
                    {
                      "jsonrpc": "2.0",
                      "id": {{msg["id"]!.ToJsonString()}},
                      "result": { "protocolVersion": "2025-06-18", "serverInfo": { "name": "fake-xdreamer", "version": "1.0.0" }, "capabilities": { "tools": {} } }
                    }
                    """);
                init.Headers.Add("Mcp-Session-Id", fresh);
                return init;
            }
            lock (_sessions) if (sid == null || !_sessions.Contains(sid)) return Json(HttpStatusCode.NotFound, """{"error":"no such session"}""");
            if (!request.Headers.TryGetValues("MCP-Protocol-Version", out var pv) || pv.First() != "2025-06-18")
                return Json(HttpStatusCode.BadRequest, """{"error":"MCP-Protocol-Version missing"}""");
            if (msg["id"] == null) return new HttpResponseMessage(HttpStatusCode.Accepted);
            string id = msg["id"]!.ToJsonString();
            if (method == "tools/list")
                return Json(HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"tools":[{"name":"generate_image","description":"สร้างภาพ","inputSchema":{"type":"object"}}]}}""");
            if (method == "tools/call")
            {
                string prompt = msg["params"]?["arguments"]?["prompt"]?.ToString() ?? "";
                string answer = $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"content":[{"type":"text","text":{{{JsonSerializer.Serialize("queued: " + prompt)}}}}]}}""";
                string note = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}""";
                return Json(HttpStatusCode.OK, $"event: message\ndata: {note}\n\n: keep-alive\n\ndata: {answer}\n\n", "text/event-stream");
            }
            return Json(HttpStatusCode.OK, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32601,"message":"no such method"}}""");
        }
    }

    const string FakeUrl = "https://mcp.example.test/api/mcp";

    static async Task HttpTransportTests()
    {
        Console.WriteLine("\n── MCP over HTTP: url entries + the XDREAMER relay ──");

        await Case("N1", async () =>
        {
            var fake = new FakeHttpMcp();
            var cfg = new McpServerConfig
            {
                Name = "web", Type = "http", Url = FakeUrl,
                Headers = new() { ["Authorization"] = "Bearer ${XD_TEST_KEY}" }, Env = new() { ["XD_TEST_KEY"] = "axm-good" },
            };
            var t = new McpHttpTransport(fake);
            var notes = new List<string>();
            bool lost = false;
            t.OnNotification += n => notes.Add(n.Method);
            t.OnUnexpectedExit += () => lost = true;
            await t.StartAsync(cfg);
            var init = await t.SendRequestAsync("initialize", new McpInitializeParams(), 5000);
            await t.SendNotificationAsync("notifications/initialized");
            var list = await t.SendRequestAsync("tools/list", new { }, 5000);
            Check("N1", "a url entry handshakes over HTTP: ${VAR} header from env, session id and protocol version carried",
                cfg.IsRemote && init.IsSuccess && list.IsSuccess && list.Result!.Value.GetProperty("tools")[0].GetProperty("description").GetString() == "สร้างภาพ",
                list.Error?.Message ?? "");
            var call = await t.SendRequestAsync("tools/call", new { name = "generate_image", arguments = new { prompt = "แมว" } }, 5000);
            Check("N2", "an SSE answer: the response picked by id, the progress note delivered as a notification",
                call.IsSuccess && call.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString() == "queued: แมว" && notes.Contains("notifications/progress"));
            fake.Expire();
            bool threw = false;
            try { await t.SendRequestAsync("tools/list", new { }, 5000); } catch (IOException) { threw = true; }
            Check("N3", "a forgotten session (404) ends the connection and hands it to the supervisor", threw && lost && !t.IsAlive);
            await t.StopAsync();

            var wrong = new McpHttpTransport(fake);
            await wrong.StartAsync(new McpServerConfig { Url = FakeUrl, Headers = new() { ["Authorization"] = "Bearer axm-wrong" } });
            string why = "";
            try { await wrong.SendRequestAsync("initialize", new McpInitializeParams(), 5000); } catch (InvalidOperationException ex) { why = ex.Message; }
            Check("N4", "a refused key says so, in the server's own words", why.Contains("refused the key") && why.Contains("Invalid API key"), why);

            bool clear = false;
            try { await new McpHttpTransport(fake).StartAsync(new McpServerConfig { Url = "http://example.com/mcp" }); } catch (InvalidOperationException) { clear = true; }
            bool unset = false;
            try { McpHttpTransport.ResolveHeaders(new McpServerConfig { Url = FakeUrl, Headers = new() { ["Authorization"] = "Bearer ${CX_SURELY_UNSET_9}" } }); }
            catch (InvalidOperationException) { unset = true; }
            Check("N5", "plain http off this PC is refused, and an unset ${VAR} is an error (never 'Bearer ')", clear && unset);
        });

        await Case("N6", async () =>
        {
            var item = McpServerDisplayItem.FromConfig("web", new McpServerConfig { Type = "http", Url = FakeUrl, Headers = new() { ["Authorization"] = "Bearer x" } }, false, 0);
            var back = item.ToConfig();
            Check("N6", "the MCP page shows a url entry's address and saves it back as type/url/headers (no empty command)",
                item.Command == FakeUrl && back.IsRemote && back.Url == FakeUrl && back.Type == "http" && back.Headers!["Authorization"] == "Bearer x" && back.Command == "");
            Check("N6b", "the Add box's one header line", McpServerConfig.ParseHeaderLine("Authorization: Bearer axm-1") is { Key: "Authorization", Value: "Bearer axm-1" }
                && McpServerConfig.ParseHeaderLine("-y @scope/pkg") == null);
            await Task.CompletedTask;
        });

        await Case("N7", async () =>
        {
            var fake = new FakeHttpMcp();
            var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n",
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"brainx-brain","version":"2"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":"c3","method":"tools/call","params":{"name":"generate_image","arguments":{"prompt":"ช้าง"}}}""",
                "not json") + "\n"));
            var output = new MemoryStream();
            var log = new StringWriter();
            int code = await ConnectorLauncher.RelayHttpAsync(new Uri(FakeUrl), new Dictionary<string, string> { ["Authorization"] = "Bearer axm-good" },
                input, output, log, "XDREAMER", fake);
            string text = Encoding.UTF8.GetString(output.ToArray());
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool allJson = lines.All(l => { try { JsonDocument.Parse(l).Dispose(); return true; } catch { return false; } });
            Check("N7", "the relay: one line per answer (a pretty-printed one compacted), number and string ids kept, Thai intact",
                code == 0 && allJson && text.Contains("\"id\":1") && text.Contains("\"id\":2") && text.Contains("\"id\":\"c3\"") && text.Contains("ช้าง")
                && text.Contains("notifications/progress") && log.ToString().Contains("not JSON"), text);

            var refusedOut = new MemoryStream();
            var refusedLog = new StringWriter();
            await ConnectorLauncher.RelayHttpAsync(new Uri(FakeUrl), new Dictionary<string, string> { ["Authorization"] = "Bearer axm-nope" },
                new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9,"method":"initialize","params":{}}""" + "\n")), refusedOut, refusedLog, "XDREAMER", fake);
            string refused = Encoding.UTF8.GetString(refusedOut.ToArray());
            Check("N8", "a refused key: the client gets a JSON-RPC error carrying its id (never left waiting), stderr says why",
                refused.Contains("\"id\":9") && refused.Contains("-32000") && refusedLog.ToString().Contains("refused the key"), refused);
        });
    }
}
