using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>Where one widget written in the project sits on a rendered screen (logical pixels).</summary>
public sealed record WidgetBox(string Widget, string File, int Line, int Column, double X, double Y, double W, double H, int Depth);

/// <summary>One screen as Flutter drew it.</summary>
public sealed class ScreenRender
{
    public string PageId { get; init; } = "";
    /// <summary>The PNG Flutter wrote (null when the screen could not be built or drawn).</summary>
    public string? PngPath { get; set; }
    public List<WidgetBox> Boxes { get; } = new();
    /// <summary>What went wrong while drawing: overflow, an exception in build() — what the save check reports.</summary>
    public List<string> Problems { get; } = new();
    /// <summary>Expected in a renderer with no device and no network (plugins, HTTP) — kept out of the report.</summary>
    public int Noise { get; set; }
    /// <summary>Why it was not drawn at all (a constructor value the renderer can't make up).</summary>
    public string? Skipped { get; set; }
    public long Millis { get; set; }
}

/// <summary>One finding of `dart analyze` (Severity: ERROR, WARNING or INFO; Code: the analyzer's, e.g. UNDEFINED_IDENTIFIER).</summary>
public sealed record AnalyzeIssue(string Severity, string Code, string File, int Line, int Column, string Message);

public sealed class AnalyzeResult
{
    public List<AnalyzeIssue> Issues { get; } = new();
    /// <summary>Why the analyzer did not run (a key), null when it did.</summary>
    public string? Error { get; set; }
}

/// <summary>One run of the renderer.</summary>
public sealed class RenderRun
{
    public bool Ok { get; set; }
    public List<ScreenRender> Screens { get; } = new();
    /// <summary>The app did not compile (with the compiler's message) — the save check reports it as is.</summary>
    public string? CompileError { get; set; }
    /// <summary>A reason it did not run (no Flutter SDK, refused by the safety check, dry-run).</summary>
    public string? Error { get; set; }
    public TimeSpan Took { get; set; }
}

/// <summary>
/// Draws a Flutter app's REAL screens with Flutter itself: a generated widget test (it lives in
/// <c>.dart_tool/cluadex_studio/</c>, never in the sources) pumps each screen inside the app's own theme,
/// translations and router, then saves a PNG and the place of every widget the project wrote (file:line:column from
/// the widget inspector, the same data DevTools uses) — so a click on the picture opens the exact line.
/// It runs under <c>flutter test</c>: no device, no window, and no network (the test binding answers every HTTP
/// request with 400 and this renderer answers every plugin call with an empty reply), so nothing the app does while
/// drawing reaches a server or the phone's storage.
/// </summary>
public static class FlutterRenderer
{
    public const string Folder = ".dart_tool/cluadex_studio";
    public const string TestFile = "render_test.dart";

    // ───────────────────────────── the app's setup ─────────────────────────────

    /// <summary>How the generated test builds the app around a screen.</summary>
    internal sealed class Setup
    {
        public string Package = "";
        public string Theme = "ThemeData(useMaterial3: true)";
        public List<string> Imports = new();
        public string? L10nClass;
        public string? L10nImport;
        public bool Riverpod, GoRouter, SharedPrefs, Localizations;
        public string Language = "en";
    }

    internal static Setup ReadSetup(FlutterProject p, string language)
    {
        var s = new Setup { Language = language };
        string pubspec = WebPages.SafeRead(Path.Combine(p.Root, "pubspec.yaml"), 300_000);
        s.Package = Regex.Match(pubspec, @"(?m)^name:\s*(?<n>[A-Za-z_][\w]*)").Groups["n"].Value;
        s.Riverpod = Regex.IsMatch(pubspec, @"(?m)^\s+(flutter_riverpod|hooks_riverpod):");
        s.GoRouter = Regex.IsMatch(pubspec, @"(?m)^\s+go_router:");
        s.SharedPrefs = Regex.IsMatch(pubspec, @"(?m)^\s+shared_preferences:");
        s.Localizations = Regex.IsMatch(pubspec, @"(?m)^\s+flutter_localizations:");

        // translations: the gen-l10n class, imported from where gen-l10n writes it (output-dir, else next to the .arb
        // files — Flutter 3.32+ has no synthetic package — else an older Flutter's package:flutter_gen)
        if (FlutterL10n.Load(p.Root) is { } tr)
        {
            s.L10nImport = tr.ImportUri(s.Package);
            s.L10nClass = s.L10nImport != null ? tr.OutputClass : null;
        }
        else if (Regex.IsMatch(pubspec, @"(?m)^\s+generate:\s*true") && File.Exists(Path.Combine(p.Root, ".dart_tool", "flutter_gen", "gen_l10n", "app_localizations.dart")))
        {
            s.L10nClass = "AppLocalizations";
            s.L10nImport = "package:flutter_gen/gen_l10n/app_localizations.dart";
        }

        // the theme: what the app's own widget tests use (they already build screens outside the app), else main.dart's
        // when it only names project declarations, else a plain Material 3 theme in the app's colour
        if (ThemeFromTests(p, s.Package) is { } fromTests)
        {
            s.Theme = fromTests.Expr;
            s.Imports.AddRange(fromTests.Imports);
        }
        else
        {
            uint a = p.Theme.Accent;
            s.Theme = $"ThemeData(useMaterial3: true, colorSchemeSeed: const Color(0x{a:X8}), brightness: Brightness.{(p.Theme.Dark ? "dark" : "light")})";
        }
        return s;
    }

    private static (string Expr, List<string> Imports)? ThemeFromTests(FlutterProject p, string package)
    {
        string test = Path.Combine(p.Root, "test");
        if (!Directory.Exists(test) || package.Length == 0) return null;
        foreach (var f in SourceSearch.ProjectFiles(test, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dart" }, 400))
        {
            string text = WebPages.SafeRead(f, 400_000);
            var m = Regex.Match(text, @"MaterialApp(?:\.router)?\s*\([^;]*?\btheme\s*:\s*");
            if (!m.Success) continue;
            string? expr = ArgText(text, m.Index + m.Length);
            if (expr == null || expr.Length > 300 || Regex.IsMatch(expr, @"\bGoogleFonts\b")) continue;
            var imports = Regex.Matches(text, @"(?m)^import\s+'(?<u>package:" + Regex.Escape(package) + @"/[^']+)'\s*;")
                .Select(x => x.Groups["u"].Value).ToList();
            return (expr.Trim(), imports);
        }
        return null;
    }

    /// <summary>The text of one argument starting at <paramref name="start"/>, up to the comma or bracket that ends it.</summary>
    internal static string? ArgText(string s, int start)
    {
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\'' or '"')
            {
                int e = s.IndexOf(c, i + 1);
                if (e < 0) return null;
                i = e;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) return s[start..i];
                depth--;
            }
            else if (c == ',' && depth == 0) return s[start..i];
        }
        return null;
    }

    // ───────────────────────────── the screens ─────────────────────────────

    /// <summary>The Dart expression that builds the screen with made-up values for its required parameters, or null
    /// (with the reason) when one of them has a type the renderer can't invent.</summary>
    internal static string? CtorFor(FlutterProject p, DartClass cls, string prefix, out string? why)
    {
        why = null;
        var args = new List<string>();
        foreach (var prm in cls.Params.OrderBy(x => x.Named ? 1 : 0).ThenBy(x => x.Index))
        {
            if (!prm.Required) continue;
            string type = cls.FieldTypes.TryGetValue(prm.Name, out var t) ? t : "";
            string? value = SampleValue(p, type);
            if (value == null) { why = $"{prm.Name}: {(type.Length > 0 ? type : "?")}"; return null; }
            args.Add(prm.Named ? $"{prm.Name}: {value}" : value);
        }
        return $"{prefix}.{cls.Name}({string.Join(", ", args)})";
    }

    private static string? SampleValue(FlutterProject p, string type)
    {
        string t = type.Replace(" ", "");
        if (t.EndsWith('?')) return "null";
        switch (t)
        {
            case "int" or "num": return "1";
            case "double": return "1.0";
            case "String": return "'1'";
            case "bool": return "false";
            case "DateTime": return "DateTime(2026, 1, 1)";
            case "Duration": return "Duration.zero";
            case "Color": return "const Color(0xFF888888)";
            case "VoidCallback" or "voidFunction()" or "GestureTapCallback": return "() {}";
        }
        if (t.StartsWith("ValueChanged<", StringComparison.Ordinal) || t.StartsWith("ValueSetter<", StringComparison.Ordinal)) return "(_) {}";
        if (t.StartsWith("List<", StringComparison.Ordinal) || t.StartsWith("Iterable<", StringComparison.Ordinal) || t == "List") return "const []";
        if (t.StartsWith("Map<", StringComparison.Ordinal) || t.StartsWith("Set<", StringComparison.Ordinal)) return "const {}";
        return null;   // a type of the app's own (a model, an enum): no honest sample value — the screen is listed as not drawn
    }

    // ───────────────────────────── the generated test ─────────────────────────────

    internal static string Generate(Setup s, IReadOnlyList<(int Id, string Import, string Ctor)> screens, string outDir, int width, int height)
    {
        var imp = new SortedSet<string>(StringComparer.Ordinal)
        {
            "dart:async", "dart:convert", "dart:io", "package:flutter/material.dart", "package:flutter/rendering.dart",
            "package:flutter/services.dart", "package:flutter_test/flutter_test.dart",
        };
        if (s.Riverpod) imp.Add("package:flutter_riverpod/flutter_riverpod.dart");
        if (s.GoRouter) imp.Add("package:go_router/go_router.dart");
        if (s.SharedPrefs) imp.Add("package:shared_preferences/shared_preferences.dart");
        if (s.L10nImport != null) imp.Add(s.L10nImport);
        foreach (var i in s.Imports) imp.Add(i);

        var sb = new StringBuilder();
        sb.AppendLine("// Generated by CluadeX UI Studio to draw this app's screens for its page gallery. Safe to delete.");
        sb.AppendLine("// It lives in .dart_tool (never in the sources) and runs under `flutter test`: no device, no network.");
        sb.AppendLine("// ignore_for_file: type=lint");
        foreach (var i in imp) sb.AppendLine($"import '{i}';");
        sb.AppendLine("import 'dart:ui' as ui;");
        foreach (var (id, import, _) in screens) sb.AppendLine($"import '{import}' as s{id};");
        sb.AppendLine();
        sb.AppendLine($"const _out = r'{outDir.TrimEnd('\\', '/')}';");
        sb.AppendLine($"const _size = Size({width}, {height});");
        sb.Append(Runtime);
        string l10n = s.L10nClass != null
            ? $"localizationsDelegates: {s.L10nClass}.localizationsDelegates, supportedLocales: {s.L10nClass}.supportedLocales, locale: const Locale('{s.Language}'),"
            : "";
        string app = s.GoRouter
            ? $"MaterialApp.router(debugShowCheckedModeBanner: false, theme: _withFallback({s.Theme}), {l10n} routerConfig: GoRouter(routes: [GoRoute(path: '/', builder: (_, __) => RepaintBoundary(key: key, child: screen()))]))"
            : $"MaterialApp(debugShowCheckedModeBanner: false, theme: _withFallback({s.Theme}), {l10n} home: RepaintBoundary(key: key, child: screen()))";
        if (s.Riverpod) app = $"ProviderScope(child: {app})";
        sb.AppendLine();
        sb.AppendLine("Widget _app(GlobalKey key, Widget Function() screen) => " + app + ";");
        sb.AppendLine();
        sb.AppendLine("void main() {");
        sb.AppendLine("  setUpAll(_fonts);");
        if (s.SharedPrefs) sb.AppendLine("  setUp(() => SharedPreferences.setMockInitialValues(<String, Object>{}));");
        foreach (var (id, _, ctor) in screens)
            sb.AppendLine($"  testWidgets('cx{id}', (t) => _shot(t, {id}, () => s{id}.{ctor.Substring(ctor.IndexOf('.') + 1)}));");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // The part of the test that never changes: fonts, the quiet platform, the shot and the widget map.
    private const string Runtime = """

Future<void> _fonts() async {
  final have = <String>{};
  try {
    final manifest = json.decode(await rootBundle.loadString('FontManifest.json')) as List<dynamic>;
    for (final f in manifest) {
      final loader = FontLoader(f['family'] as String);
      have.add(f['family'] as String);
      for (final a in (f['fonts'] as List<dynamic>)) {
        loader.addFont(rootBundle.load(a['asset'] as String));
      }
      await loader.load();
    }
  } catch (_) {}
  await _systemFonts(have);
}

// Flutter's test engine has no system fonts: the default family (Roboto) and every letter the app's own fonts lack
// would be drawn as empty boxes. The PC's UI font stands in for them (Leelawadee UI has Thai and Latin).
Future<void> _systemFonts(Set<String> have) async {
  try {
    final dir = '${Platform.environment['WINDIR'] ?? r'C:\Windows'}${Platform.pathSeparator}Fonts${Platform.pathSeparator}';
    List<String> files = const [];
    for (final set in const [['LeelawUI.ttf', 'LeelaUIb.ttf', 'LeelUIsl.ttf'], ['tahoma.ttf', 'tahomabd.ttf'], ['segoeui.ttf', 'segoeuib.ttf']]) {
      files = set.where((f) => File(dir + f).existsSync()).toList();
      if (files.isNotEmpty) break;
    }
    if (files.isEmpty) return;
    for (final family in const ['Roboto', 'CxFallback']) {
      if (have.contains(family)) continue;
      final loader = FontLoader(family);
      for (final f in files) {
        loader.addFont(Future.value(ByteData.sublistView(File(dir + f).readAsBytesSync())));
      }
      await loader.load();
    }
  } catch (_) {}
}

/// The app's theme, with the stand-in font behind every text style (a letter its fonts lack, or a font that can't
/// load here — a Google Font fetched at runtime — is drawn in it instead of as a box).
ThemeData _withFallback(ThemeData? theme) {
  final t = theme ?? ThemeData();
  return t.copyWith(
    textTheme: t.textTheme.apply(fontFamilyFallback: const ['CxFallback']),
    primaryTextTheme: t.primaryTextTheme.apply(fontFamilyFallback: const ['CxFallback']),
  );
}

ByteData? _empty(ByteData? message) {
  try {
    const StandardMethodCodec().decodeMethodCall(message);
    return const StandardMethodCodec().encodeSuccessEnvelope(null);
  } catch (_) {
    return const StandardMessageCodec().encodeMessage(<Object?>[null]);
  }
}

String _first(Object e) => e.toString().split('\n').first.trim();

bool _noise(String m) =>
    m.contains('MissingPluginException') || m.contains('PlatformException') || m.contains('SocketException') ||
    m.contains('HttpException') || m.contains('DioException') || m.contains('DioError') || m.contains('connection on channel') ||
    m.contains('HTTP request failed') || m.contains('statusCode: 400') || m.contains('status code of 400') ||
    m.contains('ClientException') || m.contains('Failed host lookup') || m.contains('A Timer is still pending');

Future<void> _shot(WidgetTester t, int id, Widget Function() screen) async {
  final problems = <String>[];
  var noise = 0;
  void add(String m) {
    if (m.isEmpty) return;
    if (_noise(m)) {
      noise++;
    } else if (!problems.contains(m)) {
      problems.add(m);
    }
  }
  final old = FlutterError.onError;
  FlutterError.onError = (d) => add(_first(d.exception));
  final messenger = t.binding.defaultBinaryMessenger;
  messenger.allMessagesHandler = (channel, handler, message) {
    if (handler != null) return handler(message);
    if (channel.startsWith('flutter/')) return messenger.delegate.send(channel, message);
    return Future<ByteData?>.value(_empty(message));
  };
  final sw = Stopwatch()..start();
  await runZonedGuarded(() async {
    final key = GlobalKey();
    await t.binding.setSurfaceSize(_size);
    await t.pumpWidget(_app(key, screen));
    for (var i = 0; i < 4; i++) {
      await t.pump(const Duration(milliseconds: 100));
    }
    // pictures decode on real time, outside the test's fake clock
    await t.runAsync(() async {
      for (final e in find.byType(Image).evaluate().toList()) {
        try {
          await precacheImage((e.widget as Image).image, e);
        } catch (_) {}
      }
      await Future<void>.delayed(const Duration(milliseconds: 120));
    });
    for (var i = 0; i < 3; i++) {
      await t.pump(const Duration(milliseconds: 100));
    }
    final boxes = <Map<String, Object?>>[];
    final svc = WidgetInspectorService.instance;
    try {
      final tree = json.decode(svc.getRootWidgetSummaryTree('cx')) as Map<String, dynamic>;
      void walk(Map<String, dynamic> n, int depth) {
        final loc = n['creationLocation'] as Map<String, dynamic>?;
        if (loc != null && n['createdByLocalProject'] == true) {
          final obj = svc.toObject(n['valueId'] as String?, 'cx');
          final ro = obj is Element ? obj.renderObject : null;
          if (ro is RenderBox && ro.attached && ro.hasSize) {
            final o = ro.localToGlobal(Offset.zero);
            boxes.add({'w': n['description'], 'f': loc['file'], 'l': loc['line'], 'c': loc['column'],
              'x': o.dx, 'y': o.dy, 'wd': ro.size.width, 'ht': ro.size.height, 'd': depth});
          }
        }
        for (final k in (n['children'] as List<dynamic>? ?? const <dynamic>[])) {
          walk(k as Map<String, dynamic>, depth + 1);
        }
      }
      walk(tree, 0);
    } catch (e) {
      add('widget map: ${_first(e)}');
    }
    svc.disposeGroup('cx');
    await t.runAsync(() async {
      final b = key.currentContext?.findRenderObject();
      if (b is RenderRepaintBoundary) {
        final img = await b.toImage(pixelRatio: 2);
        final bytes = await img.toByteData(format: ui.ImageByteFormat.png);
        File('$_out${Platform.pathSeparator}$id.png').writeAsBytesSync(bytes!.buffer.asUint8List());
      }
      File('$_out${Platform.pathSeparator}$id.json')
          .writeAsStringSync(json.encode({'ms': sw.elapsedMilliseconds, 'problems': problems, 'noise': noise, 'boxes': boxes}));
    });
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(seconds: 1));
  }, (e, s) => add(_first(e)));
  FlutterError.onError = old;
}

""";

    // ───────────────────────────── running it ─────────────────────────────

    /// <summary>Where the Flutter SDK's launcher is: FLUTTER_ROOT, then PATH. Null when Flutter isn't installed.</summary>
    public static string? FindFlutter()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("FLUTTER_ROOT") is { Length: > 0 } fr) candidates.Add(Path.Combine(fr, "bin", "flutter.bat"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), "flutter.bat")); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Draw these screens. Each gets its PNG + widget map in <paramref name="outDir"/> (named by its index in
    /// the list). Runs the app's own code under <c>flutter test</c>, through the same always-on safety check as every
    /// command CluadeX runs.</summary>
    public static async Task<RenderRun> RunAsync(FlutterProject p, IReadOnlyList<StudioPage> pages, string outDir, string language,
        CancellationToken ct, Action<string>? log = null, int width = 390, int height = 844)
    {
        var run = new RenderRun();
        var sw = Stopwatch.StartNew();
        var setup = ReadSetup(p, language);
        if (setup.Package.Length == 0) { run.Error = "studio.render.noPackage"; return run; }
        var list = new List<(int, string, string)>();
        string libRoot = Path.GetFullPath(Path.Combine(p.Root, "lib")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var sr = new ScreenRender { PageId = page.Id };
            run.Screens.Add(sr);
            if (page.Detail == null || p.ClassOf(page.Detail) is not { } cls || !cls.File.Path.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase))
            {
                sr.Skipped = "?";
                continue;
            }
            string? ctor = CtorFor(p, cls, "s" + i, out var why);
            if (ctor == null) { sr.Skipped = why; continue; }
            string import = $"package:{setup.Package}/{cls.File.Path[libRoot.Length..].Replace('\\', '/')}";
            list.Add((i, import, ctor));
        }
        if (list.Count == 0) { run.Ok = true; run.Took = sw.Elapsed; return run; }

        string? flutter = FindFlutter();
        if (flutter == null) { run.Error = "studio.render.noFlutter"; return run; }
        // the translations first: `flutter test --no-pub` never makes them, so a changed .arb needs gen-l10n here
        var gen = await EnsureL10nAsync(p, flutter, ct, log);
        if (gen.Error != null) { run.Error = gen.Error; return run; }
        if (gen.Failure != null) { run.CompileError = gen.Failure; run.Took = sw.Elapsed; return run; }
        if (gen.Ran) setup = ReadSetup(p, language);   // the generated class may be somewhere else now

        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.EnumerateFiles(outDir)) { try { File.Delete(f); } catch { } }
        string dir = Path.Combine(p.Root, Folder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        string testPath = Path.Combine(dir, TestFile);
        await File.WriteAllTextAsync(testPath, Generate(setup, list, outDir, width, height), new UTF8Encoding(false), ct);

        bool pubReady = File.Exists(Path.Combine(p.Root, ".dart_tool", "package_config.json"));
        string command = $"flutter test {Folder}/{TestFile} --reporter json" + (pubReady ? " --no-pub" : "");

        // the app's code runs here — the same always-on check as run_command (it reads what changed first)
        if (await Safety.DangerApproval.GateAsync("UI Studio", command, await Safety.CodeRunGuard.CheckAsync(command, p.Root)) is { } unsafeRun)
        {
            run.Error = "studio.err.unsafe";
            log?.Invoke(unsafeRun);
            return run;
        }
        // a harness that wants real pictures names flutter.bat in ProcessLaunchPolicy.Allowed (dry-run stays on for the rest)
        if (Safety.ProcessLaunchPolicy.Blocks(flutter))
        {
            run.Error = "studio.render.dryRun";
            log?.Invoke(Safety.ProcessLaunchPolicy.Report(command));
            return run;
        }

        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + flutter + "\" " + command[("flutter ".Length)..] + "\"")
        {
            WorkingDirectory = p.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        var errors = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (!e.Data.StartsWith('{') && !e.Data.StartsWith('[')) { log?.Invoke(e.Data); return; }
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                string? type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                if (type == "error" && root.TryGetProperty("error", out var err))
                {
                    string msg = err.GetString() ?? "";
                    if (msg.Contains("Failed to load", StringComparison.Ordinal) || msg.Contains("Compilation failed", StringComparison.Ordinal) || msg.Contains("Error: ", StringComparison.Ordinal))
                        lock (errors) if (errors.Length < 8000) errors.AppendLine(msg);
                }
                else if (type == "print" && root.TryGetProperty("message", out var pm)) log?.Invoke(pm.GetString() ?? "");
                else if (type == "testDone" && root.TryGetProperty("hidden", out var h) && !h.GetBoolean()) log?.Invoke("·");
            }
            catch { /* a line that only looks like JSON */ }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
        try
        {
            proc.Start();
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            run.Error = ct.IsCancellationRequested ? "cancelled" : "studio.render.timeout";
            return run;
        }
        catch (Exception ex)
        {
            run.Error = "studio.render.noFlutter";
            log?.Invoke(ex.Message);
            return run;
        }

        // read what each screen left
        for (int i = 0; i < run.Screens.Count; i++)
        {
            var sr = run.Screens[i];
            if (sr.Skipped != null) continue;
            string png = Path.Combine(outDir, i + ".png"), meta = Path.Combine(outDir, i + ".json");
            if (File.Exists(png)) sr.PngPath = png;
            if (!File.Exists(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(meta, ct));
                var r = doc.RootElement;
                sr.Millis = r.TryGetProperty("ms", out var ms) ? ms.GetInt64() : 0;
                sr.Noise = r.TryGetProperty("noise", out var nz) ? nz.GetInt32() : 0;
                if (r.TryGetProperty("problems", out var pr))
                    foreach (var x in pr.EnumerateArray()) if (x.GetString() is { Length: > 0 } m) sr.Problems.Add(m);
                if (r.TryGetProperty("boxes", out var bx))
                    foreach (var b in bx.EnumerateArray())
                    {
                        string file = b.GetProperty("f").GetString() ?? "";
                        if (file.Contains("cluadex_studio", StringComparison.Ordinal)) continue;   // our own test's widgets
                        sr.Boxes.Add(new WidgetBox(b.GetProperty("w").GetString() ?? "", FileFromUri(file), b.GetProperty("l").GetInt32(), b.GetProperty("c").GetInt32(),
                            b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("wd").GetDouble(), b.GetProperty("ht").GetDouble(),
                            b.TryGetProperty("d", out var d) ? d.GetInt32() : 0));
                    }
            }
            catch { /* a half-written file: the screen shows as not drawn */ }
        }
        string compile = errors.ToString();
        if (run.Screens.All(s => s.Skipped != null || s.PngPath == null) && compile.Length > 0) run.CompileError = Trim(compile);
        run.Ok = run.CompileError == null;
        run.Took = sw.Elapsed;
        return run;
    }

    // ───────────────────────────── the app's translations ─────────────────────────────

    /// <summary>The fingerprint of l10n.yaml and the .arb files at the last draw (in the renderer's own folder).</summary>
    internal const string L10nStamp = "l10n.stamp";

    /// <summary>What making the translations before a draw came to. Ran: gen-l10n ran and succeeded. Error: a key — it
    /// could not run (refused, dry run, cancelled). Failure: gen-l10n's report — an .arb file has an error, so the app
    /// can't build either.</summary>
    internal readonly record struct L10nGen(bool Ran, string? Error, string? Failure);

    /// <summary>Bring the app's generated translations up to date for a draw: <c>flutter gen-l10n</c> runs only when an
    /// .arb file (or l10n.yaml) changed since the last draw — and on the first draw here only when the generated code
    /// is missing or older than an .arb. Through the same gates as the draw itself (CodeRunGuard, DangerApproval,
    /// ProcessLaunchPolicy).</summary>
    internal static async Task<L10nGen> EnsureL10nAsync(FlutterProject p, string flutter, CancellationToken ct, Action<string>? log)
    {
        var l10n = FlutterL10n.Load(p.Root);
        if (l10n == null || l10n.ArbFiles.Count == 0) return default;
        string stamp = Path.Combine(p.Root, Folder.Replace('/', Path.DirectorySeparatorChar), L10nStamp);
        string print = l10n.Fingerprint();
        string? last = null;
        try { if (File.Exists(stamp)) last = (await File.ReadAllTextAsync(stamp, ct)).Trim(); } catch (IOException) { }
        if (last == print) return default;
        if (last == null && l10n.GeneratedUpToDate()) { WriteStamp(stamp, print); return default; }

        const string command = "flutter gen-l10n";
        if (await Safety.DangerApproval.GateAsync("UI Studio", command, await Safety.CodeRunGuard.CheckAsync(command, p.Root)) is { } unsafeRun)
        {
            log?.Invoke(unsafeRun);
            return new L10nGen(false, "studio.err.unsafe", null);
        }
        if (Safety.ProcessLaunchPolicy.Blocks(flutter))
        {
            log?.Invoke(Safety.ProcessLaunchPolicy.Report(command));
            return new L10nGen(false, "studio.render.dryRun", null);
        }
        log?.Invoke("flutter gen-l10n — the translations changed since the last draw");
        var (exit, output) = await RunToolAsync(flutter, command["flutter ".Length..], p.Root, TimeSpan.FromMinutes(6), ct);
        if (exit == NotStarted) { log?.Invoke(output); return new L10nGen(false, "studio.render.noFlutter", null); }
        if (exit == null) return new L10nGen(false, ct.IsCancellationRequested ? "cancelled" : "studio.render.timeout", null);
        if (exit != 0) return new L10nGen(false, null, "flutter gen-l10n failed:\n" + Trim(output.Length > 0 ? output : "exit code " + exit));
        WriteStamp(stamp, print);
        return new L10nGen(true, null, null);
    }

    private static void WriteStamp(string path, string print)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, print, new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const int NotStarted = int.MinValue;

    /// <summary>Run one Flutter / Dart tool command in the project (no window, no input). Exit code (null: timed out or
    /// cancelled; <see cref="NotStarted"/>: it could not start) and everything it printed.</summary>
    private static async Task<(int? Exit, string Output)> RunToolAsync(string exe, string args, string cwd, TimeSpan limit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + exe + "\" " + args + "\"")
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        var output = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) if (output.Length < 20000) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) if (output.Length < 20000) output.AppendLine(e.Data); };
        try
        {
            proc.Start();
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return (null, "");
        }
        catch (Exception ex) { return (NotStarted, ex.Message); }
        lock (output) return (proc.ExitCode, output.ToString());
    }

    // ───────────────────────────── the save check's analyzer ─────────────────────────────

    /// <summary>`dart analyze` on files of the app (static analysis: the app's code is read, not run). Issues of every
    /// severity; <see cref="AnalyzeResult.Error"/> says why it could not run.</summary>
    public static async Task<AnalyzeResult> AnalyzeAsync(FlutterProject p, IReadOnlyList<string> files, CancellationToken ct, Action<string>? log = null)
    {
        var result = new AnalyzeResult();
        string? flutter = FindFlutter();
        string? dart = flutter == null ? null : new[] { Path.Combine(Path.GetDirectoryName(flutter)!, "dart.bat"), Path.Combine(Path.GetDirectoryName(flutter)!, "cache", "dart-sdk", "bin", "dart.exe") }.FirstOrDefault(File.Exists);
        if (dart == null) { result.Error = "studio.check.noDart"; return result; }
        string root = p.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rel = files.Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(f)).Select(f => f[root.Length..].Replace('\\', '/')).Distinct().ToList();
        if (rel.Count == 0) return result;
        // a long list would pass the command line's limit: then the whole lib/ (the report is filtered to the files)
        string targets = rel.Count > 40 || rel.Sum(r => r.Length + 3) > 6000 ? "lib" : string.Join(" ", rel.Select(r => "\"" + r + "\""));
        string command = "dart analyze --format=machine " + targets;
        if (await Safety.DangerApproval.GateAsync("UI Studio", command, await Safety.CodeRunGuard.CheckAsync(command, p.Root)) is { } unsafeRun)
        {
            result.Error = "studio.err.unsafe";
            log?.Invoke(unsafeRun);
            return result;
        }
        if (Safety.ProcessLaunchPolicy.Blocks(dart))
        {
            result.Error = "studio.render.dryRun";
            log?.Invoke(Safety.ProcessLaunchPolicy.Report(command));
            return result;
        }
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + dart + "\" " + command["dart ".Length..] + "\"")
        {
            WorkingDirectory = p.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        var lines = new List<string>();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        try
        {
            proc.Start();
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            result.Error = ct.IsCancellationRequested ? "cancelled" : "studio.render.timeout";
            return result;
        }
        catch (Exception ex) { result.Error = "studio.check.noDart"; log?.Invoke(ex.Message); return result; }
        var wanted = new HashSet<string>(files.Select(f => Path.GetFullPath(f)), StringComparer.OrdinalIgnoreCase);
        lock (lines)
            foreach (var line in lines)
            {
                // SEVERITY|TYPE|CODE|FILE|LINE|COLUMN|LENGTH|MESSAGE
                var f = line.Split('|');
                if (f.Length < 8 || f[0] is not ("ERROR" or "WARNING" or "INFO")) { if (line.Trim().Length > 0) log?.Invoke(line); continue; }
                string file;
                try { file = Path.GetFullPath(Path.IsPathRooted(f[3]) ? f[3] : Path.Combine(p.Root, f[3])); } catch { continue; }
                if (!wanted.Contains(file)) continue;
                int.TryParse(f[4], out int ln);
                int.TryParse(f[5], out int col);
                result.Issues.Add(new AnalyzeIssue(f[0], f[2], file, ln, col, string.Join("|", f.Skip(7)).Replace("\\|", "|")));
            }
        return result;
    }

    /// <summary>"file:///D:/x/lib/a.dart" → "D:\x\lib\a.dart".</summary>
    internal static string FileFromUri(string f)
    {
        if (Uri.TryCreate(f, UriKind.Absolute, out var u) && u.IsFile) return u.LocalPath;
        return f.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Trim(string s)
    {
        var lines = s.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).Take(40);
        return string.Join("\n", lines);
    }
}
