using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>One Dart file, tokenised once.</summary>
internal sealed class DartFile
{
    public string Path { get; }
    public string Text { get; }
    public List<DToken> Tokens { get; }
    public int[] Match { get; }
    private int[]? _lineStarts;

    public DartFile(string path, string text)
    {
        Path = path;
        Text = text;
        Tokens = DartLexer.Lex(text);
        Match = DartLexer.Brackets(Tokens);
    }

    public DartParser Parser() => new(Tokens, Match);

    public int LineOf(int pos)
    {
        if (_lineStarts == null)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < Text.Length; i++) if (Text[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }
        int idx = Array.BinarySearch(_lineStarts, pos);
        return (idx >= 0 ? idx : ~idx - 1) + 1;
    }

    /// <summary>Token index of the first token at or after a character position.</summary>
    public int TokenAt(int pos)
    {
        int lo = 0, hi = Tokens.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Tokens[mid].Pos < pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>A constructor parameter: its name, named or positional, and where its default value is written (-1: none).</summary>
internal sealed record DartParam(string Name, bool Named, int Index, int DefaultAt = -1, bool Required = false);

/// <summary>A class declared in the project: its superclass, constructor parameters, members and fields.</summary>
internal sealed class DartClass
{
    public string Name { get; init; } = "";
    public string Super { get; init; } = "";
    /// <summary>For a State class: the widget it belongs to (<c>State&lt;UsersScreen&gt;</c>).</summary>
    public string? StateOf { get; init; }
    public DartFile File { get; init; } = null!;
    public int Open { get; init; }
    public int Close { get; init; }
    public List<DartParam> Params { get; } = new();
    /// <summary>Method/getter name → token index of its name.</summary>
    public Dictionary<string, int> Members { get; } = new(StringComparer.Ordinal);
    /// <summary>Field name → token index of its initializer.</summary>
    public Dictionary<string, int> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Field name → its declared type as written ("int", "String?", "List&lt;Widget&gt;").</summary>
    public Dictionary<string, string> FieldTypes { get; } = new(StringComparer.Ordinal);
    public bool ConstCtor { get; set; }
    public int Line => File.LineOf(File.Tokens[Open].Pos);

    public bool IsWidget => FlutterProject.WidgetBases.Contains(Super) || FlutterProject.StatefulBases.Contains(Super);
    public bool IsStateful => FlutterProject.StatefulBases.Contains(Super);
}

/// <summary>
/// A Flutter project read for the UI Studio: every class of lib/, the app's routes (go_router, named routes,
/// onGenerateRoute, GetX pages), its translations (ARB files) and its colours — everything needed to list its
/// screens and draw each one as a wireframe without running the app (Flutter can't run inside the Studio's browser,
/// and many apps don't build for the web at all).
/// </summary>
public sealed class FlutterProject : IScreenSketcher
{
    internal static readonly HashSet<string> WidgetBases = new(StringComparer.Ordinal)
        { "StatelessWidget", "ConsumerWidget", "HookWidget", "HookConsumerWidget", "GetView", "GetWidget", "StatelessHookWidget" };
    internal static readonly HashSet<string> StatefulBases = new(StringComparer.Ordinal)
        { "StatefulWidget", "ConsumerStatefulWidget", "StatefulHookWidget", "StatefulHookConsumerWidget" };
    private static readonly HashSet<string> StateBases = new(StringComparer.Ordinal) { "State", "ConsumerState" };

    public string Root { get; }
    internal Dictionary<string, DartClass> Classes { get; } = new(StringComparer.Ordinal);
    internal List<DartFile> Files { get; } = new();
    internal Dictionary<string, string> L10n { get; } = new(StringComparer.Ordinal);
    public SketchTheme Theme { get; private set; } = new();
    public bool Truncated { get; private set; }

    /// <summary>Page id → how to draw it (the screen's constructor call, the shell around it).</summary>
    private readonly Dictionary<string, ScreenRef> _refs = new(StringComparer.Ordinal);

    internal sealed record ScreenRef(string ClassName, DCall? Call, DartFile? CallFile, ShellRef? Shell);
    internal sealed record ShellRef(DExpr Builder, DartFile File, string? SlotParam);

    private FlutterProject(string root) => Root = root;

    public static bool IsFlutter(string root) => File.Exists(Path.Combine(root, "pubspec.yaml")) && Directory.Exists(Path.Combine(root, "lib"))
        && WebPages.SafeRead(Path.Combine(root, "pubspec.yaml"), 200_000).Contains("flutter", StringComparison.Ordinal);

    public static FlutterProject Load(string root, string language, CancellationToken ct = default, int maxFiles = 1500)
    {
        var p = new FlutterProject(root);
        string lib = Path.Combine(root, "lib");
        var dart = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dart" };
        int count = 0;
        foreach (var f in SourceSearch.ProjectFiles(lib, dart, maxFiles + 1))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > maxFiles) { p.Truncated = true; break; }
            if (f.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)) continue;
            string text = WebPages.SafeRead(f, 1_500_000);
            if (text.Length == 0) continue;
            var file = new DartFile(f, text);
            p.Files.Add(file);
            p.IndexClasses(file);
        }
        p.LoadL10n(language);
        p.Theme = ReadTheme(p.Files);
        return p;
    }

    // ───────────────────────────── classes ─────────────────────────────

    private void IndexClasses(DartFile f)
    {
        var t = f.Tokens;
        for (int i = 0; i < t.Count - 2; i++)
        {
            if (t[i].Kind != DTok.Ident || t[i].Text != "class" || t[i + 1].Kind != DTok.Ident) continue;
            if (i > 0 && t[i - 1].Kind == DTok.Punct && t[i - 1].Text == ".") continue;
            string name = t[i + 1].Text;
            string super = "", stateOf = "";
            int k = i + 2;
            int open = -1;
            for (; k < t.Count && k < i + 80; k++)
            {
                if (t[k].Kind == DTok.Punct && t[k].Text == "{") { open = k; break; }
                if (t[k].Kind == DTok.Punct && t[k].Text == ";") break;   // class X = Y with Z;
                if (t[k].Kind == DTok.Ident && t[k].Text == "extends" && t[k + 1].Kind == DTok.Ident)
                {
                    super = t[k + 1].Text;
                    if (t[k + 2].Text == "<" && t[k + 3].Kind == DTok.Ident) stateOf = t[k + 3].Text;
                }
            }
            if (open < 0 || f.Match[open] < 0) continue;
            var cls = new DartClass
            {
                Name = name, Super = super, StateOf = StateBases.Contains(super) && stateOf.Length > 0 ? stateOf : null,
                File = f, Open = open, Close = f.Match[open],
            };
            IndexMembers(cls);
            Classes.TryAdd(name, cls);
            i = open;   // classes don't nest in Dart
        }
    }

    private static void IndexMembers(DartClass c)
    {
        var t = c.File.Tokens;
        var m = c.File.Match;
        int p = c.Open + 1;
        while (p < c.Close)
        {
            var tk = t[p];
            if (tk.Kind == DTok.Punct && tk.Text is "{" or "(" or "[" && m[p] > p)
            {
                // constructor:  Name(  /  const Name(  /  Name.named(
                if (tk.Text == "(" && p > 0 && t[p - 1].Kind == DTok.Ident && t[p - 1].Text == c.Name
                    && !(p > 1 && t[p - 2].Kind == DTok.Punct && t[p - 2].Text == "."))
                {
                    if (c.Params.Count == 0)
                    {
                        ReadParams(c.File, p, c.Params);
                        c.ConstCtor = p > 1 && t[p - 2].Kind == DTok.Ident && t[p - 2].Text == "const";
                    }
                }
                else if (tk.Text == "(" && p > 1 && t[p - 1].Kind == DTok.Ident && IsDeclStart(t, p - 1, c.Name))
                {
                    c.Members.TryAdd(t[p - 1].Text, p - 1);
                }
                p = m[p] + 1;
                continue;
            }
            // getter:  Widget get header =>  /  get header {
            if (tk.Kind == DTok.Ident && tk.Text == "get" && t[p + 1].Kind == DTok.Ident && t[p + 2].Text is "=>" or "{")
            {
                c.Members.TryAdd(t[p + 1].Text, p + 1);
                p += 2;
                continue;
            }
            // field:  final _x = ...;  /  late final Foo _y = ...;
            if (tk.Kind == DTok.Ident && t[p + 1].Kind == DTok.Punct && t[p + 1].Text == "=" && p > c.Open + 1
                && (t[p - 1].Kind == DTok.Ident || t[p - 1].Kind == DTok.Punct && t[p - 1].Text is ">" or "?"))
            {
                c.Fields.TryAdd(tk.Text, p + 2);
            }
            // field type:  final int readingId;  /  final List<Widget>? actions;  /  String title = 'x';
            if (tk.Kind == DTok.Ident && t[p + 1].Kind == DTok.Punct && t[p + 1].Text is ";" or "=" && p > c.Open + 1)
            {
                var type = new List<string>();
                for (int b = p - 1; b > c.Open; b--)
                {
                    var bt = t[b];
                    if (bt.Kind == DTok.Punct && bt.Text is ";" or "{" or "}" or ")" or "]" or "=" or "=>") break;
                    if (bt.Kind == DTok.Ident && bt.Text is "final" or "late" or "static" or "const" or "covariant" or "var" or "external") break;
                    if (bt.Kind == DTok.Punct && bt.Text == "@") break;
                    type.Insert(0, bt.Text);
                }
                if (type.Count > 0 && type.Count < 12) c.FieldTypes.TryAdd(tk.Text, string.Concat(type));
            }
            p++;
        }
    }

    /// <summary>A method declaration's name at <paramref name="nameIdx"/>: preceded by a return type (or nothing
    /// for a constructor-like call is excluded), followed after its parameters by a body.</summary>
    private static bool IsDeclStart(List<DToken> t, int nameIdx, string className)
    {
        if (t[nameIdx].Text == className) return false;
        var prev = t[nameIdx - 1];
        bool typed = prev.Kind == DTok.Ident && prev.Text is not ("return" or "new" or "const" or "await" or "else" or "case")
                     || prev.Kind == DTok.Punct && prev.Text is ">" or "?";
        return typed;
    }

    private static void ReadParams(DartFile f, int open, List<DartParam> into)
    {
        var t = f.Tokens;
        int close = f.Match[open];
        bool named = false, optionalPositional = false;
        int index = 0;
        var part = new List<DToken>();
        void Flush()
        {
            if (part.Count == 0) return;
            string? name = null;
            int defaultAt = -1;
            for (int k = 0; k < part.Count; k++)
            {
                if (part[k].Kind == DTok.Punct && part[k].Text is "=" or ":")
                {
                    if (k + 1 < part.Count) defaultAt = f.TokenAt(part[k + 1].Pos);
                    break;
                }
                if (name == null && part[k].Text is "this" or "super" && k + 2 < part.Count && part[k + 1].Text == ".") { name = part[k + 2].Text; k += 2; continue; }
                if (part[k].Kind == DTok.Ident && part[k].Text is not ("required" or "final" or "covariant" or "const" or "this" or "super")) name = part[k].Text;
            }
            bool required = named ? part.Any(x => x.Kind == DTok.Ident && x.Text == "required") : !optionalPositional;
            if (name != null && name != "key") into.Add(new DartParam(name, named, named ? -1 : index++, defaultAt, required && defaultAt < 0));
            else if (name == "key" && !named) index++;
            part.Clear();
        }
        int depth = 0;
        for (int k = open + 1; k < close; k++)
        {
            var tk = t[k];
            if (tk.Kind == DTok.Punct)
            {
                if (tk.Text is "{" or "[")
                {
                    // the start of the named {…} / optional […] group — not a default value like "this.actions = const []"
                    if (depth == 0 && part.Count == 0) { named = tk.Text == "{"; optionalPositional = tk.Text == "["; continue; }
                    depth++;
                }
                else if (tk.Text is "}" or "]")
                {
                    if (depth == 0) { Flush(); continue; }   // the group ends
                    depth--;
                }
                else if (tk.Text is "(" or "<") depth++;
                else if (tk.Text is ")" or ">") depth = Math.Max(0, depth - 1);
                else if (tk.Text == "," && depth == 0) { Flush(); continue; }
            }
            part.Add(tk);
        }
        Flush();
    }

    internal DartClass? ClassOf(string name) => Classes.TryGetValue(name, out var c) ? c : null;

    /// <summary>The class whose build() draws the widget: itself, or its State class.</summary>
    internal DartClass? BuildClassOf(DartClass widget)
    {
        if (!widget.IsStateful) return widget;
        return Classes.Values.FirstOrDefault(c => c.StateOf == widget.Name && c.File == widget.File)
               ?? Classes.Values.FirstOrDefault(c => c.StateOf == widget.Name);
    }

    /// <summary>What a method/getter returns: its arrow expression, or every return of its block.</summary>
    internal List<DExpr> ReturnsOf(DartClass c, string member, out List<string> parameters)
    {
        parameters = new List<string>();
        var list = new List<DExpr>();
        if (!c.Members.TryGetValue(member, out int nameIdx)) return list;
        var f = c.File;
        var t = f.Tokens;
        int k = nameIdx + 1;
        if (t[k].Text == "(" && f.Match[k] > k)
        {
            var ps = new List<DartParam>();
            ReadParams(f, k, ps);
            parameters.AddRange(ps.Select(p => p.Name));
            k = f.Match[k] + 1;
        }
        while (k < t.Count && t[k].Kind == DTok.Ident && t[k].Text is "async" or "sync") k++;
        var parser = f.Parser();
        if (t[k].Text == "=>") { list.Add(parser.ParseAt(k + 1)); return list; }
        if (t[k].Text == "{" && f.Match[k] > k) parser.CollectReturns(k, f.Match[k], list);
        return list;
    }

    /// <summary>Local variables of a method body: name → token index of the initializer.</summary>
    internal Dictionary<string, int> LocalsOf(DartClass c, string member)
    {
        var locals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!c.Members.TryGetValue(member, out int nameIdx)) return locals;
        var f = c.File;
        var t = f.Tokens;
        int k = nameIdx + 1;
        if (t[k].Text == "(" && f.Match[k] > k) k = f.Match[k] + 1;
        while (k < t.Count && t[k].Kind == DTok.Ident) k++;
        if (t[k].Text != "{" || f.Match[k] < 0) return locals;
        int end = f.Match[k];
        for (int p = k + 1; p < end - 2; p++)
        {
            if (t[p].Kind == DTok.Punct && t[p].Text is "(" or "[" && f.Match[p] > p) { p = f.Match[p]; continue; }
            // "final x = …" / "Widget x = …", and "x = …" assigned later ("final List<Widget> slivers; if (…) slivers = …")
            if (t[p].Kind == DTok.Ident && t[p + 1].Kind == DTok.Punct && t[p + 1].Text == "="
                && (t[p - 1].Kind == DTok.Ident && t[p - 1].Text is not ("return") || t[p - 1].Text is ">" or "?" or ";" or "{" or "}" or ")"))
                locals.TryAdd(t[p].Text, p + 2);
        }
        return locals;
    }

    // ───────────────────────────── translations ─────────────────────────────

    private void LoadL10n(string language)
    {
        string yaml = WebPages.SafeRead(Path.Combine(Root, "l10n.yaml"), 50_000);
        string arbDir = Regex.Match(yaml, @"(?m)^\s*arb-dir:\s*(?<d>\S+)").Groups["d"].Value;
        string template = Regex.Match(yaml, @"(?m)^\s*template-arb-file:\s*(?<f>\S+)").Groups["f"].Value;
        string dir = Path.Combine(Root, arbDir.Length > 0 ? arbDir.Replace('/', Path.DirectorySeparatorChar) : Path.Combine("lib", "l10n"));
        if (!Directory.Exists(dir)) return;
        var arbs = Directory.EnumerateFiles(dir, "*.arb").ToList();
        string? pick = arbs.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a).EndsWith("_" + language, StringComparison.OrdinalIgnoreCase))
                       ?? (template.Length > 0 ? arbs.FirstOrDefault(a => Path.GetFileName(a).Equals(template, StringComparison.OrdinalIgnoreCase)) : null)
                       ?? arbs.FirstOrDefault();
        // the template first (every key), then the chosen language over it
        foreach (var arb in new[] { template.Length > 0 ? Path.Combine(dir, template) : null, pick }.Where(a => a != null).Distinct())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(arb!), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (!prop.Name.StartsWith('@') && prop.Value.ValueKind == JsonValueKind.String)
                        L10n[prop.Name] = IcuText(prop.Value.GetString() ?? "");
            }
            catch { /* a broken ARB file: its keys stay unresolved */ }
        }
    }

    /// <summary>An ICU message as plain text: placeholders become "…", a plural/select shows its "other" case.</summary>
    internal static string IcuText(string msg)
    {
        for (int guard = 0; guard < 8; guard++)
        {
            var m = Regex.Match(msg, @"\{\s*\w+\s*,\s*(?:plural|select|selectordinal)\s*,");
            if (!m.Success) break;
            int start = m.Index, depth = 0, end = -1;
            for (int i = start; i < msg.Length; i++)
            {
                if (msg[i] == '{') depth++;
                else if (msg[i] == '}' && --depth == 0) { end = i; break; }
            }
            if (end < 0) break;
            string body = msg[start..(end + 1)];
            var other = Regex.Match(body, @"other\s*\{");
            string inner = "…";
            if (other.Success)
            {
                int o = other.Index + other.Length, d = 1, e = o;
                for (; e < body.Length && d > 0; e++) { if (body[e] == '{') d++; else if (body[e] == '}') d--; }
                inner = body[o..Math.Max(o, e - 1)];
            }
            msg = msg[..start] + inner + msg[(end + 1)..];
        }
        return Regex.Replace(msg, @"\{\s*\w+\s*\}", "…").Replace("#", "…");
    }

    // ───────────────────────────── colours ─────────────────────────────

    private static readonly Regex ColorLit = new(@"Color\(\s*0x(?<c>[0-9A-Fa-f]{8})\s*\)", RegexOptions.Compiled);

    private static SketchTheme ReadTheme(List<DartFile> files)
    {
        var themeFiles = files.Where(f => Regex.IsMatch(System.IO.Path.GetFileName(f.Path), "theme|palette|colors?|main", RegexOptions.IgnoreCase)).ToList();
        string all = string.Join("\n", themeFiles.Select(f => f.Text));
        // The first value written for any of the keys — ThemeData / ColorScheme names, and the words a custom
        // palette class tends to use (bg:, gold:, textStrong:, cardSolid:).
        uint? Find(params string[] keys)
        {
            foreach (var key in keys)
            {
                var m = Regex.Match(all, @"(?<![\w.])" + key + @"\s*[:=]\s*(?:const\s+)?Color\(\s*0x(?<c>[0-9A-Fa-f]{8})\s*\)");
                if (m.Success) return Convert.ToUInt32(m.Groups["c"].Value, 16) | 0xFF000000;
            }
            return null;
        }
        uint accent = Find("seedColor", "colorSchemeSeed", "primary", "primaryColor", "accent", "brand", "gold", "main") ?? 0xFF6750A4;
        bool dark = Regex.IsMatch(all, @"themeMode\s*:\s*ThemeMode\.dark") ||
                    (Regex.IsMatch(all, @"Brightness\.dark") && !Regex.IsMatch(all, @"Brightness\.light"));
        var theme = dark ? SketchTheme.DarkDefault(accent) : new SketchTheme { Accent = accent };
        if (Find("scaffoldBackgroundColor", "background", "bg", "backgroundColor") is { } bg)
        {
            theme.Background = bg;
            theme.Dark = Luma(bg) < 0.45;
            theme.OnBackground = theme.Dark ? 0xFFE6E0E9 : 0xFF1C1B1F;
            theme.Surface = Mix(bg, theme.Dark ? 0xFFFFFFFF : 0xFF000000, 0.07);
        }
        if (Find("surface", "cardSolid", "card", "cardColor", "surfaceContainer") is { } surface && Math.Abs(Luma(surface) - Luma(theme.Background)) < 0.4) theme.Surface = surface;
        if (Find("onBackground", "onSurface", "textStrong", "textPrimary", "text") is { } fg && Math.Abs(Luma(fg) - Luma(theme.Background)) > 0.4) theme.OnBackground = fg;
        return theme;
    }

    internal static double Luma(uint argb) => (0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF)) / 255.0;

    internal static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
        return 0xFF000000 | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    // ───────────────────────────── pages ─────────────────────────────

    /// <summary>Every screen of the app: the routes first (go_router, named routes, onGenerateRoute, GetX), then
    /// the screen classes no route reaches (pushed with Navigator.push, shown as dialogs, or not wired yet).</summary>
    public List<StudioPage> Pages()
    {
        _refs.Clear();
        var pages = new List<StudioPage>();
        var routed = new HashSet<string>(StringComparer.Ordinal);

        void AddRoute(string route, DCall? call, DartFile file, ShellRef? shell, string? name)
        {
            if (call == null || ClassOf(call.Name) is not { } cls) return;
            var norm = WebPages.NormalizeRoute(route, out var parameters);
            if (norm == null) return;
            string id = "screen:" + norm.ToLowerInvariant();
            if (_refs.ContainsKey(id)) return;
            _refs[id] = new ScreenRef(cls.Name, call, file, shell);
            routed.Add(cls.Name);
            pages.Add(new StudioPage
            {
                Id = id, Kind = StudioPageKind.Screen, Route = norm, Title = WebPages.Humanize(cls.Name),
                SourcePath = cls.File.Path, SourceLine = cls.Line, Detail = cls.Name, Params = parameters,
                InShell = shell != null, Risky = PageSafety.IsRisky(norm, name),
            });
        }

        foreach (var f in Files)
        {
            if (!f.Text.Contains("GoRoute(", StringComparison.Ordinal) && !f.Text.Contains("GetPage(", StringComparison.Ordinal)
                && !f.Text.Contains("routes:", StringComparison.Ordinal) && !f.Text.Contains("onGenerateRoute", StringComparison.Ordinal)) continue;
            var parser = f.Parser();
            var t = f.Tokens;
            var covered = new List<(int From, int To)>();
            for (int i = 0; i < t.Count - 1; i++)
            {
                if (t[i].Kind != DTok.Ident || t[i + 1].Text != "(" || covered.Any(c => i > c.From && i < c.To)) continue;
                string w = t[i].Text;
                if (w is "GoRouter" or "GoRoute" or "ShellRoute" or "StatefulShellRoute")
                {
                    int close = f.Match[i + 1];
                    if (close < 0) continue;
                    var e = parser.ParseAt(i);
                    covered.Add((i, close));
                    if (e is DCall gr && gr.Name == "GoRouter") WalkRoutes(gr.Arg("routes"), "", null, f, AddRoute);
                    else WalkRoutes(e, "", null, f, AddRoute);
                }
                else if (w == "GetPage")
                {
                    if (parser.ParseAt(i) is DCall gp && Literal(gp.Arg("name")) is { } route)
                        AddRoute(route, ScreenCall(gp.Arg("page")), f, null, null);
                }
                else if (w is "MaterialApp" or "CupertinoApp" or "GetMaterialApp" or "WidgetsApp")
                {
                    if (parser.ParseAt(i) is DCall app)
                    {
                        if (app.Arg("routes") is DMap map)
                            foreach (var (k, v) in map.Entries)
                                if (Literal(k) is { } route) AddRoute(route, ScreenCall(v), f, null, null);
                        if (app.Arg("home") is { } home && ScreenCall(home) is { } hc) AddRoute("/", hc, f, null, null);
                    }
                }
            }
            {
                foreach (Match m in Regex.Matches(f.Text, @"case\s+(['""])(?<r>/[^'""]*)\1\s*:\s*return\s+(?:Material|Cupertino)PageRoute(?:<[^>]*>)?\(\s*(?:settings\s*:\s*\w+\s*,\s*)?builder\s*:\s*\([^)]*\)\s*=>\s*(?:const\s+)?(?<w>[A-Z]\w*)\("))
                {
                    int tok = f.TokenAt(m.Groups["w"].Index);
                    if (parser.ParseAt(tok) is DCall c) AddRoute(m.Groups["r"].Value, c, f, null, null);
                }
            }
        }

        // screens no route reaches
        foreach (var c in Classes.Values.OrderBy(c => c.File.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name))
        {
            if (!c.IsWidget || c.Name.StartsWith('_') || routed.Contains(c.Name)) continue;
            // "…Screen" is a screen; "…Page"/"…View" too unless it wraps content passed to it (TpPage(slivers: …) is a layout)
            bool screen = c.Name.EndsWith("Screen", StringComparison.Ordinal)
                          || Regex.IsMatch(c.Name, @"(Page|View)$") && !c.Params.Any(p => p.Name is "child" or "children" or "body" or "slivers" or "content" or "builder" or "items" or "sliver");
            if (!screen) continue;
            string id = "screen:" + c.Name;
            _refs[id] = new ScreenRef(c.Name, null, null, null);
            pages.Add(new StudioPage
            {
                Id = id, Kind = StudioPageKind.Screen, Route = null, Title = WebPages.Humanize(c.Name),
                SourcePath = c.File.Path, SourceLine = c.Line, Detail = c.Name,
            });
        }
        return pages;
    }

    private void WalkRoutes(DExpr? e, string prefix, ShellRef? shell, DartFile file, Action<string, DCall?, DartFile, ShellRef?, string?> add)
    {
        switch (e)
        {
            case DList list:
                foreach (var item in list.Items) WalkRoutes(item, prefix, shell, file, add);
                break;
            case DIf iff:
                WalkRoutes(iff.Then, prefix, shell, file, add);
                WalkRoutes(iff.Else, prefix, shell, file, add);
                break;
            case DSpread sp:
                WalkRoutes(sp.Inner, prefix, shell, file, add);
                break;
            case DCall c when c.Name == "GoRoute":
            {
                string path = Literal(c.Arg("path")) ?? "";
                string full = WebPages.JoinRoute(prefix.Length == 0 ? "/" : prefix, path);
                var builder = c.Arg("builder") ?? c.Arg("pageBuilder");
                add(full, ScreenCall(builder), file, shell, Literal(c.Arg("name")));
                WalkRoutes(c.Arg("routes"), full, shell, file, add);
                break;
            }
            case DCall c when c.Name is "ShellRoute" or "StatefulShellRoute" or "StatefulShellRoute.indexedStack" or "StatefulShellRoute.custom":
            {
                var builder = c.Arg("builder") ?? c.Arg("pageBuilder");
                string? slot = builder is DLambda l && l.Params.Count > 0 ? l.Params[^1] : null;
                var sh = builder != null ? new ShellRef(builder, file, slot) : shell;
                WalkRoutes(c.Arg("routes"), prefix, sh, file, add);
                if (c.Arg("branches") is DList branches)
                    foreach (var b in branches.Items)
                        if (b is DCall bc) WalkRoutes(bc.Arg("routes"), prefix, sh, file, add);
                break;
            }
            case DName n when n.Target == null:
                // routes: appRoutes  — a list declared elsewhere in the same file
                foreach (var v in TopLevelValues(file, n.Path)) WalkRoutes(v, prefix, shell, file, add);
                break;
        }
    }

    private IEnumerable<DExpr> TopLevelValues(DartFile f, string name)
    {
        var t = f.Tokens;
        for (int i = 1; i < t.Count - 2; i++)
            if (t[i].Kind == DTok.Ident && t[i].Text == name && t[i + 1].Text == "=" && t[i - 1].Kind == DTok.Ident)
                yield return f.Parser().ParseAt(i + 2);
    }

    internal static string? Literal(DExpr? e) => e is DStr s && !s.Interpolated ? s.Value : null;

    /// <summary>The screen a route builds: the first project widget constructor in the builder (inside page
    /// wrappers like MaterialPage(child:) or ProviderScope(child:)).</summary>
    internal DCall? ScreenCall(DExpr? e, int depth = 0)
    {
        if (e == null || depth > 6) return null;
        switch (e)
        {
            case DLambda l:
                foreach (var r in l.Returns) if (ScreenCall(r, depth + 1) is { } c) return c;
                return null;
            case DCall call:
                if (call.Target == null && ClassOf(call.Name) is { IsWidget: true }) return call;
                foreach (var key in new[] { "child", "page", "screen", "builder", "body", "home" })
                    if (call.Arg(key) is { } inner && ScreenCall(inner, depth + 1) is { } c) return c;
                foreach (var a in call.Args) if (ScreenCall(a, depth + 1) is { } c) return c;
                return null;
            case DIf iff:
                return ScreenCall(iff.Else, depth + 1) ?? ScreenCall(iff.Then, depth + 1);
            default:
                return null;
        }
    }

    // ───────────────────────────── sketches ─────────────────────────────

    /// <summary>Draw a page as a wireframe (null when it is not one of this project's screens).</summary>
    public ScreenSketch? Sketch(StudioPage page)
    {
        if (!_refs.TryGetValue(page.Id, out var r) || ClassOf(r.ClassName) is not { } cls) return null;
        var sketcher = new FlutterSketcher(this);
        return sketcher.Screen(cls, r.Call, r.CallFile, r.Shell);
    }
}
