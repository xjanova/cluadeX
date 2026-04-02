#include "plugins.h"
#include "app_state.h"
#include "theme.h"
#include "resource.h"
#include <shlobj.h>
#include <fstream>
#include <sstream>
#include <algorithm>
#include <set>

// ── Plugin Directory ─────────────────────────────────────────────────

std::wstring GetPluginsDir() {
    return GetSettingsDir() + L"\\plugins";
}

static std::wstring GetPluginConfigPath() {
    return GetSettingsDir() + L"\\plugins_config.json";
}

// ── Scan Plugins ─────────────────────────────────────────────────────

std::vector<PluginInfo> ScanPlugins() {
    std::vector<PluginInfo> plugins;
    std::wstring dir = GetPluginsDir();
    CreateDirectoryW(GetSettingsDir().c_str(), nullptr);
    CreateDirectoryW(dir.c_str(), nullptr);

    // Scan for plugin directories with manifest.json
    WIN32_FIND_DATAW fd;
    HANDLE hFind = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return plugins;

    // Load enabled state from enabled_plugins array
    std::set<std::string> enabledSet;
    {
        std::ifstream cfg(GetPluginConfigPath());
        if (cfg.is_open()) {
            std::stringstream ss;
            ss << cfg.rdbuf();
            std::string json = ss.str();
            // Find the enabled_plugins array and extract quoted strings
            size_t arrPos = json.find("\"enabled_plugins\"");
            if (arrPos != std::string::npos) {
                size_t arrStart = json.find('[', arrPos);
                size_t arrEnd = json.find(']', arrStart);
                if (arrStart != std::string::npos && arrEnd != std::string::npos) {
                    std::string arr = json.substr(arrStart, arrEnd - arrStart);
                    size_t pos = 0;
                    while ((pos = arr.find('"', pos)) != std::string::npos) {
                        pos++;
                        size_t end = arr.find('"', pos);
                        if (end == std::string::npos) break;
                        enabledSet.insert(arr.substr(pos, end - pos));
                        pos = end + 1;
                    }
                }
            }
        }
    }

    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;

        std::wstring manifestPath = dir + L"\\" + name + L"\\manifest.json";
        std::ifstream manifest(manifestPath);

        PluginInfo pi;
        pi.name = name;
        pi.path = dir + L"\\" + name;
        pi.enabled = enabledSet.count(WideToUtf8(name)) > 0;

        if (manifest.is_open()) {
            std::stringstream ss;
            ss << manifest.rdbuf();
            std::string json = ss.str();

            // Extract description and version
            auto extract = [&](const std::string& key) -> std::string {
                std::string search = "\"" + key + "\"";
                size_t pos = json.find(search);
                if (pos == std::string::npos) return "";
                pos = json.find('"', pos + search.size() + 1);
                if (pos == std::string::npos) return "";
                pos++;
                std::string val;
                while (pos < json.size() && json[pos] != '"') val += json[pos++];
                return val;
            };

            pi.description = Utf8ToWide(extract("description"));
            pi.version = Utf8ToWide(extract("version"));
        } else {
            pi.description = L"(no manifest.json)";
            pi.version = L"?";
        }

        plugins.push_back(pi);
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);

    return plugins;
}

// ── Enable/Disable Plugin ────────────────────────────────────────────

bool EnablePlugin(const std::wstring& name, bool enable) {
    auto plugins = ScanPlugins();
    for (auto& p : plugins) {
        if (p.name == name) p.enabled = enable;
    }

    // Save config
    std::ofstream cfg(GetPluginConfigPath());
    if (!cfg.is_open()) return false;
    cfg << "{\n  \"enabled_plugins\": [\n";
    bool first = true;
    for (auto& p : plugins) {
        if (p.enabled) {
            if (!first) cfg << ",\n";
            cfg << "    \"" << WideToUtf8(p.name) << "\"";
            first = false;
        }
    }
    cfg << "\n  ]\n}\n";
    return true;
}

bool InstallPluginFromPath(const std::wstring& srcPath) {
    // Copy directory to plugins dir
    std::wstring name = srcPath.substr(srcPath.find_last_of(L"\\/") + 1);
    std::wstring dest = GetPluginsDir() + L"\\" + name;
    CreateDirectoryW(dest.c_str(), nullptr);

    // Simple copy: just copy all files
    WIN32_FIND_DATAW fd;
    HANDLE hFind = FindFirstFileW((srcPath + L"\\*").c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return false;

    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        std::wstring src = srcPath + L"\\" + fd.cFileName;
        std::wstring dst = dest + L"\\" + fd.cFileName;
        CopyFileW(src.c_str(), dst.c_str(), FALSE);
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);
    return true;
}

// ── Plugin Manager Dialog ────────────────────────────────────────────

static INT_PTR CALLBACK PluginMgrProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static std::vector<PluginInfo> s_plugins;
    static HWND hList = nullptr, hInfo = nullptr;

    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        hList = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOTIFY,
            8, 8, 200, h - 88, hDlg, (HMENU)IDC_PLUGIN_LIST, g_hInst, nullptr);
        SendMessageW(hList, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        hInfo = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT",
            L"Select a plugin for details.\r\n\r\n"
            L"Plugins are loaded from:\r\n%APPDATA%\\CluadeX\\plugins\\\r\n\r\n"
            L"Each plugin needs a manifest.json with:\r\n"
            L"{\r\n  \"name\": \"...\",\r\n  \"description\": \"...\",\r\n  \"version\": \"1.0\"\r\n}",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY,
            216, 8, w - 224, h - 88, hDlg, (HMENU)IDC_PLUGIN_INFO, g_hInst, nullptr);
        SendMessageW(hInfo, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        int btnY = h - 72;
        CreateWindowExW(0, L"BUTTON", L"Enable", WS_CHILD | WS_VISIBLE,
            8, btnY, 60, 24, hDlg, (HMENU)IDC_PLUGIN_ENABLE, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Disable", WS_CHILD | WS_VISIBLE,
            74, btnY, 60, 24, hDlg, (HMENU)IDC_PLUGIN_DISABLE, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Add...", WS_CHILD | WS_VISIBLE,
            140, btnY, 60, 24, hDlg, (HMENU)IDC_PLUGIN_ADD, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Open Folder", WS_CHILD | WS_VISIBLE,
            8, btnY + 30, 90, 24, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Close", WS_CHILD | WS_VISIBLE,
            w - 72, btnY + 30, 64, 24, hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Refresh
        s_plugins = ScanPlugins();
        for (auto& p : s_plugins) {
            std::wstring display = (p.enabled ? L"[ON]  " : L"[OFF] ") + p.name;
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
        }
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_PLUGIN_LIST:
            if (HIWORD(wp) == LBN_SELCHANGE) {
                int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
                if (sel >= 0 && sel < (int)s_plugins.size()) {
                    auto& p = s_plugins[sel];
                    std::wstring info = L"Name: " + p.name + L"\r\n";
                    info += L"Version: " + p.version + L"\r\n";
                    info += L"Status: " + std::wstring(p.enabled ? L"Enabled" : L"Disabled") + L"\r\n";
                    info += L"Path: " + p.path + L"\r\n\r\n";
                    info += L"Description:\r\n" + p.description;
                    SetWindowTextW(hInfo, info.c_str());
                }
            }
            return TRUE;

        case IDC_PLUGIN_ENABLE:
        case IDC_PLUGIN_DISABLE: {
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel < 0 || sel >= (int)s_plugins.size()) break;
            bool enable = (LOWORD(wp) == IDC_PLUGIN_ENABLE);
            s_plugins[sel].enabled = enable;
            EnablePlugin(s_plugins[sel].name, enable);
            // Update display
            std::wstring display = (enable ? L"[ON]  " : L"[OFF] ") + s_plugins[sel].name;
            SendMessageW(hList, LB_DELETESTRING, sel, 0);
            SendMessageW(hList, LB_INSERTSTRING, sel, (LPARAM)display.c_str());
            SendMessageW(hList, LB_SETCURSEL, sel, 0);
            return TRUE;
        }

        case IDC_PLUGIN_ADD: {
            // Browse for plugin folder
            BROWSEINFOW bi = {};
            bi.hwndOwner = hDlg;
            bi.lpszTitle = L"Select plugin folder (must contain manifest.json)";
            bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
            PIDLIST_ABSOLUTE pidl = SHBrowseForFolderW(&bi);
            if (pidl) {
                wchar_t path[MAX_PATH];
                SHGetPathFromIDListW(pidl, path);
                CoTaskMemFree(pidl);
                if (InstallPluginFromPath(path)) {
                    MessageBoxW(hDlg, L"Plugin installed. Restart to apply.", L"Plugin", MB_ICONINFORMATION);
                }
            }
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

void ShowPluginManagerDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 420; tpl.dt.cy = 280;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, PluginMgrProc);
}

// ═══════════════════════════════════════════════════════════════════
// ── Permissions System ─────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════

static std::wstring GetPermissionsPath() {
    return GetSettingsDir() + L"\\permissions.json";
}

static std::vector<PermissionRule> s_rules;

std::vector<PermissionRule> LoadPermissionRules() {
    s_rules.clear();
    std::ifstream file(GetPermissionsPath());
    if (!file.is_open()) return s_rules;

    std::string line;
    while (std::getline(file, line)) {
        // Simple parse: "pattern|scope|action"
        if (line.empty() || line[0] == '#' || line[0] == '{' || line[0] == '}') continue;

        // Find pattern, scope, action from JSON-like format
        size_t p1 = line.find("\"pattern\"");
        if (p1 == std::string::npos) continue;

        auto extractVal = [&](const std::string& key) -> std::string {
            size_t pos = line.find("\"" + key + "\"");
            if (pos == std::string::npos) return "";
            pos = line.find('"', pos + key.size() + 2);
            if (pos == std::string::npos) return "";
            pos++;
            std::string v;
            while (pos < line.size() && line[pos] != '"') v += line[pos++];
            return v;
        };

        PermissionRule r;
        r.pattern = Utf8ToWide(extractVal("pattern"));
        r.scope = Utf8ToWide(extractVal("scope"));
        std::string act = extractVal("action");
        r.action = (act == "allow") ? PermAction::Allow :
                   (act == "deny") ? PermAction::Deny : PermAction::Ask;

        if (!r.pattern.empty()) s_rules.push_back(r);
    }
    return s_rules;
}

bool SavePermissionRules(const std::vector<PermissionRule>& rules) {
    s_rules = rules;
    std::ofstream file(GetPermissionsPath());
    if (!file.is_open()) return false;

    file << "[\n";
    for (size_t i = 0; i < rules.size(); i++) {
        file << "  {\"pattern\": \"" << WideToUtf8(rules[i].pattern)
             << "\", \"scope\": \"" << WideToUtf8(rules[i].scope)
             << "\", \"action\": \""
             << (rules[i].action == PermAction::Allow ? "allow" :
                 rules[i].action == PermAction::Deny ? "deny" : "ask")
             << "\"}";
        if (i + 1 < rules.size()) file << ",";
        file << "\n";
    }
    file << "]\n";
    return true;
}

PermAction CheckPermission(const std::wstring& resource, const std::wstring& scope) {
    for (auto& r : s_rules) {
        if (r.scope != scope && r.scope != L"*") continue;
        // Simple glob match on pattern
        if (resource.find(r.pattern) != std::wstring::npos || r.pattern == L"*") {
            return r.action;
        }
    }
    return PermAction::Ask; // default: ask
}

// ── Permissions Dialog ───────────────────────────────────────────────

static INT_PTR CALLBACK PermDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static std::vector<PermissionRule> s_editRules;
    static HWND hList = nullptr;

    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        hList = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOTIFY,
            8, 8, w - 16, h - 100, hDlg, (HMENU)IDC_PERM_LIST, g_hInst, nullptr);
        SendMessageW(hList, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        CreateWindowExW(0, L"STATIC", L"Pattern:", WS_CHILD | WS_VISIBLE,
            8, h - 84, 42, 14, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            54, h - 86, 140, 18, hDlg, (HMENU)IDC_PERM_RULE, g_hInst, nullptr);

        CreateWindowExW(0, L"STATIC", L"Scope:", WS_CHILD | WS_VISIBLE,
            200, h - 84, 34, 14, hDlg, nullptr, g_hInst, nullptr);
        HWND hScope = CreateWindowExW(0, L"COMBOBOX", L"",
            WS_CHILD | WS_VISIBLE | CBS_DROPDOWNLIST,
            238, h - 86, 80, 80, hDlg, (HMENU)IDC_PERM_MODE, g_hInst, nullptr);
        SendMessageW(hScope, CB_ADDSTRING, 0, (LPARAM)L"file");
        SendMessageW(hScope, CB_ADDSTRING, 0, (LPARAM)L"command");
        SendMessageW(hScope, CB_ADDSTRING, 0, (LPARAM)L"network");
        SendMessageW(hScope, CB_ADDSTRING, 0, (LPARAM)L"*");
        SendMessageW(hScope, CB_SETCURSEL, 0, 0);

        CreateWindowExW(0, L"BUTTON", L"+ Allow", WS_CHILD | WS_VISIBLE,
            8, h - 60, 60, 22, hDlg, (HMENU)IDC_PERM_ADD, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"+ Deny", WS_CHILD | WS_VISIBLE,
            74, h - 60, 60, 22, hDlg, (HMENU)IDC_PERM_DENY, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Remove", WS_CHILD | WS_VISIBLE,
            140, h - 60, 60, 22, hDlg, (HMENU)IDC_PERM_REMOVE, g_hInst, nullptr);

        CreateWindowExW(0, L"BUTTON", L"Save", WS_CHILD | WS_VISIBLE,
            w - 136, h - 34, 60, 24, hDlg, (HMENU)IDC_SAVE, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Close", WS_CHILD | WS_VISIBLE,
            w - 72, h - 34, 64, 24, hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Load rules
        s_editRules = LoadPermissionRules();
        for (auto& r : s_editRules) {
            std::wstring act = (r.action == PermAction::Allow) ? L"ALLOW" :
                               (r.action == PermAction::Deny) ? L"DENY" : L"ASK";
            std::wstring display = L"[" + act + L"] " + r.scope + L": " + r.pattern;
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
        }
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_PERM_ADD:
        case IDC_PERM_DENY: {
            wchar_t pat[256] = {};
            GetDlgItemTextW(hDlg, IDC_PERM_RULE, pat, 255);
            if (wcslen(pat) == 0) break;

            HWND hScope = GetDlgItem(hDlg, IDC_PERM_MODE);
            int idx = (int)SendMessageW(hScope, CB_GETCURSEL, 0, 0);
            wchar_t scope[32] = {};
            SendMessageW(hScope, CB_GETLBTEXT, idx, (LPARAM)scope);

            PermissionRule r;
            r.pattern = pat;
            r.scope = scope;
            r.action = (LOWORD(wp) == IDC_PERM_ADD) ? PermAction::Allow : PermAction::Deny;
            s_editRules.push_back(r);

            std::wstring act = (r.action == PermAction::Allow) ? L"ALLOW" : L"DENY";
            std::wstring display = L"[" + act + L"] " + r.scope + L": " + r.pattern;
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
            SetDlgItemTextW(hDlg, IDC_PERM_RULE, L"");
            return TRUE;
        }

        case IDC_PERM_REMOVE: {
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            if (sel >= 0 && sel < (int)s_editRules.size()) {
                s_editRules.erase(s_editRules.begin() + sel);
                SendMessageW(hList, LB_DELETESTRING, sel, 0);
            }
            return TRUE;
        }

        case IDC_SAVE:
            SavePermissionRules(s_editRules);
            MessageBoxW(hDlg, L"Permissions saved.", L"Permissions", MB_ICONINFORMATION);
            return TRUE;

        case IDCANCEL:
            EndDialog(hDlg, 0);
            return TRUE;
        }
        break;
    }
    return FALSE;
}

void ShowPermissionsDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 380; tpl.dt.cy = 260;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, PermDlgProc);
}
