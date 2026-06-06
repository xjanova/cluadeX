namespace CluadeX.Models;

/// <summary>Kind of a single line in a rendered edit/write diff.</summary>
public enum EditDiffKind
{
    Context,
    Add,
    Remove,
    Hunk,
}

/// <summary>One displayable line of an edit diff (built once, then bound read-only).</summary>
public class EditDiffLine
{
    public EditDiffKind Kind { get; set; }

    /// <summary>The raw line text WITHOUT any +/- marker.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Text with a leading marker, for monospace display.</summary>
    public string Display => Kind switch
    {
        EditDiffKind.Add => "+ " + Text,
        EditDiffKind.Remove => "- " + Text,
        EditDiffKind.Hunk => Text,
        _ => "  " + Text,
    };
}

/// <summary>The result of diffing two versions of a file's content (for the chat trust diff).</summary>
public class EditDiffResult
{
    public List<EditDiffLine> Lines { get; set; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }

    /// <summary>True when the rendered line list was capped (the +/- counts are still totals).</summary>
    public bool Truncated { get; set; }

    /// <summary>Short stat like "+12  -3" for a header chip.</summary>
    public string Stat => $"+{Added}  -{Removed}" + (Truncated ? "  (diff truncated)" : "");

    public bool HasChanges => Added > 0 || Removed > 0;
}
