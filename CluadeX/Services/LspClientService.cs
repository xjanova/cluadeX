using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services;

/// <summary>
/// Lightweight LSP (Language Server Protocol) client.
/// Connects to language servers via stdio for code intelligence features:
/// diagnostics, completions, hover info, go-to-definition, references.
/// </summary>
public sealed class LspClientService : IDisposable
{
    private readonly FileSystemService _fileSystem;
    private readonly SettingsService _settingsService;
    private Process? _serverProcess;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _requestId;
    private bool _initialized;
    private bool _disposed;
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    // Supported language → server command mapping
    private static readonly Dictionary<string, (string exe, string args)> KnownServers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ("OmniSharp", "-lsp"),
        ["cs"] = ("OmniSharp", "-lsp"),
        ["python"] = ("pylsp", ""),
        ["py"] = ("pylsp", ""),
        ["typescript"] = ("typescript-language-server", "--stdio"),
        ["ts"] = ("typescript-language-server", "--stdio"),
        ["javascript"] = ("typescript-language-server", "--stdio"),
        ["js"] = ("typescript-language-server", "--stdio"),
        ["rust"] = ("rust-analyzer", ""),
        ["rs"] = ("rust-analyzer", ""),
        ["go"] = ("gopls", "serve"),
        ["java"] = ("jdtls", ""),
    };

    public bool IsConnected => _serverProcess != null && !_serverProcess.HasExited && _initialized;

    public LspClientService(FileSystemService fileSystem, SettingsService settingsService)
    {
        _fileSystem = fileSystem;
        _settingsService = settingsService;
    }

    /// <summary>Start a language server for the given language.</summary>
    public async Task<bool> StartServerAsync(string language, CancellationToken ct = default)
    {
        if (IsConnected) return true;

        if (!KnownServers.TryGetValue(language, out var server))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = server.exe,
                Arguments = server.args,
                WorkingDirectory = _fileSystem.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            _serverProcess = Process.Start(psi);
            if (_serverProcess == null) return false;

            _writer = _serverProcess.StandardInput;
            _reader = _serverProcess.StandardOutput;

            // Start reading responses in background
            _ = Task.Run(() => ReadResponsesAsync(ct), ct);

            // Send initialize request
            var initResult = await SendRequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri = $"file:///{_fileSystem.WorkingDirectory.Replace('\\', '/')}",
                capabilities = new
                {
                    textDocument = new
                    {
                        completion = new { completionItem = new { snippetSupport = false } },
                        hover = new { contentFormat = new[] { "plaintext", "markdown" } },
                        definition = new { },
                        references = new { },
                        diagnostics = new { },
                    }
                }
            }, ct);

            // Send initialized notification
            await SendNotificationAsync("initialized", new { }, ct);
            _initialized = true;

            return true;
        }
        catch
        {
            StopServer();
            return false;
        }
    }

    /// <summary>Get diagnostics (errors/warnings) for a file.</summary>
    public async Task<List<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsConnected) return new();

        string uri = PathToUri(filePath);
        string content = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, ct) : "";

        // Open the document
        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = DetectLanguage(filePath), version = 1, text = content }
        }, ct);

        // Wait briefly for server to process
        await Task.Delay(500, ct);

        // Request diagnostics via pull diagnostics or wait for push
        // Most servers push diagnostics after didOpen — we'll collect them from the response reader
        return new List<LspDiagnostic>(); // Diagnostics come via notifications
    }

    /// <summary>Get hover info for a position in a file.</summary>
    public async Task<string> GetHoverInfoAsync(string filePath, int line, int character, CancellationToken ct = default)
    {
        if (!IsConnected) return "";

        try
        {
            var result = await SendRequestAsync("textDocument/hover", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            }, ct);

            if (result.TryGetProperty("contents", out var contents))
            {
                if (contents.ValueKind == JsonValueKind.String)
                    return contents.GetString() ?? "";
                if (contents.TryGetProperty("value", out var val))
                    return val.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    /// <summary>Get completions at a position in a file.</summary>
    public async Task<List<string>> GetCompletionsAsync(string filePath, int line, int character, CancellationToken ct = default)
    {
        if (!IsConnected) return new();

        try
        {
            var result = await SendRequestAsync("textDocument/completion", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            }, ct);

            var items = new List<string>();
            JsonElement itemsArray;

            if (result.TryGetProperty("items", out itemsArray) || result.ValueKind == JsonValueKind.Array)
            {
                var arr = result.ValueKind == JsonValueKind.Array ? result : itemsArray;
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("label", out var label))
                        items.Add(label.GetString() ?? "");
                    if (items.Count >= 20) break;
                }
            }
            return items;
        }
        catch { return new(); }
    }

    /// <summary>Get definition location for a symbol.</summary>
    public async Task<string> GetDefinitionAsync(string filePath, int line, int character, CancellationToken ct = default)
    {
        if (!IsConnected) return "";

        try
        {
            var result = await SendRequestAsync("textDocument/definition", new
            {
                textDocument = new { uri = PathToUri(filePath) },
                position = new { line, character }
            }, ct);

            if (result.ValueKind == JsonValueKind.Array)
            {
                foreach (var loc in result.EnumerateArray())
                {
                    string uri = loc.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                    int startLine = 0, startChar = 0;
                    if (loc.TryGetProperty("range", out var range) && range.TryGetProperty("start", out var start))
                    {
                        startLine = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                        startChar = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
                    }
                    return $"{UriToPath(uri)}:{startLine + 1}:{startChar + 1}";
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>Stop the language server.</summary>
    public void StopServer()
    {
        _initialized = false;
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                // Try graceful shutdown
                try
                {
                    SendNotificationAsync("shutdown", new { }, CancellationToken.None).Wait(2000);
                    SendNotificationAsync("exit", new { }, CancellationToken.None).Wait(1000);
                }
                catch { }

                if (!_serverProcess.HasExited)
                {
                    try { _serverProcess.Kill(true); } catch { }
                }
            }
            _serverProcess?.Dispose();
            _serverProcess = null;
            _writer = null;
            _reader = null;
        }
        catch { }
    }

    // ─── LSP Protocol Helpers ───

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonElement>();

        lock (_lock)
            _pendingRequests[id] = tcs;

        var message = new { jsonrpc = "2.0", id, method, @params = parameters };
        await SendMessageAsync(message, ct);

        // Wait for response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        finally
        {
            lock (_lock)
                _pendingRequests.Remove(id);
        }
    }

    private async Task SendNotificationAsync(string method, object parameters, CancellationToken ct)
    {
        var message = new { jsonrpc = "2.0", method, @params = parameters };
        await SendMessageAsync(message, ct);
    }

    private async Task SendMessageAsync(object message, CancellationToken ct)
    {
        if (_writer == null) return;

        string json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        string header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";

        await _writer.WriteAsync(header.AsMemory(), ct);
        await _writer.WriteAsync(json.AsMemory(), ct);
        await _writer.FlushAsync(ct);
    }

    private async Task ReadResponsesAsync(CancellationToken ct)
    {
        if (_reader == null) return;

        try
        {
            while (!ct.IsCancellationRequested && _serverProcess != null && !_serverProcess.HasExited)
            {
                // Read header
                string? headerLine;
                int contentLength = 0;

                while ((headerLine = await _reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrEmpty(headerLine)) break; // Empty line = end of headers
                    if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(headerLine["Content-Length:".Length..].Trim());
                    }
                }

                if (contentLength <= 0) continue;

                // Read body
                char[] buffer = new char[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int read = await _reader.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                string body = new(buffer, 0, totalRead);

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    // Check if it's a response to a request
                    if (root.TryGetProperty("id", out var idProp))
                    {
                        int id = idProp.GetInt32();
                        TaskCompletionSource<JsonElement>? tcs;
                        lock (_lock)
                            _pendingRequests.TryGetValue(id, out tcs);

                        if (tcs != null)
                        {
                            if (root.TryGetProperty("result", out var result))
                                tcs.SetResult(result.Clone());
                            else if (root.TryGetProperty("error", out var error))
                                tcs.SetException(new Exception(error.ToString()));
                            else
                                tcs.SetResult(default);
                        }
                    }
                    // Notifications (diagnostics, etc.) are ignored for now
                }
                catch { /* ignore malformed messages */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* reader closed */ }
    }

    private static string PathToUri(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = '/' + normalized;
        return $"file://{normalized}";
    }

    private static string UriToPath(string uri)
    {
        if (uri.StartsWith("file:///"))
            return uri["file:///".Length..].Replace('/', '\\');
        if (uri.StartsWith("file://"))
            return uri["file://".Length..].Replace('/', '\\');
        return uri;
    }

    private static string DetectLanguage(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".ts" => "typescript",
            ".tsx" => "typescriptreact",
            ".js" => "javascript",
            ".jsx" => "javascriptreact",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            ".c" => "c",
            ".h" or ".hpp" => "cpp",
            _ => "plaintext",
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}

public class LspDiagnostic
{
    public int Line { get; set; }
    public int Character { get; set; }
    public string Severity { get; set; } = ""; // error, warning, info, hint
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";
}
