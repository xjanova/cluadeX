namespace CluadeX.Services.Voice.Tts;

/// <summary>
/// How each girl SOUNDS (owner 2026-11-09: "พร้อมเสียงพูดบุคลิก cluadex ตามที่เธอตอบ") — the same characters
/// AgentPersona gives their words to. Shifts are added to the user's own fine settings, so the sliders still steer;
/// the tag opens the text on ElevenLabs v3 / v4, the instructions go to OpenAI gpt-4o-mini-tts, and rate / pitch
/// multiply the free voices'.
/// </summary>
public sealed record VoiceStyle(
    string Girl,
    double StabilityShift, double StyleShift, double SpeedFactor,
    string AudioTag,
    string Instructions,
    double RateFactor, double PitchFactor,
    string ThaiNote);

public static class VoicePersona
{
    public static readonly VoiceStyle Neutral = new("", 0, 0, 1, "", "", 1, 1, "เสียงปกติ");

    private static readonly Dictionary<string, VoiceStyle> Styles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cluadex"] = new("cluadex", +0.10, +0.20, 0.92, "[softly]",
            "Speak softly and sweetly, a little sleepy and shy, gentle and warm, unhurried; a tiny flustered smile in the voice.",
            0.92, 1.12, "นุ่ม ๆ ง่วง ๆ เขินนิด ๆ พูดช้า"),
        ["claude"] = new("claude", +0.05, +0.25, 0.95, "[warmly]",
            "Speak like a warm, kind big sister: calm, patient and clear, with a gentle smile; thoughtful pauses.",
            0.95, 1.0, "อบอุ่น ใจเย็น แบบพี่สาว"),
        ["codex"] = new("codex", +0.20, +0.05, 1.06, "[calmly]",
            "Speak cool and quietly confident, terse and matter-of-fact, a dry hint of amusement; brisk pace.",
            1.08, 0.95, "คูล นิ่ง พูดสั้น เร็วนิด"),
        ["antigravity"] = new("antigravity", -0.05, +0.35, 1.02, "[cheerfully]",
            "Speak dreamy and cheerful, light and floaty, upbeat but never rushed.",
            1.02, 1.1, "ฝัน ๆ ร่าเริง เบาสบาย"),
        ["gemini"] = new("gemini", -0.10, +0.40, 1.05, "[excited]",
            "Speak bright, curious and eager, lively with sparkle in the voice when ideas click.",
            1.06, 1.12, "สดใส ช่างสงสัย กระตือรือร้น"),
        ["grok"] = new("grok", -0.15, +0.50, 1.10, "[playfully]",
            "Speak cheeky, bold and energetic, teasing and quick, laughing easily.",
            1.12, 1.05, "ทะเล้น พลังเยอะ เร็ว"),
        ["mai"] = new("mai", +0.25, +0.05, 1.0, "[professionally]",
            "Speak like a calm, precise, polite secretary: composed, exact and clear, with quiet warmth.",
            1.0, 1.0, "สุภาพ แม่นยำ แบบเลขา"),
        ["mind"] = new("mai", +0.25, +0.05, 1.0, "[professionally]",
            "Speak like a calm, precise, polite secretary: composed, exact and clear, with quiet warmth.",
            1.0, 1.0, "สุภาพ แม่นยำ แบบเลขา"),
        ["brainx"] = new("brainx", +0.15, +0.15, 1.0, "[confidently]",
            "Speak clear, confident and friendly, like a capable team lead.",
            1.0, 1.0, "มั่นใจ ชัดเจน"),
        ["elevenlabs"] = new("elevenlabs", -0.10, +0.45, 1.0, "[expressively]",
            "Speak like a charismatic voice actress: expressive, polished and playful, with rich warm tone.",
            1.0, 1.05, "นักพากย์ มีสีสัน ชัดถ้อยชัดคำ"),
    };

    public static VoiceStyle For(string? girl) =>
        !string.IsNullOrWhiteSpace(girl) && Styles.TryGetValue(girl.Trim(), out var s) ? s : Neutral;

    /// <summary>The girls with their own sound, for the per-girl voice table.</summary>
    public static IReadOnlyList<string> Girls { get; } = ["cluadex", "claude", "codex", "antigravity", "gemini", "grok", "mai", "brainx", "elevenlabs"];
}
