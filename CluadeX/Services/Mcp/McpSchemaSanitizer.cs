using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Turns an MCP server's tool <c>inputSchema</c> into ONE form that the Anthropic, OpenAI and Gemini tool APIs all
/// accept — keeping the structure the model needs, instead of the old flattening to {name: {type, description}}.
///
/// The old builder lost items, enum and every nested property, and a property whose "type" was an array
/// (["string","null"]) threw inside its loop, so every parameter AFTER it silently vanished from the tool.
///
/// Kept: type, properties, items, enum, required, description, default — recursively, through nested objects and
/// arrays. Changed: a type array collapses to its first non-null type; an array without items gets
/// {"type":"string"} items; anyOf/oneOf become their first non-null branch, allOf is merged; "const" becomes a
/// one-value enum; local $refs (#/$defs/…, #/definitions/…) are inlined. Folded into the description: format and the
/// value limits (minimum/maximum, minLength/maxLength, pattern, minItems/maxItems) — so the model still knows them.
/// Dropped: $schema, $id, $defs, title, additionalProperties, unresolvable or recursive $refs, a property whose name
/// the APIs refuse (not ^[a-zA-Z0-9_.-]{1,64}$ — one would 400 the WHOLE request) and every other keyword.
/// The root is always {"type":"object","properties":{…}}. Never throws.
///
/// Inlining $refs can multiply a schema: a definition used in several places, nested a few levels, is a "schema
/// bomb" (1.5 KB became 11.9 MB). Each tool has a budget (<see cref="MaxNodes"/> nodes and
/// <see cref="CharBudgetFor"/> characters); past it, deeper parts are offered as a plain value of their type with a
/// note. Enum values, defaults and descriptions count against that budget before they go in: an enum longer than
/// <see cref="MaxEnumChars"/> (or than what is left) is offered as a plain value whose description names the count and
/// the first values, and duplicates are dropped in O(n). Results are memoized per (server, tool, schema) by
/// <see cref="SanitizeCached"/>, which the per-turn schema build and the argument typing use.
/// </summary>
public static class McpSchemaSanitizer
{
    /// <summary>Deeper nesting than this is offered as a JSON string (a pathological or recursive schema).</summary>
    public const int MaxDepth = 12;

    /// <summary>Most nodes one tool's sanitized schema may have before deeper parts are collapsed.</summary>
    public const int MaxNodes = 2000;

    /// <summary>Smallest character budget for one tool's sanitized schema (a large hand-written schema gets twice its
    /// own size — only an expansion is cut).</summary>
    public const int MinCharBudget = 32 * 1024;

    /// <summary>The description a collapsed (over-budget) part carries.</summary>
    public const string TooLargeNote = "(too large to describe here: pass a JSON value of this type)";

    /// <summary>Property names all three tool APIs accept (Anthropic refuses the whole request otherwise).</summary>
    private static readonly Regex PropertyName = new("^[a-zA-Z0-9_.-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The character budget for a schema whose raw text is <paramref name="rawLength"/> long.</summary>
    public static int CharBudgetFor(int rawLength) => Math.Min(MaxCharBudget, Math.Max(MinCharBudget, rawLength * 2));

    /// <summary>Largest character budget for one tool, however large its own schema: one tool must not fill a request.</summary>
    public const int MaxCharBudget = 256 * 1024;

    /// <summary>Largest enum (as JSON text) offered as an enum; a longer one — or one past the tool's remaining
    /// budget — becomes a plain value whose description names the count and the first values.</summary>
    public const int MaxEnumChars = 16 * 1024;

    /// <summary>How much of a too-long enum the description lists.</summary>
    private const int EnumExampleChars = 400;

    /// <summary>A description is cut to the tool's remaining budget, but never below this…</summary>
    private const int MinDescriptionChars = 256;

    /// <summary>…and never longer than this (a parameter's description, not a document).</summary>
    public const int MaxDescriptionChars = 16 * 1024;

    /// <summary>The note a too-long enum's description carries (for the harness).</summary>
    public const string TooManyValuesNote = "too many to list here";

    /// <summary>One enum value's identity, for de-duplicating in O(n): strings by value, numbers by value (invariant
    /// culture — 1 and 1.0 are the same), anything else by its JSON text.</summary>
    private static string EnumKey(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "s" + v.GetString(),
        JsonValueKind.Number => "n" + (v.TryGetDouble(out double d)
            ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : v.GetRawText()),
        _ => v.ValueKind + v.GetRawText(),
    };

    private static readonly ConcurrentDictionary<string, JsonElement> Cache = new(StringComparer.Ordinal);
    // Hundreds of tools is normal (BrainX alone offers ~150); a server that keeps changing its schemas only churns this.
    private const int MaxCacheEntries = 1024;

    /// <summary>
    /// <see cref="Sanitize"/>, memoized by server, tool and the schema's exact text (a server that changes a tool's
    /// schema gets a fresh entry). Safe from any thread.
    /// </summary>
    public static JsonElement SanitizeCached(string? serverName, string? toolName, JsonElement? inputSchema)
    {
        string raw;
        try { raw = inputSchema is { ValueKind: not JsonValueKind.Undefined } e ? e.GetRawText() : ""; }
        catch { return Sanitize(inputSchema); }
        string key = $"{serverName}\u0001{toolName}\u0001{raw.Length}\u0001{Hash(raw)}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var result = Sanitize(inputSchema);
        if (Cache.Count >= MaxCacheEntries) Cache.Clear();
        Cache[key] = result;
        return result;
    }

    /// <summary>How many sanitized schemas are memoized (for the harness).</summary>
    public static int CachedCount => Cache.Count;

    private static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>The declared type of each top-level parameter as the model is told it (after $ref / anyOf / type
    /// arrays are resolved), or an empty map. What the argument typing in McpServerManager.CallToolAsync follows.</summary>
    public static IReadOnlyDictionary<string, string> DeclaredTypes(JsonElement sanitized)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sanitized.ValueKind == JsonValueKind.Object && sanitized.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    map[p.Name] = t.GetString() ?? "";
        return map;
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
        { "string", "number", "integer", "boolean", "array", "object" };

    /// <summary>The sanitized schema (a detached element, safe to keep).</summary>
    public static JsonElement Sanitize(JsonElement? inputSchema)
    {
        JsonObject root;
        try { root = SanitizeRoot(inputSchema); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP schema] could not sanitize ({ex.GetType().Name}) — offering no parameters");
            root = EmptyRoot();
        }
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>The single type a property declares, the way the sanitizer reads it ("string" for
    /// ["string","null"], the first non-null branch of anyOf/oneOf), or null when it declares none. A $ref can't be
    /// followed from a lone property — callers that have the whole schema use <see cref="DeclaredTypes"/>.</summary>
    public static string? TypeOf(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object) return null;
        if (property.TryGetProperty("type", out var t)) return CollapseType(t);
        foreach (var combinator in new[] { "anyOf", "oneOf" })
        {
            if (!property.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in branches.EnumerateArray())
                if (!IsNullOnly(b) && TypeOf(b) is { } bt) return bt;
        }
        return null;
    }

    private static JsonObject EmptyRoot() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static JsonObject SanitizeRoot(JsonElement? inputSchema)
    {
        if (inputSchema is not { ValueKind: JsonValueKind.Object } schema) return EmptyRoot();
        var ctx = new Context(schema, CharBudgetFor(schema.GetRawText().Length));
        var node = SanitizeNode(schema, ctx, 0, new HashSet<string>(StringComparer.Ordinal));
        if (node["type"]?.GetValue<string>() != "object") return EmptyRoot();
        if (node["properties"] is not JsonObject) node["properties"] = new JsonObject();
        // A root description/default means nothing to the tool APIs.
        node.Remove("default");
        node.Remove("enum");
        return node;
    }

    private sealed class Context
    {
        public readonly JsonElement Root;
        public readonly int CharBudget;
        public int Nodes;
        public int Chars;
        public Context(JsonElement root, int charBudget) { Root = root; CharBudget = charBudget; }
        public bool OverBudget => Nodes > MaxNodes || Chars > CharBudget;

        /// <summary>"#/$defs/Name", "#/definitions/Name" (and deeper JSON pointers) inside this schema.</summary>
        public bool TryResolve(string reference, out JsonElement target)
        {
            target = default;
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;
            JsonElement cur = Root;
            foreach (var raw in reference[2..].Split('/'))
            {
                string seg = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out var next)) return false;
                cur = next;
            }
            if (cur.ValueKind is not (JsonValueKind.Object or JsonValueKind.True)) return false;
            target = cur;
            return true;
        }
    }

    private static JsonObject SanitizeNode(JsonElement node, Context ctx, int depth, HashSet<string> activeRefs)
    {
        // Budget: a $ref used many times over, nested, multiplies — past the budget, a part is just "a value of its type".
        // Every node returned is counted, including the small ones below (uncounted, 117,649 "too deep" leaves of a
        // schema bomb came to 222 KB).
        ctx.Nodes++;
        ctx.Chars += 24;
        // Boolean schemas (true = anything) and other non-objects: offer a plain string.
        if (node.ValueKind != JsonValueKind.Object) return new JsonObject { ["type"] = "string" };
        if (ctx.OverBudget)
        {
            ctx.Chars += TooLargeNote.Length;
            return Collapsed(node, ctx);
        }
        if (depth > MaxDepth)
        {
            ctx.Chars += 60;
            return new JsonObject { ["type"] = "string", ["description"] = "A JSON value (the schema nests too deeply to describe)." };
        }

        string? description = node.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

        // ── $ref: inline a local definition, once per path (a recursive one is dropped) ──
        if (node.TryGetProperty("$ref", out var refEl) && refEl.ValueKind == JsonValueKind.String)
        {
            string reference = refEl.GetString() ?? "";
            if (!activeRefs.Contains(reference) && ctx.TryResolve(reference, out var target) && target.ValueKind == JsonValueKind.Object)
            {
                activeRefs.Add(reference);
                var resolved = SanitizeNode(target, ctx, depth + 1, activeRefs);
                activeRefs.Remove(reference);
                if (description != null) resolved["description"] = description;
                return resolved;
            }
            // Unresolvable or recursive: keep whatever else this node says; with nothing else, an object.
            bool hasOther = node.EnumerateObject().Any(p => p.Name is "type" or "properties" or "items" or "enum" or "anyOf" or "oneOf" or "allOf");
            if (!hasOther)
            {
                var obj = new JsonObject { ["type"] = "object" };
                if (description != null) obj["description"] = description;
                return obj;
            }
        }

        // ── anyOf / oneOf: the first branch that is not just null ──
        foreach (var combinator in new[] { "anyOf", "oneOf" })
        {
            if (!node.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            JsonElement? pick = null;
            foreach (var b in branches.EnumerateArray())
            {
                if (IsNullOnly(b)) continue;
                pick = b;
                break;
            }
            if (pick is { } chosen)
            {
                // The node's own keywords still apply: {"type":"object","properties":{…},"anyOf":[{"required":["a"]},…]}
                // is an object whose anyOf only constrains it — taking the branch alone would lose every property.
                var outer = SanitizeNode(Without(node, combinator, "$ref"), ctx, depth + 1, activeRefs);
                if (!HasShape(chosen)) return outer;
                var branch = SanitizeNode(chosen, ctx, depth + 1, activeRefs);
                if (!HasShape(Without(node, combinator))) return Overlay(branch, outer);
                bool typed = true;
                return MergeAll(outer, branch, ref typed);
            }
        }

        // ── allOf: merge the branches (properties and required add up) ──
        if (node.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            var acc = SanitizeNode(Without(node, "allOf", "$ref"), ctx, depth + 1, activeRefs);
            bool typed = node.TryGetProperty("type", out _);
            foreach (var b in all.EnumerateArray())
            {
                var part = SanitizeNode(b, ctx, depth + 1, activeRefs);
                acc = MergeAll(acc, part, ref typed);
            }
            return acc;
        }

        // A type array (["string","null"]) collapses to its first real type: a nullable property is simply optional
        // for the model, so the null branch (and a null enum value) goes away.
        var result = new JsonObject();
        string type = node.TryGetProperty("type", out var typeEl) ? CollapseType(typeEl) ?? InferType(node) : InferType(node);
        result["type"] = type;

        // format and the value limits are not accepted everywhere; their meaning survives in the description.
        if (node.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fmt.GetString()))
            description = string.IsNullOrWhiteSpace(description) ? $"Format: {fmt.GetString()}." : $"{description} (format: {fmt.GetString()})";
        if (LimitsNote(node) is { } limits)
            description = string.IsNullOrWhiteSpace(description) ? $"Limits: {limits}." : $"{description} (limits: {limits})";

        // enum / const — only values of the declared type (a null from ["string","null"] goes away), each once.
        // Counted against the budget BEFORE it goes in: a 20,000-value enum was de-duplicated pairwise (O(n²), a
        // minute on the first turn) and then sent whole, however large. Past MaxEnumChars or the tool's remaining
        // budget, the list is not offered as an enum (a partial one would forbid the values left out): the parameter
        // becomes a plain value of its type, and the description names how many values there are and the first ones.
        var values = new JsonArray();
        if (node.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
        {
            int room = Math.Max(0, Math.Min(MaxEnumChars, ctx.CharBudget - ctx.Chars));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var first = new List<string>();    // the first values, for the description when the list is too long
            int distinct = 0, chars = 2, firstChars = 0;
            bool fits = true, firstFull = false;
            foreach (var v in en.EnumerateArray())
            {
                if (!Matches(v, type) || !seen.Add(EnumKey(v))) continue;
                distinct++;
                string text = v.GetRawText();
                if (!firstFull)
                {
                    if (firstChars + text.Length + 2 <= EnumExampleChars) { first.Add(text); firstChars += text.Length + 2; }
                    else firstFull = true;
                }
                if (!fits) continue;
                chars += text.Length + 1;
                if (chars > room) { fits = false; values.Clear(); }
                else values.Add(JsonNode.Parse(text));
            }
            if (!fits)
            {
                string note = $"one of {distinct.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} allowed values — {TooManyValuesNote}; the first {first.Count}: {string.Join(", ", first)}";
                description = string.IsNullOrWhiteSpace(description) ? $"Allowed values: {note}." : $"{description} (allowed values: {note})";
            }
        }
        else if (node.TryGetProperty("const", out var cst) && Matches(cst, type) && cst.GetRawText().Length <= MaxEnumChars)
            values.Add(JsonNode.Parse(cst.GetRawText()));
        if (values.Count > 0)
        {
            result["enum"] = values;
            ctx.Chars += values.ToJsonString().Length;
        }

        if (!string.IsNullOrEmpty(description))
        {
            // One description must not fill the request either (nor starve the parameters after it).
            int roomForText = Math.Min(MaxDescriptionChars, Math.Max(MinDescriptionChars, ctx.CharBudget - ctx.Chars));
            if (description.Length > roomForText)
            {
                if (char.IsHighSurrogate(description[roomForText - 1])) roomForText--;   // never half an emoji
                description = description[..roomForText] + "…";
            }
            result["description"] = description;
            ctx.Chars += description.Length;
        }

        if (node.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null && Matches(def, type))
        {
            string defText = def.GetRawText();
            if (defText.Length <= Math.Min(MaxEnumChars, Math.Max(0, ctx.CharBudget - ctx.Chars)))
            {
                result["default"] = JsonNode.Parse(defText);
                ctx.Chars += defText.Length;
            }
        }

        if (type == "object")
        {
            var props = new JsonObject();
            if (node.TryGetProperty("properties", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pEl.EnumerateObject())
                {
                    if (string.IsNullOrEmpty(p.Name) || props.ContainsKey(p.Name)) continue;
                    if (!PropertyName.IsMatch(p.Name))
                    {
                        // Anthropic refuses the WHOLE request for one such key: leave this parameter out instead.
                        System.Diagnostics.Debug.WriteLine($"[MCP schema] parameter '{p.Name}' left out: its name is not ^[a-zA-Z0-9_.-]{{1,64}}$");
                        continue;
                    }
                    ctx.Chars += p.Name.Length + 4;
                    props[p.Name] = SanitizeNode(p.Value, ctx, depth + 1, activeRefs);
                }
            }
            if (props.Count > 0) result["properties"] = props;
            var required = new JsonArray();
            if (node.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.Array)
            {
                var listed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in rq.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String) continue;
                    string name = r.GetString() ?? "";
                    // Only names that exist: Gemini rejects a required property it was never told about.
                    if (props.ContainsKey(name) && listed.Add(name)) required.Add(name);
                }
            }
            if (required.Count > 0) result["required"] = required;
        }
        else if (type == "array")
        {
            JsonElement? itemSchema = null;
            if (node.TryGetProperty("items", out var it))
            {
                if (it.ValueKind == JsonValueKind.Object) itemSchema = it;
                else if (it.ValueKind == JsonValueKind.Array && it.GetArrayLength() > 0) itemSchema = it[0];   // tuple form
            }
            if (itemSchema == null && node.TryGetProperty("prefixItems", out var pre) && pre.ValueKind == JsonValueKind.Array && pre.GetArrayLength() > 0)
                itemSchema = pre[0];
            // OpenAI (and Gemini) reject an array schema without items — and with it the WHOLE tools request.
            result["items"] = itemSchema is { } i ? SanitizeNode(i, ctx, depth + 1, activeRefs) : new JsonObject { ["type"] = "string" };
        }

        return result;
    }

    /// <summary>An over-budget part: a plain value of the type it declares, with a note — no recursion into it, so the
    /// cost stops here. A reference is followed ONE step to read the type it points at (a $ref to a string stays a
    /// string — it was offered, and typed, as an object); branches give their first non-null branch's type; "object"
    /// is the fallback only when neither says.</summary>
    private static JsonObject Collapsed(JsonElement node, Context ctx)
    {
        string type = ShallowType(node, ctx, followRef: true)
            ?? (node.TryGetProperty("$ref", out _) || node.TryGetProperty("anyOf", out _) || node.TryGetProperty("oneOf", out _)
                || node.TryGetProperty("allOf", out _) ? "object" : InferType(node));
        var o = new JsonObject { ["type"] = type, ["description"] = TooLargeNote };
        if (type == "array") o["items"] = new JsonObject { ["type"] = "string" };
        return o;
    }

    /// <summary>The type a node declares without sanitizing it: its own "type", else — once — the type its local $ref
    /// points at, else its first non-null anyOf/oneOf branch's; null when none says. Bounded: one $ref step, one branch
    /// per level.</summary>
    private static string? ShallowType(JsonElement node, Context ctx, bool followRef)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("type", out var t) && CollapseType(t) is { } declared) return declared;
        if (followRef && node.TryGetProperty("$ref", out var r) && r.ValueKind == JsonValueKind.String
            && ctx.TryResolve(r.GetString() ?? "", out var target) && target.ValueKind == JsonValueKind.Object)
            return ShallowType(target, ctx, followRef: false)
                ?? (target.TryGetProperty("properties", out _) || target.TryGetProperty("items", out _)
                    || target.TryGetProperty("enum", out _) || target.TryGetProperty("const", out _) ? InferType(target) : null);
        foreach (var combinator in new[] { "anyOf", "oneOf" })
        {
            if (!node.TryGetProperty(combinator, out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in branches.EnumerateArray())
            {
                if (IsNullOnly(b)) continue;
                return ShallowType(b, ctx, followRef);
            }
        }
        return null;
    }

    /// <summary>"minimum 1, maximum 10, at most (characters) 64, pattern ^[a-z]+$, …" — or null.</summary>
    private static string? LimitsNote(JsonElement node)
    {
        var parts = new List<string>();
        void Num(string key, string label)
        {
            if (node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number) parts.Add($"{label} {v.GetRawText()}");
        }
        Num("minimum", "minimum");
        Num("exclusiveMinimum", "greater than");
        Num("maximum", "maximum");
        Num("exclusiveMaximum", "less than");
        Num("multipleOf", "a multiple of");
        Num("minLength", "at least (characters)");
        Num("maxLength", "at most (characters)");
        Num("minItems", "at least (items)");
        Num("maxItems", "at most (items)");
        if (node.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pat.GetString()))
            parts.Add($"pattern {pat.GetString()}");
        if (node.TryGetProperty("uniqueItems", out var u) && u.ValueKind == JsonValueKind.True) parts.Add("items must be unique");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"string" | ["string","null"] | ["null","integer"] → the first non-null known type.</summary>
    private static string? CollapseType(JsonElement t)
    {
        if (t.ValueKind == JsonValueKind.String)
        {
            string s = t.GetString() ?? "";
            if (KnownTypes.Contains(s)) return s;
            return s == "null" ? "string" : null;
        }
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in t.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && KnownTypes.Contains(x.GetString() ?? "")) return x.GetString();
            return null;
        }
        return null;
    }

    private static string InferType(JsonElement node)
    {
        if (node.TryGetProperty("properties", out _)) return "object";
        if (node.TryGetProperty("items", out _) || node.TryGetProperty("prefixItems", out _)) return "array";
        JsonElement sample = default;
        if (node.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
            sample = en.EnumerateArray().FirstOrDefault(v => v.ValueKind != JsonValueKind.Null);
        else if (node.TryGetProperty("const", out var c)) sample = c;
        return sample.ValueKind switch
        {
            JsonValueKind.Number => sample.TryGetInt64(out _) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            _ => "string",
        };
    }

    private static bool Matches(JsonElement v, string type) => type switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "number" => v.ValueKind == JsonValueKind.Number,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    private static bool IsNullOnly(JsonElement b) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t)
        && ((t.ValueKind == JsonValueKind.String && t.GetString() == "null")
            || (t.ValueKind == JsonValueKind.Array && t.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString() == "null")));

    /// <summary>A copy of <paramref name="node"/> without the given keywords.</summary>
    private static JsonElement Without(JsonElement node, params string[] names)
    {
        var copy = new JsonObject();
        foreach (var p in node.EnumerateObject())
            if (!names.Contains(p.Name)) copy[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return JsonSerializer.SerializeToElement(copy);
    }

    /// <summary>Does this schema say what a value IS (a type, properties, items, values, a reference, branches),
    /// rather than only constrain it ({"required":["a"]})?</summary>
    private static bool HasShape(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any(p => p.Name is
            "type" or "properties" or "items" or "prefixItems" or "enum" or "const" or "$ref" or "anyOf" or "oneOf" or "allOf");

    /// <summary>The chosen branch, with the outer node's description (and a default of the same type) on top.</summary>
    private static JsonObject Overlay(JsonObject branch, JsonObject outer)
    {
        if (outer["description"] is JsonNode desc && branch["description"] == null) branch["description"] = desc.DeepClone();
        if (outer["default"] is JsonNode dflt && branch["default"] == null && outer["type"]?.GetValue<string>() == branch["type"]?.GetValue<string>())
            branch["default"] = dflt.DeepClone();
        return branch;
    }

    private static JsonObject MergeAll(JsonObject acc, JsonObject part, ref bool typed)
    {
        string? accType = acc["type"]?.GetValue<string>();
        string? partType = part["type"]?.GetValue<string>();
        if (!typed && partType != null)
        {
            acc["type"] = partType;
            accType = partType;
            typed = true;
        }
        if (acc["description"] == null && part["description"] is JsonNode d) acc["description"] = d.DeepClone();
        if (accType == "object" && partType == "object")
        {
            var props = acc["properties"] as JsonObject ?? new JsonObject();
            if (part["properties"] is JsonObject pp)
                foreach (var (k, v) in pp)
                    if (!props.ContainsKey(k) && v != null) props[k] = v.DeepClone();
            if (props.Count > 0) acc["properties"] = props;
            var req = acc["required"] as JsonArray ?? new JsonArray();
            if (part["required"] is JsonArray pr)
                foreach (var r in pr)
                    if (r != null && !req.Any(x => x?.GetValue<string>() == r.GetValue<string>())) req.Add(r.DeepClone());
            if (req.Count > 0) acc["required"] = req;
        }
        else if (accType == "array" && acc["items"] == null && part["items"] is JsonNode items)
        {
            acc["items"] = items.DeepClone();
        }
        // A merged object that ended up without items for an array type gets the default.
        if (acc["type"]?.GetValue<string>() == "array" && acc["items"] == null) acc["items"] = new JsonObject { ["type"] = "string" };
        return acc;
    }
}
