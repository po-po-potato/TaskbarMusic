using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>显示器信息（A7 设置 UI 列表/建条用）</summary>
public sealed class MonitorEntry
{
    /// <summary>设备名（\\.\DISPLAY1 形式，配置 key）</summary>
    public string Key = "";

    /// <summary>是否主屏</summary>
    public bool IsPrimary;

    /// <summary>分辨率宽（物理像素，展示用）</summary>
    public int Width;

    /// <summary>分辨率高（物理像素，展示用）</summary>
    public int Height;
}

/// <summary>
/// 壳层管理器（A7 多显示器）：多条 Shell 的建/关/重建收口。
///
/// 职责：
/// - 启动：按 config.EnabledMonitors（空 = 仅主屏）逐屏建条
/// - 设置 UI 勾选变化 → ApplyMonitorSelection 增删条（至少保留主屏）
/// - 5s 轮询重扫任务栏集合：新副屏任务栏出现（接显示器/开副屏任务栏）且在勾选集合
///   → 自动补条。不做 WM_DISPLAYCHANGE 监听——条是 reparent 后的子窗口收不到
///   顶层广播，轮询简单可靠；条消失的场景由各条 sticky/逃逸机制自兜底（隐藏等回来），
///   manager 只加不减，避免 explorer 重启窗口期误关条。
/// - 条意外销毁（任务栏重启逃逸失败的兜底路径）→ OnShellClosed 决定是否重建
/// </summary>
internal static class ShellManager
{
    private static readonly List<TaskbarShell> _shells = new();
    private static DispatcherTimer? _rescanTimer;
    private static bool _exiting;
    private static string _primaryKey = "";

    /// <summary>主屏条（托盘/设置窗/SMTC 监视器归属它）；主条未建时为 null</summary>
    internal static TaskbarShell? Primary => _shells.FirstOrDefault(s => s.IsPrimary);

    /// <summary>App 启动入口：建条 + 启动重扫轮询</summary>
    internal static void Startup()
    {
        EnsureShells("startup");
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _rescanTimer.Tick += (_, _) => EnsureShells("rescan");
        _rescanTimer.Start();

        // dev 验证后门：TBM_AUTO_OPEN_SETTINGS=1 启动即开设置窗
        //（A7 设置窗迁移场景的自动化测试钩子，不进发布形态）
        if (Environment.GetEnvironmentVariable("TBM_AUTO_OPEN_SETTINGS") == "1")
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); if (!_exiting) OpenSettings(); };
            t.Start();
        }

        // dev 验证后门：TBM_APPLY_AFTER_MS=N 启动 N ms 后模拟"用户在设置窗里勾选建条"
        // 一次性把显示器勾选改为全集（apply 全屏建条），对比 startup 路径渲染差异——
        // 用于排查 A7 黑条（apply 路径 MusicModule 不渲染）根因
        if (int.TryParse(Environment.GetEnvironmentVariable("TBM_APPLY_AFTER_MS"), out var applyMs))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(applyMs) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_exiting) return;
                var monitors = EnumerateTaskbarMonitors();
                if (monitors.Count > 0)
                {
                    var all = monitors.Select(m => m.Key).ToList();
                    ApplyMonitorSelection(all);
                    MediaService.Trace($"[DEV] TBM_APPLY_AFTER_MS={applyMs} -> apply [{string.Join(",", all)}]");
                }
            };
            t.Start();
        }
    }

    /// <summary>进程退出中（ExitApp 调）：Closed 兜底重建全部静默</summary>
    internal static void MarkExiting() => _exiting = true;

    /// <summary>主屏显示器设备名（Shell_TrayWnd 所在屏；缓存住 explorer 重启窗口期的抖动）</summary>
    internal static string PrimaryMonitorKey
    {
        get
        {
            var tray = Win32.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                var key = Win32.MonitorDeviceOf(tray);
                if (!string.IsNullOrEmpty(key)) _primaryKey = key;
            }
            return _primaryKey;
        }
    }

    /// <summary>枚举有任务栏的显示器（主 Shell_TrayWnd + 全部副 Shell_SecondaryTrayWnd）。
    /// 只有任务栏存在的屏才能挂条——语义上这就是"可选项列表"。</summary>
    internal static List<MonitorEntry> EnumerateTaskbarMonitors()
    {
        var list = new List<MonitorEntry>();
        var primary = Win32.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
        {
            var mon = Win32.MonitorFromWindow(primary, Win32.MONITOR_DEFAULTTONEAREST);
            var entry = EntryOf(mon, isPrimary: true);
            if (entry != null) list.Add(entry);
        }

        Win32.EnumWindows((hwnd, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            Win32.GetClassName(hwnd, sb, 64);
            if (sb.ToString() == "Shell_SecondaryTrayWnd")
            {
                var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
                var entry = EntryOf(mon, isPrimary: false);
                if (entry != null && list.All(e => e.Key != entry.Key)) list.Add(entry);
            }
            return true;
        }, IntPtr.Zero);
        return list;

        static MonitorEntry? EntryOf(IntPtr mon, bool isPrimary)
        {
            if (mon == IntPtr.Zero) return null;
            var mi = new Win32.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>(),
            };
            if (!Win32.GetMonitorInfo(mon, ref mi)) return null;
            if (string.IsNullOrEmpty(mi.szDevice)) return null;
            return new MonitorEntry
            {
                Key = mi.szDevice,
                IsPrimary = isPrimary,
                Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
            };
        }
    }

    /// <summary>解析勾选集合 → 当前实际该挂条的显示器 key 列表：
    /// 过滤掉不存在的屏。主屏不强制保留——只挂副屏是合法选择；
    /// 唯一保底：集合为空（用户全不勾）回落主屏，防进程失去全部 UI 入口</summary>
    private static List<string> ResolveEnabled(List<MonitorEntry> taskbarMonitors)
    {
        var config = AppConfig.Shared;
        string pk = taskbarMonitors.FirstOrDefault(m => m.IsPrimary)?.Key ?? PrimaryMonitorKey;

        var enabled = config.EnabledMonitors
            .Where(k => taskbarMonitors.Any(m => m.Key == k))
            .ToList();
        if (enabled.Count == 0 && !string.IsNullOrEmpty(pk)) enabled.Add(pk); // 全不勾 → 主屏兜底
        return enabled;
    }

    /// <summary>对齐目标状态：缺的屏建条（含 trace）；多的屏关条。
    /// rescan 路径只加不减——关条只由用户改勾选（ApplyMonitorSelection）触发。
    /// 变更检测：仅建/关条或托盘归属变化时才 NotifyTrayOwnerChanged——
    /// rescan 每 5s 一轮，无条件广播会让设置窗 RebuildSections 反复执行并
    /// 把用户踢回首分区（2026-09-21 实锤"每隔几秒跳回常规"根因）。</summary>
    private static void EnsureShells(string source)
    {
        if (_exiting) return;
        var monitors = EnumerateTaskbarMonitors();
        if (monitors.Count == 0) return; // explorer 重启窗口期：等下轮

        var enabled = ResolveEnabled(monitors);
        bool changed = false;

        // 关掉显示器已不存在的条（显示器拔了；条自己 sticky 会一直隐藏，这里主动收掉）
        // ——仅限 EnsureShells 的显式路径（startup/apply/rescan 都适用：勾选集合
        //    已过滤不存在的屏，多出来的条就是孤儿）
        foreach (var shell in _shells.Where(s => !enabled.Contains(GetKey(s))).ToList())
        {
            MediaService.Trace($"[A7] close orphan bar {GetKey(shell)} ({source})");
            CloseShell(shell);
            changed = true;
        }

        foreach (var key in enabled)
        {
            if (_shells.Any(s => GetKey(s) == key)) continue;
            var mon = monitors.First(m => m.Key == key);
            MediaService.Trace($"[A7] create bar {key} primary={mon.IsPrimary} {mon.Width}x{mon.Height} ({source})");
            var shell = new TaskbarShell(key, mon.IsPrimary);
            _shells.Add(shell);
            shell.Show();
            changed = true;

            // 黑条修复尝试（apply 路径 MusicModule 不渲染根因未知）；
            // 在 Render 优先级异步强制 layout pass + 重跑 module 背景更新——
            // WPF 透明 + WS_EX_LAYERED + 子窗口在"运行中创建"路径的渲染初始化
            // 时序可能异常，强制 invalidate 给一次补救机会
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (_exiting || !shell.IsLoaded) return;
                shell.UpdateLayout();
                shell.InvalidateVisual();
                MediaService.Trace($"[A7-blackbar-fix] forced layout for {key} (source={source})");
            }));
        }

        // 迁移：旧全局 OffsetX（V1 单条时代）首次进多屏配置时落到主屏 entry
        MigrateLegacyOffset(monitors);

        // 托盘归属收口：主条优先，主条不存在（只挂副屏）跟随第一条存活条。
        // 每次建/关条后重算，覆盖主条被关 → 托盘迁移副条的场景
        changed |= EnsureTray();

        // 设置窗分区刷新：仅结构真的变化时广播（托盘归属/条集合变化）；
        // rescan 空轮不广播（防设置窗被反复踢回首分区）
        if (changed) NotifyTrayOwnerChanged();
    }

    /// <summary>托盘宿主：主条优先，否则第一条存活条（只挂副屏时托盘跟副条走）</summary>
    internal static TaskbarShell? TrayOwner =>
        _shells.FirstOrDefault(s => s.IsPrimary) ?? _shells.FirstOrDefault();

    /// <summary>shell 自查：本条是否该持托盘（Loaded 时用；幂等建由 shell.AttachTray 兜）</summary>
    internal static bool ShouldOwnTray(TaskbarShell shell) => ReferenceEquals(TrayOwner, shell);

    /// <summary>对齐托盘归属：宿主建（幂等），非宿主拆。单实例托盘，多屏不重复。
    /// 返回归属是否发生变化（EnsureShells 的变更检测用）</summary>
    private static bool EnsureTray()
    {
        var owner = TrayOwner;
        if (owner == null) return false;
        bool changed = !ReferenceEquals(_trayOwner, owner);
        if (changed)
            MediaService.Trace($"[A7] tray owner -> {GetKey(owner)}");
        _trayOwner = owner;
        foreach (var s in _shells.Where(s => !ReferenceEquals(s, owner)).ToList())
            s.DetachTray();
        owner.AttachTray();
        return changed;
    }
    private static TaskbarShell? _trayOwner;

    /// <summary>托盘归属变化事件（建/关条后重算时触发）：设置窗订阅刷新分区内容。
    /// 替代原"宿主条死 → 关窗重开"——窗不关只换内容，位置/尺寸保持，无闪烁。
    /// startup 时也会触发一次，但设置窗尚未打开，event=null 安全无副作用</summary>
    internal static event Action? TrayOwnerChanged;
    private static void NotifyTrayOwnerChanged() => TrayOwnerChanged?.Invoke();

    // ===== 设置窗（进程级单例，Win32 层完全独立于任何条）=====
    // 原 TaskbarShell._settingsWindow 实例字段是架构债：Win32 层（Owner/WS_CHILD）和
    // 条没有任何关联，但分区内容构造时绑了宿主条实例（SettingsWindow(shell, host) +
    // ShellSettingsSection(this) 操作该条 offset/host.Modules）——宿主条销毁后 View
    // 持有死对象只能关窗重开，"闪一下"是补丁。
    // 改造：设置窗从宿主条字段升级为 ShellManager 静态管理，宿主条销毁时窗不关，
    // 只通过 TrayOwnerChanged 事件触发分区内容重建——视觉上是内容闪，窗不动。

    private static SettingsWindow? _settingsWindow;
    internal static SettingsWindow? SettingsWindow => _settingsWindow;
    internal static bool SettingsWindowOpen => _settingsWindow != null;

    /// <summary>打开设置窗（条右键菜单/托盘右键/双击共用入口）。单例 + 激活</summary>
    internal static void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; };
        _settingsWindow.Show();
    }

    /// <summary>材质切换：backdrop 是窗口级一次性设置，关+开干净生效。
    /// 由 ShellSettingsViewModel.BackdropChanged 经 ShellManager 调用</summary>
    internal static void ReopenSettings()
    {
        if (_settingsWindow != null) _settingsWindow.Close();
        OpenSettings();
    }

    /// <summary>构造分区列表（设置窗打开/TrayOwner 变化时调用）：
    /// 壳分区 + 各模块分区，全部跟随当前 TrayOwner</summary>
    internal static List<FrameworkElement> BuildSectionList()
    {
        var list = new List<FrameworkElement>();
        var owner = TrayOwner;
        if (owner == null) return list;
        list.Add(owner.ShellSectionForSettings);
        foreach (var m in owner.Host.ModulesInOrder)
        {
            if (m.SettingsSection != null) list.Add(m.SettingsSection);
        }
        return list;
    }

    /// <summary>壳分区回调的统一入口：宿主操作改为"当前 TrayOwner"操作。
    /// 替代 ShellSettingsSection 直接绑 TaskbarShell 实例方法的耦合——
    /// 解耦后 ShellSettingsSection 无需持有 shell 引用</summary>
    internal static void RequestResetPosition() => TrayOwner?.ResetPosition();
    internal static void RequestResetWidth() => TrayOwner?.ResetWidth();
    internal static void RequestRefreshModulesTextStyle()
    {
        foreach (var s in _shells) s.RefreshModulesTextStyle();
    }

    // ===== SMTC 监视器（进程级单例，与设置窗同模式）=====
    private static SmtcMonitorWindow? _smtcMonitorWindow;
    internal static bool SmtcMonitorOpen => _smtcMonitorWindow != null;

    /// <summary>打开 SMTC 监视器（条右键/托盘右键/诊断入口共用）。
    /// 从 TrayOwner 的 MusicModule 拿 MediaService（M2 模块分区跨条共享同一单例）</summary>
    internal static void OpenSmtcMonitor()
    {
        if (_smtcMonitorWindow != null) { _smtcMonitorWindow.Activate(); return; }

        MediaService? media = null;
        var owner = TrayOwner;
        if (owner != null)
        {
            foreach (var module in owner.Host.Modules)
                if (module is MusicModule music)
                {
                    media = music.Media;
                    break;
                }
        }
        if (media == null) return;

        _smtcMonitorWindow = new SmtcMonitorWindow(media, AppConfig.Shared);
        _smtcMonitorWindow.Closed += (_, _) => { _smtcMonitorWindow = null; };
        _smtcMonitorWindow.Show();
    }

    /// <summary>设置 UI 勾选变化：写配置 + 立即增删条</summary>
    internal static void ApplyMonitorSelection(IEnumerable<string> keys)
    {
        var config = AppConfig.Shared;
        config.EnabledMonitors = keys.Distinct().ToList();
        config.Save();
        EnsureShells("apply");
    }

    /// <summary>条意外销毁（任务栏重启兜底路径）：仍在勾选集合 → 重建；否则仅移除登记。
    /// 设置窗迁移：宿主条死（含用户在设置窗里勾选增删条）→ 立即关旧窗防双开，
    /// 替代条就绪后由新宿主（TrayOwner）重开</summary>
    internal static void OnShellClosed(TaskbarShell shell)
    {
        _shells.Remove(shell);
        if (_exiting) return;
        var key = GetKey(shell);
        MediaService.Trace($"[A7] bar closed {key} (unexpected; rebuild if still enabled)");

        // 异步重建（原兜底路径语义：BeginInvoke 等 Closed 完全走完）
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_exiting) return;
            // 重建竞态：已有替代条（如 apply 路径主循环已建）则不重复 EnsureShells
            if (!_shells.Any(s => GetKey(s) == key)) EnsureShells("rebuild");
            // EnsureShells 末尾已 NotifyTrayOwnerChanged，设置窗若开着会自动刷新分区内容
        });
    }

    /// <summary>设置 UI 用：当前勾选集合（ResolveEnabled 后的稳定值）</summary>
    internal static List<string> CurrentSelection()
    {
        var monitors = EnumerateTaskbarMonitors();
        return monitors.Count == 0
            ? new List<string> { PrimaryMonitorKey }
            : ResolveEnabled(monitors);
    }

    private static void CloseShell(TaskbarShell shell)
    {
        _shells.Remove(shell);
        shell.Close(); // Closing 里 DetachAll + Save 正常走
    }

    private static string GetKey(TaskbarShell shell) => shell.MonitorKey;

    /// <summary>旧全局 OffsetX 一次性迁移：主屏 entry 尚无记录且旧值非默认 → 写入。
    /// 之后统一读 MonitorOffsets（主屏无记录时 OffsetXOf 仍回退旧字段，双保险）。</summary>
    private static void MigrateLegacyOffset(List<MonitorEntry> monitors)
    {
        var config = AppConfig.Shared;
        var pk = monitors.FirstOrDefault(m => m.IsPrimary)?.Key;
        if (pk == null || config.MonitorOffsets.ContainsKey(pk)) return;
        if (Math.Abs(config.OffsetX - 200) < 0.01) return; // 默认值无迁移意义
        config.MonitorOffsets[pk] = config.OffsetX;
        config.Save();
        MediaService.Trace($"[A7] legacy OffsetX {config.OffsetX} migrated to primary {pk}");
    }
}
