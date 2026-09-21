using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarMusic;

/// <summary>
/// 天气设置分区（A3）：城市搜索（Open-Meteo geocoding）。
/// 搜索 → 结果列表（最多 5 个，显示名称 + 行政区 + 国家）→ 点选保存
/// City/Lat/Lon → WeatherModule.RefreshNow() 立即拉取新城市天气。
/// </summary>
public partial class WeatherSettingsSection : UserControl
{
    private sealed record GeoResult(string Display, double Lat, double Lon);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private List<GeoResult> _results = new();

    public WeatherSettingsSection(WeatherModule module)
    {
        InitializeComponent();
        _ = module; // 刷新走 WeatherModule.RefreshNow()（static），参数对齐构造契约
        RefreshCurrentCity();
    }

    private void RefreshCurrentCity()
    {
        var cfg = AppConfig.Shared;
        CurrentCityText.Text = string.IsNullOrEmpty(cfg.WeatherCity)
            ? "当前：未配置（条上显示占位）"
            : $"当前：{cfg.WeatherCity}（{cfg.WeatherLat:0.##}, {cfg.WeatherLon:0.##}）";
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void CityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        string q = CityBox.Text.Trim();
        if (q.Length == 0) return;
        SearchButton.IsEnabled = false;
        StatusText.Text = "搜索中…";
        ResultList.Visibility = Visibility.Collapsed;
        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=5&language=zh&format=json";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            _results.Clear();
            ResultList.Items.Clear();
            if (doc.RootElement.TryGetProperty("results", out var arr))
            {
                foreach (var r in arr.EnumerateArray())
                {
                    string name = r.GetProperty("name").GetString() ?? "";
                    string admin = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                    string country = r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                    double lat = r.GetProperty("latitude").GetDouble();
                    double lon = r.GetProperty("longitude").GetDouble();
                    var g = new GeoResult($"{name} · {admin} · {country}".Trim('·', ' '), lat, lon);
                    _results.Add(g);
                    ResultList.Items.Add(g);
                }
            }
            if (_results.Count == 0)
            {
                StatusText.Text = "没有找到匹配的城市，换个关键词试试";
            }
            else
            {
                ResultList.Visibility = Visibility.Visible;
                StatusText.Text = $"找到 {_results.Count} 个结果，点击选择";
            }
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

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not GeoResult g) return;
        var cfg = AppConfig.Shared;
        cfg.WeatherCity = g.Display.Split('·')[0].Trim();
        cfg.WeatherLat = g.Lat;
        cfg.WeatherLon = g.Lon;
        cfg.Save();
        MediaService.Trace($"[WX] city -> {cfg.WeatherCity} ({g.Lat:0.##},{g.Lon:0.##})");
        RefreshCurrentCity();
        ResultList.Visibility = Visibility.Collapsed;
        ResultList.SelectedItem = null;
        StatusText.Text = $"已选择：{g.Display}，天气刷新中…";
        WeatherModule.RefreshNow();
    }
}
