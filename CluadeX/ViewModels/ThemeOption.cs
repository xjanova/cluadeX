namespace CluadeX.ViewModels;

/// <summary>One card of the theme picker (Settings › General › Appearance).</summary>
public sealed class ThemeOption : ViewModelBase
{
    public ThemeOption(Services.ThemeInfo info)
    {
        Id = info.Id;
        NameKey = info.NameKey;
        DescKey = info.DescKey;
        IsReady = info.IsReady;
    }

    /// <summary>ThemeIds value — also what the card's preview template is chosen by.</summary>
    public string Id { get; }
    public string NameKey { get; }
    public string DescKey { get; }

    /// <summary>False: the card carries an "in progress" badge (the theme applies, but is not fully painted yet).</summary>
    public bool IsReady { get; }

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private bool _isSelected;
    /// <summary>The theme on screen now.</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
