#include "extras.h"
#include "app_state.h"
#include "theme.h"
#include "resource.h"
#include <mmsystem.h>
#include <fstream>
#include <map>

#pragma comment(lib, "winmm.lib")

// ═══════════════════════════════════════════════════════════════════
// ── Task Manager ───────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════

static std::map<int, TaskInfo> s_tasks;
static int s_nextTaskId = 1;
static std::mutex s_taskMutex;

static DWORD WINAPI TaskThreadProc(LPVOID param) {
    int taskId = (int)(intptr_t)param;
    std::wstring cmd;
    {
        std::lock_guard<std::mutex> lock(s_taskMutex);
        if (s_tasks.count(taskId)) cmd = s_tasks[taskId].command;
    }

    std::string output = RunCommand(cmd);

    {
        std::lock_guard<std::mutex> lock(s_taskMutex);
        if (s_tasks.count(taskId)) {
            s_tasks[taskId].output = output;
            s_tasks[taskId].status = L"completed";
        }
    }
    return 0;
}

int CreateTask(const std::wstring& name, const std::wstring& command) {
    std::lock_guard<std::mutex> lock(s_taskMutex);
    int id = s_nextTaskId++;

    TaskInfo ti;
    ti.id = id;
    ti.name = name;
    ti.command = command;
    ti.status = L"running";
    ti.hThread = CreateThread(nullptr, 0, TaskThreadProc, (LPVOID)(intptr_t)id, 0, nullptr);
    s_tasks[id] = std::move(ti);
    return id;
}

bool StopTask(int taskId) {
    std::lock_guard<std::mutex> lock(s_taskMutex);
    if (!s_tasks.count(taskId)) return false;
    auto& t = s_tasks[taskId];
    if (t.hThread && t.status == L"running") {
        // Note: TerminateThread is unsafe but there's no cooperative cancellation
        // for RunCommand (CreateProcess). We mark as stopped; the thread will
        // finish naturally when the child process exits.
        t.status = L"stopped";
        // Close the handle to avoid leak
        CloseHandle(t.hThread);
        t.hThread = nullptr;
    }
    return true;
}

std::vector<TaskInfo> ListTasks() {
    std::lock_guard<std::mutex> lock(s_taskMutex);
    std::vector<TaskInfo> list;
    for (auto& [id, t] : s_tasks) {
        // Check if thread finished
        if (t.hThread && t.status == L"running") {
            DWORD exitCode;
            if (GetExitCodeThread(t.hThread, &exitCode) && exitCode != STILL_ACTIVE) {
                t.status = L"completed";
                CloseHandle(t.hThread);
                t.hThread = nullptr;
            }
        }
        list.push_back(t);
    }
    return list;
}

std::string GetTaskOutput(int taskId) {
    std::lock_guard<std::mutex> lock(s_taskMutex);
    if (s_tasks.count(taskId)) return s_tasks[taskId].output;
    return "";
}

// ── Task Manager Dialog ──────────────────────────────────────────────

static INT_PTR CALLBACK TaskMgrProc(HWND hDlg, UINT msg, WPARAM wp, LPARAM lp) {
    static HWND hList = nullptr, hOutput = nullptr;

    switch (msg) {
    case WM_CTLCOLOREDIT: case WM_CTLCOLORSTATIC: case WM_CTLCOLORBTN:
    case WM_CTLCOLORDLG:  case WM_CTLCOLORLISTBOX:
        return (LRESULT)Theme_HandleCtlColor((HDC)wp, (HWND)lp, msg);

    case WM_INITDIALOG: {
        Theme_ApplyDarkTitle(hDlg);
        RECT rc; GetClientRect(hDlg, &rc);
        int w = rc.right, h = rc.bottom;

        // Task list
        hList = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOTIFY,
            8, 8, 200, h / 2 - 12, hDlg, (HMENU)IDC_TASK_LIST, g_hInst, nullptr);
        SendMessageW(hList, WM_SETFONT, (WPARAM)g_hFont, TRUE);

        // New task controls
        CreateWindowExW(0, L"STATIC", L"Name:", WS_CHILD | WS_VISIBLE,
            216, 10, 34, 14, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            252, 8, w - 260, 18, hDlg, (HMENU)IDC_TASK_STATUS, g_hInst, nullptr);

        CreateWindowExW(0, L"STATIC", L"Cmd:", WS_CHILD | WS_VISIBLE,
            216, 34, 34, 14, hDlg, nullptr, g_hInst, nullptr);
        CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            252, 32, w - 330, 18, hDlg, (HMENU)(IDC_TASK_STATUS + 1), g_hInst, nullptr);

        CreateWindowExW(0, L"BUTTON", L"Start", WS_CHILD | WS_VISIBLE,
            w - 72, 32, 36, 18, hDlg, (HMENU)IDC_TASK_NEW, g_hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Stop", WS_CHILD | WS_VISIBLE,
            w - 34, 32, 28, 18, hDlg, (HMENU)IDC_TASK_STOP, g_hInst, nullptr);

        // Output
        hOutput = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            8, h / 2 + 4, w - 16, h / 2 - 40, hDlg, (HMENU)IDC_TASK_OUTPUT, g_hInst, nullptr);
        SendMessageW(hOutput, WM_SETFONT, (WPARAM)g_hFontMono, TRUE);

        CreateWindowExW(0, L"BUTTON", L"Close", WS_CHILD | WS_VISIBLE,
            w - 72, h - 30, 64, 24, hDlg, (HMENU)IDCANCEL, g_hInst, nullptr);

        // Populate
        auto tasks = ListTasks();
        for (auto& t : tasks) {
            std::wstring display = L"[" + t.status + L"] " + t.name;
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
        }

        // Auto-refresh timer
        SetTimer(hDlg, 1, 2000, nullptr);
        return TRUE;
    }

    case WM_TIMER:
        if (wp == 1) {
            // Refresh task statuses
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            SendMessageW(hList, LB_RESETCONTENT, 0, 0);
            auto tasks = ListTasks();
            for (auto& t : tasks) {
                std::wstring display = L"[" + t.status + L"] #"
                    + std::to_wstring(t.id) + L" " + t.name;
                SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
            }
            if (sel >= 0) SendMessageW(hList, LB_SETCURSEL, sel, 0);
        }
        return TRUE;

    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDC_TASK_LIST:
            if (HIWORD(wp) == LBN_SELCHANGE) {
                int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
                auto tasks = ListTasks();
                if (sel >= 0 && sel < (int)tasks.size()) {
                    SetWindowTextW(hOutput, Utf8ToWide(tasks[sel].output).c_str());
                }
            }
            return TRUE;

        case IDC_TASK_NEW: {
            wchar_t name[256] = {}, cmd[1024] = {};
            GetDlgItemTextW(hDlg, IDC_TASK_STATUS, name, 255);
            GetDlgItemTextW(hDlg, IDC_TASK_STATUS + 1, cmd, 1023);
            if (wcslen(name) == 0 || wcslen(cmd) == 0) {
                MessageBoxW(hDlg, L"Enter task name and command.", L"Task", MB_ICONWARNING);
                return TRUE;
            }
            int id = CreateTask(name, cmd);
            std::wstring display = L"[running] #" + std::to_wstring(id) + L" " + name;
            SendMessageW(hList, LB_ADDSTRING, 0, (LPARAM)display.c_str());
            SetDlgItemTextW(hDlg, IDC_TASK_STATUS, L"");
            SetDlgItemTextW(hDlg, IDC_TASK_STATUS + 1, L"");
            return TRUE;
        }

        case IDC_TASK_STOP: {
            int sel = (int)SendMessageW(hList, LB_GETCURSEL, 0, 0);
            auto tasks = ListTasks();
            if (sel >= 0 && sel < (int)tasks.size()) {
                StopTask(tasks[sel].id);
            }
            return TRUE;
        }

        case IDCANCEL:
            KillTimer(hDlg, 1);
            EndDialog(hDlg, 0);
            return TRUE;
        }
        break;
    }
    return FALSE;
}

void ShowTaskManagerDialog(HWND hParent) {
    struct { DLGTEMPLATE dt; WORD m, c, t; } tpl = {};
    tpl.dt.style = DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION
        | WS_SYSMENU | WS_THICKFRAME | WS_VISIBLE;
    tpl.dt.cx = 420; tpl.dt.cy = 300;
    DialogBoxIndirectW(g_hInst, &tpl.dt, hParent, TaskMgrProc);
}

// ═══════════════════════════════════════════════════════════════════
// ── Voice Input (Windows waveIn API) ───────────────────────────────
// ═══════════════════════════════════════════════════════════════════

static HWAVEIN s_hWaveIn = nullptr;
static WAVEHDR s_waveHdr = {};
static std::vector<BYTE> s_audioBuffer;
static bool s_recording = false;
static std::wstring s_wavPath;

static void CALLBACK WaveInCallback(HWAVEIN hwi, UINT uMsg, DWORD_PTR dwInstance,
    DWORD_PTR dwParam1, DWORD_PTR dwParam2) {
    if (uMsg == WIM_DATA) {
        // Buffer filled
        WAVEHDR* hdr = (WAVEHDR*)dwParam1;
        if (hdr->dwBytesRecorded > 0) {
            s_audioBuffer.insert(s_audioBuffer.end(),
                hdr->lpData, hdr->lpData + hdr->dwBytesRecorded);
        }
        if (s_recording) {
            waveInAddBuffer(hwi, hdr, sizeof(WAVEHDR));
        }
    }
}

bool StartVoiceRecording() {
    if (s_recording) return false;

    WAVEFORMATEX wfx = {};
    wfx.wFormatTag = WAVE_FORMAT_PCM;
    wfx.nChannels = 1;
    wfx.nSamplesPerSec = 16000;
    wfx.wBitsPerSample = 16;
    wfx.nBlockAlign = wfx.nChannels * wfx.wBitsPerSample / 8;
    wfx.nAvgBytesPerSec = wfx.nSamplesPerSec * wfx.nBlockAlign;

    MMRESULT result = waveInOpen(&s_hWaveIn, WAVE_MAPPER, &wfx,
        (DWORD_PTR)WaveInCallback, 0, CALLBACK_FUNCTION);
    if (result != MMSYSERR_NOERROR) return false;

    s_audioBuffer.clear();

    // Allocate buffer (1 second chunks)
    static char buf[32000];
    s_waveHdr = {};
    s_waveHdr.lpData = buf;
    s_waveHdr.dwBufferLength = sizeof(buf);
    waveInPrepareHeader(s_hWaveIn, &s_waveHdr, sizeof(WAVEHDR));
    waveInAddBuffer(s_hWaveIn, &s_waveHdr, sizeof(WAVEHDR));

    s_recording = true;
    waveInStart(s_hWaveIn);
    return true;
}

bool StopVoiceRecording(std::wstring& wavFilePath) {
    if (!s_recording) return false;
    s_recording = false;

    waveInStop(s_hWaveIn);
    waveInReset(s_hWaveIn);
    waveInUnprepareHeader(s_hWaveIn, &s_waveHdr, sizeof(WAVEHDR));
    waveInClose(s_hWaveIn);
    s_hWaveIn = nullptr;

    if (s_audioBuffer.empty()) return false;

    // Write WAV file
    s_wavPath = GetSettingsDir() + L"\\recording.wav";
    std::ofstream wav(s_wavPath, std::ios::binary);
    if (!wav.is_open()) return false;

    DWORD dataSize = (DWORD)s_audioBuffer.size();
    DWORD fileSize = 36 + dataSize;

    // WAV header
    wav.write("RIFF", 4);
    wav.write((char*)&fileSize, 4);
    wav.write("WAVE", 4);
    wav.write("fmt ", 4);
    DWORD fmtSize = 16;
    wav.write((char*)&fmtSize, 4);
    WORD audioFmt = 1; wav.write((char*)&audioFmt, 2);
    WORD channels = 1; wav.write((char*)&channels, 2);
    DWORD sampleRate = 16000; wav.write((char*)&sampleRate, 4);
    DWORD byteRate = 32000; wav.write((char*)&byteRate, 4);
    WORD blockAlign = 2; wav.write((char*)&blockAlign, 2);
    WORD bitsPerSample = 16; wav.write((char*)&bitsPerSample, 2);
    wav.write("data", 4);
    wav.write((char*)&dataSize, 4);
    wav.write((char*)s_audioBuffer.data(), dataSize);
    wav.close();

    wavFilePath = s_wavPath;
    return true;
}

bool IsRecording() { return s_recording; }

// ═══════════════════════════════════════════════════════════════════
// ── Vim Mode ───────────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════

enum class VimState { Normal, Insert, Visual };
static VimState s_vimState = VimState::Insert; // start in Insert
static bool s_vimEnabled = false;
static WNDPROC s_origEditProc = nullptr;
static HWND s_vimEdit = nullptr;

bool IsVimModeEnabled() { return s_vimEnabled; }

std::wstring GetVimModeStatus() {
    if (!s_vimEnabled) return L"";
    switch (s_vimState) {
    case VimState::Normal: return L"-- NORMAL --";
    case VimState::Insert: return L"-- INSERT --";
    case VimState::Visual: return L"-- VISUAL --";
    }
    return L"";
}

static LRESULT CALLBACK VimEditProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_KEYDOWN && s_vimState == VimState::Normal) {
        DWORD start, end;
        SendMessageW(hWnd, EM_GETSEL, (WPARAM)&start, (LPARAM)&end);

        switch (wp) {
        case 'I': // Enter insert mode
            s_vimState = VimState::Insert;
            SetMainStatus(L"-- INSERT --");
            return 0;
        case 'A': // Append (enter insert after cursor)
            s_vimState = VimState::Insert;
            SendMessageW(hWnd, EM_SETSEL, end + 1, end + 1);
            SetMainStatus(L"-- INSERT --");
            return 0;
        case 'H': // Left
            if (start > 0) SendMessageW(hWnd, EM_SETSEL, start - 1, start - 1);
            return 0;
        case 'L': // Right
            SendMessageW(hWnd, EM_SETSEL, end + 1, end + 1);
            return 0;
        case 'X': // Delete char
            SendMessageW(hWnd, EM_SETSEL, start, start + 1);
            SendMessageW(hWnd, EM_REPLACESEL, TRUE, (LPARAM)L"");
            return 0;
        case '0': // Home
            SendMessageW(hWnd, EM_SETSEL, 0, 0);
            return 0;
        case VK_OEM_4: // $ = End (Shift+4)
            if (GetKeyState(VK_SHIFT) & 0x8000) {
                int len = GetWindowTextLengthW(hWnd);
                SendMessageW(hWnd, EM_SETSEL, len, len);
            }
            return 0;
        case 'U': // Undo
            SendMessageW(hWnd, EM_UNDO, 0, 0);
            return 0;
        case 'V': // Visual mode
            s_vimState = VimState::Visual;
            SetMainStatus(L"-- VISUAL --");
            return 0;
        }
        // Block other keys in normal mode
        if (wp >= 'A' && wp <= 'Z') return 0;
    }

    if (msg == WM_KEYDOWN && s_vimState == VimState::Insert) {
        if (wp == VK_ESCAPE) {
            s_vimState = VimState::Normal;
            SetMainStatus(L"-- NORMAL --");
            return 0;
        }
    }

    if (msg == WM_KEYDOWN && s_vimState == VimState::Visual) {
        if (wp == VK_ESCAPE) {
            s_vimState = VimState::Normal;
            SetMainStatus(L"-- NORMAL --");
            return 0;
        }
    }

    if (msg == WM_CHAR && s_vimState == VimState::Normal) {
        return 0; // Block character input in normal mode
    }

    return CallWindowProcW(s_origEditProc, hWnd, msg, wp, lp);
}

void EnableVimMode(HWND hEdit) {
    if (s_vimEnabled) return;
    s_vimEnabled = true;
    s_vimEdit = hEdit;
    s_vimState = VimState::Normal;
    s_origEditProc = (WNDPROC)SetWindowLongPtrW(hEdit, GWLP_WNDPROC, (LONG_PTR)VimEditProc);
    SetMainStatus(L"-- NORMAL -- (Vim mode enabled)");
}

void DisableVimMode(HWND hEdit) {
    if (!s_vimEnabled) return;
    s_vimEnabled = false;
    SetWindowLongPtrW(hEdit, GWLP_WNDPROC, (LONG_PTR)s_origEditProc);
    s_origEditProc = nullptr;
    s_vimState = VimState::Insert;
    SetMainStatus(L"Vim mode disabled");
}
