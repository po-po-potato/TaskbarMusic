using System;
using System.Windows;
using System.Windows.Controls;

namespace TaskbarMusic;

/// <summary>倒数日设置分区 code-behind：列表行代码构建（对齐壳层显示器卡模式），
/// 直改 AppConfig + 即时落盘 + CountdownModule.NotifyItemsChanged 即时生效。</summary>
public partial class CountdownSettingsSection : UserControl
{
    public CountdownSettingsSection()
    {
        InitializeComponent();
        RebuildList();
    }

    private void RebuildList()
    {
        ItemListPanel.Children.Clear();
        var items = AppConfig.Shared.CountdownItems;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new TextBlock
            {
                Text = $"{item.Name} · {item.Date}{(item.Annual ? "（每年）" : "")}",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var del = new Button
            {
                Content = "删除",
                Width = 64,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            del.Click += (_, _) =>
            {
                AppConfig.Shared.CountdownItems.Remove(item);
                AppConfig.Shared.Save();
                MediaService.Trace($"[CD] item removed: {item.Name}");
                RebuildList();
                CountdownModule.NotifyItemsChanged();
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            ItemListPanel.Children.Add(row);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string date = DateBox.Text.Trim();

        if (name.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "名称不能为空", "倒数日",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!DateOnly.TryParse(date, out _))
        {
            MessageBox.Show(Window.GetWindow(this), "日期格式应为 yyyy-MM-dd，如 2026-10-01", "倒数日",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppConfig.Shared.CountdownItems.Add(new CountdownItem
        {
            Name = name,
            Date = date,
            Annual = AnnualCheck.IsChecked == true,
        });
        AppConfig.Shared.Save();
        MediaService.Trace($"[CD] item added: {name} {date} annual={AnnualCheck.IsChecked}");
        NameBox.Clear();
        DateBox.Clear();
        AnnualCheck.IsChecked = false;
        RebuildList();
        CountdownModule.NotifyItemsChanged();
    }
}
