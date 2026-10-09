using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Manages the Hook Script Library (Sprint 3 #2).
///
/// On first run:
///   1. Copies bundled .ps1 scripts from the app's "Resources/HooksBundled" content
///      directory into ~/.cluadex/hooks-bundled/ whenever the deployed copy is missing or
///      differs from the shipped one (a copy this service did not write is kept as
///      &lt;name&gt;.ps1.&lt;yyyyMMdd-HHmmss&gt;.bak, and every replacement is logged under "Hooks").
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
    private readonly DebugLogService? _log;
    private readonly string _bundleDir;          // ~/.cluadex/hooks-bundled/
    private readonly string _stateFile;          // ~/.cluadex/hooks-bundle-state.json
    private readonly string _projectedConfig;    // ~/.cluadex/hooks-bundled.json
    private readonly object _lock = new();

    private HookBundleState _state = new();
    private List<HookBundle> _catalog;

    public event Action? Changed;

    public HookBundleService(HookService hookService, DebugLogService? debugLog = null)
    {
        _hookService = hookService;
        _log = debugLog;
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
        // Re-projected on EVERY start from the catalog (the state file only stores on/off), so an existing
        // install picks up a changed matcher — e.g. the shell-safety hooks now also cover task_create and
        // powershell — without the user toggling anything.
        ProjectEnabledToConfig();
        // HookService may already have cached the previous projection (anything that ran a hook before this
        // service was first resolved); make it re-read the file we just wrote.
        _hookService.ReloadHooks();
    }

    public IReadOnlyList<HookBundle> GetCatalog()
    {
        lock (_lock)
        {
            // Hydrate IsEnabled from state for the UI
            foreach (var b in _catalog)
            {
                b.IsEnabled = _state.EnabledById.TryGetValue(b.Id, out var v) ? v : b.DefaultEnabled;
                // Name/description in the UI language, re-read on every call so a language switch
                // shows on the next refresh. The English text in BuildCatalog stays the fallback.
                if (!_english.TryGetValue(b.Id, out var en)) _english[b.Id] = en = (b.Name, b.Description);
                b.Name = Localized($"hookb.{b.Id}.name", en.Name);
                b.Description = Localized($"hookb.{b.Id}.desc", en.Description);
            }
            return _catalog.ToList();
        }
    }

    private readonly Dictionary<string, (string Name, string Description)> _english = new();

    private static string Localized(string key, string fallback)
    {
        string s = LocalizedResources.Instance[key];
        return s == key ? fallback : s;
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
            return File.Exists(path) ? File.ReadAllText(path) : LocalizedResources.Instance["hooklib.notDeployed"];
        }
        catch { return LocalizedResources.Instance["hooklib.readFailed"]; }
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
        string sourceDir = Path.Combine(AppContext.BaseDirectory, "Resources", "HooksBundled");
        DeployScripts(sourceDir, _bundleDir, _log);
    }

    /// <summary>SHA-256 of every script this service wrote (name → hex), so the next deploy can tell its own
    /// older release (replace quietly) from a copy someone edited (keep a backup).</summary>
    public const string DeployedHashesFile = ".deployed-hashes.json";

    /// <summary>
    /// Make every deployed script in <paramref name="bundleDir"/> byte-identical to the shipped one.
    /// The deployed copies are app-managed. The old rule ("copy only if the shipped file is NEWER") left
    /// an outdated script in place whenever the deployed copy had a later timestamp — and the outdated
    /// safety scripts block with exit 1 while echoing the model's command into stderr, the fail-open the
    /// exit-2 contract closes. A differing copy that is not one this service wrote (its hash is not the one
    /// recorded in <see cref="DeployedHashesFile"/> — a hand edit, or an install from before the record
    /// existed) is kept as &lt;name&gt;.ps1.&lt;yyyyMMdd-HHmmss&gt;.bak first; timestamps decided this before,
    /// and an edit made before an app update was overwritten without a copy. Every replacement is logged
    /// (Info, category "Hooks") with the file and where its backup went. Returns how many were (re)written.
    /// </summary>
    public static int DeployScripts(string sourceDir, string bundleDir, DebugLogService? log = null)
    {
        if (!Directory.Exists(sourceDir)) return 0;
        string hashesPath = Path.Combine(bundleDir, DeployedHashesFile);
        var known = LoadDeployedHashes(hashesPath);
        bool hashesChanged = false;
        int written = 0;
        foreach (var srcPath in Directory.GetFiles(sourceDir, "*.ps1"))
        {
            string name = Path.GetFileName(srcPath);
            string dstPath = Path.Combine(bundleDir, name);
            try
            {
                byte[] shipped = File.ReadAllBytes(srcPath);
                string shippedHash = Sha256Hex(shipped);
                string? backup = null;
                bool replacing = File.Exists(dstPath);
                if (replacing)
                {
                    byte[] deployed = File.ReadAllBytes(dstPath);
                    if (deployed.AsSpan().SequenceEqual(shipped))
                    {
                        if (!known.TryGetValue(name, out var h) || h != shippedHash) { known[name] = shippedHash; hashesChanged = true; }
                        continue;
                    }
                    bool ours = known.TryGetValue(name, out var recorded) && recorded == Sha256Hex(deployed);
                    if (!ours)
                    {
                        backup = UniqueBackupPath(dstPath);
                        File.Copy(dstPath, backup, overwrite: false);
                    }
                }
                File.Copy(srcPath, dstPath, overwrite: true);
                known[name] = shippedHash;
                hashesChanged = true;
                written++;
                if (replacing)
                    log?.Info("Hooks", backup != null
                        ? $"Hook Library: {name} differed from the shipped script and was not written by CluadeX; replaced it with the shipped version. The previous copy is kept as {backup}"
                        : $"Hook Library: {name} was an older CluadeX release; replaced it with the shipped version (no backup needed) in {bundleDir}");
            }
            catch (Exception ex)
            {
                log?.Warn("Hooks", $"Hook Library: could not deploy {name} to {bundleDir} — the next start tries again", ex);
            }
        }
        if (hashesChanged)
        {
            try
            {
                File.WriteAllText(hashesPath, JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                log?.Warn("Hooks", $"Hook Library: could not record the deployed script hashes in {hashesPath}", ex);
            }
        }
        return written;
    }

    private static Dictionary<string, string> LoadDeployedHashes(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } d)
                return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt record → every differing copy is backed up (the safe side) */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>&lt;file&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak (Gregorian digits whatever the UI culture), never an existing name.</summary>
    private static string UniqueBackupPath(string dstPath)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string p = $"{dstPath}.{stamp}.bak";
        for (int n = 2; File.Exists(p); n++) p = $"{dstPath}.{stamp}-{n}.bak";
        return p;
    }

    // ── Projection: enabled bundle → hooks-bundled.json ────────────────

    private void ProjectEnabledToConfig()
    {
        try
        {
            File.WriteAllText(_projectedConfig, BuildProjectedConfig(_catalog, _state.EnabledById, _bundleDir));
        }
        catch { /* projection failure shouldn't crash */ }
    }

    /// <summary>The hooks.json text (the format HookService.ParseHooksFile reads) for the enabled
    /// bundles. Matchers come from the catalog, never from the saved state.</summary>
    public static string BuildProjectedConfig(IEnumerable<HookBundle> catalog, IReadOnlyDictionary<string, bool> enabledById, string bundleDir)
    {
        var grouped = new Dictionary<string, List<object>>();
        foreach (var b in catalog)
        {
            bool on = enabledById.TryGetValue(b.Id, out var v) ? v : b.DefaultEnabled;
            if (!on) continue;

            string scriptPath = Path.Combine(bundleDir, b.ScriptFile);
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

        return JsonSerializer.Serialize(new { hooks = grouped }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Tokens the hook process already receives exactly through the environment
    /// (CLUADEX_HOOK_* — see HookService). They are NOT put on the command line: the payload is
    /// unbounded (a write_file's whole content in {arguments} overflowed cmd.exe's 8,191-char limit
    /// and blocked every large write), and a path or command squeezed through cmd quoting arrives
    /// altered ($ and % stripped, "R&amp;D" mangled).</summary>
    private static readonly HashSet<string> EnvironmentTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "{tool}", "{path}", "{command}", "{arguments}", "{cwd}",
    };

    public static string BuildShellCommand(string scriptPath, HookBundle bundle)
    {
        // Build: powershell -NoProfile -ExecutionPolicy Bypass -File "<script>" <token args>
        var sb = new System.Text.StringBuilder();
        sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -File \"");
        sb.Append(scriptPath);
        sb.Append('"');

        // Forward the remaining (small, numeric/name) tokens as named params.
        // The script's param() block must match the casing here.
        foreach (var tok in bundle.SubstitutionTokens)
        {
            if (EnvironmentTokens.Contains(tok)) continue;
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

    /// <summary>Every tool that writes file content. The file hooks used to match write_file alone,
    /// so an agent that edited instead of rewrote (the usual case) skipped the secret scan,
    /// prettier and the console.log check entirely. HookService understands "a|b" matchers.</summary>
    public const string FileWriteTools = "write_file|edit_file|multi_edit";

    /// <summary>Every tool that runs a shell command. The shell-safety hooks matched run_command alone,
    /// so the same command sent as task_create (a background task), powershell, or as run_build /
    /// run_tests (whose optional "command" overrides the detected one and runs through the same shell)
    /// skipped the dangerous-command, push-gate, pre-commit and dev-server checks. The scripts read the
    /// payload's "command" — or, for powershell, "script".</summary>
    public const string ShellCommandTools = "run_command|task_create|powershell|run_build|run_tests";

    /// <summary>Timeout of every bundled PreToolUse hook. A PreToolUse timeout BLOCKS the tool, and a bundled hook
    /// is a PowerShell process whose cold start alone can pass 3 s on a loaded machine — at 3000 ms the advisory
    /// dangerous-command check timed out and blocked a plain run_build (tooltest B2). 10 s covers a cold start
    /// with room to spare; a hook that really hangs is still stopped.</summary>
    public const int PreToolUseTimeoutMs = 10000;

    // ── Catalog — the 15 Sprint-3-#2 bundles ───────────────────────────

    private static List<HookBundle> BuildCatalog() => new()
    {
        // ── PreToolUse (5) ────────────────────────────────────────────
        new HookBundle
        {
            Id = "pretool-block-dev-server",
            Name = "Block Dev Server Outside Workspace",
            Description = "Refuses a shell command (run_command / task_create / powershell / run_build / run_tests) that starts a long-running dev server (npm run dev, vite, next, etc.) when no workspace folder is open.",
            Phase = "PreToolUse", Matcher = ShellCommandTools,
            ScriptFile = "pretool-block-dev-server.ps1",
            SubstitutionTokens = new() { "{command}", "{cwd}" },
            Category = "safety", Risk = "med", TimeoutMs = PreToolUseTimeoutMs,
        },
        new HookBundle
        {
            Id = "pretool-git-push-confirm",
            Name = "Git Push Confirmation Gate",
            Description = "Stops git push to main / master / production — sent through run_command / task_create / powershell / run_build / run_tests — unless the command contains '--force-confirmed' or a marker file exists.",
            Phase = "PreToolUse", Matcher = ShellCommandTools,
            ScriptFile = "pretool-git-push-confirm.ps1",
            SubstitutionTokens = new() { "{command}" },
            Category = "safety", Risk = "high", TimeoutMs = PreToolUseTimeoutMs,
        },
        new HookBundle
        {
            Id = "pretool-secret-scan",
            Name = "Secret Scan",
            Description = "Scans the content being written/edited for API keys, private keys, JWTs, AWS credentials before allowing the write to proceed.",
            Phase = "PreToolUse", Matcher = FileWriteTools,
            ScriptFile = "pretool-secret-scan.ps1",
            SubstitutionTokens = new() { "{path}", "{arguments}" },
            Category = "safety", Risk = "high", TimeoutMs = PreToolUseTimeoutMs,
            DefaultEnabled = true,
        },
        new HookBundle
        {
            Id = "pretool-pre-commit-quality",
            Name = "Pre-Commit Quality Check",
            Description = "Before a git commit run through run_command / task_create / powershell / run_build / run_tests, ensures no .env / *.key / *.pem files are staged.",
            Phase = "PreToolUse", Matcher = ShellCommandTools,
            ScriptFile = "pretool-pre-commit-quality.ps1",
            SubstitutionTokens = new() { "{command}", "{cwd}" },
            Category = "safety", Risk = "med", TimeoutMs = PreToolUseTimeoutMs,
        },
        new HookBundle
        {
            Id = "pretool-dangerous-cmd-warn",
            Name = "Dangerous Command Warning",
            Description = "Refuses rm -rf /, format c:, drop database, git push --force --no-verify and similar destructive commands sent through run_command / task_create / powershell / run_build / run_tests.",
            Phase = "PreToolUse", Matcher = ShellCommandTools,
            ScriptFile = "pretool-dangerous-cmd-warn.ps1",
            SubstitutionTokens = new() { "{command}" },
            Category = "safety", Risk = "high", TimeoutMs = PreToolUseTimeoutMs,
            DefaultEnabled = true,
        },

        // ── PostToolUse (5) ───────────────────────────────────────────
        new HookBundle
        {
            Id = "posttool-prettier-format",
            Name = "Prettier Format",
            Description = "After write_file / edit_file / multi_edit on JS/TS/CSS/JSON/MD, runs prettier --write if installed.",
            Phase = "PostToolUse", Matcher = FileWriteTools,
            ScriptFile = "posttool-prettier-format.ps1",
            SubstitutionTokens = new() { "{path}" },
            Category = "quality", Risk = "low", TimeoutMs = 10000,
        },
        new HookBundle
        {
            Id = "posttool-typescript-check",
            Name = "TypeScript Type Check",
            Description = "After write_file / edit_file / multi_edit on a .ts/.tsx file, runs tsc --noEmit on it and reports errors to the debug log.",
            Phase = "PostToolUse", Matcher = FileWriteTools,
            ScriptFile = "posttool-typescript-check.ps1",
            SubstitutionTokens = new() { "{path}" },
            Category = "quality", Risk = "low", TimeoutMs = 20000,
        },
        new HookBundle
        {
            Id = "posttool-console-log-warn",
            Name = "console.log Linter",
            Description = "After write_file / edit_file / multi_edit on .js/.ts, greps for forgotten console.log statements and prints a warning.",
            Phase = "PostToolUse", Matcher = FileWriteTools,
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
            Description = "After any *.cs / *.csproj write or edit, runs dotnet build --no-restore -nologo. Logs result to debug log.",
            Phase = "PostToolUse", Matcher = FileWriteTools,
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
