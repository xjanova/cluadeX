using System.IO;
using System.Text;
using CluadeX.Services.UiStudio;

namespace GraphTest;

/// <summary>
/// The Node Graph's project mode, driven through the REAL built CluadeX.dll: one fixture project per language
/// (C#/WPF, Flutter/Dart, Laravel/PHP, Python, TypeScript with aliases) must give EXACTLY the expected edges —
/// kind, inferred or not, and the file:line each stands on. Code in comments, strings, docstrings and `#if` blocks
/// makes none. Then the bounds: a huge synthetic repo (caps hold, a binary file named .js and an oversized file are
/// skipped, the read is cancellable), a folder deleted between two builds, CRLF and UTF-16 files.
/// Run: build CluadeX first, then `dotnet run` (report: bin/Debug/net8.0-windows/graph-report.md).
/// `dotnet run -- --dump [fixture…]` prints the edges instead of checking them.
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;
    static readonly StringBuilder Report = new();

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else Fail++;
        string line = (ok ? "  PASS " : "  FAIL ") + name;
        Console.WriteLine(line);
        Report.AppendLine((ok ? "- ✅ " : "- ❌ ") + name);
        if (!ok && detail.Length > 0)
        {
            Console.WriteLine("       " + detail.Replace("\n", "\n       "));
            Report.AppendLine("  ```\n  " + detail.Replace("\n", "\n  ") + "\n  ```");
        }
    }

    static void Section(string title)
    {
        Console.WriteLine("\n== " + title);
        Report.AppendLine("\n## " + title);
    }

    static string Fixtures => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures"));

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "--dump") return Dump(args[1..]);

        var total = System.Diagnostics.Stopwatch.StartNew();
        Fixture("wpf", WpfEdges, new[] { "Views/OldView.xaml.cs", "Legacy.cs" });
        Fixture("flutter", FlutterEdges, new[] { "lib/widgets/old_banner.dart" });
        Fixture("laravel", LaravelEdges, new[] { "resources/views/partials/old.blade.php" });
        Fixture("python", PythonEdges, new[] { "app/legacy.py" });
        Fixture("ts", TsEdges, new[] { "src/Old.tsx", "src/Older.tsx", "src/Fake.ts", "src/Fake2.ts", "src/old.css", "src/old.ts" });
        Extra();
        Bounds();
        StaticXaml();

        Console.WriteLine($"\n{Pass} passed, {Fail} failed ({total.Elapsed.TotalSeconds:0.0} s)");
        Report.Insert(0, $"# graphtest — {Pass} passed, {Fail} failed\n\nRun {DateTime.Now:yyyy-MM-dd HH:mm}, {total.Elapsed.TotalSeconds:0.0} s.\n");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "graph-report.md"), Report.ToString());
        return Fail == 0 ? 0 : 1;
    }

    static ProjectGraph Build(string root, int maxFiles = ProjectGraphBuilder.DefaultMaxFiles, CancellationToken ct = default)
        => ProjectGraphBuilder.Build(root, false, ct, maxFiles);

    /// <summary>Exactly these edges ("from -Kind-> to @file:line", "?" after the kind = inferred), every inferred one
    /// with its reason, every evidence line really saying it, and the files that are named only in comments or
    /// strings left without any edge.</summary>
    static void Fixture(string name, string[] expected, string[] untouched)
    {
        Section(name);
        string root = Path.Combine(Fixtures, name);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = Build(root);
        long cold = sw.ElapsedMilliseconds;
        var actual = g.Edges.Select(e => e.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var want = expected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missing = want.Except(actual).ToList();
        var extra = actual.Except(want).ToList();
        Check($"{name}: exactly the {want.Count} expected edges (kind, inferred, first evidence line)", missing.Count == 0 && extra.Count == 0,
            (missing.Count > 0 ? "missing:\n  " + string.Join("\n  ", missing) + "\n" : "") + (extra.Count > 0 ? "unexpected:\n  " + string.Join("\n  ", extra) : ""));

        foreach (var file in untouched)
            Check($"{name}: {file} is named only in comments, strings or a disabled block — it is a node with no edge",
                g.ById.ContainsKey(file) && !g.Edges.Any(e => e.From == file || e.To == file),
                string.Join("; ", g.Edges.Where(e => e.From == file || e.To == file)));

        Check($"{name}: every inferred edge says why, no confirmed one does", g.Edges.All(e => e.Inferred == !string.IsNullOrWhiteSpace(e.Why)),
            string.Join("; ", g.Edges.Where(e => e.Inferred == string.IsNullOrWhiteSpace(e.Why))));

        // every evidence line is a real line of that file, and its text is that line
        var bad = new List<string>();
        foreach (var e in g.Edges)
        {
            if (e.Evidence.Count == 0 || e.Count < e.Evidence.Count) bad.Add($"{e}: {e.Evidence.Count} evidence, count {e.Count}");
            foreach (var ev in e.Evidence)
            {
                if (!g.ById.TryGetValue(ev.File, out var n) || n.FullPath == null) { bad.Add($"{e}: {ev.File} is no node"); continue; }
                var lines = File.ReadAllText(n.FullPath).Replace("\r\n", "\n").Split('\n');
                if (ev.Line < 1 || ev.Line > lines.Length) { bad.Add($"{e}: line {ev.Line} of {lines.Length}"); continue; }
                string real = lines[ev.Line - 1].Trim();
                string shown = ev.Text.TrimEnd('…').Trim();
                if (shown.Length == 0 || !real.StartsWith(shown, StringComparison.Ordinal)) bad.Add($"{e}: '{ev.Text}' is not line {ev.Line} ('{real}')");
            }
        }
        Check($"{name}: every evidence line exists and shows that line's code", bad.Count == 0, string.Join("\n", bad));

        sw.Restart();
        var warm = Build(root);
        long warmMs = sw.ElapsedMilliseconds;
        Check($"{name}: a second build (files unchanged) gives the same edges", warm.Edges.Select(e => e.ToString()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual));
        Report.AppendLine($"  - cold {cold} ms ({g.Timings}), warm {warmMs} ms");
    }

    static readonly string[] WpfEdges =
    {
        "App.xaml -CodeBehind-> App.xaml.cs @App.xaml:1",
        "App.xaml -Loads-> MainWindow.xaml @App.xaml:4",
        "App.xaml -Loads-> Themes/Dark.xaml @App.xaml:8",
        "App.xaml.cs -Registers-> Services/IClock.cs @App.xaml.cs:10",
        "App.xaml.cs -Registers-> Services/SystemClock.cs @App.xaml.cs:10",
        "App.xaml.cs -Registers-> ViewModels/MainViewModel.cs @App.xaml.cs:11",
        "MainWindow.xaml -CodeBehind-> MainWindow.xaml.cs @MainWindow.xaml:1",
        "MainWindow.xaml -Binds-> ViewModels/MainViewModel.Commands.cs @MainWindow.xaml:11",
        "MainWindow.xaml -Binds-> ViewModels/MainViewModel.cs @MainWindow.xaml:7",
        "MainWindow.xaml -Renders-> Views/ClockView.xaml @MainWindow.xaml:12",
        "Services/SystemClock.cs -Implements-> Services/IClock.cs @Services/SystemClock.cs:3",
        "Ticking/Ticker.cs -References?-> Services/SystemClock.cs @Ticking/Ticker.cs:5",
        "ViewModels/MainViewModel.Commands.cs -References-> ViewModels/MainViewModel.cs @ViewModels/MainViewModel.Commands.cs:3",
        "ViewModels/MainViewModel.cs -Calls-> Services/IClock.cs @ViewModels/MainViewModel.cs:12",
        "ViewModels/MainViewModel.cs -Inherits-> ViewModels/ViewModelBase.cs @ViewModels/MainViewModel.cs:5",
        "Views/ClockView.xaml -Binds?-> ViewModels/ClockViewModel.cs @Views/ClockView.xaml:4",
        "Views/ClockView.xaml -CodeBehind-> Views/ClockView.xaml.cs @Views/ClockView.xaml:1",
    };

    static readonly string[] FlutterEdges =
    {
        "lib/main.dart -Imports-> lib/routes.dart @lib/main.dart:2",
        "lib/main.dart -Registers-> lib/services/cart_service.dart @lib/main.dart:9",
        "lib/routes.dart -Routes-> lib/screens/detail.dart @lib/routes.dart:6",
        "lib/routes.dart -Routes-> lib/screens/home.dart @lib/routes.dart:5",
        "lib/screens/detail.dart -Inherits-> lib/screens/base_screen.dart @lib/screens/detail.dart:5",
        "lib/screens/detail.dart -Calls-> lib/utils/format.dart @lib/screens/detail.dart:10",
        "lib/screens/detail.dart -Renders?-> lib/widgets/helper_banner.dart @lib/screens/detail.dart:10",
        "lib/screens/home.dart -Routes-> lib/screens/detail.dart @lib/screens/home.dart:11",
        "lib/screens/home.dart -Renders-> lib/widgets/product_card.dart @lib/screens/home.dart:12",
    };

    static readonly string[] LaravelEdges =
    {
        "app/Http/Controllers/OldController.php -Inherits-> app/Http/Controllers/Controller.php @app/Http/Controllers/OldController.php:5",
        "app/Http/Controllers/UserController.php -Inherits-> app/Http/Controllers/Controller.php @app/Http/Controllers/UserController.php:7",
        "app/Http/Controllers/UserController.php -Calls-> app/Models/User.php @app/Http/Controllers/UserController.php:11",
        "app/Http/Controllers/UserController.php -Renders-> resources/views/users/index.blade.php @app/Http/Controllers/UserController.php:11",
        "app/Providers/AppServiceProvider.php -Registers-> app/Contracts/PaymentGateway.php @app/Providers/AppServiceProvider.php:12",
        "app/Providers/AppServiceProvider.php -Registers-> app/Services/StripeGateway.php @app/Providers/AppServiceProvider.php:12",
        "app/Services/ReportService.php -References?-> app/Models/Invoice.php @app/Services/ReportService.php:9",
        "app/Services/StripeGateway.php -Implements-> app/Contracts/PaymentGateway.php @app/Services/StripeGateway.php:7",
        "resources/views/users/index.blade.php -Renders-> resources/views/components/alert.blade.php @resources/views/users/index.blade.php:4",
        "resources/views/users/index.blade.php -Inherits-> resources/views/layouts/app.blade.php @resources/views/users/index.blade.php:1",
        "routes/web.php -Routes-> app/Http/Controllers/UserController.php @routes/web.php:6",
        "routes/web.php -Routes-> resources/views/about.blade.php @routes/web.php:7",
    };

    static readonly string[] PythonEdges =
    {
        "app/models.py -Inherits-> app/db.py @app/models.py:4",
        "app/views.py -Imports-> app/models.py @app/views.py:1",
        "app/views.py -Calls-> app/services/mailer.py @app/views.py:13",
        "app/views.py -Calls?-> tools/helpers.py @app/views.py:14",
        "urls.py -Routes-> app/api/urls.py @urls.py:6",
        "urls.py -Routes-> app/views.py @urls.py:5",
    };

    static readonly string[] TsEdges =
    {
        "index.html -Loads-> src/main.tsx @index.html:4",
        "src/App.tsx -Calls-> shared/util.ts @src/App.tsx:13",
        "src/App.tsx -Renders-> src/components/Button.tsx @src/App.tsx:14",
        "src/App.tsx -Imports?-> src/legacy.ts @src/App.tsx:4",
        "src/App.tsx -Calls-> src/lib/index.ts @src/App.tsx:13",
        "src/App.tsx -Imports-> utils/store.ts @src/App.tsx:3",
        "src/main.tsx -Renders-> src/App.tsx @src/main.tsx:5",
        "src/main.tsx -Imports-> src/styles.css @src/main.tsx:3",
        "src/styles.css -Imports-> src/theme.css @src/styles.css:2",
        "utils/store.ts -Inherits-> src/core/base.ts @utils/store.ts:3",
    };

    /// <summary>What the edge sets alone do not show: labels, the second line of a route, folded kinds.</summary>
    static void Extra()
    {
        Section("labels and second evidence");
        var lv = Build(Path.Combine(Fixtures, "laravel"));
        var route = lv.Edges.Single(e => e.From == "routes/web.php" && e.To.EndsWith("UserController.php"));
        Check("laravel: a route is labelled with its verb, URI and action", route.Label == "GET /users → index", route.Label ?? "");
        Check("laravel: …and its evidence goes on to the controller method", route.Evidence.Any(ev => ev.File == "app/Http/Controllers/UserController.php" && ev.Line == 9),
            string.Join(",", route.Evidence.Select(ev => ev.File + ":" + ev.Line)));
        Check("laravel: the commented route and the route in a string make no edge to OldController",
            !lv.Edges.Any(e => e.Kind == GraphEdgeKind.Routes && e.To.EndsWith("OldController.php")));

        var ts = Build(Path.Combine(Fixtures, "ts"));
        Check("ts: an import that is also called is one Calls edge (the weaker Imports folds into it)",
            ts.Edges.Count(e => e.From == "src/App.tsx" && e.To == "src/lib/index.ts") == 1);
        Check("ts: the tsconfig 'extends' chain and the Vite alias both resolve", ts.Edges.Any(e => e.To == "utils/store.ts") && ts.Edges.Any(e => e.To == "shared/util.ts"));
        Check("ts: config files are read, never drawn", !ts.ById.ContainsKey("tsconfig.json") && !ts.ById.ContainsKey("vite.config.ts"));

        var wpf = Build(Path.Combine(Fixtures, "wpf"));
        var bind = wpf.Edges.Single(e => e.From == "MainWindow.xaml" && e.To == "ViewModels/MainViewModel.cs");
        Check("wpf: the DataContext element and a binding to it are one Binds edge with both lines", bind.Evidence.Select(ev => ev.Line).SequenceEqual(new[] { 7, 10 }),
            string.Join(",", bind.Evidence.Select(ev => ev.Line)));
        Check("wpf: a binding to a property the view model does not have makes no edge", !wpf.Edges.Any(e => e.Label?.Contains("Missing") == true));
        Check("wpf: the partial part points at the primary file, labelled", wpf.Edges.Any(e => e.From.EndsWith("MainViewModel.Commands.cs") && e.Label == "partial"));
        Check("wpf: nodes are typed (a Window is a page, a UserControl a component)",
            wpf.ById["MainWindow.xaml"].Kind == GraphNodeKind.Page && wpf.ById["Views/ClockView.xaml"].Kind == GraphNodeKind.Component,
            $"{wpf.ById["MainWindow.xaml"].Kind} {wpf.ById["Views/ClockView.xaml"].Kind}");
        Check("all: a node knows its folder (for group by folder)", wpf.ById["Views/ClockView.xaml"].Folder == "Views" && wpf.ById["App.xaml"].Folder == "");
    }

    /// <summary>The read is bounded and never chokes on what a real repo holds.</summary>
    static void Bounds()
    {
        Section("bounds");
        string temp = Path.Combine(Path.GetTempPath(), "cluadex-graphtest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ── a huge repo: 3200 modules in 40 folders, each importing the next ──
            string big = Path.Combine(temp, "big");
            for (int i = 0; i < 3200; i++)
            {
                string dir = Path.Combine(big, "src", "m" + (i % 40));
                Directory.CreateDirectory(dir);
                int next = (i + 1) % 3200;
                File.WriteAllText(Path.Combine(dir, $"f{i}.ts"), $"import {{ v{next} }} from '../m{next % 40}/f{next}'\nexport const v{i} = v{next} + 1\n");
            }
            // things a repo holds that are not source
            var bin = new byte[4096]; new Random(1).NextBytes(bin); bin[10] = 0;
            File.WriteAllBytes(Path.Combine(big, "a-binary.js"), bin);
            File.WriteAllText(Path.Combine(big, "huge.ts"), "import x from './src/m0/f0'\n" + new string('/', 600 * 1024));
            File.WriteAllText(Path.Combine(big, "app.min.js"), "import './src/m0/f0';" + string.Concat(Enumerable.Repeat("var a=1;", 2000)));
            Directory.CreateDirectory(Path.Combine(big, "node_modules", "react"));
            File.WriteAllText(Path.Combine(big, "node_modules", "react", "index.js"), "export default 1");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Build(big);
            long ms = sw.ElapsedMilliseconds;
            Check($"huge repo: at most {ProjectGraphBuilder.DefaultMaxFiles} files read, and the graph says it stopped short",
                g.Nodes.Count <= ProjectGraphBuilder.DefaultMaxFiles && g.Truncated && g.TruncatedWhy == "files", $"{g.Nodes.Count} nodes, truncated={g.Truncated} {g.TruncatedWhy}");
            Check("huge repo: the edges among the files read are there", g.Edges.Count > 2000, g.Edges.Count.ToString());
            Check($"huge repo: built in {ms} ms (bound: 60 s, on a loaded machine)", ms < 60_000);
            var small = Build(big, maxFiles: 50);
            Check("huge repo: a smaller cap holds too", small.Nodes.Count <= 50 && small.Truncated, small.Nodes.Count.ToString());

            // the odd files, apart from the cap (a repo with them and few sources)
            string odd = Path.Combine(temp, "odd");
            Directory.CreateDirectory(Path.Combine(odd, "src"));
            foreach (var f in new[] { "a-binary.js", "huge.ts", "app.min.js" }) File.Copy(Path.Combine(big, f), Path.Combine(odd, f));
            File.WriteAllText(Path.Combine(odd, "src", "b.ts"), "export const b = 1\n");
            File.WriteAllText(Path.Combine(odd, "src", "crlf.ts"), "// first\r\n\r\nimport { b } from './b'\r\nexport const c = b\r\n");
            File.WriteAllText(Path.Combine(odd, "src", "wide.ts"), "\n\nimport { b } from './b'\nexport const w = b\n", new UnicodeEncoding(false, true));
            File.WriteAllText(Path.Combine(odd, "src", "wide-be.ts"), "import { b } from './b'\n", new UnicodeEncoding(true, true));
            File.WriteAllText(Path.Combine(odd, "src", "thai.ts"), "// ภาษาไทย\nimport { b } from './b'\nconst s = 'สวัสดี'\n", new UTF8Encoding(true));
            // an entity bomb in a XAML file (a hostile repo): the XML reader caps the expansion, the read goes on
            File.WriteAllText(Path.Combine(odd, "bomb.xaml"), "<?xml version=\"1.0\"?>\n<!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\">"
                + string.Concat(Enumerable.Range(1, 9).Select(i => $"<!ENTITY {(char)('a' + i)} \"" + string.Concat(Enumerable.Repeat($"&{(char)('a' + i - 1)};", 10)) + "\">"))
                + "]>\n<Window x:Class=\"Bomb\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">&j;</Window>\n");
            sw.Restart();
            var o = Build(odd);
            Check($"odd: an XML entity bomb in a .xaml file does not hang the read ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 15_000 && o.ById.ContainsKey("bomb.xaml"));
            Check("odd: a binary file named .js is skipped (no node)", !o.ById.ContainsKey("a-binary.js"));
            Check("odd: a file over the size cap is skipped", !o.ById.ContainsKey("huge.ts"));
            Check("odd: a minified bundle is skipped", !o.ById.ContainsKey("app.min.js"));
            Check("odd: …and they are counted as skipped", o.Skipped >= 3, o.Skipped.ToString());
            Check("odd: a CRLF file's import is on its real line", o.Edges.Any(e => e.From == "src/crlf.ts" && e.To == "src/b.ts" && e.First?.Line == 3),
                string.Join("; ", o.Edges));
            Check("odd: a UTF-16 LE file (with BOM) is read, not taken for binary", o.Edges.Any(e => e.From == "src/wide.ts" && e.To == "src/b.ts" && e.First?.Line == 3),
                string.Join("; ", o.Edges));
            Check("odd: a UTF-16 BE file too", o.Edges.Any(e => e.From == "src/wide-be.ts" && e.To == "src/b.ts"));
            Check("odd: UTF-8 with a BOM and Thai text", o.Edges.Any(e => e.From == "src/thai.ts" && e.To == "src/b.ts" && e.First?.Line == 2));

            // ── cancellation: the chat moves on while a big read runs ──
            ProjectScanCacheClear();
            using (var cts = new CancellationTokenSource(40))
            {
                sw.Restart();
                bool cancelled = false;
                try { Build(big, ct: cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check($"cancel: a big read stops when told to ({sw.ElapsedMilliseconds} ms after start, 40 ms cancel)", cancelled && sw.ElapsedMilliseconds < 5000,
                    $"cancelled={cancelled} {sw.ElapsedMilliseconds} ms");
            }

            // ── a folder deleted between two builds ──
            string copy = Path.Combine(temp, "ts");
            CopyDir(Path.Combine(Fixtures, "ts"), copy);
            var before = Build(copy);
            Check("deleted folder: (control) the component is there", before.ById.ContainsKey("src/components/Button.tsx"));
            Directory.Delete(Path.Combine(copy, "src", "components"), recursive: true);
            var after = Build(copy);
            Check("deleted folder: its files and every edge to them are gone at the next build",
                !after.Nodes.Any(n => n.Id.StartsWith("src/components/")) && !after.Edges.Any(e => e.To.StartsWith("src/components/")),
                string.Join("; ", after.Edges.Where(e => e.To.StartsWith("src/components/"))));
            Check("deleted folder: the rest is unchanged", after.Edges.Count == before.Edges.Count - 1, $"{before.Edges.Count} → {after.Edges.Count}");

            // ── an edited file is read again (the cache keys on time and size) ──
            string app = Path.Combine(copy, "src", "App.tsx");
            File.WriteAllText(app, File.ReadAllText(app).Replace("import Store from 'utils/store'", "// import Store from 'utils/store'"));
            File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddSeconds(5));
            var edited = Build(copy);
            Check("edited file: commenting an import out removes its edge at the next build", !edited.Edges.Any(e => e.From == "src/App.tsx" && e.To == "utils/store.ts"),
                string.Join("; ", edited.Edges.Where(e => e.From == "src/App.tsx")));

            // ── C#: what a file binds to is kept between builds, and dropped exactly when it can change ──
            string cs = Path.Combine(temp, "wpf");
            CopyDir(Path.Combine(Fixtures, "wpf"), cs);
            int csFiles = Directory.GetFiles(cs, "*.cs", SearchOption.AllDirectories).Length;
            var c0 = Build(cs);
            var c1 = Build(cs);
            Check("c#: a rebuild with nothing changed binds no file again", c1.Timings.Contains($"0 of {csFiles} .cs bound"), c1.Timings);
            void Edit(string rel, string from, string to)
            {
                string p = Path.Combine(cs, rel);
                File.WriteAllText(p, File.ReadAllText(p).Replace(from, to));
                File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(10 + Pass));
            }
            Edit("ViewModels/MainViewModel.cs", "Title = _clock.Now().ToString();", "Title = _clock.Now().ToString(\"t\");");
            var c2 = Build(cs);
            Check("c#: an edit inside a method body binds that one file again", c2.Timings.Contains($"1 of {csFiles} .cs bound"), c2.Timings);
            Check("c#: …and the edges are the same", c2.Edges.Select(e => e.ToString()).OrderBy(x => x).SequenceEqual(c0.Edges.Select(e => e.ToString()).OrderBy(x => x)));
            Edit("Ticking/Ticker.cs", "public object Make() => new SystemClock();", "public object Make() => new SystemClock();\n    public object Vm() { return new Demo.ViewModels.ClockViewModel(); }");
            var c3 = Build(cs);
            Check("c#: a new member changes the project's shape: every file is bound again", c3.Timings.Contains($"{csFiles} of {csFiles} .cs bound"), c3.Timings);
            Check("c#: …and the new `new` is an edge", c3.Edges.Any(e => e.From == "Ticking/Ticker.cs" && e.To == "ViewModels/ClockViewModel.cs" && !e.Inferred && e.First?.Line == 6),
                string.Join("; ", c3.Edges.Where(e => e.From == "Ticking/Ticker.cs")));
            Edit("Ticking/Ticker.cs", "namespace Demo.Ticking;", "using Demo.Services;\nnamespace Demo.Ticking;");
            var c4 = Build(cs);
            Check("c#: adding the missing using turns the inferred reference into a confirmed one",
                c4.Edges.Any(e => e.From == "Ticking/Ticker.cs" && e.To == "Services/SystemClock.cs" && !e.Inferred && e.First?.Line == 6),
                string.Join("; ", c4.Edges.Where(e => e.From == "Ticking/Ticker.cs")));
            Edit("Services/SystemClock.cs", "class SystemClock", "class WallClock");
            var c5 = Build(cs);
            Check("c#: a renamed class (shape change) leaves no stale edge to it", !c5.Edges.Any(e => e.From == "Ticking/Ticker.cs" && e.To == "Services/SystemClock.cs" && !e.Inferred),
                string.Join("; ", c5.Edges.Where(e => e.From == "Ticking/Ticker.cs")));

            // ── a project root that is gone ──
            var gone = Build(Path.Combine(temp, "does-not-exist"));
            Check("missing root: an empty graph, no exception", gone.Nodes.Count == 0 && gone.Edges.Count == 0);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>What compiles but renders blank in the Node Graph page: every {services:Loc} key it shows (XAML and
    /// the view model, every node and edge kind) has a Thai value, every {StaticResource} exists, every {DynamicResource}
    /// is a theme token, every {Binding} names a real property of the view model or of the item it binds.</summary>
    static void StaticXaml()
    {
        Section("static (NodeGraphView.xaml)");
        string repo = Path.GetFullPath(Path.Combine(Fixtures, "..", "..", ".."));
        string P(params string[] parts) => Path.Combine(new[] { repo, "CluadeX" }.Concat(parts).ToArray());
        string xaml = File.ReadAllText(P("Views", "NodeGraphView.xaml"));
        string code = File.ReadAllText(P("ViewModels", "NodeGraphViewModel.cs")) + File.ReadAllText(P("Views", "NodeGraphView.xaml.cs"));
        string resources = File.ReadAllText(P("Resources", "Theme.xaml")) + File.ReadAllText(P("App.xaml")) + File.ReadAllText(P("Views", "UiStudio", "StudioStyles.xaml")) + xaml;
        string theme = File.ReadAllText(P("Resources", "Themes", "Midnight.xaml")) + resources;

        var loc = new CluadeX.Services.LocalizationService();
        loc.SetLanguage("th");
        var keys = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value)
            .Concat(System.Text.RegularExpressions.Regex.Matches(code, @"(?:\bT|\bTr|_vm\.Tr)\(""([\w.]+)""").Select(m => m.Groups[1].Value))
            .Concat(System.Text.RegularExpressions.Regex.Matches(code, @"Add\(""([\w.]+)""").Select(m => m.Groups[1].Value))
            .Concat(new[] { "graph.truncatedSize" })
            .Concat(Enum.GetNames<GraphEdgeKind>().Select(k => "graph.edge." + k.ToLowerInvariant()))
            .Concat(Enum.GetNames<GraphNodeKind>().Select(k => "graph.kind." + k.ToLowerInvariant()))
            .Where(k => !k.EndsWith('.')).Distinct().ToList();
        var missing = keys.Where(k => loc.T(k) == k || !System.Text.RegularExpressions.Regex.IsMatch(loc.T(k), "[฀-๿]")
                                      && k is not ("graph.title" or "graph.kind.element" or "studio.title")).ToList();
        Check($"static: all {keys.Count} localized keys the page uses have a Thai value", missing.Count == 0, string.Join(", ", missing));
        loc.SetLanguage("en");
        var noEnglish = keys.Where(k => loc.T(k) == k).ToList();
        Check("static: …and an English one", noEnglish.Count == 0, string.Join(", ", noEnglish));
        Check("static: the counts in the summary format", loc.T("graph.truncated", 2500).Contains("2500") && loc.T("graph.places", 3).Contains('3') && loc.T("graph.skipped", 4).Contains('4'));

        var missingRes = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("static: every {StaticResource} exists", missingRes.Count == 0, string.Join(", ", missingRes));
        var missingDyn = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !theme.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("static: every {DynamicResource} is a theme token", missingDyn.Count == 0, string.Join(", ", missingDyn));
        var types = new[] { typeof(CluadeX.ViewModels.NodeGraphViewModel), typeof(CluadeX.ViewModels.GraphLink), typeof(CluadeX.ViewModels.GraphEvidenceItem),
            typeof(CluadeX.ViewModels.EdgeKindFilter) };
        var badBindings = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value)
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p)
            .Select(p => p.Split('.')[0]).Distinct()
            .Where(p => !types.Any(t => t.GetProperty(p) != null)).ToList();
        Check("static: every {Binding} names a real property", badBindings.Count == 0, string.Join(", ", badBindings));
    }

    static void ProjectScanCacheClear()
    {
        var t = typeof(ProjectGraphBuilder).Assembly.GetType("CluadeX.Services.UiStudio.Graph.ProjectScan");
        t?.GetMethod("ClearCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
        var cs = typeof(ProjectGraphBuilder).Assembly.GetType("CluadeX.Services.UiStudio.Graph.CSharpReader");
        cs?.GetMethod("ClearCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
    }

    static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to));
    }

    static int Dump(string[] dirs)
    {
        foreach (var dir in dirs.Length > 0 ? dirs : Directory.GetDirectories(Fixtures).Select(Path.GetFileName).ToArray())
        {
            string root = Path.IsPathRooted(dir) ? dir : Path.Combine(Fixtures, dir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Build(root);
            long cold = sw.ElapsedMilliseconds;
            string coldT = g.Timings;
            sw.Restart();
            g = Build(root);
            Console.WriteLine($"   cold {cold} ms [{coldT}] · warm {sw.ElapsedMilliseconds} ms [{g.Timings}]");
            Console.WriteLine($"## {dir}  ({g.Nodes.Count} nodes, {g.Edges.Count} edges, truncated={g.Truncated} {g.TruncatedWhy}, skipped={g.Skipped})");
            if (g.Edges.Count < 400)
                foreach (var e in g.Edges.OrderBy(e => e.From).ThenBy(e => e.To))
                    Console.WriteLine($"  {e}{(e.Label != null ? "  [" + e.Label + "]" : "")}{(e.Why != null ? "  why: " + e.Why : "")}  ev={string.Join(",", e.Evidence.Select(x => x.File + ":" + x.Line))}");
            else
                foreach (var k in g.Edges.GroupBy(e => e.Kind)) Console.WriteLine($"  {k.Key}: {k.Count()} ({k.Count(e => e.Inferred)} inferred)");
        }
        return 0;
    }
}
