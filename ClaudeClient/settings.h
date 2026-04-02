#pragma once
#include <string>
#include <windows.h>

enum class ServerMode { Cloud, Local };

struct AppSettings {
    std::wstring apiKey;
    std::wstring model         = L"claude-sonnet-4-20250514";
    int          maxTokens     = 4096;
    double       temperature   = 1.0;
    std::wstring systemPrompt  = L"You are a helpful AI assistant.";
    ServerMode   serverMode    = ServerMode::Cloud;
    std::wstring localEndpoint = L"http://localhost:11434";  // Ollama default

    bool IsConfigured() const {
        if (serverMode == ServerMode::Local) return true;  // No key needed
        return !apiKey.empty();
    }
    bool IsLocal() const { return serverMode == ServerMode::Local; }
};

// Returns path like C:\Users\<user>\AppData\Roaming\CluadeX
std::wstring GetSettingsDir();
std::wstring GetSettingsPath();

bool LoadSettings(AppSettings& s);
bool SaveSettings(const AppSettings& s);
