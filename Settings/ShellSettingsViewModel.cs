using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>
/// 壳设置分区 VM：布局（当前宽度显示 / 重置位置 / 重置宽度）。
/// 双向绑定 AppConfig，变更即时 Save 并通过事件通知壳执行。
/// </summary>
public partial class ShellSettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    /// <summary>重置位置按钮</summary>
    public event Action? ResetPositionRequested;

    /// <summary>重置宽度按钮</summary>
    public event Action? ResetWidthRequested;

    /// <summary>设置窗背景材质变更（壳收到后重开设置窗使新材质生效）</summary>
    public event Action? BackdropChanged;

    [ObservableProperty]
    private double _windowWidth;

    [ObservableProperty]
    private WindowBackdrop _windowBackdrop;

    public ShellSettingsViewModel(AppConfig config)
    {
        _config = config;
        _windowWidth = config.Width;
        _windowBackdrop = config.WindowBackdrop;
    }

    partial void OnWindowBackdropChanged(WindowBackdrop value)
    {
        _config.WindowBackdrop = value;
        _config.Save();
        BackdropChanged?.Invoke();
    }

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => WindowWidth = _config.Width;

    [RelayCommand]
    private void ResetPosition() => ResetPositionRequested?.Invoke();

    [RelayCommand]
    private void ResetWidth()
    {
        ResetWidthRequested?.Invoke();
        RefreshWidth();
    }
}
