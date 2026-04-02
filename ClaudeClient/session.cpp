#include "session.h"
#include "app_state.h"
#include "theme.h"
#include "resource.h"
#include <fstream>
#include <sstream>
#include <ctime>
#include <algorithm>

// ── Session Directory ────────────────────────────────────────────────

std::wstring GetSessionsDir() {
    return GetSettingsDir() + L"\\sessions";
}

// ── Save Session ─────────────────────────────────────────────────────

static std::string EscJ(const std::string& s) {
    std::string out;
    for (char c : s) {
        switch (c) {
        case '\\': out += "\\\\"; break;
        case '"': out += "\\\""; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default: out += c; break;
        }
    }
    return out;
}

bool SaveSession(const std::wstring& name, const std::vector<ChatMessage>& conversation,
    const AppSettings& settings)
{
    std::wstring dir = GetSessionsDir();
    CreateDirectoryW(GetSettingsDir().c_str(), nullptr);
    CreateDirectoryW(dir.c_str(), nullptr);

    // Generate filename with timestamp
    time_t now = time(nullptr);
    struct tm t;
    localtime_s(&t, &now);
    wchar_t ts[64];
    wcsftime(ts, 64, L"%Y%m%d_%H%M%S", &t);

    std::wstring filename = std::wstring(ts) + L"_" + name + L".json";
    // Sanitize filename
    for (auto& c : filename) {
        if (c == L' ' || c == L'/' || c == L'\\' || c == L':' || c == L'*' || c == L'?')
            c = L'_';
    }

    std::wstring path = dir + L"\\" + filename;
    std::ofstream file(path);
    if (!file.is_open()) return false;

    char dateStr[32];
    strftime(dateStr, 32, "%Y-%m-%d %H:%M:%S", &t);

    file << "{\n";
    file << "  \"name\": \"" << EscJ(WideToUtf8(name)) << "\",\n";
    file << "  \"date\": \"" << dateStr << "\",\n";
    file << "  \"model\": \"" << EscJ(WideToUtf8(settings.model)) << "\",\n";
    file << "  \"messages\": [\n";

    for (size_t i = 0; i < conversation.size(); i++) {
        file << "    {\"role\": \"" << conversation[i].role << "\", ";
        file << "\"content\": \"" << EscJ(conversation[i].content) << "\"}";
        if (i + 1 < conversation.size()) file << ",";
        file << "\n";
    }

    file << "  ]\n}\n";
    file.close();
    return true;
}

// ── Load Session ─────────────────────────────────────────────────────

static std::string UnescJ(const std::string& s) {
    std::string out;
    for (size_t i = 0; i < s.size(); i++) {
        if (s[i] == '\\' && i + 1 < s.size()) {
            switch (s[i + 1]) {
            case '\\': out += '\\'; i++; break;
            case '"': out += '"'; i++; break;
            case 'n': out += '\n'; i++; break;
            case 'r': out += '\r'; i++; break;
            case 't': out += '\t'; i++; break;
            default: out += s[i]; break;
            }
        } else {
            out += s[i];
        }
    }
    return out;
}

static std::string ExtractStr(const std::string& json, const std::string& key) {
    std::string search = "\"" + key + "\"";
    size_t pos = json.find(search);
    if (pos == std::string::npos) return "";
    pos = json.find('"', pos + search.size() + 1);
    if (pos == std::string::npos) return "";
    pos++;
    std::string val;
    while (pos < json.size() && json[pos] != '"') {
        if (json[pos] == '\\' && pos + 1 < json.size()) {
            val += json[pos]; val += json[pos + 1]; pos += 2;
        } else {
            val += json[pos++];
        }
    }
    return UnescJ(val);
}

bool LoadSession(const std::wstring& filename, std::vector<ChatMessage>& conversation,
    std::wstring& model)
{
    std::wstring path = GetSessionsDir() + L"\\" + filename;
    std::ifstream file(path, std::ios::binary);
    if (!file.is_open()) return false;

    std::stringstream ss;
    ss << file.rdbuf();
    std::string json = ss.str();
    file.close();

    model = Utf8ToWide(ExtractStr(json, "model"));

    conversation.clear();

    // Parse messages array
    size_t msgPos = json.find("\"messages\"");
    if (msgPos == std::string::npos) return false;

    size_t arrStart = json.find('[', msgPos);
    if (arrStart == std::string::npos) return false;

    // Find each {role, content} pair — skip braces inside strings
    size_t arrEnd = std::string::npos;
    {   // Find matching ] for the messages array, respecting strings
        int depth = 0;
        bool inStr = false;
        for (size_t i = arrStart; i < json.size(); i++) {
            if (json[i] == '\\' && inStr) { i++; continue; }
            if (json[i] == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (json[i] == '[') depth++;
            if (json[i] == ']') { depth--; if (depth == 0) { arrEnd = i; break; } }
        }
    }
    if (arrEnd == std::string::npos) return false;

    // Find each top-level {} inside the array, respecting strings
    size_t pos = arrStart + 1;
    while (pos < arrEnd) {
        size_t objStart = std::string::npos;
        // Find next unquoted '{'
        bool inStr = false;
        for (size_t i = pos; i < arrEnd; i++) {
            if (json[i] == '\\' && inStr) { i++; continue; }
            if (json[i] == '"') { inStr = !inStr; continue; }
            if (!inStr && json[i] == '{') { objStart = i; break; }
        }
        if (objStart == std::string::npos) break;

        // Find matching '}' respecting strings
        int depth = 0; inStr = false;
        size_t objEnd = std::string::npos;
        for (size_t i = objStart; i < arrEnd; i++) {
            if (json[i] == '\\' && inStr) { i++; continue; }
            if (json[i] == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (json[i] == '{') depth++;
            if (json[i] == '}') { depth--; if (depth == 0) { objEnd = i; break; } }
        }
        if (objEnd == std::string::npos) break;

        std::string obj = json.substr(objStart, objEnd - objStart + 1);
        std::string role = ExtractStr(obj, "role");
        std::string content = ExtractStr(obj, "content");

        if (!role.empty()) {
            conversation.push_back({ role, content });
        }
        pos = objEnd + 1;
    }

    return true;
}

// ── Delete Session ───────────────────────────────────────────────────

bool DeleteSession(const std::wstring& filename) {
    std::wstring path = GetSessionsDir() + L"\\" + filename;
    return DeleteFileW(path.c_str()) != 0;
}

// ── List Sessions ────────────────────────────────────────────────────

std::vector<SessionInfo> ListSessions() {
    std::vector<SessionInfo> sessions;
    std::wstring dir = GetSessionsDir();

    WIN32_FIND_DATAW fd;
    HANDLE hFind = FindFirstFileW((dir + L"\\*.json").c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return sessions;

    do {
        SessionInfo si;
        si.filename = fd.cFileName;

        // Read file to get metadata
        std::ifstream file(dir + L"\\" + fd.cFileName, std::ios::binary);
        if (file.is_open()) {
            std::stringstream ss;
            ss << file.rdbuf();
            std::string json = ss.str();
            file.close();

            si.name = Utf8ToWide(ExtractStr(json, "name"));
            si.date = Utf8ToWide(ExtractStr(json, "date"));
            si.model = Utf8ToWide(ExtractStr(json, "model"));

            // Count messages
            si.messageCount = 0;
            size_t pos = 0;
            while ((pos = json.find("\"role\"", pos + 1)) != std::string::npos)
                si.messageCount++;
        }
        sessions.push_back(si);
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);

    // Sort by date descending (filename has timestamp prefix)
    std::sort(sessions.begin(), sessions.end(), [](const SessionInfo& a, const SessionInfo& b) {
        return a.filename > b.filename;
    });

    return sessions;
}

// ── Session History Dialog ───────────────────────────────────────────

static INT_PTR CALLBACK SessionDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static std::vector<SessionInfo> s_sessions;
    static HWND hList = nullptr, hPreview = nullptr;

    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        // Session list
        hList = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOTIFY,
            8, 8, 200, h - 52, hDlg, (HMENU)IDC_SESSION_LIST, g_hInst, nullptr);
        SendMessageW(hList, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        // Preview
        hPreview = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"Select a session to preview.",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            216, 8, w - 224, h - 52, hDlg, (HMENU)IDC_SESSION_PREVIEW, g_hInst, nullptr);
        SendMessageW(hPreview, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        // Buttons
        CreateWindowExW(0, L"BUTTON", L"Load", WS_CHILD | WS_VISIBLE,
            8, h - 38, 60, 26, hDlg, (HMENU)IDC_SESSION_LOAD, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Delete", WS_CHILD | WS_VISIBLE,
            74, h - 38, 60, 26, hDlg, (HMENU)IDC_SESSION_DELETE, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Close", WS_CHILD | WS_VISIBLE,
            w - 72, h - 38, 64, 26, hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Load session list
        s_sessions = ListSessions();
        for (auto& s : s_sessions) {
            std::wstring display = s.date + L" - " + s.name
                + L" (" + std::to_wstring(s.messageCount) + L" msgs)";
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
        }
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_SESSION_LIST:
            if (HIWORD(wp) == LBN_SELCHANGE) {
                int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
                if (sel >= 0 && sel < (int)s_sessions.size()) {
                    std::vector<ChatMessage> msgs;
                    std::wstring model;
                    if (LoadSession(s_sessions[sel].filename, msgs, model)) {
                        std::wstring preview = L"Session: " + s_sessions[sel].name + L"\r\n";
                        preview += L"Model: " + model + L"\r\n";
                        preview += L"Messages: " + std::to_wstring(msgs.size()) + L"\r\n\r\n";
                        for (auto& m : msgs) {
                            preview += L"[" + Utf8ToWide(m.role) + L"]\r\n";
                            std::wstring content = Utf8ToWide(m.content);
                            if (content.size() > 200) content = content.substr(0, 200) + L"...";
                            preview += content + L"\r\n\r\n";
                        }
                        SetWindowTextW(hPreview, preview.c_str());
                    }
                }
            }
            return TRUE;

        case IDC_SESSION_LOAD: {
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel < 0 || sel >= (int)s_sessions.size()) break;

            if (MessageBoxW(hDlg, L"Load this session? Current chat will be replaced.",
                L"Load Session", MB_YESNO | MB_ICONQUESTION) != IDYES) break;

            std::wstring model;
            if (LoadSession(s_sessions[sel].filename, g_conversation, model)) {
                g_settings.model = model;
                SetMainStatus(L"Session loaded: " + s_sessions[sel].name);
                EndDialog(hDlg, IDOK);
            }
            return TRUE;
        }

        case IDC_SESSION_DELETE: {
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel < 0 || sel >= (int)s_sessions.size()) break;

            if (MessageBoxW(hDlg, L"Delete this session?",
                L"Delete", MB_YESNO | MB_ICONWARNING) != IDYES) break;

            DeleteSession(s_sessions[sel].filename);
            s_sessions.erase(s_sessions.begin() + sel);
            SendMessageW(hList, LB_DELETESTRING, sel, 0);
            SetWindowTextW(hPreview, L"Deleted.");
            return TRUE;
        }

        case IDCANCEL:
            EndDialog(hDlg, 0);
            return TRUE;
        }
        break;
    }
    return FALSE;
}

void ShowSessionHistoryDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 460; tpl.dt.cy = 300;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, SessionDlgProc);
}
