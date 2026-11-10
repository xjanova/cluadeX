using CluadeX.Services;

namespace CluadeX.Models;

/// <summary>
/// An "instinct" — a pattern the agent has observed enough times to
/// suspect it's a real behavioural rule of THIS project / THIS user.
/// Once an instinct has high enough confidence, it can be promoted to
/// a skill or auto-injected into the system prompt as a project rule.
///
/// Storage: JSON file at ~/.cluadex/instincts.json (v1 — SQLite later).
///
/// Lifecycle:
///   1. Observation lands (manual add, or Stop-hook extracts from transcript)
///   2. OccurrenceCount++, LastSeen=now
///   3. User reviews → AcceptCount++ or RejectCount++ → Confidence recomputed
///   4. Confidence ≥ 0.45 → injected into the prompt (global, or only in its own project)
///   5. Confidence ≥ 0.70 (needs an Accept) → synced to the brain, promotable to a skill
/// </summary>
public class Instinct
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>One-line summary of the pattern (shown as the card title).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>"When this happens" — the situation that triggers the rule.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>"Do this" — the action / preference / lesson the agent should apply.</summary>
    public string Action { get; set; } = "";

    /// <summary>Optional bucket so the user can filter (e.g. "git", "style", "perf", "security").</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>How many times the pattern has been observed in transcripts.</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>User explicitly said "yes, that's a real pattern".</summary>
    public int AcceptCount { get; set; }

    /// <summary>User explicitly said "no, that was a one-off / wrong".</summary>
    public int RejectCount { get; set; }

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>True once the user has promoted this to a skill or project rule.</summary>
    public bool Promoted { get; set; }

    /// <summary>If promoted as a skill, the resulting skill file name.</summary>
    public string? PromotedSkillPath { get; set; }

    /// <summary>
    /// If pushed to the ObsidianX brain via MCP, this is the note id returned
    /// by brain_create_note. Used both as "did we already sync this?" check
    /// and to enable future updates via brain_append_note instead of creating
    /// a duplicate.
    /// </summary>
    public string? BrainNoteId { get; set; }

    /// <summary>Convenience for UI: true if instinct has been synced to brain.</summary>
    public bool SyncedToBrain => !string.IsNullOrEmpty(BrainNoteId);

    /// <summary>
    /// The project this instinct belongs to — <see cref="Services.InstinctService.ProjectKey"/> of the folder the chat
    /// worked in (a git worktree counts as its main checkout). Null = global: it goes into every project's prompt.
    /// An Android project's "never install the APK" must not reach a web project.
    /// </summary>
    public string? ProjectKey { get; set; }

    /// <summary>The project's folder name, for the Instincts page (null when global).</summary>
    public string? ProjectName { get; set; }

    /// <summary>Stored before instincts had a project (store v1): nobody knows where it came from. Treated as global
    /// until a new sighting says which project it belongs to.</summary>
    public bool LegacyUnscoped { get; set; }

    /// <summary>The chats it was seen in (capped). A chat counts once: extracting the same chat again after more
    /// messages must not look like a second sighting.</summary>
    public List<string> SeenInSessions { get; set; } = new();

    /// <summary>Other wordings of the same rule, merged into this one (shown on the page, used by the word match).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Sightings where the user explicitly corrected the assistant into this behaviour, with their own
    /// words quoted from the transcript (checked, not taken on the model's word).</summary>
    public int CorrectionCount { get; set; }

    /// <summary>When the chat last asked the owner about it ("remember this next time?"); null = never. A card is
    /// not shown again for the same instinct within a week.</summary>
    public DateTime? LastAskedUtc { get; set; }

    /// <summary>The value an un-voted instinct's success rate starts at. Below 0.70 on purpose: what was only
    /// observed tops out at EMERGING (in the prompt), and STRONG (synced to the brain, promotable) needs the
    /// owner's own "yes".</summary>
    public const double UnvotedSuccess = 0.68;

    /// <summary>Confidence an instinct needs to go into the prompt.</summary>
    public const double InjectThreshold = 0.45;

    /// <summary>
    /// Confidence score 0..1.
    ///
    ///   confidence = success_rate × evidence_curve × recency_decay
    ///
    /// success_rate   : Accept / (Accept + Reject); <see cref="UnvotedSuccess"/> with no votes. One reject with no
    ///                  accept → 0: a "no" keeps it out of the prompt for good.
    /// evidence_curve : ln(1 + e) / ln(4), capped at 1, where e = sightings in distinct chats + 2 per accept
    ///                  + 1 per verified user correction. One sighting → 0.50, two (or one correction) → 0.79, three → 1.
    /// recency_decay  : 1 − min(days since last seen / 60, 0.5) — old patterns fade.
    ///
    /// So: seen once → 0.34 (not injected); seen in two chats, or once as an explicit correction → 0.54 (injected);
    /// accepted once → 1.0 (STRONG). The old curve (ln(1+n)/ln(11) × 0.5) needed eight exact repeats to reach 0.45,
    /// and since the extractor rewords every time, nothing ever did.
    /// </summary>
    public double Confidence
    {
        get
        {
            int votes = AcceptCount + RejectCount;
            double successRate = votes == 0 ? UnvotedSuccess : (double)AcceptCount / votes;
            double evidence = Math.Max(0, OccurrenceCount) + 2.0 * AcceptCount + Math.Max(0, CorrectionCount);
            double freq = Math.Min(1.0, Math.Log(1 + evidence) / Math.Log(4));
            double daysOld = Math.Max(0, (DateTime.UtcNow - LastSeen).TotalDays);
            double recency = 1.0 - Math.Min(daysOld / 60.0, 0.5);
            return Math.Round(successRate * freq * recency, 3);
        }
    }

    /// <summary>"every project" / "only in X" for the Instincts page.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ScopeLabel => ProjectKey == null
        ? LocalizedResources.Instance["instinct.card.scopeGlobal"]
        : string.Format(LocalizedResources.Instance["instinct.card.scopeProject"],
            ProjectName ?? InstinctService.ProjectNameOf(ProjectKey) ?? "");

    /// <summary>True when the owner has said yes or no to it (on the chat card or the Instincts page).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVoted => AcceptCount + RejectCount > 0;

    /// <summary>True when the owner said no more often than yes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRejected => RejectCount > AcceptCount;

    /// <summary>Bucket the confidence into a visible tier for the UI.</summary>
    public string ConfidenceTier
    {
        get
        {
            var c = Confidence;
            if (c >= 0.70) return "STRONG";
            if (c >= 0.45) return "EMERGING";
            if (c >= 0.20) return "WEAK";
            return "NOISE";
        }
    }

    /// <summary>Hex color matching the tier.</summary>
    public string ConfidenceColorHex
    {
        get
        {
            return ConfidenceTier switch
            {
                "STRONG"   => ThemeColors.Hex("Status.Success", "#5CFFB0"),  // mint — ready to promote
                "EMERGING" => ThemeColors.Hex("Accent.1", "#4CDFFF"),  // cyan — keep watching
                "WEAK"     => ThemeColors.Hex("Status.Warning", "#FFD166"),  // amber — needs more evidence
                _          => ThemeColors.Hex("Text.Muted", "#8388BD"),  // muted — likely noise
            };
        }
    }

    /// <summary>Promotion eligibility — true if user can click Promote.
    /// FIX (audit MEDIUM #10): require at least one explicit Accept so a
    /// purely-observed pattern (no human feedback) can't auto-eligible
    /// itself off the back of the 0.5 default success-rate floor.</summary>
    public bool CanPromote => !Promoted && AcceptCount >= 1 && Confidence >= InjectThreshold;

    /// <summary>Friendly stamp for the UI.</summary>
    public string RelativeLastSeen
    {
        get
        {
            var diff = DateTime.UtcNow - LastSeen;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 14) return $"{(int)diff.TotalDays}d ago";
            return LastSeen.ToLocalTime().ToString("MMM dd");
        }
    }
}

/// <summary>
/// Wrapper for the JSON file format. Versioned so we can migrate later
/// without breaking existing user files.
/// </summary>
public class InstinctStore
{
    /// <summary>2 = instincts carry a project, the chats they were seen in and their aliases (see InstinctService.Migrate).</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public List<Instinct> Instincts { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
