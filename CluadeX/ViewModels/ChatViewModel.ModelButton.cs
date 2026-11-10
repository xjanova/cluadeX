using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// The model button in the composer (it opens ModelPickerOverlay). Owner 2026-11-06: "เมื่อเลือกแล้ว ก็อยากให้ ปุ่ม แสดงชื่อ
/// โมเดลที่ใช้จริงตอนนั้นด้วย และมันควรเรืองแสง เมื่อมันกำลัง คิดหรือใช้งานอยู่ และล๊อกปุ่มให้กดเปลี่ยนไม่ได้ระหว่างมันใช้งาน".
/// So it names the model that really answers (the id the last response named — a CLI's "default" becomes the model it
/// ran — else the pick; the loaded GGUF for a model on this PC), glows in her colour while this chat's run works, and is
/// locked for that time (OpenModelPickerCommand's CanExecute; the tooltip says why and what to do).
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Called from InitControls (the constructor).</summary>
    private void InitModelButton()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedChatModel) or nameof(LiveModelEffortText) or nameof(SelectedLocalModel)
                or nameof(IsGenerating) or nameof(CurrentSession) or nameof(IsLoadingModel) or nameof(ShowLocalModelPicker)
                or nameof(ChatModelPlaceholder))
            {
                OnPropertyChanged(nameof(ModelButtonText));
                OnPropertyChanged(nameof(ModelButtonFace));
                OnPropertyChanged(nameof(ModelButtonGlow));
                OnPropertyChanged(nameof(ModelButtonPrice));
                OnPropertyChanged(nameof(HasModelButtonText));
                OnPropertyChanged(nameof(ShowModelPlaceholder));
                OnPropertyChanged(nameof(IsModelButtonLocked));
                OnPropertyChanged(nameof(ModelButtonTip));
                // Another chat on screen may be idle while this one works: the lock follows the chat shown.
                if (e.PropertyName is nameof(IsGenerating) or nameof(CurrentSession)) CommandManager.InvalidateRequerySuggested();
            }
        };
    }

    /// <summary>The GGUF file a model on this PC has loaded (null = none).</summary>
    private string? LoadedGgufPath() =>
        _llamaService.LoadedModelPath ?? (_providerManager.ActiveProvider.IsReady ? _settingsService.Settings.SelectedModelPath : null);

    /// <summary>The model that really answers this chat now, as people say it ("Opus 5.5", "GPT-6.1 Sol"); "" = none.</summary>
    public string ModelButtonText
    {
        get
        {
            // A chat on a model of its own (a Fable chat) names that model, not the app's pick.
            if (ShownOwnModel is { } own)
                return LiveSnapshot()?.Model is { Length: > 0 } ran ? LiveModelReport.FriendlyModel(ran) : LiveModelReport.FriendlyModel(own);
            if (_selectedChatModel is { } c)
            {
                if (LiveSnapshot()?.Model is { Length: > 0 } real) return LiveModelReport.FriendlyModel(real);
                return c.Model is "default" or "auto" or "" ? Loc("picker.model.cliDefault") : LiveModelReport.FriendlyModel(c.Model);
            }
            return ShowLocalModelPicker && LoadedGgufPath() is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : "";
        }
    }

    public bool HasModelButtonText => ModelButtonText.Length > 0;

    /// <summary>The placeholder ("a model on this PC — pick one…") only while the button has nothing to name.</summary>
    public bool ShowModelPlaceholder => !HasModelButtonText && ChatModelPlaceholder.Length > 0;

    private ChatCharacter ModelButtonGirl => ChatCharacter.ForProvider(_selectedChatModel?.Provider ?? _providerManager.PickedProviderType);

    /// <summary>Her face, round, beside the name.</summary>
    public Brush? ModelButtonFace => ModelButtonGirl.FaceBrush;

    /// <summary>Her colour: the button's frame and glow while the run works.</summary>
    public Color ModelButtonGlow => ModelButtonGirl.Accent;

    /// <summary>"$2 / $10" for a priced model, "" otherwise.</summary>
    public string ModelButtonPrice => ShownOwnModel is { } own ? PriceLabelOf(own) : _selectedChatModel?.PriceLabel ?? "";

    /// <summary>True while this chat's run works: the button glows and does not open the picker.</summary>
    public bool IsModelButtonLocked => IsGenerating;

    public string ModelButtonTip => IsGenerating ? Loc("picker.lockedTip") : ShownOwnModel != null ? Loc("fable.model.tip") : Loc("picker.open");
}
