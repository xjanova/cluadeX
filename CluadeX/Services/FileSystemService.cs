using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// Provides file system operations for the coding agent.
/// All paths are validated to stay within the working directory.
/// </summary>
public class FileSystemService
{
    private string _workingDirectory = string.Empty;

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => _workingDirectory = value;
    }

    public bool HasWorkingDirectory => !string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory);

    // ─── Read File ───
    public string ReadFile(string relativePath)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        // Check file size (limit to 500KB to avoid memory issues)
        var fi = new FileInfo(fullPath);
        if (fi.Length > 512 * 1024)
            throw new InvalidOperationException($"File too large ({fi.Length / 1024}KB). Max 500KB.");

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    // ─── Write File ───
    public void WriteFile(string relativePath, string content)
    {
        string fullPath = ResolveSafePath(relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
    }

    // ─── Edit File (Find & Replace) ───
    public (bool found, int replacements) EditFile(string relativePath, string find, string replace)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        string content = File.ReadAllText(fullPath, Encoding.UTF8);
        int count = 0;

        // Count occurrences
        int idx = 0;
        while ((idx = content.IndexOf(find, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += find.Length;
        }

        if (count == 0) return (false, 0);

        string newContent = content.Replace(find, replace);
        File.WriteAllText(fullPath, newContent, Encoding.UTF8);
        return (true, count);
    }

    // ─── List Directory ───
    public List<FileEntry> ListDirectory(string relativePath = ".", int maxDepth = 1)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {relativePath}");

        var entries = new List<FileEntry>();
        ListDirectoryRecursive(fullPath, fullPath, entries, 0, maxDepth);
        return entries;
    }

    private void ListDirectoryRecursive(string rootPath, string currentPath, List<FileEntry> entries, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                string name = Path.GetFileName(dir);
                // Skip hidden and common ignore dirs
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or ".git" or "__pycache__" or ".vs")
                    continue;

                string rel = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
                entries.Add(new FileEntry { Name = name, RelativePath = rel, IsDirectory = true });
                ListDirectoryRecursive(rootPath, dir, entries, depth + 1, maxDepth);
            }

            foreach (var file in Directory.GetFiles(currentPath))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith('.')) continue;

                string rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var fi = new FileInfo(file);
                entries.Add(new FileEntry { Name = name, RelativePath = rel, IsDirectory = false, SizeBytes = fi.Length });
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    // ─── Search Files by Pattern ───
    public List<string> SearchFiles(string pattern, string relativePath = ".")
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            return new();

        var results = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_workingDirectory, file).Replace('\\', '/');
                // Skip common ignore dirs
                if (rel.Contains("node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    rel.Contains("/.git/") || rel.Contains("/__pycache__/") || rel.Contains("/.vs/"))
                    continue;
                results.Add(rel);
                if (results.Count >= 100) break; // Limit results
            }
        }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    // ─── Search Content (grep-like) ───
    public List<SearchContentResult> SearchContent(string query, string relativePath = ".", string filePattern = "*.*")
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            return new();

        var results = new List<SearchContentResult>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, filePattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_workingDirectory, file).Replace('\\', '/');
                if (rel.Contains("node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    rel.Contains("/.git/") || rel.Contains("/__pycache__/") || rel.Contains("/.vs/"))
                    continue;

                // Only search text files (skip binary)
                var fi = new FileInfo(file);
                if (fi.Length > 1024 * 1024) continue; // Skip files > 1MB

                try
                {
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new SearchContentResult
                            {
                                FilePath = rel,
                                LineNumber = i + 1,
                                LineContent = lines[i].TrimEnd('\r').Trim(),
                            });
                            if (results.Count >= 50) return results;
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }
        }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    // ─── Create Directory ───
    public void CreateDirectory(string relativePath)
    {
        string fullPath = ResolveSafePath(relativePath);
        Directory.CreateDirectory(fullPath);
    }

    // ─── File/Dir Exists ───
    public bool FileExists(string relativePath)
    {
        try { return File.Exists(ResolveSafePath(relativePath)); }
        catch { return false; }
    }

    public bool DirectoryExists(string relativePath)
    {
        try { return Directory.Exists(ResolveSafePath(relativePath)); }
        catch { return false; }
    }

    // ─── Get Project Tree (compact) ───
    public string GetProjectTree(int maxDepth = 3)
    {
        if (!HasWorkingDirectory) return "(no working directory set)";

        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(_workingDirectory) + "/");
        BuildTree(sb, _workingDirectory, "", 0, maxDepth);
        return sb.ToString().TrimEnd();
    }

    private void BuildTree(StringBuilder sb, string dir, string indent, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        var dirs = GetFilteredDirs(dir);
        var files = GetFilteredFiles(dir);

        foreach (var d in dirs)
        {
            sb.AppendLine($"{indent}  {Path.GetFileName(d)}/");
            BuildTree(sb, d, indent + "  ", depth + 1, maxDepth);
        }
        foreach (var f in files)
        {
            sb.AppendLine($"{indent}  {Path.GetFileName(f)}");
        }
    }

    private static string[] GetFilteredDirs(string path)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Where(d =>
                {
                    string n = Path.GetFileName(d);
                    return !n.StartsWith('.') && n is not ("node_modules" or "bin" or "obj" or "__pycache__" or ".vs");
                })
                .OrderBy(d => Path.GetFileName(d))
                .ToArray();
        }
        catch { return []; }
    }

    private static string[] GetFilteredFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => Path.GetFileName(f))
                .ToArray();
        }
        catch { return []; }
    }

    // ─── Safety: Resolve and validate path ───
    public string ResolveSafePath(string relativePath)
    {
        if (!HasWorkingDirectory)
            throw new InvalidOperationException("No working directory set. Open a project folder first.");

        // Normalize path
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

        // Reject absolute paths entirely — only relative paths allowed
        if (Path.IsPathRooted(normalized))
            throw new UnauthorizedAccessException("Absolute paths are not allowed. Use relative paths from the project root.");

        string fullPath = Path.GetFullPath(Path.Combine(_workingDirectory, normalized));

        // Security: ensure path stays within working directory (handles .. traversal)
        string workDirFull = Path.GetFullPath(_workingDirectory);
        if (!fullPath.StartsWith(workDirFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied. Path must be within the working directory.");

        return fullPath;
    }
}

// ─── Helper Models ───
public class FileEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
}

public class SearchContentResult
{
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string LineContent { get; set; } = "";
}
