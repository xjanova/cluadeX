namespace CluadeX.Models;

public enum AiProviderType
{
    Local,      // Existing LLamaSharp GGUF
    OpenAI,     // OpenAI API (GPT-4o, o1, o3, etc.)
    Anthropic,  // Anthropic API (Claude Sonnet, Opus, Haiku)
    Gemini,     // Google Gemini API
    Ollama,     // Local Ollama server
}
