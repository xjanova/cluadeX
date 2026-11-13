using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using CluadeX.Services;

namespace CluadeX;

/// <summary>
/// น้อง CluadeX in the top-left corner (owner 2026-11-08, her sketch: big, under the title bar over the rail and part of
/// the chat list, the rail and the list carrying on below her; then "2:1 หน้าต่างอวาต้าก็ดี แต่ไร้ขอบ" — 1 wide, 2 tall,
/// no frame), and the owner's text-size zoom. Then "ขยายให้เห็นเธอทั้งคลิป มันเหมือนโดนครอปไปเกือบครึ่ง": the frame shows
/// nearly the whole clip (hat to skirt, not hat to chest), so she is wider here, on a raised plate that hides the
/// rail's edge behind her ("ทำพื้นที่กรอบนูนก็ได้").
/// <para><b>Her size.</b> Her height is twice her width. Beside the chat list she is <see cref="HeroBig"/> wide (narrower in
/// a short window, and never so wide that the list's title has no room); on the pages without the list she is the rail's
/// width, so she never covers a page. Every size is in the shell's own units — DIPs under the zoom — so DPI and the text size
/// need nothing extra.</para>
/// <para><b>What moves.</b> Nothing is ever under her: the rail's icons start below her (<c>BuddyRailSpacer</c>; they scroll
/// when a short window has no room), and the list's title, buttons, search box and projects sit right of her
/// (<c>SidebarTopZone</c>, <c>SidebarProjects</c>), padded down to her feet, so the chats begin below her. It is recomputed
/// whenever the list shows or hides, is resized, its top grows, or the window or the zoom changes.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Her width beside the chat list in a window of normal height (her height is twice it). 105 since
    /// 2026-11-13 (owner: "ย่อกล่องน้องอวาต้า ให้เล็กลงอีก 30% … มันยังเห็นห้องแชทน้อยไป"): it was 150, and the chats start
    /// under her feet, so a third less of her is 90 px — about three more chats in view.</summary>
    public const double HeroBig = 105;

    /// <summary>Her width in a window of middling height (560–720), and in a short one: the same 30 % down from 110 / 80
    /// (the short one stops at the rail's width, the least she ever is).</summary>
    public const double HeroMid = 77;
    public const double HeroSmall = 60;

    /// <summary>The rail's width (MainWindow.xaml, column 0 of MainContentGrid).</summary>
    public const double RailWidth = 60;

    /// <summary>The narrowest the list's title zone may get beside her: the title and its three buttons.</summary>
    public const double BesideMin = 150;

    /// <summary>Her width now (tests); her height is twice it.</summary>
    public double BuddyHeroSize { get; private set; }

    private const double TitleBarHeight = 42;

    private void InitBuddyHero()
    {
        ChatSidebar.IsVisibleChanged += (_, _) => UpdateBuddyHero();
        ChatSidebar.SizeChanged += (_, _) => UpdateBuddyHero();
        SidebarTopZone.SizeChanged += (_, _) => UpdateBuddyHero();
        MainContentGrid.SizeChanged += (_, _) => UpdateBuddyHero();
        Loaded += (_, _) => UpdateBuddyHero();
        TypographyService.Changed += ApplyUiScale;
        Closed += (_, _) => TypographyService.Changed -= ApplyUiScale;
        ApplyUiScale();
    }

    /// <summary>Her width for a list of width <paramref name="sidebarWidth"/> (0 = no list) and a content area
    /// <paramref name="contentHeight"/> tall, both in shell units; her height is twice it. Pure, for tests.</summary>
    public static double HeroSizeFor(double sidebarWidth, double contentHeight)
    {
        if (sidebarWidth <= 0) return RailWidth;
        double big = contentHeight >= 720 ? HeroBig : contentHeight >= 560 ? HeroMid : HeroSmall;
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
        double tall = s * 2;
        if (BuddyHero.Width != s) { BuddyHero.Width = s; BuddyHero.Height = tall; }
        // The rail: her height plus a breath, then the divider and the icons.
        double spacer = tall + 4;
        if (BuddyRailSpacer.Height != spacer) BuddyRailSpacer.Height = spacer;
        // The list: the top zone and the projects start right of her (their own padding adds the gap), and the projects
        // are padded down to her feet, so the chats begin under her.
        double over = Math.Max(0, s - RailWidth - 8);
        var m = new Thickness(over, 0, 0, 0);
        if (SidebarTopZone.Margin != m) SidebarTopZone.Margin = m;
        if (SidebarProjects.Margin != m) SidebarProjects.Margin = m;
        if (SidebarTopZone.MinHeight != 0) SidebarTopZone.MinHeight = 0;
        double minP = s > RailWidth ? Math.Max(0, Math.Ceiling(tall + 2 - SidebarTopZone.ActualHeight)) : 0;
        if (Math.Abs(SidebarProjects.MinHeight - minP) > 0.5) SidebarProjects.MinHeight = minP;
        PlaceBuddyBubble();
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
