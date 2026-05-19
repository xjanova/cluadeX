namespace CluadeX.Models;

/// <summary>
/// A bundled hook script that ships with CluadeX. Users browse the catalog
/// in HookLibraryView and toggle individual hooks on/off. Enabled hooks are
/// projected into ~/.cluadex/hooks-bundled.json which HookService reads
/// alongside the user's own hooks.json.
/// </summary>
public class HookBundle
{
    /// <summary>Stable id, e.g. "pretool-secret-scan".</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name, e.g. "Secret Scan".</summary>
    public string Name { get; set; } = "";

    /// <summary>One-line description shown in the catalog card.</summary>
    public string Description { get; set; } = "";

    /// <summary>Phase: PreToolUse / PostToolUse / Stop / SessionStart / PreCompact.</summary>
    public string Phase { get; set; } = "";

    /// <summary>Matcher pattern (tool name or "*"). Only meaningful for tool phases.</summary>
    public string Matcher { get; set; } = "*";

    /// <summary>The .ps1 filename inside ~/.cluadex/hooks-bundled/.</summary>
    public string ScriptFile { get; set; } = "";

    /// <summary>Optional extra args appended to the powershell command (e.g. " -Strict").</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>Tokens that will be substituted by HookService into the script call.</summary>
    public List<string> SubstitutionTokens { get; set; } = new();

    /// <summary>Default timeout in ms.</summary>
    public int TimeoutMs { get; set; } = 10000;

    /// <summary>If true, the hook is enabled out-of-the-box on first run.</summary>
    public bool DefaultEnabled { get; set; }

    /// <summary>One of: safety, quality, dx, observability, telemetry.</summary>
    public string Category { get; set; } = "dx";

    /// <summary>Risk badge: low / med / high. Affects UI color.</summary>
    public string Risk { get; set; } = "low";

    // Runtime-only — not persisted in the bundle catalog
    public bool IsEnabled { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string LastRunMessage { get; set; } = "";
    public bool LastRunSucceeded { get; set; } = true;
}

/// <summary>State file persisted at ~/.cluadex/hooks-bundle-state.json.</summary>
public class HookBundleState
{
    public Dictionary<string, bool> EnabledById { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
