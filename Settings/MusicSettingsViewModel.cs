using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>展示模式下拉框选项项（ComboBox DisplayMemberPath=Label / SelectedValuePath=Value）</summary>
public sealed record LyricModeOption(string Label, LyricDisplayMode Value);

/// <summary>
/// 音乐模块设置分区 VM：歌词（开关/模式下拉/过渡/翻译·随模式显隐）+ 外观（取色/字号）+ 播放（偏移/暂停补偿）。
/// 字体已迁壳分区（全局项）；偏移 2026-08-26 改毫秒（LyricOffsetMs）并挪入播放分区。
/// 双向绑定 AppConfig，变更即时 Save 并通过事件通知 MusicModule 刷新。
/// </summary>
public partial class MusicSettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    /// <summary>字号变化时刷新（注入 MusicModule）</summary>
    public event Action? TextStyleChanged;

    /// <summary>显示歌词开关 / 模式 / 翻译 / 偏移切换时刷新</summary>
    public event Action? LyricToggleChanged;

    /// <summary>跟随封面取色变化时刷新</summary>
    public event Action? FollowCoverChanged;

    /// <summary>暂停淡出补偿变化时刷新（注入 MediaService）</summary>
    public event Action? PauseFadeOutChanged;

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
    private bool _showTranslation;

    [ObservableProperty]
    private double _pauseFadeOutSec;

    [ObservableProperty]
    private int _lyricOffsetMs;

    [ObservableProperty]
    private LineTransition _lineTransition;

    /// <summary>换句过渡可见性：双行模式（Follow）换句自带垂直滚动，无行内过渡概念
    /// （叠加会在滚动完成后重放一次 fade 造成闪烁，见 MusicModule.SetLineContent）→ 整卡隐藏</summary>
    public bool IsLineTransitionVisible => LyricMode != LyricDisplayMode.Follow;

    /// <summary>翻译可见性：单行模式专属——其余模式两行已满，塞翻译必挤掉原生内容 → 整卡隐藏</summary>
    public bool IsTranslationVisible => LyricMode == LyricDisplayMode.LyricOnly;

    /// <summary>展示模式下拉框选项（Label 显示 / Value 绑回枚举）</summary>
    public IReadOnlyList<LyricModeOption> ModeOptions { get; } = new[]
    {
        new LyricModeOption("歌名 + 歌词", LyricDisplayMode.ReplaceArtist),
        new LyricModeOption("单行", LyricDisplayMode.LyricOnly),
        new LyricModeOption("双行（当前 + 下一句）", LyricDisplayMode.Follow),
    };

    public MusicSettingsViewModel(AppConfig config)
    {
        _config = config;
        _titleFontSize = config.TitleFontSize;
        _artistFontSize = config.ArtistFontSize;
        _backgroundFollowCover = config.BackgroundFollowCover;
        _showLyric = config.ShowLyric;
        _lyricMode = config.LyricMode;
        _showTranslation = config.ShowTranslation;
        _pauseFadeOutSec = config.PauseFadeOutSec;
        _lyricOffsetMs = config.LyricOffsetMs;
        _lineTransition = config.LineTransition;
    }

    // ===== 属性变化回调：写回 config + Save + 通知模块 =====

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
        OnPropertyChanged(nameof(IsLineTransitionVisible)); // 双行模式下换句过渡整卡隐藏
        OnPropertyChanged(nameof(IsTranslationVisible));    // 翻译仅单行模式可见
        LyricToggleChanged?.Invoke();
    }

    partial void OnShowTranslationChanged(bool value)
    {
        _config.ShowTranslation = value;
        _config.Save();
        LyricToggleChanged?.Invoke(); // 复用歌词刷新链路：行内容立即重排
    }

    partial void OnPauseFadeOutSecChanged(double value)
    {
        _config.PauseFadeOutSec = System.Math.Max(0, value);
        _config.Save();
        PauseFadeOutChanged?.Invoke();
    }

    partial void OnLyricOffsetMsChanged(int value)
    {
        _config.LyricOffsetMs = System.Math.Clamp(value, -2000, 2000);
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
        // 字号滑块失焦时手动触发绑定，Apply 按钮兜底一次写回
        _config.TitleFontSize = TitleFontSize;
        _config.ArtistFontSize = ArtistFontSize;
        _config.Save();
        TextStyleChanged?.Invoke();
    }
}
