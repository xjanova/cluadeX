using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.ModelPicker;

namespace CluadeX.ViewModels.ModelPicker;

/// <summary>One provider in the picker's first step: her girl, how she is reached, and whether she answers right now.</summary>
public sealed class PickerProviderCard : ViewModelBase
{
    public PickerProviderCard(AiProviderType group, string title, PickerProviderKind kind, string kindText,
        string stateText, string stateTone, bool isCurrent, IReadOnlyList<PickerModelCard> models)
    {
        Group = group;
        Title = title;
        Kind = kind;
        KindText = kindText;
        StateText = stateText;
        StateTone = stateTone;
        IsCurrent = isCurrent;
        Models = models;
        Mascot = ModelFacts.MascotOf(group);
    }

    public AiProviderType Group { get; }
    public string Title { get; }
    public PickerProviderKind Kind { get; }
    /// <summary>"API key", "CLI on your plan", "GGUF · this PC", …</summary>
    public string KindText { get; }
    /// <summary>"Answering this chat", "Key saved", "Signed in", "Not installed", …</summary>
    public string StateText { get; }
    /// <summary>The pill's colour: current / ok / warn / off / busy (the view maps it to a theme brush).</summary>
    public string StateTone { get; }
    public bool IsCurrent { get; }
    public IReadOnlyList<PickerModelCard> Models { get; }
    public int ModelCount => Models.Count;
    /// <summary>"12 models", or the local count — filled by the builder in the owner's language.</summary>
    public string ModelCountText { get; init; } = "";

    public ChatCharacter Mascot { get; }
    public ImageSource? Image => Mascot.Image;
    public Brush? Face => Mascot.FaceBrush;
    public Brush Accent => Mascot.AccentBrush;
    public Brush Wash => Mascot.WashBrush;
    public Brush Tint => Mascot.TintBrush;
    public Brush OnAccent => Mascot.OnAccentBrush;
    public System.Windows.Media.Effects.Effect Glow => Mascot.GlowEffect;

    private bool _isHighlighted;
    /// <summary>The keyboard's cursor is on this card.</summary>
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
}

/// <summary>One model in the picker's second step: what it can do, what it suits, what it costs, and whether it can be
/// picked now — and if not, why, in plain words.</summary>
public sealed class PickerModelCard : ViewModelBase
{
    public required AiProviderType Provider { get; init; }
    public required string Model { get; init; }
    /// <summary>"GPT-6.1 Terra", "Claude Opus 5.5", a GGUF's name.</summary>
    public required string Title { get; init; }
    /// <summary>The id the request sends (shown small, mono), or a GGUF's quant · size.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>The old model box's entry this card stands for (null for a GGUF file).</summary>
    public ViewModels.ChatViewModel.ChatModelChoice? Choice { get; init; }
    /// <summary>The GGUF file this card stands for (null for anything else).</summary>
    public ModelInfo? Gguf { get; init; }

    public bool HasTools { get; init; }
    public bool HasVision { get; init; }
    /// <summary>"thinks · up to max" — empty when the model does not think.</summary>
    public string ThinkingText { get; init; } = "";
    public bool HasThinking => ThinkingText.Length > 0;
    /// <summary>"1.05M context".</summary>
    public string ContextText { get; init; } = "";
    public string SuitsText { get; init; } = "";

    public PriceKind PriceKind { get; init; }
    public bool HasTokenPrice => PriceKind == PriceKind.PerToken;
    /// <summary>"$1" (input per 1M).</summary>
    public string PriceInText { get; init; } = "";
    /// <summary>"$5" (output per 1M).</summary>
    public string PriceOutText { get; init; } = "";
    /// <summary>"cached $0.05" — empty when the vendor has no cache price.</summary>
    public string PriceCacheText { get; init; } = "";
    /// <summary>"Free — runs on this PC", "On your Claude plan", "Price not reported" — when there is no per-token price.</summary>
    public string PriceNote { get; init; } = "";

    public required PickStatus Status { get; init; }
    public required PickAction Action { get; init; }
    public string StatusText { get; init; } = "";
    /// <summary>current / ok / warn / off / busy / blocked.</summary>
    public string StatusTone { get; init; } = "ok";
    /// <summary>Why it cannot be picked as it is, or what a click will do instead — empty for a plain pick.</summary>
    public string ReasonText { get; init; } = "";
    public bool HasReason => ReasonText.Length > 0;
    /// <summary>The button's words: "Use this model", "Add a key", "Sign in", …; empty when nothing can be done.</summary>
    public string ActionText { get; init; } = "";
    public bool CanChoose => Action != PickAction.None;
    public bool IsCurrent => Status == PickStatus.Current;

    public required ChatCharacter Mascot { get; init; }
    public Brush Accent => Mascot.AccentBrush;
    public Brush Tint => Mascot.TintBrush;
    public Brush OnAccent => Mascot.OnAccentBrush;

    private bool _isHighlighted;
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
}
