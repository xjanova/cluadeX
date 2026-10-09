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
        => FindRule(resource, scope, toolName) ?? PermAction.Ask;

    /// <summary>A rule written for one of these exact tool names (no wildcard tool rule counts). Lets the
    /// user allow or deny one specific risky tool on purpose while a blanket "*" rule never does.</summary>
    public PermAction? FindNamedToolRule(params string[] toolNames)
    {
        List<PermissionRule> rules;
        lock (_gate) rules = Rules.ToList();
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.ToolName) || rule.ToolName.Contains('*') || rule.ToolName.Contains('(')) continue;
            if (toolNames.Any(n => string.Equals(n, rule.ToolName, StringComparison.OrdinalIgnoreCase)))
                return rule.Action;
        }
        return null;
    }

    /// <summary>The action of the first rule that matches, or null when no rule does — so a caller can
    /// apply its own default (e.g. reading inside the open project) without overriding a user rule.</summary>
    public PermAction? FindRule(string resource, string scope, string? toolName)
    {
        foreach (var rule in SnapshotRules())
        {
            if (!ScopeMatches(rule, scope)) continue;
            if (RuleMatches(rule, resource, toolName))
                return rule.Action;
        }
        return null;
    }

    /// <summary>
    /// The rule for a call whose resource is a URL (web_fetch, git_clone). A URL is not a path: matched as
    /// one, a path glob such as "**/src/**" also matches "https://attacker.example/src/?d=…" and approves
    /// sending data to any host without a prompt. So only rules written for URLs decide on the URL itself —
    /// a Pattern that starts with http:// or https://, or a tool-scoped rule such as
    /// "web_fetch(https://docs.example.com/*)" / "web_fetch(domain:docs.example.com)". Every other rule is
    /// evaluated exactly as it was before URLs were looked at: against <paramref name="legacyResource"/>
    /// (the tool name), so a "web_fetch" → Allow rule keeps working. First matching rule wins; null = no
    /// rule matched (the caller asks).
    /// </summary>
    public PermAction? FindUrlRule(string url, string scope, string? toolName, string legacyResource)
    {
        var uri = TryParseHttpUrl(url);
        foreach (var rule in SnapshotRules())
        {
            if (!ScopeMatches(rule, scope)) continue;

            bool urlPattern = IsUrlPattern(rule.Pattern);
            string? toolArg = ToolArgPatternFor(rule.ToolName, toolName);
            if (!urlPattern && toolArg == null)
            {
                if (RuleMatches(rule, legacyResource, toolName))
                    return rule.Action;
                continue;
            }

            // A URL rule vouches only for a plain http(s) URL that it really matches.
            if (uri == null) continue;
            if (!string.IsNullOrEmpty(rule.ToolName))
            {
                if (toolArg != null)
                {
                    if (!MatchesUrlArg(uri, toolArg)) continue;
                }
                else if (toolName == null || rule.ToolName.Contains('(')
                         || !rule.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // written for another tool
                }
            }
            if (rule.Pattern != "*" && !(urlPattern && MatchesUrlPattern(uri, rule.Pattern)))
                continue;
            return rule.Action;
        }
        return null;
    }

    /// <summary>Snapshot under the lock so a concurrent AddRule/RemoveRule (UI thread) can't throw
    /// "collection was modified" while the agent thread is iterating.</summary>
    private List<PermissionRule> SnapshotRules()
    {
        lock (_gate) return Rules.ToList();
    }

    /// <summary>Scope must match: either rule scope is "*" or matches exactly.</summary>
    private static bool ScopeMatches(PermissionRule rule, string scope)
        => rule.Scope == "*" || string.Equals(rule.Scope, scope, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="rule"/> matches <paramref name="resource"/> (scope already checked).</summary>
    private static bool RuleMatches(PermissionRule rule, string resource, string? toolName)
    {
        // Tool name constraint check
        if (!string.IsNullOrEmpty(rule.ToolName))
        {
            if (toolName == null) return false; // Rule requires a tool, but no tool specified

            // Check tool-scoped pattern like "run_command(npm:*)"
            if (!MatchesToolPattern(toolName, resource, rule.ToolName))
                return false;

            // Also check the resource Pattern if set (don't skip it for tool-scoped rules)
            return rule.Pattern == "*" || MatchesGlob(resource, rule.Pattern);
        }

        // Regular resource pattern match
        return MatchesGlob(resource, rule.Pattern);
    }

    private static bool IsUrlPattern(string? pattern)
        => pattern != null
           && (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>The argument part of a tool-scoped rule written for <paramref name="toolName"/> — "x" for
    /// "web_fetch(x)" — else null.</summary>
    private static string? ToolArgPatternFor(string? ruleToolName, string? toolName)
    {
        if (string.IsNullOrEmpty(ruleToolName) || string.IsNullOrEmpty(toolName)) return null;
        var match = Regex.Match(ruleToolName, @"^(\w+)\((.+)\)$");
        return match.Success && match.Groups[1].Value.Equals(toolName, StringComparison.OrdinalIgnoreCase)
            ? match.Groups[2].Value.Trim()
            : null;
    }

    /// <summary>
    /// The URL as the fetch will see it, or null when no URL rule may vouch for it: not http(s), or written
    /// in a form URL parsers disagree on (a backslash, whitespace or control character inside, user-info
    /// before the host) — "https://docs.example.com\@attacker.example/" must not pass as docs.example.com.
    /// </summary>
    private static Uri? TryParseHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string s = url.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (char c in s)
            if (c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri;
    }

    /// <summary>
    /// "https://docs.example.com/*" against a parsed URL: the scheme must be the same, the host part is a
    /// glob over the REAL host (plus ":port" when not the default), and the rest a glob over path + query.
    /// Matching the parsed host — never the raw string — is what keeps "https://attacker.example/?u=https://docs.example.com/x"
    /// and "https://docs.example.com.attacker.example/" out. A pattern with no path covers the root only.
    /// </summary>
    private static bool MatchesUrlPattern(Uri uri, string pattern)
    {
        int sep = pattern.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return false;
        if (!pattern[..sep].Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        string rest = pattern[(sep + 3)..];
        int slash = rest.IndexOf('/');
        string hostPattern = slash < 0 ? rest : rest[..slash];
        string pathPattern = slash < 0 ? "/" : rest[slash..];
        if (hostPattern.Length == 0) return false;

        string authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return MatchesGlob(authority, hostPattern) && MatchesGlob(uri.PathAndQuery, pathPattern);
    }

    /// <summary>The argument of a tool-scoped URL rule: "*" / "**" (any http(s) URL), a URL pattern, or
    /// "domain:host" (that host, globs allowed). Anything else vouches for no URL.</summary>
    private static bool MatchesUrlArg(Uri uri, string arg)
    {
        if (arg is "*" or "**") return true;
        if (IsUrlPattern(arg)) return MatchesUrlPattern(uri, arg);
        if (arg.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
        {
            string host = arg["domain:".Length..].Trim();
            return host.Length > 0 && MatchesGlob(uri.Host, host);
        }
        return false;
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
