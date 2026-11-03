using System.IO;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>What every language reader shares while one graph is built: the files read, the nodes, and the one way
/// to add an edge (deduplicated by from, to and kind, keeping the evidence).</summary>
internal sealed class GraphContext
{
    public const int MaxEvidence = 6;

    public GraphContext(string fullRoot, ProjectGraph graph, ProjectScan.Result scan, bool includePackages, CancellationToken ct)
    {
        FullRoot = fullRoot;
        Graph = graph;
        IncludePackages = includePackages;
        Ct = ct;
        Files = scan.Files;
        Configs = scan.Configs;
        foreach (var f in scan.Files) { ByFull[f.Full] = f; ById[f.Id] = f; }
    }

    /// <summary>The project folder, ending in a separator.</summary>
    public string FullRoot { get; }
    public ProjectGraph Graph { get; }
    public bool IncludePackages { get; }
    public CancellationToken Ct { get; }
    public List<SourceFile> Files { get; }
    public List<SourceFile> Configs { get; }
    public Dictionary<string, SourceFile> ByFull { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SourceFile> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string, GraphEdgeKind), GraphEdge> _edges = new();

    public IEnumerable<SourceFile> OfExt(params string[] exts) => Files.Where(f => exts.Contains(f.Ext));

    /// <summary>The project file at this path (any separators, any ./..), or null when it is not one the graph read.</summary>
    public SourceFile? FileAt(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
            return ByFull.TryGetValue(full, out var f) ? f : null;
        }
        catch { return null; }
    }

    public string Rel(string full) => full.StartsWith(FullRoot, StringComparison.OrdinalIgnoreCase) ? full[FullRoot.Length..].Replace('\\', '/') : full;

    /// <summary>Add (or strengthen) an edge. A confirmed sighting of an edge first seen as inferred makes it confirmed.</summary>
    public GraphEdge? Edge(SourceFile from, string toId, GraphEdgeKind kind, int line, bool inferred = false, string? why = null, string? label = null,
        SourceFile? evidenceIn = null)
    {
        if (string.Equals(from.Id, toId, StringComparison.OrdinalIgnoreCase) || !Graph.ById.ContainsKey(toId)) return null;
        var key = (from.Id.ToLowerInvariant(), toId.ToLowerInvariant(), kind);
        if (!_edges.TryGetValue(key, out var e))
        {
            e = new GraphEdge(from.Id, Graph.ById[toId].Id, kind) { Inferred = inferred, Why = inferred ? why : null, Label = label };
            _edges[key] = e;
            Graph.Edges.Add(e);
        }
        else if (e.Inferred && !inferred)
        {
            e.Inferred = false;
            e.Why = null;
            e.Label ??= label;
            // the confirmed line goes first: it is the one that proves the edge
            e.Evidence.Insert(0, Ev(evidenceIn ?? from, line));
            e.Count++;
            Trim(e);
            return e;
        }
        e.Label ??= label;
        e.Count++;
        var src = evidenceIn ?? from;
        if (line > 0 && e.Evidence.Count < MaxEvidence && !e.Evidence.Any(x => x.Line == line && x.File == src.Id)) e.Evidence.Add(Ev(src, line));
        return e;
    }

    public GraphEdge? Edge(SourceFile from, SourceFile to, GraphEdgeKind kind, int line, bool inferred = false, string? why = null, string? label = null) =>
        Edge(from, to.Id, kind, line, inferred, why, label);

    private static GraphEvidence Ev(SourceFile f, int line) => new(f.Id, line, f.Lines.Text(line));

    private static void Trim(GraphEdge e)
    {
        for (int i = e.Evidence.Count - 1; i > 0; i--)
            if (e.Evidence.Take(i).Any(x => x.Line == e.Evidence[i].Line && x.File == e.Evidence[i].File)) e.Evidence.RemoveAt(i);
        while (e.Evidence.Count > MaxEvidence) e.Evidence.RemoveAt(e.Evidence.Count - 1);
    }

    /// <summary>A package the code imports (shown only when the user asks for packages).</summary>
    public string? Package(string name, string detail)
    {
        if (!IncludePackages || name.Length == 0) return null;
        string id = "pkg:" + name;
        Graph.Add(new GraphNode { Id = id, Label = name, Detail = detail, Kind = GraphNodeKind.Package, Folder = "(packages)" });
        return id;
    }

    /// <summary>
    /// Several kinds between the same two files say the same thing at different strengths: an import of a class that
    /// is then extended is an "inherits", not an "imports" plus an "inherits". The weak kinds (imports, references)
    /// fold into the strongest confirmed kind of the pair — their lines join its evidence — so a pair draws as one
    /// wire per thing it really does.
    /// </summary>
    public void Finish()
    {
        static bool Weak(GraphEdgeKind k) => k is GraphEdgeKind.Imports or GraphEdgeKind.References;
        var drop = new HashSet<GraphEdge>();
        foreach (var pair in Graph.Edges.GroupBy(e => (e.From.ToLowerInvariant(), e.To.ToLowerInvariant())))
        {
            var list = pair.ToList();
            if (list.Count < 2) continue;
            var strong = list.Where(e => !Weak(e.Kind)).OrderBy(e => e.Inferred).ThenBy(e => (int)e.Kind).FirstOrDefault();
            foreach (var weak in list.Where(e => Weak(e.Kind)))
            {
                GraphEdge? into = strong;
                if (into == null && weak.Kind == GraphEdgeKind.References) into = list.FirstOrDefault(e => e.Kind == GraphEdgeKind.Imports);
                if (into == null || into.Inferred && !weak.Inferred) continue;   // never hide a proof under a guess
                foreach (var ev in weak.Evidence)
                    if (into.Evidence.Count < MaxEvidence && !into.Evidence.Any(x => x.Line == ev.Line && x.File == ev.File)) into.Evidence.Add(ev);
                into.Count += weak.Count;
                drop.Add(weak);
            }
        }
        if (drop.Count > 0) Graph.Edges.RemoveAll(drop.Contains);
    }
}
