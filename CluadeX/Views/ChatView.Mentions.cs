using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The composer's @-mentions, "explain simply" on a reply and a click on the side panel's picture
/// (ChatViewModel.Vibe holds the logic; this is only the keys, the popup and the clicks).</summary>
public partial class ChatView
{
    private int _mentionVersion;
    private (int Start, string Query)? _mention;

    private void InputBox_MentionChanged(object sender, TextChangedEventArgs e) => _ = RefreshMentionsAsync();

    /// <summary>The caret moved (into or out of an "@word", or after text set from code, where TextChanged saw the old
    /// caret). Only does anything when a list is open or the caret now sits in an "@word".</summary>
    private void InputBox_MentionCaret(object sender, RoutedEventArgs e)
    {
        var at = Mentions.QueryAt(InputBox.Text, InputBox.CaretIndex);
        if (at == null && !MentionPopup.IsOpen) return;
        if (at == _mention && MentionPopup.IsOpen) return;
        _ = RefreshMentionsAsync();
    }

    private async Task RefreshMentionsAsync()
    {
        int v = ++_mentionVersion;
        var at = Mentions.QueryAt(InputBox.Text, InputBox.CaretIndex);
        _mention = at;
        if (at == null || DataContext is not ChatViewModel vm) { MentionPopup.IsOpen = false; return; }

        MentionPopup.IsOpen = true;
        var source = await vm.MentionSourceAsync();
        if (v != _mentionVersion) return;   // typed on: the newer call fills it
        if (source == null)
        {
            MentionList.ItemsSource = null;
            ShowMentionNote(vm.HasProject ? "vibe.mention.none" : "vibe.mention.noProject");
            return;
        }
        var items = Mentions.Match(at.Value.Query, source.Value.Files, source.Value.Symbols);
        MentionList.ItemsSource = items;
        if (items.Count == 0) ShowMentionNote("vibe.mention.none");
        else
        {
            MentionEmpty.Visibility = Visibility.Collapsed;
            MentionList.Visibility = Visibility.Visible;
            MentionList.SelectedIndex = 0;
            MentionList.ScrollIntoView(items[0]);
        }
    }

    private void ShowMentionNote(string key)
    {
        MentionEmpty.Text = LocalizedResources.Instance[key];
        MentionEmpty.Visibility = Visibility.Visible;
        MentionList.Visibility = Visibility.Collapsed;
    }

    /// <summary>While the list is open the arrows move in it and Enter / Tab take the pick — before the box's own
    /// Enter, which sends (a XAML handler runs before the constructor's; Handled stops that one).</summary>
    private void InputBox_MentionKeys(object sender, KeyEventArgs e)
    {
        if (!MentionPopup.IsOpen) return;
        int count = MentionList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                MentionList.SelectedIndex = (MentionList.SelectedIndex + 1) % count;
                MentionList.ScrollIntoView(MentionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                MentionList.SelectedIndex = (MentionList.SelectedIndex - 1 + count) % count;
                MentionList.ScrollIntoView(MentionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when Keyboard.Modifiers == ModifierKeys.None && MentionList.SelectedItem is MentionItem pick:
                ApplyMention(pick);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseMentions();
                e.Handled = true;
                break;
        }
    }

    private void MentionList_Click(object sender, MouseButtonEventArgs e)
    {
        if (MentionList.SelectedItem is MentionItem pick) ApplyMention(pick);
    }

    private void ApplyMention(MentionItem pick)
    {
        if (_mention is not { } at) { CloseMentions(); return; }
        var (text, caret) = Mentions.Apply(InputBox.Text, at.Start, InputBox.CaretIndex, pick);
        CloseMentions();
        InputBox.Text = text;   // the binding carries it to UserInput
        InputBox.CaretIndex = caret;
        InputBox.Focus();
        _mentionVersion++;      // the TextChanged this raised must not reopen it
        _mention = null;
        MentionPopup.IsOpen = false;
    }

    private void CloseMentions()
    {
        _mentionVersion++;
        _mention = null;
        MentionPopup.IsOpen = false;
    }

    // ─── explain simply ───

    private void ExplainMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is FrameworkElement { DataContext: ChatMessage reply })
            vm.ExplainSimply(reply);
    }

    // ─── the side panel's picture: click a spot to ask about it ───

    private void SidePicture_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Tag: string path } img || DataContext is not ChatViewModel vm) return;
        if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return;
        var p = e.GetPosition(img);
        if (vm.AskAboutPictureSpot(path, p.X / img.ActualWidth, p.Y / img.ActualHeight))
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        e.Handled = true;
    }
}
