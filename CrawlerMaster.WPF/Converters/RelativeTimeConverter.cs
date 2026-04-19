using System;
using System.Globalization;
using System.Windows.Data;

namespace CrawlerMaster.WPF.Converters
{
    /// <summary>
    /// 时间转相对时间（xx秒前/xx分钟前），提升可读性
    /// </summary>
    [ValueConversion(typeof(DateTime), typeof(string))]
    public class RelativeTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DateTime utcTime) return "--";

            var localTime = utcTime.ToLocalTime();
            var timeSpan = DateTime.Now - localTime;

            return timeSpan.TotalSeconds switch
            {
                < 60 => $"{Math.Floor(timeSpan.TotalSeconds)}秒前",
                < 3600 => $"{Math.Floor(timeSpan.TotalMinutes)}分钟前",
                < 86400 => $"{Math.Floor(timeSpan.TotalHours)}小时前",
                _ => localTime.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}