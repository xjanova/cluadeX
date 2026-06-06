using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Computes text embeddings via Ollama's /api/embeddings (e.g. nomic-embed-text), used to semantically
/// re-rank codebase_search candidates. FULLY graceful: any failure (Ollama not running, model not
/// pulled, network, bad response) returns null so the caller falls back to keyword ranking.
///
/// Design note (hidden-bug avoidance): there is NO persistent vector index. Embeddings are computed on
/// demand for the query + a small bounded candidate set, so there is nothing to go stale when files
/// change, and no async indexing pipeline to race. The caller must embed the QUERY first and bail to
/// keyword ranking if that returns null — that way a missing Ollama costs exactly one fast failed call,
/// not one per candidate.
/// </summary>
public class EmbeddingService
{
    private readonly SettingsService _settings;

    // A single shared client with a short timeout — embedding calls are local and should be fast;
    // we never want one to hang the search.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public EmbeddingService(SettingsService settings) => _settings = settings;

    public bool Enabled => _settings.Settings.SemanticSearchEnabled;

    private string BaseUrl()
    {
        if (_settings.Settings.ProviderConfigs.TryGetValue("Ollama", out var cfg)
            && !string.IsNullOrWhiteSpace(cfg.BaseUrl))
            return cfg.BaseUrl.TrimEnd('/');
        return "http://localhost:11434";
    }

    /// <summary>Embed a single text. Returns null on any failure (caller falls back to keyword ranking).</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            string model = string.IsNullOrWhiteSpace(_settings.Settings.EmbeddingModel)
                ? "nomic-embed-text"
                : _settings.Settings.EmbeddingModel;

            string body = JsonSerializer.Serialize(new { model, prompt = text });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl()}/api/embeddings")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("embedding", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var vec = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var v in arr.EnumerateArray())
                vec[i++] = (float)v.GetDouble();
            return vec.Length > 0 ? vec : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cosine similarity in [-1, 1]; 0 for empty / mismatched / zero vectors.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
