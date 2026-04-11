using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Services;
using CluadeX.Services.Mcp;
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

        // Ensure skill directories exist
        try
        {
            string skillsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "skills");
            System.IO.Directory.CreateDirectory(skillsDir);
        }
        catch { }

        // Initialize background services (non-blocking)
        _ = Task.Run(async () =>
        {
            try
            {
                // Validate license key against online API
                var activation = _serviceProvider.GetRequiredService<ActivationService>();
                await activation.ValidateOnlineAsync();
            }
            catch { /* License validation is best-effort */ }

            try
            {
                // Initialize MCP servers
                var mcpManager = _serviceProvider.GetRequiredService<McpServerManager>();
                await mcpManager.InitializeAsync();
            }
            catch { /* MCP init is best-effort */ }
        });
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
        services.AddSingleton<LspClientService>();
        services.AddSingleton<SkillService>();
        services.AddSingleton<CostTrackingService>();
        services.AddSingleton<MemoryService>();
        services.AddSingleton<HookService>();
        services.AddSingleton<McpServerManager>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PluginManagerViewModel>();
        services.AddSingleton<PermissionsViewModel>();
        services.AddSingleton<TaskManagerViewModel>();
        services.AddSingleton<FeaturesViewModel>();
        services.AddSingleton<McpServersViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Flush all pending state to disk before exit
        try
        {
            var settings = _serviceProvider?.GetService<SettingsService>();
            settings?.Save();

            var chatVm = _serviceProvider?.GetService<ChatViewModel>();
            chatVm?.SaveNow();
        }
        catch { /* best-effort on exit */ }

        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }
}
