using System.Collections.Concurrent;
using System.Text.Json;

namespace CluadeX.Services.Browser;

/// <summary>One request the page made.</summary>
public sealed class NetworkEntry
{
    public long Seq { get; init; }
    public string RequestId { get; init; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    /// <summary>Document, Script, Image, Fetch, XHR, Stylesheet, …</summary>
    public string Type { get; set; } = "";
    public int? Status { get; set; }
    public string? StatusText { get; set; }
    public string? MimeType { get; set; }
    public long? EncodedBytes { get; set; }
    public double StartTs { get; set; }
    public double? EndTs { get; set; }
    public bool Failed { get; set; }
    public string? ErrorText { get; set; }
    public bool Canceled { get; set; }

    public bool IsProblem => Failed || Status >= 400;
    public int? DurationMs => EndTs is double end && end >= StartTs ? (int)Math.Round((end - StartTs) * 1000) : null;

    public string Format()
    {
        string status = Failed ? (Canceled ? "canceled" : "FAILED " + (ErrorText ?? "")) : Status?.ToString() ?? "pending";
        string size = EncodedBytes is long b ? (b >= 1024 ? $"{b / 1024.0:0.#} KB" : $"{b} B") : "";
        string ms = DurationMs is int d ? d + " ms" : "";
        return $"{Method} {status} {Type} {ConsoleEntry.ShortUrl(Url)} {size} {ms}".Replace("  ", " ").TrimEnd();
    }
}

/// <summary>
/// Requests since the page was opened, at most <see cref="MaxEntries"/> (oldest dropped). Like the console, the
/// engine thread only enqueues raw JSON; <see cref="Pump"/> parses on the reading thread.
/// </summary>
public sealed class NetworkLog
{
    public const int MaxEntries = 500;
    private const int MaxRawPending = 5000;

    private readonly ConcurrentQueue<(string Method, string Json)> _raw = new();
    private int _rawCount;
    private readonly object _lock = new();
    private readonly Dictionary<string, NetworkEntry> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<NetworkEntry> _order = new();
    private long _seq;
    private long _navigationSeq;

    public void EnqueueRaw(string method, string json)
    {
        _raw.Enqueue((method, json));
        if (Interlocked.Increment(ref _rawCount) > MaxRawPending && _raw.TryDequeue(out _))
            Interlocked.Decrement(ref _rawCount);
    }

    public void MarkNavigation()
    {
        Pump();
        lock (_lock) _navigationSeq = _seq + 1;
    }

    /// <summary>Engine/any thread: a new page starts loading — queued in-stream, before its document request.</summary>
    public void EnqueueNavigation() => EnqueueRaw("@nav", "");

    public void Pump()
    {
        if (_raw.IsEmpty) return;
        lock (_lock)
        {
            while (_raw.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _rawCount);
                if (item.Method == "@nav") { _navigationSeq = _seq + 1; continue; }
                try { Apply(item.Method, item.Json); } catch { /* one bad event never stops the rest */ }
            }
        }
    }

    private void Apply(string method, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;
        string id = ConsoleBuffer.Str(p, "requestId") ?? "";
        if (id.Length == 0) return;
        switch (method)
        {
            case "Network.requestWillBeSent":
            {
                if (!p.TryGetProperty("request", out var req)) return;
                // A redirect re-uses the id: the earlier hop is finished, a new entry starts.
                if (_byId.TryGetValue(id, out var prev) && p.TryGetProperty("redirectResponse", out var rr))
                {
                    prev.Status = rr.TryGetProperty("status", out var rs) && rs.TryGetInt32(out var s) ? s : prev.Status;
                    _byId.Remove(id);
                }
                var e = new NetworkEntry
                {
                    Seq = ++_seq, RequestId = id,
                    Method = ConsoleBuffer.Str(req, "method") ?? "GET",
                    Url = ConsoleBuffer.Str(req, "url") ?? "",
                    Type = ConsoleBuffer.Str(p, "type") ?? "",
                    StartTs = p.TryGetProperty("timestamp", out var ts) && ts.TryGetDouble(out var t) ? t : 0,
                };
                if (e.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) e.Url = e.Url[..Math.Min(e.Url.Length, 40)] + "…";
                _byId[id] = e;
                _order.AddLast(e);
                while (_order.Count > MaxEntries)
                {
                    var first = _order.First!.Value;
                    _order.RemoveFirst();
                    if (_byId.TryGetValue(first.RequestId, out var cur) && ReferenceEquals(cur, first)) _byId.Remove(first.RequestId);
                }
                break;
            }
            case "Network.responseReceived":
            {
                if (!_byId.TryGetValue(id, out var e) || !p.TryGetProperty("response", out var r)) return;
                if (r.TryGetProperty("status", out var st) && st.TryGetInt32(out var s)) e.Status = s;
                e.StatusText = ConsoleBuffer.Str(r, "statusText");
                e.MimeType = ConsoleBuffer.Str(r, "mimeType");
                if (ConsoleBuffer.Str(p, "type") is { Length: > 0 } type) e.Type = type;
                break;
            }
            case "Network.loadingFinished":
            {
                if (!_byId.TryGetValue(id, out var e)) return;
                if (p.TryGetProperty("encodedDataLength", out var len) && len.TryGetDouble(out var l)) e.EncodedBytes = (long)l;
                if (p.TryGetProperty("timestamp", out var ts) && ts.TryGetDouble(out var t)) e.EndTs = t;
                break;
            }
            case "Network.loadingFailed":
            {
                if (!_byId.TryGetValue(id, out var e)) return;
                e.Failed = true;
                e.ErrorText = ConsoleBuffer.Str(p, "errorText");
                e.Canceled = p.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True;
                if (p.TryGetProperty("timestamp", out var ts) && ts.TryGetDouble(out var t)) e.EndTs = t;
                break;
            }
        }
    }

    /// <summary>Requests of the current page: failures and 4xx/5xx first, then in order.</summary>
    public List<NetworkEntry> Read(string? filter, bool failedOnly, int limit, bool currentPageOnly, out int omitted)
    {
        Pump();
        lock (_lock)
        {
            var list = _order.Where(e => (!currentPageOnly || e.Seq >= _navigationSeq)
                                         && (string.IsNullOrEmpty(filter) || e.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                         && (!failedOnly || e.IsProblem))
                             .OrderByDescending(e => e.IsProblem).ThenBy(e => e.Seq).ToList();
            limit = Math.Max(1, limit);
            omitted = Math.Max(0, list.Count - limit);
            return list.Take(limit).ToList();
        }
    }

    /// <summary>Failed requests (network error or 4xx/5xx) of the current page.</summary>
    public int ProblemsSinceNavigation()
    {
        Pump();
        lock (_lock) return _order.Count(e => e.Seq >= _navigationSeq && e.IsProblem);
    }

    /// <summary>The main document's response status for <paramref name="loaderId"/> (CDP uses the loader id as
    /// the request id of a navigation), or the newest Document of the current page.</summary>
    public int? DocumentStatus(string? loaderId)
    {
        Pump();
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(loaderId) && _byId.TryGetValue(loaderId, out var e) && e.Status != null) return e.Status;
            for (var n = _order.Last; n != null; n = n.Previous)
                if (n.Value.Seq >= _navigationSeq && n.Value.Type == "Document" && n.Value.Status != null) return n.Value.Status;
            return null;
        }
    }

    /// <summary>Was any request (of the whole session) sent to a URL containing <paramref name="needle"/>?</summary>
    public bool AnyRequestTo(string needle)
    {
        Pump();
        lock (_lock) return _order.Any(e => e.Url.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
