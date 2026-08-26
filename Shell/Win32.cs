using System;
using System.Runtime.InteropServices;

namespace TaskbarMusic;

/// <summary>
/// Win32 互操作集中地：所有 P/Invoke 声明收口在此，壳与设置窗共用。
/// 模块原则上不碰 Win32（模块只经 TaskbarShell 的事件/属性拿环境信息）。
/// </summary>
internal static class Win32
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_CHILD = 0x40000000;

    // ===== 任务栏重启逃逸（父窗口销毁通知）=====
    /// <summary>父窗口销毁前发给子窗口的通知；wParam 低 16 位是事件（WM_DESTROY=父正在销毁）</summary>
    internal const int WM_PARENTNOTIFY = 0x0210;
    internal const int WM_DESTROY = 0x0002;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ===== DWM（设置窗 Mica 背景用）=====
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE：Win11 22H2+ 系统背景材质（Mica/Acrylic）</summary>
    internal const int DWMWA_SYSTEM_BACKDROP_TYPE = 38;
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：DWM 绘制层（标题栏/Mica/Acrylic 染色）
    /// 的深浅开关。WPF 主题字典只管控件层，DWM 层深浅必须用这个属性单独喂——
    /// 不设的话深色主题下 Mica/Acrylic 渲染成白色（2026-08-26 实锤）</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS { public int Left, Right, Top, Bottom; }

    /// <summary>把 DWM 边框帧扩展进客户区——Mica 生效的硬性前提（Acrylic 不需要）。
    /// margins 全 -1 = 整窗扩展（sheet of glass）。</summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
