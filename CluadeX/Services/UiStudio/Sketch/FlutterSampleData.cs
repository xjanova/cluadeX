using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>A request the app's code makes, and the sample reply the renderer answers it with while screens are drawn.</summary>
public sealed class SampleReply
{
    public string Method { get; init; } = "GET";
    /// <summary>The path as the code writes it: "/fortune/ai-pool"; "/users/*" for an interpolated segment.</summary>
    public string Path { get; init; } = "";
    /// <summary>What the code reads the reply into: "AiPool"; "[User]" for a list of them.</summary>
    public string Model { get; init; } = "";
    /// <summary>Where the request is written (1-based line).</summary>
    public string File { get; init; } = "";
    public int Line { get; init; }
    /// <summary>The reply as it comes over the wire — in the app's envelope when its code reads one.</summary>
    public JsonNode? Body { get; init; }
    /// <summary>The files that declare the models the reply is read into (what each key means: the AI reads them).</summary>
    public IReadOnlyList<string> ModelFiles { get; init; } = Array.Empty<string>();
    /// <summary>The owner's realistic values (the AI's file) changed this reply.</summary>
    public bool Overridden { get; init; }

    /// <summary>How the override file names the request: "GET /fortune/ai-pool".</summary>
    public string Key => Method + " " + Path;

    /// <summary>A regular expression for the end of a request's path (what comes before it is the base URL's).</summary>
    public string Pattern => PatternOf(Path);
    /// <summary>The fixed segments it names: the most specific reply wins.</summary>
    public int Weight => Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Count(s => s != "*");

    internal static string PatternOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s == "*" ? "[^/]+" : Regex.Escape(s));
        return "(^|/)" + string.Join("/", parts) + "/?$";
    }
}

/// <summary>What an app's screens are fed while Flutter draws them: a reply for each request its code makes, shaped the
/// way its code reads it.</summary>
public sealed class SampleData
{
    public List<SampleReply> Replies { get; } = new();
    /// <summary>Requests whose reply the code doesn't say how it reads (no sample for them: they keep failing).</summary>
    public List<SampleReply> Unread { get; } = new();
    /// <summary>The envelope the app's replies come in — its success flag and data keys — null when its code reads none.</summary>
    public string? SuccessKey { get; internal set; }
    public string? DataKey { get; internal set; }

    /// <summary>Replies the owner's override file changed.</summary>
    public int Overridden => Replies.Count(r => r.Overridden);
    /// <summary>Requests the override file names that the code doesn't make (or whose reply it doesn't read): left out.</summary>
    public List<string> Ignored { get; } = new();
    /// <summary>Values of the override file that kept the sample's ("GET /shop/stats /data/items/0/sold"): another kind
    /// of value than the code reads there.</summary>
    public List<string> Kept { get; } = new();
    /// <summary>Why the override file was not used (a key: studio.samples.badJson…); null when there is none or it was read.</summary>
    public string? OverridesError { get; internal set; }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The file the generated test reads: [{method, pattern, weight, body}, …].</summary>
    public string ToJson()
    {
        var all = new JsonArray();
        foreach (var r in Replies)
            all.Add(new JsonObject { ["method"] = r.Method, ["pattern"] = r.Pattern, ["weight"] = r.Weight, ["body"] = r.Body?.DeepClone() });
        return all.ToJsonString(Relaxed);
    }

    /// <summary>The replies the way the override file is written — <c>{ "GET /path": reply, … }</c> — one request a line.</summary>
    public string RepliesJson()
    {
        var sb = new System.Text.StringBuilder("{\n");
        for (int i = 0; i < Replies.Count; i++)
            sb.Append(JsonSerializer.Serialize(Replies[i].Key, Relaxed)).Append(": ").Append(Replies[i].Body?.ToJsonString(Relaxed) ?? "null").Append(i + 1 < Replies.Count ? ",\n" : "\n");
        return sb.Append('}').ToString();
    }

    /// <summary>A copy that shares the replies (they are never changed).</summary>
    internal SampleData Copy()
    {
        var c = new SampleData { SuccessKey = SuccessKey, DataKey = DataKey };
        c.Replies.AddRange(Replies);
        c.Unread.AddRange(Unread);
        return c;
    }
}

/// <summary>
/// Sample data for drawing a Flutter app's data screens. The renderer runs the app offline, so every request used to
/// fail and a screen that loads data drew its error state. Here the app's own code says what to answer:
/// <list type="bullet">
/// <item>its requests — <c>_api.get('/fortune/ai-pool')</c>, <c>dio.post('/x')</c>, <c>http.get(Uri.parse('…'))</c> with a
/// written path — and what the reply is read into (<c>AiPool.fromJson(_m(data))</c>, <c>Paged.parse(data, User.fromJson)</c>);</item>
/// <item>each model's shape, from how its <c>fromJson</c> reads the map: the keys, and what each one is — a number
/// (<c>TpFmt.toInt(j['n'])</c>, <c>as int</c>), a flag (<c>== true</c>), a date (<c>DateTime.tryParse</c>), a nested object (<c>_m(j['x'])</c>,
/// another model's <c>fromJson</c>), a list (<c>_list(j['keys']).map(AiKey.fromJson)</c>, a loop over it), a value it compares
/// with — following the app's own helpers (<c>_list</c>, <c>TpFmt.toInt</c>, json_serializable's <c>_$XFromJson</c>);</item>
/// <item>the envelope its client unwraps (<c>json['success']</c> + <c>json['data']</c>).</item>
/// </list>
/// The values are made up from the key names (a count, an amount, a Thai name, three list items, recent dates). A
/// request the code doesn't name keeps the old answer (400): nothing is ever sent to the network.
/// The owner can have them made realistic: the AI writes an override file (kept in the Studio's own store, never in the
/// app) that <see cref="For(string, string?, CancellationToken)"/> merges over the made-up replies (<see cref="Merge"/>).
/// </summary>
public static class FlutterSampleData
{
    public const string FileName = "sample_data.json";
    /// <summary>An override file bigger than this is not read (it is a few kB per request).</summary>
    public const int MaxOverridesBytes = 4_000_000;

    private static readonly Dictionary<string, (string Stamp, SampleData Data)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (SampleData Made, string Stamp, SampleData Data)> Merged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sample data of a project, read again only when its lib/ code changed (every save draws the screens
    /// again; the code that answers them rarely changes).</summary>
    public static SampleData For(string root, CancellationToken ct = default)
    {
        string stamp = Stamp(Path.Combine(root, "lib"));
        lock (Cache)
            if (Cache.TryGetValue(root, out var hit) && hit.Stamp == stamp) return hit.Data;
        var data = Build(root, DateTime.Now, ct);
        lock (Cache) Cache[root] = (stamp, data);
        return data;
    }

    /// <summary>The sample data with the owner's override file (<paramref name="overridesPath"/>, written by the AI) merged
    /// over the made-up replies. No file: the made-up ones. A file that can't be read: the made-up ones, with
    /// <see cref="SampleData.OverridesError"/> saying why.</summary>
    public static SampleData For(string root, string? overridesPath, CancellationToken ct = default)
    {
        var made = For(root, ct);
        string stamp = OverridesStamp(overridesPath);
        if (stamp.Length == 0) return made;
        lock (Merged)
            if (Merged.TryGetValue(overridesPath!, out var hit) && ReferenceEquals(hit.Made, made) && hit.Stamp == stamp) return hit.Data;
        var data = MergeFile(made, overridesPath!);
        lock (Merged) Merged[overridesPath!] = (made, stamp, data);
        return data;
    }

    /// <summary>The override file as it is now (size and last write), "" when there is none.</summary>
    public static string OverridesStamp(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return "";
            var f = new FileInfo(path);
            return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        }
        catch { return ""; }
    }

    private static SampleData MergeFile(SampleData made, string path)
    {
        string? error = null;
        JsonNode? node = null;
        try
        {
            if (new FileInfo(path).Length > MaxOverridesBytes) error = "studio.samples.tooBig";
            else node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException) { error = "studio.samples.badJson"; }
        catch { error = "studio.samples.unreadable"; }
        if (error == null && node is JsonObject o) return Merge(made, o);
        var copy = made.Copy();
        copy.OverridesError = error ?? "studio.samples.notObject";
        return copy;
    }

    /// <summary>
    /// The owner's realistic values over the made-up replies. <paramref name="overrides"/> names each request as
    /// <see cref="SampleReply.Key"/> does ("GET /shop/stats") and holds its reply, merged over the made-up one by
    /// <see cref="MergeValue"/>. A reply in the app's envelope (<c>{success, data}</c>) may be given without it: an object
    /// that names neither its flag nor its data key, or a list, is its data. A request the code doesn't make (or whose
    /// reply it doesn't read) is left out (<see cref="SampleData.Ignored"/>).
    /// </summary>
    public static SampleData Merge(SampleData made, JsonObject overrides)
    {
        var data = new SampleData { SuccessKey = made.SuccessKey, DataKey = made.DataKey };
        data.Unread.AddRange(made.Unread);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in made.Replies)
        {
            if (!overrides.TryGetPropertyValue(r.Key, out var over)) { data.Replies.Add(r); continue; }
            used.Add(r.Key);
            if (made.DataKey is { } dk && r.Body is JsonObject env && env.ContainsKey(dk) && env.All(kv => kv.Key == dk || kv.Key == made.SuccessKey)
                && (over is JsonArray || over is JsonObject o && !o.ContainsKey(dk) && (made.SuccessKey == null || !o.ContainsKey(made.SuccessKey))))
                over = new JsonObject { [dk] = over!.DeepClone() };
            var kept = new List<string>();
            var body = MergeValue(r.Body, over, kept);
            data.Kept.AddRange(kept.Select(k => r.Key + " " + (k.Length == 0 ? "/" : k)));
            data.Replies.Add(new SampleReply
            {
                Method = r.Method, Path = r.Path, Model = r.Model, File = r.File, Line = r.Line, ModelFiles = r.ModelFiles,
                Body = body, Overridden = !JsonNode.DeepEquals(body, r.Body),
            });
        }
        foreach (var kv in overrides)
            if (!used.Contains(kv.Key)) data.Ignored.Add(kv.Key);
        return data;
    }

    private static readonly Regex WholeNumber = new(@"^-?\d+$");
    /// <summary>The dates the samples are made with.</summary>
    private static readonly Regex MadeDate = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
    /// <summary>What Dart's DateTime.parse reads: a date, maybe a time, maybe a zone.</summary>
    private static readonly Regex DartDate = new(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}(:\d{2}(:\d{2}([.,]\d{1,9})?)?)?\s?(Z|z|[+-]\d{2}(:?\d{2})?)?)?$");
    /// <summary>A number written as text (the code reads it with int.parse / double.parse) — not a phone number's 0…</summary>
    private static readonly Regex NumberText = new(@"^-?(0|[1-9]\d*)(\.\d+)?$");
    private static readonly Regex IntText = new(@"^-?\d+$");
    private static readonly Regex DecimalText = new(@"^-?\d+(\.\d+)?$");

    /// <summary>
    /// One value of the owner's file over the made-up one, so it can't make the app's own code fail:
    /// <list type="bullet">
    /// <item>a map, key by key (a key the made-up reply hasn't is added: the code may read it, the AI saw the model);</item>
    /// <item>a list, item by item over the made-up item at that place (the last one past its end): it has as many items
    /// as the owner's list, none for []. A list that is empty in the samples stays empty (errors, failures);</item>
    /// <item>a plain value only of the same kind: text for text (a date stays a date, a number written as text stays one),
    /// a number for a number (whole where the sample is: the code may read an int; a whole number over a fraction is
    /// written as a fraction, 1500 → 1500.0, since the code may read a double), true / false for a flag;</item>
    /// <item>null stays null (a picture's address: there is no network to load it from).</item>
    /// </list>
    /// Anything else keeps the made-up value; where, is added to <paramref name="kept"/> ("/data/items/0/sold").
    /// </summary>
    public static JsonNode? MergeValue(JsonNode? made, JsonNode? over, List<string>? kept = null, string at = "")
    {
        switch (made)
        {
            case null:
                if (over != null) kept?.Add(at);
                return null;
            case JsonObject mo:
            {
                if (over is not JsonObject oo) { kept?.Add(at); return mo.DeepClone(); }
                var r = new JsonObject();
                foreach (var kv in mo) r[kv.Key] = oo.TryGetPropertyValue(kv.Key, out var ov) ? MergeValue(kv.Value, ov, kept, at + "/" + kv.Key) : kv.Value?.DeepClone();
                foreach (var kv in oo) if (!mo.ContainsKey(kv.Key)) r[kv.Key] = kv.Value?.DeepClone();
                return r;
            }
            case JsonArray ma:
            {
                if (over is not JsonArray oa) { kept?.Add(at); return ma.DeepClone(); }
                var r = new JsonArray();
                if (ma.Count == 0) { if (oa.Count > 0) kept?.Add(at); return r; }
                for (int i = 0; i < oa.Count; i++) r.Add(MergeValue(ma[Math.Min(i, ma.Count - 1)], oa[i], kept, at + "/" + i));
                return r;
            }
        }
        if (over is JsonValue v && Plain(made.AsValue(), v) is { } same) return same;
        kept?.Add(at);
        return made.DeepClone();
    }

    /// <summary>The owner's plain value when the code reads it the way it reads the made-up one (null: it doesn't).</summary>
    private static JsonNode? Plain(JsonValue made, JsonValue over)
    {
        JsonValueKind mk = made.GetValueKind(), ok = over.GetValueKind();
        switch (mk)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return ok is JsonValueKind.True or JsonValueKind.False ? over.DeepClone() : null;
            case JsonValueKind.Number:
            {
                if (ok != JsonValueKind.Number) return null;
                string m = made.ToJsonString(), o = over.ToJsonString();
                bool mWhole = WholeNumber.IsMatch(m), oWhole = WholeNumber.IsMatch(o);
                if (mWhole && !oWhole) return null;
                return !mWhole && oWhole ? JsonNode.Parse(o + ".0") : over.DeepClone();
            }
            case JsonValueKind.String:
            {
                if (ok != JsonValueKind.String) return null;
                string m = made.GetValue<string>(), o = over.GetValue<string>();
                if (MadeDate.IsMatch(m) && !DartDate.IsMatch(o)) return null;
                if (NumberText.IsMatch(m) && !(m.Contains('.') ? DecimalText : IntText).IsMatch(o)) return null;
                return over.DeepClone();
            }
            default:
                return null;
        }
    }

    /// <summary>The Dart files of a folder as they are now: how many, their sizes, the newest write.</summary>
    private static string Stamp(string lib)
    {
        try
        {
            if (!Directory.Exists(lib)) return "";
            long size = 0, newest = 0;
            int count = 0;
            foreach (var f in new DirectoryInfo(lib).EnumerateFiles("*.dart", SearchOption.AllDirectories))
            {
                count++;
                size += f.Length;
                newest = Math.Max(newest, f.LastWriteTimeUtc.Ticks);
            }
            return $"{count}:{size}:{newest}";
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    public static SampleData Build(string root, DateTime now, CancellationToken ct = default, int maxFiles = 1500)
    {
        var data = new SampleData();
        try
        {
            var code = Code.Load(Path.Combine(root, "lib"), maxFiles, ct);
            var reader = new Reader(code);
            var (okKey, okValue, dataKey) = Envelope(code);
            data.SuccessKey = okKey;
            data.DataKey = dataKey;
            var gen = new Gen(now);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rq in code.Requests())
            {
                ct.ThrowIfCancellationRequested();
                var shape = reader.Reply(rq, out var model, out bool raw);
                string path = rq.Path;
                if (shape == null)
                {
                    var (ul, _) = rq.Doc.LineCol(rq.Doc.T[rq.Call.NameTok].Pos);
                    data.Unread.Add(new SampleReply { Method = rq.Method, Path = path, File = rq.File, Line = ul });
                    continue;
                }
                if (!seen.Add(rq.Method + " " + SampleReply.PatternOf(path))) continue;
                JsonNode? body = gen.Value(shape, 0, 0);
                if (!raw && dataKey != null)
                    body = new JsonObject { [okKey ?? "success"] = okValue != null ? JsonValue.Create(okValue) : JsonValue.Create(true), [dataKey] = body };
                var (line, _) = rq.Doc.LineCol(rq.Doc.T[rq.Call.NameTok].Pos);
                data.Replies.Add(new SampleReply { Method = rq.Method, Path = path, Model = model, File = rq.File, Line = line, Body = body, ModelFiles = code.ModelFiles(shape) });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* whatever was read so far */ }
        return data;
    }

    /// <summary>The envelope the app's client unwraps: a map read for "data" (or result / payload) next to a success
    /// flag ("success", "ok", or "status" compared with a value).</summary>
    private static (string? Ok, string? OkValue, string? Data) Envelope(Code code)
    {
        foreach (var (_, d) in code.Files)
        {
            for (int i = 0; i + 3 < d.T.Count; i++)
            {
                if (d.T[i].Kind != DTok.Ident || !d.Is(i + 1, "[") || d.T[i + 2] is not { Kind: DTok.Str, Text: "data" or "result" or "payload" } || !d.Is(i + 3, "]")) continue;
                string v = d.T[i].Text;
                for (int k = Math.Max(0, i - 300); k < Math.Min(d.T.Count - 4, i + 300); k++)
                {
                    if (!d.IsIdent(k, v) || !d.Is(k + 1, "[") || d.T[k + 2] is not { Kind: DTok.Str, Text: "success" or "ok" or "status" } || !d.Is(k + 3, "]")) continue;
                    string key = d.T[k + 2].Text;
                    string? value = key == "status" && (d.Is(k + 4, "==")) && d.T[k + 5].Kind == DTok.Str ? d.T[k + 5].Text : key == "status" ? "success" : null;
                    return (key, value, d.T[i + 2].Text);
                }
            }
        }
        return (null, null, null);
    }

    // ───────────────────────────── the app's code ─────────────────────────────

    /// <summary>A request: where it is made (<see cref="Call"/>, the HTTP call), and when the path is a parameter of a
    /// helper of the app (<c>_get(String path)</c>), the helper and the call of it that names the path (<see cref="Site"/>).</summary>
    private sealed record Request(DartDoc Doc, string File, string Method, string Path, DartCall Call, bool Raw,
                                   Func? Helper = null, DartCall? Site = null);

    /// <summary>A function of the app: its file, the ( of its parameters, its parameters, its body and its return type.</summary>
    private sealed record Func(DartDoc Doc, int NameTok, int Open, List<(string Name, bool Named)> Params, int From, int To, string Returns);

    private sealed class Code
    {
        public readonly List<(string Path, DartDoc Doc)> Files = new();
        public readonly Dictionary<string, (DartDoc Doc, int Open)> Classes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Func> Functions = new(StringComparer.Ordinal);
        private readonly Dictionary<DartDoc, string> _paths = new();

        /// <summary>The files declaring the models a reply is read into, the reply's own model first.</summary>
        public List<string> ModelFiles(JShape shape)
        {
            var names = new List<string>();
            var seen = new HashSet<JShape>();
            void Walk(JShape s, int depth)
            {
                if (depth > 24 || !seen.Add(s)) return;
                if (s.ModelName is { } m && !names.Contains(m)) names.Add(m);
                foreach (var k in s.Order) Walk(s.Keys[k], depth + 1);
                if (s.Item != null) Walk(s.Item, depth + 1);
            }
            Walk(shape, 0);
            return names.Select(n => Classes.TryGetValue(n, out var c) ? _paths.GetValueOrDefault(c.Doc) : null).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Code Load(string lib, int max, CancellationToken ct)
        {
            var c = new Code();
            if (!Directory.Exists(lib)) return c;
            foreach (var f in Directory.EnumerateFiles(lib, "*.dart", SearchOption.AllDirectories).Take(max))
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length > 1_500_000) continue;
                    text = File.ReadAllText(f);
                }
                catch { continue; }
                var d = new DartDoc(text);
                c.Files.Add((f, d));
                c._paths[d] = f;
                c.Index(d);
            }
            return c;
        }

        private void Index(DartDoc d)
        {
            for (int i = 0; i + 1 < d.T.Count; i++)
            {
                if (d.IsIdent(i, "class") && d.T[i + 1].Kind == DTok.Ident && d.EnclosingOpener(i) < 0)
                {
                    for (int k = i + 2; k < d.T.Count && k < i + 80; k++)
                    {
                        if (d.Is(k, ";")) break;
                        if (d.Is(k, "{") && d.Match[k] > k) { Classes.TryAdd(d.T[i + 1].Text, (d, k)); break; }
                    }
                }
                // a top-level function: Widget _footer(String note) => …;  Map<String, dynamic> _m(dynamic v) => …
                if (d.Is(i + 1, "(") && d.T[i].Kind == DTok.Ident && d.EnclosingOpener(i) < 0 && FuncAt(d, i + 1) is { } fn)
                    Functions.TryAdd(d.T[i].Text, fn);
            }
        }

        /// <summary>The member <paramref name="member"/> of class <paramref name="cls"/>: a factory or named constructor
        /// (<c>factory X.fromJson(…)</c>), or a method (<c>static int toInt(…)</c>).</summary>
        public Func? Member(string cls, string member)
        {
            if (!Classes.TryGetValue(cls, out var c)) return null;
            var d = c.Doc;
            int close = d.Match[c.Open];
            for (int k = c.Open + 1; k < close; k++)
            {
                if (!d.IsOpener(k) || d.Match[k] <= k) continue;
                if (d.Is(k, "(") && d.IsIdent(k - 1, member) && (d.Is(k - 2, ".") ? d.IsIdent(k - 3, cls) : !d.Is(k - 2, "?.")) && FuncAt(d, k) is { } fn)
                    return fn;
                k = d.Match[k];
            }
            return null;
        }

        /// <summary>Requests with a written path: <c>x.get('/path')</c> (Dio, an app's client), <c>http.get(Uri.parse('…'))</c>.</summary>
        public IEnumerable<Request> Requests()
        {
            foreach (var (path, d) in Files)
            {
                if (path.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)) continue;
                for (int i = 2; i + 2 < d.T.Count; i++)
                {
                    if (d.T[i] is not { Kind: DTok.Ident, Text: "get" or "post" or "put" or "patch" or "delete" } || !(d.Is(i - 1, ".") || d.Is(i - 1, "?."))) continue;
                    int open = i + 1;
                    if (d.Is(open, "<")) { open = d.SkipAngles(open); if (open < 0) continue; }
                    if (!d.Is(open, "(") || d.Match[open] <= open) continue;
                    if (d.CallFromOpen(open) is not { } call) continue;
                    // a client of the app gives back what the reply carries; Dio and package:http give the reply itself
                    string target = d.T[i - 2].Kind == DTok.Ident ? d.T[i - 2].Text : "";
                    bool raw = target.EndsWith("dio", StringComparison.OrdinalIgnoreCase) || target is "http" or "client" or "httpClient" or "_client" or "_http";
                    string method = d.T[i].Text.ToUpperInvariant();
                    string? url = d.T[open + 1].Kind == DTok.Str ? d.T[open + 1].Text
                        : d.IsIdent(open + 1, "Uri") && d.Is(open + 2, ".") && d.T[open + 3].Text is "parse" or "tryParse" && d.Is(open + 4, "(") && d.T[open + 5].Kind == DTok.Str ? d.T[open + 5].Text
                        : null;
                    if (url != null)
                    {
                        if (PathOf(url) is { } p) yield return new Request(d, path, method, p, call, raw);
                        continue;
                    }
                    // the path is a parameter of a helper (Future<dynamic> _get(String path) …): each call of it that
                    // writes the path is a request
                    if (d.T[open + 1].Kind != DTok.Ident || !(d.Is(open + 2, ",") || d.Is(open + 2, ")"))) continue;
                    if (EnclosingFunc(d, i) is not { } helper || helper.NameTok < 0) continue;
                    int at = helper.Params.FindIndex(x => !x.Named && x.Name == d.T[open + 1].Text);
                    if (at < 0) continue;
                    foreach (var site in CallsOf(d, helper))
                        if (site.Positional(at) is { } a && a.FirstTok == a.LastTok && d.T[a.FirstTok] is { Kind: DTok.Str } s && PathOf(s.Text) is { } hp)
                            yield return new Request(d, path, method, hp, call, raw, helper, site);
                }
            }
        }

        /// <summary>The method or top-level function whose body holds token <paramref name="tok"/>.</summary>
        private Func? EnclosingFunc(DartDoc d, int tok)
        {
            int top = -1;
            for (int o = d.EnclosingOpener(tok); o >= 0; o = d.EnclosingOpener(o)) top = o;
            if (top >= 0 && d.Is(top, "{"))
            {
                // a member of the class (or mixin) whose body that is
                int close = d.Match[top];
                for (int k = top + 1; k < close; k++)
                {
                    if (!d.IsOpener(k) || d.Match[k] <= k) continue;
                    if (d.Is(k, "(") && d.T[k - 1].Kind == DTok.Ident && !d.Is(k - 2, ".") && FuncAt(d, k) is { } fn && fn.From <= tok && tok <= fn.To) return fn;
                    k = d.Match[k];
                }
                return null;
            }
            foreach (var fn in Functions.Values)
                if (ReferenceEquals(fn.Doc, d) && fn.From <= tok && tok <= fn.To) return fn;
            return null;
        }

        /// <summary>The calls of a helper in its file (this._get(…) too), its declaration left out.</summary>
        private static IEnumerable<DartCall> CallsOf(DartDoc d, Func helper)
        {
            string name = d.T[helper.NameTok].Text;
            for (int k = 0; k + 1 < d.T.Count; k++)
            {
                if (k == helper.NameTok || !d.IsIdent(k, name) || !d.Is(k + 1, "(") || (d.Is(k - 1, ".") && !d.IsIdent(k - 2, "this"))) continue;
                if (FuncAt(d, k + 1) != null) continue;   // another declaration of that name
                if (d.CallAtOffset(d.T[k].Pos) is { } c) yield return c;
            }
        }

        /// <summary>"/users/…?page=…" → "/users/*" (an interpolation is one segment); a whole URL → its path. Null: not a path.</summary>
        private static string? PathOf(string url)
        {
            string u = url.Trim();
            int q = u.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) u = u[..q];
            var m = Regex.Match(u, @"^https?://[^/]+(?<p>/.*)?$");
            if (m.Success) u = m.Groups["p"].Value;
            else if (!u.StartsWith('/') && !u.Contains('/')) return null;   // a bare word: a map key, not a path
            var segs = u.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Contains('…') ? "*" : s).ToList();
            if (segs.Count == 0 || segs.All(s => s == "*")) return null;
            return "/" + string.Join("/", segs);
        }
    }

    /// <summary>The function whose parameters a ( opens, with its body: <c>=&gt; expr;</c>, <c>{ … }</c>, or an initializer list.</summary>
    private static Func? FuncAt(DartDoc d, int open)
    {
        if (!d.Is(open, "(") || d.Match[open] <= open) return null;
        int close = d.Match[open];
        int after = close + 1;
        if (d.IsIdent(after, "async") || d.IsIdent(after, "sync")) { after++; if (d.Is(after, "*")) after++; }
        int from, to;
        if (d.Is(after, "=>"))
        {
            from = after + 1;
            to = StatementEnd(d, from, d.T.Count - 1);
        }
        else if (d.Is(after, "{") && d.Match[after] > after) { from = after + 1; to = d.Match[after] - 1; }
        else if (d.Is(after, ":") && IsCtorName(d, open))
        {
            // a constructor's initializer list (X.fromJson(Map j) : a = j['a'];), then maybe a body — never a call in a
            // ternary (c ? _m(x) : y)
            from = after + 1;
            int k = from;
            while (k < d.T.Count && !d.Is(k, ";") && !d.Is(k, "{")) k = d.IsOpener(k) && d.Match[k] > k ? d.Match[k] + 1 : k + 1;
            to = d.Is(k, "{") && d.Match[k] > k ? d.Match[k] - 1 : k - 1;
        }
        else return null;
        int nameTok = open - 1;
        if (d.Is(nameTok, ">")) { int lt = d.AngleOpenBefore(nameTok); if (lt < 0) return null; nameTok = lt - 1; }
        if (nameTok < 0 || d.T[nameTok].Kind != DTok.Ident || d.T[nameTok].Text is "if" or "for" or "while" or "switch" or "catch" or "on" or "when") return null;
        return new Func(d, nameTok, open, Params(d, open), from, to, ReturnType(d, nameTok));
    }

    /// <summary>The ( follows a constructor's name: Foo(…), Foo.named(…) (a class name: upper case, maybe after _).</summary>
    private static bool IsCtorName(DartDoc d, int open)
    {
        int k = open - 1;
        if (d.T[k].Kind != DTok.Ident) return false;
        if (d.Is(k - 1, ".")) k -= 2;
        string n = k >= 0 && d.T[k].Kind == DTok.Ident ? d.T[k].Text.TrimStart('_') : "";
        return n.Length > 0 && char.IsUpper(n[0]) && !d.Is(k - 1, ".") && !d.Is(k - 1, "?") && !d.Is(k - 1, "(") && !d.Is(k - 1, ",");
    }

    /// <summary>The token before which a statement or a value ends (;  ,  or a closing bracket at its own depth).</summary>
    private static int StatementEnd(DartDoc d, int from, int limit)
    {
        int k = from;
        while (k <= limit && k < d.T.Count && d.T[k].Kind != DTok.End)
        {
            if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k] + 1; continue; }
            if (d.T[k].Kind == DTok.Punct && d.T[k].Text is ";" or "," or ")" or "]" or "}") break;
            k++;
        }
        return k - 1;
    }

    private static List<(string Name, bool Named)> Params(DartDoc d, int open)
    {
        var list = new List<(string, bool)>();
        int close = d.Match[open], start = open + 1;
        bool named = false;
        for (int k = open + 1; k <= close; k++)
        {
            // Map<String, dynamic> j — the type's own commas
            if (d.Is(k, "<") && d.SkipAngles(k) is int past && past > k && past <= close) { k = past - 1; continue; }
            bool end = k == close || d.Is(k, ",") || d.Is(k, "}") || d.Is(k, "]");
            if (!end && d.IsOpener(k) && d.Match[k] > k)
            {
                if (k == start && (d.Is(k, "{") || d.Is(k, "["))) { named = d.Is(k, "{"); start = k + 1; continue; }
                k = d.Match[k];
                continue;
            }
            if (!end) continue;
            if (k > start)
            {
                int stop = k;
                for (int j = start; j < k; j++)
                {
                    if (d.IsOpener(j) && d.Match[j] > j && d.Match[j] < k) { j = d.Match[j]; continue; }
                    if (d.Is(j, "=")) { stop = j; break; }
                }
                int n = stop - 1;
                if (n >= start && d.T[n].Kind == DTok.Ident) list.Add((d.T[n].Text, named));
            }
            start = k + 1;
        }
        return list;
    }

    /// <summary>The return type written before a function's name ("int", "DateTime", "List", "Map", "AiPool"; "" when none).</summary>
    private static string ReturnType(DartDoc d, int nameTok)
    {
        int k = nameTok - 1;
        if (d.Is(k, "?")) k--;
        if (d.Is(k, ">") || d.Is(k, ">>")) { int lt = d.AngleOpenBefore(k); if (lt < 0) return ""; k = lt - 1; }
        return k >= 0 && d.T[k].Kind == DTok.Ident && d.T[k].Text is not ("static" or "factory" or "final" or "const" or "external" or "async") ? d.T[k].Text : "";
    }

    // ───────────────────────────── what the code reads ─────────────────────────────

    internal enum JKind { Unknown, WeakString, String, Bool, Int, NumString, Double, Date, List, Object }

    /// <summary>The shape of a JSON value, learnt from how the code reads it.</summary>
    internal sealed class JShape
    {
        public JKind Kind;
        public string Name = "";
        /// <summary>A value the code compares it with (an enum-like word): the sample uses it.</summary>
        public string? Literal;
        public readonly List<string> Order = new();
        public readonly Dictionary<string, JShape> Keys = new(StringComparer.Ordinal);
        public JShape? Item;
        /// <summary>The model whose fromJson reads it ("AiPool").</summary>
        public string? ModelName;
        /// <summary>For a request's result: what its .data / .body reads (a Dio or package:http reply).</summary>
        public JShape? Wire;
        public bool IsRequest;

        public JShape Child(string key)
        {
            Mark(JKind.Object);
            if (!Keys.TryGetValue(key, out var c)) { c = new JShape { Name = key }; Keys[key] = c; Order.Add(key); }
            return c;
        }

        public JShape ItemNode()
        {
            Mark(JKind.List);
            return Item ??= new JShape { Name = Name };
        }

        public void Mark(JKind k) { if (Rank(k) > Rank(Kind)) Kind = k; }

        /// <summary>A value's own kind, over a weaker guess — never over a map or a list.</summary>
        public void Force(JKind k) { if (Kind is not (JKind.List or JKind.Object)) Kind = k; }

        private static int Rank(JKind k) => k switch
        {
            JKind.WeakString => 1, JKind.String => 2, JKind.Bool or JKind.Int or JKind.NumString => 3, JKind.Double => 4, JKind.Date => 5,
            JKind.List or JKind.Object => 6, _ => 0,
        };

        public void Merge(JShape o, int depth = 0)
        {
            if (depth > 16 || ReferenceEquals(o, this)) return;
            Mark(o.Kind);
            Literal ??= o.Literal;
            ModelName ??= o.ModelName;
            foreach (var k in o.Order) Child(k).Merge(o.Keys[k], depth + 1);
            if (o.Item != null) ItemNode().Merge(o.Item, depth + 1);
        }

        public bool Empty => Kind == JKind.Unknown && Keys.Count == 0 && Item == null;
    }

    private sealed class Reader
    {
        private readonly Code _code;
        private readonly Dictionary<string, JShape> _models = new(StringComparer.Ordinal);
        private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
        private readonly Dictionary<(DartDoc, int, int), JShape> _followed = new();
        private int _budget = 2_000_000;

        public Reader(Code code) => _code = code;

        /// <summary>What a request's reply is read as. Raw: the code reads the reply itself (<c>res.data</c>), so the
        /// envelope is already part of what it reads.</summary>
        public JShape? Reply(Request rq, out string model, out bool raw)
        {
            var result = new JShape { Name = "reply", IsRequest = true };
            if (rq.Helper is { } helper && rq.Site is { } site)
            {
                // inside the helper: what it makes of the reply, and what it gives back (return body['data'];)
                _returns = new List<JShape>();
                Read(rq.Doc, rq.Call, result, helper.To);
                var given = _returns.FirstOrDefault() ?? result;
                _returns = null;
                // where it is called with the path: what the caller makes of that
                Read(rq.Doc, site, given, RegionEnd(rq.Doc, site));
            }
            else Read(rq.Doc, rq.Call, result, RegionEnd(rq.Doc, rq.Call));
            // the code reads .data / .body off it: the reply itself (Dio, package:http) — else what the app's client hands back
            JShape shape;
            if (result.Wire is { Empty: false } wire) { raw = true; shape = wire; }
            else { raw = rq.Raw; shape = result; }
            model = Describe(shape);
            return shape.Empty ? null : shape;
        }

        /// <summary>The values a helper being read gives back (null: not reading a helper).</summary>
        private List<JShape>? _returns;

        /// <summary>Read what the code does with a call's value: kept in a variable (final data = await _api.get(…)),
        /// handed to a parser (parser: (d) =&gt; X.fromJson(d)), read where it is made (X.fromJson(await …), .then(…)), and
        /// the rest of the code up to <paramref name="end"/>.</summary>
        private void Read(DartDoc d, DartCall call, JShape value, int end)
        {
            var views = new Dictionary<string, JShape>(StringComparer.Ordinal);
            int start = ExprStart(d, call.StartTok), v = start - 1;
            if (d.IsIdent(v, "await")) v--;
            if (d.Is(v, "=") && d.T[v - 1].Kind == DTok.Ident) views[d.T[v - 1].Text] = value;
            foreach (var a in call.Args)
                if (a.Name != null && Lambda(d, a.FirstTok) is { } lp) views[lp] = value;
            Left(d, start, call.CloseTok, value, views, 0);
            Chain(d, call.CloseTok, value, views, 0);
            Body(d, call.OpenTok, end, views, 0);
        }

        /// <summary>Where a call starts with its target: <c>_api.get(…)</c> → _api, <c>ref.read(p).dio.get(…)</c> → ref.</summary>
        private static int ExprStart(DartDoc d, int k)
        {
            while (k >= 2 && (d.Is(k - 1, ".") || d.Is(k - 1, "?.")))
            {
                int p = k - 2;
                if ((d.Is(p, ")") || d.Is(p, "]")) && d.Match[p] >= 0)
                {
                    p = d.Match[p];
                    if (p >= 1 && d.T[p - 1].Kind == DTok.Ident) p--;
                }
                if (p < 0 || d.T[p].Kind != DTok.Ident) break;
                k = p;
            }
            return k;
        }

        private static string Describe(JShape s) =>
            s.Kind == JKind.List ? "[" + (s.Item?.ModelName ?? "…") + "]"
            : s.ModelName is { } m ? m
            // a reply read whole (res.data): what its data part is read as
            : s.Kind == JKind.Object && s.Keys.FirstOrDefault(kv => kv.Key is "data" or "result" or "payload").Value is { } inner && !inner.Empty ? Describe(inner)
            : s.Kind == JKind.Object ? "{…}" : s.Kind.ToString().ToLowerInvariant();

        /// <summary>Where the code that reads a request's reply ends: the function or closure it is in (through try / if
        /// blocks), else the statement (an arrow member).</summary>
        private static int RegionEnd(DartDoc d, DartCall call)
        {
            for (int o = d.EnclosingOpener(call.StartTok); o >= 0; o = d.EnclosingOpener(o))
            {
                if (!d.Is(o, "{") || d.Match[o] <= o) continue;
                if (d.EnclosingOpener(o) < 0) break;   // a class body: the call is in an arrow member
                int p = o - 1;
                if (d.Is(p, "*")) p--;
                if (d.IsIdent(p, "async") || d.IsIdent(p, "sync")) p--;
                if (d.Is(p, ")") && d.Match[p] >= 0 && FlutterCodeEdit.FuncFromParams(d, d.Match[p]) != null) return d.Match[o] - 1;
            }
            return StatementEnd(d, call.CloseTok + 1, d.T.Count - 1);
        }

        /// <summary>The shape a model's member reads (<c>AiPool.fromJson</c>): what its first parameter is read as.</summary>
        public JShape Model(string cls, string member, int depth)
        {
            string key = cls + "." + member;
            if (_models.TryGetValue(key, out var done)) return done;
            var shape = new JShape { Name = cls, Kind = JKind.Object, ModelName = cls };
            if (depth > 8 || !_busy.Add(key)) return shape;   // a model inside itself: cut there
            if (_code.Member(cls, member) is { Params.Count: > 0 } fn)
                Body(fn.Doc, fn.From, fn.To, new Dictionary<string, JShape>(StringComparer.Ordinal) { [fn.Params[0].Name] = shape }, depth + 1);
            _busy.Remove(key);
            _models[key] = shape;
            return shape;
        }

        /// <summary>Read a function body: every value that holds part of the JSON (<paramref name="views"/>) and what
        /// is done with it.</summary>
        private void Body(DartDoc d, int from, int to, Dictionary<string, JShape> views, int depth)
        {
            to = Math.Min(to, d.T.Count - 2);
            for (int k = Math.Max(1, from); k <= to; k++)
            {
                if (--_budget < 0) return;
                var t = d.T[k];
                if (t.Kind != DTok.Ident) continue;
                if (t.Text == "for" && d.Is(k + 1, "(") && d.Match[k + 1] > k) { LoopVar(d, k + 1, views); continue; }
                // what a helper being read gives back: return body['data'];
                if (t.Text == "return" && depth == 0 && _returns != null && ViewOf(d, k + 1, StatementEnd(d, k + 1, to), views) is { } given) _returns.Add(given);
                // final x = …;  x = …;  — x holds what its value reads
                if (d.Is(k + 1, "=") && !d.Is(k - 1, ".") && !d.Is(k - 1, "?.") && Declares(d, k))
                {
                    if (ViewOf(d, k + 2, StatementEnd(d, k + 2, to), views) is { } v) views[t.Text] = v;
                    continue;
                }
                if (d.Is(k - 1, ".") || d.Is(k - 1, "?.") || !views.TryGetValue(t.Text, out var node)) continue;
                int at = k;
                var cur = Navigate(d, ref at, node);
                Right(d, k, at, cur, views, depth);
                Left(d, k, at, cur, views, depth);
                k = at;
            }
        }

        /// <summary>V['a']['b'], V?['a'], V[i] (an element), V.data / V.body of a request's reply.</summary>
        private static JShape Navigate(DartDoc d, ref int at, JShape cur)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                int b = at + 1;
                if (cur.IsRequest && (d.Is(b, ".") || d.Is(b, "?.")) && d.T[b + 1] is { Kind: DTok.Ident, Text: "data" or "body" })
                {
                    cur = cur.Wire ??= new JShape { Name = "reply" };
                    at = b + 1;
                    continue;
                }
                if ((d.Is(b, "!") || d.Is(b, "?")) && d.Is(b + 1, "[")) b++;
                if (!d.Is(b, "[") || d.Match[b] <= b) break;
                int close = d.Match[b];
                cur = close == b + 2 && d.T[b + 1] is { Kind: DTok.Str, Interp: null } ? cur.Child(d.T[b + 1].Text) : cur.ItemNode();
                at = close;
            }
            return cur;
        }

        /// <summary>A declaration or assignment target: final x =, Type x =, x = at a statement's start.</summary>
        private static bool Declares(DartDoc d, int k)
        {
            var p = d.T[k - 1];
            if (p.Kind == DTok.Ident) return p.Text is not ("return" or "await" or "yield" or "case" or "in" or "is" or "as" or "else");
            return d.Is(k - 1, ">") || d.Is(k - 1, "?") || d.Is(k - 1, ";") || d.Is(k - 1, "{") || d.Is(k - 1, "}");
        }

        /// <summary>What part of the JSON a value is: the first one it names (<c>_m(j['x'])</c> → x, <c>raw[i]</c> → an element,
        /// <c>pv.first</c> → an element, <c>j.cast&lt;…&gt;()</c> → j).</summary>
        private static JShape? ViewOf(DartDoc d, int a, int b, Dictionary<string, JShape> views)
        {
            for (int k = a; k <= b && k < d.T.Count; k++)
            {
                if (d.T[k].Kind != DTok.Ident || d.Is(k - 1, ".") || d.Is(k - 1, "?.") || !views.TryGetValue(d.T[k].Text, out var v)) continue;
                int at = k;
                var cur = Navigate(d, ref at, v);
                if ((d.Is(at + 1, ".") || d.Is(at + 1, "?.")) && d.T[at + 2] is { Kind: DTok.Ident, Text: "first" or "last" or "single" }) cur = cur.ItemNode();
                return cur;
            }
            return null;
        }

        /// <summary>for (final e in X) — e is an element of X.</summary>
        private static void LoopVar(DartDoc d, int open, Dictionary<string, JShape> views)
        {
            int close = d.Match[open];
            for (int k = open + 1; k < close; k++)
            {
                if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k]; continue; }
                if (!d.IsIdent(k, "in") || d.T[k - 1].Kind != DTok.Ident) continue;
                if (ViewOf(d, k + 1, close - 1, views) is { } list) views[d.T[k - 1].Text] = list.ItemNode();
                return;
            }
        }

        /// <summary>The first parameter of a closure that starts at <paramref name="tok"/> ((e) =&gt; …, (e) { … }), or null.</summary>
        private static string? Lambda(DartDoc d, int tok, int which = 0)
        {
            if (!d.Is(tok, "(") || d.Match[tok] <= tok) return null;
            int after = d.Match[tok] + 1;
            if (d.IsIdent(after, "async")) after++;
            if (!d.Is(after, "=>") && !d.Is(after, "{")) return null;
            var names = new List<string>();
            for (int k = tok + 1; k < d.Match[tok]; k++)
                if (d.T[k].Kind == DTok.Ident && (d.Is(k + 1, ",") || k + 1 == d.Match[tok])) names.Add(d.T[k].Text);
            return which < names.Count ? names[which] : null;
        }

        private static JKind KindOfType(DartDoc d, int t)
        {
            if (t >= d.T.Count || d.T[t].Kind != DTok.Ident) return JKind.Unknown;
            return KindOfType(d.T[t].Text);
        }

        private static JKind KindOfType(string type) => type switch
        {
            "int" => JKind.Int,
            "double" => JKind.Double,
            "num" => JKind.Int,   // (a number: .toDouble() after it makes it a fraction)
            "bool" => JKind.Bool,
            "String" => JKind.String,
            "DateTime" => JKind.Date,
            "List" or "Iterable" or "Set" => JKind.List,
            "Map" => JKind.Object,
            _ => JKind.Unknown,
        };

        /// <summary>What the code does with a value right after it: as T, is T, == true, ?? 'x', .toString(), .map(…).</summary>
        private void Right(DartDoc d, int s, int at, JShape n, Dictionary<string, JShape> views, int depth)
        {
            int k = at + 1;
            if (d.Is(k, "!") && !d.Is(k + 1, "=")) k++;
            if (d.IsIdent(k, "as")) { n.Mark(KindOfType(d, k + 1)); AfterGroup(d, s, at, n, views, depth); return; }
            if (d.IsIdent(k, "is")) { n.Mark(KindOfType(d, d.Is(k + 1, "!") ? k + 2 : k + 1)); return; }
            if (d.Is(k, "==") || d.Is(k, "!="))
            {
                var v = d.T[k + 1];
                if (v.Kind == DTok.Ident && v.Text is "true" or "false") n.Mark(JKind.Bool);
                else if (v.Kind == DTok.Str && v.Interp == null) { n.Mark(JKind.String); n.Literal ??= v.Text; }
                else if (v.Kind == DTok.Num) n.Mark(v.Text.Contains('.') ? JKind.Double : JKind.Int);
                return;
            }
            if (d.Is(k, "??"))
            {
                int v = d.IsIdent(k + 1, "const") ? k + 2 : k + 1;
                var t = d.T[v];
                if (t.Kind == DTok.Str && t.Interp == null) { n.Mark(JKind.String); if (Regex.IsMatch(t.Text, @"^[A-Za-z][\w.-]{0,40}$")) n.Literal ??= t.Text; }
                else if (t.Kind == DTok.Num) n.Mark(t.Text.Contains('.') ? JKind.Double : JKind.Int);
                else if (t.Kind == DTok.Ident && t.Text is "true" or "false") n.Mark(JKind.Bool);
                else if (d.Is(v, "[")) n.Mark(JKind.List);
                else if (d.Is(v, "{")) n.Mark(JKind.Object);
                AfterGroup(d, s, at, n, views, depth);
                return;
            }
            Chain(d, at, n, views, depth);
            AfterGroup(d, s, at, n, views, depth);
        }

        /// <summary>(x as List).map(…), (x ?? []).where(…), (x as Map)['key']: what is done with the parentheses a value opens.</summary>
        private void AfterGroup(DartDoc d, int s, int at, JShape n, Dictionary<string, JShape> views, int depth)
        {
            int o = d.EnclosingOpener(s);
            if (o < 0 || !d.Is(o, "(") || d.CallFromOpen(o) != null || FlutterCodeEdit.FuncFromParams(d, o) != null) return;
            int close = d.Match[o];
            if (close <= at) return;
            int b = d.Is(close + 1, "?") || d.Is(close + 1, "!") ? close + 2 : close + 1;   // (x as Map?)?['key']
            if (d.Is(b, "[") && d.Match[b] == b + 2 && d.T[b + 1] is { Kind: DTok.Str, Interp: null })
            {
                int end = close;
                var child = Navigate(d, ref end, n);
                Right(d, o, end, child, views, depth);
                Left(d, o, end, child, views, depth);
                return;
            }
            Chain(d, close, n, views, depth);
        }

        /// <summary>Method calls on a value: .map(X.fromJson) / .map((e) =&gt; …) — its elements; .first — one; .then((v) =&gt; …).</summary>
        private void Chain(DartDoc d, int end, JShape n, Dictionary<string, JShape> views, int depth)
        {
            var cur = n;
            int k = end;
            for (int guard = 0; guard < 12; guard++)
            {
                int dot = k + 1;
                if (d.Is(dot, "!")) dot++;
                if (!(d.Is(dot, ".") || d.Is(dot, "?.")) || dot + 1 >= d.T.Count || d.T[dot + 1].Kind != DTok.Ident) return;
                string m = d.T[dot + 1].Text;
                int open = dot + 2;
                if (d.Is(open, "<")) { open = d.SkipAngles(open); if (open < 0) return; }
                bool call = d.Is(open, "(") && d.Match[open] > open;
                switch (m)
                {
                    case "map" or "expand" or "where" or "forEach" or "firstWhere" or "lastWhere" or "singleWhere" or "any" or "every" or "indexWhere":
                        cur.Mark(JKind.List);
                        if (call) Element(d, open, cur.ItemNode(), views, depth, 0);
                        if (m is "map" or "expand") return;   // (a new list: what the closure makes)
                        break;
                    case "fold":
                        cur.Mark(JKind.List);
                        if (call && d.Match[open] > open) { int second = NextArg(d, open); if (second > 0) Element(d, second - 1, cur.ItemNode(), views, depth, 1); }
                        return;
                    case "whereType" or "toList" or "toSet" or "take" or "skip" or "sublist" or "followedBy" or "reversed":
                        cur.Mark(JKind.List);
                        break;
                    case "first" or "last" or "single" or "elementAt":
                        cur = cur.ItemNode();
                        break;
                    case "cast" or "then":
                        if (m == "then" && call) Element(d, open, cur, views, depth, 0);
                        break;
                    case "containsKey" or "keys" or "values" or "entries":
                        cur.Mark(JKind.Object);
                        return;
                    case "toString":
                        cur.Mark(JKind.WeakString);
                        return;
                    case "toInt" or "round" or "floor" or "ceil" or "truncate":
                        cur.Mark(JKind.Int);
                        return;
                    case "toDouble" or "toStringAsFixed":
                        cur.Mark(JKind.Double);
                        return;
                    case "trim" or "toLowerCase" or "toUpperCase" or "split" or "startsWith" or "endsWith" or "substring" or "replaceAll" or "padLeft" or "codeUnits":
                        cur.Mark(JKind.String);
                        return;
                    default:
                        return;
                }
                k = call ? d.Match[open] : dot + 1;
            }
        }

        private static int NextArg(DartDoc d, int open)
        {
            for (int k = open + 1; k < d.Match[open]; k++)
            {
                if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k]; continue; }
                if (d.Is(k, ",")) return k + 1;
            }
            return -1;
        }

        /// <summary>The argument a list method gets: a model's tear-off (<c>AiKey.fromJson</c>) or a closure whose parameter
        /// is the element.</summary>
        private void Element(DartDoc d, int open, JShape item, Dictionary<string, JShape> views, int depth, int which)
        {
            int a = open + 1;
            if (TearOff(d, a) is { } m) { item.Merge(Model(m.Cls, m.Member, depth + 1)); return; }
            if (Lambda(d, a, which) is { } p) views[p] = item;
        }

        /// <summary>X.member written without a call (a tear-off) of a class of the app: (Class, member).</summary>
        private (string Cls, string Member)? TearOff(DartDoc d, int k)
        {
            if (d.T[k].Kind != DTok.Ident || !d.Is(k + 1, ".") || d.T[k + 2].Kind != DTok.Ident || d.Is(k + 3, "(") || d.Is(k + 3, "<")) return null;
            if (!(d.Is(k + 3, ")") || d.Is(k + 3, ","))) return null;
            string cls = d.T[k].Text, member = d.T[k + 2].Text;
            return _code.Classes.ContainsKey(cls) && _code.Member(cls, member) != null ? (cls, member) : null;
        }

        /// <summary>The call a value is handed to, through parentheses, ?? and casts: what that call makes of it.</summary>
        private void Left(DartDoc d, int s, int e, JShape n, Dictionary<string, JShape> views, int depth)
        {
            for (int guard = 0; guard < 8; guard++)
            {
                int o = d.EnclosingOpener(s);
                while (o >= 0 && d.Is(o, "(") && d.CallFromOpen(o) == null && FlutterCodeEdit.FuncFromParams(d, o) == null) o = d.EnclosingOpener(o);
                if (o < 0 || !d.Is(o, "(")) return;
                var call = d.CallFromOpen(o);
                if (call == null) return;
                var arg = call.Args.FirstOrDefault(x => x.FirstTok <= s && e <= x.LastTok);
                if (arg == null || !MainValue(d, arg, s)) return;
                if (!Apply(d, call, arg, n, views, depth)) return;
                // a converter hands the same data on: _list(j['keys']).map(AiKey.fromJson), AiPool.fromJson(_m(data))
                Chain(d, call.CloseTok, n, views, depth);
                s = call.StartTok;
                e = call.CloseTok;
            }
        }

        /// <summary>The value is what the argument is (give or take parentheses and await), or one of its ?? choices:
        /// F(j['x']), F((j['x'] ?? '').trim()), F(m['amount_thb'] ?? m['amount']).</summary>
        private static bool MainValue(DartDoc d, DartItem arg, int s)
        {
            int k = arg.FirstTok;
            while (k <= arg.LastTok)
            {
                while (k < s && (d.Is(k, "(") || d.IsIdent(k, "await"))) k++;
                if (k == s) return true;
                // the next ?? choice at the argument's own level
                int next = -1;
                for (int j = k; j <= arg.LastTok; j++)
                {
                    if (d.IsOpener(j) && d.Match[j] > j) { j = d.Match[j]; continue; }
                    if (d.Is(j, "??")) { next = j + 1; break; }
                }
                if (next < 0 || next > s) return false;
                k = next;
            }
            return false;
        }

        /// <summary>What a call makes of a value handed to it. True when it hands the same data on (a converter: _m, _list,
        /// Map.from, jsonDecode…), so the call is read on.</summary>
        private bool Apply(DartDoc d, DartCall call, DartItem arg, JShape n, Dictionary<string, JShape> views, int depth)
        {
            if (depth > 8) return false;
            string name = call.Name;
            // a list method with a model's tear-off beside it: Paged.parse(data, User.fromJson)
            foreach (var other in call.Args)
                if (!ReferenceEquals(other, arg) && TearOff(d, other.FirstTok) is { } item && other.FirstTok + 2 == other.LastTok)
                {
                    n.ItemNode().Merge(Model(item.Cls, item.Member, depth + 1));
                    return false;
                }
            int index = arg.Name == null ? call.Args.Where(x => x.Name == null).ToList().IndexOf(arg) : -1;
            // json.decode / int.parse: a lower-case prefix the call's name leaves out
            string prefix = call.NameTok >= 2 && d.Is(call.NameTok - 1, ".") && d.T[call.NameTok - 2].Kind == DTok.Ident && !name.Contains('.') ? d.T[call.NameTok - 2].Text + "." : "";
            switch (prefix + name)
            {
                case "int.parse" or "int.tryParse": n.Mark(JKind.NumString); return false;
                case "double.parse" or "double.tryParse" or "num.parse" or "num.tryParse": n.Mark(JKind.NumString); return false;
                case "DateTime.parse" or "DateTime.tryParse": n.Mark(JKind.Date); return false;
                case "Uri.parse" or "Uri.tryParse": n.Mark(JKind.String); return false;
                case "jsonDecode" or "json.decode" or "convert.jsonDecode": return true;
                case "Map.from" or "Map.of" or "Map.castFrom" or "Map<String, dynamic>.from": n.Mark(JKind.Object); return true;
                case "List.from" or "List.of" or "List.castFrom" or "Set.from": n.Mark(JKind.List); return true;
            }
            if (name.StartsWith("Map<", StringComparison.Ordinal)) { n.Mark(JKind.Object); return true; }
            // a member of a class of the app: X.fromJson(j), TpFmt.toInt(v)
            if (name.Contains('.'))
            {
                string cls = name[..name.IndexOf('.')], member = name[(name.IndexOf('.') + 1)..];
                if (_code.Member(cls, member) is { } fn)
                {
                    bool model = index == 0 && (member.StartsWith("from", StringComparison.Ordinal) || member is "parse" or "fromJson" or "fromMap")
                                 && !IsConverter(fn.Returns);
                    if (model) { n.Merge(Model(cls, member, depth + 1)); return false; }
                    return Follow(fn, index, arg.Name, n, depth);
                }
            }
            // a function of the app: one declared before the call in this code, a method of the class the call is in, a
            // top-level one
            else if ((LocalFunc(d, call.StartTok, name) ?? OwnMethod(d, call.StartTok, name) ?? (_code.Functions.TryGetValue(name, out var top) ? top : null)) is { } fn)
                return Follow(fn, index, arg.Name, n, depth);
            // a class of the app: its constructor's parameter of that name has the field's type
            else if (_code.Classes.ContainsKey(name) && arg.Name != null && FieldType(name, arg.Name) is { } type)
            {
                n.Mark(KindOfType(type));
                return false;
            }
            // what a function from a package says it makes
            string low = name.ToLowerInvariant();
            if (Regex.IsMatch(low, @"toint|asint|parseint")) n.Mark(JKind.Int);
            else if (Regex.IsMatch(low, @"todouble|asdouble|tonum|asnum|parsedouble|money|amount|price")) n.Mark(JKind.Double);
            else if (Regex.IsMatch(low, @"bool")) n.Mark(JKind.Bool);
            else if (Regex.IsMatch(low, @"date|time")) n.Mark(JKind.Date);
            else if (Regex.IsMatch(low, @"(^|_|\.)(as)?list$|tolist$")) { n.Mark(JKind.List); return true; }
            else if (Regex.IsMatch(low, @"^_?m$|(^|_|\.)(as|to)?map$|^_?obj$")) { n.Mark(JKind.Object); return true; }
            else if (Regex.IsMatch(low, @"str|text|clean|label|name")) n.Mark(JKind.WeakString);
            return false;
        }

        private static bool IsConverter(string returns) => KindOfType(returns) != JKind.Unknown;

        /// <summary>Read a function of the app with the value as its parameter: what the body does with it (and what it
        /// gives back: an int, a DateTime, the same data as a list or a map).</summary>
        private bool Follow(Func fn, int index, string? named, JShape n, int depth)
        {
            int p = named != null ? fn.Params.FindIndex(x => x.Named && x.Name == named) : index;
            if (p < 0 || p >= fn.Params.Count) return false;
            var key = (fn.Doc, fn.Open, p);
            if (!_followed.TryGetValue(key, out var shape))
            {
                shape = new JShape { Name = n.Name };
                _followed[key] = shape;   // (set first: a function that calls itself stops here)
                Body(fn.Doc, fn.From, fn.To, new Dictionary<string, JShape>(StringComparer.Ordinal) { [fn.Params[p].Name] = shape }, depth + 1);
            }
            var kind = KindOfType(fn.Returns);
            if (kind is JKind.Unknown or JKind.List or JKind.Object)
            {
                n.Merge(shape);
                n.Mark(kind);
                return kind != JKind.Unknown;   // (the same data as a list or a map: read on)
            }
            // a converter to a value (static int toInt(dynamic v)): what it gives back is what the JSON holds, whatever
            // else it would accept
            n.Force(kind);
            return false;
        }

        /// <summary>A function declared inside the current code before <paramref name="before"/> (OpsSummary.fromJson's
        /// <c>Map&lt;String, dynamic&gt; m(dynamic v) =&gt; …</c>).</summary>
        private static Func? LocalFunc(DartDoc d, int before, string name)
        {
            for (int k = before - 1; k >= 0 && k > before - 4000; k--)
            {
                if (!d.IsIdent(k, name) || !d.Is(k + 1, "(") || d.Is(k - 1, ".")) continue;
                // declared in a block the call is in
                int block = d.EnclosingOpener(k);
                if (block < 0 || !d.Is(block, "{") || d.Match[block] < before) continue;
                if (FuncAt(d, k + 1) is { } fn) return fn;
            }
            return null;
        }

        /// <summary>A method of the class the code at <paramref name="tok"/> is in (AnalyticsOverview's static <c>_points</c>,
        /// declared after the factory that calls it).</summary>
        private Func? OwnMethod(DartDoc d, int tok, string name)
        {
            for (int o = d.EnclosingOpener(tok); o >= 0; o = d.EnclosingOpener(o))
            {
                if (!d.Is(o, "{") || d.EnclosingOpener(o) >= 0) continue;
                for (int k = o - 1; k >= 0 && k > o - 60; k--)
                {
                    if (!d.IsIdent(k, "class") && !d.IsIdent(k, "mixin") && !d.IsIdent(k, "extension")) continue;
                    if (d.T[k + 1].Kind != DTok.Ident) return null;
                    string cls = d.T[k + 1].Text;
                    return _code.Classes.TryGetValue(cls, out var c) && ReferenceEquals(c.Doc, d) ? _code.Member(cls, name) : MemberIn(d, o, name);
                }
                return null;
            }
            return null;
        }

        /// <summary>A member named <paramref name="name"/> of the body a { opens (a mixin's, an extension's).</summary>
        private static Func? MemberIn(DartDoc d, int open, string name)
        {
            int close = d.Match[open];
            for (int k = open + 1; k < close; k++)
            {
                if (!d.IsOpener(k) || d.Match[k] <= k) continue;
                if (d.Is(k, "(") && d.IsIdent(k - 1, name) && !d.Is(k - 2, ".") && FuncAt(d, k) is { } fn) return fn;
                k = d.Match[k];
            }
            return null;
        }

        /// <summary>The type a class declares a field with ("int", "List", "AiPool"), null when it declares none.</summary>
        private string? FieldType(string cls, string field)
        {
            if (!_code.Classes.TryGetValue(cls, out var c)) return null;
            var d = c.Doc;
            int close = d.Match[c.Open];
            for (int k = c.Open + 1; k < close; k++)
            {
                if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k]; continue; }
                if (!d.IsIdent(k, field) || !(d.Is(k + 1, ";") || d.Is(k + 1, "=") || d.Is(k + 1, ","))) continue;
                int t = k - 1;
                if (d.Is(t, "?")) t--;
                if (d.Is(t, ">") || d.Is(t, ">>")) { int lt = d.AngleOpenBefore(t); if (lt < 0) return null; t = lt - 1; }
                return t > c.Open && d.T[t].Kind == DTok.Ident && d.T[t].Text is not ("final" or "late" or "var" or "static" or "const") ? d.T[t].Text : null;
            }
            return null;
        }
    }

    // ───────────────────────────── the sample values ─────────────────────────────

    /// <summary>Values made up from a key's name: a count, an amount, a Thai name, three list items, recent dates.</summary>
    private sealed class Gen
    {
        private readonly DateTime _now;
        private int _budget = 40_000;
        private static readonly string[] People = { "สมชาย ใจดี", "สมหญิง รักไทย", "วิชัย มั่นคง" };

        public Gen(DateTime now) => _now = now;

        /// <summary>A value for <paramref name="s"/>: element <paramref name="index"/> of a list, under the keys and models
        /// <paramref name="context"/> names (revenue/split/… makes a number an amount, a user's name a person's).</summary>
        public JsonNode? Value(JShape s, int index, int depth, string context = "")
        {
            if (--_budget < 0 || depth > 10) return null;
            string name = s.Name.ToLowerInvariant();
            string inner = context + "/" + name + (s.ModelName != null ? "/" + s.ModelName.ToLowerInvariant() : "");
            switch (s.Kind)
            {
                case JKind.Object:
                {
                    var o = new JsonObject();
                    foreach (var k in s.Order) o[k] = Value(s.Keys[k], index, depth + 1, inner);
                    return o;
                }
                case JKind.List:
                {
                    var a = new JsonArray();
                    int count = Regex.IsMatch(name, @"degraded|errors?$|failures|failed|warnings|alerts|issues|problems|blocked|banned") ? 0 : 3;
                    for (int i = 0; i < count; i++)
                        a.Add(s.Item is { } it && !it.Empty ? Value(it, i, depth + 1, inner) : JsonValue.Create(Text(s.Item?.Name ?? s.Name, i, inner)));
                    return a;
                }
                case JKind.Bool:
                    return JsonValue.Create(!Regex.IsMatch(name, @"error|fail|exhaust|disabled|banned|lock|suspend|delet|expire|critical|overdue|block|degrad|unavailable|flag|mute|hidden|archiv|empty|offline"));
                case JKind.Int:
                    return JsonValue.Create(Int(name, index));
                case JKind.Double:
                    return JsonValue.Create(Double(name, index, context));
                case JKind.NumString:
                    return JsonValue.Create(Regex.IsMatch(name, Money) ? Double(name, index, context).ToString("0.00", CultureInfo.InvariantCulture) : Int(name, index).ToString(CultureInfo.InvariantCulture));
                case JKind.Date:
                    return JsonValue.Create(_now.AddDays(-index).AddMinutes(-37 * index).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                default:
                    if (s.Literal != null) return JsonValue.Create(s.Literal);
                    if (Regex.IsMatch(name, @"url|link|href|image|img|avatar|photo|picture|thumb|icon|logo|cover|banner|video|audio|attachment|slip")) return null;
                    return JsonValue.Create(Text(name, index, context));
            }
        }

        private const string Money = @"amount|price|thb|revenue|total|balance|value|sum|cost|fee|paid|income|expense|spent|commission|salary|budget|money|baht|sales|payment|payout|withdraw|deposit|wallet|refund|profit|split";

        private int Int(string name, int i)
        {
            if (Regex.IsMatch(name, @"(^|_)id$")) return 101 + i;
            if (Regex.IsMatch(name, @"error|fail")) return i;
            if (Regex.IsMatch(name, @"count|total|qty|quantity|(^|_)num|number|amount|sold|views|visits|members|users|orders|buyers|tokens|requests")) return 12 + 5 * i;
            if (Regex.IsMatch(name, @"hour")) return 9 + i;
            if (Regex.IsMatch(name, @"minute")) return 15 + 5 * i;
            if (Regex.IsMatch(name, @"percent|pct|rate|ratio|progress|score")) return Math.Max(5, 75 - 10 * i);
            if (Regex.IsMatch(name, @"year")) return _now.Year;
            if (Regex.IsMatch(name, @"month")) return _now.Month;
            if (Regex.IsMatch(name, @"(^|_)day")) return _now.Day;
            if (Regex.IsMatch(name, @"current_page|^page$")) return 1;
            if (Regex.IsMatch(name, @"last_page")) return 3;
            if (Regex.IsMatch(name, @"per_page")) return 20;
            if (Regex.IsMatch(name, @"limit|max|capacity|quota")) return 300;
            if (Regex.IsMatch(name, @"priority|order|rank|level|position|index|sort|step")) return i + 1;
            return 12 + 5 * i;
        }

        private static double Double(string name, int i, string context)
        {
            if (Regex.IsMatch(name, @"(^|_)lat")) return 13.7563;
            if (Regex.IsMatch(name, @"(^|_)(lng|lon)")) return 100.5018;
            if (Regex.IsMatch(name, @"percent|pct|rate|ratio|progress")) return 0.75;
            // an amount: its own name says so, or the keys around it do (revenue_today/split/fortune)
            if (Regex.IsMatch(name, Money) || Regex.IsMatch(context, Money)) return 1250.5 + 310 * i;
            return 12.5 + i;
        }

        private static string Text(string name, int i, string context = "")
        {
            string n = name.ToLowerInvariant();
            // a bare name is a person's only among a person's keys (a user, a customer…), else a thing's
            if (n is "name" or "display_name" or "title_name" && !Regex.IsMatch(context, @"user|customer|member|sender|owner|author|admin|staff|rider|seller|buyer|profile|account|contact|person|employee|client|driver|patient|student|teacher"))
                return "ตัวอย่าง" + (i > 0 ? " " + (i + 1) : "");
            string no = i > 0 ? " " + (i + 1) : "";
            if (n.Contains("email")) return $"user{i + 1}@example.com";
            if (Regex.IsMatch(n, @"phone|tel|mobile")) return "08" + (12345678 + i).ToString(CultureInfo.InvariantCulture);
            if (Regex.IsMatch(n, @"masked")) return "••••" + (1234 + i).ToString(CultureInfo.InvariantCulture);
            // a person: name, user_name, customer_name, sender, author… (not provider_name, package_name)
            if (n is "name" or "display_name" or "sender" or "author" or "owner" or "customer" or "username" or "nickname"
                || Regex.IsMatch(n, @"(user|customer|sender|owner|author|member|full|first|last|display|nick|contact|admin|staff|rider|seller|buyer|recipient|receiver|agent|employee|person|client)_?name$"))
                return People[i % People.Length];
            if (Regex.IsMatch(n, @"message|desc|note|text|body|content|detail|reason|comment|summary|remark|preview")) return "ข้อความตัวอย่างสำหรับดูหน้าตา" + no;
            if (Regex.IsMatch(n, @"currency")) return "THB";
            if (Regex.IsMatch(n, @"(^|_)(lang|locale)")) return "th";
            if (Regex.IsMatch(n, @"colou?r")) return "#2E7D32";
            if (Regex.IsMatch(n, @"(^|_)(code|key|token|slug|ref|reference|uuid|sku)$")) return "SAMPLE-" + (i + 1).ToString("000", CultureInfo.InvariantCulture);
            if (Regex.IsMatch(n, @"(^|_)(status|state)$")) return "active";
            return "ตัวอย่าง" + no;
        }
    }
}
