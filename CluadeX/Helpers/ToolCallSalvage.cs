using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Helpers;

/// <summary>
/// Recovers tool calls that a WEAK local model emitted as prose / JSON inside message <c>content</c>
/// instead of via the structured <c>tool_calls</c> mechanism, and suggests the closest valid tool name
/// when the model misspells one. Both are corrective-feedback levers: without them a weak model that
/// "almost" called a tool either ends the turn silently or gets a bare error it can't act on.
///
/// Everything here is DETERMINISTIC (no model calls) and only accepts a salvaged call when its name
/// resolves to a real, registered tool — so random JSON in a reply is never mistaken for a tool call.
/// </summary>
public static class ToolCallSalvage
{
    private static readonly Regex FenceRegex =
        new(@"```(?:json|tool_call|tool|js|javascript)?\s*(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    // Keys a model might use for the tool name / its arguments, across the many shapes weak models emit.
    private static readonly string[] NameKeys = { "name", "tool", "tool_name", "toolName", "action", "function_name" };
    private static readonly string[] ArgKeys = { "arguments", "parameters", "input", "args", "params" };

    /// <summary>
    /// Extract tool calls from free text. Returns (name, input) pairs whose name passes
    /// <paramref name="isKnownTool"/>. Empty when nothing salvageable is found.
    /// </summary>
    public static List<(string Name, JsonElement Input)> ExtractFromText(string? text, Func<string, bool> isKnownTool)
    {
        var results = new List<(string, JsonElement)>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        foreach (var candidate in EnumerateJsonCandidates(text))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                CollectCalls(doc.RootElement, isKnownTool, results);
            }
            catch { /* candidate isn't valid JSON — try the next one */ }
            if (results.Count > 0) break; // first candidate that yields a known tool call wins
        }
        return results;
    }

    /// <summary>Suggest the closest valid tool name to a misspelled one (Levenshtein on a normalized form),
    /// or null when nothing is close enough. Used to turn "Unknown tool X" into "did you mean Y?".</summary>
    public static string? SuggestClosestName(string unknown, IEnumerable<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(unknown)) return null;
        string u = Normalize(unknown);
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            int d = Levenshtein(u, Normalize(c));
            if (d < bestDist) { bestDist = d; best = c; }
        }
        // Only suggest when the names are genuinely close: within 1/3 of the length (min 2).
        int threshold = Math.Max(2, u.Length / 3);
        return best != null && bestDist <= threshold ? best : null;
    }

    // ─── JSON candidate enumeration ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        foreach (Match m in FenceRegex.Matches(text))
            yield return m.Groups[1].Value.Trim();
        // Arrays before objects: a multi-call array must be parsed WHOLE, before its individual
        // objects are extracted one at a time (which would stop after the first call).
        foreach (var c in ExtractBalanced(text, '[', ']')) yield return c;
        foreach (var c in ExtractBalanced(text, '{', '}')) yield return c;
        yield return text.Trim();
    }

    /// <summary>Yield each top-level balanced <paramref name="open"/>…<paramref name="close"/> region in
    /// the text, respecting string literals and escapes so braces inside strings don't throw off the depth.</summary>
    private static IEnumerable<string> ExtractBalanced(string text, char open, char close)
    {
        int depth = 0, start = -1;
        bool inStr = false, esc = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == open)
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == close && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text.Substring(start, i - start + 1);
                    start = -1;
                }
            }
        }
    }

    // ─── Call collection from a parsed JSON element ──────────────────────────────────────────────

    private static void CollectCalls(JsonElement el, Func<string, bool> isKnownTool, List<(string, JsonElement)> outList)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectCalls(item, isKnownTool, outList);
                break;
            case JsonValueKind.Object:
                // OpenAI shape: { "tool_calls": [ { "function": { "name", "arguments" } } ] }
                if (el.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                        CollectCalls(tc, isKnownTool, outList);
                    return;
                }
                if (TryReadCall(el, isKnownTool, out var name, out var input))
                    outList.Add((name, input));
                break;
        }
    }

    private static bool TryReadCall(JsonElement obj, Func<string, bool> isKnownTool, out string name, out JsonElement input)
    {
        name = "";
        input = EmptyObject();

        // A wrapped { "function": { "name", "arguments" } } element.
        if (obj.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            obj = fn;

        foreach (var key in NameKeys)
        {
            if (obj.TryGetProperty(key, out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var n = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(n)) { name = n.Trim(); break; }
            }
        }
        if (string.IsNullOrEmpty(name) || !isKnownTool(name)) return false;

        foreach (var key in ArgKeys)
        {
            if (!obj.TryGetProperty(key, out var argEl)) continue;
            if (argEl.ValueKind == JsonValueKind.Object) { input = argEl.Clone(); return true; }
            if (argEl.ValueKind == JsonValueKind.String)
            {
                // Arguments encoded as a JSON STRING (OpenAI style) — parse it.
                var raw = argEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try { using var d = JsonDocument.Parse(raw); if (d.RootElement.ValueKind == JsonValueKind.Object) { input = d.RootElement.Clone(); return true; } }
                    catch { /* not JSON — ignore, leave empty input */ }
                }
            }
        }
        // Name resolved to a real tool but no args object found — still a valid (no-arg) call.
        return true;
    }

    private static JsonElement EmptyObject()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lowercase + strip non-alphanumerics so "readFile" / "read-file" / "Read_File" all compare equal.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
