using System.IO;

namespace CluadeX.Services.AutoComment;

/// <summary>The lexer family a file is read with.</summary>
public enum LexKind { CSharp, Python, JavaScript, TypeScript, Dart, Php, Java, Go, CFamily, Xml }

/// <summary>
/// How a language writes the comments auto-comment inserts: one full line per comment line, never a trailing comment.
/// <see cref="LinePrefix"/> for languages with line comments; XML-family files get <c>&lt;!-- … --&gt;</c>.
/// </summary>
public sealed record CommentLanguage(string Id, string DisplayName, LexKind Kind, string? LinePrefix, bool Jsx = false)
{
    public bool IsXml => Kind == LexKind.Xml;

    /// <summary>Braces delimit bodies (function extents come from brace depth); false = Python (indentation) and XML.</summary>
    public bool UsesBraces => Kind is not (LexKind.Python or LexKind.Xml);

    /// <summary>One inserted comment line, without indent or line ending.</summary>
    public string Render(string text) => IsXml ? "<!-- " + text + " -->" : LinePrefix + " " + text;

    private static readonly Dictionary<string, CommentLanguage> ByExtension = Build();

    private static Dictionary<string, CommentLanguage> Build()
    {
        var d = new Dictionary<string, CommentLanguage>(StringComparer.OrdinalIgnoreCase);
        void Add(CommentLanguage l, params string[] exts) { foreach (var e in exts) d[e] = l; }
        Add(new("csharp", "C#", LexKind.CSharp, "//"), ".cs", ".csx");
        Add(new("python", "Python", LexKind.Python, "#"), ".py", ".pyw", ".pyi");
        // JSX is possible in .js/.jsx/.tsx (React); .ts uses <T>expr casts, so no JSX there.
        Add(new("javascript", "JavaScript", LexKind.JavaScript, "//", Jsx: true), ".js", ".jsx", ".mjs", ".cjs");
        Add(new("typescript", "TypeScript", LexKind.TypeScript, "//", Jsx: true), ".tsx");
        Add(new("typescript", "TypeScript", LexKind.TypeScript, "//"), ".ts", ".mts", ".cts");
        Add(new("dart", "Dart", LexKind.Dart, "//"), ".dart");
        Add(new("php", "PHP", LexKind.Php, "//"), ".php", ".phtml");
        Add(new("java", "Java", LexKind.Java, "//"), ".java");
        Add(new("go", "Go", LexKind.Go, "//"), ".go");
        Add(new("c", "C/C++", LexKind.CFamily, "//"), ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx");
        Add(new("xml", "XML", LexKind.Xml, null), ".xml", ".xaml", ".axaml", ".csproj", ".fsproj", ".vbproj", ".props",
            ".targets", ".nuspec", ".resx", ".config", ".xsd", ".xsl", ".xslt", ".svg", ".manifest");
        return d;
    }

    /// <summary>The language for a file, or null when auto-comment does not support it (it then refuses, never guesses).</summary>
    public static CommentLanguage? For(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path ?? ""), out var l) ? l : null;

    public static IEnumerable<string> SupportedExtensions => ByExtension.Keys;
}
