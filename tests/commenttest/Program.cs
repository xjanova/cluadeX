using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.AutoComment;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace CommentTest;

/// <summary>
/// Contract harness for phase 5 — auto-comment existing code — run against the BUILT CluadeX.dll with a mocked model
/// answer (no network, no model, no GPU). Every check feeds real file content through the real lexer / planner /
/// service and asserts what lands on disk:
///   L*  the lexer: a comment never lands inside a C# verbatim / raw / interpolated string, a Python triple-quoted or
///       f-string, a JS template literal or JSX, a Dart triple / raw string, PHP's HTML / strings / heredoc, an XML tag /
///       comment / CDATA, a Java text block, a Go raw string, a C++ raw string — and code after them is reachable again
///   I*  insertion: each language's own comment syntax, the line's own indentation (tabs kept), the file's line ending,
///       above attributes / decorators
///   R*  round trip: stripping the inserted lines gives the original back byte for byte (BOM, CRLF, legacy code page,
///       no final newline); Verify catches a plan that changed code
///   M*  a model answer that tries to change code (edit fields, comment escapes, bidi controls) is refused whole; bad
///       positions are skipped one by one
///   X*  cancelling mid-run, or a file that changed after the preview, leaves the file untouched
///   S*  each scope picks the right lines (functions, changed lines from git diff, key points, file, selection)
///   A*  apply: Time Machine restore point, atomic write, undo
///   P*  prompt, cost estimate, and the provider adapter's structured-output call
///   W*  the Code Editor flow through the real view model (estimate → confirm → preview diff → apply → undo, cancel,
///       unsaved tab) and the static XAML checks: every Loc key has Thai, every binding / resource exists
///   T*  the chat tool add_code_comments through the real AgentToolService: the same bytes as the editor, the permission
///       prompt with the cost before the model is asked, refusals (code-changing answer, file changed mid-run, unsaved
///       tab, bad arguments), the restore point (its own, or the chat run's step), and the /comment skill
/// Run: build CluadeX first (this links the DLL and does not rebuild it), then `dotnet run`.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    static string Scratch = "";

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        Scratch = Path.Combine(Path.GetTempPath(), "cluadex-commenttest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Scratch);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
        finally { try { Directory.Delete(Scratch, true); } catch { } }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + detail.Replace("\n", "\n            "));
    }

    static async Task<int> Run()
    {
        Console.WriteLine($"CluadeX.dll: {typeof(AutoCommentService).Assembly.Location}");
        Lexer();
        Insertion();
        RoundTrip();
        await ModelAnswers();
        await Cancel();
        Scopes();
        await ApplyAndUndo();
        await PromptAndProvider();
        int w = await OnSta(Workbench);
        if (w != 0) return w;
        Console.WriteLine();
        Console.WriteLine($"{Pass} passed, {Fail} failed");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    // ───────────────────────── helpers ─────────────────────────

    static string N(string s) => s.Replace("\r\n", "\n");

    static CommentLanguage Lang(string ext) => CommentLanguage.For("x" + ext)!;

    /// <summary>1-based lines a comment may go above.</summary>
    static SortedSet<int> Insertable(string code, string ext)
    {
        var lex = CommentLexer.Lex(N(code), Lang(ext));
        var set = new SortedSet<int>();
        for (int k = 0; k < lex.Count; k++) if (lex.Info[k].Insertable) set.Add(k + 1);
        return set;
    }

    static void ExpectLines(string id, string name, string code, string ext, int[] yes, int[] no)
    {
        var got = Insertable(code, ext);
        var wrongNo = no.Where(got.Contains).ToList();
        var wrongYes = yes.Where(l => !got.Contains(l)).ToList();
        Check(id, name, wrongNo.Count == 0 && wrongYes.Count == 0,
            $"insertable: {string.Join(",", got)}\nshould NOT be: {string.Join(",", wrongNo)}  should be: {string.Join(",", wrongYes)}");
    }

    sealed class Canned(string answer) : IAutoCommentModel
    {
        public string? LastUser;
        public Task<string> AnswerAsync(string s, string u, CancellationToken ct) { LastUser = u; return Task.FromResult(answer); }
    }

    sealed class Hanging : IAutoCommentModel
    {
        public bool Started;
        public async Task<string> AnswerAsync(string s, string u, CancellationToken ct)
        {
            Started = true;
            await Task.Delay(Timeout.Infinite, ct);
            return "{}";
        }
    }

    static string Answer(params (int Line, string Text)[] c) =>
        JsonSerializer.Serialize(new { comments = c.Select(x => new { line = x.Line, text = x.Text }) });

    static string Write(string name, string text, Encoding? enc = null)
    {
        string p = Path.Combine(Scratch, name);
        File.WriteAllBytes(p, (enc ?? new UTF8Encoding(false)).GetBytes(text));
        return p;
    }

    static async Task<(AutoCommentPreview? P, Exception? E)> Preview(string path, AutoCommentOptions o, IAutoCommentModel m)
    {
        try { return (await AutoCommentService.RunAsync(AutoCommentService.Prepare(path, o), m, CancellationToken.None), null); }
        catch (Exception ex) { return (null, ex); }
    }

    static AutoCommentOptions Opt(CommentScopeKind s = CommentScopeKind.File, string lang = "en") => new(s, lang);

    static string Key(Exception? e) => e is AutoCommentException a ? a.Key : e?.GetType().Name ?? "(none)";

    static string Text(AutoCommentPreview p) => p.Plan.Result;

    // ───────────────────────── L: lexer ─────────────────────────

    static void Lexer()
    {
        Console.WriteLine("\n[L] lexer — no comment inside a string, comment, interpolation or markup");

        ExpectLines("L1", "C#: verbatim, raw, $$ raw, interpolation hole across lines, char literal", """"
            class A
            {
                string v = @"first
            second ""quoted"" // not a comment
            last";
                string r = """
                    raw "x" text
                    """;
                string ri = $$"""
                    {{Name}} and {literal}
                    """;
                string hole = $"value {Call(
                    "}",
                    x)} end";
                char c = '"';
                string after = "ok";
            }
            """", ".cs", yes: new[] { 1, 2, 3, 6, 9, 12, 15, 16, 17 }, no: new[] { 4, 5, 7, 8, 10, 11, 13, 14 });

        ExpectLines("L2", "C#: block comment, $@ verbatim interpolated, an interpolation whose string holds a brace", """
            /* block
            still block */
            var s = $@"a {x}
            b {(y ? "}" : "{")}
            c";
            var t = 1;
            """, ".cs", yes: new[] { 1, 3, 6 }, no: new[] { 2, 4, 5 });

        ExpectLines("L3", "Python: triple quotes, f-string holes with nested quotes, raw, backslash continuations", """"
            def f():
                s = """doc
            # not a comment
                end"""
                t = f"{x!r:>{w}} {'}'}"
                u = f'''a {
                    y
                } b'''
                v = r'\'still'
                w = 'a\
            b'
                x = 1 + \
                    2
                return s
            """", ".py", yes: new[] { 1, 2, 5, 6, 9, 10, 12, 14 }, no: new[] { 3, 4, 7, 8, 11, 13 });

        ExpectLines("L4", "Python: shebang and coding cookie stay first", "#!/usr/bin/env python\n# -*- coding: utf-8 -*-\nx = 1\n",
            ".py", yes: new[] { 3 }, no: new[] { 1, 2 });

        ExpectLines("L5", "JS/JSX: template literal with nested template, regex with // and a quote, JSX children", """
            const a = `multi
            ${ b ? `inner
            ` : "x" }
            end`;
            const re = /\/\/ not "a comment/g;
            function f() {
              return (
                <div className="x">
                  {items.map(i => (
                    <li>{i}</li>
                  ))}
                  text // looks like a comment
                </div>
              );
            }
            const s = 'it\'s';
            """, ".jsx", yes: new[] { 1, 5, 6, 7, 8, 14, 15, 16 }, no: new[] { 2, 3, 4, 9, 10, 11, 12, 13 });

        ExpectLines("L6", "TS: generics and comparisons are not JSX; a string holding /* is not a comment", """
            const m = new Map<string, number>();
            if (a < b && c > d) { go(); }
            const s = "/* not open";
            const t = 2;
            """, ".ts", yes: new[] { 1, 2, 3, 4 }, no: Array.Empty<int>());

        ExpectLines("L7", "Dart: triple, raw triple, interpolation with a string, nested block comments", """"
            var a = '''multi
            line ${x} ''';
            var b = r"""raw \
            """;
            var c = "x ${ f("}") } y";
            /* outer
            /* nested */
            still in outer */
            var d = 1;
            """", ".dart", yes: new[] { 1, 3, 5, 6, 9 }, no: new[] { 2, 4, 7, 8 });

        ExpectLines("L8", "PHP: HTML outside <?php, multi-line strings, {$…} holes, heredoc, nowdoc, ?> in a // comment", """
            <html>
            <?php
            $a = 'multi
            line';
            $b = "x {$arr['}']} y
            z";
            $h = <<<EOT
            heredoc // text
            EOT;
            $n = <<<'NOW'
            nowdoc
            NOW;
            // comment ?> back to html
            <p>html</p>
            <?php echo 1; ?>
            </html>
            """, ".php", yes: new[] { 3, 5, 7, 10, 13 }, no: new[] { 1, 2, 4, 6, 8, 9, 11, 12, 14, 15, 16 });

        ExpectLines("L9", "XAML: declaration first, inside tags / attributes, comments, text content, inside CDATA", """
            <?xml version="1.0"?>
            <Window x:Class="A"
                    Title="multi
            line">
              <!-- existing
              comment -->
              <Grid>
                <TextBlock>Hello
                  world</TextBlock>
                <![CDATA[
            data ]]>
              </Grid>
            </Window>
            """, ".xaml", yes: new[] { 2, 5, 7, 8, 10, 13 }, no: new[] { 1, 3, 4, 6, 9, 11 });

        ExpectLines("L10", "Java text block, Go raw string + cgo preamble, C++ raw string and a spliced // comment", """
            String q = \"\"\"
                // in a text block
                \"\"\";
            int z = 1;
            """.Replace("\\\"", "\""), ".java", yes: new[] { 1, 4 }, no: new[] { 2, 3 });
        ExpectLines("L11", "Go: raw string; `import \"C\"` keeps its preamble", "x := `raw\n// in raw\n`\n/* #include <a.h> */\nimport \"C\"\ny := 1\n",
            ".go", yes: new[] { 1, 4, 6 }, no: new[] { 2, 3, 5 });
        ExpectLines("L12", "C++: raw string with a delimiter; a // comment ending in a backslash swallows the next line",
            "auto s = R\"d(\n)\" not the end\n)d\";\n// spliced \\\nstill comment\nint k = 1'000;\nint j = 0;\n",
            ".cpp", yes: new[] { 1, 4, 6, 7 }, no: new[] { 2, 3, 5 });

        // A stray quote must not swallow the rest of the file: lexing resyncs at the next line.
        ExpectLines("L13", "JS: an unterminated string resyncs on the next line", "const a = 'oops\nconst b = 1;\n",
            ".js", yes: new[] { 1, 2 }, no: Array.Empty<int>());
        Check("L14", "unsupported file types are refused, not guessed", CommentLanguage.For("a.yaml") == null && CommentLanguage.For("a.json") == null
            && CommentLanguage.For("a.html") == null && CommentLanguage.For("A.CS") != null);
    }

    // ───────────────────────── I: insertion ─────────────────────────

    static void Insertion()
    {
        Console.WriteLine("\n[I] insertion — the language's syntax, the line's indent, the file's line ending");

        (string Result, CommentPlan Plan) Place(string code, string ext, params (int, string)[] c)
        {
            var lang = Lang(ext);
            var lex = CommentLexer.Lex(code, lang);
            var allowed = CommentScopes.AllowedLines(lex, lang, CommentScopeKind.File);
            var plan = CommentPlanner.Plan(code, lang, lex, c.Select(x => new ProposedComment(x.Item1, x.Item2)), allowed,
                CommentScopes.AnchorRemap(lex, lang), 100);
            return (plan.Result, plan);
        }

        var cs = Place("class A\n{\n        int x = 1;\n}\n", ".cs", (3, "Counts the retries."));
        Check("I1", "C#: // with the line's 8-space indent", cs.Result == "class A\n{\n        // Counts the retries.\n        int x = 1;\n}\n", cs.Result);

        var py = Place("def f():\n\tif x:\n\t\treturn 1\n", ".py", (3, "Early exit."));
        Check("I2", "Python: # with the line's tab indent", py.Result == "def f():\n\tif x:\n\t\t# Early exit.\n\t\treturn 1\n", py.Result);

        var xml = Place("<Grid>\n    <Button/>\n</Grid>\n", ".xaml", (2, "The only button."));
        Check("I3", "XAML: <!-- --> with the element's indent", xml.Result == "<Grid>\n    <!-- The only button. -->\n    <Button/>\n</Grid>\n", xml.Result);

        var php = Place("<?php\nfunction a() {\n    return 1;\n}\n", ".php", (3, "Always one."));
        Check("I4", "PHP: // inside the code block", php.Result.Contains("\n    // Always one.\n    return 1;"), php.Result);

        var js = Place("function f() {\r\n  go();\r\n}\r\n", ".js", (2, "Line one\nline two"));
        Check("I5", "JS: a multi-line comment becomes several // lines, in the file's CRLF",
            js.Result == "function f() {\r\n  // Line one\r\n  // line two\r\n  go();\r\n}\r\n", js.Result.Replace("\r", "\\r"));

        var dart = Place("void main() {\n  run();\n}", ".dart", (2, "Starts the app."));
        Check("I6", "Dart: // and a file without a final newline keeps none", dart.Result == "void main() {\n  // Starts the app.\n  run();\n}", dart.Result);

        var attr = Place("class T\n{\n    [Fact]\n    [Trait(\"a\", \"b\")]\n    public void Works()\n    {\n    }\n}\n", ".cs", (5, "The happy path."));
        Check("I7", "C#: a method's comment goes above its attributes", attr.Result.Contains("    // The happy path.\n    [Fact]\n"), attr.Result);

        var deco = Place("@app.route('/')\ndef index():\n    return 'hi'\n", ".py", (2, "Home page."));
        Check("I8", "Python: a function's comment goes above its decorator", deco.Result.StartsWith("# Home page.\n@app.route"), deco.Result);

        var mark = Place("int a;\n", ".c", (1, "// Already marked\\"));
        Check("I9", "markers the model added are dropped; a trailing backslash cannot splice the next line",
            mark.Result == "// Already marked\\.\nint a;\n", mark.Result);

        var xmlDash = Place("<a>\n  <b/>\n</a>\n", ".xml", (2, "x -- y -"));
        Check("I10", "XML: '--' (forbidden in a comment) is softened", xmlDash.Result.Contains("<!-- x — y - -->") && CommentPlanner.Verify(xmlDash.Plan, Lang(".xml")) == null, xmlDash.Result);
    }

    // ───────────────────────── R: round trip ─────────────────────────

    static void RoundTrip()
    {
        Console.WriteLine("\n[R] round trip — strip the comments, get the original back byte for byte");

        var cases = new (string Name, string Ext, byte[] Bytes, int Line)[]
        {
            ("utf-8 + BOM, CRLF", ".cs", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("class A\r\n{\r\n    int x;\r\n}\r\n")).ToArray(), 3),
            ("utf-8, LF, no final newline", ".py", Encoding.UTF8.GetBytes("def f():\n    return 1"), 2),
            ("mixed endings", ".js", Encoding.UTF8.GetBytes("a();\r\nb();\nc();\rd();\n"), 3),
            ("Thai Windows-874 legacy", ".php", TextFileCodec.EncodeForEditor("<?php\n// ภาษาไทย\n$a = 'ค่า';\n", TextFileCodec.Thai, false), 3),
            ("UTF-16 LE", ".xml", TextFileCodec.EncodeForEditor("<a>\r\n  <b/>\r\n</a>\r\n", TextFileCodec.Utf16Le, false), 2),
        };
        int n = 0;
        foreach (var c in cases)
        {
            n++;
            string path = Path.Combine(Scratch, $"rt{n}{c.Ext}");
            File.WriteAllBytes(path, c.Bytes);
            var req = AutoCommentService.Prepare(path, Opt());
            var prev = AutoCommentService.RunAsync(req, new Canned(Answer((c.Line, "ตรวจค่าแล้วคืนผล — checked then returned"))), CancellationToken.None).GetAwaiter().GetResult();
            var d = TextFileCodec.DecodeForEditor(prev.NewBytes);
            byte[] back = TextFileCodec.EncodeForEditor(CommentPlanner.Strip(d.Text, prev.Plan.Inserted), d.CodePage, d.HasBom);
            Check($"R{n}", $"{c.Name}: comment added, strip = original bytes",
                prev.Plan.Count == 1 && back.AsSpan().SequenceEqual(c.Bytes) && !prev.NewBytes.AsSpan().SequenceEqual(c.Bytes)
                && AutoCommentService.RoundTrips(prev.NewBytes, prev.Plan.Inserted, req), d.Text);
        }

        string emoji = Path.Combine(Scratch, "emoji.php");
        File.WriteAllBytes(emoji, TextFileCodec.EncodeForEditor("<?php\n$a = 'ไทย';\n", TextFileCodec.Thai, false));
        var (p6, e6) = Preview(emoji, Opt(), new Canned(Answer((2, "smile 😀")))).GetAwaiter().GetResult();
        Check("R6", "a comment the file's code page cannot hold is refused, not written as '?'", p6 == null && Key(e6) == "edt.acm.err.cannotEncode", Key(e6));

        // Verify is the proof: hand it plans that changed code and it must say so.
        var cs = Lang(".cs");
        string orig = "class A\n{\n    int x = 1;\n}\n";
        var bad1 = new CommentPlan { Original = orig, Result = "class A\n{\n    // c\n    int x = 2;\n}\n", Inserted = new() { 2 }, Placed = new(), Skipped = new() };
        Check("R7", "Verify: a changed code character is caught (strip mismatch)", CommentPlanner.Verify(bad1, cs) == "strip-mismatch");
        var bad2 = new CommentPlan { Original = orig, Result = "class A\n{\n    /* c\n    int x = 1;\n}\n", Inserted = new() { 2 }, Placed = new(), Skipped = new() };
        Check("R8", "Verify: an inserted line that is not a // comment is caught", CommentPlanner.Verify(bad2, cs)?.StartsWith("not-a-comment") == true);
        var cl = Lang(".c");
        string corig = "int a;\nint b;\n";
        var bad3 = new CommentPlan { Original = corig, Result = "// note \\\nint a;\nint b;\n", Inserted = new() { 0 }, Placed = new(), Skipped = new() };
        Check("R9", "Verify: a comment that swallows the next line (C backslash splice) is caught", CommentPlanner.Verify(bad3, cl)?.StartsWith("lexer-state-changed") == true,
            CommentPlanner.Verify(bad3, cl) ?? "passed");
        var py = Lang(".py");
        var bad4 = new CommentPlan { Original = "x = '''a\nb'''\n", Result = "x = '''a\n# c\nb'''\n", Inserted = new() { 1 }, Placed = new(), Skipped = new() };
        Check("R10", "Verify: a 'comment' inside a triple-quoted string is caught", CommentPlanner.Verify(bad4, py)?.StartsWith("inserted-outside-code") == true,
            CommentPlanner.Verify(bad4, py) ?? "passed");
    }

    // ───────────────────────── M: model answers ─────────────────────────

    static async Task ModelAnswers()
    {
        Console.WriteLine("\n[M] model answers — an answer that tries to change code is refused");
        string code = "<?php\nfunction a() {\n    return 1;\n}\n";
        string path = Write("m.php", code);
        byte[] before = File.ReadAllBytes(path);

        async Task Refused(string id, string name, string answer, string key)
        {
            var (p, e) = await Preview(path, Opt(), new Canned(answer));
            Check(id, name, p == null && Key(e) == key && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), Key(e));
        }

        await Refused("M1", "an item with a code-edit field is refused whole",
            """{"comments":[{"line":3,"text":"ok"},{"line":2,"text":"x","replace":"function b() {"}]}""", "edt.acm.refuse.edit");
        await Refused("M2", "a top-level 'code' / new file content is refused", """{"code":"<?php evil();","comments":[]}""", "edt.acm.refuse.edit");
        await Refused("M3", "a comment that closes PHP (?>) and writes code is refused", Answer((3, "x ?> <?php system('rm'); ?>")), "edt.acm.refuse.escape");
        await Refused("M4", "a JS line separator (U+2028 ends a // comment) is refused",
            Answer((3, "fine" + (char)0x2028 + "evil();")), "edt.acm.refuse.escape");
        await Refused("M5", "bidi controls (Trojan Source) are refused", Answer((3, "a" + (char)0x202E + "b")), "edt.acm.refuse.escape");
        await Refused("M6", "prose with no JSON is refused as unreadable", "Sure! Here are the comments: line 3 returns one.", "edt.acm.refuse.format");

        string xp = Write("m.xml", "<a>\n  <b/>\n</a>\n");
        var (px, ex) = await Preview(xp, Opt(), new Canned(Answer((2, "end --> <evil/>"))));
        Check("M7", "XML: a comment that closes itself (-->) and adds markup is refused", px == null && Key(ex) == "edt.acm.refuse.escape", Key(ex));

        var (p8, e8) = await Preview(path, Opt(), new Canned("```json\n{\"comments\":[{\"line\":\"3\",\"text\":\"Always one.\"}]}\n```\nDone."));
        Check("M8", "a fenced answer with prose after it and a string line number is read", p8?.Plan.Count == 1, Key(e8));

        var (p9, e9) = await Preview(path, Opt(), new Canned(Answer((3, "Keeps it simple."), (1, "On the open tag."), (99, "Past the end."),
            (3, "eslint-disable-next-line"), (3, "Keeps it simple."), (4, "Closes the function."))));
        var reasons = p9?.Plan.Skipped.Select(s => s.ReasonKey).ToList() ?? new();
        Check("M9", "bad positions are skipped one by one (not code, out of range, directive, duplicate) — the good ones stay",
            p9 != null && p9.Plan.Count == 2 && reasons.Contains("edt.acm.skip.notCode") && reasons.Contains("edt.acm.skip.range")
            && reasons.Contains("edt.acm.skip.directive") && reasons.Contains("edt.acm.skip.duplicate"),
            $"{Key(e9)} placed={p9?.Plan.Count} skipped={string.Join(",", reasons)}");

        var (p10, e10) = await Preview(Write("s.py", "s = '''\nx\n'''\ny = 1\n"), Opt(), new Canned(Answer((2, "inside the string"))));
        Check("M10", "an answer whose every line is inside a string writes nothing", p10 == null && Key(e10) == "edt.acm.err.noneUsable", Key(e10));
    }

    // ───────────────────────── X: cancel ─────────────────────────

    static async Task Cancel()
    {
        Console.WriteLine("\n[X] cancel — the file stays untouched");
        string path = Write("x.cs", "class A\n{\n    int x;\n}\n");
        byte[] before = File.ReadAllBytes(path);
        var req = AutoCommentService.Prepare(path, Opt());
        var hang = new Hanging();
        using var cts = new CancellationTokenSource();
        var run = AutoCommentService.RunAsync(req, hang, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        Exception? e = null;
        try { await run; } catch (Exception ex) { e = ex; }
        Check("X1", "cancel while the model is thinking: OperationCanceled, file bytes unchanged, no temp file",
            hang.Started && e is OperationCanceledException && File.ReadAllBytes(path).AsSpan().SequenceEqual(before)
            && Directory.GetFiles(Scratch, "*.cluadex-comment.tmp").Length == 0, e?.GetType().Name ?? "no exception");

        // Preview ready, then the user edits the file before confirming: Apply must not overwrite the edit.
        var prev = await AutoCommentService.RunAsync(req, new Canned(Answer((3, "A field."))), CancellationToken.None);
        File.WriteAllText(path, "class A\n{\n    int y;\n}\n");
        Exception? e2 = null;
        try { AutoCommentService.Apply(prev, null, Scratch); } catch (Exception ex) { e2 = ex; }
        Check("X2", "the file changed after the preview: Apply refuses and the user's edit stays",
            Key(e2) == "edt.acm.err.changedOnDisk" && File.ReadAllText(path).Contains("int y;") && !File.ReadAllText(path).Contains("A field."), Key(e2));

        using var pre = new CancellationTokenSource();
        pre.Cancel();
        Exception? e3 = null;
        try { await AutoCommentService.RunAsync(AutoCommentService.Prepare(path, Opt()), new Canned(Answer((3, "late"))), pre.Token); }
        catch (Exception ex) { e3 = ex; }
        Check("X3", "cancelled right after the answer arrives: no preview", e3 is OperationCanceledException, e3?.GetType().Name ?? "none");
    }

    // ───────────────────────── S: scopes ─────────────────────────

    static void Scopes()
    {
        Console.WriteLine("\n[S] scopes — each picks the right lines");

        SortedSet<int> Allowed(string code, string ext, CommentScopeKind s, (int, int)? sel = null, IReadOnlyCollection<int>? changed = null)
        {
            var lang = Lang(ext);
            var lex = CommentLexer.Lex(N(code), lang);
            return new SortedSet<int>(CommentScopes.AllowedLines(lex, lang, s, sel, changed).Select(k => k + 1));
        }
        string Show(IEnumerable<int> s) => string.Join(",", s);

        string cs = """
            using System;
            namespace N;
            public class Svc
            {
                private int _count = 0;

                [Obsolete]
                public int Next(int step)
                {
                    _count += step;
                    return _count;
                }

                public int Twice() => Next(2);

                private const string Name = "svc";
            }
            """;
        var f = Allowed(cs, ".cs", CommentScopeKind.Functions);
        int[] expectF = { 3, 7, 8, 9, 10, 11, 12, 14 };
        Check("S1", "C# functions: class header, method body incl. its attribute, expression-bodied member — not fields / usings / namespace;",
            f.SetEquals(expectF), $"got {Show(f)} want {Show(expectF)}");

        string py = """
            import os

            class Store:
                def __init__(self):
                    self.items = []

                def add(self, x):
                    # keep order
                    self.items.append(x)

            LIMIT = 3
            def top():
                return LIMIT
            """;
        var fp = Allowed(py, ".py", CommentScopeKind.Functions);
        int[] expectP = { 3, 4, 5, 7, 8, 9, 12, 13 };
        Check("S2", "Python functions by indentation: class, methods, a top-level def — not imports / constants",
            fp.SetEquals(expectP), $"got {Show(fp)} want {Show(expectP)}");

        string js = """
            import x from 'y';
            export function a(b) {
              return b;
            }
            const c = (d) => {
              return d * 2;
            };
            const e = 5;
            class K {
              m(z) {
                return z;
              }
            }
            """;
        var fj = Allowed(js, ".js", CommentScopeKind.Functions);
        int[] expectJ = { 2, 3, 4, 5, 6, 7, 9, 10, 11, 12 };
        Check("S3", "JS functions: function declaration, arrow const, class + method — not imports / constants",
            fj.SetEquals(expectJ), $"got {Show(fj)} want {Show(expectJ)}");

        string diff = "diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -3,0 +4,2 @@\n+new1\n+new2\n@@ -10 +12 @@\n-old\n+changed\n@@ -20,3 +23,0 @@\n-gone\n-gone\n-gone\n";
        var changed = CommentScopes.ParseChangedLines(diff);
        Check("S4", "git diff -U0: added + changed lines, pure deletions add nothing", changed.SetEquals(new[] { 4, 5, 12 }), Show(changed));

        string many = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"int v{i} = {i};")) + "\n";
        var ch = Allowed(many, ".cs", CommentScopeKind.Changed, changed: changed);
        Check("S5", "changed scope = exactly the changed lines", ch.SetEquals(new[] { 4, 5, 12 }), Show(ch));
        var untracked = Allowed(many, ".cs", CommentScopeKind.Changed, changed: null);
        Check("S6", "changed scope on an untracked file = every line", untracked.Count == 30, untracked.Count.ToString());

        var sel = Allowed(many, ".cs", CommentScopeKind.Selection, sel: (5, 8));
        Check("S7", "selection scope = the selected lines", sel.SetEquals(new[] { 5, 6, 7, 8 }), Show(sel));

        var file = Allowed("a();\n\nb();\n", ".js", CommentScopeKind.File);
        Check("S8", "file scope = every code line, never a blank one", file.SetEquals(new[] { 1, 3 }), Show(file));

        string hundred = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"x{i} = {i}")) + "\n";
        string kp = Write("kp.py", hundred);
        var ans = Answer(Enumerable.Range(1, 10).Select(i => (i * 5, $"Point {i}.")).ToArray());
        var (prev, e) = Preview(kp, Opt(CommentScopeKind.KeyPoints), new Canned(ans)).GetAwaiter().GetResult();
        Check("S9", "key points: at most lines/25 comments (100 lines → 4), the rest skipped as over the limit",
            prev?.Plan.Count == 4 && prev.Plan.Skipped.Count(s => s.ReasonKey == "edt.acm.skip.limit") == 6, Key(e));

        string fn = Write("fn.cs", cs);
        var (pf, ef) = Preview(fn, Opt(CommentScopeKind.Functions), new Canned(Answer((5, "The counter."), (8, "Advances."), (10, "Adds."))))
            .GetAwaiter().GetResult();
        Check("S10", "functions scope end to end: a field comment is skipped as out of scope; the method's lands above [Obsolete]",
            pf != null && pf.Plan.Count == 2 && pf.Plan.Skipped.Any(s => s.Line == 5 && s.ReasonKey == "edt.acm.skip.scope")
            && Text(pf).Contains("    // Advances.\n    [Obsolete]"), Key(ef) + "\n" + (pf != null ? Text(pf) : ""));

        Exception? none = null;
        try { AutoCommentService.Prepare(Write("plain.py", "x = 1\ny = 2\n"), Opt(CommentScopeKind.Functions)); }
        catch (Exception ex) { none = ex; }
        Check("S11", "functions scope on a file without functions says so before any model call", Key(none) == "edt.acm.err.noFunctions", Key(none));
    }

    // ───────────────────────── A: apply / undo ─────────────────────────

    static async Task ApplyAndUndo()
    {
        Console.WriteLine("\n[A] apply — restore point, atomic write, undo");
        string proj = Path.Combine(Scratch, "proj");
        Directory.CreateDirectory(proj);
        string path = Path.Combine(proj, "Calc.cs");
        string code = "class Calc\r\n{\r\n    int Add(int a, int b)\r\n    {\r\n        return a + b;\r\n    }\r\n}\r\n";
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(code)).ToArray());
        byte[] original = File.ReadAllBytes(path);
        var cp = new CheckpointService(null, Path.Combine(Scratch, "checkpoints"));

        var prev = await AutoCommentService.RunAsync(AutoCommentService.Prepare(path, Opt(CommentScopeKind.Functions, "th")),
            new Canned(Answer((3, "บวกเลขสองตัว"), (5, "คืนผลรวม"))), CancellationToken.None);
        var diff = DiffService.BuildSideBySide(prev.Request.Text, prev.Plan.Result);
        Check("A1", "the preview diff (DiffService) shows only added lines", diff.Added == 2 && diff.Removed == 0, $"+{diff.Added} -{diff.Removed}");

        var applied = AutoCommentService.Apply(prev, cp, proj);
        string now = File.ReadAllText(path);
        Check("A2", "applied: comments on disk with BOM and CRLF kept", now.Contains("    // บวกเลขสองตัว\r\n    int Add") && now.Contains("        // คืนผลรวม\r\n")
            && File.ReadAllBytes(path)[0] == 0xEF && applied.Comments == 2, now);

        var points = cp.Points(proj);
        Check("A3", "a Time Machine restore point holds the original", points.Count == 1 && points[0].Files.Count == 1 && points[0].Note == "before auto-comment",
            $"{points.Count} point(s)");
        var r = cp.RestoreTo(proj, points[0].Id);
        Check("A4", "restoring that point gives the original bytes", r.Ok && File.ReadAllBytes(path).AsSpan().SequenceEqual(original), r.Message);

        // Apply again, then Undo from the editor.
        var prev2 = await AutoCommentService.RunAsync(AutoCommentService.Prepare(path, Opt()), new Canned(Answer((5, "Sum."))), CancellationToken.None);
        var a2 = AutoCommentService.Apply(prev2, null, proj);
        AutoCommentService.Undo(a2);
        Check("A5", "Undo puts the original bytes back", File.ReadAllBytes(path).AsSpan().SequenceEqual(original));

        var prev3 = await AutoCommentService.RunAsync(AutoCommentService.Prepare(path, Opt()), new Canned(Answer((5, "Sum."))), CancellationToken.None);
        var a3 = AutoCommentService.Apply(prev3, null, proj);
        File.AppendAllText(path, "// the user's own edit\r\n");
        Exception? e = null;
        try { AutoCommentService.Undo(a3); } catch (Exception ex) { e = ex; }
        Check("A6", "Undo after the user edited the file refuses (their edit is not lost)",
            Key(e) == "edt.acm.err.changedSinceApply" && File.ReadAllText(path).Contains("the user's own edit"), Key(e));
        Check("A7", "no temp file left behind", Directory.GetFiles(proj, "*.tmp").Length == 0);
        Check("A8", "Apply outside a run leaves no run scope behind", AgentRunScope.Current == null);
    }

    // ───────────────────────── W: the Code Editor ─────────────────────────

    /// <summary>The view model marshals to App.Current's dispatcher: run inside a real WPF Application on an STA thread.</summary>
    static Task<int> OnSta(Func<Task<int>> body)
    {
        var done = new TaskCompletionSource<int>();
        var t = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.InvokeAsync(async () =>
            {
                try { done.SetResult(await body()); }
                catch (Exception ex) { done.SetException(ex); }
                finally { app.Shutdown(); }
            });
            app.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    static async Task<bool> Until(Func<bool> cond, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(20);
        return cond();
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CluadeX", "CluadeX.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static async Task<int> Workbench()
    {
        Console.WriteLine("\n[W] Code Editor — the whole flow through the real view model, and the static XAML checks");
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing).");
            return 3;
        }
        var loc = provider.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("th");
        var fs = provider.GetRequiredService<FileSystemService>();
        settings.Settings.ActiveProvider = AiProviderType.Anthropic;
        var vm = provider.GetRequiredService<CodeEditorViewModel>();
        // After the view model: nothing may later swap in the real ~/.cluadex/checkpoints.
        var cp = new CheckpointService(null, Path.Combine(Scratch, "w-checkpoints"));
        string cpRoot = (string)typeof(CheckpointService).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(CheckpointService.Current)!;
        if (!cpRoot.StartsWith(Scratch)) { Console.WriteLine("ABORT: checkpoints not isolated"); return 3; }

        string proj = Path.Combine(Scratch, "wproj");
        Directory.CreateDirectory(proj);
        fs.WorkingDirectory = proj;
        string path = Path.Combine(proj, "Calc.cs");
        string code = "class Calc\n{\n    int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n";
        File.WriteAllText(path, code);
        byte[] original = File.ReadAllBytes(path);
        await vm.OpenPathAsync(path);
        await Until(() => vm.ActiveTab?.FullPath == path);
        Check("W1", "the file opens in a tab", vm.ActiveTab?.FullPath == path, vm.ActiveTab?.FullPath ?? "no tab");

        vm.SetAutoCommentScopeCommand.Execute("functions");
        vm.SetAutoCommentLanguageCommand.Execute("th");
        Check("W2", "scope + language: saved, checks move", settings.Settings.AutoCommentScope == "functions" && settings.Settings.AutoCommentLanguage == "th"
            && vm.AcmScopeIsFunctions && vm.AcmLangIsThai && !vm.AcmScopeIsFile);
        var choices = vm.AutoCommentModelChoices();
        Check("W3", "model picker: the provider's own model first, then the others it knows",
            choices.Count > 2 && choices[0].Id == "" && choices.Any(c => c.Id == "claude-haiku-5-5"), string.Join(" | ", choices.Select(c => c.Label)));
        vm.SetAutoCommentModel("claude-haiku-5-5");
        Check("W4", "the cost estimate names the picked model and a price", vm.AutoCommentModelId == "claude-haiku-5-5"
            && vm.AutoCommentEstimate.Contains("claude-haiku-5-5") && vm.AutoCommentEstimate.Contains("$"), vm.AutoCommentEstimate);

        var dialogs = new List<string>();
        int answerIndex = 0;
        ChoiceDialog.Override = (t, m, b) => { dialogs.Add(t + "\n" + m); return answerIndex; };
        CodeEditorViewModel.ModelOverride = () => new Canned(Answer((3, "บวกเลขสองตัว"), (5, "คืนผลรวม")));
        try
        {
            await vm.StartAutoCommentAsync(null);
            await Until(() => vm.HasAutoCommentPreview);
            Check("W5", "before the run: a confirm dialog with the cost (nothing spent without it)",
                dialogs.Count == 1 && dialogs[0].Contains("$") && dialogs[0].Contains("claude-haiku-5-5"), string.Join("\n---\n", dialogs));
            Check("W6", "preview: the diff panel shows 2 added lines, captions now / with comments, the file is not written yet",
                vm.HasAutoCommentPreview && vm.IsDiffViewActive && vm.DiffRows.Count(r => r.Kind == DiffRowKind.Added) == 2
                && vm.DiffLeftCaption == "ตอนนี้" && vm.DiffRightCaption == "หลังใส่คอมเมนต์" && File.ReadAllBytes(path).AsSpan().SequenceEqual(original)
                && !vm.CanStartAutoComment, $"{vm.StatusMessage} | rows {vm.DiffRows.Count}");

            vm.ApplyAutoCommentCommand.Execute(null);
            await Until(() => vm.CanUndoAutoComment && !vm.IsAutoCommenting && vm.ActiveTab?.Content.Contains("// คืนผลรวม") == true);
            Check("W7", "Apply: comments on disk, the tab reloaded, a restore point kept, the preview closed",
                File.ReadAllText(path).Contains("    // บวกเลขสองตัว\n    int Add") && vm.ActiveTab?.Content.Contains("// คืนผลรวม") == true
                && cp.Points(proj).Count == 1 && !vm.HasAutoCommentPreview && !vm.IsDiffViewActive, vm.StatusMessage);

            vm.UndoAutoCommentCommand.Execute(null);
            await Until(() => !vm.CanUndoAutoComment && vm.ActiveTab?.Content == code);
            Check("W8", "Undo: the original bytes, the tab reloaded", File.ReadAllBytes(path).AsSpan().SequenceEqual(original)
                && vm.ActiveTab?.Content == code, vm.StatusMessage);

            // Discard by closing the diff.
            await vm.StartAutoCommentAsync(null);
            await Until(() => vm.HasAutoCommentPreview);
            vm.CloseDiffCommand.Execute(null);
            Check("W9", "closing the preview discards it: nothing written", !vm.HasAutoCommentPreview && !vm.IsDiffViewActive
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(original) && vm.CanStartAutoComment);

            // Cancel while the model is thinking.
            var hang = new Hanging();
            CodeEditorViewModel.ModelOverride = () => hang;
            var run = vm.StartAutoCommentAsync(null);
            await Until(() => hang.Started);
            vm.CancelAutoCommentCommand.Execute(null);
            await run;
            Check("W10", "Cancel mid-run: the status says untouched, and it is", vm.StatusMessage == loc.T("edt.acm.cancelled")
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(original) && !vm.IsAutoCommenting, vm.StatusMessage);

            // The confirm dialog's Cancel spends nothing.
            answerIndex = 1;
            var never = new Hanging();
            CodeEditorViewModel.ModelOverride = () => never;
            await vm.StartAutoCommentAsync(null);
            Check("W11", "Cancel in the cost dialog: the model is never asked", !never.Started && !vm.IsAutoCommenting);
            answerIndex = 0;

            // An unsaved tab is never overwritten.
            dialogs.Clear();
            vm.ActiveTab!.Content = code + "// typing\n";
            await vm.StartAutoCommentAsync(null);
            Check("W12", "unsaved edits: asks to save first and runs nothing", dialogs.Count == 1 && dialogs[0].Contains("บันทึก")
                && !vm.HasAutoCommentPreview && File.ReadAllBytes(path).AsSpan().SequenceEqual(original), string.Join("\n", dialogs));
        }
        finally
        {
            ChoiceDialog.Override = null;
            CodeEditorViewModel.ModelOverride = null;
        }

        await ChatTool(provider, vm, cp, loc, proj);

        // Static checks: what compiles but renders blank.
        string repo = RepoRoot();
        string xaml = File.ReadAllText(Path.Combine(repo, "CluadeX", "Views", "CodeEditorView.xaml"));
        int a = xaml.IndexOf("<Button x:Name=\"TbAutoComment\"", StringComparison.Ordinal);
        int b = a < 0 ? -1 : xaml.IndexOf("</Button.ContextMenu>", a, StringComparison.Ordinal);
        int c = xaml.IndexOf("<!-- Auto-comment preview", StringComparison.Ordinal);
        int d = c < 0 ? -1 : xaml.IndexOf("ItemsSource=\"{Binding DiffRows}\"", c, StringComparison.Ordinal);
        string block = a >= 0 && b > a && c > 0 && d > c ? xaml[a..b] + xaml[c..d] : "";
        Check("W13", "the auto-comment XAML is where the checks expect it", block.Length > 0);
        string code1 = File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "CodeEditorViewModel.AutoComment.cs"));
        string code2 = string.Concat(Directory.GetFiles(Path.Combine(repo, "CluadeX", "Services", "AutoComment"), "*.cs").Select(File.ReadAllText));
        var keys = Regex.Matches(block, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(code1 + code2, @"""(edt\.acm\.[\w.]+)""").Select(m => m.Groups[1].Value))
            .Concat(new[] { "functions", "changed", "key", "file", "selection" }.Select(k => "edt.acm.scope." + k))
            .Concat(new[] { "th", "en", "match" }.Select(k => "edt.acm.lang." + k))
            .Where(k => !k.EndsWith('.')).Distinct().ToList();
        loc.SetLanguage("th");
        var thai = new Regex("[" + (char)0x0E00 + "-" + (char)0x0E7F + "]");
        var noThai = keys.Where(k => loc.T(k) == k || !thai.IsMatch(loc.T(k))).ToList();
        loc.SetLanguage("en");
        var noEnglish = keys.Where(k => loc.T(k) == k).ToList();
        Check("W14", $"every auto-comment Loc key has Thai and English ({keys.Count} keys)", keys.Count > 60 && noThai.Count == 0 && noEnglish.Count == 0,
            "no Thai: " + string.Join(", ", noThai) + " | missing: " + string.Join(", ", noEnglish));
        var vmType = typeof(CodeEditorViewModel);
        var bindings = Regex.Matches(block, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value.Split('.')[0]).Distinct().ToList();
        var bad = bindings.Where(p => vmType.GetProperty(p) == null).ToList();
        var captions = new[] { "DiffLeftCaption", "DiffRightCaption" }.Where(p => !xaml.Contains("{Binding " + p + "}") || vmType.GetProperty(p) == null).ToList();
        Check("W15", $"every auto-comment {{Binding}} names a real view-model property ({bindings.Count} paths)", bindings.Count >= 15 && bad.Count == 0 && captions.Count == 0,
            string.Join(", ", bad.Concat(captions)));
        string resources = xaml + File.ReadAllText(Path.Combine(repo, "CluadeX", "Resources", "Theme.xaml")) + File.ReadAllText(Path.Combine(repo, "CluadeX", "App.xaml"));
        string theme = File.ReadAllText(Path.Combine(repo, "CluadeX", "Resources", "Themes", "Midnight.xaml")) + resources;
        var missingStatic = Regex.Matches(block, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        var missingDyn = Regex.Matches(block, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !theme.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("W16", "every {StaticResource} / {DynamicResource} in the auto-comment XAML exists", missingStatic.Count == 0 && missingDyn.Count == 0,
            string.Join(", ", missingStatic.Concat(missingDyn)));
        var handlers = Regex.Matches(block, @"Click=""(\w+)""").Select(m => m.Groups[1].Value).Distinct()
            .Where(h => typeof(CluadeX.Views.CodeEditorView).GetMethod(h, BindingFlags.NonPublic | BindingFlags.Instance) == null).ToList();
        Check("W17", "every Click handler exists on CodeEditorView", handlers.Count == 0, string.Join(", ", handlers));
        return 0;
    }

    // ───────────────────────── T: the chat tool (add_code_comments, /comment) ─────────────────────────

    sealed class Counted(string answer, Action? during = null) : IAutoCommentModel
    {
        public int Calls;
        public Task<string> AnswerAsync(string s, string u, CancellationToken ct) { Calls++; during?.Invoke(); return Task.FromResult(answer); }
    }

    static Task<ToolResult> Tool(AgentToolService svc, params (string K, string V)[] args) =>
        svc.ExecuteToolAsync(new ToolCall
        {
            Type = svc.ResolveToolTypePublic("add_code_comments") ?? throw new Exception("add_code_comments did not resolve"),
            ToolName = "add_code_comments",
            Arguments = args.ToDictionary(a => a.K, a => a.V),
        }, CancellationToken.None);

    static string Seen(ToolResult r) => r.Success ? r.Output : r.Error + " " + r.Output;

    /// <summary>The chat entry point, through the real AgentToolService the agent loop calls (same permission gate, same
    /// module dispatch), with a mocked model. Runs inside <see cref="Workbench"/>: it needs the editor view model.</summary>
    static async Task ChatTool(ServiceProvider provider, CodeEditorViewModel vm, CheckpointService cp, LocalizationService loc, string proj)
    {
        Console.WriteLine("\n[T] chat — add_code_comments through AgentToolService, and the /comment skill");
        var svc = provider.GetRequiredService<AgentToolService>();
        var settings = provider.GetRequiredService<SettingsService>();
        loc.SetLanguage("th");

        var schema = svc.BuildNativeToolSchemas().FirstOrDefault(t => t.Name == "add_code_comments");
        string props = schema == null ? "" : schema.InputSchema.GetProperty("properties").GetRawText();
        Check("T1", "offered to the model: a native schema with path / scope / lines / language / model",
            schema != null && svc.ResolveToolTypePublic("add_code_comments") == ToolType.AddCodeComments
            && new[] { "\"path\"", "\"scope\"", "\"start_line\"", "\"end_line\"", "\"language\"", "\"model\"", "\"selection\"", "\"match\"" }.All(props.Contains),
            props);
        var module = svc.ToolModules.OfType<AutoCommentToolModule>().SingleOrDefault();
        string section = module?.GetPromptSection(new CluadeX.Services.Tools.PromptSectionContext { OfferedToolNames = new[] { "add_code_comments" } }) ?? "";
        Check("T2", "a system-prompt line tells the model to comment through the tool, never edit_file",
            section.Contains("add_code_comments") && section.Contains("edit_file"), section);

        // The permission prompt: recorded, with what the model had been asked at that moment.
        var prompts = new List<(PermissionRequestInfo Info, int CallsBefore)>();
        bool allow = true;
        Counted? current = null;
        Func<PermissionRequestInfo, Task<bool>> gate = info => { prompts.Add((info, current?.Calls ?? 0)); return Task.FromResult(allow); };
        svc.OnPermissionRequired += gate;
        string answer = Answer((3, "บวกเลขสองตัว"), (5, "คืนผลรวม"));
        AutoCommentToolModule.ModelOverride = () => current!;
        ChoiceDialog.Override = (t, m, b) => 0;
        CodeEditorViewModel.ModelOverride = () => new Canned(answer);
        try
        {
            // ── the same bytes as the editor path ──
            string code = "class Calc\r\n{\r\n    int Add(int a, int b)\r\n    {\r\n        return a + b;\r\n    }\r\n}\r\n";
            byte[] orig = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(code)).ToArray();
            string viaEditor = Path.Combine(proj, "Same1.cs"), viaChat = Path.Combine(proj, "Same2.cs");
            File.WriteAllBytes(viaEditor, orig);
            File.WriteAllBytes(viaChat, orig);
            await vm.OpenPathAsync(viaEditor);
            await Until(() => vm.ActiveTab?.FullPath == viaEditor);
            vm.SetAutoCommentScopeCommand.Execute("functions");
            vm.SetAutoCommentLanguageCommand.Execute("th");
            await vm.StartAutoCommentAsync(null);
            await Until(() => vm.HasAutoCommentPreview);
            // W7's Apply may still be in its after-write steps (git refresh, build check): the button is disabled till then.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Until(() => vm.ApplyAutoCommentCommand.CanExecute(null), 120_000);
            Console.WriteLine($"    (the earlier Apply finished its after-write steps {sw.ElapsedMilliseconds} ms later)");
            vm.ApplyAutoCommentCommand.Execute(null);
            await Until(() => vm.CanUndoAutoComment && !vm.IsAutoCommenting && !vm.HasAutoCommentPreview);

            int pointsBefore = cp.Points(proj).Count;
            current = new Counted(answer);
            var r = await Tool(svc, ("path", "Same2.cs"), ("scope", "functions"), ("language", "th"));
            byte[] a = File.ReadAllBytes(viaEditor), b = File.ReadAllBytes(viaChat);
            Check("T3", "the tool writes byte for byte what the Code Editor writes (BOM, CRLF, Thai)",
                r.Success && !a.AsSpan().SequenceEqual(orig) && a.AsSpan().SequenceEqual(b),
                $"editor: {(a.AsSpan().SequenceEqual(orig) ? "NOT written" : "written")} · status: {vm.StatusMessage}\n" + Seen(r));
            Check("T4", "the permission prompt came first: the tool's name, the file, the cost with the model — before the model was asked",
                prompts.Count == 1 && prompts[0].CallsBefore == 0 && prompts[0].Info.ToolName == "add_code_comments"
                && prompts[0].Info.Detail.Contains("Same2.cs") && prompts[0].Info.Detail.Contains("$") && prompts[0].Info.Detail.Contains("claude-haiku-5-5")
                && prompts[0].Info.Detail.Contains("ทุกฟังก์ชัน") && current.Calls == 1,
                string.Join("\n---\n", prompts.Select(p => p.Info.Detail)));
            Check("T5", "the result tells the model what went in, and the chat row gets a diff of added lines only",
                r.Output.Contains("Added 2 comment(s)") && r.Output.Contains("// คืนผลรวม") && r.Diff is { Added: 2, Removed: 0 }
                && r.FilePath == "Same2.cs" && r.Summary.Contains("2"), Seen(r));
            Check("T6", "outside a chat run: a Time Machine restore point of its own holds the original",
                cp.Points(proj).Count == pointsBefore + 1 && cp.Points(proj)[0].Files.Any(f => f.File == viaChat && f.Note == "before auto-comment"),
                $"{pointsBefore} → {cp.Points(proj).Count}");

            // ── denied: nothing spent, nothing written ──
            File.WriteAllBytes(viaChat, orig);
            prompts.Clear();
            allow = false;
            current = new Counted(answer);
            r = await Tool(svc, ("path", "Same2.cs"), ("scope", "file"), ("language", "en"));
            Check("T7", "the user says No: the model is never asked and the file is untouched",
                !r.Success && prompts.Count == 1 && current.Calls == 0 && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(orig), Seen(r));
            allow = true;

            // ── a code-changing answer is refused whole ──
            int pts = cp.Points(proj).Count;
            current = new Counted("{\"comments\":[{\"line\":3,\"text\":\"adds\",\"new_code\":\"int Add(int a, int b) => a - b;\"}]}");
            r = await Tool(svc, ("path", "Same2.cs"), ("scope", "file"));
            Check("T8", "an answer that tries to change code is refused: nothing written, no restore point",
                !r.Success && current.Calls == 1 && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(orig) && cp.Points(proj).Count == pts
                && r.Error.Contains(loc.T("edt.acm.failed").Split('{')[0].Trim()), Seen(r));
            current = new Counted(Answer((3, "fine" + (char)0x2028 + "int y = 0;")));
            r = await Tool(svc, ("path", "Same2.cs"), ("scope", "file"));
            Check("T9", "an answer that breaks out of the comment line (U+2028) to slip code in is refused too",
                !r.Success && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(orig) && cp.Points(proj).Count == pts, Seen(r));

            // ── the file changes while the model is thinking ──
            current = new Counted(answer, () => File.AppendAllText(viaChat, "// the user's own edit\r\n"));
            r = await Tool(svc, ("path", "Same2.cs"), ("scope", "functions"));
            string now = File.ReadAllText(viaChat);
            Check("T10", "the file changed during the run: refused, the user's edit kept, no comment written",
                !r.Success && now.EndsWith("// the user's own edit\r\n") && !now.Contains("คืนผลรวม") && cp.Points(proj).Count == pts, Seen(r));
            File.WriteAllBytes(viaChat, orig);

            // ── inside a chat run: the restore point is the run's step ──
            var run = new AgentRunScope("chat-autocomment", proj) { Step = 4 };
            AgentRunScope.Enter(run);
            try
            {
                current = new Counted(answer);
                r = await Tool(svc, ("path", Path.Combine(proj, "Same2.cs")), ("scope", "functions"), ("language", "th"));
            }
            finally { AgentRunScope.Enter(null); }
            var step = cp.Points(proj).FirstOrDefault(p => p.Id == $"{run.TurnId}-004");
            Check("T11", "in a chat run the restore point lands in that run's step (the chat's rewind finds it)",
                r.Success && step != null && step.Files.Any(f => f.File == viaChat && f.Note == "before auto-comment")
                && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(b), Seen(r) + " | points: " + string.Join(", ", cp.Points(proj).Select(p => p.Id)));
            var restored = step == null ? (false, 0, "no point") : cp.RestoreTo(proj, step.Id);
            Check("T12", "restoring that step gives the original bytes", restored.Item1 && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(orig), restored.Item3);

            // ── selection by line range ──
            current = new Counted(Answer((1, "outside"), (5, "inside")));
            r = await Tool(svc, ("path", "Same2.cs"), ("start_line", "4"), ("end_line", "6"), ("language", "en"));
            now = File.ReadAllText(viaChat);
            Check("T13", "start_line / end_line alone = the selection scope: only lines inside it get a comment",
                r.Success && now.Contains("        // inside\r\n") && !now.Contains("outside") && r.Output.Contains("scope selection"), Seen(r) + "\n" + now);
            File.WriteAllBytes(viaChat, orig);

            // ── the open file, and an unsaved tab ──
            await vm.OpenPathAsync(viaChat);
            await Until(() => vm.ActiveTab?.FullPath == viaChat && vm.ActiveTab.Content.Length > 0);
            current = new Counted(answer);
            r = await Tool(svc, ("scope", "functions"), ("language", "th"));
            Check("T14", "no path: the file open in the Code Editor is the one commented", r.Success && File.ReadAllText(viaChat).Contains("// คืนผลรวม"), Seen(r));
            await Until(() => vm.ActiveTab?.Content.Contains("คืนผลรวม") == true);
            byte[] beforeTyping = File.ReadAllBytes(viaChat);
            vm.ActiveTab!.Content += "// typing\n";
            current = new Counted(answer);
            r = await Tool(svc, ("path", "Same2.cs"), ("scope", "file"));
            Check("T15", "unsaved typing in the editor: refused before the model is asked, the file not written",
                !r.Success && current.Calls == 0 && File.ReadAllBytes(viaChat).AsSpan().SequenceEqual(beforeTyping)
                && r.Error.Contains("บันทึก"), Seen(r));

            // ── bad arguments fail without a prompt or a model call ──
            prompts.Clear();
            current = new Counted(answer);
            var bad1 = await Tool(svc, ("path", "Same1.cs"), ("scope", "everything-ish"));
            var bad2 = await Tool(svc, ("path", Path.Combine(Scratch, "p.cs")));
            var bad3 = await Tool(svc, ("path", "Same1.cs"), ("scope", "selection"));
            Check("T16", "a wrong scope, a file outside the project, a selection without lines: refused, nobody asked, nothing spent",
                !bad1.Success && !bad2.Success && !bad3.Success && prompts.Count == 0 && current.Calls == 0,
                Seen(bad1) + " | " + Seen(bad2) + " | " + Seen(bad3));

            // ── a paid run is asked about even with the permission system switched off ──
            bool permOn = settings.Settings.Features.PermissionSystem;
            settings.Settings.Features.PermissionSystem = false;
            try
            {
                File.WriteAllBytes(viaEditor, orig);
                prompts.Clear();
                current = new Counted(answer);
                r = await Tool(svc, ("path", "Same1.cs"), ("scope", "functions"));
                Check("T17", "permission system off: a paid run still shows its cost first", r.Success && prompts.Count == 1, Seen(r));
            }
            finally { settings.Settings.Features.PermissionSystem = permOn; }

            // ── /comment ──
            var skills = provider.GetRequiredService<SkillService>();
            var skill = skills.GetSkillByName("comment");
            Check("T18", "/comment is a built-in skill a user can type, limited to add_code_comments (+ finding the file)",
                skill is { UserInvocable: true, IsBuiltIn: true } && skill.AllowedTools.Contains("add_code_comments")
                && !skill.AllowedTools.Any(t => t is "edit_file" or "write_file" or "multi_edit" or "run_command")
                && skill.PromptContent.Contains("WITHOUT a path"),
                skill == null ? "missing" : string.Join(",", skill.AllowedTools));
            svc.ActiveSkillAllowedTools = skill?.AllowedTools;
            try
            {
                var edit = await svc.ExecuteToolAsync(new ToolCall
                {
                    Type = ToolType.EditFile, ToolName = "edit_file",
                    Arguments = new() { ["path"] = "Same1.cs", ["old_str"] = "a + b", ["new_str"] = "a - b" },
                }, CancellationToken.None);
                File.WriteAllBytes(viaEditor, orig);
                current = new Counted(answer);
                r = await Tool(svc, ("path", "Same1.cs"), ("scope", "functions"));
                Check("T19", "under /comment the chat model cannot edit code itself, but add_code_comments runs",
                    !edit.Success && edit.Error.Contains("not allowed by the current skill") && File.ReadAllText(viaEditor).Contains("a + b")
                    && r.Success && File.ReadAllText(viaEditor).Contains("// คืนผลรวม"), Seen(edit) + " | " + Seen(r));
            }
            finally { svc.ActiveSkillAllowedTools = null; }

            // ── only what changed: the tool reads git diff -U0 HEAD itself ──
            static void GitIn(string dir, string args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(20000);
            }
            File.WriteAllBytes(viaEditor, orig);
            foreach (var g in new[] { "init -q", "config user.email t@t", "config user.name t", "add Same1.cs", "commit -q -m init" }) GitIn(proj, g);
            File.WriteAllBytes(viaEditor, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(code.Replace("a + b", "b + a"))).ToArray());
            current = new Counted(Answer((3, "the declaration"), (5, "operands swapped on purpose")));
            r = await Tool(svc, ("path", "Same1.cs"), ("scope", "changed"), ("language", "en"));
            now = File.ReadAllText(viaEditor);
            Check("T20", "scope=changed: only the line that differs from git HEAD gets a comment",
                r.Success && now.Contains("        // operands swapped on purpose\r\n        return b + a;") && !now.Contains("the declaration")
                && r.Output.Contains("scope changed") && r.Output.Contains("Skipped 1"), Seen(r) + "\n" + now);
        }
        finally
        {
            svc.OnPermissionRequired -= gate;
            AutoCommentToolModule.ModelOverride = null;
            CodeEditorViewModel.ModelOverride = null;
            ChoiceDialog.Override = null;
        }
    }

    // ───────────────────────── P: prompt / provider ─────────────────────────

    sealed class FakeProvider : IAiProvider
    {
        public string? ToolChoice, ModelSeen, SchemaName; public bool Generated;
        public string ProviderId => "fake"; public string DisplayName => "Fake"; public bool IsReady => true; public bool IsLoading => false;
        public string StatusMessage => ""; public bool Native = true;
        public event Action<string>? OnStatusChanged { add { } remove { } }
        public event Action<bool>? OnLoadingChanged { add { } remove { } }
        public event Action<string>? OnError { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<string> ChatAsync(List<ChatMessage> h, string u, string? s = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public Task<string> GenerateAsync(List<ChatMessage> h, string u, string? s = null, CancellationToken ct = default)
        { Generated = true; ModelSeen = CluadeX.Services.Agents.SubAgentModel.Override; return Task.FromResult(Answer((1, "from text"))); }
        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, ""));
        public bool SupportsNativeToolUse => Native;
        public Task<NativeToolResponse> ChatWithToolsAsync(List<NativeMessage> m, string s, List<ToolSchema> tools, Action<string>? d = null,
            CancellationToken ct = default, string? toolChoice = null)
        {
            ToolChoice = toolChoice; SchemaName = tools.FirstOrDefault()?.Name; ModelSeen = CluadeX.Services.Agents.SubAgentModel.Override;
            using var doc = JsonDocument.Parse(Answer((1, "from the tool")));
            return Task.FromResult(new NativeToolResponse
            {
                StopReason = "tool_use",
                ToolCalls = { new NativeToolCall { Id = "t1", Name = toolChoice ?? "", Input = doc.RootElement.Clone() } },
            });
        }
        public void Dispose() { }
    }

    static async Task PromptAndProvider()
    {
        Console.WriteLine("\n[P] prompt, cost, provider — structured output, the chosen model");
        string path = Write("p.cs", "class A\n{\n    int x;\n}\n");
        var req = AutoCommentService.Prepare(path, Opt(CommentScopeKind.File, "th"));
        Check("P1", "the prompt numbers the code, lists the allowed lines and asks for Thai",
            req.UserPrompt.Contains("3|     int x;") && req.UserPrompt.Contains("Allowed lines: 1-4") && req.UserPrompt.Contains("Comment language: Thai"),
            req.UserPrompt);
        var api = AutoCommentService.Estimate(req, "claude-sonnet-5-5", local: false);
        var local = AutoCommentService.Estimate(req, "claude-sonnet-5-5", local: true);
        Check("P2", "cost estimate: tokens in/out and a price for an API model, free for a local one",
            api.InputTokens > 100 && api.OutputTokens > 0 && api.Usd > 0 && local.Usd == 0, $"{api} / {local}");

        var fake = new FakeProvider();
        string a = await new ProviderCommentModel(fake, "claude-haiku-5-5").AnswerAsync("s", "u", CancellationToken.None);
        Check("P3", "native provider: forced submit_comments tool call (structured output) on the chosen model",
            fake.ToolChoice == "submit_comments" && fake.SchemaName == "submit_comments" && fake.ModelSeen == "claude-haiku-5-5" && a.Contains("from the tool"),
            $"{fake.ToolChoice} {fake.SchemaName} {fake.ModelSeen} {a}");
        Check("P4", "the model override does not leak out of the call", CluadeX.Services.Agents.SubAgentModel.Override == null);

        var textOnly = new FakeProvider { Native = false };
        string b = await new ProviderCommentModel(textOnly, null).AnswerAsync("s", "u", CancellationToken.None);
        Check("P5", "a provider without tool use answers JSON as text", textOnly.Generated && b.Contains("from text") && textOnly.ModelSeen == null);

        // A large file sends only the lines in scope plus context, and says what it left out.
        string big = string.Join("\n", Enumerable.Range(1, 9000).Select(i => $"int value{i:D5} = {i}; // padding padding padding")) + "\n";
        string bp = Write("big.cs", big);
        var breq = AutoCommentService.Prepare(bp, Opt(CommentScopeKind.Selection) with { Selection = (4500, 4502) });
        Check("P6", "a big file: only the selection ± context goes in the prompt",
            breq.UserPrompt.Length < 10_000 && breq.UserPrompt.Contains("4501|") && breq.UserPrompt.Contains("not shown"), breq.UserPrompt.Length.ToString());
    }
}
