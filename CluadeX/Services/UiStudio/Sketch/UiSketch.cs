namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>What one block of a simulated screen is.</summary>
public enum SketchKind
{
    Column, Row, Wrap, Stack,
    Text, Heading, Caption,
    Button, IconButton, Fab,
    Input, Toggle, Slider, Chip, Badge, Icon, Image, Avatar,
    List, Grid, Tile, Card, Table, Tabs, NavBar, AppBar, Dialog,
    Divider, Spacer, Progress, Box,
}

/// <summary>
/// One block of a simulated screen — a wireframe of what the source would draw. Every block remembers the line
/// that wrote it (<see cref="File"/>:<see cref="Line"/>) and, when it was drawn by a shared widget from another
/// file, the line of the screen that used that widget (<see cref="UseFile"/>:<see cref="UseLine"/>) — a click on the
/// simulated screen opens the place the screen's author would edit.
/// </summary>
public sealed class SketchNode
{
    public SketchKind Kind { get; init; }
    public string? Text { get; set; }
    /// <summary>A second line (a tile's subtitle, a header's subtitle).</summary>
    public string? Text2 { get; set; }
    /// <summary>An icon's name as the code spells it ("home", "house", "magnifyingGlass").</summary>
    public string? Icon { get; set; }
    /// <summary>The widget in the source ("TpCard", "ListView.builder").</summary>
    public string? Widget { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
    public string? UseFile { get; set; }
    public int UseLine { get; set; }
    public List<SketchNode> Children { get; } = new();
    /// <summary>Labels of a tab strip / nav bar / segmented button / table header.</summary>
    public List<string> Items { get; } = new();
    /// <summary>Icon names matching <see cref="Items"/> (nav bars).</summary>
    public List<string?> ItemIcons { get; } = new();
    public int Columns { get; set; }
    /// <summary>An explicit height in logical pixels (spacers, images), 0 = natural.</summary>
    public double Height { get; set; }
    /// <summary>Button: "filled", "tonal", "outlined", "text"; Progress: "linear"; Input: "search"; Card: "box".</summary>
    public string? Variant { get; set; }
    /// <summary>Drawn over the content (a floating action button) rather than in the flow.</summary>
    public bool Floating { get; set; }
    /// <summary>Placed by Positioned inside a Stack: it takes no room of its own (a glow, a badge, a corner button).</summary>
    public bool Overlay { get; set; }

    /// <summary>Has something to read or press (not just a decoration).</summary>
    public bool HasContent => Walk().Any(n => n.Text != null && n.Text != "…" || n.Kind is SketchKind.Button or SketchKind.IconButton
        or SketchKind.Input or SketchKind.Toggle or SketchKind.Chip or SketchKind.Tile or SketchKind.Fab or SketchKind.Badge);

    public int Count() => 1 + Children.Sum(c => c.Count());

    public IEnumerable<SketchNode> Walk()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Walk()) yield return d;
    }

    /// <summary>The place to open for this block: where the screen uses it, else where it is written.</summary>
    public (string? File, int Line) Target => UseFile != null ? (UseFile, UseLine) : (File, Line);

    public override string ToString() => $"{Kind}{(Text != null ? " \"" + Text + "\"" : "")}{(Widget != null ? " <" + Widget + ">" : "")}";
}

/// <summary>Colours of the simulated app, read from its theme (seed colour, background, dark or light).</summary>
public sealed class SketchTheme
{
    public uint Accent { get; set; } = 0xFF6750A4;   // Material 3's default seed
    public uint Background { get; set; } = 0xFFFFFBFE;
    public uint Surface { get; set; } = 0xFFF3EDF7;
    public uint OnBackground { get; set; } = 0xFF1C1B1F;
    public bool Dark { get; set; }

    public static SketchTheme DarkDefault(uint accent) => new()
    {
        Accent = accent, Background = 0xFF141218, Surface = 0xFF211F26, OnBackground = 0xFFE6E0E9, Dark = true,
    };
}

/// <summary>A simulated screen: its blocks, the app's colours, and the title it shows.</summary>
public sealed class ScreenSketch
{
    public SketchNode Root { get; init; } = new() { Kind = SketchKind.Column };
    public SketchTheme Theme { get; init; } = new();
    public string? Title { get; init; }
    /// <summary>Widgets the sketch could not read (drawn as labelled boxes) — shown so the user knows it is approximate.</summary>
    public int Unknown { get; init; }
}
