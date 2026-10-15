using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Background;

/// <summary>
/// DI registration for background processes and dev servers (team T2 owns this file). App.ConfigureServices calls
/// <see cref="AddBackgroundModule"/>:
/// <list type="bullet">
/// <item><see cref="BackgroundProcessService"/> — the one registry (the Task Manager page is a facade over it), also an
///   <see cref="IAppShutdownHook"/> so every tree dies on exit;</item>
/// <item><see cref="BackgroundToolModule"/> — bg_start / bg_serve / bg_output / bg_stop / bg_list (and the task_* names);</item>
/// <item><see cref="LongRunningCommand"/> — run_command / powershell hand servers to the background and refuse watchers.</item>
/// </list>
/// </summary>
public static class BackgroundRegistration
{
    public static IServiceCollection AddBackgroundModule(this IServiceCollection services)
    {
        services.AddSingleton(sp => new BackgroundProcessService(
            sp.GetRequiredService<SettingsService>(), sp.GetService<FileSystemService>()));
        services.AddSingleton<IAppShutdownHook>(sp => sp.GetRequiredService<BackgroundProcessService>());

        services.AddSingleton(sp => new BackgroundToolModule(
            sp.GetRequiredService<BackgroundProcessService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ActivationService>(),
            sp.GetRequiredService<PermissionService>(),
            sp.GetRequiredService<FileSystemService>(),
            sp));
        services.AddSingleton<IAgentToolModule>(sp => sp.GetRequiredService<BackgroundToolModule>());

        services.AddSingleton(sp => new LongRunningCommand(
            sp.GetRequiredService<BackgroundProcessService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ActivationService>(),
            sp));
        services.AddSingleton<IShellCommandInterceptor>(sp => sp.GetRequiredService<LongRunningCommand>());
        return services;
    }
}
