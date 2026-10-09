using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

public class PluginService
{
    private readonly SettingsService _settingsService;
    private readonly string _pluginsDir;
    private readonly string _configPath;

    /// <summary>Fires when plugins are installed, enabled, or disabled. Subscribers (e.g. HookService) should reload.</summary>
    public event Action? PluginsChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public PluginService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _pluginsDir = System.IO.Path.Combine(_settingsService.DataRoot, "plugins");
        _configPath = System.IO.Path.Combine(_pluginsDir, "plugins_config.json");
        Directory.CreateDirectory(_pluginsDir);

        // The catalog is the source of truth for a built-in plugin's hooks. Installed copies
        // persist hooks.json at install time, so a later bug-fix to a hook command (e.g. the
        // branch-protector's batch `%%b` → cmd `findstr`) would otherwise never reach machines
        // that already installed it. Re-materialize hooks.json for installed catalog plugins.
        try { ResyncInstalledCatalogHooks(); } catch { /* best-effort — never block startup */ }
    }

    /// <summary>
    /// Rewrites hooks.json (and the hook scripts) for every installed plugin whose folder matches a
    /// curated-catalog id, so catalog hook fixes propagate to existing installs without a manual reinstall.
    /// This is also what moves existing installs off the old hooks that spliced {path} into a shell string.
    /// </summary>
    private void ResyncInstalledCatalogHooks()
    {
        if (!Directory.Exists(_pluginsDir)) return;
        var byId = CuratedCatalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            string id = System.IO.Path.GetFileName(dir);
            if (!byId.TryGetValue(id, out var catalog) || catalog.Hooks.Count == 0) continue;
            try { WriteCatalogHookFiles(catalog, dir); }
            catch { /* skip a locked/unwritable plugin dir */ }
        }
    }

    /// <summary>The literal that catalog hook commands use for their own install folder.</summary>
    private const string PluginDirToken = "{plugin_dir}";

    /// <summary>
    /// Write a catalog plugin's hook scripts and hooks.json into <paramref name="pluginDir"/>. Each file is only
    /// rewritten when its content changed (this runs at every launch).
    /// </summary>
    internal static void WriteCatalogHookFiles(CatalogPlugin catalog, string pluginDir)
    {
        if (CatalogScripts.TryGetValue(catalog.Id, out var scripts))
        {
            foreach (var (fileName, content) in scripts)
                WriteIfChanged(System.IO.Path.Combine(pluginDir, fileName), HookScriptPrelude + content,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // PS 5.1 reads BOM-less files as ANSI
        }

        var hooksConfig = new Dictionary<string, object>();
        foreach (var phase in new[] { "PreToolUse", "PostToolUse" })
        {
            var hooks = catalog.Hooks.Where(h => h.Phase == phase).ToList();
            if (hooks.Count > 0)
                hooksConfig[phase] = hooks.Select(h => new
                {
                    matcher = h.Matcher,
                    command = h.Command.Replace(PluginDirToken, pluginDir.TrimEnd('\\', '/')),
                    timeout = h.TimeoutMs,
                }).ToList();
        }
        var hooksJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["hooks"] = hooksConfig }, JsonOptions);
        WriteIfChanged(System.IO.Path.Combine(pluginDir, "hooks.json"), hooksJson, new System.Text.UTF8Encoding(false));
    }

    private static void WriteIfChanged(string path, string content, System.Text.Encoding encoding)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content, encoding);
    }

    /// <summary>True when a catalog plugin actually does something once installed (it has hooks).</summary>
    public static bool IsImplemented(CatalogPlugin catalog) => catalog.Hooks.Count > 0;

    /// <summary>Scans subdirectories of the plugins folder for manifest.json files.</summary>
    public List<PluginInfo> ScanPlugins()
    {
        var plugins = new List<PluginInfo>();

        // Respect feature toggle — if PluginSystem is disabled, return empty
        if (!_settingsService.Settings.Features.PluginSystem)
            return plugins;

        var enabledNames = LoadEnabledPluginNames();

        if (!Directory.Exists(_pluginsDir))
            return plugins;

        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            var manifestPath = System.IO.Path.Combine(dir, "manifest.json");
            if (System.IO.Path.GetFileName(dir).StartsWith('.') || !File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<JsonElement>(json);

                var plugin = new PluginInfo
                {
                    Name = manifest.TryGetProperty("name", out var n) ? n.GetString() ?? "" : System.IO.Path.GetFileName(dir),
                    Description = manifest.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Version = manifest.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "0.0.0",
                    Path = dir,
                    Enabled = enabledNames.Contains(
                        manifest.TryGetProperty("name", out var nm) ? nm.GetString() ?? System.IO.Path.GetFileName(dir) : System.IO.Path.GetFileName(dir),
                        StringComparer.OrdinalIgnoreCase),
                };

                plugins.Add(plugin);
            }
            catch
            {
                // Skip plugins with invalid manifests
            }
        }

        return plugins;
    }

    /// <summary>Enables a plugin by name and persists the config.</summary>
    public void EnablePlugin(string name)
    {
        var enabled = LoadEnabledPluginNames();
        if (!enabled.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            enabled.Add(name);
            SaveEnabledPluginNames(enabled);
            PluginsChanged?.Invoke();
        }
    }

    /// <summary>Disables a plugin by name and persists the config.</summary>
    public void DisablePlugin(string name)
    {
        var enabled = LoadEnabledPluginNames();
        enabled.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        SaveEnabledPluginNames(enabled);
        PluginsChanged?.Invoke();
    }

    public enum InstallCheck { Ok, NotFound, NoManifest, InsidePluginsFolder, ContainsPluginsFolder, AlreadyInstalled }

    /// <summary>
    /// Can <paramref name="sourcePath"/> be installed? The "Add plugin" picker used to open IN the plugins
    /// folder, so picking an installed plugin made source == destination: the old code deleted the destination
    /// (= the source) and then crashed copying a folder that no longer existed. A source that CONTAINS the
    /// plugins folder (e.g. the data folder) would copy into itself forever.
    /// </summary>
    public InstallCheck CheckInstallSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath)) return InstallCheck.NotFound;
        string src = NormalizeDir(sourcePath);
        string plugins = NormalizeDir(_pluginsDir);
        if (src.StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) return InstallCheck.InsidePluginsFolder;
        if (plugins.StartsWith(src, StringComparison.OrdinalIgnoreCase)) return InstallCheck.ContainsPluginsFolder;
        if (!File.Exists(System.IO.Path.Combine(sourcePath, "manifest.json"))) return InstallCheck.NoManifest;
        if (Directory.Exists(InstallTargetFor(sourcePath))) return InstallCheck.AlreadyInstalled;
        return InstallCheck.Ok;
    }

    /// <summary>Where <see cref="InstallPlugin"/> would put a source folder.</summary>
    public string InstallTargetFor(string sourcePath) =>
        System.IO.Path.Combine(_pluginsDir, System.IO.Path.GetFileName(sourcePath.TrimEnd('\\', '/')));

    private static string NormalizeDir(string p) =>
        System.IO.Path.GetFullPath(p).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;

    /// <summary>
    /// Copy a plugin directory into the plugins folder. The copy goes to a temp folder first and is swapped in
    /// only when complete, so a failure half-way never leaves a half-copied plugin — or, when replacing, no
    /// plugin at all. Replacing an installed plugin requires <paramref name="overwrite"/> (ask the user first).
    /// </summary>
    /// <exception cref="InvalidOperationException">the source failed <see cref="CheckInstallSource"/>.</exception>
    public void InstallPlugin(string sourcePath, bool overwrite = false)
    {
        var check = CheckInstallSource(sourcePath);
        if (check == InstallCheck.AlreadyInstalled && !overwrite)
            throw new InvalidOperationException($"A plugin named '{System.IO.Path.GetFileName(sourcePath)}' is already installed.");
        if (check is not (InstallCheck.Ok or InstallCheck.AlreadyInstalled))
            throw new InvalidOperationException($"Cannot install '{sourcePath}': {check}.");

        string destPath = InstallTargetFor(sourcePath);
        // Staging lives NEXT TO the plugins folder, not in it: HookService loads hooks from every plugins\*
        // folder with a manifest, so a half-copied or left-over replaced copy inside it would run as a plugin.
        string staging = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_pluginsDir.TrimEnd('\\', '/'))!, ".plugins-staging");
        Directory.CreateDirectory(staging);
        string tempPath = System.IO.Path.Combine(staging, $"installing-{Guid.NewGuid():N}");
        string? oldPath = null;
        try
        {
            CopyDirectory(sourcePath, tempPath);
            if (Directory.Exists(destPath))
            {
                oldPath = System.IO.Path.Combine(staging, $"replaced-{Guid.NewGuid():N}");
                Directory.Move(destPath, oldPath);
            }
            Directory.Move(tempPath, destPath);
        }
        catch
        {
            try { if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true); } catch { }
            // Put the previous version back if the swap got half-way.
            try { if (oldPath != null && Directory.Exists(oldPath) && !Directory.Exists(destPath)) Directory.Move(oldPath, destPath); } catch { }
            throw;
        }
        if (oldPath != null)
            try { Directory.Delete(oldPath, true); } catch { /* left in the staging folder, outside plugins\ */ }
        PluginsChanged?.Invoke();
    }

    /// <summary>Returns a list of enabled plugin names.</summary>
    public List<string> GetEnabledPlugins()
    {
        return LoadEnabledPluginNames();
    }

    /// <summary>Returns the plugins directory path.</summary>
    public string PluginsDirectory => _pluginsDir;

    /// <summary>Removes a plugin directory entirely (callers confirm first — it holds any hook edits).</summary>
    public void UninstallPlugin(string name)
    {
        var plugins = ScanPlugins();
        var plugin = plugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (plugin != null && Directory.Exists(plugin.Path))
        {
            DisablePlugin(name);
            Directory.Delete(plugin.Path, true);
            PluginsChanged?.Invoke();
        }
    }

    /// <summary>Installs a catalog plugin by generating its manifest.json and hook files.</summary>
    /// <exception cref="InvalidOperationException">the plugin has no hooks yet (installing it would do nothing).</exception>
    public void InstallCatalogPlugin(CatalogPlugin catalog)
    {
        // 11 of the 20 catalog entries have no hooks: installing one wrote a manifest + README and the page
        // then showed it "installed and enabled" while it did nothing at all.
        if (!IsImplemented(catalog))
            throw new InvalidOperationException($"Catalog plugin '{catalog.Id}' has no hooks yet.");

        var pluginDir = System.IO.Path.Combine(_pluginsDir, catalog.Id);
        Directory.CreateDirectory(pluginDir);

        // Generate manifest.json
        var manifest = new Dictionary<string, object>
        {
            ["name"] = catalog.Name,
            ["description"] = catalog.Description,
            ["version"] = catalog.Version,
            ["author"] = catalog.Author,
            ["category"] = catalog.Category,
            ["tags"] = catalog.Tags,
            ["hooks"] = catalog.HookEvents,
            ["hookSummary"] = catalog.HookSummary,
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(System.IO.Path.Combine(pluginDir, "manifest.json"), json);

        // Generate a README
        var readme = $"# {catalog.Name}\n\n{catalog.Description}\n\n" +
                     $"## Version\n{catalog.Version}\n\n" +
                     $"## Author\n{catalog.Author}\n\n" +
                     $"## Hook Events\n{string.Join(", ", catalog.HookEvents)}\n\n" +
                     $"## What it does\n{catalog.HookSummary}\n";
        File.WriteAllText(System.IO.Path.Combine(pluginDir, "README.md"), readme);

        // Generate hooks.json (+ hook scripts) for HookService integration
        WriteCatalogHookFiles(catalog, pluginDir);

        // Auto-enable on install
        EnablePlugin(catalog.Name);
        PluginsChanged?.Invoke();
    }

    // ─── Curated Plugin Catalog ─────────────────────────────────────
    // Based on Claude Code CLI hook system: PreToolUse, PostToolUse,
    // SessionStart, SessionEnd, Notification, etc. (27 hook events)

    public static readonly List<CatalogPlugin> CuratedCatalog =
    [
        // ═══ Code Quality ═══
        new CatalogPlugin
        {
            Id = "auto-lint",
            Name = "Auto Lint & Format",
            NameTh = "จัดรูปแบบโค้ดอัตโนมัติ",
            Description = "Automatically runs linter/formatter (prettier, black, rustfmt) after AI writes or edits files. Catches style issues before commit.",
            DescriptionTh = "รัน linter/formatter (prettier, black, rustfmt) อัตโนมัติหลัง AI เขียนหรือแก้ไฟล์ จับปัญหาสไตล์ก่อน commit",
            Version = "1.2.0", Category = "code-quality", Icon = "\u2728",
            Author = "CluadeX Team", Tags = ["lint", "format", "prettier", "black"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After FileWrite/FileEdit, formats the file with the formatter already installed for its language: prettier (JS/TS/JSON/CSS/MD/HTML/YAML, the project's own copy — nothing is downloaded), black (Python), rustfmt (Rust).",
            HookSummaryTh = "หลังเขียน/แก้ไฟล์ จะจัดรูปแบบด้วย formatter ที่ติดตั้งไว้แล้วตามภาษา: prettier (JS/TS/JSON/CSS/MD/HTML/YAML ใช้ตัวในโปรเจกต์ ไม่ดาวน์โหลดเพิ่ม), black (Python), rustfmt (Rust)",
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = PsHook("lint.ps1"), TimeoutMs = 20000 },
                     new() { Phase = "PostToolUse", Matcher = "edit_file", Command = PsHook("lint.ps1"), TimeoutMs = 20000 }],
        },
        new CatalogPlugin
        {
            Id = "test-runner",
            Name = "Auto Test Runner",
            NameTh = "รันเทสต์อัตโนมัติ",
            Description = "Automatically runs relevant tests after code changes. Detects test framework (jest, pytest, cargo test, dotnet test) and runs affected tests.",
            DescriptionTh = "รันเทสต์ที่เกี่ยวข้องอัตโนมัติหลังแก้โค้ด ตรวจจับ test framework และรันเฉพาะเทสต์ที่ได้รับผลกระทบ",
            Version = "1.1.0", Category = "code-quality", Icon = "\U0001F9EA",
            Author = "CluadeX Team", Tags = ["test", "jest", "pytest", "ci"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After FileWrite/FileEdit on source files, finds and runs related test files. Reports pass/fail inline.",
            HookSummaryTh = "หลังแก้ไฟล์ซอร์ส จะค้นหาและรันไฟล์เทสต์ที่เกี่ยวข้อง รายงานผลลัพธ์ทันที",
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = "npm test 2>nul || dotnet test --no-build -q 2>nul || pytest -x -q 2>nul || echo no-tests", TimeoutMs = 30000 },
                     new() { Phase = "PostToolUse", Matcher = "edit_file", Command = "npm test 2>nul || dotnet test --no-build -q 2>nul || pytest -x -q 2>nul || echo no-tests", TimeoutMs = 30000 }],
        },
        new CatalogPlugin
        {
            Id = "type-checker",
            Name = "Type Check Guard",
            NameTh = "ตรวจสอบ Type อัตโนมัติ",
            Description = "Runs TypeScript tsc, mypy, or equivalent type checker after code changes to catch type errors early.",
            DescriptionTh = "รัน TypeScript tsc, mypy หรือ type checker หลังแก้โค้ด เพื่อจับ type error ตั้งแต่เนิ่นๆ",
            Version = "1.0.0", Category = "code-quality", Icon = "\U0001F50D",
            Author = "CluadeX Team", Tags = ["typescript", "mypy", "types"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After FileWrite/FileEdit on .ts/.tsx/.py files, runs type checker and reports errors.",
            HookSummaryTh = "หลังแก้ไฟล์ .ts/.tsx/.py จะรัน type checker และรายงาน error",
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = PsHook("typecheck.ps1"), TimeoutMs = 30000 },
                     new() { Phase = "PostToolUse", Matcher = "edit_file", Command = PsHook("typecheck.ps1"), TimeoutMs = 30000 }],
        },

        // ═══ Security ═══
        new CatalogPlugin
        {
            Id = "secret-scanner",
            Name = "Secret Scanner",
            NameTh = "ตรวจจับ Secret",
            Description = "Scans files before write for hardcoded secrets, API keys, passwords, and tokens. Blocks writes that contain sensitive data.",
            DescriptionTh = "สแกนไฟล์ก่อนเขียนเพื่อตรวจจับ secret, API key, รหัสผ่าน และ token ที่ฝังในโค้ด บล็อกการเขียนที่มีข้อมูลอ่อนไหว",
            Version = "1.3.0", Category = "security", Icon = "\U0001F575\uFE0F",
            Author = "CluadeX Team", Tags = ["security", "secrets", "api-keys", "scan"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before FileWrite/FileEdit, scans the NEW content (not the file already on disk) for AWS keys, GitHub/Slack/Google tokens, sk-… API keys, private keys and passwords in URLs. Blocks the write if one is found.",
            HookSummaryTh = "ก่อนเขียน/แก้ไฟล์ จะสแกนเนื้อหาใหม่ (ไม่ใช่ไฟล์เดิมบนดิสก์) หา AWS key, token ของ GitHub/Slack/Google, API key แบบ sk-…, private key และรหัสผ่านใน URL บล็อกการเขียนถ้าพบ",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = PsHook("secret-scan.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = PsHook("secret-scan.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "multi_edit", Command = PsHook("secret-scan.ps1") }],
        },
        new CatalogPlugin
        {
            Id = "dependency-audit",
            Name = "Dependency Auditor",
            NameTh = "ตรวจสอบ Dependencies",
            Description = "Audits package dependencies for known vulnerabilities after package.json, requirements.txt, or Cargo.toml changes.",
            DescriptionTh = "ตรวจสอบ dependencies ว่ามีช่องโหว่ที่รู้จักหรือไม่ หลังแก้ไข package.json, requirements.txt หรือ Cargo.toml",
            Version = "1.0.0", Category = "security", Icon = "\U0001F6E1\uFE0F",
            Author = "CluadeX Team", Tags = ["security", "audit", "npm", "pip", "cargo"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After changes to dependency files, runs npm audit / pip-audit / cargo audit and reports vulnerabilities.",
            HookSummaryTh = "หลังแก้ไฟล์ dependency จะรัน npm audit / pip-audit / cargo audit และรายงานช่องโหว่",
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = "npm audit --production 2>nul || pip-audit 2>nul || echo audit-done", TimeoutMs = 30000 }],
        },
        new CatalogPlugin
        {
            Id = "path-guard",
            Name = "Path Traversal Guard",
            NameTh = "ป้องกัน Path Traversal",
            Description = "Enhanced path validation that blocks file operations outside the project root. Prevents ../ attacks and symlink escapes.",
            DescriptionTh = "ตรวจสอบ path ขั้นสูง บล็อกการทำงานกับไฟล์นอกโปรเจกต์ ป้องกัน ../ attack และ symlink escape",
            Version = "1.1.0", Category = "security", Icon = "\U0001F512",
            Author = "CluadeX Team", Tags = ["security", "path", "traversal"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before FileRead/FileWrite/FileEdit, blocks any path that resolves outside the open project folder — ../ traversal, absolute paths elsewhere, and symlinks/junctions inside the project that point outside it.",
            HookSummaryTh = "ก่อนอ่าน/เขียน/แก้ไฟล์ จะบล็อก path ที่ชี้ออกนอกโฟลเดอร์โปรเจกต์ — ทั้ง ../ path แบบเต็มที่อยู่ที่อื่น และ symlink/junction ในโปรเจกต์ที่ชี้ออกไปข้างนอก",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = PsHook("path-guard.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = PsHook("path-guard.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "multi_edit", Command = PsHook("path-guard.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "read_file", Command = PsHook("path-guard.ps1") }],
        },

        // ═══ Git & Version Control ═══
        new CatalogPlugin
        {
            Id = "auto-commit",
            Name = "Smart Auto-Commit",
            NameTh = "Auto-Commit อัจฉริยะ",
            Description = "Automatically creates descriptive git commits after successful code changes. Groups related changes and generates meaningful commit messages.",
            DescriptionTh = "สร้าง git commit อัตโนมัติพร้อมข้อความอธิบายหลังแก้โค้ดสำเร็จ จัดกลุ่มการเปลี่ยนแปลงที่เกี่ยวข้อง",
            Version = "1.2.0", Category = "git", Icon = "\U0001F4E6",
            Author = "CluadeX Team", Tags = ["git", "commit", "auto"],
            HookEvents = ["PostToolUse", "SessionEnd"],
            HookSummary = "After successful file writes, stages and commits with AI-generated message. On SessionEnd, creates final summary commit if needed.",
            HookSummaryTh = "หลังเขียนไฟล์สำเร็จ จะ stage และ commit ด้วยข้อความจาก AI เมื่อจบเซสชันจะสร้าง summary commit",
        },
        new CatalogPlugin
        {
            Id = "branch-protector",
            Name = "Branch Protector",
            NameTh = "ป้องกัน Branch หลัก",
            Description = "Prevents direct commits to main/master/develop branches. Forces feature branch workflow.",
            DescriptionTh = "ป้องกัน commit ตรงไปยัง branch main/master/develop บังคับใช้ feature branch workflow",
            Version = "1.0.0", Category = "git", Icon = "\U0001F6A7",
            Author = "CluadeX Team", Tags = ["git", "branch", "protection"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before BashTool git commit/push, checks current branch. Blocks if on protected branch (main, master, develop).",
            HookSummaryTh = "ก่อน git commit/push จะตรวจ branch ปัจจุบัน บล็อกถ้าอยู่บน branch ที่ป้องกัน",
            // Runs via `cmd /c` (HookService), so use findstr — NOT batch `for /f %%b`, which is
            // .bat-only syntax and errors with "%%b was unexpected at this time" on the command line.
            // findstr /x /i matches the current branch exactly (case-insensitive) against the
            // protected set; a match blocks (exit 1) with a clear stderr message, else allows (exit 0).
            Hooks = [new() { Phase = "PreToolUse", Matcher = "git_commit", Command = "git branch --show-current | findstr /x /i \"main master develop\" >nul && (echo [branch-protector] Blocked: don't commit directly to a protected branch. Create a feature branch first. 1>&2 & exit 1) || exit 0" },
                     new() { Phase = "PreToolUse", Matcher = "git_push", Command = "git branch --show-current | findstr /x /i \"main master develop\" >nul && (echo [branch-protector] Blocked: don't push directly to a protected branch. Open a PR from a feature branch. 1>&2 & exit 1) || exit 0" }],
        },
        new CatalogPlugin
        {
            Id = "pr-template",
            Name = "PR Template Generator",
            NameTh = "สร้างเทมเพลต PR",
            Description = "Generates comprehensive PR descriptions with summary, test plan, and change log from git diff.",
            DescriptionTh = "สร้างคำอธิบาย PR ครบถ้วนพร้อมสรุป แผนทดสอบ และ changelog จาก git diff",
            Version = "1.0.0", Category = "git", Icon = "\U0001F4CB",
            Author = "CluadeX Team", Tags = ["git", "github", "pr", "template"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before gh pr create, generates structured PR description from diff analysis.",
            HookSummaryTh = "ก่อนสร้าง PR จะวิเคราะห์ diff และสร้างคำอธิบาย PR อัตโนมัติ",
        },

        // ═══ Productivity ═══
        new CatalogPlugin
        {
            Id = "session-logger",
            Name = "Session Logger",
            NameTh = "บันทึกเซสชัน",
            Description = "Logs all session activity (files changed, commands run, errors encountered) to a markdown file for review.",
            DescriptionTh = "บันทึกกิจกรรมเซสชันทั้งหมด (ไฟล์ที่แก้ คำสั่งที่รัน ข้อผิดพลาด) ลงไฟล์ markdown สำหรับตรวจสอบ",
            Version = "1.0.0", Category = "productivity", Icon = "\U0001F4DD",
            Author = "CluadeX Team", Tags = ["logging", "session", "audit"],
            HookEvents = ["SessionStart", "SessionEnd", "PostToolUse"],
            HookSummary = "On SessionStart, creates log file. Logs each tool use. On SessionEnd, writes summary with stats.",
            HookSummaryTh = "เมื่อเริ่มเซสชันจะสร้างไฟล์ log บันทึกการใช้เครื่องมือทุกครั้ง เมื่อจบจะเขียนสรุปพร้อมสถิติ",
        },
        new CatalogPlugin
        {
            Id = "todo-tracker",
            Name = "TODO Tracker",
            NameTh = "ติดตาม TODO",
            Description = "Automatically detects and tracks TODO/FIXME/HACK comments in code. Reports new TODOs added during session.",
            DescriptionTh = "ตรวจจับและติดตาม TODO/FIXME/HACK ในโค้ดอัตโนมัติ รายงาน TODO ใหม่ที่เพิ่มระหว่างเซสชัน",
            Version = "1.0.0", Category = "productivity", Icon = "\u2705",
            Author = "CluadeX Team", Tags = ["todo", "tracking", "code-quality"],
            HookEvents = ["PostToolUse", "SessionEnd"],
            HookSummary = "After FileWrite, scans for new TODO/FIXME/HACK comments. On SessionEnd, reports all new items found.",
            HookSummaryTh = "หลังเขียนไฟล์ จะสแกนหา TODO/FIXME/HACK ใหม่ เมื่อจบเซสชันจะรายงานรายการทั้งหมด",
        },
        new CatalogPlugin
        {
            Id = "context-compactor",
            Name = "Context Compactor",
            NameTh = "บีบอัด Context",
            Description = "Intelligently compacts conversation context when approaching token limits. Preserves important code and decisions.",
            DescriptionTh = "บีบอัดบริบทการสนทนาอัจฉริยะเมื่อใกล้ถึงขีดจำกัด token เก็บโค้ดและการตัดสินใจสำคัญ",
            Version = "1.0.0", Category = "productivity", Icon = "\U0001F4E6",
            Author = "CluadeX Team", Tags = ["context", "memory", "optimization"],
            HookEvents = ["PreCompact", "PostCompact"],
            HookSummary = "On PreCompact, identifies critical code blocks and decisions to preserve. Summarizes non-essential messages.",
            HookSummaryTh = "ก่อนบีบอัด จะระบุโค้ดและการตัดสินใจสำคัญที่ต้องเก็บ สรุปข้อความที่ไม่จำเป็น",
        },

        // ═══ Documentation ═══
        new CatalogPlugin
        {
            Id = "doc-generator",
            Name = "Auto Documentation",
            NameTh = "สร้างเอกสารอัตโนมัติ",
            Description = "Generates JSDoc/docstrings/XML comments for functions after they're written. Supports TypeScript, Python, C#, Rust.",
            DescriptionTh = "สร้าง JSDoc/docstring/XML comment สำหรับฟังก์ชันหลังเขียนเสร็จ รองรับ TypeScript, Python, C#, Rust",
            Version = "1.1.0", Category = "docs", Icon = "\U0001F4DA",
            Author = "CluadeX Team", Tags = ["docs", "jsdoc", "docstring", "comments"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After FileWrite/FileEdit, detects new/modified functions and adds documentation comments if missing.",
            HookSummaryTh = "หลังเขียน/แก้ไฟล์ จะตรวจจับฟังก์ชันใหม่/ที่แก้ไข และเพิ่ม documentation comment ถ้ายังไม่มี",
        },
        new CatalogPlugin
        {
            Id = "changelog-writer",
            Name = "Changelog Writer",
            NameTh = "เขียน Changelog",
            Description = "Maintains a CHANGELOG.md file, automatically adding entries for significant code changes following Keep a Changelog format.",
            DescriptionTh = "ดูแลไฟล์ CHANGELOG.md เพิ่มรายการอัตโนมัติสำหรับการเปลี่ยนแปลงสำคัญตามรูปแบบ Keep a Changelog",
            Version = "1.0.0", Category = "docs", Icon = "\U0001F4C4",
            Author = "CluadeX Team", Tags = ["changelog", "docs", "versioning"],
            HookEvents = ["SessionEnd"],
            HookSummary = "On SessionEnd, reviews all file changes and appends categorized entries (Added/Changed/Fixed/Removed) to CHANGELOG.md.",
            HookSummaryTh = "เมื่อจบเซสชัน จะตรวจสอบการเปลี่ยนแปลงทั้งหมดและเพิ่มรายการหมวดหมู่ลง CHANGELOG.md",
        },

        // ═══ Notification & Integration ═══
        new CatalogPlugin
        {
            Id = "desktop-notify",
            Name = "Desktop Notifications",
            NameTh = "แจ้งเตือนเดสก์ท็อป",
            Description = "Sends Windows desktop notifications for important events: task completion, errors, long-running operation finished.",
            DescriptionTh = "ส่งแจ้งเตือน Windows สำหรับเหตุการณ์สำคัญ: งานเสร็จ เกิดข้อผิดพลาด การทำงานยาวนานเสร็จสิ้น",
            Version = "1.0.0", Category = "integration", Icon = "\U0001F514",
            Author = "CluadeX Team", Tags = ["notification", "windows", "alert"],
            HookEvents = ["Notification", "PostToolUseFailure", "SessionEnd"],
            HookSummary = "On errors or session end, shows Windows toast notification. Useful when CluadeX runs in background.",
            HookSummaryTh = "เมื่อเกิดข้อผิดพลาดหรือจบเซสชัน จะแสดง Windows toast notification มีประโยชน์เมื่อ CluadeX ทำงานเบื้องหลัง",
        },
        new CatalogPlugin
        {
            Id = "cost-tracker",
            Name = "API Cost Tracker",
            NameTh = "ติดตามค่าใช้จ่าย API",
            Description = "Tracks estimated API costs per session based on token usage. Shows running total and per-message cost breakdown.",
            DescriptionTh = "ติดตามค่าใช้จ่าย API โดยประมาณต่อเซสชันจากการใช้ token แสดงยอดรวมและต้นทุนต่อข้อความ",
            Version = "1.0.0", Category = "integration", Icon = "\U0001F4B0",
            Author = "CluadeX Team", Tags = ["cost", "api", "tokens", "billing"],
            HookEvents = ["PostToolUse", "SessionEnd"],
            HookSummary = "After each API call, estimates cost based on model pricing. On SessionEnd, writes cost summary.",
            HookSummaryTh = "หลังเรียก API แต่ละครั้ง จะประเมินค่าใช้จ่ายตามราคาโมเดล เมื่อจบเซสชันจะเขียนสรุปค่าใช้จ่าย",
        },

        // ═══ Code Safety ═══
        new CatalogPlugin
        {
            Id = "backup-before-edit",
            Name = "Backup Before Edit",
            NameTh = "สำรองข้อมูลก่อนแก้",
            Description = "Creates automatic backup copies of files before AI edits them. Stored in .cluadex-backups/ with timestamps.",
            DescriptionTh = "สร้างสำเนาสำรองอัตโนมัติก่อน AI แก้ไขไฟล์ เก็บใน .cluadex-backups/ พร้อม timestamp",
            Version = "1.0.0", Category = "safety", Icon = "\U0001F4BE",
            Author = "CluadeX Team", Tags = ["backup", "safety", "recovery"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before FileWrite/FileEdit, copies the original file to .cluadex-backups/{yyyyMMdd-HHmmss}/{path} in the project. Backup folders older than 7 days are removed automatically.",
            HookSummaryTh = "ก่อนเขียน/แก้ไฟล์ จะคัดลอกไฟล์ต้นฉบับไปที่ .cluadex-backups/{วันเวลา}/{path} ในโปรเจกต์ และลบโฟลเดอร์สำรองที่เก่ากว่า 7 วันให้อัตโนมัติ",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = PsHook("backup.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = PsHook("backup.ps1") },
                     new() { Phase = "PreToolUse", Matcher = "multi_edit", Command = PsHook("backup.ps1") }],
        },
        new CatalogPlugin
        {
            Id = "size-guard",
            Name = "File Size Guard",
            NameTh = "จำกัดขนาดไฟล์",
            Description = "Prevents AI from writing excessively large files or making changes that would bloat file sizes beyond configurable limits.",
            DescriptionTh = "ป้องกัน AI เขียนไฟล์ใหญ่เกินไปหรือแก้ไขที่ทำให้ไฟล์บวมเกินขีดจำกัดที่ตั้งค่าได้",
            Version = "1.0.0", Category = "safety", Icon = "\U0001F4CF",
            Author = "CluadeX Team", Tags = ["safety", "size", "limit"],
            HookEvents = ["PreToolUse"],
            HookSummary = "Before FileWrite, checks the size of the NEW content and blocks it above 500 KB (set the CLUADEX_SIZE_GUARD_KB environment variable to change the limit).",
            HookSummaryTh = "ก่อนเขียนไฟล์ จะตรวจขนาดของเนื้อหาใหม่ และบล็อกถ้าเกิน 500 KB (ตั้งตัวแปรสภาพแวดล้อม CLUADEX_SIZE_GUARD_KB เพื่อเปลี่ยนขีดจำกัด)",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = PsHook("size-guard.ps1") }],
        },

        // ═══ AI Enhancement ═══
        new CatalogPlugin
        {
            Id = "prompt-enhancer",
            Name = "Prompt Enhancer",
            NameTh = "ปรับปรุง Prompt",
            Description = "Automatically enhances user prompts with project context, relevant file references, and coding standards before sending to AI.",
            DescriptionTh = "ปรับปรุง prompt อัตโนมัติด้วยบริบทโปรเจกต์ การอ้างอิงไฟล์ และมาตรฐานการเขียนโค้ดก่อนส่งให้ AI",
            Version = "1.0.0", Category = "ai", Icon = "\U0001F680",
            Author = "CluadeX Team", Tags = ["prompt", "context", "enhancement"],
            HookEvents = ["UserPromptSubmit"],
            HookSummary = "On UserPromptSubmit, analyzes the prompt and injects relevant project context (package.json, tsconfig, file structure).",
            HookSummaryTh = "เมื่อส่ง prompt จะวิเคราะห์และเพิ่มบริบทโปรเจกต์ที่เกี่ยวข้อง (package.json, tsconfig, โครงสร้างไฟล์)",
        },
        new CatalogPlugin
        {
            Id = "code-reviewer",
            Name = "AI Code Reviewer",
            NameTh = "รีวิวโค้ดด้วย AI",
            Description = "Reviews AI-generated code for common issues: unused imports, missing error handling, potential null refs, performance pitfalls.",
            DescriptionTh = "รีวิวโค้ดที่ AI สร้างเพื่อหาปัญหาทั่วไป: import ไม่ใช้, ไม่จัดการ error, null ref, ปัญหาประสิทธิภาพ",
            Version = "1.0.0", Category = "ai", Icon = "\U0001F9D0",
            Author = "CluadeX Team", Tags = ["review", "code-quality", "ai"],
            HookEvents = ["PostToolUse"],
            HookSummary = "After FileWrite/FileEdit, performs a quick AI review of the changes looking for common issues and anti-patterns.",
            HookSummaryTh = "หลังเขียน/แก้ไฟล์ จะรีวิวการเปลี่ยนแปลงด้วย AI เพื่อหาปัญหาและ anti-pattern ทั่วไป",
        },
    ];

    // ─── Catalog hook scripts ───────────────────────────────────────
    // The old catalog hooks spliced {path} into cmd / PowerShell strings. HookService's escaping is for cmd
    // only, so size-guard's  Get-Item '{path}'  ran code for a file named  a'+(Start-Process calc)+'.txt .
    // Every hook that needs the path or the new content is now a script that reads them from the
    // environment HookService sets (CLUADEX_HOOK_PATH / CLUADEX_HOOK_CWD / CLUADEX_HOOK_PAYLOAD_FILE), and
    // the hook command line contains nothing that came from the model.

    private static string PsHook(string script) =>
        $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{PluginDirToken}\\{script}\"";

    /// <summary>Shared helpers put in front of every catalog script (ASCII only — Windows PowerShell 5.1).</summary>
    public const string HookScriptPrelude = """
        # Generated by CluadeX for a catalog plugin. It is rewritten at every launch - edits here are not kept.
        # Input comes from the environment CluadeX sets for each hook (CLUADEX_HOOK_TOOL, CLUADEX_HOOK_PATH,
        # CLUADEX_HOOK_CWD, CLUADEX_HOOK_PAYLOAD_FILE). Nothing is spliced into a command line, so a file named
        # like  a'+(Start-Process calc)+'.txt  is only an odd file name here, never code.
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'

        function Get-HookCwd {
            $cwd = $env:CLUADEX_HOOK_CWD
            if ([string]::IsNullOrWhiteSpace($cwd)) { $cwd = (Get-Location).ProviderPath }
            return [IO.Path]::GetFullPath($cwd)
        }

        function Resolve-HookPath {
            $p = $env:CLUADEX_HOOK_PATH
            if ([string]::IsNullOrWhiteSpace($p)) { return $null }
            try { return [IO.Path]::GetFullPath([IO.Path]::Combine((Get-HookCwd), $p)) } catch { return $null }
        }

        function Read-HookArgs {
            $f = $env:CLUADEX_HOOK_PAYLOAD_FILE
            if ([string]::IsNullOrWhiteSpace($f) -or -not [IO.File]::Exists($f)) { return $null }
            try { $j = [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8) | ConvertFrom-Json } catch { return $null }
            if ($null -ne $j -and $j.PSObject.Properties['arguments']) { $j = $j.arguments }
            if ($j -is [string]) { try { $j = $j | ConvertFrom-Json } catch { return $null } }
            return $j
        }

        function Write-HookBlock([string]$message) { [Console]::Error.WriteLine($message) }

        # Characters cmd.exe re-reads when Windows hands arguments to a .cmd shim (npx, prettier.cmd, tsc.cmd).
        $CmdMeta = '[&|<>^%!()"]'

        """;

    private const string SecretScanScript = """
        $payloadFile = $env:CLUADEX_HOOK_PAYLOAD_FILE
        if ([string]::IsNullOrWhiteSpace($payloadFile) -or -not [IO.File]::Exists($payloadFile)) { exit 0 }

        $texts = New-Object 'System.Collections.Generic.List[string]'
        function Add-Text($node, [string]$key) {
            if ($null -eq $node) { return }
            if ($node -is [string]) {
                # The path and the text being REPLACED (edit_file "find", old_*) are not new content -
                # removing a leaked key must not be blocked.
                if ($key -match '^(path|file_path|find|search|old_\w*)$') { return }
                if ($key -eq 'edits' -and $node.TrimStart().StartsWith('[')) {
                    try { Add-Text ($node | ConvertFrom-Json) $key; return } catch { }
                }
                $texts.Add($node)
                return
            }
            if ($node -is [System.Management.Automation.PSCustomObject]) {
                foreach ($prop in $node.PSObject.Properties) { Add-Text $prop.Value $prop.Name }
                return
            }
            if ($node -is [System.Collections.IEnumerable]) { foreach ($item in $node) { Add-Text $item $key } }
        }

        $a = Read-HookArgs
        if ($null -ne $a) { Add-Text $a '' }
        else { $texts.Add([IO.File]::ReadAllText($payloadFile, [Text.Encoding]::UTF8)) }   # unparseable: scan it raw

        # Real key shapes only. The old check was findstr "sk-", which matched task-, disk-, risk-... in any file.
        $checks = [ordered]@{
            'an AWS access key'       = '(?<![A-Z0-9])AKIA[0-9A-Z]{16}(?![A-Z0-9])'
            'a GitHub token'          = '(?<![A-Za-z0-9_])(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})'
            'a private key'           = '-----BEGIN (RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY( BLOCK)?-----'
            'an API key (sk-...)'     = '(?<![A-Za-z0-9_-])sk-(ant-|proj-|live-|test-)?[A-Za-z0-9_-]{20,}'
            'a Slack token'           = '(?<![A-Za-z0-9])xox[abposr]-[A-Za-z0-9-]{10,}'
            'a Google API key'        = '(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{35}'
            'a password inside a URL' = '(?i)\b[a-z][a-z0-9+.-]{1,15}://[^\s:/@''"]{1,64}:(?![$<{*%])[^\s:/@''"]{8,}@'
        }
        foreach ($t in $texts) {
            foreach ($c in $checks.GetEnumerator()) {
                if ([regex]::IsMatch($t, $c.Value)) {
                    Write-HookBlock ("[secret-scanner] Blocked: the new content contains what looks like {0}. Keep secrets in an environment variable or an untracked .env file, not in source." -f $c.Key)
                    exit 1
                }
            }
        }
        exit 0
        """;

    private const string SizeGuardScript = """
        $limit = 500 * 1024
        $kb = 0
        if ([int]::TryParse([string]$env:CLUADEX_SIZE_GUARD_KB, [ref]$kb) -and $kb -gt 0) { $limit = $kb * 1024 }

        $payloadFile = $env:CLUADEX_HOOK_PAYLOAD_FILE
        if ([string]::IsNullOrWhiteSpace($payloadFile) -or -not [IO.File]::Exists($payloadFile)) { exit 0 }

        # Measure the NEW content. The old guard measured the file already on disk, i.e. the version being replaced.
        $bytes = -1
        $a = Read-HookArgs
        if ($null -ne $a) {
            foreach ($k in 'content', 'file_text', 'text') {
                if ($a.PSObject.Properties[$k] -and $null -ne $a.$k) { $bytes = [Text.Encoding]::UTF8.GetByteCount([string]$a.$k); break }
            }
        } else {
            # Too big for ConvertFrom-Json: the payload's own size (content + a little JSON) is close enough.
            $bytes = ([IO.FileInfo]$payloadFile).Length
        }
        if ($bytes -gt $limit) {
            Write-HookBlock ("[size-guard] Blocked: the new file would be {0} KB; the limit is {1} KB. Split it into smaller files, or raise CLUADEX_SIZE_GUARD_KB." -f [math]::Ceiling($bytes / 1024), [math]::Floor($limit / 1024))
            exit 1
        }
        exit 0
        """;

    private const string BackupScript = """
        try {
            $full = Resolve-HookPath
            if ($null -eq $full -or -not [IO.File]::Exists($full)) { exit 0 }
            $cwd = (Get-HookCwd).TrimEnd('\') + '\'
            $root = [IO.Path]::Combine($cwd, '.cluadex-backups')
            if ($full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 0 }

            $rel = if ($full.StartsWith($cwd, [StringComparison]::OrdinalIgnoreCase)) { $full.Substring($cwd.Length) } else { [IO.Path]::GetFileName($full) }
            # Invariant culture: under a Thai UI culture the year would otherwise be the Buddhist-era 25xx.
            $inv = [Globalization.CultureInfo]::InvariantCulture
            $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss', $inv)
            $dest = [IO.Path]::Combine([IO.Path]::Combine($root, $stamp), $rel)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
            [IO.File]::Copy($full, $dest, $true)

            # Keep 7 days. Only folders whose NAME is a backup stamp are ever removed.
            $cutoff = (Get-Date).AddDays(-7)
            foreach ($d in [IO.Directory]::GetDirectories($root)) {
                $name = [IO.Path]::GetFileName($d)
                $when = [datetime]::MinValue
                if ([datetime]::TryParseExact($name, 'yyyyMMdd-HHmmss', $inv, [Globalization.DateTimeStyles]::None, [ref]$when) -and $when -lt $cutoff) {
                    [IO.Directory]::Delete($d, $true)
                }
            }
        } catch {
            # A failed backup must never block the edit itself.
        }
        exit 0
        """;

    private const string PathGuardScript = """
        $p = $env:CLUADEX_HOOK_PATH
        if ([string]::IsNullOrWhiteSpace($p)) { exit 0 }
        if ([string]::IsNullOrWhiteSpace($env:CLUADEX_HOOK_CWD)) { exit 0 }   # no project folder to guard
        $root = (Get-HookCwd).TrimEnd('\') + '\'
        $full = Resolve-HookPath
        if ($null -eq $full) { Write-HookBlock '[path-guard] Blocked: that is not a valid file path.'; exit 1 }
        if (-not ($full.TrimEnd('\') + '\').StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            Write-HookBlock ("[path-guard] Blocked: {0} is outside the project folder {1}." -f $full, $root.TrimEnd('\'))
            exit 1
        }
        # A symlink or junction inside the project can still lead outside it.
        try {
            $cur = $full
            while ($null -ne $cur -and $cur.Length -gt $root.Length) {
                if ([IO.File]::Exists($cur) -or [IO.Directory]::Exists($cur)) {
                    $item = Get-Item -LiteralPath $cur -Force -ErrorAction SilentlyContinue
                    if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        $target = @($item.Target) | Select-Object -First 1
                        if ($target) {
                            $t = [IO.Path]::GetFullPath([IO.Path]::Combine([IO.Path]::GetDirectoryName($cur), ([string]$target).Replace('\??\', '')))
                            if (-not ($t.TrimEnd('\') + '\').StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                                Write-HookBlock ("[path-guard] Blocked: {0} goes through a link that points outside the project ({1})." -f $full, $t)
                                exit 1
                            }
                        }
                    }
                }
                $cur = [IO.Path]::GetDirectoryName($cur)
            }
        } catch { }
        exit 0
        """;

    private const string LintScript = """
        $ErrorActionPreference = 'Continue'   # native tools write to stderr; under 'Stop' PS 5.1 would abort on it
        $full = Resolve-HookPath
        if ($null -eq $full -or -not [IO.File]::Exists($full)) { exit 0 }
        $cwd = Get-HookCwd
        $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
        try {
            if ($ext -in '.js', '.jsx', '.mjs', '.cjs', '.ts', '.tsx', '.json', '.css', '.scss', '.less', '.md', '.html', '.vue', '.yaml', '.yml') {
                # prettier.cmd is a batch shim and Windows re-reads a batch file's arguments through cmd.exe, so a
                # name containing & | < > ^ % ! ( ) " could run a command there: such files are skipped.
                if ($full -match $CmdMeta -or $cwd -match $CmdMeta) { exit 0 }
                $prettier = [IO.Path]::Combine($cwd, 'node_modules', '.bin', 'prettier.cmd')
                if ([IO.File]::Exists($prettier)) { & $prettier --write "$full" 2>&1 | Out-Null }
            } elseif ($ext -eq '.py') {
                if (Get-Command black -ErrorAction SilentlyContinue) { & black -q -- "$full" 2>&1 | Out-Null }
            } elseif ($ext -eq '.rs') {
                if (Get-Command rustfmt -ErrorAction SilentlyContinue) { & rustfmt -- "$full" 2>&1 | Out-Null }
            }
        } catch { }
        exit 0
        """;

    private const string TypeCheckScript = """
        $ErrorActionPreference = 'Continue'   # native tools write to stderr; under 'Stop' PS 5.1 would abort on it
        $full = Resolve-HookPath
        if ($null -eq $full -or -not [IO.File]::Exists($full)) { exit 0 }
        $cwd = Get-HookCwd
        $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
        try {
            if ($ext -in '.ts', '.tsx', '.mts', '.cts') {
                if ($cwd -match $CmdMeta) { exit 0 }   # tsc.cmd is a batch shim - see auto-lint
                $tsc = [IO.Path]::Combine($cwd, 'node_modules', '.bin', 'tsc.cmd')
                if ([IO.File]::Exists($tsc) -and [IO.File]::Exists([IO.Path]::Combine($cwd, 'tsconfig.json'))) {
                    & $tsc --noEmit 2>&1 | Select-Object -First 40 | ForEach-Object { "$_" }
                }
            } elseif ($ext -eq '.py') {
                if (Get-Command mypy -ErrorAction SilentlyContinue) {
                    & mypy -- "$full" 2>&1 | Select-Object -First 40 | ForEach-Object { "$_" }
                }
            }
        } catch { }
        exit 0
        """;

    /// <summary>Script files each catalog plugin installs next to its hooks.json (id → file → body).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> CatalogScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto-lint"] = new() { ["lint.ps1"] = LintScript },
        ["type-checker"] = new() { ["typecheck.ps1"] = TypeCheckScript },
        ["secret-scanner"] = new() { ["secret-scan.ps1"] = SecretScanScript },
        ["path-guard"] = new() { ["path-guard.ps1"] = PathGuardScript },
        ["backup-before-edit"] = new() { ["backup.ps1"] = BackupScript },
        ["size-guard"] = new() { ["size-guard.ps1"] = SizeGuardScript },
    };

    private List<string> LoadEnabledPluginNames()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
        }
        catch
        {
            // Corrupted config, start fresh
        }

        return new List<string>();
    }

    private void SaveEnabledPluginNames(List<string> names)
    {
        try
        {
            Directory.CreateDirectory(_pluginsDir);
            var json = JsonSerializer.Serialize(names, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save plugin config: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }
}
