using System;
using System.Diagnostics;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 关于分区：版本号（程序集元数据，csproj &lt;Version&gt; 单一真源）+ 仓库链接 + 许可致谢。
/// 链接打开用 Process.Start(UseShellExecute) 走默认浏览器。
/// </summary>
public partial class AboutSection : UserControl
{
    private const string RepoUrl = "https://github.com/po-po-potato/TaskbarMusic";

    public AboutSection()
    {
        InitializeComponent();

        // 版本号：csproj 的 <Version> 会写入 AssemblyVersion（1.0.0）与
        // InformationalVersion（含 prerelease 后缀时），显示用短形式
        var v = typeof(App).Assembly.GetName().Version;
        if (v != null)
            VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnRepoClick(object sender, System.Windows.RoutedEventArgs e) =>
        OpenUrl(RepoUrl);

    private void OnReleasesClick(object sender, System.Windows.RoutedEventArgs e) =>
        OpenUrl(RepoUrl + "/releases");

    private void OnIssuesClick(object sender, System.Windows.RoutedEventArgs e) =>
        OpenUrl(RepoUrl + "/issues");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器启动失败（无默认浏览器/策略限制）静默忽略，不崩设置窗
        }
    }
}
