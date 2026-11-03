using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CluadeX;

/// <summary>
/// Ctrl+Alt+End, from any app: stop every running chat at once, no question asked. An agent driving the desktop moves
/// the user's real mouse and keyboard, so CluadeX's Stop button may be hard to reach — the panic key must work while
/// another window is in front (owner 2026-11-02: the desktop tools need a way out). A key another program already
/// holds is simply not registered; the Stop button still works.
/// </summary>
public partial class MainWindow
{
    private const int StopHotkeyId = 0xC1A0;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_END = 0x23;
    private const int WM_HOTKEY = 0x0312;
    private HwndSource? _hotkeySource;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        if (_hotkeySource == null) return;
        if (!RegisterHotKey(handle, StopHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_END))
        {
            System.Diagnostics.Debug.WriteLine($"[hotkey] Ctrl+Alt+End is taken (error {Marshal.GetLastWin32Error()})");
            _hotkeySource = null;
            return;
        }
        _hotkeySource.AddHook(HotkeyHook);
        Closed += (_, _) =>
        {
            UnregisterHotKey(handle, StopHotkeyId);
            _hotkeySource?.RemoveHook(HotkeyHook);
            _hotkeySource = null;
        };
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == StopHotkeyId)
        {
            handled = true;
            _viewModel.ChatVM.StopAllRuns();
        }
        return IntPtr.Zero;
    }
}
