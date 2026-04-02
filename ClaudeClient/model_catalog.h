#pragma once
#include <windows.h>
#include <string>
#include <vector>

// ═══════════════════════════════════════════════════════════════════════
// Curated Model Catalog — Popular & Proven Models
// ═══════════════════════════════════════════════════════════════════════

struct CatalogModel {
    const wchar_t* name;        // Display name
    const wchar_t* ollamaId;    // Ollama pull ID (e.g. "llama3.2:3b")
    const wchar_t* sizeStr;     // Human-readable size
    const wchar_t* category;    // "General", "Code", "Small", "Vision"
    const wchar_t* description; // Short description
};

// Curated list of proven, popular models from Ollama/HuggingFace
static const CatalogModel g_catalogModels[] = {
    // ── General Purpose (Top Tier) ──
    { L"Llama 3.3 70B",      L"llama3.3:70b",        L"~40 GB",  L"General",
      L"Meta's flagship open model. Excellent reasoning and instruction following." },
    { L"Llama 3.2 3B",       L"llama3.2:3b",         L"~2 GB",   L"General",
      L"Fast and capable small model by Meta. Great for quick tasks." },
    { L"Llama 3.1 8B",       L"llama3.1:8b",         L"~4.7 GB", L"General",
      L"Solid mid-range model by Meta. Good balance of speed and quality." },
    { L"Gemma 3 12B",        L"gemma3:12b",           L"~8 GB",   L"General",
      L"Google's latest open model. Strong multilingual and reasoning." },
    { L"Gemma 3 4B",         L"gemma3:4b",            L"~3 GB",   L"General",
      L"Google's compact model. Fast with good quality." },
    { L"Mistral 7B",         L"mistral:7b",           L"~4.1 GB", L"General",
      L"Mistral AI's efficient 7B model. Great speed-to-quality ratio." },
    { L"Mixtral 8x7B",       L"mixtral:8x7b",         L"~26 GB",  L"General",
      L"Mistral's mixture-of-experts model. Near GPT-4 quality." },
    { L"Phi-4 14B",          L"phi4:14b",             L"~9 GB",   L"General",
      L"Microsoft's latest small language model. Strong reasoning." },
    { L"Qwen 2.5 7B",        L"qwen2.5:7b",           L"~4.7 GB", L"General",
      L"Alibaba's multilingual model. Excellent for Asian languages." },
    { L"Qwen 2.5 14B",       L"qwen2.5:14b",          L"~9 GB",   L"General",
      L"Alibaba's larger model. Strong coding and math capabilities." },

    // ── Code Specialists ──
    { L"Qwen 2.5 Coder 7B",  L"qwen2.5-coder:7b",    L"~4.7 GB", L"Code",
      L"Top open-source coding model. Rivals GPT-4 on code tasks." },
    { L"Qwen 2.5 Coder 14B", L"qwen2.5-coder:14b",   L"~9 GB",   L"Code",
      L"Larger coding variant. Best open code model at this size." },
    { L"CodeLlama 7B",       L"codellama:7b",         L"~3.8 GB", L"Code",
      L"Meta's code-specialized Llama. Good for code completion." },
    { L"DeepSeek Coder V2 16B", L"deepseek-coder-v2:16b", L"~8.9 GB", L"Code",
      L"DeepSeek's coding model. Strong at complex programming tasks." },
    { L"Starcoder2 7B",      L"starcoder2:7b",        L"~4 GB",   L"Code",
      L"BigCode's coding model. Trained on The Stack v2." },

    // ── Small & Fast (< 3B) ──
    { L"Llama 3.2 1B",       L"llama3.2:1b",         L"~1.3 GB", L"Small",
      L"Ultra-light Meta model. Good for simple tasks and constrained hardware." },
    { L"Phi-3 Mini 3.8B",    L"phi3:mini",           L"~2.3 GB", L"Small",
      L"Microsoft's compact model. Surprisingly capable for its size." },
    { L"Gemma 2 2B",         L"gemma2:2b",            L"~1.6 GB", L"Small",
      L"Google's tiny model. Fast inference with decent quality." },
    { L"TinyLlama 1.1B",     L"tinyllama:1.1b",      L"~637 MB", L"Small",
      L"Smallest practical chat model. Near-instant responses." },
    { L"Qwen 2.5 0.5B",      L"qwen2.5:0.5b",        L"~397 MB", L"Small",
      L"Alibaba's tiny model. Minimal resource usage." },

    // ── Vision (Multimodal) ──
    { L"Llama 3.2 Vision 11B", L"llama3.2-vision:11b", L"~7.9 GB", L"Vision",
      L"Meta's multimodal model. Understands images + text." },
    { L"LLaVA 7B",           L"llava:7b",             L"~4.7 GB", L"Vision",
      L"Visual instruction model. Describe and analyze images." },
    { L"Gemma 3 12B",        L"gemma3:12b",           L"~8 GB",   L"Vision",
      L"Google's multimodal Gemma. Image understanding + text." },
};
static const int CATALOG_MODEL_COUNT = _countof(g_catalogModels);

// Resource IDs for catalog controls (added to main resource.h range)
#define IDC_CATALOG_LIST        1180
#define IDC_CATALOG_PULL        1181
#define IDC_CATALOG_DESC        1182
#define IDC_CATALOG_FILTER      1183
#define IDC_CATALOG_PROGRESS    1184
