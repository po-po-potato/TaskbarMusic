using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// A3 天气模块（设计稿定稿 2026-09-21）。
///
/// 数据：Open-Meteo（免费无 key）——current（温度/体感/湿度/风/WMO code）+
/// daily 3 天（code/最高最低温）。30min DispatcherTimer 定时刷新 + 配置城市后立即刷。
/// 断网/失败：保留缓存数据继续显示，hover 浮层标注最后成功更新时间。
/// 未配置城市：条内显示占位（--° 未配置），hover 浮层引导去设置。
/// A7 多显示器：数据 static 单一真源，StateChanged 广播刷新各条 UI。
/// WMO weather code → 10 类内置图标（Canvas 组合矢量绘制，无图片资源）。
/// </summary>
public partial class WeatherModule : UserControl, ITaskbarModule
{
    public string Id => "weather";
    public string DisplayName => "天气";
    public FrameworkElement View => this;

    private WeatherSettingsSection? _settingsSection;
    public FrameworkElement? SettingsSection => _settingsSection ??= new WeatherSettingsSection(this);

    private TaskbarShell? _shell;
    private OverlayWindow? _overlay;

    // ===== static 数据（A7 跨条共享的单一真源）=====

    private sealed class WeatherData
    {
        public double Temp;
        public double Apparent;
        public int Humidity;
        public double Wind;
        public int Code;
        public List<DailyEntry> Daily = new();
        public DateTime Updated;
        public bool FetchFailed;
    }

    private sealed class DailyEntry
    {
        public int Code;
        public double Min, Max;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static WeatherData? _data;
    private static DispatcherTimer? _timer;
    private static int _attachCount;
    private static bool _fetching;
    private static event Action? StateChanged;

    public WeatherModule(AppConfig config)
    {
        InitializeComponent();
        _ = config; // 读 AppConfig.Shared（static 单一真源），参数对齐模块契约
    }

    // ===== ITaskbarModule 生命周期 =====

    public void OnAttach(TaskbarShell shell)
    {
        _shell = shell;
        StateChanged += OnStateChanged;
        _attachCount++;
        EnsureTimer();
        RefreshDisplay(); // 首帧立即渲染（缓存或占位），不等首个 tick
        MediaService.Trace($"[WX] attached ({_attachCount} bar) city={AppConfig.Shared.WeatherCity}");
    }

    public void OnDetach()
    {
        _overlay?.HideOverlay();
        StateChanged -= OnStateChanged;
        _attachCount = Math.Max(0, _attachCount - 1);
        if (_attachCount == 0)
        {
            _timer?.Stop();
            MediaService.Trace("[WX] detached (last bar) timer stopped");
        }
    }

    public void OnHoverChanged(bool hovering)
    {
        if (hovering) ShowHoverOverlay();
        else _overlay?.GraceHide();
    }

    private void OnStateChanged() => RefreshDisplay();

    private static void EnsureTimer()
    {
        if (_timer != null) { _timer.Start(); return; }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_, _) => _ = FetchAsync();
        _timer.Start();
        _ = FetchAsync(); // 首次立即拉
    }

    /// <summary>设置分区改城市后调用：立即重拉（不等 30min 周期）</summary>
    internal static void RefreshNow() => _ = FetchAsync();

    // ===== 拉取与解析 =====

    private static async System.Threading.Tasks.Task FetchAsync()
    {
        var cfg = AppConfig.Shared;
        if (string.IsNullOrEmpty(cfg.WeatherCity) || (cfg.WeatherLat == 0 && cfg.WeatherLon == 0))
            return; // 未配置城市：条内占位即可
        if (_fetching) return; // 防重入（30min tick 与设置分区触发重叠）
        _fetching = true;
        try
        {
            string url = "https://api.open-meteo.com/v1/forecast" +
                $"?latitude={cfg.WeatherLat}&longitude={cfg.WeatherLon}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=3&timezone=auto";
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var cur = doc.RootElement.GetProperty("current");
            var day = doc.RootElement.GetProperty("daily");
            var times = day.GetProperty("time");
            var codes = day.GetProperty("weather_code");
            var maxs = day.GetProperty("temperature_2m_max");
            var mins = day.GetProperty("temperature_2m_min");

            var d = new WeatherData
            {
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Apparent = cur.GetProperty("apparent_temperature").GetDouble(),
                Humidity = cur.GetProperty("relative_humidity_2m").GetInt32(),
                Wind = cur.GetProperty("wind_speed_10m").GetDouble(),
                Code = cur.GetProperty("weather_code").GetInt32(),
                Updated = DateTime.Now,
                FetchFailed = false,
            };
            for (int i = 0; i < times.GetArrayLength() && i < 3; i++)
                d.Daily.Add(new DailyEntry
                {
                    Code = codes[i].GetInt32(),
                    Max = maxs[i].GetDouble(),
                    Min = mins[i].GetDouble(),
                });
            _data = d;
            MediaService.Trace($"[WX] fetched {cfg.WeatherCity} {d.Temp:0}° code={d.Code} days={d.Daily.Count}");
        }
        catch (Exception ex)
        {
            // 失败保留缓存：_data 不动，仅标记（浮层显示最后更新时间）
            if (_data != null) _data.FetchFailed = true;
            MediaService.Trace($"[WX] fetch error {ex.Message}");
        }
        finally
        {
            _fetching = false;
            StateChanged?.Invoke();
        }
    }

    // ===== 显示 =====

    private void RefreshDisplay()
    {
        if (_data == null)
        {
            IconHost.Content = null;
            TempText.Text = "--°";
            DescText.Text = string.IsNullOrEmpty(AppConfig.Shared.WeatherCity) ? "未配置" : "获取中…";
            return;
        }
        IconHost.Content = CreateIcon(_data.Code);
        TempText.Text = $"{Math.Round(_data.Temp)}°";
        DescText.Text = Describe(_data.Code);
        if (_overlay is { IsOverlayVisible: true } && _shell != null)
        {
            _overlay.SetContent(BuildOverlayContent());
            _overlay.ShowAbove(_shell);
        }
    }

    /// <summary>hover 浮层：位置行 / 当前温度大字 / 今明后三天 / 体感湿度风</summary>
    private FrameworkElement BuildOverlayContent()
    {
        var panel = new StackPanel();
        var white = Brushes.White;
        var dim = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

        if (_data == null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(AppConfig.Shared.WeatherCity)
                    ? "未配置城市——在设置 → 天气里搜索添加"
                    : "获取中…",
                FontSize = 13,
                Foreground = white,
            });
            return panel;
        }

        string updated = _data.Updated.ToString("HH:mm");
        string failNote = _data.FetchFailed ? " · 更新失败，显示缓存" : "";

        // 位置行：城市 · 描述    右侧更新时间（两列 Grid）
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = $"{AppConfig.Shared.WeatherCity} · {Describe(_data.Code)}",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = white,
        });
        var time = new TextBlock
        {
            Text = $"{updated} 更新{failNote}", FontSize = 11, Foreground = dim,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(time, 1);
        head.Children.Add(time);
        panel.Children.Add(head);

        // 三天行
        string[] dayNames = { "今天", "明天", "后天" };
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        for (int i = 0; i < _data.Daily.Count; i++)
        {
            var e = _data.Daily[i];
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{dayNames[i]} · {Describe(e.Code)}",
                FontSize = 12,
                FontWeight = i == 0 ? FontWeights.Medium : FontWeights.Normal,
                Foreground = i == 0 ? white : dim,
            });
            var range = new TextBlock
            {
                Text = $"{Math.Round(e.Min):0}° – {Math.Round(e.Max):0}°",
                FontSize = 12, FontFamily = new FontFamily("Consolas"),
                Foreground = i == 0 ? white : dim,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(range, 1);
            row.Children.Add(range);
            panel.Children.Add(row);
        }

        // 体感详情
        panel.Children.Add(new TextBlock
        {
            Text = $"体感 {Math.Round(_data.Apparent)}° · 湿度 {_data.Humidity}% · 风速 {_data.Wind:0.#} km/h",
            FontSize = 11, Foreground = dim, Margin = new Thickness(0, 8, 0, 0),
        });
        return panel;
    }

    private void ShowHoverOverlay()
    {
        if (_shell == null) return;
        _overlay ??= new OverlayWindow(OverlayDismiss.HoverDismiss);
        _overlay.SetContent(BuildOverlayContent());
        _overlay.ShowAbove(_shell);
    }

    // ===== WMO weather code → 描述 / 图标 =====

    /// <summary>WMO code → 中文描述（与图标分类同 key）</summary>
    internal static string Describe(int code) => code switch
    {
        0 => "晴",
        1 => "少云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        >= 51 and <= 57 => "毛毛雨",
        >= 61 and <= 67 => "雨",
        >= 71 and <= 77 or 85 or 86 => "雪",
        >= 80 and <= 82 => "阵雨",
        >= 95 => "雷阵雨",
        _ => "未知",
    };

    /// <summary>10 类内置图标（18×18 Canvas 组合矢量，无图片资源）。
    /// 配色对齐设计稿：太阳 #FFC24B · 云 #C9D4E0 · 雨 #6FB3FF · 雪 #E8F0FA</summary>
    internal static FrameworkElement CreateIcon(int code)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        return code switch
        {
            0 => Sun(),
            1 => SunCloud(scaleSun: 1.0),
            2 => SunCloud(scaleSun: 0.8),
            3 => Cloud(),
            45 or 48 => Fog(),
            >= 51 and <= 57 => CloudWith(lines: DrizzleLines()),
            >= 61 and <= 67 => CloudWith(lines: RainLines()),
            >= 71 and <= 77 or 85 or 86 => CloudWith(lines: SnowDots()),
            >= 80 and <= 82 => CloudWith(lines: ShowerLines()),
            _ => CloudWith(lines: Bolt()),
        };
    }

    private static FrameworkElement Sun()
    {
        var c = new Canvas { Width = 18, Height = 18 };
        c.Children.Add(new Ellipse
        {
            Width = 7.2, Height = 7.2,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4B)),
            Margin = new Thickness(5.4, 5.4, 0, 0),
        });
        // 8 向光芒（四正 + 四斜）
        var rays = new Path
        {
            Data = Geometry.Parse("M9 0 L9 2 M9 16 L9 18 M0 9 L2 9 M16 9 L18 9 " +
                                   "M2.6 2.6 L4 4 M14 14 L15.4 15.4 M2.6 15.4 L4 14 M14 4 L15.4 2.6"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4B)),
            StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(rays);
        return c;
    }

    /// <summary>标准云形（Material cloud path，24 基准缩到 18 画布）</summary>
    private static FrameworkElement CloudPath(double offsetX = 0, double offsetY = 0, double scale = 0.75,
        Color? color = null, double opacity = 1)
    {
        var p = new Path
        {
            Data = Geometry.Parse("M19.35 10.04A7.49 7.49 0 0 0 12 4C9.11 4 6.6 5.64 5.35 8.04A5.994 5.994 0 0 0 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z"),
            Fill = new SolidColorBrush(color ?? Color.FromRgb(0xC9, 0xD4, 0xE0)),
            Opacity = opacity,
        };
        var c = new Canvas { Width = 18, Height = 18 };
        p.RenderTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(p, offsetX);
        Canvas.SetTop(p, offsetY);
        c.Children.Add(p);
        return c;
    }

    private static FrameworkElement Cloud() => CloudPath(0, 1);

    /// <summary>雾：云上移 + 底部两道横线</summary>
    private static FrameworkElement Fog()
    {
        var c = new Canvas { Width = 18, Height = 18 };
        var cloud = (Canvas)CloudPath(0, -2.5);
        c.Children.Add(cloud);
        var lines = new Path
        {
            Data = Geometry.Parse("M3 13 L15 13 M5 16 L13 16"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xC9, 0xD4, 0xE0)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(lines);
        return c;
    }

    /// <summary>少云/多云：右上小太阳 + 前景云</summary>
    private static FrameworkElement SunCloud(double scaleSun)
    {
        var c = new Canvas { Width = 18, Height = 18 };
        // 小太阳（右上）
        var s = new Canvas { Width = 18, Height = 18, RenderTransform = new ScaleTransform(scaleSun, scaleSun, 12, 4) };
        s.Children.Add(new Ellipse
        {
            Width = 5.4, Height = 5.4, Margin = new Thickness(9.8, 1.2, 0, 0),
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4B)),
        });
        s.Children.Add(new Path
        {
            Data = Geometry.Parse("M12.5 0 L12.5 1 M17 4.5 L18 4.5 M16.4 0.6 L15.7 1.3 M16.4 8.4 L15.7 7.7 M8.6 4.5 L9.6 4.5"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4B)),
            StrokeThickness = 1.1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        c.Children.Add(s);
        // 前景云（压住太阳下半）
        var cloud = (Canvas)CloudPath(0, 2);
        c.Children.Add(cloud);
        return c;
    }

    private static FrameworkElement CloudWith(FrameworkElement lines)
    {
        var c = new Canvas { Width = 18, Height = 18 };
        // 云整体上移给雨线留底部空间
        var cloud = (Canvas)CloudPath(0, -1.5);
        c.Children.Add(cloud);
        Canvas.SetTop(lines, 10.5);
        c.Children.Add(lines);
        return c;
    }

    private static FrameworkElement DrizzleLines() => Lines("M5 0 L5 2 M11 0 L11 2", 1.2);
    private static FrameworkElement RainLines() => Lines("M4.2 0 L3.2 3 M8.2 0 L7.2 3 M12.2 0 L11.2 3", 1.2);

    private static FrameworkElement ShowerLines()
    {
        var c = new Canvas { Width = 18, Height = 7 };
        var p = new Path
        {
            Data = Geometry.Parse("M4.5 0 L3 3.5 M9 0 L7.5 3.5 M13.5 0 L12 3.5"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x6F, 0xB3, 0xFF)),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(p);
        return c;
    }

    private static FrameworkElement SnowDots()
    {
        var c = new Canvas { Width = 18, Height = 6 };
        var fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA));
        foreach (var (x, y) in new[] { (4.2, 0.2), (9.2, 1.4), (13.8, 0.4) })
            c.Children.Add(new Ellipse { Width = 1.8, Height = 1.8, Fill = fill, Margin = new Thickness(x, y, 0, 0) });
        return c;
    }

    private static FrameworkElement Bolt()
    {
        var c = new Canvas { Width = 18, Height = 8 };
        c.Children.Add(new Path
        {
            Data = Geometry.Parse("M9.5 0 L6.5 4.4 L8.8 4.4 L7.2 7.8 L11.5 3.2 L9 3.2 L10.8 0 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4B)),
        });
        return c;
    }

    private static FrameworkElement Lines(string data, double thickness)
    {
        var c = new Canvas { Width = 18, Height = 6 };
        c.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(Color.FromRgb(0x6F, 0xB3, 0xFF)),
            StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        return c;
    }
}
