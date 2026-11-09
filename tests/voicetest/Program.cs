using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views.Settings;

namespace VoiceTest;

/// <summary>
/// Settings › Voice, rendered for real: the panel with the app's own theme, once per engine (browser, Windows,
/// ElevenLabs, OpenAI) in Thai, to PNGs in &lt;exe&gt;\voice-out. Fails on a XAML or binding exception, a blank
/// picture, the wrong section showing, or a missing mascot. No network: portable.txt → &lt;exe&gt;\Data.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly StringBuilder Report = new();
    static readonly string Out = Path.Combine(AppContext.BaseDirectory, "voice-out");

    [STAThread]
    static int Main()
    {
        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
            finally
            {
                Console.WriteLine($"\n{Pass} passed, {Fail} failed — pictures: {Out}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        Environment.Exit(rc);
        return rc;
    }

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else Fail++;
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    }

    static async Task Idle(int ms = 0)
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task<int> RunAll()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))) { Log("FATAL portable.txt is missing"); return 2; }
        if (Directory.Exists(Out)) Directory.Delete(Out, true);
        Directory.CreateDirectory(Out);

        var bindingErrors = new List<string>();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new Collect(bindingErrors));

        var settings = new SettingsService();
        Check("portable data root", settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), settings.DataRoot);
        settings.UpdateSettings(s => { s.Language = "th"; s.Voice = new VoiceSettings(); });
        var loc = new LocalizationService();
        loc.SetLanguage("th");
        LocalizedResources.Instance.Initialize(loc);

        var vm = new VoiceSettingsViewModel(settings, loc.T);
        var panel = new VoiceSettingsPanel { DataContext = vm };
        var host = new Border { Background = (Brush)Application.Current.FindResource("Card.Background"), Padding = new Thickness(16), Child = panel, Width = 900 };
        var win = new Window
        {
            Content = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Width = 940, SizeToContent = SizeToContent.Height, Left = -3000, Top = 0, ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("Card.Background"),
        };
        win.Show();
        await Idle(200);

        foreach (var engine in VoiceSettings.Engines)
        {
            vm.Engine = engine;
            await Idle(250);
            int colours = Save(host, $"voice-{engine}.png");
            Check($"{engine}: rendered (not blank)", colours > 20, $"{colours} colours, {host.ActualHeight:0} px tall");
            Check($"{engine}: the mascot shows", vm.MascotImage != null && vm.MascotLine.Length > 5 && !vm.MascotLine.StartsWith("voice."), vm.MascotName);
            var visibleHeads = Texts(panel).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            bool elevenShown = visibleHeads.Contains("ElevenLabs API key");
            bool openAiShown = visibleHeads.Contains("OpenAI");
            Check($"{engine}: only its own section shows", elevenShown == (engine == VoiceSettings.ElevenLabs) && openAiShown == (engine == VoiceSettings.OpenAI),
                $"eleven={elevenShown} openai={openAiShown}");
            var raw = visibleHeads.Where(t => t.StartsWith("voice.")).ToList();
            Check($"{engine}: every text is translated", raw.Count == 0, string.Join(", ", raw.Take(5)));
            // A record's own ToString ("VoiceChoice { Value = … }") means a picker shows the object, not its name.
            var objects = visibleHeads.Where(t => t.Contains(" { ") && t.Contains(" = ")).ToList();
            Check($"{engine}: every picker shows a name, not an object", objects.Count == 0, string.Join(", ", objects.Take(3)));
        }

        vm.Engine = VoiceSettings.ElevenLabs;
        vm.SelectedModel = vm.Models.First(m => m.Model.Id == "eleven_multilingual_v2");
        await Idle(150);
        Check("multilingual v2: style slider on, Thai warning shown", vm.StyleEnabled && vm.ThaiWarning, "");
        vm.SelectedModel = vm.Models.First(m => m.Model.Id == "eleven_v3");
        vm.Stability = 0.62;
        await Idle(150);
        Check("eleven_v3: style off, stability snaps to Natural", !vm.StyleEnabled && !vm.ThaiWarning && vm.Stability == 0.5 && vm.StabilityText == "ธรรมชาติ", vm.StabilityText);
        Save(host, "voice-elevenlabs-v3.png");
        vm.Save();
        Check("the page saves into settings", settings.Settings.Voice.Engine == VoiceSettings.ElevenLabs && settings.Settings.Voice.ElevenLabsModel == "eleven_v3", "");
        Check("each girl has a row with her face", vm.GirlRows.Count >= 8 && vm.GirlRows.All(r => r.Face != null), $"{vm.GirlRows.Count} rows");
        Check("no binding errors", bindingErrors.Count == 0, string.Join(" | ", bindingErrors.Take(3)));

        win.Close();
        File.WriteAllText(Path.Combine(Out, "voice-report.md"), Report.ToString(), new UTF8Encoding(false));
        return Fail == 0 ? 0 : 1;
    }

    static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is TextBlock t) yield return t;
            foreach (var x in Texts(c)) yield return x;
        }
    }

    static int Save(FrameworkElement el, string name)
    {
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        if (w <= 0 || h <= 0) return 0;
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(Out, name))) enc.Save(fs);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        var colours = new HashSet<int>();
        for (int y = 0; y < h; y += 7) for (int x = 0; x < w; x += 7) { int i = (y * w + x) * 4; colours.Add(px[i] >> 4 | (px[i + 1] >> 4) << 4 | (px[i + 2] >> 4) << 8); }
        return colours.Count;
    }

    sealed class Collect(List<string> into) : System.Diagnostics.TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (into) into.Add(message); }
    }
}
