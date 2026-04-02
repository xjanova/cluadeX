#pragma once
#include <string>
#include <vector>
#include <functional>

struct ChatMessage {
    std::string role;    // "user", "assistant", or "system"
    std::string content;
};

struct ApiResponse {
    bool        success = false;
    std::string text;
    std::string error;
    int         inputTokens  = 0;
    int         outputTokens = 0;
};

using ApiCallback = std::function<void(const ApiResponse&)>;

class ClaudeAPI {
public:
    // Cloud (Anthropic API) — blocking call
    static ApiResponse SendChat(
        const std::wstring& apiKey,
        const std::wstring& model,
        int maxTokens,
        double temperature,
        const std::string& systemPrompt,
        const std::vector<ChatMessage>& messages
    );

    // Local (Ollama API) — blocking call
    static ApiResponse SendChatLocal(
        const std::wstring& endpoint,   // e.g. http://localhost:11434
        const std::wstring& model,
        int maxTokens,
        double temperature,
        const std::string& systemPrompt,
        const std::vector<ChatMessage>& messages
    );

    // Test cloud connection
    static ApiResponse TestConnection(
        const std::wstring& apiKey,
        const std::wstring& model
    );

    // Test local connection
    static ApiResponse TestConnectionLocal(
        const std::wstring& endpoint,
        const std::wstring& model
    );

    // List models from Ollama
    static std::vector<std::string> ListLocalModels(
        const std::wstring& endpoint
    );
};

// UTF-8 <-> UTF-16 helpers
std::string  WideToUtf8(const std::wstring& w);
std::wstring Utf8ToWide(const std::string& s);
