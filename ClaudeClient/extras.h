#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include <functional>

// ── Task Manager ──
void ShowTaskManagerDialog(HWND hParent);

struct TaskInfo {
    int id;
    std::wstring name;
    std::wstring command;
    std::wstring status;   // "running", "completed", "failed"
    std::string output;
    HANDLE hThread;
};

int CreateTask(const std::wstring& name, const std::wstring& command);
bool StopTask(int taskId);
std::vector<TaskInfo> ListTasks();
std::string GetTaskOutput(int taskId);

// ── Voice Input ──
// Record audio using Windows waveIn API, returns WAV file path
bool StartVoiceRecording();
bool StopVoiceRecording(std::wstring& wavFilePath);
bool IsRecording();

// ── Vim Mode ──
// Subclass procedure for vim-style editing in input control
void EnableVimMode(HWND hEdit);
void DisableVimMode(HWND hEdit);
bool IsVimModeEnabled();
std::wstring GetVimModeStatus(); // "NORMAL", "INSERT", "VISUAL"
