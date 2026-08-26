using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TaskbarMusic;

/// <summary>
/// 歌词行显示控件。
///
/// 滚动逻辑（三段式跑马灯，业界桌面歌词标准节奏）：
///   句首停留(≤1.2s) → 匀速平移 → 句尾停留(≤0.8s) → 换句重置
/// 关键：调用方传入"本句已消耗时间 elapsedSec"，动画用 From 起点补偿启动延迟，
/// 把滚动结束点钉死在句尾 —— 修掉"滚不到尾部就换句"的老 bug。
///
/// 实现：Canvas + TextBlock + TranslateTransform + DoubleAnimation。
/// 宽度用 TextBlock.Measure 直接量，不依赖 ScrollViewer / 布局事件时序。
/// Duration 依赖属性仅为兼容外部赋值保留（信息已由 StartLine 参数取代）。
/// </summary>
public partial class MarqueeTextBlock : UserControl
{
    public MarqueeTextBlock()
    {
        InitializeComponent();
        HostCanvas.Height = Math.Max(1, TextFontSize * 1.4); // 首帧就撑起行高，避免塌成 0
        Loaded += (_, _) => Relayout();
        SizeChanged += (_, _) => Relayout(); // 视口变化（拖宽/字号）重算
    }

    #region Dependency Properties

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeTextBlock),
            new PropertyMetadata("", OnContentChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>当前句持续时长（秒）。已废弃——由 StartLine(totalSec) 取代，保留仅为兼容外部赋值。</summary>
    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(double), typeof(MarqueeTextBlock),
            new PropertyMetadata(0.0)); // 不再触发重排

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty TextFontSizeProperty =
        DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(MarqueeTextBlock),
            new PropertyMetadata(13.0, OnContentChanged));

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public static readonly DependencyProperty TextFontWeightProperty =
        DependencyProperty.Register(nameof(TextFontWeight), typeof(FontWeight), typeof(MarqueeTextBlock),
            new PropertyMetadata(FontWeights.Normal, OnContentChanged));

    public FontWeight TextFontWeight
    {
        get => (FontWeight)GetValue(TextFontWeightProperty);
        set => SetValue(TextFontWeightProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (MarqueeTextBlock)d;
        if (e.Property == TextProperty)
        {
            m._lastText = m.MainText.Text; // 推挤过渡需要旧句（推上淡出的那一行）
            m.MainText.Text = (string)e.NewValue;
        }
        if (!m._suppressRelayout) m.Relayout();
    }

    /// <summary>
    /// 冻结快照（双行推挤旧块用）：写入旧句文本并钉在当前滚动位上。
    /// 走实时控件而非 RenderTargetBitmap 位图——位图离屏渲染无 ClearType、
    /// 分数 DPI 下重采样发虚，换图瞬间有可感知的"变淡"质量台阶（2026-08-26）。
    /// </summary>
    public void FreezeSnapshot(string text, double x)
    {
        _suppressRelayout = true;
        try { Text = text; }
        finally { _suppressRelayout = false; }
        // Canvas 需显式高（Collapsed 期间 Relayout 可能没跑过）
        HostCanvas.Height = Math.Max(1, TextFontSize * 1.4);
        // 停掉 X 动画拿回本地值，钉在旧句当前滚动位（DP getter 返回的是动画生效值）
        Shift.BeginAnimation(TranslateTransform.XProperty, null);
        Shift.X = x;
        MainText.BeginAnimation(OpacityProperty, null);
        MainText.Opacity = 1;
    }

    /// <summary>当前滚动位（X 动画进行中时返回动画生效值，供冻结旧块用）</summary>
    public double CurrentShiftX => Shift.X;

    private bool _suppressRelayout;

    /// <summary>上一句文本快照（Text 变更时抓取，仅供推挤过渡渲染 GhostText）</summary>
    private string? _lastText;

    /// <summary>
    /// 预览模式（E 模式次行"下一句"用）：不滚动，超宽时句首左对齐、右侧截断。
    /// 普通模式超宽走三段式/单程滚动。
    /// </summary>
    public static readonly DependencyProperty PreviewModeProperty =
        DependencyProperty.Register(nameof(PreviewMode), typeof(bool), typeof(MarqueeTextBlock),
            new PropertyMetadata(false, OnPreviewModeChanged));

    public bool PreviewMode
    {
        get => (bool)GetValue(PreviewModeProperty);
        set => SetValue(PreviewModeProperty, value);
    }

    private static void OnPreviewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MarqueeTextBlock)d).Relayout();
    }

    /// <summary>
    /// 换句过渡（新句 180ms 入场 + 旧句 120ms 快退，QuadraticEase）：
    /// None=不动画；Fade=纯淡入；Slide=淡入+从下方 3px 上滑；
    /// Zoom=淡入+0.9→1 左中放大；Blur=淡入+模糊 8px→0 渐清晰；
    /// BlurZoom=模糊+缩放+淡入三合一（AMLL 复合签名效果）。
    /// Push=推挤：旧句整行推上淡出 + 新句整行从下方推入（400ms，
    /// cubic-bezier(0.4,0,0.2,1)，仿 SPlayer 任务栏歌词的 transition-group 行推挤）。
    /// 旧句退场（非 Push 模式）= 快退慢进：120ms 淡出+3px 上浮（EaseIn 加速离场），
    /// 与新句入场空间错开，避免同位置 alpha 叠加发糊（纯交叉淡化会"太软"）。
    /// 模糊走 BlurEffect 半径动画，完成后摘掉 Effect 恢复 ClearType 渲染。
    /// </summary>
    public void BeginTransition(LineTransition kind)
    {
        if (kind == LineTransition.None) return;

        if (kind == LineTransition.Push)
        {
            BeginPushTransition();
            return;
        }

        // 旧句快退（120ms 淡出+3px 上浮，前 1/3 结束）：修"旧行瞬间蒸发、
        // 新行却有动画"的不对称（2026-08-26 Glenn 反馈）。首句/空行无退场。
        if (!string.IsNullOrEmpty(_lastText))
        {
            GhostShift.BeginAnimation(TranslateTransform.YProperty, null);
            GhostText.BeginAnimation(OpacityProperty, null);
            GhostText.Text = _lastText;
            GhostText.Visibility = Visibility.Visible;
            GhostShift.X = Shift.X; // 与新句静止位水平对齐（同推挤的简化）
            GhostShift.Y = 0;
            var gEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var gDur = TimeSpan.FromMilliseconds(120);
            var gop = new DoubleAnimation(1, 0, gDur) { EasingFunction = gEase };
            var gy = new DoubleAnimation(0, -3, gDur) { EasingFunction = gEase };
            gop.Completed += (_, _) =>
            {
                GhostText.BeginAnimation(OpacityProperty, null);
                GhostText.Opacity = 1;
                GhostShift.BeginAnimation(TranslateTransform.YProperty, null);
                GhostShift.Y = 0;
                GhostText.Visibility = Visibility.Collapsed;
                GhostText.Text = "";
            };
            GhostText.BeginAnimation(OpacityProperty, gop);
            GhostShift.BeginAnimation(TranslateTransform.YProperty, gy);
        }

        var ease = new System.Windows.Media.Animation.QuadraticEase();
        var dur = TimeSpan.FromMilliseconds(180);

        var op = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        MainText.BeginAnimation(OpacityProperty, op);

        if (kind == LineTransition.Slide)
        {
            var sy = new DoubleAnimation(3, 0, dur) { EasingFunction = ease };
            Shift.BeginAnimation(TranslateTransform.YProperty, sy);
        }

        if (kind is LineTransition.Zoom or LineTransition.BlurZoom)
        {
            var sc = new DoubleAnimation(0.9, 1.0, dur) { EasingFunction = ease };
            LineScale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            LineScale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
        }

        if (kind is LineTransition.Blur or LineTransition.BlurZoom)
        {
            // AMLL 语义：新句从模糊中渐清晰。Effect 会让文本走中间表面渲染（ClearType
            // 临时降级为灰度 AA），180ms 极短可接受；完成后立即摘除恢复像素级清晰。
            const double blurFrom = 8;
            var blurFx = new BlurEffect { Radius = blurFrom };
            MainText.Effect = blurFx;
            var ba = new DoubleAnimation(blurFrom, 0, dur) { EasingFunction = ease };
            ba.Completed += (_, _) =>
            {
                if (ReferenceEquals(MainText.Effect, blurFx))
                    MainText.Effect = null;
            };
            blurFx.BeginAnimation(BlurEffect.RadiusProperty, ba);
        }
    }

    /// <summary>
    /// 推挤过渡（仿 SPlayer transition-group 行推挤）：
    /// 旧句（GhostText）整行推上淡出，新句整行从行高下方推入淡显。
    /// 400ms cubic-bezier(0.4,0,0.2,1)（CubicEase EaseInOut 近似）。
    /// 旧句水平位置与 MainText 当前静止位对齐（换句滚动重置后的 X），400ms 内快速离场无感。
    /// </summary>
    private void BeginPushTransition()
    {
        const int durMs = 400;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(durMs);
        double h = HostCanvas.Height;

        // 清掉上一轮过渡可能残留的 Y 动画（动画值优先于本地值）
        Shift.BeginAnimation(TranslateTransform.YProperty, null);
        Shift.Y = h; // 新句从整行下方进来（超界部分由 ClipToBounds 裁掉）

        var sy = new DoubleAnimation(h, 0, dur) { EasingFunction = ease };
        var op = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        sy.Completed += (_, _) =>
        {
            Shift.BeginAnimation(TranslateTransform.YProperty, null);
            Shift.Y = 0;
        };
        Shift.BeginAnimation(TranslateTransform.YProperty, sy);
        MainText.BeginAnimation(OpacityProperty, op);

        // 旧句推上淡出；无旧句（首句/空行）则只做新句入场
        if (!string.IsNullOrEmpty(_lastText))
        {
            GhostText.Text = _lastText;
            GhostText.Visibility = Visibility.Visible;
            GhostShift.X = Shift.X; // 与新句静止位水平对齐
            GhostShift.Y = 0;

            var gy = new DoubleAnimation(0, -h, dur) { EasingFunction = ease };
            var gop = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            gy.Completed += (_, _) =>
            {
                GhostText.BeginAnimation(OpacityProperty, null);
                GhostShift.BeginAnimation(TranslateTransform.YProperty, null);
                GhostText.Visibility = Visibility.Collapsed;
                GhostText.Opacity = 1;
            };
            GhostShift.BeginAnimation(TranslateTransform.YProperty, gy);
            GhostText.BeginAnimation(OpacityProperty, gop);
        }
    }

    #endregion

    /// <summary>量出文本的自然宽度（不受视口约束）</summary>
    private double MeasureTextWidth()
    {
        if (string.IsNullOrEmpty(MainText.Text)) return 0;
        var ft = new FormattedText(
            MainText.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(MainText.FontFamily, MainText.FontStyle, MainText.FontWeight, MainText.FontStretch),
            MainText.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// 开始一行歌词的滚动。每次换句都调用（含同文本重复句）。
    /// elapsedSec: 本句已播放的秒数（含歌词提前量后的进度），用于对齐动画起点；
    /// totalSec: 本句总时长（下一句时间戳 - 本句时间戳）。
    /// 不超宽 → 句首左对齐静止；超宽 → 三段式滚动。
    /// </summary>
    public void StartLine(double elapsedSec, double totalSec)
    {
        if (!IsLoaded || MainText == null) return;

        // 预览模式：静止，句首左对齐（超宽右侧截断）
        if (PreviewMode)
        {
            Shift.X = 0;
            return;
        }

        // 停旧动画，拿回本地值控制权
        Shift.BeginAnimation(TranslateTransform.XProperty, null);

        HostCanvas.Height = Math.Max(1, TextFontSize * 1.4);
        // Collapsed 期间 ActualWidth=0，不能写进 Canvas 宽度（见 Relayout 同款保护）
        if (ActualWidth > 1) HostCanvas.Width = ActualWidth;

        double viewport = ActualWidth;
        double textWidth = MeasureTextWidth();
        double overflow = textWidth - viewport;

        if (overflow <= 0.5 || viewport <= 1)
        {
            Shift.X = 0; // 不超宽：句首左对齐静止
            return;
        }

        double T = Math.Max(0.5, totalSec);
        double head = Math.Min(1.2, T * 0.25); // 句首停留
        double tail = Math.Min(0.8, T * 0.20); // 句尾停留
        double scroll = T - head - tail;       // 滚动窗口
        if (scroll < 0.4)
        {
            // 句间隔太短：放弃停留，几乎全程用来滚
            head = 0; tail = 0; scroll = Math.Max(0.1, T - 0.1);
        }

        double e = Math.Clamp(elapsedSec, 0, T);
        double from, begin, dur;
        if (e <= head)
        {
            // 还在句首停留期：等 head-e 秒后从 0 开滚
            from = 0;
            begin = head - e;
            dur = scroll;
        }
        else
        {
            // 已进入滚动期：按已消耗比例算起点，剩余时长 = scroll*(1-p)
            // —— 这就是"滚不到尾部"的修复核心：结束点 = 句尾 - tail，不随启动延迟漂移
            double p = Math.Min(1.0, (e - head) / scroll);
            from = -overflow * p;
            dur = scroll * (1 - p);
            begin = 0;
            if (dur <= 0.05)
            {
                Shift.X = -overflow; // 已滚完：句尾停留
                return;
            }
        }

        Shift.X = from; // BeginTime 等待期间显示本地值
        var anim = new DoubleAnimation(from, -overflow, TimeSpan.FromSeconds(dur))
        {
            BeginTime = TimeSpan.FromSeconds(begin)
        };
        Shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>外部强制重排：版式切换（如 LyricOnly 折叠行重新显示）后由宿主调用</summary>
    public void RefreshLayout() => Relayout();

    /// <summary>
    /// 静止排布（无滚动信息时的兜底）：超宽 → 句尾顶住右边缘（左侧被 ClipToBounds 裁掉）；
    /// 不超宽 → 句首左对齐。Text/字号/视口变化时先走这里，随后 StartLine 会接管滚动。
    /// </summary>
    private void Relayout()
    {
        if (!IsLoaded || MainText == null) return;

        // Canvas 自身不会被子元素撑开，必须显式给高/宽，否则整行塌成 0 → 什么都不显示
        HostCanvas.Height = Math.Max(1, TextFontSize * 1.4);
        // 关键防御：Collapsed 期间 ActualWidth=0，把 0 写进 Canvas 宽度会持久化；
        // 恢复 Visible 后若 SizeChanged 因故未触发，整行宽 0 → hover 第二行直接消失。
        // 折叠期间保留旧宽度，恢复后由 SizeChanged / RefreshLayout 校正。
        if (ActualWidth > 1) HostCanvas.Width = ActualWidth;

        // 停动画：动画值优先于本地值，不停的话静态赋值不生效
        Shift.BeginAnimation(TranslateTransform.XProperty, null);

        double viewport = ActualWidth;
        double textWidth = MeasureTextWidth();
        double overflow = textWidth - viewport;

        // 超宽：普通模式句尾顶右边缘静止（左侧超出被 ClipToBounds 裁掉）；
        // 预览模式句首左对齐（右侧截断）；否则句首左对齐
        Shift.X = (overflow > 0.5 && viewport > 1 && !PreviewMode) ? -overflow : 0;
    }
}
