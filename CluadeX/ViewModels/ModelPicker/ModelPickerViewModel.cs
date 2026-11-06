using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.ModelPicker;

namespace CluadeX.ViewModels.ModelPicker;

/// <summary>
/// The chat's model picker, full screen over the window (owner 2026-11-06: "ทำใหม่ให้เห็นเมนูเลือกสวยงามเป็นสาวมาสคอต
/// ค่ายนั้นๆ … ให้กดเลือกก่อนว่าค่ายไหน แล้วค่อยโชว์โมเดล"). Step one: a card per provider, her girl and whether she
/// answers now. Step two: that provider's models — what they can do, what they suit, the price, and whether one can be
/// picked now. What a pick does is the host's (<see cref="ChatViewModel"/>): exactly what the old model box did.
/// The keyboard: arrows move, Enter picks, Backspace goes back a step, Esc closes.
/// </summary>
public sealed class ModelPickerViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PickerProviderCard>> _build;
    private readonly Action<PickerModelCard> _apply;

    public ModelPickerViewModel(Func<IReadOnlyList<PickerProviderCard>> build, Action<PickerModelCard> apply)
    {
        _build = build;
        _apply = apply;
        CloseCommand = new RelayCommand(Close);
        BackCommand = new RelayCommand(Back);
        PickProviderCommand = new RelayCommand<PickerProviderCard>(c => { if (c != null) PickProvider(c); });
        PickModelCommand = new RelayCommand<PickerModelCard>(c => { if (c != null) PickModel(c); });
    }

    public ObservableCollection<PickerProviderCard> Providers { get; } = new();
    public ObservableCollection<PickerModelCard> Models { get; } = new();

    public ICommand CloseCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PickProviderCommand { get; }
    public ICommand PickModelCommand { get; }

    /// <summary>Raised when the picker opens (the view takes keyboard focus) and closes.</summary>
    public event Action<bool>? OpenChanged;
    /// <summary>The picker opened — the host starts its slow looks (CLI sign-in, GGUF headers) and calls <see cref="Refresh"/>.</summary>
    public event Action? Opened;

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set { if (SetProperty(ref _isOpen, value)) OpenChanged?.Invoke(value); }
    }

    private PickerProviderCard? _provider;
    /// <summary>The provider whose models are shown (null on step one).</summary>
    public PickerProviderCard? Provider
    {
        get => _provider;
        private set
        {
            if (!SetProperty(ref _provider, value)) return;
            OnPropertyChanged(nameof(IsProviderStep));
            OnPropertyChanged(nameof(IsModelStep));
            OnPropertyChanged(nameof(Columns));
        }
    }
    public bool IsProviderStep => _provider == null;
    public bool IsModelStep => _provider != null;

    private int _providerColumns = 4, _modelColumns = 2;
    /// <summary>Provider cards per row, as the view lays them out (it sets this from its width).</summary>
    public int ProviderColumns
    {
        get => _providerColumns;
        set { if (SetProperty(ref _providerColumns, Math.Max(1, value))) OnPropertyChanged(nameof(Columns)); }
    }
    /// <summary>Model cards per row.</summary>
    public int ModelColumns
    {
        get => _modelColumns;
        set { if (SetProperty(ref _modelColumns, Math.Max(1, value))) OnPropertyChanged(nameof(Columns)); }
    }
    /// <summary>Cards per row in the step on screen — Up/Down move by a row.</summary>
    public int Columns => IsProviderStep ? _providerColumns : _modelColumns;

    private int _highlight = -1;
    /// <summary>The keyboard cursor's index in the step on screen (-1: none).</summary>
    public int HighlightIndex
    {
        get => _highlight;
        private set
        {
            if (!SetProperty(ref _highlight, value)) return;
            for (int i = 0; i < Providers.Count; i++) Providers[i].IsHighlighted = IsProviderStep && i == value;
            for (int i = 0; i < Models.Count; i++) Models[i].IsHighlighted = IsModelStep && i == value;
            OnPropertyChanged(nameof(Highlighted));
        }
    }

    /// <summary>The card the cursor is on (a provider card on step one, a model card on step two).</summary>
    public object? Highlighted => IsProviderStep
        ? (_highlight >= 0 && _highlight < Providers.Count ? Providers[_highlight] : null)
        : (_highlight >= 0 && _highlight < Models.Count ? Models[_highlight] : null);

    private string _note = "";
    /// <summary>A line under the cards: why the card just pressed did nothing.</summary>
    public string Note { get => _note; private set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;

    public void Open()
    {
        Fill(_build(), keepStep: false);
        Note = "";
        IsOpen = true;
        Opened?.Invoke();
    }

    /// <summary>Open straight on a provider's models (the owner already said which).</summary>
    public void OpenOn(AiProviderType provider)
    {
        Open();
        var card = Providers.FirstOrDefault(p => p.Group == ModelFacts.GroupOf(provider));
        if (card != null) PickProvider(card);
    }

    /// <summary>Rebuild the cards from what is true now (a sign-in look came back, a GGUF header was read), staying on
    /// the same step and the same card.</summary>
    public void Refresh()
    {
        if (!IsOpen) return;
        Fill(_build(), keepStep: true);
    }

    private void Fill(IReadOnlyList<PickerProviderCard> cards, bool keepStep)
    {
        var step = keepStep ? _provider?.Group : null;
        object? at = keepStep ? Highlighted : null;
        Providers.Clear();
        foreach (var c in cards) Providers.Add(c);
        var again = step is { } g ? Providers.FirstOrDefault(p => p.Group == g) : null;
        if (again != null)
        {
            ShowModels(again);
            int i = at is PickerModelCard m ? IndexOf(Models, x => x.Provider == m.Provider && x.Model == m.Model) : -1;
            if (i >= 0) SetHighlight(i);
            return;
        }
        Provider = null;
        Models.Clear();
        int keep = at is PickerProviderCard p0 ? IndexOf(Providers, x => x.Group == p0.Group) : -1;
        SetHighlight(keep >= 0 ? keep : Math.Max(0, IndexOf(Providers, x => x.IsCurrent)));
    }

    public void Close()
    {
        IsOpen = false;
        Provider = null;
        Models.Clear();
        _highlight = -1;
    }

    /// <summary>Step two → step one (with the provider still under the cursor); on step one, closes.</summary>
    public void Back()
    {
        if (_provider == null) { Close(); return; }
        var from = _provider.Group;
        Provider = null;
        Models.Clear();
        Note = "";
        SetHighlight(Math.Max(0, IndexOf(Providers, p => p.Group == from)));
    }

    public void PickProvider(PickerProviderCard card)
    {
        Note = "";
        ShowModels(card);
    }

    private void ShowModels(PickerProviderCard card)
    {
        Models.Clear();
        foreach (var m in card.Models) Models.Add(m);
        _highlight = -1;
        Provider = card;
        int cur = IndexOf(Models, m => m.IsCurrent);
        SetHighlight(cur >= 0 ? cur : Math.Max(0, IndexOf(Models, m => m.CanChoose)));
    }

    /// <summary>A model card pressed: pick it (or open its wizard), close for the one in use, or say why not.</summary>
    public void PickModel(PickerModelCard card)
    {
        switch (card.Action)
        {
            case PickAction.None:
                Note = card.ReasonText;
                SetHighlight(Models.IndexOf(card));
                return;
            case PickAction.Close:
                Close();
                return;
            default:
                Close();
                _apply(card);
                return;
        }
    }

    /// <summary>Enter: the card under the cursor.</summary>
    public void Activate()
    {
        switch (Highlighted)
        {
            case PickerProviderCard p: PickProvider(p); break;
            case PickerModelCard m: PickModel(m); break;
        }
    }

    /// <summary>Arrow keys: <paramref name="dx"/> moves one card, <paramref name="dy"/> one row; stays inside the step.</summary>
    public void Move(int dx, int dy)
    {
        int count = IsProviderStep ? Providers.Count : Models.Count;
        if (count == 0) return;
        if (_highlight < 0) { SetHighlight(0); return; }
        int next = _highlight + dx + dy * Columns;
        SetHighlight(Math.Clamp(next, 0, count - 1));
    }

    /// <summary>The picker's keys, handled here so a harness can press them without a window. True = handled.</summary>
    public bool HandleKey(Key key)
    {
        if (!IsOpen) return false;
        switch (key)
        {
            case Key.Escape: Close(); return true;
            case Key.Back: Back(); return true;
            case Key.Enter: Activate(); return true;
            case Key.Left: Move(-1, 0); return true;
            case Key.Right: Move(1, 0); return true;
            case Key.Up: Move(0, -1); return true;
            case Key.Down: Move(0, 1); return true;
            default: return false;
        }
    }

    private void SetHighlight(int i)
    {
        _highlight = int.MinValue;   // force the per-card flags to be rewritten even when the index is unchanged
        HighlightIndex = i;
    }

    private static int IndexOf<T>(IList<T> list, Func<T, bool> pick)
    {
        for (int i = 0; i < list.Count; i++) if (pick(list[i])) return i;
        return -1;
    }
}
