using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>What a DGX Spark (or any box on the LAN) is serving.</summary>
public sealed record DgxSparkProbe(string Host, string? Backend, string? Version, int? Port, IReadOnlyList<string> Models, string? Error)
{
    public bool Reachable => Backend != null;
}

/// <summary>A model that fits a DGX Spark's 128 GB of unified memory, with what published tests measured.
/// <paramref name="UseKey"/> is the localization key of what it is good for.</summary>
public sealed record SparkModelPick(string OllamaTag, string Name, string Size, string Speed, string UseKey);

/// <summary>
/// CluadeX on an NVIDIA DGX Spark. The Spark runs DGX OS (Linux, ARM64) and the desktop app runs on Windows,
/// so CluadeX does not run ON the Spark — it drives the model the Spark serves over the local network:
/// Ollama (port 11434, the setup NVIDIA's own Spark playbooks use), llama.cpp's llama-server (8080) or vLLM
/// (8000). With 128 GB of memory the Spark runs 120B-class models that no 8–24 GB desktop GPU can hold.
/// </summary>
public class DgxSparkService
{
    /// <summary>The XMAN Studio campaign page — the only purchase link the app shows (owner rule).</summary>
    public const string CampaignUrl = "https://xman4289.com/dgx-spark";

    /// <summary>Models worth running on a Spark, best first. Speeds are measured figures from NVIDIA's developer
    /// blog and a single-Spark benchmark on the NVIDIA developer forum (decode tokens/s, single request); they
    /// vary with quantisation, context length and serving stack.</summary>
    public static readonly IReadOnlyList<SparkModelPick> RecommendedModels =
    [
        // gpt-oss-120b: Ollama 42.1 · llama.cpp 55.4 · vLLM 60.7     qwen3-coder-next: Ollama 59.2 · vLLM 74.8
        // gpt-oss-20b: llama.cpp 82.7                              Qwen3-30B-A3B: vLLM 77.1 · Ollama 85.2
        // (NVIDIA developer blog "How NVIDIA DGX Spark's performance enables intensive AI tasks" for llama.cpp;
        //  NVIDIA developer forum "Measured inference benchmarks on a single DGX Spark" for Ollama / vLLM.)
        new("gpt-oss:120b", "gpt-oss 120B", "≈ 65 GB", "≈ 42–61 tok/s", "spark.pick.gptoss120"),
        new("qwen3-coder-next", "Qwen3-Coder-Next (80B MoE)", "≈ 52 GB", "≈ 59–75 tok/s", "spark.pick.qwen3coder"),
        new("gpt-oss:20b", "gpt-oss 20B", "≈ 14 GB", "≈ 83 tok/s", "spark.pick.gptoss20"),
        new("qwen3:30b", "Qwen3 30B-A3B", "≈ 19 GB", "≈ 77–85 tok/s", "spark.pick.qwen30"),
    ];

    /// <summary>Ollama's port on the Spark. A debug build reads CLUADEX_SPARK_OLLAMA_PORT so the contract harness can
    /// stand a localhost mock in for the Spark without colliding with a real Ollama on this PC. Read on every call,
    /// never cached in a static field: a cached value is fixed at the type's first use, which in the harness came
    /// before the override was set — and its "mock" download went to the real Ollama.</summary>
    private static int OllamaPort =>
#if DEBUG
        int.TryParse(Environment.GetEnvironmentVariable("CLUADEX_SPARK_OLLAMA_PORT"), out int port) && port > 0 ? port :
#endif
        11434;

    /// <summary>Context window CluadeX asks a Spark-served model for. 128 GB leaves room for a long window.</summary>
    public const int SparkContextSize = 32768;

    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$|^\[[0-9A-Fa-f:]+\]$", RegexOptions.Compiled);

    private readonly SettingsService _settings;
    private readonly AiProviderManager _providers;
    private readonly DebugLogService? _log;
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _pull = new() { Timeout = Timeout.InfiniteTimeSpan };

    public DgxSparkService(SettingsService settings, AiProviderManager providers, DebugLogService? log = null)
    {
        _settings = settings;
        _providers = providers;
        _log = log;
        // OnProviderChanged runs before the new provider initialises, so a local llama-server never starts with
        // the Spark's window. Also checked once now, for a switch that happened while this service didn't exist.
        _providers.OnProviderChanged += _ => RestoreContextIfLeftSpark();
        RestoreContextIfLeftSpark();
    }

    /// <summary>"spark-1a2b.local", "192.168.1.50", "http://spark:11434/" → the bare host (no scheme, port or path),
    /// or null when it isn't a host name / IP at all.</summary>
    public static string? NormalizeHost(string? input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (Uri.TryCreate(s.Contains("://") ? s : "http://" + s, UriKind.Absolute, out var u))
            s = u.HostNameType == UriHostNameType.IPv6 ? $"[{u.IdnHost.Trim('[', ']')}]" : u.IdnHost;
        if (s.StartsWith('[') && !s.EndsWith(']')) s += "]";
        return HostPattern.IsMatch(s) ? s : null;
    }

    /// <summary>Ask the box what it serves. Every backend is tried at once; Ollama wins when several answer.</summary>
    public async Task<DgxSparkProbe> ProbeAsync(string hostInput, CancellationToken ct = default)
    {
        string? host = NormalizeHost(hostInput);
        if (host == null) return new DgxSparkProbe(hostInput, null, null, null, [], "invalid-host");

        var ollama = ProbeOllamaAsync(host, ct);
        var llama = ProbeOpenAiCompatibleAsync(host, 8080, "llama.cpp", ct);
        var vllm = ProbeOpenAiCompatibleAsync(host, 8000, "vLLM", ct);
        await Task.WhenAll(ollama, llama, vllm);

        var hit = new[] { ollama.Result, llama.Result, vllm.Result }.FirstOrDefault(p => p.Reachable);
        _log?.Info("DgxSpark", hit != null
            ? $"{host}: {hit.Backend} {hit.Version} · {hit.Models.Count} model(s)"
            : $"{host}: nothing answered on 11434 / 8080 / 8000");
        return hit ?? new DgxSparkProbe(host, null, null, null, [], "unreachable");
    }

    private async Task<DgxSparkProbe> ProbeOllamaAsync(string host, CancellationToken ct)
    {
        try
        {
            string root = $"http://{host}:{OllamaPort}";
            using var ver = await _probe.GetAsync(root + "/api/version", ct);
            if (!ver.IsSuccessStatusCode) return Miss(host);
            string? version = null;
            using (var doc = JsonDocument.Parse(await ver.Content.ReadAsStringAsync(ct)))
                if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();

            var models = new List<string>();
            using var tags = await _probe.GetAsync(root + "/api/tags", ct);
            if (tags.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var m in arr.EnumerateArray())
                        if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) models.Add(name);
            }
            return new DgxSparkProbe(host, "Ollama", version, OllamaPort, models, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Miss(host);
        }
    }

    private async Task<DgxSparkProbe> ProbeOpenAiCompatibleAsync(string host, int port, string backend, CancellationToken ct)
    {
        try
        {
            using var r = await _probe.GetAsync($"http://{host}:{port}/v1/models", ct);
            if (!r.IsSuccessStatusCode) return Miss(host);
            var models = new List<string>();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name) models.Add(name);
            return new DgxSparkProbe(host, backend, null, port, models, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Miss(host);
        }
    }

    private static DgxSparkProbe Miss(string host) => new(host, null, null, null, [], null);

    /// <summary>
    /// Download a model onto the Spark (Ollama /api/pull, streamed). Reports (status, fraction 0–1 or null).
    /// The download happens on the Spark itself — nothing large comes to this PC.
    /// </summary>
    public async Task PullAsync(string host, string model, IProgress<(string Status, double? Fraction)> progress, CancellationToken ct)
    {
        string? h = NormalizeHost(host) ?? throw new ArgumentException("invalid host", nameof(host));
        string body = JsonSerializer.Serialize(new { model, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{h}:{OllamaPort}/api/pull")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _pull.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());
            string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            double? fraction = root.TryGetProperty("total", out var t) && t.TryGetInt64(out long total) && total > 0
                && root.TryGetProperty("completed", out var c) && c.TryGetInt64(out long done)
                ? Math.Clamp((double)done / total, 0, 1) : null;
            progress.Report((status, fraction));
        }
        _log?.Info("DgxSpark", $"{h}: pulled {model}");
    }

    /// <summary>Make the Spark's model the active one: the Ollama provider pointed at the Spark, a long window.</summary>
    public async Task UseOllamaModelAsync(string host, string model, CancellationToken ct = default)
    {
        string h = NormalizeHost(host) ?? throw new ArgumentException("invalid host", nameof(host));
        _settings.UpdateSettings(s =>
        {
            if (!s.ProviderConfigs.TryGetValue("Ollama", out var cfg) || cfg == null)
                s.ProviderConfigs["Ollama"] = cfg = new ProviderConfig();
            cfg.BaseUrl = $"http://{h}:{OllamaPort}";
            cfg.SelectedModel = model;
            cfg.UseCustomModel = false;
            if (s.ContextSize < SparkContextSize)
            {
                s.DgxSparkSavedContextSize ??= s.ContextSize;
                s.ContextSize = SparkContextSize;
            }
            s.DgxSparkHost = h;
        });
        // SwitchProviderAsync is a no-op when Ollama is already active — re-initialise so the new address and
        // model take effect instead of the old local server staying in use.
        if (_providers.ActiveProviderType == AiProviderType.Ollama)
            await _providers.ReinitializeActiveAsync(ct);
        else
            await _providers.SwitchProviderAsync(AiProviderType.Ollama, ct);
        _log?.Info("DgxSpark", $"using {model} on {h} through Ollama");
    }

    /// <summary>CluadeX moved off the Spark: give the context size back to what it was, before a local load reads it.</summary>
    private void RestoreContextIfLeftSpark()
    {
        if (_settings.Settings.DgxSparkSavedContextSize is not uint saved || IsActive) return;
        _settings.UpdateSettings(s =>
        {
            s.ContextSize = saved;
            s.DgxSparkSavedContextSize = null;
        });
        _log?.Info("DgxSpark", $"left the Spark — context size back to {saved}");
    }

    /// <summary>True when the active provider is the Ollama server on the remembered Spark.</summary>
    public bool IsActive
    {
        get
        {
            var s = _settings.Settings;
            if (s.ActiveProvider != AiProviderType.Ollama || string.IsNullOrEmpty(s.DgxSparkHost)) return false;
            return s.ProviderConfigs.TryGetValue("Ollama", out var cfg)
                && (cfg?.BaseUrl ?? "").Contains(s.DgxSparkHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}
