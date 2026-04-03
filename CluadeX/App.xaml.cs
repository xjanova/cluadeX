using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace CluadeX;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Services (singletons)
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GpuDetectionService>();
        services.AddSingleton<HuggingFaceService>();
        services.AddSingleton<LlamaInferenceService>();
        services.AddSingleton<CodeExecutionService>();
        services.AddSingleton<FileSystemService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<GitHubService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<ContextMemoryService>();
        services.AddSingleton<SmartEditingService>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<ChatPersistenceService>();
        services.AddSingleton<AiProviderManager>();
        services.AddSingleton<CodeAgentService>();
        services.AddSingleton<PluginService>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<TaskManagerService>();
        services.AddSingleton<WebFetchService>();
        services.AddSingleton<BuddyService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ActivationService>();
        services.AddSingleton<XmanLicenseService>();
        services.AddSingleton<BugReportService>();
        services.AddSingleton<AutoUpdateService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PluginManagerViewModel>();
        services.AddSingleton<PermissionsViewModel>();
        services.AddSingleton<TaskManagerViewModel>();
        services.AddSingleton<FeaturesViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }
}
