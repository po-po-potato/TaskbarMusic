using System.Windows;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 音乐模块设置分区：VM 事件直连 MusicModule 的公开刷新方法。
/// MusicModule 持有本分区实例常驻复用（事件订阅只连一次）。
/// </summary>
public partial class MusicSettingsSection : UserControl
{
    private readonly MusicSettingsViewModel _vm;

    /// <summary>暴露 VM 供宿主（设置窗全局字体跟随）订阅 TextStyleChanged</summary>
    public MusicSettingsViewModel ViewModel => _vm;

    public MusicSettingsSection(MusicModule module)
    {
        InitializeComponent();
        _vm = new MusicSettingsViewModel(module.Config);
        DataContext = _vm;

        _vm.FollowCoverChanged += module.RefreshBackgroundFromCurrentCover;
        _vm.TextStyleChanged += module.ApplyTextStyle;
        _vm.LyricToggleChanged += module.ApplyLyricToggle;
        _vm.PauseFadeOutChanged += module.ApplyPauseFadeOut;
    }
}
