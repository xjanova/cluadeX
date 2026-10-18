using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Who is talking (ChatCharacter): the girl of the provider answering — or, with the BrainX cowork room as the model,
/// the room member answering right now. Her portrait and colour go on the boxes she opens (ask_user, permission
/// prompts) and on the composer's frame, so the owner can see at a glance who is asking (owner 2026-10-18).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The girl answering this chat now: the composer's frame and tag.</summary>
    public ChatCharacter ActiveCharacter => CharacterFor(_providerManager.ActiveProviderType, Messages);

    /// <summary>The provider's girl; for the cowork room, the member who spoke last in the latest reply that has one
    /// (the room itself — the BrainX girl — until someone in it has answered).</summary>
    public static ChatCharacter CharacterFor(AiProviderType provider, IEnumerable<ChatMessage>? messages)
    {
        if (provider != AiProviderType.BrainX) return ChatCharacter.ForProvider(provider);
        var last = messages?.LastOrDefault(m => m.Role == MessageRole.Assistant && m.RoomSpeaker != null);
        return last != null ? ChatCharacter.ForRoomAgent(last.RoomSpeaker) : ChatCharacter.ForProvider(provider);
    }

    /// <summary>The girl for a box a run opens now (its own chat's messages decide who in the room is speaking).</summary>
    private ChatCharacter CharacterForBox(ChatRun? run) => CharacterFor(_providerManager.ActiveProviderType, run?.Messages ?? Messages);

    private void InitCharacter()
    {
        void Raise() => OnPropertyChanged(nameof(ActiveCharacter));
        _providerManager.OnProviderChanged += _ => App.Current?.Dispatcher.BeginInvoke(Raise);
        ChatMessage.RoomSpeakerChanged += m => App.Current?.Dispatcher.BeginInvoke(() => { if (Messages.Contains(m)) Raise(); });
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Messages) or nameof(CurrentSession)) Raise(); };
    }
}
