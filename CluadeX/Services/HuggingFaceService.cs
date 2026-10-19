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
        // Every RepoId / file name / size below comes from the public HF API (GET /api/models/<repo>?blobs=true) as of
        // 2026-10-19 — generated, not typed; tests/managertest --network re-checks every quant of every entry.
        // Architecture / NativeContext / TotalParamsB are the API's "gguf" block; ActiveParamsB is from the model card.
        // Scores (0-5 per job) rank recommendations; Description is the English fallback of "modelsm.desc.<Id>".
        // Projector = the repo's F16 vision projector (mmproj); ProjectorType = its GGUF "clip(.vision).projector_type",
        // read from the file header on HF (Range request) — checked against mtmd.dll by LlamaBackendInfo.
        // Retired on 2026-10-19: Gemma 3N, Gemma 3 QAT (gated), Gemma 2 + CodeGemma, DeepSeek Coder V2 Lite, StarCoder2,
        // CodeLlama, Phi 3.5, Qwen2.5 Coder 1.5B/32B, DeepSeek R1 Distill 7B — superseded by the 2026 models below.

        // ─── Current generation ───
        new() { Id = "qwen3.8-27b", RepoId = "unsloth/Qwen3.8-27B-GGUF", DisplayName = "Qwen3.8 27B", Publisher = "Qwen", Released = "2026-08", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 27.3, ActiveParamsB = 0, ParameterBillions = 27, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Coding | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 5, ChatScore = 5, ReasoningScore = 5, ThaiScore = 4, VisionScore = 4, Stars = 5, IsLegacy = false, FileName = "Qwen3.8-27B-UD-Q4_K_M.gguf", ApproxSizeBytes = 16464440224, RequiredVramMB = 18432, Url = "https://huggingface.co/unsloth/Qwen3.8-27B-GGUF", Description = "Qwen's newest dense model: strongest all-rounder that still runs on one 24 GB card — coding, agents, long documents, images and video.", Projector = Q("mmproj-F16.gguf", 927607488), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.8-27B-UD-IQ4_XS.gguf", 14252845984), Q("Qwen3.8-27B-UD-Q4_K_M.gguf", 16464440224), Q("Qwen3.8-27B-UD-Q5_K_M.gguf", 19771509664), Q("Qwen3.8-27B-UD-Q6_K.gguf", 21983677344), Q("Qwen3.8-27B-Q8_0.gguf", 29047086048) } },
        new() { Id = "qwen3.6-35b-a3b", RepoId = "unsloth/Qwen3.6-35B-A3B-GGUF", DisplayName = "Qwen3.6 35B-A3B", Publisher = "Qwen", Released = "2026-04", Architecture = "qwen35moe", NativeContext = 262144, TotalParamsB = 34.7, ActiveParamsB = 3, ParameterBillions = 35, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Coding | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 5, ChatScore = 4, ReasoningScore = 4, ThaiScore = 4, VisionScore = 4, Stars = 5, IsLegacy = false, FileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf", ApproxSizeBytes = 22134528992, RequiredVramMB = 24064, Url = "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF", Description = "Mixture-of-experts: 35B of knowledge but only 3B active per token, so it stays quick even when part of it sits in system RAM. Tuned for agentic coding.", Projector = Q("mmproj-F16.gguf", 899283680), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.6-35B-A3B-UD-Q3_K_M.gguf", 16600710112), Q("Qwen3.6-35B-A3B-UD-IQ4_XS.gguf", 17730509792), Q("Qwen3.6-35B-A3B-UD-Q4_K_M.gguf", 22134528992), Q("Qwen3.6-35B-A3B-UD-Q5_K_M.gguf", 26456194016), Q("Qwen3.6-35B-A3B-UD-Q6_K.gguf", 29308320736), Q("Qwen3.6-35B-A3B-Q8_0.gguf", 36903140320) } },
        new() { Id = "kat-coder-v2.5-dev", RepoId = "bartowski/Kwaipilot_KAT-Coder-V2.5-Dev-GGUF", DisplayName = "KAT-Coder V2.5 Dev", Publisher = "Kwaipilot", Released = "2026-07", Architecture = "qwen35moe", NativeContext = 262144, TotalParamsB = 34.7, ActiveParamsB = 3, ParameterBillions = 35, Category = "Coding", Tasks = ModelTasks.Coding | ModelTasks.Reasoning, CodingScore = 5, ChatScore = 3, ReasoningScore = 4, ThaiScore = 2, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "Kwaipilot_KAT-Coder-V2.5-Dev-Q4_K_M.gguf", ApproxSizeBytes = 21391448480, RequiredVramMB = 23552, Url = "https://huggingface.co/bartowski/Kwaipilot_KAT-Coder-V2.5-Dev-GGUF", Description = "Agentic-coding specialist (35B MoE, 3B active) built on Qwen3.5. Text only.", Quants = { Q("Kwaipilot_KAT-Coder-V2.5-Dev-Q3_K_M.gguf", 16226548128), Q("Kwaipilot_KAT-Coder-V2.5-Dev-IQ4_XS.gguf", 18806446496), Q("Kwaipilot_KAT-Coder-V2.5-Dev-Q4_K_M.gguf", 21391448480), Q("Kwaipilot_KAT-Coder-V2.5-Dev-Q5_K_M.gguf", 25017457056), Q("Kwaipilot_KAT-Coder-V2.5-Dev-Q6_K.gguf", 30053415328), Q("Kwaipilot_KAT-Coder-V2.5-Dev-Q8_0.gguf", 36914690464) } },
        new() { Id = "ornith-1.0-9b", RepoId = "unsloth/Ornith-1.0-9B-GGUF", DisplayName = "Ornith 1.0 9B", Publisher = "DeepReinforce", Released = "2026-07", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 9.0, ActiveParamsB = 0, ParameterBillions = 9, Category = "Coding", Tasks = ModelTasks.Coding | ModelTasks.Reasoning, CodingScore = 4, ChatScore = 3, ReasoningScore = 4, ThaiScore = 3, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "Ornith-1.0-9B-Q4_K_M.gguf", ApproxSizeBytes = 5701067872, RequiredVramMB = 7168, Url = "https://huggingface.co/unsloth/Ornith-1.0-9B-GGUF", Description = "A 9B coding agent trained with self-improving RL on top of Qwen3.5 — the best coder that fits an 8 GB card.", Quants = { Q("Ornith-1.0-9B-Q3_K_M.gguf", 4699464800), Q("Ornith-1.0-9B-IQ4_XS.gguf", 5312963680), Q("Ornith-1.0-9B-Q4_K_M.gguf", 5701067872), Q("Ornith-1.0-9B-Q5_K_M.gguf", 6542287968), Q("Ornith-1.0-9B-Q6_K.gguf", 7476782176), Q("Ornith-1.0-9B-Q8_0.gguf", 9527501920) } },
        new() { Id = "mimo-v2.6-distill-9b", RepoId = "bartowski/MiMo-V2.6-Distill-Qwen-9B-GGUF", DisplayName = "MiMo V2.6 Distill 9B", Publisher = "Xiaomi MiMo", Released = "2026-09", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 9.0, ActiveParamsB = 0, ParameterBillions = 9, Category = "Coding", Tasks = ModelTasks.Coding | ModelTasks.Reasoning | ModelTasks.Vision, CodingScore = 4, ChatScore = 3, ReasoningScore = 4, ThaiScore = 3, VisionScore = 3, Stars = 4, IsLegacy = false, FileName = "MiMo-V2.6-Distill-Qwen-9B-Q4_K_M.gguf", ApproxSizeBytes = 5841049120, RequiredVramMB = 7168, Url = "https://huggingface.co/bartowski/MiMo-V2.6-Distill-Qwen-9B-GGUF", Description = "Xiaomi's 9B agent model (Qwen3.5-9B fine-tune): coding, tool use and visual coding from screenshots.", Projector = Q("mmproj-MiMo-V2.6-Distill-Qwen-9B-f16.gguf", 918166048), ProjectorType = "qwen3vl_merger", Quants = { Q("MiMo-V2.6-Distill-Qwen-9B-Q3_K_M.gguf", 4479948320), Q("MiMo-V2.6-Distill-Qwen-9B-IQ4_XS.gguf", 5227304480), Q("MiMo-V2.6-Distill-Qwen-9B-Q4_K_M.gguf", 5841049120), Q("MiMo-V2.6-Distill-Qwen-9B-Q5_K_M.gguf", 6876124704), Q("MiMo-V2.6-Distill-Qwen-9B-Q6_K.gguf", 7793710624), Q("MiMo-V2.6-Distill-Qwen-9B-Q8_0.gguf", 9545979424) } },
        new() { Id = "devstral-small-2-24b", RepoId = "unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF", DisplayName = "Devstral Small 2 24B", Publisher = "Mistral AI", Released = "2025-12", Architecture = "mistral3", NativeContext = 393216, TotalParamsB = 23.6, ActiveParamsB = 0, ParameterBillions = 24, Category = "Coding", Tasks = ModelTasks.Coding | ModelTasks.Vision, CodingScore = 4, ChatScore = 3, ReasoningScore = 3, ThaiScore = 2, VisionScore = 3, Stars = 4, IsLegacy = false, FileName = "Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf", ApproxSizeBytes = 14334446752, RequiredVramMB = 15872, Url = "https://huggingface.co/unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF", Description = "Mistral's software-engineering agent model with a 256K context and image input.", Projector = Q("mmproj-F16.gguf", 878054048), ProjectorType = "pixtral", Quants = { Q("Devstral-Small-2-24B-Instruct-2512-Q3_K_M.gguf", 11474619552), Q("Devstral-Small-2-24B-Instruct-2512-IQ4_XS.gguf", 12780424352), Q("Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf", 14334446752), Q("Devstral-Small-2-24B-Instruct-2512-Q5_K_M.gguf", 16764521632), Q("Devstral-Small-2-24B-Instruct-2512-Q6_K.gguf", 19346476192), Q("Devstral-Small-2-24B-Instruct-2512-Q8_0.gguf", 25055317152) } },
        new() { Id = "qwen3-coder-30b-a3b", RepoId = "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", DisplayName = "Qwen3 Coder 30B-A3B", Publisher = "Qwen", Released = "2025-07", Architecture = "qwen3moe", NativeContext = 262144, TotalParamsB = 30.5, ActiveParamsB = 3.3, ParameterBillions = 31, Category = "Coding", Tasks = ModelTasks.Coding, CodingScore = 4, ChatScore = 3, ReasoningScore = 3, ThaiScore = 3, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", ApproxSizeBytes = 18556689568, RequiredVramMB = 20480, Url = "https://huggingface.co/unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", Description = "Proven coding MoE (3.3B active). Fast with CPU offload, solid tool calling.", Quants = { Q("Qwen3-Coder-30B-A3B-Instruct-Q3_K_M.gguf", 14711850144), Q("Qwen3-Coder-30B-A3B-Instruct-IQ4_XS.gguf", 16378076320), Q("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", 18556689568), Q("Qwen3-Coder-30B-A3B-Instruct-Q5_K_M.gguf", 21725584544), Q("Qwen3-Coder-30B-A3B-Instruct-Q6_K.gguf", 25092535456), Q("Qwen3-Coder-30B-A3B-Instruct-Q8_0.gguf", 32483935392) } },
        new() { Id = "qwen3-coder-next", RepoId = "unsloth/Qwen3-Coder-Next-GGUF", DisplayName = "Qwen3 Coder Next 80B-A3B", Publisher = "Qwen", Released = "2026-02", Architecture = "qwen3next", NativeContext = 262144, TotalParamsB = 79.7, ActiveParamsB = 3, ParameterBillions = 80, Category = "Coding", Tasks = ModelTasks.Coding | ModelTasks.Reasoning, CodingScore = 5, ChatScore = 3, ReasoningScore = 4, ThaiScore = 3, VisionScore = 0, Stars = 5, IsLegacy = false, FileName = "Qwen3-Coder-Next-Q4_K_M.gguf", ApproxSizeBytes = 48528320544, RequiredVramMB = 51712, Url = "https://huggingface.co/unsloth/Qwen3-Coder-Next-GGUF", Description = "Workstation-class coder: 80B total, 3B active. Needs about 48 GB of VRAM + RAM combined.", Quants = { Q("Qwen3-Coder-Next-Q3_K_M.gguf", 38322487328), Q("Qwen3-Coder-Next-IQ4_XS.gguf", 42676135968), Q("Qwen3-Coder-Next-Q4_K_M.gguf", 48528320544) } },
        new() { Id = "gemma-4-31b", RepoId = "unsloth/gemma-4-31B-it-GGUF", DisplayName = "Gemma 4 31B", Publisher = "Google", Released = "2026-04", Architecture = "gemma4", NativeContext = 262144, TotalParamsB = 30.7, ActiveParamsB = 0, ParameterBillions = 31, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai | ModelTasks.Coding, CodingScore = 4, ChatScore = 5, ReasoningScore = 5, ThaiScore = 5, VisionScore = 5, Stars = 5, IsLegacy = false, FileName = "gemma-4-31B-it-Q4_K_M.gguf", ApproxSizeBytes = 18323733440, RequiredVramMB = 20480, Url = "https://huggingface.co/unsloth/gemma-4-31B-it-GGUF", Description = "Google's flagship open model: 140+ languages (excellent Thai), vision, 256K context, strong reasoning.", Projector = Q("mmproj-F16.gguf", 1198957024), ProjectorType = "gemma4v", Quants = { Q("gemma-4-31B-it-Q3_K_M.gguf", 14736608192), Q("gemma-4-31B-it-IQ4_XS.gguf", 16372460480), Q("gemma-4-31B-it-Q4_K_M.gguf", 18323733440), Q("gemma-4-31B-it-Q5_K_M.gguf", 21658401728), Q("gemma-4-31B-it-Q6_K.gguf", 25201486784), Q("gemma-4-31B-it-Q8_0.gguf", 32635677632) } },
        new() { Id = "gemma-4-26b-a4b", RepoId = "unsloth/gemma-4-26B-A4B-it-GGUF", DisplayName = "Gemma 4 26B-A4B", Publisher = "Google", Released = "2026-04", Architecture = "gemma4", NativeContext = 262144, TotalParamsB = 25.2, ActiveParamsB = 3.8, ParameterBillions = 25, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai | ModelTasks.Coding, CodingScore = 4, ChatScore = 4, ReasoningScore = 4, ThaiScore = 4, VisionScore = 4, Stars = 4, IsLegacy = false, FileName = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", ApproxSizeBytes = 16947541728, RequiredVramMB = 18944, Url = "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF", Description = "Gemma 4 as a mixture-of-experts: 3.8B active, so it runs fast even partly on the CPU.", Projector = Q("mmproj-F16.gguf", 1193058784), ProjectorType = "gemma4v", Quants = { Q("gemma-4-26B-A4B-it-UD-Q3_K_M.gguf", 12728497888), Q("gemma-4-26B-A4B-it-UD-IQ4_XS.gguf", 13597177568), Q("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16947541728), Q("gemma-4-26B-A4B-it-UD-Q5_K_M.gguf", 21150365408), Q("gemma-4-26B-A4B-it-UD-Q6_K.gguf", 23172478688), Q("gemma-4-26B-A4B-it-Q8_0.gguf", 26859861728) } },
        new() { Id = "gemma-4-12b", RepoId = "unsloth/gemma-4-12b-it-GGUF", DisplayName = "Gemma 4 12B", Publisher = "Google", Released = "2026-05", Architecture = "gemma4", NativeContext = 262144, TotalParamsB = 11.9, ActiveParamsB = 0, ParameterBillions = 12, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 3, ChatScore = 4, ReasoningScore = 4, ThaiScore = 4, VisionScore = 4, Stars = 4, IsLegacy = false, FileName = "gemma-4-12b-it-Q4_K_M.gguf", ApproxSizeBytes = 7121861440, RequiredVramMB = 8704, Url = "https://huggingface.co/unsloth/gemma-4-12b-it-GGUF", Description = "Mid-size Gemma 4 with built-in image and audio understanding. Great Thai for its size.", Projector = Q("mmproj-F16.gguf", 175115840), ProjectorType = "gemma4uv", Quants = { Q("gemma-4-12b-it-Q3_K_M.gguf", 5693872960), Q("gemma-4-12b-it-IQ4_XS.gguf", 6375734080), Q("gemma-4-12b-it-Q4_K_M.gguf", 7121861440), Q("gemma-4-12b-it-Q5_K_M.gguf", 8413576000), Q("gemma-4-12b-it-Q6_K.gguf", 9786022720), Q("gemma-4-12b-it-Q8_0.gguf", 12669647680) } },
        new() { Id = "gemma-4-e4b", RepoId = "unsloth/gemma-4-E4B-it-GGUF", DisplayName = "Gemma 4 E4B", Publisher = "Google", Released = "2026-04", Architecture = "gemma4", NativeContext = 131072, TotalParamsB = 7.5, ActiveParamsB = 0, ParameterBillions = 8, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 2, ChatScore = 3, ReasoningScore = 3, ThaiScore = 3, VisionScore = 3, Stars = 3, IsLegacy = false, FileName = "gemma-4-E4B-it-Q4_K_M.gguf", ApproxSizeBytes = 4977171584, RequiredVramMB = 6144, Url = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF", Description = "Laptop-class Gemma 4 (4.5B effective) with image and audio input.", Projector = Q("mmproj-F16.gguf", 990372672), ProjectorType = "gemma4v", Quants = { Q("gemma-4-E4B-it-Q3_K_M.gguf", 4058137728), Q("gemma-4-E4B-it-IQ4_XS.gguf", 4715416704), Q("gemma-4-E4B-it-Q4_K_M.gguf", 4977171584), Q("gemma-4-E4B-it-Q5_K_M.gguf", 5481798784), Q("gemma-4-E4B-it-Q6_K.gguf", 7074929792), Q("gemma-4-E4B-it-Q8_0.gguf", 8192953472) } },
        new() { Id = "gemma-4-e2b", RepoId = "unsloth/gemma-4-E2B-it-GGUF", DisplayName = "Gemma 4 E2B", Publisher = "Google", Released = "2026-04", Architecture = "gemma4", NativeContext = 131072, TotalParamsB = 4.6, ActiveParamsB = 0, ParameterBillions = 5, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Vision, CodingScore = 2, ChatScore = 2, ReasoningScore = 2, ThaiScore = 2, VisionScore = 3, Stars = 2, IsLegacy = false, FileName = "gemma-4-E2B-it-Q4_K_M.gguf", ApproxSizeBytes = 3106738272, RequiredVramMB = 4096, Url = "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF", Description = "Smallest Gemma 4 (2.3B effective) — runs on almost anything, even CPU only.", Projector = Q("mmproj-F16.gguf", 985654080), ProjectorType = "gemma4v", Quants = { Q("gemma-4-E2B-it-Q3_K_M.gguf", 2536786016), Q("gemma-4-E2B-it-IQ4_XS.gguf", 2983944288), Q("gemma-4-E2B-it-Q4_K_M.gguf", 3106738272), Q("gemma-4-E2B-it-Q5_K_M.gguf", 3356037216), Q("gemma-4-E2B-it-Q6_K.gguf", 4501721184), Q("gemma-4-E2B-it-Q8_0.gguf", 5048352864) } },
        new() { Id = "qwen3.5-9b", RepoId = "unsloth/Qwen3.5-9B-GGUF", DisplayName = "Qwen3.5 9B", Publisher = "Qwen", Released = "2026-02", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 9.0, ActiveParamsB = 0, ParameterBillions = 9, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Coding | ModelTasks.Reasoning | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 3, ChatScore = 4, ReasoningScore = 4, ThaiScore = 4, VisionScore = 4, Stars = 4, IsLegacy = false, FileName = "Qwen3.5-9B-Q4_K_M.gguf", ApproxSizeBytes = 5680522464, RequiredVramMB = 7168, Url = "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF", Description = "Best general model for 8 GB cards: multilingual, sees images, 256K context.", Projector = Q("mmproj-F16.gguf", 918166080), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.5-9B-Q3_K_M.gguf", 4673643744), Q("Qwen3.5-9B-IQ4_XS.gguf", 5168653536), Q("Qwen3.5-9B-Q4_K_M.gguf", 5680522464), Q("Qwen3.5-9B-Q5_K_M.gguf", 6577841376), Q("Qwen3.5-9B-Q6_K.gguf", 7458301152), Q("Qwen3.5-9B-Q8_0.gguf", 9527502048) } },
        new() { Id = "qwen3.5-4b", RepoId = "unsloth/Qwen3.5-4B-GGUF", DisplayName = "Qwen3.5 4B", Publisher = "Qwen", Released = "2026-03", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 4.2, ActiveParamsB = 0, ParameterBillions = 4, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Coding | ModelTasks.Vision | ModelTasks.Thai, CodingScore = 3, ChatScore = 3, ReasoningScore = 3, ThaiScore = 3, VisionScore = 3, Stars = 3, IsLegacy = false, FileName = "Qwen3.5-4B-Q4_K_M.gguf", ApproxSizeBytes = 2740937888, RequiredVramMB = 4096, Url = "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF", Description = "Compact all-rounder for 4–6 GB cards and laptops.", Projector = Q("mmproj-F16.gguf", 672423616), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.5-4B-Q3_K_M.gguf", 2293388448), Q("Qwen3.5-4B-IQ4_XS.gguf", 2477053088), Q("Qwen3.5-4B-Q4_K_M.gguf", 2740937888), Q("Qwen3.5-4B-Q5_K_M.gguf", 3143656608), Q("Qwen3.5-4B-Q6_K.gguf", 3525956768), Q("Qwen3.5-4B-Q8_0.gguf", 4482403488) } },
        new() { Id = "qwen3.5-2b", RepoId = "unsloth/Qwen3.5-2B-GGUF", DisplayName = "Qwen3.5 2B", Publisher = "Qwen", Released = "2026-02", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 1.9, ActiveParamsB = 0, ParameterBillions = 2, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Vision, CodingScore = 2, ChatScore = 2, ReasoningScore = 2, ThaiScore = 2, VisionScore = 2, Stars = 2, IsLegacy = false, FileName = "Qwen3.5-2B-Q4_K_M.gguf", ApproxSizeBytes = 1280835840, RequiredVramMB = 2560, Url = "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF", Description = "Tiny and quick — fine for simple chat and short tasks on CPU.", Projector = Q("mmproj-F16.gguf", 668227264), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.5-2B-Q3_K_M.gguf", 1107149056), Q("Qwen3.5-2B-IQ4_XS.gguf", 1172996352), Q("Qwen3.5-2B-Q4_K_M.gguf", 1280835840), Q("Qwen3.5-2B-Q5_K_M.gguf", 1435238656), Q("Qwen3.5-2B-Q6_K.gguf", 1574961408), Q("Qwen3.5-2B-Q8_0.gguf", 2012012800) } },
        new() { Id = "qwen3.5-0.8b", RepoId = "unsloth/Qwen3.5-0.8B-GGUF", DisplayName = "Qwen3.5 0.8B", Publisher = "Qwen", Released = "2026-03", Architecture = "qwen35", NativeContext = 262144, TotalParamsB = 0.8, ActiveParamsB = 0, ParameterBillions = 1, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Vision, CodingScore = 1, ChatScore = 2, ReasoningScore = 1, ThaiScore = 1, VisionScore = 2, Stars = 1, IsLegacy = false, FileName = "Qwen3.5-0.8B-Q4_K_M.gguf", ApproxSizeBytes = 532517120, RequiredVramMB = 1536, Url = "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF", Description = "Under 1 GB. For very old hardware or quick experiments.", Projector = Q("mmproj-F16.gguf", 204987232), ProjectorType = "qwen3vl_merger", Quants = { Q("Qwen3.5-0.8B-Q3_K_M.gguf", 470167808), Q("Qwen3.5-0.8B-IQ4_XS.gguf", 492605696), Q("Qwen3.5-0.8B-Q4_K_M.gguf", 532517120), Q("Qwen3.5-0.8B-Q5_K_M.gguf", 590057728), Q("Qwen3.5-0.8B-Q6_K.gguf", 639029504), Q("Qwen3.5-0.8B-Q8_0.gguf", 811843840) } },
        new() { Id = "gpt-oss-20b", RepoId = "unsloth/gpt-oss-20b-GGUF", DisplayName = "gpt-oss 20B", Publisher = "OpenAI", Released = "2025-08", Architecture = "gpt-oss", NativeContext = 131072, TotalParamsB = 20.9, ActiveParamsB = 3.6, ParameterBillions = 21, Category = "Reasoning", Tasks = ModelTasks.Reasoning | ModelTasks.Coding | ModelTasks.Chat, CodingScore = 4, ChatScore = 4, ReasoningScore = 5, ThaiScore = 2, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "gpt-oss-20b-Q4_K_M.gguf", ApproxSizeBytes = 11624759488, RequiredVramMB = 13312, Url = "https://huggingface.co/unsloth/gpt-oss-20b-GGUF", Description = "OpenAI's open reasoning model (3.6B active). Shipped in MXFP4, so every quant is about the same size.", Quants = { Q("gpt-oss-20b-Q4_K_M.gguf", 11624759488), Q("gpt-oss-20b-Q8_0.gguf", 12109567168) } },
        new() { Id = "nemotron-3.5-lightning-30b-a3b", RepoId = "ggml-org/NVIDIA-Nemotron-3.5-Lightning-30B-A3B-GGUF", DisplayName = "Nemotron 3.5 Lightning 30B-A3B", Publisher = "NVIDIA", Released = "2026-08", Architecture = "nemotron_h_moe", NativeContext = 1048576, TotalParamsB = 31.6, ActiveParamsB = 3, ParameterBillions = 32, Category = "Reasoning", Tasks = ModelTasks.Reasoning | ModelTasks.Coding | ModelTasks.Chat, CodingScore = 4, ChatScore = 4, ReasoningScore = 4, ThaiScore = 2, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "NVIDIA-Nemotron-3.5-Lightning-30B-A3B-Q4_0.gguf", ApproxSizeBytes = 18898091584, RequiredVramMB = 20992, Url = "https://huggingface.co/ggml-org/NVIDIA-Nemotron-3.5-Lightning-30B-A3B-GGUF", Description = "NVIDIA's hybrid Mamba MoE for agents: 3B active, up to 1M context.", Quants = { Q("NVIDIA-Nemotron-3.5-Lightning-30B-A3B-Q4_0.gguf", 18898091584), Q("NVIDIA-Nemotron-3.5-Lightning-30B-A3B-Q8_0.gguf", 33585495616) } },
        new() { Id = "ministral-3-14b-reasoning", RepoId = "lmstudio-community/Ministral-3-14B-Reasoning-2512-GGUF", DisplayName = "Ministral 3 14B Reasoning", Publisher = "Mistral AI", Released = "2025-12", Architecture = "mistral3", NativeContext = 262144, TotalParamsB = 13.5, ActiveParamsB = 0, ParameterBillions = 14, Category = "Reasoning", Tasks = ModelTasks.Reasoning | ModelTasks.Chat | ModelTasks.Vision, CodingScore = 3, ChatScore = 3, ReasoningScore = 4, ThaiScore = 2, VisionScore = 3, Stars = 3, IsLegacy = false, FileName = "Ministral-3-14B-Reasoning-2512-Q4_K_M.gguf", ApproxSizeBytes = 8239066144, RequiredVramMB = 9728, Url = "https://huggingface.co/lmstudio-community/Ministral-3-14B-Reasoning-2512-GGUF", Description = "Mistral's 14B thinking model with image input.", Projector = Q("mmproj-Ministral-3-14B-Reasoning-2512-F16.gguf", 878053440), ProjectorType = "pixtral", Quants = { Q("Ministral-3-14B-Reasoning-2512-Q4_K_M.gguf", 8239066144), Q("Ministral-3-14B-Reasoning-2512-Q6_K.gguf", 11088407584), Q("Ministral-3-14B-Reasoning-2512-Q8_0.gguf", 14359309344) } },
        new() { Id = "nemotron-3-nano-4b", RepoId = "lmstudio-community/NVIDIA-Nemotron-3-Nano-4B-GGUF", DisplayName = "Nemotron 3 Nano 4B", Publisher = "NVIDIA", Released = "2026-03", Architecture = "nemotron_h", NativeContext = 1048576, TotalParamsB = 4.0, ActiveParamsB = 0, ParameterBillions = 4, Category = "Reasoning", Tasks = ModelTasks.Reasoning | ModelTasks.Chat, CodingScore = 2, ChatScore = 3, ReasoningScore = 3, ThaiScore = 1, VisionScore = 0, Stars = 2, IsLegacy = false, FileName = "NVIDIA-Nemotron-3-Nano-4B-Q4_K_M.gguf", ApproxSizeBytes = 2837072896, RequiredVramMB = 4096, Url = "https://huggingface.co/lmstudio-community/NVIDIA-Nemotron-3-Nano-4B-GGUF", Description = "Small hybrid model with a very long (1M) context window.", Quants = { Q("NVIDIA-Nemotron-3-Nano-4B-Q4_K_M.gguf", 2837072896), Q("NVIDIA-Nemotron-3-Nano-4B-Q6_K.gguf", 3933669376), Q("NVIDIA-Nemotron-3-Nano-4B-Q8_0.gguf", 4233677952) } },
        new() { Id = "typhoon2.5-qwen3-30b-a3b", RepoId = "typhoon-ai/typhoon2.5-qwen3-30b-a3b-gguf", DisplayName = "Typhoon 2.5 30B-A3B", Publisher = "Typhoon (SCB 10X)", Released = "2025-10", Architecture = "qwen3moe", NativeContext = 262144, TotalParamsB = 30.5, ActiveParamsB = 3.3, ParameterBillions = 31, Category = "Thai", Tasks = ModelTasks.Thai | ModelTasks.Chat | ModelTasks.Reasoning, CodingScore = 3, ChatScore = 4, ReasoningScore = 3, ThaiScore = 5, VisionScore = 0, Stars = 4, IsLegacy = false, FileName = "typhoon2.5-qwen3-30b-a3b-q4_k_m.gguf", ApproxSizeBytes = 18556681696, RequiredVramMB = 20480, Url = "https://huggingface.co/typhoon-ai/typhoon2.5-qwen3-30b-a3b-gguf", Description = "Thai-first model from SCB 10X (Qwen3 30B-A3B base): natural Thai, Thai culture and documents, function calling.", Quants = { Q("typhoon2.5-qwen3-30b-a3b-q4_k_m.gguf", 18556681696) } },
        new() { Id = "typhoon2.5-qwen3-4b", RepoId = "typhoon-ai/typhoon2.5-qwen3-4b-gguf", DisplayName = "Typhoon 2.5 4B", Publisher = "Typhoon (SCB 10X)", Released = "2025-10", Architecture = "qwen3", NativeContext = 262144, TotalParamsB = 4.0, ActiveParamsB = 0, ParameterBillions = 4, Category = "Thai", Tasks = ModelTasks.Thai | ModelTasks.Chat, CodingScore = 2, ChatScore = 3, ReasoningScore = 2, ThaiScore = 5, VisionScore = 0, Stars = 3, IsLegacy = false, FileName = "typhoon2.5-qwen3-4b-q4_k_m.gguf", ApproxSizeBytes = 2497276128, RequiredVramMB = 3584, Url = "https://huggingface.co/typhoon-ai/typhoon2.5-qwen3-4b-gguf", Description = "Small Thai-first model with a 256K context — Thai chat on modest hardware.", Quants = { Q("typhoon2.5-qwen3-4b-q4_k_m.gguf", 2497276128) } },
        new() { Id = "pathumma-thaillm-8b-think", RepoId = "nectec/pathumma-thaillm-8b-think-3.0.0-GGUF", DisplayName = "Pathumma ThaiLLM 8B Think", Publisher = "NECTEC", Released = "2026-02", Architecture = "qwen3", NativeContext = 40960, TotalParamsB = 8.2, ActiveParamsB = 0, ParameterBillions = 8, Category = "Thai", Tasks = ModelTasks.Thai | ModelTasks.Reasoning | ModelTasks.Chat, CodingScore = 2, ChatScore = 3, ReasoningScore = 3, ThaiScore = 5, VisionScore = 0, Stars = 3, IsLegacy = false, FileName = "pathumma-thaillm-8b-think-3-Q4_K_M.gguf", ApproxSizeBytes = 5027782208, RequiredVramMB = 6144, Url = "https://huggingface.co/nectec/pathumma-thaillm-8b-think-3.0.0-GGUF", Description = "NECTEC's Thai reasoning model on the national ThaiLLM base: step-by-step thinking and tool calling in Thai.", Quants = { Q("pathumma-thaillm-8b-think-3-Q4_K_M.gguf", 5027782208), Q("pathumma-thaillm-8b-think-3-Q5_K_M.gguf", 5851110976), Q("pathumma-thaillm-8b-think-3-Q6_K.gguf", 6725897792), Q("pathumma-thaillm-8b-think-3-Q8_0.gguf", 8709516864) } },

        // ─── Older generation: hidden unless "Show older models" is on ───
        new() { Id = "qwen2.5-coder-14b", RepoId = "bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 14B", Publisher = "Qwen", Released = "2024-11", Architecture = "qwen2", NativeContext = 32768, TotalParamsB = 14.8, ActiveParamsB = 0, ParameterBillions = 15, Category = "Coding", Tasks = ModelTasks.Coding, CodingScore = 3, ChatScore = 2, ReasoningScore = 2, ThaiScore = 2, VisionScore = 0, Stars = 3, IsLegacy = true, FileName = "Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf", ApproxSizeBytes = 8988111072, RequiredVramMB = 10240, Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-14B-Instruct-GGUF", Description = "2024 coding model. Superseded by Qwen3.x and Ornith, still runs on the in-process backend.", Quants = { Q("Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf", 8988111072) } },
        new() { Id = "qwen2.5-coder-7b", RepoId = "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", DisplayName = "Qwen2.5 Coder 7B", Publisher = "Qwen", Released = "2024-09", Architecture = "qwen2", NativeContext = 32768, TotalParamsB = 7.6, ActiveParamsB = 0, ParameterBillions = 8, Category = "Coding", Tasks = ModelTasks.Coding, CodingScore = 3, ChatScore = 2, ReasoningScore = 2, ThaiScore = 2, VisionScore = 0, Stars = 3, IsLegacy = true, FileName = "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", ApproxSizeBytes = 4683074336, RequiredVramMB = 6144, Url = "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", Description = "2024 coding model for 8 GB cards. Superseded by Ornith 1.0 9B.", Quants = { Q("Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", 4683074336) } },
        new() { Id = "deepseek-r1-distill-14b", RepoId = "bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", DisplayName = "DeepSeek R1 Distill 14B", Publisher = "DeepSeek", Released = "2025-01", Architecture = "qwen2", NativeContext = 131072, TotalParamsB = 14.8, ActiveParamsB = 0, ParameterBillions = 15, Category = "Reasoning", Tasks = ModelTasks.Reasoning, CodingScore = 2, ChatScore = 2, ReasoningScore = 3, ThaiScore = 2, VisionScore = 0, Stars = 3, IsLegacy = true, FileName = "DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", ApproxSizeBytes = 8988110240, RequiredVramMB = 10240, Url = "https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF", Description = "Early-2025 distilled reasoning model.", Quants = { Q("DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", 8988110240) } },
        new() { Id = "gemma-3-12b", RepoId = "bartowski/google_gemma-3-12b-it-GGUF", DisplayName = "Gemma 3 12B", Publisher = "Google", Released = "2025-03", Architecture = "gemma3", NativeContext = 131072, TotalParamsB = 11.8, ActiveParamsB = 0, ParameterBillions = 12, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Thai, CodingScore = 2, ChatScore = 3, ReasoningScore = 3, ThaiScore = 3, VisionScore = 0, Stars = 3, IsLegacy = true, FileName = "google_gemma-3-12b-it-Q4_K_M.gguf", ApproxSizeBytes = 7300575264, RequiredVramMB = 8704, Url = "https://huggingface.co/bartowski/google_gemma-3-12b-it-GGUF", Description = "Previous-generation Gemma. Gemma 4 12B replaces it.", Quants = { Q("google_gemma-3-12b-it-Q4_K_M.gguf", 7300575264) } },
        new() { Id = "gemma-3-4b", RepoId = "bartowski/google_gemma-3-4b-it-GGUF", DisplayName = "Gemma 3 4B", Publisher = "Google", Released = "2025-03", Architecture = "gemma3", NativeContext = 131072, TotalParamsB = 3.9, ActiveParamsB = 0, ParameterBillions = 4, Category = "General", Tasks = ModelTasks.Chat, CodingScore = 2, ChatScore = 2, ReasoningScore = 2, ThaiScore = 2, VisionScore = 0, Stars = 2, IsLegacy = true, FileName = "google_gemma-3-4b-it-Q4_K_M.gguf", ApproxSizeBytes = 2489758112, RequiredVramMB = 3584, Url = "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF", Description = "Previous-generation small Gemma.", Quants = { Q("google_gemma-3-4b-it-Q4_K_M.gguf", 2489758112) } },
        new() { Id = "phi-4-mini", RepoId = "bartowski/microsoft_Phi-4-mini-instruct-GGUF", DisplayName = "Phi-4 mini 3.8B", Publisher = "Microsoft", Released = "2025-02", Architecture = "phi3", NativeContext = 131072, TotalParamsB = 3.8, ActiveParamsB = 0, ParameterBillions = 4, Category = "General", Tasks = ModelTasks.Chat | ModelTasks.Reasoning, CodingScore = 2, ChatScore = 2, ReasoningScore = 3, ThaiScore = 1, VisionScore = 0, Stars = 2, IsLegacy = true, FileName = "microsoft_Phi-4-mini-instruct-Q4_K_M.gguf", ApproxSizeBytes = 2491874688, RequiredVramMB = 3584, Url = "https://huggingface.co/bartowski/microsoft_Phi-4-mini-instruct-GGUF", Description = "Microsoft's small model from early 2025. English-centred.", Quants = { Q("microsoft_Phi-4-mini-instruct-Q4_K_M.gguf", 2491874688) } },
        new() { Id = "llama-3.1-8b", RepoId = "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", DisplayName = "Llama 3.1 8B", Publisher = "Meta", Released = "2024-07", Architecture = "llama", NativeContext = 131072, TotalParamsB = 8.0, ActiveParamsB = 0, ParameterBillions = 8, Category = "General", Tasks = ModelTasks.Chat, CodingScore = 2, ChatScore = 3, ReasoningScore = 2, ThaiScore = 2, VisionScore = 0, Stars = 2, IsLegacy = true, FileName = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", ApproxSizeBytes = 4920739232, RequiredVramMB = 6144, Url = "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", Description = "2024 workhorse. Runs on every backend, including the in-process one.", Quants = { Q("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", 4920739232) } },
        new() { Id = "llama-3.2-3b", RepoId = "bartowski/Llama-3.2-3B-Instruct-GGUF", DisplayName = "Llama 3.2 3B", Publisher = "Meta", Released = "2024-09", Architecture = "llama", NativeContext = 131072, TotalParamsB = 3.2, ActiveParamsB = 0, ParameterBillions = 3, Category = "General", Tasks = ModelTasks.Chat, CodingScore = 1, ChatScore = 2, ReasoningScore = 1, ThaiScore = 1, VisionScore = 0, Stars = 2, IsLegacy = true, FileName = "Llama-3.2-3B-Instruct-Q4_K_M.gguf", ApproxSizeBytes = 2019377696, RequiredVramMB = 3072, Url = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF", Description = "2024 compact model, fast on CPU.", Quants = { Q("Llama-3.2-3B-Instruct-Q4_K_M.gguf", 2019377696) } },
    };

    private static QuantOption Q(string fileName, long sizeBytes) => new(RecommendedModel.QuantNameOf(fileName), fileName, sizeBytes);

    /// <summary>
    /// Repos that answer 401/403 to a download without a token of an account that accepted the license
    /// (HF reports them as "gated"). The UI explains that instead of a bare "401 Unauthorized".
    /// </summary>
    private static readonly HashSet<string> GatedRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        // None of the current catalog is gated (the Gemma 3 QAT repos were). Keep the set: the download error
        // explanation and the --network harness both read it.
    };

    public static bool IsGatedRepo(string repoId) => GatedRepos.Contains(repoId);

    /// <summary>The full curated catalog (for the catalog harness).</summary>
    public static IReadOnlyList<RecommendedModel> Catalog => CuratedCodingModels;

    // ─── Model Sources ────────────────────────────────────────────────

    public static readonly List<ModelSource> PopularSources = new()
    {
        new() { Name = "HuggingFace", Url = "https://huggingface.co/models?library=gguf&sort=trending", Description = "Largest model hub. Search for any GGUF model.", Icon = "" },
        new() { Name = "unsloth", Url = "https://huggingface.co/unsloth", Description = "GGUF builds of new models on release day, many quant sizes.", Icon = "" },
        new() { Name = "bartowski", Url = "https://huggingface.co/bartowski", Description = "High-quality GGUF quantizations of popular models. Updated frequently.", Icon = "" },
        new() { Name = "ggml-org", Url = "https://huggingface.co/ggml-org", Description = "Official GGUFs from the llama.cpp team.", Icon = "" },
        new() { Name = "lmstudio-community", Url = "https://huggingface.co/lmstudio-community", Description = "Models tested for LM Studio compatibility. Works great with LLamaSharp too.", Icon = "" },
        new() { Name = "Ollama Library", Url = "https://ollama.com/library", Description = "Browse Ollama's model library. Use with the Ollama provider.", Icon = "" },
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
