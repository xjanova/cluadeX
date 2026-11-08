using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Colibri;

/// <summary>What the plugin keeps about itself (plugins\colibri\plugin.json).</summary>
public sealed class ColibriPluginState
{
    /// <summary>The release tag the engine folder in use came from ("v2.1.0"), or null before the first install.</summary>
    public string? InstalledTag { get; set; }
    /// <summary>The release before the last update — kept on disk so an update that misbehaves can be rolled back.</summary>
    public string? PreviousTag { get; set; }
    /// <summary>The catalog id the user chose ("qwen36-35b"): an update re-runs the setup for the same model.</summary>
    public string? ModelId { get; set; }
    /// <summary>Where the models are downloaded (big: 20-500 GB).</summary>
    public string? ModelsDir { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    public string? LatestSeenTag { get; set; }
}

/// <summary>
/// The colibri plugin (owner 2026-11-08: "ทำเรื่อง colibri ต่อเป็น plugin เสริม … ให้มันเรียกมาติดตั้งเอง เรามีหน้าปลั๊กอินอยู่แล้ว").
/// colibri (github.com/JustVugg/colibri, Apache-2.0) runs big mixture-of-experts models on a small GPU: the dense part in
/// RAM, the routed experts streamed from the SSD, the hottest ones on the card. CluadeX does not reimplement any of it —
/// it downloads colibri's own Windows release (checksum-verified), drives its documented machine interface
/// (`coli setup --list --json`, `coli setup --yes`, `coli status --json`, `coli start/stop`) and points the
/// "Colibri (local)" provider at the server it starts. Everything lives under &lt;DataRoot&gt;\plugins\colibri:
/// engine\&lt;tag&gt; (one folder per release; the previous one is kept for a rollback), home (colibri's own config, state and
/// logs: COLI_SETUP_HOME), plugin.json. The models go where the user says (they are the big part).
/// </summary>
public sealed class ColibriService
{
    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;
    private static readonly HttpClient Http = CreateHttp();
    private readonly SemaphoreSlim _busy = new(1, 1);

    public ColibriService(SettingsService settings, DebugLogService? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public string Root => Path.Combine(_settings.DataRoot, "plugins", "colibri");
    public string EngineRoot => Path.Combine(Root, "engine");
    public string Home => Path.Combine(Root, "home");
    private string StatePath => Path.Combine(Root, "plugin.json");
    public string SetupLogPath => Path.Combine(Root, "setup.log");

    public string? EngineDir(string? tag) => string.IsNullOrWhiteSpace(tag) ? null : Path.Combine(EngineRoot, SafeTag(tag));
    public string? InstalledEngineDir => State.InstalledTag is { } t && EngineDir(t) is { } d && File.Exists(Path.Combine(d, "coli")) ? d : null;
    public bool IsInstalled => InstalledEngineDir != null;

    // ─── state ───

    private ColibriPluginState? _state;
    public ColibriPluginState State
    {
        get
        {
            if (_state != null) return _state;
            try { _state = File.Exists(StatePath) ? JsonSerializer.Deserialize<ColibriPluginState>(File.ReadAllText(StatePath)) : null; }
            catch (Exception ex) { _log?.Warn("Colibri", "plugin.json unreadable — starting fresh", ex); }
            return _state ??= new ColibriPluginState();
        }
    }

    /// <summary>The catalog id of the model the plugin set up ("qwen36-35b-a3b"), read from plugin.json under
    /// <paramref name="dataRoot"/>: it names the size when the running server's own model id does not. Null when none.</summary>
    public static string? CatalogModelId(string dataRoot)
    {
        try
        {
            string path = Path.Combine(dataRoot, "plugins", "colibri", "plugin.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<ColibriPluginState>(File.ReadAllText(path))?.ModelId : null;
        }
        catch { return null; }
    }

    public void SaveState()
    {
        Directory.CreateDirectory(Root);
        string tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatePath, overwrite: true);
    }

    /// <summary>Where models go unless the user chose: colibri's own default (~\colibri-models) on the fixed drive with the
    /// most free space — the models are tens to hundreds of GB.</summary>
    /// <remarks>Never a USB or spinning drive: the experts are read from it on every token (owner's E: is a USB HDD).
    /// Calls WMI — keep it off the UI thread.</remarks>
    public string DefaultModelsDir()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !IsSlowDrive(d.RootDirectory.FullName))
                .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
            if (best != null && !string.Equals(best.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(best.RootDirectory.FullName, "colibri-models");
        }
        catch { }
        return Path.Combine(profile, "colibri-models");
    }

    public string ModelsDir => string.IsNullOrWhiteSpace(State.ModelsDir) ? (State.ModelsDir = DefaultModelsDir()) : State.ModelsDir!;

    /// <summary>A drive the models should not stream from: removable, network, USB-attached, or a spinning disk. WMI
    /// (root\Microsoft\Windows\Storage); false when it cannot tell.</summary>
    public static bool IsSlowDrive(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            if (root.Length < 2 || root[1] != ':') return true;   // UNC: a network share
            var di = new DriveInfo(root);
            if (di.DriveType is DriveType.Removable or DriveType.Network or DriveType.CDRom) return true;
            char letter = char.ToUpperInvariant(root[0]);
            var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var parts = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'"));
            foreach (System.Management.ManagementObject part in parts.Get())
            {
                uint disk = Convert.ToUInt32(part["DiskNumber"]);
                using var disks = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery($"SELECT BusType FROM MSFT_Disk WHERE Number = {disk}"));
                foreach (System.Management.ManagementObject d in disks.Get())
                    if (Convert.ToUInt32(d["BusType"]) == 7) return true;   // 7 = USB
                using var phys = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"));
                foreach (System.Management.ManagementObject pd in phys.Get())
                    if (Convert.ToUInt32(pd["MediaType"]) == 3) return true;   // 3 = HDD
            }
        }
        catch { }
        return false;
    }

    // ─── Python (colibri's launcher, API gateway and setup are Python; the engines are C) ───

    /// <summary>colibri's Python code uses <c>@dataclass(slots=True)</c>, which needs 3.10.</summary>
    public static readonly Version MinPython = new(3, 10);

    /// <summary>The interpreter paths `py -0p` lists, in its order (newest first). Both the classic launcher
    /// (" -3.12-64 *   C:\...\python.exe") and the install manager (" -V:3.12 *   C:\...") formats are read. Asking
    /// `py -3.N` for each version instead would let the install manager offer to download a missing one.</summary>
    public static IEnumerable<string> ParsePyListPaths(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*-(?:V:)?\d\S*\s+(?:\*\s+)?(.+?)\s*$");
            if (m.Success) yield return m.Groups[1].Value;
        }
    }

    /// <summary>Candidates in the order they are tried. `py -3` alone is not enough: it can resolve to an older 3.x that
    /// another product registered (Visual Studio ships a 3.9), so every interpreter the launcher knows comes next, then
    /// the bare names on PATH.</summary>
    private async Task<List<(string Exe, string[] Prefix)>> PythonCandidatesAsync(CancellationToken ct)
    {
        var list = new List<(string Exe, string[] Prefix)> { ("py", new[] { "-3" }) };
        var listed = await RunAsync("py", new[] { "-0p" }, TimeSpan.FromSeconds(15), ct, env: false);
        if (listed.Code == 0) list.AddRange(ParsePyListPaths(listed.Out).Select(p => (p, Array.Empty<string>())));
        list.Add(("python", Array.Empty<string>()));
        list.Add(("python3", Array.Empty<string>()));
        return list;
    }

    /// <summary>Parse "major minor" printed by the probe; null when it is not a version.</summary>
    public static Version? ParsePythonVersion(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) ? new Version(major, minor) : null;
    }

    /// <summary>The Python (3.10 or newer) to run colibri with: the py launcher first (stays right with several versions
    /// installed), then python on PATH. Null when there is none new enough.</summary>
    public async Task<(string Exe, string[] Prefix)?> FindPythonAsync(CancellationToken ct = default)
    {
        foreach (var (exe, prefix) in await PythonCandidatesAsync(ct))
        {
            var r = await RunAsync(exe, prefix.Concat(new[] { "-c", "import sys;print(sys.version_info[0], sys.version_info[1])" }), TimeSpan.FromSeconds(15), ct, env: false);
            if (r.Code == 0 && ParsePythonVersion(r.Out) is { } v && v.Major == 3 && v >= MinPython) return (exe, prefix);
        }
        return null;
    }

    // ─── GitHub release ───

    public async Task<ColibriRelease?> CheckLatestAsync(CancellationToken ct = default)
    {
        string json = await Http.GetStringAsync(ColibriFacts.LatestReleaseApi, ct);
        var release = ColibriFacts.PickRelease(json);
        State.LastUpdateCheck = DateTime.Now;
        if (release != null) State.LatestSeenTag = release.Tag;
        try { SaveState(); } catch { }
        return release;
    }

    /// <summary>
    /// Download a release's Windows archive (and the CUDA one for an Ampere-or-newer NVIDIA card), check both against the
    /// release's SHA256SUMS, unpack into engine\&lt;tag&gt; through a staging folder, and make it the engine in use. The
    /// engine in use before stays on disk as the rollback. The server must be stopped first (its .exe files are open).
    /// </summary>
    public async Task InstallEngineAsync(ColibriRelease release, IProgress<(string Step, double? Fraction)>? progress, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(EngineRoot);
            string sums = await Http.GetStringAsync(release.SumsUrl, ct);
            var files = new List<(string Name, string Url)> { (release.ZipName, release.ZipUrl) };
            if (release.CudaZipName != null && release.CudaZipUrl != null && ColibriFacts.WantsCuda(GpuDetectionService.NvidiaComputeCapability()))
                files.Add((release.CudaZipName, release.CudaZipUrl));

            string target = EngineDir(release.Tag)!;
            string staging = target + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            for (int i = 0; i < files.Count; i++)
            {
                var (name, url) = files[i];
                string expected = ColibriFacts.ChecksumFor(sums, name) ?? throw new InvalidOperationException("colibri.err.checksum");
                string zipPath = Path.Combine(EngineRoot, name + ".download");
                await DownloadAsync(url, zipPath, f => progress?.Report(("download " + name, (i + f) / files.Count)), ct);
                string actual;
                await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != expected) { TryDelete(zipPath); throw new InvalidOperationException("colibri.err.checksum"); }
                progress?.Report(("unpack " + name, null));
                Unpack(zipPath, staging);
                TryDelete(zipPath);
            }
            if (!File.Exists(Path.Combine(staging, "coli"))) throw new InvalidOperationException("colibri.err.noAsset");
            File.WriteAllText(Path.Combine(staging, "cluadex-release.txt"), release.Tag);

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            if (!string.Equals(State.InstalledTag, release.Tag, StringComparison.Ordinal))
            {
                // Keep exactly one rollback: the engine in use until now. Any older one goes.
                string? old = State.PreviousTag;
                State.PreviousTag = State.InstalledTag;
                State.InstalledTag = release.Tag;
                if (old != null && old != State.PreviousTag && old != State.InstalledTag && EngineDir(old) is { } od && Directory.Exists(od))
                    try { Directory.Delete(od, true); } catch (Exception ex) { _log?.Warn("Colibri", $"could not remove the old engine {old}", ex); }
            }
            SaveState();
            _log?.Info("Colibri", $"engine {release.Tag} installed ({string.Join(", ", files.Select(f => f.Name))})");
        }
        finally { _busy.Release(); }
    }

    /// <summary>Back to the engine in use before the last update. False when there is none on disk.</summary>
    public bool Rollback()
    {
        if (State.PreviousTag is not { } prev || EngineDir(prev) is not { } dir || !File.Exists(Path.Combine(dir, "coli"))) return false;
        (State.InstalledTag, State.PreviousTag) = (prev, State.InstalledTag);
        SaveState();
        _log?.Info("Colibri", $"rolled back to {prev}");
        return true;
    }

    private static void Unpack(string zipPath, string into)
    {
        string rootFull = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (!ColibriFacts.SafeEntry(e.FullName)) continue;
            string dest = Path.GetFullPath(Path.Combine(into, e.FullName));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
    }

    private static async Task DownloadAsync(string url, string path, Action<double> fraction, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        if (total > ColibriFacts.MaxArchiveBytes) throw new InvalidOperationException("colibri.err.tooBig");
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            done += n;
            if (done > ColibriFacts.MaxArchiveBytes) throw new InvalidOperationException("colibri.err.tooBig");
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            if (total > 0) fraction((double)done / total.Value);
        }
    }

    // ─── coli ───

    /// <summary>The models colibri offers, each measured against this PC and the models folder.</summary>
    public async Task<List<ColibriModelOption>> ListModelsAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDir);
        var r = await ColiAsync(new[] { "setup", "--list", "--json", "--dir", ModelsDir }, TimeSpan.FromMinutes(2), ct);
        if (r.Code != 0) throw new InvalidOperationException(Tail(r.Err.Length > 0 ? r.Err : r.Out));
        return ColibriFacts.ParseModels(r.Out);
    }

    public async Task<ColibriStatus> StatusAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return new ColibriStatus();
        var r = await ColiAsync(new[] { "status", "--json" }, TimeSpan.FromSeconds(40), ct);
        try { return ColibriFacts.ParseStatus(r.Out); }
        catch (JsonException) { return new ColibriStatus(); }
    }

    /// <summary>
    /// Install a model (download with resume, engine build choice, plan) WITHOUT starting it, as a process of its own that
    /// outlives CluadeX — the download can take hours; closing the app must not kill it. Progress is read back from
    /// <see cref="StatusAsync"/> (install.phase / done / total), the output goes to setup.log.
    /// </summary>
    public async Task StartModelSetupAsync(string modelId, CancellationToken ct = default)
    {
        if (!ModelIdOk(modelId)) throw new ArgumentException("bad model id", nameof(modelId));
        Directory.CreateDirectory(ModelsDir);
        State.ModelId = modelId;
        State.ModelsDir = ModelsDir;
        SaveState();
        File.AppendAllText(SetupLogPath, $"\n--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} setup {modelId} → {ModelsDir}\n");
        int pid = await LaunchLoggedAsync(new[] { "setup", "--yes", "--model", modelId, "--no-start", "--dir", ModelsDir }, SetupLogPath, null, ct);
        _log?.Info("Colibri", $"setup {modelId} started (pid {pid}) → {ModelsDir}");
    }

    /// <summary>Start the configured server in the background (no browser) and point the Colibri provider at it once it is ready.</summary>
    /// <remarks>Not through <see cref="ColiAsync"/>: the server it spawns inherits the stdout pipe, so reading to the end
    /// would wait for the SERVER to exit, and the timeout's tree-kill would then stop the server it just started.</remarks>
    public async Task<ColibriStatus> StartServerAsync(CancellationToken ct = default)
    {
        string log = Path.Combine(Root, "server.log");
        File.AppendAllText(log, $"\n--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} start\n");
        await LaunchLoggedAsync(new[] { "start", "--background", "--no-browser" }, log, TimeSpan.FromMinutes(2), ct);
        var s = await StatusAsync(ct);
        ApplyProvider(s);
        return s;
    }

    /// <summary>The last lines of a log the plugin writes (setup.log / server.log), for an error message.</summary>
    public string LogTail(string path)
    {
        try { return File.Exists(path) ? Tail(File.ReadAllText(path)) : ""; } catch { return ""; }
    }

    /// <summary>
    /// Run `coli &lt;args&gt;` through cmd with its output appended to <paramref name="log"/> — never a pipe back to CluadeX,
    /// so a child it leaves running (the setup's download, the background server) is not tied to this process. With a
    /// <paramref name="wait"/>, wait that long for coli itself to return; a non-zero exit is an error carrying the log's tail.
    /// Returns the cmd process id.
    /// </summary>
    private async Task<int> LaunchLoggedAsync(string[] coliArgs, string log, TimeSpan? wait, CancellationToken ct)
    {
        var py = await FindPythonAsync(ct) ?? throw new InvalidOperationException("colibri.err.noPython");
        string engine = InstalledEngineDir ?? throw new InvalidOperationException("colibri.err.notInstalled");
        Directory.CreateDirectory(Home);
        var args = py.Prefix.Concat(new[] { Path.Combine(engine, "coli") }).Concat(coliArgs);
        string line = $"\"{Quote(py.Exe)} {string.Join(' ', args.Select(Quote))} >> {Quote(log)} 2>&1\"";
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c " + line) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = engine };
        ApplyEnv(psi);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("colibri.err.start");
        int pid = p.Id;
        if (wait is { } w)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(w);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return pid; }   // still going — the status poll takes it from here
            if (p.ExitCode != 0) throw new InvalidOperationException(LogTail(log) is { Length: > 0 } t ? t : "coli exit " + p.ExitCode);
        }
        return pid;
    }

    /// <summary>A catalog id goes on a cmd line: letters, digits and . - _ only (colibri's ids are like "qwen36-35b").</summary>
    public static bool ModelIdOk(string id) => id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public async Task StopServerAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return;
        await ColiAsync(new[] { "stop" }, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>Write the server's URL and model id into the "Colibri (local)" provider's config (only while it is ready:
    /// the model id is read from the running server). True when something changed.</summary>
    public bool ApplyProvider(ColibriStatus s)
    {
        if (!s.IsReady || string.IsNullOrWhiteSpace(s.ModelId)) return false;
        string key = AiProviderType.Colibri.ToString();
        var configs = _settings.Settings.ProviderConfigs;
        if (!configs.TryGetValue(key, out var cfg)) configs[key] = cfg = new ProviderConfig();
        string url = string.IsNullOrWhiteSpace(s.OpenAiBaseUrl) ? "http://localhost:8000/v1" : s.OpenAiBaseUrl!.TrimEnd('/');
        if (cfg.BaseUrl == url && cfg.SelectedModel == s.ModelId && !cfg.UseCustomModel) return false;
        cfg.BaseUrl = url;
        cfg.SelectedModel = s.ModelId;
        cfg.UseCustomModel = false;
        _settings.Save();
        _log?.Info("Colibri", $"provider → {url} · {s.ModelId}");
        return true;
    }

    /// <summary>Remove the engine and colibri's own state (the downloaded models stay unless <paramref name="models"/>).</summary>
    public async Task UninstallAsync(bool models, CancellationToken ct = default)
    {
        try { await StopServerAsync(ct); } catch { }
        string modelsDir = ModelsDir;
        foreach (var dir in new[] { EngineRoot, Home }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (models && Directory.Exists(modelsDir) && !IsRootOrProfile(modelsDir)) Directory.Delete(modelsDir, true);
        _state = new ColibriPluginState { ModelsDir = models ? null : modelsDir };
        SaveState();
        _log?.Info("Colibri", "uninstalled" + (models ? " with its models" : ""));
    }

    private static bool IsRootOrProfile(string dir)
    {
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        return Path.GetPathRoot(full)?.TrimEnd('\\') == full
               || string.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int Code, string Out, string Err)> ColiAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var py = await FindPythonAsync(ct) ?? throw new InvalidOperationException("colibri.err.noPython");
        string engine = InstalledEngineDir ?? throw new InvalidOperationException("colibri.err.notInstalled");
        Directory.CreateDirectory(Home);
        return await RunAsync(py.Exe, py.Prefix.Concat(new[] { Path.Combine(engine, "coli") }).Concat(args), timeout, ct, env: true, cwd: engine);
    }

    private void ApplyEnv(ProcessStartInfo psi)
    {
        psi.Environment["COLI_SETUP_HOME"] = Home;
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["NO_COLOR"] = "1";
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        bool env, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd != null) psi.WorkingDirectory = cwd;
        if (env) ApplyEnv(psi);
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return (-1, "", exe + " not found"); }
        if (p == null) return (-1, "", "");
        using (p)
        {
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return (-2, "", "timed out");
            }
            return (p.ExitCode, await o, await e);
        }
    }

    private static string Quote(string s) => s.Length > 0 && !s.Any(c => c is ' ' or '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')')
        ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string SafeTag(string tag) => new(tag.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());

    private static string Tail(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(4));
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX");
        return http;
    }
}
