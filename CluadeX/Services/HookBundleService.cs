using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Manages the Hook Script Library (Sprint 3 #2).
///
/// On first run:
///   1. Copies bundled .ps1 scripts from the app's "Resources/HooksBundled" content
///      directory into ~/.cluadex/hooks-bundled/ (only if missing or older).
///   2. Loads/creates ~/.cluadex/hooks-bundle-state.json which tracks which hooks
///      the user has enabled.
///   3. Projects the enabled set into ~/.cluadex/hooks-bundled.json — the same
///      schema HookService.LoadHooks() already understands.
///
/// Toggling a hook calls SetEnabled(id, bool) which rewrites both state + the
/// projected hooks-bundled.json, then asks HookService to reload its cache.
/// </summary>
public class HookBundleService
{
    private readonly HookService _hookService;
    private readonly string _bundleDir;          // ~/.cluadex/hooks-bundled/
    private readonly string _stateFile;          // ~/.cluadex/hooks-bundle-state.json
    private readonly string _projectedConfig;    // ~/.cluadex/hooks-bundled.json
    private readonly object _lock = new();

    private HookBundleState _state = new();
    private List<HookBundle> _catalog;

    public event Action? Changed;

    public HookBundleService(HookService hookService)
    {
        _hookService = hookService;
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex");
        Directory.CreateDirectory(root);
        _bundleDir = Path.Combine(root, "hooks-bundled");
        _stateFile = Path.Combine(root, "hooks-bundle-state.json");
        _projectedConfig = Path.Combine(root, "hooks-bundled.json");
        Directory.CreateDirectory(_bundleDir);

        _catalog = BuildCatalog();
        LoadState();
        DeployBundledScripts();
        ProjectEnabledToConfig();
    }

    public IReadOnlyList<HookBundle> GetCatalog()
    {
        lock (_lock)
        {
            // Hydrate IsEnabled from state for the UI
            foreach (var b in _catalog)
                b.IsEnabled = _state.EnabledById.TryGetValue(b.Id, out var v) ? v : b.DefaultEnabled;
            return _catalog.ToList();
        }
    }

    public string BundleDir => _bundleDir;

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var hit = _catalog.FirstOrDefault(b => b.Id == id);
            if (hit == null) return;
            _state.EnabledById[id] = enabled;
            _state.LastModified = DateTime.UtcNow;
            SaveState();
            ProjectEnabledToConfig();
        }
        _hookService.ReloadHooks();
        Changed?.Invoke();
    }

    /// <summary>Read the deployed script's content for the preview panel.</summary>
    public string ReadScript(HookBundle bundle)
    {
        try
        {
            string path = Path.Combine(_bundleDir, bundle.ScriptFile);
            return File.Exists(path) ? File.ReadAllText(path) : "(not deployed yet)";
        }
        catch (Exception ex) { return $"(error reading: {ex.Message})"; }
    }

    // ── State persistence ───────────────────────────────────────────────

    private void LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                _state = JsonSerializer.Deserialize<HookBundleState>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new HookBundleState();
            }
        }
        catch { _state = new HookBundleState(); }
    }

    private void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFile, json);
        }
        catch { /* state save failure shouldn't crash the app */ }
    }

    // ── Script deployment ───────────────────────────────────────────────

    private void DeployBundledScripts()
    {
        // Source dir: scripts copied to <app>/Resources/HooksBundled/ at build time.
        string appDir = AppContext.BaseDirectory;
        string sourceDir = Path.Combine(appDir, "Resources", "HooksBundled");
        if (!Directory.Exists(sourceDir)) return;

        foreach (var srcPath in Directory.GetFiles(sourceDir, "*.ps1"))
        {
            string name = Path.GetFileName(srcPath);
            string dstPath = Path.Combine(_bundleDir, name);
            try
            {
                bool needsCopy = !File.Exists(dstPath) ||
                                 File.GetLastWriteTimeUtc(srcPath) > File.GetLastWriteTimeUtc(dstPath);
                if (needsCopy)
                    File.Copy(srcPath, dstPath, overwrite: true);
            }
            catch { /* file lock, perms — skip silently */ }
        }
    }

    // ── Projection: enabled bundle → hooks-bundled.json ────────────────

    private void ProjectEnabledToConfig()
    {
        // Build the same hooks.json format HookService.ParseHooksFile expects.
        var grouped = new Dictionary<string, List<object>>();
        foreach (var b in _catalog)
        {
            bool on = _state.EnabledById.TryGetValue(b.Id, out var v) ? v : b.DefaultEnabled;
            if (!on) continue;

            string scriptPath = Path.Combine(_bundleDir, b.ScriptFile);
            string command = BuildShellCommand(scriptPath, b);

            if (!grouped.TryGetValue(b.Phase, out var list))
            {
                list = new List<object>();
                grouped[b.Phase] = list;
            }
            list.Add(new
            {
                matcher = b.Matcher,
                command,
                timeout = b.TimeoutMs,
            });
        }

        var doc = new { hooks = grouped };
        try
        {
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_projectedConfig, json);
        }
        catch { /* projection failure shouldn't crash */ }
    }

    private static string BuildShellCommand(string scriptPath, HookBundle bundle)
    {
        // Build: powershell -NoProfile -ExecutionPolicy Bypass -File "<script>" <token args>
        var sb = new System.Text.StringBuilder();
        sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -File \"");
        sb.Append(scriptPath);
        sb.Append('"');

        // Forward whatever tokens the bundle declares as named params.
        // The script's param() block must match the casing here.
        foreach (var tok in bundle.SubstitutionTokens)
        {
            // strip braces for the param name; the value remains substituted by HookService
            string param = tok.Trim('{', '}');
            sb.Append(' ').Append('-').Append(Capitalize(param)).Append(' ').Append('"').Append(tok).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(bundle.ExtraArgs))
            sb.Append(' ').Append(bundle.ExtraArgs);
        return sb.ToString();
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── Catalog — the 15 Sprint-3-#2 bundles ───────────────────────────

    private static List<HookBundle> BuildCatalog() => new()
    {
        // ── PreToolUse (5) ────────────────────────────────────────────
        new HookBundle
        {
            Id = "pretool-block-dev-server",
            Name = "Block Dev Server Outside Workspace",
            Description = "Refuses run_command when it spawns a long-running dev server (npm run dev, vite, next, etc.) outside the current workspace.",
            Phase = "PreToolUse", Matcher = "run_command",
            ScriptFile = "pretool-block-dev-server.ps1",
            SubstitutionTokens = new() { "{command}", "{cwd}" },
            Category = "safety", Risk = "med", TimeoutMs = 5000,
        },
        new HookBundle
        {
            Id = "pretool-git-push-confirm",
            Name = "Git Push Confirmation Gate",
            Description = "Stops git push to main / master / production unless the command contains '--force-confirmed' or a marker file exists.",
            Phase = "PreToolUse", Matcher = "run_command",
            ScriptFile = "pretool-git-push-confirm.ps1",
            SubstitutionTokens = new() { "{command}" },
            Category = "safety", Risk = "high", TimeoutMs = 5000,
        },
        new HookBundle
        {
            Id = "pretool-secret-scan",
            Name = "Secret Scan",
            Description = "Scans the content being written/edited for API keys, private keys, JWTs, AWS credentials before allowing the write to proceed.",
            Phase = "PreToolUse", Matcher = "write_file",
            ScriptFile = "pretool-secret-scan.ps1",
            SubstitutionTokens = new() { "{path}", "{arguments}" },
            Category = "safety", Risk = "high", TimeoutMs = 8000,
            DefaultEnabled = true,
        },
        new HookBundle
        {
            Id = "pretool-pre-commit-quality",
            Name = "Pre-Commit Quality Check",
            Description = "Before git commit, ensures no .env / *.key / *.pem files are staged.",
            Phase = "PreToolUse", Matcher = "run_command",
            ScriptFile = "pretool-pre-commit-quality.ps1",
            SubstitutionTokens = new() { "{command}", "{cwd}" },
            Category = "safety", Risk = "med", TimeoutMs = 8000,
        },
        new HookBundle
        {
            Id = "pretool-dangerous-cmd-warn",
            Name = "Dangerous Command Warning",
            Description = "Refuses rm -rf /, format c:, drop database, git push --force --no-verify, and similar destructive commands.",
            Phase = "PreToolUse", Matcher = "run_command",
            ScriptFile = "pretool-dangerous-cmd-warn.ps1",
            SubstitutionTokens = new() { "{command}" },
            Category = "safety", Risk = "high", TimeoutMs = 3000,
            DefaultEnabled = true,
        },

        // ── PostToolUse (5) ───────────────────────────────────────────
        new HookBundle
        {
            Id = "posttool-prettier-format",
            Name = "Prettier Format",
            Description = "After write_file or edit_file on JS/TS/CSS/JSON/MD, runs prettier --write if installed.",
            Phase = "PostToolUse", Matcher = "write_file",
            ScriptFile = "posttool-prettier-format.ps1",
            SubstitutionTokens = new() { "{path}" },
            Category = "quality", Risk = "low", TimeoutMs = 10000,
        },
        new HookBundle
        {
            Id = "posttool-typescript-check",
            Name = "TypeScript Type Check",
            Description = "After write_file on a .ts/.tsx file, runs tsc --noEmit on it and reports errors to the debug log.",
            Phase = "PostToolUse", Matcher = "write_file",
            ScriptFile = "posttool-typescript-check.ps1",
            SubstitutionTokens = new() { "{path}" },
            Category = "quality", Risk = "low", TimeoutMs = 20000,
        },
        new HookBundle
        {
            Id = "posttool-console-log-warn",
            Name = "console.log Linter",
            Description = "After write_file/edit_file on .js/.ts, greps for forgotten console.log statements and prints a warning.",
            Phase = "PostToolUse", Matcher = "write_file",
            ScriptFile = "posttool-console-log-warn.ps1",
            SubstitutionTokens = new() { "{path}" },
            Category = "quality", Risk = "low", TimeoutMs = 5000,
        },
        new HookBundle
        {
            Id = "posttool-pr-link-logger",
            Name = "PR Link Logger",
            Description = "After every git push, logs the GitHub PR URL (if any) to ~/.cluadex/logs/pr-links.log.",
            Phase = "PostToolUse", Matcher = "run_command",
            ScriptFile = "posttool-pr-link-logger.ps1",
            SubstitutionTokens = new() { "{command}", "{cwd}" },
            Category = "observability", Risk = "low", TimeoutMs = 5000,
        },
        new HookBundle
        {
            Id = "posttool-quality-gate",
            Name = "Quality Gate (build + test)",
            Description = "After any *.cs / *.csproj write, runs dotnet build --no-restore -nologo. Logs result to debug log.",
            Phase = "PostToolUse", Matcher = "write_file",
            ScriptFile = "posttool-quality-gate.ps1",
            SubstitutionTokens = new() { "{path}", "{cwd}" },
            Category = "quality", Risk = "low", TimeoutMs = 60000,
        },

        // ── Stop (3) ──────────────────────────────────────────────────
        new HookBundle
        {
            Id = "stop-pattern-extract",
            Name = "Pattern Extract → Instincts",
            Description = "When the agentic loop ends, writes a record to ~/.cluadex/instincts-inbox.jsonl that InstinctService can ingest as observations.",
            Phase = "Stop", Matcher = "*",
            ScriptFile = "stop-pattern-extract.ps1",
            SubstitutionTokens = new() { "{model}", "{cost}", "{tokens}", "{turn_count}" },
            Category = "telemetry", Risk = "low", TimeoutMs = 5000,
            DefaultEnabled = true,
        },
        new HookBundle
        {
            Id = "stop-cost-summary-toast",
            Name = "Cost Summary Toast",
            Description = "At end of session, writes a one-line cost summary to ~/.cluadex/logs/session-costs.csv (model, cost USD, tokens, turns).",
            Phase = "Stop", Matcher = "*",
            ScriptFile = "stop-cost-summary-toast.ps1",
            SubstitutionTokens = new() { "{model}", "{cost}", "{tokens}", "{turn_count}" },
            Category = "telemetry", Risk = "low", TimeoutMs = 3000,
            DefaultEnabled = true,
        },
        new HookBundle
        {
            Id = "stop-desktop-notify",
            Name = "Desktop Notification on Done",
            Description = "Pops a Windows toast notification when the agent finishes — handy when you're in another window.",
            Phase = "Stop", Matcher = "*",
            ScriptFile = "stop-desktop-notify.ps1",
            SubstitutionTokens = new() { "{model}", "{cost}" },
            Category = "dx", Risk = "low", TimeoutMs = 3000,
        },

        // ── SessionStart (2) ──────────────────────────────────────────
        new HookBundle
        {
            Id = "sessionstart-load-handoff",
            Name = "Load Last Handoff",
            Description = "If a Notes/Claude-Sessions/handoff-*.md exists in {cwd}, copies its content into ~/.cluadex/logs/last-handoff.md so the AI can read it as a tool.",
            Phase = "SessionStart", Matcher = "*",
            ScriptFile = "sessionstart-load-handoff.ps1",
            SubstitutionTokens = new() { "{cwd}" },
            Category = "dx", Risk = "low", TimeoutMs = 5000,
        },
        new HookBundle
        {
            Id = "sessionstart-detect-pkg-mgr",
            Name = "Detect Package Manager",
            Description = "Reports which package manager the workspace uses (npm/yarn/pnpm/bun/pip/poetry/cargo/dotnet) to ~/.cluadex/logs/last-package-manager.txt.",
            Phase = "SessionStart", Matcher = "*",
            ScriptFile = "sessionstart-detect-pkg-mgr.ps1",
            SubstitutionTokens = new() { "{cwd}" },
            Category = "dx", Risk = "low", TimeoutMs = 3000,
            DefaultEnabled = true,
        },

        // ── PreCompact (1) ────────────────────────────────────────────
        new HookBundle
        {
            Id = "precompact-save-snapshot",
            Name = "Save Snapshot Before Compact",
            Description = "Before microcompaction strips/truncates history, saves a timestamped snapshot of the current chat to ~/.cluadex/logs/compaction-snapshots/.",
            Phase = "PreCompact", Matcher = "*",
            ScriptFile = "precompact-save-snapshot.ps1",
            SubstitutionTokens = new() { "{message_count}", "{token_estimate}" },
            Category = "observability", Risk = "low", TimeoutMs = 5000,
            DefaultEnabled = true,
        },
    };
}
