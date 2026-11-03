using System.Windows;
using CluadeX.Services.UiStudio;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// What draws the Node Graph: the GPU view (<see cref="NodeGraphGpu"/>, WebGL in a WebView2) when the machine has it,
/// else the WPF canvas (<see cref="NodeGraphCanvas"/>). The page sets the same things on either and hears the same
/// picks back.
/// </summary>
public interface INodeGraphSurface
{
    ProjectGraph? Graph { get; }
    GraphNode? Selected { get; }
    ISet<string> Highlights { get; set; }
    /// <summary>Nodes whose file changed lately (id → when, UTC): drawn with a fading glow.</summary>
    IReadOnlyDictionary<string, DateTime>? Changed { get; set; }
    TimeSpan ChangeGlow { get; set; }
    /// <summary>Files an agent tool reads or writes now (id → when, write). The GPU view runs data along their wires.</summary>
    IReadOnlyDictionary<string, (DateTime At, bool Write)>? Active { get; set; }
    ISet<GraphEdgeKind> HiddenKinds { get; set; }
    bool ShowInferred { get; set; }
    bool GroupByFolder { get; set; }
    GraphEdge? SelectedEdge { get; set; }

    void SetGraph(ProjectGraph? graph, GraphNode? selected);
    /// <summary>Only the glow and the work on files changed (no new graph, no new pick).</summary>
    void SetActivity();
    void FitToView();
    void CenterOn(GraphNode n);

    event Action<GraphNode?>? NodeSelected;
    event Action<GraphNode>? NodeActivated;
    event Action<GraphNode>? NodeMoved;
    event Action<GraphNode, Point>? NodeContextMenu;
    event Action<GraphEdge?>? EdgeSelected;
    event Action<GraphEdge>? EdgeActivated;
}
