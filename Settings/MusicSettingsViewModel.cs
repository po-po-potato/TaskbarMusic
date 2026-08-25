using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>
/// 音乐模块设置分区 VM：外观（背景跟随/歌词开关/模式）+ 播放（淡出补偿）+ 字体。
/// 双向绑定 AppConfig，变更即时 Save 并通过事件通知 MusicModule 刷新。
/// （自原 SettingsViewModel 拆出，去掉壳相关字段，逻辑不变）
/// </summary>
public partial class MusicSettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    // 系统字体枚举是同步 IO，进程级缓存一份避免每次开窗都卡
    private static readonly List<string> _systemFonts = LoadSystemFonts();

    private static List<string> LoadSystemFonts()
    {
        var list = new List<string>();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var src = f.Source;
            if (!string.IsNullOrWhiteSpace(src)) list.Add(src);
        }
        list.Sort(System.StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>字体/字号变化时刷新</summary>
    public event Action? TextStyleChanged;

    /// <summary>显示歌词开关 / 模式切换时刷新</summary>
    public event Action? LyricToggleChanged;

    /// <summary>跟随封面取色变化时刷新</summary>
    public event Action? FollowCoverChanged;

    /// <summary>暂停淡出补偿变化时刷新（注入 MediaService）</summary>
    public event Action? PauseFadeOutChanged;

    public ReadOnlyCollection<string> SystemFonts => _systemFonts.AsReadOnly();

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _titleFontSize;

    [ObservableProperty]
    private double _artistFontSize;

    [ObservableProperty]
    private bool _backgroundFollowCover;

    [ObservableProperty]
    private bool _showLyric;

    [ObservableProperty]
    private LyricDisplayMode _lyricMode;

    [ObservableProperty]
    private double _pauseFadeOutSec;

    [ObservableProperty]
    private double _lyricOffsetSec;

    [ObservableProperty]
    private LineTransition _lineTransition;

    public MusicSettingsViewModel(AppConfig config)
    {
        _config = config;
        _fontFamily = config.FontFamily;
        _titleFontSize = config.TitleFontSize;
        _artistFontSize = config.ArtistFontSize;
        _backgroundFollowCover = config.BackgroundFollowCover;
        _showLyric = config.ShowLyric;
        _lyricMode = config.LyricMode;
        _pauseFadeOutSec = config.PauseFadeOutSec;
        _lyricOffsetSec = config.LyricOffsetSec;
        _lineTransition = config.LineTransition;
    }

    // ===== 属性变化回调：写回 config + Save + 通知模块 =====

    partial void OnFontFamilyChanged(string value)
    {
        _config.FontFamily = value;
        _config.Save();
        TextStyleChanged?.Invoke();
    }

    partial void OnTitleFontSizeChanged(double value)
    {
        _config.TitleFontSize = value;
        _config.Save();
        TextStyleChanged?.Invoke();
    }

    partial void OnArtistFontSizeChanged(double value)
    {
        _config.ArtistFontSize = value;
        _config.Save();
        TextStyleChanged?.Invoke();
    }

    partial void OnBackgroundFollowCoverChanged(bool value)
    {
        _config.BackgroundFollowCover = value;
        _config.Save();
        FollowCoverChanged?.Invoke();
    }

    partial void OnShowLyricChanged(bool value)
    {
        _config.ShowLyric = value;
        _config.Save();
        LyricToggleChanged?.Invoke();
    }

    partial void OnLyricModeChanged(LyricDisplayMode value)
    {
        _config.LyricMode = value;
        _config.Save();
        LyricToggleChanged?.Invoke();
    }

    partial void OnPauseFadeOutSecChanged(double value)
    {
        _config.PauseFadeOutSec = System.Math.Max(0, value);
        _config.Save();
        PauseFadeOutChanged?.Invoke();
    }

    partial void OnLyricOffsetSecChanged(double value)
    {
        _config.LyricOffsetSec = System.Math.Clamp(value, -10, 10);
        _config.Save();
        LyricToggleChanged?.Invoke(); // 复用歌词刷新链路：偏移变了立即重查当前句
    }

    partial void OnLineTransitionChanged(LineTransition value)
    {
        _config.LineTransition = value;
        _config.Save();
        // 下次换句生效，无需立即刷新
    }

    // ===== 命令 =====

    [RelayCommand]
    private void Apply()
    {
        // 字号输入框失焦时手动触发绑定，Apply 按钮兜底一次写回
        _config.FontFamily = FontFamily;
        _config.TitleFontSize = TitleFontSize;
        _config.ArtistFontSize = ArtistFontSize;
        _config.Save();
        TextStyleChanged?.Invoke();
    }
}
