#include "tools.h"
#include "app_state.h"
#include "git_ops.h"
#include "theme.h"
#include "resource.h"
#include <commctrl.h>
#include <commdlg.h>
#include <fstream>
#include <sstream>
#include <algorithm>
#include <mutex>
#include <regex>

#pragma comment(lib, "comctl32.lib")

// ── Glob Pattern Matching ────────────────────────────────────────────

bool GlobMatch(const std::wstring& pattern, const std::wstring& text) {
    size_t pi = 0, ti = 0;
    size_t starP = std::wstring::npos, starT = 0;
    while (ti < text.size()) {
        if (pi < pattern.size() && (pattern[pi] == L'?' || towlower(pattern[pi]) == towlower(text[ti]))) {
            pi++; ti++;
        } else if (pi < pattern.size() && pattern[pi] == L'*') {
            starP = pi++; starT = ti;
        } else if (starP != std::wstring::npos) {
            pi = starP + 1; ti = ++starT;
        } else {
            return false;
        }
    }
    while (pi < pattern.size() && pattern[pi] == L'*') pi++;
    return pi == pattern.size();
}

// ── Directory Listing ────────────────────────────────────────────────

std::vector<FileEntry> ListDirectory(const std::wstring& dir, bool recursive) {
    std::vector<FileEntry> entries;
    WIN32_FIND_DATAW fd;
    std::wstring search = dir + L"\\*";
    HANDLE hFind = FindFirstFileW(search.c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return entries;

    do {
        std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..") continue;
        if (name == L".git" || name == L"node_modules" || name == L"__pycache__") continue;

        FileEntry e;
        e.path = dir + L"\\" + name;
        e.isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        e.size = fd.nFileSizeLow;
        entries.push_back(e);

        if (e.isDir && recursive) {
            auto sub = ListDirectory(e.path, true);
            entries.insert(entries.end(), sub.begin(), sub.end());
        }
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);
    return entries;
}

// ── Grep ─────────────────────────────────────────────────────────────

std::vector<GrepResult> GrepFiles(const std::wstring& dir, const std::wstring& pattern,
    const std::wstring& fileGlob, int maxResults)
{
    std::vector<GrepResult> results;
    auto files = ListDirectory(dir, true);

    std::string patUtf8 = WideToUtf8(pattern);

    for (auto& f : files) {
        if (f.isDir) continue;
        if (f.size > 2 * 1024 * 1024) continue; // skip files > 2MB

        // Check file glob
        std::wstring fname = f.path.substr(f.path.find_last_of(L"\\/") + 1);
        if (fileGlob != L"*" && !GlobMatch(fileGlob, fname)) continue;

        std::ifstream file(f.path, std::ios::binary);
        if (!file.is_open()) continue;

        std::string line;
        int lineNum = 0;
        while (std::getline(file, line) && (int)results.size() < maxResults) {
            lineNum++;
            // Case-insensitive search
            std::string lower = line;
            std::string lowerPat = patUtf8;
            std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);
            std::transform(lowerPat.begin(), lowerPat.end(), lowerPat.begin(), ::tolower);

            if (lower.find(lowerPat) != std::string::npos) {
                GrepResult r;
                r.file = f.path;
                r.line = lineNum;
                r.text = Utf8ToWide(line.substr(0, 200));
                results.push_back(r);
            }
        }
        if ((int)results.size() >= maxResults) break;
    }
    return results;
}

// ── File Browser Dialog ──────────────────────────────────────────────

static HWND s_hTreeView = nullptr;
static HWND s_hFileEdit = nullptr;
static HWND s_hFilePath = nullptr;
static std::wstring s_currentFilePath;
static std::wstring s_browseRoot;

static void PopulateTree(HWND hTree, HTREEITEM hParent, const std::wstring& dir) {
    auto entries = ListDirectory(dir, false);
    // Dirs first, then files
    std::sort(entries.begin(), entries.end(), [](const FileEntry& a, const FileEntry& b) {
        if (a.isDir != b.isDir) return a.isDir > b.isDir;
        return _wcsicmp(a.path.c_str(), b.path.c_str()) < 0;
    });

    for (auto& e : entries) {
        std::wstring name = e.path.substr(e.path.find_last_of(L"\\/") + 1);
        TVINSERTSTRUCTW tvi = {};
        tvi.hParent = hParent;
        tvi.hInsertAfter = TVI_LAST;
        tvi.item.mask = TVIF_TEXT | TVIF_PARAM | TVIF_CHILDREN;
        tvi.item.pszText = (LPWSTR)name.c_str();
        tvi.item.lParam = (LPARAM)new std::wstring(e.path);
        tvi.item.cChildren = e.isDir ? 1 : 0;
        SendMessageW(hTree, TVM_INSERTITEMW, 0, (LPARAM)&tvi);
    }
}

static INT_PTR CALLBACK FileBrowserProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        // Create controls programmatically for complex layout
        RECT rc;
        GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        // Path bar
        s_hFilePath = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", s_browseRoot.c_str(),
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL | ES_READONLY,
            8, 8, w - 16, 22, hDlg, (HMENU)IDC_FILE_PATH, g_hInst, nullptr);
        SendMessageW(s_hFilePath, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        // TreeView (left)
        s_hTreeView = CreateWindowExW(WS_EX_CLIENTEDGE, WC_TREEVIEWW, L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | TVS_HASLINES | TVS_HASBUTTONS
            | TVS_LINESATROOT | TVS_SHOWSELALWAYS,
            8, 36, 220, h - 86, hDlg, (HMENU)IDC_FILE_TREE, g_hInst, nullptr);
        SendMessageW(s_hTreeView, WM_SETFONT, (WPARAM)g_hFont, TRUE);
        Theme_ApplyDarkControl(s_hTreeView);
        TreeView_SetBkColor(s_hTreeView, CLR_BASE);
        TreeView_SetTextColor(s_hTreeView, CLR_TEXT);
        TreeView_SetLineColor(s_hTreeView, CLR_SURFACE1);

        // File content (right)
        s_hFileEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL
            | ES_MULTILINE | ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_WANTRETURN,
            236, 36, w - 244, h - 86, hDlg, (HMENU)IDC_FILE_CONTENT, g_hInst, nullptr);
        SendMessageW(s_hFileEdit, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);
        SendMessageW(s_hFileEdit, EM_SETLIMITTEXT, 0x7FFFFFFE, 0);

        // Buttons
        CreateWindowExW(0, L"BUTTON", L"Save File",
            WS_CHILD | WS_VISIBLE, 8, h - 42, 80, 26,
            hDlg, (HMENU)IDC_FILE_SAVE, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Send to Chat",
            WS_CHILD | WS_VISIBLE, 96, h - 42, 90, 26,
            hDlg, (HMENU)IDC_FILE_OPEN_BTN, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Close",
            WS_CHILD | WS_VISIBLE, w - 72, h - 42, 64, 26,
            hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Populate root
        PopulateTree(s_hTreeView, TVI_ROOT, s_browseRoot);
        return TRUE;
    }

    case WM_NOTIFY: {
        NMHDR* nm = (NMHDR*)lp;
        if (nm->hwndFrom == s_hTreeView) {
            if (nm->code == TVN_DELETEITEMW) {
                NMTREEVIEWW* ntv = (NMTREEVIEWW*)lp;
                delete (std::wstring*)ntv->itemOld.lParam;
                return 0;
            }
            if (nm->code == TVN_ITEMEXPANDINGW) {
                NMTREEVIEWW* ntv = (NMTREEVIEWW*)lp;
                if (ntv->action == TVE_EXPAND) {
                    // Lazy load: populate children on expand
                    HTREEITEM hChild = TreeView_GetChild(s_hTreeView, ntv->itemNew.hItem);
                    if (!hChild) {
                        auto* path = (std::wstring*)ntv->itemNew.lParam;
                        if (path) PopulateTree(s_hTreeView, ntv->itemNew.hItem, *path);
                    }
                }
            }
            if (nm->code == TVN_SELCHANGEDW) {
                NMTREEVIEWW* ntv = (NMTREEVIEWW*)lp;
                auto* path = (std::wstring*)ntv->itemNew.lParam;
                if (path) {
                    s_currentFilePath = *path;
                    SetWindowTextW(s_hFilePath, path->c_str());

                    // Check if file (not dir)
                    DWORD attr = GetFileAttributesW(path->c_str());
                    if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
                        // Read file content
                        std::ifstream file(*path, std::ios::binary);
                        if (file.is_open()) {
                            std::stringstream ss;
                            ss << file.rdbuf();
                            std::string content = ss.str();
                            // Limit to 1MB display
                            if (content.size() > 1024 * 1024)
                                content = content.substr(0, 1024 * 1024) + "\n\n[... truncated ...]";
                            SetWindowTextW(s_hFileEdit, Utf8ToWide(content).c_str());
                        }
                    }
                }
            }
        }
        return 0;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_FILE_SAVE: {
            if (s_currentFilePath.empty()) break;
            DWORD attr = GetFileAttributesW(s_currentFilePath.c_str());
            if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) break;

            int len = GetWindowTextLengthW(s_hFileEdit);
            std::wstring text(len + 1, 0);
            GetWindowTextW(s_hFileEdit, &text[0], len + 1);
            text.resize(len);

            std::ofstream out(s_currentFilePath, std::ios::binary);
            if (out.is_open()) {
                std::string utf8 = WideToUtf8(text);
                out.write(utf8.c_str(), utf8.size());
                out.close();
                MessageBoxW(hDlg, L"File saved.", L"Success", MB_ICONINFORMATION);
            } else {
                MessageBoxW(hDlg, L"Failed to save file.", L"Error", MB_ICONERROR);
            }
            return TRUE;
        }
        case IDC_FILE_OPEN_BTN: {
            if (s_currentFilePath.empty()) break;
            int len = GetWindowTextLengthW(s_hFileEdit);
            std::wstring text(len + 1, 0);
            GetWindowTextW(s_hFileEdit, &text[0], len + 1);
            text.resize(len);

            std::wstring fname = s_currentFilePath.substr(
                s_currentFilePath.find_last_of(L"\\/") + 1);
            std::wstring msg = L"File `" + fname + L"`:\n\n" + text;
            // Put in main input (handled by caller)
            AppendChatLine(L"[File Attached]\r\n", fname);
            {
                std::lock_guard<std::mutex> lock(g_apiMutex);
                g_conversation.push_back({ "user",
                    "Here is the file `" + WideToUtf8(fname) + "`:\n\n" + WideToUtf8(text) });
            }
            return TRUE;
        }
        case IDCANCEL:
            EndDialog(hDlg, 0);
            return TRUE;
        }
        break;

    case WM_SIZE: {
        RECT rc;
        GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;
        MoveWindow(s_hFilePath, 8, 8, w - 16, 22, TRUE);
        MoveWindow(s_hTreeView, 8, 36, 220, h - 86, TRUE);
        MoveWindow(s_hFileEdit, 236, 36, w - 244, h - 86, TRUE);
        return TRUE;
    }

    case WM_DESTROY: {
        // TreeView sends TVN_DELETEITEM for each item on destroy, which frees lParam
        return 0;
    }
    }
    return FALSE;
}

void ShowFileBrowserDialog(HWND hParent) {
    // Pick folder
    wchar_t dir[MAX_PATH] = L".";
    GetCurrentDirectoryW(MAX_PATH, dir);
    s_browseRoot = dir;

    // Create dialog template in memory (resizable)
    struct {
        DLGTEMPLATE dt;
        WORD menu, cls, title;
    } dlgTpl = {};
    dlgTpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    dlgTpl.dt.cx = 500; dlgTpl.dt.cy = 350;
    dlgTpl.title = 0;

    DialogBoxIndirectW(g_hInst, &dlgTpl.dt, hParent, FileBrowserProc);
}

// ── Code Search Dialog ───────────────────────────────────────────────

static INT_PTR CALLBACK CodeSearchProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static HWND hResults = nullptr;
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc;
        GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        CreateWindowExW(0, L"STATIC", L"Search:", WS_CHILD | WS_VISIBLE,
            8, 10, 44, 16, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            56, 8, w - 200, 20, hDlg, (HMENU)IDC_SEARCH_PATTERN, g_hInst, nullptr);

        CreateWindowExW(0, L"STATIC", L"File:", WS_CHILD | WS_VISIBLE,
            w - 138, 10, 28, 16, hDlg, nullptr, g_hInst, nullptr);
        auto hGlob = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"*",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            w - 108, 8, 60, 20, hDlg, (HMENU)IDC_SEARCH_DIR, g_hInst, nullptr);

        CreateWindowExW(0, L"BUTTON", L"Search",
            WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
            w - 42, 8, 36, 20, hDlg, (HMENU)IDC_SEARCH_GO, g_hInst, nullptr);

        hResults = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL
            | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, 36, w - 16, h - 44, hDlg, (HMENU)IDC_SEARCH_RESULTS, g_hInst, nullptr);
        SendMessageW(hResults, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);
        return TRUE;
    }

    case WM_COMMAND:
        if (LOWORD(wp) == IDC_SEARCH_GO) {
            wchar_t pat[512] = {}, glob[128] = {};
            GetDlgItemTextW(hDlg, IDC_SEARCH_PATTERN, pat, 511);
            GetDlgItemTextW(hDlg, IDC_SEARCH_DIR, glob, 127);

            if (wcslen(pat) == 0) break;

            wchar_t cwd[MAX_PATH];
            GetCurrentDirectoryW(MAX_PATH, cwd);

            SetWindowTextW(hResults, L"Searching...\r\n");

            auto results = GrepFiles(cwd, pat, glob, 200);

            std::wstring output;
            output += L"Found " + std::to_wstring(results.size()) + L" results:\r\n\r\n";
            for (auto& r : results) {
                output += r.file + L":" + std::to_wstring(r.line) + L":  " + r.text + L"\r\n";
            }
            SetWindowTextW(hResults, output.c_str());
            return TRUE;
        }
        if (LOWORD(wp) == IDCANCEL) { EndDialog(hDlg, 0); return TRUE; }
        break;

    case WM_SIZE: {
        RECT rc;
        GetClientRect(hDlg, &rc);
        MoveWindow(hResults, 8, 36, rc.right - 16, rc.bottom - 44, TRUE);
        return TRUE;
    }
    }
    return FALSE;
}

void ShowCodeSearchDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 440; tpl.dt.cy = 280;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, CodeSearchProc);
}

// ── Web Fetch Dialog ─────────────────────────────────────────────────

static INT_PTR CALLBACK WebFetchProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static HWND hContent = nullptr;
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right;

        CreateWindowExW(0, L"STATIC", L"URL:", WS_CHILD | WS_VISIBLE,
            8, 10, 28, 16, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"https://",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            38, 8, w - 120, 20, hDlg, (HMENU)IDC_WEB_URL, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Fetch", WS_CHILD | WS_VISIBLE,
            w - 76, 8, 40, 20, hDlg, (HMENU)IDC_WEB_FETCH_BTN, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Chat", WS_CHILD | WS_VISIBLE,
            w - 34, 8, 28, 20, hDlg, (HMENU)IDC_WEB_SEND_CHAT, g_hInst, nullptr);

        hContent = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, 36, w - 16, rc.bottom - 44, hDlg, (HMENU)IDC_WEB_CONTENT, g_hInst, nullptr);
        SendMessageW(hContent, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);
        SendMessageW(hContent, EM_SETLIMITTEXT, 0x7FFFFFFE, 0);
        return TRUE;
    }

    case WM_COMMAND:
        if (LOWORD(wp) == IDC_WEB_FETCH_BTN) {
            wchar_t url[2048] = {};
            GetDlgItemTextW(hDlg, IDC_WEB_URL, url, 2047);
            if (wcslen(url) < 8) break;

            SetWindowTextW(hContent, L"Fetching...\r\n");
            EnableWindow(GetDlgItem(hDlg, IDC_WEB_FETCH_BTN), FALSE);

            std::wstring urlW = url;
            std::thread([hDlg, urlW]() {
                // Validate URL: must start with http:// or https://, no shell metacharacters
                bool validUrl = (urlW.find(L"http://") == 0 || urlW.find(L"https://") == 0);
                for (auto c : urlW) {
                    if (c == L'\'' || c == L'"' || c == L'`' || c == L'|' || c == L'&'
                        || c == L';' || c == L'$' || c == L'(' || c == L')' || c == L'{' || c == L'}') {
                        validUrl = false;
                        break;
                    }
                }
                if (!validUrl) {
                    PostMessageW(hDlg, WM_USER + 10, 0,
                        (LPARAM)new std::wstring(L"Invalid URL. Must start with http:// or https:// and contain no special characters."));
                    return;
                }
                // Simple HTTP GET
                std::string result = RunCommand(L"powershell -Command \"(Invoke-WebRequest -Uri '"
                    + urlW + L"' -UseBasicParsing).Content\"");

                PostMessageW(hDlg, WM_USER + 10, 0,
                    (LPARAM)new std::wstring(Utf8ToWide(result)));
            }).detach();
            return TRUE;
        }
        if (LOWORD(wp) == IDC_WEB_SEND_CHAT) {
            int len = GetWindowTextLengthW(hContent);
            if (len <= 0) break;
            std::wstring text(len + 1, 0);
            GetWindowTextW(hContent, &text[0], len + 1); text.resize(len);

            wchar_t url[2048] = {};
            GetDlgItemTextW(hDlg, IDC_WEB_URL, url, 2047);

            std::string content = "Content fetched from " + WideToUtf8(url) + ":\n\n" + WideToUtf8(text);
            {
                std::lock_guard<std::mutex> lock(g_apiMutex);
                g_conversation.push_back({ "user", content });
            }
            AppendChatLine(L"[Web Content]\r\n", std::wstring(url));
            return TRUE;
        }
        if (LOWORD(wp) == IDCANCEL) { EndDialog(hDlg, 0); return TRUE; }
        break;

    case WM_USER + 10: {
        EnableWindow(GetDlgItem(hDlg, IDC_WEB_FETCH_BTN), TRUE);
        auto* text = (std::wstring*)lp;
        SetWindowTextW(hContent, text->c_str());
        delete text;
        return TRUE;
    }

    case WM_SIZE: {
        RECT rc; GetClientRect(hDlg, &rc);
        MoveWindow(hContent, 8, 36, rc.right - 16, rc.bottom - 44, TRUE);
        return TRUE;
    }
    }
    return FALSE;
}

void ShowWebFetchDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 420; tpl.dt.cy = 280;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, WebFetchProc);
}

// ── Project Context Dialog ───────────────────────────────────────────

static std::string GatherProjectContext(const std::wstring& dir) {
    std::ostringstream ctx;
    ctx << "=== Project Context ===\n\n";

    // Current directory
    ctx << "Working Directory: " << WideToUtf8(dir) << "\n\n";

    // Check for project files
    const wchar_t* projectFiles[] = {
        L"package.json", L"Cargo.toml", L"CMakeLists.txt", L"Makefile",
        L"go.mod", L"pom.xml", L"build.gradle", L"pyproject.toml",
        L"requirements.txt", L"setup.py", L".sln", L"*.vcxproj",
        L"CLAUDE.md", L"README.md"
    };
    ctx << "Project Files Found:\n";
    for (auto pf : projectFiles) {
        WIN32_FIND_DATAW fd;
        std::wstring search = dir + L"\\" + pf;
        HANDLE h = FindFirstFileW(search.c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE) {
            ctx << "  - " << WideToUtf8(fd.cFileName) << "\n";
            FindClose(h);
        }
    }

    // Read CLAUDE.md if exists
    std::ifstream claudeMd(dir + L"\\CLAUDE.md");
    if (claudeMd.is_open()) {
        ctx << "\n--- CLAUDE.md ---\n";
        std::string line;
        int lines = 0;
        while (std::getline(claudeMd, line) && lines < 100) {
            ctx << line << "\n";
            lines++;
        }
        ctx << "---\n";
    }

    // Git info
    if (IsGitRepo(dir)) {
        ctx << "\nGit Status:\n" << RunCommand(L"git status --short") << "\n";
        ctx << "Git Branch: " << RunCommand(L"git branch --show-current") << "\n";
    }

    // Directory structure (top 2 levels)
    ctx << "\nDirectory Structure:\n";
    auto entries = ListDirectory(dir, false);
    for (auto& e : entries) {
        std::wstring name = e.path.substr(e.path.find_last_of(L"\\/") + 1);
        ctx << (e.isDir ? "  [DIR] " : "  ") << WideToUtf8(name);
        if (!e.isDir) ctx << " (" << e.size << " bytes)";
        ctx << "\n";
    }

    return ctx.str();
}

static INT_PTR CALLBACK ProjectCtxProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static HWND hInfo = nullptr;
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        hInfo = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"Gathering context...",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL
            | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, 8, w - 16, h - 52, hDlg, (HMENU)IDC_CTX_INFO, g_hInst, nullptr);
        SendMessageW(hInfo, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        CreateWindowExW(0, L"BUTTON", L"Refresh", WS_CHILD | WS_VISIBLE,
            8, h - 36, 70, 26, hDlg, (HMENU)IDC_CTX_REFRESH, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Send to Chat", WS_CHILD | WS_VISIBLE,
            86, h - 36, 90, 26, hDlg, (HMENU)IDC_CTX_SEND, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Close", WS_CHILD | WS_VISIBLE,
            w - 72, h - 36, 64, 26, hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Gather context
        wchar_t cwd[MAX_PATH];
        GetCurrentDirectoryW(MAX_PATH, cwd);
        std::string ctx = GatherProjectContext(cwd);
        SetWindowTextW(hInfo, Utf8ToWide(ctx).c_str());
        return TRUE;
    }

    case WM_COMMAND:
        if (LOWORD(wp) == IDC_CTX_REFRESH) {
            wchar_t cwd[MAX_PATH];
            GetCurrentDirectoryW(MAX_PATH, cwd);
            std::string ctx = GatherProjectContext(cwd);
            SetWindowTextW(hInfo, Utf8ToWide(ctx).c_str());
            return TRUE;
        }
        if (LOWORD(wp) == IDC_CTX_SEND) {
            int len = GetWindowTextLengthW(hInfo);
            std::wstring text(len + 1, 0);
            GetWindowTextW(hInfo, &text[0], len + 1); text.resize(len);
            {
                std::lock_guard<std::mutex> lock(g_apiMutex);
                g_conversation.push_back({ "user", WideToUtf8(text) });
            }
            AppendChatLine(L"[Project Context]\r\n", L"Sent project context to chat.");
            return TRUE;
        }
        if (LOWORD(wp) == IDCANCEL) { EndDialog(hDlg, 0); return TRUE; }
        break;

    case WM_SIZE: {
        RECT rc; GetClientRect(hDlg, &rc);
        MoveWindow(hInfo, 8, 8, rc.right - 16, rc.bottom - 52, TRUE);
        return TRUE;
    }
    }
    return FALSE;
}

void ShowProjectContextDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 400; tpl.dt.cy = 300;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, ProjectCtxProc);
}
