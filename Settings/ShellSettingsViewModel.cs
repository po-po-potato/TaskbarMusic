using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskbarMusic;

/// <summary>
/// 模块管理行（E6 单屏轮播，设置窗"模块"组）：每模块一行 = 启用勾选 + 轮播排序。
/// 勾选/排序改即生效（ModuleHost 即时挂载/卸载/重排并落盘）；
/// 排序变化后整组行按新顺序重建。
/// </summary>
public partial class ModuleStateRow : ObservableObject
{
    private readonly ModuleHost _host;

    public string Id { get; }
    public string Name { get; }

    /// <summary>启用 = 进入轮播序列；取消勾选 = 停用（OnDetach，服务停止）</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>排序变化 → VM 重建行集合（保持行序 = 轮播序）</summary>
    public event Action? OrderChanged;

    public ModuleStateRow(ModuleHost host, ITaskbarModule module, bool enabled)
    {
        _host = host;
        Id = module.Id;
        Name = module.DisplayName;
        _isEnabled = enabled;
    }

    partial void OnIsEnabledChanged(bool value) =>
        _host.ApplyModuleState(Id, value ? ModuleDisplayState.Expanded : ModuleDisplayState.Disabled);

    [RelayCommand]
    private void MoveUp()
    {
        _host.MoveModuleOrder(Id, -1);
        OrderChanged?.Invoke();
    }

    [RelayCommand]
    private void MoveDown()
    {
        _host.MoveModuleOrder(Id, +1);
        OrderChanged?.Invoke();
    }
}

/// <summary>
/// 壳设置分区 VM：字体（全局项，2026-08-26 从音乐分区迁入——设置窗整体跟随渲染，
/// 用户心理模型就是全局）+ 模块（E6 槽位三档）+ 材质 + 重置。
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

    /// <summary>颜色模式变更（设置窗订阅后实时重应用全套主题，不重开窗）</summary>
    public event Action? ThemeChanged;

    /// <summary>字体变更（设置窗跟随渲染 + 条上文字刷新，订阅方各自处理）</summary>
    public event Action? FontChanged;

    [ObservableProperty]
    private double _windowWidth;

    [ObservableProperty]
    private WindowBackdrop _windowBackdrop;

    [ObservableProperty]
    private AppThemeMode _appTheme;

    [ObservableProperty]
    private string _fontFamily;

    public IReadOnlyList<string> SystemFonts => _systemFonts;

    /// <summary>模块管理行（E6 单屏轮播）：按轮播顺序排列，排序变化后重建</summary>
    public System.Collections.ObjectModel.ObservableCollection<ModuleStateRow> ModuleRows { get; } = new();

    public ShellSettingsViewModel(AppConfig config, ModuleHost? host = null)
    {
        _config = config;
        _windowWidth = config.Width;
        _windowBackdrop = config.WindowBackdrop;
        _appTheme = config.AppTheme;
        // 纯下拉后 SelectedItem 只能匹配列表项：值不在系统字体列表（手输残留/字体已卸载）
        // 时回退 Segoe UI，并写回 config 防止条上渲染 fallback
        _fontFamily = ResolveFont(config.FontFamily);
        if (!string.Equals(_fontFamily, config.FontFamily, System.StringComparison.Ordinal))
        {
            config.FontFamily = _fontFamily;
            config.Save();
        }
        if (host != null) BuildModuleRows(host);
    }

    /// <summary>按轮播顺序构建模块行；行内排序操作后触发重建（行序 = 轮播序）</summary>
    private void BuildModuleRows(ModuleHost host)
    {
        ModuleRows.Clear();
        foreach (var module in host.ModulesInOrder)
        {
            var row = new ModuleStateRow(host, module, host.StateOf(module.Id) != ModuleDisplayState.Disabled);
            row.OrderChanged += () => BuildModuleRows(host); // host 闭包捕获，同一实例
            ModuleRows.Add(row);
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

    partial void OnAppThemeChanged(AppThemeMode value)
    {
        _config.AppTheme = value;
        _config.Save();
        ThemeChanged?.Invoke();
    }

    partial void OnFontFamilyChanged(string value)
    {
        _config.FontFamily = value;
        _config.Save();
        FontChanged?.Invoke();
    }

    /// <summary>条宽度被拖动时由壳调用，刷新显示</summary>
    public void RefreshWidth() => WindowWidth = _config.Width;

    /// <summary>TrayOwner 变化时由 ShellSettingsSection 调用，重建模块管理行</summary>
    public void RebuildModuleRows(ModuleHost? host)
    {
        ModuleRows.Clear();
        if (host != null) BuildModuleRows(host);
    }

    [RelayCommand]
    private void ResetPosition() => ResetPositionRequested?.Invoke();

    [RelayCommand]
    private void ResetWidth()
    {
        ResetWidthRequested?.Invoke();
        RefreshWidth();
    }
}
