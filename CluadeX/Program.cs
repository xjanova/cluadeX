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
    [STAThread]
    public static void Main(string[] args)
    {
        // The safety hook Claude Dev runs before every tool call: headless, before Velopack and WPF, never a window.
        if (args.Length > 0 && args[0] == Services.Safety.GuardHook.Flag)
        {
            Environment.Exit(Services.Safety.GuardHook.Run());
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
        // CluadeX's chat tools (open_project / new_chat / close_chat) as an MCP server for a Claude Dev run — headless too.
        if (args.Length > 0 && args[0] == Services.Tools.ChatToolBridge.Flag)
        {
            Environment.Exit(Services.Tools.ChatToolBridge.RunStdioServer());
            return;
        }

        try { Velopack.VelopackApp.Build().Run(); }
        catch (Exception ex) { Debug.WriteLine($"Velopack init: {ex.Message}"); }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
