using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace GpuShareTest;

/// <summary>
/// The popup and the hint exactly as MainWindow.xaml has them (cut out of the real file), parsed by WPF with the app's
/// theme dictionaries and rendered to PNG — catches a template that compiles but throws or renders blank at runtime.
/// </summary>
internal static class Render
{
    public static void Run(Action<string, string, bool, string> check, string outDir)
    {
        string xamlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\MainWindow.xaml"));
        if (!File.Exists(xamlPath)) { check("R0", "MainWindow.xaml found next to the harness", false, xamlPath); return; }
        string src = File.ReadAllText(xamlPath);

        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = new SettingsService();
        var loc = new LocalizationService();
        LocalizedResources.Instance.Initialize(loc);

        foreach (var theme in new[] { "Lavender", "Midnight", "NeonCyber", "Classic" })
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/CluadeX;component/Resources/Themes/{theme}.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CluadeX;component/Resources/Theme.xaml") });
            app.Resources["BoolVis"] = new CluadeX.Converters.BoolToVisibilityConverter();

            foreach (var lang in new[] { "th", "en" })
            {
                if (theme != "Lavender" && lang == "en") continue;
                loc.SetLanguage(lang);
                settings.UpdateSettings(s => s.GpuUsagePercent = 60);
                var vm = new GpuShareViewModel(settings, loc, null!, null,
                    () => new CluadeX.Models.GpuLiveStats { VramTotalMB = 8192, VramUsedMB = 7300 }, null);
                var ctx = new { GpuShare = vm };

                var popup = Cut(src, "<Popup Grid.Column=\"1\" IsOpen=\"{Binding GpuShare.IsOpen");
                var hint = Cut(src, "<Popup Grid.Column=\"1\" IsOpen=\"{Binding GpuShare.HintOpen");
                foreach (var (name, body, w, h) in new[] { ("slider", popup, 380, 330), ("hint", hint, 330, 110) })
                {
                    string id = $"R-{theme}-{lang}-{name}";
                    try
                    {
                        var el = (FrameworkElement)XamlReader.Parse(body);
                        el.DataContext = ctx;
                        var host = new System.Windows.Controls.Border
                        {
                            Background = (Brush)app.Resources["Bg.Window"] ?? Brushes.Black,
                            Child = el, Width = w, Height = h,
                        };
                        host.Measure(new Size(w, h));
                        host.Arrange(new Rect(0, 0, w, h));
                        host.UpdateLayout();
                        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(host);
                        string file = Path.Combine(outDir, $"gpushare-{theme}-{lang}-{name}.png");
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var fs = File.Create(file)) enc.Save(fs);
                        bool hasPercent = name != "slider" || FindText(el, "60");
                        check(id, $"{theme}/{lang} {name} parses, binds and renders", hasPercent && new FileInfo(file).Length > 2000, file);
                    }
                    catch (Exception ex) { check(id, $"{theme}/{lang} {name} parses, binds and renders", false, ex.GetBaseException().Message); }
                }
            }
        }
        loc.SetLanguage("en");
    }

    /// <summary>The Popup's single child, as standalone XAML with the namespaces MainWindow declares.</summary>
    private static string Cut(string src, string startMarker)
    {
        int a = src.IndexOf(startMarker, StringComparison.Ordinal);
        if (a < 0) throw new InvalidOperationException("marker not found: " + startMarker);
        int openEnd = src.IndexOf('>', a) + 1;
        int b = src.IndexOf("</Popup>", openEnd, StringComparison.Ordinal);
        string inner = src[openEnd..b].Trim();
        int firstSpace = inner.IndexOfAny(new[] { ' ', '>', '\r', '\n' });
        const string ns = " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                        + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                        + " xmlns:services=\"clr-namespace:CluadeX.Services;assembly=CluadeX\"";
        return inner.Insert(firstSpace, ns);
    }

    private static bool FindText(DependencyObject root, string text)
    {
        if (root is System.Windows.Controls.TextBlock tb && tb.Text == text) return true;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) if (FindText(VisualTreeHelper.GetChild(root, i), text)) return true;
        return false;
    }
}
