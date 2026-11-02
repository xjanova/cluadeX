namespace CluadeX.Services.UiStudio;

public enum GraphNodeKind { Page, Component, Script, Style, Asset, Package, Element, Code }

/// <summary>What one file does to another. Appended only (saved filters and colours key on the names).</summary>
public enum GraphEdgeKind { Imports, Renders, Loads, Links, Contains, Calls, Inherits, Implements, Routes, Registers, Binds, CodeBehind, References }

public sealed class GraphNode
{
    public string Id { get; init; } = "";
    public string Label { get; set; } = "";
    public string? Detail { get; set; }
    public GraphNodeKind Kind { get; set; }
    /// <summary>The file (full path) for a project node; the source location for a live element.</summary>
    public string? FullPath { get; init; }
    public string? Src { get; init; }
    public int? DomId { get; init; }
    public int Lines { get; init; }
    /// <summary>The folder a project node sits in ("" at the root) — what "group by folder" groups on.</summary>
    public string Folder { get; init; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 176;
    public double Height { get; set; } = 44;
    public int Layer { get; set; }
    public bool Collapsed { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>Where the code says so: the file (node id), the 1-based line, and that line's text.</summary>
public sealed record GraphEvidence(string File, int Line, string Text);

/// <summary>
/// One connection of one kind. <see cref="Inferred"/> edges were matched by a name or a convention, not resolved —
/// the canvas draws them dashed and <see cref="Why"/> says what was matched. Every edge carries the lines that made
/// it (<see cref="Evidence"/>, first one first).
/// </summary>
public sealed class GraphEdge
{
    public GraphEdge(string from, string to, GraphEdgeKind kind) { From = from; To = to; Kind = kind; }
    public string From { get; }
    public string To { get; }
    public GraphEdgeKind Kind { get; }
    public bool Inferred { get; set; }
    public string? Why { get; set; }
    /// <summary>A short name for what the edge is, where the kind alone does not say ("GET /users", "partial").</summary>
    public string? Label { get; set; }
    public List<GraphEvidence> Evidence { get; } = new();
    /// <summary>How many places say it (Evidence keeps the first few).</summary>
    public int Count { get; set; }
    public GraphEvidence? First => Evidence.Count > 0 ? Evidence[0] : null;
    public override string ToString() => $"{From} -{Kind}{(Inferred ? "?" : "")}-> {To}" + (First is { } e ? $" @{e.File}:{e.Line}" : "");
}

public sealed class ProjectGraph
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public Dictionary<string, GraphNode> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Truncated { get; set; }
    /// <summary>Why the scan stopped short (file count, total size), for the summary line.</summary>
    public string? TruncatedWhy { get; set; }
    /// <summary>Files left out on purpose: too big, binary, or generated.</summary>
    public int Skipped { get; set; }
    /// <summary>How long each part of the read took (for the Debug Log).</summary>
    public string Timings { get; set; } = "";

    public GraphNode Add(GraphNode n)
    {
        if (ById.TryGetValue(n.Id, out var existing)) return existing;
        Nodes.Add(n);
        ById[n.Id] = n;
        return n;
    }
}
