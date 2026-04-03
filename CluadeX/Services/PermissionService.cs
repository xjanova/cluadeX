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
                Rules = JsonSerializer.Deserialize<List<PermissionRule>>(json, JsonOptions)
                        ?? new List<PermissionRule>();
            }
        }
        catch
        {
            Rules = new List<PermissionRule>();
        }
    }

    public void SaveRules()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_permissionsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(Rules, JsonOptions);
            File.WriteAllText(_permissionsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save permissions: {ex.Message}");
        }
    }

    public void AddRule(PermissionRule rule)
    {
        Rules.Add(rule);
        SaveRules();
    }

    public void RemoveRule(PermissionRule rule)
    {
        Rules.Remove(rule);
        SaveRules();
    }

    /// <summary>
    /// Check permission for a given resource and scope.
    /// First matching rule wins. Returns Ask if no rule matches.
    /// </summary>
    public PermAction CheckPermission(string resource, string scope)
    {
        foreach (var rule in Rules)
        {
            // Scope must match: either rule scope is "*" or matches exactly
            if (rule.Scope != "*" && !string.Equals(rule.Scope, scope, StringComparison.OrdinalIgnoreCase))
                continue;

            // Pattern match using simple * glob
            if (MatchesGlob(resource, rule.Pattern))
                return rule.Action;
        }

        return PermAction.Ask;
    }

    /// <summary>
    /// Simple glob matching: * matches any sequence of characters.
    /// </summary>
    private static bool MatchesGlob(string input, string pattern)
    {
        if (pattern == "*") return true;

        // Escape regex special chars, then replace escaped \* with .*
        string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
