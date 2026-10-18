using System.Text.RegularExpressions;
using System.Windows.Media;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Who is talking in the chat, as the boxes show her: one of the girls (Assets\Girls) and her colour. The question card,
/// the option pills, the permission prompt and the composer's frame all take her portrait and her accent, so the box
/// looks like the one who asks (owner 2026-10-18: "ทำให้สมกับว่าคุณใช้ provider ไหน ถ้า claude ก็ใช้น้องอวาตาลสีส้ม …
/// ถ้าเป็น Cowork room model ก็ต้องให้ตรงว่าใครเป็นคนตอบในห้อง").
///
/// The colours are the girls' own (their outfits, BrainX's boot-screen glow per girl — BrainX.Client wwwroot/universe/
/// hud.js), not theme colours: a girl is the same colour in every theme. Readability stays the theme's job — text on
/// a box is a theme token, her colour goes on borders, tints and fills, and the text on a fill of her colour is
/// whichever of near-black or white reads better on it (<see cref="OnAccent"/>). The red danger prompt never takes
/// her colour (ChatView: IsPermissionDanger wins).
/// </summary>
public sealed class ChatCharacter
{
    /// <summary>Her picture's name under Assets\Girls ("claude", "codex", …).</summary>
    public string Girl { get; }
    /// <summary>The name on her tag ("Claude", "มายด์", or a room agent's own id when the room has someone new).</summary>
    public string Name { get; }
    public Color Accent { get; }

    public SolidColorBrush AccentBrush { get; }
    /// <summary>Her colour, see-through: the composer's frame while it is not focused.</summary>
    public SolidColorBrush FrameBrush { get; }
    /// <summary>A faint wash of her colour over a box's own surface.</summary>
    public SolidColorBrush TintBrush { get; }
    /// <summary>Text on a fill of her colour: near-black or white, whichever has the higher contrast.</summary>
    public SolidColorBrush OnAccentBrush { get; }

    public ImageSource? Image => GirlArt.Image(Girl);

    private ChatCharacter(string girl, string name, Color accent)
    {
        Girl = girl;
        Name = name;
        Accent = accent;
        AccentBrush = Frozen(accent);
        FrameBrush = Frozen(Color.FromArgb(0xA0, accent.R, accent.G, accent.B));
        TintBrush = Frozen(Color.FromArgb(0x1E, accent.R, accent.G, accent.B));
        OnAccentBrush = Frozen(OnAccent(accent));
        var wash = new LinearGradientBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B),
            Color.FromArgb(0x08, accent.R, accent.G, accent.B), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        wash.Freeze();
        WashBrush = wash;
        var glow = new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.4 };
        glow.Freeze();
        GlowEffect = glow;
    }

    /// <summary>Her colour washed diagonally over a card (strong at the top-left corner, where her portrait is).</summary>
    public Brush WashBrush { get; }
    /// <summary>A halo of her colour around a card (only where the theme has its flourishes on).</summary>
    public System.Windows.Media.Effects.Effect GlowEffect { get; }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ─── the girls ───

    public static readonly ChatCharacter Claude = new("claude", "Claude", Color.FromRgb(0xF0, 0x84, 0x3C));    // the orange girl
    public static readonly ChatCharacter Codex = new("codex", "Codex", Color.FromRgb(0x3C, 0xA8, 0xFF));
    public static readonly ChatCharacter Gemini = new("gemini", "Gemini", Color.FromRgb(0xA8, 0x8C, 0xFF));
    public static readonly ChatCharacter Grok = new("grok", "Grok", Color.FromRgb(0xA3, 0xE6, 0x35));
    public static readonly ChatCharacter Mind = new("mai", "มายด์", Color.FromRgb(0xC3, 0x9B, 0xFF));
    public static readonly ChatCharacter BrainX = new("brainx", "BrainX", Color.FromRgb(0x3E, 0x9B, 0xFF));
    public static readonly ChatCharacter CluadeX = new("cluadex", "CluadeX", Color.FromRgb(0xB9, 0x8C, 0xFF));

    public static IReadOnlyList<ChatCharacter> All { get; } = [Claude, Codex, Gemini, Grok, Mind, BrainX, CluadeX];

    /// <summary>The girl who answers for a provider — the same girls AgentPersona gives the voices to.</summary>
    public static ChatCharacter ForProvider(AiProviderType provider) => provider switch
    {
        AiProviderType.Anthropic or AiProviderType.ClaudeDev => Claude,
        AiProviderType.OpenAI => Codex,
        AiProviderType.Gemini => Gemini,
        AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama => Mind,   // local = มายด์
        AiProviderType.BrainX => BrainX,   // the room itself, until someone in it answers
        _ => CluadeX,
    };

    /// <summary>A girl by her picture's name (null/unknown = CluadeX's own girl).</summary>
    public static ChatCharacter ForGirl(string? girl) =>
        All.FirstOrDefault(c => string.Equals(c.Girl, girl, StringComparison.OrdinalIgnoreCase)) ?? CluadeX;

    private static readonly Dictionary<string, ChatCharacter> _roomStrangers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A member of the BrainX cowork room by her agent id (BrainXRoomProvider's "### 💬 {from}"): "claude" and Claude
    /// Code's local-agent-mode seat are Claude, "mind"/"broker" are มายด์ (the broker IS her — BrainX office.js), and
    /// so on. Someone the room has that we have no girl for stands as the BrainX girl under her own id.
    /// </summary>
    public static ChatCharacter ForRoomAgent(string? agent)
    {
        string a = (agent ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) return BrainX;
        if (a.StartsWith("claude") || a.Contains("brainx-brain") || a.StartsWith("anthropic")) return Claude;
        if (a.StartsWith("codex") || a.StartsWith("openai") || a.StartsWith("chatgpt") || a.StartsWith("gpt")) return Codex;
        if (a.StartsWith("gemini") || a.StartsWith("google")) return Gemini;
        if (a.StartsWith("grok") || a.StartsWith("xai")) return Grok;
        if (a is "mind" or "mai" or "broker" or "มายด์") return Mind;
        if (a.StartsWith("cluadex")) return CluadeX;
        if (a is "brainx" or "brain") return BrainX;
        lock (_roomStrangers)
        {
            if (!_roomStrangers.TryGetValue(a, out var c))
                _roomStrangers[a] = c = new ChatCharacter(BrainX.Girl, agent!.Trim(), BrainX.Accent);
            return c;
        }
    }

    private static readonly Regex SpeakerLine = new(@"^###\s*💬\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The room members who answered in a reply, in the order they first spoke (from its "### 💬 id" lines).</summary>
    public static List<string> RoomSpeakersIn(string? reply)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(reply) || !reply.Contains("💬", StringComparison.Ordinal)) return list;
        foreach (Match m in SpeakerLine.Matches(reply))
            if (!list.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) list.Add(m.Groups[1].Value);
        return list;
    }

    /// <summary>Who spoke last in a reply (the member answering right now while it streams), or null.</summary>
    public static string? LastRoomSpeakerIn(string? reply)
    {
        if (string.IsNullOrEmpty(reply) || !reply.Contains("💬", StringComparison.Ordinal)) return null;
        var all = SpeakerLine.Matches(reply);
        return all.Count == 0 ? null : all[^1].Groups[1].Value;
    }

    // ─── contrast ───

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    public static double Luminance(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>A property, not a static field: the girls above are built by static initialisers that run in text
    /// order, before a field declared down here would be set (it read as transparent — the name chips were blank).</summary>
    private static Color Ink => Color.FromRgb(0x14, 0x12, 0x1C);

    /// <summary>Near-black or white, whichever reads better on <paramref name="fill"/>.</summary>
    public static Color OnAccent(Color fill) => Contrast(fill, Ink) >= Contrast(fill, Colors.White) ? Ink : Colors.White;

    public override string ToString() => $"{Name} ({Girl})";
}
