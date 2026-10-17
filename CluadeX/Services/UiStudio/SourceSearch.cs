using System.IO;
namespace CluadeX.Services.UiStudio;

/// <summary>A place in the source that probably wrote an element the page could not locate exactly.</summary>
public sealed record SourceCandidate(string FullPath, string RelativePath, int Line, string Preview, int Score)
{
    public string Display => $"{RelativePath}:{Line}";
}

/// <summary>
/// For an element with no exact location (rendered by a library, built in a loop from data, a framework without
/// the locator), search the project's markup/code for its id, its text and its most distinctive class. The result
/// is a hint list for the user — the Studio never writes to a guessed place.
/// </summary>
public static class SourceSearch
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".jsx", ".tsx", ".js", ".ts", ".vue", ".svelte", ".astro", ".mdx", ".php", ".cshtml", ".razor", ".erb", ".hbs", ".njk",
    };

    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "dist", "build", "out", ".git", ".next", ".nuxt", ".svelte-kit", ".output", "coverage", "bin", "obj", "vendor", ".cache", ".vite", ".turbo",
    };

    public static IEnumerable<string> ProjectFiles(string root, ISet<string>? extensions = null, int max = 4000)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int count = 0;
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir); dirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                if (extensions != null && !extensions.Contains(Path.GetExtension(f))) continue;
                yield return f;
                if (++count >= max) yield break;
            }
            foreach (var d in dirs)
            {
                string name = Path.GetFileName(d);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                stack.Push(d);
            }
        }
    }

    public static List<SourceCandidate> Find(string root, StudioElement el, CancellationToken ct)
    {
        var needles = new List<(string Text, int Score)>();
        if (el.Id.Length > 1)
        {
            needles.Add(($"id=\"{el.Id}\"", 6));
            needles.Add(($"id='{el.Id}'", 6));
        }
        string text = el.OnlyText ? el.Text.Trim() : "";
        if (text.Length >= 3)
            needles.Add((text.Length > 48 ? text[..48] : text, 4));
        var cls = el.Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(c => c.Length >= 4 && !c.Contains(':')).OrderByDescending(c => c.Length).FirstOrDefault();
        if (cls != null) needles.Add((cls, 1));
        if (needles.Count == 0) return new();

        var results = new List<SourceCandidate>();
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in ProjectFiles(root, Extensions))
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1_000_000) continue;
                content = File.ReadAllText(file);
            }
            catch { continue; }

            foreach (var (needle, score) in needles)
            {
                int at = content.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) continue;
                var (line, _) = SourceTagEditor.LineColOf(content, at);
                int ls = content.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
                int le = content.IndexOf('\n', at);
                if (le < 0) le = content.Length;
                string preview = content[ls..le].Trim();
                if (preview.Length > 120) preview = preview[..120] + "…";
                string rel = file.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? file[fullRoot.Length..].Replace('\\', '/') : file;
                var existing = results.FindIndex(r => r.FullPath == file && r.Line == line);
                if (existing >= 0) results[existing] = results[existing] with { Score = results[existing].Score + score };
                else results.Add(new SourceCandidate(file, rel, line, preview, score));
            }
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.RelativePath).Take(8).ToList();
    }
}
