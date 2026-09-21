using System;
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
    private FrameworkElement[] _sections = Array.Empty<FrameworkElement>();
    private readonly AppConfig _config;
    private ShellSettingsSection? _shellSection;

    /// <summary>右侧内容宿主（代码构造，经 INavigationView.ReplaceContent 装载；
    /// 24px 左右留白对齐 WPF Gallery 设置页呼吸感）</summary>
    private readonly ContentControl SectionHost = new() { Margin = new Thickness(24, 8, 24, 24) };

    /// <summary>窗级主题（构造时由 ThemeService.ApplySystemTheme 返回，
    /// OnSourceInitialized 喂给 DWM 层用）</summary>
    private Wpf.Ui.Appearance.ApplicationTheme _theme;

    /// <summary>配置里的材质（用户选择）；实际生效值看 WindowBackdropType，
    /// 环境不支持时被 Safe 映射降级为 None</summary>
    private WindowBackdrop _configBackdrop;

    /// <summary>上次采样到的系统透明效果开关（WM_SETTINGCHANGE 跟随用）</summary>
    private bool _transparencyOn = true;

    /// <summary>构造：进程级单例（归 ShellManager 管），Win32 层与任何条无关联。
    /// 分区内容跟 ShellManager.TrayOwner 走：构造时建一次，TrayOwner 变化时
    /// 由订阅的事件 RebuildSections 重建（设置窗不关，位置/尺寸保持）</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        _config = AppConfig.Shared;

        // 主题单点（ThemeService）：窗级深浅色按 config 颜色模式（跟随系统/浅色/深色）
        // + 喂 TitleBar 前景色，材质映射到内建 backdrop；_theme 留给 OnSourceInitialized
        // 喂 DWM 层（App.OnStartup 已提前应用过一次，此处幂等重应用确保新会话正确）。
        // 材质用 Safe 版：系统透明关闭/RDP 下 DWM 不画 backdrop 而 Wpf.Ui 已清
        // WPF 背景 → 整窗露白（2026-08-27 他机实锤），降级 None 走纯色恢复路径
        _theme = ThemeService.ApplyTheme(_config.AppTheme);
        RootTitleBar.ApplicationTheme = _theme;
        _configBackdrop = _config.WindowBackdrop;
        WindowBackdropType = ThemeService.MapBackdropSafe(_configBackdrop);

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

        // 订阅 TrayOwner 变化：宿主条销毁/切换时刷新分区内容（窗不关）
        ShellManager.TrayOwnerChanged += RebuildSections;
        Closed += (_, _) =>
        {
            SectionHost.Content = null;
            UnsubscribeShellSectionEvents();
            ShellManager.TrayOwnerChanged -= RebuildSections;
        };

        // 首次构造分区（取当前 TrayOwner 的内容）
        RebuildSections();

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

        // 字体全局初始（构造时按当前配置应用；后续变化通过 ShellSection 事件订阅）
        ApplyGlobalFont(_config.FontFamily);
    }

    /// <summary>重建分区（构造 + TrayOwner 变化时调用）：从 ShellManager 取当前
    /// TrayOwner 的 ShellSettingsSection + 各模块 SettingsSection；NavView 重建。
    /// 旧 ShellSettingsSection 的事件先解绑防 VM 持有死引用</summary>
    private void RebuildSections()
    {
        // 记住当前选中的分区标题：真有结构变化（显示器增删等）重建后恢复
        // 用户所在分区，而不是强制跳回首分区（2026-09-21 修复"跳回常规"体验）
        string? activeTitle = null;
        for (int i = 0; i < NavView.MenuItems.Count; i++)
            if (NavView.MenuItems[i] is NavigationViewItem { IsActive: true } it)
                activeTitle = it.Content as string;

        UnsubscribeShellSectionEvents();

        var titleBuilder = new List<string>();
        var sectionBuilder = new List<FrameworkElement>();

        var owner = ShellManager.TrayOwner;
        if (owner != null)
        {
            titleBuilder.Add("常规");
            sectionBuilder.Add(owner.ShellSectionForSettings);
            foreach (var module in owner.Host.ModulesInOrder)
            {
                if (module.SettingsSection != null)
                {
                    titleBuilder.Add(module.DisplayName);
                    sectionBuilder.Add(module.SettingsSection);
                }
            }
        }

        // 关于页（开源准备）：版本号读程序集元数据，固定挂在导航末尾
        titleBuilder.Add("关于");
        sectionBuilder.Add(new AboutSection());

        _sections = sectionBuilder.ToArray();
        _shellSection = _sections.Length > 0 ? _sections[0] as ShellSettingsSection : null;

        // 重建 NavView item 集合
        NavView.MenuItems.Clear();
        for (int i = 0; i < titleBuilder.Count; i++)
        {
            int index = i;
            var item = new NavigationViewItem
            {
                Content = titleBuilder[i],
                Icon = new SymbolIcon { Symbol = IconForSection(titleBuilder[i]) },
            };
            item.PreviewMouseLeftButtonDown += (_, _) => SelectItem(index);
            NavView.MenuItems.Add(item);
        }

        // 内容区装载（Loaded 之后才有效；Loaded 之前 RebuildSections 是为捕获初始内容）
        if (IsLoaded && _sections.Length > 0)
        {
            int restore = titleBuilder.IndexOf(activeTitle ?? "");
            SelectItem(restore >= 0 ? restore : 0);
        }

        // 重新订阅新 ShellSettingsSection 的事件
        if (_shellSection != null)
        {
            _shellSection.ViewModel.FontChanged += OnGlobalFontChanged;
            _shellSection.ViewModel.ThemeChanged += OnAppThemeChanged;
        }
    }

    /// <summary>解绑当前 ShellSection VM 的事件（重建前 + 关闭时调用）</summary>
    private void UnsubscribeShellSectionEvents()
    {
        if (_shellSection != null)
        {
            _shellSection.ViewModel.FontChanged -= OnGlobalFontChanged;
            _shellSection.ViewModel.ThemeChanged -= OnAppThemeChanged;
        }
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
        "番茄钟" => SymbolRegular.Clock24,
        "天气" => SymbolRegular.WeatherSunnyHigh24,
        "财经" => SymbolRegular.ChartMultiple24,
        "网速" => SymbolRegular.Wifi124,
        "倒数日" => SymbolRegular.CalendarLtr24,
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

        // 远程诊断：环境 + 材质决策落 trace.log（他机问题排查用，本机无害）
        _transparencyOn = ThemeService.IsSystemTransparencyEnabled();
        MediaService.Trace(
            $"settings backdrop: config={_configBackdrop} effective={WindowBackdropType} " +
            $"transparency={_transparencyOn} rdp={Win32.GetSystemMetrics(Win32.SM_REMOTESESSION) != 0} " +
            $"build={Environment.OSVersion.Version} theme={_theme}");

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
    /// 传当前 WindowBackdropType 属性值——材质是用户选择，主题切换不改变它。
    /// 仅跟随系统模式响应（固定深/浅色时系统切换无意义，跳过防覆盖用户选择）。
    /// 顺带跟随"透明效果"开关：切换时按新环境重映射材质（关闭→降级纯色防露白，
    /// 开启→恢复用户所选材质）</summary>
    private System.IntPtr ThemeChangeWndProc(System.IntPtr hwnd, int msg,
        System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != System.IntPtr.Zero)
        {
            var what = System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam);
            if (what == "ImmersiveColorSet" && _config.AppTheme == AppThemeMode.System)
            {
                ReapplyTheme();
            }
            else if (what == "Personalize\\Transparency"
                && ThemeService.IsSystemTransparencyEnabled() != _transparencyOn)
            {
                _transparencyOn = ThemeService.IsSystemTransparencyEnabled();
                WindowBackdropType = ThemeService.MapBackdropSafe(_configBackdrop);
            }
        }
        return System.IntPtr.Zero;
    }

    private void OnGlobalFontChanged() =>
        ApplyGlobalFont(_shellSection?.ViewModel.FontFamily ?? "");

    /// <summary>颜色模式切换（个性化设置项）：重应用全套主题——字典（ApplyTheme
    /// 内含条背景防护）+ TitleBar + 背景序列 + DWM 层深浅。走与系统主题变化
    /// hook 相同的完整序列，材质（用户选择）保持不变。设置窗开着即可见实时切换。</summary>
    private void OnAppThemeChanged() => ReapplyTheme();

    /// <summary>重应用主题完整序列（颜色模式切换 / 跟随系统模式的系统主题变化共用）</summary>
    private void ReapplyTheme()
    {
        _theme = ThemeService.ApplyTheme(_config.AppTheme);
        RootTitleBar.ApplicationTheme = _theme;
        Wpf.Ui.Appearance.WindowBackgroundManager.UpdateBackground(
            this, _theme, WindowBackdropType);
        ThemeService.ApplyDarkModeAttribute(this, _theme);
    }

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
