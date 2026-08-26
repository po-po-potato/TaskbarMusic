using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>
/// 壳设置分区 VM：字体（全局项，2026-08-26 从音乐分区迁入——设置窗整体跟随渲染，
/// 用户心理模型就是全局）+ 材质 + 重置。
/// 双向绑定 AppConfig，变更即时 Save 并通过事件通知壳执行。
/// </summary>
public partial class ShellSettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    // 系统字体枚举是同步 IO，进程级缓存一份避免每次开窗都卡（自音乐 VM 迁入）
    private static readonly List<string> _systemFonts = LoadSystemFonts();

    private static List<string> LoadSystemFonts()
    {
        var list = new List<string>();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var src = f.Source;
            if (!string.IsNullOrWhiteSpace(src)) list.Add(src);
        }
        list.Sort(System.StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>重置位置按钮</summary>
    public event Action? ResetPositionRequested;

    /// <summary>重置宽度按钮</summary>
    public event Action? ResetWidthRequested;

    /// <summary>设置窗背景材质变更（壳收到后重开设置窗使新材质生效）</summary>
    public event Action? BackdropChanged;

    /// <summary>字体变更（设置窗跟随渲染 + 条上文字刷新，订阅方各自处理）</summary>
    public event Action? FontChanged;

    [ObservableProperty]
    private double _windowWidth;

    [ObservableProperty]
    private WindowBackdrop _windowBackdrop;

    [ObservableProperty]
    private string _fontFamily;

    public IReadOnlyList<string> SystemFonts => _systemFonts;

    public ShellSettingsViewModel(AppConfig config)
    {
        _config = config;
        _windowWidth = config.Width;
        _windowBackdrop = config.WindowBackdrop;
        // 纯下拉后 SelectedItem 只能匹配列表项：值不在系统字体列表（手输残留/字体已卸载）
        // 时回退 Segoe UI，并写回 config 防止条上渲染 fallback
        _fontFamily = ResolveFont(config.FontFamily);
        if (!string.Equals(_fontFamily, config.FontFamily, System.StringComparison.Ordinal))
        {
            config.FontFamily = _fontFamily;
            config.Save();
        }
    }

    /// <summary>字体值归一：忽略大小写匹配列表项；无匹配回退 Segoe UI → 列表首项</summary>
    private string ResolveFont(string value)
    {
        var hit = _systemFonts.Find(f => string.Equals(f, value, System.StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        return _systemFonts.Find(f => f.Contains("Segoe UI", System.StringComparison.OrdinalIgnoreCase))
               ?? _systemFonts.FirstOrDefault() ?? value;
    }

    partial void OnWindowBackdropChanged(WindowBackdrop value)
    {
        _config.WindowBackdrop = value;
        _config.Save();
        BackdropChanged?.Invoke();
    }

    partial void OnFontFamilyChanged(string value)
    {
        _config.FontFamily = value;
        _config.Save();
        FontChanged?.Invoke();
    }

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => WindowWidth = _config.Width;

    [RelayCommand]
    private void ResetPosition() => ResetPositionRequested?.Invoke();

    [RelayCommand]
    private void ResetWidth()
    {
        ResetWidthRequested?.Invoke();
        RefreshWidth();
    }
}
