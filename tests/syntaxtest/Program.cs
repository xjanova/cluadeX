using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using CluadeX.Services;
using CluadeX.Views.Editor;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace SyntaxTest;

/// <summary>
/// Syntax colouring of the code editor, run against the BUILT CluadeX.dll — the same NeonHighlighting.ForExtension
/// the editor calls, AvalonEdit's real DocumentHighlighter, the real theme dictionaries. Born from GDScript and
/// .gdshader files opening as plain white text.
///   X*  every bundled .xshd is embedded and passes AvalonEdit's schema; each extension resolves to its definition
///   L*  per language: a realistic sample is highlighted and named tokens get the expected colour NAME
///       (func → Keyword, return → ControlKeyword, _make( → Function, AudioStreamWAV → Type, 0.10 → Number …)
///   P*  per theme: every colour name of every bundled definition is re-tinted to the Syntax.* token it belongs to
///       (none left on its xshd fallback), the built-in definitions pick up Function / Control / Variable, the new
///       tokens are readable on the editor surface and distinct from the other syntax colours, and a theme round
///       trip restores the exact colours.
/// No settings, no network, no model. Run: build CluadeX, then `dotnet run -p:CluadeXBin=&lt;OutDir&gt;\`.
/// `-- --shot &lt;dir&gt;` also renders the real editor (GDScript + shader) in each theme to PNGs, to look at.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();

    static string? ShotDir;

    [STAThread]
    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0 && shot + 1 < args.Length) ShotDir = args[shot + 1];
        try { return Run(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + detail.Replace("\n", "\n            "));
    }

    static int Run()
    {
        // ThemeService.ColorOf reads Application.Current's resources — the harness plays the app.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        UseTheme(app, "Midnight");

        Console.WriteLine("\n## X — bundled definitions");
        BundledDefinitions();
        Extensions();

        Console.WriteLine("\n## L — token colours per language");
        GdScript();
        GodotShader();
        GodotResource();
        Jsonc();
        Yaml();
        Toml();
        Shell();
        Dart();

        foreach (string theme in new[] { "Midnight", "Classic", "NeonCyber", "Lavender" })
        {
            Console.WriteLine($"\n## P — palette: {theme}");
            UseTheme(app, theme);
            Palette(theme);
        }
        Console.WriteLine("\n## P — theme round trip");
        RoundTrip(app);

        if (ShotDir != null) Shots(app, ShotDir);

        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL ═══");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════════════════ shots ═══════════════════════════════════════════

    static readonly string GdShot = "extends Node2D\nclass_name SoundBank\n## Synthesised table sounds — no audio files shipped.\n\nsignal played(name: String)\nconst RATE := 22050\n@export var volume_db: float = -6.0\n@onready var player: AudioStreamPlayer = $Player\n\nstatic func _tick() -> AudioStreamWAV:\n\tvar n := int(RATE * 0.10)\n\tvar d := PackedFloat32Array()\n\td.resize(n)\n\tfor i in n:\n\t\tvar t := float(i) / RATE\n\t\td[i] = sin(TAU * 1500.0 * t) * exp(-t * 120.0) * 0.6\n\treturn _make(d)\n\nfunc play(name: String) -> void:\n\tif not player.playing:   # one sound at a time\n\t\tplayer.stream = _tick()\n\t\tplayer.play()\n\tplayed.emit(name)\n";

    static readonly string ShaderShot = "shader_type canvas_item;\n// Felt cloth: soft vignette and weave.\nuniform vec4 cloth : source_color = vec4(0.05, 0.42, 0.22, 1.0);\nuniform float weave : hint_range(0.0, 1.0) = 0.35;\nvarying vec2 wpos;\n\nvoid vertex() {\n\twpos = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;\n}\n\nvoid fragment() {\n\tvec2 p = UV - 0.5;\n\tfloat v = 1.0 - smoothstep(0.35, 0.9, length(p));\n\tif (weave > 0.0) {\n\t\tv *= 0.95 + 0.05 * sin(wpos.x * 3.1) * sin(wpos.y * 2.7);\n\t}\n\tCOLOR = vec4(cloth.rgb * v, 1.0);\n}\n";

    /// <summary>The real AvalonEdit editor, coloured through NeonHighlighting, in each theme — saved as PNGs.</summary>
    static void Shots(Application app, string dir)
    {
        Directory.CreateDirectory(dir);
        var shots = new List<System.Windows.Media.Imaging.BitmapSource>();
        foreach (string theme in new[] { "Midnight", "NeonCyber", "Classic", "Lavender" })
        {
            UseTheme(app, theme);
            var shot = RenderEditors(theme);
            SavePng(shot, Path.Combine(dir, $"syntax-{theme}.png"));
            shots.Add(shot);
        }
        var all = new DrawingVisual();
        using (var dc = all.RenderOpen())
            for (int i = 0; i < shots.Count; i++)
                dc.DrawImage(shots[i], new Rect(0, i * shots[i].PixelHeight, shots[i].PixelWidth, shots[i].PixelHeight));
        var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap(shots[0].PixelWidth, shots.Sum(b => b.PixelHeight), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(all);
        SavePng(sheet, Path.Combine(dir, "syntax-all-themes.png"));
        Console.WriteLine($"\n## shots → {dir}");
    }

    static System.Windows.Media.Imaging.BitmapSource RenderEditors(string theme)
    {
        const int W = 1400, H = 400;   // shorter than the samples: the scroll bars show
        var surface = ThemeService.BrushOf("Editor.Background", Brushes.Transparent);
        if (surface is SolidColorBrush { Color.A: < 0x80 }) surface = ThemeService.BrushOf("Bg.Window", Brushes.Black);
        var grid = new System.Windows.Controls.Grid { Width = W, Height = H, Background = surface };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        var title = new System.Windows.Controls.TextBlock
        {
            Text = $"{theme} — snd.gd · cloth.gdshader", Margin = new Thickness(12, 8, 0, 4), FontSize = 13,
            Foreground = ThemeService.BrushOf("Text.Muted", Brushes.Gray),
        };
        System.Windows.Controls.Grid.SetColumnSpan(title, 2);
        grid.Children.Add(title);
        grid.Children.Add(ShotEditor(GdShot, ".gd", 0, surface));
        grid.Children.Add(ShotEditor(ShaderShot, ".gdshader", 1, surface));
        grid.Measure(new Size(W, H));
        grid.Arrange(new Rect(0, 0, W, H));
        grid.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(grid);
        return bmp;
    }

    static FrameworkElement ShotEditor(string text, string ext, int column, Brush surface)
    {
        var editor = new ICSharpCode.AvalonEdit.TextEditor
        {
            Text = text,
            SyntaxHighlighting = NeonHighlighting.ForExtension(ext),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            ShowLineNumbers = true,
            Background = surface,
            Foreground = ThemeService.BrushOf("Editor.Foreground", Brushes.White),
            LineNumbersForeground = ThemeService.BrushOf("Editor.LineNumber", Brushes.Gray),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Visible,
            Margin = new Thickness(8, 0, 8, 8),
        };
        editor.Options.IndentationSize = 4;
        System.Windows.Controls.Grid.SetRow(editor, 1);
        System.Windows.Controls.Grid.SetColumn(editor, column);
        return editor;
    }

    static void SavePng(System.Windows.Media.Imaging.BitmapSource bmp, string path)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void UseTheme(Application app, string id)
    {
        var dict = new ResourceDictionary { Source = ThemeService.DictionaryUri(id) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict); else merged[0] = dict;
    }

    // ═══════════════════════════════════════════ X ═══════════════════════════════════════════

    static readonly string[] BundledNames = { "GDScript", "Godot Shader", "Godot Resource", "JSONC", "YAML", "TOML", "Shell", "Dart" };

    static void BundledDefinitions()
    {
        var asm = typeof(NeonHighlighting).Assembly;
        var resources = asm.GetManifestResourceNames().Where(n => n.StartsWith("CluadeX.Resources.Syntax.") && n.EndsWith(".xshd")).OrderBy(n => n).ToList();
        Check("X1", $"8 .xshd files embedded as CluadeX.Resources.Syntax.*", resources.Count == 8, string.Join(", ", resources));
        foreach (var res in resources)
        {
            // Load each one the strict way (schema-validated) so a broken file names its own error here; the app
            // swallows it and falls back to plain text.
            string error = null, name = null;
            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                using var reader = XmlReader.Create(stream);
                name = HighlightingLoader.Load(reader, HighlightingManager.Instance).Name;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " → " + ex.InnerException.Message : ""); }
            Check("X2", $"{res["CluadeX.Resources.Syntax.".Length..]} loads (schema-valid) as '{name}'", error == null && BundledNames.Contains(name), error ?? name);
        }
    }

    static void Extensions()
    {
        var expect = new (string Ext, string Name)[]
        {
            (".gd", "GDScript"), (".gdshader", "Godot Shader"), (".gdshaderinc", "Godot Shader"),
            (".tscn", "Godot Resource"), (".tres", "Godot Resource"), (".godot", "Godot Resource"),
            (".import", "Godot Resource"), (".cfg", "Godot Resource"), (".json", "JSONC"), (".jsonc", "JSONC"),
            (".yml", "YAML"), (".yaml", "YAML"), (".toml", "TOML"), (".sh", "Shell"), (".bash", "Shell"), (".zsh", "Shell"), (".dart", "Dart"),
            ("GD", "GDScript"),
            // the built-ins still resolve as before
            (".cs", "C#"), (".py", "Python"), (".ts", "JavaScript"), (".xaml", "XML"), (".ps1", "PowerShell"),
        };
        foreach (var (ext, name) in expect)
        {
            var def = NeonHighlighting.ForExtension(ext);
            Check("X3", $"{ext} → {name}", def?.Name == name, def?.Name ?? "null (plain text)");
        }
        Check("X3", ".txt stays plain text", NeonHighlighting.ForExtension(".txt") == null);
    }

    // ═══════════════════════════════════════════ L ═══════════════════════════════════════════

    /// <summary>(anchor = a unique snippet of the sample, token = the part of it to look at, expected colour name;
    /// "" = plain text, no colour at all).</summary>
    record struct T(string Anchor, string Token, string Expect)
    {
        public static implicit operator T((string a, string t, string e) x) => new(x.a, x.t, x.e);
    }

    static void Lang(string id, string ext, string sample, params T[] checks)
    {
        var def = NeonHighlighting.ForExtension(ext);
        if (def == null) { Check(id, $"{ext}: a definition", false, "ForExtension returned null"); return; }
        var doc = new TextDocument(sample);
        var highlighter = new DocumentHighlighter(doc, def);
        Console.WriteLine($"  -- {def.Name} ({ext})");
        foreach (var c in checks)
        {
            int at = sample.IndexOf(c.Anchor, StringComparison.Ordinal);
            int inAnchor = c.Anchor.IndexOf(c.Token, StringComparison.Ordinal);
            if (at < 0 || inAnchor < 0) { Check(id, $"{Show(c.Token)} (sample)", false, $"anchor {Show(c.Anchor)} not found"); continue; }
            var (got, detail) = ColorAt(doc, highlighter, at + inAnchor, c.Token.Length);
            string want = c.Expect.Length == 0 ? "(plain)" : c.Expect;
            Check(id, $"{Show(c.Token)} → {want}", (got ?? "(plain)") == want, $"got {got ?? "(plain)"}{detail}");
        }
    }

    /// <summary>The colour name over [offset, offset+length): the innermost named section covering all of it.
    /// A different colour starting or ending inside the token means the rule cut it — reported, never a pass.</summary>
    static (string Name, string Detail) ColorAt(TextDocument doc, DocumentHighlighter highlighter, int offset, int length)
    {
        var line = doc.GetLineByOffset(offset);
        var hl = highlighter.HighlightLine(line.LineNumber);
        var named = hl.Sections.Where(s => s.Color?.Name != null).ToList();
        HighlightedSection best = null;
        foreach (var s in named)
            if (s.Offset <= offset && s.Offset + s.Length >= offset + length && (best == null || s.Length <= best.Length)) best = s;
        var cuts = named.Where(s => s != best && s.Offset < offset + length && s.Offset + s.Length > offset
                                    && !(s.Offset <= offset && s.Offset + s.Length >= offset + length)
                                    && s.Color.Name != best?.Color.Name).ToList();
        string dump = "\n  line: " + string.Join(" ", named.Select(s => $"{s.Color.Name}[{doc.GetText(s.Offset, s.Length)}]"));
        if (cuts.Count > 0)
            return ("split: " + string.Join(", ", cuts.Select(s => $"{s.Color.Name}[{doc.GetText(s.Offset, s.Length)}]")), dump);
        return (best?.Color.Name, dump);
    }

    static string Show(string s) => "'" + s.Replace("\t", "\\t").Replace("\n", "\\n") + "'";

    static void GdScript()
    {
        const string src =
            "## Synth helper: builds short tones at runtime.\n" +
            "@tool\n" +
            "class_name ToneSynth\n" +
            "extends Node2D\n\n" +
            "signal finished(name: StringName)\n\n" +
            "const RATE := 44100\n" +
            "@export_range(0.0, 1.0) var volume: float = 0.8\n" +
            "@onready var _player: AudioStreamPlayer = $Player\n" +
            "@onready var _label := %StatusLabel\n" +
            "var notes: Array[int] = [0x1F, 1_000, 2.5e-3]\n" +
            "var url := \"http://example.com/#anchor\"\n\n" +
            "func _ready() -> void:\n" +
            "\t# Play a tick on start.\n" +
            "\t_player.stream = _tick()\n" +
            "\t_label.text = \"tick %d\" % RATE\n" +
            "\tif volume > 0.5 and not is_inside_tree():\n" +
            "\t\tpass\n" +
            "\telif volume == 0:\n" +
            "\t\treturn\n" +
            "\telse:\n" +
            "\t\tawait get_tree().create_timer(1.0).timeout\n" +
            "\temit_signal(&\"finished\", &\"tick\")\n" +
            "\tvar path := ^\"Player:volume_db\"\n" +
            "\tvar doc := \"\"\"multi\n" +
            "line # not a comment\"\"\"\n" +
            "\tvar ok := true\n\n" +
            "static func _tick() -> AudioStreamWAV:\n" +
            "\tvar n := int(RATE * 0.10)\n" +
            "\tvar d := PackedFloat32Array()\n" +
            "\tfor i in n:\n" +
            "\t\tvar t := float(i) / RATE\n" +
            "\t\td.append(sin(TAU * 880.0 * t))\n" +
            "\t\tif t > 1.0:\n" +
            "\t\t\tbreak\n" +
            "\t\tcontinue\n" +
            "\twhile d.size() > n:\n" +
            "\t\td.remove_at(0)\n" +
            "\tmatch n:\n" +
            "\t\t0:\n" +
            "\t\t\tpass\n" +
            "\treturn _make(d)\n\n" +
            "static func _make(data: PackedFloat32Array) -> AudioStreamWAV:\n" +
            "\tvar s := AudioStreamWAV.new()\n" +
            "\ts.mix_rate = RATE\n" +
            "\treturn s if data.size() > 0 else null\n";
        Lang("L-gd", ".gd", src,
            ("## Synth helper: builds short tones at runtime.", "## Synth helper: builds short tones at runtime.", "DocComment"),
            ("@tool", "@tool", "Annotation"),
            ("class_name ToneSynth", "class_name", "Keyword"),
            ("class_name ToneSynth", "ToneSynth", "Type"),
            ("extends Node2D", "extends", "Keyword"),
            ("extends Node2D", "Node2D", "Type"),
            ("signal finished(", "signal", "Keyword"),
            ("signal finished(", "finished", "Function"),
            ("name: StringName", "StringName", "Type"),
            ("const RATE := 44100", "const", "Keyword"),
            ("const RATE := 44100", "RATE", "ConstantVariable"),
            ("const RATE := 44100", "44100", "Number"),
            ("@export_range(0.0", "@export_range", "Annotation"),
            ("@export_range(0.0", "0.0", "Number"),
            ("var volume: float", "var", "Keyword"),
            ("var volume: float", "volume", ""),
            ("volume: float", "float", "Type"),
            ("= 0.8", "0.8", "Number"),
            ("@onready var _player", "@onready", "Annotation"),
            ("AudioStreamPlayer = $Player", "AudioStreamPlayer", "Type"),
            ("= $Player", "$Player", "NodeVariable"),
            ("%StatusLabel", "%StatusLabel", "NodeVariable"),
            ("Array[int]", "Array", "Type"),
            ("Array[int]", "int", "Type"),
            ("[0x1F, 1_000, 2.5e-3]", "0x1F", "Number"),
            ("[0x1F, 1_000, 2.5e-3]", "1_000", "Number"),
            ("[0x1F, 1_000, 2.5e-3]", "2.5e-3", "Number"),
            ("\"http://example.com/#anchor\"", "\"http://example.com/#anchor\"", "String"),
            ("func _ready() -> void:", "func", "Keyword"),
            ("func _ready() -> void:", "_ready", "Function"),
            ("func _ready() -> void:", "void", "Type"),
            ("# Play a tick on start.", "# Play a tick on start.", "Comment"),
            ("_player.stream", "stream", "Property"),
            ("= _tick()", "_tick", "Function"),
            ("\"tick %d\" % RATE", "\"tick %d\"", "String"),
            ("\" % RATE", "%", ""),
            ("\"tick %d\" % RATE", "RATE", "ConstantVariable"),
            ("if volume > 0.5", "if", "ControlKeyword"),
            ("if volume > 0.5", "0.5", "Number"),
            ("and not is_inside_tree()", "and", "Keyword"),
            ("and not is_inside_tree()", "not", "Keyword"),
            ("and not is_inside_tree()", "is_inside_tree", "Function"),
            ("\t\tpass\n\telif", "pass", "ControlKeyword"),
            ("elif volume == 0:", "elif", "ControlKeyword"),
            ("\t\treturn\n", "return", "ControlKeyword"),
            ("\telse:", "else", "ControlKeyword"),
            ("await get_tree()", "await", "ControlKeyword"),
            ("await get_tree()", "get_tree", "Function"),
            ("create_timer(1.0).timeout", "create_timer", "Function"),
            ("create_timer(1.0).timeout", "timeout", "Property"),
            ("&\"finished\"", "&\"finished\"", "String"),
            ("^\"Player:volume_db\"", "^\"Player:volume_db\"", "String"),
            ("\"\"\"multi", "\"\"\"multi", "String"),
            ("line # not a comment\"\"\"", "line # not a comment\"\"\"", "String"),
            ("var ok := true", "true", "ConstantKeyword"),
            ("static func _tick() -> AudioStreamWAV:", "static", "Keyword"),
            ("static func _tick() -> AudioStreamWAV:", "func", "Keyword"),
            ("static func _tick() -> AudioStreamWAV:", "_tick", "Function"),
            ("static func _tick() -> AudioStreamWAV:", "AudioStreamWAV", "Type"),
            ("int(RATE * 0.10)", "int", "Type"),
            ("int(RATE * 0.10)", "RATE", "ConstantVariable"),
            ("int(RATE * 0.10)", "0.10", "Number"),
            ("var d := PackedFloat32Array()", "PackedFloat32Array", "Type"),
            ("for i in n:", "for", "ControlKeyword"),
            ("for i in n:", "i", ""),
            ("for i in n:", "in", "Keyword"),
            ("float(i) / RATE", "float", "Type"),
            ("d.append(sin(TAU * 880.0", "append", "Function"),
            ("d.append(sin(TAU * 880.0", "sin", "Function"),
            ("d.append(sin(TAU * 880.0", "TAU", "ConstantKeyword"),
            ("d.append(sin(TAU * 880.0", "880.0", "Number"),
            ("\t\t\tbreak", "break", "ControlKeyword"),
            ("\t\tcontinue", "continue", "ControlKeyword"),
            ("while d.size() > n:", "while", "ControlKeyword"),
            ("while d.size() > n:", "size", "Function"),
            ("match n:", "match", "ControlKeyword"),
            ("return _make(d)", "return", "ControlKeyword"),
            ("return _make(d)", "_make", "Function"),
            ("static func _make(data", "_make", "Function"),
            ("data: PackedFloat32Array) ->", "PackedFloat32Array", "Type"),
            ("AudioStreamWAV.new()", "AudioStreamWAV", "Type"),
            ("AudioStreamWAV.new()", "new", "Function"),
            ("s.mix_rate = RATE", "mix_rate", "Property"),
            ("return s if data", "if", "ControlKeyword"),
            ("else null", "else", "ControlKeyword"),
            ("else null", "null", "ConstantKeyword"));
    }

    static void GodotShader()
    {
        const string src =
            "// Water surface: scrolls a noise map.\n" +
            "shader_type spatial;\n" +
            "render_mode blend_mix, cull_disabled, unshaded;\n\n" +
            "#include \"res://shaders/common.gdshaderinc\"\n" +
            "#define WAVES 3\n\n" +
            "uniform vec4 tint : source_color = vec4(0.1, 0.4, 0.8, 1.0);\n" +
            "uniform float speed : hint_range(0.0, 2.0, 0.01) = 0.5;\n" +
            "uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;\n" +
            "varying vec3 world_pos;\n\n" +
            "/* Shared helper,\n" +
            "   spans two lines. */\n" +
            "float wave(vec2 p, float t) {\n" +
            "\treturn sin(p.x * 4.0 + t) * 0.5 + 0.5;\n" +
            "}\n\n" +
            "void vertex() {\n" +
            "\tworld_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;\n" +
            "\tVERTEX.y += wave(world_pos.xz, TIME * speed);\n" +
            "}\n\n" +
            "void fragment() {\n" +
            "\tvec3 n = texture(noise_tex, UV + TIME * 0.05).rgb;\n" +
            "\tfor (int i = 0; i < WAVES; i++) {\n" +
            "\t\tif (n.r > 0.9) { discard; }\n" +
            "\t}\n" +
            "\tALBEDO = tint.rgb * n;\n" +
            "\tbool ok = true;\n" +
            "\tuint mask = 0x1Fu;\n" +
            "}\n";
        Lang("L-shd", ".gdshader", src,
            ("// Water surface: scrolls a noise map.", "// Water surface: scrolls a noise map.", "Comment"),
            ("shader_type spatial;", "shader_type", "Keyword"),
            ("shader_type spatial;", "spatial", "Annotation"),
            ("render_mode blend_mix", "render_mode", "Keyword"),
            ("render_mode blend_mix", "blend_mix", "Annotation"),
            ("cull_disabled", "cull_disabled", "Annotation"),
            ("#include \"res://", "#include", "Preprocessor"),
            ("\"res://shaders/common.gdshaderinc\"", "\"res://shaders/common.gdshaderinc\"", "String"),
            ("#define WAVES 3", "#define", "Preprocessor"),
            ("#define WAVES 3", "WAVES", "BuiltinVariable"),
            ("#define WAVES 3", "3", "Number"),
            ("uniform vec4 tint", "uniform", "Keyword"),
            ("uniform vec4 tint", "vec4", "Type"),
            ("uniform vec4 tint", "tint", ""),
            (": source_color", "source_color", "Annotation"),
            ("vec4(0.1, 0.4", "0.1", "Number"),
            ("hint_range(0.0, 2.0, 0.01)", "hint_range", "Annotation"),
            ("hint_range(0.0, 2.0, 0.01)", "0.01", "Number"),
            ("uniform sampler2D noise_tex", "sampler2D", "Type"),
            ("filter_linear_mipmap, repeat_enable", "filter_linear_mipmap", "Annotation"),
            ("filter_linear_mipmap, repeat_enable", "repeat_enable", "Annotation"),
            ("varying vec3 world_pos", "varying", "Keyword"),
            ("varying vec3 world_pos", "vec3", "Type"),
            ("/* Shared helper,", "/* Shared helper,", "Comment"),
            ("   spans two lines. */", "   spans two lines. */", "Comment"),
            ("float wave(vec2 p", "float", "Type"),
            ("float wave(vec2 p", "wave", "Function"),
            ("float wave(vec2 p", "vec2", "Type"),
            ("return sin(p.x", "return", "ControlKeyword"),
            ("return sin(p.x", "sin", "Function"),
            ("p.x * 4.0 + t", "4.0", "Number"),
            ("void vertex()", "void", "Type"),
            ("void vertex()", "vertex", "Function"),
            ("(MODEL_MATRIX * vec4(VERTEX, 1.0))", "MODEL_MATRIX", "BuiltinVariable"),
            ("(MODEL_MATRIX * vec4(VERTEX, 1.0))", "VERTEX", "BuiltinVariable"),
            ("TIME * speed", "TIME", "BuiltinVariable"),
            ("texture(noise_tex", "texture", "Function"),
            ("UV + TIME * 0.05", "UV", "BuiltinVariable"),
            ("UV + TIME * 0.05", "0.05", "Number"),
            ("for (int i = 0;", "for", "ControlKeyword"),
            ("for (int i = 0;", "int", "Type"),
            ("for (int i = 0;", "0", "Number"),
            ("if (n.r > 0.9)", "if", "ControlKeyword"),
            ("{ discard; }", "discard", "ControlKeyword"),
            ("ALBEDO = tint.rgb", "ALBEDO", "BuiltinVariable"),
            ("bool ok = true;", "bool", "Type"),
            ("bool ok = true;", "true", "ConstantKeyword"),
            ("uint mask = 0x1Fu;", "uint", "Type"),
            ("uint mask = 0x1Fu;", "0x1Fu", "Number"));
    }

    static void GodotResource()
    {
        const string scene =
            "[gd_scene load_steps=3 format=3 uid=\"uid://b6x1v2kq0abcd\"]\n\n" +
            "[ext_resource type=\"Script\" path=\"res://player.gd\" id=\"1_k3p0x\"]\n" +
            "[ext_resource type=\"PackedScene\" uid=\"uid://c2ball\" path=\"res://ball.tscn\" id=\"2_ball\"]\n\n" +
            "[sub_resource type=\"RectangleShape2D\" id=\"RectangleShape2D_a1b2c\"]\n" +
            "size = Vector2(32, 48.5)\n\n" +
            "[node name=\"Player\" type=\"CharacterBody2D\"]\n" +
            "script = ExtResource(\"1_k3p0x\")\n" +
            "visible = true\n" +
            "metadata/_edit_group_ = true\n" +
            "modulate = Color(1, 0.5, 0.25, 1)\n\n" +
            "[node name=\"Ball\" parent=\".\" instance=ExtResource(\"2_ball\")]\n\n" +
            "[node name=\"Shape\" type=\"CollisionShape2D\" parent=\".\"]\n" +
            "shape = SubResource(\"RectangleShape2D_a1b2c\")\n" +
            "; a comment\n\n" +
            "[node name=\"Hint\" type=\"Label\" parent=\".\"]\n" +
            "text = \"Line one\n" +
            "Line two\"\n";
        Lang("L-res", ".tscn", scene,
            ("[gd_scene load_steps=3", "[gd_scene", "SectionType"),
            ("[gd_scene load_steps=3", "load_steps", "PropertyName"),
            ("[gd_scene load_steps=3", "3", "Number"),
            ("uid=\"uid://b6x1v2kq0abcd\"]", "\"uid://b6x1v2kq0abcd\"", "String"),
            ("uid=\"uid://b6x1v2kq0abcd\"]", "]", "SectionType"),
            ("[ext_resource type=\"Script\"", "type", "PropertyName"),
            ("path=\"res://player.gd\"", "\"res://player.gd\"", "String"),
            ("size = Vector2(32, 48.5)", "size", "PropertyName"),
            ("size = Vector2(32, 48.5)", "Vector2", "Function"),
            ("size = Vector2(32, 48.5)", "32", "Number"),
            ("size = Vector2(32, 48.5)", "48.5", "Number"),
            ("script = ExtResource(\"1_k3p0x\")", "script", "PropertyName"),
            ("script = ExtResource(\"1_k3p0x\")", "ExtResource", "Function"),
            ("script = ExtResource(\"1_k3p0x\")", "\"1_k3p0x\"", "String"),
            ("visible = true", "true", "ConstantKeyword"),
            ("metadata/_edit_group_ = true", "metadata/_edit_group_", "PropertyName"),
            ("modulate = Color(1, 0.5", "0.5", "Number"),
            ("instance=ExtResource(\"2_ball\")]", "instance", "PropertyName"),
            ("instance=ExtResource(\"2_ball\")]", "ExtResource", "Function"),
            ("shape = SubResource(", "SubResource", "Function"),
            ("; a comment", "; a comment", "Comment"),
            ("text = \"Line one", "\"Line one", "String"),
            ("Line two\"", "Line two\"", "String"));

        const string project =
            "; Engine configuration file.\n" +
            "config_version=5\n\n" +
            "[application]\n\n" +
            "config/name=\"Snooker\"\n" +
            "config/features=PackedStringArray(\"4.3\", \"Forward Plus\")\n" +
            "run/main_scene=\"res://main.tscn\"\n";
        Lang("L-res", ".godot", project,
            ("; Engine configuration file.", "; Engine configuration file.", "Comment"),
            ("config_version=5", "config_version", "PropertyName"),
            ("config_version=5", "5", "Number"),
            ("[application]", "[application]", "SectionType"),
            ("config/name=\"Snooker\"", "config/name", "PropertyName"),
            ("config/name=\"Snooker\"", "\"Snooker\"", "String"),
            ("PackedStringArray(\"4.3\"", "PackedStringArray", "Function"),
            ("PackedStringArray(\"4.3\"", "\"4.3\"", "String"),
            ("run/main_scene=\"res://main.tscn\"", "\"res://main.tscn\"", "String"));
    }

    static void Jsonc()
    {
        const string src =
            "{\n" +
            "  // editor settings\n" +
            "  \"name\": \"demo-project\",\n" +
            "  \"homepage\": \"https://example.com/a//b\",\n" +
            "  \"private\": true,\n" +
            "  \"count\": -12.5e3,\n" +
            "  \"nothing\": null,\n" +
            "  \"nested\": { \"key\\\"q\": [1, 2, false], \"x\":0 }\n" +
            "  /* block\n" +
            "     comment */\n" +
            "}\n";
        Lang("L-json", ".json", src,
            ("// editor settings", "// editor settings", "Comment"),
            ("\"name\": \"demo-project\"", "\"name\"", "PropertyName"),
            ("\"name\": \"demo-project\"", "\"demo-project\"", "String"),
            ("\"homepage\": ", "\"homepage\"", "PropertyName"),
            ("\"https://example.com/a//b\"", "\"https://example.com/a//b\"", "String"),
            ("\"private\": true", "true", "ConstantKeyword"),
            ("\"count\": -12.5e3", "-12.5e3", "Number"),
            ("\"nothing\": null", "null", "ConstantKeyword"),
            ("\"key\\\"q\": [1", "\"key\\\"q\"", "PropertyName"),
            ("[1, 2, false]", "1", "Number"),
            ("[1, 2, false]", "false", "ConstantKeyword"),
            ("\"x\":0", "\"x\"", "PropertyName"),
            ("\"x\":0", "0", "Number"),
            ("/* block", "/* block", "Comment"),
            ("     comment */", "     comment */", "Comment"));
    }

    static void Yaml()
    {
        const string src =
            "# CI workflow\n" +
            "name: build\n" +
            "on:\n" +
            "  push:\n" +
            "    branches: [main]\n" +
            "env:\n" +
            "  DOTNET_VERSION: \"8.0.x\"\n" +
            "  RETRIES: 3\n" +
            "  RATIO: -0.75\n" +
            "  DEBUG: false\n" +
            "  EMPTY: ~\n" +
            "  VERSION: 1.2.3\n" +
            "  WHEN: 12:30\n" +
            "defaults: &defaults\n" +
            "  shell: bash\n" +
            "jobs:\n" +
            "  test:\n" +
            "    <<: *defaults\n" +
            "    runs-on: windows-latest\n" +
            "    steps:\n" +
            "      - uses: actions/checkout@v4\n" +
            "      - name: 'Run tests # not a comment'\n" +
            "        run: |\n" +
            "          dotnet test\n" +
            "      - run: echo a:b # trailing\n" +
            "---\n";
        Lang("L-yaml", ".yaml", src,
            ("# CI workflow", "# CI workflow", "Comment"),
            ("name: build", "name", "PropertyName"),
            ("name: build", "build", ""),
            ("on:\n", "on", "PropertyName"),
            ("  push:", "push", "PropertyName"),
            ("branches: [main]", "branches", "PropertyName"),
            ("DOTNET_VERSION: \"8.0.x\"", "DOTNET_VERSION", "PropertyName"),
            ("DOTNET_VERSION: \"8.0.x\"", "\"8.0.x\"", "String"),
            ("RETRIES: 3", "3", "Number"),
            ("RATIO: -0.75", "-0.75", "Number"),
            ("DEBUG: false", "false", "ConstantKeyword"),
            ("EMPTY: ~", "~", "ConstantKeyword"),
            ("VERSION: 1.2.3", "1.2.3", ""),
            ("WHEN: 12:30", "12:30", ""),
            ("defaults: &defaults", "&defaults", "Annotation"),
            ("<<: *defaults", "*defaults", "Annotation"),
            ("runs-on: windows-latest", "runs-on", "PropertyName"),
            ("runs-on: windows-latest", "windows-latest", ""),
            ("- uses: actions/checkout@v4", "uses", "PropertyName"),
            ("'Run tests # not a comment'", "'Run tests # not a comment'", "String"),
            ("run: |", "run", "PropertyName"),
            ("run: |", "|", "Keyword"),
            ("- run: echo a:b # trailing", "echo a:b", ""),
            ("- run: echo a:b # trailing", "# trailing", "Comment"),
            ("---\n", "---", "Keyword"));
    }

    static void Toml()
    {
        const string src =
            "# Cargo-ish manifest\n" +
            "title = \"TOML demo\"\n\n" +
            "[package]\n" +
            "name = \"demo\"\n" +
            "edition = 2021\n" +
            "authors = [\"A <a@x.io>\", 'C:\\literal']\n\n" +
            "[dependencies.serde]\n" +
            "features = [\"derive\"]\n" +
            "pi = 3.14_15\n" +
            "hex = 0xDEAD_beef\n" +
            "neg = -1e-3\n" +
            "enabled = true\n" +
            "when = 1979-05-27T07:32:00Z\n" +
            "description = \"\"\"\n" +
            "multi # not a comment\n" +
            "line\"\"\"\n" +
            "matrix = [\n" +
            "  [1, 2],\n" +
            "]\n\n" +
            "[[bin]]\n" +
            "path = \"src/main.rs\" # trailing comment\n" +
            "nested.key = 'x'\n";
        Lang("L-toml", ".toml", src,
            ("# Cargo-ish manifest", "# Cargo-ish manifest", "Comment"),
            ("title = \"TOML demo\"", "title", "PropertyName"),
            ("title = \"TOML demo\"", "\"TOML demo\"", "String"),
            ("[package]", "[package]", "SectionType"),
            ("edition = 2021", "2021", "Number"),
            ("'C:\\literal'", "'C:\\literal'", "String"),
            ("[dependencies.serde]", "[dependencies.serde]", "SectionType"),
            ("pi = 3.14_15", "3.14_15", "Number"),
            ("hex = 0xDEAD_beef", "0xDEAD_beef", "Number"),
            ("neg = -1e-3", "-1e-3", "Number"),
            ("enabled = true", "true", "ConstantKeyword"),
            ("when = 1979-05-27T07:32:00Z", "1979-05-27T07:32:00Z", "Number"),
            ("multi # not a comment", "multi # not a comment", "String"),
            ("line\"\"\"", "line\"\"\"", "String"),
            ("  [1, 2],", "[", ""),
            ("  [1, 2],", "2", "Number"),
            ("[[bin]]", "[[bin]]", "SectionType"),
            ("path = \"src/main.rs\"", "path", "PropertyName"),
            ("# trailing comment", "# trailing comment", "Comment"),
            ("nested.key = 'x'", "nested.key", "PropertyName"));
    }

    static void Shell()
    {
        const string src =
            "#!/usr/bin/env bash\n" +
            "# build script\n" +
            "set -euo pipefail\n" +
            "readonly OUT_DIR=\"${HOME}/out\"\n" +
            "export MODE=release\n" +
            "count=0\n\n" +
            "build() {\n" +
            "  local target=\"$1\"\n" +
            "  echo \"building $target in $OUT_DIR\"\n" +
            "  if [[ -z \"$target\" ]]; then\n" +
            "    return 1\n" +
            "  fi\n" +
            "  for f in src/*.c; do\n" +
            "    count=$((count + 1))\n" +
            "  done\n" +
            "  printf '%s\\n' 'no $expansion here'\n" +
            "  ./configure --prefix=/usr\n" +
            "}\n\n" +
            "case \"$MODE\" in\n" +
            "  release) build all ;;\n" +
            "  *) exit 2 ;;\n" +
            "esac\n" +
            "echo \"done: $? ${#count}\"   # trailing comment\n";
        Lang("L-sh", ".sh", src,
            ("#!/usr/bin/env bash", "#!/usr/bin/env bash", "Comment"),
            ("# build script", "# build script", "Comment"),
            ("set -euo pipefail", "set", "Function"),
            ("readonly OUT_DIR=", "readonly", "Keyword"),
            ("readonly OUT_DIR=", "OUT_DIR", "Variable"),
            ("\"${HOME}/out\"", "${HOME}", "Variable"),
            ("\"${HOME}/out\"", "/out\"", "String"),
            ("export MODE=release", "export", "Keyword"),
            ("export MODE=release", "MODE", "Variable"),
            ("count=0", "count", "Variable"),
            ("count=0", "0", "Number"),
            ("build() {", "build", "Function"),
            ("local target=\"$1\"", "local", "Keyword"),
            ("local target=\"$1\"", "$1", "Variable"),
            ("echo \"building $target", "echo", "Function"),
            ("echo \"building $target", "$target", "Variable"),
            ("in $OUT_DIR\"", "$OUT_DIR", "Variable"),
            ("if [[ -z", "if", "ControlKeyword"),
            ("]]; then", "then", "ControlKeyword"),
            ("return 1", "return", "ControlKeyword"),
            ("return 1", "1", "Number"),
            ("  fi\n", "fi", "ControlKeyword"),
            ("for f in src/*.c; do", "for", "ControlKeyword"),
            ("for f in src/*.c; do", "in", "ControlKeyword"),
            ("for f in src/*.c; do", "do", "ControlKeyword"),
            ("  done\n", "done", "ControlKeyword"),
            ("printf '%s\\n'", "printf", "Function"),
            ("'no $expansion here'", "'no $expansion here'", "String"),
            ("--prefix=/usr", "prefix", ""),
            ("case \"$MODE\" in", "case", "ControlKeyword"),
            ("exit 2 ;;", "exit", "Function"),
            ("exit 2 ;;", "2", "Number"),
            ("esac\n", "esac", "ControlKeyword"),
            ("$? ${#count}", "$?", "Variable"),
            ("$? ${#count}", "${#count}", "Variable"),
            ("# trailing comment", "# trailing comment", "Comment"));
    }

    static void Dart()
    {
        // The file that opened as plain white text on 2026-10-17 (a Flutter project), plus the usual Flutter shapes.
        const string src =
            "/// Parses an install error into a Thai message.\n" +
            "import 'package:flutter/material.dart';\n" +
            "// a line comment\n" +
            "/* block */\n" +
            "@override\n" +
            "class UpdateDialog extends StatelessWidget {\n" +
            "  const UpdateDialog({super.key, required this.title});\n" +
            "  final String title;\n" +
            "  static const MAX_RETRIES = 3;\n" +
            "  Future<void> run(int n) async {\n" +
            "    final m = AutoUpdater.installErrorMessage(null);\n" +
            "    var count = 0x1F + 2.5e-3;\n" +
            "    if (n > 0 && widget.title.isEmpty) {\n" +
            "      return;\n" +
            "    } else {\n" +
            "      await Future.delayed(Duration(seconds: 1));\n" +
            "    }\n" +
            "    for (final x in items) { print('hi $name and ${x.id}'); }\n" +
            "    throw StateError(r'raw $notInterpolated');\n" +
            "    expect(s.isHandedOff, isTrue);\n" +
            "    final ok = true ?? false;\n" +
            "    final list = <String>[];\n" +
            "    setState(() {});\n" +
            "  }\n" +
            "}\n";
        Lang("L-dart", ".dart", src,
            ("/// Parses an install error into a Thai message.", "/// Parses an install error into a Thai message.", "DocComment"),
            ("import 'package:flutter/material.dart';", "import", "Keyword"),
            ("import 'package:flutter/material.dart';", "'package:flutter/material.dart'", "String"),
            ("// a line comment", "// a line comment", "Comment"),
            ("/* block */", "/* block */", "Comment"),
            ("@override", "@override", "Annotation"),
            ("class UpdateDialog extends", "class", "Keyword"),
            ("class UpdateDialog extends", "UpdateDialog", "Type"),
            ("extends StatelessWidget", "extends", "Keyword"),
            ("extends StatelessWidget", "StatelessWidget", "Type"),
            ("required this.title", "required", "Keyword"),
            ("required this.title", "this", "Keyword"),
            ("final String title;", "final", "Keyword"),
            ("final String title;", "String", "Type"),
            ("MAX_RETRIES = 3", "MAX_RETRIES", "ConstantVariable"),
            ("MAX_RETRIES = 3", "3", "Number"),
            ("Future<void> run(", "Future", "Type"),
            ("Future<void> run(", "void", "Type"),
            ("Future<void> run(", "run", "Function"),
            ("run(int n)", "int", "Type"),
            ("async {", "async", "Keyword"),
            ("AutoUpdater.installErrorMessage(null)", "AutoUpdater", "Type"),
            ("AutoUpdater.installErrorMessage(null)", "installErrorMessage", "Function"),
            ("installErrorMessage(null)", "null", "ConstantKeyword"),
            ("0x1F + 2.5e-3", "0x1F", "Number"),
            ("0x1F + 2.5e-3", "2.5e-3", "Number"),
            ("if (n > 0", "if", "ControlKeyword"),
            ("widget.title.isEmpty", "title", "Property"),
            ("widget.title.isEmpty", "isEmpty", "Property"),
            ("      return;", "return", "ControlKeyword"),
            ("} else {", "else", "ControlKeyword"),
            ("await Future.delayed", "await", "ControlKeyword"),
            ("Future.delayed(", "delayed", "Function"),
            ("for (final x in items)", "for", "ControlKeyword"),
            ("for (final x in items)", "in", "Keyword"),
            ("print('hi", "print", "Function"),
            ("'hi $name and ${x.id}'", "'hi ", "String"),
            ("'hi $name and ${x.id}'", "$name", "InterpolationVariable"),
            ("'hi $name and ${x.id}'", "${x.id}", "InterpolationVariable"),
            ("throw StateError", "throw", "ControlKeyword"),
            ("r'raw $notInterpolated'", "r'raw $notInterpolated'", "String"),
            ("expect(s.isHandedOff, isTrue)", "isHandedOff", "Property"),
            ("true ?? false", "true", "ConstantKeyword"),
            ("<String>[]", "String", "Type"),
            ("setState(() {})", "setState", "Function"));
    }

    // ═══════════════════════════════════════════ P ═══════════════════════════════════════════

    /// <summary>Which Syntax.* token each colour name of the bundled definitions must end up as.</summary>
    static readonly Dictionary<string, string> TokenFor = new()
    {
        ["Comment"] = "Syntax.Comment", ["DocComment"] = "Syntax.DocComment", ["String"] = "Syntax.String",
        ["Number"] = "Syntax.Number", ["Keyword"] = "Syntax.Keyword", ["ConstantKeyword"] = "Syntax.Keyword",
        ["ControlKeyword"] = "Syntax.Control", ["Type"] = "Syntax.Type", ["SectionType"] = "Syntax.Type",
        ["Function"] = "Syntax.Function", ["Annotation"] = "Syntax.Preprocessor", ["Preprocessor"] = "Syntax.Preprocessor",
        ["Variable"] = "Syntax.Variable", ["NodeVariable"] = "Syntax.Variable", ["ConstantVariable"] = "Syntax.Variable",
        ["BuiltinVariable"] = "Syntax.Variable", ["Property"] = "Syntax.Variable", ["PropertyName"] = "Syntax.Variable",
        ["InterpolationVariable"] = "Syntax.Variable",
    };

    static readonly Color Missing = Color.FromArgb(1, 2, 3, 4);

    static Color? Fg(HighlightingColor c) => (c.Foreground?.GetBrush(null) as SolidColorBrush)?.Color;

    static string Hex(Color? c) => c == null ? "none" : $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";

    static void Palette(string theme)
    {
        string id = "P-" + theme[..3].ToLowerInvariant();
        foreach (var token in new[] { "Syntax.Function", "Syntax.Control", "Syntax.Variable" })
            Check(id, $"{theme} defines {token}", ThemeService.ColorOf(token, Missing) != Missing, Hex(ThemeService.ColorOf(token, Missing)));

        // Every colour of every bundled definition carries its token's colour — nothing left on an xshd fallback.
        foreach (string name in BundledNames)
        {
            var def = NeonHighlighting.ForExtension(ExtOf(name));
            if (def == null) { Check(id, $"{name}: loaded", false); continue; }
            var wrong = new List<string>();
            foreach (var color in def.NamedHighlightingColors)
            {
                if (!TokenFor.TryGetValue(color.Name, out var token)) { wrong.Add($"{color.Name}: no token for this name"); continue; }
                var want = ThemeService.ColorOf(token, Missing);
                if (Fg(color) != want) wrong.Add($"{color.Name}: {Hex(Fg(color))} ≠ {token} {Hex(want)}");
            }
            Check(id, $"{name}: every colour is its Syntax.* token ({def.NamedHighlightingColors.Count()} colours)", wrong.Count == 0, string.Join("\n", wrong));
        }

        // The built-in definitions pick up the new split too (by colour name), and keep the rest.
        var builtin = new (string Ext, string Color, string Token)[]
        {
            (".cs", "MethodCall", "Syntax.Function"), (".cs", "GotoKeywords", "Syntax.Control"), (".cs", "Keywords", "Syntax.Keyword"),
            (".cs", "ReferenceTypeKeywords", "Syntax.Type"), (".cs", "String", "Syntax.String"),
            (".py", "MethodCall", "Syntax.Function"), (".cpp", "ControlFlow", "Syntax.Control"), (".cpp", "MethodName", "Syntax.Function"),
            (".js", "JavaScriptGlobalFunctions", "Syntax.Function"), (".ps1", "Variable", "Syntax.Variable"),
            (".ps1", "GotoKeywords", "Syntax.Control"), (".java", "MethodName", "Syntax.Function"),
        };
        foreach (var (ext, colorName, token) in builtin)
        {
            var color = NeonHighlighting.ForExtension(ext)?.GetNamedColor(colorName);
            var want = ThemeService.ColorOf(token, Missing);
            Check(id, $"built-in {ext} {colorName} → {token}", color != null && Fg(color) == want, $"{Hex(color == null ? null : Fg(color))} vs {Hex(want)}");
        }

        // Readable on the code surface and distinct from every other syntax colour.
        var surface = ThemeService.ColorOf("Editor.Background", Colors.Transparent);
        if (surface.A < 0x80) surface = ThemeService.ColorOf("Bg.Window", Missing);
        var all = new[] { "Syntax.Keyword", "Syntax.Type", "Syntax.String", "Syntax.Number", "Syntax.Preprocessor",
                          "Syntax.Comment", "Syntax.Function", "Syntax.Control", "Syntax.Variable", "Editor.Foreground" };
        foreach (var token in new[] { "Syntax.Function", "Syntax.Control", "Syntax.Variable" })
        {
            var c = ThemeService.ColorOf(token, Missing);
            double ratio = Contrast(c, surface);
            Check(id, $"{token} {Hex(c)} readable on the editor {Hex(surface)} (≥ 4.5:1)", ratio >= 4.5, $"{ratio:0.00}:1");
            var close = all.Where(o => o != token)
                           .Select(o => (o, d: DeltaE2000(c, ThemeService.ColorOf(o, Missing))))
                           .OrderBy(x => x.d).First();
            Check(id, $"{token} distinct from the other syntax colours (ΔE2000 ≥ 10)", close.d >= 10, $"nearest {close.o} ΔE {close.d:0.0}");
            Console.WriteLine($"            {token} {Hex(c)}: {ratio:0.0}:1 · nearest {close.o} ΔE {close.d:0.0}");
        }
    }

    static string ExtOf(string name) => name switch
    {
        "GDScript" => ".gd", "Godot Shader" => ".gdshader", "Godot Resource" => ".tscn", "JSONC" => ".json",
        "YAML" => ".yaml", "TOML" => ".toml", "Shell" => ".sh", "Dart" => ".dart", _ => ".none",
    };

    static void RoundTrip(Application app)
    {
        // NeonHighlighting re-tints from the ORIGINAL xshd colours on each palette change — never compounding.
        (Color? Now, Color Token) Function()
            => (Fg(NeonHighlighting.ForExtension(".gd").GetNamedColor("Function")), ThemeService.ColorOf("Syntax.Function", Missing));
        UseTheme(app, "Midnight");
        var midnight = Function();
        UseTheme(app, "Classic");
        var classic = Function();
        UseTheme(app, "Midnight");
        var back = Function();
        Check("P-rt", "GDScript Function follows the theme (Midnight → Classic → Midnight)",
            midnight.Now == midnight.Token && classic.Now == classic.Token && back.Now == midnight.Now && midnight.Now != classic.Now,
            $"{Hex(midnight.Now)} → {Hex(classic.Now)} → {Hex(back.Now)}");
        var control = Fg(NeonHighlighting.ForExtension(".cs").GetNamedColor("GotoKeywords"));
        Check("P-rt", "C# return/break back on Midnight's Syntax.Control after the round trip",
            control == ThemeService.ColorOf("Syntax.Control", Missing), Hex(control));
    }

    // ═══════════════════════════════════════════ colour maths ═══════════════════════════════════════════

    static double Lin(byte v) { double c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    static double RelLum(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    static double Contrast(Color a, Color b)
    {
        double la = RelLum(a), lb = RelLum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static (double L, double A, double B) Lab(Color c)
    {
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b,
               z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>CIEDE2000 colour difference (Sharma et al. 2005).</summary>
    static double DeltaE2000(Color c1, Color c2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        static double Deg(double r) => r * 180 / Math.PI;
        var (l1, a1, b1) = Lab(c1); var (l2, a2, b2) = Lab(c2);
        double cb = (Math.Sqrt(a1 * a1 + b1 * b1) + Math.Sqrt(a2 * a2 + b2 * b2)) / 2;
        double g = 0.5 * (1 - Math.Sqrt(Math.Pow(cb, 7) / (Math.Pow(cb, 7) + Math.Pow(25, 7))));
        double a1p = (1 + g) * a1, a2p = (1 + g) * a2;
        double c1p = Math.Sqrt(a1p * a1p + b1 * b1), c2p = Math.Sqrt(a2p * a2p + b2 * b2);
        double h1p = (Deg(Math.Atan2(b1, a1p)) + 360) % 360, h2p = (Deg(Math.Atan2(b2, a2p)) + 360) % 360;
        double dl = l2 - l1, dc = c2p - c1p, dh = h2p - h1p;
        if (c1p * c2p == 0) dh = 0; else if (dh > 180) dh -= 360; else if (dh < -180) dh += 360;
        double dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dh / 2));
        double lbp = (l1 + l2) / 2, cbp = (c1p + c2p) / 2, hbp;
        if (c1p * c2p == 0) hbp = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180) hbp = (h1p + h2p) / 2;
        else hbp = h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
        double t = 1 - 0.17 * Math.Cos(Rad(hbp - 30)) + 0.24 * Math.Cos(Rad(2 * hbp)) + 0.32 * Math.Cos(Rad(3 * hbp + 6)) - 0.20 * Math.Cos(Rad(4 * hbp - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hbp - 275) / 25, 2));
        double rc = 2 * Math.Sqrt(Math.Pow(cbp, 7) / (Math.Pow(cbp, 7) + Math.Pow(25, 7)));
        double sl = 1 + 0.015 * Math.Pow(lbp - 50, 2) / Math.Sqrt(20 + Math.Pow(lbp - 50, 2));
        double sc = 1 + 0.045 * cbp, sh = 1 + 0.015 * cbp * t, rt = -Math.Sin(Rad(2 * dTheta)) * rc;
        return Math.Sqrt(Math.Pow(dl / sl, 2) + Math.Pow(dc / sc, 2) + Math.Pow(dH / sh, 2) + rt * (dc / sc) * (dH / sh));
    }
}
