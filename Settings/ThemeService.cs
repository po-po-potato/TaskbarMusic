using System.Linq;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace TaskbarMusic;

/// <summary>
/// 主题单点收敛（演进点）：所有 Wpf.Ui 主题/材质的应用只经手本类。
/// ThemesDictionary/ControlsDictionary 合并在 App.Resources（App.xaml），
/// 本类负责按系统主题切换 Theme + 喂 TitleBar/DWM 染色。
/// 任务栏条上原生 Button 全用显式 IconBtn 样式（覆盖优先级高于 ControlsDictionary
/// 隐式样式），无原生 CheckBox/RadioButton/TextBox/ComboBox——ControlsDictionary
/// 全局合并对条零影响（之前 V1 "防污染"假设过度防御，已破除）。
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// 按系统当前主题应用 Wpf.Ui 主题（深/浅），返回应用的主题值。
    /// 必须用官方 ApplicationThemeManager.Apply（Source URI 替换 + 缓存 + Changed 事件），
    /// 手动改 ThemesDictionary.Theme 属性不可靠——字典内容不更新，窗口停留在 Light
    /// （2026-08-26 纯色变白实锤）。
    /// backdrop 参数必须显式 None：Apply 默认 Mica 且作用于 MainWindow（= 任务栏条），
    /// 会给条套背景效果。
    /// 调用时机：App.OnStartup（窗口创建前，DynamicResource 首次解析即正确值）
    /// + 设置窗打开时 + 系统主题变化 hook 触发时。幂等。
    /// 高对比度按浅色处理。
    /// </summary>
    public static ApplicationTheme ApplySystemTheme()
    {
        var systemTheme = ApplicationThemeManager.GetSystemTheme();
        var theme = systemTheme == SystemTheme.Dark
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

        // 【Wpf.Ui 隐藏副作用防护】Apply(theme, None) 内部对 MainWindow（= 任务栏条）
        // 执行 WindowBackgroundManager.UpdateBackground(…, None) → RemoveBackdrop →
        // RestoreContentBackground：把条 Background=Transparent 改成主题纯色画刷
        // （Light≈#FAFAFA）、CompositionTarget.BackgroundColor 改成 SystemColors.WindowColor。
        // 条是 AllowsTransparency 分层窗口——背景一变不透明，模块渐变右侧 alpha=0
        // 透出白窗底而非任务栏（2026-08-27 他机"开设置窗后条渐变变白"实锤）。
        // 启动时 MainWindow 未创建（null）不中招，设置窗构造/主题切换 hook 再调就中招。
        // Apply 前抓条背景快照，Apply 后还原。
        var shell = Application.Current.MainWindow;
        System.Windows.Media.Brush? savedBackground = null;
        System.Windows.Interop.HwndTarget? compositionTarget = null;
        var savedCompositionColor = System.Windows.Media.Colors.White;
        if (shell is TaskbarShell)
        {
            savedBackground = shell.Background;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(shell).Handle;
            compositionTarget = hwnd != System.IntPtr.Zero
                ? System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.CompositionTarget
                : null;
            if (compositionTarget != null)
                savedCompositionColor = compositionTarget.BackgroundColor;
        }

        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None);

        if (shell is TaskbarShell)
        {
            shell.Background = savedBackground ?? System.Windows.Media.Brushes.Transparent;
            if (compositionTarget != null)
                compositionTarget.BackgroundColor = savedCompositionColor;
            var restored = shell.Background as System.Windows.Media.SolidColorBrush;
            MediaService.Trace(
                $"theme apply: shell backdrop side-effect guarded " +
                $"(bg={restored?.Color.ToString() ?? shell.Background.GetType().Name}, theme={theme})");
        }
        return theme;
    }

    /// <summary>config 材质枚举 → Wpf.Ui 窗口 backdrop 枚举</summary>
    public static Wpf.Ui.Controls.WindowBackdropType MapBackdrop(WindowBackdrop backdrop) => backdrop switch
    {
        WindowBackdrop.Mica => Wpf.Ui.Controls.WindowBackdropType.Mica,
        WindowBackdrop.Acrylic => Wpf.Ui.Controls.WindowBackdropType.Acrylic,
        _ => Wpf.Ui.Controls.WindowBackdropType.None,
    };

    /// <summary>
    /// 系统"透明效果"是否开启（HKCU\...\Themes\Personalize\EnableTransparency）。
    /// 关闭时 DWM 不绘制 DWMWA_SYSTEMBACKDROP_TYPE 的 backdrop，而 Wpf.Ui 在应用
    /// 材质前已把 WPF 窗口背景清成 Transparent（RemoveBackground）→ 窗口整片露白
    /// （2026-08-27 他机 25H2 实锤：选 Mica/Acrylic 后白色不透明且切换无效）。
    /// 键不存在按开启兜底。RDP 会话同理禁透明（TerminalServerSession）。
    /// </summary>
    public static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int v)
                return v != 0;
            return true;
        }
        catch { return true; }
    }

    /// <summary>backdrop 当前环境是否可用：系统透明开启 + 非远程桌面会话</summary>
    public static bool IsBackdropAvailable() =>
        IsSystemTransparencyEnabled()
        && Win32.GetSystemMetrics(Win32.SM_REMOTESESSION) == 0;

    /// <summary>
    /// config 材质 → 实际生效的 backdrop：环境不支持（透明关闭/RDP）时降级 None。
    /// None 路径走 FluentWindow.OnBackdropTypeChanged → RemoveBackdrop →
    /// RestoreContentBackground，恢复主题纯色背景（而不是 Transparent 露白）。
    /// </summary>
    public static Wpf.Ui.Controls.WindowBackdropType MapBackdropSafe(WindowBackdrop backdrop) =>
        IsBackdropAvailable() ? MapBackdrop(backdrop) : Wpf.Ui.Controls.WindowBackdropType.None;

    /// <summary>
    /// 同步 DWM 层深浅（DWMWA_USE_IMMERSIVE_DARK_MODE）：Mica/Acrylic 由 DWM 绘制，
    /// 深浅染色不看 WPF 主题字典只看这个 Win32 属性——不设的话深色主题下
    /// backdrop 渲染成白色而控件层是深色（双层撕裂，2026-08-26 实锤）。
    /// 必须在 SourceInitialized 之后调用（需要有效 hwnd），纯色 backdrop 也设
    /// （标题栏深浅同样由它决定）。theme 参数取 ApplySystemTheme 的返回值
    /// （ThemesDictionary.Theme 是 write-only 读不回）。
    /// </summary>
    public static void ApplyDarkModeAttribute(Window window, ApplicationTheme theme)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = theme == ApplicationTheme.Dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref value, sizeof(int));
    }
}
