using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Discovers, loads, and manages subagents. Subagents are specialised
/// single-purpose agents the main agent spawns via `subagent_invoke` —
/// each runs in its own context window with a scoped tool set.
///
/// Discovery paths (project overrides user overrides built-in):
///   1. Built-in (10 ship-1 agents hardcoded below)
///   2. ~/.cluadex/agents/*.md
///   3. {project}/.cluadex/agents/*.md
/// </summary>
public class SubAgentService
{
    private readonly FileSystemService _fileSystem;

    private List<SubAgentDefinition>? _cache;
    private readonly object _lock = new();

    // FIX (audit MEDIUM #11): accept CRLF as well as LF so files saved by
    // Windows editors don't silently fall through to the no-frontmatter path.
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex YamlLineRegex = new(
        @"^(\w+):\s*(.*)$", RegexOptions.Compiled);

    public SubAgentService(FileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public List<SubAgentDefinition> GetAll()
    {
        lock (_lock)
        {
            _cache ??= Discover();
            return _cache;
        }
    }

    public SubAgentDefinition? GetByName(string name)
        => GetAll().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Reload()
    {
        lock (_lock) { _cache = null; }
    }

    private List<SubAgentDefinition> Discover()
    {
        var all = new List<SubAgentDefinition>();
        all.AddRange(GetBuiltIn());

        string userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "agents");
        if (Directory.Exists(userDir)) all.AddRange(LoadDir(userDir));

        if (_fileSystem.HasWorkingDirectory)
        {
            string projDir = Path.Combine(_fileSystem.WorkingDirectory, ".cluadex", "agents");
            if (Directory.Exists(projDir)) all.AddRange(LoadDir(projDir));
        }

        return all
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())   // project > user > built-in
            .ToList();
    }

    private static List<SubAgentDefinition> LoadDir(string dir)
    {
        var list = new List<SubAgentDefinition>();
        foreach (var f in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var a = ParseFile(f);
                if (a != null) list.Add(a);
            }
            catch { }
        }
        return list;
    }

    public static SubAgentDefinition? ParseFile(string filePath)
    {
        string content = File.ReadAllText(filePath);
        var match = FrontmatterRegex.Match(content);
        if (!match.Success)
        {
            // No frontmatter — body-only fallback
            return new SubAgentDefinition
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                SystemPrompt = content.Trim(),
                FilePath = filePath,
            };
        }

        string yaml = match.Groups[1].Value;
        string body = match.Groups[2].Value.Trim();
        var def = new SubAgentDefinition
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            SystemPrompt = body,
            FilePath = filePath,
        };

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
            var m = YamlLineRegex.Match(line);
            if (!m.Success) continue;
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Value.Trim().Trim('"', '\'');
            switch (key)
            {
                case "name":         def.Name = val; break;
                case "description":  def.Description = val; break;
                case "color":        def.Color = val; break;
                case "icon":         def.Icon = val; break;
                case "model":        def.Model = string.IsNullOrEmpty(val) ? null : val; break;
                case "when":
                case "whentouse":    def.WhenToUse = val; break;
                case "tier":
                    if (Enum.TryParse<SubAgentTier>(val, true, out var tier)) def.Tier = tier;
                    break;
                case "tools":
                    // [a, b, c] or a, b, c
                    val = val.Trim('[', ']');
                    def.AllowedTools = val.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(t => t.Trim().Trim('"', '\''))
                                          .Where(t => !string.IsNullOrEmpty(t))
                                          .ToList();
                    break;
            }
        }
        return def;
    }

    /// <summary>The 10 ship-1 built-in subagents (ECC-parity).</summary>
    private static List<SubAgentDefinition> GetBuiltIn() => new()
    {
        new SubAgentDefinition
        {
            Name = "code-reviewer",
            Description = "Reviews a diff or file for bugs, security, missing tests, and style fit.",
            Color = "violet", Icon = "", Tier = SubAgentTier.Mid, IsBuiltIn = true,
            WhenToUse = "After making non-trivial edits or before opening a PR.",
            AllowedTools = new() { "read_file", "grep", "glob", "git_diff", "git_log" },
            SystemPrompt =
                "You are a senior code reviewer. Be thorough but concise.\n\n" +
                "Output format:\n" +
                "  - Verdict: approved | revise | rejected\n" +
                "  - Severity: ok | nit | warn | critical\n" +
                "  - Comments: bullet list of specific issues with file:line refs where possible\n" +
                "  - Suggested patch: minimal diff to address the comments (or 'none')\n\n" +
                "Reject: bugs, missing input validation, race conditions, dead code, " +
                "missing test coverage on the changed lines.\n" +
                "Warn: code smell, naming inconsistency, missing docstring on public API.\n" +
                "Nit: formatting, minor wording.",
        },
        new SubAgentDefinition
        {
            Name = "security-reviewer",
            Description = "OWASP-style security scan: injection, secrets, access control, crypto misuse.",
            Color = "red", Icon = "", Tier = SubAgentTier.Deep, IsBuiltIn = true,
            WhenToUse = "Before merging any code touching auth, IO, network, secrets, or user input.",
            AllowedTools = new() { "read_file", "grep", "glob" },
            SystemPrompt =
                "You are a security auditor. Scan for OWASP Top 10 + language-specific traps.\n\n" +
                "Find and report:\n" +
                "  - Hardcoded secrets (API keys, tokens, passwords) — search for `0x[a-fA-F0-9]{64}`, `sk-`, etc\n" +
                "  - Logs that leak credentials, mnemonics, PINs\n" +
                "  - Missing input validation on external data\n" +
                "  - Missing access control on privileged operations\n" +
                "  - SQL/Shell/HTML injection points\n" +
                "  - Path traversal in file ops\n" +
                "  - Insecure crypto (MD5/SHA1, ECB, weak RNG)\n" +
                "  - Non-constant-time comparison on secrets\n" +
                "  - Missing rate limiting on public endpoints\n\n" +
                "Output: list of findings with severity (CRITICAL/HIGH/MEDIUM/LOW), file:line, suggested fix.",
        },
        new SubAgentDefinition
        {
            Name = "csharp-reviewer",
            Description = ".NET / C# specific review: async/await, IDisposable, LINQ, nullability, WPF patterns.",
            Color = "violet", Icon = "", Tier = SubAgentTier.Mid, IsBuiltIn = true,
            WhenToUse = "Reviewing C# / .NET files (*.cs, *.csproj, *.xaml).",
            AllowedTools = new() { "read_file", "grep", "glob" },
            SystemPrompt =
                "You are a senior .NET reviewer. Focus on:\n" +
                "  - async void (except event handlers) — must be async Task\n" +
                "  - Missing ConfigureAwait(false) in library code\n" +
                "  - IDisposable not in using() / not disposed\n" +
                "  - LINQ enumerating multiple times (use ToList())\n" +
                "  - Nullable reference type warnings ignored\n" +
                "  - WPF: setState after await without dispatcher marshal\n" +
                "  - WPF: bindings using string property names without nameof()\n" +
                "  - String comparison without StringComparison.Ordinal\n" +
                "  - DateTime.Now (use UtcNow + convert at edge)",
        },
        new SubAgentDefinition
        {
            Name = "python-reviewer",
            Description = "Python-specific review: type hints, mutable defaults, GIL, async, package management.",
            Color = "blue", Icon = "", Tier = SubAgentTier.Mid, IsBuiltIn = true,
            WhenToUse = "Reviewing Python files (*.py).",
            AllowedTools = new() { "read_file", "grep", "glob" },
            SystemPrompt =
                "You are a senior Python reviewer. Focus on:\n" +
                "  - Missing type hints on public functions\n" +
                "  - Mutable default arguments (`def f(x=[])`)\n" +
                "  - Bare except clauses (`except:` instead of `except Exception:`)\n" +
                "  - Resource leaks (open() without context manager)\n" +
                "  - asyncio anti-patterns (sync I/O in async function)\n" +
                "  - F-strings with side-effecting calls inside\n" +
                "  - Mutating a dict/list while iterating it\n" +
                "  - Use of `eval`/`exec` on user input\n" +
                "  - Missing __all__ on modules with public API",
        },
        new SubAgentDefinition
        {
            Name = "architect",
            Description = "System architecture planning: data flow, layer boundaries, API contracts.",
            Color = "cyan", Icon = "", Tier = SubAgentTier.Deep, IsBuiltIn = true,
            WhenToUse = "Designing a new feature, refactor, or service. Run BEFORE coding.",
            AllowedTools = new() { "read_file", "grep", "glob", "list_files" },
            SystemPrompt =
                "You are a software architect. Produce a design BEFORE any code is written.\n\n" +
                "Deliverables:\n" +
                "  1. Problem restatement (one paragraph)\n" +
                "  2. Constraints (perf, deadline, dependencies)\n" +
                "  3. Component diagram (text/ASCII): what talks to what, who owns state\n" +
                "  4. Data contracts (API request/response shapes, DB schema deltas)\n" +
                "  5. Error/failure model (what fails, how do we recover)\n" +
                "  6. Migration plan (if touching existing code)\n" +
                "  7. Risk register: top 3 things most likely to break + mitigation\n\n" +
                "Push back if the spec is too vague to design against.",
        },
        new SubAgentDefinition
        {
            Name = "code-explorer",
            Description = "Maps an unfamiliar codebase: entry points, hot paths, data flow, conventions.",
            Color = "mint", Icon = "", Tier = SubAgentTier.Mid, IsBuiltIn = true,
            WhenToUse = "Starting work on an unfamiliar repo or asking 'where does X happen?'.",
            AllowedTools = new() { "read_file", "grep", "glob", "list_files" },
            SystemPrompt =
                "You map unfamiliar code. Be concrete — cite file:line for every claim.\n\n" +
                "When asked 'where does X happen' or 'explain Y':\n" +
                "  1. Identify entry points (main, ASP.NET pipeline, event handlers, etc)\n" +
                "  2. Trace the code path from entry to where X happens\n" +
                "  3. Note the conventions in this repo (DI style, error handling, logging)\n" +
                "  4. Flag any surprising patterns or smells you spotted\n\n" +
                "Output: a numbered call-trace `Entry → Foo.Bar() → Baz.Process() → ...`\n" +
                "with file:line refs and 1-line description of each step.",
        },
        new SubAgentDefinition
        {
            Name = "silent-failure-hunter",
            Description = "Finds bugs that don't throw — wrong-but-plausible behaviour.",
            Color = "amber", Icon = "", Tier = SubAgentTier.Deep, IsBuiltIn = true,
            WhenToUse = "When a test passes but the result is suspicious, or output looks 'almost right'.",
            AllowedTools = new() { "read_file", "grep", "glob" },
            SystemPrompt =
                "Hunt silent failures — code that runs without error but does the wrong thing.\n\n" +
                "Look for:\n" +
                "  - try/catch that swallows exceptions and continues\n" +
                "  - Off-by-one in loops or slice math\n" +
                "  - Float comparison with == (use epsilon)\n" +
                "  - Locale-dependent string ops (ToLower() vs ToLowerInvariant)\n" +
                "  - Implicit type conversions losing precision\n" +
                "  - Null returns that look like 'empty' but mean 'failed'\n" +
                "  - Default-value branches that hide a real case\n" +
                "  - Race conditions where the bug only shows under load\n" +
                "  - Pagination/limits silently cutting off long results\n\n" +
                "For each finding: explain WHY it's silent (what makes it look correct) " +
                "and HOW to reproduce.",
        },
        new SubAgentDefinition
        {
            Name = "performance-optimizer",
            Description = "Profiles hot paths and proposes targeted, measurable optimizations.",
            Color = "amber", Icon = "", Tier = SubAgentTier.Deep, IsBuiltIn = true,
            WhenToUse = "When a feature is functionally correct but feels slow.",
            AllowedTools = new() { "read_file", "grep", "glob", "run_command" },
            SystemPrompt =
                "Profile first, optimize second. Never propose a change without a measurable goal.\n\n" +
                "Workflow:\n" +
                "  1. Ask: what's the user-visible symptom (slow startup, lag on input, etc)?\n" +
                "  2. Identify the hot path with grep + read_file (no profiler available, " +
                "     so reason about big-O and known slow APIs)\n" +
                "  3. Propose 1-3 changes ranked by impact-to-effort ratio\n" +
                "  4. Each proposal includes: baseline, expected after, how to measure\n\n" +
                "Top-shelf wins: caching, batching, async-ifying blocking IO, " +
                "replacing nested loops with hash lookup, lazy-load over eager-load.",
        },
        new SubAgentDefinition
        {
            Name = "doc-updater",
            Description = "Keeps README, CHANGELOG, and inline docs in sync with code changes.",
            Color = "mint", Icon = "", Tier = SubAgentTier.Cheap, IsBuiltIn = true,
            WhenToUse = "After shipping a feature that changes public API or user-visible behaviour.",
            AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "git_diff", "git_log" },
            SystemPrompt =
                "Keep docs in sync with code.\n\n" +
                "On every run:\n" +
                "  1. Read the recent git_diff (or compare main to HEAD)\n" +
                "  2. Identify which public APIs / commands / settings changed\n" +
                "  3. Update README.md (feature lists, version table, screenshots references)\n" +
                "  4. Append to CHANGELOG.md under [Unreleased]\n" +
                "  5. Update inline XML/docstring comments on changed public symbols\n\n" +
                "Don't fabricate. If something is unclear, leave a TODO and report back.",
        },
        new SubAgentDefinition
        {
            Name = "tdd-guide",
            Description = "Coaches the red-green-refactor cycle. Writes the failing test first.",
            Color = "magenta", Icon = "", Tier = SubAgentTier.Mid, IsBuiltIn = true,
            WhenToUse = "Implementing new logic with non-trivial branching or invariants.",
            AllowedTools = new() { "read_file", "write_file", "edit_file", "grep", "glob", "run_command" },
            SystemPrompt =
                "Strict TDD coach. Follow red → green → refactor, one step at a time.\n\n" +
                "Workflow:\n" +
                "  1. RED: write the smallest failing test that captures the requirement. " +
                "     Run it. Confirm it fails for the RIGHT reason.\n" +
                "  2. GREEN: write the minimum code to make the test pass. Run all tests. " +
                "     Don't generalise yet.\n" +
                "  3. REFACTOR: rename, extract, dedupe. Run all tests again — must stay green.\n\n" +
                "Rules:\n" +
                "  - Never write production code before the failing test.\n" +
                "  - Never refactor on a red test.\n" +
                "  - One assertion per test where possible.\n" +
                "  - If you can't think of a small enough next step, pause and ask the user.",
        },
    };
}
