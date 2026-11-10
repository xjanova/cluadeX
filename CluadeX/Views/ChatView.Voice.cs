using System.Windows;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>
/// The chat's side of reading replies aloud: the ▶ on a reply's hover strip, and the player box's "go to this reply".
/// A click on a reply's words reads nothing (owner 2026-11-11: a click to read or select the text read a reply she had
/// already heard again — "เสียงควรเล่นรอบเดียว"); only ▶ plays one again.
/// </summary>
public partial class ChatView
{
    private void ListenMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is FrameworkElement { DataContext: ChatMessage reply })
            vm.PlayMessageVoice(reply);
    }

    /// <summary>The player box's first words were clicked: the chat scrolls to that reply's bubble.</summary>
    private void VoicePlayer_ShowMessage(ChatMessage message)
    {
        if (ChatList.Items.Contains(message)) ChatList.ScrollIntoView(message);
    }
}
