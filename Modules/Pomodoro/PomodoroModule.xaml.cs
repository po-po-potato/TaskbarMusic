using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>番茄钟运行阶段（暂停是独立态，便于恢复时知道回到哪个阶段）</summary>
public enum PomodoroPhase
{
    /// <summary>待命：显示配置的专注时长，等单击开始</summary>
    Idle = 0,

    /// <summary>专注倒计时中（番茄钟主阶段）</summary>
    Working = 1,

    /// <summary>专注暂停（_remaining 冻结剩余）</summary>
    WorkingPaused = 2,

    /// <summary>休息倒计时中（专注完成自动进入）</summary>
    Break = 3,

    /// <summary>休息暂停</summary>
    BreakPaused = 4,

    /// <summary>快速倒计时（右键菜单选时长，单段结束回 Idle，不进休息不计数）</summary>
    Quick = 5,

    /// <summary>快速倒计时暂停</summary>
    QuickPaused = 6,
}

/// <summary>
/// 番茄钟/倒计时模块（PRD 4.3 F2 第一版）。
///
/// 模型：
/// - 计时基于 DateTimeOffset 目标时刻（_endTime）而非累计 tick——250ms 轮询只做
///   显示刷新，UI 线程卡顿/滚走不显示都不产生漂移。
/// - E6 常驻验证点：模块滚走（视图移出视觉树）timer 照跑，滚回来看到正确剩余；
///   tick 更新不在树内的 TextBlock 无渲染成本。
/// - 交互（F2.5）：单击 = 启动/暂停/恢复；双击 = 重置当前段（专注重置不计番茄）；
///   右键 = 阶段操作 + 快速倒计时（1/5/10/25/45 分钟，F2.1）。
/// - 循环（F2.2）：专注完成 → 今日计数+1（F2.6 落盘，跨天自动清零）→ 自动进休息；
///   休息完成 → 回 Idle（不自动连跑下一颗，由用户决定节奏）。
/// - 到点提醒（F2.3 第一版）：overlay 闪烁 ~2s（工作完成绿/其余橙）。
///   toast + 提示音暂缓（无依赖资源，后续版本补）。
/// </summary>
public partial class PomodoroModule : UserControl, ITaskbarModule
{
    // 阶段配色（圆点）
    private static readonly Color DotWorking = Color.FromRgb(0xFF, 0x69, 0x52); // 珊瑚橙红
    private static readonly Color DotBreak = Color.FromRgb(0x3D, 0xDC, 0x84);   // 绿
    private static readonly Color DotQuick = Color.FromRgb(0x56, 0xB6, 0xF5);   // 蓝
    private static readonly Color DotIdle = Color.FromRgb(0x9E, 0x9E, 0x9E);    // 灰

    private readonly AppConfig _config;

    /// <summary>倒计时轮询：250ms（比 1s 密，跨秒边界显示不迟滞；仅刷新显示）</summary>
    private readonly DispatcherTimer _tickTimer;

    /// <summary>单击延迟确认：区分单击（启停）与双击（重置）</summary>
    private readonly DispatcherTimer _clickTimer;

    private PomodoroPhase _phase = PomodoroPhase.Idle;

    /// <summary>运行中目标时刻（毫秒精度外推，不累计 tick）</summary>
    private DateTimeOffset _endTime;

    /// <summary>暂停时冻结的剩余时长</summary>
    private TimeSpan _remaining;

    /// <summary>快速倒计时的总秒数（用于显示/恢复）</summary>
    private int _quickTotalSec;

    public string Id => "pomodoro";
    public string DisplayName => "番茄钟";
    public FrameworkElement View => this;
    public FrameworkElement? SettingsSection => _settingsSection ??= new PomodoroSettingsSection(this);

    /// <summary>暴露配置供设置分区 VM 绑定（模式对齐 MusicModule.Config）</summary>
    internal AppConfig Config => _config;

    private PomodoroSettingsSection? _settingsSection;

    public PomodoroModule(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tickTimer.Tick += (_, _) => OnTick();

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += (_, _) => PerformSingleClickAction();
    }

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        RollTodayCountIfNeeded();
        ApplyDurations(); // Idle 显示按当前配置刷新
        MediaService.Trace($"[F2] attached work={_config.PomodoroWorkMin}m break={_config.PomodoroBreakMin}m today={_config.PomodoroTodayCount}");
    }

    /// <summary>禁用即弃：停计时回 Idle（重新启用从干净状态开始；今日计数持久化不受影响）</summary>
    public void OnDetach()
    {
        _clickTimer.Stop();
        GoIdle(saveCount: false);
        MediaService.Trace($"[F2] detached phase-reset");
    }

    public void OnHoverChanged(bool hovering)
    {
        // F2.4 hover 浮层暂缓（依赖 E5 通用浮层框架）
    }

    /// <summary>设置分区改时长后回调：Idle 态立即刷新显示；运行中不打断（下一段生效）</summary>
    internal void ApplyDurations()
    {
        if (_phase == PomodoroPhase.Idle)
            TimeText.Text = FormatTime(TimeSpan.FromMinutes(Math.Max(1, _config.PomodoroWorkMin)));
    }

    // ===== 计时核心 =====

    private void OnTick()
    {
        var remaining = _endTime - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            OnPhaseComplete();
            return;
        }
        TimeText.Text = FormatTime(remaining);
    }

    private void StartPhase(PomodoroPhase phase, TimeSpan duration)
    {
        _phase = phase;
        _endTime = DateTimeOffset.Now + duration;
        _tickTimer.Start();
        TimeText.Text = FormatTime(duration);
        UpdatePhaseVisual();
    }

    private void Pause()
    {
        _remaining = _endTime - DateTimeOffset.Now;
        if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
        _phase = _phase switch
        {
            PomodoroPhase.Working => PomodoroPhase.WorkingPaused,
            PomodoroPhase.Break => PomodoroPhase.BreakPaused,
            PomodoroPhase.Quick => PomodoroPhase.QuickPaused,
            _ => _phase,
        };
        _tickTimer.Stop();
        UpdatePhaseVisual();
    }

    private void Resume()
    {
        var phase = _phase switch
        {
            PomodoroPhase.WorkingPaused => PomodoroPhase.Working,
            PomodoroPhase.BreakPaused => PomodoroPhase.Break,
            PomodoroPhase.QuickPaused => PomodoroPhase.Quick,
            _ => _phase,
        };
        _phase = phase;
        _endTime = DateTimeOffset.Now + _remaining;
        _tickTimer.Start();
        UpdatePhaseVisual();
    }

    private void OnPhaseComplete()
    {
        switch (_phase)
        {
            case PomodoroPhase.Working:
                RollTodayCountIfNeeded();
                _config.PomodoroTodayCount++;
                _config.PomodoroTodayKey = TodayKey;
                _config.Save();
                CountText.Text = $"今日 {_config.PomodoroTodayCount}";
                Flash(DotBreak); // 完成一颗：闪绿
                MediaService.Trace($"[F2] work done today={_config.PomodoroTodayCount} -> break");
                StartPhase(PomodoroPhase.Break, TimeSpan.FromMinutes(Math.Max(1, _config.PomodoroBreakMin)));
                break;

            case PomodoroPhase.Break:
                Flash(Color.FromRgb(0xFF, 0xA5, 0x00)); // 休息结束：闪橙
                MediaService.Trace("[F2] break done -> idle");
                GoIdle(saveCount: false);
                PhaseText.Text = "休息完毕";
                break;

            case PomodoroPhase.Quick:
                Flash(DotBreak);
                MediaService.Trace("[F2] quick timer done -> idle");
                GoIdle(saveCount: false);
                PhaseText.Text = "倒计时结束";
                break;
        }
    }

    /// <summary>回 Idle（恢复默认视觉；saveCount=false 时今日计数已存不必再写盘）</summary>
    private void GoIdle(bool saveCount)
    {
        _phase = PomodoroPhase.Idle;
        _tickTimer.Stop();
        _remaining = TimeSpan.Zero;
        ApplyDurations();
        UpdatePhaseVisual();
        if (saveCount) _config.Save();
    }

    // ===== 交互（F2.5）=====

    private void HitArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 不触发壳拖拽

        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            // 双击 = 重置当前段（专注重置不计番茄；Idle 幂等无害）
            MediaService.Trace($"[F2] dblclick reset phase={_phase}");
            GoIdle(saveCount: false);
            return;
        }

        // 单击延迟 250ms 确认（期间来第二击则取消——那是双击）
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    /// <summary>单击动作：Idle→开始专注；运行→暂停；暂停→恢复</summary>
    private void PerformSingleClickAction()
    {
        _clickTimer.Stop();
        switch (_phase)
        {
            case PomodoroPhase.Idle:
                MediaService.Trace("[F2] click start work");
                StartPhase(PomodoroPhase.Working, TimeSpan.FromMinutes(Math.Max(1, _config.PomodoroWorkMin)));
                break;
            case PomodoroPhase.Working:
            case PomodoroPhase.Break:
            case PomodoroPhase.Quick:
                MediaService.Trace($"[F2] click pause phase={_phase}");
                Pause();
                break;
            default:
                MediaService.Trace($"[F2] click resume phase={_phase}");
                Resume();
                break;
        }
    }

    private void HitArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 不冒泡到壳的全局菜单（空白区右键仍是全局菜单）
        BuildContextMenu().Show(System.Windows.Forms.Control.MousePosition);
    }

    /// <summary>右键菜单（WinForms——与壳层同款：独立于 WPF 渲染/事件体系，无 sticky 干扰）。
    /// 每次右键按当前阶段重建（对齐壳层 BuildContextMenu 模式）。</summary>
    private System.Windows.Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        // 主操作（随阶段变化）
        switch (_phase)
        {
            case PomodoroPhase.Idle:
                menu.Items.Add("开始专注", null, (_, _) => PerformSingleClickAction());
                break;
            case PomodoroPhase.Working:
            case PomodoroPhase.Break:
            case PomodoroPhase.Quick:
                menu.Items.Add("暂停", null, (_, _) => PerformSingleClickAction());
                break;
            default:
                menu.Items.Add("继续", null, (_, _) => PerformSingleClickAction());
                break;
        }

        // 阶段操作
        if (_phase is PomodoroPhase.Working or PomodoroPhase.WorkingPaused)
            menu.Items.Add("跳过专注（直接休息）", null, (_, _) =>
            {
                MediaService.Trace("[F2] skip work -> break");
                StartPhase(PomodoroPhase.Break, TimeSpan.FromMinutes(Math.Max(1, _config.PomodoroBreakMin)));
            });
        if (_phase is PomodoroPhase.Break or PomodoroPhase.BreakPaused or PomodoroPhase.Quick or PomodoroPhase.QuickPaused)
            menu.Items.Add("跳过本段", null, (_, _) =>
            {
                MediaService.Trace("[F2] skip segment -> idle");
                GoIdle(saveCount: false);
            });

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // 快速倒计时（F2.1：选时长即单段倒计时，结束回 Idle）
        foreach (var min in new[] { 1, 5, 10, 25, 45 })
        {
            int m = min;
            menu.Items.Add($"倒计时 {m} 分钟", null, (_, _) =>
            {
                _quickTotalSec = m * 60;
                MediaService.Trace($"[F2] quick timer {m}min");
                StartPhase(PomodoroPhase.Quick, TimeSpan.FromSeconds(_quickTotalSec));
            });
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("重置今日计数", null, (_, _) =>
        {
            RollTodayCountIfNeeded();
            _config.PomodoroTodayCount = 0;
            _config.Save();
            CountText.Text = "今日 0";
            MediaService.Trace("[F2] today count reset");
        });
        return menu;
    }

    // ===== 视觉 =====

    private void UpdatePhaseVisual()
    {
        (Color dot, string label) = _phase switch
        {
            PomodoroPhase.Working => (DotWorking, "专注中"),
            PomodoroPhase.WorkingPaused => (DotIdle, "已暂停"),
            PomodoroPhase.Break => (DotBreak, "休息中"),
            PomodoroPhase.BreakPaused => (DotIdle, "休息暂停"),
            PomodoroPhase.Quick => (DotQuick, "倒计时"),
            PomodoroPhase.QuickPaused => (DotIdle, "倒计时暂停"),
            _ => (DotIdle, "待命 · 单击开始"),
        };
        PhaseDot.Fill = new SolidColorBrush(dot);
        PhaseText.Text = label;
        CountText.Text = $"今日 {_config.PomodoroTodayCount}";
    }

    /// <summary>到点闪烁：overlay 透明度 0↔0.75 波动 3 个来回（~2s），结束归零</summary>
    private void Flash(Color color)
    {
        FlashOverlay.Background = new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B));
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 0.75,
            Duration = TimeSpan.FromMilliseconds(350),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => FlashOverlay.BeginAnimation(OpacityProperty, null);
        FlashOverlay.BeginAnimation(OpacityProperty, anim);
    }

    // ===== 工具 =====

    private static string TodayKey => DateTimeOffset.Now.ToString("yyyy-MM-dd");

    /// <summary>跨天清零（F2.6：当日计数；加载时与每次完成时检查）</summary>
    private void RollTodayCountIfNeeded()
    {
        if (_config.PomodoroTodayKey != TodayKey)
        {
            _config.PomodoroTodayCount = 0;
            _config.PomodoroTodayKey = TodayKey;
            _config.Save();
        }
    }

    /// <summary>mm:ss（≥1h 用 h:mm:ss）；Consolas 等宽，数字变化不跳宽</summary>
    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
