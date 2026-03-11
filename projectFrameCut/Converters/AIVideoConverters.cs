using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace projectFrameCut.Converters
{
    /// <summary>
    /// 将整数值转换为 Picker 的索引
    /// </summary>
    public class IntToIndexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                // 将时长（秒）映射到选择器索引
                return intValue switch
                {
                    3 => 0,
                    5 => 1,
                    10 => 2, 
                    15 => 3,
                    20 => 4,
                    _ => 1 // 默认5秒
                };
            }
            return 1; // 默认索引
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                // 将选择器索引映射回时长（秒）
                return index switch
                {
                    0 => 3,
                    1 => 5,
                    2 => 10,
                    3 => 15,
                    4 => 20,
                    _ => 5 // 默认5秒
                };
            }
            return 5; // 默认值
        }
    }

    /// <summary>
    /// 比较两个值是否相等的转换器
    /// </summary>
    public class EqualConverter : IValueConverter
    {
        public object? ComparisonValue { get; set; } = "";

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null)
                return false;

            return value.ToString() == ComparisonValue?.ToString();
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}