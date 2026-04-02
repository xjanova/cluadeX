#include "git_ops.h"
#include "app_state.h"
#include "theme.h"
#include "resource.h"
#include <sstream>

// ── Git Commands (shell out) ─────────────────────────────────────────

static std::string GitCmd(const std::wstring& repoDir, const std::wstring& args) {
    std::wstring cmd = L"cd /d \"" + repoDir + L"\" && git " + args;
    return RunCommand(cmd);
}

bool IsGitRepo(const std::wstring& dir) {
    DWORD attr = GetFileAttributesW((dir + L"\\.git").c_str());
    if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) return true;
    // Also check parent dirs
    std::string result = GitCmd(dir, L"rev-parse --is-inside-work-tree 2>nul");
    return result.find("true") != std::string::npos;
}

std::string GitStatus(const std::wstring& repoDir) { return GitCmd(repoDir, L"status"); }
std::string GitLog(const std::wstring& repoDir, int count) {
    return GitCmd(repoDir, L"log --oneline --graph -" + std::to_wstring(count));
}
std::string GitDiff(const std::wstring& repoDir, bool staged) {
    return GitCmd(repoDir, staged ? L"diff --staged" : L"diff");
}
std::string GitBranches(const std::wstring& repoDir) { return GitCmd(repoDir, L"branch -a"); }
std::string GitCommit(const std::wstring& repoDir, const std::wstring& message) {
    // Sanitize commit message to prevent command injection
    std::wstring safe = message;
    for (auto& c : safe) {
        if (c == L'"') c = L'\'';
        if (c == L'`' || c == L'|' || c == L'&' || c == L';' || c == L'$') c = L'_';
    }
    return GitCmd(repoDir, L"commit -m \"" + safe + L"\"");
}
std::string GitAdd(const std::wstring& repoDir, const std::wstring& files) {
    return GitCmd(repoDir, L"add " + files);
}
std::string GitCheckout(const std::wstring& repoDir, const std::wstring& branch) {
    return GitCmd(repoDir, L"checkout " + branch);
}

// ── Git Dialog ───────────────────────────────────────────────────────

static HWND s_gitOutput = nullptr;
static std::wstring s_gitDir;

static void GitAppend(const std::string& text) {
    std::wstring w = Utf8ToWide(text);
    int len = GetWindowTextLengthW(s_gitOutput);
    SendMessageW(s_gitOutput, EM_SETSEL, len, len);
    SendMessageW(s_gitOutput, EM_REPLACESEL, FALSE, (LPARAM)(w + L"\r\n").c_str());
    SendMessageW(s_gitOutput, EM_SCROLLCARET, 0, 0);
}

static INT_PTR CALLBACK GitDlgProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;
        int btnW = 72, btnH = 24, btnY = 8, gap = 4;
        int x = 8;

        CreateWindowExW(0, L"BUTTON", L"Status", WS_CHILD | WS_VISIBLE,
            x, btnY, btnW, btnH, hDlg, (HMENU)IDC_GIT_STATUS, g_hInst, nullptr); x += btnW + gap;
        CreateWindowExW(0, L"BUTTON", L"Log", WS_CHILD | WS_VISIBLE,
            x, btnY, btnW, btnH, hDlg, (HMENU)IDC_GIT_LOG, g_hInst, nullptr); x += btnW + gap;
        CreateWindowExW(0, L"BUTTON", L"Diff", WS_CHILD | WS_VISIBLE,
            x, btnY, btnW, btnH, hDlg, (HMENU)IDC_GIT_DIFF, g_hInst, nullptr); x += btnW + gap;
        CreateWindowExW(0, L"BUTTON", L"Branches", WS_CHILD | WS_VISIBLE,
            x, btnY, btnW, btnH, hDlg, (HMENU)IDC_GIT_BRANCH, g_hInst, nullptr); x += btnW + gap;
        CreateWindowExW(0, L"BUTTON", L"Add All", WS_CHILD | WS_VISIBLE,
            x, btnY, btnW, btnH, hDlg, (HMENU)IDC_GIT_DIFF + 100, g_hInst, nullptr);

        // Commit message
        CreateWindowExW(0, L"STATIC", L"Commit:", WS_CHILD | WS_VISIBLE,
            8, btnY + btnH + 8, 44, 16, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            56, btnY + btnH + 6, w - 140, 20, hDlg, (HMENU)IDC_GIT_COMMIT_MSG, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Commit", WS_CHILD | WS_VISIBLE,
            w - 78, btnY + btnH + 6, 70, 20, hDlg, (HMENU)IDC_GIT_COMMIT, g_hInst, nullptr);

        // Output
        int outY = btnY + btnH + 36;
        s_gitOutput = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL
            | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, outY, w - 16, h - outY - 8, hDlg, (HMENU)IDC_GIT_OUTPUT, g_hInst, nullptr);
        SendMessageW(s_gitOutput, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);
        SendMessageW(s_gitOutput, EM_SETLIMITTEXT, 0x7FFFFFFE, 0);

        wchar_t cwd[MAX_PATH];
        GetCurrentDirectoryW(MAX_PATH, cwd);
        s_gitDir = cwd;

        if (!IsGitRepo(s_gitDir)) {
            GitAppend("Not a git repository: " + WideToUtf8(s_gitDir));
        } else {
            GitAppend("Git repository: " + WideToUtf8(s_gitDir));
            GitAppend(GitStatus(s_gitDir));
        }
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_GIT_STATUS:
            SetWindowTextW(s_gitOutput, L"");
            GitAppend(GitStatus(s_gitDir));
            return TRUE;
        case IDC_GIT_LOG:
            SetWindowTextW(s_gitOutput, L"");
            GitAppend(GitLog(s_gitDir, 30));
            return TRUE;
        case IDC_GIT_DIFF:
            SetWindowTextW(s_gitOutput, L"");
            GitAppend(GitDiff(s_gitDir, false));
            return TRUE;
        case IDC_GIT_BRANCH:
            SetWindowTextW(s_gitOutput, L"");
            GitAppend(GitBranches(s_gitDir));
            return TRUE;
        case IDC_GIT_DIFF + 100: // Add All
            GitAppend(GitAdd(s_gitDir, L"."));
            GitAppend("--- Files staged ---");
            GitAppend(GitStatus(s_gitDir));
            return TRUE;
        case IDC_GIT_COMMIT: {
            wchar_t msg[1024] = {};
            GetDlgItemTextW(hDlg, IDC_GIT_COMMIT_MSG, msg, 1023);
            if (wcslen(msg) == 0) {
                MessageBoxW(hDlg, L"Enter a commit message.", L"Git", MB_ICONWARNING);
                return TRUE;
            }
            if (MessageBoxW(hDlg, (L"Commit with message:\n\n" + std::wstring(msg)).c_str(),
                L"Confirm Commit", MB_YESNO | MB_ICONQUESTION) == IDYES) {
                GitAppend(GitCommit(s_gitDir, msg));
                SetDlgItemTextW(hDlg, IDC_GIT_COMMIT_MSG, L"");
            }
            return TRUE;
        }
        case IDCANCEL:
            EndDialog(hDlg, 0);
            return TRUE;
        }
        break;

    case WM_SIZE: {
        RECT rc; GetClientRect(hDlg, &rc);
        int outY = 68;
        MoveWindow(s_gitOutput, 8, outY, rc.right - 16, rc.bottom - outY - 8, TRUE);
        return TRUE;
    }
    }
    return FALSE;
}

void ShowGitDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 440; tpl.dt.cy = 300;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, GitDlgProc);
}
