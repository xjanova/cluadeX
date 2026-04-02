#pragma once
#include <windows.h>
#include <string>
#include <vector>

// Show the Git integration dialog
void ShowGitDialog(HWND hParent);

// Git operations (all shell out to `git`)
std::string GitStatus(const std::wstring& repoDir = L".");
std::string GitLog(const std::wstring& repoDir = L".", int count = 20);
std::string GitDiff(const std::wstring& repoDir = L".", bool staged = false);
std::string GitBranches(const std::wstring& repoDir = L".");
std::string GitCommit(const std::wstring& repoDir, const std::wstring& message);
std::string GitAdd(const std::wstring& repoDir, const std::wstring& files = L".");
std::string GitCheckout(const std::wstring& repoDir, const std::wstring& branch);
bool IsGitRepo(const std::wstring& dir);
