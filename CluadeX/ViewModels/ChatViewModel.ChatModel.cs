using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace CluadeX.ViewModels;

/// <summary>
/// Each chat keeps its own provider and model (Services/ChatModelStore.cs). Owner 2026-11-13: "บางแชทใช้ fable บางแชทใช้
/// opus หรือ codex มันกลายเป็นใช้ร่วมกันทั้งหมดซึ่งมันผิด … แชทใครแชทมัน".
/// <list type="bullet">
/// <item><b>A turn begins</b> (<see cref="PinChatModel"/>): the run goes out on the chat's own pick. A chat with none yet
/// takes the app's pick of that moment and keeps it — so a model picked later in another chat never reaches it.</item>
/// <item><b>The owner picks a model</b> with a chat on screen (<see cref="RememberChatPick"/>): that chat's pick, and
/// its run in progress when it has one. No other chat is touched.</item>
/// <item><b>The owner opens a chat</b> (<see cref="FollowChatPick"/>): the app's pick becomes that chat's, so the model
/// button, the price, the girl and the usage strip show what THIS chat runs on. The app's pick is what a new chat
/// starts with.</item>
/// <item><b>The app's pick changes some other way</b> while a chat is on screen (the Settings page, a model loaded on
/// this PC) — the composer shows it, so it is the pick of the chat on screen: at once when the provider changed
/// (<see cref="RestampChatOnScreen"/>), else at that chat's next turn (<see cref="PinChatModel"/>). Its run in progress
/// is left on the model it started with.</item>
/// </list>
/// The one rule behind the last two: <b>for the chat on screen, what the composer shows is what runs</b>. It holds once
/// the app's pick was matched with that chat (<see cref="_pickSyncedChat"/>); a chat that is not on screen runs on the
/// pick it keeps, whatever the composer shows.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The app's pick is being set to the pick of the chat just opened: not a new pick for that chat.</summary>
    private int _followingChatPick;

    /// <summary>The chat the app's pick was last matched with (opened and followed, or picked for). Only for that chat,
    /// while it is on screen, does the app's pick stand for the chat's own.</summary>
    private string? _pickSyncedChat;

    /// <summary>Called from InitControls (the constructor).</summary>
    private void InitChatModels()
    {
        ChatModelStore.Load(_settingsService.DataRoot);
        _providerManager.OnProviderChanged += _ =>
        {
            // Decided here, inside the switch itself: a change made to follow a chat just opened, or by the model box
            // (which stamps its chat itself), is not a new pick. Posted, a quick second chat switch would be stamped
            // with the first one's pick.
            if (_followingChatPick > 0 || _switchingModel) return;
            string? chat = CurrentSession?.Id;
            if (chat != null && chat == _pickSyncedChat) App.Current?.Dispatcher.BeginInvoke(() => RestampChatOnScreen(chat));
        };
    }

    /// <summary>The app's pick stands for <paramref name="session"/>'s own: it is on screen, was matched with it, and
    /// nothing is changing the pick right now.</summary>
    private bool AppPickIsFor(ChatSession session) =>
        ReferenceEquals(session, CurrentSession) && _pickSyncedChat == session.Id && _followingChatPick == 0 && !_switchingModel;

    /// <summary>The model Settings hold for <paramref name="type"/>, as the composer names it ("default" for a CLI's own).</summary>
    private string AppModelOf(AiProviderType type) =>
        _settingsService.Settings.ProviderConfigs.TryGetValue(type.ToString(), out var cfg) && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId)
            ? cfg.EffectiveModelId!.Trim() : DefaultModelOf(type);

    /// <summary>
    /// A turn begins in <paramref name="session"/> (BeginTurnFor, before the run's scope is entered): the run is put on
    /// the chat's own provider and pinned to its model. Called while <see cref="AgentRunScope.Current"/> is still the run
    /// that asked for this turn, when one did (new_chat, message_chat, a report waking its parent).
    /// </summary>
    private void PinChatModel(ChatRun run, ChatSession session)
    {
        var asking = AgentRunScope.Current;
        var kept = ChatModelStore.Get(session.Id);
        if (kept != null && AppPickIsFor(session))
        {
            // The chat on screen runs on what its composer shows. They differ when the pick was changed without the
            // model box (the Settings page, a model loaded on this PC), or the chat's own could not be shown (its key
            // was removed): the shown one is the chat's from here on.
            var shown = _providerManager.PickedProviderType;
            if (ChatModelStore.Set(session.Id, shown, AppModelOf(shown)))
                _debugLog?.Info("Chat", $"chat {session.Id} takes the pick on screen ({shown}) over the one it kept ({kept.Provider}/{kept.Model})");
            kept = ChatModelStore.Get(session.Id);   // null when the pick on screen is a model on this PC
        }
        AiProviderType type;
        string model;
        if (kept != null && Enum.TryParse(kept.Provider, out type) && ChatModelStore.CanPin(type))
            model = kept.Model;
        else if (asking?.PinnedProviderId is { } askingId && Enum.TryParse(askingId, out type) && ChatModelStore.CanPin(type))
            // A chat another chat's agent opened starts on that chat's own pick — not on the stand-in a failover may
            // have put that run on for now.
            model = asking.PinnedModel ?? AppModelOf(type);
        else
        {
            type = run.Scope.ProviderType ?? _providerManager.PickedProviderType;
            if (!ChatModelStore.CanPin(type)) return;   // a model on this PC, the BrainX room: the app's pick, as before
            model = AppModelOf(type);
        }

        if (run.Scope.ProviderType != type)
        {
            var own = _providerManager.FindProvider(type);
            if (own == null)
            {
                // The chat's provider is not in this build / on this PC any more: this turn runs on the app's pick, and the
                // chat keeps its pick for the day the provider is back.
                _debugLog?.Warn("Chat", $"chat {session.Id} keeps {type}, which is not available — this turn runs on {run.Scope.ProviderType}");
                return;
            }
            run.Scope.Provider = own;
            run.Scope.ProviderType = type;
        }
        run.Scope.PinModel(type.ToString(), model);
        if (kept == null && ChatModelStore.Set(session.Id, type, model))
            _debugLog?.Info("Chat", $"chat {session.Id} keeps {type}/{(model.Length > 0 ? model : "(its default)")} from this turn on");
    }

    /// <summary>The owner picked <paramref name="c"/> with a chat on screen: it is that chat's pick, and its run in
    /// progress follows (the loop sees the new model at its next step and carries on with it).</summary>
    private void RememberChatPick(ChatModelChoice c, ChatSession? chat)
    {
        if (chat == null) return;
        ChatModelStore.Set(chat.Id, c.Provider, c.Model);
        if (ReferenceEquals(chat, CurrentSession)) _pickSyncedChat = chat.Id;
        if (RunOf(chat)?.Scope is { } scope)
        {
            if (ChatModelStore.CanPin(c.Provider)) scope.PinModel(c.Provider.ToString(), c.Model);
            else scope.PinModel(null, null);
        }
    }

    /// <summary>A model on this PC was picked for the chat on screen: no pin — it follows the app's pick (the loaded weights).</summary>
    private void ForgetChatPick()
    {
        if (CurrentSession is not { } chat) return;
        ChatModelStore.Forget(chat.Id);
        RunOf(chat)?.Scope?.PinModel(null, null);
    }

    /// <summary>
    /// The owner opened <paramref name="session"/>: the app's pick becomes this chat's own, so the composer shows what
    /// this chat runs on. Nothing is stopped or carried over — runs in other chats keep the provider and model on their
    /// scope. A chat with no pick yet, a provider with no key or not installed, a chat that was left meanwhile: nothing
    /// changes.
    /// </summary>
    private async Task FollowChatPick(ChatSession session)
    {
        if (_switchingModel || !ReferenceEquals(CurrentSession, session)) return;
        try { await FollowChatPickCore(session); }
        finally
        {
            // Followed, or it could not be (no pick yet, its provider not set up): either way the composer now shows
            // what this chat's next turn runs on. While another chat's follow is still on its way (two chats opened
            // one after the other), only a pick that really is this chat's counts.
            if (ReferenceEquals(CurrentSession, session) && (_followingChatPick == 0 || AppPickMatches(session.Id))) _pickSyncedChat = session.Id;
        }
    }

    /// <summary>The app's pick is the one <paramref name="chat"/> keeps.</summary>
    private bool AppPickMatches(string chat)
    {
        var kept = ChatModelStore.Get(chat);
        var shown = _providerManager.PickedProviderType;
        return kept != null && kept.Provider == shown.ToString()
            && (kept.Model.Length == 0 || string.Equals(AppModelOf(shown), kept.Model, StringComparison.Ordinal));
    }

    private async Task FollowChatPickCore(ChatSession session)
    {
        var kept = ChatModelStore.Get(session.Id);
        if (kept == null || !Enum.TryParse<AiProviderType>(kept.Provider, out var type) || !ChatModelStore.CanPin(type)) return;
        bool sameProvider = _providerManager.PickedProviderType == type;
        bool sameModel = kept.Model.Length == 0 || string.Equals(AppModelOf(type), kept.Model, StringComparison.Ordinal);
        if (sameProvider && sameModel) return;
        // Only a provider the composer offers and that is set up: a pick whose key was removed must not open a wizard
        // or leave the app on a provider that cannot answer. Cloud providers are the paid tier, as in the model box.
        if (ChatModelChoices.Count == 0) RefreshChatModelChoices();
        var offered = ChatModelChoices.Where(c => c.Provider == type).ToList();
        if (offered.Count == 0 || offered.All(c => c.NeedsKey)) return;
        if (!sameProvider && type is not AiProviderType.Ollama && CompatiblePresets.For(type)?.Local != true && _activation?.IsActivated != true) return;

        _followingChatPick++;
        try
        {
            if (!sameModel)
            {
                string key = type.ToString();
                _settingsService.UpdateSettings(s =>
                {
                    if (!s.ProviderConfigs.TryGetValue(key, out var cfg)) s.ProviderConfigs[key] = cfg = new ProviderConfig();
                    cfg.SelectedModel = kept.Model;
                    cfg.UseCustomModel = false;
                });
            }
            // A provider switch that meets another one on its way is dropped by the manager (two chats opened one after
            // the other, Ctrl+Tab): asked again until it holds, for as long as this chat is the one on screen.
            for (int tries = 0; !sameProvider && tries < 25; tries++)
            {
                await _providerManager.SwitchProviderAsync(type);
                if (_providerManager.PickedProviderType == type || !ReferenceEquals(CurrentSession, session)) break;
                await Task.Delay(200);
            }
            _debugLog?.Info("Chat", $"the app's pick follows chat {session.Id}: {type}/{(kept.Model.Length > 0 ? kept.Model : "(its default)")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _debugLog?.Warn("Chat", $"following chat {session.Id}'s pick ({type}/{kept.Model}) failed", ex);
        }
        finally
        {
            _followingChatPick--;
            RefreshChatModelChoices();
            OnPropertyChanged(nameof(SelectedChatModel));
        }
    }

    /// <summary>The app's provider changed while <paramref name="chat"/> was on screen, and not because it was just
    /// opened or picked for: it is that chat's pick from its next turn. Only a chat that already keeps one (a new chat is
    /// pinned at its first turn), and never its run in progress — that one finishes on the model it started with.</summary>
    private void RestampChatOnScreen(string chat)
    {
        if (_followingChatPick > 0 || _switchingModel) return;
        if (CurrentSession?.Id != chat || _pickSyncedChat != chat || ChatModelStore.Get(chat) == null) return;
        var type = _providerManager.PickedProviderType;
        ChatModelStore.Set(chat, type, AppModelOf(type));   // (a model on this PC drops the pin: the chat follows the app)
    }
}
