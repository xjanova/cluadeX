using System.IO;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.Services.UiStudio.Sketch;
using CluadeX.Views.UiStudio;

namespace StudioTest;

/// <summary>
/// WPF views simulated from their XAML, through the REAL CluadeX.dll: a solution whose app project sits one folder
/// down, App.xaml merging a theme and styles (and a theme it does NOT merge), a main window showing a view through a
/// DataTemplate, a dialog, a reusable UserControl, .resx strings, the app's own translations, a half-written view and
/// a DTD that must never be expanded. Temp folders only.
/// </summary>
internal static partial class Program
{
    const string WpfNs = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    /// <summary>The 1-based line of the first occurrence of <paramref name="needle"/>.</summary>
    static int LineOf(string text, string needle)
    {
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) throw new Exception("not in the fixture: " + needle);
        return text[..at].Count(c => c == '\n') + 1;
    }

    static string Nodes(ScreenSketch sk) => string.Join(" | ", sk.Root.Walk().Where(n => n.Text != null || n.Icon != null).Select(n => $"{n.Kind}:{n.Text ?? n.Icon}"));

    static void WpfViews(string temp)
    {
        Console.WriteLine("Page gallery: WPF views, simulated from XAML");
        string root = Path.Combine(temp, "wpf-demo");
        string app = Path.Combine(root, "App");
        W(root, "WpfDemo.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"WpfDemo\", \"App\\WpfDemo.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\nEndProject\n");
        W(root, "docs/index.html", "<title>Project site</title>");   // a repository's docs page is not the app
        W(app, "WpfDemo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        W(app, "App.xaml", $$$"""
            <Application x:Class="WpfDemo.App" {{{WpfNs}}} StartupUri="MainWindow.xaml">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="Themes/Dark.xaml"/>
                            <ResourceDictionary Source="/WpfDemo;component/Styles.xaml"/>
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>
            """);
        W(app, "Themes/Dark.xaml", $$$"""
            <ResourceDictionary {{{WpfNs}}}>
                <Color x:Key="AccentColor">#FF7A00</Color>
                <SolidColorBrush x:Key="Accent.1" Color="{StaticResource AccentColor}"/>
                <SolidColorBrush x:Key="Page.Background" Color="#101418"/>
                <SolidColorBrush x:Key="Text.Primary" Color="#F0F0F0"/>
                <SolidColorBrush x:Key="Surface.Card" Color="#1E2329"/>
            </ResourceDictionary>
            """);
        W(app, "Themes/Light.xaml", $$$"""
            <ResourceDictionary {{{WpfNs}}}>
                <SolidColorBrush x:Key="Accent.1" Color="#0000FF"/>
                <SolidColorBrush x:Key="Page.Background" Color="#FFFFFF"/>
            </ResourceDictionary>
            """);
        W(app, "Styles.xaml", $$$"""
            <ResourceDictionary {{{WpfNs}}}>
                <Style x:Key="BaseButton" TargetType="Button">
                    <Setter Property="Padding" Value="12,6"/>
                </Style>
                <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource BaseButton}">
                    <Setter Property="Background" Value="{DynamicResource Accent.1}"/>
                </Style>
                <Style x:Key="SectionTitle" TargetType="TextBlock">
                    <Setter Property="FontSize" Value="18"/>
                    <Setter Property="FontWeight" Value="Bold"/>
                </Style>
                <DataTemplate x:Key="PersonRow">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="&#xE77B;" FontFamily="Segoe MDL2 Assets"/>
                        <TextBlock Text="{Binding Name}"/>
                    </StackPanel>
                </DataTemplate>
            </ResourceDictionary>
            """);
        W(app, "Properties/Strings.resx", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <data name=\"Greeting\" xml:space=\"preserve\"><value>Hello</value></data>\n</root>\n");
        W(app, "Properties/Strings.th.resx", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <data name=\"Greeting\" xml:space=\"preserve\"><value>สวัสดี</value></data>\n</root>\n");
        string mainXaml = $$$"""
            <Window x:Class="WpfDemo.MainWindow" {{{WpfNs}}}
                    xmlns:services="clr-namespace:WpfDemo.Services"
                    xmlns:vm="clr-namespace:WpfDemo.ViewModels"
                    xmlns:views="clr-namespace:WpfDemo.Views"
                    Title="Demo App" Width="1100" Height="700"
                    Background="{DynamicResource Page.Background}">
                <Window.Resources>
                    <DataTemplate DataType="{x:Type vm:HomeViewModel}">
                        <views:HomeView/>
                    </DataTemplate>
                </Window.Resources>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <Border Grid.ColumnSpan="2" Background="{DynamicResource Surface.Card}" Padding="12">
                        <TextBlock Text="{services:Loc nav.settings}" Style="{StaticResource SectionTitle}"/>
                    </Border>
                    <ListBox x:Name="People" Grid.Row="1" Width="220" ItemsSource="{Binding People}" ItemTemplate="{StaticResource PersonRow}"/>
                    <ContentControl Grid.Row="1" Grid.Column="1" Content="{Binding CurrentView}"/>
                    <Border Grid.Row="1" Grid.ColumnSpan="2" Background="#CC000000" Visibility="{Binding ShowGallery, Converter={StaticResource BoolVis}}">
                        <TextBlock Text="A gallery over everything"/>
                    </Border>
                </Grid>
            </Window>
            """;
        W(app, "MainWindow.xaml", mainXaml);
        string homeXaml = $$$"""
            <UserControl x:Class="WpfDemo.Views.HomeView" {{{WpfNs}}}
                         xmlns:p="clr-namespace:WpfDemo.Properties"
                         xmlns:local="clr-namespace:WpfDemo.Views">
                <ScrollViewer>
                    <StackPanel Margin="16">
                        <TextBlock Text="Welcome" FontSize="24" FontWeight="Bold"/>
                        <TextBlock Text="{Binding UserName}"/>
                        <TextBlock Text="{Binding Count, StringFormat='{}{0} items'}"/>
                        <TextBlock Text="{x:Static p:Strings.Greeting}"/>
                        <TextBlock Text="Loading…" Visibility="{Binding IsLoading, Converter={StaticResource BoolVis}}"/>
                        <Border Visibility="Collapsed"><TextBlock Text="Never shown"/></Border>
                        <Grid>
                            <TextBlock Text="Type a name" IsHitTestVisible="False" Visibility="{Binding Name, Converter={StaticResource EmptyToVisible}}"/>
                            <TextBox Text="{Binding Name}"/>
                        </Grid>
                        <PasswordBox/>
                        <ComboBox><ComboBoxItem Content="Red"/><ComboBoxItem Content="Green"/></ComboBox>
                        <CheckBox Content="Remember me"/>
                        <StackPanel Orientation="Horizontal">
                            <Button Content="Save" Style="{StaticResource PrimaryButton}"/>
                            <Button Content="&#xE713;" FontFamily="Segoe MDL2 Assets"/>
                        </StackPanel>
                        <TabControl>
                            <TabItem Header="General"><TextBlock Text="General settings"/></TabItem>
                            <TabItem Header="Advanced"><TextBlock Text="Advanced settings"/></TabItem>
                        </TabControl>
                        <DataGrid ItemsSource="{Binding Rows}">
                            <DataGrid.Columns>
                                <DataGridTextColumn Header="Name" Binding="{Binding Name}"/>
                                <DataGridTextColumn Header="Email" Binding="{Binding Email}"/>
                            </DataGrid.Columns>
                        </DataGrid>
                        <ItemsControl ItemsSource="{Binding Tags}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <StackPanel>
                                        <TextBlock Text="{Binding Title}"/>
                                        <TextBlock Text="selected" Visibility="{Binding IsSelected, Converter={StaticResource BoolVis}}"/>
                                    </StackPanel>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <local:BadgeControl/>
                        <StackPanel Orientation="Horizontal"><TextBlock Text="✓"/><TextBlock Text="Done"/></StackPanel>
                    </StackPanel>
                </ScrollViewer>
            </UserControl>
            """;
        W(app, "Views/HomeView.xaml", homeXaml);
        W(app, "Views/BadgeControl.xaml", $$$"""
            <UserControl x:Class="WpfDemo.Views.BadgeControl" {{{WpfNs}}}>
                <Border Background="{DynamicResource Accent.1}" CornerRadius="10" Padding="8,2">
                    <TextBlock Text="NEW"/>
                </Border>
            </UserControl>
            """);
        string settingsXaml = $$$"""
            <Window x:Class="WpfDemo.Views.SettingsWindow" {{{WpfNs}}}
                    Title="Preferences" Width="480" SizeToContent="Height">
                <DockPanel Margin="16">
                    <TextBlock DockPanel.Dock="Top" Text="Preferences" FontSize="20" FontWeight="SemiBold"/>
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right">
                        <Button Content="OK" IsDefault="True"/>
                        <Button Content="Cancel" IsCancel="True"/>
                    </StackPanel>
                    <Grid>
                        <Grid.ColumnDefinitions><ColumnDefinition Width="120"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
                        <Label Content="Server"/>
                        <TextBox Grid.Column="1"/>
                        <Label Grid.Row="1" Content="Port"/>
                        <TextBox Grid.Row="1" Grid.Column="1"/>
                    </Grid>
                </DockPanel>
            </Window>
            """;
        W(app, "Views/SettingsWindow.xaml", settingsXaml);
        W(app, "Views/Broken.xaml", $"<UserControl x:Class=\"WpfDemo.Views.Broken\" {WpfNs}>\n    <StackPanel>\n        <TextBlock Text=\"Before the break\"/>\n        <TextBlock Text=\"unterminated\n");
        // a DTD pointing an entity at a local file: never expanded, never read
        string probe = Path.Combine(temp, "xxe-probe.txt");
        File.WriteAllText(probe, "XXE-MARKER");
        W(app, "Views/Evil.xaml", $"<?xml version=\"1.0\"?>\n<!DOCTYPE UserControl [<!ENTITY secret SYSTEM \"{new Uri(probe).AbsoluteUri}\">]>\n<UserControl x:Class=\"WpfDemo.Views.Evil\" {WpfNs}><TextBlock Text=\"&secret;\"/></UserControl>\n");
        string console = Path.Combine(temp, "console-tool");
        W(console, "Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        // ── finding the app ──
        var info = StudioProject.Detect(root);
        Check("wpf: a solution whose app project is one folder down is a simulated WPF app (a docs/index.html doesn't make it a web site)",
              info.Kind == StudioProjectKind.Simulated && info.Framework == "wpf", $"{info.Kind} {info.Framework}");
        var direct = StudioProject.Detect(app);
        Check("wpf: the project folder itself too", direct.Kind == StudioProjectKind.Simulated && direct.Framework == "wpf", $"{direct.Kind} {direct.Framework}");
        Check("wpf: a console project is not a WPF app", StudioProject.Detect(console).Kind == StudioProjectKind.Unsupported, StudioProject.Detect(console).Kind.ToString());

        var r = PageCatalog.Discover(info, "th");
        var pages = r.Pages;
        Check("wpf: one page per view — the main window first, then the view it shows, then the others",
              string.Join(",", pages.Select(p => p.Detail)) == "MainWindow,HomeView,BadgeControl,Broken,SettingsWindow", string.Join(",", pages.Select(p => p.Detail)));
        Check("wpf: App.xaml and resource dictionaries are not pages", !pages.Any(p => p.Detail is "App" or "Dark" or "Light" or "Styles"));
        Check("wpf: a DTD in a XAML file is refused — the file isn't listed and its entity never read",
              !pages.Any(p => p.Detail == "Evil") && !pages.Any(p => (p.Title + p.Detail).Contains("XXE-MARKER")), string.Join(",", pages.Select(p => p.Detail)));
        Check("wpf: windows and views are grouped (the gallery's filter chips)",
              pages.Where(p => p.Group == "window").Select(p => p.Detail).SequenceEqual(new[] { "MainWindow", "SettingsWindow" }) && pages.Count(p => p.Group == "view") == 3);
        Check("wpf: a window is named by its Title, a view by its class",
              pages[0].Title == "Demo App" && pages.First(p => p.Detail == "SettingsWindow").Title == "Preferences" && pages.First(p => p.Detail == "HomeView").Title == "Home",
              string.Join(" | ", pages.Select(p => p.Title)));
        Check("wpf: each page opens its XAML file at its root element", pages[1].SourcePath!.EndsWith(Path.Combine("Views", "HomeView.xaml")) && pages[1].SourceLine == 1 && pages[1].Kind == StudioPageKind.Screen && pages[1].Route == null);
        Check("wpf: the catalog's sketcher is the WPF one", r.Screens is WpfProject && r.Flutter == null);
        Check("wpf: a text in another language comes from the running app's translations (LocalizationService)",
              LocalizationService.Lookup("nav.settings", "th") == "ตั้งค่า" && LocalizationService.Lookup("nav.settings", "en") == "Settings" && LocalizationService.Lookup("no.such.key", "th") == null);

        // ── a view ──
        var home = r.Screens!.Sketch(pages[1])!;
        var hn = home.Root.Walk().ToList();
        string homePath = Path.Combine(app, "Views", "HomeView.xaml");
        Check("view: drawn as a desktop frame (no phone status bar)", home.Desktop && home.Width >= 1000 && home.Caption == null, $"{home.Desktop} {home.Width}×{home.Height} '{home.Caption}'");
        var welcome = hn.FirstOrDefault(n => n.Text == "Welcome");
        Check("view: a big bold TextBlock is a heading, and it remembers its line", welcome is { Kind: SketchKind.Heading } && welcome.File == homePath && welcome.Line == LineOf(homeXaml, "Text=\"Welcome\""),
              $"{welcome?.Kind} {welcome?.File}:{welcome?.Line}");
        Check("view: a bound text is '…', a StringFormat keeps its words", hn.Any(n => n.Kind == SketchKind.Text && n.Text == "…") && hn.Any(n => n.Text == "… items"), Nodes(home));
        Check("view: {x:Static} reads the .resx in the UI language", hn.Any(n => n.Text == "สวัสดี"), Nodes(home));
        Check("view: what shows only while loading, and Visibility=\"Collapsed\", are left out", !hn.Any(n => n.Text is "Loading…" or "Never shown"), Nodes(home));
        Check("view: a hint text laid over a TextBox is its placeholder", hn.Any(n => n.Kind == SketchKind.Input && n.Text == "Type a name") && !hn.Any(n => n.Kind == SketchKind.Text && n.Text == "Type a name"), Nodes(home));
        Check("view: PasswordBox and ComboBox are inputs (with the first item)", hn.Any(n => n.Kind == SketchKind.Input && n.Variant == "password") && hn.Any(n => n.Kind == SketchKind.Input && n.Variant == "dropdown" && n.Text == "Red"));
        Check("view: a CheckBox is a switch with its label", hn.Any(n => n.Kind == SketchKind.Row && n.Children.Count == 2 && n.Children[0].Kind == SketchKind.Toggle && n.Children[1].Text == "Remember me"));
        var save = hn.FirstOrDefault(n => n.Kind == SketchKind.Button && n.Text == "Save");
        Check("view: a button styled PrimaryButton (BasedOn BaseButton) is filled", save?.Variant == "filled", save?.Variant ?? "no Save button");
        Check("view: a Segoe MDL2 glyph is an icon button with that glyph", hn.Any(n => n.Kind == SketchKind.IconButton && n.Icon == "") && IconGlyphs.For("") == "" && IconGlyphs.For("home") != null);
        var tabs = hn.FirstOrDefault(n => n.Kind == SketchKind.Tabs);
        Check("view: a TabControl is tabs with the first tab's content", tabs != null && tabs.Items.SequenceEqual(new[] { "General", "Advanced" }) && hn.Any(n => n.Text == "General settings") && !hn.Any(n => n.Text == "Advanced settings"), Nodes(home));
        Check("view: a DataGrid is a table of its column headers", hn.Any(n => n.Kind == SketchKind.Table && n.Items.SequenceEqual(new[] { "Name", "Email" })));
        var tagList = hn.FirstOrDefault(n => n.Kind == SketchKind.List && n.Widget == "ItemsControl");
        Check("view: an ItemsControl is a few rows of its item template — the item's usual content, not its states",
              tagList != null && tagList.Children.Count == 3 && !tagList.Walk().Any(n => n.Text == "selected"), tagList == null ? Nodes(home) : string.Join(",", tagList.Walk().Select(n => n.Kind + ":" + n.Text)));
        var badge = hn.FirstOrDefault(n => n.Text == "NEW");
        Check("view: a UserControl of the project is drawn from its own XAML, remembering where the view uses it",
              badge is { Kind: SketchKind.Chip } && badge.File!.EndsWith("BadgeControl.xaml") && badge.UseFile == homePath && badge.UseLine == LineOf(homeXaml, "<local:BadgeControl"),
              $"{badge?.Kind} {badge?.File}:{badge?.Line} use {badge?.UseFile}:{badge?.UseLine}");
        var done = hn.FirstOrDefault(n => n.Kind == SketchKind.Row && n.Children.Count == 2 && n.Children[1].Text == "Done");
        Check("view: a horizontal StackPanel sizes a short text to itself (a ✓ stays next to its words)", done != null && done.Children[0].Width is > 0 and < 40, done == null ? "no row" : $"w={done.Children[0].Width}");

        // ── the main window ──
        var main = r.Screens.Sketch(pages[0])!;
        var mn = main.Root.Walk().ToList();
        string mainPath = Path.Combine(app, "MainWindow.xaml");
        Check("window: its own size and title bar", main.Desktop && main.Width == 1100 && main.Height == 700 && main.Caption == "Demo App" && main.Title == "Demo App", $"{main.Width}×{main.Height} '{main.Caption}'");
        Check("window: {services:Loc key} in the UI language", mn.Any(n => n.Kind == SketchKind.Heading && n.Text == "ตั้งค่า"), Nodes(main));
        Check("window: the app's colours come from the theme App.xaml merges (an alias followed), not another theme file",
              main.Theme.Accent == 0xFFFF7A00 && main.Theme.Background == 0xFF101418 && main.Theme.Dark, $"accent {main.Theme.Accent:X8} bg {main.Theme.Background:X8}");
        var shown = mn.FirstOrDefault(n => n.Text == "Welcome");
        Check("window: a ContentControl bound to a view model shows the view its DataTemplate names, opened where the window uses it",
              shown != null && shown.File == homePath && shown.UseFile == mainPath && shown.UseLine == LineOf(mainXaml, "<views:HomeView"), $"{shown?.File}:{shown?.Line} use {shown?.UseFile}:{shown?.UseLine}");
        var people = mn.FirstOrDefault(n => n.Kind == SketchKind.List && n.Widget == "ListBox People");
        Check("window: a ListBox draws rows of its keyed ItemTemplate from Styles.xaml",
              people != null && people.Children.Count == 3 && people.Children[0].Walk().Any(n => n.Kind == SketchKind.Icon && n.Icon == "")
              && people.Children[0].File!.EndsWith("Styles.xaml") && people.Children[0].UseFile == mainPath, people == null ? Nodes(main) : $"{people.Children.Count} rows, {people.Children.FirstOrDefault()?.File}");
        var body = mn.FirstOrDefault(n => n.Kind == SketchKind.Row && n.Children.Contains(people!));
        Check("window: a Grid's columns sit side by side (the 220-px list beside the page) — a binding-shown overlay over them is left out",
              body != null && people!.Width == 220 && body.Children.Count == 2 && !mn.Any(n => n.Text == "A gallery over everything"), body == null ? "no row" : string.Join(",", body.Children.Select(c => c.Kind + "/" + c.Width + "/" + c.Flex)));

        // ── a dialog ──
        var prefs = r.Screens.Sketch(pages.First(p => p.Detail == "SettingsWindow"))!;
        var pn = prefs.Root.Walk().ToList();
        var dock = prefs.Root.Children.FirstOrDefault();
        Check("dialog: a SizeToContent window is a landscape frame of its width", prefs.Width == 480 && prefs.Height > 240 && prefs.Height < 480, $"{prefs.Width}×{prefs.Height}");
        Check("dialog: DockPanel — the title on top, the buttons at the bottom",
              dock != null && dock.Children.FirstOrDefault()?.Text == "Preferences" && dock.Children.LastOrDefault() is { Kind: SketchKind.Row } last && last.Children.Select(c => c.Text).SequenceEqual(new[] { "OK", "Cancel" }),
              dock == null ? "none" : string.Join(",", dock.Children.Select(c => c.Kind + ":" + c.Text)));
        Check("dialog: a form Grid keeps each label beside its input (rows first)",
              pn.Count(n => n.Kind == SketchKind.Row && n.Children.Count == 2 && n.Children[0].Text is "Server" or "Port" && n.Children[0].Width == 120 && n.Children[1].Kind == SketchKind.Input) == 2, Nodes(prefs));
        Check("dialog: IsDefault makes the default button filled", pn.FirstOrDefault(n => n.Text == "OK")?.Variant == "filled");

        // ── a half-written view ──
        SketchNode[] broken = Array.Empty<SketchNode>();
        Exception? err = null;
        try { broken = r.Screens.Sketch(pages.First(p => p.Detail == "Broken"))!.Root.Walk().ToArray(); } catch (Exception ex) { err = ex; }
        Check("wpf: a half-written view still draws what comes before the error", err == null && broken.Any(n => n.Text == "Before the break"), err?.Message ?? "");

        // ── a view nested thousands deep: read up to a limit, never a stack overflow ──
        string deep = Path.Combine(temp, "wpf-deep");
        W(deep, "Deep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        W(deep, "Deep.xaml", $"<UserControl x:Class=\"Deep.Deep\" {WpfNs}>" + string.Concat(Enumerable.Repeat("<Border>", 20000)) + "<TextBlock Text=\"bottom\"/>"
                             + string.Concat(Enumerable.Repeat("</Border>", 20000)) + "</UserControl>");
        Exception? deepErr = null;
        ScreenSketch? deepSketch = null;
        try
        {
            var dc = PageCatalog.Discover(StudioProject.Detect(deep), "en");
            deepSketch = dc.Screens!.Sketch(dc.Pages.Single());
        }
        catch (Exception ex) { deepErr = ex; }
        Check("wpf: a view nested 20,000 deep is read up to a limit — listed, drawn, no crash", deepErr == null && deepSketch != null, deepErr?.Message ?? "");

        // ── English ──
        var en = PageCatalog.Discover(info, "en");
        var enMain = en.Screens!.Sketch(en.Pages[0])!.Root.Walk().ToList();
        var enHome = en.Screens.Sketch(en.Pages[1])!.Root.Walk().ToList();
        Check("wpf: the same views in English (translations and .resx)", enMain.Any(n => n.Text == "Settings") && enHome.Any(n => n.Text == "Hello"));

        // ── drawn by the real SketchView ──
        var bmp = SaveSketch(main, null, 0.5);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        var colours = new HashSet<int>();
        for (int i = 0; i < px.Length; i += 4 * 7) colours.Add(px[i] << 16 | px[i + 1] << 8 | px[i + 2]);
        Check("render: a window draws landscape at its own size (no phone frame)", bmp.PixelWidth == 550 && bmp.PixelHeight == 350 && colours.Count > 12, $"{bmp.PixelWidth}x{bmp.PixelHeight}, {colours.Count} colours");
        var tall = SaveSketch(main, null, 0.5, fullHeight: true);
        Check("render: the stage grows a long window to its whole content", tall.PixelWidth == 550 && tall.PixelHeight > 350, $"{tall.PixelWidth}x{tall.PixelHeight}");
    }
}
