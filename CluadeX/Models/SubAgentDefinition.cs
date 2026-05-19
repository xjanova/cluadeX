namespace CluadeX.Models;

/// <summary>
/// A specialised subagent — a scoped, single-purpose agent the main agent
/// can spawn via `subagent_invoke(type, prompt, files?)`. Subagents run in
/// their own context window (don't pollute the main thread), can be
/// restricted to a subset of tools, and may be routed to a cheaper model
/// (tier-based routing → local GGUF / Haiku / Sonnet / Opus).
///
/// Discovery paths mirror SkillService:
///   - Built-in (10 ship-1)
///   - ~/.cluadex/agents/*.md (user-global)
///   - {project}/.cluadex/agents/*.md (project-specific)
///
/// File format: YAML frontmatter + markdown body.
///   ---
///   name: code-reviewer
///   description: Reviews diffs for bugs, security, and style fit
///   tier: deep        # cheap | mid | deep
///   tools: [read_file, grep, glob]   # empty = all
///   color: violet
///   icon: shield
///   model: opus       # optional explicit model override
///   ---
///   # System prompt body goes here in markdown.
/// </summary>
public class SubAgentDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";

    /// <summary>Tools the subagent is allowed to call (empty = inherit from parent).</summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>Cost tier — drives provider routing in AiProviderManager.</summary>
    public SubAgentTier Tier { get; set; } = SubAgentTier.Mid;

    /// <summary>Optional explicit model id (overrides Tier).</summary>
    public string? Model { get; set; }

    /// <summary>Neon accent color (cyan, violet, magenta, mint, amber, pink, red).</summary>
    public string Color { get; set; } = "violet";

    /// <summary>Segoe MDL2 glyph for the icon (e.g. "&#xE72E;" for shield).</summary>
    public string Icon { get; set; } = "";  // default = robot/bot

    /// <summary>"When should the parent agent spawn this subagent?" hint for the prompt.</summary>
    public string? WhenToUse { get; set; }

    public string FilePath { get; set; } = "";
    public bool IsBuiltIn { get; set; }

    /// <summary>Hex color for the UI (derived from Color name).</summary>
    public string ColorHex => Color.ToLowerInvariant() switch
    {
        "cyan"    => "#4CDFFF",
        "blue"    => "#5A8DFF",
        "violet"  => "#A672FF",
        "magenta" => "#FF5EC4",
        "pink"    => "#FF8AA6",
        "mint"    => "#5CFFB0",
        "amber"   => "#FFD166",
        "red"     => "#FF6E6E",
        _         => "#A672FF",
    };

    public string TierBadge => Tier switch
    {
        SubAgentTier.Cheap => "CHEAP",
        SubAgentTier.Mid   => "MID",
        SubAgentTier.Deep  => "DEEP",
        _                  => "MID",
    };
}

/// <summary>
/// Cost-routing tier. cheap = always local (Qwen3/Gemma4 if loaded, else Haiku).
/// mid = local if VRAM allows, else Haiku/Sonnet. deep = always best (Opus/Sonnet).
/// </summary>
public enum SubAgentTier
{
    Cheap,
    Mid,
    Deep,
}
