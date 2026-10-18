using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services.UiStudio;

/// <summary>The element the user picked in the page, as the overlay script reports it.</summary>
public sealed class StudioElement
{
    [JsonPropertyName("nodeId")] public int NodeId { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("classes")] public string Classes { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("onlyText")] public bool OnlyText { get; set; }
    /// <summary>The element's own location ("file:line:col"), or null.</summary>
    [JsonPropertyName("src")] public string? Src { get; set; }
    /// <summary>"locator" (CluadeX plugin/server), "react" (React dev info), "ancestor" (nearest located parent) or "none".</summary>
    [JsonPropertyName("srcKind")] public string SrcKind { get; set; } = "none";
    [JsonPropertyName("ancestorSrc")] public string? AncestorSrc { get; set; }
    [JsonPropertyName("parentSrc")] public string? ParentSrc { get; set; }
    [JsonPropertyName("prevSrc")] public string? PrevSrc { get; set; }
    [JsonPropertyName("nextSrc")] public string? NextSrc { get; set; }
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("styles")] public Dictionary<string, string> Styles { get; set; } = new();
    [JsonPropertyName("inlineStyle")] public string InlineStyle { get; set; } = "";
    [JsonPropertyName("attrs")] public Dictionary<string, string> Attrs { get; set; } = new();
    [JsonPropertyName("html")] public string Html { get; set; } = "";
    [JsonPropertyName("childCount")] public int ChildCount { get; set; }

    public bool HasExactSource => Src != null && SrcKind is "locator" or "react";
    public string Label => Tag + (Id.Length > 0 ? "#" + Id : "") + (Classes.Length > 0 ? "." + string.Join('.', Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)) : "");
}

/// <summary>One node of the page's element tree (the Layers panel and the Node Graph's live view).</summary>
public sealed class DomNode
{
    [JsonPropertyName("n")] public int NodeId { get; set; }
    [JsonPropertyName("t")] public string Tag { get; set; } = "";
    [JsonPropertyName("i")] public string? IdAttr { get; set; }
    [JsonPropertyName("c")] public string? Cls { get; set; }
    [JsonPropertyName("s")] public string? Src { get; set; }
    [JsonPropertyName("x")] public string? Text { get; set; }
    [JsonPropertyName("k")] public List<DomNode>? Children { get; set; }

    [JsonIgnore] public DomNode? Parent { get; set; }

    public string Label
    {
        get
        {
            string s = Tag;
            if (!string.IsNullOrEmpty(IdAttr)) s += "#" + IdAttr;
            if (!string.IsNullOrEmpty(Cls)) s += "." + string.Join('.', Cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            return s;
        }
    }

    public string? Snippet => string.IsNullOrWhiteSpace(Text) ? null : Text;
    public IEnumerable<DomNode> Kids => Children ?? Enumerable.Empty<DomNode>();

    public void LinkParents()
    {
        foreach (var k in Kids) { k.Parent = this; k.LinkParents(); }
    }

    public IEnumerable<DomNode> Walk()
    {
        yield return this;
        foreach (var k in Kids)
            foreach (var d in k.Walk()) yield return d;
    }
}

/// <summary>Messages the overlay posts with chrome.webview.postMessage.</summary>
public sealed class OverlayMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("el")] public StudioElement? Element { get; set; }
    [JsonPropertyName("tree")] public DomNode? Tree { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("width")] public string? Width { get; set; }
    [JsonPropertyName("height")] public string? Height { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, MaxDepth = 512 };

    public static OverlayMessage? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<OverlayMessage>(json, Options); }
        catch { return null; }
    }
}
