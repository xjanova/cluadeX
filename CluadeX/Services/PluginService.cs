using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

public class PluginService
{
    private readonly SettingsService _settingsService;
    private readonly string _pluginsDir;
    private readonly string _configPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public PluginService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _pluginsDir = System.IO.Path.Combine(_settingsService.DataRoot, "plugins");
        _configPath = System.IO.Path.Combine(_pluginsDir, "plugins_config.json");
        Directory.CreateDirectory(_pluginsDir);
    }

    /// <summary>Scans subdirectories of the plugins folder for manifest.json files.</summary>
    public List<PluginInfo> ScanPlugins()
    {
        var plugins = new List<PluginInfo>();
        var enabledNames = LoadEnabledPluginNames();

        if (!Directory.Exists(_pluginsDir))
            return plugins;

        foreach (var dir in Directory.GetDirectories(_pluginsDir))
        {
            var manifestPath = System.IO.Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<JsonElement>(json);

                var plugin = new PluginInfo
                {
                    Name = manifest.TryGetProperty("name", out var n) ? n.GetString() ?? "" : System.IO.Path.GetFileName(dir),
                    Description = manifest.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Version = manifest.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "0.0.0",
                    Path = dir,
                    Enabled = enabledNames.Contains(
                        manifest.TryGetProperty("name", out var nm) ? nm.GetString() ?? System.IO.Path.GetFileName(dir) : System.IO.Path.GetFileName(dir),
                        StringComparer.OrdinalIgnoreCase),
                };

                plugins.Add(plugin);
            }
            catch
            {
                // Skip plugins with invalid manifests
            }
        }

        return plugins;
    }

    /// <summary>Enables a plugin by name and persists the config.</summary>
    public void EnablePlugin(string name)
    {
        var enabled = LoadEnabledPluginNames();
        if (!enabled.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            enabled.Add(name);
            SaveEnabledPluginNames(enabled);
        }
    }

    /// <summary>Disables a plugin by name and persists the config.</summary>
    public void DisablePlugin(string name)
    {
        var enabled = LoadEnabledPluginNames();
        enabled.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        SaveEnabledPluginNames(enabled);
    }

    /// <summary>Copies a plugin directory into the plugins folder.</summary>
    public void InstallPlugin(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return;

        var dirName = System.IO.Path.GetFileName(sourcePath);
        var destPath = System.IO.Path.Combine(_pluginsDir, dirName);

        if (Directory.Exists(destPath))
            Directory.Delete(destPath, true);

        CopyDirectory(sourcePath, destPath);
    }

    /// <summary>Returns a list of enabled plugin names.</summary>
    public List<string> GetEnabledPlugins()
    {
        return LoadEnabledPluginNames();
    }

    /// <summary>Returns the plugins directory path.</summary>
    public string PluginsDirectory => _pluginsDir;

    private List<string> LoadEnabledPluginNames()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
        }
        catch
        {
            // Corrupted config, start fresh
        }

        return new List<string>();
    }

    private void SaveEnabledPluginNames(List<string> names)
    {
        try
        {
            Directory.CreateDirectory(_pluginsDir);
            var json = JsonSerializer.Serialize(names, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save plugin config: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }
}
