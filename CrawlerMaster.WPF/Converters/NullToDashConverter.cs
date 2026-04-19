using System;
using System.Globalization;
using System.Windows.Data;

namespace CrawlerMaster.WPF.Converters
{
    /// <summary>
    /// 空值/空字符串兜底转换器，空值显示「--」
    /// </summary>
    [ValueConversion(typeof(object), typeof(string))]
    public class NullToDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null
                || string.IsNullOrWhiteSpace(value.ToString())
                || value is DateTime time && time == default)
            {
                return "--";
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}