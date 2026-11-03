using Demo.Services;
using Demo.ViewModels;

namespace Demo;

public partial class App
{
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddTransient<MainViewModel>();
    }
}
