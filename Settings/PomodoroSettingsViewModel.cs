using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>
/// 番茄钟设置 VM：时长两输入框 + 应用按钮（模式对齐音乐分区——失焦/点应用兜底写回）。
/// 应用 = 写 config + 落盘 + 通知模块（Idle 态即时刷新条上显示）。
/// </summary>
public partial class PomodoroSettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    /// <summary>应用按钮：时长写回 config 落盘，模块即时刷新</summary>
    public event System.Action? DurationsApplied;

    [ObservableProperty]
    private double _workMin;

    [ObservableProperty]
    private double _breakMin;

    public PomodoroSettingsViewModel(AppConfig config)
    {
        _config = config;
        _workMin = config.PomodoroWorkMin;
        _breakMin = config.PomodoroBreakMin;
    }

    [RelayCommand]
    private void Apply()
    {
        _config.PomodoroWorkMin = (int)System.Math.Clamp(WorkMin, 1, 180);
        _config.PomodoroBreakMin = (int)System.Math.Clamp(BreakMin, 1, 60);
        _config.Save();
        DurationsApplied?.Invoke();
    }
}
