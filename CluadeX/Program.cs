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
        try { Velopack.VelopackApp.Build().Run(); }
        catch (Exception ex) { Debug.WriteLine($"Velopack init: {ex.Message}"); }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
