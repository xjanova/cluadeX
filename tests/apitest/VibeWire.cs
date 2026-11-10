// The chat's vibe-coding helpers (owner 2026-11-08): the per-turn change card's ledger and plain line, the error chip's
// filter and fix request, "explain simply", @-mentions, the context gauge's pace and the circled-spot picture. The
// pure parts are proven here; the card, chip, popup and gauge on the real chat page are in chattest (VibeTests).
using System.IO;
using CluadeX.Services.Agents;
using CluadeX.Services.Lsp;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class VibeWire
{
    static readonly Dictionary<string, string> Th = new()
    {
        ["changes.line.edited"] = "แก้ {0} ไฟล์",
        ["changes.line.created"] = "สร้างไฟล์ใหม่ {0} ไฟล์",
        ["changes.line.both"] = "แก้ {0} ไฟล์ และสร้างใหม่ {1} ไฟล์",
        ["changes.area.screen"] = "หน้าตาที่เห็น ({0})",
        ["changes.area.logic"] = "การทำงานเบื้องหลัง ({0})",
        ["changes.area.data"] = "การตั้งค่าและข้อมูล ({0})",
        ["changes.area.test"] = "ชุดทดสอบ ({0})",
        ["changes.area.docs"] = "เอกสาร ({0})",
        ["changes.and"] = " และ",
        ["changes.more"] = "อีก {0}",
    };

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        const string S = "vibe";
        section("Vibe helpers — change card, error chip, explain, @-mentions, context gauge");

        // ─── the change ledger ───
        var t = new TurnChanges();
        t.SeedBefore(@"C:\shots\old.jpg");
        t.Note(@"C:\p\src\AuthService.cs", "src/AuthService.cs", 10, 2, false);
        t.NoteImage(@"C:\shots\before.jpg");                       // still before any screen edit
        t.Note(@"C:\p\Views\Login.xaml", "Views/Login.xaml", 30, 0, true);
        t.Note(@"C:\p\src\AuthService.cs", "src/AuthService.cs", 3, 1, false);   // the same file again: adds up
        check(S, "same file adds up", t.Count == 2 && t.Files[0].Added == 13 && t.Files[0].Removed == 3,
            string.Join("; ", t.Files.Select(f => $"{f.Display} +{f.Added} -{f.Removed}")));
        check(S, "before = newest before 1st UI edit", t.BeforeImage == @"C:\shots\before.jpg", t.BeforeImage ?? "null");
        check(S, "no after without a new picture", t.AfterImage == null, t.AfterImage ?? "null");
        t.NoteImage(@"C:\shots\after1.jpg");
        t.Note(@"C:\p\Views\Header.xaml", "Views/Header.xaml", 4, 4, false);
        check(S, "a later UI edit drops the after", t.AfterImage == null, t.AfterImage ?? "null");
        t.NoteImage(@"C:\shots\after2.jpg");
        check(S, "after = picture after last UI edit", t.AfterImage == @"C:\shots\after2.jpg", t.AfterImage ?? "null");
        t.Note(@"C:\p\x.cs", "x.cs", 0, 0, false);
        check(S, "nothing changed is not listed", t.Count == 3, t.Count.ToString());

        var round = TurnChanges.FromJson(t.ToJson());
        check(S, "JSON round trip", round is { } r && r.Files.Count == 3 && r.Files[1].Created && r.Before == @"C:\shots\before.jpg"
            && r.After == @"C:\shots\after2.jpg", t.ToJson());
        check(S, "bad JSON gives no card", TurnChanges.FromJson("{nope") == null && TurnChanges.FromJson("") == null
            && TurnChanges.FromJson("{\"Files\":[]}") == null, "");
        var logicOnly = new TurnChanges();
        logicOnly.NoteImage(@"C:\shots\a.jpg");
        logicOnly.Note(@"C:\p\a.cs", "a.cs", 1, 0, false);
        logicOnly.NoteImage(@"C:\shots\b.jpg");
        var lo = TurnChanges.FromJson(logicOnly.ToJson());
        check(S, "no pictures without a screen file", lo is { Before: null, After: null }, logicOnly.ToJson());

        // ─── areas and the plain line ───
        check(S, "areas", TurnChanges.AreaOf("Views/Login.xaml") == ChangeArea.Screen
            && TurnChanges.AreaOf("resources/views/home.blade.php") == ChangeArea.Screen
            && TurnChanges.AreaOf("lib/screens/home.dart") == ChangeArea.Screen
            && TurnChanges.AreaOf("lib/models/user.dart") == ChangeArea.Logic
            && TurnChanges.AreaOf("appsettings.json") == ChangeArea.Data
            && TurnChanges.AreaOf("tests/apitest/VibeWire.cs") == ChangeArea.Test
            && TurnChanges.AreaOf("README.md") == ChangeArea.Docs
            && TurnChanges.AreaOf("src/App.tsx") == ChangeArea.Screen, "");
        string line = TurnChanges.PlainLine(t.Files, k => Th[k]);
        check(S, "plain line (Thai)", line == "แก้ 2 ไฟล์ และสร้างใหม่ 1 ไฟล์ — หน้าตาที่เห็น (Login.xaml, Header.xaml) และการทำงานเบื้องหลัง (AuthService.cs)", line);
        var many = Enumerable.Range(1, 5).Select(i => new FileChange($@"C:\p\v{i}.xaml", $"v{i}.xaml", 1, 0, true)).ToList();
        string manyLine = TurnChanges.PlainLine(many, k => Th[k]);
        check(S, "plain line caps names", manyLine == "สร้างไฟล์ใหม่ 5 ไฟล์ — หน้าตาที่เห็น (v1.xaml, v2.xaml อีก 3)", manyLine);
        check(S, "no files, no line", TurnChanges.PlainLine(Array.Empty<FileChange>(), k => Th[k]) == "", "");

        // ─── explain simply ───
        string ex = VibePrompts.ExplainSimply("อธิบายใหม่:\n\n{0}", "line one\nline two");
        check(S, "explain quotes the reply", ex == "อธิบายใหม่:\n\n> line one\n> line two", ex);
        string longEx = VibePrompts.ExplainSimply("{0}", new string('ก', 5000));
        check(S, "explain cuts a long reply", longEx.Length < VibePrompts.MaxQuote + 10 && longEx.EndsWith(" …"), longEx.Length.ToString());

        // ─── errors: the chip's filter and the request ───
        string root = Path.Combine(Path.GetTempPath(), "vibewire-proj");
        BuildProblem P(string rel, LspSeverity sev, string msg, int line = 3) =>
            new(Path.Combine(root, rel), line, 5, 0, 0, sev, msg, "CS0103", "dotnet");
        var entries = new List<BuildProblemEntry>
        {
            new(P("a.cs", LspSeverity.Error, "The name 'x' does not exist"), false),
            new(P("a.cs", LspSeverity.Error, "The name 'x' does not exist"), false),          // same place twice
            new(P("b.cs", LspSeverity.Warning, "unused"), false),                               // a warning
            new(P("c.cs", LspSeverity.Error, "stale one"), true),                               // fixed since?
            new(new BuildProblem(@"Z:\other\d.cs", 1, 1, 0, 0, LspSeverity.Error, "elsewhere", "", "dotnet"), false),
            new(P(@"sub\e.cs", LspSeverity.Error, "type missing", 9), false),
        };
        var errs = ChatViewModel.ErrorsIn(entries, root);
        check(S, "chip counts this project's live errors", errs.Count == 2, string.Join("; ", errs.Select(e => e.Path + ":" + e.Message)));
        check(S, "no project, no chip", ChatViewModel.ErrorsIn(entries, "").Count == 0, "");
        string fix = VibePrompts.FixErrors("มี {0} จุด:", "อีก {0}", root, errs.Select(e => (e.Path, e.Line, e.Column, e.Message, e.Code)).ToList());
        check(S, "fix request lists relative places", fix.StartsWith("มี 2 จุด:\n```") && fix.Contains("a.cs:3:5 — CS0103: The name 'x' does not exist")
            && fix.Contains("sub/e.cs:9:5 — CS0103: type missing") && !fix.Contains(root), fix);
        var lots = Enumerable.Range(1, 25).Select(i => (Path.Combine(root, $"f{i}.cs"), i, 1, "bad", "")).ToList();
        string lotsFix = VibePrompts.FixErrors("{0}", "+{0} more", root, lots);
        check(S, "fix request caps at 20", lotsFix.Contains("f20.cs:20") && !lotsFix.Contains("f21.cs") && lotsFix.EndsWith("+5 more"), lotsFix[^40..]);

        // ─── @-mentions ───
        check(S, "@ at start", Mentions.QueryAt("@log", 4) is { Start: 0, Query: "log" }, "");
        check(S, "@ after a space", Mentions.QueryAt("fix @Lo now", 7) is { Start: 4, Query: "Lo" }, "");
        check(S, "e-mail is no mention", Mentions.QueryAt("me@mail.com", 11) == null, "");
        check(S, "caret past a space ends it", Mentions.QueryAt("@log x", 6) == null, "");
        check(S, "bare @ lists files", Mentions.QueryAt("see @", 5) is { Start: 4, Query: "" }, "");
        var files = new[] { "src/Views/LoginPage.xaml", "src/Services/AuthService.cs", "docs/login-notes.md", "src/Services/Logger.cs" };
        var syms = new List<(string, string, int)> { ("LoginAsync", "src/Services/AuthService.cs", 42), ("Log", "src/Services/Logger.cs", 7) };
        var m = Mentions.Match("login", files, syms);
        check(S, "name start ranks first", m.Count >= 3 && !m[0].IsSymbol && m[0].Label.StartsWith("login", StringComparison.OrdinalIgnoreCase),
            string.Join(" | ", m.Select(x => x.Insert)));
        check(S, "files and symbols both listed", m.Any(x => x.IsSymbol) && m.Any(x => x.Insert == "@src/Views/LoginPage.xaml")
            && m.Any(x => x.Insert == "@docs/login-notes.md"), string.Join(" | ", m.Select(x => x.Insert)));
        check(S, "one-letter query: no symbols", Mentions.Match("L", files, syms).All(x => !x.IsSymbol), "");
        check(S, "nothing matches", Mentions.Match("zzz", files, syms).Count == 0, "");
        var names = Mentions.SymbolsIn("namespace A;\npublic sealed class Logger : IDisposable\n{\n    public async Task<int> LogLineAsync(string s) { }\n"
            + "    private static void Write<T>(T x) { }\n    if (ok) return;\n}\nexport function renderPage() {}\ndef parse_rows(x):\nrecord Point(int X);\n");
        check(S, "symbols: names, not lines", names.SequenceEqual(new (string, int)[] { ("Logger", 2), ("LogLineAsync", 4), ("Write", 5), ("renderPage", 8), ("parse_rows", 9), ("Point", 10) }),
            string.Join(", ", names.Select(n => $"{n.Name}:{n.Line}")));
        var (applied, caret) = Mentions.Apply("fix @Lo now", 4, 7, new MentionItem("Logger.cs", "src/Services/Logger.cs", "@src/Services/Logger.cs", false));
        check(S, "apply replaces the query", applied == "fix @src/Services/Logger.cs now" && caret == 27, $"{applied}|{caret}");
        var (end, endCaret) = Mentions.Apply("see @Lo", 4, 7, new MentionItem("x", "x", "@x", false));
        check(S, "apply at end adds a space", end == "see @x " && endCaret == 7, $"{end}|{endCaret}");

        // ─── the context gauge's pace ───
        check(S, "no pace yet", ChatViewModel.TurnsUntil(20, 60, null) == null && ChatViewModel.TurnsUntil(20, 60, new List<double> { 20 }) == null, "");
        check(S, "pace from recent turns", ChatViewModel.TurnsUntil(30, 60, new List<double> { 10, 20, 30 }) == 3,
            ChatViewModel.TurnsUntil(30, 60, new List<double> { 10, 20, 30 })?.ToString() ?? "null");
        check(S, "past the line", ChatViewModel.TurnsUntil(65, 60, new List<double> { 50, 65 }) == 0, "");
        check(S, "flat chat: no guess", ChatViewModel.TurnsUntil(30, 60, new List<double> { 30, 30 }) == null, "");
        check(S, "at least one turn", ChatViewModel.TurnsUntil(59.5, 60, new List<double> { 40, 50 }) == 1, "");

        // ─── the circled-spot picture ───
        string png = Path.Combine(Path.GetTempPath(), "vibewire-spot-src.png");
        try
        {
            var src = new System.Windows.Media.Imaging.WriteableBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            var px = Enumerable.Repeat(unchecked((int)0xFFFFFFFF), 200 * 100).ToArray();
            src.WritePixels(new System.Windows.Int32Rect(0, 0, 200, 100), px, 200 * 4, 0);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using (var fs = File.Create(png)) enc.Save(fs);
            byte[] marked = ChatViewModel.MarkSpot(png, 0.5, 0.5);
            var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(marked),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(dec, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var outPx = new byte[200 * 100 * 4];
            conv.CopyPixels(outPx, 200 * 4, 0);
            // the ring passes ~r = 14 px right of the centre (100,50): a red pixel there, white in the corner
            bool redOnRing = Enumerable.Range(108, 12).Any(x => { int i = (50 * 200 + x) * 4; return outPx[i + 2] > 180 && outPx[i + 1] < 120; });
            bool cornerWhite = outPx[0] > 240 && outPx[1] > 240 && outPx[2] > 240;
            check(S, "spot is circled in red", dec.PixelWidth == 200 && dec.PixelHeight == 100 && redOnRing && cornerWhite,
                $"{dec.PixelWidth}x{dec.PixelHeight} ring={redOnRing} corner={cornerWhite}");
        }
        catch (Exception e) { check(S, "spot is circled in red", false, e.Message); }
        finally { try { File.Delete(png); } catch { } }
        return Task.CompletedTask;
    }
}
