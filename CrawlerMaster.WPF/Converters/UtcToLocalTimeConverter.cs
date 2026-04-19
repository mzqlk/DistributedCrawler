using System;
using System.Globalization;
using System.Windows.Data;

namespace CrawlerMaster.WPF.Converters
{
    /// <summary>
    /// UTC时间转本地时间转换器（适配项目UTC时间规范）
    /// </summary>
    [ValueConversion(typeof(DateTime), typeof(DateTime))]
    public class UtcToLocalTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime utcTime)
            {
                return utcTime.ToLocalTime();
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime localTime)
            {
                return localTime.ToUniversalTime();
            }
            return value;
        }
    }
}