using System.Windows;

namespace CluadeX.Views;

/// <summary>
/// Whether the chat's boxes dress in the speaking girl's look (ChatCharacter): her colour on the question card, the
/// option pills, the permission prompt and the composer frame, and her portrait on them. Set once on ChatView to the
/// theme's <c>Fx.Flourish</c> and inherited down the tree (into every ListBox row), so a style asks
/// <c>{Binding (views:CharacterLook.Show), RelativeSource={RelativeSource Self}}</c> = Visible. Classic's Fx.Flourish
/// is Collapsed: there every box keeps the theme's own look (owner 2026-10-18: "แน่นอนไม่มีผลกับ ธีม คราสสิค").
/// </summary>
public static class CharacterLook
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(Visibility), typeof(CharacterLook),
        new FrameworkPropertyMetadata(Visibility.Collapsed, FrameworkPropertyMetadataOptions.Inherits));

    public static Visibility GetShow(DependencyObject d) => (Visibility)d.GetValue(ShowProperty);
    public static void SetShow(DependencyObject d, Visibility v) => d.SetValue(ShowProperty, v);
}
