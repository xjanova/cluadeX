using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Pet;

/// <summary>
/// A pet the AI invented: what it is, why it fits its owner, its colours and its body drawn as a 24×24 grid of the
/// same palette letters the built-in species use. The app adds the outline, the face (any feeling), the feet and the
/// poses, so an AI-made pet walks, sits, sleeps and emotes exactly like the built-in ones.
/// </summary>
public sealed class PetDesign
{
    public string Name { get; set; } = "";
    public string Species { get; set; } = "";
    public string SpeciesTh { get; set; } = "";
    public string Personality { get; set; } = "";
    public string PersonalityText { get; set; } = "";
    public string Reason { get; set; } = "";
    public Dictionary<string, string> Palette { get; set; } = new();
    public string[] Body { get; set; } = [];
    public bool Feet { get; set; } = true;
    public bool Floats { get; set; }
    public int EyeX { get; set; } = 7;
    public int EyeY { get; set; } = 12;
    public int MouthY { get; set; } = 15;
    public bool Blush { get; set; } = true;
    public List<string> CatchphrasesTh { get; set; } = new();
    public List<string> CatchphrasesEn { get; set; } = new();
    /// <summary>Which AI designed it ("Anthropic · claude-opus-5-5").</summary>
    public string DesignedBy { get; set; } = "";
}

/// <summary>Builds the request to the owner's AI and checks what comes back.</summary>
public static class PetDesigner
{
    public const string Letters = ".bshacwkpyrng";
    static readonly string[] Inspirations =
    [
        "moon", "tea", "thunder", "rain", "lantern", "pudding", "comet", "moss", "ink", "bell", "cloud", "ember", "pearl",
        "origami", "mango", "fog", "neon", "crystal", "bamboo", "honey", "tide", "snow", "pixel", "jade", "storm", "candle",
        "galaxy", "coffee", "mochi", "maple", "lotus", "coral", "dusk", "dawn", "spark", "shadow", "petal", "rocket",
    ];

    /// <summary>The system prompt: how to draw, and the answer format.</summary>
    public const string SystemPrompt = """
        You are this person's AI assistant, and you have been working with them for many hours. Now you get to give
        them a gift: a little pixel-art pet that will live on their desktop as their friend and companion.
        From everything you have seen of them while working together — how they work, how they talk, what they care
        about, their moods — decide what pet you would give them: what it is, what it looks like, what it is like,
        and why it is the right friend for them. It can be ANY creature or being: an animal, a mythical beast, a
        spirit, a small person, a living object, a plant, a food, a robot. Don't default to a cat or a dog; make it
        personal and a little surprising, but always cute and kind.

        Draw it as pixel art on a 24×24 grid, as 24 strings of exactly 24 characters, using ONLY these letters:
          .  empty            b  main body colour      s  body shade (lower/right side)   h  highlight (upper-left)
          a  accent (belly, inner ears, cheeks of fur)  c  second colour (hair, clothes, wings, shell, leaves)
          w  white   k  dark details (stripes, spots — NOT the eyes)   p  pink   y  yellow/gold   r  red
          n  skin    g  translucent (wings, glow)
        Rules:
        - Do NOT draw an outline — the app outlines every shape itself.
        - Do NOT draw eyes or a mouth — the app draws the face at "eyeX","eyeY","mouthY" so the pet can show feelings.
          Keep that face area plain body colour: the left eye is a 3×2 box at (eyeX, eyeY), the right eye its mirror at
          (21 - eyeX, eyeY), the mouth a 4×2 box at (10, mouthY). Typical: eyeX 7, eyeY 10–13, mouthY eyeY+3.
        - Centre it on the middle of the grid (between columns 11 and 12); about 14–18 wide and 14–20 tall; chunky,
          round, readable shapes — this is shown at 4× size.
        - If it walks ("feet": true) leave rows 20–23 empty: the app adds two little feet at rows 20–21. If it floats
          or slides ("floats": true) it may use down to row 21.
        Answer with ONE JSON object and nothing else:
        {
          "species": "short English name of what it is", "speciesTh": "the same in Thai",
          "name": "its name, in the language the owner writes in most",
          "personality": one of "Cheerful","Grumpy","Sassy","Shy","Sleepy","Hyper","Caring","Nerdy",
          "personalityText": "one sentence on its temperament, in the owner's language",
          "reason": "two sentences: why this creature fits this person, in the owner's language",
          "palette": { "b": "#RRGGBB", "s": "#RRGGBB", "h": "#RRGGBB", "a": "#RRGGBB", "c": "#RRGGBB" },
          "feet": true, "floats": false, "eyeX": 7, "eyeY": 12, "mouthY": 15, "blush": true,
          "body": [ 24 strings of 24 characters ],
          "catchphrasesTh": [ 4–6 short things it says now and then, in Thai, in its own voice ],
          "catchphrasesEn": [ the same 4–6 in English ]
        }
        """;

    /// <summary>The user message: the owner's profile (computed by PetBrain's formula), how they write, a few of
    /// their own messages, and a random inspiration so nobody can predict the result.</summary>
    public static string BuildRequest(PetState s, Random rng)
    {
        var h = s.Habits;
        var t = PetBrain.TraitsOf(h);
        var tone = PetBrain.ToneOf(h);
        static string P(double v) => $"{Math.Round(v * 100)}";
        var sb = new StringBuilder();
        sb.AppendLine($"You have worked with this person for {h.ActiveMinutes / 60.0:F0} hours of real use, over {h.ActiveDays.Count} days.");
        sb.AppendLine("From having worked with them: what pixel pet would you give them, to be their friend? What is it, what is it like, and why?");
        sb.AppendLine();
        sb.AppendLine("WHAT YOU HAVE SEEN OF THEM (measured; 0–100 unless noted)");
        sb.AppendLine($"- used for {h.ActiveMinutes / 60.0:F1} hours over {h.ActiveDays.Count} days; {h.Messages} messages; {h.ToolActions} tool actions");
        sb.AppendLine($"- works late at night: {P(t.Night)} · early morning: {P(t.Morning)} · weekends: {P(t.Weekend)}");
        sb.AppendLine($"- edits code: {P(t.Editor)} · builds/tests: {P(t.Builder)} · explores/searches: {P(t.Explorer)} · git: {P(t.Gitty)}");
        sb.AppendLine($"- struggles (failed builds/tests/errors): {P(t.Struggle)} · pace: {P(t.Busy)} · talks vs acts: {P(t.Chatty)}");
        sb.AppendLine($"- writes Thai: {P(t.Thai)} · long messages: {P(t.Wordy)} · runs AI on own PC: {P(t.Local)}");
        sb.AppendLine($"- tone: questions {P(tone.Curious)} · exclamations {P(tone.Intense)} · polite words {P(tone.Polite)} · laughs/emoji {P(tone.Playful)} · urgent {P(tone.Urgent)}");
        var hours = h.Hours.Select((m, hr) => (m, hr)).OrderByDescending(x => x.m).Take(3).Where(x => x.m > 0).Select(x => $"{x.hr}:00");
        sb.AppendLine($"- busiest hours: {string.Join(", ", hours)}");
        if (h.Samples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("A FEW OF THEIR OWN MESSAGES (to sense their character — do not quote them back):");
            foreach (var m in h.Samples.OrderBy(_ => rng.Next()).Take(40)) sb.AppendLine("- " + m);
        }
        sb.AppendLine();
        var words = Enumerable.Range(0, 3).Select(_ => Inspirations[rng.Next(Inspirations.Length)]).Distinct();
        sb.AppendLine($"RANDOM INSPIRATION (use it loosely, or as a twist): {string.Join(", ", words)} · roll {rng.Next(1000, 9999)}");
        sb.AppendLine($"The owner writes mostly in {(t.Thai >= 0.5 ? "Thai" : "English")}.");
        return sb.ToString();
    }

    /// <summary>The fallback request when the drawing came back unusable twice: pick one of the built-in bodies.</summary>
    public static string BuildTemplateRequest(string profile) =>
        profile + "\nDrawing is not needed this time. Answer with ONE JSON object: {\"template\": one of " +
        string.Join(",", Enum.GetNames<PetSpecies>().Select(n => $"\"{n}\"")) +
        ", \"hue\": 0-359, \"accentHue\": 0-359, \"species\": \"...\", \"speciesTh\": \"...\", \"name\": \"...\", \"personality\": one of \"Cheerful\",\"Grumpy\",\"Sassy\",\"Shy\",\"Sleepy\",\"Hyper\",\"Caring\",\"Nerdy\", \"personalityText\": \"...\", \"reason\": \"...\", \"catchphrasesTh\": [...], \"catchphrasesEn\": [...]}";

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The first {...} object in the reply (models wrap it in prose or ``` fences).</summary>
    public static string? ExtractJson(string reply)
    {
        int start = reply.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inString = false, escape = false;
        for (int i = start; i < reply.Length; i++)
        {
            char c = reply[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return reply[start..(i + 1)];
        }
        return null;
    }

    sealed class Raw
    {
        public string? Species { get; set; }
        public string? SpeciesTh { get; set; }
        public string? Name { get; set; }
        public string? Personality { get; set; }
        public string? PersonalityText { get; set; }
        public string? Reason { get; set; }
        public Dictionary<string, string>? Palette { get; set; }
        public bool? Feet { get; set; }
        public bool? Floats { get; set; }
        public int? EyeX { get; set; }
        public int? EyeY { get; set; }
        public int? MouthY { get; set; }
        public bool? Blush { get; set; }
        public List<string>? Body { get; set; }
        public List<string>? CatchphrasesTh { get; set; }
        public List<string>? CatchphrasesEn { get; set; }
        public string? Template { get; set; }
        public double? Hue { get; set; }
        public double? AccentHue { get; set; }
    }

    static readonly Regex Hex = new("^#?[0-9a-fA-F]{6}$");

    static List<string> Phrases(List<string>? list) =>
        (list ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length is > 0 and <= 80).Take(8).ToList();

    static void Common(Raw r, PetDesign d)
    {
        d.Species = (r.Species ?? "").Trim();
        d.SpeciesTh = (r.SpeciesTh ?? "").Trim();
        d.Name = (r.Name ?? "").Trim();
        d.Personality = (r.Personality ?? "").Trim();
        d.PersonalityText = (r.PersonalityText ?? "").Trim();
        d.Reason = (r.Reason ?? "").Trim();
        d.CatchphrasesTh = Phrases(r.CatchphrasesTh);
        d.CatchphrasesEn = Phrases(r.CatchphrasesEn);
        if (d.Name.Length > 24) d.Name = d.Name[..24];
        if (d.Species.Length > 40) d.Species = d.Species[..40];
        if (d.SpeciesTh.Length > 40) d.SpeciesTh = d.SpeciesTh[..40];
    }

    /// <summary>Read a drawn design. Lenient where it costs nothing (rows padded or cut to 24, unknown letters
    /// emptied, face moved onto the body), strict where the pet would look broken (too small, blank, no name).</summary>
    public static (PetDesign? design, string? error) ParseDesign(string reply)
    {
        string? json = ExtractJson(reply);
        if (json == null) return (null, "no JSON object in the answer");
        Raw? r;
        try { r = JsonSerializer.Deserialize<Raw>(json, Json); }
        catch (JsonException ex) { return (null, "the JSON did not parse: " + ex.Message); }
        if (r == null) return (null, "empty answer");
        if (r.Body == null || r.Body.Count < 12) return (null, "\"body\" must be 24 strings of 24 characters");

        var d = new PetDesign();
        Common(r, d);
        if (d.Name.Length == 0) return (null, "\"name\" is missing");
        if (d.Species.Length == 0 && d.SpeciesTh.Length == 0) return (null, "\"species\" is missing");

        d.Feet = r.Feet ?? true;
        d.Floats = r.Floats ?? !d.Feet;
        if (d.Floats) d.Feet = false;
        var rows = new string[PetArt.Size];
        for (int y = 0; y < PetArt.Size; y++)
        {
            string row = y < r.Body.Count ? (r.Body[y] ?? "") : "";
            var chars = new char[PetArt.Size];
            for (int x = 0; x < PetArt.Size; x++)
            {
                char c = x < row.Length ? row[x] : '.';
                if (c == ' ') c = '.';
                c = char.ToLowerInvariant(c);
                if (c == 'o' || c == 'e' || Letters.IndexOf(c) < 0) c = c == 'o' || c == 'e' ? 'b' : '.';
                // The app's feet go at rows 20–21: a walking body keeps rows 20–23 clear.
                if (d.Feet && y >= 20) c = '.';
                chars[x] = c;
            }
            rows[y] = new string(chars);
        }
        d.Body = rows;

        int filled = rows.Sum(row => row.Count(ch => ch != '.'));
        if (filled < 60) return (null, $"the drawing is too small ({filled} filled pixels; draw it about 14–18 wide and 14–20 tall)");
        if (filled > 470) return (null, $"the drawing fills almost the whole grid ({filled} pixels); leave empty space around the creature");
        int minX = PetArt.Size, maxX = -1, minY = PetArt.Size, maxY = -1;
        for (int y = 0; y < PetArt.Size; y++)
            for (int x = 0; x < PetArt.Size; x++)
                if (rows[y][x] != '.') { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX - minX < 7 || maxY - minY < 7) return (null, "the drawing is too thin; make it at least 8×8");

        foreach (var (key, value) in r.Palette ?? new())
        {
            if (key.Length != 1 || Letters.IndexOf(char.ToLowerInvariant(key[0])) <= 0 || value == null || !Hex.IsMatch(value.Trim())) continue;
            d.Palette[key.ToLowerInvariant()] = "#" + value.Trim().TrimStart('#').ToUpperInvariant();
        }
        if (!d.Palette.ContainsKey("b")) return (null, "\"palette\" needs at least \"b\" (the body colour)");

        // The face: where the model put it, nudged onto the body if it landed off it.
        d.EyeX = Math.Clamp(r.EyeX ?? 7, 2, 10);
        d.EyeY = Math.Clamp(r.EyeY ?? (minY + (maxY - minY) * 4 / 10), 1, 19);
        d.MouthY = Math.Clamp(r.MouthY ?? d.EyeY + 3, d.EyeY + 2, 21);
        if (!FaceOnBody(rows, d.EyeX, d.EyeY))
        {
            int ey = Enumerable.Range(minY, maxY - minY + 1).FirstOrDefault(y => FaceOnBody(rows, d.EyeX, y) && y >= minY + 2, -1);
            if (ey < 0) return (null, "the face area (eyeX, eyeY) is not on the body; keep it on plain body colour");
            d.MouthY += ey - d.EyeY;
            d.EyeY = ey;
            d.MouthY = Math.Clamp(d.MouthY, d.EyeY + 2, 21);
        }
        d.Blush = r.Blush ?? true;
        return (d, null);
    }

    static bool FaceOnBody(string[] rows, int eyeX, int eyeY)
    {
        int on = 0;
        for (int y = eyeY; y < eyeY + 2 && y < PetArt.Size; y++)
            for (int x = eyeX; x < eyeX + 3; x++)
                if (rows[y][x] != '.') on++;
        return on >= 4;
    }

    /// <summary>Read the fallback answer: one of the built-in bodies, in the AI's colours, with its name and voice.</summary>
    public static (PetSpecies template, double hue, double accentHue, PetDesign words)? ParseTemplate(string reply)
    {
        string? json = ExtractJson(reply);
        if (json == null) return null;
        try
        {
            var r = JsonSerializer.Deserialize<Raw>(json, Json);
            if (r == null || !Enum.TryParse<PetSpecies>(r.Template ?? "", true, out var sp)) return null;
            var words = new PetDesign();
            Common(r, words);
            if (words.Name.Length == 0) return null;
            return (sp, ((r.Hue ?? 0) % 360 + 360) % 360, ((r.AccentHue ?? 180) % 360 + 360) % 360, words);
        }
        catch (JsonException) { return null; }
    }
}
