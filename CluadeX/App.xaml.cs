using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Background;
using CluadeX.Services.Browser;
using CluadeX.Services.Chrome;
using CluadeX.Services.Desktop;
using CluadeX.Services.Mcp;
using CluadeX.ViewModels;

namespace CluadeX;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private System.Threading.Mutex? _singleInstanceMutex;

    /// <summary>Directory crash logs are written to. Kept lightweight (no directory-exists check on each access).</summary>
    private static string CrashLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cluadex", "crash-logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A test harness that creates App only for its XAML resources (chattest) gets exactly that. WPF calls
        // OnStartup as soon as the harness's dispatcher runs, and this method then built a SECOND DI container
        // (the harness's Thai UI checks read the wrong LocalizationService), showed MainWindow, auto-connected the
        // installed BrainX against the user's vault, opened the real pipe, called the license server and rewrote
        // ~/.cluadex/install.json to point at the harness exe. Only the entry assembly is checked here, so the app
        // still starts as "dotnet CluadeX.dll"; install.json additionally requires CluadeX.exe (WriteInstallMarker).
        if (!McpHostService.IsCluadeXApp()) return;

        // ─── Single instance ───
        // Two CluadeX processes fight over the SAME settings.json/session DB, and the second
        // instance's llama-server startup sweep kills the first instance's (still-owned) server —
        // observed live as "llama-server stopped (crashed or was killed)" in the first window.
        // A PORTABLE copy (portable.txt next to the exe) keeps its own data folder, so it has its own lock and can run
        // beside the installed app — how a test copy is checked without closing the owner's CluadeX.
        string lockName = @"Local\CluadeX_SingleInstance";
        try
        {
            string root = Services.SettingsService.ResolveDataRoot(out bool portable);
            if (portable)
                lockName += "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12];
        }
        catch { }
        _singleInstanceMutex = new System.Threading.Mutex(true, lockName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // The installed app and a dev build share one data folder, so only one may run — but say which
            // one is running, or "already running" sends the user hunting for the wrong window.
            string other = "";
            try
            {
                int me = Environment.ProcessId;
                var running = System.Diagnostics.Process.GetProcessesByName("CluadeX").FirstOrDefault(p => p.Id != me);
                string? path = running?.MainModule?.FileName;
                if (path != null)
                {
                    bool installed = path.Contains(@"\AppData\Local\CluadeX\current\", StringComparison.OrdinalIgnoreCase);
                    other = (installed ? "\n\nRunning: installed CluadeX / ตัวที่ติดตั้ง" : "\n\nRunning: DEV build / ตัว dev")
                            + $"\n{path}";
                }
            }
            catch { /* best effort */ }
            MessageBox.Show("CluadeX is already running — close it first; two copies can't share the same chats and settings."
                + "\n(CluadeX เปิดอยู่แล้ว — ปิดตัวนั้นก่อน เพราะสองตัวใช้แชทและการตั้งค่าชุดเดียวกัน)" + other,
                "CluadeX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
        // Restore points: created now so agent tools can reach it; the 5 GB cap is enforced off the UI thread.
        var checkpoints = _serviceProvider.GetRequiredService<CheckpointService>();
        _ = Task.Run(checkpoints.Prune);
        // The Watch page for the phone comes back on if it was on when the app closed.
        try { var watchWeb = _serviceProvider.GetRequiredService<WatchWebServer>(); if (watchWeb.Settings.Enabled) watchWeb.Start(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("watch web: " + ex.Message); }

        // Panel widths the user dragged (sidebar, explorer, chat pane, side panel) come back after a restart.
        var panelSettings = _serviceProvider.GetRequiredService<SettingsService>();
        CluadeX.Views.ResizeGrip.Load = key => panelSettings.Settings.PanelWidths.TryGetValue(key, out var w) ? w : null;
        CluadeX.Views.ResizeGrip.Save = (key, w) => panelSettings.UpdateSettings(s => s.PanelWidths[key] = w);

        // Async commands catch their own exceptions so async-void can't kill the process — but that
        // made a throwing command look like a dead button with nothing in any log. Route those
        // failures to the same debug log + crash log the sync path already uses.
        CluadeX.ViewModels.CommandErrorSink.Handler = (source, ex) =>
        {
            WriteCrashLog("command", ex);
            TryLogToDebugService(CluadeX.Models.LogLevel.Error, "Command",
                $"{source} failed: {ex.Message}", ex);
        };

        // The saved theme goes on screen before the first window exists, so nothing is drawn twice. A theme that
        // fails to load (or a damaged setting) leaves the default Midnight in place — never a startup failure.
        try { _serviceProvider.GetRequiredService<ThemeService>().ApplySaved(); }
        catch (Exception ex) { WriteCrashLog("theme", ex); }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        // Shown after the splash (end of this method), once the startup checks have run.

        // Ensure skill directories exist
        try
        {
            string skillsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "skills");
            System.IO.Directory.CreateDirectory(skillsDir);
        }
        catch { }

        // Resolve the debug log first so subsequent startup steps can log
        // their progress / failures. If DI itself failed, this is null and
        // each helper TryLogToDebugService call no-ops silently.
        var dbg = _serviceProvider.GetService<DebugLogService>();
        dbg?.Info("App", "MainWindow shown, starting background services");
        CluadeX.Helpers.UiStallWatchdog.Start(Dispatcher, dbg);

        // Initialize background services (non-blocking)
        _ = Task.Run(async () =>
        {
            // License on its own task: up to four calls to xman4289.com (15 s timeout each when the network is
            // bad) must not hold up the MCP servers below.
            LicenseStartup = Task.Run(async () =>
            {
                try
                {
                    var activation = _serviceProvider!.GetRequiredService<ActivationService>();
                    // Validate license key against online API
                    await activation.ValidateOnlineAsync();
                    dbg?.Debug("Activation", "License validation finished");
                    // Register this PC with xman4289.com, take back a license it already holds, and give a
                    // brand-new PC its free trial key straight away.
                    await activation.AutoProvisionAsync();
                }
                catch (Exception ex)
                {
                    dbg?.Warn("Activation", "License check threw (offline?)", ex);
                }
            });

            // MCP servers on their own task too: a server that never answers initialize (an npx package still
            // downloading, mcp-remote waiting for a login) can hold InitializeAsync for its whole startup timeout, and
            // the pipe host, install.json and the bundled hooks below must not wait for that. A pipe task that arrives
            // meanwhile waits for it briefly (WaitForMcpStartupAsync) and its caller is told when it was not ready.
            var mcpStartup = Task.Run(async () =>
            {
                try
                {
                    // Initialize MCP servers
                    var mcpManager = _serviceProvider.GetRequiredService<McpServerManager>();

                    // Surface the manager on MainViewModel BEFORE starting anything, so the
                    // status chip shows "connecting…" and then whatever really happens —
                    // including a start that fails outright. Binding it afterwards would
                    // hide exactly the case the chip exists for.
                    Dispatcher.Invoke(() =>
                    {
                        var mainVm = _serviceProvider!.GetRequiredService<ViewModels.MainViewModel>();
                        mainVm.McpServers = mcpManager;
                    });

                    await mcpManager.InitializeAsync();
                    // Claude in Chrome switched on earlier but its entry is gone (a fresh mcp_servers.json): put it back.
                    try { await _serviceProvider.GetRequiredService<Services.Chrome.ClaudeInChromeConnector>().EnsureAsync(); }
                    catch (Exception ex) { dbg?.Warn("Chrome", "Claude in Chrome could not be restored", ex); }
                    dbg?.Info("MCP", $"McpServerManager initialised · brain={mcpManager.BrainStatusText}");

                    if (mcpManager.ConfigLoadFailed)
                        dbg?.Warn("MCP", "mcp_servers.json could not be read — it is left untouched and nothing is written to it this session");

                    // BrainX installed but not in our MCP list (installed after CluadeX, or before it existed):
                    // connect it without asking — that is what installing it meant — unless the user removed it /
                    // said "Not interested" before, or mcp_servers.json can't be read (registering means writing it).
                    // Not installed: invite, once.
                    var auto = await mcpManager.AutoConnectInstalledBrainAsync();
                    var brainSettings = _serviceProvider.GetRequiredService<SettingsService>().Settings;
                    if (auto != Services.Mcp.McpServerManager.BrainAutoConnect.NotNeeded)
                    {
                        dbg?.Info("MCP", $"installed BrainX startup check: {auto} · {mcpManager.BrainStatusText}");
                    }
                    else if (mcpManager.GetBrainInstallState() == Services.Mcp.McpServerManager.BrainInstall.NotInstalled
                             && !brainSettings.BrainXInviteDismissed && !brainSettings.BrainXAutoConnectDeclined)
                    {
                        _ = Dispatcher.BeginInvoke(() => _serviceProvider!.GetRequiredService<ViewModels.MainViewModel>().ScheduleBrainXInvite());
                    }
                }
                catch (Exception ex)
                {
                    dbg?.Warn("MCP", "McpServerManager init failed", ex);
                }
            });
            try { _serviceProvider.GetRequiredService<McpServerManager>().TrackStartup(mcpStartup); }
            catch (Exception ex) { dbg?.Warn("MCP", "could not track the MCP startup", ex); }

            // Batches submitted before the last exit are still running (and paid for): pick their polling back up.
            try { _serviceProvider.GetRequiredService<Services.Batch.BatchService>().EnsurePolling(); }
            catch (Exception ex) { dbg?.Warn("Batch", "could not resume batch polling", ex); }

            try
            {
                // Start the in-process MCP host (named pipe).
                var host = _serviceProvider.GetRequiredService<McpHostService>();

                // Wire the tool dispatcher BEFORE accepting traffic so the
                // first request never hits a "not wired" error. Each tool
                // routes to ChatViewModel which spawns a visible session.
                host.ToolDispatcher = (invocation, ct) => HandleMcpToolAsync(invocation, ct);
                await host.StartAsync();
                dbg?.Info("MCP", $"McpHostService listening on pipe '{host.PipeName}'");

                // Self-register a discovery marker (<data root>\install.json) so an external orchestrator
                // (e.g. BrainX's CluadeXLauncher) can find THIS install's exe + pipe without guessing paths.
                WriteInstallMarker(host);

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
                dbg?.Error("MCP", "McpHostService start failed", ex);
            }

            // ─── HookBundleService: deploy bundled .ps1 scripts + project enabled set ───
            // Resolving the service triggers its ctor which copies/refreshes the
            // ~/.cluadex/hooks-bundled/ directory and writes hooks-bundled.json.
            try
            {
                _ = _serviceProvider.GetRequiredService<HookBundleService>();
                dbg?.Info("Hooks", "HookBundleService initialised");
            }
            catch (Exception ex)
            {
                dbg?.Warn("Hooks", "HookBundleService init failed", ex);
            }

            // ─── SessionStart hook ───
            // Fire once at startup. Best-effort: hook failures must not block app.
            try
            {
                var hooks = _serviceProvider.GetRequiredService<HookService>();
                // Workspace-trust prompt: if the opened project defines auto-run hooks and the folder
                // isn't trusted yet, offer to trust it (once per folder per session) instead of silently
                // running shell scripts from a possibly-hostile cloned repo.
                var promptedHookFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                hooks.OnUntrustedProjectHooks += folder =>
                {
                    lock (promptedHookFolders) { if (!promptedHookFolders.Add(folder)) return; }
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        var r = System.Windows.MessageBox.Show(
                            $"This project defines automation hooks that run shell commands automatically:\n\n{folder}\\.cluadex\\hooks.json\n\nOnly enable hooks from projects you trust. Trust and enable this project's hooks?",
                            "Workspace trust — untrusted project hooks",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);
                        if (r == System.Windows.MessageBoxResult.Yes) hooks.TrustProjectHooks(folder);
                    });
                };
                await hooks.ExecuteSessionStartHooksAsync(new HookSessionContext());
                dbg?.Debug("Hooks", "SessionStart hooks fired");
            }
            catch (Exception ex)
            {
                dbg?.Warn("Hooks", "SessionStart hooks threw", ex);
            }
        });

        // ─── Splash: the startup checks, shown — then the app ───
        // Modal, so OnStartup waits here while the background services above (license, MCP, hooks) run; the splash
        // reports them as they resolve. A splash that fails to show never blocks the app.
        try
        {
            var splash = new Views.SplashWindow(_serviceProvider);
            splash.ShowDialog();
        }
        catch (Exception ex) { WriteCrashLog("splash", ex); }
        mainWindow.Show();
    }

    /// <summary>The startup license check (validate + auto-provision), awaited by the splash instead of repeated.</summary>
    public static Task? LicenseStartup { get; private set; }

    // ════════════════════════════════════════════════════════════════════
    //  MCP tool dispatcher
    //  ────────────────────
    //  Called from the named-pipe accept loop on a background thread. The
    //  tool implementations themselves call ChatViewModel which marshals
    //  to the dispatcher internally — so nothing UI-touching runs here at
    //  this top level.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Write install.json (exe path, version, pipe name, token file) into the app's data root — ~/.cluadex for an
    /// installed app, &lt;exe&gt;\Data for a portable copy — so a launcher/orchestrator can discover and start CluadeX
    /// and connect to its named pipe without hard-coding paths. Only CluadeX.exe itself writes it (a harness never
    /// does), and every start of the installed app rewrites it, which repairs a marker a harness left behind.
    /// Best-effort — never blocks startup.
    /// </summary>
    private static void WriteInstallMarker(McpHostService host)
    {
        if (!McpHostService.WriteInstallMarker(host.DataDirectory, host.PipeName, host.TokenFilePath))
            System.Diagnostics.Debug.WriteLine("install.json not written (not the CluadeX app, or the data folder refused the write)");
    }

    /// <summary>How long an agent task from the pipe waits for the startup MCP servers (BrainX) before it runs anyway.</summary>
    private static readonly TimeSpan McpStartupWait = TimeSpan.FromSeconds(15);

    private async Task<McpToolResult> HandleMcpToolAsync(McpToolInvocation invocation, CancellationToken ct)
    {
        try
        {
            // Every call in the debug log (not its spec — that can hold anything): when a chat starts or the model changes
            // with nobody at the keyboard, the log says whether a pipe caller did it (owner 2026-10-17: "แชทถูกปลุกกลับมา").
            if (invocation.ToolName != "cluadex.get_active_model")
            {
                string Arg(string n) => invocation.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
                    && invocation.Arguments.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                // The chat a call is about (list_chats / continue_chat) — its id and project, never the message.
                string chat = invocation.ToolName is "cluadex.list_chats" or "cluadex.continue_chat"
                    ? $" chatId={Arg("chatId")} project={Arg("project")}" : "";
                _serviceProvider?.GetService<DebugLogService>()?.Info("Pipe",
                    $"{invocation.ToolName} taskId={Arg("taskId")} provider={Arg("provider")} model={Arg("model")} dir={Arg("workingDirectory")}{chat}");
            }
            // The pipe accepts tasks before the MCP servers finished starting: an agent task waits for them briefly,
            // and when they are still not ready its caller is told (its run may have had no brain / MCP tools).
            string? startupNote = null;
            if (invocation.ToolName is "cluadex.write_code" or "cluadex.run_skill" or "cluadex.review" or "cluadex.continue_chat"
                && _serviceProvider?.GetService<McpServerManager>() is { } mcpManager
                && !await mcpManager.WaitForStartupAsync(McpStartupWait, ct))
                startupNote = McpServerManager.StartupNotReadyNote;

            var result = invocation.ToolName switch
            {
                "cluadex.write_code"        => await HandleWriteCodeAsync(invocation.Arguments, ct),
                "cluadex.run_skill"         => await HandleRunSkillAsync(invocation.Arguments, ct),
                "cluadex.review"            => await HandleReviewAsync(invocation.Arguments, ct),
                "cluadex.get_active_model"  => await HandleGetActiveModelAsync(ct),
                "cluadex.set_model"         => await HandleSetModelAsync(invocation.Arguments, ct),
                "cluadex.choose_model"      => await HandleChooseModelAsync(invocation.Arguments),
                "cluadex.list_chats"        => await HandleListChatsAsync(invocation.Arguments),
                "cluadex.continue_chat"     => await HandleContinueChatAsync(invocation.Arguments, ct),
                _ => ErrorResult($"Unknown tool '{invocation.ToolName}'"),
            };
            if (startupNote != null)
                result.Content.Add(new McpContentItem { Type = "text", Text = $"({startupNote})" });
            return result;
        }
        catch (OperationCanceledException)
        {
            return ErrorResult("Cancelled by caller");
        }
        catch (Exception ex)
        {
            // The caller gets the kind of failure; the details (paths, stack) go to the debug log only.
            TryLogToDebugService(CluadeX.Models.LogLevel.Error, "MCP", $"pipe tool '{invocation.ToolName}' threw", ex);
            return ErrorResult($"Tool '{invocation.ToolName}' failed ({ex.GetType().Name}) — details are in CluadeX's Debug Log.");
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
        if (!TryReadMaxSteps(args, out int? maxSteps, out string? stepsError))
            return ErrorResult(stepsError!);
        bool? acceptEdits = ReadBool(args, "acceptEdits");

        var outcome = await chatVm.RunMcpTaskAsync(taskId, spec, lessons, contextFiles, cwd, ct, maxSteps, acceptEdits);
        return ToMcpResult(outcome);
    }

    /// <summary>A run that failed, hit its step limit, was stopped in CluadeX or lost its caller is an error result —
    /// it used to come back as isError=false, indistinguishable from success.</summary>
    private static McpToolResult ToMcpResult(ViewModels.McpTaskOutcome outcome)
        => outcome.IsError ? ErrorResult(outcome.Text) : TextResult(outcome.Text);

    /// <summary>Optional "maxSteps": an integer 0-100000 (a numeric string is accepted); 0 = no step limit for this run,
    /// like the setting's 0 (owner 2026-10-16: long tasks must not stop at a cap). Absent = the setting.</summary>
    private static bool TryReadMaxSteps(System.Text.Json.JsonElement args, out int? maxSteps, out string? error)
    {
        maxSteps = null;
        error = null;
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object || !args.TryGetProperty("maxSteps", out var v)
            || v.ValueKind == System.Text.Json.JsonValueKind.Null)
            return true;
        int n;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out n)) { }
        else if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out n)) { }
        else { error = "'maxSteps' must be an integer from 0 (no limit) to 100000"; return false; }
        if (n < 0 || n > CodeAgentService.UnlimitedSteps) { error = "'maxSteps' must be an integer from 0 (no limit) to 100000"; return false; }
        maxSteps = n == 0 ? CodeAgentService.UnlimitedSteps : n;
        return true;
    }

    /// <summary>true/false (or "true"/"false"); anything else = not given.</summary>
    private static bool? ReadBool(System.Text.Json.JsonElement args, string name)
    {
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String when bool.TryParse(v.GetString(), out bool b) => b,
            _ => null,
        };
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
        // Carry the skill's tool whitelist across. Flattening the skill into a
        // plain spec dropped it silently, so a skill invoked over the pipe ran
        // UNRESTRICTED — /brainx-tester, whose entire point is that it cannot
        // write files, happily called write_file (observed 2026-07-31). The
        // in-app path sets this from SendMessage; the pipe path had no equivalent.
        if (!TryReadMaxSteps(args, out int? maxSteps, out string? stepsError))
            return ErrorResult(stepsError!);
        var toolService = _serviceProvider.GetRequiredService<Services.AgentToolService>();
        if (skill.AllowedTools is { Count: > 0 })
            toolService.ActiveSkillAllowedTools = skill.AllowedTools;

        try
        {
            var outcome = await chatVm.RunMcpTaskAsync(
                taskId: $"{taskId}-skill-{skillName}",
                spec: sb.ToString(),
                lessons: null,
                contextFiles: null,
                workingDirectory: cwd,
                ct: ct,
                maxSteps: maxSteps);
            return ToMcpResult(outcome);
        }
        finally
        {
            // Never leak a restriction into the next chat the owner types by hand.
            toolService.ActiveSkillAllowedTools = null;
        }
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

        // What the owner can pick from another app (BrainX's cowork room): the chat page's own model box — API
        // models that have a key — and its effort levels. Read on the UI thread, where the chat keeps them.
        object[] options = Array.Empty<object>();
        string effort = "";
        IReadOnlyList<string> efforts = Array.Empty<string>();
        try
        {
            var chat = _serviceProvider.GetService<ViewModels.ChatViewModel>();
            if (chat != null)
                Dispatcher.Invoke(() =>
                {
                    options = chat.ChatModelChoices
                        .Where(c => c.Provider != Models.AiProviderType.BrainX)   // never offered to the room itself
                        .Select(c => (object)new { provider = c.Provider.ToString(), model = c.Model, label = c.ProviderLabel, price = c.PriceLabel })
                        .ToArray();
                    effort = chat.ReasoningEffort;
                    efforts = chat.EffortOptions;
                });
        }
        catch { /* the model report must not fail over the extras */ }

        var payload = new
        {
            provider = providerType.ToString(),
            model = modelName ?? "",
            path = modelPath ?? "",
            ready,
            vramUsedMB = vramUsedMb,
            vramTotalMB = vramTotalMb,
            effort,
            options,
            efforts,
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
        // The host's own log, in the data root (a portable copy keeps it next to the exe).
        var logFile = System.IO.Path.Combine(settingsService.DataRoot, "mcp-host.log");
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

        var outcome = await chatVm.RunMcpTaskAsync(
            taskId: $"{taskId}-review",
            spec: spec,
            lessons: null,
            contextFiles: null,
            workingDirectory: null,
            ct: ct);
        return ToMcpResult(outcome);
    }

    /// <summary>
    /// BrainX's cowork room picked the chat's model and/or effort. Unlike set_model (Co-Pilot alignment, any provider
    /// including a model on this PC) this goes through the chat page's model box and its rules — see
    /// ChatViewModel.ChooseFromOutsideAsync. Returns { ok, model, effort, reason }.
    /// </summary>
    private async Task<McpToolResult> HandleChooseModelAsync(System.Text.Json.JsonElement args)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        string? model = ReadString(args, "model");
        string? effort = ReadString(args, "effort");
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(effort))
            return ErrorResult("'model' or 'effort' is required");

        var chat = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();
        string? reason = await await Dispatcher.InvokeAsync(() => chat.ChooseFromOutsideAsync(model, effort));
        string nowModel = "", nowEffort = "";
        Dispatcher.Invoke(() => { nowModel = chat.SelectedChatModel?.Model ?? ""; nowEffort = chat.ReasoningEffort; });
        return TextResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = reason == null,
            model = nowModel,
            effort = nowEffort,
            reason = reason ?? "",
        }));
    }

    /// <summary>The chats there are, newest first — for carrying one on with continue_chat. Read-only: nothing on screen
    /// changes. Returns a JSON array of { id, title, project, updatedAt, running, paused }.</summary>
    private async Task<McpToolResult> HandleListChatsAsync(System.Text.Json.JsonElement args)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        if (!TryReadInt(args, "limit", 1, MaxListChats, out int? limit))
            return ErrorResult($"'limit' must be an integer from 1 to {MaxListChats}");
        var chat = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();
        var chats = await chat.ListChatsForPipeAsync(ReadString(args, "project"), ReadString(args, "query"), limit ?? 20);
        return TextResult(System.Text.Json.JsonSerializer.Serialize(chats.Select(c => new
        {
            id = c.Id,
            title = c.Title,
            project = c.Project,
            updatedAt = new DateTimeOffset(c.UpdatedAt).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            running = c.Running,
            paused = c.Paused,
        })));
    }

    private const int MaxListChats = 200;

    /// <summary>Carry on an existing chat: open it, send the message as the user, return the turn's reply.</summary>
    private async Task<McpToolResult> HandleContinueChatAsync(System.Text.Json.JsonElement args, CancellationToken ct)
    {
        if (_serviceProvider == null) return ErrorResult("DI not initialised");
        string? chatId = ReadString(args, "chatId");
        string? title = ReadString(args, "title");
        string message = ReadString(args, "message") ?? "";
        string? provider = ReadString(args, "provider");
        string? model = ReadString(args, "model");
        if (string.IsNullOrWhiteSpace(chatId) && string.IsNullOrWhiteSpace(title))
            return ErrorResult("'chatId' (or 'title') is required — cluadex.list_chats lists the chats");
        if (string.IsNullOrWhiteSpace(message))
            return ErrorResult("'message' is required");
        if (!TryReadMaxSteps(args, out int? maxSteps, out string? stepsError))
            return ErrorResult(stepsError!);
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (string.IsNullOrWhiteSpace(model))
                return ErrorResult("'provider' needs 'model' — a model id from cluadex.get_active_model's options");
            if (!Enum.TryParse<Models.AiProviderType>(provider.Trim(), ignoreCase: true, out _))
                return ErrorResult($"Unknown provider '{provider}'. Valid: " + string.Join(", ", Enum.GetNames<Models.AiProviderType>()));
        }

        var chat = _serviceProvider.GetRequiredService<ViewModels.ChatViewModel>();
        var outcome = await chat.ContinueChatForPipeAsync(chatId, title, message, maxSteps, provider, model, ct);
        return ToMcpResult(outcome);
    }

    /// <summary>Optional integer argument in [min, max] (a numeric string is accepted). Absent/null = null, true.</summary>
    private static bool TryReadInt(System.Text.Json.JsonElement args, string name, int min, int max, out int? value)
    {
        value = null;
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object || !args.TryGetProperty(name, out var v)
            || v.ValueKind == System.Text.Json.JsonValueKind.Null)
            return true;
        int n;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out n)) { }
        else if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), out n)) { }
        else return false;
        if (n < min || n > max) return false;
        value = n;
        return true;
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
        services.AddSingleton<Services.Pet.PetService>();
        services.AddSingleton<LocalizationService>();
        // ThemeService — swaps Resources/Themes/<id>.xaml in Application.Resources at runtime (Settings › General).
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ActivationService>();
        services.AddSingleton<XmanLicenseService>();
        // DgxSparkService — CluadeX driving the model an NVIDIA DGX Spark serves on the LAN (Ollama).
        services.AddSingleton<DgxSparkService>();
        services.AddSingleton<BugReportService>();
        services.AddSingleton<AutoUpdateService>();
        services.AddSingleton<LspClientService>();
        services.AddSingleton<SkillService>();
        services.AddSingleton<CostTrackingService>();
        services.AddSingleton<MemoryService>();
        // BatchService — half-price batch API jobs (Anthropic / OpenAI), kept in batches.json and polled until done.
        services.AddSingleton<Services.Batch.BatchService>();
        services.AddSingleton<SessionMemoryService>();
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<WatchService>();
        services.AddSingleton<WatchWebServer>();
        services.AddSingleton<RepoMapService>();
        services.AddSingleton<EmbeddingService>();
        services.AddSingleton<AutonomousCodingService>();
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
        // CodeIntelligenceService — search across files + go-to-definition /
        // find-references / rename for the workbench. Works with nothing
        // installed (bounded workspace scan) and upgrades to real semantic
        // results whenever LspClientService has a language server connected.
        services.AddSingleton<CodeIntelligenceService>();
        // DebugAdapterService — DAP client (same stdio framing as LSP) driving the workbench
        // debugger. Adapters are not bundled: it probes for netcoredbg / debugpy and reports
        // exactly what is installed rather than offering a Start button that does nothing.
        services.AddSingleton<DebugAdapterService>();
        // SubAgentService — Sprint 1 #1: registry of specialised subagents
        // (code-reviewer, security-reviewer, architect, ...). Built-in 10
        // plus discovery of ~/.cluadex/agents/*.md and project agents.
        services.AddSingleton<SubAgentService>();
        // InstinctService — Sprint 2 #1: continuous learning. JSON-backed
        // store of observed patterns, accept/reject voting, confidence
        // scoring (success_rate × frequency × recency_decay), promote-to-skill.
        services.AddSingleton<InstinctService>();
        // DebugLogService — central in-app log with ring buffer + daily
        // rotated file at %USERPROFILE%/.cluadex/logs/. Drives the Debug
        // Log page (Ctrl+9) AND captures global exceptions routed from
        // the dispatcher / appdomain / task handlers above.
        services.AddSingleton<DebugLogService>();
        // BrainSyncService — bridge between local Instincts and the ObsidianX
        // brain via MCP. Pushes STRONG instincts as `coding-lesson` notes so
        // the knowledge survives across machines / Claude Code sessions.
        services.AddSingleton<BrainSyncService>();
        // SecurityShieldService — Sprint 3 #1: static-analysis scanner with
        // 25 ship-1 OWASP-style rules. Drives the SecurityShield page.
        services.AddSingleton<SecurityShieldService>();
        // HookBundleService — Sprint 3 #2: 15 bundled .ps1 hook scripts.
        // On first resolve, deploys scripts to ~/.cluadex/hooks-bundled/ and
        // projects the enabled set into ~/.cluadex/hooks-bundled.json which
        // HookService reads alongside the user's own hooks.json.
        services.AddSingleton<HookBundleService>();
        // HexEditorService — binary file backend for both the Hex Editor view
        // and the AI agent's hex_* tools. Shared instance so AI patches show
        // up live in the UI and vice versa.
        services.AddSingleton<HexEditorService>();

        // Local GGUF backends — registered as singletons so AiProviderManager and
        // LocalGgufProvider share the same LlamaServerProvider instance (otherwise
        // the routing layer would launch a second llama-server process).
        services.AddSingleton<Services.Providers.LlamaServerProvider>();
        services.AddSingleton<Services.Providers.LocalGgufProvider>();

        // Tool images on disk (DataRoot\screens\<chat>\NNN.jpg) — screenshots and MCP images, pruned per chat.
        services.AddSingleton(sp => new Services.Vision.ToolImageStore(sp.GetRequiredService<SettingsService>()));

        // Extension seams. Each team registers its own services in its own file (tool modules as
        // Services.Tools.IAgentToolModule, MCP policies / launch resolvers, shell interceptors, prompt sections,
        // exit work as IAppShutdownHook) — this list is the only line here that names them.
        services.AddBackgroundModule();
        services.AddBrowserModule();
        services.AddDesktopModule();
        services.AddSingleton<SessionCleanupService>();
        services.AddSingleton(sp => new ProjectRegistryService(sp.GetService<FileSystemService>(), sp.GetService<SettingsService>()));
        services.AddSingleton<Services.Tools.IAgentToolModule>(sp => new Services.Tools.ProjectToolModule(sp.GetService<SettingsService>(), sp.GetService<ProjectRegistryService>()));
        services.AddSingleton<Services.Tools.IAgentToolModule>(sp => new Services.Tools.AppRestartToolModule());
        services.AddChromeConnector();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PluginManagerViewModel>();
        services.AddSingleton<PermissionsViewModel>();
        services.AddSingleton<TaskManagerViewModel>();
        services.AddSingleton<WatchViewModel>();
        services.AddSingleton<FeaturesViewModel>();
        services.AddSingleton<McpServersViewModel>();
        services.AddSingleton<TimeMachineViewModel>();
        services.AddSingleton<HexEditorViewModel>();
        services.AddSingleton<CodeEditorViewModel>();
        services.AddSingleton<SubAgentsViewModel>();
        services.AddSingleton<SkillsViewModel>();
        services.AddSingleton<InstinctsViewModel>();
        services.AddSingleton<DebugLogViewModel>();
        services.AddSingleton<SecurityShieldViewModel>();
        services.AddSingleton<HookLibraryViewModel>();

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

            _serviceProvider?.GetService<CostTrackingService>()?.FlushLedger();   // today's spend survives the restart
        }
        catch (Exception ex)
        {
            // Best-effort on exit — still write to debug output for troubleshooting.
            System.Diagnostics.Debug.WriteLine($"OnExit flush failed: {ex.Message}");
        }

        // Exit work registered by the modules (stop background dev servers, close the browser engine) — each hook
        // on its own, so one that throws can't keep the rest from running. Before the container is disposed.
        RunShutdownHooks(_serviceProvider);

        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }

    /// <summary>Run every registered <see cref="IAppShutdownHook"/> once. Never throws.</summary>
    internal static void RunShutdownHooks(IServiceProvider? provider)
    {
        if (provider == null) return;
        IEnumerable<IAppShutdownHook> hooks;
        try { hooks = provider.GetServices<IAppShutdownHook>().ToList(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnExit: shutdown hooks could not be resolved: {ex.Message}");
            return;
        }
        foreach (var hook in hooks)
        {
            try { hook?.OnAppShutdown(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OnExit: {hook?.GetType().Name} failed: {ex.Message}"); }
        }
    }

    // ─── Global Exception Handlers ───

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("dispatcher", e.Exception);
        TryLogToDebugService(CluadeX.Models.LogLevel.Critical, "Dispatcher",
            $"Unhandled dispatcher exception: {e.Exception.Message}", e.Exception);
        // Keep the app alive for non-fatal UI exceptions — losing unsaved chat state is worse than a blip.
        // Fatal exceptions (StackOverflow, OutOfMemory, AccessViolation) can't be caught here anyway.
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nA crash log has been saved to:\n{CrashLogDir}\n\nOpen Debug Log (Ctrl+9) to see context.",
            "CluadeX — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // AppDomain exceptions are typically fatal and the process will terminate after this returns.
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog("appdomain", ex);
            TryLogToDebugService(CluadeX.Models.LogLevel.Critical, "AppDomain",
                $"Unhandled AppDomain exception (process likely terminating): {ex.Message}", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("task", e.Exception);
        TryLogToDebugService(CluadeX.Models.LogLevel.Error, "Task",
            $"Unobserved task exception: {e.Exception.Message}", e.Exception);
        // Mark as observed so GC doesn't terminate the process.
        e.SetObserved();
    }

    /// <summary>
    /// Route global exceptions through DebugLogService so they show up in
    /// the in-app Debug Log page (Ctrl+9) AND get persisted to the daily
    /// rotated log file. Best-effort — never throws.
    /// </summary>
    private void TryLogToDebugService(CluadeX.Models.LogLevel level, string category, string message, Exception ex)
    {
        try
        {
            var log = _serviceProvider?.GetService<DebugLogService>();
            if (log == null) return;
            switch (level)
            {
                case CluadeX.Models.LogLevel.Warning:  log.Warn(category, message, ex); break;
                case CluadeX.Models.LogLevel.Error:    log.Error(category, message, ex); break;
                case CluadeX.Models.LogLevel.Critical: log.Critical(category, message, ex); break;
                default:                               log.Info(category, message); break;
            }
        }
        catch { /* logging must never crash crash handling */ }
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
