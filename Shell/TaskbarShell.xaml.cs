using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// 任务栏壳层：只管 Win32 嵌入/贴附/拖拽/调宽/DPI/sticky/设置窗宿主，
/// 不知道任何音乐逻辑；模块经 ModuleHost 挂载。
/// M2 计划（A7 多显示器）：本类按可实例化设计——每屏 new 一条 Shell，
/// 模块服务单例跨条共享（届时把模块注册从构造函数迁出到 App 级）。
/// </summary>
public partial class TaskbarShell : Window
{
    private readonly DispatcherTimer _stickyTimer;
    private readonly ModuleHost _host = new();
    private readonly AppConfig _config = new();
    private SettingsWindow? _settingsWindow;
    private ShellSettingsSection? _shellSettingsSection;
    private TrayIcon? _trayIcon;

    private bool _isDragging;
    private bool _isResizing;
    private double _heightDip = 40;
    private const double MinWidth_ = 200;
    private const double MaxWidth_ = 900;

    // sticky 增量优化缓存：上次成功贴附时的关键参数快照。tick 时若父窗口仍是
    // tray + 这些值都没变，说明无需重新定位，直接早退——避免每 500ms 无脑
    // MoveWindow/改 Height（那串同步 Win32 + 布局是"抢 UI 线程"隐患的来源）。
    private IntPtr _lastTray = IntPtr.Zero;
    private int _lastTaskbarHeight = -1;
    private int _lastDpi = -1;
    private double _lastWidth = -1;
    private double _lastOffsetX = -1;

    /// <summary>非交互区双击（模块订阅；音乐模块用它拉起源程序）</summary>
    public event Action? BarDoubleClick;

    public ModuleHost Host => _host;

    /// <summary>壳持有的全局配置（设置分区 VM 绑定用；V1 单文件，M2 拆模块节）</summary>
    internal AppConfig Config => _config;

    /// <summary>壳的设置分区（布局/重置），常驻复用</summary>
    public FrameworkElement SettingsSectionView
        => _shellSettingsSection ??= new ShellSettingsSection(this);

    public TaskbarShell()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        Width = _config.Width;

        // V1 单模块：音乐（M2 起由配置驱动注册 + E6 槽位分配）
        _host.Register(new MusicModule(_config));

        _stickyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // sticky tick 守卫：设置窗打开期间一律不贴附。此前只靠 OpenSettings 里
        // _stickyTimer.Stop() 停，但 ContextMenu.Closed（右键条开菜单→选设置）会
        // 重新 Start——导致"从右键菜单开设置时 sticky 每 500ms 抢 UI 线程 →
        // resize 卡；从托盘开则不卡"的时好时坏现象（二分排除 Mica 后
        // 实锤：wbset10 无 Mica 仍卡）。标志位守卫无视 timer 谁 Start 都不 tick。
        _stickyTimer.Tick += (_, _) =>
        {
            if (_settingsWindow != null) return; // 设置窗开着：不抢 UI 线程
            if (!_isDragging && !_isResizing) StickToTaskbar();
        };

        Loaded += TaskbarShell_Loaded;
        Closing += TaskbarShell_Closing;

        MouseLeftButtonDown += Root_MouseLeftButtonDown;
        MouseDoubleClick += Root_MouseDoubleClick;
        MouseEnter += (_, _) => _host.BroadcastHover(true);
        MouseLeave += (_, _) => _host.BroadcastHover(false);
    }

    private void TaskbarShell_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

        // 右键条弹 WinForms 菜单（与托盘共用 BuildContextMenu）。不再用 WPF
        // ContextMenu——它关闭后干扰同进程窗口渲染（右键条开的设置窗 resize 卡），
        // 且需要一套 sticky Stop/Start + ESC 处理。WinForms 菜单独立于 WPF 体系，
        // 自带 ESC/失焦关闭，无 sticky 干扰。
        RootBorder.MouseRightButtonUp += (_, _) =>
        {
            var menu = BuildContextMenu();
            // 在鼠标位置弹出（屏幕物理坐标）
            menu.Show(System.Windows.Forms.Control.MousePosition);
        };

        StickToTaskbar();
        _stickyTimer.Start();

        // 模块挂载（顺序对齐原 MainWindow_Loaded：先完成贴附，再启动模块）
        _host.AttachAll(this, ModulePanel);

        // 托盘图标（右键：设置/退出；双击：设置）
        _trayIcon = new TrayIcon(this);
    }

    /// <summary>构建右键/托盘共用的 WinForms 菜单（每次新建，避免复用状态）。
    /// WinForms ContextMenuStrip 独立于 WPF 渲染/事件体系——这是它不干扰设置窗
    /// resize 的根本原因（WPF ContextMenu 会）。</summary>
    internal System.Windows.Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("设置...", null, (_, _) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    private void TaskbarShell_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon?.Dispose();
        _host.DetachAll();
        _config.Save();
    }

    // ===== 嵌入任务栏（TrafficMonitor 方案）：SetParent 成任务栏子窗口 =====
    // 子窗口天然在父窗口（任务栏）内部，永远不会被任务栏盖住，也不需要抢 z-order，无闪烁。
    // sticky timer 只负责：任务栏重建后重新嵌入 + 尺寸变化时校正位置。
    //
    // 增量优化（：500ms 全量执行过于无脑）：tick 先做廉价检查
    // （GetParent/GetClientRect/GetDpiForWindow + config 值比对），与上次快照
    // 全都相同则直接 return——不碰 MoveWindow/SetParent/改 Height。只有真正变化
    // （任务栏重建/换父、高度变、DPI 变、用户改了宽度或偏移）才执行重定位。
    // force=true：拖动/调宽/菜单关闭等确定需要立即校正的场景，跳过早退。
    private void StickToTaskbar(bool force = false)
    {
        var tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var parent = Win32.GetParent(hwnd);
        bool needReparent = parent != tray;

        if (needReparent)
        {
            // 首次嵌入 / 任务栏重建过：改成子窗口样式后 SetParent
            int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, (style & ~Win32.WS_POPUP) | Win32.WS_CHILD);

            // 清掉 topmost 扩展样式（子窗口无意义且可能干扰）
            int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex & ~Win32.WS_EX_TOPMOST);

            Win32.SetParent(hwnd, tray);
        }

        // 廉价检查：任务栏客户区高度 + DPI + config 宽度/偏移
        if (!Win32.GetClientRect(tray, out var rc)) return;
        int taskbarClientHeight = rc.Bottom - rc.Top;
        if (taskbarClientHeight <= 0) return;
        int dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi <= 0) dpi = 96;

        // 早退：非强制、未换父，且所有关键参数与上次快照一致 → 无需重定位
        if (!force && !needReparent
            && tray == _lastTray
            && taskbarClientHeight == _lastTaskbarHeight
            && dpi == _lastDpi
            && _config.Width == _lastWidth
            && _config.OffsetX == _lastOffsetX)
        {
            return;
        }

        PositionInTaskbar(hwnd, taskbarClientHeight, dpi);

        // 更新快照
        _lastTray = tray;
        _lastTaskbarHeight = taskbarClientHeight;
        _lastDpi = dpi;
        _lastWidth = _config.Width;
        _lastOffsetX = _config.OffsetX;
    }

    /// <summary>在任务栏客户区内定位（客户区坐标：左上角为原点）。
    /// 高度/DPI 由调用方（StickToTaskbar 检查阶段）传入，避免重复 Win32 调用。</summary>
    private void PositionInTaskbar(IntPtr hwnd, int taskbarClientHeight, int dpi)
    {
        double scale = dpi / 96.0;
        _heightDip = taskbarClientHeight / scale;

        int w = (int)(_config.Width * scale);
        int offsetX = (int)(_config.OffsetX * scale);

        Win32.MoveWindow(hwnd, offsetX, 0, w, taskbarClientHeight, true);

        // 同步 WPF 高度（WPF 内部布局用 DIP）
        if (Math.Abs(Height - _heightDip) > 0.5) Height = _heightDip;
    }

    // ===== 拖动定位（子窗口不能用 DragMove，手动累计位移）=====
    // 坐标源用 GetCursorPos 屏幕物理像素，不用 e.GetPosition：
    // 窗口被 SetParent 进任务栏后，WPF 的 DIP↔物理换算矩阵不可信（reparent 收不到
    // DPI 通知），用相对坐标会把窗口自身的移动混进来，比例失配时正反馈疯狂闪动。
    // 物理坐标全程只做一次换算（物理→DIP 存储），窗口移动必然 1:1 跟手。
    private bool _dragPending;
    private double _dragStartOffsetX;
    private int _dragStartCursorX;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;

        // 模块交互区（按钮/调宽手柄）不触发拖拽。
        // V1 形态：显式排除 Button/Thumb；M2 槽位模型时泛化为模块声明交互区。
        if (e.OriginalSource is DependencyObject d)
        {
            if (FindAncestor<System.Windows.Controls.Button>(d) != null) return;
            if (FindAncestor<Thumb>(d) != null) return;
        }

        _dragPending = true;
        _dragStartOffsetX = _config.OffsetX;
        Win32.GetCursorPos(out var pt);
        _dragStartCursorX = pt.X;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragPending) return;
        if (!Win32.GetCursorPos(out var pt)) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        int dpi = hwnd != IntPtr.Zero ? Win32.GetDpiForWindow(hwnd) : 96;
        if (dpi <= 0) dpi = 96;
        double scale = dpi / 96.0;

        // 屏幕物理像素差 → DIP（唯一一次换算），OffsetX 存 DIP
        double dxDip = (pt.X - _dragStartCursorX) / scale;
        _isDragging = true;
        _config.OffsetX = Math.Max(0, _dragStartOffsetX + dxDip);

        var tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && hwnd != IntPtr.Zero
            && Win32.GetClientRect(tray, out var rc))
        {
            int h = rc.Bottom - rc.Top;
            if (h > 0) PositionInTaskbar(hwnd, h, dpi);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragPending) return;

        _dragPending = false;
        ReleaseMouseCapture();
        if (_isDragging)
        {
            _isDragging = false;
            _config.Save();
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d) ?? System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ===== 宽度拖拽 =====
    private void RightThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _isResizing = true;
        double newWidth = Math.Clamp(_config.Width + e.HorizontalChange, MinWidth_, MaxWidth_);
        _config.Width = newWidth;
        Width = newWidth;
        StickToTaskbar(force: true);
    }

    private void LeftThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _isResizing = true;
        double delta = e.HorizontalChange;
        double newWidth = Math.Clamp(_config.Width - delta, MinWidth_, MaxWidth_);
        double actualDelta = _config.Width - newWidth;
        _config.Width = newWidth;
        _config.OffsetX = Math.Max(0, _config.OffsetX + actualDelta);
        Width = newWidth;
        StickToTaskbar(force: true);
    }

    private void EdgeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizing = false;
        _config.Save();
        StickToTaskbar(force: true);
        _shellSettingsSection?.RefreshWidth();
    }

    // ===== 非交互区双击：转发给订阅模块 =====
    private void Root_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            if (FindAncestor<System.Windows.Controls.Button>(d) != null) return;
            if (FindAncestor<Thumb>(d) != null) return;
        }

        BarDoubleClick?.Invoke();
    }

    // ===== 右键菜单：重置（壳设置分区回调用）=====
    internal void ResetPosition()
    {
        _config.OffsetX = 200;
        _config.Save();
        StickToTaskbar(force: true);
    }

    internal void ResetWidth()
    {
        _config.Width = 360;
        Width = 360;
        _config.Save();
        StickToTaskbar(force: true);
    }

    // ===== 设置窗口（壳层基础设施 A8：分区容器）=====
    /// <summary>打开设置窗（条右键菜单 / 托盘右键 / 托盘双击共用入口）</summary>
    internal void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this, _host);
        // 主窗口已嵌入任务栏（WS_CHILD 子窗口），不能作为 Owner（会抛异常）。
        // 设置窗口自身 Topmost=True 保证浮在任务栏之上。
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _stickyTimer.Start();
        };

        _settingsWindow.SourceInitialized += (_, _) => _settingsWindow.ApplyToolWindowStyle();
        _stickyTimer.Stop();
        _settingsWindow.Show();
    }

    /// <summary>重开设置窗（材质切换用——backdrop 是窗口级一次性设置，重开干净生效）</summary>
    internal void ReopenSettings()
    {
        if (_settingsWindow != null)
        {
            // Close 同步触发 Closed 处理器：_settingsWindow = null + sticky 重启
            _settingsWindow.Close();
        }
        OpenSettings(); // null 时新建（材质已在 config 里，新窗构造时读取）
    }

    /// <summary>退出应用（条右键菜单 / 托盘右键共用入口）</summary>
    internal void ExitApp()
    {
        _config.Save();
        Application.Current.Shutdown();
    }
}
