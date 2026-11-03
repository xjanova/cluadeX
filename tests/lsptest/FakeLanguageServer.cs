using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LspTest;

/// <summary>
/// A language server small enough to read in one sitting: this same exe started with --fake-lsp. It speaks real
/// stdio JSON-RPC (Content-Length framing in bytes), keeps each open document's text by applying the edits it is sent
/// — incremental or whole-text, as it advertised — and logs every message to a file the harness reads.
///
/// Diagnostics: every word containing "ERR" is an error (message in Thai with an emoji, so a client that counts
/// characters instead of bytes breaks), every word containing "WARN" a warning. Like pyright, it publishes nothing
/// until the client has answered its workspace/configuration request. Hover answers "symbol &lt;word&gt;" in a code fence.
///
/// Options: --log FILE, --sync 1|2, --ignore-shutdown (never answers shutdown, never exits), --crash-after-open.
/// </summary>
internal static class FakeLanguageServer
{
    static readonly Dictionary<string, string> Docs = new();
    static readonly Dictionary<string, int> Versions = new();
    static readonly object WriteLock = new();
    static Stream _out = Stream.Null;
    static string _log = "";
    static bool _configured;

    public static int Run(string[] args)
    {
        string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        _log = Opt("--log");
        int sync = int.TryParse(Opt("--sync"), out int s) ? s : 2;
        bool ignoreShutdown = args.Contains("--ignore-shutdown");
        bool crashAfterOpen = args.Contains("--crash-after-open");

        var input = Console.OpenStandardInput();
        _out = Console.OpenStandardOutput();
        Log("started pid=" + Environment.ProcessId);
        while (true)
        {
            var body = ReadMessage(input);
            if (body == null) { Log("stdin closed"); return 0; }
            var msg = JsonNode.Parse(body)!.AsObject();
            string method = (string?)msg["method"] ?? "";
            var id = msg["id"];

            if (method.Length == 0)
            {
                // A response to our workspace/configuration request.
                if ((string?)id == "cfg-1")
                {
                    Log("config-answer " + (msg["result"]?.ToJsonString() ?? "(none)"));
                    _configured = msg["result"] is JsonArray;
                    foreach (var uri in Docs.Keys.ToList()) Publish(uri);
                }
                continue;
            }

            switch (method)
            {
                case "initialize":
                    Log("initialize rootUri=" + (string?)msg["params"]?["rootUri"]);
                    Reply(id, new JsonObject
                    {
                        ["capabilities"] = new JsonObject
                        {
                            ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = sync },
                            ["hoverProvider"] = true,
                        },
                        ["serverInfo"] = new JsonObject { ["name"] = "fake" },
                    });
                    break;
                case "initialized":
                    Log("initialized");
                    Send(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = "cfg-1", ["method"] = "workspace/configuration",
                        ["params"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["section"] = "fake" }) },
                    });
                    break;
                case "textDocument/didOpen":
                {
                    var td = msg["params"]!["textDocument"]!;
                    string uri = (string)td["uri"]!;
                    Docs[uri] = (string)td["text"]!;
                    Versions[uri] = (int)td["version"]!;
                    Log($"didOpen {uri} v={Versions[uri]} len={Docs[uri].Length} lang={(string?)td["languageId"]}");
                    WriteText(uri);
                    if (crashAfterOpen)
                    {
                        Console.Error.WriteLine("fake crash: boom");
                        Console.Error.Flush();
                        Environment.Exit(3);
                    }
                    Publish(uri);
                    break;
                }
                case "textDocument/didChange":
                {
                    var p = msg["params"]!;
                    string uri = (string)p["textDocument"]!["uri"]!;
                    int version = (int)p["textDocument"]!["version"]!;
                    var changes = p["contentChanges"]!.AsArray();
                    bool incremental = changes.All(c => c?["range"] != null);
                    string text = Docs.GetValueOrDefault(uri, "");
                    foreach (var c in changes)
                    {
                        if (c?["range"] is JsonObject range)
                        {
                            int start = Offset(text, (int)range["start"]!["line"]!, (int)range["start"]!["character"]!);
                            int end = Offset(text, (int)range["end"]!["line"]!, (int)range["end"]!["character"]!);
                            text = text[..start] + (string)c["text"]! + text[end..];
                        }
                        else text = (string)c!["text"]!;
                    }
                    Docs[uri] = text;
                    Versions[uri] = version;
                    Log($"didChange v={version} n={changes.Count} kind={(incremental ? "incr" : "full")}");
                    WriteText(uri);
                    Publish(uri);
                    break;
                }
                case "textDocument/didClose":
                {
                    string uri = (string)msg["params"]!["textDocument"]!["uri"]!;
                    Docs.Remove(uri);
                    Log("didClose " + uri);
                    break;
                }
                case "textDocument/hover":
                {
                    var p = msg["params"]!;
                    string uri = (string)p["textDocument"]!["uri"]!;
                    string text = Docs.GetValueOrDefault(uri, "");
                    int at = Offset(text, (int)p["position"]!["line"]!, (int)p["position"]!["character"]!);
                    string word = WordAt(text, at);
                    Log("hover " + word);
                    Reply(id, word.Length == 0 ? null : new JsonObject
                    {
                        ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "```csharp\nsymbol " + word + "\n```\nDocs for `" + word + "`" },
                    });
                    break;
                }
                case "shutdown":
                    Log("shutdown");
                    if (!ignoreShutdown) Reply(id, null);
                    break;
                case "exit":
                    Log("exit");
                    if (!ignoreShutdown) return 0;
                    break;
                default:
                    Log("other " + method);
                    if (id != null) Reply(id, null);
                    break;
            }
        }
    }

    static void Publish(string uri)
    {
        if (!_configured || !Docs.TryGetValue(uri, out var text)) return;
        var diags = new JsonArray();
        var lines = SplitLines(text);
        for (int line = 0; line < lines.Count; line++)
        {
            string l = lines[line];
            int i = 0;
            while (i < l.Length)
            {
                if (!IsWord(l[i])) { i++; continue; }
                int s = i;
                while (i < l.Length && IsWord(l[i])) i++;
                string word = l[s..i];
                int sev = word.Contains("ERR") ? 1 : word.Contains("WARN") ? 2 : 0;
                if (sev == 0) continue;
                diags.Add(new JsonObject
                {
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = line, ["character"] = s },
                        ["end"] = new JsonObject { ["line"] = line, ["character"] = i },
                    },
                    ["severity"] = sev,
                    ["source"] = "fake",
                    ["code"] = sev == 1 ? "F001" : "F002",
                    ["message"] = sev == 1 ? $"ผิดตรงนี้: {word} 😀" : $"careful: {word}",
                });
            }
        }
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject { ["uri"] = uri, ["version"] = Versions.GetValueOrDefault(uri), ["diagnostics"] = diags },
        });
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    static string WordAt(string text, int at)
    {
        int s = at, e = at;
        while (s > 0 && IsWord(text[s - 1])) s--;
        while (e < text.Length && IsWord(text[e])) e++;
        return text[s..e];
    }

    /// <summary>Lines without their terminators; \r\n, \n and \r each end a line (the LSP rule).</summary>
    static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n') continue;
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    static int Offset(string text, int line, int character)
    {
        int offset = 0;
        for (int l = 0; l < line && offset <= text.Length; l++)
        {
            int nl = text.IndexOfAny(new[] { '\r', '\n' }, offset);
            if (nl < 0) return text.Length;
            offset = nl + (text[nl] == '\r' && nl + 1 < text.Length && text[nl + 1] == '\n' ? 2 : 1);
        }
        int lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, Math.Min(offset, text.Length));
        if (lineEnd < 0) lineEnd = text.Length;
        return Math.Min(offset + character, lineEnd);
    }

    static byte[]? ReadMessage(Stream input)
    {
        int length = -1;
        var line = new StringBuilder();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) return null;
            if (b == '\r') continue;
            if (b != '\n') { line.Append((char)b); continue; }
            if (line.Length == 0) break;
            string h = line.ToString();
            line.Clear();
            if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(h[15..].Trim());
        }
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = input.Read(body, read, length - read);
            if (n == 0) return null;
            read += n;
        }
        return body;
    }

    static void Reply(JsonNode? id, JsonNode? result)
        => Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    static void Send(JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (WriteLock)
        {
            _out.Write(header);
            _out.Write(body);
            _out.Flush();
        }
    }

    static void Log(string line)
    {
        if (_log.Length == 0) return;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { File.AppendAllText(_log, line + "\n", Encoding.UTF8); return; }
            catch (IOException) { Thread.Sleep(10); }
        }
    }

    static void WriteText(string uri)
    {
        if (_log.Length == 0) return;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { File.WriteAllText(_log + ".text", Docs[uri], new UTF8Encoding(false)); return; }
            catch (IOException) { Thread.Sleep(10); }
        }
    }
}
