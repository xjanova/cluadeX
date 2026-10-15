using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Browser;

/// <summary>One console line (or page error) as the agent sees it.</summary>
public sealed class ConsoleEntry
{
    public long Seq { get; init; }
    /// <summary>log, info, warn, error or debug.</summary>
    public string Level { get; init; } = "log";
    /// <summary>console, exception, rejection, network, browser.</summary>
    public string Source { get; init; } = "console";
    public string Text { get; init; } = "";
    public string? Url { get; init; }
    /// <summary>1-based line and column, when known.</summary>
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Stack { get; init; }
    /// <summary>How many identical lines in a row this entry stands for (shown as ×N).</summary>
    public int Count { get; set; } = 1;
    public DateTime TimeUtc { get; init; } = DateTime.UtcNow;

    public bool IsError => Level == "error";
    public bool IsWarning => Level == "warn";

    internal long Weight => 64 + Text.Length * 2L + (Url?.Length ?? 0) * 2L + (Stack?.Length ?? 0) * 2L;

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(Level).Append(']');
        if (Source is "exception" or "rejection" or "network") sb.Append(' ').Append(Source);
        sb.Append(' ').Append(Text);
        if (!string.IsNullOrEmpty(Url))
        {
            sb.Append("  (").Append(ShortUrl(Url));
            if (Line is int l) sb.Append(':').Append(l);
            if (Column is int c) sb.Append(':').Append(c);
            sb.Append(')');
        }
        if (Count > 1) sb.Append("  ×").Append(Count);
        if (!string.IsNullOrEmpty(Stack)) sb.Append('\n').Append(Stack);
        return sb.ToString();
    }

    internal static string ShortUrl(string url)
    {
        if (url.Length <= 120) return url;
        return url[..60] + "…" + url[^50..];
    }
}

/// <summary>
/// The page's console, bounded: at most <see cref="MaxEntries"/> entries and <see cref="MaxBytes"/> of text,
/// oldest dropped first, identical consecutive lines folded into one entry with a count — a game that logs every
/// frame cannot grow it without bound.
///
/// Engine events only <see cref="EnqueueRaw"/> the raw JSON (cheap, on the engine thread); parsing happens
/// lazily in <see cref="Pump"/> on whichever thread reads. Each reader keeps its own cursor, so "since last time
/// I looked" means the same thing to the agent and to the preview pane without them stealing from each other.
/// </summary>
public sealed class ConsoleBuffer
{
    public const int MaxEntries = 2000;
    public const long MaxBytes = 256 * 1024;
    /// <summary>Raw events waiting to be parsed; beyond this the oldest raw events are dropped and counted.</summary>
    public const int MaxRawPending = 20_000;
    private const int MaxTextChars = 4000;

    private readonly ConcurrentQueue<(string Method, string Json)> _raw = new();
    private int _rawCount;
    private long _droppedRaw;

    private readonly object _lock = new();
    private readonly LinkedList<ConsoleEntry> _entries = new();
    private long _bytes;
    private long _seq;
    private long _evicted;
    private long _navigationSeq;      // first seq of the current page
    private long _clearedSeq;         // entries at or below this are hidden (browser_console clear=true)
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);

    /// <summary>Total entries ever added (after folding) — drives the pane's refresh.</summary>
    public long Version { get { lock (_lock) return _seq; } }
    public long DroppedRaw => Interlocked.Read(ref _droppedRaw);
    public long Evicted { get { lock (_lock) return _evicted; } }

    /// <summary>Engine thread: keep the event for later. Never parses, never blocks.</summary>
    public void EnqueueRaw(string method, string json)
    {
        _raw.Enqueue((method, json));
        if (Interlocked.Increment(ref _rawCount) > MaxRawPending && _raw.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _rawCount);
            Interlocked.Increment(ref _droppedRaw);
        }
    }

    private const string NavigationMarker = "@nav";
    private const string NoteMarker = "@note";

    /// <summary>Engine thread: the main frame committed a new document — "since navigation" starts at the next
    /// entry. Queued in-stream, so it lands exactly between the old page's lines and the new page's.</summary>
    public void EnqueueNavigation() => EnqueueRaw(NavigationMarker, "");

    /// <summary>Engine thread: a line that did not come from the page (a dialog dismissed, a crash).</summary>
    public void EnqueueNote(string level, string source, string text, string? url = null) =>
        EnqueueRaw(NoteMarker, JsonSerializer.Serialize(new { level, source, text, url }));

    private static ConsoleEntry? ParseNote(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;
        return new ConsoleEntry
        {
            Level = Str(p, "level") ?? "info", Source = Str(p, "source") ?? "browser",
            Text = Cap(Str(p, "text") ?? ""), Url = Str(p, "url"),
        };
    }

    /// <summary>Mark where the current page begins, from a reading thread.</summary>
    public void MarkNavigation()
    {
        Pump();
        lock (_lock) _navigationSeq = _seq + 1;
    }

    /// <summary>Parse everything enqueued so far. Safe from any thread.</summary>
    public void Pump()
    {
        if (_raw.IsEmpty) return;
        lock (_lock)
        {
            while (_raw.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _rawCount);
                if (item.Method == NavigationMarker) { _navigationSeq = _seq + 1; continue; }
                ConsoleEntry? e;
                try { e = item.Method == NoteMarker ? ParseNote(item.Json) : Parse(item.Method, item.Json); }
                catch { e = null; }
                if (e != null) AddLocked(e);
            }
        }
    }

    private void AddLocked(ConsoleEntry e)
    {
        var last = _entries.Last?.Value;
        if (last != null && last.Seq > _clearedSeq && last.Level == e.Level && last.Source == e.Source && last.Text == e.Text
            && last.Url == e.Url && last.Line == e.Line && last.Column == e.Column)
        {
            last.Count++;
            return;
        }
        var entry = new ConsoleEntry
        {
            Seq = ++_seq, Level = e.Level, Source = e.Source, Text = e.Text, Url = e.Url,
            Line = e.Line, Column = e.Column, Stack = e.Stack, TimeUtc = e.TimeUtc,
        };
        _entries.AddLast(entry);
        _bytes += entry.Weight;
        while (_entries.Count > MaxEntries || (_bytes > MaxBytes && _entries.Count > 1))
        {
            var first = _entries.First!.Value;
            _bytes -= first.Weight;
            _entries.RemoveFirst();
            _evicted++;
        }
    }

    /// <summary>Hide everything logged so far (browser_console clear=true).</summary>
    public void Clear()
    {
        Pump();
        lock (_lock) _clearedSeq = _seq;
    }

    /// <summary>
    /// Entries for <paramref name="reader"/>. <paramref name="since"/>: "navigation" (the current page),
    /// "last" (after this reader's previous read), "start" (all kept). The reader's cursor moves to the newest
    /// entry either way. <paramref name="omitted"/> counts matching entries left out by <paramref name="limit"/>
    /// (the newest are kept).
    /// </summary>
    public List<ConsoleEntry> Read(string reader, string since, string level, string? pattern, int limit,
        out int omitted, out bool evictedSome)
    {
        Pump();
        System.Text.RegularExpressions.Regex? rx = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try { rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch { rx = null; }
        }
        // Snapshot under the lock; match OUTSIDE it — a model's pathological pattern ("(a+)+$") must never hold the
        // lock the preview pane's UI-thread refresh takes.
        List<ConsoleEntry> candidates;
        lock (_lock)
        {
            long from = since switch
            {
                "last" => _cursors.TryGetValue(reader, out var c) ? c + 1 : _navigationSeq,
                "start" => 0,
                _ => _navigationSeq,
            };
            from = Math.Max(from, _clearedSeq + 1);
            evictedSome = _entries.First != null && _entries.First.Value.Seq > from && _evicted > 0;
            candidates = new List<ConsoleEntry>();
            foreach (var e in _entries)
                if (e.Seq >= from && LevelMatches(e, level)) candidates.Add(e);
            _cursors[reader] = _seq;
        }

        var matches = candidates;
        if (!string.IsNullOrEmpty(pattern))
        {
            matches = new List<ConsoleEntry>();
            var budget = System.Diagnostics.Stopwatch.StartNew();
            foreach (var e in candidates)
            {
                bool hit;
                // After 1 s of regex work in total the rest is matched as plain text (a bounded call, always).
                if (rx != null && budget.ElapsedMilliseconds < MaxPatternMs)
                {
                    try { hit = rx.IsMatch(e.Text) || (e.Url != null && rx.IsMatch(e.Url)); }
                    catch { hit = e.Text.Contains(pattern, StringComparison.OrdinalIgnoreCase); }
                }
                else hit = e.Text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (hit) matches.Add(e);
            }
            PatternTimedOut = rx != null && budget.ElapsedMilliseconds >= MaxPatternMs;
        }
        limit = Math.Max(1, limit);
        omitted = Math.Max(0, matches.Count - limit);
        return omitted > 0 ? matches.GetRange(matches.Count - limit, limit) : matches;
    }

    /// <summary>Total time one read spends on regex matching before falling back to plain text.</summary>
    public const int MaxPatternMs = 1000;

    /// <summary>The last <see cref="Read"/> ran out of regex time and matched the rest as plain text.</summary>
    public bool PatternTimedOut { get; private set; }

    /// <summary>Errors / warnings of the current page (for browser_open's summary).</summary>
    public (int Errors, int Warnings, List<ConsoleEntry> FirstErrors) CountSinceNavigation(int firstN = 5)
    {
        Pump();
        lock (_lock)
        {
            int errors = 0, warnings = 0;
            var first = new List<ConsoleEntry>();
            long from = Math.Max(_navigationSeq, _clearedSeq + 1);
            foreach (var e in _entries)
            {
                if (e.Seq < from) continue;
                if (e.IsError) { errors += e.Count; if (first.Count < firstN) first.Add(e); }
                else if (e.IsWarning) warnings += e.Count;
            }
            return (errors, warnings, first);
        }
    }

    /// <summary>The newest <paramref name="n"/> entries (the pane's mini console).</summary>
    public List<ConsoleEntry> Tail(int n)
    {
        Pump();
        lock (_lock)
        {
            var list = new List<ConsoleEntry>(Math.Min(n, _entries.Count));
            for (var node = _entries.Last; node != null && list.Count < n; node = node.Previous)
                if (node.Value.Seq > _clearedSeq) list.Add(node.Value);
            list.Reverse();
            return list;
        }
    }

    private static bool LevelMatches(ConsoleEntry e, string level) => level switch
    {
        "error" => e.IsError,
        "warn" => e.IsError || e.IsWarning,
        _ => true,
    };

    // ─────────────────────────── parsing ───────────────────────────

    internal static ConsoleEntry? Parse(string method, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;
        switch (method)
        {
            case "Runtime.consoleAPICalled":
            {
                string type = Str(p, "type") ?? "log";
                string level = type switch
                {
                    "warning" => "warn",
                    "error" or "assert" => "error",
                    "debug" => "debug",
                    "info" => "info",
                    _ => "log",
                };
                var sb = new StringBuilder();
                if (p.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var a in args.EnumerateArray())
                    {
                        if (i++ > 0) sb.Append(' ');
                        sb.Append(RemoteObjectText(a));
                        if (sb.Length > MaxTextChars) break;
                    }
                }
                if (type == "assert" && sb.Length == 0) sb.Append("Assertion failed");
                var (url, line, col) = TopFrame(p);
                return new ConsoleEntry { Level = level, Source = "console", Text = Cap(sb.ToString()), Url = url, Line = line, Column = col };
            }
            case "Runtime.exceptionThrown":
            {
                if (!p.TryGetProperty("exceptionDetails", out var d)) return null;
                string text = Str(d, "text") ?? "Uncaught";
                string? description = null;
                if (d.TryGetProperty("exception", out var ex))
                    description = Str(ex, "description") ?? (ex.TryGetProperty("value", out var v) ? v.ToString() : null);
                string message = description != null ? FirstLine(description) : "";
                bool rejection = text.Contains("(in promise)", StringComparison.OrdinalIgnoreCase);
                string full = message.Length > 0 && !text.Contains(message, StringComparison.Ordinal) ? $"{text} {message}" : text;
                string? url = Str(d, "url");
                int? line = d.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : null;
                int? col = d.TryGetProperty("columnNumber", out var cn) && cn.TryGetInt32(out var c) ? c + 1 : null;
                string? stack = null;
                if (d.TryGetProperty("stackTrace", out var st)) stack = StackText(st, 3);
                if (url == null && d.TryGetProperty("stackTrace", out var st2)) (url, line, col) = TopFrame(new JsonElementHolder(st2));
                return new ConsoleEntry
                {
                    Level = "error", Source = rejection ? "rejection" : "exception", Text = Cap(full),
                    Url = url, Line = line, Column = col, Stack = stack,
                };
            }
            case "Log.entryAdded":
            {
                if (!p.TryGetProperty("entry", out var e)) return null;
                string lvl = Str(e, "level") switch
                {
                    "error" => "error",
                    "warning" => "warn",
                    "info" => "info",
                    "verbose" => "debug",
                    _ => "log",
                };
                string src = Str(e, "source") ?? "browser";
                // Network 404s, CSP, mixed content, deprecations — "javascript"/"console-api" duplicate Runtime events.
                if (src is "javascript" or "console-api") return null;
                int? line = e.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : null;
                return new ConsoleEntry
                {
                    Level = lvl, Source = src == "network" ? "network" : "browser",
                    Text = Cap(Str(e, "text") ?? ""), Url = Str(e, "url"), Line = line,
                };
            }
            default:
                return null;
        }
    }

    private readonly struct JsonElementHolder
    {
        public readonly JsonElement StackTrace;
        public JsonElementHolder(JsonElement st) => StackTrace = st;
    }

    private static (string? Url, int? Line, int? Col) TopFrame(JsonElementHolder h)
    {
        if (h.StackTrace.ValueKind == JsonValueKind.Object && h.StackTrace.TryGetProperty("callFrames", out var frames)
            && frames.ValueKind == JsonValueKind.Array)
            foreach (var f in frames.EnumerateArray())
            {
                string? url = Str(f, "url");
                if (string.IsNullOrEmpty(url)) continue;
                int? line = f.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : null;
                int? col = f.TryGetProperty("columnNumber", out var cn) && cn.TryGetInt32(out var c) ? c + 1 : null;
                return (url, line, col);
            }
        return (null, null, null);
    }

    private static (string? Url, int? Line, int? Col) TopFrame(JsonElement p) =>
        p.TryGetProperty("stackTrace", out var st) ? TopFrame(new JsonElementHolder(st)) : (null, null, null);

    private static string? StackText(JsonElement st, int max)
    {
        if (!st.TryGetProperty("callFrames", out var frames) || frames.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        int n = 0;
        foreach (var f in frames.EnumerateArray())
        {
            if (n++ >= max) break;
            string fn = Str(f, "functionName") is { Length: > 0 } s ? s : "(anonymous)";
            string url = Str(f, "url") ?? "";
            int line = f.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : 0;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("    at ").Append(fn).Append(" (").Append(ConsoleEntry.ShortUrl(url)).Append(':').Append(line).Append(')');
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>A CDP RemoteObject as console text: primitives by value, objects by description / preview.</summary>
    internal static string RemoteObjectText(JsonElement o)
    {
        string type = Str(o, "type") ?? "";
        if (o.TryGetProperty("value", out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Null => "null",
                _ => v.GetRawText(),
            };
        }
        if (type == "undefined") return "undefined";
        if (Str(o, "unserializableValue") is { } u) return u;
        if (o.TryGetProperty("preview", out var pv) && pv.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
        {
            bool array = Str(pv, "subtype") == "array";
            var sb = new StringBuilder(array ? "[" : "{");
            int i = 0;
            foreach (var prop in props.EnumerateArray())
            {
                if (i++ > 0) sb.Append(", ");
                if (!array) sb.Append(Str(prop, "name")).Append(": ");
                sb.Append(Str(prop, "value") ?? Str(prop, "type"));
            }
            if (pv.TryGetProperty("overflow", out var of) && of.ValueKind == JsonValueKind.True) sb.Append(", …");
            sb.Append(array ? "]" : "}");
            return sb.ToString();
        }
        return Str(o, "description") ?? type;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private static string Cap(string s) => s.Length <= MaxTextChars ? s : s[..MaxTextChars] + $"…(+{s.Length - MaxTextChars} chars)";
}
