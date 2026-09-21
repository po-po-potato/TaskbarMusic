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
/// A7 多显示器：每屏 new 一条 Shell（monitorKey 绑定目标任务栏——主屏
/// Shell_TrayWnd / 副屏 Shell_SecondaryTrayWnd）；配置/服务单例跨条共享
/// （AppConfig.Shared + MediaService.Shared + 番茄钟 static 状态机），
/// 每条各挂一套模块 View。托盘图标与设置窗仅主条（IsPrimary）。
/// </summary>
public partial class TaskbarShell : Window
{
    private readonly DispatcherTimer _stickyTimer;
    private readonly ModuleHost _host = new();
    private readonly AppConfig _config = new();
    private ShellSettingsSection? _shellSettingsSection;
    private TrayIcon? _trayIcon;

    /// <summary>A7：本条绑定的显示器设备名（\\.\DISPLAY1 形式）</summary>
    private readonly string _monitorKey;

    /// <summary>A7：本条绑定的显示器设备名（ShellManager 登记/对账用）</summary>
    internal string MonitorKey => _monitorKey;

    /// <summary>A7：是否主屏条（托盘图标/设置窗/SMTC 监视器归属主条）</summary>
    internal bool IsPrimary { get; }

    /// <summary>A7：本条的横向偏移（per-monitor 存储；Width 全局共享）</summary>
    private double OffsetXCurrent => _config.OffsetXOf(_monitorKey, IsPrimary);

    private bool _isDragging;
    private bool _isResizing;
    private double _heightDip = 40;
    private const double MinWidth_ = 200;
    private const double MaxWidth_ = 900;

    // ===== 任务栏重启存活机制 =====
    // 条是 Shell_TrayWnd 的子窗口（WS_CHILD），Win32 销毁父窗口时会递归销毁子窗口
    // ——任务栏重启（explorer 重建）时条窗口被带走，进而 OnLastWindowClose 把整个
    // 进程退掉，条从此消失（2026-08-26 "任务栏重启后不见了"根因）。
    // 双层修复：
    // ① WM_PARENTNOTIFY(WM_DESTROY) 逃逸：父销毁通知到达的瞬间改回 WS_POPUP +
    //    SetParent(null) 脱离子窗口链——窗口保活（模块/托盘/媒体状态零丢失），
    //    sticky timer 稍后自动找新任务栏重新嵌入；
    // ② 兜底重建：逃逸万一失败窗口仍被销毁（Closed 且非用户退出）→ 异步 new 新壳
    //    （配合 App.ShutdownMode=OnExplicitShutdownOnly 进程不再被带走）。
    private bool _explicitExit;          // 用户显式退出（托盘/右键"退出"）
    private bool _pendingShow = false;   // 逃逸后等重新嵌入任务栏再显示（仅逃逸路径用）

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

    /// <summary>壳的设置分区（布局/重置），常驻复用。
    /// 通过 ShellManager.BuildSectionList 暴露给设置窗，跟随 TrayOwner 动态。
    /// 构造无参（2026-09-09 解耦：不再绑宿主条实例，所有事件转发 ShellManager）</summary>
    internal FrameworkElement ShellSectionForSettings
        => _shellSettingsSection ??= new ShellSettingsSection();

    /// <summary>A7 多显示器：绑定目标屏建条。config 用进程单例（多条共享同一实例，
    /// 否则各自 Load/Save 互相覆盖）；每条各 new 一套模块实例（WPF View 不可跨视觉树），
    /// 服务层单例共享（MediaService/LyricService/番茄钟 static 状态机）。</summary>
    public TaskbarShell(string monitorKey, bool isPrimary)
    {
        InitializeComponent();
        _monitorKey = monitorKey;
        IsPrimary = isPrimary;

        _config = AppConfig.Shared;
        Width = _config.Width;
        // 注意：不能在这里 Visibility=Hidden 隐藏——WPF 隐藏窗口不创建 HWND，
        // Loaded/StickToTaskbar/托盘全不会跑（2026-08-26 "rebuild 后不显示"实锤）。
        // 首次启动保持默认显示；只有任务栏逃逸路径（Win32 层 SW_HIDE）才延迟显示。

        // 模块注册（组合根）：配置驱动的显示状态/顺序由 ModuleHost 槽位模型管理，
        // 挂新模块仅需一行 Register（E6 出口标准：零壳层逻辑改动）
        _host.Register(new MusicModule(_config));
        // F2 番茄钟（M2 首个真实第二模块）：E6 单屏轮播的真实消费者——验证切换
        // 过渡 + 常驻模型（滚走计时不断）；A7 static 状态机跨条同步
        _host.Register(new PomodoroModule(_config));
        // A3 天气（2026-09-21 定稿）：Open-Meteo 免费无 key，当前天气 + 今明后三天，
        // 30min 定时 + 断网缓存；条内三元素（图标/温度/描述），低频信息归 hover 浮层
        _host.Register(new WeatherModule(_config));
        // A4 财经（2026-09-21 定稿）：单股静态轮播（行情 5s 轮询 + 展示 8s 轮换解耦），
        // 分时 sparkline 红涨绿跌；数据源腾讯 qt.gtimg / ifzq.gtimg（合规遗留决策点）
        _host.Register(new StocksModule(_config));
        // A6 网速监控（2026-09-18 定稿）：IP Helper 累计字节差分（TrafficMonitor 同源），
        // 1s 采样 + 回绕守卫 + 多网卡取流量最大；A7 static 采样器跨条共享
        _host.Register(new NetSpeedModule(_config));
        // A7 倒数日（2026-09-18 定稿）：纪念日+手动倒计时合并，最近一项 + 天数颜色分层
        _host.Register(new CountdownModule(_config));
        // dev 验证后门：TBM_DEMO_MODULE=1 挂空壳模块（E6 双模块槽位实证用，不进发布形态）
        if (Environment.GetEnvironmentVariable("TBM_DEMO_MODULE") == "1")
            _host.Register(new DemoModule());

        _stickyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // sticky tick 守卫：设置窗打开期间一律不贴附。此前只靠 OpenSettings 里
        // _stickyTimer.Stop() 停，但 ContextMenu.Closed（右键条开菜单→选设置）会
        // 重新 Start——导致"从右键菜单开设置时 sticky 每 500ms 抢 UI 线程 →
        // resize 卡；从托盘开则不卡"的时好时坏现象（二分排除 Mica 后
        // 实锤：wbset10 无 Mica 仍卡）。标志位守卫无视 timer 谁 Start 都不 tick。
        _stickyTimer.Tick += (_, _) =>
        {
            if (ShellManager.SettingsWindowOpen || ShellManager.SmtcMonitorOpen) return; // 设置窗/SMTC 窗开着：不抢 UI 线程
            if (!_isDragging && !_isResizing) StickToTaskbar();
        };

        Loaded += TaskbarShell_Loaded;
        Closing += TaskbarShell_Closing;

        // 兜底重建：逃逸万一失败，窗口被任务栏销毁带走（Closed 且非用户退出）
        // → 交 ShellManager 决定是否重建（该屏仍在勾选集合才重建；A7 前
        // 直接 new TaskbarShell()，多屏后建/关条的管理权收拢到 manager）
        Closed += (_, _) =>
        {
            if (_explicitExit) return;
            // 设置窗不在这里连带关闭：用户在设置窗里增删显示器勾选会关掉宿主条，
            // 连带关 = "设置窗自杀"（2026-09-09 实锤）。改由 ShellManager 迁移——
            // 立即关旧窗（防双开），替代条就绪后新宿主重开
            ShellManager.OnShellClosed(this);
        };

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

        // 任务栏销毁逃逸钩子：父窗口 DestroyWindow 前会向子窗口发
        // WM_PARENTNOTIFY(低16位=WM_DESTROY)——此刻脱离子窗口链即保活窗口
        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.AddHook(ShellWndProc);

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

        // 托盘图标（右键：设置/退出；双击：设置）——单实例，归属 manager 收口：
        // 主条优先，只挂副屏时跟副条走（Loaded 早于 manager 重算时幂等兜底）
        if (ShellManager.ShouldOwnTray(this)) AttachTray();

        // dev 验证后门：TBM_AUTO_OPEN_SETTINGS=1 启动后自动开设置窗
        // （免手动托盘交互，冒烟验证设置窗构建路径用）
        if (Environment.GetEnvironmentVariable("TBM_AUTO_OPEN_SETTINGS") == "1")
            Dispatcher.BeginInvoke(ShellManager.OpenSettings, System.Windows.Threading.DispatcherPriority.Background);

        // dev 验证后门：TBM_AUTO_OPEN_SMTC=1 启动后自动开 SMTC 监视器
        if (Environment.GetEnvironmentVariable("TBM_AUTO_OPEN_SMTC") == "1")
            Dispatcher.BeginInvoke(ShellManager.OpenSmtcMonitor, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>构建右键/托盘共用的 WinForms 菜单（每次新建，避免复用状态）。
    /// WinForms ContextMenuStrip 独立于 WPF 渲染/事件体系——这是它不干扰设置窗
    /// resize 的根本原因（WPF ContextMenu 会）。</summary>
    internal System.Windows.Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        // 设置窗已升级为 ShellManager 进程级单例（不再绑宿主条），所有入口统一转发
        menu.Items.Add("设置...", null, (_, _) => ShellManager.OpenSettings());
        menu.Items.Add("SMTC 监视器...", null, (_, _) => ShellManager.OpenSmtcMonitor());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    /// <summary>建托盘（幂等）：EnsureTray 宿主迁移用</summary>
    internal void AttachTray()
    {
        _trayIcon ??= new TrayIcon(this);
    }

    /// <summary>拆托盘（幂等）：宿主转移/非宿主条用</summary>
    internal void DetachTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
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
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // A7 per-monitor 任务栏查找：正常情况沿用上次快照（GetParent 匹配即免查找），
        // 只有换父（首次嵌入/任务栏重建/逃逸后）才全量找——EnumWindows 副屏匹配
        // 的成本只发生在重建路径，500ms tick 零额外开销。
        var tray = _lastTray;
        bool needReparent = tray == IntPtr.Zero || Win32.GetParent(hwnd) != tray;
        if (needReparent)
        {
            tray = FindTaskbarFor(_monitorKey, IsPrimary);
            if (tray == IntPtr.Zero) return; // 本屏任务栏暂不存在（explorer 重启中/显示器拔了）
        }

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
            && OffsetXCurrent == _lastOffsetX)
        {
            return;
        }

        PositionInTaskbar(hwnd, taskbarClientHeight, dpi);

        // 逃逸重嵌完成后恢复显示（仅逃逸路径：_pendingShow 由 EscapeFromDyingParent 置位，
        // 对称地用 Win32 层恢复——逃逸时的隐藏也是 Win32 层，不碰 WPF Visibility）
        if (_pendingShow)
        {
            _pendingShow = false;
            Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        }

        // 更新快照
        _lastTray = tray;
        _lastTaskbarHeight = taskbarClientHeight;
        _lastDpi = dpi;
        _lastWidth = _config.Width;
        _lastOffsetX = OffsetXCurrent;
    }

    /// <summary>A7：找本屏的任务栏窗口。主屏 = Shell_TrayWnd；副屏 = 枚举
    /// Shell_SecondaryTrayWnd（Windows 每个副屏任务栏一个该类窗口），
    /// 用窗口所在显示器设备名匹配。找不到返回 IntPtr.Zero（调用方早退等下轮）。</summary>
    private static IntPtr FindTaskbarFor(string monitorKey, bool isPrimary)
    {
        var primary = Win32.FindWindow("Shell_TrayWnd", null);
        if (isPrimary) return primary;

        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (found != IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            Win32.GetClassName(hwnd, sb, 64);
            if (sb.ToString() == "Shell_SecondaryTrayWnd"
                && Win32.MonitorDeviceOf(hwnd) == monitorKey)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ===== 任务栏重启逃逸 =====

    /// <summary>窗口消息钩子：监听父（任务栏）销毁通知，触发逃逸</summary>
    private IntPtr ShellWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_PARENTNOTIFY && (wParam.ToInt64() & 0xFFFF) == Win32.WM_DESTROY)
        {
            EscapeFromDyingParent(hwnd);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 父（任务栏）正在销毁：立即改回 WS_POPUP 并 SetParent(null) 脱离子窗口链，
    /// 避免被 DestroyWindow 递归销毁。窗口保活（模块/托盘/媒体状态零丢失），
    /// sticky timer 自动找新任务栏重新嵌入后经 _pendingShow 恢复显示。
    /// 注意：处于父销毁流程的 hook 回调中——只用 Win32 调用（SetParent/ShowWindow），
    /// 不碰 WPF Visibility（会触发布局/消息重入，有风险）。
    /// </summary>
    private void EscapeFromDyingParent(IntPtr hwnd)
    {
        int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, (style & ~Win32.WS_CHILD) | Win32.WS_POPUP);
        Win32.SetParent(hwnd, IntPtr.Zero);
        Win32.ShowWindow(hwnd, Win32.SW_HIDE);
        _pendingShow = true;

        // 复位交互状态；幂等重启 sticky（设置窗打开路径曾 Stop 过；
        // tick 守卫会跳过设置窗开启期间的 tick，关闭后自然恢复重嵌）
        _dragPending = false;
        _isDragging = false;
        _isResizing = false;
        _stickyTimer.Start();
    }

    /// <summary>在任务栏客户区内定位（客户区坐标：左上角为原点）。
    /// 高度/DPI 由调用方（StickToTaskbar 检查阶段）传入，避免重复 Win32 调用。</summary>
    private void PositionInTaskbar(IntPtr hwnd, int taskbarClientHeight, int dpi)
    {
        double scale = dpi / 96.0;
        _heightDip = taskbarClientHeight / scale;

        int w = (int)(_config.Width * scale);
        int offsetX = (int)(OffsetXCurrent * scale);

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
        _dragStartOffsetX = OffsetXCurrent;
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
        _config.SetOffsetX(_monitorKey, Math.Max(0, OffsetXCurrent + actualDelta));
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

        // E6 常驻模型输入路由：BarDoubleClick 是广播事件，MusicModule 滚走仍订阅着——
        // 番茄钟等其他模块显示时双击条空白区会误拉起音乐源程序（2026-09-08 实锤）。
        // 修复：仅当前显示模块是订阅者（音乐）时才广播。V1 直连风格（is MusicModule），
        // 与 RefreshModulesTextStyle 同款过渡写法，M2 后续统一改 CurrentModule 路由。
        if (_host.CurrentModule is not MusicModule) return;
        BarDoubleClick?.Invoke();
    }

    // ===== 右键菜单：重置（壳设置分区回调用）=====
    internal void ResetPosition()
    {
        _config.SetOffsetX(_monitorKey, 200); // A7：只重置本条
        _config.Save();
        StickToTaskbar(force: true);
    }

    /// <summary>重置宽度（条右键菜单：重置回调）</summary>
    internal void ResetWidth()
    {
        _config.Width = 360;
        Width = 360;
        _config.Save();
        StickToTaskbar(force: true);
    }

    /// <summary>设置窗改字体后刷新条上文字（V1 单模块直连；M2 槽位模型时改广播）</summary>
    internal void RefreshModulesTextStyle()
    {
        foreach (var module in _host.Modules)
            if (module is MusicModule music)
                music.ApplyTextStyle();
    }

    // ===== 设置窗口 / SMTC 监视器（已上移 ShellManager 进程级单例）=====
    // 原实例字段 + OpenSettings / ReopenSettings / OpenSmtcMonitor 已移除——
    // 设置窗与条在 Win32 层本就独立（无 Owner/WS_CHILD 关联），统一归 ShellManager 管。
    // 分区内容跟 TrayOwner 动态刷新；宿主条销毁时窗不关，只换内容。

    /// <summary>退出应用（条右键菜单 / 托盘右键共用入口）。
    /// 显式退出标志必须先于 Shutdown 设置——Closed 兜底重建靠它区分
    /// "用户退出（不重建）"与"任务栏重启窗口被带走（要重建）"</summary>
    internal void ExitApp()
    {
        _explicitExit = true;
        ShellManager.MarkExiting(); // A7：进程退出中，Closed 兜底重建全部静默
        _config.Save();
        Application.Current.Shutdown();
    }
}
