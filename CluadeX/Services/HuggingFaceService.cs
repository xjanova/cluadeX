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
        // Stars: 5=elite, 4=excellent, 3=good, 2=decent, 1=basic
        // Every RepoId / FileName / ApproxSizeBytes below was checked against the public HF API
        // (GET /api/models/<repo>?blobs=true) on 2026-10-09; tests/managertest --network re-checks them.
        // Removed then: gemma-4-9B / gemma-4-14B / codegemma-4-9B / bartowski starcoder2-7b + 15b (the repos
        // do not exist — 10 of 30 Download buttons failed), and the Gemma 4 26B file is the "UD-" quant.
        new() { RepoId = "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 7B", Description = "Best coding model for 8GB VRAM. Excellent at code generation, debugging, and refactoring.", FileName = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_683_074_336, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 14B", Description = "High quality code generation. Strong reasoning about complex codebases.", FileName = "Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_988_111_072, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", Stars = 5 },
        new() { RepoId = "bartowski/Qwen2.5-Coder-32B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 32B", Description = "Top-tier local coding model. Rivals cloud APIs in code quality.", FileName = "Qwen2.5-Coder-32B-Instruct-Q4_K_M.gguf", ParameterBillions = 32, RequiredVramMB = 20480, ApproxSizeBytes = 19_851_336_672, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-32B-Instruct-GGUF", Stars = 5 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 4 — Google's newest open model (April 2026) — 256K ctx, vision, video, reasoning.
        // All routed to llama-server (gemma4 arch post-dates the LLamaSharp snapshot).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "unsloth/gemma-4-E2B-it-GGUF", DisplayName = "Gemma 4 E2B", Description = "Google's ultra-compact edge model (2.3B effective). Runs on mobile/edge. Supports audio + vision.", FileName = "gemma-4-E2B-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 4096, ApproxSizeBytes = 3_106_738_272, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF", Stars = 2 },
        new() { RepoId = "unsloth/gemma-4-E4B-it-GGUF", DisplayName = "Gemma 4 E4B", Description = "Google's efficient edge model (4.5B effective). Vision + audio. Great for laptops.", FileName = "gemma-4-E4B-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 6144, ApproxSizeBytes = 4_977_171_584, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF", Stars = 3 },
        new() { RepoId = "unsloth/gemma-4-26B-A4B-it-GGUF", DisplayName = "Gemma 4 26B MoE", Description = "MoE architecture: only 3.8B active params from 25.2B total. 256K context. Fast inference!", FileName = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", ParameterBillions = 26, RequiredVramMB = 18432, ApproxSizeBytes = 16_947_541_728, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF", Stars = 4 },
        new() { RepoId = "unsloth/gemma-4-31B-it-GGUF", DisplayName = "Gemma 4 31B", Description = "Google's BEST open model! 256K context, vision, video, reasoning, 140+ languages. AIME 2026: 89.2%!", FileName = "gemma-4-31B-it-Q4_K_M.gguf", ParameterBillions = 31, RequiredVramMB = 20480, ApproxSizeBytes = 18_323_733_440, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-31B-it-GGUF", Stars = 5 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 3N — mobile-class multimodal. Uses the gemma3n arch (llama-server path).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "unsloth/gemma-3n-E2B-it-GGUF", DisplayName = "Gemma 3N E2B", Description = "Mobile-optimized 2B effective params. Per-Layer Embeddings + MatFormer for phone-grade hardware.", FileName = "gemma-3n-E2B-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 4096, ApproxSizeBytes = 3_026_881_888, Category = "General", Url = "https://huggingface.co/unsloth/gemma-3n-E2B-it-GGUF", Stars = 2 },
        new() { RepoId = "unsloth/gemma-3n-E4B-it-GGUF", DisplayName = "Gemma 3N E4B", Description = "Mobile-optimized 4B effective. Audio/vision input. Runs well on laptops and edge devices.", FileName = "gemma-3n-E4B-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 6144, ApproxSizeBytes = 4_539_054_208, Category = "General", Url = "https://huggingface.co/unsloth/gemma-3n-E4B-it-GGUF", Stars = 3 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 3 — Google's previous generation, still excellent (March 2025).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "bartowski/google_gemma-3-1b-it-GGUF", DisplayName = "Gemma 3 1B", Description = "Google's ultra-compact model. Runs on any hardware, even CPU-only. Great for quick tasks.", FileName = "google_gemma-3-1b-it-Q4_K_M.gguf", ParameterBillions = 1, RequiredVramMB = 1536, ApproxSizeBytes = 806_058_496, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-1b-it-GGUF", Stars = 1 },
        new() { RepoId = "bartowski/google_gemma-3-4b-it-GGUF", DisplayName = "Gemma 3 4B", Description = "Google's compact powerhouse. Excellent quality-to-size ratio with vision support.", FileName = "google_gemma-3-4b-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 2_489_758_112, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/google_gemma-3-12b-it-GGUF", DisplayName = "Gemma 3 12B", Description = "Google's mid-size model. Strong reasoning and coding, multimodal. Fits 12GB VRAM.", FileName = "google_gemma-3-12b-it-Q4_K_M.gguf", ParameterBillions = 12, RequiredVramMB = 9216, ApproxSizeBytes = 7_300_575_264, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-12b-it-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/google_gemma-3-27b-it-GGUF", DisplayName = "Gemma 3 27B", Description = "Google's previous flagship. 140+ languages, vision, function calling.", FileName = "google_gemma-3-27b-it-Q4_K_M.gguf", ParameterBillions = 27, RequiredVramMB = 18432, ApproxSizeBytes = 16_546_404_992, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-27b-it-GGUF", Stars = 4 },

        // Gemma 3 QAT (quantization-aware trained) — near-fp16 quality at Q4 size.
        // Officially published by Google. GATED: the download needs a Hugging Face token from an account that
        // accepted Google's license on the model page (see GatedRepos) — without one it is 401/403.
        new() { RepoId = "google/gemma-3-1b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 1B (QAT)", Description = "Official Google QAT build. Q4_0 with much smaller quality loss than PTQ. Best tiny model. Needs a free Hugging Face token (accept Google's license on the model page first).", FileName = "gemma-3-1b-it-q4_0.gguf", ParameterBillions = 1, RequiredVramMB = 1536, ApproxSizeBytes = 1_003_541_152, Category = "General", Url = "https://huggingface.co/google/gemma-3-1b-it-qat-q4_0-gguf", Stars = 2 },
        new() { RepoId = "google/gemma-3-4b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 4B (QAT)", Description = "Official Google QAT build. Best-in-class 4B with vision support. Needs a free Hugging Face token (accept Google's license on the model page first).", FileName = "gemma-3-4b-it-q4_0.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 3_155_051_328, Category = "General", Url = "https://huggingface.co/google/gemma-3-4b-it-qat-q4_0-gguf", Stars = 4 },
        new() { RepoId = "google/gemma-3-12b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 12B (QAT)", Description = "Official Google QAT. Near-fp16 quality at Q4 size. Outstanding for 12GB cards. Needs a free Hugging Face token (accept Google's license on the model page first).", FileName = "gemma-3-12b-it-q4_0.gguf", ParameterBillions = 12, RequiredVramMB = 9216, ApproxSizeBytes = 8_074_473_920, Category = "General", Url = "https://huggingface.co/google/gemma-3-12b-it-qat-q4_0-gguf", Stars = 5 },
        new() { RepoId = "google/gemma-3-27b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 27B (QAT)", Description = "Official Google QAT. Flagship-class quality at Q4 footprint. 24GB VRAM recommended. Needs a free Hugging Face token (accept Google's license on the model page first).", FileName = "gemma-3-27b-it-q4_0.gguf", ParameterBillions = 27, RequiredVramMB = 18432, ApproxSizeBytes = 17_229_630_496, Category = "General", Url = "https://huggingface.co/google/gemma-3-27b-it-qat-q4_0-gguf", Stars = 5 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 2 — legacy but still popular. Supported natively by LLamaSharp (in-proc).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "bartowski/gemma-2-2b-it-GGUF", DisplayName = "Gemma 2 2B", Description = "Compact general model. Runs well even on CPU. Still a great daily driver.", FileName = "gemma-2-2b-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 2048, ApproxSizeBytes = 1_708_582_752, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-2b-it-GGUF", Stars = 2 },
        new() { RepoId = "bartowski/gemma-2-9b-it-GGUF", DisplayName = "Gemma 2 9B", Description = "Classic 9B workhorse. Strong instruction following. Runs great on 8GB VRAM.", FileName = "gemma-2-9b-it-Q4_K_M.gguf", ParameterBillions = 9, RequiredVramMB = 7168, ApproxSizeBytes = 5_761_057_728, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-9b-it-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/gemma-2-27b-it-GGUF", DisplayName = "Gemma 2 27B", Description = "Older flagship, still strong. Good option if you want broader multilingual coverage.", FileName = "gemma-2-27b-it-Q4_K_M.gguf", ParameterBillions = 27, RequiredVramMB = 17408, ApproxSizeBytes = 16_645_381_632, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-27b-it-GGUF", Stars = 3 },

        // CodeGemma (Gemma 2-era) — coding specialist for in-proc LLamaSharp users
        new() { RepoId = "bartowski/codegemma-7b-it-GGUF", DisplayName = "CodeGemma 7B", Description = "Google's coding-specialized Gemma 2. Fill-in-the-middle + function completion.", FileName = "codegemma-7b-it-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 5_329_759_232, Category = "Coding", Url = "https://huggingface.co/bartowski/codegemma-7b-it-GGUF", Stars = 3 },

        // DeepSeek - Outstanding reasoning
        new() { RepoId = "bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF", DisplayName = "DeepSeek Coder V2 Lite", Description = "MoE architecture: only 2.4B active params but 16B total. Very efficient.", FileName = "DeepSeek-Coder-V2-Lite-Instruct-Q4_K_M.gguf", ParameterBillions = 16, RequiredVramMB = 11264, ApproxSizeBytes = 10_364_416_768, Category = "Coding", Url = "https://huggingface.co/bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", DisplayName = "DeepSeek R1 Distill 7B", Description = "Distilled reasoning model. Great for step-by-step problem solving.", FileName = "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_683_073_504, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", DisplayName = "DeepSeek R1 Distill 14B", Description = "Strong reasoning distilled from DeepSeek R1. Good for complex tasks.", FileName = "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_988_110_240, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", Stars = 5 },

        // Llama 3 family
        new() { RepoId = "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", DisplayName = "Llama 3.1 8B", Description = "Meta's workhorse model. Excellent general + coding ability.", FileName = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", ParameterBillions = 8, RequiredVramMB = 6144, ApproxSizeBytes = 4_920_739_232, Category = "General", Url = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", Stars = 4 },

        // Compact models for low VRAM
        new() { RepoId = "bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 1.5B", Description = "Ultra-compact coder. Runs on 2GB VRAM or CPU-only.", FileName = "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf", ParameterBillions = 1, RequiredVramMB = 2048, ApproxSizeBytes = 986_048_800, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", Stars = 2 },
        new() { RepoId = "bartowski/Phi-3.5-mini-instruct-GGUF", DisplayName = "Phi 3.5 Mini 3.8B", Description = "Microsoft's compact model. Surprisingly smart for its size.", FileName = "Phi-3.5-mini-instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_393_232_672, Category = "General", Url = "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/Llama-3.2-3B-Instruct-GGUF", DisplayName = "Llama 3.2 3B", Description = "Meta's compact general model. Fast inference on CPU.", FileName = "Llama-3.2-3B-Instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_019_377_696, Category = "General", Url = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF", Stars = 3 },

        // StarCoder2 — the instruct build (the base 7B/15B are completion-only, and their bartowski repos are gone)
        new() { RepoId = "bartowski/starcoder2-15b-instruct-v0.1-GGUF", DisplayName = "StarCoder2 15B Instruct", Description = "BigCode's code model, instruction-tuned. Trained on The Stack v2.", FileName = "starcoder2-15b-instruct-v0.1-Q4_K_M.gguf", ParameterBillions = 15, RequiredVramMB = 12288, ApproxSizeBytes = 9_860_188_864, Category = "Coding", Url = "https://huggingface.co/bartowski/starcoder2-15b-instruct-v0.1-GGUF", Stars = 3 },

        // CodeLlama
        new() { RepoId = "TheBloke/CodeLlama-7B-Instruct-GGUF", DisplayName = "CodeLlama 7B", Description = "Meta's code-specialized LLaMA. Good for code completion.", FileName = "codellama-7b-instruct.Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_081_095_360, Category = "Coding", Url = "https://huggingface.co/TheBloke/CodeLlama-7B-Instruct-GGUF", Stars = 3 },
    };

    /// <summary>
    /// Repos that answer 401/403 to a download without a token of an account that accepted the license
    /// (HF reports them as "gated"). The UI explains that instead of a bare "401 Unauthorized".
    /// </summary>
    private static readonly HashSet<string> GatedRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        "google/gemma-3-1b-it-qat-q4_0-gguf",
        "google/gemma-3-4b-it-qat-q4_0-gguf",
        "google/gemma-3-12b-it-qat-q4_0-gguf",
        "google/gemma-3-27b-it-qat-q4_0-gguf",
    };

    public static bool IsGatedRepo(string repoId) => GatedRepos.Contains(repoId);

    /// <summary>The full curated catalog (for the catalog harness).</summary>
    public static IReadOnlyList<RecommendedModel> Catalog => CuratedCodingModels;

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

    // Pipeline tags for non-LLM tasks that won't work with llama.cpp text inference.
    // We reject any repo whose `pipeline_tag` or `tags` collection intersects this set.
    private static readonly HashSet<string> IncompatiblePipelineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "feature-extraction", "sentence-similarity", "fill-mask", "token-classification",
        "text-classification", "zero-shot-classification", "translation", "summarization",
        "question-answering", "table-question-answering",
        "image-classification", "image-segmentation", "image-to-text", "image-to-image",
        "object-detection", "depth-estimation", "unconditional-image-generation",
        "text-to-image", "text-to-video", "text-to-3d", "video-classification",
        "automatic-speech-recognition", "text-to-speech", "audio-classification",
        "audio-to-audio", "voice-activity-detection",
        "reinforcement-learning", "robotics", "graph-ml", "time-series-forecasting",
        "tabular-classification", "tabular-regression",
    };

    /// <summary>
    /// Search HuggingFace for models COMPATIBLE with this app. Compatible means:
    ///   1. Published in GGUF format (CluadeX runs GGUFs via LLamaSharp / llama-server)
    ///   2. A text-generation model (LLM), not an audio / image / embedding model
    ///
    /// Strategy: ask HF to pre-filter with <c>?filter=gguf&amp;pipeline_tag=text-generation</c>,
    /// then do a second pass client-side to reject anything the server let through that
    /// still doesn't look like a runnable LLM repo.
    /// </summary>
    public async Task<List<HuggingFaceModelResult>> SearchModelsAsync(string query, CancellationToken ct = default)
    {
        // If the user typed "gguf" we let the search-term carry it; otherwise rely on the
        // filter param so we don't narrow the text search for no reason.
        string searchTerm = query.Trim();
        string url = "https://huggingface.co/api/models"
                   + $"?search={Uri.EscapeDataString(searchTerm)}"
                   + "&filter=gguf"
                   + "&pipeline_tag=text-generation"
                   + "&sort=downloads"
                   + "&limit=40"; // over-fetch so client-side filtering still gives a full page

        using var request = CreateAuthRequest(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);

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

                string? pipelineTag = elem.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() : null;

                if (elem.TryGetProperty("tags", out var tags))
                {
                    foreach (var t in tags.EnumerateArray())
                    {
                        var tag = t.GetString();
                        if (tag != null) model.Tags.Add(tag);
                    }
                }

                if (!IsCompatibleRepo(model, pipelineTag)) continue;
                results.Add(model);
                if (results.Count >= 20) break; // display cap
            }
            catch { /* skip malformed entries */ }
        }

        return results;
    }

    /// <summary>Final gate: reject entries that can't be used in this app even after HF filtering.</summary>
    private static bool IsCompatibleRepo(HuggingFaceModelResult model, string? pipelineTag)
    {
        if (string.IsNullOrEmpty(model.ModelId)) return false;

        // Must be a GGUF repo — the tag OR the repo name needs to signal it. HF's filter=gguf
        // is strict but the field can be stale; belt-and-braces check on the name too.
        bool looksLikeGguf = model.Tags.Any(t => t.Equals("gguf", StringComparison.OrdinalIgnoreCase))
                           || model.ModelId.Contains("gguf", StringComparison.OrdinalIgnoreCase)
                           || model.ModelId.Contains("GGUF", StringComparison.Ordinal);
        if (!looksLikeGguf) return false;

        // Reject anything self-labelled as a non-LLM task.
        if (!string.IsNullOrEmpty(pipelineTag) && IncompatiblePipelineTags.Contains(pipelineTag))
            return false;
        if (model.Tags.Any(t => IncompatiblePipelineTags.Contains(t))) return false;

        // LoRA / adapter repos can't be run standalone by llama.cpp — filter them out.
        if (model.Tags.Any(t => t.Equals("lora", StringComparison.OrdinalIgnoreCase)
                             || t.Equals("adapter", StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    /// <summary>
    /// GGUF files of a repo, best choice first. The old version listed files alphabetically and the search page
    /// showed only the first five — for a typical bartowski repo those are IQ2/IQ3 quants, so Q4_K_M was never
    /// offered — and without <c>?blobs=true</c> the API returns no sizes at all. Split models
    /// (<c>name-00001-of-00003.gguf</c>) come back as ONE entry: the first shard, with the total size; downloading
    /// it fetches every shard. Vision projector files (mmproj) are skipped — they are not models.
    /// </summary>
    public async Task<List<HuggingFaceSibling>> GetModelFilesAsync(string repoId, CancellationToken ct = default)
    {
        using var doc = await GetRepoInfoAsync(repoId, ct);
        var files = new List<(string Name, long? Size)>();
        if (doc.RootElement.TryGetProperty("siblings", out var siblingsEl))
        {
            foreach (var sib in siblingsEl.EnumerateArray())
            {
                string filename = sib.TryGetProperty("rfilename", out var fn) ? fn.GetString() ?? "" : "";
                if (!filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add((filename, ReadSize(sib)));
            }
        }
        return GroupAndRankGgufFiles(files);
    }

    private static long? ReadSize(JsonElement sib)
    {
        if (sib.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var v)) return v;
        if (sib.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("size", out var ls)
            && ls.ValueKind == JsonValueKind.Number && ls.TryGetInt64(out var lv)) return lv;
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex ShardPattern =
        new(@"^(?<base>.+)-(?<idx>\d{5})-of-(?<count>\d{5})\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Quants in the order a user should see them: the usual sweet spot first, full precision last.</summary>
    private static readonly string[] QuantPreference =
        ["Q4_K_M", "Q5_K_M", "Q4_K_S", "Q6_K", "Q8_0", "Q5_K_S", "IQ4_XS", "IQ4_NL", "Q4_0", "Q3_K_L", "Q3_K_M",
         "Q5_0", "Q4_1", "Q3_K_S", "IQ3_M", "IQ3_XS", "IQ3_XXS", "Q2_K_L", "Q2_K", "IQ2_M", "IQ2_S", "IQ2_XS", "IQ2_XXS",
         "IQ1_M", "IQ1_S", "BF16", "F16", "F32"];

    /// <summary>Rank of a file name's quant in <see cref="QuantPreference"/> (unknown quants sort after known ones).</summary>
    public static int QuantRank(string fileName)
    {
        string upper = fileName.ToUpperInvariant();
        for (int i = 0; i < QuantPreference.Length; i++)
        {
            // Match the quant as a token ("…-Q4_K_M.gguf", "….Q4_K_M.gguf", "…-UD-Q4_K_M-…"), not as a substring
            // of a longer one (Q4_K inside Q4_K_M, Q2_K inside Q2_K_L).
            var m = System.Text.RegularExpressions.Regex.Match(upper, $@"(^|[-_.]){System.Text.RegularExpressions.Regex.Escape(QuantPreference[i])}($|[-_.])");
            if (m.Success) return i;
        }
        return QuantPreference.Length;
    }

    /// <summary>Group split shards into one entry (first shard, total size), drop mmproj, best quant first.</summary>
    public static List<HuggingFaceSibling> GroupAndRankGgufFiles(IEnumerable<(string Name, long? Size)> files)
    {
        var result = new List<HuggingFaceSibling>();
        foreach (var group in files
                     .Where(f => !Path.GetFileName(f.Name).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => ShardPattern.Match(f.Name) is { Success: true } m ? m.Groups["base"].Value : f.Name,
                              StringComparer.OrdinalIgnoreCase))
        {
            var shards = group.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var first = shards[0];
            bool sharded = ShardPattern.IsMatch(first.Name);
            if (sharded)
            {
                // Only offer a split model when every shard is listed — a partial set cannot load.
                int count = int.Parse(ShardPattern.Match(first.Name).Groups["count"].Value);
                if (shards.Count != count || !first.Name.Contains("-00001-of-", StringComparison.OrdinalIgnoreCase)) continue;
            }
            long? total = shards.All(s => s.Size.HasValue) ? shards.Sum(s => s.Size!.Value) : null;
            result.Add(new HuggingFaceSibling { RFilename = first.Name, Size = total });
        }
        return result
            .OrderBy(s => QuantRank(s.RFilename))
            .ThenBy(s => s.Size ?? long.MaxValue)
            .ThenBy(s => s.RFilename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>All shard file names for a (possibly) split model, given its first file.</summary>
    public static List<string> ShardFileNames(string fileName)
    {
        var m = ShardPattern.Match(fileName);
        if (!m.Success) return [fileName];
        int count = int.Parse(m.Groups["count"].Value);
        string baseName = m.Groups["base"].Value; // keeps any repo sub-folder prefix
        return Enumerable.Range(1, count).Select(i => $"{baseName}-{i:D5}-of-{count:D5}.gguf").ToList();
    }

    private async Task<JsonDocument> GetRepoInfoAsync(string repoId, CancellationToken ct)
    {
        string url = $"https://huggingface.co/api/models/{repoId}?blobs=true";
        using var request = CreateAuthRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, ct);
        ThrowForHfStatus(response, repoId);
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    private static void ThrowForHfStatus(HttpResponseMessage response, string repoId)
    {
        // HF answers 401 both for gated repos without access and for repos that do not exist (so it does not
        // leak private names) — the UI explains both cases instead of showing "401 (Unauthorized)".
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new HfAccessDeniedException(repoId, (int)response.StatusCode);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new HfNotFoundException(repoId);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>What the API says about one file: its size, LFS SHA-256 and the commit it belongs to.</summary>
    public sealed record HfFileMeta(string Revision, long? Size, string? Sha256);

    public async Task<HfFileMeta> GetFileMetaAsync(string repoId, string fileName, CancellationToken ct = default)
    {
        using var doc = await GetRepoInfoAsync(repoId, ct);
        string revision = doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() ?? "main" : "main";
        if (doc.RootElement.TryGetProperty("siblings", out var sibs))
        {
            foreach (var sib in sibs.EnumerateArray())
            {
                if (!sib.TryGetProperty("rfilename", out var fn) || !string.Equals(fn.GetString(), fileName, StringComparison.Ordinal)) continue;
                string? sha256 = sib.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("sha256", out var h) ? h.GetString() : null;
                return new HfFileMeta(revision, ReadSize(sib), sha256);
            }
        }
        throw new HfNotFoundException($"{repoId}/{fileName}");
    }

    /// <summary>Progress of a model download: bytes of the current file, which shard, and the phase.</summary>
    public readonly record struct DownloadProgressInfo(long Bytes, long Total, int Shard, int ShardCount, bool Verifying)
    {
        public double Fraction => Total > 0 ? Math.Clamp((double)Bytes / Total, 0, 1) : 0;
    }

    /// <summary>Extra free space kept on the drive beyond the download itself.</summary>
    private const long DiskHeadroomBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Download a model file (all of its shards when it is split) into <paramref name="targetPath"/>'s folder.
    ///
    /// Robust against what used to go wrong: the partial file (<c>.downloading</c>) is resumed only when its
    /// sidecar says it is the same file at the same commit — resuming used to append bytes of whatever
    /// revision "main" pointed to now; the download URL is pinned to that commit; free disk space is checked
    /// before starting; and the finished file must match the API's size and LFS SHA-256 before it is renamed
    /// to <c>.gguf</c> — a truncated or corrupted download used to become a "model" that failed to load.
    /// A cancelled download keeps its partial file for resuming; <see cref="DeletePartialDownload"/> removes it.
    /// </summary>
    /// <exception cref="HfAccessDeniedException">401/403 — gated repo without access, or the repo does not exist.</exception>
    /// <exception cref="HfNotFoundException">404 — the file is not in the repo.</exception>
    /// <exception cref="InsufficientDiskSpaceException">not enough free space on the target drive.</exception>
    /// <exception cref="DownloadVerificationException">size or SHA-256 mismatch (the bad file is deleted).</exception>
    public async Task DownloadModelAsync(
        string repoId,
        string fileName,
        string targetPath,
        IProgress<DownloadProgressInfo> progress,
        CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(targetPath) ?? _settingsService.Settings.ModelDirectory;
        Directory.CreateDirectory(dir);

        var shards = ShardFileNames(fileName);
        for (int i = 0; i < shards.Count; i++)
        {
            string shardTarget = Path.Combine(dir, Path.GetFileName(shards[i]));
            if (File.Exists(shardTarget) && i > 0) continue; // an earlier run already finished this shard
            await DownloadOneAsync(repoId, shards[i], shardTarget, i + 1, shards.Count, progress, ct);
        }
    }

    private sealed class ResumeInfo
    {
        public string RepoId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Revision { get; set; } = "";
        public long? Size { get; set; }
        public string? Sha256 { get; set; }
    }

    public static string PartialPathFor(string targetPath) => targetPath + ".downloading";

    /// <summary>Delete a kept partial download (and its resume sidecar). Returns true when something was removed.</summary>
    public static bool DeletePartialDownload(string targetPath)
    {
        bool any = false;
        foreach (var shard in ShardFileNames(Path.GetFileName(targetPath)))
        {
            string p = PartialPathFor(Path.Combine(Path.GetDirectoryName(targetPath) ?? "", shard));
            foreach (var f in new[] { p, p + ".json" })
                if (File.Exists(f)) { File.Delete(f); any = true; }
        }
        return any;
    }

    private async Task DownloadOneAsync(string repoId, string fileName, string targetPath, int shard, int shardCount,
        IProgress<DownloadProgressInfo> progress, CancellationToken ct)
    {
        string tempPath = PartialPathFor(targetPath);
        string metaPath = tempPath + ".json";

        // Which revision / size / hash are we downloading? A partial file is only resumed when its sidecar
        // describes this exact file; otherwise it is stale (other repo, other quant, older format) → start over.
        ResumeInfo? resume = null;
        try
        {
            if (File.Exists(tempPath) && File.Exists(metaPath))
            {
                var saved = JsonSerializer.Deserialize<ResumeInfo>(await File.ReadAllTextAsync(metaPath, ct));
                if (saved != null && saved.RepoId == repoId && saved.FileName == fileName && saved.Revision.Length > 0)
                    resume = saved;
            }
        }
        catch (JsonException) { resume = null; }

        if (resume == null)
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            HfFileMeta meta;
            try { meta = await GetFileMetaAsync(repoId, fileName, ct); }
            catch (HttpRequestException) { meta = new HfFileMeta("main", null, null); } // API hiccup: download unverified
            resume = new ResumeInfo { RepoId = repoId, FileName = fileName, Revision = meta.Revision, Size = meta.Size, Sha256 = meta.Sha256 };
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(resume), ct);
        }

        long have = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        long? expected = resume.Size;
        if (expected.HasValue && have > expected.Value) { File.Delete(tempPath); have = 0; }

        EnsureFreeSpace(targetPath, (expected ?? 0) - have);

        if (!(expected.HasValue && have == expected.Value))
        {
            string url = $"https://huggingface.co/{repoId}/resolve/{Uri.EscapeDataString(resume.Revision)}/"
                       + string.Join('/', fileName.Split('/').Select(Uri.EscapeDataString));
            using var request = CreateAuthRequest(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try { ThrowForHfStatus(response, repoId); }
            catch (Exception ex) when ((ex is HfAccessDeniedException or HfNotFoundException) && have == 0)
            {
                try { File.Delete(metaPath); } catch { }   // nothing downloaded — leave no sidecar behind
                throw;
            }

            bool append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!append) have = 0; // the server ignored the range: start from byte 0
            long total = expected ?? (have + (response.Content.Headers.ContentLength ?? 0));
            if (!expected.HasValue && response.Content.Headers.ContentLength is long len) EnsureFreeSpace(targetPath, len);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (var file = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                byte[] buffer = new byte[1 << 20];
                long done = have;
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                    {
                        progress.Report(new DownloadProgressInfo(done, total, shard, shardCount, false));
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
        }

        // ── Verify before the file is allowed to look like a model ──
        long final = new FileInfo(tempPath).Length;
        if (expected.HasValue && final != expected.Value)
        {
            // Short = the connection ended early: keep it, the next attempt resumes. Long = corrupt: delete.
            if (final > expected.Value) { File.Delete(tempPath); File.Delete(metaPath); }
            throw new DownloadVerificationException(fileName, final < expected.Value ? "incomplete" : "size", expected.Value, final);
        }
        if (!string.IsNullOrEmpty(resume.Sha256))
        {
            progress.Report(new DownloadProgressInfo(0, final, shard, shardCount, true));
            string actual = await Sha256OfFileAsync(tempPath, final, shard, shardCount, progress, ct);
            if (!string.Equals(actual, resume.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                File.Delete(metaPath);
                throw new DownloadVerificationException(fileName, "sha256", final, final);
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
        try { File.Delete(metaPath); } catch { /* a stale sidecar is ignored next time */ }
        progress.Report(new DownloadProgressInfo(final, final, shard, shardCount, false));
    }

    private static async Task<string> Sha256OfFileAsync(string path, long total, int shard, int shardCount,
        IProgress<DownloadProgressInfo> progress, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] buffer = new byte[1 << 20];
        long done = 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                progress.Report(new DownloadProgressInfo(done, total, shard, shardCount, true));
                lastReport = DateTime.UtcNow;
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Throw when the target drive cannot hold <paramref name="bytesNeeded"/> plus headroom.</summary>
    private static void EnsureFreeSpace(string targetPath, long bytesNeeded)
    {
        if (bytesNeeded <= 0) return;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return; // UNC share: DriveInfo can't tell
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < bytesNeeded + DiskHeadroomBytes)
                throw new InsufficientDiskSpaceException(root, bytesNeeded + DiskHeadroomBytes, drive.AvailableFreeSpace);
        }
        catch (InsufficientDiskSpaceException) { throw; }
        catch { /* unknown drive type — let the write itself fail if it must */ }
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
            // A split model loads from its first shard; the other shards and vision projectors (mmproj) are not
            // models of their own and only failed when "loaded".
            .Where(path => !Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                           && !(ShardPattern.Match(Path.GetFileName(path)) is { Success: true } m
                                && m.Groups["idx"].Value != "00001"))
            .Select(path =>
            {
                var fi = new FileInfo(path);
                string name = Path.GetFileNameWithoutExtension(fi.Name);
                long size = ShardFileNames(fi.Name)
                    .Select(s => new FileInfo(Path.Combine(fi.DirectoryName ?? "", s)))
                    .Where(s => s.Exists).Sum(s => s.Length);
                return new ModelInfo
                {
                    Id = fi.FullName,
                    Name = name,
                    FileName = fi.Name,
                    FileSize = size,
                    LocalPath = fi.FullName,
                    IsDownloaded = true,
                    QuantizationType = ExtractQuantization(fi.Name),
                };
            })
            .OrderByDescending(m => m.FileSize)
            .ToList();
    }

    /// <summary>Delete a model file (every shard of a split model). Throws IOException when a file is in use.</summary>
    public void DeleteModel(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        foreach (var shard in ShardFileNames(Path.GetFileName(path)))
        {
            string p = Path.Combine(dir, shard);
            if (File.Exists(p)) File.Delete(p);
        }
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

/// <summary>401/403 from Hugging Face: a gated repo the token can't access (or none set), or a repo that doesn't exist.</summary>
public sealed class HfAccessDeniedException : Exception
{
    public string RepoId { get; }
    public int StatusCode { get; }
    public bool IsGated => HuggingFaceService.IsGatedRepo(RepoId);
    public HfAccessDeniedException(string repoId, int status)
        : base($"Hugging Face refused access to {repoId} ({status}) — gated repo without an accepted license/token, or the repo does not exist.")
    { RepoId = repoId; StatusCode = status; }
}

/// <summary>404 from Hugging Face: the repo or file is not there.</summary>
public sealed class HfNotFoundException : Exception
{
    public HfNotFoundException(string what) : base($"Not found on Hugging Face: {what}") { }
}

/// <summary>The target drive does not have room for the download (checked before starting).</summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    public string Drive { get; }
    public long BytesNeeded { get; }
    public long BytesAvailable { get; }
    public InsufficientDiskSpaceException(string drive, long needed, long available)
        : base($"Not enough free space on {drive}: need {needed:N0} bytes, {available:N0} available.")
    { Drive = drive; BytesNeeded = needed; BytesAvailable = available; }
}

/// <summary>The downloaded file does not match the size / SHA-256 Hugging Face publishes for it.</summary>
public sealed class DownloadVerificationException : IOException
{
    /// <summary>"incomplete" (shorter — partial kept for resume), "size" (longer) or "sha256" (bad content; deleted).</summary>
    public string Kind { get; }
    public DownloadVerificationException(string file, string kind, long expected, long actual)
        : base($"{file}: verification failed ({kind}; expected {expected:N0} bytes, got {actual:N0}).")
        => Kind = kind;
}
