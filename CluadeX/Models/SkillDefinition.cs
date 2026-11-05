namespace CluadeX.Models;

/// <summary>Where a skill was found. Later sources override earlier ones with the same name.</summary>
public enum SkillSource
{
    BuiltIn,
    /// <summary>~/.cluadex/skills — also where skills installed from a trusted source land.</summary>
    User,
    /// <summary>{project}/.cluadex/skills</summary>
    Project,
}

/// <summary>
/// Represents a skill definition loaded from a markdown file with YAML frontmatter.
/// Skills are reusable prompt templates that can be invoked via /command or by the AI.
/// </summary>
public class SkillDefinition
{
    /// <summary>Skill name (from frontmatter or filename).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When should the AI invoke this skill automatically.</summary>
    public string? WhenToUse { get; set; }

    /// <summary>List of tool names the skill is allowed to use (empty = all tools).</summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>Optional model override (e.g., "opus", "sonnet").</summary>
    public string? Model { get; set; }

    /// <summary>The markdown prompt content (body after frontmatter).</summary>
    public string PromptContent { get; set; } = string.Empty;

    /// <summary>Path to the skill file on disk.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Whether users can invoke this skill via /command.</summary>
    public bool UserInvocable { get; set; } = true;

    /// <summary>Short hint about what arguments the skill accepts.</summary>
    public string? ArgumentHint { get; set; }

    /// <summary>Whether this is a built-in (bundled) skill.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Which discovery path it came from.</summary>
    public SkillSource Source { get; set; }

    /// <summary>The page it was installed from (frontmatter <c>source:</c>), for skills fetched from a trusted source.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Frontmatter <c>license:</c>, shown before install.</summary>
    public string? License { get; set; }

    /// <summary>Tool names in <c>allowed-tools</c> that no CluadeX tool answers to (shown as a warning).</summary>
    public List<string> UnknownTools { get; set; } = new();
}
