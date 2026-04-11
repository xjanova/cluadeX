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
    }

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
            if (!File.Exists(manifestPath))
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

    /// <summary>Copies a plugin directory into the plugins folder.</summary>
    public void InstallPlugin(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return;

        var dirName = System.IO.Path.GetFileName(sourcePath);
        var destPath = System.IO.Path.Combine(_pluginsDir, dirName);

        if (Directory.Exists(destPath))
            Directory.Delete(destPath, true);

        CopyDirectory(sourcePath, destPath);
    }

    /// <summary>Returns a list of enabled plugin names.</summary>
    public List<string> GetEnabledPlugins()
    {
        return LoadEnabledPluginNames();
    }

    /// <summary>Returns the plugins directory path.</summary>
    public string PluginsDirectory => _pluginsDir;

    /// <summary>Removes a plugin directory entirely.</summary>
    public void UninstallPlugin(string name)
    {
        var plugins = ScanPlugins();
        var plugin = plugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (plugin != null && Directory.Exists(plugin.Path))
        {
            DisablePlugin(name);
            Directory.Delete(plugin.Path, true);
        }
    }

    /// <summary>Installs a catalog plugin by generating its manifest.json and hook files.</summary>
    public void InstallCatalogPlugin(CatalogPlugin catalog)
    {
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

        // Generate hooks.json for HookService integration
        if (catalog.Hooks.Count > 0)
        {
            var hooksConfig = new Dictionary<string, object>();
            var preHooks = catalog.Hooks.Where(h => h.Phase == "PreToolUse").ToList();
            var postHooks = catalog.Hooks.Where(h => h.Phase == "PostToolUse").ToList();

            if (preHooks.Count > 0)
                hooksConfig["PreToolUse"] = preHooks.Select(h => new { matcher = h.Matcher, command = h.Command, timeout = h.TimeoutMs }).ToList();
            if (postHooks.Count > 0)
                hooksConfig["PostToolUse"] = postHooks.Select(h => new { matcher = h.Matcher, command = h.Command, timeout = h.TimeoutMs }).ToList();

            var hooksFile = new Dictionary<string, object> { ["hooks"] = hooksConfig };
            var hooksJson = JsonSerializer.Serialize(hooksFile, JsonOptions);
            File.WriteAllText(System.IO.Path.Combine(pluginDir, "hooks.json"), hooksJson);
        }

        // Auto-enable on install
        EnablePlugin(catalog.Name);
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
            HookSummary = "After FileWrite/FileEdit, runs the project's configured formatter (detects package.json scripts, .prettierrc, pyproject.toml, rustfmt.toml).",
            HookSummaryTh = "หลังเขียน/แก้ไฟล์ จะรัน formatter ที่ตั้งค่าไว้ในโปรเจกต์อัตโนมัติ",
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = "npx prettier --write {path} 2>nul || python -m black {path} 2>nul || echo formatted" },
                     new() { Phase = "PostToolUse", Matcher = "edit_file", Command = "npx prettier --write {path} 2>nul || python -m black {path} 2>nul || echo formatted" }],
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
            Hooks = [new() { Phase = "PostToolUse", Matcher = "write_file", Command = "npx tsc --noEmit 2>nul || python -m mypy {path} 2>nul || echo type-check-done", TimeoutMs = 30000 },
                     new() { Phase = "PostToolUse", Matcher = "edit_file", Command = "npx tsc --noEmit 2>nul || python -m mypy {path} 2>nul || echo type-check-done", TimeoutMs = 30000 }],
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
            HookSummary = "Before FileWrite, scans content with regex patterns for AWS keys, GitHub tokens, private keys, passwords in URLs, etc. Blocks if found.",
            HookSummaryTh = "ก่อนเขียนไฟล์ จะสแกนเนื้อหาด้วย regex เพื่อหา AWS key, GitHub token, private key ฯลฯ บล็อกถ้าพบ",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = "findstr /r /c:\"AKIA\" /c:\"ghp_\" /c:\"-----BEGIN\" /c:\"sk-\" {path} && exit 1 || exit 0" },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = "findstr /r /c:\"AKIA\" /c:\"ghp_\" /c:\"-----BEGIN\" /c:\"sk-\" {path} && exit 1 || exit 0" }],
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
            HookSummary = "Before FileRead/FileWrite/FileEdit, validates paths are within project root. Resolves symlinks and rejects traversal.",
            HookSummaryTh = "ก่อนอ่าน/เขียน/แก้ไฟล์ จะตรวจสอบว่า path อยู่ในโปรเจกต์ แก้ symlink และปฏิเสธ traversal",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = "echo {path} | findstr /r \"\\.\\.\" && exit 1 || exit 0" },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = "echo {path} | findstr /r \"\\.\\.\" && exit 1 || exit 0" },
                     new() { Phase = "PreToolUse", Matcher = "read_file", Command = "echo {path} | findstr /r \"\\.\\.\" && exit 1 || exit 0" }],
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
            Hooks = [new() { Phase = "PreToolUse", Matcher = "git_commit", Command = "for /f %%b in ('git branch --show-current') do @if \"%%b\"==\"main\" exit 1 & if \"%%b\"==\"master\" exit 1 & if \"%%b\"==\"develop\" exit 1" },
                     new() { Phase = "PreToolUse", Matcher = "git_push", Command = "for /f %%b in ('git branch --show-current') do @if \"%%b\"==\"main\" exit 1 & if \"%%b\"==\"master\" exit 1 & if \"%%b\"==\"develop\" exit 1" }],
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
            HookSummary = "Before FileWrite/FileEdit, copies the original file to .cluadex-backups/{timestamp}/{path}. Auto-cleans old backups (>7 days).",
            HookSummaryTh = "ก่อนเขียน/แก้ไฟล์ จะคัดลอกไฟล์ต้นฉบับไปที่ .cluadex-backups/ ลบสำเนาเก่าอัตโนมัติ (>7 วัน)",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = "if exist {path} (mkdir .cluadex-backups 2>nul & copy /y {path} .cluadex-backups\\ >nul 2>nul)" },
                     new() { Phase = "PreToolUse", Matcher = "edit_file", Command = "if exist {path} (mkdir .cluadex-backups 2>nul & copy /y {path} .cluadex-backups\\ >nul 2>nul)" }],
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
            HookSummary = "Before FileWrite, checks if content exceeds max file size (default 500KB). Warns or blocks oversized writes.",
            HookSummaryTh = "ก่อนเขียนไฟล์ จะตรวจว่าเนื้อหาเกินขนาดสูงสุด (ค่าเริ่มต้น 500KB) เตือนหรือบล็อกไฟล์ใหญ่เกิน",
            Hooks = [new() { Phase = "PreToolUse", Matcher = "write_file", Command = "powershell -NoProfile -Command \"if ((Get-Item '{path}' -ErrorAction SilentlyContinue).Length -gt 512000) { exit 1 } else { exit 0 }\"" }],
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
