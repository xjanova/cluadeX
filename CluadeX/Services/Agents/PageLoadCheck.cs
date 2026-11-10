using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>
/// "Does the page you wrote actually run?" — the step after <see cref="WrittenFileCheck"/>, which only parses. The Sudoku
/// run of 2026-11-06 wrote a page whose script parsed and then froze the tab on load (the solver got a flat index as a row:
/// runaway backtracking). This opens each page the run wrote in a headless Chrome (Edge when there is no Chrome) with a
/// fresh throwaway profile, lets its timers run for <see cref="VirtualTimeMs"/> of virtual time, and reports:
/// <list type="bullet">
/// <item>an uncaught exception or promise rejection thrown by the page's own files, at its line;</item>
/// <item>a page that never finishes loading in <see cref="LoadTimeout"/> (an endless loop or recursion).</item>
/// </list>
/// What file:// itself causes is not the page's fault and is never reported: a fetch of a local file blocked by CORS, a
/// network error, an error thrown inside a CDN script. No browser, or a browser that gives nothing back, says nothing.
/// </summary>
public static class PageLoadCheck
{
    public const int MaxPages = 3;
    public const int VirtualTimeMs = 3000;
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    public const string HangMessage = "page load: it did not finish loading in 15 s — a script runs forever (an endless loop or "
        + "recursion) or waits on something that never comes";

    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };
    private static readonly HashSet<string> AssetExt = new(StringComparer.OrdinalIgnoreCase) { ".js", ".mjs", ".cjs" };

    // [pid:tid:date/time.ms:INFO:CONSOLE:2] "Uncaught ReferenceError: x is not defined", source: file:///D:/p/index.html (2)
    // (older builds: INFO:CONSOLE(2)). The message itself may hold quotes, so the source is anchored at the line's end.
    private static readonly Regex ConsoleLine = new(
        @":CONSOLE[^\]]*\]\s*""(?<msg>.*)"",\s*source:\s*(?<src>.*?)\s*\((?<line>\d+)\)\s*$", RegexOptions.Multiline);

    /// <summary>Failures that come from opening the page as a file, not from the page's code.</summary>
    private static readonly string[] NotThePage =
        ["Failed to fetch", "CORS policy", "net::ERR_", "NetworkError", "Load failed", "Cross origin requests", "origin 'null'"];

    /// <summary>
    /// The pages to open for what a run wrote: every page it wrote, plus a page next to (or one folder above) a script it
    /// wrote that loads that script. At most <see cref="MaxPages"/>.
    /// </summary>
    public static IReadOnlyList<string> PagesFor(IEnumerable<string> writtenFullPaths)
    {
        var written = writtenFullPaths.Where(p => !string.IsNullOrWhiteSpace(p) && SafeExists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pages = new List<string>();
        void Add(string page)
        {
            if (pages.Count < MaxPages && !pages.Contains(page, StringComparer.OrdinalIgnoreCase)) pages.Add(page);
        }
        foreach (var p in written.Where(p => HtmlExt.Contains(Path.GetExtension(p)))) Add(p);
        foreach (var script in written.Where(p => AssetExt.Contains(Path.GetExtension(p))))
        {
            string name = Path.GetFileName(script);
            string? dir = Path.GetDirectoryName(script);
            foreach (var folder in new[] { dir, dir == null ? null : Path.GetDirectoryName(dir) })
            {
                if (folder == null || pages.Count >= MaxPages) continue;
                IEnumerable<string> htmls;
                try { htmls = Directory.EnumerateFiles(folder, "*.htm*").Where(h => HtmlExt.Contains(Path.GetExtension(h))).Take(10).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var html in htmls)
                {
                    try
                    {
                        if (new FileInfo(html).Length <= WrittenFileCheck.MaxFileBytes
                            && File.ReadAllText(html).Contains(name, StringComparison.OrdinalIgnoreCase)) Add(html);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return pages;
    }

    /// <summary>
    /// Open each page (at most <see cref="MaxPages"/>). <paramref name="findBrowser"/> answers a chrome.exe / msedge.exe
    /// path or null; <paramref name="run"/> null = the real browser, stopped after <see cref="LoadTimeout"/>. Empty = every
    /// page that could be opened ran without an uncaught error.
    /// </summary>
    public static async Task<IReadOnlyList<FileCheckIssue>> CheckAsync(IEnumerable<string> pages, Func<string?> findBrowser,
        WrittenFileCheck.Runner? run, CancellationToken ct)
    {
        var list = pages.Where(SafeExists).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPages).ToList();
        if (list.Count == 0) return [];
        string? browser = findBrowser();
        if (browser == null) return [];
        run ??= (exe, args, c) => WrittenFileCheck.RunProcessAsync(exe, args, c, LoadTimeout, stderrFirst: true);

        var issues = new List<FileCheckIssue>();
        foreach (var page in list)
        {
            ct.ThrowIfCancellationRequested();
            string profile = Path.Combine(Path.GetTempPath(), "cluadex-pagecheck-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var (_, output, timedOut) = await run(browser, Arguments(page, profile), ct).ConfigureAwait(false);
                if (timedOut) { issues.Add(new FileCheckIssue(page, 0, HangMessage)); continue; }
                if (!GaveAPage(output)) continue;   // the browser did not load it (a broken install, a policy): say nothing
                issues.AddRange(ParseConsole(page, output));
            }
            finally { await DeleteProfileAsync(profile).ConfigureAwait(false); }
            if (issues.Count >= WrittenFileCheck.MaxIssues) break;
        }
        return issues.Take(WrittenFileCheck.MaxIssues).ToList();
    }

    /// <summary>The browser's command line: headless, its own empty profile, console messages on stderr, the DOM on stdout
    /// once virtual time has run out.</summary>
    internal static IReadOnlyList<string> Arguments(string page, string profile) =>
    [
        "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
        "--disable-sync", "--disable-background-networking", "--disable-component-update", "--mute-audio",
        "--user-data-dir=" + profile, "--enable-logging=stderr", "--v=0",
        "--virtual-time-budget=" + VirtualTimeMs, "--dump-dom", new Uri(page).AbsoluteUri,
    ];

    /// <summary>--dump-dom printed a document: the page was really loaded.</summary>
    internal static bool GaveAPage(string output) =>
        output.Contains("<html", StringComparison.OrdinalIgnoreCase) || output.Contains("<body", StringComparison.OrdinalIgnoreCase);

    /// <summary>The page's own uncaught errors in the browser's log, once each.</summary>
    internal static IReadOnlyList<FileCheckIssue> ParseConsole(string page, string output)
    {
        var issues = new List<FileCheckIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ConsoleLine.Matches(output.Replace("\r", "")))
        {
            string msg = m.Groups["msg"].Value.Trim();
            if (!msg.StartsWith("Uncaught", StringComparison.Ordinal)) continue;   // console.log / console.error are the page's business
            if (NotThePage.Any(n => msg.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
            string src = m.Groups["src"].Value.Trim();
            string? file = SourceFile(src, page);
            if (file == null) continue;   // thrown inside a CDN script: not the page's own file
            int.TryParse(m.Groups["line"].Value, out int line);
            if (msg.Length > 300) msg = msg[..300] + "…";
            if (seen.Add($"{file}|{line}|{msg}")) issues.Add(new FileCheckIssue(file, line, "page load: " + msg));
        }
        return issues;
    }

    /// <summary>A console source as a local file: file:// → its path, empty → the page itself, anything else → null.</summary>
    private static string? SourceFile(string src, string page)
    {
        if (src.Length == 0) return page;
        if (!src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(new Uri(src).LocalPath); }
        catch { return page; }
    }

    /// <summary>The note the model reads when a page it wrote fails to load. Short, like <see cref="WrittenFileCheck.Note"/>.</summary>
    public static string Note(IReadOnlyList<FileCheckIssue> issues, string? root)
    {
        var sb = new StringBuilder("[page-check] ✗ CluadeX opened the page you wrote in a headless browser and it fails — fix it before you finish:\n");
        foreach (var i in issues)
            sb.Append("- ").Append(WrittenFileCheck.Shown(i.Path, root)).Append(i.Line > 0 ? $":{i.Line}" : "").Append(": ").Append(i.Message).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static async Task DeleteProfileAsync(string profile)
    {
        // The browser can hold its profile for a moment after it exits.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(profile)) return;
                Directory.Delete(profile, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }
}
