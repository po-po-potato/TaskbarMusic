using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>浮层关闭模式（E5）</summary>
public enum OverlayDismiss
{
    /// <summary>失焦即隐：浮层被激活过后失去焦点（Deactivated）即隐藏。
    /// 适合搜索框等需要键盘输入的浮层（F1.2）。</summary>
    LightDismiss = 0,

    /// <summary>hover 自动隐：鼠标离开浮层（宽限期后）即隐藏，全程不抢焦点。
    /// 适合信息展示类 hover 浮层（F2.4）。</summary>
    HoverDismiss = 1,
}

/// <summary>
/// E5 通用浮层窗（PRD 4.2 E5 第一版）：Topmost / 无边框 / 透明圆角 / 按模式自动隐藏。
///
/// 锚定：条上方、水平居中于条；条在屏幕顶部任务栏（上方放不下）时翻到条下方；
/// 水平/垂直方向夹取到条所在显示器的工作区。
///
/// 定位走物理像素（GetWindowRect + SetWindowPos），不经 WPF DIP 换算——
/// 条是 reparent 进任务栏的子窗口，WPF 的 DIP↔物理矩阵不可信
/// （与壳层拖拽坐标同结论，见 TaskbarShell 拖动注释）。
///
/// 用法（消费方 = 模块）：
/// - LightDismiss：SetContent(...) → ShowAbove(shell)；失焦自动隐藏。
/// - HoverDismiss：OnHoverChanged(true) → ShowAbove(shell)；
///   OnHoverChanged(false) → GraceHide()（宽限期内鼠标进入浮层则取消，
///   支持"条 → 浮层"的连续移动；浮层自身 MouseLeave 同样走宽限隐藏）。
/// </summary>
public sealed class OverlayWindow : Window
{
    /// <summary>浮层与条的垂直间距（DIP）</summary>
    private const int AnchorGapDip = 8;

    /// <summary>hover 宽限期默认值（ms）：条↔浮层之间移动的容错窗口</summary>
    private const int GraceMsDefault = 300;

    private readonly Border _root;
    private readonly OverlayDismiss _mode;
    private readonly DispatcherTimer _graceTimer;
    private TaskbarShell? _anchorShell;

    public OverlayWindow(OverlayDismiss mode = OverlayDismiss.LightDismiss)
    {
        _mode = mode;

        // 窗口形态：无边框 + 透明（圆角由内容 Border 画）+ 置顶 + 不进任务栏
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // hover 模式不抢焦点（键盘/前台应用不受干扰）；LightDismiss 模式需要焦点（输入）
        ShowActivated = mode == OverlayDismiss.LightDismiss;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 浮层面板：深色圆角 + 细描边（与条上模块底色 #E6202030 同族，透明度更高）
        _root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x25, 0x30)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
        };
        Content = _root;

        // 不进 Alt+Tab（ShowInTaskbar=false 不够，还要 TOOLWINDOW 扩展样式）
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
        };

        // 关闭模式
        _graceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GraceMsDefault) };
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            if (!IsMouseOver) HideOverlay(); // 宽限期到点时鼠标已回到浮层上则不隐藏
        };

        if (mode == OverlayDismiss.HoverDismiss)
        {
            MouseEnter += (_, _) => _graceTimer.Stop();
            MouseLeave += (_, _) => _graceTimer.Start();
        }
        else
        {
            Deactivated += (_, _) => HideOverlay();
        }

        // 首次显示：布局完成后才能拿到 ActualWidth/Height，ContentRendered 兜底定位
        ContentRendered += (_, _) => Reposition();
    }

    /// <summary>设置浮层内容（消费方自建布局；内容不需要自带背景/圆角/边距）</summary>
    public void SetContent(FrameworkElement content) => _root.Child = content;

    /// <summary>浮层当前是否可见（消费方判断要不要刷新内容）</summary>
    public bool IsOverlayVisible => Visibility == Visibility.Visible;

    /// <summary>显示并锚定到条上方（水平居中于条）。重复调用 = 重锚定（内容变化后重定位）</summary>
    public void ShowAbove(TaskbarShell shell)
    {
        _anchorShell = shell;
        if (Visibility != Visibility.Visible)
        {
            Show();
            MediaService.Trace($"[OV] show mode={_mode}"); // 诊断：滚轮卡住排查（2026-09-21）
        }
        // 复用显示时布局已完成，直接重定位（首次显示由 ContentRendered 兜底）
        Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
    }

    /// <summary>hover-out 宽限隐藏（HoverDismiss 模式）：条上鼠标离开时调用；
    /// 宽限期内鼠标进入浮层则取消——支持"条 → 浮层"的连续移动。</summary>
    public void GraceHide(int ms = GraceMsDefault)
    {
        if (_mode != OverlayDismiss.HoverDismiss) return;
        _graceTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _graceTimer.Stop();
        _graceTimer.Start();
    }

    /// <summary>取消宽限隐藏（鼠标回到条上时调用）</summary>
    public void CancelGraceHide() => _graceTimer.Stop();

    /// <summary>隐藏浮层（窗口保留复用，不销毁）</summary>
    public void HideOverlay()
    {
        _graceTimer.Stop();
        Hide();
        MediaService.Trace("[OV] hide"); // 诊断：滚轮卡住排查（2026-09-21）
    }

    // ===== 锚定定位（物理像素，与 WPF DIP 体系隔离）=====

    private void Reposition()
    {
        if (_anchorShell == null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var barHwnd = new WindowInteropHelper(_anchorShell).Handle;
        if (hwnd == IntPtr.Zero || barHwnd == IntPtr.Zero) return;
        if (!Win32.GetWindowRect(barHwnd, out var bar)) return;

        int dpi = Win32.GetDpiForWindow(barHwnd);
        if (dpi <= 0) dpi = 96;
        double scale = dpi / 96.0;

        int w = (int)Math.Ceiling(ActualWidth * scale);
        int h = (int)Math.Ceiling(ActualHeight * scale);
        if (w <= 0 || h <= 0) return; // 布局未完成（ContentRendered 兜底会再触发）

        int gap = (int)Math.Round(AnchorGapDip * scale);
        int x = bar.Left + ((bar.Right - bar.Left) - w) / 2; // 水平居中于条
        int y = bar.Top - gap - h;                            // 默认在条上方

        // 夹取到条所在显示器工作区：顶部任务栏（上方放不下）翻到条下方；水平贴边收
        var mon = Win32.MonitorFromWindow(barHwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>(),
        };
        if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref mi))
        {
            var work = mi.rcWork;
            if (y < work.Top) y = bar.Bottom + gap;
            if (x < work.Left) x = work.Left;
            if (x + w > work.Right) x = work.Right - w;
            if (y + h > work.Bottom) y = work.Bottom - h;
        }

        // 只移动：尺寸交给 SizeToContent，z-order 交给 Topmost 属性，不抢激活
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
    }
}
