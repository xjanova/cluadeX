using System.IO;
using System.Net.Http;
using System.Text;
using CluadeX.Services.UiStudio;

namespace StudioTest;

/// <summary>
/// UI Studio / Node Graph core, driven through the REAL built CluadeX.dll: the source editor (HTML, JSX, Vue,
/// Svelte), the locator (instrumenting and resolving, path safety), the local static server, every starter
/// template (created, detected, served) and the project graph. No network, no Node — temp folders only.
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {name}"); return; }
        Fail++;
        Console.WriteLine($"  FAIL {name}");
        if (detail.Length > 0) Console.WriteLine("       " + detail.Replace("\n", "\n       "));
    }

    /// <summary>Render a wireframe with the real SketchView (off-screen) to a PNG; returns the pixels for checks.</summary>
    static System.Windows.Media.Imaging.BitmapSource SaveSketch(CluadeX.Services.UiStudio.Sketch.ScreenSketch sk, string? file, double scale = 1, bool fullHeight = false)
    {
        var view = new CluadeX.Views.UiStudio.SketchView { Sketch = sk, FullHeight = fullHeight };
        // a phone screen is 360 × 760; a desktop window (WPF) is the size its XAML gives
        double w = (sk.Desktop && sk.Width > 0 ? sk.Width : CluadeX.Views.UiStudio.SketchView.ScreenWidth) * scale;
        double h = (sk.Desktop && sk.Height > 0 ? sk.Height : CluadeX.Views.UiStudio.SketchView.ScreenHeight) * scale;
        view.Measure(new System.Windows.Size(w, fullHeight ? double.PositiveInfinity : h));
        var size = view.DesiredSize;
        view.Arrange(new System.Windows.Rect(size));
        view.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(view);
        bmp.Freeze();
        if (file != null)
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        return bmp;
    }

    static SourceTag Tag(string text, string marker, SourceDialect d)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new Exception("marker not found: " + marker);
        return SourceTagEditor.ParseOpenTag(text, at, d) ?? throw new Exception("no tag at " + marker);
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // studiotest create <templateId> <folder> — make a project exactly as the Studio's gallery does (for a real
        // npm install + vite dev check of the templates and the locator plugin).
        if (args.Length == 3 && args[0] == "create")
        {
            string? e = StudioTemplates.CreateProject(args[1], args[2], "CX Test");
            Console.WriteLine(e ?? "created " + args[2]);
            return e == null ? 0 : 1;
        }
        // studiotest serve <folder> — the Studio's local server on its own (to drive the overlay in a browser).
        if (args.Length == 2 && args[0] == "serve")
        {
            using var s = new StudioStaticServer(args[1]);
            s.Start();
            Console.WriteLine(s.BaseUrl);
            Thread.Sleep(Timeout.Infinite);
        }
        // studiotest pages <folder> — list a project's pages as the page gallery finds them (read-only).
        if (args.Length == 2 && args[0] == "pages")
        {
            var info = StudioProject.Detect(args[1]);
            var found = CluadeX.Services.UiStudio.Pages.PageCatalog.Discover(info, "th");
            Console.WriteLine($"{info.Kind} {info.Framework} via {found.Source} · {found.Pages.Count} pages · base {found.SuggestedBaseUrl}");
            foreach (var pg in found.Pages)
                Console.WriteLine($"  {pg.Kind,-8} {pg.Route ?? "-",-34} {pg.Title,-30} {(pg.NeedsParams ? "[params] " : "")}{(pg.Risky ? "[risky] " : "")}{(pg.NeedsLogin ? "[login] " : "")}{(pg.InShell ? "[shell] " : "")}{pg.Detail} {(pg.SourcePath != null ? Path.GetRelativePath(args[1], pg.SourcePath) + ":" + pg.SourceLine : "")}");
            return 0;
        }
        // studiotest render <flutter folder> <out folder> [route…] — draw real screens with `flutter test` (runs the app's
        // code in Flutter's test renderer: no device, no network). Dry-run is switched off for this explicit command only.
        if (args.Length >= 3 && args[0] == "render")
        {
            CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
            var found = CluadeX.Services.UiStudio.Pages.PageCatalog.Discover(StudioProject.Detect(args[1]), "th");
            var wanted = args.Skip(3).ToHashSet();
            var pages = found.Pages.Where(p => wanted.Count == 0 || wanted.Contains(p.Route ?? "") || wanted.Contains(p.Detail ?? "")).ToList();
            var run = CluadeX.Services.UiStudio.Sketch.FlutterRenderer.RunAsync(found.Flutter!, pages, args[2], "th", CancellationToken.None,
                line => { if (line != "·") Console.WriteLine("  | " + line); else Console.Write("·"); }).GetAwaiter().GetResult();
            Console.WriteLine($"\nok={run.Ok} error={run.Error} took={run.Took.TotalSeconds:0.0}s");
            if (run.CompileError != null) Console.WriteLine("COMPILE:\n" + run.CompileError);
            foreach (var (s, pg) in run.Screens.Zip(pages))
                Console.WriteLine($"  {pg.Route ?? pg.Detail,-28} {(s.PngPath != null ? "png" : "---")} boxes={s.Boxes.Count,4} noise={s.Noise,3} {s.Skipped}{(s.Problems.Count > 0 ? " PROBLEMS: " + string.Join(" | ", s.Problems.Take(3)) : "")}");
            return run.Ok ? 0 : 1;
        }
        // studiotest texts <flutter folder> — every Text that shows a parameter of its widget or of a helper function, and what each
        // use / call passes (read-only).
        if (args.Length == 2 && args[0] == "texts") return ParamReport(args[1]);
        // studiotest picks <flutter folder> <out folder> [route…] — draw the screens for real (as `render` does), then for every
        // Text drawn from a helper function's parameter, the call the text tool would type over, told from the real boxes.
        if (args.Length >= 3 && args[0] == "picks") return HelperPickReport(args[1], args[2], args.Skip(3).ToHashSet());
        // studiotest tree <app folder> <route or class> — print one screen's wireframe blocks (to see what the sketch read).
        if (args.Length == 3 && args[0] == "tree")
        {
            var found = CluadeX.Services.UiStudio.Pages.PageCatalog.Discover(StudioProject.Detect(args[1]), "th");
            var pg = found.Pages.FirstOrDefault(p => p.Route == args[2] || p.Detail == args[2]);
            var sk = pg == null ? null : found.Screens?.Sketch(pg);
            if (sk == null) { Console.WriteLine("no such screen"); return 1; }
            void Dump(CluadeX.Services.UiStudio.Sketch.SketchNode n, int d)
            {
                string icon = n.Icon == null ? "" : " icon=" + (n.Icon.Length <= 2 && n.Icon.All(ch => ch >= '' && ch <= '') ? string.Concat(n.Icon.Select(ch => $"U+{(int)ch:X4}")) : n.Icon);
                string size = (n.Width > 0 ? $" w={n.Width:0}" : "") + (n.Flex > 0 ? $" flex={n.Flex:0.#}" : "") + (n.Variant != null ? " " + n.Variant : "");
                Console.WriteLine($"{new string(' ', d * 2)}{n.Kind} <{n.Widget}> {(n.Text != null ? "\"" + n.Text + "\"" : "")}{(n.Text2 != null ? " / \"" + n.Text2 + "\"" : "")}{icon}{size}{(n.Items.Count > 0 ? " [" + string.Join("|", n.Items) + "]" : "")} {Path.GetFileName(n.File)}:{n.Line}{(n.UseFile != null ? " use " + Path.GetFileName(n.UseFile) + ":" + n.UseLine : "")}");
                foreach (var c in n.Children) Dump(c, d + 1);
            }
            Console.WriteLine($"title \"{sk.Title}\" · {(sk.Desktop ? $"desktop {sk.Width:0}×{sk.Height:0}" : "phone")} · caption \"{sk.Caption}\" · bg {sk.Theme.Background:X8} accent {sk.Theme.Accent:X8} dark={sk.Theme.Dark} · unknown {sk.Unknown}");
            Dump(sk.Root, 0);
            return 0;
        }
        // studiotest sketch <app folder> <out folder> — draw every screen's wireframe to a PNG (read-only on the project).
        if (args.Length == 3 && args[0] == "sketch")
        {
            var info = StudioProject.Detect(args[1]);
            var found = CluadeX.Services.UiStudio.Pages.PageCatalog.Discover(info, "th");
            Directory.CreateDirectory(args[2]);
            int i = 0;
            var thumbs = new List<System.Windows.Media.Imaging.BitmapSource>();
            foreach (var pg in found.Pages)
            {
                var sk = found.Screens?.Sketch(pg);
                if (sk == null) { Console.WriteLine($"  no sketch: {pg.Id}"); continue; }
                string file = Path.Combine(args[2], $"{i++:00}-{string.Concat((pg.Route ?? pg.Detail ?? "x").Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.png");
                SaveSketch(sk, file, 2);
                thumbs.Add(SaveSketch(sk, null, 0.5));
                Console.WriteLine($"  {pg.Route ?? pg.Detail,-28} {sk.Title,-24} nodes={sk.Root.Count(),4} unknown={sk.Unknown} → {Path.GetFileName(file)}");
            }
            // one contact sheet of every screen, the way the page gallery shows them
            if (thumbs.Count > 0)
            {
                int cols = thumbs[0].PixelWidth > thumbs[0].PixelHeight ? 4 : 8, tw = thumbs.Max(t => t.PixelWidth), th = thumbs.Max(t => t.PixelHeight), rows = (thumbs.Count + cols - 1) / cols;
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.DimGray, null, new System.Windows.Rect(0, 0, cols * (tw + 8), rows * (th + 8)));
                    for (int k = 0; k < thumbs.Count; k++)
                        dc.DrawImage(thumbs[k], new System.Windows.Rect(k % cols * (tw + 8) + 4, k / cols * (th + 8) + 4, thumbs[k].PixelWidth, thumbs[k].PixelHeight));
                }
                var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap(cols * (tw + 8), rows * (th + 8), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                sheet.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sheet));
                using var fs = File.Create(Path.Combine(args[2], "sheet.png"));
                enc.Save(fs);
            }
            return 0;
        }
        string temp = Path.Combine(Path.GetTempPath(), "cx-studiotest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            Editor();
            Locator(temp);
            Server(temp).GetAwaiter().GetResult();
            Templates(temp);
            PageDiscovery(temp);
            LaravelSite(temp);
            ShotStore(temp);
            DartReading();
            FlutterScreens(temp);
            FlutterEdits(temp);
            ArbEdits(temp);
            WpfViews(temp);
        }
        catch (Exception ex) { Fail++; Console.WriteLine("FATAL " + ex); }
        finally { try { Directory.Delete(temp, true); } catch { } }
        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        return Fail == 0 ? 0 : 1;
    }

    // ───────────────────────────── source editor ─────────────────────────────

    static void Editor()
    {
        Console.WriteLine("SourceTagEditor");
        var H = SourceDialect.Html;
        var J = SourceDialect.Jsx;

        string html = "<div class=\"a\">\n  <p id=\"x\" title='t > 1'>Hello</p>\n  <img src=\"a.png\">\n</div>\n";
        var p = Tag(html, "<p", H);
        Check("parse: quoted '>' inside an attribute does not end the tag", p.End == html.IndexOf(">Hello", StringComparison.Ordinal) + 1);
        Check("parse: attributes read", p.Attrs.Count == 2 && p.Find("title")?.Value(html) == "t > 1");

        var r = SourceTagEditor.SetAttribute(html, p, H, "class", "lead big");
        Check("html: add class", r.Ok && r.Text!.Contains("<p id=\"x\" title='t > 1' class=\"lead big\">Hello</p>"), r.Text ?? r.Error ?? "");
        r = SourceTagEditor.SetAttribute(html, Tag(html, "<div", H), H, "class", "b");
        Check("html: replace class", r.Ok && r.Text!.StartsWith("<div class=\"b\">"), r.Text ?? "");
        r = SourceTagEditor.SetAttribute(html, p, H, "title", null);
        Check("html: remove attribute", r.Ok && r.Text!.Contains("<p id=\"x\">Hello</p>"), r.Text ?? "");
        r = SourceTagEditor.SetAttribute(html, p, H, "title", "say \"hi\" & <go>");
        Check("html: attribute value escaped", r.Ok && r.Text!.Contains("title=\"say &quot;hi&quot; &amp; &lt;go&gt;\""), r.Text ?? "");

        r = SourceTagEditor.SetStyle(html, p, H, new Dictionary<string, string?> { ["color"] = "red", ["font-size"] = "18px" });
        Check("html: style added", r.Ok && r.Text!.Contains("style=\"color: red; font-size: 18px\""), r.Text ?? "");
        string styled = "<p style=\"color: blue; background: url(data:image/png;base64,AA==)\">x</p>";
        r = SourceTagEditor.SetStyle(styled, Tag(styled, "<p", H), H, new Dictionary<string, string?> { ["color"] = "red", ["margin"] = "0" });
        Check("html: style merged, url(;) kept whole", r.Ok && r.Text == "<p style=\"color: red; background: url(data:image/png;base64,AA==); margin: 0\">x</p>", r.Text ?? "");
        r = SourceTagEditor.SetStyle(styled, Tag(styled, "<p", H), H, new Dictionary<string, string?> { ["color"] = null, ["background"] = null });
        Check("html: style emptied → attribute removed", r.Ok && r.Text == "<p>x</p>", r.Text ?? "");

        r = SourceTagEditor.SetText(html, p, H, "Hi <you> & me");
        Check("html: text replaced and escaped", r.Ok && r.Text!.Contains(">Hi &lt;you&gt; &amp; me</p>"), r.Text ?? "");
        r = SourceTagEditor.SetText(html, Tag(html, "<div", H), H, "x");
        Check("html: text refused when the element has children", !r.Ok && r.Error == "hasChildren");
        Check("html: void element ends at its tag", SourceTagEditor.FindElementEnd(html, Tag(html, "<img", H), H) == html.IndexOf("\n</div>", StringComparison.Ordinal));

        string nested = "<div a>\n  <div b>\n    <div c></div>\n  </div>\n  <span>s</span>\n</div>";
        var outer = Tag(nested, "<div a", H);
        Check("nested same-name elements counted", SourceTagEditor.FindElementEnd(nested, outer, H) == nested.Length);
        r = SourceTagEditor.Delete(nested, Tag(nested, "<div b", H), H);
        Check("delete takes its whole lines", r.Ok && r.Text == "<div a>\n  <span>s</span>\n</div>", r.Text ?? "");
        r = SourceTagEditor.Duplicate(nested, Tag(nested, "<span", H), H);
        Check("duplicate on its own line", r.Ok && r.Text == "<div a>\n  <div b>\n    <div c></div>\n  </div>\n  <span>s</span>\n  <span>s</span>\n</div>", r.Text ?? "");
        int spanAt = nested.IndexOf("<span", StringComparison.Ordinal);
        r = SourceTagEditor.SwapWithNext(nested, Tag(nested, "<div b", H), H, spanAt);
        Check("swap with the next sibling", r.Ok && r.Text == "<div a>\n  <span>s</span>\n  <div b>\n    <div c></div>\n  </div>\n</div>", r.Text ?? "");
        r = SourceTagEditor.SwapWithNext(nested, Tag(nested, "<div b", H), H, spanAt + 1);
        Check("swap refused when the page's neighbour is not the source's", !r.Ok && r.Error == "noSibling");
        r = SourceTagEditor.SwapWithNext(nested, Tag(nested, "<span", H), H);
        Check("swap refused for the last child", !r.Ok);

        // JSX
        string jsx = "export function A({ cls }) {\n  return (\n    <section className=\"hero\" onClick={() => go(a > b)}>\n      <h1 className={cls}>Title</h1>\n      <p>{count} items</p>\n      <Button label=\"x\" />\n    </section>\n  )\n}\n";
        var sec = Tag(jsx, "<section", J);
        Check("jsx: arrow function with '>' inside braces", sec.Find("onClick") != null && jsx[sec.End - 1] == '>' && jsx.IndexOf("<h1", StringComparison.Ordinal) > sec.End);
        r = SourceTagEditor.SetAttribute(jsx, sec, J, "class", "hero dark");
        Check("jsx: class → className", r.Ok && r.Text!.Contains("<section className=\"hero dark\" onClick"), r.Text ?? "");
        r = SourceTagEditor.SetAttribute(jsx, Tag(jsx, "<h1", J), J, "class", "x");
        Check("jsx: className={expr} refused", !r.Ok && r.Error == "expression");
        r = SourceTagEditor.SetStyle(jsx, Tag(jsx, "<h1", J), J, new Dictionary<string, string?> { ["font-size"] = "32px", ["color"] = "#fff" });
        Check("jsx: style object written", r.Ok && r.Text!.Contains("<h1 className={cls} style={{ fontSize: '32px', color: '#fff' }}>"), r.Text ?? "");
        string jsx2 = r.Text!;
        r = SourceTagEditor.SetStyle(jsx2, Tag(jsx2, "<h1", J), J, new Dictionary<string, string?> { ["color"] = null, ["margin-top"] = "4px" });
        Check("jsx: style object merged", r.Ok && r.Text!.Contains("style={{ fontSize: '32px', marginTop: '4px' }}"), r.Text ?? "");
        string jsxVar = "<div style={styles.box}>x</div>";
        r = SourceTagEditor.SetStyle(jsxVar, Tag(jsxVar, "<div", J), J, new Dictionary<string, string?> { ["color"] = "red" });
        Check("jsx: style={variable} refused", !r.Ok && r.Error == "styleExpression");
        r = SourceTagEditor.SetText(jsx, Tag(jsx, "<h1", J), J, "New {title}");
        Check("jsx: text with braces becomes a string expression", r.Ok && r.Text!.Contains("<h1 className={cls}>{\"New {title}\"}</h1>"), r.Text ?? "");
        r = SourceTagEditor.SetText(jsx, Tag(jsx, "<p", J), J, "x");
        Check("jsx: {expression} text refused", !r.Ok && r.Error == "textExpression");
        r = SourceTagEditor.SetAttribute(jsx, Tag(jsx, "<p", J), J, "title", "He said \"hi\"");
        Check("jsx: quote in value → JS string expression", r.Ok && r.Text!.Contains("<p title={\"He said \\\"hi\\\"\"}>"), r.Text ?? "");
        Check("jsx: self-closing component ends at '/>'", SourceTagEditor.FindElementEnd(jsx, Tag(jsx, "<Button", J), J) == jsx.IndexOf("/>", StringComparison.Ordinal) + 2);

        // Vue / Svelte
        string vue = "<template>\n  <div :class=\"{a: x}\" class=\"box\" @click=\"go\">{{ msg }}</div>\n</template>";
        r = SourceTagEditor.SetAttribute(vue, Tag(vue, "<div", SourceDialect.Vue), SourceDialect.Vue, "class", "box big");
        Check("vue: static class edited, :class binding kept", r.Ok && r.Text!.Contains("<div :class=\"{a: x}\" class=\"box big\" @click=\"go\">"), r.Text ?? "");
        string sv = "<button class:active={on} on:click={() => n++}>Count</button>";
        var b = Tag(sv, "<button", SourceDialect.Svelte);
        Check("svelte: directives parsed", b.Attrs.Count == 2 && sv[b.End - 1] == '>');
        r = SourceTagEditor.SetText(sv, b, SourceDialect.Svelte, "Clicks {n}");
        Check("svelte: braces in text escaped", r.Ok && r.Text!.Contains(">Clicks &#123;n&#125;</button>"), r.Text ?? "");

        // locating
        var lt = SourceTagEditor.Locate(html, 2, 3, "p", H, out _);
        Check("locate by line/col", lt != null && lt.Name == "p");
        lt = SourceTagEditor.Locate(html, 2, 1, "p", H, out _);
        Check("locate tolerates a column drift on the same line", lt != null && lt.Name == "p");
        lt = SourceTagEditor.Locate(html, 2, 3, "span", H, out var err);
        Check("locate refuses a different tag (stale source)", lt == null && err == "stale");
        lt = SourceTagEditor.Locate(html, 2, 3, "", H, out _);
        Check("locate any tag at an exact position", lt != null && lt.Name == "p");

        // CRLF files keep their line endings
        string crlf = "<ul>\r\n  <li>a</li>\r\n  <li>b</li>\r\n</ul>\r\n";
        r = SourceTagEditor.Delete(crlf, Tag(crlf, "<li>a", H), H);
        Check("CRLF: delete keeps \\r\\n", r.Ok && r.Text == "<ul>\r\n  <li>b</li>\r\n</ul>\r\n", r.Text?.Replace("\r", "\\r").Replace("\n", "\\n") ?? "");
    }

    // ───────────────────────────── locator ─────────────────────────────

    static void Locator(string temp)
    {
        Console.WriteLine("SourceLocator");
        string root = Path.Combine(temp, "site");
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "lib"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        string page = "<!doctype html>\n<html>\n<head><title>T</title><script>if (a<b) document.write('<p>x</p>')</script></head>\n<body>\n  <!-- <div>not me</div> -->\n  <main class=\"m\">\n    <h1>Hi</h1>\n  </main>\n</body>\n</html>\n";
        File.WriteAllText(Path.Combine(root, "index.html"), page);
        File.WriteAllText(Path.Combine(root, "node_modules", "lib", "x.js"), "");
        File.WriteAllText(Path.Combine(temp, "outside.html"), "");

        string inst = SourceLocator.InstrumentHtml(page, "index.html");
        Check("instrument: body/main/h1 located", inst.Contains("<body data-cx-src=\"index.html:4:1\">") && inst.Contains("<main data-cx-src=\"index.html:6:3\" class=\"m\">") && inst.Contains("<h1 data-cx-src=\"index.html:7:5\">"), inst);
        Check("instrument: script body and comments untouched", inst.Contains("document.write('<p>x</p>')") && inst.Contains("<!-- <div>not me</div> -->"));
        Check("instrument: head elements skipped", !inst.Contains("<title data-cx-src") && !inst.Contains("<html data-cx-src"));
        Check("instrument: removing the attributes gives the file back", System.Text.RegularExpressions.Regex.Replace(inst, " data-cx-src=\"[^\"]*\"", "") == page);

        var ok = SourceLocator.Resolve("index.html:7:5", root);
        Check("resolve: inside the project", ok != null && ok.Line == 7 && ok.Column == 5 && ok.RelativePath == "index.html");
        Check("resolve: absolute path inside the project", SourceLocator.Resolve(Path.Combine(root, "index.html") + ":7:5", root) != null);
        Check("resolve: ../ outside the project refused", SourceLocator.Resolve("../outside.html:1:1", root) == null);
        Check("resolve: absolute path outside refused", SourceLocator.Resolve(Path.Combine(temp, "outside.html") + ":1:1", root) == null);
        Check("resolve: node_modules refused", SourceLocator.Resolve("node_modules/lib/x.js:1:1", root) == null);
        Check("resolve: garbage refused", SourceLocator.Resolve("index.html:x:1", root) == null && SourceLocator.Resolve("index.html", root) == null);
    }

    // ───────────────────────────── static server ─────────────────────────────

    static async Task Server(string temp)
    {
        Console.WriteLine("StudioStaticServer");
        string root = Path.Combine(temp, "site");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(root, "about.html"), "<p>About</p>");
        File.WriteAllText(Path.Combine(root, "app.css"), "p{color:red}");
        using var server = new StudioStaticServer(root);
        server.Start();
        Check("server: listening on localhost", server.IsRunning && server.BaseUrl.StartsWith("http://localhost:", StringComparison.Ordinal));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var index = await http.GetAsync(server.BaseUrl);
        string body = await index.Content.ReadAsStringAsync();
        Check("server: / serves index.html instrumented", index.IsSuccessStatusCode && body.Contains("data-cx-src=\"index.html:"), body.Length > 200 ? body[..200] : body);
        Check("server: no-store", index.Headers.CacheControl?.NoStore == true);
        var css = await http.GetAsync(server.BaseUrl + "app.css");
        Check("server: css with its type", css.Content.Headers.ContentType?.MediaType == "text/css" && (await css.Content.ReadAsStringAsync()) == "p{color:red}");
        Check("server: pretty URL /about → about.html", (await http.GetStringAsync(server.BaseUrl + "about")).Contains("About"));
        Check("server: dot-files never served", (await http.GetAsync(server.BaseUrl + ".env")).StatusCode == System.Net.HttpStatusCode.NotFound);
        Check("server: .git never served", (await http.GetAsync(server.BaseUrl + ".git/config")).StatusCode == System.Net.HttpStatusCode.NotFound);
        Check("server: node_modules never served", (await http.GetAsync(server.BaseUrl + "node_modules/lib/x.js")).StatusCode == System.Net.HttpStatusCode.NotFound);
        Check("server: encoded traversal refused", (await http.GetAsync(server.BaseUrl + "%2e%2e/outside.html")).StatusCode == System.Net.HttpStatusCode.NotFound);
        Check("server: POST refused", (await http.PostAsync(server.BaseUrl, new StringContent("x"))).StatusCode == System.Net.HttpStatusCode.MethodNotAllowed);
        server.Dispose();
        Check("server: stopped", !server.IsRunning);
    }

    // ───────────────────────────── templates ─────────────────────────────

    static void Templates(string temp)
    {
        Console.WriteLine("StudioTemplates");
        var all = StudioTemplates.All();
        Check("templates: 11 embedded", all.Count == 11, string.Join(", ", all.Select(t => t.Id)));
        Check("templates: every one has en+th names and descriptions", all.All(t => t.Name.Length > 0 && t.NameTh.Length > 0 && t.Description.Length > 0 && t.DescriptionTh.Length > 0));
        Check("overlay script embedded", StudioTemplates.ReadAsset("overlay.js").Contains("window.__cx"));
        Check("locator plugin embedded", StudioTemplates.ReadAsset("cluadex-locator.mjs").Contains("apply: 'serve'"));

        foreach (var t in all)
        {
            string dir = Path.Combine(temp, "new", t.Id);
            string? err = StudioTemplates.CreateProject(t.Id, dir, "My <Shop> {App}");
            Check($"create {t.Id}", err == null, err ?? "");
            if (err != null) continue;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToList();
            string allText = string.Join("\n", files.Where(f => !f.EndsWith(".png")).Select(f => File.ReadAllText(Path.Combine(dir, f))));
            Check($"  {t.Id}: placeholders filled, title sanitised", !allText.Contains("{{PROJECT_") && allText.Contains("My Shop App") && !allText.Contains("<Shop>"));
            Check($"  {t.Id}: no template.json copied", !files.Contains("template.json"));
            var info = StudioProject.Detect(dir);
            if (t.Kind == "static")
            {
                Check($"  {t.Id}: detected as static with an entry page", info.Kind == StudioProjectKind.Static && info.EntryHtml == "index.html");
                foreach (var html in files.Where(f => f.EndsWith(".html")))
                {
                    string src = File.ReadAllText(Path.Combine(dir, html));
                    string inst = SourceLocator.InstrumentHtml(src, html);
                    int located = System.Text.RegularExpressions.Regex.Matches(inst, "data-cx-src=").Count;
                    Check($"  {t.Id}/{html}: instrumented ({located} elements) and reversible", located > 5 &&
                          System.Text.RegularExpressions.Regex.Replace(inst, " data-cx-src=\"[^\"]*\"", "") == src);
                }
            }
            else
            {
                Check($"  {t.Id}: detected as Vite with the locator installed", info.Kind == StudioProjectKind.Vite && info.LocatorInstalled && info.DevScript == "dev",
                      $"{info.Kind} locator={info.LocatorInstalled}");
                Check($"  {t.Id}: locator plugin + .gitignore written", files.Contains("cluadex-locator.mjs") && files.Contains(".gitignore"));
                Check($"  {t.Id}: package name is npm-safe", File.ReadAllText(Path.Combine(dir, "package.json")).Contains("\"name\": \"my-shop-app\""));
            }
        }
        Check("create refuses a non-empty folder", StudioTemplates.CreateProject("static-blank", Path.Combine(temp, "new", "static-landing"), "x") == "studio.tpl.notEmpty");

        // graph of a real template
        var g = ProjectGraphBuilder.Build(Path.Combine(temp, "new", "vite-react-ts"), false, CancellationToken.None);
        bool Edge(string from, string to, GraphEdgeKind? kind = null) => g.Edges.Any(e => e.From == from && e.To == to && (kind == null || e.Kind == kind));
        Check("graph: index.html loads src/main.tsx", Edge("index.html", "src/main.tsx", GraphEdgeKind.Loads), string.Join("; ", g.Edges.Select(e => $"{e.From}->{e.To}:{e.Kind}")));
        Check("graph: main.tsx renders App.tsx", Edge("src/main.tsx", "src/App.tsx", GraphEdgeKind.Renders));
        Check("graph: App renders Header/Hero/FeatureCard/TaskList", new[] { "Header", "Hero", "FeatureCard", "TaskList" }.All(c => Edge("src/App.tsx", $"src/components/{c}.tsx", GraphEdgeKind.Renders)));
        Check("graph: main.tsx imports index.css", Edge("src/main.tsx", "src/index.css", GraphEdgeKind.Imports));
        Check("graph: packages hidden by default", !g.Nodes.Any(n => n.Kind == GraphNodeKind.Package));
        var gp = ProjectGraphBuilder.Build(Path.Combine(temp, "new", "vite-react-ts"), true, CancellationToken.None);
        Check("graph: packages on request", gp.Nodes.Any(n => n.Id == "pkg:react") && gp.Nodes.Any(n => n.Id == "pkg:react-dom"));
        var gs = ProjectGraphBuilder.Build(Path.Combine(temp, "new", "static-landing"), false, CancellationToken.None);
        Check("graph: static page loads its css and js", gs.Edges.Any(e => e.From == "index.html" && e.To == "styles.css") && gs.Edges.Any(e => e.From == "index.html" && e.To == "script.js"));
        var ga = ProjectGraphBuilder.Build(Path.Combine(temp, "new", "static-auth"), false, CancellationToken.None);
        Check("graph: auth pages link to each other", ga.Edges.Any(e => e.From == "index.html" && e.To == "register.html" && e.Kind == GraphEdgeKind.Links));

        // locator install plan
        var (patched, perr) = StudioProject.PlanLocatorInstall("import { defineConfig } from 'vite'\nimport react from '@vitejs/plugin-react'\nexport default defineConfig({\n  plugins: [react()],\n})\n");
        Check("locator install: import first, plugin first", perr == null && patched!.StartsWith("import cxLocator from './cluadex-locator.mjs'\n") && patched.Contains("plugins: [cxLocator(), react()]"), patched ?? perr ?? "");
        var (none, merr) = StudioProject.PlanLocatorInstall("export default { build: {} }");
        Check("locator install: no plugins array → manual", none == null && merr == "studio.locator.manual");
    }
}
