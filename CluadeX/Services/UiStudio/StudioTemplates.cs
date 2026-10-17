using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.UiStudio;

/// <summary>One starter template (its <c>template.json</c>).</summary>
public sealed class StudioTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameTh { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionTh { get; set; } = "";
    /// <summary>"static" (served by CluadeX, no Node) or "vite" (npm install + dev server).</summary>
    public string Kind { get; set; } = "static";
    public string Stack { get; set; } = "";
    /// <summary>A Segoe MDL2 glyph for the gallery card.</summary>
    public string Icon { get; set; } = "";
    public string Accent { get; set; } = "#7C5CFF";
    public int Order { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();

    public bool NeedsNode => Kind == "vite";
    public string DisplayName(string lang) => lang == "th" && NameTh.Length > 0 ? NameTh : Name;
    public string DisplayDescription(string lang) => lang == "th" && DescriptionTh.Length > 0 ? DescriptionTh : Description;
}

/// <summary>
/// The starter templates and the Studio's own web assets (overlay script, Vite locator plugin), embedded in
/// CluadeX.exe as resources named <c>UiStudio/…</c> — the app is portable, nothing is downloaded.
/// </summary>
public static class StudioTemplates
{
    private const string Prefix = "UiStudio/";
    private const string TemplatePrefix = Prefix + "templates/";
    private static readonly Assembly Asm = typeof(StudioTemplates).Assembly;
    private static List<StudioTemplate>? _cache;

    /// <summary>Resource names normalised to forward slashes (RecursiveDir gives backslashes).</summary>
    private static IEnumerable<(string Name, string Resource)> Resources() =>
        Asm.GetManifestResourceNames()
           .Select(r => (Name: r.Replace('\\', '/'), Resource: r))
           .Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal));

    public static string ReadAsset(string name)
    {
        var res = Resources().FirstOrDefault(r => r.Name == Prefix + name).Resource
                  ?? throw new FileNotFoundException("Missing embedded asset " + name);
        using var s = Asm.GetManifestResourceStream(res)!;
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<StudioTemplate> All()
    {
        if (_cache != null) return _cache;
        var list = new List<StudioTemplate>();
        foreach (var (name, res) in Resources())
        {
            if (!name.StartsWith(TemplatePrefix, StringComparison.Ordinal) || !name.EndsWith("/template.json", StringComparison.Ordinal)) continue;
            try
            {
                using var s = Asm.GetManifestResourceStream(res)!;
                var t = JsonSerializer.Deserialize<StudioTemplate>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (t == null) continue;
                t.Id = name[TemplatePrefix.Length..^"/template.json".Length];
                list.Add(t);
            }
            catch { /* a broken manifest hides that template only */ }
        }
        _cache = list.OrderBy(t => t.Order).ThenBy(t => t.Name).ToList();
        return _cache;
    }

    /// <summary>The template's files (relative path → content), with {{PROJECT_NAME}} / {{PROJECT_TITLE}} filled in.</summary>
    public static Dictionary<string, byte[]> Files(string templateId, string projectName)
    {
        string prefix = TemplatePrefix + templateId + "/";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string slug = Slug(projectName);
        // The title lands in HTML text, JSX text and JS strings: keep only characters that are plain text in all of them.
        string title = new string(projectName.Where(c => "{}<>\"'`\\$&".IndexOf(c) < 0 && !char.IsControl(c)).ToArray()).Trim();
        if (title.Length == 0) title = slug;
        foreach (var (name, res) in Resources())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = name[prefix.Length..];
            if (rel == "template.json") continue;
            // npm drops ".gitignore" from published packages; templates keep it as "_gitignore" for the same reason.
            if (Path.GetFileName(rel) == "_gitignore") rel = rel[..^"_gitignore".Length] + ".gitignore";
            using var s = Asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            if (IsText(rel))
            {
                string text = Encoding.UTF8.GetString(bytes)
                    .Replace("{{PROJECT_NAME}}", slug)
                    .Replace("{{PROJECT_TITLE}}", title);
                bytes = new UTF8Encoding(false).GetBytes(text);
            }
            files[rel] = bytes;
        }
        if (files.Count > 0 && All().FirstOrDefault(t => t.Id == templateId) is { NeedsNode: true })
            files[StudioProject.LocatorFileName] = new UTF8Encoding(false).GetBytes(ReadAsset("cluadex-locator.mjs"));
        return files;
    }

    private static bool IsText(string rel) => Path.GetExtension(rel).ToLowerInvariant() is
        ".html" or ".css" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".vue" or ".svelte" or ".json" or ".md" or ".svg" or ".txt" or ".gitignore" or "";

    /// <summary>An npm-safe package name from a display name.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "my-app" : slug;
    }

    /// <summary>
    /// Write the template into <paramref name="folder"/>. The folder must be empty or missing — a template never
    /// overwrites anything. Returns the error key, or null on success.
    /// </summary>
    public static string? CreateProject(string templateId, string folder, string projectName)
    {
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                return "studio.tpl.notEmpty";
            var files = Files(templateId, projectName);
            if (files.Count == 0) return "studio.tpl.missing";
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var (rel, bytes) in files)
            {
                string full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never outside the folder
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
            }
            return null;
        }
        catch (UnauthorizedAccessException) { return "studio.tpl.noPermission"; }
        catch (IOException) { return "studio.tpl.ioError"; }
    }
}
