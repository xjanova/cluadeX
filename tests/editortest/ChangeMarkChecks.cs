using System.IO;
using System.Reflection;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EditorTest;

/// <summary>
/// CM*  the editor's change marks (owner 2026-11-09: "ป้ายแถบสีที่มีการเปลี่ยนแปลง … มี compare ก่อนหลังได้ … ถ้าคุณแก้โค๊ดเอง ก็ต้อง
///      ระบายไว้อีกสี ว่า user แก้ และมาร์กคอมเม้นต์ไว้เอง … และสามารถนำมาค้นหาภายหลังได้"):
///      classification into AI-added / AI-modified / AI-deleted / user-edited (ChangeMarks.Build), the [user-edit] marker per
///      language and never inside strings, comments or files without comments (UserEditMarker), the per-chat baseline that
///      survives the user's save and a reopen (CheckpointService.BeforeSession + UserEditStore), the real save path of the
///      view model, and the search that lists every user-edited spot in a project.
/// </summary>
internal static partial class Program
{
    static readonly DateTime MarkDay = new(2026, 11, 9);
    const string Day = "[user-edit 2026-11-09]";

    static string Lines(params string[] l) => string.Join("\n", l) + "\n";

    static async Task ChangeMarkChecks(IServiceProvider provider)
    {
        Console.WriteLine("\n── change marks: AI vs user, [user-edit] comments, per-chat baseline ──");
        var priorStore = UserEditStore.Current;
        UserEditStore.Current = new UserEditStore(Path.Combine(Scratch, "user-edits"));
        try
        {
            await Case("CM1", () => { ClassifyChecks(); return Task.CompletedTask; });
            await Case("CM2", () => { MarkerChecks(); return Task.CompletedTask; });
            await Case("CM3", () => BaselineChecks());
            await Case("CM4", () => SavePathChecks(provider));
            await Case("CM5", () => { SearchChecks(); return Task.CompletedTask; });
            await Case("CM6", () => { KeyAndNoteChecks(); return Task.CompletedTask; });
        }
        finally { UserEditStore.Current = priorStore; }
    }

    static string Show(IEnumerable<AiChangeHunk> hunks) =>
        string.Join(" | ", hunks.Select(h => $"{(h.ByUser ? "USER" : "AI")}-{h.Kind}@{h.StartLine}+{h.LineCount}{(h.Partial ? "p" : "")}"));

    // ───────────────────────── CM1: classification ─────────────────────────

    static void ClassifyChecks()
    {
        // Before the chat: 12 lines. The AI changes line 2, adds one after line 6, deletes line 10 (so the file the AI left is
        // "disk"). Then the user types into line 4 (not saved yet).
        var before = Enumerable.Range(1, 12).Select(i => $"line{i}();").ToArray();
        var ai = before.ToList();
        ai[1] = "line2_ai();";
        ai.Insert(6, "added_by_ai();");
        ai.RemoveAt(10);   // line10 (index 9 → 10 after the insert)
        string baseline = Lines(before), disk = Lines(ai.ToArray());
        var typed = ai.ToList();
        typed[3] = "line4_user();";
        string now = Lines(typed.ToArray());

        var h = ChangeMarks.Build(baseline, disk, now, null);
        bool aiMod = h.Any(x => !x.ByUser && x.Kind == AiChangeKind.Modified && x.StartLine == 2 && x.LineCount == 1);
        bool aiAdd = h.Any(x => !x.ByUser && x.Kind == AiChangeKind.Added && x.StartLine == 7 && x.LineCount == 1);
        bool aiDel = h.Any(x => !x.ByUser && x.Kind == AiChangeKind.Removed && x.LineCount == 0 && x.OldLines.SequenceEqual(new[] { "line10();" }));
        bool user = h.Any(x => x.ByUser && x.StartLine == 4 && x.LineCount == 1);
        Check("CM1", "AI-modified (line 2), AI-added (line 7), AI-deleted (line10) and the user's unsaved edit (line 4) are four separate marks",
            aiMod && aiAdd && aiDel && user && h.Count == 4, Show(h));
        var (nAi, nUser) = ChangeMarks.Count(h);
        Check("CM1b", "…counted 3 AI / 1 user for the status bar", nAi == 3 && nUser == 1, $"{nAi}/{nUser}");

        // The user edits the line the AI added: that line becomes the user's (no AI mark left on it).
        var typed2 = ai.ToList();
        typed2[6] = "added_by_ai(); extra_by_user();";
        var h2 = ChangeMarks.Build(baseline, disk, Lines(typed2.ToArray()), null);
        Check("CM1c", "a line the user edits inside the AI's change takes the user's colour; the rest of that AI change is still the AI's",
            h2.Any(x => x.ByUser && x.StartLine == 7) && !h2.Any(x => !x.ByUser && x.StartLine == 7 && x.LineCount > 0) && h2.Count(x => !x.ByUser) == 2, Show(h2));

        // A split AI change cannot be reverted alone; a whole one can.
        var block = before.ToList();
        block[4] = "five_ai();"; block[5] = "six_ai();"; block[6] = "seven_ai();";
        var blockUser = block.ToList(); blockUser[5] = "six_user();";
        var h3 = ChangeMarks.Build(baseline, Lines(block.ToArray()), Lines(blockUser.ToArray()), null);
        Check("CM1d", "an AI change with a user line in the middle splits into AI / user / AI, and the AI parts offer no Revert",
            Show(h3) == "AI-Modified@5+1p | USER-Modified@6+1 | AI-Modified@7+1p" && h3.Where(x => !x.ByUser).All(x => !x.CanRevert),
            Show(h3));
        var whole = ChangeMarks.Build(baseline, Lines(block.ToArray()), Lines(block.ToArray()), null);
        Check("CM1e", "…while the untouched AI change is one hunk that can be put back", whole.Count == 1 && whole[0].CanRevert && !whole[0].ByUser, Show(whole));

        // Saved edits are remembered: a reopen (disk == now) still shows them as the user's, the deletion too.
        var savedLines = ai.ToList();
        savedLines[3] = "line4_user();";
        savedLines.RemoveAt(savedLines.Count - 1);   // the user deletes "line12();"
        string saved = Lines(savedLines.ToArray());
        var rec = ChangeMarks.Record(null, @"C:\p\a.cs", disk, saved, MarkDay);
        var h4 = ChangeMarks.Build(baseline, saved, saved, rec);
        Check("CM1f", "after a save and a reopen the user's line and the user's deletion are still the user's (UserEditRecord), the AI's still the AI's",
            h4.Any(x => x.ByUser && x.StartLine == 4 && x.LineCount == 1) && h4.Any(x => x.ByUser && x.LineCount == 0)
            && h4.Count(x => !x.ByUser) == 3 && rec.Lines.Count == 1 && rec.Removed.Count == 1, Show(h4) + $" · rec {rec.Lines.Count}/{rec.Removed.Count}");

        // No chat touched the file: only the user's marks (marker comment, record, unsaved typing).
        string plain = Lines("a();", "b(); // [user-edit 2026-11-01]", "c();", "d();", "e();");
        var h5 = ChangeMarks.Build(null, plain, plain.Replace("e();", "e2();"), null);
        Check("CM1g", "without a chat baseline, a line carrying [user-edit] and unsaved typing are the user's — nothing is the AI's",
            Show(h5) == "USER-Modified@2+1 | USER-Modified@5+1", Show(h5));

        // Taking a marker off by hand is not an edit of the code.
        string marked = Lines("x();", "y(); // [user-edit 2026-11-01]");
        Check("CM1h", "removing a [user-edit] comment by hand is not a user edit (no mark, no new marker on save)",
            ChangeMarks.Build(null, marked, Lines("x();", "y();"), null).Count == 0 && ChangeMarks.EditedRuns(marked, Lines("x();", "y();")).Count == 0,
            Show(ChangeMarks.Build(null, marked, Lines("x();", "y();"), null)));

        // Ten remembered braces mark ten braces — not every brace in the file.
        var many = new List<string>();
        for (int i = 0; i < 40; i++) { many.Add($"void F{i}() {{"); many.Add("}"); }
        var recBrace = new[] { new UserEditLine("}", 2, "2026-11-09") };
        var at = ChangeMarks.Match(recBrace, many.ToArray());
        Check("CM1i", "a remembered '}' matches the one brace nearest where it was, not the other 39", at.SequenceEqual(new[] { 2 }), string.Join(",", at));
        var far = ChangeMarks.Match(new[] { new UserEditLine("}", 500, "2026-11-09") }, many.ToArray());
        Check("CM1j", "…and a trivial line far from where it was matches nothing", far.Count == 0, string.Join(",", far));
    }

    // ───────────────────────── CM2: the marker per language ─────────────────────────

    static string Mark(string file, string text, int line, int count = 1) =>
        UserEditMarker.AddMarkers(text, file, new[] { (line, count) }, MarkDay).Text;

    static void MarkerChecks()
    {
        var cases = new (string File, string Line, string Expect)[]
        {
            ("a.cs", "var a = 1;", "var a = 1; // " + Day),
            ("a.ts", "const a = 1;", "const a = 1; // " + Day),
            ("a.go", "x := 1", "x := 1 // " + Day),
            ("a.rs", "fn f<'a>(x: &'a str) -> &'a str {", "fn f<'a>(x: &'a str) -> &'a str { // " + Day),
            ("a.py", "x = 1", "x = 1 # " + Day),
            ("run.sh", "echo hi", "echo hi # " + Day),
            ("a.ps1", "$x = 1", "$x = 1 # " + Day),
            ("a.rb", "x = 1", "x = 1 # " + Day),
            ("a.sql", "SELECT 1;", "SELECT 1; -- " + Day),
            ("a.lua", "local x = 1", "local x = 1 -- " + Day),
            ("a.css", "a { color: red; }", "a { color: red; } /* " + Day + " */"),
            ("a.cs", "char q = '\"'; int n = 2;", "char q = '\"'; int n = 2; // " + Day),
        };
        var bad = new List<string>();
        foreach (var (file, line, expect) in cases)
        {
            string got = Mark(file, line + "\n", 1);
            if (got != expect + "\n") bad.Add($"{file}: «{got.TrimEnd()}»");
        }
        Check("CM2", $"the marker uses each language's own comment ({cases.Length} languages: //, #, --, /* */) at the end of the line",
            bad.Count == 0, string.Join(" ;; ", bad));

        var none = new (string File, string Text, int Line, string Why)[]
        {
            ("a.json", "{ \"a\": 1 }\n", 1, "JSON has no comments"),
            ("README.md", "# Title\n", 1, "Markdown"),
            ("a.xaml", "<Grid/>\n", 1, "XAML"),
            ("a.yaml", "a: 1\n", 1, "YAML"),
            ("a.xyz", "whatever\n", 1, "an unknown language"),
            ("a.jsx", "return <div/>;\n", 1, "JSX"),
            ("a.cs", "var s = @\"one\ntwo\nthree\";\n", 2, "inside a C# verbatim string"),
            ("a.cs", "var s = \"\"\"\n  raw\n  \"\"\";\n", 2, "inside a C# raw string"),
            ("a.py", "def f():\n    \"\"\"Doc\n    more\n    \"\"\"\n", 3, "inside a Python docstring"),
            ("a.ts", "const t = `a\nb ${x}\n`;\n", 2, "inside a JS template literal"),
            ("a.go", "s := `raw\nstill raw\n`\n", 2, "inside a Go raw string"),
            ("a.rs", "let s = r#\"x\ny\n\"#;\n", 2, "inside a Rust raw string"),
            ("a.cs", "/* block\n   inner\n*/\n", 2, "inside a block comment"),
            ("a.c", "#define A(x) \\\n  ((x) + 1)\n", 1, "a line continued with a backslash"),
            ("a.c", "#include <stdio.h>\n", 1, "a preprocessor line"),
            ("a.js", "foo(); // eslint-disable-line no-console\n", 1, "a line that already ends in a comment (it may be a directive)"),
            ("run.sh", "#!/bin/bash\n", 1, "a shebang"),
            ("run.sh", "cat <<EOF\nbody\nEOF\n", 2, "a heredoc body"),
            ("a.ps1", "$s = @\"\ninside\n\"@\n", 2, "a PowerShell here-string"),
            ("a.sh", "echo \"one\ntwo\"\n", 1, "a shell string that runs onto the next line"),
            ("a.cs", "var s = \"unclosed\n", 1, "an unclosed one-line string"),
            ("a.lua", "local s = [[\nlong\n]]\n", 2, "a Lua long string"),
            ("a.cs", "\n", 1, "a blank line"),
        };
        var leaked = new List<string>();
        foreach (var (file, text, line, why) in none)
        {
            var (got, marked) = UserEditMarker.AddMarkers(text, file, new[] { (line, 1) }, MarkDay);
            if (marked.Count > 0 || got != text) leaked.Add($"{why} ({file})");
        }
        Check("CM2b", $"no marker where it could break or change the code ({none.Length} cases: strings, comments, heredocs, continuations, directives, JSON…)",
            leaked.Count == 0, string.Join(" ;; ", leaked));

        // A run: the marker goes on its first line that can hold one.
        string run = "foo(); // eslint-disable-line\nbar();\nbaz();\n";
        Check("CM2c", "in a run of edited lines the marker goes on the first line that can take one (once per run)",
            Mark("a.js", run, 1, 3) == "foo(); // eslint-disable-line\nbar(); // " + Day + "\nbaz();\n", Mark("a.js", run, 1, 3));
        string crlf = "a();\r\nb();\r\nc();";
        Check("CM2d", "line ends are kept exactly (CRLF, no newline at the end)", Mark("a.cs", crlf, 3) == "a();\r\nb();\r\nc(); // " + Day, Mark("a.cs", crlf, 3));
        string twice = Mark("a.cs", "x();\n", 1);
        Check("CM2e", "a run that already carries a marker gets no second one", Mark("a.cs", twice, 1) == twice, Mark("a.cs", twice, 1));
        Check("CM2f", "the closing line of a multi-line string can carry one (the string ended)",
            Mark("a.cs", "var s = @\"one\ntwo\";\n", 2) == "var s = @\"one\ntwo\"; // " + Day + "\n", Mark("a.cs", "var s = @\"one\ntwo\";\n", 2));
        var found = UserEditMarker.Find("a();\nb(); // " + Day + "\nc(); # [user-edit 2026-01-02]\n");
        Check("CM2g", "Find lists each marked line with its date, Strip takes the marker off and nothing else",
            found.Count == 2 && found[0] == (2, "2026-11-09", "b();") && found[1].Date == "2026-01-02"
            && UserEditMarker.Strip("x(); // keep // " + Day) == "x(); // keep" && UserEditMarker.Strip("x();") == "x();",
            string.Join(" | ", found));
        Check("CM2h", "one exact searchable tag", UserEditMarker.Tag == "[user-edit" && UserEditMarker.Text(MarkDay) == Day, UserEditMarker.Text(MarkDay));
    }

    // ───────────────────────── CM3: the per-chat baseline ─────────────────────────

    static Task BaselineChecks()
    {
        string proj = Dir("cm-project");
        string store = Path.Combine(Scratch, "cm-checkpoints");
        string file = Path.Combine(proj, "Calc.cs");
        string original = Lines("class Calc {", "  int A() => 1;", "", "  int B() => 2;", "", "}");
        File.WriteAllText(file, original);
        var cp = new CheckpointService(null, store);
        var prior = AgentRunScope.Current;
        try
        {
            // Turn 1 of chat "sess-A": changes A. Turn 2 of the same chat: changes B.
            var t1 = new AgentRunScope("sess-A", proj) { Step = 1 };
            AgentRunScope.Enter(t1); cp.CaptureBeforeChange(file);
            string afterT1 = original.Replace("=> 1", "=> 10");
            File.WriteAllText(file, afterT1);
            Thread.Sleep(20);
            var t2 = new AgentRunScope("sess-A", proj) { Step = 1 };
            AgentRunScope.Enter(t2); cp.CaptureBeforeChange(file);
            string afterT2 = afterT1.Replace("=> 2", "=> 20");
            File.WriteAllText(file, afterT2);
            AgentRunScope.Enter(null);

            var session = cp.BeforeSession(proj, file);
            var turn = cp.BeforeLatestTurn(proj, file);
            Check("CM3", "the baseline is the file before the CHAT first touched it (both turns' changes show), not only before its last turn",
                session?.Text == original && turn?.Text == afterT1 && session?.Session == "sess-A" && session.Turn == turn.Turn,
                $"session={session?.Text?.Length} turn={turn?.Text?.Length}");

            // The user edits and saves in the editor (the real store), then the file is closed and opened again.
            string userSaved = afterT2.Replace("}", "  int C() => 3;\n}");
            UserEditStore.Current!.Remember(file, afterT2, userSaved, MarkDay);
            File.WriteAllText(file, userSaved);
            var reopenedCp = new CheckpointService(null, store);
            var reopenedStore = new UserEditStore(UserEditStore.Current.Root);
            var again = reopenedCp.BeforeSession(proj, file);
            var rec = reopenedStore.Get(file);
            var hunks = ChangeMarks.Build(again?.Text, userSaved, userSaved, rec);
            Check("CM3b", "a save by the user and a reopen keep the baseline (it comes from the kept copy, never from the file) and the user's lines",
                again?.Text == original && rec != null && rec.Lines.Count == 1
                && hunks.Count(h => !h.ByUser) == 2 && hunks.Count(h => h.ByUser) == 1,
                Show(hunks));

            // Another chat changes the file later: the marks are now that chat's.
            var t3 = new AgentRunScope("sess-B", proj) { Step = 1 };
            AgentRunScope.Enter(t3); cp.CaptureBeforeChange(file);
            File.WriteAllText(file, userSaved.Replace("=> 3", "=> 30"));
            AgentRunScope.Enter(null);
            var other = new CheckpointService(null, store).BeforeSession(proj, file);
            Check("CM3c", "a later chat's marks start from the file as it was when THAT chat first touched it",
                other?.Text == userSaved && other.Session == "sess-B", other?.Session ?? "null");
        }
        finally { AgentRunScope.Enter(prior); }
        return Task.CompletedTask;
    }

    // ───────────────────────── CM4: the view model's real save ─────────────────────────

    static async Task SavePathChecks(IServiceProvider provider)
    {
        var vm = provider.GetRequiredService<CodeEditorViewModel>();
        var ws = provider.GetRequiredService<CodeWorkspaceService>();
        var settings = provider.GetRequiredService<SettingsService>();
        bool priorMarkers = settings.Settings.EditorUserEditMarkers;
        string dir = Dir("cm-save");
        string cs = Path.Combine(dir, "Svc.cs"), json = Path.Combine(dir, "app.json");
        File.WriteAllText(cs, Lines("class Svc {", "  int X() => 1;", "  string S = \"keep\";", "}"));
        File.WriteAllText(json, Lines("{", "  \"a\": 1", "}"));
        var save = typeof(CodeEditorViewModel).GetMethod("SaveTabInteractiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            settings.UpdateSettings(s => s.EditorUserEditMarkers = true);
            var tab = await ws.OpenFileAsync(cs);
            tab!.Content = tab.Content.Replace("=> 1", "=> 2").Replace("}\n", "  int Y() => 3;\n}\n");
            bool ok = await (Task<bool>)save.Invoke(vm, new object[] { tab })!;
            string disk = File.ReadAllText(cs);
            string today = UserEditMarker.Text(DateTime.Now);
            Check("CM4", "a save in normal edit mode writes the file with a [user-edit] comment on each run the user typed (two runs → two)",
                ok && disk.Contains("int X() => 2; // " + today) && disk.Contains("int Y() => 3; // " + today)
                && !disk.Contains("\"keep\"; //") && disk == tab.Content && !tab.IsDirty, disk);
            var rec = UserEditStore.Current!.Get(cs);
            Check("CM4b", "…and remembers those lines (UserEditStore), without the marker in the remembered text",
                rec != null && rec.Lines.Select(l => l.Text).SequenceEqual(new[] { "int X() => 2;", "int Y() => 3;" }), string.Join(" | ", rec?.Lines.Select(l => l.Text) ?? new string[0]));

            // Saving again without typing changes nothing; typing elsewhere keeps the first lines and their marker.
            tab.Content = tab.Content.Replace("\"keep\"", "\"kept\"");
            ok = await (Task<bool>)save.Invoke(vm, new object[] { tab })!;
            var rec2 = UserEditStore.Current.Get(cs);
            Check("CM4c", "a second save adds the new line and keeps the earlier ones (and their single marker)",
                ok && rec2!.Lines.Count == 3 && File.ReadAllText(cs).Split(UserEditMarker.Tag).Length - 1 == 3, File.ReadAllText(cs));

            // A file without comments: no marker, still remembered.
            var jt = await ws.OpenFileAsync(json);
            jt!.Content = jt.Content.Replace("1", "2");
            ok = await (Task<bool>)save.Invoke(vm, new object[] { jt })!;
            Check("CM4d", "JSON is saved exactly as typed (no comment), and the edited line is still remembered",
                ok && File.ReadAllText(json) == Lines("{", "  \"a\": 2", "}") && UserEditStore.Current.Get(json)?.Lines.Count == 1, File.ReadAllText(json));

            // The setting off: no marker, remembered all the same.
            settings.UpdateSettings(s => s.EditorUserEditMarkers = false);
            var t2 = await ws.OpenFileAsync(cs);
            t2!.Content = t2.Content.Replace("class Svc {", "class Svc2 {");
            ok = await (Task<bool>)save.Invoke(vm, new object[] { t2 })!;
            Check("CM4e", "with the setting off the save adds no comment but the line is still remembered",
                ok && File.ReadAllText(cs).StartsWith("class Svc2 {\n") && UserEditStore.Current.Get(cs)!.Lines.Any(l => l.Text == "class Svc2 {"),
                File.ReadAllText(cs).Split('\n')[0]);
        }
        finally { settings.UpdateSettings(s => s.EditorUserEditMarkers = priorMarkers); }
    }

    // ───────────────────────── CM5: search ─────────────────────────

    static void SearchChecks()
    {
        string root = Dir("cm-search");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "lib"));
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), Lines("a();", "b(); // " + Day, "c();"));
        File.WriteAllText(Path.Combine(root, "src", "b.py"), Lines("x = 1 # [user-edit 2026-10-01]"));
        File.WriteAllText(Path.Combine(root, "node_modules", "lib", "v.js"), Lines("v(); // " + Day));   // a dependency: skipped
        string json = Path.Combine(root, "conf.json");
        File.WriteAllText(json, Lines("{", "  \"port\": 8080", "}"));
        var store = UserEditStore.Current!;
        store.Put(new UserEditRecord { Path = json, Lines = { new UserEditLine("\"port\": 8080", 2, "2026-11-09") } });
        // An open tab with unsaved typing counts as the editor shows it.
        string openA = Lines("a(); // " + Day, "b(); // " + Day, "c();");
        var spots = store.FindInProject(root, p => p.EndsWith("A.cs") ? openA : null);
        string got = string.Join(" | ", spots.Select(s => $"{s.RelPath}:{s.Line}:{s.Date}"));
        Check("CM5", "search finds every marked line (open tabs as the editor shows them) and the remembered lines of a file without comments",
            spots.Count == 4 && spots.Any(s => s.RelPath == "conf.json" && s.Line == 2 && !s.HasMarker)
            && spots.Count(s => s.RelPath.EndsWith("A.cs")) == 2 && spots.Any(s => s.RelPath.EndsWith("b.py") && s.Date == "2026-10-01"),
            got);
        Check("CM5b", "…but not inside node_modules", !spots.Any(s => s.RelPath.Contains("node_modules")), got);
        Check("CM5c", "a project with no user edits lists nothing", store.FindInProject(Dir("cm-empty")).Count == 0, "");
    }

    // ───────────────────────── CM6: keys and the AI's note ─────────────────────────

    static void KeyAndNoteChecks()
    {
        var keys = (Dictionary<string, Dictionary<string, string>>)typeof(CodeEditorViewModel).Assembly
            .GetType("CluadeX.Services.Localization.ChangeMarkKeys")!.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var missing = keys.Where(k => !k.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th)
                                      || !k.Value.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en)).Select(k => k.Key).ToList();
        Check("CM6", $"every change-mark string has Thai and English ({keys.Count} keys)", missing.Count == 0 && keys.Count > 20, string.Join(", ", missing));
        var none = new AppSettings();
        string note = CodeCommentPolicy.ForAgent(none, @"C:\work\demo");
        Check("CM6b", "the AI is told about [user-edit] lines on every turn, even with AI comments off (and the comment policy itself stays empty)",
            note == CodeCommentPolicy.UserEditNote && note.Contains("[user-edit") && CodeCommentPolicy.InstructionFor(none, @"C:\work\demo") == "", note);
    }
}
