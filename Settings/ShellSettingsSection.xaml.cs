using System.Collections.Generic;
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

    /// <summary>无参构造：从 ShellManager 静态取配置 + 当前 TrayOwner 的 host，
    /// 事件全部转发 ShellManager（不再绑死 shell 实例）。
    /// 订阅 TrayOwnerChanged：新 TrayOwner 就绪时重建模块行</summary>
    public ShellSettingsSection()
    {
        InitializeComponent();
        _vm = new ShellSettingsViewModel(AppConfig.Shared, ShellManager.TrayOwner?.Host);
        DataContext = _vm;

        // 事件统一转发 ShellManager（与条 Win32 层无关联）
        _vm.ResetPositionRequested += ShellManager.RequestResetPosition;
        _vm.ResetWidthRequested += ShellManager.RequestResetWidth;
        // 材质切换：关掉当前设置窗再重开（backdrop 是窗口级一次性设置，重开干净生效）
        _vm.BackdropChanged += ShellManager.ReopenSettings;
        // 字体变更：刷新条上文字（设置窗跟随由 SettingsWindow 监听同一事件）
        _vm.FontChanged += ShellManager.RequestRefreshModulesTextStyle;

        // 订阅 TrayOwner 变化：新 TrayOwner 就绪时重建模块行
        ShellManager.TrayOwnerChanged += OnTrayOwnerChanged;

        // 环境不支持材质（透明效果关闭/RDP）时提示降级（分区常驻复用，
        // 开关系统透明后重开设置窗即刷新）
        if (!ThemeService.IsBackdropAvailable())
            BackdropEnvHint.Visibility = Visibility.Visible;

        // A7 多显示器：显示器多选列表（设备名+分辨率+勾选态）
        BuildMonitorList();
    }

    /// <summary>TrayOwner 变化时重建模块行（每条各持有自己的 ModuleHost 不可跨条）</summary>
    private void OnTrayOwnerChanged()
    {
        var owner = ShellManager.TrayOwner;
        _vm?.RebuildModuleRows(owner?.Host);
    }

    // ===== A7 显示器多选 =====

    /// <summary>按当前任务栏集合构建勾选列表（重开设置窗刷新热插拔变化）</summary>
    private void BuildMonitorList()
    {
        MonitorListPanel.Children.Clear();
        var monitors = ShellManager.EnumerateTaskbarMonitors();
        if (monitors.Count == 0)
        {
            MonitorListPanel.Children.Add(new TextBlock
            {
                Text = "未检测到任务栏窗口（explorer 重启中？），重开设置窗刷新",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource(
                    "TextFillColorSecondaryBrush"),
            });
            return;
        }
        var selection = ShellManager.CurrentSelection();
        foreach (var mon in monitors)
        {
            var cb = new CheckBox
            {
                Content = $"{(mon.IsPrimary ? "主屏" : "副屏")} · {mon.Width}×{mon.Height}（{mon.Key}）",
                IsChecked = selection.Contains(mon.Key),
                Tag = mon.Key,
                Margin = new Thickness(0, 4, 0, 0),
            };
            cb.Checked += (_, _) => ApplyMonitorSelectionFromUi();
            cb.Unchecked += (_, _) => ApplyMonitorSelectionFromUi();
            MonitorListPanel.Children.Add(cb);
        }
    }

    /// <summary>收集勾选集合 → ShellManager 增删条 → 重刷列表
    /// （全不勾会被 manager 回落主屏，重刷后勾选态回归真实状态）</summary>
    private void ApplyMonitorSelectionFromUi()
    {
        var keys = new List<string>();
        foreach (var child in MonitorListPanel.Children)
        {
            if (child is CheckBox { IsChecked: true, Tag: string key }) keys.Add(key);
        }
        ShellManager.ApplyMonitorSelection(keys);
        BuildMonitorList(); // 勾选态以 manager 解析结果为准（全不勾回落主屏时可见）
    }

    /// <summary>壳分区 VM（设置窗字体跟随渲染订阅用）</summary>
    public ShellSettingsViewModel ViewModel => _vm;

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => _vm.RefreshWidth();
}
