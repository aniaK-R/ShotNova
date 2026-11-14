using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ShotNova;

/// <summary>Funções do Windows usadas pela gravação de vídeo e pelo seletor de janela.</summary>
internal static class Win
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    /// <summary>Retângulo visível da janela (sem a sombra invisível), em pixels físicos.</summary>
    public static bool TryFrameBounds(IntPtr h, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return false; // EXTENDED_FRAME_BOUNDS
        rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    public static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, 14, out int v, sizeof(int)) == 0 && v != 0; // DWMWA_CLOAKED

    /// <summary>
    /// Janela flutuante que não rouba foco, não aparece no Alt+Tab e é excluída da captura de tela
    /// (assim a barra de gravação não aparece no vídeo).
    /// </summary>
    public static void MakeOverlay(IntPtr h, bool clickThrough)
    {
        const int GWL_EXSTYLE = -20;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        ex |= 0x80 | 0x08000000;           // TOOLWINDOW | NOACTIVATE
        if (clickThrough) ex |= 0x20;      // TRANSPARENT (cliques passam)
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowDisplayAffinity(h, 0x11); // WDA_EXCLUDEFROMCAPTURE
    }
}
