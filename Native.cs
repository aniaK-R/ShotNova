using System;
using System.Runtime.InteropServices;

namespace ShotNova;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Barra de título escura (Windows 10 20H1+ / 11). Ignora falhas.</summary>
    public static void UseDarkTitleBar(System.Windows.Window w)
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            var on = 1;
            DwmSetWindowAttribute(h, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = 0x001B1818;                                    // COLORREF 0x00BBGGRR (#18181B)
            DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR (Win11)
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
