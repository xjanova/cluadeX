#include "settings.h"
#include <shlobj.h>
#include <fstream>
#include <sstream>

#pragma comment(lib, "shell32.lib")

// ── Helpers ──────────────────────────────────────────────────────────

static std::wstring EscapeJson(const std::wstring& s) {
    std::wstring out;
    for (wchar_t c : s) {
        switch (c) {
        case L'\\': out += L"\\\\"; break;
        case L'"':  out += L"\\\""; break;
        case L'\n': out += L"\\n";  break;
        case L'\r': out += L"\\r";  break;
        case L'\t': out += L"\\t";  break;
        default:    out += c;       break;
        }
    }
    return out;
}

static std::wstring UnescapeJson(const std::wstring& s) {
    std::wstring out;
    for (size_t i = 0; i < s.size(); i++) {
        if (s[i] == L'\\' && i + 1 < s.size()) {
            switch (s[i + 1]) {
            case L'\\': out += L'\\'; i++; break;
            case L'"':  out += L'"';  i++; break;
            case L'n':  out += L'\n'; i++; break;
            case L'r':  out += L'\r'; i++; break;
            case L't':  out += L'\t'; i++; break;
            default:    out += s[i];       break;
            }
        } else {
            out += s[i];
        }
    }
    return out;
}

// Extract value for a key from simple flat JSON
static std::wstring JsonGetString(const std::wstring& json, const std::wstring& key) {
    std::wstring search = L"\"" + key + L"\"";
    size_t pos = json.find(search);
    if (pos == std::wstring::npos) return L"";

    pos = json.find(L':', pos + search.size());
    if (pos == std::wstring::npos) return L"";

    // Find opening quote
    pos = json.find(L'"', pos + 1);
    if (pos == std::wstring::npos) return L"";
    pos++; // skip quote

    // Find closing quote (handle escapes)
    std::wstring value;
    while (pos < json.size() && json[pos] != L'"') {
        if (json[pos] == L'\\' && pos + 1 < json.size()) {
            value += json[pos];
            value += json[pos + 1];
            pos += 2;
        } else {
            value += json[pos];
            pos++;
        }
    }
    return UnescapeJson(value);
}

static int JsonGetInt(const std::wstring& json, const std::wstring& key, int def) {
    std::wstring search = L"\"" + key + L"\"";
    size_t pos = json.find(search);
    if (pos == std::wstring::npos) return def;

    pos = json.find(L':', pos + search.size());
    if (pos == std::wstring::npos) return def;
    pos++;

    // Skip whitespace
    while (pos < json.size() && (json[pos] == L' ' || json[pos] == L'\t')) pos++;

    std::wstring num;
    while (pos < json.size() && (iswdigit(json[pos]) || json[pos] == L'-'))
        num += json[pos++];

    return num.empty() ? def : _wtoi(num.c_str());
}

static double JsonGetDouble(const std::wstring& json, const std::wstring& key, double def) {
    std::wstring search = L"\"" + key + L"\"";
    size_t pos = json.find(search);
    if (pos == std::wstring::npos) return def;

    pos = json.find(L':', pos + search.size());
    if (pos == std::wstring::npos) return def;
    pos++;

    while (pos < json.size() && (json[pos] == L' ' || json[pos] == L'\t')) pos++;

    std::wstring num;
    while (pos < json.size() && (iswdigit(json[pos]) || json[pos] == L'.' || json[pos] == L'-'))
        num += json[pos++];

    return num.empty() ? def : _wtof(num.c_str());
}

// ── Public API ───────────────────────────────────────────────────────

std::wstring GetSettingsDir() {
    wchar_t* appdata = nullptr;
    SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &appdata);
    std::wstring dir = std::wstring(appdata) + L"\\CluadeX";
    CoTaskMemFree(appdata);
    return dir;
}

std::wstring GetSettingsPath() {
    return GetSettingsDir() + L"\\settings.json";
}

bool LoadSettings(AppSettings& s) {
    std::wifstream file(GetSettingsPath());
    if (!file.is_open()) return false;

    std::wstringstream ss;
    ss << file.rdbuf();
    std::wstring json = ss.str();
    file.close();

    std::wstring val;

    val = JsonGetString(json, L"apiKey");
    if (!val.empty()) s.apiKey = val;

    val = JsonGetString(json, L"model");
    if (!val.empty()) s.model = val;

    s.maxTokens   = JsonGetInt(json, L"maxTokens", s.maxTokens);
    s.temperature  = JsonGetDouble(json, L"temperature", s.temperature);

    val = JsonGetString(json, L"systemPrompt");
    if (!val.empty()) s.systemPrompt = val;

    val = JsonGetString(json, L"serverMode");
    if (val == L"local") s.serverMode = ServerMode::Local;
    else s.serverMode = ServerMode::Cloud;

    val = JsonGetString(json, L"localEndpoint");
    if (!val.empty()) s.localEndpoint = val;

    return true;
}

bool SaveSettings(const AppSettings& s) {
    std::wstring dir = GetSettingsDir();
    CreateDirectoryW(dir.c_str(), nullptr);

    std::wofstream file(GetSettingsPath());
    if (!file.is_open()) return false;

    file << L"{\n";
    file << L"  \"apiKey\": \"" << EscapeJson(s.apiKey) << L"\",\n";
    file << L"  \"model\": \"" << EscapeJson(s.model) << L"\",\n";
    file << L"  \"maxTokens\": " << s.maxTokens << L",\n";
    file << L"  \"temperature\": " << s.temperature << L",\n";
    file << L"  \"systemPrompt\": \"" << EscapeJson(s.systemPrompt) << L"\",\n";
    file << L"  \"serverMode\": \"" << (s.serverMode == ServerMode::Local ? L"local" : L"cloud") << L"\",\n";
    file << L"  \"localEndpoint\": \"" << EscapeJson(s.localEndpoint) << L"\"\n";
    file << L"}\n";

    file.close();
    return true;
}
