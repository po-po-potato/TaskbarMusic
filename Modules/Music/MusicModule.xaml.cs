using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// 音乐模块：SMTC 媒体跟随 + 歌词跑马灯 + 播控 + 背景跟随封面。
/// 全部逻辑自 MainWindow（M1 前 God Class）原样平移，渲染管道（ComposeLines/
/// ApplyLineLayout/SetLineContent 分层）零改动——重构的唯一目标是壳/模块分离，
/// 顺手优化一律禁止。
/// </summary>
public partial class MusicModule : UserControl, ITaskbarModule
{
    internal AppConfig Config => _config;

    private readonly DispatcherTimer _lyricTimer;
    private readonly AppConfig _config;
    private MediaService? _media;
    private readonly LyricService _lyricService = new();
    private List<LrcParser.LrcLine> _currentLyric = new();
    private List<LrcParser.LrcLine> _currentTranslation = new();
    private string _currentLyricKey = "";
    private string _lastLyricLine = "";
    private int _lastLyricIdx = -1; // 跨句检测：同文本重复句也要重启动画
    private string _currentArtistFallback = "";
    private string _currentTitleFallback = "";
    private LyricDisplayMode _lastRenderedMode = (LyricDisplayMode)(-1);

    // ===== 行槽位：布局无关的内容载体（XAML 控件名 TitleMarquee/ArtistMarquee 只是物理名）=====
    private readonly MarqueeTextBlock[] _lines;
    private static readonly Color DefaultBg = Color.FromArgb(0xE6, 0x10, 0x10, 0x10);
    // 歌词全局提前量：让歌词比真实进度提前 0.2s 显示，听感更跟手
    private static readonly TimeSpan LyricLeadTime = TimeSpan.FromSeconds(0.2);

    // hover 时让出歌词位置，回到"歌名/艺术家"经典两行；离开后再恢复歌词
    private bool _isHovering;

    private TaskbarShell? _shell;
    private MusicSettingsSection? _settingsSection;

    public string Id => "music";
    public string DisplayName => "音乐";
    public FrameworkElement View => this;
    public FrameworkElement? SettingsSection => _settingsSection ??= new MusicSettingsSection(this);

    public MusicModule(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _lines = new[] { TitleMarquee, ArtistMarquee };

        // 升位行渲染层放大变换（左下锚点）：动画期间视觉字号渐增而不触发布局重排
        _riseScale = new System.Windows.Media.ScaleTransform { CenterX = 0, CenterY = 1 };
        ArtistMarquee.RenderTransform = _riseScale;

        // Row2 落位微调变换（消收尾跳变用）：三行不等高（大 24.4 / 小 20.2）导致
        // PanelShift 按大字行高滚动时 Row2 终点偏差 ~4px——动画期间给 Row2 自身
        // 一个反向补偿偏移，让它精确落在 Row1 静态槽位，完成后重填零跳变。
        _row2Shift = new System.Windows.Media.TranslateTransform();
        NextMarquee.RenderTransform = _row2Shift;

        _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _lyricTimer.Tick += (_, _) => RefreshLyricLine();
    }

    private readonly System.Windows.Media.ScaleTransform _riseScale;
    private readonly System.Windows.Media.TranslateTransform _row2Shift;

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        _shell = shell;
        shell.BarDoubleClick += OpenSourceApp;

        // 布局校正的确定性事件源：模块自身 SizeChanged。
        // 模块从初始 0 → 实际高度（~48 DIP）的布局变化必然触发本事件，
        // 且触发时机在布局 pass 完成之后（ActualHeight 已正确）。
        // 不用壳窗口 SizeChanged（窗口尺寸未变时布局完成不派发）、
        // 不用 InvokeAsync(Loaded)（Loaded 优先级 6 < Render 7，跑在布局前）、
        // 不靠媒体事件兜底（Dispatcher.Invoke Normal 优先级可能插队到布局前）。
        // ——三者的时序缺陷均经实测"拖宽度封面才出现"证实。
        SizeChanged += (_, _) => LayoutByHeight();

        // 顺序对齐原 MainWindow_Loaded：先视觉初始化，再起媒体服务，再起歌词调度
        ApplyBackground(null);
        ApplyTextStyle();

        _media = new MediaService
        {
            PauseFadeOutSec = _config.PauseFadeOutSec
        };
        _media.MediaChanged += OnMediaChanged;
        try { _ = StartMediaWithRetryAsync(); }
        catch { }

        _lyricTimer.Start();
    }

    /// <summary>
    /// SMTC 启动防御（"连歌词都不显示"，实锤 42712 实例
    /// 活着但 6 分钟零 trace 记录 = 会话从未连上）：
    /// 原来的 `_ = _media.StartAsync()` fire-and-forget——RequestAsync()
    /// 悬挂（不返回）或抛异常都静默吞掉（外层 catch 捕不到 async 内部异常），
    /// 症状 = 进程活着、条上无歌名无歌词、日志零记录。
    /// 这里带 8s 超时 + 无限重试，失败写 trace.log 留痕，恢复时记录尝试次数。
    /// </summary>
    private async System.Threading.Tasks.Task StartMediaWithRetryAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var request = _media!.StartAsync();
                var timeout = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8));
                var done = await System.Threading.Tasks.Task.WhenAny(request, timeout);
                if (done == request)
                {
                    if (attempt > 1)
                        MediaService.Trace($"[SMTC] recovered after {attempt} attempts");
                    return;
                }
                MediaService.Trace($"[SMTC] start timeout attempt={attempt}");
            }
            catch (Exception ex)
            {
                MediaService.Trace($"[SMTC] start fault attempt={attempt}: {ex.Message}");
            }
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8));
        }
    }

    public void OnDetach()
    {
        if (_shell != null)
        {
            _shell.BarDoubleClick -= OpenSourceApp;
            _shell = null;
        }
        _lyricTimer.Stop();
    }

    public void OnHoverChanged(bool hovering)
    {
        FadeControls(hovering);
        if (_isHovering == hovering) return;
        _isHovering = hovering;

        _lastLyricLine = "<FORCE>";
        if (hovering)
        {
            // E 模式滚动动画期间不抢跑（动画完成后的渲染走 ComposeLines
            // 会自动带上 hover 让位状态）；非动画期立即让位
            if (!_followAnimating) RenderLyric(null);
        }
        else
        {
            RefreshLyricLine();
        }
    }

    /// <summary>
    /// 根据当前高度调整封面尺寸 + 文字左边距（模块 SizeChanged 时调用）
    /// </summary>
    private void LayoutByHeight()
    {
        if (CoverImage != null)
        {
            CoverImage.Width = ActualHeight;
            CoverImage.Height = ActualHeight;
        }
        if (TextClipWindow != null)
        {
            TextClipWindow.Margin = new Thickness(ActualHeight + 10, 0, 8, 0);
        }
    }

    // ===== 控件淡入淡出 =====
    private void FadeControls(bool show)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(150)
        };
        ControlsPanel.BeginAnimation(OpacityProperty, anim);
    }

    // ===== 双击拉起源程序（壳路由的 BarDoubleClick）=====
    private void OpenSourceApp()
    {
        var aumid = _media?.Current.SourceApp ?? "";
        if (string.IsNullOrWhiteSpace(aumid)) return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    // ===== SMTC 回调 =====
    private void OnMediaChanged(MediaInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            if (!info.HasContent)
            {
                _lines[0].Text = "未播放";
                _lines[1].Text = "";
                CoverImage.Source = null;
                PlayPauseButton.Content = "\uE768";
                ApplyBackground(null);
                _currentLyric = new();
                _currentTranslation = new();
                _currentLyricKey = "";
                _lastLyricLine = "";
                _lastLyricIdx = -1;
                _followIdx = -1;
                _followAnimating = false;
                _currentArtistFallback = "";
                _currentTitleFallback = "";
                // 停止播放时恢复经典两行版式（之前可能停在单行大字模式）
                ApplyLineLayout(2);
                return;
            }

            var newKey = $"{info.Title}__{info.Artist}";
            bool isSongChanged = newKey != _currentLyricKey;

            PlayPauseButton.Content = info.IsPlaying ? "\uE769" : "\uE768";
            if (!ReferenceEquals(CoverImage.Source, info.Thumbnail))
            {
                CoverImage.Source = info.Thumbnail;
                ApplyBackground(info.Thumbnail);
            }
            _currentArtistFallback = info.Artist;
            _currentTitleFallback = info.Title;

            if (isSongChanged)
            {
                _lastLyricLine = "";
                _lastLyricIdx = -1;
                _followIdx = -1; // 切歌重置：防止新歌第一句误触发垂直滚动（会从歌名两行滚过去）
                RenderLyric(null);
                EnsureLyricLoaded(info.Title, info.Artist);
            }
            else
            {
                ApplyStaticTextsByMode();
            }
        });
    }

    private void EnsureLyricLoaded(string title, string artist)
    {
        if (!_config.ShowLyric)
        {
            _currentLyric = new();
            _currentLyricKey = "";
            _lastLyricLine = "";
            return;
        }

        var key = $"{title}__{artist}";
        if (key == _currentLyricKey) return;
        _currentLyricKey = key;
        _currentLyric = new();
        _lastLyricLine = "";

        _ = LoadLyricAsync(key, title, artist);
    }

    private async System.Threading.Tasks.Task LoadLyricAsync(string requestKey, string title, string artist)
    {
        try
        {
            var result = await _lyricService.FetchLyricAsync(title, artist);
            if (requestKey != _currentLyricKey) return;

            var parsed = LrcParser.Parse(result.Lrc);
            // 双语合并 LRC（同时间戳 [原文, 翻译] 交替行）拆分：
            // 主序列只留原文（E 模式滚动每时间戳一次），其余行进翻译序列
            if (LrcParser.TrySplitBilingual(parsed, out var primary, out var embedded))
            {
                _currentLyric = primary;
                // tlyric 优先；没有 tlyric 时用内嵌翻译行
                _currentTranslation = string.IsNullOrEmpty(result.Translation)
                    ? embedded
                    : LrcParser.Parse(result.Translation);
            }
            else
            {
                _currentLyric = parsed;
                _currentTranslation = string.IsNullOrEmpty(result.Translation)
                    ? new()
                    : LrcParser.Parse(result.Translation);
            }
            RefreshLyricLine();
        }
        catch { }
    }

    // ===== E 模式（双行跟随）状态 =====
    private int _followIdx = -1;          // 上次渲染的当前句索引
    private bool _followAnimating;        // 垂直滚动动画进行中

    private void RefreshLyricLine()
    {
        if (!_config.ShowLyric || _media == null) return;
        // E 模式滚动动画进行中：拒绝一切重渲染（防抢跑）。
        // RefreshLyricLine 的触发源除了 250ms tick 还有媒体事件
        // （OnMediaChanged→ApplyStaticTextsByMode）、hover 移出、歌词加载完成——
        // 任何一条在 200ms 动画期间插入，RenderLyric 会直接把新内容画上去，
        // 跳过滚入过程 = "第三句突现"的直接根因（动画 200ms < tick 250ms 掩盖了它）。
        if (_followAnimating) return;
        var info = _media.Current;
        if (!info.HasContent) return;

        string? lyricText = null;
        string? nextText = null;          // E 模式 Row1：下一句
        string? nextNextText = null;      // E 模式 Row2：下下句（换句时从窗口底滚入的内容）
        string? translation = null;       // 翻译叠加次行：当前句翻译
        double lyricDurationSec = 0; // 当前句到下一句的时长（秒），滚动窗口基准
        double lyricElapsedSec = 0;  // 本句已消耗秒数（含提前量），滚动起点对齐用
        if (_currentLyric.Count > 0)
        {
            // 全局提前量 + 用户偏移（C12）：等价于"用 N 秒后的进度去查当前行"
            // （偏移 2026-08-26 改毫秒单位）
            var now = info.EstimateNowPosition() + LyricLeadTime
                      + TimeSpan.FromMilliseconds(_config.LyricOffsetMs);
            int idx = LrcParser.FindCurrentIndex(_currentLyric, now);
            if (idx >= 0)
            {
                lyricText = _currentLyric[idx].Text;
                var start = _currentLyric[idx].Time;
                // 时长 = 下一句时间戳 - 当前句时间戳；最后一句给个默认值
                lyricDurationSec = (idx + 1 < _currentLyric.Count)
                    ? (_currentLyric[idx + 1].Time - start).TotalSeconds
                    : 5.0;
                lyricElapsedSec = (now - start).TotalSeconds;

                // 翻译叠加：当前句翻译（时间戳对齐）
                translation = LrcParser.FindTranslation(_currentTranslation, start);
                // E 模式：下一句(Row1) + 下下句(Row2，换句时滚入)
                nextText = (idx + 1 < _currentLyric.Count) ? _currentLyric[idx + 1].Text : null;
                nextNextText = (idx + 2 < _currentLyric.Count) ? _currentLyric[idx + 2].Text : null;
            }

            // 跨句强制重渲染：同文本重复句（副歌等）也必须重启动画，
            // 否则上一遍的滚动状态会残留（停在句尾不动）
            if (idx != _lastLyricIdx)
            {
                _lastLyricIdx = idx;
                _lastLyricLine = "";
            }

            // E 模式换句：先播垂直滚动动画（旧下一句顶上来），动画完再渲染新内容。
            // 传入滚动前应显示的三行（旧视角）：Row0=当前前一句、Row1=当前句(将升顶)、
            // Row2=下一句(将滚入)。注意 idx 已是"新当前句"，故"当前句"对应 idx，
            // Row0 旧句=idx-1，Row1=idx，Row2=idx+1。
            if (_config.LyricMode == LyricDisplayMode.Follow &&
                idx >= 1 && idx != _followIdx && _followIdx >= 0 &&
                lyricText != null && !_followAnimating)
            {
                string prevText = _currentLyric[idx - 1].Text;   // 将滚出顶部
                string curText = lyricText;                       // 将升到主行（新当前句）
                string? scrollInText = nextText;                  // 将滚入次行（新下一句）
                _followIdx = idx;
                StartFollowScroll(prevText, curText, scrollInText,
                    newText: lyricText, durSec: lyricDurationSec, elapsedSec: lyricElapsedSec,
                    nextText: nextText, nextNextText: nextNextText);
                return; // 本次不直接渲染，动画完成后由回调渲染
            }
            _followIdx = idx;
        }

        RenderLyric(lyricText, lyricDurationSec, lyricElapsedSec, translation, nextText, nextNextText);
    }

    /// <summary>
    /// E 模式垂直滚动。动画前**显式填三行**（不依赖上次渲染状态，这是之前
    /// Row1/Row2 内容错乱/空白的根因）：
    ///   Row0(TitleMarquee) = scrollOutText（旧当前句，将滚出顶部，大字白）
    ///   Row1(ArtistMarquee) = riseText（新当前句，将升到主行，小字灰→动画放大变白）
    ///   Row2(NextMarquee)   = scrollInText（新下一句，将从窗口底滚入，小字灰）
    /// 面板整体上移一个大字行高，三行常驻（有真实布局位置）故 Row2 可见滚入。
    /// 完成后 Y 复位 + 按新句 RenderLyric 重填。
    /// </summary>
    private void StartFollowScroll(string scrollOutText, string riseText, string? scrollInText,
        string newText, double durSec, double elapsedSec, string? nextText, string? nextNextText)
    {
        _followAnimating = true;

        // ── 第 1 步：动画前显式铺三行（不依赖上次渲染状态——十一轮教训）──
        //   Row0 = 旧当前句（将滚出顶部，大字白）
        //   Row1 = 新当前句（将升到主行位，小字灰 → 动画放大变白）
        //   Row2 = 新下一句（将从窗口底滚入次行位，小字灰）
        TitleMarquee.PreviewMode = true;
        TitleMarquee.TextFontSize = _config.TitleFontSize;
        TitleMarquee.TextFontWeight = FontWeights.SemiBold;
        TitleMarquee.Foreground = new SolidColorBrush(Colors.White);
        TitleMarquee.Text = scrollOutText;
        ArtistMarquee.PreviewMode = true;
        ArtistMarquee.TextFontSize = _config.ArtistFontSize;
        // 升位行从滚动开始就是 SemiBold（目标字重）——WPF 的 FontWeight 不支持
        // 插值动画，若动画期间保持 Normal，完成重填切 TitleMarquee(SemiBold) 时
        // 字重瞬间跳变 = "bold 是滚动后突然变的"割裂感（）。
        // 起点的 Normal→SemiBold 跳变发生在 13px 灰小字上且被滚动运动掩盖，无感。
        ArtistMarquee.TextFontWeight = FontWeights.SemiBold;
        ArtistMarquee.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        ArtistMarquee.Text = riseText;
        NextMarquee.PreviewMode = true;
        NextMarquee.TextFontSize = _config.ArtistFontSize;
        NextMarquee.TextFontWeight = FontWeights.Normal;
        NextMarquee.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        NextMarquee.Text = scrollInText ?? "";

        // ── 第 2 步：单段垂直滚动动画（WPF 原生 AnimationClock，渲染层三属性）──
        // 滚动距离 = 大字行高 + 行距（Row1 升到 Row0 位需走的距离）。
        // Y 0 → -lineHeight：Row0 滚出顶、Row1 升主行位（放大+变白）、Row2 滚入次行位。
        //
        // 渲染保证（十六轮核心认知，实测方案）：三层结构下 Row2 恒在布局容器
        // （TextClip，Auto 三行高）内——WPF 必然渲染它；外层 TextClipWindow 是
        // 恒定两行高的裁剪窗口，Row2 平时被裁不可见，滚入时自然进入可视区。
        // 窗口高度全程不变 → 无 Center 偏移 → 无收尾跳变。
        //
        // 帧率（十四轮教训）：不用 DispatcherTimer 手动插值（队列抖动掉帧），
        // 三个动画全是渲染层属性，零布局重排：
        //   PanelShift.Y（TranslateTransform）/ RiseScale（ScaleTransform，替代
        //   TextFontSize 动画避免每帧 Relayout）/ riseBrush.Color（ColorAnimation）
        double lineHeight = TitleMarquee.ActualHeight + ArtistMarquee.Margin.Top;
        // Row2 落位补偿（消 4px 收尾跳变）：PanelShift 按大字行高滚（服务 Row1
        // 精确升位），Row2 终点会差 (lineHeight - 小字行距) ≈ 4px——动画期间给
        // Row2 自身反向偏移，终点精确落在 Row1 静态槽位，完成后重填零跳变。
        double row2Offset = lineHeight - (ArtistMarquee.ActualHeight + NextMarquee.Margin.Top);
        double durMs = 280;
        var ease = new System.Windows.Media.Animation.QuadraticEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var riseBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        ArtistMarquee.Foreground = riseBrush;
        double scaleTo = _config.TitleFontSize / Math.Max(1, _config.ArtistFontSize);

        var yAnim = new System.Windows.Media.Animation.DoubleAnimation(
            0, -lineHeight, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease };
        var sAnim = new System.Windows.Media.Animation.DoubleAnimation(
            1, scaleTo, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease };
        var cAnim = new System.Windows.Media.Animation.ColorAnimation(
            riseBrush.Color, Colors.White, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease };
        var r2Anim = new System.Windows.Media.Animation.DoubleAnimation(
            0, row2Offset, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease };

        yAnim.Completed += (_, _) =>
        {
            // 同帧收尾（防中间态闪现）：解除动画值拿回本地值控制权 → 复位四属性
            // → RenderLyric 重填（Row0=新当前句、Row1=新下一句、Row2=新下下句）。
            // 动画终点视觉 [Row1=新当前句@0, Row2=新下一句@24.4] 与重填后静态
            // [Row0=新当前句@0, Row1=新下一句@24.4] 完全重合——零跳变。
            PanelShift.BeginAnimation(TranslateTransform.YProperty, null);
            PanelShift.Y = 0;
            _riseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _riseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _riseScale.ScaleX = _riseScale.ScaleY = 1;
            _row2Shift.BeginAnimation(TranslateTransform.YProperty, null);
            _row2Shift.Y = 0;
            riseBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            FinishFollowScroll(newText, durSec, elapsedSec, nextText, nextNextText);
        };

        PanelShift.BeginAnimation(TranslateTransform.YProperty, yAnim);
        _riseScale.BeginAnimation(ScaleTransform.ScaleXProperty, sAnim);
        _riseScale.BeginAnimation(ScaleTransform.ScaleYProperty, sAnim);
        riseBrush.BeginAnimation(SolidColorBrush.ColorProperty, cAnim);
        _row2Shift.BeginAnimation(TranslateTransform.YProperty, r2Anim);
    }

    /// <summary>
    /// E 模式滚动完成：Y 复位 + 按新句重填三行（角色固定，不交换控件/指针）。
    /// nextNextText 一并传入——之前漏传导致静态期 Row2 空（十轮设计的"常驻装
    /// 下下句"失效，动画起点 Row2 从空变有内容）。
    /// </summary>
    private void FinishFollowScroll(string newText, double durSec, double elapsedSec,
        string? nextText, string? nextNextText)
    {
        PanelShift.Y = 0;
        _followAnimating = false;

        _lastLyricLine = "<FORCE>";
        RenderLyric(newText, durSec, elapsedSec, null, nextText, nextNextText);
    }

    /// <summary>
    /// 渲染入口：内容层产出"几行、每行放啥"，布局层按行数给样式，
    /// 最后逐行 SetLineContent。RenderLyric 自身不再关心模式差异。
    /// </summary>
    private void RenderLyric(string? lyricText, double lyricDurationSec = 0, double lyricElapsedSec = 0,
        string? translation = null, string? nextText = null, string? nextNextText = null)
    {
        var key = lyricText ?? "<NULL>";
        if (key == _lastLyricLine && _lastRenderedMode == _config.LyricMode) return;
        _lastLyricLine = key;
        _lastRenderedMode = _config.LyricMode;

        var specs = ComposeLines(lyricText, lyricDurationSec, lyricElapsedSec, translation, nextText);
        ApplyLineLayout(specs.Length);

        for (int i = 0; i < specs.Length && i < _lines.Length; i++)
            SetLineContent(_lines[i], specs[i].Text, specs[i].LyricWindowSec, specs[i].LyricElapsedSec,
                specs[i].Preview);

        // E 模式第三行（NextMarquee）：常驻装"下下句"，天然落窗口下方被裁——
        // 换句动画上移时从窗口底真实滚入。非 E 模式塌 0 恢复干净两行。
        ApplyFollowThirdLine(nextNextText);
    }

    /// <summary>
    /// E 模式第三行管理（三层结构下的职责重新划分）：
    /// - 布局层 TextClip：Auto 高度 = 三行自然高，永不限高——Row2 恒在布局
    ///   容器内，WPF 必然渲染（十六轮铁律：Row2 的测量约束被父容器可用高度
    ///   压没 = 不渲染 = "直接出现"的根因）
    /// - 裁剪层 TextClipWindow：锁两行高（Row0 大 + Row1 小），Row2 落窗口
    ///   下方被裁不可见；换句动画上移时自然滚入可视区。窗口高度恒定 →
    ///   动画全程无 Center 偏移
    /// - 非 E 模式：RowNext 塌 0 + Row2 Collapsed，恢复干净两行
    /// </summary>
    private void ApplyFollowThirdLine(string? nextNextText)
    {
        bool followMode = _config.LyricMode == LyricDisplayMode.Follow
                          && _config.ShowLyric && !_isHovering;

        if (followMode)
        {
            RowNext.Height = GridLength.Auto;
            NextMarquee.Visibility = Visibility.Visible;
            NextMarquee.TextFontSize = _config.ArtistFontSize;   // Row2 小字（与 Row1 同）
            NextMarquee.TextFontWeight = FontWeights.Normal;
            NextMarquee.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            NextMarquee.FontFamily = TitleMarquee.FontFamily;
            NextMarquee.PreviewMode = true;
            NextMarquee.Text = nextNextText ?? "";

            // 双层锁定（十七轮补丁：TextClip 必须【显式】三行高——Auto 不行）：
            // 布局约束链的规则是"显式 Height 的元素用自己的高度约束子元素"。
            // 若 TextClip=Auto，父窗口的 42.8 约束直通 TextPanel 三行 Grid，
            // Row2 的测量约束被压没 → 不渲染（"直接出现"复发）。显式锁三行高
            // 才能隔断约束直通，Row2 正常测量渲染，只是被外层窗口裁掉视觉。
            // 裁剪窗口锁两行高。两者都延迟到布局 pass 后取 ActualHeight。
            Dispatcher.InvokeAsync(() =>
            {
                double twoRows = TitleMarquee.ActualHeight + ArtistMarquee.Margin.Top
                                 + ArtistMarquee.ActualHeight;
                double threeRows = twoRows + NextMarquee.Margin.Top + NextMarquee.ActualHeight;
                if (twoRows > 1)
                {
                    TextClipWindow.Height = twoRows;   // 裁剪窗口：两行高
                    TextClip.Height = threeRows;       // 布局容器：显式三行高（渲染保证）
                }
                // DIAG5（临时）：静态几何——E 模式 vs 其他模式两行 Y 位置一致性取证
                D5();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            RowNext.Height = new GridLength(0);
            NextMarquee.Visibility = Visibility.Collapsed;
            NextMarquee.Text = "";
            // 非 E 模式也显式锁定两行高（"两行 Y 位置与其他模式
            // 不一致"）：Auto 的 DesiredSize 经布局取整 ≈41.6 ≠ 显式 42.8，Center
            // 偏移起点差 0.8px（DPI 下 1 物理像素可见）。统一显式锁定路径，
            // 两种模式高度计算一致 → 垂直位置一致。
            Dispatcher.InvokeAsync(() =>
            {
                double twoRows = TitleMarquee.ActualHeight + ArtistMarquee.Margin.Top
                                 + ArtistMarquee.ActualHeight;
                if (twoRows > 1)
                {
                    TextClipWindow.Height = twoRows;
                    TextClip.Height = twoRows; // 非 E 无第三行，两行即全部内容
                }
            }, DispatcherPriority.Loaded);
            // DIAG5（临时）：非 E 模式也记录静态几何，供与 E 模式对比
            D5();
        }
    }

    // ===== DIAG5（临时）：E 模式 vs 其他模式静态两行 Y 位置取证 =====
    // 记录锁定生效后的布局值（再延迟一帧，Background 优先级），写 static5.log。
    private void D5()
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                double row0Y = TitleMarquee.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
                double row1Y = ArtistMarquee.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
                string winH = double.IsNaN(TextClipWindow.Height) ? "Auto" : TextClipWindow.Height.ToString("F1");
                string clipH = double.IsNaN(TextClip.Height) ? "Auto" : TextClip.Height.ToString("F1");
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "TaskbarMusic", "static5.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} mode={_config.LyricMode} hover={_isHovering} " +
                    $"winH={winH} clipH={clipH} row0Y={row0Y:F1} row1Y={row1Y:F1} " +
                    $"row0H={TitleMarquee.ActualHeight:F1} row1H={ArtistMarquee.ActualHeight:F1}\n");
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    /// <summary>一行内容规格：LyricWindowSec 有值=歌词行（按句滚动），null=静态行（超宽单程滚）；Preview=预览行（静止截断）</summary>
    private sealed record LineSpec(string Text, double? LyricWindowSec = null, double LyricElapsedSec = 0,
        bool Preview = false);

    /// <summary>
    /// 内容层：唯一决定"几行、每行放啥、滚不滚"的地方。
    /// 加新模式 = 在这里多一个映射分支，布局层/渲染层零改动。
    /// </summary>
    private LineSpec[] ComposeLines(string? lyricText, double lyricDurationSec, double lyricElapsedSec,
        string? translation = null, string? nextText = null)
    {
        // hover 中或歌词总开关关闭：经典两行（歌名 / 艺术家）
        if (!_config.ShowLyric || _isHovering)
        {
            return new[]
            {
                new LineSpec(_currentTitleFallback),
                new LineSpec(_currentArtistFallback),
            };
        }

        bool hasLyric = lyricText != null;
        // 翻译叠加（2026-08-26 三轮定稿）：单行模式专属——歌名+歌词/双行两行已满，
        // 塞翻译必挤掉原生内容（歌名让位/预览让位），语义劣化不做；
        // 且双行换句滚动路径（StartFollowScroll→RenderLyricWithScroll）translation
        // 传 null，历史实现也从未真正稳定支持过
        bool showTranslation = _config.ShowTranslation && translation != null;
        double? window = hasLyric ? Math.Max(0.5, lyricDurationSec) : null;
        var joined = string.IsNullOrWhiteSpace(_currentArtistFallback)
            ? _currentTitleFallback
            : $"{_currentTitleFallback} - {_currentArtistFallback}";

        return _config.LyricMode switch
        {
            // 歌名+歌词：歌名 / 歌词（无歌词回退艺术家）
            LyricDisplayMode.ReplaceArtist => new[]
            {
                new LineSpec(_currentTitleFallback),
                new LineSpec(hasLyric ? lyricText! : _currentArtistFallback, window, lyricElapsedSec),
            },

            // 双行：当前句 / 下一句预览
            LyricDisplayMode.Follow when hasLyric => new[]
            {
                new LineSpec(lyricText!, window, lyricElapsedSec),
                new LineSpec(nextText ?? "", Preview: true), // 下一句预览：静止截断
            },

            // 单行：开翻译时追加翻译行（"原文（大字）/ 翻译"两行）；无歌词回退歌名+艺术家拼一行
            _ => showTranslation && hasLyric
                ? new[]
                {
                    new LineSpec(lyricText!, window, lyricElapsedSec),
                    new LineSpec(translation!),
                }
                : new[]
                {
                    new LineSpec(hasLyric ? lyricText! : joined, window, lyricElapsedSec),
                },
        };
    }

    /// <summary>静态行（歌名/艺术家）超宽时的单程滚动窗口（秒）：句首停留→匀速→停句尾</summary>
    private const double StaticLineWindowSec = 10.0;

    /// <summary>
    /// 通用行内容设置（各模式共用）：
    /// - lyricWindowSec 有值 → 歌词行：按句三段式滚动（同文本重复句也重启，由 RenderLyric 缓存控制频率）
    /// - lyricWindowSec 为 null → 静态行：超宽也给 StaticLineWindowSec 单程滚一遍停句尾；
    ///   仅文本变化时启动，跨句不重启（避免歌名行每句都从头重滚）
    /// - preview=true → 预览行：静止、超宽左侧截断（E 模式次行）
    /// 文本变化时按 LineTransition 设置做行内过渡（C11）。
    /// E 模式（Follow）恒禁行内过渡——垂直滚动本身就是该模式的换句过渡，
    /// 叠加会在滚动完成后重放一次 fade 造成闪烁。
    /// </summary>
    private void SetLineContent(MarqueeTextBlock line, string? text,
        double? lyricWindowSec = null, double lyricElapsedSec = 0, bool preview = false)
    {
        string t = text ?? "";
        bool textChanged = line.Text != t;
        line.PreviewMode = preview;
        line.Text = t; // 触发 OnContentChanged → Relayout 静止兜底（句尾顶右/预览左截断）

        if (textChanged && _config.LyricMode != LyricDisplayMode.Follow)
            line.BeginTransition(_config.LineTransition); // C11：无/淡入/上滑

        if (lyricWindowSec is > 0.5)
            line.StartLine(lyricElapsedSec, lyricWindowSec.Value);
        else if (textChanged && !preview)
            line.StartLine(0, StaticLineWindowSec);
    }

    private void ApplyStaticTextsByMode()
    {
        _lastLyricLine = "<FORCE>";
        // 直接走 RefreshLyricLine 完整路径：算出 elapsed/total，
        // 否则滚动窗口为 0 会导致动画用兜底时长乱滚
        RefreshLyricLine();
    }

    /// <summary>
    /// 布局层：只看"几行"给样式，不知道每行放什么内容。
    /// 2 行：主行(TitleFontSize,SemiBold) + 次行(ArtistFontSize,Normal)，次行可见
    /// 1 行：单行(LyricOnlyFontSize,Normal)，次行折叠
    /// </summary>
    private bool _line1Collapsed;

    private void ApplyLineLayout(int lineCount = 2)
    {
        // 行样式代码统一管理（不依赖 XAML 静态值）：E 模式滚动动画会临时改
        // ArtistMarquee 的字号/颜色（升位渐变），每次渲染必须在此归位到目标样式，
        // 否则动画残留值会带到下一句。_lines[0/1] 固定 = TitleMarquee/ArtistMarquee（不再交换）。
        var mainColor = new SolidColorBrush(Colors.White);
        var subColor = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        if (lineCount >= 2)
        {
            bool wasCollapsed = _line1Collapsed;
            _line1Collapsed = false;

            RowArtist.Height = GridLength.Auto;
            _lines[1].Visibility = Visibility.Visible;
            _lines[0].Margin = new Thickness(0);
            _lines[1].Margin = new Thickness(0, 2, 0, 0);
            _lines[0].TextFontSize = _config.TitleFontSize;
            _lines[0].TextFontWeight = FontWeights.SemiBold;
            _lines[0].Foreground = mainColor;
            _lines[1].TextFontSize = _config.ArtistFontSize;
            _lines[1].TextFontWeight = FontWeights.Normal;
            _lines[1].Foreground = subColor;

            // 仅从折叠态恢复（C 模式切走 / hover 进入）时才延迟重排：
            // 一是无条件重排会停掉刚启动的滚动动画（RefreshLayout 停动画），
            // 二是折叠期间 ActualWidth=0，要等布局 pass 跑完再校正 Canvas 宽度
            if (wasCollapsed)
            {
                Dispatcher.InvokeAsync(() => _lines[1].RefreshLayout(),
                    DispatcherPriority.Background);
            }
        }
        else
        {
            _line1Collapsed = true;
            // 单行模式：次行整行隐藏（Collapsed 确保不渲染，
            // 不能只靠行高 0——Canvas 固定高度会溢出显示），主行用大字号
            RowArtist.Height = new GridLength(0);
            _lines[1].Visibility = Visibility.Collapsed;
            _lines[1].Margin = new Thickness(0);
            _lines[0].TextFontSize = _config.LyricOnlyFontSize;
            _lines[0].TextFontWeight = FontWeights.Normal;
            _lines[0].Foreground = mainColor;
        }
    }

    /// <summary>设置窗口切歌词开关 / 切模式时调用</summary>
    public void ApplyLyricToggle()
    {
        // 三个分支最终都走 RenderLyric → ApplyLineLayout，这里无需先布局
        if (!_config.ShowLyric)
        {
            _lastLyricLine = "<FORCE>";
            RenderLyric(null);
        }
        else if (_media != null && _media.Current.HasContent)
        {
            _lastLyricLine = "<FORCE>";
            if (_currentLyric.Count == 0)
            {
                _currentLyricKey = "";
                EnsureLyricLoaded(_media.Current.Title, _media.Current.Artist);
                RenderLyric(null);
            }
            else
            {
                RefreshLyricLine();
            }
        }
        else
        {
            _lastLyricLine = "<FORCE>";
            RenderLyric(null);
        }
    }

    // ===== 背景跟随封面 =====
    private void ApplyBackground(BitmapSource? cover)
    {
        Color baseColor;
        if (_config.BackgroundFollowCover && cover != null)
        {
            try
            {
                baseColor = Darken(ExtractDominantColor(cover), 0.45);
            }
            catch { baseColor = DefaultBg; }
        }
        else
        {
            baseColor = DefaultBg;
        }

        var solid = Color.FromArgb(0xE6, baseColor.R, baseColor.G, baseColor.B);
        var transparent = Color.FromArgb(0x00, baseColor.R, baseColor.G, baseColor.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop(solid, 0.0));
        brush.GradientStops.Add(new GradientStop(solid, 0.85));
        brush.GradientStops.Add(new GradientStop(transparent, 1.0));

        ModuleRoot.Background = brush;
    }

    private static Color ExtractDominantColor(BitmapSource src)
    {
        const int size = 16;
        var resized = new TransformedBitmap(src, new ScaleTransform((double)size / src.PixelWidth, (double)size / src.PixelHeight));
        var formatted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);

        int stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);

        long r = 0, g = 0, b = 0;
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte B = pixels[i], G = pixels[i + 1], R = pixels[i + 2], A = pixels[i + 3];
            if (A < 128) continue;
            int max = Math.Max(R, Math.Max(G, B));
            int min = Math.Min(R, Math.Min(G, B));
            if (max < 30 || min > 230) continue;
            r += R; g += G; b += B; count++;
        }
        if (count == 0) return Color.FromRgb(0x10, 0x10, 0x10);
        return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static Color Darken(Color c, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));
    }

    // ===== 播控按钮 =====
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_media != null) await _media.TogglePlayPauseAsync();
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_media != null) await _media.PreviousAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_media != null) await _media.NextAsync();
    }

    // ===== 设置窗回调（MusicSettingsSection 事件触发）=====

    public void RefreshBackgroundFromCurrentCover()
    {
        if (CoverImage.Source is BitmapSource bs) ApplyBackground(bs);
        else ApplyBackground(null);
    }

    /// <summary>把配置里的字体应用到所有行（字号/字重归 ApplyLineLayout 管）</summary>
    public void ApplyTextStyle()
    {
        var family = new FontFamily("Segoe UI");
        try { family = new FontFamily(_config.FontFamily); } catch { }
        foreach (var line in _lines) line.FontFamily = family;
        ApplyLineLayout(2);
    }

    /// <summary>设置窗改"暂停淡出补偿"时同步到 MediaService</summary>
    public void ApplyPauseFadeOut()
    {
        if (_media != null) _media.PauseFadeOutSec = _config.PauseFadeOutSec;
    }
}
