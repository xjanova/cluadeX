#include "theme.h"
#include <dwmapi.h>
#include <uxtheme.h>
#include <map>
#include <vector>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "uxtheme.lib")

// ═══════════════════════════════════════════════════════════════════════
// Brush Cache
// ═══════════════════════════════════════════════════════════════════════

static std::map<COLORREF, HBRUSH> s_brushCache;

HBRUSH Theme_GetBrush(COLORREF clr) {
    auto it = s_brushCache.find(clr);
    if (it != s_brushCache.end()) return it->second;
    HBRUSH br = CreateSolidBrush(clr);
    s_brushCache[clr] = br;
    return br;
}

void Theme_Cleanup() {
    for (auto& [clr, br] : s_brushCache) {
        if (br) DeleteObject(br);
    }
    s_brushCache.clear();
}

// ═══════════════════════════════════════════════════════════════════════
// Dark Mode Initialization
// ═══════════════════════════════════════════════════════════════════════

// Undocumented uxtheme APIs for dark mode menus (stable since Win10 1809)
typedef enum { AppMode_Default, AppMode_AllowDark, AppMode_ForceDark } PreferredAppMode;
typedef PreferredAppMode(WINAPI* fnSetPreferredAppMode)(PreferredAppMode);
typedef void(WINAPI* fnFlushMenuThemes)();

void Theme_Init() {
    // Enable dark mode for menus and common controls
    HMODULE hUxTheme = GetModuleHandleW(L"uxtheme.dll");
    if (hUxTheme) {
        auto SetPreferredAppMode = (fnSetPreferredAppMode)
            GetProcAddress(hUxTheme, MAKEINTRESOURCEA(135));
        auto FlushMenuThemes = (fnFlushMenuThemes)
            GetProcAddress(hUxTheme, MAKEINTRESOURCEA(136));
        if (SetPreferredAppMode) SetPreferredAppMode(AppMode_ForceDark);
        if (FlushMenuThemes) FlushMenuThemes();
    }
}

void Theme_ApplyDarkTitle(HWND hWnd) {
    // Dark title bar (Win10 1809+)
    BOOL darkMode = TRUE;
    DwmSetWindowAttribute(hWnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/,
        &darkMode, sizeof(darkMode));
    // Caption color (Win11 22H2+) — fails silently on older versions
    COLORREF captionClr = CLR_CRUST;
    DwmSetWindowAttribute(hWnd, 35 /*DWMWA_CAPTION_COLOR*/,
        &captionClr, sizeof(captionClr));
}

void Theme_ApplyDarkControl(HWND hCtrl) {
    SetWindowTheme(hCtrl, L"DarkMode_Explorer", nullptr);
}

// ═══════════════════════════════════════════════════════════════════════
// WM_CTLCOLOR Handler
// ═══════════════════════════════════════════════════════════════════════

HBRUSH Theme_HandleCtlColor(HDC hdc, HWND hCtrl, UINT msg, COLORREF bgColor) {
    COLORREF textColor = CLR_TEXT;

    switch (msg) {
    case WM_CTLCOLORDLG:
        return Theme_GetBrush(CLR_MANTLE);

    case WM_CTLCOLORSTATIC: {
        SetTextColor(hdc, textColor);
        SetBkColor(hdc, CLR_MANTLE);
        SetBkMode(hdc, TRANSPARENT);
        return Theme_GetBrush(CLR_MANTLE);
    }

    case WM_CTLCOLOREDIT: {
        SetTextColor(hdc, textColor);
        SetBkColor(hdc, bgColor);
        SetBkMode(hdc, OPAQUE);
        return Theme_GetBrush(bgColor);
    }

    case WM_CTLCOLORLISTBOX: {
        SetTextColor(hdc, textColor);
        SetBkColor(hdc, CLR_BASE);
        return Theme_GetBrush(CLR_BASE);
    }

    case WM_CTLCOLORBTN: {
        SetTextColor(hdc, textColor);
        SetBkMode(hdc, TRANSPARENT);
        return Theme_GetBrush(CLR_MANTLE);
    }

    case WM_CTLCOLORSCROLLBAR:
        return Theme_GetBrush(CLR_SURFACE0);

    default:
        SetTextColor(hdc, textColor);
        SetBkColor(hdc, bgColor);
        return Theme_GetBrush(bgColor);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Drawing Helpers
// ═══════════════════════════════════════════════════════════════════════

void Theme_FillRect(HDC hdc, const RECT& rc, COLORREF clr) {
    HBRUSH br = Theme_GetBrush(clr);
    ::FillRect(hdc, &rc, br);
}

void Theme_DrawText(HDC hdc, const RECT& rc, const std::wstring& text,
    COLORREF clr, HFONT hFont, UINT fmt)
{
    HFONT oldFont = (HFONT)SelectObject(hdc, hFont);
    SetTextColor(hdc, clr);
    SetBkMode(hdc, TRANSPARENT);
    RECT r = rc;
    DrawTextW(hdc, text.c_str(), (int)text.size(), &r, fmt);
    SelectObject(hdc, oldFont);
}

void Theme_DrawRoundRect(HDC hdc, const RECT& rc, COLORREF fill,
    COLORREF border, int radius)
{
    HBRUSH hBr = CreateSolidBrush(fill);
    HPEN hPen = CreatePen(PS_SOLID, 1, border);
    HBRUSH oldBr = (HBRUSH)SelectObject(hdc, hBr);
    HPEN oldPen = (HPEN)SelectObject(hdc, hPen);

    RoundRect(hdc, rc.left, rc.top, rc.right, rc.bottom, radius * 2, radius * 2);

    SelectObject(hdc, oldBr);
    SelectObject(hdc, oldPen);
    DeleteObject(hBr);
    DeleteObject(hPen);
}

// ═══════════════════════════════════════════════════════════════════════
// Owner-Draw Buttons
// ═══════════════════════════════════════════════════════════════════════

void Theme_DrawButton(LPDRAWITEMSTRUCT dis, bool isPrimary) {
    HDC hdc = dis->hDC;
    RECT rc = dis->rcItem;
    bool pressed = (dis->itemState & ODS_SELECTED) != 0;
    bool focused = (dis->itemState & ODS_FOCUS) != 0;
    bool disabled = (dis->itemState & ODS_DISABLED) != 0;

    // Determine colors
    COLORREF bgColor, textColor, borderColor;
    if (disabled) {
        bgColor = CLR_SURFACE0;
        textColor = CLR_OVERLAY0;
        borderColor = CLR_SURFACE1;
    } else if (isPrimary) {
        bgColor = pressed ? CLR_BTN_PRIMARY_HOV : CLR_BTN_PRIMARY;
        textColor = CLR_BTN_PRIMARY_TXT;
        borderColor = pressed ? CLR_BLUE : CLR_LAVENDER;
    } else {
        bgColor = pressed ? CLR_BTN_SEC_HOV : CLR_BTN_SECONDARY;
        textColor = CLR_TEXT;
        borderColor = pressed ? CLR_SURFACE2 : CLR_SURFACE1;
    }

    // Offset when pressed (3D press effect)
    if (pressed) {
        rc.top += 1;
    }

    // Draw rounded rect background
    Theme_DrawRoundRect(hdc, rc, bgColor, borderColor, 6);

    // Focus ring
    if (focused && !pressed) {
        RECT focusRc = { rc.left - 1, rc.top - 1, rc.right + 1, rc.bottom + 1 };
        HPEN hPen = CreatePen(PS_SOLID, 2, CLR_BLUE);
        HPEN oldPen = (HPEN)SelectObject(hdc, hPen);
        HBRUSH oldBr = (HBRUSH)SelectObject(hdc, GetStockObject(NULL_BRUSH));
        RoundRect(hdc, focusRc.left, focusRc.top, focusRc.right, focusRc.bottom, 14, 14);
        SelectObject(hdc, oldBr);
        SelectObject(hdc, oldPen);
        DeleteObject(hPen);
    }

    // Draw button text
    wchar_t text[256] = {};
    GetWindowTextW(dis->hwndItem, text, 255);
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, textColor);
    DrawTextW(hdc, text, -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
}

// ═══════════════════════════════════════════════════════════════════════
// Sidebar
// ═══════════════════════════════════════════════════════════════════════

static const SidebarNavItem s_navItems[] = {
    { 0,  L">",  L"Chat"         },
    { 1,  L"F",  L"Files"        },
    { 2,  L"S",  L"Search"       },
    { 3,  L"G",  L"Git"          },
    { 4,  L"W",  L"Web Fetch"    },
    { 5,  L"P",  L"Plugins"      },
    { 6,  L"L",  L"Permissions"  },
    { 7,  L"T",  L"Tasks"        },
    { 8,  L"*",  L"Settings"     },
};
static const int NAV_ITEM_COUNT = _countof(s_navItems);

const SidebarNavItem* Theme_GetNavItems(int& count) {
    count = NAV_ITEM_COUNT;
    return s_navItems;
}

void Theme_PaintSidebar(HDC hdc, const RECT& rc, HFONT hFont, HFONT hFontBold,
    int activeNav, const std::vector<std::wstring>& sessions)
{
    // Sidebar background
    Theme_FillRect(hdc, rc, CLR_MANTLE);

    // Right border
    RECT borderRc = { rc.right - 1, rc.top, rc.right, rc.bottom };
    Theme_FillRect(hdc, borderRc, CLR_SURFACE0);

    int y = 12;
    int padX = 16;
    int w = rc.right - rc.left;

    // ── App Title ──
    {
        RECT titleRc = { padX, y, w - padX, y + 28 };
        Theme_DrawText(hdc, titleRc, L"CluadeX", CLR_BLUE, hFontBold,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        y += 32;

        // Subtle subtitle
        RECT subRc = { padX, y, w - padX, y + 16 };
        Theme_DrawText(hdc, subRc, L"by Xman Studio", CLR_OVERLAY0, hFont,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        y += 28;
    }

    // ── Separator ──
    {
        RECT sepRc = { padX, y, w - padX, y + 1 };
        Theme_FillRect(hdc, sepRc, CLR_SURFACE0);
        y += 12;
    }

    // ── Navigation Items ──
    int navItemH = 32;
    for (int i = 0; i < NAV_ITEM_COUNT; i++) {
        RECT itemRc = { 4, y, w - 4, y + navItemH };
        bool isActive = (i == activeNav);

        if (isActive) {
            // Active background
            Theme_DrawRoundRect(hdc, itemRc, CLR_SURFACE0, CLR_SURFACE0, 6);

            // Left accent bar
            RECT accentRc = { 4, y + 4, 7, y + navItemH - 4 };
            Theme_DrawRoundRect(hdc, accentRc, CLR_BLUE, CLR_BLUE, 2);
        }

        // Icon (emoji) — draw using normal font
        RECT iconRc = { padX + 4, y, padX + 28, y + navItemH };
        Theme_DrawText(hdc, iconRc, s_navItems[i].icon,
            isActive ? CLR_BLUE : CLR_SUBTEXT0, hFont,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE);

        // Label
        RECT labelRc = { padX + 30, y, w - 8, y + navItemH };
        Theme_DrawText(hdc, labelRc, s_navItems[i].label,
            isActive ? CLR_TEXT : CLR_SUBTEXT0, hFont,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE);

        y += navItemH + 2;
    }

    // ── Sessions Section ──
    y += 8;
    {
        RECT sepRc = { padX, y, w - padX, y + 1 };
        Theme_FillRect(hdc, sepRc, CLR_SURFACE0);
        y += 8;

        RECT headerRc = { padX, y, w - padX, y + 18 };
        Theme_DrawText(hdc, headerRc, L"Recent Sessions", CLR_OVERLAY0, hFont,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        y += 24;
    }

    // Session items
    for (size_t i = 0; i < sessions.size() && i < 8; i++) {
        RECT sessRc = { padX, y, w - 8, y + 22 };
        Theme_DrawText(hdc, sessRc, sessions[i], CLR_SUBTEXT0, hFont,
            DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        y += 24;
    }
}
