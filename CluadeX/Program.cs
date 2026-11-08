using System;
using System.Diagnostics;

namespace CluadeX;

/// <summary>
/// Explicit entry point so Velopack's install/update/uninstall lifecycle hooks run
/// as the VERY first thing in the process — before any WPF machinery exists.
///
/// Velopack fast-exits the process during those hooks. Running it from
/// App.OnStartup would mean an Application object and a dispatcher already exist
/// when it does, which Velopack warns against, and on this app OnStartup also
/// claims the single-instance mutex — a hook that exits while holding it would
/// leave the next launch thinking another copy is running.
/// </summary>
public static class Program
{
    /// <summary>Velopack reported this launch as the first one after a fresh install (its OnFirstRun hook) —
    /// one of the two signals that show the setup wizard (the other: no settings file before this launch).</summary>
    public static bool VelopackFirstRun { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // The safety hook Claude Dev runs before every tool call: headless, before Velopack and WPF, never a window.
        if (args.Length > 0 && args[0] == Services.Safety.GuardHook.Flag)
        {
            Environment.Exit(Services.Safety.GuardHook.Run());
            return;
        }
        // Antigravity Dev's hooks (agy's PreToolUse / PreInvocation): the same guard in agy's protocol — headless too.
        if (args.Length > 0 && args[0] == Services.Safety.AntigravityGuard.Flag)
        {
            Environment.Exit(Services.Safety.AntigravityGuard.Run(args));
            return;
        }
        // A connection (GitHub, Stripe, Canva…) started by BrainX's bridge or CluadeX's MCP list: reads its key now and
        // relays the real server's stdio — headless, so the configs naming it never hold a key.
        if (args.Length > 0 && args[0] == Services.Connections.ConnectorLauncher.Flag)
        {
            Environment.Exit(Services.Connections.ConnectorLauncher.Run(args));
            return;
        }
        // Claude Dev's Stop hook: the effort the turn really ran at, for the chat's model/effort label. Headless, never blocks.
        if (args.Length > 0 && args[0] == Services.Safety.GuardHook.EffortFlag)
        {
            Environment.Exit(Services.Safety.GuardHook.RunEffortReport());
            return;
        }
        // A CLI's sub-agent spawn: the hook asks the running app, which refuses it or sets its model and worktree. Headless.
        if (args.Length > 0 && args[0] == Services.Safety.GuardHook.AgentGateFlag)
        {
            Environment.Exit(Services.Safety.GuardHook.RunAgentGate());
            return;
        }
        // CluadeX's chat tools (open_project / new_chat / close_chat) as an MCP server for a Claude Dev run — headless too.
        if (args.Length > 0 && args[0] == Services.Tools.ChatToolBridge.Flag)
        {
            Environment.Exit(Services.Tools.ChatToolBridge.RunStdioServer());
            return;
        }
        // Chrome started this as the CluadeX Bridge extension's native host: relays to the running app — headless too.
        if (Services.Chrome.ChromeNativeHost.IsHostCall(args))
        {
            Environment.Exit(Services.Chrome.ChromeNativeHost.Run(args));
            return;
        }

        // Windows' "Uninstall" button (UninstallEntry points it here): the uninstall page — no Velopack startup, no
        // single-instance lock, no services, no log in ~/.cluadex (the page may be about to delete that folder).
        if (args.Length > 0 && string.Equals(args[0], Services.Uninstall.UninstallEntry.Flag, StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(Services.Uninstall.UninstallHost.Run(args));
            return;
        }

        try
        {
            // Velopack's own startup apply is ON by default: it would install any downloaded package right here, before
            // the splash — past the circuit breaker (a paused version would be retried, uncounted, on every launch),
            // without the bounded save before the restart, and with no Skip. The splash is the one automatic installer
            // at startup (SplashWindow.RunUpdateAsync reads the package from disk); the exit-time install
            // (AutoUpdateService.OnAppShutdown) and Install & Restart start Update.exe themselves and are unaffected.
            Velopack.VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                // Only this install's Chrome host: the key is shared by every copy on the PC (UnregisterIfFor).
                .OnBeforeUninstallFastCallback(_ =>
                {
                    if (Services.Uninstall.UninstallEntry.Detect() is { } inst) Services.Chrome.ChromeBridgeInstall.UnregisterIfFor(inst.Root);
                    else Services.Chrome.ChromeBridgeInstall.Unregister();
                })
                .OnFirstRun(_ => VelopackFirstRun = true)
                .Run();
        }
        catch (Exception ex) { Debug.WriteLine($"Velopack init: {ex.Message}"); }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
