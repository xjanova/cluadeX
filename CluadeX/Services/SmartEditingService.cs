using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Intelligent code editing pipeline that enhances the model's code modification capabilities.
/// Analyzes code before sending to model, validates responses, and creates minimal patches.
/// </summary>
public class SmartEditingService
{
    private readonly FileSystemService _fileSystemService;

    public SmartEditingService(FileSystemService fileSystemService)
    {
        _fileSystemService = fileSystemService;
    }

    /// <summary>
    /// Analyze a file and create a focused context for the model.
    /// Extracts structure and relevant sections based on the query.
    /// </summary>
    public string CreateFocusedContext(string filePath, string query)
    {
        if (!_fileSystemService.HasWorkingDirectory) return "";

        try
        {
            string fullPath = _fileSystemService.ResolveSafePath(filePath);
            if (!System.IO.File.Exists(fullPath)) return "";

            string content = System.IO.File.ReadAllText(fullPath);
            var sb = new StringBuilder();

            sb.AppendLine($"=== File: {filePath} ===");

            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            string lang = DetectLanguage(ext);
            sb.AppendLine($"Language: {lang}");

            var structure = ExtractStructure(content, lang);
            if (!string.IsNullOrEmpty(structure))
            {
                sb.AppendLine("Structure:");
                sb.AppendLine(structure);
            }

            if (content.Length < 5000)
            {
                sb.AppendLine($"```{lang}");
                sb.AppendLine(content);
                sb.AppendLine("```");
            }
            else
            {
                var relevantLines = FindRelevantSections(content, query);
                sb.AppendLine("Relevant Sections:");
                sb.AppendLine($"```{lang}");
                sb.AppendLine(relevantLines);
                sb.AppendLine("```");
            }

            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Validate a code block for basic syntax correctness (bracket balance, common issues).
    /// </summary>
    public CodeValidationResult ValidateCode(string code, string language)
    {
        var result = new CodeValidationResult { IsValid = true };

        int braces = 0, parens = 0, brackets = 0;
        bool inString = false, inChar = false;
        bool inLineComment = false, inBlockComment = false;
        char prev = '\0';

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];

            if (inLineComment) { if (c == '\n') inLineComment = false; prev = c; continue; }
            if (inBlockComment) { if (prev == '*' && c == '/') inBlockComment = false; prev = c; continue; }
            if (inString) { if (c == '"' && prev != '\\') inString = false; prev = c; continue; }
            if (inChar) { if (c == '\'' && prev != '\\') inChar = false; prev = c; continue; }

            if (c == '/' && i + 1 < code.Length)
            {
                if (code[i + 1] == '/') { inLineComment = true; prev = c; continue; }
                if (code[i + 1] == '*') { inBlockComment = true; prev = c; continue; }
            }
            if (c == '"') { inString = true; prev = c; continue; }
            if (c == '\'') { inChar = true; prev = c; continue; }

            switch (c)
            {
                case '{': braces++; break;
                case '}': braces--; break;
                case '(': parens++; break;
                case ')': parens--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
            }

            if (braces < 0 || parens < 0 || brackets < 0)
            {
                result.IsValid = false;
                result.Issues.Add($"Unmatched closing delimiter near position {i}");
            }

            prev = c;
        }

        if (braces != 0) { result.IsValid = false; result.Issues.Add($"Unbalanced braces (off by {braces})"); }
        if (parens != 0) { result.IsValid = false; result.Issues.Add($"Unbalanced parentheses (off by {parens})"); }
        if (brackets != 0) { result.IsValid = false; result.Issues.Add($"Unbalanced brackets (off by {brackets})"); }

        if (code.Contains("// TODO") || code.Contains("// ...") || code.Contains("/* ... */"))
            result.Warnings.Add("Code contains placeholder comments - may be incomplete");

        return result;
    }

    /// <summary>
    /// Create a minimal line-based diff between original and modified code.
    /// </summary>
    public string CreateMinimalDiff(string original, string modified)
    {
        var origLines = original.Split('\n');
        var modLines = modified.Split('\n');
        var sb = new StringBuilder();

        int i = 0, j = 0;
        while (i < origLines.Length && j < modLines.Length)
        {
            if (origLines[i].TrimEnd() == modLines[j].TrimEnd()) { i++; j++; continue; }

            sb.AppendLine($"@@ Line {i + 1} @@");
            int origEnd = Math.Min(i + 5, origLines.Length);
            int modEnd = Math.Min(j + 5, modLines.Length);

            // Scan ahead for next matching line
            for (int oi = i; oi < origEnd; oi++)
            {
                for (int mj = j + 1; mj < modEnd; mj++)
                {
                    if (origLines[oi].TrimEnd() == modLines[mj].TrimEnd())
                    { origEnd = oi; modEnd = mj; goto DoneScanning; }
                }
            }
            DoneScanning:

            while (i < origEnd) { sb.AppendLine($"- {origLines[i++]}"); }
            while (j < modEnd) { sb.AppendLine($"+ {modLines[j++]}"); }
        }

        while (i < origLines.Length) sb.AppendLine($"- {origLines[i++]}");
        while (j < modLines.Length) sb.AppendLine($"+ {modLines[j++]}");

        return sb.ToString();
    }

    /// <summary>
    /// Enhance a user's code editing request with file context.
    /// </summary>
    public string EnhanceEditRequest(string userMessage, List<string> mentionedFiles)
    {
        if (mentionedFiles.Count == 0) return userMessage;

        var sb = new StringBuilder(userMessage);
        sb.AppendLine();

        foreach (var file in mentionedFiles.Take(3))
        {
            var context = CreateFocusedContext(file, userMessage);
            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine();
                sb.AppendLine(context);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extract file paths mentioned in a message.
    /// </summary>
    public List<string> ExtractMentionedFiles(string message)
    {
        var files = new List<string>();
        var patterns = new[] { @"`([^`]+\.\w{1,10})`", @"[\w./\\-]+\.\w{1,10}" };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(message, pattern))
            {
                string path = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                if (System.IO.Path.HasExtension(path) && !path.Contains("http"))
                    files.Add(path);
            }
        }

        return files.Distinct().ToList();
    }

    private static string DetectLanguage(string extension) => extension switch
    {
        ".cs" => "csharp", ".py" => "python", ".js" => "javascript", ".ts" => "typescript",
        ".jsx" => "jsx", ".tsx" => "tsx", ".java" => "java", ".cpp" or ".cc" => "cpp",
        ".c" => "c", ".h" or ".hpp" => "cpp", ".rs" => "rust", ".go" => "go",
        ".rb" => "ruby", ".php" => "php", ".swift" => "swift", ".kt" => "kotlin",
        ".xaml" or ".xml" => "xml", ".json" => "json", ".yaml" or ".yml" => "yaml",
        ".html" or ".htm" => "html", ".css" => "css", ".sql" => "sql",
        ".sh" or ".bash" => "bash", ".ps1" => "powershell",
        _ => "text",
    };

    private static string ExtractStructure(string content, string language)
    {
        var sb = new StringBuilder();
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            string trimmed = line.TrimStart();
            if (Regex.IsMatch(trimmed, @"^(public|private|protected|internal)?\s*(static\s+)?(class|interface|struct|enum|record)\s+\w+"))
                sb.AppendLine($"  {trimmed.TrimEnd()}");
            else if (Regex.IsMatch(trimmed, @"^(public|private|protected|internal)\s+.*\(.*\)") && !trimmed.Contains("="))
                sb.AppendLine($"    {trimmed.TrimEnd()}");
            else if (Regex.IsMatch(trimmed, @"^(def|function|fn|func)\s+\w+"))
                sb.AppendLine($"  {trimmed.TrimEnd()}");
        }

        return sb.ToString();
    }

    private static string FindRelevantSections(string content, string query)
    {
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).Select(w => w.ToLowerInvariant()).ToList();

        var lines = content.Split('\n');
        var relevantRanges = new HashSet<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            string lower = lines[i].ToLowerInvariant();
            if (keywords.Any(k => lower.Contains(k)))
            {
                for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                    relevantRanges.Add(j);
            }
        }

        if (relevantRanges.Count == 0)
            return string.Join('\n', lines.Take(50)) + "\n...\n" + string.Join('\n', lines.TakeLast(50));

        var sb = new StringBuilder();
        var sorted = relevantRanges.OrderBy(x => x).ToList();
        int prev = -2;
        foreach (int lineNum in sorted)
        {
            if (lineNum - prev > 1 && prev >= 0) sb.AppendLine("...");
            sb.AppendLine($"{lineNum + 1}: {lines[lineNum]}");
            prev = lineNum;
        }

        return sb.ToString();
    }
}

public class CodeValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
