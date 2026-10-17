using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio;

/// <summary>How the UI Studio runs and maps a project.</summary>
public enum StudioProjectKind
{
    None,
    /// <summary>Plain HTML/CSS/JS — served by CluadeX itself, exact locations with no setup.</summary>
    Static,
    /// <summary>A Vite project (React, Vue, Svelte, vanilla) — exact locations through the CluadeX locator plugin.</summary>
    Vite,
    /// <summary>Another Node dev server (Next, Nuxt, Astro, CRA, Angular…) — previewed and inspected; React 18 apps
    /// still map through React's own dev source info, the rest fall back to searching the source.</summary>
    NodeOther,
    /// <summary>A UI the Studio cannot render in a browser view (Flutter, WPF, native) — shown with the reason.</summary>
    Unsupported,
}

public sealed class StudioProjectInfo
{
    public string Root { get; init; } = "";
    public StudioProjectKind Kind { get; init; }
    public string Framework { get; init; } = "";      // "react", "vue", "svelte", "next", "html", "flutter"…
    public string? DevScript { get; init; }            // "dev" / "start"
    public string? EntryHtml { get; init; }            // static: the page to open first (relative)
    public bool LocatorInstalled { get; init; }
    public string? ViteConfigPath { get; init; }
    public string? UnsupportedReason { get; init; }    // a localization key
}

/// <summary>Looks at a folder and decides how the Studio should run it.</summary>
public static class StudioProject
{
    public const string LocatorFileName = "cluadex-locator.mjs";

    public static StudioProjectInfo Detect(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new StudioProjectInfo { Root = root ?? "", Kind = StudioProjectKind.None };

        string pkgPath = Path.Combine(root, "package.json");
        if (File.Exists(pkgPath))
        {
            var (scripts, deps) = ReadPackage(pkgPath);
            string? script = scripts.Contains("dev") ? "dev" : scripts.Contains("start") ? "start" : null;
            string framework = deps.Contains("next") ? "next"
                : deps.Contains("nuxt") ? "nuxt"
                : deps.Contains("astro") ? "astro"
                : deps.Contains("@angular/core") ? "angular"
                : deps.Contains("svelte") ? "svelte"
                : deps.Contains("vue") ? "vue"
                : deps.Contains("react") ? "react"
                : "node";
            string? viteConfig = new[] { "vite.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.mts", "vite.config.cjs" }
                .Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists);
            bool isVite = viteConfig != null || deps.Contains("vite") && framework is not ("next" or "nuxt" or "astro" or "angular");
            if (script != null)
            {
                bool locator = viteConfig != null && SafeRead(viteConfig).Contains("cluadex-locator", StringComparison.Ordinal);
                return new StudioProjectInfo
                {
                    Root = root, Kind = isVite ? StudioProjectKind.Vite : StudioProjectKind.NodeOther, Framework = framework,
                    DevScript = script, LocatorInstalled = locator, ViteConfigPath = viteConfig,
                };
            }
            // A package.json without a dev script: fall through to static if there is a page to show.
        }

        if (File.Exists(Path.Combine(root, "pubspec.yaml")))
            return new StudioProjectInfo { Root = root, Kind = StudioProjectKind.Unsupported, Framework = "flutter", UnsupportedReason = "studio.unsupported.flutter" };

        string? entry = FindEntryHtml(root);
        if (entry != null)
            return new StudioProjectInfo { Root = root, Kind = StudioProjectKind.Static, Framework = "html", EntryHtml = entry, LocatorInstalled = true };

        bool wpf = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any(p => SafeRead(p).Contains("<UseWPF>true", StringComparison.OrdinalIgnoreCase));
        if (wpf)
            return new StudioProjectInfo { Root = root, Kind = StudioProjectKind.Unsupported, Framework = "wpf", UnsupportedReason = "studio.unsupported.wpf" };

        return new StudioProjectInfo { Root = root, Kind = StudioProjectKind.Unsupported, Framework = "", UnsupportedReason = "studio.unsupported.none" };
    }

    private static string? FindEntryHtml(string root)
    {
        foreach (var candidate in new[] { "index.html", "public/index.html", "src/index.html", "docs/index.html", "dist/index.html" })
            if (File.Exists(Path.Combine(root, candidate))) return candidate;
        try
        {
            return Directory.EnumerateFiles(root, "*.html", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch { return null; }
    }

    private static (HashSet<string> Scripts, HashSet<string> Deps) ReadPackage(string path)
    {
        var scripts = new HashSet<string>(StringComparer.Ordinal);
        var deps = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject()) scripts.Add(p.Name);
            foreach (var key in new[] { "dependencies", "devDependencies" })
                if (doc.RootElement.TryGetProperty(key, out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.EnumerateObject()) deps.Add(p.Name);
        }
        catch { /* a broken package.json: treated as having nothing */ }
        return (scripts, deps);
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    // ───────────────────────────── locator install ─────────────────────────────

    private static readonly Regex PluginsArray = new(@"plugins\s*:\s*\[", RegexOptions.Compiled);

    /// <summary>
    /// What adding the locator plugin to a Vite project changes: the new <c>cluadex-locator.mjs</c> next to the
    /// config and the config itself with the import and <c>cxLocator()</c> first in <c>plugins: [</c>. Null config
    /// text (with a reason) when the config is not in a shape this can patch safely — then the user is shown the
    /// two lines to add by hand.
    /// </summary>
    public static (string? NewConfig, string? Error) PlanLocatorInstall(string configText)
    {
        if (configText.Contains("cluadex-locator", StringComparison.Ordinal)) return (configText, null);
        var plugins = PluginsArray.Matches(configText);
        if (plugins.Count != 1) return (null, "studio.locator.manual");
        int insertAt = plugins[0].Index + plugins[0].Length;
        string patched = configText[..insertAt] + "cxLocator(), " + configText[insertAt..];

        // First line of the file: always valid ESM (a multi-line import further down can't be split this way).
        string nl = configText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        patched = "import cxLocator from './" + LocatorFileName + "'" + nl + patched;
        return (patched, null);
    }
}
