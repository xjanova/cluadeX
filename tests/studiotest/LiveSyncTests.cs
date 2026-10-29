using System.IO;
using CluadeX.Services.UiStudio;

namespace StudioTest;

/// <summary>The Studio and the Node Graph following the files as the AI edits them (2026-10-19): which changes count,
/// which preview addresses are opened unasked, and the wire colour of each kind of connection.</summary>
internal static partial class Program
{
    static void LiveSync()
    {
        Console.WriteLine("Live sync");
        string root = Path.Combine(Path.GetTempPath(), "cx-live-root");
        string P(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        Check("watch: a page, a component and a stylesheet count", StudioService.IsSourceChange(root, P("index.html"))
            && StudioService.IsSourceChange(root, P("src/components/Card.tsx")) && StudioService.IsSourceChange(root, P("resources/views/home.blade.php")));
        Check("watch: installed packages and build output do not", !StudioService.IsSourceChange(root, P("node_modules/react/index.js"))
            && !StudioService.IsSourceChange(root, P("vendor/laravel/x.php")) && !StudioService.IsSourceChange(root, P("build/app.js"))
            && !StudioService.IsSourceChange(root, P("bin/Debug/a.dll")) && !StudioService.IsSourceChange(root, P("storage/logs/laravel.php")));
        Check("watch: hidden folders (.git, .dart_tool) and editor temp files do not", !StudioService.IsSourceChange(root, P(".git/index"))
            && !StudioService.IsSourceChange(root, P(".dart_tool/x.dart")) && !StudioService.IsSourceChange(root, P("src/a.tsx.tmp"))
            && !StudioService.IsSourceChange(root, P("src/a.tsx~")) && !StudioService.IsSourceChange(root, P("Makefile")));
        Check("watch: a file outside the project does not", !StudioService.IsSourceChange(root, Path.Combine(Path.GetTempPath(), "other", "a.html"))
            && !StudioService.IsSourceChange("", P("a.html")));

        Check("follow: localhost, loopback and local dev domains open by themselves",
            CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("http://localhost:8000/") && CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("http://127.0.0.1:5173")
            && CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("http://xmanstudio.test") && CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("https://shop.local/"));
        Check("follow: an internet APP_URL (or a non-web scheme) is never opened unasked",
            !CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("https://example.com/") && !CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("https://localhost.example.com/")
            && !CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("file:///C:/x.html") && !CluadeX.ViewModels.UiStudioViewModel.IsLocalUrl("not a url"));

        // A chat that opens an empty folder and has the AI write the site into it (naruay999, 2026-10-29)
        string grow = Path.Combine(Path.GetTempPath(), "cx-live-grow-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string G(string rel) => Path.Combine(grow, rel.Replace('/', Path.DirectorySeparatorChar));
            void Put(string rel, string text = "<!doctype html><title>x</title>")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(G(rel))!);
                File.WriteAllText(G(rel), text);
            }
            Directory.CreateDirectory(grow);
            Check("grow: an empty folder has nothing to show", StudioProject.Detect(grow).Kind == StudioProjectKind.Unsupported);
            Put("assets/hero.png", "png");
            Put("assets/index.html");   // an icon set's demo page, not the site
            Check("grow: images (and a page inside assets/) are not the site yet", StudioProject.Detect(grow).Kind == StudioProjectKind.Unsupported,
                StudioProject.Detect(grow).EntryHtml ?? "");
            Put("node_modules/pkg/index.html");
            Put(".cache/index.html");
            Check("grow: packages and hidden folders are not the site", StudioProject.Detect(grow).Kind == StudioProjectKind.Unsupported,
                StudioProject.Detect(grow).EntryHtml ?? "");
            Put("site/index.html");
            var sub = StudioProject.Detect(grow);
            Check("grow: an index.html one folder down is the site", sub.Kind == StudioProjectKind.Static && sub.EntryHtml == "site/index.html",
                $"{sub.Kind} {sub.EntryHtml}");
            Put("index.html");
            Check("grow: a page at the top wins over one folder down", StudioProject.Detect(grow).EntryHtml == "index.html",
                StudioProject.Detect(grow).EntryHtml ?? "");
        }
        finally { try { Directory.Delete(grow, true); } catch { } }

        var kinds = Enum.GetValues<GraphEdgeKind>();
        var colours = kinds.Select(CluadeX.Views.UiStudio.NodeGraphCanvas.EdgeColor).ToList();
        Check("wires: every kind of connection has its own colour", colours.Distinct().Count() == kinds.Length && !colours.Contains(System.Windows.Media.Colors.Gray),
            string.Join(", ", kinds.Zip(colours, (k, c) => $"{k}={c}")));
    }
}
