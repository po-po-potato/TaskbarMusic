using System.Windows;

namespace TaskbarMusic;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 主题必须在 base.OnStartup（创建主窗口）之前应用：
        // 窗口 XAML 的 DynamicResource 在构造时解析——启动即把 App 级字典
        // 切到系统深浅色，窗口解析时直接拿到正确 brush，不依赖换字典后的
        // DynamicResource 刷新（对字典 Source 替换的刷新不可靠，
        // 2026-08-26 分区标题一直黑字实锤）。backdrop 显式 None：
        // Apply 默认 Mica 且作用于 MainWindow（= 任务栏条）会污染条
        ThemeService.ApplySystemTheme();
        base.OnStartup(e);
    }
}
