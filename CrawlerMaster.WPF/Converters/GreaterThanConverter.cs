using System;
using System.Globalization;
using System.Windows.Data;

namespace CrawlerMaster.WPF.Converters
{
    /// <summary>
    /// 大于比较转换器：判断绑定值是否大于参数值
    /// </summary>
    public class GreaterThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            try
            {
                double valueNum = System.Convert.ToDouble(value, culture);
                double paramNum = System.Convert.ToDouble(parameter, culture);
                return valueNum > paramNum;
            }
            catch
            {
                return false;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}