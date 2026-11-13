using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>The provider and model one chat keeps (<see cref="ChatModelStore"/>): the provider's id as Settings name it
/// ("Anthropic", "OpenAIDev" …) and the model as Settings hold it ("claude-opus-5-5", an alias like "fable", "default").</summary>
public sealed record ChatModelPick(string Provider, string Model);

/// <summary>
/// Each chat keeps its own provider and model. Owner 2026-11-13: "บางแชทใช้ fable บางแชทใช้ opus หรือ codex มันกลายเป็น
/// ใช้ร่วมกันทั้งหมดซึ่งมันผิด มันต้องใช้แยกกัน แชทใครแชทมัน … เลยกลายเป็นสลับโมเดลกลางแชทซึ่งทำให้เสียโทเค็นหนัก".
/// <para>The pick used to be one for the whole app (<c>Settings.ActiveProvider</c> + each provider's
/// <c>SelectedModel</c>), and every provider read the model from Settings on every request. So a model picked in chat B
/// reached chat A's next turn — and, on the same provider, chat A's run in progress, whose loop then re-wrote its whole
/// conversation for the new model (the prompt cache gone, 100k+ tokens written again).</para>
/// <para>Now a chat is pinned at its first turn to the pick of that moment, and re-pinned only when the owner picks a
/// model with that chat on screen. A run carries the pin on its scope (<c>AgentRunScope.PinModel</c>), where every
/// provider reads it before Settings. The app's pick stays, as what a new chat starts with and what the composer shows;
/// it follows the chat on screen. Kept in DataRoot/chat-models.json.</para>
/// <para>Not pinned: a model on this PC (Local, LlamaServer — one set of weights is loaded for the whole app) and the
/// BrainX room. A chat that ran on one of those follows the app's pick, as before.</para>
/// </summary>
public static class ChatModelStore
{
    private static readonly Dictionary<string, ChatModelPick> Picks = new(StringComparer.Ordinal);
    private static readonly object Lock = new();
    private static string? _file;

    /// <summary>Persist the picks here. Null = memory only (the harness).</summary>
    public static void Load(string? dataRoot)
    {
        lock (Lock)
        {
            _file = dataRoot == null ? null : Path.Combine(dataRoot, "chat-models.json");
            Picks.Clear();
            try
            {
                if (_file != null && File.Exists(_file))
                    foreach (var (chat, pick) in JsonSerializer.Deserialize<Dictionary<string, ChatModelPick>>(File.ReadAllText(_file)) ?? new())
                        if (!string.IsNullOrWhiteSpace(chat) && pick != null && !string.IsNullOrWhiteSpace(pick.Provider))
                            Picks[chat] = new ChatModelPick(pick.Provider.Trim(), (pick.Model ?? "").Trim());
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file = no chat is pinned: each takes the app's pick at its next turn, as a new chat does
            }
        }
    }

    /// <summary>A provider a chat can be pinned to: every cloud API, CLI login and Ollama; not the weights loaded on this
    /// PC (one model for the whole app) and not the BrainX room.</summary>
    public static bool CanPin(AiProviderType type) =>
        type is not (AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.BrainX);

    /// <summary>The pick <paramref name="chat"/> keeps, or null when it has none yet.</summary>
    public static ChatModelPick? Get(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return null;
        lock (Lock) return Picks.TryGetValue(chat, out var pick) ? pick : null;
    }

    /// <summary>Keep <paramref name="provider"/> / <paramref name="model"/> for <paramref name="chat"/>. False when nothing
    /// changed (or the provider is one a chat is not pinned to — then the chat's pick is dropped).</summary>
    public static bool Set(string? chat, AiProviderType provider, string? model)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        if (!CanPin(provider)) return Forget(chat);
        var pick = new ChatModelPick(provider.ToString(), (model ?? "").Trim());
        lock (Lock)
        {
            if (Picks.TryGetValue(chat, out var was) && was == pick) return false;
            Picks[chat] = pick;
            Save();
        }
        return true;
    }

    /// <summary>A chat that is gone, or that goes back to following the app's pick. True when it had one.</summary>
    public static bool Forget(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        lock (Lock)
        {
            if (!Picks.Remove(chat)) return false;
            Save();
        }
        return true;
    }

    /// <summary>How many chats keep a pick (the harness).</summary>
    public static int Count { get { lock (Lock) return Picks.Count; } }

    private static void Save()
    {
        if (_file == null) return;
        try
        {
            // whole file, written beside and swapped in: a crash mid-write must not leave half a file
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Picks));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: the picks hold for this run of the app, and each chat is pinned again at its next turn
        }
    }
}
