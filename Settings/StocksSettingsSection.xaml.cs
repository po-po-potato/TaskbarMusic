using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskbarMusic;

/// <summary>
/// 财经设置分区（A4）：自选股搜索添加 + 删除。改动即时生效 + 落盘。
///
/// 添加两通道：
/// - 输入本身是合法代码格式（sh/sz+6 位、hk+5 位）→ 回车/搜索直接添加
/// - 否则走腾讯 smartbox 搜索（https://smartbox.gtimg.cn/s3/?v=2&amp;q=..&amp;t=all，
///   GBK + \uXXXX 转义名称；实测格式 v_hint="市场~代码~名称~拼音~类型^..."），
///   结果列表点选添加。列表顺序 = ticker 轮播顺序；重复代码不重复添加。
/// </summary>
public partial class StocksSettingsSection : UserControl
{
    /// <summary>smartbox 搜索结果（code = 市场+代码，如 sh600519）</summary>
    private sealed record StockResult(string Code, string Name, string Type)
    {
        public override string ToString() => $"{Name}  {Code} · {Type}";
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    private List<StockResult> _results = new();

    static StocksSettingsSection() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public StocksSettingsSection(StocksModule module)
    {
        InitializeComponent();
        _ = module; // 刷新走 StocksModule.NotifyCodesChanged()（static），参数对齐构造契约
        ReloadList();
    }

    private void ReloadList()
    {
        ItemListPanel.Children.Clear();
        var codes = AppConfig.Shared.StockCodes;
        EmptyHint.Visibility = codes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var code in codes)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = code.ToUpperInvariant(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White,
            };
            row.Children.Add(label);

            string c = code;
            var del = new Button
            {
                Content = "删除", Width = 60, VerticalAlignment = VerticalAlignment.Center,
            };
            del.Click += (_, _) => RemoveCode(c);
            Grid.SetColumn(del, 1);
            row.Children.Add(del);

            ItemListPanel.Children.Add(row);
        }
    }

    // ===== 添加（直加代码 or smartbox 搜索）=====

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchOrAddAsync();

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchOrAddAsync();
    }

    private async System.Threading.Tasks.Task SearchOrAddAsync()
    {
        string q = CodeBox.Text.Trim();
        if (q.Length == 0) return;

        // 通道 1：输入本身是合法代码 → 直加（老用法保留）
        if (IsDirectCode(q, out string directCode))
        {
            if (AddCode(directCode)) CodeBox.Text = "";
            return;
        }

        // 通道 2：smartbox 搜索（名称/拼音/代码模糊匹配）
        SearchButton.IsEnabled = false;
        StatusText.Text = "搜索中…";
        ResultList.Visibility = Visibility.Collapsed;
        try
        {
            string url = $"https://smartbox.gtimg.cn/s3/?v=2&q={Uri.EscapeDataString(q)}&t=all";
            byte[] bytes = await Http.GetByteArrayAsync(url);
            string text = Encoding.GetEncoding("GBK").GetString(bytes);

            _results = ParseSmartbox(text).Take(10).ToList();
            ResultList.Items.Clear();
            foreach (var r in _results) ResultList.Items.Add(r);
            if (_results.Count == 0)
            {
                StatusText.Text = "没有匹配结果，换个关键词（名称 / 拼音缩写 / 代码）";
            }
            else
            {
                ResultList.Visibility = Visibility.Visible;
                StatusText.Text = $"匹配 {_results.Count} 个，点击添加";
            }
            MediaService.Trace($"[STK] search '{q}' -> {_results.Count} results");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    /// <summary>输入是否为可直接添加的代码格式（sh600519 / sz300750 / hk00700）</summary>
    private static bool IsDirectCode(string input, out string code)
    {
        code = input.ToLowerInvariant();
        return (code.StartsWith("sh") || code.StartsWith("sz")) && code.Length == 8
            || code.StartsWith("hk") && code.Length == 7;
    }

    /// <summary>解析 smartbox 响应：v_hint="市场~代码~\u名称~拼音~类型^..."
    /// 名称是 \uXXXX 转义（JS 风格），手写反转义；只收 sh/sz/hk 市场且代码纯数字的结果。
    /// 类型过滤：仅排除 QZ（港股权证/涡轮——"宁德"会混进 8 个衍生品）；
    /// ETF/基金（KJ）、指数等一律收录，类型标注在结果行里供用户自行判断</summary>
    private static IEnumerable<StockResult> ParseSmartbox(string text)
    {
        int q1 = text.IndexOf('"');
        int q2 = text.LastIndexOf('"');
        if (q1 < 0 || q2 <= q1) yield break;
        string inner = text.Substring(q1 + 1, q2 - q1 - 1);
        if (inner.Length == 0) yield break;

        foreach (var item in inner.Split('^'))
        {
            var f = item.Split('~');
            if (f.Length < 5) continue;
            string market = f[0].ToLowerInvariant();
            string codeNum = f[1];
            if (market is not ("sh" or "sz" or "hk")) continue;
            if (codeNum.Length is not (5 or 6) || !codeNum.All(char.IsDigit)) continue;
            if (f[4].Equals("QZ", StringComparison.OrdinalIgnoreCase)) continue; // 权证 only 排除
            yield return new StockResult(market + codeNum, UnescapeUnicode(f[2]), f[4]);
        }
    }

    /// <summary>\uXXXX → 字符（smartbox 名称转义，JS 风格，C# 无现成解码）</summary>
    private static string UnescapeUnicode(string s) =>
        Regex.Replace(s, @"\\u([0-9a-fA-F]{4})",
            m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not StockResult r) return;
        AddCode(r.Code);
        ResultList.Visibility = Visibility.Collapsed;
        ResultList.SelectedItem = null;
        StatusText.Text = $"已添加：{r.Name}（{r.Code}）";
        CodeBox.Text = "";
    }

    /// <summary>添加代码（去重）；成功返回 true 并即时生效</summary>
    private bool AddCode(string code)
    {
        var codes = AppConfig.Shared.StockCodes;
        if (codes.Contains(code))
        {
            StatusText.Text = $"{code.ToUpperInvariant()} 已在列表中";
            return false;
        }
        codes.Add(code);
        AppConfig.Shared.Save();
        ReloadList();
        StocksModule.NotifyCodesChanged();
        MediaService.Trace($"[STK] add {code} -> {codes.Count} stocks");
        return true;
    }

    private void RemoveCode(string code)
    {
        var codes = AppConfig.Shared.StockCodes;
        codes.Remove(code);
        AppConfig.Shared.Save();
        ReloadList();
        StocksModule.NotifyCodesChanged();
        MediaService.Trace($"[STK] remove {code} -> {codes.Count} stocks");
    }
}
