using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.UiStudio;

/// <summary>
/// DI registration for the UI Studio and the Node Graph. App.ConfigureServices calls <see cref="AddUiStudioModule"/>:
/// the shared Studio state (also an exit hook, so a dev server it started goes with CluadeX) and both pages' view models.
/// </summary>
public static class UiStudioRegistration
{
    public static IServiceCollection AddUiStudioModule(this IServiceCollection services)
    {
        services.AddSingleton(sp => new StudioService(sp.GetRequiredService<SettingsService>(), sp.GetService<DebugLogService>()));
        services.AddSingleton<IAppShutdownHook>(sp => sp.GetRequiredService<StudioService>());
        services.AddSingleton<ViewModels.UiStudioViewModel>();
        services.AddSingleton<ViewModels.NodeGraphViewModel>();
        return services;
    }
}
