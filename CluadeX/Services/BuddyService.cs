using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Deterministic companion generation from userId hash.
/// Bones are regenerated each time (never stored).
/// Soul (name/personality) is stored in buddy.json.
/// </summary>
public class BuddyService
{
    private const string SALT = "cluadex-buddy-2026";
    private static readonly string[] Eyes = ["·", "✦", "×", "◉", "@", "°"];
    private static readonly (BuddyRarity rarity, int weight)[] RarityWeights =
    [
        (BuddyRarity.Common, 60),
        (BuddyRarity.Uncommon, 25),
        (BuddyRarity.Rare, 10),
        (BuddyRarity.Epic, 4),
        (BuddyRarity.Legendary, 1),
    ];

    private static readonly string[][] IdleQuips =
    [
        ["*yawns*", "*stretches*", "*blinks slowly*", "zzZ...", "*nods approvingly*"],
        ["Looking good!", "Keep going!", "You can do it!", "Nice code!", "*happy wiggle*"],
        ["*curious head tilt*", "Hmm...", "*watches intently*", "Interesting...", "*takes notes*"],
        ["*bounces excitedly*", "Ooh!", "*sparkles*", "That's cool!", "*tail wag*"],
        ["*dramatic gasp*", "Wait what?!", "*falls asleep*", "*sneezes*", "*spins around*"],
    ];

    private static readonly string[] PetResponses =
    [
        "❤️ *purrs happily*", "❤️ *nuzzles you*", "❤️ So warm!", "❤️ *wiggles*",
        "❤️ Best friend!", "❤️ *sparkle eyes*", "❤️ Again! Again!", "❤️ *happy dance*",
    ];

    private readonly SettingsService _settingsService;
    private readonly string _buddyPath;
    private readonly DispatcherTimer _quipTimer;
    private readonly Random _displayRng = new();

    private Buddy? _buddy;

    public Buddy? CurrentBuddy => _buddy;
    public event Action? BuddyChanged;

    public BuddyService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _buddyPath = Path.Combine(settingsService.DataRoot, "buddy.json");

        // Random quips every 30-90 seconds
        _quipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _quipTimer.Tick += (_, _) => ShowRandomQuip();
    }

    /// <summary>Initialize or load the buddy companion.</summary>
    public void Initialize()
    {
        string userId = GetUserId();
        var bones = GenerateBones(userId);
        var soul = LoadSoul();

        if (soul == null)
        {
            // First hatch — generate a name based on species
            soul = new BuddySoul
            {
                Name = GenerateName(bones.Species, bones.Rarity),
                Personality = GeneratePersonality(bones),
                HatchedAt = DateTime.Now,
            };
            SaveSoul(soul);
        }

        _buddy = new Buddy { Bones = bones, Soul = soul };
        _quipTimer.Start();
        BuddyChanged?.Invoke();
    }

    /// <summary>Pet the buddy — shows hearts and happy response.</summary>
    public void Pet()
    {
        if (_buddy == null) return;
        _buddy.IsPetting = true;
        _buddy.SpeechBubble = PetResponses[_displayRng.Next(PetResponses.Length)];
        BuddyChanged?.Invoke();

        // Clear petting state after 2.5 seconds
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_buddy != null) _buddy.IsPetting = false;
            BuddyChanged?.Invoke();
        };
        timer.Start();

        // Clear speech after 5 seconds
        var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        clearTimer.Tick += (_, _) =>
        {
            clearTimer.Stop();
            if (_buddy != null) _buddy.SpeechBubble = "";
        };
        clearTimer.Start();
    }

    /// <summary>Make the buddy react to something (tool use, error, success).</summary>
    public void React(string message)
    {
        if (_buddy == null) return;
        _buddy.SpeechBubble = message;
        _buddy.CurrentMood = "reacting";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_buddy != null)
            {
                _buddy.SpeechBubble = "";
                _buddy.CurrentMood = "idle";
            }
        };
        timer.Start();
    }

    private void ShowRandomQuip()
    {
        if (_buddy == null || _buddy.HasSpeech) return;
        int group = _displayRng.Next(IdleQuips.Length);
        string quip = IdleQuips[group][_displayRng.Next(IdleQuips[group].Length)];
        _buddy.SpeechBubble = quip;

        // Random interval for next quip: 30-90 seconds
        _quipTimer.Interval = TimeSpan.FromSeconds(30 + _displayRng.Next(60));

        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            if (_buddy != null && _buddy.SpeechBubble == quip)
                _buddy.SpeechBubble = "";
        };
        clear.Start();
    }

    // ─── Deterministic Generation ────────────────────────────

    private static BuddyBones GenerateBones(string userId)
    {
        uint seed = FnvHash(userId + SALT);
        var rng = new Mulberry32(seed);

        var rarity = RollRarity(rng);
        var species = (BuddySpecies)(rng.Next() % Enum.GetValues<BuddySpecies>().Length);
        var eye = Eyes[rng.Next() % Eyes.Length];
        var hat = rarity == BuddyRarity.Common
            ? BuddyHat.None
            : (BuddyHat)(rng.Next() % Enum.GetValues<BuddyHat>().Length);
        bool shiny = (rng.Next() % 100) == 0; // 1% chance

        return new BuddyBones
        {
            Rarity = rarity,
            Species = species,
            Eye = eye,
            Hat = hat,
            Shiny = shiny,
            Stats = RollStats(rng, rarity),
        };
    }

    private static BuddyRarity RollRarity(Mulberry32 rng)
    {
        int roll = (int)(rng.Next() % 100);
        int cumulative = 0;
        foreach (var (rarity, weight) in RarityWeights)
        {
            cumulative += weight;
            if (roll < cumulative) return rarity;
        }
        return BuddyRarity.Common;
    }

    private static BuddyStats RollStats(Mulberry32 rng, BuddyRarity rarity)
    {
        int floor = rarity switch
        {
            BuddyRarity.Common => 5,
            BuddyRarity.Uncommon => 15,
            BuddyRarity.Rare => 25,
            BuddyRarity.Epic => 35,
            BuddyRarity.Legendary => 50,
            _ => 5,
        };

        int[] stats = new int[5];
        int peakIdx = (int)(rng.Next() % 5);
        int dumpIdx = (int)((peakIdx + 1 + rng.Next() % 4) % 5);

        for (int i = 0; i < 5; i++)
        {
            if (i == peakIdx)
                stats[i] = Math.Clamp(50 + floor + (int)(rng.Next() % 30), 1, 100);
            else if (i == dumpIdx)
                stats[i] = Math.Clamp(floor - 10 + (int)(rng.Next() % 15), 1, 100);
            else
                stats[i] = Math.Clamp(floor + (int)(rng.Next() % 40), 1, 100);
        }

        return new BuddyStats
        {
            Debugging = stats[0],
            Patience = stats[1],
            Chaos = stats[2],
            Wisdom = stats[3],
            Snark = stats[4],
        };
    }

    private static string GenerateName(BuddySpecies species, BuddyRarity rarity)
    {
        string[][] names =
        [
            ["Quackers", "Waddle", "Ducky", "Puddles", "Sunny"],     // Duck
            ["Honkers", "Goober", "Gigi", "Wadsworth", "Feathers"],   // Goose
            ["Blobby", "Splat", "Goop", "Wobble", "Jelly"],          // Blob
            ["Whiskers", "Mittens", "Neko", "Shadow", "Luna"],        // Cat
            ["Ember", "Scales", "Blaze", "Drake", "Pyro"],           // Dragon
            ["Inky", "Tentacles", "Squidly", "Octo", "Coral"],       // Octopus
            ["Hoot", "Athena", "Owlbert", "Noctis", "Sage"],         // Owl
            ["Tux", "Waddle", "Pingu", "Frost", "Flipper"],          // Penguin
            ["Shell", "Tortuga", "Slowpoke", "Mossy", "Zen"],        // Turtle
            ["Spiral", "Slime", "Gary", "Trail", "Dewdrop"],         // Snail
            ["Boo", "Casper", "Phantom", "Wisp", "Spooky"],          // Ghost
            ["Axo", "Lotl", "Pinky", "Gills", "Bubbles"],            // Axolotl
            ["Capy", "Chill", "Nugget", "Mellow", "Bean"],           // Capybara
            ["Spike", "Prickle", "Verde", "Sunny", "Thorn"],         // Cactus
            ["Beep", "Boop", "Chip", "Circuit", "Sparky"],           // Robot
            ["Bun", "Hopper", "Clover", "Marshmallow", "Flopsy"],    // Rabbit
            ["Shroom", "Fungi", "Toad", "Cap", "Spore"],             // Mushroom
            ["Chunky", "Thicc", "Mochi", "Pudge", "Dumpling"],       // Chonk
        ];

        int speciesIdx = (int)species;
        if (speciesIdx < names.Length && names[speciesIdx].Length > 0)
        {
            int nameIdx = (int)(FnvHash(species.ToString() + rarity.ToString()) % (uint)names[speciesIdx].Length);
            return names[speciesIdx][nameIdx];
        }
        return "Buddy";
    }

    private static string GeneratePersonality(BuddyBones bones)
    {
        string peak = bones.Stats.PeakStat;
        return peak switch
        {
            "Debugging" => "meticulous and loves finding bugs",
            "Patience" => "calm and supportive, always encouraging",
            "Chaos" => "chaotic and unpredictable, full of surprises",
            "Wisdom" => "thoughtful and philosophical",
            "Snark" => "sarcastic but lovable",
            _ => "curious and friendly",
        };
    }

    // ─── Persistence ─────────────────────────────────────────

    private BuddySoul? LoadSoul()
    {
        try
        {
            if (File.Exists(_buddyPath))
            {
                string json = File.ReadAllText(_buddyPath);
                return JsonSerializer.Deserialize<BuddySoul>(json);
            }
        }
        catch { }
        return null;
    }

    private void SaveSoul(BuddySoul soul)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_buddyPath);
            if (dir != null) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(soul, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_buddyPath, json);
        }
        catch { }
    }

    private string GetUserId()
    {
        // Use a stable identifier — machine name + username
        return $"{Environment.MachineName}:{Environment.UserName}";
    }

    // ─── Hash & PRNG ─────────────────────────────────────────

    private static uint FnvHash(string str)
    {
        uint hash = 2166136261;
        foreach (char c in str)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>Mulberry32 PRNG — deterministic from seed.</summary>
    private class Mulberry32
    {
        private uint _state;

        public Mulberry32(uint seed) => _state = seed;

        public uint Next()
        {
            _state += 0x6D2B79F5u;
            uint z = _state;
            z = (z ^ (z >> 15)) * (z | 1u);
            z ^= z + (z ^ (z >> 7)) * (z | 61u);
            return z ^ (z >> 14);
        }
    }
}
