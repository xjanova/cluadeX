using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// Serves a plain HTML/CSS/JS project to the UI Studio page on http://localhost:&lt;free port&gt;/ — no Node, no
/// build. HTML pages are served with <c>data-cx-src</c> locators added (<see cref="SourceLocator.InstrumentHtml"/>)
/// so every element maps to its exact line; every other file is served as it is on disk.
///
/// Safety: it listens on localhost only (HttpListener "localhost" prefix — never "+" or "*"), every path is
/// resolved inside the project root, and dot-files/folders (.env, .git, .cluadex) and node_modules are never served.
/// Responses carry no-store so a reload always shows what is on disk now.
/// </summary>
public sealed class StudioStaticServer : IDisposable
{
    private readonly string _root;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public string BaseUrl => $"http://localhost:{Port}/";
    public bool IsRunning => _listener?.IsListening == true;

    public StudioStaticServer(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public void Start()
    {
        if (IsRunning) return;
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(listener, _cts.Token));
                return;
            }
            catch (HttpListenerException ex)
            {
                last = ex;   // the port was taken between FreePort and Start: try another
                try { listener.Close(); } catch { }
            }
        }
        throw new InvalidOperationException("Could not start the local preview server: " + last?.Message, last);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }   // stopped
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            if (ctx.Request.HttpMethod is not ("GET" or "HEAD")) { Send(res, 405, "text/plain", "Method not allowed"u8.ToArray()); return; }

            string? full = MapPath(ctx.Request.Url?.AbsolutePath ?? "/");
            if (full == null) { Send(res, 404, "text/plain", "Not found"u8.ToArray()); return; }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            byte[] body;
            if (ext is ".html" or ".htm")
            {
                var file = CluadeX.Helpers.TextFileCodec.Read(full);
                string rel = full[_root.Length..];
                body = Encoding.UTF8.GetBytes(file.IsValidUtf8 ? SourceLocator.InstrumentHtml(file.Text, rel) : file.Text);
                Send(res, 200, "text/html; charset=utf-8", body, ctx.Request.HttpMethod == "HEAD");
                return;
            }
            // Everything else is streamed from disk (a video or a big image never sits in memory whole).
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            res.StatusCode = 200;
            res.ContentType = MimeFor(ext);
            res.ContentLength64 = fs.Length;
            if (ctx.Request.HttpMethod != "HEAD") fs.CopyTo(res.OutputStream);
            res.OutputStream.Close();
        }
        catch (Exception ex)
        {
            try { Send(res, 500, "text/plain", Encoding.UTF8.GetBytes("Server error: " + ex.GetType().Name)); } catch { }
        }
    }

    /// <summary>The file for a URL path, or null when it is outside the root, hidden, or missing.
    /// "/" and "/dir/" serve index.html; "/about" serves about.html when it exists (pretty URLs).</summary>
    internal string? MapPath(string urlPath)
    {
        string path = Uri.UnescapeDataString(urlPath).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (path.Contains('\0')) return null;
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (seg.StartsWith('.') || seg.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return null;

        string full;
        try { full = Path.GetFullPath(Path.Combine(_root, path)); }
        catch { return null; }
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full + Path.DirectorySeparatorChar, _root, StringComparison.OrdinalIgnoreCase))
            return null;

        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        else if (!File.Exists(full) && Path.GetExtension(full).Length == 0 && File.Exists(full + ".html")) full += ".html";
        return File.Exists(full) ? full : null;
    }

    private static void Send(HttpListenerResponse res, int status, string type, byte[] body, bool headOnly = false)
    {
        res.StatusCode = status;
        res.ContentType = type;
        res.ContentLength64 = body.Length;
        if (!headOnly) res.OutputStream.Write(body, 0, body.Length);
        res.OutputStream.Close();
    }

    internal static string MimeFor(string ext) => ext switch
    {
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }
}
