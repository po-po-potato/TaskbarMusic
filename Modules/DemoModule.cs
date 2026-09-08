using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// 开发验证用空壳模块（E6 槽位模型出口标准："加第二个空壳模块零壳层改动"）。
/// 不进默认注册——仅在环境变量 TBM_DEMO_MODULE=1 时由组合根挂载，发布产物不可达。
/// 内容：秒级时钟 + 完成计数（证明独立模块生命周期与挂载/卸载安全）。
/// </summary>
public partial class DemoModule : UserControl, ITaskbarModule
{
    private readonly DispatcherTimer _timer;
    private int _ticks;

    public string Id => "demo";
    public string DisplayName => "演示";
    public FrameworkElement View => this;
    public FrameworkElement? SettingsSection => null;

    public DemoModule()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x30));
        var text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        Content = text;
        _text = text;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _ticks++;
            _text.Text = $"{DateTime.Now:HH:mm:ss}  #{_ticks}";
        };
    }

    private readonly TextBlock _text;

    public void OnAttach(TaskbarShell shell)
    {
        _ticks = 0;
        _text.Text = $"{DateTime.Now:HH:mm:ss}  #0";
        _timer.Start();
    }

    public void OnDetach() => _timer.Stop();

    public void OnHoverChanged(bool hovering)
    {
        // 空壳模块无 hover 交互
    }
}
