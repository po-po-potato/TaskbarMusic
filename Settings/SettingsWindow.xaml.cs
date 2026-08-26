using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace TaskbarMusic;

/// <summary>
/// 设置窗（WPF Gallery 组件版）：FluentWindow + TitleBar + NavigationView。
/// 分区实例常驻（宿主持有），本窗每次打开仅装配——切换分区只换 SectionHost.Content
/// （setter 会正确解除旧分区逻辑父绑定）。窗口关闭时置空 Content 断开逻辑父引用
/// （WPF 窗口销毁不会自动断开，不清掉下次开窗 Add 同一分区会抛
/// "指定的元素已经是另一个元素的逻辑子元素"导致闪退——M1 已踩坑）。
/// 背景材质：FluentWindow.WindowBackdropType 内建接管（替代旧手写 DWM 三步法，
/// 手写 DwmSetWindowAttribute/DwmExtendFrameIntoClientArea 全部删除）；
/// 切换材质仍走壳层 ReopenSettings（backdrop 是窗口级一次性设置，重开干净生效）。
/// 字体全局设置：跟随用户在音乐分区选择的字体（回退系统 UI 字体），改字体实时生效。
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly FrameworkElement[] _sections;
    private readonly AppConfig _config;
    private readonly ShellSettingsSection? _shellSection;

    /// <summary>右侧内容宿主（代码构造，经 INavigationView.ReplaceContent 装载；
    /// 24px 左右留白对齐 WPF Gallery 设置页呼吸感）</summary>
    private readonly ContentControl SectionHost = new() { Margin = new Thickness(24, 8, 24, 24) };

    /// <summary>窗级主题（构造时由 ThemeService.ApplySystemTheme 返回，
    /// OnSourceInitialized 喂给 DWM 层用）</summary>
    private Wpf.Ui.Appearance.ApplicationTheme _theme;

    public SettingsWindow(TaskbarShell shell, ModuleHost host)
    {
        InitializeComponent();
        _config = shell.Config;

        // 主题单点（ThemeService）：窗级深浅色跟随系统 + 喂 TitleBar 前景色，
        // 材质映射到内建 backdrop；_theme 留给 OnSourceInitialized 喂 DWM 层
        // （App.OnStartup 已提前应用过一次，此处幂等重应用确保新会话正确）
        _theme = ThemeService.ApplySystemTheme();
        RootTitleBar.ApplicationTheme = _theme;
        WindowBackdropType = ThemeService.MapBackdrop(_config.WindowBackdrop);

        // 宽高记忆：恢复用户上次拖动后的尺寸（XAML 默认 800x560 只是无配置时的兜底）
        if (_config.SettingsWindowWidth >= MinWidth)
            Width = _config.SettingsWindowWidth;
        if (_config.SettingsWindowHeight >= MinHeight)
            Height = _config.SettingsWindowHeight;

        // 关闭时保存实际宽高（含材质切换的重开路径——重开前旧窗关闭也会存）
        Closing += (_, _) =>
        {
            _config.SettingsWindowWidth = ActualWidth;
            _config.SettingsWindowHeight = ActualHeight;
            _config.Save();
        };

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

        // 关于页（开源准备）：版本号读程序集元数据，固定挂在导航末尾
        titles.Add("关于");
        sections.Add(new AboutSection());
        _sections = sections.ToArray();
        _shellSection = _sections.Length > 0 ? _sections[0] as ShellSettingsSection : null;

        for (int i = 0; i < titles.Count; i++)
        {
            // 闭包捕获索引避免经典循环变量陷阱。
            // 库的 Navigate(Type) 会经 activator 新建页实例，与"分区实例常驻、
            // 宿主持有"架构冲突，故不走 TargetPageType 导航——改监听 item 按下
            // 手动切 Content，选中视觉用 IsActive 手动管理
            int index = i;
            var item = new NavigationViewItem
            {
                Content = titles[i],
                Icon = new SymbolIcon { Symbol = IconForSection(titles[i]) },
            };
            item.PreviewMouseLeftButtonDown += (_, _) => SelectItem(index);
            NavView.MenuItems.Add(item);
        }

        // 内容区宿主装载：NavigationView 非 ContentControl，接口 ReplaceContent
        // 装入滚动宿主；分区切换只换 SectionHost.Content（装配语义与旧版一致）。
        // 【必须在 Loaded 里调】构造期 NavigationView 尚未 ApplyTemplate，
        // UpdateContent 访问模板 part（内容呈现器）为 null → NRE 闪退
        // （2026-08-26 打开设置窗闪退实锤）；初始选中一并在此做
        Loaded += (_, _) =>
        {
            ((INavigationView)NavView).ReplaceContent(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = SectionHost,
            }, null);
            if (_sections.Length > 0)
                SelectItem(0);
        };

        // 字体全局跟随：改监听壳分区 VM（字体 2026-08-26 迁常规分区）——
        // 字体变化实时渲染整个设置窗；构造时先按当前配置应用初始字体
        ApplyGlobalFont(shell.Config.FontFamily);
        if (_shellSection != null)
        {
            _shellSection.ViewModel.FontChanged += OnGlobalFontChanged;
            Closed += (_, _) => _shellSection.ViewModel.FontChanged -= OnGlobalFontChanged;
        }

        Closed += (_, _) => SectionHost.Content = null;
    }

    /// <summary>选中导航项：切分区内容 + 手动维护 item 选中视觉（IsActive）</summary>
    private void SelectItem(int index)
    {
        for (int i = 0; i < NavView.MenuItems.Count; i++)
        {
            if (NavView.MenuItems[i] is NavigationViewItem it)
                it.IsActive = i == index;
        }
        if (index >= 0 && index < _sections.Length)
            SectionHost.Content = _sections[index];
    }

    /// <summary>导航图标（Fluent System Icons；编译期枚举校验，写错 XAML 编译报错）</summary>
    private static SymbolRegular IconForSection(string title) => title switch
    {
        "常规" => SymbolRegular.Settings24,
        "音乐" => SymbolRegular.MusicNote224,
        "关于" => SymbolRegular.Info24,
        _ => SymbolRegular.Circle24,
    };

    /// <summary>SourceInitialized：hwnd 已创建——同步 DWM 层深浅 + 挂系统主题变化钩子。
    /// base 调用让 FluentWindow 先应用 backdrop，再喂 dark mode 属性
    /// （动态生效，backdrop 后设一次确保染色正确）</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyDarkModeAttribute(this, _theme);

        // 系统深浅色实时跟随：hook WM_SETTINGCHANGE(ImmersiveColorSet)
        // （与 Wpf.Ui SystemThemeWatcher 同款消息，但自主控制——Watcher 会强制
        // UpdateBackground 覆盖用户选的材质，不用）
        if (System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(ThemeChangeWndProc);
        }
    }

    /// <summary>系统主题变化钩子：重新应用全套主题（字典 + TitleBar + 背景序列）。
    /// 背景必须走 WindowBackgroundManager.UpdateBackground 完整序列
    /// （移除旧 backdrop → 清窗口背景 → 按当前材质重应用 → dark mode →
    /// 清标题栏背景）——缺它会出现：纯色模式顶栏/左栏残留旧主题色、
    /// 亚克力模式 backdrop 丢失变纯色（2026-08-26 实锤，重新切材质才能恢复）。
    /// 传当前 WindowBackdropType 属性值——材质是用户选择，主题切换不改变它</summary>
    private System.IntPtr ThemeChangeWndProc(System.IntPtr hwnd, int msg,
        System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != System.IntPtr.Zero
            && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            _theme = ThemeService.ApplySystemTheme();
            RootTitleBar.ApplicationTheme = _theme;
            Wpf.Ui.Appearance.WindowBackgroundManager.UpdateBackground(
                this, _theme, WindowBackdropType);
        }
        return System.IntPtr.Zero;
    }

    private void OnGlobalFontChanged() =>
        ApplyGlobalFont(_shellSection?.ViewModel.FontFamily ?? "");

    /// <summary>用户字体（单一字体构造——与条上歌词 ApplyTextStyle 同款用法，
    /// 该用法下 PingFang SC 渲染正常；复合 fallback 串 "A, B" 在代码构造下解析
    /// 失败会渲染出系统默认怪字体，不用）</summary>
    private void ApplyGlobalFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return;
        try { FontFamily = new FontFamily(family); }
        catch { /* 非法字体名保持默认 */ }
    }
}
