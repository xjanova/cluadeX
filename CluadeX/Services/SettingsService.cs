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

    /// <summary>Decrypt an "ENC:" value. <paramref name="failed"/> is true when it could not be decrypted here.</summary>
    private static string DecryptString(string encryptedText, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(encryptedText)) return encryptedText;
        if (!encryptedText.StartsWith("ENC:")) return encryptedText; // legacy plaintext
        try
        {
            byte[] encrypted = System.Convert.FromBase64String(encryptedText["ENC:".Length..]);
            byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            failed = true;
            return string.Empty;
        }
    }

    // ─── Secrets that could not be decrypted on this machine ───
    // DPAPI (CurrentUser) only decrypts on the Windows account that encrypted. A portable copy carried to
    // another PC loaded every API key as "", and the next save — any setting at all — wrote "" back over
    // the encrypted blobs: the keys were gone even on the original PC. The blobs are now remembered and
    // written back unchanged unless the user enters a new key.
    private readonly Dictionary<string, string> _undecryptable = new(StringComparer.Ordinal);
    private const string HfTokenSecretId = "HuggingFaceToken";
    private static string ProviderSecretId(string provider) => "Provider:" + provider;

    /// <summary>Keys stored on disk that could not be decrypted here ("HuggingFaceToken", "Provider:Anthropic", …).
    /// Empty when everything decrypted. The settings page tells the user to re-enter them.</summary>
    public IReadOnlyCollection<string> UndecryptableSecrets
    {
        get { lock (_ioLock) return _undecryptable.Keys.ToList(); }
    }

    /// <summary>Encrypt all API keys in settings before saving. A key that could not be decrypted on this PC and
    /// was not replaced by the user is written back as its original encrypted blob.</summary>
    private void EncryptSecrets(AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.HuggingFaceToken) && !settings.HuggingFaceToken.StartsWith("ENC:"))
            settings.HuggingFaceToken = EncryptString(settings.HuggingFaceToken);
        else if (string.IsNullOrEmpty(settings.HuggingFaceToken) && _undecryptable.TryGetValue(HfTokenSecretId, out var hfBlob))
            settings.HuggingFaceToken = hfBlob;

        foreach (var kvp in settings.ProviderConfigs)
        {
            if (!string.IsNullOrEmpty(kvp.Value.ApiKey) && !kvp.Value.ApiKey.StartsWith("ENC:"))
                kvp.Value.ApiKey = EncryptString(kvp.Value.ApiKey);
            else if (string.IsNullOrEmpty(kvp.Value.ApiKey) && _undecryptable.TryGetValue(ProviderSecretId(kvp.Key), out var blob))
                kvp.Value.ApiKey = blob;
        }
    }

    /// <summary>Decrypt all API keys in settings after loading, remembering the blobs that fail.</summary>
    private void DecryptSecrets(AppSettings settings)
    {
        _undecryptable.Clear();
        if (!string.IsNullOrEmpty(settings.HuggingFaceToken))
        {
            string blob = settings.HuggingFaceToken;
            settings.HuggingFaceToken = DecryptString(blob, out bool failed);
            if (failed) _undecryptable[HfTokenSecretId] = blob;
        }

        foreach (var kvp in settings.ProviderConfigs)
        {
            if (string.IsNullOrEmpty(kvp.Value.ApiKey)) continue;
            string blob = kvp.Value.ApiKey;
            kvp.Value.ApiKey = DecryptString(blob, out bool failed);
            if (failed) _undecryptable[ProviderSecretId(kvp.Key)] = blob;
        }
    }

    /// <summary>After a successful save: a secret the user has re-entered no longer needs its old blob.</summary>
    private void ForgetReplacedSecrets()
    {
        if (!string.IsNullOrEmpty(_settings.HuggingFaceToken)) _undecryptable.Remove(HfTokenSecretId);
        foreach (var kvp in _settings.ProviderConfigs)
            if (!string.IsNullOrEmpty(kvp.Value.ApiKey)) _undecryptable.Remove(ProviderSecretId(kvp.Key));
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

    /// <summary>The data home this build uses: &lt;exe&gt;\Data next to a "portable" / "portable.txt" marker, else
    /// ~/.cluadex. One rule for services created before this one — the debug log had its own and always wrote
    /// ~/.cluadex/logs, so a portable copy and every test harness wrote into the user's real log (2026-10-13: 90 lines
    /// of mcptest's stub servers in the log a pipe run was being watched through).</summary>
    public static string ResolveDataRoot(out bool portable)
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        portable = File.Exists(Path.Combine(exeDir, "portable")) || File.Exists(Path.Combine(exeDir, "portable.txt"));
        return portable
            ? Path.Combine(exeDir, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
    }

    public SettingsService()
    {
        // Detect portable mode: if "portable" or "portable.txt" exists next to the exe
        string root = ResolveDataRoot(out bool portable);
        IsPortable = portable;

        if (IsPortable)
        {
            _dataRoot = root;
            _settingsPath = Path.Combine(_dataRoot, "settings.json");
        }
        else
        {
            // ~/.cluadex, NOT %LOCALAPPDATA%\CluadeX.
            //
            // The old location is Velopack's install root. Measured on 2026-08-05:
            // running CluadeX-win-Setup.exe **emptied that directory** — settings.json,
            // codex.db, mcp_servers.json, permissions.json and the whole Sessions\
            // folder were gone, replaced by current\ + packages\ + Update.exe. Every
            // install would have silently taken the user's settings and chat history
            // with it. Storing app data inside the installer's own directory is the
            // bug; moving it out is the fix, not a workaround.
            //
            // ~/.cluadex is where this app already keeps logs, the MCP host token,
            // skills and bundled hooks — so this makes ONE data home instead of two.
            _dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
            _settingsPath = Path.Combine(_dataRoot, "settings.json");
            MigrateLegacyDataRoot();
        }

        Load();
        ApplyDefaultDirectories();
        EnsureDirectoriesExist();
    }

    /// <summary>
    /// One-time move of everything from the pre-3.0.54 data root
    /// (<c>%LOCALAPPDATA%\CluadeX</c>) into <see cref="_dataRoot"/>.
    ///
    /// COPIES, never deletes: the legacy folder is Velopack's install root, so it
    /// also holds current\, packages\ and Update.exe, and deleting anything there
    /// would be deleting the installed app. Skips those three by name.
    ///
    /// Runs only when the new root has no settings.json yet, so it happens once and
    /// can never overwrite newer data with older.
    /// </summary>
    private void MigrateLegacyDataRoot()
    {
        try
        {
            if (File.Exists(_settingsPath)) return;   // already migrated (or a fresh install)

            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluadeX");
            if (!Directory.Exists(legacy)) return;
            if (!File.Exists(Path.Combine(legacy, "settings.json"))) return;  // nothing worth moving

            Directory.CreateDirectory(_dataRoot);
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "current", "packages", "Update.exe", "CluadeX.exe" };

            foreach (var file in Directory.GetFiles(legacy))
            {
                string name = Path.GetFileName(file);
                if (skip.Contains(name)) continue;
                string target = Path.Combine(_dataRoot, name);
                if (!File.Exists(target)) File.Copy(file, target);
            }
            foreach (var dir in Directory.GetDirectories(legacy))
            {
                string name = Path.GetFileName(dir);
                if (skip.Contains(name)) continue;
                string target = Path.Combine(_dataRoot, name);
                if (Directory.Exists(target)) continue;
                CopyDirectory(dir, target);
            }
        }
        catch { /* a failed migration must never stop the app starting */ }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(source))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(source))
            CopyDirectory(d, Path.Combine(target, Path.GetFileName(d)));
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

                    // Owner 2026-10-20: the editor follows the files, it no longer takes the user off their page.
                    // Saved files carry the old default (true); switch it off once — a later choice in Settings stays.
                    bool changed = false;
                    if (!_settings.EditorNoAutoSwitchMigrated)
                    {
                        _settings.AutoOpenEditorOnAgentEdit = false;
                        _settings.EditorNoAutoSwitchMigrated = true;
                        changed = true;
                    }
                    changed |= RecoverKeysFromBackups();
                    if (changed) _saveAfterLoad = true;
                }
            }
            catch (Exception ex)
            {
                // Don't silently reset to defaults over a recoverable file — a single bad parse would
                // otherwise be overwritten by the next Save(), PERMANENTLY losing the user's config +
                // API keys. Preserve the bad file for recovery first, then fall back to defaults.
                // (Invariant culture: on a Thai system the name came out as ".corrupt-25691019-…", the Buddhist year.)
                try
                {
                    if (File.Exists(_settingsPath))
                        File.Copy(_settingsPath, _settingsPath + $".corrupt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}", overwrite: true);
                }
                catch { /* best-effort backup */ }
                System.Diagnostics.Debug.WriteLine($"Settings load failed (backed up): {ex.Message}");
                // One value this build cannot read must not cost every other one. An older build meeting a newer
                // provider ("ActiveProvider": "GeminiDev" read by 3.0.153, 2026-10-19) threw on that one enum and reset
                // the whole file — the API keys with it. Keep every setting that still reads; only the rest is default.
                AppSettings? salvaged = null;
                try { if (File.Exists(_settingsPath)) salvaged = Salvage(File.ReadAllText(_settingsPath), out _); }
                catch { salvaged = null; }
                _settings = salvaged ?? new AppSettings();
                if (salvaged != null) DecryptSecrets(_settings);
            }
        }
        if (_saveAfterLoad)
        {
            _saveAfterLoad = false;
            try { Save(); } catch { /* the next save writes it */ }
        }
    }

    private bool _saveAfterLoad;

    /// <summary>The providers whose API keys came back from a settings.json.corrupt-* backup at this start (for a notice).</summary>
    public IReadOnlyList<string> RecoveredKeys { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// A load that failed once reset settings.json and the next save dropped the API keys (2026-10-19, an older build
    /// over "ActiveProvider": "GeminiDev"); the keys stayed in the .corrupt-* backup it made. Each backup not looked at
    /// before gives back the keys that are empty now — never one the user has since typed in. True when anything
    /// changed (a key, or the marker of the newest backup looked at).
    /// </summary>
    private bool RecoverKeysFromBackups()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_settingsPath);
            if (dir == null || !Directory.Exists(dir)) return false;
            var backups = new DirectoryInfo(dir).GetFiles(Path.GetFileName(_settingsPath) + ".corrupt-*")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (backups.Count == 0 || backups[0].Name == _settings.KeyRecoveryCheckedBackup) return false;
            var unseen = new List<AppSettings>();
            foreach (var f in backups)
            {
                if (f.Name == _settings.KeyRecoveryCheckedBackup) break;   // older ones were looked at already
                AppSettings? s;
                try { s = Salvage(File.ReadAllText(f.FullName), out _); }
                catch { continue; }
                if (s == null) continue;
                foreach (var c in s.ProviderConfigs.Values)
                    if (!string.IsNullOrEmpty(c.ApiKey))
                    {
                        c.ApiKey = DecryptString(c.ApiKey, out bool failed);
                        if (failed) c.ApiKey = "";   // another Windows user's key: nothing to give back
                    }
                unseen.Add(s);
            }
            RecoveredKeys = RecoverKeys(_settings, unseen);
            _settings.KeyRecoveryCheckedBackup = backups[0].Name;
            if (RecoveredKeys.Count > 0)
                System.Diagnostics.Debug.WriteLine($"API keys recovered from a settings backup: {string.Join(", ", RecoveredKeys)}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fill the API keys that are empty in <paramref name="current"/> from <paramref name="backupsNewestFirst"/> (already
    /// decrypted): a provider missing entirely comes back whole, one whose key is empty gets the key only; a key the
    /// user has now is never replaced. Returns the providers that got a key. Pure, so the harness checks it.
    /// </summary>
    public static List<string> RecoverKeys(AppSettings current, IEnumerable<AppSettings> backupsNewestFirst)
    {
        var got = new List<string>();
        foreach (var backup in backupsNewestFirst)
            foreach (var (name, cfg) in backup.ProviderConfigs)
            {
                if (string.IsNullOrEmpty(cfg.ApiKey) || got.Contains(name)) continue;
                if (!current.ProviderConfigs.TryGetValue(name, out var now))
                {
                    current.ProviderConfigs[name] = cfg;
                    got.Add(name);
                }
                else if (string.IsNullOrEmpty(now.ApiKey))
                {
                    now.ApiKey = cfg.ApiKey;
                    got.Add(name);
                }
            }
        return got;
    }

    /// <summary>
    /// The settings in <paramref name="json"/> that this build can read: each top-level setting is tried on its own, and
    /// one that does not read (an enum value from a newer build, a changed type) is left at its default and named in
    /// <paramref name="dropped"/>. Null when the text is not a JSON object at all. Pure, so the harness can check it.
    /// </summary>
    public static AppSettings? Salvage(string json, out List<string> dropped)
    {
        dropped = new List<string>();
        if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject all) return null;
        var kept = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in all)
        {
            var one = new System.Text.Json.Nodes.JsonObject { [name] = value?.DeepClone() };
            try
            {
                JsonSerializer.Deserialize<AppSettings>(one.ToJsonString(), JsonOptions);
                kept[name] = value?.DeepClone();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // A map such as ProviderConfigs (where the API keys live): keep each entry that reads on its own.
                if (value is System.Text.Json.Nodes.JsonObject children)
                {
                    var keptChildren = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (child, childValue) in children)
                    {
                        var probe = new System.Text.Json.Nodes.JsonObject { [name] = new System.Text.Json.Nodes.JsonObject { [child] = childValue?.DeepClone() } };
                        try
                        {
                            JsonSerializer.Deserialize<AppSettings>(probe.ToJsonString(), JsonOptions);
                            keptChildren[child] = childValue?.DeepClone();
                        }
                        catch (Exception cex) when (cex is JsonException or NotSupportedException or InvalidOperationException) { dropped.Add($"{name}.{child}"); }
                    }
                    if (keptChildren.Count > 0) kept[name] = keptChildren;
                    continue;
                }
                dropped.Add(name);
            }
        }
        return JsonSerializer.Deserialize<AppSettings>(kept.ToJsonString(), JsonOptions) ?? new AppSettings();
    }

    /// <summary>Why the last <see cref="Save"/> failed (null after a successful save).</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>
    /// Write settings.json. Returns false when the write failed — it used to swallow the exception while the
    /// settings page said "Saved ✓". Folders are NOT created here any more: the page auto-saves while a path is
    /// being typed, and creating every intermediate value made folders like "D:\Mod" appear.
    /// </summary>
    public bool Save()
    {
        // Capture subscribers outside the lock so the Save() call itself doesn't hold
        // the IO lock while handlers run (they may re-enter via settings reads).
        Action? changedHandlers = null;
        bool ok = false;
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

                ForgetReplacedSecrets();
                LastSaveError = null;
                ok = true;
                changedHandlers = SettingsChanged;
            }
            catch (Exception ex)
            {
                LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {LastSaveError}");
            }
        }
        changedHandlers?.Invoke();
        return ok;
    }

    /// <summary>Apply <paramref name="update"/> and save. Returns false when the save failed (see LastSaveError).</summary>
    public bool UpdateSettings(Action<AppSettings> update)
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
                // The data home (~/.cluadex installed; <exe>\Data portable) — a portable copy and the test harnesses
                // must not write into the user's real ~/.cluadex.
                var logFile = System.IO.Path.Combine(_dataRoot, "mcp-host.log");
                System.IO.File.AppendAllText(logFile,
                    $"{DateTime.Now:O} ActiveProvider FLIP {before} -> {after}  caller={callerName}\n");
            }
            catch { /* best-effort */ }
        }
        return Save();
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
