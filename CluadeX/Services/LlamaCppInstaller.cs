using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>Which llama.cpp Windows build runs on this PC.</summary>
public enum LlamaCppVariant { Cpu, Vulkan, Cuda12, Cuda13 }

public sealed record LlamaCppAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>One llama.cpp release that has the build this PC needs: the main zip, and the CUDA runtime for a CUDA build.</summary>
public sealed record LlamaCppRelease(string Tag, LlamaCppVariant Variant, LlamaCppAsset Main, LlamaCppAsset? Runtime)
{
    public long TotalBytes => Main.Size + (Runtime?.Size ?? 0);
}

/// <summary>What is installed in a backend folder (cluadex-llama.json, written by the installer).</summary>
public sealed record LlamaCppInstalled(string Tag, LlamaCppVariant Variant, DateTime InstalledUtc, string? Version);

/// <summary>A step of an install, for the progress text: "find", "download", "verify", "extract", "check", "swap".</summary>
public readonly record struct LlamaCppProgress(string Stage, long Done, long Total);

/// <summary>
/// Downloads the newest llama.cpp Windows build (llama-server + its llama.dll) into
/// <see cref="LlamaBackendInfo.ManagedBackendDir"/>, so a GGUF whose architecture the bundled LLamaSharp is too old
/// for (Qwen3.5 = "qwen35" on LLamaSharp 0.26) loads through llama-server with one click instead of a script.
///
/// Why each step:
///   • Release: ggml-org's "latest" is a source-only tag (v0.9.0, 2026-11-04, no binaries); the b#### builds that
///     carry the zips are marked prerelease. So the list is read and the newest release that has THIS PC's zip wins.
///   • Variant: an NVIDIA card at compute capability ≥ 7.5 takes the CUDA 13 build, an older one (GTX 10xx = 6.1)
///     the CUDA 12 build (CUDA 13 dropped it), any other GPU Vulkan, none the CPU build. A CUDA build that then lists
///     no CUDA device is replaced by the Vulkan one (when the variant was chosen automatically).
///   • Every file is checked against the sha256 GitHub publishes for it, size-capped, and every zip entry must stay
///     inside the staging folder (zip-slip). The new llama-server must answer --version before it replaces anything.
///   • Swap: the old folder is renamed aside, the new one moved in, the old one deleted — a failed install leaves
///     the working backend as it was. A backend in use (its llama-server running) is never touched.
/// One install at a time (Setup page and Models page share it). Throws <see cref="InvalidOperationException"/>
/// whose message is a "llamacpp.err.*" localization key.
/// </summary>
public sealed class LlamaCppInstaller
{
    public const string ReleasesApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20";
    public const string InfoFile = "cluadex-llama.json";
    public const long MaxAssetBytes = 1L << 30;   // 1 GB: the CUDA runtime zip is ~420 MB

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly HttpClient SharedHttp = CreateHttp();

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly DebugLogService? _log;

    /// <summary>The folder the backend is installed into.</summary>
    public string TargetDir { get; }

    /// <summary>Runs a tool and returns (exit code, stdout+stderr); a test swaps in a fake (its llama-server is not real).</summary>
    public Func<string, string, TimeSpan, CancellationToken, Task<(int Exit, string Output)>> RunTool { get; init; } = RunProcessAsync;

    /// <summary>How the variant is chosen when none is given: (NVIDIA compute capability, any non-NVIDIA GPU).</summary>
    public Func<(double? NvidiaCc, bool OtherGpu)> DetectGpu { get; init; } = DetectGpuDefault;

    public LlamaCppInstaller(DebugLogService? log = null, HttpClient? http = null, string? releasesApi = null, string? targetDir = null)
    {
        _log = log;
        _http = http ?? SharedHttp;
        _api = releasesApi ?? ReleasesApi;
        TargetDir = targetDir ?? LlamaBackendInfo.ManagedBackendDir;
    }

    public static bool IsBusy => OneAtATime.CurrentCount == 0;

    // ═══════════════════════════════════════════════════════════════════
    //  Choosing
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The build for this GPU (see the class summary).</summary>
    public static LlamaCppVariant PickVariant(double? nvidiaComputeCapability, bool otherGpu)
    {
        if (nvidiaComputeCapability is { } cc && cc > 0) return cc >= 7.5 ? LlamaCppVariant.Cuda13 : LlamaCppVariant.Cuda12;
        return otherGpu ? LlamaCppVariant.Vulkan : LlamaCppVariant.Cpu;
    }

    private static Regex MainPattern(LlamaCppVariant v) => v switch
    {
        LlamaCppVariant.Cuda12 => new(@"^llama-b?[\w.-]*-bin-win-cuda-12(\.\d+)*-x64\.zip$", RegexOptions.IgnoreCase),
        LlamaCppVariant.Cuda13 => new(@"^llama-b?[\w.-]*-bin-win-cuda-13(\.\d+)*-x64\.zip$", RegexOptions.IgnoreCase),
        LlamaCppVariant.Vulkan => new(@"^llama-b?[\w.-]*-bin-win-vulkan-x64\.zip$", RegexOptions.IgnoreCase),
        _ => new(@"^llama-b?[\w.-]*-bin-win-cpu-x64\.zip$", RegexOptions.IgnoreCase),
    };

    private static Regex? RuntimePattern(LlamaCppVariant v) => v switch
    {
        LlamaCppVariant.Cuda12 => new(@"^cudart-llama-bin-win-cuda-12(\.\d+)*-x64\.zip$", RegexOptions.IgnoreCase),
        LlamaCppVariant.Cuda13 => new(@"^cudart-llama-bin-win-cuda-13(\.\d+)*-x64\.zip$", RegexOptions.IgnoreCase),
        _ => null,
    };

    /// <summary>
    /// The newest release in a GitHub releases list (newest first, as the API returns it) that has the zip for this
    /// variant — and its CUDA runtime zip for a CUDA build. Drafts and releases without that zip are skipped.
    /// </summary>
    public static LlamaCppRelease? ChooseRelease(string releasesJson, LlamaCppVariant variant, bool allowLoopbackHttp = false)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var main = MainPattern(variant);
        var runtime = RuntimePattern(variant);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            LlamaCppAsset? m = null, r = null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (m == null && main.IsMatch(name)) m = ToAsset(a, name, allowLoopbackHttp);
                else if (r == null && runtime?.IsMatch(name) == true) r = ToAsset(a, name, allowLoopbackHttp);
            }
            if (m == null || runtime != null && r == null) continue;
            string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "?" : "?";
            return new LlamaCppRelease(tag, variant, m, r);
        }
        return null;
    }

    private static LlamaCppAsset? ToAsset(JsonElement a, string name, bool allowLoopbackHttp)
    {
        string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && uri.IsLoopback)) return null;
        long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long z) ? z : 0;
        string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } dig && dig.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? dig[7..].ToLowerInvariant() : null;
        return new LlamaCppAsset(name, url, size, sha);
    }

    /// <summary>The release that would be installed (the list is read, nothing is downloaded).</summary>
    public async Task<LlamaCppRelease> FindReleaseAsync(LlamaCppVariant variant, CancellationToken ct = default)
    {
        string json;
        try { json = await _http.GetStringAsync(_api, ct); }
        catch (HttpRequestException ex) { _log?.Warn("LlamaCpp", "release list failed", ex); throw new InvalidOperationException("llamacpp.err.network", ex); }
        // Plain http only for a loopback list (the test mock); GitHub's list must point at https files.
        bool loopback = Uri.TryCreate(_api, UriKind.Absolute, out var api) && api.IsLoopback;
        return ChooseRelease(json, variant, loopback) ?? throw new InvalidOperationException("llamacpp.err.noRelease");
    }

    /// <summary>The variant an automatic install picks on this PC.</summary>
    public LlamaCppVariant AutoVariant()
    {
        var (cc, other) = DetectGpu();
        return PickVariant(cc, other);
    }

    /// <summary>What <paramref name="dir"/> holds, from the installer's note (null when the folder was not ours).</summary>
    public static LlamaCppInstalled? ReadInstalled(string? dir = null)
    {
        try
        {
            string file = Path.Combine(dir ?? LlamaBackendInfo.ManagedBackendDir, InfoFile);
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<LlamaCppInstalled>(File.ReadAllText(file));
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Installing
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Download, verify, test and swap in the newest build. <paramref name="variant"/> null = this PC's (and Vulkan
    /// when that CUDA build finds no CUDA device).
    /// </summary>
    public async Task<LlamaCppInstalled> InstallAsync(LlamaCppVariant? variant = null, IProgress<LlamaCppProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!await OneAtATime.WaitAsync(0, ct)) throw new InvalidOperationException("llamacpp.err.busy");
        try
        {
            LlamaCppVariant v = variant ?? AutoVariant();
            try { return await InstallOnceAsync(v, progress, ct); }
            catch (InvalidOperationException ex) when (ex.Message == "llamacpp.err.noCudaDevice" && variant == null)
            {
                _log?.Warn("LlamaCpp", $"{v} build listed no CUDA device here · installing the Vulkan build instead");
                return await InstallOnceAsync(LlamaCppVariant.Vulkan, progress, ct);
            }
        }
        finally { OneAtATime.Release(); }
    }

    private async Task<LlamaCppInstalled> InstallOnceAsync(LlamaCppVariant variant, IProgress<LlamaCppProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("find", 0, 0));
        var release = await FindReleaseAsync(variant, ct);
        _log?.Info("LlamaCpp", $"installing {release.Tag} {variant} ({release.TotalBytes / 1048576} MB) into {TargetDir}");

        string parent = Path.GetDirectoryName(Path.GetFullPath(TargetDir)) ?? throw new InvalidOperationException("llamacpp.err.target");
        Directory.CreateDirectory(parent);
        string id = Guid.NewGuid().ToString("N")[..8];
        string download = Path.Combine(Path.GetTempPath(), "cluadex-llama-dl-" + id);
        string staging = Path.Combine(parent, Path.GetFileName(TargetDir) + ".new-" + id);
        string aside = Path.Combine(parent, Path.GetFileName(TargetDir) + ".old-" + id);
        try
        {
            Directory.CreateDirectory(download);
            Directory.CreateDirectory(staging);
            long done = 0;
            var assets = release.Runtime == null ? new[] { release.Main } : new[] { release.Main, release.Runtime };
            foreach (var asset in assets)
            {
                string zip = Path.Combine(download, Path.GetFileName(asset.Name));
                long before = done;
                await DownloadVerifiedAsync(asset, zip, n => progress?.Report(new("download", before + n, release.TotalBytes)), ct);
                done += asset.Size;
                progress?.Report(new("extract", done, release.TotalBytes));
                await Task.Run(() => ExtractInside(zip, staging), ct);
                File.Delete(zip);
            }

            // The zips are flat today; a build that nests its binaries in a folder is followed to it.
            string bin = File.Exists(Path.Combine(staging, "llama-server.exe")) ? staging
                : Directory.EnumerateFiles(staging, "llama-server.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                  ?? throw new InvalidOperationException("llamacpp.err.zip");
            if (!File.Exists(Path.Combine(bin, "llama.dll"))) throw new InvalidOperationException("llamacpp.err.zip");
            if (!SamePath(bin, staging))
            {
                // CUDA runtime dlls extracted at the root belong beside llama-server.
                foreach (var f in Directory.EnumerateFiles(staging, "*.dll"))
                    File.Move(f, Path.Combine(bin, Path.GetFileName(f)), overwrite: true);
            }

            progress?.Report(new("check", release.TotalBytes, release.TotalBytes));
            string exe = Path.Combine(bin, "llama-server.exe");
            var (exit, output) = await RunTool(exe, "--version", TimeSpan.FromSeconds(30), ct);
            if (exit != 0 || !output.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Warn("LlamaCpp", $"new llama-server --version exit {exit}: {Providers.CliRefusals.ForLog(output)}");
                throw new InvalidOperationException("llamacpp.err.check");
            }
            string? version = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("version", StringComparison.OrdinalIgnoreCase));
            if (variant is LlamaCppVariant.Cuda12 or LlamaCppVariant.Cuda13)
            {
                var (_, devices) = await RunTool(exe, "--list-devices", TimeSpan.FromSeconds(30), ct);
                if (!devices.Contains("CUDA", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Warn("LlamaCpp", $"{variant} --list-devices shows no CUDA device: {Providers.CliRefusals.ForLog(devices)}");
                    throw new InvalidOperationException("llamacpp.err.noCudaDevice");
                }
            }

            var info = new LlamaCppInstalled(release.Tag, variant, DateTime.UtcNow, version);
            File.WriteAllText(Path.Combine(bin, InfoFile), JsonSerializer.Serialize(info));

            progress?.Report(new("swap", release.TotalBytes, release.TotalBytes));
            Swap(bin, staging, aside);
            LlamaBackendInfo.ClearCache();
            _log?.Info("LlamaCpp", $"installed {release.Tag} {variant} · {version}");
            return info;
        }
        finally
        {
            TryDelete(download);
            TryDelete(staging);
            TryDelete(aside);
        }
    }

    /// <summary>Stream one asset to disk with a size cap and the sha256 GitHub published for it.</summary>
    private async Task DownloadVerifiedAsync(LlamaCppAsset asset, string file, Action<long> onBytes, CancellationToken ct)
    {
        if (asset.Size > MaxAssetBytes) throw new InvalidOperationException("llamacpp.err.tooBig");
        using var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Warn("LlamaCpp", $"{asset.Name}: HTTP {(int)resp.StatusCode}");
            throw new InvalidOperationException("llamacpp.err.network");
        }
        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            long total = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > MaxAssetBytes || asset.Size > 0 && total > asset.Size) throw new InvalidOperationException("llamacpp.err.tooBig");
                sha.TransformBlock(buf, 0, n, null, 0);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                if (total - lastReport >= 1 << 20) { lastReport = total; onBytes(total); }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            onBytes(total);
            if (asset.Size > 0 && total != asset.Size) throw new InvalidOperationException("llamacpp.err.digest");
        }
        string got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (asset.Sha256 != null && got != asset.Sha256)
        {
            _log?.Warn("LlamaCpp", $"{asset.Name}: sha256 {got} ≠ published {asset.Sha256}");
            throw new InvalidOperationException("llamacpp.err.digest");
        }
        if (asset.Sha256 == null) _log?.Warn("LlamaCpp", $"{asset.Name}: no published sha256 · size checked only ({got})");
    }

    /// <summary>Extract a zip; an entry that would land outside <paramref name="dir"/> refuses the whole file.</summary>
    public static void ExtractInside(string zipFile, string dir)
    {
        string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            foreach (var e in zip.Entries)
            {
                string dest = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("llamacpp.err.zip");
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }
        catch (InvalidDataException ex) { throw new InvalidOperationException("llamacpp.err.zip", ex); }
    }

    /// <summary>Old aside, new in, old deleted by the caller; the old comes back if the new cannot be moved in.</summary>
    private void Swap(string bin, string staging, string aside)
    {
        if (Directory.Exists(TargetDir))
        {
            if (ServerRunningFrom(TargetDir)) throw new InvalidOperationException("llamacpp.err.inUse");
            try { Directory.Move(TargetDir, aside); }
            catch (IOException ex) { _log?.Warn("LlamaCpp", "old backend is locked", ex); throw new InvalidOperationException("llamacpp.err.inUse", ex); }
            catch (UnauthorizedAccessException ex) { _log?.Warn("LlamaCpp", "old backend is locked", ex); throw new InvalidOperationException("llamacpp.err.inUse", ex); }
        }
        try { Directory.Move(bin, TargetDir); }
        catch
        {
            if (Directory.Exists(aside) && !Directory.Exists(TargetDir)) Directory.Move(aside, TargetDir);
            throw;
        }
        if (!SamePath(bin, staging)) TryDelete(staging);
    }

    private static bool ServerRunningFrom(string dir)
    {
        string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try { if (p.MainModule?.FileName is { } f && f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { /* another user's process: not ours */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* a leftover in temp; the next install uses a new name */ }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Defaults
    // ═══════════════════════════════════════════════════════════════════

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-llamacpp-installer");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static (double? NvidiaCc, bool OtherGpu) DetectGpuDefault()
    {
        double? cc = GpuDetectionService.NvidiaComputeCapability();
        if (cc != null) return (cc, false);
        bool other = false;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                string name = o["Name"]?.ToString() ?? "";
                if (Regex.IsMatch(name, "AMD|Radeon|Intel.*(Arc|Iris|UHD)|NVIDIA", RegexOptions.IgnoreCase)) { other = true; break; }
            }
        }
        catch { }
        return (null, other);
    }

    /// <summary>Run a tool with stdout and stderr read together, killed at the timeout. Never throws for a bad exit.</summary>
    public static async Task<(int Exit, string Output)> RunProcessAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                return (-1, "timeout");
            }
            return (p.ExitCode, await outTask + "\n" + await errTask);
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, ex.Message); }
    }
}
