#pragma once
#include <windows.h>
#include <string>
#include <vector>

// ── Plugin System ──
void ShowPluginManagerDialog(HWND hParent);

struct PluginInfo {
    std::wstring name;
    std::wstring description;
    std::wstring path;        // path to plugin directory
    std::wstring version;
    bool enabled;
};

std::wstring GetPluginsDir();
std::vector<PluginInfo> ScanPlugins();
bool EnablePlugin(const std::wstring& name, bool enable);
bool InstallPluginFromPath(const std::wstring& srcPath);

// ── Permissions System ──
void ShowPermissionsDialog(HWND hParent);

enum class PermAction { Allow, Deny, Ask };

struct PermissionRule {
    std::wstring pattern;     // glob pattern for path or command
    std::wstring scope;       // "file", "command", "network"
    PermAction action;
};

std::vector<PermissionRule> LoadPermissionRules();
bool SavePermissionRules(const std::vector<PermissionRule>& rules);
PermAction CheckPermission(const std::wstring& resource, const std::wstring& scope);
