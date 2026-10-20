using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>
/// CluadeX's own chat tools — open_project, new_chat, close_chat — for a Claude Dev run. The Claude Code CLI is its own
/// agent with its own tools, so these lived only on the native loop: a Claude Dev chat asked to "open a chat at
/// D:\BrainX" answered that it could not (owner 2026-10-18: "ทำไมยังทำเองไม่ได้").
///
/// The CLI gets them as an MCP server: <c>CluadeX.exe --cx-tools</c> (<see cref="RunStdioServer"/>), started by the CLI
/// from the config file of <see cref="Register"/>. That process holds nothing — it relays each call over a current-user
/// pipe to the running app, which runs it exactly as a native call: the same permission gate (open_project asks before
/// it opens another project), the same handlers, inside the run of the chat that asked (its <see cref="AgentRunScope"/>,
/// so a new chat lands under it as a split). A call carries the turn's token: one random token per Claude Dev turn,
/// gone when the turn ends — another program cannot open chats through the pipe.
/// </summary>
public static class ChatToolBridge
{
    public const string Flag = "--cx-tools";

    /// <summary>The MCP server name in the CLI's config: its tools are mcp__cluadex__open_project and so on.</summary>
    public const string ServerName = "cluadex";

    /// <summary>The tools as the CLI sees them (<c>mcp__cluadex__…</c>).</summary>
    public static readonly string[] QualifiedToolNames =
    {
        $"mcp__{ServerName}__{ProjectToolModule.NewChatToolName}",
        $"mcp__{ServerName}__{ProjectToolModule.ToolName}",
        $"mcp__{ServerName}__{ProjectToolModule.CloseChatToolName}",
    };

    private const string TokenEnv = "CLUADEX_TOOLS_TOKEN";
    private const string PipeEnv = "CLUADEX_TOOLS_PIPE";

    /// <summary>The pipe; <c>CLUADEX_TOOLS_PIPE</c> renames it (tests use a private name).</summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable(PipeEnv) is { Length: > 0 } n ? n : "cluadex-chat-tools";

    /// <summary>How long one relayed call may take: open_project may wait for the user's answer on its card.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(15);

    private const int MaxLine = 1 << 20;

    private sealed record Registration(AgentRunScope? Scope, Func<ToolCall, CancellationToken, Task<ToolResult>> Execute,
        CancellationTokenSource Cts, string? Cwd);

    private static readonly ConcurrentDictionary<string, Registration> Turns = new(StringComparer.Ordinal);

    /// <summary>One turn's access: the token, the config file for <c>--mcp-config</c>. Dispose when the turn ends.</summary>
    public sealed class Ticket : IDisposable
    {
        public string Token { get; }
        public string ConfigFile { get; }
        internal Ticket(string token, string configFile) { Token = token; ConfigFile = configFile; }

        public void Dispose()
        {
            // Cancelled, not disposed: a call still waiting on its permission card holds the token.
            if (Turns.TryRemove(Token, out var reg))
                try { reg.Cts.Cancel(); } catch { }
            try { File.Delete(ConfigFile); } catch { }
        }
    }

    /// <summary>
    /// Let the Claude Dev turn in flight call the chat tools: <paramref name="execute"/> runs a call (the agent's own
    /// ExecuteToolAsync — permission gate included) inside <paramref name="scope"/>. Null when the config file cannot
    /// be written (the turn then runs without these tools).
    /// </summary>
    public static Ticket? Register(AgentRunScope? scope, Func<ToolCall, CancellationToken, Task<ToolResult>> execute,
        string? cwd, CancellationToken turn)
    {
        try
        {
            string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            string dir = Path.Combine(Path.GetTempPath(), "cluadex-guard");
            Directory.CreateDirectory(dir);
            SweepStale(dir);
            string file = Path.Combine(dir, $"claude-dev-tools-{token[..12]}.json");
            File.WriteAllText(file, ConfigJson(Safety.GuardHook.ExePath(), token, PipeName), new UTF8Encoding(false));
            Turns[token] = new Registration(scope, execute, CancellationTokenSource.CreateLinkedTokenSource(turn), cwd);
            return new Ticket(token, file);
        }
        catch { return null; }
    }

    /// <summary>The <c>--mcp-config</c> file: one stdio server, this exe in tools mode, the turn's token in its env.</summary>
    public static string ConfigJson(string exe, string token, string pipe) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "stdio",
                command = exe,
                args = new[] { Flag },
                env = new Dictionary<string, string> { [TokenEnv] = token, [PipeEnv] = pipe },
            },
        },
    });

    /// <summary>Config files a crashed turn left behind (they hold a dead token, but still: not kept).</summary>
    private static void SweepStale(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "claude-dev-tools-*.json"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        }
        catch { }
    }

    // ─── App side: the pipe ───

    private static int _started;

    /// <summary>Serve relayed calls in the app (once). If another CluadeX already serves the name, this one stays quiet.</summary>
    public static void StartServer()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        string name = PipeName;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch { return; }
                try { await server.WaitForConnectionAsync().ConfigureAwait(false); }
                catch { server.Dispose(); await Task.Delay(500).ConfigureAwait(false); continue; }
                _ = Task.Run(() => ServeOneAsync(server));
            }
        });
    }

    private static async Task ServeOneAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            (bool Ok, string Text) answer;
            try
            {
                var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var lineTask = reader.ReadLineAsync();
                answer = await Task.WhenAny(lineTask, Task.Delay(5000)).ConfigureAwait(false) == lineTask
                         && lineTask.Result is { Length: > 0 and < MaxLine } line
                    ? await HandleAsync(line).ConfigureAwait(false)
                    : (false, "CluadeX: the request did not arrive.");
            }
            catch (Exception ex) { answer = (false, "CluadeX: the call failed (" + ex.GetType().Name + ")."); }
            try
            {
                string json = JsonSerializer.Serialize(new { ok = answer.Ok, text = answer.Text });
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await server.WriteAsync(bytes).ConfigureAwait(false);
                await server.FlushAsync().ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
            catch { }
        }
    }

    /// <summary>One relayed call → (success, what the tool says). Pure apart from running the tool.</summary>
    public static async Task<(bool Ok, string Text)> HandleAsync(string requestLine)
    {
        using var doc = JsonDocument.Parse(requestLine);
        var root = doc.RootElement;
        string Str(string p) => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string token = Str("token"), tool = Str("tool");
        if (token.Length == 0 || !Turns.TryGetValue(token, out var reg))
            return (false, "CluadeX: this Claude Dev turn is over (or the call did not come from it) — nothing was done.");
        var type = new ProjectToolModule().ResolveToolType(tool);
        if (type is not (ToolType.OpenProject or ToolType.NewChat or ToolType.CloseChat))
            return (false, $"CluadeX: '{tool}' is not one of its chat tools (open_project, new_chat, close_chat).");

        var call = new ToolCall { Type = type.Value, ToolName = tool };
        if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject())
                call.Arguments[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();

        var ct = reg.Cts.Token;
        if (ct.IsCancellationRequested) return (false, "CluadeX: this Claude Dev turn is over — nothing was done.");
        // The call runs as part of the chat's run: its folder, its split parent, its permission cards.
        AgentRunScope.Enter(reg.Scope);
        var result = await reg.Execute(call, ct).ConfigureAwait(false);
        string text = result.Output is { Length: > 0 } o ? o : result.Error ?? (result.Success ? "Done." : "Failed.");
        if (result.Success && type == ToolType.OpenProject) text += "\n\n" + OpenProjectNote(reg.Cwd);
        return (result.Success, text);
    }

    /// <summary>What open_project means for a CLI that keeps the folder it started in.</summary>
    public static string OpenProjectNote(string? cwd) =>
        "(Claude Dev: this run keeps working in " + (cwd ?? "the folder it started in") + ". The folder is now allowed for "
        + "this chat: read it by absolute path now; this chat's next turn starts with it added (--add-dir), so edits and "
        + "builds there work from then. To work in it right away, start a chat there with new_chat.)";

    // ─── CLI side: CluadeX.exe --cx-tools (an MCP stdio server) ───

    /// <summary>The tools mode's entry point: newline JSON-RPC on stdin/stdout until stdin closes. Exit code 0.</summary>
    public static int RunStdioServer()
    {
        string token = Environment.GetEnvironmentVariable(TokenEnv) ?? "";
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new object();
        var pending = new List<Task>();
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            string l = line;
            // tools/call may wait minutes for the user's answer — the CLI can send other requests meanwhile.
            var t = Task.Run(() =>
            {
                string? reply;
                try { reply = Respond(l, token, Relay); }
                catch { reply = null; }
                if (reply != null) lock (writeLock) stdout.WriteLine(reply);
            });
            lock (pending) { pending.RemoveAll(p => p.IsCompleted); pending.Add(t); }
        }
        try { Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(5)); } catch { }
        return 0;
    }

    /// <summary>
    /// One JSON-RPC message → the reply line (null for a notification). <paramref name="relay"/> sends a tools/call to the
    /// app: (token, tool, arguments JSON) → (ok, text). Pure, so the harness checks the protocol without a pipe.
    /// </summary>
    public static string? Respond(string line, string token, Func<string, string, JsonElement, (bool Ok, string Text)> relay)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "parse error"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(null, -32600, "invalid request");
            bool hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            object? id = !hasId ? null : IdOf(idEl);
            string method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (!hasId) return null;   // a notification (notifications/initialized, cancelled) is never answered
            var prm = root.TryGetProperty("params", out var pp) ? pp : default;
            switch (method)
            {
                case "initialize":
                {
                    string version = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("protocolVersion", out var pv)
                        && pv.ValueKind == JsonValueKind.String ? pv.GetString() ?? "2025-03-26" : "2025-03-26";
                    return Result(id, new
                    {
                        protocolVersion = version,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "CluadeX-ChatTools", version = "1" },
                    });
                }
                case "ping":
                    return Result(id, new { });
                case "tools/list":
                    return Result(id, new { tools = ToolList() });
                case "tools/call":
                {
                    string name = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("name", out var nm)
                        && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "" : "";
                    var args = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("arguments", out var a) ? a : default;
                    var (ok, text) = token.Length == 0
                        ? (false, "CluadeX: no turn token — this server only works for a Claude Dev run started by CluadeX.")
                        : relay(token, name, args);
                    return Result(id, new { content = new[] { new { type = "text", text } }, isError = !ok });
                }
                default:
                    return Error(id, -32601, $"method '{method}' is not supported");
            }
        }
    }

    /// <summary>The tools/list entries: the native schemas, with open_project said the way it works for the CLI.</summary>
    public static object[] ToolList() => new ProjectToolModule().GetToolSchemas().Select(s => (object)new
    {
        name = s.Name,
        description = s.Name == ProjectToolModule.ToolName
            ? "Open another project folder for this CluadeX chat — CluadeX asks the user first unless they always allow it. "
              + "Use it when the task needs code in a project outside the folders you were started with: pass its path or just its "
              + "name (CluadeX finds the folder among the projects on this PC). For a NEW project pass create=\"true\" instead of "
              + "making the folder yourself — CluadeX puts it beside the user's other projects. "
              + "add=\"true\" keeps the current project and opens this one beside it. The folder is readable "
              + "by absolute path at once and fully workable from the chat's next turn; to work in it right away, use new_chat "
              + "with its path. Never answer that you cannot open a folder."
            : s.Description,
        inputSchema = s.InputSchema,
    }).ToArray();

    /// <summary>The request id echoed exactly: a string stays a string, a number a number.</summary>
    private static object? IdOf(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.String) return id.GetString();
        if (id.TryGetInt64(out long n)) return n;
        return id.GetDouble();
    }

    private static readonly JsonSerializerOptions Json =new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Result(object? id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json);

    private static string Error(object? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, Json);

    /// <summary>Send one call to the running app and wait for its answer.</summary>
    private static (bool Ok, string Text) Relay(string token, string tool, JsonElement args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            string json = JsonSerializer.Serialize(new
            {
                token, tool,
                args = args.ValueKind == JsonValueKind.Object ? args : JsonDocument.Parse("{}").RootElement,
            });
            var bytes = Encoding.UTF8.GetBytes(json.Replace("\n", " ") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            using var reader = new StreamReader(client, new UTF8Encoding(false));
            var read = reader.ReadLineAsync();
            if (!read.Wait(CallTimeout) || read.Result is not { Length: > 0 } answer)
                return (false, "CluadeX did not answer in time — nothing is known to have happened.");
            using var doc = JsonDocument.Parse(answer);
            var r = doc.RootElement;
            bool ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string text = r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            return (ok, text);
        }
        catch (TimeoutException) { return (false, "CluadeX is not running (its chat-tools pipe did not answer)."); }
        catch (Exception ex) { return (false, "CluadeX could not be reached (" + ex.GetType().Name + ")."); }
    }
}
