using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace TaskbarMusic;

/// <summary>
/// SMTC 监视器（诊断工具窗）：实时展示系统媒体会话原始值 + 全量事件/决策日志。
///
/// 上半会话面板：直接枚举 MediaService.Manager（SMTC 管理器）的全部会话，
/// 展示的 Title/Status/Position 等均为系统原始上报值——未经 MediaService 的
/// 暂停防脏/倒退守卫/进度校准，方便对照"系统给了什么 vs 条上显示了什么"。
/// 下半日志流：订阅 MediaService.TraceBroadcast（trace.log 全量内容的实时镜像，
/// 含 [EVT] 原始事件、[SESSION] 会话切换、[PUSH]/[POLL] 防脏决策、[MEDIA]/[LYRIC] 模块行为）。
///
/// 与设置窗同款骨架：FluentWindow + TitleBar + ThemeService 单点主题/材质
/// （MapBackdropSafe 环境降级同享）；条背景防护在 ApplySystemTheme 内部，无额外处理。
/// 打开入口：条右键 / 托盘右键菜单"SMTC 监视器..."（TaskbarShell.OpenSmtcMonitor）。
/// </summary>
public partial class SmtcMonitorWindow : FluentWindow
{
    private readonly MediaService _media;
    private readonly AppConfig _config;

    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>会话刷新重入守卫（上一轮 TryGetMediaPropertiesAsync 未返回时不叠发）</summary>
    private bool _refreshing;

    private bool _logPaused;
    private int _lastSessionCount = -1;
    private Wpf.Ui.Appearance.ApplicationTheme _theme;
    private WindowBackdrop _configBackdrop;

    private const int LogCap = 500;

    public SmtcMonitorWindow(MediaService media, AppConfig config)
    {
        InitializeComponent();
        _media = media;
        _config = config;

        // 主题单点（ThemeService）：与 SettingsWindow 同款——按 config 颜色模式，
        // 材质用 Safe 映射（系统透明关闭/RDP 下降级 None 防整窗露白）
        _theme = ThemeService.ApplyTheme(config.AppTheme);
        RootTitleBar.ApplicationTheme = _theme;
        _configBackdrop = config.WindowBackdrop;
        WindowBackdropType = ThemeService.MapBackdropSafe(_configBackdrop);

        LogList.ItemsSource = _log;
        SessionsList.ItemsSource = Array.Empty<SmtcSessionView>();

        MediaService.TraceBroadcast += OnTraceLine;
        Closed += (_, _) => MediaService.TraceBroadcast -= OnTraceLine;

        _timer.Tick += (_, _) => RefreshSessions();
        Loaded += (_, _) =>
        {
            RefreshSessions();
            _timer.Start();
        };

        MediaService.Trace("[MONITOR] opened");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyDarkModeAttribute(this, _theme);
    }

    // ===== 实时日志流 =====

    /// <summary>TraceBroadcast 处理（任意线程触发）：编组到 UI 线程入列。
    /// 暂停时直接丢弃（诊断窗语义：暂停 = 抓现场，不放行新行）。</summary>
    private void OnTraceLine(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_logPaused) return;
            _log.Add(line);
            while (_log.Count > LogCap) _log.RemoveAt(0);
            if (AutoScrollBox.IsChecked == true)
            {
                // 必须先 UpdateLayout：Add 与 ScrollToEnd 同帧时 ExtentHeight
                // 尚未更新，会滚不到真底部（停在倒数第二行）
                LogScroll.UpdateLayout();
                LogScroll.ScrollToEnd();
            }
        });
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _logPaused = !_logPaused;
        PauseButton.Content = _logPaused ? "继续" : "暂停";
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _log.Clear();

    private void OnCopyAumidClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string aumid })
        {
            try { Clipboard.SetText(aumid); } catch { /* 剪贴板偶发占用失败无碍 */ }
        }
    }

    // ===== 会话面板（原始值，1s 刷新） =====

    private async void RefreshSessions()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var manager = _media.Manager;
            if (manager == null)
            {
                SessionsStatus.Text = "SMTC 未连接（启动中 / 重试中，见日志）";
                SessionsEmpty.Visibility = Visibility.Collapsed;
                return;
            }

            var currentAumid = "";
            try { currentAumid = manager.GetCurrentSession()?.SourceAppUserModelId ?? ""; }
            catch { /* 会话瞬灭吞掉 */ }

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = manager.GetSessions(); }
            catch (Exception ex)
            {
                SessionsStatus.Text = "枚举会话失败: " + ex.Message;
                return;
            }

            var views = new List<SmtcSessionView>();
            foreach (var s in sessions)
            {
                try
                {
                    var playback = s.GetPlaybackInfo();
                    var timeline = s.GetTimelineProperties();
                    string title = "", artist = "";
                    try
                    {
                        var props = await s.TryGetMediaPropertiesAsync();
                        title = props?.Title ?? "";
                        artist = props?.Artist ?? "";
                    }
                    catch { /* 属性瞬灭吞掉 */ }

                    var aumid = s.SourceAppUserModelId ?? "?";
                    views.Add(new SmtcSessionView(
                        aumid, title, artist,
                        playback?.PlaybackStatus.ToString() ?? "Unknown",
                        playback?.PlaybackRate ?? 1.0,
                        timeline?.Position ?? TimeSpan.Zero,
                        timeline?.EndTime ?? TimeSpan.Zero,
                        isCurrent: aumid == currentAumid,
                        isBrowser: MusicModule.IsBrowserSource(aumid)));
                }
                catch { /* 单会话失败不拖垮整轮 */ }
            }

            SessionsList.ItemsSource = views;
            SessionsEmpty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SessionsStatus.Text = $"SMTC 已连接 · {views.Count} 个会话";

            // 会话数变化落 trace（静默观察系统层面的会话增减，不逐秒刷屏）
            if (views.Count != _lastSessionCount)
            {
                MediaService.Trace($"[MONITOR] sessions={views.Count}");
                _lastSessionCount = views.Count;
            }
        }
        catch (Exception ex)
        {
            SessionsStatus.Text = "刷新失败: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }
}

/// <summary>会话卡片视图模型（一次性快照，整列表每秒整体重建，无需变更通知）</summary>
public class SmtcSessionView
{
    public string Aumid { get; }
    public string DisplayLine { get; }
    public string StatusLine { get; }
    public bool IsCurrent { get; }
    public bool IsBrowser { get; }
    /// <summary>无 Artist = 视频类会话，主窗口对它只显示标题不搜词（复用 MusicModule.IsSongLike 保持单一真源）</summary>
    public bool IsVideoLike { get; }

    public SmtcSessionView(string aumid, string title, string artist, string status,
        double rate, TimeSpan position, TimeSpan duration, bool isCurrent, bool isBrowser)
    {
        Aumid = aumid;
        DisplayLine = string.IsNullOrWhiteSpace(title)
            ? "（无标题）"
            : string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}";
        StatusLine = $"{status} · {rate:F2}x · {Fmt(position)} / {Fmt(duration)}";
        IsCurrent = isCurrent;
        IsBrowser = isBrowser;
        IsVideoLike = !MusicModule.IsSongLike(artist);
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
