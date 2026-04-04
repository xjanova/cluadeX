using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Auto-update service: check → download → extract → restart.
/// Connected to xman4289.com version API.
/// </summary>
public class AutoUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private const string BaseUrl = "https://xman4289.com/api/v1/products/cluadex";
    private const string GitHubReleasesApi = "https://api.github.com/repos/xjanova/cluadeX/releases/latest";

    public event Action<UpdateInfo>? OnUpdateAvailable;
    public event Action<double, string>? OnDownloadProgress; // percent, status
    public event Action<string>? OnUpdateStatus;

    public AutoUpdateService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public static string CurrentVersion
    {
        get
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            return asm.GetName().Version?.ToString(3) ?? "2.0.0";
        }
    }

    /// <summary>Check xman API first, fallback to GitHub releases.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Try xman API
        var info = await CheckXmanAsync(ct);
        if (info != null) return info;

        // Fallback: GitHub releases
        return await CheckGitHubAsync(ct);
    }

    private async Task<UpdateInfo?> CheckXmanAsync(CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["current_version"] = CurrentVersion,
                ["machine_id"] = XmanLicenseService.GetMachineFingerprint(),
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{BaseUrl}/check-update", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("update_available", out var ua) && ua.GetBoolean())
            {
                var info = new UpdateInfo
                {
                    NewVersion = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    CurrentVersion = CurrentVersion,
                    DownloadUrl = root.TryGetProperty("download_url", out var dl) ? dl.GetString() ?? "" : "",
                    Changelog = root.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "",
                    FileSize = root.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0,
                    FileName = root.TryGetProperty("download_filename", out var fn) ? fn.GetString() ?? "" : "",
                };
                OnUpdateAvailable?.Invoke(info);
                return info;
            }
        }
        catch { /* silently fail */ }
        return null;
    }

    private async Task<UpdateInfo?> CheckGitHubAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(GitHubReleasesApi, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string version = tagName.TrimStart('v', 'V');

            if (IsNewerVersion(version, CurrentVersion))
            {
                // Find zip asset
                string downloadUrl = "";
                string fileName = "";
                long fileSize = 0;

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            fileName = name;
                            fileSize = asset.GetProperty("size").GetInt64();
                            break;
                        }
                    }
                }

                // Fallback to zipball
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = root.TryGetProperty("zipball_url", out var zb) ? zb.GetString() ?? "" : "";
                    fileName = $"CluadeX-{version}.zip";
                }

                string changelog = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                var info = new UpdateInfo
                {
                    NewVersion = version,
                    CurrentVersion = CurrentVersion,
                    DownloadUrl = downloadUrl,
                    Changelog = changelog,
                    FileSize = fileSize,
                    FileName = fileName,
                };
                OnUpdateAvailable?.Invoke(info);
                return info;
            }
        }
        catch { /* silently fail */ }
        return null;
    }

    /// <summary>Semantic version comparison: returns true if candidate > current.</summary>
    private static bool IsNewerVersion(string candidate, string current)
    {
        if (string.IsNullOrEmpty(candidate)) return false;

        // Try proper System.Version parsing
        if (Version.TryParse(candidate, out var candidateVer) && Version.TryParse(current, out var currentVer))
            return candidateVer > currentVer;

        // Fallback: lexicographic (shouldn't reach here for well-formed versions)
        return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>Download update zip with progress reporting.</summary>
    public async Task<string?> DownloadUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl)) return null;

        try
        {
            OnUpdateStatus?.Invoke("Downloading update...");

            string tempDir = Path.Combine(Path.GetTempPath(), "CluadeX_Update");
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, info.FileName.Length > 0 ? info.FileName : "update.zip");

            using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? info.FileSize;
            long downloadedBytes = 0;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    double percent = (double)downloadedBytes / totalBytes * 100;
                    string status = $"Downloading... {downloadedBytes / (1024.0 * 1024):F1} / {totalBytes / (1024.0 * 1024):F1} MB";
                    OnDownloadProgress?.Invoke(percent, status);
                }
            }

            OnDownloadProgress?.Invoke(100, "Download complete!");
            OnUpdateStatus?.Invoke("Download complete. Ready to install.");
            return zipPath;
        }
        catch (Exception ex)
        {
            OnUpdateStatus?.Invoke($"Download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Extract update and create a batch script to replace files and restart.</summary>
    public bool InstallAndRestart(string zipPath)
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string tempExtract = Path.Combine(Path.GetTempPath(), "CluadeX_Update", "extracted");
            string batchPath = Path.Combine(Path.GetTempPath(), "CluadeX_Update", "update.bat");

            // Extract zip
            OnUpdateStatus?.Invoke("Extracting update...");
            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, true);
            ZipFile.ExtractToDirectory(zipPath, tempExtract, true);

            // Find the actual files (might be in a subfolder)
            string sourceDir = tempExtract;
            var subDirs = Directory.GetDirectories(tempExtract);
            if (subDirs.Length == 1 && !File.Exists(Path.Combine(tempExtract, "CluadeX.exe")))
            {
                sourceDir = subDirs[0]; // zip contains a single folder
            }

            // Create batch script that waits for app to close, copies files, restarts
            string batchContent = $"""
                @echo off
                echo ========================================
                echo   CluadeX Auto-Update Installer
                echo ========================================
                echo.
                echo Waiting for CluadeX to close...
                timeout /t 3 /nobreak >nul

                :waitloop
                tasklist /FI "IMAGENAME eq CluadeX.exe" 2>nul | find /i "CluadeX.exe" >nul
                if not errorlevel 1 (
                    echo Still running, waiting...
                    timeout /t 2 /nobreak >nul
                    goto waitloop
                )

                echo.
                echo Copying new files...
                xcopy /E /Y /I "{sourceDir}\*" "{appDir}" >nul 2>&1

                echo.
                echo Update complete! Starting CluadeX...
                start "" "{Path.Combine(appDir, "CluadeX.exe")}"

                echo Cleaning up...
                rmdir /S /Q "{Path.Combine(Path.GetTempPath(), "CluadeX_Update")}" >nul 2>&1

                exit
                """;

            File.WriteAllText(batchPath, batchContent, Encoding.GetEncoding(874)); // Thai Windows codepage

            // Launch the updater batch and close the app
            OnUpdateStatus?.Invoke("Installing update... App will restart.");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            Process.Start(psi);

            // Close the app — the batch script will wait and replace files
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });

            return true;
        }
        catch (Exception ex)
        {
            OnUpdateStatus?.Invoke($"Install failed: {ex.Message}");
            return false;
        }
    }
}

public class UpdateInfo
{
    public string NewVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Changelog { get; set; } = "";
    public long FileSize { get; set; }
    public string FileName { get; set; } = "";

    public bool IsNewer => !string.IsNullOrEmpty(NewVersion) && NewVersion != CurrentVersion;
    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F1} MB",
    };
}
