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
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None);
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
