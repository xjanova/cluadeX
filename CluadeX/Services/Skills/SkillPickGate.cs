using System.IO;
using System.Text.Json;

namespace CluadeX.Services.Skills;

/// <summary>
/// Smart skill picking, per chat (owner 2026-11-07: "ควรมี ให้เลือกปิดได้ในช่องแชท"). The Skills page sets the default for
/// every chat (<c>AppSettings.SkillAutoPickEnabled</c>); the composer's switch overrides it for one chat. Only chats that
/// differ from the default are kept, in DataRoot/skill-pick-chats.json — a chat with no entry follows the default.
/// </summary>
public static class SkillPickGate
{
    private static readonly Dictionary<string, bool> Overrides = new(StringComparer.Ordinal);
    private static readonly object Lock = new();
    private static string? _file;

    public static event Action<string>? Changed;

    /// <summary>Persist overrides here. Null = memory only (the harness).</summary>
    public static void Load(string? dataRoot)
    {
        lock (Lock)
        {
            _file = dataRoot == null ? null : Path.Combine(dataRoot, "skill-pick-chats.json");
            Overrides.Clear();
            try
            {
                if (_file != null && File.Exists(_file))
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_file)) ?? new())
                        Overrides[k] = v;
            }
            catch { /* a damaged file = every chat follows the default */ }
        }
    }

    /// <summary>Is picking on for <paramref name="chat"/>? No chat (a run outside the chat page) = off: every switched-on
    /// skill is listed, as before.</summary>
    public static bool IsOn(string? chat, bool defaultOn)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        lock (Lock) return Overrides.TryGetValue(chat, out var v) ? v : defaultOn;
    }

    public static void Set(string chat, bool on, bool defaultOn)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock)
        {
            bool was = Overrides.TryGetValue(chat, out var v) ? v : defaultOn;
            if (on == defaultOn) Overrides.Remove(chat); else Overrides[chat] = on;
            Save();
            if (was == on) return;
        }
        Changed?.Invoke(chat);
    }

    /// <summary>The default for every chat changed (Skills page): chats without an override follow it — tell the composer.</summary>
    public static void DefaultChanged() => Changed?.Invoke("");

    /// <summary>A chat that is gone: its override goes too.</summary>
    public static void Forget(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock) { if (!Overrides.Remove(chat)) return; Save(); }
    }

    private static void Save()
    {
        try { if (_file != null) File.WriteAllText(_file, JsonSerializer.Serialize(Overrides)); } catch { }
    }
}
