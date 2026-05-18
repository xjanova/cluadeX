using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace CluadeX.Services;

/// <summary>
/// XAML markup extension that produces a live-binding localized string.
/// Usage: <c>Text="{services:Loc settings.title}"</c>.
///
/// Behaviour:
///   - First lookup returns the translation for the current language.
///   - When LocalizationService raises LanguageChanged, the binding re-queries
///     and the UI refreshes automatically — no manual property wiring per label.
///
/// The extension delegates to <see cref="LocalizedResources"/>, a singleton
/// indexer proxy that owns the LocalizationService reference and forwards
/// change notifications. WPF binds to <c>Item[key]</c> on that proxy.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizedResources.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
            TargetNullValue = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Singleton indexer bound to XAML. Exposes <c>this[key]</c> so bindings of the
/// form <c>{Binding [settings.title], Source={x:Static services:LocalizedResources.Instance}}</c>
/// resolve to the current translation. Also re-raises <c>Item[]</c> on language change
/// so every consumer refreshes at once.
/// </summary>
public class LocalizedResources : INotifyPropertyChanged
{
    public static LocalizedResources Instance { get; } = new();
    private LocalizationService? _loc;

    private LocalizedResources() { }

    /// <summary>Called once during app startup, after DI is built.</summary>
    public void Initialize(LocalizationService loc)
    {
        if (_loc != null) return; // idempotent
        _loc = loc;
        _loc.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        // "Item[]" refreshes every indexer binding — cheaper than tracking each key.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => _loc?.T(key) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;
}
