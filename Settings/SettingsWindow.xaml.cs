using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarMusic;

/// <summary>
/// 设置窗（壳层基础设施 A8，Fluent 重做版）：左导航 + 右内容区。
/// 分区实例常驻（宿主持有），本窗每次打开仅装配——切换分区只换 ContentControl.Content
/// （setter 会正确解除旧分区逻辑父绑定）。窗口关闭时置空 Content 断开逻辑父引用
/// （WPF 窗口销毁不会自动断开，不清掉下次开窗 Add 同一分区会抛
/// "指定的元素已经是另一个元素的逻辑子元素"导致闪退——M1 已踩坑）。
/// 字体全局设置：跟随用户在音乐分区选择的字体（回退系统 UI 字体），改字体实时生效。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly FrameworkElement[] _sections;
    private readonly MusicSettingsSection? _musicSection;
    private readonly AppConfig _config;

    public SettingsWindow(TaskbarShell shell, ModuleHost host)
    {
        InitializeComponent();
        _config = shell.Config;

        // 分区与导航项一一对应：壳分区（常规）+ 各模块分区（V1：音乐）
        var titles = new List<string>();
        var sections = new List<FrameworkElement>();
        if (shell.SettingsSectionView != null)
        {
            titles.Add("常规");
            sections.Add(shell.SettingsSectionView);
        }
        foreach (var module in host.Modules)
        {
            if (module.SettingsSection != null)
            {
                titles.Add(module.DisplayName);
                sections.Add(module.SettingsSection);
            }
        }
        _sections = sections.ToArray();
        _musicSection = _sections.Length > 1 ? _sections[1] as MusicSettingsSection : null;

        for (int i = 0; i < titles.Count; i++)
            NavList.Items.Add(new ListBoxItem { Content = titles[i], Padding = new Thickness(10, 8, 10, 8) });

        NavList.SelectedIndex = 0;

        // 字体全局设置（）：按用户选择的字体渲染整个设置窗，
        // 用户字体优先、回退系统 UI 字体；改字体实时跟随（VM TextStyleChanged）。
        ApplyGlobalFont(shell.Config.FontFamily);
        if (_musicSection != null)
        {
            _musicSection.ViewModel.TextStyleChanged += OnGlobalFontChanged;
            Closed += (_, _) => _musicSection.ViewModel.TextStyleChanged -= OnGlobalFontChanged;
        }

        Closed += (_, _) => SectionHost.Content = null;
    }

    private void OnGlobalFontChanged() => ApplyGlobalFont(_musicSection!.ViewModel.FontFamily);

    /// <summary>用户字体（单一字体构造——与条上歌词 ApplyTextStyle 同款用法，
    /// 该用法下 PingFang SC 渲染正常；复合 fallback 串 "A, B" 在代码构造下解析
    /// 失败会渲染出系统默认怪字体，不用）</summary>
    private void ApplyGlobalFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return;
        try { FontFamily = new FontFamily(family); }
        catch { /* 非法字体名保持默认 */ }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = NavList.SelectedIndex;
        if (i >= 0 && i < _sections.Length)
            SectionHost.Content = _sections[i];
    }

    /// <summary>
    /// 窗口 Mica 背景（Win11 22H2+，SYSTEMBACKDROP_TYPE=2）。
    /// Mica 生效需三步（缺一则静默退化纯色——用户反复"看不到 Mica 但 Acrylic
    /// 能看到"的根因：只做了后两步，漏了①）：
    /// ① DwmExtendFrameIntoClientArea 整窗扩展（margins 全 -1）——Mica 硬性前提，
    ///    Acrylic 不需要故之前能看到 Acrylic 看不到 Mica
    /// ② 两层透明：CompositionTarget.BackgroundColor + Window.Background = Transparent
    /// ③ DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE, 2)
    /// sticky 卡顿真凶已修（是 timer 抢 UI 线程非透明管线），故 Mica 可安全启用。
    /// </summary>
    /// <summary>
    /// 设置窗背景材质（提议做成设置项，三选一切换对比）：
    /// - None(1)：显式关闭 backdrop——不能用"不调用"实现！ThemeMode="System"
    ///   （Fluent 主题）在 Win11 上会自动给窗口应用系统 Mica（wbset16 实测：代码
    ///   禁用 Mica 后激活态仍有染色 = ThemeMode 自带的），必须显式设
    ///   DWMSBT_NONE 才是真纯色（20:54"纯色不生效"实锤）。
    /// - Mica(2)：壁纸着色 + DwmExtendFrameIntoClientArea 扩展帧
    /// - Acrylic(3)：实时模糊
    /// 材质需在 SourceInitialized 后由 TaskbarShell 调用；配置在窗口构造时读取
    /// （切换材质 = 改配置后重开设置窗生效）。
    /// </summary>
    public void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == System.IntPtr.Zero) return;

        var backdrop = _config.WindowBackdrop;
        int value = backdrop switch
        {
            WindowBackdrop.Mica => 2,
            WindowBackdrop.Acrylic => 3,
            _ => 1, // DWMSBT_NONE：显式关闭（压制 ThemeMode 自带的自动 Mica）
        };
        int hr = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEM_BACKDROP_TYPE,
                ref value, sizeof(int));
        if (hr != 0 || backdrop == WindowBackdrop.None) return;

        if (backdrop == WindowBackdrop.Mica)
        {
            // Mica 硬性前提：扩展帧进客户区（Acrylic 不需要）
            var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }

        // 两层透明：① Win32 清屏层 ② WPF 层（缺①API 成功也看不到）
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        Background = Brushes.Transparent;
    }
}
