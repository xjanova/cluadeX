using System.Text;

namespace CluadeX.Services.Problems;

/// <summary>
/// The messages the Code Editor sends to the chat. Pure text in, text out — the templates are the localized strings
/// (editor.ask.*), passed in so the harness can check them without the app's localization.
/// </summary>
public static class EditorChatPrompts
{
    /// <summary>More than this of a selection is cut: a whole file pasted into a turn is better read by the agent itself.</summary>
    public const int MaxCodeChars = 40_000;
    public const int MaxContextRows = 60;

    /// <summary>A fenced block that the code cannot close early (a fence one longer than any run of backticks in it).</summary>
    public static string Fence(string code, string language = "")
    {
        code = (code ?? "").Replace("\r\n", "\n").TrimEnd('\n');
        bool cut = false;
        if (code.Length > MaxCodeChars) { code = code[..MaxCodeChars]; cut = true; }
        int longest = 0, run = 0;
        foreach (char c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{code}{(cut ? "\n… (cut)" : "")}\n{fence}";
    }

    /// <summary>
    /// "Fix / explain / change this": <paramref name="template"/> takes {0} file, {1} first line, {2} last line, {3} the
    /// fenced code. The path is project-relative so the agent can open it; the lines let it edit the right place.
    /// </summary>
    public static string Selection(string template, string relativePath, int fromLine, int toLine, string code, string language = "")
        => string.Format(template, relativePath, fromLine, Math.Max(fromLine, toLine), Fence(code, language));

    /// <summary>The fence language for a file (what markdown renderers know), or "".</summary>
    public static string LanguageOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "tsx",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "jsx",
        ".py" or ".pyw" => "python",
        ".dart" => "dart",
        ".rs" => "rust",
        ".go" => "go",
        ".java" => "java",
        ".kt" => "kotlin",
        ".xaml" or ".xml" or ".csproj" => "xml",
        ".json" => "json",
        ".html" => "html",
        ".css" => "css",
        ".sql" => "sql",
        ".ps1" => "powershell",
        ".sh" => "bash",
        _ => "",
    };

    /// <summary>Search / reference results as context: <paramref name="header"/> then one "path:line: text" row each.</summary>
    public static string Results(string header, IEnumerable<(string RelativePath, int Line, string Text)> rows, string moreTemplate)
    {
        var list = rows.ToList();
        var sb = new StringBuilder(header).Append('\n');
        var lines = new StringBuilder();
        foreach (var (path, line, text) in list.Take(MaxContextRows))
        {
            string t = (text ?? "").Trim();
            if (t.Length > 200) t = t[..200] + " …";
            lines.Append(path).Append(':').Append(line).Append(": ").Append(t).Append('\n');
        }
        sb.Append(Fence(lines.ToString()));
        if (list.Count > MaxContextRows) sb.Append('\n').Append(string.Format(moreTemplate, list.Count - MaxContextRows));
        return sb.ToString();
    }
}
