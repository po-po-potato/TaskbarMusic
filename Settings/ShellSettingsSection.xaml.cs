using System.Windows;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 壳设置分区（布局/重置），VM 事件直连壳方法。壳持有本分区实例常驻复用。
/// </summary>
public partial class ShellSettingsSection : UserControl
{
    private readonly ShellSettingsViewModel _vm;

    public ShellSettingsSection(TaskbarShell shell)
    {
        InitializeComponent();
        _vm = new ShellSettingsViewModel(shell.Config);
        DataContext = _vm;

        _vm.ResetPositionRequested += shell.ResetPosition;
        _vm.ResetWidthRequested += shell.ResetWidth;
        // 材质切换：关掉当前设置窗再重开（backdrop 是窗口级一次性设置，重开干净生效）
        _vm.BackdropChanged += () => shell.ReopenSettings();
    }

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => _vm.RefreshWidth();
}
