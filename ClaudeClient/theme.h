#pragma once
#include <windows.h>
#include <string>
#include <vector>

// ═══════════════════════════════════════════════════════════════════════
// Catppuccin Mocha Color Palette
// ═══════════════════════════════════════════════════════════════════════

// Background hierarchy (dark → light)
constexpr COLORREF CLR_CRUST    = RGB(0x11, 0x11, 0x1B);
constexpr COLORREF CLR_MANTLE   = RGB(0x18, 0x18, 0x25);
constexpr COLORREF CLR_BASE     = RGB(0x1E, 0x1E, 0x2E);

// Surface hierarchy (interactive surfaces)
constexpr COLORREF CLR_SURFACE0 = RGB(0x31, 0x32, 0x44);
constexpr COLORREF CLR_SURFACE1 = RGB(0x45, 0x47, 0x5A);
constexpr COLORREF CLR_SURFACE2 = RGB(0x58, 0x5B, 0x70);

// Text hierarchy
constexpr COLORREF CLR_TEXT     = RGB(0xCD, 0xD6, 0xF4);
constexpr COLORREF CLR_SUBTEXT1 = RGB(0xBA, 0xC2, 0xDE);
constexpr COLORREF CLR_SUBTEXT0 = RGB(0xA6, 0xAD, 0xC8);
constexpr COLORREF CLR_OVERLAY0 = RGB(0x6C, 0x70, 0x86);

// Accent colors
constexpr COLORREF CLR_BLUE     = RGB(0x89, 0xB4, 0xFA);
constexpr COLORREF CLR_LAVENDER = RGB(0xB4, 0xBE, 0xFE);
constexpr COLORREF CLR_MAUVE    = RGB(0xCB, 0xA6, 0xF7);
constexpr COLORREF CLR_GREEN    = RGB(0xA6, 0xE3, 0xA1);
constexpr COLORREF CLR_TEAL     = RGB(0x94, 0xE2, 0xD5);
constexpr COLORREF CLR_SKY      = RGB(0x89, 0xDC, 0xFE);
constexpr COLORREF CLR_YELLOW   = RGB(0xF9, 0xE2, 0xAF);
constexpr COLORREF CLR_PEACH    = RGB(0xFA, 0xB3, 0x87);
constexpr COLORREF CLR_RED      = RGB(0xF3, 0x8B, 0xA8);
constexpr COLORREF CLR_PINK     = RGB(0xF5, 0xC2, 0xE7);

// Chat role backgrounds
constexpr COLORREF CLR_USER_BG     = RGB(0x2F, 0x3D, 0x5C);
constexpr COLORREF CLR_ASSISTANT_BG = RGB(0x24, 0x27, 0x3A);
constexpr COLORREF CLR_CODE_BG     = RGB(0x15, 0x15, 0x20);

// Button colors
constexpr COLORREF CLR_BTN_PRIMARY     = RGB(0x89, 0xB4, 0xFA);
constexpr COLORREF CLR_BTN_PRIMARY_HOV = RGB(0x74, 0x9C, 0xF0);
constexpr COLORREF CLR_BTN_PRIMARY_TXT = RGB(0x11, 0x11, 0x1B);
constexpr COLORREF CLR_BTN_SECONDARY   = RGB(0x31, 0x32, 0x44);
constexpr COLORREF CLR_BTN_SEC_HOV     = RGB(0x45, 0x47, 0x5A);

// Sidebar
constexpr int SIDEBAR_WIDTH = 240;

// ═══════════════════════════════════════════════════════════════════════
// Theme API
// ═══════════════════════════════════════════════════════════════════════

// Call once at startup before creating windows
void Theme_Init();

// Call on main window WM_DESTROY
void Theme_Cleanup();

// Call in WM_INITDIALOG / after CreateWindowEx — enables dark title bar
void Theme_ApplyDarkTitle(HWND hWnd);

// Handle WM_CTLCOLOR* messages — returns brush, sets text/bk colors
// bgColor: CLR_BASE for main content, CLR_MANTLE for dialogs/sidebar
HBRUSH Theme_HandleCtlColor(HDC hdc, HWND hCtrl, UINT msg,
    COLORREF bgColor = CLR_BASE);

// Get a cached solid brush for any color
HBRUSH Theme_GetBrush(COLORREF clr);

// Owner-draw a button (call from WM_DRAWITEM)
// isPrimary: blue bg + dark text; false: surface bg + light text
void Theme_DrawButton(LPDRAWITEMSTRUCT dis, bool isPrimary = false);

// Fill a rect with a solid color
void Theme_FillRect(HDC hdc, const RECT& rc, COLORREF clr);

// Draw text with color
void Theme_DrawText(HDC hdc, const RECT& rc, const std::wstring& text,
    COLORREF clr, HFONT hFont, UINT fmt = DT_LEFT | DT_VCENTER | DT_SINGLELINE);

// Draw a rounded rect border
void Theme_DrawRoundRect(HDC hdc, const RECT& rc, COLORREF fill,
    COLORREF border, int radius = 6);

// Apply dark theme to common controls (TreeView, ListView, etc.)
void Theme_ApplyDarkControl(HWND hCtrl);

// Paint sidebar background + content
// hdc: from WM_PAINT of main window; rc: sidebar rect
void Theme_PaintSidebar(HDC hdc, const RECT& rc, HFONT hFont, HFONT hFontBold,
    int activeNav, const std::vector<std::wstring>& sessions);

// Sidebar nav item info
struct SidebarNavItem {
    int         id;
    const wchar_t* icon;   // Segoe MDL2 Assets glyph or text emoji
    const wchar_t* label;
};

const SidebarNavItem* Theme_GetNavItems(int& count);
