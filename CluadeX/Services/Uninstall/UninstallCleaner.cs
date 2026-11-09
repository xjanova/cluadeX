using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Services.Connections;
using Microsoft.Win32;

namespace CluadeX.Services.Uninstall;

/// <summary>What the data folder holds, row by row (what the page shows sizes for and the cleaner deletes).</summary>
public sealed class DataScan
{
    public string DataRoot { get; init; } = "";
    public Dictionary<KeepGroupId, List<string>> Entries { get; } = UninstallCatalog.Order.ToDictionary(g => g, _ => new List<string>());
    public Dictionary<KeepGroupId, long> Bytes { get; } = UninstallCatalog.Order.ToDictionary(g => g, _ => 0L);
    public List<string> Always { get; } = new();
    public List<string> SettingsBackups { get; } = new();
    public List<CustomFolder> Custom { get; init; } = new();
    public Dictionary<string, long> CustomBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static DataScan Run(string dataRoot, bool measure = true, CancellationToken ct = default)
    {
        var scan = new DataScan { DataRoot = dataRoot, Custom = UninstallCatalog.CustomFolders(dataRoot) };
        if (Directory.Exists(dataRoot))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(dataRoot))
            {
                string name = Path.GetFileName(path);
                string group = UninstallCatalog.Classify(name);
                if (group == UninstallCatalog.AlwaysMarker) { scan.Always.Add(path); continue; }
                if (group == UninstallCatalog.SettingsBackupMarker) { scan.SettingsBackups.Add(path); continue; }
                var g = Enum.Parse<KeepGroupId>(group);
                scan.Entries[g].Add(path);
                if (measure) scan.Bytes[g] += TreeDeleter.SizeOf(path, ct);
            }
            // the license is a few lines inside settings.json: show it as present, never as a size of its own
            if (scan.Entries[KeepGroupId.Settings].Any(p => Path.GetFileName(p).Equals("settings.json", StringComparison.OrdinalIgnoreCase)) && HasLicense(dataRoot))
                scan.Bytes[KeepGroupId.License] += 1;
        }
        if (measure) foreach (var c in scan.Custom) scan.CustomBytes[c.Path] = TreeDeleter.SizeOf(c.Path, ct);
        return scan;
    }

    public bool HasAnything(KeepGroupId g) => Bytes[g] > 0 || Entries[g].Count > 0;

    private static bool HasLicense(string dataRoot)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "settings.json"))) as JsonObject;
            return node != null && UninstallCatalog.LicenseFields.Any(f => node.TryGetPropertyValue(f, out var v) && v != null
                && !(v is JsonValue jv && (jv.TryGetValue(out bool b) && !b || jv.TryGetValue(out string? s) && string.IsNullOrEmpty(s))));
        }
        catch { return false; }
    }
}

/// <summary>One line of the progress list.</summary>
public sealed record CleanStep(string Id, string Label);

/// <summary>
/// Does the uninstall, step by step: close CluadeX, delete every unticked row, split settings.json when only one of
/// "settings" / "license" is kept, remove what CluadeX put outside its data folder (the Chrome host key, its BrainX
/// bridge entries, its turn folders in %LOCALAPPDATA% and %TEMP%, shortcuts to this install, the legacy registry key),
/// and finally the data folder itself when nothing in it is kept. Velopack's Update.exe removes the program afterwards.
///
/// In an isolated run (--data-root) only what belongs to THAT data folder and THAT install is touched — never the
/// shared %TEMP% / %LOCALAPPDATA% turn folders or another copy's registry and BrainX entries.
/// </summary>
public sealed class UninstallCleaner
{
    private readonly DataScan _scan;
    private readonly ISet<KeepGroupId> _keep;
    private readonly ISet<string> _keepCustom;
    private readonly UninstallEntry.Install? _install;
    private readonly bool _isolated;
    private readonly UninstallReport _report;
    private readonly string _selfExe = Environment.ProcessPath ?? "";

    public long BytesFreed { get; private set; }
    public List<DeleteProblem> Problems { get; } = new();
    public bool KeptAnything => _keep.Count > 0;

    public UninstallCleaner(DataScan scan, IEnumerable<KeepGroupId> keep, IEnumerable<string> keepCustom,
        UninstallEntry.Install? install, bool isolated, UninstallReport report)
    {
        _scan = scan;
        _keep = new HashSet<KeepGroupId>(keep);
        _keepCustom = new HashSet<string>(keepCustom, StringComparer.OrdinalIgnoreCase);
        _install = install;
        _isolated = isolated;
        _report = report;
    }

    public bool Keeps(KeepGroupId g) => _keep.Contains(g);

    /// <summary>The unticked rows that have something to delete, in page order. The license row counts when the key is
    /// only a few lines inside settings.json (no files of its own).</summary>
    public IReadOnlyList<KeepGroupId> GroupsToDelete() => UninstallCatalog.Order.Where(g => !Keeps(g) && _scan.HasAnything(g)).ToList();

    // ─────────────── 1. close CluadeX ───────────────

    /// <summary>Close every other CluadeX process of this install (the app saves its chats as it closes), then the
    /// helpers it started, then any program running from a folder about to be deleted (llama-server in llama-backend).
    /// Returns how many were closed; throws when one would not close.</summary>
    public int CloseRunning(TimeSpan grace)
    {
        var doomed = DoomedFolders().ToList();
        int closed = 0;
        var stubborn = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                string? image = null;
                try { image = p.MainModule?.FileName; } catch { continue; }   // another user's / a protected process
                if (image == null || !(IsOurProgram(image) || doomed.Any(d => Safety.ProtectedPaths.IsInside(image, d)))) continue;

                _report.Line($"closing {p.ProcessName} ({p.Id}) {image}");
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit((int)grace.TotalMilliseconds)) { closed++; continue; }
                p.Kill(entireProcessTree: false);
                if (p.WaitForExit(5000)) closed++;
                else stubborn.Add($"{p.ProcessName} ({p.Id})");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        if (stubborn.Count > 0) throw new InvalidOperationException("still running: " + string.Join(", ", stubborn));
        return closed;
    }

    private bool IsOurProgram(string image)
    {
        if (_install != null) return Safety.ProtectedPaths.IsInside(image, _install.Root);
        return string.Equals(image, _selfExe, StringComparison.OrdinalIgnoreCase);   // a developer build: the same exe only
    }

    private IEnumerable<string> DoomedFolders()
    {
        foreach (var g in UninstallCatalog.Order)
            if (!Keeps(g))
                foreach (var e in _scan.Entries[g])
                    if (Directory.Exists(e)) yield return e;
        foreach (var c in _scan.Custom)
            if (!_keepCustom.Contains(c.Path)) yield return c.Path;
    }

    // ─────────────── 2. delete a row ───────────────

    /// <summary>Delete one unticked row's files. settings.json is split instead when only one of settings / license
    /// goes. Returns the bytes freed.</summary>
    public long DeleteGroup(KeepGroupId g)
    {
        var del = new TreeDeleter(_scan.DataRoot);
        foreach (string path in _scan.Entries[g])
        {
            if (g == KeepGroupId.Settings && Path.GetFileName(path).Equals("settings.json", StringComparison.OrdinalIgnoreCase) && Keeps(KeepGroupId.License))
            {
                WriteLicenseOnly(path);
                continue;
            }
            del.Delete(path);
        }
        if (g == KeepGroupId.License)
        {
            string settings = Path.Combine(_scan.DataRoot, "settings.json");
            if (Keeps(KeepGroupId.Settings) && File.Exists(settings)) StripLicense(settings);
        }
        if (g is KeepGroupId.Settings or KeepGroupId.License)
            foreach (string backup in _scan.SettingsBackups) del.Delete(backup);   // they carry the key in plain text
        Collect(del, $"row {g}");
        return del.BytesFreed;
    }

    /// <summary>Delete a folder the user chose outside the data folder (only when its row was unticked).</summary>
    public long DeleteCustom(CustomFolder c)
    {
        var del = new TreeDeleter(c.Path);
        del.Delete(c.Path);
        Collect(del, $"folder {c.Path}");
        return del.BytesFreed;
    }

    private void WriteLicenseOnly(string settingsFile)
    {
        try
        {
            var old = JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject ?? new JsonObject();
            var kept = new JsonObject();
            foreach (string f in UninstallCatalog.LicenseFields)
                if (old.TryGetPropertyValue(f, out var v)) kept[f] = v?.DeepClone();
            WriteJson(settingsFile, kept);
            _report.Line("settings.json reduced to the license and account fields");
        }
        catch (Exception ex) { Problems.Add(TreeDeleter.Classify(settingsFile, ex)); _report.Line("could not reduce settings.json: " + ex.Message); }
    }

    private void StripLicense(string settingsFile)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsFile)) is not JsonObject node) return;
            foreach (string f in UninstallCatalog.LicenseFields) node.Remove(f);
            WriteJson(settingsFile, node);
            _report.Line("license and account fields removed from settings.json");
        }
        catch (Exception ex) { Problems.Add(TreeDeleter.Classify(settingsFile, ex)); _report.Line("could not strip the license: " + ex.Message); }
    }

    /// <summary>
    /// Settings kept: mark the setup wizard as not done in the kept settings.json, so installing CluadeX again opens it
    /// (pre-filled from these settings and the license, so nothing is asked twice). v3.0.275 left it "done" and the
    /// reinstall skipped the wizard. A license-only settings.json holds no such flag. True when the file was changed.
    /// </summary>
    public bool ResetSetupWizard()
    {
        if (!Keeps(KeepGroupId.Settings)) return false;
        string file = Path.Combine(_scan.DataRoot, "settings.json");
        if (!File.Exists(file)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject node) return false;
            if (node["SetupWizardCompleted"] is not JsonValue v || !v.TryGetValue(out bool done) || !done) return false;
            node["SetupWizardCompleted"] = false;
            WriteJson(file, node);
            _report.Line("settings.json: the setup wizard will open on the next install");
            return true;
        }
        catch (Exception ex) { _report.Line("could not reset the setup wizard flag: " + ex.Message); return false; }
    }

    private static void WriteJson(string file, JsonObject node)
    {
        string tmp = file + ".uninstall-tmp";
        File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
    }

    // ─────────────── 3. what CluadeX put elsewhere ───────────────

    /// <summary>Remove what is always removed. Returns how many things were removed.</summary>
    public int RemoveLinks()
    {
        int n = 0;
        var del = new TreeDeleter(_scan.DataRoot);
        foreach (string p in _scan.Always) if (del.Delete(p)) n++;
        Collect(del, "chrome files");

        n += RemoveChromeHost();
        n += RemoveBrainXBridges();
        n += RemoveShortcuts();
        if (!_isolated)
        {
            n += RemoveTurnFolders();
            n += RemoveLegacyKey();
        }
        return n;
    }

    /// <summary>HKCU\…\NativeMessagingHosts\com.xmanstudio.cluadex_chrome — only when it names this data folder's manifest.</summary>
    private int RemoveChromeHost()
    {
        const string key = @"Software\Google\Chrome\NativeMessagingHosts\" + Chrome.ChromeBridgeInstall.HostName;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
            {
                if (k == null) return 0;
                if (k.GetValue(null) is string manifest && !string.IsNullOrWhiteSpace(manifest)
                    && !Safety.ProtectedPaths.IsInside(manifest, _scan.DataRoot))
                {
                    _report.Line($"Chrome host key names another copy ({manifest}) — left alone");
                    return 0;
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            _report.Line("removed HKCU\\" + key);
            return 1;
        }
        catch (Exception ex) { _report.Line("Chrome host key: " + ex.Message); return 0; }
    }

    /// <summary>CluadeX's entries in BrainX's mcp-bridges.json whose command is this install's program.</summary>
    private int RemoveBrainXBridges()
    {
        try
        {
            if (BrainXBridges.ResolveVault() is not { } vault) return 0;
            string file = BrainXBridges.FileFor(vault);
            if (!File.Exists(file) || BrainXBridges.Load(file) is not { } root || root["bridges"] is not JsonArray bridges) return 0;
            int removed = 0;
            for (int i = bridges.Count - 1; i >= 0; i--)
            {
                if (bridges[i] is not JsonObject e) continue;
                string? id = e["id"]?.GetValue<string>();
                string? command = e["command"] is JsonValue cv && cv.TryGetValue(out string? c) ? c : null;
                if (id == null || command == null || !BrainXBridges.IsOurs(e, id) || !IsOurProgram(Path.GetFullPath(command))) continue;
                bridges.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                BrainXBridges.Save(file, root);
                _report.Line($"removed {removed} CluadeX bridge(s) from {file}");
            }
            return removed;
        }
        catch (Exception ex) { _report.Line("BrainX bridges: " + ex.Message); return 0; }
    }

    /// <summary>Desktop / Start shortcuts named CluadeX that point into this install (Velopack removes its own too).</summary>
    private int RemoveShortcuts()
    {
        if (_install == null) return 0;
        int n = 0;
        foreach (string dir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        })
        {
            try
            {
                string link = Path.Combine(dir, "CluadeX.lnk");
                if (!File.Exists(link) || ShortcutTarget(link) is not { } target || !Safety.ProtectedPaths.IsInside(target, _install.Root)) continue;
                File.Delete(link);
                _report.Line("removed " + link);
                n++;
            }
            catch (Exception ex) { _report.Line("shortcut: " + ex.Message); }
        }
        return n;
    }

    private static string? ShortcutTarget(string link)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try { return (string)shell.CreateShortcut(link).TargetPath; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <summary>%LOCALAPPDATA%\cluadex-gemini, \cluadex-grok (holds junctions into ~/.grok — never followed) and
    /// %TEMP%\cluadex* (the per-turn settings, hooks, downloads), except this uninstall's own report.</summary>
    private int RemoveTurnFolders()
    {
        int n = 0;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string name in new[] { "cluadex-gemini", "cluadex-grok" })
        {
            string dir = Path.Combine(local, name);
            if (!Directory.Exists(dir)) continue;
            var del = new TreeDeleter(dir);
            if (del.Delete(dir)) n++;
            Collect(del, dir);
        }
        string temp = Path.GetTempPath();
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(temp, "cluadex*"))
            {
                if (Path.GetFileName(path).StartsWith("CluadeX-uninstall-", StringComparison.OrdinalIgnoreCase)) continue;
                var del = new TreeDeleter(temp);
                if (del.Delete(path)) n++;
                Collect(del, path, quiet: true);   // a temp file another program still holds is not the user's problem
            }
        }
        catch (Exception ex) { _report.Line("temp: " + ex.Message); }
        return n;
    }

    /// <summary>HKCU\Software\CluadeX — written only by the old install.ps1.</summary>
    private int RemoveLegacyKey()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\CluadeX")) if (k == null) return 0;
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\CluadeX", throwOnMissingSubKey: false);
            _report.Line(@"removed HKCU\Software\CluadeX");
            return 1;
        }
        catch (Exception ex) { _report.Line("legacy key: " + ex.Message); return 0; }
    }

    // ─────────────── 4. the data folder itself ───────────────

    /// <summary>When nothing is kept: remove the (by now empty) data folder too. True when it is gone.</summary>
    public bool RemoveDataRootIfEmpty()
    {
        if (KeptAnything) return false;
        try
        {
            if (!Directory.Exists(_scan.DataRoot)) return true;
            // anything still there (a file written while we worked, a new folder) is CluadeX's too
            var del = new TreeDeleter(_scan.DataRoot);
            foreach (string p in Directory.EnumerateFileSystemEntries(_scan.DataRoot)) del.Delete(p);
            Collect(del, "data folder leftovers");
            Directory.Delete(_scan.DataRoot, recursive: false);
            _report.Line("removed " + _scan.DataRoot);
            return true;
        }
        catch (Exception ex) { _report.Line("data folder: " + ex.Message); return false; }
    }

    private void Collect(TreeDeleter del, string what, bool quiet = false)
    {
        BytesFreed += del.BytesFreed;
        foreach (var p in del.Problems)
        {
            _report.Line($"left {p.Path} — {p.Kind}: {p.Message}");
            if (!quiet) Problems.Add(p);
        }
        if (del.BytesFreed > 0) _report.Line($"{what}: {del.BytesFreed:N0} bytes freed");
    }
}
