using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CluadeX.Models;

namespace CluadeX.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private AppSettings _settings = new();
    private readonly string _settingsPath;
    private readonly string _dataRoot;

    /// <summary>True when a "portable" marker file exists next to the exe.</summary>
    public bool IsPortable { get; }

    /// <summary>Root data directory (either AppData\CluadeX or exe-relative Data\).</summary>
    public string DataRoot => _dataRoot;

    public AppSettings Settings => _settings;

    public event Action? SettingsChanged;

    public SettingsService()
    {
        // Detect portable mode: if "portable" or "portable.txt" exists next to the exe
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        IsPortable = File.Exists(Path.Combine(exeDir, "portable"))
                  || File.Exists(Path.Combine(exeDir, "portable.txt"));

        if (IsPortable)
        {
            _dataRoot = Path.Combine(exeDir, "Data");
            _settingsPath = Path.Combine(_dataRoot, "settings.json");
        }
        else
        {
            _dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluadeX");
            _settingsPath = Path.Combine(_dataRoot, "settings.json");
        }

        Load();
        ApplyDefaultDirectories();
        EnsureDirectoriesExist();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_settingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
            EnsureDirectoriesExist();
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        update(_settings);
        Save();
    }

    /// <summary>Fill in any blank directory paths with sensible defaults.</summary>
    private void ApplyDefaultDirectories()
    {
        if (string.IsNullOrEmpty(_settings.ModelDirectory))
            _settings.ModelDirectory = Path.Combine(_dataRoot, "Models");
        if (string.IsNullOrEmpty(_settings.CacheDirectory))
            _settings.CacheDirectory = Path.Combine(_dataRoot, "Cache");
        if (string.IsNullOrEmpty(_settings.LogDirectory))
            _settings.LogDirectory = Path.Combine(_dataRoot, "Logs");
        if (string.IsNullOrEmpty(_settings.TempDirectory))
            _settings.TempDirectory = Path.Combine(_dataRoot, "Temp");
        if (string.IsNullOrEmpty(_settings.SessionDirectory))
            _settings.SessionDirectory = Path.Combine(_dataRoot, "Sessions");
    }

    private void EnsureDirectoriesExist()
    {
        TryCreateDirectory(_settings.ModelDirectory);
        TryCreateDirectory(_settings.CacheDirectory);
        TryCreateDirectory(_settings.LogDirectory);
        TryCreateDirectory(_settings.TempDirectory);
        TryCreateDirectory(_settings.SessionDirectory);
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                Directory.CreateDirectory(path);
        }
        catch { }
    }
}
