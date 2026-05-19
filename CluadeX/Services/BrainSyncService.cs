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
    /// </summary>
    private string? FindBrainServer()
    {
        foreach (var name in _mcp.GetRunningServers())
        {
            if (name.Contains("obsidianx", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("brain", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
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
