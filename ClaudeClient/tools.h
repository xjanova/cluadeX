#pragma once
#include <windows.h>
#include <string>
#include <vector>

// ── File Browser Dialog ──
void ShowFileBrowserDialog(HWND hParent);

// ── Code Search Dialog (Glob + Grep) ──
void ShowCodeSearchDialog(HWND hParent);

// ── Web Fetch Dialog ──
void ShowWebFetchDialog(HWND hParent);

// ── Project Context Dialog ──
void ShowProjectContextDialog(HWND hParent);

// ── Utility: glob pattern match ──
bool GlobMatch(const std::wstring& pattern, const std::wstring& text);

// ── Utility: recursive file search ──
struct FileEntry {
    std::wstring path;
    bool isDir;
    DWORD size;
};
std::vector<FileEntry> ListDirectory(const std::wstring& dir, bool recursive = false);

// ── Utility: grep files ──
struct GrepResult {
    std::wstring file;
    int line;
    std::wstring text;
};
std::vector<GrepResult> GrepFiles(const std::wstring& dir, const std::wstring& pattern,
    const std::wstring& fileGlob = L"*", int maxResults = 200);
