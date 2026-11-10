using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Agents;

/// <summary>What a file is for, in the words of someone who does not read code: the change summary says "the screen",
/// not ".xaml".</summary>
public enum ChangeArea { Screen, Logic, Data, Test, Docs }

/// <summary>One file a turn wrote: its +/− added up over the turn, and whether the turn made it.</summary>
public sealed record FileChange(string Path, string Display, int Added, int Removed, bool Created)
{
    public ChangeArea Area => TurnChanges.AreaOf(Display);
    public bool IsScreen => Area == ChangeArea.Screen;
}

/// <summary>
/// The per-turn change summary (owner 2026-11-08, vibe coding: "what did it just change?" without reading the diffs):
/// every file the turn wrote with its +/−, one plain line of what changed, and — when it touched a screen — the last
/// picture of the app before the first screen edit and after the last one. Collected as the tool results arrive
/// (<see cref="Note"/> / <see cref="NoteImage"/>), drawn once at the end of the turn, kept in the row as JSON.
/// Pure: the harness drives it without the app.
/// </summary>
public sealed class TurnChanges
{
    /// <summary>Files the card lists; the rest are "+N more".</summary>
    public const int MaxListed = 8;

    private readonly Dictionary<string, FileChange> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private bool _screenEdited;

    /// <summary>The newest picture before the turn's first screen edit (may come from an earlier turn: the seed).</summary>
    public string? BeforeImage { get; private set; }
    /// <summary>The newest picture taken after the turn's last screen edit (null = none was taken since).</summary>
    public string? AfterImage { get; private set; }

    public int Count => _files.Count;
    public IReadOnlyList<FileChange> Files => _order.Select(p => _files[p]).ToList();

    /// <summary>A file-writing tool succeeded. <paramref name="created"/>: the file did not exist before (a first write
    /// that removed nothing) — once a file is known, a later edit never makes it "new".</summary>
    public void Note(string fullPath, string display, int added, int removed, bool created)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || added + removed <= 0) return;
        display = (display ?? "").Replace('\\', '/');
        if (display.Length == 0) display = System.IO.Path.GetFileName(fullPath);
        if (_files.TryGetValue(fullPath, out var had))
            _files[fullPath] = had with { Added = had.Added + added, Removed = had.Removed + removed };
        else
        {
            _files[fullPath] = new FileChange(fullPath, display, added, removed, created);
            _order.Add(fullPath);
        }
        if (AreaOf(display) == ChangeArea.Screen)
        {
            _screenEdited = true;
            AfterImage = null;   // a picture from before this edit no longer shows the result
        }
    }

    /// <summary>A tool returned a picture (a screenshot of the app): before the first screen edit it is the "before",
    /// after it the "after".</summary>
    public void NoteImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_screenEdited) AfterImage = path;
        else BeforeImage = path;
    }

    /// <summary>The chat's newest picture when the turn began — the "before" when the turn takes none itself.</summary>
    public void SeedBefore(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !_screenEdited && BeforeImage == null) BeforeImage = path;
    }

    // ─── what a file is for ───

    private static readonly HashSet<string> ScreenExt = new(StringComparer.OrdinalIgnoreCase)
        { ".xaml", ".axaml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".jsx", ".tsx",
          ".razor", ".cshtml", ".storyboard", ".xib", ".uss", ".uxml" };
    private static readonly HashSet<string> DataExt = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".yaml", ".yml", ".toml", ".ini", ".env", ".config", ".csproj", ".props", ".targets", ".sln", ".sql",
          ".xml", ".plist", ".gradle", ".lock", ".csv", ".resx", ".arb" };
    private static readonly HashSet<string> DocsExt = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".rst", ".adoc" };
    private static readonly string[] ScreenWords = { "screen", "page", "view", "widget", "component", "layout", "template", "/ui/", "/pages/", "/components/", "/views/" };

    public static ChangeArea AreaOf(string path)
    {
        string p = (path ?? "").Replace('\\', '/').ToLowerInvariant();
        string name = System.IO.Path.GetFileName(p);
        string ext = System.IO.Path.GetExtension(p);
        if (p.Contains("/test") || p.StartsWith("test") || name.Contains(".test.") || name.Contains(".spec.")
            || name.EndsWith("tests.cs") || name.EndsWith("test.cs") || name.StartsWith("test_") || name.EndsWith("_test.dart")
            || name.EndsWith("_test.go") || name.EndsWith("_test.py"))
            return ChangeArea.Test;
        if (p.EndsWith(".blade.php")) return ChangeArea.Screen;
        if (ScreenExt.Contains(ext)) return ChangeArea.Screen;
        if (DocsExt.Contains(ext)) return ChangeArea.Docs;
        if (DataExt.Contains(ext) || name.StartsWith(".env")) return ChangeArea.Data;
        // Flutter, React and the like keep screens in code files: the folder or the name says so.
        if (ext is ".dart" or ".js" or ".ts" or ".kt" or ".swift" && ScreenWords.Any(w => p.Contains(w))) return ChangeArea.Screen;
        return ChangeArea.Logic;
    }

    // ─── the plain line ───

    /// <summary>"Changed 3 files, 1 new — the screen (Login.xaml, Header.xaml) and how it works (AuthService.cs)".
    /// <paramref name="loc"/> gives the templates: changes.line.{edited,created,both}, changes.area.*, changes.and
    /// (with its own spaces: Thai joins " และ" straight onto the next words), changes.more.</summary>
    public static string PlainLine(IReadOnlyList<FileChange> files, Func<string, string> loc)
    {
        if (files.Count == 0) return "";
        int created = files.Count(f => f.Created), edited = files.Count - created;
        string head = created == 0 ? string.Format(loc("changes.line.edited"), edited)
            : edited == 0 ? string.Format(loc("changes.line.created"), created)
            : string.Format(loc("changes.line.both"), edited, created);
        var parts = new List<string>();
        foreach (var group in files.GroupBy(f => f.Area).OrderBy(g => (int)g.Key))
        {
            var names = group.Select(f => System.IO.Path.GetFileName(f.Display)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string list = string.Join(", ", names.Take(2)) + (names.Count > 2 ? " " + string.Format(loc("changes.more"), names.Count - 2) : "");
            parts.Add(string.Format(loc("changes.area." + group.Key.ToString().ToLowerInvariant()), list));
        }
        string areas = parts.Count <= 1 ? string.Join("", parts)
            : string.Join(", ", parts.Take(parts.Count - 1)) + loc("changes.and") + parts[^1];
        return head + " — " + areas;
    }

    // ─── kept in the row ───

    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        Files = Files.Select(f => new StoredFile { P = f.Path, D = f.Display, A = f.Added, R = f.Removed, N = f.Created }).ToList(),
        Before = Files.Any(f => f.IsScreen) && AfterImage != null ? BeforeImage : null,
        After = Files.Any(f => f.IsScreen) ? AfterImage : null,
    });

    public static (List<FileChange> Files, string? Before, string? After)? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(json);
            if (s?.Files is not { Count: > 0 }) return null;
            return (s.Files.Select(f => new FileChange(f.P ?? "", f.D ?? "", f.A, f.R, f.N)).ToList(), s.Before, s.After);
        }
        catch (JsonException) { return null; }
    }

    private sealed class Stored
    {
        public List<StoredFile>? Files { get; set; }
        public string? Before { get; set; }
        public string? After { get; set; }
    }

    private sealed class StoredFile
    {
        public string? P { get; set; }
        public string? D { get; set; }
        public int A { get; set; }
        public int R { get; set; }
        public bool N { get; set; }
    }
}

/// <summary>The messages the chat sends for the owner from a click: explain a reply simply, fix the errors the builds
/// found. Pure text in, text out — the templates are the localized strings, passed in.</summary>
public static class VibePrompts
{
    /// <summary>A quoted reply is cut here: the model has it in the chat already, the quote only says which one.</summary>
    public const int MaxQuote = 1500;
    public const int MaxErrors = 20;

    /// <summary>"Explain simply": <paramref name="template"/> takes {0} = the reply, quoted.</summary>
    public static string ExplainSimply(string template, string reply)
    {
        reply = (reply ?? "").Replace("\r\n", "\n").Trim();
        if (reply.Length > MaxQuote) reply = reply[..MaxQuote].TrimEnd() + " …";
        string quoted = string.Join("\n", reply.Split('\n').Select(l => "> " + l));
        return string.Format(template, quoted);
    }

    /// <summary>"Fix these errors": <paramref name="header"/> takes {0} = how many; one "path:line:col — message" row
    /// each (paths relative to <paramref name="root"/>), the first <see cref="MaxErrors"/> only.</summary>
    public static string FixErrors(string header, string moreTemplate, string? root,
        IReadOnlyList<(string Path, int Line, int Column, string Message, string Code)> errors)
    {
        var sb = new StringBuilder(string.Format(header, errors.Count)).Append('\n');
        var rows = new StringBuilder();
        foreach (var e in errors.Take(MaxErrors))
        {
            string rel = e.Path;
            try
            {
                if (!string.IsNullOrEmpty(root) && System.IO.Path.IsPathRooted(rel))
                    rel = System.IO.Path.GetRelativePath(root, rel);
            }
            catch (ArgumentException) { }
            rel = rel.Replace('\\', '/');
            string msg = (e.Message ?? "").Replace('\n', ' ').Trim();
            if (msg.Length > 300) msg = msg[..300] + " …";
            rows.Append(rel);
            if (e.Line > 0) rows.Append(':').Append(e.Line);
            if (e.Column > 0) rows.Append(':').Append(e.Column);
            rows.Append(" — ");
            if (!string.IsNullOrEmpty(e.Code)) rows.Append(e.Code).Append(": ");
            rows.Append(msg).Append('\n');
        }
        sb.Append(Problems.EditorChatPrompts.Fence(rows.ToString()));
        if (errors.Count > MaxErrors) sb.Append('\n').Append(string.Format(moreTemplate, errors.Count - MaxErrors));
        return sb.ToString();
    }
}

/// <summary>One thing an @-mention can name: a project file, or a symbol declared in one.</summary>
public sealed record MentionItem(string Label, string Detail, string Insert, bool IsSymbol);

/// <summary>
/// @-mentions in the composer (owner 2026-11-08): typing "@log" lists the project's files and symbols that match, and
/// the pick goes into the message as a path the agent can open. Pure: <see cref="QueryAt"/> finds what is being typed,
/// <see cref="Match"/> ranks, <see cref="Apply"/> replaces it.
/// </summary>
public static class Mentions
{
    public const int MaxShown = 12;

    /// <summary>The "@word" the caret is in: the start of the "@" and the text after it, or null. An "@" inside a word
    /// (an e-mail address) is not a mention.</summary>
    public static (int Start, string Query)? QueryAt(string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        int i = caret - 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i]) && text[i] != '@') i--;
        if (i < 0 || text[i] != '@') return null;
        if (i > 0 && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] is not ('(' or '"' or '\'')) return null;
        return (i, text[(i + 1)..caret]);
    }

    /// <summary>Best first: a name that starts with the query, then a name that holds it, then a path that holds it;
    /// shorter paths first. An empty query lists the first files.</summary>
    public static List<MentionItem> Match(string query, IReadOnlyList<string> files, IReadOnlyList<(string Name, string File, int Line)> symbols)
    {
        query = (query ?? "").Trim().Replace('\\', '/');
        var found = new List<(int Score, MentionItem Item)>();
        foreach (var f in files)
        {
            string name = System.IO.Path.GetFileName(f);
            int score = query.Length == 0 ? 50
                : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 100
                : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 70
                : f.Contains(query, StringComparison.OrdinalIgnoreCase) ? 40 : -1;
            if (score < 0) continue;
            found.Add((score - Math.Min(30, f.Length / 8), new MentionItem(name, f, "@" + f, false)));
        }
        if (query.Length >= 2)
            foreach (var s in symbols)
            {
                int score = s.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 110
                    : s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 90
                    : s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 55 : -1;
                if (score < 0) continue;
                found.Add((score - Math.Min(30, s.File.Length / 8),
                    new MentionItem(s.Name, $"{s.File}:{s.Line}", $"`{s.Name}` (@{s.File}:{s.Line})", true)));
            }
        return found.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Detail, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item).DistinctBy(i => i.Insert).Take(MaxShown).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex TypeName = new(
        @"\b(?:class|interface|struct|enum|record|trait|protocol|mixin|extension)\s+([A-Za-z_]\w*)",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex FuncName = new(
        @"^\s*(?:@\w+\s+)*(?:(?:public|private|protected|internal|export|static|async|abstract|virtual|override|final|pub|def|func|function|fn|sealed|partial|default)\s+)+[^;{=()]*?\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal) { "if", "for", "while", "switch", "catch", "return", "new", "using", "lock", "foreach" };

    /// <summary>The names declared in a source file — types, and functions or methods with a modifier or keyword in
    /// front — with their 1-based line. Language-agnostic and cheap (one pass of two regexes per line).</summary>
    public static List<(string Name, int Line)> SymbolsIn(string content, int max = 400)
    {
        var found = new List<(string, int)>();
        if (string.IsNullOrEmpty(content)) return found;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && found.Count < max; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || line.Length > 300) continue;
            foreach (System.Text.RegularExpressions.Match m in TypeName.Matches(line)) found.Add((m.Groups[1].Value, i + 1));
            var f = FuncName.Match(line);
            if (f.Success && !NotNames.Contains(f.Groups[1].Value)) found.Add((f.Groups[1].Value, i + 1));
        }
        return found.Distinct().ToList();
    }

    /// <summary>The text with "@query" at <paramref name="start"/> replaced by the pick and a space, and where the caret goes.</summary>
    public static (string Text, int Caret) Apply(string text, int start, int caret, MentionItem pick)
    {
        text ??= "";
        start = Math.Clamp(start, 0, text.Length);
        caret = Math.Clamp(caret, start, text.Length);
        string insert = pick.Insert + " ";
        string after = text[caret..];
        if (after.StartsWith(' ')) insert = pick.Insert;
        return (text[..start] + insert + after, start + insert.Length);
    }
}
