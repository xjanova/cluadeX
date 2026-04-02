#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include <mutex>
#include "settings.h"
#include "claude_api.h"

// ── Shared application state (defined in main.cpp) ──
extern HINSTANCE   g_hInst;
extern HWND        g_hMainWnd;
extern HFONT       g_hFont;
extern HFONT       g_hFontMono;
extern AppSettings g_settings;
extern std::vector<ChatMessage> g_conversation;
extern bool        g_apiWorking;
extern std::mutex  g_apiMutex;

// Append text to the main chat window
void AppendChatText(const std::wstring& text);
void AppendChatLine(const std::wstring& prefix, const std::wstring& msg);
void SetMainStatus(const std::wstring& text);

// Run a shell command and capture output (blocking)
std::string RunCommand(const std::wstring& cmd);
