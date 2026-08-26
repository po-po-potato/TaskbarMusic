using System.Windows;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 壳设置分区（字体/材质/重置），VM 事件直连壳方法。壳持有本分区实例常驻复用。
/// 2026-08-26 重归类：布局组删除（条宽度是只读显示无交互价值），
/// 重置降级为普通行；字体从音乐分区迁入（全局项）。
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
        // 字体变更：刷新条上文字（设置窗跟随由 SettingsWindow 监听同一事件）
        _vm.FontChanged += shell.RefreshModulesTextStyle;
    }

    /// <summary>壳分区 VM（设置窗字体跟随渲染订阅用）</summary>
    public ShellSettingsViewModel ViewModel => _vm;

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => _vm.RefreshWidth();
}
