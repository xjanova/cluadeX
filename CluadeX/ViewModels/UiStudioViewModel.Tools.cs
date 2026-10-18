using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.ViewModels;

/// <summary>A widget the palette puts on a screen (click: next to the picked one; drag: where it is dropped).</summary>
public sealed class WidgetPaletteItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Code { get; init; } = "";
}

/// <summary>A colour to click — the app's own colours first.</summary>
public sealed class SwatchItem
{
    public string Hex { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Transparent;
    public string Tip { get; init; } = "";
}

public sealed class ChoiceItem
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>The app's ComboBox template shows the selected item as text.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The widget tools of a simulated (Flutter) screen: the widget picked on the real picture (or the wireframe) is
/// changed in the app's own code — text, size, weight, colours, spacing, corners, size, its place among its
/// siblings, a wrapper, a new widget from the palette (a click puts it next to the picked one, a drag puts it where it
/// is dropped). Every edit is written at once (Undo takes it back), Flutter draws the screen again, and the same
/// widget stays picked.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private FlutterWidgetInfo? _tool;
    private (string File, int Line, int Column)? _toolAt;
    /// <summary>A widget an edit made or moved, to pick once the picture is drawn again.</summary>
    private (string File, int Line, int Column)? _pendingFocus;

    private static readonly string[] ToolProps =
    {
        nameof(HasTools), nameof(ToolName), nameof(ToolSharedNote), nameof(HasToolSharedNote), nameof(CanToolText), nameof(ToolText),
        nameof(ToolTextNote), nameof(HasToolTextNote), nameof(CanToolTextStyle), nameof(ToolFontSize), nameof(ToolFontWeight), nameof(ToolTextAlign),
        nameof(ToolTextColor), nameof(ToolTextColorHex), nameof(CanToolColor), nameof(ToolColor), nameof(ToolColorHex), nameof(CanToolPadding),
        nameof(ToolPadding), nameof(CanToolMargin), nameof(ToolMargin), nameof(ToolRadius), nameof(ToolWidth), nameof(ToolHeight), nameof(CanToolMoveUp),
        nameof(CanToolMoveDown), nameof(CanToolDuplicate), nameof(CanToolUnwrap), nameof(CanToolExpanded), nameof(ToolHasChildren),
    };

    // ───────────────────────────── what the inspector shows ─────────────────────────────

    public bool HasTools => _tool != null;
    public string ToolName => _tool?.Name ?? "";
    /// <summary>The picked widget is written in another file than the screen's (a shared widget).</summary>
    public string ToolSharedNote => _toolAt is { } a && _stageCard?.Page.SourcePath is { } screen && !SameFile(a.File, screen) ? T("studio.fedit.shared", Rel(a.File)) : "";
    public bool HasToolSharedNote => ToolSharedNote.Length > 0;

    public bool CanToolText => _tool?.CanText == true;
    private string _toolText = "";
    public string ToolText { get => _toolText; set => SetProperty(ref _toolText, value ?? ""); }
    public string ToolTextNote => _tool is { CanText: false, TextNote: { } k } ? T(k) : "";
    public bool HasToolTextNote => ToolTextNote.Length > 0;

    public bool CanToolTextStyle => _tool?.CanTextStyle == true;
    private string _toolFontSize = "";
    public string ToolFontSize { get => _toolFontSize; set => SetProperty(ref _toolFontSize, value ?? ""); }
    private IReadOnlyList<ChoiceItem>? _fontWeights;
    public IReadOnlyList<ChoiceItem> FontWeights => _fontWeights ??= new[]
    {
        new ChoiceItem { Value = "", Label = T("studio.fedit.weight.theme") }, new ChoiceItem { Value = "w300", Label = T("studio.fedit.weight.light") },
        new ChoiceItem { Value = "normal", Label = T("studio.fedit.weight.normal") }, new ChoiceItem { Value = "w500", Label = T("studio.fedit.weight.medium") },
        new ChoiceItem { Value = "w600", Label = T("studio.fedit.weight.semibold") }, new ChoiceItem { Value = "bold", Label = T("studio.fedit.weight.bold") },
        new ChoiceItem { Value = "w800", Label = T("studio.fedit.weight.heavy") },
    };
    /// <summary>The weight as the code says it (choosing another one writes it).</summary>
    public string ToolFontWeight
    {
        get => _tool?.FontWeight is { } w ? (w == "w400" ? "normal" : w == "w700" ? "bold" : w) : "";
        set
        {
            if (_tool == null || (value ?? "") == ToolFontWeight) return;
            string? w = string.IsNullOrEmpty(value) ? null : value;
            RunTool("style", (t, l, c) => FlutterCodeEdit.SetFontWeight(t, l, c, w));
        }
    }
    public string ToolTextAlign => _tool?.TextAlign ?? "";
    public string ToolTextColor => _tool?.TextColor ?? "";
    private string _toolTextColorHex = "";
    public string ToolTextColorHex { get => _toolTextColorHex; set => SetProperty(ref _toolTextColorHex, value ?? ""); }

    public bool CanToolColor => _tool?.CanColor == true;
    public string ToolColor => _tool?.Color ?? "";
    private string _toolColorHex = "";
    public string ToolColorHex { get => _toolColorHex; set => SetProperty(ref _toolColorHex, value ?? ""); }

    public bool CanToolPadding => _tool?.CanPadding == true;
    private string _toolPadding = "";
    public string ToolPadding { get => _toolPadding; set => SetProperty(ref _toolPadding, value ?? ""); }
    public bool CanToolMargin => _tool?.CanMargin == true;
    private string _toolMargin = "";
    public string ToolMargin { get => _toolMargin; set => SetProperty(ref _toolMargin, value ?? ""); }
    private string _toolRadius = "";
    public string ToolRadius { get => _toolRadius; set => SetProperty(ref _toolRadius, value ?? ""); }
    private string _toolWidth = "", _toolHeight = "";
    public string ToolWidth { get => _toolWidth; set => SetProperty(ref _toolWidth, value ?? ""); }
    public string ToolHeight { get => _toolHeight; set => SetProperty(ref _toolHeight, value ?? ""); }

    public bool CanToolMoveUp => _tool?.CanMoveUp == true;
    public bool CanToolMoveDown => _tool?.CanMoveDown == true;
    public bool CanToolDuplicate => _tool?.InList == true;
    public bool CanToolUnwrap => _tool?.CanUnwrap == true;
    public bool CanToolExpanded => _tool?.CanExpanded == true;
    public bool ToolHasChildren => _tool?.HasChildren == true;

    // ───────────────────────────── the widget the tools work on ─────────────────────────────

    /// <summary>The picked widget's place in the code: exact on the real picture (Flutter's inspector), else the
    /// wireframe block's line and the widget's name on it.</summary>
    private (string File, int Line, int Column)? ToolTarget()
    {
        if (_stageBox is { } b) return (b.File, b.Line, b.Column);
        if (_stageNode is { File: { } f, Line: > 0 } n && n.Widget is { Length: > 0 } w && ReadSource(f) is { } text)
        {
            int col = FlutterCodeEdit.ColumnOf(text, n.Line, w);
            if (col > 0) return (f, n.Line, col);
        }
        return null;
    }

    /// <summary>Read the picked widget from its code again (after a pick, an edit, a new picture).</summary>
    internal void RefreshTools()
    {
        FlutterWidgetInfo? info = null;
        var at = ToolTarget();
        if (at is { } a && IsEditable(a.File) && ReadSource(a.File) is { } text)
        {
            try { info = FlutterCodeEdit.Inspect(text, a.Line, a.Column); }
            catch (Exception ex) { _log?.Warn("UiStudio", "inspect failed", ex); }
            // the code moved under a pick from an older picture: another widget is there now — no tools on it
            if (info != null && _stageBox is { Widget.Length: > 0 } box && !SameWidget(info.Name, box.Widget)) info = null;
        }
        _tool = info;
        _toolAt = info != null ? at : null;
        _toolText = info?.Text ?? "";
        _toolFontSize = Fmt(info?.FontSize);
        _toolPadding = Fmt(info?.Padding);
        _toolMargin = Fmt(info?.Margin);
        _toolRadius = Fmt(info?.Radius);
        _toolWidth = Fmt(info?.Width);
        _toolHeight = Fmt(info?.Height);
        _toolTextColorHex = info?.TextColor is { } tc && tc.StartsWith('#') ? tc : "";
        _toolColorHex = info?.Color is { } cc && cc.StartsWith('#') ? cc : "";
        foreach (var n in ToolProps) OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

    /// <summary>"Image.asset" is the inspector's "Image"; "Text-[<'k'>]" (a keyed widget) is "Text".</summary>
    private static bool SameWidget(string codeName, string inspectorName)
    {
        string a = codeName.Split('.')[0];
        string b = inspectorName.Split('-')[0].Split('<')[0].Trim();
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static bool SameFile(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Only the app's own code under lib/ is changed (not packages, not generated files).</summary>
    private bool IsEditable(string file)
    {
        if (!_studio.HasProject) return false;
        string lib = Path.Combine(_studio.Root, "lib") + Path.DirectorySeparatorChar;
        return file.StartsWith(lib, StringComparison.OrdinalIgnoreCase) && file.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)
               && !Regex.IsMatch(file, @"\.(g|freezed|gr|config|mocks)\.dart$", RegexOptions.IgnoreCase) && File.Exists(file);
    }

    private static string? ReadSource(string file)
    {
        try { var f = CluadeX.Helpers.TextFileCodec.Read(file); return f.IsValidUtf8 ? f.Text : null; }
        catch { return null; }
    }

    // ───────────────────────────── running an edit ─────────────────────────────

    /// <summary>Apply one tool to the picked widget (or <paramref name="target"/>): the file is written (Undo takes it
    /// back), the boxes follow the code until the picture is drawn again, and the edited widget stays picked.</summary>
    private void RunTool(string label, Func<string, int, int, FlutterEdit> op, (string File, int Line, int Column)? target = null)
    {
        if ((target ?? _toolAt) is not { } at) return;
        if (AiWorking) { Status(T("studio.fedit.aiBusy"), error: true); return; }
        if (IsChecking) { Status(T("studio.fedit.checkBusy"), error: true); return; }
        CluadeX.Helpers.TextFileCodec.TextFile file;
        try { file = CluadeX.Helpers.TextFileCodec.Read(at.File); }
        catch { Status(T("studio.err.read"), error: true); return; }
        if (!file.IsValidUtf8) { Status(T("studio.err.notUtf8"), error: true); return; }
        FlutterEdit r;
        try { r = op(file.Text, at.Line, at.Column); }
        catch (Exception ex) { _log?.Warn("UiStudio", "flutter tool failed: " + label, ex); Status(T("studio.fedit.failed"), error: true); return; }
        if (!r.Ok) { Status(T(r.Error ?? "studio.fedit.failed"), error: true); return; }
        if (r.Text == null || r.Text == file.Text) { RefreshTools(); return; }
        string? err = _studio.WriteEdit(at.File, file.Text, r.Text, file.HasBom, "flutter-" + label);
        if (err != null) { Status(T(err), error: true); return; }
        NoteToolEdit(at.File, file.Text, r.Text);

        // until Flutter draws it again, the boxes follow the code (what the edit rewrote has no box for now)
        string path = at.File;
        StageBoxes = _stageBoxes.Select(b => !SameFile(b.File, path) ? b : r.Map(b.Line, b.Column) is { } p ? b with { Line = p.Line, Column = p.Column } : null)
                                .Where(b => b != null).Select(b => b!).ToList();
        if (r.FocusLine > 0) SelectAt(path, r.FocusLine, r.FocusColumn, r.Text);
        else { StageBox = null; StageNode = null; }
        Status(T("studio.fedit.applied"));
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Pick the widget at a place of the new code: its box when the picture has it, else a mark there until
    /// the picture is drawn again.</summary>
    private void SelectAt(string file, int line, int column, string text)
    {
        var hit = _stageBoxes.FirstOrDefault(b => SameFile(b.File, file) && b.Line == line && b.Column == column);
        if (hit != null) { _pendingFocus = null; StageBox = hit; return; }
        _pendingFocus = (file, line, column);
        var prev = _stageBox;
        string name = FlutterCodeEdit.Inspect(text, line, column)?.Name.Split('.')[0] ?? prev?.Widget ?? "";
        StageBox = new WidgetBox(name, file, line, column, prev?.X ?? 0, prev?.Y ?? 0, 0, 0, prev?.Depth ?? 0);
    }

    /// <summary>After a new picture: the widget an edit made (or moved) is picked on it.</summary>
    private void ResolvePendingFocus()
    {
        if (_pendingFocus is { } p)
        {
            var hit = _stageBoxes.FirstOrDefault(b => SameFile(b.File, p.File) && b.Line == p.Line && b.Column == p.Column);
            if (hit != null) { _pendingFocus = null; StageBox = hit; return; }
        }
        else if (_stageBox is { } cur)
        {
            // the same widget on the new picture (its new size and place)
            var same = _stageBoxes.FirstOrDefault(b => SameFile(b.File, cur.File) && b.Line == cur.Line && b.Column == cur.Column);
            if (same != null && !ReferenceEquals(same, cur)) { StageBox = same; return; }
        }
        RefreshTools();
    }

    private static double? ParseNumber(string s)
    {
        s = s.Trim().Replace(',', '.');
        return s.Length == 0 ? null : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 10000 ? v : double.NaN;
    }

    private void ApplyNumber(string which)
    {
        string raw = which switch
        {
            "fontSize" => ToolFontSize, "padding" => ToolPadding, "margin" => ToolMargin, "radius" => ToolRadius, _ => "",
        };
        if (which == "size")
        {
            var w = ParseNumber(ToolWidth);
            var h = ParseNumber(ToolHeight);
            if (w is double.NaN || h is double.NaN) { Status(T("studio.fedit.badNumber"), error: true); return; }
            RunTool("size", (t, l, c) => FlutterCodeEdit.SetSize(t, l, c, w, h));
            return;
        }
        var v = ParseNumber(raw);
        if (v is double.NaN) { Status(T("studio.fedit.badNumber"), error: true); return; }
        RunNumber(which, v);
    }

    private void RunNumber(string which, double? v)
    {
        switch (which)
        {
            case "fontSize": RunTool("style", (t, l, c) => FlutterCodeEdit.SetFontSize(t, l, c, v is > 0 ? v : null)); break;
            case "padding": RunTool("padding", (t, l, c) => FlutterCodeEdit.SetPadding(t, l, c, v)); break;
            case "margin": RunTool("margin", (t, l, c) => FlutterCodeEdit.SetMargin(t, l, c, v)); break;
            case "radius": RunTool("radius", (t, l, c) => FlutterCodeEdit.SetRadius(t, l, c, v)); break;
        }
    }

    /// <summary>"fontSize:+2", "padding:-4": one step from what the code says (or from a sensible start).</summary>
    private void Step(string? spec)
    {
        if (spec == null || _tool == null) return;
        var parts = spec.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by)) return;
        string which = parts[0];
        double? now = which switch
        {
            "fontSize" => _tool.FontSize ?? 14, "padding" => _tool.Padding ?? 0, "margin" => _tool.Margin ?? 0, "radius" => _tool.Radius ?? 0, _ => null,
        };
        if (now == null) return;
        double next = Math.Max(0, now.Value + by);
        RunNumber(which, which != "fontSize" && next == 0 ? null : next);
    }

    // ───────────────────────────── palette and colours ─────────────────────────────

    private ObservableCollection<WidgetPaletteItem>? _palette;
    public ObservableCollection<WidgetPaletteItem> Palette => _palette ??= BuildPalette();

    private ObservableCollection<WidgetPaletteItem> BuildPalette()
    {
        string Q(string key) => FlutterCodeEdit.QuoteText(T(key));
        string text = Q("studio.fedit.sample.text"), button = Q("studio.fedit.sample.button"), link = Q("studio.fedit.sample.link"),
               title = Q("studio.fedit.sample.title"), sub = Q("studio.fedit.sample.subtitle"), field = Q("studio.fedit.sample.field");
        var items = new (string Key, string Glyph, string Code)[]
        {
            ("text", "", $"const Text({text})"),
            ("button", "", $"ElevatedButton(onPressed: () {{}}, child: const Text({button}))"),
            ("outlined", "", $"OutlinedButton(onPressed: () {{}}, child: const Text({button}))"),
            ("link", "", $"TextButton(onPressed: () {{}}, child: const Text({link}))"),
            ("icon", "", "const Icon(Icons.star)"),
            ("image", "", ImageSnippet()),
            ("box", "", "Container(height: 80, color: const Color(0xFFE0E0E0))"),
            ("space", "", "const SizedBox(height: 16)"),
            ("divider", "", "const Divider()"),
            ("card", "", $"const Card(child: ListTile(title: Text({title}), subtitle: Text({sub})))"),
            ("tile", "", $"const ListTile(leading: Icon(Icons.circle_outlined), title: Text({title}))"),
            ("field", "", $"const TextField(decoration: InputDecoration(labelText: {field}))"),
            ("row", "", $"const Row(children: [Icon(Icons.star), SizedBox(width: 8), Text({text})])"),
            ("column", "", $"const Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text({title}), Text({sub})])"),
            ("switch", "", "Switch(value: true, onChanged: (_) {})"),
            ("progress", "", "const CircularProgressIndicator()"),
        };
        return new ObservableCollection<WidgetPaletteItem>(items.Select(i => new WidgetPaletteItem { Key = i.Key, Glyph = i.Glyph, Code = i.Code, Label = T("studio.fedit.palette." + i.Key) }));
    }

    /// <summary>A picture from the app's own assets when it has one, else an image icon.</summary>
    private string ImageSnippet()
    {
        try
        {
            if (_studio.HasProject)
            {
                string root = _studio.Root;
                foreach (var dir in new[] { "assets/images", "assets/img", "assets", "images" })
                {
                    string full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(full)) continue;
                    var img = Directory.EnumerateFiles(full, "*.*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(f => Regex.IsMatch(f, @"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase));
                    if (img != null)
                    {
                        string rel = Path.GetRelativePath(root, img).Replace('\\', '/');
                        if (PubspecDeclares(root, rel)) return $"Image.asset({FlutterCodeEdit.QuoteText(rel)}, height: 120)";
                    }
                }
            }
        }
        catch { }
        return "const Icon(Icons.image_outlined, size: 64)";
    }

    private static bool PubspecDeclares(string root, string asset)
    {
        try
        {
            string pub = File.ReadAllText(Path.Combine(root, "pubspec.yaml"));
            string dir = asset.Contains('/') ? asset[..(asset.LastIndexOf('/') + 1)] : "";
            return pub.Contains("- " + asset, StringComparison.Ordinal) || dir.Length > 0 && pub.Contains("- " + dir, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private ObservableCollection<SwatchItem>? _swatches;
    /// <summary>The app's own colours (the most used Color(0x…) literals and its seed colour), then the basics.</summary>
    public ObservableCollection<SwatchItem> Swatches => _swatches ??= BuildSwatches();

    private ObservableCollection<SwatchItem> BuildSwatches()
    {
        var counts = new Dictionary<uint, int>();
        try
        {
            if (_studio.HasProject && Directory.Exists(Path.Combine(_studio.Root, "lib")))
                foreach (var f in Directory.EnumerateFiles(Path.Combine(_studio.Root, "lib"), "*.dart", SearchOption.AllDirectories).Take(600))
                {
                    string t;
                    try { t = File.ReadAllText(f); } catch { continue; }
                    foreach (Match m in Regex.Matches(t, @"Color\(\s*0x([0-9A-Fa-f]{8})\s*\)"))
                    {
                        uint v = Convert.ToUInt32(m.Groups[1].Value, 16);
                        if (v >> 24 != 0xFF) continue;
                        counts[v] = counts.GetValueOrDefault(v) + 1;
                    }
                }
        }
        catch { }
        var list = new List<uint>();
        if (_catalog?.Flutter?.Theme is { } th) list.Add(th.Accent | 0xFF000000);
        list.AddRange(counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(8));
        list.AddRange(new uint[] { 0xFFFFFFFF, 0xFF000000, 0xFF757575, 0xFFE53935, 0xFFFB8C00, 0xFF43A047, 0xFF1E88E5, 0xFF8E24AA });
        return new ObservableCollection<SwatchItem>(list.Distinct().Take(16).Select(v =>
        {
            var c = Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            var b = new SolidColorBrush(c);
            b.Freeze();
            string hex = $"#{v & 0xFFFFFF:X6}";
            return new SwatchItem { Hex = hex, Brush = b, Tip = counts.ContainsKey(v) ? T("studio.fedit.swatchApp", hex) : hex };
        }));
    }

    private void InsertPalette(WidgetPaletteItem? item)
    {
        if (item == null || _toolAt == null) { Status(T("studio.fedit.pickFirst"), error: true); return; }
        var where = ToolHasChildren ? FlutterCodeEdit.Place.Inside : FlutterCodeEdit.Place.After;
        RunTool("insert", (t, l, c) => FlutterCodeEdit.Insert(t, l, c, item.Code, where));
    }

    /// <summary>Where a palette widget dragged to (x, y) on the picture — logical pixels — would go (null: nowhere).</summary>
    public FlutterDropPlan? PlanDrop(double x, double y)
    {
        if (_stageBoxes.Count == 0 || !ShowRealRender || AiWorking) return null;
        try { return FlutterCodeEdit.PlanDrop(_stageBoxes, x, y, ReadSource, IsEditable); }
        catch (Exception ex) { _log?.Warn("UiStudio", "drop plan failed", ex); return null; }
    }

    /// <summary>A palette widget dropped on the picture: it is written where <see cref="PlanDrop"/> says.</summary>
    public void DropPalette(string key, double x, double y)
    {
        var item = Palette.FirstOrDefault(p => p.Key == key);
        if (item == null) return;
        var plan = PlanDrop(x, y);
        if (plan == null) { Status(T("studio.fedit.dropNowhere"), error: true); return; }
        RunTool("insert", (t, l, c) => FlutterCodeEdit.InsertAt(t, l, c, plan.Index, item.Code), (plan.File, plan.OwnerLine, plan.OwnerColumn));
    }

    private bool ConfirmDelete()
    {
        if (_tool == null) return false;
        if (ToolConfirmOverride is bool forced) return forced;
        int choice = Views.Editor.ChoiceDialog.Show(T("studio.fedit.deleteTitle"), T("studio.fedit.deleteBody", _tool.Name), new[] { T("studio.fedit.delete"), T("studio.cancel") }, 1);
        return choice == 0;
    }

    /// <summary>Set by a harness to answer the Studio's confirmations (delete a widget, undo every change) without a dialog.</summary>
    public bool? ToolConfirmOverride { get; set; }

    // ───────────────────────────── commands ─────────────────────────────

    private ICommand? _applyToolTextCommand, _applyToolNumberCommand, _stepToolCommand, _setToolColorCommand, _setToolTextColorCommand,
        _applyToolColorHexCommand, _setToolAlignCommand, _toolMoveCommand, _toolDuplicateCommand, _toolDeleteCommand, _toolWrapCommand,
        _toolUnwrapCommand, _insertPaletteCommand;

    public ICommand ApplyToolTextCommand => _applyToolTextCommand ??= new RelayCommand(() =>
    {
        if (_tool?.CanText != true || ToolText == _tool.Text) return;
        string v = ToolText;
        RunTool("text", (t, l, c) => FlutterCodeEdit.SetText(t, l, c, v));
    }, () => CanToolText);

    /// <summary>Parameter: fontSize / padding / margin / radius / size — writes what its box says (empty: removes it).</summary>
    public ICommand ApplyToolNumberCommand => _applyToolNumberCommand ??= new RelayCommand<string>(w => { if (w != null && _tool != null) ApplyNumber(w); });
    public ICommand StepToolCommand => _stepToolCommand ??= new RelayCommand<string>(Step);

    /// <summary>Parameter: #RRGGBB, or empty to take the colour away.</summary>
    public ICommand SetToolColorCommand => _setToolColorCommand ??= new RelayCommand<string>(hex =>
    {
        string? h = string.IsNullOrWhiteSpace(hex) ? null : hex;
        RunTool("color", (t, l, c) => FlutterCodeEdit.SetColor(t, l, c, h));
    });
    public ICommand SetToolTextColorCommand => _setToolTextColorCommand ??= new RelayCommand<string>(hex =>
    {
        string? h = string.IsNullOrWhiteSpace(hex) ? null : hex;
        RunTool("style", (t, l, c) => FlutterCodeEdit.SetTextColor(t, l, c, h));
    });
    /// <summary>Parameter: "text" (the text colour box) or "color" (the widget's own colour box).</summary>
    public ICommand ApplyToolColorHexCommand => _applyToolColorHexCommand ??= new RelayCommand<string>(which =>
    {
        string hex = which == "text" ? ToolTextColorHex : ToolColorHex;
        if (hex.Trim().Length > 0 && FlutterCodeEdit.IsColor(hex) == false) { Status(T("studio.fedit.color.bad"), error: true); return; }
        if (which == "text") SetToolTextColorCommand.Execute(hex); else SetToolColorCommand.Execute(hex);
    });
    public ICommand SetToolAlignCommand => _setToolAlignCommand ??= new RelayCommand<string>(a =>
    {
        string? v = string.IsNullOrEmpty(a) ? null : a;
        RunTool("style", (t, l, c) => FlutterCodeEdit.SetTextAlign(t, l, c, v));
    });
    /// <summary>Parameter: -1 (up / left) or 1 (down / right).</summary>
    public ICommand ToolMoveCommand => _toolMoveCommand ??= new RelayCommand<string>(d =>
    {
        int delta = d == "-1" ? -1 : 1;
        RunTool("move", (t, l, c) => FlutterCodeEdit.Move(t, l, c, delta));
    });
    public ICommand ToolDuplicateCommand => _toolDuplicateCommand ??= new RelayCommand(() => RunTool("duplicate", FlutterCodeEdit.Duplicate), () => CanToolDuplicate);
    public ICommand ToolDeleteCommand => _toolDeleteCommand ??= new RelayCommand(() =>
    {
        if (!ConfirmDelete()) return;
        RunTool("delete", FlutterCodeEdit.Delete);
    }, () => HasTools);
    /// <summary>Parameter: Padding, Center, Card, Expanded, SizedBox, Align, InkWell, ClipRRect.</summary>
    public ICommand ToolWrapCommand => _toolWrapCommand ??= new RelayCommand<string>(w =>
    {
        if (string.IsNullOrEmpty(w)) return;
        RunTool("wrap", (t, l, c) => FlutterCodeEdit.Wrap(t, l, c, w));
    });
    public ICommand ToolUnwrapCommand => _toolUnwrapCommand ??= new RelayCommand(() => RunTool("unwrap", FlutterCodeEdit.Unwrap), () => CanToolUnwrap);
    public ICommand InsertPaletteCommand => _insertPaletteCommand ??= new RelayCommand<WidgetPaletteItem>(InsertPalette);
}
