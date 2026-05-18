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
        new() { RepoId = "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 7B", Description = "Best coding model for 8GB VRAM. Excellent at code generation, debugging, and refactoring.", FileName = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_700_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 14B", Description = "High quality code generation. Strong reasoning about complex codebases.", FileName = "Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_900_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", Stars = 5 },
        new() { RepoId = "bartowski/Qwen2.5-Coder-32B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 32B", Description = "Top-tier local coding model. Rivals cloud APIs in code quality.", FileName = "Qwen2.5-Coder-32B-Instruct-Q4_K_M.gguf", ParameterBillions = 32, RequiredVramMB = 20480, ApproxSizeBytes = 19_000_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-32B-Instruct-GGUF", Stars = 5 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 4 — Google's newest open model (April 2026) — 256K ctx, vision, video, reasoning.
        // All routed to llama-server (gemma4 arch post-dates the LLamaSharp snapshot).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "unsloth/gemma-4-E2B-it-GGUF", DisplayName = "Gemma 4 E2B", Description = "Google's ultra-compact edge model (2.3B effective). Runs on mobile/edge. Supports audio + vision.", FileName = "gemma-4-E2B-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 2048, ApproxSizeBytes = 1_600_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF", Stars = 2 },
        new() { RepoId = "unsloth/gemma-4-E4B-it-GGUF", DisplayName = "Gemma 4 E4B", Description = "Google's efficient edge model (4.5B effective). Vision + audio. Great for laptops.", FileName = "gemma-4-E4B-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 3_200_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF", Stars = 3 },
        new() { RepoId = "unsloth/gemma-4-9B-it-GGUF", DisplayName = "Gemma 4 9B", Description = "Balanced 9B flagship. Vision + reasoning. Great sweet-spot for 8-12GB VRAM cards.", FileName = "gemma-4-9B-it-Q4_K_M.gguf", ParameterBillions = 9, RequiredVramMB = 7168, ApproxSizeBytes = 5_600_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-9B-it-GGUF", Stars = 4 },
        new() { RepoId = "unsloth/gemma-4-14B-it-GGUF", DisplayName = "Gemma 4 14B", Description = "Mid-weight Gemma 4 with strong coding + reasoning. Fits 12GB VRAM at Q4.", FileName = "gemma-4-14B-it-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_800_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-14B-it-GGUF", Stars = 4 },
        new() { RepoId = "unsloth/gemma-4-26B-A4B-it-GGUF", DisplayName = "Gemma 4 26B MoE", Description = "MoE architecture: only 3.8B active params from 25.2B total. 256K context. Fast inference!", FileName = "gemma-4-26B-A4B-it-Q4_K_M.gguf", ParameterBillions = 26, RequiredVramMB = 16384, ApproxSizeBytes = 15_600_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF", Stars = 4 },
        new() { RepoId = "unsloth/gemma-4-31B-it-GGUF", DisplayName = "Gemma 4 31B", Description = "Google's BEST open model! 256K context, vision, video, reasoning, 140+ languages. AIME 2026: 89.2%!", FileName = "gemma-4-31B-it-Q4_K_M.gguf", ParameterBillions = 31, RequiredVramMB = 20480, ApproxSizeBytes = 19_500_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-4-31B-it-GGUF", Stars = 5 },

        // Gemma 4 — Coding-specialized variant
        new() { RepoId = "unsloth/codegemma-4-9B-it-GGUF", DisplayName = "CodeGemma 4 9B", Description = "Coding-tuned Gemma 4. Excellent fill-in-the-middle (FIM) and multi-file code generation.", FileName = "codegemma-4-9B-it-Q4_K_M.gguf", ParameterBillions = 9, RequiredVramMB = 7168, ApproxSizeBytes = 5_700_000_000, Category = "Coding", Url = "https://huggingface.co/unsloth/codegemma-4-9B-it-GGUF", Stars = 4 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 3N — mobile-class multimodal. Uses the gemma3n arch (llama-server path).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "unsloth/gemma-3n-E2B-it-GGUF", DisplayName = "Gemma 3N E2B", Description = "Mobile-optimized 2B effective params. Per-Layer Embeddings + MatFormer for phone-grade hardware.", FileName = "gemma-3n-E2B-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 2048, ApproxSizeBytes = 1_500_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-3n-E2B-it-GGUF", Stars = 2 },
        new() { RepoId = "unsloth/gemma-3n-E4B-it-GGUF", DisplayName = "Gemma 3N E4B", Description = "Mobile-optimized 4B effective. Audio/vision input. Runs well on laptops and edge devices.", FileName = "gemma-3n-E4B-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 3_000_000_000, Category = "General", Url = "https://huggingface.co/unsloth/gemma-3n-E4B-it-GGUF", Stars = 3 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 3 — Google's previous generation, still excellent (March 2025).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "bartowski/google_gemma-3-1b-it-GGUF", DisplayName = "Gemma 3 1B", Description = "Google's ultra-compact model. Runs on any hardware, even CPU-only. Great for quick tasks.", FileName = "google_gemma-3-1b-it-Q4_K_M.gguf", ParameterBillions = 1, RequiredVramMB = 1536, ApproxSizeBytes = 900_000_000, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-1b-it-GGUF", Stars = 1 },
        new() { RepoId = "bartowski/google_gemma-3-4b-it-GGUF", DisplayName = "Gemma 3 4B", Description = "Google's compact powerhouse. Excellent quality-to-size ratio with vision support.", FileName = "google_gemma-3-4b-it-Q4_K_M.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 2_800_000_000, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/google_gemma-3-12b-it-GGUF", DisplayName = "Gemma 3 12B", Description = "Google's mid-size model. Strong reasoning and coding, multimodal. Fits 12GB VRAM.", FileName = "google_gemma-3-12b-it-Q4_K_M.gguf", ParameterBillions = 12, RequiredVramMB = 9216, ApproxSizeBytes = 7_600_000_000, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-12b-it-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/google_gemma-3-27b-it-GGUF", DisplayName = "Gemma 3 27B", Description = "Google's previous flagship. 140+ languages, vision, function calling.", FileName = "google_gemma-3-27b-it-Q4_K_M.gguf", ParameterBillions = 27, RequiredVramMB = 18432, ApproxSizeBytes = 17_200_000_000, Category = "General", Url = "https://huggingface.co/bartowski/google_gemma-3-27b-it-GGUF", Stars = 4 },

        // Gemma 3 QAT (quantization-aware trained) — near-fp16 quality at Q4 size.
        // Officially published by Google; strongly recommended when low-bit inference matters.
        new() { RepoId = "google/gemma-3-1b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 1B (QAT)", Description = "Official Google QAT build. Q4_0 with much smaller quality loss than PTQ. Best tiny model.", FileName = "gemma-3-1b-it-q4_0.gguf", ParameterBillions = 1, RequiredVramMB = 1536, ApproxSizeBytes = 900_000_000, Category = "General", Url = "https://huggingface.co/google/gemma-3-1b-it-qat-q4_0-gguf", Stars = 2 },
        new() { RepoId = "google/gemma-3-4b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 4B (QAT)", Description = "Official Google QAT build. Best-in-class 4B. Runs well on 4GB VRAM with vision support.", FileName = "gemma-3-4b-it-q4_0.gguf", ParameterBillions = 4, RequiredVramMB = 4096, ApproxSizeBytes = 2_700_000_000, Category = "General", Url = "https://huggingface.co/google/gemma-3-4b-it-qat-q4_0-gguf", Stars = 4 },
        new() { RepoId = "google/gemma-3-12b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 12B (QAT)", Description = "Official Google QAT. Near-fp16 quality at Q4 size. Outstanding for 12GB cards.", FileName = "gemma-3-12b-it-q4_0.gguf", ParameterBillions = 12, RequiredVramMB = 9216, ApproxSizeBytes = 7_400_000_000, Category = "General", Url = "https://huggingface.co/google/gemma-3-12b-it-qat-q4_0-gguf", Stars = 5 },
        new() { RepoId = "google/gemma-3-27b-it-qat-q4_0-gguf", DisplayName = "Gemma 3 27B (QAT)", Description = "Official Google QAT. Flagship-class quality at Q4 footprint. 24GB VRAM recommended.", FileName = "gemma-3-27b-it-q4_0.gguf", ParameterBillions = 27, RequiredVramMB = 18432, ApproxSizeBytes = 16_900_000_000, Category = "General", Url = "https://huggingface.co/google/gemma-3-27b-it-qat-q4_0-gguf", Stars = 5 },

        // ─────────────────────────────────────────────────────────────────
        // Gemma 2 — legacy but still popular. Supported natively by LLamaSharp (in-proc).
        // ─────────────────────────────────────────────────────────────────
        new() { RepoId = "bartowski/gemma-2-2b-it-GGUF", DisplayName = "Gemma 2 2B", Description = "Compact general model. Runs well even on CPU. Still a great daily driver.", FileName = "gemma-2-2b-it-Q4_K_M.gguf", ParameterBillions = 2, RequiredVramMB = 2048, ApproxSizeBytes = 1_700_000_000, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-2b-it-GGUF", Stars = 2 },
        new() { RepoId = "bartowski/gemma-2-9b-it-GGUF", DisplayName = "Gemma 2 9B", Description = "Classic 9B workhorse. Strong instruction following. Runs great on 8GB VRAM.", FileName = "gemma-2-9b-it-Q4_K_M.gguf", ParameterBillions = 9, RequiredVramMB = 7168, ApproxSizeBytes = 5_400_000_000, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-9b-it-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/gemma-2-27b-it-GGUF", DisplayName = "Gemma 2 27B", Description = "Older flagship, still strong. Good option if you want broader multilingual coverage.", FileName = "gemma-2-27b-it-Q4_K_M.gguf", ParameterBillions = 27, RequiredVramMB = 17408, ApproxSizeBytes = 16_500_000_000, Category = "General", Url = "https://huggingface.co/bartowski/gemma-2-27b-it-GGUF", Stars = 3 },

        // CodeGemma (Gemma 2-era) — coding specialist for in-proc LLamaSharp users
        new() { RepoId = "bartowski/codegemma-7b-it-GGUF", DisplayName = "CodeGemma 7B", Description = "Google's coding-specialized Gemma 2. Fill-in-the-middle + function completion.", FileName = "codegemma-7b-it-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_600_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/codegemma-7b-it-GGUF", Stars = 3 },

        // DeepSeek - Outstanding reasoning
        new() { RepoId = "bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF", DisplayName = "DeepSeek Coder V2 Lite", Description = "MoE architecture: only 2.4B active params but 16B total. Very efficient.", FileName = "DeepSeek-Coder-V2-Lite-Instruct-Q4_K_M.gguf", ParameterBillions = 16, RequiredVramMB = 10240, ApproxSizeBytes = 9_400_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", DisplayName = "DeepSeek R1 Distill 7B", Description = "Distilled reasoning model. Great for step-by-step problem solving.", FileName = "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_700_000_000, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", Stars = 4 },
        new() { RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", DisplayName = "DeepSeek R1 Distill 14B", Description = "Strong reasoning distilled from DeepSeek R1. Good for complex tasks.", FileName = "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", ParameterBillions = 14, RequiredVramMB = 10240, ApproxSizeBytes = 8_900_000_000, Category = "Reasoning", Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", Stars = 5 },

        // Llama 3 family
        new() { RepoId = "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", DisplayName = "Llama 3.1 8B", Description = "Meta's workhorse model. Excellent general + coding ability.", FileName = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", ParameterBillions = 8, RequiredVramMB = 6144, ApproxSizeBytes = 4_900_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", Stars = 4 },

        // Compact models for low VRAM
        new() { RepoId = "bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 1.5B", Description = "Ultra-compact coder. Runs on 2GB VRAM or CPU-only.", FileName = "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf", ParameterBillions = 1, RequiredVramMB = 2048, ApproxSizeBytes = 1_100_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF", Stars = 2 },
        new() { RepoId = "bartowski/Phi-3.5-mini-instruct-GGUF", DisplayName = "Phi 3.5 Mini 3.8B", Description = "Microsoft's compact model. Surprisingly smart for its size.", FileName = "Phi-3.5-mini-instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_400_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/Llama-3.2-3B-Instruct-GGUF", DisplayName = "Llama 3.2 3B", Description = "Meta's compact general model. Fast inference on CPU.", FileName = "Llama-3.2-3B-Instruct-Q4_K_M.gguf", ParameterBillions = 3, RequiredVramMB = 4096, ApproxSizeBytes = 2_000_000_000, Category = "General", Url = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF", Stars = 3 },

        // StarCoder2
        new() { RepoId = "bartowski/starcoder2-7b-GGUF", DisplayName = "StarCoder2 7B", Description = "BigCode's code model. Trained on The Stack v2.", FileName = "starcoder2-7b-Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_400_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/starcoder2-7b-GGUF", Stars = 3 },
        new() { RepoId = "bartowski/starcoder2-15b-GGUF", DisplayName = "StarCoder2 15B", Description = "Larger StarCoder for better output quality.", FileName = "starcoder2-15b-Q4_K_M.gguf", ParameterBillions = 15, RequiredVramMB = 12288, ApproxSizeBytes = 9_100_000_000, Category = "Coding", Url = "https://huggingface.co/bartowski/starcoder2-15b-GGUF", Stars = 3 },

        // CodeLlama
        new() { RepoId = "TheBloke/CodeLlama-7B-Instruct-GGUF", DisplayName = "CodeLlama 7B", Description = "Meta's code-specialized LLaMA. Good for code completion.", FileName = "codellama-7b-instruct.Q4_K_M.gguf", ParameterBillions = 7, RequiredVramMB = 6144, ApproxSizeBytes = 4_100_000_000, Category = "Coding", Url = "https://huggingface.co/TheBloke/CodeLlama-7B-Instruct-GGUF", Stars = 3 },
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
