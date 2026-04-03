using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CluadeX.Models;

// ─── Buddy/Companion System ─────────────────────────────────
// Deterministic companion generated from userId hash.
// Inspired by Claude Code's buddy system — reimplemented for WPF.

public enum BuddyRarity { Common, Uncommon, Rare, Epic, Legendary }

public enum BuddySpecies
{
    Duck, Goose, Blob, Cat, Dragon, Octopus, Owl, Penguin,
    Turtle, Snail, Ghost, Axolotl, Capybara, Cactus, Robot,
    Rabbit, Mushroom, Chonk
}

public enum BuddyHat
{
    None, Crown, TopHat, Propeller, Halo, Wizard, Beanie, TinyDuck
}

public class BuddyStats
{
    public int Debugging { get; set; }
    public int Patience { get; set; }
    public int Chaos { get; set; }
    public int Wisdom { get; set; }
    public int Snark { get; set; }

    public string PeakStat
    {
        get
        {
            int max = Math.Max(Math.Max(Math.Max(Debugging, Patience), Math.Max(Chaos, Wisdom)), Snark);
            if (max == Debugging) return "Debugging";
            if (max == Patience) return "Patience";
            if (max == Chaos) return "Chaos";
            if (max == Wisdom) return "Wisdom";
            return "Snark";
        }
    }
}

public class BuddyBones
{
    public BuddyRarity Rarity { get; set; }
    public BuddySpecies Species { get; set; }
    public string Eye { get; set; } = "·";
    public BuddyHat Hat { get; set; }
    public bool Shiny { get; set; }
    public BuddyStats Stats { get; set; } = new();
}

public class BuddySoul
{
    public string Name { get; set; } = "";
    public string Personality { get; set; } = "";
    public DateTime HatchedAt { get; set; } = DateTime.Now;
}

public class Buddy : INotifyPropertyChanged
{
    public BuddyBones Bones { get; set; } = new();
    public BuddySoul Soul { get; set; } = new();

    private string _currentMood = "idle";
    private string _speechBubble = "";
    private bool _isPetting;

    public string CurrentMood
    {
        get => _currentMood;
        set { _currentMood = value; OnPropertyChanged(); }
    }

    public string SpeechBubble
    {
        get => _speechBubble;
        set { _speechBubble = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSpeech)); }
    }

    public bool HasSpeech => !string.IsNullOrEmpty(_speechBubble);

    public bool IsPetting
    {
        get => _isPetting;
        set { _isPetting = value; OnPropertyChanged(); }
    }

    // Display helpers
    public string DisplayEmoji => Bones.Species switch
    {
        BuddySpecies.Duck => "\U0001F986",
        BuddySpecies.Goose => "\U0001FAA8",
        BuddySpecies.Blob => "\U0001F4A7",
        BuddySpecies.Cat => "\U0001F431",
        BuddySpecies.Dragon => "\U0001F409",
        BuddySpecies.Octopus => "\U0001F419",
        BuddySpecies.Owl => "\U0001F989",
        BuddySpecies.Penguin => "\U0001F427",
        BuddySpecies.Turtle => "\U0001F422",
        BuddySpecies.Snail => "\U0001F40C",
        BuddySpecies.Ghost => "\U0001F47B",
        BuddySpecies.Axolotl => "\U0001F98E",
        BuddySpecies.Capybara => "\U0001F9AB",
        BuddySpecies.Cactus => "\U0001F335",
        BuddySpecies.Robot => "\U0001F916",
        BuddySpecies.Rabbit => "\U0001F430",
        BuddySpecies.Mushroom => "\U0001F344",
        BuddySpecies.Chonk => "\U0001F43E",
        _ => "\U0001F431",
    };

    public string HatEmoji => Bones.Hat switch
    {
        BuddyHat.Crown => "\U0001F451",
        BuddyHat.TopHat => "\U0001F3A9",
        BuddyHat.Propeller => "\U0001FA81",
        BuddyHat.Halo => "\U0001F607",
        BuddyHat.Wizard => "\U0001F9D9",
        BuddyHat.Beanie => "\U0001F9E2",
        BuddyHat.TinyDuck => "\U0001F986",
        _ => "",
    };

    public string RarityStars => Bones.Rarity switch
    {
        BuddyRarity.Common => "\u2B50",
        BuddyRarity.Uncommon => "\u2B50\u2B50",
        BuddyRarity.Rare => "\u2B50\u2B50\u2B50",
        BuddyRarity.Epic => "\u2B50\u2B50\u2B50\u2B50",
        BuddyRarity.Legendary => "\u2B50\u2B50\u2B50\u2B50\u2B50",
        _ => "\u2B50",
    };

    public string RarityColor => Bones.Rarity switch
    {
        BuddyRarity.Common => "#A6ADC8",
        BuddyRarity.Uncommon => "#A6E3A1",
        BuddyRarity.Rare => "#89B4FA",
        BuddyRarity.Epic => "#CBA6F7",
        BuddyRarity.Legendary => "#F9E2AF",
        _ => "#A6ADC8",
    };

    public string ShinyBadge => Bones.Shiny ? " \u2728" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
