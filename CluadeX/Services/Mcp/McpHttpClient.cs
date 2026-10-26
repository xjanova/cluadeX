using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Mcp;

/// <summary>The server forgot our session (HTTP 404 on a request that carried its Mcp-Session-Id). The spec's answer
/// is a fresh initialize; the caller decides whether to do that or report the server as gone.</summary>
public sealed class McpSessionExpiredException : Exception
{
    public McpSessionExpiredException() : base("the MCP server's session expired") { }
}

/// <summary>The server refused our credentials (HTTP 401 / 403) — a wrong, revoked or missing key. Never retried.</summary>
public sealed class McpUnauthorizedException : Exception
{
    public McpUnauthorizedException(string message) : base(message) { }
}

/// <summary>
/// MCP's Streamable HTTP transport, client side: one JSON-RPC message (or batch) per POST, the answer as JSON or as an
/// SSE stream — the server picks, so both are read. Used by the MCP list's url entries (<see cref="McpHttpTransport"/>)
/// and by the connector relay that turns it back into stdio for BrainX (<c>CluadeX.exe --connector xdreamer</c>).
///
/// Raw JSON text in, raw JSON texts out: the relay passes messages through untouched, and only the transport needs to
/// understand them. Sessions (Mcp-Session-Id) and the negotiated MCP-Protocol-Version are carried automatically.
/// No retries here — a POST that may have run a paid tool must not run twice behind the caller's back.
/// </summary>
public sealed class McpHttpClient : IDisposable
{
    /// <summary>A ceiling on one answer, so a server cannot exhaust memory. Images come back base64, hence generous.</summary>
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public McpHttpClient(Uri endpoint, IReadOnlyDictionary<string, string>? headers = null, HttpMessageHandler? handler = null)
    {
        if (!IsAllowedEndpoint(endpoint, out string? why)) throw new ArgumentException(why, nameof(endpoint));
        _endpoint = endpoint;
        _headers = headers ?? new Dictionary<string, string>();
        // No HttpClient.Timeout: it is global and would cut a long SSE answer. Every call is bounded by its own token.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-MCP");
    }

    public Uri Endpoint => _endpoint;

    /// <summary>The server's session id from initialize (null: the server keeps none — legal).</summary>
    public string? SessionId { get; private set; }

    /// <summary>The protocol version the server chose in its initialize answer; sent on every later request.</summary>
    public string? ProtocolVersion { get; private set; }

    /// <summary>
    /// https anywhere; plain http only to this PC (a key sent in clear over the internet is a key given away).
    /// </summary>
    public static bool IsAllowedEndpoint(Uri? uri, out string? why)
    {
        why = null;
        if (uri == null || !uri.IsAbsoluteUri) { why = "the url is not an absolute http(s) address"; return false; }
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return true;
        why = uri.Scheme == Uri.UriSchemeHttp
            ? $"plain http is only allowed to this PC (localhost) — use https://{uri.Host}{uri.PathAndQuery}"
            : $"the url must be https (got '{uri.Scheme}')";
        return false;
    }

    /// <summary>
    /// POST one message (request, notification, response or batch) and return the JSON-RPC messages the server sent
    /// back, in order — none for a notification (202). Throws <see cref="McpSessionExpiredException"/>,
    /// <see cref="McpUnauthorizedException"/>, or <see cref="HttpRequestException"/> with the server's own words.
    /// </summary>
    public async Task<IReadOnlyList<string>> SendAsync(string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (k, v) in _headers) req.Headers.TryAddWithoutValidation(k, v);
        string? sentSession = SessionId;
        if (sentSession != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sentSession);
        if (ProtocolVersion != null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

        // ResponseHeadersRead: an SSE answer is streamed, not buffered to the end before we may look at it.
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.Headers.TryGetValues("Mcp-Session-Id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } sid)
            SessionId = sid;

        if (!res.IsSuccessStatusCode)
        {
            string body = await ReadCappedAsync(res, 2000, ct).ConfigureAwait(false);
            string said = ErrorText(body);
            if (res.StatusCode == HttpStatusCode.NotFound && sentSession != null)
            {
                SessionId = null;
                throw new McpSessionExpiredException();
            }
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new McpUnauthorizedException($"the server refused the key (HTTP {(int)res.StatusCode}){(said.Length > 0 ? ": " + said : "")}");
            throw new HttpRequestException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}{(said.Length > 0 ? " — " + said : "")}", null, res.StatusCode);
        }

        var messages = new List<string>();
        if (res.StatusCode == HttpStatusCode.Accepted || res.Content.Headers.ContentLength == 0) return messages;
        bool sse = string.Equals(res.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (sse)
        {
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            foreach (var data in await ReadEventsAsync(reader, ct).ConfigureAwait(false)) Split(data, messages);
        }
        else
        {
            Split(await ReadCappedAsync(res, MaxResponseBytes, ct).ConfigureAwait(false), messages);
        }
        foreach (var m in messages) NoteProtocolVersion(m);
        return messages;
    }

    /// <summary>Tell the server we are done with the session (best effort — a server that keeps none answers 405).</summary>
    public async Task CloseAsync()
    {
        if (SessionId is not { } sid) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
            foreach (var (k, v) in _headers) req.Headers.TryAddWithoutValidation(k, v);
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
            using var _ = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch { }
        SessionId = null;
    }

    /// <summary>The data of each SSE event, in order (multi-line data joined with \n; comments, ids and keep-alives
    /// skipped). Stops at the end of the stream or past <see cref="MaxResponseBytes"/>.</summary>
    public static async Task<List<string>> ReadEventsAsync(TextReader reader, CancellationToken ct)
    {
        var events = new List<string>();
        var data = new StringBuilder();
        long seen = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            seen += line.Length + 1;
            if (seen > MaxResponseBytes) throw new HttpRequestException($"the answer is larger than {MaxResponseBytes / (1024 * 1024)} MB");
            if (line.Length == 0)
            {
                if (data.Length > 0) { events.Add(data.ToString()); data.Clear(); }
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // event:, id:, retry:, ": comment"
            string chunk = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
            if (data.Length > 0) data.Append('\n');
            data.Append(chunk);
        }
        if (data.Length > 0) events.Add(data.ToString());
        return events;
    }

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A body that is one message stays one; a batch array becomes its elements. Each comes out on ONE line
    /// (a pretty-printed answer would break a newline-framed stdio relay). Non-JSON is dropped.</summary>
    public static void Split(string body, List<string> into)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object) into.Add(JsonSerializer.Serialize(el, Compact));
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object) into.Add(JsonSerializer.Serialize(doc.RootElement, Compact));
        }
        catch (JsonException) { }
    }

    private void NoteProtocolVersion(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
                && r.TryGetProperty("serverInfo", out _))
                ProtocolVersion = v.GetString();
        }
        catch (JsonException) { }
    }

    /// <summary>The words worth showing from an error body: a JSON-RPC error's message, an {"error": …} message,
    /// else the start of the text.</summary>
    public static string ErrorText(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        string t = body.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage res, int max, CancellationToken ct)
    {
        try
        {
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buf.Length + n > max)
                {
                    if (max >= MaxResponseBytes) throw new HttpRequestException($"the answer is larger than {MaxResponseBytes / (1024 * 1024)} MB");
                    buf.Write(chunk, 0, (int)(max - buf.Length));
                    break;
                }
                buf.Write(chunk, 0, n);
            }
            return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
        }
        catch (HttpRequestException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }

    public void Dispose() => _http.Dispose();
}
