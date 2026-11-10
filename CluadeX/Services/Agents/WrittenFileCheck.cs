using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace CluadeX.Services.Agents;

/// <summary>One thing wrong with a file the model wrote: where, and what a parser said.</summary>
public sealed record FileCheckIssue(string Path, int Line, string Message);

/// <summary>
/// The cheap "does what you wrote even parse?" pass over the files an agent run wrote, for the projects a build does
/// not cover (a page with a script, a JSON config, a Python script — the Sudoku run of 2026-11-06 ended "done" on a page
/// whose script could not load). Nothing of the project runs: JSON / XML are parsed here (DTDs refused), JavaScript goes
/// through <c>node --check</c> (parse only), the inline scripts of an HTML page are checked the same way at their own
/// line numbers, the page's local script / stylesheet / image references must exist, and Python goes through
/// <c>ast.parse</c> (no imports). A checker that is not installed is skipped, never reported as a problem.
/// </summary>
public static class WrittenFileCheck
{
    /// <summary>Runs one program with these arguments; returns its exit code, everything it printed, and whether it
    /// was stopped for taking too long. Replaceable so the harness can run without node or Python.</summary>
    public delegate Task<(int Exit, string Output, bool TimedOut)> Runner(string exe, IReadOnlyList<string> args, CancellationToken ct);

    public const int MaxFilesPerCheck = 20;
    /// <summary>Bigger than this is generated or vendored (a bundle, a data dump): not the model's hand-written code.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxIssues = 10;
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> JsonExt = new(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonc", ".webmanifest" };
    private static readonly HashSet<string> XmlExt = new(StringComparer.OrdinalIgnoreCase)
        { ".xml", ".xaml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".config", ".svg", ".resx", ".nuspec" };
    private static readonly HashSet<string> JsExt = new(StringComparer.OrdinalIgnoreCase) { ".js", ".mjs", ".cjs" };
    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };
    private static readonly HashSet<string> PyExt = new(StringComparer.OrdinalIgnoreCase) { ".py", ".pyw" };

    /// <summary>True when this pass has something to say about a file of this kind.</summary>
    public static bool Covers(string path)
    {
        string ext = Path.GetExtension(path);
        return JsonExt.Contains(ext) || XmlExt.Contains(ext) || JsExt.Contains(ext) || HtmlExt.Contains(ext) || PyExt.Contains(ext);
    }

    /// <summary>
    /// Check <paramref name="paths"/> (full paths; missing files and kinds not covered are skipped, at most
    /// <see cref="MaxFilesPerCheck"/>). <paramref name="findTool"/> answers "node" / "python" with a program path (or
    /// null = not installed); <paramref name="run"/> null = <see cref="RunProcessAsync"/>. Returns at most
    /// <see cref="MaxIssues"/> issues; an empty list = everything that could be checked parses.
    /// </summary>
    public static async Task<IReadOnlyList<FileCheckIssue>> CheckAsync(IEnumerable<string> paths, Func<string, string?> findTool,
        Runner? run, CancellationToken ct)
    {
        run ??= RunProcessAsync;
        var issues = new List<FileCheckIssue>();
        var files = paths.Where(p => !string.IsNullOrWhiteSpace(p) && Covers(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => SafeExists(p) && SizeOf(p) <= MaxFileBytes).Take(MaxFilesPerCheck).ToList();
        var python = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(file);
            try
            {
                if (JsonExt.Contains(ext)) issues.AddRange(CheckJson(file));
                else if (XmlExt.Contains(ext)) issues.AddRange(CheckXml(file));
                else if (JsExt.Contains(ext)) issues.AddRange(await CheckJsFileAsync(file, findTool, run, ct).ConfigureAwait(false));
                else if (HtmlExt.Contains(ext)) issues.AddRange(await CheckHtmlAsync(file, findTool, run, ct).ConfigureAwait(false));
                else if (PyExt.Contains(ext)) python.Add(file);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable now: nothing to say */ }
            if (issues.Count >= MaxIssues) break;
        }
        if (python.Count > 0 && issues.Count < MaxIssues)
            issues.AddRange(await CheckPythonAsync(python, findTool, run, ct).ConfigureAwait(false));
        return issues.Take(MaxIssues).ToList();
    }

    /// <summary>The note the model reads after a step whose files do not parse. Short on purpose: an 8k local window
    /// cannot afford a long one.</summary>
    public static string Note(IReadOnlyList<FileCheckIssue> issues, string? root)
    {
        var sb = new StringBuilder("[file-check] ✗ A file you wrote does not parse — fix it before you finish:\n");
        foreach (var i in issues)
            sb.Append("- ").Append(Shown(i.Path, root)).Append(i.Line > 0 ? $":{i.Line}" : "").Append(": ").Append(i.Message).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>A path the way the model knows it: relative to the project when it is inside it.</summary>
    public static string Shown(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            string rel = Path.GetRelativePath(root, path);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel.Replace('\\', '/');
        }
        catch { return path; }
    }

    // ───────────────────────── JSON / XML ─────────────────────────

    internal static IEnumerable<FileCheckIssue> CheckJson(string file)
    {
        string text = File.ReadAllText(file);
        if (text.Trim().Length == 0) return [new FileCheckIssue(file, 1, "the file is empty")];
        // tsconfig / jsconfig / .vscode settings / .jsonc are JSON-with-comments by convention; package.json and the rest are strict.
        string name = Path.GetFileName(file);
        bool lenient = Path.GetExtension(file).Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) || name.StartsWith("jsconfig", StringComparison.OrdinalIgnoreCase)
            || file.Replace('/', '\\').Contains("\\.vscode\\", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                AllowTrailingCommas = lenient,
                MaxDepth = 256,
            });
            return [];
        }
        catch (JsonException ex)
        {
            int line = (int)(ex.LineNumber ?? 0) + 1;
            string msg = ex.Message;
            int cut = msg.IndexOf(" Path:", StringComparison.Ordinal);
            if (cut > 0) msg = msg[..cut];
            return [new FileCheckIssue(file, line, "invalid JSON: " + msg.Trim())];
        }
    }

    internal static IEnumerable<FileCheckIssue> CheckXml(string file)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,   // no entity expansion, no external fetches
            XmlResolver = null,
            IgnoreComments = true,
        };
        try
        {
            using var reader = XmlReader.Create(file, settings);
            while (reader.Read()) { }
            return [];
        }
        catch (XmlException ex)
        {
            if (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)) return [];   // refused on purpose, not broken
            return [new FileCheckIssue(file, ex.LineNumber, "invalid XML: " + ex.Message)];
        }
    }

    // ───────────────────────── JavaScript ─────────────────────────

    private static readonly Regex NodeWhere = new(@"^(?<file>.+?):(?<line>\d+)\s*$", RegexOptions.Multiline);
    // Only a SyntaxError is the script's own: node --check also fails on a broken package.json above the file ("Error:
    // Invalid package config"), which the JSON check reports where it really is.
    private static readonly Regex NodeError = new(@"^(?<msg>SyntaxError:.*)$", RegexOptions.Multiline);

    private static async Task<IReadOnlyList<FileCheckIssue>> CheckJsFileAsync(string file, Func<string, string?> findTool, Runner run,
        CancellationToken ct)
    {
        string? node = findTool("node");
        if (node == null) return [];
        var (line, msg) = await NodeCheckAsync(node, file, run, ct).ConfigureAwait(false);
        return msg == null ? [] : [new FileCheckIssue(file, line, msg)];
    }

    /// <summary><c>node --check</c> on one file → (line, message) of the first syntax error, or (0, null) when it parses
    /// or the answer is not one this pass can trust (JSX, a module read as a script by an old node — those are re-tried
    /// as a module first).</summary>
    internal static async Task<(int Line, string? Message)> NodeCheckAsync(string node, string file, Runner run, CancellationToken ct,
        bool asModule = false)
    {
        var (exit, output, timedOut) = await run(node, ["--check", file], ct).ConfigureAwait(false);
        if (timedOut || exit == 0) return (0, null);
        var err = NodeError.Match(output);
        if (!err.Success) return (0, null);               // node failed for some other reason (a broken install): say nothing
        string msg = err.Groups["msg"].Value.Trim();

        // ES module syntax in a .js that an older node reads as CommonJS: ask again as a module.
        if (!asModule
            && (msg.Contains("Cannot use import statement outside a module", StringComparison.Ordinal)
                || msg.Contains("Unexpected token 'export'", StringComparison.Ordinal)
                || msg.Contains("await is only valid", StringComparison.Ordinal)))
        {
            string copy = Path.Combine(Path.GetTempPath(), "cluadex-filecheck-" + Guid.NewGuid().ToString("N")[..8] + ".mjs");
            try
            {
                File.Copy(file, copy, overwrite: true);
                return await NodeCheckAsync(node, copy, run, ct, asModule: true).ConfigureAwait(false);
            }
            catch (IOException) { return (0, null); }
            finally { TryDelete(copy); }
        }
        // JSX / TypeScript-flavoured code in a .js file is not node's to judge: a bundler reads it. Say nothing.
        if (msg.Contains("Unexpected token '<'", StringComparison.Ordinal)) return (0, null);

        int line = 0;
        foreach (Match m in NodeWhere.Matches(output))
        {
            if (!m.Groups["file"].Value.Trim().EndsWith(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(m.Groups["line"].Value, out line);
            break;
        }
        return (line, msg);
    }

    // ───────────────────────── HTML ─────────────────────────

    private static readonly Regex ScriptBlock = new(@"<script(?<attrs>[^>]*)>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LocalRef = new(
        @"<(?<tag>script|link|img|source)\b[^>]*?\b(?<attr>src|href)\s*=\s*(?<q>[""'])(?<url>[^""']+)\k<q>",
        RegexOptions.IgnoreCase);
    private static readonly Regex TypeAttr = new(@"\btype\s*=\s*[""']?(?<t>[^""'\s>]+)", RegexOptions.IgnoreCase);
    private static readonly Regex RelAttr = new(@"\brel\s*=\s*[""']?(?<r>[^""'>]+)", RegexOptions.IgnoreCase);

    internal static async Task<IReadOnlyList<FileCheckIssue>> CheckHtmlAsync(string file, Func<string, string?> findTool, Runner run,
        CancellationToken ct)
    {
        string html = File.ReadAllText(file);
        var issues = new List<FileCheckIssue>();
        string dir = Path.GetDirectoryName(file) ?? ".";

        // Every local file the page loads must exist (a typo in a script name is a page that does nothing).
        foreach (Match m in LocalRef.Matches(html))
        {
            string tag = m.Groups["tag"].Value.ToLowerInvariant();
            if (tag == "link")
            {
                // Only what the page loads: a stylesheet / icon / manifest / preload. A <link rel="canonical"> is a URL, not a file.
                string tagText = m.Value;
                var rel = RelAttr.Match(tagText);
                string relV = rel.Success ? rel.Groups["r"].Value.ToLowerInvariant() : "";
                if (!(relV.Contains("stylesheet") || relV.Contains("icon") || relV.Contains("manifest") || relV.Contains("preload")
                      || relV.Contains("modulepreload"))) continue;
            }
            string url = m.Groups["url"].Value.Trim();
            string? local = LocalPath(url, dir);
            if (local == null || SafeExists(local)) continue;
            issues.Add(new FileCheckIssue(file, LineOf(html, m.Index), $"<{tag} {m.Groups["attr"].Value}=\"{url}\"> — that file does not exist"));
        }

        // The inline scripts parse (at their own line in the page).
        string? node = findTool("node");
        if (node != null)
        {
            foreach (Match m in ScriptBlock.Matches(html))
            {
                string attrs = m.Groups["attrs"].Value;
                if (Regex.IsMatch(attrs, @"\bsrc\s*=", RegexOptions.IgnoreCase)) continue;
                var t = TypeAttr.Match(attrs);
                string type = t.Success ? t.Groups["t"].Value.ToLowerInvariant() : "";
                bool module = type == "module";
                if (type.Length > 0 && !module && type is not ("text/javascript" or "application/javascript" or "javascript")) continue;   // JSON, templates, babel
                string body = m.Groups["body"].Value;
                if (body.Trim().Length == 0) continue;
                if (body.Contains("{{", StringComparison.Ordinal) || body.Contains("<%", StringComparison.Ordinal)) continue;   // a server template
                int startLine = LineOf(html, m.Groups["body"].Index);
                // Pad so node's line numbers are the page's.
                string text = new string('\n', Math.Max(0, startLine - 1)) + body;
                string temp = Path.Combine(Path.GetTempPath(), "cluadex-filecheck-" + Guid.NewGuid().ToString("N")[..8] + (module ? ".mjs" : ".js"));
                try
                {
                    await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    var (line, msg) = await NodeCheckAsync(node, temp, run, ct).ConfigureAwait(false);
                    if (msg != null) issues.Add(new FileCheckIssue(file, line > 0 ? line : startLine, "inline <script>: " + msg));
                }
                finally { TryDelete(temp); }
                if (issues.Count >= MaxIssues) break;
            }
        }
        return issues;
    }

    /// <summary>A reference the page loads from disk, as a full path; null for anything that is not a plain relative or
    /// root-relative file (a URL, //cdn, data:, #anchor, a template expression).</summary>
    internal static string? LocalPath(string url, string pageDir)
    {
        if (url.Length == 0 || url.StartsWith('#') || url.StartsWith("//", StringComparison.Ordinal)) return null;
        if (Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:")) return null;   // http:, https:, data:, mailto:, javascript:, blob:
        if (url.IndexOfAny(['{', '}', '$', '<', '>', '%', '`']) >= 0) return null;   // {{ }}, ${ }, <% %>, %PUBLIC_URL%
        int cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];
        if (url.Length == 0) return null;
        try
        {
            string rel = Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar);
            // A root-relative "/app.js" is served from the site root, which is usually the page's own folder in a plain project.
            rel = rel.TrimStart(Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(pageDir, rel));
        }
        catch { return null; }
    }

    // ───────────────────────── Python ─────────────────────────

    internal const string PythonFilesCheck =
        "import ast,sys\n"
        + "for p in sys.argv[1:]:\n"
        + "    try:\n"
        + "        ast.parse(open(p,'rb').read(), p)\n"
        + "    except SyntaxError as e:\n"
        + "        print(f'{p}|{e.lineno or 1}|{type(e).__name__}: {e.msg}')\n"
        + "    except (ValueError, OSError):\n"
        + "        pass\n";

    private static async Task<IReadOnlyList<FileCheckIssue>> CheckPythonAsync(IReadOnlyList<string> files, Func<string, string?> findTool,
        Runner run, CancellationToken ct)
    {
        string? python = findTool("python");
        if (python == null) return [];
        var args = new List<string> { "-I", "-X", "utf8", "-c", PythonFilesCheck };
        args.AddRange(files);
        var (_, output, timedOut) = await run(python, args, ct).ConfigureAwait(false);
        if (timedOut) return [];
        var issues = new List<FileCheckIssue>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var parts = raw.Split('|', 3);
            if (parts.Length != 3 || !int.TryParse(parts[1], out int line)) continue;
            string path = files.FirstOrDefault(f => string.Equals(f, parts[0], StringComparison.OrdinalIgnoreCase)) ?? parts[0];
            issues.Add(new FileCheckIssue(path, line, parts[2].Trim()));
        }
        return issues;
    }

    // ───────────────────────── tools ─────────────────────────

    /// <summary>node / python on this PC (the Store's python stub is not Python), or null.</summary>
    public static string? FindTool(string name) => name switch
    {
        "node" => Problems.BuildProblemsService.OnPath("node.exe"),
        "python" => Problems.BuildProblemsService.FindPythonExe(),
        _ => null,
    };

    /// <summary>Runs a program directly (no shell, so nothing in a path is a command), both pipes read at once, stopped
    /// after <see cref="ToolTimeout"/>.</summary>
    public static Task<(int Exit, string Output, bool TimedOut)> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
        => RunProcessAsync(exe, args, ct, ToolTimeout);

    /// <summary>The same with its own time limit. <paramref name="stderrFirst"/>: stderr before stdout in the output, so
    /// the 20,000-character cut keeps a browser's console log and drops the end of the DOM it printed.</summary>
    public static async Task<(int Exit, string Output, bool TimedOut)> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct,
        TimeSpan timeLimit, bool stderrFirst = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NODE_OPTIONS"] = "";             // a user's --require hook must not run
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var p = new Process { StartInfo = psi };
        try { if (!p.Start()) return (-1, "", false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, "", false); }
        try { p.StandardInput.Close(); } catch { }
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeLimit);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            return (-1, "", true);
        }
        string output;
        try
        {
            string stdout = await outTask.ConfigureAwait(false), stderr = await errTask.ConfigureAwait(false);
            output = stderrFirst ? stderr + "\n" + stdout : stdout + "\n" + stderr;
        }
        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); output = ""; }
        if (output.Length > 20000) output = output[..20000];
        return (p.ExitCode, output, false);
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return long.MaxValue; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
