using System.Diagnostics;
using System.IO;

namespace CluadeX.Views.Editor;

/// <summary>
/// The only ways the workbench hands a project file to Windows.
///
/// Process.Start(path) with UseShellExecute runs the file's DEFAULT VERB — for .js/.vbs/.wsf/.bat/
/// .cmd/.ps1(on some setups)/.py/.hta/.lnk/.exe that is "run it". The Preview button and Security
/// Shield's "open" both did exactly that with files from whatever repo was opened, so previewing
/// (or auditing!) an untrusted clone executed its scripts. Here, only formats whose default handler
/// is a viewer are shell-opened; everything else is pointed at in Explorer and never launched.
/// </summary>
public static class SafeShell
{
    /// <summary>Formats whose default handler displays rather than executes: a browser, an image
    /// viewer, or a text/markdown editor.</summary>
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".svg", ".md", ".markdown",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
    };

    public static bool CanPreview(string path)
        => !string.IsNullOrEmpty(path) && PreviewableExtensions.Contains(Path.GetExtension(path));

    /// <summary>Open a previewable file with its viewer. Returns false (and does nothing) for any
    /// other kind of file, or when there is no handler.</summary>
    public static bool OpenPreview(string path)
    {
        if (!CanPreview(path) || !File.Exists(path)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;   // no associated viewer
        }
    }

    /// <summary>Show the file selected in an Explorer window — never opens or runs it.</summary>
    public static bool RevealInExplorer(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            // explorer parses its own command line: "/select," must be glued to the quoted path.
            psi.Arguments = File.Exists(full) || Directory.Exists(full)
                ? $"/select,\"{full}\""
                : $"\"{Path.GetDirectoryName(full)}\"";
            using var p = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
