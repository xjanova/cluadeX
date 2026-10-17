using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio;

public enum GraphNodeKind { Page, Component, Script, Style, Asset, Package, Element }
public enum GraphEdgeKind { Imports, Renders, Loads, Links, Contains }

public sealed class GraphNode
{
    public string Id { get; init; } = "";
    public string Label { get; set; } = "";
    public string? Detail { get; set; }
    public GraphNodeKind Kind { get; init; }
    /// <summary>The file (full path) for a project node; the source location for a live element.</summary>
    public string? FullPath { get; init; }
    public string? Src { get; init; }
    public int? DomId { get; init; }
    public int Lines { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 176;
    public double Height { get; set; } = 44;
    public int Layer { get; set; }
    public bool Collapsed { get; set; }
    public bool Hidden { get; set; }
}

public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind);

public sealed class ProjectGraph
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public Dictionary<string, GraphNode> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Truncated { get; set; }

    public GraphNode Add(GraphNode n)
    {
        if (ById.TryGetValue(n.Id, out var existing)) return existing;
        Nodes.Add(n);
        ById[n.Id] = n;
        return n;
    }
}

/// <summary>
/// Reads a project's files and draws what is really there: which page loads which script and stylesheet, which
/// module imports which, and which component renders which (a JSX/Vue/Svelte tag that names an imported
/// component). Nothing is guessed from names — an edge exists only where the code says so — and every node opens
/// its own file.
/// </summary>
public static class ProjectGraphBuilder
{
    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro" };
    private static readonly HashSet<string> StyleExt = new(StringComparer.OrdinalIgnoreCase)
        { ".css", ".scss", ".sass", ".less" };
    private static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro", ".css", ".scss", ".sass", ".less" };

    private static readonly Regex ImportFrom = new(
        @"(?:^|[;\s])import\s+(?:type\s+)?(?<what>[\w*{}\s,$]+?)\s+from\s+['""](?<p>[^'""]+)['""]|(?:^|[;\s])import\s+['""](?<p>[^'""]+)['""]|(?:require|import)\(\s*['""](?<p>[^'""]+)['""]\s*\)|export\s+(?:\*|\{[^}]*\})\s+from\s+['""](?<p>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CssImport = new(@"@import\s+(?:url\()?['""]?(?<p>[^'"")\s;]+)", RegexOptions.Compiled);
    private static readonly Regex HtmlRef = new(
        @"<(?:script)[^>]*\ssrc=['""](?<s>[^'""]+)['""]|<link[^>]*\shref=['""](?<l>[^'""]+\.css[^'""]*)['""]|<a[^>]*\shref=['""](?<a>[^'""#?:]+\.html?)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagUse = new(@"<(?<n>[A-Z][\w.]*)[\s/>]", RegexOptions.Compiled);

    public static ProjectGraph Build(string root, bool includePackages, CancellationToken ct, int maxFiles = 1500)
    {
        var graph = new ProjectGraph();
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = SourceSearch.ProjectFiles(root, All, maxFiles + 1).ToList();
        if (files.Count > maxFiles) { graph.Truncated = true; files = files.Take(maxFiles).ToList(); }
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        string Rel(string full) => full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full[fullRoot.Length..].Replace('\\', '/') : full;

        GraphNode NodeFor(string full, string content)
        {
            string ext = Path.GetExtension(full);
            string name = Path.GetFileNameWithoutExtension(full);
            var kind = ext is ".html" or ".htm" ? GraphNodeKind.Page
                : StyleExt.Contains(ext) ? GraphNodeKind.Style
                : ext is ".vue" or ".svelte" or ".astro" || (ext is ".jsx" or ".tsx" && name.Length > 0 && char.IsUpper(name[0])) ? GraphNodeKind.Component
                : GraphNodeKind.Script;
            int lines = 1;
            foreach (char c in content) if (c == '\n') lines++;
            return graph.Add(new GraphNode
            {
                Id = Rel(full), Label = Path.GetFileName(full), Detail = Path.GetDirectoryName(Rel(full))?.Replace('\\', '/'),
                Kind = kind, FullPath = full, Lines = lines,
            });
        }

        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(f).Length > 800_000) continue;
                contents[f] = File.ReadAllText(f);
                NodeFor(f, contents[f]);
            }
            catch { /* unreadable: no node */ }
        }

        string[] tryExt = { "", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".vue", ".svelte", ".css", "/index.ts", "/index.tsx", "/index.js", "/index.jsx", "/index.vue" };

        // A relative, "/root" or "@/" (Vite/Vue alias for src/) import → the project file it means; null for a package.
        // HTML and CSS references are relative to the file by default ("styles.css"); in JS a bare name is a package.
        string? ResolveSpec(string fromDir, string spec, bool relativeByDefault)
        {
            string clean = spec.Split('?', '#')[0];
            string? baseFull = clean.StartsWith("./", StringComparison.Ordinal) || clean.StartsWith("../", StringComparison.Ordinal)
                               || relativeByDefault && !clean.StartsWith('/')
                ? Path.Combine(fromDir, clean)
                : clean.StartsWith("@/", StringComparison.Ordinal) || clean.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(fullRoot, "src", clean[2..])
                : clean.StartsWith('/') ? Path.Combine(fullRoot, clean.TrimStart('/'))
                : null;
            if (baseFull == null) return null;
            foreach (var e in tryExt)
            {
                string candidate;
                try { candidate = Path.GetFullPath(baseFull.Replace('/', Path.DirectorySeparatorChar) + e.Replace('/', Path.DirectorySeparatorChar)); }
                catch { return null; }
                if (fileSet.Contains(candidate)) return candidate;
            }
            return null;
        }

        void AddEdge(string fromId, string fromDir, string spec, GraphEdgeKind kind, bool relativeByDefault = false)
        {
            if (string.IsNullOrWhiteSpace(spec) || spec.StartsWith("http", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("data:", StringComparison.Ordinal)) return;
            string? target = ResolveSpec(fromDir, spec, relativeByDefault);
            string toId;
            if (target != null)
            {
                toId = Rel(target);
                if (!graph.ById.ContainsKey(toId)) return;
            }
            else
            {
                if (!includePackages || relativeByDefault || spec.StartsWith('.') || spec.StartsWith('/')) return;
                // "@scope/pkg/sub" → "@scope/pkg"; "pkg/sub" → "pkg"
                var parts = spec.Split('/');
                string pkg = spec.StartsWith('@') && parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0];
                toId = "pkg:" + pkg;
                graph.Add(new GraphNode { Id = toId, Label = pkg, Detail = "package", Kind = GraphNodeKind.Package });
            }
            if (string.Equals(fromId, toId, StringComparison.OrdinalIgnoreCase)) return;
            if (!graph.Edges.Any(x => x.From == fromId && x.To == toId))
                graph.Edges.Add(new GraphEdge(fromId, toId, kind));
            else if (kind == GraphEdgeKind.Renders)
            {
                int i = graph.Edges.FindIndex(x => x.From == fromId && x.To == toId);
                graph.Edges[i] = graph.Edges[i] with { Kind = GraphEdgeKind.Renders };
            }
        }

        foreach (var (file, content) in contents)
        {
            ct.ThrowIfCancellationRequested();
            string from = Rel(file);
            string ext = Path.GetExtension(file);
            string dir = Path.GetDirectoryName(file)!;

            if (ext is ".html" or ".htm")
            {
                foreach (Match m in HtmlRef.Matches(content))
                {
                    if (m.Groups["s"].Success) AddEdge(from, dir, m.Groups["s"].Value, GraphEdgeKind.Loads, true);
                    else if (m.Groups["l"].Success) AddEdge(from, dir, m.Groups["l"].Value, GraphEdgeKind.Loads, true);
                    else if (m.Groups["a"].Success) AddEdge(from, dir, m.Groups["a"].Value, GraphEdgeKind.Links, true);
                }
                continue;
            }
            if (StyleExt.Contains(ext))
            {
                foreach (Match m in CssImport.Matches(content)) AddEdge(from, dir, m.Groups["p"].Value, GraphEdgeKind.Imports, true);
                continue;
            }

            // Identifiers used as tags (<Header />, <Card>) in JSX, Vue and Svelte templates alike.
            var usedTags = new HashSet<string>(TagUse.Matches(content).Select(m => m.Groups["n"].Value.Split('.')[0]), StringComparer.Ordinal);
            foreach (Match m in ImportFrom.Matches(content))
            {
                var kind = GraphEdgeKind.Imports;
                if (m.Groups["what"].Success)
                {
                    var names = Regex.Matches(m.Groups["what"].Value, @"[A-Za-z_$][\w$]*").Select(x => x.Value);
                    if (names.Any(usedTags.Contains)) kind = GraphEdgeKind.Renders;
                }
                AddEdge(from, dir, m.Groups["p"].Value, kind);
            }
        }
        return graph;
    }
}
