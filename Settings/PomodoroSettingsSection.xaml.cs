using System.Windows;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 番茄钟设置分区：VM 应用事件直连 PomodoroModule.ApplyDurations（Idle 态即时刷新显示）。
/// 模块持有本分区实例常驻复用（模式对齐 MusicSettingsSection）。
/// </summary>
public partial class PomodoroSettingsSection : UserControl
{
    private readonly PomodoroSettingsViewModel _vm;

    public PomodoroSettingsSection(PomodoroModule module)
    {
        InitializeComponent();
        _vm = new PomodoroSettingsViewModel(module.Config);
        DataContext = _vm;
        _vm.DurationsApplied += module.ApplyDurations;
    }
}
