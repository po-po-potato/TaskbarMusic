using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMusic;

/// <summary>
/// 当前媒体信息快照
/// </summary>
public class MediaInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public bool IsPlaying { get; set; }
    public string SourceApp { get; set; } = "";

    /// <summary>SMTC 上次上报的播放进度（位置）</summary>
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    /// <summary>SMTC 上次上报进度的本地 UTC 时间，用来推算"现在"的真实进度</summary>
    public DateTime PositionUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>歌曲总长（可能为 0）</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    /// <summary>播放速率（绝大多数为 1.0）</summary>
    public double PlaybackRate { get; set; } = 1.0;

    public bool HasContent => !string.IsNullOrWhiteSpace(Title);

    /// <summary>根据上次上报时间推算"此刻"的真实进度</summary>
    public TimeSpan EstimateNowPosition()
    {
        if (!IsPlaying) return Position;
        var elapsed = DateTime.UtcNow - PositionUpdatedAtUtc;
        var rate = PlaybackRate <= 0 ? 1.0 : PlaybackRate;
        var pos = Position + TimeSpan.FromSeconds(elapsed.TotalSeconds * rate);
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        return pos;
    }
}

/// <summary>
/// 通过 Windows SMTC 监听全局媒体会话（覆盖 Spotify / 网易云 / QQ音乐 / 浏览器音乐 / 本地播放器等）
/// </summary>
public class MediaService
{
    public event Action<MediaInfo>? MediaChanged;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private MediaInfo _last = new();
    // 兜底轮询：网易云等播放器拖进度条不发 TimelinePropertiesChanged，
    // 这里 1 秒拉一次 timeline，仅当 LastUpdatedTime 变化（说明 SMTC 真的更新过）
    // 才把新 Position 写入 _last——避免被陈旧快照拉回旧值。
    private DispatcherTimer? _pollTimer;
    private DateTimeOffset _lastSeenTimelineUpdate = DateTimeOffset.MinValue;

    // ===== 暂停/恢复的进度防脏状态 =====
    // 暂停瞬间部分播放器会把 SMTC timeline 归零（脏 0）；恢复播放瞬间也可能再推一次。
    // 简化规则（用户拍板）：拖动一律不管——
    //   暂停态/恢复瞬间的倒退一律视为脏值，沿用冻结位置；
    //   播放态下 SMTC 主动上报的新时间（stamp 变化）照单全收。
    private DateTime? _resumedAtUtc;     // 恢复播放时刻（恢复初期 PollTimeline 防脏窗口）

    // ===== 诊断日志（排查"多次暂停后进度变慢"用） =====
    // 写 %APPDATA%\TaskbarMusic\trace.log；>2MB 轮转成 .old
    private int _hbCount;
    private static readonly object _traceLock = new();
    private static string TracePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarMusic", "trace.log");

    internal static void Trace(string line)
    {
        try
        {
            lock (_traceLock)
            {
                var fi = new FileInfo(TracePath);
                if (fi.Exists && fi.Length > 2_000_000)
                {
                    fi.MoveTo(TracePath + ".old", true);
                }
                File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
            }
        }
        catch { /* 诊断日志失败不影响功能 */ }
    }

    /// <summary>主窗口可访问最近一次推送的快照（用来推算实时进度）</summary>
    public MediaInfo Current => _last;

    /// <summary>
    /// 暂停淡出补偿（秒），由 MainWindow 从配置注入：
    /// 点击暂停后音频还淡出播放这段时间才真正停止，但 Paused 事件淡出开始就到，
    /// 冻结值 = 事件时刻外推 + 此补偿，恢复后才能与真实进度对齐。
    /// </summary>
    public double PauseFadeOutSec { get; set; }

    public async Task StartAsync()
    {
        _pollTimer?.Stop(); // 启动重试防御：旧的轮询 timer 不停会叠加订阅
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => OnSessionChanged();
        OnSessionChanged();

        // 兜底轮询：1s 检查一次 timeline.LastUpdatedTime 是否变了，
        // 变了才视为 SMTC 真有新进度（应对网易云这种拖进度不发事件的播放器）
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => PollTimeline();
        _pollTimer.Start();
    }

    private void PollTimeline()
    {
        try
        {
            if (_currentSession == null || !_last.HasContent) return;
            var timeline = _currentSession.GetTimelineProperties();
            if (timeline == null) return;

            // 心跳（每 5s 一条）：SMTC 实时 position vs 外推 estimate，观察漂移趋势
            if (++_hbCount % 5 == 0)
            {
                Trace($"[HBEAT] smtc={timeline.Position.TotalSeconds:F2} " +
                      $"est={_last.EstimateNowPosition().TotalSeconds:F2} " +
                      $"play={_last.IsPlaying} rate={_last.PlaybackRate:F2} " +
                      $"dur={_last.Duration.TotalSeconds:F0} " +
                      $"stamp={timeline.LastUpdatedTime:HH:mm:ss}");
            }

            // LastUpdatedTime 当探针：值变了才说明 SMTC 真的写入过新 timeline
            // （应对部分播放器拖进度条不发 TimelinePropertiesChanged 事件）
            var stamp = timeline.LastUpdatedTime;
            if (stamp == _lastSeenTimelineUpdate) return;
            _lastSeenTimelineUpdate = stamp;

            var realPos = timeline.Position;
            var estimated = _last.EstimateNowPosition();
            double delta = (realPos - estimated).TotalSeconds;

            if (delta > 0.5)
            {
                // 前进跳变（>0.5s）：积极采纳校准。
                // 暂停淡出结束时 SMTC 写的最终位置（比点击时刻前进一个淡出时长）、
                // 恢复播放后追平事件延迟，都靠这条路径记账——之前 1.5s 对称阈值
                // 把这些小前进全吞掉，多次暂停后进度累积落后（越走越慢）。
                // 脏值模式是归零（倒退），前进方向无脏值风险。
                Trace($"[POLL] fwd delta={delta:F2} real={realPos.TotalSeconds:F2} est={estimated.TotalSeconds:F2} play={_last.IsPlaying} -> ADOPT");
                _last.Position = realPos;
                _last.PositionUpdatedAtUtc = DateTime.UtcNow;
            }
            else if (delta < -1.5)
            {
                // 倒退跳变：只在"持续播放态"可信（SMTC 主动上报的新时间）：
                // - 暂停期间：一律视为脏值丢弃（部分播放器暂停时上报归零 timeline），沿用冻结位置
                // - 恢复播放初期（≤3s）：恢复瞬间也可能推脏 0，同样丢弃
                // - 正常播放中：照单全收（含拖动，stamp 已变化说明是真实上报）
                bool retreat = -delta > 2;
                bool suspectWindow = !_last.IsPlaying ||
                    (_resumedAtUtc is DateTime r && (DateTime.UtcNow - r).TotalSeconds < 3);
                if (!(retreat && suspectWindow))
                {
                    Trace($"[POLL] back delta={delta:F2} real={realPos.TotalSeconds:F2} est={estimated.TotalSeconds:F2} play={_last.IsPlaying} -> ADOPT");
                    _last.Position = realPos;
                    _last.PositionUpdatedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    Trace($"[POLL] back delta={delta:F2} real={realPos.TotalSeconds:F2} est={estimated.TotalSeconds:F2} play={_last.IsPlaying} -> DROP(suspect)");
                }
            }
        }
        catch { /* 忽略偶发错误 */ }
    }

    private void OnSessionChanged()
    {
        // 解绑旧会话
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = _manager?.GetCurrentSession();

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = PushCurrentAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = PushCurrentAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = PushCurrentAsync();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => _ = PushCurrentAsync();

    private async Task PushCurrentAsync()
    {
        try
        {
            if (_currentSession == null)
            {
                _last = new MediaInfo();
                MediaChanged?.Invoke(_last);
                return;
            }

            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            var timeline = _currentSession.GetTimelineProperties();

            var info = new MediaInfo
            {
                Title = props?.Title ?? "",
                Artist = props?.Artist ?? "",
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                SourceApp = _currentSession.SourceAppUserModelId ?? "",
                PlaybackRate = playback?.PlaybackRate ?? 1.0,
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline?.EndTime ?? TimeSpan.Zero,
                // 不能用 timeline.LastUpdatedTime——某些播放器（含 Spotify、网易云）这个字段
                // 不是事件触发瞬间的时间，而是历史值，会导致启动时 EstimateNowPosition 算出
                // 远超 Duration 的位置，歌词卡在最后一行。
                // 用 UtcNow 作为"我刚拿到这个 Position 的时间"才稳。
                PositionUpdatedAtUtc = DateTime.UtcNow
            };

            // 加载封面
            if (props?.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    ms.Position = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    info.Thumbnail = bmp;
                }
                catch { /* 封面加载失败不影响其他字段 */ }
            }

            // 暂停瞬间部分播放器会把 SMTC timeline 归零/上报脏值（position=0），
            // 若照单全收，恢复播放后歌词会从第一句重来。防倒退守卫：
            // 同一首歌 + 非 Playing + 新位置比推算位置倒退 >2s → 视为脏值，
            // 沿用推算位置冻结。切歌（Title/Artist 变了）不走守卫——新歌 position 从头是正常的。
            //
            // 过渡态（暂停/恢复瞬间）不只 position 脏：PlaybackRate 可能是渐变过渡值
            // （外推时钟被拉慢 → 进度越走越慢），Duration 可能被写小（estimate 被 clamp 压住）。
            // 这两个字段一律沿用上次的正常值。
            bool sameSong = _last.HasContent
                && _last.Title == info.Title
                && _last.Artist == info.Artist;
            bool transition = sameSong && (!info.IsPlaying || !_last.IsPlaying);
            if (transition)
            {
                info.PlaybackRate = _last.PlaybackRate;
                if (_last.Duration > TimeSpan.Zero) info.Duration = _last.Duration;
            }

            if (sameSong && !info.IsPlaying)
            {
                // 暂停冻结 = 事件时刻外推（不在这里加补偿——补偿统一在恢复锚点结算，
                // 因为每次暂停-恢复循环的总滞后 = 淡出残余 + 恢复事件延迟，恢复才是误差结算点）
                var rawEst = _last.EstimateNowPosition();
                if ((rawEst - info.Position).TotalSeconds > 2)
                {
                    Trace($"[PUSH] pause-dirty new={info.Position.TotalSeconds:F2} rawEst={rawEst.TotalSeconds:F2} -> OVERRIDE frozen");
                    info.Position = rawEst;
                    info.PositionUpdatedAtUtc = DateTime.UtcNow;
                }
            }
            else if (sameSong && !_last.IsPlaying && info.IsPlaying)
            {
                // 恢复瞬间：同一首歌的倒退 >2s 视为脏值，锚定 = 冻结位置 + 暂停淡出补偿。
                // 补偿涵盖整个循环的滞后：点击暂停后音频淡出播 X 秒才真停 + 恢复事件异步延迟。
                // timeline 正常的播放器上报真实位置（无倒退）走默认采纳，不受此影响。
                _resumedAtUtc = DateTime.UtcNow;
                if ((_last.Position - info.Position).TotalSeconds > 2)
                {
                    var anchor = _last.Position + TimeSpan.FromSeconds(Math.Max(0, PauseFadeOutSec));
                    Trace($"[PUSH] resume-dirty new={info.Position.TotalSeconds:F2} keep={_last.Position.TotalSeconds:F2} fade={PauseFadeOutSec:F1} -> ANCHOR {anchor.TotalSeconds:F2}");
                    info.Position = anchor;
                    info.PositionUpdatedAtUtc = DateTime.UtcNow;
                }
            }

            if (sameSong)
            {
                Trace($"[PUSH] title={info.Title} " +
                      $"last{{pos={_last.Position.TotalSeconds:F2},upd={_last.PositionUpdatedAtUtc:HH:mm:ss.fff},rate={_last.PlaybackRate:F2},dur={_last.Duration.TotalSeconds:F0},play={_last.IsPlaying}}} " +
                      $"new{{pos={info.Position.TotalSeconds:F2},rate={info.PlaybackRate:F2},dur={info.Duration.TotalSeconds:F0},play={info.IsPlaying}}}");
            }

            _last = info;
            // 同步轮询基线，避免轮询又把刚事件处理过的 timeline 算成"新跳变"
            if (timeline != null) _lastSeenTimelineUpdate = timeline.LastUpdatedTime;
            MediaChanged?.Invoke(info);
        }
        catch (Exception)
        {
            _last = new MediaInfo();
            MediaChanged?.Invoke(_last);
        }
    }

    /// <summary>播放/暂停切换</summary>
    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession != null)
            await _currentSession.TryTogglePlayPauseAsync();
    }

    /// <summary>上一首</summary>
    public async Task PreviousAsync()
    {
        if (_currentSession != null)
            await _currentSession.TrySkipPreviousAsync();
    }

    /// <summary>下一首</summary>
    public async Task NextAsync()
    {
        if (_currentSession != null)
            await _currentSession.TrySkipNextAsync();
    }
}
