using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CluadeX.Models;

namespace CluadeX.Services;

public class HuggingFaceService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    // ─── Curated Popular Models ───────────────────────────────────────

    private static readonly List<RecommendedModel> CuratedCodingModels = new()
    {
        // ★ TOP PICKS - Best coding models 2025
        new() { RepoId = "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 7B", Description = "Best coding model for 8GB VRAM. Excellent at code generation, debugging, and refactoring.", FileName = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_700_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF" },
        new() { RepoId = "bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 14B", Description = "High quality code generation. Strong reasoning about complex codebases.", FileName = "Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_900_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-14B-Instruct-GGUF" },
        new() { RepoId = "bartowski/Qwen2.5-Coder-32B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 32B", Description = "Top-tier local coding model. Rivals cloud APIs in code quality.", FileName = "Qwen2.5-Coder-32B-Instruct-Q4_K_M.gguf", ParameterBillions = 32, RequiredVramMB = 20480, ApproxSizeBytes = 19_000_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-32B-Instruct-GGUF" },

        // DeepSeek - Outstanding reasoning
        new() { RepoId = "bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF", DisplayName = "DeepSeek Coder V2 Lite", Description = "MoE architecture: only 2.4B active params but 16B total. Very efficient.", FileName = "DeepSeek-Coder-V2-Lite-Instruct-Q4_K_M.gguf", ParameterBillions = 16, RequiredVramMB = 10240, ApproxSizeBytes = 9_400_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF" },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", DisplayName = "DeepSeek R1 Distill 7B", Description = "Distilled reasoning model. Great for step-by-step problem solving.", FileName = "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_700_000_000, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF" },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", DisplayName = "DeepSeek R1 Distill 14B", Description = "Strong reasoning distilled from DeepSeek R1. Good for complex tasks.", FileName = "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_900_000_000, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF" },

        // Llama 3 family
        new() { RepoId = "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", DisplayName = "Llama 3.1 8B", Description = "Meta's workhorse model. Excellent general + coding ability.", FileName = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", ParameterBillions = 8, RequiredVramMB = 6144, ApproxSizeBytes = 4_900_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF" },

        // Compact models for low VRAM
        new() { RepoId = "bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 1.5B", Description = "Ultra-compact coder. Runs on 2GB VRAM or CPU-only.", FileName = "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf", ParameterBillions = 1, RequiredVramMB = 2048, ApproxSizeBytes = 1_100_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF" },
        new() { RepoId = "bartowski/Phi-3.5-mini-instruct-GGUF", DisplayName = "Phi 3.5 Mini 3.8B", Description = "Microsoft's compact model. Surprisingly smart for its size.", FileName = "Phi-3.5-mini-instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_400_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF" },
        new() { RepoId = "bartowski/Llama-3.2-3B-Instruct-GGUF", DisplayName = "Llama 3.2 3B", Description = "Meta's compact general model. Fast inference on CPU.", FileName = "Llama-3.2-3B-Instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_000_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF" },

        // StarCoder2
        new() { RepoId = "bartowski/starcoder2-7b-GGUF", DisplayName = "StarCoder2 7B", Description = "BigCode's code model. Trained on The Stack v2.", FileName = "starcoder2-7b-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_400_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/starcoder2-7b-GGUF" },
        new() { RepoId = "bartowski/starcoder2-15b-GGUF", DisplayName = "StarCoder2 15B", Description = "Larger StarCoder for better output quality.", FileName = "starcoder2-15b-Q4_K_M.gguf", ParameterBillions = 15, RequiredVramMB = 12288, ApproxSizeBytes = 9_100_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/starcoder2-15b-GGUF" },

        // CodeLlama
        new() { RepoId = "TheBloke/CodeLlama-7B-Instruct-GGUF", DisplayName = "CodeLlama 7B", Description = "Meta's code-specialized LLaMA. Good for code completion.", FileName = "codellama-7b-instruct.Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_100_000_000, Category = "Coding", Url = "https://huggingface.co/TheBloke/CodeLlama-7B-Instruct-GGUF" },
    };

    // ─── Model Sources ────────────────────────────────────────────────

    public static readonly List<ModelSource> PopularSources = new()
    {
        new() { Name = "HuggingFace", Url = "https://huggingface.co/models?pipeline_tag=text-generation&sort=trending&search=gguf", Description = "Largest model hub. Search for any GGUF model.", Icon = "\uE774" },
        new() { Name = "bartowski", Url = "https://huggingface.co/bartowski", Description = "High-quality GGUF quantizations of popular models. Updated frequently.", Icon = "\uE735" },
        new() { Name = "TheBloke", Url = "https://huggingface.co/TheBloke", Description = "Classic GGUF provider. Huge library of quantized models.", Icon = "\uE735" },
        new() { Name = "lmstudio-community", Url = "https://huggingface.co/lmstudio-community", Description = "Models tested for LM Studio compatibility. Works great with LLamaSharp too.", Icon = "\uE735" },
        new() { Name = "Ollama Library", Url = "https://ollama.com/library", Description = "Browse Ollama's model library. Use with the Ollama provider.", Icon = "\uE774" },
    };

    public HuggingFaceService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
    }

    public List<RecommendedModel> GetRecommendedModels(int? maxVramMB = null)
    {
        if (maxVramMB == null)
            return CuratedCodingModels;

        return CuratedCodingModels
            .Where(m => m.RequiredVramMB <= maxVramMB.Value)
            .OrderByDescending(m => m.ParameterBillions)
            .ToList();
    }

    public async Task<List<HuggingFaceModelResult>> SearchModelsAsync(string query, CancellationToken ct = default)
    {
        // Append "gguf" to search to prioritize GGUF repos
        string searchTerm = query.Contains("gguf", StringComparison.OrdinalIgnoreCase)
            ? query : query + " gguf";
        string url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(searchTerm)}&sort=downloads&limit=20";

        using var request = CreateAuthRequest(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);

        // Parse with flexible handling
        var results = new List<HuggingFaceModelResult>();
        using var doc = JsonDocument.Parse(json);
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            try
            {
                var model = new HuggingFaceModelResult
                {
                    ModelId = elem.TryGetProperty("id", out var id) ? id.GetString() ?? "" : elem.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "",
                    Author = elem.TryGetProperty("author", out var author) ? author.GetString() ?? "" : "",
                    Downloads = elem.TryGetProperty("downloads", out var dl) ? dl.GetInt32() : 0,
                    Likes = elem.TryGetProperty("likes", out var likes) ? likes.GetInt32() : 0,
                };

                if (elem.TryGetProperty("lastModified", out var lm))
                {
                    if (DateTime.TryParse(lm.GetString(), out var dt))
                        model.LastModified = dt;
                }

                if (elem.TryGetProperty("tags", out var tags))
                {
                    foreach (var t in tags.EnumerateArray())
                    {
                        var tag = t.GetString();
                        if (tag != null) model.Tags.Add(tag);
                    }
                }

                if (!string.IsNullOrEmpty(model.ModelId))
                    results.Add(model);
            }
            catch { /* skip malformed entries */ }
        }

        return results;
    }

    public async Task<List<HuggingFaceSibling>> GetModelFilesAsync(string repoId, CancellationToken ct = default)
    {
        string url = $"https://huggingface.co/api/models/{repoId}";

        using var request = CreateAuthRequest(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var siblings = new List<HuggingFaceSibling>();
        if (doc.RootElement.TryGetProperty("siblings", out var siblingsEl))
        {
            foreach (var sib in siblingsEl.EnumerateArray())
            {
                string filename = sib.GetProperty("rfilename").GetString() ?? "";
                if (filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    long? size = null;
                    if (sib.TryGetProperty("size", out var sizeEl))
                    {
                        try { size = sizeEl.GetInt64(); } catch { }
                    }

                    siblings.Add(new HuggingFaceSibling
                    {
                        RFilename = filename,
                        Size = size,
                    });
                }
            }
        }
        return siblings;
    }

    public async Task DownloadModelAsync(
        string repoId,
        string fileName,
        string targetPath,
        IProgress<(double progress, string status)> progress,
        CancellationToken ct = default)
    {
        string url = $"https://huggingface.co/{repoId}/resolve/main/{fileName}";

        using var dlRequest = CreateAuthRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(dlRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? 0;
        string dir = Path.GetDirectoryName(targetPath) ?? _settingsService.Settings.ModelDirectory;
        Directory.CreateDirectory(dir);

        string tempPath = targetPath + ".downloading";
        long downloadedBytes = 0;

        // Resume support
        if (File.Exists(tempPath))
        {
            downloadedBytes = new FileInfo(tempPath).Length;
        }

        if (downloadedBytes > 0 && totalBytes > 0)
        {
            // Try range request for resume
            var rangeRequest = CreateAuthRequest(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(downloadedBytes, null);
            using var rangeResponse = await _httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (rangeResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                await using var rangeStream = await rangeResponse.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920);
                await CopyWithProgress(rangeStream, fileStream, totalBytes, downloadedBytes, progress, ct);
            }
            else
            {
                // Server doesn't support range, restart
                downloadedBytes = 0;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                await CopyWithProgress(stream, fileStream, totalBytes, 0, progress, ct);
            }
        }
        else
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            await CopyWithProgress(stream, fileStream, totalBytes, 0, progress, ct);
        }

        // Rename temp file to final
        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(tempPath, targetPath);

        progress.Report((1.0, "Download complete!"));
    }

    private static async Task CopyWithProgress(
        Stream source,
        Stream destination,
        long totalBytes,
        long initialBytes,
        IProgress<(double progress, string status)> progress,
        CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long downloaded = initialBytes;
        int bytesRead;
        var lastReport = DateTime.UtcNow;

        while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                double pct = totalBytes > 0 ? (double)downloaded / totalBytes : 0;
                string status = totalBytes > 0
                    ? $"{downloaded / (1024.0 * 1024):F1} MB / {totalBytes / (1024.0 * 1024):F1} MB ({pct:P1})"
                    : $"{downloaded / (1024.0 * 1024):F1} MB downloaded";
                progress.Report((pct, status));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    public List<ModelInfo> GetLocalModels()
    {
        var settings = _settingsService.Settings;
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allDirs = new List<string> { settings.ModelDirectory };

        // Add any extra directories the user has configured
        if (settings.AdditionalModelDirectories != null)
            allDirs.AddRange(settings.AdditionalModelDirectories);

        // Scan each directory for GGUF files
        foreach (string dir in allDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (string path in Directory.GetFiles(dir, "*.gguf", SearchOption.AllDirectories))
                    allPaths.Add(path);
            }
            catch { /* skip inaccessible directories */ }
        }

        return allPaths
            .Select(path =>
            {
                var fi = new FileInfo(path);
                string name = Path.GetFileNameWithoutExtension(fi.Name);
                return new ModelInfo
                {
                    Id = fi.FullName,
                    Name = name,
                    FileName = fi.Name,
                    FileSize = fi.Length,
                    LocalPath = fi.FullName,
                    IsDownloaded = true,
                    QuantizationType = ExtractQuantization(fi.Name),
                };
            })
            .OrderByDescending(m => m.FileSize)
            .ToList();
    }

    public void DeleteModel(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string ExtractQuantization(string filename)
    {
        string upper = filename.ToUpperInvariant();
        string[] quantTypes = ["Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M", "Q3_K_S", "Q2_K", "IQ4_XS", "IQ3_XXS"];
        foreach (string q in quantTypes)
        {
            if (upper.Contains(q)) return q;
        }
        return "Unknown";
    }

    /// <summary>Create a request message with auth header (per-request, not on DefaultRequestHeaders).</summary>
    private HttpRequestMessage CreateAuthRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        string? token = _settingsService.Settings.HuggingFaceToken;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
