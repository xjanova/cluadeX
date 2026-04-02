//=============================================================================
// CluadeX — Native C++ Win32 GUI for AI Chat (Cloud + Local)
//=============================================================================
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shlobj.h>

#include <string>
#include <vector>
#include <thread>
#include <mutex>
#include <fstream>
#include <sstream>

#include <richedit.h>

#include "resource.h"
#include "version.h"
#include "settings.h"
#include "claude_api.h"
#include "app_state.h"
#include "theme.h"
#include "tools.h"
#include "git_ops.h"
#include "session.h"
#include "plugins.h"
#include "extras.h"
#include "model_catalog.h"

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

// ── Globals (extern in app_state.h) ──────────────────────────────────

HINSTANCE   g_hInst       = nullptr;
HWND        g_hMainWnd    = nullptr;
static HWND g_hChatEdit   = nullptr;   // Chat history
static HWND g_hInputEdit  = nullptr;   // User input
static HWND g_hSendBtn    = nullptr;   // Send button
static HWND g_hStatusBar  = nullptr;   // Status bar
static HWND g_hVoiceBtn   = nullptr;   // Voice button
HFONT       g_hFont       = nullptr;
HFONT       g_hFontMono   = nullptr;

AppSettings g_settings;
std::vector<ChatMessage> g_conversation;
bool        g_apiWorking  = false;
std::mutex  g_apiMutex;

static HFONT g_hFontBold   = nullptr;   // Bold font for titles
static HFONT g_hFontChat   = nullptr;   // Slightly larger mono for chat
static int   g_activeNav   = 0;         // Active sidebar nav item
static std::vector<std::wstring> g_recentSessions;  // Sidebar session list

static const wchar_t* APP_CLASS = L"CluadeXWnd";
static const wchar_t* APP_TITLE = L"CluadeX";

// Custom message for API response on UI thread
#define WM_API_DONE   (WM_USER + 1)
#define WM_API_STATUS (WM_USER + 2)

// Cloud models
static const wchar_t* CLOUD_MODELS[] = {
    L"claude-sonnet-4-20250514",
    L"claude-opus-4-0-20250115",
    L"claude-haiku-4-5-20251001",
    L"claude-sonnet-4-6",
    L"claude-opus-4-6",
};
static const int CLOUD_MODEL_COUNT = _countof(CLOUD_MODELS);

// ── Utility ──────────────────────────────────────────────────────────

static void SetStatus(const std::wstring& text) {
    if (g_hStatusBar)
        SendMessageW(g_hStatusBar, SB_SETTEXTW, 0, (LPARAM)text.c_str());
}

// Extern-accessible versions (app_state.h)
void SetMainStatus(const std::wstring& text) { SetStatus(text); }

// RichEdit helper: insert colored text at end
static void RichEditAppend(HWND hRich, const std::wstring& text,
    COLORREF color, bool bold = false)
{
    if (!hRich) return;

    // Move cursor to end
    CHARRANGE cr = { -1, -1 };
    SendMessageW(hRich, EM_EXSETSEL, 0, (LPARAM)&cr);

    // Set format
    CHARFORMAT2W cf = {};
    cf.cbSize = sizeof(cf);
    cf.dwMask = CFM_COLOR | CFM_BOLD;
    cf.crTextColor = color;
    if (bold) cf.dwEffects = CFE_BOLD;
    SendMessageW(hRich, EM_SETCHARFORMAT, SCF_SELECTION, (LPARAM)&cf);

    // Insert text
    SendMessageW(hRich, EM_REPLACESEL, FALSE, (LPARAM)text.c_str());

    // Scroll to end
    SendMessageW(hRich, WM_VSCROLL, SB_BOTTOM, 0);
}

static void AppendChat(const std::wstring& text) {
    RichEditAppend(g_hChatEdit, text, CLR_TEXT);
}

void AppendChatText(const std::wstring& text) { AppendChat(text); }

void AppendChatLine(const std::wstring& prefix, const std::wstring& msg) {
    // Color-code the prefix
    COLORREF prefixColor = CLR_TEXT;
    if (prefix.find(L"[You]") != std::wstring::npos)
        prefixColor = CLR_GREEN;
    else if (prefix.find(L"[Claude]") != std::wstring::npos)
        prefixColor = CLR_BLUE;
    else if (prefix.find(L"[Error]") != std::wstring::npos)
        prefixColor = CLR_RED;
    else if (prefix.find(L"[Voice]") != std::wstring::npos)
        prefixColor = CLR_MAUVE;
    else if (prefix.find(L"[File") != std::wstring::npos)
        prefixColor = CLR_YELLOW;
    else if (prefix.find(L"[Web") != std::wstring::npos)
        prefixColor = CLR_TEAL;
    else if (prefix.find(L"[Project") != std::wstring::npos)
        prefixColor = CLR_PEACH;

    RichEditAppend(g_hChatEdit, prefix, prefixColor, true);
    RichEditAppend(g_hChatEdit, msg + L"\r\n\r\n", CLR_TEXT);
}

// Shell command runner (used by multiple modules)
std::string RunCommand(const std::wstring& cmd) {
    SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, TRUE };
    HANDLE hReadPipe, hWritePipe;
    CreatePipe(&hReadPipe, &hWritePipe, &sa, 0);
    SetHandleInformation(hReadPipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.hStdOutput = hWritePipe;
    si.hStdError  = hWritePipe;
    si.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION pi = {};
    std::wstring cmdLine = L"cmd.exe /c " + cmd;

    BOOL ok = CreateProcessW(nullptr, &cmdLine[0], nullptr, nullptr,
        TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(hWritePipe);

    std::string output;
    if (ok) {
        char buf[4096];
        DWORD bytesRead;
        while (ReadFile(hReadPipe, buf, sizeof(buf) - 1, &bytesRead, nullptr) && bytesRead > 0) {
            buf[bytesRead] = 0;
            output += buf;
        }
        WaitForSingleObject(pi.hProcess, 30000);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    } else {
        output = "Failed to run command (error " + std::to_string(GetLastError()) + ")";
    }
    CloseHandle(hReadPipe);
    return output;
}

static void PopulateModelCombo(HWND hCombo, const std::wstring& current, bool isLocal = false) {
    SendMessageW(hCombo, CB_RESETCONTENT, 0, 0);
    int sel = 0;

    if (isLocal) {
        // Fetch models from Ollama
        auto models = ClaudeAPI::ListLocalModels(g_settings.localEndpoint);
        if (models.empty()) {
            // Fallback defaults
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"llama3.2:3b");
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"llama3.1:8b");
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"mistral:7b");
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"codellama:7b");
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)L"qwen2.5:7b");
        } else {
            for (int i = 0; i < (int)models.size(); i++) {
                std::wstring w = Utf8ToWide(models[i]);
                SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)w.c_str());
                if (current == w) sel = i;
            }
        }
    } else {
        for (int i = 0; i < CLOUD_MODEL_COUNT; i++) {
            SendMessageW(hCombo, CB_ADDSTRING, 0, (LPARAM)CLOUD_MODELS[i]);
            if (current == CLOUD_MODELS[i]) sel = i;
        }
    }
    SendMessageW(hCombo, CB_SETCURSEL, sel, 0);
}

static std::wstring GetComboText(HWND hCombo) {
    int idx = (int)SendMessageW(hCombo, CB_GETCURSEL, 0, 0);
    if (idx < 0) return L"";
    int len = (int)SendMessageW(hCombo, CB_GETLBTEXTLEN, idx, 0);
    std::wstring text(len + 1, 0);
    SendMessageW(hCombo, CB_GETLBTEXT, idx, (LPARAM)text.data());
    text.resize(len);
    return text;
}

static std::wstring GetDlgText(HWND hDlg, int id) {
    HWND hCtrl = GetDlgItem(hDlg, id);
    int len = GetWindowTextLengthW(hCtrl);
    if (len <= 0) return L"";
    std::wstring text(len + 1, 0);
    GetWindowTextW(hCtrl, &text[0], len + 1);
    text.resize(len);
    return text;
}

// ── API Call on Background Thread ────────────────────────────────────

struct ApiResult {
    ApiResponse response;
};

static ApiResult* g_pendingResult = nullptr;

static void DoApiCall() {
    std::string sysPrompt = WideToUtf8(g_settings.systemPrompt);

    // Copy conversation under lock to avoid race with UI thread
    std::vector<ChatMessage> convCopy;
    {
        std::lock_guard<std::mutex> lock(g_apiMutex);
        convCopy = g_conversation;
    }

    ApiResponse resp;
    if (g_settings.IsLocal()) {
        resp = ClaudeAPI::SendChatLocal(
            g_settings.localEndpoint,
            g_settings.model,
            g_settings.maxTokens,
            g_settings.temperature,
            sysPrompt,
            convCopy
        );
    } else {
        resp = ClaudeAPI::SendChat(
            g_settings.apiKey,
            g_settings.model,
            g_settings.maxTokens,
            g_settings.temperature,
            sysPrompt,
            convCopy
        );
    }

    auto* result = new ApiResult{ resp };

    {
        std::lock_guard<std::mutex> lock(g_apiMutex);
        g_pendingResult = result;
    }

    PostMessageW(g_hMainWnd, WM_API_DONE, 0, 0);
}

static void SendUserMessage() {
    if (g_apiWorking) return;
    if (!g_settings.IsConfigured()) {
        MessageBoxW(g_hMainWnd,
            L"Please configure your connection first.\n\n"
            L"Go to Settings menu to set up Cloud API key or Local server.",
            L"Not Configured", MB_ICONWARNING);
        return;
    }

    // Get input text
    int len = GetWindowTextLengthW(g_hInputEdit);
    if (len <= 0) return;

    std::wstring inputW(len + 1, 0);
    GetWindowTextW(g_hInputEdit, &inputW[0], len + 1);
    inputW.resize(len);

    std::string inputUtf8 = WideToUtf8(inputW);
    if (inputUtf8.empty()) return;

    // Display user message
    AppendChatLine(L"[You]\r\n", inputW);

    // Add to conversation
    g_conversation.push_back({ "user", inputUtf8 });

    // Clear input
    SetWindowTextW(g_hInputEdit, L"");

    // Disable send, show status
    g_apiWorking = true;
    EnableWindow(g_hSendBtn, FALSE);
    SetStatus(L"Sending to Claude...");

    // Launch background thread
    std::thread(DoApiCall).detach();
}

static void OnApiDone() {
    ApiResult* result = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_apiMutex);
        result = g_pendingResult;
        g_pendingResult = nullptr;
    }

    g_apiWorking = false;
    EnableWindow(g_hSendBtn, TRUE);
    SetFocus(g_hInputEdit);

    if (!result) return;

    if (result->response.success) {
        std::wstring textW = Utf8ToWide(result->response.text);
        AppendChatLine(L"[Claude]\r\n", textW);

        // Add to conversation history
        g_conversation.push_back({ "assistant", result->response.text });

        // Show token usage in status
        std::wstring mode = g_settings.IsLocal() ? L"LOCAL" : L"CLOUD";
        std::wstring status = L"[" + mode + L"]  Tokens: in="
            + std::to_wstring(result->response.inputTokens)
            + L" out=" + std::to_wstring(result->response.outputTokens);
        SetStatus(status);
    } else {
        std::wstring errW = Utf8ToWide(result->response.error);
        AppendChatLine(L"[Error]\r\n", errW);
        SetStatus(L"Error — see chat");
    }

    delete result;
}

// ── Setup Dialog ─────────────────────────────────────────────────────

// Helper: update UI based on server mode
static void UpdateModeUI(HWND hDlg, bool isLocal) {
    // Enable/disable API key (cloud only)
    EnableWindow(GetDlgItem(hDlg, IDC_API_KEY), !isLocal);
    // Enable/disable endpoint (local only)
    EnableWindow(GetDlgItem(hDlg, IDC_LOCAL_ENDPOINT), isLocal);
    EnableWindow(GetDlgItem(hDlg, IDC_REFRESH_MODELS), isLocal);

    // Refresh model list
    std::wstring currentModel = GetComboText(GetDlgItem(hDlg, IDC_MODEL));
    PopulateModelCombo(GetDlgItem(hDlg, IDC_MODEL), currentModel, isLocal);
}

static INT_PTR CALLBACK SetupDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        SendMessageW(hDlg, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        // Set mode radio buttons
        bool isLocal = g_settings.IsLocal();
        CheckRadioButton(hDlg, IDC_MODE_CLOUD, IDC_MODE_LOCAL,
            isLocal ? IDC_MODE_LOCAL : IDC_MODE_CLOUD);

        // Fill fields
        if (!g_settings.apiKey.empty())
            SetDlgItemTextW(hDlg, IDC_API_KEY, g_settings.apiKey.c_str());
        SetDlgItemTextW(hDlg, IDC_LOCAL_ENDPOINT, g_settings.localEndpoint.c_str());

        PopulateModelCombo(GetDlgItem(hDlg, IDC_MODEL), g_settings.model, isLocal);
        UpdateModeUI(hDlg, isLocal);

        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_MODE_CLOUD:
            UpdateModeUI(hDlg, false);
            return TRUE;
        case IDC_MODE_LOCAL:
            UpdateModeUI(hDlg, true);
            return TRUE;

        case IDC_REFRESH_MODELS: {
            std::wstring endpoint = GetDlgText(hDlg, IDC_LOCAL_ENDPOINT);
            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Fetching models...");
            PopulateModelCombo(GetDlgItem(hDlg, IDC_MODEL), g_settings.model, true);
            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Models refreshed.");
            return TRUE;
        }

        case IDC_TEST_CONN: {
            bool isLocal = IsDlgButtonChecked(hDlg, IDC_MODE_LOCAL) == BST_CHECKED;
            std::wstring model = GetComboText(GetDlgItem(hDlg, IDC_MODEL));

            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Testing...");
            EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), FALSE);

            if (isLocal) {
                std::wstring endpoint = GetDlgText(hDlg, IDC_LOCAL_ENDPOINT);
                std::thread([hDlg, endpoint, model]() {
                    ApiResponse resp = ClaudeAPI::TestConnectionLocal(endpoint, model);
                    PostMessageW(hDlg, WM_API_STATUS, resp.success ? 1 : 0,
                        (LPARAM)new std::wstring(
                            resp.success ? L"Local server connected!" : Utf8ToWide(resp.error)));
                }).detach();
            } else {
                std::wstring apiKey = GetDlgText(hDlg, IDC_API_KEY);
                if (apiKey.empty()) {
                    SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Enter an API key first.");
                    EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), TRUE);
                    return TRUE;
                }
                std::thread([hDlg, apiKey, model]() {
                    ApiResponse resp = ClaudeAPI::TestConnection(apiKey, model);
                    PostMessageW(hDlg, WM_API_STATUS, resp.success ? 1 : 0,
                        (LPARAM)new std::wstring(
                            resp.success ? L"Cloud API connected!" : Utf8ToWide(resp.error)));
                }).detach();
            }
            return TRUE;
        }

        case IDC_SAVE: {
            bool isLocal = IsDlgButtonChecked(hDlg, IDC_MODE_LOCAL) == BST_CHECKED;

            if (!isLocal) {
                std::wstring apiKey = GetDlgText(hDlg, IDC_API_KEY);
                if (apiKey.empty()) {
                    MessageBoxW(hDlg, L"API Key is required for Cloud mode.", L"Validation", MB_ICONWARNING);
                    return TRUE;
                }
                g_settings.apiKey = apiKey;
            }

            g_settings.serverMode = isLocal ? ServerMode::Local : ServerMode::Cloud;
            g_settings.localEndpoint = GetDlgText(hDlg, IDC_LOCAL_ENDPOINT);
            g_settings.model = GetComboText(GetDlgItem(hDlg, IDC_MODEL));
            SaveSettings(g_settings);

            EndDialog(hDlg, IDOK);
            return TRUE;
        }

        case IDCANCEL:
            EndDialog(hDlg, IDCANCEL);
            return TRUE;
        }
        break;

    case WM_API_STATUS: {
        EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), TRUE);
        auto* msg_text = reinterpret_cast<std::wstring*>(lp);
        SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, msg_text->c_str());
        if (wp == 1) {
            SetDlgItemTextW(hDlg, IDC_STATUS,
                L"Connection successful! Click 'Save & Start' to begin.");
        }
        delete msg_text;
        return TRUE;
    }
    }
    return FALSE;
}

// ── Settings Dialog ──────────────────────────────────────────────────

static INT_PTR CALLBACK SettingsDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        bool isLocal = g_settings.IsLocal();
        CheckRadioButton(hDlg, IDC_MODE_CLOUD, IDC_MODE_LOCAL,
            isLocal ? IDC_MODE_LOCAL : IDC_MODE_CLOUD);

        SetDlgItemTextW(hDlg, IDC_API_KEY, g_settings.apiKey.c_str());
        SetDlgItemTextW(hDlg, IDC_LOCAL_ENDPOINT, g_settings.localEndpoint.c_str());
        PopulateModelCombo(GetDlgItem(hDlg, IDC_MODEL), g_settings.model, isLocal);
        SetDlgItemInt(hDlg, IDC_MAX_TOKENS, g_settings.maxTokens, FALSE);

        wchar_t tempBuf[32];
        swprintf_s(tempBuf, L"%.1f", g_settings.temperature);
        SetDlgItemTextW(hDlg, IDC_TEMPERATURE, tempBuf);

        SetDlgItemTextW(hDlg, IDC_SYSTEM_PROMPT, g_settings.systemPrompt.c_str());
        UpdateModeUI(hDlg, isLocal);
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_MODE_CLOUD:
            UpdateModeUI(hDlg, false);
            return TRUE;
        case IDC_MODE_LOCAL:
            UpdateModeUI(hDlg, true);
            return TRUE;

        case IDC_REFRESH_MODELS: {
            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Fetching models...");
            PopulateModelCombo(GetDlgItem(hDlg, IDC_MODEL), g_settings.model, true);
            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Models refreshed.");
            return TRUE;
        }

        case IDC_TEST_CONN: {
            bool isLocal = IsDlgButtonChecked(hDlg, IDC_MODE_LOCAL) == BST_CHECKED;
            std::wstring model = GetComboText(GetDlgItem(hDlg, IDC_MODEL));

            SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Testing...");
            EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), FALSE);

            if (isLocal) {
                std::wstring endpoint = GetDlgText(hDlg, IDC_LOCAL_ENDPOINT);
                std::thread([hDlg, endpoint, model]() {
                    ApiResponse resp = ClaudeAPI::TestConnectionLocal(endpoint, model);
                    PostMessageW(hDlg, WM_API_STATUS, resp.success ? 1 : 0,
                        (LPARAM)new std::wstring(
                            resp.success ? L"Local connected!" : Utf8ToWide(resp.error)));
                }).detach();
            } else {
                std::wstring apiKey = GetDlgText(hDlg, IDC_API_KEY);
                if (apiKey.empty()) {
                    SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, L"Enter API key first.");
                    EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), TRUE);
                    return TRUE;
                }
                std::thread([hDlg, apiKey, model]() {
                    ApiResponse resp = ClaudeAPI::TestConnection(apiKey, model);
                    PostMessageW(hDlg, WM_API_STATUS, resp.success ? 1 : 0,
                        (LPARAM)new std::wstring(
                            resp.success ? L"Cloud connected!" : Utf8ToWide(resp.error)));
                }).detach();
            }
            return TRUE;
        }

        case IDC_SAVE: {
            bool isLocal = IsDlgButtonChecked(hDlg, IDC_MODE_LOCAL) == BST_CHECKED;

            if (!isLocal) {
                std::wstring apiKey = GetDlgText(hDlg, IDC_API_KEY);
                if (apiKey.empty()) {
                    MessageBoxW(hDlg, L"API Key is required for Cloud mode.", L"Validation", MB_ICONWARNING);
                    return TRUE;
                }
                g_settings.apiKey = apiKey;
            }

            g_settings.serverMode = isLocal ? ServerMode::Local : ServerMode::Cloud;
            g_settings.localEndpoint = GetDlgText(hDlg, IDC_LOCAL_ENDPOINT);
            g_settings.model = GetComboText(GetDlgItem(hDlg, IDC_MODEL));
            g_settings.maxTokens = GetDlgItemInt(hDlg, IDC_MAX_TOKENS, nullptr, FALSE);
            if (g_settings.maxTokens < 1) g_settings.maxTokens = 4096;

            std::wstring tempStr = GetDlgText(hDlg, IDC_TEMPERATURE);
            g_settings.temperature = _wtof(tempStr.c_str());
            if (g_settings.temperature < 0.0) g_settings.temperature = 0.0;
            if (g_settings.temperature > 2.0) g_settings.temperature = 2.0;

            g_settings.systemPrompt = GetDlgText(hDlg, IDC_SYSTEM_PROMPT);

            SaveSettings(g_settings);
            EndDialog(hDlg, IDOK);
            return TRUE;
        }

        case IDCANCEL:
            EndDialog(hDlg, IDCANCEL);
            return TRUE;
        }
        break;

    case WM_API_STATUS: {
        EnableWindow(GetDlgItem(hDlg, IDC_TEST_CONN), TRUE);
        auto* msg_text = reinterpret_cast<std::wstring*>(lp);
        SetDlgItemTextW(hDlg, IDC_SETUP_STATUS, msg_text->c_str());
        delete msg_text;
        return TRUE;
    }
    }
    return FALSE;
}

// ── Helper: Run ollama command and capture output ────────────────────

static std::string RunOllamaCmd(const std::wstring& args) {
    SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, TRUE };
    HANDLE hReadPipe, hWritePipe;
    CreatePipe(&hReadPipe, &hWritePipe, &sa, 0);
    SetHandleInformation(hReadPipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.hStdOutput = hWritePipe;
    si.hStdError  = hWritePipe;
    si.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION pi = {};
    std::wstring cmdLine = L"ollama " + args;

    BOOL ok = CreateProcessW(nullptr, &cmdLine[0], nullptr, nullptr,
        TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(hWritePipe);

    std::string output;
    if (ok) {
        char buf[4096];
        DWORD bytesRead;
        while (ReadFile(hReadPipe, buf, sizeof(buf) - 1, &bytesRead, nullptr) && bytesRead > 0) {
            buf[bytesRead] = 0;
            output += buf;
        }
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD exitCode = 0;
        GetExitCodeProcess(pi.hProcess, &exitCode);
        if (exitCode != 0 && output.empty())
            output = "Command failed with exit code " + std::to_string(exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    } else {
        output = "Failed to run ollama. Is it installed? (error " + std::to_string(GetLastError()) + ")";
    }
    CloseHandle(hReadPipe);
    return output;
}

// ── Model Manager Dialog ─────────────────────────────────────────────

static void RefreshModelList(HWND hDlg) {
    HWND hList = GetDlgItem(hDlg, IDC_MODEL_LIST);
    SendMessageW(hList, LB_RESETCONTENT, 0, 0);

    auto models = ClaudeAPI::ListLocalModels(g_settings.localEndpoint);
    if (models.empty()) {
        SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)L"(no models found)");
    } else {
        for (auto& m : models) {
            std::wstring w = Utf8ToWide(m);
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)w.c_str());
        }
    }
}

static void AppendModelStatus(HWND hDlg, const std::wstring& text) {
    HWND hStatus = GetDlgItem(hDlg, IDC_MODEL_STATUS);
    int len = GetWindowTextLengthW(hStatus);
    SendMessageW(hStatus, EM_SETSEL, len, len);
    SendMessageW(hStatus, EM_REPLACESEL, FALSE, (LPARAM)(text + L"\r\n").c_str());
    SendMessageW(hStatus, EM_SCROLLCARET, 0, 0);
}

// Populate catalog listbox with optional category filter
static void PopulateCatalog(HWND hDlg, const wchar_t* filter) {
    HWND hList = GetDlgItem(hDlg, IDC_CATALOG_LIST);
    SendMessageW(hList, LB_RESETCONTENT, 0, 0);

    bool showAll = (filter == nullptr || wcscmp(filter, L"All") == 0);

    for (int i = 0; i < CATALOG_MODEL_COUNT; i++) {
        if (!showAll && wcscmp(g_catalogModels[i].category, filter) != 0)
            continue;
        std::wstring entry = std::wstring(g_catalogModels[i].name)
            + L"  [" + g_catalogModels[i].sizeStr + L"]";
        int idx = (int)SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)entry.c_str());
        SendMessageW(hList, LB_SETITEMDATA, idx, (LPARAM)i);
    }
}

static INT_PTR CALLBACK ModelMgrDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        SendDlgItemMessageW(hDlg, IDC_MODEL_STATUS, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        // Populate quantization dropdown
        HWND hQuant = GetDlgItem(hDlg, IDC_HF_QUANT);
        const wchar_t* quants[] = {
            L"Q4_K_M (default)", L"Q8_0 (best quality)", L"Q4_K_S (smaller)",
            L"Q5_K_M", L"Q6_K", L"IQ3_M (tiny)", L"Q2_K (smallest)"
        };
        for (auto q : quants)
            SendMessageW(hQuant, CB_ADDSTRING, 0, (LPARAM)q);
        SendMessageW(hQuant, CB_SETCURSEL, 0, 0);

        // Default HF repo example
        SetDlgItemTextW(hDlg, IDC_HF_REPO, L"bartowski/Llama-3.2-3B-Instruct-GGUF");

        // Populate catalog filter
        HWND hFilter = GetDlgItem(hDlg, IDC_CATALOG_FILTER);
        const wchar_t* categories[] = { L"All", L"General", L"Code", L"Small", L"Vision" };
        for (auto c : categories)
            SendMessageW(hFilter, CB_ADDSTRING, 0, (LPARAM)c);
        SendMessageW(hFilter, CB_SETCURSEL, 0, 0);

        // Populate catalog list
        PopulateCatalog(hDlg, L"All");

        RefreshModelList(hDlg);
        AppendModelStatus(hDlg, L"Model Manager ready.");
        AppendModelStatus(hDlg, L"Ollama endpoint: " + g_settings.localEndpoint);
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {

        // ── Pull from Ollama Library ──
        case IDC_OLLAMA_PULL: {
            std::wstring name = GetDlgText(hDlg, IDC_OLLAMA_NAME);
            if (name.empty()) {
                AppendModelStatus(hDlg, L"[!] Enter a model name (e.g. llama3.2:3b)");
                return TRUE;
            }
            AppendModelStatus(hDlg, L"Pulling " + name + L" from Ollama library...");
            AppendModelStatus(hDlg, L"(This may take several minutes)");
            EnableWindow(GetDlgItem(hDlg, IDC_OLLAMA_PULL), FALSE);

            std::thread([hDlg, name]() {
                std::string result = RunOllamaCmd(L"pull " + name);
                PostMessageW(hDlg, WM_API_STATUS, 1,
                    (LPARAM)new std::wstring(Utf8ToWide(result)));
            }).detach();
            return TRUE;
        }

        // ── Pull from Hugging Face ──
        case IDC_HF_PULL: {
            std::wstring repo = GetDlgText(hDlg, IDC_HF_REPO);
            if (repo.empty()) {
                AppendModelStatus(hDlg, L"[!] Enter a HuggingFace repo (e.g. bartowski/Llama-3.2-3B-Instruct-GGUF)");
                return TRUE;
            }

            // Get quantization
            std::wstring quantFull = GetComboText(GetDlgItem(hDlg, IDC_HF_QUANT));
            std::wstring quant;
            size_t space = quantFull.find(L' ');
            quant = (space != std::wstring::npos) ? quantFull.substr(0, space) : quantFull;

            std::wstring fullName = L"hf.co/" + repo + L":" + quant;
            AppendModelStatus(hDlg, L"Pulling " + fullName + L" ...");
            AppendModelStatus(hDlg, L"(This may take a long time for large models)");
            EnableWindow(GetDlgItem(hDlg, IDC_HF_PULL), FALSE);

            std::thread([hDlg, fullName]() {
                std::string result = RunOllamaCmd(L"pull " + fullName);
                PostMessageW(hDlg, WM_API_STATUS, 2,
                    (LPARAM)new std::wstring(Utf8ToWide(result)));
            }).detach();
            return TRUE;
        }

        // ── Browse GGUF file ──
        case IDC_GGUF_BROWSE: {
            wchar_t filePath[MAX_PATH] = {};
            OPENFILENAMEW ofn = { sizeof(ofn) };
            ofn.hwndOwner  = hDlg;
            ofn.lpstrFilter = L"GGUF Files\0*.gguf\0All Files\0*.*\0";
            ofn.lpstrFile  = filePath;
            ofn.nMaxFile   = MAX_PATH;
            ofn.Flags      = OFN_FILEMUSTEXIST;
            if (GetOpenFileNameW(&ofn)) {
                SetDlgItemTextW(hDlg, IDC_GGUF_PATH, filePath);
                // Auto-fill name from filename
                std::wstring fp(filePath);
                size_t slash = fp.find_last_of(L"\\/");
                std::wstring fname = (slash != std::wstring::npos) ? fp.substr(slash + 1) : fp;
                size_t dot = fname.find_last_of(L'.');
                if (dot != std::wstring::npos) fname = fname.substr(0, dot);
                SetDlgItemTextW(hDlg, IDC_GGUF_NAME, fname.c_str());
            }
            return TRUE;
        }

        // ── Import GGUF ──
        case IDC_GGUF_IMPORT: {
            std::wstring ggufPath = GetDlgText(hDlg, IDC_GGUF_PATH);
            std::wstring modelName = GetDlgText(hDlg, IDC_GGUF_NAME);
            if (ggufPath.empty() || modelName.empty()) {
                AppendModelStatus(hDlg, L"[!] Select a GGUF file and enter a model name.");
                return TRUE;
            }

            AppendModelStatus(hDlg, L"Creating model '" + modelName + L"' from GGUF...");
            EnableWindow(GetDlgItem(hDlg, IDC_GGUF_IMPORT), FALSE);

            std::thread([hDlg, ggufPath, modelName]() {
                // Write Modelfile
                std::wstring modelfileDir = GetSettingsDir();
                std::wstring modelfilePath = modelfileDir + L"\\Modelfile";
                {
                    std::wofstream mf(modelfilePath);
                    mf << L"FROM " << ggufPath << L"\n";
                    mf.close();
                }

                std::wstring cmd = L"create " + modelName + L" -f \"" + modelfilePath + L"\"";
                std::string result = RunOllamaCmd(cmd);

                // Cleanup modelfile
                DeleteFileW(modelfilePath.c_str());

                PostMessageW(hDlg, WM_API_STATUS, 3,
                    (LPARAM)new std::wstring(Utf8ToWide(result)));
            }).detach();
            return TRUE;
        }

        // ── Delete Model ──
        case IDC_MODEL_DELETE: {
            HWND hList = GetDlgItem(hDlg, IDC_MODEL_LIST);
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel == LB_ERR) {
                AppendModelStatus(hDlg, L"[!] Select a model to delete.");
                return TRUE;
            }
            int len = (int)SendMessageW(hList, LB_GETTEXTLEN, sel, 0);
            std::wstring name(len + 1, 0);
            SendMessageW(hList, LB_GETTEXT, sel, (LPARAM)name.data());
            name.resize(len);

            if (MessageBoxW(hDlg, (L"Delete model '" + name + L"'?\n\nThis cannot be undone.").c_str(),
                L"Confirm Delete", MB_YESNO | MB_ICONWARNING) != IDYES) {
                return TRUE;
            }

            AppendModelStatus(hDlg, L"Deleting " + name + L"...");
            std::thread([hDlg, name]() {
                std::string result = RunOllamaCmd(L"rm " + name);
                PostMessageW(hDlg, WM_API_STATUS, 4,
                    (LPARAM)new std::wstring(Utf8ToWide(result)));
            }).detach();
            return TRUE;
        }

        // ── Catalog: filter changed ──
        case IDC_CATALOG_FILTER: {
            if (HIWORD(wp) == CBN_SELCHANGE) {
                std::wstring filter = GetComboText(GetDlgItem(hDlg, IDC_CATALOG_FILTER));
                PopulateCatalog(hDlg, filter.c_str());
            }
            return TRUE;
        }

        // ── Catalog: selection changed — show description ──
        case IDC_CATALOG_LIST: {
            if (HIWORD(wp) == LBN_SELCHANGE) {
                HWND hList = GetDlgItem(hDlg, IDC_CATALOG_LIST);
                int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
                if (sel != LB_ERR) {
                    int catIdx = (int)SendMessageW(hList, LB_GETITEMDATA, sel, 0);
                    if (catIdx >= 0 && catIdx < CATALOG_MODEL_COUNT) {
                        SetDlgItemTextW(hDlg, IDC_CATALOG_DESC,
                            g_catalogModels[catIdx].description);
                    }
                }
            }
            return TRUE;
        }

        // ── Catalog: install selected model ──
        case IDC_CATALOG_PULL: {
            HWND hList = GetDlgItem(hDlg, IDC_CATALOG_LIST);
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel == LB_ERR) {
                AppendModelStatus(hDlg, L"[!] Select a model from the catalog first.");
                return TRUE;
            }
            int catIdx = (int)SendMessageW(hList, LB_GETITEMDATA, sel, 0);
            if (catIdx < 0 || catIdx >= CATALOG_MODEL_COUNT) return TRUE;

            std::wstring ollamaId = g_catalogModels[catIdx].ollamaId;
            std::wstring name = g_catalogModels[catIdx].name;
            std::wstring size = g_catalogModels[catIdx].sizeStr;

            AppendModelStatus(hDlg, L"Installing " + name + L" (" + size + L")...");
            AppendModelStatus(hDlg, L"Pulling " + ollamaId + L" — this may take several minutes.");
            EnableWindow(GetDlgItem(hDlg, IDC_CATALOG_PULL), FALSE);

            std::thread([hDlg, ollamaId, name]() {
                std::string result = RunOllamaCmd(L"pull " + ollamaId);
                std::wstring msg = L"[" + name + L"] " + Utf8ToWide(result);
                PostMessageW(hDlg, WM_API_STATUS, 5,
                    (LPARAM)new std::wstring(msg));
            }).detach();
            return TRUE;
        }

        case IDCANCEL:
            EndDialog(hDlg, IDCANCEL);
            return TRUE;
        }
        break;

    case WM_API_STATUS: {
        auto* text = reinterpret_cast<std::wstring*>(lp);
        AppendModelStatus(hDlg, *text);

        // Re-enable buttons based on source
        switch (wp) {
        case 1: EnableWindow(GetDlgItem(hDlg, IDC_OLLAMA_PULL), TRUE); break;
        case 2: EnableWindow(GetDlgItem(hDlg, IDC_HF_PULL), TRUE); break;
        case 3: EnableWindow(GetDlgItem(hDlg, IDC_GGUF_IMPORT), TRUE); break;
        case 5: EnableWindow(GetDlgItem(hDlg, IDC_CATALOG_PULL), TRUE); break;
        }

        // Refresh model list after any operation
        RefreshModelList(hDlg);
        AppendModelStatus(hDlg, L"Model list refreshed.");
        delete text;
        return TRUE;
    }
    }
    return FALSE;
}

// ── Run Command Dialog ───────────────────────────────────────────────

static INT_PTR CALLBACK RunCmdDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG:
        Theme_ApplyDarkTitle(hDlg);
        SendDlgItemMessageW(hDlg, IDC_CMD_OUTPUT, WM_SETFONT,
            (WPARAM)g_hFontMono, TRUE);
        return TRUE;

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_CMD_RUN: {
            std::wstring cmd = GetDlgText(hDlg, IDC_CMD_INPUT);
            if (cmd.empty()) return TRUE;

            SetDlgItemTextW(hDlg, IDC_CMD_OUTPUT, L"Running...\r\n");
            EnableWindow(GetDlgItem(hDlg, IDC_CMD_RUN), FALSE);

            std::thread([hDlg, cmd]() {
                // Create pipe for stdout
                SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, TRUE };
                HANDLE hReadPipe, hWritePipe;
                CreatePipe(&hReadPipe, &hWritePipe, &sa, 0);
                SetHandleInformation(hReadPipe, HANDLE_FLAG_INHERIT, 0);

                STARTUPINFOW si = { sizeof(si) };
                si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
                si.hStdOutput = hWritePipe;
                si.hStdError  = hWritePipe;
                si.wShowWindow = SW_HIDE;

                PROCESS_INFORMATION pi = {};
                std::wstring cmdLine = L"cmd.exe /c " + cmd;

                BOOL ok = CreateProcessW(nullptr, &cmdLine[0], nullptr, nullptr,
                    TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);

                CloseHandle(hWritePipe);

                std::string output;
                if (ok) {
                    char buf[4096];
                    DWORD bytesRead;
                    while (ReadFile(hReadPipe, buf, sizeof(buf) - 1, &bytesRead, nullptr)
                           && bytesRead > 0) {
                        buf[bytesRead] = 0;
                        output += buf;
                    }
                    WaitForSingleObject(pi.hProcess, 10000);

                    DWORD exitCode = 0;
                    GetExitCodeProcess(pi.hProcess, &exitCode);
                    output += "\r\n[Exit code: " + std::to_string(exitCode) + "]";

                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                } else {
                    output = "Failed to run command (error " +
                             std::to_string(GetLastError()) + ")";
                }
                CloseHandle(hReadPipe);

                std::wstring* outW = new std::wstring(Utf8ToWide(output));
                PostMessageW(hDlg, WM_API_STATUS, 0, (LPARAM)outW);
            }).detach();

            return TRUE;
        }

        case IDCANCEL:
            EndDialog(hDlg, IDCANCEL);
            return TRUE;
        }
        break;

    case WM_API_STATUS: {
        EnableWindow(GetDlgItem(hDlg, IDC_CMD_RUN), TRUE);
        auto* out = reinterpret_cast<std::wstring*>(lp);
        SetDlgItemTextW(hDlg, IDC_CMD_OUTPUT, out->c_str());
        delete out;
        return TRUE;
    }

    case WM_SIZE: {
        RECT rc;
        GetClientRect(hDlg, &rc);
        int w = rc.right - rc.left;
        int h = rc.bottom - rc.top;
        // Resize output to fill
        MoveWindow(GetDlgItem(hDlg, IDC_CMD_OUTPUT), 10, 42, w - 20, h - 52, TRUE);
        MoveWindow(GetDlgItem(hDlg, IDC_CMD_INPUT), 56, 8, w - 126, 14, TRUE);
        MoveWindow(GetDlgItem(hDlg, IDC_CMD_RUN), w - 62, 7, 52, 16, TRUE);
        return TRUE;
    }
    }
    return FALSE;
}

// ── Menu ─────────────────────────────────────────────────────────────

static HMENU CreateAppMenu() {
    HMENU hMenu = CreateMenu();

    // File
    HMENU hFile = CreatePopupMenu();
    AppendMenuW(hFile, MF_STRING, IDM_FILE_CLEAR,     L"&Clear Chat");
    AppendMenuW(hFile, MF_STRING, IDM_FILE_EXPORT,    L"&Export Chat...");
    AppendMenuW(hFile, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(hFile, MF_STRING, IDM_FILE_EXIT,      L"E&xit");
    AppendMenuW(hMenu, MF_POPUP, (UINT_PTR)hFile,     L"&File");

    // Session
    HMENU hSession = CreatePopupMenu();
    AppendMenuW(hSession, MF_STRING, IDM_SESSION_SAVE, L"&Save Session...");
    AppendMenuW(hSession, MF_STRING, IDM_SESSION_LOAD, L"&Load / History...");
    AppendMenuW(hMenu, MF_POPUP, (UINT_PTR)hSession,  L"&Session");

    // Settings
    AppendMenuW(hMenu, MF_STRING, IDM_SETTINGS,        L"S&ettings");

    // Tools
    HMENU hTools = CreatePopupMenu();
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_MODELS,       L"&Model Manager...");
    AppendMenuW(hTools, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_FILEBROWSER,  L"&File Browser...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_CODESEARCH,   L"Code &Search...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_GIT,          L"&Git...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_PROJECTCTX,   L"Project Conte&xt...");
    AppendMenuW(hTools, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_WEBFETCH,     L"&Web Fetch...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_RUNCMD,       L"&Run Command...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_OPENFILE,     L"&Open File...");
    AppendMenuW(hTools, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_TASKS,        L"&Task Manager...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_PLUGINS,      L"&Plugin Manager...");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_PERMISSIONS,  L"Per&missions...");
    AppendMenuW(hTools, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_VOICE,        L"&Voice Input");
    AppendMenuW(hTools, MF_STRING, IDM_TOOLS_VIM,          L"Vi&m Mode");
    AppendMenuW(hMenu, MF_POPUP, (UINT_PTR)hTools,    L"&Tools");

    // Help
    HMENU hHelp = CreatePopupMenu();
    AppendMenuW(hHelp, MF_STRING, IDM_HELP_ABOUT,     L"&About");
    AppendMenuW(hMenu, MF_POPUP, (UINT_PTR)hHelp,     L"&Help");

    return hMenu;
}

// ── Open File & Send to Chat ─────────────────────────────────────────

static void DoOpenFile() {
    wchar_t filePath[MAX_PATH] = {};
    OPENFILENAMEW ofn = { sizeof(ofn) };
    ofn.hwndOwner  = g_hMainWnd;
    ofn.lpstrFilter = L"All Files\0*.*\0Text Files\0*.txt;*.md;*.cpp;*.h;*.py;*.js;*.ts\0";
    ofn.lpstrFile  = filePath;
    ofn.nMaxFile   = MAX_PATH;
    ofn.Flags      = OFN_FILEMUSTEXIST;

    if (!GetOpenFileNameW(&ofn)) return;

    // Read file
    std::ifstream file(filePath, std::ios::binary);
    if (!file.is_open()) {
        MessageBoxW(g_hMainWnd, L"Could not open file.", L"Error", MB_ICONERROR);
        return;
    }

    std::stringstream ss;
    ss << file.rdbuf();
    std::string content = ss.str();
    file.close();

    // Limit size
    if (content.size() > 100000) {
        content = content.substr(0, 100000) + "\n\n[... truncated at 100KB ...]";
    }

    std::wstring fileW(filePath);
    size_t slash = fileW.find_last_of(L"\\/");
    std::wstring fileName = (slash != std::wstring::npos)
        ? fileW.substr(slash + 1) : fileW;

    // Put in input box with context
    std::wstring inputText = L"Here is the file `" + fileName + L"`:\n\n"
        + Utf8ToWide(content);
    SetWindowTextW(g_hInputEdit, inputText.c_str());
    SetFocus(g_hInputEdit);
}

// ── Export Chat ──────────────────────────────────────────────────────

static void DoExportChat() {
    wchar_t filePath[MAX_PATH] = {};
    OPENFILENAMEW ofn = { sizeof(ofn) };
    ofn.hwndOwner   = g_hMainWnd;
    ofn.lpstrFilter = L"Text Files\0*.txt\0All Files\0*.*\0";
    ofn.lpstrFile   = filePath;
    ofn.nMaxFile    = MAX_PATH;
    ofn.Flags       = OFN_OVERWRITEPROMPT;
    ofn.lpstrDefExt = L"txt";

    if (!GetSaveFileNameW(&ofn)) return;

    int len = GetWindowTextLengthW(g_hChatEdit);
    std::wstring text(len + 1, 0);
    GetWindowTextW(g_hChatEdit, &text[0], len + 1);

    std::ofstream out(filePath);
    if (out.is_open()) {
        out << WideToUtf8(text);
        out.close();
        SetStatus(L"Chat exported successfully.");
    }
}

// ── Main Window ──────────────────────────────────────────────────────

static void LayoutMainWindow(HWND hWnd) {
    RECT rc;
    GetClientRect(hWnd, &rc);
    int w = rc.right;
    int h = rc.bottom;

    // Status bar auto-resizes
    SendMessageW(g_hStatusBar, WM_SIZE, 0, 0);
    RECT sbrc;
    GetWindowRect(g_hStatusBar, &sbrc);
    int sbHeight = sbrc.bottom - sbrc.top;

    int sw       = SIDEBAR_WIDTH;
    int margin   = 10;
    int inputH   = 36;
    int btnW     = 76;
    int contentX = sw;
    int contentW = w - sw;
    int inputY   = h - sbHeight - inputH - margin;
    int chatY    = margin;
    int chatH    = inputY - margin - chatY;

    // Chat area (right of sidebar)
    MoveWindow(g_hChatEdit, contentX + margin, chatY,
        contentW - margin * 2, chatH, TRUE);

    // Input bar
    MoveWindow(g_hInputEdit, contentX + margin, inputY,
        contentW - margin * 3 - btnW, inputH, TRUE);
    MoveWindow(g_hSendBtn, w - margin - btnW, inputY, btnW, inputH, TRUE);

    // ── Sidebar nav buttons (invisible, used for hit-testing) ──
    int navY = 82; // below title area
    int navH = 32;
    int navCount;
    Theme_GetNavItems(navCount);
    for (int i = 0; i < navCount; i++) {
        HWND hBtn = GetDlgItem(hWnd, IDC_SIDEBAR_NAV + i);
        if (hBtn) MoveWindow(hBtn, 4, navY + i * (navH + 2), sw - 8, navH, TRUE);
    }

    // New Chat button
    HWND hNewChat = GetDlgItem(hWnd, IDC_SIDEBAR_NEWCHAT);
    if (hNewChat) MoveWindow(hNewChat, 12, 52, sw - 24, 26, TRUE);

    InvalidateRect(hWnd, nullptr, FALSE);
}

static LRESULT CALLBACK MainWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CREATE: {
        // Apply dark title bar
        Theme_ApplyDarkTitle(hWnd);

        // ── Chat history (RichEdit) ──
        LoadLibraryW(L"Msftedit.dll");
        g_hChatEdit = CreateWindowExW(
            0, MSFTEDIT_CLASS, L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE
            | ES_READONLY | ES_AUTOVSCROLL,
            0, 0, 100, 100, hWnd, (HMENU)IDC_CHAT_HISTORY, g_hInst, nullptr);
        SendMessageW(g_hChatEdit, WM_SETFONT, (WPARAM)g_hFontChat, TRUE);
        SendMessageW(g_hChatEdit, EM_SETBKGNDCOLOR, 0, CLR_BASE);
        SendMessageW(g_hChatEdit, EM_SETLIMITTEXT, 0x7FFFFFFE, 0);
        // Set default text color
        {
            CHARFORMAT2W cf = {};
            cf.cbSize = sizeof(cf);
            cf.dwMask = CFM_COLOR;
            cf.crTextColor = CLR_TEXT;
            SendMessageW(g_hChatEdit, EM_SETCHARFORMAT, SCF_ALL, (LPARAM)&cf);
        }

        // ── Input edit ──
        g_hInputEdit = CreateWindowExW(
            0, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL | ES_MULTILINE
            | ES_WANTRETURN,
            0, 0, 100, 32, hWnd, (HMENU)IDC_INPUT, g_hInst, nullptr);
        SendMessageW(g_hInputEdit, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        // ── Send button (owner-draw) ──
        g_hSendBtn = CreateWindowExW(
            0, L"BUTTON", L"Send",
            WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
            0, 0, 70, 32, hWnd, (HMENU)IDC_SEND, g_hInst, nullptr);

        // ── Status bar ──
        g_hStatusBar = CreateWindowExW(
            0, STATUSCLASSNAMEW, L"Ready",
            WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
            0, 0, 0, 0, hWnd, (HMENU)IDC_STATUS, g_hInst, nullptr);
        SendMessageW(g_hStatusBar, WM_SETFONT, (WPARAM)g_hFont, TRUE);
        SendMessageW(g_hStatusBar, SB_SETBKCOLOR, 0, CLR_CRUST);

        // ── Sidebar nav buttons (owner-draw) ──
        {
            int navCount;
            const SidebarNavItem* navItems = Theme_GetNavItems(navCount);
            for (int i = 0; i < navCount; i++) {
                CreateWindowExW(0, L"BUTTON", navItems[i].label,
                    WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
                    0, 0, 10, 10, hWnd, (HMENU)(UINT_PTR)(IDC_SIDEBAR_NAV + i),
                    g_hInst, nullptr);
            }
        }

        // ── New Chat button ──
        CreateWindowExW(0, L"BUTTON", L"+ New Chat",
            WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
            0, 0, 10, 10, hWnd, (HMENU)IDC_SIDEBAR_NEWCHAT, g_hInst, nullptr);

        LayoutMainWindow(hWnd);

        // Load recent sessions for sidebar
        {
            auto sessions = ListSessions();
            for (size_t i = 0; i < sessions.size() && i < 8; i++) {
                g_recentSessions.push_back(sessions[i].name.empty()
                    ? sessions[i].filename : sessions[i].name);
            }
        }

        // Welcome message
        RichEditAppend(g_hChatEdit, L"CluadeX\r\n", CLR_BLUE, true);
        RichEditAppend(g_hChatEdit, L"AI Coding Assistant by Xman Studio\r\n\r\n", CLR_SUBTEXT0);
        RichEditAppend(g_hChatEdit, L"Supports Cloud (Anthropic API) and Local (Ollama) modes.\r\n", CLR_SUBTEXT0);
        RichEditAppend(g_hChatEdit, L"Type a message and press Enter to chat.\r\n\r\n", CLR_SUBTEXT0);

        SetFocus(g_hInputEdit);
        return 0;
    }

    case WM_SIZE:
        LayoutMainWindow(hWnd);
        return 0;

    case WM_ERASEBKGND: {
        HDC hdc = (HDC)wp;
        RECT rc;
        GetClientRect(hWnd, &rc);
        // Sidebar
        RECT sideRc = { 0, 0, SIDEBAR_WIDTH, rc.bottom };
        Theme_FillRect(hdc, sideRc, CLR_MANTLE);
        // Content area
        RECT contentRc = { SIDEBAR_WIDTH, 0, rc.right, rc.bottom };
        Theme_FillRect(hdc, contentRc, CLR_CRUST);
        return 1;
    }

    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hWnd, &ps);
        RECT rc;
        GetClientRect(hWnd, &rc);
        RECT sideRc = { 0, 0, SIDEBAR_WIDTH, rc.bottom };
        // Paint sidebar title and sections
        Theme_PaintSidebar(hdc, sideRc, g_hFont, g_hFontBold,
            g_activeNav, g_recentSessions);
        EndPaint(hWnd, &ps);
        return 0;
    }

    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:
    case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg, CLR_SURFACE0);

    case WM_DRAWITEM: {
        LPDRAWITEMSTRUCT dis = (LPDRAWITEMSTRUCT)lp;
        int ctrlId = (int)dis->CtlID;

        // New Chat button (primary blue)
        if (ctrlId == IDC_SIDEBAR_NEWCHAT) {
            Theme_DrawButton(dis, true);
            return TRUE;
        }
        // Send button (primary blue)
        if (ctrlId == IDC_SEND) {
            Theme_DrawButton(dis, true);
            return TRUE;
        }
        // Sidebar nav buttons
        int navCount;
        Theme_GetNavItems(navCount);
        if (ctrlId >= IDC_SIDEBAR_NAV && ctrlId < IDC_SIDEBAR_NAV + navCount) {
            int navIdx = ctrlId - IDC_SIDEBAR_NAV;
            bool isActive = (navIdx == g_activeNav);

            RECT rc2 = dis->rcItem;
            COLORREF bg = isActive ? CLR_SURFACE0 : CLR_MANTLE;
            COLORREF textClr = isActive ? CLR_TEXT : CLR_SUBTEXT0;
            bool hover = (dis->itemState & ODS_SELECTED);
            if (hover) bg = CLR_SURFACE1;

            Theme_DrawRoundRect(dis->hDC, rc2, bg, bg, 6);

            // Active accent bar
            if (isActive) {
                RECT accent = { rc2.left, rc2.top + 6, rc2.left + 3, rc2.bottom - 6 };
                Theme_DrawRoundRect(dis->hDC, accent, CLR_BLUE, CLR_BLUE, 2);
            }

            // Text
            RECT textRc = { rc2.left + 16, rc2.top, rc2.right - 8, rc2.bottom };
            const SidebarNavItem* items = Theme_GetNavItems(navCount);
            std::wstring label = items[navIdx].label;
            Theme_DrawText(dis->hDC, textRc, label,
                isActive ? CLR_BLUE : textClr, g_hFont,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE);

            return TRUE;
        }
        break;
    }

    case WM_SETFOCUS:
        SetFocus(g_hInputEdit);
        return 0;

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_SEND:
            SendUserMessage();
            return 0;

        // ── Sidebar New Chat ──
        case IDC_SIDEBAR_NEWCHAT:
            SetWindowTextW(g_hChatEdit, L"");
            g_conversation.clear();
            SetStatus(L"New chat started.");
            RichEditAppend(g_hChatEdit, L"New conversation started.\r\n\r\n", CLR_SUBTEXT0);
            return 0;

        // ── Sidebar nav buttons ──
        // Nav order: Chat(0), Files(1), Search(2), Git(3), WebFetch(4),
        //            Plugins(5), Permissions(6), Tasks(7), Settings(8)
        default: {
            int id = LOWORD(wp);
            int navCount2;
            Theme_GetNavItems(navCount2);
            if (id >= IDC_SIDEBAR_NAV && id < IDC_SIDEBAR_NAV + navCount2) {
                int idx = id - IDC_SIDEBAR_NAV;
                g_activeNav = idx;
                InvalidateRect(hWnd, nullptr, TRUE);

                switch (idx) {
                case 0: SetFocus(g_hInputEdit); break; // Chat
                case 1: ShowFileBrowserDialog(hWnd); break;
                case 2: ShowCodeSearchDialog(hWnd); break;
                case 3: ShowGitDialog(hWnd); break;
                case 4: ShowWebFetchDialog(hWnd); break;
                case 5: ShowPluginManagerDialog(hWnd); break;
                case 6: ShowPermissionsDialog(hWnd); break;
                case 7: ShowTaskManagerDialog(hWnd); break;
                case 8: DialogBoxW(g_hInst, MAKEINTRESOURCEW(IDD_SETTINGS), hWnd, SettingsDlgProc); break;
                }
                g_activeNav = 0; // Return to chat after dialog closes
                InvalidateRect(hWnd, nullptr, TRUE);
                return 0;
            }
            break;
        }

        case IDM_FILE_CLEAR:
            SetWindowTextW(g_hChatEdit, L"");
            g_conversation.clear();
            SetStatus(L"Chat cleared.");
            return 0;

        case IDM_FILE_EXPORT:
            DoExportChat();
            return 0;

        case IDM_FILE_EXIT:
            PostMessageW(hWnd, WM_CLOSE, 0, 0);
            return 0;

        case IDM_SETTINGS:
            DialogBoxW(g_hInst, MAKEINTRESOURCEW(IDD_SETTINGS), hWnd, SettingsDlgProc);
            return 0;

        case IDM_SESSION_SAVE: {
            wchar_t name[256] = {};
            // Simple input: use the current model as default name
            wcscpy_s(name, L"chat");
            if (g_conversation.empty()) {
                MessageBoxW(hWnd, L"No messages to save.", L"Session", MB_ICONINFORMATION);
                return 0;
            }
            // Quick name input via message box workaround: use first message
            std::wstring autoName = L"session";
            if (!g_conversation.empty()) {
                autoName = Utf8ToWide(g_conversation[0].content.substr(0, 30));
                for (auto& c : autoName)
                    if (c == L'\n' || c == L'\r' || c == L'"') c = L' ';
            }
            if (SaveSession(autoName, g_conversation, g_settings)) {
                SetStatus(L"Session saved: " + autoName);
            }
            return 0;
        }

        case IDM_SESSION_LOAD:
            ShowSessionHistoryDialog(hWnd);
            return 0;

        case IDM_TOOLS_MODELS:
            DialogBoxW(g_hInst, MAKEINTRESOURCEW(IDD_MODELMANAGER), hWnd, ModelMgrDlgProc);
            return 0;

        case IDM_TOOLS_FILEBROWSER:
            ShowFileBrowserDialog(hWnd);
            return 0;

        case IDM_TOOLS_CODESEARCH:
            ShowCodeSearchDialog(hWnd);
            return 0;

        case IDM_TOOLS_GIT:
            ShowGitDialog(hWnd);
            return 0;

        case IDM_TOOLS_PROJECTCTX:
            ShowProjectContextDialog(hWnd);
            return 0;

        case IDM_TOOLS_WEBFETCH:
            ShowWebFetchDialog(hWnd);
            return 0;

        case IDM_TOOLS_RUNCMD:
            DialogBoxW(g_hInst, MAKEINTRESOURCEW(IDD_RUNCMD), hWnd, RunCmdDlgProc);
            return 0;

        case IDM_TOOLS_OPENFILE:
            DoOpenFile();
            return 0;

        case IDM_TOOLS_TASKS:
            ShowTaskManagerDialog(hWnd);
            return 0;

        case IDM_TOOLS_PLUGINS:
            ShowPluginManagerDialog(hWnd);
            return 0;

        case IDM_TOOLS_PERMISSIONS:
            ShowPermissionsDialog(hWnd);
            return 0;

        case IDM_TOOLS_VOICE:
            if (IsRecording()) {
                std::wstring wavPath;
                if (StopVoiceRecording(wavPath)) {
                    AppendChatLine(L"[Voice]\r\n", L"Recording saved: " + wavPath);
                    SetStatus(L"Recording stopped.");
                }
            } else {
                if (StartVoiceRecording()) {
                    SetStatus(L"Recording... (click Voice Input again to stop)");
                } else {
                    MessageBoxW(hWnd, L"Failed to start recording.\nCheck microphone.",
                        L"Voice", MB_ICONERROR);
                }
            }
            return 0;

        case IDM_TOOLS_VIM:
            if (IsVimModeEnabled()) {
                DisableVimMode(g_hInputEdit);
            } else {
                EnableVimMode(g_hInputEdit);
            }
            return 0;

        case IDM_HELP_ABOUT: {
            std::wstring aboutText =
                std::wstring(CLUADEX_APP_NAME_W) + L" v" + CLUADEX_VERSION_WSTR + L"\n\n"
                L"Native C++ Win32 AI Coding Assistant\n\n"
                L"Cloud Mode: Anthropic API (Claude)\n"
                L"Local Mode: Ollama (LLaMA, Mistral, etc.)\n\n" +
                CLUADEX_COPYRIGHT_W + L"\n" +
                CLUADEX_WEBSITE_W + L"\n\n"
                L"Licensed under the Xman Studio Proprietary License.\n"
                L"All rights reserved.";
            MessageBoxW(hWnd, aboutText.c_str(),
                (std::wstring(L"About ") + CLUADEX_APP_NAME_W).c_str(),
                MB_ICONINFORMATION);
        }
            return 0;
        }
        break;

    case WM_API_DONE:
        OnApiDone();
        return 0;

    case WM_GETMINMAXINFO: {
        auto* mmi = reinterpret_cast<MINMAXINFO*>(lp);
        mmi->ptMinTrackSize = { SIDEBAR_WIDTH + 400, 500 };
        return 0;
    }

    case WM_CLOSE:
        if (g_apiWorking) {
            if (MessageBoxW(hWnd, L"An API call is in progress. Exit anyway?",
                L"Confirm Exit", MB_YESNO | MB_ICONQUESTION) != IDYES) {
                return 0;
            }
        }
        DestroyWindow(hWnd);
        return 0;

    case WM_DESTROY:
        Theme_Cleanup();
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hWnd, msg, wp, lp);
}

// Subclass input edit to handle Ctrl+Enter
static WNDPROC g_origInputProc = nullptr;

static LRESULT CALLBACK InputSubclassProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_KEYDOWN) {
        // Ctrl+Enter or just Enter (single line mode) => send
        if (wp == VK_RETURN && (GetKeyState(VK_CONTROL) & 0x8000)) {
            SendUserMessage();
            return 0;
        }
        // Plain Enter also sends (since we have a small input box)
        if (wp == VK_RETURN && !(GetKeyState(VK_SHIFT) & 0x8000)) {
            SendUserMessage();
            return 0;
        }
    }
    return CallWindowProcW(g_origInputProc, hWnd, msg, wp, lp);
}

// ── WinMain ──────────────────────────────────────────────────────────

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, LPWSTR, int nCmdShow) {
    g_hInst = hInst;

    // Init dark theme (must be before window creation)
    Theme_Init();

    // Init common controls
    INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_BAR_CLASSES | ICC_STANDARD_CLASSES };
    InitCommonControlsEx(&icc);

    // Create fonts
    g_hFont = CreateFontW(
        -14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");

    g_hFontBold = CreateFontW(
        -16, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");

    g_hFontMono = CreateFontW(
        -13, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, FIXED_PITCH, L"Cascadia Code");

    g_hFontChat = CreateFontW(
        -14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, FIXED_PITCH, L"Cascadia Code");

    // Load settings
    LoadSettings(g_settings);

    // Register window class
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc   = MainWndProc;
    wc.hInstance      = hInst;
    wc.hCursor        = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground  = CreateSolidBrush(CLR_CRUST);
    wc.lpszClassName  = APP_CLASS;
    wc.hIcon          = LoadIconW(nullptr, IDI_APPLICATION);
    wc.hIconSm        = LoadIconW(nullptr, IDI_APPLICATION);
    RegisterClassExW(&wc);

    // Create main window
    g_hMainWnd = CreateWindowExW(
        0, APP_CLASS, APP_TITLE,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1100, 700,
        nullptr, CreateAppMenu(), hInst, nullptr);

    if (!g_hMainWnd) return 1;

    // Subclass input for Enter key
    g_origInputProc = (WNDPROC)SetWindowLongPtrW(
        g_hInputEdit, GWLP_WNDPROC, (LONG_PTR)InputSubclassProc);

    ShowWindow(g_hMainWnd, nCmdShow);
    UpdateWindow(g_hMainWnd);

    // Show setup dialog on first run
    if (!g_settings.IsConfigured()) {
        INT_PTR result = DialogBoxW(hInst, MAKEINTRESOURCEW(IDD_SETUP),
            g_hMainWnd, SetupDlgProc);
        if (result != IDOK) {
            // User cancelled setup — still allow browsing, just can't chat
            SetStatus(L"Not configured — go to Settings to add your API key.");
        } else {
            SetStatus(L"Ready — type a message to start chatting with Claude.");
        }
    } else {
        std::wstring mode = g_settings.IsLocal() ? L"LOCAL" : L"CLOUD";
        SetStatus(L"Ready [" + mode + L"] — model: " + g_settings.model);
    }

    // Message loop
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        // Handle dialog messages
        if (IsDialogMessageW(g_hMainWnd, &msg)) continue;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    // Cleanup
    DeleteObject(g_hFont);
    DeleteObject(g_hFontBold);
    DeleteObject(g_hFontMono);
    DeleteObject(g_hFontChat);

    return (int)msg.wParam;
}
