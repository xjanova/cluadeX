namespace CluadeX.Models;

public enum AiProviderType
{
    Local,        // Existing LLamaSharp GGUF
    LlamaServer,  // llama.cpp server (supports Gemma 4, latest architectures)
    OpenAI,       // OpenAI API (GPT-4o, o1, o3, etc.)
    Anthropic,    // Anthropic API (Claude Sonnet, Opus, Haiku)
    Gemini,       // Google Gemini API
    Ollama,       // Local Ollama server
    ClaudeDev,    // The installed Claude Code CLI as a model — no API key, uses the user's existing login
    BrainX,       // The BrainX cowork room as a model — the request goes to whoever is in the room (or named)
    GeminiDev,    // The installed Gemini CLI as a model — no API key, uses the user's Google sign-in
    AntigravityDev, // Google's Antigravity CLI (agy) as a model — the Google sign-in the Gemini CLI no longer serves
    OpenAIDev,    // OpenAI's Codex CLI as a model — no API key, uses the user's ChatGPT sign-in

    // OpenAI-compatible Chat Completions services — one client (CompatibleProvider), one preset each
    // (CompatiblePresets): aggregators and multi-model clouds, then local servers and a custom endpoint.
    OpenRouter,
    Groq,
    DeepSeek,
    Mistral,
    XAI,
    Together,
    Fireworks,
    Cerebras,
    DeepInfra,
    HuggingFace,
    Novita,
    SambaNova,
    Nebius,
    Qwen,         // Alibaba Cloud Model Studio (DashScope international)
    Moonshot,     // Kimi
    Zhipu,        // Z.ai GLM
    AzureOpenAI,
    LMStudio,
    Vllm,
    OpenAICompatible, // any other OpenAI-compatible endpoint (base URL typed by the user)
}
