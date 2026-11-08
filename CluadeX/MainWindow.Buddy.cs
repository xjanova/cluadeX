using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using CluadeX.Services;

namespace CluadeX;

/// <summary>
/// น้อง CluadeX's square in the top-left corner (owner 2026-11-08, her sketch: a big face under the title bar over the rail
/// and part of the chat list, the rail and the list carrying on below her), and the owner's text-size zoom.
/// <para><b>Her size.</b> Beside the chat list she is <see cref="HeroBig"/> (smaller in a short window, and never so wide
/// that the list's title has no room); on the pages without the list she shrinks to the rail's width, so she never covers a
/// page. Every size is in the shell's own units — DIPs under the zoom — so DPI and the text size need nothing extra.</para>
/// <para><b>What moves.</b> Nothing is ever under her: the rail's icons start below her (<c>BuddyRailSpacer</c>), and the
/// list's title, buttons and search box sit right of her in a zone at least as tall as she is (<c>SidebarTopZone</c>),
/// so the projects and the chats begin below her. It is recomputed whenever the list shows or hides, is resized, or the
/// window or the zoom changes.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Her side beside the chat list in a window of normal height.</summary>
    public const double HeroBig = 124;

    /// <summary>The rail's width (MainWindow.xaml, column 0 of MainContentGrid).</summary>
    public const double RailWidth = 60;

    /// <summary>The narrowest the list's title zone may get beside her: the title and its three buttons.</summary>
    public const double BesideMin = 150;

    /// <summary>Her side now (tests).</summary>
    public double BuddyHeroSize { get; private set; }

    private const double TitleBarHeight = 42;

    private void InitBuddyHero()
    {
        ChatSidebar.IsVisibleChanged += (_, _) => UpdateBuddyHero();
        ChatSidebar.SizeChanged += (_, _) => UpdateBuddyHero();
        MainContentGrid.SizeChanged += (_, _) => UpdateBuddyHero();
        Loaded += (_, _) => UpdateBuddyHero();
        TypographyService.Changed += ApplyUiScale;
        Closed += (_, _) => TypographyService.Changed -= ApplyUiScale;
        ApplyUiScale();
    }

    /// <summary>Her side for a list of width <paramref name="sidebarWidth"/> (0 = no list) and a content area
    /// <paramref name="contentHeight"/> tall, both in shell units. Pure, for tests.</summary>
    public static double HeroSizeFor(double sidebarWidth, double contentHeight)
    {
        if (sidebarWidth <= 0) return RailWidth;
        double big = contentHeight >= 520 ? HeroBig : contentHeight >= 400 ? 104 : 84;
        double room = RailWidth + Math.Max(0, sidebarWidth - BesideMin);
        return Math.Max(RailWidth, Math.Min(big, room));
    }

    private void UpdateBuddyHero()
    {
        if (BuddyHero == null) return;
        double side = ChatSidebar.IsVisible ? (ChatSidebar.ActualWidth > 0 ? ChatSidebar.ActualWidth : ChatSidebar.Width) : 0;
        if (double.IsNaN(side)) side = 0;
        double s = HeroSizeFor(side, MainContentGrid.ActualHeight > 0 ? MainContentGrid.ActualHeight : 700);
        BuddyHeroSize = s;
        if (BuddyHero.Width != s) { BuddyHero.Width = s; BuddyHero.Height = s; }
        // The rail: her square plus a breath, then the divider and the icons.
        double spacer = s + 4;
        if (BuddyRailSpacer.Height != spacer) BuddyRailSpacer.Height = spacer;
        // The list: everything in the top zone starts right of her (its own padding adds the gap) and the zone reaches
        // below her, so the projects and the chats begin under her bottom edge.
        double over = Math.Max(0, s - RailWidth - 8);
        var m = new Thickness(over, 0, 0, 0);
        if (SidebarTopZone.Margin != m) SidebarTopZone.Margin = m;
        double minH = s > RailWidth ? s + 2 : 0;
        if (SidebarTopZone.MinHeight != minH) SidebarTopZone.MinHeight = minH;
    }

    /// <summary>The text size: a zoom of the whole shell. The title bar's drag strip follows it.</summary>
    private void ApplyUiScale()
    {
        double z = TypographyService.Current?.Scale ?? 1.0;
        ShellGrid.LayoutTransform = Math.Abs(z - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(z, z);
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = TitleBarHeight * z;
        UpdateBuddyHero();
    }
}
