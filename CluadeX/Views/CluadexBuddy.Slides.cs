using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// How น้อง CluadeX shows, from her right-click menu (owner 2026-11-08: "ใช้การคลิ๊กขวา ปิดเปิดเสียง หรือ สั่งให้เป็นภาพนิ่ง
/// อย่างเดียว หรือให้สไลด์ บรรดา สาวๆ เรื่อยๆ เป็นภาพนิ่งได้ ในระยะ ครึ่งตัว"), kept in AppSettings.BuddyDisplay:
/// <list type="bullet">
/// <item><see cref="DisplayClips"/> — her clips (the default).</item>
/// <item><see cref="DisplayStills"/> — her still pictures and the drawn motion only, no clips.</item>
/// <item><see cref="DisplaySlides"/> — the girls (<see cref="GirlArt"/>: the main girls in both poses and the providers'
/// girls) one after another, every <see cref="SlideEvery"/>, cut at the waist; the order is shuffled and every picture
/// shows once before any shows again. The glow and the mood chips stay; her head's drawn motion does not reach the slide
/// (a tilted rectangle leaves her plate), which breathes and pops inside its rounded clip instead.</item>
/// </list>
/// </summary>
public partial class CluadexBuddy
{
    public const string DisplayClips = "clips", DisplayStills = "stills", DisplaySlides = "slides";

    public static readonly TimeSpan SlideEvery = TimeSpan.FromSeconds(9);

    /// <summary>Clips, stills or slides (MainWindow keeps it in step with the setting). Anything else counts as clips.</summary>
    public static string Display
    {
        get => _display;
        set
        {
            value = value is DisplayStills or DisplaySlides ? value : DisplayClips;
            if (_display == value) return;
            _display = value;
            DisplayChanged?.Invoke();
        }
    }
    private static string _display = DisplayClips;
    private static event Action? DisplayChanged;

    /// <summary>The girl picture showing in the slideshow ("" = none), for tests: "name" or "name#2".</summary>
    public string SlideShown { get; private set; } = "";

    private DispatcherTimer? _slideTimer;
    private readonly List<(string Name, int Pose)> _slideQueue = new();
    private Image? _slideFront;
    private int _slideTicket;

    private void HookDisplay()
    {
        DisplayChanged -= OnDisplayChanged;
        DisplayChanged += OnDisplayChanged;
        SyncSlides();
    }

    private void UnhookDisplay()
    {
        DisplayChanged -= OnDisplayChanged;
        StopSlides();
    }

    /// <summary>The mode changed: off with the clips (or on with them again) and the slideshow shown or hidden, at once.</summary>
    private void OnDisplayChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnDisplayChanged); return; }
        if (!IsLoaded) return;
        if (!ClipsOn) HideClips();
        SyncSlides();
        Stop();      // set her mood up again: with or without a clip
        Apply();
    }

    private void SyncSlides()
    {
        // a build with no girls keeps her own still picture rather than an empty plate
        bool on = Display == DisplaySlides && GirlArt.Names.Count + GirlArt.ProviderNames.Count > 0;
        Stills.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Slides.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { StopSlides(); return; }
        if (_slideTimer == null)
        {
            _slideTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SlideEvery };
            _slideTimer.Tick += (_, _) => { if (SlidesVisible) NextSlide(); };
            _slideTimer.Start();
        }
        if (SlideShown == "") NextSlide();
    }

    private void StopSlides()
    {
        _slideTimer?.Stop();
        _slideTimer = null;
        _slideTicket++;
        SlideA.Source = SlideB.Source = null;
        SlideA.Opacity = SlideB.Opacity = 0;
        _slideFront = null;
        SlideShown = "";
    }

    /// <summary>The slideshow is what shows in her plate now.</summary>
    private bool SlidesOn => Slides.Visibility == Visibility.Visible;

    /// <summary>The slide's clip: her 120×240 canvas with her plate's corners. The plate's corners are a fixed 14 px on
    /// screen (MainWindow BuddyPlate, 1 px outside her), so in her canvas — scaled by the Viewbox — the radius is 13 px over
    /// that scale: rounder when she is small (rail-sized), so no square corner of the picture pokes past the plate's.</summary>
    private void UpdateSlideClip()
    {
        double scale = ActualWidth > 0 ? Math.Min(ActualWidth / 120, ActualHeight / 240) : 0;
        double r = scale > 0 ? Math.Clamp(13 / scale, 4, 30) : 11;
        if (Math.Abs(SlideClip.RadiusX - r) < 0.01) return;
        SlideClip.RadiusX = SlideClip.RadiusY = r;
    }

    /// <summary>Only while someone could see her: CluadeX in front and not minimised.</summary>
    private bool SlidesVisible => IsVisible && _window is not { WindowState: WindowState.Minimized } && _window is not { IsActive: false };

    /// <summary>The next girl: cut out on a worker thread, then faded in over the one showing.</summary>
    public void NextSlide()
    {
        if (_slideQueue.Count == 0)
        {
            var all = GirlArt.Names.SelectMany(n => new[] { (n, 1), (n, 2) })
                .Concat(GirlArt.ProviderNames.Select(n => (n, 1))).ToList();
            if (all.Count == 0) return;
            lock (Dice) all = all.OrderBy(_ => Dice.Next()).ToList();
            // never the same picture twice running across a reshuffle
            if (all.Count > 1 && SlideShown == Key(all[0])) (all[0], all[^1]) = (all[^1], all[0]);
            _slideQueue.AddRange(all);
        }
        var next = _slideQueue[0];
        _slideQueue.RemoveAt(0);
        int ticket = ++_slideTicket;
        Task.Run(() => WaistUp(next.Name, next.Pose)).ContinueWith(t =>
        {
            if (ticket != _slideTicket || Display != DisplaySlides || t.Result == null) return;
            ShowSlide(t.Result, Key(next));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string Key((string Name, int Pose) s) => s.Pose == 2 ? s.Name + "#2" : s.Name;

    private void ShowSlide(ImageSource picture, string key)
    {
        var back = _slideFront;
        var front = back == SlideA ? SlideB : SlideA;
        front.BeginAnimation(OpacityProperty, null);
        front.Source = picture;
        Panel.SetZIndex(front, 2);
        if (back != null) Panel.SetZIndex(back, 1);
        _slideFront = front;
        SlideShown = key;
        if (!SystemParameters.ClientAreaAnimation)
        {
            front.Opacity = 1;
            if (back != null) { back.Opacity = 0; back.Source = null; }
            return;
        }
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            front.Opacity = 1;
            if (_slideFront == front && back != null) { back.BeginAnimation(OpacityProperty, null); back.Opacity = 0; back.Source = null; }
        };
        front.Opacity = 1;
        front.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The girl from the top of her head down to her waist, in her 1:2 frame, centred on her head: the top
    /// <see cref="WaistAt"/> of the cut-out, as wide as half that, about the middle of the opaque pixels near her head
    /// (the cut-outs are not centred on her). Frozen; null when the picture cannot be read.</summary>
    public static BitmapSource? WaistUp(string name, int pose)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = GirlArt.UriOf(name, pose);
            bmp.DecodePixelHeight = 760;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            int cropH = (int)(h * WaistAt), cropW = Math.Min(w, cropH / 2);
            double cx = HeadCentre(src);
            int x = (int)Math.Clamp(cx - cropW / 2.0, 0, w - cropW);
            var cut = new CroppedBitmap(src, new Int32Rect(x, 0, cropW, cropH));
            cut.Freeze();
            return cut;
        }
        catch { return null; }
    }

    /// <summary>Where her waist is, as a share of the cut-out's height (the girls stand head to toe in it; at 0.5 the
    /// picture ran down to her thighs — checked on all 35 pictures, 0.4 is head to waist for every one).</summary>
    public const double WaistAt = 0.4;

    /// <summary>The middle of her head: the average x of the opaque pixels from 3 % to 18 % down the picture.</summary>
    private static double HeadCentre(BitmapSource src)
    {
        int w = src.PixelWidth, y0 = (int)(src.PixelHeight * 0.03), rows = Math.Max(1, (int)(src.PixelHeight * 0.15));
        var px = new byte[w * 4 * rows];
        src.CopyPixels(new Int32Rect(0, y0, w, rows), px, w * 4, 0);
        double sum = 0, weight = 0;
        for (int i = 3; i < px.Length; i += 4)
        {
            int a = px[i];
            if (a < 128) continue;
            sum += (i / 4) % w * a;
            weight += a;
        }
        return weight > 0 ? sum / weight : w / 2.0;
    }
}
