using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// A7 倒数日模块（设计稿定稿 2026-09-18：纪念日 + 手动倒计时事件合并，不接系统日历）。
///
/// 模型：
/// - 数据 AppConfig.CountdownItems（名称 + 日期 + 每年重复），设置分区管理。
/// - 条内永远显示最近一项（剩余天数最小且 ≥0）；非 annual 过期项自动隐藏。
/// - annual 项（生日类）自动滚到下一次 occurrence（今年已过取明年）。
/// - 天数颜色分层（设计稿）：&gt;7 天白 / ≤7 天强调蓝 #4CC2FF / 当天（days=0）
///   整行高亮"今天 · XXX"。
/// - 刷新：1 分钟轮询（天数只在午夜变化，分钟级检查跨天足够；顺带兜底列表变更）。
/// - A7 多显示器：static 采样（最近项计算）+ StateChanged 广播各条刷新。
/// </summary>
public partial class CountdownModule : UserControl, ITaskbarModule
{
    private static readonly Brush AccentBlue = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush White = Brushes.White;
    private static readonly Brush WhiteDim = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    public string Id => "countdown";
    public string DisplayName => "倒数日";
    public FrameworkElement View => this;
    public FrameworkElement? SettingsSection => _settingsSection ??= new CountdownSettingsSection();

    private CountdownSettingsSection? _settingsSection;

    private TaskbarShell? _shell;
    private OverlayWindow? _overlay;

    public CountdownModule(AppConfig config)
    {
        InitializeComponent();
        _ = config; // 读 AppConfig.Shared（static 单一真源）
    }

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        _shell = shell;
        StateChanged += OnStateChanged;
        _attachCount++;
        EnsureTimer();
        RefreshDisplay(); // 启动首帧渲染（StateChanged 只在 tick 触发，OnAttach 不刷会停留 XAML 初始文案）
        MediaService.Trace($"[CD] attached ({_attachCount} bar) items={AppConfig.Shared.CountdownItems.Count}");
    }

    public void OnDetach()
    {
        _overlay?.HideOverlay();
        StateChanged -= OnStateChanged;
        _attachCount = Math.Max(0, _attachCount - 1);
        if (_attachCount == 0)
        {
            _timer?.Stop();
            MediaService.Trace("[CD] detached (last bar)");
        }
    }

    public void OnHoverChanged(bool hovering)
    {
        MediaService.Trace($"[CD] hover {hovering}"); // 诊断：滚轮卡住排查（2026-09-21）
        if (hovering) ShowHoverOverlay();
        else _overlay?.GraceHide();
    }

    /// <summary>设置分区改列表后回调：立即重算刷新（不等下一分钟 tick）</summary>
    internal static void NotifyItemsChanged()
    {
        _cachedNearest = null;
        _cachedList = null;
        StateChanged?.Invoke();
    }

    private void OnStateChanged() => RefreshDisplay();

    // ===== static 计算与轮询（A7 跨条单一真源）=====

    private static DispatcherTimer? _timer;
    private static int _attachCount;
    private static event Action? StateChanged;

    /// <summary>最近项缓存（tick/列表变更时失效重算；结构不可变，多线程读安全——本模块全 UI 线程）</summary>
    private static Entry? _cachedNearest;
    private static List<Entry>? _cachedList;

    private static void EnsureTimer()
    {
        if (_timer != null) { _timer.Start(); return; }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) =>
        {
            _cachedNearest = null; // 跨天后剩余天数变化，缓存失效
            _cachedList = null;
            StateChanged?.Invoke();
        };
        _timer.Start();
    }

    /// <summary>一条倒数项的展开形态（annual 已滚动到下一次 occurrence）</summary>
    private sealed record Entry(string Name, DateOnly Date, int Days, bool Annual);

    private static List<Entry> BuildList()
    {
        if (_cachedList != null) return _cachedList;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var list = new List<Entry>();
        foreach (var item in AppConfig.Shared.CountdownItems)
        {
            if (!DateOnly.TryParse(item.Date, out var d)) continue;
            if (item.Annual)
            {
                var next = new DateOnly(today.Year, d.Month, d.Day);
                if (!DateTime.IsLeapYear(today.Year) && d.Month == 2 && d.Day == 29)
                    next = new DateOnly(today.Year, 2, 28); // 2/29 生日在平年落到 2/28
                if (next < today)
                    next = next.AddYears(1);
                list.Add(new Entry(item.Name, next, next.DayNumber - today.DayNumber, true));
            }
            else
            {
                int days = d.DayNumber - today.DayNumber;
                if (days < 0) continue; // 非年度项过期自动隐藏
                list.Add(new Entry(item.Name, d, days, false));
            }
        }
        _cachedList = list.OrderBy(e => e.Days).ToList(); // 剩余天数升序：近的在前
        return _cachedList;
    }

    private static Entry? Nearest()
    {
        if (_cachedNearest != null) return _cachedNearest;
        var list = BuildList();
        _cachedNearest = list.Count == 0 ? null : list[0];
        return _cachedNearest;
    }

    // ===== 显示（每条各渲染一份）=====

    private void RefreshDisplay()
    {
        var nearest = Nearest();
        if (nearest == null)
        {
            PrefixText.Text = "暂无倒数日（设置里添加）";
            PrefixText.Foreground = WhiteDim;
            DaysText.Text = "";
            return;
        }

        if (nearest.Days == 0)
        {
            // 当天命中：整行高亮（设计稿当天态）
            PrefixText.Text = $"今天 · {nearest.Name}";
            PrefixText.Foreground = AccentBlue;
            PrefixText.FontWeight = FontWeights.SemiBold;
            DaysText.Text = "";
            return;
        }

        PrefixText.FontWeight = FontWeights.Normal;
        PrefixText.Foreground = WhiteDim;
        PrefixText.Text = $"距离 {nearest.Name} 还有";
        DaysText.Text = $"{nearest.Days} 天";
        DaysText.Foreground = nearest.Days <= 7 ? AccentBlue : White; // 颜色分层：≤7 天强调蓝
    }

    // ===== hover 浮层（E5 HoverDismiss 实例：全部列表按剩余天数升序，临期标蓝）=====

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
        var list = BuildList();
        if (list.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无倒数日 · 在设置 → 倒数日 里添加",
                FontSize = 12,
                Foreground = WhiteDim,
            });
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = "倒数日",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = White,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var e in list)
        {
            bool near = e.Days == 0 || e.Days <= 7;
            string label = e.Days switch
            {
                0 => $"今天 · {e.Name}{(e.Annual ? "（每年）" : "")}",
                _ => $"{e.Days} 天后 · {e.Name}{(e.Annual ? "（每年）" : "")}",
            };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = e.Days == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = e.Days == 0 ? AccentBlue : near ? AccentBlue : WhiteDim,
                Opacity = e.Days == 0 ? 1 : near ? 0.95 : 0.75,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        return panel;
    }
}
