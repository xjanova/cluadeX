using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool HeroOnly;

    /// <summary>
    /// น้อง CluadeX big in the top-left corner (owner 2026-11-08: her sketch — a square under the title bar over the rail and
    /// part of the chat list) and Settings › Text size and font. For each theme, two window sizes, two zooms and two pages
    /// (Chat with its list, Settings without): nothing a user reads or clicks is under her, she is big beside the list and
    /// rail-sized without it, and the whole shell is saved for the eye in renders\hero\. Then the bundled font: every Latin
    /// weight draws from its own Noto Sans CX file (no fake bold), Thai is left to Leelawadee UI (WPF mis-places Thai marks
    /// in Noto Sans Thai — TypographyService.BundledFamily), and Thai at 9–28 px is saved for the eye.
    /// </summary>
    static async Task HeroShots()
    {
        Log("\n## น้อง CluadeX in the corner, text size and font (rendered for the eye)");
        // her clips would make every render different: the stills until ClipChecks
        CluadexBuddy.ClipsAllowed = false;
        string dir = Path.Combine(OutRoot, "renders", "hero");
        var themes = ThemeServiceOrNull();
        var typo = Sp.GetRequiredService<TypographyService>();
        typo.ApplySaved();
        var hero = MainWin.FindName("BuddyHero") as FrameworkElement;
        Check("hero: น้อง CluadeX is on the window", hero != null);
        if (hero == null) return;
        Check("hero: she is still a button to Home", hero is Button b0 && ReferenceEquals(b0.Command, MainVm.ChatVM.GoHomeCommand));
        Check("hero: her face is the big picture", FindAll<CluadexBuddy>(MainWin).Count == 1);

        var sets = new[] { ("Classic", "light"), ("Midnight", "dark"), ("Lavender", "lavender"), ("NeonCyber", "neon") };
        var sizes = new[] { (980, 640), (1600, 1000) };
        var zooms = new[] { 1.0, 1.4 };
        foreach (var (id, label) in sets)
        {
            if (themes != null) { Invoke(themes, "Apply", id, false); await Idle(200); }
            foreach (var (w, h) in sizes)
            foreach (double z in zooms)
            foreach (string page in new[] { "Chat", "Settings" })
            {
                MainWin.Width = w; MainWin.Height = h;
                typo.SetScale(z, persist: false);
                MainVm.NavigateToCommand.Execute(page);
                if (page == "Settings") MainVm.SettingsVM.SelectedCategory = "general";
                await Idle(350);
                string tag = $"{label} {w}×{h} {z * 100:0}% {page}";
                var root = (UIElement)RootVisual(MainWin);
                Rect her = Bounds(hero, root);
                double scale = z;
                bool chat = page == "Chat";
                double expected = CluadeX.MainWindow.HeroSizeFor(chat ? ((FrameworkElement)MainWin.FindName("ChatSidebar")).ActualWidth : 0,
                                                         ((FrameworkElement)MainWin.FindName("MainContentGrid")).ActualHeight) * scale;
                Check($"hero [{tag}]: her size is {(chat ? "big beside the list" : "the rail's")}, twice as tall as wide",
                      Math.Abs(her.Width - expected) < 1.5 && Math.Abs(her.Height - 2 * her.Width) < 1.5
                      && (chat ? her.Width >= 80 * scale - 1 : Math.Abs(her.Width - 60 * scale) < 1.5),
                      $"{her.Width:0.#}×{her.Height:0.#} px, expected {expected:0.#} wide");
                Check($"hero [{tag}]: she sits right under the title bar at the left edge",
                      Math.Abs(her.X) < 1.5 && Math.Abs(her.Y - 42 * scale) < 2.5, $"{her.X:0.#},{her.Y:0.#}");
                var under = Under(hero, her, root);
                Check($"hero [{tag}]: nothing to read or click is under her", under.Count == 0, string.Join("; ", under.Take(6)));
                if (z == 1.0 || w == 1600)
                {
                    FreezeVolatile();
                    var full = Snapshot(root, w, h);
                    SavePng(full, Path.Combine(dir, $"{label}-{w}x{h}-{z * 100:0}-{page.ToLowerInvariant()}.png"));
                    if (chat && z == 1.0 && w == 1600)
                        SavePng(new TransformedBitmap(new CroppedBitmap(full, new Int32Rect(0, 0, 440, 420)), new ScaleTransform(2, 2)),
                                Path.Combine(dir, $"{label}-corner-2x.png"));
                }
            }
        }
        typo.SetScale(1.0, persist: false);
        MainWin.Width = W; MainWin.Height = H;
        if (themes != null) { Invoke(themes, "Apply", "Midnight", false); await Idle(200); }

        // ── the chat list narrowed to its least: she shrinks so the title keeps its room ──
        var side = (FrameworkElement)MainWin.FindName("ChatSidebar");
        MainVm.NavigateToCommand.Execute("Chat");
        double oldW = side.Width;
        side.Width = 180;
        await Idle(250);
        {
            var root = (UIElement)RootVisual(MainWin);
            Rect her = Bounds(hero, root);
            Check("hero [narrow list]: she shrinks with a 180 px list", her.Width < CluadeX.MainWindow.HeroBig && her.Width >= 60, $"{her.Width:0.#}");
            var under = Under(hero, her, root);
            Check("hero [narrow list]: nothing under her", under.Count == 0, string.Join("; ", under.Take(6)));
        }
        side.Width = oldW;
        await Idle(150);

        await ClipChecks();
        await BubbleChecks();

        // ── the bundled font ──
        typo.SetFont(TypographyService.BundledFont, persist: false);
        await Idle(150);
        Check("font: the bundled font goes on as Font.Ui", ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily));
        if (themes != null) { Invoke(themes, "Apply", "Classic", false); await Idle(150); }
        Check("font: it stays when the theme changes", ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily)
              && MainWin.TryFindResource("Font.Ui") == TypographyService.BundledFamily);
        foreach (var (weight, file) in new[] { (FontWeights.Normal, "NotoSansCX-Regular.ttf"), (FontWeights.Medium, "NotoSansCX-Medium.ttf"),
                                               (FontWeights.SemiBold, "NotoSansCX-SemiBold.ttf"), (FontWeights.Bold, "NotoSansCX-Bold.ttf") })
        {
            var tf = new Typeface(TypographyService.BundledFamily, FontStyles.Normal, weight, FontStretches.Normal);
            bool got = tf.TryGetGlyphTypeface(out var g);
            string uri = got ? g.FontUri.ToString() : "none";
            Check($"font: {weight} draws from its own file ({file}), not a fake bold", got && uri.EndsWith(file, StringComparison.OrdinalIgnoreCase)
                  && !tf.IsBoldSimulated, $"{uri} simulated={tf.IsBoldSimulated}");
            if (got)
                Check($"font: {weight} has Latin but no Thai, so Thai falls through to Leelawadee UI",
                      "AZaz09éß".All(c => g.CharacterToGlyphMap.ContainsKey(c)) && !Enumerable.Range(0x0E01, 0x5B).Any(c => g.CharacterToGlyphMap.ContainsKey(c)));
        }
        {
            var lee = new Typeface(new FontFamily("Leelawadee UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Check("font: Leelawadee UI is on this PC and has the Thai block", lee.TryGetGlyphTypeface(out var lg)
                  && Enumerable.Range(0x0E01, 0x2E).All(c => lg.CharacterToGlyphMap.ContainsKey(c)));
        }
        // Thai stacking at every size, for the eye (and nothing falls back to another font)
        {
            const string sample = "น้ำใจ ปั๊ม ญี่ปุ่น กี่ปี ฎีกา ฤๅษี ปู่ย่า ตั๊กแตน · Aa 012";
            var panel = new StackPanel { Background = (Brush)MainWin.FindResource("Bg.Window"), Margin = new Thickness(0) };
            foreach (double size in new[] { 9.0, 11, 13, 16, 20, 28 })
            foreach (var wt in new[] { FontWeights.Normal, FontWeights.Bold })
                panel.Children.Add(new TextBlock
                {
                    Text = $"{size:0}px {(wt == FontWeights.Bold ? "B" : " ")}  {sample}", FontFamily = TypographyService.BundledFamily,
                    FontSize = size, FontWeight = wt, Margin = new Thickness(10, 2, 10, 2), Foreground = (Brush)MainWin.FindResource("Text.Primary"),
                });
            panel.Measure(new Size(900, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 900, panel.DesiredSize.Height));
            panel.UpdateLayout();
            SavePng(Snapshot(panel, 900, (int)Math.Ceiling(panel.DesiredSize.Height)), Path.Combine(dir, "thai-sizes-bundled.png"));
        }

        // Settings › Text size and font, light and dark, at 100 % (the card scrolled into view)
        MainVm.NavigateToCommand.Execute("Settings");
        MainVm.SettingsVM.SelectedCategory = "general";
        foreach (var (id, label) in new[] { ("Classic", "light"), ("Midnight", "dark") })
        {
            if (themes != null) { Invoke(themes, "Apply", id, false); await Idle(250); }
            var card = FindAll<Border>(MainWin).FirstOrDefault(x => x.Name == "TypographyCard");
            Check($"settings [{label}]: the Text size and font card is on the General page", card is { IsVisible: true });
            card?.BringIntoView();
            await Idle(300);
            FreezeVolatile();
            SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"settings-typography-{label}.png"));
        }
        typo.SetFont(TypographyService.ThemeFont, persist: false);
        if (themes != null) { Invoke(themes, "Apply", "Midnight", false); await Idle(150); }
        Check("font: back to the theme's own takes the override out", !ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily)
              && ReferenceEquals(MainWin.TryFindResource("Font.Ui"), TypographyService.ThemeFamily()));
        Log($"  shots: {dir}");
    }

    /// <summary>Her clips (Assets/Buddy/Clips): every one the scenes name is shipped and small, and each mood picks a clip of
    /// its own scene — a greeting first, a reaction then idle, typing turns idle into listening.</summary>
    static async Task ClipChecks()
    {
        var names = CluadexBuddy.AllClipNames.ToList();
        var missing = names.Where(n => !File.Exists(Path.Combine(CluadexBuddy.ClipDir, n + ".mp4"))).ToList();
        Check($"clips: all {names.Count} clips the scenes name are shipped", missing.Count == 0, string.Join(", ", missing));
        Check("clips: there are 130 or more", names.Count >= 130, $"{names.Count}");
        var files = Directory.Exists(CluadexBuddy.ClipDir) ? new DirectoryInfo(CluadexBuddy.ClipDir).GetFiles("*.mp4") : Array.Empty<FileInfo>();
        var big = files.Where(f => f.Length > 600_000).Select(f => $"{f.Name} {f.Length / 1024} KB").ToList();
        Check("clips: none over 600 KB", big.Count == 0, string.Join(", ", big));
        long total = files.Sum(f => f.Length);
        Check("clips: all of them together under 30 MB", total < 30_000_000, $"{total / 1024} KB");
        var extra = files.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Except(names).ToList();
        Check("clips: no clip on disk that no scene plays", extra.Count == 0, string.Join(", ", extra));

        var buddy = FindAll<CluadexBuddy>(MainWin).FirstOrDefault();
        if (buddy == null || !SystemParameters.ClientAreaAnimation)
        {
            Log("  (clip scenes skipped: " + (buddy == null ? "no buddy" : "Windows animations are off — she keeps the stills") + ")");
            return;
        }
        CluadexBuddy.ClipsAllowed = true;
        // sound off first: she never picks a spoken line, so every mood shows its own gesture
        bool soundsWere = CluadexBuddy.SoundsOn;
        CluadexBuddy.SoundsOn = false;
        MainVm.NavigateToCommand.Execute("Chat");
        async Task<string> Mood(string mood)
        {
            buddy.SetCurrentValue(CluadexBuddy.MoodProperty, mood);
            await Idle(120);
            return buddy.ClipShown;
        }
        await Mood(BuddyMood.Working);
        Check("clips [working]: a working clip", CluadexBuddy.Scenes[BuddyMood.Working].Loop.Contains(buddy.ClipShown), buddy.ClipShown);
        await Mood(BuddyMood.Happy);
        Check("clips [happy]: one of her happy reactions", CluadexBuddy.Scenes[BuddyMood.Happy].Intro.Contains(buddy.ClipShown), buddy.ClipShown);
        await Mood(BuddyMood.Sad);
        Check("clips [sad]: one of her sorry reactions", CluadexBuddy.Scenes[BuddyMood.Sad].Intro.Contains(buddy.ClipShown), buddy.ClipShown);
        await Mood(BuddyMood.Waiting);
        Check("clips [waiting]: the waiting scene", buddy.SceneShown == BuddyMood.Waiting, buddy.SceneShown);
        await Mood(BuddyMood.Sleep);
        Check("clips [sleep]: she yawns first", CluadexBuddy.Scenes[BuddyMood.Sleep].Intro.Contains(buddy.ClipShown), buddy.ClipShown);
        await Mood(BuddyMood.Idle);
        Check("clips [awake]: she wakes up first", buddy.ClipShown == CluadexBuddy.WakeClip, buddy.ClipShown);
        await Mood(BuddyMood.Working);
        await Mood(BuddyMood.Idle);
        Check("clips [idle]: the idle scene", buddy.SceneShown == BuddyMood.Idle, buddy.SceneShown);
        buddy.SetListening(true);
        await Idle(80);
        Check("clips [typing]: she listens while the owner types", buddy.SceneShown == CluadexBuddy.ListeningScene, buddy.SceneShown);
        buddy.SetListening(false);
        await Idle(80);
        Check("clips [stopped typing]: back to idle", buddy.SceneShown == BuddyMood.Idle, buddy.SceneShown);
        Check("clips [suggest]: a tip gesture plays while she is idle", buddy.Suggest() && CluadexBuddy.SuggestClips.Contains(buddy.ClipShown), buddy.ClipShown);
        var players = new[] { (System.Windows.Controls.MediaElement)buddy.FindName("ClipA"), (System.Windows.Controls.MediaElement)buddy.FindName("ClipB") };
        Check("sound off: both players muted", players.All(p => p.IsMuted));

        // ---- her voice: spoken lines, at most one every SpeechGap
        CluadexBuddy.SoundsOn = true;
        void Field(string name, object value) =>
            typeof(CluadexBuddy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(buddy, value);
        void MayTalk() => Field("_lastSpokeAt", DateTime.MinValue);
        MayTalk();
        await Mood(BuddyMood.Working);
        Field("_workingSince", DateTime.UtcNow - TimeSpan.FromSeconds(20));
        await Mood(BuddyMood.Happy);
        Check("voice [done]: a long turn ends with a \"done!\" line", CluadexBuddy.DoneLines.Contains(buddy.ClipShown), buddy.ClipShown);
        Check("voice [done]: a spoken line can be heard", players.Any(p => p.Source != null && !p.IsMuted));
        await Mood(BuddyMood.Waiting);
        Check("voice [gap]: right after a line she only gestures", !CluadexBuddy.Spoken.Contains(buddy.ClipShown) && buddy.SceneShown == BuddyMood.Waiting, buddy.ClipShown);
        await Mood(BuddyMood.Working);
        await Mood(BuddyMood.Happy);
        Check("voice [short turn]: a quick answer gets a smile, not a line", CluadexBuddy.Scenes[BuddyMood.Happy].Intro.Contains(buddy.ClipShown), buddy.ClipShown);
        MayTalk();
        await Mood(BuddyMood.Waiting);
        Check("voice [decide]: waiting for the owner, she calls them", CluadexBuddy.DecideLines.Contains(buddy.ClipShown), buddy.ClipShown);
        Field("_lastSadAt", DateTime.MinValue);
        foreach (var (scene, n) in new[] { (BuddyMood.Sad, 1), (CluadexBuddy.AnnoyedScene, 2), (CluadexBuddy.AngryScene, 3) })
        {
            await Mood(BuddyMood.Idle);
            MayTalk();
            await Mood(BuddyMood.Sad);
            Check($"voice [error {n}]: the {scene} scene and its line",
                  buddy.SceneShown == scene && CluadexBuddy.ErrorLines[scene].Contains(buddy.ClipShown), $"{buddy.SceneShown} {buddy.ClipShown}");
        }

        // ---- the owner away: she calls out (waking up first when asleep), not again soon after, and the count starts over when they are back
        await Mood(BuddyMood.Idle);
        await Mood(BuddyMood.Sleep);
        await Idle(150);
        buddy.AwayTick(TimeSpan.FromSeconds(30));
        buddy.AwayTick(TimeSpan.FromMinutes(5));
        await Idle(120);
        Check("away: asleep, she wakes up to call out", buddy.AwayCalls == 1 && buddy.ClipShown == CluadexBuddy.WakeClip, $"{buddy.AwayCalls} {buddy.ClipShown}");
        buddy.AwayTick(TimeSpan.FromMinutes(6));
        Check("away: not again a minute later", buddy.AwayCalls == 1, $"{buddy.AwayCalls}");
        buddy.AwayTick(TimeSpan.FromSeconds(20));
        Check("away: the owner is back, the count starts over", buddy.AwayCalls == 0, $"{buddy.AwayCalls}");
        await Mood(BuddyMood.Idle);
        await Idle(150);
        CluadexBuddy.SoundsOn = false;
        buddy.AwayTick(TimeSpan.FromMinutes(5));
        Check("away: with sound off she stays quiet", buddy.AwayCalls == 0, $"{buddy.AwayCalls}");
        CluadexBuddy.SoundsOn = true;
        buddy.AwayTick(TimeSpan.FromSeconds(20));
        buddy.AwayTick(TimeSpan.FromMinutes(5));
        await Idle(120);
        Check("away: idle, she calls out", buddy.AwayCalls == 1 && CluadexBuddy.AwayLines.Contains(buddy.ClipShown), $"{buddy.AwayCalls} {buddy.ClipShown}");
        buddy.AwayTick(TimeSpan.FromSeconds(20));
        await Idle(120);
        Check("away: nobody answered, so she sulks when the owner is back",
              CluadexBuddy.SulkLines.Concat(CluadexBuddy.SulkClips).Contains(buddy.ClipShown), buddy.ClipShown);

        // ---- the time of day
        Check("time: good morning at 8", CluadexBuddy.GreetingsAt(TimeSpan.FromHours(8)).Contains("hi-morning"));
        Check("time: good evening at 19", CluadexBuddy.GreetingsAt(TimeSpan.FromHours(19)).Contains("hi-evening"));
        Check("time: \"it's late\" at 23 and at 2", CluadexBuddy.GreetingsAt(TimeSpan.FromHours(23)).Contains("night-late")
                                                   && CluadexBuddy.GreetingsAt(TimeSpan.FromHours(2)).Contains("night-late"));
        Check("time: a snack at lunch, not at 3 pm", CluadexBuddy.ExtrasAt(BuddyMood.Idle, new TimeSpan(12, 15, 0)).Contains("snack")
                                                    && !CluadexBuddy.ExtrasAt(BuddyMood.Idle, TimeSpan.FromHours(15)).Contains("snack"));
        Check("time: sleepy late at night", CluadexBuddy.ExtrasAt(BuddyMood.Idle, TimeSpan.FromHours(23.5)).Contains("sleepy-blink"));

        // ---- the setting turns her sound off and on, live
        var svm = MainVm.SettingsVM;
        var prop = svm.GetType().GetProperty("BuddySounds")!;
        bool was = (bool)prop.GetValue(svm)!;
        prop.SetValue(svm, false);
        Check("setting: sound off reaches her", !CluadexBuddy.SoundsOn);
        prop.SetValue(svm, true);
        Check("setting: sound on reaches her", CluadexBuddy.SoundsOn);
        prop.SetValue(svm, was);
        CluadexBuddy.SoundsOn = soundsWere;

        // a clip that cannot play leaves her on the stills
        CluadexBuddy.ClipsAllowed = false;
        await Mood(BuddyMood.Working);
        Check("clips off: no clip, the stills", buddy.ClipShown == "", buddy.ClipShown);
        await Mood(BuddyMood.Idle);
    }

    /// <summary>Her speech bubble: it says the text, sits right of her face inside the window, a click closes it, and the
    /// setting turns it off.</summary>
    static async Task BubbleChecks()
    {
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(150);
        Invoke(MainWin, "ShowBuddyBubble", "buddy.tip.palette");
        await Idle(300);
        string said = ((CluadeX.MainWindow)MainWin).BuddyBubbleSaying;
        Check("bubble: she says the tip", said.Length > 0 && said == CluadeX.Services.LocalizedResources.Instance["buddy.tip.palette"], said);
        var bubble = (FrameworkElement)MainWin.FindName("BuddyBubble");
        var root = (UIElement)RootVisual(MainWin);
        Rect b = Bounds(bubble, root), her = Bounds((FrameworkElement)MainWin.FindName("BuddyHero"), root);
        Check("bubble: right of her, beside her face, inside the window",
              b.Left >= her.Left + her.Width * 0.6 && b.Top >= 0 && b.Bottom <= her.Top + her.Height * 0.6 && b.Right <= ((FrameworkElement)root).ActualWidth,
              $"bubble {b.X:0},{b.Y:0} {b.Width:0}×{b.Height:0}; her {her.X:0},{her.Y:0} {her.Width:0}×{her.Height:0}");
        FreezeVolatile();
        SavePng(Snapshot(root, (int)MainWin.Width, (int)MainWin.Height), Path.Combine(OutRoot, "renders", "hero", "bubble.png"));
        Invoke(MainWin, "HideBuddyBubble");
        await Idle(400);
        Check("bubble: it goes away", ((CluadeX.MainWindow)MainWin).BuddyBubbleSaying == "");

        var svm = MainVm.SettingsVM;
        bool was = (bool)svm.GetType().GetProperty("BuddyBubbles")!.GetValue(svm)!;
        svm.GetType().GetProperty("BuddyBubbles")!.SetValue(svm, false);
        bool speaks = (bool)Invoke(MainWin, "CanSpeak", false);
        Check("bubble: the setting off keeps her quiet", !speaks);
        svm.GetType().GetProperty("BuddyBubbles")!.SetValue(svm, was);
    }

    static Rect Bounds(FrameworkElement e, UIElement root) =>
        e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    /// <summary>What part of <paramref name="e"/> is on screen: its bounds cut by every scrolling or clipping ancestor.</summary>
    static Rect VisiblePart(FrameworkElement e, UIElement root)
    {
        Rect r = Bounds(e, root);
        for (DependencyObject p = VisualTreeHelper.GetParent(e); p != null && p != root; p = VisualTreeHelper.GetParent(p))
        {
            if (p is UIElement u && !u.IsVisible) return Rect.Empty;
            if (p is ScrollContentPresenter or ScrollViewer || p is UIElement { ClipToBounds: true })
            {
                var fe = (FrameworkElement)p;
                r.Intersect(Bounds(fe, root));
                if (r.IsEmpty) return r;
            }
        }
        return r;
    }

    /// <summary>Text, inputs and buttons a user could see that overlap her square (her own parts left out).</summary>
    static List<string> Under(FrameworkElement hero, Rect her, UIElement root)
    {
        var bad = new List<string>();
        her.Inflate(-2, -2);
        foreach (var el in VisualChildren(root).OfType<FrameworkElement>())
        {
            if (el is not (TextBlock or TextBox or ButtonBase)) continue;
            if (!el.IsVisible || el.ActualWidth < 2 || el.ActualHeight < 2 || el.Opacity < 0.05) continue;
            if (ReferenceEquals(el, hero) || Ancestors(el).Contains(hero)) continue;
            if (el is TextBlock tb && string.IsNullOrWhiteSpace(tb.Text)) continue;
            // a TextBlock inside a button is counted as the button
            if (el is TextBlock && Ancestors(el).OfType<ButtonBase>().Any()) continue;
            Rect r;
            try { r = VisiblePart(el, root); } catch (InvalidOperationException) { continue; }
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
            if (!r.IntersectsWith(her)) continue;
            var cut = Rect.Intersect(r, her);
            if (cut.Width < 2 || cut.Height < 2) continue;
            string what = el switch { TextBlock t => $"text '{Clip(t.Text)}'", TextBox => "text box", ButtonBase bb => $"button {bb.ToolTip as string ?? bb.Name}", _ => el.GetType().Name };
            bad.Add($"{what} @{r.X:0},{r.Y:0} {r.Width:0}×{r.Height:0}");
        }
        return bad;
    }

    static string Clip(string s) => s.Length > 24 ? s[..24] + "…" : s;
}
