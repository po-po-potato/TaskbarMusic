using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TaskbarMusic;

/// <summary>
/// 模块宿主（M2 E6 单屏轮播模型）：注册/挂载/卸载模块 + 滚轮切换显示对象。
///
/// 模型（PRD E6 v3，2026-09-08 设计转向）：
/// - 条 = 一块固定宽度的小显示屏，**同时只显示一个模块**，独占整条宽度。
/// - 鼠标滚轮切换：上 = 轮播序列前一个，下 = 后一个，循环。
/// - 启用模块**全部常驻运行**（OnAttach 后滚走不停服务——音乐滚走 SMTC 监控不断），
///   滚轮只换视觉树里的 View；禁用模块走 OnDetach。
/// - 启用/禁用与轮播顺序持久化在 AppConfig.ModuleSlots；
///   当前显示模块持久化在 AppConfig.ActiveModuleId（重启恢复）。
/// - 单模块启用时无滚轮切换对象，行为与 V1 完全一致（向后兼容验收基准）。
/// </summary>
public sealed class ModuleHost
{
    /// <summary>滚轮切换过渡：**即时换台 + 纯淡入**（2026-09-08 用户反馈修正）。
    /// - 出场不做动画——旧视图切换瞬间移出视觉树，条上任何时刻只有一个模块可见，
    ///   不存在新旧重叠/半透明残留（反馈"其他模块不要显示在底下"的根因）。
    /// - 入场 120ms 纯透明度淡入，**无位移**——位移动画在分层子窗口里逐帧重渲染
    ///   整棵子树（含 DropShadowEffect），实测卡顿（反馈"位移很卡"的根因）。
    /// - 淡入期间视图挂 BitmapCache：整棵视图按缓存位图合成（GPU 路径），
    ///   结束后清掉恢复 ClearType 文本渲染。</summary>
    private const int FadeInMs = 120;

    /// <summary>A7 跨条同步：某条滚轮切换后广播（moduleId + 源 host）。
    /// 其他条的 host 收到后本地切到同一模块（不落盘、不再广播——防环）。</summary>
    internal static event Action<string, ModuleHost>? ModuleSwitched;

    private readonly List<ITaskbarModule> _modules = new();
    private Grid? _panel;
    private AppConfig? _config;
    private TaskbarShell? _shellRef;

    /// <summary>轮播序列：启用模块按配置 Order 排列（滚轮切换的先后序）</summary>
    private List<ITaskbarModule> _rotation = new();

    /// <summary>当前显示模块在 _rotation 里的下标；-1 = 无可显示模块</summary>
    private int _activeIndex = -1;

    // 入场淡入动画（快速连滚时上一个立即终结还原，保证最终状态正确）
    private Storyboard? _inStory;

    /// <summary>全部已注册模块（含禁用态——禁用只影响挂载，不注销）</summary>
    public IReadOnlyList<ITaskbarModule> Modules => _modules;

    /// <summary>按轮播顺序排列的全部模块（设置窗"模块"组行序用）</summary>
    public IEnumerable<ITaskbarModule> ModulesInOrder => OrderedModules();

    /// <summary>当前显示的模块；无可显示模块（全禁用）时为 null</summary>
    public ITaskbarModule? CurrentModule =>
        _activeIndex >= 0 && _activeIndex < _rotation.Count ? _rotation[_activeIndex] : null;

    public void Register(ITaskbarModule module) => _modules.Add(module);

    /// <summary>壳 Loaded 后调用：按配置合并 → 启用模块全部挂载（服务常驻）→ 显示当前模块。
    /// 滚轮事件挂壳 root（Preview 隧道，模块内容拦不住）。</summary>
    public void AttachAll(TaskbarShell shell, Panel container)
    {
        _config = shell.Config;
        _shellRef = shell;
        _panel = container as Grid
            ?? throw new ArgumentException("E6 单屏轮播需要 Grid 容器（当前模块 View 独占）", nameof(container));

        bool configDirty = MergeSlotConfig();

        // 启用模块全部挂载（服务常驻），滚轮只换 View
        _rotation = OrderedModules().Where(m => GetState(m.Id) != ModuleDisplayState.Disabled).ToList();
        foreach (var module in _rotation)
            module.OnAttach(shell);

        // 配置合并有变更（清残留/归一旧值/补缺失）→ 落盘
        if (configDirty) _config.Save();

        // 恢复上次显示的模块；配置指向禁用/未知模块时回退序列首位
        _activeIndex = 0;
        var saved = _config.ActiveModuleId;
        if (!string.IsNullOrEmpty(saved))
        {
            int idx = _rotation.FindIndex(m => m.Id == saved);
            if (idx >= 0) _activeIndex = idx;
        }
        if (_rotation.Count > 0)
        {
            var view = _rotation[_activeIndex].View;
            view.Opacity = 1;
            view.RenderTransform = null;
            view.CacheMode = null;
            _panel.Children.Clear();
            _panel.Children.Add(view);
        }

        // 滚轮切换：Preview 隧道事件，条上任何位置滚动都触发
        shell.PreviewMouseWheel += OnShellWheel;

        MediaService.Trace(
            $"[E6] rotation=[{string.Join(",", _rotation.Select(m => m.Id))}] " +
            $"active={CurrentModule?.Id ?? "none"}");
    }

    /// <summary>卸载全部模块（应用退出时）</summary>
    public void DetachAll()
    {
        if (_shellRef != null) _shellRef.PreviewMouseWheel -= OnShellWheel;
        SnapAnimations();
        foreach (var m in _modules) m.OnDetach();
        _rotation.Clear();
        _activeIndex = -1;
        _panel?.Children.Clear();
    }

    /// <summary>壳 hover 进/出整条时广播（仅当前显示模块——不可见模块无交互）</summary>
    public void BroadcastHover(bool hovering) => CurrentModule?.OnHoverChanged(hovering);

    // ===== 滚轮切换 =====

    private void OnShellWheel(object sender, MouseWheelEventArgs e)
    {
        // E6 诊断：无条件记录滚轮事件到达（区分"事件未送达"与"被守卫忽略"）
        MediaService.Trace($"[E6 wheel-evt] delta={e.Delta} rotation={_rotation.Count} active={CurrentModule?.Id ?? "none"}");
        if (_rotation.Count < 2) return; // 单模块/全禁用：无可切换对象（V1 行为）
        int dir = e.Delta > 0 ? -1 : 1; // 上 = 前一个，下 = 后一个
        SwitchTo(_activeIndex + dir, $"wheel{(e.Delta > 0 ? "+" : "-")}");
        e.Handled = true;
    }

    /// <summary>切换到轮播序列指定下标（循环取模）。即时换台：旧视图直接移出，
    /// 新视图纯淡入；快速连滚时上一个淡入立即终结还原。</summary>
    private void SwitchTo(int index, string source)
    {
        if (_panel == null || _rotation.Count == 0) return;
        index = ((index % _rotation.Count) + _rotation.Count) % _rotation.Count;
        if (index == _activeIndex) return;

        var next = _rotation[index];

        // 落盘当前模块 + trace
        if (_config != null)
        {
            _config.ActiveModuleId = next.Id;
            _config.Save();
        }
        MediaService.Trace($"[E6 switch] {CurrentModule?.Id ?? "none"} -> {next.Id} ({source})");
        ModuleSwitched?.Invoke(next.Id, this); // A7：广播其他条同步切换

        // 滚走的模块先收 hover-out（不可见即无交互）：hover 浮层（F2.4/E5）
        // 需要立即收起，不能等鼠标物理离开条才关
        CurrentModule?.OnHoverChanged(false);

        _activeIndex = index;
        ShowView(next.View, animate: true);
    }

    /// <summary>把模块 View 换进显示区。即时换台（旧视图立即移出视觉树——任何时刻
    /// 条上只有一个模块）+ 新视图 120ms 纯淡入（无位移，BitmapCache 缓存合成）</summary>
    private void ShowView(FrameworkElement view, bool animate)
    {
        if (_panel == null) return;

        // 终结进行中的淡入并还原视图状态（连滚时上一个视图可能停在半透明+缓存态，
        // 不还原的话下次轮到它显示会带着残态入场）
        if (_inStory != null)
        {
            _inStory.Remove();
            _inStory = null;
        }
        foreach (var m in _rotation)
        {
            m.View.Opacity = 1;
            m.View.RenderTransform = null;
            m.View.CacheMode = null;
        }

        // 即时换台：旧视图立即移出，条上任何时刻只有一个模块可见
        _panel.Children.Clear();
        _panel.Children.Add(view);

        if (!animate)
        {
            view.Opacity = 1;
            return;
        }

        // 入场：纯透明度淡入（无位移）。淡入期间 BitmapCache——整棵视图按缓存
        // 位图合成，避免分层子窗口里逐帧重渲染整棵子树（卡顿根因）；
        // 结束后清掉恢复 ClearType 文本渲染。
        view.CacheMode = new BitmapCache();
        view.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var story = new Storyboard();
        Storyboard.SetTarget(fade, view);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        story.Children.Add(fade);
        story.Completed += (_, _) =>
        {
            _inStory = null;
            view.Opacity = 1;
            view.CacheMode = null; // 还原干净：ClearType 文本 + 无缓存
        };
        _inStory = story;
        story.Begin();
    }

    /// <summary>立即终结进行中的淡入动画，全部视图落到确定态
    /// （快速连滚在 ShowView 开头处理；此处供卸载/配置变更/排序场景调用）</summary>
    private void SnapAnimations()
    {
        if (_inStory != null)
        {
            _inStory.Remove();
            _inStory = null;
        }
        foreach (var m in _rotation)
        {
            m.View.Opacity = 1;
            m.View.RenderTransform = null;
            m.View.CacheMode = null;
        }
    }

    // ===== 设置窗"模块"组入口 =====

    /// <summary>应用新状态（设置窗"模块"组）：即时生效 + 持久化。
    /// 旧配置的 Collapsed 值归一为 Expanded（折叠概念已废弃）。
    /// 禁用 → OnDetach 并移出轮播；启用 → OnAttach 插回轮播序位置。</summary>
    public void ApplyModuleState(string moduleId, ModuleDisplayState newState)
    {
        if (newState == ModuleDisplayState.Collapsed) newState = ModuleDisplayState.Expanded;
        if (_config == null || _shellRef == null) return;
        var module = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (module == null) return;

        var oldState = GetState(moduleId);
        if (oldState == newState) return;

        SetState(moduleId, newState);
        _config.Save();

        SnapAnimations();

        if (newState == ModuleDisplayState.Disabled)
        {
            int idx = _rotation.FindIndex(m => m.Id == moduleId);
            if (idx >= 0)
            {
                bool wasActive = idx == _activeIndex;
                _rotation.RemoveAt(idx);
                module.OnDetach();
                if (wasActive)
                {
                    // 切到剩余序列里的下一个（原位置的继任者，回绕）
                    _activeIndex = -1;
                    if (_rotation.Count > 0)
                    {
                        int next = Math.Min(idx, _rotation.Count - 1);
                        ShowView(_rotation[next].View, animate: false);
                        _activeIndex = next;
                        if (_config.ActiveModuleId != _rotation[next].Id)
                        {
                            _config.ActiveModuleId = _rotation[next].Id;
                            _config.Save();
                        }
                    }
                    else
                    {
                        _panel?.Children.Clear();
                    }
                }
                else if (idx < _activeIndex)
                {
                    _activeIndex--;
                }
            }
        }
        else
        {
            // 从禁用恢复：挂载 + 插回轮播序位置
            module.OnAttach(_shellRef);
            if (_rotation.Contains(module)) return;
            _rotation = OrderedModules().Where(m => GetState(m.Id) != ModuleDisplayState.Disabled).ToList();
            if (_rotation.Count == 1 || _activeIndex < 0)
            {
                // 之前无可显示模块（或首个启用）：直接显示它
                _activeIndex = _rotation.IndexOf(module);
                ShowView(module.View, animate: false);
                _config.ActiveModuleId = module.Id;
                _config.Save();
            }
            // 否则保持当前显示，新模块仅进入轮播序列
        }

        MediaService.Trace(
            $"[E6 state] id={moduleId} {oldState}->{newState} " +
            $"rotation=[{string.Join(",", _rotation.Select(m => m.Id))}] active={CurrentModule?.Id ?? "none"}");
    }

    /// <summary>调整模块轮播顺序（设置窗 ↑↓ 按钮，delta = -1 上移 / +1 下移）</summary>
    public void MoveModuleOrder(string moduleId, int delta)
    {
        if (_config == null || delta == 0) return;
        var ordered = OrderedModules().ToList();
        int idx = ordered.FindIndex(m => m.Id == moduleId);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= ordered.Count) return;

        (ordered[idx], ordered[target]) = (ordered[target], ordered[idx]);

        // Order 归一写回（0..n-1），持久化
        for (int i = 0; i < ordered.Count; i++)
        {
            var cfg = _config.ModuleSlots.FirstOrDefault(c => c.Id == ordered[i].Id);
            if (cfg != null) cfg.Order = i;
        }
        _config.Save();

        // 轮播序列按新序重建，保持当前显示模块不变
        SnapAnimations();
        var current = CurrentModule;
        _rotation = ordered.Where(m => GetState(m.Id) != ModuleDisplayState.Disabled).ToList();
        _activeIndex = current != null ? _rotation.IndexOf(current) : -1;
        if (_activeIndex < 0 && _rotation.Count > 0)
        {
            _activeIndex = 0;
            ShowView(_rotation[0].View, animate: false);
        }

        MediaService.Trace(
            $"[E6 order] id={moduleId} move {(delta < 0 ? "up" : "down")} " +
            $"rotation=[{string.Join(",", _rotation.Select(m => m.Id))}] active={CurrentModule?.Id ?? "none"}");
    }

    // ===== 模块配置（AppConfig.ModuleSlots 的读写封装） =====

    /// <summary>配置合并：清残留/归一旧折叠值/补缺失模块。返回是否有变更（有则落盘）</summary>
    private bool MergeSlotConfig()
    {
        if (_config == null) return false;
        bool dirty = false;
        // 清掉已不存在模块的残留配置（防长期膨胀）
        if (_config.ModuleSlots.RemoveAll(c => _modules.All(m => m.Id != c.Id)) > 0) dirty = true;
        // 旧折叠值归一（v2 并列模型遗留）
        foreach (var c in _config.ModuleSlots)
            if (c.State == ModuleDisplayState.Collapsed)
            {
                c.State = ModuleDisplayState.Expanded;
                dirty = true;
            }
        // 补齐缺失模块（默认启用 + 注册序）
        for (int i = 0; i < _modules.Count; i++)
        {
            if (_config.ModuleSlots.All(c => c.Id != _modules[i].Id))
            {
                _config.ModuleSlots.Add(new ModuleSlotConfig
                {
                    Id = _modules[i].Id,
                    State = ModuleDisplayState.Expanded,
                    Order = i,
                });
                dirty = true;
            }
        }
        return dirty;
    }

    private ModuleDisplayState GetState(string id) =>
        _config?.ModuleSlots.FirstOrDefault(c => c.Id == id)?.State
        ?? ModuleDisplayState.Expanded;

    /// <summary>查询模块当前状态（设置窗"模块"组初始化用）</summary>
    public ModuleDisplayState StateOf(string id) => GetState(id);

    private void SetState(string id, ModuleDisplayState state)
    {
        if (_config == null) return;
        var cfg = _config.ModuleSlots.FirstOrDefault(c => c.Id == id);
        if (cfg != null) cfg.State = state;
    }

    /// <summary>按配置 Order 排序（缺省注册序）</summary>
    private IEnumerable<ITaskbarModule> OrderedModules()
    {
        if (_config == null) return _modules;
        return _modules
            .Select((m, idx) => (m, order: _config.ModuleSlots.FirstOrDefault(c => c.Id == m.Id)?.Order ?? idx))
            .OrderBy(t => t.order)
            .Select(t => t.m);
    }
}
