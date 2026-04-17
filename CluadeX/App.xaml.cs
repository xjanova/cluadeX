using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;

namespace CluadeX;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>Directory crash logs are written to. Kept lightweight (no directory-exists check on each access).</summary>
    private static string CrashLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cluadex", "crash-logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ─── Global Exception Handlers (must be installed BEFORE any real work) ───
        // Without these, an unhandled exception anywhere in the app silently kills the process
        // with no diagnostic output — making it impossible for users to report bugs.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

        // Local GGUF backends — registered as singletons so AiProviderManager and
        // LocalGgufProvider share the same LlamaServerProvider instance (otherwise
        // the routing layer would launch a second llama-server process).
        services.AddSingleton<Services.Providers.LlamaServerProvider>();
        services.AddSingleton<Services.Providers.LocalGgufProvider>();

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
        catch (Exception ex)
        {
            // Best-effort on exit — still write to debug output for troubleshooting.
            System.Diagnostics.Debug.WriteLine($"OnExit flush failed: {ex.Message}");
        }

        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }

    // ─── Global Exception Handlers ───

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("dispatcher", e.Exception);
        // Keep the app alive for non-fatal UI exceptions — losing unsaved chat state is worse than a blip.
        // Fatal exceptions (StackOverflow, OutOfMemory, AccessViolation) can't be caught here anyway.
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nA crash log has been saved to:\n{CrashLogDir}",
            "CluadeX — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // AppDomain exceptions are typically fatal and the process will terminate after this returns.
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog("appdomain", ex);
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("task", e.Exception);
        // Mark as observed so GC doesn't terminate the process.
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDir);
            string path = Path.Combine(CrashLogDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{source}.log");
            var body = new System.Text.StringBuilder();
            body.AppendLine($"Timestamp: {DateTime.Now:O}");
            body.AppendLine($"Source: {source}");
            body.AppendLine($"App Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            body.AppendLine($"OS: {Environment.OSVersion.VersionString}");
            body.AppendLine($"CLR: {Environment.Version}");
            body.AppendLine();
            body.AppendLine(ex.ToString());
            File.WriteAllText(path, body.ToString());
        }
        catch (Exception writeEx)
        {
            // Crash log writing itself failed — do not try to show UI (could recurse).
            System.Diagnostics.Debug.WriteLine($"Failed to write crash log: {writeEx.Message}\nOriginal: {ex}");
        }
    }
}
