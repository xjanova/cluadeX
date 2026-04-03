namespace CluadeX.Models;

public class PluginInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>
/// A plugin from the curated catalog that can be installed with one click.
/// Generates a proper manifest.json in the plugins directory.
/// </summary>
public class CatalogPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameTh { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionTh { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Category { get; set; } = "general";
    public string Icon { get; set; } = "\U0001F9E9";
    public string Author { get; set; } = "CluadeX Team";
    public string[] Tags { get; set; } = [];
    public bool IsInstalled { get; set; }

    /// <summary>Hook events this plugin registers for (informational).</summary>
    public string[] HookEvents { get; set; } = [];

    /// <summary>Brief explanation of what the plugin's hooks/commands do.</summary>
    public string HookSummary { get; set; } = "";
    public string HookSummaryTh { get; set; } = "";
}
