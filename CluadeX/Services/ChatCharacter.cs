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
    /// <summary>The picture she shows, by its name under Assets\Girls ("claude", "codex", …): her own when the build has
    /// it, else CluadeX's girl standing in (<see cref="Own"/> says who she is meant to be).</summary>
    public string Girl { get; }
    /// <summary>Who she is: her own picture's name, whether or not the build has it yet ("groq", "colibri" …).</summary>
    public string Own { get; }
    /// <summary>The build has her own picture (not CluadeX's girl standing in).</summary>
    public bool HasOwnArt => Girl == Own;
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

    /// <summary>Where her face is in her full-figure picture: its centre (fractions of the width / height).</summary>
    private readonly double _faceX, _faceY;
    /// <summary>The side of the square around her face, as a fraction of the picture's height (forehead to chin, on the
    /// 1000 px cut-outs of 2026-11-07).</summary>
    private const double FaceSide = 0.12;
    private ImageBrush? _faceBrush;

    /// <summary>Her face, filling a round portrait (owner 2026-10-18: "รูปในวงกลม อวาต้า ให้โฟกัส หน้าเป็นหลัก"): a square
    /// around her face — each girl's pose puts it somewhere else — cut from her picture. Null without the picture.</summary>
    public ImageBrush? FaceBrush => _faceBrush ??= MakeFaceBrush();

    /// <summary>The face square in the picture's relative units (x, y, width, height), for a picture w × h pixels.</summary>
    public System.Windows.Rect FaceViewbox(double w, double h)
    {
        double fw = FaceSide * h / w;
        return new System.Windows.Rect(Math.Clamp(_faceX - fw / 2, 0, 1 - fw), Math.Clamp(_faceY - FaceSide / 2, 0, 1 - FaceSide), fw, FaceSide);
    }

    private ImageBrush? MakeFaceBrush()
    {
        if (Image is not System.Windows.Media.Imaging.BitmapSource bmp || bmp.PixelWidth == 0) return null;
        var b = new ImageBrush(bmp)
        {
            Stretch = Stretch.UniformToFill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = FaceViewbox(bmp.PixelWidth, bmp.PixelHeight),
        };
        b.Freeze();
        return b;
    }

    private ChatCharacter(string girl, string name, Color accent, double faceX, double faceY)
    {
        Own = girl;
        // A girl whose picture is not in the build yet (owner 2026-11-08: "เอไอมาสคอตสาวตัวไหนก็ทำให้ครบ" — the art comes
        // when the image quota does): CluadeX's girl stands in, framed on HER face, in this girl's own colour and name.
        bool has = GirlArt.Has(girl);
        Girl = has ? girl : StandIn;
        Name = name;
        Accent = accent;
        _faceX = has ? faceX : StandInFaceX;
        _faceY = has ? faceY : StandInFaceY;
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

    // The last two numbers: her face's centre in her picture (fraction of width, of height), measured on each PNG
    // (pose 1, the owner's 2026-11-07 art, cropped to the figure and 1000 px tall).
    public static readonly ChatCharacter Claude = new("claude", "Claude", Color.FromRgb(0xF0, 0x84, 0x3C), 0.51, 0.110);    // the orange girl
    public static readonly ChatCharacter Codex = new("codex", "Codex", Color.FromRgb(0x3C, 0xA8, 0xFF), 0.55, 0.120);
    public static readonly ChatCharacter Gemini = new("gemini", "Gemini", Color.FromRgb(0xA8, 0x8C, 0xFF), 0.52, 0.125);
    public static readonly ChatCharacter Grok = new("grok", "Grok", Color.FromRgb(0xA3, 0xE6, 0x35), 0.545, 0.115);
    public static readonly ChatCharacter Mind = new("mai", "มายด์", Color.FromRgb(0xC3, 0x9B, 0xFF), 0.49, 0.110);
    public static readonly ChatCharacter BrainX = new("brainx", "BrainX", Color.FromRgb(0x3E, 0x9B, 0xFF), 0.46, 0.125);
    public static readonly ChatCharacter CluadeX = new("cluadex", "CluadeX", Color.FromRgb(0xB9, 0x8C, 0xFF), 0.50, 0.115);

    // Owner 2026-11-06: "ขาดสาวคนไหน ไปสร้างมา" — Antigravity (agy) shared Gemini's girl; she has her own now (Grok Imagine,
    // cut out with rembg isnet-anime like the others).
    public static readonly ChatCharacter Antigravity = new("antigravity", "Antigravity", Color.FromRgb(0x2D, 0xD4, 0xBF), 0.57, 0.120);

    // Who stands in for a girl whose picture is not in the build yet: CluadeX's own girl, at her face.
    private const string StandIn = "cluadex";
    private const double StandInFaceX = 0.50, StandInFaceY = 0.115;

    // Owner 2026-11-08: "อย่าลืมเพิ่มมาสคอนสาว น้อง Colibri … ไอค่อนไหนใช้โมเดลน้อง อะไรก็มีให้แสดงทุกที่ … เอไอมาสคอตสาวตัวไหนก็
    // ทำให้ครบ" — every provider has her own girl now, in the brand's colour. Her picture is Assets\Girls\Providers\<name>.png
    // (drawn in ChatGPT from the main girls, each with her own face; face numbers measured on it). All 19 are drawn (2026-11-08);
    // a girl added later without a picture gets CluadeX's girl standing in under her name and colour.
    public static readonly ChatCharacter Colibri = new("colibri", "Colibri", Color.FromRgb(0x10, 0xB9, 0x81), 0.47, 0.110);   // emerald hummingbird
    public static readonly ChatCharacter OpenRouter = new("openrouter", "OpenRouter", Color.FromRgb(0x62, 0x65, 0xED), 0.52, 0.110);
    public static readonly ChatCharacter Groq = new("groq", "Groq", Color.FromRgb(0xF5, 0x50, 0x36), 0.545, 0.180);
    public static readonly ChatCharacter DeepSeek = new("deepseek", "DeepSeek", Color.FromRgb(0x4B, 0x68, 0xF6), 0.48, 0.120);
    public static readonly ChatCharacter Mistral = new("mistral", "Mistral", Color.FromRgb(0xFA, 0x52, 0x0F), 0.42, 0.100);
    public static readonly ChatCharacter Together = new("together", "Together", Color.FromRgb(0x0F, 0x6E, 0xFC), 0.44, 0.120);
    public static readonly ChatCharacter Fireworks = new("fireworks", "Fireworks", Color.FromRgb(0x67, 0x20, 0xFF), 0.48, 0.100);
    public static readonly ChatCharacter Cerebras = new("cerebras", "Cerebras", Color.FromRgb(0xF1, 0x5A, 0x29), 0.52, 0.090);
    public static readonly ChatCharacter DeepInfra = new("deepinfra", "DeepInfra", Color.FromRgb(0x2E, 0x86, 0xDE), 0.5, 0.115);
    public static readonly ChatCharacter HuggingFace = new("huggingface", "Hugging Face", Color.FromRgb(0xFF, 0xD2, 0x1E), 0.47, 0.115);
    public static readonly ChatCharacter Novita = new("novita", "Novita", Color.FromRgb(0x22, 0xD3, 0xEE), 0.47, 0.115);
    public static readonly ChatCharacter SambaNova = new("sambanova", "SambaNova", Color.FromRgb(0xEE, 0x76, 0x24), 0.5, 0.095);
    public static readonly ChatCharacter Nebius = new("nebius", "Nebius", Color.FromRgb(0x5B, 0x5B, 0xD6), 0.49, 0.110);
    public static readonly ChatCharacter Qwen = new("qwen", "Qwen", Color.FromRgb(0x61, 0x5C, 0xED), 0.53, 0.090);
    public static readonly ChatCharacter Kimi = new("kimi", "Kimi", Color.FromRgb(0x25, 0x63, 0xEB), 0.47, 0.090);
    public static readonly ChatCharacter Glm = new("glm", "GLM", Color.FromRgb(0x38, 0x59, 0xFF), 0.49, 0.120);
    public static readonly ChatCharacter Azure = new("azure", "Azure", Color.FromRgb(0x00, 0x78, 0xD4), 0.48, 0.105);
    public static readonly ChatCharacter LMStudio = new("lmstudio", "LM Studio", Color.FromRgb(0x4F, 0x46, 0xE5), 0.51, 0.110);
    public static readonly ChatCharacter Vllm = new("vllm", "vLLM", Color.FromRgb(0xF7, 0xB5, 0x00), 0.61, 0.150);

    /// <summary>The girls with a picture of their own — what a "pick a girl" list shows (stand-ins would repeat CluadeX's).</summary>
    public static IReadOnlyList<ChatCharacter> All => _all ??= Everyone.Where(c => c.HasOwnArt).ToList();
    private static List<ChatCharacter>? _all;

    /// <summary>Every provider's girl, stand-ins included (for a picture-sheet of who is drawn and who still waits).</summary>
    public static IReadOnlyList<ChatCharacter> Everyone { get; } = [Claude, Codex, Gemini, Grok, Mind, BrainX, CluadeX, Antigravity,
        Colibri, OpenRouter, Groq, DeepSeek, Mistral, Together, Fireworks, Cerebras, DeepInfra, HuggingFace, Novita, SambaNova,
        Nebius, Qwen, Kimi, Glm, Azure, LMStudio, Vllm];

    /// <summary>The girl who answers for a provider — the same girls AgentPersona gives the voices to.</summary>
    public static ChatCharacter ForProvider(AiProviderType provider) => provider switch
    {
        AiProviderType.Anthropic or AiProviderType.ClaudeDev => Claude,
        AiProviderType.OpenAI or AiProviderType.OpenAIDev => Codex,
        AiProviderType.Gemini or AiProviderType.GeminiDev => Gemini,
        AiProviderType.AntigravityDev => Antigravity,
        AiProviderType.XAI or AiProviderType.GrokDev => Grok,
        AiProviderType.Ollama => Mind,   // Ollama = มายด์
        // Owner 2026-11-06: "น้อง มายด์ เปลี่ยนเป็น cluadex สาวม่วง สำหรับ dev gguf" — a GGUF on this PC is CluadeX's own girl
        AiProviderType.Local or AiProviderType.LlamaServer => CluadeX,
        AiProviderType.BrainX => BrainX,   // the room itself, until someone in it answers
        AiProviderType.Colibri => Colibri,
        AiProviderType.OpenRouter => OpenRouter,
        AiProviderType.Groq => Groq,
        AiProviderType.DeepSeek => DeepSeek,
        AiProviderType.Mistral => Mistral,
        AiProviderType.Together => Together,
        AiProviderType.Fireworks => Fireworks,
        AiProviderType.Cerebras => Cerebras,
        AiProviderType.DeepInfra => DeepInfra,
        AiProviderType.HuggingFace => HuggingFace,
        AiProviderType.Novita => Novita,
        AiProviderType.SambaNova => SambaNova,
        AiProviderType.Nebius => Nebius,
        AiProviderType.Qwen => Qwen,
        AiProviderType.Moonshot => Kimi,
        AiProviderType.Zhipu => Glm,
        AiProviderType.AzureOpenAI => Azure,
        AiProviderType.LMStudio => LMStudio,
        AiProviderType.Vllm => Vllm,
        _ => CluadeX,   // OpenAICompatible: a custom endpoint is CluadeX's own
    };

    /// <summary>A girl by her picture's name (null/unknown = CluadeX's own girl).</summary>
    public static ChatCharacter ForGirl(string? girl) =>
        All.FirstOrDefault(c => string.Equals(c.Girl, girl, StringComparison.OrdinalIgnoreCase))
        ?? Everyone.FirstOrDefault(c => string.Equals(c.Own, girl, StringComparison.OrdinalIgnoreCase)) ?? CluadeX;

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
                _roomStrangers[a] = c = new ChatCharacter(BrainX.Girl, agent!.Trim(), BrainX.Accent, BrainX._faceX, BrainX._faceY);
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
