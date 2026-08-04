using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Mcp;

namespace CluadeX.Services;

/// <summary>
/// Bridge between local CluadeX state (Instincts today, more later) and
/// the ObsidianX brain via MCP. The brain MCP must be configured + running
/// in the user's MCP Servers page; this service is graceful when it's not.
///
/// Discovery: looks for any MCP server whose name contains "obsidianx" or
/// "brain" AND exposes a `brain_create_note` tool. The user can name the
/// server anything — we match by tool surface, not by display name.
///
/// Sprint 2 #1 + bridge: STRONG-confidence instincts (≥0.70) and promoted
/// instincts get pushed as `coding-lesson` notes so the knowledge survives
/// across machines / Claude Code sessions.
/// </summary>
public class BrainSyncService
{
    private readonly McpServerManager _mcp;
    private readonly DebugLogService _log;

    public BrainSyncService(McpServerManager mcp, DebugLogService log)
    {
        _mcp = mcp;
        _log = log;
    }

    /// <summary>True if at least one configured MCP server is running AND looks like an ObsidianX brain.</summary>
    public bool IsBrainAvailable => FindBrainServer() != null;

    /// <summary>Human-friendly status for the UI status bar.</summary>
    public string StatusText
    {
        get
        {
            var name = FindBrainServer();
            if (name == null) return "Brain not connected";
            return $"Brain connected: {name}";
        }
    }

    /// <summary>
    /// Pick the first running MCP server whose name suggests it's an ObsidianX
    /// brain. We don't enforce a specific tool list here because querying
    /// every server's tool list is wasteful — the call will surface "tool
    /// not found" if the server is misconfigured.
    ///
    /// The name rule lives in <see cref="McpServerManager.LooksLikeBrain"/> so
    /// this service, the auto-recall gate and the status chip can never disagree
    /// about which server is "the brain".
    /// </summary>
    private string? FindBrainServer()
    {
        foreach (var name in _mcp.GetRunningServers())
        {
            if (McpServerManager.LooksLikeBrain(name))
                return name;
        }
        return null;
    }

    // ─── Instinct → brain note ──────────────────────────────────────

    public class BrainSyncResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? NoteId { get; set; }
        public string? NotePath { get; set; }
    }

    /// <summary>
    /// Push a single instinct as a coding-lesson note. If BrainNoteId is
    /// already set, this appends an update rather than creating a duplicate.
    /// Returns a result object explaining what happened (success path AND
    /// the various failure paths so the UI can be honest).
    /// </summary>
    public async Task<BrainSyncResult> SyncInstinctAsync(Instinct instinct, CancellationToken ct = default)
    {
        var server = FindBrainServer();
        if (server == null)
        {
            return new BrainSyncResult
            {
                Message = "ObsidianX brain MCP server is not running. Configure one in MCP Servers (Ctrl+5)."
            };
        }

        try
        {
            // Already synced once → append update note rather than create new
            if (!string.IsNullOrEmpty(instinct.BrainNoteId))
            {
                var appendArgs = new Dictionary<string, object>
                {
                    ["id"] = instinct.BrainNoteId,
                    ["content"] = BuildAppendBlock(instinct),
                };
                _log.Debug("BrainSync", $"Appending update to existing note {instinct.BrainNoteId}");
                var appendResult = await _mcp.CallToolWithObjectArgsAsync(server, "brain_append_note", appendArgs, ct);
                if (appendResult.IsError)
                {
                    var msg = ExtractText(appendResult);
                    _log.Warn("BrainSync", $"brain_append_note failed: {msg}");
                    // Fall through and try create instead — note may have been deleted
                }
                else
                {
                    _log.Info("BrainSync", $"Updated brain note for instinct '{instinct.Pattern}'");
                    return new BrainSyncResult
                    {
                        Success = true,
                        Message = "Updated existing brain note",
                        NoteId = instinct.BrainNoteId,
                    };
                }
            }

            // Create a fresh note.
            // FIX (audit CRITICAL #4): tags must reach brain as a real JSON
            // array, not a quoted string. Use the object-typed overload so
            // `BuildTags(instinct)` (a List<string>) serialises natively.
            var createArgs = new Dictionary<string, object>
            {
                ["title"] = BuildTitle(instinct),
                ["folder"] = "Notes/Coding-Lessons",
                ["tags"] = BuildTags(instinct),
                ["content"] = BuildNoteBody(instinct),
            };
            _log.Debug("BrainSync", $"Creating new brain note for '{instinct.Pattern}'");
            var result = await _mcp.CallToolWithObjectArgsAsync(server, "brain_create_note", createArgs, ct);

            if (result.IsError)
            {
                var msg = ExtractText(result);
                _log.Error("BrainSync", $"brain_create_note failed: {msg}");
                return new BrainSyncResult { Message = $"Brain rejected note: {msg}" };
            }

            // FIX (audit HIGH #9): only assign BrainNoteId when the brain
            // actually returned one. A null id used to be replaced with a
            // fake Guid, which then made every subsequent sync try to
            // append against an id the brain never heard of.
            var (id, path) = ExtractIdAndPath(result);
            if (!string.IsNullOrEmpty(id))
            {
                instinct.BrainNoteId = id;
                _log.Info("BrainSync", $"Synced '{instinct.Pattern}' → {path ?? "(no path)"}");
                return new BrainSyncResult
                {
                    Success = true,
                    Message = $"Synced to brain ({path ?? "ok"})",
                    NoteId = id,
                    NotePath = path,
                };
            }
            else
            {
                _log.Warn("BrainSync",
                    $"brain_create_note returned ok but no id field — skipping BrainNoteId assignment so retry creates fresh");
                return new BrainSyncResult
                {
                    Success = true,
                    Message = "Synced (no id returned — will recreate next time)",
                    NotePath = path,
                };
            }
        }
        catch (Exception ex)
        {
            _log.Error("BrainSync", "SyncInstinctAsync threw", ex);
            return new BrainSyncResult { Message = $"Sync error: {ex.Message}" };
        }
    }

    /// <summary>Push everything currently at STRONG tier. Used by "Sync all STRONG" button.</summary>
    public async Task<(int synced, int skipped, int failed)> SyncAllStrongAsync(
        IEnumerable<Instinct> instincts, CancellationToken ct = default)
    {
        int synced = 0, skipped = 0, failed = 0;
        foreach (var i in instincts)
        {
            if (i.ConfidenceTier != "STRONG") { skipped++; continue; }
            if (i.SyncedToBrain) { skipped++; continue; }
            var r = await SyncInstinctAsync(i, ct);
            if (r.Success) synced++; else failed++;
        }
        _log.Info("BrainSync", $"Bulk sync: synced={synced} skipped={skipped} failed={failed}");
        return (synced, skipped, failed);
    }

    // ─── Brain → CluadeX recall (read path) ─────────────────────────

    /// <summary>
    /// Query the connected BrainX for relevant notes (coding-lessons, past decisions, bug fixes).
    /// Backs the `brain_recall` agent tool and the auto-recall context injector. Graceful when the
    /// brain MCP server isn't running — returns an explanatory string, never throws. The returned
    /// text is the brain's raw JSON result (title + preview + tags per hit), suitable as model context.
    /// </summary>
    public async Task<string> SearchAsync(string query, int limit = 5, bool semantic = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "(empty query)";

        var server = FindBrainServer();
        if (server == null)
            return "(BrainX not connected — configure a brain MCP server in MCP Servers, Ctrl+5)";

        try
        {
            var args = new Dictionary<string, object>
            {
                ["query"] = query,
                ["limit"] = limit,
            };
            string tool = semantic ? "brain_semantic_search" : "brain_search";
            var result = await _mcp.CallToolWithObjectArgsAsync(server, tool, args, ct);
            if (result.IsError)
            {
                var msg = ExtractText(result);
                _log.Warn("BrainSync", $"{tool} failed: {msg}");
                return $"(brain search failed: {msg})";
            }
            return ExtractText(result);
        }
        catch (Exception ex)
        {
            _log.Warn("BrainSync", $"SearchAsync threw: {ex.Message}");
            return $"(brain search error: {ex.Message})";
        }
    }

    /// <summary>
    /// Turn the brain's raw search JSON into something a 7B can actually read.
    ///
    /// <see cref="SearchAsync"/> returns the tool's JSON verbatim — ids, scores,
    /// tag arrays, `kind`, `appliesTo`, escaped Thai. Pasting that into a local
    /// model's context spends ~2-3× the tokens of the same facts in prose and
    /// reads as noise: the owner's report was that CluadeX searched the brain,
    /// then answered in one confused line ("งง อะไรก็ไม่รู้"). A weak model does
    /// not parse JSON for meaning; it pattern-matches text.
    ///
    /// Keeps the id, because that is the handle for a follow-up brain_get_note.
    /// Falls back to the raw string on any shape it doesn't recognise — a
    /// formatter is not worth losing the content over.
    /// </summary>
    public static string FormatSearchResultsForModel(string rawJson, int maxCharsPerHit = 260)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return rawJson;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return rawJson;
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array) return rawJson;

            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in results.EnumerateArray())
            {
                string title = Str(r, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                n++;

                // matchContext says WHY this note came back; preview is the note's
                // opening. Prefer the former — "why you are reading this" beats
                // "here is the top of a file you did not ask for".
                string why = Str(r, "matchContext");
                if (string.IsNullOrWhiteSpace(why)) why = Str(r, "preview");
                why = Collapse(why);
                if (why.Length > maxCharsPerHit) why = why[..maxCharsPerHit].TrimEnd() + "…";

                sb.Append(n).Append(". \"").Append(title).Append('"');
                string id = Str(r, "id");
                if (!string.IsNullOrEmpty(id)) sb.Append("  (id: ").Append(id).Append(')');
                sb.AppendLine();
                if (why.Length > 0) sb.Append("   ").AppendLine(why);
            }
            return n == 0 ? rawJson : sb.ToString().TrimEnd();
        }
        catch
        {
            return rawJson;
        }

        static string Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object
               && e.TryGetProperty(prop, out var v)
               && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        static string Collapse(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var c in s)
            {
                bool ws = char.IsWhiteSpace(c);
                if (ws) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }
    }

    // ─── Note formatting ────────────────────────────────────────────

    private static string BuildTitle(Instinct i)
    {
        // Brain titles work best as short noun phrases. Truncate long
        // patterns and drop trailing punctuation.
        var t = i.Pattern.Trim().TrimEnd('.', ':', ';', '!', '?');
        if (t.Length > 80) t = t[..80].TrimEnd() + "…";
        return $"CluadeX instinct — {t}";
    }

    private static List<string> BuildTags(Instinct i)
    {
        var tags = new List<string>
        {
            "coding-lesson",
            "cluadex",
            "instinct",
            $"confidence-{i.ConfidenceTier.ToLowerInvariant()}",
        };
        foreach (var t in i.Tags)
        {
            var safe = t.Trim().ToLowerInvariant().Replace(" ", "-");
            if (!string.IsNullOrEmpty(safe) && !tags.Contains(safe)) tags.Add(safe);
        }
        return tags;
    }

    private static string BuildNoteBody(Instinct i)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {BuildTitle(i)}");
        sb.AppendLine();
        sb.AppendLine($"> **Confidence:** {i.Confidence:P0} ({i.ConfidenceTier}) · " +
                      $"observed {i.OccurrenceCount}× · " +
                      $"accepted {i.AcceptCount}× · rejected {i.RejectCount}×");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(i.Trigger))
        {
            sb.AppendLine("## When");
            sb.AppendLine(i.Trigger);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(i.Action))
        {
            sb.AppendLine("## Do");
            sb.AppendLine(i.Action);
            sb.AppendLine();
        }
        sb.AppendLine("## Provenance");
        sb.AppendLine($"- Source: CluadeX Instinct `{i.Id}`");
        sb.AppendLine($"- First seen: {i.FirstSeen:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Last seen: {i.LastSeen:yyyy-MM-dd HH:mm}");
        if (i.Promoted) sb.AppendLine($"- Promoted to skill: `{System.IO.Path.GetFileName(i.PromotedSkillPath ?? "")}`");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Auto-synced from CluadeX. Edit-and-it-stays-edited: future syncs append " +
                      "updates instead of overwriting this body._");
        return sb.ToString();
    }

    private static string BuildAppendBlock(Instinct i)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"## Update — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- Confidence now: {i.Confidence:P0} ({i.ConfidenceTier})");
        sb.AppendLine($"- Observed: {i.OccurrenceCount}× (accept {i.AcceptCount} · reject {i.RejectCount})");
        sb.AppendLine($"- Last seen: {i.LastSeen:yyyy-MM-dd HH:mm}");
        return sb.ToString();
    }

    private static string ExtractText(McpToolResult result)
    {
        if (result.Content == null || result.Content.Count == 0) return "(no content)";
        var first = result.Content[0];
        return first?.Text ?? "(empty)";
    }

    /// <summary>
    /// brain_create_note typically returns JSON like { "success": true, "id": "abc123", "path": "..." }.
    /// Best-effort parse — if shape differs, we just return nulls and let the caller fall back.
    /// </summary>
    private static (string? id, string? path) ExtractIdAndPath(McpToolResult result)
    {
        try
        {
            var text = ExtractText(result);
            using var doc = JsonDocument.Parse(text);
            string? id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
            return (id, path);
        }
        catch
        {
            return (null, null);
        }
    }
}
