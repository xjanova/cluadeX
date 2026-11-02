using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// HTML, CSS and JS/TS (with Vue, Svelte and Astro): pages load scripts and stylesheets, modules import modules, and
/// an import becomes "renders" (used as a tag), "inherits"/"implements" (extended) or "calls" (called). Paths resolve
/// like the bundler does: relative, tsconfig/jsconfig <c>paths</c> + <c>baseUrl</c> (with <c>extends</c>), Vite
/// <c>resolve.alias</c>, and workspace packages by their package.json name. An alias no config declares is not
/// guessed silently: <c>@/</c> → src/ is kept as an inferred edge.
/// </summary>
internal static class WebReader
{
    private static readonly string[] Code = { ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro" };
    private static readonly string[] Styles = { ".css", ".scss", ".sass", ".less" };

    private static readonly Regex ImportFrom = new(@"(?<![\w$.])import\s+(?:type\s+)?(?<what>[\w*{}\s,$]*?)\s*from\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex ImportBare = new(@"(?<![\w$.])import\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex ImportCall = new(@"(?<![\w$.])(?:require|import)\s*\(\s*(?<q>['""`])(?<p>[^'""`\n]*)\k<q>\s*\)", RegexOptions.Compiled);
    private static readonly Regex ExportFrom = new(@"(?<![\w$.])export\s+(?:type\s+)?(?:\*(?:\s+as\s+[\w$]+)?|\{[^}]*\})\s*from\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex CssImport = new(@"@(?:import|use|forward)\s+(?:url\(\s*)?(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex HtmlRef = new(
        @"<script\b[^>]*?\ssrc\s*=\s*(?<q>['""])(?<s>[^'""]*)\k<q>|<link\b[^>]*?\shref\s*=\s*(?<q>['""])(?<l>[^'""]*\.(?:css|scss|less)(?:[?#][^'""]*)?)\k<q>|<a\b[^>]*?\shref\s*=\s*(?<q>['""])(?<a>[^'""#?:]+\.html?)(?:[#?][^'""]*)?\k<q>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] TryExt =
    {
        "", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro", ".css", ".scss", ".sass", ".less",
        "/index.ts", "/index.tsx", "/index.js", "/index.jsx", "/index.mjs", "/index.vue", "/index.svelte",
    };

    private sealed record PathRule(string Prefix, string Suffix, bool Star, List<string> Targets);
    private sealed class TsConfig
    {
        public string Dir = "";
        public string? BaseUrl;
        public List<PathRule> Paths = new();
        public string File = "";
    }

    public static void Read(GraphContext cx)
    {
        var web = cx.Files.Where(f => Code.Contains(f.Ext) || Styles.Contains(f.Ext) || f.Ext is ".html" or ".htm").ToList();
        if (web.Count == 0) return;
        foreach (var f in web) cx.Graph.ById[f.Id].Kind = KindOf(f);
        var configs = LoadTsConfigs(cx);
        var aliases = LoadViteAliases(cx);
        var workspaces = LoadWorkspaces(cx);

        foreach (var file in web)
        {
            cx.Ct.ThrowIfCancellationRequested();
            if (file.Ext is ".html" or ".htm") { ReadHtml(cx, file); continue; }
            if (Styles.Contains(file.Ext))
            {
                foreach (Match m in CssImport.Matches(file.Masked))
                    Link(cx, file, Spec(file, m), m.Index, GraphEdgeKind.Imports, relativeByDefault: true, configs, aliases, workspaces);
                continue;
            }
            ReadModule(cx, file, configs, aliases, workspaces);
        }
    }

    public static GraphNodeKind KindOf(SourceFile f)
    {
        string name = Path.GetFileNameWithoutExtension(f.Full);
        return f.Ext is ".html" or ".htm" ? GraphNodeKind.Page
            : Styles.Contains(f.Ext) ? GraphNodeKind.Style
            : f.Ext is ".vue" or ".svelte" or ".astro" || f.Ext is ".jsx" or ".tsx" && name.Length > 0 && char.IsUpper(name[0]) ? GraphNodeKind.Component
            : GraphNodeKind.Script;
    }

    private static string Spec(SourceFile f, Match m) => f.Text.Substring(m.Groups["p"].Index, m.Groups["p"].Length);

    private static void ReadHtml(GraphContext cx, SourceFile file)
    {
        foreach (Match m in HtmlRef.Matches(file.Masked))
        {
            // the masked text keeps attribute values (HTML has no string syntax to blank), only comments are gone
            if (m.Groups["s"].Success) Link(cx, file, m.Groups["s"].Value, m.Index, GraphEdgeKind.Loads, true, null, null, null);
            else if (m.Groups["l"].Success) Link(cx, file, m.Groups["l"].Value, m.Index, GraphEdgeKind.Loads, true, null, null, null);
            else if (m.Groups["a"].Success) Link(cx, file, m.Groups["a"].Value, m.Index, GraphEdgeKind.Links, true, null, null, null);
        }
    }

    private static void ReadModule(GraphContext cx, SourceFile file, List<TsConfig> configs, Dictionary<string, (string Dir, string Alias)> aliases,
        Dictionary<string, string> workspaces)
    {
        string code = file.Masked;
        foreach (Match m in ImportFrom.Matches(code))
        {
            var target = Link(cx, file, Spec(file, m), m.Index, GraphEdgeKind.Imports, false, configs, aliases, workspaces);
            if (target is not { } t || t.File == null) continue;
            foreach (var local in LocalNames(m.Groups["what"].Value))
                Usage(cx, file, t.File, local, m, t.Inferred, t.Why);
        }
        foreach (var rx in new[] { ImportBare, ImportCall, ExportFrom })
            foreach (Match m in rx.Matches(code))
            {
                string spec = Spec(file, m);
                if (spec.Contains("${", StringComparison.Ordinal)) continue;   // a computed path is not a file
                Link(cx, file, spec, m.Index, GraphEdgeKind.Imports, false, configs, aliases, workspaces);
            }
    }

    /// <summary>The names an import binds here: default, {a, b as c} → c, * as ns.</summary>
    private static IEnumerable<string> LocalNames(string what)
    {
        what = what.Trim();
        if (what.Length == 0) yield break;
        int brace = what.IndexOf('{');
        string head = brace >= 0 ? what[..brace] : what;
        foreach (var part in head.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ns = Regex.Match(part, @"^\*\s+as\s+([\w$]+)$");
            if (ns.Success) yield return ns.Groups[1].Value;
            else if (Regex.IsMatch(part, @"^[\w$]+$")) yield return part;
        }
        if (brace >= 0)
        {
            int close = what.IndexOf('}', brace);
            string inner = what[(brace + 1)..(close > brace ? close : what.Length)];
            foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = Regex.Replace(part, @"^type\s+", "");
                var alias = Regex.Match(p, @"^[\w$]+\s+as\s+([\w$]+)$");
                if (alias.Success) yield return alias.Groups[1].Value;
                else if (Regex.IsMatch(p, @"^[\w$]+$")) yield return p;
            }
        }
    }

    /// <summary>How the imported name is used in this file: as a tag (renders), extended (inherits), implemented, or
    /// called. Each use is its own edge with the line of the use.</summary>
    private static void Usage(GraphContext cx, SourceFile file, SourceFile target, string local, Match import, bool inferred, string? why)
    {
        string code = file.Masked;
        string esc = Regex.Escape(local);
        int skipFrom = import.Index, skipTo = import.Index + import.Length;
        Match? First(string pattern)
        {
            foreach (Match m in Regex.Matches(code, pattern))
                if (m.Index < skipFrom || m.Index >= skipTo) return m;
            return null;
        }
        void Add(Match? m, GraphEdgeKind kind)
        {
            if (m != null) cx.Edge(file, target, kind, file.Lines.LineOf(m.Index), inferred, why);
        }
        if (char.IsUpper(local[0]))
        {
            // a tag, not a type argument: List<Item> has a name right before the '<', `return <Item` has a space
            if (file.Ext is ".jsx" or ".tsx" or ".vue" or ".svelte" or ".astro")
                Add(First(@"(?<![\w$\])])<" + esc + @"(?=[\s/>.])"), GraphEdgeKind.Renders);
            string kebab = Regex.Replace(local, @"(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant();
            if (kebab.Contains('-') && file.Ext is ".vue") Add(First(@"<" + Regex.Escape(kebab) + @"(?=[\s/>])"), GraphEdgeKind.Renders);
            Add(First(@"\bextends\s+" + esc + @"\b"), GraphEdgeKind.Inherits);
            Add(First(@"\bimplements\s+(?:[\w$.<>]+\s*,\s*)*" + esc + @"\b"), GraphEdgeKind.Implements);
            Add(First(@"\bnew\s+" + esc + @"\s*[(<]"), GraphEdgeKind.References);
        }
        Add(First(@"(?<![\w$.]|new\s)" + esc + @"\s*\("), GraphEdgeKind.Calls);
        Add(First(@"(?<![\w$.])" + esc + @"\.[\w$]+\s*\("), GraphEdgeKind.Calls);
    }

    private sealed record Resolved(SourceFile? File, bool Inferred, string? Why);

    /// <summary>Add the edge for one path written in the code, resolved to a project file or (when asked for) a
    /// package. Returns what it resolved to.</summary>
    private static Resolved? Link(GraphContext cx, SourceFile from, string spec, int at, GraphEdgeKind kind, bool relativeByDefault,
        List<TsConfig>? configs, Dictionary<string, (string Dir, string Alias)>? aliases, Dictionary<string, string>? workspaces)
    {
        spec = spec.Trim();
        if (spec.Length == 0 || spec.Contains("://", StringComparison.Ordinal) || spec.StartsWith("//", StringComparison.Ordinal)
            || spec.StartsWith("data:", StringComparison.Ordinal) || spec.StartsWith("mailto:", StringComparison.Ordinal)) return null;
        int line = from.Lines.LineOf(at);
        var r = Resolve(cx, from, spec, relativeByDefault, configs ?? new(), aliases ?? new(), workspaces ?? new());
        if (r?.File != null)
        {
            cx.Edge(from, r.File, kind, line, r.Inferred, r.Why);
            return r;
        }
        if (relativeByDefault || spec.StartsWith('.') || spec.StartsWith('/') || spec.StartsWith("@/", StringComparison.Ordinal)
            || spec.StartsWith("~/", StringComparison.Ordinal) || spec.StartsWith('#') || spec.StartsWith("virtual:", StringComparison.Ordinal)) return null;
        var parts = spec.Split('/');
        string pkg = spec.StartsWith('@') && parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0];
        if (pkg.StartsWith("node:", StringComparison.Ordinal)) pkg = pkg[5..];
        if (cx.Package(pkg, "npm") is { } id) cx.Edge(from, id, kind, line);
        return null;
    }

    private static Resolved? Resolve(GraphContext cx, SourceFile from, string spec, bool relativeByDefault, List<TsConfig> configs,
        Dictionary<string, (string Dir, string Alias)> aliases, Dictionary<string, string> workspaces)
    {
        // a query or a fragment is not part of the file — but a leading '#' is a name ('#shared', Node subpath imports)
        int cut = spec.IndexOfAny(new[] { '?', '#' }, spec.StartsWith('#') ? 1 : 0);
        string clean = cut < 0 ? spec : spec[..cut];
        if (clean.Length == 0) return null;
        if (clean.StartsWith("./", StringComparison.Ordinal) || clean.StartsWith("../", StringComparison.Ordinal) || clean is "." or ".."
            || relativeByDefault && !clean.StartsWith('/'))
            return Found(TryFile(cx, Path.Combine(from.Dir, clean)));
        if (clean.StartsWith('/'))
        {
            // the site root: the project, or its public/ folder (Vite, Laravel)
            var f = TryFile(cx, Path.Combine(cx.FullRoot, clean.TrimStart('/'))) ?? TryFile(cx, Path.Combine(cx.FullRoot, "public", clean.TrimStart('/')));
            return Found(f);
        }

        var config = configs.Where(c => from.Full.StartsWith(c.Dir, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Dir.Length).FirstOrDefault();
        if (config != null)
        {
            // the longest matching prefix wins, as in TypeScript
            foreach (var rule in config.Paths.OrderByDescending(p => p.Prefix.Length))
            {
                string? rest = null;
                if (rule.Star && clean.StartsWith(rule.Prefix, StringComparison.Ordinal) && clean.EndsWith(rule.Suffix, StringComparison.Ordinal)
                    && clean.Length >= rule.Prefix.Length + rule.Suffix.Length)
                    rest = clean[rule.Prefix.Length..(clean.Length - rule.Suffix.Length)];
                else if (!rule.Star && clean == rule.Prefix) rest = "";
                if (rest == null) continue;
                foreach (var target in rule.Targets)
                {
                    string path = rule.Star ? target.Replace("*", rest) : target;
                    var f = TryFile(cx, Path.Combine(config.BaseUrl ?? config.Dir, path));
                    if (f != null) return new Resolved(f, false, null);
                }
            }
        }
        foreach (var (prefix, (dir, _)) in aliases.OrderByDescending(a => a.Key.Length))
        {
            if (!from.Full.StartsWith(cx.FullRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (clean == prefix || clean.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                var f = TryFile(cx, Path.Combine(dir, clean[prefix.Length..].TrimStart('/')));
                if (f != null) return new Resolved(f, false, null);
            }
        }
        if (config?.BaseUrl != null && !clean.StartsWith('@') && TryFile(cx, Path.Combine(config.BaseUrl, clean)) is { } viaBase)
            return new Resolved(viaBase, false, null);
        foreach (var (name, dir) in workspaces.OrderByDescending(w => w.Key.Length))
            if (clean == name || clean.StartsWith(name + "/", StringComparison.Ordinal))
            {
                string sub = clean[name.Length..].TrimStart('/');
                var f = sub.Length > 0 ? TryFile(cx, Path.Combine(dir, sub)) ?? TryFile(cx, Path.Combine(dir, "src", sub))
                    : TryFile(cx, Path.Combine(dir, "src", "index")) ?? TryFile(cx, Path.Combine(dir, "index"));
                if (f != null) return new Resolved(f, false, null);
            }
        if (clean.StartsWith("@/", StringComparison.Ordinal) || clean.StartsWith("~/", StringComparison.Ordinal))
        {
            var f = TryFile(cx, Path.Combine(cx.FullRoot, "src", clean[2..]));
            if (f != null) return new Resolved(f, true, $"'{clean[..2]}' is not declared in tsconfig/jsconfig paths or the Vite aliases — read as src/");
        }
        return null;

        static Resolved? Found(SourceFile? f) => f == null ? null : new Resolved(f, false, null);
    }

    private static SourceFile? TryFile(GraphContext cx, string basePath)
    {
        foreach (var e in TryExt)
            if (cx.FileAt(basePath + e) is { } f) return f;
        // ESM TypeScript writes the output name: './util.js' is util.ts on disk
        string ext = Path.GetExtension(basePath);
        if (ext is ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            string stem = basePath[..^ext.Length];
            foreach (var e in new[] { ".ts", ".tsx", ".mts", ".cts" })
                if (cx.FileAt(stem + e) is { } f) return f;
        }
        return null;
    }

    // ───────────────────────────── configs ─────────────────────────────

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static List<TsConfig> LoadTsConfigs(GraphContext cx)
    {
        var list = new List<TsConfig>();
        foreach (var f in cx.Configs.Where(c => Path.GetFileName(c.Full) is "tsconfig.json" or "jsconfig.json"))
        {
            var tc = new TsConfig { Dir = f.Dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, File = f.Id };
            Merge(cx, f.Full, tc, 0);
            list.Add(tc);
        }
        return list;
    }

    /// <summary>Read compilerOptions.baseUrl/paths from a config and what it extends (relative extends only; the
    /// extending file wins).</summary>
    private static void Merge(GraphContext cx, string path, TsConfig into, int depth)
    {
        if (depth > 5) return;
        string? text = cx.Configs.FirstOrDefault(c => string.Equals(c.Full, path, StringComparison.OrdinalIgnoreCase))?.Text;
        if (text == null)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length < ProjectScan.MaxFileBytes) text = File.ReadAllText(path); }
            catch { }
        }
        if (text == null) return;
        try
        {
            using var doc = JsonDocument.Parse(text, Jsonc);
            var root = doc.RootElement;
            string dir = Path.GetDirectoryName(path)!;
            if (root.TryGetProperty("extends", out var ext) && ext.ValueKind == JsonValueKind.String && ext.GetString() is { } parent && parent.StartsWith('.'))
            {
                string p = Path.GetFullPath(Path.Combine(dir, parent));
                if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) p += ".json";
                Merge(cx, p, into, depth + 1);
            }
            if (!root.TryGetProperty("compilerOptions", out var opts) || opts.ValueKind != JsonValueKind.Object) return;
            if (opts.TryGetProperty("baseUrl", out var b) && b.GetString() is { } baseUrl) into.BaseUrl = Path.GetFullPath(Path.Combine(dir, baseUrl));
            if (opts.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
            {
                into.Paths.Clear();
                foreach (var prop in paths.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                    // paths are relative to baseUrl, or to the config that declares them
                    string relTo = into.BaseUrl ?? dir;
                    var targets = prop.Value.EnumerateArray().Select(t => t.GetString()).Where(t => t != null)
                        .Select(t => Path.GetFullPath(Path.Combine(relTo, t!.Replace("*", "__STAR__"))).Replace("__STAR__", "*")).ToList();
                    int star = prop.Name.IndexOf('*');
                    into.Paths.Add(star >= 0
                        ? new PathRule(prop.Name[..star], prop.Name[(star + 1)..], true, targets)
                        : new PathRule(prop.Name, "", false, targets));
                }
            }
        }
        catch (JsonException) { }
    }

    private static readonly Regex ViteAlias = new(
        @"(?<q>['""])(?<a>[@~#$\w][\w/@~#$-]*)\k<q>\s*:\s*(?:path\.)?(?:resolve|join)\(\s*__dirname\s*,\s*(?<q2>['""])(?<p>[^'""]*)\k<q2>"
        + @"|(?<q>['""])(?<a>[@~#$\w][\w/@~#$-]*)\k<q>\s*:\s*fileURLToPath\(\s*new\s+URL\(\s*(?<q2>['""])(?<p>[^'""]*)\k<q2>"
        + @"|find\s*:\s*(?<q>['""])(?<a>[@~#$\w][\w/@~#$-]*)\k<q>\s*,\s*replacement\s*:\s*(?:path\.)?(?:resolve|join)\(\s*__dirname\s*,\s*(?<q2>['""])(?<p>[^'""]*)\k<q2>",
        RegexOptions.Compiled);

    private static Dictionary<string, (string Dir, string Alias)> LoadViteAliases(GraphContext cx)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var f in cx.Configs.Where(c => Path.GetFileName(c.Full).StartsWith("vite.config", StringComparison.OrdinalIgnoreCase)))
        {
            string masked = CodeMask.Mask(f.Text, CodeMask.Lang.Js);
            // the alias names are strings, so match on the source but only where the masked text has code
            foreach (Match m in ViteAlias.Matches(f.Text))
            {
                if (masked[m.Index] != f.Text[m.Index]) continue;   // inside a comment
                string alias = m.Groups["a"].Value.TrimEnd('/');
                string target = Path.GetFullPath(Path.Combine(f.Dir, m.Groups["p"].Value.TrimStart('/').Length == 0 ? "." : m.Groups["p"].Value));
                map[alias] = (target, alias);
            }
        }
        return map;
    }

    private static Dictionary<string, string> LoadWorkspaces(GraphContext cx)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in cx.Configs.Where(c => Path.GetFileName(c.Full) == "package.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(f.Text, Jsonc);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name
                    && !string.Equals(f.Dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cx.FullRoot, StringComparison.OrdinalIgnoreCase))
                    map[name] = f.Dir;
            }
            catch (JsonException) { }
        }
        return map;
    }
}
