#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include "claude_api.h"
#include "settings.h"

// Show session history dialog
void ShowSessionHistoryDialog(HWND hParent);

// Session management
struct SessionInfo {
    std::wstring filename;
    std::wstring name;
    std::wstring date;
    std::wstring model;
    int messageCount;
};

std::wstring GetSessionsDir();
bool SaveSession(const std::wstring& name, const std::vector<ChatMessage>& conversation,
    const AppSettings& settings);
bool LoadSession(const std::wstring& filename, std::vector<ChatMessage>& conversation,
    std::wstring& model);
bool DeleteSession(const std::wstring& filename);
std::vector<SessionInfo> ListSessions();
