namespace CluadeX.Models;

public enum PermAction { Allow, Deny, Ask }

public class PermissionRule
{
    public string Pattern { get; set; } = "*";
    public string Scope { get; set; } = "*";  // "file", "command", "network", "*"
    public PermAction Action { get; set; } = PermAction.Ask;

    public override string ToString() => $"[{Action}] {Scope}: {Pattern}";
}
