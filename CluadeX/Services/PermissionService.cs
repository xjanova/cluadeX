using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

public class PermissionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _permissionsPath;
    private readonly object _gate = new(); // guards Rules across agent-thread checks + UI-thread edits

    public List<PermissionRule> Rules { get; private set; } = new();

    public PermissionService(SettingsService settingsService)
    {
        _permissionsPath = Path.Combine(settingsService.DataRoot, "permissions.json");
        LoadRules();
    }

    public void LoadRules()
    {
        try
        {
            if (File.Exists(_permissionsPath))
            {
                string json = File.ReadAllText(_permissionsPath);
                var loaded = JsonSerializer.Deserialize<List<PermissionRule>>(json, JsonOptions)
                        ?? new List<PermissionRule>();
                lock (_gate) Rules = loaded;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load permissions (using empty rule set): {ex.Message}");
            lock (_gate) Rules = new List<PermissionRule>();
        }
    }

    public void SaveRules()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_permissionsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            string json;
            lock (_gate) json = JsonSerializer.Serialize(Rules, JsonOptions);

            // Atomic + durable write (temp → fsync → replace) so a crash mid-write can't corrupt the rule
            // file and silently drop the user back to "ask for everything" (or worse).
            string tempPath = _permissionsPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_permissionsPath))
                File.Replace(tempPath, _permissionsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _permissionsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save permissions: {ex.Message}");
        }
    }

    public void AddRule(PermissionRule rule)
    {
        lock (_gate) Rules.Add(rule);
        SaveRules();
    }

    public void RemoveRule(PermissionRule rule)
    {
        lock (_gate) Rules.Remove(rule);
        SaveRules();
    }

    /// <summary>
    /// Check permission for a given resource and scope.
    /// First matching rule wins. Returns Ask if no rule matches.
    /// </summary>
    public PermAction CheckPermission(string resource, string scope)
    {
        return CheckPermission(resource, scope, toolName: null);
    }

    /// <summary>
    /// Check permission with tool-scoped rules.
    /// Supports Claude Code-style patterns: "run_command", "Bash(npm:*)", "read_file(/src/**)".
    /// </summary>
    public PermAction CheckPermission(string resource, string scope, string? toolName)
    {
        // Snapshot under the lock so a concurrent AddRule/RemoveRule (UI thread) can't throw
        // "collection was modified" while the agent thread is iterating.
        List<PermissionRule> rules;
        lock (_gate) rules = Rules.ToList();
        foreach (var rule in rules)
        {
            // Scope must match: either rule scope is "*" or matches exactly
            if (rule.Scope != "*" && !string.Equals(rule.Scope, scope, StringComparison.OrdinalIgnoreCase))
                continue;

            // Tool name constraint check
            if (!string.IsNullOrEmpty(rule.ToolName))
            {
                if (toolName == null) continue; // Rule requires a tool, but no tool specified

                // Check tool-scoped pattern like "run_command(npm:*)"
                if (!MatchesToolPattern(toolName, resource, rule.ToolName))
                    continue;

                // Also check the resource Pattern if set (don't skip it for tool-scoped rules)
                if (rule.Pattern != "*" && !MatchesGlob(resource, rule.Pattern))
                    continue;

                return rule.Action; // Both tool and resource pattern matched
            }

            // Regular resource pattern match
            if (MatchesGlob(resource, rule.Pattern))
                return rule.Action;
        }

        return PermAction.Ask;
    }

    /// <summary>
    /// Match Claude Code-style tool patterns.
    /// Patterns: "tool_name", "tool_name(prefix:*)", "tool_name(/path/**)"
    /// </summary>
    private static bool MatchesToolPattern(string toolName, string resource, string pattern)
    {
        // Simple tool name match: "run_command"
        if (!pattern.Contains('('))
            return pattern.Equals(toolName, StringComparison.OrdinalIgnoreCase);

        // Tool with argument pattern: "run_command(npm:*)"
        var match = Regex.Match(pattern, @"^(\w+)\((.+)\)$");
        if (!match.Success) return false;

        string patternTool = match.Groups[1].Value;
        string argPattern = match.Groups[2].Value;

        // Tool name must match
        if (!patternTool.Equals(toolName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Argument pattern match against the resource/command
        return MatchesGlob(resource, argPattern);
    }

    /// <summary>
    /// Simple glob matching: * matches any sequence of characters.
    /// ** matches across path separators.
    /// </summary>
    private static bool MatchesGlob(string input, string pattern)
    {
        if (pattern == "*") return true;

        // Handle ** (match across path separators).
        // Use a private-use unicode sentinel that is rejected by Regex.Escape and cannot
        // occur in any real-world path pattern. This avoids the magic-string collision bug
        // where a literal "@@DOUBLESTAR@@" in user input would corrupt the regex.
        const string DoubleStarSentinel = "\uE000DS\uE001";
        string escaped = Regex.Escape(pattern);
        string regexPattern = "^" + escaped
            .Replace("\\*\\*", DoubleStarSentinel)   // Preserve **
            .Replace("\\*", "[^/\\\\]*")             // * = anything except path separators
            .Replace(DoubleStarSentinel, ".*")       // ** = anything including path separators
            + "$";

        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
