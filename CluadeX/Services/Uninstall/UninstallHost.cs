using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CluadeX.ViewModels.Setup;
using CluadeX.Views.Setup;

namespace CluadeX.Services.Uninstall;

/// <summary>What the uninstall page was started with.</summary>
public sealed record UninstallOptions(string DataRoot, bool DataRootOverridden, IReadOnlyList<KeepGroupId>? Keep, bool Yes, bool NoHandoff)
{
    /// <summary>
    ///   CluadeX.exe --uninstall                          the page (what Windows' "Uninstall" runs)
    ///   … --keep settings,chats --yes                     ticks those, skips the questions, closes itself (IT scripts, the test)
    ///   … --data-root D:\copy\.cluadex                    another data folder — refused unless it holds CluadeX data
    ///   … --no-handoff                                    clean only; the program is not removed (the test harness)
    /// </summary>
    public static UninstallOptions Parse(string[] args)
    {
        string root = SettingsService.ResolveDataRoot(out _);
        bool overridden = false, yes = false, noHandoff = false;
        List<KeepGroupId>? keep = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--yes", StringComparison.OrdinalIgnoreCase)) yes = true;
            else if (a.Equals("--no-handoff", StringComparison.OrdinalIgnoreCase)) noHandoff = true;
            else if (a.Equals("--keep", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                keep = UninstallCatalog.ParseKeep(args[++i]);
            else if (a.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string candidate = Path.GetFullPath(args[++i]);
                if (!UninstallCatalog.LooksLikeDataRoot(candidate))
                    throw new ArgumentException($"{candidate} does not look like a CluadeX data folder (no settings.json / codex.db)");
                root = candidate;
                overridden = true;
            }
        }
        return new UninstallOptions(root, overridden, keep, yes, noHandoff);
    }
}

/// <summary>
/// Runs the uninstall page in its own small WPF application — not <see cref="App"/>: no single-instance lock (the
/// installed app may be open, the page closes it), no DI container, no services, no log inside ~/.cluadex. When the
/// page finishes it starts Velopack's <c>Update.exe --uninstall --silent</c>, which removes the program files, its
/// shortcuts and the Installed-apps row once this process has exited. If the page cannot open at all, Windows' button
/// still works: the Velopack command is put back and run (that path keeps the user's data, as Velopack always did).
/// </summary>
public static class UninstallHost
{
    public static int Run(string[] args)
    {
        var install = UninstallEntry.Detect();
        var report = new UninstallReport();
        UninstallOptions opts;
        try { opts = UninstallOptions.Parse(args); }
        catch (Exception ex)
        {
            report.Line("arguments refused: " + ex.Message);
            report.Save();
            return 2;
        }
        report.Line($"CluadeX {AutoUpdateService.CurrentVersion} uninstall page · data {opts.DataRoot} · install {install?.Root ?? "(not an installed copy)"}");

        bool finished = false;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var loc = new LocalizationService();
            loc.SetLanguage(ReadLanguage(opts.DataRoot));
            LocalizedResources.Instance.Initialize(loc);

            var vm = new UninstallViewModel(opts, install, report, loc);
            var win = new UninstallWindow(vm);
            app.Run(win);
            finished = vm.Finished;
        }
        catch (Exception ex)
        {
            report.Line("the page failed: " + ex);
            report.Save();
            if (install != null && !opts.NoHandoff)
            {
                // Never leave Windows' button dead: the old behaviour (program removed, data kept).
                UninstallEntry.RestoreVelopackCommand(install);
                TryStart(install.UpdateExe, "--uninstall", report);
            }
            return 1;
        }

        if (finished && install != null && !opts.NoHandoff)
            TryStart(install.UpdateExe, "--uninstall --silent", report);
        report.Save();
        return finished ? 0 : 3;
    }

    internal static bool TryStart(string exe, string arguments, UninstallReport report)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),   // never a folder that is about to be deleted
            });
            report.Line($"started {exe} {arguments}");
            return p != null;
        }
        catch (Exception ex)
        {
            report.Line($"could not start {exe}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The app's language from settings.json, read only (the page must not write into the data folder).</summary>
    internal static string ReadLanguage(string dataRoot)
    {
        try
        {
            string file = Path.Combine(dataRoot, "settings.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("Language", StringComparison.OrdinalIgnoreCase) && p.Value.GetString() is { } l && (l == "th" || l == "en"))
                        return l;
            }
        }
        catch { }
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "th" ? "th" : "en";
    }
}

/// <summary>A plain-text record of the uninstall in %TEMP% (never in the data folder): what was kept, deleted and why
/// anything was left — the file the page offers to open and the team asks for.</summary>
public sealed class UninstallReport
{
    private readonly System.Text.StringBuilder _sb = new();
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        $"CluadeX-uninstall-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.log");

    public void Line(string s)
    {
        lock (_sb) _sb.Append(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append("  ").AppendLine(s);
    }

    public void Save()
    {
        try { lock (_sb) File.WriteAllText(Path, _sb.ToString(), new System.Text.UTF8Encoding(false)); } catch { }
    }
}
