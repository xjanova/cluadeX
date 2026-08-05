using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Backend for the Code Editor page. Builds a recursive file tree
/// rooted at the current FileSystemService.WorkingDirectory, opens files
/// into in-memory tabs, and writes them back on save.
///
/// Tree builds are throttled (skips well-known noise folders like node_modules,
/// bin, obj, .git, etc) and lazy-load deep subtrees so a 50k-file repo
/// doesn't freeze the UI on first render.
/// </summary>
public class CodeWorkspaceService
{
    private readonly FileSystemService _fs;
    private readonly GitService _git;

    public CodeWorkspaceService(FileSystemService fs, GitService git)
    {
        _fs = fs;
        _git = git;
    }

    public string WorkingDirectory => _fs.WorkingDirectory;
    public bool HasWorkingDirectory => _fs.HasWorkingDirectory;

    // Folders we DON'T descend into — they explode the tree size.
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out",
        ".vs", ".idea", ".vscode-test", "__pycache__", ".pytest_cache",
        "target", "venv", ".venv", "env", ".env",
        "packages", ".nuget",
    };

    // Cap files-per-folder so a huge generated folder doesn't enumerate millions of entries.
    private const int MaxEntriesPerFolder = 2000;

    /// <summary>Build a lazy tree from the working directory root.</summary>
    public List<FileTreeNode> BuildTree()
    {
        var result = new List<FileTreeNode>();
        if (!HasWorkingDirectory) return result;

        // Root entry — represents the working directory itself; expanded by default
        var root = new FileTreeNode
        {
            FullPath = WorkingDirectory,
            Name = Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/')) ?? WorkingDirectory,
            IsDirectory = true,
            IsExpanded = true,
        };
        PopulateChildren(root);
        result.Add(root);
        return result;
    }

    /// <summary>Synchronously populate a folder's children (cheap because we cap + filter).</summary>
    public void PopulateChildren(FileTreeNode folder)
    {
        if (!folder.IsDirectory) return;
        folder.Children.Clear();

        try
        {
            var dirs = Directory.EnumerateDirectories(folder.FullPath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name)) return false;
                    if (name.StartsWith(".") && name != ".cluadex" && name != ".claude") return false;
                    return !ExcludedFolders.Contains(name);
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntriesPerFolder);

            foreach (var d in dirs)
            {
                folder.Children.Add(new FileTreeNode
                {
                    FullPath = d,
                    Name = Path.GetFileName(d) ?? d,
                    IsDirectory = true,
                });
            }

            var files = Directory.EnumerateFiles(folder.FullPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntriesPerFolder);

            foreach (var f in files)
            {
                folder.Children.Add(new FileTreeNode
                {
                    FullPath = f,
                    Name = Path.GetFileName(f),
                    IsDirectory = false,
                });
            }
        }
        catch
        {
            // Permission denied / IO error — just leave it empty
        }
    }

    /// <summary>Open a file and return its tab — caller pushes to ObservableCollection.</summary>
    public async Task<OpenFileTab?> OpenFileAsync(string fullPath, CancellationToken ct = default)
    {
        if (!File.Exists(fullPath)) return null;

        // Cap at 2 MB so opening accidental binaries doesn't freeze
        var fi = new FileInfo(fullPath);
        if (fi.Length > 2 * 1024 * 1024) return null;

        string content;
        try
        {
            // Detect binary by checking for NUL bytes in first 1 KB
            byte[] sample = new byte[Math.Min(1024, (int)fi.Length)];
            await using (var fs = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await fs.ReadAsync(sample, 0, sample.Length, ct);
            }
            for (int i = 0; i < sample.Length; i++)
            {
                if (sample[i] == 0)
                {
                    // Binary — refuse to open in text editor
                    return null;
                }
            }
            content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        }
        catch
        {
            return null;
        }

        var tab = new OpenFileTab { FullPath = fullPath };
        tab.MarkOpened(content);
        return tab;
    }

    public async Task<bool> SaveTabAsync(OpenFileTab tab, CancellationToken ct = default)
    {
        if (tab == null || string.IsNullOrEmpty(tab.FullPath)) return false;
        try
        {
            await File.WriteAllTextAsync(tab.FullPath, tab.Content, Encoding.UTF8, ct);
            tab.MarkSaved();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─── Git status enrichment (best-effort) ──────────────────────────
    /// <summary>Run git status and tag matching nodes with M/A/U/? badges.</summary>
    public async Task EnrichGitStatusAsync(IEnumerable<FileTreeNode> roots)
    {
        if (!HasWorkingDirectory) return;
        try
        {
            var st = await _git.StatusAsync();
            if (!st.Success) return;

            var map = new Dictionary<string, (string badge, string color)>();
            foreach (var line in st.Output.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length < 4) continue;
                string code = l.Substring(0, 2).Trim();
                string relPath = l.Substring(3).Trim();
                if (relPath.StartsWith("\"") && relPath.EndsWith("\"")) relPath = relPath[1..^1];
                string full = Path.GetFullPath(Path.Combine(WorkingDirectory, relPath));

                string badge = code switch
                {
                    "M" or "MM" or "AM" or "RM" => "M",
                    "A" or "AA" or "AD" => "A",
                    "??" => "U",
                    "D" or "DD" => "D",
                    "R" or "RR" => "R",
                    _ => code.Length > 0 ? code[0].ToString() : "?",
                };
                string color = badge switch
                {
                    "M" => "#FFD166",
                    "A" => "#5CFFB0",
                    "U" => "#5CFFB0",
                    "D" => "#FF6E6E",
                    "R" => "#A672FF",
                    _ => "#8388BD",
                };
                map[full] = (badge, color);
            }

            foreach (var root in roots) WalkAndTag(root, map);
        }
        catch { /* git not installed / not a repo — silently skip */ }
    }

    private static void WalkAndTag(FileTreeNode node, Dictionary<string, (string, string)> map)
    {
        if (!node.IsDirectory && map.TryGetValue(node.FullPath, out var hit))
        {
            node.GitBadge = hit.Item1;
            node.GitBadgeColorHex = hit.Item2;
        }
        foreach (var c in node.Children) WalkAndTag(c, map);
    }
}
