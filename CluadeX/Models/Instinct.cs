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
///   4. When Confidence ≥ 0.70 → eligible for /evolve clustering + promotion
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
    /// Confidence score 0..1.
    ///
    ///   confidence = success_rate × frequency_curve × recency_decay
    ///
    /// success_rate    : Accept / (Accept + Reject), defaulting to 0.5 if no votes yet
    /// frequency_curve : log(1 + Occurrences) / log(11) → caps at 1.0 around 10 occurrences
    /// recency_decay   : 1 - min(days_since_last_seen / 60, 0.5) — old patterns fade
    /// </summary>
    public double Confidence
    {
        get
        {
            int votes = AcceptCount + RejectCount;
            double successRate = votes == 0 ? 0.5 : (double)AcceptCount / votes;
            // frequency: ln(1+n) / ln(11). 0 occurrences → 0, 10 → ~1.0, asymptote past that.
            double freq = Math.Min(1.0, Math.Log(1 + OccurrenceCount) / Math.Log(11));
            double daysOld = Math.Max(0, (DateTime.UtcNow - LastSeen).TotalDays);
            double recency = 1.0 - Math.Min(daysOld / 60.0, 0.5);
            return Math.Round(successRate * freq * recency, 3);
        }
    }

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
                "STRONG"   => "#5CFFB0",  // mint — ready to promote
                "EMERGING" => "#4CDFFF",  // cyan — keep watching
                "WEAK"     => "#FFD166",  // amber — needs more evidence
                _          => "#8388BD",  // muted — likely noise
            };
        }
    }

    /// <summary>Promotion eligibility — true if user can click Promote.</summary>
    public bool CanPromote => !Promoted && Confidence >= 0.45;

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
    public int Version { get; set; } = 1;
    public List<Instinct> Instincts { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
