using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Discovers, loads, and manages skills from disk and built-in definitions.
/// Skills are markdown files with YAML frontmatter that define reusable prompt templates.
///
/// Discovery paths:
///   - ~/.cluadex/skills/     (user-global skills)
///   - {project}/.cluadex/skills/  (project-specific skills)
///   - Built-in skills (commit, review-pr, simplify)
/// </summary>
public class SkillService
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private List<SkillDefinition>? _cachedSkills;
    private readonly object _cacheLock = new();

    // YAML frontmatter regex: ---\n...\n---
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n(.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Simple YAML key-value parser for frontmatter
    private static readonly Regex YamlLineRegex = new(
        @"^(\w+):\s*(.*)$",
        RegexOptions.Compiled);

    public SkillService(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
    }

    /// <summary>Get all available skills (cached).</summary>
    public List<SkillDefinition> GetAllSkills()
    {
        lock (_cacheLock)
        {
            if (_cachedSkills != null) return _cachedSkills;
            _cachedSkills = DiscoverSkills();
            return _cachedSkills;
        }
    }

    /// <summary>Find a skill by name (case-insensitive).</summary>
    public SkillDefinition? GetSkillByName(string name)
    {
        return GetAllSkills().FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Clear the skill cache and rediscover.</summary>
    public void ReloadSkills()
    {
        lock (_cacheLock) { _cachedSkills = null; }
    }

    /// <summary>Discover all skills from built-in + disk.</summary>
    private List<SkillDefinition> DiscoverSkills()
    {
        var skills = new List<SkillDefinition>();

        // 1. Built-in skills
        skills.AddRange(GetBuiltInSkills());

        // 2. User-global skills (~/.cluadex/skills/)
        string userSkillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "skills");
        if (Directory.Exists(userSkillsDir))
            skills.AddRange(LoadSkillsFromDirectory(userSkillsDir));

        // 3. Project-specific skills ({project}/.cluadex/skills/)
        if (_fileSystemService.HasWorkingDirectory)
        {
            string projectSkillsDir = Path.Combine(
                _fileSystemService.WorkingDirectory, ".cluadex", "skills");
            if (Directory.Exists(projectSkillsDir))
                skills.AddRange(LoadSkillsFromDirectory(projectSkillsDir));
        }

        // Deduplicate by name (project overrides user, user overrides built-in)
        return skills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last()) // Last wins (project > user > built-in)
            .ToList();
    }

    /// <summary>Load all .md skill files from a directory.</summary>
    private List<SkillDefinition> LoadSkillsFromDirectory(string directory)
    {
        var skills = new List<SkillDefinition>();

        foreach (var file in Directory.GetFiles(directory, "*.md"))
        {
            try
            {
                var skill = ParseSkillFile(file);
                if (skill != null)
                    skills.Add(skill);
            }
            catch { /* skip unparseable files */ }
        }

        return skills;
    }

    /// <summary>Parse a skill markdown file with YAML frontmatter.</summary>
    public static SkillDefinition? ParseSkillFile(string filePath)
    {
        string content = File.ReadAllText(filePath);
        var match = FrontmatterRegex.Match(content);

        if (!match.Success)
        {
            // No frontmatter — treat entire file as prompt with filename as name
            return new SkillDefinition
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                Description = "Custom skill",
                PromptContent = content,
                FilePath = filePath,
            };
        }

        string yamlSection = match.Groups[1].Value;
        string markdownBody = match.Groups[2].Value;

        var skill = new SkillDefinition
        {
            PromptContent = markdownBody.Trim(),
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath),
        };

        // Parse YAML frontmatter (simple key: value format)
        foreach (var line in yamlSection.Split('\n'))
        {
            var kvMatch = YamlLineRegex.Match(line.Trim());
            if (!kvMatch.Success) continue;

            string key = kvMatch.Groups[1].Value.ToLowerInvariant();
            string value = kvMatch.Groups[2].Value.Trim().Trim('"', '\'');

            switch (key)
            {
                case "name":
                    skill.Name = value;
                    break;
                case "description":
                    skill.Description = value;
                    break;
                case "whentouse":
                    skill.WhenToUse = value;
                    break;
                case "allowedtools":
                    // Parse as comma-separated or YAML array
                    skill.AllowedTools = value.TrimStart('[').TrimEnd(']')
                        .Split(',')
                        .Select(t => t.Trim().Trim('"', '\''))
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                    break;
                case "model":
                    skill.Model = value;
                    break;
                case "userinvocable":
                    skill.UserInvocable = value.ToLowerInvariant() is "true" or "yes" or "1";
                    break;
                case "argumenthint":
                    skill.ArgumentHint = value;
                    break;
            }
        }

        return skill;
    }

    /// <summary>Get built-in skill definitions.</summary>
    private static List<SkillDefinition> GetBuiltInSkills()
    {
        return new List<SkillDefinition>
        {
            new()
            {
                Name = "commit",
                Description = "Create a git commit with a well-crafted message",
                IsBuiltIn = true,
                UserInvocable = true,
                AllowedTools = new() { "git_status", "git_diff", "git_log", "git_add", "git_commit", "run_command", "read_file" },
                PromptContent = """
                    Create a git commit for the current changes. Follow these steps:

                    1. Run git status to see all untracked and modified files
                    2. Run git diff to see staged and unstaged changes
                    3. Run git log --oneline -5 to see recent commit message style
                    4. Analyze all changes and draft a commit message:
                       - Summarize the nature of changes (new feature, bug fix, refactor, etc.)
                       - Write a concise (1-2 sentence) commit message focusing on "why" not "what"
                       - Follow the repository's existing commit message style
                    5. Stage relevant files (avoid .env, credentials, large binaries)
                    6. Create the commit
                    7. Run git status to verify success

                    IMPORTANT:
                    - Always create NEW commits, never --amend unless asked
                    - Never use --no-verify or skip hooks unless asked
                    - Never commit files that contain secrets
                    - Prefer staging specific files over "git add ."
                    """,
            },
            new()
            {
                Name = "review-pr",
                Description = "Review a pull request with structured feedback",
                IsBuiltIn = true,
                UserInvocable = true,
                AllowedTools = new() { "run_command", "read_file", "search_content", "git_diff", "git_log" },
                PromptContent = """
                    Review the current branch's changes as a pull request. Follow these steps:

                    1. Run git log main..HEAD (or master..HEAD) to see all commits
                    2. Run git diff main...HEAD to see all changes
                    3. Read the changed files to understand context

                    Provide a structured review:

                    ## Summary
                    Brief description of what the PR does.

                    ## Changes Reviewed
                    List each file changed with a brief note on what changed.

                    ## Issues Found
                    - CRITICAL: Must fix before merge (bugs, security issues)
                    - SUGGESTION: Improvements that would be nice
                    - NITPICK: Style/preference items

                    ## Security
                    Any security concerns (secrets, injection, auth issues).

                    ## Verdict
                    APPROVE / REQUEST CHANGES / NEEDS DISCUSSION
                    """,
            },
            new()
            {
                Name = "simplify",
                Description = "Review changed code for reuse, quality, and efficiency",
                IsBuiltIn = true,
                UserInvocable = true,
                AllowedTools = new() { "read_file", "edit_file", "search_content", "git_diff", "run_command" },
                PromptContent = """
                    Review the recently changed code for opportunities to simplify and improve.

                    1. Run git diff to see what changed
                    2. Read the full files that were changed
                    3. Look for:
                       - Code duplication that can be extracted
                       - Unnecessary complexity or over-engineering
                       - Dead code or unused imports
                       - Performance improvements
                       - Better use of language features
                    4. Apply fixes directly using edit_file
                    5. Verify changes compile/pass tests

                    Keep changes minimal and focused. Don't refactor code that wasn't recently changed.
                    """,
            },
        };
    }
}
