using System.IO;
using System.IO.Enumeration;
using System.Text.Json;

namespace CluadeX.Services.Uninstall;

/// <summary>The rows of "what to keep" on the uninstall page.</summary>
public enum KeepGroupId { Settings, Chats, Models, Extensions, Studio, License, Logs }

/// <summary>A folder the user chose in Settings that lies outside the data folder (models on another drive, …): its own
/// row with its full path, kept unless the user unticks it — it may hold things that are not CluadeX's.</summary>
public sealed record CustomFolder(string SettingName, string Path, KeepGroupId Kind);

/// <summary>
/// Which file in the data folder (~/.cluadex) belongs to which row, from the inventory of every writer in the app
/// (2026-11-08). Anything not listed is app data and goes with "Settings", so a folder a future version adds is never
/// left behind when everything is unticked. The license and the account sign-in live INSIDE settings.json, so those two
/// rows split that one file (<see cref="LicenseFields"/>).
/// </summary>
public static class UninstallCatalog
{
    public static readonly IReadOnlyList<KeepGroupId> Order = new[]
    {
        KeepGroupId.Chats, KeepGroupId.Settings, KeepGroupId.License, KeepGroupId.Extensions,
        KeepGroupId.Studio, KeepGroupId.Models, KeepGroupId.Logs,
    };

    /// <summary>Logs and cache are rebuilt when needed; everything else is the user's and is kept unless unticked.</summary>
    public static bool DefaultKeep(KeepGroupId g) => g != KeepGroupId.Logs;

    public static string LabelKey(KeepGroupId g) => "un.g." + Short(g);
    public static string DescKey(KeepGroupId g) => "un.g." + Short(g) + ".d";

    public static string Short(KeepGroupId g) => g switch
    {
        KeepGroupId.Settings => "settings",
        KeepGroupId.Chats => "chats",
        KeepGroupId.Models => "models",
        KeepGroupId.Extensions => "ext",
        KeepGroupId.Studio => "studio",
        KeepGroupId.License => "license",
        _ => "logs",
    };

    /// <summary>"settings,chats" (or "none") → the rows to keep; unknown names are refused.</summary>
    public static List<KeepGroupId> ParseKeep(string list)
    {
        var keep = new List<KeepGroupId>();
        foreach (string raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) { keep.AddRange(Order); continue; }
            var g = Order.FirstOrDefault(x => Short(x).Equals(raw, StringComparison.OrdinalIgnoreCase) || x.ToString().Equals(raw, StringComparison.OrdinalIgnoreCase), (KeepGroupId)(-1));
            if ((int)g < 0) throw new ArgumentException($"unknown --keep item '{raw}' (settings, chats, models, ext, studio, license, logs, all, none)");
            if (!keep.Contains(g)) keep.Add(g);
        }
        return keep;
    }

    /// <summary>Removed with every uninstall, whatever is ticked: CluadeX's own Chrome connection files.</summary>
    public const string AlwaysMarker = "<always>";

    /// <summary>settings.json's backups (.bak, .tmp, .corrupt-*) carry the license key in plain text: they go when
    /// either the settings row or the license row is unticked.</summary>
    public const string SettingsBackupMarker = "<settings-backup>";

    private static readonly (string Pattern, string Group)[] Rules =
    {
        // first: a backup of settings.json is never "just a .tmp"
        ("settings.json?*", SettingsBackupMarker),
        // chats and what the AI remembers
        ("Sessions", nameof(KeepGroupId.Chats)), ("codex.db*", nameof(KeepGroupId.Chats)), ("checkpoints", nameof(KeepGroupId.Chats)),
        ("file-backups", nameof(KeepGroupId.Chats)), ("attachments", nameof(KeepGroupId.Chats)), ("screens", nameof(KeepGroupId.Chats)),
        ("memory", nameof(KeepGroupId.Chats)), ("projects", nameof(KeepGroupId.Chats)), ("chat-links.json", nameof(KeepGroupId.Chats)),
        ("chat-branches.json", nameof(KeepGroupId.Chats)), ("ultra-chats.json", nameof(KeepGroupId.Chats)), ("design-chats.json", nameof(KeepGroupId.Chats)),
        ("skill-pick-chats.json", nameof(KeepGroupId.Chats)), ("verify-baselines", nameof(KeepGroupId.Chats)), ("batches.json", nameof(KeepGroupId.Chats)),
        ("instincts*", nameof(KeepGroupId.Chats)),
        // models and the GPU runtime
        ("Models", nameof(KeepGroupId.Models)), ("llama-backend*", nameof(KeepGroupId.Models)),
        // what the user added
        ("skills", nameof(KeepGroupId.Extensions)), ("skill-usage.json", nameof(KeepGroupId.Extensions)), ("skill-brain-index.json", nameof(KeepGroupId.Extensions)),
        ("agents", nameof(KeepGroupId.Extensions)), ("plugins", nameof(KeepGroupId.Extensions)), ("plugins_config.json", nameof(KeepGroupId.Extensions)),
        ("hooks-bundled*", nameof(KeepGroupId.Extensions)), ("hooks.json", nameof(KeepGroupId.Extensions)), ("hooks-bundle-state.json", nameof(KeepGroupId.Extensions)),
        ("trusted-hook-folders.json", nameof(KeepGroupId.Extensions)), ("allow-push-main", nameof(KeepGroupId.Extensions)),
        ("mcp_servers.json*", nameof(KeepGroupId.Extensions)), ("mcp-started.json", nameof(KeepGroupId.Extensions)),
        ("connectors", nameof(KeepGroupId.Extensions)), ("workflows", nameof(KeepGroupId.Extensions)),
        // studio work
        ("UiStudio", nameof(KeepGroupId.Studio)), ("NodeGraph", nameof(KeepGroupId.Studio)), ("MediaEditor", nameof(KeepGroupId.Studio)),
        // the sign-in window's cookies
        ("webview-signin", nameof(KeepGroupId.License)),
        // rebuilt when needed
        ("logs", nameof(KeepGroupId.Logs)), ("crash-logs", nameof(KeepGroupId.Logs)), ("bg", nameof(KeepGroupId.Logs)), ("Cache", nameof(KeepGroupId.Logs)),
        ("Temp", nameof(KeepGroupId.Logs)), ("WebView2", nameof(KeepGroupId.Logs)), ("update-cache", nameof(KeepGroupId.Logs)),
        ("update-attempts.json", nameof(KeepGroupId.Logs)), ("mcp-host.log", nameof(KeepGroupId.Logs)), ("mcp-host-token", nameof(KeepGroupId.Logs)),
        ("voice", nameof(KeepGroupId.Logs)), ("splash-girl.txt", nameof(KeepGroupId.Logs)), ("setup-girls.txt", nameof(KeepGroupId.Logs)),
        ("permissions-today.json", nameof(KeepGroupId.Logs)), ("install.json", nameof(KeepGroupId.Logs)), ("*.tmp", nameof(KeepGroupId.Logs)),
        // always
        ("chrome", AlwaysMarker),
    };

    /// <summary>The row a top-level entry of the data folder belongs to: a group name, <see cref="AlwaysMarker"/>, or
    /// <see cref="SettingsBackupMarker"/>. settings.json itself is "Settings" (split with the license by the cleaner).</summary>
    public static string Classify(string name)
    {
        foreach (var (pattern, group) in Rules)
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return group;
        return nameof(KeepGroupId.Settings);
    }

    /// <summary>The fields of settings.json that are the license and the Xman Studio account (AppSettings).</summary>
    public static readonly IReadOnlyList<string> LicenseFields = new[]
    {
        "ActivationKey", "LicenseType", "LicenseExpiresUtc", "LicenseValidatedUtc", "LicenseTrialRequested", "LicenseTrialEndedNoticeShown",
        "AccountLinked", "AccountEmailMasked", "AccountName", "AccountLinkedUtc",
    };

    /// <summary>A folder holds CluadeX data (what --data-root insists on, so a typo can never empty another folder).</summary>
    public static bool LooksLikeDataRoot(string dir) =>
        Directory.Exists(dir) && (File.Exists(System.IO.Path.Combine(dir, "settings.json")) || File.Exists(System.IO.Path.Combine(dir, "codex.db")));

    /// <summary>The five folders Settings lets the user move (ModelDirectory, SessionDirectory, …) when they point
    /// outside the data folder.</summary>
    public static List<CustomFolder> CustomFolders(string dataRoot)
    {
        var list = new List<CustomFolder>();
        try
        {
            string file = System.IO.Path.Combine(dataRoot, "settings.json");
            if (!File.Exists(file)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var (name, kind) in new[]
            {
                ("SessionDirectory", KeepGroupId.Chats), ("ModelDirectory", KeepGroupId.Models),
                ("CacheDirectory", KeepGroupId.Logs), ("LogDirectory", KeepGroupId.Logs), ("TempDirectory", KeepGroupId.Logs),
            })
            {
                if (!doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) continue;
                string? path = v.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                string full;
                try { full = System.IO.Path.GetFullPath(path); } catch { continue; }
                if (Safety.ProtectedPaths.IsInside(full, dataRoot) || !Directory.Exists(full)) continue;
                if (list.Any(c => UninstallEntry.SamePath(c.Path, full))) continue;
                list.Add(new CustomFolder(name, full, kind));
            }
        }
        catch { }
        return list;
    }
}
