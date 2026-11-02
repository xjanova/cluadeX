using System.IO;
using CluadeX.Services.UiStudio.Graph;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// Reads a project's files and draws what is really there, one reader per language: web pages, styles and JS/TS
/// modules (<see cref="WebReader"/>), C# through Roslyn (<see cref="CSharpReader"/>) and its XAML
/// (<see cref="XamlReader"/>), Dart/Flutter (<see cref="DartReader"/>), PHP/Laravel (<see cref="PhpReader"/>) and
/// Python (<see cref="PythonReader"/>). Code inside comments and strings never makes an edge; an edge exists where
/// the code names the other file, resolved the way that language resolves it, and carries the lines that say so.
/// What could only be matched by a name or a convention is kept, marked inferred, with the reason.
/// The read is bounded (<see cref="ProjectScan"/>) and checks the cancellation token between files.
/// </summary>
public static class ProjectGraphBuilder
{
    public const int DefaultMaxFiles = 2500;

    public static ProjectGraph Build(string root, bool includePackages, CancellationToken ct, int maxFiles = DefaultMaxFiles)
    {
        var graph = new ProjectGraph();
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(fullRoot)) return graph;
        var scanWatch = System.Diagnostics.Stopwatch.StartNew();
        var scan = ProjectScan.Read(fullRoot, maxFiles, ct);
        long scanMs = scanWatch.ElapsedMilliseconds;
        graph.Truncated = scan.Truncated;
        graph.TruncatedWhy = scan.TruncatedWhy;
        graph.Skipped = scan.Skipped;

        foreach (var f in scan.Files)
        {
            int slash = f.Id.LastIndexOf('/');
            graph.Add(new GraphNode
            {
                Id = f.Id, Label = Path.GetFileName(f.Full), Detail = slash > 0 ? f.Id[..slash] : null, Folder = slash > 0 ? f.Id[..slash] : "",
                Kind = GraphNodeKind.Script, FullPath = f.Full, Lines = f.Lines.Count,
            });
        }

        var cx = new GraphContext(fullRoot, graph, scan, includePackages, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<string> { $"scan {scanMs} ms" };
        void Phase(string name, Action read)
        {
            ct.ThrowIfCancellationRequested();
            sw.Restart();
            read();
            if (sw.ElapsedMilliseconds > 0) times.Add($"{name} {sw.ElapsedMilliseconds} ms");
        }
        CSharpModel? cs = null;
        Phase("web", () => WebReader.Read(cx));
        if (cx.Files.Any(f => f.Ext == ".cs")) Phase("c#", () => cs = CSharpReader.Read(cx));
        if (cs != null) times.Add($"{cs.Rebound} of {cx.Files.Count(f => f.Ext == ".cs")} .cs bound");
        Phase("xaml", () => XamlReader.Read(cx, cs));
        Phase("dart", () => DartReader.Read(cx));
        Phase("php", () => PhpReader.Read(cx));
        Phase("python", () => PythonReader.Read(cx));
        cx.Finish();
        graph.Timings = string.Join(" · ", times);
        return graph;
    }
}
