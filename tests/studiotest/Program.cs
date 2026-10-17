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
internal static class Program
{
    static int Pass, Fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {name}"); return; }
        Fail++;
        Console.WriteLine($"  FAIL {name}");
        if (detail.Length > 0) Console.WriteLine("       " + detail.Replace("\n", "\n       "));
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
        string temp = Path.Combine(Path.GetTempPath(), "cx-studiotest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            Editor();
            Locator(temp);
            Server(temp).GetAwaiter().GetResult();
            Templates(temp);
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
