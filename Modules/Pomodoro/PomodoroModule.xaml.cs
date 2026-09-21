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
/// 番茄钟/倒计时模块（PRD 4.3 F2；A7 多显示器改造：状态机 static 化）。
///
/// 模型：
/// - 计时基于 DateTimeOffset 目标时刻（_endTime）而非累计 tick——250ms 轮询只做
///   显示刷新，UI 线程卡顿/滚走不显示都不产生漂移。
/// - E6 常驻验证点：模块滚走（视图移出视觉树）timer 照跑，滚回来看到正确剩余；
///   tick 更新不在树内的 TextBlock 无渲染成本。
/// - A7 跨条同步：状态机（阶段/计时/计数）全部 static——多条 Shell 各挂一个
///   PomodoroModule 实例，共享同一份状态；任何一条上的交互（启停/倒计时/跳过）
///   经 StateChanged 广播到所有条的 UI，"倒计时全局一致"（A7 验收点）。
/// - 交互（F2.5）：单击 = 启动/暂停/恢复；双击 = 重置当前段（专注重置不计番茄）；
///   右键 = 阶段操作 + 快速倒计时（1/5/10/25/45 分钟，F2.1）。
/// - 循环（F2.2）：专注完成 → 今日计数+1（F2.6 落盘，跨天自动清零）→ 自动进休息；
///   休息完成 → 回 Idle（不自动连跑下一颗，由用户决定节奏）。
/// - 到点提醒（F2.3 第一版）：overlay 闪烁 ~2s（工作完成绿/其余橙），每条都闪。
///   toast + 提示音暂缓（无依赖资源，后续版本补）。
/// </summary>
public partial class PomodoroModule : UserControl, ITaskbarModule
{
    // 阶段配色（圆点）
    private static readonly Color DotWorking = Color.FromRgb(0xFF, 0x69, 0x52); // 珊瑚橙红
    private static readonly Color DotBreak = Color.FromRgb(0x3D, 0xDC, 0x84);   // 绿
    private static readonly Color DotQuick = Color.FromRgb(0x56, 0xB6, 0xF5);   // 蓝
    private static readonly Color DotIdle = Color.FromRgb(0x9E, 0x9E, 0x9E);    // 灰
    private static readonly Color DotBreakDone = Color.FromRgb(0xFF, 0xA5, 0x00); // 休息结束闪橙

    private readonly AppConfig _config;

    // ===== 实例状态（每条一份的 UI 附属）=====
    // 区分单双击的延迟确认是"这条上的鼠标行为"，各条独立
    private readonly DispatcherTimer _clickTimer;

    /// <summary>壳引用（OnAttach 存，hover 浮层锚定用）</summary>
    private TaskbarShell? _shell;

    /// <summary>F2.4 hover 浮层（E5 通用浮层实例，懒建复用）</summary>
    private OverlayWindow? _overlay;

    private PomodoroSettingsSection? _settingsSection;

    // ===== A7 static 状态机（跨条共享的单一真源）=====

    private static PomodoroPhase _phase = PomodoroPhase.Idle;

    /// <summary>运行中目标时刻（毫秒精度外推，不累计 tick）</summary>
    private static DateTimeOffset _endTime;

    /// <summary>暂停时冻结的剩余时长</summary>
    private static TimeSpan _remaining;

    /// <summary>快速倒计时的总秒数（用于显示/恢复）</summary>
    private static int _quickTotalSec;

    /// <summary>当前段开始时刻（浮层显示阶段起止时间用）</summary>
    private static DateTimeOffset _phaseStart;

    /// <summary>阶段完成转换的覆盖文案（"休息完毕"等；下一段开始时清）</summary>
    private static string? _completedNote;

    /// <summary>250ms 倒计时轮询（static 懒建——状态机共享，timer 只此一份）</summary>
    private static DispatcherTimer? _tickTimer;

    /// <summary>挂载中的模块实例数（A7：最后一条 detach 时才重置状态机）</summary>
    private static int _attachCount;

    /// <summary>状态变化广播：参数为到点闪烁色（null = 普通刷新）。
    /// 每条实例订阅并刷新各自 UI——倒计时跨条全局一致的核心通道。</summary>
    private static event Action<Color?>? StateChanged;

    /// <summary>static 状态机的配置真源（实例侧另有 _config 供设置分区绑定）</summary>
    private static AppConfig Cfg => AppConfig.Shared;

    public string Id => "pomodoro";
    public string DisplayName => "番茄钟";
    public FrameworkElement View => this;
    public FrameworkElement? SettingsSection => _settingsSection ??= new PomodoroSettingsSection(this);

    /// <summary>暴露配置供设置分区 VM 绑定（模式对齐 MusicModule.Config）</summary>
    internal AppConfig Config => _config;

    public PomodoroModule(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += (_, _) => PerformSingleClickAction();
    }

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        _shell = shell;
        StateChanged += OnStateChanged;
        _attachCount++;
        RollTodayCountIfNeeded();
        ApplyDurations(); // Idle 显示按当前配置刷新
        MediaService.Trace($"[F2] attached ({_attachCount} bar) work={_config.PomodoroWorkMin}m break={_config.PomodoroBreakMin}m today={_config.PomodoroTodayCount}");
    }

    /// <summary>禁用即弃：最后一条 detach 时停计时回 Idle（重新启用从干净状态开始；
    /// 今日计数持久化不受影响）。多条时仅退订本条 UI。</summary>
    public void OnDetach()
    {
        _clickTimer.Stop();
        _overlay?.HideOverlay(); // hover 浮层不能在模块禁用后还挂在屏幕上
        StateChanged -= OnStateChanged;
        _attachCount = Math.Max(0, _attachCount - 1);
        if (_attachCount == 0)
        {
            GoIdle(saveCount: false);
            MediaService.Trace("[F2] detached (last bar) phase-reset");
        }
        else
        {
            MediaService.Trace($"[F2] detached ({_attachCount} bar remains)");
        }
    }

    public void OnHoverChanged(bool hovering)
    {
        // F2.4 hover 浮层（E5 HoverDismiss 实例）：hover 条即出，离开宽限收
        if (hovering) ShowHoverOverlay();
        else _overlay?.GraceHide();
    }

    /// <summary>设置分区改时长后回调：Idle 态立即刷新显示；运行中不打断（下一段生效）</summary>
    internal void ApplyDurations()
    {
        if (_phase == PomodoroPhase.Idle)
            StateChanged?.Invoke(null);
    }

    // ===== A7 状态广播 → 实例 UI 刷新 =====

    /// <summary>static 状态机变化 → 本条 UI 刷新（每条各一份 handler）</summary>
    private void OnStateChanged(Color? flashColor)
    {
        if (flashColor is { } c) Flash(c);
        RefreshDisplay();
    }

    /// <summary>按 static 状态渲染本条全部显示（时间/圆点/阶段文案/计数/浮层）</summary>
    private void RefreshDisplay()
    {
        TimeText.Text = _phase switch
        {
            PomodoroPhase.Idle => FormatTime(TimeSpan.FromMinutes(Math.Max(1, _config.PomodoroWorkMin))),
            PomodoroPhase.WorkingPaused or PomodoroPhase.BreakPaused or PomodoroPhase.QuickPaused
                => FormatTime(_remaining),
            _ => FormatTime(_endTime - DateTimeOffset.Now),
        };
        UpdatePhaseVisual();
    }

    // ===== 计时核心（static：单一真源）=====

    private static DispatcherTimer TickTimer
        => _tickTimer ??= CreateTickTimer();

    private static DispatcherTimer CreateTickTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) => StaticOnTick();
        return t;
    }

    private static void StaticOnTick()
    {
        var remaining = _endTime - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            OnPhaseComplete();
            return;
        }
        StateChanged?.Invoke(null); // 每 tick 刷新各条时间显示
    }

    private static void StartPhase(PomodoroPhase phase, TimeSpan duration)
    {
        _phase = phase;
        _phaseStart = DateTimeOffset.Now;
        _endTime = DateTimeOffset.Now + duration;
        _completedNote = null;
        TickTimer.Start();
        StateChanged?.Invoke(null);
    }

    private static void Pause()
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
        TickTimer.Stop();
        StateChanged?.Invoke(null);
    }

    private static void Resume()
    {
        _phase = _phase switch
        {
            PomodoroPhase.WorkingPaused => PomodoroPhase.Working,
            PomodoroPhase.BreakPaused => PomodoroPhase.Break,
            PomodoroPhase.QuickPaused => PomodoroPhase.Quick,
            _ => _phase,
        };
        _phaseStart = DateTimeOffset.Now;
        _endTime = DateTimeOffset.Now + _remaining;
        TickTimer.Start();
        StateChanged?.Invoke(null);
    }

    private static void OnPhaseComplete()
    {
        switch (_phase)
        {
            case PomodoroPhase.Working:
                RollTodayCountIfNeeded();
                AppConfig.Shared.PomodoroTodayCount++;
                AppConfig.Shared.PomodoroTodayKey = TodayKey;
                AppConfig.Shared.Save();
                FlashAll(DotBreak); // 完成一颗：闪绿
                MediaService.Trace($"[F2] work done today={AppConfig.Shared.PomodoroTodayCount} -> break");
                StartPhase(PomodoroPhase.Break, TimeSpan.FromMinutes(Math.Max(1, AppConfig.Shared.PomodoroBreakMin)));
                break;

            case PomodoroPhase.Break:
                FlashAll(DotBreakDone); // 休息结束：闪橙
                MediaService.Trace("[F2] break done -> idle");
                _completedNote = "休息完毕";
                GoIdle(saveCount: false);
                break;

            case PomodoroPhase.Quick:
                FlashAll(DotBreakDone);
                MediaService.Trace("[F2] quick timer done -> idle");
                _completedNote = "倒计时结束";
                GoIdle(saveCount: false);
                break;
        }
    }

    /// <summary>回 Idle（恢复默认视觉；saveCount=false 时今日计数已存不必再写盘）</summary>
    private static void GoIdle(bool saveCount)
    {
        _phase = PomodoroPhase.Idle;
        TickTimer.Stop();
        _remaining = TimeSpan.Zero;
        StateChanged?.Invoke(null);
        if (saveCount) AppConfig.Shared.Save();
    }

    /// <summary>到点闪烁广播（每条实例各自 Flash——用户在任何一条都能看到提醒）</summary>
    private static void FlashAll(Color color) => StateChanged?.Invoke(color);

    // ===== 交互（F2.5；动作 static，单双击区分为实例级行为）=====

    private void HitArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 不触发壳拖拽

        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            // 双击 = 重置当前段（专注重置不计番茄；Idle 幂等无害）
            MediaService.Trace($"[F2] dblclick reset phase={_phase}");
            _completedNote = null;
            GoIdle(saveCount: false);
            return;
        }

        // 单击延迟 250ms 确认（期间来第二击则取消——那是双击）
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    /// <summary>单击动作（static）：Idle→开始专注；运行→暂停；暂停→恢复。
    /// 任何一条触发，全条状态一起变。</summary>
    private static void PerformSingleClickAction()
    {
        switch (_phase)
        {
            case PomodoroPhase.Idle:
                MediaService.Trace("[F2] click start work");
                StartPhase(PomodoroPhase.Working, TimeSpan.FromMinutes(Math.Max(1, AppConfig.Shared.PomodoroWorkMin)));
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
    /// 每次右键按当前阶段重建（对齐壳层 BuildContextMenu 模式）。菜单项全部走
    /// static 动作——在副屏条上操作主屏计时一样生效（A7）。</summary>
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
                StartPhase(PomodoroPhase.Break, TimeSpan.FromMinutes(Math.Max(1, AppConfig.Shared.PomodoroBreakMin)));
            });
        if (_phase is PomodoroPhase.Break or PomodoroPhase.BreakPaused or PomodoroPhase.Quick or PomodoroPhase.QuickPaused)
            menu.Items.Add("跳过本段", null, (_, _) =>
            {
                MediaService.Trace("[F2] skip segment -> idle");
                _completedNote = null;
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
            AppConfig.Shared.PomodoroTodayCount = 0;
            AppConfig.Shared.Save();
            StateChanged?.Invoke(null);
            MediaService.Trace("[F2] today count reset");
        });
        return menu;
    }

    // ===== 视觉（实例：每条各渲染一份）=====

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
        // 暂停态视觉降级（交互稿 A2）：圆点空心（阶段色描边）+ 倒计时降透明。
        // 空心颜色取"暂停前阶段色"（专注暂停=珊瑚红空心/休息暂停=绿空心）
        bool paused = _phase is PomodoroPhase.WorkingPaused or PomodoroPhase.BreakPaused
                                          or PomodoroPhase.QuickPaused;
        Color dotColor = paused
            ? _phase switch
            {
                PomodoroPhase.WorkingPaused => DotWorking,
                PomodoroPhase.BreakPaused => DotBreak,
                _ => DotQuick,
            }
            : dot;
        if (paused)
        {
            PhaseDot.Fill = Brushes.Transparent;
            PhaseDot.Stroke = new SolidColorBrush(dotColor);
            PhaseDot.StrokeThickness = 1.5;
        }
        else
        {
            PhaseDot.Fill = new SolidColorBrush(dot);
            PhaseDot.Stroke = null;
        }
        TimeText.Opacity = paused ? 0.55 : 1.0;
        // 完成转换的覆盖文案（"休息完毕"）优先于默认 Idle 文案
        PhaseText.Text = _phase == PomodoroPhase.Idle && _completedNote != null ? _completedNote : label;

        // F2.4：阶段转换时浮层可能正开着（hover 中恰好到点/点了暂停）——内容重建 + 重锚定
        if (_overlay is { IsOverlayVisible: true } && _shell != null)
        {
            _overlay.SetContent(BuildOverlayContent());
            _overlay.ShowAbove(_shell);
        }
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

    // ===== F2.4 hover 浮层（E5 HoverDismiss 实例）=====

    /// <summary>hover 条即显示浮层：今日完成番茄数 + 当前阶段起止时间（PRD F2.4 规格）</summary>
    private void ShowHoverOverlay()
    {
        if (_shell == null) return;
        _overlay ??= new OverlayWindow(OverlayDismiss.HoverDismiss);
        _overlay.SetContent(BuildOverlayContent());
        _overlay.ShowAbove(_shell);
    }

    /// <summary>浮层内容：标题行（今日计数）+ 阶段行（起止/剩余/配置）</summary>
    private FrameworkElement BuildOverlayContent()
    {
        var panel = new StackPanel();

        var title = new TextBlock
        {
            Text = $"番茄钟 · 今日完成 {_config.PomodoroTodayCount} 颗",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
        };
        panel.Children.Add(title);

        var detail = new TextBlock
        {
            Text = PhaseDetailText(),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(detail);
        return panel;
    }

    /// <summary>阶段行文案：运行中显示起止时刻（HH:mm – HH:mm），暂停显示剩余，
    /// 待命显示配置时长——悬浮时一眼看清"现在进行到哪、什么时候结束"</summary>
    private string PhaseDetailText()
    {
        int workMin = Math.Max(1, _config.PomodoroWorkMin);
        int breakMin = Math.Max(1, _config.PomodoroBreakMin);
        return _phase switch
        {
            PomodoroPhase.Working => $"专注中 · {FormatClock(_phaseStart)} – {FormatClock(_endTime)}",
            PomodoroPhase.WorkingPaused => $"专注暂停 · 剩余 {FormatTime(_remaining)}",
            PomodoroPhase.Break => $"休息中 · {FormatClock(_phaseStart)} – {FormatClock(_endTime)}",
            PomodoroPhase.BreakPaused => $"休息暂停 · 剩余 {FormatTime(_remaining)}",
            PomodoroPhase.Quick => $"倒计时 {(_quickTotalSec + 59) / 60} 分钟 · 结束于 {FormatClock(_endTime)}",
            PomodoroPhase.QuickPaused => $"倒计时暂停 · 剩余 {FormatTime(_remaining)}",
            _ => $"待命 · 专注 {workMin} 分钟 / 休息 {breakMin} 分钟",
        };
    }

    private static string FormatClock(DateTimeOffset t) => t.LocalDateTime.ToString("HH:mm");

    // ===== 工具（static：状态机附属）=====

    private static string TodayKey => DateTimeOffset.Now.ToString("yyyy-MM-dd");

    /// <summary>跨天清零（F2.6：当日计数；加载时与每次完成时检查）</summary>
    private static void RollTodayCountIfNeeded()
    {
        if (AppConfig.Shared.PomodoroTodayKey != TodayKey)
        {
            AppConfig.Shared.PomodoroTodayCount = 0;
            AppConfig.Shared.PomodoroTodayKey = TodayKey;
            AppConfig.Shared.Save();
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
