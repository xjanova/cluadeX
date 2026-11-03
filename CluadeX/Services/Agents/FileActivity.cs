namespace CluadeX.Services.Agents;

/// <summary>
/// A file an agent tool is reading or changing right now (full path; <c>write</c> = an edit or a write). The Node Graph
/// lights that node and runs data along its wires while it lasts, so the owner can watch where the AI works. Edits a CLI
/// agent makes with its own tools still show through the project's file watcher; only its reads are not seen here.
/// Raised on the tool's thread; a listener must not throw back into the run.
/// </summary>
public static class FileActivity
{
    public static event Action<string, bool>? Touched;

    public static void Note(string? fullPath, bool write)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        try { Touched?.Invoke(fullPath, write); } catch { /* a view's refresh must not break the run */ }
    }
}
