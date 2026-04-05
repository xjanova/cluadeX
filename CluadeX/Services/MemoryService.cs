using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// Persistent file-based memory system modeled after Claude Code's memdir.
/// Memories are markdown files with YAML frontmatter, indexed by MEMORY.md.
///
/// Memory types:
///   - user: Info about the user (role, preferences, knowledge)
///   - feedback: Guidance on approach (corrections, confirmations)
///   - project: Ongoing work context (goals, deadlines, decisions)
///   - reference: Pointers to external resources (URLs, tools, systems)
///
/// Storage:
///   - Global: ~/.cluadex/memory/
///   - Project: {project}/.cluadex/memory/
/// </summary>
public class MemoryService
{
    private readonly FileSystemService _fileSystemService;

    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n(.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex YamlLineRegex = new(
        @"^(\w+):\s*(.*)$",
        RegexOptions.Compiled);

    public MemoryService(FileSystemService fileSystemService)
    {
        _fileSystemService = fileSystemService;
    }

    /// <summary>Get the global memory directory path.</summary>
    public string GlobalMemoryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cluadex", "memory");

    /// <summary>Get the project memory directory path (or null if no project open).</summary>
    public string? ProjectMemoryDir =>
        _fileSystemService.HasWorkingDirectory
            ? Path.Combine(_fileSystemService.WorkingDirectory, ".cluadex", "memory")
            : null;

    /// <summary>Load the MEMORY.md index file content.</summary>
    public string LoadMemoryIndex()
    {
        var sb = new StringBuilder();

        // Global memory
        string globalIndex = Path.Combine(GlobalMemoryDir, "MEMORY.md");
        if (File.Exists(globalIndex))
        {
            string content = File.ReadAllText(globalIndex);
            // Truncate to 200 lines as per Claude Code spec
            var lines = content.Split('\n');
            sb.AppendLine("# Global Memory");
            foreach (var line in lines.Take(200))
                sb.AppendLine(line);
            if (lines.Length > 200)
                sb.AppendLine("... (truncated)");
        }

        // Project memory
        if (ProjectMemoryDir != null)
        {
            string projectIndex = Path.Combine(ProjectMemoryDir, "MEMORY.md");
            if (File.Exists(projectIndex))
            {
                string content = File.ReadAllText(projectIndex);
                var lines = content.Split('\n');
                sb.AppendLine("\n# Project Memory");
                foreach (var line in lines.Take(200))
                    sb.AppendLine(line);
                if (lines.Length > 200)
                    sb.AppendLine("... (truncated)");
            }
        }

        return sb.ToString();
    }

    /// <summary>Save a memory to a file and update the MEMORY.md index.</summary>
    public void SaveMemory(string name, string type, string description, string content, bool isProjectScope = false)
    {
        string dir = isProjectScope && ProjectMemoryDir != null ? ProjectMemoryDir : GlobalMemoryDir;
        Directory.CreateDirectory(dir);

        // Sanitize filename
        string fileName = SanitizeFileName(name) + ".md";
        string filePath = Path.Combine(dir, fileName);

        // Write memory file with frontmatter
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {description}");
        sb.AppendLine($"type: {type}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(content);

        File.WriteAllText(filePath, sb.ToString());

        // Update MEMORY.md index
        UpdateMemoryIndex(dir, name, fileName, description);
    }

    /// <summary>Remove a memory by name.</summary>
    public bool RemoveMemory(string name, bool isProjectScope = false)
    {
        string dir = isProjectScope && ProjectMemoryDir != null ? ProjectMemoryDir : GlobalMemoryDir;
        string fileName = SanitizeFileName(name) + ".md";
        string filePath = Path.Combine(dir, fileName);

        if (!File.Exists(filePath)) return false;

        File.Delete(filePath);

        // Remove from MEMORY.md index — use exact markdown link target match
        // e.g. "](filename.md)" to avoid accidentally removing unrelated lines
        string indexPath = Path.Combine(dir, "MEMORY.md");
        if (File.Exists(indexPath))
        {
            string linkTarget = $"]({fileName})";
            var lines = File.ReadAllLines(indexPath)
                .Where(l => !l.Contains(linkTarget, StringComparison.OrdinalIgnoreCase))
                .ToList();
            File.WriteAllLines(indexPath, lines);
        }

        return true;
    }

    /// <summary>Load a specific memory file.</summary>
    public MemoryEntry? LoadMemory(string name, bool isProjectScope = false)
    {
        string dir = isProjectScope && ProjectMemoryDir != null ? ProjectMemoryDir : GlobalMemoryDir;
        string fileName = SanitizeFileName(name) + ".md";
        string filePath = Path.Combine(dir, fileName);

        if (!File.Exists(filePath)) return null;

        return ParseMemoryFile(filePath);
    }

    /// <summary>List all memories from both global and project scopes.</summary>
    public List<MemoryEntry> ListAllMemories()
    {
        var memories = new List<MemoryEntry>();

        if (Directory.Exists(GlobalMemoryDir))
        {
            foreach (var file in Directory.GetFiles(GlobalMemoryDir, "*.md"))
            {
                if (Path.GetFileName(file) == "MEMORY.md") continue;
                var entry = ParseMemoryFile(file);
                if (entry != null)
                {
                    entry.Scope = "global";
                    memories.Add(entry);
                }
            }
        }

        if (ProjectMemoryDir != null && Directory.Exists(ProjectMemoryDir))
        {
            foreach (var file in Directory.GetFiles(ProjectMemoryDir, "*.md"))
            {
                if (Path.GetFileName(file) == "MEMORY.md") continue;
                var entry = ParseMemoryFile(file);
                if (entry != null)
                {
                    entry.Scope = "project";
                    memories.Add(entry);
                }
            }
        }

        return memories;
    }

    /// <summary>Parse a memory file with YAML frontmatter.</summary>
    private static MemoryEntry? ParseMemoryFile(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            var match = FrontmatterRegex.Match(content);

            if (!match.Success)
                return new MemoryEntry
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Content = content,
                    FilePath = filePath,
                };

            var entry = new MemoryEntry
            {
                Content = match.Groups[2].Value.Trim(),
                FilePath = filePath,
                Name = Path.GetFileNameWithoutExtension(filePath),
            };

            foreach (var line in match.Groups[1].Value.Split('\n'))
            {
                var kvMatch = YamlLineRegex.Match(line.Trim());
                if (!kvMatch.Success) continue;

                string key = kvMatch.Groups[1].Value.ToLowerInvariant();
                string value = kvMatch.Groups[2].Value.Trim();

                switch (key)
                {
                    case "name": entry.Name = value; break;
                    case "description": entry.Description = value; break;
                    case "type": entry.Type = value; break;
                }
            }

            return entry;
        }
        catch { return null; }
    }

    /// <summary>Update the MEMORY.md index file.</summary>
    private static void UpdateMemoryIndex(string dir, string name, string fileName, string description)
    {
        string indexPath = Path.Combine(dir, "MEMORY.md");

        var lines = new List<string>();
        if (File.Exists(indexPath))
        {
            lines = File.ReadAllLines(indexPath).ToList();
            // Remove existing entry for this file
            lines.RemoveAll(l => l.Contains($"({fileName})", StringComparison.OrdinalIgnoreCase));
        }

        // Add new entry (keep under 200 lines)
        string entry = $"- [{name}]({fileName}) — {description}";
        lines.Add(entry);

        if (lines.Count > 200)
            lines = lines.TakeLast(200).ToList();

        File.WriteAllLines(indexPath, lines);
    }

    private static string SanitizeFileName(string name)
    {
        // Replace spaces with underscores, remove invalid chars
        string sanitized = name.Replace(' ', '_').ToLowerInvariant();
        foreach (char c in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(c.ToString(), "");
        // Guard against empty result
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = $"memory_{DateTime.Now:yyyyMMdd_HHmmss}";
        return sanitized.Length > 60 ? sanitized[..60] : sanitized;
    }
}

/// <summary>A memory entry loaded from disk.</summary>
public class MemoryEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "user"; // user, feedback, project, reference
    public string Content { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Scope { get; set; } = "global"; // global, project
}
