using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Models;
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

        // Wire the XAML-level localization proxy so {services:Loc key} works in any view.
        LocalizedResources.Instance.Initialize(_serviceProvider.GetRequiredService<LocalizationService>());

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

            try
            {
                // Start the in-process MCP host (named pipe).
                var host = _serviceProvider.GetRequiredService<McpHostService>();

                // Wire the tool dispatcher BEFORE accepting traffic so the
                // first request never hits a "not wired" error. Each tool
                // routes to ChatViewModel which spawns a visible session.
                host.ToolDispatcher = (invocation, ct) => HandleMcpToolAsync(invocation, ct);
                await host.StartAsync();

                // Surface the host on MainViewModel so the sidebar status chip
                // can data-bind to its observable properties. Do this on the UI
                // thread because property setter raises PropertyChanged, which
                // some bindings expect on the dispatcher.
                Dispatcher.Invoke(() =>
                {
                    var mainVm = _serviceProvider!.GetRequiredService<ViewModels.MainViewModel>();
                    mainVm.McpHost = host;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"McpHostService start failed: {ex.Message}");
            }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  MCP tool dispatcher
    //  ────────────────────
    //  Called from the named-pipe accept loop on a background thread. The
    //  tool implementations themselves call ChatViewModel which marshals
    //  to the dispatcher internally — so nothing UI-touching runs here at
    //  this top level.
    // ════════════════════════════════════════════════════════════════════

    private async Task<McpToolResult> HandleMcpToolAsync(McpToolInvocation invocation, CancellationToken ct)
    {
        try
        {
            return invocation.ToolName switch
            {
                "cluadex.write_code"        => await HandleWriteCodeAsync(invocation.Arguments, ct),
                "cluadex.run_skill"         => await HandleRunSkillAsync(invocation.Arguments, ct),
                "cluadex.review"            => await HandleReviewAsync(invocation.Arguments, ct),
                "cluadex.get_active_model"  => await HandleGetActiveModelAsync(ct),
                "cluadex.set_model"         => await HandleSetModelAsync(invocation.Arguments, ct),
                _ => ErrorResult($"Unknown tool '{invocation.ToolName}'"),
            };
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("Cancelled by caller");
        }
        catch (Exception ex)
        {
            return ErrorResult($"Tool '{invocation.ToolName}' threw: {ex.Message}");
        }
    }

    private async Task<McpToolResult> HandleWriteCodeAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        var chatVm = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();

        string taskId = ReadString(args, "taskId") ?? Guid.NewGuid().ToString("N")[..8];
        string spec = ReadString(args, "spec") ?? "";
        string? cwd = ReadString(args, "workingDirectory");
        var lessons = ReadStringArray(args, "lessons");
        var contextFiles = ReadStringArray(args, "contextFiles");

        if (string.IsNullOrWhiteSpace(spec))
            return ErrorResult("'spec' is required");

        string finalText = await chatVm.RunMcpTaskAsync(taskId, spec, lessons, contextFiles, cwd, ct);
        return TextResult(finalText);
    }

    private async Task<McpToolResult> HandleRunSkillAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        var chatVm = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();
        var skillService = _serviceProvider.GetRequiredService<Services.SkillService>();

        string taskId = ReadString(args, "taskId") ?? Guid.NewGuid().ToString("N")[..8];
        string skillName = ReadString(args, "skill") ?? "";
        string skillArgs = ReadString(args, "args") ?? "";
        string? cwd = ReadString(args, "workingDirectory");

        if (string.IsNullOrWhiteSpace(skillName))
            return ErrorResult("'skill' is required (e.g. 'commit', 'review-pr', 'simplify')");

        var skill = skillService.GetSkillByName(skillName);
        if (skill == null)
            return ErrorResult($"Skill '/{skillName}' not found. Run tools/list to see available tools, " +
                               "and check ~/.cluadex/skills/ for installed skills.");

        // Build a synthetic spec out of the skill template so we can reuse
        // RunMcpTaskAsync. The skill prompt is treated as the spec; the
        // user's free-form args go in as ## context.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(skill.PromptContent);
        if (!string.IsNullOrWhiteSpace(skillArgs))
        {
            sb.AppendLine();
            sb.AppendLine("## Caller arguments");
            sb.AppendLine(skillArgs);
        }
        string finalText = await chatVm.RunMcpTaskAsync(
            taskId: $"{taskId}-skill-{skillName}",
            spec: sb.ToString(),
            lessons: null,
            contextFiles: null,
            workingDirectory: cwd,
            ct: ct);
        return TextResult(finalText);
    }

    /// <summary>
    /// Report what model CluadeX has loaded right now. ObsidianX uses
    /// this before each Co-Pilot run to refuse a task if its intern and
    /// the CluadeX worker would fight over GPU memory by loading two
    /// different models simultaneously. The user explicitly asked us to
    /// enforce alignment ("ต้องให้โหลดโมเดลเดียวกัน").
    /// </summary>
    private Task<McpToolResult> HandleGetActiveModelAsync(CancellationToken ct)
    {
        if (_serviceProvider == null) return Task.FromResult(ErrorResult("DI not initialised"));
        var providerManager = _serviceProvider.GetRequiredService<Services.AiProviderManager>();
        var settings = _serviceProvider.GetRequiredService<Services.SettingsService>().Settings;
        var gpu = _serviceProvider.GetRequiredService<Services.GpuDetectionService>();

        // Resolve "what model name is in play" depending on provider.
        // Local GGUF / LlamaServer use the top-level SelectedModelName +
        // SelectedModelPath; cloud / Ollama providers store their model in
        // ProviderConfigs[ProviderId].EffectiveModelId. Reading the wrong
        // slot would give the orchestrator the OLD model name and trigger
        // a phantom "MISMATCH" warning even after a successful set_model.
        var providerType = providerManager.ActiveProviderType;
        string? modelName;
        string? modelPath = null;
        switch (providerType)
        {
            case Models.AiProviderType.Local:
            case Models.AiProviderType.LlamaServer:
                modelName = settings.SelectedModelName;
                modelPath = settings.SelectedModelPath;
                break;
            default:
                modelName = settings.ProviderConfigs.TryGetValue(providerType.ToString(), out var cfg)
                    ? cfg.EffectiveModelId
                    : null;
                break;
        }
        bool ready = providerManager.ActiveProvider.IsReady;

        // Best-effort VRAM read. GetLiveStats() shells out to nvidia-smi
        // (~50ms first time, cached otherwise); we wrap in try/catch so
        // a non-NVIDIA box just returns nulls instead of throwing into
        // the caller's tools/call response.
        int? vramUsedMb = null;
        int? vramTotalMb = null;
        try
        {
            var stats = gpu.GetLiveStats();
            if (stats != null)
            {
                vramUsedMb = stats.VramUsedMB;
                vramTotalMb = stats.VramTotalMB;
            }
        }
        catch { /* non-NVIDIA or nvidia-smi missing — leave nulls */ }

        var payload = new
        {
            provider = providerType.ToString(),
            model = modelName ?? "",
            path = modelPath ?? "",
            ready,
            vramUsedMB = vramUsedMb,
            vramTotalMB = vramTotalMb,
            // Hint the orchestrator can show to the user.
            note = ready
                ? "OK"
                : "CluadeX has no model loaded — open the Models tab and pick one before delegating tasks.",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        return Task.FromResult(TextResult(json));
    }

    /// <summary>
    /// Force CluadeX to switch provider + model so it aligns with the
    /// orchestrator's intern. Most common case: ObsidianX uses Ollama
    /// → tells CluadeX "switch to Ollama with this same model" so both
    /// share the daemon and one entry in VRAM.
    ///
    /// VRAM hygiene: if the previous provider was Local GGUF or
    /// LlamaServer (both load weights into VRAM), we explicitly stop
    /// the llama-server and release the LLamaSharp context before
    /// switching. Otherwise we'd keep the old 6 GB model in VRAM
    /// alongside whatever the new provider wants — defeating the
    /// whole alignment exercise.
    /// </summary>
    private async Task<McpToolResult> HandleSetModelAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");

        string? providerStr = ReadString(args, "provider");
        string? model = ReadString(args, "model");
        string? path = ReadString(args, "path");
        if (string.IsNullOrWhiteSpace(providerStr) || string.IsNullOrWhiteSpace(model))
            return ErrorResult("'provider' and 'model' are required");

        if (!Enum.TryParse<Models.AiProviderType>(providerStr, ignoreCase: true, out var providerType))
        {
            return ErrorResult($"Unknown provider '{providerStr}'. Valid: " +
                               string.Join(", ", Enum.GetNames<Models.AiProviderType>()));
        }

        var providerManager = _serviceProvider.GetRequiredService<Services.AiProviderManager>();
        var settingsService = _serviceProvider.GetRequiredService<Services.SettingsService>();
        var llamaServer = _serviceProvider.GetRequiredService<Services.Providers.LlamaServerProvider>();
        var localGguf = _serviceProvider.GetRequiredService<Services.Providers.LocalGgufProvider>();

        // Step 1: free VRAM held by GGUF backends if we're switching AWAY
        // from them. The provider classes don't auto-release on
        // SwitchProviderAsync — they keep weights loaded for fast
        // toggle-back. We don't want that here; the whole point of
        // alignment is to NOT have two big models loaded.
        var oldProviderType = providerManager.ActiveProviderType;
        bool switchingAwayFromLocal =
            (oldProviderType == Models.AiProviderType.Local ||
             oldProviderType == Models.AiProviderType.LlamaServer) &&
            providerType != Models.AiProviderType.Local &&
            providerType != Models.AiProviderType.LlamaServer;

        if (switchingAwayFromLocal)
        {
            try { await llamaServer.StopServerAsync(); } catch { /* best-effort */ }
        }

        // Step 2: persist the model choice into the right slot. Local /
        // LlamaServer read SelectedModelName + SelectedModelPath; cloud
        // / Ollama providers read ProviderConfigs[ProviderId].SelectedModel.
        settingsService.UpdateSettings(s =>
        {
            switch (providerType)
            {
                case Models.AiProviderType.Local:
                case Models.AiProviderType.LlamaServer:
                    s.SelectedModelName = model;
                    if (!string.IsNullOrWhiteSpace(path)) s.SelectedModelPath = path;
                    break;
                default:
                    if (!s.ProviderConfigs.TryGetValue(providerType.ToString(), out var cfg))
                    {
                        cfg = new Models.ProviderConfig();
                        s.ProviderConfigs[providerType.ToString()] = cfg;
                    }
                    cfg.SelectedModel = model;
                    cfg.UseCustomModel = false;
                    break;
            }
        });

        // Step 3: switch provider (also re-initialises). SwitchProviderAsync
        // has three silent-return guards (provider missing, same-instance,
        // already-switching) — none of which we want here. Logging the
        // before/after state to mcp-host.log so we can verify the path
        // taken if something looks off (e.g. settings still showing the
        // previous provider after a successful-looking response).
        var logFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "mcp-host.log");
        var beforeProvider = providerManager.ActiveProviderType;
        var beforeReady = providerManager.ActiveProvider.IsReady;
        try { System.IO.File.AppendAllText(logFile,
            $"{DateTime.Now:O} set_model BEGIN: target={providerType} model={model} " +
            $"before-provider={beforeProvider} before-ready={beforeReady}\n"); } catch {}

        try
        {
            await providerManager.SwitchProviderAsync(providerType, ct);
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(logFile,
                $"{DateTime.Now:O} set_model SWITCH-THREW: {ex.Message}\n"); } catch {}
            return ErrorResult($"SwitchProviderAsync failed: {ex.Message}");
        }

        var midProvider = providerManager.ActiveProviderType;
        var midActive = providerManager.ActiveProvider.GetType().Name;
        try { System.IO.File.AppendAllText(logFile,
            $"{DateTime.Now:O} set_model AFTER-SWITCH: ActiveProviderType={midProvider} " +
            $"ActiveProvider.Type={midActive}\n"); } catch {}

        // If the same-instance / already-switching guard fired and the
        // switch was a no-op, brute-force the settings + re-init the
        // current active provider with the new model. Worst case: the
        // user has to flip the provider in CluadeX's UI manually.
        if (midProvider != providerType)
        {
            settingsService.UpdateSettings(s => s.ActiveProvider = providerType);
            try { System.IO.File.AppendAllText(logFile,
                $"{DateTime.Now:O} set_model FORCE-WRITE settings.ActiveProvider={providerType}\n"); } catch {}
        }
        await providerManager.ReinitializeActiveAsync(ct);

        // Step 4: confirm by reading back the active state. Even if the
        // provider initialise failed (no Ollama daemon, missing API key),
        // we return what we ended up at so the orchestrator knows.
        bool ready = providerManager.ActiveProvider.IsReady;
        var result = new
        {
            ok = ready,
            provider = providerManager.ActiveProviderType.ToString(),
            model,
            path = settingsService.Settings.SelectedModelPath,
            ready,
            note = ready
                ? $"Switched to {providerManager.ActiveProviderType}:{model}"
                : $"Switched to {providerManager.ActiveProviderType}:{model} but provider is not ready " +
                  "— is the daemon running / API key set?",
            freedLocalWeights = switchingAwayFromLocal,
        };
        return TextResult(System.Text.Json.JsonSerializer.Serialize(result));
    }

    private async Task<McpToolResult> HandleReviewAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        var chatVm = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();

        string taskId = ReadString(args, "taskId") ?? Guid.NewGuid().ToString("N")[..8];
        string diff = ReadString(args, "diff") ?? "";
        string intent = ReadString(args, "intent") ?? "";

        if (string.IsNullOrWhiteSpace(diff))
            return ErrorResult("'diff' is required");

        // Use the existing `/review-pr` skill if present; otherwise build an
        // ad-hoc review prompt. Either way we still spawn a visible session
        // so the user can scroll back and audit what the worker decided.
        string spec =
            $"Review the following change for bugs, security issues, missing tests, and style fit.\n" +
            $"Original intent: {intent}\n\n" +
            "Reply in this format:\n" +
            "  - Verdict: approved | revise | rejected\n" +
            "  - Severity: ok | nit | warn | critical\n" +
            "  - Comments: bullet list of specific issues with file:line refs where possible\n" +
            "  - Suggested patch: minimal diff to address the comments (or 'none')\n\n" +
            "## Diff\n```diff\n" + diff + "\n```";

        string finalText = await chatVm.RunMcpTaskAsync(
            taskId: $"{taskId}-review",
            spec: spec,
            lessons: null,
            contextFiles: null,
            workingDirectory: null,
            ct: ct);
        return TextResult(finalText);
    }

    // ─── Tool result helpers ─────────────────────────────────────────────
    private static McpToolResult TextResult(string text) => new()
    {
        Content = { new McpContentItem { Type = "text", Text = text } },
        IsError = false,
    };

    private static McpToolResult ErrorResult(string message) => new()
    {
        Content = { new McpContentItem { Type = "text", Text = message } },
        IsError = true,
    };

    private static string? ReadString(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        return v.ToString();
    }

    private static IReadOnlyList<string>? ReadStringArray(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
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
        services.AddSingleton<SessionMemoryService>();
        services.AddSingleton<HookService>();
        services.AddSingleton<McpServerManager>();
        // McpHostService — exposes CluadeX's coding agent as an MCP server over
        // a per-user named pipe (\\.\pipe\cluadex-mcp). Used by ObsidianX's
        // Co-Pilot Arena to delegate write/run/review tasks while keeping the
        // chat visible inside CluadeX.
        services.AddSingleton<McpHostService>();
        // TimeMachineService — git-backed commit timeline + safe rewind with
        // auto-stash and snapshot branches. Drives the Time Machine view.
        services.AddSingleton<TimeMachineService>();
        // CodeWorkspaceService — backs the Code Editor page (file tree,
        // open/save tabs, git status enrichment for tree badges).
        services.AddSingleton<CodeWorkspaceService>();
        // SubAgentService — Sprint 1 #1: registry of specialised subagents
        // (code-reviewer, security-reviewer, architect, ...). Built-in 10
        // plus discovery of ~/.cluadex/agents/*.md and project agents.
        services.AddSingleton<SubAgentService>();
        // HexEditorService — binary file backend for both the Hex Editor view
        // and the AI agent's hex_* tools. Shared instance so AI patches show
        // up live in the UI and vice versa.
        services.AddSingleton<HexEditorService>();

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
        services.AddSingleton<TimeMachineViewModel>();
        services.AddSingleton<HexEditorViewModel>();
        services.AddSingleton<CodeEditorViewModel>();
        services.AddSingleton<SubAgentsViewModel>();
        services.AddSingleton<SkillsViewModel>();

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
