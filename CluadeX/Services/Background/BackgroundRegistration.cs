using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Background;

/// <summary>
/// DI registration for background processes and dev servers (team T2 owns this file). App.ConfigureServices calls
/// <see cref="AddBackgroundModule"/>; register here, e.g. BackgroundProcessService (also as IAppShutdownHook),
/// the bg_* tools as <c>Tools.IAgentToolModule</c> and LongRunningCommand as <c>Tools.IShellCommandInterceptor</c>.
/// </summary>
public static class BackgroundRegistration
{
    public static IServiceCollection AddBackgroundModule(this IServiceCollection services) => services;
}
