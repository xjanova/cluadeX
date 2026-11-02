using Demo.Services;

namespace Demo.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IClock _clock;

    public MainViewModel(IClock clock)
    {
        _clock = clock;
        Title = _clock.Now().ToString();
    }

    public string Title { get; }
}
