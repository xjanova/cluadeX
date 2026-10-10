using Microsoft.Extensions.DependencyInjection;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("browsertest")]

namespace CluadeX.Services.Browser;

/// <summary>
/// DI registration for the built-in browser (team T3 owns this file). App.ConfigureServices calls
/// <see cref="AddBrowserModule"/>: the engine service (also an exit hook, so the WebView2 browser process goes
/// with CluadeX), the browser_* tool module, and the preview presenter that pops the page out when the agent
/// opens one while no Preview pane is on screen.
/// </summary>
public static class BrowserRegistration
{
    public static IServiceCollection AddBrowserModule(this IServiceCollection services)
    {
        services.AddSingleton(sp => new BrowserService(sp.GetRequiredService<SettingsService>())
        {
            // The user's browse rules decide for the page's own requests too (Allow lets one through, Deny blocks
            // even an image) — the same lookup the permission gate uses for browser_open.
            UrlRule = url => sp.GetService<PermissionService>()?.FindUrlRule(url, "browse", "browser_open", legacyResource: "browser_open"),
            // The GPU guard asks the provider layer, not only for a llama-server process: an in-process (LLamaSharp)
            // model, loaded or loading, holds VRAM as well.
            LocalModelLoaded = () =>
            {
                var llama = sp.GetService<LlamaInferenceService>();
                var server = sp.GetService<Providers.LlamaServerProvider>();
                return llama is { IsModelLoaded: true } or { IsLoading: true } || server is { IsServerRunning: true };
            },
        });
        services.AddSingleton<IAppShutdownHook>(sp => sp.GetRequiredService<BrowserService>());

        services.AddSingleton<ViewModels.PreviewViewModel>();
        services.AddSingleton<IPreviewPresenter>(sp => new Views.Preview.PreviewPresenter(sp.GetRequiredService<ViewModels.PreviewViewModel>()));

        services.AddSingleton(sp => new BrowserToolModule(
            sp.GetRequiredService<BrowserService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ActivationService>(),
            sp.GetRequiredService<Vision.ToolImageStore>(),
            sp.GetRequiredService<FileSystemService>(),
            sp.GetServices<IBrowserUrlResolver>(),
            sp.GetService<IPreviewPresenter>()));
        services.AddSingleton<Tools.IAgentToolModule>(sp => sp.GetRequiredService<BrowserToolModule>());
        return services;
    }
}
