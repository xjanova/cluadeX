using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Lsp;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Document;

namespace LspTest;

/// <summary>
/// Contract harness for the Code Editor's live errors, run against the BUILT CluadeX.dll with a fake language server
/// (this exe started with --fake-lsp, see FakeLanguageServer) — nothing to install, no network, no model:
///   P*  file URI ↔ path (percent-encoded drive colon, spaces, Thai)
///   C*  the catalog: which files, which languageId, finding a server on PATH (an npm .cmd shim), not inventing one
///   M*  diagnostics → squiggle segments (clamping, empty ranges, markers riding edits, hover lookup)
///   J*  publishDiagnostics / hover parsing
///   S*  the service end to end: start, answer workspace/configuration, byte framing with Thai + emoji, debounced
///       incremental and whole-text sync (the server's copy must equal the editor's), hover, too-large files,
///       not-installed → install command → check again, a crashing server, stopping (polite, and a server that
///       ignores shutdown), a project switch, app exit
///   L*  the real Dart analysis server, when this machine has a Dart / Flutter SDK (skip with --offline)
///   X*  the Problems row's "ask AI" snippet and the Thai strings
///   B* T* R* E*  errors from build / check output with no language server, and the editor's messages to the chat
///       (BuildProblemsTests.cs)
/// Run: build CluadeX (-nodeReuse:false), then `dotnet run` here (or -p:CluadeXBin=&lt;OutDir&gt;\).
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    static string Scratch = "";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-lsp") return FakeLanguageServer.Run(args);

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        Scratch = Path.Combine(Path.GetTempPath(), "cluadex-lsptest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Scratch);
        try
        {
            Console.WriteLine("CluadeX live-errors harness — " + typeof(EditorLanguageService).Assembly.Location);
            Paths();
            Catalog();
            Markers();
            Parsing();
            Service();
            if (!args.Contains("--offline")) LiveDart();
            Extras();
            BuildProblems();
        }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); Fail++; }
        finally
        {
            try { Directory.Delete(Scratch, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{Pass} passed, {Fail} failed");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + (detail.Length > 800 ? detail[..800] + "…" : detail).Replace("\n", "\n            "));
    }

    static bool WaitFor(Func<bool> condition, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(20);
        }
        try { return condition(); } catch { return false; }
    }

    static string ReadShared(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                return r.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(10); }
        }
        return "";
    }

    static bool ProcessGone(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
    }

    // ═══════════════════════════ P — paths ═══════════════════════════

    static void Paths()
    {
        Console.WriteLine("P  file URIs");
        Check("P1", "file:///C:/x/a.cs → C:\\x\\a.cs", LspPaths.UriToPath("file:///C:/x/a.cs") == @"C:\x\a.cs");
        Check("P2", "percent-encoded drive colon (tsserver, OmniSharp) resolves to the same file",
            LspPaths.SamePath(LspPaths.UriToPath("file:///c%3A/x/a.cs"), @"C:\x\a.cs"), LspPaths.UriToPath("file:///c%3A/x/a.cs"));
        string odd = Path.Combine(Scratch, "โฟลเดอร์ มี space", "a b.cs");
        string uri = LspPaths.PathToUri(odd);
        Check("P3", "a path with spaces and Thai round-trips through its URI", LspPaths.SamePath(LspPaths.UriToPath(uri), odd) && !uri.Contains(' '), uri);
        Check("P4", "a UNC URI keeps its server", LspPaths.UriToPath("file://server/share/a.py") == @"\\server\share\a.py", LspPaths.UriToPath("file://server/share/a.py"));
    }

    // ═══════════════════════════ C — catalog ═══════════════════════════

    static void Catalog()
    {
        Console.WriteLine("C  catalog");
        Check("C1", "C#, TypeScript, JavaScript, Python and Dart files have a language; Markdown does not",
            LanguageServerCatalog.ForPath("a.cs")?.Key == "csharp" && LanguageServerCatalog.ForPath("a.tsx")?.Key == "typescript"
            && LanguageServerCatalog.ForPath("a.mjs")?.Key == "typescript" && LanguageServerCatalog.ForPath("a.py")?.Key == "python"
            && LanguageServerCatalog.ForPath("a.dart")?.Key == "dart" && LanguageServerCatalog.ForPath("README.md") == null
            && LanguageServerCatalog.ForPath("Makefile") == null);
        var ts = LanguageServerCatalog.ForKey("typescript")!;
        Check("C2", "languageId per extension (typescriptreact / javascript)",
            ts.LanguageIdFor("a.tsx") == "typescriptreact" && ts.LanguageIdFor("a.js") == "javascript" && ts.LanguageIdFor("a.ts") == "typescript");
        Check("C3", "every language says how to get its server (command or page)",
            LanguageServerCatalog.Languages.All(l => !string.IsNullOrEmpty(l.InstallCommand) || l.InstallUrl.StartsWith("https://")));

        // An npm-installed server is a .cmd shim on PATH.
        string bin = Path.Combine(Scratch, "fakebin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "typescript-language-server.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(bin, "typescript-language-server"), "#!/bin/sh\n");   // Git Bash's twin: not runnable here
        string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
            var found = LanguageServerCatalog.Resolve(ts);
            Check("C4", "a server on PATH is found as its .cmd shim, with --stdio",
                found != null && found.Executable.EndsWith("typescript-language-server.cmd", StringComparison.OrdinalIgnoreCase) && found.Arguments == "--stdio",
                found?.Executable ?? "(null)");
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        var nowhere = new LanguageServerLanguage("x", "X", new[] { ".x" },
            new[] { new LanguageServerCandidate("none", new[] { "cluadex-no-such-server-" + Guid.NewGuid().ToString("N")[..6] }, "") },
            null, "https://example.invalid", Array.Empty<string>());
        Check("C5", "a server that is not there is not invented", LanguageServerCatalog.Resolve(nowhere) == null);
    }

    // ═══════════════════════════ M — markers ═══════════════════════════

    static EditorDiagnostic Diag(int sl, int sc, int el, int ec, LspSeverity sev = LspSeverity.Error, string msg = "m")
        => new(sl, sc, el, ec, sev, msg, "src", "");

    static void Markers()
    {
        Console.WriteLine("M  diagnostics → squiggles");
        var doc = new TextDocument("using X;\r\nint value = ERRthing;\nlast");
        // line 1 (0-based) starts at offset 10 ("using X;\r\n" = 10 chars)
        var (s, l) = DiagnosticMarkerRenderer.Map(doc, Diag(1, 12, 1, 20));
        Check("M1", "an LSP range maps to the exact characters (CRLF before it counted once)",
            doc.GetText(s, l) == "ERRthing", $"got '{doc.GetText(s, l)}' at {s}+{l}");
        (s, l) = DiagnosticMarkerRenderer.Map(doc, Diag(1, 14, 1, 14));
        Check("M2", "an empty range on a word squiggles the whole word", doc.GetText(s, l) == "ERRthing", $"'{doc.GetText(s, l)}'");
        (s, l) = DiagnosticMarkerRenderer.Map(doc, Diag(1, 21, 1, 21));
        Check("M3", "an empty range at the end of a line (missing ';') squiggles the character before it",
            l == 1 && doc.GetText(s, l) == ";", $"'{doc.GetText(s, l)}'");
        (s, l) = DiagnosticMarkerRenderer.Map(doc, Diag(1, 4, 1, 999));
        Check("M4", "a character past the end of the line is clamped to the line (no spill into the next)",
            doc.GetText(s, l) == "value = ERRthing;", $"'{doc.GetText(s, l)}'");
        (s, l) = DiagnosticMarkerRenderer.Map(doc, Diag(40, 0, 41, 3));
        Check("M5", "a line past the end (server one edit behind) lands at the end, never throws", s <= doc.TextLength && s + l <= doc.TextLength);

        var marks = new DiagnosticMarkerRenderer();
        marks.Set(doc, new[] { Diag(1, 12, 1, 20, LspSeverity.Warning, "w"), Diag(1, 12, 1, 20, LspSeverity.Error, "e"), Diag(0, 0, 0, 5, LspSeverity.Hint) });
        Check("M6", "hints are not drawn; errors and warnings are", marks.Count == 2);
        var at = marks.At(doc.Text.IndexOf("ERRthing", StringComparison.Ordinal) + 3);
        Check("M7", "hovering a squiggle finds its problems, most severe first", at.Count == 2 && at[0].Diagnostic.Message == "e");
        doc.Insert(10, "// a new line above\n");
        var moved = marks.Markers.First();
        Check("M8", "typing above an error moves its squiggle with the text", doc.GetText(moved) == "ERRthing", $"'{doc.GetText(moved)}'");
        marks.Set(doc, Array.Empty<EditorDiagnostic>());
        Check("M9", "a fresh publish replaces the markers (no leftovers)", marks.Count == 0);
    }

    // ═══════════════════════════ J — parsing ═══════════════════════════

    static void Parsing()
    {
        Console.WriteLine("J  protocol parsing");
        using var d = JsonDocument.Parse("""
            {"uri":"file:///c:/a.cs","diagnostics":[
              {"range":{"start":{"line":3,"character":1},"end":{"line":3,"character":4}},"message":"later","code":123},
              {"range":{"start":{"line":0,"character":2},"end":{"line":0,"character":5}},"severity":2,"message":"first","source":"cs","code":"CS0168"},
              {"message":"no range: skipped"}]}
            """);
        var list = EditorLanguageService.ParseDiagnostics(d.RootElement);
        Check("J1", "diagnostics: sorted by position, no severity = error, numeric code kept, rangeless skipped",
            list.Count == 2 && list[0].Message == "first" && list[0].Severity == LspSeverity.Warning && list[0].Code == "CS0168"
            && list[1].Severity == LspSeverity.Error && list[1].Code == "123");

        string? Hover(string json) { using var h = JsonDocument.Parse(json); return EditorLanguageService.HoverText(h.RootElement); }
        var md = Hover("""{"contents":{"kind":"markdown","value":"```csharp\nint Foo(string s)\n```\n---\nReturns foo."}}""");
        Check("J2", "hover markdown: fences dropped, the signature and the docs kept",
            md != null && md.Contains("int Foo(string s)") && md.Contains("Returns foo.") && !md.Contains("```"), md ?? "(null)");
        var arr = Hover("""{"contents":[{"language":"python","value":"def f(x)"},"Docs here"]}""");
        Check("J3", "hover MarkedString[] (pylsp) joined", arr == "def f(x)\n\nDocs here", arr ?? "(null)");
        Check("J4", "hover null / empty contents → nothing to show",
            Hover("""{"contents":""}""") == null && Hover("""{"other":1}""") == null && Hover("null") == null);
    }

    // ═══════════════════════════ S — the service end to end ═══════════════════════════

    static string Exe => Environment.ProcessPath!;

    static EditorLanguageService NewService(string log, string extra = "", int sync = 2)
    {
        var svc = new EditorLanguageService
        {
            Debounce = TimeSpan.FromMilliseconds(200),
            LargeFileDebounce = TimeSpan.FromMilliseconds(400),
            StartTimeout = TimeSpan.FromSeconds(20),
            StopBudget = TimeSpan.FromSeconds(1.5),
        };
        svc.Resolver = lang => lang.Key == "dart" ? null
            : new ResolvedLanguageServer("fake-" + lang.Key, Exe, $"--fake-lsp --log \"{log}\" --sync {sync} {extra}");
        return svc;
    }

    /// <summary>The editor's side of a document: a TextDocument edited like a user types, reporting each edit the way
    /// CodeEditorView.Lsp does (LSP range from Changing, snapshot after Changed).</summary>
    sealed class EditorSim
    {
        public readonly TextDocument Doc;
        readonly EditorLanguageService _svc;
        readonly string _path;
        LspTextChange? _change;
        public bool ForceFull;

        public EditorSim(EditorLanguageService svc, string root, string path, string text)
        {
            _svc = svc; _path = path;
            Doc = new TextDocument(text);
            Doc.Changing += (_, e) =>
            {
                var a = Doc.GetLocation(e.Offset);
                var b = Doc.GetLocation(e.Offset + e.RemovalLength);
                _change = ForceFull ? null : new LspTextChange(a.Line - 1, a.Column - 1, b.Line - 1, b.Column - 1, e.InsertedText.Text);
            };
            Doc.Changed += (_, _) =>
            {
                var snap = Doc.CreateSnapshot();
                _svc.ChangeDocument(_path, _change, () => snap.Text, Doc.TextLength);
            };
            var first = Doc.CreateSnapshot();
            _svc.OpenDocument(root, path, () => first.Text, Doc.TextLength);
        }
    }

    static int Count(string log, string prefix) => ReadShared(log).Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    static void Service()
    {
        Console.WriteLine("S  service with a fake language server");
        string root = Path.Combine(Scratch, "proj");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "Program.cs");
        string log = Path.Combine(Scratch, "s1.log");
        var svc = NewService(log);
        var events = 0;
        svc.DiagnosticsChanged += _ => Interlocked.Increment(ref events);
        try
        {
            var ed = new EditorSim(svc, root, file, "class A {\n    int ok;\n    int bad = ERRone;\n}\n");
            bool running = WaitFor(() => svc.StatusFor(file).State == LspServerState.Running, 15000);
            Check("S1", "the server starts for the project and the file is opened with it",
                running && Count(log, "didOpen") == 1 && ReadShared(log).Contains("initialize rootUri=file:///"),
                svc.StatusFor(file) + "\n" + ReadShared(log));
            Check("S2", "workspace/configuration (string id) is answered with one null per item — a pyright-style server waits on it",
                WaitFor(() => ReadShared(log).Contains("config-answer [null]"), 5000), ReadShared(log));
            bool got = WaitFor(() => svc.DiagnosticsFor(file).Count == 1, 5000);
            var d = svc.DiagnosticsFor(file).FirstOrDefault();
            Check("S3", "published diagnostics arrive with the right line, range and severity",
                got && d!.StartLine == 2 && d.StartCharacter == 14 && d.EndCharacter == 20 && d.Severity == LspSeverity.Error && d.Code == "F001",
                string.Join("; ", svc.DiagnosticsFor(file)));
            Check("S4", "byte framing: a Thai + emoji message arrives whole (Content-Length counts bytes)",
                d?.Message == "ผิดตรงนี้: ERRone 😀", d?.Message ?? "(none)");
            Check("S5", "the language server's name is reported for the status bar", svc.StatusFor(file).ServerName == "fake-csharp");

            // Debounce: 40 keystrokes 5 ms apart become one didChange (two at most if the machine stalls).
            int before = Count(log, "didChange");
            int at = ed.Doc.Text.IndexOf("int ok;", StringComparison.Ordinal) + "int ok".Length;
            foreach (char c in "_value_typed_quickly_by_the_user_1234567")
            {
                ed.Doc.Insert(at++, c.ToString());
                Thread.Sleep(5);
            }
            bool settled = WaitFor(() => ReadShared(log + ".text") == ed.Doc.Text, 4000);
            int sent = Count(log, "didChange") - before;
            Check("S6", "40 keystrokes are sent as one debounced didChange, and the server's text equals the editor's",
                settled && sent >= 1 && sent <= 2, $"didChange sent: {sent}\nserver: {ReadShared(log + ".text")}\neditor: {ed.Doc.Text}");
            Check("S7", "the edits went incrementally (a few bytes, not the whole file)",
                ReadShared(log).Split('\n').Where(l => l.StartsWith("didChange")).All(l => l.Contains("kind=incr")));

            // A deletion across lines + a CRLF insert: the server's copy must still match.
            int nl = ed.Doc.Text.IndexOf('\n');
            ed.Doc.Replace(nl - 2, 8, "X\r\n  WARNme\r\n");
            ed.Doc.Remove(0, 3);
            Check("S8", "multi-line replace, CRLF insert and a delete keep both copies identical",
                WaitFor(() => ReadShared(log + ".text") == ed.Doc.Text, 4000), $"server: {ReadShared(log + ".text")}\neditor: {ed.Doc.Text}");
            Check("S9", "the diagnostics follow the edit (a new warning appears where it was typed)",
                WaitFor(() => svc.DiagnosticsFor(file).Any(x => x.Severity == LspSeverity.Warning && x.StartLine == 1), 4000),
                string.Join("; ", svc.DiagnosticsFor(file)));

            // A change too big to describe (reload / replace-all) goes as the whole text.
            ed.ForceFull = true;
            ed.Doc.Text = "class B { int ERRtwo; int ERRthree; }";
            ed.ForceFull = false;
            Check("S10", "an edit reported without a range is sent as the whole text, even to an incremental server",
                WaitFor(() => ReadShared(log + ".text") == ed.Doc.Text, 4000) && ReadShared(log).Split('\n').Last(l => l.StartsWith("didChange")).Contains("kind=full"));
            Check("S11", "…and the squiggles are recomputed for it", WaitFor(() => svc.DiagnosticsFor(file).Count == 2, 4000));

            string? hover = svc.HoverAsync(file, 0, 16).GetAwaiter().GetResult();
            Check("S12", "hover answers for the symbol under the mouse, as plain text", hover == "symbol ERRtwo\nDocs for `ERRtwo`", hover ?? "(null)");

            // Hover right after typing: the pending edit is sent first, so the answer is about the text on screen.
            ed.Doc.Insert(ed.Doc.TextLength, " fresh_word");
            string? fresh = svc.HoverAsync(file, 0, ed.Doc.TextLength - 3).GetAwaiter().GetResult();
            Check("S13", "a hover flushes the pending edit first (no waiting for the debounce)", fresh?.StartsWith("symbol fresh_word") == true, fresh ?? "(null)");

            // Too large: never sent.
            string big = Path.Combine(root, "Big.cs");
            int opensBefore = Count(log, "didOpen");
            svc.OpenDocument(root, big, () => new string('x', 10), EditorLanguageService.MaxLiveChars + 1);
            Thread.Sleep(300);
            Check("S14", "a file over 2M chars is not sent (status says live checks are off)",
                svc.StatusFor(big).State == LspServerState.TooLarge && Count(log, "didOpen") == opensBefore);

            // Close: didClose, problems gone.
            svc.CloseDocument(file);
            Check("S15", "closing the file sends didClose and drops its problems",
                WaitFor(() => Count(log, "didClose") == 1, 3000) && svc.DiagnosticsFor(file).Count == 0);

            var pids = svc.ServerProcessIds();
            var sw = Stopwatch.StartNew();
            svc.StopAllAsync().GetAwaiter().GetResult();
            Check("S16", "stopping: shutdown + exit, the process is gone within the budget",
                pids.Count == 1 && WaitFor(() => ProcessGone(pids[0]), 3000) && ReadShared(log).Contains("shutdown\nexit"),
                $"pids={string.Join(",", pids)} stop took {sw.ElapsedMilliseconds} ms\n{ReadShared(log)}");
        }
        finally { svc.Dispose(); }

        FullSync(root);
        NotInstalledThenInstalled(root);
        Crash(root);
        Stubborn(root);
        ProjectSwitchAndExit();
    }

    static void FullSync(string root)
    {
        string log = Path.Combine(Scratch, "full.log");
        var svc = NewService(log, sync: 1);
        try
        {
            string file = Path.Combine(root, "app.py");
            var ed = new EditorSim(svc, root, file, "def f():\n    return 1\n");
            WaitFor(() => svc.StatusFor(file).State == LspServerState.Running && Count(log, "didOpen") == 1, 15000);
            for (int i = 0; i < 10; i++) { ed.Doc.Insert(ed.Doc.TextLength, "# c" + i + "\n"); Thread.Sleep(5); }
            bool same = WaitFor(() => ReadShared(log + ".text") == ed.Doc.Text, 4000);
            var changes = ReadShared(log).Split('\n').Where(l => l.StartsWith("didChange")).ToList();
            Check("S17", "a whole-text server gets the whole text once per pause, and it matches",
                same && changes.Count is >= 1 and <= 3 && changes.All(l => l.Contains("kind=full")),
                string.Join("\n", changes));
            Check("S18", "the Python file was opened with languageId python", ReadShared(log).Contains("lang=python"));
        }
        finally { svc.Dispose(); }
    }

    static void NotInstalledThenInstalled(string root)
    {
        string log = Path.Combine(Scratch, "install.log");
        var svc = NewService(log);
        bool installed = false;
        svc.Resolver = lang => installed ? new ResolvedLanguageServer("fake-dart", Exe, $"--fake-lsp --log \"{log}\"") : null;
        try
        {
            string cs = Path.Combine(root, "Other.cs");
            svc.OpenDocument(root, cs, () => "class C { int ERRx; }", 21);
            WaitFor(() => svc.StatusFor(cs).State == LspServerState.NotInstalled, 5000);
            var st = svc.StatusFor(cs);
            Check("S19", "no server: the status says so and carries the install command (no dead button)",
                st.State == LspServerState.NotInstalled && st.InstallCommand == "dotnet tool install --global csharp-ls" && st.ServerName == null,
                st.ToString());
            string dart = Path.Combine(root, "main.dart");
            svc.OpenDocument(root, dart, () => "void main() {}", 14);
            WaitFor(() => svc.StatusFor(dart).State == LspServerState.NotInstalled, 5000);
            var sd = svc.StatusFor(dart);
            Check("S20", "Dart without an SDK: no made-up command, the page that explains it instead",
                sd.State == LspServerState.NotInstalled && sd.InstallCommand == null && sd.InstallUrl == "https://dart.dev/get-dart", sd.ToString());

            installed = true;   // the user ran the command…
            svc.RestartAsync("csharp").GetAwaiter().GetResult();   // …and pressed Check again
            Check("S21", "Check again after installing: the server starts and the open file is checked",
                WaitFor(() => svc.StatusFor(cs).State == LspServerState.Running && svc.DiagnosticsFor(cs).Count == 1, 15000),
                svc.StatusFor(cs) + "\n" + ReadShared(log));
        }
        finally { svc.Dispose(); }
    }

    static void Crash(string root)
    {
        string log = Path.Combine(Scratch, "crash.log");
        var svc = NewService(log, "--crash-after-open");
        try
        {
            string file = Path.Combine(root, "index.ts");
            svc.OpenDocument(root, file, () => "let ERRa = 1;", 13);
            bool failed = WaitFor(() => svc.StatusFor(file).State == LspServerState.Failed, 15000);
            var st = svc.StatusFor(file);
            Check("S22", "a server that dies is reported as stopped, with its last stderr line as the reason",
                failed && st.Detail?.Contains("boom") == true, st.ToString());
            Check("S23", "…and its problems are not left on screen as if they were current", svc.DiagnosticsFor(file).Count == 0);
            Check("S24", "the dead server is not counted as running", svc.ServerProcessIds().Count == 0);
        }
        finally { svc.Dispose(); }
    }

    static void Stubborn(string root)
    {
        string log = Path.Combine(Scratch, "stubborn.log");
        var svc = NewService(log, "--ignore-shutdown");
        try
        {
            string file = Path.Combine(root, "x.js");
            svc.OpenDocument(root, file, () => "var a;", 6);
            WaitFor(() => svc.StatusFor(file).State == LspServerState.Running, 15000);
            var pids = svc.ServerProcessIds();
            var sw = Stopwatch.StartNew();
            svc.StopAllAsync().GetAwaiter().GetResult();
            long took = sw.ElapsedMilliseconds;
            Check("S25", "a server that ignores shutdown is killed when the budget runs out (not waited on forever)",
                pids.Count == 1 && WaitFor(() => ProcessGone(pids[0]), 2000) && took < 4000, $"took {took} ms");
        }
        finally { svc.Dispose(); }
    }

    static void ProjectSwitchAndExit()
    {
        string a = Path.Combine(Scratch, "projA"), b = Path.Combine(Scratch, "projB");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string log = Path.Combine(Scratch, "switch.log");
        var svc = NewService(log);
        try
        {
            string fa = Path.Combine(a, "a.cs");
            svc.OpenDocument(a, fa, () => "class A {}", 10);
            WaitFor(() => svc.StatusFor(fa).State == LspServerState.Running, 15000);
            var first = svc.ServerProcessIds();
            string fb = Path.Combine(b, "b.cs");
            svc.OpenDocument(b, fb, () => "class B {}", 10);
            WaitFor(() => svc.StatusFor(fb).State == LspServerState.Running, 15000);
            var second = svc.ServerProcessIds();
            Check("S26", "another project: the old project's server stops, a new one serves the new project",
                first.Count == 1 && WaitFor(() => ProcessGone(first[0]), 4000) && second.Count == 1 && second[0] != first[0],
                $"first={string.Join(",", first)} second={string.Join(",", second)}");
            Check("S27", "the new server was started with the new project as its root",
                ReadShared(log).Split('\n').Last(l => l.StartsWith("initialize ")).Contains("projB"), ReadShared(log));

            svc.OnAppShutdown();   // what App.OnExit runs
            Check("S28", "app exit stops every server", second.Count == 1 && WaitFor(() => ProcessGone(second[0]), 3000));
            Check("S29", "a file still on screen after its project closed reports nothing (no endless 'starting…')",
                svc.StatusFor(fb).State == LspServerState.NotSupported && svc.StatusFor(fa).State == LspServerState.NotSupported,
                svc.StatusFor(fb).ToString());
        }
        finally { svc.Dispose(); }
    }

    // ═══════════════════════════ L — a real server ═══════════════════════════

    /// <summary>The real Dart analysis server when this machine has a Dart / Flutter SDK (nothing is installed for the
    /// test): proves the client against a server we did not write. A GAP line, not a failure, where there is none.</summary>
    static void LiveDart()
    {
        Console.WriteLine("L  live: the real Dart language server");
        var found = LanguageServerCatalog.Resolve(LanguageServerCatalog.ForKey("dart")!);
        if (found == null) { Console.WriteLine("  GAP  L*    no Dart SDK on this machine: the live check did not run"); return; }
        Check("L1", "the real probe finds the SDK's dart.exe (not Flutter's update-checking .bat)",
            found.Executable.EndsWith(@"\dart.exe", StringComparison.OrdinalIgnoreCase), found.Executable);

        string root = Path.Combine(Scratch, "dartproj");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "main.dart");
        string text = "void main() {\n  int count = \"three\";\n  print(count);\n}\n";
        File.WriteAllText(file, text);
        var svc = new EditorLanguageService { Debounce = TimeSpan.FromMilliseconds(300), StartTimeout = TimeSpan.FromSeconds(90) };
        try
        {
            var ed = new EditorSim(svc, root, file, text);
            bool running = WaitFor(() => svc.StatusFor(file).State == LspServerState.Running, 90000);
            bool error = WaitFor(() => svc.DiagnosticsFor(file).Any(d => d.Severity == LspSeverity.Error && d.StartLine == 1), 60000);
            var err = svc.DiagnosticsFor(file).FirstOrDefault(d => d.Severity == LspSeverity.Error);
            Check("L2", "a type error shows up as an error on its line, from the real analyzer",
                running && error && err!.Source == "dart", svc.StatusFor(file) + "\n" + string.Join("\n", svc.DiagnosticsFor(file)));
            var mapped = err == null ? (0, 0) : DiagnosticMarkerRenderer.Map(ed.Doc, err);
            Check("L3", "…and its squiggle covers the offending text", err != null && ed.Doc.GetText(mapped.Item1, mapped.Item2) == "\"three\"",
                err == null ? "" : ed.Doc.GetText(mapped.Item1, mapped.Item2));
            string? hover = svc.HoverAsync(file, 2, 3).GetAwaiter().GetResult();
            Check("L4", "hover on print shows its signature", hover?.Contains("print(") == true, hover ?? "(null)");

            // The human (or the AI) fixes it: the error must go away after the debounced incremental edit.
            int at = ed.Doc.Text.IndexOf("\"three\"", StringComparison.Ordinal);
            ed.Doc.Replace(at, "\"three\"".Length, "3");
            Check("L5", "fixing the code clears the error (incremental edit reached the real server)",
                WaitFor(() => !svc.DiagnosticsFor(file).Any(d => d.Severity == LspSeverity.Error), 30000),
                string.Join("\n", svc.DiagnosticsFor(file)));

            var pids = svc.ServerProcessIds();
            svc.StopAllAsync().GetAwaiter().GetResult();
            Check("L6", "the real server shuts down when the project closes", pids.Count == 1 && WaitFor(() => ProcessGone(pids[0]), 5000));
        }
        finally { svc.Dispose(); }
    }

    // ═══════════════════════════ X — extras ═══════════════════════════

    static void Extras()
    {
        Console.WriteLine("X  ask-AI snippet, strings");
        var snippetMethod = typeof(CluadeX.Views.CodeEditorView).GetMethod("Snippet", BindingFlags.NonPublic | BindingFlags.Static);
        var lines = Enumerable.Range(1, 20).Select(i => "line " + i).ToArray();
        string snippet = (string)snippetMethod!.Invoke(null, new object[] { lines, 10, 2 })!;
        Check("X1", "the snippet sent to the AI is numbered and marks the problem's line",
            snippet == "   8 | line 8\n   9 | line 9\n> 10 | line 10\n  11 | line 11\n  12 | line 12", snippet);
        string edge = (string)snippetMethod.Invoke(null, new object[] { lines, 1, 2 })!;
        Check("X2", "…and stays inside the file at its first line", edge.StartsWith("> 1 | line 1") && edge.Split('\n').Length == 3, edge);

        var keysType = typeof(EditorLanguageService).Assembly.GetType("CluadeX.Services.Localization.LspKeys");
        var keys = (Dictionary<string, Dictionary<string, string>>)keysType!.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var bad = keys.Where(k => !k.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th)
                                   || !k.Value.TryGetValue("en", out var en) || Placeholders(en) != Placeholders(th))
                      .Select(k => k.Key).ToList();
        Check("X3", "every lsp.* string has Thai, with the same {placeholders} as the English", bad.Count == 0, string.Join(", ", bad));

        // Every key the editor uses exists (a missing key renders as the raw key).
        string root = FindRepoRoot();
        var used = new HashSet<string>();
        foreach (var f in new[] { "CluadeX/Views/CodeEditorView.Lsp.cs", "CluadeX/Views/CodeEditorView.Problems.cs", "CluadeX/Views/CodeEditorView.xaml" })
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, f)),
                         @"[""\s]((?:lsp|problems)\.[A-Za-z.]+|search\.toChat[A-Za-z]*|editor\.(?:menu|ask)\.[A-Za-z]+|editor\.(?:askChangeHint|contextAdded))[""}]"))
                used.Add(m.Groups[1].Value);
        var missing = used.Where(k => !keys.ContainsKey(k)).ToList();
        Check("X4", "every lsp.* / problems.* / editor-to-chat key the editor uses is defined", used.Count > 30 && missing.Count == 0, $"used {used.Count}; missing: {string.Join(", ", missing)}");
    }

    static string Placeholders(string s)
        => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x));

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CluadeX", "CluadeX.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
