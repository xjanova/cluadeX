using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Pet;

public enum PetPersonality { Cheerful, Grumpy, Sassy, Shy, Sleepy, Hyper, Caring, Nerdy }

/// <summary>What the pet has learned about how its owner works. Counted from real use only (and, once, from the
/// chat history already on disk); kept on this PC in pet.json, never sent anywhere.</summary>
public sealed class PetHabits
{
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Local dates (yyyy-MM-dd) CluadeX was actually used on.</summary>
    public List<string> ActiveDays { get; set; } = new();
    public int ActiveMinutes { get; set; }
    /// <summary>Active minutes by local hour of day.</summary>
    public int[] Hours { get; set; } = new int[24];
    public int WeekendMinutes { get; set; }
    public int Messages { get; set; }
    public int ThaiMessages { get; set; }
    public int LongMessages { get; set; }
    public int Edits { get; set; }
    public int Builds { get; set; }
    public int BuildFailures { get; set; }
    public int Tests { get; set; }
    public int TestFailures { get; set; }
    public int Git { get; set; }
    public int Searches { get; set; }
    public int Browser { get; set; }
    public int Shell { get; set; }
    public int Brain { get; set; }
    public int OtherTools { get; set; }
    public int ToolErrors { get; set; }
    public int LocalTurns { get; set; }
    public int ApiTurns { get; set; }
    /// <summary>Turns per provider ("Anthropic", "Local" …): the AI the owner uses most designs the pet.</summary>
    public Dictionary<string, int> ProviderTurns { get; set; } = new();

    // How they write (counted per message).
    public int Questions { get; set; }
    public int Exclaims { get; set; }
    public int Polite { get; set; }
    public int Playful { get; set; }
    public int Urgent { get; set; }
    /// <summary>A spread-out sample of their own messages (secrets removed, ≤ 200 characters each), kept so the AI
    /// that designs the pet can sense their character.</summary>
    public List<string> Samples { get; set; } = new();
    public int SamplesSeen { get; set; }
    /// <summary>The chat history on disk was read once to give the egg a head start.</summary>
    public bool Bootstrapped { get; set; }

    [JsonIgnore] public int ToolActions => Edits + Builds + Tests + Git + Searches + Browser + Shell + Brain + OtherTools;
    [JsonIgnore] public int Observations => Messages + ToolActions;
}

/// <summary>Everything about one pet, as kept in pet.json.</summary>
public sealed class PetState
{
    public int Version { get; set; } = 1;
    /// <summary>Random per install (not derived from the machine name): two people never share an egg.</summary>
    public long Seed { get; set; }
    public double EggHue { get; set; }
    public int EggSpots { get; set; }
    public PetHabits Habits { get; set; } = new();

    public bool Hatched { get; set; }
    public DateTime? HatchedAtUtc { get; set; }
    public PetSpecies Species { get; set; }
    public PetPersonality Personality { get; set; }
    public double Hue { get; set; }
    public double AccentHue { get; set; }
    public bool Shiny { get; set; }
    public string Name { get; set; } = "";
    /// <summary>0..100: rises with pats and time together, falls with too much poking.</summary>
    public int Affection { get; set; } = 30;
    public int Pokes { get; set; }
    /// <summary>Out on the desktop (the user can put it away and call it back).</summary>
    public bool OnDesktop { get; set; } = true;

    /// <summary>What the owner's AI made it (null: a built-in body, or not hatched yet).</summary>
    public PetDesign? Design { get; set; }
    /// <summary>The AI's own words when it chose a built-in body (name, reasons, catchphrases).</summary>
    public PetDesign? Words { get; set; }
    public int HatchAttempts { get; set; }
    public DateTime? LastHatchAttemptUtc { get; set; }

    [JsonIgnore] public PetLook Look => new(Species, Hue, AccentHue, Shiny, Design);
    /// <summary>The AI's words about this pet — the design itself, or the words that came with a built-in body.</summary>
    [JsonIgnore] public PetDesign? About => Design ?? Words;
}

/// <summary>The pet's decisions, kept free of UI so the harness can drive them.</summary>
public static class PetBrain
{
    // No hurry: an egg hatches only once it really knows its owner — 24 hours of actual use (minutes spent working
    // and chatting, not hours on the clock), spread over at least 3 days, and 100 of their own messages to read
    // their character from.
    public const int HatchDays = 3;
    public const int HatchMinutes = 24 * 60;
    public const int HatchMessages = 100;
    public const int MaxSamples = 80;

    public static PetState NewEgg(Random rng) => new()
    {
        Seed = rng.NextInt64(1, long.MaxValue),
        EggHue = rng.Next(360),
        EggSpots = rng.Next(1, int.MaxValue),
    };

    /// <summary>0..1, the mean of the three parts (each capped at 1).</summary>
    public static double HatchProgress(PetHabits h) =>
        (Math.Min(1.0, h.ActiveDays.Count / (double)HatchDays)
         + Math.Min(1.0, h.ActiveMinutes / (double)HatchMinutes)
         + Math.Min(1.0, h.Messages / (double)HatchMessages)) / 3.0;

    public static bool ReadyToHatch(PetHabits h) =>
        h.ActiveDays.Count >= HatchDays && h.ActiveMinutes >= HatchMinutes && h.Messages >= HatchMessages;

    /// <summary>How they write, 0..1 per message.</summary>
    public sealed record Tone(double Curious, double Intense, double Polite, double Playful, double Urgent);

    public static Tone ToneOf(PetHabits h)
    {
        double n = Math.Max(1, h.Messages);
        return new Tone(Math.Min(1, h.Questions / n), Math.Min(1, h.Exclaims / n), Math.Min(1, h.Polite / n),
                        Math.Min(1, h.Playful / n), Math.Min(1, h.Urgent / n));
    }

    /// <summary>The AI the owner uses most (a provider key such as "Anthropic"), or null before any turn.</summary>
    public static string? MostUsedProvider(PetHabits h) =>
        h.ProviderTurns.Count == 0 ? null : h.ProviderTurns.OrderByDescending(kv => kv.Value).First().Key;

    /// <summary>The habits as 0..1 traits.</summary>
    public sealed record Traits(double Night, double Morning, double Weekend, double Builder, double Struggle, double Editor,
        double Explorer, double Gitty, double Chatty, double Thai, double Local, double Wordy, double Busy);

    public static Traits TraitsOf(PetHabits h)
    {
        static double R(double a, double b) => b <= 0 ? 0 : Math.Clamp(a / b, 0, 1);
        int minutes = Math.Max(1, h.Hours.Sum());
        int night = h.Hours[22] + h.Hours[23] + h.Hours[0] + h.Hours[1] + h.Hours[2] + h.Hours[3] + h.Hours[4];
        int morning = h.Hours[5] + h.Hours[6] + h.Hours[7] + h.Hours[8] + h.Hours[9];
        int tools = Math.Max(1, h.ToolActions);
        return new Traits(
            Night: R(night, minutes),
            Morning: R(morning, minutes),
            Weekend: R(h.WeekendMinutes, Math.Max(1, h.ActiveMinutes)),
            Builder: R(h.Builds + h.Tests, tools),
            Struggle: R(h.BuildFailures + h.TestFailures + h.ToolErrors, Math.Max(1, h.Builds + h.Tests + h.ToolActions / 4)),
            Editor: R(h.Edits, tools),
            Explorer: R(h.Searches + h.Browser, tools),
            Gitty: R(h.Git, tools),
            Chatty: R(h.Messages, h.Messages + tools),
            Thai: R(h.ThaiMessages, Math.Max(1, h.Messages)),
            Local: R(h.LocalTurns, h.LocalTurns + h.ApiTurns),
            Wordy: R(h.LongMessages, Math.Max(1, h.Messages)),
            Busy: Math.Clamp(h.Observations / (h.ActiveMinutes / 60.0 + 1) / 60.0, 0, 1));
    }

    /// <summary>How strongly each species fits these habits. Every species keeps a base chance: habits tilt the
    /// odds, they don't decide alone — so nobody can predict exactly what will hatch.</summary>
    public static Dictionary<PetSpecies, double> SpeciesAffinity(Traits t) => new()
    {
        [PetSpecies.Owl] = 1 + 4 * t.Night,
        [PetSpecies.Ghost] = 1 + 3 * t.Night + 2 * t.Struggle,
        [PetSpecies.Cat] = 1 + 2 * t.Explorer + 1.5 * t.Night,
        [PetSpecies.Robot] = 1 + 3 * t.Builder + 2 * t.Gitty,
        [PetSpecies.Dragon] = 1 + 3 * t.Editor + 2 * t.Struggle,
        [PetSpecies.Fox] = 1 + 2 * t.Editor + 2 * t.Chatty,
        [PetSpecies.Bunny] = 1 + 3 * t.Morning + 1.5 * t.Chatty,
        [PetSpecies.Slime] = 1 + 3 * t.Local + t.Struggle,
        [PetSpecies.Fairy] = 1 + 2 * t.Thai + 2 * t.Builder * (1 - t.Struggle),
        [PetSpecies.Girl] = 1 + 2.5 * t.Thai + 1.5 * t.Chatty,
        [PetSpecies.Penguin] = 1 + 3 * t.Weekend + t.Builder,
        [PetSpecies.Frog] = 1 + 2 * t.Explorer + t.Wordy,
    };

    public static Dictionary<PetPersonality, double> PersonalityAffinity(Traits t) => new()
    {
        [PetPersonality.Sleepy] = 1 + 4 * t.Night,
        [PetPersonality.Grumpy] = 1 + 4 * t.Struggle,
        [PetPersonality.Sassy] = 1 + 2 * t.Struggle + 2 * t.Chatty,
        [PetPersonality.Shy] = 1 + 2 * (1 - t.Chatty) + t.Explorer,
        [PetPersonality.Hyper] = 1 + 3 * t.Busy,
        [PetPersonality.Caring] = 1 + 2 * t.Thai + 2 * t.Wordy,
        [PetPersonality.Cheerful] = 1.5 + 2 * t.Morning,
        [PetPersonality.Nerdy] = 1 + 3 * t.Builder + 2 * t.Gitty,
    };

    /// <summary>Hue ranges that read as the species (an orange fox, a green frog); anything else is free.</summary>
    static (double from, double to) HueRange(PetSpecies s) => s switch
    {
        PetSpecies.Fox => (14, 38),
        PetSpecies.Frog => (80, 150),
        PetSpecies.Penguin => (200, 300),
        PetSpecies.Robot => (190, 230),
        _ => (0, 360),
    };

    static readonly string[] NamesTh =
        ["โมจิ", "ถั่วแดง", "ข้าวตัง", "ปุยฝ้าย", "มะขาม", "ขนมปัง", "บัตเตอร์", "ซาลาเปา", "ทองหยอด", "ลูกชิ้น", "เต้าหู้",
         "ไข่มุก", "ข้าวปั้น", "มะม่วง", "น้ำแข็งใส", "กะทิ", "ส้มจี๊ด", "ขนมครก", "ลำไย", "ปลาทู"];
    static readonly string[] NamesEn =
        ["Mochi", "Bean", "Pudding", "Biscuit", "Pixel", "Nugget", "Sprout", "Pebble", "Waffle", "Tofu", "Noodle", "Cosmo",
         "Maple", "Bubbles", "Sushi", "Clover", "Dumpling", "Pickle", "Toast", "Momo"];

    static T Weighted<T>(Dictionary<T, double> weights, Random rng) where T : notnull
    {
        // Squared, so a strong habit counts for more than a slight one.
        var items = weights.Select(kv => (kv.Key, W: kv.Value * kv.Value)).ToList();
        double roll = rng.NextDouble() * items.Sum(i => i.W);
        foreach (var (key, w) in items)
        {
            if (roll < w) return key;
            roll -= w;
        }
        return items[^1].Key;
    }

    /// <summary>Decide what the egg becomes. The same seed and habits always give the same pet; different habits
    /// or another person's seed give another.</summary>
    public static void Hatch(PetState s, bool thai, DateTime nowUtc)
    {
        var t = TraitsOf(s.Habits);
        var rng = new Random((int)(s.Seed ^ (s.Seed >> 32)) ^ s.Habits.Observations);
        s.Species = Weighted(SpeciesAffinity(t), rng);
        s.Personality = Weighted(PersonalityAffinity(t), rng);
        var (from, to) = HueRange(s.Species);
        s.Hue = from + rng.NextDouble() * (to - from);
        s.AccentHue = rng.Next(360);
        s.Shiny = rng.Next(64) == 0;
        var names = thai ? NamesTh : NamesEn;
        s.Name = names[rng.Next(names.Length)];
        s.Hatched = true;
        s.HatchedAtUtc = nowUtc;
    }

    /// <summary>Which tool family a tool call belongs to (for the habits).</summary>
    public static string ToolFamily(string tool)
    {
        string t = tool.ToLowerInvariant();
        int i = t.LastIndexOf("__", StringComparison.Ordinal);
        string leaf = i >= 0 ? t[(i + 2)..] : t;
        if (leaf.Contains("brain")) return "brain";
        if (leaf is "write_file" or "edit_file" or "multi_edit" or "apply_patch" or "notebook_edit" or "create_directory") return "edit";
        if (leaf is "run_build" or "build" or "dotnet_build") return "build";
        if (leaf is "run_tests" or "run_test" or "test") return "test";
        if (leaf.StartsWith("git_") || leaf.StartsWith("gh_")) return "git";
        if (leaf is "grep_search" or "glob" or "search_files" or "search_content" or "codebase_search" or "find_symbol" or "list_symbols" or "read_file" or "list_files") return "search";
        if (leaf.StartsWith("browser_") || leaf is "web_fetch" or "web_search") return "browser";
        if (leaf is "run_command" or "powershell" or "bash" or "execute_code" || leaf.StartsWith("bg_")) return "shell";
        return "other";
    }

    /// <summary>Count one tool call into the habits.</summary>
    public static void RecordTool(PetHabits h, string tool, bool success)
    {
        switch (ToolFamily(tool))
        {
            case "edit": h.Edits++; break;
            case "build": h.Builds++; if (!success) h.BuildFailures++; break;
            case "test": h.Tests++; if (!success) h.TestFailures++; break;
            case "git": h.Git++; break;
            case "search": h.Searches++; break;
            case "browser": h.Browser++; break;
            case "shell": h.Shell++; break;
            case "brain": h.Brain++; break;
            default: h.OtherTools++; break;
        }
        if (!success) h.ToolErrors++;
    }

    public static bool LooksThai(string text) => text.Any(c => c >= '฀' && c <= '๿');

    static readonly Regex PoliteWords = new(@"ครับ|ค่ะ|คะ|นะคะ|ขอบคุณ|รบกวน|\bplease\b|\bthanks?\b|\bthank you\b", RegexOptions.IgnoreCase);
    static readonly Regex PlayfulWords = new(@"555|ฮ่า|5{3,}|\blol\b|\bhaha|😂|🤣|😆|😁|😊|❤|🥰|✨", RegexOptions.IgnoreCase);
    static readonly Regex UrgentWords = new(@"ด่วน|รีบ|เร็วๆ|เร็ว ๆ|\basap\b|\burgent\b|!!!", RegexOptions.IgnoreCase);
    static readonly Regex Secrets = new(
        @"(sk-[A-Za-z0-9_\-]{10,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-.]+|[A-Za-z0-9+/_\-]{32,}={0,2}|[\w.+\-]+@[\w\-]+\.[\w.\-]+|https?://\S+)");

    /// <summary>A message as a sample: no secrets (keys, tokens, e-mails, links), no pasted code, ≤ 200 characters.
    /// Null when there is nothing personal left to read.</summary>
    public static string? SampleOf(string text)
    {
        string t = text.Trim();
        int fence = t.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) t = t[..fence];
        var firstLines = t.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3);
        t = Secrets.Replace(string.Join(" / ", firstLines), "[…]");
        if (t.Length > 200) t = t[..200] + "…";
        return t.Count(char.IsLetter) < 4 ? null : t;
    }

    public static void RecordMessage(PetHabits h, string text, Random? rng = null)
    {
        h.Messages++;
        if (LooksThai(text)) h.ThaiMessages++;
        if (text.Length > 200) h.LongMessages++;
        if (text.Contains('?')) h.Questions++;
        if (text.Contains('!')) h.Exclaims++;
        if (PoliteWords.IsMatch(text)) h.Polite++;
        if (PlayfulWords.IsMatch(text)) h.Playful++;
        if (UrgentWords.IsMatch(text)) h.Urgent++;
        // Reservoir sample: every message ever sent has the same chance to be among the kept ones, so the sample
        // covers all the days, not just the first or the last.
        if (SampleOf(text) is not { } sample) return;
        h.SamplesSeen++;
        if (h.Samples.Count < MaxSamples) h.Samples.Add(sample);
        else
        {
            int j = (rng ?? Random.Shared).Next(h.SamplesSeen);
            if (j < MaxSamples) h.Samples[j] = sample;
        }
    }

    /// <summary>One minute of real use at <paramref name="localNow"/>.</summary>
    public static void RecordMinute(PetHabits h, DateTime localNow)
    {
        h.ActiveMinutes++;
        h.Hours[localNow.Hour]++;
        if (localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) h.WeekendMinutes++;
        string day = localNow.ToString("yyyy-MM-dd");
        if (!h.ActiveDays.Contains(day)) h.ActiveDays.Add(day);
    }

    /// <summary>A head start from chat history already on disk: the user's own message times and texts.
    /// Minutes are counted as distinct 10-minute slots with a message in them.</summary>
    public static void Bootstrap(PetHabits h, IEnumerable<(DateTime local, string text)> userMessages)
    {
        if (h.Bootstrapped) return;
        var slots = new HashSet<string>();
        foreach (var (local, text) in userMessages)
        {
            RecordMessage(h, text);
            string day = local.ToString("yyyy-MM-dd");
            if (!h.ActiveDays.Contains(day)) h.ActiveDays.Add(day);
            if (slots.Add(local.ToString("yyyy-MM-dd HH:") + local.Minute / 10))
            {
                h.ActiveMinutes += 10;
                h.Hours[local.Hour] += 10;
                if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) h.WeekendMinutes += 10;
            }
        }
        h.Bootstrapped = true;
    }
}
