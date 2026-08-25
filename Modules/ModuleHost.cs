using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>
/// 模块宿主：注册/挂载/卸载模块，把模块 View 装进壳的模块区域。
/// V1 只有音乐一个模块（占满整条）；M2 起做多模块槽位分配（PRD E6 槽位模型）。
/// </summary>
public sealed class ModuleHost
{
    private readonly List<ITaskbarModule> _modules = new();

    public IReadOnlyList<ITaskbarModule> Modules => _modules;

    public void Register(ITaskbarModule module) => _modules.Add(module);

    /// <summary>壳 Loaded 后调用：逐模块 attach 并把 View 装入布局容器</summary>
    public void AttachAll(TaskbarShell shell, Panel container)
    {
        foreach (var m in _modules)
        {
            m.OnAttach(shell);
            container.Children.Add(m.View);
        }
    }

    /// <summary>卸载全部模块（应用退出时）</summary>
    public void DetachAll()
    {
        foreach (var m in _modules) m.OnDetach();
        _modules.Clear();
    }

    /// <summary>壳 hover 进/出整条时广播</summary>
    public void BroadcastHover(bool hovering)
    {
        foreach (var m in _modules) m.OnHoverChanged(hovering);
    }
}
