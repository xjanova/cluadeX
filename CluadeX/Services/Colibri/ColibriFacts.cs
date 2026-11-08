using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CluadeX.Services.Colibri;

/// <summary>A colibri release on GitHub: its tag and the Windows files CluadeX installs from it.</summary>
public sealed record ColibriRelease(string Tag, string ZipName, string ZipUrl, string? CudaZipName, string? CudaZipUrl, string SumsUrl);

/// <summary>One model of `coli setup --list --json`, measured against this PC by colibri itself.</summary>
public sealed record ColibriModelOption(string Id, string Name, string Kind, double DownloadGb, double RamMinGb, double RamGoodGb,
    bool FromRam, string Status, bool Fits, string Reason, bool Recommended, string Summary, string LicenseNote)
{
    /// <summary>Chat models that call tools are what the agent can use; pictures and decision models are not.</summary>
    public bool IsChat => Kind is "" or "text" or "vision";
}

/// <summary>`coli status --json`, the parts CluadeX shows and acts on.</summary>
public sealed record ColibriStatus
{
    public bool Configured { get; init; }
    /// <summary>The model the setup is configured for (its catalog name), or null.</summary>
    public string? Model { get; init; }
    public string? Backend { get; init; }
    /// <summary>"stopped", "loading" or "ready" — null when nothing is configured yet.</summary>
    public string? ServerState { get; init; }
    public string? OpenAiBaseUrl { get; init; }
    public string? DashboardUrl { get; init; }
    /// <summary>What API requests must send as "model" (only known while the server is ready).</summary>
    public string? ModelId { get; init; }
    public double? TokensPerSecond { get; init; }
    /// <summary>"starting", "build", "download", "ready", "started", "error", "interrupted" … (setup's own words).</summary>
    public string? InstallPhase { get; init; }
    public bool InstallRunning { get; init; }
    public long InstallDone { get; init; }
    public long InstallTotal { get; init; }
    public double InstallRate { get; init; }
    public string? InstallMessage { get; init; }

    public bool IsReady => ServerState == "ready";
    public bool IsLoading => ServerState == "loading";
    public double? InstallFraction => InstallTotal > 0 ? Math.Clamp((double)InstallDone / InstallTotal, 0, 1) : null;
}

/// <summary>
/// The pure half of the colibri plugin: which release files are ours, whether a tag is newer, and what colibri's JSON
/// says. No process, no network — <see cref="ColibriService"/> does that — so the harness can prove every rule here.
/// </summary>
public static class ColibriFacts
{
    public const string Repo = "JustVugg/colibri";
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleaseDownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    /// <summary>colibri's CUDA DLL on Windows is built for compute capability 8.0 and newer (Ampere+); older NVIDIA cards
    /// (a GTX 1070 Ti is 6.1) run through Vulkan, which the main archive already has.</summary>
    public const double CudaMinComputeCapability = 8.0;
    /// <summary>The biggest archive we accept (the 2.1.0 ones are 16 and 24 MB): a release file this big is not one.</summary>
    public const long MaxArchiveBytes = 300L * 1024 * 1024;

    /// <summary>The release's Windows archives and checksum list. Only files under the repo's own download URL count, so
    /// a release JSON can never send the download somewhere else.</summary>
    public static ColibriRelease? PickRelease(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0 || !root.TryGetProperty("assets", out var assets)) return null;
        string? zip = null, zipUrl = null, cuda = null, cudaUrl = null, sums = null;
        foreach (var a in assets.EnumerateArray())
        {
            string n = a.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
            string u = a.TryGetProperty("browser_download_url", out var uu) ? uu.GetString() ?? "" : "";
            if (!u.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal) || n.Contains('/') || n.Contains('\\')) continue;
            if (n.EndsWith("-windows-x86_64.zip", StringComparison.OrdinalIgnoreCase)) { zip = n; zipUrl = u; }
            else if (n.EndsWith("-windows-x86_64-cuda.zip", StringComparison.OrdinalIgnoreCase)) { cuda = n; cudaUrl = u; }
            else if (n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = u;
        }
        return zip == null || zipUrl == null || sums == null ? null : new ColibriRelease(tag, zip, zipUrl, cuda, cudaUrl, sums);
    }

    /// <summary>The sha256 a checksum list gives a file ("&lt;hex&gt;  name" or "&lt;hex&gt; *name"), lower-case, or null.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            string name = line[(sp + 1)..].TrimStart().TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.Ordinal) && line[..64].All(Uri.IsHexDigit))
                return line[..64].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>"v2.1.0" → 2.1.0; anything after the numbers ("-rc1") is ignored. Null when there are no numbers.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim().TrimStart('v', 'V');
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        var parts = s[..end].Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4).ToList();
        if (parts.Count == 0) return null;
        while (parts.Count < 2) parts.Add("0");
        return Version.TryParse(string.Join('.', parts), out var v) ? v : null;
    }

    /// <summary>A release is an update when its tag is a higher version than the installed one (or nothing is installed).</summary>
    public static bool IsNewer(string? latestTag, string? installedTag)
    {
        var latest = ParseTag(latestTag);
        if (latest == null) return false;
        var installed = ParseTag(installedTag);
        return installed == null || latest > installed;
    }

    /// <summary>Take the CUDA archive too: an NVIDIA card colibri's CUDA DLL was built for.</summary>
    public static bool WantsCuda(double? nvidiaComputeCapability) => nvidiaComputeCapability is >= CudaMinComputeCapability;

    /// <summary>`coli setup --list --json`: chat models first in colibri's own order (the recommended one leads).</summary>
    public static List<ColibriModelOption> ParseModels(string json)
    {
        var list = new List<ColibriModelOption>();
        int start = json.IndexOf('[');
        if (start < 0) return list;
        using var doc = JsonDocument.Parse(json[start..]);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string id = Str(e, "id");
            if (id.Length == 0) continue;
            list.Add(new ColibriModelOption(id, Str(e, "name") is { Length: > 0 } n ? n : id, Str(e, "kind"),
                Num(e, "download_gb"), Num(e, "ram_min_gb"), Num(e, "ram_good_gb"),
                Str(e, "runs").Contains("RAM", StringComparison.OrdinalIgnoreCase), Str(e, "status"), Bool(e, "fits"),
                Str(e, "reason"), Bool(e, "recommended"), Str(e, "summary"), Str(e, "license_note")));
        }
        return list;
    }

    /// <summary>The model CluadeX offers first: colibri's recommendation when it is a chat model that fits, else the
    /// first chat model that fits, else null (nothing fits this PC).</summary>
    public static ColibriModelOption? DefaultPick(IReadOnlyList<ColibriModelOption> models) =>
        models.FirstOrDefault(m => m.Recommended && m.Fits && m.IsChat) ?? models.FirstOrDefault(m => m.Fits && m.IsChat);

    /// <summary>`coli status --json`. Tolerant: a field colibri renames reads as missing, never as a crash.</summary>
    public static ColibriStatus ParseStatus(string json)
    {
        int start = json.IndexOf('{');
        if (start < 0) return new ColibriStatus();
        using var doc = JsonDocument.Parse(json[start..]);
        var r = doc.RootElement;
        var s = new ColibriStatus { Configured = r.TryGetProperty("configured", out var c) && c.ValueKind == JsonValueKind.True };
        if (r.TryGetProperty("setup", out var setup) && setup.ValueKind == JsonValueKind.Object)
        {
            string? model = setup.TryGetProperty("model", out var m) ? m.ValueKind switch
            {
                JsonValueKind.String => m.GetString(),
                JsonValueKind.Object => Str(m, "id") is { Length: > 0 } mid ? mid : Str(m, "name"),
                _ => null,
            } : null;
            s = s with { Model = string.IsNullOrEmpty(model) ? null : model, Backend = Str(setup, "backend") is { Length: > 0 } b ? b : null };
        }
        if (r.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object)
        {
            string? openai = null, browser = null;
            if (server.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
            {
                openai = Str(urls, "openai_base_url") is { Length: > 0 } o ? o : null;
                browser = Str(urls, "browser") is { Length: > 0 } w ? w : null;
            }
            s = s with
            {
                ServerState = Str(server, "state") is { Length: > 0 } st ? st : null,
                OpenAiBaseUrl = openai,
                DashboardUrl = browser,
                ModelId = Str(server, "model_id") is { Length: > 0 } id ? id : null,
                TokensPerSecond = server.TryGetProperty("tokens_per_second", out var tps) && tps.ValueKind == JsonValueKind.Number ? tps.GetDouble() : null,
                Model = s.Model ?? (Str(server, "model") is { Length: > 0 } sm ? sm : null),
                Backend = s.Backend ?? (Str(server, "backend") is { Length: > 0 } sb ? sb : null),
            };
        }
        if (r.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object)
        {
            s = s with
            {
                InstallPhase = Str(install, "phase") is { Length: > 0 } ph ? ph : null,
                InstallRunning = Bool(install, "running"),
                InstallDone = (long)Num(install, "done"),
                InstallTotal = (long)Num(install, "total"),
                InstallRate = Num(install, "rate"),
                InstallMessage = Str(install, "message") is { Length: > 0 } msg ? msg : null,
            };
        }
        return s;
    }

    /// <summary>Is an archive entry safe to unpack under the engine folder (no "..", no rooted path, no drive).</summary>
    public static bool SafeEntry(string fullName) =>
        fullName.Length > 0 && !fullName.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(fullName)
        && !fullName.Contains(':') && !fullName.StartsWith('/') && !fullName.StartsWith('\\');

    /// <summary>"23.4 GB" / "512 MB" for bytes.</summary>
    public static string Bytes(double bytes) => bytes >= 1024d * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (1024d * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
