using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        if (e.Property == TextProperty) m.MainText.Text = (string)e.NewValue;
        m.Relayout();
    }

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
    /// 换句过渡（150ms，克制风格）：
    /// Fade=纯淡入；Slide=淡入+从下方 3px 上滑；None=不动画
    /// </summary>
    public void BeginTransition(LineTransition kind)
    {
        if (kind == LineTransition.None) return;

        var ease = new System.Windows.Media.Animation.QuadraticEase();
        var dur = TimeSpan.FromMilliseconds(150);

        var op = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        MainText.BeginAnimation(OpacityProperty, op);

        if (kind == LineTransition.Slide)
        {
            var sy = new DoubleAnimation(3, 0, dur) { EasingFunction = ease };
            Shift.BeginAnimation(TranslateTransform.YProperty, sy);
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
