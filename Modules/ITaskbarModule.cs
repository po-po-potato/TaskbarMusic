using System.Windows;

namespace TaskbarMusic;

/// <summary>
/// 任务栏条模块契约（V1 最小集）。
/// 壳（TaskbarShell）负责嵌入/拖拽/调宽/DPI/sticky 等一切 Win32 职责，
/// 模块只提供 View 与自身业务；输入事件由壳路由（hover/双击/尺寸变化）。
/// M2 扩展点：槽位分配（E6）、模块持久化（启用/顺序/折叠态）。
/// </summary>
public interface ITaskbarModule
{
    /// <summary>模块唯一标识（持久化键）</summary>
    string Id { get; }

    /// <summary>显示名（设置分区标题等）</summary>
    string DisplayName { get; }

    /// <summary>模块在条内的 UI（UserControl，铺满分配到的区域）</summary>
    FrameworkElement View { get; }

    /// <summary>设置分区 UI（由设置窗动态装入）；null = 无设置项</summary>
    FrameworkElement? SettingsSection { get; }

    /// <summary>挂载到壳：订阅壳事件、启动服务。调用时壳 HWND 已就绪、已完成首次贴附。</summary>
    void OnAttach(TaskbarShell shell);

    /// <summary>卸载：停服务、退订事件</summary>
    void OnDetach();

    /// <summary>鼠标进入/离开整条（壳转发）</summary>
    void OnHoverChanged(bool hovering);
}
