using System.Windows;

namespace TaskbarMusic;

/// <summary>
/// 任务栏条模块契约（V1 最小集 + M2 E6 单屏轮播）。
/// 壳（TaskbarShell）负责嵌入/拖拽/调宽/DPI/sticky 等一切 Win32 职责，
/// 模块只提供 View 与自身业务；输入事件由壳路由（hover/双击/滚轮切换）。
/// E6 单屏轮播模型：条是一块固定宽度的小显示屏，同时只显示一个模块，
/// 滚轮切换显示对象；启用模块全部常驻运行（服务不断），只换视图。
/// 模块自身不实现切换逻辑。
///
/// ⚠️ 透明底模块（Background=Transparent 叠任务栏）注意事项（2026-09-21 实锤）：
/// 条是分层窗口，alpha=0 像素会被 Win32 hit-test 穿透（滚轮/点击直达任务栏）。
/// 壳层 RootBorder 已垫 #01FFFFFF 兜底（整条不穿透），模块 View 用 Transparent
/// 表达"透明叠任务栏"语义即可，无需自行处理穿透。
/// </summary>
public interface ITaskbarModule
{
    /// <summary>模块唯一标识（持久化键）</summary>
    string Id { get; }

    /// <summary>显示名（设置分区标题/提示等）</summary>
    string DisplayName { get; }

    /// <summary>模块在条内的 UI（UserControl，独占整条显示区域）</summary>
    FrameworkElement View { get; }

    /// <summary>设置分区 UI（由设置窗动态装入）；null = 无设置项</summary>
    FrameworkElement? SettingsSection { get; }

    /// <summary>挂载到壳：订阅壳事件、启动服务。调用时壳 HWND 已就绪、已完成首次贴附。
    /// 启用模块常驻（滚走也不停服务），仅禁用时走 OnDetach；
    /// 禁用→重新启用会再次调用，需幂等安全。</summary>
    void OnAttach(TaskbarShell shell);

    /// <summary>卸载：停服务、退订事件（禁用模块走此路径，需可重入）</summary>
    void OnDetach();

    /// <summary>鼠标进入/离开整条（壳转发，仅当前显示模块收到）</summary>
    void OnHoverChanged(bool hovering);
}
