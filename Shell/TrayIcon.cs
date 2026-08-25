namespace TaskbarMusic;

/// <summary>
/// 托盘图标（壳层职责）：右键菜单 = 设置/退出；双击 = 打开设置。
/// 用 WinForms NotifyIcon（框架自带，零第三方依赖）。
/// 注意：csproj 已移除 System.Windows.Forms 全局 using（避免与 WPF 类型二义性），
/// 本文件内 WinForms 类型一律用完整限定名。
/// V1 用系统默认图标，M3 开源发布时换项目专属 .ico（PRD 遗留项）。
/// </summary>
internal sealed class TrayIcon : System.IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIcon(TaskbarShell shell)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "TaskbarMusic",
            ContextMenuStrip = shell.BuildContextMenu(), // 与右键条共用同一套 WinForms 菜单
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => shell.OpenSettings();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
