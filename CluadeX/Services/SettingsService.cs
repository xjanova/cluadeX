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

                    // Migration: lift the stale 4096 default to 8192. The agentic system prompt + core tool
                    // schemas are ~4.4k tokens, so a 4096 window overflows on the first agentic message. 4096
                    // was the OLD default, so an exact 4096 means "never deliberately changed" — bump it; a
                    // user who set 2048/6000/etc on purpose is left alone.
                    // Same reasoning applies again at 8192: measured on a real agentic turn, the
                    // prompt+schemas+one file floor is ~8.2k, so 8192 overflows by a few tokens and
                    // compaction can't rescue it (that floor isn't history). Exact old defaults are
                    // treated as "never deliberately changed"; a hand-picked value is left alone.
                    if (_settings.ContextSize is 4096 or 8192) _settings.ContextSize = 12288;
                }
            }
            catch (Exception ex)
            {
                // Don't silently reset to defaults over a recoverable file — a single bad parse would
                // otherwise be overwritten by the next Save(), PERMANENTLY losing the user's config +
                // API keys. Preserve the bad file for recovery first, then fall back to defaults.
                try
                {
                    if (File.Exists(_settingsPath))
                        File.Copy(_settingsPath, _settingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                }
                catch { /* best-effort backup */ }
                System.Diagnostics.Debug.WriteLine($"Settings load failed (backed up, using defaults): {ex.Message}");
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
                // fsync the temp file BEFORE swapping it in. File.Replace can commit the directory entry
                // while the new file's data pages are still in the OS cache, so a power-loss in between
                // would leave settings.json pointing at a zero/partial file — losing every preference and
                // the (encrypted) API keys, the exact corruption this temp-write was meant to prevent.
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(json);
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }
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
        AiProviderType before;
        AiProviderType after;
        lock (_ioLock)
        {
            before = _settings.ActiveProvider;
            update(_settings);
            after = _settings.ActiveProvider;
        }
        // Diagnostic: trace every UpdateSettings that flips ActiveProvider so
        // we can see the culprit reverting our set_model alignment. Cheap to
        // ship; can be removed once the lifecycle is settled.
        if (before != after)
        {
            try
            {
                var stack = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false);
                var caller = stack.GetFrames()?.FirstOrDefault(f => f.GetMethod()?.Name != "UpdateSettings")?.GetMethod();
                var callerName = caller != null ? $"{caller.DeclaringType?.Name}.{caller.Name}" : "?";
                var logFile = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cluadex", "mcp-host.log");
                System.IO.File.AppendAllText(logFile,
                    $"{DateTime.Now:O} ActiveProvider FLIP {before} -> {after}  caller={callerName}\n");
            }
            catch { /* best-effort */ }
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
