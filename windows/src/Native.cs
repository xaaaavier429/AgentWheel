using System;
using System.Runtime.InteropServices;

namespace AgentWheel;

/// <summary>Win32 calls: global mouse / Shift state, monitors and window styles.</summary>
public static class Native
{
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_SHIFT = 0x10;
    public const int SM_SWAPBUTTON = 23;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int processId);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>The primary mouse button, honouring the "swap buttons" setting.</summary>
    public static bool LeftDown
    {
        get
        {
            int key = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }
    }

    public static bool ShiftDown => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    public static POINT CursorPos
    {
        get
        {
            GetCursorPos(out var p);
            return p;
        }
    }

    /// <summary>
    /// True when the point is over File Explorer, the desktop or a file dialog — the places file drags start from.
    /// </summary>
    public static bool IsShellWindowAt(POINT p)
    {
        try
        {
            IntPtr root = GetAncestor(WindowFromPoint(p), 2 /* GA_ROOT */);
            var name = new System.Text.StringBuilder(64);
            GetClassName(root, name, name.Capacity);
            return name.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" or "#32770";
        }
        catch
        {
            return true;
        }
    }

    public sealed class MonitorInfo
    {
        public RECT Bounds;
        public RECT Work;
        /// <summary>Physical pixels per WPF unit on this monitor (1.0 at 100 %, 1.5 at 150 % …).</summary>
        public double Scale = 1;
    }

    public static MonitorInfo MonitorAt(POINT p)
    {
        IntPtr h = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(h, ref info);
        double scale = 1;
        try
        {
            if (GetDpiForMonitor(h, 0, out uint dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
        }
        catch
        {
            // shcore missing: assume 100 %.
        }
        return new MonitorInfo { Bounds = info.rcMonitor, Work = info.rcWork, Scale = scale };
    }

    /// <summary>Hides the window from Alt-Tab and the taskbar; optionally lets clicks pass through.</summary>
    public static void MakeToolWindow(IntPtr hwnd, bool clickThrough, bool noActivate)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        if (clickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Puts a window on top of everything at an exact pixel rectangle.</summary>
    public static void Place(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
