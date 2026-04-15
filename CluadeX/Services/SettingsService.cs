using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CluadeX.Models;

namespace CluadeX.Services;

public class SettingsService
{
    // ─── DPAPI Encryption for API Keys ───────────────────────────
    private static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return "ENC:" + System.Convert.ToBase64String(encrypted);
        }
        catch { return plainText; }
    }

    private static string DecryptString(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText)) return encryptedText;
        if (!encryptedText.StartsWith("ENC:")) return encryptedText; // legacy plaintext
        try
        {
            byte[] encrypted = System.Convert.FromBase64String(encryptedText["ENC:".Length..]);
            byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return string.Empty; }
    }

    /// <summary>Encrypt all API keys in settings before saving.</summary>
    private static void EncryptSecrets(AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.HuggingFaceToken) && !settings.HuggingFaceToken.StartsWith("ENC:"))
            settings.HuggingFaceToken = EncryptString(settings.HuggingFaceToken);

        foreach (var kvp in settings.ProviderConfigs)
        {
            if (!string.IsNullOrEmpty(kvp.Value.ApiKey) && !kvp.Value.ApiKey.StartsWith("ENC:"))
                kvp.Value.ApiKey = EncryptString(kvp.Value.ApiKey);
        }
    }

    /// <summary>Decrypt all API keys in settings after loading.</summary>
    private static void DecryptSecrets(AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.HuggingFaceToken))
            settings.HuggingFaceToken = DecryptString(settings.HuggingFaceToken);

        foreach (var kvp in settings.ProviderConfigs)
        {
            if (!string.IsNullOrEmpty(kvp.Value.ApiKey))
                kvp.Value.ApiKey = DecryptString(kvp.Value.ApiKey);
        }
    }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private AppSettings _settings = new();
    private readonly string _settingsPath;
    private readonly string _dataRoot;
    // Serializes disk I/O and Update batches — without this, two Save() calls
    // from different threads could race File.WriteAllText and corrupt settings.json.
    private readonly object _ioLock = new();

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
        lock (_ioLock)
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                    DecryptSecrets(_settings);
                }
            }
            catch
            {
                _settings = new AppSettings();
            }
        }
    }

    public void Save()
    {
        // Capture subscribers outside the lock so the Save() call itself doesn't hold
        // the IO lock while handlers run (they may re-enter via settings reads).
        Action? changedHandlers = null;
        lock (_ioLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_settingsPath);
                if (dir != null) Directory.CreateDirectory(dir);

                // Clone settings and encrypt secrets before writing to disk
                var clone = JsonSerializer.Deserialize<AppSettings>(
                    JsonSerializer.Serialize(_settings, JsonOptions), JsonOptions)!;
                EncryptSecrets(clone);

                string json = JsonSerializer.Serialize(clone, JsonOptions);

                // Atomic write: write to temp file, then move over the target.
                // A crash mid-write previously left a zero-byte or partial settings.json
                // and users would lose every preference on next launch.
                string tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(_settingsPath))
                    File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
                else
                    File.Move(tempPath, _settingsPath);

                EnsureDirectoriesExist();
                changedHandlers = SettingsChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.GetType().Name}: {ex.Message}");
            }
        }
        changedHandlers?.Invoke();
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        lock (_ioLock)
        {
            update(_settings);
        }
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
