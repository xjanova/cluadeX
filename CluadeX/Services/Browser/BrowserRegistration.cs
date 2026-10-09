using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Browser;

/// <summary>
/// DI registration for the built-in browser (team T3 owns this file). App.ConfigureServices calls
/// <see cref="AddBrowserModule"/>; register here, e.g. BrowserService (also as IAppShutdownHook) and the
/// browser_* tools as <c>Tools.IAgentToolModule</c>.
/// </summary>
public static class BrowserRegistration
{
    public static IServiceCollection AddBrowserModule(this IServiceCollection services) => services;
}
