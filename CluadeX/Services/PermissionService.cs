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
    private readonly string _dataRoot;
    private readonly object _gate = new(); // guards Rules across agent-thread checks + UI-thread edits

    public List<PermissionRule> Rules { get; private set; } = new();

    public PermissionService(SettingsService settingsService)
    {
        _dataRoot = settingsService.DataRoot;
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

    /// <summary>
    /// The user's rule for one MCP tool. Three kinds of rule can match: the tool's full name "mcp__server__tool", a
    /// server-wide "mcp__&lt;server&gt;__*" ("every tool of this server"), and the bare leaf ("navigate").
    /// A Deny of ANY kind wins — blocking a whole server is never undone by an Allow that names one of its tools.
    /// Otherwise the full name is the most specific; a server-wide rule beats a bare leaf (a leaf alone could mean a
    /// tool of any server). The server must match exactly (case-insensitive), so "mcp__brainx__*" never covers
    /// "brainx-brain". A blanket "*" tool rule never counts. Null = no rule (the caller applies the tool's default risk).
    /// </summary>
    public PermAction? FindMcpToolRule(string? serverName, string leaf, string qualifiedName)
    {
        var byFullName = FindNamedToolRule(qualifiedName);
        var byLeaf = string.IsNullOrEmpty(leaf) || string.Equals(leaf, qualifiedName, StringComparison.OrdinalIgnoreCase)
            ? null : FindNamedToolRule(leaf);
        PermAction? serverWide = null;
        if (!string.IsNullOrEmpty(serverName))
        {
            string pattern = $"mcp__{serverName}__*";
            foreach (var rule in SnapshotRules())
            {
                if (string.Equals(rule.ToolName?.Trim(), pattern, StringComparison.OrdinalIgnoreCase))
                {
                    serverWide = rule.Action;
                    break;
                }
            }
        }
        if (byFullName == PermAction.Deny || serverWide == PermAction.Deny || byLeaf == PermAction.Deny) return PermAction.Deny;
        return byFullName ?? serverWide ?? byLeaf;
    }

    // ─── "Accept edits" (session mode) ───
    // write_file / edit_file / multi_edit / create_directory inside the open project run without a prompt. Held in
    // memory only, bound to the project folder it was switched on for: switching projects (or restarting the app)
    // ends it even if a caller forgets to reset it, because another folder never matches.
    private string? _acceptEditsProject;

    /// <summary>Raised when Accept edits is switched on or off (any thread).</summary>
    public event Action? AcceptEditsChanged;

    /// <summary>The project folder Accept edits is on for, or null when it is off.</summary>
    public string? AcceptEditsProject
    {
        get { lock (_gate) return _acceptEditsProject; }
    }

    /// <summary>
    /// Switch Accept edits on for <paramref name="projectRoot"/>, or off with null/empty. Returns false — and leaves
    /// it OFF — for a folder it must never cover (<see cref="IsUnsafeAcceptEditsRoot"/>: a drive root, the user
    /// folder, a system folder, the app's own data): "inside the project" would then include the Startup folder,
    /// ~/.ssh or permissions.json.
    /// </summary>
    public bool SetAcceptEdits(string? projectRoot)
    {
        string? normalized = null;
        bool refused = false;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            try { normalized = Path.GetFullPath(projectRoot).TrimEnd('\\', '/'); }
            catch { normalized = null; }
            if (normalized == null || IsUnsafeAcceptEditsRoot(normalized, _dataRoot))
            {
                normalized = null;
                refused = true;
            }
        }
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_acceptEditsProject, normalized, StringComparison.OrdinalIgnoreCase);
            _acceptEditsProject = normalized;
        }
        if (changed) AcceptEditsChanged?.Invoke();
        return !refused;
    }

    /// <summary>
    /// A folder Accept edits must never be switched on for: a drive (or share) root; a folder that CONTAINS the user
    /// profile, Windows, Program Files, ProgramData, AppData, the Startup folders, ~/.ssh or CluadeX's data
    /// (<paramref name="dataRoot"/>); or a folder INSIDE Windows, Program Files, ProgramData, AppData\Roaming, a Start
    /// Menu, ~/.ssh or CluadeX's data. A normal project under the user folder (C:\Users\me\src\app) is fine.
    /// </summary>
    public static bool IsUnsafeAcceptEditsRoot(string projectRoot, string? dataRoot = null)
    {
        string root;
        try { root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/'); }
        catch { return true; }
        if (root.Length == 0) return true;
        // A folder reached through a junction/symlink (D:\proj → C:\Users\me) is judged where it really is, too.
        string? real = ResolveThroughLinks(root);
        if (real == null) return true;
        if (!string.Equals(real, root, StringComparison.OrdinalIgnoreCase) && IsUnsafeAcceptEditsRootCore(real, dataRoot)) return true;
        return IsUnsafeAcceptEditsRootCore(root, dataRoot);
    }

    /// <summary>The path with every junction/symlink on the way resolved to its final target; the path itself when there
    /// are none; null when a link can't be resolved.</summary>
    private static string? ResolveThroughLinks(string fullPath)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(driveRoot)) return fullPath;
            string cur = driveRoot;
            string[] parts = fullPath[driveRoot.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                cur = Path.Combine(cur, parts[i]);
                if (!Directory.Exists(cur))
                {
                    // The rest does not exist (yet): nothing further to resolve.
                    for (int j = i + 1; j < parts.Length; j++) cur = Path.Combine(cur, parts[j]);
                    break;
                }
                if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) == 0) continue;
                var target = Directory.ResolveLinkTarget(cur, returnFinalTarget: true);
                if (target == null) continue;   // a reparse point that is not a link (e.g. a OneDrive placeholder)
                cur = Path.GetFullPath(target.FullName);
            }
            return cur.TrimEnd('\\', '/');
        }
        catch { return null; }
    }

    private static bool IsUnsafeAcceptEditsRootCore(string root, string? dataRoot)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(root)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(driveRoot) || string.Equals(driveRoot, root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { return true; }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string F(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return ""; } }
        // Documents holds the PowerShell profiles (WindowsPowerShell\Microsoft.PowerShell_profile.ps1,
        // PowerShell\profile.ps1), which run every time PowerShell starts. A project INSIDE Documents is fine.
        string documents = F(Environment.SpecialFolder.MyDocuments);
        // The project may not CONTAIN any of these (switching on for an ancestor covers them)…
        var mustNotContain = new[]
        {
            profile, documents, F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.System),
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.Startup),
            F(Environment.SpecialFolder.CommonStartup), F(Environment.SpecialFolder.StartMenu),
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".ssh"),
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".cluadex"),
            dataRoot ?? "",
        };
        // …nor sit INSIDE these (code there runs at logon, as the system, or as CluadeX's own settings).
        var mustNotBeInside = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.StartMenu), F(Environment.SpecialFolder.CommonStartMenu),
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".ssh"),
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".cluadex"),
            string.IsNullOrEmpty(documents) ? "" : Path.Combine(documents, "WindowsPowerShell"),
            string.IsNullOrEmpty(documents) ? "" : Path.Combine(documents, "PowerShell"),
            dataRoot ?? "",
        };
        foreach (var f in mustNotContain)
            if (IsSameOrUnder(f, root)) return true;
        foreach (var f in mustNotBeInside)
            if (IsSameOrUnder(root, f)) return true;
        return false;
    }

    /// <summary>Is <paramref name="path"/> the folder <paramref name="folder"/> or somewhere below it?</summary>
    private static bool IsSameOrUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/');
            string f = Path.GetFullPath(folder).TrimEnd('\\', '/');
            return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Is Accept edits on for this project folder?</summary>
    public bool AcceptsEditsIn(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return false;
        return Same(AcceptEditsProject, projectRoot) || Same(AgentRunScope.Current?.AcceptEditsRoot, projectRoot);

        static bool Same(string? on, string root)
        {
            if (string.IsNullOrWhiteSpace(on)) return false;
            try { return string.Equals(Path.GetFullPath(on).TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Does Accept edits cover writing <paramref name="path"/> (relative to, or absolute under,
    /// <paramref name="projectRoot"/>)? Only when it is on for that project and the path is plainly inside it:
    /// not the root itself, not through a junction/symlink, no alternate data stream or device name — and not a file
    /// that runs code later (<see cref="IsCodeRunningConfig"/>): a git or editor hook, a CI workflow, or a file that
    /// defines or runs the build, which the automatic build check would then execute.
    /// </summary>
    public bool AcceptEditsCovers(string projectRoot, string path)
        => AcceptsEditsIn(projectRoot) && IsPlainPathInside(projectRoot, path);

    /// <summary>Accept edits for the project also covers a related folder the run opened beside it — with the same
    /// exclusions (.git, hook folders, junctions out).</summary>
    public bool AcceptEditsCoversRelated(string projectRoot, string relatedRoot, string path)
        => AcceptsEditsIn(projectRoot) && !IsUnsafeAcceptEditsRoot(relatedRoot, null) && IsPlainPathInside(relatedRoot, path);

    /// <summary>Folders whose files run code on their own (git hooks, hook managers, editor tasks, agent settings) —
    /// and credential folders (.ssh, .gnupg, .aws), which a project has no business editing unseen.</summary>
    private static readonly HashSet<string> CodeRunningFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".cluadex", ".husky", ".githooks", ".vscode", ".claude", ".idea", ".devcontainer", ".ssh", ".gnupg", ".aws",
    };

    /// <summary>Files that define or run a build/restore/test: the automatic build check after an edit runs them.</summary>
    private static readonly HashSet<string> BuildFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", ".npmrc", ".yarnrc", ".yarnrc.yml", ".pnpmfile.cjs", "makefile", "gnumakefile", "cmakelists.txt",
        "build.rs", "cargo.toml", "pyproject.toml", "setup.py", "setup.cfg", "build.gradle", "build.gradle.kts",
        "settings.gradle", "settings.gradle.kts", "gradlew", "gradlew.bat", "pom.xml", "global.json", "nuget.config",
        "justfile", "rakefile", "build.ps1", "build.cmd", "build.bat", "build.sh",
        // Loaded and run by the test runner itself (pytest / tox / nox), and by pre-commit on every commit.
        "conftest.py", "pytest.ini", "tox.ini", "noxfile.py", ".pre-commit-config.yaml",
    };

    private static readonly HashSet<string> BuildExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj", ".props", ".targets", ".sln", ".slnx", ".slnf", ".cmake", ".mk",
    };

    /// <summary>vite.config.ts, webpack.config.js, jest.config.mjs … — JavaScript the build tool loads and runs —
    /// and the rc-file forms (.eslintrc.js, .prettierrc.cjs, .babelrc.js, .mocharc.mjs …), which a linter/formatter
    /// hook or the test runner runs the same way.</summary>
    private static readonly Regex JsToolConfig = new(@"(\.config\.(c|m)?(j|t)s|^\.[\w.-]*rc\.(c|m)?(j|t)s)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Is this project-relative path a file that runs code later without anyone running it on purpose: anything under
    /// .git, .cluadex, .husky, .githooks, .vscode, .claude, .idea, .devcontainer or .github/workflows|actions, or a file
    /// that defines or runs the build (*.csproj, *.csproj.user, *.props, *.targets, *.sln, Directory.Build.*, package.json,
    /// .npmrc, *.config.js/ts, .*rc.js, Makefile, CMakeLists.txt, build.rs, Cargo.toml, pyproject.toml, setup.py,
    /// conftest.py, pytest.ini, tox.ini, build.gradle*, pom.xml …)?
    /// Accept edits never covers these: they still ask.
    /// </summary>
    public static bool IsCodeRunningConfig(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        string[] parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd('.', ' ')).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (CodeRunningFolders.Contains(parts[i])) return true;
            if (parts[i].Equals(".github", StringComparison.OrdinalIgnoreCase)
                && (parts[i + 1].Equals("workflows", StringComparison.OrdinalIgnoreCase) || parts[i + 1].Equals("actions", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        string name = parts[^1];
        if (CodeRunningFolders.Contains(name)) return true;   // the folder itself (create_directory .husky)
        if (BuildFiles.Contains(name) || BuildExtensions.Contains(Path.GetExtension(name))) return true;
        // App.csproj.user, Build.props.user …: MSBuild imports a project's .user file by itself.
        if (Path.GetExtension(name).Equals(".user", StringComparison.OrdinalIgnoreCase)
            && BuildExtensions.Contains(Path.GetExtension(Path.GetFileNameWithoutExtension(name)))) return true;
        if (name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase)) return true;
        return JsToolConfig.IsMatch(name);
    }

    /// <summary>The rule behind <see cref="AcceptEditsCovers"/>'s path check (public for the harnesses): a plain
    /// file inside the project that is not <see cref="IsCodeRunningConfig"/>.</summary>
    public static bool IsPlainPathInside(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
            string p = path.Trim();
            // "file.txt:stream", "\\?\…", "\\.\…" — never a plain project file.
            if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return false;
            int colon = p.IndexOf(':');
            if (colon >= 0 && !(colon == 1 && Path.IsPathRooted(p))) return false;
            if (p.IndexOf(':', colon + 1) >= 0) return false;

            string full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p)).TrimEnd('\\', '/');
            string rootSlash = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootSlash, StringComparison.OrdinalIgnoreCase)) return false;   // outside, or the root itself

            string relative = full[rootSlash.Length..];
            // Hooks, editor tasks, CI workflows and build files run code later — they still ask.
            if (IsCodeRunningConfig(relative)) return false;
            string[] parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string seg = part.TrimEnd('.', ' ');
                // An 8.3 short name ("GIT~1") can be another way to spell .git — not a plain path.
                if (Regex.IsMatch(seg, @"~\d")) return false;
                // CON, NUL, COM1… resolve to devices, not files.
                string stem = Path.GetFileNameWithoutExtension(seg).ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                    return false;
            }

            // Every existing folder on the way down (and the target itself) must be a real folder/file, not a link:
            // a junction inside the project can point anywhere on the disk.
            string cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                cur = Path.Combine(cur, parts[i]);
                FileAttributes attrs;
                try
                {
                    if (!File.Exists(cur) && !Directory.Exists(cur)) break;   // the rest does not exist yet
                    attrs = File.GetAttributes(cur);
                }
                catch { return false; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Tools that run something: a shell command, code, a program, or keys into another app.</summary>
    private static readonly HashSet<string> RunTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command", "powershell", "run_build", "run_tests", "bg_start", "repl", "python", "node", "repl_exec",
        "execute_code", "run_code", "desktop_launch", "desktop_type", "desktop_keys",
    };

    /// <summary>True for a tool that runs something (see <see cref="RunTools"/>).</summary>
    public static bool IsRunTool(string? toolName) => toolName != null && RunTools.Contains(toolName);

    /// <summary>A rule that names no tool and whose pattern matches anything ("Allow *").</summary>
    private static bool IsBlanket(PermissionRule rule) =>
        string.IsNullOrWhiteSpace(rule.ToolName) && (rule.Pattern ?? "").Trim().Trim('*', '/', '\\').Length == 0;

    /// <summary>The action of the first rule that matches, or null when no rule does — so a caller can
    /// apply its own default (e.g. reading inside the open project) without overriding a user rule.
    /// A blanket Allow ("Allow *") never approves running something — a command, code, a program, keys into an app
    /// (scope "execute", or a <see cref="IsRunTool">run tool</see>): only a rule that names the tool does. On
    /// 2026-10-17 an "Allow *" rule let an agent's own test run <c>rd /s /q C:\</c> unasked. A blanket Deny still denies.
    /// <paramref name="runCall"/> overrides the run test when the caller looks a rule up under a family name.</summary>
    public PermAction? FindRule(string resource, string scope, string? toolName, bool? runCall = null)
        => FirstMatch(SnapshotRules(), resource, scope, toolName, runCall);

    /// <summary><see cref="FindRule"/> over a given rule list (pure — no file, no state).</summary>
    public static PermAction? FirstMatch(IEnumerable<PermissionRule> rules, string resource, string scope, string? toolName, bool? runCall = null)
    {
        bool running = runCall ?? (string.Equals(scope, "execute", StringComparison.OrdinalIgnoreCase) || IsRunTool(toolName));
        foreach (var rule in rules)
        {
            if (!ScopeMatches(rule, scope)) continue;
            if (running && rule.Action == PermAction.Allow && IsBlanket(rule)) continue;
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
