using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskbarMusic;

/// <summary>
/// A4 财经模块（设计稿定稿 2026-09-21：单股静态轮播）。
///
/// 双节奏解耦：
/// - 行情刷新 = 交易时段 5s 轮询（qt.gtimg.cn 拉全部自选股，GBK 编码；
///   名称[1]/现价[3]/昨收[4]，涨跌幅自算——不依赖接口字段位次）
/// - 展示轮换 = 8s 切下一只（120ms 纯淡入，与 E6 同语言；展示期 ≥1 次行情刷新）
///
/// 分时 sparkline：ifzq.gtimg.cn minute/query（分钟级价格序列），当前展示股
/// 每 60s 全量拉一次 + 切换展示股时立即拉。Canvas 90×26 Polyline + 14% 填充，
/// 着色随涨跌（红涨绿跌，与涨跌幅文字同色）。
///
/// 价格变动闪色：现价变化时 300ms 透明度闪烁。
/// hover：轮换暂停 + 自选股列表浮层（名称代码 + 现价 + 涨跌幅着色）。
/// 非交易时段（周一~五 9:00-16:00 之外）：不轮询（数据静止），轮换保留
/// （可看全部自选股的收盘缓存），浮层标注「已收盘」。
/// A7 多显示器：数据 static 单一真源，StateChanged 广播刷新各条 UI。
/// </summary>
public partial class StocksModule : UserControl, ITaskbarModule
{
    public string Id => "stocks";
    public string DisplayName => "财经";
    public FrameworkElement View => this;

    private StocksSettingsSection? _settingsSection;
    public FrameworkElement? SettingsSection => _settingsSection ??= new StocksSettingsSection(this);

    private TaskbarShell? _shell;
    private OverlayWindow? _overlay;

    // ===== static 状态机（A7 跨条共享的单一真源）=====

    private sealed record Quote(string Name, double Price, double PrevClose)
    {
        public double ChangePct => PrevClose != 0 ? (Price - PrevClose) / PrevClose * 100 : 0;
    }

    private static readonly Dictionary<string, Quote> Quotes = new();

    /// <summary>当前展示股的分时价格序列（_sparkCode 对应；切换展示股时重拉）</summary>
    private static List<double> _sparkPrices = new();
    private static string _sparkCode = "";

    private static int _showIndex;
    private static DispatcherTimer? _quoteTimer;   // 5s 行情
    private static DispatcherTimer? _rotateTimer;  // 8s 轮换
    private static DispatcherTimer? _sparkTimer;   // 60s 分时
    private static int _attachCount;
    private static bool _fetchingQuotes;
    private static event Action? StateChanged;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    static StocksModule() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public StocksModule(AppConfig config)
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
        EnsureTimers();
        RefreshDisplay(); // 首帧立即渲染（缓存或占位）
        MediaService.Trace($"[STK] attached ({_attachCount} bar) codes={AppConfig.Shared.StockCodes.Count}");
    }

    public void OnDetach()
    {
        _overlay?.HideOverlay();
        StateChanged -= OnStateChanged;
        _attachCount = Math.Max(0, _attachCount - 1);
        if (_attachCount == 0)
        {
            _quoteTimer?.Stop();
            _rotateTimer?.Stop();
            _sparkTimer?.Stop();
            MediaService.Trace("[STK] detached (last bar) timers stopped");
        }
    }

    public void OnHoverChanged(bool hovering)
    {
        // hover 暂停轮换（设计稿 A4：hover 轮播暂停 + 浮层）；离开恢复
        if (hovering)
        {
            _rotateTimer?.Stop();
            ShowHoverOverlay();
        }
        else
        {
            _rotateTimer?.Start();
            _overlay?.GraceHide();
        }
    }

    private void OnStateChanged() => RefreshDisplay();

    private static void EnsureTimers()
    {
        if (_quoteTimer != null)
        {
            // 三个 timer 同批创建（见下），非空等价；?. 消解可空警告
            _quoteTimer.Start();
            _rotateTimer?.Start();
            _sparkTimer?.Start();
            return;
        }
        _quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _quoteTimer.Tick += (_, _) => _ = FetchQuotesAsync();
        _quoteTimer.Start();

        _rotateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _rotateTimer.Tick += (_, _) =>
        {
            var codes = AppConfig.Shared.StockCodes;
            if (codes.Count == 0) return;
            _showIndex = (_showIndex + 1) % codes.Count;
            MediaService.Trace($"[STK] rotate -> {codes[_showIndex]}");
            StateChanged?.Invoke();
            _ = FetchSparkAsync(codes[_showIndex]); // 新展示股立即拉分时
        };
        _rotateTimer.Start();

        _sparkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _sparkTimer.Tick += (_, _) =>
        {
            var codes = AppConfig.Shared.StockCodes;
            if (codes.Count == 0) return;
            _ = FetchSparkAsync(codes[Math.Min(_showIndex, codes.Count - 1)]);
        };
        _sparkTimer.Start();

        _ = FetchQuotesAsync();
    }

    /// <summary>设置分区改自选股后调用：重置轮换位 + 立即拉行情</summary>
    internal static void NotifyCodesChanged()
    {
        _showIndex = 0;
        _sparkPrices = new List<double>();
        _sparkCode = "";
        _ = FetchQuotesAsync();
    }

    // ===== 交易时段（简化：覆盖 A 股 + 港股主时段）=====

    private static bool IsTradingHours()
    {
        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = now.TimeOfDay;
        return t >= new TimeSpan(9, 0, 0) && t < new TimeSpan(16, 0, 0);
    }

    // ===== 行情拉取（qt.gtimg.cn，GBK）=====

    private static async System.Threading.Tasks.Task FetchQuotesAsync()
    {
        var codes = AppConfig.Shared.StockCodes;
        if (codes.Count == 0) return;
        if (_fetchingQuotes) return;
        if (!IsTradingHours() && Quotes.Count > 0) return; // 非交易时段：已有数据就不空转
        _fetchingQuotes = true;
        try
        {
            string url = "https://qt.gtimg.cn/q=" + string.Join(",", codes);
            byte[] bytes = await Http.GetByteArrayAsync(url);
            string text = Encoding.GetEncoding("GBK").GetString(bytes);

            foreach (var line in text.Split('\n'))
            {
                int q1 = line.IndexOf('"');
                if (q1 < 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 2) continue;
                string code = line.Substring(2, eq - 2); // "v_hk00700=" → "hk00700"
                var f = line.Substring(q1 + 1, line.LastIndexOf('"') - q1 - 1).Split('~');
                if (f.Length < 5) continue;
                if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var price)) continue;
                if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var prev)) continue;
                Quotes[code] = new Quote(f[1], price, prev);
            }
            MediaService.Trace($"[STK] quotes fetched {Quotes.Count}/{codes.Count}");
            // 首次成功后若分时还没有，补当前展示股
            if (_sparkPrices.Count == 0 && Quotes.Count > 0)
                _ = FetchSparkAsync(codes[Math.Min(_showIndex, codes.Count - 1)]);
        }
        catch (Exception ex)
        {
            MediaService.Trace($"[STK] quotes error {ex.Message}");
        }
        finally
        {
            _fetchingQuotes = false;
            StateChanged?.Invoke();
        }
    }

    // ===== 分时拉取（ifzq.gtimg.cn minute/query）=====

    private static async System.Threading.Tasks.Task FetchSparkAsync(string code)
    {
        if (string.IsNullOrEmpty(code) || _sparkCode == code && _sparkPrices.Count > 0 && !IsTradingHours())
            return; // 非交易时段已有该股数据不重拉
        try
        {
            string url = $"https://ifzq.gtimg.cn/appstock/app/minute/query?code={code}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var arr = doc.RootElement.GetProperty("data").GetProperty(code)
                        .GetProperty("data").GetProperty("data");
            var prices = new List<double>();
            foreach (var item in arr.EnumerateArray())
            {
                var parts = item.GetString()?.Split(' ');
                if (parts != null && parts.Length >= 2
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    prices.Add(p);
            }
            if (prices.Count >= 2)
            {
                _sparkPrices = prices;
                _sparkCode = code;
                MediaService.Trace($"[STK] spark {code} pts={prices.Count}");
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            MediaService.Trace($"[STK] spark error {code} {ex.Message}");
        }
    }

    // ===== 显示 =====

    private static readonly SolidColorBrush Up = new(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly SolidColorBrush Down = new(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly SolidColorBrush Flat = new(Color.FromRgb(0xC5, 0xC5, 0xC5));

    private string? _lastPriceText;
    private string? _lastShownCode;

    private void RefreshDisplay()
    {
        var codes = AppConfig.Shared.StockCodes;
        if (codes.Count == 0 || Quotes.Count == 0)
        {
            NameText.Text = codes.Count == 0 ? "未添加自选股" : "获取中…";
            SparkCanvas.Children.Clear();
            PriceText.Text = "--";
            ChangeText.Text = "";
            return;
        }

        int idx = Math.Min(_showIndex, codes.Count - 1);
        string code = codes[idx];
        if (!Quotes.TryGetValue(code, out var q)) return;

        // 换股淡入（rotate 或增删自选导致展示对象变化时）
        if (_lastShownCode != code)
        {
            FadeIn();
            _lastShownCode = code;
        }

        NameText.Text = q.Name;
        string priceText = q.Price.ToString("0.00", CultureInfo.InvariantCulture);
        bool priceChanged = _lastPriceText != null && _lastPriceText != priceText;
        PriceText.Text = priceText;
        _lastPriceText = priceText;

        double pct = q.ChangePct;
        ChangeText.Text = $"{pct:+0.00;-0.00;0.00}%";
        ChangeText.Foreground = pct > 0.001 ? Up : pct < -0.001 ? Down : Flat;

        DrawSpark(code == _sparkCode ? _sparkPrices : null, pct);

        // 价格变动闪色（300ms 透明度闪烁）
        if (priceChanged)
            PriceText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(150)) { AutoReverse = true });

        if (_overlay is { IsOverlayVisible: true } && _shell != null)
        {
            _overlay.SetContent(BuildOverlayContent());
            _overlay.ShowAbove(_shell);
        }
    }

    /// <summary>换股淡入（120ms 纯淡入，与 E6 切换同语言）</summary>
    private void FadeIn() =>
        RootPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    /// <summary>分时 sparkline：90×26 Polyline + 14% 填充，着色随涨跌。
    /// prices 为空（该股分时未就绪）时清空画布。</summary>
    private void DrawSpark(IReadOnlyList<double>? prices, double pct)
    {
        SparkCanvas.Children.Clear();
        if (prices == null || prices.Count < 2) return;

        var color = pct > 0.001 ? Up : pct < -0.001 ? Down : Flat;
        double min = prices.Min(), max = prices.Max();
        var pts = new PointCollection();
        for (int i = 0; i < prices.Count; i++)
        {
            double x = (double)i / (prices.Count - 1) * 88 + 1;
            double y = max == min ? 13 : 24 - (prices[i] - min) / (max - min) * 22;
            pts.Add(new Point(x, y));
        }

        var fill = new Polygon { Fill = color, Opacity = 0.14 };
        foreach (var p in pts) fill.Points.Add(p);
        fill.Points.Add(new Point(pts[^1].X, 26));
        fill.Points.Add(new Point(pts[0].X, 26));
        SparkCanvas.Children.Add(fill);

        SparkCanvas.Children.Add(new Polyline { Points = pts, Stroke = color, StrokeThickness = 1.2 });
    }

    // ===== hover 浮层 =====

    private void ShowHoverOverlay()
    {
        if (_shell == null) return;
        _overlay ??= new OverlayWindow(OverlayDismiss.HoverDismiss);
        _overlay.SetContent(BuildOverlayContent());
        _overlay.ShowAbove(_shell);
    }

    private FrameworkElement BuildOverlayContent()
    {
        var panel = new StackPanel();
        var codes = AppConfig.Shared.StockCodes;
        string state = IsTradingHours() ? "交易中 · 5s 刷新" : "已收盘 · 显示缓存";
        panel.Children.Add(new TextBlock
        {
            Text = $"自选股 · {codes.Count} 只 · {state}",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
        });

        var dim = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        foreach (var code in codes)
        {
            if (!Quotes.TryGetValue(code, out var q)) continue;
            double pct = q.ChangePct;
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{q.Name} {code.ToUpperInvariant()}",
                FontSize = 12, Foreground = dim,
            });
            var right = new TextBlock
            {
                Text = $"{q.Price:0.00}  {pct:+0.00;-0.00;0.00}%",
                FontSize = 12, FontFamily = new FontFamily("Consolas"),
                Foreground = pct > 0.001 ? Up : pct < -0.001 ? Down : Flat,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            panel.Children.Add(row);
        }
        return panel;
    }
}
