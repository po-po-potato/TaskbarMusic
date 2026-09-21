using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// 网速设置分区（A6）：网卡选择 + 显示风格（小数位/单位显隐/箭头风格）+ 今日累计。
/// 网卡下拉数据源 = 枚举 Up 且非 Loopback/Tunnel 的网卡（与采样器同过滤条件）；
/// 选中项写 NetSpeedAdapterId（空 = 自动），采样器下一秒自然生效。
/// 风格三项写 config 即时落盘——模块 OnStateChanged 每秒幂等拾取（改完 ≤1s 生效）。
/// 今日累计 1s 跟随采样器刷新（分区常驻，仅窗开着可见时 tick 才有意义）。
/// </summary>
public partial class NetSpeedSettingsSection : UserControl
{
    private readonly DispatcherTimer _refresh;
    private bool _loadingStyle = true; // 构造期初始化控件触发 SelectionChanged，防误写

    public NetSpeedSettingsSection(NetSpeedModule module)
    {
        InitializeComponent();
        _ = module; // 状态快照走 NetSpeedModule static（A7 单一真源），参数仅为对齐构造契约

        ReloadAdapters();
        LoadStyle();

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) =>
        {
            if (!IsVisible) return; // 窗关/分区不在树上不刷
            var (rx, tx) = NetSpeedModule.Today;
            TodayText.Text = $"↓ {FormatBytes(rx)} · ↑ {FormatBytes(tx)}";
            if (!AdapterBox.IsDropDownOpen) ReloadAdapters(); // 下拉开着不重建（会打断选择）
        };
        _refresh.Start();
    }

    // ===== 显示风格（小数位 / 单位 / 箭头）=====

    /// <summary>构造期按配置初始化三个风格控件（_loadingStyle 抑制初始化事件写回）</summary>
    private void LoadStyle()
    {
        var cfg = AppConfig.Shared;
        DecimalBox.SelectedIndex = cfg.NetSpeedDecimalPlaces <= 0 ? 0 : 1;
        UnitModeBox.SelectedIndex = Math.Clamp(cfg.NetSpeedUnitMode, 0, 2);
        ArrowBox.SelectedIndex = Math.Clamp(cfg.NetSpeedArrowStyle, 0, 2);
        _loadingStyle = false;
    }

    private void DecimalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle) return;
        AppConfig.Shared.NetSpeedDecimalPlaces = DecimalBox.SelectedIndex <= 0 ? 0 : 1;
        AppConfig.Shared.Save();
        MediaService.Trace($"[NET] style decimals -> {AppConfig.Shared.NetSpeedDecimalPlaces}");
    }

    private void UnitModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle) return;
        AppConfig.Shared.NetSpeedUnitMode = Math.Max(0, UnitModeBox.SelectedIndex);
        AppConfig.Shared.Save();
        MediaService.Trace($"[NET] style unit mode -> {AppConfig.Shared.NetSpeedUnitMode}");
    }

    private void ArrowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle) return;
        AppConfig.Shared.NetSpeedArrowStyle = Math.Max(0, ArrowBox.SelectedIndex);
        AppConfig.Shared.Save();
        MediaService.Trace($"[NET] style arrow -> {AppConfig.Shared.NetSpeedArrowStyle}");
    }

    // ===== 网卡选择 =====

    /// <summary>重建网卡下拉：自动 + 当前 Up 的网卡列表；选中项对齐配置</summary>
    private void ReloadAdapters()
    {
        string current = AppConfig.Shared.NetSpeedAdapterId;
        int sel = 0;
        AdapterBox.Items.Clear();
        AdapterBox.Items.Add(new AdapterItem("", "自动（跟随流量最大的网卡）"));
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                           or NetworkInterfaceType.Tunnel) continue;
                AdapterBox.Items.Add(new AdapterItem(nic.Id, nic.Description));
                if (nic.Id == current) sel = AdapterBox.Items.Count - 1;
            }
        }
        catch { /* 枚举失败保留"自动" */ }
        AdapterBox.SelectedIndex = Math.Min(sel, AdapterBox.Items.Count - 1);
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not AdapterItem item) return;
        if (AppConfig.Shared.NetSpeedAdapterId == item.Id) return; // Reload 重建触发的同值选择不落盘
        AppConfig.Shared.NetSpeedAdapterId = item.Id;
        AppConfig.Shared.Save();
        MediaService.Trace($"[NET] adapter lock -> {(string.IsNullOrEmpty(item.Id) ? "auto" : item.Name)}");
    }

    private record AdapterItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static string FormatBytes(double b) => b < 1024
        ? $"{b:0} B"
        : b < 1024 * 1024
            ? $"{b / 1024:0.#} KB"
            : b < 1024L * 1024 * 1024
                ? $"{b / 1024 / 1024:0.#} MB"
                : $"{b / 1024 / 1024 / 1024:0.##} GB";
}
