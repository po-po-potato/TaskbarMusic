using System.Globalization;
using System.Windows.Data;

namespace TaskbarMusic;

/// <summary>
/// RadioButton 绑定到枚举：IsChecked="{Binding LyricMode, Converter={StaticResource EnumToBool}, ConverterParameter=SwapTitleArtist}"
/// 注意：XAML 裸标识符 ConverterParameter 传进来是【字符串】，enum.Equals(string)
/// 恒 false——之前 Convert 没做类型适配，所有枚举单选永远不显示选中态（切换
/// 功能靠 ConvertBack 兜着没暴露；看材质选项无选中态实锤）。
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        if (parameter is string s && value is Enum)
        {
            try { return value.Equals(System.Enum.Parse(value.GetType(), s)); }
            catch { return false; }
        }
        return value.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null) return parameter;
        return Binding.DoNothing;
    }
}

/// <summary>
/// 数字输入框容错转换：全角标点转半角（中文输入法打小数点变"。"／"，"），
/// 解析失败（输入中间态如 "0."、空串）返回 DoNothing 不打扰输入。
/// </summary>
public class LenientDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return d.ToString("0.##", CultureInfo.InvariantCulture);
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Binding.DoNothing;

        // 全角转半角：小数点、负号
        s = s.Replace('．', '.').Replace('。', '.').Replace('，', '.').Replace('－', '-').Trim();

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            return r;
        return Binding.DoNothing; // "0." / "0.2" 输入中间态或垃圾输入：不更新 source，不闪错
    }
}
