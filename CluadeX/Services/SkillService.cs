using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Discovers, loads, and manages skills from disk and built-in definitions.
/// Skills are markdown files with YAML frontmatter that define reusable prompt templates.
///
/// Discovery paths:
///   - Built-in skills (commit, review-pr, simplify, …)
///   - ~/.cluadex/skills/     (user-global skills: name.md or name/SKILL.md — the Anthropic layout)
///   - {project}/.cluadex/skills/  (project-specific skills, same layouts)
///
/// The list is cached, but the cache follows the disk: it is rebuilt when either folder's write time or the
/// project changes, and a name that is not found triggers one rescan before "not found" (a skill saved a few
/// seconds earlier was refused: tool-precision, 2026-11-05 14:44:34 written, 14:44:38 "not found").
/// </summary>
public class SkillService
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private List<SkillDefinition>? _cachedSkills;
    private string? _cacheStamp;
    private readonly object _cacheLock = new();

    private readonly object _usageLock = new();
    private Dictionary<string, SkillUsage>? _usage;

    /// <summary>Raised (on any thread) after a use or refusal was recorded.</summary>
    public event Action? UsageChanged;

    /// <summary>Raised after a skill was switched on or off.</summary>
    public event Action? EnabledChanged;

    // YAML frontmatter regex: ---\n...\n---
    private static readonly Regex FrontmatterRegex = new(
        @"^\uFEFF?---\s*\n(.*?)\n---\s*(?:\n(.*))?$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // "key: value" at the start of a line; keys may use '-' or '_' (allowed-tools, when_to_use).
    private static readonly Regex YamlLineRegex = new(
        @"^([A-Za-z][\w-]*):\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>What an installable skill name may look like (also the file name it is saved under).</summary>
    public static readonly Regex ValidNameRegex = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    public SkillService(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
        UserSkillsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "skills");
    }

    /// <summary>~/.cluadex/skills. Settable so a test harness never touches the owner's real folder.</summary>
    public string UserSkillsDirectory { get; set; }

    public string? ProjectSkillsDirectory => _fileSystemService.HasWorkingDirectory
        ? Path.Combine(_fileSystemService.WorkingDirectory, ".cluadex", "skills")
        : null;

    /// <summary>Get all available skills, switched off ones included (cached; follows the disk).</summary>
    public List<SkillDefinition> GetAllSkills()
    {
        lock (_cacheLock)
        {
            string stamp = DiskStamp();
            if (_cachedSkills != null && stamp == _cacheStamp) return _cachedSkills;
            _cachedSkills = DiscoverSkills();
            _cacheStamp = stamp;
            return _cachedSkills;
        }
    }

    /// <summary>The skills that are switched on.</summary>
    public List<SkillDefinition> GetEnabledSkills() => GetAllSkills().Where(s => IsEnabled(s.Name)).ToList();

    /// <summary>Find a skill by name (case-insensitive). A miss rescans the disk once before answering null.</summary>
    public SkillDefinition? GetSkillByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var hit = Find(GetAllSkills(), name);
        if (hit != null) return hit;
        ReloadSkills();
        return Find(GetAllSkills(), name);
    }

    private static SkillDefinition? Find(List<SkillDefinition> all, string name) =>
        all.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Clear the skill cache and rediscover.</summary>
    public void ReloadSkills()
    {
        lock (_cacheLock) { _cachedSkills = null; _cacheStamp = null; }
    }

    // ── on / off ────────────────────────────────────────────────────────────

    public bool IsEnabled(string name) =>
        !_settingsService.Settings.DisabledSkills.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Switch a skill on or off and save settings. Returns false when settings could not be written.</summary>
    public bool SetEnabled(string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (IsEnabled(name) == enabled) return true;
        bool ok = _settingsService.UpdateSettings(s =>
        {
            s.DisabledSkills.RemoveAll(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!enabled) s.DisabledSkills.Add(name);
        });
        EnabledChanged?.Invoke();
        return ok;
    }

    // ── usage ───────────────────────────────────────────────────────────────

    /// <summary>How a skill was started.</summary>
    public enum UseKind { Slash, Tool, Pipe, Refused }

    public class SkillUsage
    {
        public int Slash { get; set; }
        public int Tool { get; set; }
        public int Pipe { get; set; }
        public int Refused { get; set; }
        public DateTime? LastUsed { get; set; }
        /// <summary>Words of the turns that ran it (Skills/SkillPicker terms → how many turns), the picker's learned hint.</summary>
        public Dictionary<string, int> Terms { get; set; } = new();
        public int Total => Slash + Tool + Pipe;
    }

    /// <summary>Most words a skill keeps; the rarest go first when it is full.</summary>
    public const int MaxLearnedTerms = 40;

    /// <summary>The words of a turn that ran <paramref name="name"/> (its first 12 distinct terms) are remembered for it:
    /// a later task said the same way finds it even when its description uses other words.</summary>
    public void LearnTerms(string name, string? text)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text)) return;
        var terms = Skills.SkillPicker.Terms(text).Take(12).ToList();
        if (terms.Count == 0) return;
        lock (_usageLock)
        {
            var all = LoadUsage();
            string key = name.Trim().ToLowerInvariant();
            if (!all.TryGetValue(key, out var u)) all[key] = u = new SkillUsage();
            u.Terms ??= new();
            foreach (var t in terms) u.Terms[t] = u.Terms.TryGetValue(t, out var c) ? c + 1 : 1;
            if (u.Terms.Count > MaxLearnedTerms)
                u.Terms = u.Terms.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Take(MaxLearnedTerms).ToDictionary(kv => kv.Key, kv => kv.Value);
            SaveUsage(all);
        }
    }

    /// <summary>Forget the words learned for <paramref name="name"/> (all skills when null).</summary>
    public void ForgetLearnedTerms(string? name = null)
    {
        lock (_usageLock)
        {
            var all = LoadUsage();
            foreach (var (k, u) in all)
                if (name == null || k == name.Trim().ToLowerInvariant()) u.Terms = new();
            SaveUsage(all);
        }
    }

    /// <summary>The words learned for <paramref name="name"/> (empty when none).</summary>
    public IReadOnlyDictionary<string, int> LearnedTerms(string name)
    {
        lock (_usageLock)
            return LoadUsage().TryGetValue(name.Trim().ToLowerInvariant(), out var u) && u.Terms != null
                ? new Dictionary<string, int>(u.Terms) : new Dictionary<string, int>();
    }

    // ── pinned ─────────────────────────────────────────────────────────────

    public bool IsPinned(string name) =>
        _settingsService.Settings.PinnedSkills.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pin a skill (offered on every turn while picking is on) or unpin it.</summary>
    public bool SetPinned(string name, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (IsPinned(name) == pinned) return true;
        bool ok = _settingsService.UpdateSettings(s =>
        {
            s.PinnedSkills.RemoveAll(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (pinned) s.PinnedSkills.Add(name);
        });
        EnabledChanged?.Invoke();
        return ok;
    }

    private string UsagePath => Path.Combine(_settingsService.DataRoot, "skill-usage.json");

    /// <summary>Count one start (or a refusal of a switched-off skill) — kept in skill-usage.json.</summary>
    public void RecordUse(string name, UseKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_usageLock)
        {
            var all = LoadUsage();
            string key = name.Trim().ToLowerInvariant();
            if (!all.TryGetValue(key, out var u)) all[key] = u = new SkillUsage();
            switch (kind)
            {
                case UseKind.Slash: u.Slash++; break;
                case UseKind.Tool: u.Tool++; break;
                case UseKind.Pipe: u.Pipe++; break;
                case UseKind.Refused: u.Refused++; break;
            }
            if (kind != UseKind.Refused) u.LastUsed = DateTime.Now;
            SaveUsage(all);
        }
        UsageChanged?.Invoke();
    }

    private void SaveUsage(Dictionary<string, SkillUsage> all)
    {
        try
        {
            Directory.CreateDirectory(_settingsService.DataRoot);
            string tmp = UsagePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, UsagePath, overwrite: true);
        }
        catch { /* a count that is not saved must never break a run */ }
    }

    public SkillUsage GetUsage(string name)
    {
        lock (_usageLock)
            return LoadUsage().TryGetValue(name.Trim().ToLowerInvariant(), out var u) ? u : new SkillUsage();
    }

    private Dictionary<string, SkillUsage> LoadUsage()
    {
        if (_usage != null) return _usage;
        try
        {
            if (File.Exists(UsagePath))
                _usage = JsonSerializer.Deserialize<Dictionary<string, SkillUsage>>(File.ReadAllText(UsagePath));
        }
        catch { /* damaged file: start counting again */ }
        return _usage ??= new Dictionary<string, SkillUsage>();
    }

    // ── discovery ───────────────────────────────────────────────────────────

    /// <summary>Changes when a skill file is added, removed or renamed in either folder, or the project changes.</summary>
    private string DiskStamp()
    {
        static string Of(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "-";
            try
            {
                long t = Directory.GetLastWriteTimeUtc(dir).Ticks;
                foreach (var sub in Directory.GetDirectories(dir))
                    t ^= Directory.GetLastWriteTimeUtc(sub).Ticks;
                return dir + ":" + t;
            }
            catch { return dir + ":?"; }
        }
        return Of(UserSkillsDirectory) + "|" + Of(ProjectSkillsDirectory);
    }

    /// <summary>Discover all skills from built-in + disk.</summary>
    private List<SkillDefinition> DiscoverSkills()
    {
        var skills = new List<SkillDefinition>();

        // 1. Built-in skills
        skills.AddRange(GetBuiltInSkills());

        // 2. User-global skills (~/.cluadex/skills/)
        if (Directory.Exists(UserSkillsDirectory))
            skills.AddRange(LoadSkillsFromDirectory(UserSkillsDirectory, SkillSource.User));

        // 3. Project-specific skills ({project}/.cluadex/skills/)
        if (ProjectSkillsDirectory is { } projectDir && Directory.Exists(projectDir))
            skills.AddRange(LoadSkillsFromDirectory(projectDir, SkillSource.Project));

        // Deduplicate by name (project overrides user, user overrides built-in)
        return skills
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last()) // Last wins (project > user > built-in)
            .ToList();
    }

    /// <summary>Load name.md files and name/SKILL.md folders from a directory.</summary>
    private static List<SkillDefinition> LoadSkillsFromDirectory(string directory, SkillSource source)
    {
        var files = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(directory, "*.md"));
            foreach (var sub in Directory.GetDirectories(directory))
            {
                string skillMd = Path.Combine(sub, "SKILL.md");
                if (File.Exists(skillMd)) files.Add(skillMd);
            }
        }
        catch { return new(); }

        var skills = new List<SkillDefinition>();
        foreach (var file in files)
        {
            try
            {
                var skill = ParseSkillFile(file);
                if (skill == null) continue;
                skill.Source = source;
                skills.Add(skill);
            }
            catch { /* skip unparseable files */ }
        }
        return skills;
    }

    /// <summary>Parse a skill markdown file with YAML frontmatter.</summary>
    public static SkillDefinition? ParseSkillFile(string filePath)
    {
        string fallback = Path.GetFileName(filePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "skill"
            : Path.GetFileNameWithoutExtension(filePath);
        return ParseSkillContent(File.ReadAllText(filePath), fallback, filePath);
    }

    /// <summary>
    /// Parse skill text. Accepts both CluadeX keys (whenToUse, allowedTools) and the Claude Code / Anthropic spelling
    /// (when_to_use, allowed-tools, argument-hint, user-invocable), quoted values, block scalars (description: &gt;)
    /// and YAML lists. Claude Code tool names in allowed-tools (Read, Bash(git:*), …) are mapped to CluadeX tools.
    /// </summary>
    public static SkillDefinition ParseSkillContent(string content, string fallbackName, string filePath = "")
    {
        content = content.Replace("\r\n", "\n");
        var match = FrontmatterRegex.Match(content);

        if (!match.Success)
        {
            // No frontmatter — treat entire file as prompt with filename as name
            return new SkillDefinition
            {
                Name = fallbackName,
                Description = "Custom skill",
                PromptContent = content.TrimStart('\uFEFF'),
                FilePath = filePath,
            };
        }

        var skill = new SkillDefinition
        {
            PromptContent = match.Groups[2].Value.Trim(),
            FilePath = filePath,
            Name = fallbackName,
        };

        foreach (var (key, value, list) in ReadYaml(match.Groups[1].Value))
        {
            switch (key)
            {
                case "name": if (value.Length > 0) skill.Name = value; break;
                case "description": skill.Description = value; break;
                case "whentouse": skill.WhenToUse = value; break;
                case "allowedtools":
                    var raw = list.Count > 0 ? list : SplitToolList(value);
                    foreach (var t in raw)
                    {
                        string? mapped = MapToolName(t);
                        if (mapped == null) skill.UnknownTools.Add(t);
                        else if (!skill.AllowedTools.Contains(mapped, StringComparer.OrdinalIgnoreCase)) skill.AllowedTools.Add(mapped);
                    }
                    break;
                case "model": skill.Model = value; break;
                case "userinvocable": skill.UserInvocable = value.ToLowerInvariant() is "true" or "yes" or "1"; break;
                case "argumenthint": skill.ArgumentHint = value; break;
                case "license": skill.License = value; break;
                case "source": skill.SourceUrl = value; break;
            }
        }

        return skill;
    }

    /// <summary>Flat YAML: key: value, key: [a, b], key:\n  - a, key: &gt;\n  folded text. Keys lower-cased, '-'/'_' dropped.</summary>
    private static List<(string Key, string Value, List<string> List)> ReadYaml(string yaml)
    {
        var result = new List<(string, string, List<string>)>();
        string? key = null; var scalar = new List<string>(); var list = new List<string>(); string value = ""; bool block = false;

        void Flush()
        {
            if (key == null) return;
            string v = block ? string.Join(" ", scalar).Trim() : value;
            result.Add((key, Unquote(v), list.Select(Unquote).Where(x => x.Length > 0).ToList()));
            key = null; scalar = new(); list = new(); value = ""; block = false;
        }

        foreach (var rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            bool indented = char.IsWhiteSpace(line[0]);
            var kv = indented ? null : YamlLineRegex.Match(line);
            if (kv is { Success: true })
            {
                Flush();
                key = kv.Groups[1].Value.ToLowerInvariant().Replace("-", "").Replace("_", "");
                value = kv.Groups[2].Value.Trim();
                if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+") { block = true; value = ""; }
                continue;
            }
            if (key == null) continue;
            string t = line.Trim();
            if (t.StartsWith("- ")) list.Add(t[2..].Trim());
            else if (block || value.Length == 0) { block = true; scalar.Add(t); }
        }
        Flush();
        return result;
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1];
        return v.Trim();
    }

    private static List<string> SplitToolList(string value)
    {
        value = value.Trim().TrimStart('[').TrimEnd(']');
        // Commas when there are any; otherwise Claude Code's space-separated form ("Read Grep Bash(git:*)").
        var parts = value.Contains(',') ? value.Split(',') : Regex.Split(value, @"\s+(?![^()]*\))");
        return parts.Select(Unquote).Where(t => t.Length > 0).ToList();
    }

    /// <summary>Claude Code tool names → CluadeX tool names. Already-CluadeX names (lower-case) pass through;
    /// a capitalised name that is not a known Claude Code tool returns null (reported as unknown).</summary>
    public static string? MapToolName(string tool)
    {
        string t = tool.Trim();
        int paren = t.IndexOf('(');
        string head = paren > 0 ? t[..paren] : t;
        string? mapped = head switch
        {
            "Read" => "read_file",
            "Write" => "write_file",
            "Edit" => "edit_file",
            "MultiEdit" => "multi_edit",
            "Bash" => "run_command",
            "PowerShell" => "powershell",
            "Glob" => "glob",
            "Grep" => "grep",
            "LS" => "list_files",
            "WebFetch" => "web_fetch",
            "WebSearch" => "web_search",
            "NotebookEdit" => "notebook_edit",
            "TodoWrite" => "todo_write",
            "Task" => "agent_spawn",
            "Skill" => "skill_invoke",
            _ => null,
        };
        if (mapped != null) return mapped;
        if (head.StartsWith("mcp__", StringComparison.Ordinal)) return head;
        return head.Length > 0 && head.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_') ? head : null;
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

            // /comment — Code Editor ▸ Auto-comment from the chat. Only add_code_comments (plus finding the file) is
            // allowed: the chat model picks file / scope / language / model and never edits code itself.
            new()
            {
                Name = "comment",
                Description = "Comment the open (or named) file: what each part does and why — the code is not changed",
                IsBuiltIn = true,
                UserInvocable = true,
                ArgumentHint = "[file] [functions|changed|key|file|<from>-<to>] [th|en|match] [model]",
                AllowedTools = new() { "add_code_comments", "glob", "list_files" },
                PromptContent = """
                    Add explanatory comments to existing code with the add_code_comments tool.

                    1. The file: the one named in the user's arguments. With none named, call the tool WITHOUT a path —
                       it uses the file open in the Code Editor. Given only a file name, find it with glob first.
                    2. Read the rest of the arguments:
                       - scope: functions (default) | changed (only what changed since git HEAD) | key (only key points)
                         | file (the whole file) | a line range like 40-80 → scope=selection with start_line / end_line
                       - language: th | en | match (Thai / English / the file's own); leave out to use the saved choice
                       - model: a model id, if one is given
                    3. Call add_code_comments once per file. Never edit code and never write comments any other way:
                       the tool's model only names lines, the app inserts the comments and proves the code unchanged.
                    4. Reply in the user's language: how many comments went in, what was skipped and why, and that a
                       Time Machine restore point holds the file as it was. If the tool refused, say why in one line.
                    """,
            },

            // ════════════════════════════════════════════════════════════════
            // Sprint 1 #2 — Skill .md Library: 15 ECC-parity built-in skills
            // ════════════════════════════════════════════════════════════════

            // ─── Workflow (7) ────────────────────────────────────────────────
            new()
            {
                Name = "verification-loop",
                Description = "Red-green verification cycle — write test, see fail, patch, see pass, repeat",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command" },
                PromptContent = """
                    Implement the requested change with a verification loop. NO step gets skipped.

                    1. RED:    write a failing test (or repro script) that captures the requirement.
                               Run it. Confirm it fails for the RIGHT reason — not import/syntax.
                    2. PATCH:  make the smallest change that flips it green. Don't generalise.
                    3. GREEN:  run the test (and the full suite if reachable). Must be green.
                    4. REPEAT: peel off the next requirement and start at RED again.

                    If you can't think of a small enough next RED, stop and ask the user to narrow scope.
                    Never patch without a failing test pointing at the bug first.
                    """,
            },
            new()
            {
                Name = "tdd-workflow",
                Description = "Strict TDD with one-assertion-per-test discipline and refactor-on-green",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command" },
                PromptContent = """
                    Strict test-driven development. Follow red → green → refactor, one step at a time.

                    Rules (non-negotiable):
                      - Never write production code before the failing test.
                      - Never refactor on a red test.
                      - One assertion per test where possible.
                      - Test names describe the behaviour, not the implementation.

                    Workflow:
                      1. RED      — smallest failing test capturing one behaviour. Run, confirm fail.
                      2. GREEN    — minimum code to make THAT test pass. Run all tests.
                      3. REFACTOR — rename / extract / dedupe. Tests stay green. Commit.
                      4. Pick the next behaviour, back to 1.

                    Report the test name + verdict + diff after each cycle.
                    """,
            },
            new()
            {
                Name = "deep-research",
                Description = "Multi-source investigation: web + local code + docs, with cited findings",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "web_fetch", "web_search", "run_command" },
                PromptContent = """
                    Conduct systematic research on the user's question. Be exhaustive, not lazy.

                    Phase 1 — SCOPE
                      - Restate the question in one sentence.
                      - List the 3 sub-questions whose answers would resolve it.

                    Phase 2 — GATHER (parallel where possible)
                      - Search the local codebase (grep/glob) for prior art.
                      - web_search for authoritative sources (docs, RFCs, GitHub issues).
                      - web_fetch the top 3 results and quote relevant passages.

                    Phase 3 — SYNTHESISE
                      - Answer each sub-question with citations [source].
                      - Flag contradictions between sources explicitly.
                      - State the confidence level (HIGH/MEDIUM/LOW) and what would raise it.

                    Don't fabricate. If a source is paywalled or unreachable, say so.
                    """,
            },
            new()
            {
                Name = "eval-harness",
                Description = "Define + run an eval suite with pass@k metrics and grader selection",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command" },
                PromptContent = """
                    Define and run an evaluation suite for the AI's recent output. Goal: replace
                    "looks fine" with measurable pass rates.

                    1. Identify the capability under test (one sentence).
                    2. Build/pick a fixture set (5-20 inputs covering happy path + edge cases).
                    3. Choose a grader for each fixture:
                       - rule-based (regex / contains / length / shape)
                       - unit-test (run actual code with the output, capture pass/fail)
                       - llm-judge (delegate to subagent_invoke security-reviewer / code-reviewer)
                    4. Run pass@k (k=1 by default; ask user if they want pass@3).
                    5. Report: pass@k, per-fixture verdict, top 3 failure patterns, regression diff vs last run.

                    Persist the suite to .cluadex/evals/<name>.yaml so the user can re-run later.
                    """,
            },
            new()
            {
                Name = "cost-aware-llm-pipeline",
                Description = "Map tasks to cheapest model that meets the quality bar; flag waste",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "config" },
                PromptContent = """
                    Audit the current chat / agent loop for cost waste. Goal: lower spend without
                    quality regression.

                    Findings format:
                      - Hot prompt #N: routing to <model>, costs $X/call. Could move to <cheaper model>
                        because <reason: deterministic / classification / short answer / cached pattern>.
                      - Wasted re-prompts: <count> redundant calls due to <missing system prompt / no caching / etc>.
                      - Token bloat: <file or context block> is <N> tokens and only <M>% is referenced.

                    Recommendations:
                      1. Tier-route easy work (formatter / classifier / lint) → local Qwen3/Gemma4.
                      2. Cache the system prompt + tools (Anthropic prompt caching, cache_control ephemeral).
                      3. Truncate / summarise large unchanging context.
                      4. Switch from sync agent loop to async batch where applicable.

                    Quantify the estimated $/day savings for each recommendation.
                    """,
            },
            new()
            {
                Name = "context-budget",
                Description = "Allocate the context window across system prompt / history / files",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "config", "memory_list" },
                PromptContent = """
                    Inspect the current context budget and propose a tighter allocation.

                    1. Measure current usage:
                       - System prompt size (tokens)
                       - Conversation history (tokens, last N turns)
                       - Open file contexts (each file with byte and token count)
                       - Memories injected
                    2. Identify waste:
                       - Files referenced but never read by the model
                       - Repeat content across turns (tool results that haven't changed)
                       - Memory entries that are stale or duplicated
                    3. Propose budget targets:
                       - System: ≤8% of window
                       - History: ≤45%, prefer microcompact older turns
                       - Files: ≤30%, lazy-attach via grep instead of full read
                       - Memories: ≤10%
                       - Headroom for response: ≥7%

                    Apply automatic compaction where safe; flag the rest for user approval.
                    """,
            },
            new()
            {
                Name = "autonomous-loop",
                Description = "Run a self-paced loop with explicit exit criteria and progress checkpoints",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command", "git_status", "git_diff", "memory_save" },
                PromptContent = """
                    Take the user's high-level goal and execute it autonomously, but with discipline.

                    Before starting:
                      - Restate the goal in one sentence.
                      - Define EXIT CRITERIA — explicit, testable conditions for "done".
                      - Define ABORT CRITERIA — when to stop and ask for help instead of pushing on.

                    Loop:
                      1. Pick the next smallest task that moves toward the goal.
                      2. Execute it (use tools as needed; spawn subagents for scoped work).
                      3. Verify: tests still pass / build still works / output looks right.
                      4. Checkpoint: save a one-line summary to memory_save so progress survives a crash.
                      5. Re-check exit criteria. If met, stop and report. Otherwise loop.

                    Hard limits: 15 iterations OR 30 minutes elapsed OR abort criteria triggered.
                    If you exceed, stop and hand back to the user with the partial work + remaining list.
                    """,
            },

            // ─── Quality (3) ─────────────────────────────────────────────────
            new()
            {
                Name = "e2e-testing",
                Description = "End-to-end test discovery, running, and triage of failures",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "run_command" },
                PromptContent = """
                    Find, run, and triage end-to-end tests for this project.

                    1. DISCOVER: glob for test files (*test*, *spec*, /e2e/, cypress/, playwright/).
                    2. CLASSIFY: separate unit / integration / E2E by directory or filename convention.
                    3. RUN: invoke the project's test runner (npm test, dotnet test, pytest, go test).
                       Capture full output, don't summarise prematurely.
                    4. TRIAGE each failure:
                       - Test name, file:line
                       - Error message + relevant stack frame
                       - Root cause category: flaky / broken-fixture / actual-regression / env-issue
                       - Suggested next step (rerun / fix fixture / patch code / open issue)
                    5. Report pass/fail count + the triage table.

                    Don't patch the code yet — this skill is REPORT-ONLY. The user picks what to fix.
                    """,
            },
            new()
            {
                Name = "security-scan",
                Description = "Hardened security scan — OWASP top 10 + language-specific traps",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "run_command" },
                PromptContent = """
                    Run a security audit on the current change set (or the whole repo if scope = full).

                    Findings format:
                      [SEVERITY] file:line — Issue title
                      Description: what is wrong + why it matters
                      Reproduction: minimal example of the exploit
                      Fix: concrete patch suggestion (with diff if possible)

                    Severity:
                      CRITICAL: RCE, auth bypass, data exfiltration, hardcoded prod secret
                      HIGH:     SQLi/XSS/SSRF, missing access control, broken crypto
                      MEDIUM:   weak crypto, missing rate limit, info disclosure in errors
                      LOW:      missing headers, verbose logging, weak defaults

                    Scan checklist:
                      - OWASP Top 10
                      - Hardcoded secrets (grep `api[_-]?key`, `secret`, `0x[a-f0-9]{64}`)
                      - Log statements leaking creds / mnemonics / PII
                      - Missing input validation (anywhere user input meets a sink)
                      - Path traversal in file ops
                      - Unsafe deserialisation (pickle/yaml.load/BinaryFormatter)
                      - String == for secret comparison (timing leak)
                      - Missing rate limits on public endpoints
                    """,
            },
            new()
            {
                Name = "agent-introspection-debug",
                Description = "Diagnose why the agent is stuck / looping / hallucinating",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "config", "memory_list" },
                PromptContent = """
                    The user thinks the agent is misbehaving. Diagnose, don't fix yet.

                    Collect evidence:
                      1. Last N tool calls (sequence + outputs) — what did the agent see?
                      2. System prompt currently in effect
                      3. Active skill / subagent (if any) and its allowed_tools
                      4. Active memories injected into the prompt
                      5. Recent error messages or 413/429 events

                    Diagnostic checklist:
                      - Tool result not actually contradicting the agent's plan? (hallucination)
                      - Loop: same tool with same args called >2 times in a row? (stuck)
                      - Allowed_tools too narrow for the task? (handicap)
                      - System prompt has conflicting instructions? (priority confusion)
                      - Memory injected a stale fact contradicting the user's new request?
                      - Context near 90%? (quality degradation)

                    Output: top 3 likely causes, ranked by evidence strength, each with a one-line fix.
                    """,
            },

            // ─── Productivity (3) ────────────────────────────────────────────
            new()
            {
                Name = "content-engine",
                Description = "Batch content generation with brand voice + per-asset distribution plan",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "web_search", "web_fetch" },
                PromptContent = """
                    Generate a batch of content assets (blog / social / email / landing copy) with
                    a consistent voice and a distribution plan for each.

                    Inputs (ask if missing):
                      - Topic / angle
                      - Audience (technical depth, pains, motivations)
                      - Tone (read existing brand assets via grep/glob to anchor the voice)
                      - Surfaces (which channels)
                      - Deadline + frequency

                    For each surface, produce:
                      - Headline
                      - Hook (first 2 lines)
                      - Body (sized per surface: tweet ≤280, blog ≥800, email ≥200)
                      - CTA
                      - Hashtags / SEO terms
                      - "Best time to post" suggestion

                    Save each asset as a separate file in /content/<date>-<surface>-<slug>.md so the
                    user can review and post.
                    """,
            },
            new()
            {
                Name = "market-research",
                Description = "Competitive scan: features, pricing, positioning, gaps to exploit",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "web_search", "web_fetch", "write_file" },
                PromptContent = """
                    Profile the competitive landscape for the user's product / feature.

                    1. IDENTIFY 5-8 competitors (direct + adjacent).
                    2. For each:
                       - Tagline / positioning
                       - Pricing model + key plan tiers
                       - Core feature list (what they brag about)
                       - Last shipped feature (recency signal — are they alive)
                       - Estimated traction (GH stars / NPM dl / Twitter followers / customer logos)
                    3. SYNTHESISE:
                       - Common features (table stakes)
                       - Differentiated features (what wins customers)
                       - GAPS — features missing across all competitors (white space)
                       - PRICING GAPS — under-served price points

                    Output: a markdown report saved to /research/competitors-<date>.md.
                    Cite every claim with a URL.
                    """,
            },
            new()
            {
                Name = "harness-optimizer",
                Description = "Tune the agent harness itself — context, tools, hooks, prompts",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "edit_file", "grep", "glob", "config", "memory_list" },
                PromptContent = """
                    The agent harness (system prompt, tools, hooks, memory) is the product. Tune it.

                    Audit dimensions:
                      1. SYSTEM PROMPT — too long? contradictions? outdated tool list? missing
                         examples? Recommend specific deletions / additions with diff.
                      2. TOOLS — duplicates (read_file + grep + glob serving same need)? Missing
                         tools causing the agent to fall back to slow alternatives?
                      3. HOOKS — any noisy hooks firing too often? Missing hooks for known traps
                         (secret-scan, prettier-format, console.log warn)?
                      4. MEMORY — stale facts? duplicate entries? entries that should be skill
                         frontmatter instead?
                      5. CONTEXT — files always-included that should be lazy-loaded?

                    Output ranked recommendations with expected impact (cost / latency / quality)
                    and a one-click change set the user can approve.
                    """,
            },

            // ─── Pattern (2) ─────────────────────────────────────────────────
            new()
            {
                Name = "multi-plan",
                Description = "Generate 3 alternative implementation plans for the same spec",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "grep", "glob", "git_log" },
                PromptContent = """
                    Don't lock in on one design. Generate THREE distinct plans for the user's spec.

                    For each plan:
                      ## Plan <N>: <one-line name>

                      **Approach** — high-level shape (e.g. "OOP service layer", "FP pipeline", "tiny CLI script")
                      **Strengths** — 3 bullets
                      **Weaknesses** — 3 bullets
                      **Effort** — small / medium / large (with reasoning)
                      **Best for** — what kind of follow-up work this plan favours
                      **Sketch** — 5-10 lines of pseudocode or component list

                    End with a recommendation table:
                      | Criterion | Plan 1 | Plan 2 | Plan 3 |
                      |---|---|---|---|
                      | Ship speed | … | … | … |
                      | Maintainability | … | … | … |
                      | Cost to revert | … | … | … |
                      | Fits team skill | … | … | … |

                    The user picks. Don't pick for them. After they pick, use multi-execute to ship.
                    """,
            },
            new()
            {
                Name = "multi-execute",
                Description = "Spin up 3 git worktrees and implement all 3 plans in parallel",
                IsBuiltIn = true, UserInvocable = true,
                AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command", "git_status", "git_diff", "git_worktree_create" },
                PromptContent = """
                    Take the 3 plans (from multi-plan) and ship them in parallel worktrees so the
                    user can diff and pick.

                    1. For each plan, create a worktree:
                         git worktree add ../<repo>-plan-<N> <new-branch-name>
                    2. In each worktree, implement the corresponding plan end-to-end.
                       Each implementation MUST:
                         - Pass the same test suite
                         - Have the same external API surface (so they're comparable)
                         - End with one commit per worktree
                    3. After all three are done, produce a comparison:
                         - LOC count per implementation
                         - Files touched
                         - Performance/behaviour deltas (if measurable)
                         - "Pick this one if …" recommendation

                    The user picks the winner. Merge that branch and remove the other worktrees.
                    """,
            },
        };
    }
}
