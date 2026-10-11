using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;

namespace ThemeTest;

/// <summary>
/// Opt-in extras for the converter teams (all off by default, so the standard run is unchanged):
///
///   --all-pages        also render the pages the standard run skips (the other Settings categories and the manager
///                      pages). They have no image in the shared golden, so they are compared only when the --out
///                      folder has one (capture it once with --golden from a build BEFORE the theme work).
///   --only a,b,c       render only these page / dialog keys (an extra page key works without --all-pages).
///   --probe            the "does this page follow the theme?" check. On every page, live: render under the current
///                      theme, swap the theme slot for a copy whose every TOKEN colour is inverted (255 − c; legacy
///                      keys keep their value), render again. With aliased text and edges every pixel drawn from
///                      tokens then inverts exactly, so a pixel that does not is drawn from something that ignores the
///                      theme: a literal colour, a legacy key, a StaticResource that was resolved once, a bound colour,
///                      a bitmap. The element audit names them: every brush / effect on the page that is not a token
///                      instance. Results: renders\probe\&lt;page&gt;.png (red = not following) + PROBE lines in the report.
/// </summary>
internal static partial class Program
{
    static bool AllPages, ProbeMode;
    static string[] OnlyPages;
    static int ProbeTolerance = 16;

    /// <summary>Pages the standard run does not render.</summary>
    static readonly (string Key, string Nav, string Label)[] ExtraPages =
    {
        ("settings-model", "Settings", "Settings › Model"),
        ("settings-local", "Settings", "Settings › Local"),
        ("settings-agent", "Settings", "Settings › Agent"),
        ("settings-autonomous", "Settings", "Settings › Autonomous"),
        ("settings-memory", "Settings", "Settings › Memory"),
        ("permissions", "Permissions", "Permissions"),
        ("sub-agents", "SubAgents", "Sub-agents"),
        ("hook-library", "HookLibrary", "Hook library"),
        ("instincts", "Instincts", "Instincts"),
        ("security-shield", "SecurityShield", "Security shield"),
        ("time-machine", "TimeMachine", "Time Machine"),
        ("hex-editor", "HexEditor", "Hex editor"),
        ("debug-log", "DebugLog", "Debug log"),
    };

    /// <summary>The view class each page key shows (the element audit walks only that view).</summary>
    static readonly Dictionary<string, string> PageViews = new()
    {
        ["chat"] = "ChatView", ["code-editor"] = "CodeEditorView", ["settings"] = "SettingsView",
        ["features"] = "FeaturesView", ["model-manager"] = "ModelManagerView", ["mcp-servers"] = "McpServersView",
        ["plugins"] = "PluginManagerView", ["skills"] = "SkillsView", ["task-manager"] = "TaskManagerView",
        ["permissions"] = "PermissionsView", ["sub-agents"] = "SubAgentsView", ["hook-library"] = "HookLibraryView",
        ["instincts"] = "InstinctsView", ["security-shield"] = "SecurityShieldView", ["time-machine"] = "TimeMachineView",
        ["hex-editor"] = "HexEditorView", ["debug-log"] = "DebugLogView",
    };

    /// <summary>settings → provider, settings-general → general, settings-model → model …</summary>
    static string SettingsCategoryOf(string key) => key.StartsWith("settings-") ? key["settings-".Length..] : "provider";

    static void SelectPages()
    {
        if (AllPages) Pages = Pages.Concat(ExtraPages).ToArray();
        if (OnlyPages == null) return;
        var pool = AllPages ? Pages : Pages.Concat(ExtraPages).ToArray();
        var unknown = OnlyPages.Where(k => !pool.Any(p => p.Key == k) && !Dialogs.Any(d => d.Key == k)).ToList();
        if (unknown.Count > 0) Console.WriteLine("warning: --only names unknown page(s): " + string.Join(", ", unknown));
        Pages = pool.Where(p => OnlyPages.Contains(p.Key)).ToArray();
        Dialogs = Dialogs.Where(d => OnlyPages.Contains(d.Key)).ToArray();
    }

    // ═══════════════════════════════════════════ the probe ═══════════════════════════════════════════

    static async Task RunProbe()
    {
        Log("\n## Probe — every token inverted live on each page; what does not invert ignores the theme");
        var asm = typeof(CluadeX.App).Assembly;
        var tokensType = asm.GetType("CluadeX.Services.ThemeTokens");
        var tokenKeys = ((IEnumerable)tokensType.GetProperty("All").GetValue(null)).Cast<string>().ToHashSet();
        var legacyKeys = ((IEnumerable)tokensType.GetProperty("Legacy").GetValue(null)).Cast<string>().ToHashSet();

        var appRes = Application.Current.Resources;
        int slotIndex = -1;
        for (int i = 0; i < appRes.MergedDictionaries.Count; i++)
            if (appRes.MergedDictionaries[i].Contains("Theme.Id")) { slotIndex = i; break; }
        if (slotIndex < 0) { Check("probe: the theme slot is in Application.Resources", false); return; }
        var slot = appRes.MergedDictionaries[slotIndex];

        var inverted = new ResourceDictionary();
        var tokenValues = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var legacyValues = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int invertedCount = 0;
        foreach (string k in Keys(slot))
        {
            object v = slot[k];
            if (tokenKeys.Contains(k))
            {
                object iv = Invert(v);
                if (!ReferenceEquals(iv, v)) invertedCount++;
                inverted[k] = iv;
                if (iv != null) tokenValues.Add(iv);
            }
            else
            {
                inverted[k] = v;
                if (v != null && legacyKeys.Contains(k)) legacyValues.Add(v);
            }
        }
        Log($"probe theme: {invertedCount} colour tokens inverted, {legacyValues.Count} legacy keys kept · tolerance {ProbeTolerance}/255");

        string dir = Path.Combine(OutRoot, "renders", "probe");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // Aliased text and edges: every pixel is then a linear mix of theme colours, so a followed pixel inverts exactly.
        TextOptions.SetTextRenderingMode(MainWin, TextRenderingMode.Aliased);
        RenderOptions.SetEdgeMode(MainWin, EdgeMode.Aliased);
        var settingsVm = MainVm.SettingsVM;
        int pagesClean = 0;
        try
        {
            foreach (var (key, nav, _) in Pages)
            {
                MainVm.NavigateToCommand.Execute(nav == "Permissions" ? "Chat" : "Permissions");
                await Idle();
                MainVm.NavigateToCommand.Execute(nav);
                if (nav == "Settings") settingsVm.SelectedCategory = SettingsCategoryOf(key);
                FreezeVolatile();
                var before = await Stable(() => (RootVisual(MainWin), W, H), $"probe/{key}/theme");

                appRes.MergedDictionaries[slotIndex] = inverted;
                try
                {
                    FreezeVolatile();
                    var after = await Stable(() => (RootVisual(MainWin), W, H), $"probe/{key}/inverted");
                    var pb = Pixels(before);
                    var pa = Pixels(after);
                    var mask = NonInverting(pb, pa);

                    var page = FindPageView(key);
                    var root = (Visual)RootVisual(MainWin);
                    Rect pageRect = page != null && page.IsVisible
                        ? page.TransformToAncestor(root).TransformBounds(new Rect(page.RenderSize))
                        : new Rect(0, 0, W, H);

                    var stray = Audit(page ?? (DependencyObject)root, tokenValues, legacyValues);
                    var regions = Regions(mask, minArea: 24);
                    int inPage = 0, pagePx = 0;
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                            if (pageRect.Contains(x + 0.5, y + 0.5)) { pagePx++; if (mask[y * W + x]) inPage++; }
                    double pct = pagePx == 0 ? 0 : 100.0 * inPage / pagePx;

                    SavePng(Overlay(pb, mask), Path.Combine(dir, key + ".png"));
                    SavePng(after, Path.Combine(dir, key + "-inverted.png"));

                    var pageRegions = regions.Where(r => pageRect.IntersectsWith(r.Box)).ToList();
                    bool clean = stray.Count == 0 && pageRegions.Count == 0;
                    if (clean) pagesClean++;
                    Log($"PROBE  {key}: {pct:F3} % of the page does not follow the theme ({inPage} px, {pageRegions.Count} region(s) ≥ 24 px) · " +
                        $"{stray.Count} element colour(s) are not theme tokens{(page == null ? " · (view not found — audited the whole window)" : "")}");

                    foreach (var g in stray.GroupBy(s => s.Signature).OrderByDescending(g => g.Count()).Take(40))
                    {
                        var s = g.First();
                        Log($"         element  {s.What} · {s.Prop} {s.Value} — {s.Kind}{(g.Count() > 1 ? $" ×{g.Count()}" : "")}{(s.Text.Length > 0 ? $"  “{s.Text}”" : "")}");
                    }
                    foreach (var r in pageRegions.OrderByDescending(r => r.Area).Take(12))
                    {
                        var owner = stray.Where(s => s.Box.Contains(r.Sample)).OrderBy(s => s.Box.Width * s.Box.Height).FirstOrDefault();
                        string who = owner != null ? $"{owner.What} · {owner.Prop} {owner.Value}" : "← " + DescribeHit(root, r.Sample);
                        Log($"         region   {r.Box.Width}×{r.Box.Height} at ({r.Box.X},{r.Box.Y}), {r.Area} px  {who}");
                    }
                }
                finally
                {
                    appRes.MergedDictionaries[slotIndex] = slot;
                    await Idle();
                }
            }
        }
        finally
        {
            MainWin.ClearValue(TextOptions.TextRenderingModeProperty);
            MainWin.ClearValue(RenderOptions.EdgeModeProperty);
            settingsVm.SelectedCategory = "provider";
            MainVm.NavigateToCommand.Execute("Chat");
            await Idle();
        }
        Log($"probe: {pagesClean} of {Pages.Length} page(s) follow the theme completely · images in {dir}");
    }

    static object Invert(object v)
    {
        switch (v)
        {
            case SolidColorBrush b:
            {
                var c = b.CloneCurrentValue(); c.Color = Inv(b.Color); c.Freeze(); return c;
            }
            case GradientBrush g:
            {
                var c = g.CloneCurrentValue();
                foreach (var s in c.GradientStops) s.Color = Inv(s.Color);
                c.Freeze(); return c;
            }
            case DropShadowEffect e:
            {
                var c = e.CloneCurrentValue(); c.Color = Inv(e.Color); c.Freeze(); return c;
            }
            case Color col: return Inv(col);
            default: return v;
        }
    }

    static Color Inv(Color c) => Color.FromArgb(c.A, (byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

    /// <summary>Premultiplied BGRA: a pixel drawn only from inverted colours is alpha − before in every channel.</summary>
    static bool[] NonInverting(byte[] before, byte[] after)
    {
        var mask = new bool[W * H];
        for (int p = 0, i = 0; p < mask.Length; p++, i += 4)
        {
            int a = before[i + 3];
            int d = Math.Abs(after[i + 3] - a);
            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(after[i + c] - (a - before[i + c])));
            mask[p] = d > ProbeTolerance;
        }
        return mask;
    }

    sealed record Region(Int32Rect Box0, int Area, Point Sample)
    {
        public Rect Box => new(Box0.X, Box0.Y, Box0.Width, Box0.Height);
    }

    /// <summary>8-connected groups of marked pixels, at least <paramref name="minArea"/> px.</summary>
    static List<Region> Regions(bool[] mask, int minArea)
    {
        var seen = new bool[mask.Length];
        var list = new List<Region>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, area = 0;
            seen[start] = true; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); area++;
                int x = p % W, y = p / W;
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                        int q = ny * W + nx;
                        if (mask[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
                    }
            }
            if (area >= minArea)
                list.Add(new Region(new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1), area,
                    new Point(start % W + 0.5, start / W + 0.5)));
        }
        return list;
    }

    static BitmapSource Overlay(byte[] before, bool[] mask)
    {
        var px = new byte[before.Length];
        for (int p = 0, i = 0; p < mask.Length; p++, i += 4)
        {
            if (mask[p]) { px[i] = 0x30; px[i + 1] = 0x30; px[i + 2] = 0xFF; }
            else { px[i] = (byte)(before[i] / 3); px[i + 1] = (byte)(before[i + 1] / 3); px[i + 2] = (byte)(before[i + 2] / 3); }
            px[i + 3] = 0xFF;
        }
        return BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, px, W * 4);
    }

    static FrameworkElement FindPageView(string key)
    {
        string k = key.StartsWith("settings") ? "settings" : key;
        if (!PageViews.TryGetValue(k, out var type)) return null;
        return Find<UserControl>(MainWin).FirstOrDefault(u => u.GetType().Name == type && u.IsVisible);
    }

    // ═══════════════════════════════════════════ element audit ═══════════════════════════════════════════

    sealed record Stray(string What, string Prop, string Value, string Kind, string Text, Rect Box)
    {
        public string Signature => $"{What}|{Prop}|{Value}|{Kind}";
    }

    /// <summary>Every brush / coloured effect set on an element of the page (not inherited, not a default) that is
    /// not one of the current theme's token instances.</summary>
    static List<Stray> Audit(DependencyObject page, HashSet<object> tokenValues, HashSet<object> legacyValues)
    {
        var list = new List<Stray>();
        var root = (Visual)RootVisual(MainWin);
        foreach (var el in new[] { page }.Concat(Find<DependencyObject>(page)))
        {
            // Inside a control drawn by the SYSTEM template (Aero2), the colours are Windows', not ours: name the
            // control once if it is on screen, skip its parts.
            if (!InsideSystemTemplate(el, drawnOnly: true) && el is Control sc && SystemDrawn(sc)
                && DependencyPropertyHelper.GetValueSource(sc, Control.TemplateProperty).BaseValueSource == BaseValueSource.DefaultStyle
                && sc.IsVisible && sc.ActualWidth > 0 && sc.ActualHeight > 0
                // a borderless box on a transparent background shows nothing of the system template
                && !(sc is TextBox or ListBox or PasswordBox && sc.BorderThickness == default && Invisible(sc.Background ?? Brushes.Transparent)))
            {
                list.Add(new Stray(What(sc, sc), "Template", "system (Aero2) template", "system look — ignores the theme", TextOf(sc),
                    sc.TransformToAncestor(root).TransformBounds(new Rect(sc.RenderSize))));
            }
            if (InsideSystemTemplate(el, drawnOnly: false)) continue;
            // Text nobody gave a colour: it falls back to the system's black.
            if (el is TextBlock dt && dt.IsVisible && !string.IsNullOrWhiteSpace(dt.Text)
                && DependencyPropertyHelper.GetValueSource(dt, TextBlock.ForegroundProperty).BaseValueSource == BaseValueSource.Default)
            {
                list.Add(new Stray(What(dt, dt), "Foreground", dt.Foreground is SolidColorBrush db ? db.Color.ToString() : "?",
                    "no colour set — system default", TextOf(dt), dt.TransformToAncestor(root).TransformBounds(new Rect(dt.RenderSize))));
            }
            foreach (var (dp, name) in BrushProps(el))
                Consider(el, el, dp, name);
            if (el is TextBlock tb)
                foreach (var inline in Inlines(tb.Inlines))
                {
                    Consider(inline, tb, TextElement.ForegroundProperty, "Run.Foreground");
                    Consider(inline, tb, TextElement.BackgroundProperty, "Run.Background");
                }
        }
        return list;

        void Consider(DependencyObject owner, DependencyObject visualOwner, DependencyProperty dp, string name)
        {
            var src = DependencyPropertyHelper.GetValueSource(owner, dp);
            if (src.BaseValueSource is BaseValueSource.Default or BaseValueSource.Inherited or BaseValueSource.Unknown
                or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger) return;
            object v = owner.GetValue(dp);
            if (v == null || Invisible(v) || tokenValues.Contains(v)) return;
            if (v is Effect && v is not DropShadowEffect) return;
            string kind =
                legacyValues.Contains(v) ? "legacy key" :
                BindingOperations.GetBindingExpressionBase(owner, dp) != null ? "bound (binding / converter)" :
                BoundColour(v) ? "data colour (bound)" :
                $"literal ({src.BaseValueSource})";
            if (owner is FrameworkElement fe0 && fe0.TemplatedParent != null && src.BaseValueSource == BaseValueSource.ParentTemplate)
                kind = "literal (control template)";
            Rect box = Rect.Empty;
            if (visualOwner is Visual vis && visualOwner is UIElement ue && ue.IsVisible)
            {
                try { box = vis.TransformToAncestor(root).TransformBounds(new Rect(ue.RenderSize)); } catch { }
            }
            list.Add(new Stray(What(visualOwner, owner), name, Fmt(v), kind, TextOf(visualOwner), box));
        }
    }

    /// <summary>True for a part of a control whose template came from the system theme (its colours are Windows').</summary>
    static bool InsideSystemTemplate(DependencyObject el, bool drawnOnly)
    {
        for (var tp = (el as FrameworkElement)?.TemplatedParent as Control; tp != null; tp = tp.TemplatedParent as Control)
            if (DependencyPropertyHelper.GetValueSource(tp, Control.TemplateProperty).BaseValueSource == BaseValueSource.DefaultStyle
                && (!drawnOnly || SystemDrawn(tp)))
                return true;
        return false;
    }

    /// <summary>Controls whose system template paints something of its own (a ScrollViewer or a plain ContentControl
    /// only hosts content; a ScrollBar, button, box or bar draws Windows' colours).</summary>
    static bool SystemDrawn(Control c) => c is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.ButtonBase
        or TextBox or PasswordBox or ComboBox or ListBox or Slider or ProgressBar or Expander or TabControl or TabItem
        or ListBoxItem or ComboBoxItem or MenuItem;

    static IEnumerable<(DependencyProperty, string)> BrushProps(DependencyObject el)
    {
        switch (el)
        {
            case Border: yield return (Border.BackgroundProperty, "Background"); yield return (Border.BorderBrushProperty, "BorderBrush"); break;
            case Panel: yield return (Panel.BackgroundProperty, "Background"); break;
            case TextBlock: yield return (TextBlock.ForegroundProperty, "Foreground"); yield return (TextBlock.BackgroundProperty, "Background"); break;
            case Control:
                yield return (Control.BackgroundProperty, "Background"); yield return (Control.ForegroundProperty, "Foreground");
                yield return (Control.BorderBrushProperty, "BorderBrush");
                if (el is System.Windows.Controls.Primitives.TextBoxBase) yield return (System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "CaretBrush");
                break;
            case Shape: yield return (Shape.FillProperty, "Fill"); yield return (Shape.StrokeProperty, "Stroke"); break;
        }
        if (el is UIElement) yield return (UIElement.EffectProperty, "Effect");
    }

    static IEnumerable<Inline> Inlines(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i is Span s) foreach (var x in Inlines(s.Inlines)) yield return x;
        }
    }

    static bool Invisible(object v) => v switch
    {
        SolidColorBrush b => b.Color.A == 0 || b.Opacity == 0,
        GradientBrush g => g.Opacity == 0 || g.GradientStops.All(s => s.Color.A == 0),
        DropShadowEffect e => e.Opacity == 0,
        _ => false,
    };

    static bool BoundColour(object v) => v switch
    {
        SolidColorBrush b => BindingOperations.IsDataBound(b, SolidColorBrush.ColorProperty),
        DropShadowEffect e => BindingOperations.IsDataBound(e, DropShadowEffect.ColorProperty),
        GradientBrush g => g.GradientStops.Any(s => BindingOperations.IsDataBound(s, GradientStop.ColorProperty)),
        _ => false,
    };

    static string Fmt(object v) => v switch
    {
        SolidColorBrush b => b.Color + (b.Opacity < 1 ? $"@{b.Opacity:0.##}" : ""),
        GradientBrush g => "gradient " + string.Join("→", g.GradientStops.Select(s => s.Color.ToString())),
        DropShadowEffect e => $"shadow {e.Color} blur {e.BlurRadius:0.#} op {e.Opacity:0.##}",
        _ => v.GetType().Name,
    };

    /// <summary>"Border in SkillDefinition" — the element type, its x:Name, and the data item (or named ancestor) it
    /// belongs to, so a template row can be found in the XAML.</summary>
    static string What(DependencyObject visualOwner, DependencyObject owner)
    {
        string t = owner is Inline ? owner.GetType().Name + " in TextBlock" : owner.GetType().Name;
        if (visualOwner is FrameworkElement fe)
        {
            if (owner == visualOwner && !string.IsNullOrEmpty(fe.Name)) t += "#" + fe.Name;
            if (fe.TemplatedParent is FrameworkElement tp) t += $" (template of {tp.GetType().Name}{(string.IsNullOrEmpty(tp.Name) ? "" : "#" + tp.Name)})";
            var dc = fe.DataContext;
            if (dc != null && !dc.GetType().Name.EndsWith("ViewModel")) t += $" [item {dc.GetType().Name}]";
            else
            {
                for (DependencyObject p = VisualTreeHelper.GetParent(fe); p != null; p = VisualTreeHelper.GetParent(p))
                    if (p is FrameworkElement pf && !string.IsNullOrEmpty(pf.Name)) { t += $" under #{pf.Name}"; break; }
            }
        }
        return t;
    }

    static string TextOf(DependencyObject el)
    {
        string s = el is TextBlock tb ? tb.Text : FirstText(el, 0);
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    static string FirstText(DependencyObject el, int depth)
    {
        if (depth > 6) return null;
        int n = VisualTreeHelper.GetChildrenCount(el);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(el, i);
            if (c is TextBlock t && !string.IsNullOrWhiteSpace(t.Text) && t.FontFamily?.Source?.Contains("MDL2") != true) return t.Text;
            var r = FirstText(c, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    static string DescribeHit(Visual root, Point p)
    {
        DependencyObject hit = null;
        VisualTreeHelper.HitTest(root, null, r => { hit = r.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(p));
        if (hit == null) return "nothing hit-testable (IsHitTestVisible=False?)";
        string text = TextOf(hit);
        return What(hit, hit) + (text.Length > 0 ? $"  “{text}”" : "");
    }
}
