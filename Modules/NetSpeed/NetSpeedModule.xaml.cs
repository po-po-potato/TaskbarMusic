using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// A6 网速监控模块（设计稿定稿 2026-09-18：无曲线精简版）。
///
/// 采样路线（与 TrafficMonitor 同源，源码查证 2026-09-18）：
/// - IP Helper 累计字节差分（C# 走 NetworkInterface.IPv4Statistics，
///   底层 GetIfEntry 同源；不走 PerformanceCounter——类别名在中文 Windows
///   会本地化，是已知坑）。
/// - 多网卡选择：取累计流量（rx+tx）最大者（TrafficMonitor 方案，比
///   "物理/虚拟过滤"鲁棒——虚拟网卡流量自然小于在用物理网卡）。
/// - 守卫（吸收自 TrafficMonitor）：首次采样 / 累计数回绕（last &gt; cur，
///   网卡重置或重连）/ 选中网卡变化 → 本轮速率归零，不出负数或天文数字。
/// - 按真实采样间隔归一：diff × 1000 / elapsedMs，容忍 DispatcherTimer 抖动。
///
/// 今日累计：有效差分累加，TodayKey 跨天清零；落盘节流 60s（避免每秒写盘）。
/// A7 多显示器：采样器 static（单一真源），StateChanged 广播刷新各条 UI。
/// </summary>
public partial class NetSpeedModule : UserControl, ITaskbarModule
{
    public string Id => "netspeed";
    public string DisplayName => "网速";
    public FrameworkElement View => this;

    private NetSpeedSettingsSection? _settingsSection;
    public FrameworkElement? SettingsSection => _settingsSection ??= new NetSpeedSettingsSection(this);

    private TaskbarShell? _shell;
    private OverlayWindow? _overlay;

    public NetSpeedModule(AppConfig config)
    {
        InitializeComponent();
        _ = config; // 采样器读 AppConfig.Shared（static 单一真源），构造参数仅为对齐模块契约
    }

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        _shell = shell;
        StateChanged += OnStateChanged;
        _attachCount++;
        RollTodayIfNeeded();
        EnsureSampler();
        OnStateChanged(); // 首帧立即渲染（字体/风格应用不等首个 1s tick）
        MediaService.Trace($"[NET] attached ({_attachCount} bar)");
    }

    public void OnDetach()
    {
        _overlay?.HideOverlay();
        StateChanged -= OnStateChanged;
        _attachCount = Math.Max(0, _attachCount - 1);
        if (_attachCount == 0)
        {
            _sampler?.Stop();
            SaveToday(); // 最后一条卸载时落盘今日累计
            MediaService.Trace("[NET] detached (last bar) sampler stopped");
        }
        else
        {
            MediaService.Trace($"[NET] detached ({_attachCount} bar remains)");
        }
    }

    public void OnHoverChanged(bool hovering)
    {
        if (hovering) ShowHoverOverlay();
        else _overlay?.GraceHide();
    }

    private void OnStateChanged()
    {
        var cfg = AppConfig.Shared;

        // 可设置项应用（每秒 tick 幂等刷新——设置改动下一秒生效，无需事件广播）
        // 字体：数字与单位统一接全局「界面字体」（条级无全局字体机制，XAML 默认
        // Segoe UI 不跟随 config，必须显式应用到全部四个 TextBlock；
        // 位置稳定性由固定宽度容器保证——上行组 104 + 数字区 44，不依赖等宽字体。
        // 仅字体串变化时才重建 FontFamily，避免每秒 new）
        if (_lastFontApplied != cfg.FontFamily)
        {
            try
            {
                var family = new FontFamily(cfg.FontFamily);
                UpNum.FontFamily = family;
                UpUnit.FontFamily = family;
                DownNum.FontFamily = family;
                DownUnit.FontFamily = family;
            }
            catch { /* 非法字体名保持默认 */ }
            _lastFontApplied = cfg.FontFamily;
        }

        // 箭头风格：0 实心 / 1 线条 / 2 无
        bool solid = cfg.NetSpeedArrowStyle == 0;
        bool outline = cfg.NetSpeedArrowStyle == 1;
        UpArrowSolid.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        UpArrowOutline.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        DownArrowSolid.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        DownArrowOutline.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;

        // 速率文本（数字 + 按设置的单位制式：0 字节 / 1 比特 / 2 隐藏）
        var (upNum, upUnit) = FormatSpeedParts(_upSpeed, cfg.NetSpeedUnitMode, cfg.NetSpeedDecimalPlaces);
        var (downNum, downUnit) = FormatSpeedParts(_downSpeed, cfg.NetSpeedUnitMode, cfg.NetSpeedDecimalPlaces);
        UpNum.Text = upNum;
        UpUnit.Text = upUnit;
        DownNum.Text = downNum;
        DownUnit.Text = downUnit;

        if (_overlay is { IsOverlayVisible: true } && _shell != null)
        {
            _overlay.SetContent(BuildOverlayContent());
            _overlay.ShowAbove(_shell);
        }
    }

    /// <summary>上次应用的全局字体（变化检测：仅字体切换时重建 FontFamily）</summary>
    private string _lastFontApplied = "";

    // ===== static 采样器（A7 跨条共享的单一真源）=====

    private static DispatcherTimer? _sampler;
    private static int _attachCount;

    /// <summary>选中网卡的稳定标识（NetworkInterface.Id，GUID）；粘性选择的锚点。
    /// 显示名 _adapterName 跟随更新。</summary>
    private static string _adapterId = "";

    /// <summary>选中网卡显示名（浮层展示用）</summary>
    private static string _adapterName = "";

    /// <summary>上次采样的累计值与时刻（差分与守卫判断用）</summary>
    private static long _lastRx, _lastTx;
    private static DateTime _lastSample = DateTime.MinValue;

    /// <summary>当前速率（bytes/s，已按间隔归一）</summary>
    private static double _upSpeed, _downSpeed;

    /// <summary>今日累计（bytes；TodayKey 跨天清零）</summary>
    private static long _todayRx, _todayTx;
    private static DateTime _lastSave = DateTime.MinValue;

    private static event Action? StateChanged;

    // ===== 设置分区读取的状态快照 =====

    /// <summary>当前选中网卡显示名（设置分区展示用）</summary>
    internal static string CurrentAdapter => _adapterName;

    /// <summary>今日累计（下行, 上行）字节——设置分区展示用</summary>
    internal static (long rx, long tx) Today => (_todayRx, _todayTx);

    private static void EnsureSampler()
    {
        if (_sampler != null) { _sampler.Start(); return; }
        _sampler = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sampler.Tick += (_, _) => Sample();
        _sampler.Start();
    }

    private static void Sample()
    {
        RollTodayIfNeeded();

        // 网卡选择：Up + 非 Loopback/Tunnel 中取累计流量最大者。
        // 粘性策略（2026-09-18 跑测实锤修复）：同一物理卡常有多个绑定条目
        // （如 Realtek + VirtualBox NDIS 过滤器），累计值互有领先会让"最大者"
        // 每秒来回跳 → 速率反复归零。改为：上次选中的网卡仍存活就保持，
        // 只有它从列表消失（拔线/断 Wi-Fi）才切到新的最大者。
        // 用户锁定（设置分区 2026-09-21）：NetSpeedAdapterId 非空且该网卡存活
        // 时强制选它，覆盖粘性/最大者逻辑。
        NIC? best = null;
        NIC? sticky = null;
        NIC? locked = null;
        string lockId = AppConfig.Shared.NetSpeedAdapterId;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                           or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceStatistics st;
                try { st = nic.GetIPStatistics(); }
                catch { continue; } // 部分虚拟网卡查询会抛
                long total = st.BytesReceived + st.BytesSent;
                var cur = new NIC(nic.Id, nic.Description, st.BytesReceived, st.BytesSent, total);
                if (cur.Id == _adapterId) sticky = cur;
                if (cur.Id == lockId) locked = cur;
                if (best == null || total > best.Total) best = cur;
            }
        }
        catch (Exception ex)
        {
            MediaService.Trace($"[NET] enum error {ex.Message}");
        }

        var pick = locked ?? sticky ?? best;
        if (pick == null)
        {
            // 没有可用网卡（罕见）：速率归零，保持上次累计基准不污染
            _upSpeed = _downSpeed = 0;
            StateChanged?.Invoke();
            return;
        }

        if (pick.Id != _adapterId)
        {
            // 选中网卡变化（上次选中者消失后才走到这）：本轮归零重建基准
            MediaService.Trace($"[NET] adapter -> {pick.Description}");
            _adapterId = pick.Id;
            _lastRx = pick.Rx; _lastTx = pick.Tx;
            _lastSample = DateTime.Now;
            _upSpeed = _downSpeed = 0;
            StateChanged?.Invoke();
            return;
        }

        var now = DateTime.Now;
        double elapsedMs = (_lastSample == DateTime.MinValue) ? 0 : (now - _lastSample).TotalMilliseconds;

        // 守卫：首次采样 / 回绕（网卡计数重置）→ 速率归零，只重建基准
        if (_lastSample == DateTime.MinValue || elapsedMs <= 0 || pick.Rx < _lastRx || pick.Tx < _lastTx)
        {
            _upSpeed = _downSpeed = 0;
        }
        else
        {
            long dRx = pick.Rx - _lastRx;
            long dTx = pick.Tx - _lastTx;
            _upSpeed = dTx * 1000.0 / elapsedMs;   // 上行 = 发送
            _downSpeed = dRx * 1000.0 / elapsedMs; // 下行 = 接收
            _todayRx += dRx;
            _todayTx += dTx;
        }

        _lastRx = pick.Rx; _lastTx = pick.Tx;
        _adapterName = pick.Description; // 浮层显示名（跟 Id 走，无抖动）
        _lastSample = now;
        StateChanged?.Invoke();

        // 今日累计落盘节流：60s 一次（差分数据每秒都在内存，丢 1 分钟可接受）
        if ((now - _lastSave).TotalSeconds >= 60) SaveToday();
    }

    private sealed record NIC(string Id, string Description, long Rx, long Tx, long Total);

    // ===== 今日累计持久化（TodayKey 模式，对齐番茄钟计数）=====

    private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

    private static void RollTodayIfNeeded()
    {
        if (AppConfig.Shared.NetSpeedTodayKey == TodayKey)
        {
            // 首次加载 static 区时从配置恢复（进程内只做一次）
            if (!_todayLoaded)
            {
                _todayRx = AppConfig.Shared.NetSpeedTodayRx;
                _todayTx = AppConfig.Shared.NetSpeedTodayTx;
                _todayLoaded = true;
            }
            return;
        }
        _todayRx = _todayTx = 0;
        AppConfig.Shared.NetSpeedTodayKey = TodayKey;
        AppConfig.Shared.NetSpeedTodayRx = 0;
        AppConfig.Shared.NetSpeedTodayTx = 0;
        AppConfig.Shared.Save();
        _todayLoaded = true;
    }

    private static bool _todayLoaded;

    private static void SaveToday()
    {
        AppConfig.Shared.NetSpeedTodayKey = TodayKey;
        AppConfig.Shared.NetSpeedTodayRx = _todayRx;
        AppConfig.Shared.NetSpeedTodayTx = _todayTx;
        AppConfig.Shared.Save();
        _lastSave = DateTime.Now;
    }

    // ===== hover 浮层（E5 HoverDismiss 实例）=====

    private void ShowHoverOverlay()
    {
        if (_shell == null) return;
        _overlay ??= new OverlayWindow(OverlayDismiss.HoverDismiss);
        _overlay.SetContent(BuildOverlayContent());
        _overlay.ShowAbove(_shell);
    }

    private FrameworkElement BuildOverlayContent()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"今日累计  ↓ {FormatBytes(_todayRx)} · ↑ {FormatBytes(_todayTx)}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(_adapterName) ? "等待网卡采样…" : _adapterName,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 4, 0, 0),
        });
        return panel;
    }

    // ===== 格式化（自适应单位；数字部分等宽字体下变化不跳宽，单位继承全局字体）=====

    /// <summary>速率拆分（数字, 单位）：数字与单位统一全局字体，位置由固定宽度保证。
    /// unitMode：0 字节（B/s 自动 KB/MB/GB）· 1 比特（bps 自动 Kbps/Mbps/Gbps，
    /// 运营商带宽口径 = 字节 × 8）· 2 隐藏（单位空串）；
    /// decimals = 小数位（0/1）</summary>
    private static (string num, string unit) FormatSpeedParts(double bps, int unitMode, int decimals)
    {
        string fmt = decimals <= 0 ? "F0" : "F1";
        double v = unitMode == 1 ? bps * 8 : bps;
        (string, string) Core(double x, string u) => (x.ToString(fmt), unitMode == 2 ? "" : u);
        return v < 1024
            ? Core(v, unitMode == 1 ? "bps" : "B/s")
            : v < 1024 * 1024
                ? Core(v / 1024, unitMode == 1 ? "Kbps" : "KB/s")
                : v < 1024L * 1024 * 1024
                    ? Core(v / 1024 / 1024, unitMode == 1 ? "Mbps" : "MB/s")
                    : Core(v / 1024 / 1024 / 1024, unitMode == 1 ? "Gbps" : "GB/s");
    }

    private static string FormatBytes(double b) => b < 1024
        ? $"{b:0} B"
        : b < 1024 * 1024
            ? $"{b / 1024:0.#} KB"
            : b < 1024L * 1024 * 1024
                ? $"{b / 1024 / 1024:0.#} MB"
                : $"{b / 1024 / 1024 / 1024:0.##} GB";
}
