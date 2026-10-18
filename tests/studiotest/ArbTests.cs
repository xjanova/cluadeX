using System.IO;
using System.Reflection;
using System.Text.Json;
using CluadeX.Services.UiStudio.Sketch;

namespace StudioTest;

/// <summary>
/// The text tool on translated text (through the REAL CluadeX.dll): a Text that reads a gen-l10n getter is resolved
/// to its key; the key's message is read from — and written into — the .arb file of the language the screen is drawn
/// in, changing nothing but that one string (formatting, key order, every other key, line endings kept; a missing key
/// added next to its template neighbour); placeholder / plural messages and new text with braces are refused; every
/// result is strict JSON. Also l10n.yaml's reading (where the generated Dart goes, its import) and when the renderer
/// makes the translations again (only after an .arb changed since the last draw).
/// </summary>
internal static partial class Program
{
    // an .arb as an app writes it: @@locale first, groups split by blank lines, @metadata after its key, Thai as is
    const string ThArb = "{\n  \"@@locale\": \"th\",\n  \"appName\": \"Thaiprompt Admin\",\n\n  \"loginTitle\": \"เข้าสู่ระบบ\",\n" +
                         "  \"@loginTitle\": { \"description\": \"หัวข้อหน้าเข้าสู่ระบบ\" },\n  \"loginButton\": \"เข้าสู่ระบบอย่างปลอดภัย\",\n" +
                         "  \"appVersion\": \"v{version} · ป้องกันด้วย 2FA\",\n  \"@appVersion\": { \"placeholders\": { \"version\": {} } },\n" +
                         "  \"items\": \"{count, plural, =0{ไม่มีรายการ} other{{count} รายการ}}\",\n\n  \"qrTitle\": \"สแกน QR\"\n}\n";
    // its English file: no @@locale (the name says it), loginButton not translated yet
    const string EnArb = "{\n  \"appName\": \"Thaiprompt Admin\",\n\n  \"loginTitle\": \"Sign In\",\n  \"@loginTitle\": { \"description\": \"The login page's title\" },\n" +
                         "  \"appVersion\": \"v{version} · 2FA Protected\",\n\n  \"qrTitle\": \"Scan QR\"\n}\n";

    const string L10nScreen = @"import 'package:flutter/material.dart';
import 'gen/l10n/app_localizations.dart';

extension L10nX on BuildContext {
  AppL10n get l10n => AppL10n.of(this);
}

class LoginScreen extends StatelessWidget {
  const LoginScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final t = AppL10n.of(context);
    final s = Stats.load();
    return Column(
      children: [
        Text(context.l10n.loginTitle),
        Text(AppL10n.of(context).loginButton),
        Text(t.qrTitle),
        Text(s.total),
        Text(context.l10n.appVersion('1.0')),
        Text(AppLocalizations.of(context)!.appName),
        Text(AppLocalizations.of(context)?.appName ?? 'Admin'),
        ElevatedButton(onPressed: () {}, child: Text(context.l10n.loginButton)),
        Text(context.l10n.loginTitle.toUpperCase()),
        Text(widget.title),
      ],
    );
  }

  Widget row(AppL10n l10n) => Text(l10n.qrTitle);

  Widget cell(Stats t) => Text(t.total);
}
";

    static void ArbEdits(string temp)
    {
        Console.WriteLine("UI Studio: translated text (.arb files)");

        // ── reading a file ──
        Check("arb: a key's message", ArbFile.Message(ThArb, "loginTitle") == "เข้าสู่ระบบ");
        Check("arb: @@locale reads as a value, @metadata is not a message", ArbFile.Message(ThArb, "@@locale") == "th" && ArbFile.Message(ThArb, "@loginTitle") == null);
        Check("arb: the message keys in the file's order", string.Join(",", ArbFile.Keys(ThArb)) == "appName,loginTitle,loginButton,appVersion,items,qrTitle", string.Join(",", ArbFile.Keys(ThArb)));
        Check("arb: \\u escapes are read", ArbFile.Message("{\"x\": \"\\u0e2a\\u0e27\\u0e31\\u0e2a\\u0e14\\u0e35 \\\"ok\\\"\"}", "x") == "สวัสดี \"ok\"");
        Check("arb: a key written twice reads its last value (as Dart's json does)", ArbFile.Message("{\"a\": \"1\", \"a\": \"2\"}", "a") == "2");

        // ── ICU: only plain text is typed over ──
        Check("icu: plain text shows as it is", ArbFile.PlainText("เข้าสู่ระบบ", false) == "เข้าสู่ระบบ" && ArbFile.PlainText("Don't", false) == "Don't");
        Check("icu: a placeholder is not plain text", ArbFile.PlainText("v{version} · ป้องกันด้วย 2FA", false) == null);
        Check("icu: a plural is not plain text", ArbFile.PlainText("{count, plural, =0{ไม่มี} other{{count} รายการ}}", false) == null);
        Check("icu: use-escaping — '' is one quote, '…' is literal", ArbFile.PlainText("Don''t", true) == "Don't" && ArbFile.PlainText("'{x}' ok", true) == "{x} ok"
              && ArbFile.PlainText("'Flutter''s' best", true) == "Flutter's best");
        Check("icu: use-escaping — an unmatched quote is broken (not plain)", ArbFile.PlainText("it's", true) == null);
        Check("icu: new text with { or } is refused (it would become a placeholder)", ArbFile.Encode("Hi {name}", false) == null && ArbFile.Encode("a } b", true) == null);
        Check("icu: an apostrophe is written as it is, or as '' with use-escaping", ArbFile.Encode("Don't", false) == "Don't" && ArbFile.Encode("Don't", true) == "Don''t");

        // ── writing: exactly one string changes ──
        var e1 = ArbFile.SetMessage(ThArb, "loginTitle", "ลงชื่อเข้าใช้");
        ArbExact("write: only the message's characters change", e1, ThArb.Replace("\"loginTitle\": \"เข้าสู่ระบบ\"", "\"loginTitle\": \"ลงชื่อเข้าใช้\""));
        var e2 = ArbFile.SetMessage(ThArb, "qrTitle", "say \"hi\" \\ ok\nline2\ttab");
        ArbExact("write: quotes, backslashes, new lines and tabs are JSON-escaped", e2, ThArb.Replace("\"qrTitle\": \"สแกน QR\"", "\"qrTitle\": \"say \\\"hi\\\" \\\\ ok\\nline2\\ttab\""));
        Check("write: the result is strict JSON that reads back the new message", e2.Text != null && StrictJson(e2.Text) is { } j2 && j2.GetProperty("qrTitle").GetString() == "say \"hi\" \\ ok\nline2\ttab");
        Check("write: every other member is exactly as it was", e1.Text != null && SameExcept(ThArb, e1.Text, "loginTitle"));
        string crlf = ThArb.Replace("\n", "\r\n");
        ArbExact("write: a CRLF file keeps its line breaks", ArbFile.SetMessage(crlf, "loginTitle", "ลงชื่อเข้าใช้"), crlf.Replace("\"loginTitle\": \"เข้าสู่ระบบ\"", "\"loginTitle\": \"ลงชื่อเข้าใช้\""));
        ArbExact("write: a key written twice — its last value is the one changed", ArbFile.SetMessage("{\"a\": \"1\", \"a\": \"2\"}", "a", "3"), "{\"a\": \"1\", \"a\": \"3\"}");

        // ── writing: a key the language doesn't translate yet ──
        var add = ArbFile.SetMessage(EnArb, "loginButton", "Sign in safely", ArbFile.Keys(ThArb));
        ArbExact("add: after its template neighbour and that one's @metadata, in the file's layout", add,
                 EnArb.Replace("  \"@loginTitle\": { \"description\": \"The login page's title\" },\n",
                               "  \"@loginTitle\": { \"description\": \"The login page's title\" },\n  \"loginButton\": \"Sign in safely\",\n"));
        string enNoName = EnArb.Replace("  \"appName\": \"Thaiprompt Admin\",\n\n", "");
        ArbExact("add: the template's first key goes first", ArbFile.SetMessage(enNoName, "appName", "Admin", ArbFile.Keys(ThArb)),
                 enNoName.Replace("{\n  \"loginTitle\"", "{\n  \"appName\": \"Admin\",\n  \"loginTitle\""));
        string thNoName = ThArb.Replace("  \"appName\": \"Thaiprompt Admin\",\n", "");
        ArbExact("add: … but after @@locale", ArbFile.SetMessage(thNoName, "appName", "แอดมิน", ArbFile.Keys(ThArb)),
                 thNoName.Replace("  \"@@locale\": \"th\",\n", "  \"@@locale\": \"th\",\n  \"appName\": \"แอดมิน\",\n"));
        ArbExact("add: a file written on one line stays on one line", ArbFile.SetMessage("{ \"a\": \"x\" }", "b", "y", new[] { "a", "b" }), "{ \"a\": \"x\", \"b\": \"y\" }");
        ArbExact("add: into an empty file", ArbFile.SetMessage("{}", "a", "x"), "{\n  \"a\": \"x\"\n}");

        // ── never a broken file ──
        Check("refuse: a file that isn't strict JSON (a trailing comma) is not touched", ArbFile.SetMessage("{\"a\": \"x\",}", "a", "y").Error == "studio.fedit.arb.bad");
        Check("refuse: comments are not JSON either", ArbFile.SetMessage("{\n  // note\n  \"a\": \"x\"\n}", "a", "y").Error == "studio.fedit.arb.bad");
        Check("refuse: a key whose value isn't text", ArbFile.SetMessage(ThArb, "@loginTitle", "x").Error == "studio.fedit.arb.bad");

        // ── the getter a Text reads → its key ──
        L10nKeyOf("getter: context.l10n.x", "Text(context.l10n.loginTitle)", "loginTitle", false);
        L10nKeyOf("getter: AppL10n.of(context).x", "Text(AppL10n.of(context).loginButton)", "loginButton", false);
        L10nKeyOf("getter: a variable that holds AppL10n.of(context)", "Text(t.qrTitle)", "qrTitle", false);
        L10nKeyOf("getter: AppLocalizations.of(context)!.x", "Text(AppLocalizations.of(context)!.appName)", "appName", false);
        L10nKeyOf("getter: a nullable getter with a fallback (?. … ?? 'x')", "Text(AppLocalizations.of(context)?.appName", "appName", false);
        L10nKeyOf("getter: a parameter typed AppL10n", "Text(l10n.qrTitle)", "qrTitle", false);
        L10nKeyOf("getter: called with a value → a message with placeholders", "Text(context.l10n.appVersion(", "appVersion", true);
        L10nKeyOf("getter: a button's label", "ElevatedButton(", "loginButton", false);
        L10nKeyOf("not a getter: data that ends in a name (final s = Stats.load())", "Text(s.total)", null, false);
        L10nKeyOf("not a getter: a parameter of another type (Stats t)", "Text(t.total)", null, false);
        L10nKeyOf("not a getter: a call on the translation (toUpperCase)", "Text(context.l10n.loginTitle.toUpperCase())", null, false);
        L10nKeyOf("not a getter: widget.title", "Text(widget.title)", null, false);
        var (gl, gc) = At(L10nScreen, "Text(context.l10n.loginTitle)");
        var gi = FlutterCodeEdit.Inspect(L10nScreen, gl, gc);
        Check("getter: the Text itself is not typed over in the code (the .arb is)", gi is { CanText: false, TextNote: "studio.fedit.text.l10n" });
        Check("getter: SetText on the Dart code still refuses it", FlutterCodeEdit.SetText(L10nScreen, gl, gc, "x").Error == "studio.fedit.text.l10n");

        // ── an app's gen-l10n setup ──
        string app = Path.Combine(temp, "l10n-app");
        W(app, "pubspec.yaml", "name: cxapp\ndependencies:\n  flutter:\n    sdk: flutter\nflutter:\n  generate: true\n");
        W(app, "l10n.yaml", "arb-dir: lib/l10n\ntemplate-arb-file: app_th.arb\noutput-localization-file: app_localizations.dart\noutput-class: AppL10n\nnullable-getter: false\noutput-dir: lib/gen/l10n\n");
        W(app, "lib/l10n/app_th.arb", ThArb);
        W(app, "lib/l10n/app_en.arb", EnArb);
        W(app, "lib/login_screen.dart", L10nScreen);
        var cfg = FlutterL10n.Load(app);
        string arbTh = Path.Combine(app, "lib", "l10n", "app_th.arb"), arbEn = Path.Combine(app, "lib", "l10n", "app_en.arb");
        Check("l10n.yaml: arb-dir, the template, the output class and folder", cfg != null && cfg.ArbFiles.Count == 2 && cfg.OutputClass == "AppL10n"
              && SamePath(cfg.TemplatePath, arbTh) && SamePath(cfg.OutputDir, Path.Combine(app, "lib", "gen", "l10n")), $"{cfg?.OutputClass} {cfg?.OutputDir} {cfg?.TemplatePath}");
        Check("l10n.yaml: the generated class's import", cfg?.ImportUri("cxapp") == "package:cxapp/gen/l10n/app_localizations.dart", cfg?.ImportUri("cxapp") ?? "");
        Check("language: the UI language's file — by @@locale, or by the file's name", SamePath(cfg?.ArbFor("th"), arbTh) && SamePath(cfg?.ArbFor("en"), arbEn));
        Check("language: a language the app lacks → Thai first", SamePath(cfg?.ArbFor("ja"), arbTh));
        var r1 = cfg?.Resolve("loginTitle", "th", out _);
        Check("resolve: the Thai text, written to app_th.arb", r1 is { Shown: "เข้าสู่ระบบ", FromTemplate: false } && SamePath(r1.ArbPath, arbTh), $"{r1?.Shown} {r1?.ArbPath}");
        var r2 = cfg?.Resolve("loginTitle", "en", out _);
        Check("resolve: the English text, written to app_en.arb", r2 is { Shown: "Sign In" } && SamePath(r2.ArbPath, arbEn));
        var r3 = cfg?.Resolve("loginButton", "en", out _);
        Check("resolve: not translated into English — the template's text shows, a change adds it to app_en.arb",
              r3 is { Shown: "เข้าสู่ระบบอย่างปลอดภัย", FromTemplate: true } && SamePath(r3.ArbPath, arbEn) && r3.TemplateKeys?.Count == 6);
        string? why = null;
        Check("resolve: a placeholder message is refused with its reason", cfg?.Resolve("appVersion", "th", out why) == null && why == "studio.fedit.text.l10nPlaceholder", why ?? "");
        Check("resolve: a plural message too", cfg?.Resolve("items", "th", out why) == null && why == "studio.fedit.text.l10nPlaceholder", why ?? "");
        Check("resolve: a key no .arb has", cfg?.Resolve("nope", "th", out why) == null && why == "studio.fedit.text.l10nMissing", why ?? "");
        Check("files: the generated Dart is gen-l10n's, the .arb files are sources",
              cfg != null && cfg.IsGenerated(Path.Combine(app, "lib", "gen", "l10n", "app_localizations_th.dart")) && cfg.IsGenerated(Path.Combine(app, "lib", "gen", "l10n", "app_localizations.dart"))
              && !cfg.IsGenerated(Path.Combine(app, "lib", "gen", "l10n", "routes.dart")) && !cfg.IsGenerated(arbTh) && cfg.IsArb(arbTh) && !cfg.IsArb(Path.Combine(app, "lib", "login_screen.dart")));
        var setup = SetupOf(app);
        Check("render: the test imports the generated class from output-dir", setup.Class == "AppL10n" && setup.Import == "package:cxapp/gen/l10n/app_localizations.dart", $"{setup.Class} {setup.Import}");

        // Flutter 3.32+ (this PC has 3.41): no synthetic package — without output-dir the code goes next to the .arb files
        string modern = Path.Combine(temp, "l10n-modern");
        W(modern, "pubspec.yaml", "name: demo\ndependencies:\n  flutter:\n    sdk: flutter\nflutter:\n  generate: true\n");
        W(modern, "l10n.yaml", "arb-dir: lib/l10n\ntemplate-arb-file: app_en.arb\n");
        W(modern, "lib/l10n/app_en.arb", "{\n  \"title\": \"Hello\"\n}\n");
        W(modern, "lib/main.dart", "void main() {}\n");
        var mcfg = FlutterL10n.Load(modern);
        Check("l10n.yaml: no output-dir — the code is next to the .arb files (no package:flutter_gen)",
              mcfg != null && SamePath(mcfg.OutputDir, Path.Combine(modern, "lib", "l10n")) && mcfg.ImportUri("demo") == "package:demo/l10n/app_localizations.dart" && SetupOf(modern).Import == "package:demo/l10n/app_localizations.dart",
              $"{mcfg?.OutputDir} {mcfg?.ImportUri("demo")} {SetupOf(modern).Import}");

        // ── when the renderer makes the translations again (dry run: it says it would run, nothing starts) ──
        string gen = Path.Combine(app, "lib", "gen", "l10n", "app_localizations.dart");
        W(app, "lib/gen/l10n/app_localizations.dart", "// generated\n");
        File.SetLastWriteTimeUtc(arbTh, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(arbEn, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(Path.Combine(app, "l10n.yaml"), DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(gen, DateTime.UtcNow.AddMinutes(-5));
        Check("gen: the generated code is newer than every .arb — up to date", FlutterL10n.Load(app)!.GeneratedUpToDate());
        var g0 = EnsureL10n(app);
        Check("gen: the first draw with up-to-date code runs nothing", g0.Error == null && g0.Failure == null && !g0.Ran && File.Exists(Path.Combine(app, ".dart_tool", "cluadex_studio", "l10n.stamp")), $"{g0.Error} {g0.Failure}");
        var g1 = EnsureL10n(app);
        Check("gen: nothing changed since the last draw — nothing runs", g1.Error == null && !g1.Ran);
        File.WriteAllText(arbTh, e1.Text!);
        var g2 = EnsureL10n(app);
        Check("gen: an .arb changed since the last draw → flutter gen-l10n would run (stopped by the dry run here)", g2.Error == "studio.render.dryRun", $"{g2.Error} {g2.Failure}");
        File.WriteAllText(arbTh, ThArb);
        var g3 = EnsureL10n(app);
        Check("gen: back to what the last draw had — nothing runs", g3.Error == null && !g3.Ran, $"{g3.Error}");
        File.Delete(Path.Combine(app, ".dart_tool", "cluadex_studio", "l10n.stamp"));
        File.Delete(gen);
        Check("gen: the first draw with no generated code at all → it would run", EnsureL10n(app).Error == "studio.render.dryRun");
        Check("gen: an app without l10n.yaml is left alone", EnsureL10n(Path.Combine(temp, "l10n-none")).Error == null);
    }

    static void ArbExact(string name, ArbEdit e, string expected)
    {
        if (!e.Ok || e.Text == null) { Check(name, false, "failed: " + e.Error); return; }
        bool ok = e.Text == expected && StrictJson(e.Text) != null;
        Check(name, ok, ok ? "" : "GOT:\n" + e.Text + "\nEXPECTED:\n" + expected);
    }

    static JsonElement? StrictJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Every member but <paramref name="key"/> has the same value, in the same order.</summary>
    static bool SameExcept(string a, string b, string key)
    {
        if (StrictJson(a) is not { } ja || StrictJson(b) is not { } jb) return false;
        var pa = ja.EnumerateObject().Where(p => p.Name != key).Select(p => p.Name + "=" + p.Value.GetRawText()).ToList();
        var pb = jb.EnumerateObject().Where(p => p.Name != key).Select(p => p.Name + "=" + p.Value.GetRawText()).ToList();
        return pa.SequenceEqual(pb);
    }

    static void L10nKeyOf(string name, string marker, string? key, bool call)
    {
        var (l, c) = At(L10nScreen, marker);
        var info = FlutterCodeEdit.Inspect(L10nScreen, l, c);
        bool ok = info != null && info.L10nKey == key && info.L10nCall == call && !info.CanText;
        Check(name, ok, $"key={info?.L10nKey ?? "null"} call={info?.L10nCall} note={info?.TextNote}");
    }

    static bool SamePath(string? a, string? b) => a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the renderer's generated test imports for the app's translations.</summary>
    static (string? Class, string? Import) SetupOf(string root)
    {
        var p = FlutterProject.Load(root, "th");
        var setup = typeof(FlutterRenderer).GetMethod("ReadSetup", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { p, "th" })!;
        var t = setup.GetType();
        return ((string?)t.GetField("L10nClass")!.GetValue(setup), (string?)t.GetField("L10nImport")!.GetValue(setup));
    }

    /// <summary>The renderer's gen-l10n step (internal) on a folder; flutter.bat is only a name here — dry-run stops it.</summary>
    static (bool Ran, string? Error, string? Failure) EnsureL10n(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        if (!File.Exists(Path.Combine(root, "pubspec.yaml"))) W(root, "pubspec.yaml", "name: none\ndependencies:\n  flutter:\n    sdk: flutter\n");
        var p = FlutterProject.Load(root, "th");
        var m = typeof(FlutterRenderer).GetMethod("EnsureL10nAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)m.Invoke(null, new object?[] { p, "flutter.bat", CancellationToken.None, null })!;
        task.GetAwaiter().GetResult();
        object r = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var t = r.GetType();
        return ((bool)t.GetProperty("Ran")!.GetValue(r)!, (string?)t.GetProperty("Error")!.GetValue(r), (string?)t.GetProperty("Failure")!.GetValue(r));
    }
}
