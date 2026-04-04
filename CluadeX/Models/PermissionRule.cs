namespace CluadeX.Models;

public enum PermAction { Allow, Deny, Ask }

public class PermissionRule
{
    public string Pattern { get; set; } = "*";
    public string Scope { get; set; } = "*";  // "file", "command", "network", "*"
    public PermAction Action { get; set; } = PermAction.Ask;

    /// <summary>Optional tool name constraint. When set, rule only applies to this tool.
    /// Supports Claude Code-style patterns like "run_command", "Bash(npm:*)", etc.</summary>
    public string? ToolName { get; set; }

    public override string ToString()
    {
        string tool = string.IsNullOrEmpty(ToolName) ? "" : $" [{ToolName}]";
        return $"[{Action}] {Scope}{tool}: {Pattern}";
    }
}
